//! Sound input for the sound reactive effects: what the default output device is playing, captured
//! with WASAPI loopback (cpal) on its own thread, analysed like a hardware spectrum analyzer.
//!
//! Capture only runs while some visible state uses an audio effect (the daemon calls
//! [`AudioSource::set_active`] every frame). Audio is analysed in memory only: it is never stored,
//! logged or sent anywhere; only the levels in [`AudioLevels`] leave this module.
//!
//! The analysis ([`Analyzer`]): a 2048-point FFT (at 48 kHz, scaled with the sample rate) every
//! 512 samples (~94 times a second), 24 log-spaced bands from 30 Hz to 16 kHz with a perceptual
//! tilt, per-band automatic gain shown on a dB scale, fast-attack / slow-release smoothing,
//! falling peaks, and beats from spectral-flux onsets in the bass with an adaptive threshold.

use crate::effects::AudioLevels;
use rustfft::num_complex::Complex;
use rustfft::{Fft, FftPlanner};
use std::sync::Arc;

/// What the UI is told about sound capture.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct AudioStatus {
    /// Capture is wanted (an audio effect is visible).
    pub active: bool,
    /// The output device being captured, if capture is running.
    pub device: Option<String>,
}

/// Where the daemon gets audio levels from. The real one captures sound; tests use fakes.
pub trait AudioSource: Send {
    /// Start (true) or stop (false) capturing. Called every frame; cheap when nothing changes.
    fn set_active(&mut self, on: bool);
    /// The latest levels (silent when not capturing).
    fn levels(&self) -> AudioLevels;
    fn status(&self) -> AudioStatus;
}

/// No sound at all (tests, and before a real source is plugged in).
pub struct NoAudio;

impl AudioSource for NoAudio {
    fn set_active(&mut self, _on: bool) {}
    fn levels(&self) -> AudioLevels {
        AudioLevels::SILENT
    }
    fn status(&self) -> AudioStatus {
        AudioStatus::default()
    }
}

const BANDS: usize = AudioLevels::BANDS;

/// FFT length at 48 kHz (~43 ms); other rates use the power of two closest to the same duration,
/// so the frequency resolution (~23 Hz per bin) stays the same.
const FFT_48K: usize = 2048;
/// Windows overlap: a new analysis every FFT length / `HOP_DIV` samples (512 = 10.7 ms at 48 kHz).
const HOP_DIV: usize = 4;
/// Analyzer range (Hz): `BANDS` log-spaced bands between these.
const F_LO: f32 = 30.0;
const F_HI: f32 = 16000.0;
/// A band is never narrower than this many FFT bins (the low bands share bins with neighbours).
const MIN_BAND_BINS: f32 = 1.0;
/// Bands centred below these frequencies count as bass, then mid; the rest is treble.
const BASS_MAX: f32 = 250.0;
const MID_MAX: f32 = 4000.0;
/// Perceptual tilt (A-weighting-like, without its deep bass cut): +dB per octave above 1 kHz,
/// clamped, so treble is not always dimmer than the bass that dominates music.
const TILT_DB_PER_OCT: f32 = 3.0;
const TILT_MIN_DB: f32 = -6.0;
const TILT_MAX_DB: f32 = 12.0;
/// Display scale: a band this many dB below its gain reference shows 0, at the reference 1.
const RANGE_DB: f32 = 24.0;
/// Automatic gain: each band's reference is its running maximum, falling back over `REF_TAU`
/// seconds; the loudest band's reference falls over `GLOBAL_TAU`.
const REF_TAU: f32 = 3.0;
const GLOBAL_TAU: f32 = 6.0;
/// A band's reference never drops below this fraction of the loudest band's (-20 dB: a band
/// that only carries leakage or noise is not blown up to full scale)...
const BAND_REF_MIN: f32 = 0.1;
/// ...nor below this absolute amplitude (-40 dBFS: near-silence stays dark).
const REF_FLOOR: f32 = 0.01;
/// Smoothing time constants (seconds): bands jump up and fall back slower.
const ATTACK: f32 = 0.008;
const RELEASE: f32 = 0.12;
/// Peaks hold this long, then fall at this rate (full scale per second).
const PEAK_HOLD: f32 = 0.25;
const PEAK_FALL: f32 = 1.2;
/// Beats: spectral flux over the bands centred below this frequency (kick drum, bass)...
const BEAT_MAX_HZ: f32 = 150.0;
/// ...comparing each band with its value this many analyses earlier (the window spans 4 hops,
/// so an attack shows up over several analyses)...
const FLUX_LAG: usize = 2;
/// ...log-compressed as ln(1 + amplitude / FLUX_REF), so the flux does not depend on loudness...
const FLUX_REF: f32 = 0.001;
/// ...above mean + FLUX_K * std of the flux over the last `FLUX_WINDOW` seconds, and above
/// FLUX_MIN (mean rise in nepers; 0.35 ≈ 3 dB)...
const FLUX_WINDOW: f32 = 1.0;
const FLUX_K: f32 = 1.5;
const FLUX_MIN: f32 = 0.35;
/// ...and at least this long after the previous beat (or `TEMPO_GAP` of the tempo's period).
const BEAT_MIN_GAP: f32 = 0.2;
const TEMPO_GAP: f32 = 0.45;
/// Tempo: the median of the last intervals between beats within this range (seconds).
const IOI_MIN: f32 = 0.25;
const IOI_MAX: f32 = 1.5;
const IOIS: usize = 8;
/// Values below this snap to 0 (no denormals, and silence is exactly dark).
const TINY: f32 = 1.0e-4;
/// The music's character (`mood`: the centroid) and `energy` (the level) follow over this long.
const MOOD_TAU: f32 = 3.0;
const ENERGY_TAU: f32 = 2.5;
/// `centroid` follows the sound this quickly.
const CENTROID_TAU: f32 = 0.15;
/// `phase` wraps here: a whole number of cycles for the waves effects draw from it (x 0.25 and x 0.11),
/// so the wrap is invisible.
const PHASE_WRAP: f32 = 400.0;
/// `phase` runs at this many beats per second while the tempo is not known yet.
const DEFAULT_BPS: f32 = 2.0;

