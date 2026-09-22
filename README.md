# Monitor Switcher

Flip a shared monitor between two computers over a keyboard shortcut or a menu click — no physical input button on the display required.

Built for a two-input monitor (one HDMI, one DisplayPort) shared between a Mac and a Windows PC, but the mechanism generalizes to any DDC/CI-capable monitor.

## How it works

Nearly every modern monitor supports **DDC/CI**, a VESA standard that lets the connected computer send control commands over the same cable as the video signal — including switching the active input (VCP feature `0x60`). Each side gets its own small app that sends that command over its own connection:

- **macOS app** shells out to [`m1ddc`](https://github.com/waydabber/m1ddc), which talks to the private `IOAVService` framework Apple Silicon requires for DDC/CI (the old IOKit/I2C approach doesn't work on M1/M2/M3/M4 Macs).
- **Windows app** calls Windows' native Monitor Configuration API (`dxva2.dll` — `SetVCPFeature`) directly via P/Invoke — no extra dependency needed.

DDC/CI generally only works over the *currently active* input, so there's no single controller for both sides — you trigger the handoff from whichever machine currently has the picture.

## Before you build: find your monitor's real input codes

The VESA defaults (DisplayPort-1 = `15`, HDMI-1 = `17`) work for many monitors, but not all. On a Mac with `m1ddc` installed:

```
m1ddc display list                     # find your display's number
m1ddc display <N> set input 15         # test — does this go to DisplayPort?
m1ddc display <N> set input 17         # test — does this go to HDMI?
```

Try `16`/`18` (Displayport-2/HDMI-2) or `3`/`4` (DVI-1/2) if those don't match. Once confirmed, both projects need the same two numbers — see the CONFIG comments at the top of each entry point.

## macOS

See [`macos/`](./macos). Requires Xcode Command Line Tools and Homebrew.

```
brew install m1ddc
cd macos
# edit the three CONFIG constants at the top of Sources/MonitorSwitcher/main.swift
#   (DISPLAY_NUMBER, DISPLAYPORT_INPUT_CODE, HDMI_INPUT_CODE) to match your setup
env -u TOOLCHAINS xcrun swift build -c release
./build_app.sh
mv MonitorSwitcher.app /Applications/
```

Launch it from Applications. A "⇄" menu bar icon appears with two menu items, plus global hotkeys **⌘⌥D** (send to DisplayPort) and **⌘⌥H** (send to HDMI) — edit `HOTKEY_DISPLAYPORT_KEYCODE` / `HOTKEY_HDMI_KEYCODE` / `HOTKEY_MODIFIERS` in `main.swift` to change them.

To launch at login: System Settings → General → Login Items → add `MonitorSwitcher.app`.

## Windows

See [`windows/`](./windows). Requires the .NET 8 SDK (`winget install Microsoft.DotNet.SDK.8`).

```
cd windows
# edit the two CONFIG constants at the top of Program.cs to match your setup
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

Run `publish\MonitorSwitcher.exe` — a tray icon appears with the same two actions. To launch at login, drop a shortcut to it in `shell:startup`.

## Limitations / contributions welcome

- Input codes are compile-time constants, not a runtime settings UI — PRs adding a preferences window (macOS) or config file (both) welcome.
- No code signing/notarization on the macOS build — Gatekeeper will show an "unidentified developer" prompt on first launch of a downloaded binary; building from source avoids this entirely.
- Global hotkeys are currently fixed at build time on both platforms.

## License

MIT — see [LICENSE](./LICENSE).
