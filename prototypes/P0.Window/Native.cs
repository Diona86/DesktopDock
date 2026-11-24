using System.Runtime.InteropServices;
using System.Text;

namespace P0.WindowProbe;

internal static class Native
{
    internal const int Style = -16;
    internal const int ExtendedStyle = -20;
    internal const int Caption = 0x00C00000;
    internal const int ThickFrame = 0x00040000;
    internal const int Layered = 0x00080000;
    internal const int AppWindow = 0x00040000;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        internal int Left, Top, Right, Bottom;
        internal readonly int Width => Right - Left;
        internal readonly int Height => Bottom - Top;

        internal readonly bool Covers(Rect other) =>
            Left <= other.Left + 2 && Top <= other.Top + 2 &&
            Right >= other.Right - 2 && Bottom >= other.Bottom - 2;

        public override readonly string ToString() => $"({Left},{Top})-({Right},{Bottom})";
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MonitorInfo
    {
        internal int Size;
        internal Rect Bounds, Work;
        internal int Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct Point(int x, int y)
    {
        internal readonly int X = x;
        internal readonly int Y = y;
    }

    [DllImport("user32.dll")]
    internal static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    internal static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll")]
    internal static extern bool IsIconic(nint hwnd);

    [DllImport("user32.dll")]
    internal static extern bool IsZoomed(nint hwnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    internal static extern int GetWindowLong(nint hwnd, int index);

    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("user32.dll")]
    internal static extern nint MonitorFromWindow(nint hwnd, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    internal static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    internal static extern nint WindowFromPoint(Point point);

    [DllImport("user32.dll")]
    internal static extern nint GetDC(nint hwnd);

    [DllImport("user32.dll")]
    internal static extern int ReleaseDC(nint hwnd, nint dc);

    [DllImport("gdi32.dll")]
    internal static extern uint GetPixel(nint dc, int x, int y);

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int GetDwmRect(nint hwnd, uint attribute, out Rect rect, int size);

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int GetDwmInt(nint hwnd, uint attribute, out int value, int size);

    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    internal static extern nint GetProcessWindowStation();

    [DllImport("user32.dll")]
    internal static extern nint GetThreadDesktop(uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint OpenInputDesktop(uint flags, bool inherit, uint desiredAccess);

    [DllImport("user32.dll")]
    internal static extern bool CloseDesktop(nint desktop);

    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetUserObjectInformation(nint handle, int index, StringBuilder value, int size, out int needed);

    internal static string UserObjectName(nint handle)
    {
        var name = new StringBuilder(256);
        return handle != 0 && GetUserObjectInformation(handle, 2, name, name.Capacity * sizeof(char), out _)
            ? name.ToString()
            : "unavailable";
    }

    internal static bool TryGetMonitor(nint hwnd, out MonitorInfo monitor)
    {
        monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        return GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref monitor);
    }

    internal static int GetFrame(nint hwnd, out Rect frame) => GetDwmRect(hwnd, 9, out frame, Marshal.SizeOf<Rect>());

    internal static int GetCloak(nint hwnd, out int cloak) => GetDwmInt(hwnd, 14, out cloak, sizeof(int));
}
