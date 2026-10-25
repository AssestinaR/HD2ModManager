# 「某个 unit 属于某件护甲的什么部位」——全量事实源与消费点审计

**日期：** 2026-10-25
**范围：** 只覆盖**护甲 unit 级**事实（unit → 部位 / 槽位 / 身形 / 层 / unit id）。
mesh 级几何事实（section、顶点、材质槽）只在与 unit 命名混淆时提及。
**不在范围：** 武器、载具、披风的物理与动画。

---

## 0. 结论摘要

这条事实在项目里被**四套互不隶属的来源**提供，被**至少 18 处**各自解释，其中：

- 有 **2 套槽位编号**（件表 0..9 数字 vs unit 自带字符串），代码里**没有任何类型或断言阻止混用**；
- 有 **4 套中文部位名**，同一个部位被叫成"头盔/头部"、"胸部/胸口"、"左肩/左肩甲"；
- 有 **3 份 `slot → UnitMeshPartKind` 转换**，其中 1 份漏了肩甲和披风、1 份把肩甲折成躯干；
- 有 **5 套身形编号转换**，其中原始 `0=Stocky` 与枚举 `0=Unknown` 正好错位，而 `1=Slim`、`3=Any` 又恰好相等——这类 bug 只会在部分护甲上出现；
- unit 的显示名有 **5 种互不兼容的拼法**，其中 `1447930109_lod0` 这种"用 meshId 当名字"的兜底就是截图里那一行。

用户的判断是对的：**现在的显示既不真实，也不稳定。**

### 0.1 结构说明已经落到代码里

本轮的护甲结构结论不再只写在文档里，而是写进了
[`HD2ModCore/Domain/ArmorStructure.cs`](../HD2ModCore/Domain/ArmorStructure.cs) 顶部的注释块
（四元组寻址、身形编码、件记录布局、两套事实源、规范名），并在这几个最容易被改坏的地方加了指针：

- `HD2GameItemAssets/GameItemAssetModels.cs` · `CustomizationKitSlot`
- `HD2ModCore/Domain/GameDataAuthorityFacts.cs` · `GameDataAuthorityPiece`
- `HD2ModCore/Domain/RuntimeArmorRegistry.cs` · `RuntimeArmorRegistryModelPiece`
- `HD2ModCore/Domain/RuntimeArmorModelStructure.cs` · `Any` 与 `Stocky+Slim` 互斥规则
- `HD2ModAdaptation/Analysis/UnitMeshPartContracts.cs` · `UnitMeshPartKind`（并注明它没有 Cape）

以后改这块先读那一段，不要再从别处推断。

---

## 1. 事实源清单（谁在提供事实）

| 编号 | 来源 | 粒度 | 权威性 | 入口 |
|---|---|---|---|---|
| **S1** | unit 自身 `CustomizationInfo`（TocData +0x4C） | unit | **权威**（游戏自带） | `UnitMeshReader.ReadCustomizationInfo` `HD2ModAdaptation/PatchReconstruction/UnitMesh/UnitMeshReader.cs:744` |
| **S2** | 注册文件件表 `generated_customization_armor_sets` | **件** | **权威**（游戏自带） | `CustomizationArmorSetsReader` → `GameDataAuthorityProvider.Project` `HD2ModCore/Infrastructure/GameDataAuthorityProvider.cs:312` |
| **S3** | `bonehash.txt`（MeshID → 骨名） | mesh | **网上下载**（社区/SDK） | `GitHubAssetMetadataSyncService` `HD2ModCore/Infrastructure/GitHubAssetMetadataSyncService.cs:19` |
| **S4** | `UnitMeshPartClassifier` 字符串启发 | mesh | 猜 | `HD2ModAdaptation/Analysis/UnitMeshPartClassifier.cs:88` |
| **S5** | SQLite `game_data_authority_pieces` | 件 | **派生缓存**（S2 的物化） | `AssetArchiveIndexService.SaveAuthoritySnapshotAsync:784` |
| **S6** | SQLite `game_data_unit_parts` | mesh | **混合**（S1+S4，再被 S5 覆盖） | `AdvancedEquipmentIndexService.CreatePartFacts:450` |
| **S7** | 手写冻结表 `re824_to_re2310_override.lua` | unit | **人工**（已过期） | `HD2RuntimeArmorOverride/src/re824_to_re2310_override.lua:39` |
| **S8** | Lua 运行时 `is_body_slot(slot) = 2..9` | slot | 手写 | `HD2RuntimeArmorOverride/src/armor_registry_composition.lua:30` |

### 1.1 关于 S3（网上下载源）的现状——比想象的好，但有残留

好消息：**生产路径已经不使用骨名 LUT 做部位判定**。

- `UnitMeshPartClassifier.Classify(..., globalBoneNames)`（带 LUT 的重载）**只有测试在调用**；
- `AdvancedEquipmentIndexService:461` 明确写 `classifier.Classify(adaptationKey, model)`（不带 LUT），注释是 *"avoid community bone-name lookup here entirely"*；
- `DecorationHostTargetBinder:89` 同样不带 LUT。

残留：

- `bonehash.txt` 仍由 `GitHubAssetMetadataSyncService` 下载，CLI `--bone-hash`（`CliApplication.cs:2763-2789`）用于**骨骼重定向**——这个用途是正当的，与部位无关。
- `UnitMeshReader.BuildSemanticInfo` 仍会用 unit **自带**的 `UnitBoneNames` 覆盖部位（见 §3 N12）。这是游戏内数据，不是下载数据，但**覆盖方式错了**。

---

## 2. 消费点清单（谁在解释事实）

### 2.1 域类型

