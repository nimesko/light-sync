//! JSON-RPC 2.0 request handling and the render loop state, independent of the transport.
//!
//! Methods (one JSON object per line). `target` is "all" (default), a device / zone / fan id
//! (see scene.rs), or "group:<id>".
//! - `status` -> `{ effect, color, brightness, speed, connected, error }` (the "all" state)
//! - `devices` / `rescan` -> `[DeviceInfo]` with stable ids, zones, fans and `hidden: bool`
//! - `get_target { target }` -> the state that target currently shows
//! - `set_state { target?, effect?, color?, brightness?, speed? }` -> applied within one frame;
//!   effects: see effects.rs (snake_case names, e.g. static, breathing, rainbow_wave, heartbeat);
//!   sound reactive: sound_pulse, spectrum_bars, beat_flash (sound is captured only while one of
//!   them is visible; their `speed` is sensitivity, or how fast beat_flash fades)
//! - `groups` -> `[Group]`; `save_group { id?, name, members }` -> Group; `delete_group { id }`
//! - `set_layout { zone, fans, leds_per_fan }` -> split an addressable header into fans (fans 0 = remove)
//! - `set_color { color }`, `set_brightness { brightness }` -> shortcuts for `set_state` on "all"
//! - `get_targets { targets: [id] }` -> `{ id: { effect, color, brightness, speed, own, mixed, on } }`
//!   (what each target shows now; `mixed` = a group whose members differ)
//! - `reset { profiles? }` -> back to defaults: no overrides, groups, fan layouts or triggers
//!   (profiles are kept unless `profiles` is true; hidden devices stay hidden)
//! - `set_power { on }` -> all lights off (black) or back on; changing any state turns them on
//! - `preview { max? }` -> `[{ id, colors, zones: [{ id, colors }], hidden }]`: what each device
//!   shows right now, trigger effects included, as "rrggbb" colors (at most `max`, default 32,
//!   spread over the device / zone); hidden devices show the scene's colors for them
//! - `profiles` -> `[name]`; `save_profile { name }`, `load_profile { name }`, `delete_profile { name }`
//!   (a profile holds the lighting only: groups and fan layouts stay as they are)
//! - `audio_status` -> `{ active, device, level }`: whether sound is being captured (an audio
//!   effect or an enabled audio trigger is visible), the output device captured (null if none
//!   yet) and the current level 0..1
//! - `audio_spectrum` -> `{ active, device, level, bass, mid, treble, beat, bands: [24], peaks: [24] }`
//!   (levels 0..1; zeros while nothing is captured; `device` is the Windows output device captured)
//! - `hidden` -> `[id]`; `set_hidden { id, hidden }` -> `{ hidden: [id] }`: hidden devices are
//!   never written (no mode change, no frames, no triggers); their other software keeps them.
//!   They stay hidden across rescans and restarts; previews and states still work for them.
//! - `diagnostics` -> `[{ id, name, provider, index, hidden, drive, mode, leds, controller_leds,
//!   last_write_ms_ago, last_error, reason }]`: how each device is driven. `drive` is "direct"
//!   (per-LED frames), "static" (the device's Static mode re-sent with one color), "none" (cannot
//!   be driven: see `reason`), "hidden" or "chroma"; times and errors are null when unknown
//! - `triggers` -> `[Trigger]`; `save_trigger { trigger }` -> Trigger (id "t<n>" assigned when
//!   missing; an unknown id is an error); `delete_trigger { id }` -> true. A Trigger is
//!   `{ id, name, enabled, source, action, targets: [id] }` (targets: device / zone / fan ids or
//!   "group:<id>"; empty = every device that is not hidden), with
//!   `source`: `{kind:"key", keys:"any"|[vk 1..254]}`, `{kind:"mouse_click", button?:"left"|"right"|"middle"}`,
//!   `{kind:"mouse_move"}`, `{kind:"audio_beat"}`, `{kind:"audio_level", band:"level"|"bass"|"mid"|"treble", above:0..1}`;
//!   `action`: `{kind:"flash", color, duration_ms}`, `{kind:"pulse", duration_ms}`,
//!   `{kind:"ripple", color, duration_ms}`, `{kind:"fade_in", color, duration_ms}`, `{kind:"preset", name}`
//!   (duration_ms 50..10000, color "RRGGBB", preset = a saved profile). Each trigger fires at most
//!   once per 50 ms; at most 32 instances show at once, newest on top. Nothing fires while the
//!   lights are off.
//! - `leds { device }` -> `[{ index, name, zone, col, row }]`: a device's LEDs in frame order (keys by
//!   name, grid place when known). A trigger target can be one LED: `"<device id>@<index>"`.
//!   Triggers always paint over the device's own lighting (whatever effect it shows).
//! - `shutdown` -> true: save, then the service exits (the app closing entirely).
//! - `set_apps { apps: [name] }` -> true: which programs watched by `app_running` triggers run now.
//! - `move_trigger { id, to }` -> `[Trigger]`: move a trigger to position `to`. The list order is the
//!   priority: where triggers light the same LEDs, the one higher in the list is painted on top.
//! - `input_event { kind: "key"|"mouse_click"|"mouse_move", vk?, button? }` -> `{ fired }`: input
//!   seen by the UI (`button` defaults to "left"). Key codes are only matched, never logged or kept.
//! - `input_wanted` -> `{ keys, key_codes, mouse_click, mouse_move }`: which input an enabled trigger
//!   needs (the UI listens to nothing else; all false while the lights are off). `key_codes`: a key
//!   trigger is local, so every key press is sent with its code (to light that key).
//! - Local effects: flash, pulse, ripple and fade_in take an optional `radius` (0..30, keys on a
//!   keyboard's grid, else LEDs): the effect starts at the pressed key (by OpenRGB's key names) or
//!   at the mouse wheel / logo for mouse events, and reaches only that far.

use crate::audio::{AudioSource, NoAudio};
use crate::effects::{self, AudioLevels, Effect, LightState};
use crate::scene::{FanLayout, Group, Scene};
use crate::triggers::{self, Engine, Input, MouseButton, Overlay, Trigger};
use anyhow::{anyhow, bail, Result};
use async_trait::async_trait;
use lightsync_core::{Color, DeviceKind};
use serde::Serialize;
use serde_json::{json, Value};
use std::path::PathBuf;
use std::time::Duration;

#[derive(Debug, Clone, Serialize, PartialEq)]
pub struct FanInfo {
    pub id: String,
    pub name: String,
}

#[derive(Debug, Clone, Serialize, PartialEq)]
pub struct ZoneInfo {
    pub id: String,
    pub index: u32,
    pub name: String,
    pub leds: u32,
    /// Addressable header whose LED count can be set (so it can be split into fans).
    pub resizable: bool,
    pub max_leds: u32,
    /// Only for zones with a fan layout.
    pub fans: Vec<FanInfo>,
}

#[derive(Debug, Clone, Serialize, PartialEq)]
pub struct DeviceInfo {
    pub id: String,
    pub name: String,
    pub kind: DeviceKind,
    pub provider: String,
    pub led_count: usize,
    /// OpenRGB controller index, if the device comes from OpenRGB.
    pub openrgb_index: Option<u32>,
    pub zones: Vec<ZoneInfo>,
    /// The user hid it: LightSync does not write to it (filled in by the daemon from the scene).
    pub hidden: bool,
    /// Where each LED is and what it is called (for effects that start at a pressed key).
    #[serde(skip)]
    pub led_map: LedMap,
}

/// Names and grid positions of a device's LEDs, in frame order (both may be empty).
#[derive(Debug, Clone, Default, PartialEq)]
pub struct LedMap {
    /// "Key: A", "Scroll Wheel"... (OpenRGB's names).
    pub names: Vec<String>,
    /// (column, row) in key units for LEDs on a grid (keyboards); None for the others.
    pub pos: Vec<Option<(f32, f32)>>,
}

/// The hardware side of the daemon. The real one talks to OpenRGB/Chroma; tests use a fake.
#[async_trait]
pub trait Lights: Send {
    /// List devices; `rescan` reconnects and re-applies zone sizes from the scene's fan layouts.
    async fn devices(&mut self, rescan: bool, scene: &Scene) -> Result<Vec<DeviceInfo>>;
    /// Render the scene at time `t` (seconds), with the current sound `audio` and the triggers'
    /// `overlay` on top, and send the frame to every device that is not hidden.
    async fn render(&mut self, scene: &Scene, t: f32, audio: &AudioLevels, overlay: &Overlay) -> Result<()>;
    /// How each device is driven (see the `diagnostics` method).
    fn diagnostics(&self, _scene: &Scene) -> Vec<Value> {
        vec![]
    }
}

