using Microsoft.Win32;
using System.Security;

namespace DesktopDock;

internal enum DockStartupState { Disabled, Enabled, OtherInstallation, Conflict, Unavailable }

internal sealed record DockStartupStatus(DockStartupState State, string Message);

internal sealed class DockStartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DesktopDock.Windows";
    private const string StartupArgument = " --startup";
    private readonly string keyPath;
    private readonly string command;

    internal DockStartupRegistration(string executable) : this(executable, RunKey) { }

    private DockStartupRegistration(string executable, string keyPath)
    {
        if (!Path.IsPathFullyQualified(executable) || !Path.GetFileName(executable).Equals("DesktopDock.exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("启动项需要 DesktopDock.exe 的绝对路径。");
        this.keyPath = keyPath;
        command = $"\"{Path.GetFullPath(executable)}\"{StartupArgument}";
    }

    internal DockStartupStatus Read()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(keyPath);
            return Read(key);
        }
        catch (Exception error) when (IsRegistryError(error))
        {
            return new(DockStartupState.Unavailable, $"无法读取 Windows 启动项。{error.Message}");
        }
    }

    internal DockStartupStatus SetEnabled(bool enabled)
    {
        try
        {
            if (enabled && command.Length > 260)
                throw new InvalidOperationException("启动命令超出 Windows 的长度限制。请将程序放到路径较短的目录。");
            using RegistryKey? key = enabled ? Registry.CurrentUser.CreateSubKey(keyPath, writable: true)
                : Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
            DockStartupStatus status = Read(key);
            if (status.State == DockStartupState.Conflict)
                throw new InvalidOperationException(status.Message);
            if (enabled && status.State != DockStartupState.Enabled)
                key!.SetValue(ValueName, command, RegistryValueKind.String);
            if (!enabled && status.State is DockStartupState.Enabled or DockStartupState.OtherInstallation)
                key!.DeleteValue(ValueName, throwOnMissingValue: false);
            return Read(key);
        }
        catch (Exception error) when (IsRegistryError(error))
        {
            throw new InvalidOperationException($"无法修改当前用户的 Windows 启动项。{error.Message}", error);
        }
    }

    internal void RemoveForUninstall()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
        if (Read(key).State == DockStartupState.Enabled)
            key!.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    private DockStartupStatus Read(RegistryKey? key)
    {
        object? value = key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (value == null)
            return new(DockStartupState.Disabled, "未注册为当前用户的登录启动项。");
        if (value is string data && key!.GetValueKind(ValueName) == RegistryValueKind.String)
        {
            if (data.Equals(command, StringComparison.OrdinalIgnoreCase))
                return new(DockStartupState.Enabled, "当前版本已注册。Windows 的「启动应用」设置仍控制是否运行。");
            if (IsOwnedCommand(data))
                return new(DockStartupState.OtherInstallation, "启动项指向另一个 Desktop Dock 路径。");
        }
        return new(DockStartupState.Conflict, "相同名称的启动项已被其他命令使用，现有条目已保留。");
    }

    private static bool IsOwnedCommand(string commandLine)
    {
        if (!commandLine.StartsWith('"') || !commandLine.EndsWith("\"" + StartupArgument, StringComparison.Ordinal))
            return false;
        string executable = commandLine[1..^(StartupArgument.Length + 1)];
        return executable.IndexOf('"') < 0 && Path.IsPathFullyQualified(executable)
            && Path.GetFileName(executable).Equals("DesktopDock.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRegistryError(Exception error) => error is IOException or UnauthorizedAccessException or SecurityException;

    internal static object ProbeForTest(string executable)
    {
        string keyPath = $@"Software\DesktopDock.Validation.{Guid.NewGuid():N}";
        var a = new DockStartupRegistration(executable, keyPath);
        var b = new DockStartupRegistration(Path.Combine(Path.GetDirectoryName(executable)!, "另一版本", "DesktopDock.exe"), keyPath);
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true);
            key.SetValue("Unrelated", "keep", RegistryValueKind.String);
            DockStartupState initial = a.Read().State;
            DockStartupState enabled = a.SetEnabled(true).State;
            DockStartupState repeated = a.SetEnabled(true).State;
            int ownedValues = key.GetValueNames().Count(name => name == ValueName);
            DockStartupState other = b.Read().State;
            b.RemoveForUninstall();
            bool otherPreserved = a.Read().State == DockStartupState.Enabled;
            a.RemoveForUninstall();
            DockStartupState removed = a.Read().State;
            _ = a.SetEnabled(true);
            DockStartupState disabled = a.SetEnabled(false).State;
            key.SetValue(ValueName, "unrelated command", RegistryValueKind.String);
            DockStartupState conflict = a.Read().State;
            bool conflictPreserved;
            try
            {
                _ = a.SetEnabled(true);
                conflictPreserved = false;
            }
            catch (InvalidOperationException)
            {
                conflictPreserved = Equals(key.GetValue(ValueName), "unrelated command");
            }
            return new { initial, enabled, repeated, ownedValues, other, otherPreserved, removed, disabled, conflict,
                conflictPreserved, unrelatedPreserved = Equals(key.GetValue("Unrelated"), "keep") };
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKey(keyPath, throwOnMissingSubKey: false);
        }
    }
}
