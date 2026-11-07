# GUI ↔ CLI 能力对比：CLI 缺失的原子底座盘点

> 生成时间：2026-10-14 ｜ 目的：**为 CLI 补"底座级"原子能力**提供依据
> 配套文档：`docs/cli/HD2ModCli-capability-map.md`（CLI 现状盘点）

---

## 0. 这份文档要回答什么

用户的目标是：**让 Agent 能用 CLI 的最基本命令 + 外部脚本，拼出 GUI 做不到的定制流程。**

所以本文不看"GUI 有哪些按钮"，而看**"GUI 背后调用了哪些 Core 原子操作，而 CLI 没有出口"**。

判断标准（三层）：

| 层级 | 含义 | 例 |
|---|---|---|
| **L1 底座**（最细） | 字节/条目级操作：读一个 assetkey、写一个 assetkey、替换一个 unit mesh | `PatchSubsetWriter.WriteAsync(patch, out, [assetKey])` |
| **L2 复合** | 组合 L1 完成一个语义动作：生成隐藏 unit、合并头盔、拆分材质 | `HiddenUnitGenerator.Generate` |
| **L3 向导** | 组合 L2 完成一个完整业务流程 | `equipment helmet-merge apply` |

**当前 CLI 的问题：L3 很丰富，L1/L2 大量缺失。** GUI 因为有交互，把 L1/L2 藏在 ViewModel 里直接调用了，Agent 却拿不到。

---

## 1. 结论速览：CLI 缺失的原子能力

### 🔴 完全缺失（Core 有能力，CLI 零出口）

| # | 能力 | Core/Adaptation 层现成入口 | CLI 现状 | 对应 GUI 入口 |
|---|---|---|---|---|
| **G1** | **材质拆分 / 替换 / 嵌入** | `IMaterialPackagingApplicationService`：`InspectAsync` / `FindCandidatesAsync` / `SplitAsync` / `MergeAsync` | **完全无 material 命令** | `MaterialPackagingPageViewModel` |
| **G2** | **材质交付事实读取** | `IMaterialDeliveryFactsService.GetAsync` | 无 | `ModDetailsPageViewModel`（材质包装检查） |
| **G3** | **游戏装备/披风关系表读取** | `IGameDataAuthorityProvider.LoadAsync` → `GameDataAuthoritySnapshot`（含 `GameDataEquipmentKitKind.Cape`） | **无命令**（CLI 只用 Archive 索引，不用装备权威表） | 装备目录 / 护甲映射页 |
| **G4** | **高级 Unit 模型解析（带缓存）** | `IAdvancedModAnalysisService.GetStateAsync/GetCachedStateAsync/AnalyzeAsync/GetRequiredAnalysesAsync` | ⚠️ `mod analyze` 用**另一套** `PatchGroupAnalyzer`（无缓存、直读）。**带缓存的 `IAdvancedModAnalysisService` CLI 零接入** | `ModDetailsPageViewModel` |
| **G5** | **Patch 引用链诊断 + 轻量/完整对比** | `IPatchGraphDiagnosticsService.AnalyzeDependencyGraphAsync` / `AnalyzeFullPatchGraphAsync` | ⚠️ `mod analyze` 会输出 `CliModAnalysisReference`（引用），但**没有"轻量 vs 完整对比"**这一诊断命令 | `ModDetailsPageViewModel`（引用链诊断测试） |
| **G6** | **Mod 级资产明细查询**（按类型/名称/AssetKey/问题筛选） | `IAdvancedModAssetQueryService.QueryAsync` | 只有 `gamedata asset find`（原版侧），Mod 侧无 | 高级 Mod 详情资产表 |
| **G7** | **Option 路由 / 后代只读解析** | `ResolveOptionRoutes` / `GetOptionDescendantsForHost`（`ModLibraryService`） | 只有 `option show`（单点），无"树/路由解析" | Mod 列表 + 详情页关系视图 |
| **G8** | **批量装饰激活**（一次算好每主体最终集合再重建，避免重复重建） | `ApplyDecorationActivationBatchAsync` / `ToggleDecorationForAllAvailableHostsAsync` | 只有逐个 `host-derived plan decoration` | 库页"一键启停装饰" |
| **G9** | **导出**（把 Mod 树导出为带 manifest 的压缩包） | `IModExporter` | ✅ 有（`mod export`） | 库页导出 |
| **G10** | **缩略图生成 / 缓存清理** | `IModThumbnailProducer` / `RequestThumbnailAsync` / `SweepThumbnailCacheAsync` | 无 | 库页图标 |

