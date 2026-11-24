using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace DesktopDock;

public partial class DockWindow : Window
{
    private const double EdgeGap = 10;
    private const double GripLength = 26;
    private const double SettingsLength = 36;
    private const string ItemDragFormat = "DesktopDock.ItemId";

    private readonly DockSession session;
    private readonly DockIconService icons;
    private readonly Action showSettings;
    private readonly Action<DockItem> editItem;
    private readonly Action<DockItem> replaceIcon;
    private readonly Style buttonStyle;

    private CancellationTokenSource? iconLoads;
    private Button? pressedItem;
    private Point pressPoint;
    private Guid? draggingItem;
    private bool vertical;
    private int layoutTracks = 1;
    private bool groupExpandLayout;
    private long lastDragScroll;
    private DispatcherOperation? placement;
    private DispatcherOperation? dpiRefresh;
    private double layoutScale;
    private Point? moveStart;
    private Rect moveBounds;
    private DockWindowPosition.WorkArea moveArea;
    private bool movedDock;

    public DockWindow(DockSession session, DockIconService icons, Action showSettings,
        Action<DockItem> editItem, Action<DockItem> replaceIcon, Action requestHide)
    {
        this.session = session;
        this.icons = icons;
        this.showSettings = showSettings;
        this.editItem = editItem;
        this.replaceIcon = replaceIcon;
        InitializeComponent();
        buttonStyle = (Style)FindResource("DockButton");

        SettingsButton.Click += (_, _) => showSettings();
        Surface.ContextMenu = TrackInteractionMenu(new ContextMenu());
        Surface.ContextMenu.Items.Add(CreateMenuItem("创建分组", () => RunGroupDialog(() => DockGroupDialogs.Create(this, session, null))));
        Surface.ContextMenu.Items.Add(CreateMenuItem("打开设置", showSettings));
        Surface.ContextMenu.Items.Add(CreateMenuItem("隐藏 Dock", requestHide));
        Surface.PreviewMouseMove += (_, e) => UpdateGrip(e.OriginalSource as DependencyObject);
        Surface.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (HasGroupPanels && !HasButtonAncestor(e.OriginalSource as DependencyObject))
                CloseGroups();
        };
        Surface.MouseLeave += (_, _) => UpdateGrip(null, false);
        PreviewKeyDown += PreviewInteractionKey;
        MoveHandle.PreviewMouseLeftButtonDown += BeginMove;
        MoveHandle.PreviewMouseMove += ContinueMove;
        MoveHandle.PreviewMouseLeftButtonUp += EndMove;
        MoveHandle.LostMouseCapture += (_, _) => CancelMove();
        ItemScroll.PreviewMouseWheel += ScrollItems;
        PreviewDragEnter += PreviewDockDrop;
        PreviewDragOver += PreviewDockDrop;
        PreviewDragLeave += (_, _) => HideDropIndicator();
        PreviewDrop += DropOnDock;

