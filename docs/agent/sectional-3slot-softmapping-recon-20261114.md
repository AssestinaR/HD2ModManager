# 3 槽压缩 + 软映射「污染黑名单」：情报报告（2026-11-14）

> 本轮只做情报：**没有改任何代码、没有构建、没有进游戏**。
> 结论按「已核实 / 强推断 / 待定」标注。所有路径相对仓库根。

---

## 0. 一句话结论（先说「有没有希望」）

**数据上够用，技术上有一块真空，但真空不在软映射侧。**

| 问题 | 结论 | 依据 |
|---|---|---|
| 每套护甲都有「可承载的胸部层」吗？ | **有**。138 套身体护甲**每一套**的胸部都至少有 2 个 unit 槽（pt0+pt1），55 套还有第 3 个（pt2） | 本文 §3；`HD2RuntimeArmorOverride/notes/sectional-survey-20261104/…` 同类实测 |
| 能找到「不被别的护甲共用」的胸部 unit 吗？ | **97/138 能**；剩下 **41 套三层全被共用** | 本文 §3.3 |
| 「其他部位」能找到独占 unit 吗？ | **80/138 能**；**58 套一处独占都没有** | 本文 §3.3 |
| 3 槽（头/胸/其他）全都找得到独占层？ | **78/138** | 本文 §3.4 |
| 如果允许「同一 custom model 内部复用」，还算污染吗？ | 那 **138/138 全部可行**（当前 299 条映射全部指向同一个素体） | 本文 §5.2 |
| 技术上最大的坑在哪？ | **不在数据，在编译层**：今天的 `SectionalCompiler` 只会「改名副本 + 写隐藏壳」，**没有几何合并能力**；把「头 + 躯干」合成一个 unit 必须复用 `CanonicalDecorationUnitAppender` 那条重链（真骨架、重算 CullingBounds、重算每件物理骨表） | §6.5、§6.6 |

---

## 1. 两个必须先纠正的口径

### 1.1 「0/1/2 层」不是「内层 / 护甲 / 装饰」

`HD2ModCore/Domain/ArmorStructure.cs:54-68` 的注释写的是「0=内层、1=护甲、2=装饰」，**但实测不是这样**：

把 live `.dl_bin` 的 3527 个件与索引库 `game_data_unit_parts.piece_type`（unit 自己的 `CustomizationInfo.PieceType` 字符串）逐件对账，138 套身体护甲的胸部：

| 注册表槽号 | unit 自己声称的类型 | 套数 |
|---|---|---|
| pt0 | **Armor**（胸甲本体） | 124 / 138 |
| pt1 | **Undergarment**（身体/内衣） | 99 / 138 |
| pt2 | Accessory | 30 / 55（有这一层的套数） |

样例（`kit=124623533` FS-05「神射手」）：

```
slot2 pt=0 unit=0x8d02a82d0c214171  自身=Armor        mesh=g_torso_male            9518 三角形
slot2 pt=1 unit=0xf2715c19b24ecd8b  自身=Undergarment mesh=g_torso_undergarment_male 4896 三角形
```

仓库其实**早就测到过同一件事**：`docs/agent/armor-cell-plan-20261027.md:36-45`（F2）——注册表 `pieceType 0` ↔ 索引 `part_layer 2`（护甲）的有 1004 个 unit，一致率只有 12.8%。

⇒ **「从高到低选层」如果按槽号理解，会优先选到「内衣层」（pt1）**，而它恰恰是被共用最多的那层（83/138 套被别的护甲共用）。这条规则必须改成按「unit 自身内容」定义。
（另：`docs/agent/vpk-rekey-outer-layer-fix-20261101.md:101` 里有一处注释也把 0/1 写反，引用时别当权威。）

### 1.2 「污染」在仓库里的既有含义 = **unit 被别的护甲共用**

不是「几何里混了别的部位」，也不是「深度代理遮挡」。原话在：

