//! Domain types and the provider abstraction shared by every lighting backend.

use async_trait::async_trait;
use serde::{Deserialize, Serialize};
use std::str::FromStr;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub struct Color {
    pub r: u8,
    pub g: u8,
    pub b: u8,
}

impl Color {
    pub const BLACK: Color = Color { r: 0, g: 0, b: 0 };
    pub const fn new(r: u8, g: u8, b: u8) -> Self {
        Self { r, g, b }
    }
    /// Windows BGR packed value used by the Razer Chroma SDK (0x00BBGGRR).
    pub const fn to_bgr_u32(self) -> u32 {
        (self.b as u32) << 16 | (self.g as u32) << 8 | self.r as u32
    }
}

#[derive(Debug, thiserror::Error)]
#[error("invalid color '{0}', expected RRGGBB or #RRGGBB")]
pub struct ParseColorError(String);

impl FromStr for Color {
    type Err = ParseColorError;
    fn from_str(s: &str) -> Result<Self, Self::Err> {
        let h = s.trim().trim_start_matches('#');
        if h.len() != 6 || !h.is_ascii() {
            return Err(ParseColorError(s.into()));
        }
        let p = |i: usize| u8::from_str_radix(&h[i..i + 2], 16).map_err(|_| ParseColorError(s.into()));
        Ok(Color::new(p(0)?, p(2)?, p(4)?))
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum DeviceKind {
    Motherboard,
    Ram,
    Fan,
    Keyboard,
    Mouse,
    Speaker,
    Other,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Device {
    /// Provider-unique id.
    pub id: String,
    pub name: String,
    pub kind: DeviceKind,
    /// Name of the provider that owns this device.
    pub provider: String,
    pub led_count: usize,
}

/// A lighting backend (OpenRGB, Razer Chroma, ...). Each physical device is owned by exactly one provider.
#[async_trait]
pub trait LightingProvider: Send + Sync {
    fn name(&self) -> &'static str;
    /// Connect (if needed) and list the devices this provider can control.
    async fn discover(&mut self) -> anyhow::Result<Vec<Device>>;
    /// Set every LED of every device owned by this provider to one color.
    async fn set_all(&mut self, color: Color) -> anyhow::Result<()>;
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_hex() {
        assert_eq!("#ff8000".parse::<Color>().unwrap(), Color::new(255, 128, 0));
        assert_eq!("00FF00".parse::<Color>().unwrap(), Color::new(0, 255, 0));
        assert!("xyz".parse::<Color>().is_err());
        assert!("ggggggg".parse::<Color>().is_err());
    }

    #[test]
    fn bgr_packing() {
        assert_eq!(Color::new(0x11, 0x22, 0x33).to_bgr_u32(), 0x00332211);
    }
}
