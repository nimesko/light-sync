//! lightsyncd: background service that owns the lighting providers and serves JSON-RPC to the UI.
//! Windows: named pipe `\\.\pipe\lightsync`. Elsewhere (dev/tests): Unix socket `$LIGHTSYNC_SOCKET`
//! or `/tmp/lightsync.sock`. Protocol: one JSON-RPC 2.0 object per line, one response line per request.

mod audio;
mod effects;
mod rpc;
mod scene;
mod triggers;

use anyhow::{bail, Result};
use async_trait::async_trait;
use effects::AudioLevels;
use lightsync_core::{Color, LightingProvider};
use lightsync_openrgb::{kind_from_openrgb, Change, Drive, OpenRgbProvider, DEFAULT_ADDR};
use rpc::{device_frame_with, Daemon, DeviceInfo, FanInfo, Lights, ZoneInfo};
use scene::{zone_ids, Scene};
use serde_json::{json, Value};
use std::collections::{BTreeMap, BTreeSet};
use std::sync::Arc;
use std::time::{Duration, Instant};
use tokio::io::{AsyncBufReadExt, AsyncRead, AsyncReadExt, AsyncWrite, AsyncWriteExt, BufReader};
use tokio::sync::Mutex;
use triggers::Overlay;

/// Frame rate of the render loop (animated effects; static colors are only re-sent every second).
const FPS: u64 = 30;
/// Frame rate while a sound reactive effect is visible (the loop ticks at this rate and skips
/// every other tick otherwise).
const AUDIO_FPS: u64 = 2 * FPS;
/// LEDs given to each empty addressable (ARGB) header, so fans on the motherboard headers light up.
/// OpenRGB forgets zone sizes when it restarts, so this is redone on every discovery. Headers with
/// a fan layout (see scene.rs) are sized to exactly fans x LEDs per fan instead.
const DEFAULT_ARGB_LEDS: u32 = 120;

/// Unchanged frames are re-sent this often, only to devices that fall back to their own lighting
/// when software stops talking to them (Razer, while Synapse runs). Other controllers flash on
/// every write (Gigabyte RGB Fusion, the Corsair iCUE Link hub), so they only get a frame when
/// its colors change. (If a device falls back to its own lighting after a while, list it here.)
const KEEPALIVE: Duration = Duration::from_secs(1);
/// This often the OpenRGB controller count is checked and one controller (round robin) is
/// re-read, so zones resized or modes changed in OpenRGB's own window are noticed within a few
/// seconds (frames are always fitted to the LED count last read).
const CHECK_EVERY: Duration = Duration::from_secs(1);
/// After OpenRGB says its device list changed, writes pause until it has been quiet this long
/// (it is usually still detecting), then the list is read again.
const LIST_SETTLE: Duration = Duration::from_millis(1500);
/// While OpenRGB is unreachable but other devices work, try to reach it again this often.
const RECONNECT_EVERY: Duration = Duration::from_secs(5);
/// Devices driven through their Static mode get a new color at most this often (some save the
/// mode to flash on every change).
const STATIC_MIN_INTERVAL: Duration = Duration::from_millis(250);
/// OpenRGB handles each client's packets in order and writes to slow USB devices synchronously,
/// so many devices at 60 fps can queue up behind it. The periodic count request doubles as a
/// ping: replies slower than `SLOW_REPLY` widen the minimum gap between two frames to one device
/// (up to `MAX_FRAME_GAP`), replies faster than `FAST_REPLY` narrow it again.
const SLOW_REPLY: Duration = Duration::from_millis(120);
const FAST_REPLY: Duration = Duration::from_millis(30);
const MAX_FRAME_GAP: Duration = Duration::from_millis(500);

/// Devices that need unchanged colors re-sent (see `KEEPALIVE`).
fn needs_keepalive(name: &str) -> bool {
    let n = name.to_lowercase();
    n.contains("razer")
}

/// Whether to send `frame` to a device, given what was last sent and when.
fn should_send(last: Option<&(Vec<Color>, Instant)>, frame: &[Color], keepalive: bool) -> bool {
    match last {
        None => true,
        Some((prev, at)) => prev.as_slice() != frame || (keepalive && at.elapsed() >= KEEPALIVE),
    }
}

/// Average color of a frame (what a device driven through its Static mode shows).
fn average(frame: &[Color]) -> Color {
    if frame.is_empty() {
        return Color::BLACK;
    }
    let n = frame.len() as u32;
    let sum = frame.iter().fold([0u32; 3], |s, c| [s[0] + c.r as u32, s[1] + c.g as u32, s[2] + c.b as u32]);
    let avg = |v: u32| ((v + n / 2) / n) as u8;
    Color::new(avg(sum[0]), avg(sum[1]), avg(sum[2]))
}

