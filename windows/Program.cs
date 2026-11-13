using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

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
// file to change inputs, brightness/contrast presets, mouse switching, or
// hotkeys — no rebuild required. Any field you omit keeps its C# default
// (System.Text.Json only overwrites properties actually present in the
// file), so older config files never break here. Values must match the
// Mac side's config (see macos/Sources/MonitorSwitcher/main.swift).
// ============================================================================

internal sealed class HotkeyConfig
{
    public string[] Modifiers { get; set; } = new[] { "ctrl", "alt" };
    public string Key { get; set; } = "s";
}

/// One of the two computers sharing the monitor. InputCode is the VCP 0x60
/// value of the monitor input that computer is plugged into (VESA defaults:
/// 15 = DisplayPort-1, 16 = DisplayPort-2, 17 = HDMI-1, 18 = HDMI-2,
/// 27 = USB-C on many monitors). MouseChannel is that computer's channel on
/// a multi-host mouse (only used when MouseSwitchEnabled is true).
internal sealed class MachineConfig
{
    public string Name { get; set; } = "";
    public int InputCode { get; set; }
    public int MouseChannel { get; set; } = 1;
}

internal sealed class AppConfig
{
    /// Exactly two machines. The same list (same order) can be used on both
    /// computers; only ThisMachine differs.
    public List<MachineConfig>? Machines { get; set; }
    /// Index into Machines of the computer this copy of the app runs on.
    public int? ThisMachine { get; set; }

    // Legacy (pre-"machines") keys — read for migration, never written back.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? DisplayPortInputCode { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? HdmiInputCode { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? MouseDisplayPortChannel { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? MouseHdmiChannel { get; set; }

    public HotkeyConfig ToggleHotkey { get; set; } = new HotkeyConfig();
    public HotkeyConfig NightModeHotkey { get; set; } = new HotkeyConfig { Modifiers = new[] { "ctrl", "alt" }, Key = "n" };
    public int DefaultLuminance { get; set; } = 75;
    public int NightModeLuminance { get; set; } = 15;
    public int LuminanceStep { get; set; } = 10;
    public int DefaultContrast { get; set; } = 75;
    public int ContrastStep { get; set; } = 10;

    // Optional companion switch for a Logitech multi-host mouse using the
    // reverse-engineered HID++ "CHANGE_HOST" feature, via the mx-host-switch
    // CLI (https://github.com/antonio-castellon/mx-host-switch). Off by
    // default — Logitech Flow or the mouse's own button can do this instead.
    // MouseSwitchPath
    // has no sensible universal default on Windows (the tool ships as a
    // portable .exe, not something with a fixed install location) — update
    // it to wherever you put it.
    public bool MouseSwitchEnabled { get; set; } = false;
    public string MouseSwitchPath { get; set; } = @"C:\Tools\MXHostSwitch\MXHostSwitch.exe";

    [JsonIgnore] public int LocalIndex => ThisMachine ?? 1;
    [JsonIgnore] public int RemoteIndex => 1 - LocalIndex;
    [JsonIgnore] public MachineConfig Local => Machines![LocalIndex];
    [JsonIgnore] public MachineConfig Remote => Machines![RemoteIndex];

    /// Fills in the two-machine layout. Configs from before "machines"
    /// existed are migrated: DisplayPort → "Surface" (this PC), HDMI → "Mac".
    /// The order matches the Mac side (Mac first), so the same machines list
    /// can be pasted into both configs; only ThisMachine differs.
    private void Normalize()
    {
        if (Machines == null || Machines.Count != 2)
        {
            Machines = new List<MachineConfig>
            {
                new() { Name = "Mac", InputCode = HdmiInputCode ?? 17, MouseChannel = MouseHdmiChannel ?? 2 },
                new() { Name = "Surface", InputCode = DisplayPortInputCode ?? 15, MouseChannel = MouseDisplayPortChannel ?? 1 },
            };
            ThisMachine = 1;
        }
        if (ThisMachine is not (0 or 1)) ThisMachine = 1;
        DisplayPortInputCode = HdmiInputCode = MouseDisplayPortChannel = MouseHdmiChannel = null;
    }

