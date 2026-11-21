//! Minimal OpenRGB SDK client (TCP, default port 6742). Speaks protocol <= 5; the server
//! downgrades automatically when we announce our max version.

use anyhow::{bail, Context, Result};
use async_trait::async_trait;
use lightsync_core::{Color, Device, DeviceKind, LightingProvider};
use std::time::Duration;
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::TcpStream;
use tokio::time::timeout;

pub const DEFAULT_ADDR: &str = "127.0.0.1:6742";
const CLIENT_MAX_PROTOCOL: u32 = 5;

const PKT_REQUEST_CONTROLLER_COUNT: u32 = 0;
const PKT_REQUEST_CONTROLLER_DATA: u32 = 1;
const PKT_REQUEST_PROTOCOL_VERSION: u32 = 40;
const PKT_SET_CLIENT_NAME: u32 = 50;
const PKT_RESIZE_ZONE: u32 = 1000;
const PKT_UPDATE_LEDS: u32 = 1050;
const PKT_UPDATE_MODE: u32 = 1101;

/// Parsed subset of an OpenRGB controller description.
#[derive(Debug, Clone, PartialEq)]
pub struct Controller {
    pub index: u32,
    pub device_type: i32,
    pub name: String,
    pub vendor: String,
    pub led_count: usize,
    pub modes: Vec<Mode>,
    pub zones: Vec<Zone>,
}

#[derive(Debug, Clone, PartialEq)]
pub struct Zone {
    pub name: String,
    pub leds_min: u32,
    pub leds_max: u32,
    pub leds_count: u32,
}

/// One device mode, kept as the exact serialized block the server sent so it can be sent back.
#[derive(Debug, Clone, PartialEq)]
pub struct Mode {
    pub index: u32,
    pub name: String,
    pub raw: Vec<u8>,
    /// Offset inside `raw` of the `num_colors` field (colors follow it to the end of the block).
    pub colors_off: usize,
}

impl Mode {
    /// Block with its color list replaced by a single color (used for hardware "Static" modes).
    pub fn with_single_color(&self, c: Color) -> Vec<u8> {
        let mut v = self.raw[..self.colors_off].to_vec();
        v.extend_from_slice(&1u16.to_le_bytes());
        v.extend_from_slice(&[c.r, c.g, c.b, 0]);
        v
    }
}

impl Controller {
    fn find_mode(&self, names: &[&str]) -> Option<&Mode> {
        names.iter().find_map(|w| self.modes.iter().find(|m| m.name.eq_ignore_ascii_case(w)))
    }

    /// Mode used for software-driven colors.
    pub fn direct_mode(&self) -> Option<&Mode> {
        self.find_mode(&["direct", "custom", "software", "static"]).or_else(|| self.modes.first())
    }

    /// Hardware static-color mode, if the device has one.
    pub fn static_mode(&self) -> Option<&Mode> {
        self.find_mode(&["static"])
    }
}

pub struct OpenRgbProvider {
    addr: String,
    stream: Option<TcpStream>,
    protocol: u32,
    controllers: Vec<Controller>,
    /// Vendors handled by another provider (none by default).
    skip_vendors: Vec<String>,
    /// Prefer the device's hardware Static mode over Direct (fixes devices that ignore Direct).
    prefer_static: bool,
}

impl OpenRgbProvider {
    pub fn new(addr: impl Into<String>) -> Self {
        Self { addr: addr.into(), stream: None, protocol: 0, controllers: vec![], skip_vendors: vec![], prefer_static: false }
    }

    pub fn prefer_static(mut self, yes: bool) -> Self {
        self.prefer_static = yes;
        self
    }

    pub fn skip_vendor(mut self, vendor: &str) -> Self {
        self.skip_vendors.push(vendor.to_lowercase());
        self
    }

    pub fn protocol(&self) -> u32 {
        self.protocol
    }

    pub fn controllers(&self) -> &[Controller] {
        &self.controllers
    }

    fn owned(&self, c: &Controller) -> bool {
        let v = c.vendor.to_lowercase();
        let n = c.name.to_lowercase();
        !self.skip_vendors.iter().any(|s| v.contains(s) || n.contains(s))
    }

    async fn send_mode(&mut self, dev: u32, idx: u32, raw: &[u8]) -> Result<()> {
        let mut m = Vec::with_capacity(8 + raw.len());
        m.extend_from_slice(&((8 + raw.len()) as u32).to_le_bytes());
        m.extend_from_slice(&idx.to_le_bytes());
        m.extend_from_slice(raw);
        self.send(dev, PKT_UPDATE_MODE, &m).await
    }

