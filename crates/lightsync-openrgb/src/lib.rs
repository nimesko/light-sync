//! Minimal OpenRGB SDK client (TCP, default port 6742). Speaks protocol <= 5; the server
//! downgrades automatically when we announce our max version.
//!
//! Safety rules (older OpenRGB servers trust clients and overflow their heap otherwise):
//! - every UpdateLEDs frame carries exactly the controller's current LED count ([`fit_frame`]);
//! - DEVICE_LIST_UPDATED notifications are tracked ([`OpenRgbProvider::poll_events`]) so callers
//!   stop writing to controller indexes that may now point at another device;
//! - a write that cannot complete within [`WRITE_TIMEOUT`] drops the connection instead of
//!   queueing frames behind a stalled server.

use anyhow::{bail, Context, Result};
use async_trait::async_trait;
use lightsync_core::{Color, Device, DeviceKind, LightingProvider};
use std::time::Duration;
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::TcpStream;
use tokio::time::{timeout, timeout_at, Instant};

pub const DEFAULT_ADDR: &str = "127.0.0.1:6742";
const CLIENT_MAX_PROTOCOL: u32 = 5;
/// A write to OpenRGB that takes longer than this means the server stopped reading: the
/// connection is dropped (and re-established by the caller) rather than letting frames pile up.
pub const WRITE_TIMEOUT: Duration = Duration::from_millis(750);
/// Replies to requests must arrive within this.
const READ_TIMEOUT: Duration = Duration::from_secs(5);

const PKT_REQUEST_CONTROLLER_COUNT: u32 = 0;
const PKT_REQUEST_CONTROLLER_DATA: u32 = 1;
const PKT_REQUEST_PROTOCOL_VERSION: u32 = 40;
const PKT_SET_CLIENT_NAME: u32 = 50;
/// Sent by the server (protocol >= 1) whenever its device list changes.
const PKT_DEVICE_LIST_UPDATED: u32 = 100;
const PKT_RESIZE_ZONE: u32 = 1000;
const PKT_UPDATE_LEDS: u32 = 1050;
const PKT_UPDATE_ZONE_LEDS: u32 = 1051;
const PKT_UPDATE_MODE: u32 = 1101;

/// Mode flags and color modes (OpenRGB's RGBController.h).
pub const MODE_FLAG_HAS_SPEED: u32 = 1 << 0;
pub const MODE_FLAG_HAS_PER_LED_COLOR: u32 = 1 << 5;
pub const MODE_FLAG_HAS_MODE_SPECIFIC_COLOR: u32 = 1 << 6;
pub const MODE_COLORS_PER_LED: u32 = 1;
pub const MODE_COLORS_MODE_SPECIFIC: u32 = 2;

/// Parsed subset of an OpenRGB controller description.
#[derive(Debug, Clone, PartialEq)]
pub struct Controller {
    pub index: u32,
    pub device_type: i32,
    pub name: String,
    pub vendor: String,
    pub led_count: usize,
    /// Index of the mode the device is in now (as the server reports it).
    pub active_mode: i32,
    pub modes: Vec<Mode>,
    pub zones: Vec<Zone>,
    /// Each LED's name, in LED order (keyboards: "Key: A", "Key: Left Shift"...); empty if the
    /// server didn't send them.
    pub led_names: Vec<String>,
}

#[derive(Debug, Clone, PartialEq)]
pub struct Zone {
    pub name: String,
    pub leds_min: u32,
    pub leds_max: u32,
    pub leds_count: u32,
    /// The zone's grid (keyboards): where each LED sits, row by row.
    pub matrix: Option<Matrix>,
}

/// A zone's LED grid: `map[row * width + col]` is the LED index within the zone, or `NONE`.
#[derive(Debug, Clone, PartialEq)]
pub struct Matrix {
    pub height: u32,
    pub width: u32,
    pub map: Vec<u32>,
}

impl Matrix {
    pub const NONE: u32 = u32::MAX;
}

/// One device mode, kept as the exact serialized block the server sent so it can be sent back.
#[derive(Debug, Clone, PartialEq)]
pub struct Mode {
    pub index: u32,
    pub name: String,
    pub flags: u32,
    pub colors_max: u32,
    pub color_mode: u32,
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

    /// Takes one color per LED (UpdateLEDs works in this mode).
    pub fn per_led(&self) -> bool {
        self.color_mode == MODE_COLORS_PER_LED || self.flags & MODE_FLAG_HAS_PER_LED_COLOR != 0
    }

    /// Takes a color list inside the mode itself (set with UpdateMode).
    pub fn mode_color(&self) -> bool {
        self.colors_max >= 1 && (self.color_mode == MODE_COLORS_MODE_SPECIFIC || self.flags & MODE_FLAG_HAS_MODE_SPECIFIC_COLOR != 0)
    }
}

