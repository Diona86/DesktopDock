using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace DesktopDock;

public partial class DockWindow
{
    internal Action<bool>? ModalInteractionRequested { get; set; }

    private Button CreateEntryButton(Guid id, double icon, double side, double scale, CancellationToken cancellationToken = default)
    {
        DockGroup? group = session.Groups.FirstOrDefault(value => value.Id == id);
        DockItem? item = group == null ? session.Items.FirstOrDefault(value => value.Id == id) : null;
        string name = group?.Name ?? item?.Name ?? throw new InvalidOperationException("该 Dock 条目已移除。");
        var image = new Image { Width = icon, Height = icon, Stretch = Stretch.Uniform, IsHitTestVisible = false };
        var button = new Button
        {
            Style = buttonStyle,
            Width = side,
            Height = side,
            Content = image,
            ContextMenu = group == null ? CreateItemMenu(item!) : CreateGroupMenu(group),
            Tag = id
        };
        button.ToolTip = new ToolTip
        {
            Content = name,
            PlacementTarget = button,
            Placement = session.Options.Placement switch
            {
                DockPlacement.Top => PlacementMode.Bottom,
                DockPlacement.Left => PlacementMode.Right,
                DockPlacement.Right => PlacementMode.Left,
                _ => PlacementMode.Top
            }
        };
        AutomationProperties.SetAutomationId(button, group == null ? $"DockItem_{id:N}" : $"DockGroup_{id:N}");
        AutomationProperties.SetName(button, name);
        ToolTipService.SetInitialShowDelay(button, 400);
        ToolTipService.SetBetweenShowDelay(button, 100);
        button.Click += (_, _) => TryAction(() =>
        {
            if (group == null)
                OpenFromDock(id);
            else
                OpenGroup(id);
        });
        button.PreviewMouseLeftButtonDown += (_, e) =>
        {
            pressedItem = button;
            pressPoint = e.GetPosition(button);
        };
        button.PreviewMouseLeftButtonUp += (_, _) => pressedItem = null;
        button.LostMouseCapture += (_, _) => { if (pressedItem == button) pressedItem = null; };
        button.PreviewMouseMove += (_, e) => BeginReorder(button, id, e);
        _ = LoadIconAsync(item, group, image, (int)Math.Ceiling(icon * scale), cancellationToken);
        return button;
    }

    private ContextMenu CreateGroupMenu(DockGroup group)
    {
        var menu = new ContextMenu();
        menu.Items.Add(CreateMenuItem("展开分组", () => OpenGroup(group.Id)));
        if (session.Entries.Any(entry => entry.Id == group.Id && entry.ParentId == null))
            menu.Items.Add(CreateMenuItem("创建子分组", () => RunGroupDialog(() => DockGroupDialogs.Create(this, session, group.Id))));
        menu.Items.Add(CreateMenuItem("编辑分组", () => RunGroupDialog(() => DockGroupDialogs.Edit(this, session, group))));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem("删除分组…", () => RunGroupDialog(() => DockGroupDialogs.Delete(this, session, group))));
        return TrackInteractionMenu(menu);
    }

    private void RunGroupDialog(Action action)
    {
        ModalInteractionRequested?.Invoke(true);
        try { action(); }
        finally { ModalInteractionRequested?.Invoke(false); }
    }

    private Guid? GetDraggedEntry(DragEventArgs e) => draggingItem is Guid id
        && e.Data.GetDataPresent(ItemDragFormat, false)
        && e.Data.GetData(ItemDragFormat, false) is string text && Guid.TryParse(text, out Guid parsed) && parsed == id
        ? id : null;

    private bool CanDropEntry(Guid source, Guid? parentId, Guid? targetId, bool center)
    {
        DockEntry? sourceEntry = session.Entries.FirstOrDefault(entry => entry.Id == source);
        if (sourceEntry == null || targetId == source || parentId == source)
            return false;

        bool sourceIsGroup = session.Groups.Any(group => group.Id == source);
        bool targetIsGroup = center && session.Groups.Any(group => group.Id == targetId);
        Guid? destination = targetIsGroup ? targetId : parentId;
        if (destination.HasValue && !session.Groups.Any(group => group.Id == destination))
            return false;
        if (targetIsGroup && sourceEntry.ParentId == destination)
            return false;

        bool secondLevelParent = destination.HasValue && session.Entries.Any(entry => entry.Id == destination && entry.ParentId != null);
        if (sourceIsGroup && destination.HasValue)
            return !secondLevelParent && !session.Entries.Any(entry => entry.ParentId == source && session.Groups.Any(group => group.Id == entry.Id));

        return !center || targetId == null || targetIsGroup || sourceIsGroup || !secondLevelParent;
    }

    private bool CommitEntryDrop(Guid source, Guid? parentId, Guid? targetId, bool after, bool center)
    {
        if (!CanDropEntry(source, parentId, targetId, center))
            return false;
        if (center && targetId is Guid target)
        {
            if (session.Groups.Any(group => group.Id == target))
                session.MoveEntry(source, target);
            else if (session.Items.Any(item => item.Id == source) && session.Items.Any(item => item.Id == target))
                session.CreateGroupFromItems(source, target);
            else
                session.MoveEntry(source, parentId, target, after);
        }
        else
        {
            session.MoveEntry(source, parentId, targetId, after);
        }
        return true;
    }

    private Button? RootEntryButton(Guid id) => ItemStrip.Children.OfType<Button>().FirstOrDefault(button => button.Tag is Guid key && key == id);

    private Rect EntryScreenBounds(Button button)
    {
        if (!button.IsVisible || PresentationSource.FromVisual(button) == null)
            return Rect.Empty;
        return new Rect(button.PointToScreen(new Point(0, 0)), button.PointToScreen(new Point(button.ActualWidth, button.ActualHeight)));
    }

    private void QueueGroupLayoutOpen()
    {
        if (!groupExpandLayout)
            return;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!IsVisible || !groupExpandLayout || HasGroupPanels)
                return;
            DockEntry? first = session.Entries.FirstOrDefault(entry => entry.ParentId == null && session.Groups.Any(group => group.Id == entry.Id));
            if (first != null)
                OpenGroup(first.Id);
        }));
    }

    private static bool HasButtonAncestor(DependencyObject? element)
    {
        while (element != null)
        {
            if (element is Button)
                return true;
            element = element is Visual or Visual3D ? VisualTreeHelper.GetParent(element) : (element as FrameworkContentElement)?.Parent;
        }
        return false;
    }
}
