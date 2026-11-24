using System.Windows.Threading;

namespace DesktopDock;

internal sealed class DockInteractionRuntime : IDisposable
{
    private const int PointerIntervalMs = 40;
    private const int ForegroundIntervalMs = 160;
    private const int DisplayIntervalMs = 3000;
    private readonly DockWindow dock;
    private readonly DockSession session;
    private readonly bool validationMode;
    private readonly Action<bool> render;
    private readonly DispatcherTimer timer;
    private readonly DockVisibilityController visibility;
    private readonly DockOutsideClickMonitor outsideClicks;
    private DockHotkeyService? hotkeys;
    private string? hotkeyStartupFailure;
    private DockOptions options;
    private IReadOnlyList<DockMonitor> physicalMonitors;
    private IReadOnlyList<DockMonitor>? validationMonitors;
    private ResolvedDockHotZone[] zones = [];
    private DockWindowObservation? foreground;
    private string? foregroundMonitor;
    private long nextForeground;
    private long nextDisplays;
    private long? validationTime;
    private string? validationPointerMonitor;
    private bool nativeSamplingForTest;
    private bool outsideRequested;
    private bool mouseDown;
    private bool escapeDown;
    private int outsideHookError;
    private long pointerSamples;
    private long foregroundSamples;
    private long automaticOpens;
    private bool modalInteraction;

    internal DockInteractionRuntime(DockWindow dock, DockSession session, bool validationMode, Action<bool> render,
        Func<DockHotkeyAction, bool> captureShortcut)
    {
        this.dock = dock;
        this.session = session;
        this.validationMode = validationMode;
        this.render = render;
        options = session.Options;
        physicalMonitors = DockWindows.GetMonitors();
        visibility = new DockVisibilityController(options.Behavior.Mode, options.ShowDockOnStartup);
        timer = new DispatcherTimer(DispatcherPriority.Background, dock.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(PointerIntervalMs)
        };
        timer.Tick += SampleNative;
        outsideClicks = new DockOutsideClickMonitor((x, y) => OutsideClick(x, y));
        dock.EscapeRequested = HandleEscape;
        session.Changed += OptionsChanged;
        RebuildZones();

        if (!validationMode)
        {
            try
            {
                hotkeys = new DockHotkeyService(action =>
                {
                    if (!captureShortcut(action))
                        Shortcut(action);
                });
                hotkeys.ApplyAtStartup(options.Behavior.Hotkeys);
            }
            catch (Exception error) when (DockDialogs.IsUserFacing(error))
            {
                hotkeyStartupFailure = error.Message;
            }
        }
        Publish();
    }

    internal IReadOnlyList<DockMonitor> Monitors => validationMonitors ?? physicalMonitors;
    internal IReadOnlyList<ResolvedDockHotZone> Zones => zones;
    internal string? HotkeyStatus => hotkeyStartupFailure ?? (hotkeys?.Errors.Count > 0 ? string.Join("\n", hotkeys.Errors) : null);
    internal bool HotZonesPaused => visibility.HotZonesPaused;

    internal IReadOnlyList<DockMonitor> ReadMonitors()
    {
        if (validationMonitors == null)
            RefreshMonitors();
        return Monitors;
    }

    private bool NativeSampling => !validationMode || nativeSamplingForTest;
    private long Now => validationTime ?? Environment.TickCount64;
    private bool Dismissible => visibility.IsVisible && (visibility.Mode == DockMode.AutoHide || visibility.State == DockVisibilityState.Temporary);

    internal string? Apply(DockBehaviorOptions behavior, bool retryShortcuts)
    {
        try
        {
            behavior.Validate();
            void Persist() => session.UpdateOptions(session.Options with { Behavior = behavior });

            bool needsRegistration = behavior.Hotkeys != options.Behavior.Hotkeys || (retryShortcuts && HotkeyStatus != null);
            if (!validationMode && needsRegistration)
            {
                if (hotkeys == null)
                    return hotkeyStartupFailure ?? "全局快捷键服务不可用。";
                if (!hotkeys.TryApply(behavior.Hotkeys, Persist, out string? error))
                    return error ?? "无法注册全局快捷键。";
            }
            else
            {
                Persist();
            }
            return null;
        }
        catch (Exception error) when (DockDialogs.IsUserFacing(error))
        {
            return error.Message;
        }
    }

    internal void SetVisible(bool visible)
    {
        if (visible && !visibility.IsVisible)
            RefreshForegroundForManual();
        visibility.SetVisible(visible, Now, PointerMonitorId());
        Publish();
    }

