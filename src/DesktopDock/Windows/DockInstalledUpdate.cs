using Microsoft.Win32;
using System.Diagnostics;
using System.Globalization;
using System.Security;

namespace DesktopDock;

internal static class DockInstalledUpdate
{
    private const string RegistrationKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\DesktopDock.Windows_is1";

    internal static string DefaultProfileDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopDock", "config");

    internal static string? Find(string profileDirectory)
    {
        if (!SamePath(profileDirectory, DefaultProfileDirectory))
            return null;

        try
        {
            using var registration = Registry.CurrentUser.OpenSubKey(RegistrationKey);
            return MatchInstallation(registration?.GetValue("InstallLocation") as string,
                registration?.GetValue("DisplayVersion") as string, Environment.ProcessPath, DockDiagnostics.Version);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException
                                      or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    internal static string? MatchInstallation(string? root, string? registeredVersion, string? runningExecutable, string version)
    {
        if (root == null || runningExecutable == null || registeredVersion != version || !Path.IsPathFullyQualified(root))
            return null;

        string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string expectedExecutable = Path.Combine(normalizedRoot, "versions", version, "DesktopDock.exe");
        return SamePath(runningExecutable, expectedExecutable) ? normalizedRoot : null;
    }

    internal static void Start(string profileDirectory, string installer, Version version)
    {
        string installedRoot = Find(profileDirectory)
            ?? throw new InvalidOperationException("当前安装位置已改变，请重新打开 Desktop Dock 后再试。");
        if (version <= Version.Parse(DockDiagnostics.Version))
            throw new InvalidDataException("下载的安装包版本没有高于当前版本。");

        FileVersionInfo information = FileVersionInfo.GetVersionInfo(installer);
        if (information.FileDescription?.Trim() != "Desktop Dock Setup" ||
            !Version.TryParse(information.FileVersion?.Trim(), out Version? installerVersion) ||
            installerVersion.Build < 0 ||
            installerVersion.ToString(3) != version.ToString(3))
            throw new InvalidDataException("校验通过的文件与 Desktop Dock 安装包版本不符。");

        using Process? setup = Process.Start(CreateStartInfo(installer, installedRoot, Environment.ProcessId));
        if (setup == null)
            throw new InvalidOperationException("Windows 未能启动更新安装程序。");
    }

    internal static ProcessStartInfo CreateStartInfo(string installer, string installedRoot, int processId)
    {
        var start = new ProcessStartInfo(installer) { UseShellExecute = false, WorkingDirectory = installedRoot };
        foreach (string argument in new[] { "/SP-", "/SILENT", "/NORESTART", "/TASKS=", "/LANG=chinesesimp",
            "/DIR=" + installedRoot, "/UPDATEPID=" + processId.ToString(CultureInfo.InvariantCulture) })
            start.ArgumentList.Add(argument);
        return start;
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);
}
