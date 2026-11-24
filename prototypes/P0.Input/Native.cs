using System.Runtime.InteropServices;

internal static class Native
{
    internal static readonly nint PerMonitorV2 = -4;
    internal const uint WmHotkey = 0x0312;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly PixelRect AsPixels() => new(Left, Top, Right, Bottom);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MonitorInfo
    {
        public uint Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DisplayDevice
    {
        public uint Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Key;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Message
    {
        public nint Window;
        public uint Id;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public Point Position;
        public uint Reserved;
    }

    internal delegate bool MonitorCallback(nint monitor, nint dc, nint bounds, nint data);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint SetThreadDpiAwarenessContext(nint context);

    [DllImport("user32.dll")]
    internal static extern nint GetWindowDpiAwarenessContext(nint window);

    [DllImport("user32.dll")]
    internal static extern bool AreDpiAwarenessContextsEqual(nint first, nint second);

    [DllImport("user32.dll")]
    internal static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorCallback callback, nint data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW", ExactSpelling = true)]
    internal static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplayDevicesW", ExactSpelling = true)]
    internal static extern bool EnumDisplayDevices(string name, uint index, ref DisplayDevice device, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateWindowExW", ExactSpelling = true, SetLastError = true)]
    internal static extern nint CreateWindowEx(uint extendedStyle, string className, string? caption, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameters);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool DestroyWindow(nint window);

    [DllImport("user32.dll")]
    internal static extern nint MonitorFromWindow(nint window, uint flags);

    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(nint window);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool UnregisterHotKey(nint window, int id);

    [DllImport("user32.dll", EntryPoint = "PeekMessageW", ExactSpelling = true)]
    internal static extern bool PeekMessage(out Message message, nint window, uint minimum, uint maximum, uint remove);
}
