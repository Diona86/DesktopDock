using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace DesktopDock;

public partial class App
{
    private sealed class DisplayDock(DockSession profile, string themeId)
    {
        internal DockSession Profile { get; } = profile;
        internal string ThemeId { get; } = themeId;
        internal DockWindow Window { get; set; } = null!;
        internal DockInteractionRuntime Runtime { get; set; } = null!;
        internal DockMonitor? Monitor { get; set; }
        internal bool RequestedVisible { get; set; }
        internal bool Removing { get; set; }
    }

    private readonly Dictionary<string, DisplayDock> displayDocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly DockIconService sharedIcons = new();
    private DockDisplayProfiles? displayProfiles;
    private DockDisplayInput? displayInput;
    private DisplayDock? legacyDock;
    private DisplayDock? activeDock;
    private DispatcherTimer? displayRefreshTimer;
    private DockMonitor[] displayTopology = [];

    private IEnumerable<DisplayDock> ConnectedDocks => displayTopology.Length == 0 && legacyDock != null
        ? [legacyDock] : displayDocks.Values;

    private void InitializeDisplays(string directory, DockSession rootProfile)
    {
        displayProfiles = DockDisplayProfiles.Open(directory);
        if (!validationMode)
            displayInput = new DockDisplayInput();
        DockMonitor[] topology = validationMode ? [] : DockWindows.GetMonitors().ToArray();
        IReadOnlyList<DockDisplayProfile> profiles = displayProfiles.Resolve(topology, rootProfile.Options.FloatingMonitorId);
        DockMonitor? home = profiles.FirstOrDefault(profile => profile.UsesLegacyProfile)?.Monitor;
        legacyDock = CreateDisplayDock(rootProfile, DockThemeState.PrimaryDockId, home,
            ownsInput: true, bound: !validationMode && topology.Length > 0);
        MainWindow = legacyDock.Window;
        ActivateDock(legacyDock);

        if (!validationMode)
        {
            ApplyTopology(topology, profiles);
            SystemEvents.DisplaySettingsChanged += SystemDisplayChanged;
            displayRefreshTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            {
                Interval = TimeSpan.FromSeconds(2)
            };
            displayRefreshTimer.Tick += RefreshDisplays;
            displayRefreshTimer.Start();
            if (displayProfiles.LoadError is string error)
                Dispatcher.BeginInvoke(() => MessageBox.Show(error, "Desktop Dock", MessageBoxButton.OK, MessageBoxImage.Warning));
        }
    }

    private DisplayDock CreateDisplayDock(DockSession profile, string themeId, DockMonitor? monitor, bool ownsInput, bool bound)
    {
        var instance = new DisplayDock(profile, themeId) { Monitor = monitor };
        instance.Window = new DockWindow(profile, sharedIcons,
            () => ShowSettingsFor(instance), item => EditItemFor(instance, item), item => ReplaceIconFor(instance, item),
            () => instance.Runtime.SetVisible(false));
        instance.Window.GlobalPositionLock = () => legacyDock?.Profile.Options.GlobalPositionLocked ?? profile.Options.GlobalPositionLocked;
        instance.Window.Closing += (_, args) =>
        {
            if (exiting || instance.Removing)
                return;
            args.Cancel = true;
            Dispatcher.BeginInvoke(() => instance.Runtime.SetVisible(false));
        };
        ApplyThemeTo(instance.Window, instance.ThemeId);
        instance.Runtime = new DockInteractionRuntime(instance.Window, profile, validationMode,
            visible => RenderDockVisibility(instance, visible), CaptureShortcut, assignedMonitor: monitor,
            ownsGlobalInput: ownsInput, routeGlobalShortcut: RouteGlobalShortcut, sharedInput: displayInput, monitorBound: bound);
        return instance;
    }

    private void SystemDisplayChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(
        DispatcherPriority.Background, new Action(() => RefreshDisplays(null, EventArgs.Empty)));

    private void RefreshDisplays(object? sender, EventArgs args)
    {
        if (exiting || displayProfiles == null)
            return;
        DockMonitor[] monitors = DockWindows.GetMonitors().ToArray();
        if (monitors.Length == 0 || displayTopology.SequenceEqual(monitors))
            return;
        try { ApplyTopology(monitors); }
        catch (Exception error) when (DockDialogs.IsUserFacing(error))
        {
            if (settings?.IsVisible == true)
                settings.UpdateDisplayNotice(error.Message);
            if (tray != null)
                tray.Text = "Desktop Dock · 显示器配置需要检查";
        }
    }

