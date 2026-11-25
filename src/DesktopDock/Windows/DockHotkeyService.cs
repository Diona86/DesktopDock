using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace DesktopDock;

internal enum DockHotkeyAction { Toggle, Peek, Lock, Pause, RestoreDesktop }

internal sealed record DockHotkeyProbeResult(bool Registered, int RegisterError, bool SecondRegistered, int ConflictError,
    bool Unregistered, bool RegisteredAfterRelease, int RegisterAfterReleaseError, bool ReleasedAgain);

internal sealed class DockHotkeyService : IDisposable
{
    private const uint NoRepeat = 0x4000;
    private const int HotkeyMessage = 0x0312;
    private readonly HwndSource source;
    private readonly Action<DockHotkeyAction> dispatch;
    private readonly Dictionary<HotkeyChord, Registration> registered = new();
    private readonly List<string> errors = [];
    private int nextId;
    private bool disposed;

    private sealed record Registration(int Id, DockHotkeyAction? Action);

    internal DockHotkeyService(Action<DockHotkeyAction> dispatch)
    {
        this.dispatch = dispatch;
        source = CreateMessageWindow();
        source.AddHook(HandleMessage);
    }

    internal IReadOnlyList<string> Errors => errors.AsReadOnly();
    internal static HotkeyChord DesktopRestoreChord { get; } = new(7, 0x7A);

    internal bool RegisterDesktopRestore(out string? error)
    {
        EnsureActive();
        int id = NewId();
        if (!Register(source.Handle, id, DesktopRestoreChord, out int win32Error))
        {
            error = RegistrationError(DockHotkeyAction.RestoreDesktop, win32Error);
            return false;
        }
        registered.Add(DesktopRestoreChord, new Registration(id, DockHotkeyAction.RestoreDesktop));
        error = null;
        return true;
    }

    internal void ApplyAtStartup(DockHotkeys hotkeys)
    {
        EnsureActive();
        errors.Clear();
        foreach (var (action, chord) in Enumerate(hotkeys))
        {
            if (!ValidChord(chord))
            {
                errors.Add($"{ActionName(action)}的快捷键无效。");
                continue;
            }
            if (registered.ContainsKey(chord))
            {
                errors.Add($"{ActionName(action)}的快捷键与另一动作重复。");
                continue;
            }

            int id = NewId();
            if (Register(source.Handle, id, chord, out int error))
                registered.Add(chord, new Registration(id, action));
            else
                errors.Add(RegistrationError(action, error));
        }
    }

    internal bool TryApply(DockHotkeys hotkeys, out string? error) => TryApply(hotkeys, static () => { }, out error);

    internal bool TryApply(DockHotkeys hotkeys, Action persist, out string? error)
    {
        EnsureActive();
        var next = new Dictionary<HotkeyChord, DockHotkeyAction>();
        foreach (var (action, chord) in Enumerate(hotkeys))
        {
            if (!ValidChord(chord) || !next.TryAdd(chord, action))
            {
                error = $"{ActionName(action)}的快捷键无效或与另一动作重复。";
                return false;
            }
        }

        var staged = new List<HotkeyChord>();
        try
        {
            foreach (var (chord, action) in next)
            {
                if (registered.ContainsKey(chord))
                    continue;
                int id = NewId();
                if (!Register(source.Handle, id, chord, out int win32Error))
                {
                    error = RegistrationError(action, win32Error);
                    foreach (HotkeyChord added in staged)
                        Release(added);
                    return false;
                }
                registered.Add(chord, new Registration(id, null));
                staged.Add(chord);
            }

            // Retain old bindings while persistence runs, so a failed write leaves them live.
            persist();
        }
        catch
        {
            foreach (HotkeyChord added in staged)
                Release(added);
            throw;
        }

        errors.Clear();
        foreach (HotkeyChord chord in registered.Keys.ToArray())
        {
            if (next.TryGetValue(chord, out DockHotkeyAction action))
                registered[chord] = registered[chord] with { Action = action };
            else
                Release(chord);
        }
        error = null;
        return true;
    }

