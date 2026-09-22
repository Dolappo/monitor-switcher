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
// file to change inputs, brightness/contrast presets, or hotkeys — no
// rebuild required. Values must match the Mac side's config (see
// macos/Sources/MonitorSwitcher/main.swift).
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
    public HotkeyConfig NightModeHotkey { get; set; } = new HotkeyConfig { Modifiers = new[] { "ctrl", "alt" }, Key = "n" };
    public int DefaultLuminance { get; set; } = 75;
    public int NightModeLuminance { get; set; } = 15;
    public int LuminanceStep { get; set; } = 10;
    public int DefaultContrast { get; set; } = 75;
    public int ContrastStep { get; set; } = 10;

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

// ----------------------------------------------------------------------------
// App-tracked "last known state" — separate from the user-editable config.
// Windows' DDC API can technically read VCP values back, but many monitors
// (including the one this app was built for) respond unreliably to "get" for
// input/luminance/contrast. So exactly like the Mac side, we only ever WRITE
// values via SetVCPFeature and remember what we last set here. It self-
// corrects the next time you switch/adjust from this app.
// ----------------------------------------------------------------------------

internal sealed class StateData
{
    public int? LastInput { get; set; }
    public int? CurrentLuminance { get; set; }
    public int? CurrentContrast { get; set; }
    public bool IsNightMode { get; set; }
    public int? PreNightModeLuminance { get; set; }
}

internal static class AppState
{
    private static string StateDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MonitorSwitcher");

    private static string StatePath => Path.Combine(StateDir, "state.json");

    public static StateData Load()
    {
        try
        {
            if (File.Exists(StatePath))
            {
                var json = File.ReadAllText(StatePath);
                var loaded = JsonSerializer.Deserialize<StateData>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (loaded != null) return loaded;
            }
        }
        catch
        {
            // ignore, treated as unknown/defaults
        }
        return new StateData();
    }

