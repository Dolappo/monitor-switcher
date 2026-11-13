# Monitor Switcher

Flip a shared monitor between two computers over a keyboard shortcut or a menu click — no physical input button on the display required. Also gives you quick brightness/contrast control and a one-click Night Mode, since you're already talking to the monitor over the same channel.

Works with any two computers sharing one DDC/CI-capable monitor. You name the two machines and say which monitor input each one uses; the menus, icon and notifications use those names. The default setup is a Mac on HDMI and a Surface on DisplayPort.

## How it works

Nearly every modern monitor supports **DDC/CI**, a VESA standard that lets the connected computer send control commands over the same cable as the video signal — including switching the active input (VCP feature `0x60`), brightness/luminance (`0x10`), and contrast (`0x12`). Each side gets its own small app that sends these commands over its own connection:

- **macOS app** shells out to [`m1ddc`](https://github.com/waydabber/m1ddc), which talks to the private `IOAVService` framework Apple Silicon requires for DDC/CI (the old IOKit/I2C approach doesn't work on M1/M2/M3/M4 Macs).
- **Windows app** calls Windows' native Monitor Configuration API (`dxva2.dll` — `SetVCPFeature`) directly via P/Invoke — no extra dependency needed.

DDC/CI generally only works over the *currently active* input, so there's no single controller for both sides — you trigger the handoff from whichever machine currently has the picture.

### A note on "current state" tracking

Reliable read-back over DDC/CI (`get`) is spotty across monitors in general, and on the monitor this was built for it's outright unusable — every `get`/`max` query returns garbage values even when the exit code reports success. So this project never reads state back from the monitor at all. Every control (input, brightness, contrast, Night Mode) only ever *writes* a value, and each app remembers what it last set locally:

- The checkmark next to the active input, and the toggle hotkey's direction, are based on **the last input that app itself successfully switched to**.
- Brightness and contrast start from configurable defaults (see below), tracked the same way — the macOS brightness slider starts at the last value it set, and contrast is adjusted in fixed steps from there.
- Night Mode remembers the brightness you were at before it was turned on, so toggling it off restores exactly where you left off.

This is accurate right after you use either app. Each app also watches its own display list: when the shared monitor drops off this computer, the app marks the other machine as having it, and when it reappears it marks this one. So the state usually stays right even after a switch made from the other computer. Some monitors keep the connection alive while showing another input, so no change is detected; then the state only updates the next time you switch from this app. If you ever hit the toggle and it goes the "wrong" way, just hit it again or use the explicit menu items instead, which always send a fixed value regardless of tracked state.

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

Example (macOS):

```json
{
  "displayNumber": 1,
  "machines": [
    { "name": "Mac",     "inputCode": 17, "mouseChannel": 2 },
    { "name": "Surface", "inputCode": 15, "mouseChannel": 1 }
  ],
  "thisMachine": 0,
  "toggleHotkey": { "modifiers": ["cmd", "option"], "key": "s" },
  "nightModeHotkey": { "modifiers": ["cmd", "option"], "key": "n" },
  "defaultLuminance": 75,
  "nightModeLuminance": 15,
  "defaultContrast": 75,
  "contrastStep": 10,
  "mouseSwitchEnabled": false,
  "mouseSwitchPath": "/Applications/MXHostSwitch.app/Contents/MacOS/MXHostSwitch"
}
```

The Windows config uses the same keys. Use the same `machines` list on both computers, and set `thisMachine` to `0` on the Mac and `1` on the Surface (the index of the machine the app is running on). The Windows defaults use `ctrl`+`alt` hotkeys and a `.exe` path for `mouseSwitchPath`.

- **`machines`**: exactly two entries, one per computer.
  - `name` is what the menus, menu bar icon and notifications show ("Show monitor on Surface", "✓ Mac").
  - `inputCode` is the monitor input that computer is plugged into, as a DDC/CI VCP `0x60` value. Common values: 15 = DisplayPort-1, 16 = DisplayPort-2, 17 = HDMI-1, 18 = HDMI-2, 27 = USB-C on many monitors. See [find your monitor's real input codes](#before-you-build-find-your-monitors-real-input-codes).
  - `mouseChannel` is that computer's Easy-Switch channel on a multi-host mouse. It's only used when `mouseSwitchEnabled` is `true`.
- **`thisMachine`**: which entry in `machines` is this computer.
- `displayNumber` is macOS-only. It's the `m1ddc` display selector from `m1ddc display list`. Windows sends commands to every connected monitor automatically.
- `toggleHotkey.modifiers` / `nightModeHotkey.modifiers` accept `cmd`/`command`, `option`/`alt`, `shift`, `control`/`ctrl` on macOS, and `ctrl`/`control`, `alt`, `shift`, `win`/`windows` on Windows.
- `defaultLuminance` / `defaultContrast` are the starting values each app assumes on first run (0–100). `contrastStep` is how much each Contrast Up/Down click changes. `nightModeLuminance` is the brightness Night Mode drops to.
- `mouseSwitchEnabled` / `mouseSwitchPath` are optional and off by default. See [Mouse switching](#mouse-switching-optional).

Edit the file, then quit and relaunch the app to pick up changes.

**Upgrading from an older config file:** configs from before `machines` existed (with `displayPortCode`, `hdmiCode`, `mouseDisplayPortChannel`, `mouseHdmiChannel`) still load. They're converted to two machines, "Mac" (the HDMI input and Mac mouse channel) and "Surface" (the DisplayPort input and Surface mouse channel), with `thisMachine` set to the Mac on macOS and the Surface on Windows. The Mac app leaves your file as it is until it next saves (for example after mouse calibration); the Windows app writes the new format the next time it saves. Any other missing field falls back to its default, so nothing you've customized gets reset.

## macOS

See [`macos/`](./macos). Requires Xcode Command Line Tools and Homebrew.

Install m1ddc, then from the `macos` folder:

    brew install m1ddc
    env -u TOOLCHAINS xcrun swift build -c release
    ./build_app.sh
    mv "Monitor Switcher.app" /Applications/

Launch it from Applications. A "⇄" menu bar icon appears with:

- **Bring monitor here (Mac)** / **Show monitor on Surface**: explicit switches named after your two machines, with a checkmark on the machine that currently has the monitor. The menu bar icon shows the same thing at rest (for example "⇄ Mac")
- **Toggle: ⌘⌥S** (default) — one global hotkey that flips between the two, based on tracked state (see note above)
- **Brightness slider** — drag to any value; the label above it updates live as you drag, and the DDC command is sent shortly after you stop (not on every pixel of movement)
- **Night Mode: ⌘⌥N** (default) — one-click/hotkey preset that drops brightness for low-light use and remembers your previous level to restore on toggle-off, with a checkmark showing whether it's active
- **Contrast Up / Contrast Down** — adjusts contrast in configurable steps
- A confirmation sound plays on every action (a distinct sound for success vs. failure), and the menu bar icon immediately shows ✓ plus the machine name (or ✗ if the monitor rejected the command)

To launch at login: System Settings → General → Login Items → add "Monitor Switcher".

## Mouse switching (optional)

If you also share a Logitech multi-host mouse between the two computers, the toggle and explicit switch items can flip it over to the other machine at the same time as the monitor, using [`mx-host-switch`](https://github.com/antonio-castellon/mx-host-switch) — a small open-source tool that talks to Logitech's reverse-engineered HID++ "CHANGE_HOST" feature. This is separate from and unrelated to the monitor-switching mechanism itself (`m1ddc` on macOS, direct DDC/CI calls on Windows); it controls the mouse's Bluetooth/receiver pairing, not the monitor. Both the macOS app and the Windows app support this now.

This is genuinely hit-or-miss depending on your exact mouse and how it's connected — there's no open standard here like there is for DDC/CI, and Logitech doesn't document or officially support software-triggered switching. Before configuring anything, confirm it actually works for your hardware:

1. Download `mx-host-switch` for macOS from its [releases page](https://github.com/antonio-castellon/mx-host-switch/releases) and get it past Gatekeeper (`xattr -cr` the downloaded `.app`, since it isn't notarized — same idea as our own build).
2. With your mouse connected, run its device-detection command from Terminal — for the packaged app, the executable inside the bundle accepts CLI flags directly:

       "/Applications/MXHostSwitch.app/Contents/MacOS/MXHostSwitch" --switch <channel>

   Try switching to the channel your mouse is already on (harmless no-op) first, to confirm the binary runs and returns immediately with a clean success message rather than opening a GUI or hanging.
3. If that works, find out which Easy-Switch channel number is bonded to which computer (short-press the mouse's Easy-Switch/Connect button to cycle channels; the packaged app or its GUI can also show current channel).

On Windows, the setup is the same idea: download `mx-host-switch` for Windows from the same [releases page](https://github.com/antonio-castellon/mx-host-switch/releases), unzip it somewhere permanent (e.g. `C:\Tools\MXHostSwitch\`), and run the same `--switch <channel>` / `--list` commands from PowerShell or cmd to confirm it works before touching `config.json`.

Confirmed working on a Pebble Mouse 2 (M350s) over Bluetooth. A Logitech keyboard's Easy-Switch is a different story — on the Pebble Keys 2 (K380s/K390s), it's pure firmware behavior with no software hook at all (it doesn't show up in `mx-host-switch`'s device scan even while actively connected), so there's no known way to automate it. Use the keyboard's own Fn+1/Fn+2/Fn+3 combo for that side.

Set these fields in `config.json` to match your setup:

- `mouseSwitchEnabled`: off (`false`) by default in new configs. Set it to `true` to turn this feature on. If Logitech Flow or the mouse's own button already moves the mouse for you, leave it off.
- `mouseSwitchPath` — full path to the `mx-host-switch` executable: on macOS, the binary inside its `.app` bundle (e.g. `/Applications/MXHostSwitch.app/Contents/MacOS/MXHostSwitch`); on Windows, the `.exe` wherever you unzipped it (the default, `C:\Tools\MXHostSwitch\MXHostSwitch.exe`, almost certainly won't match your real path — change it).
- `mouseChannel` on each entry in `machines`: the Easy-Switch channel number bonded to that computer.

If the mouse switch command fails (wrong path, tool not installed, mouse doesn't support it), it fails silently from the app's perspective on both platforms — the monitor still switches and gets its normal sound/checkmark feedback, and the mouse-switch error is only logged (visible via Console.app on macOS, or via a debugger if you go looking, on Windows), never an alert popup. The two are intentionally decoupled that way.

### Calibrating the channel numbers (and re-calibrating after a re-pair)

Easy-Switch channel assignments aren't fixed — they depend on the order you paired things in, and they can drift if you ever re-pair a channel (which is also the only way to "erase" one, since Logitech doesn't expose an official unpair command). Hardcoding channel numbers in `config.json` and hoping they stay put is fragile, so both apps read the mouse's actual current channel live instead of trusting a cached value:

- Opening the tray/menu shows a live **"Mouse channel right now: N"** label, refreshed by actually asking the mouse (via `mx-host-switch --list`) every time you open the menu — not a number the app remembered from last time.
- Two menu items, **"Set Current Channel as <name>'s Mouse"** (one per machine), read whatever channel the mouse is on right now and save it straight into that machine's `mouseChannel` in `config.json`. Use these instead of hand-editing the JSON: switch the mouse to the machine you mean (Easy-Switch button, or however you normally do it), open the tray menu on that machine, and click the matching "Set Current Channel as..." item.
- If the label ever shows "not detected" or the calibration button fails, it means the mouse isn't currently connected to that machine, `mouseSwitchPath` doesn't point at a working `mx-host-switch` executable, or `mouseSwitchEnabled` is `false` — the app tells you which.

The live read runs on a background thread on both platforms (`DispatchQueue.global` on macOS, `Task.Run` on Windows) and only updates the label once it finishes, so opening the menu never blocks waiting on a Bluetooth round-trip.

## Adapting this for your own setup

Nothing about specific machines is hardcoded. To use it with any two computers:

1. Find the input code for each computer's port (see [above](#before-you-build-find-your-monitors-real-input-codes)).
2. Put both computers in `machines` with the names you want to see, and use the same list on both sides.
3. Set `thisMachine` on each computer to its own index.

Both computers can even use the same kind of port (for example DisplayPort-1 = 15 and DisplayPort-2 = 16). If both entries have the same `inputCode`, switching can't do anything, and the Mac app logs a warning.

## Windows

See [`windows/`](./windows). Requires the .NET 8 SDK (`winget install Microsoft.DotNet.SDK.8`).

From the `windows` folder:

    dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish

Run `publish\MonitorSwitcher.exe` — a tray icon appears with the same explicit switch items (with checkmarks), a global toggle hotkey (**Ctrl+Alt+S** by default), Brightness Up/Down (the Windows side doesn't yet have the slider the macOS app has — a straightforward follow-up), a Night Mode toggle with its own hotkey (**Ctrl+Alt+N** by default, checkmark shows active state), Contrast Up/Down, mouse switching with the same live "Mouse channel right now" label and "Set Current Channel as..." calibration items described in [Mouse switching](#mouse-switching-optional) above, a confirmation sound and balloon notification on every action, and an "Open Config Folder" shortcut. To launch at login, drop a shortcut to the exe in `shell:startup`.

## Limitations / contributions welcome

- "Current state" (input, brightness, contrast, Night Mode) is tracked locally per-app, not read live from the monitor — see the note above.
- Color-channel (RGB) gain was evaluated and dropped: the reference monitor accepts the DDC `set red/green/blue` commands without error but doesn't actually change anything, so shipping it would be silently broken. It may work on other monitors — a config-gated version would be a welcome contribution.
- The brightness slider only exists on macOS so far; Windows still uses Brightness Up/Down buttons.
- Mouse switching (now on both platforms) is mouse-only (not keyboards), and depends entirely on your specific mouse exposing Logitech's undocumented HID++ CHANGE_HOST feature — test with `mx-host-switch` before relying on it. Keyboard switching remains an open question: Bluetooth-direct Logitech keyboards (like the Pebble Keys 2) don't expose the same software hook — only USB-receiver models do — so there's no known way to automate it for a Bluetooth-only setup like this one.
- No code signing/notarization on the macOS build — Gatekeeper will show an "unidentified developer" prompt on first launch of a downloaded binary; building from source avoids this entirely.
- No settings UI yet — config is a hand-edited JSON file. A preferences window would be a nice contribution.

## Author

Built by [Dolapo Falana](https://github.com/Dolappo).

## License

MIT — see [LICENSE](./LICENSE).