| 类型 | 位置 | 携带的部位信息 |
|---|---|---|
| `UnitMeshPartFact` | `HD2ModAdaptation/Analysis/UnitMeshPartContracts.cs:48` | `PartKind` `Layer` `BodyVariant` `SemanticName` `EvidenceKind` `Confidence` |
| `GameDataUnitPartFact` | `HD2ModCore/Domain/GameDataUnitPartFact.cs:7` | 同上 + `Origin` `PieceType` + 几何 |
| `GameDataAuthorityPiece` | `HD2ModCore/Domain/GameDataAuthorityFacts.cs:34` | `RawBodyType` `BodyVariant` `Slot` `PartKind` `PieceType` `Weight` |
| `EquipmentUnitPart` | `HD2ModCore/Domain/EquipmentUnitCatalog.cs:20` | `PartKind` `Layer` `BodyVariant` `SemanticName` `PieceType` |
| `WardrobeSectionMapUnit` | `HD2ModCore/Domain/WardrobeSectionMap.cs` | `Slot`(字符串) `PieceType` `BodyType` `Weight` |
| `RuntimeArmorRegistryModelPiece` | `HD2ModCore/Domain/RuntimeArmorRegistry*.cs` | `Body`(原始 int) `Slot`(数字) `PieceType` |

**注意：`PartKind` 在这三个类型里语义并不相同**——在 `GameDataAuthorityPiece` 里它来自件表 `slot`，在 `GameDataUnitPartFact` 里它可能来自 unit 字符串或骨名。同名不同义。

### 2.2 转换 / 投影点

| 位置 | 作用 | 问题 |
|---|---|---|
| `GameDataAuthorityProvider.ToPartKind:1015` | 件表 slot → PartKind | ✅ 正确（用 `CustomizationKitSlot` 常量） |
| `RuntimeArmorRegistryService.ToPartKind:248` | 同 | ❌ 裸数字，漏 1/8/9 |
| `CustomModelConversionService.SlotFor:100` | PartKind → 件表 slot | ❌ 肩甲/配件全折成 2 |
| `ArmorRegistryPageViewModel.SlotLabel:2159` | slot → 中文 | ❌ 漏 1/8/9 |
| `ArmorRegistryPageViewModel.SlotOrder:1550` | slot 排序 | ❌ 漏 1/8/9 |
| `ArmorRegistryPageViewModel.ComponentSlotOptions:307` | 下拉可选部位 | ❌ 漏 1/8/9 |
| `WardrobeTree.SlotLabel:420` | slot 字符串 → 中文 | ✅ 全 9 项 |
| `WardrobeTree.SlotOrder:115` | 排序 | ✅ |
| `AssetArchiveIndexService.PartName:1095` | PartKind → 中文 | ⚠ 用词不同（头部/胸口） |
| `AdvancedModAssetQueryService.PartName:158` | 同上 | ⚠ 逐字复制粘贴 |
| `ModSearchMatcher:67` | 搜索别名 | ⚠ 又一份词表，只覆盖头盔 |
| `AdvancedEquipmentIndexService.AuthorityUnitRole.Create:494` | 件表角色合并 | ⚠ 见 N1 |
| `WardrobeLinkCompiler:41` | 用 (body, slot, piece_type) 编译链接 | ✅ 只用件表，注释已说明不混编号 |

### 2.3 GUI 呈现点

| 界面 | 列 | 绑定 | 事实源 |
|---|---|---|---|
| 交叉护甲页 · 来源可见部件 | 部位 / 身形 / **语义** / ID | `PartKind` `BodyVariant` `SemanticName` | S5+S6 |
| 交叉护甲页 · 目标预测 | 部位 / 结果 / ID / **目标** / 尺寸 / 命中·复用 / 目录身形 / 来源身形 | `Target.PartKind` `Target.SemanticName` `Target.BodyVariant` | S5+S6 |
| 装饰规划页 | 部位 | `PartKind` | S6 |
| 批量装饰规划页 | 部位 | `PartKind` | S6 |
| 护甲注册页 · 组件编辑器 | 分组标题 = 中文部位；每行 = (slot, pieceType)，三格 = Stocky/Slim/Any | `SlotLabel` `BodyLabel` | S5 / 自定义素体 |
| 衣柜编辑器 | 护甲 → 身形 → 槽位 → Unit N → section | `WardrobeTree` | S1 |

### 2.4 CLI 呈现点

| 位置 | 输出 |
|---|---|
| `CliUnitInspection.cs:237` | `CliUnitCustomizationSummary(BodyType, Slot, Weight, PieceType)` — **字符串原样**，最忠实 |
| `CliApplication.cs:2337` | `slot = pair.Value.Model.CustomizationInfo.Slot` |
| `CliApplication.cs:2981`、`CliSourceConvert.cs:257` | `{slot}/{pieceType}{Any\|0\|1}` — 裸数字，无部位名 |
| `CliWardrobe.cs` | 完全不输出部位（衣柜 CLI 只认 unit + section 下标） |

---

## 3. 不规范清单

### N1 — 同一 unit 的"部位"由**引用它的护甲集合**决定，而不是由 unit 自己决定

`AdvancedEquipmentIndexService.CreatePartFacts:464`：

```csharp
var kind = authorityRole?.PartKind ?? part.PartKind;
```

`AuthorityUnitRole.Create:496-500`：把该 unit 在**所有 kit** 里出现过的 `part_kind` 去重，**只有一个值才采用**，否则 `null` 退回 unit 自身语义。

后果：

- 一个 unit 若同时被 2 个部位引用（共享 unit 很常见，例如左右肩共用一个"肩甲"unit），显示的部位会**静默退化**成骨名启发的结果；
- 加入/删除一件护甲可能改变另一件护甲的显示；
- `AuthorityUnitRole` 不区分 kit 种类，同一 archive 同时属于 Armor 与 Helmet 时也会判 Ambiguous。

### N2 — 两套槽位编号，无类型保护

| 编号 | 取值 | 出处 |
|---|---|---|
| 件表 slot | `0..9`（0 头盔 / 1 披风 / 2 躯干 / 3 胯 / 4 左腿 / 5 右腿 / 6 左臂 / 7 右臂 / 8 左肩 / 9 右肩） | `HD2GameItemAssets/GameItemAssetModels.cs:10` `CustomizationKitSlot` |
| unit `CustomizationInfo.Slot` | 字符串 `Helmet`/`Torso`/`Hips`/`LeftArm`/… | S1 |

