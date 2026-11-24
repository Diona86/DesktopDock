# Desktop Dock 技术设计与架构方案

-   文档版本：1.0
-   对应 PRD：1.0
-   技术基线：Windows 11 / C# / WPF
-   状态：建议架构；P0 验证后冻结关键实现

## 1. 架构目标

1.  Dock 展示、系统集成、设置管理职责分离。
2.  Windows API 调用集中封装，避免业务逻辑散落在 UI。
3.  多显示器、多个 Dock、热区、主题覆盖具备可扩展数据模型。
4.  核心功能可独立测试，系统状态变化可诊断。
5.  先验证高风险 Win32 能力，再投入完整 UI 开发。

## 2. 建议解决方案结构

建议采用一个 Visual Studio Solution：

``` text
DesktopDock.sln
src/
  DesktopDock.App/                 # WPF 启动入口、生命周期、DI、托盘
  DesktopDock.Core/                # 实体、枚举、业务规则、接口
  DesktopDock.Application/         # 用例、服务编排、DTO、验证
  DesktopDock.Infrastructure/      # 配置存储、图标提取、日志、文件系统
  DesktopDock.Windows/             # Win32、DWM、Shell、窗口检测、热区
  DesktopDock.DockHost/             # Dock WPF 窗口与交互展示
  DesktopDock.Settings/             # 设置窗口、页面导航、编辑器
  DesktopDock.ThemeEngine/          # 主题解析、校验、应用、预览
tests/
  DesktopDock.Core.Tests/
  DesktopDock.Application.Tests/
  DesktopDock.Infrastructure.Tests/
  DesktopDock.Windows.Tests/
  DesktopDock.Integration.Tests/
docs/
  PRD.md
  TECHNICAL_DESIGN.md
  CODEX_MASTER_PROMPT.md
```

可根据实际工程规模合并项目，但必须保留清晰模块边界。避免为了分层而过度拆分，也避免将所有代码塞进单一
WPF 项目。

## 3. 进程与窗口模型

### 3.1 第一版建议

优先采用单进程多窗口，减少进程间通信和部署复杂度： -
后台应用生命周期管理器。 - 一个或多个 Dock WPF 窗口。 - 独立设置窗口。 -
托盘图标。

Dock 窗口关闭行为应与应用退出区分。关闭设置窗口不退出后台应用。

如果 P0/P1 发现 WPF UI
线程、全屏窗口层级或系统钩子稳定性需要隔离，再评估将 Dock Host
拆为独立进程。不要未经验证就一开始引入复杂 IPC。

### 3.2 Dock 窗口

WPF 无边框透明窗口，按需设置置顶、非激活显示等窗口行为。必须验证： -
`AllowsTransparency`、透明背景和渲染性能。 -
`WindowStyle=None`、任务栏显示策略。 - 点击穿透与可交互区域控制。 - Dock
呼出时是否抢焦点。 - 全屏应用覆盖关系。 - 多屏 DPI 坐标换算。 -
窗口隐藏/显示动画期间输入命中是否正确。

不应简单地让 Dock 永远
TopMost。置顶策略需要结合显示模式、全屏状态和交互状态动态管理。

## 4. 模块职责

### 4.1 Core

纯业务模型和规则，不依赖 WPF 或 Win32： -
DockInstance、DockItem、DockGroup。 - HotZone、ShortcutBinding。 -
Theme、ThemeOverride。 - AppBehaviorRule。 -
项目类型、布局类型、展开类型、运行模式枚举。 -
两级分组约束、项目引用安全规则。

### 4.2 Application

用例编排： - AddDockItem。 - RemoveDockItemReference。 -
ReorderDockItems。 - CreateGroup / MoveItemToGroup。 -
UpdateDockLayout。 - ConfigureHotZone。 - RegisterShortcut。 -
ApplyTheme / PreviewTheme。 - HideDesktopIcons / RestoreDesktopIcons。 -
DetectInvalidTargets。 - ExportDiagnostics。

所有写操作应经过应用层服务，避免 UI 直接改配置对象。

### 4.3 Windows Integration

集中封装： - 全局快捷键注册与注销。 - 显示器枚举与 DPI。 -
前台窗口和窗口矩形检测。 - 最大化窗口识别。 - 全屏候选识别。 -
Shell/桌面图标显示控制。 - Explorer 生命周期变化监测。 -
系统托盘与开机启动集成。

建议定义接口，业务层依赖抽象： - `IHotKeyService` - `IMonitorService` -
`IWindowStateService` - `IDesktopIconVisibilityService` -
`IExplorerStateMonitor` - `IStartupRegistrationService`

### 4.4 Dock Host

负责： - 图标列表渲染。 - Tooltip。 - 点击启动。 - 拖拽排序和添加。 -
分组展开面板。 - 移动手柄。 - 动画和命中测试。 -
常驻/自动隐藏状态视觉表现。

不要在控件事件里直接调用复杂 Win32 逻辑。