/// How software colors reach a device.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Drive {
    /// A per-LED mode (Direct, or the closest thing): frames go out with UpdateLEDs.
    PerLed { mode: u32 },
    /// No per-LED mode: the device's Static mode, carrying one color, is re-sent with UpdateMode.
    StaticColor { mode: u32 },
}

impl Drive {
    pub fn mode(&self) -> u32 {
        match *self {
            Drive::PerLed { mode } | Drive::StaticColor { mode } => mode,
        }
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

    /// How to drive this device, or why it cannot be driven. Order: a mode named Direct, another
    /// per-LED mode (Custom / Software first, Static last since it may write to flash), then a
    /// Static (or other single-color) mode set with UpdateMode. `prefer_static` puts Static first.
    pub fn drive(&self, prefer_static: bool) -> std::result::Result<Drive, String> {
        if self.led_count == 0 {
            return Err("it has no LEDs (resize its zones in OpenRGB)".into());
        }
        if self.modes.is_empty() {
            return Err("OpenRGB reports no modes for it".into());
        }
        let static_color = || {
            self.static_mode()
                .filter(|m| m.mode_color())
                .or_else(|| self.modes.iter().find(|m| m.mode_color()))
                .map(|m| Drive::StaticColor { mode: m.index })
        };
        if prefer_static {
            if let Some(d) = static_color() {
                return Ok(d);
            }
        }
        if let Some(m) = self.find_mode(&["direct"]) {
            return Ok(Drive::PerLed { mode: m.index });
        }
        let per_led = self
            .find_mode(&["custom", "software"])
            .filter(|m| m.per_led())
            // Modes without a speed are still images, not animations that happen to take colors.
            .or_else(|| self.modes.iter().find(|m| m.per_led() && m.flags & MODE_FLAG_HAS_SPEED == 0 && !m.name.eq_ignore_ascii_case("static")))
            .or_else(|| self.modes.iter().find(|m| m.per_led() && !m.name.eq_ignore_ascii_case("static")))
            .or_else(|| self.static_mode().filter(|m| m.per_led()));
        if let Some(m) = per_led {
            return Ok(Drive::PerLed { mode: m.index });
        }
        static_color().ok_or_else(|| {
            let names: Vec<&str> = self.modes.iter().map(|m| m.name.as_str()).collect();
            format!("none of its modes takes software colors (modes: {})", names.join(", "))
        })
    }
}

/// What re-reading a controller found.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Change {
    /// Same device, same LEDs (its mode may differ: see `Controller::active_mode`).
    Same,
    /// Same device, but its LED or zone counts changed.
    Resized,
    /// Another device now sits at that index (or it is gone).
    Replaced,
}

pub struct OpenRgbProvider {
    addr: String,
    stream: Option<TcpStream>,
    /// Bytes read from the server but not yet parsed into packets.
    rbuf: Vec<u8>,
    /// A DEVICE_LIST_UPDATED notification arrived since `poll_events` last reported one.
    list_changed: bool,
    protocol: u32,
    /// Controller count the server reported at the last [`OpenRgbProvider::reload`].
    count: u32,
    controllers: Vec<Controller>,
    /// Vendors handled by another provider (none by default).
    skip_vendors: Vec<String>,
    /// Prefer the device's hardware Static mode over Direct (fixes devices that ignore Direct).
    prefer_static: bool,
}

/// Exactly `count` colors: `frame` cut to size, or padded with black. OpenRGB servers before 1.0
/// copy as many colors as the packet says into the controller's buffer, so a frame sized for a
/// zone the user has since shrunk corrupts the server's heap.
pub fn fit_frame(frame: &[Color], count: usize) -> Vec<Color> {
    let mut v: Vec<Color> = frame.iter().take(count).copied().collect();
    v.resize(count, Color::BLACK);
    v
}

impl OpenRgbProvider {
    pub fn new(addr: impl Into<String>) -> Self {
        Self { addr: addr.into(), stream: None, rbuf: vec![], list_changed: false, protocol: 0, count: 0, controllers: vec![], skip_vendors: vec![], prefer_static: false }
    }

    pub fn prefer_static(mut self, yes: bool) -> Self {
        self.prefer_static = yes;
        self
    }

    pub fn prefers_static(&self) -> bool {
        self.prefer_static
    }

    pub fn skip_vendor(mut self, vendor: &str) -> Self {
        self.skip_vendors.push(vendor.to_lowercase());
        self
    }

    pub fn protocol(&self) -> u32 {
        self.protocol
    }

