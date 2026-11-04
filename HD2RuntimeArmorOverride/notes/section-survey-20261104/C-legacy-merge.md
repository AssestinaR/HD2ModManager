# C 线调查：护甲映射组件模型 / 头身合并 / 常规 Mod 模型整体合并

2026-11-04。只读调查：未改代码、未 commit、未动 `library.json`。

## 0. 结论速览

| # | 结论 | 依据 |
|---|---|---|
| 1 | 注册表的"组件"= **(body, slot, pieceType) → unit**，不是骨骼也不是 section | `RuntimeArmorRegistry.cs:17-26` |
| 2 | `pieceOverrides` = 关掉目标甲上某一件（**减**）；`targetModes` = 这批开关归谁（**作用域**） | `RuntimeArmorComponentProjection.cs:13-21` |
| 3 | 两字段**已无在役运行时消费**；在役 Lua 只读 `link=`，连 `map=` 都不读 | `RuntimeArmorRegistryLuaSource.cs:22-26`、`armor_override_runtime.lua:70-101` |
| 4 | 唯一读它们的 Lua 首行 `require('ffi')`，本版引擎无此模块 → 加载即退出 | `RuntimeArmorRegistryLuaSource.cs:11-20`、`armor_registry_runtime.lua:856,963` |
| 5 | 故为**历史数据兼容**，非"仍被运行时消费"；现网这两类行各 0 条 | `migration-preflight-two-answers-20261023.md:20-22` |
| 6 | 头身合并是**两份不同实现**：VPK 换 unit 槽位，常规追加几何（§3） | `CliVpkRekey.cs:784-833`、`HelmetToArmorMergeService.cs:313-353` |
| 7 | `CanonicalMeshSemanticMerger` 跨甲分支已 obsolete，同 ID 重构链路仍在役 | `CanonicalCrossArmorOrchestrator.cs:135-136,264`；`SameKeyCanonicalUnitRebuilder.cs:46,154` |
| 8 | 跨甲"整体合并"默认**不合并几何**——换键复用来源 payload；section 级"关掉"早有实现（minifier 把第 0 段外全置空） | `EquipmentUnitCatalog.cs:140`、`CanonicalCrossArmorOrchestrator.cs:570-571`、`CanonicalPlaceholderMinifier.cs:43-46` |

## 1. 组件模型表达什么

```
RuntimeArmorRegistryDocument                    ← 用户意图文档（工作区 + 协议两种形态）
├─ Enabled / Revision / CompositionMode
├─ Mappings[]  目标 item → 来源（另一件护甲 SourceItemId | 虚拟素体 ModelKey）
├─ Models[]    虚拟素体：Key + 可选 SourceItemId 模板
│   └─ Pieces[] 一件 = (Body, Unit, Slot, PieceType, Tone, Enabled, IsDefaultBase, IsReserved)
├─ PieceOverrides[]  某 item 上某一件：开/关
└─ TargetModes[]     某 item 的件开关 follow / manual
```

`RuntimeArmorRegistry.cs:3-63`。坐标系被注释钉死：`Body` 是**原始编码**（0=Stocky、1=Slim、3=Any），
**不是** `UnitMeshBodyVariant`（两者在 0 上错位，只能走 `ArmorStructure.ToBodyVariant`）；
`PieceType` 是"该部位第几个 unit 槽"，不是部位编号（`RuntimeArmorRegistry.cs:9-16`）。

**`pieceOverrides`**（`RuntimeArmorRegistryService.cs:807-821`）格式
`piece_override=<owner item>,<body>,<unit>,<slot>,<piece_type>,<enabled>`，owner 是**目标护甲自己**，
键里带 `Unit` 是为了位置精确。真实需求见 `migration-preflight-two-answers-20261023.md:263-277`：
严格 join 后目标甲上有一批件在来源里找不到对应件（337 件，slot=3 pt=0 占 72%），会**保留原外观继续渲染**；
旧链路靠 `piece_override enabled=false` 把它们藏掉（同文件 `:277`）。→ 语义是"让某件不被画"，是**减**。

**`targetModes`**：`var owner = target is { } id && !followSource ? id : sourceOverrideOwner;`
（`RuntimeArmorComponentProjection.cs:13`）。`follow`（默认）目标沿用**来源**那套开关（给 B 关的件，所有映射到
B 的目标一起关）；`manual` 用**自己**那套。即 `pieceOverrides` 是数据，`targetModes` 是这批数据属于谁。

