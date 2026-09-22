import Cocoa
import Carbon.HIToolbox

// ---- CONFIG: adjust after running `m1ddc display list` and testing `m1ddc display N set input X` ----
let DISPLAY_NUMBER          = 2    // confirmed via `m1ddc display list`
let DISPLAYPORT_INPUT_CODE  = 15   // confirmed: VESA default for DisplayPort-1 (0x0F)
let HDMI_INPUT_CODE         = 17   // confirmed: VESA default for HDMI-1      (0x11)
let M1DDC_PATH               = "/opt/homebrew/bin/m1ddc"

// Global hotkeys: Cmd+Option+D = send to Surface (DisplayPort), Cmd+Option+H = bring to Mac (HDMI)
let HOTKEY_DISPLAYPORT_KEYCODE = UInt32(kVK_ANSI_D)
let HOTKEY_HDMI_KEYCODE        = UInt32(kVK_ANSI_H)
let HOTKEY_MODIFIERS           = UInt32(cmdKey | optionKey)
// -----------------------------------------------------------------------------------------

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
    case 1: AppDelegate.shared?.switchToDisplayPort()
    case 2: AppDelegate.shared?.switchToHDMI()
    default: break
    }
    return noErr
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    static var shared: AppDelegate?

    var statusItem: NSStatusItem!
    var hotKeyRefDisplayPort: EventHotKeyRef?
    var hotKeyRefHDMI: EventHotKeyRef?

    func applicationDidFinishLaunching(_ notification: Notification) {
        AppDelegate.shared = self
        NSApp.setActivationPolicy(.accessory) // menu bar only, no Dock icon, no bundle/plist needed

        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        statusItem.button?.title = "⇄"

        let menu = NSMenu()
        menu.addItem(NSMenuItem(title: "Send monitor to Surface (⌘⌥D)", action: #selector(switchToDisplayPort), keyEquivalent: ""))
        menu.addItem(NSMenuItem(title: "Bring monitor to this Mac (⌘⌥H)", action: #selector(switchToHDMI), keyEquivalent: ""))
        menu.addItem(NSMenuItem.separator())
        menu.addItem(NSMenuItem(title: "Quit", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q"))
        statusItem.menu = menu

        registerGlobalHotKeys()
    }

    func registerGlobalHotKeys() {
        var eventType = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: OSType(kEventHotKeyPressed))
        InstallEventHandler(GetApplicationEventTarget(), hotKeyEventHandler, 1, &eventType, nil, nil)

        let idDisplayPort = EventHotKeyID(signature: fourCharCode("mnDP"), id: 1)
        RegisterEventHotKey(HOTKEY_DISPLAYPORT_KEYCODE, HOTKEY_MODIFIERS, idDisplayPort, GetApplicationEventTarget(), 0, &hotKeyRefDisplayPort)

        let idHDMI = EventHotKeyID(signature: fourCharCode("mnHD"), id: 2)
        RegisterEventHotKey(HOTKEY_HDMI_KEYCODE, HOTKEY_MODIFIERS, idHDMI, GetApplicationEventTarget(), 0, &hotKeyRefHDMI)
    }

    @objc func switchToDisplayPort() { runM1DDC(input: DISPLAYPORT_INPUT_CODE) }
    @objc func switchToHDMI()        { runM1DDC(input: HDMI_INPUT_CODE) }

    func runM1DDC(input: Int) {
        let task = Process()
        task.executableURL = URL(fileURLWithPath: M1DDC_PATH)
        task.arguments = ["display", "\(DISPLAY_NUMBER)", "set", "input", "\(input)"]
        do {
            try task.run()
        } catch {
            let alert = NSAlert()
            alert.messageText = "Couldn't run m1ddc"
            alert.informativeText = "\(error)\n\nCheck that m1ddc is installed at \(M1DDC_PATH)."
            alert.runModal()
        }
    }
}

let app = NSApplication.shared
let delegate = AppDelegate()
app.delegate = delegate
app.run()
