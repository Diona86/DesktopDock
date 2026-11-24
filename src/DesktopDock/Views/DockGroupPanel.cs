using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DesktopDock;

internal sealed class DockGroupPanel : Window
{
    internal const int AnimationMilliseconds = 170;
    private static readonly Brush CanvasColor = FrozenBrush(Color.FromArgb(247, 25, 31, 29));
    private static readonly Brush RuleColor = FrozenBrush(Color.FromRgb(59, 73, 69));
    private static readonly Brush Ink = FrozenBrush(Color.FromRgb(242, 247, 243));
    private static readonly Brush Muted = FrozenBrush(Color.FromRgb(178, 193, 187));
    private static readonly Brush Accent = FrozenBrush(Color.FromRgb(137, 215, 188));
    private static readonly Brush DropFill = FrozenBrush(Color.FromArgb(34, 137, 215, 188));

    private readonly Border surface;
    private readonly Canvas overlay;
    private readonly Border indicator;
    private readonly TextBlock title;
    private readonly ScrollViewer scroll;
    private readonly List<Button> entryButtons = [];
    private readonly ScaleTransform expansionScale = new(1, 1);
    private CancellationTokenSource? iconLoads;
    private bool animationRunning;
    private long lastDragScroll;

    internal DockGroupPanel(Window owner, Guid groupId, Guid? parentId, Style chromeStyle)
    {
        GroupId = groupId;
        ParentId = parentId;
        Owner = owner;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        UseLayoutRounding = true;
        AllowDrop = true;

        BackButton = new Button
        {
            Style = chromeStyle,
            Content = parentId.HasValue ? "‹" : "×",
            Width = 28,
            Height = 26,
            Foreground = Ink,
            FontSize = 19,
            ToolTip = parentId.HasValue ? "返回上一级。可将项目拖到这里移至上一级。" : "关闭分组。可将项目拖到这里移回 Dock。"
        };
        AutomationProperties.SetName(BackButton, parentId.HasValue ? "返回上一级" : "关闭分组");
        AutomationProperties.SetAutomationId(BackButton, "GroupPanelBack");

        title = new TextBlock
        {
            Margin = new Thickness(7, 0, 3, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Ink,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 7) };
        DockPanel.SetDock(BackButton, Dock.Left);
        header.Children.Add(BackButton);
        header.Children.Add(title);

        scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            PanningMode = PanningMode.Both,
            Focusable = false,
            Padding = new Thickness(0, 6, 0, 0)
        };
        scroll.PreviewMouseWheel += (_, e) =>
        {
            if (scroll.ScrollableHeight > 0 || scroll.ScrollableWidth <= 0)
                return;
            scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset - e.Delta / 3d);
            e.Handled = true;
        };

        var shell = new Grid();
        shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        shell.Children.Add(header);
        var separator = new Border { Child = scroll, BorderBrush = RuleColor, BorderThickness = new Thickness(0, 1, 0, 0) };
        Grid.SetRow(separator, 1);
        shell.Children.Add(separator);
        indicator = new Border { Visibility = Visibility.Collapsed, Background = DropFill, BorderBrush = Accent, CornerRadius = new CornerRadius(5) };
        overlay = new Canvas { IsHitTestVisible = false, ClipToBounds = true };
        overlay.Children.Add(indicator);
        Grid.SetRowSpan(overlay, 2);
        shell.Children.Add(overlay);

        surface = new Border
        {
            Background = CanvasColor,
            BorderBrush = RuleColor,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(8),
            RenderTransform = expansionScale,
            RenderTransformOrigin = new Point(0.5, 0.5),
            Child = shell
        };
        Content = surface;
        AutomationProperties.SetAutomationId(this, $"DockGroupPanel_{groupId:N}");
        Closed += (_, _) => CancelIcons();
    }

    internal Guid GroupId { get; }
    internal Guid? ParentId { get; }
    internal Button BackButton { get; }
    internal DockGroupExpandMode ExpandMode { get; private set; }
    internal DockExpandDirection Direction { get; private set; }
    internal DockExpandDirection ActualDirection { get; private set; }
    internal IReadOnlyList<Button> EntryButtons => entryButtons;
    internal bool AnimationActive => animationRunning;
    internal double AnimatedOpacity => surface.Opacity;
    internal bool FeedbackVisible => indicator.IsVisible;

    internal void Refresh(DockGroup group, DockOptions options, DockWindowPosition.WorkArea work, IReadOnlyList<Guid> entries,
        Func<Guid, double, double, double, CancellationToken, Button> createButton)
    {
        ExpandMode = group.ExpandMode;
        Direction = group.Direction == DockExpandDirection.Auto ? options.Layout.ExpansionDirection : group.Direction;
        Title = title.Text = group.Name;
        title.ToolTip = group.Name;
        MaxWidth = Math.Max(1, Math.Min(660, work.Width - 12));
        MaxHeight = Math.Max(1, Math.Min(470, work.Height - 12));
        MinWidth = Math.Min(182, MaxWidth);

        CancelIcons();
        iconLoads = new CancellationTokenSource();
        entryButtons.Clear();
        ClearFeedback();
        scroll.Content = null;

        double icon = options.IconSize;
        double side = icon + 8;
        double gap = options.Layout.IconSpacing;
        int limit = options.Layout.MaxVisibleIcons == 0 ? entries.Count : Math.Min(options.Layout.MaxVisibleIcons, entries.Count);
        int columns = Math.Clamp(options.Layout.GridTracks, 1, Math.Max(1, limit));
        Panel? cells = entries.Count == 0 ? null : ExpandMode switch
        {
            DockGroupExpandMode.Horizontal => new StackPanel { Orientation = Orientation.Horizontal },
            DockGroupExpandMode.Vertical => new StackPanel { Orientation = Orientation.Vertical },
            DockGroupExpandMode.Fan => new Canvas(),
            _ => new UniformGrid { Columns = columns }
        };

        foreach (Guid id in entries)
        {
            Button button = createButton(id, icon, side, work.Scale, iconLoads.Token);
            button.Margin = ExpandMode == DockGroupExpandMode.Fan ? new Thickness(0) : new Thickness(gap / 2);
            entryButtons.Add(button);
            cells!.Children.Add(button);
        }

        double cell = side + gap;
        scroll.MaxWidth = Math.Max(1, MaxWidth - 18);
        scroll.MaxHeight = Math.Max(1, MaxHeight - 53);
        if (limit > 0)
        {
            if (ExpandMode == DockGroupExpandMode.Horizontal)
                scroll.MaxWidth = Math.Min(scroll.MaxWidth, limit * cell + 1);
            else if (ExpandMode == DockGroupExpandMode.Vertical)
                scroll.MaxHeight = Math.Min(scroll.MaxHeight, limit * cell + 7);
            else if (ExpandMode == DockGroupExpandMode.Grid)
                scroll.MaxHeight = Math.Min(scroll.MaxHeight, Math.Max(1, limit / columns) * cell + 7);
        }

        if (cells is Canvas fan)
        {
            int capacity = Math.Max(1, Math.Min(7, (int)(scroll.MaxWidth / cell)));
            int perArc = Math.Min(capacity, Math.Max(1, limit));
            ArrangeFan(fan, perArc, side, cell);
            int visibleArcs = Math.Max(1, limit / perArc);
            double rise = perArc > 2 ? side * 0.50 : 0;
            scroll.MaxHeight = Math.Min(scroll.MaxHeight, visibleArcs * (cell + rise) + 7);
        }

        scroll.Content = cells is not null ? cells : new TextBlock
        {
            Text = "将项目拖入此分组",
            Foreground = Muted,
            Margin = new Thickness(6, 9, 6, 10),
            IsHitTestVisible = false
        };
        surface.Measure(new Size(MaxWidth, MaxHeight));
        Width = Math.Clamp(surface.DesiredSize.Width, MinWidth, MaxWidth);
        Height = Math.Clamp(surface.DesiredSize.Height, 1, MaxHeight);
    }

    internal void AnimateOpen()
    {
        if (!SystemParameters.ClientAreaAnimation)
            return;
        animationRunning = true;
        var motion = new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(AnimationMilliseconds))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        expansionScale.BeginAnimation(ScaleTransform.ScaleXProperty, motion);
        expansionScale.BeginAnimation(ScaleTransform.ScaleYProperty, motion);
        var opacityMotion = new DoubleAnimation(0.30, 1, motion.Duration) { FillBehavior = FillBehavior.Stop };
        opacityMotion.Completed += (_, _) => animationRunning = false;
        surface.BeginAnimation(OpacityProperty, opacityMotion);
    }

    internal void Place(Rect container, Rect anchor, DockWindowPosition.WorkArea work, DockExpandDirection preferred, double gapDips)
    {
        if (container.IsEmpty)
            return;
        if (anchor.IsEmpty)
            anchor = container;

        double gap = gapDips * work.Scale;
        Size size = new(Width * work.Scale, Height * work.Scale);
        double Available(DockExpandDirection side) => side switch
        {
            DockExpandDirection.Up => container.Top - work.Pixels.Top,
            DockExpandDirection.Down => work.Pixels.Bottom - container.Bottom,
            DockExpandDirection.Left => container.Left - work.Pixels.Left,
            _ => work.Pixels.Right - container.Right
        };
        double Required(DockExpandDirection side) => side is DockExpandDirection.Up or DockExpandDirection.Down ? size.Height + gap : size.Width + gap;

        DockExpandDirection direction = Direction == DockExpandDirection.Auto ? preferred : Direction;
        DockExpandDirection opposite = direction switch
        {
            DockExpandDirection.Up => DockExpandDirection.Down,
            DockExpandDirection.Down => DockExpandDirection.Up,
            DockExpandDirection.Left => DockExpandDirection.Right,
            _ => DockExpandDirection.Left
        };
        if (Available(direction) < Required(direction))
        {
            DockExpandDirection[] candidates = [opposite, DockExpandDirection.Up, DockExpandDirection.Down, DockExpandDirection.Left, DockExpandDirection.Right];
            direction = candidates.FirstOrDefault(side => Available(side) >= Required(side));
            if (direction == DockExpandDirection.Auto)
                direction = candidates.MaxBy(side => Available(side) / Math.Max(1, Required(side)));
        }

        ActualDirection = direction;
        double x = anchor.Left + (anchor.Width - size.Width) / 2;
        double y = anchor.Top + (anchor.Height - size.Height) / 2;
        Point location = direction switch
        {
            DockExpandDirection.Up => new Point(x, container.Top - gap - size.Height),
            DockExpandDirection.Down => new Point(x, container.Bottom + gap),
            DockExpandDirection.Left => new Point(container.Left - gap - size.Width, y),
            _ => new Point(container.Right + gap, y)
        };

        _ = new WindowInteropHelper(this).EnsureHandle();
        DockWindowPosition.Move(this, work.Clamp(location, size));
    }

    internal Button? EntryButton(Guid id) => entryButtons.FirstOrDefault(button => button.Tag is Guid candidate && candidate == id);

    internal Rect ButtonBounds(Button button) => new(button.TranslatePoint(new Point(), surface), new Size(button.ActualWidth, button.ActualHeight));

    internal bool IsOver(Button button, Point point) => button.IsVisible && ButtonBounds(button).Contains(point);

    internal Button? EntryAt(Point point)
    {
        var rect = new Rect(scroll.TranslatePoint(new Point(), surface), new Size(scroll.ViewportWidth, scroll.ViewportHeight));
        return rect.Contains(point) ? entryButtons.FirstOrDefault(button => IsOver(button, point)) : null;
    }

    internal Point GetLocalPoint(DragEventArgs e) => e.GetPosition(surface);

    internal void ScrollDuringDrag(Point point)
    {
        long now = Environment.TickCount64;
        if (now - lastDragScroll < 65 || (scroll.ScrollableWidth <= 0 && scroll.ScrollableHeight <= 0))
            return;

        var viewport = new Rect(scroll.TranslatePoint(new Point(), surface), new Size(scroll.ViewportWidth, scroll.ViewportHeight));
        if (!viewport.Contains(point))
            return;

        static int Step(double offset, double length) => offset < 12 ? -14 : offset > length - 12 ? 14 : 0;
        int dx = Step(point.X - viewport.Left, viewport.Width);
        int dy = Step(point.Y - viewport.Top, viewport.Height);
        if (dx == 0 && dy == 0)
            return;

        lastDragScroll = now;
        if (dx != 0 && scroll.ScrollableWidth > 0)
            scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset + dx);
        if (dy != 0 && scroll.ScrollableHeight > 0)
            scroll.ScrollToVerticalOffset(scroll.VerticalOffset + dy);
    }

    internal void ShowFeedback(Button? target, bool center, bool after, bool vertical)
    {
        Rect bounds = target is null ? new Rect(8, Math.Max(8, surface.ActualHeight - 10), Math.Max(4, surface.ActualWidth - 16), 2)
            : ButtonBounds(target);
        if (target is not null && !center)
        {
            bounds = vertical
                ? new Rect(bounds.X + 3, (after ? bounds.Bottom : bounds.Top) - 1, Math.Max(2, bounds.Width - 6), 2)
                : new Rect((after ? bounds.Right : bounds.Left) - 1, bounds.Y + 3, 2, Math.Max(2, bounds.Height - 6));
        }

        Point origin = surface.TranslatePoint(bounds.Location, overlay);
        Canvas.SetLeft(indicator, origin.X);
        Canvas.SetTop(indicator, origin.Y);
        indicator.Width = bounds.Width;
        indicator.Height = bounds.Height;
        indicator.BorderThickness = new Thickness(1);
        indicator.Visibility = Visibility.Visible;
    }

    internal void ClearFeedback() => indicator.Visibility = Visibility.Collapsed;

    private static void ArrangeFan(Canvas fan, int perArc, double side, double cell)
    {
        int count = fan.Children.Count;
        int rows = (int)Math.Ceiling(count / (double)perArc);
        double rise = perArc > 2 ? side * 0.50 : 0;
        fan.Width = Math.Min(perArc, count) * cell;
        fan.Height = rows * (cell + rise);
        for (int i = 0; i < count; i++)
        {
            int row = i / perArc;
            int position = i % perArc;
            int width = Math.Min(perArc, count - row * perArc);
            double t = width <= 2 ? 0 : 2.0 * position / (width - 1) - 1;
            double x = (fan.Width - width * cell) / 2 + position * cell + (cell - side) / 2;
            double y = row * (cell + rise) + rise * (1 - Math.Sqrt(1 - t * t));
            Canvas.SetLeft(fan.Children[i], x);
            Canvas.SetTop(fan.Children[i], y);
        }
    }

    private void CancelIcons()
    {
        iconLoads?.Cancel();
        iconLoads?.Dispose();
        iconLoads = null;
    }

    private static Brush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
