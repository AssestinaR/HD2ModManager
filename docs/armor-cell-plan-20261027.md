# 替换护甲：单元格装箱方案

时间：2026-10-27
范围：`ArmorCell` / `ArmorCellTable` / `CellAssignmentPlanner` / `game_data_unit_parts.authority_slot`
基线：`5f626d1`

本文记录**已经落地**的部分和设计依据。旧规划器（`EquipmentUnitCatalogService.CreatePlanAsync` 里的 P1~P5 启发式）暂未删除，等新链路跑通后替换。

---

## 1. 用户给的领域事实

这些是游戏行为，不是推测。方案的硬规则全部由它们推出。

| 事实 | 推论 |
|---|---|
| 头部是独立 archive | 头盔是独立目标 kit，不跟护甲混在一个计划里 |
| 头部与胸部 unit 在第一人称下隐藏 | 这两处的部件**不能外挂**到别的槽位（外挂后第一人称里会糊住相机）；别的部件也不该挂进来（等于凭空消失） |
| 分尸时按 unit 分离 | 改挂会改变断肢归属。**按要求忽略**：玩家死后很快切队友视角，且常见 mod 的网格本来就连着、分尸效果本来就差 |
| 任意网格挂任意非披风部位都能正确渲染 | 改挂只需要挑一个空槽，**不需要改注册表**——把来源几何写进那个槽原本指向的 unit 即可 |
| 披风双向禁区 | 披风挂出去、或别的部件挂进披风槽，都会渲染异常 |
| 替换护甲不碰披风 | 来源排除披风 unit；目标排除披风 cell（不分配、不隐藏） |

---

## 2. 为什么旧方案分不出"内层"和"护甲"

实测证据（索引库 + 原版注册表）：

**F1 — 网格名一样。** 躯干内层 unit 与躯干护甲 unit 的 mesh 都叫 `Torso_Armor_Stocky_lod0`。`SemanticFamily` 和 `Layer` 在内容上完全相同，旧规划器只能靠几何大小猜。

**F2 — `part_layer` 与注册表 97% 不一致。** 按 unit 对比 2006 个原版 unit：

| 注册表 pieceType | 索引 part_layer | unit 数 |
|---|---|---|
| 0（内层） | 2（护甲） | 1004 |
| 1（护甲） | 1（内层） | 629 |
| 2（装饰） | 2（护甲） | 144 |
| 1（护甲） | 1（内层，精确匹配） | 23 |

`AuthorityUnitRole.FallbackLayer` 本来是用来修的，但它只在 `part.Layer == Unknown` 时生效，而分类器几乎从不返回 Unknown——**这个修正从来没生效过**。

**F3 — 一个 unit 全局只属于一个格子。** 2006 个 unit 里，`(slot, piece_type)` 出现两种以上的：**0 个**。所以格子是 unit 的**身份**，不是"某件护甲的属性"。

**F4 — 每个格子要么 Any，要么 Slim+Stocky 成对。** 2379 个格子里没有"只有 Slim 没有 Stocky"的半满形态。但**同一个格子在不同 kit 里可能一种是 Any、一种是成对**，实测有 6 个：`(2,2) (4,0) (4,2) (5,0) (6,0) (7,0)`。

---

## 3. 算法：来源找箱子

```
输出 = { 目标 cell → 一组来源 part }
```

一个目标 cell 产出一个 unit，这个 unit 的 mesh 列表 = 分给它的所有来源 part 的 mesh 合并。
1 个 part = 直接替换；2 个以上 = 合并；0 个 = 隐藏。

**方向必须是"来源找箱子"**：反过来（每个目标格子找来源）在来源比目标多的时候会静默丢掉来源部件，mod 的模型就少一块。

| 阶段 | 规则 |
|---|---|
| **P1 自然 / 广播** | 同格子。来源是通用网格 → 一个 unit 服务目标两个身形；来源该格子只有一个具体身形 → 精确命中同身形、其余广播；来源成对而目标是 Any → 按「优先身形」挑一个，另一个留作多余件 |
| **P2 同槽改层** | 同槽位还有空桶 |
| **P3 跨槽改挂** | 池子 = 胯 / 腿 / 臂 / 肩（槽 3..9）。胸（2）与头（0）**不进池子** |
| **P4 合并** | 剩下的合进已占用的桶：优先同槽、再**往高处并**（多余的第 2 层并进第 1 层；只有第 0 层时全并进第 0 层） |
| **P5 隐藏** | 目标上仍空着的桶；`AutoHideUnmappedTargetUnits` 关掉时这些桶**根本不进计划**（原版保持原样，不写 Patch） |

