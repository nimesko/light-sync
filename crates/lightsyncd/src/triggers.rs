//! Triggers: an event (a key, a click, mouse movement, a beat, a loud band) briefly paints over
//! the scene on some devices (a flash, a brightness pulse, a ripple, a glow), or loads a profile.
//!
//! Triggers are part of the scene (persisted, none by default). What they are doing right now
//! lives in an [`Engine`]: up to [`MAX_ACTIVE`] instances, each fading out over its duration. Every
//! frame takes an [`Overlay`] snapshot of them and paints it over each device's rendered colors,
//! oldest first so the newest ends up on top, each blended by its remaining strength.
//!
//! Privacy: key events carry a virtual-key code only so key triggers can match it; codes are
//! never logged, stored or kept after the event is handled.

use crate::effects::{self, AudioLevels, LightState};
use crate::rpc::DeviceInfo;
use crate::scene::Scene;
use anyhow::{anyhow, bail, Result};
use lightsync_core::Color;
use serde::{Deserialize, Serialize};
use std::collections::BTreeMap;
use std::time::Duration;

/// A trigger fires at most this often.
pub const MIN_GAP: Duration = Duration::from_millis(50);
/// At most this many instances are shown at once (the oldest go first).
pub const MAX_ACTIVE: usize = 128;
/// At most this many triggers can be saved.
pub const MAX_TRIGGERS: usize = 64;
/// Mouse movement is reported a lot: events closer together than this are ignored.
pub const MOUSE_MOVE_GAP: Duration = Duration::from_millis(100);
/// Allowed `duration_ms`.
pub const DURATION_MS: std::ops::RangeInclusive<u32> = 50..=10_000;
/// How much a `fade_in` glow builds on each event (it is full after 4 quick events).
const FADE_STEP: f32 = 0.25;
/// Width of a ripple's wave front, as a fraction of half the device.
const RIPPLE_WIDTH: f32 = 0.3;

/// Which virtual-key codes a key trigger reacts to: `"any"` or a list of codes 1..254.
#[derive(Debug, Clone, PartialEq)]
pub enum Keys {
    Any,
    Codes(Vec<u8>),
}

#[derive(Serialize, Deserialize)]
#[serde(untagged)]
enum KeysRepr {
    Word(String),
    Codes(Vec<u64>),
}

impl Serialize for Keys {
    fn serialize<S: serde::Serializer>(&self, s: S) -> std::result::Result<S::Ok, S::Error> {
        match self {
            Keys::Any => KeysRepr::Word("any".into()),
            Keys::Codes(c) => KeysRepr::Codes(c.iter().map(|&k| k as u64).collect()),
        }
        .serialize(s)
    }
}

