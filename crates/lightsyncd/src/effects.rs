//! Lighting effects: pure functions from (state, time, LED position) to a color.

use lightsync_core::Color;
use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, Default)]
#[serde(rename_all = "snake_case")]
pub enum Effect {
    /// One solid color.
    #[default]
    Static,
    /// The color fades in and out.
    Breathing,
    /// Every LED cycles through the hue wheel together.
    Spectrum,
    /// A rainbow that moves along each device's LEDs.
    RainbowWave,
    /// A band of the chosen color that travels along each device's LEDs.
    ColorWave,
    /// A bright head of the chosen color with a fading tail, running along each device.
    Comet,
    /// LEDs of the chosen color sparkle at random over a dim glow.
    Twinkle,
    /// Flickering reds, oranges and yellows.
    Fire,
    /// Slowly drifting greens, teals and purples.
    Aurora,
    /// A still blend from the chosen color into its neighbouring hues along each device.
    Gradient,
    /// Two quick beats of the chosen color, then a rest.
    Heartbeat,
    /// The chosen color flashes on and off.
    Strobe,
    /// Groups of lit LEDs that march along each device (theater chase).
    Chase,
    /// Halves of each device flash red and blue in turn.
    Police,
    /// Rolling blues and teals.
    Ocean,
    /// A warm, gently flickering glow of the chosen color.
    Candle,
    /// Drops of the chosen color that fall along each device and fade.
    Rain,
    /// Swirling, ever-changing rainbow plasma.
    Plasma,
    /// Meteors of the chosen color streak along each device with glowing tails.
    MeteorShower,
    /// Slow, warm blobs of the chosen color rise and merge like a lava lamp.
    LavaLamp,
    /// Streams of the chosen color (green by default) rain down with bright heads and flickering trails.
    DigitalRain,
    /// Colorful bursts explode at random spots and sparkle out.
    Fireworks,
    /// A bright eye of the chosen color sweeps back and forth with a soft glow.
    Scanner,
    /// A dark, cloudy night sky lit by sudden lightning strikes.
    LightningStorm,
    /// Two neon colors (the chosen one and its opposite) chase each other in glowing waves.
    NeonPulse,
    /// Sound reactive: every beat flashes the lights, as bright as the beat is strong, fading at a
    /// rate set by the speed. Each light (device, zone or LED: [`Split`]) gets its own color.
    /// (Older sound effects saved before it are read as the closest of the three.)
    #[serde(alias = "beat_flash")]
    SoundBeat,
    /// Sound reactive: the lights follow the bass and its hits (kicks, bass notes). Each light
    /// (device, zone or LED) gets its own color.
    #[serde(alias = "sound_pulse", alias = "bass_glow", alias = "bass_wave", alias = "bass_treble")]
    SoundBass,
    /// Sound reactive: each light (device, zone or LED) shows one part of the spectrum, bass first,
    /// as bright as that part is loud.
    #[serde(alias = "spectrum_bars", alias = "spectrum_analyzer", alias = "vu_meter", alias = "treble_sparkle", alias = "music_flow")]
    SoundSpectrum,
}

/// Which lights a sound effect treats as one: what gets its own color (and, for the spectrum, its
/// own part of it).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, Default)]
#[serde(rename_all = "snake_case")]
pub enum Split {
    /// A whole device is one light, as if it had a single LED. ("zone", from an earlier version,
    /// reads as this.)
    #[default]
    #[serde(alias = "zone")]
    Device,
    /// Every LED of every device is a light of its own.
    Led,
}

impl Split {
    pub fn parse(s: &str) -> Option<Split> {
        serde_json::from_value(serde_json::Value::String(s.into())).ok()
    }
}

/// Where an LED is: its place in what is being lit (a device, zone or fan), and which device it
/// belongs to (for sound effects with a light per device).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Spot {
    /// LED `i` of `n`.
    pub i: usize,
    pub n: usize,
    /// Device `device` of `devices` (the devices LightSync lights).
    pub device: usize,
    pub devices: usize,
}

impl Spot {
    /// LED `i` of `n` on a device of its own.
    pub fn led(i: usize, n: usize) -> Spot {
        Spot { i, n, device: 0, devices: 1 }
    }

    /// The light this LED belongs to with this split: (which, of how many).
    fn unit(&self, split: Split) -> (usize, usize) {
        match split {
            Split::Led => (self.i, self.n.max(1)),
            Split::Device => (self.device, self.devices.max(1)),
        }
    }
}

impl Effect {
    pub fn is_animated(self) -> bool {
        !matches!(self, Effect::Static | Effect::Gradient)
    }

    /// Whether the effect reacts to sound (the daemon only captures audio while one is shown).
    pub fn is_audio(self) -> bool {
        matches!(self, Effect::SoundBeat | Effect::SoundBass | Effect::SoundSpectrum)
    }

    pub fn parse(s: &str) -> Option<Effect> {
        serde_json::from_value(serde_json::Value::String(s.into())).ok()
    }
}

/// The colors a sound reactive effect uses.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, Default)]
#[serde(rename_all = "snake_case")]
pub enum Palette {
    /// The effect's own colors: the chosen color, or the effect's built-in ones.
    #[default]
    Effect,
    /// Every color of the rainbow along each device, drifting with the music.
    Rainbow,
    /// A new random color on every beat.
    Random,
    /// By intensity: blue when quiet, through cyan, green and yellow, to red at the loudest.
    ColdHot,
    /// By pitch: deep red for bass, through orange, green and blue, to violet for treble.
    LowHigh,
    /// By intensity: deep red when quiet, to orange, yellow and white-hot at the loudest.
    Fire,
    /// Neon pink and cyan along each device, swapping on every beat.
    Neon,
}

impl Palette {
    pub fn parse(s: &str) -> Option<Palette> {
        serde_json::from_value(serde_json::Value::String(s.into())).ok()
    }
}

/// An effect by name, or Static for a name this version doesn't know.
fn effect_or_static<'de, D: serde::Deserializer<'de>>(d: D) -> Result<Effect, D::Error> {
    let v = serde_json::Value::deserialize(d)?;
    Ok(serde_json::from_value(v).unwrap_or_default())
}

