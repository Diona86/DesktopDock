using System.Windows;

namespace DesktopDock;

public partial class App
{
    private DockHotZoneMapWindow? zoneMap;

    private void ShowZoneMap()
    {
        if (zoneMap == null)
        {
            zoneMap = new DockHotZoneMapWindow(ReadZoneMapDisplays, SaveMapZones, activeDock?.Monitor?.Id)
            {
                ShowActivated = !validationMode
            };
            zoneMap.Closed += (_, _) => zoneMap = null;
            zoneMap.Show();
        }
        if (zoneMap.WindowState == WindowState.Minimized)
            zoneMap.WindowState = WindowState.Normal;
        if (!validationMode)
            zoneMap.Activate();
    }

    private IReadOnlyList<DockHotZoneMapDisplay> ReadZoneMapDisplays()
    {
        if (displayTopology.Length == 0 && legacyDock != null)
        {
            return legacyDock.Runtime.ReadMonitors().Take(1)
                .Select(monitor => new DockHotZoneMapDisplay(monitor, legacyDock.Profile.Options, DockBounds(legacyDock)))
                .ToArray();
        }
        return displayTopology.Where(monitor => displayDocks.ContainsKey(monitor.Id)).Select(monitor =>
        {
            DisplayDock instance = displayDocks[monitor.Id];
            return new DockHotZoneMapDisplay(monitor, instance.Profile.Options, DockBounds(instance));
        }).ToArray();
    }

    private static Rect? DockBounds(DisplayDock instance) =>
        instance.Window.IsVisible && DockWindowPosition.TryGetBounds(instance.Window, out Rect bounds) ? bounds : null;

    private void SaveMapZones(string monitorId, DockHotZone[]? zones)
    {
        if (!displayDocks.TryGetValue(monitorId, out DisplayDock? instance))
        {
            if (displayTopology.Length != 0 || legacyDock?.Runtime.ReadMonitors().FirstOrDefault()?.Id != monitorId)
                throw new InvalidOperationException("显示器已断开。请重新连接后保存热区草稿。");
            instance = legacyDock;
        }
        DockBehaviorOptions behavior = instance.Profile.Options.Behavior;
        MonitorHotZones[] retained = behavior.Monitors.Where(profile => !string.Equals(profile.MonitorId, monitorId,
            StringComparison.OrdinalIgnoreCase)).ToArray();
        MonitorHotZones[] profiles = zones == null ? retained : [.. retained, new MonitorHotZones(monitorId, zones)];
        string? error = instance.Runtime.Apply(behavior with { Monitors = profiles }, false);
        if (error != null)
            throw new InvalidOperationException(error);
    }
}
