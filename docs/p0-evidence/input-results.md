# P0 输入、DPI 与快捷键证据

2026-11-24 在 Windows 11（NT `10.0.26200.0`）交互会话 2 运行 `P0.Input`，使用 .NET 8.0.15 x64 和本机 .NET 9.0.203 SDK。复现命令见[输入原型说明](../../prototypes/P0.Input/README.md)。

| 验证 | 实测 | 结论 |
| --- | --- | --- |
| 显示器枚举和 DPI | `EnumDisplayMonitors` 返回 1 块主屏。`GetMonitorInfo` 返回 2560×1440 px，工作区同为 2560×1440 px。隐藏原型窗口 PMv2、`MonitorFromWindow` 匹配，`GetDpiForWindow` 返回 96。成功取得显示器接口并记录其指纹。 | 单屏 100% 缩放通过。双屏异 DPI、重连、负坐标物理拓扑与显示器身份稳定性未验证。 |
| DPI 换算 | 受控输入在负原点下通过 96、120、144、192 DPI 四挡往返，128 DIP 分别得到 128、160、192、256 px。 | 换算用例通过。实际 125%、150%、200% 和跨屏移动未验证。 |
| 热区几何与时序 | 四边负原点、半开边界、重叠优先级、短暂停留取消、进入与离开延迟、交互区域延迟取消、切换区域重新计时均有受控输入证据。真实鼠标采样读取成功，三次自动运行中没有触发热区状态变化。 | 几何与时序通过。真实鼠标触发、收起和与 WPF 交互区域组合未验证。 |
| 全局快捷键生命周期 | 沙箱内首次注册失败，Win32 5。常规桌面上线程 1 注册成功，线程 4 注册相同组合收到 Win32 1409，线程 1 两次注销均成功，注销后重注册成功。 | 注册、线程间冲突和注销通过。物理按键的 `WM_HOTKEY` 投递、进程异常退出和系统保留键未验证。 |

每次自动运行以 25 ms 为目标采样周期，并记录相邻采样时间。初测有超过 200 ms 的间隔，短时滑过存在漏采风险。三轮采样均观察到光标变化，因此性能数据属于输入采样原型工作负载。CPU 以单逻辑核心为分母，进程内存为结束时快照。

| 原始日志 | 光标成功/失败 | 光标变化样本 | 最大间隔 | 样本时长 | CPU 时间（单核比例） | 工作集 / 私有内存 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| [沙箱初测](input-sandbox.log) | 929 / 0 | 359 | 251 ms | 30,012.2 ms | 46.9 ms（0.156%） | 27.51 / 6.90 MiB |
| [本机同线程不同 ID 初测](input-normal.log) | 941 / 0 | 433 | 202 ms | 30,001.5 ms | 78.1 ms（0.260%） | 28.52 / 7.74 MiB |
| [本机跨线程复测](input-hotkey-competing-thread.log) | 952 / 0 | 282 | 57 ms | 30,021.9 ms | 62.5 ms（0.208%） | 28.43 / 7.74 MiB |

构建成功，0 条构建警告、0 个错误。.NET CLI 另报沙箱临时工具目录的 PATH 提示和 workload 校验提示。[构建日志](input-build.log)

DPI 采集使用隐藏窗口本身的 DPI 感知上下文与显示器位置。`EnumDisplayDevices` 提供的显示器接口用于对比，复接时应进一步核验显示路径和设备身份。快捷键 API 与窗口线程绑定，原型在持有线程上释放注册。[GetDpiForWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getdpiforwindow) · [EnumDisplayDevicesW](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-enumdisplaydevicesw) · [RegisterHotKey](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey)