/// What the user picked; persisted between runs.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(default)]
pub struct LightState {
    /// An effect this version doesn't know (a removed one, or one from a newer version) reads as
    /// Static, so the rest of a saved scene, preset or trigger still loads.
    #[serde(deserialize_with = "effect_or_static")]
    pub effect: Effect,
    /// "RRGGBB"
    pub color: String,
    /// 0..=100
    pub brightness: u8,
    /// 1..=100
    pub speed: u8,
    /// Colors of the sound reactive effects (the others ignore it).
    pub palette: Palette,
    /// Sound effects with lights of their own ([`Effect::is_audio`]): what one light is.
    pub split: Split,
    /// How many colors the random palette uses (1..=8); 0: a random number on every beat.
    pub colors: u8,
}

impl Default for LightState {
    fn default() -> Self {
        Self { effect: Effect::Static, color: "ffffff".into(), brightness: 100, speed: 50, palette: Palette::Effect, split: Split::Device, colors: 3 }
    }
}

impl LightState {
    pub fn base_color(&self) -> Color {
        self.color.parse().unwrap_or(Color::new(255, 255, 255))
    }
}

/// What the sound reactive effects see of the audio playing now (see audio.rs). Levels are 0..1
/// after auto-gain, on a dB scale (0 is 24 dB below the recent maximum); `beat_age` is the time in
/// seconds since the last detected beat.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct AudioLevels {
    pub level: f32,
    pub bass: f32,
    pub mid: f32,
    pub treble: f32,
    /// Falling peak of `level`, 0..1 (for level meters).
    pub level_peak: f32,
    /// A beat was detected very recently (within `BEAT_HOLD` seconds).
    pub beat: bool,
    pub beat_age: f32,
    /// How strong the last beat was, about 0.55 (a soft one) .. 1 (a hard kick).
    pub beat_strength: f32,
    /// Beats so far (wraps); a new value means a new beat.
    pub beat_count: u32,
    /// How far level, bass, mid and treble are above their last half second, 0..1: 0.5 while steady,
    /// near 1 on a hit (kick, clap, hi-hat), near 0 in a dip. Loud music that hardly changes level
    /// still punches here.
    pub level_punch: f32,
    pub bass_punch: f32,
    pub mid_punch: f32,
    pub treble_punch: f32,
    /// Spectrum analyzer: `BANDS` log-spaced bands (about 30 Hz to 16 kHz), each 0..1.
    pub bands: [f32; AudioLevels::BANDS],
    /// Slowly falling peak of each band, 0..1 (for analyzer displays).
    pub peaks: [f32; AudioLevels::BANDS],
    /// How bright the sound is right now: the spectral centroid, 0 (all bass) .. 1 (all treble).
    pub centroid: f32,
    /// The song's character: the centroid over the last few seconds (dark, bass-heavy music low;
    /// bright, airy music high).
    pub mood: f32,
    /// How energetic the music is over the last few seconds, 0 (calm / silent) .. 1.
    pub energy: f32,
    /// The beat's tempo in beats per second once steady (0: not known yet).
    pub tempo: f32,
    /// Grows by one per beat at the song's tempo (a steady pace while the tempo is unknown, slower
    /// when calm): effects that move with the music use it as their clock.
    pub phase: f32,
}

impl AudioLevels {
    /// Number of spectrum analyzer bands.
    pub const BANDS: usize = 24;
    /// No sound (also what effects see while nothing is captured).
    pub const SILENT: AudioLevels = AudioLevels {
        level: 0.0,
        bass: 0.0,
        mid: 0.0,
        treble: 0.0,
        level_peak: 0.0,
        beat: false,
        beat_age: 1.0e6,
        beat_strength: 1.0,
        beat_count: 0,
        level_punch: 0.0,
        bass_punch: 0.0,
        mid_punch: 0.0,
        treble_punch: 0.0,
        bands: [0.0; AudioLevels::BANDS],
        peaks: [0.0; AudioLevels::BANDS],
        centroid: 0.0,
        mood: 0.5,
        energy: 0.0,
        tempo: 0.0,
        phase: 0.0,
    };
    /// How long `beat` stays true after a beat.
    pub const BEAT_HOLD: f32 = 0.1;
}

impl Default for AudioLevels {
    fn default() -> Self {
        Self::SILENT
    }
}

fn lerp(a: f32, b: f32, t: f32) -> f32 {
    a + (b - a) * t
}

/// HSV (h in 0..1, s and v in 0..1) to RGB.
pub fn hsv(h: f32, s: f32, v: f32) -> Color {
    let h = h.rem_euclid(1.0) * 6.0;
    let i = h.floor() as i32;
    let f = h - i as f32;
    let (p, q, t) = (v * (1.0 - s), v * (1.0 - s * f), v * (1.0 - s * (1.0 - f)));
    let (r, g, b) = match i {
        0 => (v, t, p),
        1 => (q, v, p),
        2 => (p, v, t),
        3 => (p, q, v),
        4 => (t, p, v),
        _ => (v, p, q),
    };
    let u = |x: f32| (x.clamp(0.0, 1.0) * 255.0).round() as u8;
    Color::new(u(r), u(g), u(b))
}

/// Deterministic pseudo-random number in 0..1 for (a, b).
fn hash(a: u32, b: u32) -> f32 {
    let mut x = a.wrapping_mul(0x9E37_79B1) ^ b.wrapping_mul(0x85EB_CA77) ^ 0x27D4_EB2F;
    x ^= x >> 15;
    x = x.wrapping_mul(0x2C1B_3C6D);
    x ^= x >> 12;
    x = x.wrapping_mul(0x297A_2D39);
    x ^= x >> 15;
    (x >> 8) as f32 / (1u32 << 24) as f32
}

/// Smooth random value for LED `i` that drifts over `x` (value noise between hashed points).
fn drift(i: usize, x: f32) -> f32 {
    let k = x.floor();
    let f = x - k;
    let f = f * f * (3.0 - 2.0 * f);
    lerp(hash(i as u32, k as u32), hash(i as u32, k as u32 + 1), f)
}

