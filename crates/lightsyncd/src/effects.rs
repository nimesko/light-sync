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
    /// Sound reactive: the chosen color, its brightness follows the loudness (speed = sensitivity).
    SoundPulse,
    /// Sound reactive: the analyzer bands as bar graphs along each device (bass first), each bar
    /// with its own hue and a falling peak dot (speed = sensitivity).
    SpectrumBars,
    /// Sound reactive: the chosen color flashes on each beat and fades (speed = how fast it fades).
    BeatFlash,
    /// Sound reactive: every LED shows one analyzer band, hue by frequency and brightness by its
    /// energy, bass at the start of each device (speed = sensitivity).
    SpectrumAnalyzer,
    /// Sound reactive: the chosen color glows with the bass and shifts hue on each beat
    /// (speed = sensitivity).
    BassGlow,
    /// Sound reactive: a level meter along each device, green to yellow to red, with a falling
    /// peak marker (speed = sensitivity).
    VuMeter,
    /// Sound reactive: the bass as a warm glow rising from the start of each device, the treble as
    /// cool sparkles over it (speed = sensitivity).
    BassTreble,
    /// Sound reactive: LEDs of the chosen color sparkle with the treble (hi-hats, cymbals) over a
    /// glow that follows the bass (speed = sensitivity).
    TrebleSparkle,
    /// Sound reactive: every beat sends a wave of the chosen color from the middle of each device to
    /// its ends; the bass keeps a dim glow in between (speed = how fast the waves run).
    BassWave,
    /// Sound reactive: follows the flow of the music. Colors drift along each device at the song's
    /// tempo; the palette follows its character (bass-heavy, dark songs: deep reds and purples;
    /// bright ones: teals, greens and gold), calm passages are soft and slow, energetic ones
    /// saturated and quick, and each beat gives a kick (speed = sensitivity).
    MusicFlow,
}

impl Effect {
    pub fn is_animated(self) -> bool {
        !matches!(self, Effect::Static | Effect::Gradient)
    }

    /// Whether the effect reacts to sound (the daemon only captures audio while one is shown).
    pub fn is_audio(self) -> bool {
        matches!(
            self,
            Effect::SoundPulse
                | Effect::SpectrumBars
                | Effect::BeatFlash
                | Effect::SpectrumAnalyzer
                | Effect::BassGlow
                | Effect::VuMeter
                | Effect::BassTreble
                | Effect::TrebleSparkle
                | Effect::BassWave
                | Effect::MusicFlow
        )
    }

    pub fn parse(s: &str) -> Option<Effect> {
        serde_json::from_value(serde_json::Value::String(s.into())).ok()
    }
}

/// What the user picked; persisted between runs.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(default)]
pub struct LightState {
    pub effect: Effect,
    /// "RRGGBB"
    pub color: String,
    /// 0..=100
    pub brightness: u8,
    /// 1..=100
    pub speed: u8,
}