`ArmourUnitTableSource.cs:13-15` 和 `WardrobeLinkCompiler.cs:16-17` 都用**注释**写了"两套编号在 5/6/7 档不一致，不能混用"，但没有任何类型、断言或测试阻止混用。`GameDataAuthorityPiece` 同时带 `Slot` 和 `PartKind`，`GameDataUnitPartFact` 也带 `PartKind`——两个不同来源的 `PartKind` 同名同类型。

### N3 — `RuntimeArmorRegistryService.ToPartKind` 漏了披风和双肩

```csharp
0=>Head, 2=>Torso, 3=>Pelvis, 4=>LeftLeg, 5=>RightLeg, 6=>LeftArm, 7=>RightArm, _=>Unknown
```

`1`（Cape）、`8`（LeftShoulder）、`9`（RightShoulder）全部落到 `Unknown`。自定义素体里这三类组件在交叉护甲页**无法被选中**（`CreatePlanAsync:154` 要求 `PartKind != Unknown`）。

同一件事，`GameDataAuthorityProvider:1015` 是正确实现。两份实现，一份漏。

### N4 — 自定义素体转换把肩甲/配件折成躯干

`CustomModelConversionService.SlotFor:100`：

```csharp
UnitMeshPartKind.LeftShoulder or UnitMeshPartKind.RightShoulder => 2,
UnitMeshPartKind.Accessory => 2,
```

把带肩甲的来源转成自定义素体后，肩甲会被写成 `slot=2`（躯干）件。运行时按 `(body, slot, piece_type)` 匹配，于是它会被当成躯干层，挤占躯干的 3 个 unit 槽。

这是"用起来不踏实"的典型：数据能跑，但语义是错的。

### N5 — "可选部位"列表只有 7 项

`ArmorRegistryPageViewModel.cs:307`：

```csharp
new(0,"头盔"), new(2,"胸部"), new(6,"左臂"), new(7,"右臂"), new(3,"胯部"), new(4,"左腿"), new(5,"右腿")
```

用户在"添加组件"里**选不到左肩 / 右肩 / 披风**。`SlotOrder:1550` 与 `SlotLabel:2159` 是同一份枚举的第三、第四遍抄写，同样漏三项。

### N6 — 中文部位名有 4 套，用词不一致

| 位置 | 头 | 躯干 | 肩 |
|---|---|---|---|
| `WardrobeTree.SlotLabel:420` | 头盔 | 胸部 | 左肩 / 右肩 |
| `ArmorRegistryPageViewModel.SlotLabel:2159` | 头盔 | 胸部 | **无** |
| `AssetArchiveIndexService.PartName:1095` | **头部** | **胸口** | **左肩甲** / 右肩甲 |
| `AdvancedModAssetQueryService.PartName:158` | **头部** | **胸口** | **左肩甲** / 右肩甲 |
| `ModSearchMatcher:67` | 头盔/头部/头 | — | — |

同一件东西，搜索能搜到"头部"，界面写"头盔"，索引摘要写"头部"。

### N7 — `DescribeParts` 四件套逐字重复

`AssetArchiveIndexService.cs:1087-1114` 与 `AdvancedModAssetQueryService.cs:148-190` 的
`DescribeParts` / `PartName` / `LayerName` / `BodyVariantName` 是**复制粘贴**的两份（约 35 行）。

### N8 — 死代码：`AssetArchiveIndexService` 里的 unit part 写入

`CreateUnitPartInsertCommand:1052` 与 `InsertUnitPartAsync:1064` **没有任何调用方**。
写 `game_data_unit_parts` 的唯一活路径是 `AdvancedEquipmentIndexService`。
保留它只会让人误以为两条路径都在写，从而以为两份逻辑一致。

### N9 — 分类谓词不一致，共享 archive 会被静默排除

> **2026-10-25 实测修正**：本机 421 个 archive 里**没有任何一个**同时承载两种 category，
> 所以 `IN ('armor','helmet')` 目前不会真的丢东西。这仍是**潜在** bug（谓词不一致本身
> 就是维护陷阱，且 `"Armor, Helmet"` 这个返回值随时可能成真），但严重度从"正在丢数据"
> 下调为"防御性修复"。

| 位置 | 谓词 |
|---|---|
| `AdvancedEquipmentIndexService`（7 处） | `lower(category) LIKE '%armor%' OR LIKE '%helmet%'` |
| `AssetArchiveIndexService:980` | 同上 |
| **`EquipmentUnitCatalogService:69`、`:1032`** | **`lower(category) IN ('armor','helmet')`** |

而 `GameDataArchiveSemanticClassifier:42-45` 在**同一个 archive 同时属于 Armor 与 Helmet** 时返回 `"Armor, Helmet"`。

于是这种 archive 在交叉护甲目录里**被静默丢掉**（`LIKE` 版本能匹配，`IN` 版本不能）。`ORDER BY CASE lower(category) WHEN 'armor' THEN 0 ELSE 1 END:73` 对多值也判错。

### N10 — 部位枚举没有"披风"

`UnitMeshPartKind` 只有 `Accessory`。`CustomizationKitSlot.Cape = 1` 和 `GameDataEquipmentKitKind.Cape` 都存在，`ToPartKind` 把披风映射成 `Accessory`，与真正的配件（pouch / pad / plate）混在一个桶里。披风在运行时是独立 slot（`armor_registry_composition.lua:122`），在事实层却不是一个部位。

### N11 — 身形编号双轨，且**部分相等、部分错位**

| 原始值 | 含义 | `UnitMeshBodyVariant` | 是否相等 |
|---|---|---|---|
| 0 | Stocky | **Unknown** | ❌ 错位 |
| 1 | Slim | Slim | ✅ 巧合 |
| 3 | Any | Any | ✅ 巧合 |

任何一处"顺手 `(UnitMeshBodyVariant)raw`"都会把 Stocky 变成 Unknown，而 Slim/Any 又恰好对——这种 bug 只在**部分护甲**上出现。

