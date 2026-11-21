//! Razer Chroma SDK provider. Talks to the REST API that Razer Synapse exposes on localhost.
//! The REST API cannot enumerate hardware, so we address every device category and report
//! which ones Synapse accepted; unconfirmed categories are listed as "(if present)".

use anyhow::{bail, Context, Result};
use async_trait::async_trait;
use lightsync_core::{Color, Device, DeviceKind, LightingProvider};
use serde_json::json;
use std::time::Duration;

pub const CHROMA_ENTRY: &str = "http://localhost:54235/razer/chromasdk";

/// REST endpoint name, display name, kind.
const CATEGORIES: &[(&str, &str, DeviceKind)] = &[
    ("keyboard", "Razer keyboard", DeviceKind::Keyboard),
    ("mouse", "Razer mouse", DeviceKind::Mouse),
    ("mousepad", "Razer mousepad", DeviceKind::Other),
    ("keypad", "Razer keypad", DeviceKind::Keyboard),
    ("headset", "Razer headset", DeviceKind::Other),
    ("chromalink", "Razer Chroma Link (soundbar/accessories)", DeviceKind::Speaker),
];

pub struct RazerProvider {
    http: reqwest::Client,
    session_uri: Option<String>,
}

impl RazerProvider {
    pub fn new() -> Self {
        Self { http: reqwest::Client::builder().timeout(Duration::from_secs(3)).build().unwrap(), session_uri: None }
    }

    async fn register(&mut self) -> Result<String> {
        let body = json!({
            "title": "LightSync",
            "description": "Unified RGB control",
            "author": { "name": "LightSync", "contact": "https://localhost" },
            "device_supported": ["keyboard", "mouse", "headset", "mousepad", "keypad", "chromalink"],
            "category": "application"
        });
        let resp = self
            .http
            .post(CHROMA_ENTRY)
            .json(&body)
            .send()
            .await
            .context("Razer Synapse Chroma service not reachable (is Synapse running with Chroma Connect enabled?)")?;
        let v: serde_json::Value = resp.json().await?;
        let uri = v["uri"].as_str().context("Chroma registration returned no uri")?.to_string();
        self.session_uri = Some(uri.clone());
        Ok(uri)
    }

    async fn put_static(&self, uri: &str, endpoint: &str, color: Color) -> Result<bool> {
        let body = json!({ "effect": "CHROMA_STATIC", "param": { "color": color.to_bgr_u32() } });
        let resp = self.http.put(format!("{uri}/{endpoint}")).json(&body).send().await?;
        let v: serde_json::Value = resp.json().await.unwrap_or_default();
        // result 0 = success; other codes mean the device category is missing/unsupported.
        Ok(v["result"].as_i64() == Some(0))
    }
}

impl Default for RazerProvider {
    fn default() -> Self {
        Self::new()
    }
}

#[async_trait]
impl LightingProvider for RazerProvider {
    fn name(&self) -> &'static str {
        "razer"
    }

    async fn discover(&mut self) -> Result<Vec<Device>> {
        let uri = self.register().await?;
        // Probe with black so a probe never leaves a visible color behind.
        let mut found = vec![];
        for (ep, name, kind) in CATEGORIES {
            if self.put_static(&uri, ep, Color::BLACK).await.unwrap_or(false) {
                found.push(Device {
                    id: format!("razer:{ep}"),
                    name: (*name).into(),
                    kind: *kind,
                    provider: "razer".into(),
                    led_count: 0, // Chroma does not expose LED counts via REST
                });
            }
        }
        if found.is_empty() {
            bail!("Synapse answered, but accepted no device category");
        }
        Ok(found)
    }

    async fn set_all(&mut self, color: Color) -> Result<()> {
        let uri = match &self.session_uri {
            Some(u) => u.clone(),
            None => self.register().await?,
        };
        for (ep, _, _) in CATEGORIES {
            self.put_static(&uri, ep, color).await.ok();
        }
        Ok(())
    }
}
