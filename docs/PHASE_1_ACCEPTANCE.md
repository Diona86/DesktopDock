# Desktop Dock 阶段 1 验收

2026-11-24 在 Windows 11 25H2 x64（26200.9974）运行基础 Dock。正式项目为 `net10.0-windows`，SDK 10.0.404。发布产物包含 .NET 与 Windows Desktop 10.0.15。解决方案 Release 构建为 0 警告、0 错误，Windows x64 自包含发布成功。 [构建日志](p1-evidence/solution-build.log) · [发布日志](p1-evidence/build.log)

完整 MCP 集成回归退出码为 0，13 个命名检查通过。项目和设置使用独立测试配置。真实系统启动回执、WPF 控件状态、PNG 渲染结果及原始文件哈希分别记录。 [完整运行日志](p1-evidence/mcp-results.log) · [核心检查](p1-evidence/core.log) · [图标检查](p1-evidence/icons.log)

## 结果

| 编号 | 判定 | 实测证据与边界 |
| --- | --- | --- |
| P1-01 EXE | 通过 | 显式添加带空格的临时 EXE，通过正式 Dock 打开入口启动。程序生成 `exe-receipt.txt`。 |
| P1-02 LNK | 通过 | 真实 `.lnk` 通过 Shell 启动目标。回执确认快捷方式参数和中文目录工作路径保持。 |
| P1-03 文件与文件夹 | 通过 | 经正式文件拖入处理逻辑加入文本文件和中文文件夹。打开文件、文件夹和文件所在位置的 Shell 请求成功返回。Explorer OLE 拖入及外部窗口内容另待验证。 |
| P1-04 URL | 通过 | 保存 HTTP 和 HTTPS，拒绝 `file:`。Windows 默认浏览器打开本地 HTTP 目标，监听器收到 `GET /p1-dock` 和 Chrome 用户代理并响应 200。HTTPS 执行了添加和校验。 |
| P1-05 图标 | 通过 | EXE、LNK、文本文件及文件夹取得冻结的原生位图，网址显示内置矢量图标。实际 Dock 渲染中显示四个 40 × 40 位图，其中包括用户替换的 PNG。独立组件检查覆盖缓存复用、变更刷新和缺失图标回退。 |
| P1-06 Tooltip | 通过 | 应用内打开真实 WPF Tooltip，名称正确，弹窗附着且尺寸为 92 × 25。验证关闭成功。物理鼠标悬停与触发时延待补测。 |
| P1-07 图标排序 | 通过 | MCP 使用真实 Dock 控件坐标和正式插入、反馈和提交处理。反馈可见，项目与按钮顺序改变，原生窗口位置保持。物理拖动和 OLE 事件路由待补测。 |
| P1-08 移动与锁定 | 通过 | 从原生 HWND 读取四边停靠位置。悬浮手柄验证在背景区现身、原生位置从 `(180,200)` 变为 `(228,232)` 且项目顺序保持。全局锁和当前 Dock 锁分别阻止移动。实鼠捕获、拖动及释放待补测。 |
| P1-09 配置恢复 | 通过 | 客户端关闭 stdin 后进程正常退出，再启动另一个进程。顺序、浮动位置、图标尺寸与 Dock 尺寸、名称及自定义图标恢复，`schemaVersion=1`。 |
| P1-10 移除引用 | 通过 | 应用移除测试项目后，目标 EXE、LNK、文本文件和覆盖图标的 SHA-256 均一致，测试文件夹仍存在。 |
| P1-11 设置与配置安全 | 通过 | 实际渲染外观、项目、位置和通用四页。关闭设置窗口后 Dock 仍可用，显隐状态切换成功。拒绝无效路径、非 HTTP/HTTPS 网址、损坏与未来版本配置。Windows 文件共享冲突测试确认失败时磁盘与内存保持原配置，解除冲突后可保存。 [启动保护日志](p1-evidence/profile-guard.log) |

表中的通过针对所列测试路径。正常模式曾实际打开系统文件选择器。选择器完成确认后的返回、Explorer 发起的拖入、硬件指针的悬停与拖动、通知区域点击和外部关联程序的窗口内容没有完成端到端验收。

## 运行与复测

- 本机发布目录为 `artifacts/DesktopDock-P1-win-x64`。运行同目录中的 `DesktopDock.exe`，从 Dock 设置或通知区域的「设置」进入项目管理。正常配置位于 `%LocalAppData%\DesktopDock\config\dock.json`。
- 使用 .NET 10 SDK 可运行 `dotnet build DesktopDock.slnx -c Release`。发布可用 `dotnet publish src/DesktopDock/DesktopDock.csproj -c Release -r win-x64 --self-contained true -o artifacts/DesktopDock-P1-win-x64`。
- MCP 测试使用标准输入和输出，显式提供独立配置目录。测试脚本在 `artifacts` 中保留回执和 Dock、设置窗口 PNG。 [MCP 接入与复测命令](P1_MCP.md)

## 实现边界与回归修复

- P1 使用主屏工作区，浮动坐标以工作区相对 DIP 保存。常驻窗口使用普通 Z 序。多屏配置和自动隐藏属于后续阶段。
- 集成测试初次发现图标布局触发反复重建，较大的 `dock_state` 请求超时。WPF `Image.DpiChanged` 会在图标首次测量时冒泡。修复后刷新只响应真实窗口 DPI 回调，并合并到当前布局完成之后。完整回归的三次添加请求耗时 59–75 ms。 [WPF Image 源码](https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Controls/Image.cs) · [完整运行日志](p1-evidence/mcp-results.log)
- 已检查实际 WPF PNG。最终样式为长目标列提供省略和完整路径提示，统一项目列表文字颜色。最终回归复验了应用和渲染，浏览器与文件关联的回执取自完整回归。 [最终复跑日志](p1-evidence/mcp-final.log)

P1 已交付，P2 等待确认。 [阶段计划](PHASE_1_PLAN.md) · [项目 TODO](../TODO.md)