现在靠 **5 处手写 switch** 各自维护：`GameDataAuthorityProvider:1008`、`RuntimeArmorRegistryService:230`、`ArmorRegistryPageViewModel:2152`、`RuntimeArmorModelStructure`（`Body == 3` 字面量 ×6）、`CliArmorRegistryProtocol:730`。

### N12 — 骨名会**覆盖**权威的 `CustomizationInfo`

`UnitMeshReader.BuildSemanticInfo:794-804`：

```csharp
if (boneName is not null) {
    name = boneName;
    var inferred = InferSemanticFromName(boneName);   // 拆 '_' 取 parts[0..2]
    if (inferred is not null) { slot = inferred.Slot; pieceType = inferred.PieceType; bodyType = inferred.BodyType; }
}
```

`InferSemanticFromName("g_torso_undergarment_slim")` → `slot="g"`、`pieceType="torso"`、`bodyType="undergarment"`。

也就是说：**游戏自带的权威字符串被骨名拆词结果覆盖成了垃圾**。现在之所以没炸，是因为下游 `ResolveKind` / `ResolveBodyVariant` 又在整串文本里做了一次 `contains` 兜底——"错误数据 + 巧合修复"。

### N13 — unit 的显示名有 5 种拼法，且被直接当作主标识

| 拼法 | 例子 | 出处 |
|---|---|---|
| `{Slot}_{PieceType}_{Body}_lod{N}` | `Torso_Undergarment_Slim_lod0` | `BuildSemanticInfo:793` |
| 骨名 | `g_torso_undergarment_slim` | `BuildSemanticInfo:796` |
| `{meshId}_lod{N}` | **`1447930109_lod0`** | `BuildSemanticInfo:780`（兜底） |
| `{meshId}_mesh{N}` | culling | `BuildSemanticInfo:780` |
| `custom_{modelKey}_{slot}_{pieceType}` | `custom_xxx_2_1` | `RuntimeArmorRegistryService:237` |

截图里"语义"列和"目标"列显示的就是上表里的任意一种。用户说"显示的不真实"——根因在这里：**这一列不是部位名，是一个 per-mesh 的调试字符串**。

### N14 — `lod0` 出现在 unit 级标识里

`SemanticName` 是 **per-mesh** 的（`BuildSemanticInfo` 接收 `lodIndex` / `meshInfoIndex`），而 `EquipmentUnitPart` / `CrossArmorTransferMapping` 是 **per-unit** 的概念。
把 mesh 名当 unit 名，就必然带出 `_lod0` 后缀，而且一旦某 unit 有多个 visual mesh，还会出现"一个 unit 多行、尺寸重复计"的隐患（`StoredBytes` 按 unit 的 `MAX(...)` 算，`MeshInfoIndex` 却按 mesh 区分）。

**建议在实机上确认一次：是否存在 visual mesh 数 > 1 的护甲 unit。** 若存在，交叉护甲页当前是重复行。

### N15 — 冻结的人工部位表仍在构建产物里

`HD2RuntimeArmorOverride/src/re824_to_re2310_override.lua:39-62` 是 22 条手写的
`{源 unit, 目标 unit, "left_leg_inner_any" / "chest_armor_stocky" / "left_shoulder_stocky_attachment_to_armor"}`。

它是唯一一处**人工命名**的部位事实（而且命名风格恰好就是用户想要的那种），与权威源无任何关联，会随游戏更新漂移。`wardrobe-links-migration-20261023.md:133` 已声明被 `wardrobe.links` 取代，但 `Build.ps1:29` 和 `Test.ps1:45` 仍在编译/测试它。

### N16 — 仓库根目录有个 0 字节的 `armor_registry_runtime.lua`

`E:\Data\source\repos\WpfApp1\armor_registry_runtime.lua` 大小 0（2026-10-20）。真身在 `HD2RuntimeArmorOverride/src/`（65 KB）。
误读会得出"运行时没有实现"的结论。

### N17 — Lua 侧件长仍硬编码 96，C# 已收敛为 112

| 位置 | 值 |
|---|---|
| `armor_registry_runtime.lua:699,734,900` | `piece*96` / `j*96` |
| `armor_metadata_probe_core.lua:41` | `#data ~= 96` |
| `composition_clone_publish_test.lua:61,112` | `piece*96` |
| C# `CustomizationArmorSetsReader` | `PieceSize = 112`（**文件**格式） |

需要确认**内存里**的件记录是否仍是 96。若引擎内存布局也随文件一起加宽，这就是 2026-10-20 那次回归的同类残留；若内存仍是 96，则应在 Lua 里写明"这是内存布局，不是文件布局"，否则下一个人一定会改错。

### N18 — CLI 只输出裸数字

`CliApplication.cs:2981`、`CliSourceConvert.cs:257`：`{slot}/{pieceType}{Any|0|1}`，例如 `2/1Any`。
没有部位名，也没有单位。GUI 显示"胸部"，CLI 显示 `2`。同一事实两种呈现。

### N19 — 权威性（`Origin`）在投影层丢失

`GameDataUnitPartFact.Origin` 已经区分 `AuthoritativeGameData` / `DerivedUnitStructure` / `HeuristicFallback`，但：

- `EquipmentUnitPart`（GUI 用）**没有** `Origin` 字段；
- 交叉护甲页只保留 `Confidence`；
- `EquipmentUnitCatalogService:154` 用 `Confidence == 100` 硬过滤。

等于把"这条事实是游戏给的还是猜的"编码进了一个魔数。用户看不到哪些行是可信的。

### N20 — 搜索别名表是第 5 份部位词表

`ModSearchMatcher.cs:67`：`AddAliases(result, "helmet", "helmet", "头盔", "头部", "头")`。只覆盖头盔，其余部位没有对应别名。

---

## 4. 建议的统一模型

### 4.1 一个值对象，一个目录

