# HD2 Mod Manager CLI

`hd2mod.exe` 与管理器一同发布，供脚本和 Agent 使用。先让程序报告当前能力，再只导出这次任务所需的 Skill：

```powershell
.\hd2mod.exe capabilities --json
.\hd2mod.exe agent skills list --json
.\hd2mod.exe agent skills export safe-import --output .\.agents\skills
```

`agent skills export --output <目录>` 可一次导出全部能力；`agent skill --output SKILL.md` 导出轻量路由器。命令自动化时始终传 `--json`，并以 `capabilities --json` 与 `--help` 为当前版本的唯一命令表。

各能力的精确命令、计划审阅方式和安全边界见 [SKILL.md](SKILL.md) 及 [skills](skills)。没有 `--dry-run`：有计划的流程使用 `inspect`/`plan`、`validate` 和 `apply --confirm`；其他库内写入也要求 `--confirm`。独立 Patch 输出不会自动导入或部署。

## Patch 底座原语（文件进、文件出）

除面向完整业务流程的命令外，CLI 还提供一组**无状态底座命令**：作用于任意 Patch 文件路径（不要求已登记进库），不取库租约、不走四段式，只写你显式给出的 `--output` 新目录。它们供外部脚本 / Agent 自由组装 GUI 无法直接表达的流程：

```powershell
hd2mod patch entries list "<patch_0>" --json                    # 枚举任意 Patch 的条目
hd2mod patch entry export "<patch_0>" 0x<type> 0x<file> --output "<dir>" --json   # 拆成三段裸文件
hd2mod patch compose --header "<tmpl>" --pick "<src>" 0x<type> 0x<file> --output "<dir>" --json
hd2mod patch edit "<src>" --remove-key 0x<type>:0x<file> --output "<dir>" --json
hd2mod patch hide-unit "<src>" --keys "@keys.json" --output "<dir>" --json
hd2mod gamedata equipment list --game-data "<data>" --kind cape --json
```

`--keys` 接受内联 `0xtype:0xfile,...`、`@file.json` 或内联 JSON。典型组合见 [skills/patch-primitives](skills/patch-primitives) 与 [skills/equipment-facts](skills/equipment-facts)。

## Unit 数据视图（只读网格解析）

同一底座上还有一组**只读**命令，直接解析任意 Patch 内某个 Unit 的网格结构，不要求登记进库，也不写任何文件（拒绝 `--output` / `--plan` / `--confirm`）：

```powershell
hd2mod unit show     "<patch_0>" --keys 0xe0a48d0be9a7453f:0x<file> --json   # 版本/骨骼组/流/顶点分量/网格
hd2mod unit geometry "<patch_0>" --keys 0xe0a48d0be9a7453f:0x<file> --json   # 逐网格顶点数、三角形数、几何质量
hd2mod unit bones    "<patch_0>" --keys 0xe0a48d0be9a7453f:0x<file> --json   # 骨骼组真实索引与骨骼名表
```

`unit geometry` 会把每个网格归入 `Unreadable` / `Placeholder` / `CullingOnly` / `Renderable` / `RenderableLod0`，可用来区分"故意隐藏的占位 Unit"与"仍带可见模型的 Unit"。典型组合见 [skills/unit-inspection](skills/unit-inspection)。

