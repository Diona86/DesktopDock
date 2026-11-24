# P0 桌面图标只读探针

在 Windows 11 的交互登录会话中使用 PowerShell 7.4 或更新版本运行。脚本通过 `Add-Type` 编译独立探针，并输出窗口层级、窗口可见样式、全局 Shell 设置和桌面视图标志。

```powershell
pwsh -NoProfile -STA -File .\prototypes\P0.Shell\Probe-DesktopIcons.ps1
```

- `candidate_count=1` 与 `shell_stable=True` 表示本次找到唯一、同 Shell 进程的 `Progman` / `WorkerW` → `SHELLDLL_DefView` → `SysListView32` 候选链。窗口句柄需要在每次观察时重新查找。
- `shell_setting_hide_icons` 来自 `Shell.Application.GetSetting(SSF_HIDEICONS)`。`no_icons` 来自桌面 `IFolderView2` 的 `FWF_NOICONS`。窗口可见样式与两项标志用于交叉检查，实际画面需要独立观察。
- `IShellWindows.FindWindowSW(SWC_DESKTOP)` 返回的 HWND 必须匹配当前 `GetShellWindow`。探针查询顶层 `IShellBrowser` 的活动 `IShellView` 并读取 `IFolderView2`，完成后释放 COM 引用。沙箱可能在激活 `IShellWindows` 时返回 `0x80070005`，此时仍可记录 Win32 层级和全局 Shell 设置。

## 隔离环境验收步骤

桌面变更和 Explorer 重启分别在有快照的 Windows 11 测试环境中进行，先确认操作范围。记录系统构建号和屏幕拓扑，测试前后运行只读探针并保留输出。

1. 在测试桌面准备一个可辨识的普通文件，并选择一个已显示的系统图标。通过 `Get-FileHash -Algorithm SHA256` 记录该测试文件哈希，同时记录初始桌面外观与菜单勾选状态。
2. 在桌面空白处右击，进入「查看」并取消「显示桌面图标」。验证测试图标都消失，双屏环境分别观察两屏。通过文件资源管理器访问同一测试文件，复核路径和文件哈希。
3. 将「显示桌面图标」恢复为测试前状态。验证图标按原设置恢复，复核菜单、探针与文件哈希。
4. 单独验证 Explorer 重启。关闭无关窗口并结束文件传输，在测试环境中通过任务管理器重启 Windows 资源管理器，记录重启前后 Shell PID、句柄、菜单与画面。对图标可见和隐藏两种初始状态分别重复。每轮结束都恢复原勾选状态。
5. 程序化隐藏、正常退出恢复、人工修改优先和强制终止后的恢复需要独立的写入原型验收。验证重启后重新发现活动视图，测试用户切换过的状态，并保留原生菜单手动恢复入口。

`IFolderView2.SetCurrentFolderFlags` 是候选接口。Microsoft 对 Windows 7 及之后版本列出了 `FVO_CUSTOMPOSITION` 使用要求，需在测试环境测量标志、画面和图标位置在 Explorer 重启前后的行为。该候选接口目前只完成读取验证。Windows 原生菜单覆盖全部图标，系统图标可单独通过 Windows 的「桌面图标设置」管理。普通图标和系统图标分范围的程序化控制仍待验证。 [SetCurrentFolderFlags](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ifolderview2-setcurrentfolderflags) · [Microsoft 的桌面图标设置说明](https://support.microsoft.com/en-us/windows/experience/personalization/customize-the-desktop-icons-in-windows)

读取接口和 ABI 依据见 [IShellWindows.FindWindowSW](https://learn.microsoft.com/en-us/windows/win32/api/exdisp/nf-exdisp-ishellwindows-findwindowsw)、[IFolderView2.GetCurrentFolderFlags](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ifolderview2-getcurrentfolderflags)、[Shell.GetSetting](https://learn.microsoft.com/en-us/windows/win32/shell/shell-getsetting) 与 [Microsoft Windows SDK IDL](https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/um/ShObjIdl_core.idl)。