```csharp
// 唯一的 unit 级部位身份。所有层都用它，不再各自拼字符串。
public readonly record struct ArmorUnitRef(
    ArmorPart Part,        // Helmet, Cape, Torso, Hips, LeftLeg, RightLeg, LeftArm, RightArm, LeftShoulder, RightShoulder
    byte Slot,             // 件表编号 0..9（CustomizationKitSlot）
    BodyVariant Body,      // Stocky / Slim / Any
    byte Layer,            // piece_type 0..N
    ulong UnitFileId);
```

配套一个 `ArmorPartCatalog` 静态类，独占：

1. `CustomizationKitSlot` ↔ `ArmorPart` 的双向映射（**只此一份**）；
2. 原始身形 int（0/1/3）↔ `BodyVariant`（**只此一份**，带断言）；
3. 中文显示名（**只此一份**）；
4. 排序（**只此一份**）；
5. `ArmorUnitRef` → 规范名（见 4.2）。

`GameDataAuthorityProvider.ToPartKind`、`RuntimeArmorRegistryService.ToPartKind`、`CustomModelConversionService.SlotFor`、
`ArmorRegistryPageViewModel.SlotLabel/SlotOrder/ComponentSlotOptions`、`WardrobeTree.SlotLabel/SlotOrder`、
`AssetArchiveIndexService.PartName`、`AdvancedModAssetQueryService.PartName` 全部改为调用它。

### 4.2 规范名：`{part}_{pieceType}_{body}`

**已确认**：`torso_2` 里的 `2` 是**该部位的第 3 个 unit 槽**（`piece_type`），不是部位编号。
所以名字里排第二的是 `piece_type`：

```
helmet_0_any         头盔 · 第 0 槽 · 通用
torso_0_slim         躯干 · 第 0 槽（内层）· 纤细
torso_1_slim         躯干 · 第 1 槽（护甲）· 纤细
torso_2_slim         躯干 · 第 2 槽（装饰）· 纤细
hips_0_stocky
left_leg_1_any
right_leg_1_slim
left_arm_1_stocky
right_arm_0_any
left_shoulder_0_any
right_shoulder_0_any
cape_0_any
```

- 全小写 ASCII，可直接做文件名、键名、Lua 标识符、搜索词；
- 四元组 `(itemId, body, slot, pieceType)` 才是唯一键，名字把后三维都编进去了；
- **不带 `lod`**——unit 级身份与 LOD 无关；
- 部位用完整英文，不用 `l_`/`r_` 缩写（缩写是 `UnitMeshPartClassifier` 里那堆 `contains` 的根源）。

完整结构见 `HD2ModCore/Domain/ArmorStructure.cs` 顶部。

### 4.3 `SemanticName` 降级为调试字段

`SemanticName` 保留，但**不再作为主标识**：

- 主标识列改为规范名 + unit id；
- `SemanticName` 移到 tooltip 或"原始语义"次要列；
- 交叉护甲页的"目标"列改名为"**部位**"或"**Unit**"，并把现有"部位"列改为规范名。

---

## 5. 建议的整改顺序

| 阶段 | 内容 | 风险 | 收益 |
|---|---|---|---|
| **P0** | 建 `ArmorPartCatalog`（映射 + 中文名 + 排序 + 规范名），只加不改 | 无 | 后续所有改动有落点 |
| **P1** | 修 **N3 / N4 / N5**（肩甲、披风被漏/被折成躯干） | 低（纯扩表） | 自定义素体终于能放肩甲 |
| **P2** | 修 **N9**（`IN` → `LIKE`），统一分类谓词 | 低 | 共享 archive 不再静默消失 |
| **P3** | 删 **N8** 死代码、删 **N16** 空文件、处置 **N15** 冻结表 | 低 | 少两个坑 |
| **P4** | 合并 **N6 / N7** 的中文名与 `DescribeParts` | 低 | 界面用词一致 |
| **P5** | 修 **N12**（骨名不得覆盖 `CustomizationInfo`） | 中（会改变现有显示） | 事实回到权威 |
| **P6** | 修 **N1**（部位由 unit 自己决定，不由引用集合决定） | 中（需改成多对多） | 显示稳定 |
| **P7** | 引入 **4.2 规范名**，`SemanticName` 降级 | 中（涉及 GUI/CLI/协议） | 用户看到的终于"真实" |
| **P8** | 把 `Origin` 透出到 GUI（**N19**） | 低 | 用户能分辨权威/猜测 |

**P5 必须先定语义再动手**：

- P5：骨名到底该不该参与部位判定？如果该，那么骨名的解析规则必须改成"按已知前缀表匹配"，而不是"拆 `_` 取前三段"。

**P6 的语义已定（2026-10-25）**：共享 unit **同时属于所有引用它的护甲，不属于其中任何一件**。
这是事实，不需要折中。因此事实层必须建模成 unit ↔ kit 的**多对多**关系；
`AuthorityUnitRole.Create` 那种"只被一个部位引用才采用、否则判 Ambiguous 并退回猜测"的做法是错的——
它把一个真实存在的多对多关系当成数据缺陷来消解。正确做法是保留全部归属，让界面如实展示"这个 unit 同时是 A 的躯干和 B 的左臂"。

---

## 6. 已定的结构问题（2026-10-25）

1. **命名里的数字**：`torso_2` 的 `2` 是**该部位的第 3 个 unit 槽**（`pieceType`），不是部位编号 → `{part}_{pieceType}_{body}`。
2. **层要不要进名字**：已经进了，就是 `pieceType` 这一维；不需要额外的 `_l` 后缀。
3. **共享 unit 归谁**：**属于所有引用它的护甲**，多对多，不做唯一化。

---

## 7. 附：本轮已确认的事实
- `CustomizationKitSlot`：`Helmet=0, Cape=1, Torso=2, Hips=3, LeftLeg=4, RightLeg=5, LeftArm=6, RightArm=7, LeftShoulder=8, RightShoulder=9`（`HD2GameItemAssets/GameItemAssetModels.cs:10`）
- 件表 slot 与 unit `CustomizationInfo.Slot` 是两套编号，5/6/7 档不一致（`migration-preflight-two-answers-20261023.md:88`）
- 件长 96 → 112 的回归已修复，3404 件 100% 可解析（同上 §D/§F）
- 生产路径已不使用骨名 LUT 做部位判定（`AdvancedEquipmentIndexService.cs:458-461` 注释）
- 交叉护甲页要求 `PartKind != Unknown && Confidence == 100`（`EquipmentUnitCatalogService.cs:154`）

