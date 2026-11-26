using System.Diagnostics;
using System.Net.Http;
using System.Windows;

namespace DesktopDock;

public partial class SettingsWindow
{
    private CancellationTokenSource? updateCheck;
    private CancellationTokenSource? updateDownload;
    private Uri? newerReleasePage;
    private DockUpdatePackage? updatePackage;
    private Action<string, Version>? startUpdateInstaller;

    internal void ConfigureUpdateInstaller(Action<string, Version> startInstaller) => startUpdateInstaller = startInstaller;

    private async void CheckForUpdates(object sender, RoutedEventArgs e) => await CheckForUpdatesAsync();

    internal async Task CheckForUpdatesAsync()
    {
        if (updateCheck != null || updateDownload != null)
            return;

        using var request = new CancellationTokenSource();
        updateCheck = request;
        CheckForUpdatesButton.IsEnabled = false;
        newerReleasePage = null;
        updatePackage = null;
        InstallUpdateButton.Visibility = Visibility.Collapsed;
        OpenReleaseButton.Content = "打开 GitHub Releases";
        UpdateStatusText.Text = "正在检查更新…";

        try
        {
            DockUpdateResult result = await DockUpdateService.Shared.CheckAsync(Version.Parse(DockDiagnostics.Version), request.Token);
            if (request.IsCancellationRequested)
                return;

            newerReleasePage = result.ReleasePage;
            updatePackage = result.Package;
            if (newerReleasePage != null)
                OpenReleaseButton.Content = result.Status != DockUpdateStatus.UpdateAvailable || (updatePackage != null && startUpdateInstaller != null)
                    ? "查看发行说明" : "查看发行说明和下载";
            if (updatePackage != null && startUpdateInstaller != null)
                InstallUpdateButton.Visibility = Visibility.Visible;

            UpdateStatusText.Text = result.Status switch
            {
                DockUpdateStatus.UpToDate => "没有可用的较新版本。",
                DockUpdateStatus.UpdateAvailable when InstallUpdateButton.Visibility == Visibility.Visible =>
                    $"发现新版本 {result.LatestVersion}。一键更新会下载安装包，校验后重新启动 Desktop Dock。",
                DockUpdateStatus.UpdateAvailable when updatePackage == null =>
                    $"发现新版本 {result.LatestVersion}。此发行缺少安装包校验信息，可在发行页下载。",
                DockUpdateStatus.UpdateAvailable => $"发现新版本 {result.LatestVersion}。此副本可从发行页下载新版。",
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

    private async void InstallUpdate(object sender, RoutedEventArgs e)
    {
        if (updateDownload != null)
        {
            updateDownload.Cancel();
            return;
        }
        if (updatePackage is not { } package || startUpdateInstaller is not { } startInstaller || updateCheck != null)
            return;

        using var request = new CancellationTokenSource();
        updateDownload = request;
        CheckForUpdatesButton.IsEnabled = false;
        InstallUpdateButton.Content = "取消下载";
        UpdateProgress.Value = 0;
        UpdateProgress.Visibility = Visibility.Visible;
        UpdateStatusText.Text = $"正在下载 {package.Version}…";
        bool installerStarted = false;

        var progress = new Progress<int>(percent =>
        {
            if (updateDownload != request || request.IsCancellationRequested)
                return;
            UpdateProgress.Value = percent;
            UpdateStatusText.Text = percent < 100 ? $"正在下载 {package.Version}… {percent}%" : "正在校验安装包…";
        });

        try
        {
            using DockUpdateDownload download = await DockUpdateService.Shared.DownloadAsync(package, progress, request.Token);
            request.Token.ThrowIfCancellationRequested();
            UpdateStatusText.Text = "校验通过，正在启动安装…";
            startInstaller(download.Path, package.Version);
            download.KeepForInstaller();
            installerStarted = true;
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
            UpdateStatusText.Text = "已取消下载，可再次点击「一键更新」。";
        }
        catch (Exception error) when (DockDialogs.IsUserFacing(error) || error is HttpRequestException or TimeoutException
                                      or System.Security.SecurityException or System.Security.Cryptography.CryptographicException)
        {
            UpdateStatusText.Text = error is HttpRequestException or TimeoutException
                ? "下载失败，请检查网络后重试。"
                : $"更新未启动。{error.Message}";
        }
        finally
        {
            updateDownload = null;
            if (!installerStarted)
            {
                UpdateProgress.Visibility = Visibility.Collapsed;
                InstallUpdateButton.Content = "一键更新";
                CheckForUpdatesButton.IsEnabled = true;
            }
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
        downloading = updateDownload != null,
        canInstallInApp = startUpdateInstaller != null,
        oneClickAvailable = InstallUpdateButton.Visibility == Visibility.Visible,
        verifyingPackageAvailable = updatePackage != null,
        message = UpdateStatusText.Text,
        link = (newerReleasePage ?? DockUpdateService.ReleasesPage).AbsoluteUri,
        actionLabel = OpenReleaseButton.Content
    };
}
