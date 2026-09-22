import Cocoa
import Carbon.HIToolbox

// ============================================================================
// Config: loaded from ~/Library/Application Support/MonitorSwitcher/config.json
// (created automatically on first launch with these defaults if missing).
// Edit that file to change inputs, display number, or the toggle hotkey —
// no rebuild required.
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

    static let defaultConfig = AppConfig(
        displayNumber: 1,             // from `m1ddc display list` — change to match your setup
        displayPortCode: 15,          // VESA default: DisplayPort-1
        hdmiCode: 17,                 // VESA default: HDMI-1
        toggleHotkey: HotkeyConfig(modifiers: ["cmd", "option"], key: "s")
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
    if hotKeyID.id == 1 {
        AppDelegate.shared?.toggleInput()
    }
    return noErr
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    static var shared: AppDelegate?

    var statusItem: NSStatusItem!
    var config: AppConfig!
    var hotKeyRefToggle: EventHotKeyRef?
    var displayPortMenuItem: NSMenuItem!
    var hdmiMenuItem: NSMenuItem!

    // Best-effort tracked state: m1ddc has no "get input" command, so we can't
    // truly read the monitor's current input. We remember the last input THIS
    // app successfully switched to. It's accurate right after you use this
    // app, and only goes stale if the other machine switches inputs first —
    // it self-corrects the next time you switch from here.
    var currentInput: Int? {
        didSet { UserDefaults.standard.set(currentInput, forKey: "lastKnownInput") }
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        AppDelegate.shared = self
        NSApp.setActivationPolicy(.accessory) // menu bar only, no Dock icon, no bundle/plist needed

        config = loadConfig()
        currentInput = (UserDefaults.standard.object(forKey: "lastKnownInput") as? Int)

        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        statusItem.button?.title = "⇄"

        buildMenu()
        registerGlobalHotKey()
    }

    func buildMenu() {
        let menu = NSMenu()

        displayPortMenuItem = NSMenuItem(title: "Send monitor to Surface (DisplayPort)", action: #selector(switchToDisplayPort), keyEquivalent: "")
        hdmiMenuItem = NSMenuItem(title: "Bring monitor to this Mac (HDMI)", action: #selector(switchToHDMI), keyEquivalent: "")
        menu.addItem(displayPortMenuItem)
        menu.addItem(hdmiMenuItem)
        menu.addItem(NSMenuItem.separator())

        let toggleInfo = NSMenuItem(title: "Toggle: \(hotkeyDisplayString())", action: nil, keyEquivalent: "")
        toggleInfo.isEnabled = false
        menu.addItem(toggleInfo)
        menu.addItem(NSMenuItem(title: "Reveal Config File in Finder", action: #selector(revealConfig), keyEquivalent: ""))
        menu.addItem(NSMenuItem.separator())

        menu.addItem(NSMenuItem(title: "Quit", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q"))
        statusItem.menu = menu
        updateCheckmarks()
    }

    func updateCheckmarks() {
        displayPortMenuItem.state = (currentInput == config.displayPortCode) ? .on : .off
        hdmiMenuItem.state = (currentInput == config.hdmiCode) ? .on : .off
    }

    func hotkeyDisplayString() -> String {
        let symbols = config.toggleHotkey.modifiers.map { m -> String in
            switch m.lowercased() {
            case "cmd", "command": return "⌘"
            case "option", "alt": return "⌥"
            case "shift": return "⇧"
            case "control", "ctrl": return "⌃"
            default: return ""
            }
        }
        return (symbols + [config.toggleHotkey.key.uppercased()]).joined()
    }

    func registerGlobalHotKey() {
        var eventType = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: OSType(kEventHotKeyPressed))
        InstallEventHandler(GetApplicationEventTarget(), hotKeyEventHandler, 1, &eventType, nil, nil)

        guard let keyCode = macVirtualKeycodes[config.toggleHotkey.key.lowercased()] else {
            NSLog("MonitorSwitcher: unrecognized hotkey letter '\(config.toggleHotkey.key)' in config.json — hotkey disabled")
            return
        }
        var modifierMask: UInt32 = 0
        for m in config.toggleHotkey.modifiers {
            modifierMask |= macModifierFlags[m.lowercased()] ?? 0
        }

        let hotKeyID = EventHotKeyID(signature: fourCharCode("mnSW"), id: 1)
        RegisterEventHotKey(keyCode, modifierMask, hotKeyID, GetApplicationEventTarget(), 0, &hotKeyRefToggle)
    }

    @objc func switchToDisplayPort() { runM1DDC(input: config.displayPortCode) }
    @objc func switchToHDMI()        { runM1DDC(input: config.hdmiCode) }

    @objc func toggleInput() {
        let target = (currentInput == config.displayPortCode) ? config.hdmiCode : config.displayPortCode
        runM1DDC(input: target)
    }

    @objc func revealConfig() {
        NSWorkspace.shared.activateFileViewerSelecting([configFileURL()])
    }

    func runM1DDC(input: Int) {
        let task = Process()
        task.executableURL = URL(fileURLWithPath: M1DDC_PATH)
        task.arguments = ["display", "\(config.displayNumber)", "set", "input", "\(input)"]
        do {
            try task.run()
            task.waitUntilExit()
            if task.terminationStatus == 0 {
                currentInput = input
                updateCheckmarks()
                playFeedback(success: true, input: input)
            } else {
                playFeedback(success: false, input: input)
            }
        } catch {
            let alert = NSAlert()
            alert.messageText = "Couldn't run m1ddc"
            alert.informativeText = "\(error)\n\nCheck that m1ddc is installed at \(M1DDC_PATH)."
            alert.runModal()
        }
    }

    func playFeedback(success: Bool, input: Int) {
        NSSound(named: success ? "Pop" : "Basso")?.play()
        let label = (input == config.displayPortCode) ? "DP" : (input == config.hdmiCode ? "HDMI" : "\(input)")
        statusItem.button?.title = success ? "✓ \(label)" : "✗"
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.2) { [weak self] in
            self?.statusItem.button?.title = "⇄"
        }
    }
}

let app = NSApplication.shared
let delegate = AppDelegate()
app.delegate = delegate
app.run()