---

## 8. 反向追溯：CLI 与 GUI 的实际输出（2026-10-25 实测）

方法是**从输出反推字段来源**，不是从代码正推：先跑命令 / 看界面，再问"这个数字是谁算的、
有没有第二个人算出别的值"。

### 8.0 实测样本

```
hd2mod gamedata equipment list --game-data "<游戏data>" --kind all --json
```

| 项 | 数量 |
|---|---:|
| kit 总数 | **422** |
| ├ Armor | 138 |
| ├ Helmet | 161 |
| └ Cape | 123 |
| 件总数 | **3527** |
| ├ pieceType 0 / 1 / 2 | 1960 / 1380 / 187 |
| ├ 原始 body 0 / 1 / 3 | 1148 / 1148 / 1231 |
| └ weight 0 / 1 / 2 | 912 / 1841 / 774 |

**输出里的 `sourcePath` 是备份文件**（`ArmorRegistry\backup\generated_customization_armor_sets.original.dl_bin`），
不是游戏目录里那份。这是信任模型的设计（只读已验证原件），但意味着**CLI 看到的是最后一次
Steam 校验时的那份数据**——游戏刚更新、还没校验时，CLI 会安静地给出旧结果。界面上没有任何提示。

### 8.1 由输出反推出的结构结论（已写进 `ArmorStructure.cs`）

| 结论 | 证据 |
|---|---|
| 每个 kit 恰好 3 行 body = Stocky / Slim / Any | 422 个 kit 的件覆盖了全部三种 bodyVariant，无第四种原始值 |
| Any 与 Stocky/Slim **互斥** | 按 `(kit, slot, pieceType)` 分组，Any 与具体身形共存 **0 次** |
| 每个部位最多 3 个 unit 槽 | 按 `(kit, slot)` 统计不同 pieceType 数：3 个的 96 组、2 个的 691 组、1 个的 709 组，**没有超过 3** |
| 身体 kit 必带披风 + 核心 6 区域 | 138 个身体 kit 全部含 slot 1 与 2..7 |
| 双肩不是每套都有 | 125/138 有 slot 8 或 9；13 套完全没有，2 套只有左肩、4 套只有右肩 |
| 头盔是独立 archive | 421 个 archive 中**没有一个**同时承载两种 category；109 个 setId 同时含身体与头盔、archiveId 不同、名称相同 |
| 头盔 kit 不一定只含头盔 | 2 个头盔 kit 含披风件（unit `0xea846ef460cc5ba0`） |
| weight 是**逐件**的 | 138 套里 61 套全 1，但 18 套 {0,1}、17 套 {1,2}、9 套 {0,1,2} |
| 共享 unit 是常态 | 同一 unit id 会在**同一件护甲**里占两个位置，最常见是 Stocky 与 Slim 两行写同一个 id |

最后一条是"共享 unit 属于所有护甲"最硬的证据：连一件护甲内部都会用同一个 unit 填两个身形槽位。

### 8.2 CLI 发现

**C1（已修）— `units[]` 缺 `pieceType`，四元组无法重建**

`CliEquipmentFactUnit` 原本只有 `TypeId/FileId/PartKind/BodyVariant/Slot/Weight`。
后果很具体：FS-37（archive `267fcb7e9e312e33`）在 slot=4 有**两个** unit，输出里两行除了 id 之外
完全一样，脚本无法判断哪个是内层、哪个是护甲。也就是说 CLI 的输出**表达不了护甲结构**。

已补上 `PieceType`、`RawBodyType`、`Tone`（纯增量，JSON 向后兼容）。

**C2（已修）— 文本输出把四维压成两维**

`FormatEquipmentFacts` 原来只打 `{PartKind}/{BodyVariant}`，连 slot 都没有。
现在打 `slot={Slot} pt={PieceType} {PartKind}/{BodyVariant} weight= tone=`。

**C3 — CLI 里有两个都叫 `slot` 的东西，没有区分**

- `gamedata equipment *` 的 `slot` 是**件表数字编号** 0..9；
- `unit show` 的 `slot` 是 **unit 自己 CustomizationInfo 的字符串**（`Torso` / `Hip` / `Undergarment` 那套）。

两者在 5/6/7 档不一致（`ArmourUnitTableSource` 注释）。输出里同名字段、不同语义，没有任何提示。

**C4 — `source convert` 有第 5、第 6 份部位表，且不看 pieceType**

`CliSourceConvert.cs:13` `PartKinds`（BodyRegion → partKind 字符串）和 `:25` `SlotNames`
（BodyRegion → unit 槽位名，注意写的是 `Hip` 不是 `Hips`）都是手写表，与 `CustomizationKitSlot` 无关。

更实际的问题：`SelectDonors:63-70` 只按 `VariantRank`（Any 优先，其次 Stocky）和 archiveId 排序，
**完全不看 pieceType**。所以"胸口"这个部位挑到的外壳可能是 pt=0（内层）而不是 pt=1（护甲），
取决于排序碰巧落到哪个。这正是"用起来不踏实"的来源。

**C5 — 装备表输出没有 `Origin`**

CLI 无法区分"这条来自游戏注册文件"还是"这条来自 unit 结构推断"——虽然目前 `gamedata equipment`
只读注册文件，所以暂时都可信；但一旦将来合并两源，输出里没有任何字段能表达这件事。

### 8.3 GUI 逐列回溯

