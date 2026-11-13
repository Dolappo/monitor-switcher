import Cocoa
import Carbon.HIToolbox

// ============================================================================
// Config: loaded from ~/Library/Application Support/MonitorSwitcher/config.json
// (created automatically on first launch with these defaults if missing).
// Edit that file to change inputs, display number, contrast preset, mouse
// switching, or hotkeys — no rebuild required. Any field you omit falls back
// to its default individually, so older config files never break.
// ============================================================================

struct HotkeyConfig: Codable {
    var modifiers: [String]
    var key: String
}

/// One of the two computers sharing the monitor. `inputCode` is the VCP 0x60
/// value of the monitor input that computer is plugged into (VESA defaults:
/// 15 = DisplayPort-1, 16 = DisplayPort-2, 17 = HDMI-1, 18 = HDMI-2,
/// 27 = USB-C on many monitors — see README for finding yours).
/// `mouseChannel` is that computer's channel on a multi-host mouse (only used
/// when mouseSwitchEnabled is true).
struct MachineConfig: Codable {
    var name: String
    var inputCode: Int
    var mouseChannel: Int
}

struct AppConfig: Codable {
    var displayNumber: Int
    /// Exactly two machines. The same list (same order) can be used on both
    /// computers; only `thisMachine` differs.
    var machines: [MachineConfig]
    /// Index into `machines` of the computer this copy of the app runs on.
    var thisMachine: Int
    var toggleHotkey: HotkeyConfig
    var nightModeHotkey: HotkeyConfig
    var defaultLuminance: Int
    var nightModeLuminance: Int
    var defaultContrast: Int
    var contrastStep: Int

    // Optional companion switch for a Logitech multi-host mouse using the
    // reverse-engineered HID++ "CHANGE_HOST" feature, via the mx-host-switch
    // CLI (https://github.com/antonio-castellon/mx-host-switch). Off by
    // default; Logitech Flow or the mouse's own button can handle this instead.
    var mouseSwitchEnabled: Bool
    var mouseSwitchPath: String

    var local: MachineConfig { machines[thisMachine] }
    var remote: MachineConfig { machines[1 - thisMachine] }
    var remoteIndex: Int { 1 - thisMachine }

    static let defaultMachines = [
        MachineConfig(name: "Mac", inputCode: 17, mouseChannel: 2),      // HDMI-1
        MachineConfig(name: "Surface", inputCode: 15, mouseChannel: 1)   // DisplayPort-1
    ]

    static let defaultConfig = AppConfig(
        displayNumber: 1,             // from `m1ddc display list` — change to match your setup
        machines: defaultMachines,
        thisMachine: 0,
        toggleHotkey: HotkeyConfig(modifiers: ["cmd", "option"], key: "s"),
        nightModeHotkey: HotkeyConfig(modifiers: ["cmd", "option"], key: "n"),
        defaultLuminance: 75,         // assumed starting brightness (0-100) — the monitor can't be read back, see README
        nightModeLuminance: 15,
        defaultContrast: 75,
        contrastStep: 10,
        mouseSwitchEnabled: false,
        mouseSwitchPath: "/Applications/MXHostSwitch.app/Contents/MacOS/MXHostSwitch"
    )

    private enum CodingKeys: String, CodingKey {
        case displayNumber, machines, thisMachine, toggleHotkey, nightModeHotkey
        case defaultLuminance, nightModeLuminance, defaultContrast, contrastStep
        case mouseSwitchEnabled, mouseSwitchPath
        // Legacy (pre-"machines") keys — read for migration, never written.
        case displayPortCode, hdmiCode, mouseDisplayPortChannel, mouseHdmiChannel
    }

    init(displayNumber: Int, machines: [MachineConfig], thisMachine: Int, toggleHotkey: HotkeyConfig,
         nightModeHotkey: HotkeyConfig, defaultLuminance: Int, nightModeLuminance: Int,
         defaultContrast: Int, contrastStep: Int, mouseSwitchEnabled: Bool, mouseSwitchPath: String) {
        self.displayNumber = displayNumber
        self.machines = machines
        self.thisMachine = thisMachine
        self.toggleHotkey = toggleHotkey
        self.nightModeHotkey = nightModeHotkey
        self.defaultLuminance = defaultLuminance
        self.nightModeLuminance = nightModeLuminance
        self.defaultContrast = defaultContrast
        self.contrastStep = contrastStep
        self.mouseSwitchEnabled = mouseSwitchEnabled
        self.mouseSwitchPath = mouseSwitchPath
    }

