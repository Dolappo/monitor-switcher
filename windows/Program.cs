using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MonitorSwitcher;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayAppContext());
    }
}

// ============================================================================
// Config: loaded from %AppData%\MonitorSwitcher\config.json (created
// automatically on first run with these defaults if missing). Edit that
// file to change inputs or the toggle hotkey — no rebuild required. Values
// must match the Mac side's config (see macos/Sources/MonitorSwitcher/main.swift).
// ============================================================================

internal sealed class HotkeyConfig
{
    public string[] Modifiers { get; set; } = new[] { "ctrl", "alt" };
    public string Key { get; set; } = "s";
}

internal sealed class AppConfig
{
    public byte DisplayPortInputCode { get; set; } = 0x0F; // 15 decimal (VESA default: DisplayPort-1)
    public byte HdmiInputCode { get; set; } = 0x11;         // 17 decimal (VESA default: HDMI-1)
    public HotkeyConfig ToggleHotkey { get; set; } = new HotkeyConfig();

    private static string ConfigDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MonitorSwitcher");

    private static string ConfigPath => Path.Combine(ConfigDir, "config.json");

    public static string ConfigFilePath => ConfigPath;

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var loaded = JsonSerializer.Deserialize<AppConfig>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (loaded != null) return loaded;
            }
        }
        catch
        {
            // fall through to defaults
        }

        var defaults = new AppConfig();
        try
        {
            Directory.CreateDirectory(ConfigDir);
            var json = JsonSerializer.Serialize(defaults, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigPath, json);
        }
        catch
        {
            // ignore write failure, still usable with in-memory defaults
        }
        return defaults;
    }
}

internal static class AppState
{
    private static string StateDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MonitorSwitcher");

    private static string StatePath => Path.Combine(StateDir, "state.json");

    public static byte? LoadLastInput()
    {
        try
        {
            if (File.Exists(StatePath))
            {
                var json = File.ReadAllText(StatePath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("lastInput", out var el))
                {
                    return (byte)el.GetInt32();
                }
            }
        }
        catch
        {
            // ignore, treated as unknown
        }
        return null;
    }

    public static void SaveLastInput(byte input)
    {
        try
        {
            Directory.CreateDirectory(StateDir);
            File.WriteAllText(StatePath, JsonSerializer.Serialize(new { lastInput = (int)input }));
        }
        catch
        {
            // best-effort only
        }
    }
}

internal sealed class TrayAppContext : ApplicationContext
{
    private const int HOTKEY_ID_TOGGLE = 1;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;
    private const int WM_HOTKEY = 0x0312;

    private readonly AppConfig _config;
    private readonly NotifyIcon _trayIcon;
    private readonly ToolStripMenuItem _displayPortItem;
    private readonly ToolStripMenuItem _hdmiItem;
    private readonly List<PHYSICAL_MONITOR> _openMonitors = new();
    private readonly HotkeyWindow _hotkeyWindow;

    // Best-effort tracked state: Windows' DDC API can technically read VCP
    // values, but many monitors reply unreliably to "get" for the input-source
    // feature specifically. We remember the last input THIS app switched to,
    // matching the same approach as the Mac side. It self-corrects the next
    // time you switch from here.
    private byte? _currentInput;