    async fn send(&mut self, dev: u32, ty: u32, payload: &[u8]) -> Result<()> {
        let s = self.stream.as_mut().context("not connected")?;
        let mut buf = Vec::with_capacity(16 + payload.len());
        buf.extend_from_slice(b"ORGB");
        buf.extend_from_slice(&dev.to_le_bytes());
        buf.extend_from_slice(&ty.to_le_bytes());
        buf.extend_from_slice(&(payload.len() as u32).to_le_bytes());
        buf.extend_from_slice(payload);
        s.write_all(&buf).await?;
        Ok(())
    }

    /// Read packets until one of type `want` arrives (skips async notifications such as DEVICE_LIST_UPDATED).
    async fn recv(&mut self, want: u32) -> Result<Vec<u8>> {
        let s = self.stream.as_mut().context("not connected")?;
        loop {
            let mut h = [0u8; 16];
            timeout(Duration::from_secs(5), s.read_exact(&mut h)).await.context("OpenRGB timeout")??;
            if &h[0..4] != b"ORGB" {
                bail!("bad OpenRGB packet magic");
            }
            let ty = u32::from_le_bytes(h[8..12].try_into().unwrap());
            let size = u32::from_le_bytes(h[12..16].try_into().unwrap()) as usize;
            if size > 16 * 1024 * 1024 {
                bail!("absurd OpenRGB packet size {size}");
            }
            let mut body = vec![0u8; size];
            s.read_exact(&mut body).await?;
            if ty == want {
                return Ok(body);
            }
        }
    }

    /// Resize one zone (addressable-LED headers report 0 LEDs until sized).
    pub async fn resize_zone(&mut self, dev: u32, zone: u32, size: u32) -> Result<()> {
        let mut p = Vec::with_capacity(8);
        p.extend_from_slice(&zone.to_le_bytes());
        p.extend_from_slice(&size.to_le_bytes());
        self.send(dev, PKT_RESIZE_ZONE, &p).await
    }

    /// Give every empty, resizable zone (ARGB headers) its maximum LED count. Returns what changed.
    /// Extra LEDs beyond the real strip/fan chain are ignored by the hardware.
    pub async fn expand_empty_zones(&mut self, cap: u32) -> Result<Vec<String>> {
        let mut done = vec![];
        let todo: Vec<(u32, String, u32, u32)> = self
            .controllers
            .iter()
            .flat_map(|c| {
                c.zones.iter().enumerate().filter(|(_, z)| z.leds_count == 0 && z.leds_max > 0).map(|(i, z)| {
                    (c.index, format!("{} / {}", c.name, z.name), i as u32, z.leds_max.min(cap))
                })
            })
            .collect();
        for (dev, label, zone, size) in todo {
            self.resize_zone(dev, zone, size).await?;
            done.push(format!("{label} -> {size} LEDs"));
        }
        Ok(done)
    }

    pub async fn connect(&mut self) -> Result<()> {
        let s = timeout(Duration::from_secs(2), TcpStream::connect(&self.addr))
            .await
            .context("connecting to OpenRGB timed out")?
            .with_context(|| format!("OpenRGB server not reachable at {}", self.addr))?;
        s.set_nodelay(true).ok();
        self.stream = Some(s);
        self.send(0, PKT_SET_CLIENT_NAME, b"LightSync\0").await?;
        self.send(0, PKT_REQUEST_PROTOCOL_VERSION, &CLIENT_MAX_PROTOCOL.to_le_bytes()).await?;
        let b = self.recv(PKT_REQUEST_PROTOCOL_VERSION).await?;
        let server = u32::from_le_bytes(b.get(0..4).context("short version reply")?.try_into().unwrap());
        self.protocol = server.min(CLIENT_MAX_PROTOCOL);
        Ok(())
    }
}