/// Per-analysis smoothing factors (the analysis interval is fixed).
#[derive(Debug, Clone, Copy)]
struct Rates {
    dt: f32,
    ref_fall: f32,
    attack: f32,
    release: f32,
}

/// One analyzer channel (a band, or level/bass/mid/treble): automatic gain, dB display scale,
/// attack/release smoothing and a falling peak.
#[derive(Debug, Clone, Copy, Default)]
struct Meter {
    reference: f32,
    out: f32,
    peak: f32,
    hold: f32,
}

impl Meter {
    fn step(&mut self, raw: f32, floor: f32, r: &Rates) {
        self.reference = (self.reference * r.ref_fall).max(raw).max(floor);
        let x = raw / self.reference;
        let target = if x > 0.0 { (1.0 + 20.0 * x.log10() / RANGE_DB).clamp(0.0, 1.0) } else { 0.0 };
        let k = if target > self.out { r.attack } else { r.release };
        self.out += (target - self.out) * k;
        if self.out < TINY {
            self.out = 0.0;
        }
        if self.out >= self.peak {
            self.peak = self.out;
            self.hold = PEAK_HOLD;
        } else if self.hold > 0.0 {
            self.hold -= r.dt;
        } else {
            self.peak = (self.peak - PEAK_FALL * r.dt).max(self.out);
        }
    }
}

/// Turns a stream of samples into [`AudioLevels`]. Pure computation, so it is unit-tested with
/// synthetic signals. Allocates only in [`Analyzer::new`].
pub struct Analyzer {
    rate: f32,
    n: usize,
    hop: usize,
    fft: Arc<dyn Fft<f32>>,
    window: Vec<f32>,
    /// The last `n` mono samples (a ring; `pos` is the oldest).
    ring: Vec<f32>,
    pos: usize,
    since_hop: usize,
    buf: Vec<Complex<f32>>,
    scratch: Vec<Complex<f32>>,
    power: Vec<f32>,
    /// FFT bins and their weights per band: `taps[band_taps[b].0..band_taps[b].1]`.
    taps: Vec<(usize, f32)>,
    band_taps: [(usize, usize); BANDS],
    /// Amplitude factor of the perceptual tilt per band.
    tilt: [f32; BANDS],
    /// Bands [0, bass_end) are bass, [bass_end, mid_end) mid, the rest treble.
    bass_end: usize,
    mid_end: usize,
    /// Bands [0, beat_end) feed the beat detector.
    beat_end: usize,
    rates: Rates,
    global_fall: f32,
    global_ref: f32,
    raw: [f32; BANDS],
    bands: [Meter; BANDS],
    /// Level, bass, mid, treble.
    agg: [Meter; 4],
    /// Log-compressed beat bands of the last `FLUX_LAG + 1` analyses (a ring).
    lagged: [[f32; BANDS]; FLUX_LAG + 1],
    lag_pos: usize,
    /// Flux over the last `FLUX_WINDOW` seconds (a ring), and the two latest values.
    flux: Vec<f32>,
    flux_pos: usize,
    flux_prev: [f32; 2],
    since_beat: f32,
    iois: [f32; IOIS],
    ioi_count: usize,
    /// Beats detected so far.
    pub beats: u64,
    centroid: f32,
    mood: f32,
    energy: f32,
    phase: f32,
}