### 🟠 粒度不够细（CLI 有命令，但被包成整体，缺中间拆解）

| # | 能力 | 粒度缺口 | 底座已有 |
|---|---|---|---|
| **F1** | **按 AssetKey 选子集 → 另存新 Patch** | ⚠️ **已有，但门槛是"必须先进库"**。`patch repackage` 已支持任意 AssetKey 子集（`CliPatchResourceSelection(VariantId, PatchGroupId)`，注释明写 "deliberately works at whole AssetKey payload granularity"），且**跨多 Mod / 多 Patch 组**。**真正的缺口**：它只认**已登记进库**的 Mod，无法对"库外的 patch 文件路径"操作 | ✅ 业务底座 `PatchResourceRepackagingService.WriteAsync(selections)`；底层 `PatchSubsetWriter.WriteAsync` |
| **F2** | **单条目三段载荷导出成离散文件** | ❌ **真缺**。CLI 能选子集→写成**一个新 patch**，但**不能把某个 assetkey 的 `toc`/`stream`/`gpu_resources` 三段各自导成一个裸文件**（用户原话要的"解包成每一个 assetkey 一个离散文件"）。`gamedata extract asset` 是**原版侧**，不作用于 Mod Patch | ✅ `IPatchEntryPayloadReader.ReadPayloadAsync(entry)` → `TocData`/`StreamData`/`GpuResourceData` |
| **F2b** | **对既有 Patch 做条目级外科手术（写回）** | ❌ **真缺**。CLI 所有写操作都输出"新目录里的完整 patch"，无法对既有 patch 加/删/换一条 | ✅ `PatchWorkspaceChangeSet.Add/Remove/Replace/Keep` + `PatchOperationWorkspace.Stage/Reference` |
| **F3** | **多源 Patch 合并**（头部模板 + 多来源选择） | ❌ **真缺**。**这正是"把头盔 archive 视为盔甲 archive 一部分"的底座**——已核实 `CustomModelConversionService:169` 就是调它做 `Combine split source patches`。`patch repackage` 虽跨 Mod 选择，但**不接受"指定 header 模板"**且用途不同 | ✅ `PatchSubsetWriter.WriteSelectedAsync(headerTemplate, outDir, selections[])`；已被 `CustomModelConversionService` / `HelmetToArmorMergeService` / `PatchResourceRepackagingService` / `MaterialPackagingService` **四处高层调用**，但 CLI 侧无出口 |
| **F4** | **任意 Patch 文件的 TOC 扫描 / 条目枚举** | ⚠️ **部分已有**：`mod analyze` 覆盖**已登记 Mod**。缺口 = 对**任意 patch 文件路径**（库外、外部下载的、原版 patch）无命令 | ✅ `PatchTocScanner` + `PatchWorkspaceReader.ReadIndexAsync/ReadAsync` |
| **F5** | **单个 Patch 条目的载荷读取（原始字节）** | ⚠️ **能看到大小，拿不到字节**。`CliModAnalysisPatchFile` 给了 `Toc`/`Stream`/`GpuResources` 的文件信息，但没有"把这一段字节吐出来"的命令 | ✅ `IPatchEntryPayloadReader.ReadPayloadAsync(entry)` |
| **F6** | **隐藏 Unit 生成** | **只在 `armor-normalize`/`cross-armor`/`patch repair` 内部被间接调用**；无独立命令 | ✅ `IHiddenUnitGenerator.Generate(target, targetStream)` |
| **F7** | **Unit mesh 重建**（Same-key canonical） | CLI 有 `patch repair`，但只能走固定配方，无法"给一个 unit 直接重建" | ✅ `SameKeyCanonicalUnitRebuilder` / `PatchAssetReconstructor.ReconstructAsync` |
| **F8** | **装饰 Unit 快照捕获** | 藏在 `decoration package/edit` 里 | ✅ `DecorationPackageSnapshotService.CaptureAsync` |
| **G11** | **Unit 部位/身形/层细粒度选择** | GUI 可选 `(archiveId, assetkey, meshIndex, partKind, bodyVariant, layer, culling)`；CLI 只能整包/整 Unit | ✅ `UnitMeshPartClassifier` / `EquipmentUnitSourcePartBinder` |
| **G12** | **跨护甲高级选项** | `DirectSourceUnitReuse` / `AutoHideUnmappedTargetUnits` / `PreserveSourceReferences` 在 CLI 里被**硬编码** | ✅ 参数已在 `CrossArmorTransferCandidateRequest` |

