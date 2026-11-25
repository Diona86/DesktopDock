using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DesktopDock;

public partial class DockHotZoneMapWindow
{
    private static readonly Brush ConflictBrush = Solid(Color.FromRgb(153, 96, 25));
    private static readonly Brush SelectedZoneBrush = Solid(Color.FromRgb(34, 92, 80));
    private static readonly Brush ZoneBrush = Solid(Color.FromRgb(84, 134, 118));
    private static readonly Brush White = Brushes.White;
    private static readonly Brush ScreenBrush = Solid(Color.FromRgb(251, 251, 247));
    private static readonly Brush ActiveScreenBrush = Solid(Color.FromRgb(225, 235, 228));
    private static readonly Brush ScreenBorder = Solid(Color.FromRgb(153, 164, 154));
    private static readonly Brush DockBrush = Solid(Color.FromRgb(37, 42, 40));
    private const double MapInset = 22;

    private sealed record MapHit(string MonitorId, int? Index = null, DockHotZoneMapHandle? Handle = null);
    private sealed record ZoneDrag(DockHotZoneMapDisplay Display, DockHotZone Original, DockHotZoneMapHandle Handle, Point Origin,
        double MapScale, bool HadDraft, DockHotZone[]? PreviousDraft);
    private sealed record Projection(double Scale, double Left, double Top)
    {
        internal Rect MapBounds(Rect bounds) => new(Left + bounds.Left * Scale, Top + bounds.Top * Scale,
            bounds.Width * Scale, bounds.Height * Scale);
    }

    private ZoneDrag? activeDrag;