    /// How many controllers the server had at the last `reload` (some may not have parsed).
    pub fn server_count(&self) -> u32 {
        self.count
    }

    pub fn controllers(&self) -> &[Controller] {
        &self.controllers
    }

    pub fn controller(&self, dev: u32) -> Option<&Controller> {
        self.controllers.iter().find(|c| c.index == dev)
    }

    pub fn is_connected(&self) -> bool {
        self.stream.is_some()
    }

    fn owned(&self, c: &Controller) -> bool {
        let v = c.vendor.to_lowercase();
        let n = c.name.to_lowercase();
        !self.skip_vendors.iter().any(|s| v.contains(s) || n.contains(s))
    }

    fn disconnect(&mut self) {
        self.stream = None;
        self.rbuf.clear();
    }

    async fn send_mode(&mut self, dev: u32, idx: u32, raw: &[u8]) -> Result<()> {
        let mut m = Vec::with_capacity(8 + raw.len());
        m.extend_from_slice(&((8 + raw.len()) as u32).to_le_bytes());
        m.extend_from_slice(&idx.to_le_bytes());
        m.extend_from_slice(raw);
        self.send(dev, PKT_UPDATE_MODE, &m).await
    }

    async fn send(&mut self, dev: u32, ty: u32, payload: &[u8]) -> Result<()> {
        let s = self.stream.as_mut().context("not connected to OpenRGB")?;
        let mut buf = Vec::with_capacity(16 + payload.len());
        buf.extend_from_slice(b"ORGB");
        buf.extend_from_slice(&dev.to_le_bytes());
        buf.extend_from_slice(&ty.to_le_bytes());
        buf.extend_from_slice(&(payload.len() as u32).to_le_bytes());
        buf.extend_from_slice(payload);
        let r = match timeout(WRITE_TIMEOUT, s.write_all(&buf)).await {
            Ok(Ok(())) => Ok(()),
            Ok(Err(e)) => Err(anyhow::Error::new(e).context("writing to OpenRGB")),
            Err(_) => Err(anyhow::anyhow!("OpenRGB stopped reading (write timed out after {WRITE_TIMEOUT:?})")),
        };
        if r.is_err() {
            // A half-written packet leaves the stream unusable.
            self.disconnect();
        }
        r
    }

    /// Take one complete packet `(type, body)` from the read buffer, if there is one.
    fn take_packet(&mut self) -> Result<Option<(u32, Vec<u8>)>> {
        if self.rbuf.len() < 16 {
            return Ok(None);
        }
        if &self.rbuf[0..4] != b"ORGB" {
            bail!("bad OpenRGB packet magic");
        }
        let ty = u32::from_le_bytes(self.rbuf[8..12].try_into().unwrap());
        let size = u32::from_le_bytes(self.rbuf[12..16].try_into().unwrap()) as usize;
        if size > 16 * 1024 * 1024 {
            bail!("absurd OpenRGB packet size {size}");
        }
        if self.rbuf.len() < 16 + size {
            return Ok(None);
        }
        let body = self.rbuf[16..16 + size].to_vec();
        self.rbuf.drain(..16 + size);
        if ty == PKT_DEVICE_LIST_UPDATED {
            self.list_changed = true;
        }
        Ok(Some((ty, body)))
    }

    /// Read packets until one of type `want` arrives (notifications are noted and skipped).
    async fn recv(&mut self, want: u32) -> Result<Vec<u8>> {
        let r = self.recv_inner(want).await;
        if r.is_err() {
            self.disconnect();
        }
        r
    }

    async fn recv_inner(&mut self, want: u32) -> Result<Vec<u8>> {
        let deadline = Instant::now() + READ_TIMEOUT;
        let mut buf = vec![0u8; 64 * 1024];
        loop {
            while let Some((ty, body)) = self.take_packet()? {
                if ty == want {
                    return Ok(body);
                }
            }
            let s = self.stream.as_mut().context("not connected to OpenRGB")?;
            let n = timeout_at(deadline, s.read(&mut buf)).await.context("OpenRGB timeout")??;
            if n == 0 {
                bail!("OpenRGB closed the connection");
            }
            self.rbuf.extend_from_slice(&buf[..n]);
        }
    }

