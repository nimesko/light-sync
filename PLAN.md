# LightSync — unified RGB control for a Windows PC

## Context
The user has an RGB-heavy PC (motherboard, Razer keyboard/mouse, soundbar, Corsair RAM, fans) and wants one desktop app to control all of it instead of juggling Razer Synapse, iCUE, board vendor software, etc. The project dir `/home/nimesko/projects/light-sync` is empty (no repo). Dev shell is WSL2, but the hardware is only reachable from the Windows host, so the app is **Windows-native only** (no cross-platform for now).

Decisions already made with the user:
- **Engine:** OpenRGB SDK server does all device I/O (1000+ devices, no per-vendor SDKs).
- **Core:** Rust. **UI:** dedicated native Windows UI → **WinUI 3 (C#, Windows App SDK)**, kept as a separate process from the Rust core.
- **v1 scope:** global color/effects, per-device/zone control + profiles, audio-reactive + screen sync, auto-profiles/automation.

## Architecture

```
 WinUI 3 app (C#)  <--JSON-RPC over named pipe-->  lightsyncd (Rust, tokio)  <--TCP :6742-->  OpenRGB.exe --server
  tray, device list,                               ├─ device registry / zone model              (PawnIO driver for SMBus,
  effect editor, profiles                          ├─ effect engine (60 fps, layered)            USB HID for Razer, etc.)
                                                   ├─ audio analyzer (WASAPI loopback + FFT)
                                                   ├─ screen sampler (DXGI Desktop Duplication)
                                                   ├─ automation (foreground app, power/lock, schedule)
                                                   └─ profile store (%APPDATA%\LightSync\*.json)
```

Why a separate daemon: effects/audio/screen loops must keep running when the UI window is closed (tray-only), and it keeps Rust out of the WinUI layer (Rust↔WinUI 3 bindings are not maintained; process boundary + named pipe is the standard approach and needs no extra deps).

### Rust workspace (`crates/`)
- `lightsync-core` — domain types: `Device`, `Zone`, `Led`, `Color`, `Canvas` (maps zones → 2D/linear space), `Profile`, `Effect` trait.
- `lightsync-openrgb` — thin SDK client wrapper. Start from crate `openrgb2` (async, tokio; supports protocol 4/5). **Risk:** OpenRGB 1.0 ships SDK protocol 6 — if `openrgb2` can't negotiate down cleanly, implement the small binary protocol ourselves (header `ORGB` + dev idx + pkt type + size; calls: RequestControllerCount/Data, UpdateLeds, UpdateZoneLeds, SetCustomMode, ResizeZone, SetClientName).
- `lightsync-effects` — static, breathing, rainbow, wave, gradient, per-zone; blend/layering; brightness master. Frames are batched into one `UpdateLeds` per device per tick; throttle per-device (USB HID/SMBus devices tolerate far less than 60 fps — per-device max-fps in config).
- `lightsync-audio` — WASAPI loopback (`cpal` or `wasapi` crate) → `rustfft` → band energies/beat → effect inputs.
- `lightsync-screen` — DXGI Desktop Duplication (`windows` crate) → downsample to edge/zone colors; configurable monitor + smoothing.
- `lightsync-automation` — rule engine: triggers (foreground exe via `SetWinEventHook`, session lock/unlock + sleep/resume via power broadcast, time-of-day) → actions (apply profile, brightness, off).
- `lightsync-ipc` — JSON-RPC 2.0 over `\\.\pipe\lightsync` (tokio named pipes) with event subscriptions (device list changed, profile changed, live frame preview).
- `lightsyncd` — binary: wires everything, supervises OpenRGB, tray-less headless service started at logon.
- `lightsync-cli` — `lightsync set --color ff0000`, `profile apply gaming`, `devices` (also our primary test harness before the UI exists).

### WinUI 3 app (`ui/LightSync.App/`)
C# + MVVM (CommunityToolkit.Mvvm), tray icon via H.NotifyIcon.WinUI. Pages: Dashboard (global color/effect/brightness), Devices (tree device→zone→LED with live preview), Effects editor, Audio/Screen sync, Profiles, Automation rules, Settings (OpenRGB path, autostart, per-device fps caps). Thin generated/handwritten `LightSync.Client` lib for the pipe JSON-RPC.

### OpenRGB supervision (important Windows specifics)
- Daemon locates/launches `OpenRGB.exe --server --noautoconnect` (hidden), restarts on crash, retries connect with backoff.
- **PawnIO** driver must be installed (replaced WinRing0) and **OpenRGB must run as admin** for SMBus devices (Corsair RAM, some motherboards). Plan: OpenRGB launched via an elevated scheduled task at logon (one UAC prompt at install); `lightsyncd` itself stays non-elevated.
- **Conflicts:** SMBus allows one controller at a time; Synapse/iCUE/Armoury Crate etc. must be closed or their lighting modules disabled. Settings page includes a "conflicting software detected" check.
- DDR5 RAM SMBus writes carry some risk on certain boards — keep RAM writes off by default until verified in Milestone 0.

## REVISION: zero-setup experience (user requirement)
User wants **one executable, no manual steps**: run it, it finds the devices, they start working. This changes how we ship OpenRGB/PawnIO — the user never installs them by hand.

- **Deliverable:** a single `LightSync-Setup.exe` (self-contained: WinUI app + Rust daemon + bundled OpenRGB 1.0 portable + PawnIO installer + .NET/Windows App SDK runtime self-contained, no prerequisites). Optionally a portable `LightSync.exe` that self-extracts the same payload to `%LOCALAPPDATA%\LightSync`.
- **First run, automated, one UAC prompt only:**
  1. Silently install PawnIO (`PawnIO_setup.exe -install -silent`; exit code 3010 = reboot needed → prompt user). Skip if already present.
  2. Register an elevated scheduled task (or service) for the bundled `OpenRGB.exe --server --noautoconnect`, so every later launch/logon needs no UAC.
  3. Detect conflicting software (Synapse, iCUE, Armoury Crate…) and offer a one-click "stop/disable their lighting" instead of a manual instruction.
  4. Start `lightsyncd`, scan devices, and show the result as a live checklist ("Motherboard ✔, Razer keyboard ✔, Corsair RAM ✔, soundbar ✖ unsupported"), then land on the dashboard with everything already controllable.
- **Auto-discovery:** daemon rescans on startup and on USB/device-change events (`WM_DEVICECHANGE`); a Devices page "Rescan" button as fallback. Unsupported devices are listed with the reason rather than failing silently.
- **Build side:** Rust workspace + `dotnet publish` self-contained + installer built with Inno Setup or WiX in CI-style script (`build.ps1`) — the developer (us) runs one command to produce the exe; the end user only double-clicks.
- **Licensing checks before shipping:** OpenRGB is GPL-2.0 (separate process over TCP, redistribute with source link/notice). PawnIO redistribution terms are **unconfirmed** (listed as freeware, no official text found) — verify at pawnio.eu / its repo; fallback is to download it from the official release at first run instead of embedding it.
- **Still unavoidable:** one UAC prompt (driver + elevated OpenRGB), possible reboot if the driver demands it, and closing vendor RGB software that fights for the same hardware.

## REVISION 2: device-provider abstraction (user's Razer devices)
User's Razer gear: **Leviathan V2 X** (soundbar), **Cobra Pro** (wireless mouse), **DeathStalker V2** (keyboard). I could not confirm OpenRGB support for any of the three (searches only found SignalRGB support; Cobra Pro is wireless/dongle, a known weak spot for third-party HID drivers). So the backend is no longer OpenRGB-only:

- `lightsync-core` defines a `LightingProvider` trait (`discover()`, `devices()`, `apply_frame()`). Providers are merged into one device registry; the effect engine/UI don't care where a device came from.
- **Provider 1 — OpenRGB** (bundled): motherboard, Corsair RAM, fans/controllers, and any Razer device it happens to support.
- **Provider 2 — Razer Chroma SDK** (REST at `http://localhost:54235/razer/chroma`, needs Synapse installed, which you already have for this hardware): covers all three Razer devices officially, including the wireless mouse and soundbar. Crate `lightsync-razer`. Per-device effects via Chroma "custom" effects per device type (keyboard/mouse/headset-style arrays; soundbar maps to the speaker/chromalink zones — exact zone layout to confirm on hardware).
- **Conflict rule:** each physical device is owned by exactly one provider. Default: Razer devices → Chroma provider (no HID fight with Synapse, wireless works); OpenRGB's Razer controllers are disabled/ignored unless the user overrides in Settings. This also removes "close Synapse" from the first-run flow — Synapse stays running; only iCUE/Armoury Crate-type software still conflicts with OpenRGB's SMBus access.
- Chroma needs a registered app (title/author/category JSON POST to `/razer/chroma`) and a heartbeat every ~10 s; the provider handles registration, heartbeat, reconnection when Synapse restarts. First-run check: Synapse installed? If not, offer to download/open its installer rather than failing silently.
- Dev note: Chroma Developer terms (razer.com API terms of use) must be reviewed before distributing publicly; personal use is fine.

Verification addition: with Synapse running, `lightsync devices` must list Leviathan V2 X, Cobra Pro and DeathStalker V2 via the Chroma provider; `lightsync set --color` changes all three; sleep/resume and Synapse restart recover automatically.

## Milestones
0. **Hardware spike (no code):** on Windows install OpenRGB 1.0 + PawnIO, run as admin, record which of motherboard / Razer KB+mouse / Corsair RAM / fans / soundbar are actually detected and controllable. Soundbar is the likeliest gap (model unknown) — if unsupported, fall back to a vendor SDK/driver plugin or drop it from v1.
1. **Core skeleton:** workspace, `lightsync-openrgb`, device enumeration, set-all-static-color via CLI. Prove end-to-end on real hardware.
2. **Effect engine + profiles + IPC:** layered effects, per-zone control, profile save/load, JSON-RPC server, daemon supervision of OpenRGB.
3. **WinUI 3 UI + tray:** dashboard, device/zone editor with live preview, profiles page.
4. **Audio-reactive + screen sync.**
5. **Automation:** foreground-app / lock-sleep / schedule rules.
6. **Packaging (moved earlier in practice — build a minimal installer right after M1 so zero-setup is tested continuously):** single-exe installer, autostart, bundled OpenRGB + PawnIO first-run bootstrap, crash logging (`tracing` → `%APPDATA%\LightSync\logs`).

## Dev environment notes
- Rust and .NET are **not** installed in WSL (only node/python). Build on the Windows side: Rust `x86_64-pc-windows-msvc` + VS Build Tools, .NET 8+ SDK, Windows App SDK templates. Repo can live in WSL for editing, but builds/runs should happen via Windows toolchain (or move the repo to a Windows path) — decide at start of implementation.
- Create git repo + `CLAUDE.md` with build/run commands at Milestone 1.

## Verification
- M0: OpenRGB device list screenshot/log matches physical hardware; manually change color on each device.
- M1: `lightsync devices` lists all detected devices; `lightsync set --color 00ff00` turns every controllable device green.
- Core unit tests: color math, effect frame output (golden frames), profile (de)serialization, automation rule matching, IPC request/response with a mock OpenRGB server (fake TCP server implementing the protocol subset).
- M3+: launch UI, change global color → devices update <100 ms; close window → daemon/tray keeps effects running; kill OpenRGB → daemon recovers.
- M4: play music → visible response; screen sync lag/CPU measured (target <5% CPU at 30 fps).
- M5: launch a game exe → profile switches; lock/sleep → lights off, resume → restored.

## Open items for implementation time
- Exact device models (esp. soundbar) — settled by Milestone 0.
- Whether `openrgb2` handles protocol 6 or we write our own client.
- Installer format (MSIX vs WiX) and whether to bundle OpenRGB or require install.

## Sources consulted
- OpenRGB SDK/PawnIO notes: https://openrgb-wiki.readthedocs.io/ , https://openrgb.org/releases.html
- Rust client: https://docs.rs/crate/openrgb2/latest
- WinUI IPC: https://learn.microsoft.com/windows/apps/develop/communication/interprocess-communication