---

## 1.5 ★ 核心洞察：CLI 的问题是"能读不能写、能整体不能拆件"

我把 CLI 的 Patch 能力核实到底后，发现一个比"缺命令"更准确的说法：

**`mod analyze` 的粒度其实已经很细了。** 它直接构造 `PatchGroupAnalyzer`，输出结构包含：

```
CliModAnalysisDocument
├─ CliModAnalysisSource          （Mod 身份、登记 patch 组数）
├─ CliModAnalysisPatchGroup      （每个 patch 文件，含 Toc/Stream/GpuResources 三件套）
├─ CliModAnalysisEntry           （每条：AssetKey + Unit? + 引用 + 诊断）  ← 条目级
├─ CliModAnalysisUnit            （Unit 结构 + CompositeAssetKey）
├─ CliModAnalysisMesh            （mesh 列表）                            ← mesh 级
├─ CliModAnalysisReference       （source AssetKey → target AssetKey）    ← 引用级
└─ CliModAnalysisDiagnostic      （含 patch 文件名 + AssetKey 定位）
```

**也就是说：Agent 今天已经能"看见"一个 Mod 里每个 assetkey、每个 mesh、每条引用。**

**问题主要在"写"这一侧，但需要更精确地说**：

| 想做 | CLI 现状 |
|---|---|
| 看见一个 Mod 里有哪些 assetkey / mesh / 引用 | ✅ **能**（`mod analyze`，已登记 Mod） |
| 看见**库外**那个 patch 文件里有什么 | ❌ 不能（必须先登记进库） |
| 按 assetkey 选一批 → 另存成一个新 patch | ✅ **能**（`patch repackage`，接受任意子集、跨多 Mod） |
| 把某个 assetkey 的三段载荷导出成**三个裸文件** | ❌ 不能（只有原版侧的 `gamedata extract asset`） |
| 往既有 patch 里**加/删/换一条**（原地外科手术） | ❌ 不能（只能输出新目录的完整 patch） |
| **指定 header 模板 + 多来源合并**成一个 patch | ❌ 不能（`custom-model convert` 内部做了，但不对外） |
| 为一个 unit **就地**生成隐藏版本 | ❌ 不能（只在 `armor-normalize` 内部） |

**⇒ 补能力的方向有两块**：
1. **给写侧开"条目级"口子**：导出裸载荷、原地增删改条、指定 header 合并；
2. **把"库外 patch 文件"提升为一等公民**：让所有 patch 级命令都能作用于任意路径，而不只是已登记的 Mod。

这也解释了为什么 CLI 现在"僵化"：写操作要么落到已包装好的业务流程里，要么被"必须先登记进库"卡住，Agent 无法在流程中间插手、无法拿中间产物去做别的事。

---

## 2. 用户举的那个例子走一遍：隐藏全部披风

**目标**：生成一个 mod，隐藏游戏里全部披风类物品。

**现在的 CLI（做不到，但缺的是"写侧"）**：
- ① 读披风清单：**完全没有**命令能读 `GameDataEquipmentKitKind.Cape`（G3 —— CLI 零接入装备权威表）
- ② 生成隐藏 unit：**没有独立命令**（F6 —— 只在高层流程内部）
- ③ 打包 patch：**没有条目级写入口**（F2b/F3 —— 读得到 assetkey，但写不出"裸载荷离散文件"、也不能指定 header 多源合并）

**底层零件其实全都在**：