/// Static scenes are rendered this often. The hardware side decides per device whether an
/// unchanged frame is actually re-sent (Razer needs it; others flash on every write).
pub const KEEPALIVE: Duration = Duration::from_secs(1);
/// After a failed frame, wait this long before trying again (each try may reconnect to OpenRGB).
pub const RETRY: Duration = Duration::from_secs(3);

pub struct Daemon<L> {
    pub lights: L,
    pub scene: Scene,
    store: Option<PathBuf>,
    dirty: bool,
    last_write: Option<Duration>,
    retry_at: Option<Duration>,
    last_error: Option<String>,
    /// Time of the latest render-loop step, so previews show the same moment as the lights.
    now: Duration,
    audio: Box<dyn AudioSource>,
    /// Sound levels of the latest render-loop step (also shown by previews).
    levels: AudioLevels,
    /// What the triggers are showing now.
    triggers: Engine,
    /// The last frame had a trigger overlay (one more frame is drawn once it is gone).
    overlay_shown: bool,
    /// When the scene was last written to `store`, and whether a change still waits to be written
    /// (dragging a slider changes it many times a second; the file is written at most once a second).
    last_save: Option<std::time::Instant>,
    save_pending: bool,
    /// `shutdown` was asked for: the server exits after answering.
    pub shutdown_requested: bool,
}

/// The scene file is written at most this often.
const SAVE_GAP: Duration = Duration::from_secs(1);

/// Whether anything visible uses a sound reactive effect, or an enabled trigger listens to sound
/// (only then is sound captured).
pub fn uses_audio(scene: &Scene) -> bool {
    !scene.off
        && (scene.default.effect.is_audio()
            || scene.overrides.values().any(|s| s.effect.is_audio())
            || triggers::uses_audio(scene))
}

/// [`device_frame`] with the triggers' overlay painted on top.
pub fn device_frame_with(scene: &Scene, d: &DeviceInfo, t: f32, audio: &AudioLevels, overlay: &Overlay) -> Vec<Color> {
    let mut frame = device_frame(scene, d, t, audio);
    overlay.apply(d, scene, &mut frame);
    frame
}

/// One frame for an OpenRGB device: zones in order, each LED resolved through the scene.
pub fn device_frame(scene: &Scene, d: &DeviceInfo, t: f32, audio: &AudioLevels) -> Vec<Color> {
    let n = d.led_count;
    if scene.off {
        return vec![Color::BLACK; n];
    }
    let mut frame = Vec::with_capacity(n);
    for z in &d.zones {
        let len = z.leds as usize;
        let offset = frame.len();
        for j in 0..len.min(n.saturating_sub(offset)) {
            let p = scene.pick(&d.id, &z.id, j, len, offset, n);
            frame.push(effects::render_with(p.state, t, p.pos, p.len, audio));
        }
    }
    // LEDs outside any zone (should not happen) follow the device.
    while frame.len() < n {
        let i = frame.len();
        frame.push(effects::render_with(scene.effective(&d.id), t, i, n, audio));
    }
    frame
}

/// Up to `max` colors spread evenly over `colors` (all of them if there are few).
fn sample(colors: &[Color], max: usize) -> Vec<String> {
    if colors.len() <= max {
        return colors.iter().map(|c| hex(*c)).collect();
    }
    (0..max)
        .map(|k| hex(colors[((2 * k + 1) * colors.len()) / (2 * max)]))
        .collect()
}

fn hex(c: Color) -> String {
    format!("{:02x}{:02x}{:02x}", c.r, c.g, c.b)
}

fn state_json(s: &LightState) -> Value {
    json!({"effect": s.effect, "color": s.color, "brightness": s.brightness, "speed": s.speed})
}

impl<L: Lights> Daemon<L> {
    /// `store`: where the scene is saved between runs (None = don't persist).
    pub fn new(lights: L, store: Option<PathBuf>) -> Self {
        let scene = store
            .as_ref()
            .and_then(|p| std::fs::read_to_string(p).ok())
            .and_then(|s| Scene::from_json(&s))
            .unwrap_or_default();
        Self {
            lights,
            scene,
            store,
            dirty: true,
            last_write: None,
            retry_at: None,
            last_error: None,
            now: Duration::ZERO,
            audio: Box::new(NoAudio),
            levels: AudioLevels::SILENT,
            triggers: Engine::default(),
            overlay_shown: false,
            last_save: None,
            save_pending: false,
            shutdown_requested: false,
        }
    }

    /// Use `audio` for the sound reactive effects (the default hears nothing).
    pub fn with_audio(mut self, audio: Box<dyn AudioSource>) -> Self {
        self.audio = audio;
        self
    }

    /// Whether the render loop should run at the faster audio frame rate.
    pub fn uses_audio(&self) -> bool {
        uses_audio(&self.scene)
    }

    /// Whether the render loop should run at the faster frame rate (sound, or triggers showing).
    pub fn wants_fast(&self) -> bool {
        self.uses_audio() || self.triggers.is_active()
    }

    /// List devices, each marked hidden or not.
    pub async fn devices(&mut self, rescan: bool) -> Result<Vec<DeviceInfo>> {
        let mut devs = self.devices_inner(rescan).await?;
        for d in &mut devs {
            d.hidden = self.scene.is_hidden(&d.id);
        }
        Ok(devs)
    }

    /// List devices. Settings saved under the old index-based zone ids ("<device>/<index>") move to
    /// the name-based ones, so they follow the right zone when OpenRGB reorders zones.
    async fn devices_inner(&mut self, rescan: bool) -> Result<Vec<DeviceInfo>> {
        let devs = self.lights.devices(rescan, &self.scene).await?;
        let layouts = self.scene.layouts.clone();
        let mut moved = false;
        for d in &devs {
            for z in &d.zones {
                let old = format!("{}/{}", d.id, z.index);
                if old != z.id {
                    moved |= self.scene.rename_target(&old, &z.id);
                }
            }
        }
        if !moved {
            return Ok(devs);
        }
        self.changed();
        if self.scene.layouts != layouts {
            // Fan layouts moved: size the headers and list their fans again.
            return self.lights.devices(true, &self.scene).await;
        }
        Ok(devs)
    }

    /// One render-loop step at `now` (time since the daemon started).
    pub async fn tick(&mut self, now: Duration) {
        self.now = now;
        if self.save_pending && self.last_save.is_none_or(|l| l.elapsed() >= SAVE_GAP) {
            self.save();
        }
        // Capture sound only while an audio effect is visible.
        let audio = self.uses_audio();
        self.audio.set_active(audio);
        self.levels = if audio { self.audio.levels() } else { AudioLevels::SILENT };
        if audio {
            let fired = self.triggers.audio(&self.scene, &self.levels, now);
            self.run_preset(fired.preset);
        }
        let overlay = self.triggers.overlay(&self.scene, now, &self.levels);
        if self.retry_at.is_some_and(|r| now < r) && !self.dirty {
            return;
        }
        let due = self.dirty
            || self.scene.is_animated()
            || !overlay.is_empty()
            || self.overlay_shown
            || self
                .last_write
                .is_none_or(|w| now.saturating_sub(w) >= KEEPALIVE);
        if !due {
            return;
        }
        self.overlay_shown = !overlay.is_empty();
        match self
            .lights
            .render(&self.scene, now.as_secs_f32(), &self.levels, &overlay)
            .await
        {
            Ok(()) => {
                self.dirty = false;
                self.last_write = Some(now);
                self.retry_at = None;
                self.last_error = None;
            }
            Err(e) => {
                self.dirty = false;
                self.retry_at = Some(now + RETRY);
                self.last_error = Some(format!("{e:#}"));
            }
        }
    }

    fn changed(&mut self) {
        self.dirty = true;
        if self.store.is_none() {
            return;
        }
        if self.last_save.is_some_and(|l| l.elapsed() < SAVE_GAP) {
            self.save_pending = true; // written by the render loop shortly
        } else {
            self.save();
        }
    }

    /// Write a change that is still waiting (on exit, or for tests).
    pub fn flush(&mut self) {
        if self.save_pending {
            self.save();
        }
    }

    /// Write the scene to `store` (the user's profile: %APPDATA%\LightSync\state.json). Written to a
    /// temporary file first and then swapped in, so a crash or power cut mid-write never leaves a
    /// half-written file (which would lose presets, triggers and settings).
    fn save(&mut self) {
        self.save_pending = false;
        self.last_save = Some(std::time::Instant::now());
        let Some(p) = &self.store else {
            return;
        };
        if let Some(dir) = p.parent() {
            let _ = std::fs::create_dir_all(dir);
        }
        let Ok(s) = serde_json::to_string_pretty(&self.scene) else {
            return;
        };
        let tmp = p.with_extension("json.tmp");
        if std::fs::write(&tmp, s).is_ok() && std::fs::rename(&tmp, p).is_err() {
            let _ = std::fs::remove_file(&tmp);
        }
    }