    public static void Save(StateData state)
    {
        try
        {
            Directory.CreateDirectory(StateDir);
            File.WriteAllText(StatePath, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
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
    private const int HOTKEY_ID_NIGHTMODE = 2;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;
    private const int WM_HOTKEY = 0x0312;

    private const byte VCP_INPUT = 0x60;
    private const byte VCP_LUMINANCE = 0x10;
    private const byte VCP_CONTRAST = 0x12;

    private readonly AppConfig _config;
    private readonly NotifyIcon _trayIcon;
    private readonly ToolStripMenuItem _displayPortItem;
    private readonly ToolStripMenuItem _hdmiItem;
    private readonly ToolStripMenuItem _nightModeItem;
    private readonly List<PHYSICAL_MONITOR> _openMonitors = new();
    private readonly HotkeyWindow _hotkeyWindow;

    private byte? _currentInput;
    private int _currentLuminance;
    private int _currentContrast;
    private bool _isNightMode;
    private int _preNightModeLuminance;

    public TrayAppContext()
    {
        _config = AppConfig.Load();

        var state = AppState.Load();
        _currentInput = state.LastInput.HasValue ? (byte)state.LastInput.Value : (byte?)null;
        _currentLuminance = state.CurrentLuminance ?? _config.DefaultLuminance;
        _currentContrast = state.CurrentContrast ?? _config.DefaultContrast;
        _isNightMode = state.IsNightMode;
        _preNightModeLuminance = state.PreNightModeLuminance ?? _config.DefaultLuminance;

        _displayPortItem = new ToolStripMenuItem("Bring monitor to this Surface (DisplayPort)", null,
            (_, _) => SwitchInput(_config.DisplayPortInputCode));
        _hdmiItem = new ToolStripMenuItem("Send monitor to the Mac (HDMI)", null,
            (_, _) => SwitchInput(_config.HdmiInputCode));
        _nightModeItem = new ToolStripMenuItem($"Night Mode ({HotkeyDisplayString(_config.NightModeHotkey)})", null,
            (_, _) => ToggleNightMode());

        var menu = new ContextMenuStrip();
        menu.Items.Add(_displayPortItem);
        menu.Items.Add(_hdmiItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem($"Toggle: {HotkeyDisplayString(_config.ToggleHotkey)}") { Enabled = false });
        menu.Items.Add(new ToolStripMenuItem("Open Config Folder", null, (_, _) => OpenConfigFolder()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Brightness Up", null, (_, _) => BrightnessUp()));
        menu.Items.Add(new ToolStripMenuItem("Brightness Down", null, (_, _) => BrightnessDown()));
        menu.Items.Add(_nightModeItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Contrast Up", null, (_, _) => ContrastUp()));
        menu.Items.Add(new ToolStripMenuItem("Contrast Down", null, (_, _) => ContrastDown()));
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
            else if (id == HOTKEY_ID_NIGHTMODE) ToggleNightMode();
        };
        RegisterHotkey(_config.ToggleHotkey, HOTKEY_ID_TOGGLE);
        RegisterHotkey(_config.NightModeHotkey, HOTKEY_ID_NIGHTMODE);
    }

    private string HotkeyDisplayString(HotkeyConfig hk)
    {
        var parts = hk.Modifiers.Select(m => m.ToLowerInvariant() switch
        {
            "ctrl" or "control" => "Ctrl",
            "alt" => "Alt",
            "shift" => "Shift",
            "win" or "windows" => "Win",
            _ => m
        }).ToList();
        parts.Add(hk.Key.ToUpperInvariant());
        return string.Join("+", parts);
    }

    private void RegisterHotkey(HotkeyConfig hk, int id)
    {
        uint mods = MOD_NOREPEAT;
        foreach (var m in hk.Modifiers)
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
        if (string.IsNullOrEmpty(hk.Key)) return;
        uint vk = char.ToUpperInvariant(hk.Key[0]);
        RegisterHotKey(_hotkeyWindow.Handle, id, mods, vk);
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
        _nightModeItem.Checked = _isNightMode;
    }

    private void OpenConfigFolder()
    {
        var dir = Path.GetDirectoryName(AppConfig.ConfigFilePath)!;
        Directory.CreateDirectory(dir);
        System.Diagnostics.Process.Start("explorer.exe", dir);
    }

    private void PersistState()
    {
        AppState.Save(new StateData
        {
            LastInput = _currentInput,
            CurrentLuminance = _currentLuminance,
            CurrentContrast = _currentContrast,
            IsNightMode = _isNightMode,
            PreNightModeLuminance = _preNightModeLuminance
        });
    }

    private void SwitchInput(byte inputCode)
    {
        bool ok = SetVcpFeature(VCP_INPUT, inputCode);
        if (ok)
        {
            _currentInput = inputCode;
            PersistState();
            UpdateChecks();
            var label = inputCode == _config.DisplayPortInputCode ? "DisplayPort"
                : (inputCode == _config.HdmiInputCode ? "HDMI" : $"input {inputCode}");
            PlayFeedback(success: true, label: $"Switched to {label}");
        }
        else
        {
            PlayFeedback(success: false,
                label: "No monitor responded to the DDC/CI command. Check that DDC/CI is enabled in the monitor's OSD menu.");
        }
    }

    private void BrightnessUp()
    {
        int value = Math.Min(100, _currentLuminance + _config.LuminanceStep);
        if (SetVcpFeature(VCP_LUMINANCE, (uint)value))
        {
            _currentLuminance = value;
            PersistState();
            PlayFeedback(success: true, label: $"Brightness {value}%");
        }
        else
        {
            PlayFeedback(success: false, label: "Couldn't change brightness.");
        }
    }

    private void BrightnessDown()
    {
        int value = Math.Max(0, _currentLuminance - _config.LuminanceStep);
        if (SetVcpFeature(VCP_LUMINANCE, (uint)value))
        {
            _currentLuminance = value;
            PersistState();
            PlayFeedback(success: true, label: $"Brightness {value}%");
        }
        else
        {
            PlayFeedback(success: false, label: "Couldn't change brightness.");
        }
    }

    private void ContrastUp()
    {
        int value = Math.Min(100, _currentContrast + _config.ContrastStep);
        if (SetVcpFeature(VCP_CONTRAST, (uint)value))
        {
            _currentContrast = value;
            PersistState();
            PlayFeedback(success: true, label: $"Contrast {value}");
        }
        else
        {
            PlayFeedback(success: false, label: "Couldn't change contrast.");
        }
    }

    private void ContrastDown()
    {
        int value = Math.Max(0, _currentContrast - _config.ContrastStep);
        if (SetVcpFeature(VCP_CONTRAST, (uint)value))
        {
            _currentContrast = value;
            PersistState();
            PlayFeedback(success: true, label: $"Contrast {value}");
        }
        else
        {
            PlayFeedback(success: false, label: "Couldn't change contrast.");
        }
    }

    private void ToggleNightMode()
    {
        if (_isNightMode)
        {
            int restore = _preNightModeLuminance;
            if (SetVcpFeature(VCP_LUMINANCE, (uint)restore))
            {
                _currentLuminance = restore;
                _isNightMode = false;
                PersistState();
                UpdateChecks();
                PlayFeedback(success: true, label: $"Brightness {restore}%");
            }
            else
            {
                PlayFeedback(success: false, label: "Couldn't leave Night Mode.");
            }
        }
        else
        {
            int toRestore = _currentLuminance;
            if (SetVcpFeature(VCP_LUMINANCE, (uint)_config.NightModeLuminance))
            {
                _preNightModeLuminance = toRestore;
                _currentLuminance = _config.NightModeLuminance;
                _isNightMode = true;
                PersistState();
                UpdateChecks();
                PlayFeedback(success: true, label: "Night Mode");
            }
            else
            {
                PlayFeedback(success: false, label: "Couldn't enter Night Mode.");
            }
        }
    }

    private bool SetVcpFeature(byte vcpCode, uint value)
    {
        bool anySucceeded = false;
        try
        {
            foreach (var handle in GetPhysicalMonitorHandles())
            {
                if (Dxva2.SetVCPFeature(handle, vcpCode, value))
                {
                    anySucceeded = true;
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't send the DDC/CI command:\n{ex.Message}", "Monitor Switcher",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            ReleaseAllPhysicalMonitors();
        }
        return anySucceeded;
    }

    private void PlayFeedback(bool success, string label)
    {
        if (success)
        {
            System.Media.SystemSounds.Asterisk.Play();
            _trayIcon.ShowBalloonTip(1500, "Monitor Switcher", label, ToolTipIcon.None);
        }
        else
        {
            System.Media.SystemSounds.Hand.Play();
            _trayIcon.ShowBalloonTip(3000, "Monitor Switcher", label, ToolTipIcon.Warning);
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