impl Default for LightState {
    fn default() -> Self {
        Self { effect: Effect::Static, color: "ffffff".into(), brightness: 100, speed: 50 }
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

/// How far a beat turns the hue in bass_glow (fraction of the color wheel).
const BEAT_HUE_SHIFT: f32 = 0.08;

/// Gain of the sound reactive effects for a speed of 0..1 (1.25 at the default speed).
fn sensitivity(speed: f32) -> f32 {
    lerp(0.5, 2.0, speed)
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

/// The whole spectrum as one color (devices with too few LEDs for bars): hue from the spectral
/// centroid, brightness from the loudest band.
fn spectrum_color(audio: &AudioLevels, sens: f32) -> Color {
    let last = (AudioLevels::BANDS - 1) as f32;
    let (mut sum, mut weighted, mut max) = (0.0, 0.0, 0.0f32);
    for (k, &v) in audio.bands.iter().enumerate() {
        sum += v;
        weighted += v * k as f32 / last;
        max = max.max(v);
    }
    let hue = if sum > 0.0 { band_hue(weighted / sum) } else { 0.0 };
    let v = (max * sens).clamp(0.0, 1.0);
    hsv(hue, 1.0, 0.03 + 0.97 * v * v)
}

/// Level meter colors by position 0..1: green, then yellow, then red at the top.
fn vu_hue(x: f32) -> f32 {
    if x < 0.55 {
        1.0 / 3.0
    } else if x < 0.8 {
        lerp(1.0 / 3.0, 1.0 / 6.0, (x - 0.55) / 0.25)
    } else {
        lerp(1.0 / 6.0, 0.0, ((x - 0.8) / 0.12).min(1.0))
    }
}

/// Color of LED `i` of a device with `n` LEDs at time `t` seconds, brightness included, with no
/// sound (sound reactive effects stay dark; see [`render_with`]).
#[allow(dead_code)] // kept for callers without audio (tests, future tools)
pub fn render(state: &LightState, t: f32, i: usize, n: usize) -> Color {
    render_with(state, t, i, n, &AudioLevels::SILENT)
}

/// Like [`render`], with the audio levels the sound reactive effects follow.
pub fn render_with(state: &LightState, t: f32, i: usize, n: usize, audio: &AudioLevels) -> Color {
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
        Effect::SoundPulse => {
            // The level is already smoothed (fast attack, slower release); squaring it gives the
            // glow more contrast between quiet and loud.
            let k = (audio.level * sensitivity(speed)).clamp(0.0, 1.0);
            mul(base, 0.03 + 0.97 * k * k)
        }
        Effect::SpectrumBars => {
            let sens = sensitivity(speed);
            if n < 3 {
                spectrum_color(audio, sens)
            } else {
                // `bars` bars along the device (at least BAR_LEDS LEDs each), each showing the
                // loudest of its share of the bands and filling from its start.
                const BAR_LEDS: usize = 4;
                let bars = (n / BAR_LEDS).clamp(3, AudioLevels::BANDS);
                let bar = (i * bars / n).min(bars - 1);
                let start = (bar * n).div_ceil(bars);
                let len = ((bar + 1) * n).div_ceil(bars) - start;
                let b0 = bar * AudioLevels::BANDS / bars;
                let b1 = ((bar + 1) * AudioLevels::BANDS / bars).max(b0 + 1);
                let max = |v: &[f32]| (v.iter().cloned().fold(0.0, f32::max) * sens).clamp(0.0, 1.0);
                let (value, peak) = (max(&audio.bands[b0..b1]), max(&audio.peaks[b0..b1]));
                let hue = band_hue((b0 + b1 - 1) as f32 / 2.0 / (AudioLevels::BANDS - 1) as f32);
                let local = i - start;
                // Peak-hold dot: the LED the peak reached, when it is above the bar.
                let top = (peak * len as f32).ceil() as usize;
                if len >= 4 && peak > 0.02 && top >= 1 && local == top - 1 && peak > value + 0.5 / len as f32 {
                    hsv(hue, 0.3, 1.0)
                } else {
                    let lit = (value * len as f32 - local as f32).clamp(0.0, 1.0);
                    hsv(hue, 1.0, 0.04 + 0.96 * lit)
                }
            }
        }
        Effect::BeatFlash => {
            // Full on each beat, fading at a rate set by the speed; a faint glow with the bass.
            let k = (-audio.beat_age.max(0.0) * lerp(2.0, 12.0, speed)).exp();
            mul(base, 0.03 + 0.97 * k.max(0.2 * audio.bass * audio.bass))
        }
        Effect::SpectrumAnalyzer => {
            let sens = sensitivity(speed);
            if n < 3 {
                spectrum_color(audio, sens)
            } else {
                let x = i as f32 / (n - 1) as f32;
                let v = (band_at(&audio.bands, x) * sens).clamp(0.0, 1.0);
                hsv(band_hue(x), 1.0, 0.03 + 0.97 * v * v)
            }
        }
        Effect::BassGlow => {
            let b = (audio.bass * sensitivity(speed)).clamp(0.0, 1.0);
            // Each beat pushes the hue along the wheel; it eases back over half a second or so.
            let shift = BEAT_HUE_SHIFT * (-audio.beat_age.max(0.0) * 4.0).exp();
            let (h, s, v) = to_hsv(base);
            hsv(h + shift, s, v * (0.04 + 0.96 * b * b))
        }
        Effect::VuMeter => {
            let sens = sensitivity(speed);
            let level = (audio.level * sens).clamp(0.0, 1.0);
            let peak = (audio.level_peak * sens).clamp(0.0, 1.0);
            if n == 1 {
                hsv(vu_hue(level), 1.0, 0.03 + 0.97 * level)
            } else {
                let x = (i as f32 + 0.5) / n as f32;
                let top = (peak * n as f32).ceil() as usize;
                if n >= 4 && peak > 0.02 && top >= 1 && i == top - 1 && peak > level + 0.5 / n as f32 {
                    hsv(vu_hue(x), 1.0, 1.0)
                } else {
                    let lit = (level * n as f32 - i as f32).clamp(0.0, 1.0);
                    hsv(vu_hue(x), 1.0, 0.03 + 0.97 * lit)
                }
            }
        }
        Effect::BassTreble => {
            let sens = sensitivity(speed);
            let bass = (audio.bass * sens).clamp(0.0, 1.0);
            let treble = (audio.treble * sens).clamp(0.0, 1.0);
            // Warm bass glow, fullest at the start of the device and rising further with the bass.
            let reach = if n > 1 { (bass * 1.2 - pos).clamp(0.0, 1.0).max(0.25 * bass) } else { bass };
            let warm = hsv(0.97 + 0.05 * bass, 1.0, reach * reach);
            // Cool sparkles: each LED flickers on its own clock, more of them the more treble.
            let slot = (t * 18.0).floor() as u32;
            let sparkle = if hash(i as u32, slot) < 0.6 * treble * treble { 0.4 + 0.6 * treble } else { 0.0 };
            add(warm, hsv(0.55, 0.35, sparkle))
        }
        Effect::TrebleSparkle => {
            let sens = sensitivity(speed);
            let bass = (audio.bass * sens).clamp(0.0, 1.0);
            let treble = (audio.treble * sens).clamp(0.0, 1.0);
            // A sparkle lasts a few frames and fades; how many LEDs sparkle follows the treble.
            let rate = 14.0;
            let slot = (t * rate).floor() as u32;
            let age = (t * rate).fract();
            let on = hash(i as u32, slot.wrapping_mul(7)) < 0.15 + 0.6 * treble * treble && treble > 0.05;
            let glow = 0.05 + 0.3 * bass * bass;
            let k = if on { glow.max(1.0 - age) } else { glow };
            let (h, s, _) = to_hsv(base);
            // Sparkles go a little whiter than the glow.
            hsv(h, if on { s * 0.6 } else { s }, k)
        }
        Effect::BassWave => {
            // Each beat launches a wave from the middle to the ends; speed = how fast it travels.
            let travel = lerp(0.8, 4.0, speed);
            let front = audio.beat_age.max(0.0) * travel;
            let x = if n > 1 { (pos + 0.5 / n as f32 - 0.5).abs() * 2.0 } else { 0.0 };
            const WIDTH: f32 = 0.35;
            let wave = (1.0 - (x - front).abs() / WIDTH).max(0.0) * (1.0 - front / 1.6).max(0.0);
            let glow = 0.04 + 0.2 * audio.bass * audio.bass;
            let (h, s, _) = to_hsv(base);
            hsv(h + 0.04 * audio.bass, s, glow.max(wave))
        }
        Effect::MusicFlow => {
            let sens = sensitivity(speed);
            let energy = (audio.energy * sens).clamp(0.0, 1.0);
            // Palette from the song's character: dark / bass-heavy -> deep reds, magentas, purples;
            // bright -> teals, greens, gold. Neighbouring hues spread along the device.
            let mood = audio.mood.clamp(0.0, 1.0);
            let centre = lerp(0.88, 1.38, mood); // 0.88 purple .. 1.38 (= 0.38) green-teal, via red/gold
            let spread = lerp(0.06, 0.16, energy);
            // Waves drift along at the music's pace (one wave per beat).
            let w = (std::f32::consts::TAU * (pos * 1.5 - audio.phase * 0.25)).sin();
            let w2 = (std::f32::consts::TAU * (pos * 0.7 + audio.phase * 0.11)).sin();
            let hue = centre + spread * (0.7 * w + 0.3 * w2) + 0.08 * (audio.centroid - mood);
            // Calm: soft pastel and dim; energetic: saturated and bright; a kick on each beat.
            let kick = (-audio.beat_age.max(0.0) * 6.0).exp() * (0.3 + 0.7 * energy);
            let sat = lerp(0.45, 1.0, energy);
            let v = (0.12 + 0.55 * energy + 0.25 * (0.5 + 0.5 * w) * energy + 0.35 * kick).min(1.0);
            let level = (audio.level * sens).clamp(0.0, 1.0);
            hsv(hue, sat, v * (0.35 + 0.65 * level.max(energy)))
        }
    };
    mul(c, state.brightness.min(100) as f32 / 100.0)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn st(effect: Effect) -> LightState {
        LightState { effect, color: "ff0000".into(), brightness: 100, speed: 50 }
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

    fn sound(level: f32, bass: f32, mid: f32, treble: f32, beat_age: f32) -> AudioLevels {
        AudioLevels { level, bass, mid, treble, beat: beat_age < AudioLevels::BEAT_HOLD, beat_age, ..AudioLevels::SILENT }
    }

    #[test]
    fn sound_pulse_follows_the_level() {
        let s = st(Effect::SoundPulse);
        let quiet = render_with(&s, 0.0, 0, 10, &sound(0.1, 0.0, 0.0, 0.0, 9.0));
        let loud = render_with(&s, 0.0, 0, 10, &sound(0.9, 0.0, 0.0, 0.0, 9.0));
        assert!(loud.r > quiet.r && quiet.r > 0, "{quiet:?} {loud:?}");
        assert_eq!((loud.g, loud.b), (0, 0), "keeps the chosen color");
        assert_eq!(render_with(&s, 0.0, 0, 10, &sound(1.0, 0.0, 0.0, 0.0, 9.0)).r, 255);
        // Silence: a faint glow, and plain render() is silent.
        assert!(render(&s, 0.0, 0, 10).r < 10);
        assert_eq!(render(&s, 0.0, 0, 10), render_with(&s, 0.0, 0, 10, &AudioLevels::SILENT));
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
    fn spectrum_bars_fill_with_each_band() {
        let s = LightState { speed: 34, ..st(Effect::SpectrumBars) }; // sensitivity ~1
        // 32 LEDs: 8 bars of 4 LEDs, 3 bands each.
        let lit = |a: &AudioLevels, bar: usize| {
            (bar * 4..bar * 4 + 4).filter(|&i| bright(render_with(&s, 0.0, i, 32, a)) > 128).count()
        };
        // Bass loud, mid half, treble silent.
        let a = spectrum(|b| if b < 6 { 1.0 } else if b < 15 { 0.5 } else { 0.0 }, 0.0);
        assert_eq!(lit(&a, 0), 4, "bass bar full");
        assert_eq!(lit(&a, 3), 2, "mid bar half");
        assert_eq!(lit(&a, 7), 0, "treble bar empty");
        // Bars fill from their start.
        assert!(bright(render_with(&s, 0.0, 12, 32, &a)) > 200 && bright(render_with(&s, 0.0, 15, 32, &a)) < 20);
        // Each bar has its own hue: bass red, treble blue/violet.
        let full = spectrum(|_| 1.0, 0.0);
        let first = render_with(&s, 0.0, 0, 32, &full);
        let last = render_with(&s, 0.0, 31, 32, &full);
        assert!(first.r > 200 && first.b < 50 && last.b > 200 && last.g < 50, "{first:?} {last:?}");
        assert_ne!(render_with(&s, 0.0, 8, 32, &full), render_with(&s, 0.0, 12, 32, &full));
        // A peak above the bar shows as a whitish dot at the height it reached.
        let p = spectrum(|b| if b < 3 { 0.25 } else { 0.0 }, 0.0);
        let mut p2 = p;
        p2.peaks[1] = 1.0;
        let dot = render_with(&s, 0.0, 3, 32, &p2);
        assert!(dot.g > 60 && dot.b > 60 && dot.r > 200, "dot {dot:?}");
        assert!(bright(render_with(&s, 0.0, 3, 32, &p)) < 20, "no dot without a peak");
        // Silence: all dim. A single LED still reacts.
        assert_eq!((0..8).map(|b| lit(&AudioLevels::SILENT, b)).sum::<usize>(), 0);
        assert!(bright(render_with(&s, 0.0, 0, 1, &full)) > 200);
        assert!(bright(render_with(&s, 0.0, 0, 1, &AudioLevels::SILENT)) < 10);
        let bass = render_with(&s, 0.0, 0, 1, &spectrum(|b| if b < 3 { 1.0 } else { 0.0 }, 0.0));
        assert!(bass.r > 200 && bass.b < 10, "bass alone is red: {bass:?}");
        // Few LEDs still get bars.
        let few = render_with(&s, 0.0, 0, 3, &a);
        assert!(bright(few) > 200 && bright(render_with(&s, 0.0, 2, 3, &a)) < 20);
    }

    #[test]
    fn spectrum_analyzer_maps_bands_along_the_device() {
        let s = LightState { speed: 34, ..st(Effect::SpectrumAnalyzer) };
        // Only treble: the end of the device is lit, the start dark, on a strip and on a fan.
        let treble = spectrum(|b| if b >= 18 { 1.0 } else { 0.0 }, 0.0);
        for n in [8, 16, 60] {
            assert!(bright(render_with(&s, 0.0, n - 1, n, &treble)) > 200, "n={n}");
            assert!(bright(render_with(&s, 0.0, 0, n, &treble)) < 10, "n={n}");
            assert!(bright(render_with(&s, 0.0, n / 3, n, &treble)) < 10, "n={n}");
        }
        // Hue by frequency, brightness by energy.
        let full = spectrum(|_| 1.0, 0.0);
        let (a, z) = (render_with(&s, 0.0, 0, 24, &full), render_with(&s, 0.0, 23, 24, &full));
        assert!(a.r > 200 && a.b < 30 && z.b > 200 && z.r < 200, "{a:?} {z:?}");
        let half = spectrum(|_| 0.5, 0.0);
        assert!(bright(render_with(&s, 0.0, 5, 24, &half)) < bright(render_with(&s, 0.0, 5, 24, &full)));
        // Between two bands the value is interpolated.
        let step = spectrum(|b| if b >= 12 { 1.0 } else { 0.0 }, 0.0);
        let mid = (0..47).map(|i| bright(render_with(&s, 0.0, i, 47, &step))).collect::<Vec<_>>();
        assert!(mid.iter().any(|&v| v > 20 && v < 200), "{mid:?}");
        // Silence is dark; a single LED shows the whole spectrum.
        assert!((0..24).all(|i| bright(render_with(&s, 0.0, i, 24, &AudioLevels::SILENT)) < 10));
        let one = render_with(&s, 0.0, 0, 1, &treble);
        assert!(one.b > 200 && one.r < 200, "{one:?}");
    }

    #[test]
    fn bass_glow_follows_the_bass_and_shifts_on_beats() {
        let s = st(Effect::BassGlow);
        let quiet = render_with(&s, 0.0, 0, 10, &sound(0.5, 0.2, 1.0, 1.0, 9.0));
        let loud = render_with(&s, 0.0, 0, 10, &sound(0.5, 0.9, 0.0, 0.0, 9.0));
        assert!(loud.r > quiet.r + 100, "{quiet:?} {loud:?}");
        assert_eq!((loud.g, loud.b), (0, 0), "the chosen color between beats");
        assert!(render(&s, 0.0, 0, 10).r < 15, "dark in silence");
        // On a beat the hue moves towards orange, then eases back.
        let beat = render_with(&s, 0.0, 0, 10, &sound(0.5, 0.9, 0.0, 0.0, 0.0));
        let later = render_with(&s, 0.0, 0, 10, &sound(0.5, 0.9, 0.0, 0.0, 0.3));
        assert!(beat.g > later.g && later.g > loud.g, "{beat:?} {later:?} {loud:?}");
    }

    #[test]
    fn vu_meter_shows_the_level() {
        let s = LightState { speed: 34, ..st(Effect::VuMeter) };
        let vu = |level: f32, peak: f32, i: usize| {
            render_with(&s, 0.0, i, 20, &AudioLevels { level, level_peak: peak, ..AudioLevels::SILENT })
        };
        let lit = |level: f32, peak: f32| (0..20).filter(|&i| bright(vu(level, peak, i)) > 128).count();
        assert_eq!(lit(0.5, 0.5), 10);
        assert_eq!(lit(1.0, 1.0), 20);
        assert_eq!(lit(0.0, 0.0), 0);
        // Green at the bottom, yellow in the middle-top, red at the top.
        let (g, y, r) = (vu(1.0, 1.0, 0), vu(1.0, 1.0, 14), vu(1.0, 1.0, 19));
        assert!(g.g > 200 && g.r < 30, "{g:?}");
        assert!(y.r > 150 && y.g > 150 && y.b < 30, "{y:?}");
        assert!(r.r > 200 && r.g < 30, "{r:?}");
        // The peak marker sits above the bar.
        assert_eq!(lit(0.3, 0.8), 7);
        assert!(bright(vu(0.3, 0.8, 16)) > 200 && bright(vu(0.3, 0.8, 12)) < 20);
        // One LED: color and brightness by level.
        let one = |level: f32| render_with(&s, 0.0, 0, 1, &AudioLevels { level, ..AudioLevels::SILENT });
        assert!(one(0.2).g > one(0.2).r && one(1.0).r > one(1.0).g && bright(one(0.0)) < 10);
    }

    #[test]
    fn beat_flash_flashes_and_decays() {
        let s = st(Effect::BeatFlash);
        let at = |age: f32| render_with(&s, 0.0, 3, 10, &sound(0.5, 0.5, 0.5, 0.5, age)).r;
        assert_eq!(at(0.0), 255);
        assert!(at(0.05) < 255 && at(0.05) > at(0.2) && at(0.2) > at(1.0));
        assert!(at(5.0) < 30);
        assert!(render(&s, 0.0, 0, 1).r < 10, "no beats without sound");
    }

    #[test]
    fn music_following_effects() {
        let s = |e| LightState { speed: 34, color: "00aaff".into(), ..st(e) };
        // Bass & treble: bass lights the start warm; treble adds sparkles across the device.
        let bass = AudioLevels { bass: 1.0, ..AudioLevels::SILENT };
        let c = render_with(&s(Effect::BassTreble), 0.0, 0, 20, &bass);
        assert!(c.r > 150 && c.b < 120, "warm bass at the start: {c:?}");
        let treble = AudioLevels { treble: 1.0, ..AudioLevels::SILENT };
        let lit = (0..40).filter(|&i| bright(render_with(&s(Effect::BassTreble), 0.0, i, 40, &treble)) > 60).count();
        assert!(lit > 5 && lit < 40, "some sparkles, not all: {lit}");
        // Treble sparkle: more treble, more sparkles.
        let count = |tr: f32| (0..60).filter(|&i| bright(render_with(&s(Effect::TrebleSparkle), 0.03, i, 60, &AudioLevels { treble: tr, ..AudioLevels::SILENT })) > 100).count();
        assert!(count(1.0) > count(0.3), "{} vs {}", count(1.0), count(0.3));
        // Bass wave: right after a beat the middle is lit, the ends not yet; later the ends.
        let beat = |age: f32| AudioLevels { beat_age: age, bass: 0.5, ..AudioLevels::SILENT };
        let wave = |i: usize, age: f32| bright(render_with(&s(Effect::BassWave), 0.0, i, 21, &beat(age)));
        assert!(wave(10, 0.0) > 200 && wave(0, 0.0) < 80, "starts in the middle");
        assert!(wave(0, 0.4) > wave(10, 0.4), "reaches the ends");
        // Music flow: dark, bass-heavy songs get reds/purples, bright ones teal/green/gold; calm is dim.
        let flow = |mood: f32, energy: f32| render_with(&s(Effect::MusicFlow), 0.0, 3, 10, &AudioLevels { mood, energy, level: energy, centroid: mood, ..AudioLevels::SILENT });
        let (dark, light) = (flow(0.1, 0.8), flow(0.9, 0.8));
        let (hd, _, _) = to_hsv(dark);
        let (hl, _, _) = to_hsv(light);
        assert!(hd > 0.75 || hd < 0.05, "dark songs: purple to red, got hue {hd}");
        assert!((0.08..0.55).contains(&hl), "bright songs: gold to teal, got hue {hl}");
        assert!(bright(flow(0.5, 0.05)) < bright(flow(0.5, 0.9)), "calm is softer than energetic");
        // It moves with the music's clock (phase), not the wall clock.
        let at = |phase: f32| render_with(&s(Effect::MusicFlow), 0.0, 3, 10, &AudioLevels { mood: 0.5, energy: 0.8, level: 0.8, phase, ..AudioLevels::SILENT });
        assert_eq!(at(1.0), render_with(&s(Effect::MusicFlow), 99.0, 3, 10, &AudioLevels { mood: 0.5, energy: 0.8, level: 0.8, phase: 1.0, ..AudioLevels::SILENT }));
        assert_ne!(at(1.0), at(2.0));
        for (name, e) in [("bass_treble", Effect::BassTreble), ("treble_sparkle", Effect::TrebleSparkle), ("bass_wave", Effect::BassWave), ("music_flow", Effect::MusicFlow)] {
            assert_eq!(Effect::parse(name), Some(e));
        }
    }

    #[test]
    fn audio_effects() {
        let all = [
            Effect::SoundPulse, Effect::SpectrumBars, Effect::BeatFlash, Effect::SpectrumAnalyzer, Effect::BassGlow, Effect::VuMeter,
            Effect::BassTreble, Effect::TrebleSparkle, Effect::BassWave, Effect::MusicFlow,
        ];
        for e in all {
            assert!(e.is_audio() && e.is_animated(), "{e:?}");
            // Silence keeps every LED dim, on any device size.
            for n in [1, 2, 3, 8, 30] {
                for i in 0..n {
                    assert!(bright(render(&st(e), 1.0, i, n)) < 15, "{e:?} lit in silence");
                }
            }
        }
        assert!(!Effect::Spectrum.is_audio() && !Effect::Static.is_audio());
        assert_eq!(Effect::parse("sound_pulse"), Some(Effect::SoundPulse));
        assert_eq!(Effect::parse("spectrum_bars"), Some(Effect::SpectrumBars));
        assert_eq!(Effect::parse("beat_flash"), Some(Effect::BeatFlash));
        assert_eq!(Effect::parse("spectrum_analyzer"), Some(Effect::SpectrumAnalyzer));
        assert_eq!(Effect::parse("bass_glow"), Some(Effect::BassGlow));
        assert_eq!(Effect::parse("vu_meter"), Some(Effect::VuMeter));
    }

    #[test]
    fn effect_names() {
        assert_eq!(Effect::parse("rainbow_wave"), Some(Effect::RainbowWave));
        assert_eq!(Effect::parse("aurora"), Some(Effect::Aurora));
        assert_eq!(Effect::parse("nope"), None);
        assert!(!Effect::Static.is_animated());
    }
}