    // Decodes each field independently with a fallback to its default, so a
    // config.json written before a field existed still loads correctly
    // instead of failing to parse and resetting everything to defaults.
    // Configs from before "machines" existed (displayPortCode/hdmiCode +
    // mouse*Channel) are migrated in memory: HDMI → "Mac" (this machine),
    // DisplayPort → "Surface". The file itself is only rewritten in the new
    // shape the next time the app saves (e.g. mouse calibration).
    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        let d = AppConfig.defaultConfig
        displayNumber = try c.decodeIfPresent(Int.self, forKey: .displayNumber) ?? d.displayNumber
        if let m = try? c.decodeIfPresent([MachineConfig].self, forKey: .machines), m.count == 2 {
            machines = m
            thisMachine = try c.decodeIfPresent(Int.self, forKey: .thisMachine) ?? d.thisMachine
        } else {
            let dm = AppConfig.defaultMachines
            machines = [
                MachineConfig(name: "Mac",
                              inputCode: try c.decodeIfPresent(Int.self, forKey: .hdmiCode) ?? dm[0].inputCode,
                              mouseChannel: try c.decodeIfPresent(Int.self, forKey: .mouseHdmiChannel) ?? dm[0].mouseChannel),
                MachineConfig(name: "Surface",
                              inputCode: try c.decodeIfPresent(Int.self, forKey: .displayPortCode) ?? dm[1].inputCode,
                              mouseChannel: try c.decodeIfPresent(Int.self, forKey: .mouseDisplayPortChannel) ?? dm[1].mouseChannel)
            ]
            thisMachine = 0
        }
        if thisMachine != 0 && thisMachine != 1 { thisMachine = 0 }
        toggleHotkey = try c.decodeIfPresent(HotkeyConfig.self, forKey: .toggleHotkey) ?? d.toggleHotkey
        nightModeHotkey = try c.decodeIfPresent(HotkeyConfig.self, forKey: .nightModeHotkey) ?? d.nightModeHotkey
        defaultLuminance = try c.decodeIfPresent(Int.self, forKey: .defaultLuminance) ?? d.defaultLuminance
        nightModeLuminance = try c.decodeIfPresent(Int.self, forKey: .nightModeLuminance) ?? d.nightModeLuminance
        defaultContrast = try c.decodeIfPresent(Int.self, forKey: .defaultContrast) ?? d.defaultContrast
        contrastStep = try c.decodeIfPresent(Int.self, forKey: .contrastStep) ?? d.contrastStep
        mouseSwitchEnabled = try c.decodeIfPresent(Bool.self, forKey: .mouseSwitchEnabled) ?? d.mouseSwitchEnabled
        mouseSwitchPath = try c.decodeIfPresent(String.self, forKey: .mouseSwitchPath) ?? d.mouseSwitchPath
        if machines[0].inputCode == machines[1].inputCode {
            NSLog("MonitorSwitcher: both machines in config.json use inputCode \(machines[0].inputCode) — switching won't do anything until one is changed")
        }
    }

    func encode(to encoder: Encoder) throws {
        var c = encoder.container(keyedBy: CodingKeys.self)
        try c.encode(displayNumber, forKey: .displayNumber)
        try c.encode(machines, forKey: .machines)
        try c.encode(thisMachine, forKey: .thisMachine)
        try c.encode(toggleHotkey, forKey: .toggleHotkey)
        try c.encode(nightModeHotkey, forKey: .nightModeHotkey)
        try c.encode(defaultLuminance, forKey: .defaultLuminance)
        try c.encode(nightModeLuminance, forKey: .nightModeLuminance)
        try c.encode(defaultContrast, forKey: .defaultContrast)
        try c.encode(contrastStep, forKey: .contrastStep)
        try c.encode(mouseSwitchEnabled, forKey: .mouseSwitchEnabled)
        try c.encode(mouseSwitchPath, forKey: .mouseSwitchPath)
    }
}

let M1DDC_PATH = "/opt/homebrew/bin/m1ddc"

func configDirectoryURL() -> URL {
    let fm = FileManager.default
    let appSupport = fm.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
    let dir = appSupport.appendingPathComponent("MonitorSwitcher", isDirectory: true)
    try? fm.createDirectory(at: dir, withIntermediateDirectories: true)
    return dir
}

