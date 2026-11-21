# Third-party software

LightSync is MIT licensed (see [LICENSE](LICENSE)). It uses the following third-party software,
under their own licenses. Rust crates are fetched by Cargo and .NET packages by NuGet at build time;
only three.js is included in this repository.

## Included in this repository

| Component | Where | License |
|---|---|---|
| [three.js](https://threejs.org) r160 (`three.module.js`, `OrbitControls.js`, `TransformControls.js`) | `ui/LightSync.App/Preview3D/vendor/three` | MIT (see the `LICENSE` file there) |

## Used at build time (not included)

| Component | Used for | License |
|---|---|---|
| [tokio](https://tokio.rs) | Service runtime, named pipe | MIT |
| [serde](https://serde.rs), serde_json | Settings and the service protocol | MIT or Apache-2.0 |
| [anyhow](https://github.com/dtolnay/anyhow), [thiserror](https://github.com/dtolnay/thiserror), [async-trait](https://github.com/dtolnay/async-trait) | Error handling, traits | MIT or Apache-2.0 |
| [cpal](https://github.com/RustAudio/cpal) | Capturing the Windows sound output (loopback) | Apache-2.0 |
| [RustFFT](https://github.com/ejmahler/RustFFT) | Sound spectrum analysis | MIT or Apache-2.0 |
| [windows-sys / windows](https://github.com/microsoft/windows-rs) | Windows APIs (pipe security) | MIT or Apache-2.0 |
| [Windows App SDK / WinUI 3](https://github.com/microsoft/WindowsAppSDK) | The app's UI | MIT |
| [H.NotifyIcon](https://github.com/HavenDV/H.NotifyIcon) | Tray icon | MIT |
| Microsoft Edge WebView2 | Hosting the 3D preview | Microsoft Software License (runtime installed separately) |

The full list of Rust crates and versions is in `Cargo.lock`; run `cargo tree` to see it.

## Programs LightSync works with (not included)

| Program | How LightSync uses it | License |
|---|---|---|
| [OpenRGB](https://openrgb.org) | Started as a separate program; LightSync talks to it over its network SDK on 127.0.0.1. LightSync can download official OpenRGB releases for you. | GPL-2.0 |
| PawnIO | Driver OpenRGB can use to reach motherboard / RAM lighting | See its project |

No OpenRGB code is copied into LightSync.