/// How one OpenRGB device is driven, and how that went.
#[derive(Default)]
struct Driven {
    index: u32,
    /// None: it cannot be driven (see `reason`).
    drive: Option<Drive>,
    reason: Option<String>,
    /// Our mode is set on the device (cleared to switch it again).
    mode_applied: bool,
    /// Last frame sent (per-LED devices), to skip unchanged frames.
    sent: Option<(Vec<Color>, Instant)>,
    /// Last color sent (Static-mode devices).
    static_sent: Option<(Color, Instant)>,
    last_write: Option<Instant>,
    last_error: Option<String>,
}

struct RealLights {
    openrgb: OpenRgbProvider,
    cache: Option<Vec<DeviceInfo>>,
    argb_leds: u32,
    /// OpenRGB devices by stable id.
    driven: BTreeMap<String, Driven>,
    /// The mode each device was in before LightSync drove it (mode index, mode block), restored
    /// when the device is hidden.
    original_modes: BTreeMap<String, (u32, Vec<u8>)>,
    /// Problems already logged (so each is logged once per device).
    told: BTreeSet<String>,
    last_check: Instant,
    check_next: usize,
    /// When OpenRGB last said its device list changed (writes wait for it to settle).
    list_changed_at: Option<Instant>,
    /// A controller's LED count differs from the cached device list: rebuild it before writing.
    stale: bool,
    /// Minimum time between two frames to one device (0 unless OpenRGB falls behind).
    frame_gap: Duration,
}

/// Stable ids for OpenRGB devices: "openrgb:<name>", with "#2", "#3"... for repeated names.
/// Controller indexes change when OpenRGB restarts or detects devices in another order; names don't.
fn stable_ids<'a>(names: impl IntoIterator<Item = &'a str>) -> Vec<String> {
    let mut seen: BTreeMap<&str, u32> = BTreeMap::new();
    names
        .into_iter()
        .map(|n| {
            let k = seen.entry(n).or_insert(0);
            *k += 1;
            if *k == 1 {
                format!("openrgb:{n}")
            } else {
                format!("openrgb:{n}#{k}")
            }
        })
        .collect()
}

impl RealLights {
    fn new() -> Self {
        Self {
            openrgb: OpenRgbProvider::new(DEFAULT_ADDR)
                .prefer_static(std::env::var_os("LIGHTSYNC_STATIC").is_some()),

            cache: None,
            argb_leds: std::env::var("LIGHTSYNC_ARGB_LEDS")
                .ok()
                .and_then(|v| v.parse().ok())
                .unwrap_or(DEFAULT_ARGB_LEDS),
            driven: BTreeMap::new(),
            original_modes: BTreeMap::new(),
            told: BTreeSet::new(),
            last_check: Instant::now(),
            check_next: 0,
            list_changed_at: None,
            stale: false,
            frame_gap: Duration::ZERO,
        }
    }

    /// Adapt the per-device frame gap to how long OpenRGB took to answer a request.
    fn adapt(&mut self, reply: Duration) {
        if reply > SLOW_REPLY {
            let next = (self.frame_gap * 2).clamp(Duration::from_millis(33), MAX_FRAME_GAP);
            if next > self.frame_gap {
                eprintln!(
                    "[lightsyncd] OpenRGB is falling behind (reply took {} ms): at most one frame per {} ms per device",
                    reply.as_millis(),
                    next.as_millis()
                );
            }
            self.frame_gap = next;
        } else if reply < FAST_REPLY && !self.frame_gap.is_zero() {
            self.frame_gap /= 2;
            if self.frame_gap < Duration::from_millis(16) {
                self.frame_gap = Duration::ZERO;
                eprintln!("[lightsyncd] OpenRGB caught up: full frame rate again");
            }
        }
    }

    /// Log `msg` about device `id` once (per distinct message).
    fn tell(&mut self, id: &str, msg: String) {
        if self.told.insert(format!("{id}\n{msg}")) {
            eprintln!("[lightsyncd] {msg}");
        }
    }

    /// Size addressable headers: zones with a fan layout get exactly fans x LEDs per fan, other
    /// empty headers get `argb_leds`. Zones the user sized (in OpenRGB) without a fan layout are
    /// never touched, nor are hidden devices. Returns what changed.
    async fn size_zones(&mut self, scene: &Scene) -> Result<Vec<String>> {
        let ctrls = self.openrgb.owned_controllers();
        let ids = stable_ids(ctrls.iter().map(|c| c.name.as_str()));
        let mut todo = vec![];
        for (c, id) in ctrls.iter().zip(&ids) {
            if scene.is_hidden(id) {
                continue;
            }
            let zids = zone_ids(id, c.zones.iter().map(|z| z.name.as_str()));
            for ((zi, z), zid) in c.zones.iter().enumerate().zip(&zids) {
                if let Some(want) = zone_size(z.leds_min, z.leds_max, z.leds_count, scene.layouts.get(zid).map(|l| l.total()), self.argb_leds) {
                    todo.push((c.index, zi as u32, want, format!("{} / {} -> {want} LEDs", c.name, z.name)));
                }
            }
        }
        let mut resized = BTreeSet::new();
        let mut done = vec![];
        for (dev, zone, size, label) in todo {
            self.openrgb.resize_zone(dev, zone, size).await?;
            resized.insert(dev);
            done.push(label);
        }
        // LED counts changed on the server; re-read them so frames cover the new LEDs.
        for dev in resized {
            self.openrgb.refresh_controller(dev).await?;
        }
        Ok(done)
    }