- `HD2RuntimeArmorOverride/scripts/Inspect-UnitSharing.lua:1-5`：
  「同键替换……**不区分引用者**。只要某个 Unit 还被别的护甲引用，替换它就会把那件护甲也一起改掉。」
- `docs/agent/professional-workbench-architecture-20261026.md:264-272`：「共享 unit……**隔离，避免一个 unit 被多件护甲共用导致污染**」
- `docs/agent/mod-derivation-functions-20261026.md:139`：转素体的前提是「来源必须有**独占 Unit**」
- `docs/agent/patch流程.md:13`：「检测这些 Unit 是否还被其它未选择对象共用，并返回风险信息」

「遮挡」那条现象在仓库里一律叫**遮挡 / 剔除 / 挡掉**，从不叫污染（`C-placement.md:65-72`、`vpk-rekey-outer-layer-fix-20261101.md:19-38`）。

⇒ 「复用污染 unit 自动黑名单」在实现上就是 `Inspect-UnitSharing` 那条判据：**扫注册表，某 unit 被 ≥2 个 kit 引用 ⇒ 进黑名单**。

---

## 2. 权威数据源与复核

按你的提示，直接读了游戏目录的权威注册表，而不是只用管理器索引：

```
D:\SteamLibrary\steamapps\common\Helldivers 2\data\game\generated_customization_armor_sets.dl_bin
  SHA256 3E48BABF…54A1CE   453,252 B   2026-11-11 18:54
```

用仓库自己的 `Decryptor` + `HD2GameItemAssets.CustomizationArmorSetsReader` 解出：

- **422 个 kit / 3527 个件**（身体 138 / 头盔 161 / 披风 123）
- 与索引库 `game_data_authority_pieces` 逐件对比：**掩码到 64 位后 0 处差异**（此前的「差异」只是 SQLite 里存的是有符号 i64）
- 索引库建库时间 2026-11-11 10:27 UTC，来源是原件副本 `…\ArmorRegistry\backup\generated_customization_armor_sets.original.dl_bin`（`meta` 表 `game_data_authority_source_path`）；live `.dl_bin` 与备份字节不同，但**解出来的件表一致**，所以这轮结论对「索引老了」是免疫的。

复核脚本（本轮新建，可重跑）：
`tmp/dlbin-probe/`（解密 + 导出 JSON）、`tmp/analyze_slots.py`、`tmp/key_numbers.py`、`tmp/analyze_shared2.py`。

---

## 3. 每套护甲的 unit 结构（138 套身体护甲）

### 3.1 层数分布（按槽位，全部 138 套）

| 槽位 | 层组合 | 套数 |
|---|---|---|
| 胸部 2 | `{0,1}` | 83 |
| | `{0,1,2}` | 55 |
| 胯 3 | `{0,1}` 96 / `{0,1,2}` 26 / `{1}` 12 / `{1,2}` 4 | 138 |
| 左腿 4 | `{0,1}` 121 / `{1}` 9 / `{0,1,2}` 8 | 138 |
| 右腿 5 | `{0,1}` 122 / `{1}` 11 / `{0,1,2}` 5 | 138 |
| 左臂 6 | `{0,1}` 137 / `{0,1,2}` 1 | 138 |
| 右臂 7 | `{0,1}` 125 / `{1}` 12 / `{0,1,2}` 1 | 138 |

**胸部没有一套少于 2 层** —— 你记的「必须在 0/1/2 里选一个」在数据上永远至少有 2 个候选。

### 3.2 每层被「别的护甲」共用的套数

（判据 = 该 unit 的 kit 引用集合去掉自己后非空，与 `Inspect-UnitSharing.lua` 同口径）

| 部位 | pt0 | pt1 | pt2 |
|---|---|---|---|
| 胸部 | **54 / 138** | **83 / 138** | 4 / 55 |
| 胯 | 77 / 122 | 90 / 138 | 4 / 30 |
| 左腿 | 80 / 129 | 89 / 138 | 0 / 8 |
| 右腿 | 80 / 127 | 89 / 138 | 0 / 5 |
| 左臂 | 80 / 138 | 91 / 138 | 0 / 1 |
| 右臂 | 75 / 126 | 91 / 138 | 0 / 1 |

