using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace DesktopDock;

public partial class DockWindow
{
    public static readonly DependencyProperty IsResizableTileProperty = DependencyProperty.RegisterAttached(
        "IsResizableTile", typeof(bool), typeof(DockWindow));

    public static bool GetIsResizableTile(DependencyObject source) => (bool)source.GetValue(IsResizableTileProperty);
    public static void SetIsResizableTile(DependencyObject source, bool value) => source.SetValue(IsResizableTileProperty, value);

    public static readonly DependencyProperty IsExpandedTileProperty = DependencyProperty.RegisterAttached(
        "IsExpandedTile", typeof(bool), typeof(DockWindow));

    public static bool GetIsExpandedTile(DependencyObject source) => (bool)source.GetValue(IsExpandedTileProperty);
    public static void SetIsExpandedTile(DependencyObject source, bool value) => source.SetValue(IsExpandedTileProperty, value);

    private sealed record GridResize(DockGrid Grid, DockTile? Tile, string Edges, Point Pointer, Rect Bounds,
        DockWindowPosition.WorkArea Work, double Cell);

    private GridResize? gridResize;
    private bool IsGridResizing => gridResize != null;
    private DockGrid? TileGrid => session.Options.Layout.Grid;
    private bool HasTileGrid => TileGrid != null;
    private int TileRows => TileGrid!.Rows;
    private int TileColumns => TileGrid!.Columns;
    private double GridCell => ThemedIconSize + 8 + ThemedIconSpacing + EntryLabelSpace(session.Options.IconNameDisplay);

