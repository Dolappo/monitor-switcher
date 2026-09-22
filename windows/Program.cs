using System.Runtime.InteropServices;

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

// ---- CONFIG: these must match the Mac side. Confirm the real codes with
//      `m1ddc display list` / `m1ddc display N set input X` on the Mac, then set both here
//      AND in macos/Sources/MonitorSwitcher/main.swift. ----
internal static class Config
{
    public const byte DisplayPortInputCode = 0x0F; // 15 decimal (VESA default: DisplayPort-1)
    public const byte HdmiInputCode = 0x11;         // 17 decimal (VESA default: HDMI-1)
}

internal sealed class TrayAppContext : ApplicationContext
{
    private readonly NotifyIcon _trayIcon;
    private readonly List<PHYSICAL_MONITOR> _openMonitors = new();

    public TrayAppContext()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Bring monitor to this Surface (DisplayPort)", null, (_, _) => SwitchInput(Config.DisplayPortInputCode));
        menu.Items.Add("Send monitor to the Mac (HDMI)", null, (_, _) => SwitchInput(Config.HdmiInputCode));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = true,
            Text = "Monitor Switcher"
        };
        _trayIcon.DoubleClick += (_, _) => SwitchInput(Config.DisplayPortInputCode);
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

            if (!anySucceeded)
            {
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