**内衣层（pt1）是共用最严重的一层**，不是 pt0。胸部 pt2（装饰）几乎总是独占（4/55 例外），因为它是小配件。

### 3.3 「独占」候选

- 胸部：**97/138** 套至少有一层独占；**41 套三层全共用**（例：`B-01`、`TR-40`、`AF-91`、`CM-14` 等，多数是 2~14 套共用一个胸甲或内衣）。
  按「最高独占层」分布：**pt2 = 51 套、pt1 = 32 套、pt0 = 14 套、无 = 41 套**。
- 其他部位（胯/腿/臂）：**80/138** 套至少一处独占；**58 套一处都没有**（这些套的胯腿臂 unit 全部被别的护甲共用）。
- 头盔：161 套里 **106 套**的头盔 unit 独占；其余共用最多到 17 套（同模型的换色/变体）。

### 3.4 3 槽素体的「可映射性」

| 口径 | 结果 |
|---|---|
| 胸 + 其他部位都能找到**独占**承载 | **78 / 138** |
| 只要允许「同一 custom model 内部复用不算污染」 | **138 / 138**（当前 299 条映射全部指向同一个素体 `custom-0984…`，见 §5.2） |

另外：**每 (kit, 身形) 的件数只有 4~11 件**，离运行时的 20 件硬上限还很远，所以「压缩是为了省预算」这条动机在当前数据上不成立 —— 真正的动机是「少碰污染 unit」和「少 unit ⇒ 少骨表/少 draw」。

### 3.5 共用到底发生在哪些护甲之间

77 个被共用的胸部 unit 里：

- **只有 10 个是「同一套的换色/变体」**（同 setId）
- 其余 **67 个跨 2~6 个不同 setId**（例：`0x18902007139a6060` 被 14 套、6 个 setId 共用）

⇒ 共用不是「换色兄弟套」，是真的跨套装复用（尤其内衣层）。这也解释了为什么「黑名单」会吃掉不少候选。

---

## 4. 流程地图（你要的那几条链路）

### 4.1 四段链路（现行口径）

`docs/agent/sectional-compile-base-20261108.md:7-18`：

```
导入（vpk / patch） → 转换（vpk2patch / legacy2sectional → sectional/） → 编译（sectional + 条件 → Overwrite/） → 部署
```

- `sectional/` 是**patch 级权威事实**，形状固定（素体 + 标准 7 槽 unit 组 + 材质组），只有转换链路能写；
- `Overwrite/` 是唯一生效层，只有编译能写。

### 4.2 「7 槽」是谁切出来的

**两条转换链路，规则完全不同：**

| 链路 | 入口 | 切法 |
|---|---|---|
| vpk2patch | `HD2SourceMigration/Segmentation/SourceSegmentation.cs:90-185` | 按**骨骼归属**把每个 bodypart 切成 7 个 `BodyRegion`；slot 映射唯一（`BodyRegion.cs:35-45`，Head=0/Chest=2/Hips=3/LeftLeg=4/RightLeg=5/LeftArm=6/RightArm=7）。**没有披风、没有肩** |
| legacy2sectional | `HD2ModCore/Domain/SectionalSplitPlan.cs:126-253` | 按 `(标准槽, 身形)` 分组；`StandardSlots` = Helmet/Torso/Hip/LeftArm/RightArm/LeftLeg/RightLeg（`:81-82`）；左右肩→左右臂，其它非标准槽→Torso（`:109-121`）；每槽最多 `{Any}` 或 `{Stocky,Slim}`，所以 **unit 数 = 槽 × 身形，7~13 个** |

产物：`sectional/<archive>.patch_0`（只装 unit 条目）+ `patch_1..N`（只装材质）+ `patches.json` / `sections.json` / `components.json` / `custom-model.json`（字段与读写者见 §4.4）。

### 4.3 编译层（sectional → Overwrite）