排序键全部写死（槽升序 → 层升序 → Slim 优先 → unit id），保证同一输入产出同一计划。

### 实测容量（138 个护甲 kit 的 18906 个有序配对，已排除披风、胸部隔离）

| 情况 | 占比 |
|---|---|
| 完全自然命中 | 47% |
| 改挂解决，不合并 | 54% |
| 必须合并 | 45% |

合并缺口 1~2 个部件占 52%。这个模拟假设 mod 提供来源 kit 的**全部** unit；真实 mod 只提供一部分，实际比例更低。

---

## 4. 合并从哪来

不需要新写。`CanonicalDecorationUnitAppender.TryAppendLodFamily` 就是 unit 级 Blender Join，现役调用方三个：

- `DecorationHostRebuildService`（装饰挂宿主）
- `HelmetToArmorMergeService`（头盔合并胸甲）
- **`ArmorNormalizationService`（N 个来源 part 合并进一个目标 unit）** ← P4 就是它

契约：

1. 宿主 unit `CompositeRef != 0` → 不支持（`DecorationCompositeUnitUnsupported`），退化成 warning 跳过。
2. 来源 LOD 必须与宿主 LOD 相同（`DecorationLodMismatch`）。
3. 只支持可见 LOD。
4. 重建整族 LOD。
5. **`replaceTargetGeometry` 必须传 `false`**——传 `true` 会把宿主几何极小化掉。我们要的是"第一个 part 的几何保留 + 其余追加"。

隐藏侧已有 `CanonicalHiddenUnitBuilder`（`ArmorNormalizationService:157` 在用）。

---

## 5. 已落地（本轮）

### 5.1 索引新增权威格子

`game_data_unit_parts` 加两列，**-1 表示没有权威归属**：

```sql
authority_slot       INTEGER NOT NULL DEFAULT -1   -- CustomizationKitSlot 0..9
authority_piece_type INTEGER NOT NULL DEFAULT -1   -- 0=内层 1=护甲 2=装饰
```

写入链：`AdvancedEquipmentIndexService.ReadAuthorityRolesAsync` 多读 `slot` 列 → `AuthorityUnitRole.Create` 在 `(slot, pieceType)` 唯一时给出 `ArmorCell`，不唯一时老实给 `ArmorCell.Unknown`（不折中）→ `GameDataUnitPartFact.Cell` → 落库。
读取链：`EquipmentUnitCatalogService.ReadEntriesAsync` → `EquipmentUnitPart.Cell`。
`AssetArchiveIndexService` 的 `preserve/restore` 临时表也带上了这两列，否则重建基础索引会把它们丢掉。

**`part_layer` 没有改。** 它还被装饰、头盔合并、护甲归一化三条现役链路用着，改语义要单独验证。规划器只认 `Cell`。

### 5.2 领域类型

- `ArmorCell(Slot, Layer)` —— 格子身份，带 `IsFirstPersonHidden`（胸/头）与 `IsCape`。
- `ArmorCellSlot` / `ArmorCellTable` —— 把一堆 unit 折成 `(格子, 身形) → unit`；没有格子的 unit 进 `Unplaced`，同格子同身形出现多个进 `Duplicates`，都不静默丢。
- `CellAssignmentPlanner.Plan(...)` —— P1~P5，输出 `CellAssignmentResult`。

### 5.3 验证

重建 `units-all`（77 秒）后对账：

| 检查 | 结果 |
|---|---|
| 两列是否存在 | 是 |
| 有权威格子的行 | **16569 / 16569 = 100%** |
| 一个 unit 出现多个格子 | **0** |
| 与 `game_data_authority_pieces` 逐行对账 | **0 处不一致** |
| `part_layer` 与权威 piece_type 一致 | 2122 / 16569 = **12.8%**（证明旧字段不可信） |

