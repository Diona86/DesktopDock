using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace DesktopDock;

internal sealed partial class DesktopIconService
{
    internal static DesktopIconService Open(string configDirectory, bool allowChanges = true)
    {
        var service = new DesktopIconService(configDirectory, new ExplorerIconView(), allowChanges);
        service.Refresh();
        return service;
    }
}

internal sealed class ExplorerIconView : IDesktopIconBackend
{
    private const int WindowStyle = -16;
    private const uint VisibleStyle = 0x10000000;
    private const uint ShellHideIconsMask = 0x4000;
    private const uint HideIconsBit = 1 << 12; // SHELLFLAGSTATE.fHideIcons in the SDK.
    private const int Hide = 0;
    private const int ShowWithoutActivation = 4;

    public DesktopIconObservation? Observe()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            return null;

        nint shell = GetShellWindow();
        uint processId = Owner(shell);
        if (shell == 0 || processId == 0)
            return null;

        long started;
        try
        {
            using Process process = Process.GetProcessById((int)processId);
            if (!string.Equals(process.ProcessName, "explorer", StringComparison.OrdinalIgnoreCase))
                return null;
            started = process.StartTime.ToUniversalTime().Ticks;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return null;
        }

        var candidates = new List<nint>();
        if (!EnumWindows((host, _) =>
        {
            if (Owner(host) == processId && Class(host) is "Progman" or "WorkerW")
                FindIconViews(host, processId, candidates);
            return true;
        }, 0) || candidates.Count != 1)
            return null;

        nint iconView = candidates[0];
        uint style = GetWindowLong(iconView, WindowStyle);
        if (style == 0 && Marshal.GetLastPInvokeError() != 0)
            return null;

        bool visible = (style & VisibleStyle) != 0;
        SHGetSettings(out uint flags, ShellHideIconsMask);
        if (visible != IsWindowVisible(iconView) || GetShellWindow() != shell ||
            Owner(shell) != processId || Owner(iconView) != processId)
            return null;

        return new(new(processId, started, shell.ToInt64(), iconView.ToInt64()), visible, (flags & HideIconsBit) != 0);
    }

    public bool SetVisible(DesktopIconObservation expected, bool visible)
    {
        // Reacquire before requesting a change. A new/recreated view is left to Windows and the user.
        if (expected.WindowsHidden || Observe() != expected)
            return false;
        return ShowWindowAsync(new nint(expected.Shell.IconView), visible ? ShowWithoutActivation : Hide);
    }

    private static void FindIconViews(nint host, uint processId, List<nint> candidates)
    {
        EnumChildWindows(host, (view, _) =>
        {
            if (Owner(view) == processId && Class(view) == "SHELLDLL_DefView")
            {
                EnumChildWindows(view, (list, _) =>
                {
                    if (Owner(list) == processId && Class(list) == "SysListView32")
                        candidates.Add(list);
                    return true;
                }, 0);
            }
            return true;
        }, 0);
    }

    private static uint Owner(nint window)
    {
        _ = GetWindowThreadProcessId(window, out uint owner);
        return owner;
    }

    private static string Class(nint window)
    {
        var name = new StringBuilder(64);
        _ = GetClassName(window, name, name.Capacity);
        return name.ToString();
    }

    private delegate bool WindowCallback(nint window, nint parameter);

    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(nint window, WindowCallback callback, nint parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint owner);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder name, int capacity);
    [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetWindowLongW")] private static extern uint GetWindowLong(nint window, int index);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(nint window, int command);
    [DllImport("shell32.dll")] private static extern void SHGetSettings(out uint flags, uint mask);
}