impl Analyzer {
    pub fn new(rate: u32) -> Self {
        let rate = rate.max(8000) as f32;
        let n = (1usize << (FFT_48K as f32 * rate / 48000.0).log2().round() as u32).clamp(512, 16384);
        let hop = n / HOP_DIV;
        let dt = hop as f32 / rate;
        let window = (0..n).map(|k| 0.5 - 0.5 * (std::f32::consts::TAU * k as f32 / n as f32).cos()).collect();
        let fft = FftPlanner::new().plan_fft_forward(n);
        let scratch = vec![Complex::default(); fft.get_inplace_scratch_len()];
        let bin_hz = rate / n as f32;
        let nyquist = rate / 2.0;
        let mut taps = Vec::new();
        let mut band_taps = [(0, 0); BANDS];
        let mut tilt = [1.0; BANDS];
        let (mut bass_end, mut mid_end, mut beat_end) = (0, 0, 0);
        let edge = |k: usize| F_LO * (F_HI / F_LO).powf(k as f32 / BANDS as f32);
        for b in 0..BANDS {
            let (mut lo, mut hi) = (edge(b), edge(b + 1).min(nyquist));
            let centre = (lo * hi).sqrt();
            let min_w = MIN_BAND_BINS * bin_hz;
            if hi - lo < min_w {
                lo = (centre - min_w / 2.0).max(0.0);
                hi = lo + min_w;
            }
            let start = taps.len();
            for k in 1..=n / 2 {
                let (a, z) = ((k as f32 - 0.5) * bin_hz, (k as f32 + 0.5) * bin_hz);
                let w = (hi.min(z) - lo.max(a)) / bin_hz;
                if w > 0.0 {
                    taps.push((k, w.min(1.0)));
                }
            }
            band_taps[b] = (start, taps.len());
            let db = (TILT_DB_PER_OCT * (centre / 1000.0).log2()).clamp(TILT_MIN_DB, TILT_MAX_DB);
            tilt[b] = 10f32.powf(db / 20.0);
            if centre < BASS_MAX {
                bass_end = b + 1;
            }
            if centre < MID_MAX {
                mid_end = b + 1;
            }
            if centre < BEAT_MAX_HZ {
                beat_end = b + 1;
            }
        }
        let fall = |tau: f32| (-dt / tau).exp();
        Self {
            rate,
            n,
            hop,
            fft,
            window,
            ring: vec![0.0; n],
            pos: 0,
            since_hop: 0,
            buf: vec![Complex::default(); n],
            scratch,
            power: vec![0.0; n / 2 + 1],
            taps,
            band_taps,
            tilt,
            bass_end,
            mid_end,
            beat_end: beat_end.max(1),
            rates: Rates { dt, ref_fall: fall(REF_TAU), attack: 1.0 - fall(ATTACK), release: 1.0 - fall(RELEASE) },
            global_fall: fall(GLOBAL_TAU),
            global_ref: REF_FLOOR,
            raw: [0.0; BANDS],
            bands: [Meter::default(); BANDS],
            agg: [Meter::default(); 4],
            lagged: [[0.0; BANDS]; FLUX_LAG + 1],
            lag_pos: 0,
            flux: vec![0.0; ((FLUX_WINDOW / dt).round() as usize).max(8)],
            flux_pos: 0,
            flux_prev: [0.0; 2],
            since_beat: 1.0e6,
            iois: [0.0; IOIS],
            ioi_count: 0,
            beats: 0,
            centroid: 0.0,
            mood: 0.5,
            energy: 0.0,
            phase: 0.0,
        }
    }

    /// Seconds between two analyses (the update rate of the levels).
    #[allow(dead_code)] // diagnostics and tests
    pub fn interval(&self) -> f32 {
        self.rates.dt
    }

    /// Feed interleaved samples (-1..1) with `channels` channels; they are mixed down to mono.
    pub fn push(&mut self, samples: &[f32], channels: usize) {
        let ch = channels.max(1);
        for frame in samples.chunks(ch) {
            let s = frame.iter().sum::<f32>() / frame.len() as f32;
            self.sample(if s.is_finite() { s } else { 0.0 });
        }
    }

    /// Feed `frames` frames of silence (loopback delivers nothing while nothing plays).
    pub fn push_silence(&mut self, frames: usize) {
        // Long gaps only need enough silence to let everything decay.
        for _ in 0..frames.min(self.rate as usize * 2) {
            self.sample(0.0);
        }
    }

    fn sample(&mut self, s: f32) {
        self.ring[self.pos] = s;
        self.pos = (self.pos + 1) % self.n;
        self.since_hop += 1;
        if self.since_hop >= self.hop {
            self.since_hop = 0;
            self.analyze();
        }
    }

    fn analyze(&mut self) {
        let n = self.n;
        for k in 0..n {
            let s = self.ring[(self.pos + k) % n];
            self.buf[k] = Complex::new(s * self.window[k], 0.0);
        }
        self.fft.process_with_scratch(&mut self.buf, &mut self.scratch);
        for (p, c) in self.power.iter_mut().zip(&self.buf) {
            *p = c.norm_sqr();
        }
        // Band amplitudes, scaled so a sine of amplitude A whose energy falls in a band reads about
        // A (Hann window: the positive half of the spectrum holds 3/32 N² A² of power).
        let scale = (32.0f32 / 3.0).sqrt() / n as f32;
        let mut loudest = 0.0f32;
        for b in 0..BANDS {
            let (a, z) = self.band_taps[b];
            let sum: f32 = self.taps[a..z].iter().map(|&(k, w)| self.power[k] * w).sum();
            let v = sum.sqrt() * scale * self.tilt[b];
            self.raw[b] = if v.is_finite() { v } else { 0.0 };
            loudest = loudest.max(self.raw[b]);
        }
        self.global_ref = (self.global_ref * self.global_fall).max(loudest).max(REF_FLOOR);
        let floor = (self.global_ref * BAND_REF_MIN).max(REF_FLOOR);
        let rates = self.rates;
        for b in 0..BANDS {
            self.bands[b].step(self.raw[b], floor, &rates);
        }
        let energy = |r: &[f32]| r.iter().map(|v| v * v).sum::<f32>().sqrt();
        let agg = [
            energy(&self.raw),
            energy(&self.raw[..self.bass_end]),
            energy(&self.raw[self.bass_end..self.mid_end]),
            energy(&self.raw[self.mid_end..]),
        ];
        for (m, raw) in self.agg.iter_mut().zip(agg) {
            m.step(raw, floor, &rates);
        }
        self.detect_beat();
        self.follow_flow();
    }

