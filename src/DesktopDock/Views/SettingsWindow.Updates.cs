using System.Diagnostics;
using System.Windows;

namespace DesktopDock;

public partial class SettingsWindow
{
    private CancellationTokenSource? updateCheck;
    private Uri? newerReleasePage;

    private async void CheckForUpdates(object sender, RoutedEventArgs e) => await CheckForUpdatesAsync();

    internal async Task CheckForUpdatesAsync()
    {
        if (updateCheck != null)
            return;

        using var request = new CancellationTokenSource();
        updateCheck = request;
        CheckForUpdatesButton.IsEnabled = false;
        newerReleasePage = null;
        OpenReleaseButton.Content = "打开 GitHub Releases";
        UpdateStatusText.Text = "正在检查更新…";

        try
        {
            DockUpdateResult result = await DockUpdateService.Shared.CheckAsync(Version.Parse(DockDiagnostics.Version), request.Token);
            if (request.IsCancellationRequested)
                return;

            newerReleasePage = result.ReleasePage;
            if (newerReleasePage != null)
                OpenReleaseButton.Content = result.Status == DockUpdateStatus.UpdateAvailable ? "查看发行说明和下载" : "查看发行说明";

            UpdateStatusText.Text = result.Status switch
            {
                DockUpdateStatus.UpToDate => "没有可用的较新版本。",
                DockUpdateStatus.UpdateAvailable => $"发现新版本 {result.LatestVersion}。可在发行页下载 Windows x64 安装包。",
                DockUpdateStatus.PackageMissing => $"发现新版本 {result.LatestVersion}，发行页尚无 Windows x64 安装包。",
                DockUpdateStatus.NoRelease => "GitHub 暂无可见的稳定发行版本。",
                DockUpdateStatus.RateLimited => "GitHub 暂时限制了请求次数，请稍后重试。",
                DockUpdateStatus.InvalidRelease => "GitHub 发行信息格式异常，请在发行页查看。",
                _ => "暂时无法连接 GitHub 检查更新，请稍后重试。"
            };
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
        }
        finally
        {
            updateCheck = null;
            if (!request.IsCancellationRequested)
                CheckForUpdatesButton.IsEnabled = true;
        }
    }

    private void OpenRelease(object sender, RoutedEventArgs e)
    {
        Uri page = newerReleasePage ?? DockUpdateService.ReleasesPage;
        try
        {
            using Process? browser = Process.Start(new ProcessStartInfo(page.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception error) when (DockDialogs.IsUserFacing(error))
        {
            UpdateStatusText.Text = "无法打开默认浏览器，请检查 Windows 的默认浏览器设置。";
        }
    }

    internal object ReadUpdateState() => new
    {
        currentVersion = DockDiagnostics.Version,
        checking = updateCheck != null,
        message = UpdateStatusText.Text,
        link = (newerReleasePage ?? DockUpdateService.ReleasesPage).AbsoluteUri,
        actionLabel = OpenReleaseButton.Content
    };
}