    internal void Shortcut(DockHotkeyAction action)
    {
        string? monitor = PointerMonitorId();
        if (action != DockHotkeyAction.Pause && !visibility.IsVisible)
            RefreshForegroundForManual();
        switch (action)
        {
            case DockHotkeyAction.Toggle: visibility.Toggle(Now, monitor); break;
            case DockHotkeyAction.Peek: visibility.Peek(Now, monitor); break;
            case DockHotkeyAction.Lock: visibility.ToggleLock(Now, monitor); break;
            case DockHotkeyAction.Pause: visibility.TogglePause(); break;
        }
        Publish();
    }

    internal void SetModalInteraction(bool active) => modalInteraction = active;

    internal bool OutsideClick(int x, int y)
    {
        if (!Dismissible || modalInteraction || dock.ContainsInteraction(x, y, forClick: true))
            return false;
        dock.CloseInteractionMenus();
        visibility.Dismiss();
        Publish();
        return true;
    }

    internal bool HandleEscape()
    {
        escapeDown = true;
        if (dock.CloseTopInteraction())
            return true;
        if (!Dismissible || modalInteraction)
            return false;
        visibility.Dismiss();
        Publish();
        return true;
    }

    internal void SetTestMonitors(IReadOnlyList<DockMonitor>? monitors)
    {
        validationMonitors = monitors;
        visibility.ResetDwell();
        RebuildZones();
    }

    internal void StepForTest(long now, int x, int y, DockWindowObservation state, bool? inside)
    {
        if (nativeSamplingForTest || (validationTime.HasValue && now < validationTime.Value))
            throw new ArgumentException("Use increasing test time with native sampling off.");
        validationTime = now;
        validationPointerMonitor = MonitorAt(x, y)?.Id;
        Sample(now, x, y, state, inside);
    }

    internal void SetNativeSamplingForTest(bool enabled)
    {
        validationTime = null;
        validationPointerMonitor = null;
        nativeSamplingForTest = enabled;
        Publish();
    }

    internal object Diagnostics() => new
    {
        mode = visibility.Mode,
        state = visibility.State,
        paused = visibility.HotZonesPaused,
        monitorId = visibility.MonitorId,
        pendingZoneId = visibility.PendingZoneId,
        activeZoneId = visibility.ActiveZoneId,
        hideDelayMs = visibility.HideDelayMs,
        outsideHookError,
        pointerSamples,
        foregroundSamples,
        automaticOpens,
        foreground,
        hotkeyStatus = HotkeyStatus
    };

    private void OptionsChanged(object? sender, EventArgs args)
    {
        DockOptions next = session.Options;
        if (next.Behavior == options.Behavior && next.Placement == options.Placement)
            return;
        options = next;
        visibility.SetMode(options.Behavior.Mode);
        visibility.ResetDwell();
        foreground = null;
        RebuildZones();
        Publish();
    }

    private void RebuildZones() => zones = Monitors.SelectMany(monitor =>
        DockHotZoneGeometry.ResolveZones(options.Behavior, options.Placement, monitor)).ToArray();

    private DockMonitor? MonitorAt(int x, int y) => Monitors.FirstOrDefault(m => x >= m.Left && x < m.Right && y >= m.Top && y < m.Bottom);

    private string? PointerMonitorId()
    {
        if (validationMode && !nativeSamplingForTest)
            return validationPointerMonitor;
        return DockWindows.TryGetPointer(out int x, out int y) ? MonitorAt(x, y)?.Id : null;
    }

    private void RefreshForegroundForManual()
    {
        if (!NativeSampling)
            return;
        DockMonitor? monitor = Monitors.FirstOrDefault(m => m.Id == PointerMonitorId()) ?? Monitors.FirstOrDefault();
        foreground = monitor == null ? null : ReadForeground(monitor, Now);
    }

    private DockWindowObservation ReadForeground(DockMonitor monitor, long now)
    {
        foregroundSamples++;
        foreground = DockWindows.ReadForeground(monitor);
        foregroundMonitor = monitor.Id;
        nextForeground = now + ForegroundIntervalMs;
        return foreground;
    }

    private void SampleNative(object? sender, EventArgs args)
    {
        long now = Environment.TickCount64;
        if (now >= nextDisplays)
        {
            nextDisplays = now + DisplayIntervalMs;
            RefreshMonitors();
        }
        if (!DockWindows.TryGetPointer(out int x, out int y))
        {
            visibility.ResetDwell();
            return;
        }
        Sample(now, x, y, null, null);

        if (dock.Topmost && Dismissible && now >= nextForeground)
        {
            DockMonitor? monitor = physicalMonitors.FirstOrDefault(item => item.Id == visibility.MonitorId)
                ?? physicalMonitors.FirstOrDefault();
            if (monitor != null)
            {
                DockWindowObservation observed = ReadForeground(monitor, now);
                if (!CanFloatAbove(observed))
                    dock.Topmost = false;
            }
        }

        if (Dismissible)
        {
            bool escape = DockWindows.IsVirtualKeyDown(0x1B);
            if (escape && !escapeDown)
                HandleEscape();
            escapeDown = escape;
            if (outsideHookError != 0)
            {
                bool down = IsMouseButtonDown();
                if (down && !mouseDown)
                    OutsideClick(x, y);
                mouseDown = down;
            }
        }
    }

