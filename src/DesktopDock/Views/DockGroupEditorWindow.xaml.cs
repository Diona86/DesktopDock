using System.Windows;

namespace DesktopDock;

public partial class DockGroupEditorWindow : Window
{
    private readonly DockSession session;
    private readonly DockGroup? original;
    private readonly Guid? parentId;
    private string? iconPath;

    internal DockGroupEditorWindow(DockSession session, DockGroup? group, Guid? parentId)
    {
        this.session = session;
        original = group;
        this.parentId = parentId;
        iconPath = group?.CustomIconPath;
        InitializeComponent();

        if (group == null)
        {
            Title = parentId.HasValue ? "新建子分组" : "新建分组";
            EditFields.Visibility = Visibility.Collapsed;
            SaveButton.Content = "创建";
            DockGroup? parent = session.Groups.FirstOrDefault(candidate => candidate.Id == parentId);
            if (parent != null)
            {
                PlacementHint.Text = $"在「{parent.Name}」内创建子分组";
                PlacementHint.Visibility = Visibility.Visible;
            }
        }
        else
        {
            NameInput.Text = group.Name;
            ExpandModeInput.SelectedIndex = (int)group.ExpandMode;
            DirectionInput.SelectedIndex = (int)group.Direction;
            RefreshIcon();
        }

        Loaded += (_, _) =>
        {
            NameInput.Focus();
            NameInput.SelectAll();
        };
    }

    private void ChooseIcon(object sender, RoutedEventArgs e)
    {
        if (DockDialogs.ChooseIcon(this) is string path)
        {
            iconPath = path;
            RefreshIcon();
        }
    }

    private void ResetIcon(object sender, RoutedEventArgs e)
    {
        iconPath = null;
        RefreshIcon();
    }

    private void RefreshIcon()
    {
        IconLabel.Text = iconPath ?? "使用默认分组图标";
        IconLabel.ToolTip = iconPath;
        ResetIconButton.IsEnabled = iconPath != null;
    }

    private void Save(object sender, RoutedEventArgs e)
    {
        try
        {
            if (original == null)
                session.CreateGroup(NameInput.Text, parentId);
            else
                session.UpdateGroup(original.Id, NameInput.Text, (DockGroupExpandMode)ExpandModeInput.SelectedIndex,
                    iconPath, (DockExpandDirection)DirectionInput.SelectedIndex);
            DialogResult = true;
        }
        catch (Exception error) when (DockDialogs.IsUserFacing(error))
        {
            ErrorMessage.Text = error.Message;
            ErrorMessage.Visibility = Visibility.Visible;
        }
    }
}
