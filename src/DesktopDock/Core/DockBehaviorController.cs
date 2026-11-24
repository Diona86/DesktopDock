namespace DesktopDock;

public enum DockVisibilityState { Hidden, Resident, Revealed, Temporary, Locked }

public sealed class DockVisibilityController
{
    private const int MaximumSampleGapMs = 300;
    private const int ManualRevealGraceMs = 1500;
    private ResolvedDockHotZone? pendingZone;
    private long pendingSince;
    private long? outsideSince;
    private long? graceSince;
    private long? lastSample;
    private bool waitForLeave;

    public DockVisibilityController(DockMode mode, bool showOnStartup = true)
    {
        Mode = mode;
        State = mode == DockMode.Resident && showOnStartup ? DockVisibilityState.Resident : DockVisibilityState.Hidden;
    }

    public DockMode Mode { get; private set; }
    public DockVisibilityState State { get; private set; }
    public bool IsVisible => State != DockVisibilityState.Hidden;
    public bool HotZonesPaused { get; private set; }
    public string? MonitorId { get; private set; }
    public Guid? PendingZoneId => pendingZone?.Zone.Id;
    public Guid? ActiveZoneId { get; private set; }
    public int HideDelayMs { get; private set; } = DockHotZoneGeometry.DefaultHideDelayMs;

    public void SetMode(DockMode mode)
    {
        if (Mode == mode)
            return;

        Mode = mode;
        if (mode == DockMode.Resident)
            Show(DockVisibilityState.Resident, null);
        else
            Hide(requireLeave: false);
    }

    public void SetVisible(bool visible, long now, string? monitorId = null)
    {
        if (!visible)
            Hide(requireLeave: true);
        else if (!IsVisible)
            Show(Mode == DockMode.Resident ? DockVisibilityState.Resident : DockVisibilityState.Revealed,
                monitorId, manualAt: now);
    }

    public void Toggle(long now, string? monitorId = null) => SetVisible(!IsVisible, now, monitorId);

    public void Peek(long now, string? monitorId = null)
    {
        if (State is DockVisibilityState.Resident or DockVisibilityState.Locked)
            return;
        Show(DockVisibilityState.Temporary, monitorId ?? MonitorId, HideDelayMs, ActiveZoneId, now);
    }

    public void ToggleLock(long now, string? monitorId = null)
    {
        if (State == DockVisibilityState.Locked)
        {
            Show(Mode == DockMode.Resident ? DockVisibilityState.Resident : DockVisibilityState.Revealed,
                MonitorId, HideDelayMs, ActiveZoneId, now);
        }
        else
        {
            Show(DockVisibilityState.Locked, MonitorId ?? monitorId, HideDelayMs, ActiveZoneId);
        }
    }

    public void TogglePause()
    {
        HotZonesPaused = !HotZonesPaused;
        ResetDwell();
    }

    public void Dismiss()
    {
        if (Mode == DockMode.AutoHide || State == DockVisibilityState.Temporary)
            Hide(requireLeave: true);
    }

    public void ResetDwell() => pendingZone = null;

    public void Tick(long now, ResolvedDockHotZone? zone, bool automaticAllowed, bool insideInteraction)
    {
        bool gap = lastSample is long previous && (now < previous || now - previous > MaximumSampleGapMs);
        lastSample = now;
        if (gap)
        {
            ResetDwell();
            outsideSince = null;
        }
        if (zone == null)
            waitForLeave = false;

        if (State is DockVisibilityState.Resident or DockVisibilityState.Locked)
            return;

        if (IsVisible)
        {
            SampleVisible(now, zone, insideInteraction);
            return;
        }

        if (Mode != DockMode.AutoHide || zone == null || !automaticAllowed || HotZonesPaused || waitForLeave)
        {
            ResetDwell();
            return;
        }

        if (pendingZone != zone)
        {
            pendingZone = zone;
            pendingSince = now;
        }
        if (now - pendingSince >= zone.Zone.RevealDelayMs)
            Show(DockVisibilityState.Revealed, zone.MonitorId, zone.Zone.HideDelayMs, zone.Zone.Id);
    }

    private void SampleVisible(long now, ResolvedDockHotZone? zone, bool insideInteraction)
    {
        if (insideInteraction)
        {
            outsideSince = null;
            graceSince = null;
            return;
        }
        if (graceSince is long start && now >= start && now - start < ManualRevealGraceMs)
            return;

        outsideSince ??= now;
        if (now - outsideSince >= HideDelayMs)
            Hide(requireLeave: zone != null);
    }

    private void Show(DockVisibilityState state, string? monitorId,
        int hideDelay = DockHotZoneGeometry.DefaultHideDelayMs, Guid? zoneId = null, long? manualAt = null)
    {
        State = state;
        MonitorId = monitorId;
        HideDelayMs = hideDelay;
        ActiveZoneId = zoneId;
        graceSince = manualAt;
        outsideSince = null;
        waitForLeave = false;
        ResetDwell();
    }

    private void Hide(bool requireLeave)
    {
        Show(DockVisibilityState.Hidden, null);
        waitForLeave = requireLeave;
    }
}
