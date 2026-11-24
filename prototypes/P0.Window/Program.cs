using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace P0.WindowProbe;

internal sealed class Probe(TextWriter log)
{
    private const uint NoActivateNoZOrder = 0x0010 | 0x0004;
    private const uint BackdropColor = 0x00904818;
    private static readonly Color PanelColor = Color.FromArgb(128, 224, 48, 48);
    private int failures;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 2 || args[0] is not ("--probe" or "--idle"))
            return 2;

        using var stream = new FileStream(args[1], FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        using var log = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        var probe = new Probe(log);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            try
            {
                probe.LogEnvironment();
                if (args[0] == "--probe")
                    await probe.CheckWindows();
                else
                    await probe.SampleIdle();
                app.Shutdown(probe.failures == 0 ? 0 : 1);
            }
            catch (Exception exception)
            {
                log.WriteLine($"ERROR {exception.GetType().Name}: {exception.Message}");
                app.Shutdown(1);
            }
        };
        return app.Run();
    }

    private void LogEnvironment()
    {
        using var process = Process.GetCurrentProcess();
        nint input = Native.OpenInputDesktop(0, false, 0x0001);
        int inputError = input == 0 ? Marshal.GetLastPInvokeError() : 0;
        try
        {
            log.WriteLine($"utc={DateTimeOffset.UtcNow:O} os={Environment.OSVersion.Version} runtime={Environment.Version} session={process.SessionId} architecture={RuntimeInformation.ProcessArchitecture}");
            log.WriteLine($"station={Native.UserObjectName(Native.GetProcessWindowStation())} threadDesktop={Native.UserObjectName(Native.GetThreadDesktop(Native.GetCurrentThreadId()))} inputDesktop={Native.UserObjectName(input)} inputDesktopError={inputError} foreground=0x{Native.GetForegroundWindow():X}");
        }
        finally
        {
            if (input != 0)
                Native.CloseDesktop(input);
        }
    }

    private async Task CheckWindows()
    {
        var backdrop = CreateFixture("P0 background", WindowStyle.None, ResizeMode.NoResize);
        backdrop.Topmost = true;
        backdrop.Background = new SolidColorBrush(Color.FromRgb(24, 72, 144));
        var dock = CreateDock();
        dock.Topmost = true;
        int activationEvents = 0;
        int renderEvents = 0;
        dock.Activated += (_, _) => activationEvents++;
        dock.ContentRendered += (_, _) => renderEvents++;

        try
        {
            backdrop.Show();
            nint backHandle = Handle(backdrop);
            if (!Native.TryGetMonitor(backHandle, out Native.MonitorInfo monitor))
                throw new InvalidOperationException($"GetMonitorInfo failed ({Marshal.GetLastPInvokeError()})");

            uint backdropDpi = Native.GetDpiForWindow(backHandle);
            int width = Math.Min(monitor.Work.Width - 64, Scale(320, backdropDpi));
            int height = Math.Min(monitor.Work.Height - 64, Scale(150, backdropDpi));
            if (width < Scale(180, backdropDpi) || height < Scale(100, backdropDpi))
                throw new InvalidOperationException("Monitor work area too small for the pixel probe.");

            int left = monitor.Work.Left + (monitor.Work.Width - width) / 2;
            int top = monitor.Work.Top + (monitor.Work.Height - height) / 2;
            Position(backHandle, left, top, width, height);
            await Task.Delay(200);

            for (int attempt = 1; attempt <= 3; attempt++)
            {
                nint before = Native.GetForegroundWindow();
                dock.Show();
                nint dockHandle = Handle(dock);
                Position(dockHandle, left, top, width, height);
                await Task.Delay(350);
                if (attempt == 1)
                    CheckComposition(dock, dockHandle, backHandle, left, top, width, height, renderEvents);

                nint shown = Native.GetForegroundWindow();
                dock.Hide();
                await Task.Delay(150);
                nint hidden = Native.GetForegroundWindow();
                log.WriteLine($"focus iteration={attempt} before=0x{before:X} shown=0x{shown:X} hidden=0x{hidden:X} dock=0x{dockHandle:X} activations={activationEvents} hiddenStyleVisible={Native.IsWindowVisible(dockHandle)}");
                if (shown == dockHandle || hidden == dockHandle || activationEvents != 0)
                    Check(false, $"focus-{attempt}");
                else if (before == 0 || before != shown || before != hidden)
                    log.WriteLine($"UNVERIFIED focus-{attempt} unstable or unavailable foreground baseline");
                else
                    Check(!Native.IsWindowVisible(dockHandle), $"focus-{attempt}");
            }
        }
        finally
        {
            dock.Close();
            backdrop.Close();
        }

        await CheckClassifiers();
    }

    private void CheckComposition(Window dock, nint dockHandle, nint backHandle, int left, int top, int width, int height, int renderEvents)
    {
        int style = Native.GetWindowLong(dockHandle, Native.Style);
        int extended = Native.GetWindowLong(dockHandle, Native.ExtendedStyle);
        bool visible = Native.IsWindowVisible(dockHandle);
        uint dpi = Native.GetDpiForWindow(dockHandle);
        log.WriteLine($"dock hwnd=0x{dockHandle:X} dpi={dpi} visible={visible} style=0x{style:X8} exStyle=0x{extended:X8} renderEvents={renderEvents} showInTaskbar={dock.ShowInTaskbar} renderingTier={RenderCapability.Tier >> 16}");
        Check(visible && renderEvents > 0 && (style & (Native.Caption | Native.ThickFrame)) == 0 &&
              (extended & Native.Layered) != 0 && (extended & Native.AppWindow) == 0, "borderless-layered-render");

        var clear = new Native.Point(left + 2, top + 2);
        var panel = new Native.Point(left + width / 2 - Scale(55, dpi), top + height / 2);
        nint clearHit = Native.WindowFromPoint(clear);
        nint panelHit = Native.WindowFromPoint(panel);
        log.WriteLine($"hit-test clear=0x{clearHit:X} panel=0x{panelHit:X} backdrop=0x{backHandle:X} dock=0x{dockHandle:X}");
        if ((clearHit != backHandle && clearHit != dockHandle) || (panelHit != backHandle && panelHit != dockHandle))
        {
            log.WriteLine("UNVERIFIED window-hit-test probe windows were obscured or inaccessible");
            return;
        }

        Check(clearHit == backHandle && panelHit == dockHandle, "transparent-vs-panel-native-hit-test");
        if (clearHit != backHandle || panelHit != dockHandle)
            return;

        nint dc = Native.GetDC(0);
        if (dc == 0)
        {
            log.WriteLine("UNVERIFIED alpha-composition screen DC unavailable");
            return;
        }

        try
        {
            uint clearPixel = Native.GetPixel(dc, clear.X, clear.Y);
            uint panelPixel = Native.GetPixel(dc, panel.X, panel.Y);
            log.WriteLine($"screen pixels clear=0x{clearPixel:X8} panel=0x{panelPixel:X8} expectedClearColorRef=0x{BackdropColor:X8} expectedPanelRGB=(124,60,96)");
            if (clearPixel == uint.MaxValue || panelPixel == uint.MaxValue)
                log.WriteLine("UNVERIFIED alpha-composition GetPixel returned CLR_INVALID");
            else
                Check(clearPixel == BackdropColor && IsNearRgb(panelPixel, 124, 60, 96), "alpha-composition");
        }
        finally
        {
            Native.ReleaseDC(0, dc);
        }
    }

    private async Task CheckClassifiers()
    {
        var framed = CreateFixture("P0 framed fixture", WindowStyle.SingleBorderWindow, ResizeMode.CanResize);
        var borderless = CreateFixture("P0 borderless fixture", WindowStyle.None, ResizeMode.NoResize);
        try
        {
            framed.Show();
            await Task.Delay(250);
            nint framedHandle = Handle(framed);
            Check(Describe("normal-framed", framedHandle) == "normal", "classify-normal-framed");

            framed.WindowState = WindowState.Maximized;
            await Task.Delay(250);
            Check(Describe("maximized-framed", framedHandle) == "maximized", "classify-maximized-framed");
            framed.Hide();

            borderless.Show();
            nint borderlessHandle = Handle(borderless);
            if (!Native.TryGetMonitor(borderlessHandle, out Native.MonitorInfo monitor))
                throw new InvalidOperationException($"GetMonitorInfo failed ({Marshal.GetLastPInvokeError()})");

            Native.Rect work = monitor.Work;
            Position(borderlessHandle, work.Left, work.Top, work.Width, work.Height);
            await Task.Delay(250);
            string workResult = Describe("borderless-work-area", borderlessHandle);
            if (work.Covers(monitor.Bounds))
                log.WriteLine("UNVERIFIED classify-borderless-work-area work area does not differ from monitor bounds");
            else
                Check(workResult == "normal", "classify-borderless-work-area");

            Native.Rect full = monitor.Bounds;
            int inset = Scale(48, Native.GetDpiForWindow(borderlessHandle));
            Position(borderlessHandle, full.Left, full.Top, full.Width, full.Height - inset);
            await Task.Delay(250);
            Check(Describe("borderless-inset-from-monitor", borderlessHandle) == "normal", "classify-borderless-inset-from-monitor");

            Position(borderlessHandle, full.Left, full.Top, full.Width, full.Height);
            await Task.Delay(250);
            Check(Describe("borderless-monitor", borderlessHandle) == "borderless-candidate", "classify-borderless-monitor");
        }
        finally
        {
            borderless.Close();
            framed.Close();
        }

        Describe("live-foreground-observation", Native.GetForegroundWindow());
    }

    private string Describe(string label, nint handle)
    {
        if (handle == 0 || !Native.IsWindowVisible(handle) || Native.IsIconic(handle))
        {
            log.WriteLine($"window case={label} hwnd=0x{handle:X} classification=unknown reason=missing-hidden-or-minimized");
            return "unknown";
        }

        int style = Native.GetWindowLong(handle, Native.Style);
        int styleError = Marshal.GetLastPInvokeError();
        int cloakResult = Native.GetCloak(handle, out int cloak);
        int frameResult = Native.GetFrame(handle, out Native.Rect frame);
        bool monitored = Native.TryGetMonitor(handle, out Native.MonitorInfo monitor);
        bool zoomed = Native.IsZoomed(handle);
        bool full = monitored && frame.Covers(monitor.Bounds);
        bool chrome = (style & (Native.Caption | Native.ThickFrame)) != 0;
        string classification = "unknown";
        if (styleError == 0 && cloakResult >= 0 && cloak == 0 && frameResult >= 0 &&
            frame.Width > 0 && frame.Height > 0 && monitored)
        {
            if (full && !chrome)
                classification = "borderless-candidate";
            else if (zoomed)
                classification = "maximized";
            else if (!full)
                classification = "normal";
        }

        log.WriteLine($"window case={label} hwnd=0x{handle:X} classification={classification} zoomed={zoomed} style=0x{style:X8} styleError={styleError} cloakHResult=0x{cloakResult:X8} cloak={cloak} frameHResult=0x{frameResult:X8} frame={frame} monitorValid={monitored} monitor={monitor.Bounds} work={monitor.Work} dpiForWindow={Native.GetDpiForWindow(handle)}");
        return classification;
    }

    private async Task SampleIdle()
    {
        var dock = CreateDock();
        try
        {
            dock.Show();
            await Task.Delay(TimeSpan.FromSeconds(2));
            bool initiallyVisible = Native.IsWindowVisible(Handle(dock));
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            TimeSpan initialCpu = process.TotalProcessorTime;
            long initialWorking = process.WorkingSet64;
            long initialPrivate = process.PrivateMemorySize64;
            var watch = Stopwatch.StartNew();
            await Task.Delay(TimeSpan.FromSeconds(30));
            watch.Stop();
            process.Refresh();
            double cpuMs = (process.TotalProcessorTime - initialCpu).TotalMilliseconds;
            double elapsedMs = watch.Elapsed.TotalMilliseconds;
            int logicalCores = Environment.ProcessorCount;
            double machineCpu = 100 * cpuMs / elapsedMs / logicalCores;
            bool finallyVisible = Native.IsWindowVisible(Handle(dock));
            log.WriteLine(FormattableString.Invariant($"idle shownBefore={initiallyVisible} shownAfter={finallyVisible} elapsedMs={elapsedMs:F2} cpuMs={cpuMs:F2} processorCount={logicalCores} singleCorePercent={100 * cpuMs / elapsedMs:F4} normalizedPercent={machineCpu:F4} workingBeforeBytes={initialWorking} workingAfterBytes={process.WorkingSet64} privateBeforeBytes={initialPrivate} privateAfterBytes={process.PrivateMemorySize64}"));
            Check(initiallyVisible && finallyVisible && elapsedMs >= 30_000 && machineCpu <= 1 &&
                  Math.Max(initialWorking, process.WorkingSet64) <= 150 * 1024 * 1024, "idle-baseline");
        }
        finally
        {
            dock.Close();
        }
    }

    private static Window CreateDock()
    {
        var window = CreateFixture("P0 WPF transparency", WindowStyle.None, ResizeMode.NoResize);
        window.AllowsTransparency = true;
        window.Background = Brushes.Transparent;
        window.Content = new Border
        {
            Width = 156,
            Height = 68,
            CornerRadius = new CornerRadius(8),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(PanelColor),
            Child = new TextBlock
            {
                Text = "P0 WPF",
                Foreground = Brushes.White,
                FontSize = 14,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        return window;
    }

    private static Window CreateFixture(string title, WindowStyle style, ResizeMode resizeMode) => new()
    {
        Title = title,
        Width = 320,
        Height = 150,
        Left = 80,
        Top = 80,
        WindowStyle = style,
        ResizeMode = resizeMode,
        ShowActivated = false,
        ShowInTaskbar = false
    };

    private static nint Handle(Window window) => new WindowInteropHelper(window).Handle;

    private static int Scale(int dip, uint dpi) => (int)Math.Round(dip * (dpi / 96d));

    private static void Position(nint hwnd, int left, int top, int width, int height)
    {
        if (!Native.SetWindowPos(hwnd, 0, left, top, width, height, NoActivateNoZOrder))
            throw new InvalidOperationException($"SetWindowPos failed ({Marshal.GetLastPInvokeError()})");
    }

    private static bool IsNearRgb(uint pixel, int red, int green, int blue) =>
        Math.Abs((int)(pixel & 0xff) - red) <= 3 &&
        Math.Abs((int)((pixel >> 8) & 0xff) - green) <= 3 &&
        Math.Abs((int)((pixel >> 16) & 0xff) - blue) <= 3;

    private void Check(bool condition, string name)
    {
        log.WriteLine($"{(condition ? "PASS" : "FAIL")} {name}");
        if (!condition)
            failures++;
    }
}
