using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DesktopDock;

internal enum DockWindowState
{
    Desktop,
    Ordinary,
    Maximized,
    BorderlessFullScreen,
    ExclusiveFullScreen,
    Unknown
}

internal sealed record DockWindowObservation(
    DockWindowState State, string Reason, string? ExecutablePath = null, int? ShellState = null,
    bool IsMaximized = false, bool CoversMonitor = false, bool HasFrame = false, bool OnTargetMonitor = false)
{
    internal bool CanAutoReveal(IReadOnlyList<DockApplicationRule> rules)
    {
        // A locked or switched user session should never display the Dock automatically.
        if (ShellState == DockWindows.ShellNotPresent)
            return false;

        if (ExecutablePath != null)
        {
            DockApplicationRule? rule = rules.FirstOrDefault(candidate =>
                string.Equals(candidate.ExecutablePath, ExecutablePath, StringComparison.OrdinalIgnoreCase));
            if (rule != null)
                return rule.Action == DockRuleAction.AllowAutomatic;
        }

        return State is DockWindowState.Desktop or DockWindowState.Ordinary;
    }
}

internal static class DockWindows
{
    internal const int ShellNotPresent = 1;
    private const int ShellBusy = 2;
    private const int ShellExclusive = 3;
    private const int ShellPresentation = 4;
    private const int ShellAcceptsNotifications = 5;
    private const int ShellQuietTime = 6;
    private const int ShellApp = 7;
    private const int StyleIndex = -16;
    private const int ExtendedStyleIndex = -20;
    private const int Caption = 0x00C00000;
    private const int ThickFrame = 0x00040000;
    private const int Popup = unchecked((int)0x80000000);
    private const int ToolWindow = 0x00000080;
    private const int ExtendedFrameBounds = 9;
    private const int Cloaked = 14;