    /// Read the device list: `reconnect` opens a new OpenRGB connection (and re-applies every
    /// device's mode), otherwise the list is re-read on the current one.
    async fn discover(&mut self, scene: &Scene, reconnect: bool) -> Result<Vec<DeviceInfo>> {
        self.cache = None;
        self.list_changed_at = None;
        self.stale = false;
        self.last_check = Instant::now();
        let mut out = vec![];
        let mut errors = vec![];
        let listed = if reconnect || !self.openrgb.is_connected() {
            self.openrgb.discover().await.map(|_| true)
        } else {
            self.openrgb.reload().await.map(|_| false)
        };
        match listed {
            Ok(fresh) => {
                match self.size_zones(scene).await {
                    Ok(done) => done
                        .iter()
                        .for_each(|l| eprintln!("[lightsyncd] sized header: {l}")),
                    Err(e) => eprintln!("[lightsyncd] could not size addressable headers: {e:#}"),
                }
                let ctrls = self.openrgb.owned_controllers();
                let ids = stable_ids(ctrls.iter().map(|c| c.name.as_str()));
                let mut driven = BTreeMap::new();
                for (c, id) in ctrls.iter().zip(ids) {
                    let zids = zone_ids(&id, c.zones.iter().map(|z| z.name.as_str()));
                    let zones = c
                        .zones
                        .iter()
                        .enumerate()
                        .zip(zids)
                        .map(|((zi, z), zid)| {
                            let fans = scene
                                .layouts
                                .get(&zid)
                                .map(|l| {
                                    (0..l.fans)
                                        .map(|k| FanInfo {
                                            id: format!("{zid}/{k}"),
                                            name: format!("Fan {}", k + 1),
                                        })
                                        .collect()
                                })
                                .unwrap_or_default();
                            ZoneInfo {
                                id: zid,
                                index: zi as u32,
                                name: z.name.clone(),
                                leds: z.leds_count,
                                resizable: z.leds_max > 0 && z.leds_min != z.leds_max,
                                max_leds: z.leds_max,
                                fans,
                            }
                        })
                        .collect();
                    let drive = c.drive(self.openrgb.prefers_static());
                    let mode = drive.as_ref().ok().map(|d| d.mode());
                    // Remember the device's own mode, to give it back if the user hides it.
                    if mode.is_some_and(|m| c.active_mode >= 0 && c.active_mode as u32 != m) {
                        if let Some(own) = c.modes.get(c.active_mode as usize) {
                            self.original_modes.insert(id.clone(), (own.index, own.raw.clone()));
                        }
                    }
                    let prev = self.driven.remove(&id).unwrap_or_default();
                    // Re-reading the list on the same connection keeps a mode that is still set.
                    let mode_applied = !fresh
                        && prev.mode_applied
                        && prev.index == c.index
                        && prev.drive.map(|d| d.mode()) == mode
                        && mode.is_some_and(|m| c.active_mode == m as i32);
                    driven.insert(
                        id.clone(),
                        Driven {
                            index: c.index,
                            drive: drive.as_ref().ok().copied(),
                            reason: drive.err(),
                            mode_applied,
                            last_write: prev.last_write,
                            last_error: prev.last_error,
                            ..Default::default()
                        },
                    );
                    out.push(DeviceInfo {
                        id,
                        name: c.name.clone(),
                        kind: kind_from_openrgb(c.device_type),
                        provider: "openrgb".into(),
                        led_count: c.led_count,
                        openrgb_index: Some(c.index),
                        zones,
                        hidden: false,
                        led_map: led_map(c),
                        slot: (0, 1),
                    });
                }
                self.driven = driven;
            }
            Err(e) => {
                self.driven.clear();
                errors.push(format!("OpenRGB: {e:#}"));
            }
        }
        for e in &errors {
            eprintln!("[lightsyncd] {e}");
        }
        if out.is_empty() && !errors.is_empty() {
            bail!("{}", errors.join("; "));
        }
        // Each shown device's place in the list (sound effects split by device spread over them).
        let shown = out.iter().filter(|d| !scene.is_hidden(&d.id)).count();
        let mut k = 0;
        for d in &mut out {
            d.slot = if scene.is_hidden(&d.id) { (0, 1) } else { k += 1; (k - 1, shown) };
        }
        self.cache = Some(out.clone());
        Ok(out)
    }