    internal static DockHotkeyProbeResult ProbeConflict(HotkeyChord chord)
    {
        if (!ValidChord(chord))
            throw new ArgumentException("探针快捷键无效。", nameof(chord));

        using HwndSource first = CreateMessageWindow();
        using HwndSource second = CreateMessageWindow();
        const int id = 1;
        bool registered = Register(first.Handle, id, chord, out int registerError);
        if (!registered)
            return new(false, registerError, false, 0, false, false, 0, false);

        bool secondRegistered = Register(second.Handle, id, chord, out int conflictError);
        if (secondRegistered)
            UnregisterHotKey(second.Handle, id);
        bool unregistered = UnregisterHotKey(first.Handle, id);
        int retryError = 0;
        bool retried = unregistered && Register(second.Handle, id, chord, out retryError);
        bool releasedAgain = retried && UnregisterHotKey(second.Handle, id);
        return new(true, 0, secondRegistered, conflictError, unregistered, retried, retryError, releasedAgain);
    }

    public void Dispose()
    {
        source.Dispatcher.VerifyAccess();
        if (disposed)
            return;
        disposed = true;
        foreach (HotkeyChord chord in registered.Keys.ToArray())
            Release(chord);
        source.RemoveHook(HandleMessage);
        source.Dispose();
    }

    private nint HandleMessage(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != HotkeyMessage)
            return 0;
        int id = unchecked((int)wParam);
        int keyData = unchecked((int)lParam);
        var chord = new HotkeyChord(keyData & 0xF, (keyData >> 16) & 0xFFFF);
        if (!registered.TryGetValue(chord, out Registration? binding) || binding.Id != id || binding.Action is not DockHotkeyAction action)
            return 0;

        handled = true;
        dispatch(action);
        return 0;
    }

    private void Release(HotkeyChord chord)
    {
        Registration entry = registered[chord];
        if (UnregisterHotKey(source.Handle, entry.Id))
            registered.Remove(chord);
        else
        {
            errors.Add($"无法注销快捷键（Win32 {Marshal.GetLastPInvokeError()}）。");
            registered[chord] = entry with { Action = null };
        }
    }

    private int NewId()
    {
        // The HWND has its own application-defined ID range. Avoid the IDs still registered on it.
        do { nextId = nextId % 0xBFFF + 1; }
        while (registered.Values.Any(binding => binding.Id == nextId));
        return nextId;
    }

    private void EnsureActive()
    {
        source.Dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private static HwndSource CreateMessageWindow() => new(new HwndSourceParameters("Desktop Dock hotkeys")
    {
        ParentWindow = new nint(-3),
        WindowStyle = 0,
        Width = 0,
        Height = 0
    });

    private static IEnumerable<(DockHotkeyAction Action, HotkeyChord Chord)> Enumerate(DockHotkeys hotkeys)
    {
        if (hotkeys.Toggle is HotkeyChord toggle) yield return (DockHotkeyAction.Toggle, toggle);
        if (hotkeys.Peek is HotkeyChord peek) yield return (DockHotkeyAction.Peek, peek);
        if (hotkeys.Lock is HotkeyChord @lock) yield return (DockHotkeyAction.Lock, @lock);
        if (hotkeys.Pause is HotkeyChord pause) yield return (DockHotkeyAction.Pause, pause);
    }

    private static bool ValidChord(HotkeyChord chord) => chord.Modifiers is >= 0 and <= 0xF
        && chord.VirtualKey is > 0 and <= 0xFE
        && chord.VirtualKey is not (0x10 or 0x11 or 0x12 or 0x5B or 0x5C or 0x7B or >= 0xA0 and <= 0xA5);

    private static bool Register(nint window, int id, HotkeyChord chord, out int error)
    {
        bool registered = RegisterHotKey(window, id, (uint)chord.Modifiers | NoRepeat, (uint)chord.VirtualKey);
        error = registered ? 0 : Marshal.GetLastPInvokeError();
        return registered;
    }

    private static string RegistrationError(DockHotkeyAction action, int error) => error == 1409
        ? $"{ActionName(action)}的快捷键已被另一个程序占用（Win32 1409）。"
        : $"无法注册{ActionName(action)}的快捷键（Win32 {error}）。";

    private static string ActionName(DockHotkeyAction action) => action switch
    {
        DockHotkeyAction.Toggle => "呼出或收起",
        DockHotkeyAction.Peek => "临时显示",
        DockHotkeyAction.Lock => "锁定或解锁",
        DockHotkeyAction.RestoreDesktop => "恢复桌面图标",
        _ => "暂停或恢复热区"
    };

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint hwnd, int id);
}
