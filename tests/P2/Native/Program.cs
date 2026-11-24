using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DesktopDock;

static class NativeProbe
{
    [STAThread]
    private static int Main(string[] args)
    {
        bool skipHotkeys = args is ["--skip-hotkeys"];
        if (args.Length != 0 && !skipHotkeys)
        {
            Console.Error.WriteLine("Usage: NativeProbe [--skip-hotkeys]");
            return 2;
        }

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            try
            {
                await Verify(skipHotkeys);
                app.Shutdown(0);
            }
            catch (Exception e)
            {
                Console.WriteLine("FAIL " + e);
                app.Shutdown(1);
            }
        };
        return app.Run();
    }

    private static async Task Verify(bool skipHotkeys)
    {
        var monitors = DockWindows.GetMonitors();
        DockMonitor monitor = monitors.First(item => item.Primary);
        Console.WriteLine($"PASS monitor count={monitors.Count} physical={monitor.Left},{monitor.Top},{monitor.Right},{monitor.Bottom} work={monitor.WorkLeft},{monitor.WorkTop},{monitor.WorkRight},{monitor.WorkBottom} scale={monitor.Scale} stableInterfaceId={monitor.Id == DockWindows.GetMonitors().First(item => item.Primary).Id}");
        Console.WriteLine($"Pointer sample available={DockWindows.TryGetPointer(out _, out _)}");
        var foreground = DockWindows.ReadForeground(monitor);
        Console.WriteLine($"Live foreground state={foreground.State} shell={foreground.ShellState} automatic={foreground.CanAutoReveal([])}");

        Window framed = WindowFixture(WindowStyle.SingleBorderWindow);
        Window? borderless = null;
        try
        {
            framed.Show();
            await Task.Delay(250);
            Confirm(framed, monitor, DockWindowState.Ordinary);
            framed.WindowState = WindowState.Maximized;
            await Task.Delay(250);
            var max = Confirm(framed, monitor, DockWindowState.Maximized);
            if (max.ExecutablePath is null || !max.CanAutoReveal([new(max.ExecutablePath, DockRuleAction.AllowAutomatic)]) || max.CanAutoReveal([]))
                throw new InvalidOperationException("Native process allow rule failed.");
            Console.WriteLine("PASS explicit allow rule for the native maximized fixture executable");
            framed.Close();

            borderless = WindowFixture(WindowStyle.None);
            borderless.Left = monitor.Left / monitor.Scale;
            borderless.Top = monitor.Top / monitor.Scale;
            borderless.Width = (monitor.Right - monitor.Left) / monitor.Scale;
            borderless.Height = (monitor.Bottom - monitor.Top) / monitor.Scale;
            borderless.Show();
            nint hwnd = new WindowInteropHelper(borderless).Handle;
            if (!SetWindowPos(hwnd, 0, monitor.Left, monitor.Top, monitor.Right - monitor.Left, monitor.Bottom - monitor.Top, 0x0014))
                throw new InvalidOperationException("Fixture positioning failed");
            await Task.Delay(250);
            Confirm(borderless, monitor, DockWindowState.BorderlessFullScreen);
            using var videoFixture = AviFixture.Create();
            await VerifyVideo(borderless, monitor, videoFixture.FilePath);
            var invalid = DockWindows.ReadWindow(0, monitor);
            if (invalid.State != DockWindowState.Unknown || invalid.CanAutoReveal([]))
                throw new InvalidOperationException("Invalid HWND did not block automatic reveal");
            Console.WriteLine("PASS invalid HWND policy blocks automatic reveal");
        }
        finally
        {
            if (framed.IsVisible) framed.Close();
            borderless?.Close();
        }

        if (skipHotkeys)
            Console.WriteLine("SKIP native shortcut registration at caller's request");
        else
            VerifyHotkeys();

        using var click = new DockOutsideClickMonitor((_, _) => { });
        bool started = click.TryStart(out int hookError);
        click.Stop();
        bool restarted = click.TryStart(out int restartError);
        click.Stop();
        Console.WriteLine($"Passive outside mouse hook installed={started} error={hookError} reinstalled={restarted} retryError={restartError}; button delivery not sampled");
    }

    private static void VerifyHotkeys()
    {
        var chord = new HotkeyChord(7, 0x79);
        var probe = DockHotkeyService.ProbeConflict(chord);
        Console.WriteLine($"Native conflict probe {probe}");
        if (probe.Registered)
        {
            if (probe.ConflictError != 1409 || !probe.Unregistered || !probe.RegisteredAfterRelease || !probe.ReleasedAgain)
                throw new InvalidOperationException("Win32 conflict or release result unexpected");
            Console.WriteLine("PASS Win32 conflict and release enforcement");
            using var live = new DockHotkeyService(_ => { });
            using var competing = new DockHotkeyService(_ => { });
            var first = Bind(chord);
            var secondChord = new HotkeyChord(7, 0x7A);
            var second = Bind(secondChord);
            live.ApplyAtStartup(first);
            competing.ApplyAtStartup(second);
            if (live.Errors.Count != 0 || competing.Errors.Count != 0)
                throw new InvalidOperationException("Probe combinations are already taken");
            if (live.TryApply(second, out string? error) || error?.Contains("1409") != true
                || DockHotkeyService.ProbeConflict(chord).RegisterError != 1409)
                throw new InvalidOperationException("Prior binding disappeared after attempted conflict");
            competing.Dispose();
            try
            {
                live.TryApply(second, static () => throw new IOException("controlled persist error"), out _);
                throw new InvalidOperationException("Persist failure should propagate");
            }
            catch (IOException)
            {
                if (DockHotkeyService.ProbeConflict(chord).RegisterError != 1409 || !DockHotkeyService.ProbeConflict(secondChord).Registered)
                    throw new InvalidOperationException("Prior binding not retained, or staged key leaked, after failed persist");
            }
            Console.WriteLine("PASS hotkey conflict and persistence failure retain old binding and release staged binding");
        }
        else
        {
            Console.WriteLine($"UNVERIFIED native lifecycle registration unavailable error={probe.RegisterError}");
        }

    }

    private static DockHotkeys Bind(HotkeyChord chord) => new() { Toggle = chord, Peek = null, Lock = null, Pause = null };

    private static async Task VerifyVideo(Window borderless, DockMonitor monitor, string path)
    {
        var video = new System.Windows.Controls.MediaElement
        {
            LoadedBehavior = System.Windows.Controls.MediaState.Manual,
            UnloadedBehavior = System.Windows.Controls.MediaState.Manual,
            Volume = 0,
            Stretch = System.Windows.Media.Stretch.Uniform
        };
        var opened = new TaskCompletionSource();
        video.MediaOpened += (_, _) => opened.TrySetResult();
        video.MediaFailed += (_, e) => opened.TrySetException(e.ErrorException);
        borderless.Content = video;
        video.Source = new Uri(Path.GetFullPath(path));
        try
        {
            video.Play();
            await opened.Task.WaitAsync(TimeSpan.FromSeconds(8));
            await Task.Delay(450);
            double position = video.Position.TotalMilliseconds;
            if (!video.HasVideo || position < 10)
                throw new InvalidOperationException($"Media opened but no frame position advanced: hasVideo={video.HasVideo} positionMs={position:0}");
            var observation = Confirm(borderless, monitor, DockWindowState.BorderlessFullScreen);
            if (observation.CanAutoReveal([]))
                throw new InvalidOperationException("Borderless fullscreen video should block automatic reveal");
            Console.WriteLine($"PASS local fullscreen WPF video playback frame={video.NaturalVideoWidth}x{video.NaturalVideoHeight} positionMs={position:0}; automatic reveal blocked");
        }
        catch (Exception e)
        {
            Console.WriteLine($"UNVERIFIED local fullscreen WPF playback: {e.GetType().Name} {e.Message}");
        }
        finally
        {
            video.Close();
        }
    }

    private static DockWindowObservation Confirm(Window window, DockMonitor monitor, DockWindowState expected)
    {
        var observed = DockWindows.ReadWindow(new WindowInteropHelper(window).Handle, monitor);
        Console.WriteLine($"Observed {observed.State} for expected={expected} maximized={observed.IsMaximized} covers={observed.CoversMonitor} hasFrame={observed.HasFrame} sameMonitor={observed.OnTargetMonitor}");
        if (observed.State != expected)
            throw new InvalidOperationException(observed.Reason);
        Console.WriteLine($"PASS native {expected} classification");
        return observed;
    }

    private static Window WindowFixture(WindowStyle style) => new()
    {
        Title = "Desktop Dock P2 native validation fixture", ShowActivated = false, ShowInTaskbar = false,
        WindowStyle = style, Width = 360, Height = 220, Left = 70, Top = 70,
        ResizeMode = style == WindowStyle.None ? ResizeMode.NoResize : ResizeMode.CanResize,
        WindowStartupLocation = WindowStartupLocation.Manual
    };

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
}