    internal static IReadOnlyList<DockMonitor> GetMonitors()
    {
        var monitors = new List<DockMonitor>();
        EnumDisplayMonitors(0, 0, (monitor, _, _, _) =>
        {
            var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(monitor, ref info) || !info.Bounds.Usable)
                return true;

            var display = new DisplayDevice { Size = (uint)Marshal.SizeOf<DisplayDevice>() };
            const uint getDeviceInterfaceName = 1;
            bool hasInterface = EnumDisplayDevices(info.DeviceName, 0, ref display, getDeviceInterfaceName)
                && !string.IsNullOrWhiteSpace(display.DeviceId);
            string id = hasInterface
                ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(display.DeviceId.ToUpperInvariant())))[..24]
                : info.DeviceName.ToUpperInvariant();
            string name = hasInterface && !string.IsNullOrWhiteSpace(display.Description)
                ? $"{display.Description} ({info.DeviceName})" : info.DeviceName;
            NativeRect bounds = info.Bounds;
            NativeRect work = info.Work.Usable ? info.Work : bounds;
            monitors.Add(new DockMonitor(id, name, bounds.Left, bounds.Top, bounds.Right, bounds.Bottom,
                work.Left, work.Top, work.Right, work.Bottom, ReadMonitorScale(monitor, bounds), (info.Flags & 1) != 0));
            return true;
        }, 0);

        return monitors.OrderByDescending(monitor => monitor.Primary).ThenBy(monitor => monitor.Id, StringComparer.Ordinal).ToArray();
    }

    internal static bool TryGetPointer(out int x, out int y)
    {
        bool read = GetCursorPos(out NativePoint point);
        x = point.X;
        y = point.Y;
        return read;
    }

    internal static bool IsVirtualKeyDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    internal static DockWindowObservation ReadForeground(DockMonitor monitor)
    {
        bool shellKnown = SHQueryUserNotificationState(out int shell) >= 0;
        int? shellState = shellKnown ? shell : null;
        nint hwnd = GetForegroundWindow();
        if (hwnd == 0)
            return WithShell(new(DockWindowState.Unknown, "未能取得前台窗口"), shellState);

        DockWindowObservation foreground = InspectWindow(hwnd, monitor);
        if (foreground.Reason == "窗口位于其他屏幕" && shellState is not (ShellNotPresent or ShellBusy or ShellExclusive or ShellPresentation))
            foreground = ReadTopWindowOnMonitor(monitor);
        return WithShell(foreground, shellState);
    }

    internal static DockWindowObservation ReadWindow(nint hwnd, DockMonitor monitor, bool includeShellState = false)
    {
        DockWindowObservation window = InspectWindow(hwnd, monitor);
        if (!includeShellState)
            return window;
        return WithShell(window, SHQueryUserNotificationState(out int shell) >= 0 ? shell : null);
    }

    private static DockWindowObservation ReadTopWindowOnMonitor(DockMonitor monitor)
    {
        DockWindowObservation? result = null;
        // EnumWindows starts at the visible Z-order. Ignore the Dock, its settings and transient tool windows.
        bool completed = EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd) || (GetWindowLong(hwnd, ExtendedStyleIndex) & ToolWindow) != 0)
                return true;
            GetWindowThreadProcessId(hwnd, out uint process);
            if (process == Environment.ProcessId || hwnd == GetShellWindow() || hwnd == GetDesktopWindow())
                return true;

            DockWindowObservation observed = InspectWindow(hwnd, monitor);
            if (!observed.OnTargetMonitor || observed.State == DockWindowState.Desktop)
                return true;
            result = observed;
            return false;
        }, 0);
        return result ?? (completed
            ? new(DockWindowState.Desktop, "目标屏幕没有可见应用窗口")
            : new(DockWindowState.Unknown, "未能枚举目标屏幕的窗口"));
    }

    private static DockWindowObservation InspectWindow(nint hwnd, DockMonitor monitor)
    {
        if (hwnd == 0 || !IsWindow(hwnd))
            return new(DockWindowState.Unknown, "窗口句柄无效");
        if (hwnd == GetShellWindow() || hwnd == GetDesktopWindow())
            return new(DockWindowState.Desktop, "Windows 桌面");

        var className = new StringBuilder(64);
        GetClassName(hwnd, className, className.Capacity);
        if (className.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            return new(DockWindowState.Desktop, "Windows 桌面或任务栏");
        if (!IsWindowVisible(hwnd) || IsIconic(hwnd))
            return new(DockWindowState.Unknown, "窗口隐藏或最小化");

        bool hasCloakState = GetDwmInt(hwnd, Cloaked, out int cloaked, sizeof(int)) >= 0;
        if (hasCloakState && cloaked != 0)
            return new(DockWindowState.Unknown, "DWM 隐藏了窗口");

        bool dwmBounds = GetDwmRect(hwnd, ExtendedFrameBounds, out NativeRect bounds, Marshal.SizeOf<NativeRect>()) >= 0 && bounds.Usable;
        if (!dwmBounds && (!GetWindowRect(hwnd, out bounds) || !bounds.Usable))
            return new(DockWindowState.Unknown, "无法取得窗口边界");

        var target = new NativeRect(monitor.Left, monitor.Top, monitor.Right, monitor.Bottom);
        if (!bounds.Intersects(target))
            return new(DockWindowState.Unknown, "窗口位于其他屏幕");

        int style = GetWindowLong(hwnd, StyleIndex);
        bool styleKnown = style != 0 || Marshal.GetLastPInvokeError() == 0;
        bool hasFrame = (style & Caption) == Caption || (style & ThickFrame) != 0;
        bool maximized = IsZoomed(hwnd);
        // Permit a small rounding tolerance across DPI boundaries, but do not use size alone.
        bool covers = bounds.Covers(target, Math.Max(2, (int)Math.Ceiling(monitor.Scale * 2)));
        DockWindowState state;
        string reason;
        if (!styleKnown || !hasCloakState)
            (state, reason) = (DockWindowState.Unknown, "无法可靠读取窗口样式或 DWM 状态");
        else if (dwmBounds && covers && !hasFrame)
            (state, reason) = (DockWindowState.BorderlessFullScreen, "无边框窗口的 DWM 边界覆盖屏幕");
        else if (maximized)
            (state, reason) = (DockWindowState.Maximized, "Win32 窗口已最大化");
        else if (covers)
            (state, reason) = (DockWindowState.Unknown, "窗口覆盖屏幕，样式不足以确认全屏类别");
        else
            (state, reason) = (DockWindowState.Ordinary, "普通窗口");

        return new(state, reason, ReadExecutable(hwnd), null, maximized, covers, hasFrame, true);
    }

    private static DockWindowObservation WithShell(DockWindowObservation window, int? shellState) => shellState switch
    {
        ShellExclusive => window with { State = DockWindowState.ExclusiveFullScreen, Reason = "Windows Shell 报告独占 Direct3D 全屏", ShellState = shellState },
        ShellNotPresent or ShellBusy or ShellPresentation => window with
        {
            State = window.State is DockWindowState.Maximized or DockWindowState.BorderlessFullScreen ? window.State : DockWindowState.Unknown,
            Reason = $"Windows Shell 报告不可打断状态 {shellState}",
            ShellState = shellState
        },
        ShellAcceptsNotifications or ShellQuietTime or ShellApp => window with { ShellState = shellState },
        _ => window with { State = DockWindowState.Unknown, Reason = "Windows Shell 全屏信号不可用", ShellState = shellState }
    };

    private static string? ReadExecutable(nint hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint processId);
        if (processId == 0)
            return null;
        using SafeProcessHandle process = OpenProcess(0x1000, false, processId);
        if (process.IsInvalid)
            return null;

        var path = new StringBuilder(1024);
        uint length = (uint)path.Capacity;
        if (QueryFullProcessImageName(process, 0, path, ref length))
            return path.ToString();
        if (Marshal.GetLastPInvokeError() != 122)
            return null;

        path = new StringBuilder(32768);
        length = (uint)path.Capacity;
        return QueryFullProcessImageName(process, 0, path, ref length) ? path.ToString() : null;
    }

    private static double ReadMonitorScale(nint monitor, NativeRect bounds)
    {
        // A temporary hidden window reads the effective DPI, including nonstandard scale values.
        nint hwnd = CreateWindowEx(0, "STATIC", null, Popup, bounds.Left, bounds.Top, 1, 1, 0, 0, 0, 0);
        if (hwnd != 0)
        {
            try
            {
                uint dpi = GetDpiForWindow(hwnd);
                if (dpi > 0)
                    return dpi / 96d;
            }
            finally
            {
                DestroyWindow(hwnd);
            }
        }

        return GetScaleFactorForMonitor(monitor, out int scale) >= 0 && scale > 0 ? scale / 100d : 1d;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { internal int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativeRect(int Left, int Top, int Right, int Bottom)
    {
        internal bool Usable => Right > Left && Bottom > Top;
        internal bool Intersects(NativeRect other) => Left < other.Right && Right > other.Left && Top < other.Bottom && Bottom > other.Top;
        internal bool Covers(NativeRect other, int tolerance) => Left <= other.Left + tolerance && Top <= other.Top + tolerance
            && Right >= other.Right - tolerance && Bottom >= other.Bottom - tolerance;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        internal uint Size;
        internal NativeRect Bounds, Work;
        internal uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string DeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        internal uint Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string Description;
        internal uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string DeviceKey;
    }

    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool MonitorCallback(nint monitor, nint dc, nint bounds, nint data);
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool WindowCallback(nint hwnd, nint data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorCallback callback, nint data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplayDevicesW", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(string adapter, uint index, ref DisplayDevice display, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateWindowExW", ExactSpelling = true)]
    private static extern nint CreateWindowEx(uint extendedStyle, string className, string? title, int style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("shcore.dll")]
    private static extern int GetScaleFactorForMonitor(nint monitor, out int factor);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern nint GetShellWindow();

    [DllImport("user32.dll")]
    private static extern nint GetDesktopWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW", ExactSpelling = true)]
    private static extern int GetClassName(nint hwnd, StringBuilder className, int count);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(WindowCallback callback, nint data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hwnd, out NativeRect bounds);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong(nint hwnd, int index);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int GetDwmRect(nint hwnd, int attribute, out NativeRect rect, int size);

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int GetDwmInt(nint hwnd, int attribute, out int result, int size);

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref uint length);
}