### 4.5 Theme Engine

-   解析版本化主题文件。
-   校验允许字段与范围。
-   加载受控资源。
-   将主题参数映射到 WPF ResourceDictionary/样式。
-   支持临时预览、撤销、保存、另存为。
-   合并全局主题和 Dock 级覆盖参数。

主题包不得执行代码，不得动态加载任意程序集或脚本。

## 5. 关键技术专题

### 5.1 多显示器与 DPI

使用 Windows 显示器 API 枚举显示器，保存稳定显示器标识和工作区信息。WPF
DIP 与 Win32 物理像素坐标转换必须集中处理。

测试： - 100%、125%、150%、200% 缩放。 - 主副屏不同缩放。 -
屏幕左右/上下排列。 - 主显示器切换。 - 分辨率变化。 - 显示器拔插。 -
Dock 跨屏移动。

不要将 `Screen.AllScreens` 的数组序号当作永久显示器 ID。

### 5.2 热区

建议采用事件驱动为主、低频补偿为辅： -
监听鼠标位置或使用合适的低级输入机制。 -
只在鼠标接近已配置热区时进行精细命中判断。 -
热区定义使用显示器物理坐标或明确的规范坐标，统一转换。 -
多热区冲突要有确定优先级。 - 支持触发延迟和收起延迟，避免边缘抖动。 -
暂停热区时仍允许快捷键操作。

避免高频全屏轮询造成 CPU
占用或与游戏输入冲突。全局鼠标钩子需要评估安全、权限、性能和稳定性；若采用必须确保及时释放和异常清理。

### 5.3 窗口最大化与全屏检测

使用多信号综合判断： - 前台窗口句柄和进程身份。 -
窗口矩形与所在显示器边界/工作区。 - 窗口样式和扩展样式。 - DWM
状态（适用时）。 - 最大化状态。 -
窗口是否覆盖显示器、是否处于无边框模式。 - 应用例外规则。

注意： - 覆盖整个屏幕不等于独占全屏。 -
无边框全屏和独占全屏的行为可能不同。 - 某些游戏使用特殊呈现路径，通用
API 不一定能准确识别。 - 不能仅凭进程名或窗口标题作为唯一身份。 -
不确定时采用保守策略，避免自动弹出干扰游戏。 -
用户例外规则必须可查看、编辑、禁用。

### 5.4 桌面图标显示控制

这是高风险系统集成点。桌面图标通常由 Explorer Shell
相关窗口承载，控制方式可能涉及 Progman/WorkerW/SysListView32
等窗口层级，但不同 Windows 版本、Explorer 状态和桌面配置可能变化。

实施要求： 1. P0 做独立原型，先只读识别桌面窗口结构。 2.
验证隐藏和恢复是否只影响显示，不改变文件。 3. 验证 Explorer 重启后状态。
4. 验证多显示器桌面。 5. 记录修改前状态与本软件是否实施了修改。 6.
避免无条件恢复覆盖用户运行期间的手动操作。 7. 不把未经测试的 API
调用包装成"绝对可靠恢复"。

若可靠性不足，应提供明确的手动恢复入口和启动时恢复提示，而不是静默执行可能破坏用户设置的操作。

### 5.5 图标提取与缓存

-   `.lnk`：解析快捷方式目标及图标位置。
-   `.exe`：提取关联图标。
-   文件：优先 Shell 图标接口，必要时使用关联程序图标。
-   文件夹：使用系统文件夹图标或用户覆盖图标。
-   URL：使用通用网页图标或用户指定图标；不强制联网抓取 favicon。
-   缓存键考虑路径、文件修改时间、图标索引和缓存版本。
-   文件路径变化时失效缓存。
-   支持 ICO/PNG 等受控格式。
-   图标加载应异步，避免阻塞 UI。

### 5.6 快捷键

封装 `RegisterHotKey` / `UnregisterHotKey` 或经验证的等价机制： -
管理注册生命周期。 - 检测重复绑定。 - 处理系统注册失败。 -
窗口关闭、应用退出和异常清理时注销。 - 不允许 UI
显示"已启用"但底层注册失败。

### 5.7 启动项目

-   `.exe` 使用受控进程启动流程。
-   `.lnk` 应保留快捷方式语义，必要时通过 Shell 执行。
-   普通文件和文件夹使用系统关联打开。
-   URL 使用系统默认浏览器。
-   启动参数和工作目录需进行正确转义和验证。
-   管理员运行属于高级选项，必须由用户明确设置并解释 UAC 行为。
-   不从主题文件或不可信配置执行任意命令。

## 6. 配置与数据持久化

### 6.1 初期存储

建议第一阶段使用版本化 JSON
配置，便于调试、备份和迁移。若事务、查询或并发需求增长，再评估 SQLite。

建议目录：

``` text
%LocalAppData%/DesktopDock/
  config/
    settings.json
    docks.json
    items.json
    groups.json
    shortcuts.json
    hotzones.json
  themes/
    builtin/
    user/
  assets/
    icons/
  cache/
  logs/
  backups/
```