    /// Read whatever the server sent without waiting. Returns whether the server's device list
    /// changed since the last call (controller indexes may now point at other devices). Errors
    /// when the server closed the connection.
    pub fn poll_events(&mut self) -> Result<bool> {
        let mut buf = [0u8; 4096];
        loop {
            let s = self.stream.as_mut().context("not connected to OpenRGB")?;
            match s.try_read(&mut buf) {
                Ok(0) => {
                    self.disconnect();
                    bail!("OpenRGB closed the connection");
                }
                Ok(n) => self.rbuf.extend_from_slice(&buf[..n]),
                Err(e) if e.kind() == std::io::ErrorKind::WouldBlock => break,
                Err(e) => {
                    self.disconnect();
                    return Err(anyhow::Error::new(e).context("reading from OpenRGB"));
                }
            }
        }
        // No request is outstanding here, so anything else is stale and dropped.
        while self.take_packet()?.is_some() {}
        Ok(std::mem::take(&mut self.list_changed))
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
        let resized: std::collections::BTreeSet<u32> = todo.iter().map(|t| t.0).collect();
        for (dev, label, zone, size) in todo {
            self.resize_zone(dev, zone, size).await?;
            done.push(format!("{label} -> {size} LEDs"));
        }
        // LED counts changed on the server; re-read them so later writes cover the new LEDs.
        for dev in resized {
            self.refresh_controller(dev).await?;
        }
        Ok(done)
    }

    async fn request_controller(&mut self, dev: u32) -> Result<Controller> {
        self.send(dev, PKT_REQUEST_CONTROLLER_DATA, &self.protocol.to_le_bytes()).await?;
        let b = self.recv(PKT_REQUEST_CONTROLLER_DATA).await?;
        parse_controller(dev, &b, self.protocol)
    }

    /// Re-request one controller's data (after a resize the LED count changes).
    pub async fn refresh_controller(&mut self, dev: u32) -> Result<()> {
        self.validate(dev).await.map(|_| ())
    }

    /// Re-read one controller and say what changed since it was last read. The stored copy is
    /// updated either way (including its active mode).
    pub async fn validate(&mut self, dev: u32) -> Result<Change> {
        let c = self.request_controller(dev).await?;
        let change = match self.controllers.iter_mut().find(|x| x.index == dev) {
            Some(slot) => {
                let change = if slot.name != c.name || slot.device_type != c.device_type {
                    Change::Replaced
                } else if slot.led_count != c.led_count || slot.zones != c.zones {
                    Change::Resized
                } else {
                    Change::Same
                };
                *slot = c;
                change
            }
            None => {
                self.controllers.push(c);
                Change::Replaced
            }
        };
        Ok(change)
    }

    /// How many controllers the server has now.
    pub async fn controller_count(&mut self) -> Result<u32> {
        self.send(0, PKT_REQUEST_CONTROLLER_COUNT, &[]).await?;
        let b = self.recv(PKT_REQUEST_CONTROLLER_COUNT).await?;
        Ok(u32::from_le_bytes(b.get(0..4).context("short count reply")?.try_into().unwrap()))
    }

    /// Re-read the whole device list on the current connection.
    pub async fn reload(&mut self) -> Result<()> {
        self.list_changed = false;
        let count = self.controller_count().await?;
        self.count = count;
        let mut list = vec![];
        for i in 0..count {
            match self.request_controller(i).await {
                Ok(c) => list.push(c),
                Err(e) if self.stream.is_none() => return Err(e),
                Err(e) => eprintln!("[openrgb] device {i}: cannot parse controller data (protocol {}): {e}", self.protocol),
            }
        }
        self.controllers = list;
        Ok(())
    }

    /// Light one zone of one device and turn the rest of that device off (used to map ARGB headers to fans).
    pub async fn light_single_zone(&mut self, dev: u32, zone: u32, color: Color) -> Result<String> {
        let c = self.controllers.iter().find(|c| c.index == dev).cloned().with_context(|| format!("no OpenRGB device #{dev}"))?;
        let z = c.zones.get(zone as usize).with_context(|| format!("device #{dev} ({}) has no zone {zone}", c.name))?.clone();
        if z.leds_count == 0 {
            bail!("zone {zone} ({}) has 0 LEDs; run `lightsync expand` first", z.name);
        }
        if let Some(m) = c.direct_mode() {
            self.send_mode(dev, m.index, &m.raw).await?;
        }
        self.send(dev, PKT_UPDATE_LEDS, &leds_payload(c.led_count, Color::BLACK)).await?;
        self.send(dev, PKT_UPDATE_ZONE_LEDS, &zone_leds_payload(zone, z.leds_count as usize, color)).await?;
        Ok(format!("{} / {} ({} LEDs)", c.name, z.name, z.leds_count))
    }