func configFileURL() -> URL {
    configDirectoryURL().appendingPathComponent("config.json")
}

func loadConfig() -> AppConfig {
    let url = configFileURL()
    if let data = try? Data(contentsOf: url),
       let config = try? JSONDecoder().decode(AppConfig.self, from: data) {
        return config
    }
    let defaults = AppConfig.defaultConfig
    let encoder = JSONEncoder()
    encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
    if let data = try? encoder.encode(defaults) {
        try? data.write(to: url)
    }
    return defaults
}

// macOS ANSI virtual keycodes for letters (stable, standard US keyboard layout)
let macVirtualKeycodes: [String: UInt32] = [
    "a": 0x00, "b": 0x0B, "c": 0x08, "d": 0x02, "e": 0x0E, "f": 0x03, "g": 0x05,
    "h": 0x04, "i": 0x22, "j": 0x26, "k": 0x28, "l": 0x25, "m": 0x2E, "n": 0x2D,
    "o": 0x1F, "p": 0x23, "q": 0x0C, "r": 0x0F, "s": 0x01, "t": 0x11, "u": 0x20,
    "v": 0x09, "w": 0x0D, "x": 0x07, "y": 0x10, "z": 0x06
]

let macModifierFlags: [String: UInt32] = [
    "cmd": UInt32(cmdKey), "command": UInt32(cmdKey),
    "option": UInt32(optionKey), "alt": UInt32(optionKey),
    "shift": UInt32(shiftKey),
    "control": UInt32(controlKey), "ctrl": UInt32(controlKey)
]

func fourCharCode(_ string: String) -> FourCharCode {
    var result: FourCharCode = 0
    for char in string.utf16 {
        result = (result << 8) + FourCharCode(char)
    }
    return result
}

private func hotKeyEventHandler(nextHandler: EventHandlerCallRef?, event: EventRef?, userData: UnsafeMutableRawPointer?) -> OSStatus {
    var hotKeyID = EventHotKeyID()
    GetEventParameter(event, EventParamName(kEventParamDirectObject), EventParamType(typeEventHotKeyID), nil, MemoryLayout<EventHotKeyID>.size, nil, &hotKeyID)
    switch hotKeyID.id {
    case 1: AppDelegate.shared?.toggleInput()
    case 2: AppDelegate.shared?.toggleNightMode()
    default: break
    }
    return noErr
}

final class AppDelegate: NSObject, NSApplicationDelegate, NSMenuDelegate {
    static var shared: AppDelegate?

    var statusItem: NSStatusItem!
    var config: AppConfig!
    var hotKeyRefToggle: EventHotKeyRef?
    var hotKeyRefNightMode: EventHotKeyRef?

    /// One "switch to" item per machine, indexed like config.machines.
    var machineMenuItems: [NSMenuItem] = []
    var nightModeMenuItem: NSMenuItem!
    var brightnessLabelItem: NSMenuItem!
    var brightnessSlider: NSSlider!
    var brightnessDebounceTimer: Timer?
    var mouseChannelLabelItem: NSMenuItem!

    /// Input we've just asked the monitor to switch to but m1ddc hasn't
    /// confirmed yet. The toggle hotkey reads this first, so pressing it
    /// twice quickly flips back instead of re-sending the same input.
    var pendingInput: Int?

    // Loaded once at launch; NSSound(named:) on every switch adds a delay,
    // and play() on an already-playing NSSound silently does nothing.
    let successSound = NSSound(named: "Pop")
    let failureSound = NSSound(named: "Basso")
    var feedbackResetWork: DispatchWorkItem?
    var isShowingFeedback = false

    // Display-presence tracking: when the shared monitor vanishes from this
    // Mac's display list the other machine has it; when it reappears, this
    // Mac does. Lets the icon stay correct even when the switch was made from
    // the other computer (or the monitor's own buttons).
    var lastMonitorPresent: Bool?
    var displayCheckWork: DispatchWorkItem?
    var monitorUUID: String? {
        get { UserDefaults.standard.string(forKey: "monitorUUID") }
        set { UserDefaults.standard.set(newValue, forKey: "monitorUUID") }
    }

