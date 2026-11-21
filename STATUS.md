# LightSync — status (2026-11-17)

Goal: one Windows app that auto-discovers and controls all RGB on this PC. Full design in `PLAN.md`.

## Done
- Rust workspace in `crates/`: `lightsync-core` (Color, Device, `LightingProvider` trait), `lightsync-openrgb` (own SDK client, protocol <= 5), `lightsync-razer` (Synapse Chroma REST, opt-in), `lightsync-cli`.
- CLI: `lightsync devices | set RRGGBB | openrgb | expand [cap]`. Run with `cargo run -q -p lightsync-cli -- <cmd>` from PowerShell (Windows toolchain; WSL cannot build/run it).
- Toolchain installed on Windows: Rust 1.100, .NET SDK 8, VS Build Tools (C++). Smart App Control was turned OFF by the user (it blocked unsigned builds, error 4551).
- OpenRGB 1.1 unpacked to `vendor/openrgb/` and PawnIO 2.2 installer to `vendor/pawnio/` (both git-ignored; PawnIO is already installed on the PC, signature verified).
- OpenRGB is started by hand: `OpenRGB.exe --server --noautoconnect`, elevated (UAC). The CLI talks to it on 127.0.0.1:6742.

## Hardware findings (this PC)
Seen by OpenRGB: Gigabyte X870 GAMING X WIFI7 (zones ARGB_V2_1/2/3, Chipset Accent, LED_C), Razer Cobra Pro (wireless), Razer Leviathan V2 X, Razer DeathStalker V2, Corsair iCUE Link System Hub (3x QX fans, 34 LEDs each).
- Razer: Synapse's Chroma REST (`/razer/chromasdk`, color param `{"color": BGR}`) accepted commands but nothing lit. Via OpenRGB it works for keyboard + soundbar **only while the Synapse window (RazerAppEngine) is closed**. Mouse kept cycling a rainbow (testing hardware "Static" mode via `LIGHTSYNC_STATIC=1`; result not yet confirmed).
- Gigabyte Control Center (GCC) and the Corsair Device Control Service are running (elevated, could not be stopped) and likely hold the chipset SMBus ("Failed to select PIIX4 port 0/1"), so **RAM is not detected**. User must quit GCC (and iCUE) from the tray, then restart OpenRGB.
- GPU: MSI NVIDIA card (`10DE:2C05`, subsystem `1462:5310`) registers an I2C bus but no controller — probably unsupported; its 2 fans may only be controllable from MSI software.
- Case layout: 3 top fans, 2 bottom, 3 side, 1 rear, 1 CPU fan (mirror), 2 RAM sticks. Only the 3 Corsair QX fans were confirmed lit so far; the other 7 are believed to be on the board's ARGB headers.
- ARGB headers were 0 LEDs; `lightsync expand 256` resized each to 256 and `set ff0000` was sent. **Result not yet reported by the user.**

## Next steps
1. Ask user which fans lit after `expand`; then test headers one at a time to map header -> fans.
2. After GCC/iCUE are closed: restart OpenRGB elevated, rescan, check RAM.
3. Confirm mouse Static-mode result; decide fallback for Cobra Pro (wireless).
4. Daemon (`lightsyncd`) with effect engine, profiles, IPC; then WinUI 3 UI; audio/screen sync; automation (see PLAN.md milestones).
5. Zero-setup installer: bundle OpenRGB + PawnIO, one UAC prompt, elevated OpenRGB at logon, conflict detection (Synapse UI, GCC, iCUE).
6. Known gaps: Chroma provider needs heartbeat + real device detection; `set_all` re-reads nothing after resize; no persistence of zone sizes.