```
① 读披风清单
   GameDataAuthorityProvider.LoadAsync(gameDataDir)
   → GameDataAuthoritySnapshot 里筛 Kind == GameDataEquipmentKitKind.Cape(2)
   → 每条取 Piece 的 UnitAssetKey（TypeId + FileId）
   ★ 现成，但 CLI 没出口  ← G3

② 生成隐藏 unit
   HiddenUnitGenerator.Generate(UnitRawMeshData target, UnitStreamInfo targetStream)
   → CanonicalPlaceholderMinificationResult（极小化占位 mesh）
   ★ 现成，但只被 L3 命令内部调用  ← F6

③ 打包 patch
   条目级：IPatchEntryPayloadReader.ReadPayloadAsync(entry) → 拿三段裸字节
   写新 patch：PatchSubsetWriter.WriteAsync / WriteSelectedAsync（指定 header + 多源）
   原地改：PatchWorkspaceChangeSet.Add/Remove/Replace + PatchOperationWorkspace.Stage
   ★ 现成（4 个高层服务都在用），但 CLI 没出口  ← F2/F2b/F3
```

**结论**：这件事**完全可行**，但当前需要改产品代码加命令。补上 G3 + F6 + F2/F2b/F3 之后，Agent 就能用一条外部脚本拼出来：

```bash
# ① 读出所有披风的 unit assetkey
hd2mod gamedata equipment list --kind cape --json > capes.json

# ② 逐个生成隐藏 unit（或一次性批量）
hd2mod equipment hide-unit --keys @capes.json --output ./hidden

# ③ 把生成的隐藏 unit 打包成 patch
hd2mod patch compose --header <源patch> --from ./hidden --output ./capes-hidden-mod
```

**这个组合能复用到几十个类似场景**：隐藏任意一类装备、批量替换任意类 unit、按类别抽取资源、批量生成空壳 unit 等。

---

## 3. GUI 侧的"自动便利逻辑"——CLI 缺的正是它们的拆解

GUI 为了让人类少点几下，做了大量**自动推导**。这些推导背后是原子操作，Agent 需要能**手动控制每一步**。

| GUI 的自动逻辑 | 底层原子操作 | CLI 缺什么 |
|---|---|---|
| **头盔自动视为盔甲 archive 的一部分** | `CustomModelConversionService` 判 `Category == "Helmet"` → `slot = 0` / `body = 3`；并用 `PatchSubsetWriter.WriteSelectedAsync` 把两个 archive 的条目合到一个 patch | **F3 多源 Patch 合并**；CLI 的 `custom-model convert` 虽然接受 `来源Archive[,头盔Archive]`，但**这个"合并"本身没有独立命令**，Agent 无法对其他场景复用 |
| 装饰自动推导目标部位/身形 | `DecorationPlanningDefaults.SelectPreferredArchiveId/ResolveTargetPart/ResolveTargetBodyVariant/ToPartLayerKey` | **G11 Unit 级细粒度选择**：CLI 只能整包，无法指定 partKind/bodyVariant/layer |
| 自动挑"最大 Archive" | `SelectPreferredArchiveId` | 无只读命令能列出"候选 archive + 各自 unit 数"供 Agent 自己挑 |
| 改装饰计划后自动重建 Overwrite | `UpdateDecorationPlanAsync` → `IDecorationHostRebuildService` | CLI 无"只重建某个主体 Overwrite"的独立命令（只有 `host-derived plan rebuild`，粒度对齐但需先走 plan 流程） |
| 一键启停全部可用主体的装饰 | `ToggleDecorationForAllAvailableHostsAsync` → `ScheduleDecorationActivationBatchAsync` | **G8 批量装饰激活** |
| 护甲规整自动隐藏多余 Unit | `ArmorNormalizationService` + `HiddenUnitGenerator` | **F6 隐藏 unit 生成**无独立出口 |

---

## 4. 建议补的原子能力（按"杠杆率"排序）

"杠杆率" = 这条底座能被多少个场景复用。

