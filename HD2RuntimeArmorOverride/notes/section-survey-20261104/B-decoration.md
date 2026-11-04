# 装饰（decoration）线调查报告

范围：`HD2ModCore/Decoration/`、`DecorationHostRebuildService` 及协作者、两个装饰页面、三份状态文件、与 section 系统的关系。只读调查，未修改任何代码。

## 0. 一句话结论

**装饰不是"一个 patch"，也不是"往宿主里追加 section"。它是一份「宿主 Unit 的几何捐赠清单」+ 一份「附着声明」，产物是宿主目录下的 `Overwrite/` 替换层——一个重新生成的宿主 patch。** 追加进去的 section 是匿名材质批，装饰与 section 之间没有任何持久化对应关系，所以用户**不能**用 VPK 那套 section 开关去关一个装饰。

---

## 1. 装饰在 HD2 里到底是什么（物理形态）

### 1.1 磁盘上：一个带 `decoration.json` 的目录

```
<库>/<显示名>-<guidN>/
    decoration.json            ← 附着声明 + 来源 Unit 选择（唯一身份文件）
    <archive>.patch_0          ← 从来源 Mod 原样复制的 patch TOC
    <archive>.patch_0.gpu_resources / .stream
```

- 目录与节点：`HD2ModManager/Services/ModLibraryService.cs:1138`（`$"{safeName}-{nodeId.Value:N}"`）、`:1154`（`ModNodeKind.Decoration`，**PatchGroups 与 Children 皆空**）。
- 判据：`HD2ModCore/Infrastructure/ObjectTreeImporter.cs:96-97`（目录里有 `decoration.json`）。
- 复制的文件：`HD2ModCore/Infrastructure/DecorationPackageSnapshotService.cs:56-66`（base + `.gpu_resources` + `.stream`，`File.Copy` 原样，不解码）。

### 1.2 那批 patch 不是可部署的 patch

装饰目录里虽躺着可解析的 patch TOC，但它**不参与部署**：`PatchGroups = []` → `HasPatchContent = false` → 不进 profile（`ModLibraryService.cs:1154`、`:1783`）；导入侧显式把装饰排除在 patch 扫描外（`ModLibraryImporter.cs:557`、`:643`、`:756`）。它只被重建服务当"来源快照"读（`DecorationHostRebuildService.cs:545-604`）。**所以装饰包本身部署下去什么也不会发生——它是模型仓库，不是 mod。**

### 1.3 契约描述了什么

| 类型 | 内容 | 行 |
|---|---|---|
| `DecorationPlanDocument` | 格式、`SourceStorageMode`（现只有 `PatchSnapshot`）、`SourceUnits`、`Payloads`（**旧版 .bin**）、`Plan` | `HD2ModCore/Decoration/DecorationPlanDocument.cs:7-17` |
| `DecorationAttachmentPlan` | `TargetModGuids[]`（**声明挂到哪些宿主**）、`TargetPart`、`TargetBodyVariant`、`DualVariantMode`、`ReplaceWhenSourcePartLayerMatches`、`SourcePartLayers[]` | `:19-30` |
| `DecorationSourceUnit` | `(TypeId, FileId, MeshInfoIndex, BodyVariant, Layer, IsCulling)`——**没有 section 字段** | `:38-48` |
| `DecorationPayloadDocument` | 内存态网格片段。注释原文 *"Neutral, non-deployable mesh data."* | `HD2ModCore/Decoration/DecorationPayloadDocument.cs:6-15` |

`DecorationPayloadDocument` **不落盘**（旧 `.bin` 路线已废，见 §6），只是重建时由 `BuildSnapshotPayloads` 现切的中间产物（`DecorationHostRebuildService.cs:606-620`）。

### 1.4 产物写到哪：宿主目录的 `Overwrite/`

