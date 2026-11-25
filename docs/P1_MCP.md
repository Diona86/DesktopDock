# Desktop Dock 本地 MCP 测试接口

MCP 用于驱动独立测试配置和读取应用自己的 WPF 窗口。客户端启动 `DesktopDock.exe` 子进程，重定向标准输入和输出。进程使用一行一条的 UTF-8 JSON-RPC 消息，支持 MCP `2025-11-25` 的握手与工具调用。标准输入关闭后应用退出。 [MCP stdio 规范](https://modelcontextprotocol.io/specification/2025-11-25/basic/transports)

## 接入

按 MCP 客户端的配置格式填写可执行文件和参数。例如，使用 `mcpServers` 配置的客户端可以填写：

```json
{
  "mcpServers": {
    "desktop-dock-p1": {
      "command": "D:\\myapp\\desk-design\\artifacts\\DesktopDock-P1-win-x64\\DesktopDock.exe",
      "args": ["--mcp-stdio", "--config-dir", "D:\\myapp\\desk-design\\artifacts\\mcp-profile"]
    }
  }
}
```

应用要求指定独立配置目录。会话与该目录中的 `dock.json` 和 `dock.lock` 关联。请为并行客户端分别选目录。MCP 模式通过子进程的标准流通信，启动时跳过系统托盘入口。只给能控制子进程的受信任本地客户端使用。`dock_open` 和 `dock_show_in_folder` 会调用 Windows Shell，可能打开所选应用、资源管理器或默认浏览器。

## 工具

| 操作 | 工具 |
| --- | --- |
| 查看项目、配置与实际控件状态 | `dock_state` |
| 添加、编辑、替换图标、移除引用 | `dock_add`、`dock_edit`、`dock_remove` |
| 打开目标与所在目录 | `dock_open`、`dock_show_in_folder` |
| 设置、显隐和设置窗口生命周期 | `dock_options`、`dock_visibility`、`dock_settings` |
| 检查通用设置中的更新状态 | `dock_update_view`，`check: true` 发起同一个 GitHub 查询 |
| 验证添加应用 | `dock_add` 的 `input: "application"`，或 `dock_add_explorer` 添加文件资源管理器 |
| 验证排序、移动手柄和 Tooltip | `dock_drag_item`、`dock_drag_handle`、`dock_tooltip` |
| 获取 Dock 或设置窗口的实际渲染 | `dock_capture` |

通过 `tools/list` 获取参数结构。用于拖拽的调用在应用内验证命中、反馈和提交，Tooltip 调用在应用内打开真实 WPF 弹窗。系统文件选择器、物理指针、Explorer 发起的 OLE 拖放和任务栏通知区域属于独立验证场景。

Windows 11 上可用下列命令执行集成用例。脚本创建独立素材和配置，启动默认程序处理真实 Shell 请求，并保存测试回执与 PNG。

```powershell
pwsh -NoProfile -File .\tests\P1\Test-DockMcp.ps1 -Executable .\artifacts\DesktopDock-P1-win-x64\DesktopDock.exe
```