    /// The music's flow: how bright the sound is (centroid), its character over a few seconds
    /// (mood), how energetic it is (energy), and a clock that runs at its tempo (phase).
    fn follow_flow(&mut self) {
        let dt = self.rates.dt;
        let k = |tau: f32| 1.0 - (-dt / tau).exp();
        let last = (BANDS - 1) as f32;
        let (mut sum, mut weighted) = (0.0f32, 0.0f32);
        for (b, m) in self.bands.iter().enumerate() {
            sum += m.out;
            weighted += m.out * b as f32 / last;
        }
        let level = self.agg[0].out;
        // Only while something plays: silence keeps the last character.
        if sum > 0.3 {
            let c = weighted / sum;
            self.centroid += (c - self.centroid) * k(CENTROID_TAU);
            self.mood += (self.centroid - self.mood) * k(MOOD_TAU);
        }
        self.energy += (level - self.energy) * k(ENERGY_TAU);
        if self.energy < TINY {
            self.energy = 0.0;
        }
        // One step per beat at the song's tempo; calm music moves slower, silence stops it.
        let bps = self.period().map_or(DEFAULT_BPS, |p| 1.0 / p);
        self.phase = (self.phase + dt * bps * (0.3 + 0.9 * self.energy) * (self.energy > 0.02) as u8 as f32) % PHASE_WRAP;
    }

    /// Spectral-flux onset detection on the bass bands.
    fn detect_beat(&mut self) {
        let dt = self.rates.dt;
        self.since_beat += dt;
        if self.since_beat > IOI_MAX * 2.0 {
            self.ioi_count = 0; // the music stopped or changed: forget the tempo
        }
        let nb = self.beat_end;
        let now = self.lag_pos;
        let then = (now + 1) % (FLUX_LAG + 1); // the oldest entry: FLUX_LAG analyses ago
        for b in 0..nb {
            self.lagged[now][b] = (1.0 + self.raw[b] / FLUX_REF).ln();
        }
        let mut f = 0.0;
        for b in 0..nb {
            f += (self.lagged[now][b] - self.lagged[then][b]).max(0.0);
        }
        let f = f / nb as f32;
        self.lag_pos = then;
        self.flux[self.flux_pos] = f;
        self.flux_pos = (self.flux_pos + 1) % self.flux.len();
        // The previous value is an onset if it is a local maximum above the adaptive threshold.
        let [prev, prev2] = self.flux_prev;
        self.flux_prev = [f, prev];
        let len = self.flux.len() as f32;
        let mean = self.flux.iter().sum::<f32>() / len;
        let var = self.flux.iter().map(|v| (v - mean) * (v - mean)).sum::<f32>() / len;
        let threshold = (mean + FLUX_K * var.sqrt()).max(FLUX_MIN);
        let gap = match self.period() {
            Some(p) => BEAT_MIN_GAP.max(TEMPO_GAP * p),
            None => BEAT_MIN_GAP,
        };
        if prev > threshold && prev >= prev2 && prev > f && self.since_beat >= gap {
            if (IOI_MIN..=IOI_MAX).contains(&self.since_beat) {
                self.iois[self.ioi_count % IOIS] = self.since_beat;
                self.ioi_count += 1;
            }
            self.since_beat = 0.0;
            self.beats += 1;
        }
    }

    /// The beat period (seconds) once a steady tempo has been heard: the median of the last
    /// intervals between beats.
    fn period(&self) -> Option<f32> {
        let k = self.ioi_count.min(IOIS);
        if k < 4 {
            return None;
        }
        let mut v = self.iois;
        let v = &mut v[..k];
        v.sort_by(|a, b| a.total_cmp(b));
        Some(v[k / 2])
    }

    pub fn levels(&self) -> AudioLevels {
        AudioLevels {
            level: self.agg[0].out,
            bass: self.agg[1].out,
            mid: self.agg[2].out,
            treble: self.agg[3].out,
            level_peak: self.agg[0].peak,
            beat: self.since_beat < AudioLevels::BEAT_HOLD,
            beat_age: self.since_beat,
            bands: self.bands.map(|m| m.out),
            peaks: self.bands.map(|m| m.peak),
            centroid: self.centroid,
            mood: self.mood,
            energy: self.energy,
            tempo: self.period().map_or(0.0, |p| 1.0 / p),
            phase: self.phase,
        }
    }
}

pub use capture::Capture;

#[cfg(windows)]
mod capture {
    use super::{Analyzer, AudioSource, AudioStatus};
    use crate::effects::AudioLevels;
    use anyhow::{anyhow, bail, Result};
    use cpal::traits::{DeviceTrait, HostTrait, StreamTrait};
    use cpal::{FromSample, SampleFormat, SizedSample};
    use std::sync::atomic::{AtomicBool, Ordering};
    use std::sync::mpsc::{sync_channel, Receiver, RecvTimeoutError, SyncSender};
    use std::sync::{Arc, Mutex};
    use std::time::{Duration, Instant};

    /// Without a usable output device, try again this often.
    const RETRY: Duration = Duration::from_secs(3);
    /// Check this often whether the default output device changed.
    const DEVICE_CHECK: Duration = Duration::from_secs(2);
    /// Wait this long for samples before treating the gap as silence.
    const IDLE: Duration = Duration::from_millis(40);
    /// Sample buffers in flight between the audio callback and the analysis (they are reused).
    const BUFFERS: usize = 64;