```
宿主 Mod 目录
  <archive>.patch_0 (+ .gpu_resources/.stream)   ← 输入：原始 patch（唯一重建依据）
  Overwrite/                                     ← 输出：重新生成的同名 patch
      <archive>.patch_0 (+ 两个 sidecar)             （追加过装饰几何）
      .hd2-decoration-overwrite                  ← "管理器生成"的所有权标记
```

输出路径 `DecorationHostRebuildService.cs:45`；标记名 `:21`、写入 `:102`。`Overwrite/` 的语义是**替换层**：只有同名根 patch 存在时才生效，自己不构成新内容（`HD2ModCore/Infrastructure/ModNodePayloadLayout.cs:10-16`，实现 `:115-147`）。**游戏里看到的"装饰"，是宿主自己那份 patch 的一个变体。**

---

## 2. 生成链路

### 2.1 数据流

```
用户勾选"给宿主 H 启用装饰 D"
   │
   ▼
ModLibraryService.ScheduleDecorationActivationBatchAsync            (:1332-1369)
   ├ 读 D 的 decoration.json.Plan.TargetModGuids 校验 H 合法        (:1308-1328)
   └ 只改内存 pending + 2 秒防抖                                     (:1362-1365, :1371-1380)
   ▼ 防抖到期
FlushPendingDecorationRebuildsAsync → ApplyDecorationActivationBatchCoreAsync (:1382, :1436)
   ├ RebuildDecorationOverwritesAsync（受影响宿主）                  (:1511-1539)
   │    ├ ReadEnabledDecorationsAsync ← 读所有已启用装饰的 decoration.json (:1575-1591)
   │    └ RebuildHostOverwriteAsync → HostDerivedOverwriteService.RebuildAsync (:1545, :27-96)
   │           ├ 只有装饰        → DecorationHostRebuildService.RebuildHostAsync
   │           └ 装饰 + 头盔合并 → 先装饰，再把头盔合并叠上去        (:73-95, :137-188)
   │                 ├ 输入 = 宿主目录顶层 patch（不含 Overwrite/）  (:779-787)
   │                 ├ 读装饰来源快照 → DecorationPayloadDocument[]  (:545-604)
   │                 ├ 备份并清空 Overwrite/                         (:638-654)
   │                 ├ 逐 patch：读 TOC → 选宿主 mesh → 追加        (:135-222)
   │                 └ PatchArchiveWriter.WriteAsync                 (:219)
   └ 全部宿主重建成功后才写 decoration-activations.json              (:1485-1488)
        （失败则按操作前状态把已重建宿主再重建一遍回滚               :1490-1497）
```

### 2.2 谁触发

| 触发者 | 位置 |
|---|---|
| GUI 勾选装饰（单个 / 全部 / 某宿主） | `ModLibraryService.cs:128`、`:136`、`:1037`、`:1054` |
| 编辑装饰定义（写完 `decoration.json` 回建所有已启用宿主） | `ModLibraryService.cs:1164-1176` |
| 切换头盔合并（装饰是其前置层） | `ModLibraryService.cs:1593-1609` |
| CLI `host-derived plan decoration enable/disable`；CLI 批量修 patch | `CliHostDerivedOverwriteExecutor.cs:85`、`:201`、`:311-316`；`CliBatchPatchRepairExecutor.cs:296-310` |

### 2.3 `CanonicalDecorationUnitAppender` 追加什么

`HD2ModAdaptation/PatchReconstruction/UnitMesh/Canonical/CanonicalDecorationUnitAppender.cs`。不是"插新 mesh"，而是 **Blender Join 语义**（类注释 `:19-20`）：把来源三角形并进宿主**同一 mesh 的 section 表末尾**，再整体重算材质布局、骨骼、stream、TOC。