    /// Cheap periodic check: the controller count, and one controller re-read (round robin).
    /// Anything that moved (devices added or removed, zones resized) re-reads the list; a device
    /// switched to another mode (in OpenRGB's window) gets ours again unless it is hidden.
    async fn check(&mut self, scene: &Scene) -> Result<()> {
        self.last_check = Instant::now();
        let asked = Instant::now();
        let count = self.openrgb.controller_count().await?;
        self.adapt(asked.elapsed());
        if count != self.openrgb.server_count() {
            eprintln!(
                "[lightsyncd] OpenRGB now has {count} devices (was {}); reading them again",
                self.openrgb.server_count()
            );
            self.discover(scene, false).await?;
            return Ok(());
        }
        let n = self.openrgb.controllers().len();
        if n == 0 {
            return Ok(());
        }
        let i = self.check_next % n;
        self.check_next = i + 1;
        let index = self.openrgb.controllers()[i].index;
        match self.openrgb.validate(index).await? {
            Change::Replaced => {
                eprintln!("[lightsyncd] OpenRGB device #{index} changed; reading the list again");
                self.discover(scene, false).await?;
            }
            Change::Resized => {
                let name = self.openrgb.controller(index).map(|c| c.name.clone()).unwrap_or_default();
                eprintln!("[lightsyncd] {name}: LEDs or zones changed in OpenRGB; reading the list again");
                self.discover(scene, false).await?;
            }
            Change::Same => {
                let Some(c) = self.openrgb.controller(index).cloned() else {
                    return Ok(());
                };
                let Some((id, dr)) = self.driven.iter_mut().find(|(_, d)| d.index == index) else {
                    return Ok(());
                };
                let Some(drive) = dr.drive else {
                    return Ok(());
                };
                if scene.is_hidden(id) || !dr.mode_applied || c.active_mode == drive.mode() as i32 {
                    return Ok(());
                }
                dr.mode_applied = false;
                let id = id.clone();
                if let Some(own) = usize::try_from(c.active_mode).ok().and_then(|m| c.modes.get(m)) {
                    self.original_modes.insert(id.clone(), (own.index, own.raw.clone()));
                }
                self.tell(
                    &id,
                    format!(
                        "{}: its mode was changed outside LightSync; switching it back (hide the device in LightSync to keep its own lighting)",
                        c.name
                    ),
                );
            }
        }
        Ok(())
    }

    /// Send one OpenRGB device its frame (or nothing, if it is hidden, cannot be driven or
    /// nothing changed). Errors only when the connection is gone; a device's own failure is
    /// recorded for `diagnostics` and logged once.
    async fn drive_device(
        &mut self,
        scene: &Scene,
        d: &DeviceInfo,
        index: u32,
        t: f32,
        audio: &AudioLevels,
        overlay: &Overlay,
    ) -> Result<()> {
        let Some(mut dr) = self.driven.remove(&d.id) else {
            return Ok(());
        };
        let r = self.drive_one(&mut dr, scene, d, index, t, audio, overlay).await;
        let lost = r.is_err() && !self.openrgb.is_connected();
        match &r {
            Ok(()) => {}
            Err(e) => {
                let msg = format!("{e:#}");
                dr.last_error = Some(msg.clone());
                if !lost {
                    self.tell(&d.id, format!("{}: {msg}", d.name));
                }
            }
        }
        self.driven.insert(d.id.clone(), dr);
        if lost {
            r
        } else {
            Ok(())
        }
    }

