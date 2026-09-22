# Monitor Switcher

Flip a shared monitor between two computers over a keyboard shortcut or a menu click — no physical input button on the display required. Also gives you quick brightness/contrast control and a one-click Night Mode, since you're already talking to the monitor over the same channel.

Built for a two-input monitor (one HDMI, one DisplayPort) shared between a Mac and a Windows PC, but the mechanism generalizes to any DDC/CI-capable monitor.

## How it works

Nearly every modern monitor supports **DDC/CI**, a VESA standard that lets the connected computer send control commands over the same cable as the video signal — including switching the active input (VCP feature `0x60`), brightness/luminance (`0x10`), and contrast (`0x12`). Each side gets its own small app that sends these commands over its own connection:

- **macOS app** shells out to [`m1ddc`](https://github.com/waydabber/m1ddc), which talks to the private `IOAVService` framework Apple Silicon requires for DDC/CI (the old IOKit/I2C approach doesn't work on M1/M2/M3/M4 Macs).
- **Windows app** calls Windows' native Monitor Configuration API (`dxva2.dll` — `SetVCPFeature`) directly via P/Invoke — no extra dependency needed.

DDC/CI generally only works over the *currently active* input, so there's no single controller for both sides — you trigger the handoff from whichever machine currently has the picture.

### A note on "current state" tracking

Reliable read-back over DDC/CI (`get`) is spotty across monitors in general, and on the monitor this was built for it's outright unusable — every `get`/`max` query returns garbage values even when the exit code reports success. So this project never reads state back from the monitor at all. Every control (input, brightness, contrast, Night Mode) only ever *writes* a value, and each app remembers what it last set locally:

- The checkmark next to the active input, and the toggle hotkey's direction, are based on **the last input that app itself successfully switched to**.
- Brightness and contrast start from configurable defaults (see below) and are adjusted in fixed steps from there, tracked the same way.
- Night Mode remembers the brightness you were at before it was turned on, so toggling it off restores exactly where you left off.

This is accurate right after you use either app, and only goes one step stale if you switch from the *other* machine without touching this one first — it self-corrects the next time you use it. If you ever hit the toggle and it goes the "wrong" way, just hit it again or use the explicit menu items instead, which always send a fixed value regardless of tracked state.

If your monitor *does* support reliable DDC read-back, pull requests to read actual state on launch are welcome — this project deliberately avoids it since it can't be trusted on the reference hardware.

## Before you build: find your monitor's real input codes

The VESA defaults (DisplayPort-1 = 15, HDMI-1 = 17) work for many monitors, but not all. On a Mac with `m1ddc` installed, run:

    m1ddc display list
    m1ddc display N set input 15
    m1ddc display N set input 17

(replace N with your display's number from the first command). Watch the monitor to confirm which number is which. Try 16/18 (DisplayPort-2/HDMI-2) or 3/4 (DVI-1/2) if those don't match.

It's also worth testing brightness/contrast/color before relying on them:

    m1ddc display N set luminance 50
    m1ddc display N set contrast 50
    m1ddc display N set red 50

Watch the screen after each command. If you don't see a change, that VCP feature isn't supported on your monitor over DDC — this happened with color-channel (red/green/blue) gain on the reference hardware, which is why this project only implements input, luminance, and contrast, not RGB.

## Configuration (no rebuild needed)

Both apps read a JSON config file on first launch, and create it with defaults if it doesn't exist yet:

- **macOS**: `~/Library/Application Support/MonitorSwitcher/config.json` (menu has a "Reveal Config File in Finder" shortcut)
- **Windows**: `%AppData%\MonitorSwitcher\config.json` (tray menu has an "Open Config Folder" shortcut)

Example:

```json
{
  "displayNumber": 1,
  "displayPortCode": 15,
  "hdmiCode": 17,
  "toggleHotkey": { "modifiers": ["cmd", "option"], "key": "s" },
  "nightModeHotkey": { "modifiers": ["cmd", "option"], "key": "n" },
  "defaultLuminance": 75,
  "nightModeLuminance": 15,
  "luminanceStep": 10,
  "defaultContrast": 75,
  "contrastStep": 10
}
```

(`displayNumber` is macOS-only — it's the `m1ddc` display selector. Windows targets every connected monitor automatically.) `toggleHotkey.modifiers`/`nightModeHotkey.modifiers` accept `cmd`/`command`, `option`/`alt`, `shift`, `control`/`ctrl` on macOS, and `ctrl`/`control`, `alt`, `shift`, `win`/`windows` on Windows — mix as you like. `defaultLuminance`/`defaultContrast` are the starting values each app assumes on first run (0–100); `luminanceStep`/`contrastStep` control how much each Brightness/Contrast Up/Down click changes; `nightModeLuminance` is the brightness Night Mode drops to. Edit the file, then quit and relaunch the app to pick up changes.

**Upgrading from an older config file:** if you seeded `config.json` before Night Mode/brightness/contrast existed, add the five new fields above (`nightModeHotkey`, `defaultLuminance`, `nightModeLuminance`, `luminanceStep`, `defaultContrast`, `contrastStep`) to your existing file. Both apps require every field to be present — a config missing any of them fails to parse and silently gets replaced with all-defaults, which would also reset `displayNumber`/`displayPortCode`/`hdmiCode` back to their defaults.

## macOS

See [`macos/`](./macos). Requires Xcode Command Line Tools and Homebrew.

Install m1ddc, then from the `macos` folder:

    brew install m1ddc
    env -u TOOLCHAINS xcrun swift build -c release
    ./build_app.sh
    mv "Monitor Switcher.app" /Applications/

Launch it from Applications. A "⇄" menu bar icon appears with:

- **Send monitor to Surface (DisplayPort)** / **Bring monitor to this Mac (HDMI)** — explicit switches, with a checkmark showing the last-known active input
- **Toggle: ⌘⌥S** (default) — one global hotkey that flips between the two, based on tracked state (see note above)
- **Brightness Up / Brightness Down** — adjusts luminance in configurable steps
- **Night Mode: ⌘⌥N** (default) — one-click/hotkey preset that drops brightness for low-light use and remembers your previous level to restore on toggle-off, with a checkmark showing whether it's active
- **Contrast Up / Contrast Down** — adjusts contrast in configurable steps
- A confirmation sound plays on every action (a distinct sound for success vs. failure), and the menu bar icon briefly flashes ✓/✗ with the result

To launch at login: System Settings → General → Login Items → add "Monitor Switcher".

## Windows

See [`windows/`](./windows). Requires the .NET 8 SDK (`winget install Microsoft.DotNet.SDK.8`).

From the `windows` folder:

    dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish

Run `publish\MonitorSwitcher.exe` — a tray icon appears with the same explicit switch items (with checkmarks), a global toggle hotkey (**Ctrl+Alt+S** by default), Brightness Up/Down, a Night Mode toggle with its own hotkey (**Ctrl+Alt+N** by default, checkmark shows active state), Contrast Up/Down, a confirmation sound and balloon notification on every action, and an "Open Config Folder" shortcut. To launch at login, drop a shortcut to the exe in `shell:startup`.

## Limitations / contributions welcome

- "Current state" (input, brightness, contrast, Night Mode) is tracked locally per-app, not read live from the monitor — see the note above.
- Color-channel (RGB) gain was evaluated and dropped: the reference monitor accepts the DDC `set red/green/blue` commands without error but doesn't actually change anything, so shipping it would be silently broken. It may work on other monitors — a config-gated version would be a welcome contribution.
- No code signing/notarization on the macOS build — Gatekeeper will show an "unidentified developer" prompt on first launch of a downloaded binary; building from source avoids this entirely.
- No settings UI yet — config is a hand-edited JSON file. A preferences window would be a nice contribution.

## Author

Built by [Dolapo Falana](https://github.com/Dolappo).

## License

MIT — see [LICENSE](./LICENSE).