    /// Load the profile a `preset` trigger asked for (a profile deleted since is skipped).
    fn run_preset(&mut self, name: Option<String>) {
        let Some(l) = name.and_then(|n| self.scene.profiles.get(&n).cloned()) else {
            return;
        };
        let before = self.scene.clone();
        self.scene.load(&l);
        if self.scene != before {
            self.changed();
        }
    }

    fn input_event(&mut self, p: &Value) -> Result<Value> {
        // Never echo or keep the key code: it only selects which key triggers fire.
        let ev = match p.get("kind").and_then(Value::as_str) {
            Some("key") => Input::Key(match p.get("vk") {
                None | Some(Value::Null) => None,
                Some(v) => Some(
                    v.as_u64()
                        .and_then(|k| u8::try_from(k).ok())
                        .filter(|k| (1..=254).contains(k))
                        .ok_or_else(|| anyhow!("'vk' must be an integer 1..254"))?,
                ),
            }),
            Some("mouse_click") => Input::Click(match p.get("button") {
                None | Some(Value::Null) => MouseButton::Left,
                Some(v) => serde_json::from_value(v.clone())
                    .map_err(|_| anyhow!("'button' must be \"left\", \"right\" or \"middle\""))?,
            }),
            Some("mouse_move") => Input::Move,
            _ => bail!("'kind' must be \"key\", \"mouse_click\" or \"mouse_move\""),
        };
        let fired = self.triggers.input(&self.scene, ev, self.now);
        let count = fired.count;
        self.run_preset(fired.preset);
        Ok(json!({ "fired": count }))
    }

    fn save_trigger(&mut self, p: &Value) -> Result<Value> {
        let raw = p.get("trigger").ok_or_else(|| anyhow!("missing 'trigger'"))?;
        let t: Trigger = serde_json::from_value(raw.clone()).map_err(|e| anyhow!("invalid trigger: {e}"))?;
        let mut t = triggers::validate(t, &self.scene)?;
        let id = t.id.trim().to_string();
        if id.is_empty() {
            if self.scene.triggers.len() >= triggers::MAX_TRIGGERS {
                bail!("at most {} triggers", triggers::MAX_TRIGGERS);
            }
            t.id = triggers::next_id(&self.scene);
            self.scene.triggers.push(t.clone());
        } else {
            t.id = id;
            let slot = self
                .scene
                .triggers
                .iter_mut()
                .find(|x| x.id == t.id)
                .ok_or_else(|| anyhow!("unknown trigger '{}'", t.id))?;
            *slot = t.clone();
            // Edited: what it was showing may no longer fit.
            self.triggers.forget(&t.id);
        }
        self.changed();
        Ok(serde_json::to_value(t)?)
    }

    fn diagnostics(&self) -> Value {
        Value::Array(self.lights.diagnostics(&self.scene))
    }

    /// Handle one request line and return the response line (no trailing newline).
    pub async fn handle_line(&mut self, line: &str) -> String {
        let req: Value = match serde_json::from_str(line) {
            Ok(v) => v,
            Err(e) => return json!({"jsonrpc": "2.0", "id": null, "error": {"code": -32700, "message": format!("parse error: {e}")}}).to_string(),
        };
        let id = req.get("id").cloned().unwrap_or(Value::Null);
        let method = req.get("method").and_then(Value::as_str).unwrap_or("");
        let params = req.get("params").cloned().unwrap_or(Value::Null);
        match self.call(method, &params).await {
            Ok(result) => json!({"jsonrpc": "2.0", "id": id, "result": result}).to_string(),
            Err(e) => json!({"jsonrpc": "2.0", "id": id, "error": {"code": -32000, "message": format!("{e:#}")}}).to_string(),
        }
    }

    fn status(&self) -> Value {
        let mut v = state_json(&self.scene.default);
        v["connected"] = json!(self.last_write.is_some() && self.last_error.is_none());
        v["on"] = json!(!self.scene.off);
        v["error"] = json!(self.last_error);
        v
    }

    fn target_of(p: &Value) -> Result<String> {
        match p.get("target") {
            None | Some(Value::Null) => Ok("all".into()),
            Some(Value::String(s)) if !s.trim().is_empty() => Ok(s.trim().to_string()),
            _ => bail!("'target' must be \"all\", a device/zone/fan id or \"group:<id>\""),
        }
    }

    /// The state a target shows now; a group shows its first member's state.
    fn effective(&self, target: &str) -> LightState {
        match self.scene.expand(target).first() {
            Some(first) if target != "all" => self.scene.effective(first).clone(),
            _ => self.scene.default.clone(),
        }
    }

    /// Validate every field first, then apply to every member of the target.
    fn set_state(&mut self, p: &Value) -> Result<Value> {
        let target = Self::target_of(p)?;
        if target.starts_with("group:") && self.scene.group(&target[6..]).is_none() {
            bail!("unknown group '{}'", &target[6..]);
        }
        let mut next = self.effective(&target);
        if let Some(v) = p.get("effect") {
            let name = v
                .as_str()
                .ok_or_else(|| anyhow!("'effect' must be a string"))?;
            next.effect = Effect::parse(name).ok_or_else(|| anyhow!("unknown effect '{name}'"))?;
        }
        if let Some(v) = p.get("color") {
            let s = v
                .as_str()
                .ok_or_else(|| anyhow!("'color' must be \"RRGGBB\""))?;
            next.color = hex(s.parse::<Color>()?);
        }
        if let Some(v) = p.get("brightness") {
            next.brightness = percent(v, "brightness", 0)?;
        }
        if let Some(v) = p.get("speed") {
            next.speed = percent(v, "speed", 1)?;
        }
        let before = self.scene.clone();
        self.scene.off = false;
        if target == "all" {
            self.scene.apply("all", next.clone());
        } else {
            for m in self.scene.expand(&target) {
                self.scene.apply(&m, next.clone());
            }
        }
        if self.scene != before {
            self.changed();
        }
        Ok(state_json(&next))
    }