```
宿主 mesh(LOD k):  [sec0 材质A][sec1 材质B]
装饰 D1:           [sec0 材质X][sec1 材质Y]
装饰 D2:           [sec0 材质X]

追加 (:93-110)  5 个 section：宿主 2 个在前，D1 2 个、D2 1 个按输入顺序在后
材质布局 (:121) 先占宿主自有 slot，再分配外来 slot/material id (:160-183)
骨骼 (:131) → 按材质槽合并 (:134 → :189-197) → stream 重编 (:140)
   → 顶点重排 (:144-151) → TOC 重建 (:153)
   → PatchUnitMeshEditResult(..., ReplacementMaterialIds) (:156-157)
```

要点：

1. **新 section 是"材质批"，不是"装饰"。** `:189-197` 按 `(MaterialIndex, MaterialSlotId)` 合并——这正是引擎事实：一个 LOD mesh = 一个共享顶点池 + 按材质切分的索引区间（实测见 `HD2RuntimeArmorOverride/notes/capacity-problem-may-not-exist-sections-are-toggles-20261017.md:95-104`）。**两个装饰共用材质时，合并后物理上无法只关其中一个。**
2. **材质编号**由 `CanonicalMaterialBindingResolver` + `CanonicalUnitMaterialLayoutCompiler` 统一分配；`PreferMeshLocalSlotReuse: true`（`:180`）让同材质复用同槽——既是"多装饰不打架"的成因，也是上面合并的成因。
3. **命名空间** `DecorationNamespace(decorationId)`（`DecorationHostRebuildService.cs:531`）经 `MaterialNamespace` 传入（`:199`）用于区分来源，**不写进任何产物**。
4. **`replace` 模式真的会把宿主几何清零**：`replaceTargetGeometry` 为真时先用 `CanonicalPlaceholderMinifier` 把宿主 mesh 压成 3 顶点 / 1 三角形，其中 `section[0]` 留 1 个三角形、**其余 section 三角形数组为空（即 `numIndices = 0`）**（`CanonicalPlaceholderMinifier.cs:30-46`）。这是整条装饰线里唯一与 section 清零语义重合的地方，但用户不可控。

### 2.4 幂等性：有，且是结构性的

| 保护 | 位置 | 效果 |
|---|---|---|
| 输入永远是原始 patch（`Overwrite/` 是子目录，不在枚举内） | `:779-787` | 不会把上次产物当输入 → 不累加 |
| 输出先备份再重建 | `:638-654`（Move 到 `.backup-<guid>`，成功删除 `:103`） | 每次全量重算 |
| 所有权标记：无装饰标记且无头盔标记 → 拒绝覆盖 | `:646-648` | 不误删用户手建目录 |
| 取消/异常回滚 | `:106-118` | 不留半成品 |
| 多宿主事务回滚 | `ModLibraryService.cs:1490-1497` | 不出现"一半宿主有装饰" |

**重跑两次不会重复追加。** 真正的问题不是幂等，而是**"重跑会丢掉后处理"**：输出是重新生成的，任何对 `Overwrite/` patch 做过的原地修改都会在下次激活变化时被抹掉。这是 §7 的核心障碍。

---

## 3. 两个页面：不是重复，是**方向相反**

| | `DecorationPlanPageViewModel`（409 行） | `BatchDecorationPlanPageViewModel`（333 行） |
|---|---|---|
| 入口 | 「生成装饰 Mod（以来源 Mod）」 | 「批量生成装饰（以主体 Mod）」 |
| 起点 | 一个**来源** Mod | 一个**宿主** Mod |
| 选择 | 来源 Unit 多选 + **目标 Mod 多选**（N 宿主） | **来源 Mod 多选**（N 来源）+ 目标固定 `[HostModId]` |
| 目标绑定 | `TargetModGuids = 选中多个宿主`（`:245`、`:265`） | `TargetModGuids = [HostModId]`（`:205`） |
| 编辑已有装饰 | ✅ `IsEditing` / `UpdateDecorationPlanAsync`（`:283-292`） | ❌ |
| culling mesh 显示 | ✅ `ShowPotentialCulling`（`:86-94`） | ❌ |
| 非自动导入落盘 | `CopyDecorationFiles` 复制**目录内全部文件**（`:335-343`） | `CopyOutput` 只复制 `decoration.json/stocky.bin/slim.bin`（`:257-267`） |

