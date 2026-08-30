# HD2 Mod Manager CLI

`hd2mod.exe` 与管理器一同发布，供脚本和 Agent 使用。先让程序报告当前能力，再只导出这次任务所需的 Skill：

```powershell
.\hd2mod.exe capabilities --json
.\hd2mod.exe agent skills list --json
.\hd2mod.exe agent skills export safe-import --output .\.agents\skills
```

`agent skills export --output <目录>` 可一次导出全部能力；`agent skill --output SKILL.md` 导出轻量路由器。命令自动化时始终传 `--json`，并以 `capabilities --json` 与 `--help` 为当前版本的唯一命令表。

各能力的精确命令、计划审阅方式和安全边界见 [SKILL.md](SKILL.md) 及 [skills](skills)。没有 `--dry-run`：有计划的流程使用 `inspect`/`plan`、`validate` 和 `apply --confirm`；其他库内写入也要求 `--confirm`。独立 Patch 输出不会自动导入或部署。
