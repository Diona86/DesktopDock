# 安装、升级与卸载

Desktop Dock 面向 Windows 11 x64。安装包按当前用户安装，默认程序目录为 `%LOCALAPPDATA%\Programs\Desktop Dock`。可选择目录、桌面快捷方式和登录启动。程序自带运行时。

1. 如有旧版正在运行，先从其托盘菜单退出。
2. 运行 `DesktopDock-<版本>-win-x64-setup.exe`，阅读安装路径和任务选项。
3. 启动后通过托盘打开设置。程序使用 `%LOCALAPPDATA%\DesktopDock\config` 保存默认档案。便携版也使用此档案，除非显式传入绝对路径的 `--config-dir`。

卸载从 Windows「设置 → 应用 → 已安装的应用」打开。保留配置是默认行为。交互卸载可明确选择清理应用识别的配置。清理路径以程序维护的文件清单为准，项目目标和外部自定义图标都留在原处。明确清理时会先检查桌面图标恢复记录。如果逐文件清理遇到权限或磁盘错误，可从之前的备份恢复已清理的配置。便携版可通过 `DesktopDock.exe --uninstall-cleanup` 清理其注册的登录启动项。

## 备份与恢复

安装器写入程序前检查档案和主题版本，在 `%LOCALAPPDATA%\DesktopDock\release-backups` 生成带 SHA-256 清单的备份。不兼容或读写失败会阻止升级。安装目录下的 `versions` 按版本隔离程序文件。现有 Dock 档案的旧结构在该档案第一次成功写入时迁移，原配置保留迁移备份。备份包含 Dock 配置、主题、显示器档案和恢复状态。桌面文件、项目目标及外部自定义图标仍由原位置管理。

命令行支持检查、备份和恢复。执行前退出使用同一档案的 Dock。以下为默认安装位置的 PowerShell 示例。按实际情况替换程序目录和备份位置。

```powershell
$dockExe = Join-Path $env:LOCALAPPDATA 'Programs\Desktop Dock\versions\1.0.0\DesktopDock.exe'
$profile = Join-Path $env:LOCALAPPDATA 'DesktopDock\config'
$backupRoot = Join-Path $env:LOCALAPPDATA 'DesktopDock\release-backups'
& $dockExe --verify-profile $profile | Out-Host
$snapshot = & $dockExe --prepare-upgrade $profile $backupRoot 1.0.0 1.0.0
$snapshot
```

各命令运行后可通过 `$LASTEXITCODE` 核对退出码。确定要恢复后，将 `$snapshot` 设为所选择的备份目录，再运行以下命令。

```powershell
& $dockExe --restore-upgrade $profile $snapshot | Out-Host
```

恢复前核对备份清单，恢复过程中为当前配置另做检查点。桌面图标恢复记录保留当前值，避免使用旧 Explorer 窗口身份。升级中断后可在注册的原目录重试，或恢复档案后运行 `versions\旧版本\DesktopDock.exe`。若首次安装中断并留下部分文件，可选新的空目录重试。已有登录启动设置会在升级时保留，可在应用内关闭。

正式包的校验值见随包的 `SHA256SUMS.txt`。测试范围和设备限制见 [发布验收](PHASE_6_ACCEPTANCE.md) 和 [已知限制](KNOWN_ISSUES.md)。
