# Desktop Dock

Desktop Dock 是适用于 Windows 11 x64 的本地桌面启动栏。它有独立多屏 Dock、自动隐藏、两级分组、可编辑主题和可视化热区。用户自行添加应用、文件和网址。

## 1.0.0

运行发布目录中的 `DesktopDock-1.0.0-win-x64-setup.exe`，选择安装位置与快捷方式。登录启动由用户选择。也可解压便携包后运行 `DesktopDock.exe`。程序自带 .NET 运行时。

- 从任务栏通知区域打开设置，左栏可选择要配置的屏幕。
- 向 Dock 拖入应用、快捷方式、文件或文件夹，也可在「项目管理」添加网址。
- 拖动窗口边缘或四角调整自由网格。图标可移入空格，悬停后可拖右下角手柄调整占格。
- 进入「热区与行为」查看和编辑各屏热区。主题编辑器位于托盘和「Dock 外观」。
- 桌面图标隐藏需要确认。托盘「恢复桌面图标」和 `Ctrl+Alt+Shift+F11` 用于恢复由 Dock 管理的显示状态。

[安装与卸载](docs/INSTALLATION.md) · [使用和常见问题](docs/USER_GUIDE.md) · [配置备份与恢复](docs/INSTALLATION.md#备份与恢复) · [构建发行包](docs/BUILD.md)

## 验收和版本

[最终验收](docs/PHASE_6_ACCEPTANCE.md) · [已知限制](docs/KNOWN_ISSUES.md) · [版本记录](CHANGELOG.md)

历史验收记录包含 [环境及技术验证](docs/P0_VALIDATION_REPORT.md)、[基础 Dock](docs/PHASE_1_ACCEPTANCE.md)、[系统交互](docs/PHASE_2_ACCEPTANCE.md)、[布局与分组](docs/PHASE_3_ACCEPTANCE.md)、[主题](docs/PHASE_4_ACCEPTANCE.md) 和 [多屏与桌面管理](docs/PHASE_5_ACCEPTANCE.md)。

项目设计见 [PRD](PRD.md) 和 [技术设计](TECHNICAL_DESIGN.md)。自动化使用 [应用内 MCP](docs/P1_MCP.md) 和独立配置。