| 优先级 | 建议新增 | 依赖的现成底座 | 能解锁什么 |
|---|---|---|---|
| **P0-1** | `hd2mod patch entries list <patch.toc 路径>` —— 枚举**任意 patch 文件**（不要求已登记进库）的 assetkey + 类型 + 三段大小 | `PatchTocScanner` / `PatchWorkspaceReader.ReadIndexAsync` | `mod analyze` 已能枚举**已登记 Mod**，但库外/原版/下载来的 patch 看不到。这是所有外部脚本的起点 |
| **P0-2** | `hd2mod patch entry export <patch.toc> <typeId> <fileId> --output <dir>` —— 单条目三段载荷**导出成离散裸文件** | `IPatchEntryPayloadReader.ReadPayloadAsync` | 用户原话要的"**单独将 patch 解包成每一个 assetkey 一个离散文件**"（现无任何命令能做到） |
| **P0-3** | `hd2mod patch edit <patch.toc> --add/--remove/--replace <key> --from <src> --output <dir>` —— **条目级外科手术** | `PatchWorkspaceChangeSet.Add/Remove/Replace` + `PatchOperationWorkspace.StageReference` | 现无。让 Agent 能对既有 patch 加/删/换单条 |
| **P0-4** | `hd2mod patch compose --header <patch.toc> --from <srcPatch:key>... \| --from <dir> --output <dir>` —— **指定 header + 多源合并** | `PatchSubsetWriter.WriteSelectedAsync` | **头盔并入盔甲 archive** 的底座（`CustomModelConversionService` 内部在用但不对外）；`patch repackage` 不接受自定义 header |
| **P0-5** | `hd2mod patch entry read <patch.toc> <typeId> <fileId>` —— 只读单条目载荷信息（大小/hash/是否 unit） | `IPatchEntryPayloadReader` | 脚本里做"先探再取" |
| **P0-6** | 让 `patch repackage` 等命令接受**库外 patch 路径**（而非只认 VariantId/PatchGroupId） | 同上 | 解锁"对任何来源的 patch 操作" |
| **P1-6** | `hd2mod gamedata equipment list --kind cape\|armor\|helmet\|... [--json]` —— 读游戏装备权威表 | `GameDataAuthorityProvider.LoadAsync` | **用户举例的那个场景**；所有"按类别批量处理装备"的基础 |
| **P1-7** | `hd2mod equipment hide-unit --keys @capes.json --output <dir>` —— 独立隐藏 unit 生成 | `HiddenUnitGenerator.Generate` | 隐藏任意装备；用户举例场景的第②步 |
| **P1-8** | `hd2mod material inspect\|split\|merge` —— 材质打包三件套 | `IMaterialPackagingApplicationService` | **G1，完全缺失的最大一块** |
| **P1-9** | `hd2mod unit rebuild <mod> <patch> <typeId:fileId> --output <dir>` —— 单 unit 重建（不进 `patch repair` 配方） | `SameKeyCanonicalUnitRebuilder` / `PatchAssetReconstructor` | 让"重建一个 unit"成为可复用原语 |
| **P2-10** | `hd2mod mod assets <mod-id>` —— Mod 级资产明细查询 | `IAdvancedModAssetQueryService.QueryAsync` | 替代 GUI 的高级资产表 |
| **P2-11** | `hd2mod option routes <mod-id>` / `option descendants <option-id>` —— 关系解析 | `ResolveOptionRoutes` / `GetOptionDescendantsForHost` | Agent 不用自己做图遍历 |
| **P2-12** | `hd2mod patch diagnostics <mod-id>` —— 引用链轻量/完整对比 | `IPatchGraphDiagnosticsService` | 排查 mod 兼容性 |
| **P2-13** | `hd2mod decoration activate-batch` —— 批量装饰激活 | `ApplyDecorationActivationBatchAsync` | 一次性处理多主体 |
| **P3-14** | `hd2mod mod thumbnail` / `mod assets analyze --cached` | `IModThumbnailProducer` / `IAdvancedModAnalysisService` | 补齐剩余 |

---

## 5. 一个重要的设计建议

**新增的底座命令，最好统一走"文件进、文件出"的形态**，理由：

1. **可组合**：Agent 能把上一条命令的 JSON 输出直接喂给下一条的 `--keys @file.json`；
2. **可审阅**：与现有"计划即文件"哲学一致；
3. **不破坏安全模型**：底座命令默认**只读或只写显式 `--output`**，不碰库、不碰 GameData，因此**不需要 plan/validate/apply 四段式**（对，这正好解决了我上一份文档里 P0-2/P1-4 的部分问题——底座命令天然是"无状态"的）。