    private static readonly JsonSerializerOptions SaveOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase // same key names as the Mac config
    };

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
                if (loaded != null)
                {
                    loaded.Normalize();
                    return loaded;
                }
            }
        }
        catch
        {
            // fall through to defaults
        }

        var defaults = new AppConfig();
        defaults.Normalize();
        defaults.Save();
        return defaults;
    }

    /// Persists this config instance back to config.json — used by mouse
    /// channel calibration so a recalibrated value survives a restart.
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            var json = JsonSerializer.Serialize(this, SaveOptions);
            File.WriteAllText(ConfigPath, json);
        }
        catch
        {
            // best-effort only
        }
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
    /// One "switch to" item per machine, indexed like _config.Machines.
    private readonly ToolStripMenuItem[] _machineItems;
    private readonly ToolStripMenuItem _nightModeItem;
    private readonly ToolStripMenuItem _mouseChannelLabelItem;
    private readonly HotkeyWindow _hotkeyWindow;

    private int? _currentInput;

    // Display-presence tracking: when the shared monitor drops out of this
    // PC's display list the other machine has it; when it comes back, this
    // PC does. Keeps the tray state right even when the switch was made from
    // the other computer.
    private int _lastScreenCount;
    private readonly System.Windows.Forms.Timer _displayCheckTimer = new() { Interval = 1000 };
    private SynchronizationContext? _uiContext;
    private int _currentLuminance;
    private int _currentContrast;
    private bool _isNightMode;
    private int _preNightModeLuminance;

    public TrayAppContext()
    {
        _config = AppConfig.Load();

        var state = AppState.Load();
        _currentInput = state.LastInput;
        _currentLuminance = state.CurrentLuminance ?? _config.DefaultLuminance;
        _currentContrast = state.CurrentContrast ?? _config.DefaultContrast;
        _isNightMode = state.IsNightMode;
        _preNightModeLuminance = state.PreNightModeLuminance ?? _config.DefaultLuminance;

        _machineItems = _config.Machines!.Select((m, i) =>
        {
            int index = i;
            string title = index == _config.LocalIndex ? $"Bring monitor here ({m.Name})" : $"Show monitor on {m.Name}";
            return new ToolStripMenuItem(title, null, (_, _) => SwitchInput(index));
        }).ToArray();
        _nightModeItem = new ToolStripMenuItem($"Night Mode ({HotkeyDisplayString(_config.NightModeHotkey)})", null,
            (_, _) => ToggleNightMode());
        _mouseChannelLabelItem = new ToolStripMenuItem("Mouse channel: —") { Enabled = false };

        var menu = new ContextMenuStrip();
        menu.Items.Add(_machineItems[_config.LocalIndex]);
        menu.Items.Add(_machineItems[_config.RemoteIndex]);
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
        menu.Items.Add(_mouseChannelLabelItem);
        foreach (int i in new[] { _config.LocalIndex, _config.RemoteIndex })
        {
            var machine = _config.Machines![i];
            menu.Items.Add(new ToolStripMenuItem($"Set Current Channel as {machine.Name}'s Mouse", null,
                async (_, _) => await CalibrateMouseChannel(v => machine.MouseChannel = v, machine.Name)));
        }
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        // Live-refreshes the mouse channel label every time the menu opens —
        // a genuine hardware read (unlike the monitor's tracked state), so
        // it's worth re-querying rather than caching. Runs off the UI thread
        // so opening the menu is never blocked on the subprocess call.
        menu.Opening += (_, _) => RefreshMouseChannelLabel();

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

        _uiContext = SynchronizationContext.Current;
        _lastScreenCount = Screen.AllScreens.Length;
        _displayCheckTimer.Tick += (_, _) => { _displayCheckTimer.Stop(); CheckDisplayPresence(); };
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        // Can arrive on a SystemEvents thread; hop to the UI thread, then
        // debounce — displays flicker in and out while a monitor re-syncs.
        void Restart() { _displayCheckTimer.Stop(); _displayCheckTimer.Start(); }
        if (_uiContext != null) _uiContext.Post(_ => Restart(), null);
        else Restart();
    }

    private void CheckDisplayPresence()
    {
        int count = Screen.AllScreens.Length;
        if (count == _lastScreenCount) return;
        bool gained = count > _lastScreenCount;
        _lastScreenCount = count;
        if (_pendingInput != null) return; // our own switch is in flight; its result sets the state
        int newInput = gained ? _config.Local.InputCode : _config.Remote.InputCode;
        if (newInput == _currentInput) return;
        _currentInput = newInput;
        PersistState();
        UpdateChecks();
    }

    protected override void ExitThreadCore()
    {
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _displayCheckTimer.Dispose();
        _trayIcon.Visible = false;
        base.ExitThreadCore();
    }

    private string MachineLabel(int inputCode) =>
        _config.Machines!.FirstOrDefault(m => m.InputCode == inputCode)?.Name ?? $"input {inputCode}";

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

    /// Input we've just asked the monitor to switch to but SetVCPFeature
    /// hasn't confirmed yet. The toggle hotkey reads this first, so pressing
    /// it twice quickly flips back instead of re-sending the same input.
    private int? _pendingInput;

    // DDC/CI calls run on background threads; this keeps two of them from
    // talking to the monitor at the same time.
    private readonly object _ddcLock = new();

    private void ToggleInput()
    {
        int? effective = _pendingInput ?? _currentInput;
        // Unknown state counts as "here", so the first press sends it away.
        int target = (effective == _config.Remote.InputCode) ? _config.LocalIndex : _config.RemoteIndex;
        SwitchInput(target);
    }

    private void UpdateChecks()
    {
        for (int i = 0; i < _machineItems.Length; i++)
            _machineItems[i].Checked = _currentInput == _config.Machines![i].InputCode;
        // Tray tooltip shows who has the monitor (max 63 chars).
        string text = _currentInput.HasValue ? $"Monitor Switcher — on {MachineLabel(_currentInput.Value)}" : "Monitor Switcher";
        _trayIcon.Text = text.Length > 63 ? text[..63] : text;
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

    private void SwitchInput(int machineIndex)
    {
        var machine = _config.Machines![machineIndex];
        int inputCode = machine.InputCode;
        _pendingInput = inputCode;
        RunVcpSet(VCP_INPUT, (uint)inputCode, $"Monitor → {machine.Name}",
            "No monitor responded to the DDC/CI command. Check that DDC/CI is enabled in the monitor's OSD menu.",
            onSuccess: () =>
            {
                _currentInput = inputCode;
                // Best-effort companion mouse switch, only after the monitor
                // switch itself succeeded — never move the mouse to a machine
                // that doesn't have the picture.
                SwitchMouseChannel(machine.MouseChannel);
            },
            onComplete: () => { if (_pendingInput == inputCode) _pendingInput = null; });
    }

    /// Sends one VCP write without ever blocking the UI thread. Feedback is
    /// optimistic: the sound and balloon fire the instant the action is
    /// triggered, because DDC/CI writes can take a second or more to return
    /// while the monitor re-syncs. App state (onSuccess, PersistState,
    /// UpdateChecks) is only updated once the write is confirmed, back on
    /// the UI thread; on failure the failure feedback replaces the success.
    private async void RunVcpSet(byte vcpCode, uint value, string successLabel, string failureLabel,
        Action onSuccess, Action? onComplete = null)
    {
        PlayFeedback(success: true, label: successLabel);
        bool ok = await Task.Run(() => SetVcpFeature(vcpCode, value));
        try
        {
            if (ok)
            {
                onSuccess();
                PersistState();
                UpdateChecks();
            }
            else
            {
                PlayFeedback(success: false, label: failureLabel);
            }
        }
        finally
        {
            onComplete?.Invoke();
        }
    }

    /// Shells out to `mx-host-switch --switch N`, entirely on a background
    /// thread (Bluetooth HID++ round-trips aren't instant). Failures are
    /// logged, not surfaced — the monitor switch already got its feedback.
    private void SwitchMouseChannel(int channel)
    {
        if (!_config.MouseSwitchEnabled) return;
        string path = _config.MouseSwitchPath;
        _ = Task.Run(() =>
        {
            if (!File.Exists(path))
            {
                System.Diagnostics.Debug.WriteLine($"MonitorSwitcher: mouse switch tool not found at {path} — skipping");
                return;
            }
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = path,
                    Arguments = $"--switch {channel}",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                proc?.WaitForExit(5000);
                if (proc != null && proc.HasExited && proc.ExitCode != 0)
                    System.Diagnostics.Debug.WriteLine($"MonitorSwitcher: mouse switch to channel {channel} exited with {proc.ExitCode}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MonitorSwitcher: couldn't run mouse switch tool: {ex.Message}");
            }
        });
    }

    /// Runs `mx-host-switch --list` and pulls the mouse's *actual current*
    /// channel out of its output (a line containing `current=Channel N/M`).
    /// This is a genuine live read from the mouse, not app-tracked state.
    /// Returns null if switching is disabled, the tool is missing, or
    /// nothing was detected (e.g. the mouse isn't connected to this PC).
    private int? QueryCurrentMouseChannel()
    {
        if (!_config.MouseSwitchEnabled || !File.Exists(_config.MouseSwitchPath)) return null;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = _config.MouseSwitchPath,
                Arguments = "--list",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return null;
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);
            var match = Regex.Match(output, @"current=Channel (\d+)");
            if (match.Success && int.TryParse(match.Groups[1].Value, out int channel)) return channel;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"MonitorSwitcher: couldn't query mouse channel: {ex.Message}");
        }
        return null;
    }

    /// Updates the label without blocking the menu's appearance — shows
    /// "checking…" immediately, then fills in the real value once the
    /// subprocess (running on a background thread) returns.
    private async void RefreshMouseChannelLabel()
    {
        if (!_config.MouseSwitchEnabled)
        {
            _mouseChannelLabelItem.Text = "Mouse switching: disabled in config";
            return;
        }
        _mouseChannelLabelItem.Text = "Mouse channel: checking…";
        int? channel = await Task.Run(() => QueryCurrentMouseChannel());
        _mouseChannelLabelItem.Text = channel.HasValue
            ? $"Mouse channel right now: {channel.Value}"
            : "Mouse channel: not detected";
    }

    /// One-click recalibration: whichever channel the mouse is actually on
    /// right now becomes the saved value for that role, written back to
    /// config.json immediately. Use any time a re-pair shifts which channel
    /// belongs to which computer — switch the mouse to that machine first,
    /// then click the matching item.
    private async Task CalibrateMouseChannel(Action<int> setter, string roleName)
    {
        int? channel = await Task.Run(() => QueryCurrentMouseChannel());
        if (channel == null)
        {
            string reason = _config.MouseSwitchEnabled
                ? "Make sure the mouse is connected to this PC right now, and that MouseSwitchPath in config.json points to a working mx-host-switch executable."
                : "Mouse switching is disabled (MouseSwitchEnabled: false in config.json).";
            MessageBox.Show($"Couldn't read the mouse's current channel.\n\n{reason}", "Monitor Switcher",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        setter(channel.Value);
        _config.Save();
        PlayFeedback(success: true, label: $"{roleName} mouse = Ch {channel.Value}");
    }

    private void BrightnessUp() => SetLuminance(Math.Min(100, _currentLuminance + _config.LuminanceStep));

    private void BrightnessDown() => SetLuminance(Math.Max(0, _currentLuminance - _config.LuminanceStep));

    private void SetLuminance(int value) =>
        RunVcpSet(VCP_LUMINANCE, (uint)value, $"Brightness {value}%", "Couldn't change brightness.",
            onSuccess: () => _currentLuminance = value);

    private void ContrastUp() => SetContrast(Math.Min(100, _currentContrast + _config.ContrastStep));

    private void ContrastDown() => SetContrast(Math.Max(0, _currentContrast - _config.ContrastStep));

    private void SetContrast(int value) =>
        RunVcpSet(VCP_CONTRAST, (uint)value, $"Contrast {value}", "Couldn't change contrast.",
            onSuccess: () => _currentContrast = value);

    private void ToggleNightMode()
    {
        if (_isNightMode)
        {
            int restore = _preNightModeLuminance;
            RunVcpSet(VCP_LUMINANCE, (uint)restore, $"Brightness {restore}%", "Couldn't leave Night Mode.",
                onSuccess: () =>
                {
                    _currentLuminance = restore;
                    _isNightMode = false;
                });
        }
        else
        {
            int toRestore = _currentLuminance;
            int night = _config.NightModeLuminance;
            RunVcpSet(VCP_LUMINANCE, (uint)night, "Night Mode", "Couldn't enter Night Mode.",
                onSuccess: () =>
                {
                    _preNightModeLuminance = toRestore;
                    _currentLuminance = night;
                    _isNightMode = true;
                });
        }
    }

    /// Runs on a background thread (see RunVcpSet). Monitor handles are
    /// local to each call and the whole thing is serialized by _ddcLock, so
    /// overlapping actions can't release each other's handles. No dialogs
    /// here — the caller shows failure feedback on the UI thread.
    private bool SetVcpFeature(byte vcpCode, uint value)
    {
        lock (_ddcLock)
        {
            bool anySucceeded = false;
            var monitors = new List<PHYSICAL_MONITOR>();
            try
            {
                foreach (var m in GetPhysicalMonitors(monitors))
                {
                    if (Dxva2.SetVCPFeature(m.hPhysicalMonitor, vcpCode, value))
                    {
                        anySucceeded = true;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MonitorSwitcher: DDC/CI command failed: {ex.Message}");
            }
            finally
            {
                if (monitors.Count > 0)
                    Dxva2.DestroyPhysicalMonitors((uint)monitors.Count, monitors.ToArray());
            }
            return anySucceeded;
        }
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

    private static List<PHYSICAL_MONITOR> GetPhysicalMonitors(List<PHYSICAL_MONITOR> into)
    {
        User32.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data) =>
            {
                if (Dxva2.GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out uint count) && count > 0)
                {
                    var monitors = new PHYSICAL_MONITOR[count];
                    if (Dxva2.GetPhysicalMonitorsFromHMONITOR(hMonitor, count, monitors))
                    {
                        into.AddRange(monitors);
                    }
                }
                return true;
            }, IntPtr.Zero);

        return into;
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
