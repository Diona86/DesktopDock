# Desktop Dock 项目文档包

## Windows 11 多屏与桌面管理

运行 `artifacts/DesktopDock-P5-win-x64/DesktopDock.exe` 后，可在设置左侧切换显示器。托盘提供所有 Dock 的显示、隐藏、热区暂停、桌面图标恢复和退出。

- 「热区与行为」可打开可视化热区地图，按屏拖动边界并保存。
- 「通用设置」可管理登录启动和桌面图标。隐藏桌面图标需要在对话框确认。手动恢复通过桌面右键「查看 → 显示桌面图标」。
- 删除便携版前运行 `DesktopDock.exe --uninstall-cleanup`，可清理此版本的登录启动项。

功能路径、验证范围和限制见 [P5 验收](docs/PHASE_5_ACCEPTANCE.md)。

## Windows 11 主题编辑器

退出正在使用同一配置的旧版 Dock，再运行 `artifacts/DesktopDock-P4-win-x64/DesktopDock.exe`。在托盘菜单或「设置 → Dock 外观」打开「主题编辑器」。

- 选择「全局」或「当前 Dock」，再选择主题和参数。中间预览和可见的 Dock 会随修改更新。
- 「保存」提交修改，「另存为」创建独立主题，「取消」恢复上次保存的设置。内置主题保留原版。
- 独立主题文件使用 `.docktheme`，包含全部参数和声明式纹理。导入可先预览，保存后加入主题库。

发布和复测步骤见 [P4 验收](docs/PHASE_4_ACCEPTANCE.md) 和 [P4 计划](docs/PHASE_4_PLAN.md)。

## 项目文档

本目录包含：
- PRD.md：产品需求文档
- TECHNICAL_DESIGN.md：技术设计与架构方案
- CODEX_MASTER_PROMPT.md：交给 Codex 的开发总提示词

建议使用顺序：
1. 阅读 PRD，确认产品范围。
2. 阅读技术设计，重点关注 P0 风险。
3. 将三份文件放入项目仓库的 docs/ 目录（或按团队习惯调整路径）。
4. 将 CODEX_MASTER_PROMPT.md 的内容交给 Codex。
5. Codex 必须先检查仓库并完成 P0 技术验证，不应直接开发完整产品。

注意：技术设计中的 Windows API 方案属于待验证设计，不代表所有行为已在目标设备上验证。