impl<'de> Deserialize<'de> for Keys {
    fn deserialize<D: serde::Deserializer<'de>>(d: D) -> std::result::Result<Self, D::Error> {
        use serde::de::Error;
        match KeysRepr::deserialize(d).map_err(|_| D::Error::custom("'keys' must be \"any\" or a list of key codes 1..254"))? {
            KeysRepr::Word(w) if w == "any" => Ok(Keys::Any),
            KeysRepr::Word(_) => Err(D::Error::custom("'keys' must be \"any\" or a list of key codes 1..254")),
            KeysRepr::Codes(c) => {
                let mut v = c
                    .into_iter()
                    .map(|k| u8::try_from(k).ok().filter(|k| (1..=254).contains(k)))
                    .collect::<Option<Vec<u8>>>()
                    .ok_or_else(|| D::Error::custom("key codes must be 1..254"))?;
                v.sort_unstable();
                v.dedup();
                Ok(Keys::Codes(v))
            }
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum MouseButton {
    Left,
    Right,
    Middle,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum Band {
    Level,
    Bass,
    Mid,
    Treble,
}

impl Band {
    fn of(self, a: &AudioLevels) -> f32 {
        match self {
            Band::Level => a.level,
            Band::Bass => a.bass,
            Band::Mid => a.mid,
            Band::Treble => a.treble,
        }
    }
}

/// What makes a trigger fire.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(tag = "kind", rename_all = "snake_case")]
pub enum Source {
    Key { keys: Keys },
    MouseClick {
        /// Any button when missing.
        #[serde(default, skip_serializing_if = "Option::is_none")]
        button: Option<MouseButton>,
    },
    MouseMove,
    /// A detected beat.
    AudioBeat,
    /// A band rising above a level (0..1). Fires when it crosses; `fade_in` keeps glowing while
    /// the band stays above.
    AudioLevel { band: Band, above: f32 },
    /// An application is running (its program file, e.g. "cs2.exe"): its `lighting` shows on the
    /// targets for as long as it runs. The UI reports which of these programs run.
    AppRunning {
        app: String,
        /// Only while the browser's current page is on this site (a host such as "youtube.com",
        /// which also covers "music.youtube.com"; no paths or queries).
        #[serde(default, skip_serializing_if = "Option::is_none")]
        site: Option<String>,
        /// More apps (and sites): the trigger holds while any one of them does.
        #[serde(default, skip_serializing_if = "Vec::is_empty")]
        apps: Vec<AppWatch>,
    },
}

/// One app (and optionally a site it shows) an `app_running` trigger watches.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct AppWatch {
    pub app: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub site: Option<String>,
}

/// The most apps and sites one trigger watches.
const MAX_WATCHES: usize = 16;

/// Everything an `app_running` trigger watches, as the keys the UI reports ([`watch_key`]).
fn watch_keys(app: &str, site: Option<&str>, apps: &[AppWatch]) -> Vec<String> {
    std::iter::once(watch_key(app, site))
        .chain(apps.iter().map(|w| watch_key(&w.app, w.site.as_deref())))
        .collect()
}

impl Source {
    pub fn is_audio(&self) -> bool {
        matches!(self, Source::AudioBeat | Source::AudioLevel { .. })
    }
}

/// What a trigger does.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(tag = "kind", rename_all = "snake_case")]
pub enum Action {
    /// The color, fading out over the duration.
    Flash {
        color: String,
        duration_ms: u32,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        radius: Option<f32>,
        /// A new random (bright) color each time it fires instead of `color`.
        #[serde(default, skip_serializing_if = "is_false")]
        random: bool,
        /// With a radius: each ring of keys / LEDs around the origin starts this much later.
        #[serde(default, skip_serializing_if = "Option::is_none")]
        layer_delay_ms: Option<u32>,
    },
    /// The current colors at full brightness, rising and falling over the duration.
    Pulse {
        duration_ms: u32,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        radius: Option<f32>,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        layer_delay_ms: Option<u32>,
    },
    /// A wave of the color travelling from the centre of each device to its ends (or, with a
    /// radius, from where the event happened out to the radius).
    Ripple {
        color: String,
        duration_ms: u32,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        radius: Option<f32>,
        #[serde(default, skip_serializing_if = "is_false")]
        random: bool,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        layer_delay_ms: Option<u32>,
    },
    /// A glow of the color that builds while events repeat and fades out over the duration
    /// after they stop.
    FadeIn {
        color: String,
        duration_ms: u32,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        radius: Option<f32>,
        #[serde(default, skip_serializing_if = "is_false")]
        random: bool,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        layer_delay_ms: Option<u32>,
    },
    /// Show this lighting (any effect: static, breathing, rainbow, fire, spectrum bars...) on the
    /// targets: while the app runs (`app_running`), or for `duration_ms` after an event, fading out
    /// over the last quarter (with a radius: around where the event happened). Without `duration_ms`
    /// it stays on once fired, until the trigger is turned off, changed or deleted.
    Lighting {
        #[serde(flatten)]
        state: LightState,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        duration_ms: Option<u32>,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        radius: Option<f32>,
        /// A different lighting per device (zone, fan, group or LED): each shows on its own target,
        /// over `state`, which keeps any of the trigger's targets that have none.
        #[serde(default, skip_serializing_if = "Vec::is_empty")]
        per_target: Vec<TargetLighting>,
    },
    /// Load a saved profile.
    Preset { name: String },
}

/// One target's own lighting in a `lighting` action.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct TargetLighting {
    pub target: String,
    #[serde(flatten)]
    pub state: LightState,
}

/// The most targets with their own lighting in one trigger.
const MAX_PER_TARGET: usize = 64;

/// What a trigger's `lighting` action paints: (expanded targets, lighting), bottom first. Its own
/// lighting on the targets without one of their own (all of them, if none has), then each target's.
fn lightings(action: &Action, targets: &[String], scene: &Scene) -> Vec<(Vec<String>, LightState)> {
    let Action::Lighting { state, per_target, .. } = action else {
        return vec![];
    };
    let expand = |ids: &[String]| ids.iter().flat_map(|id| scene.expand(id)).collect::<Vec<_>>();
    let mut out = vec![];
    if per_target.is_empty() || targets.is_empty() {
        out.push((expand(targets), state.clone()));
    } else {
        let rest: Vec<String> = targets.iter().filter(|id| !per_target.iter().any(|p| p.target == **id)).cloned().collect();
        if !rest.is_empty() {
            out.push((expand(&rest), state.clone()));
        }
    }
    for p in per_target {
        out.push((scene.expand(&p.target), p.state.clone()));
    }
    out
}

fn yes() -> bool {
    true
}

fn is_false(b: &bool) -> bool {
    !*b
}

/// Largest delay between two rings of a local effect.
pub const MAX_LAYER_DELAY_MS: u32 = 1000;

/// A program file name as triggers keep it: the file part only, lowercase, ".exe" added.
pub fn app_name(s: &str) -> String {
    let file = s.trim().rsplit(['\\', '/']).next().unwrap_or("").trim().to_lowercase();
    if file.is_empty() || file.ends_with(".exe") {
        file
    } else {
        format!("{file}.exe")
    }
}

/// A web site's host from what the user typed or a browser shows: lowercase, without the scheme,
/// "www.", port, path, query or fragment ("https://www.YouTube.com/watch?v=1" -> "youtube.com").
/// Empty if it is not a host.
pub fn site_host(s: &str) -> String {
    let s = s.trim().to_lowercase();
    let s = s.split_once("://").map_or(s.as_str(), |(_, rest)| rest);
    let host = s.split(['/', '?', '#']).next().unwrap_or("");
    let host = host.rsplit_once('@').map_or(host, |(_, h)| h);
    let host = host.split(':').next().unwrap_or("");
    let host = host.strip_prefix("www.").unwrap_or(host).trim_end_matches('.');
    let ok = host.len() >= 3
        && host.len() <= 253
        && host.contains('.')
        && host.split('.').all(|l| !l.is_empty() && !l.starts_with('-') && !l.ends_with('-'))
        && host.chars().all(|c| c.is_ascii_alphanumeric() || c == '.' || c == '-');
    if ok { host.to_string() } else { String::new() }
}

/// What the UI is asked to check, and reports back when it holds: "chrome.exe" (the app runs) or
/// "chrome.exe/youtube.com" (and its current page is on that site).
pub fn watch_key(app: &str, site: Option<&str>) -> String {
    match site {
        Some(s) => format!("{app}/{s}"),
        None => app.to_string(),
    }
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct Trigger {
    #[serde(default)]
    pub id: String,
    pub name: String,
    #[serde(default = "yes")]
    pub enabled: bool,
    pub source: Source,
    pub action: Action,
    /// Device / zone / fan ids or "group:<id>"; empty means every device that is not hidden.
    #[serde(default)]
    pub targets: Vec<String>,
    /// More effects it plays at the same time, each on its own targets, over the one above (so
    /// with no targets above, these devices show their own effect and the rest show `action`).
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub also: Vec<Part>,
}

/// One more effect of a trigger, on its own targets (empty: every device that is not hidden).
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct Part {
    pub action: Action,
    #[serde(default)]
    pub targets: Vec<String>,
    /// The same effect as the one before it, for devices with their own settings ("lights per",
    /// sound colors): only the app uses it, to show them as one effect.
    #[serde(default, skip_serializing_if = "is_false")]
    pub with_previous: bool,
}

/// The most effects one trigger plays besides its own.
pub const MAX_PARTS: usize = 16;

impl Trigger {
    /// Its effects with their targets, bottom first: its own, then each in `also`.
    fn units(&self) -> impl Iterator<Item = (&Action, &[String])> {
        std::iter::once((&self.action, self.targets.as_slice())).chain(self.also.iter().map(|p| (&p.action, p.targets.as_slice())))
    }
}

fn color_of(s: &str) -> Result<String> {
    let c: Color = s.parse().map_err(|_| anyhow!("'color' must be \"RRGGBB\""))?;
    Ok(format!("{:02x}{:02x}{:02x}", c.r, c.g, c.b))
}

fn duration_of(ms: u32) -> Result<u32> {
    if DURATION_MS.contains(&ms) {
        Ok(ms)
    } else {
        bail!("'duration_ms' must be {}..{}", DURATION_MS.start(), DURATION_MS.end())
    }
}

/// Largest local radius, in keys (or LEDs along devices without a grid).
pub const MAX_RADIUS: f32 = 30.0;

fn radius_of(r: Option<f32>) -> Result<()> {
    match r {
        Some(r) if !(0.0..=MAX_RADIUS).contains(&r) => bail!("'radius' must be 0..{MAX_RADIUS}"),
        _ => Ok(()),
    }
}

fn layer_delay_of(ms: Option<u32>) -> Result<()> {
    match ms {
        Some(ms) if ms > MAX_LAYER_DELAY_MS => bail!("'layer_delay_ms' must be 0..{MAX_LAYER_DELAY_MS}"),
        _ => Ok(()),
    }
}

impl Action {
    /// The local radius: the effect starts where the event happened (the pressed key, the mouse
    /// wheel) and only reaches this far. None: the whole target.
    pub fn radius(&self) -> Option<f32> {
        match self {
            Action::Flash { radius, .. } | Action::Pulse { radius, .. } | Action::Ripple { radius, .. } | Action::FadeIn { radius, .. } | Action::Lighting { radius, .. } => *radius,
            Action::Preset { .. } => None,
        }
    }

    /// Seconds between two rings of a local effect (0: all at once).
    fn layer_delay(&self) -> f32 {
        match self {
            Action::Flash { layer_delay_ms, .. } | Action::Pulse { layer_delay_ms, .. } | Action::Ripple { layer_delay_ms, .. } | Action::FadeIn { layer_delay_ms, .. } => {
                layer_delay_ms.unwrap_or(0) as f32 / 1000.0
            }
            _ => 0.0,
        }
    }

    fn random(&self) -> bool {
        matches!(self, Action::Flash { random: true, .. } | Action::Ripple { random: true, .. } | Action::FadeIn { random: true, .. })
    }
}

/// Check a trigger and normalise it (trimmed name, lowercase colors, unique targets). The id is
/// left to the caller.
pub fn validate(mut t: Trigger, scene: &Scene) -> Result<Trigger> {
    t.name = t.name.trim().to_string();
    if t.name.is_empty() || t.name.chars().count() > 64 {
        bail!("a trigger needs a 'name' of 1..64 characters");
    }
    match &mut t.source {
        Source::Key { keys: Keys::Codes(c) } if c.is_empty() => bail!("'keys' must be \"any\" or a non-empty list of key codes"),
        Source::AudioLevel { above, .. } if !(0.0..=1.0).contains(above) => bail!("'above' must be 0..1"),
        Source::AppRunning { app, site, apps } => {
            // Every app (and site) it watches, checked and normalised, duplicates dropped; the first
            // stays in `app` / `site`, the others in `apps`.
            let mut all: Vec<AppWatch> = vec![];
            let first = AppWatch { app: std::mem::take(app), site: site.take() };
            for w in std::iter::once(first).chain(std::mem::take(apps)).filter(|w| !w.app.trim().is_empty()) {
                let a = app_name(&w.app);
                if a.len() < 5 || a.len() > 128 {
                    bail!("'app' must be a program file name such as \"game.exe\"");
                }
                let s = match w.site.as_deref() {
                    Some(s) => match site_host(s) {
                        h if !h.is_empty() => Some(h),
                        _ if s.trim().is_empty() => None,
                        _ => bail!("'site' must be a web site's host such as \"youtube.com\""),
                    },
                    None => None,
                };
                let w = AppWatch { app: a, site: s };
                if !all.contains(&w) {
                    all.push(w);
                }
            }
            if all.is_empty() {
                bail!("'app' must be a program file name such as \"game.exe\"");
            }
            if all.len() > MAX_WATCHES {
                bail!("a trigger watches at most {MAX_WATCHES} apps and sites");
            }
            let first = all.remove(0);
            *app = first.app;
            *site = first.site;
            *apps = all;
        }
        _ => {}
    }
    // An app trigger shows its lighting while the app runs (no duration) and plays its other
    // effects when the app starts; any other trigger plays its effects when it fires.
    let app = matches!(t.source, Source::AppRunning { .. });
    check_action(&mut t.action, app, scene)?;
    t.targets = check_targets(&t.targets, scene)?;
    if t.also.len() > MAX_PARTS {
        bail!("a trigger plays at most {} effects", MAX_PARTS + 1);
    }
    for p in t.also.iter_mut() {
        check_action(&mut p.action, app, scene)?;
        p.targets = check_targets(&p.targets, scene)?;
    }
    Ok(t)
}

/// Check one of a trigger's effects and normalise it (lowercase colors, one entry per target).
fn check_action(action: &mut Action, app: bool, scene: &Scene) -> Result<()> {
    if app {
        // Nothing happened at a place: an app's effects light their whole targets.
        match action {
            Action::Flash { radius, layer_delay_ms, .. }
            | Action::Pulse { radius, layer_delay_ms, .. }
            | Action::Ripple { radius, layer_delay_ms, .. }
            | Action::FadeIn { radius, layer_delay_ms, .. } => {
                *radius = None;
                *layer_delay_ms = None;
            }
            _ => {}
        }
    }
    match action {
        Action::Flash { color, duration_ms, radius, layer_delay_ms, .. }
        | Action::Ripple { color, duration_ms, radius, layer_delay_ms, .. }
        | Action::FadeIn { color, duration_ms, radius, layer_delay_ms, .. } => {
            *color = color_of(color)?;
            duration_of(*duration_ms)?;
            radius_of(*radius)?;
            layer_delay_of(*layer_delay_ms)?;
        }
        Action::Pulse { duration_ms, radius, layer_delay_ms } => {
            duration_of(*duration_ms)?;
            radius_of(*radius)?;
            layer_delay_of(*layer_delay_ms)?;
        }
        Action::Lighting { state, duration_ms, radius, per_target } => {
            let check = |state: &mut LightState| -> Result<()> {
                state.color = color_of(&state.color)?;
                state.brightness = state.brightness.min(100);
                state.speed = state.speed.clamp(1, 100);
                state.colors = state.colors.min(8);
                Ok(())
            };
            check(state)?;
            if per_target.len() > MAX_PER_TARGET {
                bail!("at most {MAX_PER_TARGET} targets can have their own lighting");
            }
            for p in per_target.iter_mut() {
                p.target = p.target.trim().to_string();
                if p.target.is_empty() || p.target.len() > 256 {
                    bail!("each 'per_target' entry needs a 'target' (a device, zone, fan, group or LED id)");
                }
                check(&mut p.state)?;
            }
            // One entry per target (the last one wins).
            let mut seen = std::collections::HashSet::new();
            let mut kept: Vec<TargetLighting> = per_target.drain(..).rev().filter(|p| seen.insert(p.target.clone())).collect();
            kept.reverse();
            *per_target = kept;
            if app {
                // Shown for as long as the app runs, over the whole targets.
                *duration_ms = None;
                *radius = None;
            } else {
                // No duration: once fired it stays until the trigger is turned off, changed or deleted.
                if let Some(ms) = duration_ms {
                    duration_of(*ms)?;
                }
                radius_of(*radius)?;
            }
        }
        Action::Preset { name } => {
            *name = name.trim().to_string();
            if !scene.profiles.contains_key(name.as_str()) {
                bail!("unknown profile '{name}'");
            }
        }
    }
    Ok(())
}

/// Check an effect's targets: trimmed, known groups, no duplicates.
fn check_targets(ids: &[String], scene: &Scene) -> Result<Vec<String>> {
    if ids.len() > 256 {
        bail!("too many targets");
    }
    let mut targets: Vec<String> = vec![];
    for id in ids {
        let id = id.trim();
        if id.is_empty() || id == "all" {
            bail!("targets must be device, zone or fan ids or \"group:<id>\" (an empty list means all devices)");
        }
        if let Some(g) = id.strip_prefix("group:") {
            if scene.group(g).is_none() {
                bail!("unknown group '{g}'");
            }
        }
        if !targets.iter().any(|x| x == id) {
            targets.push(id.to_string());
        }
    }
    Ok(targets)
}

/// The next free trigger id ("t1", "t2"...).
pub fn next_id(scene: &Scene) -> String {
    let n = scene
        .triggers
        .iter()
        .filter_map(|t| t.id.strip_prefix('t')?.parse::<u32>().ok())
        .max()
        .unwrap_or(0);
    format!("t{}", n + 1)
}

/// Which input the UI needs to listen to: only what an enabled trigger uses, and nothing while
/// the lights are off.
#[derive(Debug, Clone, PartialEq, Eq, Default, Serialize)]
pub struct InputWanted {
    pub keys: bool,
    /// A key trigger lights the pressed key itself: the UI then sends every key's code (only to
    /// place the effect; never kept).
    pub key_codes: bool,
    pub mouse_click: bool,
    pub mouse_move: bool,
    /// Programs `app_running` triggers watch: the UI reports which of these run (and no others).
    pub apps: Vec<String>,
}

pub fn input_wanted(scene: &Scene) -> InputWanted {
    let mut w = InputWanted::default();
    if scene.off {
        return w;
    }
    for t in scene.triggers.iter().filter(|t| t.enabled) {
        match &t.source {
            Source::Key { .. } => {
                w.keys = true;
                w.key_codes |= t.units().any(|(a, _)| a.radius().is_some());
            }
            Source::MouseClick { .. } => w.mouse_click = true,
            Source::MouseMove => w.mouse_move = true,
            Source::AppRunning { app, site, apps } => {
                for key in watch_keys(app, site.as_deref(), apps) {
                    if !w.apps.contains(&key) {
                        w.apps.push(key);
                    }
                }
            }
            _ => {}
        }
    }
    w
}

/// Whether any enabled trigger listens to sound (a sound trigger, or an app's lighting with a
/// sound effect).
pub fn uses_audio(scene: &Scene) -> bool {
    scene.triggers.iter().any(|t| {
        t.enabled
            && (t.source.is_audio()
                || t.units().any(|(a, _)| matches!(a, Action::Lighting { state, per_target, .. }
                    if state.effect.is_audio() || per_target.iter().any(|p| p.state.effect.is_audio()))))
    })
}

/// One input event from the UI.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Input {
    /// A key went down: its virtual-key code (which key triggers match) and its scan code (where it
    /// is on the keyboard, whatever the layout: 0xE0__ for extended keys), if the UI sent them.
    Key(Option<u8>, Option<u16>),
    Click(MouseButton),
    Move,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum Kind {
    Flash,
    Pulse,
    Ripple,
    FadeIn,
    /// A full effect (from `Action::Lighting`), held and then faded out over the last quarter.
    Effect,
}

#[derive(Debug, Clone)]
struct Instance {
    trigger: String,
    /// Which of the trigger's effects (0: its own, then `also`).
    part: usize,
    kind: Kind,
    color: Color,
    start: Duration,
    duration: f32,
    /// Strength at `start` (1, or how far a fade_in glow has built).
    level: f32,
    /// Expanded targets; empty = every device that is not hidden.
    targets: Vec<String>,
    /// The event that fired it (where a local effect starts); None for sound.
    origin: Option<Input>,
    /// Local effect: only this far from the origin (keys / LEDs).
    radius: Option<f32>,
    /// Local effect: seconds each ring around the origin starts after the one inside it.
    delay: f32,
    /// A full effect to show (Kind::Effect).
    state: Option<LightState>,
}

/// How strong an effect of `kind` is at `x` (0..1 through its duration).
fn curve(kind: Kind, x: f32) -> f32 {
    if !(0.0..1.0).contains(&x) {
        return 0.0;
    }
    match kind {
        Kind::Pulse => (std::f32::consts::PI * x).sin(),
        Kind::Ripple => 1.0 - 0.6 * x,
        Kind::Flash | Kind::FadeIn => 1.0 - x,
        Kind::Effect => ((1.0 - x) / 0.25).min(1.0),
    }
}

impl Instance {
    /// Progress through the duration, 0..1 (1 = over).
    fn progress(&self, now: Duration) -> f32 {
        (self.age(now) / self.duration).clamp(0.0, 1.0)
    }

    fn age(&self, now: Duration) -> f32 {
        now.saturating_sub(self.start).as_secs_f32()
    }

    /// Still showing: the outermost ring of a delayed local effect starts last.
    fn alive(&self, now: Duration) -> bool {
        let rings = self.radius.map_or(0.0, |r| r.ceil() + 1.0);
        self.age(now) < self.duration + self.delay * rings
    }

    fn strength(&self, now: Duration) -> f32 {
        self.level * curve(self.kind, self.age(now) / self.duration)
    }
}

/// A bright color of a random hue (for "random color each time").
fn random_color(seed: u64) -> Color {
    // splitmix64: plenty for picking a hue.
    let mut z = seed.wrapping_add(0x9E37_79B9_7F4A_7C15);
    z = (z ^ (z >> 30)).wrapping_mul(0xBF58_476D_1CE4_E5B9);
    z = (z ^ (z >> 27)).wrapping_mul(0x94D0_49BB_1331_11EB);
    z ^= z >> 31;
    let h = (z % 3600) as f32 / 3600.0 * 6.0;
    let x = 1.0 - ((h % 2.0) - 1.0).abs();
    let (r, g, b) = match h as u32 {
        0 => (1.0, x, 0.0),
        1 => (x, 1.0, 0.0),
        2 => (0.0, 1.0, x),
        3 => (0.0, x, 1.0),
        4 => (x, 0.0, 1.0),
        _ => (1.0, 0.0, x),
    };
    let c = |v: f32| (v * 255.0).round() as u8;
    Color::new(c(r), c(g), c(b))
}

/// What handling an event did.
#[derive(Debug, Clone, Default, PartialEq)]
pub struct Fired {
    /// Triggers that fired.
    pub count: usize,
    /// A profile to load (the last `preset` trigger that fired).
    pub preset: Option<String>,
}

/// The triggers' live state (not persisted).
#[derive(Default)]
pub struct Engine {
    active: Vec<Instance>,
    last_fire: BTreeMap<String, Duration>,
    /// audio_level triggers whose band is above their level now.
    above: BTreeMap<String, bool>,
    prev_beat_age: Option<f32>,
    last_move: Option<Duration>,
    /// Programs the UI says are running (only ones `app_running` triggers watch).
    apps: Vec<String>,
    /// Counts fires, for random colors.
    fires: u64,
}

impl Engine {
    /// Whether anything is showing (the render loop then draws every frame).
    pub fn is_active(&self) -> bool {
        !self.active.is_empty() || !self.apps.is_empty()
    }

    /// Which watched programs run now (from the UI). Returns whether that changed.
    pub fn set_apps(&mut self, apps: Vec<String>) -> bool {
        let mut apps: Vec<String> = apps
            .iter()
            .filter_map(|a| match a.split_once('/') {
                // "chrome.exe/youtube.com": the app, on that site.
                Some((app, site)) => {
                    let (app, site) = (app_name(app), site_host(site));
                    (!app.is_empty() && !site.is_empty()).then(|| watch_key(&app, Some(&site)))
                }
                None => Some(app_name(a)).filter(|a| !a.is_empty()),
            })
            .collect();
        apps.sort();
        apps.dedup();
        apps.truncate(64);
        let changed = apps != self.apps;
        self.apps = apps;
        changed
    }

    /// The watched programs running now.
    pub fn running_apps(&self) -> &[String] {
        &self.apps
    }

    /// Play the effects (other than lighting, which shows while the app runs) of the app triggers
    /// whose apps started: none of them ran in `before`, one runs now.
    pub fn apps_started(&mut self, scene: &Scene, before: &[String], now: Duration) -> Fired {
        let mut f = Fired::default();
        if scene.off {
            return f;
        }
        for t in scene.triggers.iter().filter(|t| t.enabled) {
            if let Source::AppRunning { app, site, apps } = &t.source {
                let keys = watch_keys(app, site.as_deref(), apps);
                if keys.iter().any(|k| self.apps.contains(k)) && !keys.iter().any(|k| before.contains(k)) {
                    self.fire(scene, t, now, &mut f, None);
                }
            }
        }
        f
    }

    /// Forget everything live (after a reset, or the lights going off). Which apps run is kept.
    pub fn clear(&mut self) {
        let apps = std::mem::take(&mut self.apps);
        *self = Engine { apps, ..Engine::default() };
    }

    /// Drop what one trigger is showing (it was deleted or disabled).
    pub fn forget(&mut self, id: &str) {
        self.active.retain(|i| i.trigger != id);
        self.last_fire.remove(id);
        self.above.remove(id);
    }

    /// Handle an input event from the UI at `now`.
    pub fn input(&mut self, scene: &Scene, ev: Input, now: Duration) -> Fired {
        let mut f = Fired::default();
        if scene.off {
            return f;
        }
        if ev == Input::Move {
            if self.last_move.is_some_and(|l| now.saturating_sub(l) < MOUSE_MOVE_GAP) {
                return f;
            }
            self.last_move = Some(now);
        }
        for t in scene.triggers.iter().filter(|t| t.enabled) {
            let hit = match (&t.source, ev) {
                (Source::Key { keys: Keys::Any }, Input::Key(..)) => true,
                (Source::Key { keys: Keys::Codes(c) }, Input::Key(Some(vk), _)) => c.contains(&vk),
                (Source::MouseClick { button }, Input::Click(b)) => button.is_none_or(|w| w == b),
                (Source::MouseMove, Input::Move) => true,
                _ => false,
            };
            if hit {
                self.fire(scene, t, now, &mut f, Some(ev));
            }
        }
        f
    }

    /// Check the sound triggers against the levels of this frame.
    pub fn audio(&mut self, scene: &Scene, a: &AudioLevels, now: Duration) -> Fired {
        let mut f = Fired::default();
        // A new beat: `beat` is set and the beat is younger than the one seen last frame.
        let new_beat = a.beat && self.prev_beat_age.is_none_or(|p| a.beat_age < p);
        self.prev_beat_age = Some(a.beat_age);
        if scene.off {
            return f;
        }
        for t in scene.triggers.iter().filter(|t| t.enabled) {
            match &t.source {
                Source::AudioBeat if new_beat => self.fire(scene, t, now, &mut f, None),
                Source::AudioLevel { band, above } => {
                    let v = band.of(a);
                    let was = self.above.get(&t.id).copied().unwrap_or(false);
                    // A little hysteresis so a level hovering at the line does not chatter.
                    let is = if was { v > above * 0.9 } else { v > *above };
                    self.above.insert(t.id.clone(), is);
                    if is && (!was || matches!(t.action, Action::FadeIn { .. })) {
                        self.fire(scene, t, now, &mut f, None);
                    }
                }
                _ => {}
            }
        }
        f
    }

    fn fire(&mut self, scene: &Scene, t: &Trigger, now: Duration, f: &mut Fired, origin: Option<Input>) {
        if self.last_fire.get(&t.id).is_some_and(|l| now.saturating_sub(*l) < MIN_GAP) {
            return;
        }
        self.last_fire.insert(t.id.clone(), now);
        f.count += 1;
        let app = matches!(t.source, Source::AppRunning { .. });
        for (part, (action, targets)) in t.units().enumerate() {
            self.fire_one(scene, &t.id, part, action, targets, app, now, f, origin);
        }
        if self.active.len() > MAX_ACTIVE {
            let extra = self.active.len() - MAX_ACTIVE;
            self.active.drain(..extra);
        }
    }

    /// Play one of a trigger's effects (`part`) on its targets.
    #[allow(clippy::too_many_arguments)]
    fn fire_one(&mut self, scene: &Scene, id: &str, part: usize, action: &Action, targets: &[String], app: bool, now: Duration, f: &mut Fired, origin: Option<Input>) {
        let (kind, color, ms) = match action {
            Action::Preset { name } => {
                f.preset = Some(name.clone());
                return;
            }
            // An app's lighting shows while the app runs (see `overlay`), it isn't fired.
            Action::Lighting { .. } if app => return,
            Action::Lighting { duration_ms, .. } => (Kind::Effect, "000000", duration_ms.unwrap_or(0)),
            Action::Flash { color, duration_ms, .. } => (Kind::Flash, color.as_str(), *duration_ms),
            Action::Pulse { duration_ms, .. } => (Kind::Pulse, "000000", *duration_ms),
            Action::Ripple { color, duration_ms, .. } => (Kind::Ripple, color.as_str(), *duration_ms),
            Action::FadeIn { color, duration_ms, .. } => (Kind::FadeIn, color.as_str(), *duration_ms),
        };
        let radius = action.radius();
        let mut level = 1.0;
        let same = |i: &Instance| i.trigger == id && i.part == part && (radius.is_none() || i.origin == origin);
        // A persistent effect (no duration): one per effect (per key / wheel for a local one); firing
        // again just starts it over there.
        let persistent = kind == Kind::Effect && ms == 0;
        if persistent {
            self.active.retain(|i| !same(i));
        }
        if kind == Kind::FadeIn {
            // One glow per effect (per key, for a glow round the pressed key): each event builds
            // on what is still showing.
            if let Some(k) = self.active.iter().position(same) {
                let old = self.active.remove(k);
                level = (old.strength(now) + FADE_STEP).min(1.0);
            } else {
                level = FADE_STEP;
            }
        }
        self.fires = self.fires.wrapping_add(1);
        let color = if action.random() {
            random_color(self.fires ^ now.as_nanos() as u64)
        } else {
            color.parse().unwrap_or(Color::new(255, 255, 255))
        };
        // A lighting action can show a different effect per target: one instance each.
        let pieces: Vec<(Vec<String>, Option<LightState>)> = match action {
            Action::Lighting { .. } => lightings(action, targets, scene).into_iter().map(|(targets, s)| (targets, Some(s))).collect(),
            _ => vec![(targets.iter().flat_map(|id| scene.expand(id)).collect(), None)],
        };
        for (targets, state) in pieces {
            self.active.push(Instance {
                trigger: id.to_string(),
                part,
                kind,
                color,
                start: now,
                duration: if persistent { f32::INFINITY } else { ms.max(1) as f32 / 1000.0 },
                level,
                targets,
                origin,
                radius,
                delay: if radius.is_some() { action.layer_delay() } else { 0.0 },
                state,
            });
        }
    }

    /// What to paint over this frame (finished instances are dropped): the running apps'
    /// lighting and the fired effects, lowest priority first so the trigger highest in the list
    /// ends up on top (newest on top among one trigger's own).
    pub fn overlay(&mut self, scene: &Scene, now: Duration, audio: &AudioLevels) -> Overlay {
        self.active.retain(|i| i.alive(now));
        let rank = |id: &str| scene.triggers.iter().position(|t| t.id == id).unwrap_or(usize::MAX);
        let mut items: Vec<Item> = vec![];
        if !scene.off {
            for (k, t) in scene.triggers.iter().enumerate().filter(|(_, t)| t.enabled) {
                if let Source::AppRunning { app, site, apps } = &t.source {
                    // Any one of the apps (or sites) it watches: its lighting effects, bottom first.
                    if watch_keys(app, site.as_deref(), apps).iter().any(|k| self.apps.contains(k)) {
                        for (action, targets) in t.units() {
                            for (targets, state) in lightings(action, targets, scene) {
                                items.push(Item { lighting: Some(state), targets, rank: k, ..Item::default() });
                            }
                        }
                    }
                }
            }
        }
        items.extend(self.active.iter().map(|i| Item {
            kind: i.kind,
            color: i.color,
            strength: i.strength(now),
            front: i.progress(now),
            targets: i.targets.clone(),
            origin: i.origin,
            radius: i.radius,
            age: i.age(now),
            duration: i.duration,
            level: i.level,
            delay: i.delay,
            rank: rank(&i.trigger),
            lighting: i.state.clone(),
        }));
        // Stable: lower priority (higher rank) first; app lighting under the fired effects of the
        // same trigger rank.
        items.sort_by(|a, b| b.rank.cmp(&a.rank));
        Overlay { items, t: now.as_secs_f32(), audio: *audio }
    }
}

#[derive(Debug, Clone)]
struct Item {
    kind: Kind,
    color: Color,
    strength: f32,
    /// Ripple: how far the wave has travelled, 0 (centre) .. 1 (ends).
    front: f32,
    targets: Vec<String>,
    origin: Option<Input>,
    radius: Option<f32>,
    /// Seconds since it fired, its duration, starting strength and delay per ring (local effects).
    age: f32,
    duration: f32,
    level: f32,
    delay: f32,
    /// Its trigger's place in the list (0 = highest priority).
    rank: usize,
    /// An app's lighting, shown in full on its targets.
    lighting: Option<LightState>,
}

impl Default for Item {
    fn default() -> Self {
        Item {
            kind: Kind::Flash,
            color: Color::BLACK,
            strength: 1.0,
            front: 0.0,
            targets: vec![],
            origin: None,
            radius: None,
            age: 0.0,
            duration: 1.0,
            level: 1.0,
            delay: 0.0,
            rank: 0,
            lighting: None,
        }
    }
}

/// OpenRGB's name for the key with this Windows virtual-key code (US/ISO layouts), without
/// the "Key: " prefix. Several names where layouts differ.
fn key_names(vk: u8) -> &'static [&'static str] {
    match vk {
        0x08 => &["Backspace"],
        0x09 => &["Tab"],
        0x0D => &["Enter", "Enter (ISO)"],
        0x10 | 0xA0 => &["Left Shift"],
        0xA1 => &["Right Shift"],
        0x11 | 0xA2 => &["Left Control"],
        0xA3 => &["Right Control"],
        0x12 | 0xA4 => &["Left Alt"],
        0xA5 => &["Right Alt"],
        0x13 => &["Pause/Break"],
        0x14 => &["Caps Lock"],
        0x1B => &["Escape"],
        0x20 => &["Space"],
        0x21 => &["Page Up"],
        0x22 => &["Page Down"],
        0x23 => &["End"],
        0x24 => &["Home"],
        0x25 => &["Left Arrow"],
        0x26 => &["Up Arrow"],
        0x27 => &["Right Arrow"],
        0x28 => &["Down Arrow"],
        0x2C => &["Print Screen"],
        0x2D => &["Insert"],
        0x2E => &["Delete"],
        0x30 => &["0"], 0x31 => &["1"], 0x32 => &["2"], 0x33 => &["3"], 0x34 => &["4"],
        0x35 => &["5"], 0x36 => &["6"], 0x37 => &["7"], 0x38 => &["8"], 0x39 => &["9"],
        0x41 => &["A"], 0x42 => &["B"], 0x43 => &["C"], 0x44 => &["D"], 0x45 => &["E"], 0x46 => &["F"],
        0x47 => &["G"], 0x48 => &["H"], 0x49 => &["I"], 0x4A => &["J"], 0x4B => &["K"], 0x4C => &["L"],
        0x4D => &["M"], 0x4E => &["N"], 0x4F => &["O"], 0x50 => &["P"], 0x51 => &["Q"], 0x52 => &["R"],
        0x53 => &["S"], 0x54 => &["T"], 0x55 => &["U"], 0x56 => &["V"], 0x57 => &["W"], 0x58 => &["X"],
        0x59 => &["Y"], 0x5A => &["Z"],
        0x5B => &["Left Windows"],
        0x5C => &["Right Windows"],
        0x5D => &["Menu", "Context"],
        0x60 => &["Number Pad 0"], 0x61 => &["Number Pad 1"], 0x62 => &["Number Pad 2"], 0x63 => &["Number Pad 3"],
        0x64 => &["Number Pad 4"], 0x65 => &["Number Pad 5"], 0x66 => &["Number Pad 6"], 0x67 => &["Number Pad 7"],
        0x68 => &["Number Pad 8"], 0x69 => &["Number Pad 9"],
        0x6A => &["Number Pad *"],
        0x6B => &["Number Pad +"],
        0x6D => &["Number Pad -"],
        0x6E => &["Number Pad ."],
        0x6F => &["Number Pad /"],
        0x70 => &["F1"], 0x71 => &["F2"], 0x72 => &["F3"], 0x73 => &["F4"], 0x74 => &["F5"], 0x75 => &["F6"],
        0x76 => &["F7"], 0x77 => &["F8"], 0x78 => &["F9"], 0x79 => &["F10"], 0x7A => &["F11"], 0x7B => &["F12"],
        0x90 => &["Num Lock"],
        0x91 => &["Scroll Lock"],
        0xAD => &["Media Mute"],
        0xAE => &["Media Volume -"],
        0xAF => &["Media Volume +"],
        0xB0 => &["Media Next"],
        0xB1 => &["Media Previous"],
        0xB2 => &["Media Stop"],
        0xB3 => &["Media Play/Pause"],
        0xBA => &[";"],
        0xBB => &["="],
        0xBC => &[","],
        0xBD => &["-"],
        0xBE => &["."],
        0xBF => &["/"],
        0xC0 => &["`"],
        0xDB => &["["],
        0xDC => &["\\ (ANSI)", "\\"],
        0xDD => &["]"],
        0xDE => &["'"],
        0xE2 => &["\\ (ISO)"],
        _ => &[],
    }
}

/// OpenRGB's name for the key at this scan code (set 1; 0xE0__ for extended keys), by its place on a
/// US / ISO keyboard, which is how OpenRGB names keys. Empty for codes whose key is better found by
/// its virtual-key code (Pause, Num Lock).
fn scan_names(sc: u16) -> &'static [&'static str] {
    match sc {
        0x01 => &["Escape"],
        0x02 => &["1"], 0x03 => &["2"], 0x04 => &["3"], 0x05 => &["4"], 0x06 => &["5"],
        0x07 => &["6"], 0x08 => &["7"], 0x09 => &["8"], 0x0A => &["9"], 0x0B => &["0"],
        0x0C => &["-"],
        0x0D => &["="],
        0x0E => &["Backspace"],
        0x0F => &["Tab"],
        0x10 => &["Q"], 0x11 => &["W"], 0x12 => &["E"], 0x13 => &["R"], 0x14 => &["T"],
        0x15 => &["Y"], 0x16 => &["U"], 0x17 => &["I"], 0x18 => &["O"], 0x19 => &["P"],
        0x1A => &["["],
        0x1B => &["]"],
        0x1C => &["Enter", "Enter (ISO)"],
        0x1D => &["Left Control"],
        0x1E => &["A"], 0x1F => &["S"], 0x20 => &["D"], 0x21 => &["F"], 0x22 => &["G"],
        0x23 => &["H"], 0x24 => &["J"], 0x25 => &["K"], 0x26 => &["L"],
        0x27 => &[";"],
        0x28 => &["'"],
        0x29 => &["`"],
        0x2A => &["Left Shift"],
        0x2B => &["\\ (ANSI)", "\\", "# (ISO)", "#"],
        0x2C => &["Z"], 0x2D => &["X"], 0x2E => &["C"], 0x2F => &["V"], 0x30 => &["B"],
        0x31 => &["N"], 0x32 => &["M"],
        0x33 => &[","],
        0x34 => &["."],
        0x35 => &["/"],
        0x36 => &["Right Shift"],
        0x37 => &["Number Pad *"],
        0x38 => &["Left Alt"],
        0x39 => &["Space"],
        0x3A => &["Caps Lock"],
        0x3B => &["F1"], 0x3C => &["F2"], 0x3D => &["F3"], 0x3E => &["F4"], 0x3F => &["F5"],
        0x40 => &["F6"], 0x41 => &["F7"], 0x42 => &["F8"], 0x43 => &["F9"], 0x44 => &["F10"],
        0x46 => &["Scroll Lock"],
        0x47 => &["Number Pad 7"],
        0x48 => &["Number Pad 8"],
        0x49 => &["Number Pad 9"],
        0x4A => &["Number Pad -"],
        0x4B => &["Number Pad 4"],
        0x4C => &["Number Pad 5"],
        0x4D => &["Number Pad 6"],
        0x4E => &["Number Pad +"],
        0x4F => &["Number Pad 1"],
        0x50 => &["Number Pad 2"],
        0x51 => &["Number Pad 3"],
        0x52 => &["Number Pad 0"],
        0x53 => &["Number Pad ."],
        0x56 => &["\\ (ISO)"],
        0x57 => &["F11"],
        0x58 => &["F12"],
        0xE010 => &["Media Previous"],
        0xE019 => &["Media Next"],
        0xE01C => &["Number Pad Enter"],
        0xE01D => &["Right Control"],
        0xE020 => &["Media Mute"],
        0xE022 => &["Media Play/Pause"],
        0xE024 => &["Media Stop"],
        0xE02E => &["Media Volume -"],
        0xE030 => &["Media Volume +"],
        0xE035 => &["Number Pad /"],
        0xE037 => &["Print Screen"],
        0xE038 => &["Right Alt"],
        0xE047 => &["Home"],
        0xE048 => &["Up Arrow"],
        0xE049 => &["Page Up"],
        0xE04B => &["Left Arrow"],
        0xE04D => &["Right Arrow"],
        0xE04F => &["End"],
        0xE050 => &["Down Arrow"],
        0xE051 => &["Page Down"],
        0xE052 => &["Insert"],
        0xE053 => &["Delete"],
        0xE05B => &["Left Windows"],
        0xE05C => &["Right Windows"],
        0xE05D => &["Menu", "Context"],
        _ => &[],
    }
}