#[async_trait]
impl LightingProvider for OpenRgbProvider {
    fn name(&self) -> &'static str {
        "openrgb"
    }

    async fn discover(&mut self) -> Result<Vec<Device>> {
        self.connect().await?;
        self.send(0, PKT_REQUEST_CONTROLLER_COUNT, &[]).await?;
        let b = self.recv(PKT_REQUEST_CONTROLLER_COUNT).await?;
        let count = u32::from_le_bytes(b.get(0..4).context("short count reply")?.try_into().unwrap());
        let mut list = vec![];
        for i in 0..count {
            self.send(i, PKT_REQUEST_CONTROLLER_DATA, &self.protocol.to_le_bytes()).await?;
            let b = self.recv(PKT_REQUEST_CONTROLLER_DATA).await?;
            match parse_controller(i, &b, self.protocol) {
                Ok(c) => list.push(c),
                Err(e) => eprintln!("[openrgb] device {i}: cannot parse controller data (protocol {}): {e}", self.protocol),
            }
        }
        self.controllers = list;
        Ok(self
            .controllers
            .iter()
            .filter(|c| self.owned(c))
            .map(|c| Device {
                id: format!("openrgb:{}", c.index),
                name: c.name.clone(),
                kind: kind_from_openrgb(c.device_type),
                provider: "openrgb".into(),
                led_count: c.led_count,
            })
            .collect())
    }

    async fn set_all(&mut self, color: Color) -> Result<()> {
        let todo: Vec<Controller> = self.controllers.iter().filter(|c| self.owned(c)).cloned().collect();
        for c in todo {
            if c.led_count == 0 {
                continue;
            }
            // Activate a mode via UPDATEMODE; hardware Static carries the color inside the mode block.
            let static_mode = if self.prefer_static { c.static_mode() } else { None };
            if let Some(m) = static_mode {
                let raw = m.with_single_color(color);
                self.send_mode(c.index, m.index, &raw).await?;
                continue;
            }
            if let Some(m) = c.direct_mode() {
                self.send_mode(c.index, m.index, &m.raw).await?;
            }
            let mut p = Vec::with_capacity(6 + c.led_count * 4);
            p.extend_from_slice(&((6 + c.led_count * 4) as u32).to_le_bytes());
            p.extend_from_slice(&(c.led_count as u16).to_le_bytes());
            for _ in 0..c.led_count {
                p.extend_from_slice(&[color.r, color.g, color.b, 0]);
            }
            self.send(c.index, PKT_UPDATE_LEDS, &p).await?;
        }
        Ok(())
    }
}

fn kind_from_openrgb(t: i32) -> DeviceKind {
    match t {
        0 => DeviceKind::Motherboard,
        1 => DeviceKind::Ram,
        3 | 15 => DeviceKind::Fan,
        5 | 16 => DeviceKind::Keyboard,
        6 | 7 => DeviceKind::Mouse,
        12 => DeviceKind::Speaker,
        _ => DeviceKind::Other,
    }
}

struct Reader<'a> {
    b: &'a [u8],
    p: usize,
}

impl<'a> Reader<'a> {
    fn take(&mut self, n: usize) -> Result<&'a [u8]> {
        let end = self.p.checked_add(n).filter(|&e| e <= self.b.len()).context("controller data truncated")?;
        let s = &self.b[self.p..end];
        self.p = end;
        Ok(s)
    }
    fn u16(&mut self) -> Result<u16> {
        Ok(u16::from_le_bytes(self.take(2)?.try_into().unwrap()))
    }
    fn u32(&mut self) -> Result<u32> {
        Ok(u32::from_le_bytes(self.take(4)?.try_into().unwrap()))
    }
    fn i32(&mut self) -> Result<i32> {
        Ok(self.u32()? as i32)
    }
    fn string(&mut self) -> Result<String> {
        let n = self.u16()? as usize;
        let raw = self.take(n)?;
        Ok(String::from_utf8_lossy(raw).trim_end_matches('\0').to_string())
    }
}

