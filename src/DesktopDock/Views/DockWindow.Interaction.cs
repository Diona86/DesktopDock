using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Controls.Primitives;

namespace DesktopDock;

public partial class DockWindow
{
    private readonly List<ContextMenu> openMenus = [];
    private DockMonitor? revealMonitor;
    private Rect lastMenuBounds = Rect.Empty;
    private long lastMenuClosedAt = long.MinValue;

    internal Func<bool>? EscapeRequested { get; set; }

    private DockWindowPosition.WorkArea GetDockWorkArea() => DockWindowPosition.GetWorkArea(this, revealMonitor);

    internal void SetRevealMonitor(DockMonitor? monitor)
    {
        if (revealMonitor == monitor)
            return;
        revealMonitor = monitor;
        Refresh();
    }

    internal void PrepareReveal()
    {
        _ = new WindowInteropHelper(this).EnsureHandle();
        UpdateLayout();
        ApplyPlacement();
    }

    internal bool ContainsInteraction(int x, int y, bool forClick = false)
    {
        if (!IsVisible)
            return false;
        if (moveStart.HasValue || draggingItem.HasValue)
            return true;
        DockWindowPosition.TryGetBounds(this, out Rect dockBounds);
        var point = new Point(x, y);
        if (dockBounds.Contains(point))
            return true;

        foreach (ContextMenu menu in openMenus)
        {
            if (!menu.IsOpen || !TryGetMenuBounds(menu, out Rect bounds))
                continue;
            lastMenuBounds = bounds;
            if (bounds.Contains(point) || (!forClick && Bridges(point, dockBounds, bounds)))
                return true;
        }

        // The popup can close before the queued low-level mouse-down callback runs.
        return forClick && lastMenuClosedAt != long.MinValue && Environment.TickCount64 - lastMenuClosedAt <= 120
            && lastMenuBounds.Contains(point);
    }

    internal bool CloseTopInteraction()
    {
        while (openMenus.Count > 0)
        {
            ContextMenu menu = openMenus[^1];
            openMenus.RemoveAt(openMenus.Count - 1);
            if (!menu.IsOpen)
                continue;
            menu.IsOpen = false;
            return true;
        }
        return false;
    }

    internal void OpenMenuForTest()
    {
        if (!IsVisible || Surface.ContextMenu is not { } menu)
            throw new InvalidOperationException("请先显示 Dock。");
        menu.PlacementTarget = Surface;
        menu.Placement = PlacementMode.Top;
        menu.IsOpen = true;
        menu.UpdateLayout();
    }

    internal object[] InteractionMenuBounds() => openMenus
        .Select(menu => menu.IsOpen && TryGetMenuBounds(menu, out Rect area) ? DescribeBounds(area) : null)
        .OfType<object>().ToArray();

    internal bool ContainsRevealApproach(ResolvedDockHotZone zone, int x, int y)
    {
        if (zone.Contains(x, y))
            return true;
        if (!DockWindowPosition.TryGetBounds(this, out Rect bounds))
            return false;

        return Bridges(new Point(x, y), bounds, new Rect(zone.Left, zone.Top, zone.Right - zone.Left, zone.Bottom - zone.Top));
    }

    internal void CloseInteractionMenus()
    {
        foreach (ContextMenu menu in openMenus.ToArray())
            menu.IsOpen = false;
        openMenus.Clear();
    }

    private ContextMenu TrackInteractionMenu(ContextMenu menu)
    {
        menu.Opened += (_, _) =>
        {
            if (!openMenus.Contains(menu))
                openMenus.Add(menu);
            lastMenuBounds = TryGetMenuBounds(menu, out Rect bounds) ? bounds : Rect.Empty;
        };
        menu.Closed += (_, _) =>
        {
            openMenus.Remove(menu);
            lastMenuClosedAt = Environment.TickCount64;
        };
        menu.PreviewKeyDown += PreviewInteractionKey;
        return menu;
    }

    private void PreviewInteractionKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !e.IsRepeat && EscapeRequested?.Invoke() == true)
            e.Handled = true;
    }

    private static bool TryGetMenuBounds(ContextMenu menu, out Rect bounds)
    {
        nint handle = (PresentationSource.FromVisual(menu) as HwndSource)?.Handle ?? 0;
        return DockWindowPosition.TryGetBounds(handle, out bounds);
    }

    private static bool Bridges(Point point, Rect a, Rect b)
    {
        if (a.IsEmpty || b.IsEmpty)
            return false;

        // Protect only the short corridor between facing, overlapping edges.
        double x1 = Math.Max(a.Left, b.Left), x2 = Math.Min(a.Right, b.Right);
        double y1 = Math.Min(a.Bottom, b.Bottom), y2 = Math.Max(a.Top, b.Top);
        if (x1 <= x2 && y2 - y1 is >= 0 and <= 64 && point.X >= x1 && point.X <= x2 && point.Y >= y1 && point.Y <= y2)
            return true;

        y1 = Math.Max(a.Top, b.Top);
        y2 = Math.Min(a.Bottom, b.Bottom);
        x1 = Math.Min(a.Right, b.Right);
        x2 = Math.Max(a.Left, b.Left);
        return y1 <= y2 && x2 - x1 is >= 0 and <= 64 && point.Y >= y1 && point.Y <= y2 && point.X >= x1 && point.X <= x2;
    }
}
