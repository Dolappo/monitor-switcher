import Cocoa
import Carbon.HIToolbox

// ============================================================================
// Config: loaded from ~/Library/Application Support/MonitorSwitcher/config.json
// (created automatically on first launch with these defaults if missing).
// Edit that file to change inputs, display number, brightness/contrast
// presets, or hotkeys — no rebuild required.
// ============================================================================

struct HotkeyConfig: Codable {
    var modifiers: [String]
    var key: String
}

struct AppConfig: Codable {
    var displayNumber: Int
    var displayPortCode: Int
    var hdmiCode: Int
    var toggleHotkey: HotkeyConfig
    var nightModeHotkey: HotkeyConfig
    var defaultLuminance: Int
    var nightModeLuminance: Int
    var luminanceStep: Int
    var defaultContrast: Int
    var contrastStep: Int

    static let defaultConfig = AppConfig(
        displayNumber: 1,             // from `m1ddc display list` — change to match your setup
        displayPortCode: 15,          // VESA default: DisplayPort-1
        hdmiCode: 17,                 // VESA default: HDMI-1
        toggleHotkey: HotkeyConfig(modifiers: ["cmd", "option"], key: "s"),
        nightModeHotkey: HotkeyConfig(modifiers: ["cmd", "option"], key: "n"),
        defaultLuminance: 75,         // assumed starting brightness (0-100) — the monitor can't be read back, see README
        nightModeLuminance: 15,
        luminanceStep: 10,
        defaultContrast: 75,
        contrastStep: 10
    )
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

final class AppDelegate: NSObject, NSApplicationDelegate {
    static var shared: AppDelegate?

    var statusItem: NSStatusItem!
    var config: AppConfig!
    var hotKeyRefToggle: EventHotKeyRef?
    var hotKeyRefNightMode: EventHotKeyRef?

    var displayPortMenuItem: NSMenuItem!
    var hdmiMenuItem: NSMenuItem!
    var nightModeMenuItem: NSMenuItem!

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

        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        statusItem.button?.title = "⇄"

        buildMenu()
        registerGlobalHotKeys()
    }

    func buildMenu() {
        let menu = NSMenu()

        displayPortMenuItem = NSMenuItem(title: "Send monitor to Surface (DisplayPort)", action: #selector(switchToDisplayPort), keyEquivalent: "")
        hdmiMenuItem = NSMenuItem(title: "Bring monitor to this Mac (HDMI)", action: #selector(switchToHDMI), keyEquivalent: "")
        menu.addItem(displayPortMenuItem)
        menu.addItem(hdmiMenuItem)
        menu.addItem(NSMenuItem.separator())

        let toggleInfo = NSMenuItem(title: "Toggle: \(hotkeyString(config.toggleHotkey))", action: nil, keyEquivalent: "")
        toggleInfo.isEnabled = false
        menu.addItem(toggleInfo)
        menu.addItem(NSMenuItem(title: "Reveal Config File in Finder", action: #selector(revealConfig), keyEquivalent: ""))
        menu.addItem(NSMenuItem.separator())

        menu.addItem(NSMenuItem(title: "Brightness Up", action: #selector(brightnessUp), keyEquivalent: ""))
        menu.addItem(NSMenuItem(title: "Brightness Down", action: #selector(brightnessDown), keyEquivalent: ""))
        nightModeMenuItem = NSMenuItem(title: "Night Mode (\(hotkeyString(config.nightModeHotkey)))", action: #selector(toggleNightMode), keyEquivalent: "")
        menu.addItem(nightModeMenuItem)
        menu.addItem(NSMenuItem.separator())

        menu.addItem(NSMenuItem(title: "Contrast Up", action: #selector(contrastUp), keyEquivalent: ""))
        menu.addItem(NSMenuItem(title: "Contrast Down", action: #selector(contrastDown), keyEquivalent: ""))
        menu.addItem(NSMenuItem.separator())

        menu.addItem(NSMenuItem(title: "Quit", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q"))
        statusItem.menu = menu
        updateCheckmarks()
    }

    func updateCheckmarks() {
        displayPortMenuItem.state = (currentInput == config.displayPortCode) ? .on : .off
        hdmiMenuItem.state = (currentInput == config.hdmiCode) ? .on : .off
        nightModeMenuItem.state = isNightMode ? .on : .off
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

    @objc func switchToDisplayPort() { runM1DDCSet(feature: "input", value: config.displayPortCode) { self.currentInput = self.config.displayPortCode } }
    @objc func switchToHDMI()        { runM1DDCSet(feature: "input", value: config.hdmiCode) { self.currentInput = self.config.hdmiCode } }

    @objc func toggleInput() {
        let target = (currentInput == config.displayPortCode) ? config.hdmiCode : config.displayPortCode
        runM1DDCSet(feature: "input", value: target) { self.currentInput = target }
    }

    @objc func brightnessUp() {
        let value = min(100, currentLuminance + config.luminanceStep)
        runM1DDCSet(feature: "luminance", value: value, label: "\(value)%") { self.currentLuminance = value }
    }

    @objc func brightnessDown() {
        let value = max(0, currentLuminance - config.luminanceStep)
        runM1DDCSet(feature: "luminance", value: value, label: "\(value)%") { self.currentLuminance = value }
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

    /// Runs `m1ddc display N set <feature> <value>`, and on success updates
    /// app state via `onSuccess`, refreshes checkmarks, and plays feedback.
    func runM1DDCSet(feature: String, value: Int, label: String? = nil, onSuccess: @escaping () -> Void) {
        let task = Process()
        task.executableURL = URL(fileURLWithPath: M1DDC_PATH)
        task.arguments = ["display", "\(config.displayNumber)", "set", feature, "\(value)"]
        do {
            try task.run()
            task.waitUntilExit()
            if task.terminationStatus == 0 {
                onSuccess()
                updateCheckmarks()
                playFeedback(success: true, label: label ?? inputLabel(for: value))
            } else {
                playFeedback(success: false, label: nil)
            }
        } catch {
            let alert = NSAlert()
            alert.messageText = "Couldn't run m1ddc"
            alert.informativeText = "\(error)\n\nCheck that m1ddc is installed at \(M1DDC_PATH)."
            alert.runModal()
        }
    }

    func inputLabel(for value: Int) -> String {
        if value == config.displayPortCode { return "DP" }
        if value == config.hdmiCode { return "HDMI" }
        return "\(value)"
    }

    func playFeedback(success: Bool, label: String?) {
        NSSound(named: success ? "Pop" : "Basso")?.play()
        statusItem.button?.title = success ? "✓ \(label ?? "")" : "✗"
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.2) { [weak self] in
            self?.statusItem.button?.title = "⇄"
        }
    }
}

let app = NSApplication.shared
let delegate = AppDelegate()
app.delegate = delegate
app.run()