    /// Set one zone of one device to a color, leaving the device's other zones as they are.
    pub async fn set_zone_color(&mut self, dev: u32, zone: u32, color: Color) -> Result<()> {
        let c = self.controllers.iter().find(|c| c.index == dev).cloned().with_context(|| format!("no OpenRGB device #{dev}"))?;
        let z = c.zones.get(zone as usize).with_context(|| format!("device #{dev} ({}) has no zone {zone}", c.name))?.clone();
        if z.leds_count == 0 {
            bail!("zone {zone} ({}) has 0 LEDs", z.name);
        }
        if let Some(m) = c.direct_mode() {
            self.send_mode(dev, m.index, &m.raw).await?;
        }
        self.send(dev, PKT_UPDATE_ZONE_LEDS, &zone_leds_payload(zone, z.leds_count as usize, color)).await
    }

    /// Controllers this provider drives (all of them unless a vendor was skipped).
    pub fn owned_controllers(&self) -> Vec<Controller> {
        self.controllers.iter().filter(|c| self.owned(c)).cloned().collect()
    }

    /// Put every owned device with LEDs into its software-controlled ("Direct") mode.
    /// Done once per connection; frames are then streamed with `write_leds`.
    pub async fn enable_direct(&mut self) -> Result<()> {
        for c in self.owned_controllers() {
            if let Ok(d) = c.drive(self.prefer_static) {
                self.set_mode(c.index, d.mode()).await?;
            }
        }
        Ok(())
    }

    /// Switch a device to one of its modes, as the server last described it.
    pub async fn set_mode(&mut self, dev: u32, mode: u32) -> Result<()> {
        let raw = self.mode_of(dev, mode)?.raw.clone();
        self.send_mode(dev, mode, &raw).await
    }

    /// Switch a device to a single-color mode (Static) showing `color`.
    pub async fn set_mode_color(&mut self, dev: u32, mode: u32, color: Color) -> Result<()> {
        let raw = self.mode_of(dev, mode)?.with_single_color(color);
        self.send_mode(dev, mode, &raw).await
    }

    /// Send a mode block exactly as given (e.g. one saved earlier, to restore a device's mode).
    pub async fn send_raw_mode(&mut self, dev: u32, mode: u32, raw: &[u8]) -> Result<()> {
        self.mode_of(dev, mode)?;
        self.send_mode(dev, mode, raw).await
    }

    fn mode_of(&self, dev: u32, mode: u32) -> Result<&Mode> {
        let c = self.controller(dev).with_context(|| format!("no OpenRGB device #{dev}"))?;
        c.modes.get(mode as usize).with_context(|| format!("{} has no mode {mode}", c.name))
    }

    /// Send one frame (one color per LED, in OpenRGB LED order) to a device. The frame is fitted
    /// to the controller's current LED count (see [`fit_frame`]); returns whether it had to be.
    pub async fn write_leds(&mut self, dev: u32, colors: &[Color]) -> Result<bool> {
        let count = self.controller(dev).with_context(|| format!("no OpenRGB device #{dev}"))?.led_count.min(u16::MAX as usize);
        if count == 0 {
            return Ok(!colors.is_empty());
        }
        let fitted = colors.len() != count;
        let colors = fit_frame(colors, count);
        let mut p = Vec::with_capacity(6 + colors.len() * 4);
        p.extend_from_slice(&((6 + colors.len() * 4) as u32).to_le_bytes());
        p.extend_from_slice(&(colors.len() as u16).to_le_bytes());
        for c in &colors {
            p.extend_from_slice(&[c.r, c.g, c.b, 0]);
        }
        self.send(dev, PKT_UPDATE_LEDS, &p).await?;
        Ok(fitted)
    }

    pub async fn connect(&mut self) -> Result<()> {
        self.disconnect();
        self.list_changed = false;
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
        self.reload().await?;
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
            match c.drive(self.prefer_static) {
                Ok(Drive::StaticColor { mode }) => self.set_mode_color(c.index, mode, color).await?,
                Ok(Drive::PerLed { mode }) => {
                    self.set_mode(c.index, mode).await?;
                    self.send(c.index, PKT_UPDATE_LEDS, &leds_payload(c.led_count, color)).await?;
                }
                Err(_) => {}
            }
        }
        Ok(())
    }
}

/// UPDATELEDS payload: every LED of a device set to one color.
fn leds_payload(count: usize, color: Color) -> Vec<u8> {
    let mut p = Vec::with_capacity(6 + count * 4);
    p.extend_from_slice(&((6 + count * 4) as u32).to_le_bytes());
    p.extend_from_slice(&(count as u16).to_le_bytes());
    for _ in 0..count {
        p.extend_from_slice(&[color.r, color.g, color.b, 0]);
    }
    p
}

/// UPDATEZONELEDS payload: every LED of one zone set to one color.
fn zone_leds_payload(zone: u32, count: usize, color: Color) -> Vec<u8> {
    let mut p = Vec::with_capacity(10 + count * 4);
    p.extend_from_slice(&((10 + count * 4) as u32).to_le_bytes());
    p.extend_from_slice(&zone.to_le_bytes());
    p.extend_from_slice(&(count as u16).to_le_bytes());
    for _ in 0..count {
        p.extend_from_slice(&[color.r, color.g, color.b, 0]);
    }
    p
}