/// RGB to HSV (all in 0..1).
fn to_hsv(c: Color) -> (f32, f32, f32) {
    let (r, g, b) = (c.r as f32 / 255.0, c.g as f32 / 255.0, c.b as f32 / 255.0);
    let max = r.max(g).max(b);
    let min = r.min(g).min(b);
    let d = max - min;
    let h = if d == 0.0 {
        0.0
    } else if max == r {
        ((g - b) / d).rem_euclid(6.0) / 6.0
    } else if max == g {
        ((b - r) / d + 2.0) / 6.0
    } else {
        ((r - g) / d + 4.0) / 6.0
    };
    (h, if max == 0.0 { 0.0 } else { d / max }, max)
}

/// Light added together (clipped at full).
fn add(a: Color, b: Color) -> Color {
    Color::new(a.r.saturating_add(b.r), a.g.saturating_add(b.g), a.b.saturating_add(b.b))
}

fn mul(c: Color, f: f32) -> Color {
    let m = |v: u8| ((v as f32) * f.clamp(0.0, 1.0)).round() as u8;
    Color::new(m(c.r), m(c.g), m(c.b))
}


/// Gain of the sound reactive effects for a speed of 0..1 (1.25 at the default speed).
fn sensitivity(speed: f32) -> f32 {
    lerp(0.5, 2.0, speed)
}

/// How much of a sound effect's drive is the punch (the hits) rather than the plain level.
const PUNCH_MIX: f32 = 0.65;

/// What a sound effect follows for one meter, 0..1: its level mixed with its punch (see
/// [`AudioLevels::bass_punch`]), so kicks, claps and hi-hats stand out even in loud, compressed
/// music, times the sensitivity.
fn drive(level: f32, punch: f32, sens: f32) -> f32 {
    ((PUNCH_MIX * punch + (1.0 - PUNCH_MIX) * level) * sens).clamp(0.0, 1.0)
}

/// How many colors the random palette uses on this beat: `colors` (1..=8), or 2..=6 picked anew on
/// every beat when it is 0.
fn color_count(colors: u8, beat: u32) -> u32 {
    if colors == 0 {
        2 + (hash(beat, 0xC010) * 5.0) as u32
    } else {
        colors.min(8) as u32
    }
}

/// One light of a split sound effect ([`Effect::is_audio`]): light `u` of `units` (a device, zone
/// or LED, see [`Split`]), brightness from the beat, the bass or its part of the spectrum, color from
/// the palette.
fn split_light(state: &LightState, spot: &Spot, audio: &AudioLevels, speed: f32) -> Color {
    let (u, units) = spot.unit(state.split);
    // The light's place along the spectrum (bass first); a single light shows all of it.
    let x = if units > 1 { u as f32 / (units - 1) as f32 } else { audio.centroid };
    let sens = sensitivity(speed);
    // Brightness (with more contrast than the level itself) and how intense the light is (heat).
    let (v, heat) = match state.effect {
        Effect::SoundBeat => {
            let k = (-audio.beat_age.max(0.0) * lerp(2.0, 12.0, speed)).exp() * audio.beat_strength;
            let glow = 0.2 * audio.bass_punch * audio.bass_punch;
            (k.max(glow), k)
        }
        Effect::SoundBass => {
            let b = drive(audio.bass, audio.bass_punch, sens);
            (b * b, b)
        }
        _ => {
            let b = if units > 1 {
                band_at(&audio.bands, x)
            } else {
                audio.bands.iter().copied().fold(0.0, f32::max)
            };
            let b = (b * sens).clamp(0.0, 1.0);
            (b * b, b)
        }
    };
    let beat = audio.beat_count;
    let (h, s) = match state.palette {
        Palette::Effect => {
            let (h, s, _) = to_hsv(state.base_color());
            (h, s)
        }
        Palette::Random => {
            // `count` colors spread round the wheel, turned at random on every beat; each light
            // takes one of them, also anew on every beat.
            let count = color_count(state.colors, beat);
            let pick = ((hash(u as u32, beat.wrapping_mul(7919)) * count as f32) as u32).min(count - 1);
            (hash(beat, 0x5EED) + pick as f32 / count as f32, 1.0)
        }
        Palette::Rainbow => (x + 0.05 * audio.phase, 1.0),
        Palette::ColdHot => (lerp(0.66, 0.0, heat.clamp(0.0, 1.0)), 1.0),
        // Spectrum: each light by its frequency; beat and bass: by the pitch of the sound.
        Palette::LowHigh => (band_hue(if state.effect == Effect::SoundSpectrum { x } else { audio.centroid }), 1.0),
        Palette::Fire => {
            let heat = heat.clamp(0.0, 1.0);
            (lerp(0.0, 0.14, heat), lerp(1.0, 0.45, heat * heat))
        }
        Palette::Neon => (if (u as u32 + beat).is_multiple_of(2) { 0.92 } else { 0.5 }, 1.0),
    };
    hsv(h, s, 0.03 + 0.97 * v)
}

/// Hue for a position 0..1 along the analyzer bands: red bass through to violet treble.
fn band_hue(x: f32) -> f32 {
    lerp(0.0, 0.78, x.clamp(0.0, 1.0))
}

/// The analyzer value at position `x` (0..1, lowest to highest band), interpolated between bands.
fn band_at(v: &[f32; AudioLevels::BANDS], x: f32) -> f32 {
    let f = x.clamp(0.0, 1.0) * (AudioLevels::BANDS - 1) as f32;
    let k = (f.floor() as usize).min(AudioLevels::BANDS - 2);
    lerp(v[k], v[k + 1], f - k as f32)
}

/// Color of LED `i` of a device with `n` LEDs at time `t` seconds, brightness included, with no
/// sound (sound reactive effects stay dark; see [`render_with`]).
#[allow(dead_code)] // kept for callers without audio (tests, future tools)
pub fn render(state: &LightState, t: f32, i: usize, n: usize) -> Color {
    render_with(state, t, i, n, &AudioLevels::SILENT)
}

