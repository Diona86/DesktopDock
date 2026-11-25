using DesktopDock;

try
{
    switch (args)
    {
        case ["--prepare-upgrade", var profile, var backupRoot, var fromVersion, var toVersion, var report]:
            _ = DockReleaseUpgrade.Prepare(profile, backupRoot, fromVersion, toVersion);
            WriteReport(report, "配置检查通过。");
            return 0;
        case ["--ensure-profile-closed", var profile, var report]:
            if (!Path.IsPathFullyQualified(profile))
                throw new ArgumentException("配置目录必须是绝对路径。");
            string lease = Path.Combine(profile, "dock.lock");
            if (File.Exists(lease))
                using (File.Open(lease, FileMode.Open, FileAccess.Read, FileShare.None)) { }
            WriteReport(report, "配置当前未被占用。");
            return 0;
        case ["--remove-profile-data", var profile, var report]:
            DockReleaseUpgrade.RemoveOwnedData(profile);
            WriteReport(report, "已清理可安全移除的 Desktop Dock 配置。");
            return 0;
        default:
            return 2;
    }
}
catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException
    or NotSupportedException or System.Text.Json.JsonException or System.Security.SecurityException)
{
    string message = $"Desktop Dock 配置操作未完成。{error.Message}";
    try
    {
        if (args.Length > 0)
            WriteReport(args[^1], message);
    }
    catch (Exception reportError) when (reportError is IOException or UnauthorizedAccessException or ArgumentException) { }
    Console.Error.WriteLine(message);
    return 1;
}

static void WriteReport(string path, string content)
{
    if (!Path.IsPathFullyQualified(path))
        throw new ArgumentException("结果文件必须使用绝对路径。");
    File.WriteAllText(path, content, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
}