pub fn kind_from_openrgb(t: i32) -> DeviceKind {
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
    let active_mode = r.i32()?;
    let mut modes = vec![];
    for mode_idx in 0..num_modes {
        let mode_start = r.p;
        let mode_name = r.string()?; // mode name
        r.i32()?; // value
        let flags = r.u32()?;
        r.take(4 * 2)?; // speed_min, speed_max
        if protocol >= 3 {
            r.take(8)?; // brightness min/max
        }
        r.u32()?; // colors_min
        let colors_max = r.u32()?;
        r.take(4)?; // speed
        if protocol >= 3 {
            r.take(4)?; // brightness
        }
        r.take(4)?; // direction
        let color_mode = r.u32()?;
        let colors_off = r.p - mode_start;
        let n = r.u16()? as usize;
        r.take(n * 4)?;
        modes.push(Mode { index: mode_idx as u32, name: mode_name, flags, colors_max, color_mode, raw: r.b[mode_start..r.p].to_vec(), colors_off });
    }
    let num_zones = r.u16()?;
    let mut zones = vec![];
    for _ in 0..num_zones {
        let zone_name = r.string()?; // zone name
        let z = r.take(4 * 4)?; // type, leds_min, leds_max, leds_count
        let u = |i: usize| u32::from_le_bytes(z[i..i + 4].try_into().unwrap());
        let matrix_len = r.u16()? as usize;
        let raw = r.take(matrix_len)?;
        let matrix = parse_matrix(raw);
        zones.push(Zone { name: zone_name, leds_min: u(4), leds_max: u(8), leds_count: u(12), matrix });
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
    // LED names follow (name + value each); older or shorter blocks just have none.
    let mut led_names = Vec::with_capacity(led_count);
    for _ in 0..led_count {
        match (r.string(), r.u32()) {
            (Ok(n), Ok(_)) => led_names.push(n),
            _ => {
                led_names.clear();
                break;
            }
        }
    }
    Ok(Controller { index, device_type, name, vendor, led_count, active_mode, modes, zones, led_names })
}

/// A zone's matrix block: height, width, then height * width LED indices (u32 each).
fn parse_matrix(raw: &[u8]) -> Option<Matrix> {
    if raw.len() < 8 {
        return None;
    }
    let u = |i: usize| u32::from_le_bytes(raw[i..i + 4].try_into().unwrap());
    let (height, width) = (u(0), u(4));
    let cells = (height as usize).checked_mul(width as usize)?;
    if cells == 0 || cells > 4096 || raw.len() < 8 + cells * 4 {
        return None;
    }
    Some(Matrix { height, width, map: (0..cells).map(|k| u(8 + 4 * k)).collect() })
}

#[cfg(test)]
mod tests {
    use super::*;

    fn s(out: &mut Vec<u8>, v: &str) {
        out.extend_from_slice(&((v.len() + 1) as u16).to_le_bytes());
        out.extend_from_slice(v.as_bytes());
        out.push(0);
    }

    /// One protocol-5 mode block.
    fn mode(o: &mut Vec<u8>, name: &str, flags: u32, colors_max: u32, color_mode: u32) {
        s(o, name);
        o.extend_from_slice(&[0u8; 4]); // value
        o.extend_from_slice(&flags.to_le_bytes());
        o.extend_from_slice(&[0u8; 8]); // speed min/max
        o.extend_from_slice(&[0u8; 8]); // brightness min/max
        o.extend_from_slice(&[0u8; 4]); // colors min
        o.extend_from_slice(&colors_max.to_le_bytes());
        o.extend_from_slice(&[0u8; 4]); // speed
        o.extend_from_slice(&[0u8; 4]); // brightness
        o.extend_from_slice(&[0u8; 4]); // direction
        o.extend_from_slice(&color_mode.to_le_bytes());
        o.extend_from_slice(&1u16.to_le_bytes()); // num colors
        o.extend_from_slice(&[1, 2, 3, 0]);
    }

    /// A protocol-5 controller blob: the given modes (name, flags, colors_max, color_mode), one
    /// zone (with 2x2 matrix + 1 segment) and `leds` LEDs.
    fn blob_with(name: &str, modes: &[(&str, u32, u32, u32)], leds: u16) -> Vec<u8> {
        let mut o = vec![0u8; 4]; // size placeholder
        o.extend_from_slice(&1i32.to_le_bytes()); // type dram
        for t in [name, "Corsair", "desc", "1.0", "serial", "loc"] {
            s(&mut o, t);
        }
        o.extend_from_slice(&(modes.len() as u16).to_le_bytes()); // modes
        o.extend_from_slice(&0i32.to_le_bytes()); // active
        for &(n, f, cm, c) in modes {
            mode(&mut o, n, f, cm, c);
        }
        o.extend_from_slice(&1u16.to_le_bytes()); // zones
        s(&mut o, "Zone");
        o.extend_from_slice(&[0u8; 12]);
        o.extend_from_slice(&(leds as u32).to_le_bytes());
        o.extend_from_slice(&24u16.to_le_bytes()); // matrix len = 8 + 4*2*2
        o.extend_from_slice(&[0u8; 24]);
        o.extend_from_slice(&1u16.to_le_bytes()); // segments
        s(&mut o, "Seg");
        o.extend_from_slice(&[0u8; 12]);
        o.extend_from_slice(&[0u8; 4]); // zone flags (protocol >= 5)
        o.extend_from_slice(&leds.to_le_bytes()); // leds
        o
    }

    fn blob() -> Vec<u8> {
        blob_with("Corsair Vengeance", &[("Direct", MODE_FLAG_HAS_PER_LED_COLOR, 0, MODE_COLORS_PER_LED)], 4)
    }

    #[test]
    fn parses_controller_v5() {
        let c = parse_controller(3, &blob(), 5).unwrap();
        assert_eq!(c.name, "Corsair Vengeance");
        assert_eq!(c.vendor, "Corsair");
        assert_eq!(c.led_count, 4);
        assert_eq!(c.modes.len(), 1);
        assert_eq!(c.modes[0].name, "Direct");
        assert!(c.modes[0].per_led());
        assert_eq!(c.active_mode, 0);
        assert_eq!(c.zones[0].leds_count, 4);
        assert_eq!(c.direct_mode().unwrap().index, 0);
        let one = c.modes[0].with_single_color(Color::new(9, 8, 7));
        assert_eq!(&one[one.len() - 6..], &[1, 0, 9, 8, 7, 0]);
        assert_eq!(c.device_type, 1);
        assert_eq!(c.index, 3);
    }

    #[test]
    fn picks_how_to_drive_each_device() {
        let per = (MODE_FLAG_HAS_PER_LED_COLOR, 0, MODE_COLORS_PER_LED);
        let single = (MODE_FLAG_HAS_MODE_SPECIFIC_COLOR, 1, MODE_COLORS_MODE_SPECIFIC);
        let none = (0, 0, 0);
        let drive = |modes: &[(&str, (u32, u32, u32))], leds: u16, prefer_static: bool| {
            let m: Vec<(&str, u32, u32, u32)> = modes.iter().map(|(n, (f, cm, c))| (*n, *f, *cm, *c)).collect();
            parse_controller(0, &blob_with("Dev", &m, leds), 5).unwrap().drive(prefer_static)
        };
        assert_eq!(drive(&[("Rainbow", none), ("Static", single), ("Direct", per)], 4, false), Ok(Drive::PerLed { mode: 2 }));
        assert_eq!(drive(&[("Rainbow", none), ("Static", single), ("Direct", per)], 4, true), Ok(Drive::StaticColor { mode: 1 }));
        // No Direct: another per-LED mode, then Static with a color.
        assert_eq!(drive(&[("Static", single), ("Custom", per)], 4, false), Ok(Drive::PerLed { mode: 1 }));
        assert_eq!(drive(&[("Breathing", single), ("Static", single)], 4, false), Ok(Drive::StaticColor { mode: 1 }));
        assert_eq!(drive(&[("Breathing", single)], 4, false), Ok(Drive::StaticColor { mode: 0 }));
        let why = drive(&[("Rainbow", none), ("Spectrum Cycle", none)], 4, false).unwrap_err();
        assert!(why.contains("Rainbow, Spectrum Cycle"), "{why}");
        assert!(drive(&[("Direct", per)], 0, false).unwrap_err().contains("no LEDs"));
    }

    #[test]
    fn frames_fit_the_controller() {
        let red = Color::new(255, 0, 0);
        assert_eq!(fit_frame(&[red; 5], 3), vec![red; 3]);
        assert_eq!(fit_frame(&[red; 2], 3), vec![red, red, Color::BLACK]);
        assert_eq!(fit_frame(&[red; 3], 3), vec![red; 3]);
        assert!(fit_frame(&[red; 3], 0).is_empty());
    }

    #[test]
    fn led_payloads() {
        let c = Color::new(1, 2, 3);
        assert_eq!(leds_payload(2, c), vec![14, 0, 0, 0, 2, 0, 1, 2, 3, 0, 1, 2, 3, 0]);
        assert_eq!(zone_leds_payload(5, 1, c), vec![14, 0, 0, 0, 5, 0, 0, 0, 1, 0, 1, 2, 3, 0]);
    }

    #[test]
    fn truncated_data_errors() {
        let b = blob();
        assert!(parse_controller(0, &b[..b.len() - 10], 5).is_err());
    }

    fn packet(dev: u32, ty: u32, body: &[u8]) -> Vec<u8> {
        let mut p = b"ORGB".to_vec();
        p.extend_from_slice(&dev.to_le_bytes());
        p.extend_from_slice(&ty.to_le_bytes());
        p.extend_from_slice(&(body.len() as u32).to_le_bytes());
        p.extend_from_slice(body);
        p
    }

    /// A fake OpenRGB server with one controller whose LED count the test can change; it
    /// records the color count of every UpdateLEDs it gets and can push DEVICE_LIST_UPDATED.
    #[tokio::test]
    async fn clamps_frames_and_sees_list_updates() {
        use std::sync::{Arc, Mutex};
        let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
        let addr = listener.local_addr().unwrap().to_string();
        let leds = Arc::new(Mutex::new(4u16));
        let written = Arc::new(Mutex::new(Vec::<u16>::new()));
        let (notify_tx, mut notify_rx) = tokio::sync::mpsc::unbounded_channel::<()>();
        let (l2, w2) = (leds.clone(), written.clone());
        tokio::spawn(async move {
            let (mut sock, _) = listener.accept().await.unwrap();
            loop {
                let mut h = [0u8; 16];
                tokio::select! {
                    r = sock.read_exact(&mut h) => { if r.is_err() { return; } }
                    Some(()) = notify_rx.recv() => {
                        sock.write_all(&packet(0, PKT_DEVICE_LIST_UPDATED, &[])).await.unwrap();
                        continue;
                    }
                }
                let dev = u32::from_le_bytes(h[4..8].try_into().unwrap());
                let ty = u32::from_le_bytes(h[8..12].try_into().unwrap());
                let mut body = vec![0u8; u32::from_le_bytes(h[12..16].try_into().unwrap()) as usize];
                sock.read_exact(&mut body).await.unwrap();
                let reply = match ty {
                    PKT_REQUEST_PROTOCOL_VERSION => Some(5u32.to_le_bytes().to_vec()),
                    PKT_REQUEST_CONTROLLER_COUNT => Some(1u32.to_le_bytes().to_vec()),
                    PKT_REQUEST_CONTROLLER_DATA => {
                        let n = *l2.lock().unwrap();
                        Some(blob_with("Strip", &[("Direct", MODE_FLAG_HAS_PER_LED_COLOR, 0, MODE_COLORS_PER_LED)], n))
                    }
                    PKT_UPDATE_LEDS => {
                        w2.lock().unwrap().push(u16::from_le_bytes(body[4..6].try_into().unwrap()));
                        assert_eq!(body.len(), 6 + 4 * u16::from_le_bytes(body[4..6].try_into().unwrap()) as usize);
                        None
                    }
                    _ => None,
                };
                if let Some(r) = reply {
                    sock.write_all(&packet(dev, ty, &r)).await.unwrap();
                }
            }
        });
        let mut o = OpenRgbProvider::new(addr);
        o.discover().await.unwrap();
        assert_eq!(o.controllers()[0].led_count, 4);
        let red = [Color::new(255, 0, 0); 6];
        assert!(o.write_leds(0, &red).await.unwrap(), "6 colors fitted to 4 LEDs");
        assert!(!o.write_leds(0, &red[..4]).await.unwrap());
        // The user shrinks the zone in OpenRGB: validation sees it, frames follow.
        *leds.lock().unwrap() = 2;
        assert_eq!(o.validate(0).await.unwrap(), Change::Resized);
        assert_eq!(o.validate(0).await.unwrap(), Change::Same);
        o.write_leds(0, &red[..4]).await.unwrap();
        assert!(o.write_leds(7, &red).await.is_err(), "unknown controller");
        assert!(!o.poll_events().unwrap());
        notify_tx.send(()).unwrap();
        let mut seen = false;
        for _ in 0..100 {
            tokio::time::sleep(Duration::from_millis(10)).await;
            if o.poll_events().unwrap() {
                seen = true;
                break;
            }
        }
        assert!(seen, "DEVICE_LIST_UPDATED noticed");
        assert_eq!(o.controller_count().await.unwrap(), 1);
        tokio::time::sleep(Duration::from_millis(50)).await;
        assert_eq!(*written.lock().unwrap(), vec![4, 4, 2]);
    }
}
