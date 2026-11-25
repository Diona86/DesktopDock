using System.Windows;

namespace DesktopDock;

internal enum DockHotZoneMapHandle { Start, End, Offset, Thickness }

internal sealed record DockHotZoneMapDisplay(DockMonitor Monitor, DockOptions Options, Rect? DockBounds = null);

internal static class DockHotZoneMapGeometry
{
    internal static bool IsHorizontal(HotZoneEdge edge) => edge is HotZoneEdge.Top or HotZoneEdge.Bottom;

    internal static Rect Screen(DockMonitor monitor) =>
        new(monitor.Left, monitor.Top, (double)monitor.Right - monitor.Left, (double)monitor.Bottom - monitor.Top);

    internal static DockHotZone Drag(DockMonitor monitor, DockHotZone original, DockHotZoneMapHandle handle,
        double deltaX, double deltaY)
    {
        if (!double.IsFinite(deltaX) || !double.IsFinite(deltaY))
            throw new ArgumentException("拖动距离必须为有效数值。");

        Rect screen = Screen(monitor);
        bool horizontal = IsHorizontal(original.Edge);
        double span = horizontal ? screen.Width : screen.Height;
        double depth = (horizontal ? screen.Height : screen.Width) / monitor.Scale;
        double across = (horizontal ? deltaY : deltaX) / monitor.Scale;
        if (original.Edge is HotZoneEdge.Bottom or HotZoneEdge.Right)
            across = -across;
        double along = 100 * (horizontal ? deltaX : deltaY) / span;
        double step = Math.Min(100, Math.Ceiling(10000 / span) / 100);
        double minimumThickness = Math.Ceiling(100 / monitor.Scale) / 100;
        double limit = Math.Max(minimumThickness, Math.Floor(depth * 100) / 100);
        double offset = Math.Clamp(original.Offset, 0, limit - minimumThickness);
        double thickness = Math.Clamp(original.Thickness, minimumThickness, Math.Max(minimumThickness, limit - offset));
        static double Round(double value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

        return handle switch
        {
            DockHotZoneMapHandle.Start => original with
            {
                StartPercent = Math.Clamp(Round(original.StartPercent + along), 0, Math.Max(0, original.EndPercent - step))
            },
            DockHotZoneMapHandle.End => original with
            {
                EndPercent = Math.Clamp(Round(original.EndPercent + along), Math.Min(100, original.StartPercent + step), 100)
            },
            DockHotZoneMapHandle.Offset => original with
            {
                Offset = Math.Clamp(Round(offset + across), 0, Math.Max(0, limit - thickness)),
                Thickness = thickness
            },
            DockHotZoneMapHandle.Thickness => original with
            {
                Offset = offset,
                Thickness = Math.Clamp(Round(thickness + across), minimumThickness, Math.Max(minimumThickness, limit - offset))
            },
            _ => throw new ArgumentException("未知的热区手柄。")
        };
    }

    internal static Rect Dock(DockHotZoneMapDisplay display)
    {
        DockMonitor monitor = display.Monitor;
        Rect screen = Screen(monitor);
        if (display.DockBounds is Rect actual && !actual.IsEmpty)
        {
            actual.Intersect(screen);
            if (!actual.IsEmpty && actual.Width > 0 && actual.Height > 0)
                return actual;
        }

        Rect work = monitor.WorkRight > monitor.WorkLeft && monitor.WorkBottom > monitor.WorkTop
            ? new Rect(monitor.WorkLeft, monitor.WorkTop, (double)monitor.WorkRight - monitor.WorkLeft,
                (double)monitor.WorkBottom - monitor.WorkTop)
            : screen;
        DockOptions options = display.Options;
        bool vertical = options.Layout.Mode switch
        {
            DockLayoutMode.SingleColumn or DockLayoutMode.MultiColumnGrid => true,
            DockLayoutMode.SingleRow or DockLayoutMode.MultiRowGrid => false,
            _ => options.Placement is DockPlacement.Left or DockPlacement.Right ||
                options.Placement == DockPlacement.Floating && options.FloatingVertical
        };
        double inset = Math.Min(10 * monitor.Scale, Math.Min(work.Width, work.Height) / 4);
        double length = (options.DockLength > 0 ? options.DockLength : 210) * monitor.Scale;
        double thickness = options.DockThickness * monitor.Scale;
        if (options.Layout.Grid is DockGrid grid)
        {
            double cell = (options.IconSize + 8 + options.Layout.IconSpacing) * monitor.Scale;
            length = (vertical ? grid.Rows : grid.Columns) * cell;
            thickness = (vertical ? grid.Columns : grid.Rows) * cell;
        }
        double width = Math.Clamp(vertical ? thickness : length, 0, work.Width - 2 * inset);
        double height = Math.Clamp(vertical ? length : thickness, 0, work.Height - 2 * inset);
        double centerX = work.Left + (work.Width - width) / 2;
        double centerY = work.Top + (work.Height - height) / 2;
        Point origin = options.Placement switch
        {
            DockPlacement.Top => new Point(centerX, work.Top + inset),
            DockPlacement.Bottom => new Point(centerX, work.Bottom - height - inset),
            DockPlacement.Left => new Point(work.Left + inset, centerY),
            DockPlacement.Right => new Point(work.Right - width - inset, centerY),
            _ => new Point(options.FloatingLeft is double left ? work.Left + left * monitor.Scale : centerX,
                options.FloatingTop is double top ? work.Top + top * monitor.Scale : centerY)
        };
        return new Rect(Math.Clamp(origin.X, work.Left, work.Right - width),
            Math.Clamp(origin.Y, work.Top, work.Bottom - height), width, height);
    }
}
