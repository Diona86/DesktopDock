using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

internal static class Program
{
    private const int HotkeyId = 1;
    private const uint HotkeyModifiers = 0x4007; // Ctrl+Alt+Shift, no repeat.
    private const uint HotkeyKey = 0x79; // F10.

    private static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        bool listen = args.Length > 0 && args[0] == "--listen";
        if (args.Length > 0 && (!listen || args.Length != 1))
        {
            Console.Error.WriteLine("Usage: P0.Input [--listen]");
            return 2;
        }

        using var process = Process.GetCurrentProcess();
        Log($"start os={Environment.OSVersion.Version} runtime={Environment.Version} session={process.SessionId} " +
            $"interactive={Environment.UserInteractive} mode={(listen ? "manual-input" : "automatic")}");

        bool checksPassed = VerifyGeometryAndTiming();
        nint previousDpiContext = Native.SetThreadDpiAwarenessContext(Native.PerMonitorV2);
        if (previousDpiContext == 0)
            Log($"monitor pmv2Thread=False win32={Marshal.GetLastPInvokeError()}");

        try
        {
            var screens = ReadScreens();
            bool cleanupsPassed = VerifyHotkeyRegistration();
            ObserveInput(process, screens, listen);
            return checksPassed && cleanupsPassed ? 0 : 1;
        }
        finally
        {
            if (previousDpiContext != 0 && Native.SetThreadDpiAwarenessContext(previousDpiContext) == 0)
                Log($"monitor restoreThreadDpiContext=False win32={Marshal.GetLastPInvokeError()}");
        }
    }

    private static List<PixelRect> ReadScreens()
    {
        const uint wsPopup = 0x80000000;
        const uint wsExNoActivateToolWindow = 0x08000080;
        var handles = new List<nint>();
        var screens = new List<PixelRect>();
        bool enumerated = Native.EnumDisplayMonitors(0, 0, (monitor, _, _, _) => { handles.Add(monitor); return true; }, 0);
        Log($"monitor enumeration={enumerated} count={handles.Count}");

        foreach (nint monitor in handles)
        {
            var info = new Native.MonitorInfo { Size = (uint)Marshal.SizeOf<Native.MonitorInfo>() };
            if (!Native.GetMonitorInfo(monitor, ref info))
            {
                Log("monitor getInfo=False");
                continue;
            }

            PixelRect bounds = info.Monitor.AsPixels();
            screens.Add(bounds);
            var device = new Native.DisplayDevice { Size = (uint)Marshal.SizeOf<Native.DisplayDevice>() };
            bool resolvedDevice = Native.EnumDisplayDevices(info.Device, 0, ref device, 1) && !string.IsNullOrEmpty(device.Id);
            string deviceFingerprint = resolvedDevice
                ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(device.Id)))[..12]
                : "unavailable";
            Log($"monitor device={info.Device} interfaceFingerprint={deviceFingerprint} primary={(info.Flags & 1) != 0} " +
                $"boundsPx={bounds} workPx={info.Work.AsPixels()}");

            nint probe = Native.CreateWindowEx(wsExNoActivateToolWindow, "STATIC", null, wsPopup,
                bounds.Left, bounds.Top, 1, 1, 0, 0, 0, 0);
            if (probe == 0)
            {
                Log($"monitor device={info.Device} hiddenProbe=False win32={Marshal.GetLastPInvokeError()}");
                continue;
            }

            try
            {
                bool expectedMonitor = Native.MonitorFromWindow(probe, 0) == monitor;
                bool perMonitor = Native.AreDpiAwarenessContextsEqual(Native.GetWindowDpiAwarenessContext(probe), Native.PerMonitorV2);
                uint dpi = Native.GetDpiForWindow(probe);
                string widthDip = dpi == 0 ? "unavailable" : $"{bounds.Width * 96.0 / dpi:F2}";
                Log($"monitor device={info.Device} pmv2={perMonitor} monitorMatch={expectedMonitor} dpi={dpi} widthDip={widthDip}");
            }
            finally
            {
                if (!Native.DestroyWindow(probe))
                    Log($"monitor destroyProbe=False win32={Marshal.GetLastPInvokeError()}");
            }
        }

        return screens;
    }

    private static bool VerifyHotkeyRegistration()
    {
        bool first = Register(HotkeyId, "first");
        if (!first)
            return false;

        bool released = false;
        bool conflict;
        try
        {
            conflict = CheckCompetingOwner();
        }
        finally
        {
            released = Release(HotkeyId, "first");
        }

        bool repeat = released && Register(HotkeyId, "after-release");
        bool finalRelease = repeat && Release(HotkeyId, "after-release");
        return conflict && released && repeat && finalRelease;
    }

    private static bool CheckCompetingOwner()
    {
        const int duplicateId = 2;
        bool conflict = false;
        var competingThread = new Thread(() =>
        {
            bool duplicate = Native.RegisterHotKey(0, duplicateId, HotkeyModifiers, HotkeyKey);
            int duplicateError = Marshal.GetLastPInvokeError();
            conflict = !duplicate && duplicateError == 1409;
            Log($"hotkey duplicate ownerThread={Environment.CurrentManagedThreadId} success={duplicate} " +
                $"win32={(duplicate ? 0 : duplicateError)} conflictVerified={conflict}");
            if (duplicate)
                Release(duplicateId, "unexpected-duplicate");
        });
        competingThread.Start();
        competingThread.Join();
        return conflict;
    }

    private static bool Register(int id, string phase)
    {
        bool registered = Native.RegisterHotKey(0, id, HotkeyModifiers, HotkeyKey);
        int error = Marshal.GetLastPInvokeError();
        Log($"hotkey register phase={phase} ownerThread={Environment.CurrentManagedThreadId} " +
            $"combination=Ctrl+Alt+Shift+F10 success={registered} win32={(registered ? 0 : error)}");
        return registered;
    }

    private static bool Release(int id, string phase)
    {
        bool released = Native.UnregisterHotKey(0, id);
        int error = Marshal.GetLastPInvokeError();
        Log($"hotkey unregister phase={phase} success={released} win32={(released ? 0 : error)}");
        return released;
    }

    private static void ObserveInput(Process process, List<PixelRect> screens, bool listen)
    {
        const int intervalMs = 25;
        var zones = new List<HotZone>();
        for (int index = 0; index < screens.Count; index++)
        {
            var screen = screens[index];
            if (screen.Width < 4 || screen.Height < 6)
                continue;

            zones.Add(new HotZone($"{index + 1}-top-a",
                HotZone.Place(screen, Edge.Top, screen.Width / 4, screen.Width * 3 / 4, 6, 0), 250, 400));
            zones.Add(new HotZone($"{index + 1}-top-b",
                HotZone.Place(screen, Edge.Top, screen.Width / 2, screen.Width, 6, 0), 350, 400));
        }

        foreach (var zone in zones)
            Log($"mouse native zone={zone.Id} boundsPx={zone.Bounds} triggerMs={zone.EnterMs} hideMs={zone.ExitMs}");

        var tracker = new HotZoneTracker([.. zones], []);
        bool listening = listen && Register(HotkeyId, "manual-delivery");
        if (listen)
            Log("manual for 45s: hold the pointer in a logged zone, move away, and optionally press Ctrl+Alt+Shift+F10");

        try
        {
            int durationMs = listen ? 45_000 : 30_000;
            bool firstRead = Native.GetCursorPos(out _);
            int firstError = Marshal.GetLastPInvokeError();
            Log($"mouse native initialPositionRead={firstRead} win32={(firstRead ? 0 : firstError)}");
            Thread.Sleep(500);
            process.Refresh();
            var cpuBefore = process.TotalProcessorTime;
            var timer = Stopwatch.StartNew();
            int reads = 0, failedReads = 0, transitions = 0, messages = 0, changedReads = 0;
            long previousSampleMs = 0, maxIntervalMs = 0;
            Native.Point previousPoint = default;

            while (timer.ElapsedMilliseconds < durationMs)
            {
                long now = timer.ElapsedMilliseconds;
                maxIntervalMs = Math.Max(maxIntervalMs, now - previousSampleMs);
                previousSampleMs = now;
                if (Native.GetCursorPos(out var point))
                {
                    if (reads > 0 && (point.X != previousPoint.X || point.Y != previousPoint.Y))
                        changedReads++;
                    previousPoint = point;
                    reads++;
                    var action = tracker.Sample(point.X, point.Y, now);
                    if (action.HasValue)
                    {
                        transitions++;
                        Log($"mouse native action={action.Value.Action} zone={action.Value.Zone} observedDwellMs={action.Value.ElapsedMs}");
                    }
                }
                else if (++failedReads == 1)
                {
                    Log($"mouse native firstSampleFailure win32={Marshal.GetLastPInvokeError()}");
                }

                while (listening && Native.PeekMessage(out var message, 0, Native.WmHotkey, Native.WmHotkey, 1))
                {
                    if (message.Id == Native.WmHotkey && message.WParam == HotkeyId)
                    {
                        messages++;
                        Log($"hotkey native WM_HOTKEY id={message.WParam}");
                    }
                }

                Thread.Sleep(intervalMs);
            }

            double elapsedMs = timer.Elapsed.TotalMilliseconds;
            process.Refresh();
            double cpuMs = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
            double corePercent = 100 * cpuMs / elapsedMs;
            Log($"mouse native pollMs={intervalMs} maxIntervalMs={maxIntervalMs} reads={reads} movingSamples={changedReads} failures={failedReads} transitions={transitions}");
            Log($"hotkey native registeredForDelivery={listening} messageCount={messages}");
            Log($"performance polling wallMs={elapsedMs:F1} cpuMs={cpuMs:F1} cpuPctOneCore={corePercent:F3} " +
                $"logicalCpus={Environment.ProcessorCount} workingSetMiB={process.WorkingSet64 / 1048576.0:F2} " +
                $"privateMiB={process.PrivateMemorySize64 / 1048576.0:F2}");
        }
        finally
        {
            if (listening)
                Release(HotkeyId, "manual-delivery");
        }
    }

    private static bool VerifyGeometryAndTiming()
    {
        bool passed = true;
        void Expect(string caseId, bool result)
        {
            Log($"controlled {caseId} pass={result}");
            passed &= result;
        }

        var negative = new PixelRect(-1920, -1080, 0, 0);
        var top = HotZone.Place(negative, Edge.Top, 100, 300, 6, 3);
        var bottom = HotZone.Place(negative, Edge.Bottom, 100, 300, 6, 3);
        var left = HotZone.Place(negative, Edge.Left, 100, 300, 6, 3);
        var right = HotZone.Place(negative, Edge.Right, 100, 300, 6, 3);
        Expect("four-edges-negative-origin",
            top == new PixelRect(-1820, -1077, -1620, -1071) &&
            bottom == new PixelRect(-1820, -9, -1620, -3) &&
            left == new PixelRect(-1917, -980, -1911, -780) &&
            right == new PixelRect(-9, -980, -3, -780));
        Expect("half-open-edges", top.Contains(-1820, -1077) && top.Contains(-1621, -1072) &&
            !top.Contains(-1620, -1072) && !top.Contains(-1621, -1071) && !top.Contains(-1820, -1078));

        foreach (var (dpi, expectedPx) in new[] { (96, 128), (120, 160), (144, 192), (192, 256) })
        {
            const double localDip = 128;
            double localPx = localDip * dpi / 96.0;
            double desktopPx = negative.Left + localPx;
            double returnedDip = (desktopPx - negative.Left) * 96.0 / dpi;
            Expect($"local-dip-roundtrip dpi={dpi} dip={localDip} localPx={localPx} desktopPx={desktopPx}",
                localPx == expectedPx && returnedDip == localDip);
        }

        var a = new HotZone("a", new PixelRect(-100, 0, 0, 6), 250, 400);
        var b = new HotZone("b", new PixelRect(-50, 0, 100, 6), 350, 400);
        var tracker = new HotZoneTracker([a, b], [new(-100, 6, 100, 40)]);
        Expect("start-first-zone", tracker.Sample(-75, 2, 0) is null);
        Expect("short-edge-visit-cancels", tracker.Sample(-75, 2, 249) is null && tracker.Sample(500, 500, 249) is null);
        Expect("overlap-first-listed-zone", tracker.Sample(-25, 2, 300) is null && tracker.Sample(-25, 2, 549) is null &&
            tracker.Sample(-25, 2, 550) == ("show", "a", 250L));
        Expect("interaction-area-defers-hide", tracker.Sample(-25, 20, 600) is null && tracker.Sample(-25, 20, 1200) is null);
        Expect("outside-delay-resets-on-return", tracker.Sample(500, 500, 1300) is null && tracker.Sample(-25, 20, 1699) is null &&
            tracker.Sample(500, 500, 1700) is null && tracker.Sample(500, 500, 2099) is null &&
            tracker.Sample(500, 500, 2100) == ("hide", "a", 400L));
        Expect("zone-switch-resets-enter-clock", tracker.Sample(-75, 2, 2200) is null && tracker.Sample(50, 2, 2449) is null &&
            tracker.Sample(50, 2, 2798) is null && tracker.Sample(50, 2, 2799) == ("show", "b", 350L));
        return passed;
    }

    private static void Log(string message) => Console.WriteLine($"{DateTimeOffset.Now:O} {message}");
}
