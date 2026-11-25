using System.Diagnostics;
using System.Windows;

namespace DesktopDock;

public partial class DesktopIconWindow : Window
{
    private readonly DesktopIconService service;

    internal DesktopIconWindow(DesktopIconService service, string? shortcutStatus = null)
    {
        this.service = service;
        InitializeComponent();
        ManualText.Text = DesktopIconService.ManualRecovery;
        ShortcutText.Text = shortcutStatus ?? "恢复快捷键　Ctrl+Alt+Shift+F11";
        service.Changed += ServiceChanged;
        Closed += (_, _) => service.Changed -= ServiceChanged;
        UpdateStatus(service.Refresh());
    }

    private void ServiceChanged(object? sender, EventArgs e) => UpdateStatus(service.Status);

    private void UpdateStatus(DesktopIconStatus status)
    {
        StateText.Text = status.Message;
        HideButton.IsEnabled = status.CanHide;
        RestoreButton.IsEnabled = status.CanRestore;
    }

    private void HideIcons(object sender, RoutedEventArgs e)
    {
        if (!service.Refresh().CanHide)
            return;
        MessageBoxResult response = MessageBox.Show(this,
            "要临时隐藏当前显示的全部桌面图标吗？这会同时影响普通图标和当前显示的系统图标。之后可在这里恢复显示。",
            "确认隐藏桌面图标", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (response == MessageBoxResult.Yes)
            UpdateStatus(service.HideAfterConfirmation());
    }

    private void RestoreIcons(object sender, RoutedEventArgs e) => UpdateStatus(service.Restore());

    private void OpenWindowsThemes(object sender, RoutedEventArgs e)
    {
        try { using Process? settings = Process.Start(new ProcessStartInfo("ms-settings:themes") { UseShellExecute = true }); }
        catch (Exception error) when (DockDialogs.IsUserFacing(error))
        {
            DockDialogs.Error(this, "无法打开 Windows 主题设置", error);
        }
    }

    private void CloseWindow(object sender, RoutedEventArgs e) => Close();
}