注册：`ShellViewModel.cs:2364-2365`、`WorkspacePagePresentationController.cs:262`、`MainWindow.xaml:75-78`；按钮 `ModDetailsPageViewModel.Actions.cs:46-47`，可见性 `:71`（`CanShowDecorationPlan` = `!IsDecoration && HasPatchContent`，`ModDetailsPageViewModel.cs:109`）。

**判定：**

- **两个都活着**，都有真实入口，都不是死代码。
- 功能**部分重复**：最终都调 `CreateDecorationAsync`，生成的 `DecorationPlanDocument` 结构一致（`:254-267` vs `:201-211`）。
- **该用哪个**：一件东西挂很多宿主 → 单页；一个宿主挂一堆来源 → 批量页（免去逐个点选，自动挑首选 archive，`DecorationPlanningDefaults.SelectPreferredArchiveId`，`HD2ModCore/Decoration/DecorationPlanningDefaults.cs:9-22`）；要**改**已有装饰 → 只能单页。
- **真正的过时点是批量页的非自动导入分支**：它按 `stocky.bin/slim.bin` 复制（`:260`），而这两个文件自 `PatchSnapshot` 格式起**再无写入者**（`DecorationPlanDocument.cs:12-13`）。所以"批量 + 关自动导入"导出的目录里**只有 `decoration.json`、没有 patch**，包不可用。单页同功能（`:335-343`）是对的。
- 顺带一处文档漂移：`docs/mod-derivation-functions-20261026.md:167` 说批量页是"一次对多个主体生成"，与代码（1 宿主 × N 来源，`BatchDecorationPlanPageViewModel.cs:91`、`:205`）正好相反。

---

## 4. 三份状态文件的关系

| 文件 | 结构 | 权威于 | 位置 |
|---|---|---|---|
| `library.json` | `nodes[guid] = {id, relativePath, metadata{name, kind, hostModGuids[], optionOrder}, patchGroups[], children[]}` | 节点身份 / kind / 物理路径；Option 的声明宿主与默认顺序 | `ModNodeMetadata.cs:5-19` |
| `option-activations.json` | `{version:3, options[optionId][hostId]={enabled, effectiveOrder}}`（v1 兼容 `{id:[host]}`） | Option 在哪个宿主启用 + 用户排序 | `OptionActivationStore.cs:9-10`、`:49-59`、`:62-71`、`:453` |
| `decoration-activations.json` | `{decorationId: [hostId, ...]}` | 装饰在哪些宿主启用 | `DecorationActivationStore.cs:5-7`、`:237-264`；路径 `StoragePaths.cs:14` |

**`DecorationActivationStore` 只管 `decoration-activations.json`**，不碰 `library.json`、不碰 `option-activations.json`（类定义 `DecorationActivationStore.cs:8`，全部方法只操作 `_enabledByDecoration`）。两个 store 有一处刻意差异：桌面宽容加载（坏文件当空，`:196-208`），无头严格加载（`:25-41` 抛 `InvalidDataException`），理由 `:15-17`。

**Option：同一件事存两遍，刻意的。** "挂哪些宿主"在 `library.json` 的 `metadata.hostModGuids`（`ModNodeMetadata.cs:11`），**同时**在 Option 目录镜像 `option.json`（`ModNodeStorageProtocol.cs:73-102`）。目的是 `library.json` 丢失时物理目录仍能重建关系（`ObjectTreeImporter.cs:37`、`:71`）。"顺序"也两处但语义不同：`metadata.optionOrder` = 可移植默认序，`option-activations.effectiveOrder` = 本库用户拖动序，读取后者优先（`ModLibraryService.cs:177-178`）。