    private void RefreshMonitors()
    {
        IReadOnlyList<DockMonitor> updated = DockWindows.GetMonitors();
        if (physicalMonitors.SequenceEqual(updated))
            return;
        physicalMonitors = updated;
        visibility.ResetDwell();
        RebuildZones();
        Publish();
    }

    private void Sample(long now, int x, int y, DockWindowObservation? testObservation, bool? testInside)
    {
        pointerSamples++;
        ResolvedDockHotZone? zone = DockHotZoneGeometry.HitTest(zones, x, y);
        DockMonitor? monitor = MonitorAt(x, y);
        bool allowed = false;
        if (testObservation != null)
        {
            foreground = testObservation;
            allowed = testObservation.CanAutoReveal(options.Behavior.ApplicationRules);
        }
        else if (!visibility.IsVisible && !visibility.HotZonesPaused && zone != null && monitor != null)
        {
            DockWindowObservation observation = foreground == null || foregroundMonitor != monitor.Id || now >= nextForeground
                ? ReadForeground(monitor, now) : foreground;
            allowed = observation.CanAutoReveal(options.Behavior.ApplicationRules);
        }

        ResolvedDockHotZone? active = visibility.ActiveZoneId is Guid activeId
            ? zones.FirstOrDefault(item => item.Zone.Id == activeId && item.MonitorId == visibility.MonitorId) : null;
        bool inside = testInside ?? (modalInteraction || dock.ContainsInteraction(x, y) || (active != null && dock.ContainsRevealApproach(active, x, y)));
        DockVisibilityState previous = visibility.State;
        visibility.Tick(now, zone, allowed, inside);

        if (previous == DockVisibilityState.Hidden && visibility.State == DockVisibilityState.Revealed)
        {
            // Recheck before the HWND becomes visible even if the dwell used a cached observation.
            if (testObservation == null && (monitor == null || !ReadForeground(monitor, now).CanAutoReveal(options.Behavior.ApplicationRules)))
                visibility.Dismiss();
            else
                automaticOpens++;
        }
        if (visibility.State != previous)
            Publish();
    }

    private void Publish()
    {
        bool temporary = visibility.State is DockVisibilityState.Temporary or DockVisibilityState.Revealed or DockVisibilityState.Locked;
        if (visibility.IsVisible)
        {
            DockMonitor? monitor = temporary ? physicalMonitors.FirstOrDefault(m => m.Id == visibility.MonitorId) : null;
            dock.SetRevealMonitor(monitor);
            dock.Topmost = temporary && CanFloatAbove(foreground);
        }
        else
        {
            dock.Topmost = false;
        }
        render(visibility.IsVisible);

        bool wantOutside = NativeSampling && Dismissible;
        if (outsideRequested != wantOutside)
        {
            outsideRequested = wantOutside;
            if (wantOutside)
            {
                outsideClicks.TryStart(out outsideHookError);
                mouseDown = IsMouseButtonDown();
            }
            else
            {
                outsideClicks.Stop();
                outsideHookError = 0;
                escapeDown = false;
            }
        }

        bool wantTimer = NativeSampling && (visibility.State == DockVisibilityState.Temporary ||
            visibility.Mode == DockMode.AutoHide && (!visibility.HotZonesPaused || visibility.IsVisible));
        if (wantTimer && !timer.IsEnabled)
            timer.Start();
        else if (!wantTimer && timer.IsEnabled)
            timer.Stop();
    }

    private bool CanFloatAbove(DockWindowObservation? observation) => observation != null
        && observation.State is not (DockWindowState.ExclusiveFullScreen or DockWindowState.Unknown)
        && (visibility.ActiveZoneId == null || observation.CanAutoReveal(options.Behavior.ApplicationRules));

    private static bool IsMouseButtonDown() => DockWindows.IsVirtualKeyDown(1) || DockWindows.IsVirtualKeyDown(2) || DockWindows.IsVirtualKeyDown(4);

    public void Dispose()
    {
        session.Changed -= OptionsChanged;
        timer.Stop();
        outsideClicks.Dispose();
        hotkeys?.Dispose();
    }
}
