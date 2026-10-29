# 部位索引重构：注册表已经是权威，剩下的都是几何

2026-10-29。本文记录「哪些流程因为注册表能直接给出 unit 部位而过时」的结论、实测数据、
以及已做/未做的边界。以后要动这条链路，先读这里，不要重新推一遍。

## 1. 一句话结论

注册表（`generated_customization_armor_sets` → `game_data_authority_kits/pieces`，以及
自定义素体工作区里的 `models[].pieces[]`）能给出的是 **unit 级身份**：
`(itemId, body, slot, piece_type) → 一个 unit`。它给不了 **mesh 级事实**：
一个 unit 里有几个 mesh、哪个是可见 LOD0、多少顶点、stream 组件布局。

所以过时的不是「重算部位」，而是 **逐 mesh 猜身份** 和 **逐 mesh 存身份**。

## 2. 实测底账（本机 `asset-index.sqlite`，schema v10）

| 项 | 值 |
| --- | --- |
| `game_data_authority_kits` / `_pieces` | 422 / 3527（2006 个 unit，含 1 个全零占位） |
| `game_data_unit_parts` | 16569 行 / 2003 个 unit |
| `fact_origin = AuthoritativeGameData` | 16569 / 16569 |
| `confidence = 100` | 16569 / 16569 |
| `authority_slot >= 0` | 16569 / 16569（一行「未定位」都没有） |
| `part_kind` 取值 | 只有 1..9，与 `ToPartKind(authority_slot)` 逐对完全对齐，无 Unknown |
| 一个 unit 跨多个 slot / 多个 piece_type | 0 / 0 |
| 一个 unit 存在于多个 archive | 429 个 |
| `game_data_stream_layouts` | **0 行** |

推论：`UnitMeshPartClassifier` 在部位索引里的那一次调用，产出的 kind/body **每一行都被
注册表覆盖**（因为一个 unit 只有一个 slot，`partKinds.Length` 恒为 1，`PartKind` 永不落空）。
它在这里的信息量是 0，只剩「遍历 mesh」。

## 3. 已做（五个提交）

1. **`d2ccd02` 基础索引重建也保住 stream 布局。** 布局表只有高级装备索引会写，而
   `ClearIndexDataAsync` 会删掉它、基础索引又是 `IncludeStreamLayouts: false` 根本不写。
   结果 `HasCompleteEquipmentFactsAsync` 永远查不到布局行 → `IsCurrentAsync` 恒为假 →
   用户点「补齐缺失」其实每次都是全量重算。本机布局表 0 行就是这么来的。
   preserve/restore 现在两张表一起搬。
2. **`c6953b1` 删除无调用方的 `BuildOrRefreshAsync`。** 它自己再跑一遍
   `GameDataArchiveIndexer` 去拿 stream 布局，等于把基础索引的活干第二遍；GUI 与 CLI
   都只走 `RebuildAll`/`BuildMissing`。连带删掉 `SaveAsync` / `ClearEquipmentFactsAsync` /
   `ReadAnalyzedUnitsAsync` 与 `IGameDataArchiveIndexer` 依赖。
3. **`9c607ba` unit 集合改由注册表给出。** 以前按 `archives.category LIKE armor/helmet`
   扫 `archive_entries`。注册表里 2006 个 unit 有 2 个（披风槽）被护甲套引用、实体却存在
   一个 **Weapon** 分类的 archive（`18235e0c9ec0e636`）里，于是永远进不了部位表 ——
   任何护甲的披风槽都没有部件事实，而且连「未定位」都不显示，是彻底不存在。
   现在：集合 = 注册表 `kit_kind 0/1` 的 unit ∩ `archive_entries`；archive 只决定从哪个
   文件解析。同一 unit 有多份副本时（429 个）优先装备分类的那一份。枚举 3233 → 3235，
   新增的正好是那 2 个披风 unit，没有丢任何一条。完整性判据同时并进同一个查询。
4. **`6b10c68` 读取收成一处。** 同一张表以前有两个读法：目录读
   `authority_slot/authority_piece_type`，`GetUnitPartFactsAsync` 根本不读那两列，
   于是同一份数据在两处给出不同的 `ArmorCell`。现在两条路径共用 `UnitPartFactQuery`
   （列清单 + 行映射 + 格子来源），格子由读取时 JOIN 注册表得出。
   坐标合法性收进 `ArmorCell.FromRegistryCoordinates`。
5. **`dd389d6` 退役那两列。** 写入端不再存、preserve/restore 不再搬、`SchemaVersion` 11
   把旧库里的列真的 `DROP COLUMN` 掉。`AuthorityUnitRole.Cell` 随之删除。

## 4. 没做，以及为什么

### 4.1 删 Legacy 规划器 —— **已完成（`6b82001`）**

结果：`CrossArmorPlannerKind` / `plannerKind` 删除，`EquipmentUnitCatalogService` 从 1112 行
降到 315 行，13 条断言 Legacy 专有行为的用例一并删除，合成部件补上格子。
遗留：`CrossArmorLayerPreference` 参数与 GUI 的「层偏好」下拉框现在没有读者，需要单独决定
（删控件，或把偏好实现进 Cells——后者会改装箱结果）。

以下是当初评估代价的记录，保留以备回溯。

`CrossArmorPlannerKind.Legacy` 在生产里已经不可达：GUI（`CrossArmorTransferPlanWindowViewModel`）
与 CLI（`CliCrossArmorTransferPlanArtifacts`）都显式传 `Cells`，只剩默认参数。