    #[derive(Default)]
    struct Snapshot {
        levels: AudioLevels,
        at: Option<Instant>,
        device: Option<String>,
    }

    #[derive(Default)]
    struct Shared {
        want: AtomicBool,
        /// Whether the capture thread exists (guarded so start/stop cannot race).
        alive: Mutex<bool>,
        snap: Mutex<Snapshot>,
    }

    impl Shared {
        fn wanted(&self) -> bool {
            self.want.load(Ordering::SeqCst)
        }

        fn set_device(&self, device: Option<String>) {
            if let Ok(mut s) = self.snap.lock() {
                s.device = device;
            }
        }

        fn publish(&self, levels: AudioLevels) {
            if let Ok(mut s) = self.snap.lock() {
                s.levels = levels;
                s.at = Some(Instant::now());
            }
        }

        fn clear(&self) {
            if let Ok(mut s) = self.snap.lock() {
                *s = Snapshot::default();
            }
        }
    }

    /// Loopback capture of the default output device.
    #[derive(Default)]
    pub struct Capture {
        shared: Arc<Shared>,
    }

    impl Capture {
        pub fn new() -> Self {
            Self::default()
        }
    }

    impl AudioSource for Capture {
        fn set_active(&mut self, on: bool) {
            if self.shared.want.swap(on, Ordering::SeqCst) == on || !on {
                return; // unchanged, or stopping (the thread sees `want` and exits)
            }
            let mut alive = self.shared.alive.lock().unwrap_or_else(|e| e.into_inner());
            if !*alive {
                *alive = true;
                let shared = self.shared.clone();
                let spawned = std::thread::Builder::new()
                    .name("lightsyncd-audio".into())
                    .spawn(move || run(shared));
                if let Err(e) = spawned {
                    eprintln!("[lightsyncd] sound: cannot start capture thread: {e}");
                    *alive = false;
                }
            }
        }

        fn levels(&self) -> AudioLevels {
            if !self.shared.wanted() {
                return AudioLevels::SILENT;
            }
            match self.shared.snap.lock() {
                Ok(s) => {
                    let mut l = s.levels;
                    // Time has passed since the analysis ran: keep beats ageing between updates.
                    if let Some(at) = s.at {
                        l.beat_age += at.elapsed().as_secs_f32();
                        l.beat = l.beat_age < AudioLevels::BEAT_HOLD;
                    }
                    l
                }
                Err(_) => AudioLevels::SILENT,
            }
        }

        fn status(&self) -> AudioStatus {
            let active = self.shared.wanted();
            let device = if active {
                self.shared.snap.lock().ok().and_then(|s| s.device.clone())
            } else {
                None
            };
            AudioStatus { active, device }
        }
    }

    /// The capture thread: capture while wanted, reconnect on device changes, retry on errors.
    fn run(shared: Arc<Shared>) {
        let mut last_error = String::new();
        loop {
            if shared.wanted() {
                match capture(&shared) {
                    Ok(()) => last_error.clear(),
                    Err(e) => {
                        shared.clear();
                        let msg = format!("{e:#}");
                        if msg != last_error {
                            eprintln!("[lightsyncd] sound: {msg} (retrying)");
                            last_error = msg;
                        }
                        let until = Instant::now() + RETRY;
                        while shared.wanted() && Instant::now() < until {
                            std::thread::sleep(Duration::from_millis(100));
                        }
                    }
                }
            }
            let mut alive = shared.alive.lock().unwrap_or_else(|e| e.into_inner());
            if !shared.wanted() {
                *alive = false;
                shared.clear();
                return;
            }
        }
    }

    fn build<T>(
        device: &cpal::Device,
        config: cpal::StreamConfig,
        tx: SyncSender<Vec<f32>>,
        free: Receiver<Vec<f32>>,
        failed: Arc<AtomicBool>,
    ) -> Result<cpal::Stream>
    where
        T: SizedSample + Send + 'static,
        f32: FromSample<T>,
    {
        Ok(device.build_input_stream::<T, _, _>(
            config,
            move |data: &[T], _| {
                // Reuse a buffer the analysis handed back; allocate only while the pool fills up.
                let mut v = free.try_recv().unwrap_or_default();
                v.clear();
                v.extend(data.iter().map(|s| s.to_sample::<f32>()));
                // If the analysis falls behind, drop audio rather than queue it up.
                let _ = tx.try_send(v);
            },
            move |e: cpal::Error| {
                // Glitches are not failures: virtual devices (e.g. SteelSeries Sonar) report buffer
                // under/overruns constantly while audio keeps flowing. Only a lost or invalid stream
                // needs a reconnect.
                if fatal(e.kind()) {
                    failed.store(true, Ordering::SeqCst);
                }
            },
            Some(Duration::from_secs(2)),
        )?)
    }

    /// Stream errors after which the stream must be rebuilt (the rest are glitches or notices).
    fn fatal(kind: cpal::ErrorKind) -> bool {
        !matches!(
            kind,
            cpal::ErrorKind::Xrun | cpal::ErrorKind::DeviceChanged | cpal::ErrorKind::RealtimeDenied | cpal::ErrorKind::DeviceBusy
        )
    }