/// The LED a local effect starts from on this device: the pressed key (by its name), the mouse
/// wheel for mouse events, else the middle of the device.
fn origin_led(d: &DeviceInfo, origin: Option<Input>, n: usize) -> usize {
    let names = &d.led_map.names;
    let find = |pred: &dyn Fn(&str) -> bool| names.iter().take(n).position(|s| pred(s));
    let key = |s: &str| s.strip_prefix("Key: ").unwrap_or(s).to_string();
    let found = match origin {
        // The key's place first (its scan code: right Shift, the number pad without Num Lock and
        // other layouts all stay where they are), then its name from the virtual-key code.
        Some(Input::Key(vk, sc)) => sc
            .map(scan_names)
            .into_iter()
            .chain(vk.map(key_names))
            .flatten()
            .find_map(|want| find(&|s| key(s).eq_ignore_ascii_case(want))),
        Some(Input::Click(_)) | Some(Input::Move) => {
            find(&|s| {
                let s = s.to_ascii_lowercase();
                s.contains("wheel") || s.contains("scroll")
            })
            .or_else(|| find(&|s| s.to_ascii_lowercase().contains("logo")))
        }
        _ => None,
    };
    found.unwrap_or(n / 2)
}

/// How far LED `i` is from LED `o`: on the key grid when both have a place, else along the LED order.
fn led_distance(d: &DeviceInfo, i: usize, o: usize) -> f32 {
    match (d.led_map.pos.get(i).copied().flatten(), d.led_map.pos.get(o).copied().flatten()) {
        (Some((x1, y1)), Some((x2, y2))) => ((x1 - x2).powi(2) + (y1 - y2).powi(2)).sqrt(),
        _ => (i as f32 - o as f32).abs(),
    }
}

