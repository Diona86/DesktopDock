# Desktop Dock 1.0.0 发布验收

平台为 Windows 11 x64，应用基于 .NET 10 和 WPF。发行结构包括每用户安装包、自包含便携包、已提交源码和 SHA-256 清单。设备验证范围单列，受控输入结果按其实际覆盖记录。

## 发行与数据保护

| 验收项 | 证据 |
| --- | --- |
| 构建与版本 | .NET SDK 10.0.404，Release 构建 0 警告、0 错误。WinExe 返回 `1.0.0`，同一版本由运行中的 Dock 报告。便携 ZIP 中程序集的 SHA-256 与性能测试应用一致。 [发布 CLI](p6-evidence/release-cli.log) · [正式包校验](p6-evidence/release-binary.log) |
| 安装与卸载 | Inno Setup 7.1.1 生成未签名的正式安装器。真实安装器隔离变体通过 19 项，涵盖自选 Unicode 路径、开始菜单和桌面快捷方式、登录选项、卸载默认保留和明确清理。桌面恢复记录有问题时，实测卸载停止并保留程序、配置和登录启动项。登录项测试使用临时非启动注册表键。 [构建记录](p6-evidence/installer-build.log) · [安装验收](p6-evidence/installer-validation.log) |
| 安装中断与升级失败 | 隔离安装器在写入第一个新版文件后实际结束测试工作进程。旧版登记、程序哈希和配置完整，后续升级成功并保留旧程序。备份目录拒绝权限、档案占用、损坏主题以及降级均阻止升级。测试使用 1.0.1 作为升级路径的探针版本号。 [安装验收](p6-evidence/installer-validation.log) |
| 版本与兼容性 | 独立 Windows 探针通过 37 项。验证 v1–v4、降级拒绝、主题和显示器索引兼容性、SHA-256 清单、备份篡改拒绝和档案占用拒绝。 [升级探针](p6-evidence/profile-upgrade.log) |
| 迁移、恢复与异常 | 发行 WinExe 通过 11 项检查。已有档案预检保留原字节，首次 WPF 保存迁移并生成原始备份。显式恢复产生检查点，独立探针用真实文件占用触发部分写入失败并核对回滚。损坏主题拦截升级，未处理的桌面恢复记录阻止删除配置。外部目标哈希保持一致。 [发布 CLI](p6-evidence/release-cli.log) · [升级探针](p6-evidence/profile-upgrade.log) |
| 诊断导出 | 实际 WPF 进程导出 JSON，包含版本及启动事件。用例确认目标路径、项目名称和文件内容的测试标记未出现在导出中。 [发布 CLI](p6-evidence/release-cli.log) |

## 功能回归

最终程序的 P1、P2、P3、自由网格、P4、P5 分别通过 13、49、56、36、53、33 项检查。回归日志中的独立用例路径与发布应用程序集 SHA-256 可相互核对。

| 领域 | 实际验证范围 |
| --- | --- |
| 常驻、自动隐藏、热区和避让 | 真实 WPF HWND 的显隐、受控热区、计时和菜单命中。Win32 探针验证普通、最大化、无边框全屏、前台规则和真实 WPF 静音视频。 [P2 回归](p6-evidence/p2-regression.log) · [Win32 窗口探针](p6-evidence/native-windows.log) |
| 快捷键 | Windows 注册、占用冲突、修改事务和释放。恢复快捷键经过 Win32 注册探针，应用动作经 MCP 验证。 [P2 回归](p6-evidence/p2-regression.log) · [P5 回归](p6-evidence/p5-regression.log) |
| 添加、启动与引用保护 | 实际 EXE、LNK 启动并收到回执。文件、目录和打开所在位置的 Shell 请求成功。系统默认浏览器向本机测试监听器返回 HTTP 请求。图标、菜单及删除引用后的原文件校验和通过。 [P1 回归](p6-evidence/p1-regression.log) |
| 拖拽、分组与网格 | 使用真实 WPF 控件和相同的布局与投放业务路径，覆盖两级分组、移动、重排、空格投放、跨度和持久恢复。 [P3 回归](p6-evidence/p3-regression.log) · [自由网格](p6-evidence/p3-grid-regression.log) |
| 主题编辑与主题文件 | 实际预览、撤销、清除预览、保存、另存、全局与本地覆盖。主题导入导出、版本与大小限制、恶意和损坏文件拒绝。 [P4 回归](p6-evidence/p4-regression.log) |
| 多屏、托盘、热区编辑和登录启动 | 真实 WPF 窗口在受控拓扑中的独立尺寸、DPI 换算、重连和保存。托盘业务命令与退出、Windows 临时注册表键中的启动项读写和所有权。 [P5 回归](p6-evidence/p5-regression.log) |
| 桌面图标、异常退出与恢复 | 29 项独立探针通过。实际 Explorer 状态只读核对，显隐操作对隔离 Win32 窗口执行。受控后端覆盖 Explorer 代际变化、恢复记录、用户手动更改和异步退出。 [桌面图标探针](p6-evidence/desktop-icons.log) |