| 界面 · 列 | 绑定 | 实际来源 | 问题 |
|---|---|---|---|
| 交叉护甲 · 来源可见部件 · 部位 | `PartKind` | `game_data_unit_parts.part_kind` ← 件表 `slot` 覆盖 → 否则骨名/字符串猜 | 混合来源，界面看不出来（N1/N19） |
| 交叉护甲 · 来源可见部件 · 身形 | `BodyVariant` | 同上 | 同上 |
| 交叉护甲 · 来源可见部件 · **语义** | `SemanticName` | `{Slot}_{Piece}_{Body}_lod{N}` / 骨名 / `{meshId}_lod{N}` | 5 种拼法（N13），且是 mesh 级（N14） |
| 交叉护甲 · 目标预测 · 部位 | `Target.PartKind` | 同上 | 同上 |
| 交叉护甲 · 目标预测 · **目标** | `Target.SemanticName` | 同上 | 截图里 `1447930109_lod0` 就是兜底名 |
| 交叉护甲 · 目标预测 · 命中/复用 | `HitCount` / `UsedByArchiveIds.Count` | 计划期统计 | 只说明"被几套用"，不说明"是哪几套" |
| 交叉护甲 · 目标预测 · 目录身形 | `Target.BodyVariant` | 件表 | ✅ 权威 |
| 护甲注册 · 组件编辑器 · 分组标题 | `SlotLabel` | 硬编码 7 项 | 肩甲显示成"槽位 8"（N5） |
| 护甲注册 · 组件编辑器 · 每行三格 | `Stocky/Slim/Any` | 自定义素体 piece 的 `Body` | ✅ 与结构一致 |
| 衣柜 · 树 | `WardrobeTree` | `sections.json` 的 unit 语义 | 层次正确，但 slot 字符串是 unit 级（N2） |
| 装饰规划 / 批量装饰 · 部位 | `PartKind` | `game_data_unit_parts` | 同 N1 |
| 搜索 | `ModSearchMatcher` | 硬编码别名 | 只有头盔有中文别名（N20） |

### 8.4 反向追溯的净结论

结构本身是**清楚的、可验证的、与代码基本一致**的——只有一处硬伤（`AuthorityUnitRole` 把多对多
当成缺陷消解）。真正的问题全部集中在**表达层**：

1. 四元组在三个地方被压成三元组甚至二元组（CLI 的 `units[]`、GUI 的"语义"列、`SlotFor` 的映射）；
2. 同一件事有 4~6 份手写表，谁也不知道哪份是权威；
3. 权威性（`Origin`）在离开 Core 的那一刻就丢了。

所以后续整改的重点不是"再查一遍事实"，而是**让事实在每一层都保持四维 + 来源标记**。

---

## 9. 第二轮反向追溯：GUI 各页 + CLI 全命令（2026-10-25）

### 9.1 GUI 逐页核对

| 页面 / 文件 | 与部位有关的列 | 绑定 | 实际显示 | 问题 |
|---|---|---|---|---|
| 替换护甲 · `CrossArmorPlanPageView.xaml:53,101` | 部位 | `PartKind`（枚举直接绑） | `LeftArm` | 英文，且来源混合（N1） |
| 同上 `:55,106` | **语义 / 目标** | `SemanticName` | `Torso_Armor_Stocky_lod0` / `g_torso_...` / **`1447930109_lod0`** / `torso_culling` | 5 种拼法（N13） |
| 同上 `:111,112` | 目录身形 / 来源身形 | `BodyVariant` | `Slim` / `Any` | 英文 |
| 护甲映射 · 视觉组件卡片 · `ArmorRegistryPageView.xaml:30,51,88,90` | 身形 / Unit / 部位 / **层级** | `BodyLabel` / `Unit` / `SlotLabel` / `LayerLabel` | `Slim` · `0x41fb...` · `胸部` · **`层级 0`** | ① `层级 0` 是裸数字，没有"内层/护甲/装饰"的含义；② `SlotLabel` 缺 8/9 → 肩甲显示 `槽位 8`；③ 同一张卡里英文身形 + 中文部位混排；④ 卡片不显示所属 archive |
| Mod 详情 · `AdvancedModDetailsPageView.xaml:61` | **部件** | `PartSummary` | `头部－内部－纤细，胸口－护甲－健壮` | 拼接串，不能按字段筛；一个 unit 多 mesh 时越拼越长 |
| Mod 详情 · `:62` | 目标 | `TargetSummary` | `Mod 引用：Model / Unit 0x...` | 与部位无关，但同列并列易混 |
| GameData archive 详情 · `GameDataArchiveDetailsPageView.xaml:61` | **部件** | `PartSummary` | 同上 | 走的是**另一份**逐字复制的 `DescribeParts`（N7） |
| 生成装饰 · `DecorationPlanPageView.xaml:103-106` | 部位 / 身形 / 层级 | `PartKind` / `BodyVariant` / `Layer` | `LeftArm` / `Slim` / `Undergarment` | 全英文；行身份是 per-mesh（见 9.3） |
| 批量生成装饰 · `BatchDecorationPlanPageView.xaml:42-44` | 部位 / 身形 / 层级 | 同上 | 同上 | 同上 |
| 衣柜树 · `WardrobeTree.cs:420` | 槽位节点 | `SlotLabel` | `左臂` / `头盔` | 唯一一份完整 9 项的中文表 |

**同一件东西的四种叫法，在同一套界面里同时出现：**

| 位置 | 左臂 | 头盔 | 躯干 |
|---|---|---|---|
| 交叉护甲 / 装饰表格 | `LeftArm` | `Head` | `Torso` |
| 护甲映射卡片 · 衣柜树 | 左臂 | 头盔 | 胸部 |
| Mod 详情 / archive 详情 · 部件列 | 左臂 | **头部** | **胸口** |

### 9.2 护甲映射卡片：`层级 0` 什么都没说

卡片四行是 `BodyLabel` / `Unit` / `SlotLabel` / `LayerLabel`，而 `LayerLabel => $"层级 {PieceType}"`
（`ArmorRegistryPageViewModel.cs:2246`）。用户看到 `层级 0`、`层级 1`、`层级 2`，
但结构里这三者的含义是**内层 / 护甲 / 装饰**，界面上一个字都没说。