- `ModCompileService.CompileAsync`（`HD2ModCore/Infrastructure/ModCompileService.cs:52-117`）读 `wardrobe.json` 的三个条件（目标护甲 / section 开关 / 头身合并）→ `SectionalCompiler`。
- `SectionalCompilePlanner.Build`（`HD2ModCore/Infrastructure/Sectional/SectionalCompilePlan.cs:83-227`）的规则：
  - 披风（slot 1）**不替换也不隐藏**；
  - 每个来源 unit → 目标**同部位最外层**（`Max(Layer)`）；
  - 目标其余层 + 没有来源的部位 → **隐藏壳**；
  - 头身合并：头 → 目标躯干的**次外层**，身体 → 最外层；躯干只有 1 层则 `SkippedNoFreeTorsoLayer`。
- **隐藏壳不是「不写」**：写一个 3 顶点/1 三角形的空壳，且 `minifyCullingMeshes: true` 强制把 `lod=-1` 深度代理一起压掉（`SectionalHiddenUnit.cs:39-75`、`SectionalUnitPatchWriter.cs:53-168`）。t

### 4.4 四个结构文件

| 文件 | 写者 | 关键字段 | 读者 |
|---|---|---|---|
| `patches.json` | `SectionalPatchManifestFile` | patchIndex / role(unit/material) / unitCount | 编译器找 unit patch、材质层 |
| `sections.json` | `WardrobeSectionMapFile` / `SectionalSplitter` | 每 unit 的 `slot/pieceType/bodyType/weight` + 逐 section 偏移与指纹 | `SectionalCompiler.CellOf`、组件树、烘焙 |
| `components.json` | `SectionGroupFile` / `SectionalSplitter` | 组 → `(unit, section 下标)` | 组件树（用户能关什么）、`TranslateGroupsAsync` |
| `custom-model.json` | `SectionalModelDefinition` / CLI / `ModCompileService`（派生份） | `pieces[]{body,unit,slot,pieceType}` | 软映射来源身份（`CustomModelCatalog`，**Overwrite 优先**） |

⚠ 素体件的 `pieceType` 现在是**恒 0**（`SectionalModelDefinition.RegistryPieceLayer = 0`，`:44`、`:87`），`sections.json` 里也写成 `Undergarment`（`SectionalSplitter.cs:382-397`）。两条表必须同步，否则组件树画出来的树和素体对不上。

### 4.5 物理容器（HD2PBX01）

- **容器是 patch 级的，不是 unit 级的**：每个「含 unit 条目」的 patch 文件末尾追加一份，与里面有几个 unit 无关（`PhysBoneContainerEmbedder.cs:120-121`、`PhysBoneCarrier.cs:127-128`；同 patch 7 unit 只 1 份容器，`docs/agent/physbone-per-unit-rig-for-converter.md:40-50`）。
- 真正**跟着 unit 分区**的是**骨架里的 `source_extra_*` 骨表**：转换期按件裁骨（`VpkPatchAssembler.cs:684-697`、`ExtraBoneSubset.cs:23-45`），每个 unit 只带自己用到的合成骨。
- 合并 unit 的后果：被并进来的 donor 的骨不在宿主 `TransformInfo` 里 → 运行时按骨名匹配不到 → 那块布没有物理。更硬的是 `CanonicalTransformInfoExpander.EnsureHash` 在 avatar 里找不到骨会**直接抛**（`:42`），而四条合并路径传的都是游戏原生骨架（里面没有 `source_extra_*`）⇒ **自带物理骨的 unit 一合并就失败**。
- 结论：**改编译层不会丢容器（文件级自动跟走），但骨表必须跟着合并重算** —— 这条目前代码里没有实现，只在文档里写着。

### 4.6 软映射（运行时 Lua）