## 性能与持续运行

Windows 11 版本 26200，14 个逻辑处理器。数值为最终发布应用负载的真实采样。启动测试共五个新进程，未清除系统文件缓存。延迟包含采样与 MCP 往返。双 Dock 使用受控拓扑下的两个真实 HWND，采样期间同机执行部分发布工作。 [原始样本](p6-evidence/performance.json) · [运行日志](p6-evidence/performance.log)

| 指标 | 实测 |
| --- | --- |
| 启动到原生窗口可见 | 中位数 2.198 s，范围 1.524–2.356 s |
| 空闲 CPU 与内存 | 15 秒窗口内，单 Dock 进程 CPU 增量低于该采样分辨率，工作集 168.05 MiB，私有内存 99.98 MiB。双 Dock 的整机归一化 CPU 为 0.0223%，工作集 211.70 MiB，私有内存 128.56 MiB |
| 多 Dock 增量 | 双 Dock 较单 Dock 增加 43.64 MiB 工作集、28.58 MiB 私有内存。自动隐藏启用原生轮询后，15 秒采样 403 次指针，意外呼出 0 次 |
| 热区呼出 | 250 ms 配置延迟下，中位数 258.9 ms，P95 为 264.6 ms。达到阈值后至观测窗口可见的中位数为 8.5 ms |
| 图标加载 | 12 个启动图标全部可见中位数 2.201 s。运行中更换本地 PNG 到观测位图，包含配置保存的中位数 40.7 ms，6 次 |
| 主题切换 | 16 次 WPF 应用与状态往返的中位数 52.9 ms，P95 为 206.6 ms |
| 持续运行 | 600.003 秒双 Dock 样本。30 次状态查询均响应，两扇窗口始终可见。状态往返中位数 3.926 ms、P95 为 18.939 ms，整机归一化 CPU 为 0.0110%。采样工作集 200.08–210.61 MiB、私有内存 117.21–125.10 MiB。 [十分钟日志](p6-evidence/long-run.log) · [十分钟样本](p6-evidence/long-run.json) |

标准性能脚本的三分钟样本和 16 项检查也保留。其中包括强制结束测试进程后的恢复、损坏配置与主题保留、只读文件权限不足和项目目标移动后的重新定位。 [性能与异常测试](p6-evidence/performance.log)

## 现场验收边界

本机枚举到一台物理屏，且未安装 Windows Sandbox。实体显示器拔插和混合 DPI、真实 Explorer 显隐与重启、实际 Windows 登录启动、第三方独占全屏和物理手势仍待在相应系统验证。桌面状态相关自动化使用只读 Explorer 和隔离用例。 [已知限制](KNOWN_ISSUES.md)

## 复现

构建流程、安装策略和操作入口见 [构建](BUILD.md)、[安装与恢复](INSTALLATION.md)、[用户指南](USER_GUIDE.md) 和 [阶段计划](PHASE_6_PLAN.md)。日志时间使用 UTC。

- `python tests/P6/Test-ReleaseCli.py` 和 `python tests/P6/Test-DockP6Performance.py --soak-seconds 600` 使用自包含便携目录和新的测试档案。
- 文件系统探针可用 `dotnet run --project tests/P6/ProfileUpgrade/ProfileUpgradeProbe.csproj -c Release -- <未创建的绝对结果目录>` 重跑。
- 安装器使用 [构建脚本](../installer/Build-Installer.ps1) 的 `-Validation` 分别构建 1.0.0 和 1.0.1 变体。使用 [安装验收脚本](../installer/Test-Installer.ps1) 传入 `-BaseInstaller`、`-UpdateInstaller` 和新的 `-RunDirectory`。变体使用隔离的注册信息、配置和桌面快捷方式位置。