**装饰：不是两遍，是"另一处"。** 声明宿主**只在 `decoration.json` 的 `Plan.TargetModGuids`**（`DecorationPlanDocument.cs:21`）；`library.json` 里装饰节点的 `hostModGuids` **是空的**（创建时就没传，`ModLibraryService.cs:1154`；`ModEntity.HostModGuids` 因此为空数组，`:1782`）。读取权威处 `ModLibraryService.cs:1308-1328`（每次现读 `decoration.json`，再与 `library.json` 求交集）。装饰**完全没有顺序概念**：无 `optionOrder`、无 `effectiveOrder`，追加顺序 = `All()` 枚举顺序（`:1581`）——而顺序会通过材质槽分配影响最终 section 布局，所以这是**功能缺口**，不只是显示问题。

**结论：** 三份文件各管一段，没有真正的重复写入；真正的重复在**概念层**——两套"往宿主加东西"的机制、两套启用状态、两套宿主解析，最后被一个方法合成一张表：`ModLibraryService.cs:296-297` 的 `GetAttachmentsForHost = GetOptionsForHost + GetDecorationsForHost`。宿主合法性判据也不一致：Option 用 `IsOptionHost`（`:403`），装饰要求 `PatchGroups.Count > 0 && kind ∈ {Standard, Option}`（`:1321-1322`），因此装饰**不能挂到逻辑容器上**。

---

## 5. 装饰与 section 的关系（关键）

### 5.1 装饰**没有**自己的 section 概念

1. 契约里没有 section 字段（`DecorationPlanDocument.cs:38-48`）。
2. 追加后的 section 下标只在一处算出——`CanonicalDecorationUnitAppender.cs:93`（`origins`）与 `:107`（`CanonicalAppendSectionOrigin`）——且**只喂给骨骼编译器**（`:131`），编译完即丢。
3. 产物只写 patch：`DecorationHostRebuildService.cs:219`，**不写** `sections.json`、**不写** `components.json`。
4. `MergeSectionsByMaterialSlot`（`:189-197`）进一步销毁边界：同材质 section 合并，一个 section 可能同时装着宿主几何和多个装饰的几何。

**追加进去的 section 是匿名的，无法反查"这属于哪个装饰"。**

### 5.2 用户**不能**用同一套 section 开关去关装饰

VPK 线是另一条链路：

```
vpk2patch 构建期 → sections.json (hd2mod.section-map/3)  每 section 身份 + 各级 LOD 落点
                → components.json (hd2mod.vpk-components/1) 用户能关什么（中文名 → section 集合）
                → wardrobe.masks                          用户勾选（按指纹）
                        │ 勾选变化
                        ▼
   WardrobeSectionBaker.BakeAsync(patchTocPath, map, disabledByUnit)
     把 section 记录 +16 处的 numIndices 写 0（隐藏）或写回原值（显示）
```

引用：`HD2ModCore/Domain/WardrobeSectionMap.cs:116-143`（`sectionRecordSize=24`、`numIndicesOffset=16`）、`HD2ModCore/Domain/VpkComponentSwitch.cs:3-18`、`HD2ModCore/Infrastructure/WardrobeSectionBaker.cs:24-49`。

装饰产物既不在 `sections.json` 覆盖范围内，也没有 `components.json`，所以：宿主若是 VPK 版，装饰重建后它的 `sections.json` 指纹**全部失效**（`patch.sha256` 指向被替换掉的旧 patch）。烘焙器有更强兜底（`WardrobeSectionBaker.cs:44-48`：目标 dword 必须"是 0 或原值"，否则拒绝），所以**不会关错**，但装饰追加出来的 section 也**永远进不了这张表**；即使手工补表，section 下标也会随"启用了几个装饰、什么顺序"变化（§4），指纹方案持续失效。

### 5.3 两套机制独立在哪、为什么

