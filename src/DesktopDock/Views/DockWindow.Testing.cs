using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace DesktopDock;

public partial class DockWindow
{
    internal object ReadVisualState()
    {
        UpdateLayout();
        return new
        {
            visible = IsVisible,
            transparent = AllowsTransparency,
            background = Background?.ToString(),
            surfaceBackground = Surface.Background?.ToString(),
            windowStyle = WindowStyle.ToString(),
            activatesOnShow = ShowActivated,
            showInTaskbar = ShowInTaskbar,
            topmost = Topmost,
            bounds = DockWindowPosition.TryGetBounds(this, out Rect bounds) ? DescribeBounds(bounds) : null,
            placement = session.Options.Placement.ToString(),
            vertical,
            positionLocked = PositionLocked,
            gripVisible = MoveHandle.IsVisible,
            items = RenderedItemButtons().Select(button =>
            {
                var tooltip = button.ToolTip as ToolTip;
                var image = button.Content as Image;
                var bitmap = image?.Source as BitmapSource;
                return new
                {
                    id = button.Tag,
                    name = AutomationProperties.GetName(button),
                    automationId = AutomationProperties.GetAutomationId(button),
                    tooltip = tooltip?.Content as string,
                    tooltipInitialDelay = ToolTipService.GetInitialShowDelay(button),
                    tooltipOpen = tooltip?.IsOpen == true,
                    tooltipPopupAttached = tooltip != null && PresentationSource.FromVisual(tooltip) != null,
                    imageLoaded = image?.Source != null,
                    imagePixelWidth = bitmap?.PixelWidth,
                    imagePixelHeight = bitmap?.PixelHeight
                };
            }).ToArray()
        };
    }

    internal object TestDragItem(Guid sourceId, Guid targetId, bool after)
    {
        RequireVisibleDock();
        _ = RequireItemButton(sourceId);
        Button target = RequireItemButton(targetId);
        target.BringIntoView();
        UpdateLayout();

        Rect before = RequireWindowBounds();
        Guid[] orderBefore = RenderedItemIds();
        double fraction = after ? 0.75 : 0.25;
        Point offset = vertical
            ? new Point(target.ActualWidth / 2, target.ActualHeight * fraction)
            : new Point(target.ActualWidth * fraction, target.ActualHeight / 2);
        Point pointer = target.TranslatePoint(offset, Viewport);
        try
        {
            bool feedback = ShowItemDropFeedback(pointer) && DropIndicator.IsVisible;
            bool committed = CommitItemDrop(sourceId, pointer);
            Guid[] orderAfter = RenderedItemIds();
            return new
            {
                feedback,
                committed,
                orderChanged = !orderBefore.SequenceEqual(orderAfter),
                orderBefore,
                orderAfter,
                windowMoved = before != RequireWindowBounds()
            };
        }
        finally
        {
            HideDropIndicator();
        }
    }

    internal object TestDragHandle(double dx, double dy)
    {
        RequireVisibleDock();
        if (!double.IsFinite(dx) || !double.IsFinite(dy))
            throw new ArgumentException("移动距离必须是有限的 DIP 数值。");

        Rect before = RequireWindowBounds();
        DockWindowPosition.WorkArea work = DockWindowPosition.GetWorkArea(this);
        Guid[] order = RenderedItemIds();
        bool locked = PositionLocked;
        bool gripVisibleBefore = MoveHandle.IsVisible;
        UpdateGrip(GripZone);
        bool gripVisibleOnBackground = MoveHandle.IsVisible;
        var delta = new Vector(dx, dy);
        bool committed = false;
        try
        {
            if (!locked && BeyondDragThreshold(delta))
            {
                MoveDock(before, work, delta * work.Scale);
                committed = CommitDockMove(work);
            }

            Rect after = RequireWindowBounds();
            return new
            {
                locked,
                gripVisibleBefore,
                gripVisibleOnBackground,
                moved = before.Location != after.Location,
                committed,
                orderChanged = !order.SequenceEqual(RenderedItemIds()),
                before = DescribeBounds(before),
                after = DescribeBounds(after),
                placement = session.Options.Placement.ToString()
            };
        }
        finally
        {
            UpdateGrip(null, false);
            QueuePlacement();
        }
    }

    internal object TestTooltip(Guid id, bool open)
    {
        RequireVisibleDock();
        Button button = RequireItemButton(id);
        if (button.ToolTip is not ToolTip tooltip)
            throw new InvalidOperationException("该 Dock 项目没有工具提示。");

        tooltip.IsOpen = open;
        tooltip.UpdateLayout();
        return new
        {
            name = AutomationProperties.GetName(button),
            content = tooltip.Content as string,
            isOpen = tooltip.IsOpen,
            popupAttached = PresentationSource.FromVisual(tooltip) != null,
            width = tooltip.ActualWidth,
            height = tooltip.ActualHeight
        };
    }

    private IEnumerable<Button> RenderedItemButtons() => ItemStrip.Children.OfType<Button>().Where(button => button.Tag is Guid);

    private Guid[] RenderedItemIds() => RenderedItemButtons().Select(button => (Guid)button.Tag!).ToArray();

    private Button RequireItemButton(Guid id) => RenderedItemButtons()
        .FirstOrDefault(button => button.Tag is Guid candidate && candidate == id)
        ?? throw new ArgumentException("Dock 中没有该项目。", nameof(id));

    private void RequireVisibleDock()
    {
        if (!IsVisible)
            throw new InvalidOperationException("请先显示 Dock。");
        UpdateLayout();
    }

    private Rect RequireWindowBounds() => DockWindowPosition.TryGetBounds(this, out Rect bounds) ? bounds
        : throw new InvalidOperationException("无法读取 Dock 窗口位置。");

    private static object DescribeBounds(Rect bounds) => new
    {
        x = bounds.X, y = bounds.Y, width = bounds.Width, height = bounds.Height
    };
}