    public TrayAppContext()
    {
        _config = AppConfig.Load();
        _currentInput = AppState.LoadLastInput();

        _displayPortItem = new ToolStripMenuItem("Bring monitor to this Surface (DisplayPort)", null,
            (_, _) => SwitchInput(_config.DisplayPortInputCode));
        _hdmiItem = new ToolStripMenuItem("Send monitor to the Mac (HDMI)", null,
            (_, _) => SwitchInput(_config.HdmiInputCode));

        var menu = new ContextMenuStrip();
        menu.Items.Add(_displayPortItem);
        menu.Items.Add(_hdmiItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem($"Toggle: {HotkeyDisplayString()}") { Enabled = false });
        menu.Items.Add(new ToolStripMenuItem("Open Config Folder", null, (_, _) => OpenConfigFolder()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = true,
            Text = "Monitor Switcher"
        };
        _trayIcon.DoubleClick += (_, _) => ToggleInput();

        UpdateChecks();

        _hotkeyWindow = new HotkeyWindow();
        _hotkeyWindow.HotKeyPressed += id =>
        {
            if (id == HOTKEY_ID_TOGGLE) ToggleInput();
        };
        RegisterToggleHotkey();
    }

    private string HotkeyDisplayString()
    {
        var parts = _config.ToggleHotkey.Modifiers.Select(m => m.ToLowerInvariant() switch
        {
            "ctrl" or "control" => "Ctrl",
            "alt" => "Alt",
            "shift" => "Shift",
            "win" or "windows" => "Win",
            _ => m
        }).ToList();
        parts.Add(_config.ToggleHotkey.Key.ToUpperInvariant());
        return string.Join("+", parts);
    }

    private void RegisterToggleHotkey()
    {
        uint mods = MOD_NOREPEAT;
        foreach (var m in _config.ToggleHotkey.Modifiers)
        {
            mods |= m.ToLowerInvariant() switch
            {
                "ctrl" or "control" => MOD_CONTROL,
                "alt" => MOD_ALT,
                "shift" => MOD_SHIFT,
                "win" or "windows" => MOD_WIN,
                _ => 0u
            };
        }
        if (string.IsNullOrEmpty(_config.ToggleHotkey.Key)) return;
        uint vk = char.ToUpperInvariant(_config.ToggleHotkey.Key[0]);
        RegisterHotKey(_hotkeyWindow.Handle, HOTKEY_ID_TOGGLE, mods, vk);
    }

    private void ToggleInput()
    {
        byte target = (_currentInput == _config.DisplayPortInputCode) ? _config.HdmiInputCode : _config.DisplayPortInputCode;
        SwitchInput(target);
    }

    private void UpdateChecks()
    {
        _displayPortItem.Checked = _currentInput == _config.DisplayPortInputCode;
        _hdmiItem.Checked = _currentInput == _config.HdmiInputCode;
    }

    private void OpenConfigFolder()
    {
        var dir = Path.GetDirectoryName(AppConfig.ConfigFilePath)!;
        Directory.CreateDirectory(dir);
        System.Diagnostics.Process.Start("explorer.exe", dir);
    }

    private void SwitchInput(byte inputCode)
    {
        bool anySucceeded = false;
        try
        {
            foreach (var handle in GetPhysicalMonitorHandles())
            {
                if (Dxva2.SetVCPFeature(handle, 0x60, inputCode))
                {
                    anySucceeded = true;
                }
            }

            if (anySucceeded)
            {
                _currentInput = inputCode;
                AppState.SaveLastInput(inputCode);
                UpdateChecks();
                System.Media.SystemSounds.Asterisk.Play();
                var label = inputCode == _config.DisplayPortInputCode ? "DisplayPort"
                    : (inputCode == _config.HdmiInputCode ? "HDMI" : $"input {inputCode}");
                _trayIcon.ShowBalloonTip(1500, "Monitor Switcher", $"Switched to {label}", ToolTipIcon.None);
            }
            else
            {
                System.Media.SystemSounds.Hand.Play();
                _trayIcon.ShowBalloonTip(3000, "Monitor Switcher",
                    "No monitor responded to the DDC/CI command. Check that DDC/CI is enabled in the monitor's OSD menu.",
                    ToolTipIcon.Warning);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't switch input:\n{ex.Message}", "Monitor Switcher",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            ReleaseAllPhysicalMonitors();
        }
    }

    private List<IntPtr> GetPhysicalMonitorHandles()
    {
        var handles = new List<IntPtr>();

        User32.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data) =>
            {
                if (Dxva2.GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out uint count) && count > 0)
                {
                    var monitors = new PHYSICAL_MONITOR[count];
                    if (Dxva2.GetPhysicalMonitorsFromHMONITOR(hMonitor, count, monitors))
                    {
                        foreach (var m in monitors)
                        {
                            handles.Add(m.hPhysicalMonitor);
                            _openMonitors.Add(m);
                        }
                    }
                }
                return true;
            }, IntPtr.Zero);

        return handles;
    }

    private void ReleaseAllPhysicalMonitors()
    {
        if (_openMonitors.Count > 0)
        {
            Dxva2.DestroyPhysicalMonitors((uint)_openMonitors.Count, _openMonitors.ToArray());
            _openMonitors.Clear();
        }
    }

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private sealed class HotkeyWindow : NativeWindow
    {
        public event Action<int>? HotKeyPressed;

        public HotkeyWindow()
        {
            CreateHandle(new CreateParams());
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY)
            {
                HotKeyPressed?.Invoke(m.WParam.ToInt32());
            }
            base.WndProc(m);
        }
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct RECT
{
    public int Left, Top, Right, Bottom;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct PHYSICAL_MONITOR
{
    public IntPtr hPhysicalMonitor;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
    public string szPhysicalMonitorDescription;
}

internal static class User32
{
    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);
}

internal static class Dxva2
{
    [DllImport("dxva2.dll")]
    public static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint pdwNumberOfPhysicalMonitors);

    [DllImport("dxva2.dll", CharSet = CharSet.Unicode)]
    public static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint dwPhysicalMonitorArraySize,
        [Out] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

    [DllImport("dxva2.dll")]
    public static extern bool SetVCPFeature(IntPtr hMonitor, byte bVCPCode, uint dwNewValue);

    [DllImport("dxva2.dll", CharSet = CharSet.Unicode)]
    public static extern bool DestroyPhysicalMonitors(uint dwPhysicalMonitorArraySize,
        [In] PHYSICAL_MONITOR[] pPhysicalMonitorArray);
}