        SourceInitialized += (_, _) => Refresh();
        SizeChanged += (_, _) => QueuePlacement();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                QueuePlacement();
                QueueGroupLayoutOpen();
            }
            else
            {
                UpdateGrip(null, false);
                CloseInteractionMenus();
                CloseGroups();
            }
        };
        session.Changed += SessionChanged;
        Closed += (_, _) =>
        {
            session.Changed -= SessionChanged;
            CloseGroups();
            CancelIconLoads();
            placement?.Abort();
            dpiRefresh?.Abort();
        };
        Refresh();
    }

    private bool PositionLocked => session.Options.DockPositionLocked || session.Options.GlobalPositionLocked;

    private void SessionChanged(object? sender, EventArgs e)
    {
        Refresh();
        RefreshGroupPanels();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (newDpi.DpiScaleX != layoutScale && dpiRefresh?.Status != DispatcherOperationStatus.Pending)
        {
            // The window can receive element-level routed DPI notifications while measuring new icons.
            // Use the window's own DPI callback and defer changes to the visual tree until layout returns.
            dpiRefresh = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (GetDockWorkArea().Scale != layoutScale)
                    Refresh();
            }));
        }
    }

    private void Refresh()
    {
        DockOptions options = session.Options;
        DockWindowPosition.WorkArea work = GetDockWorkArea();
        layoutScale = work.Scale;
        DockLayoutOptions layout = options.Layout;
        bool edgeVertical = options.Placement == DockPlacement.Left || options.Placement == DockPlacement.Right ||
            (options.Placement == DockPlacement.Floating && options.FloatingVertical);
        vertical = layout.Mode switch
        {
            DockLayoutMode.SingleColumn or DockLayoutMode.MultiColumnGrid => true,
            DockLayoutMode.SingleRow or DockLayoutMode.MultiRowGrid => false,
            _ => edgeVertical
        };
        bool openGroup = !groupExpandLayout && layout.Mode == DockLayoutMode.GroupExpand;
        groupExpandLayout = layout.Mode == DockLayoutMode.GroupExpand;
        DockEntry[] entries = session.Entries.Where(entry => entry.ParentId == null).ToArray();
        int iconCount = entries.Length;
        layoutTracks = layout.Mode is DockLayoutMode.MultiColumnGrid or DockLayoutMode.MultiRowGrid
            ? Math.Min(layout.GridTracks, Math.Max(1, Math.Min(iconCount, layout.MaxVisibleIcons == 0 ? iconCount : layout.MaxVisibleIcons))) : 1;
        double cross = vertical ? work.Width : work.Height;
        double main = vertical ? work.Height : work.Width;
        double preferredThickness = layoutTracks == 1 ? options.DockThickness
            : Math.Max(options.DockThickness, layoutTracks * (options.IconSize + 8 + layout.IconSpacing) + 12);
        double thickness = Math.Min(preferredThickness, Math.Max(1, cross - EdgeGap * 2));
        double padding = Math.Clamp((thickness - options.IconSize - 4) / 2, 0, 5);
        double crossSpacing = layoutTracks > 1 ? layout.IconSpacing : 0;
        double side = Math.Min(options.IconSize + 8, Math.Max(1, (thickness - (padding + 1) * 2) / layoutTracks - crossSpacing));
        double icon = Math.Min(options.IconSize, Math.Max(1, side - 2));
        double emptyItemLength = vertical ? Math.Max(34, side) : 92;
        double chrome = (padding + 1) * 2 + GripLength + SettingsLength;
        int mainCells = (int)Math.Ceiling(Math.Max(1, iconCount) / (double)layoutTracks);
        double cell = side + layout.IconSpacing;
        double desired = chrome + (iconCount == 0 ? emptyItemLength : mainCells * cell);
        double minimum = chrome + (iconCount == 0 ? emptyItemLength : cell);
        double cap = layout.MaxVisibleIcons == 0 ? double.PositiveInfinity
            : chrome + Math.Max(1, layout.MaxVisibleIcons / layoutTracks) * cell;
        double length = Math.Min(options.DockLength > 0 ? Math.Max(options.DockLength, minimum) : desired,
            Math.Max(1, main - EdgeGap * 2));
        if (iconCount > 0)
            length = Math.Min(length, cap);

        Surface.Padding = new Thickness(padding);
        Surface.Width = vertical ? thickness : length;
        Surface.Height = vertical ? length : thickness;
        DockPanel.SetDock(GripZone, vertical ? Dock.Top : Dock.Left);
        DockPanel.SetDock(SettingsButton, vertical ? Dock.Bottom : Dock.Right);
        GripZone.Width = vertical ? double.NaN : GripLength;
        GripZone.Height = vertical ? GripLength : double.NaN;
        MoveHandle.Width = vertical ? 24 : 20;
        MoveHandle.Height = vertical ? 20 : 24;
        MoveHandle.Visibility = Visibility.Hidden;
        GripDots.Orientation = vertical ? Orientation.Horizontal : Orientation.Vertical;
        SettingsButton.Margin = vertical ? new Thickness(0, 6, 0, 0) : new Thickness(6, 0, 0, 0);
        ItemStrip.Rows = vertical ? mainCells : layoutTracks;
        ItemStrip.Columns = vertical ? layoutTracks : mainCells;
        ItemStrip.HorizontalAlignment = vertical ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        ItemStrip.VerticalAlignment = vertical ? VerticalAlignment.Top : VerticalAlignment.Center;
        ItemScroll.HorizontalScrollBarVisibility = vertical ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Hidden;
        ItemScroll.VerticalScrollBarVisibility = vertical ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Disabled;

        RebuildItems(entries, side, icon, emptyItemLength, work.Scale);
        QueuePlacement();
        if (openGroup && IsVisible)
            QueueGroupLayoutOpen();
    }

    private void RebuildItems(DockEntry[] entries, double side, double icon, double emptyLength, double scale)
    {
        CancelIconLoads();
        iconLoads = new CancellationTokenSource();
        ItemStrip.Children.Clear();

        if (entries.Length == 0)
        {
            var add = new Button
            {
                Style = buttonStyle,
                Width = vertical ? side : emptyLength,
                Height = vertical ? emptyLength : side,
                Content = vertical ? "添加\n项目" : "+  添加项目",
                FontSize = 12,
                ToolTip = "添加文件、文件夹或网址"
            };
            AutomationProperties.SetAutomationId(add, "DockAddItem");
            AutomationProperties.SetName(add, "添加项目");
            add.Click += (_, _) => showSettings();
            ItemStrip.Children.Add(add);
            return;
        }

        double gap = session.Options.Layout.IconSpacing / 2;
        foreach (DockEntry entry in entries)
        {
            Button button = CreateEntryButton(entry.Id, icon, side, scale, iconLoads.Token);
            button.Margin = new Thickness(vertical && layoutTracks == 1 ? 0 : gap, !vertical && layoutTracks == 1 ? 0 : gap,
                vertical && layoutTracks == 1 ? 0 : gap, !vertical && layoutTracks == 1 ? 0 : gap);
            ItemStrip.Children.Add(button);
        }
    }

    private async Task LoadIconAsync(DockItem? item, DockGroup? group, Image image, int pixels, CancellationToken cancellationToken)
    {
        try
        {
            ImageSource bitmap = group == null ? await icons.GetAsync(item!, pixels, cancellationToken)
                : await icons.GetAsync(group, pixels, cancellationToken);
            if (!cancellationToken.IsCancellationRequested)
                image.Source = bitmap;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // This visual was replaced by a newer view of the configuration.
        }
    }

    private void CancelIconLoads()
    {
        iconLoads?.Cancel();
        iconLoads?.Dispose();
        iconLoads = null;
    }

    private ContextMenu CreateItemMenu(DockItem item)
    {
        var menu = new ContextMenu();
        menu.Items.Add(CreateMenuItem("打开", () => OpenFromDock(item.Id)));
        menu.Items.Add(CreateMenuItem("编辑", () => editItem(item)));
        menu.Items.Add(CreateMenuItem("替换图标", () => replaceIcon(item)));
        MenuItem location = CreateMenuItem("打开所在位置", () => DockTargetLauncher.ShowInFolder(item));
        location.IsEnabled = item.Kind != DockItemKind.Url;
        menu.Items.Add(location);
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem("从 Dock 移除", () => session.RemoveItem(item.Id)));
        return TrackInteractionMenu(menu);
    }

    internal void OpenFromDock(Guid id)
    {
        DockItem item = session.Items.FirstOrDefault(item => item.Id == id)
            ?? throw new InvalidOperationException("该 Dock 项目已移除。");
        DockTargetLauncher.Open(item);
    }

    private MenuItem CreateMenuItem(string label, Action action)
    {
        var item = new MenuItem { Header = label };
        item.Click += (_, _) => TryAction(action);
        return item;
    }

    private void TryAction(Action action)
    {
        try
        {
            action();
        }
        catch (Exception error) when (IsActionError(error))
        {
            ShowError(error.Message);
        }
    }

    private void ShowError(string message) =>
        MessageBox.Show(this, message, "Desktop Dock", MessageBoxButton.OK, MessageBoxImage.Warning);

    private static bool IsActionError(Exception error) =>
        error is IOException or UnauthorizedAccessException or ArgumentException or Win32Exception or InvalidOperationException;

    private void BeginReorder(Button source, Guid id, MouseEventArgs e)
    {
        if (pressedItem != source || e.LeftButton != MouseButtonState.Pressed)
            return;

        Vector delta = e.GetPosition(source) - pressPoint;
        if (!BeyondDragThreshold(delta))
            return;

        e.Handled = true;
        pressedItem = null;
        draggingItem = id;
        if (source.IsMouseCaptured)
            source.ReleaseMouseCapture();

        try
        {
            DragDrop.DoDragDrop(source, new DataObject(ItemDragFormat, id.ToString("D")), DragDropEffects.Move);
        }
        finally
        {
            draggingItem = null;
            HideDropIndicator();
        }
    }

    private void PreviewDockDrop(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        HideDropIndicator();
        if (GetDraggedEntry(e) is Guid source)
        {
            Point pointer = e.GetPosition(Viewport);
            ScrollDuringDrag(pointer);
            if (ShowItemDropFeedback(pointer, source))
            {
                e.Effects = DragDropEffects.Move;
            }
        }
        else if (e.Data.GetDataPresent(DataFormats.FileDrop, false))
        {
            e.Effects = GetExternalDropEffect(e.AllowedEffects);
        }

        e.Handled = true;
    }

    private void DropOnDock(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        e.Handled = true;
        HideDropIndicator();
        if (GetDraggedEntry(e) is Guid source)
        {
            Point pointer = e.GetPosition(Viewport);
            bool committed = false;
            TryAction(() => committed = CommitItemDrop(source, pointer));
            if (committed)
                e.Effects = DragDropEffects.Move;

            return;
        }

        DragDropEffects effect = GetExternalDropEffect(e.AllowedEffects);
        if (effect == DragDropEffects.None || !e.Data.GetDataPresent(DataFormats.FileDrop, false) ||
            e.Data.GetData(DataFormats.FileDrop, false) is not string[] targets)
            return;

        var (target, _, center) = GetInsertion(e.GetPosition(Viewport));
        Guid? parent = center && target?.Tag is Guid candidate && session.Groups.Any(group => group.Id == candidate) ? candidate : null;
        FileDropResult result = AcceptFiles(targets, parent);
        if (result.Errors.Count > 0)
        {
            string summary = string.Join(Environment.NewLine, result.Errors.Take(3));
            if (result.Errors.Count > 3)
                summary += $"{Environment.NewLine}另有 {result.Errors.Count - 3} 项无法添加。";
            Dispatcher.InvokeAsync(() => ShowError(summary));
        }

        if (result.Added > 0)
            e.Effects = effect;
    }

    internal sealed record FileDropResult(int Added, IReadOnlyList<string> Errors);

    internal FileDropResult AcceptFiles(IEnumerable<string> paths, Guid? parentId = null)
    {
        int added = 0;
        var errors = new List<string>();
        foreach (string path in paths)
        {
            try
            {
                session.AddItem(path, parentId: parentId);
                added++;
            }
            catch (Exception error) when (IsActionError(error))
            {
                errors.Add(error.Message);
            }
        }

        return new FileDropResult(added, errors);
    }

    private static DragDropEffects GetExternalDropEffect(DragDropEffects allowed)
    {
        if ((allowed & DragDropEffects.Link) != 0)
            return DragDropEffects.Link;

        return allowed & DragDropEffects.Copy;
    }

    private (Button? Button, bool After, bool Center) GetInsertion(Point pointer)
    {
        Button? nearest = null;
        double distance = double.PositiveInfinity;
        double fraction = 0;
        foreach (Button item in ItemStrip.Children.OfType<Button>())
        {
            if (item.Tag is not Guid || item.ActualWidth <= 0 || item.ActualHeight <= 0)
                continue;

            Point local = Viewport.TranslatePoint(pointer, item);
            double ratio = vertical ? local.Y / item.ActualHeight : local.X / item.ActualWidth;
            if (local.X >= 0 && local.X < item.ActualWidth && local.Y >= 0 && local.Y < item.ActualHeight)
                return (item, ratio >= .5, ratio is >= .32 and <= .68);
            double squareDistance = (local - new Point(item.ActualWidth / 2, item.ActualHeight / 2)).LengthSquared;
            if (squareDistance < distance)
            {
                distance = squareDistance;
                nearest = item;
                fraction = ratio;
            }
        }

        return (nearest, fraction >= .5, false);
    }

    private bool ShowItemDropFeedback(Point pointer, Guid source)
    {
        (Button? target, bool after, bool center) = GetInsertion(pointer);
        if (!CanDropEntry(source, null, target?.Tag as Guid?, center))
            return false;

        if (target != null)
            ShowInsertion(target, after, center);
        return true;
    }

    private bool CommitItemDrop(Guid source, Point pointer)
    {
        (Button? target, bool after, bool center) = GetInsertion(pointer);
        return CommitEntryDrop(source, null, target?.Tag as Guid?, after, center);
    }

    private void ShowInsertion(Button target, bool after, bool center)
    {
        Point origin = target.TranslatePoint(new Point(), Viewport);
        Brush accent = (Brush)FindResource("DockAccent");
        DropIndicator.BorderBrush = accent;
        DropIndicator.BorderThickness = center ? new Thickness(1) : new Thickness(0);
        DropIndicator.Background = center ? Brushes.Transparent : accent;
        if (center)
        {
            DropIndicator.Width = target.ActualWidth;
            DropIndicator.Height = target.ActualHeight;
            Canvas.SetLeft(DropIndicator, origin.X);
            Canvas.SetTop(DropIndicator, origin.Y);
        }
        else if (vertical)
        {
            DropIndicator.Width = Math.Max(2, target.ActualWidth - 8);
            DropIndicator.Height = 2;
            Canvas.SetLeft(DropIndicator, origin.X + 4);
            Canvas.SetTop(DropIndicator, Math.Clamp(origin.Y + (after ? target.ActualHeight : -2), 0,
                Math.Max(0, Viewport.ActualHeight - 2)));
        }
        else
        {
            DropIndicator.Width = 2;
            DropIndicator.Height = Math.Max(2, target.ActualHeight - 8);
            Canvas.SetLeft(DropIndicator, Math.Clamp(origin.X + (after ? target.ActualWidth : -2), 0,
                Math.Max(0, Viewport.ActualWidth - 2)));
            Canvas.SetTop(DropIndicator, origin.Y + 4);
        }

        DropIndicator.Visibility = Visibility.Visible;
    }

    private void HideDropIndicator() => DropIndicator.Visibility = Visibility.Collapsed;

    private void ScrollItems(object sender, MouseWheelEventArgs e)
    {
        if (!vertical && ItemScroll.ScrollableWidth > 0)
        {
            ItemScroll.ScrollToHorizontalOffset(ItemScroll.HorizontalOffset - e.Delta / 3d);
            e.Handled = true;
        }
    }

    private void ScrollDuringDrag(Point pointer)
    {
        long now = Environment.TickCount64;
        if (now - lastDragScroll < 65)
            return;

        double axis = vertical ? pointer.Y : pointer.X;
        double length = vertical ? Viewport.ActualHeight : Viewport.ActualWidth;
        double change = axis < 12 ? -14 : axis > length - 12 ? 14 : 0;
        if (change == 0)
            return;

        lastDragScroll = now;
        if (vertical)
            ItemScroll.ScrollToVerticalOffset(ItemScroll.VerticalOffset + change);
        else
            ItemScroll.ScrollToHorizontalOffset(ItemScroll.HorizontalOffset + change);
    }

    private void UpdateGrip(DependencyObject? source, bool inSurface = true)
    {
        while (source is not null and not Button)
        {
            source = source is Visual or Visual3D ? VisualTreeHelper.GetParent(source)
                : (source as FrameworkContentElement)?.Parent;
        }

        bool show = !PositionLocked && (moveStart.HasValue || (inSurface && (source is null || source == MoveHandle)));
        MoveHandle.Visibility = show ? Visibility.Visible : Visibility.Hidden;
    }

    private void BeginMove(object sender, MouseButtonEventArgs e)
    {
        if (PositionLocked || !DockWindowPosition.TryGetPointer(out Point point) ||
            !DockWindowPosition.TryGetBounds(this, out moveBounds) || !MoveHandle.CaptureMouse())
            return;

        moveArea = GetDockWorkArea();
        moveStart = point;
        movedDock = false;
        e.Handled = true;
    }

    private void ContinueMove(object sender, MouseEventArgs e)
    {
        if (moveStart is not Point start || e.LeftButton != MouseButtonState.Pressed ||
            !DockWindowPosition.TryGetPointer(out Point position))
            return;

        Vector delta = position - start;
        if (!movedDock && !BeyondDragThreshold(delta / moveArea.Scale))
            return;

        movedDock = true;
        MoveDock(moveBounds, moveArea, delta);
        e.Handled = true;
    }

    private void EndMove(object sender, MouseButtonEventArgs e)
    {
        if (!moveStart.HasValue)
            return;

        moveStart = null;
        MoveHandle.ReleaseMouseCapture();
        e.Handled = true;
        if (movedDock)
            TryAction(() => CommitDockMove(moveArea));

        QueuePlacement();
    }

    private void MoveDock(Rect start, DockWindowPosition.WorkArea work, Vector delta) =>
        DockWindowPosition.Move(this, work.Clamp(start.Location + delta, start.Size));

    private bool CommitDockMove(DockWindowPosition.WorkArea work)
    {
        if (PositionLocked || !DockWindowPosition.TryGetBounds(this, out Rect bounds))
            return false;

        session.UpdateOptions(session.Options with
        {
            Placement = DockPlacement.Floating,
            FloatingVertical = vertical,
            FloatingLeft = (bounds.Left - work.Pixels.Left) / work.Scale,
            FloatingTop = (bounds.Top - work.Pixels.Top) / work.Scale
        });
        return true;
    }

    private void CancelMove()
    {
        if (!moveStart.HasValue)
            return;

        moveStart = null;
        QueuePlacement();
    }

    private static bool BeyondDragThreshold(Vector delta) =>
        Math.Abs(delta.X) >= SystemParameters.MinimumHorizontalDragDistance ||
        Math.Abs(delta.Y) >= SystemParameters.MinimumVerticalDragDistance;

    private void QueuePlacement()
    {
        if (placement?.Status != DispatcherOperationStatus.Pending)
            placement = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(ApplyPlacement));
    }

    private void ApplyPlacement()
    {
        if (moveStart.HasValue || !DockWindowPosition.TryGetBounds(this, out Rect bounds))
            return;

        DockWindowPosition.WorkArea work = GetDockWorkArea();
        Rect area = work.Pixels;
        double inset = EdgeGap * work.Scale;
        double centeredX = area.Left + (area.Width - bounds.Width) / 2;
        double centeredY = area.Top + (area.Height - bounds.Height) / 2;
        DockOptions options = session.Options;
        Point desired = options.Placement switch
        {
            DockPlacement.Top => new Point(centeredX, area.Top + inset),
            DockPlacement.Left => new Point(area.Left + inset, centeredY),
            DockPlacement.Right => new Point(area.Right - bounds.Width - inset, centeredY),
            DockPlacement.Floating => new Point(
                options.FloatingLeft is double left ? area.Left + left * work.Scale : centeredX,
                options.FloatingTop is double top ? area.Top + top * work.Scale : centeredY),
            _ => new Point(centeredX, area.Bottom - bounds.Height - inset)
        };

        DockWindowPosition.Move(this, work.Clamp(desired, bounds.Size));
    }
}