/// Like [`render`], with the audio levels the sound reactive effects follow.
pub fn render_with(state: &LightState, t: f32, i: usize, n: usize, audio: &AudioLevels) -> Color {
    render_spot(state, t, &Spot::led(i, n), audio)
}

/// Like [`render_with`], knowing the LED's zone and device too (sound effects split by zone or device).
pub fn render_spot(state: &LightState, t: f32, spot: &Spot, audio: &AudioLevels) -> Color {
    let (i, n) = (spot.i, spot.n);
    let speed = state.speed.clamp(1, 100) as f32 / 100.0;
    let pos = if n > 1 { i as f32 / n as f32 } else { 0.0 };
    let base = state.base_color();
    let c = match state.effect {
        Effect::Static => base,
        Effect::Breathing => {
            let period = lerp(8.0, 1.0, speed);
            let k = 0.5 - 0.5 * (std::f32::consts::TAU * t / period).cos();
            mul(base, 0.04 + 0.96 * k)
        }
        Effect::Spectrum => hsv(t * lerp(0.02, 0.5, speed), 1.0, 1.0),
        Effect::RainbowWave => hsv(pos - t * lerp(0.05, 1.0, speed), 1.0, 1.0),
        Effect::ColorWave => {
            let k = 0.5 + 0.5 * (std::f32::consts::TAU * (pos - t * lerp(0.05, 1.0, speed))).sin();
            mul(base, 0.08 + 0.92 * k * k)
        }
        Effect::Comet => {
            let head = (t * lerp(0.1, 1.5, speed)).rem_euclid(1.0);
            let behind = (head - pos).rem_euclid(1.0);
            const TAIL: f32 = 0.3;
            let k = if behind < TAIL { 1.0 - behind / TAIL } else { 0.0 };
            mul(base, k * k)
        }
        Effect::Twinkle => {
            let period = lerp(4.0, 0.6, speed);
            let phase = (t / period + hash(i as u32, 7)).rem_euclid(1.0);
            const FLASH: f32 = 0.18;
            let k = if phase < FLASH { (std::f32::consts::PI * phase / FLASH).sin() } else { 0.0 };
            mul(base, 0.06 + 0.94 * k)
        }
        Effect::Fire => {
            let rate = lerp(2.0, 14.0, speed);
            let heat = (0.55 * drift(i, t * rate) + 0.45 * drift(i + 7919, t * rate * 0.37)).clamp(0.0, 1.0);
            hsv(lerp(0.0, 0.12, heat), lerp(1.0, 0.85, heat), lerp(0.25, 1.0, heat))
        }
        Effect::Aurora => {
            let rate = lerp(0.02, 0.3, speed);
            let w1 = (std::f32::consts::TAU * (pos * 0.8 - t * rate)).sin();
            let w2 = (std::f32::consts::TAU * (pos * 2.3 + t * rate * 1.7)).sin();
            let hue = 0.42 + 0.17 * w1 + 0.06 * w2;
            let v = 0.55 + 0.45 * (0.5 + 0.5 * (std::f32::consts::TAU * (pos * 1.3 - t * rate * 0.8)).sin());
            hsv(hue, 0.9, v)
        }
        Effect::Gradient => {
            let (h, sat, v) = to_hsv(base);
            // Grey has no hue to spread: fade from the color to a quarter of it instead.
            if sat < 0.05 {
                mul(base, lerp(1.0, 0.25, pos))
            } else {
                hsv(h + lerp(-0.08, 0.08, pos), sat, v)
            }
        }
        Effect::Heartbeat => {
            let period = lerp(2.0, 0.6, speed);
            let p = (t / period).rem_euclid(1.0);
            let beat = |c: f32, w: f32| (1.0 - ((p - c) / w).abs()).max(0.0);
            let k = beat(0.08, 0.08).max(0.7 * beat(0.28, 0.08));
            mul(base, 0.05 + 0.95 * k)
        }
        Effect::Strobe => {
            let period = lerp(1.2, 0.08, speed);
            if (t / period).rem_euclid(1.0) < 0.5 { base } else { Color::BLACK }
        }
        Effect::Chase => {
            let shift = (t * lerp(2.0, 30.0, speed)).floor() as usize;
            match (i + shift) % 3 {
                0 => base,
                _ => mul(base, 0.04),
            }
        }
        Effect::Police => {
            let period = lerp(1.6, 0.25, speed);
            let p = (t / period).rem_euclid(1.0);
            let first_half = pos < 0.5;
            // Each side double-flashes during its half of the period.
            let on = |q: f32| (q < 0.18) || (0.25..0.43).contains(&q);
            match (p < 0.5, first_half) {
                (true, true) if on(p * 2.0) => Color::new(255, 0, 0),
                (false, false) if on(p * 2.0 - 1.0) => Color::new(0, 40, 255),
                _ => Color::BLACK,
            }
        }
        Effect::Ocean => {
            let rate = lerp(0.03, 0.4, speed);
            let w = (std::f32::consts::TAU * (pos * 1.5 - t * rate)).sin();
            let foam = drift(i, t * rate * 6.0);
            hsv(0.53 + 0.06 * w, lerp(0.95, 0.6, foam * foam), 0.45 + 0.35 * (0.5 + 0.5 * w) + 0.2 * foam)
        }
        Effect::Candle => {
            let rate = lerp(1.5, 10.0, speed);
            let f = 0.6 * drift(i / 4, t * rate) + 0.4 * drift(i / 4 + 3571, t * rate * 2.3);
            mul(base, 0.45 + 0.55 * f)
        }
        Effect::Rain => {
            // Each LED gets a drop now and then; a drop is bright and fades over a short tail.
            let period = lerp(3.0, 0.5, speed);
            let lane = i as u32;
            let phase = (t / period + hash(lane, 11)).rem_euclid(1.0);
            let k = if phase < 0.25 { 1.0 - phase / 0.25 } else { 0.0 };
            let fall = (t / period).floor() as u32;
            let lit = hash(lane, fall.wrapping_mul(31).wrapping_add(5)) < 0.35;
            mul(base, 0.03 + if lit { 0.97 * k * k } else { 0.0 })
        }
        Effect::Plasma => {
            let s = lerp(0.1, 1.2, speed);
            let a = (std::f32::consts::TAU * (pos * 1.3 + t * s * 0.30)).sin();
            let b = (std::f32::consts::TAU * (pos * 2.7 - t * s * 0.47)).sin();
            let c = (std::f32::consts::TAU * (pos * 0.6 + t * s * 0.21 + (t * s * 0.5).sin())).sin();
            let v = (a + b + c) / 3.0;
            hsv(v * 0.5 + t * s * 0.05, 1.0, 0.55 + 0.45 * v.abs())
        }
        Effect::MeteorShower => {
            let mut k = 0.0f32;
            let mut head = 0.0f32;
            for m in 0..3u32 {
                let period = lerp(3.0, 0.8, speed) * (0.8 + 0.4 * hash(m, 1));
                let phase = t / period + hash(m, 2);
                let cycle = phase.floor() as u32;
                if hash(m, cycle.wrapping_add(17)) > 0.75 {
                    continue; // this meteor rests this time round
                }
                let front = phase.fract() * 1.4 - 0.2;
                let behind = front - pos;
                const TAIL: f32 = 0.25;
                if (0.0..TAIL).contains(&behind) {
                    k = k.max((1.0 - behind / TAIL).powi(2));
                }
                if behind.abs() < 0.6 / n.max(1) as f32 {
                    head = 1.0;
                }
            }
            add(mul(base, k), mul(Color::new(255, 255, 255), 0.6 * head))
        }
        Effect::LavaLamp => {
            let r = lerp(0.15, 1.2, speed);
            let blob = (0.5 + 0.5 * (std::f32::consts::TAU * (pos * 1.4 + t * r * 0.13)).sin())
                * (0.5 + 0.5 * (std::f32::consts::TAU * (pos * 0.9 - t * r * 0.07 + 0.3)).sin());
            let k = ((blob - 0.2) / 0.4).clamp(0.0, 1.0);
            let k = k * k * (3.0 - 2.0 * k);
            let (h, s, _) = to_hsv(base);
            let s = if s < 0.05 { 0.0 } else { s.max(0.8) };
            hsv(h - 0.03 + 0.06 * k, s, lerp(0.12, 1.0, k))
        }
        Effect::DigitalRain => {
            let mut k = 0.0f32;
            let mut head = false;
            for d in 0..4u32 {
                let period = lerp(4.0, 1.0, speed) * (0.7 + 0.6 * hash(d, 3));
                let front = (t / period + hash(d, 4)).fract() * 1.35;
                let behind = front - pos;
                const TRAIL: f32 = 0.35;
                if (0.0..TRAIL).contains(&behind) {
                    k = k.max((1.0 - behind / TRAIL).powf(1.5));
                    head |= behind < 1.0 / n.max(1) as f32;
                }
            }
            // Glyphs flicker in the trails.
            let k = k * (0.75 + 0.25 * hash(i as u32, (t * 12.0) as u32));
            let (h, s, _) = to_hsv(base);
            if head { hsv(h, s * 0.3, 1.0) } else { hsv(h, s, 0.02 + 0.98 * k) }
        }
        Effect::Fireworks => {
            let mut c = Color::BLACK;
            for b in 0..3u32 {
                let period = lerp(3.0, 1.2, speed) * (0.8 + 0.4 * hash(b, 5));
                let phase = t / period + hash(b, 6);
                let cycle = phase.floor() as u32;
                let f = phase.fract();
                let centre = 0.15 + 0.7 * hash(b.wrapping_mul(31).wrapping_add(cycle), 7);
                let hue = hash(b, cycle.wrapping_mul(3).wrapping_add(1));
                let dist = (pos - centre).abs();
                let ring = (1.0 - (dist - f * 0.35).abs() / 0.07).max(0.0);
                let flash = if f < 0.08 && dist < 0.05 { 1.0 } else { 0.0 };
                let sparkle = if hash(i as u32, (t * 20.0) as u32 ^ b) > 0.3 { 1.0 } else { 0.4 };
                let v = (ring * (1.0 - f).powf(1.5) * sparkle).max(flash);
                c = add(c, hsv(hue, if flash > 0.0 { 0.2 } else { 0.9 }, v));
            }
            c
        }
        Effect::Scanner => {
            let p = (t * lerp(0.2, 1.5, speed)).fract();
            let eye = 1.0 - (2.0 * p - 1.0).abs();
            let x = if n > 1 { i as f32 / (n - 1) as f32 } else { 0.5 };
            let k = (1.0 - (x - eye).abs() / 0.15).max(0.0);
            mul(base, 0.03 + 0.97 * k * k)
        }
        Effect::LightningStorm => {
            let sky = hsv(0.63, 0.85, 0.05 + 0.06 * drift(i / 3, t * 0.4));
            let rate = lerp(0.4, 2.5, speed);
            let slot = (t * rate).floor() as u32;
            let f = (t * rate).fract();
            // Not every moment has a strike; one is a bright flash, a flicker, then an afterglow.
            let flash = if hash(slot, 9) > 0.4 {
                0.0
            } else if f < 0.05 || (0.11..0.16).contains(&f) {
                1.0
            } else if f < 0.4 {
                (0.4 - f) / 0.4 * 0.35
            } else {
                0.0
            };
            let centre = hash(slot, 10);
            let width = 0.25 + 0.35 * hash(slot, 11);
            let k = (1.0 - (pos - centre).abs() / width).max(0.0);
            add(sky, hsv(0.6, 0.15, flash * k))
        }
        Effect::NeonPulse => {
            let rate = lerp(0.1, 1.0, speed);
            let (h, s, _) = to_hsv(base);
            // A grey pick gets the classic pink / cyan pair.
            let (h, s) = if s < 0.1 { (0.92, 1.0) } else { (h, s.max(0.85)) };
            let w = (std::f32::consts::TAU * (pos * 2.0 - t * rate)).sin();
            let glow = 0.75 + 0.25 * (std::f32::consts::TAU * t * rate * 0.5).sin();
            hsv(if w >= 0.0 { h } else { h + 0.5 }, s, w.abs().powf(0.6) * glow)
        }
        Effect::SoundBeat | Effect::SoundBass | Effect::SoundSpectrum => split_light(state, spot, audio, speed),
    };
    mul(c, state.brightness.min(100) as f32 / 100.0)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn st(effect: Effect) -> LightState {
        LightState { effect, color: "ff0000".into(), palette: Palette::Effect, ..LightState::default() }
    }

    #[test]
    fn hsv_primaries() {
        assert_eq!(hsv(0.0, 1.0, 1.0), Color::new(255, 0, 0));
        assert_eq!(hsv(1.0 / 3.0, 1.0, 1.0), Color::new(0, 255, 0));
        assert_eq!(hsv(2.0 / 3.0, 1.0, 1.0), Color::new(0, 0, 255));
        assert_eq!(hsv(1.0, 1.0, 1.0), Color::new(255, 0, 0));
    }

    #[test]
    fn static_and_brightness() {
        let mut s = st(Effect::Static);
        assert_eq!(render(&s, 3.0, 5, 10), Color::new(255, 0, 0));
        s.brightness = 50;
        assert_eq!(render(&s, 0.0, 0, 1), Color::new(128, 0, 0));
        s.brightness = 0;
        assert_eq!(render(&s, 0.0, 0, 1), Color::BLACK);
    }

    #[test]
    fn breathing_goes_dim_and_bright() {
        let s = st(Effect::Breathing);
        let period = lerp(8.0, 1.0, 0.5);
        assert!(render(&s, 0.0, 0, 1).r < 20);
        assert_eq!(render(&s, period / 2.0, 0, 1).r, 255);
    }

    #[test]
    fn waves_vary_along_the_device() {
        for e in [Effect::RainbowWave, Effect::ColorWave] {
            let s = st(e);
            let a = render(&s, 0.3, 0, 20);
            assert!((1..20).any(|i| render(&s, 0.3, i, 20) != a), "{e:?} is flat");
        }
    }

    #[test]
    fn spectrum_moves_over_time() {
        let s = st(Effect::Spectrum);
        assert_ne!(render(&s, 0.0, 0, 1), render(&s, 1.0, 0, 1));
        assert_eq!(render(&s, 1.0, 0, 5), render(&s, 1.0, 4, 5));
    }

    #[test]
    fn new_effects_animate_and_stay_in_range() {
        for e in [Effect::Comet, Effect::Twinkle, Effect::Fire, Effect::Aurora] {
            let s = st(e);
            assert!(e.is_animated());
            let frames: Vec<Vec<Color>> = (0..20).map(|f| (0..30).map(|i| render(&s, f as f32 * 0.1, i, 30)).collect()).collect();
            assert!(frames.windows(2).any(|w| w[0] != w[1]), "{e:?} does not move");
            assert!(frames[5].iter().any(|c| *c != frames[5][0]), "{e:?} is flat");
        }
        // Comet: the head is bright, far from it is dark.
        let s = st(Effect::Comet);
        let lit = (0..100).filter(|&i| render(&s, 0.25, i, 100).r > 10).count();
        assert!(lit > 5 && lit < 50, "comet lit {lit} of 100");
        assert!(hash(1, 2) >= 0.0 && hash(1, 2) < 1.0 && hash(1, 2) != hash(2, 1));
    }

    #[test]
    fn more_effects() {
        for e in [Effect::Heartbeat, Effect::Strobe, Effect::Chase, Effect::Police, Effect::Ocean, Effect::Candle, Effect::Rain] {
            let s = st(e);
            assert!(e.is_animated(), "{e:?}");
            let frames: Vec<Vec<Color>> = (0..40).map(|f| (0..30).map(|i| render(&s, f as f32 * 0.05, i, 30)).collect()).collect();
            assert!(frames.windows(2).any(|w| w[0] != w[1]), "{e:?} does not move");
        }
        // Gradient: still, varies along the device, and starts at the chosen color.
        let g = st(Effect::Gradient);
        assert!(!Effect::Gradient.is_animated());
        assert_eq!(render(&g, 0.0, 3, 10), render(&g, 5.0, 3, 10));
        assert_ne!(render(&g, 0.0, 0, 10), render(&g, 0.0, 9, 10));
        let white = LightState { color: "ffffff".into(), ..g.clone() };
        assert_ne!(render(&white, 0.0, 0, 10), render(&white, 0.0, 9, 10));
        // Strobe is either the color or off.
        let s = st(Effect::Strobe);
        assert!((0..50).map(|f| render(&s, f as f32 * 0.03, 0, 1)).all(|c| c == Color::new(255, 0, 0) || c == Color::BLACK));
        // Chase lights every third LED.
        let c = st(Effect::Chase);
        assert_eq!((0..30).filter(|&i| render(&c, 0.0, i, 30).r == 255).count(), 10);
        assert_eq!(to_hsv(Color::new(0, 0, 255)), (2.0 / 3.0, 1.0, 1.0));
    }

    #[test]
    fn beat_bass_and_spectrum_with_lights_per_device_zone_or_led() {
        let st = |effect, split, palette, colors| LightState { effect, split, palette, colors, color: "ff0000".into(), speed: 34, ..LightState::default() };
        let at = |s: &LightState, spot: Spot, a: &AudioLevels| render_spot(s, 0.0, &spot, a);
        let led = |i, n| Spot::led(i, n);
        let hue = |c: Color| (to_hsv(c).0 * 100.0).round() as i32;
        // Beat: a flash on the beat, as strong as the beat, gone a second later; dark in silence.
        let beat = |age: f32, strength: f32| AudioLevels { beat_age: age, beat_strength: strength, beat_count: 5, ..AudioLevels::SILENT };
        let b = st(Effect::SoundBeat, Split::Led, Palette::Effect, 3);
        assert_eq!(at(&b, led(0, 10), &beat(0.0, 1.0)), Color::new(255, 0, 0));
        assert!(bright(at(&b, led(0, 10), &beat(0.0, 0.6))) < 200);
        assert!(bright(at(&b, led(0, 10), &beat(1.0, 1.0))) < 20);
        assert!(bright(at(&b, led(0, 10), &AudioLevels::SILENT)) < 10);
        // Random colors per LED: exactly the number asked for, a new set on the next beat.
        let r = st(Effect::SoundBeat, Split::Led, Palette::Random, 3);
        let hues = |a: &AudioLevels| (0..40).map(|i| hue(at(&r, led(i, 40), a))).collect::<std::collections::BTreeSet<_>>();
        assert_eq!(hues(&beat(0.0, 1.0)).len(), 3, "{:?}", hues(&beat(0.0, 1.0)));
        let next = AudioLevels { beat_count: 6, ..beat(0.0, 1.0) };
        assert_ne!(hues(&beat(0.0, 1.0)), hues(&next));
        // A random number of colors (0): 2 to 6, changing from beat to beat.
        let any = st(Effect::SoundBeat, Split::Led, Palette::Random, 0);
        let counts: Vec<usize> = (0..12u32)
            .map(|k| (0..60).map(|i| hue(at(&any, led(i, 60), &AudioLevels { beat_count: k, ..beat(0.0, 1.0) }))).collect::<std::collections::BTreeSet<_>>().len())
            .collect();
        assert!(counts.iter().all(|c| (2..=6).contains(c)) && counts.iter().any(|&c| c != counts[0]), "{counts:?}");
        // Per device: the device acts as one LED (all its LEDs one color); other devices differ.
        let d = st(Effect::SoundBeat, Split::Device, Palette::Random, 8);
        let on = |device: usize, i: usize| at(&d, Spot { device, devices: 5, ..led(i, 10) }, &beat(0.0, 1.0));
        assert!((0..10).all(|i| on(2, i) == on(2, 0)));
        assert!((0..5).any(|k| on(k, 0) != on(0, 0)));
        // "zone" from an earlier version reads as per device; per device is the default.
        assert_eq!(Split::parse("zone"), Some(Split::Device));
        assert_eq!(LightState::default().split, Split::Device);
        // Bass: brighter with the bass hits.
        let bass = st(Effect::SoundBass, Split::Device, Palette::Effect, 3);
        let with = |b: f32| bright(at(&bass, led(0, 1), &AudioLevels { bass: b, bass_punch: b, ..AudioLevels::SILENT }));
        assert!(with(1.0) > 200 && with(0.3) < 60 && with(0.0) < 10, "{} {} {}", with(1.0), with(0.3), with(0.0));
        // Spectrum per LED: only treble lights the end; low to high colors run red to violet.
        let sp = st(Effect::SoundSpectrum, Split::Led, Palette::LowHigh, 3);
        let treble = spectrum(|b| if b >= 18 { 1.0 } else { 0.0 }, 0.0);
        assert!(bright(at(&sp, led(23, 24), &treble)) > 200 && bright(at(&sp, led(0, 24), &treble)) < 10);
        let full = spectrum(|_| 1.0, 0.0);
        let (first, last) = (at(&sp, led(0, 24), &full), at(&sp, led(23, 24), &full));
        assert!(first.r > 200 && first.b < 30 && last.b > 200, "{first:?} {last:?}");
        // Spectrum per device: bass on the first device, treble on the last.
        let spd = st(Effect::SoundSpectrum, Split::Device, Palette::LowHigh, 3);
        let dev = |k: usize| bright(at(&spd, Spot { device: k, devices: 4, ..led(3, 8) }, &treble));
        assert!(dev(3) > 200 && dev(0) < 10, "{} {}", dev(0), dev(3));
        // One light (a single device) shows the whole spectrum: its loudest band.
        assert!(bright(at(&spd, led(0, 8), &treble)) > 200);
        // Cold to hot: loud bands red, quiet ones blue.
        let ch = st(Effect::SoundSpectrum, Split::Led, Palette::ColdHot, 3);
        let mixed = spectrum(|b| if b < 12 { 1.0 } else { 0.45 }, 0.0);
        assert!(hue(at(&ch, led(0, 24), &mixed)) < 5 && hue(at(&ch, led(23, 24), &mixed)) > 30);
        for e in [Effect::SoundBeat, Effect::SoundBass, Effect::SoundSpectrum] {
            assert!(e.is_audio());
            for split in [Split::Device, Split::Led] {
                assert!(bright(at(&st(e, split, Palette::Random, 0), led(2, 5), &AudioLevels::SILENT)) < 10, "{e:?} lit in silence");
            }
        }
        assert_eq!(Effect::parse("sound_spectrum"), Some(Effect::SoundSpectrum));
        assert_eq!(Split::parse("led"), Some(Split::Led));
    }

    /// Synthetic analyzer output: `f(band)` for each band, peaks at `peak` (at least the band).
    fn spectrum(f: impl Fn(usize) -> f32, peak: f32) -> AudioLevels {
        let bands: [f32; AudioLevels::BANDS] = std::array::from_fn(f);
        AudioLevels { bands, peaks: bands.map(|v| v.max(peak)), ..AudioLevels::SILENT }
    }

    fn bright(c: Color) -> u8 {
        c.r.max(c.g).max(c.b)
    }

    #[test]
    fn creative_effects_move_and_light_up() {
        let new = [
            ("plasma", Effect::Plasma),
            ("meteor_shower", Effect::MeteorShower),
            ("lava_lamp", Effect::LavaLamp),
            ("digital_rain", Effect::DigitalRain),
            ("fireworks", Effect::Fireworks),
            ("scanner", Effect::Scanner),
            ("lightning_storm", Effect::LightningStorm),
            ("neon_pulse", Effect::NeonPulse),
        ];
        for (name, e) in new {
            assert_eq!(Effect::parse(name), Some(e));
            assert!(e.is_animated() && !e.is_audio(), "{e:?}");
            let s = LightState { color: "00ff40".into(), ..st(e) };
            // Over 20 s on a 40-LED strip it lights up brightly somewhere, and changes over time.
            let frames: Vec<Vec<Color>> = (0..200).map(|k| (0..40).map(|i| render(&s, k as f32 * 0.1, i, 40)).collect()).collect();
            let max = frames.iter().flatten().map(|c| bright(*c)).max().unwrap();
            assert!(max > 180, "{e:?} never lights up (max {max})");
            assert!(frames.windows(2).filter(|w| w[0] != w[1]).count() > 100, "{e:?} hardly moves");
            // Brightness 0 is dark, and one LED works.
            assert!(bright(render(&LightState { brightness: 0, ..s.clone() }, 1.0, 3, 40)) == 0);
            let _ = render(&s, 1.0, 0, 1);
        }
        // Color-led effects stay in the chosen color family.
        let green = LightState { color: "00ff40".into(), ..st(Effect::Scanner) };
        let c = (0..40).map(|i| render(&green, 0.3, i, 40)).max_by_key(|c| bright(*c)).unwrap();
        assert!(c.g > c.r && c.g > c.b, "{c:?}");
    }

    #[test]
    fn effect_names() {
        assert_eq!(Effect::parse("rainbow_wave"), Some(Effect::RainbowWave));
        assert_eq!(Effect::parse("aurora"), Some(Effect::Aurora));
        assert_eq!(Effect::parse("nope"), None);
        assert!(!Effect::Static.is_animated());
    }

    #[test]
    fn an_unknown_effect_loads_as_static() {
        // A removed effect (or one from a newer version) in a saved state: Static, the rest kept.
        let s: LightState = serde_json::from_str(r#"{"effect":"screen_ambience","color":"00ff00","brightness":40,"split":"led"}"#).unwrap();
        assert_eq!((s.effect, s.color.as_str(), s.brightness, s.split), (Effect::Static, "00ff00", 40, Split::Led));
        let scene = crate::scene::Scene::from_json(r#"{"default":{"effect":"screen_ambience"},"overrides":{"x":{"effect":"breathing"}}}"#).unwrap();
        assert_eq!((scene.default.effect, scene.overrides["x"].effect), (Effect::Static, Effect::Breathing));
        // Asked for by name, an unknown effect is still an error.
        assert_eq!(Effect::parse("screen_ambience"), None);
        assert_eq!(Effect::parse("static"), Some(Effect::Static));
    }

    #[test]
    fn older_sound_effects_become_beat_bass_or_spectrum() {
        for (old, new) in [
            ("beat_flash", Effect::SoundBeat),
            ("sound_pulse", Effect::SoundBass), ("bass_glow", Effect::SoundBass), ("bass_wave", Effect::SoundBass), ("bass_treble", Effect::SoundBass),
            ("spectrum_bars", Effect::SoundSpectrum), ("spectrum_analyzer", Effect::SoundSpectrum), ("vu_meter", Effect::SoundSpectrum),
            ("treble_sparkle", Effect::SoundSpectrum), ("music_flow", Effect::SoundSpectrum),
        ] {
            assert_eq!(Effect::parse(old), Some(new), "{old}");
        }
        // A saved state with one keeps its other settings, and is written back with the new name.
        let s: LightState = serde_json::from_str(r#"{"effect":"bass_wave","color":"00ff00","brightness":40,"speed":70,"palette":"rainbow"}"#).unwrap();
        assert_eq!((s.effect, s.brightness, s.speed, s.palette), (Effect::SoundBass, 40, 70, Palette::Rainbow));
        assert!(serde_json::to_string(&s).unwrap().contains(r#""effect":"sound_bass""#));
    }

    #[test]
    fn the_sound_effects_hit_with_the_music() {
        let st = |effect, palette| LightState { effect, palette, color: "ff0000".into(), split: Split::Led, speed: 34, ..LightState::default() };
        // Loud, compressed music: the level hardly changes, the punch does. The kick lights the bass
        // effect up, the dip between kicks is dark, though the level is high in both.
        let bass = st(Effect::SoundBass, Palette::Effect);
        let kick = AudioLevels { bass: 0.95, bass_punch: 0.95, ..AudioLevels::SILENT };
        let dip = AudioLevels { bass: 0.8, bass_punch: 0.1, ..AudioLevels::SILENT };
        assert!(render_with(&bass, 0.0, 0, 10, &kick).r > 200 && render_with(&bass, 0.0, 0, 10, &dip).r < 60);
        // Sound in general: silent when nothing plays, on every device size and split.
        for e in [Effect::SoundBeat, Effect::SoundBass, Effect::SoundSpectrum] {
            assert!(e.is_audio() && e.is_animated());
            for n in [1, 2, 3, 8, 30] {
                assert!((0..n).all(|i| bright(render(&st(e, Palette::Random), 1.0, i, n)) < 10), "{e:?} lit in silence");
            }
        }
        // Palettes: rainbow along the lights, drifting with the music; fire from red to yellow with
        // the level; neon pink and cyan swapping on the beat.
        let loud = |phase: f32, beat_count: u32| AudioLevels { bass: 1.0, bass_punch: 1.0, phase, beat_count, ..AudioLevels::SILENT };
        let hue = |s: &LightState, i: usize, a: &AudioLevels| to_hsv(render_with(s, 0.0, i, 12, a)).0;
        let rainbow = st(Effect::SoundBass, Palette::Rainbow);
        assert!((0..11).all(|i| hue(&rainbow, i, &loud(0.0, 0)) != hue(&rainbow, i + 1, &loud(0.0, 0))));
        assert!((hue(&rainbow, 0, &loud(2.0, 0)) - 0.1).abs() < 0.02);
        let fire = st(Effect::SoundBass, Palette::Fire);
        let soft = AudioLevels { bass: 0.4, bass_punch: 0.4, ..AudioLevels::SILENT };
        assert!(hue(&fire, 0, &loud(0.0, 0)) > hue(&fire, 0, &soft));
        let neon = st(Effect::SoundBass, Palette::Neon);
        assert_ne!(hue(&neon, 0, &loud(0.0, 0)), hue(&neon, 1, &loud(0.0, 0)));
        assert_eq!(hue(&neon, 0, &loud(0.0, 0)), hue(&neon, 1, &loud(0.0, 1)));
        assert_eq!(Palette::parse("cold_hot"), Some(Palette::ColdHot));
        assert_eq!(Palette::parse("nope"), None);
    }
}