/// The trigger instances of one frame, oldest first.
#[derive(Debug, Clone, Default)]
pub struct Overlay {
    items: Vec<Item>,
    /// Scene time and sound, for the effects of an app's lighting.
    t: f32,
    audio: AudioLevels,
}

fn mix(a: Color, b: Color, w: f32) -> Color {
    let w = w.clamp(0.0, 1.0);
    let m = |x: u8, y: u8| (x as f32 + (y as f32 - x as f32) * w).round() as u8;
    Color::new(m(a.r, b.r), m(a.g, b.g), m(a.b, b.b))
}

/// The same color at full brightness (black stays black).
fn boost(c: Color) -> Color {
    let m = c.r.max(c.g).max(c.b);
    if m == 0 {
        return c;
    }
    let f = 255.0 / m as f32;
    let s = |v: u8| (v as f32 * f).round().min(255.0) as u8;
    Color::new(s(c.r), s(c.g), s(c.b))
}

/// The most specific target id of every LED of a device (fan, else zone, else the device), in
/// the LED order `device_frame` uses.
fn led_paths(d: &DeviceInfo, scene: &Scene, n: usize) -> Vec<String> {
    let mut out = Vec::with_capacity(n);
    for z in &d.zones {
        let layout = scene.layouts.get(&z.id).filter(|l| l.fans > 0 && l.leds_per_fan > 0);
        for j in 0..z.leds as usize {
            if out.len() >= n {
                return out;
            }
            match layout {
                Some(l) if j / (l.leds_per_fan as usize) < l.fans as usize => {
                    out.push(format!("{}/{}", z.id, j / l.leds_per_fan as usize))
                }
                _ => out.push(z.id.clone()),
            }
        }
    }
    out.resize(n, d.id.clone());
    out
}

