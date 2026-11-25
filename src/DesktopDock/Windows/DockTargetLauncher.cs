using System.ComponentModel;
using System.Diagnostics;

namespace DesktopDock;

public static class DockTargetLauncher
{
    public static void Open(DockItem item)
    {
        string target = item.Kind == DockItemKind.Url
            ? DockTargets.ParseUrl(item.Target).AbsoluteUri
            : DockTargets.ExistingPath(item);

        var start = new ProcessStartInfo(target) { UseShellExecute = item.Kind != DockItemKind.Application };
        if (item.Kind == DockItemKind.Application)
            start.WorkingDirectory = Path.GetDirectoryName(target) ?? string.Empty;

        Start(start, "Windows 无法打开所选项目。");
    }

    public static void ShowInFolder(DockItem item)
    {
        if (item.Kind == DockItemKind.Url)
            throw new ArgumentException("网址没有本地文件位置。");

        string target = Path.TrimEndingDirectorySeparator(DockTargets.ExistingPath(item));
        if (Path.GetDirectoryName(target) is null)
        {
            Open(item);
            return;
        }

        var start = new ProcessStartInfo(DockTargets.FileExplorerExecutable) { Arguments = $"/select,\"{target}\"" };
        Start(start, "Windows 无法在文件资源管理器中定位所选项目。");
    }

    private static void Start(ProcessStartInfo start, string errorMessage)
    {
        try
        {
            using Process? process = Process.Start(start);
        }
        catch (Win32Exception error)
        {
            throw new InvalidOperationException($"{errorMessage}（Windows 错误 {error.NativeErrorCode}）", error);
        }
    }
}