    fn default_id(host: &cpal::Host) -> Option<cpal::DeviceId> {
        host.default_output_device().and_then(|d| d.id().ok())
    }

    /// Capture until capture is no longer wanted (Ok) or the default device changed (Ok, the
    /// caller reconnects); errors when there is no device or the stream fails.
    fn capture(shared: &Shared) -> Result<()> {
        let host = cpal::default_host();
        let device = host
            .default_output_device()
            .ok_or_else(|| anyhow!("no audio output device"))?;
        let id = device.id().ok();
        let name = device.to_string();
        let supported = device.default_output_config()?;
        let channels = supported.channels() as usize;
        let rate = supported.sample_rate();
        let config = supported.config();
        let (tx, rx) = sync_channel::<Vec<f32>>(BUFFERS);
        let (free_tx, free_rx) = sync_channel::<Vec<f32>>(BUFFERS);
        let failed = Arc::new(AtomicBool::new(false));
        let f = failed.clone();
        // Building an input stream on an output device makes WASAPI capture it in loopback mode.
        let stream = match supported.sample_format() {
            SampleFormat::F32 => build::<f32>(&device, config, tx, free_rx, f)?,
            SampleFormat::I16 => build::<i16>(&device, config, tx, free_rx, f)?,
            SampleFormat::I32 => build::<i32>(&device, config, tx, free_rx, f)?,
            SampleFormat::U16 => build::<u16>(&device, config, tx, free_rx, f)?,
            other => bail!("unsupported sample format {other:?} on '{name}'"),
        };
        stream.play()?;
        eprintln!("[lightsyncd] sound: capturing '{name}' ({rate} Hz, {channels} ch)");
        let mut an = Analyzer::new(rate);
        shared.set_device(Some(name.clone()));
        shared.publish(an.levels());
        let mut last_data = Instant::now();
        let mut last_check = Instant::now();
        while shared.wanted() {
            match rx.recv_timeout(IDLE) {
                Ok(v) => {
                    an.push(&v, channels);
                    let _ = free_tx.try_send(v);
                    last_data = Instant::now();
                }
                Err(RecvTimeoutError::Timeout) => {
                    let gap = last_data.elapsed();
                    an.push_silence((gap.as_secs_f32() * rate as f32) as usize);
                    last_data = Instant::now();
                }
                Err(RecvTimeoutError::Disconnected) => bail!("audio stream closed"),
            }
            shared.publish(an.levels());
            if failed.load(Ordering::SeqCst) {
                bail!("audio stream of '{name}' failed (device removed?)");
            }
            if last_check.elapsed() >= DEVICE_CHECK {
                last_check = Instant::now();
                if default_id(&host) != id {
                    eprintln!("[lightsyncd] sound: default output changed, reconnecting");
                    return Ok(());
                }
            }
        }
        Ok(())
    }
}

#[cfg(not(windows))]
mod capture {
    use super::{AudioSource, AudioStatus};
    use crate::effects::AudioLevels;

    /// Loopback capture is only implemented on Windows; elsewhere sound effects stay silent.
    #[derive(Default)]
    pub struct Capture {
        want: bool,
    }

    impl Capture {
        pub fn new() -> Self {
            Self::default()
        }
    }