| 维度 | section 开关（VPK 线） | 装饰（decoration 线） |
|---|---|---|
| 操作 | **减**：把已有 section 的 `numIndices` 写 0 | **加**：往宿主 mesh 追加三角形与 section |
| 状态载体 | `wardrobe.json`（Mod 目录）+ `wardrobe.masks`（指纹） | `decoration-activations.json`（库根）+ `decoration.json` |
| 生效 | 烘焙进 patch，部署即生效；幂等（值相等即跳过） | 重建整个 `Overwrite/`，从源重算 |
| 粒度 | 一个 section | 一个装饰（1~N 个 section，且与别人合并） |
| 可逆 | 写回原值 | 重新生成（丢弃后处理） |

**为什么独立：语义方向相反。** section 开关假设"几何已在 unit 里，我只决定画不画"；装饰假设"几何默认不在，我决定加不加"。在"加"的模型下，"关掉一个装饰"的正确实现是**根本不追加**，而不是"追加了再关"——后者要求宿主永远带着全部装饰几何，与当前"按启用集重建"直接冲突。

> 仓库已**验证过**这条路可行：`HD2RuntimeArmorOverride/notes/decoration-section-map-20261017.md:9-35`（自有 vpk2patch 产物能做到 11 section ↔ 11 manifest group 逐个相等）、`:37-41`（材质不能代替装饰）、`notes/per-decoration-runtime-toggle-works-20261017.md:5-16`（运行期写 numIndices 成立）。但同两份笔记也写明：该 1:1 **只对我们自己的构建成立**，作者原始包是 22 section / 12 材质，装饰边界不可判定（`decoration-section-map:33-35`）；运行期 Lua 在 2026-10 更新后已不可用（`WardrobeSectionBaker.cs:28-31`）。这正是今天装饰线没走 section 语义的原因。

---

## 6. 过时 / 重复 / 没人用（已排除全部 `*.Tests`）

| 类型 | 状态 | 证据 |
|---|---|---|
| `DecorationPackageSnapshotService`（92 行） | **活**，真实现 | `CliDecorationPackageExecutor.cs:38`；GUI 经 shim `ModLibraryService.cs:1147` |
| `DecorationPatchSnapshotService`（23 行） | **纯转发壳**，可删 | 类体只有一行转发 `DecorationPatchSnapshotService.cs:19-22`；3 个 GUI 调用点（`DecorationPlanPageViewModel.cs:314`、`BatchDecorationPlanPageViewModel.cs:229`、`ModLibraryService.cs:1147`） |
| `DecorationHostTargetBinder`（175 行，internal） | **活**，单一调用点 | `DecorationHostRebuildService.cs:277` |
| `DecorationHostRebuildService`（788 行） | **活**，核心 | `ModLibraryService.cs:60`/`:430`；`CliHostDerivedOverwriteExecutor.cs:85`/`:201`；`CliBatchPatchRepairExecutor.cs:308` |
| `HostDerivedOverwriteService`（297 行） | **活**，装饰+头盔合成器 | `ModLibraryService.cs:1560` |
| `BatchDecorationPlanPageViewModel`（333 行） | **活**，但非自动导入分支已坏 | 入口 `ShellViewModel.cs:2365`；坏点 `:257-267` |
| `DecorationPlanPageViewModel`（409 行） | **活**，唯一能编辑装饰的页面 | `ShellViewModel.cs:2364`、`ModDetailsPageViewModel.Actions.cs:51` |
| `DecorationActivationStore`（310 行） | **活**，双端共用 | GUI `ModLibraryService.cs:427`；CLI 6 处（`CliWorkspace.cs:163`、`CliLibraryMutationOperations.cs:844`、`CliDecorationEditExecutor.cs:218`、`CliHostDerivedOverwriteExecutor.cs:311`、`CliBatchPatchRepairExecutor.cs:267`） |
| `DecorationPayloadFile` / `.bin` 读取 | **死**：只有读者无写者 | 读 `DecorationHostRebuildService.cs:77-85`（非 `.bin` 直接抛）、`:533-539`；`stocky.bin` 全仓仅剩 `BatchDecorationPlanPageViewModel.cs:260` 与测试夹具 |
| `DecorationPlanningDefaults` | **活**，两页面与重建服务共用 | `DecorationPlanPageViewModel.cs:146`/`:160-161`、`BatchDecorationPlanPageViewModel.cs:141`/`:151-152`、`DecorationHostRebuildService.cs:192` |
| `DecorationHostRebuildServiceTests` | **只测标记删除**（2 例，`DecorationHostRebuildServiceTests.cs:7-58`） | 788 行合并逻辑**零单元测试** |

