# P0 WPF 窗口验证原型

在 Windows 11 交互桌面中从项目根目录运行。需要能构建 `net8.0-windows` 的 SDK 和 .NET 8 Desktop Runtime。若在当前 Codex 沙箱中遇到 `MSB4184` 的平台名称注册表错误，构建命令追加 `'-p:TargetPlatformMoniker=Windows%2CVersion=7.0'`，并保留项目内的中间文件输出路径。

```powershell
dotnet build prototypes/P0.Window/P0.Window.csproj -c Release --disable-build-servers -p:NuGetAudit=false --ignore-failed-sources
if ($LASTEXITCODE -ne 0) { throw 'P0.Window build failed' }

$app = (Resolve-Path -LiteralPath 'prototypes/P0.Window/bin/Release/net8.0-windows/P0.Window.exe').Path
foreach ($mode in @('probe', 'idle')) {
    $log = Join-Path ([IO.Path]::GetTempPath()) "desktop-dock-p0-$mode-$([guid]::NewGuid()).log"
    $start = [Diagnostics.ProcessStartInfo]::new($app)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.ArgumentList.Add("--$mode")
    $start.ArgumentList.Add($log)
    $process = [Diagnostics.Process]::Start($start)
    try {
        $process.WaitForExit()
        Get-Content -LiteralPath $log -Encoding utf8
        "processExitCode=$($process.ExitCode)"
    } finally {
        $process.Dispose()
    }
}
```

`--probe` 短暂显示自有 WPF 测试窗口，记录屏幕像素、原生命中测试、前台窗口是否变化、窗口分类结果，并关闭原型窗口。运行时保持原前台窗口稳定。`--idle` 显示透明窗口，预热 2 秒并采样约 30 秒，记录进程 CPU、工作集和私有内存。

日志逐项记录 `PASS`、`FAIL` 或 `UNVERIFIED`。退出码 `0` 表示未记录断言失败，仍需检查日志中的 `UNVERIFIED`。采样仅比较自有窗口位置的两个像素。位置被其他窗口遮挡或前台状态变化时重新运行。 [实测结论](../../docs/P0_VALIDATION_REPORT.md)