    impl AudioSource for Capture {
        fn set_active(&mut self, on: bool) {
            self.want = on;
        }
        fn levels(&self) -> AudioLevels {
            AudioLevels::SILENT
        }
        fn status(&self) -> AudioStatus {
            AudioStatus { active: self.want, device: None }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const RATE: u32 = 48000;

    fn tone(freq: f32, amp: f32, secs: f32, channels: usize) -> Vec<f32> {
        let n = (secs * RATE as f32) as usize;
        (0..n)
            .flat_map(|k| {
                let s = amp * (std::f32::consts::TAU * freq * k as f32 / RATE as f32).sin();
                std::iter::repeat_n(s, channels)
            })
            .collect()
    }

    /// A kick drum: a 55 Hz sine with a fast decay, then silence up to `secs`.
    fn kick(amp: f32, secs: f32) -> Vec<f32> {
        let n = (secs * RATE as f32) as usize;
        (0..n)
            .map(|k| {
                let t = k as f32 / RATE as f32;
                amp * (-t / 0.09).exp() * (std::f32::consts::TAU * 55.0 * t).sin()
            })
            .collect()
    }

    fn loudest(l: &AudioLevels) -> usize {
        (0..AudioLevels::BANDS).max_by(|&a, &b| l.bands[a].total_cmp(&l.bands[b])).unwrap()
    }

    #[test]
    fn layout() {
        let a = Analyzer::new(RATE);
        assert_eq!(a.n, 2048);
        assert_eq!(a.hop, 512);
        assert!(a.interval() < 1.0 / 60.0, "updates faster than the frame rate");
        assert_eq!(Analyzer::new(44100).n, 2048);
        assert_eq!(Analyzer::new(96000).n, 4096);
        // Every band reads some bins; bass, mid and treble all have bands.
        assert!(a.band_taps.iter().all(|&(s, e)| e > s));
        assert!(a.bass_end > 3 && a.mid_end > a.bass_end + 3 && a.mid_end < BANDS - 2, "{} {}", a.bass_end, a.mid_end);
        assert!(a.beat_end >= 3 && a.beat_end <= a.bass_end);
        // The tilt lifts treble.
        assert!(a.tilt[BANDS - 1] > 2.0 && a.tilt[0] < 1.0);
    }

    #[test]
    fn silence_is_silent() {
        let mut a = Analyzer::new(RATE);
        a.push(&vec![0.0; RATE as usize], 2);
        let l = a.levels();
        assert_eq!(l.level, 0.0);
        assert!(l.bands.iter().chain(&l.peaks).all(|&v| v == 0.0), "{l:?}");
        assert_eq!(a.beats, 0);
        assert!(!l.beat);
        // Near-silence (-80 dBFS hiss) stays dark too.
        let hiss: Vec<f32> = (0..RATE).map(|k| 1.0e-4 * ((k as f32 * 12.9898).sin() * 43758.547).fract()).collect();
        a.push(&hiss, 1);
        let l = a.levels();
        assert!(l.level < 0.05 && l.bands.iter().all(|&v| v < 0.05), "{l:?}");
    }

    #[test]
    fn tones_light_their_band() {
        for (freq, region, dark_from, dark_to) in [(60.0, 1..=5, 10, BANDS), (1000.0, 12..=14, 0, 8), (8000.0, 20..=22, 0, 16)] {
            let mut a = Analyzer::new(RATE);
            a.push(&tone(freq, 0.3, 0.5, 2), 2);
            let l = a.levels();
            assert!(region.contains(&loudest(&l)), "{freq} Hz -> band {}: {:?}", loudest(&l), l.bands);
            assert!(l.bands[loudest(&l)] > 0.9, "{freq} Hz: {:?}", l.bands);
            assert!(l.bands[dark_from..dark_to].iter().all(|&v| v < 0.1), "{freq} Hz leaks: {:?}", l.bands);
            assert!(l.level > 0.8, "{freq} Hz: {l:?}");
            let (b, m, t) = (l.bass, l.mid, l.treble);
            match freq as u32 {
                60 => assert!(b > 0.8 && m < 0.3 && t < 0.3, "bass tone: {l:?}"),
                1000 => assert!(m > 0.8 && b < 0.3 && t < 0.3, "mid tone: {l:?}"),
                _ => assert!(t > 0.8 && b < 0.3 && m < 0.3, "treble tone: {l:?}"),
            }
        }
    }

    #[test]
    fn sweep_moves_across_the_bands() {
        // A slow log sweep: the loudest band only ever moves up.
        let mut a = Analyzer::new(RATE);
        let mut last = 0;
        let mut phase = 0.0f32;
        let secs = 4.0;
        let total = (secs * RATE as f32) as usize;
        let mut chunk = Vec::with_capacity(480);
        for k in 0..total {
            let f = 40.0 * (12000.0f32 / 40.0).powf(k as f32 / total as f32);
            phase = (phase + std::f32::consts::TAU * f / RATE as f32).rem_euclid(std::f32::consts::TAU);
            chunk.push(0.3 * phase.sin());
            if chunk.len() == 480 {
                a.push(&chunk, 1);
                chunk.clear();
                let l = a.levels();
                let b = loudest(&l);
                if l.bands[b] > 0.5 {
                    assert!(b + 1 >= last, "went back from {last} to {b}");
                    last = last.max(b);
                }
            }
        }
        assert!(last >= BANDS - 4, "reached band {last}");
    }

    #[test]
    fn smoothing_and_peaks() {
        let mut a = Analyzer::new(RATE);
        // Attack: within ~30 ms of a tone starting the band is up.
        a.push(&tone(1000.0, 0.3, 0.06, 1), 1);
        let l = a.levels();
        let b = loudest(&l);
        assert!(l.bands[b] > 0.7, "{l:?}");
        a.push(&tone(1000.0, 0.3, 0.5, 1), 1);
        // Release: the band falls back smoothly, the peak holds then falls at a fixed rate.
        a.push_silence((0.05 * RATE as f32) as usize);
        let l = a.levels();
        assert!(l.bands[b] > 0.1 && l.bands[b] < 0.9, "releases smoothly: {}", l.bands[b]);
        assert!(l.peaks[b] > 0.95, "peak holds: {}", l.peaks[b]);
        a.push_silence((0.5 * RATE as f32) as usize);
        let l = a.levels();
        assert!(l.bands[b] < 0.05, "{}", l.bands[b]);
        assert!(l.peaks[b] > 0.5 && l.peaks[b] < 0.9, "peak falls slowly: {}", l.peaks[b]);
        assert!(l.level_peak >= l.level);
        a.push_silence(RATE as usize);
        assert_eq!(a.levels().peaks[b], 0.0);
    }

    #[test]
    fn auto_gain() {
        // A quiet tone still reaches a high level once the gain adapts...
        let mut a = Analyzer::new(RATE);
        a.push(&tone(1000.0, 0.05, 1.0, 1), 1);
        assert!(a.levels().level > 0.8, "{:?}", a.levels());
        // ...and levels fall back after the sound stops.
        a.push_silence(RATE as usize);
        let l = a.levels();
        assert!(l.level < 0.05 && l.mid < 0.05, "{l:?}");
        // After a loud passage, a quieter one (-12 dB) shows at first and grows as the gain adapts.
        let mut a = Analyzer::new(RATE);
        a.push(&tone(1000.0, 0.4, 1.0, 1), 1);
        a.push(&tone(1000.0, 0.1, 0.3, 1), 1);
        let early = a.levels().mid;
        a.push(&tone(1000.0, 0.1, 6.0, 1), 1);
        let late = a.levels().mid;
        assert!(early > 0.3 && early < 0.8 && late > 0.9, "{early} {late}");
        // Quiet treble next to loud bass still moves (per-band gain).
        let mut a = Analyzer::new(RATE);
        let mix: Vec<f32> = tone(60.0, 0.5, 2.0, 1).iter().zip(tone(9000.0, 0.02, 2.0, 1)).map(|(x, y)| x + y).collect();
        a.push(&mix, 1);
        let l = a.levels();
        assert!(l.bass > 0.8 && l.treble > 0.5, "{l:?}");
    }

    #[test]
    fn beats_are_detected_on_kicks() {
        // A kick every 0.5 s (120 BPM) over a quiet hi-hat bed.
        let mut a = Analyzer::new(RATE);
        let mut at = Vec::new();
        let mut t = 0.0;
        let hat = tone(9000.0, 0.02, 0.5, 1);
        for _ in 0..12 {
            let bar: Vec<f32> = kick(0.8, 0.5).iter().zip(&hat).map(|(k, h)| k + h).collect();
            for part in bar.chunks(480) {
                let before = a.beats;
                a.push(part, 1);
                t += part.len() as f32 / RATE as f32;
                if a.beats > before {
                    at.push(t);
                    assert!(a.levels().beat);
                }
            }
        }
        assert!((11..=13).contains(&at.len()), "beats at {at:?}");
        for w in at.windows(2) {
            assert!((w[1] - w[0] - 0.5).abs() < 0.05, "uneven beats {at:?}");
        }
        // Detected within ~50 ms of each kick.
        assert!(at.iter().all(|t| (t + 0.5).rem_euclid(0.5) < 0.06), "late beats {at:?}");
        let p = a.period().expect("tempo");
        assert!((p - 0.5).abs() < 0.03, "period {p}");
        // Quieter kicks (150 BPM) in noise and a held bass note are still found.
        let mut a = Analyzer::new(RATE);
        let mut seed = 1u32;
        let mut noise = || {
            seed = seed.wrapping_mul(1_664_525).wrapping_add(1_013_904_223);
            (seed >> 8) as f32 / (1u32 << 23) as f32 - 1.0
        };
        let bed = tone(110.0, 0.05, 0.4, 1);
        for _ in 0..15 {
            let bar: Vec<f32> = kick(0.25, 0.4).iter().zip(&bed).map(|(k, b)| k + b + 0.03 * noise()).collect();
            a.push(&bar, 1);
        }
        assert!((13..=16).contains(&a.beats), "beats in noise: {}", a.beats);
        let p = a.period().expect("tempo");
        assert!((p - 0.4).abs() < 0.03, "period {p}");
        // A steady tone is not a beat.
        let mut a = Analyzer::new(RATE);
        a.push(&tone(60.0, 0.5, 0.2, 1), 1);
        let first = a.beats;
        a.push(&tone(60.0, 0.5, 3.0, 1), 1);
        assert_eq!(a.beats, first, "steady bass keeps beating");
        // Hi-hats alone are not beats.
        let mut a = Analyzer::new(RATE);
        for _ in 0..8 {
            let mut hit = tone(9000.0, 0.5, 0.05, 1);
            hit.extend(vec![0.0; RATE as usize / 4]);
            a.push(&hit, 1);
        }
        assert_eq!(a.beats, 0);
    }

    #[test]
    fn follows_the_flow_of_the_music() {
        // Bass-heavy music reads dark, airy music bright; energy rises with loud music and falls in
        // silence; the tempo of steady kicks is found and the phase runs at it.
        let mut dark = Analyzer::new(RATE);
        dark.push(&tone(70.0, 0.4, 6.0, 1), 1);
        let mut bright = Analyzer::new(RATE);
        bright.push(&tone(6000.0, 0.4, 6.0, 1), 1);
        let (d, b) = (dark.levels(), bright.levels());
        assert!(d.mood < 0.3 && b.mood > 0.7, "mood dark {} bright {}", d.mood, b.mood);
        assert!(d.centroid < b.centroid);
        assert!(d.energy > 0.7, "energy {}", d.energy);
        for _ in 0..4 {
            dark.push_silence(RATE as usize * 2); // one call covers at most 2 s
        }
        let quiet = dark.levels();
        assert!(quiet.energy < 0.1, "energy fades: {}", quiet.energy);
        assert!(quiet.mood < 0.3, "silence keeps the character: {}", quiet.mood);
        let mut a = Analyzer::new(RATE);
        for _ in 0..16 {
            a.push(&kick(0.8, 0.5), 1);
        }
        let l = a.levels();
        assert!((l.tempo - 2.0).abs() < 0.15, "tempo {} beats/s", l.tempo);
        let before = l.phase;
        for _ in 0..4 {
            a.push(&kick(0.8, 0.5), 1);
        }
        let ran = a.levels().phase - before;
        assert!(ran > 1.0 && ran < 9.0, "phase ran {ran} over 2 s at 2 beats/s");
    }

    #[test]
    fn no_audio_is_silent() {
        let mut n = NoAudio;
        n.set_active(true);
        assert_eq!(n.levels(), AudioLevels::SILENT);
        assert_eq!(n.status(), AudioStatus::default());
    }
}
