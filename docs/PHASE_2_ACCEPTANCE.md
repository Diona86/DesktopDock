# Desktop Dock 阶段 2 验收

在 Windows 11 25H2 x64（26200.9974）验证。SDK 为 10.0.404，Windows x64 自包含发布包含 Desktop Runtime 10.0.15。Release 解决方案构建为 0 个警告、0 个错误，发布退出码 0。 [构建](p2-evidence/build.log) · [发布](p2-evidence/publish.log)

纯状态机和几何测试通过 43 项。Windows 成品 MCP 集成及最终复跑各通过 49 项，覆盖独立配置、实际 HWND 和菜单、系统快捷键冲突、迁移与重启。P1 回归执行 12 项并通过，按测试参数跳过再次打开浏览器及其他 Shell 关联窗口。 [核心行为](p2-evidence/behavior.log) · [MCP 集成](p2-evidence/mcp-results.log) · [最终复跑](p2-evidence/mcp-final.log) · [P1 回归](p2-evidence/p1-regression.log)

## 结果与边界

| 验收项 | 结果 | 实测证据与边界 |
| --- | --- | --- |
| 常驻与完全隐藏 | 通过 | P1 配置启动时常驻。切换自动隐藏后，WPF `IsVisible` 和原生 HWND 可见性都为假。手动显隐及重启后的隐藏状态通过。热区仅保存为坐标。 |
| 四边、多尺寸和重叠热区 | 通过 | 受控输入不同位置、厚度、偏移与时间，核对四边物理矩形、半开边界、重叠优先级、连续停留、跨区重新计时和采样中断。能在设置页按显示器查看当前热区。 |
| 多显示器 | 部分通过 | 实际枚举一台 2560 × 1440、100% 物理显示器。使用两台模拟显示器验证负坐标、150% 缩放、独立配置及触发屏选择。两台实体屏的跨屏定位和混合 DPI 尚待实测。 |
| 窗口智能避让 | 部分通过 | 真实 HWND 探针正确区分普通、最大化和无边框全屏窗口。生成的本地 AVI 在 WPF 全屏窗口实际播放，进度约 498 ms，默认拒绝呼出。模拟 Shell 独占信号、未知状态和按 EXE 路径允许或阻止的规则均通过。第三方游戏、第三方全屏播放器及实际独占 Direct3D 游戏待补测。 [Win32 实测](p2-evidence/windows.log) |
| 快捷键 | 部分通过 | 重复校验、自定义和恢复默认通过。Win32 两个消息 HWND 实测注册冲突 1409、释放后重注册，配置写入模拟失败时原绑定仍有效。常规受限会话返回过 5，批准后的普通桌面注册成功。实体键盘录入和系统实际投递 WM_HOTKEY 待补测。 [Win32 实测](p2-evidence/windows.log) |
| 临时、锁定、延迟、外部点击和 Esc | 部分通过 | 通过 MCP 调用实际状态机及 View 处理，验证临时显示、锁定、暂停仍可主动呼出、延迟取消、点击 Dock 内保持和外部立即收起。实际 WPF 菜单 HWND 为 155 × 55，指针样本留在菜单时超过延迟仍显示。首次 Esc 关闭菜单，第二次收起 Dock。鼠标钩子安装、停止和重装成功。物理外点与实按收起待补测。P3 的分组面板交互单列。 |
| 版本迁移 | 通过 | 实际读取版本 1，首次改写为版本 2 后备份与原字节 SHA-256 一致。重启恢复模式、多热区、应用例外和绑定，临时锁定和暂停重置。 |

## 性能与误触发

设置页及 WPF 窗口运行后，两轮启用真实被动指针采样。测试热区置于屏外，用于测量远离热区时的调度成本。22.01 秒样本读取 468 次，按机器逻辑处理器归一化的进程 CPU 为 0.2130%，工作集 177.08 MiB。最终 20.00 秒样本读取 426 次，归一化 CPU 为 0.1674%，工作集 176.69 MiB、私有内存 113.16 MiB。两轮均为 0 次前台查询、0 次误呼出。混合 DPI、持续移动和游戏负载仍需单独测量。靠近热区时按间隔查询，正式呼出前刷新一次避让判定。 [首次测量](p2-evidence/mcp-results.log) · [最终测量](p2-evidence/mcp-final.log)

窗口分类结合窗口样式、可见边界、DWM 状态、窗口最大化、显示器归属和 Shell 全屏信号。Shell 的独占正信号用于 Direct3D。窗口类别或会话信号不确定时阻止自动呼出，显式快捷键可请求非激活显示。 [Windows Shell 信号](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/ne-shellapi-query_user_notification_state)

## 运行与复测

- 退出现有 Dock 实例后运行本机 `artifacts/DesktopDock-P2-win-x64/DesktopDock.exe`。在设置中打开「热区与行为」选择自动隐藏，在「快捷键」录入组合。默认四项依次是 `Ctrl+Alt+Shift+F6` 到 `F9`。常驻模式默认保留 P1 行为。
- MCP 继续用 `--mcp-stdio --config-dir <独立测试目录>`。新增配置、显示器几何、步骤、显隐事件、原生采样与冲突探针。可通过 `tools/list` 查看参数。集成测试用 `python tests/P2/Test-DockP2.py`，输出在 `artifacts`。热区和快捷键两页的应用内 PNG 已实际查看。 [MCP 工作方式](P1_MCP.md) · [测试脚本](../tests/P2/Test-DockP2.py)
- 普通交互会话运行 `dotnet run --project tests/P2/Native/NativeProbe.csproj -c Release` 可复现独立 Win32 窗口、视频、鼠标钩子和快捷键冲突测试。加 `-- --skip-hotkeys` 跳过短暂的全局注册。

阶段 2 已交付。P3 等待确认。 [计划](PHASE_2_PLAN.md) · [项目 TODO](../TODO.md)

P5 发布包再次通过 P2 回归，并使用受控拓扑验证多个实际 Dock 的独立热区与显示状态。混合 DPI 的物理屏仍待现场复测。 [P5 验收](PHASE_5_ACCEPTANCE.md)