    #[allow(clippy::too_many_arguments)]
    async fn drive_one(
        &mut self,
        dr: &mut Driven,
        scene: &Scene,
        d: &DeviceInfo,
        index: u32,
        t: f32,
        audio: &AudioLevels,
        overlay: &Overlay,
    ) -> Result<()> {
        if scene.is_hidden(&d.id) {
            if dr.mode_applied {
                // Give the device back the mode it had before LightSync drove it, if we know it.
                dr.mode_applied = false;
                dr.sent = None;
                dr.static_sent = None;
                if let Some((mode, raw)) = self.original_modes.get(&d.id).cloned() {
                    eprintln!("[lightsyncd] {} is hidden: giving it back its own mode", d.name);
                    self.openrgb.send_raw_mode(index, mode, &raw).await?;
                }
            }
            return Ok(());
        }
        let Some(drive) = dr.drive else {
            let reason = dr.reason.clone().unwrap_or_default();
            self.tell(&d.id, format!("not driving {}: {reason}", d.name));
            return Ok(());
        };
        if self.openrgb.controller(index).map(|c| c.led_count) != Some(d.led_count) {
            // The device list is out of date; this frame is fitted, the next one is rebuilt.
            self.stale = true;
        }
        let frame = device_frame_with(scene, d, t, audio, overlay);
        match drive {
            Drive::PerLed { mode } => {
                if !dr.mode_applied {
                    self.openrgb.set_mode(index, mode).await?;
                    dr.mode_applied = true;
                    dr.sent = None;
                }
                // One UpdateLEDs per device per frame at most, and none for unchanged frames.
                if !should_send(dr.sent.as_ref(), &frame, needs_keepalive(&d.name)) {
                    return Ok(());
                }
                // OpenRGB is behind: skip this frame (a later one, or the keepalive render,
                // carries the change since it still differs from what was sent).
                if dr.sent.as_ref().is_some_and(|(_, at)| at.elapsed() < self.frame_gap) {
                    return Ok(());
                }
                self.openrgb.write_leds(index, &frame).await?;
                let now = Instant::now();
                dr.sent = Some((frame, now));
                dr.last_write = Some(now);
            }
            Drive::StaticColor { mode } => {
                let c = average(&frame);
                let due = !dr.mode_applied
                    || dr
                        .static_sent
                        .is_none_or(|(prev, at)| prev != c && at.elapsed() >= STATIC_MIN_INTERVAL);
                if !due {
                    return Ok(());
                }
                self.openrgb.set_mode_color(index, mode, c).await?;
                let now = Instant::now();
                dr.mode_applied = true;
                dr.static_sent = Some((c, now));
                dr.last_write = Some(now);
            }
        }
        dr.last_error = None;
        Ok(())
    }

    async fn render_inner(&mut self, scene: &Scene, t: f32, audio: &AudioLevels, overlay: &Overlay) -> Result<()> {
        let mut write_openrgb = self.openrgb.is_connected();
        if write_openrgb {
            if self.openrgb.poll_events()? {
                if self.list_changed_at.is_none() {
                    eprintln!("[lightsyncd] OpenRGB's device list changed; waiting for it to settle");
                }
                self.list_changed_at = Some(Instant::now());
            }
            match self.list_changed_at {
                // Indexes may point at other devices now: write nothing until the list is re-read.
                Some(at) if at.elapsed() < LIST_SETTLE => write_openrgb = false,
                Some(_) => {
                    self.discover(scene, false).await?;
                }
                None if self.stale => {
                    self.discover(scene, false).await?;
                }
                None if self.last_check.elapsed() >= CHECK_EVERY => self.check(scene).await?,
                None => {}
            }
        } else if self.last_check.elapsed() >= RECONNECT_EVERY {
            // OpenRGB was not reachable (other devices are driven); see if it is now.
            self.discover(scene, true).await?;
            write_openrgb = self.openrgb.is_connected();
        }
        let devices = self.cache.clone().unwrap_or_default();
        if write_openrgb {
            for d in &devices {
                if let Some(index) = d.openrgb_index {
                    if d.led_count > 0 {
                        self.drive_device(scene, d, index, t, audio, overlay).await?;
                    }
                }
            }
        }
        Ok(())
    }
}

/// The LED count to give a zone, if it should be resized: zones with a fan layout get exactly
/// that many LEDs; empty addressable headers get `argb_leds`; anything else (a zone the user
/// sized, a fixed zone) is left alone.
fn zone_size(min: u32, max: u32, count: u32, layout_total: Option<u32>, argb_leds: u32) -> Option<u32> {
    if max == 0 || min == max {
        return None; // not resizable
    }
    let want = match layout_total {
        Some(total) if total > 0 => total.clamp(min.max(1), max),
        _ if count == 0 && argb_leds > 0 => argb_leds.min(max),
        _ => return None,
    };
    (want != count).then_some(want)
}

#[async_trait]
impl Lights for RealLights {
    async fn devices(&mut self, rescan: bool, scene: &Scene) -> Result<Vec<DeviceInfo>> {
        match (&self.cache, rescan) {
            (Some(c), false) => Ok(c.clone()),
            _ => self.discover(scene, true).await,
        }
    }

    async fn render(&mut self, scene: &Scene, t: f32, audio: &AudioLevels, overlay: &Overlay) -> Result<()> {
        if self.cache.is_none() {
            self.discover(scene, true).await?;
        }
        let result = self.render_inner(scene, t, audio, overlay).await;
        if result.is_err() {
            // OpenRGB may have restarted: reconnect (and re-size headers) on the next attempt.
            self.cache = None;
        }
        result
    }