fn covers(target: &str, path: &str) -> bool {
    path == target || (path.len() > target.len() && path.starts_with(target) && path.as_bytes()[target.len()] == b'/')
}

impl Overlay {
    pub fn is_empty(&self) -> bool {
        self.items.is_empty()
    }

    /// Paint the instances over one device's frame. Hidden devices and lights that are off are
    /// left alone.
    pub fn apply(&self, d: &DeviceInfo, scene: &Scene, frame: &mut [Color]) {
        if self.items.is_empty() || frame.is_empty() || scene.off || scene.hidden.contains(&d.id) {
            return;
        }
        let n = frame.len();
        let mut paths: Option<Vec<String>> = None;
        let centre = (n as f32 - 1.0) / 2.0;
        for it in &self.items {
            // A delayed local effect can still be on its way to the outer rings.
            if it.strength <= 0.0 && it.lighting.is_none() && !(it.delay > 0.0 && it.radius.is_some()) {
                continue;
            }
            // Which LEDs this instance covers: all, none, or those under a zone/fan target, plus single
            // LEDs ("<device>@<index>"; "#" can be part of a device id: "openrgb:ENE DRAM#2").
            // (Runs every frame for every device: nothing is allocated for whole-device targets, and
            // zone paths are only built when a zone / fan target needs them.)
            let whole = it.targets.is_empty() || it.targets.iter().any(|t| *t == d.id);
            let under = |t: &str, sep: u8| t.len() > d.id.len() && t.starts_with(d.id.as_str()) && t.as_bytes()[d.id.len()] == sep;
            let single: Vec<usize> = if whole {
                Vec::new()
            } else {
                it.targets.iter().filter(|t| under(t, b'@')).filter_map(|t| t[d.id.len() + 1..].parse().ok()).collect()
            };
            let zoned = !whole && it.targets.iter().any(|t| under(t, b'/'));
            if !whole && single.is_empty() && !zoned {
                continue;
            }
            let paths = if zoned { Some(paths.get_or_insert_with(|| led_paths(d, scene, n))) } else { None };
            // A local effect: where it starts on this device.
            let origin = it.radius.map(|_| origin_led(d, it.origin, n));
            for (i, c) in frame.iter_mut().enumerate() {
                if !whole && !single.contains(&i) && !paths.as_ref().is_some_and(|p| it.targets.iter().any(|t| covers(t, &p[i]))) {
                    continue;
                }

                let w = match (it.radius, origin) {
                    (Some(r), Some(o)) if it.delay > 0.0 => {
                        // Ring by ring: ring k (k keys / LEDs out) starts k delays after the key.
                        let dist = led_distance(d, i, o);
                        let ring = dist.round();
                        if ring > r.round() {
                            0.0
                        } else {
                            it.level * curve(it.kind, (it.age - ring * it.delay) / it.duration)
                        }
                    }
                    (Some(r), Some(o)) => {
                        let dist = led_distance(d, i, o);
                        if it.kind == Kind::Ripple && r > 0.0 {
                            // A ring growing from the key out to the radius; the key itself stays lit.
                            let ring = (1.0 - (dist - it.front * r).abs() / 1.2).max(0.0);
                            let key = if dist < 0.5 { 1.0 } else { 0.0 };
                            it.strength * ring.max(key * (1.0 - it.front))
                        } else {
                            // Full inside the radius, fading out over one more key.
                            it.strength * (1.0 - (dist - r).max(0.0)).clamp(0.0, 1.0)
                        }
                    }
                    _ => match it.kind {
                    Kind::Ripple => {
                        let dist = if centre > 0.0 { (i as f32 - centre).abs() / centre } else { 0.0 };
                        let front = it.front * (1.0 + RIPPLE_WIDTH);
                        it.strength * (1.0 - (dist - front).abs() / RIPPLE_WIDTH).max(0.0)
                    }
                    _ => it.strength,
                    },
                };
                if w <= 0.0 {
                    continue;
                }
                // An effect (an app's lighting, or one played after an event) is rendered here; the
                // others paint their color, or brighten what is there (pulse).
                let over = match (&it.lighting, it.kind) {
                    (Some(state), _) => effects::render_with(state, self.t, i, n, &self.audio),
                    (None, Kind::Pulse) => boost(*c),
                    (None, _) => it.color,
                };
                *c = mix(*c, over, w);
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    impl Engine {
        /// The overlay with no scene ranks and silence (most tests need only the instances).
        fn overlay_at(&mut self, now: Duration) -> Overlay {
            self.overlay(&Scene::default(), now, &AudioLevels::SILENT)
        }
    }
    use crate::rpc::ZoneInfo;
    use crate::scene::{FanLayout, Group, Lighting};
    use lightsync_core::DeviceKind;
    use serde_json::json;

    fn ms(v: u64) -> Duration {
        Duration::from_millis(v)
    }

    fn trig(v: serde_json::Value) -> Trigger {
        serde_json::from_value(v).unwrap()
    }

    fn scene_with(triggers: Vec<Trigger>) -> Scene {
        Scene { triggers, ..Scene::default() }
    }

    fn dev(n: usize) -> DeviceInfo {
        DeviceInfo {
            id: "openrgb:Strip".into(),
            name: "Strip".into(),
            kind: DeviceKind::Other,
            provider: "openrgb".into(),
            led_count: n,
            openrgb_index: Some(0),
            zones: vec![
                ZoneInfo { id: "openrgb:Strip/A".into(), index: 0, name: "A".into(), leds: (n / 2) as u32, resizable: true, max_leds: 100, fans: vec![] },
                ZoneInfo { id: "openrgb:Strip/B".into(), index: 1, name: "B".into(), leds: (n - n / 2) as u32, resizable: true, max_leds: 100, fans: vec![] },
            ],
            hidden: false,
            led_map: Default::default(),
            slot: (0, 1),
        }
    }

    /// A 3 x 3 keyboard: Q W E / A S D / Z X C, one LED per key on a grid.
    fn keyboard() -> DeviceInfo {
        let keys = ["Q", "W", "E", "A", "S", "D", "Z", "X", "C"];
        DeviceInfo {
            id: "openrgb:Kb".into(),
            name: "Kb".into(),
            kind: DeviceKind::Keyboard,
            provider: "openrgb".into(),
            led_count: 9,
            openrgb_index: Some(1),
            zones: vec![ZoneInfo { id: "openrgb:Kb/Keys".into(), index: 0, name: "Keys".into(), leds: 9, resizable: false, max_leds: 9, fans: vec![] }],
            hidden: false,
            led_map: crate::rpc::LedMap {
                names: keys.iter().map(|k| format!("Key: {k}")).collect(),
                pos: (0..9).map(|i| Some(((i % 3) as f32, (i / 3) as f32))).collect(),
            },
            slot: (0, 1),
        }
    }

    #[test]
    fn the_pressed_key_is_found_by_its_place() {
        // The scan code wins over the virtual-key code: on an AZERTY layout the key at the "Q" place
        // sends VK 'A' (and scan code 0x10, the Q place); the Q LED must light.
        let kb = keyboard();
        assert_eq!(origin_led(&kb, Some(Input::Key(Some(b'A'), Some(0x10))), 9), 0, "Q, by its place");
        assert_eq!(origin_led(&kb, Some(Input::Key(Some(b'A'), None)), 9), 3, "no scan code: by its name");
        assert_eq!(origin_led(&kb, Some(Input::Key(None, Some(0x1F))), 9), 4, "S");
        // An unknown scan code falls back to the virtual-key code, then the middle.
        assert_eq!(origin_led(&kb, Some(Input::Key(Some(b'D'), Some(0x7F))), 9), 5);
        assert_eq!(origin_led(&kb, Some(Input::Key(None, Some(0x7F))), 9), 4);
        // Right-hand and number pad keys by their own place.
        assert_eq!(scan_names(0x36), ["Right Shift"]);
        assert_eq!(scan_names(0xE01D), ["Right Control"]);
        assert_eq!(scan_names(0x47), ["Number Pad 7"]);
        assert_eq!(scan_names(0xE047), ["Home"]);
        assert_eq!(scan_names(0xE01C), ["Number Pad Enter"]);
    }

    #[test]
    fn rings_light_one_after_another_and_random_colors_vary() {
        // Radius 2, 100 ms per ring, 300 ms each: S at once, its neighbours at 100 ms, corners (ring 1
        // on the grid's diagonal is 1.4 -> ring 1) too; Q..C all within ring 2.
        let t = trig(json!({"id":"t1","name":"k","source":{"kind":"key","keys":"any"},"action":{"kind":"flash","color":"ff0000","duration_ms":300,"radius":1,"layer_delay_ms":100}}));
        let s = scene_with(vec![t]);
        let mut e = Engine::default();
        e.input(&s, Input::Key(Some(b'S'), None), ms(0));
        let lit_at = |e: &mut Engine, at: u64| {
            let mut frame = vec![Color::BLACK; 9];
            e.overlay(&s, ms(at), &AudioLevels::SILENT).apply(&keyboard(), &s, &mut frame);
            frame.iter().map(|c| c.r > 0).collect::<Vec<_>>()
        };
        assert_eq!(lit_at(&mut e, 0), [false, false, false, false, true, false, false, false, false], "only the key at first");
        assert_eq!(lit_at(&mut e, 150), [true, true, true, true, true, true, true, true, true], "then the ring around it");
        assert_eq!(lit_at(&mut e, 350), [true, true, true, true, false, true, true, true, true], "the key has faded, the ring not yet");
        assert!(lit_at(&mut e, 450).iter().all(|l| !l), "all done");
        // Random colors: each press a (probably) different bright color.
        let t = trig(json!({"id":"t1","name":"k","source":{"kind":"key","keys":"any"},"action":{"kind":"flash","color":"ff0000","duration_ms":1000,"random":true}}));
        let s = scene_with(vec![t]);
        let mut e = Engine::default();
        let mut seen = std::collections::BTreeSet::new();
        for k in 0..8u64 {
            e.input(&s, Input::Key(None, None), ms(100 * k));
            let mut frame = vec![Color::BLACK; 1];
            e.overlay(&s, ms(100 * k), &AudioLevels::SILENT).apply(&dev(1), &s, &mut frame);
            seen.insert((frame[0].r, frame[0].g, frame[0].b));
        }
        assert!(seen.len() >= 4, "{seen:?}");
    }

    #[test]
    fn app_lighting_and_priority() {
        let app = |id: &str, color: &str| trig(json!({"id":id,"name":format!("game {id}"),"source":{"kind":"app_running","app":"C:\\Games\\Game.EXE"},"action":{"kind":"lighting","effect":"static","color":color,"brightness":100,"speed":50}}));
        let ok = validate(app("", "00FF00"), &Scene::default()).unwrap();
        assert_eq!(ok.source, Source::AppRunning { app: "game.exe".into(), site: None, apps: vec![] });
        // Any effect goes with an app (it plays when the app starts); it lights its whole targets.
        let pulse = validate(trig(json!({"name":"x","source":{"kind":"app_running","app":"a.exe"},"action":{"kind":"pulse","duration_ms":200,"radius":2}})), &Scene::default()).unwrap();
        assert_eq!(pulse.action.radius(), None);
        let mut s = scene_with(vec![validate(app("t1", "00ff00"), &Scene::default()).unwrap(), validate(app("t2", "0000ff"), &Scene::default()).unwrap()]);
        assert_eq!(input_wanted(&s).apps, ["game.exe"]);
        let mut e = Engine::default();
        let paint = |e: &mut Engine, s: &Scene| {
            let mut frame = vec![Color::BLACK; 2];
            e.overlay(s, ms(0), &AudioLevels::SILENT).apply(&dev(2), s, &mut frame);
            frame[0]
        };
        assert_eq!(paint(&mut e, &s), Color::BLACK, "nothing while the app isn't running");
        assert!(e.set_apps(vec!["GAME.exe".into()]));
        assert!(e.is_active());
        // t1 is first in the list, so it wins over t2.
        assert_eq!(paint(&mut e, &s), Color::new(0, 255, 0));
        s.triggers.swap(0, 1);
        assert_eq!(paint(&mut e, &s), Color::new(0, 0, 255));
        // A flash from a lower trigger stays under a higher one's lighting.
        s.triggers.push(trig(json!({"id":"t3","name":"f","source":{"kind":"mouse_move"},"action":{"kind":"flash","color":"ff0000","duration_ms":1000}})));
        e.input(&s, Input::Move, ms(0));
        assert_eq!(paint(&mut e, &s), Color::new(0, 0, 255));
        s.triggers.rotate_right(1); // t3 first now
        assert_eq!(paint(&mut e, &s), Color::new(255, 0, 0));
        assert!(e.set_apps(vec![]));
        assert!(!e.set_apps(vec![]));
    }

    #[test]
    fn one_trigger_any_effects_on_their_own_devices() {
        // Red static on every device, a green flash on A on top; an app's flash plays when it starts.
        let t = validate(
            trig(json!({"id":"t1","name":"mix","source":{"kind":"mouse_move"},"targets":[],
                "action":{"kind":"lighting","effect":"static","color":"FF0000","brightness":100,"speed":50,"duration_ms":1000},
                "also":[{"targets":["openrgb:Strip/A"],"action":{"kind":"flash","color":"00ff00","duration_ms":1000}}]})),
            &Scene::default(),
        )
        .unwrap();
        assert_eq!(t.also.len(), 1);
        assert!(!t.also[0].with_previous);
        // "with_previous" (the app's grouping) is kept as saved.
        let grouped: Part = serde_json::from_value(json!({"action":{"kind":"pulse","duration_ms":200},"targets":["d"],"with_previous":true})).unwrap();
        assert!(grouped.with_previous);
        assert_eq!(serde_json::to_value(&grouped).unwrap()["with_previous"], json!(true));
        let s = scene_with(vec![t]);
        let mut e = Engine::default();
        e.input(&s, Input::Move, ms(0));
        let mut frame = vec![Color::BLACK; 4];
        e.overlay(&s, ms(1), &AudioLevels::SILENT).apply(&dev(4), &s, &mut frame);
        assert!(frame[0].g > 200 && frame[0].r < 60, "A flashes green: {:?}", frame[0]);
        assert_eq!(frame[3], Color::new(255, 0, 0), "the rest is red");

        let app = validate(
            trig(json!({"id":"t2","name":"game","source":{"kind":"app_running","app":"game.exe"},
                "action":{"kind":"flash","color":"0000ff","duration_ms":1000}})),
            &Scene::default(),
        )
        .unwrap();
        let s = scene_with(vec![app]);
        let mut e = Engine::default();
        let before = e.running_apps().to_vec();
        assert!(e.set_apps(vec!["game.exe".into()]));
        assert_eq!(e.apps_started(&s, &before, ms(0)).count, 1);
        // Still running: no new flash.
        let before = e.running_apps().to_vec();
        assert_eq!(e.apps_started(&s, &before, ms(500)).count, 0);
        let mut frame = vec![Color::BLACK; 4];
        e.overlay(&s, ms(1), &AudioLevels::SILENT).apply(&dev(4), &s, &mut frame);
        assert!(frame[0].b > 200, "flash on start: {:?}", frame[0]);
    }

    #[test]
    fn one_trigger_a_different_effect_per_device() {
        // Targets A and B (zones standing in for two devices); red for any without its own.
        let t = |targets: serde_json::Value, per_target: serde_json::Value| {
            validate(
                trig(json!({"id":"t1","name":"split","source":{"kind":"mouse_move"},"targets":targets,
                    "action":{"kind":"lighting","effect":"static","color":"FF0000","brightness":100,"speed":50,"per_target":per_target}})),
                &Scene::default(),
            )
            .unwrap()
        };
        let paint = |trigger: Trigger| {
            let s = scene_with(vec![trigger]);
            let mut e = Engine::default();
            e.input(&s, Input::Move, ms(0));
            let mut frame = vec![Color::BLACK; 4];
            e.overlay(&s, ms(10), &AudioLevels::SILENT).apply(&dev(4), &s, &mut frame);
            frame
        };
        let (red, green, blue) = (Color::new(255, 0, 0), Color::new(0, 255, 0), Color::new(0, 0, 255));
        let ab = json!(["openrgb:Strip/A", "openrgb:Strip/B"]);
        let a_green = json!({"target":"openrgb:Strip/A","effect":"static","color":"00ff00","brightness":100,"speed":50});
        let b_blue = json!({"target":"openrgb:Strip/B","effect":"static","color":"0000ff","brightness":100,"speed":50});
        // Each its own: A green, B blue.
        assert_eq!(paint(t(ab.clone(), json!([a_green, b_blue]))), [green, green, blue, blue]);
        // Only A has its own: B keeps the trigger's red.
        assert_eq!(paint(t(ab.clone(), json!([a_green]))), [green, green, red, red]);
        // "All devices": red everywhere, A's own on top.
        assert_eq!(paint(t(json!([]), json!([a_green]))), [green, green, red, red]);
        // Bad entries are refused; one entry per target (the last wins).
        let bad = trig(json!({"name":"x","source":{"kind":"mouse_move"},"action":{"kind":"lighting","effect":"static","color":"ff0000","per_target":[{"target":"","effect":"static"}]}}));
        assert!(validate(bad, &Scene::default()).is_err());
        let twice = t(ab.clone(), json!([a_green, {"target":"openrgb:Strip/A","effect":"static","color":"0000ff"}]));
        assert!(matches!(&twice.action, Action::Lighting { per_target, .. } if per_target.len() == 1 && per_target[0].state.color == "0000ff"));
        // A sound effect on one device's own lighting turns sound capture on.
        let sound = t(ab, json!([{"target":"openrgb:Strip/A","effect":"sound_beat"}]));
        assert!(uses_audio(&scene_with(vec![sound])));
    }

    #[test]
    fn several_apps_and_sites_any_of_them() {
        let t = |source: serde_json::Value| {
            trig(json!({"id":"t1","name":"games","source":source,"action":{"kind":"lighting","effect":"static","color":"00ff00","brightness":100,"speed":50}}))
        };
        // Apps and sites together; normalised, duplicates dropped; the first stays in app / site.
        let ok = validate(
            t(json!({"kind":"app_running","app":"C:\\Games\\Dota2.exe","apps":[
                {"app":"cs2"},{"app":"chrome.exe","site":"https://www.youtube.com/watch?v=1"},{"app":"DOTA2.EXE"},{"app":"msedge.exe","site":" "}
            ]})),
            &Scene::default(),
        )
        .unwrap();
        let Source::AppRunning { app, site, apps } = &ok.source else { panic!("{:?}", ok.source) };
        assert_eq!((app.as_str(), site), ("dota2.exe", &None));
        assert_eq!(apps, &[
            AppWatch { app: "cs2.exe".into(), site: None },
            AppWatch { app: "chrome.exe".into(), site: Some("youtube.com".into()) },
            AppWatch { app: "msedge.exe".into(), site: None },
        ]);
        // Only the list (no first app) works too; bad entries are refused; at most 16.
        assert!(validate(t(json!({"kind":"app_running","app":"","apps":[{"app":"game.exe"}]})), &Scene::default()).is_ok());
        assert!(validate(t(json!({"kind":"app_running","app":"","apps":[]})), &Scene::default()).is_err());
        assert!(validate(t(json!({"kind":"app_running","app":"a.exe","apps":[{"app":"chrome.exe","site":"not a site"}]})), &Scene::default()).is_err());
        let many: Vec<serde_json::Value> = (0..16).map(|k| json!({"app": format!("game{k}.exe")})).collect();
        assert!(validate(t(json!({"kind":"app_running","app":"first.exe","apps":many})), &Scene::default()).is_err());
        // Asked for each; lit while any one holds.
        let s = scene_with(vec![ok]);
        assert_eq!(input_wanted(&s).apps, ["dota2.exe", "cs2.exe", "chrome.exe/youtube.com", "msedge.exe"]);
        let mut e = Engine::default();
        let lit = |e: &mut Engine| {
            let mut frame = vec![Color::BLACK; 2];
            e.overlay(&s, ms(0), &AudioLevels::SILENT).apply(&dev(2), &s, &mut frame);
            frame[0] != Color::BLACK
        };
        assert!(!lit(&mut e));
        for running in [vec!["cs2.exe"], vec!["chrome.exe/youtube.com"], vec!["dota2.exe", "cs2.exe"]] {
            e.set_apps(running.iter().map(|s| s.to_string()).collect());
            assert!(lit(&mut e), "{running:?}");
        }
        e.set_apps(vec!["chrome.exe".into()]);
        assert!(!lit(&mut e), "chrome without youtube");
    }

    #[test]
    fn app_on_a_web_site() {
        // Only the host is kept: no scheme, www., port, path or query.
        assert_eq!(site_host("https://www.YouTube.com/watch?v=1"), "youtube.com");
        assert_eq!(site_host("music.youtube.com/playlist"), "music.youtube.com");
        assert_eq!(site_host("http://user@example.org:8080/a#b"), "example.org");
        for bad in ["", "youtube", "not a site.com", "http://", "a..b.com", "-x.com"] {
            assert_eq!(site_host(bad), "", "{bad}");
        }
        let site = |id: &str, s: &str| {
            trig(json!({"id":id,"name":"yt","source":{"kind":"app_running","app":"chrome.exe","site":s},"action":{"kind":"lighting","effect":"static","color":"ff0000","brightness":100,"speed":50}}))
        };
        let t = validate(site("t1", "https://www.YouTube.com/watch?v=1"), &Scene::default()).unwrap();
        assert_eq!(t.source, Source::AppRunning { app: "chrome.exe".into(), site: Some("youtube.com".into()), apps: vec![] });
        assert!(validate(site("t2", "not a site"), &Scene::default()).is_err());
        let blank = validate(site("t3", " "), &Scene::default()).unwrap();
        assert_eq!(blank.source, Source::AppRunning { app: "chrome.exe".into(), site: None, apps: vec![] });
        // The UI is asked for the app on the site, and lights only while that holds.
        let s = scene_with(vec![t]);
        assert_eq!(input_wanted(&s).apps, ["chrome.exe/youtube.com"]);
        let mut e = Engine::default();
        let lit = |e: &mut Engine| {
            let mut frame = vec![Color::BLACK; 2];
            e.overlay(&s, ms(0), &AudioLevels::SILENT).apply(&dev(2), &s, &mut frame);
            frame[0] != Color::BLACK
        };
        e.set_apps(vec!["chrome.exe".into()]);
        assert!(!lit(&mut e), "the browser alone is not enough");
        e.set_apps(vec!["Chrome.exe/www.YouTube.com".into()]);
        assert!(lit(&mut e));
        e.set_apps(vec!["chrome.exe/github.com".into()]);
        assert!(!lit(&mut e));
    }

    #[test]
    fn any_effect_after_an_event() {
        // A click plays a rainbow wave for a second on the strip, over whatever it shows, then fades.
        let t = trig(json!({"id":"t1","name":"r","source":{"kind":"mouse_click"},"action":{"kind":"lighting","effect":"rainbow_wave","color":"ffffff","brightness":100,"speed":50,"duration_ms":1000}}));
        let s = scene_with(vec![validate(t, &Scene::default()).unwrap()]);
        let mut e = Engine::default();
        e.input(&s, Input::Click(MouseButton::Left), ms(0));
        let at = |e: &mut Engine, when: u64| {
            let mut frame = vec![Color::BLACK; 8];
            e.overlay(&s, ms(when), &AudioLevels::SILENT).apply(&dev(8), &s, &mut frame);
            frame
        };
        let f = at(&mut e, 100);
        let distinct: std::collections::BTreeSet<_> = f.iter().map(|c| (c.r, c.g, c.b)).collect();
        assert!(distinct.len() >= 4, "a rainbow across the LEDs: {f:?}");
        let held = f.iter().map(|c| c.r as u32 + c.g as u32 + c.b as u32).sum::<u32>();
        let fading = at(&mut e, 900).iter().map(|c| c.r as u32 + c.g as u32 + c.b as u32).sum::<u32>();
        assert!(fading < held / 2, "fades over the last quarter: {held} -> {fading}");
        assert!(at(&mut e, 1100).iter().all(|c| *c == Color::BLACK), "over");
        // A duration is needed after an event (only an app's lighting has none).
        // No duration: it stays on until the trigger is turned off or removed.
        let t = trig(json!({"id":"t1","name":"x","source":{"kind":"mouse_move"},"action":{"kind":"lighting","effect":"static","color":"00ff00"}}));
        let s = scene_with(vec![validate(t, &Scene::default()).unwrap()]);
        let mut e = Engine::default();
        e.input(&s, Input::Move, ms(0));
        e.input(&s, Input::Move, ms(500));
        let mut frame = vec![Color::BLACK; 2];
        e.overlay(&s, ms(3_600_000), &AudioLevels::SILENT).apply(&dev(2), &s, &mut frame);
        assert_eq!(frame, vec![Color::new(0, 255, 0); 2], "still on an hour later");
        assert_eq!(e.active.len(), 1, "one per trigger, not one per event");
        e.forget("t1");
        let mut frame = vec![Color::BLACK; 2];
        e.overlay(&s, ms(3_600_001), &AudioLevels::SILENT).apply(&dev(2), &s, &mut frame);
        assert_eq!(frame, vec![Color::BLACK; 2], "gone with the trigger");
    }

    #[test]
    fn single_led_targets() {
        let red = Color::new(255, 0, 0);
        let grey = Color::new(40, 40, 40);
        // LEDs 1 and 3 of the strip, plus the whole of zone B on another trigger.
        let s = scene_with(vec![trig(json!({"id":"t1","name":"f","source":{"kind":"mouse_move"},"action":{"kind":"flash","color":"ff0000","duration_ms":1000},"targets":["openrgb:Strip@1","openrgb:Strip@3"]}))]);
        let ok = validate(s.triggers[0].clone(), &s).unwrap();
        assert_eq!(ok.targets, ["openrgb:Strip@1", "openrgb:Strip@3"]);
        let mut e = Engine::default();
        e.input(&s, Input::Move, ms(0));
        let mut frame = vec![grey; 4];
        e.overlay(&s, ms(0), &AudioLevels::SILENT).apply(&dev(4), &s, &mut frame);
        assert_eq!(frame, vec![grey, red, grey, red], "only those LEDs, over whatever the device showed");
        // A second device with the same name ("openrgb:Strip#2") is a whole device, not LED 2 of this one.
        let s = scene_with(vec![trig(json!({"id":"t1","name":"f","source":{"kind":"mouse_move"},"action":{"kind":"flash","color":"ff0000","duration_ms":1000},"targets":["openrgb:Strip#2"]}))]);
        let mut e = Engine::default();
        e.input(&s, Input::Move, ms(0));
        let mut frame = vec![grey; 4];
        e.overlay(&s, ms(0), &AudioLevels::SILENT).apply(&dev(4), &s, &mut frame);
        assert_eq!(frame, vec![grey; 4], "another device's id is not an LED of this one");
        // Another device's LED numbers don't touch this one.
        let s = scene_with(vec![trig(json!({"id":"t1","name":"f","source":{"kind":"mouse_move"},"action":{"kind":"flash","color":"ff0000","duration_ms":1000},"targets":["openrgb:Other@1"]}))]);
        let mut e = Engine::default();
        e.input(&s, Input::Move, ms(0));
        let mut frame = vec![grey; 4];
        e.overlay(&s, ms(0), &AudioLevels::SILENT).apply(&dev(4), &s, &mut frame);
        assert_eq!(frame, vec![grey; 4]);
    }

    #[test]
    fn local_effects_start_at_the_pressed_key() {
        let red = Color::new(255, 0, 0);
        let lit = |radius: f32, vk: u8| {
            let t = trig(json!({"id":"t1","name":"k","source":{"kind":"key","keys":"any"},"action":{"kind":"flash","color":"ff0000","duration_ms":1000,"radius":radius}}));
            let s = scene_with(vec![t]);
            assert!(input_wanted(&s).key_codes, "local key effects need the key codes");
            let mut e = Engine::default();
            e.input(&s, Input::Key(Some(vk), None), ms(0));
            let mut frame = vec![Color::BLACK; 9];
            e.overlay_at(ms(0)).apply(&keyboard(), &s, &mut frame);
            frame.iter().map(|c| *c == red).collect::<Vec<_>>()
        };
        // Radius 0: only S (the middle key) lights.
        assert_eq!(lit(0.0, b'S'), [false, false, false, false, true, false, false, false, false]);
        // Radius 1: S and the four keys next to it, not the corners.
        assert_eq!(lit(1.0, b'S'), [false, true, false, true, true, true, false, true, false]);
        // Q lights only round Q.
        assert_eq!(lit(0.0, b'Q'), [true, false, false, false, false, false, false, false, false]);
        // A ripple starts at the key: at first only the key is lit.
        let t = trig(json!({"id":"t1","name":"k","source":{"kind":"key","keys":"any"},"action":{"kind":"ripple","color":"ff0000","duration_ms":1000,"radius":2.0}}));
        let s = scene_with(vec![t]);
        let mut e = Engine::default();
        e.input(&s, Input::Key(Some(b'Q'), None), ms(0));
        let mut frame = vec![Color::BLACK; 9];
        e.overlay_at(ms(0)).apply(&keyboard(), &s, &mut frame);
        assert!(frame[0].r > 200 && frame[8] == Color::BLACK, "{frame:?}");
        // Without a radius the whole keyboard flashes as before, and key codes aren't needed.
        let t = trig(json!({"id":"t1","name":"k","source":{"kind":"key","keys":"any"},"action":{"kind":"flash","color":"ff0000","duration_ms":1000}}));
        let s = scene_with(vec![t]);
        assert!(!input_wanted(&s).key_codes);
        let mut e = Engine::default();
        e.input(&s, Input::Key(None, None), ms(0));
        let mut frame = vec![Color::BLACK; 9];
        e.overlay_at(ms(0)).apply(&keyboard(), &s, &mut frame);
        assert!(frame.iter().all(|c| *c == red));
        // Out of range radius is refused.
        assert!(validate(trig(json!({"name":"x","source":{"kind":"mouse_move"},"action":{"kind":"pulse","duration_ms":200,"radius":99}})), &Scene::default()).is_err());
    }

    #[test]
    fn validation() {
        let mut s = Scene::default();
        s.profiles.insert("Night".into(), Lighting::default());
        s.groups.push(Group { id: "g1".into(), name: "Fans".into(), members: vec!["openrgb:A".into()] });
        let ok = validate(
            trig(json!({"name":" Keys ","source":{"kind":"key","keys":[65,65,32]},"action":{"kind":"flash","color":"#FF0000","duration_ms":300},"targets":["openrgb:A"," openrgb:A","group:g1"]})),
            &s,
        )
        .unwrap();
        assert_eq!(ok.name, "Keys");
        assert!(ok.enabled, "enabled by default");
        assert_eq!(ok.source, Source::Key { keys: Keys::Codes(vec![32, 65]) });
        assert_eq!(ok.action, Action::Flash { color: "ff0000".into(), duration_ms: 300, radius: None, random: false, layer_delay_ms: None });
        assert_eq!(ok.targets, ["openrgb:A", "group:g1"]);
        let v = serde_json::to_value(&ok).unwrap();
        assert_eq!(v["source"], json!({"kind":"key","keys":[32,65]}));
        assert_eq!(serde_json::from_value::<Trigger>(v).unwrap(), ok);
        let any = trig(json!({"name":"a","source":{"kind":"key","keys":"any"},"action":{"kind":"pulse","duration_ms":200}}));
        assert_eq!(serde_json::to_value(&any).unwrap()["source"]["keys"], json!("any"));
        let click = trig(json!({"name":"c","source":{"kind":"mouse_click"},"action":{"kind":"preset","name":"Night"}}));
        assert!(validate(click, &s).is_ok());
        // Bad values.
        let bad = [
            json!({"name":"","source":{"kind":"mouse_move"},"action":{"kind":"pulse","duration_ms":200}}),
            json!({"name":"x","source":{"kind":"mouse_move"},"action":{"kind":"pulse","duration_ms":10}}),
            json!({"name":"x","source":{"kind":"mouse_move"},"action":{"kind":"pulse","duration_ms":20000}}),
            json!({"name":"x","source":{"kind":"mouse_move"},"action":{"kind":"flash","color":"red","duration_ms":200}}),
            json!({"name":"x","source":{"kind":"audio_level","band":"bass","above":1.5},"action":{"kind":"pulse","duration_ms":200}}),
            json!({"name":"x","source":{"kind":"key","keys":[]},"action":{"kind":"pulse","duration_ms":200}}),
            json!({"name":"x","source":{"kind":"mouse_move"},"action":{"kind":"preset","name":"Nope"}}),
            json!({"name":"x","source":{"kind":"mouse_move"},"action":{"kind":"pulse","duration_ms":200},"targets":["all"]}),
            json!({"name":"x","source":{"kind":"mouse_move"},"action":{"kind":"pulse","duration_ms":200},"targets":["group:g9"]}),
        ];
        for b in bad {
            let t: Trigger = serde_json::from_value(b.clone()).unwrap();
            assert!(validate(t, &s).is_err(), "{b}");
        }
        let unparsable = [
            json!({"name":"x","source":{"kind":"key","keys":"some"},"action":{"kind":"pulse","duration_ms":200}}),
            json!({"name":"x","source":{"kind":"key","keys":[0]},"action":{"kind":"pulse","duration_ms":200}}),
            json!({"name":"x","source":{"kind":"key","keys":[300]},"action":{"kind":"pulse","duration_ms":200}}),
            json!({"name":"x","source":{"kind":"mouse_click","button":"thumb"},"action":{"kind":"pulse","duration_ms":200}}),
            json!({"name":"x","source":{"kind":"audio_level","band":"sub","above":0.5},"action":{"kind":"pulse","duration_ms":200}}),
            json!({"name":"x","source":{"kind":"telepathy"},"action":{"kind":"pulse","duration_ms":200}}),
            json!({"name":"x","source":{"kind":"mouse_move"},"action":{"kind":"explode"}}),
        ];
        for b in unparsable {
            assert!(serde_json::from_value::<Trigger>(b.clone()).is_err(), "{b}");
        }
    }

    #[test]
    fn input_wanted_follows_enabled_triggers() {
        let key = trig(json!({"id":"t1","name":"k","source":{"kind":"key","keys":"any"},"action":{"kind":"pulse","duration_ms":200}}));
        let mut mv = trig(json!({"id":"t2","name":"m","source":{"kind":"mouse_move"},"action":{"kind":"pulse","duration_ms":200}}));
        mv.enabled = false;
        let beat = trig(json!({"id":"t3","name":"b","source":{"kind":"audio_beat"},"action":{"kind":"pulse","duration_ms":200}}));
        assert_eq!(input_wanted(&Scene::default()), InputWanted::default());
        let mut s = scene_with(vec![key, mv, beat]);
        assert_eq!(input_wanted(&s), InputWanted { keys: true, key_codes: false, mouse_click: false, mouse_move: false, apps: vec![] });
        s.triggers[1].enabled = true;
        assert_eq!(input_wanted(&s), InputWanted { keys: true, key_codes: false, mouse_click: false, mouse_move: true, apps: vec![] });
        s.off = true;
        assert_eq!(input_wanted(&s), InputWanted::default());
    }

    #[test]
    fn firing_rules() {
        let flash = trig(json!({"id":"t1","name":"f","source":{"kind":"key","keys":[65]},"action":{"kind":"flash","color":"ff0000","duration_ms":200}}));
        let click = trig(json!({"id":"t2","name":"c","source":{"kind":"mouse_click","button":"right"},"action":{"kind":"flash","color":"00ff00","duration_ms":200}}));
        let mv = trig(json!({"id":"t3","name":"m","source":{"kind":"mouse_move"},"action":{"kind":"fade_in","color":"0000ff","duration_ms":500}}));
        let s = scene_with(vec![flash, click, mv]);
        let mut e = Engine::default();
        assert_eq!(e.input(&s, Input::Key(Some(66), None), ms(0)).count, 0);
        assert_eq!(e.input(&s, Input::Key(None, None), ms(0)).count, 0, "listed keys need a code");
        assert_eq!(e.input(&s, Input::Key(Some(65), None), ms(0)).count, 1);
        assert_eq!(e.input(&s, Input::Key(Some(65), None), ms(30)).count, 0, "at most once per 50 ms");
        assert_eq!(e.input(&s, Input::Key(Some(65), None), ms(60)).count, 1);
        assert_eq!(e.input(&s, Input::Click(MouseButton::Left), ms(60)).count, 0);
        assert_eq!(e.input(&s, Input::Click(MouseButton::Right), ms(60)).count, 1);
        // Mouse moves are throttled; a fade_in glow builds while they repeat.
        assert_eq!(e.input(&s, Input::Move, ms(100)).count, 1);
        assert_eq!(e.input(&s, Input::Move, ms(150)).count, 0);
        e.input(&s, Input::Move, ms(200));
        e.input(&s, Input::Move, ms(300));
        let glow: Vec<&Instance> = e.active.iter().filter(|i| i.trigger == "t3").collect();
        assert_eq!(glow.len(), 1, "one glow per trigger");
        assert!(glow[0].level > FADE_STEP * 2.0, "built up: {}", glow[0].level);
        // Off: nothing fires. Instances end after their duration.
        let off = Scene { off: true, ..s.clone() };
        assert_eq!(e.input(&off, Input::Key(Some(65), None), ms(1000)).count, 0);
        assert!(e.is_active());
        assert!(e.overlay_at(ms(5000)).is_empty());
        assert!(!e.is_active());
        // At most MAX_ACTIVE instances.
        for k in 0..200 {
            e.input(&s, Input::Key(Some(65), None), ms(10_000 + k * 60));
        }
        assert_eq!(e.active.len(), MAX_ACTIVE);
    }

    #[test]
    fn audio_sources() {
        let beat = trig(json!({"id":"t1","name":"b","source":{"kind":"audio_beat"},"action":{"kind":"flash","color":"ffffff","duration_ms":100}}));
        let bass = trig(json!({"id":"t2","name":"l","source":{"kind":"audio_level","band":"bass","above":0.5},"action":{"kind":"pulse","duration_ms":100}}));
        let s = scene_with(vec![beat, bass]);
        assert!(uses_audio(&s) && !uses_audio(&Scene::default()));
        let mut e = Engine::default();
        let silent = AudioLevels::SILENT;
        let kick = AudioLevels { beat: true, beat_age: 0.01, ..silent };
        assert_eq!(e.audio(&s, &silent, ms(0)).count, 0);
        assert_eq!(e.audio(&s, &kick, ms(100)).count, 1);
        let later = AudioLevels { beat: true, beat_age: 0.05, ..silent };
        assert_eq!(e.audio(&s, &later, ms(140)).count, 0, "same beat");
        let loud = AudioLevels { bass: 0.8, ..silent };
        assert_eq!(e.audio(&s, &loud, ms(300)).count, 1);
        assert_eq!(e.audio(&s, &loud, ms(400)).count, 0, "fires when crossing, not while above");
        assert_eq!(e.audio(&s, &AudioLevels { bass: 0.47, ..silent }, ms(500)).count, 0);
        assert_eq!(e.audio(&s, &loud, ms(600)).count, 0, "hysteresis: did not really drop");
        assert_eq!(e.audio(&s, &silent, ms(700)).count, 0);
        assert_eq!(e.audio(&s, &loud, ms(800)).count, 1);
    }

    #[test]
    fn overlay_blends_newest_on_top() {
        let red = Color::new(255, 0, 0);
        let blue = Color::new(0, 0, 255);
        let grey = Color::new(40, 40, 40);
        let d = dev(4);
        let s = Scene::default();
        let mk = |kind, color, strength: f32, targets: Vec<&str>| Item { kind, color, strength, front: 0.0, targets: targets.into_iter().map(String::from).collect(), origin: None, radius: None, ..Item::default() };
        // Full red then half blue: blue blends over red.
        let o = Overlay { items: vec![mk(Kind::Flash, red, 1.0, vec![]), mk(Kind::Flash, blue, 0.5, vec![])], ..Overlay::default() };
        let mut f = vec![grey; 4];
        o.apply(&d, &s, &mut f);
        assert_eq!(f[0], Color::new(128, 0, 128));
        // Order matters: full red on top hides blue.
        let o = Overlay { items: vec![mk(Kind::Flash, blue, 0.5, vec![]), mk(Kind::Flash, red, 1.0, vec![])], ..Overlay::default() };
        let mut f = vec![grey; 4];
        o.apply(&d, &s, &mut f);
        assert_eq!(f, vec![red; 4]);
        // Pulse brightens the current colors; a zone target only covers that zone.
        let o = Overlay { items: vec![mk(Kind::Pulse, red, 1.0, vec!["openrgb:Strip/B"])], ..Overlay::default() };
        let mut f = vec![grey; 4];
        o.apply(&d, &s, &mut f);
        assert_eq!(f, vec![grey, grey, Color::new(255, 255, 255), Color::new(255, 255, 255)]);
        // Other devices' targets do not touch this one; hidden devices and lights off are left alone.
        let o = Overlay { items: vec![mk(Kind::Flash, red, 1.0, vec!["openrgb:Other"])], ..Overlay::default() };
        let mut f = vec![grey; 4];
        o.apply(&d, &s, &mut f);
        assert_eq!(f, vec![grey; 4]);
        let all = Overlay { items: vec![mk(Kind::Flash, red, 1.0, vec![])], ..Overlay::default() };
        let mut hidden = Scene::default();
        hidden.hidden.insert("openrgb:Strip".into());
        all.apply(&d, &hidden, &mut f);
        assert_eq!(f, vec![grey; 4]);
        all.apply(&d, &Scene { off: true, ..Scene::default() }, &mut f);
        assert_eq!(f, vec![grey; 4]);
        // Fan targets: only that fan's LEDs.
        let mut fans = Scene::default();
        fans.layouts.insert("openrgb:Strip/A".into(), FanLayout { fans: 2, leds_per_fan: 1 });
        let o = Overlay { items: vec![mk(Kind::Flash, red, 1.0, vec!["openrgb:Strip/A/1"])], ..Overlay::default() };
        let mut f = vec![grey; 4];
        o.apply(&d, &fans, &mut f);
        assert_eq!(f, vec![grey, red, grey, grey]);
        // A ripple starts in the centre and moves outwards.
        let ripple = |front: f32| {
            let o = Overlay { items: vec![Item { kind: Kind::Ripple, color: red, strength: 1.0, front, targets: vec![], origin: None, radius: None, ..Item::default() }], ..Overlay::default() };
            let mut f = vec![Color::BLACK; 21];
            o.apply(&dev(21), &s, &mut f);
            f
        };
        let start = ripple(0.0);
        assert!(start[10].r > 200 && start[0].r == 0, "{start:?}");
        let end = ripple(0.8);
        assert!(end[0].r > end[10].r && end[20].r > end[10].r, "{end:?}");
    }

    #[test]
    fn instances_fade_out() {
        let flash = trig(json!({"id":"t1","name":"f","source":{"kind":"mouse_click"},"action":{"kind":"flash","color":"ff0000","duration_ms":1000}}));
        let s = scene_with(vec![flash]);
        let mut e = Engine::default();
        e.input(&s, Input::Click(MouseButton::Middle), ms(0));
        let d = dev(2);
        let at = |e: &mut Engine, t| {
            let mut f = vec![Color::BLACK; 2];
            e.overlay_at(ms(t)).apply(&d, &s, &mut f);
            f[0].r
        };
        assert_eq!(at(&mut e, 0), 255);
        let mid = at(&mut e, 500);
        assert!(mid > 100 && mid < 150, "{mid}");
        assert_eq!(at(&mut e, 1000), 0);
        assert!(!e.is_active());
        e.input(&s, Input::Click(MouseButton::Left), ms(2000));
        e.forget("t1");
        assert!(!e.is_active());
    }
}