- 意图：`%LOCALAPPDATA%\CowboyBingus\Helldivers2\ArmorRegistry\armor-registry.protocol`（299 条 `map_model=`，11 个 model 定义，但**全部 299 条都指向同一个 model**）。
- 编译：`WardrobeLinkCompiler.Compile`（`HD2ModCore/Infrastructure/WardrobeLinkCompiler.cs:46-151`）：
  - 目标件表来自权威索引库；
  - 来源 = `model.Pieces`（`(body, slot, pieceType) → unit`）；
  - **逐格精确对齐**：同身形同层 → Any 同层 → 目标是 Any 行时容忍按身形拆 → 都没有就 `HiddenUnit`；
  - 整片对不上时，最底层借来源最底层（`ResolvePartFallback`，`:250-264`）；
  - 产物 `wardrobe.links`：`schema=3` + `link=<目标 unit>,<来源 unit>`（自链接也发）。
- 运行时：`armor_override_runtime.lua` 1 Hz 读文件，只做 `set_resource_override('unit', src, dst)`；**同一源键先到先得**（`:90-94`），后写的行**静默丢弃**；删除/关闭不释放，只记「需重启」。
- 隐藏 unit：`def7dc54e86d6054`（真实存在的空 unit，必须随包部署）。**软映射的「隐藏」是把目标 unit 指向这个空 unit**，所以它不会留下深度代理 —— 这与编译层的「写空壳」是两种不同的隐藏。

**现有软映射对「unit 复用」没有任何检测、提示或黑名单**（`WardrobeLinkCompiler` 只对完全相同的 `(own, donor)` 去重；`ValidateMappings` 只校验目标护甲不重复）。唯一的复用检测工具是离线脚本 `Inspect-UnitSharing.lua`。

---

## 5. 现成可复用的实现（别重写）

### 5.1 `CellAssignmentPlanner` = 你说的那套「适应性分配」已经有一份

`HD2ModCore/Domain/CellAssignmentPlanner.cs:41-67`：

- **P1 自然/广播** → **P2 同槽改层** → **P3 跨槽改挂（池 = 胯/腿/臂/肩）** → **P4 合并（往高处并）** → **P5 隐藏**
- **胸（2）与头（0）不进改挂池**（`:177-178`、`:206`），理由写在 `docs/agent/armor-cell-plan-20261027.md:22`：**这两处在第一人称下隐藏**，外挂会糊住相机；别的部件挂进来会凭空消失。
- P4 的「往高处并」正是你说的「从高到低选」（`:212-217`，注释：「多余的第 2 层并进第 1 层；只有第 0 层时全并进第 0 层」）。

⇒ 软映射要做的事，本质是**把这个规划器的方向反过来**（来源找箱子 → 箱子找来源），或者直接在它上面加一层「3 槽来源」的投影。

### 5.2 其余

| 能力 | 位置 | 备注 |
|---|---|---|
| 独占/共用判据 | `HD2RuntimeArmorOverride/scripts/Inspect-UnitSharing.lua:19-55` | 翻成 C# 即可（`unit → {kit}`） |
| unit 级几何合并 | `HD2ModAdaptation/…/CanonicalDecorationUnitAppender.TryAppendLodFamily` | LOD 家族整体重建；`replaceTargetGeometry: false`；`CullingBounds` 必须显式 `UnionWithComputed` |
| 转换期合并先例 | `HD2ModCore/Infrastructure/SectionalSplitter.cs:181-282` | host/donor；必须 `uniqueSlotPerSection`；donor 自带物理骨会失败 |
| 隐藏壳 | `SectionalHiddenUnit.cs` / `CanonicalHiddenUnitBuilder` | 强制 minify culling |
| 「开关即编译」范式 | `HD2ModManager/ViewModels/ModDetailsPageViewModel.Vpk.cs:900-994` | 翻开关 → 写意图 → 后台编译 → 按实际结果说话 |
| 事务/回滚 | `ManagedOverwriteDirectory`（Prepare/Commit/Rollback） | 新工作流直接套 |
| 批量重构建 | `VpkBulkRebuildService.RunAsync` | 全库刷一遍的现成骨架 |

---

## 6. 硬边界与风险（决定方案形状）

