using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace DesktopDock;

public partial class DockWindow
{
    private readonly List<ContextMenu> openMenus = [];
    private DockMonitor? revealMonitor;
    private DockWindowPosition.RevealAnchor? revealAnchor;
    private Rect lastMenuBounds = Rect.Empty;
    private long lastMenuClosedAt = long.MinValue;
    private bool desktopNewMenuOpen;

    internal Func<bool>? EscapeRequested { get; set; }

    private DockWindowPosition.WorkArea GetDockWorkArea() => DockWindowPosition.GetWorkArea(this, revealMonitor);

    internal void SetRevealPlacement(DockMonitor? monitor, DockWindowPosition.RevealAnchor? anchor)
    {
        bool monitorChanged = revealMonitor != monitor;
        bool anchorChanged = revealAnchor != anchor;
        revealMonitor = monitor;
        revealAnchor = anchor;
        if (monitorChanged)
        {
            Refresh();
            RefreshGroupPanels();
        }
        else if (anchorChanged)
            QueuePlacement();
    }

    internal void PrepareReveal()
    {
        _ = new WindowInteropHelper(this).EnsureHandle();
        UpdateLayout();
        ApplyPlacement();
    }

    internal bool ContainsInteraction(int x, int y, bool forClick = false)
    {
        if (!IsVisible || !allowPointerInput)
            return false;
        if (desktopNewMenuOpen || moveStart.HasValue || draggingItem.HasValue || IsGridResizing)
            return true;
        DockWindowPosition.TryGetBounds(this, out Rect dockBounds);
        var point = new Point(x, y);
        if (dockBounds.Contains(point))
            return true;

        double groupGap = Math.Max(64, session.Options.Layout.PanelSpacing * GetDockWorkArea().Scale + 4);
        Rect previous = dockBounds;
        Rect[] panels = GroupPanelBounds();
        foreach (Rect panelBounds in panels)
        {
            if (panelBounds.Contains(point) || (!forClick && Bridges(point, previous, panelBounds, groupGap)))
                return true;
            previous = panelBounds;
        }

        foreach (ContextMenu menu in openMenus)
        {
            if (!menu.IsOpen || !TryGetMenuBounds(menu, out Rect bounds))
                continue;
            lastMenuBounds = bounds;
            if (bounds.Contains(point) || (!forClick && (Bridges(point, dockBounds, bounds)
                || panels.Any(panel => Bridges(point, panel, bounds, groupGap)))))
                return true;
        }

        // The popup can close before the queued low-level mouse-down callback runs.
        return forClick && lastMenuClosedAt != long.MinValue && Environment.TickCount64 - lastMenuClosedAt <= 120
            && lastMenuBounds.Contains(point);
    }

    internal bool CloseTopInteraction()
    {
        if (desktopNewMenuOpen && DesktopShellNewMenu.EndMenu())
            return true;
        while (openMenus.Count > 0)
        {
            ContextMenu menu = openMenus[^1];
            openMenus.RemoveAt(openMenus.Count - 1);
            if (!menu.IsOpen)
                continue;
            menu.IsOpen = false;
            return true;
        }
        return CloseTopGroup();
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

        Rect zoneBounds = new(zone.Left, zone.Top, zone.Right - zone.Left, zone.Bottom - zone.Top);
        // The taskbar can leave a larger gap between its screen edge and the usable work area.
        return Bridges(new Point(x, y), bounds, zoneBounds, revealAnchor.HasValue ? double.PositiveInfinity : 64);
    }

    internal void CloseInteractionMenus()
    {
        if (desktopNewMenuOpen)
            DesktopShellNewMenu.EndMenu();
        foreach (ContextMenu menu in openMenus.ToArray())
            menu.IsOpen = false;
        openMenus.Clear();
    }

    private void OpenDesktopNewMenu()
    {
        Point anchor = DockWindowPosition.TryGetPointer(out Point pointer) ? pointer : Surface.PointToScreen(new Point());
        Surface.ContextMenu.IsOpen = false;
        _ = Dispatcher.InvokeAsync(() => TryAction(() => ShowDesktopNewMenu(anchor)), DispatcherPriority.Input);
    }

    private void ShowDesktopNewMenu(Point anchor)
    {
        if (!IsVisible || desktopNewMenuOpen || PresentationSource.FromVisual(this) is not HwndSource source)
            return;

        desktopNewMenuOpen = true;
        try
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            using var menu = new DesktopShellNewMenu(desktop, source.Handle);
            nint Forward(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
            {
                handled = menu.TryForwardMessage(message, wParam, lParam, out nint result);
                return result;
            }

            source.AddHook(Forward);
            try
            {
                menu.Show((int)anchor.X, (int)anchor.Y);
            }
            finally
            {
                source.RemoveHook(Forward);
            }
        }
        finally
        {
            desktopNewMenuOpen = false;
        }
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

    private static bool Bridges(Point point, Rect a, Rect b, double maxGap = 64)
    {
        if (a.IsEmpty || b.IsEmpty)
            return false;

        // Protect only the short corridor between facing, overlapping edges.
        double x1 = Math.Max(a.Left, b.Left), x2 = Math.Min(a.Right, b.Right);
        double y1 = Math.Min(a.Bottom, b.Bottom), y2 = Math.Max(a.Top, b.Top);
        if (x1 <= x2 && y2 >= y1 && y2 - y1 <= maxGap && point.X >= x1 && point.X <= x2 && point.Y >= y1 && point.Y <= y2)
            return true;

        y1 = Math.Max(a.Top, b.Top);
        y2 = Math.Min(a.Bottom, b.Bottom);
        x1 = Math.Min(a.Right, b.Right);
        x2 = Math.Max(a.Left, b.Left);
        return y1 <= y2 && x2 >= x1 && x2 - x1 <= maxGap && point.Y >= y1 && point.Y <= y2 && point.X >= x1 && point.X <= x2;
    }
}