    // Best-effort tracked state: m1ddc can't reliably read input, luminance,
    // or contrast back from this monitor, so every value here is "what this
    // app last set it to," not a live read. See README for details.
    var currentInput: Int? {
        didSet { UserDefaults.standard.set(currentInput, forKey: "lastKnownInput") }
    }
    var currentLuminance: Int = 0 {
        didSet { UserDefaults.standard.set(currentLuminance, forKey: "currentLuminance") }
    }
    var currentContrast: Int = 0 {
        didSet { UserDefaults.standard.set(currentContrast, forKey: "currentContrast") }
    }
    var isNightMode: Bool = false {
        didSet { UserDefaults.standard.set(isNightMode, forKey: "isNightMode") }
    }
    var preNightModeLuminance: Int = 0 {
        didSet { UserDefaults.standard.set(preNightModeLuminance, forKey: "preNightModeLuminance") }
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        AppDelegate.shared = self
        NSApp.setActivationPolicy(.accessory) // menu bar only, no Dock icon, no bundle/plist needed

        config = loadConfig()
        let defaults = UserDefaults.standard
        currentInput = (defaults.object(forKey: "lastKnownInput") as? Int)
        currentLuminance = defaults.object(forKey: "currentLuminance") as? Int ?? config.defaultLuminance
        currentContrast = defaults.object(forKey: "currentContrast") as? Int ?? config.defaultContrast
        isNightMode = defaults.bool(forKey: "isNightMode")
        preNightModeLuminance = defaults.object(forKey: "preNightModeLuminance") as? Int ?? config.defaultLuminance

        // variableLength, not squareLength: a square item clips the "✓ HDMI"
        // feedback text, so the switch looked like it hadn't registered.
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        statusItem.button?.title = restingTitle()

        buildMenu()
        registerGlobalHotKeys()
        startWatchingDisplays()
    }