**CLI 侧全套都在**（`CliApplication.cs:513-660` 分发、`:8058-8067` 帮助）：

```
hd2mod decoration inspect source / show
hd2mod decoration package plan|validate|apply          ← 生成（对应单页）
hd2mod decoration edit export|inspect|plan|validate|apply   ← 编辑
hd2mod host-derived plan decoration enable|disable|enable-all|disable-all --host <H>   (:1550, :8137-8138)
```

**缺口：没有批量装饰的 CLI 等价物**（`decoration package plan` 一次只吃一个 `--host`，`:4372`）；GUI 批量页是"循环调用单个"。

---

## 7. 融合进 section 系统的可行切入点

### 7.1 天然对齐

| 对齐点 | 说明 | 位置 |
|---|---|---|
| **A. 产物同类** | 装饰产物 = 宿主目录一份 patch TOC；`WardrobeSectionBaker.BakeAsync(patchTocPath, map, disabledByUnit)` 是纯后处理，只认"patch 路径 + 映射表 + 关哪些 unit 的哪些 section"，**对装饰合并后的 patch 一样能跑** | `WardrobeSectionBaker.cs:57-77` |
| **B. 身份机制已有** | section 身份 = `(unit, LOD0 下标, 指纹)`，本就是为"重建后要么继续有效、要么安全失效"设计的 | `WardrobeSectionMap.cs:9-14`、`:56-68` |
| **C. 追加时就能记边界** | appender 已算出每个来源 section 落到哪个最终下标（`origins`），只是用完就扔；落成 sidecar 就是现成的"装饰 → section"表 | `CanonicalDecorationUnitAppender.cs:93`、`:107` |
| **D. 状态形状一致** | 装饰启用 = `(decorationId, hostId) → bool`；section 开关 = `(unit, sectionIndex) → hidden`，前者是后者的集合级视图 | `DecorationActivationStore.cs:5-7` vs `WardrobeSectionMap.cs:36-41` |
| **E. 显示名已有** | `components.json` 的 `Name`（"猫耳"）与装饰节点 `Metadata.Name` 是同一种东西 | `VpkComponentSwitch.cs:32-40` |

### 7.2 根本对不上

| 对不上 | 为什么 | 位置 |
|---|---|---|
| **① 加 vs 减（最硬）** | section 开关只能关**已存在**的 section；装饰未启用时几何根本不在 unit 里。要统一必须反转设计：**永远追加全部装饰，用开关表达启用** | `DecorationHostRebuildService.cs:68-86`、`:194-202` |
| **② section ≠ 装饰** | 引擎里 section 是材质批；`MergeSectionsByMaterialSlot` 合并同材质 section，因此一个 section 可能横跨多个装饰，一个装饰也可能跨多个 section。**共用材质时"只关 A 不关 B"物理上不可能** | `CanonicalDecorationUnitAppender.cs:189-197`；`notes/capacity-problem-...:95-104` |
| **③ 重建抹掉烘焙** | 激活一变就**从源 patch 全量重算 `Overwrite/`**，任何事后烘进去的 `numIndices=0` 都会消失。烘焙必须**移进重建内部** | `DecorationHostRebuildService.cs:87-104`、`:638-654` |
| **④ 顺序不可控** | 装饰无 order 字段，追加顺序 = `All()` 顺序；顺序又影响材质槽与 section 布局。要做 section 语义，**必须先给装饰一个 order**（可复用 `optionOrder`/`effectiveOrder` 形状） | `ModLibraryService.cs:1581`；对照 `:177-178` |
| **⑤ 容量** | "永远追加 + 开关隐藏"意味着关掉的装饰仍占 section 与材质槽；当前单 unit section 数已达 98（头盔 22 / 躯干 25） | `notes/capacity-problem-...:40-46`、`:51-62` |
| **⑥ 生命周期不同** | section 烘焙就地改产物且幂等（不校验 sha256，只校验"当前值 ∈ {0, 原值}"）；装饰每次重生成。两套幂等契约不能直接叠 | `WardrobeSectionBaker.cs:44-48` |

