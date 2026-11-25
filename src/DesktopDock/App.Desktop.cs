using System.Security;
using System.Windows;
using System.Windows.Threading;

namespace DesktopDock;

public partial class App
{
    private DockStartupRegistration? startupRegistration;
    private DesktopIconService? desktopIcons;
    private DesktopIconWindow? desktopManager;
    private DockHotkeyService? desktopShortcut;
    private DispatcherTimer? desktopRefreshTimer;
    private string? desktopShortcutError;

    private static DockStartupRegistration CreateStartupRegistration() =>
        new(Environment.ProcessPath ?? throw new InvalidOperationException("无法读取 Desktop Dock 程序路径。"));

    private void InitializeDesktopIntegration(string directory)
    {
        desktopIcons = DesktopIconService.Open(directory, allowChanges: !validationMode);
        if (validationMode)
            return;

        startupRegistration = CreateStartupRegistration();
        try
        {
            desktopShortcut = new DockHotkeyService(_ => RestoreDesktopIcons());
            _ = desktopShortcut.RegisterDesktopRestore(out desktopShortcutError);
        }
        catch (Exception error) when (DockDialogs.IsUserFacing(error))
        {
            desktopShortcutError = error.Message;
        }

        desktopRefreshTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        desktopRefreshTimer.Tick += (_, _) => desktopIcons.Refresh();
        desktopRefreshTimer.Start();
    }

    private void ShowDesktopRecoveryIfNeeded()
    {
        if (desktopIcons?.Refresh().RecoveryNeeded == true)
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(ShowDesktopManager));
    }

    private void ShowDesktopManager()
    {
        if (desktopIcons == null)
            return;
        if (desktopManager == null)
        {
            desktopManager = new DesktopIconWindow(desktopIcons, desktopShortcutError) { ShowActivated = !validationMode };
            if (settings?.IsVisible == true)
                desktopManager.Owner = settings;
            desktopManager.Closed += (_, _) => desktopManager = null;
            desktopManager.Show();
        }
        if (desktopManager.WindowState == WindowState.Minimized)
            desktopManager.WindowState = WindowState.Normal;
        if (!validationMode)
            desktopManager.Activate();
    }

    private void RestoreDesktopIcons()
    {
        if (desktopIcons == null)
            return;
        try
        {
            DesktopIconStatus status = desktopIcons.Restore();
            if (status.RecoveryNeeded || status.Visible != true)
                ShowDesktopManager();
        }
        catch (Exception error) when (DockDialogs.IsUserFacing(error))
        {
            if (!validationMode)
                DockDialogs.Error(desktopManager ?? (Window?)settings, "桌面图标恢复", error);
        }
    }

    private void ApplyTrayCommand(string command)
    {
        switch (command)
        {
            case "show": SetAllDocksVisible(true); break;
            case "hide": SetAllDocksVisible(false); break;
            case "pause": SetAllHotZonesPaused(true); break;
            case "resume": SetAllHotZonesPaused(false); break;
            case "settings": ShowSettings(); break;
            case "restore": RestoreDesktopIcons(); break;
            case "exit": ExitApp(0); break;
            default: throw new ArgumentException("未知通知区域操作。");
        }
        UpdateTrayBehavior();
    }

    private void DisposeDesktopIntegration()
    {
        desktopRefreshTimer?.Stop();
        desktopShortcut?.Dispose();
        if (!validationMode)
        {
            try { desktopIcons?.RestoreOnExit(); }
            catch (Exception error) when (DockDialogs.IsUserFacing(error) || error is SecurityException)
            {
                // The journal remains available on the next launch if the Explorer session cannot be reached.
            }
        }
    }
}