    async fn call(&mut self, method: &str, p: &Value) -> Result<Value> {
        match method {
            "status" => Ok(self.status()),
            "devices" => Ok(serde_json::to_value(self.devices(false).await?)?),
            "rescan" => {
                let devs = self.devices(true).await?;
                self.dirty = true;
                Ok(serde_json::to_value(devs)?)
            }
            "preview" => {
                let max = p
                    .get("max")
                    .and_then(Value::as_u64)
                    .unwrap_or(32)
                    .clamp(1, 256) as usize;
                let t = self.now.as_secs_f32();
                let devs = self.devices(false).await?;
                let overlay = self.triggers.overlay(&self.scene, self.now, &self.levels);
                let out: Vec<Value> = devs
                    .iter()
                    .map(|d| {
                        let mut frame = device_frame(&self.scene, d, t, &self.levels);
                        if frame.is_empty() {
                            // Devices without per-LED control (Razer Chroma) show one color.
                            frame.push(if self.scene.off {
                                Color::BLACK
                            } else {
                                let s = self.scene.effective(&d.id);
                                effects::render_with(s, t, 0, 1, &self.levels)
                            });
                        }
                        overlay.apply(d, &self.scene, &mut frame);
                        let mut offset = 0;
                        let zones: Vec<Value> = d
                            .zones
                            .iter()
                            .map(|z| {
                                let end = (offset + z.leds as usize).min(frame.len());
                                let colors = sample(&frame[offset.min(end)..end], max);
                                offset = end;
                                json!({ "id": z.id, "colors": colors })
                            })
                            .collect();
                        json!({ "id": d.id, "colors": sample(&frame, max), "zones": zones, "hidden": d.hidden })
                    })
                    .collect();
                Ok(Value::Array(out))
            }
            "get_target" => {
                let target = Self::target_of(p)?;
                let mut v = state_json(&self.effective(&target));
                v["target"] = json!(target);
                v["own"] = json!(target == "all" || self.scene.overrides.contains_key(&target));
                Ok(v)
            }
            "get_targets" => {
                let ids = p
                    .get("targets")
                    .and_then(Value::as_array)
                    .ok_or_else(|| anyhow!("'targets' must be a list of ids"))?;
                let mut out = serde_json::Map::new();
                for id in ids.iter().filter_map(Value::as_str) {
                    let mut v = state_json(&self.effective(id));
                    v["own"] = json!(id == "all" || self.scene.overrides.contains_key(id));
                    let members = self.scene.expand(id);
                    v["mixed"] = json!(
                        id != "all"
                            && members
                                .iter()
                                .any(|m| self.scene.effective(m) != self.scene.effective(&members[0]))
                    );
                    v["on"] = json!(!self.scene.off);
                    out.insert(id.to_string(), v);
                }
                Ok(Value::Object(out))
            }
            "set_state" => self.set_state(p),
            "set_color" | "set_brightness" => {
                let key = if method == "set_color" {
                    "color"
                } else {
                    "brightness"
                };
                if p.get(key).is_none() {
                    bail!("missing '{key}'");
                }
                self.set_state(p)
            }
            "audio_status" => {
                let s = self.audio.status();
                let level = if s.active { self.audio.levels().level } else { 0.0 };
                Ok(json!({ "active": s.active, "device": s.device, "level": level }))
            }
            "audio_spectrum" => {
                let s = self.audio.status();
                let active = s.active;
                let l = if active { self.audio.levels() } else { AudioLevels::SILENT };
                Ok(json!({
                    "active": active, "device": s.device, "level": l.level, "bass": l.bass, "mid": l.mid,
                    "treble": l.treble, "beat": l.beat, "bands": l.bands.to_vec(), "peaks": l.peaks.to_vec(),
                    "tempo_bpm": (l.tempo * 60.0).round(), "mood": l.mood, "energy": l.energy,
                }))
            }
            "hidden" => Ok(json!(self.scene.hidden)),
            "set_hidden" => {
                let id = p
                    .get("id")
                    .and_then(Value::as_str)
                    .map(str::trim)
                    .filter(|i| !i.is_empty() && *i != "all" && !i.starts_with("group:"))
                    .ok_or_else(|| anyhow!("'id' must be a device id"))?
                    .to_string();
                let hide = p
                    .get("hidden")
                    .and_then(Value::as_bool)
                    .ok_or_else(|| anyhow!("'hidden' must be true or false"))?;
                let changed = if hide {
                    self.scene.hidden.insert(id)
                } else {
                    self.scene.hidden.remove(&id)
                };
                if changed {
                    self.changed();
                }
                Ok(json!({ "hidden": self.scene.hidden }))
            }
            "diagnostics" => Ok(self.diagnostics()),
            "triggers" => Ok(serde_json::to_value(&self.scene.triggers)?),
            "save_trigger" => self.save_trigger(p),
            "delete_trigger" => {
                let id = p
                    .get("id")
                    .and_then(Value::as_str)
                    .ok_or_else(|| anyhow!("missing 'id'"))?
                    .to_string();
                let before = self.scene.triggers.len();
                self.scene.triggers.retain(|t| t.id != id);
                if self.scene.triggers.len() == before {
                    bail!("unknown trigger '{id}'");
                }
                self.triggers.forget(&id);
                self.changed();
                Ok(json!(true))
            }
            "input_event" => self.input_event(p),
            "shutdown" => {
                // LightSync is closing entirely: save what is pending; the server then exits (the lights
                // keep their last colors).
                self.flush();
                self.shutdown_requested = true;
                Ok(json!(true))
            }
            "leds" => {
                // A device's LEDs, for picking single LEDs as trigger targets ("<device>@<index>").
                let id = p.get("device").and_then(Value::as_str).ok_or_else(|| anyhow!("missing 'device'"))?;
                let devs = self.devices(false).await?;
                let d = devs.iter().find(|d| d.id == id).ok_or_else(|| anyhow!("unknown device '{id}'"))?;
                let mut out = Vec::with_capacity(d.led_count);
                let mut i = 0usize;
                for z in &d.zones {
                    for k in 0..z.leds as usize {
                        let name = d
                            .led_map
                            .names
                            .get(i)
                            .filter(|n| !n.is_empty())
                            .map(|n| n.strip_prefix("Key: ").unwrap_or(n).to_string())
                            .unwrap_or_else(|| format!("{} {}", z.name, k + 1));
                        let pos = d.led_map.pos.get(i).copied().flatten();
                        out.push(json!({ "index": i, "name": name, "zone": z.id, "col": pos.map(|p| p.0), "row": pos.map(|p| p.1) }));
                        i += 1;
                    }
                }
                Ok(Value::Array(out))
            }
            "set_apps" => {
                // Which watched programs run (the UI only reports those `input_wanted.apps` lists).
                let apps: Vec<String> = p
                    .get("apps")
                    .and_then(Value::as_array)
                    .ok_or_else(|| anyhow!("missing 'apps'"))?
                    .iter()
                    .filter_map(Value::as_str)
                    .map(String::from)
                    .collect();
                if self.triggers.set_apps(apps) {
                    self.dirty = true;
                }
                Ok(json!(true))
            }
            "move_trigger" => {
                // Priority: the list order; the first trigger wins where several light the same LEDs.
                let id = p.get("id").and_then(Value::as_str).ok_or_else(|| anyhow!("missing 'id'"))?;
                let to = p.get("to").and_then(Value::as_u64).ok_or_else(|| anyhow!("missing 'to'"))? as usize;
                let from = self.scene.triggers.iter().position(|t| t.id == id).ok_or_else(|| anyhow!("unknown trigger '{id}'"))?;
                let t = self.scene.triggers.remove(from);
                let to = to.min(self.scene.triggers.len());
                self.scene.triggers.insert(to, t);
                self.changed();
                Ok(serde_json::to_value(&self.scene.triggers)?)
            }
            "input_wanted" => Ok(serde_json::to_value(triggers::input_wanted(&self.scene))?),
            "groups" => Ok(serde_json::to_value(&self.scene.groups)?),
            "save_group" => {
                let name = p
                    .get("name")
                    .and_then(Value::as_str)
                    .map(str::trim)
                    .filter(|n| !n.is_empty())
                    .ok_or_else(|| anyhow!("a group needs a 'name'"))?;
                let members: Vec<String> = p
                    .get("members")
                    .and_then(Value::as_array)
                    .ok_or_else(|| anyhow!("'members' must be a list of ids"))?
                    .iter()
                    .map(|m| {
                        m.as_str()
                            .map(str::to_string)
                            .ok_or_else(|| anyhow!("member ids must be strings"))
                    })
                    .collect::<Result<_>>()?;
                if members
                    .iter()
                    .any(|m| m == "all" || m.starts_with("group:"))
                {
                    bail!("a group can only hold devices, zones and fans");
                }
                let group = match p.get("id").and_then(Value::as_str) {
                    Some(id) => {
                        let g = self
                            .scene
                            .groups
                            .iter_mut()
                            .find(|g| g.id == id)
                            .ok_or_else(|| anyhow!("unknown group '{id}'"))?;
                        g.name = name.into();
                        g.members = members;
                        g.clone()
                    }
                    None => {
                        let g = Group {
                            id: self.scene.next_group_id(),
                            name: name.into(),
                            members,
                        };
                        self.scene.groups.push(g.clone());
                        g
                    }
                };
                self.changed();
                Ok(serde_json::to_value(group)?)
            }
            "delete_group" => {
                let id = p
                    .get("id")
                    .and_then(Value::as_str)
                    .ok_or_else(|| anyhow!("missing 'id'"))?;
                let before = self.scene.groups.len();
                self.scene.groups.retain(|g| g.id != id);
                if self.scene.groups.len() == before {
                    bail!("unknown group '{id}'");
                }
                self.changed();
                Ok(json!(true))
            }
            "set_layout" => {
                let zone = p
                    .get("zone")
                    .and_then(Value::as_str)
                    .filter(|z| z.matches('/').count() == 1)
                    .ok_or_else(|| {
                        anyhow!("'zone' must be a zone id like \"openrgb:Board/ARGB_V2_1\"")
                    })?;
                let n = |k: &str| {
                    p.get(k)
                        .and_then(Value::as_u64)
                        .filter(|v| *v <= 1024)
                        .map(|v| v as u32)
                        .ok_or_else(|| anyhow!("'{k}' must be an integer 0..1024"))
                };
                let (fans, per) = (n("fans")?, n("leds_per_fan")?);
                if fans == 0 || per == 0 {
                    self.scene.layouts.remove(zone);
                } else {
                    self.scene.layouts.insert(
                        zone.to_string(),
                        FanLayout {
                            fans,
                            leds_per_fan: per,
                        },
                    );
                }
                self.changed();
                // Resize the header now so the new fans light up.
                Ok(serde_json::to_value(self.devices(true).await?)?)
            }
            "set_power" => {
                let on = p
                    .get("on")
                    .and_then(Value::as_bool)
                    .ok_or_else(|| anyhow!("'on' must be true or false"))?;
                if self.scene.off == on {
                    self.scene.off = !on;
                    self.triggers.clear();
                    self.changed();
                }
                Ok(json!({ "on": on }))
            }
            "reset" => {
                // A fresh start: default lighting, no per-device settings, groups, fan layouts or
                // triggers. Hidden devices stay hidden (their own software keeps driving them).
                let keep = if p.get("profiles").and_then(Value::as_bool).unwrap_or(false) {
                    Default::default()
                } else {
                    std::mem::take(&mut self.scene.profiles)
                };
                self.scene = Scene {
                    profiles: keep,
                    hidden: std::mem::take(&mut self.scene.hidden),
                    ..Scene::default()
                };
                self.triggers.clear();
                self.changed();
                Ok(json!({ "profiles": self.scene.profiles.len() }))
            }
            "profiles" => Ok(json!(self.scene.profiles.keys().collect::<Vec<_>>())),
            "save_profile" | "load_profile" | "delete_profile" => {
                let name = p
                    .get("name")
                    .and_then(Value::as_str)
                    .map(str::trim)
                    .filter(|n| !n.is_empty())
                    .ok_or_else(|| anyhow!("a profile needs a 'name'"))?
                    .to_string();
                match method {
                    "save_profile" => {
                        let l = self.scene.lighting();
                        self.scene.profiles.insert(name, l);
                    }
                    "load_profile" => {
                        let l = self
                            .scene
                            .profiles
                            .get(&name)
                            .cloned()
                            .ok_or_else(|| anyhow!("unknown profile '{name}'"))?;
                        self.scene.load(&l);
                    }
                    _ => {
                        if self.scene.profiles.remove(&name).is_none() {
                            bail!("unknown profile '{name}'");
                        }
                    }
                }
                self.changed();
                Ok(json!(self.scene.profiles.keys().collect::<Vec<_>>()))
            }
            "" => Err(anyhow!("missing 'method'")),
            m => Err(anyhow!("unknown method '{m}'")),
        }
    }
}