### 6.1 第一人称隐藏 slot 0 与 slot 2

`HD2ModCore/Domain/ArmorCell.cs:51`（`IsFirstPersonHidden => Torso or Helmet`）+ `docs/agent/armor-cell-plan-20261027.md:22`。

- 胸不能外挂、别的部位不能并进胸 —— 与你的「胸甲不能适应」完全一致。
- **但「头 + 胸合并成一个 unit」正好都在隐藏侧**，安全。
- 「其他部位」必须挂在一个**非 0/2** 的槽上（胯/腿/臂），否则第一人称里会整块消失。

### 6.2 只有 3 条 bank 通道

`pt0 → A / pt1 → B / pt≥2 → C`，**同通道最后写入者赢**（`docs/agent/armor-true-mapping-audit-20261108.md:414-418`、`notes/armor-bank-code-evidence-20261017.md:54-84`）。

⇒ 「未被选择的或黑名单的则隐藏」在运行时是**覆盖成空 unit**，不是不写；同一通道里多层同时覆盖时，只有最后一个生效 —— 黑名单必须保证**同一 (slot, pt) 只留一条有效覆盖**。

### 6.3 20 件预算

每 (kit, 身形) 最多 20 件，第 21 件起静默丢弃，且 Any 行先吃预算（`notes/armor-bank-code-evidence-20261017.md:107-114`、`8f8111b`）。
当前原生每 (kit, body) 只有 4~11 件，**不紧张**；压缩的收益不在预算，在「少碰污染 unit / 少骨表」。

### 6.4 隐藏 unit 必须真实存在

`WardrobeLinkCompiler.cs:24-28`、`LuaDeploymentService.cs:360-376`：隐藏 unit 必须随包部署（独占 boot 模式也要额外发一个「只含隐藏 unit」的补丁，序号恒为倒数第二）。
（注：11-12 的 A/B 证伪了「缺隐藏 unit ⇒ 军械库崩溃」的因果，但「必须存在」这条仍是代码里的硬规则，别拿崩溃当论据。）

### 6.5 编译层没有几何合并能力

`SectionalUnitPatchWriter.cs:33-44` 自己写着：「只做改名副本 + 隐藏壳，**没有几何重建、没有骨骼对齐、没有 LOD 展开**」。
要把「头 + 躯干」或「胯腿臂」合成一个 unit，必须走 `CanonicalDecorationUnitAppender`，它的前置条件是：
1. 真头像骨架（`CanonicalCrossArmorOrchestrator.cs:514-516` 明说「直通路径默认不读它」）；
2. LOD 家族整体重建（否则近 LOD 换了、远 LOD 没换）；
3. `CullingBounds` 显式重算（默认 `Preserve`，顶点出盒会被剔除）；
4. 物理骨要在 avatar 里备好（否则 `MissingTargetBone` 硬失败）。

### 6.6 `sections.json` 的身份恒为 `Undergarment`

`SectionalModelDefinition.RegistryPieceLayer = 0`、`SectionalSplitter.BuildUnitIdentities` 写 `"Undergarment"`。
压缩后如果一个素体 unit 要落在目标的不同层（0/1/2），这两张表必须先重定义，否则 `SectionalCompiler.CellOf`/`LayerOf` 会把所有压缩 unit 当成层 0，`SectionalCompilePlanner` 随后把 1/2 层全隐藏 —— **表与产物互相矛盾**。

### 6.7 软映射的「隐藏」与编译层的「隐藏」不是一回事

软映射 = 指向空 unit（无几何、无代理）；编译层 = 写空壳（保留 struct，代理可压小）。两者对「遮挡」的影响不同，验收时别混。

---

## 7. 三个需要你拍板的问题（决定改动量级）

### Q1「污染」按哪种口径？