    fn diagnostics(&self, scene: &Scene) -> Vec<Value> {
        let ago = |at: Option<Instant>| at.map(|a| a.elapsed().as_millis() as u64);
        self.cache
            .iter()
            .flatten()
            .map(|d| {
                let hidden = scene.is_hidden(&d.id);
                let Some(index) = d.openrgb_index else {
                    return json!({
                        "id": d.id, "name": d.name, "provider": d.provider, "index": null,
                        "hidden": hidden, "drive": "none", "mode": null, "leds": d.led_count,
                        "controller_leds": null, "last_write_ms_ago": null, "last_error": null, "reason": null,
                    });
                };
                let dr = self.driven.get(&d.id);
                let ctrl = self.openrgb.controller(index);
                let drive = dr.and_then(|x| x.drive);
                let drive_name = match (hidden, drive) {
                    (true, _) => "hidden",
                    (false, Some(Drive::PerLed { .. })) => "direct",
                    (false, Some(Drive::StaticColor { .. })) => "static",
                    (false, None) => "none",
                };
                let mode = drive
                    .zip(ctrl)
                    .and_then(|(x, c)| c.modes.get(x.mode() as usize))
                    .map(|m| m.name.clone());
                json!({
                    "id": d.id, "name": d.name, "provider": d.provider, "index": index,
                    "hidden": hidden, "drive": drive_name, "mode": mode, "leds": d.led_count,
                    "controller_leds": ctrl.map(|c| c.led_count),
                    "last_write_ms_ago": ago(dr.and_then(|x| x.last_write)),
                    "last_error": dr.and_then(|x| x.last_error.clone()),
                    "reason": dr.and_then(|x| x.reason.clone()),
                })
            })
            .collect()
    }
}

async fn handle_conn<S: AsyncRead + AsyncWrite>(
    stream: S,
    daemon: Arc<Mutex<Daemon<RealLights>>>,
) -> Result<()> {
    // Requests are small; a cap keeps a misbehaving client from making the daemon buffer forever.
    const MAX_REQUEST: u64 = 1 << 20;
    let (r, mut w) = tokio::io::split(stream);
    let mut reader = BufReader::new(r);
    let mut line = String::new();
    loop {
        line.clear();
        let n = (&mut reader).take(MAX_REQUEST).read_line(&mut line).await?;
        if n == 0 {
            break;
        }
        if !line.ends_with('\n') && n as u64 >= MAX_REQUEST {
            bail!("request longer than {MAX_REQUEST} bytes");
        }
        if line.trim().is_empty() {
            continue;
        }
        let (resp, quit) = {
            let mut d = daemon.lock().await;
            let resp = d.handle_line(&line).await;
            (resp, d.shutdown_requested)
        };
        w.write_all(resp.as_bytes()).await?;
        w.write_all(b"\n").await?;
        w.flush().await?;
        if quit {
            eprintln!("[lightsyncd] shutting down (asked by the app)");
            std::process::exit(0);
        }
    }
    Ok(())
}

#[cfg(windows)]
async fn serve(daemon: Arc<Mutex<Daemon<RealLights>>>) -> Result<()> {
    use anyhow::Context;
    use tokio::net::windows::named_pipe::ServerOptions;
    const PIPE: &str = r"\\.\pipe\lightsync";
    // Only this user (and SYSTEM) may connect, and only from this PC: the pipe can change every light
    // and read what the triggers watch.
    let sddl = pipe_sddl();
    let create = |first: bool| -> Result<tokio::net::windows::named_pipe::NamedPipeServer> {
        let mut o = ServerOptions::new();
        o.first_pipe_instance(first).reject_remote_clients(true);
        match &sddl {
            Some(s) => pipe_with_sddl(&o, PIPE, s),
            None => Ok(o.create(PIPE)?),
        }
    };
    let mut server = create(true).context("cannot create pipe (is lightsyncd already running?)")?;
    eprintln!("[lightsyncd] listening on {PIPE}");
    loop {
        server.connect().await?;
        let client = server;
        server = create(false)?;
        let d = daemon.clone();
        tokio::spawn(async move {
            if let Err(e) = handle_conn(client, d).await {
                eprintln!("[lightsyncd] client error: {e:#}");
            }
        });
    }
}

