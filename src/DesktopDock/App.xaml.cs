using System.Windows;
using Forms = System.Windows.Forms;

namespace DesktopDock;

public partial class App : Application
{
    private DockSession? session;
    private DockWindow? dock;
    private SettingsWindow? settings;
    private Forms.NotifyIcon? tray;
    private Forms.ToolStripMenuItem? pauseZonesMenu;
    private DockInteractionRuntime? interaction;
    private bool dockVisibilityRequested;
    private bool exiting;
    private bool validationMode;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        validationMode = e.Args.Contains("--mcp-stdio", StringComparer.Ordinal);
        bool releaseCommand = IsReleaseCommand(e.Args);
        try
        {
            if (releaseCommand)
            {
                RunReleaseCommand(e.Args);
                ExitApp(0);
                return;
            }
            (string directory, bool showSettings) = ReadOptions(e.Args);
            if (validationMode && (!Console.IsInputRedirected || !Console.IsOutputRedirected))
                throw new ArgumentException("MCP 模式需要重定向 stdin 和 stdout。");
            session = DockSession.Open(directory);
            AttachDiagnostics(directory);
            InitializeThemes(directory);
            InitializeDisplays(directory, session);
            InitializeDesktopIntegration(directory);
            if (!validationMode)
                CreateTray();
            UpdateTrayBehavior();
            if (showSettings)
                ShowSettings();
            if (validationMode)
                _ = RunMcp();
            else
                ShowDesktopRecoveryIfNeeded();
        }
        catch (Exception exception) when (DockDialogs.IsUserFacing(exception) || exception is System.Security.SecurityException)
        {
            diagnostics?.Record("startup_error", exception);
            if (validationMode || releaseCommand)
                WriteMcpError(exception);
            else
                DockDialogs.Error(null, "无法启动 Desktop Dock", exception);
            ExitApp(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DetachDiagnostics(e.ApplicationExitCode);
        DetachThemes();
        DisposeDesktopIntegration();
        DisposeDisplays();
        if (tray != null)
        {
            tray.Visible = false;
            tray.ContextMenuStrip?.Dispose();
            tray.Dispose();
        }
        base.OnExit(e);
    }

    private (string Directory, bool ShowSettings) ReadOptions(string[] args)
    {
        string defaultDirectory = DockInstalledUpdate.DefaultProfileDirectory;
        string directory = defaultDirectory;
        bool settings = false;
        for (int index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--config-dir" when index + 1 < args.Length && Path.IsPathFullyQualified(args[index + 1]):
                    directory = args[++index];
                    break;
                case "--settings":
                    settings = true;
                    break;
                case "--mcp-stdio":
                case "--startup":
                    break;
                default:
                    throw new ArgumentException("启动选项无效。可使用 --config-dir <绝对路径>、--settings、--mcp-stdio 或 --uninstall-cleanup。");
            }
        }
        if (validationMode && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)), defaultDirectory, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("MCP 模式需要使用 --config-dir 指定独立的测试配置目录。");
        return (directory, settings);
    }

    private void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("显示全部 Dock", null, (_, _) => ApplyTrayCommand("show"));
        menu.Items.Add("隐藏全部 Dock", null, (_, _) => ApplyTrayCommand("hide"));
        pauseZonesMenu = new Forms.ToolStripMenuItem("暂停热区");
        pauseZonesMenu.Click += (_, _) => ApplyTrayCommand(ConnectedDocks.All(item => item.Runtime.HotZonesPaused) ? "resume" : "pause");
        menu.Items.Add(pauseZonesMenu);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("设置", null, (_, _) => ApplyTrayCommand("settings"));
        menu.Items.Add("可视化热区…", null, (_, _) => ShowZoneMap());
        menu.Items.Add("主题编辑器…", null, (_, _) => ShowThemeEditor());
        menu.Items.Add("桌面图标…", null, (_, _) => ShowDesktopManager());
        menu.Items.Add("恢复桌面图标", null, (_, _) => ApplyTrayCommand("restore"));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ApplyTrayCommand("exit"));
        tray = new Forms.NotifyIcon
        {
            Text = "Desktop Dock",
            Icon = System.Drawing.SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = true
        };
        tray.DoubleClick += (_, _) => ShowSettings();
    }

    private void SetDockVisible(bool visible)
    {
        interaction?.SetVisible(visible);
    }

    private void RenderDockVisibility(DisplayDock instance, bool visible)
    {
        bool revealing = visible && !instance.RequestedVisible;
        instance.RequestedVisible = visible;
        if (revealing && instance.Profile.Options.Behavior.Mode == DockMode.AutoHide)
            instance.Window.PrepareReveal();
        instance.Window.SetThemedVisibility(visible);
        if (instance == activeDock)
        {
            dockVisibilityRequested = visible;
            settings?.UpdateDockVisibility(visible);
        }
        UpdateTrayBehavior();
    }

    private void UpdateTrayBehavior()
    {
        if (pauseZonesMenu == null)
            return;
        bool paused = ConnectedDocks.All(instance => instance.Runtime.HotZonesPaused);
        pauseZonesMenu.Checked = paused;
        pauseZonesMenu.Text = paused ? "恢复热区" : "暂停热区";
    }

    private void ShowSettings()
    {
        if (settings == null)
        {
            DisplayDock instance = activeDock!;
            settings = new SettingsWindow(instance.Profile, instance.RequestedVisible, instance.Runtime.SetVisible,
                item => EditItemFor(instance, item), item => ReplaceIconFor(instance, item),
                interaction!.ReadMonitors, SaveBehavior, ShowThemeEditor)
            {
                ShowActivated = !validationMode
            };
            settings.Closed += (_, _) => settings = null;
            settings.UpdateHotkeyRegistrationStatus(legacyDock!.Runtime.HotkeyStatus);
            settings.ConfigureSystemControls(ShowZoneMap, ShowDesktopManager, startupRegistration,
                () => legacyDock.Profile.Options.GlobalPositionLocked, SetGlobalPositionLock,
                () => legacyDock.Profile.Options.Behavior.Hotkeys);
            settings.SetDiagnosticExporter(ExportDiagnostics);
            if (!validationMode && diagnostics?.ProfileDirectory is string profileDirectory && DockInstalledUpdate.Find(profileDirectory) != null)
                settings.ConfigureUpdateInstaller((file, version) =>
                {
                    DockInstalledUpdate.Start(profileDirectory, file, version);
                    _ = Dispatcher.InvokeAsync(() => ExitApp(0));
                });
            UpdateDisplayChoices();
            settings.Show();
        }
        if (settings.WindowState == WindowState.Minimized)
            settings.WindowState = WindowState.Normal;
        if (!validationMode)
            settings.Activate();
    }

    private void ExitApp(int code)
    {
        exiting = true;
        Shutdown(code);
    }

    private string? SaveBehavior(DockBehaviorOptions options)
    {
        DockInteractionRuntime global = legacyDock!.Runtime;
        string? error = settings?.IsEditingShortcuts == true
            ? global.Apply(legacyDock.Profile.Options.Behavior with { Hotkeys = options.Hotkeys }, true)
            : interaction!.Apply(options, false);
        settings?.UpdateHotkeyRegistrationStatus(global.HotkeyStatus);
        return error;
    }

    private bool CaptureShortcut(DockHotkeyAction action)
    {
        DockHotkeys bindings = legacyDock!.Profile.Options.Behavior.Hotkeys;
        HotkeyChord? chord = action switch
        {
            DockHotkeyAction.Toggle => bindings.Toggle,
            DockHotkeyAction.Peek => bindings.Peek,
            DockHotkeyAction.Lock => bindings.Lock,
            _ => bindings.Pause
        };
        return chord != null && settings?.CaptureRegisteredHotkey(chord) == true;
    }

    private Window? ItemDialogOwner(DisplayDock instance) =>
        instance == activeDock && settings?.IsActive == true ? settings
            : instance.Window.IsVisible ? instance.Window : settings;

    private void EditItemFor(DisplayDock instance, DockItem item)
    {
        instance.Runtime.SetModalInteraction(true);
        try { DockDialogs.Edit(ItemDialogOwner(instance), instance.Profile, item); }
        finally { instance.Runtime.SetModalInteraction(false); }
    }

    private void ReplaceIconFor(DisplayDock instance, DockItem item)
    {
        instance.Runtime.SetModalInteraction(true);
        try { DockDialogs.ReplaceIcon(ItemDialogOwner(instance), instance.Profile, item); }
        finally { instance.Runtime.SetModalInteraction(false); }
    }

    private void SetGlobalPositionLock(bool locked)
    {
        DockSession root = legacyDock!.Profile;
        root.UpdateOptions(root.Options with { GlobalPositionLocked = locked });
    }
}