| 口径 | 结果 | 代价 |
|---|---|---|
| **A 严格独占**（`Inspect-UnitSharing` 口径：被任何别的 kit 引用就算污染） | 胸 97/138、其他部位 80/138、3 槽全可行 78/138；41 套胸部无解 | 最保守，但 30% 的护甲映射不了 |
| **B 同一 model 内可复用**（只有「别的 model / 未映射」引用才算污染） | 当前 138/138 全可行（299 条映射全指向同一个素体） | 最符合实际体验；但用户以后一分模型，候选会变少 |
| **C 只对「会被另一套不同外观的护甲用」算污染**（即同 setId 的换色兄弟不算） | 介于两者之间（共用里只有 10/77 是同 setId） | 需要定义「同一套」 |

我倾向 **B**（判定动态化：黑名单 = 该 unit 的引用者里存在「映射到别的 model 或没映射」的 kit），但这是产品选择，得你定。

### Q2「3 槽」的形态？

1. 每槽是否还分 `Stocky / Slim`（`{Any}` 还是 `{Stocky,Slim}`）？—— 现有素体是「每槽 × 身形」，`body=Any` 会让两套身形几何同时出现（`sectional-u0-findings-20261104.md:196-214` 已证伪过）。
2. 头盔槽保留吗？「其他部位」挂在哪个槽（胯 3 / 腿 4-5 / 臂 6-7）—— 这决定第一人称可见性和「哪些目标件必须隐藏」。
3. 压缩后「其他」的几何是**一个 unit 装胯+双腿+双臂**，还是**每侧一个**（左手/右手分开）？前者 unit 数最少、后者更贴近现有 7 槽。

### Q3 压缩发生在哪一段？

| 方案 | 改哪 | 风险 |
|---|---|---|
| **A 转换器多产一个 3 槽包** | `SectionalSplitPlan` / `SectionalSplitter` / `patches.json`（加形态维度）/ `custom-model.json` | 与「一份权威 unit patch」的现有假设冲突；`sections.json` 的 patch 指纹只描述一个 patch |
| **B 编译期加一个条件** | `SectionalCompiler` / `SectionalCompilePlan` / `SectionalUnitPatchWriter` | 编译层没有几何合并能力（§6.5），要新写；但 `sectional/` 不动，符合硬边界 |
| **C 改 vpk2patch / legacy2sectional 的产物格式** | 两条转换链路 + 所有读者 | 破形状，存量产物要迁移；`patches.json` 的定位是「可重建的派生数据」，加字段可以，加第二种 unit patch 不行 |

你的原话「也可以考虑直接改动 vpk2patch 流程和 legacy2sectional 流程」对应的就是 **A**；它比 B 少一次几何合并（转换期本来就在合并），但要动「形状固定」这条边界。

---

## 8. 我建议的下一步（都不进产品代码）

1. **先用口径 B 出一张全量表**：138 套 × 每槽 × 每层的「独占/共用（谁在用）」，作为黑名单的输入与验收基线。（口径 A 的表已经出来了：`tmp/armor-slot-analysis/torso-candidates.csv`）
2. **拿一套样本做离线验证**：选 1 套「胸有 pt2、其他部位有独占」的护甲 + 1 个现有素体，手工构造 3 槽映射，走 `WardrobeLinkCompiler` 生成 `wardrobe.links`，只读校验链路是否按预期把未选中的件指向隐藏 unit。
3. 真要动几何合并（头+胸），先做 `CullingBounds` 与骨表的离线断言（`vertices_outside == 0`、三角形数守恒），再谈进游戏。

---

## 9. 口径 A 的正式定义与名单（2026-11-14 追加）

用户确认的判据（**就是 A**）：

1. 先扫注册表，把**被 ≥2 套护甲（不同 `item_id`）引用**的 unit 全部剔除；
   - 同一套甲的 Stocky / Slim 两行引用同一个 unit **不算两套**；
   - 同一套甲在两个槽位引用同一个 unit 也**只算一套**；
   - 一旦被剔除，对所有引用它的套都不可用（全局判定）。
2. 剔除后，某套护甲的**胸部（slot 2）只要还剩任意一层**可用，就算「可做到不污染」。

结果（138 套身体护甲）：