/// The pipe's access list: full access for SYSTEM and the user running lightsyncd, nobody else
/// (Windows' default would also let every account open it for reading). None if the user's SID
/// can't be read (the default then applies).
#[cfg(windows)]
fn pipe_sddl() -> Option<String> {
    use windows_sys::Win32::Foundation::{CloseHandle, LocalFree, HANDLE};
    use windows_sys::Win32::Security::Authorization::ConvertSidToStringSidW;
    use windows_sys::Win32::Security::{GetTokenInformation, TokenUser, TOKEN_QUERY, TOKEN_USER};
    use windows_sys::Win32::System::Threading::{GetCurrentProcess, OpenProcessToken};
    // SAFETY: plain Win32 calls with buffers sized as the API asks; every handle and allocation is freed.
    unsafe {
        let mut token: HANDLE = std::ptr::null_mut();
        if OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &mut token) == 0 {
            return None;
        }
        let mut len = 0u32;
        GetTokenInformation(token, TokenUser, std::ptr::null_mut(), 0, &mut len);
        let mut buf = vec![0u8; len as usize];
        let ok = len > 0 && GetTokenInformation(token, TokenUser, buf.as_mut_ptr().cast(), len, &mut len) != 0;
        CloseHandle(token);
        if !ok {
            return None;
        }
        let user = &*(buf.as_ptr() as *const TOKEN_USER);
        let mut s: windows_sys::core::PWSTR = std::ptr::null_mut();
        if ConvertSidToStringSidW(user.User.Sid, &mut s) == 0 || s.is_null() {
            return None;
        }
        let n = (0..).take_while(|&i| *s.add(i) != 0).count();
        let sid = String::from_utf16_lossy(std::slice::from_raw_parts(s, n));
        LocalFree(s.cast());
        Some(format!("D:P(A;;GA;;;SY)(A;;GA;;;{sid})"))
    }
}

/// Create a pipe instance with the access list `sddl`.
#[cfg(windows)]
fn pipe_with_sddl(
    o: &tokio::net::windows::named_pipe::ServerOptions,
    name: &str,
    sddl: &str,
) -> Result<tokio::net::windows::named_pipe::NamedPipeServer> {
    use windows_sys::Win32::Foundation::LocalFree;
    use windows_sys::Win32::Security::Authorization::{ConvertStringSecurityDescriptorToSecurityDescriptorW, SDDL_REVISION_1};
    use windows_sys::Win32::Security::{PSECURITY_DESCRIPTOR, SECURITY_ATTRIBUTES};
    let wide: Vec<u16> = sddl.encode_utf16().chain(Some(0)).collect();
    let mut sd: PSECURITY_DESCRIPTOR = std::ptr::null_mut();
    // SAFETY: `wide` is NUL-terminated; the descriptor is freed after the pipe is created (Windows
    // copies it into the pipe object).
    unsafe {
        if ConvertStringSecurityDescriptorToSecurityDescriptorW(wide.as_ptr(), SDDL_REVISION_1, &mut sd, std::ptr::null_mut()) == 0 {
            bail!("invalid pipe security descriptor");
        }
        let mut sa = SECURITY_ATTRIBUTES {
            nLength: std::mem::size_of::<SECURITY_ATTRIBUTES>() as u32,
            lpSecurityDescriptor: sd,
            bInheritHandle: 0,
        };
        let r = o.create_with_security_attributes_raw(name, (&mut sa as *mut SECURITY_ATTRIBUTES).cast());
        LocalFree(sd.cast());
        Ok(r?)
    }
}

#[cfg(unix)]
async fn serve(daemon: Arc<Mutex<Daemon<RealLights>>>) -> Result<()> {
    let path = std::env::var("LIGHTSYNC_SOCKET").unwrap_or_else(|_| "/tmp/lightsync.sock".into());
    let _ = std::fs::remove_file(&path);
    let listener = tokio::net::UnixListener::bind(&path)?;
    eprintln!("[lightsyncd] listening on {path}");
    loop {
        let (client, _) = listener.accept().await?;
        let d = daemon.clone();
        tokio::spawn(async move {
            if let Err(e) = handle_conn(client, d).await {
                eprintln!("[lightsyncd] client error: {e:#}");
            }
        });
    }
}

fn state_path() -> Option<std::path::PathBuf> {
    let base = ["APPDATA", "XDG_CONFIG_HOME"]
        .iter()
        .filter_map(std::env::var_os)
        .find(|v| !v.is_empty())?;
    Some(
        std::path::PathBuf::from(base)
            .join("LightSync")
            .join("state.json"),
    )
}

#[tokio::main]
async fn main() -> Result<()> {
    let daemon = Arc::new(Mutex::new(
        Daemon::new(RealLights::new(), state_path()).with_audio(Box::new(audio::Capture::new())),
    ));
    // Discover up front so the first UI request is fast; failure is fine (OpenRGB may start later).
    let mut guard = daemon.lock().await;
    let d = &mut *guard;
    if let Err(e) = d.devices(true).await {
        eprintln!("[lightsyncd] initial discovery failed: {e:#}");
    }
    drop(guard);
    // Render loop: animated effects every frame; unchanged frames only go to devices that need a keepalive.
    // It ticks at AUDIO_FPS while a sound reactive effect or a trigger effect is visible, FPS otherwise.
    let engine = daemon.clone();
    tokio::spawn(async move {
        let start = Instant::now();
        let mut tick = tokio::time::interval(Duration::from_micros(1_000_000 / AUDIO_FPS));
        tick.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Skip);
        let mut n: u64 = 0;
        loop {
            tick.tick().await;
            n += 1;
            let mut d = engine.lock().await;
            if d.wants_fast() || n.is_multiple_of(AUDIO_FPS / FPS) {
                d.tick(start.elapsed()).await;
            }
        }
    });
    serve(daemon).await
}

