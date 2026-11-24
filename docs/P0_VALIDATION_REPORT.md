# Desktop Dock P0 技术验证报告

2026-11-24，Windows 11 25H2 x64，内部版本 `26200.9974`，Intel Core Ultra 7 155U、14 个可用逻辑处理器。原型运行在交互会话 2 的 `WinSta0\Default`。显示器枚举结果为一台 2560 × 1440、96 DPI 的主屏。阶段状态为**交付已暂停，验收部分通过**。复测条件和建议方案见下文。

## 仓库和工具链

- 启动时项目目录只有 `CODEX_MASTER_PROMPT.md`、`PRD.md`、`TECHNICAL_DESIGN.md`、`README.md`。已阅读三份基线。Git 2.55.0 检查返回 `not a git repository`，当前没有可核对的分支或未提交状态。未发现可复用代码、解决方案、依赖、测试或 TODO。执行期间另有 `fen.txt` 出现在根目录，已保留。结束时四份原有文档的 SHA-256 与启动检查一致。
- 本机有 .NET SDK 6.0.407 和 9.0.203，Visual Studio 2022 Professional / Community 17.13 均列出 .NET 桌面开发工作负载。本机 CLI 成功列出 WPF 模板并为 `net8.0-windows` 构建 Release。默认沙箱曾限制模板目录、SCM 和 MSBuild 注册表访问，已通过本地 CLI 缓存及固定目标平台标识完成编译。 [环境证据](p0-evidence/environment.txt)
- Microsoft 官方便携 .NET SDK 10.0.404 经 SHA-512 校验，构建了标准 WPF 模板和 WPF 原型临时副本，均为 0 警告、0 错误。原型在 Desktop Runtime 10.0.15 实际运行并退出 0。建议正式目标为 `net10.0-windows`。原型源码可用本机已装的 .NET 8 运行时复现。正式开发使用 .NET 10 CLI，若选用 Visual Studio，则使用 18.0 或更高版本。 [环境证据](p0-evidence/environment.txt) · [.NET 10 窗口实测](p0-evidence/window-net10.log) · [.NET 支持策略](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core) · [SDK、MSBuild 与 Visual Studio 兼容表](https://learn.microsoft.com/en-us/dotnet/core/porting/versioning-sdk-msbuild-vs)

## 逐项实测

| 项目 | 判定 | 步骤、证据与已知限制 |
| --- | --- | --- |
| P0-01 WPF 透明无边框 | 部分通过 | 独立 WPF 窗口在 .NET 8 和 .NET 10 均测得 `WS_EX_LAYERED`、无窗口边框、渲染事件和正确的桌面像素合成。透明点命中原型背景窗口，绘制点命中 Dock，半透明点 RGB 为 `(124,60,96)`。任务栏排除核对了 `ShowInTaskbar=False` 和原生扩展样式。实际任务栏表现、点击事件及动画中的输入命中未实测。 [窗口日志](p0-evidence/window-check.log) · [.NET 10 日志](p0-evidence/window-net10.log) |
| P0-02 显示与隐藏焦点 | 通过 | 使用 `ShowActivated=false`，定位调用使用 `SWP_NOACTIVATE`。两个运行时各 3 轮，显示前、显示后、隐藏后的前台 HWND 一致，Dock 激活事件为 0，隐藏后 `IsWindowVisible=False`。适用边界为当前会话中的 Show/Hide。第三方全屏前台、点击内容和设置窗口激活另待复测。 [窗口日志](p0-evidence/window-check.log) · [.NET 10 日志](p0-evidence/window-net10.log) |
| P0-03 多显示器与 DPI | 部分通过 | 原生显示器枚举获取真实边界、工作区与显示设备接口指纹。对应隐藏 HWND 证实 Per-Monitor V2、生效 DPI 96 和正确显示器归属。负原点及 96、120、144、192 DPI 的 DIP 换算通过确定性用例。当前仅一台屏，混合 DPI、跨屏移动、主屏变化、分辨率变化、拔插和稳定标识跨拓扑复测均未验证。 [输入日志](p0-evidence/input-sandbox.log) |
| P0-04 鼠标热区 | 部分通过 | 受控坐标与时间覆盖四边、负坐标、半开边界、偏移、短暂进入后取消、重叠优先级、切换区域重置计时、交互区延迟收起和返回取消。最终非沙箱 30 秒 `GetCursorPos` 采样全部 952 次成功，其中 282 次位置变化、0 次热区状态转换。配置间隔 25 ms，最终采样的最大间隔为 57 ms，前一轮为 202 ms。真实鼠标满足停留和离开阈值后的转换仍待验证。 [输入实测](p0-evidence/input-results.md) · [复测方法](../prototypes/P0.Input/README.md) |
| P0-05 窗口最大化 | 部分通过 | 对自有普通和最大化 WPF 窗口分别读取 `IsZoomed`、样式及 DWM 边界，两个运行时均正确分类。还成功读取当前非最大化前台窗口。第三方最大化应用、逐屏策略未验证。 [窗口日志](p0-evidence/window-check.log) · [.NET 10 日志](p0-evidence/window-net10.log) |
| P0-06 全屏检测 | 部分通过 | 同一真实无边框 WPF 窗口缩进 48 px 时判定普通，覆盖整个屏幕时判定无边框候选。当前工作区与屏幕边界相同，无法验证两者分离时的判定。真实视频、无边框游戏及独占全屏均未执行。 [窗口日志](p0-evidence/window-check.log) · [.NET 10 日志](p0-evidence/window-net10.log) |
| P0-07 桌面图标只读识别 | 通过 | 在本机会话找到同一 PID 的 `Progman → SHELLDLL_DefView → SysListView32` 且视图可见。常规沙箱中的 `ShellWindows` 激活返回 `0x80070005`。经批准只读重试，`FindWindowSW(SWC_DESKTOP)` 返回同一桌面 HWND，`IFolderView2.GetCurrentFolderFlags` 返回 `0x40200224`，`FWF_DESKTOP` 为真、`FWF_NOICONS` 为假，与 `Shell.Application.GetSetting(SSF_HIDEICONS)=False` 一致。 [Shell 实测](p0-evidence/shell-probe.txt) · [只读探针](../prototypes/P0.Shell/Probe-DesktopIcons.ps1) |
| P0-08 桌面图标隐藏与恢复 | 未验证 | 当前用户会话中的图标隐藏需要项目文档要求的明确确认，本轮未执行写操作。Explorer 重启、原型异常退出、多屏、用户手动改动及普通和系统图标分范围控制未执行。当前环境未定位到可用的隔离 Windows 测试会话。保留 Windows「查看 → 显示桌面图标」作为手动控制入口。 [隔离验收步骤](../prototypes/P0.Shell/README.md) |
| P0-09 全局快捷键 | 部分通过 | `RegisterHotKey` 在常规沙箱返回 Win32 5。同一交互会话经批准重试，主线程成功注册临时组合 `Ctrl+Alt+Shift+F10`，另一线程注册同一组合返回 Win32 1409，注销、重新注册和再次注销均成功。实际键盘输入引发 `WM_HOTKEY` 尚未验证。 [快捷键日志](p0-evidence/input-hotkey-competing-thread.log) · [复测方法](../prototypes/P0.Input/README.md) |
| P0-10 空闲 CPU 与内存 | 通过 | Release WPF 可见窗口均预热 2 秒。 .NET 10 在 30.003 秒内消耗进程 CPU 15.62 ms，等效单核 0.0521%，按 14 个逻辑处理器归一化为 0.0037%。工作集约 138.01 → 138.11 MiB，私有内存约 81.47 → 81.41 MiB。 .NET 8 的 30.010 秒样本用时 578.12 ms，单核 1.9265%，归一化 0.1376%，工作集 137.85 → 146.39 MiB，私有内存 80.64 → 82.02 MiB。两组均满足归一化 CPU ≤ 1% 和工作集 ≤ 150 MiB。每个运行时各采样一次，未计 DWM 和未来产品服务，不能据此比较运行时性能。 [.NET 10 空闲日志](p0-evidence/window-net10-idle.log) · [.NET 8 空闲日志](p0-evidence/window-idle.log) |

输入原型在非沙箱下最终独立采样约 30 秒，含鼠标活动，消耗进程 CPU 62.5 ms，等效单核 0.208%，工作集 28.43 MiB、私有内存 7.74 MiB。实际采样间隔随机器负载变化，需要复测热区端到端响应。 [输入实测](p0-evidence/input-results.md)

## 技术决策与降级

1. **窗口**。采用 WPF 无边框透明窗、非激活呼出和 Per-Monitor V2。从单进程多窗口开始评估。原型的临时置顶仅用于可控像素测量。产品置顶策略随窗口状态决定。保持透明绘制区域紧凑，若后续命中或性能回归，先关闭动画和重材质并复测。
2. **坐标与热区**。屏幕边界、工作区、鼠标和热区统一用物理像素。窗口按自身 DPI 和所在屏原点将局部位置转换为 DIP。显示设备接口标识可作为候选标识，拓扑变化前须复测。热区计时使用单调时钟，处理采样中断并保留暂停选项。前台和窗口变化优先事件驱动，避免把 25 ms 原型轮询直接作为产品默认。
3. **窗口避让**。合用前台句柄、`IsZoomed`、样式、监视器边界和 DWM 状态。当前证据支持无边框候选分类。未知或疑似全屏时默认抑制自动呼出，保留显式快捷键入口并延后验证全屏焦点与层级。
4. **桌面图标**。保留只读诊断和 Windows 手动入口。自动修改暂不接入。`SetCurrentFolderFlags` 的文档列出了 `FVO_CUSTOMPOSITION` 前置要求，写入适用性和持久性待测。隔离环境先验证操作前状态、显示切换、恢复及原文件，再复测 Explorer 重启、用户手动改动和异常退出。状态或所有权有歧义时提示用户恢复。 [候选接口文档](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ifolderview2-setcurrentfolderflags)
5. **快捷键**。应用层先检查重复绑定，Windows 集成层以原生注册结果为准。冲突或访问拒绝要显示为不可用，并释放已注册组合。保留受控实按、跨应用冲突和进程退出清理复测。

阶段只新增独立原型、计划、验收记录、实测日志和 TODO。项目初始化和 P1 留待确认。 [P0 计划](PHASE_0_PLAN.md) · [P0 验收](PHASE_0_ACCEPTANCE.md) · [项目 TODO](../TODO.md)

原型复现命令见各自的运行说明。 [窗口](../prototypes/P0.Window/README.md) · [输入](../prototypes/P0.Input/README.md) · [桌面图标](../prototypes/P0.Shell/README.md)
