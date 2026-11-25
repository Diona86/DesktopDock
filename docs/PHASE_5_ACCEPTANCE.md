# Desktop Dock 阶段 5 验收

## 使用

运行 `artifacts/DesktopDock-P5-win-x64/DesktopDock.exe`。在设置左栏选择要管理的显示器。每台屏幕保存独立的 Dock 位置、布局、条目、主题覆盖、位置锁和热区。变更 Windows 主屏后，原配置仍跟随第一次绑定的显示器。重连按设备标识恢复。如果驱动改变设备接口标识，应用可能将它识别为新屏幕，原配置仍保存在磁盘上。

- 「热区与行为 → 打开可视化热区地图」显示所有相连屏幕和 Dock。选择一个热区后，可拖动起点、终点、偏移、厚度的手柄。按「应用到当前屏」保存。删除最后一个热区会停用该屏热区。恢复默认可重新启用默认设置。
- 托盘提供显示/隐藏全部 Dock、暂停/恢复热区、设置、主题编辑器、桌面图标恢复和退出。
- 「通用设置 → 登录 Windows 时启动 Desktop Dock」管理当前用户的登录启动项。Windows「启动应用」设置仍可控制是否执行。便携版删除前，可在程序目录运行 `DesktopDock.exe --uninstall-cleanup` 清理该路径所拥有的启动项。此命令保留其他版本的启动项与全部 Dock 配置。 [Windows Run 项文档](https://learn.microsoft.com/en-us/windows/win32/setupapi/run-and-runonce-registry-keys)

## 桌面图标和恢复

从托盘「桌面图标」或「通用设置」进入。隐藏需要在确认对话框中选「是」，程序先保存 Explorer 身份和操作前状态，再请求切换该会话的实时图标视图。普通图标和当前可见的系统图标共用这个列表。单独选择「此电脑」「回收站」等系统项目使用窗口里的 Windows 设置入口。

- 通过托盘「恢复桌面图标」或 P5 固定快捷键 `Ctrl+Alt+Shift+F11` 恢复 Dock 创建的隐藏状态。快捷键被占用时，窗口会显示注册错误。
- 手动入口始终可用。在桌面空白处右键，点「查看 → 显示桌面图标」。如果已经勾选，先取消再重新勾选。
- 程序会核对 Explorer PID、进程启动时间、桌面窗口与 Windows 显示偏好。正常退出时，限时核对本次进程发起的异步请求，确认隐藏后尝试恢复。如果检测到用户改动、Explorer 更换或状态不确定，则保留当前选择并提供手动入口。异常退出后在下次启动核对恢复记录。 [Win32 ShowWindowAsync](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-showwindowasync)

## 复测

- `python tests/P5/Test-DockP5.py` 在独立配置中操作真实 WPF 窗口，通过应用内 MCP 提交受控的显示器和热区坐标。
- 加上 `--native-system` 可使用临时测试注册表键以及 Win32 快捷键探针。实际登录启动项不受这条测试命令影响。
- 独立桌面恢复探针使用 `dotnet run --project tests/P5/DesktopIcons/DesktopIconsProbe.csproj -c Release -- <空的绝对输出目录>`。读取真实 Explorer，并在探针拥有的跨进程窗口和受控后端执行显隐、恢复和故障路径。 [独立探针日志](p5-evidence/desktop-icons-probe.log)

## 实测结果

在 Windows 11 x64 构建和验收。Release 构建 0 警告、0 错误，Windows x64 自包含包发布成功。最终包通过 33 项 MCP 检查。P1、P2、P3、自由网格和 P4 回归分别通过 12、49、56、36 和 53 项。 [P5 MCP](p5-evidence/p5-mcp.log) · [P1](p5-evidence/p1-regression.log) · [P2](p5-evidence/p2-regression.log) · [P3](p5-evidence/p3-regression.log) · [自由网格](p5-evidence/p3-grid-regression.log) · [P4](p5-evidence/p4-regression.log)

| 验收项 | 观测结果 |
| --- | --- |
| 多屏和配置恢复 | 独立探针通过 18 项，最终包在受控拓扑下驱动两个真实 HWND，验证负坐标、独立主题、布局、锁定和热区。主屏、DPI、分辨率和排列变化后原档案仍归属原标识。断开时保存的配置哈希未变，重连和进程重启后恢复。普通运行模式还在本机真实屏幕上启动两次，异常结束测试进程后同一隔离配置可重新打开，原配置字节一致。 [显示器探针](p5-evidence/display-core.log) · [普通启动](p5-evidence/normal-launch.log) |
| 热区地图 | 独立 WPF 检查通过 19 项。最终包实测端点、偏移和厚度换算、重叠提示、添加和删除、只保存所选屏，以及清空与恢复默认。查看了实际 WPF 截图。 [热区探针](p5-evidence/hot-zone-map.log) |
| 桌面图标 | 独立探针通过 29 项。实际 Explorer 只读结果为图标可见、Windows 隐藏偏好关闭。跨进程显隐只对测试窗口执行。受控后端验证了确认后的记录顺序、一键恢复、刚隐藏即退出、退出等待超时、用户手动覆盖、Explorer 代际更换、异常记录和恢复。 [桌面图标探针](p5-evidence/desktop-icons-probe.log) |
| 托盘与登录启动 | 应用内调用托盘命令验证全部 Dock 显隐、暂停和恢复、设置和正常退出。真实 Win32 注册验证恢复快捷键冲突与释放。临时注册表键验证启动项启停、重复注册、路径归属、冲突保护和卸载清理。 [P5 MCP](p5-evidence/p5-mcp.log) |

当前设备枚举到一台物理屏幕。实体拔插、混合 DPI、当前 Explorer 图标显隐与重启、实体托盘点击和快捷键投递、实际 Windows 登录启动仍待现场验证。P5 在此停止，P6 等待确认。 [计划](PHASE_5_PLAN.md) · [项目 TODO](../TODO.md)
