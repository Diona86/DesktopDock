using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace DesktopDock;

internal sealed class DockOutsideClickMonitor(Action<int, int> onButtonDown) : IDisposable
{
    private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
    private HookCallback? callback;
    private nint hook;
    private int generation;
    private bool enabled;
    private bool disposed;

    internal bool TryStart(out int win32Error)
    {
        dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(disposed, this);
        win32Error = 0;
        if (hook != 0)
        {
            enabled = true;
            return true;
        }

        callback ??= OnMouse;
        hook = SetWindowsHookEx(14, callback, GetModuleHandle(null), 0);
        if (hook != 0)
        {
            enabled = true;
            generation++;
            return true;
        }

        win32Error = Marshal.GetLastPInvokeError();
        return false;
    }

    internal void Stop()
    {
        dispatcher.VerifyAccess();
        enabled = false;
        const int invalidHookHandle = 1404;
        if (hook != 0 && (UnhookWindowsHookEx(hook) || Marshal.GetLastPInvokeError() == invalidHookHandle))
            hook = 0;
        generation++;
    }

    public void Dispose()
    {
        if (disposed)
            return;
        Stop();
        disposed = true;
    }

    private nint OnMouse(int code, nint wParam, nint lParam)
    {
        // Observe coordinates without changing input delivery or waiting on any WPF work.
        nint result = CallNextHookEx(0, code, wParam, lParam);
        int message = unchecked((int)wParam);
        if (code != 0 || !enabled || disposed || message is not (0x0201 or 0x0204 or 0x0207 or 0x020B)
            || dispatcher.HasShutdownStarted)
            return result;

        MouseEvent mouse = Marshal.PtrToStructure<MouseEvent>(lParam);
        const uint injected = 1;
        if ((mouse.Flags & injected) != 0)
            return result;

        int version = generation;
        try
        {
            dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (enabled && !disposed && version == generation)
                    onButtonDown(mouse.X, mouse.Y);
            }));
        }
        catch (InvalidOperationException) when (dispatcher.HasShutdownStarted)
        {
        }
        return result;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseEvent
    {
        internal int X, Y;
        internal uint MouseData, Flags, Time;
        internal nuint ExtraInfo;
    }

    private delegate nint HookCallback(int code, nint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int id, HookCallback callback, nint module, uint thread);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleHandleW", ExactSpelling = true)]
    private static extern nint GetModuleHandle(string? moduleName);
}