    private void ApplyTopology(DockMonitor[] monitors, IReadOnlyList<DockDisplayProfile>? profiles = null)
    {
        if (monitors.Length == 0 || displayProfiles == null || legacyDock == null)
            return;
        profiles ??= displayProfiles.Resolve(monitors, legacyDock.Profile.Options.FloatingMonitorId);
        var connected = profiles.Select(profile => profile.Monitor.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (DockDisplayProfile profile in profiles)
        {
            string id = profile.Monitor.Id;
            if (!displayDocks.TryGetValue(id, out DisplayDock? instance))
            {
                if (profile.UsesLegacyProfile)
                {
                    instance = legacyDock;
                }
                else
                {
                    bool newProfile = !File.Exists(Path.Combine(profile.ProfileDirectory, "dock.json"));
                    DockSession local = DockSession.Open(profile.ProfileDirectory);
                    try
                    {
                        if (newProfile)
                            SeedExistingMonitorBehavior(local, id);
                        instance = CreateDisplayDock(local, profile.ThemeDockId, profile.Monitor, ownsInput: false, bound: true);
                    }
                    catch
                    {
                        local.Dispose();
                        throw;
                    }
                }
                displayDocks.Add(id, instance);
            }
            instance.Monitor = profile.Monitor;
            instance.Runtime.UpdateAssignedMonitor(profile.Monitor);
        }

        DockMonitor? legacyMonitor = profiles.FirstOrDefault(profile => profile.UsesLegacyProfile)?.Monitor;
        if (legacyMonitor == null)
        {
            legacyDock.Monitor = null;
            legacyDock.Runtime.UpdateAssignedMonitor(null);
        }
        displayTopology = monitors;
        if (activeDock?.Monitor is not DockMonitor selected || !connected.Contains(selected.Id))
            ActivateDock(FindPrimaryDock() ?? legacyDock);

        foreach (string id in displayDocks.Keys.Where(id => !connected.Contains(id)).ToArray())
        {
            DisplayDock old = displayDocks[id];
            displayDocks.Remove(id);
            if (old != legacyDock)
                CloseDisplayDock(old);
        }

        UpdateDisplayChoices();
        zoneMap?.RefreshDisplays();
        UpdateTrayBehavior();
    }

    private void SeedExistingMonitorBehavior(DockSession profile, string monitorId)
    {
        DockBehaviorOptions previous = legacyDock!.Profile.Options.Behavior;
        MonitorHotZones[] matching = previous.Monitors.Where(value =>
            string.Equals(value.MonitorId, monitorId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matching.Length != 0 || previous.Mode != DockMode.Resident || previous.ApplicationRules.Length != 0)
            profile.UpdateOptions(profile.Options with
            {
                Behavior = profile.Options.Behavior with { Monitors = matching, Mode = previous.Mode, ApplicationRules = previous.ApplicationRules }
            });
    }

    private DisplayDock? FindPrimaryDock() => displayTopology
        .OrderByDescending(monitor => monitor.Primary)
        .Select(monitor => displayDocks.GetValueOrDefault(monitor.Id)).OfType<DisplayDock>().FirstOrDefault();

    private void ActivateDock(DisplayDock instance)
    {
        if (activeDock == instance)
            return;
        int page = settings?.SelectedPage ?? 1;
        bool reopenSettings = settings?.IsVisible == true;
        themeEditor?.Close();
        settings?.Close();
        activeDock = instance;
        session = instance.Profile;
        dock = instance.Window;
        interaction = instance.Runtime;
        dockVisibilityRequested = instance.RequestedVisible;
        if (reopenSettings)
        {
            ShowSettings();
            settings?.SelectPage(page);
        }
    }

    private void ShowSettingsFor(DisplayDock instance)
    {
        ActivateDock(instance);
        ShowSettings();
    }

    private void UpdateDisplayChoices()
    {
        if (settings == null)
            return;
        settings.SetDisplayChoices(displayTopology, activeDock?.Monitor?.Id, id =>
        {
            if (displayDocks.TryGetValue(id, out DisplayDock? instance))
                ActivateDock(instance);
        });
    }

    private void RouteGlobalShortcut(DockHotkeyAction action)
    {
        DisplayDock? target = null;
        if (DockWindows.TryGetPointer(out int x, out int y))
        {
            DockMonitor? monitor = displayTopology.FirstOrDefault(item =>
                x >= item.Left && x < item.Right && y >= item.Top && y < item.Bottom);
            if (monitor != null)
                displayDocks.TryGetValue(monitor.Id, out target);
        }
        (target ?? activeDock ?? legacyDock)?.Runtime.Shortcut(action);
    }

    private void SetAllDocksVisible(bool show)
    {
        foreach (DisplayDock instance in ConnectedDocks.ToArray())
            instance.Runtime.SetVisible(show);
    }

    private void SetAllHotZonesPaused(bool pause)
    {
        foreach (DisplayDock instance in ConnectedDocks.ToArray())
            if (instance.Runtime.HotZonesPaused != pause)
                instance.Runtime.Shortcut(DockHotkeyAction.Pause);
    }

    private void DisposeDisplays()
    {
        if (displayRefreshTimer != null)
        {
            displayRefreshTimer.Stop();
            SystemEvents.DisplaySettingsChanged -= SystemDisplayChanged;
        }
        foreach (DisplayDock instance in displayDocks.Values.Where(item => item != legacyDock).ToArray())
            DisposeDisplayDock(instance);
        if (legacyDock != null)
            DisposeDisplayDock(legacyDock);
        else
            session?.Dispose();
        displayDocks.Clear();
        displayInput?.Dispose();
    }

    private static void DisposeDisplayDock(DisplayDock instance)
    {
        instance.Removing = true;
        instance.Runtime.Dispose();
        instance.Profile.Dispose();
    }

    private static void CloseDisplayDock(DisplayDock instance)
    {
        instance.Removing = true;
        instance.Runtime.Dispose();
        instance.Window.Close();
        instance.Profile.Dispose();
    }
}
