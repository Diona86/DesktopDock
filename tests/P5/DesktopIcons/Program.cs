using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using DesktopDock;

if (args is ["--native-fixture"])
    return NativeFixture.Run();
if (args.Length != 1 || !Path.IsPathFullyQualified(args[0]))
{
    Console.Error.WriteLine("Specify a new absolute evidence directory.");
    return 2;
}
if (Directory.Exists(args[0]))
    throw new IOException("The evidence directory already exists.");
Directory.CreateDirectory(args[0]);

int passed = 0;
void Check(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
    passed++;
    Console.WriteLine($"PASS {message}");
}

string Profile(string name)
{
    string directory = Path.Combine(args[0], name);
    Directory.CreateDirectory(directory);
    return directory;
}

var initial = new DesktopIconObservation(new(1234, 1, 21, 63), true, false);
const string journalName = "desktop-icons.json";
var backend = new ControlledDesktop(initial);
string normal = Profile("normal");
string journal = Path.Combine(normal, journalName);
string target = Path.Combine(normal, "fixture.txt");
File.WriteAllText(target, "Desktop Dock isolated fixture");
byte[] hash = SHA256.HashData(File.ReadAllBytes(target));

var service = new DesktopIconService(normal, backend);
Check(service.Refresh().CanHide && backend.Requests == 0 && !File.Exists(journal),
    "startup does not change visibility or write a record");
backend.BeforeProcess = () => Check(File.ReadAllText(journal).Contains("HidePending"), "operation is journaled before the hide request");
Check(service.HideAfterConfirmation() is { State: "Hidden", CanRestore: true, RecoveryNeeded: false },
    "confirmed hide observes changed visibility with original preference intact");
backend.BeforeProcess = () => Check(File.ReadAllText(journal).Contains("RestorePending"), "restore request is journaled before mutation");
Check(service.Restore() is { State: "Restored", Visible: true, CanRestore: false } && backend.Requests == 2,
    "explicit restore returns the view to its original visibility");
Check(SHA256.HashData(File.ReadAllBytes(target)).SequenceEqual(hash), "test file path and content remain intact");

backend = new ControlledDesktop(initial with { Visible = false, WindowsHidden = true });
service = new DesktopIconService(Profile("windows-hidden"), backend);
Check(!service.Refresh().CanHide && !service.Restore().CanRestore && backend.Requests == 0,
    "an existing Windows hide preference never gets an ownership record");

backend = new ControlledDesktop(initial);
service = new DesktopIconService(Profile("manual"), backend);
service.HideAfterConfirmation();
backend.Current = backend.Current! with { WindowsHidden = true };
Check(service.Refresh() is { State: "UserChanged", CanRestore: false }, "manual Windows preference change releases ownership");
service.RestoreOnExit();
Check(backend.Requests == 1 && backend.Current.WindowsHidden, "exit preserves later Windows settings");

backend = new ControlledDesktop(initial);
service = new DesktopIconService(Profile("normal-exit"), backend);
service.HideAfterConfirmation();
service.RestoreOnExit();
Check(service.Status is { State: "Restored", Visible: true } && backend.Requests == 2,
    "normal exit restores Dock-owned visibility on the same Explorer generation");

backend = new ControlledDesktop(initial) { Defer = true };
service = new DesktopIconService(Profile("exit-during-pending-hide"), backend);
Check(service.HideAfterConfirmation() is { State: "HidePending", Visible: true },
    "normal exit fixture starts with an accepted hide still pending");
int observationsUntilHidden = 2;
backend.BeforeObserve = () =>
{
    if (--observationsUntilHidden != 0)
        return;
    backend.BeforeObserve = null;
    backend.Current = initial with { Visible = false };
    backend.Defer = false;
};
service.RestoreOnExit();
Check(service.Status is { State: "Restored", Visible: true } && backend.Requests == 2,
    "normal exit confirms a pending hide before restoring the same Explorer view");

backend = new ControlledDesktop(initial) { Defer = true };
service = new DesktopIconService(Profile("exit-during-unconfirmed-hide"), backend);
service.HideAfterConfirmation();
var exitWait = Stopwatch.StartNew();
service.RestoreOnExit();
Check(exitWait.Elapsed < TimeSpan.FromSeconds(8) && service.Status is { Visible: true, CanRestore: false } &&
    backend.Requests == 1, "normal exit bounds observation and does not restore an unconfirmed hide");

backend = new ControlledDesktop(initial) { Defer = true };
service = new DesktopIconService(Profile("asynchronous-view"), backend);
Check(service.HideAfterConfirmation() is { State: "HidePending", CanRestore: false, Visible: true },
    "accepted asynchronous request still awaits an observed state change");
backend.Current = initial with { Visible = false };
Check(service.Refresh() is { State: "Hidden", CanRestore: true }, "observation confirms asynchronous hide");
backend.Current = initial;
Check(service.Refresh() is { State: "UserChanged", CanRestore: false } && backend.Requests == 1,
    "external visibility change releases ownership even with the same Windows preference");

backend = new ControlledDesktop(initial);
string interrupted = Profile("interrupted");
new DesktopIconService(interrupted, backend).HideAfterConfirmation();
service = new DesktopIconService(interrupted, backend);
Check(service.Refresh() is { RecoveryNeeded: true, CanRestore: true }, "reopening a prior-run hide record offers recovery");
service.RestoreOnExit();
Check(backend.Requests == 1, "exit leaves the previous process's recovery for an explicit decision");
Check(service.Restore().Visible == true && backend.Requests == 2, "explicit recovery on the same Explorer generation succeeds");

