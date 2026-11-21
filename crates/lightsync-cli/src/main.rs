use lightsync_core::{Color, Device, LightingProvider};
use lightsync_openrgb::{OpenRgbProvider, DEFAULT_ADDR};
use lightsync_razer::RazerProvider;

/// Discover with every provider; a failing provider is reported but never stops the others.
async fn discover_all(providers: &mut [Box<dyn LightingProvider>]) -> Vec<(usize, Device)> {
    let mut out = vec![];
    for (i, p) in providers.iter_mut().enumerate() {
        match p.discover().await {
            Ok(devs) => out.extend(devs.into_iter().map(|d| (i, d))),
            Err(e) => eprintln!("[{}] unavailable: {e:#}", p.name()),
        }
    }
    out
}

#[tokio::main]
async fn main() -> anyhow::Result<()> {
    let args: Vec<String> = std::env::args().skip(1).collect();
    if args.first().map(String::as_str) == Some("openrgb") {
        let mut o = OpenRgbProvider::new(DEFAULT_ADDR);
        o.discover().await?;
        println!("{} controller(s) reported by OpenRGB (protocol {}):", o.controllers().len(), o.protocol());
        for c in o.controllers() {
            let modes: Vec<&str> = c.modes.iter().map(|m| m.name.as_str()).collect();
            println!("  #{:<2} type={:<3} {:<40} vendor={:<10} leds={:<4} modes={:?} -> uses {:?}", c.index, c.device_type, c.name, c.vendor, c.led_count, modes, c.direct_mode().map(|m| m.name.as_str()));
            for z in &c.zones {
                println!("      zone {:<22} leds={:<4} (min {}, max {})", z.name, z.leds_count, z.leds_min, z.leds_max);
            }
        }
        return Ok(());
    }
    if args.first().map(String::as_str) == Some("expand") {
        // Size every empty addressable-LED header (ARGB) so fans/strips on it can receive colors.
        let mut o = OpenRgbProvider::new(DEFAULT_ADDR);
        o.discover().await?;
        let cap: u32 = args.get(1).and_then(|v| v.parse().ok()).unwrap_or(256);
        let changed = o.expand_empty_zones(cap).await?;
        if changed.is_empty() {
            println!("No empty zones to expand.");
        }
        for line in changed {
            println!("expanded: {line}");
        }
        return Ok(());
    }
    let mut providers: Vec<Box<dyn LightingProvider>> =
        vec![Box::new(OpenRgbProvider::new(DEFAULT_ADDR).prefer_static(std::env::var_os("LIGHTSYNC_STATIC").is_some()))];
    // The Synapse Chroma provider is opt-in: it accepts commands for devices that may not exist.
    if std::env::var_os("LIGHTSYNC_RAZER_CHROMA").is_some() {
        providers.push(Box::new(RazerProvider::new()));
    }

    match args.first().map(String::as_str) {
        Some("devices") => {
            let devs = discover_all(&mut providers).await;
            if devs.is_empty() {
                println!("No devices found.");
            }
            for (_, d) in devs {
                println!("[{:<8}] {:<44} {:?} ({} LEDs)", d.provider, d.name, d.kind, d.led_count);
            }
        }
        Some("set") => {
            let color: Color = args
                .get(1)
                .ok_or_else(|| anyhow::anyhow!("usage: lightsync set RRGGBB"))?
                .parse()?;
            let devs = discover_all(&mut providers).await;
            let used: std::collections::BTreeSet<usize> = devs.iter().map(|(i, _)| *i).collect();
            for i in used {
                let p = &mut providers[i];
                match p.set_all(color).await {
                    Ok(()) => println!("[{}] set to #{:02x}{:02x}{:02x}", p.name(), color.r, color.g, color.b),
                    Err(e) => eprintln!("[{}] failed: {e:#}", p.name()),
                }
            }
        }
        _ => println!("usage: lightsync <devices | set RRGGBB>"),
    }
    Ok(())
}