测试：`CellAssignmentPlannerTests` 17 条，覆盖格子身份、格子表去重/未定位/重复、P1 自然与三种广播、P2 同槽改层、P3 跨槽且优先邻近、胸部件不外挂、披风双向排除、P4 往高处并、P5 开关、以及确定性。全量 1325 通过 / 1 跳过。

---

## 5.4 A/B：接进 CreatePlanAsync

`CreatePlanAsync` 增加 `plannerKind`，默认仍是 `Legacy`（`EquipmentUnitCatalogServiceTests` 有一整套旧行为基线）；
**GUI 与 CLI 显式传 `Cells`**。新增 `CellCrossArmorPlanProjection` 把装箱结果投影成同样的
`CrossArmorTransferMapping`，生产端一行没改。合并用 `ExtraSources` 表达（`Source` 是宿主，其余按顺序追加）。

A/B 用真实数据跑：来源 `lunalice_l4d2替换RE2310`，目标 BFM-16 / BFM-220 / BFM-77 的护甲+头盔（6 个目标）。

| | 旧启发式 | 单元格装箱 |
|---|---|---|
| 映射总数 | 70 | 70 |
| 替换 | 30 | **33** |
| 隐藏 | 40 | 37 |
| 问题 | 0 | 0 |
| 每个护甲目标的替换数 | 9 | **10** |

- **旧方案替换的 30 个 unit 一个没丢**（only-old = 0），新的多出 3 个。
- 多出来的 3 个都是 **Torso/Undergarment/Stocky**：来源同时有 Slim 和 Stocky 两个躯干内层，
  旧方案只命中了 Slim 那个，Stocky 那个被"覆盖完备性"状态机漏掉了。
- 所有命中都是 `Natural`，没有发生合并——这套数据里来源和目标结构本来就对得上。

### A/B 抓到的一个真 bug

第一版把所有目标 unit 摊成一张表，结果**头盔目标吞下了 8 个身体部件**
（胯/腿/臂的内层全被合并进那个唯一的头盔 unit）。原因：护甲 kit 的格子是槽 1..9，
头盔 kit 只有一个槽 0；不按装备族拆分来源，头盔那个唯一的桶就会把剩余部件全部吸收。

修法：来源按 `IsSameFamily` 拆成 护甲/头盔 两张表，按目标归档的 category 取用。
`CellCrossArmorPlanProjectionTests` 里留了三条护栏，其中第一条就是这个现场。

**这个 bug 只有跑真实数据才会暴露**——纯单测里我一直用的是同一族的部件。


---

## 6. 还没做

| 项 | 说明 |
|---|---|
| P4 接 `TryAppendLodFamily` | cell 分到 2+ part 时走 canonical 重建；1 个 part 仍走「快速复用来源 Unit」。**注意**：`CanonicalCrossArmorOrchestrator` 的直通路径目前要求"目标的所有命中 Mesh 来自同一个来源 Unit"，遇到 `ExtraSources` 非空会报 `DirectReuseMultipleSourceUnits`。生产端还没接合并，所以现在**规划阶段可能出现合并，生成阶段会拒绝**——下一轮必须先补这一段 |
| P5 接 `CanonicalHiddenUnitBuilder` | 复用 `ArmorNormalizationService` 的做法 |
| 来源部件表改造 | 两段式加载（TOC 立即列出全部 unit → 后台算可见性）+「作为来源」列 + 披风标「不参与」 |
| 自动导入 Mod 库 | `AutoImport` 复选框 + `new ImportService(_library, informationCenter: _library.InformationCenter).ImportPathAsync(output, ct)`；替换当前 Mod 用 `_library.ReplaceStoredFilesAsync(nodeId, dir, ct)` |
| 删旧规划器 | A/B 已通过（旧结果全保留 + 多 3 个），可以开始迁移 `EquipmentUnitCatalogServiceTests` 并翻转默认值 |
| 计划表显示新原因 | 计划表目前只认识「命中 / 强制命中 / 隐藏 / 强制隐藏」四种 `Reason`；新增的「广播 / 改挂 / 合并」需要补颜色规则 |