    private void InitializeGridResize()
    {
        AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(BeginGridResize));
        AddHandler(Thumb.DragDeltaEvent, new DragDeltaEventHandler(PreviewGridResize));
        AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(EndGridResize));
    }

    private bool RefreshTileGrid(DockWindowPosition.WorkArea work)
    {
        DockGrid? grid = TileGrid;
        ItemScroll.Visibility = grid == null ? Visibility.Visible : Visibility.Collapsed;
        GridScroll.Visibility = grid == null ? Visibility.Collapsed : Visibility.Visible;
        TileCanvas.Children.Clear();
        if (grid == null)
            return false;

        ItemStrip.Children.Clear();
        CancelIconLoads();
        iconLoads = new CancellationTokenSource();
        double cell = GridCell;
        double gap = ThemedIconSpacing;
        double icon = ThemedIconSize;
        TileCanvas.Width = grid.Columns * cell;
        TileCanvas.Height = grid.Rows * cell;
        LimitGridSurface(TileCanvas.Width, TileCanvas.Height, work);

        foreach (DockTile tile in grid.Tiles.OrderBy(tile => tile.Row).ThenBy(tile => tile.Column))
        {
            double tileIcon = icon * Math.Min(2, Math.Min(tile.ColumnSpan, tile.RowSpan));
            var button = CreateEntryButton(tile.EntryId, tileIcon, cell - gap, work.Scale, iconLoads.Token);
            SetIsResizableTile(button, true);
            button.Width = tile.ColumnSpan * cell - gap;
            button.Height = tile.RowSpan * cell - gap;
            SetIsExpandedTile(button, tile.ColumnSpan > 1 || tile.RowSpan > 1);
            Canvas.SetLeft(button, tile.Column * cell + gap / 2);
            Canvas.SetTop(button, tile.Row * cell + gap / 2);
            TileCanvas.Children.Add(button);
        }
        if (grid.Tiles.Length == 0)
        {
            var add = new Button { Style = buttonStyle, Content = "+", Width = cell, Height = cell, ToolTip = "添加项目" };
            AutomationProperties.SetAutomationId(add, "DockAddItem");
            AutomationProperties.SetName(add, "添加项目");
            add.Click += (_, _) => showSettings();
            ApplyEntryTooltip(add);
            TileCanvas.Children.Add(add);
        }
        return true;
    }

    private void LimitGridSurface(double width, double height, DockWindowPosition.WorkArea work)
    {
        Size chrome = GridChrome();
        Surface.Width = Math.Min(width + chrome.Width, AvailableGridAxis(work.Width));
        Surface.Height = Math.Min(height + chrome.Height, AvailableGridAxis(work.Height));
    }

    private Size GridChrome()
    {
        double border = 2 * (Surface.Padding.Left + Surface.BorderThickness.Left);
        return new Size(border + (vertical ? 0 : GripLength), border + (vertical ? GripLength : 0));
    }

    private Size GridWorkCapacity(DockWindowPosition.WorkArea work, double cell)
    {
        Size chrome = GridChrome();
        return new Size(Math.Max(1, Math.Floor((AvailableGridAxis(work.Width) - chrome.Width) / cell)),
            Math.Max(1, Math.Floor((AvailableGridAxis(work.Height) - chrome.Height) / cell)));
    }

    private double AvailableGridAxis(double workAxis) => Math.Max(1, workAxis - (EdgeGap + ThemeShadowInset) * 2);

    private DockGrid GridSnapshot()
    {
        if (TileGrid is DockGrid saved)
            return saved;

        // Use the rendered order as the seed, then account for extra space from a custom Dock length.
        DockGrid seed = session.ResolveGrid(Math.Max(1, ItemStrip.Columns), Math.Max(1, ItemStrip.Rows));
        Size chrome = GridChrome();
        return seed.Resize(Math.Max(seed.Columns, RoundCells((Surface.ActualWidth - chrome.Width) / GridCell)),
            Math.Max(seed.Rows, RoundCells((Surface.ActualHeight - chrome.Height) / GridCell)));
    }

    private static int RoundCells(double value)
    {
        if (!double.IsFinite(value))
            throw new ArgumentException("网格尺寸须为有限数值。");
        return (int)Math.Round(Math.Clamp(value, 1, DockGrid.MaxDimension), MidpointRounding.AwayFromZero);
    }

    private static bool IsResizeThumb(DependencyObject? source)
    {
        while (source != null && source is not Button)
        {
            if (source is Thumb)
                return true;
            source = source is Visual or Visual3D ? VisualTreeHelper.GetParent(source) : (source as FrameworkContentElement)?.Parent;
        }
        return false;
    }

    private void BeginGridResize(object sender, DragStartedEventArgs e)
    {
        if (e.OriginalSource is not Thumb thumb ||
            !DockWindowPosition.TryGetPointer(out Point pointer) ||
            !DockWindowPosition.TryGetBounds(this, out Rect bounds))
            return;

        DockGrid grid = GridSnapshot();
        Guid? entry = (thumb.TemplatedParent as Button)?.Tag as Guid?;
        DockTile? tile = grid.Tiles.FirstOrDefault(value => value.EntryId == entry);
        string? edges = thumb.Tag as string;
        if (edges == null && tile == null)
            return;

        DockWindowPosition.WorkArea work = GetDockWorkArea();
        if (edges == null && tile != null)
        {
            // Near a monitor boundary, pull inward to enlarge the tile.
            Rect tileBounds = EntryScreenBounds((Button)thumb.TemplatedParent);
            double cellPixels = GridCell * work.Scale;
            string horizontal = work.Pixels.Right - tileBounds.Right < cellPixels ? "Left" : "Right";
            string vertical = work.Pixels.Bottom - tileBounds.Bottom < cellPixels ? "Top" : "Bottom";
            edges = horizontal + " " + vertical;
        }
        pressedItem = null;
        gridResize = new(grid, tile, edges!, pointer, bounds, work, GridCell);
        e.Handled = true;
    }

    private void PreviewGridResize(object sender, DragDeltaEventArgs e)
    {
        if (gridResize is not GridResize drag || !DockWindowPosition.TryGetPointer(out Point pointer))
            return;

        Size proposed = GridDragSize(drag, pointer);
        if (drag.Tile is DockTile tile && RootEntryButton(tile.EntryId) is Button button)
        {
            Point origin = button.TranslatePoint(new Point(), Viewport);
            Size capacity = GridWorkCapacity(drag.Work, drag.Cell);
            bool left = HasResizeEdge(drag, "Left"), top = HasResizeEdge(drag, "Top");
            bool valid = TryStretchTile(drag.Grid, tile, proposed, capacity, out _, left, top);
            double column = left ? Math.Max(0, tile.Column + tile.ColumnSpan - proposed.Width) : tile.Column;
            double row = top ? Math.Max(0, tile.Row + tile.RowSpan - proposed.Height) : tile.Row;
            LimitGridSurface(Math.Max(drag.Grid.Columns, column + proposed.Width) * drag.Cell,
                Math.Max(drag.Grid.Rows, row + proposed.Height) * drag.Cell, drag.Work);
            QueuePlacement();
            origin += new Vector((column - tile.Column) * drag.Cell, (row - tile.Row) * drag.Cell);
            ShowTileOutline(new Rect(origin, new Size(proposed.Width * drag.Cell, proposed.Height * drag.Cell)), valid);
        }
        else
        {
            DockGrid floor = drag.Grid.Resize(1, 1);
            LimitGridSurface(Math.Max(floor.Columns, proposed.Width) * drag.Cell,
                Math.Max(floor.Rows, proposed.Height) * drag.Cell, drag.Work);
            QueuePlacement();
        }
        e.Handled = true;
    }

    private void EndGridResize(object sender, DragCompletedEventArgs e)
    {
        if (gridResize is not GridResize drag)
            return;

        gridResize = null;
        HideDropIndicator();
        if (!e.Canceled && DockWindowPosition.TryGetPointer(out Point pointer) && BeyondDragThreshold((pointer - drag.Pointer) / drag.Work.Scale))
        {
            Size proposed = GridDragSize(drag, pointer);
            Size capacity = GridWorkCapacity(drag.Work, drag.Cell);
            if (drag.Tile is DockTile tile)
            {
                if (TryStretchTile(drag.Grid, tile, proposed, capacity, out DockGrid changed,
                    HasResizeEdge(drag, "Left"), HasResizeEdge(drag, "Top")))
                    TryAction(() => session.UpdateGrid(changed));
            }
            else
            {
                TryAction(() => SaveGridSize(drag.Grid, proposed, capacity, drag));
            }
        }
        Refresh();
        e.Handled = true;
    }

    private static bool HasResizeEdge(GridResize drag, string edge) => drag.Edges.Contains(edge, StringComparison.Ordinal);

    private static Size GridDragSize(GridResize drag, Point pointer)
    {
        Vector delta = (pointer - drag.Pointer) / (drag.Work.Scale * drag.Cell);
        double widthDelta = HasResizeEdge(drag, "Left") ? -delta.X : HasResizeEdge(drag, "Right") ? delta.X : 0;
        double heightDelta = HasResizeEdge(drag, "Top") ? -delta.Y : HasResizeEdge(drag, "Bottom") ? delta.Y : 0;
        return new Size(Math.Max(1, (drag.Tile?.ColumnSpan ?? drag.Grid.Columns) + widthDelta),
            Math.Max(1, (drag.Tile?.RowSpan ?? drag.Grid.Rows) + heightDelta));
    }

    private void SaveGridSize(DockGrid grid, Size proposed, Size capacity, GridResize? drag = null)
    {
        DockGrid changed = grid.Resize(Math.Min(RoundCells(proposed.Width), Math.Max(grid.Columns, (int)capacity.Width)),
            Math.Min(RoundCells(proposed.Height), Math.Max(grid.Rows, (int)capacity.Height)));
        if (drag != null && session.Options.Placement == DockPlacement.Floating)
        {
            Size chrome = GridChrome();
            Size bounds = new((Math.Min(changed.Columns * drag.Cell + chrome.Width, AvailableGridAxis(drag.Work.Width)) + ThemeShadowInset * 2) * drag.Work.Scale,
                (Math.Min(changed.Rows * drag.Cell + chrome.Height, AvailableGridAxis(drag.Work.Height)) + ThemeShadowInset * 2) * drag.Work.Scale);
            Point moved = ResizeOrigin(drag, bounds);
            session.UpdateGrid(changed, (moved.X - drag.Work.Pixels.Left) / drag.Work.Scale,
                (moved.Y - drag.Work.Pixels.Top) / drag.Work.Scale);
        }
        else
        {
            session.UpdateGrid(changed);
        }
    }

    private static Point ResizeOrigin(GridResize drag, Size size) => drag.Work.Clamp(
        new Point(HasResizeEdge(drag, "Left") ? drag.Bounds.Right - size.Width : drag.Bounds.Left,
            HasResizeEdge(drag, "Top") ? drag.Bounds.Bottom - size.Height : drag.Bounds.Top), size);

    private bool TryPlaceGridResize(Rect bounds)
    {
        if (gridResize is not { Tile: null } drag || session.Options.Placement != DockPlacement.Floating)
            return false;
        DockWindowPosition.Move(this, ResizeOrigin(drag, bounds.Size));
        return true;
    }

    private static bool TryStretchTile(DockGrid grid, DockTile tile, Size raw, Size capacity, out DockGrid result,
        bool left = false, bool top = false)
    {
        int columns = RoundCells(raw.Width), rows = RoundCells(raw.Height);
        int column = left ? Math.Max(0, tile.Column + tile.ColumnSpan - columns) : tile.Column;
        int row = top ? Math.Max(0, tile.Row + tile.RowSpan - rows) : tile.Row;
        columns = Math.Min(columns, Math.Max(grid.Columns, (int)capacity.Width) - column);
        rows = Math.Min(rows, Math.Max(grid.Rows, (int)capacity.Height) - row);
        DockGrid expanded = grid.Resize(Math.Max(grid.Columns, column + columns), Math.Max(grid.Rows, row + rows));
        return expanded.TryPlace(tile.EntryId, column, row, columns, rows, out result);
    }

    private bool DropOnTileGrid(Point pointer, Guid source, bool preview)
    {
        DockGrid grid = TileGrid!;
        Point local = Viewport.TranslatePoint(pointer, TileCanvas);
        if (local.X < 0 || local.Y < 0 || local.X >= TileCanvas.Width || local.Y >= TileCanvas.Height)
            return false;

        int column = (int)(local.X / GridCell), row = (int)(local.Y / GridCell);
        DockTile? target = grid.Tiles.FirstOrDefault(tile => column >= tile.Column && column < tile.Column + tile.ColumnSpan &&
            row >= tile.Row && row < tile.Row + tile.RowSpan);
        DockTile? moving = grid.Tiles.FirstOrDefault(tile => tile.EntryId == source);
        if (target?.EntryId == source && moving?.Column == column && moving?.Row == row)
            return false;

        if (target != null && target.EntryId != source)
        {
            var (targetButton, after, center) = GetInsertion(pointer);
            if (center && targetButton?.Tag is Guid targetId && CanDropEntry(source, null, targetId, center: true))
            {
                if (preview)
                    ShowInsertion(targetButton, after, center: true);
                return preview || CommitEntryDrop(source, null, targetId, after, center: true);
            }
        }

        int columnSpan = moving?.ColumnSpan ?? 1, rowSpan = moving?.RowSpan ?? 1;
        DockGrid proposed;
        bool swap = target != null && target.EntryId != source;
        bool accepted = swap
            ? grid.TrySwap(source, target!.EntryId, out proposed)
            : grid.TryPlace(source, column, row, columnSpan, rowSpan, out proposed);
        if (!accepted)
            return false;
        if (preview)
        {
            if (swap)
            {
                column = target!.Column;
                row = target.Row;
            }
            Point origin = TileCanvas.TranslatePoint(new Point(column * GridCell, row * GridCell), Viewport);
            ShowTileOutline(new Rect(origin, new Size(columnSpan * GridCell, rowSpan * GridCell)), true);
        }
        else if (moving == null)
            session.MoveEntryToGrid(source, column, row);
        else
            session.UpdateGrid(proposed);
        return true;
    }

    private void ShowTileOutline(Rect bounds, bool valid)
    {
        DropIndicator.BorderBrush = valid ? (Brush)FindResource("DockAccent") : Brushes.IndianRed;
        DropIndicator.BorderThickness = new Thickness(1);
        DropIndicator.Background = Brushes.Transparent;
        DropIndicator.Width = Math.Max(1, bounds.Width - 2);
        DropIndicator.Height = Math.Max(1, bounds.Height - 2);
        Canvas.SetLeft(DropIndicator, bounds.X + 1);
        Canvas.SetTop(DropIndicator, bounds.Y + 1);
        DropIndicator.Visibility = Visibility.Visible;
    }

    private void ScrollTileGridDuringDrag(Point pointer)
    {
        static double Step(double offset, double extent) => offset < 12 ? -14 : offset > extent - 12 ? 14 : 0;
        GridScroll.ScrollToHorizontalOffset(GridScroll.HorizontalOffset + Step(pointer.X, Viewport.ActualWidth));
        GridScroll.ScrollToVerticalOffset(GridScroll.VerticalOffset + Step(pointer.Y, Viewport.ActualHeight));
    }

    internal object TestTileGrid(string action, Guid? entryId, int column, int row, double columns, double rows)
    {
        RequireVisibleDock();
        DockGrid grid = GridSnapshot();
        Size capacity = GridWorkCapacity(GetDockWorkArea(), GridCell);
        bool accepted = true;
        switch (action)
        {
            case "read":
                break;
            case "resize":
                SaveGridSize(grid, new Size(columns, rows), capacity);
                break;
            case "stretch":
                DockTile tile = grid.Tiles.FirstOrDefault(value => value.EntryId == entryId)
                    ?? throw new ArgumentException("Dock 中没有该图标。");
                accepted = TryStretchTile(grid, tile, new Size(columns, rows), capacity, out DockGrid stretched);
                if (accepted)
                    session.UpdateGrid(stretched);
                break;
            case "move" when HasTileGrid && entryId is Guid id && column > 0 && row > 0:
                Point local = new((column - .5) * GridCell, (row - .5) * GridCell);
                Point pointer = TileCanvas.TranslatePoint(local, Viewport);
                accepted = DropOnTileGrid(pointer, id, preview: true) && DropIndicator.IsVisible && DropOnTileGrid(pointer, id, preview: false);
                HideDropIndicator();
                break;
            default:
                throw new ArgumentException("无效的网格测试操作。");
        }
        UpdateLayout();
        grid = GridSnapshot();
        return new
        {
            accepted,
            grid = new
            {
                active = HasTileGrid,
                grid.Columns,
                grid.Rows,
                settingsButtonPresent = FindName("SettingsButton") != null,
                windowResizeHandles = ((Grid)Content).Children.OfType<Thumb>().Count(),
                tiles = grid.Tiles.Select(tile =>
                {
                    Button? button = RootEntryButton(tile.EntryId);
                    Rect bounds = button == null ? Rect.Empty : EntryScreenBounds(button);
                    return new
                    {
                        id = tile.EntryId, column = tile.Column + 1, row = tile.Row + 1,
                        tile.ColumnSpan, tile.RowSpan,
                        resizeHandlePresent = button?.Template.FindName("TileResize", button) is Thumb,
                        bounds = bounds.IsEmpty ? null : DescribeBounds(bounds)
                    };
                }).ToArray()
            }
        };
    }
}