### 7.3 建议的最小切入点（不改语义，先加可观测性）

1. **先补 sidecar，不补开关。** 让 appender 把 `origins` 落成 `<宿主>/Overwrite/decoration-sections.json`（`decorationId → [(lod, meshIndex, sectionIndex, materialId, triangles)]`），由重建流程一并写出。立刻能回答"这个装饰占哪些 section""两个装饰是否共用材质""关掉它少多少三角形"；纯新增文件，不改 patch、不改现有状态，风险极低。
2. **再决定方向。** 有了这张表才能用数据判断：多数装饰**材质互不相同**（自有 vpk2patch 产物常见）→ section 边界 ≈ 装饰边界，可复用 `WardrobeSectionBaker` 烘焙路径（对齐 A/B/C）；若普遍共用材质 → section 语义只能表达**材质级**开关，装饰级仍需"追加/不追加"，即**两套机制本来就该分开**。
3. **不要**让 `wardrobe.masks` 直接承载装饰：它的指纹是 `(unit, LOD0 下标, 指纹)`（`WardrobeSectionMap.cs:56-68`），而装饰 section 下标会随启用集变化；共用一张表会把"重建后安全失效"退化成"每次都失效"。
4. **顺带修三处**：`BatchDecorationPlanPageViewModel.cs:257-267` 改成复制目录内全部文件（对齐 `DecorationPlanPageViewModel.cs:335-343`）；删 `DecorationPatchSnapshotService`（23 行纯壳）；删 `.bin` 读取分支（`DecorationHostRebuildService.cs:77-85`、`:533-539`）与 `BatchDecorationPlanPageViewModel.cs:260` 的 bin 名单。

---

## 8. 全景

```
 来源 Mod 的 patch TOC（Unit/mesh/section）── 用户只选 Unit，无 section 粒度
 decoration.json (SourceUnits + TargetModGuids) ──► DecorationPackageSnapshotService
        原样复制 patch 三件套 ──► <库>/<名>-<guid>/ ← 装饰包（不可部署）
                                             │ 重建时按 hostId 过滤读取
 decoration-activations.json ────────────────┤
 {decorationId: [hostId...]}                 ▼
                          DecorationHostRebuildService.RebuildHostAsync
                            输入：宿主顶层 patch（唯一真源）
                            输出：宿主/Overwrite/（替换层 + 标记）
                              ├─► CanonicalDecorationUnitAppender  追加 section / 重算材质 / 合并
                              └─►（可选）HelmetToArmorMerge        叠在装饰产物之上
                                             ▼
                          Overwrite/<同名>.patch_0  ← 最终部署的就是它
                                             │
                        ┌────────────────────┴────────────────────┐
                        ▼                                         ▼
              sections.json / components.json                  无
              （装饰不写；宿主的已失效）                （所以 section 开关关不到装饰）
```

**给决策者一句话**：装饰这条线是"**生成期合并**"，section 那条线是"**产物期开关**"。它们共享同一份文件（宿主 patch），但一个在**生成前**决定内容、一个在**生成后**修改内容。要融合，唯一干净的方向是把 section 开关**前移**进生成流程（追加时就决定 `numIndices`），而不是把装饰**后移**成产物期开关。