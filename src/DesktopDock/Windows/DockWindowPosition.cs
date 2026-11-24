using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace DesktopDock;

internal static class DockWindowPosition
{
    private const uint DefaultToPrimary = 1;
    private const uint NoSizeNoOrderNoActivation = 0x0001 | 0x0004 | 0x0010;

    internal readonly record struct WorkArea(Rect Pixels, double Scale)
    {
        internal double Width => Pixels.Width / Scale;
        internal double Height => Pixels.Height / Scale;

        internal Point Clamp(Point location, Size window) => new(
            Math.Clamp(location.X, Pixels.Left, Math.Max(Pixels.Left, Pixels.Right - window.Width)),
            Math.Clamp(location.Y, Pixels.Top, Math.Max(Pixels.Top, Pixels.Bottom - window.Height)));
    }

    internal static WorkArea GetWorkArea(Window window)
    {
        nint handle = new WindowInteropHelper(window).Handle;
        uint dpi = handle != 0 ? GetDpiForWindow(handle) : 0;
        double scale = dpi != 0 ? dpi / 96d : VisualTreeHelper.GetDpi(window).DpiScaleX;

        // P1 uses the primary screen. Persist floating position relative to its work area.
        var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (GetMonitorInfo(MonitorFromPoint(new NativePoint(), DefaultToPrimary), ref monitor))
            return new WorkArea(monitor.Work.ToRect(), scale);

        Rect work = SystemParameters.WorkArea;
        return new WorkArea(new Rect(work.X * scale, work.Y * scale, work.Width * scale, work.Height * scale), scale);
    }

    internal static bool TryGetBounds(Window window, out Rect bounds)
    {
        nint handle = new WindowInteropHelper(window).Handle;
        if (handle != 0 && GetWindowRect(handle, out NativeRect rect))
        {
            bounds = rect.ToRect();
            return true;
        }

        bounds = Rect.Empty;
        return false;
    }

    internal static bool TryGetPointer(out Point point)
    {
        bool success = GetCursorPos(out NativePoint cursor);
        point = new Point(cursor.X, cursor.Y);
        return success;
    }

    internal static void Move(Window window, Point position)
    {
        nint handle = new WindowInteropHelper(window).Handle;
        if (handle != 0)
            SetWindowPos(handle, 0, (int)Math.Round(position.X), (int)Math.Round(position.Y), 0, 0, NoSizeNoOrderNoActivation);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { internal int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        internal int Left, Top, Right, Bottom;
        internal readonly Rect ToRect() => new(Left, Top, Right - Left, Bottom - Top);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        internal int Size;
        internal NativeRect Monitor, Work;
        internal int Flags;
    }

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
}
