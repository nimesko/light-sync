# LightSync

LightSync is a Windows app that controls all the RGB lighting in a PC from one place: motherboard
headers and fans, RAM, keyboard, mouse and speakers. It talks to the hardware through
[OpenRGB](https://openrgb.org).

**Why:** this is a personal project. I wanted one simple app for the lights on my own PC, instead
of a separate vendor app for each brand. It's shared as open source in case it's useful to someone
else; it has only been tested on that one PC.

![Home: devices, 3D preview of the setup and triggers](docs/screenshots/home.png)

## Features

| Feature | What it does |
|---|---|
| All devices in one list | Devices, zones and fans together; hide what you don't use; split fan headers into single fans; groups |
| 3D preview | A live 3D model of the case and desk in the current colors; drag parts to where they really are |
| 36 effects | Calm, motion, colorful, alerts and sound-reactive effects, each with color, brightness and speed |
| Music sync | Follows what Windows is playing: spectrum, bass, treble, beat, tempo and the mood of the song; kicks, claps and hi-hats hit even in loud electronic music; a live spectrum shows what it hears |
| Sound colors | Sound effects in their own colors, or rainbow, random on every beat, cold to hot, fire or neon |
| Triggers | A key press, a mouse click, the beat or an app starting can light whole devices, some zones or single keys and LEDs, with priorities |
| Presets | Save and load your lighting setups |
| Power | Lights off on sleep, lock or when idle; start with Windows; lives in the tray |
| OpenRGB management | Starts, stops and updates OpenRGB for you, listening on this PC only |

| Device | Triggers |
|---|---|
| ![A device's effects](docs/screenshots/device.png) | ![The trigger editor](docs/screenshots/trigger.png) |

## Requirements

To **run**:
- Windows 10 (version 1809 or later) or Windows 11, 64-bit
- [OpenRGB](https://openrgb.org) 1.0 or later (LightSync can install it). OpenRGB needs administrator
  rights for motherboard and RAM lighting, so Windows asks once.
- [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) for the
  3D preview (already part of Windows 11)

To **build**:
- [Rust](https://rustup.rs) (stable, MSVC)
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Visual Studio Build Tools with "Desktop development with C++"

```powershell
cargo build -p lightsyncd                    # the lighting service
dotnet run --project ui\LightSync.App        # the app (starts the service)
cargo test --workspace                       # tests
```

## How it's built

A WinUI 3 app (`ui/LightSync.App`, C#) talks over a local named pipe to a small Rust service
(`crates/lightsyncd`), which renders the lighting at 30-60 frames a second and sends it to OpenRGB
(`crates/lightsync-openrgb`) on 127.0.0.1.

## Privacy

Everything stays on the PC. Sound and key presses are only processed in memory, never stored or
sent. The only online request is the check for new OpenRGB versions.

## License

[MIT](LICENSE) © 2026 Rogerio Tristao Junior. See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)
for the software it uses. OpenRGB is a separate program and isn't included.
