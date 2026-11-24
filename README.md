# Desktop Dock 项目文档包

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
