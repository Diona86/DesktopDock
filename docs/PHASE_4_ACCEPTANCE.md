# Desktop Dock 阶段 4 验收

## 使用

退出使用同一配置的旧版，运行 `artifacts/DesktopDock-P4-win-x64/DesktopDock.exe`。从托盘菜单或「设置 → Dock 外观」打开主题编辑器。左侧选择应用范围、主题和参数分类，中间预览 Dock 和分组，右侧编辑数值。编辑器打开时，可见的 Dock 跟随预览；取消会恢复最近一次保存的配置。

- 改动内置主题后保存会创建自定义主题，原内置主题继续保留。也可用「另存为」直接指定名称。
- 「当前 Dock」可以选择独立主题，也可以继承全局主题并只覆盖勾选的参数。P4 的实际应用显示一个 Dock，多个 Dock 窗口属于 P5。
- `.docktheme` 是 UTF-8 JSON，当前版本为 1。外层字段为 `format: desktop-dock-theme`、`schemaVersion` 和 `theme`。纹理为内嵌的点阵、网格或斜线等声明式参数。单文件上限 32 KiB。导入进入草稿，保存后加入主题库。
- 玻璃模糊作用于 Dock 自绘的材质层。性能模式移除模糊与阴影，将启用的动画简化为最多 100 ms 的淡入淡出。全局关闭动画后，Dock 和分组即时切换。 [Windows DWM 接口说明](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/nf-dwmapi-dwmenableblurbehindwindow)

## 复测

在 Windows 11 执行 `python tests/P4/Test-DockP4.py`。脚本使用独立配置和真实 WPF 窗口，通过应用内 MCP 调用编辑器业务路径，保存窗口 PNG 和结构化结果。 [阶段计划](PHASE_4_PLAN.md) · [项目 TODO](../TODO.md)

## 实测结果

环境为 Windows 11 x64，系统版本 26200，.NET SDK 10.0.404。

Release 构建零警告、零错误，Windows x64 自包含发布成功。专项 53 项、核心 45 项、编辑器独立验证 14 项均通过。最终包的 P1、P2、P3 和自由网格回归分别通过 12、49、56 和 36 项。连续 18 次实时切换测得中位数 71.4 ms，P95 为 100.1 ms。采样包含本机 MCP 往返与 WPF 布局。 [发布包验收日志](p4-evidence/p4-mcp.log)

| 验收项 | 观测结果 |
| --- | --- |
| 三种主题 | 实际 WPF PNG 各不相同。极简为纯色圆角，玻璃为半透明材质和条形手柄，科技为方角网格材质、线形手柄及多行布局。 |
| 编辑和恢复 | 即时读到圆角、背景画刷与图标尺寸变化。撤销与恢复默认值恢复实测参数，取消后 Dock PNG 与初始截图哈希一致。编辑器检查了常用尺寸及 760 × 510 DIP 的最小窗口。 |
| 保存、另存和覆盖 | 另存创建独立主题，保存更新已有自定义主题，重启恢复全局、局部参数和性能开关。验证切换全局时保留局部圆角，恢复继承时保留逐项覆盖。独立核心另测试两个 Dock 标识。 |
| 主题文件 | 导出主题包含全部参数和声明式纹理；在另一份配置中导入、预览并保存成功。错误版本、未知字段、路径穿越字段、代码字段、越界数值、重复字段、超限文件和重复标识均被拒绝，配置和草稿保持不变。损坏的主题配置仍可用默认主题启动，并保留原文件。 |
| 动画与性能 | 观测到呼出和收起中间帧。收起开始即取消命中，完成后 HWND 隐藏。收起期间再次呼出能取消待执行的隐藏。关闭动画后 Dock 和分组立即切换。性能模式确实移除了 WPF 阴影和材质模糊。 |
| P3 兼容 | 主题操作前后的 `dock.json` 字节一致，项目、分组和自由网格继续使用原配置。复跑早期阶段的适用路径。 |

测试覆盖真实 WPF 窗口的应用内输入和独立 Windows API 探针。实体指针和每种 GPU、DPI 组合属于后续设备复测。P1 回归保留实际 EXE 与 LNK 的启动检查，跳过浏览器和其他外部 Shell 窗口。 [核心](p4-evidence/core.log) · [编辑器](p4-evidence/editor.log) · [P1](p4-evidence/p1-regression.log) · [P2](p4-evidence/p2-regression.log) · [P3](p4-evidence/p3-regression.log) · [自由网格](p4-evidence/p3-grid-regression.log)