**`CompositionMode`**：`full`/`existing_only`（`RuntimeArmorRegistry.cs:55`；校验
`RuntimeArmorRegistryService.cs:638-639`；只在非 full 时写一行 `:864-865`）。`existing_only` 保持目标行
结构/件数/顺序、**只换 unit**（`WardrobeLinkCompiler.cs:11-14` 自述等价）；`full` 按来源/模型**重建**
rows/pieces——改件数、改顺序、注入目标没有的件、恢复 weight、写 tone
（`armor-mapping-boundary-20261023.md:82-86`）。唯一消费者是**已死**的 `armor_registry_composition.lua:33-47`。

**`RuntimeArmorComponentProjection`（24 行）**：纯函数投影——来源件表 + 谁的开关 + 目标 + follow/manual
+ 全部覆盖 → "目标最终哪些件开着"。结果用 `piece.Enabled && enabled`，**覆盖只能关不能开**（`:21`）；
`RuntimeArmorComponentProjectionTests.cs:24-38` 钉住"已禁用定义 + 覆盖 true 仍为关"。它存在的理由是把
运行时 Lua 语义搬到 C# 可测（注释 `:20`）。

## 2. 这套东西现在还活着吗

```
                ┌─ 真映射模式（默认，ArmorMappingMode.cs:39）──────────────────┐
Mappings/Models ┼ ArmorRegistryDataMapper 改 generated_customization_armor_    │
                │ sets.dl_bin 的件表（只取 unit/slot/piece_type）               │
                │ RuntimeArmorRegistryService.cs:537-576                        │
                ├─ 兼容模式 0/1（UsesRuntimeLua）──────────────────────────────┤
Mappings/Models ┼ WardrobeLinkCompiler → wardrobe.links（schema=3, link=…）    │
                │ RuntimeArmorRegistryService.cs:516-524                        │
                │   ↓ armor_override_runtime.lua → set_resource_override('unit')│
                └───────────────────────────────────────────────────────────────┘

PieceOverrides / TargetModes / CompositionMode ──✗──> 两条链路都不读
```

**证据链（本报告最关键一段）**

1. 在役运行时只有一个：`RuntimeArmorRegistryLuaSource.Load()` **只**返回 `armor_override_runtime.lua`
   （`:22-26`）；同文件 `:11-20` 明写旧 ffi 链路"在 26088801 上第一件事就是 `require('ffi')`，而该模块
   在这版引擎里不存在，于是整条链在加载时就退出——那段代码已经不可能生效"。
2. 在役运行时只认 `schema/revision/enabled/link`，第 96 行注释 `-- map= 是管理器的意图记录，运行时不需要。`
   （`armor_override_runtime.lua:70-101`）。
3. `wardrobe.links` 的编译器签名 `Compile(snapshot, mappings, revision, enabled, models)`
   （`WardrobeLinkCompiler.cs:41-46`）——**没有 overrides / targetModes / compositionMode**。
4. 真映射链路只从 `compiled.Mappings` 构造 `ArmorRegistryDataMapping`（模型映射展开 `model.Pieces` 里
   `Enabled && !IsReserved` 的件），**从不引用 `PieceOverrideDefinitions`**（`RuntimeArmorRegistryService.cs:537-562`）。
5. 还在读的地方：`armor_registry_runtime.lua:854-877`（`build_override_index`）与 `:963`
   （`target_modes[mapping.target]==false`）——**已死文件**，却仍作 EmbeddedResource 编进 Core
   （`HD2ModCore.csproj:104-105`）；`armor_registry_protocol.lua:147-169,239-250` 只被这个死运行时与
   `scripts/Verify-Protocol.lua`、`tests/test_armor_registry_protocol.lua` 使用；CLI
   `CliArmorRegistryProtocol.cs:256-282,332-348,367-368` **只搬运不消费**，`ArmorDiagnosticEvidence.cs:52`
   原样塞进诊断 ZIP；GUI `ArmorRegistryPageViewModel.cs:654-655` 读入、`:1559-1560` 回写、`:1505-1506` 剪孤儿。