- **97 套可做到不污染**（96 个不同名字）；**41 套无解**（30 个不同名字）。
- 97 套里按「最高可用层」分配：**pt2 = 51 套、pt1 = 32 套、pt0 = 14 套**。
- 41 套里按「拖住它的那条链最长共用数」分桶：14 套共用 12 例、8 套共用 5 例、7 套共用 2 例、6 套共用 4 例、4 套共用 6 例、3 套共用 8 例、2 套共用 4 例。

**41 套「无可用胸部层」的完整名单**见 [`tmp/armor-slot-analysis/blocked-by-name.md`](../../tmp/armor-slot-analysis/blocked-by-name.md)（按名字聚合，避免 7 个 B-01 变体看起来像 7 个不同套装）；逐层明细见 [`chest-A-definition.md`](../../tmp/armor-slot-analysis/chest-A-definition.md)。

> 注意：显示名不唯一。同一名字可以有多个 `item_id`（配色 / 变体），所以「名字在名单里」不等于「所有该名字的套都不行」——以 itemId 为准。

> 另外 14 套「只剩 pt0 可用」是边界情况：如果坚持「pt0 一定污染」，这 14 套也要算无解，97 → **83**。

## 附录 A：本轮新建的可复跑脚本

| 文件 | 作用 |
|---|---|
| `tmp/dlbin-probe/` | 解密 live `.dl_bin` → `tmp/dlbin-live.json`（422 kit / 3527 件） |
| `tmp/compare_live_index.py` / `compare2.py` | 与索引库逐件对账（掩码后 0 差异） |
| `tmp/analyze_slots.py` | 每槽每层的可用性 / 独占性；产出 `tmp/armor-slot-analysis/torso-layers.csv` |
| `tmp/analyze_shared2.py` | 共用 unit 的 setId 分布（10/77 同套，67 跨套） |
| `tmp/key_numbers.py` | §3.2 / §3.3 的表；产出 `key-numbers.txt` |
| `tmp/final_inventory.py` | 3 槽可行性统计（78/138）、每 (kit, body) 件数 |

## 附录 B：本轮引用的关键代码/文档索引

- 护甲结构唯一权威：`HD2ModCore/Domain/ArmorStructure.cs:13-126`（四元组寻址、槽位、层、共享 unit）
- 第一人称隐藏：`HD2ModCore/Domain/ArmorCell.cs:51`；`docs/agent/armor-cell-plan-20261027.md:22`
- 装箱策略：`HD2ModCore/Domain/CellAssignmentPlanner.cs:41-67`、`:177-217`
- 软映射编译：`HD2ModCore/Infrastructure/WardrobeLinkCompiler.cs:46-151`、`:220-264`
- 运行时：`HD2RuntimeArmorOverride/src/armor_override_runtime.lua:90-94`、`:113-131`
- 复用检测：`HD2RuntimeArmorOverride/scripts/Inspect-UnitSharing.lua:1-5`、`:19-55`
- 编译层：`HD2ModCore/Infrastructure/Sectional/SectionalCompilePlan.cs:83-227`、`SectionalUnitPatchWriter.cs:33-44`、`SectionalHiddenUnit.cs:39-75`
- 物理容器：`HD2SourceMigration/VpkConversion/PhysBoneContainerEmbedder.cs:120-142`、`HD2ModAdaptation/PatchReconstruction/PhysBoneCarrier.cs:283-314`
- 引擎事实清单：`HD2RuntimeArmorOverride/notes/sectional-survey-20261104/C-placement.md:39-106`
- 20 件预算 / 3 条 bank：`docs/agent/armor-true-mapping-audit-20261108.md:414-418`、`notes/armor-bank-code-evidence-20261017.md:54-114`
- 历史：`5dffd67`（43 件压进 23 键：11 替换 / 12 隐藏 / 1 共用不动）、`0acfe06`、`8f8111b`、`dcb1396`（层优先）、`9962946`（外层优先）、`bf3fce0`（层号不是绝对编号）
