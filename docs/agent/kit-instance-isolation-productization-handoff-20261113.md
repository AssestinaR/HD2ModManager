# 按实例隔离：接入管理器（交接）

日期：2026-11-13
前置阅读：[按实例隔离共用部件：计划](kit-instance-isolation-plan-20261113.md)（含实机记录 1 至 5）

## 1. 现状：一个案例已经实机通过

| 项 | 状态 |
|---|---|
| 只读探针 `HD2RuntimeArmorOverride/src/kit_instance_probe.lua` | 0.4.0。实机能认出每个部件属于谁：玩家 avatar 或坐标簇 |
| 隐藏运行时 `HD2RuntimeArmorOverride/src/kit_instance_hider.lua` | 0.1.0。只有一条写死的规则：爱弥斯民主官 |
| 爱弥斯 Mod（库内 `mods\爱弥斯民主官`） | 已去掉 9 个全局隐藏材质，剩 36 项 |
| 实机结果 | 用户原话「现在爱弥斯正常了。所有护甲都正常」。日志每轮 `markers=1 hidden=13` |

管理器目前完全不知道这套机制。Hider 以独立的 Lua mod zip 手工导入（`.local-notes/kit-isolation/`，不进 git）。

## 2. 要做的事

用户的问题：**「如果我新加了替换神射手的护甲咋办？」**

大多数整套替换的 Mod 会改共用的身体部件或材质，同款部件的其它护甲会跟着变。本任务把爱弥斯那套手工流程变成管理器功能：

1. **发现污染。** 导入或启用 Mod 时，统计它覆盖的 Unit 与材质被多少套护甲共用，在 UI 中点名提示，例如「会影响 CE-64、CM-17 等 5 套」。数据来源是 `asset-index.sqlite` 的 `game_data_authority_pieces`。材质的引用关系需要读 Unit 的绑定，见 §4。
2. **转专属。** 把模型挂到目标护甲**独有**的部件上（标记部件），同时生成一条隐藏规则：标记部件加上要在该实例上隐藏的共用部件。
3. **规则外置。** Hider 改为读取管理器写出的规则文件，不再写死规则。沿用 `wardrobe.links` 的做法：放在 `ArmorRegistry` 目录、原子替换、带 revision、解析失败时保留上一版。
4. **部署。** Hider 随管理器投放，与现有护甲运行时的投放方式（独占或外置入口）对齐，不再手工导入 zip。

按 AGENTS.md，先在 `docs/agent/` 写计划并请用户确认，再动手。

## 3. 已证实的事实

- **允许的 API。** 只能用 `C:\Users\q1243\Downloads\hd2pbx_api.txt` 里的 `Application`、`World`、`Unit`、`Vector3`、`Quaternion`、`Matrix4x4`。`IdString64` 不在清单里，不要调用。
- **资源 ID。** 用 `tostring(Unit.resource_name(unit))`，结果形如 `[IdString64 "#ID[4d1c334d294dfa97]" 0x4d1c...]`。`Unit.name_hash` 是另一套哈希，对不上资源 ID。
- **归属。** 同一角色的部件，`Unit.world_position(unit, 1)` 与该角色重合，距离 0.00 米。玩家用 `World.units_by_resource(world, 'content/fac_helldivers/cha_avatar/avatar_helldiver')` 找到。飞船上的民主官不是玩家 avatar，可以按坐标簇认出来。
- **成本。** 每帧 300 个 unit 约 0.2 至 0.3 ms，飞船上一共 11 个 world、约 2300 个 unit。
- **隐藏。** `Unit.set_unit_visibility(unit, false)` 在实机生效，只作用于这一个实例。
- **日志。** MDL3 加载器的 `open_log` 写满约 64 KB 后会截掉开头。Hider 自限 48 KB，但它现在随 `left_visible` 变化频繁写入，应改为只在 `markers` 或 `hidden` 变化时写。
- **FS-05 的三条记录。** `124623533` 是民主官 NPC，标记部件 `82595b5d`。`3970674111` 是玩家穿的神射手，左肩 `90b57e54`。`1490803405` 有没有人穿未知，它和 `3970674111` 共用左肩 `90b57e54`，所以拿玩家神射手的左肩当标记前要先查清楚。
- **覆盖面。** 138 套身体护甲中有 107 套至少有一个独有部件，另外 31 套没有，无法转专属，只能提示。

## 4. 工具与复现

- 导出原版 Unit：`HD2ModCli\bin\Release\net10.0\hd2mod.exe gamedata extract asset e0a48d0be9a7453f <unit hex> <归档 ID> --game-data "E:\SteamLibrary\steamapps\common\Helldivers 2\data" --output <目录> --overwrite --json`。归档 ID 从 `asset_archives` 表查（`type_id`、`file_id` 按有符号 64 位存储）。
- Unit 绑定的材质：TOC `0x70` 处是偏移，指向 `u32 count`，之后依次是 `count` 个 u32 section ID 和 `count` 个 u64 材质 ID（`UnitMaterialReferenceReader`）。
- Lua 测试：本机没有 luajit.exe。先运行 `python -m pip install --target <临时目录>/pylib lupa`，再运行 `python -I HD2RuntimeArmorOverride/scripts/run_lua_test.py <pylib> <测试> <源码>`。`scripts/Test.ps1` 已登记这两个新测试。
- 打包 Lua mod：`python HD2RuntimeArmorOverride/scripts/build_lua_mod.py --lua <源码> --resource mods/hd2modmanager/<名字> --out <zip> --guid <guid> --name <名字>`。
- `dotnet test` 中有 5 个 `DeploymentCliTests` 测试会在游戏运行时失败（`CliDeploymentProcessGuard` 检查 `helldivers2` 进程）。跑测试前先关闭游戏。

## 5. 验收（产品化之后）

| 操作 | 预期 |
|---|---|
| 新导入一个改共用身体部件的神射手 Mod | 导入或启用时点名提示受影响的护甲 |
| 对它执行「转专属」并部署 | 穿神射手显示新模型；CE-64、CM-17、FS-11、FS-61、B-24 身体完整 |
| 爱弥斯民主官改用规则文件 | 与现在的手工版效果相同 |
| 停用该 Mod | 规则随之移除；重进飞船或打开军械库后恢复原样 |