建议同时提供 **`--keys` 支持从文件/stdin 读 assetkey 列表**，这样"读披风清单 → 生成隐藏 unit → 打包"可以纯管道串起来，无需中间脚本落盘。

### 5.1 建议给底座命令划一条明确的"安全线"

用户的目标（Agent 用外部脚本自由组装）与现有安全模型（写操作都要四段式 + 租约）**有张力**。建议这样划：

| 命令类别 | 是否走四段式 | 是否要租约 | 能不能碰游戏/库 |
|---|---|---|---|
| **纯只读**（读注册表、读 patch 条目、读装备表） | 否 | 否 | 只读，随便用 |
| **纯转换**（导出裸载荷、生成隐藏 unit、格式转换） | 否 | 否 | **只写 `--output` 指定的新目录**，拒绝落在库内/游戏内 |
| **产出成品**（compose 出一个新 patch 目录） | 否 | 否 | 同上；产物要不要进库由 Agent 显式再调 `import` |
| **改动库/游戏**（现有 `import` / `deploy` / `mod delete` 等） | **是** | **是** | 维持现状不动 |

**关键点：把"产出新目录"和"写进库"彻底分开。** 前者是自由的底座，后者才需要安全门禁。这样 Agent 能尽情组装，而危险操作仍然被守住——这也正是现有 `mod export` / `patch repackage` / `helmet-merge` 已经采用的模式（输出到库外新目录，不自动导入）。

---

## 6. 附：核查方法与证据

本文结论均来自**读代码**，未凭猜测。关键核查点：

| 论断 | 证据 |
|---|---|
| CLI 无 material 命令 | `grep -rn "material" HD2ModCli/*.cs` 命中全是 `materialize`（部署物化），无 `IMaterialPackagingApplicationService` 引用 |
| CLI 不接装备权威表 | `grep -rn "GameDataAuthority" HD2ModCli/*.cs` **零命中** |
| CLI 不接高级分析 | `grep -rn "AdvancedModAnalysis\|PatchGraphDiagnostics\|MaterialDeliveryFacts\|AdvancedModAssetQuery" HD2ModCli/*.cs` **零命中** |
| 离散导出底座存在 | `HD2ModAdaptation/PatchReconstruction/PatchSubsetWriter.cs` `WriteAsync(sourcePatchTocPath, outputDirectoryPath, selectedAssetKeys, ...)` |
| 多源合并底座存在 | 同文件 `WriteSelectedAsync(headerTemplatePatchTocPath, outputDirectoryPath, selections[], ...)`，注释明写"multi-source repack" |
| 隐藏 unit 底座存在 | `HD2ModAdaptation/PatchReconstruction/UnitMesh/HiddenUnitGenerator.cs` `Generate(UnitRawMeshData, UnitStreamInfo)` |
| 披风枚举底座存在 | `HD2ModCore/Domain/GameDataAuthorityFacts.cs:27` `Cape = 2`；`GameDataAuthorityProvider.LoadAsync` 读 `generated_customization_armor_sets.dl_bin` |
| Patch 条目级读写底座 | `PatchOperationWorkspace.Stage/Reference`、`PatchWorkspaceChangeSet.Add/Remove/Replace/Keep`、`PatchWorkspaceReader.ReadIndexAsync` |

### 附：Core 层可用但 CLI 未接入的接口（89 个接口中）

```
IAdvancedModAnalysisService      ← G4
IAdvancedModAssetQueryService    ← G6
IGameDataAuthorityProvider       ← G3
IMaterialDeliveryFactsService    ← G2
IMaterialPackagingApplicationService ← G1
IPatchGraphDiagnosticsService    ← G5
IModThumbnailProducer            ← G10
IEquipmentUnitCatalogService     ← G11 相关
IReferenceGraphProducer          ← 引用图
IReferenceGraphQueryIndex        ← 引用图查询
IReplacementTargetDeriver        ← 替换目标推导
IModCompatibilityAnalyzer        ← 兼容性分析
IUnitVersionProbe / IUnitVersionInformationProducer ← Unit 版本探测
IPatchEntryPayloadReader         ← F5（CLI 未直接暴露）
IPatchTocScanner                 ← F4（CLI 未直接暴露）
```
