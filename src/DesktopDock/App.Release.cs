using Microsoft.Win32;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace DesktopDock;

public partial class App
{
    private DockDiagnostics? diagnostics;

    private static bool IsReleaseCommand(string[] args) => args.FirstOrDefault() is
        "--version" or "--register-startup" or "--uninstall-cleanup" or "--verify-profile"
        or "--prepare-upgrade" or "--restore-upgrade" or "--remove-profile-data";

    private void RunReleaseCommand(string[] args)
    {
        static string FullPath(string value) => Path.IsPathFullyQualified(value)
            ? Path.GetFullPath(value) : throw new ArgumentException("请使用绝对路径。");
        string? result = null;
        switch (args)
        {
            case ["--version"]:
                result = DockDiagnostics.Version;
                break;
            case ["--register-startup"]:
                CreateStartupRegistration().SetEnabled(true);
                break;
            case ["--uninstall-cleanup"]:
                CreateStartupRegistration().RemoveForUninstall();
                break;
            case ["--verify-profile", var profile]:
                DockReleaseUpgrade.Validate(FullPath(profile));
                break;
            case ["--prepare-upgrade", var profile, var backups, var from, var to]:
                result = DockReleaseUpgrade.Prepare(FullPath(profile), FullPath(backups), from, to);
                break;
            case ["--restore-upgrade", var profile, var snapshot]:
                result = DockReleaseUpgrade.Restore(FullPath(profile), FullPath(snapshot));
                break;
            case ["--remove-profile-data", var profile]:
                DockReleaseUpgrade.RemoveOwnedData(FullPath(profile));
                break;
            default:
                throw new ArgumentException("发布命令参数无效。请查看构建与升级说明。");
        }
        if (result != null)
        {
            using Stream stdout = Console.OpenStandardOutput();
            stdout.Write(Encoding.UTF8.GetBytes(result + Environment.NewLine));
        }
    }

    private void AttachDiagnostics(string directory)
    {
        diagnostics = new DockDiagnostics(directory);
        diagnostics.Record("started");
        DispatcherUnhandledException += OnDispatcherCrash;
        AppDomain.CurrentDomain.UnhandledException += OnProcessCrash;
    }

    private void DetachDiagnostics(int code)
    {
        DispatcherUnhandledException -= OnDispatcherCrash;
        AppDomain.CurrentDomain.UnhandledException -= OnProcessCrash;
        diagnostics?.Record(code == 0 ? "stopped" : "stopped_with_error");
    }

    private void OnProcessCrash(object sender, UnhandledExceptionEventArgs e) =>
        diagnostics?.Record("process_exception", e.ExceptionObject as Exception);

    private void OnDispatcherCrash(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        diagnostics?.Record("dispatcher_exception", e.Exception);
        if (exiting)
            return;
        e.Handled = true;
        if (!validationMode)
            MessageBox.Show("Desktop Dock 遇到意外错误并将退出。重新打开后可在通用设置导出诊断。",
                "Desktop Dock", MessageBoxButton.OK, MessageBoxImage.Warning);
        ExitApp(1);
    }

    private void ExportDiagnostics()
    {
        if (diagnostics == null)
            return;
        var picker = new SaveFileDialog
        {
            Title = "导出诊断",
            Filter = "诊断报告 (*.json)|*.json",
            DefaultExt = ".json",
            FileName = $"DesktopDock-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.json",
            OverwritePrompt = false
        };
        if ((settings == null ? picker.ShowDialog() : picker.ShowDialog(settings)) != true)
            return;
        try
        {
            diagnostics.Export(picker.FileName, DiagnosticDisplays());
            settings?.SetDiagnosticStatus("诊断文件已保存。");
        }
        catch (Exception error) when (DockDialogs.IsUserFacing(error))
        {
            DockDialogs.Error(settings, "无法导出诊断文件", error);
        }
    }

    private object DiagnosticDisplays() => ConnectedDocks.Select(instance => new
    {
        Primary = instance.Monitor?.Primary,
        Scale = instance.Monitor?.Scale,
        Width = instance.Monitor?.Right - instance.Monitor?.Left,
        Height = instance.Monitor?.Bottom - instance.Monitor?.Top,
        instance.RequestedVisible,
        Mode = instance.Profile.Options.Behavior.Mode.ToString()
    }).ToArray();
}
