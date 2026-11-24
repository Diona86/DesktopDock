using System.Windows;

namespace DesktopDock;

public partial class DockGroupDeleteWindow : Window
{
    private readonly DockSession session;
    private readonly Guid groupId;

    internal DockGroupDeleteWindow(DockSession session, DockGroup group)
    {
        this.session = session;
        groupId = group.Id;
        InitializeComponent();

        var groups = session.Groups.ToDictionary(candidate => candidate.Id);
        IReadOnlyList<DockEntry> entries = session.Entries;
        Guid? parentId = entries.FirstOrDefault(entry => entry.Id == groupId)?.ParentId;
        var childGroups = entries.Where(entry => entry.ParentId == groupId && groups.ContainsKey(entry.Id))
            .Select(entry => entry.Id).ToHashSet();
        int itemCount = entries.Count(entry => !groups.ContainsKey(entry.Id)
            && entry.ParentId is Guid container && (container == groupId || childGroups.Contains(container)));
        string destination = parentId is Guid parent && groups.TryGetValue(parent, out DockGroup? parentGroup)
            ? $"「{parentGroup.Name}」" : "Dock 顶层";

        GroupTitle.Text = $"删除「{group.Name}」";
        GroupTitle.ToolTip = group.Name;
        Summary.Text = $"此分组包含 {itemCount} 个项目引用和 {childGroups.Count} 个子分组。请选择删除后的处理办法。";
        DestinationHint.Text = childGroups.Count > 0
            ? $"项目与子分组移到{destination}，保留引用。"
            : $"项目移到{destination}，保留引用。";
        Loaded += (_, _) => CancelButton.Focus();
    }

    private void SelectChoice(object sender, RoutedEventArgs e) => ConfirmButton.IsEnabled = true;

    private void Confirm(object sender, RoutedEventArgs e)
    {
        DockGroupDeleteChoice? choice = MoveOption.IsChecked == true ? DockGroupDeleteChoice.MoveContentsToParent
            : RemoveOption.IsChecked == true ? DockGroupDeleteChoice.RemoveReferences : null;
        if (!choice.HasValue)
            return;

        try
        {
            session.DeleteGroup(groupId, choice.Value);
            DialogResult = true;
        }
        catch (Exception error) when (DockDialogs.IsUserFacing(error))
        {
            ErrorMessage.Text = error.Message;
            ErrorMessage.Visibility = Visibility.Visible;
        }
    }
}
