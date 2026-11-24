using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace DesktopDock;

public partial class DockWindow
{
    private readonly List<DockGroupPanel> groupPanels = [];

    internal bool HasGroupPanels => groupPanels.Count != 0;
    internal DockGroupPanel? CurrentGroupPanel => HasGroupPanels ? groupPanels[^1] : null;
    internal IReadOnlyList<DockGroupPanel> OpenGroupPanels => groupPanels;
    internal event Action? GroupPanelsChanged;

    internal Rect[] GroupPanelBounds() => groupPanels
        .Select(panel => panel.IsVisible && DockWindowPosition.TryGetBounds(panel, out Rect bounds) ? bounds : Rect.Empty)
        .Where(bounds => !bounds.IsEmpty).ToArray();

    internal void OpenGroup(Guid id)
    {
        if (!IsVisible)
            throw new InvalidOperationException("请先显示 Dock。");

        DockGroup group = session.Groups.FirstOrDefault(item => item.Id == id)
            ?? throw new ArgumentException("该分组不存在。", nameof(id));
        Guid? parentId = session.Entries.First(entry => entry.Id == id).ParentId;
        int existing = groupPanels.FindIndex(panel => panel.GroupId == id);
        if (existing >= 0)
        {
            CloseGroupsFrom(existing);
            return;
        }

        if (parentId is Guid parent)
        {
            if (groupPanels.Count == 0 || groupPanels[0].GroupId != parent)
                OpenGroup(parent);
            CloseGroupsFrom(1);
        }
        else
        {
            CloseGroups();
        }

        var panel = new DockGroupPanel(this, id, parentId, buttonStyle);
        panel.ContextMenu = CreatePanelMenu(panel);
        panel.BackButton.Click += (_, _) => CloseGroupAndChildren(panel);
        panel.PreviewKeyDown += PreviewInteractionKey;
        panel.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (IsPanelControl(e.OriginalSource as DependencyObject))
                return;
            CloseGroupAndChildren(panel);
            e.Handled = true;
        };
        panel.PreviewDragEnter += (_, e) => PanelDragOver(panel, e);
        panel.PreviewDragOver += (_, e) => PanelDragOver(panel, e);
        panel.PreviewDragLeave += (_, _) => panel.ClearFeedback();
        panel.PreviewDrop += (_, e) => PanelDrop(panel, e);
        panel.Closed += (_, _) => CloseGroupAndChildren(panel);

