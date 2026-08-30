# HD2 Mod Manager CLI

面向脚本与 AI Agent 的本地入口；以 `hd2mod capabilities --json` 为当前功能准则。

```powershell
hd2mod agent skills list --json
hd2mod agent skills export --output .\.agents\skills
hd2mod agent skills export safe-import --output .\.agents\skills
```

可安装能力只有：`library-inspection`（只读库检查）和 `safe-import`（`inspect → plan → validate → apply --confirm`）。目录导出会写入独立 Skill 目录和 SHA-256 清单；不删除同级的其它用户 Skill。刷新同一输出目录请显式加 `--overwrite`。

单文件兼容：`hd2mod agent skill export safe-import --output SKILL.md`；旧命令 `hd2mod agent skill --output SKILL.md` 输出轻量路由器。
