# Monitor Switcher

Flip a shared monitor between two computers over a keyboard shortcut or a menu click — no physical input button on the display required.

Built for a two-input monitor (one HDMI, one DisplayPort) shared between a Mac and a Windows PC, but the mechanism generalizes to any DDC/CI-capable monitor.

## How it works

Nearly every modern monitor supports **DDC/CI**, a VESA standard that lets the connected computer send control commands over the same cable as the video signal — including switching the active input (VCP feature `0x60`). Each side gets its own small app that sends that command over its own connection:

- **macOS app** shells out to [`m1ddc`](https://github.com/waydabber/m1ddc), which talks to the private `IOAVService` framework Apple Silicon requires for DDC/CI (the old IOKit/I2C approach doesn't work on M1/M2/M3/M4 Macs).
- **Windows app** calls Windows' native Monitor Configuration API (`dxva2.dll` — `SetVCPFeature`) directly via P/Invoke — no extra dependency needed.

DDC/CI generally only works over the *currently active* input, so there's no single controller for both sides — you trigger the handoff from whichever machine currently has the picture.

### A note on "current input" tracking

Neither `m1ddc` nor a fully reliable equivalent exists for reading the input source back from the monitor (only writing/setting it is well supported). So the checkmark next to the active input in each app's menu, and the toggle hotkey's direction, are based on **the last input that app itself successfully switched to** — not a live read of the monitor. This is accurate right after you use either app, and only goes one step stale if you switch from the *other* machine without touching this one first — it self-corrects the next time you use it. If you ever hit the toggle and it goes the "wrong" way, just hit it again or use the explicit menu items instead, which always send a fixed input regardless of tracked state.

## Before you build: find your monitor's real input codes

The VESA defaults (DisplayPort-1 = 15, HDMI-1 = 17) work for many monitors, but not all. On a Mac with `m1ddc` installed, run:

    m1ddc display list
    m1ddc display N set input 15
    m1ddc display N set input 17

(replace N with your display's number from the first command). Watch the monitor to confirm which number is which. Try 16/18 (DisplayPort-2/HDMI-2) or 3/4 (DVI-1/2) if those don't match.

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
  "toggleHotkey": { "modifiers": ["cmd", "option"], "key": "s" }
}
```

(`displayNumber` is macOS-only — it's the `m1ddc` display selector. Windows targets every connected monitor automatically.) `toggleHotkey.modifiers` accepts `cmd`/`command`, `option`/`alt`, `shift`, `control`/`ctrl` on macOS, and `ctrl`/`control`, `alt`, `shift`, `win`/`windows` on Windows — mix as you like. Edit the file, then quit and relaunch the app to pick up changes.

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
- A confirmation sound plays on every switch (a distinct sound for success vs. failure), and the menu bar icon briefly flashes ✓/✗ with the target name

To launch at login: System Settings → General → Login Items → add "Monitor Switcher".

## Windows

See [`windows/`](./windows). Requires the .NET 8 SDK (`winget install Microsoft.DotNet.SDK.8`).

From the `windows` folder:

    dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish

Run `publish\MonitorSwitcher.exe` — a tray icon appears with the same explicit switch items (with checkmarks), a global toggle hotkey (**Ctrl+Alt+S** by default — configurable, see above), a confirmation sound and balloon notification on every switch, and an "Open Config Folder" shortcut. To launch at login, drop a shortcut to the exe in `shell:startup`.

## Limitations / contributions welcome

- "Current input" is tracked locally per-app, not read live from the monitor — see the note above.
- No code signing/notarization on the macOS build — Gatekeeper will show an "unidentified developer" prompt on first launch of a downloaded binary; building from source avoids this entirely.
- No settings UI yet — config is a hand-edited JSON file. A preferences window would be a nice contribution.

## Author

Built by [Dolapo Falana](https://github.com/Dolappo).

## License

MIT — see [LICENSE](./LICENSE).