这是最该改的一处文案：它同时是"唯一显示 pieceType 的地方"，而 pieceType 恰好是四元组里最容易丢的那一维。

### 9.3 数据库实测：GUI 真正渲染出来的值

对 `HD2ModManager\bin\Debug\net10.0-windows\data\indexes\asset-index.sqlite`（构建于 2026-10-25 14:37 UTC）直查：

| 指标 | 值 |
|---|---:|
| `game_data_unit_parts` 行数 | 16569 |
| 其中 `semantic_name` = `Slot_Piece_Body_lodN`（合成） | 11553（69.7%） |
| 其中 `semantic_name` = `g_*`（骨名） | 2404（14.5%） |
| 其中 `semantic_name` = `{meshId}_lodN`（**兜底**） | **1901（11.5%）** |
| 其中 `semantic_name` = `*_culling`（**第 5 种拼法**） | **711（4.3%）** |
| `part_kind = Unknown`（`confidence=0`） | 1381（8.3%） |
| `part_kind = Accessory`（其中 263 个是**披风**） | 263 |

**结论：15.8% 的行显示的"名字"根本不是部位名**——要么是 meshId，要么是 `torso_culling` 这种内部标记。
这正是用户说"显示的不真实"的直接来源。

**一个 unit 多行的实测规模**：

| 指标 | 值 |
|---|---:|
| 有可见非 LOD mesh 的 unit | 2002 |
| 其中 **>1 行**的 unit | **1052（52%）** |
| 多出来的行 | 1215 |

所以"一个 unit 在表里出现多次"不是理论风险，是**一半以上的 unit**。
交叉护甲页与装饰页都是 per-mesh 建模（装饰页的行身份就是
`(ArchiveId, UnitKey, MeshInfoIndex, PartKind, BodyVariant, Layer)`），
所以用户会在这些表里看到同一个 Unit ID 重复好几行、尺寸各不同。

### 9.4 CLI 全命令扫描

`hd2mod capabilities --json` 共 **155** 条能力，其中与部位/槽位相关的：

| 命令 | 与部位的关系 | 输出里有没有四元组 |
|---|---|---|
| `gamedata equipment list/show` | 件表原文 | 修好后有（`slot` + `pieceType` + `bodyVariant`） |
| `unit show` | unit 自己的 `CustomizationInfo` | 只有字符串，**没有**件表 slot/pieceType |
| `unit geometry` / `unit bones` | 几何/骨骼，无部位 | — |
| `gamedata archive show` | `partSummary` 拼接串 | ❌ 只有串 |
| `custom-model inspect/convert` | 来源部位 → 新素体 | ❌ 走 `SlotFor`，肩甲折成躯干（N4） |
| `decoration inspect source` | 装饰 Unit 部位 | ❌ 只给 partKind/layer 串 |
| `equipment cross-armor inspect/plan` | 映射表 | 部分（有 PartKind/BodyVariant，无 pieceType） |
| `equipment helmet-merge inspect/plan` | Armor/Any Helmet 集合 | 部分 |
| `equipment armor-normalize inspect/plan` | 标准承载 Unit | 部分 |
| `gamedata index inspect` | 部位事实状态 | ❌ 状态是错的（见 C6） |
| `mod analyze` | Unit/几何摘要 | 部分 |
| `patch entries list` / `entry show` | 条目类型，无部位 | — |
| `wardrobe sections` | unit + section，无部位 | — |

**C6（新，重要）— `gamedata index inspect` 的状态是错的**

实测输出：

```
GameData 索引维护检查：基础索引=current；Armor / Helmet Unit 部位事实=缺失或过期
```

但直查数据库：**3233 个 Armor/Helmet Unit 全部有部位事实**（`game_data_unit_parts` 覆盖 298/298 个 Armor+Helmet archive，缺失 0）。

原因：`AdvancedEquipmentIndexService.HasCompleteEquipmentFactsAsync:343` 除了要求 `game_data_unit_parts` 有行，
还要求 `game_data_stream_layouts` 有行——而那张表**当前 0 行**（`stream_layouts distinct archives = 0`）。

于是这个检查**永远不可能通过**。用户和 agent 都被永久告知"部位事实缺失或过期"，
而实际数据是全的。这也解释了设置页为什么一直提示要"通过专用按钮单独计算"。

**C7 — `gamedata archive show --json` 只给拼接串**

`assets[].partSummary` 是 `"头部－内部－纤细，胸口－护甲－健壮"` 这种串。
agent 拿到它没法按 slot / pieceType 过滤，只能做子串匹配——而 `GameDataBrowserPageViewModel:228`
的搜索确实就是这么干的（`PartSummary.Contains(SearchText)`）。

**C8 — 两个都叫 `slot` 的字段，在 CLI 里同时出现**

- `gamedata equipment *`：`slot` = 件表数字 0..9
- `unit show`：`自定义：... slot=Torso ...` = unit 自己的字符串

agent 把两者并列读时必然混淆。至少应该在 `unit show` 里改名为 `unitSlot` 或加注。

### 9.5 一个差点误报的坑：`UnitMeshPartKind` 的数值顺序

排查时我一度认为 `game_data_authority_pieces.part_kind` 把腿和臂对调了
（slot=4 存的是 6，slot=5 存的是 7，slot=6 存的是 4，slot=7 存的是 5）。

实际是**我错了**：`UnitMeshPartKind` 的顺序是 `Head, Torso, Pelvis, LeftArm, RightArm, LeftLeg, RightLeg, …`，
所以 slot 4（LeftLeg）对应枚举 6 是**正确的**。

但这恰好说明这个转换有多危险：**`CustomizationKitSlot` 的 4=LeftLeg，而 `UnitMeshPartKind` 的 4=LeftArm。**
`(UnitMeshPartKind)slot` 会静默把腿和臂对调，编译通过、数值合理、只有玩家能看出来。
和身形编码 `0=Stocky` vs `Unknown` 是同一类陷阱。**这一条必须写进 `ArmorStructure`**，已补。
