using System.Windows;
using Forms = System.Windows.Forms;

namespace DesktopDock;

public partial class App : Application
{
    private DockSession? session;
    private DockWindow? dock;
    private SettingsWindow? settings;
    private Forms.NotifyIcon? tray;
    private Forms.ToolStripMenuItem? visibilityMenu;
    private Forms.ToolStripMenuItem? pauseZonesMenu;
    private DockInteractionRuntime? interaction;
    private bool dockVisibilityRequested;
    private bool exiting;
    private bool validationMode;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        validationMode = e.Args.Contains("--mcp-stdio", StringComparer.Ordinal);
        try
        {
            (string directory, bool showSettings) = ReadOptions(e.Args);
            if (validationMode && (!Console.IsInputRedirected || !Console.IsOutputRedirected))
                throw new ArgumentException("MCP 模式需要重定向 stdin 和 stdout。");
            session = DockSession.Open(directory);
            dock = new DockWindow(session, new DockIconService(), ShowSettings, EditItem, ReplaceIcon, () => SetDockVisible(false));
            MainWindow = dock;
            InitializeThemes(directory);
            dock.Closing += (_, args) =>
            {
                if (exiting)
                    return;
                args.Cancel = true;
                Dispatcher.BeginInvoke(() => SetDockVisible(false));
            };
            if (!validationMode)
                CreateTray();
            interaction = new DockInteractionRuntime(dock, session, validationMode, RenderDockVisibility, CaptureShortcut);
            UpdateTrayBehavior();
            if (showSettings)
                ShowSettings();
            if (validationMode)
                _ = RunMcp();
        }
        catch (Exception exception) when (DockDialogs.IsUserFacing(exception))
        {
            if (validationMode)
                WriteMcpError(exception);
            else
                DockDialogs.Error(null, "无法启动 Desktop Dock", exception);
            ExitApp(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DetachThemes();
        interaction?.Dispose();
        if (tray != null)
        {
            tray.Visible = false;
            tray.ContextMenuStrip?.Dispose();
            tray.Dispose();
        }
        session?.Dispose();
        base.OnExit(e);
    }

    private (string Directory, bool ShowSettings) ReadOptions(string[] args)
    {
        string defaultDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopDock", "config");
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
                    break;
                default:
                    throw new ArgumentException("启动选项无效。可使用 --config-dir <绝对路径>、--settings 和 --mcp-stdio。");
            }
        }
        if (validationMode && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)), defaultDirectory, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("MCP 模式需要使用 --config-dir 指定独立的测试配置目录。");
        return (directory, settings);
    }

    private void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("设置", null, (_, _) => ShowSettings());
        menu.Items.Add("主题编辑器…", null, (_, _) => ShowThemeEditor());
        visibilityMenu = new Forms.ToolStripMenuItem();
        visibilityMenu.Click += (_, _) => SetDockVisible(!dockVisibilityRequested);
        menu.Items.Add(visibilityMenu);
        pauseZonesMenu = new Forms.ToolStripMenuItem("暂停热区");
        pauseZonesMenu.Click += (_, _) =>
        {
            interaction?.Shortcut(DockHotkeyAction.Pause);
            UpdateTrayBehavior();
        };
        menu.Items.Add(pauseZonesMenu);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitApp(0));
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

    private void RenderDockVisibility(bool visible)
    {
        if (dock == null)
            return;
        bool revealing = visible && !dockVisibilityRequested;
        dockVisibilityRequested = visible;
        if (revealing && session?.Options.Behavior.Mode == DockMode.AutoHide)
            dock.PrepareReveal();
        dock.SetThemedVisibility(visible);
        if (visibilityMenu != null)
            visibilityMenu.Text = visible ? "隐藏 Dock" : "显示 Dock";
        settings?.UpdateDockVisibility(visible);
        UpdateTrayBehavior();
    }

    private void UpdateTrayBehavior()
    {
        if (pauseZonesMenu == null)
            return;
        bool paused = interaction?.HotZonesPaused == true;
        pauseZonesMenu.Checked = paused;
        pauseZonesMenu.Text = paused ? "恢复热区" : "暂停热区";
    }

    private void ShowSettings()
    {
        if (settings == null)
        {
            settings = new SettingsWindow(session!, dockVisibilityRequested, SetDockVisible, EditItem, ReplaceIcon,
                interaction!.ReadMonitors, SaveBehavior, ShowThemeEditor)
            {
                ShowActivated = !validationMode
            };
            settings.Closed += (_, _) => settings = null;
            settings.UpdateHotkeyRegistrationStatus(interaction.HotkeyStatus);
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

    private Window? DialogOwner => settings?.IsActive == true ? settings : dock?.IsVisible == true ? dock : settings;

    private string? SaveBehavior(DockBehaviorOptions options)
    {
        string? error = interaction!.Apply(options, settings?.IsEditingShortcuts == true);
        settings?.UpdateHotkeyRegistrationStatus(interaction.HotkeyStatus);
        return error;
    }

    private bool CaptureShortcut(DockHotkeyAction action)
    {
        DockHotkeys bindings = session!.Options.Behavior.Hotkeys;
        HotkeyChord? chord = action switch
        {
            DockHotkeyAction.Toggle => bindings.Toggle,
            DockHotkeyAction.Peek => bindings.Peek,
            DockHotkeyAction.Lock => bindings.Lock,
            _ => bindings.Pause
        };
        return chord != null && settings?.CaptureRegisteredHotkey(chord) == true;
    }

    private void EditItem(DockItem item)
    {
        interaction?.SetModalInteraction(true);
        try { DockDialogs.Edit(DialogOwner, session!, item); }
        finally { interaction?.SetModalInteraction(false); }
    }

    private void ReplaceIcon(DockItem item)
    {
        interaction?.SetModalInteraction(true);
        try { DockDialogs.ReplaceIcon(DialogOwner, session!, item); }
        finally { interaction?.SetModalInteraction(false); }
    }
}
