# P0 输入与显示器验证原型

从项目根目录使用 PowerShell 构建和运行，无需第三方 NuGet 包。原型只在 Windows 上运行，使用开发机已安装的 .NET 8 运行时。

```powershell
dotnet build prototypes/P0.Input/P0.Input.csproj -c Release --disable-build-servers -p:NuGetAudit=false --ignore-failed-sources
.\prototypes\P0.Input\bin\Release\net8.0\P0.Input.exe
```

默认运行验证四边热区几何、DIP 换算和可控时钟用例。随后枚举当前显示器，用该屏的隐藏 PMv2 窗口读取 DPI，短暂注册 `Ctrl+Alt+Shift+F10` 并测试冲突、注销和重注册，最后记录 30 秒鼠标采样与进程资源。退出码 `1` 表示受控用例或快捷键生命周期检查未通过，显示器和鼠标结果以各自日志为准。

在普通交互桌面运行下列命令可记录真实输入。原型监听 45 秒，移动鼠标至日志所示的屏幕上边缘范围内并停留至少 350 毫秒，再移开至少 400 毫秒。按一次 `Ctrl+Alt+Shift+F10`，检查 `mouse native action=show`、`mouse native action=hide` 和 `hotkey native WM_HOTKEY`。快捷键注册成功与收到真实按键消息分别记录。

```powershell
.\prototypes\P0.Input\bin\Release\net8.0\P0.Input.exe --listen
```

原型在启动时读取一次屏幕拓扑。分别调整主副屏排列和缩放后重新运行，可对比像素矩形、DPI 和接口指纹。热插拔期间的更新、实际 Dock 输入命中和跨屏移动需要后续验证。显示器接口指纹用于对比观察，绑定策略需对照多屏拓扑和重连结果。

采样周期设为 25 毫秒，实际间隔随 Windows 定时器和调度变化。日志中的 `movingSamples` 表示相邻采样点位置不同，`maxIntervalMs` 用于判断可能漏采的短停留。`cpuPctOneCore` 以单逻辑核心为分母，`workingSetMiB` 和 `privateMiB` 是采样结束时的快照。