fn percent(v: &Value, name: &str, min: u64) -> Result<u8> {
    match v.as_u64() {
        Some(b) if (min..=100).contains(&b) => Ok(b as u8),
        _ => Err(anyhow!("'{name}' must be an integer {min}..100")),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[derive(Default)]
    struct Fake {
        frames: Vec<Scene>,
        heard: Vec<AudioLevels>,
        /// What the board was sent each frame (None: not written, it is hidden).
        board: Vec<Option<Vec<Color>>>,
        fail: bool,
        rescans: usize,
    }

    fn board() -> DeviceInfo {
        DeviceInfo {
            id: "openrgb:Board".into(),
            name: "Board".into(),
            kind: DeviceKind::Motherboard,
            provider: "openrgb".into(),
            led_count: 3,
            openrgb_index: Some(0),
            zones: vec![ZoneInfo {
                id: "openrgb:Board/ARGB_V2_1".into(),
                index: 0,
                name: "ARGB_V2_1".into(),
                leds: 3,
                resizable: true,
                max_leds: 1024,
                fans: vec![],
            }],
            hidden: false,
            led_map: Default::default(),
        }
    }

    #[async_trait]
    impl Lights for Fake {
        async fn devices(&mut self, rescan: bool, _scene: &Scene) -> Result<Vec<DeviceInfo>> {
            if rescan {
                self.rescans += 1;
            }
            Ok(vec![board()])
        }
        async fn render(&mut self, s: &Scene, t: f32, audio: &AudioLevels, overlay: &Overlay) -> Result<()> {
            if self.fail {
                anyhow::bail!("OpenRGB server not reachable");
            }
            self.frames.push(s.clone());
            self.heard.push(*audio);
            let b = board();
            self.board.push((!s.is_hidden(&b.id)).then(|| device_frame_with(s, &b, t, audio, overlay)));
            Ok(())
        }
        fn diagnostics(&self, scene: &Scene) -> Vec<Value> {
            vec![json!({ "id": "openrgb:Board", "hidden": scene.is_hidden("openrgb:Board"), "drive": "direct" })]
        }
    }

    /// Synthetic sound; `active` is shared so tests can see what the daemon asked for.
    struct FakeAudio {
        active: std::sync::Arc<std::sync::atomic::AtomicBool>,
        levels: AudioLevels,
    }

    impl AudioSource for FakeAudio {
        fn set_active(&mut self, on: bool) {
            self.active.store(on, std::sync::atomic::Ordering::SeqCst);
        }
        fn levels(&self) -> AudioLevels {
            self.levels
        }
        fn status(&self) -> crate::audio::AudioStatus {
            let active = self.active.load(std::sync::atomic::Ordering::SeqCst);
            crate::audio::AudioStatus { active, device: active.then(|| "Speakers".into()) }
        }
    }

    #[tokio::test]
    async fn audio_is_captured_only_while_an_audio_effect_is_visible() {
        let active = std::sync::Arc::new(std::sync::atomic::AtomicBool::new(false));
        let loud = AudioLevels { level: 1.0, bass: 1.0, mid: 1.0, treble: 1.0, beat: true, beat_age: 0.0, ..AudioLevels::SILENT };
        let audio = FakeAudio { active: active.clone(), levels: loud };
        let mut d = Daemon::new(Fake::default(), None).with_audio(Box::new(audio));
        let on = || active.load(std::sync::atomic::Ordering::SeqCst);
        // Off by default: a fresh daemon, an old state file and a reset never capture sound.
        assert!(!LightState::default().effect.is_audio() && !uses_audio(&Scene::default()));
        assert!(!uses_audio(&Scene::from_json(r#"{"effect":"spectrum","color":"ff0000","brightness":50,"speed":50}"#).unwrap()));
        for i in 0..3 {
            d.tick(ms(i * 10)).await;
        }
        assert!(!on() && !d.uses_audio());
        call(&mut d, r#"{"id":0,"method":"reset"}"#).await;
        d.tick(ms(0)).await;
        assert!(!on() && !d.uses_audio());
        assert_eq!(d.lights.heard[0], AudioLevels::SILENT);
        let s = call(&mut d, r#"{"id":1,"method":"audio_status"}"#).await;
        assert_eq!(s["result"], json!({"active": false, "device": null, "level": 0.0}));
        // An audio effect on one zone turns capture on; the frame and preview get the levels.
        call(&mut d, r#"{"id":2,"method":"set_state","params":{"target":"openrgb:Board/ARGB_V2_1","effect":"sound_pulse","color":"00ff00"}}"#).await;
        d.tick(ms(20)).await;
        assert!(on() && d.uses_audio());
        assert_eq!(*d.lights.heard.last().unwrap(), loud);
        let r = call(&mut d, r#"{"id":3,"method":"preview"}"#).await;
        assert_eq!(r["result"][0]["colors"][0], json!("00ff00"));
        let s = call(&mut d, r#"{"id":4,"method":"audio_status"}"#).await;
        assert_eq!(s["result"], json!({"active": true, "device": "Speakers", "level": 1.0}));
        // Lights off stops capture; so does going back to a normal effect.
        call(&mut d, r#"{"id":5,"method":"set_power","params":{"on":false}}"#).await;
        d.tick(ms(40)).await;
        assert!(!on());
        call(&mut d, r#"{"id":6,"method":"set_state","params":{"effect":"spectrum_bars"}}"#).await;
        d.tick(ms(60)).await;
        assert!(on(), "set on 'all'");
        call(&mut d, r#"{"id":7,"method":"set_state","params":{"effect":"rainbow_wave"}}"#).await;
        d.tick(ms(80)).await;
        assert!(!on());
        assert_eq!(*d.lights.heard.last().unwrap(), AudioLevels::SILENT);
    }

    #[test]
    fn frames_follow_the_sound() {
        let dev = DeviceInfo {
            id: "openrgb:Strip".into(),
            name: "Strip".into(),
            kind: DeviceKind::Other,
            provider: "openrgb".into(),
            led_count: 4,
            openrgb_index: Some(1),
            zones: vec![],
            hidden: false,
            led_map: Default::default(),
        };
        let mut scene = Scene::default();
        scene.default.effect = Effect::BeatFlash;
        scene.default.color = "0000ff".into();
        let beat = AudioLevels { beat: true, beat_age: 0.0, ..AudioLevels::SILENT };
        assert_eq!(device_frame(&scene, &dev, 1.0, &beat), vec![Color::new(0, 0, 255); 4]);
        assert!(device_frame(&scene, &dev, 1.0, &AudioLevels::SILENT).iter().all(|c| c.b < 10));
    }

    async fn call(d: &mut Daemon<Fake>, req: &str) -> Value {
        serde_json::from_str(&d.handle_line(req).await).unwrap()
    }

    fn ms(v: u64) -> Duration {
        Duration::from_millis(v)
    }

    #[tokio::test]
    async fn static_is_sent_on_change_and_kept_alive() {
        let mut d = Daemon::new(Fake::default(), None);
        d.tick(ms(0)).await; // first frame
        d.tick(ms(100)).await; // nothing new
        assert_eq!(d.lights.frames.len(), 1);
        let r = call(
            &mut d,
            r##"{"id":1,"method":"set_state","params":{"color":"#FF8000","brightness":40}}"##,
        )
        .await;
        assert_eq!(r["result"]["color"], json!("ff8000"));
        d.tick(ms(150)).await; // change -> immediate
        d.tick(ms(900)).await; // < keepalive
        d.tick(ms(1200)).await; // keepalive
        assert_eq!(d.lights.frames.len(), 3);
        assert_eq!(d.lights.frames[2].default.color, "ff8000");
        // Same values again: not dirty.
        call(
            &mut d,
            r#"{"id":2,"method":"set_color","params":{"color":"ff8000"}}"#,
        )
        .await;
        d.tick(ms(1300)).await;
        assert_eq!(d.lights.frames.len(), 3);
    }

    #[tokio::test]
    async fn per_target_state_and_inheritance() {
        let mut d = Daemon::new(Fake::default(), None);
        call(
            &mut d,
            r#"{"id":1,"method":"set_state","params":{"color":"0000ff"}}"#,
        )
        .await;
        call(&mut d, r#"{"id":2,"method":"set_state","params":{"target":"openrgb:Board","effect":"breathing"}}"#).await;
        let zone = call(
            &mut d,
            r#"{"id":3,"method":"get_target","params":{"target":"openrgb:Board/ARGB_V2_1"}}"#,
        )
        .await;
        // The zone inherits the board's breathing, with the color the board had.
        assert_eq!(zone["result"]["effect"], json!("breathing"));
        assert_eq!(zone["result"]["color"], json!("0000ff"));
        assert_eq!(zone["result"]["own"], json!(false));
        let all = call(
            &mut d,
            r#"{"id":4,"method":"get_target","params":{"target":"all"}}"#,
        )
        .await;
        assert_eq!(all["result"]["effect"], json!("static"));
        // Setting "all" resets every device.
        call(
            &mut d,
            r#"{"id":5,"method":"set_state","params":{"target":"all","color":"ff0000"}}"#,
        )
        .await;
        assert!(d.scene.overrides.is_empty());
    }

    #[tokio::test]
    async fn groups_crud_and_apply() {
        let mut d = Daemon::new(Fake::default(), None);
        let g = call(&mut d, r#"{"id":1,"method":"save_group","params":{"name":" Fans ","members":["openrgb:Hub/0","openrgb:Hub/1"]}}"#).await;
        assert_eq!(g["result"]["id"], json!("g1"));
        assert_eq!(g["result"]["name"], json!("Fans"));
        call(
            &mut d,
            r#"{"id":2,"method":"set_state","params":{"target":"group:g1","effect":"spectrum"}}"#,
        )
        .await;
        assert_eq!(d.scene.overrides.len(), 2);
        assert!(d.scene.is_animated());
        let r = call(&mut d, r#"{"id":3,"method":"save_group","params":{"id":"g1","name":"Top","members":["openrgb:Hub/0"]}}"#).await;
        assert_eq!(r["result"]["members"], json!(["openrgb:Hub/0"]));
        assert_eq!(
            call(&mut d, r#"{"id":4,"method":"groups"}"#).await["result"]
                .as_array()
                .unwrap()
                .len(),
            1
        );
        assert!(call(
            &mut d,
            r#"{"id":5,"method":"save_group","params":{"name":"","members":[]}}"#
        )
        .await["error"]
            .is_object());
        assert!(call(
            &mut d,
            r#"{"id":6,"method":"save_group","params":{"name":"x","members":["group:g1"]}}"#
        )
        .await["error"]
            .is_object());
        assert!(call(
            &mut d,
            r#"{"id":7,"method":"set_state","params":{"target":"group:g9","color":"ffffff"}}"#
        )
        .await["error"]
            .is_object());
        assert_eq!(
            call(
                &mut d,
                r#"{"id":8,"method":"delete_group","params":{"id":"g1"}}"#
            )
            .await["result"],
            json!(true)
        );
        assert!(call(
            &mut d,
            r#"{"id":9,"method":"delete_group","params":{"id":"g1"}}"#
        )
        .await["error"]
            .is_object());
    }

    #[tokio::test]
    async fn layouts_resize_and_validate() {
        let mut d = Daemon::new(Fake::default(), None);
        call(&mut d, r#"{"id":1,"method":"set_layout","params":{"zone":"openrgb:Board/ARGB_V2_1","fans":3,"leds_per_fan":8}}"#).await;
        assert_eq!(
            d.scene.layouts["openrgb:Board/ARGB_V2_1"],
            FanLayout {
                fans: 3,
                leds_per_fan: 8
            }
        );
        assert_eq!(d.lights.rescans, 1);
        call(&mut d, r#"{"id":2,"method":"set_layout","params":{"zone":"openrgb:Board/ARGB_V2_1","fans":0,"leds_per_fan":8}}"#).await;
        assert!(d.scene.layouts.is_empty());
        assert!(call(&mut d, r#"{"id":3,"method":"set_layout","params":{"zone":"openrgb:Board","fans":3,"leds_per_fan":8}}"#).await["error"].is_object());
    }

    #[tokio::test]
    async fn get_targets_reports_each_rows_state() {
        let mut d = Daemon::new(Fake::default(), None);
        call(&mut d, r#"{"id":1,"method":"set_state","params":{"target":"openrgb:A","effect":"breathing","color":"00ff00"}}"#).await;
        call(&mut d, r#"{"id":2,"method":"save_group","params":{"name":"G","members":["openrgb:A","openrgb:B"]}}"#).await;
        let r = call(&mut d, r#"{"id":3,"method":"get_targets","params":{"targets":["all","openrgb:A","openrgb:B","group:g1"]}}"#).await;
        let r = &r["result"];
        assert_eq!(r["openrgb:A"]["effect"], json!("breathing"));
        assert_eq!(r["openrgb:A"]["own"], json!(true));
        assert_eq!(r["openrgb:B"]["effect"], json!("static"));
        assert_eq!(r["openrgb:B"]["own"], json!(false));
        assert_eq!(r["group:g1"]["mixed"], json!(true));
        assert_eq!(r["all"]["mixed"], json!(false));
    }

    #[tokio::test]
    async fn reset_starts_fresh_but_keeps_profiles_unless_asked() {
        let mut d = Daemon::new(Fake::default(), None);
        call(&mut d, r#"{"id":1,"method":"set_state","params":{"target":"openrgb:A","color":"00ff00"}}"#).await;
        call(&mut d, r#"{"id":2,"method":"save_group","params":{"name":"G","members":["openrgb:A"]}}"#).await;
        call(&mut d, r#"{"id":3,"method":"save_profile","params":{"name":"P"}}"#).await;
        call(&mut d, r#"{"id":4,"method":"set_power","params":{"on":false}}"#).await;
        call(&mut d, r#"{"id":5,"method":"reset"}"#).await;
        assert!(d.scene.overrides.is_empty() && d.scene.groups.is_empty() && !d.scene.off);
        assert_eq!(d.scene.default, LightState::default());
        assert_eq!(d.scene.profiles.len(), 1);
        call(&mut d, r#"{"id":6,"method":"reset","params":{"profiles":true}}"#).await;
        assert!(d.scene.profiles.is_empty());
    }

    #[tokio::test]
    async fn power_and_profiles() {
        let mut d = Daemon::new(Fake::default(), None);
        call(&mut d, r#"{"id":1,"method":"set_state","params":{"color":"ff0000"}}"#).await;
        let r = call(&mut d, r#"{"id":2,"method":"save_profile","params":{"name":"Red"}}"#).await;
        assert_eq!(r["result"], json!(["Red"]));
        call(&mut d, r#"{"id":3,"method":"set_state","params":{"color":"0000ff"}}"#).await;
        call(&mut d, r#"{"id":4,"method":"set_power","params":{"on":false}}"#).await;
        let s = call(&mut d, r#"{"id":5,"method":"status"}"#).await;
        assert_eq!(s["result"]["on"], json!(false));
        call(&mut d, r#"{"id":6,"method":"load_profile","params":{"name":"Red"}}"#).await;
        assert!(!d.scene.off);
        assert_eq!(d.scene.default.color, "ff0000");
        assert!(call(&mut d, r#"{"id":7,"method":"load_profile","params":{"name":"Nope"}}"#).await["error"].is_object());
        call(&mut d, r#"{"id":8,"method":"set_power","params":{"on":false}}"#).await;
        call(&mut d, r#"{"id":9,"method":"set_state","params":{"brightness":50}}"#).await;
        assert!(!d.scene.off, "changing the lighting turns it back on");
        let r = call(&mut d, r#"{"id":10,"method":"delete_profile","params":{"name":"Red"}}"#).await;
        assert_eq!(r["result"], json!([]));
    }

    #[tokio::test]
    async fn animated_effects_render_every_tick() {
        let mut d = Daemon::new(Fake::default(), None);
        call(
            &mut d,
            r#"{"id":1,"method":"set_state","params":{"effect":"rainbow_wave","speed":80}}"#,
        )
        .await;
        for i in 0..5 {
            d.tick(ms(i * 33)).await;
        }
        assert_eq!(d.lights.frames.len(), 5);
        let s = call(&mut d, r#"{"id":2,"method":"status"}"#).await;
        assert_eq!(s["result"]["effect"], json!("rainbow_wave"));
        assert_eq!(s["result"]["connected"], json!(true));
    }

    #[tokio::test]
    async fn failures_back_off_and_are_reported() {
        let mut d = Daemon::new(
            Fake {
                fail: true,
                ..Default::default()
            },
            None,
        );
        d.tick(ms(0)).await;
        let s = call(&mut d, r#"{"id":1,"method":"status"}"#).await;
        assert_eq!(s["result"]["connected"], json!(false));
        assert!(s["result"]["error"]
            .as_str()
            .unwrap()
            .contains("not reachable"));
        d.lights.fail = false;
        d.tick(ms(1000)).await; // still backing off
        assert!(d.lights.frames.is_empty());
        d.tick(RETRY + ms(1)).await;
        assert_eq!(d.lights.frames.len(), 1);
    }

    #[tokio::test]
    async fn bad_requests_change_nothing() {
        let mut d = Daemon::new(Fake::default(), None);
        assert_eq!(
            call(&mut d, "not json").await["error"]["code"],
            json!(-32700)
        );
        assert!(
            call(&mut d, r#"{"id":1,"method":"nope"}"#).await["error"]["message"]
                .as_str()
                .unwrap()
                .contains("unknown method")
        );
        assert!(call(
            &mut d,
            r#"{"id":1,"method":"set_state","params":{"color":"00ff00","effect":"disco"}}"#
        )
        .await["error"]
            .is_object());
        assert!(call(
            &mut d,
            r#"{"id":1,"method":"set_state","params":{"brightness":101}}"#
        )
        .await["error"]
            .is_object());
        assert!(call(
            &mut d,
            r#"{"id":1,"method":"set_state","params":{"speed":0}}"#
        )
        .await["error"]
            .is_object());
        assert_eq!(d.scene, Scene::default());
    }

    #[tokio::test]
    async fn preview_shows_each_device_and_zone() {
        let mut d = Daemon::new(Fake::default(), None);
        call(&mut d, r#"{"id":1,"method":"set_state","params":{"color":"ff0000"}}"#).await;
        let r = call(&mut d, r#"{"id":2,"method":"preview"}"#).await;
        assert_eq!(r["result"][0]["id"], json!("openrgb:Board"));
        assert_eq!(r["result"][0]["colors"], json!(["ff0000", "ff0000", "ff0000"]));
        assert_eq!(r["result"][0]["zones"][0]["colors"].as_array().unwrap().len(), 3);
        let r = call(&mut d, r#"{"id":3,"method":"preview","params":{"max":2}}"#).await;
        assert_eq!(r["result"][0]["colors"].as_array().unwrap().len(), 2);
        call(&mut d, r#"{"id":4,"method":"set_power","params":{"on":false}}"#).await;
        let r = call(&mut d, r#"{"id":5,"method":"preview"}"#).await;
        assert_eq!(r["result"][0]["zones"][0]["colors"][0], json!("000000"));
    }

    #[tokio::test]
    async fn index_zone_ids_move_to_names() {
        let mut d = Daemon::new(Fake::default(), None);
        let layout = FanLayout {
            fans: 3,
            leds_per_fan: 1,
        };
        d.scene.layouts.insert("openrgb:Board/0".into(), layout);
        d.scene.apply("openrgb:Board/0/1", LightState::default());
        let r = call(&mut d, r#"{"id":1,"method":"devices"}"#).await;
        assert_eq!(
            r["result"][0]["zones"][0]["id"],
            json!("openrgb:Board/ARGB_V2_1")
        );
        assert_eq!(d.scene.layouts["openrgb:Board/ARGB_V2_1"], layout);
        assert!(d.scene.overrides.contains_key("openrgb:Board/ARGB_V2_1/1"));
        assert!(!d.scene.layouts.contains_key("openrgb:Board/0"));
        assert_eq!(
            d.lights.rescans, 1,
            "headers are sized again for the moved layout"
        );
        call(&mut d, r#"{"id":2,"method":"devices"}"#).await;
        assert_eq!(d.lights.rescans, 1, "nothing left to move");
    }

    #[tokio::test]
    async fn hidden_devices_are_not_written() {
        let mut d = Daemon::new(Fake::default(), None);
        assert_eq!(call(&mut d, r#"{"id":1,"method":"hidden"}"#).await["result"], json!([]));
        let r = call(&mut d, r#"{"id":2,"method":"devices"}"#).await;
        assert_eq!(r["result"][0]["hidden"], json!(false));
        let r = call(&mut d, r#"{"id":3,"method":"set_hidden","params":{"id":"openrgb:Board","hidden":true}}"#).await;
        assert_eq!(r["result"], json!({"hidden": ["openrgb:Board"]}));
        d.tick(ms(0)).await;
        assert_eq!(d.lights.board, vec![None], "a hidden device gets nothing");
        // It stays hidden on rescan; previews and states still work.
        let r = call(&mut d, r#"{"id":4,"method":"rescan"}"#).await;
        assert_eq!(r["result"][0]["hidden"], json!(true));
        call(&mut d, r#"{"id":5,"method":"set_state","params":{"target":"openrgb:Board","color":"00ff00"}}"#).await;
        let r = call(&mut d, r#"{"id":6,"method":"preview"}"#).await;
        assert_eq!(r["result"][0]["colors"][0], json!("00ff00"));
        assert_eq!(r["result"][0]["hidden"], json!(true));
        let r = call(&mut d, r#"{"id":7,"method":"get_targets","params":{"targets":["openrgb:Board"]}}"#).await;
        assert_eq!(r["result"]["openrgb:Board"]["color"], json!("00ff00"));
        let r = call(&mut d, r#"{"id":8,"method":"diagnostics"}"#).await;
        assert_eq!(r["result"][0]["hidden"], json!(true));
        // Triggers do not touch it either.
        call(&mut d, r#"{"id":9,"method":"save_trigger","params":{"trigger":{"name":"f","source":{"kind":"mouse_click"},"action":{"kind":"flash","color":"ff0000","duration_ms":500}}}}"#).await;
        call(&mut d, r#"{"id":10,"method":"input_event","params":{"kind":"mouse_click"}}"#).await;
        d.tick(ms(100)).await;
        assert_eq!(*d.lights.board.last().unwrap(), None);
        // Bad requests.
        assert!(call(&mut d, r#"{"id":11,"method":"set_hidden","params":{"id":"all","hidden":true}}"#).await["error"].is_object());
        assert!(call(&mut d, r#"{"id":12,"method":"set_hidden","params":{"id":"openrgb:Board"}}"#).await["error"].is_object());
        // Reset keeps it hidden; unhiding writes again.
        call(&mut d, r#"{"id":13,"method":"reset"}"#).await;
        assert!(d.scene.is_hidden("openrgb:Board"));
        let r = call(&mut d, r#"{"id":14,"method":"set_hidden","params":{"id":"openrgb:Board","hidden":false}}"#).await;
        assert_eq!(r["result"], json!({"hidden": []}));
        d.tick(ms(200)).await;
        assert!(d.lights.board.last().unwrap().is_some());
    }

    #[tokio::test]
    async fn triggers_crud_and_input() {
        let mut d = Daemon::new(Fake::default(), None);
        assert_eq!(call(&mut d, r#"{"id":1,"method":"triggers"}"#).await["result"], json!([]));
        assert_eq!(
            call(&mut d, r#"{"id":2,"method":"input_wanted"}"#).await["result"],
            json!({"keys": false, "key_codes": false, "mouse_click": false, "mouse_move": false, "apps": []})
        );
        let r = call(&mut d, r#"{"id":3,"method":"save_trigger","params":{"trigger":{"name":"Typing","source":{"kind":"key","keys":"any"},"action":{"kind":"flash","color":"FF0000","duration_ms":1000},"targets":["openrgb:Board/ARGB_V2_1"]}}}"#).await;
        let t = &r["result"];
        assert_eq!(t["id"], json!("t1"));
        assert_eq!(t["enabled"], json!(true));
        assert_eq!(t["action"], json!({"kind":"flash","color":"ff0000","duration_ms":1000}));
        assert_eq!(
            call(&mut d, r#"{"id":4,"method":"input_wanted"}"#).await["result"],
            json!({"keys": true, "key_codes": false, "mouse_click": false, "mouse_move": false, "apps": []})
        );
        // A key press flashes the target on the next frame and fades.
        call(&mut d, r#"{"id":5,"method":"set_state","params":{"color":"000000"}}"#).await;
        d.tick(ms(0)).await;
        let r = call(&mut d, r#"{"id":6,"method":"input_event","params":{"kind":"key","vk":65}}"#).await;
        assert_eq!(r["result"], json!({"fired": 1}));
        d.tick(ms(1)).await;
        assert!(d.wants_fast());
        let lit = d.lights.board.last().unwrap().clone().unwrap();
        assert!(lit[0].r > 240, "{lit:?}");
        let r = call(&mut d, r#"{"id":7,"method":"preview"}"#).await;
        assert_ne!(r["result"][0]["colors"][0], json!("000000"));
        d.tick(ms(1100)).await;
        assert_eq!(d.lights.board.last().unwrap().clone().unwrap()[0], Color::BLACK, "faded and cleared");
        assert!(!d.wants_fast());
        // Edit: disable it. Unknown ids and bad triggers are errors.
        let r = call(&mut d, r#"{"id":8,"method":"save_trigger","params":{"trigger":{"id":"t1","name":"Typing","enabled":false,"source":{"kind":"key","keys":"any"},"action":{"kind":"pulse","duration_ms":200}}}}"#).await;
        assert_eq!(r["result"]["enabled"], json!(false));
        assert_eq!(call(&mut d, r#"{"id":9,"method":"input_event","params":{"kind":"key","vk":65}}"#).await["result"], json!({"fired": 0}));
        assert!(call(&mut d, r#"{"id":10,"method":"save_trigger","params":{"trigger":{"id":"t9","name":"x","source":{"kind":"mouse_move"},"action":{"kind":"pulse","duration_ms":200}}}}"#).await["error"].is_object());
        assert!(call(&mut d, r#"{"id":11,"method":"save_trigger","params":{"trigger":{"name":"x","source":{"kind":"mouse_move"},"action":{"kind":"pulse","duration_ms":1}}}}"#).await["error"].is_object());
        assert!(call(&mut d, r#"{"id":12,"method":"input_event","params":{"kind":"key","vk":999}}"#).await["error"].is_object());
        let e = call(&mut d, r#"{"id":13,"method":"input_event","params":{"kind":"key","vk":123456}}"#).await;
        assert!(!e["error"]["message"].as_str().unwrap().contains("123456"), "key codes are never echoed");
        assert!(call(&mut d, r#"{"id":14,"method":"input_event","params":{"kind":"wheel"}}"#).await["error"].is_object());
        assert!(call(&mut d, r#"{"id":15,"method":"input_event","params":{"kind":"mouse_click","button":"thumb"}}"#).await["error"].is_object());
        // A preset trigger loads a profile.
        call(&mut d, r#"{"id":16,"method":"set_state","params":{"color":"0000ff"}}"#).await;
        call(&mut d, r#"{"id":17,"method":"save_profile","params":{"name":"Blue"}}"#).await;
        call(&mut d, r#"{"id":18,"method":"set_state","params":{"color":"ffffff"}}"#).await;
        call(&mut d, r#"{"id":19,"method":"save_trigger","params":{"trigger":{"name":"Click","source":{"kind":"mouse_click","button":"right"},"action":{"kind":"preset","name":"Blue"}}}}"#).await;
        let r = call(&mut d, r#"{"id":20,"method":"input_event","params":{"kind":"mouse_click","button":"right"}}"#).await;
        assert_eq!(r["result"], json!({"fired": 1}));
        assert_eq!(d.scene.default.color, "0000ff");
        assert_eq!(
            call(&mut d, r#"{"id":21,"method":"input_wanted"}"#).await["result"],
            json!({"keys": false, "key_codes": false, "mouse_click": true, "mouse_move": false, "apps": []})
        );
        assert_eq!(call(&mut d, r#"{"id":22,"method":"delete_trigger","params":{"id":"t2"}}"#).await["result"], json!(true));
        assert!(call(&mut d, r#"{"id":23,"method":"delete_trigger","params":{"id":"t2"}}"#).await["error"].is_object());
        assert_eq!(d.scene.triggers.len(), 1);
    }

    #[tokio::test]
    async fn audio_triggers_capture_only_when_enabled() {
        let active = std::sync::Arc::new(std::sync::atomic::AtomicBool::new(false));
        let mut bands = [0.0; AudioLevels::BANDS];
        bands[3] = 0.5;
        let loud = AudioLevels { level: 1.0, bass: 1.0, mid: 0.5, treble: 0.25, beat: true, beat_age: 0.0, bands, peaks: bands, ..AudioLevels::SILENT };
        let audio = FakeAudio { active: active.clone(), levels: loud };
        let mut d = Daemon::new(Fake::default(), None).with_audio(Box::new(audio));
        let on = || active.load(std::sync::atomic::Ordering::SeqCst);
        call(&mut d, r#"{"id":0,"method":"set_state","params":{"color":"000000"}}"#).await;
        d.tick(ms(0)).await;
        assert!(!on());
        let s = call(&mut d, r#"{"id":1,"method":"audio_spectrum"}"#).await;
        assert_eq!(s["result"]["active"], json!(false));
        assert_eq!(s["result"]["bands"].as_array().unwrap().len(), 24);
        assert_eq!(s["result"]["bands"][3], json!(0.0));
        // A disabled audio trigger does not capture; enabling it does.
        call(&mut d, r#"{"id":2,"method":"save_trigger","params":{"trigger":{"name":"Beat","enabled":false,"source":{"kind":"audio_beat"},"action":{"kind":"flash","color":"ffffff","duration_ms":200}}}}"#).await;
        d.tick(ms(10)).await;
        assert!(!on() && !d.uses_audio());
        call(&mut d, r#"{"id":3,"method":"save_trigger","params":{"trigger":{"id":"t1","name":"Beat","source":{"kind":"audio_beat"},"action":{"kind":"flash","color":"ffffff","duration_ms":200}}}}"#).await;
        d.tick(ms(20)).await;
        assert!(on() && d.uses_audio());
        assert_eq!(d.lights.board.last().unwrap().clone().unwrap()[0], Color::new(255, 255, 255), "the beat fired");
        let s = call(&mut d, r#"{"id":4,"method":"audio_spectrum"}"#).await;
        assert_eq!(s["result"]["active"], json!(true));
        assert_eq!(s["result"]["bass"], json!(1.0));
        assert_eq!(s["result"]["beat"], json!(true));
        assert_eq!(s["result"]["bands"][3], json!(0.5));
        assert_eq!(s["result"]["peaks"].as_array().unwrap().len(), 24);
        // Lights off, or the trigger deleted: capture stops.
        call(&mut d, r#"{"id":5,"method":"set_power","params":{"on":false}}"#).await;
        d.tick(ms(30)).await;
        assert!(!on());
        call(&mut d, r#"{"id":6,"method":"set_power","params":{"on":true}}"#).await;
        d.tick(ms(40)).await;
        assert!(on());
        call(&mut d, r#"{"id":7,"method":"delete_trigger","params":{"id":"t1"}}"#).await;
        d.tick(ms(50)).await;
        assert!(!on());
    }

    #[tokio::test]
    async fn scene_persists() {
        let dir = std::env::temp_dir().join(format!("lightsyncd-test-{}", std::process::id()));
        let path = dir.join("state.json");
        let _ = std::fs::remove_file(&path);
        let mut d = Daemon::new(Fake::default(), Some(path.clone()));
        call(&mut d, r#"{"id":1,"method":"set_state","params":{"target":"openrgb:Board","effect":"breathing","color":"123456"}}"#).await;
        // The first change is written at once; the second, a moment later, waits for the next write.
        assert!(Daemon::new(Fake::default(), Some(path.clone())).scene.overrides.contains_key("openrgb:Board"));
        call(&mut d, r#"{"id":2,"method":"save_group","params":{"name":"Desk","members":["openrgb:Board"]}}"#).await;
        assert!(d.save_pending);
        d.flush();
        let d2 = Daemon::new(Fake::default(), Some(path.clone()));
        assert_eq!(d2.scene, d.scene);
        // Written whole: no temporary file is left behind.
        assert!(!path.with_extension("json.tmp").exists());
        let _ = std::fs::remove_dir_all(dir);
    }
}