具体拆分可根据数据一致性决定。写入必须采用临时文件 +
原子替换或等效安全策略，避免崩溃造成配置半写入。

### 6.2 配置版本

-   每份持久化数据包含 schemaVersion。
-   启动时执行迁移。
-   迁移前自动备份。
-   不支持的未来版本应安全拒绝写入，不能覆盖用户数据。
-   导入配置前校验格式、字段和路径。

### 6.3 建议数据结构

``` json
{
  "schemaVersion": 1,
  "docks": [
    {
      "id": "stable-guid",
      "monitorId": "stable-monitor-id",
      "placement": "Bottom",
      "mode": "AutoHide",
      "layout": "SingleRow",
      "position": { "x": 0, "y": 0 },
      "locked": false,
      "themeId": "theme-default",
      "themeOverrides": {}
    }
  ]
}
```

以上仅为结构示意，正式模型应使用强类型 DTO
并进行校验。不要直接把未经验证的 JSON 反序列化结果用于系统操作。

## 7. 主题包规范建议

主题文件采用版本化 ZIP 或 JSON + 资源包。建议： - manifest：主题
ID、名称、作者字段（可选）、版本、最低应用版本、主题 schema 版本。 -
tokens：颜色、透明度、尺寸、圆角、阴影、动画等声明式参数。 -
assets：受限的图像资源。 -
不包含脚本、DLL、EXE、PowerShell、批处理或动态程序集。 -
解压时防止路径穿越。 -
限制压缩包大小、解压后总大小、单文件大小和资源数量。 -
校验资源类型和图片尺寸。 - 导入先预览，用户确认后应用。 -
导入失败不得覆盖当前主题。

## 8. 日志与诊断

-   使用结构化日志。
-   日志级别：Trace/Debug/Information/Warning/Error。
-   记录热区触发、窗口状态决策、快捷键注册结果、显示器变化、配置迁移、桌面图标状态变化。
-   不记录文件内容。
-   对路径和进程信息提供隐私保护选项。
-   提供导出诊断包功能，导出前提示包含的内容。
-   对系统 API 失败记录错误码和上下文。

## 9. 测试策略

### 单元测试

-   两级分组约束。
-   项目排序。
-   主题覆盖合并。
-   热区几何命中。
-   快捷键冲突逻辑。
-   配置迁移与校验。

### 集成测试

-   `.lnk` 解析。
-   图标提取。
-   启动文件/文件夹/网址。
-   Win32 快捷键注册。
-   多显示器枚举。
-   热区触发。
-   窗口状态检测。
-   桌面图标显示与恢复。

### 手工验收

-   Windows 11 不同 DPI。
-   多显示器拔插。
-   最大化办公窗口。
-   无边框全屏游戏。
-   独占全屏游戏（若测试环境支持）。
-   视频全屏。
-   Explorer 重启。
-   软件异常结束。
-   快捷键冲突。
-   路径失效。
-   主题包异常输入。

## 10. 风险登记

  风险                          影响   缓解策略
  ----------------------------- ------ --------------------------------
  Explorer 桌面图标控制不稳定   高     P0 原型验证；保留手动恢复
  全屏检测不完全准确            高     多信号检测、保守策略、应用例外
  多屏 DPI 坐标错位             高     统一坐标服务、矩阵测试
  WPF 透明窗口性能              中高   原型基准测试、避免过度模糊
  热区误触发                    中     延迟、区域可视化、暂停开关
  快捷键冲突                    中     注册结果检测、编辑器提示
  配置损坏                      高     原子写入、备份、schema 迁移
  主题包恶意输入                高     声明式格式、路径与资源校验

## 11. P0 技术验证清单

P0 不追求完整产品 UI，只建立独立可运行原型并记录真实结果：

1.  WPF 透明无边框 Dock 窗口。
2.  不抢焦点的显示/隐藏行为。
3.  多显示器与混合 DPI 坐标。
4.  热区命中、延迟与多热区。
5.  最大化窗口识别。
6.  无边框全屏候选识别。
7.  桌面图标窗口结构识别、隐藏与恢复。
8.  Explorer 重启后的状态行为。
9.  全局快捷键注册、冲突和注销。
10. 空闲 CPU 与内存基准。

每项记录：Windows
版本、测试硬件、步骤、预期、实际结果、日志、结论、已知限制。未实际测试的项目标记为"未验证"，不得标记通过。

## 12. 关键架构决策门槛

P0 结束时必须召开一次技术决策检查： -
桌面图标控制是否足够可靠？若不可靠，是否缩小承诺范围？ - Dock
是否能在目标场景稳定显示且不抢焦点？ - 全屏检测的可靠边界是什么？ -
多显示器坐标方案是否正确？ - 是否需要多进程隔离？ - 最终 .NET
SDK/运行时版本是什么？

只有这些问题得到有证据的结论后，才冻结 P1 实现方案。
