namespace DesktopDock;

public sealed record ResolvedDockHotZone(string MonitorId, DockHotZone Zone, int Left, int Top, int Right, int Bottom)
{
    public bool IsDefault => Zone.Id == Guid.Empty;

    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;

    public bool Overlaps(ResolvedDockHotZone other) =>
        string.Equals(MonitorId, other.MonitorId, StringComparison.OrdinalIgnoreCase) &&
        Math.Max(Left, other.Left) < Math.Min(Right, other.Right) &&
        Math.Max(Top, other.Top) < Math.Min(Bottom, other.Bottom);
}

public static class DockHotZoneGeometry
{
    public const int DefaultHideDelayMs = 450;

    public static ResolvedDockHotZone[] ResolveZones(DockBehaviorOptions behavior, DockPlacement placement, DockMonitor monitor)
    {
        if (monitor.Left >= monitor.Right || monitor.Top >= monitor.Bottom ||
            !double.IsFinite(monitor.Scale) || monitor.Scale <= 0)
            return [];

        MonitorHotZones? configuration = behavior.Monitors.FirstOrDefault(entry =>
            string.Equals(entry.MonitorId, monitor.Id, StringComparison.OrdinalIgnoreCase));
        DockHotZone[] zones = configuration?.Zones ?? [DefaultZone(placement)];
        var result = new List<ResolvedDockHotZone>(zones.Length);
        foreach (DockHotZone zone in zones)
        {
            ResolvedDockHotZone bounds = Resolve(zone, monitor);
            if (bounds.Left < bounds.Right && bounds.Top < bounds.Bottom)
                result.Add(bounds);
        }
        return result.ToArray();
    }

    public static ResolvedDockHotZone? HitTest(IEnumerable<ResolvedDockHotZone> zones, int x, int y)
    {
        foreach (ResolvedDockHotZone zone in zones)
            if (zone.Contains(x, y))
                return zone;
        return null;
    }

    private static DockHotZone DefaultZone(DockPlacement placement)
    {
        HotZoneEdge edge = placement switch
        {
            DockPlacement.Top => HotZoneEdge.Top,
            DockPlacement.Left => HotZoneEdge.Left,
            DockPlacement.Right => HotZoneEdge.Right,
            _ => HotZoneEdge.Bottom
        };
        return new DockHotZone(Guid.Empty, edge, 35, 65, 6, 0, 250, DefaultHideDelayMs);
    }

    private static ResolvedDockHotZone Resolve(DockHotZone zone, DockMonitor monitor)
    {
        bool horizontal = zone.Edge is HotZoneEdge.Top or HotZoneEdge.Bottom;
        int alongStart = horizontal ? monitor.Left : monitor.Top;
        int alongEnd = horizontal ? monitor.Right : monitor.Bottom;
        int crossStart = horizontal ? monitor.Top : monitor.Left;
        int crossEnd = horizontal ? monitor.Bottom : monitor.Right;
        double length = (double)alongEnd - alongStart;
        double depth = (double)crossEnd - crossStart;
        int first = PixelBoundary(Math.Clamp(alongStart + length * (zone.StartPercent / 100), alongStart, alongEnd));
        int last = PixelBoundary(Math.Clamp(alongStart + length * (zone.EndPercent / 100), alongStart, alongEnd));
        double near = Math.Clamp(zone.Offset * monitor.Scale, 0, depth);
        double far = Math.Clamp((zone.Offset + zone.Thickness) * monitor.Scale, 0, depth);

        return zone.Edge switch
        {
            HotZoneEdge.Top => new(monitor.Id, zone, first, PixelBoundary(crossStart + near), last, PixelBoundary(crossStart + far)),
            HotZoneEdge.Bottom => new(monitor.Id, zone, first, PixelBoundary(crossEnd - far), last, PixelBoundary(crossEnd - near)),
            HotZoneEdge.Left => new(monitor.Id, zone, PixelBoundary(crossStart + near), first, PixelBoundary(crossStart + far), last),
            _ => new(monitor.Id, zone, PixelBoundary(crossEnd - far), first, PixelBoundary(crossEnd - near), last)
        };
    }

    // Integer pointer positions in a half-open continuous interval occupy [ceil(start), ceil(end)).
    private static int PixelBoundary(double coordinate) => (int)Math.Ceiling(coordinate);
}
