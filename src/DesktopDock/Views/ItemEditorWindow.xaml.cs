using Microsoft.Win32;
using System.Windows;
using System.Windows.Automation;

namespace DesktopDock;

public partial class ItemEditorWindow : Window
{
    private readonly DockSession session;
    private readonly DockItem? original;
    private string? iconPath;

    public ItemEditorWindow(DockSession session, DockItem? item)
    {
        this.session = session;
        original = item;
        iconPath = item?.CustomIconPath;
        InitializeComponent();

        NameInput.Text = item?.Name ?? string.Empty;
        TargetInput.Text = item?.Target ?? "https://";
        if (item == null)
        {
            Title = "添加网址";
            TargetLabel.Text = "HTTP 或 HTTPS 网址";
            AutomationProperties.SetName(TargetInput, TargetLabel.Text);
            TargetActions.Visibility = Visibility.Collapsed;
            IconActions.Visibility = Visibility.Collapsed;
            Loaded += (_, _) =>
            {
                TargetInput.Focus();
                TargetInput.SelectAll();
            };
        }
        else
        {
            Loaded += (_, _) => NameInput.Focus();
        }
        RefreshIcon();
    }

    private void BrowseFile(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择目标文件", CheckFileExists = true, DereferenceLinks = false };
        if (dialog.ShowDialog(this) == true)
            TargetInput.Text = dialog.FileName;
    }

    private void BrowseFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择目标文件夹" };
        if (dialog.ShowDialog(this) == true)
            TargetInput.Text = dialog.FolderName;
    }

    private void BrowseIcon(object sender, RoutedEventArgs e)
    {
        if (DockDialogs.ChooseIcon(this) is string selected)
        {
            iconPath = selected;
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
        IconLabel.Text = iconPath ?? "使用原始图标";
        IconLabel.ToolTip = iconPath;
    }

    private void Save(object sender, RoutedEventArgs e)
    {
        try
        {
            if (original == null)
            {
                if (!Uri.TryCreate(TargetInput.Text.Trim(), UriKind.Absolute, out Uri? uri)
                    || uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host))
                    throw new ArgumentException("请输入完整的 HTTP 或 HTTPS 网址。");
                session.AddItem(TargetInput.Text, NameInput.Text);
            }
            else
            {
                session.UpdateItem(original.Id, TargetInput.Text, NameInput.Text, iconPath);
            }
            DialogResult = true;
        }
        catch (Exception error) when (DockDialogs.IsUserFacing(error))
        {
            ValidationMessage.Text = error.Message;
            ValidationMessage.Visibility = Visibility.Visible;
        }
    }
}