/// An OpenRGB device's LED names and, for zones with a grid (keyboards), each LED's column and row.
/// Grid values are LED indices within the zone; zones are laid out one after another in frame order.
fn led_map(c: &lightsync_openrgb::Controller) -> rpc::LedMap {
    let mut pos = vec![None; c.led_count];
    let mut start = 0usize;
    for z in &c.zones {
        let n = z.leds_count as usize;
        if let Some(m) = &z.matrix {
            for row in 0..m.height as usize {
                for col in 0..m.width as usize {
                    let v = m.map[row * m.width as usize + col];
                    if v == lightsync_openrgb::Matrix::NONE {
                        continue;
                    }
                    let i = start + v as usize;
                    if (v as usize) < n && i < pos.len() && pos[i].is_none() {
                        pos[i] = Some((col as f32, row as f32));
                    }
                }
            }
        }
        start += n;
    }
    rpc::LedMap { names: c.led_names.clone(), pos }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn unchanged_frames_only_go_to_keepalive_devices() {
        let red = vec![Color::new(255, 0, 0); 3];
        let blue = vec![Color::new(0, 0, 255); 3];
        assert!(should_send(None, &red, false));
        let fresh = (red.clone(), Instant::now());
        assert!(!should_send(Some(&fresh), &red, false));
        assert!(!should_send(Some(&fresh), &red, true), "keepalive waits for its interval");
        assert!(should_send(Some(&fresh), &blue, false));
        let old = (red.clone(), Instant::now() - KEEPALIVE - Duration::from_millis(10));
        assert!(should_send(Some(&old), &red, true));
        assert!(!should_send(Some(&old), &red, false));
        assert!(!needs_keepalive("Corsair iCUE Link System Hub"));
        assert!(needs_keepalive("Razer Leviathan V2 X"));
        assert!(!needs_keepalive("X870 GAMING X WIFI7"));
    }

    #[test]
    fn only_empty_or_fan_layout_zones_are_resized() {
        // Empty addressable header: gets the default size (capped by the zone's max).
        assert_eq!(zone_size(0, 1024, 0, None, 120), Some(120));
        assert_eq!(zone_size(0, 60, 0, None, 120), Some(60));
        // A header the user sized (in OpenRGB) without a fan layout is left alone.
        assert_eq!(zone_size(0, 1024, 37, None, 120), None);
        assert_eq!(zone_size(0, 1024, 37, Some(0), 120), None);
        // A fan layout sets the exact size, within the zone's limits.
        assert_eq!(zone_size(0, 1024, 37, Some(24), 120), Some(24));
        assert_eq!(zone_size(0, 1024, 24, Some(24), 120), None, "already right");
        assert_eq!(zone_size(0, 20, 0, Some(24), 120), Some(20));
        // Fixed zones never change.
        assert_eq!(zone_size(8, 8, 8, Some(24), 120), None);
        assert_eq!(zone_size(0, 0, 0, None, 120), None);
        assert_eq!(zone_size(0, 1024, 0, None, 0), None, "LIGHTSYNC_ARGB_LEDS=0 sizes nothing");
    }

    #[test]
    fn frame_rate_backs_off_while_openrgb_lags() {
        let mut l = RealLights::new();
        l.adapt(Duration::from_millis(5));
        assert_eq!(l.frame_gap, Duration::ZERO);
        l.adapt(Duration::from_millis(300));
        assert_eq!(l.frame_gap, Duration::from_millis(33));
        for _ in 0..10 {
            l.adapt(Duration::from_millis(300));
        }
        assert_eq!(l.frame_gap, MAX_FRAME_GAP);
        l.adapt(Duration::from_millis(60));
        assert_eq!(l.frame_gap, MAX_FRAME_GAP, "in between: unchanged");
        for _ in 0..10 {
            l.adapt(Duration::from_millis(5));
        }
        assert_eq!(l.frame_gap, Duration::ZERO);
    }

    #[test]
    fn static_devices_show_the_average() {
        assert_eq!(average(&[]), Color::BLACK);
        assert_eq!(average(&[Color::new(255, 0, 0), Color::new(0, 0, 255)]), Color::new(128, 0, 128));
        assert_eq!(average(&[Color::new(10, 20, 30); 7]), Color::new(10, 20, 30));
    }

    #[test]
    fn duplicate_names_get_numbered_ids() {
        assert_eq!(stable_ids(["Fan Hub", "RAM", "RAM"]), ["openrgb:Fan Hub", "openrgb:RAM", "openrgb:RAM#2"]);
    }
}