backend = new ControlledDesktop(initial);
service = new DesktopIconService(Profile("explorer-generation"), backend);
service.HideAfterConfirmation();
backend.Current = null;
Check(service.Refresh() is { RecoveryNeeded: true, CanRestore: false }, "missing Explorer view disables restore while ownership is uncertain");
backend.Current = initial with { Shell = initial.Shell with { ProcessId = 5678, ProcessStartTicks = 2 }, Visible = false };
Check(service.Refresh() is { State: "ExplorerChanged", CanRestore: false, RecoveryNeeded: true },
    "new Explorer generation triggers manual recovery and invalidates captured HWND");
service.RestoreOnExit();
Check(backend.Requests == 1, "exit does not force visibility on the replacement Explorer");

backend = new ControlledDesktop(initial) { Reject = true };
service = new DesktopIconService(Profile("rejected"), backend);
Check(service.HideAfterConfirmation() is { State: "Uncertain", CanRestore: false, Visible: true },
    "failed request preserves observed state and avoids claiming ownership");

backend = new ControlledDesktop(initial);
string unwritable = Profile("locked-journal");
service = new DesktopIconService(unwritable, backend);
Directory.CreateDirectory(Path.Combine(unwritable, journalName));
Check(service.HideAfterConfirmation() is { RecoveryNeeded: true, Visible: true, CanHide: false } && backend.Requests == 0,
    "journal write failure stops the native mutation before it is requested");

backend = new ControlledDesktop(initial);
string corrupt = Profile("corrupt");
string corruptFile = Path.Combine(corrupt, journalName);
File.WriteAllText(corruptFile, "{broken");
service = new DesktopIconService(corrupt, backend);
Check(service.HideAfterConfirmation() is { RecoveryNeeded: true, CanHide: false } && backend.Requests == 0 &&
    File.ReadAllText(corruptFile) == "{broken", "invalid journal stays intact and disables writes");

if (OperatingSystem.IsWindows())
{
    string observed = Profile("native-observation");
    service = DesktopIconService.Open(observed, allowChanges: false);
    Console.WriteLine($"NATIVE shell_pid={service.Status.ExplorerProcessId} list_visible={service.Status.Visible} windows_hide={service.Status.WindowsPreferenceHidden}");
    Check(!service.Status.CanHide && !service.Status.CanRestore && !File.Exists(Path.Combine(observed, journalName)),
        "native probe is read-only and leaves no recovery data");
    NativeFixture.Verify(Check);
}
Console.WriteLine($"END passed={passed} failed=0");
return 0;

internal sealed class ControlledDesktop(DesktopIconObservation initial) : IDesktopIconBackend
{
    internal DesktopIconObservation? Current { get; set; } = initial;
    internal Action? BeforeObserve { get; set; }
    internal Action? BeforeProcess { get; set; }
    internal int Requests { get; private set; }
    internal bool Reject { get; init; }
    internal bool Defer { get; set; }
    public DesktopIconObservation? Observe()
    {
        BeforeObserve?.Invoke();
        return Current;
    }

    public bool SetVisible(DesktopIconObservation expected, bool visible)
    {
        Requests++;
        BeforeProcess?.Invoke();
        if (Reject || Current != expected)
            return false;
        if (!Defer)
            Current = expected with { Visible = visible };
        return true;
    }
}

// This deliberately has no code path that accepts a caller-supplied HWND.
internal static class NativeFixture
{
    internal static int Run()
    {
        int offscreen = GetSystemMetrics(76) - 200;
        nint window = CreateWindowEx(0x08000080, "STATIC", "Desktop Dock P5 isolated fixture", 0x90000000,
            offscreen, 0, 80, 80, 0, 0, 0, 0);
        if (window == 0)
            return 3;
        Console.WriteLine(window.ToInt64());
        Console.Out.Flush();
        while (IsWindow(window) && GetMessage(out Message message, 0, 0, 0) > 0)
            DispatchMessage(ref message);
        return 0;
    }

    internal static void Verify(Action<bool, string> check)
    {
        using Process child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--native-fixture")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true
        }) ?? throw new InvalidOperationException("Cannot start the isolated native fixture.");
        nint fixture = 0;
        try
        {
            string? result = child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            fixture = new nint(long.Parse(result!));
            GetWindowThreadProcessId(fixture, out uint found);
            check(found == child.Id && IsWindowVisible(fixture), "isolated cross-process Win32 HWND starts visible offscreen");
            check(ShowWindowAsync(fixture, 0) && SpinWait.SpinUntil(() => !IsWindowVisible(fixture), 2000),
                "ShowWindowAsync hides the isolated HWND");
            check(ShowWindowAsync(fixture, 4) && SpinWait.SpinUntil(() => IsWindowVisible(fixture), 2000),
                "ShowWindowAsync restores the isolated HWND without changing Explorer");
        }
        finally
        {
            if (fixture != 0 && GetWindowThreadProcessId(fixture, out uint ending) != 0 && ending == child.Id)
                PostMessage(fixture, 0x10, 0, 0);
            if (!child.WaitForExit(5000))
                child.Kill();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        internal nint Window;
        internal uint Id;
        internal nint WParam, LParam;
        internal uint Time;
        internal int X, Y;
        internal uint Private;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowEx(uint exStyle, string cls, string name,
        uint style, int x, int y, int width, int height, nint parent, nint menu, nint module, nint parameter);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int field);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll", EntryPoint = "GetMessageW")] private static extern int GetMessage(out Message message, nint window, uint min, uint max);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")] private static extern nint DispatchMessage(ref Message message);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(nint window, int show);
}