把默认值翻成 `Cells` 之后 `EquipmentUnitCatalogServiceTests` **21/34 失败**（先把合成部件的
格子补齐是 21，不补是 24）。逐个看下来，失败分两类：

**一类只是用例的机械问题**（不是行为差异）：用例用**记录相等**比较部件
（`Assert.Equal(larger, mapping.Source)`），而 `Entry(...)` 里补上的 `Cell` 让计划里的部件
与用例本地变量不再相等。这类补上格子 + 改成按 (UnitAssetKey, MeshInfoIndex) 比较即可。
顺带确认：`StoredBytes` **不是** Legacy 独有的，`ArmorCellTable` / `CellCrossArmorPlanProjection`
把它当几何排序之后的平手判据（`ArmorCellTable.cs:130`、`CellCrossArmorPlanProjection.cs:267`），
所以「选最大的来源」在 Cells 下仍然成立，只是优先级排在几何之后。

**另一类是真的只存在于 Legacy**，删掉就是删功能：

| 行为 | 只有 Legacy 有的证据 |
| --- | --- |
| 层偏好（`CrossArmorLayerPreference`） | `layerPreference` 只出现在 Legacy 的 `SelectBodyTargets` / `ToPartLayer` 里；`CellCrossArmorPlanProjection.Build` 根本不收这个参数 —— 也就是说 GUI 上那个下拉框**现在就是空的** |
| 语义族匹配（`SemanticFamily` / `SemanticFamilyKey`） | 只被 `BuildSourceBodyProfiles` / `SelectSourceForTarget` 等 Legacy 助手使用 |
| 共享/固定 archive 的变体策略（`ApplySharedArchiveVariantStrategies` / `ApplyFixedArchiveVariantStrategies` / `ReconcileSharedTargetFallbackVariants`） | 同上，Legacy 专有 |
| 一个来源 mesh 复用到多个目标（`SourceMeshCanBeReusedForEveryCompatibleTarget`） | Cells 是「一个格子放一个来源」，复用在装箱模型里没有位置 |

对应要处理/删除的用例：层偏好 3 条、语义族 1 条、共享变体 2 条、来源复用 1 条，
其余 14 条是机械问题。**这四族要不要保留是产品决定**，不是清理：
其中「层偏好」在 GUI 上还留着一个无效的下拉框，删 Legacy 时必须一并处理，否则就是
「界面上有个控件，点了没反应」。

### 4.2 `part_layer` / `piece_type` —— 缺陷在输入，不在列

- `part_layer` 是第三套「层」：按 `pt=0→内层 / pt=1→护甲` 的约定，16569 行里只有
  **12.8%** 自洽（984 + 1138）。装箱路径已经全走 `Cell.Layer`（=piece_type），但
  **装饰管线仍在用它当筛选键**（`DecorationHostRebuildService`、`CliVpkRekey`、
  `DecorationHostTargetBinder`）。删列会直接改装饰行为。
- `piece_type` 字符串列混了两套词汇：`Armor` 6329 / `Undergarment` 5072 / `''` 1921 /
  `leg` 870 / `torso` 588 / `Accessory` 548 / `legs` 512 / `arm` 360 / `shoulder` 231 /
  `helmet` 138。根因在 `UnitMeshReader.BuildSemanticInfo`：**只要有骨骼名，就用骨骼名的
  三个 token 覆盖 CustomizationInfo 的 slot/pieceType/bodyType**，把唯一可靠的那份输入
  盖掉了（`ArmorStructure` §4 明确说「要判层看 unit 自己的 PieceType 字符串」）。

结论：这里该修的是「不要用骨骼名覆盖 CustomizationInfo」，而不是删列。改之前要先确认
`ArmorNormalizationService.SameLodFamily`、`UnitLodFamilySource`、`CanonicalAutoLodMappingExpander`
这些拿 `SemanticInfo` 做族匹配的地方不会因此错配。

### 4.3 顺带记录的两个未解观察

- `game_data_authority_pieces` 里 161 个头盔套有 **4 个不是恰好 1 件**（约定是每个头盔套
  1 件 0/0）。是数据如此还是去重逻辑漏了，没查。
- `ArmourUnitTableSource` / `ArmourUnitTableFacts` 的注释说「派生表会丢行（RE-2310 头盔套
  0 行、护甲套缺一个槽位）」，**现在不复现**：0/422 个 kit 丢行，RE-2310 有 1 行，
  138 个护甲套全部齐 2..7 六槽。那条理由已经被 `authority_pieces_schema = 2` 的
  `ON CONFLICT … RETURNING` 修掉了，但注释还在，于是系统里留着两条「谁更权威」的路径。

## 5. 怎么验证

- `dotnet build HD2ModCore.Tests` / `HD2ModCli` / `HD2ModManager`：0 警告 0 错误。
  管理器正在运行时用 `-p:OutputPath=tmp\manager-verify\` 绕过 bin 占用。
- `HD2ModCore.Tests` 829 通过；`HD2ModAdaptation.Tests` 180 通过。
- `HD2ModCli.Tests` 有 5 条 `DeploymentCliTests` 失败，**与本次改动无关**：它们要求
  `HD2ModManager` 进程不在运行（`CliManagerProcessGuard.EnsureDesktopManagerIsClosed`），
  改动前后同样 5 条失败（已用 stash 对拍）。
- 真库只读校验（`tmp/validate-sql.py` 的形态）：目录 SQL 仍返回 4447 行、4447 行都有格子。