**判定：历史数据兼容。** 唯一消费者是跑不起来的 Lua；两条在役链路签名里根本没有这两个参数；现网协议
7544 字节 / revision=124 逐行统计 `piece_override`=0、`target_mode`=0、`composition_mode`=0
（`migration-preflight-two-answers-20261023.md:13-22`）。代价是真实的：它们**仍被完整校验**（重复键、
`slot=0 只能 body=3`、unit 必须 16 位 hex，`RuntimeArmorRegistryService.cs:1033-1058`），**坏数据会让整份
工作区读不出来**。

**`ArmorRegistryPageViewModel` 还剩什么**

| 能力 | 位置 | VPK 线也用？ |
|---|---|---|
| 目录浏览（游戏甲 + 头盔 + 素体合成一张可选表） | `:154-157, 613-652` | ✅ 素体来自 VPK 转换 |
| 三类筛选/搜索（仅甲/仅盔/自动组合；Unit 覆盖命中/未命中） | `:199-238, 1307-1352` | 否 |
| 建映射（拖放"设为视觉来源"→"添加映射目标"）/ 解除映射 / 归还可选 | `:1059-1143, 948-1006, 1144-1204`；`ArmorRegistryPageView.xaml:157-171` | ✅ 核心用途 |
| 素体卡片（列表/计数/**改名**，行动作 `ArmorRenameCustomModel`） | `:157, 656-662, 1849-1850` | ✅ 改名写回 `custom-model.json` |
| 应用协议（revision+1、指纹校验、编 links、落真映射、等回执）+ 运行时状态 | `:1526-1684, 574-612, 1686-1772` | ✅ 应用；回执仅兼容模式有 |
| 工作区自动保存（400 ms 防抖）+ 保住别的进程刚加的素体 | `:1367-1452`、`RuntimeArmorRegistryService.cs:126-161` | ✅ VPK 导入是独立进程写工作区 |
| 删素体时剪孤儿映射 | `:1476-1513` | 否 |
| **已删**：视觉组件编辑器（件表增删改、合并/拆分/补缺、Wardrobe 树）；`ArmorRegistryPageView.xaml:10-52` 还留着它绑定的 `ArmorPieceEditorTemplate`（`BodyLabel`/`CanRemove`/`RemoveCommand` 在 VM 里已不存在，`x:Key` 全仓无引用点）——**孤儿 XAML** | 无 | — |

## 3. 头身合并的两份实现

**（a）VPK 线：换 unit 槽位，不动几何。** 入口 `CliArguments.cs:370-380` →
`CliApplication.cs:2982-2990`（`HeadMerge=="on"`）→ `CliVpkRekey.BuildPlan(..., mergeHeadIntoArmour)`
（`:572-750`）。实现在 `ResolveHeadBodyMerge`（`:784-833`）：① 来源素体须同时有 `Helmet` 与 `Torso`
件，否则 `UnavailableNoHelmetKit`（`:790-796`）；② 目标里"件全是 Helmet"的套件 = 头盔套、
"有 Torso 件"的 = 护甲套，缺一即同上（`:798-806`）；③ 数目标甲**胸部槽位可承载层数**
（去 Culling/Static/Unknown），`<2` 即 `UnavailableSingleLayer`（`:808-821`）；
④ 层号**算出来不写死**：最外层放头、次外层放身体（`:823-827`）。

```
目标护甲 胸部槽位
  layers[0]（最外层）← 来源素体 Helmet unit   （整份 payload 顶上）
  layers[1]（次外层）← 来源素体 Torso  unit
  其余层              ← 不写，交给 AutoHideUnmappedTargetUnits 顶成隐藏壳
头盔套件所有件        ← 一件都不写，全部隐藏（CliVpkRekey.cs:601-605）
```

执行走 `CrossArmorTransferPlan` + `DirectSourceUnitReuse: true`（`:245-247`），即来源 unit payload
原样挂到目标键上（`CanonicalCrossArmorOrchestrator.cs:570-571`）。**所以 VPK 的头身合并不是几何合并**：
只是把头这个 unit 搬进护甲胸部的另一个**槽位**，两个 unit 各自独立、各自一次 draw call。
状态枚举见 `CliVpkRekey.cs:10-20`。

**（b）常规线：`HelmetToArmorMergeService`（真几何追加）**，`:97-418`：

```
InspectAsync ─ ModEquipmentSourceFactsReader → BuildCandidates（:527-616）：按 DisplayName
               把 Armor 与 Any-Helmet 配对 → HelmetToArmorMergeCandidate
ExecuteAsync ─ 逐 Patch：① 读胸部 unit（:269）
               ② ResolveVisibleLodFamily：胸部 unit 全部可见 LOD（:292, 756-766）
               ③ 每个 LOD 取头盔对应 LOD 的 mesh（:299, 741-754）
               ④ CanonicalDecorationUnitAppender.TryAppendLodFamily（:313-316）
                  → CanonicalAppendMeshAssembler：**追加** section（§4.3）
               ⑤ 头盔 unit 用 CanonicalHiddenUnitBuilder 生成隐藏版（:343-353）
```

产物是**新 Patch 目录**（非原地）：胸部 unit 的 mesh 同时含躯干与头的三角形，头盔 unit 变空壳；
`SharedArmorVariants` 还额外产出差分变体 + `manifest.json`（`:390-401, 420-513`）。
UI 入口三处：独立向导页（`ShellViewModel.cs:853-859, 2366, 2489` + `HelmetToArmorMergePlanPageViewModel.cs:12-108`）；
Mod 详情页两个动作（`ModDetailsPageViewModel.Actions.cs:48-49`，实现 `ModDetailsPageViewModel.cs:1660-1719`）；
CLI `helmet-merge plan/validate/apply`（`CliApplication.cs:1397-1440`）。

**（c）是不是同一件事的两份实现？** 目标相同、手段不同，算法不能合并。

| | VPK `--head-merge` | 常规 `HelmetToArmorMergeService` |
|---|---|---|
| 数据前提 | 来源是"我们自己造的 7 个 unit"的素体 | 来源是已做好的 HD2 patch（真实 Armor/Helmet 归档） |
| 手段 | **换 unit 槽位**（layer0 头 / layer1 身体） | **几何追加**（头的 mesh 并进胸部 mesh） |
| 字节与 draw call | 不改几何只换键；头是独立 1 个 draw call | 重写 TOC/GPU，section 数与顶点数都变；头与躯干同一 unit、不同 section |
| 头能否单独关 | ✅ 独立 unit | ❌ 只能靠段掩码 |
| 限制 | 胸部 ≥2 层；须有配套头盔套件 | 需同名 Armor/Helmet 配对；LOD 须对得上 |
| 产物 | `Overwrite/` 下变体 patch | 新输出目录（含 manifest） |

能合到哪一步：算法层不能合，但下游已共享——VPK 走 `CanonicalCrossArmorOrchestrator` 的 direct-reuse
分支，常规线走同一家族的 `CanonicalDecorationUnitAppender`，最终都落到 `CanonicalPatchSessionEntry` +
`CanonicalPatchWriter`。真正重复的只有"哪件是头/哪件是胸/哪层放谁"（`CliVpkRekey.cs:784-833` vs
`HelmetToArmorMergeService.cs:672-704`），可抽成 `HelmetArmorPairResolver`。**但两者"层"的排序方向相反**：
VPK `Undergarment 0 < Armor 1 < Accessory 2`（`CliVpkRekey.cs:844-852`），常规线
`Armor 3 > Undergarment 2 > Accessory 1`（`HelmetToArmorMergeService.cs:706-712`）——合并前必须先统一口径。

## 4. 常规 Mod 的"模型整体合并进护甲"

**默认不合并。** `DirectSourceUnitReuse = true` 是**默认值**（`EquipmentUnitCatalog.cs:140`），
GUI 硬编码 true（`CrossArmorTransferPlanWindowViewModel.cs:175`），CLI 也硬编码 true
（`CliCrossArmorTransferExecutor.cs:59`、`CliVpkRekey.cs:245`）：

```csharp
// CanonicalCrossArmorOrchestrator.cs:132-136：跨甲生产走 direct source Unit reuse，
// Canonical 分支只是显式诊断回退。
private static bool UsesCanonicalCrossArmorFallback(CrossArmorTransferCandidateRequest r) => !r.DirectSourceUnitReuse;
```

`CanonicalMeshSemanticMerger` 只在这条 `#pragma warning disable CS0618` 分支里被用到，且会打日志
`[OBSOLETE-ROUTE] CrossArmor=CanonicalFullRebuild Action=CompatibilityOnly`（`:261-264`）。

三种形态：**A 一源→一目标**（默认）来源 payload 改键 → 目标 unit id（`:570-571`），几何一字节不动、section
表/材质槽沿用来源自己的；**B 多源→一目标**（目标格子少）host=sources[0]，其余 append（`:462-553` → §4.3）；
**C 一源→一目标但来源过时** `SameKeyCanonicalUnitRebuilder` → merger（§4.4）。

**4.3 追加：`CanonicalAppendMeshAssembler.TryAppendMany`（`:30-89`）** 一次过处理多份来源（避免反复拷贝
已合并的大 mesh，注释 `:28-29`）：① 先原样拷贝**目标**全部 section，索引不变、provenance 记
`IsTargetSection=true`（`:53-59`）；② 顶点追加到末尾，`offset = vertices.Count`（`:71`）；③ 来源每个
section 的三角形索引**整体加 offset**、section 追加到表尾（`:74-80`）；④ **section 自带 `MaterialSlotId`**，
追加时原样带过来；⑤ 产出 `CanonicalAppendSectionProvenance(FinalSectionIndex, IsTargetSection,
SourceSectionIndex)`——"最终第 i 段是谁"的账，材质与骨骼都靠它。材质槽分配在
`CanonicalUnitMaterialLayoutCompiler.TryCompile`（`:28-`）：先**保留**目标自己的槽位（空 section / culling /
默认材质）登记进 `occupiedSlots`（`:54-71`）；再给搬运来的 section 分配——同一 `(目标 NameHash,
MeshInfoIndex, sectionIndex, MaterialId, occurrence)` 解析到同一槽（`:90` 的 `MaterialIdentity`），故
**同材质复用、异材质新开槽**；一个最终 section 解析出多个来源材质即硬失败（`:38-39`）。命名空间由
`MaterialNamespace(candidateId)`（SHA256 前 8 字节）提供（`HelmetToArmorMergeService.cs:777-781,303`），
防止两个来源各自的 slot 3 被误认成同一材质。

**4.4 替换：`CanonicalSectionLayout`（62 行）的四条不变量**

| # | 不变量 | 行 |
|---|---|---|
| 1 | 目标至少一个 section，否则 `TargetSectionsMissing` | `:29-30` |
| 2 | 只取 `Triangles.Count != 0` 的来源段；一个都没有则 `EmptySourceMesh` | `:32-38` |
| 3 | **最终段数上限由来源决定，不由目标决定**（"目标原来的 section 数不是最终上限"） | `:5, :42` |
| 4 | 来源可见段 > 目标段 → 按**来源**材质组重建整表（writer 扩 MeshInfo）；否则**保留目标槽位身份**、按序号配对，多出的目标段给空三角形 | `:48-56` |

```
来源可见段 3，目标段 5（正常替换）
  输出 = [t0←s0, t1←s1, t2←s2, t3←[], t4←[]]  ← 目标槽位身份全保留，尾部段为空
  （这里已经产出了"numIndices = 0 的 section"）
来源可见段 6，目标段 5（来源材质组更多）
  输出 = 6 段，MaterialSlotId 全取自来源；writer 负责扩 MeshInfo
```

`CanonicalBoneRebuilder.cs:45,236` 也调它——"最终 section 表"是骨骼/调色板编译的共同输入。

**4.5 `CanonicalUnitRebuildTelemetry`** 纯计时聚合（`:36-80`）：八阶段（TransformExpansion /
MeshAssembly / StreamContract / FirstPreparation / BonePalette / FinalPreparation / MaterialBindings /
Serialization），MeshAssembly 再拆四子项。唯一用途是让跨甲编排 / 同 ID 重构 / 头盔合并三条路径的
耗时可比（`:35` 注释），无业务语义。

**4.6 与 VPK 转换共用同一套 unit 重建代码吗？** 共用"unit 重建与落盘"层，不共用"mesh 合并"层：

```
共用：CanonicalUnitRebuilder (UnitAuthor.cs:54) + CanonicalPatchSession
      + CanonicalPatchWriter.cs:44-46,126-142   ← VPK 走这里（VpkPatchAssembler.cs:472-517,553-570）
不共用：无合并=改键（:570-571） / CanonicalAppendMeshAssembler（追加） / Merger+SectionLayout（替换）
```

`UnitAuthor.cs:38` 自认"步骤刻意与 `SameKeyCanonicalUnitRebuilder` 同序"——**同序的第二份实现**，
不是同一份代码；`VpkPatchAssembler.cs:481` 还注明"每份一个作者对象"，因为 `UnitAuthor` 内部持有
若干 `Canonical*` 编译器实例。

## 5. 过时判定

| 功能 | 判定 | 依据 |
|---|---|---|
| 护甲映射页 | **仍有独立价值**（两线共用：VPK 素体必须在这里挂到护甲上） | `CliApplication.cs:2818-2871` 写素体 → `ArmorRegistryPageViewModel.cs:1059-1143` 挂映射 |
| 组件编辑器 | **已删**，只剩孤儿 XAML 模板 | `backlog-20261104.md:100-105`；`ArmorRegistryPageView.xaml:10-52` 无引用 |
| `pieceOverrides` / `targetModes` | **历史数据兼容**（无编辑入口、无运行时消费、现网各 0 条） | §2 证据链 |
| `CompositionMode` | **历史数据兼容**（唯一消费者是死 Lua） | `armor_registry_composition.lua:33-47`；`RuntimeArmorRegistryLuaSource.cs:13` |
| `HelmetToArmorMergeService` + 计划页 | **仍有独立价值**（几何级合并，VPK 做不到；4 处调用） | `ModDetailsPageViewModel.Actions.cs:48-49`、`CliApplication.cs:1397-1440`、`CliHostDerivedOverwriteExecutor.cs:86,202`、`CliBatchPatchRepairExecutor.cs:309` |
| `--head-merge` | **仍有独立价值**（唯一"头跟甲走"手段，语义与常规线不同） | `CliVpkRekey.cs:784-833`、`ModDetailsPageViewModel.Vpk.cs:141-149` |
| `CanonicalMeshSemanticMerger` | **仍有独立价值**，跨甲分支已 obsolete | 在役 `SameKeyCanonicalUnitRebuilder.cs:46,154`；obsolete `CanonicalCrossArmorOrchestrator.cs:135-136,264` |
| `CanonicalSectionLayout` / `CanonicalAppendMeshAssembler` | **在役且核心** | `CanonicalMeshSemanticMerger.cs:73`、`CanonicalDecorationUnitAppender.cs:111`、`CanonicalBoneRebuilder.cs:45,236` |
| `armor_registry_runtime/composition/transaction.lua`；`wardrobe.masks` + `armor_section_*.lua` | 前者**完全没人用**（ffi 不存在且不再部署）；后者**半死**——只剩 CLI 显式导出，无部署路径 | `RuntimeArmorRegistryLuaSource.cs:11-26,16-17`；仍被 `HD2ModCore.csproj:64-69,104-105` 内嵌；`CliWardrobe.cs:73,124,193` |

## 6. 融合进 section 系统的切入点与风险

**6.1 现状：section 已经不是"只能减"。** 构建期关段 = `CanonicalPlaceholderMinifier`：保留 section 身份与
`MaterialSlotId`，第 0 段留一个 1e-4 三角形、**其余段给空三角形列表**（`:43-46`）——这就是 `numIndices=0`
的构建期写法；替换期关段 = `CanonicalSectionLayout` 的 `t3←[], t4←[]`（`:53-56`）；追加期多段 =
`CanonicalAppendMeshAssembler`（`:74-80`）。**账本已经有了**：`CanonicalAppendSectionProvenance` /
`CanonicalMaterialSectionProvenance`（`CanonicalAppendMeshAssembler.cs:7-10`、`CanonicalUnitMaterialLayoutCompiler.cs:6-14`）
——"最终第 i 段来自哪个 unit 的第 j 段、用哪个材质"，正是 section 表需要的主键。即：**section 表在字节层
已存在，缺的是把它当产品概念暴露出来。**

**6.2 能直接映射成 section 的**

| 老功能 | 映射后 | 可行性 |
|---|---|---|
| `pieceOverrides`（关某件） | 一件 = 该 unit 的一个或多个 section → `numIndices` 置 0 | ✅ 天然映射，且更细（原来只能整件关） |
| `targetModes`（follow/manual） | 掩码集的"继承"语义 | ✅ `WardrobeMaskProjection` 已在做"继承者指向来源同一个掩码集"（`CliWardrobe.cs:266-276`） |
| `CompositionMode` = existing_only / full | 前者只换 unit、段结构不动（✅ 等价）；后者段数/顺序都变 → 段键须重新生成 | ⚠️ 后者需"段 id 稳定性"设计（§6.4） |
| 头盔合并（VPK 换槽位 / 常规几何追加） | 前者是独立 unit 的独立 section（✅）；后者段的 provenance 可区分 | ⚠️ "关头"是关一组段而非一件 |
| `wardrobe.masks` | 它本身就是段掩码表 | ✅ 现成落点 |

**6.3 映射不了的** ① **"整体合并进护甲"是在加几何，section 开关只能减**——
`CanonicalAppendMeshAssembler.cs:48-49` 的 `vertexCapacity`/`sectionCapacity` 就是"变多"的直接证据；开关能把
并进来的头**关掉**，但**不能凭空产生**一个本来没有的头 → 合并是**构建期**决策、开关是**运行期**决策，不同层。
② **换材质/换贴图**：section 的 `MaterialSlotId` 指向材质槽，换贴图是改"槽指向的材质资源"
（`backlog-20261104.md:144-146` 把"颜色变体包转出来一个材质都不含"列在同一清单里，原因在此）。
③ **`tone`（每件 0–255）** 是 piece 记录字段，不是资源也不是段（`armor-mapping-boundary-20261023.md:97`）。
④ **`weight`（轻/中/重甲）** 是玩法字段，section 方案不能碰（`three-mapping-modes-20261025.md:32-48`）。

**6.4 风险**

| 风险 | 具体形态 | 依据 |
|---|---|---|
| **段 id 不稳定 / 同名不同物** | `full` 会改段数/顺序，段号一变用户存的掩码就指向别的几何；两来源各自的 slot 3 语义不同，靠 `MaterialNamespace` 哈希隔离，掩码若按 slot 号存会跨来源误伤 | `CanonicalSectionLayout.cs:42-56`；`HelmetToArmorMergeService.cs:777-781`、`CanonicalUnitMaterialLayoutCompiler.cs:38-39` |
| **跨护甲共享 unit** | 按 unit 关段会误伤（实测 337 遗留件里 210 个 unit 只被已映射甲引用，1 个被 5 套未映射甲引用） | `migration-preflight-two-answers-20261023.md:283-294` |
| **剔除代理段不能关** | 护甲件 `mesh0` 是 `lod=-1` 深度代理，关掉会改遮挡关系（"手套/丝袜整段不显示"） | `CliVpkRekey.cs:556-560,626-630`；`SameKeyCanonicalUnitRebuilder.cs:112-117` |
| **运行时写入能力缺失** | 现役运行时只有 `set_resource_override`，无 `numIndices` 写入；段开关必须**构建期烧进 patch**，做不到热切换 | `armor_override_runtime.lua:142`；`armor-mapping-boundary-20261023.md:96` |
| **两套"层"排序口径相反** | VPK 内层优先 vs 常规线外层优先；统一会静默改变承载层选择 | `CliVpkRekey.cs:844-852` vs `HelmetToArmorMergeService.cs:706-712` |

**6.5 切入点（按代价排序）** ① **先把 provenance 提升成持久化产物**（写进 `sections.json` 那层）：段身份
一旦有稳定 id，`pieceOverrides` / `targetModes` / `wardrobe.masks` 可收敛成同一张表。② **`pieceOverrides`
→ 段掩码的迁移是安全的**（现网 0 条，无需数据迁移），但**协议字段不要删**——
`RuntimeArmorRegistryService.cs:1033-1058` 的校验会让老工作区直接读不出来。③ **先统一 `--head-merge` 与
`HelmetToArmorMergeService` 的"配对与层选择"**（§3 的 `HelmetArmorPairResolver`），再谈是否统一到 section 表。
④ **不要试图用 section 表达"整体合并进护甲"**：那是加几何，留在构建期。

## 附：未覆盖 / 附带结论

未覆盖：`set_resource_override` 覆盖表容量上限（满了静默拒绝，`armor_override_runtime.lua:147-149`）；
`wardrobe.workspace.json` 与 `armor-registry.workspace.json` 的职责边界（`WardrobeWorkspace.cs:14`）。
附带过时结论：`HD2ModCli` 的 `armor-diagnostic *` 系列仍按**旧 ffi 运行时**语义写诊断
（`ArmorDiagnosticSession.cs:60-113` 做 full / existing_only 的 A/B 冷协议），与实际部署的运行时不一致。