        UpdateLayout();
        RefreshPanel(panel, group, GetDockWorkArea());
        if (!HasGroupPanels)
        {
            LocationChanged += OnGroupOwnerLocationChanged;
            SizeChanged += OnGroupOwnerSizeChanged;
        }
        groupPanels.Add(panel);
        PositionGroupPanels(bringAnchorIntoView: true);
        panel.Show();
        panel.AnimateOpen();
        GroupPanelsChanged?.Invoke();
    }

    internal bool CloseTopGroup()
    {
        if (!HasGroupPanels)
            return false;
        CloseGroupsFrom(groupPanels.Count - 1);
        return true;
    }

    internal void CloseGroups() => CloseGroupsFrom(0);

    private void CloseGroupAndChildren(DockGroupPanel panel)
    {
        int index = groupPanels.IndexOf(panel);
        if (index >= 0)
            CloseGroupsFrom(index, alreadyClosed: !panel.IsVisible ? panel : null);
    }

    private void CloseGroupsFrom(int index, DockGroupPanel? alreadyClosed = null)
    {
        if (index >= groupPanels.Count)
            return;

        for (int i = groupPanels.Count - 1; i >= index; i--)
        {
            DockGroupPanel panel = groupPanels[i];
            groupPanels.RemoveAt(i);
            if (!ReferenceEquals(panel, alreadyClosed))
                panel.Close();
        }
        if (!HasGroupPanels)
        {
            LocationChanged -= OnGroupOwnerLocationChanged;
            SizeChanged -= OnGroupOwnerSizeChanged;
        }
        GroupPanelsChanged?.Invoke();
    }

    private void RefreshGroupPanels()
    {
        if (!HasGroupPanels)
            return;

        DockWindowPosition.WorkArea work = GetDockWorkArea();
        for (int i = 0; i < groupPanels.Count; i++)
        {
            DockGroupPanel panel = groupPanels[i];
            DockGroup? group = session.Groups.FirstOrDefault(item => item.Id == panel.GroupId);
            DockEntry? entry = session.Entries.FirstOrDefault(item => item.Id == panel.GroupId);
            if (group == null || entry == null || entry.ParentId != panel.ParentId)
            {
                CloseGroupsFrom(i);
                break;
            }
            RefreshPanel(panel, group, work);
        }
        PositionGroupPanels();
    }

    private void RefreshPanel(DockGroupPanel panel, DockGroup group, DockWindowPosition.WorkArea work)
    {
        Guid[] ids = session.Entries.Where(entry => entry.ParentId == group.Id).Select(entry => entry.Id).ToArray();
        panel.Refresh(group, session.Options, work, ids, CreateEntryButton);
    }

    private void OnGroupOwnerLocationChanged(object? sender, EventArgs e) => PositionGroupPanels();
    private void OnGroupOwnerSizeChanged(object sender, SizeChangedEventArgs e) => PositionGroupPanels();

    private void PositionGroupPanels(bool bringAnchorIntoView = false)
    {
        if (!HasGroupPanels || !DockWindowPosition.TryGetBounds(this, out Rect dockBounds))
            return;

        DockWindowPosition.WorkArea work = GetDockWorkArea();
        Rect container = dockBounds;
        DockGroupPanel? previous = null;
        foreach (DockGroupPanel panel in groupPanels)
        {
            Button? button = previous == null ? RootEntryButton(panel.GroupId) : previous.EntryButton(panel.GroupId);
            if (bringAnchorIntoView && button != null)
            {
                button.BringIntoView();
                if (previous == null)
                    UpdateLayout();
                else
                    previous.UpdateLayout();
            }

            Rect anchor = button != null ? EntryScreenBounds(button) : Rect.Empty;
            if (!anchor.IsEmpty && !container.IntersectsWith(anchor))
                anchor = Rect.Empty;
            DockExpandDirection preferred = previous == null ? InitialGroupDirection(dockBounds, work.Pixels)
                : (container.Right + (container.Width / 2) <= work.Pixels.Right || container.Left - work.Pixels.Left <= work.Pixels.Right - container.Right
                    ? DockExpandDirection.Right : DockExpandDirection.Left);

            panel.Place(container, anchor, work, preferred, session.Options.Layout.PanelSpacing);
            if (!DockWindowPosition.TryGetBounds(panel, out container))
                break;
            previous = panel;
        }
    }

    private DockExpandDirection InitialGroupDirection(Rect dockBounds, Rect work) => session.Options.Placement switch
    {
        DockPlacement.Bottom => DockExpandDirection.Up,
        DockPlacement.Top => DockExpandDirection.Down,
        DockPlacement.Left => DockExpandDirection.Right,
        DockPlacement.Right => DockExpandDirection.Left,
        _ => dockBounds.Top - work.Top >= work.Bottom - dockBounds.Bottom ? DockExpandDirection.Up : DockExpandDirection.Down
    };

    private ContextMenu CreatePanelMenu(DockGroupPanel panel)
    {
        var menu = new ContextMenu();
        if (panel.ParentId == null)
            menu.Items.Add(CreateMenuItem("创建子分组", () => RunGroupDialog(() => DockGroupDialogs.Create(panel, session, panel.GroupId))));
        menu.Items.Add(CreateMenuItem(panel.ParentId.HasValue ? "返回上一级" : "关闭分组", () => CloseGroupAndChildren(panel)));
        return TrackInteractionMenu(menu);
    }

    private sealed record GroupDropTarget(Guid? ParentId, Guid? AnchorId, Button? Button, bool After, bool Center, bool Vertical);

    private static GroupDropTarget LocateGroupDrop(DockGroupPanel panel, Point pointer)
    {
        if (panel.IsOver(panel.BackButton, pointer))
            return new GroupDropTarget(panel.ParentId, null, panel.BackButton, false, true, false);

        Button? button = panel.EntryAt(pointer);
        Guid? anchor = button?.Tag as Guid?;
        if (!anchor.HasValue || button == null)
            return new GroupDropTarget(panel.GroupId, null, null, false, false, false);

        Rect bounds = panel.ButtonBounds(button);
        double x = (pointer.X - bounds.Left) / Math.Max(1, bounds.Width);
        double y = (pointer.Y - bounds.Top) / Math.Max(1, bounds.Height);
        bool center = x is >= 0.28 and <= 0.72 && y is >= 0.28 and <= 0.72;
        bool vertical = panel.ExpandMode == DockGroupExpandMode.Vertical
            || ((panel.ExpandMode is DockGroupExpandMode.Grid or DockGroupExpandMode.Fan) && Math.Abs(y - 0.5) > Math.Abs(x - 0.5));
        return new GroupDropTarget(panel.GroupId, anchor, button, (vertical ? y : x) >= 0.5, center, vertical);
    }

    private void PanelDragOver(DockGroupPanel panel, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        panel.ClearFeedback();
        Point pointer = panel.GetLocalPoint(e);
        panel.ScrollDuringDrag(pointer);
        GroupDropTarget target = LocateGroupDrop(panel, pointer);
        if (GetDraggedEntry(e) is Guid source)
        {
            if (!CanDropEntry(source, target.ParentId, target.AnchorId, target.Center))
                return;
            e.Effects = DragDropEffects.Move;
        }
        else if (e.Data.GetDataPresent(DataFormats.FileDrop, false))
        {
            e.Effects = GetExternalDropEffect(e.AllowedEffects);
        }

        if (e.Effects != DragDropEffects.None)
            panel.ShowFeedback(target.Button, target.Center, target.After, target.Vertical);
    }

    private void PanelDrop(DockGroupPanel panel, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        panel.ClearFeedback();
        GroupDropTarget target = LocateGroupDrop(panel, panel.GetLocalPoint(e));
        if (GetDraggedEntry(e) is Guid source)
        {
            bool committed = false;
            TryAction(() => committed = CommitEntryDrop(source, target.ParentId, target.AnchorId, target.After, target.Center));
            if (committed)
                e.Effects = DragDropEffects.Move;
            return;
        }

        DragDropEffects effect = GetExternalDropEffect(e.AllowedEffects);
        if (effect == DragDropEffects.None || !e.Data.GetDataPresent(DataFormats.FileDrop, false) ||
            e.Data.GetData(DataFormats.FileDrop, false) is not string[] paths)
            return;
        Guid? destination = target.Center && target.AnchorId is Guid candidate && session.Groups.Any(group => group.Id == candidate)
            ? candidate : target.ParentId;
        FileDropResult result = AcceptFiles(paths, destination);
        if (result.Errors.Count > 0)
            Dispatcher.InvokeAsync(() => ShowError(string.Join(Environment.NewLine, result.Errors.Take(3))));
        if (result.Added > 0)
            e.Effects = effect;
    }

    internal object ReadGroupPanelState() => new
    {
        depth = groupPanels.Count,
        panels = groupPanels.Select(panel =>
        {
            panel.UpdateLayout();
            return new
            {
                groupId = panel.GroupId,
                name = panel.Title,
                expandMode = panel.ExpandMode.ToString(),
                direction = panel.Direction.ToString(),
                actualDirection = panel.ActualDirection.ToString(),
                bounds = DockWindowPosition.TryGetBounds(panel, out Rect bounds) ? DescribeBounds(bounds) : null,
                animationMilliseconds = DockGroupPanel.AnimationMilliseconds,
                animationActive = panel.AnimationActive,
                opacity = panel.AnimatedOpacity,
                nativeVisible = DockWindowPosition.IsNativeVisible(panel),
                activatesOnShow = panel.ShowActivated,
                entries = panel.EntryButtons.Select(button => new { id = button.Tag, bounds = DescribeBounds(panel.ButtonBounds(button)) }).ToArray()
            };
        }).ToArray()
    };

    internal object TestGroupPanelDrop(Guid source, Guid panelGroupId, Guid? target, bool after, bool center)
    {
        DockGroupPanel panel = groupPanels.FirstOrDefault(value => value.GroupId == panelGroupId)
            ?? throw new InvalidOperationException("请先打开目标分组。");
        Button? anchor = target.HasValue ? panel.EntryButton(target.Value) : null;
        if (target.HasValue && anchor == null)
            throw new ArgumentException("目标条目不在该面板。", nameof(target));
        panel.UpdateLayout();
        bool accepted = CanDropEntry(source, panelGroupId, target, center);
        if (!accepted)
            return new { accepted, feedback = false, committed = false };

        try
        {
            panel.ShowFeedback(anchor, center, after, panel.ExpandMode == DockGroupExpandMode.Vertical);
            bool feedback = panel.FeedbackVisible;
            bool committed = CommitEntryDrop(source, panelGroupId, target, after, center);
            return new { accepted, feedback, committed };
        }
        finally
        {
            panel.ClearFeedback();
        }
    }

    private static bool IsPanelControl(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is ButtonBase or ScrollBar or Thumb)
                return true;
            source = source is Visual or Visual3D ? VisualTreeHelper.GetParent(source) : (source as FrameworkContentElement)?.Parent;
        }
        return false;
    }
}
