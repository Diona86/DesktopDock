using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace DesktopDock;

public partial class LayoutSettingsPage : UserControl
{
    private DockSession? session;

    public LayoutSettingsPage() => InitializeComponent();

    private GroupRow? Selected => GroupsList.SelectedItem as GroupRow;

    internal void LoadOptions(DockSession currentSession)
    {
        session = currentSession;
        DockLayoutOptions options = currentSession.Options.Layout;
        ModeInput.SelectedIndex = (int)options.Mode;
        DirectionInput.SelectedIndex = (int)options.ExpansionDirection;
        MaxIconsInput.Text = options.MaxVisibleIcons.ToString(CultureInfo.CurrentCulture);
        GridTracksInput.Text = options.GridTracks.ToString(CultureInfo.CurrentCulture);
        IconSpacingInput.Text = options.IconSpacing.ToString(CultureInfo.CurrentCulture);
        PanelSpacingInput.Text = options.PanelSpacing.ToString(CultureInfo.CurrentCulture);
        RefreshGroups();
    }

    internal DockLayoutOptions ReadOptions(DockLayoutOptions basis) => basis with
    {
        Mode = (DockLayoutMode)ModeInput.SelectedIndex,
        ExpansionDirection = (DockExpandDirection)DirectionInput.SelectedIndex,
        MaxVisibleIcons = ReadInteger(MaxIconsInput, "图标上限"),
        GridTracks = ReadInteger(GridTracksInput, "网格行 / 列数"),
        IconSpacing = ReadNumber(IconSpacingInput, "图标间距"),
        PanelSpacing = ReadNumber(PanelSpacingInput, "面板间距")
    };

    internal void RefreshGroups()
    {
        if (session == null)
            return;

        Guid? selectedId = Selected?.Group.Id;
        var groups = session.Groups.ToDictionary(group => group.Id);
        var entries = session.Entries.ToLookup(entry => entry.ParentId);
        var rows = new List<GroupRow>(groups.Count);
        foreach (DockEntry entry in entries[null])
        {
            if (!groups.TryGetValue(entry.Id, out DockGroup? group))
                continue;

            rows.Add(new(group, null, group.Name, entries[group.Id].Count(child => !groups.ContainsKey(child.Id))));
            foreach (DockEntry child in entries[group.Id])
            {
                if (groups.TryGetValue(child.Id, out DockGroup? subgroup))
                    rows.Add(new(subgroup, group.Id, $"{group.Name} › {subgroup.Name}", entries[subgroup.Id].Count()));
            }
        }

        GroupsList.ItemsSource = rows;
        GroupsList.SelectedItem = rows.FirstOrDefault(row => row.Group.Id == selectedId);
        EmptyGroups.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateButtons();
    }

    private void SelectGroup(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    private void UpdateButtons()
    {
        if (AddSubgroupButton == null)
            return;
        GroupRow? selected = Selected;
        AddSubgroupButton.IsEnabled = selected is { ParentId: null };
        EditGroupButton.IsEnabled = selected != null;
        DeleteGroupButton.IsEnabled = selected != null;
    }

    private void CreateGroup(object sender, RoutedEventArgs e)
    {
        if (session != null && Window.GetWindow(this) is Window owner)
            DockGroupDialogs.Create(owner, session, null);
    }

    private void CreateSubgroup(object sender, RoutedEventArgs e)
    {
        if (session != null && Selected is { ParentId: null } row && Window.GetWindow(this) is Window owner)
            DockGroupDialogs.Create(owner, session, row.Group.Id);
    }

    private void EditGroup(object sender, RoutedEventArgs e)
    {
        if (session != null && Selected is GroupRow row && Window.GetWindow(this) is Window owner)
            DockGroupDialogs.Edit(owner, session, row.Group);
    }

    private void DeleteGroup(object sender, RoutedEventArgs e)
    {
        if (session != null && Selected is GroupRow row && Window.GetWindow(this) is Window owner)
            DockGroupDialogs.Delete(owner, session, row.Group);
    }

    private static int ReadInteger(TextBox input, string label)
    {
        if (int.TryParse(input.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out int value))
            return value;
        input.Focus();
        throw new ArgumentException($"请输入有效的{label}整数。");
    }

    private static double ReadNumber(TextBox input, string label)
    {
        if (double.TryParse(input.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double value) && double.IsFinite(value))
            return value;
        input.Focus();
        throw new ArgumentException($"请输入有效的{label}。");
    }

    private sealed record GroupRow(DockGroup Group, Guid? ParentId, string Path, int ItemCount)
    {
        public string Mode => Group.ExpandMode switch
        {
            DockGroupExpandMode.Fan => "扇形",
            DockGroupExpandMode.Horizontal => "横向",
            DockGroupExpandMode.Vertical => "纵向",
            _ => "网格"
        };
    }
}