/// Parse an OpenRGB controller data block (protocol <= 5).
pub fn parse_controller(index: u32, data: &[u8], protocol: u32) -> Result<Controller> {
    let mut r = Reader { b: data, p: 0 };
    let _size = r.u32()?;
    let device_type = r.i32()?;
    let name = r.string()?;
    let vendor = if protocol >= 1 { r.string()? } else { String::new() };
    r.string()?; // description
    r.string()?; // version
    r.string()?; // serial
    r.string()?; // location
    let num_modes = r.u16()?;
    r.i32()?; // active mode
    let mut modes = vec![];
    for mode_idx in 0..num_modes {
        let mode_start = r.p;
        let mode_name = r.string()?; // mode name
        r.take(4 * 4)?; // value, flags, speed_min, speed_max
        if protocol >= 3 {
            r.take(8)?; // brightness min/max
        }
        r.take(4 * 2)?; // colors_min, colors_max
        r.take(4)?; // speed
        if protocol >= 3 {
            r.take(4)?; // brightness
        }
        r.take(4 * 2)?; // direction, color_mode
        let colors_off = r.p - mode_start;
        let n = r.u16()? as usize;
        r.take(n * 4)?;
        modes.push(Mode { index: mode_idx as u32, name: mode_name, raw: r.b[mode_start..r.p].to_vec(), colors_off });
    }
    let num_zones = r.u16()?;
    let mut zones = vec![];
    for _ in 0..num_zones {
        let zone_name = r.string()?; // zone name
        let z = r.take(4 * 4)?; // type, leds_min, leds_max, leds_count
        let u = |i: usize| u32::from_le_bytes(z[i..i + 4].try_into().unwrap());
        zones.push(Zone { name: zone_name, leds_min: u(4), leds_max: u(8), leds_count: u(12) });
        let matrix_len = r.u16()? as usize;
        r.take(matrix_len)?;
        if protocol >= 4 {
            let segs = r.u16()?;
            for _ in 0..segs {
                r.string()?;
                r.take(12)?; // type, start_idx, leds_count
            }
        }
        if protocol >= 5 {
            r.take(4)?; // zone flags
        }
    }
    let led_count = r.u16()? as usize;
    Ok(Controller { index, device_type, name, vendor, led_count, modes, zones })
}

#[cfg(test)]
mod tests {
    use super::*;

    fn s(out: &mut Vec<u8>, v: &str) {
        out.extend_from_slice(&((v.len() + 1) as u16).to_le_bytes());
        out.extend_from_slice(v.as_bytes());
        out.push(0);
    }

    /// Build a protocol-5 controller blob: 1 mode, 1 zone (with 2x2 matrix + 1 segment), 4 LEDs.
    fn blob() -> Vec<u8> {
        let mut o = vec![0u8; 4]; // size placeholder
        o.extend_from_slice(&1i32.to_le_bytes()); // type dram
        for t in ["Corsair Vengeance", "Corsair", "desc", "1.0", "serial", "loc"] {
            s(&mut o, t);
        }
        o.extend_from_slice(&1u16.to_le_bytes()); // modes
        o.extend_from_slice(&0i32.to_le_bytes()); // active
        s(&mut o, "Direct");
        o.extend_from_slice(&[0u8; 16]); // value, flags, speed min/max
        o.extend_from_slice(&[0u8; 8]); // brightness min/max
        o.extend_from_slice(&[0u8; 8]); // colors min/max
        o.extend_from_slice(&[0u8; 4]); // speed
        o.extend_from_slice(&[0u8; 4]); // brightness
        o.extend_from_slice(&[0u8; 8]); // direction, color_mode
        o.extend_from_slice(&1u16.to_le_bytes()); // num colors
        o.extend_from_slice(&[1, 2, 3, 0]);
        o.extend_from_slice(&1u16.to_le_bytes()); // zones
        s(&mut o, "Zone");
        o.extend_from_slice(&[0u8; 16]);
        o.extend_from_slice(&24u16.to_le_bytes()); // matrix len = 8 + 4*2*2
        o.extend_from_slice(&[0u8; 24]);
        o.extend_from_slice(&1u16.to_le_bytes()); // segments
        s(&mut o, "Seg");
        o.extend_from_slice(&[0u8; 12]);
        o.extend_from_slice(&[0u8; 4]); // zone flags (protocol >= 5)
        o.extend_from_slice(&4u16.to_le_bytes()); // leds
        o
    }

    #[test]
    fn parses_controller_v5() {
        let c = parse_controller(3, &blob(), 5).unwrap();
        assert_eq!(c.name, "Corsair Vengeance");
        assert_eq!(c.vendor, "Corsair");
        assert_eq!(c.led_count, 4);
        assert_eq!(c.modes.len(), 1);
        assert_eq!(c.modes[0].name, "Direct");
        assert_eq!(c.direct_mode().unwrap().index, 0);
        let one = c.modes[0].with_single_color(Color::new(9, 8, 7));
        assert_eq!(&one[one.len() - 6..], &[1, 0, 9, 8, 7, 0]);
        assert_eq!(c.device_type, 1);
        assert_eq!(c.index, 3);
    }

    #[test]
    fn truncated_data_errors() {
        let b = blob();
        assert!(parse_controller(0, &b[..b.len() - 10], 5).is_err());
    }
}