    func buildMenu() {
        let menu = NSMenu()

        // This machine first, then the other one.
        machineMenuItems = config.machines.enumerated().map { (i, m) in
            let title = (i == config.thisMachine) ? "Bring monitor here (\(m.name))" : "Show monitor on \(m.name)"
            let item = NSMenuItem(title: title, action: #selector(switchToMachine(_:)), keyEquivalent: "")
            item.tag = i
            return item
        }
        menu.addItem(machineMenuItems[config.thisMachine])
        menu.addItem(machineMenuItems[config.remoteIndex])
        menu.addItem(NSMenuItem.separator())

        let toggleInfo = NSMenuItem(title: "Toggle: \(hotkeyString(config.toggleHotkey))", action: nil, keyEquivalent: "")
        toggleInfo.isEnabled = false
        menu.addItem(toggleInfo)
        menu.addItem(NSMenuItem(title: "Reveal Config File in Finder", action: #selector(revealConfig), keyEquivalent: ""))
        menu.addItem(NSMenuItem.separator())

        brightnessLabelItem = NSMenuItem(title: "Brightness: \(currentLuminance)%", action: nil, keyEquivalent: "")
        brightnessLabelItem.isEnabled = false
        menu.addItem(brightnessLabelItem)
        let (sliderItem, slider) = makeSliderMenuItem(value: currentLuminance, action: #selector(brightnessSliderChanged(_:)))
        brightnessSlider = slider
        menu.addItem(sliderItem)

        nightModeMenuItem = NSMenuItem(title: "Night Mode (\(hotkeyString(config.nightModeHotkey)))", action: #selector(toggleNightMode), keyEquivalent: "")
        menu.addItem(nightModeMenuItem)
        menu.addItem(NSMenuItem.separator())

        menu.addItem(NSMenuItem(title: "Contrast Up", action: #selector(contrastUp), keyEquivalent: ""))
        menu.addItem(NSMenuItem(title: "Contrast Down", action: #selector(contrastDown), keyEquivalent: ""))
        menu.addItem(NSMenuItem.separator())

        // Mouse channel is genuinely live-readable (unlike the monitor's
        // input/brightness/contrast), so this label is refreshed every time
        // the menu opens — see menuWillOpen below — rather than app-tracked.
        mouseChannelLabelItem = NSMenuItem(title: "Mouse channel: —", action: nil, keyEquivalent: "")
        mouseChannelLabelItem.isEnabled = false
        menu.addItem(mouseChannelLabelItem)
        for i in [config.thisMachine, config.remoteIndex] {
            let item = NSMenuItem(title: "Set Current Channel as \(config.machines[i].name)'s Mouse",
                                  action: #selector(calibrateMouseForMachine(_:)), keyEquivalent: "")
            item.tag = i
            menu.addItem(item)
        }
        menu.addItem(NSMenuItem.separator())

        menu.addItem(NSMenuItem(title: "Quit", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q"))
        menu.delegate = self
        statusItem.menu = menu
        updateCheckmarks()
    }

    /// Refreshes the live mouse-channel label every time the menu is opened,
    /// so it never goes stale the way app-tracked state can.
    func menuWillOpen(_ menu: NSMenu) {
        updateMouseChannelLabel()
    }

    /// Builds a slider embedded directly in an NSMenuItem's custom view — the
    /// same pattern macOS's own Control Center brightness slider uses. The
    /// menu doesn't auto-close while you drag it.
    func makeSliderMenuItem(value: Int, action: Selector) -> (NSMenuItem, NSSlider) {
        let container = NSView(frame: NSRect(x: 0, y: 0, width: 240, height: 24))
        let slider = NSSlider(frame: NSRect(x: 20, y: 2, width: 200, height: 20))
        slider.minValue = 0
        slider.maxValue = 100
        slider.integerValue = value
        slider.isContinuous = true
        slider.target = self
        slider.action = action
        container.addSubview(slider)
        let item = NSMenuItem()
        item.view = container
        return (item, slider)
    }

    func updateCheckmarks() {
        for (i, item) in machineMenuItems.enumerated() {
            item.state = (currentInput == config.machines[i].inputCode) ? .on : .off
        }
        if !isShowingFeedback { statusItem.button?.title = restingTitle() }
        nightModeMenuItem.state = isNightMode ? .on : .off
        brightnessLabelItem.title = "Brightness: \(currentLuminance)%"
        brightnessSlider.integerValue = currentLuminance
    }

    func hotkeyString(_ hk: HotkeyConfig) -> String {
        let symbols = hk.modifiers.map { m -> String in
            switch m.lowercased() {
            case "cmd", "command": return "⌘"
            case "option", "alt": return "⌥"
            case "shift": return "⇧"
            case "control", "ctrl": return "⌃"
            default: return ""
            }
        }
        return (symbols + [hk.key.uppercased()]).joined()
    }

    func registerGlobalHotKeys() {
        var eventType = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: OSType(kEventHotKeyPressed))
        InstallEventHandler(GetApplicationEventTarget(), hotKeyEventHandler, 1, &eventType, nil, nil)

        registerHotKey(config.toggleHotkey, id: 1, signature: "mnSW", ref: &hotKeyRefToggle)
        registerHotKey(config.nightModeHotkey, id: 2, signature: "mnNM", ref: &hotKeyRefNightMode)
    }

    func registerHotKey(_ hk: HotkeyConfig, id: UInt32, signature: String, ref: inout EventHotKeyRef?) {
        guard let keyCode = macVirtualKeycodes[hk.key.lowercased()] else {
            NSLog("MonitorSwitcher: unrecognized hotkey letter '\(hk.key)' in config.json — that hotkey is disabled")
            return
        }
        var modifierMask: UInt32 = 0
        for m in hk.modifiers {
            modifierMask |= macModifierFlags[m.lowercased()] ?? 0
        }
        let hotKeyID = EventHotKeyID(signature: fourCharCode(signature), id: id)
        RegisterEventHotKey(keyCode, modifierMask, hotKeyID, GetApplicationEventTarget(), 0, &ref)
    }

    @objc func switchToMachine(_ sender: NSMenuItem) { switchInput(toMachine: sender.tag) }

    @objc func toggleInput() {
        let effective = pendingInput ?? currentInput
        // Unknown state counts as "here", so the first press sends it away.
        let target = (effective == config.remote.inputCode) ? config.thisMachine : config.remoteIndex
        switchInput(toMachine: target)
    }

    func switchInput(toMachine index: Int) {
        let machine = config.machines[index]
        let target = machine.inputCode
        pendingInput = target
        runM1DDCSet(feature: "input", value: target, label: machine.name, onSuccess: {
            self.currentInput = target
            self.switchMouseChannel(machine.mouseChannel)
        }, onComplete: {
            if self.pendingInput == target { self.pendingInput = nil }
        })
    }

    /// Best-effort companion switch for the mouse, run only after the
    /// monitor switch itself has already succeeded (see callers above), so
    /// we never move the mouse to a machine that doesn't have the picture.
    /// Failures here are logged, not surfaced with an alert — the monitor
    /// switch is the primary action and already got its own feedback.
    func switchMouseChannel(_ channel: Int) {
        guard config.mouseSwitchEnabled else { return }
        guard FileManager.default.isExecutableFile(atPath: config.mouseSwitchPath) else {
            NSLog("MonitorSwitcher: mouse switch tool not found at \(config.mouseSwitchPath) — skipping")
            return
        }
        let path = config.mouseSwitchPath
        // Bluetooth HID++ round-trips take a noticeable moment; never make
        // the menu bar icon or sound wait on them.
        DispatchQueue.global(qos: .utility).async {
            let task = Process()
            task.executableURL = URL(fileURLWithPath: path)
            task.arguments = ["--switch", "\(channel)"]
            do {
                try task.run()
                task.waitUntilExit()
                if task.terminationStatus != 0 {
                    NSLog("MonitorSwitcher: mouse switch to channel \(channel) exited with status \(task.terminationStatus)")
                }
            } catch {
                NSLog("MonitorSwitcher: couldn't run mouse switch tool: \(error)")
            }
        }
    }

    /// Runs `mx-host-switch --list` and pulls the mouse's *actual current*
    /// channel out of its output (a line containing `current=Channel N/M`).
    /// Unlike the monitor's input/brightness/contrast, this is a genuine
    /// live read straight from the mouse, not app-tracked state — so it's
    /// only accurate for whichever computer is running this right now.
    /// Returns nil if switching is disabled, the tool is missing, or nothing
    /// was detected (e.g. the mouse isn't connected to this machine).
    func queryCurrentMouseChannel() -> Int? {
        guard config.mouseSwitchEnabled,
              FileManager.default.isExecutableFile(atPath: config.mouseSwitchPath) else { return nil }
        let task = Process()
        task.executableURL = URL(fileURLWithPath: config.mouseSwitchPath)
        task.arguments = ["--list"]
        let pipe = Pipe()
        task.standardOutput = pipe
        do {
            try task.run()
            let data = pipe.fileHandleForReading.readDataToEndOfFile()
            task.waitUntilExit()
            guard let output = String(data: data, encoding: .utf8),
                  let range = output.range(of: "current=Channel [0-9]+", options: .regularExpression) else {
                return nil
            }
            let digits = output[range].drop(while: { !$0.isNumber })
            return Int(digits)
        } catch {
            NSLog("MonitorSwitcher: couldn't query mouse channel: \(error)")
            return nil
        }
    }

    /// Updates the label without blocking the menu's appearance. The
    /// subprocess query takes a noticeable moment (real HID++ round-trips
    /// over Bluetooth aren't instant), so this shows "checking…" right away
    /// and fills in the real value a beat later on the main thread — the
    /// menu itself always opens immediately.
    func updateMouseChannelLabel() {
        guard mouseChannelLabelItem != nil else { return }
        guard config.mouseSwitchEnabled else {
            mouseChannelLabelItem.title = "Mouse switching: disabled in config"
            return
        }
        mouseChannelLabelItem.title = "Mouse channel: checking…"
        DispatchQueue.global(qos: .userInitiated).async { [weak self] in
            let channel = self?.queryCurrentMouseChannel()
            DispatchQueue.main.async {
                guard let self = self, self.mouseChannelLabelItem != nil else { return }
                if let channel = channel {
                    self.mouseChannelLabelItem.title = "Mouse channel right now: \(channel)"
                } else {
                    self.mouseChannelLabelItem.title = "Mouse channel: not detected"
                }
            }
        }
    }

    /// One-click recalibration: whichever channel the mouse is *actually on
    /// right now* becomes the saved value for that role, and is written back
    /// to config.json immediately. Use this any time a re-pair shifts which
    /// channel belongs to which computer, instead of hand-editing JSON or
    /// guessing — just switch the mouse to the machine in question first,
    /// then click the matching item here.
    @objc func calibrateMouseForMachine(_ sender: NSMenuItem) { calibrateMouseChannel(machineIndex: sender.tag) }

    func calibrateMouseChannel(machineIndex: Int) {
        let roleName = config.machines[machineIndex].name
        guard let channel = queryCurrentMouseChannel() else {
            let alert = NSAlert()
            alert.messageText = "Couldn't read the mouse's current channel"
            alert.informativeText = config.mouseSwitchEnabled
                ? "Make sure the mouse is connected to this Mac right now, and that mouseSwitchPath in config.json points to a working mx-host-switch binary."
                : "Mouse switching is disabled (mouseSwitchEnabled: false in config.json)."
            alert.runModal()
            return
        }
        config.machines[machineIndex].mouseChannel = channel
        saveConfig()
        updateMouseChannelLabel()
        playFeedback(success: true, label: "\(roleName) mouse = Ch \(channel)")
    }

    /// Persists the current in-memory config back to config.json, preserving
    /// whatever the user hand-edited elsewhere in the file's structure.
    func saveConfig() {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        if let data = try? encoder.encode(config) {
            try? data.write(to: configFileURL())
        } else {
            NSLog("MonitorSwitcher: failed to encode config for saving")
        }
    }

    /// Live-updates the label as you drag, but only actually sends the DDC
    /// command ~150ms after you pause — sliding smoothly to a value fires
    /// m1ddc once, not on every pixel of drag.
    @objc func brightnessSliderChanged(_ sender: NSSlider) {
        let value = sender.integerValue
        brightnessLabelItem.title = "Brightness: \(value)%"
        brightnessDebounceTimer?.invalidate()
        brightnessDebounceTimer = Timer.scheduledTimer(withTimeInterval: 0.15, repeats: false) { [weak self] _ in
            self?.applyBrightness(value)
        }
    }

    func applyBrightness(_ value: Int) {
        runM1DDCSet(feature: "luminance", value: value, label: "\(value)%") { [weak self] in
            self?.currentLuminance = value
        }
    }

    @objc func contrastUp() {
        let value = min(100, currentContrast + config.contrastStep)
        runM1DDCSet(feature: "contrast", value: value, label: "Contrast \(value)") { self.currentContrast = value }
    }

    @objc func contrastDown() {
        let value = max(0, currentContrast - config.contrastStep)
        runM1DDCSet(feature: "contrast", value: value, label: "Contrast \(value)") { self.currentContrast = value }
    }

    @objc func toggleNightMode() {
        if isNightMode {
            let restore = preNightModeLuminance
            runM1DDCSet(feature: "luminance", value: restore, label: "\(restore)%") {
                self.currentLuminance = restore
                self.isNightMode = false
                self.updateCheckmarks()
            }
        } else {
            preNightModeLuminance = currentLuminance
            runM1DDCSet(feature: "luminance", value: config.nightModeLuminance, label: "Night") {
                self.currentLuminance = self.config.nightModeLuminance
                self.isNightMode = true
                self.updateCheckmarks()
            }
        }
    }

    @objc func revealConfig() {
        NSWorkspace.shared.activateFileViewerSelecting([configFileURL()])
    }

    /// Runs `m1ddc display N set <feature> <value>` off the main thread so
    /// the menu bar never freezes while the monitor re-syncs.
    ///
    /// Feedback is optimistic: the icon and sound fire the instant the
    /// command is sent, because DDC writes almost never fail once the setup
    /// works and m1ddc can take a second or more to return after an input
    /// change. If it does fail, the ✗ and error sound replace the ✓.
    /// App state (`onSuccess`) is still only updated once m1ddc confirms.
    func runM1DDCSet(feature: String, value: Int, label: String? = nil,
                     onSuccess: @escaping () -> Void, onComplete: (() -> Void)? = nil) {
        let displayLabel = label ?? inputLabel(for: value)
        let args = ["display", "\(config.displayNumber)", "set", feature, "\(value)"]
        playFeedback(success: true, label: displayLabel)

        DispatchQueue.global(qos: .userInitiated).async { [weak self] in
            let task = Process()
            task.executableURL = URL(fileURLWithPath: M1DDC_PATH)
            task.arguments = args
            task.standardOutput = FileHandle.nullDevice
            task.standardError = FileHandle.nullDevice
            var launchError: Error?
            do {
                try task.run()
                task.waitUntilExit()
            } catch {
                launchError = error
            }
            DispatchQueue.main.async {
                guard let self = self else { return }
                defer { onComplete?() }
                if let error = launchError {
                    self.playFeedback(success: false, label: nil)
                    let alert = NSAlert()
                    alert.messageText = "Couldn't run m1ddc"
                    alert.informativeText = "\(error)\n\nCheck that m1ddc is installed at \(M1DDC_PATH)."
                    alert.runModal()
                } else if task.terminationStatus == 0 {
                    onSuccess()
                    self.updateCheckmarks()
                } else {
                    self.playFeedback(success: false, label: nil)
                }
            }
        }
    }

    func inputLabel(for value: Int) -> String {
        if let m = config.machines.first(where: { $0.inputCode == value }) { return m.name }
        return "\(value)"
    }

    /// The icon at rest: which machine currently has the monitor, if known.
    func restingTitle() -> String {
        if let input = currentInput, let m = config.machines.first(where: { $0.inputCode == input }) {
            return "⇄ \(m.name)"
        }
        return "⇄"
    }

    // MARK: - Display presence

    func startWatchingDisplays() {
        NotificationCenter.default.addObserver(forName: NSApplication.didChangeScreenParametersNotification,
                                               object: nil, queue: .main) { [weak self] _ in
            self?.scheduleDisplayCheck()
        }
        lastMonitorPresent = isMonitorConnected()
        resolveMonitorUUID()
    }

    /// Learns the shared monitor's display UUID from `m1ddc display list`
    /// (lines look like `[2] Mi Monitor (2391FA75-...)`), so presence checks
    /// can pick it out among other screens. Remembered across launches.
    func resolveMonitorUUID() {
        let displayNumber = config.displayNumber
        DispatchQueue.global(qos: .utility).async { [weak self] in
            let task = Process()
            task.executableURL = URL(fileURLWithPath: M1DDC_PATH)
            task.arguments = ["display", "list"]
            let pipe = Pipe()
            task.standardOutput = pipe
            task.standardError = FileHandle.nullDevice
            guard (try? task.run()) != nil else { return }
            let data = pipe.fileHandleForReading.readDataToEndOfFile()
            task.waitUntilExit()
            guard let output = String(data: data, encoding: .utf8) else { return }
            for line in output.split(separator: "\n") where line.hasPrefix("[\(displayNumber)]") {
                guard !line.contains("(null)"),
                      let open = line.lastIndex(of: "("), let close = line.lastIndex(of: ")"), open < close else { continue }
                let uuid = String(line[line.index(after: open)..<close])
                DispatchQueue.main.async {
                    guard let self = self else { return }
                    self.monitorUUID = uuid
                    self.lastMonitorPresent = self.isMonitorConnected()
                }
            }
        }
    }

    /// Is the shared monitor in this Mac's online display list right now?
    /// Matches by UUID when known; otherwise any external display counts.
    func isMonitorConnected() -> Bool {
        var count: UInt32 = 0
        guard CGGetOnlineDisplayList(0, nil, &count) == .success, count > 0 else { return false }
        var ids = [CGDirectDisplayID](repeating: 0, count: Int(count))
        guard CGGetOnlineDisplayList(count, &ids, &count) == .success else { return false }
        let wanted = monitorUUID?.uppercased()
        for id in ids.prefix(Int(count)) {
            if let wanted = wanted {
                if let cfUUID = CGDisplayCreateUUIDFromDisplayID(id)?.takeRetainedValue(),
                   let s = CFUUIDCreateString(nil, cfUUID) as String?, s.uppercased() == wanted {
                    return true
                }
            } else if CGDisplayIsBuiltin(id) == 0 {
                return true
            }
        }
        return false
    }

    /// Displays flicker in and out while a monitor re-syncs, so only act on
    /// the state once it has settled for a moment.
    func scheduleDisplayCheck() {
        displayCheckWork?.cancel()
        let work = DispatchWorkItem { [weak self] in self?.checkDisplayPresence() }
        displayCheckWork = work
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.0, execute: work)
    }

    func checkDisplayPresence() {
        let present = isMonitorConnected()
        guard present != lastMonitorPresent else { return }
        lastMonitorPresent = present
        // Our own switch is in flight — its result will set the state.
        guard pendingInput == nil else { return }
        let newInput = present ? config.local.inputCode : config.remote.inputCode
        guard newInput != currentInput else { return }
        currentInput = newInput
        updateCheckmarks()
    }

    func playFeedback(success: Bool, label: String?) {
        let sound = success ? successSound : failureSound
        sound?.stop()   // restart if a previous switch's sound is still playing
        sound?.play()
        statusItem.button?.title = success ? "✓ \(label ?? "")" : "✗"
        isShowingFeedback = true
        // Cancel the previous reset so quick repeated switches don't have an
        // older timer snap the icon back mid-feedback.
        feedbackResetWork?.cancel()
        let work = DispatchWorkItem { [weak self] in
            guard let self = self else { return }
            self.isShowingFeedback = false
            self.statusItem.button?.title = self.restingTitle()
        }
        feedbackResetWork = work
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.2, execute: work)
    }
}

let app = NSApplication.shared
let delegate = AppDelegate()
app.delegate = delegate
app.run()