    private static Brush Solid(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private Projection? MapProjection()
    {
        if (displays.Length == 0 || Map.ActualWidth <= 2 * MapInset || Map.ActualHeight <= 2 * MapInset)
            return null;

        Rect extent = Rect.Empty;
        foreach (DockHotZoneMapDisplay display in displays)
            extent.Union(DockHotZoneMapGeometry.Screen(display.Monitor));
        double scale = Math.Min((Map.ActualWidth - 2 * MapInset) / extent.Width,
            (Map.ActualHeight - 2 * MapInset) / extent.Height);
        return new Projection(scale, (Map.ActualWidth - extent.Width * scale) / 2 - extent.Left * scale,
            (Map.ActualHeight - extent.Height * scale) / 2 - extent.Top * scale);
    }

    private void MapSizeChanged(object sender, SizeChangedEventArgs e) => DrawMap();

    private void DrawMap()
    {
        if (Map == null)
            return;
        Map.Children.Clear();
        if (MapProjection() is not Projection projection)
            return;

        foreach (DockHotZoneMapDisplay display in displays)
            DrawScreen(display, projection);
        foreach (DockHotZoneMapDisplay display in displays)
            DrawZones(display, projection);
    }

    private void DrawScreen(DockHotZoneMapDisplay display, Projection projection)
    {
        DockMonitor monitor = display.Monitor;
        Rect screen = projection.MapBounds(DockHotZoneMapGeometry.Screen(monitor));
        bool selected = DisplayIds.Equals(monitor.Id, selectedDisplayId);
        var hit = new MapHit(monitor.Id);
        Rectangle surface = AddRectangle(screen, selected ? ActiveScreenBrush : ScreenBrush, selected ? SelectedZoneBrush : ScreenBorder,
            new DisplayRow(display).ToString(), hit);
        surface.StrokeThickness = selected ? 1.5 : 1;
        surface.ToolTip = $"{new DisplayRow(display)}\n{Resolution(monitor)} · 原点 ({monitor.Left}, {monitor.Top})";
        Rect dock = KeepVisible(projection.MapBounds(DockHotZoneMapGeometry.Dock(display)), screen, 4);
        Rectangle bar = AddRectangle(dock, DockBrush, null, PlacementName(display.Options.Placement), hit);
        bar.RadiusX = bar.RadiusY = 2;
        bar.ToolTip = PlacementName(display.Options.Placement);
        if (screen.Width >= 48 && screen.Height >= 40)
        {
            double labelTop = screen.Top + 8;
            if (dock.Top <= labelTop + 32 && dock.Bottom + 36 < screen.Bottom)
                labelTop = dock.Bottom + 4;
            var label = new System.Windows.Controls.TextBlock
            {
                Foreground = DockBrush,
                FontSize = 12,
                Text = new DisplayRow(display).ToString(),
                FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Width = screen.Width - 14,
                IsHitTestVisible = false
            };
            Place(label, new Point(screen.Left + 7, labelTop));
            if (screen.Width >= 112 && screen.Height >= 55)
            {
                var resolution = new System.Windows.Controls.TextBlock
                {
                    Foreground = DockBrush,
                    FontSize = 11,
                    Text = Resolution(monitor),
                    Width = screen.Width - 14,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    IsHitTestVisible = false
                };
                Place(resolution, new Point(screen.Left + 7, labelTop + 17));
            }
        }
    }

    private void DrawZones(DockHotZoneMapDisplay display, Projection projection)
    {
        DockMonitor monitor = display.Monitor;
        Rect screen = projection.MapBounds(DockHotZoneMapGeometry.Screen(monitor));
        DockHotZone[] zones = Zones(display);
        ResolvedDockHotZone[] resolved = ResolvedZones(display, ConfiguredZones(display));
        var conflict = ConflictingZones(resolved);
        Rect selectedZone = Rect.Empty;
        DockHotZone? selected = null;

        foreach (ResolvedDockHotZone zone in resolved)
        {
            int index = Array.IndexOf(zones, zone.Zone);
            bool isSelected = DisplayIds.Equals(monitor.Id, selectedDisplayId) && zoneIndex == index;
            Rect bounds = projection.MapBounds(ZoneBounds(zone));
            Brush color = conflict.Contains(zone.Zone) ? ConflictBrush : isSelected ? SelectedZoneBrush : ZoneBrush;
            var hit = new MapHit(monitor.Id, index);
            ZoneRow row = new(zone.Zone);
            Rectangle shape = AddRectangle(KeepVisible(bounds, screen, 4), color, null, row.Label, hit);
            shape.ToolTip = $"{row.Label}\n{row.Details}";
            if (isSelected)
            {
                selectedZone = bounds;
                selected = zone.Zone;
            }
        }
        if (selected != null)
            DrawHandles(selectedZone, selected.Edge, new MapHit(monitor.Id, zoneIndex));
    }

    private void DrawHandles(Rect bounds, HotZoneEdge edge, MapHit hit)
    {
        bool horizontal = DockHotZoneMapGeometry.IsHorizontal(edge);
        bool reverse = edge is HotZoneEdge.Bottom or HotZoneEdge.Right;
        double start = horizontal ? bounds.Left : bounds.Top;
        double end = horizontal ? bounds.Right : bounds.Bottom;
        double midpoint = (start + end) / 2;
        double crossMin = horizontal ? bounds.Top : bounds.Left;
        double crossMax = horizontal ? bounds.Bottom : bounds.Right;
        double near = (reverse ? crossMax : crossMin) + (reverse ? 7 : -7);
        double far = (reverse ? crossMin : crossMax) + (reverse ? -7 : 7);
        double crossMiddle = (crossMin + crossMax) / 2;
        Cursor along = horizontal ? Cursors.SizeWE : Cursors.SizeNS;
        Cursor across = horizontal ? Cursors.SizeNS : Cursors.SizeWE;
        Point Center(double first, double second) => horizontal ? new Point(first, second) : new Point(second, first);
        AddHandle(Center(Math.Min(start, midpoint - 7), crossMiddle), hit with { Handle = DockHotZoneMapHandle.Start }, "拖动起点", along, circular: true);
        AddHandle(Center(Math.Max(end, midpoint + 7), crossMiddle), hit with { Handle = DockHotZoneMapHandle.End }, "拖动终点", along, circular: true);
        AddHandle(Center(midpoint, near), hit with { Handle = DockHotZoneMapHandle.Offset }, "拖动调整偏移", across, circular: false);
        AddHandle(Center(midpoint, far), hit with { Handle = DockHotZoneMapHandle.Thickness }, "拖动调整厚度", across, circular: false);
    }

    private Rectangle AddRectangle(Rect bounds, Brush fill, Brush? stroke, string name, MapHit hit)
    {
        var rectangle = new Rectangle { Width = bounds.Width, Height = bounds.Height, Fill = fill, Stroke = stroke, Tag = hit };
        AutomationProperties.SetName(rectangle, name);
        Place(rectangle, bounds.TopLeft);
        return rectangle;
    }

    private void AddHandle(Point center, MapHit hit, string name, Cursor cursor, bool circular)
    {
        const double size = 10;
        Shape handle = circular ? new Ellipse() : new Rectangle { RadiusX = 1, RadiusY = 1 };
        handle.Width = handle.Height = size;
        handle.Fill = White;
        handle.Stroke = SelectedZoneBrush;
        handle.StrokeThickness = 1.5;
        handle.Tag = hit;
        handle.Cursor = cursor;
        handle.ToolTip = name;
        AutomationProperties.SetName(handle, name);
        Place(handle, new Point(center.X - size / 2, center.Y - size / 2));
        System.Windows.Controls.Panel.SetZIndex(handle, 1);
    }

    private void Place(FrameworkElement visual, Point at)
    {
        System.Windows.Controls.Canvas.SetLeft(visual, at.X);
        System.Windows.Controls.Canvas.SetTop(visual, at.Y);
        Map.Children.Add(visual);
    }

    private static Rect KeepVisible(Rect bounds, Rect screen, double minimum)
    {
        double width = Math.Min(screen.Width, Math.Max(bounds.Width, minimum));
        double height = Math.Min(screen.Height, Math.Max(bounds.Height, minimum));
        return new Rect(Math.Clamp(bounds.X + (bounds.Width - width) / 2, screen.Left, Math.Max(screen.Left, screen.Right - width)),
            Math.Clamp(bounds.Y + (bounds.Height - height) / 2, screen.Top, Math.Max(screen.Top, screen.Bottom - height)), width, height);
    }

    private static Rect ZoneBounds(ResolvedDockHotZone zone) => new(zone.Left, zone.Top,
        (double)zone.Right - zone.Left, (double)zone.Bottom - zone.Top);

    private void MapPointerDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not FrameworkElement { Tag: MapHit hit })
            return;
        Point pointer = e.GetPosition(Map);
        SelectDisplay(hit.MonitorId, hit.Index ?? 0);
        if (hit.Handle is DockHotZoneMapHandle handle && MapProjection() is Projection projection &&
            SelectedDisplay is DockHotZoneMapDisplay display && Zones(display).ElementAtOrDefault(zoneIndex) is DockHotZone original)
        {
            Map.Focus();
            if (Map.CaptureMouse())
            {
                bool hadDraft = drafts.TryGetValue(display.Monitor.Id, out DockHotZone[]? previous);
                activeDrag = new ZoneDrag(display, original, handle, pointer, projection.Scale, hadDraft, previous);
            }
        }
        e.Handled = true;
    }

    private void MapPointerMove(object sender, MouseEventArgs e)
    {
        if (activeDrag is not ZoneDrag drag || e.LeftButton != MouseButtonState.Pressed)
            return;
        Vector delta = (e.GetPosition(Map) - drag.Origin) / drag.MapScale;
        if (delta.LengthSquared > 0)
        {
            DockHotZone updated = DockHotZoneMapGeometry.Drag(drag.Display.Monitor, drag.Original, drag.Handle, delta.X, delta.Y);
            DockHotZone? current = Zones(drag.Display).ElementAtOrDefault(zoneIndex);
            if (current != null && updated with { Id = current.Id } != current)
                Guard(() => Edit(updated));
        }
        else if (Zones(drag.Display).ElementAtOrDefault(zoneIndex) is DockHotZone current && current != drag.Original)
        {
            RestoreDragDraft(drag);
        }
        e.Handled = true;
    }

    private void MapPointerUp(object sender, MouseButtonEventArgs e)
    {
        if (activeDrag == null)
            return;
        activeDrag = null;
        Map.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void CancelDrag()
    {
        if (activeDrag is not ZoneDrag drag)
            return;
        activeDrag = null;
        RestoreDragDraft(drag);
        if (Map.IsMouseCaptured)
            Map.ReleaseMouseCapture();
    }

    private void RestoreDragDraft(ZoneDrag drag)
    {
        string id = drag.Display.Monitor.Id;
        if (drag.HadDraft)
            drafts[id] = drag.PreviousDraft;
        else
            drafts.Remove(id);
        UpdateEditor();
    }

    private void MapCaptureLost(object sender, MouseEventArgs e) => CancelDrag();

    private void MapKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && activeDrag != null)
        {
            CancelDrag();
            e.Handled = true;
        }
    }

    private object MapState()
    {
        Projection? projection = MapProjection();
        return new
        {
            visible = IsVisible,
            selectedDisplayId,
            selectedZoneIndex = zoneIndex,
            dirtyDisplayIds = drafts.Keys.ToArray(),
            conflictStatus = ConflictStatus.Text,
            screens = displays.Select(display =>
            {
                DockHotZone[]? configured = ConfiguredZones(display);
                ResolvedDockHotZone[] resolved = ResolvedZones(display, configured);
                return new
                {
                    display.Monitor,
                    display.Options.Placement,
                    dock = Describe(DockHotZoneMapGeometry.Dock(display)),
                    map = projection == null ? null : Describe(projection.MapBounds(DockHotZoneMapGeometry.Screen(display.Monitor))),
                    isDefault = configured == null,
                    zones = Zones(display),
                    resolved = resolved.Select(zone => Describe(ZoneBounds(zone))).ToArray(),
                    conflicts = ConflictingZones(resolved).Count
                };
            }).ToArray(),
            handleCount = Map.Children.OfType<Shape>().Count(shape => shape.Tag is MapHit { Handle: not null })
        };
    }

    private static object Describe(Rect bounds) => new { x = bounds.X, y = bounds.Y, width = bounds.Width, height = bounds.Height };
}
