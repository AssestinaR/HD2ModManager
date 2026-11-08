# 管理器信息中心增量化重构设计

## —— 从「四层缓存栈」到「统一增量信息图」

> 日期：2026-10-10  
> 状态：**设计草案，未实施，不修改任何源码**  
> 前置文档：`mod-information-center-implementation-plan.md`、`mod-information-center-migration-status.md`、`mod-information-center-stage0-audit.md`、`docs/agent/derived-cache-migration-baseline.md`、`fact-lifecycle-invalidation-refactoring-plan.md`  （2026-11-08 更正：原写 `docs/architecture/`，文档树已整体搬进 `docs/agent/`）  
> 本文定位：**不推翻上述文档**。上述文档解决的是「把业务收拢到唯一中心」；本文解决的是它留下的两个缺口——**细粒度请求**与**依赖传播**——并交付一份**全量信息需求裁决表**。

---

## 0. 结论摘要（先读这段）

1. 仓库**已经**有一个信息中心（`IModInformationCenter` / `ModInformationCenter`，约 707 行），而且**已经**有一套细粒度属性模型（`ModInformationPropertyKind` + `ModInformationSelector` + `ModInformationCacheKey` + `ModContentRevision`）。所以本轮不是"要不要建"，而是"**把已经存在的两块拼起来**"。
   > 该枚举在本文写作时为 **21** 种；阶段 1/2 之后为 **23** 种（新增 `GameDataIndex` / `AssetMetadata`，见 §20.2）。**下文凡写"21 条边""21 个 kind"的，都是当时的快照，不是现状。**
2. 当前真实形态是**四层各自独立的缓存栈**，每层都有自己的键模型与失效逻辑：
   ```text
   GUI 页面 / 业务服务
        │
        ▼
   ┌───────────────────────────────────────────────────────────────┐
   │ L4  DerivedStateCoordinator      快照 + 3 个脏位（content/expected/deployed）
   └───────────────────────────────────────────────────────────────┘
        │
        ▼
   ┌───────────────────────────────────────────────────────────────┐
   │ L3  ModInformationCenter         7 种产品 · 磁盘 JSON · 请求合并
   │     key = "{nodeId}|{kind}|{generation}"
   └───────────────────────────────────────────────────────────────┘
        │
        ▼
   ┌───────────────────────────────────────────────────────────────┐
   │ L2  ModInformationPatchGroupAnalysisProvider
   │     分组分析 · 内存 96 条 LRU · epoch · 请求合并
   │     key = (NodeId, PatchPath, Revision, Depth)
   └───────────────────────────────────────────────────────────────┘
        │
        ▼
   ┌───────────────────────────────────────────────────────────────┐
   │ L1  ModInformationReader         21 种细粒度属性 · 内存 LRU · epoch
   │     key = ModInformationCacheKey(NodeId, Property, Revision,
   │                                  ContentView, SelectorKey)
   └───────────────────────────────────────────────────────────────┘
        │
        ▼
   底层 PatchWorkspaceReader / PatchEntryPayloadReader / PatchUnitMeshReader
   ```
   另有中心之外的旁路：`GameDataMappingFacts`、`DeployedOverrideGraph`、`AssetArchiveIndex`、`AdvancedEquipmentIndex`、`ThumbnailService`、`RuntimeArmorRegistryService`。
3. **真正的问题不是"缓存太多"，而是"四套键模型互不承认"。** 同一个问题——"这个 Mod 的内容变了吗"——被独立回答了四次：L1 用文件修订、L2 用分组修订、L3 用 content generation、L4 用 `SavedUtc`。因此**没有任何一个地方能权威地回答"这条信息现在新鲜吗"**。
4. 本设计的核心动作只有三条：
   - **统一身份**：所有缓存键收敛为 `(FactKey, RevisionKey)`，`FactKey` 内含粒度与作用域；
   - **显式依赖**：每个事实声明依赖哪些上游事实，失效沿依赖边传播，取代"每层各自比对文件时间"；
   - **分层归位**：信息项分为 `Source / PersistentFact / DerivedMemo / Operation` 四类，分别规定"可否落盘、可否被请求"。
5. 第 4 节是**逐项裁决表**（纳入中心 / 廉价直读 / 派生记忆 / 保留旁路），第 5 节是**粒度阶梯与预留位**，第 6 节是迁移路线。

---

## 1. 现状盘点

### 1.1 四套键模型对照

| 层                                                 | 键                                                                                 | 修订来源                                                            | 存储                                       | 失效方式                                                 |                                                                 |                                                          |
| ------------------------------------------------- | --------------------------------------------------------------------------------- | --------------------------------------------------------------- | ---------------------------------------- | ---------------------------------------------------- | --------------------------------------------------------------- | -------------------------------------------------------- |
| **L1** `ModInformationReader`                     | `ModInformationCacheKey(NodeId, Property, RevisionKey, ContentView, SelectorKey)` | 文件 `name+length+mtime`（含 `.stream` / `.gpu_resources`）的 SHA-256 | 内存（Operation / Session 两套 LRU，按字节预算淘汰）   | `InvalidateNode` bump epoch；过期结果标 `Stale` 且**不写入**缓存 |                                                                 |                                                          |
| **L2** `ModInformationPatchGroupAnalysisProvider` | `AnalysisCacheKey(NodeId, PatchPath, Revision, Depth)`                            | 同一套文件 SHA-256                                                   | 内存 96 条 LRU（只存投影后的分析事实，绝不存 Unit/Payload） | epoch + `TryGetSatisfying`（深深度结果可满足浅深度请求）            |                                                                 |                                                          |
| **L3** `ModInformationCenter`                     | 字符串 \`"{nodeId}                                                                   | {kind}                                                          | {generation}"\`                          | 生产者给的 generation（如 `ContentGeneration`）              | 磁盘 JSON `data/indexes/mod-information/{kind}_{node}_{gen}.json` | `InvalidateNodeAsync` 删文件 + 取消在途；Schema/Producer 版本不符即删除 |
| **L4** `DerivedStateCoordinator`                  | 无键，三个 bool 脏位                                                                     | Profile revision / library `SavedUtc` / 活动节点集合                  | 内存快照                                     | 事件订阅（Profile / Library / Relationship / Option）      |                                                                 |                                                          |

**观察**：L1 与 L2 的修订来源完全相同（同一套文件 SHA-256），却各自算了一遍、各自存了一份。L3 的 generation 与它们的修订**没有关联**。L4 又用 `SavedUtc` 重新判断了一遍。

### 1.2 中心之外的旁路清单

| 旁路                                        | 实现                                             | 缓存/指纹现状                      | 消费者                                   |
| ----------------------------------------- | ---------------------------------------------- | ---------------------------- | ------------------------------------- |
| `GameDataMappingFactsService.MapAsync`    | 计算 `MappingGeneration`                         | **无缓存**                      | `GameDataArchiveBrowserService`（直接调用） |
| `DeployedOverrideGraphService.BuildAsync` | 枚举 Data 全部 patch、扫 TOC、**对每个部署文件与其源做 SHA-256** | **无缓存**                      | 护甲页、GameData 浏览器页、Mod 详情跨护甲计划         |
| `AssetArchiveIndexService`                | SQLite `asset-index.sqlite`                    | 有 `SourceFingerprint`        | 浏览器、护甲页、设置页、Shell 启动检查                |
| `AdvancedEquipmentIndexService`           | SQLite（网格部位事实）                                 | 有 `IsCurrentAsync`           | 跨护甲传输                                 |
| `RuntimeArmorRegistryService`             | SQLite `game_data_authority_kits` + 协议文件       | 直读（便宜）                       | 护甲页                                   |
| `ThumbnailService`                        | 静态 `cache/thumbs`，键 = 源路径 SHA-256 + 尺寸         | 有，但**无 generation / 节点失效协议** | 库页、详情页、首页、`ModLibraryService`         |
| `JsonModFileFactsCache`                   | FileFacts 磁盘 JSON                              | 有（由中心持有）                     | 中心                                    |
| `ModDataIndex`                            | 内存索引摘要                                         | 有（由中心持有）                     | 中心                                    |
| `CanonicalHiddenUnitOutputCache`          | 磁盘 `hidden-unit-cache`                         | 有                            | Adaptation 跨护甲                        |
| `LibraryDerivedDataService` 的分组指纹         | `data/patch-group-fingerprints.json`           | 有                            | 库派生刷新                                 |

> **注意**：`ThumbnailService` 与中心的 Thumbnail 产品是**双路并存**——库页、详情页、首页都同时用到两者（`LibraryPageViewModel:348/352`、`ModDetailsPageViewModel:318/320`、`PageViewModels:798/802`）。这是 `derived-cache-migration-baseline.md` 已记录的待迁移项，至今仍在。

### 1.3 GUI 页面信息需求清单

| 页面 / VM                                                                          | 打开时需要                   | 当前获取方式                                                                             | 成本                  |
| -------------------------------------------------------------------------------- | ----------------------- | ---------------------------------------------------------------------------------- | ------------------- |
| LibraryPage                                                                      | 每 Mod 图标、名称、用户状态        | 中心 Thumbnail **+** `ThumbnailService`（双路）+ `DerivedStateCoordinator`               | 中                   |
| ProfilePage                                                                      | 图标 + 预计覆盖图              | 中心 `AssetInventory` + `ProfileOverrideGraph`                                       | 中                   |
| ModDetails（普通）                                                                   | 资产标签、Unit 版本、缩略图、关系摘要   | 中心 `AssetInventory` / `UnitVersion` / `Thumbnail` / `GetAssetRelationSummaryAsync` | 中                   |
| ModDetails（高级）                                                                   | 高级资产表、材质交付、same-key 状态  | 中心 `AdvancedUnitAnalysis` + SQLite + provider 直调                                   | 高（按需）               |
| GameDataBrowser                                                                  | 全部 archive + 覆盖 overlay | `GameDataArchiveBrowserService.BuildAsync`                                         | **高（无缓存）**          |
| **ArmorRegistry**                                                                | 每护甲：是否被覆盖 + 覆盖者缩略图      | 同上 `BuildAsync` + `RuntimeArmorRegistryService`                                    | **高（无缓存，且只需要两个字段）** |
| 设置 / 首页                                                                          | 索引状态、重建按钮               | `AssetArchiveIndexService` / `AdvancedEquipmentIndexService` 直建                    | 低（仅状态）              |
| DecorationPlan / BatchDecoration / HelmetToArmor / CrossArmor / PatchRepackaging | 按需 Unit 几何 / 结构         | 中心 `AssetInventory` + L1 细粒度                                                       | 高（按需）               |

### 1.4 已具备的"可复用资产"（不要重造）

- `ModInformationSelector`：已支持按 `PatchGroups / AssetKeys / MeshInfoIndices / ArchiveIds` 限定子集，并已实现稳定的 `ToCacheKey()`。
- `ModContentRevision`：已把 `source / overwrite / effective / gamedata / analyzer` 五段独立修订组合成一个 `CacheKey`——**这正是"多输入指纹组合"的通用形态**。
- `ModInformationValueSource`：已区分 `PersistentCache / SessionCache / OperationCache / Producer / Composite / LegacyCache`。
- `ModInformationPropertyStatus`：已区分 `Missing / Fresh / Cached / Stale / Partial / Failed / Unavailable`。
- `IModInformationOperationCache`：已支持按 `ModInformationCacheKey` 的 LRU + 字节预算淘汰。
- `ModInformationPatchGroupAnalysisProvider`：已实现"深深度结果满足浅深度请求"的复用。

---

## 2. （本节已丢失，尚未重建）

> ⚠️ **本节内容在 2026-10-10 因外部进程部分回退而丢失，且无任何备份**：
> `docs/` 被 `.gitignore`（第 379 行）忽略，git 里没有它的历史；`_backup/` 建立于丢失之后；记忆日志中也没有本节的记录。
>
> 从 §0 / §1 / §3 的上下文推断，本节原为「现状盘点（§1）→ 目标架构（§3）」之间的**核心问题 / 根因定义**——§1.1 已点出「四套键模型互不承认」，§9 速览也把「四套键模型互不承认 → 没有唯一的新鲜度权威」列为要解决的核心问题。
>
> **本节尚未重建。** 如需补上，建议基于 §0 与 §1 重写一节「核心问题定义」，而不是假装它是原文。

---

## 3. 目标架构：增量信息图（IIG）

### 3.1 节点模型

```text
FactNode
  Key          : FactKey          // 身份：粒度 + 作用域
  Revision     : string           // 输入指纹（必须廉价可得）
  Dependencies : FactKey[]        // 上游事实
  Category     : Source | PersistentFact | DerivedMemo | Operation
  Persistence  : Disk | Memory | None
  Producer     : Func<Request, ValueTask<object>>
  InvalidateOn : DomainEvent[]    // 领域事件（冗余于 Dependencies，便于 UI 主动刷新）
```

中心只做一件事：**给定 `FactKey`，返回新鲜值**。

- 命中且新鲜 → 直返；
- 本节点 `Revision` 变了 → 只重算本节点；
- 本节点新鲜但依赖过期 → 先让依赖重算，再决定是否需要本节点重算。

### 3.2 失效传播算法（伪码）

```text
Resolve(key):
    node = registry[key]
    revision = node.ComputeRevision()          // 廉价：只读 FileInfo / 内存状态
    cached = store.TryLoad(key, revision)
    if cached != null: return cached           // O(1) 命中

    inputs = [Resolve(dep) for dep in node.Dependencies]   // 递归保证上游新鲜
    if cached == null or revision != cached.Revision:
        value = node.Producer(inputs)          // 只重算本节点
        store.Save(key, revision, value)       // 仅当 Category 允许落盘
    return value
```

**关键不变量**：`ComputeRevision()` 必须"廉价且完整"——只允许读 `FileInfo` / 内存状态 / 已缓存的 JSON 摘要，**绝不为了算指纹去扫内容或算 SHA-256**。现有 `DeployedOverrideGraph` 违反此条（先全量 SHA-256 才得到 generation），这是它无法被缓存的门槛。

### 3.3 FactKey 设计（统一身份）

```text
FactKey = Kind + Scope + RevisionSource
Scope   ∈ { Node(nodeId) | GameData(gameDataDir) | Deployment(gameDataDir)
          | Library | Profile(profileId) | Global }
```

这是对现有两套键的**收敛**，不是新增第三套：

| 现有键                                               | 收敛为                                                                         |
| ------------------------------------------------- | --------------------------------------------------------------------------- |
| L1 `(NodeId, Property, Revision, View, Selector)` | `Kind=Property, Scope=Node, Revision, View, Selector`                       |
| L3 `(kind, nodeId, generation)`                   | `Kind=Product, Scope=Node, Revision=generation`                             |
| （新增）game-data 侧                                   | `Kind=GameDataIndex/Mapping/Metadata, Scope=GameData, Revision=fingerprint` |
| （新增）部署侧                                           | `Kind=DeployedGraph, Scope=Deployment, Revision=deploymentFingerprint`      |
| （新增）聚合视图                                          | `Kind=ArmorCoverage/ArchiveBrowserView, Scope=Library, Revision=组合指纹`       |

### 3.4 四类节点与现有资产的映射（收编，而非新建）

| 类别                 | 定义            | 可否落盘             | 可否被请求   | 现有对应物                                                                                                                                                         |
| ------------------ | ------------- | ---------------- | ------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **Source**         | 外部输入状态，本身就是真相 | 否                | 否（只作依赖） | `library.json`、`profiles.json`、部署记录、游戏目录、`settings.json`、option/decoration 激活                                                                                 |
| **PersistentFact** | 可由指纹重建的事实     | **是**            | **是**   | PatchCatalog、AssetInventory、ReferenceGraph、UnitVersion、AdvancedUnitAnalysis、Maintenance、**MappingFacts（新）**、**DeployedOverrideGraph（新）**、**GameDataIndex（新）** |
| **DerivedMemo**    | 由事实组合出的会话投影   | **否**（只 memoize） | 是       | ProfileOverrideGraph、ProfileMaterialDiagnostics、ModUserStatus、GameDataArchiveBrowserSnapshot、**ArmorCoverageView（新）**                                         |
| **Operation**      | 用户显式触发的重型操作   | 输出到产物目录          | 否       | same-key 生成、材质拆分、跨护甲、索引重建                                                                                                                                     |

> 与 `fact-lifecycle-invalidation-refactoring-plan.md` 的分类一致（SourceState / PersistentFact / SessionProjection / PageProjection / OnDemandOperation / ExternalValidation）。本文把后三类合并为 `DerivedMemo + Operation`，以减少维度。

---


## 4. 信息需求全量裁决表（核心交付物）

**裁决口径**

- **纳入中心**：值得拥有统一指纹 + 统一失效 + 可被请求；
- **廉价直读**：单次读取成本远低于"判断它是否过期"的成本 → **不进中心**，调用点直接读；
- **派生记忆**：由事实组合，**禁止落盘**，只 memoize；
- **保留旁路**：因外部约束（CLI 只读、Adaptation 边界）暂不收编，但**登记依赖**。

| #  | 信息项                                                    | 消费者               | 现状                            | **裁决**           | 粒度 (Kind)                                | 依赖                                                 | 失效事件                                          |
| -- | ------------------------------------------------------ | ----------------- | ----------------------------- | ---------------- | ---------------------------------------- | -------------------------------------------------- | --------------------------------------------- |
| 1  | PatchCatalog (FileFacts)                               | 部署、库刷新            | 中心✅                           | 纳入（已有）           | `Property.PatchCatalog`                  | Mod 目录文件集                                          | NodeChanged/Added/Removed                     |
| 2  | AssetInventory                                         | 库派生、预计图、状态、导入、浏览器 | 中心✅                           | 纳入（已有）           | `Property.AssetInventory`                | 1 + analyzer 版本                                    | 同上                                            |
| 3  | ReferenceGraph                                         | 材质诊断、材质交付、高级查询    | 中心✅                           | 纳入（已有）           | `Property.ReferenceClosure`              | 2                                                  | 同上                                            |
| 4  | UnitVersion                                            | 详情、首页             | 中心✅                           | 纳入（已有）           | `Property.Compatibility`                 | 2                                                  | 同上                                            |
| 5  | AdvancedUnitAnalysis                                   | 高级详情              | 中心✅                           | 纳入（已有）           | `Property.UnitStructure`                 | 2（Full）                                            | 同上                                            |
| 6  | Thumbnail                                              | 库、详情、首页、护甲        | 中心✅ **+ ThumbnailService 双路** | **纳入（合并双路）**     | `Property.Thumbnail`                     | Mod 图标文件                                           | IconChanged                                   |
| 7  | MaintenanceAnalysis                                    | 一键修复              | 中心✅                           | 纳入（已有）           | `Property.Maintenance`                   | 2                                                  | 同上                                            |
| 8  | **GameDataIndex**（archive summaries + unit part facts） | 浏览器、护甲、跨护甲、设置     | 旁路 SQLite                     | **纳入（新增）**       | `GameDataIndex`                          | 游戏目录 + `archivehashes.json` + schema/classifier 版本 | GameDataFingerprintChanged / IndexRebuilt     |
| 9  | **AssetMetadataCatalog**                               | mapping 投影        | 旁路文件读                         | **纳入（新增）**       | `AssetMetadata`                          | metadata 文件                                        | MetadataChanged                               |
| 10 | **GameDataMappingFacts**                               | 浏览器、护甲、预计图        | 旁路**无缓存**                     | **纳入（新增）**       | `GameDataMapping`                        | 8 + 9                                              | 上游任一                                          |
| 11 | **DeployedOverrideGraph**                              | 浏览器、护甲、详情跨护甲      | 旁路**无缓存**                     | **纳入（新增）**       | `DeployedGraph`                          | Data 文件集 + activation state                        | DeploymentChanged                             |
| 12 | AdvancedEquipmentIndex（网格部位事实）                         | 跨护甲               | 旁路 SQLite                     | 纳入（新增，可选）        | `EquipmentMeshFacts`                     | 8 + `UnitPartAnalyzerVersion`                      | 上游                                            |
| 13 | ArchiveHashes                                          | 索引、mapping        | 文件读                           | 廉价直读（登记依赖）       | —                                        | —                                                  | —                                             |
| 14 | **Armor catalog**（`game_data_authority_kits`）          | 护甲页               | SQLite 直读                     | **廉价直读**（用户已明确）  | —                                        | —                                                  | —                                             |
| 15 | Armor registry protocol / workspace 快照                 | 护甲页               | 文件读                           | 廉价直读             | —                                        | —                                                  | —                                             |
| 16 | Custom model catalog                                   | 护甲页、详情            | 文件读                           | 廉价直读             | —                                        | —                                                  | —                                             |
| 17 | Settings / Library / Profiles / Option / Decoration 激活 | 全局                | JSON 读                        | 廉价直读（**Source**） | —                                        | —                                                  | —                                             |
| 18 | Mod 图标路径（`ModIconLocator`）                             | 库派生               | fs 扫描                         | 廉价直读（随 #19 投影）   | —                                        | —                                                  | —                                             |
| 19 | ProfileOverrideGraph（预计）                               | Profile 页、状态      | `DerivedStateCoordinator`     | **派生记忆**         | `ExpectedGraph`                          | 2（活动）+ 10                                          | ProfileChanged / ModChanged / GameDataRebuilt |
| 20 | ProfileMaterialDiagnostics                             | Profile 页         | `DerivedStateCoordinator`     | **派生记忆**         | `MaterialDiagnostics`                    | 19 + 3                                             | 同上                                            |
| 21 | ModUserStatus                                          | 状态投影              | 纯内存                           | **派生记忆**         | `UserStatus`                             | 19/20 + option 激活                                  | 同上                                            |
| 22 | GameDataArchiveBrowserSnapshot                         | 浏览器页              | 每次 `BuildAsync`               | **派生记忆**         | `ArchiveBrowserView`                     | 8+10+11+2                                          | 上游任一                                          |
| 23 | **ArmorCoverageView**（护甲显示事实）                          | 护甲页               | 每次 `BuildAsync`               | **派生记忆**         | `ArmorCoverage`（Selector=护甲 archive ids） | 11 + 10（+ 6 图标）                                    | DeploymentChanged / MappingChanged            |
| 24 | same-key / 材质拆分 / 跨护甲 / 索引重建                           | 用户点击              | Operation                     | **Operation**    | —                                        | —                                                  | 手动                                            |
| 25 | ReShade ini / Steam 目录 / 外部安装事实                        | 部署、导入             | 文件读                           | 廉价直读             | —                                        | —                                                  | —                                             |

**表的关键含义**

- **只需新增 5 项**（#8、#9、#10、#11、#12），中心就覆盖了全部"值得缓存"的信息；
- **护甲页的问题被 #23 一句话解决**：它只要 `ArmorCoverage`（Selector 限到护甲 archive）+ 中心 Thumbnail，**不再需要 `BuildAsync`**；
- **#14 明确不进中心**（你已指出：直接问 SQL 就够）；
- **#19–#23 一律不落盘**，这是防"缓存山"的关键纪律。

---

## 5. 粒度分层与预留

### 5.1 粒度阶梯

| 级别     | 名称             | 内容                                            | 可否落盘             |
| ------ | -------------- | --------------------------------------------- | ---------------- |
| **L0** | SourceRevision | 文件 `(name,length,mtime)` / JSON 文件哈希 / 游戏目录指纹 | 否（本身即输入）         |
| **L1** | EntityFact     | 单个 Mod / 单个 archive / 单个护甲                    | 是                |
| **L2** | PropertyFact   | 实体 × 属性（21 种 `PropertyKind` 已就位）              | 是                |
| **L3** | SubsetFact     | 属性 × 选择器子集（`ModInformationSelector` 已就位）      | 是（确有子集语义时）       |
| **L4** | CompositeView  | 跨实体聚合投影（浏览器快照、护甲覆盖视图）                         | **否**（只 memoize） |

> L4 禁止落盘的原因：它随任意上游变化而变化，一旦落盘就又多了一个"会忘记失效"的缓存。

### 5.2 按失效频率聚类（决定细分到哪一级）

| 频率档                | 典型信息                                                           | 策略              |
| ------------------ | -------------------------------------------------------------- | --------------- |
| **高频**（每次部署变）      | DeployedGraph、ArmorCoverageView、UserStatus                     | 只记忆，不落盘         |
| **中频**（每次 Mod 内容变） | AssetInventory、ReferenceGraph、PatchCatalog                     | 落盘 + generation |
| **低频**（游戏更新才变）     | GameDataIndex、AssetMetadata、GameDataMapping、EquipmentMeshFacts | 落盘 + 指纹         |
| **极低频**（几乎不变）      | Thumbnail、UnitVersion                                          | 落盘，图标单独按文件指纹    |

**细分判据**：

```text
细分收益 = Σ(避免的重算) − Σ(额外指纹探测成本) − 复杂度成本
```

只有"单条生产成本 ≫ 判断它是否过期的成本"**且**"两个属性变化频率不相关"时才值得拆。

- icon 与 name：同在 Mod 元数据、频率接近 → **可合**；
- override 与 icon：一个每次部署变、一个几乎不变 → **必须拆**。

即：**细分依据是失效频率，不是字段数量。**

### 5.3 预留的"廉价细分位"

不必预先实现所有细分，但要在键模型里**预留**，避免以后加不动。好消息是——**键结构已经足够通用，预留成本≈0**：

| 预留维度                | 现状                                                     | 未来新增成本                 |
| ------------------- | ------------------------------------------------------ | ---------------------- |
| **Selector**（子集）    | `ModInformationSelector` 已在键里                          | 要"只算某几个 AssetKey"→ 零成本 |
| **Scope**（作用域）      | 本设计扩展为 Node/GameData/Deployment/Library/Profile/Global | 加新作用域 → 不改键结构          |
| **Property**（属性）    | 21 种枚举，可增长                                             | 新属性 → 加枚举值 + 生产者       |
| **Revision**（指纹组合）  | `ModContentRevision` 已含 5 段                            | 新输入 → 加一段，不改缓存结构       |
| **ContentView**（视图） | `Source / Overwrite / Effective` 已在键里                  | 要"源视图 vs 有效视图"→ 零成本    |

**真正要做的不是"预留更多字段"，而是把 L3 从"7 个命名方法"改成"通用 `Request(FactKey)`"。**

---

## 6. 迁移路线

| 阶段        | 目标             | 关键改动                                                                                                                                         | 验收                                                   |
| --------- | -------------- | -------------------------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------------------- |
| **0（本文）** | 设计与全量裁决        | 只写文档，不改代码                                                                                                                                    | 本文通过评审                                               |
| **1**     | 统一身份           | 中心增加通用 `RequestAsync<T>(FactKey)`，内部委托现有 7 个产品；`ModInformationKind → PropertyKind` 改为**注册表**（`ModInformationRequest.EffectiveProperty` 已是雏形） | 现有页面行为不变；新增事实只需注册                                    |
| **2**     | 收编 game-data 侧 | #8/#9/#10 登记为 `Scope=GameData` 的 PersistentFact；`GameDataArchiveBrowserService.BuildAsync` 改为向中心请求，自身退化为**纯投影**                              | 浏览器页与护甲页不再直调 `MapAsync` / `GetArchiveSummariesAsync` |
| **3**     | 收编部署侧          | #11 登记为 `Scope=Deployment`；`DeployedOverrideGraphService` 的 SHA-256 校验改为**仅在指纹变化时执行**                                                        | 部署未变时二次打开零重算                                         |
| **4**     | 护甲页改造          | 改为请求 `ArmorCoverage`（Selector=护甲 archive ids）+ 中心 Thumbnail，停止调用 `BuildAsync`                                                                | 首开耗时量化下降；改图标不触发覆盖重算                                  |
| **5**     | 合并缩略图双路        | `ThumbnailService` 降级为中心 Thumbnail 产品的**渲染器**（只解码/落盘，不再自己判新鲜）                                                                                | 全库仅一套缩略图失效协议                                         |
| **6**     | 护栏测试           | 扩展架构测试；新增行为测试（见第 7 节）                                                                                                                        | 测试全绿                                                 |

> **顺序理由**：阶段 2/3 先把"中心之外"收进来，阶段 4 的护甲页才能"只要两个字段"；反过来先改护甲页，就会在中心外面又贴一张膏药。

> **阶段 2 的现状（2026-10-11，见 §19.1 / §20.2）**：本表与 §15.5 对"阶段 2"给了两个互相冲突的定义。
> 实际执行拆成了两半：
> - **登记表那一半已完成**——#8/#9 作为 `GameDataIndex` / `AssetMetadata` 登记为源事实，#10 连上这两条边（§20.2）；
> - **页面那一半未做**——"浏览器页与护甲页不再直调 `MapAsync` / `GetArchiveSummariesAsync`"要改页面代码，
>   验收口径随之变化，**留待单独拍板**（§20.8）。所以上表阶段 2 的"验收"列**尚未满足**。
>
> 中间的 §15.5 与 §19 用的是另一个定义（**显式依赖 DAG**），那一版**已完成**（§19）。

#### 6.1 迁移路线总进度（2026-10-11 更新）

**回答"信息中心是否已全面重构完毕"：结构上已完成。** 阶段 0/1/2/3/4/5/6 均已完成，
阶段 6 的护栏已补齐到"能落地的口径"（§25），只剩量化验收（用户已明确"先不急着测提升速度"）。

| 阶段 | 状态 | 已完成的部分 | 还差什么 |
|---|---|---|---|
| **0** 设计与全量裁决 | ✅ 完成 | 本文档 | — |
| **1** 统一身份 | ✅ 完成 | `FactKey`/`FactSubject`/`FactRevisionKey`、登记表、`RequestAsync<T>` 单一入口 | — （§15 设计 / §17 实施） |
| **2** 收编 game-data 侧 | ✅ 完成 | **登记表半边**：`GameDataIndex` / `AssetMetadata` 入表为源事实，`GameDataMapping` 连上这两条边；并补上了该服务原本完全没有的缓存（§20.2 / §20.3）。**页面半边**：`GameDataArchiveBrowserService` 退化为纯投影，只认识 `IGameDataFactSource`（§22.6） | — |
| **3** 收编部署侧 | ✅ 完成 | **缓存半边**：按来源指纹记忆，"部署未变时二次打开零重算"（§21）。**登记半边**：`DeployedGraph` 入表，经中心统一身份与合并（§22） | — |
| **4** 护甲页改造 | ✅ 完成 | 改为请求 `ArmorCoverage`（`Selector`=护甲归档集合），**不再调用 `BuildAsync`**；图标仍由页面按赢家节点做父链查找（§23）。**并在启动预热阶段把这一页的 VM / 数据 / 视图一并预热**（§24） | 验收口径是"首开耗时量化下降"——用户已在真实 Mod 库上实测"快了好几倍"，但**没有留下可复现的数字**；正式版发布前若要写进说明，需要一次带计时的实测 |
| **5** 合并缩略图双路 | ✅ 完成 | 指纹抽成唯一定义 `ThumbnailFingerprint`，事实缓存与磁盘缓存共用；`ThumbnailService` 降级为纯渲染器；事件驱动的删除协议整个消失（2 处调用点 → 0）；启动时一次性清扫负责 GC（§27） | — |
| **6** 护栏测试 | ✅ 完成（口径已澄清） | Manager 半边（§19.8）+ 登记表完整性（§19.4）+ 运行时主体与登记表一致性（§22.8）+ 护甲页不得构建浏览器（§23.6）+ **整包读取收口与投影缝**（§25.2）+ **图标不进覆盖事实**（§25.3）+ **启动预热的正确性前提**（§24.8）+ **缩略图单一定义与单一 GC**（§27.7） | 只剩"改部署 → 覆盖重算、缩略图不重算"的对拍用例未写（§25.5 记了它真正的落点是在两个服务的实现测试里，不是中心测试）；原先一并列出的"关闭页面取消私有请求"已由 §27 消解（全局取消已不存在） |

> **§28（2026-10-11）补完了阶段 2 的最后一块**：§20.8 列出的"6 个业务服务仍直调 `MapAsync`"
> 已经收口——它们现在都只向中心要映射，`IGameDataMappingFactsService` 从这 6 个服务的构造函数里整个删掉，
> 并由新护栏 `GameDataMappingIsRequestedThroughTheCenterOnly` 钉住。详见 §28。

> **一个曾经贯穿全局的前置（已解决）**：中心此前**所有**请求都挂在 `ModNode` 上
> （`RequestXxxAsync(ModNode node, ...)` + 每节点一个 `ConcurrentDictionary<FactRevisionKey, …>` 任务表 + `InvalidateNodeAsync`），
> 而 game-data 侧与部署侧的事实**没有节点**——它们的身份是"游戏目录 + 来源指纹"。
> 这就是阶段 2 页面半边与阶段 3 登记半边一直没做的真正原因。
> §22 把"无节点事实"作为一种独立形态补进中心，§23 又在同一形态上加了第四条（`ArmorCoverage`），前置从此解除。

> **§24 / §25 与迁移路线的关系**：它们**不在**原表的六个阶段里。
> §24（启动预热）是阶段 4 验收口径的延伸——"首开快"不止靠少读，也靠提前读；
> §25 是阶段 6 的收尾，并把护栏 1 的"业务服务半边"从一句过宽的口号改成了能落地的两条断言。

---

## 7. 护栏与验收测试

1. **架构测试（扩展现有 `ModInformationCenterArchitectureTests`）**
   - 已有：`HD2ModManager` 不得 `new` 生产者/缓存；
   - **新增**：`HD2ModManager` 与业务服务不得直接调用  
     `IDeployedOverrideGraphService.BuildAsync`、  
     `IGameDataMappingFactsService.MapAsync`、  
     `IAssetArchiveIndexService.GetArchiveSummariesAsync`。
   - **现状（2026-10-11 收尾，详见 §25）**：Manager 半边已实现（`ManagerDoesNotConsumeTheGameDataBypassServicesDirectly`），
     护甲页那一条也已落地（`ArmorPageProjectsCoverageInsteadOfBuildingTheGameDataArchiveBrowser`，§23.6）。
     **业务服务半边按原文口径会误伤 7 个正当调用点**（`MapAsync` 的合法调用方，分布在 6 个文件），
     因此改按**方法**收口到两个"整包事实"（`GetArchiveSummariesAsync` 与整个 `IDeployedOverrideGraphService`），
     再加上 `GameDataArchiveBrowserProjectsThroughTheFactSourceOnly` 那道投影缝。三条断言均已落地。
     **2026-10-11 更新（§28）**：那 7 个 `MapAsync` 调用点已全部收口到中心，
     映射因此也能被约束了——新增 `GameDataMappingIsRequestedThroughTheCenterOnly`（两条断言），
     于是护栏 1 对三个旁路服务的覆盖现在是**完整的**。
2. **依赖声明完整性测试**
   - 反射扫描事实注册表：每个 `FactKind` 必须声明非空 `Dependencies`（`Source` 类除外）。
3. **行为测试**
   - 未变指纹 → 不重新调用 analyzer（已有部分）；
   - **改图标 → `ArmorCoverage` 不重算**；
   - **改部署 → `ArmorCoverage` 重算，但 `Thumbnail` 不重算**；
   - 关闭页面 → 取消该页面私有请求，不取消全局任务。
   - **现状（2026-10-11 收尾，详见 §25.3 / §25.5）**：第一条（改图标不重算）已落地——
     `ArmorCoverageDependsOnItsThreeUpstreamsAndNeverOnThumbnails`（直接依赖 + 传递闭包）
     与 `ArmorCoverageProductionNeverTouchesThumbnails`（生产代码）。
     第二条的对拍用例**未写**，原因不是懒：中心的无节点账本在生产完成后即清账（§22.4），
     "重算 / 不重算"在中心层面只能看上游生产者的调用次数，而那两个生产者的记忆在**实现内部**——
     所以这条用例真正的落点是两个服务的实现测试（前半句已由 §21 的 `DeployedOverrideGraphServiceTests` 覆盖）。
     第四条（关闭页面 → 取消私有请求）**未写**。
4. **可观测性**
   - 复用 `DiagnosticRecorded` / `ProductionStarted`，按 `FactKey` 统计命中率与生产耗时——**"该往哪细分"由数据决定，而非拍脑袋**。

---

## 8. 风险与"不做的事"

### 风险

| 风险               | 说明                              | 对策                           |
| ---------------- | ------------------------------- | ---------------------------- |
| **指纹完整性**（唯一硬风险） | 指纹漏掉任一输入 → **静默**返回陈旧数据，比无缓存更难查 | 依赖声明 + 完整性测试 + "刷新"按钮强制绕过    |
| **冷启动**          | 细粒度只解决"变更后的重算范围"，**不解决首开**      | 必须配磁盘持久 + 廉价前置指纹门            |
| **中心膨胀**         | 707 行继续长成"上帝类"                  | 中心只做通用"请求-生产-缓存"管道，业务语义留在生产者 |
| **迁移面广**         | 阶段 2/3 触及多个页面                   | 分批 + 每批回归 + 可回滚              |

### 明确不做

- ❌ 不新建第二套中心；
- ❌ 不为"便宜直读"项（护甲 catalog、协议快照、JSON 状态）套中心；
- ❌ 不让 L4 派生视图落盘；
- ❌ 不在页面打开时同步跑索引重建；
- ❌ 不为了细分而细分——**按失效频率聚类，而不是按字段数拆**。

---

## 9. 一页速览

```text
要解决的核心问题：四套键模型互不承认 → 没有唯一的新鲜度权威
要做的三件事：统一身份 (FactKey) + 显式依赖 (DAG) + 分层归位 (四类)
要新增的事实：5 项（GameDataIndex / AssetMetadata / GameDataMapping /
                    DeployedGraph / EquipmentMeshFacts）
要改造的页面：护甲页（改为请求 ArmorCoverage + Thumbnail）
要合并的重复：缩略图双路（中心产品 vs ThumbnailService）
要守住的纪律：派生视图永不落盘；细分按失效频率；指纹必须廉价且完整
```

---

## 10. 可精简项清单（第三轮机械审计）

> 审计时间 2026-10-10。方法：全仓 `grep -rn --include=*.cs` 机械引用计数（覆盖 10 个项目 + 3 个工具工程）；「零引用」= 符号只出现在定义处。`CoreServices` 是唯一装配点，故「接口/实现存在但无 `CoreServices.CreateX(` 调用」= 生产路径不存在。

### 10.1 A 组 · 零引用可直接删

| 项 | 位置 | 说明 |
|---|---|---|
| `patch-group-fingerprints.json` | `data/indexes/` | 全仓 0 命中，已被 `JsonModInformationCache` 取代 |
| `GitHubArchiveHashesSync` | `HD2ModCore/Infrastructure/` | 无工厂无调用；已被 `GitHubAssetMetadataSyncService` 覆盖（后者活） |
| `game_data_unit_part_scans` DDL | `SqliteSchema.cs:206` | `DROP TABLE IF EXISTS`，但该表从未 CREATE |

### 10.2 B 组 · 无生产调用者

- **B1 连测试都没有**：`ModAssetRoleFactsService` / `IModAssetRoleFactsService` / `CreateModAssetRoleFactsService`；`AdaptationPatchGroupAnalysisProvider`（无工厂无调用，已被 `ModInformationPatchGroupAnalysisProvider` 取代）。
- **B2 仅测试引用**：`ConflictDetector` + `AssetKeySetProvider` 簇；`ReplacementTargetDeriver`；**孤儿 Adaptation 分析子图** = `AdaptationAnalysisBuilder` + `ResourceReuseDetector` + `ReplacementIntegrityAnalyzer` + `GameItemResourceRelationBuilder` + `GameDataResourceGraphBuilder`（顶层入口无人调用，下挂四个 builder 只服务它）。
- **B3 脚手架簇**（接口 + 实现 + 工厂，但零调用）：`GameDataLocator`、`MaterialDependencyValidator`、`ModManifestImporter`、`PatchTocFileCollector`。**详见 §16。**
- **B4 死工厂**（类活着、工厂没人用）：`CreatePatchGroupAnalysisProvider`、`CreateDependencyGraphAnalysisProvider`、`CreateFullPatchAnalysisProvider`（中心走 `ForDepth()` 而非一深度一工厂）、`CreatePatchTocScanner`、`CreateGameDataAuthorityProvider`。

### 10.3 C 组 · mod-facts 库「只写不读」表

`patch_groups`、`mod_fact_snapshots`、`advanced_mod_analysis_snapshots` 均只有 INSERT/DELETE（`mod_fact_snapshots` 仅被一次性迁移 SELECT），**从不作为读取源**；`schema_info(version)` 只写不读 → **mod-facts 库没有版本闸门**。

对比：game-data 权威库（`SqliteSchema` SchemaVersion=10）版本闸门是活的（`AdvancedEquipmentIndexService.cs:45-46`）。

### 10.4 D 组 · 项目级

- `ManagedMain` 仍在 `.sln`，无任何项目引用 → 建议摘出。
- `Decryptor.Tests` **磁盘有但不在 `.sln`** → 从未被跑过。
- `HD2RuntimeArmorOverride` 不在 `.sln`，保留为独立资产。

> ✅ 上述三项**已处理**：ManagedMain 已归档并移出 `.sln`、`Decryptor.Tests` 已加入 `.sln`（见提交 `672c8e7`）。

### 10.5 必须保留（防误删）

`GameDataAuthorityProvider`、两个 `PatchTocScanner`（Adaptation 版 + Core 版都在用）、`PatchAnalysisDepth` 三档、`ModInformationPatchGroupAnalysisProvider`、`ModContentFactsService`、导入链（`ModLibraryImporter` / `ModImportExecutionService` / `ModLibrarySynchronizer`）、`HelmetToArmorMergeService` / `DecorationHostRebuildService` / `CanonicalCrossArmorOrchestrator` / `ArmorNormalizationService`，以及一批逐项回查确认在跑的 service。

### 10.6 命名冲突（属重构收敛对象，非清理对象）

`PatchTocScanner` / `IPatchTocScanner` **各有两份**（`HD2ModAdaptation.PatchReconstruction` 版 与 `HD2ModCore.Infrastructure` / `Application` 版），靠 `using` 别名共存（如 `GameDataAuthorityProvider.cs:7`）。是 §1.1「四套键模型互不承认」的缩影。

### 10.7 执行顺序建议

1. 零风险：A 组
2. 低风险：B1、B4（只删工厂不删类）→ 跑 Core/Cli 测试
3. 中风险：C 组（先停写、观察、再删 DDL；注意老用户库里表仍在）
4. 待用户决策：B2 / B3 / D 组（等于宣布功能不做，属产品决策）

### 10.8 当时的提示（⚠️ 已被 §11.6 推翻，保留作决策轨迹）

> B3-3 `ModManifestImporter` 与 B2 的 `GameDataResourceGraphBuilder` / `GameItemResourceRelationBuilder` 正好覆盖用户需求里的「解析 patch 详细信息」「模型作为装饰临时合并」——方向正确但当前是孤儿。删之前先判断是否「以后要接上」；若要用，应**接进信息中心**（§4 裁决表已留位置），而非删掉。

---

## 11. CLI 实测裁定

> 用 CLI 实测「合并装饰」「解析 patch 详细信息」，看上线功能由谁提供、谁占工位不干活。

### 11.1 实测方法要点

- **旧二进制不可信**：`HD2ModCli/bin/Release` 报协议 1.26，源码是 1.27（`CliProjections.cs:8`）→ 已从源码重建。
- 环境：用户真实管理器目录（`HD2ModManager/bin/Debug/net10.0-windows`）+ 真实库（**106 mod / 98 带 patch**，`D:\SteamLibrary\steamapps\common\HD2ModManager\mods`）+ 真实 GameData。
- 只跑 read-only 命令，未写入用户库。临时探针文件已清理。

### 11.2 实测一：解析 patch 详细信息

`mod analyze 97c02c5f-...` → isReady=true，`analyzerVersion=patch-group-v6-streamless-compatibility`，depth=full，**94 条目 / 590 引用 / 82 个 unit**。

**提供者 = `PatchGroupAnalyzer`。** `CliModAnalysis.cs:10-15` 注释明确说它**刻意绕开** ModInformationCenter / ModInformationReader / SQLite，自己 new `PatchGroupAnalyzer`。Manager 侧走 `ModDetailsPageViewModel` → 中心 → `ModInformationPatchGroupAnalysisProvider` → 同一个 `PatchGroupAnalyzer`。

### 11.3 实测二：合并装饰

`host-derived inspect 9a1d97a4...` → 宿主 `水墨牛牛替换AC-1&AC-2`（relPath `RS-100 Sanctioner+...`），**current/planned 各 8 个装饰**，sourceStorageMode=PatchSnapshot，overwrite: exists=true / isManaged=true / hasDecorationMarker=true / fileCount=3 / totalBytes=**85675006 (85.6MB)**。

磁盘核对：`mods/RS-100 Sanctioner+.../Overwrite/` 内有 `.hd2-decoration-overwrite`(=generated)、`9ba626afa44a3aa3.patch_0`(883KB)、`.gpu_resources`(84.8MB)。

**提供者 = `DecorationHostRebuildService` + `DecorationHostTargetBinder` + `CanonicalDecorationUnitAppender` + `HostDerivedOverwriteService`**（CLI 与 Manager `ModLibraryService.cs:417-418` 同源）。

业务规则（来自真实报错 `DecorationSourceKindUnsupported`）：Decoration 类型 mod **不能**作为装饰来源，只有带 Patch 的普通/选项 Mod 或 PatchSnapshot 装饰 Mod 可以。

### 11.4 裁定（推翻了 §10.8 的判断）

`GameItemResourceRelationBuilder` / `GameDataResourceGraphBuilder` / `ModManifestImporter` / `AdaptationAnalysisBuilder` / `ResourceReuseDetector` / `ReplacementIntegrityAnalyzer` —— 全仓库引用点穷举后，**除自身文件外只被自身测试或同子图的死类引用，生产调用 = 0**。

- **不是「没做完的功能」，而是「被更好实现取代、忘了删的重复实现」。**
- §10.8 说「方向对、建议接进信息中心」→ **更正为：直接删（连测试一起）**。若接进中心，等于给已有更好实现的路径再养一套，正是「屎山」。
- `ModManifestImporter` 的取代关系：CLI 用 `ModImportExecutionService`，Manager 用 `ModLibraryImporter`（`ImportService.cs:67`），manifest 由 `StandardModManifest.TryLoad` 解析。
- 唯一保留判断：`GameDataResourceGraphBuilder` 的 Node/Edge/Graph 模型在概念上接近「事实依赖图」，删前花 5 分钟确认是「同构重复」还是「依赖图预留骨架」→ **结论见 §14**。

### 11.5 顺带坐实的两件事

1. **版本常量双写实测坐实**：`PatchGroupAnalyzer.cs:10-12` 与 `ModInformationPatchGroupAnalysisProvider.cs:21-23` 各写一份**完全相同**的三常量（`patch-group-v6-streamless-compatibility` ×2 + `patch-group-v7-...`）。两处不一致就会造成「中心说过期、CLI 说没过期」的分裂。
2. **`patch-group-fingerprints.json` 代码零引用是真的，但个人管理器目录里文件还在**：`HD2ModManager/bin/Debug/net10.0-windows/data/patch-group-fingerprints.json` 与 `.tmp` 残留，删代码后需单独清理。

### 11.6 对 §10.8 的更正（本节是决策轨迹的关键节点）

§10.8 的建议方向是「接进信息中心」，其隐含前提是「这些是未完成的功能」。§11 的 CLI 实测**证伪了该前提**：这些能力在生产里**已经有活的实现**（`PatchGroupAnalyzer` 提供 patch 解析，`DecorationHostRebuildService` 提供装饰合并），孤儿子图是**同构重复**而非**功能空缺**。故裁定从「收编」改为「删除」。

---

## 12. 第二轮死代码排查（可达性分析 + CLI 能力反查）

> 要求「再通过 CLI 真实能力判断是否还有残留无用代码」。

> **本节结论已执行（2026-10-11，见 §20.4）**：§12.3 的 4 个簇与 §12.4 的累计清单中**仍然不可达的那部分**、以及 `CoreServices` 的 5 个零调用工厂**全部删除**；
> 不可达类型 **40 → 0**，`check_dead_code.py` 的两个方向皆空。
> （§12.4 记的是 `_diagnostics/` 归档**之前**的 63 个类型；归档 + 加护栏之后剩 40 个，本次清完。）
> 本节保留为**判据与推理过程的留档**——下次排查照 §12.1 的三层方法走。

### 12.1 方法（三层交叉验证）

1. **机械可达性脚本**：1587 个生产类型，从 4 个入口（`HD2ModCli/Program.cs`、`CliApplication.cs`、`HD2ModManager/App.xaml.cs`、`MainWindow.xaml.cs`）+ **30 个 XAML 根**出发做传递闭包；`CoreServices` 89 个工厂按「**被调用才连通**」处理。结果：**664 个 .cs 中 627 可达，63 个类型不可达**。
2. **3 个子代理独立核验**（Adaptation / Manager / Core 各一片，共 36 个类型），排查 XAML 短名、`x:Key`、`d:` 设计时、反射/DI、`.csproj` 注册。
3. **CLI 运行时反查**：对 `capabilities` 声明的 **135 个能力**逐个真实执行。

### 12.2 结论一：CLI 能力表 135/135 全部有路由

无幽灵命令。首轮 7 个 `UnknownCommand` 全是探测方式问题（需位置参数），补参数后全部路由。**意味着不能靠 CLI 表面删代码，该删的都藏在实现里。**

### 12.3 结论二：新增 4 个死代码簇

**簇 1 · Adaptation `Canonical*`（16 类型 / 4 文件）**

`CanonicalDependencyClosure.cs`(7)、`CanonicalMaterialCompatibilityAnalyzer.cs`(4)、`CanonicalUnitMaterialGraphAnalyzer.cs`(4)、`CanonicalMaterialBindingLayout.cs`(1)。引用只来自自身测试 + `_diagnostics/LegacyPatchExperiment`。

⚠️ **易混淆**：`CanonicalDependencyClosureValidation` 是**枚举**（`CanonicalPatchSession.cs:13`），**它是活的**（`PatchWorkspaceSessionComposer` / `PatchWorkspaceWriter` 在用）；死的是**同名 class**。

**簇 2 · `PatchReconstruction/Validation/` 整个目录（7 类型 / 2 文件）**

`PatchValidator`、`IPatchValidator`、`PatchValidationIssue/Options/Result/Severity`、`SourceGeometryValidationContext`。引用只来自 `PatchValidatorTests` + `_diagnostics/PatchValidationTool`。

🚧 **删除拦路虎**：`HD2ModCore.Tests/ModInformationCenterArchitectureTests.cs:57-64` 的 `PatchValidatorStreamsSourceGeometryComparisonPerUnit` **把 `PatchValidator.cs` 当文本读**并断言其内容（必须含 `ValidateSourceGeometryForUnitAsync`、不得含 `Dictionary<AssetKey, PatchUnitMesh>`）。删文件必须连该测试一起删/改。

**簇 3 · HD2ModCore 杂项（8 类型）**

`IModInformationProducer<T>`（**零实现**）、`MurmurHash`（被 `HD2GameItemAssets/StingrayHash.Murmur64A` 取代）、`DirectoryCopy`（唯一引用在 `.vs/CopilotSnapshots` IDE 快照，非编译输入）、`PatchArchiveEditPlacement`、`PatchAssetSourceFileFingerprint`、`PatchGroupAnalysisCacheEntry`、`AdaptationPatchGroupAnalysisProvider`、`GitHubArchiveHashesSync`。这 8 个在 `CoreServices` 里**都无工厂**。

**簇 4 · HD2ModManager（9 类型）**

`BooleanInverseToVisibilityConverter`、`CompactMinHeightConverter`、`CompactWidthConverter`、`StringNotEmptyToVisibilityConverter`、`MarqueeBehavior`、`FloatingActionItem`、`TabViewModel`（文件名 `TabViewmodel.cs`）、`IntegrityService`、`TextEncodingUtil`。

⚠️ `FloatingActionItem` 是诱饵：`FloatingActionCluster` 活的，但绑定的是 `ViewModels/PageActionViewModel`（`Kind` 见 `PageActionViewModel.cs:37`），`FloatingActionItem` 连 `Kind` 属性都没有。

### 12.4 累计死代码

`HD2ModAdaptation` 46 + `HD2ModCore` 8 + `HD2ModManager` 9 = **63 个类型**，占 1587 个生产类型的 **4.0%**。

### 12.5 结构性发现：`_diagnostics/` 是「死代码看起来还活着」的根源

6 个工程（ComponentEditorPreview / LegacyPatchExperiment / MaterialPackagingSmoke / PatchSemanticDiff / PatchValidationTool / UnitSemanticProbe）**都不在 `WpfApp1.sln`**，但**都 ProjectReference 了 HD2ModManager / HD2ModAdaptation**。它们是簇 1、簇 2 类型的**唯一非测试消费者**。→ 纯 grep 会把它们判成活代码。

建议：整体归档，或加纪律「生产代码不得依赖 `_diagnostics` 的任何类型」。

> ✅ **该纪律已落地**：`.github/copilot-instructions.md`「临时诊断工程规则」+ `HD2ModCore.Tests/DiagnosticsIsolationArchitectureTests.cs`（见提交 `20f5014`）。

### 12.6 方法论（最该记住的一条）

**「grep 能搜到引用」≠「代码是活的」。** 本轮 4 个簇**全都有 grep 可见的引用**，但来自三类非生产位置：①测试 ②`_diagnostics/` 临时工程 ③死簇内部互引。

正确口径：**引用是否来自「从生产入口可达」的代码**。

**补充（2026-10-11，阶段 2 期间踩到）：注释里的类名也算「引用」。**
`tools/check_dead_code.py` 是**词匹配**，所以在生产文件的注释里写下某个类名，会把这个类**误标为可达**。
实际经过：在 `ModInformationFactRegistry.cs` 注释里写了 `CanonicalDependencyClosure`，该簇 7 个类型立刻从"不可达"里消失（40 → 33）。
检测手法：**两个方向一起看**——`comm -13`（新增不可达）与 `comm -23`（消失的不可达）。
只盯"不可达总数有没有下降"是发现不了的（总数下降看起来像好事）。

**推论**：引用类名做说明时，要么改用不含标识符的措辞（"canonical 依赖闭包审计"），要么在文档里登记这次漂移。

### 12.7 交付物

- 新增可复用工具 `tools/check_dead_code.py`（整理自本轮脚本，已验证可复现「63 个不可达类型」；清理完后可重跑验证）。

---

## 13. 落地前准备与准入判定

> 回答：「现在是否可以整理代码、开始落地信息中心？还是说有别的得提前准备？」

### 13.0 一句话结论

**可以先做收尾整理，但不能直接进阶段 1。** 当前工作区里有一处半落地、零测试、且是整棵树里**唯一会删除用户数据**的改动；它必须先收口。理由见 §13.2。

### 13.1 工作区真实状态（审计时间 2026-10-10）

- 分支 `master`，HEAD `30a4f83 Fix runtime helmet mapping classification`
- 未提交条目 **17 项** = 7 个已修改 + 10 个未跟踪
- `git diff --stat` = `7 files changed, 204 insertions(+), 2 deletions(-)`
- `.github/workflows/` 存在但**为空**（无 CI）；无 `Directory.Build.props`

已修改的 7 个文件及归属：

| 文件                                                    | 增量     | 归属                 |
| ----------------------------------------------------- | ------ | ------------------ |
| `HD2ModCore/Domain/ImportResult.cs`                   | +6     | A                  |
| `HD2ModCore/Infrastructure/ModLibraryImporter.cs`     | +73    | A                  |
| `HD2ModCore/Infrastructure/ModLibrarySynchronizer.cs` | +10/-2 | A                  |
| `HD2ModCore.Tests/ModLibraryImporterTests.cs`         | +53    | A                  |
| `HD2ModCore.Tests/ModLibrarySynchronizerTests.cs`     | +40    | A                  |
| `HD2ModManager/Services/ImportService.cs`             | +5/-1  | A                  |
| `HD2ModManager/Services/ModLibraryService.cs`         | +19    | A（后半段）+ **B（前半段）** |

未跟踪项中与本议题相关的：

- `HD2ModCore/Infrastructure/ModLibraryResidualCleanupService.cs`（新增，99 行）—— **B**
- `tools/check_dead_code.py`（§10–§12 的支撑工具，应一并提交）

### 13.2 为什么它是拦路虎（按严重度排序）

**（1）它是当前工作区里唯一会删除用户数据的代码，却零测试。**

核心动作是 `Directory.Delete(directory, recursive: true)`。其保护逻辑 `IsSafeResidual` 设计是保守且正确的——含逻辑标记、含可识别外部资源、含任何被 `IPatchFileNameParser` 识别的 patch 文件，一律跳过。但：

- 全仓引用点**只有 2 处**：自身声明 + 唯一调用点；
- `HD2ModCore.Tests/` 下**没有** `ModLibraryResidualCleanupServiceTests`；
- 本次新增的 2 个测试（`ImportFolderAsync_RemapConflictingExportedNodeGuid_WithoutOverwritingExistingNode`、`SynchronizeAsync_DuplicateLogicalMarker_RecoversWithFreshNodeId`）覆盖的是 **A 部分**，与 B 无关。

即：一个会递归删目录的组件，其判定逻辑（元数据白名单、标记不可读时是否仍算残留）**没有任何测试钉住**。

**（2）它违反项目已有的宪法。**

`.github/copilot-instructions.md` 规定页面层不得"直接创建信息 producer/cache/store"。而现在的接线是裸 `new`、无抽象接口、`CoreServices` 无对应工厂：

```csharp
// HD2ModManager/Services/ModLibraryService.cs:499
var residualCleanup = new ModLibraryResidualCleanupService(CoreServices.CreatePatchFileNameParser())
    .Cleanup(synchronizeSnapshot, _paths.ModsDirectory, cancellationToken);
```

这正是 `ModInformationCenterArchitectureTests` 要拦的形态。**带着一个既有违规进重构，等于开局先把护栏掰弯。**

**（3）A 与 B 语义对抗。**

B 在 `SynchronizeAsync` **之前**运行，用 `synchronizeSnapshot`（同步前旧快照）判断"无引用"；而 A 刚刚让更多目录变得**可恢复**（GUID 冲突不再抛异常，改为重分配身份）。一个让目录更容易被认领，另一个在认领前先删掉"看起来没人要"的目录。当前没出事，是因为 `IsSafeResidual` 的保护集与 A 的恢复集大致重合——这是巧合，不是设计。

**（4）它落在重构要改的文件上。**

`ModLibraryService.cs` / `ModLibraryImporter.cs` 正是阶段 1（统一身份）会触碰的地方。先让这块落地并提交，能显著降低后续冲突面。

### 13.3 拆分建议

**A 部分（GUID 冲突恢复）——建议直接提交。**

它修的是一个真实可用性 bug：此前一个残留的 `.hd2modmanager-logical.json` 标记会让 `ModLibrarySynchronizer` 抛 `InvalidOperationException`，**整个库扫描直接失败**。改为重分配新 `ModNodeId` 方向正确，且已有 2 个测试。`ModLibraryService.cs` 后半段"把修复后的身份写回标记"归属也合理——`ModLibrarySynchronizer` 的注释已声明它被当作纯检视操作、故意不写盘，所以写回放编排层是对的。

> 小瑕疵（不阻塞）：`while (nodes.ContainsKey(id)) id = ModNodeId.New();` 理论上是无限循环。Guid 碰撞概率可忽略，但改成有界重试更符合本项目习惯。

**B 部分（残留目录清理）——三选一，不要就这么留在树里：**

| 方案    | 动作                                                                                                          | 适用          |
| ----- | ----------------------------------------------------------------------------------------------------------- | ----------- |
| B1 补完 | 补 `ModLibraryResidualCleanupServiceTests`（覆盖 `IsSafeResidual` 每条分支）+ 抽接口 + 走 `CoreServices` 工厂 + 移到同步**之后** | 确认该功能要上线    |
| B2 撤下 | `git stash` 掉 B 的接线与新增文件，功能另开分支                                                                             | 还不确定要不要     |
| B3 降级 | 改成只**报告**不删除（返回候选列表，用户确认后再生效）                                                                               | 想先观察实际会命中什么 |

倾向 **B1 或 B3**：一个会删用户 mod 目录的功能，"有测试钉住"与"先报告后删除"二者至少要有一个。

### 13.4 建议执行顺序

| 步 | 动作                                                           | 完成判据                                    |
| - | ------------------------------------------------------------ | --------------------------------------- |
| 0 | 拆分并收口未提交改动：A 提交；B 按 §13.3 处理；`tools/check_dead_code.py` 一并提交 | `git status` 干净（有意保留项除外）                |
| 1 | 打全绿基线                                                        | `dotnet test` 四套 = 728 通过，打 tag         |
| 2 | 执行 §12.5 死代码批次 1–2（Manager 9 + Core 8 类型，零风险）                | 每批单独提交，每批后重跑 `HD2ModCore.Tests`（≈3m43s） |
| 3 | 补护栏：加 `Directory.Build.props` + 最小 CI（只跑 `dotnet test`）      | 护栏能在无人工干预下拦住违规                          |
| 4 | 开始阶段 1：统一身份（泛型 `Request(FactKey)` + kind→property 注册表）       | 见 §6 阶段 1 判据                            |

**为什么第 3 步不能省：** 项目没有 CI，`ModInformationCenterArchitectureTests` 是唯一护栏，而它的形态是**读源码文本做字符串断言**（如 `ModInformationCenterArchitectureTests.cs:56-64` 读 `PatchValidator.cs`，断言 `DoesNotContain("Dictionary<AssetKey, PatchUnitMesh>")`）。这种护栏只有"每次都真的执行"才有意义。重构期间靠人记得跑，等于没有。

### 13.5 开工前需要拍板的 5 个决策

以下 5 项均已定位清楚，但属产品/架构取舍，需要你点头：

1. **`GameDataResourceGraph` 的 `Node`/`Edge`/`Graph` 模型**：是"与现有实现同构重复"（删），还是"为依赖图预留的骨架"（留概念、删实现）？
2. **6 个 `_diagnostics/*` 项目**：直接归档，还是保留但加规则"生产代码不得依赖任何 `_diagnostics` 类型"？它们不在 `.sln` 里，却是死代码簇 1、2 的唯一非测试消费者。
3. **4 个脚手架服务**（`GameDataLocator` / `MaterialDependencyValidator` / `ModManifestImporter` / `PatchTocFileCollector`）：留还是删？纯产品决策——§11 已证明其功能已被别的实现覆盖。
4. **`.sln` 成员整理**：`ManagedMain`（已废弃）是否移出？`Decryptor.Tests`（在磁盘上但不在 `.sln`，因此**从未运行过**）是否加回？
   > ⛔ 已过时（2026-11-08）：这一项已落地——`WpfApp1.sln` 里**没有** `ManagedMain`，`Decryptor.Tests` **已在** `.sln`（14 个项目之一）。不必再按"要不要移出/加回"决策。
5. **阶段 1 的开工形态**：是否就按"泛型 `Request(FactKey)` + kind→property 注册表"动手？

### 13.6 准入检查清单

开工阶段 1 之前，以下必须全部为 ✅：

- [ ] 未提交改动已拆分提交，工作区干净
- [ ] B 部分（残留清理）已有测试，或已撤下，或已降级为只报告
- [ ] `HD2ModCore.Tests` 全绿（440）
- [ ] `HD2ModAdaptation.Tests` / `HD2ModCli.Tests` / `Decryptor.Tests` 全绿
- [ ] 基线 tag 已打
- [ ] 死代码批次 1–2 已清理并单独提交
- [ ] CI 已能自动跑测试
- [ ] §13.5 的 5 个决策已有结论

### 13.7 基线实测结果与 CLI 失败根因（2026-10-10）

| 测试套件 | 结果 | 说明 |
|---|---|---|
| `HD2ModCore.Tests` | **455 通过 / 0 失败** | 440 基线 + 本次新增 15（残留清理 12 + 诊断隔离 3） |
| `HD2ModAdaptation.Tests` | **199 通过 / 0 失败** | 删除 10 个源文件 + 5 个测试文件后仍全绿 |
| `Decryptor.Tests` | **5 通过 / 0 失败** | **首次真正被执行**（此前不在 `.sln`，从未运行过） |
| `HD2ModCli.Tests` | **141 通过 / 0 失败** | 修复前为 **26 失败 / 112 通过 / 138** |

#### CLI 26 个失败的真实根因

**不是代码缺陷，是测试不 hermetic（依赖开发者桌面状态）。**

失败的错误码只有一种：退出码 2（`CliUserException`）。其中一个用例暴露了真实错误码：

```
Expected: "OptionPositionOutOfRange"
Actual:   "ManagerProcessRunning"
```

根因在 `HD2ModCli/CliWriteTargetGuard.cs` → `FindRunningManagerProcessIds()`：

```csharp
Process.GetProcessesByName("HD2ModManager")
```

这是**按进程名的机器级检查**，与 CLI 正在操作哪个库无关。只要本机跑着任何 `HD2ModManager.exe`，**所有 Mod 库写入命令一律返回退出码 2**。而 CLI 测试的夹具都建在独立临时目录里，无法绕开这个全局检查 → 26 个写入类用例必然失败；读取类用例（inspect/plan/show/analyze）不受影响 → 112 通过。

**这个守卫是必要的，不能删。** 已核实 `CoreServices.CreateLibraryWriteLeaseProvider()` 的跨进程租约（`.hd2mod-cli-write.lock`）**只被 CLI 使用，桌面端从不获取它**：

| 并发场景 | 由谁保护 |
|---|---|
| CLI ↔ CLI | 跨进程租约 |
| CLI ↔ 桌面端 | **只有这个进程检查** |

桌面端把库快照持有在内存里（`ModLibraryService._snapshot`）并按自己的节奏写回 `library.json`；CLI 若并发写入，桌面端下一次保存会**静默覆盖** CLI 的结果。所以进程检查是唯一屏障。

**结论：守卫正确，测试错误。** 测试断言"库写入应成功"，但结果取决于开发者桌面上是否开着 GUI——这样的套件无法进 CI。

#### 修复方式

在 CLI 侧加一个**单点可替换探测** `CliManagerProcessProbe`（由 `CliManagerProcessGuard` 与 `CliWriteTargetGuard` 共用），测试程序集在 `[ModuleInitializer]` 里把探测置为"未运行"（`CliTestEnvironment`）。于是：

- **生产行为零改动**：默认探测仍是真实的 `Process.GetProcessesByName("HD2ModManager")`。
- **测试变得 hermetic**：结果不再依赖开发者桌面状态。
- **守卫本身仍被显式验证**：新增 `CliManagerProcessGuardTests` 三个用例，分别覆盖"运行中必须拒绝"（断言 `ManagerProcessRunning`）、"未运行应成功"、"进程枚举不可靠必须拒绝"（断言 `ManagerProcessCheckFailed`）。

> **一个容易漏掉的点**：`DeploymentCliTests` 等 **20 个测试文件直接调用** `CliApplication.RunAsync`，绕过 `CliTestConsole`。所以修复必须放在**程序集初始化层**，不能只放在测试助手层——第一版只改了助手，26 个失败只降到 14 个。

#### 验证（受控实验）

用户的管理器进程在验证期间会被关闭，污染结论，因此改用**伪造进程**做受控验证：把 `ping.exe` 复制为 `HD2ModManager.exe` 并运行。

| 条件 | 结果 |
|---|---|
| 伪造管理器运行中 + 修复前代码 | 26 失败 |
| 伪造管理器运行中 + 修复后代码 | **全绿**（Deployment / Export / ProfileAlign 全部通过） |
| 同上，`CliManagerProcessGuardTests` | 3/3 通过（证明守卫仍生效） |

**对 §13.4 第 3 步（补 CI）的影响**：修复后 CI 可以第一天就绿，第 3 步不再需要"先隔离 26 个失败"。

---

## 14. `GameDataResourceGraph` 的 Node/Edge/Graph：作用、替代实现与删除后果

> 回答：「这几个删不删我不太了解，你可以详细描述一下它的作用，以及删了有啥后果。」

### 14.1 这个模型是什么

三个 record，29 行，在 `HD2ModAdaptation/Analysis/GameDataResourceGraphContracts.cs`：

```csharp
public sealed record GameDataResourceNode(AssetKey AssetKey, string ResourceKind, string? ArchiveName, bool IsResolved);
public sealed record GameDataResourceEdge(AssetKey From, AssetKey To, string Relation, bool IsResolved);
public sealed record GameDataResourceGraph(
	IReadOnlyList<GameDataResourceNode> Nodes,
	IReadOnlyList<GameDataResourceEdge> Edges,
	IReadOnlyList<PatchAnalysisIssue> Issues);
```

配一个 140 行的 builder，入口是：

```csharp
BuildAsync(GameDataArchiveIndex archiveIndex, IReadOnlyCollection<AssetKey> rootAssets, CancellationToken)
```

它做的事：从一组根资产出发，**真的去读 payload 字节**，解析出引用，递归展开成一张有向图。

### 14.2 它实际在算什么

以 **Unit 为根**时（`ReadDependencies`），做三件事：

1. **Unit → Material**：用 `UnitMeshReader` 读 unit 的 `toc` + `gpu_resources`，取 `Materials[].MaterialId`。
2. **Unit → Bone**：在 `toc` 偏移 **8** 处 `ReadUInt64LittleEndian` 读一个 bone 引用。
3. **Unit → CompositeUnit**：在 `toc` 偏移 **16** 处再读一个引用。

以 **Material 为根**时：用 `StingrayMaterialReferenceReader.ReadTextureIds(tocData)` 取 texture 引用。

然后对每个引用：查 `archiveIndex.FindArchivesByAsset` 判断是否 resolved，加一条带 `Relation` 标签的边（`BoneReference` / `CompositeReference` / `TextureReference`），再递归 visit。读不出 payload / 载荷非法 / 归档里有歧义副本 → 产出 `PatchAnalysisIssue`。

> **一句话**：它是"给定根资产，顺着 payload 里的二进制引用一路展开依赖图，并标注每个节点是否在归档里找得到"的引擎。**视角是「游戏本体的这批资源」**，而不是"某个 mod 引用了什么"。

### 14.3 这个能力今天由谁提供

**不是它。** 现网跑的是另一条链路：

| 关注点 | 孤儿实现 | 现网实现 |
|---|---|---|
| 归档索引（输入） | `GameDataArchiveIndex` | **同一个** `GameDataArchiveIndex`，由 `GameDataArchiveIndexer` 产出；消费者是 `AdvancedEquipmentIndexService`、`AssetArchiveIndexService`、`GameDataAuthorityArchiveNameResolver` |
| 引用图产物 | `GameDataResourceGraph` | `ReferenceGraphProducer` → `ReferenceGraphFacts`，经 `IPatchGroupAnalysisProvider`（`depth: PatchAnalysisDepth.DependencyGraph`） |
| 装备→资源映射 | `GameItemResourceRelationBuilder` → `GameItemResourceInfo` | `AdvancedEquipmentIndexService`（`IAdvancedEquipmentIndexService`）+ `ModEquipmentSourceFactsReader` + `EquipmentUnitCatalogService` |

**关键点：`GameDataArchiveIndex`（数据）是活的，`GameDataResourceGraph`（包在它外面的那层 Node/Edge/Graph）是死的。** 两者不是竞争关系——孤儿 builder 只是把同一个索引又包了一遍。

引用点普查（排除 `bin`/`obj`）：

- `GameDataResourceGraphBuilder` → 只被 `GameDataResourceGraphBuilderTests` 引用。**零生产调用者。**
- `GameItemResourceRelationBuilder` → 只被 `AdaptationAnalysisBuilder.cs:20`（`new GameItemResourceRelationBuilder()`）和它自己的测试引用。而 `AdaptationAnalysisBuilder` 本身已判定为**零生产调用者**。

### 14.4 删除的具体后果

**会失去什么：**

1. **一份"直接从 Game Data 原始归档展开引用、不依赖 mod 上下文"的独立能力。** 现网 `ReferenceGraphProducer` 的输入是 `ModNode`，回答"这个 **mod** 引用了哪些资源"；`GameDataResourceGraphBuilder` 的输入是 `AssetKey` 集合 + 归档索引，回答"**游戏本体**的这批资源，依赖链长什么样"。前者是 mod 视角，后者是游戏数据视角。**这两个视角不重合。**
2. **一个现成的实现，供将来的"游戏数据依赖图"信息产品复用。** §4 裁决表里 `GameDataMapping` 是**要新增的 5 项事实之一**。

**不会失去什么：**

1. 现网任何功能——护甲页、装备索引、引用图、装饰合并、mod 分析——**一个都不会变**。已由 CLI 实测 + 可达性分析双重确认。
2. `GameDataArchiveIndex` 本身不动，`AdvancedEquipmentIndexService` 等照常工作。

**风险**：唯一实质风险是"将来真要做游戏数据视角的依赖图时，要重写约 140 + 29 行"。这个成本很低（核心逻辑就是三个偏移量 + 一个材质读取器），且届时可以按 §3 的 IIG 规范重新设计，而不是沿用这套与 `ReferenceGraphFacts` 并行的第二套模型。

### 14.5 裁定（已按选项 A 执行）

| 类型 | 处理 | 理由 |
|---|---|---|
| `GameDataResourceGraphBuilder`、`IGameDataResourceGraphBuilder`、`GameDataResourceGraphBuilderTests` | **已删** | 有完整替代（`ReferenceGraphProducer`），属"被更好实现取代、忘了删的重复实现" |
| `GameItemResourceRelationBuilder`、`IGameItemResourceRelationBuilder`、`GameItemResourceInfo`、`GameItemInput`、`ResourceDependencyFact`、`GameItemResourceRelationBuilderTests` | **已删** | 唯一调用者 `AdaptationAnalysisBuilder` 本身是死代码，整簇一起删 |
| `GameDataResourceNode` / `GameDataResourceEdge` / `GameDataResourceGraph` | **已删（选项 A）** | 它们代表的需求（`GameDataMapping`）已记录在 §4 裁决表——**裁决表才是锚点**，留着 record 等于把同一件事记两遍，其中一遍还是死代码；且其形状（`string ResourceKind`、`string Relation` 魔法字符串）与信息中心规范不一致，留着会诱导后来者照抄 |

实际删除范围（**10 个源文件 + 5 个测试文件**，簇闭合，零外部引用）：

```
HD2ModAdaptation/Analysis/
  AdaptationAnalysisBuilder.cs        AdaptationAnalysisContracts.cs
  GameDataResourceGraphBuilder.cs     GameDataResourceGraphContracts.cs
  GameItemResourceContracts.cs        GameItemResourceRelationBuilder.cs
  ReplacementIntegrityAnalyzer.cs     ReplacementIntegrityContracts.cs
  ResourceReuseContracts.cs           ResourceReuseDetector.cs
HD2ModAdaptation.Tests/
  AdaptationAnalysisBuilderTests.cs   GameDataResourceGraphBuilderTests.cs
  GameItemResourceRelationBuilderTests.cs
  ReplacementIntegrityAnalyzerTests.cs ResourceReuseDetectorTests.cs
```

删除后 `HD2ModAdaptation/Analysis/` 只剩 6 个文件：`GameDataArchiveIndexer.cs`、`GameDataIndexContracts.cs`、`PatchAnalysisContracts.cs`、`PatchGroupAnalyzer.cs`、`UnitMeshPartClassifier.cs`、`UnitMeshPartContracts.cs`。删除后 `HD2ModAdaptation.Tests` **199/199 全绿**。

---

## 15. 阶段 1 详述：统一身份

### 15.1 阶段 1 要解决的唯一问题

现在系统里有**四套互不承认的键模型**。它们各自能回答"这个事实是否新鲜"，但**彼此无法对话**：

| 键模型 | 谁在用 | 粒度 | 失效判定 |
|---|---|---|---|
| `ModInformationCacheKey` | L1 `ModInformationReader` | 21 种属性 × 节点 | 有 generation，但只覆盖属性层 |
| `{nodeId}\|{kind}\|{generation}` | L3 `ModInformationCenter` | kind 级 | 中心自己算 |
| `PatchGroupKey` / `ModContentRevision` | L2 分析提供者 | patch group | 分析器版本常量 |
| 磁盘文件指纹 | L4 / 各旁路 | 各写各的 | 各写各的 |

后果：**没有任何一个组件能回答"这个事实是不是陈旧的"这个统一问题。** 每个消费者都要自己判断，于是判断逻辑散落各处。

**阶段 1 只做一件事：让所有事实用同一个键来命名。** 不新增事实、不改页面、不动缓存策略。**只换"名字"。**

### 15.2 具体做什么

#### 15.2.1 引入 `FactKey`

```csharp
// 作用：信息中心内所有事实的唯一标识。一个 FactKey 精确描述"哪一个实体的哪一个方面"。
public readonly record struct FactKey(FactSubject Subject, ModInformationPropertyKind Kind);

public enum FactSubjectKind { Node, GameAsset, GameData, Library }

// 事实的主体。用结构化值而不是字符串拼接。
public readonly record struct FactSubject(FactSubjectKind Kind, string Value)
{
	public static FactSubject Node(ModNodeId id) => new(FactSubjectKind.Node, id.Value.ToString("N"));
	public static FactSubject GameAsset(ulong typeId, ulong fileId) => new(FactSubjectKind.GameAsset, $"{typeId:x16}:{fileId:x16}");
	public static FactSubject GameData() => new(FactSubjectKind.GameData, "global");
	public static FactSubject Library() => new(FactSubjectKind.Library, "global");
}
```

要点：

- **`FactKey` 不是字符串。** 它是结构化值类型，`Equals`/`GetHashCode` 由编译器生成，不会出现分隔符冲突（现在 `{nodeId}|{kind}|{generation}` 用 `|` 拼字符串，节点名含 `|` 就会歧义）。
- **`FactSubject` 覆盖四类主体**：单个 mod 节点、游戏资产（`AssetKey` 语义）、全局游戏数据、全局库状态。这正好对应 §4 裁决表的四类事实来源。
- **磁盘上的字符串键保留。** L3 的 `mod-information/{kind}_{node}_{generation}.json` 命名**不改**——那是持久化格式，改了要迁移。`FactKey` 只用于**内存中的身份与依赖登记**，落盘时投影成现有字符串。**于是阶段 1 的迁移成本为零。**

#### 15.2.2 引入 kind → 描述符注册表

现在 `ModInformationPropertyKind`（**21 个值**，`Identity`…`Thumbnail`）与 `ModInformationCenter` 的 kind 是两套东西，靠人工记忆对应。阶段 1 把它变成一张**显式的表**：

```csharp
public static class ModInformationFactRegistry
{
	private static readonly Dictionary<ModInformationPropertyKind, FactDescriptor> Descriptors = new()
	{
		[ModInformationPropertyKind.PatchEntryPayload] = new(
			SubjectKind: FactSubjectKind.Node,
			CacheScope: ModInformationCacheScope.Operation,        // 与现有 L1 一致：拒绝 Persistent
			Producer: FactProducerKind.PatchEntryPayload),
		[ModInformationPropertyKind.UnitStructure] = new(
			SubjectKind: FactSubjectKind.Node,
			CacheScope: ModInformationCacheScope.Operation,
			Producer: FactProducerKind.UnitStructure),
		[ModInformationPropertyKind.AdvancedUnitAnalysis] = new(
			SubjectKind: FactSubjectKind.Node,
			CacheScope: ModInformationCacheScope.Persistent,
			Producer: FactProducerKind.AdvancedUnitAnalysis),
		[ModInformationPropertyKind.GameDataMapping] = new(
			SubjectKind: FactSubjectKind.GameData,
			CacheScope: ModInformationCacheScope.Persistent,
			Producer: FactProducerKind.GameDataMapping),
		// ... 其余 17 项
	};

	public static FactDescriptor Describe(ModInformationPropertyKind kind) => Descriptors[kind];
	public static bool TryDescribe(ModInformationPropertyKind kind, out FactDescriptor descriptor) => Descriptors.TryGetValue(kind, out descriptor);
}
```

`FactDescriptor` 至少含四项：**主体种类、缓存范围、生产者、依赖的其它 kind 集合**。

这张表一旦存在，立刻带来三个东西：

1. **启动期自检**：新增一个 `ModInformationPropertyKind` 却忘了登记 → 测试直接失败，不再靠人记得。
2. **`Request(FactKey)` 的唯一入口**。注意 `IModInformationReader` 目前只有 **5 个读取方法**（`ReadPatchIndexAsync` / `ReadPatchPayloadAsync` / `ReadUnitAsync` / `ReadUnitSummaryAsync` / `ReadSourceUnitFactsAsync`）外加 3 个生命周期方法，而这 5 个方法承载了 21 种属性。阶段 1 增加一个泛型入口：
   ```csharp
   ValueTask<ModInformationPropertyResult<T>> RequestAsync<T>(FactKey key, ModInformationReadRequest request, CancellationToken ct = default) where T : class;
   ```
   5 个现有方法保留为薄包装（内部转发到 `RequestAsync`），**调用点零改动**。
3. **依赖声明的落点**：`FactDescriptor.DependsOn` 就是阶段 2 的 DAG 边来源。阶段 1 只填"无依赖"或"静态依赖"，动态依赖留给阶段 2。

### 15.3 怎么改（按文件）

| 文件 | 动作 |
|---|---|
| `HD2ModCore/Domain/FactKey.cs` | 新增 `FactKey`、`FactSubject`、`FactSubjectKind` |
| `HD2ModCore/Domain/FactDescriptor.cs` | 新增 `FactDescriptor`、`FactProducerKind` |
| `HD2ModCore/Domain/ModInformationFactRegistry.cs` | 新增 21 项登记表 |
| `HD2ModCore/Application/IModInformationReader.cs` | 增加 `RequestAsync<T>(FactKey, ...)`；5 个现有方法保持签名不变 |
| `HD2ModCore/Infrastructure/ModInformationReader.cs` | 5 个方法体改为转发到 `RequestAsync<T>` |
| `HD2ModCore/Infrastructure/ModInformationCenter.cs` | 内部 key 从字符串改为 `FactKey`，落盘时投影回现有字符串 |
| `HD2ModCore.Tests/ModInformationFactRegistryTests.cs` | 新增：登记完整性、无重复、每个 kind 可解析 |

**不改**：`DerivedStateCoordinator`（L4）、`ModInformationPatchGroupAnalysisProvider`（L2）、所有页面/ViewModel、任何磁盘格式。

### 15.4 完成判据

阶段 1 完成 = 以下**全部**成立：

1. 每个 `ModInformationPropertyKind`（21 个）在 `ModInformationFactRegistry` 里都有登记（测试强制）。
2. `ModInformationReader` 的 5 个读取方法全部经由 `RequestAsync<T>(FactKey)` 实现（可用源码文本断言守住）。
3. L3 `ModInformationCenter` 内部不再有手工拼接 `"{nodeId}|{kind}|{generation}"` 的代码；字符串只出现在落盘/读取路径。
4. **全部测试绿，且没有任何页面/ViewModel 代码改动。** 这是最重要的验收条件——它证明"统一身份"是纯粹内部重构，无行为变化。
5. `tools/check_dead_code.py` 的可达类型数**不下降**（证明没有把活代码误删）。

### 15.5 为什么阶段 1 必须排在最前

后面所有阶段都建立在"事实有唯一名字"之上：

- **阶段 2（显式依赖 DAG）** 需要"边"的两端有稳定标识符。没有 `FactKey`，边只能用字符串拼——就是现在 `|` 拼接的老问题。
- **阶段 3（分层归位）** 需要判断"这个事实属于哪一层"，而分层依据正是 `FactDescriptor.CacheScope`。
- **阶段 4（页面改造成请求式）** 需要页面说"我要 `FactKey` 描述的这个东西"，而不是去调用 5 个具名方法中的某一个。

反过来，阶段 1 **不依赖任何其它阶段**，且**不产生任何行为变化**。这正是它适合当第一步的原因：**风险最低，对后续阶段的解锁价值最高。**

---

## 16. 脚手架簇详解：`GameDataLocator` / `MaterialDependencyValidator` / `ModManifestImporter` / `PatchTocFileCollector`

> 回答：「这四个服务有什么用？删了有什么后果？」
> 一句话结论：**四个都是「接口 + 实现 + `CoreServices` 工厂」齐备、但没有任何生产代码调用工厂的完整外观件；且四项能力在生产里都已有活着的实现。**

### 16.0 判定方法（为什么能确定「没人用」）

`CoreServices` 是本项目**唯一的装配点**——生产代码拿到服务实例的唯一合法方式是调 `CoreServices.CreateXxx(...)`。因此判据是二值的：

| 检查 | 调用点数量 |
|---|---|
| `CoreServices.CreateGameDataLocator(` | **0** |
| `CoreServices.CreateMaterialDependencyValidator(` | **0** |
| `CoreServices.CreateModManifestImporter(` | **0** |
| `CoreServices.CreatePatchTocFileCollector(` | **0** |

四个符号的全部出现位置 = 自身接口 + 自身实现 + `CoreServices` 工厂定义 + 自身测试。**没有第五处。**

### 16.1 `GameDataLocator` — 找游戏数据目录（含 `SteamLibraryFoldersReader`）

**它想做什么**：HD2 的 mod 最终要装进游戏的 `data` 目录，所以得先知道游戏装在哪。它的查找顺序是：① 先看用户手动填的路径（`IGameDataSettings.GameDataDirectoryOverride`）；② 没填就去注册表找 Steam 的安装位置（`HKCU\Software\Valve\Steam\SteamPath` 等三处）；③ 读 Steam 的 `libraryfolders.vdf`，遍历所有 Steam 库目录，逐个拼出 `steamapps\common\Helldivers 2\data`，哪个存在就用哪个。`SteamLibraryFoldersReader` 是专门负责「读那个 vdf 文件」的辅助类。

**为什么它彻底跑不起来**：它依赖的 `IGameDataSettings` 接口，**全仓没有任何生产实现**——只有接口定义、`GameDataLocator` 自己、和测试。也就是说**生产代码里根本造不出一个 `GameDataLocator` 实例**（构造函数要求传一个 settings，没东西可传）。这是一个从未接完线的功能。

**生产实际怎么找目录**：路径由用户设置提供，`GameDataTargetGuard` 负责**校验**这个传入路径是否合法（存在吗、目录名对吗），**不做自动探测**。

**删掉的后果**：失去「自动探测 Steam 库」这一从未生效的能力。用户仍按现有方式手动指定游戏目录。若将来要做自动探测，`SteamLibraryFoldersReader` 的 vdf 解析逻辑可从 `_backup/` 归档取回。

**文件簇**：`Application/IGameDataLocator.cs`、`Application/IGameDataSettings.cs`、`Infrastructure/GameDataLocator.cs`、`Infrastructure/SteamLibraryFoldersReader.cs` + 测试 `GameDataLocatorTests.cs`(2)、`SteamLibraryFoldersReaderTests.cs`(2)

### 16.2 `MaterialDependencyValidator` — 检查材质/贴图是否配齐

**它想做什么**：一个 Unit（模型）会引用若干 material（材质），每个 material 又引用若干 texture（贴图）。做 patch 时如果只打包了 material 却没打包它引用的贴图，游戏里就会出问题（材质丢失、紫黑格）。这个服务逐条检查三件事：① material 条目在不在 patch 里？② 它的贴图引用能不能读出来？③ 读出来的每个贴图 ID 在 patch 里有没有对应条目？三项都过才算「闭合」，否则记下拒绝原因。

**为什么没人用**：`MaterialDependencyValidationResult` 这个返回值，全仓只有「接口 + 实现 + 工厂」三处出现，**没有任何消费方**。

**生产实际怎么做同一件事**：底层「读 material → 贴图 ID」的能力（`StingrayMaterialReferenceReader`）是**活的、被大量使用**的，服务于 `CanonicalDependencyClosure`、`MaterialDependencyResolver`、`PatchGroupAnalyzer`、`ModInformationPatchGroupAnalysisProvider`——这些是生产里真正在跑的闭合性检查路径。`MaterialDependencyValidator` 是用同一把工具、按另一套接口**再实现一遍的同构重复**。

**删掉的后果**：**无。** 生产上真正做材质闭合检查的是上面那四个。

**特别说明**：它**连测试都没有**（无 `MaterialDependencyValidatorTests.cs`）。所以它在**生产和测试里都是死的**——是四个里证据最硬的一个。

**文件簇**：`Application/IMaterialDependencyValidator.cs`、`Domain/MaterialDependencyValidationResult.cs`、`Infrastructure/MaterialDependencyValidator.cs`（无测试）

### 16.3 `ModManifestImporter` — 导入导出的 zip 包

**它想做什么**：管理器有「导出」功能（`ModExporter`），会把一个 mod 及其选项树打包成 zip，里面带一个 `manifest.json`，记录每个节点的可移植名称和备注。这个服务是**导出功能的逆操作**：读 zip → 解压到 `mods/<新GUID>/` → 读 `manifest.json` → 把名称/备注套回节点 → 写入库快照。它还顺手兼容了格式怪异的 manifest（有容错分支）。

**为什么没人用**：`ImportExportZipAsync` 全仓只有「接口 + 实现 + 测试」三处出现。CLI 的 `import` 系列命令走的是另一条链。

**生产实际怎么导入**：
- CLI：`CliImportValidator` + `CoreServices.CreateModImportExecutionService(paths)` → `ImportArchiveAsync` / `ImportFolderAsync`（`CliApplication.cs` 的 `import apply` 分支）
- Manager：`ModLibraryImporter`（`ImportService.cs:67`）
- `manifest.json` 的解析：`StandardModManifest.TryLoad`（社区 manifest）+ `ModLibraryImporter.RestoreExportedNodeIdsAsync`（管理器自己导出的 `Nodes[].Guid` 身份还原）

也就是说：**它想做的事，生产里已由 `ModLibraryImporter` + `StandardModManifest` 做了，而且做得更全**（还管节点身份还原与 GUID 冲突恢复）。

**删掉的后果**：**无。** 但有一个**功能缺口需要你确认**：`ModExporter` 能导出，而「把导出的 zip 一键导回库并原样恢复名称/备注」这条端到端回路在生产里**没有专门入口**——用户目前要通过 CLI 的 `import` 流程完成，`manifest.json` 会被 `StandardModManifest` 读到，但**`ModManifestImporter` 那套「按 manifest 覆盖节点名称」的精细语义未必完全等价**。

→ 这是四个里**唯一**有「可能少了个功能」嫌疑的。若你认为「导出后能原样导回」是重要能力，建议单独确认；若不需要，一并删。

**文件簇**：`Application/IModManifestImporter.cs`、`Infrastructure/ModManifestImporter.cs` + 测试 `ModManifestImporterTests.cs`(2)

### 16.4 `PatchTocFileCollector` — 递归找 patch 文件

**它想做什么**：HD2 的 patch 文件命名是 `16位十六进制ID.patch_序号`（如 `9ba626afa44a3aa3.patch_0`），旁边可能还有同名 `.stream` / `.gpu_resources` 附属文件。这个服务递归扫描一个目录，**只挑出严格符合 `.patch_数字` 的主文件**，把附属文件排除掉，返回一个文件清单（`PatchTocFileSet`）。

**为什么没人用**：`PatchTocFileSet` 全仓只有「接口 + Domain record + 实现」三处出现，**没有任何消费方**。

**生产实际怎么做同一件事**：
- `PatchFileIndexBuilder` + `PatchFileNameParser`（正则 `^(?<hex>[0-9a-fA-F]{16})\.patch_(?<n>\d+)(?<sidecar>(?:\.stream|\.gpu_resources)?)$`）——**活**，被 `CliDeploymentPlanArtifacts.cs:196` 与 `ModFileFactsProducer` 使用
- `PatchStateScanner` —— 也在扫 patch 文件，并额外检查序号断档、孤儿附属文件

**删掉的后果**：**无。** 生产用的是 `PatchFileIndexBuilder` 这条链，而且它比 `PatchTocFileCollector` **更完整**（能解析出 hex、序号、附属类型，而 `PatchTocFileCollector` 只返回文件路径列表）。

**文件簇**：`Application/IPatchTocFileCollector.cs`、`Domain/PatchTocFileSet.cs`、`Infrastructure/PatchTocFileCollector.cs` + 测试 `PatchTocFileCollectorTests.cs`(2)

### 16.5 汇总

| 服务 | 作用 | 生产里谁在做这件事 | 删了丢什么 |
|---|---|---|---|
| `GameDataLocator`（+`SteamLibraryFoldersReader`） | 自动探测 Steam 游戏 data 目录 | 用户设置 + `GameDataTargetGuard` 校验 | 从未生效的自动探测能力 |
| `MaterialDependencyValidator` | 材质→贴图闭合检查 | `CanonicalDependencyClosure` / `MaterialDependencyResolver` / `PatchGroupAnalyzer` / `ModInformationPatchGroupAnalysisProvider` | **无**（连测试都没有） |
| `ModManifestImporter` | 导入导出 zip 并还原名称/备注 | `ModLibraryImporter` + `StandardModManifest`（CLI/Manager 导入链） | 无；**但「导出→一键导回」的精细语义建议单独确认** |
| `PatchTocFileCollector` | 递归收集 `.patch_N` 主文件 | `PatchFileIndexBuilder` + `PatchFileNameParser` / `PatchStateScanner` | **无**（生产能力更完整） |

**共同点**：四个都是「接口 + 实现 + 工厂」齐备的**完整外观**，所以 grep 会显示「有引用」；但工厂的**调用点为 0**，所以生产路径根本不存在。这是 §12.6 那条方法论的教科书案例。

**删除规模**：约 12 个生产文件 + 4 个测试文件 + 4 个 `CoreServices` 工厂方法。

**建议**：`MaterialDependencyValidator`、`PatchTocFileCollector`、`GameDataLocator` 三项证据充分、可安全删除。`ModManifestImporter` 建议先回答「导出后是否需要一键原样导回」再决定。

---

## 17. 阶段 1 实施记录：统一身份（已完成）

> 实施时间：2026-10-10　起点提交：`2571504`（第九轮收尾，工作树干净）
> 状态：**代码已实施并验证通过，尚未提交**。本节只记录事实，不改写 §15 的设计原意。

### 17.1 实际改动（按文件）

**新增**

| 文件 | 内容 |
|---|---|
| `HD2ModCore/Domain/FactKey.cs` | `FactSubjectKind`（Node/GameAsset/GameData/Library）、`FactSubject`（结构化主体 + `Node`/`GameAsset`/`GameData`/`Library`/`LibrarySnapshot` 工厂 + `IsNode`）、`FactKey`（`Subject` + `ModInformationPropertyKind`，含 `ForNode`）、`FactRevisionKey`（`FactKey` + `Revision`） |
| `HD2ModCore/Domain/FactDescriptor.cs` | `FactProducerKind`（11 个生产者）、`FactDescriptor`（主体种类 / 缓存范围 / 生产者 / 静态依赖） |
| `HD2ModCore/Domain/ModInformationFactRegistry.cs` | 21 项登记表 + `Describe` / `TryDescribe` / `IsRegistered` / `Count` / `Validate()` |
| `HD2ModCore/Domain/FactReadOperation.cs` | `FactReadOutcome<T>`（数据 + 体量 + 可选状态/问题）、`FactReadOperation<T>`（修订来源文件 + 实际读取动作 + 键后缀） |
| `HD2ModCore.Tests/ModInformationFactRegistryTests.cs` | 15 个测试，把 §15.4 的判据 1/2/3 固化成断言 |

**修改**

| 文件 | 动作 |
|---|---|
| `HD2ModCore/Application/IModInformationReader.cs` | 新增 `RequestAsync<T>(FactKey, request, FactReadOperation<T>, ct)`；5 个具名方法签名**一字未动** |
| `HD2ModCore/Infrastructure/ModInformationReader.cs` | 5 个具名方法改为**单语句转发**到 `RequestAsync`；真正的读取管道（缓存查询 → 合并 → 落缓存）只保留在 `RequestAsync` 内一处 |
| `HD2ModCore/Infrastructure/ModInformationCenter.cs` | 7 个进行中任务字典由 `ConcurrentDictionary<string, …>` 改为 `ConcurrentDictionary<FactRevisionKey, …>`；失效匹配由字符串 `StartsWith` 改为 `FactSubject` 结构化相等 |
| `HD2ModCore/Domain/ModInformationCacheKey.cs` | 身份部分由 `(NodeId, Property)` 收敛为 `FactKey`；新增 `BelongsTo(ModNodeId)` |
| `HD2ModCore/Infrastructure/InMemoryModInformationOperationCache.cs` | `RemoveNode` 改用 `BelongsTo` |
| `HD2ModCore.Tests/ModInformationReaderContractsTests.cs` | `CreateKey` 辅助方法跟随新签名 |
| `HD2ModCore.Tests/DecorationPackageSnapshotServiceTests.cs`、`ModEquipmentSourceFactsReaderTests.cs` | 两个 `IModInformationReader` 测试替身补上 `RequestAsync` 成员（抛 `NotSupportedException`） |

**未改动（符合 §15.3 的"不改"清单）**：`DerivedStateCoordinator`（L4）、`ModInformationPatchGroupAnalysisProvider`（L2）、**任何页面 / ViewModel**、任何磁盘格式（`mod-information/{kind}_{node}_{generation}.json` 命名原样保留）。

### 17.2 相对 §15.3 的三处偏离及理由

1. **`RequestAsync` 多了一个 `FactReadOperation<T>` 形参。**
   §15.2.2 给的签名是 `RequestAsync<T>(FactKey, request, ct)`。但 5 个具名方法各自需要不同的底层输入（TOC 条目、依赖策略、canonical 开关、索引对象、键后缀），而读取器**不是按 kind 分派的工厂**。把这些塞进 `ModInformationReadRequest` 会污染领域记录，所以用 `FactReadOperation<T>` 承载"身份之外的那部分"。

2. **缓存身份是 `(FactKey, Revision)` 而不是裸 `FactKey`。**
   §0.4 原文即"所有缓存键收敛为 `(FactKey, RevisionKey)`"，§3.3 也把 L3 的 `(kind, nodeId, generation)` 收敛为 `Kind=Product, Scope=Node, Revision=generation`——**修订本来就在身份里**。因此引入 `FactRevisionKey`，而不是把 generation 从键里丢掉。丢掉 generation 会让"同节点不同 generation 的并发请求"错误合并。

3. **`FactSubject` 增加 `LibrarySnapshot(nodeIds)` 工厂。**
   唯一一个非节点主体的事实是 `FileFacts`（库快照级）。旧键把"快照覆盖的节点集合"编进字符串，只用于按节点精确失效。为了既保住这个精确性、又不再手拼字符串，把节点集合放进 `FactSubject.Value`（`FactSubjectKind.Library`），失效改为对 `Subject.Kind == Library && Subject.Value.Contains(nodeToken)` 判断——**语义与旧实现逐字等价**。

### 17.3 与 §15.4 完成判据的逐条对照

| 判据 | 状态 | 证据 |
|---|---|---|
| 1. 21 个 `ModInformationPropertyKind` 全部登记（测试强制） | ✅ | `ModInformationFactRegistryTests.EveryPropertyKindIsRegistered` 断言 `Enum.GetValues` 长度 = 21 = `Count`，且逐个 `IsRegistered`；新增 kind 漏登记即失败 |
| 2. 5 个读取方法全部经由 `RequestAsync<T>(FactKey)` | ✅ | 源码断言：每个方法体内含 `RequestAsync(`、`return RequestAsync(` 恰好 5 处、`TryGetCacheAsync<T>(cacheKey` / `ReadCoalescedAsync(cacheKey` / `CompleteAsync(cacheKey` 各恰好 1 处；另有行为断言 `DirectRequestAndNamedReadShareTheSameCacheEntry` 证明具名方法与统一入口命中**同一条缓存记录** |
| 3. L3 内部不再手拼 `"{nodeId}|{kind}|{generation}"` | ✅ | `InformationCenterNoLongerBuildsStringCacheKeys` 断言不再含 `\|{effectiveRequest.Kind}\|`、`StartsWith($"{nodeId}\|"`、`ConcurrentDictionary<string,`；7 个字典全部改为 `FactRevisionKey` |
| 4. 全部测试绿，且无任何页面 / ViewModel 改动 | ✅ | 见 17.4；`git status` 显示 `HD2ModManager/` 下零改动 |
| 5. `tools/check_dead_code.py` 可达类型数不下降 | ✅ | 不可达类型 **40 → 40（不变）**，可达文件 622 → 626，可达类型 1518 → 1524（**+6**）；且"新增不可达"清单为空——连登记表本身都进了生产可达路径 |

### 17.4 验证结果

- **全解决方案重建：0 错误，13 行 / 11 条唯一警告**——与阶段 1 之前**完全一致**（未新增任何警告）。
- **测试**：核心 459 通过（其中新增 15 条全部通过）。
- **判据 5 的对比方法**：用 `git archive 2571504` 导出阶段 1 之前的代码树到临时目录，在两边分别跑同一个 `tools/check_dead_code.py`，再用 `comm` 比对不可达类型清单。这比"看两个总数"更硬——它能证明**没有任何一个原本可达的类型变不可达**。

### 17.5 顺手做掉的一处改进（把登记表变成生产真相）

原 `ModInformationReader.Validate` 里有一张**硬编码**清单：

```csharp
&& expected is ModInformationPropertyKind.PatchEntryPayload or ModInformationPropertyKind.UnitStructure
```

阶段 1 把它换成读取登记表：

```csharp
&& ModInformationFactRegistry.Describe(expected).CacheScope < ModInformationCacheScope.Persistent
```

对读取器实际会传入的 4 个 kind（`PatchCatalog` / `PatchEntryPayload` / `UnitStructure` / `UnitGeometrySummary`）**判定结果逐字等价**，但从此"新增一个 kind 却忘了更新校验清单"这类错误不可能再发生。附带效果：登记表从"只有测试引用"变成**生产可达**，这正是 §15.2.2 所说"这张表一旦存在，立刻带来三个东西"里的第一条。

### 17.6 决策清单（已裁定）

> 2026-10-11 用户裁定：**D3 逐条读代码补满，其余按助手建议执行**。下面保留原始问题描述并追加裁定结果。

**D1 · 是否提交阶段 1 这批改动。** → ✅ **已提交**（`8db93ec`）。

**D2 · `FactReadOperation<T>` 这个形参是否符合预期。** → ✅ **裁定：保持现状。**
如果希望 `RequestAsync` 严格保持 §15.2.2 的三参数形式，需要把"底层读取动作"搬进 `ModInformationReadRequest` 或让读取器按 `FactDescriptor.Producer` 自行分派——后者是**阶段 3/4 的活**，现在做会把阶段 1 拖大。

**D3 · `FactDescriptor.DependsOn` 补满。** → ✅ **已补满 21 条**，见 §18。
每条边都逐条读代码核对过，依据写在 `ModInformationFactRegistry.cs` 对应条目的注释里。

**D4 · `FactSubjectKind.GameAsset` 目前无任何 kind 使用。** → ✅ **裁定：保留。**
它是 §5.3「廉价细分位」为后续阶段预留的位置（例如把某个 Unit 的几何事实细分到单个游戏资产）。

**D5 · 是否顺带清理 `FactDescriptor` / `FactProducerKind` 的枚举值。** → ✅ **裁定：保留。**
`FactProducerKind` 的 11 个值在阶段 3 的"分层归位"里会被真正消费；现在删掉，阶段 3 还得再加回来。

**D6 · 阶段 2 是否现在开始。** → ✅ **已开始并完成**，见 §19。
（另注：§6 迁移路线表里把"阶段 2"写成"收编 game-data 侧"，而 §15.5 把它写成"显式依赖 DAG"。
本次按 §15.5 的口径执行——即**先做显式依赖 DAG**。§6 的"收编 game-data 侧"会改动浏览器页/护甲页，
属于需要单独拍板的工作，已单列为 §19.6 的待办。）

### 17.7 一句话总结

阶段 1 把"四套互不承认的键模型"里**信息中心自己那两套**（L1 的 `(NodeId, Property, …)` 与 L3 的 `"{nodeId}|{kind}|{generation}"`）合并成了同一个结构化身份 `(FactKey, Revision)`，并让 5 个具名读取方法变成同一个入口的薄包装。**零行为变化、零页面改动、零新增警告、不可达类型数不变。**

---

## 18. D3：21 条依赖边的逐条考证（已完成）

> 目标：把 §17.6 D3 里"只填了 6 条"补满到 21 条，**每条边都必须在代码里坐实**，不允许猜测。
> 方法：从"谁生产这个 kind"出发，读遍该生产者的实现，找出它实际消费了哪些上游 kind 的数据。

### 18.1 先说一个必须交代的事实：21 个 kind 里只有 8 个有真实生产者

设计文档 §15.2.2 说"5 个读取方法承载 21 种属性"，这句话在**枚举定义**上成立，但在**生产路径**上不成立。逐条读代码后的实际分布是：

| 分类 | kind | 说明 |
|---|---|---|
| **L1 读取器真实生产（4）** | `PatchCatalog`、`PatchEntryPayload`、`UnitStructure`、`UnitGeometrySummary` | `ModInformationReader` 的 5 个具名方法里，`ReadUnitSummaryAsync` 与 `ReadSourceUnitFactsAsync` 用的是**同一个** `UnitGeometrySummary` |
| **L3 中心生产者真实生产（7，含 2 处重名）** | `PatchCatalog`(FileFacts)、`AssetInventory`、`ReferenceClosure`、`Compatibility`、`UnitStructure`(AdvancedUnitAnalysis)、`Maintenance`、`Thumbnail` | `ModInformationRequest.EffectiveProperty` 把 `ModInformationKind` 映射到这些 kind；`PatchCatalog` 与 `UnitStructure` 各被两个不同粒度的产品复用 |
| **声明了但没有任何生产者（12）** | `Identity`、`ContentRevision`、`UnitCatalog`、`UnitPartMapping`、`MaterialDependencyClosure`、`CompositeBoneDependencyClosure`、`ReferenceEdges`、`GpuPayload`、`StreamPayload`、`TocPayload`、`GameDataMapping`、`DecorationDefinition` | 其中 `UnitPartMapping` 目前只当**请求判别位**用（`ModEquipmentSourceFactsReader` 用它决定"只取能被装备目录标注的 Unit"），`Gpu/Stream/TocPayload` 是同一个 payload 的三个字节切片，`GameDataMapping` 由中心之外的旁路服务生产 |

> **这个事实本身就是一条重要情报**：后面阶段 3/4 若要"按 kind 分派生产者"，必须先承认——有一半 kind 今天还没有生产者。
> 本节的依赖边因此分两类依据：**代码坐实**（生产路径里看得见）与**结构性定义**（是另一个 kind 的切片/闭包/子集，语义上不可能独立存在）。

### 18.2 21 条边全表

| # | kind | 主体 | 范围 | 生产者 | **DependsOn** | 依据 |
|---|---|---|---|---|---|---|
| 1 | `Identity` | Node | Persistent | Source | — | 节点身份来自 `library.json`（外部输入）。**源事实** |
| 2 | `ContentRevision` | Node | Persistent | Source | — | 内容指纹由文件 mtime/length 直接算出（`ComputeFileRevision`）。**源事实** |
| 3 | `PatchCatalog` | Node | Persistent | PatchCatalogReader | — | 直读 Mod 目录的 patch TOC 文件（`PatchWorkspaceReader.ReadIndexAsync`）。**源事实** |
| 4 | `PatchEntryPayload` | Node | Operation | PatchEntryPayloadReader | `PatchCatalog` | 代码坐实：`ReadPatchPayloadAsync(entry, …)` 必须由"已读到的 TOC 条目"驱动 |
| 5 | `AssetInventory` | Node | Persistent | AssetInventoryService | `PatchCatalog` | 代码坐实：`ModContentFactsService` → `AnalyzeNodeAsync` → `ReadPatchIndexAsync`；资产条目就是 TOC 条目的投影 |
| 6 | `UnitCatalog` | Node | Persistent | AssetInventoryService | `AssetInventory` | 结构性：Unit 目录是 `ModAssetStructureAssetFact.IsUnit` 资产的子集 |
| 7 | `UnitPartMapping` | Node | Persistent | Composite | `AssetInventory`, `UnitGeometrySummary` | 代码坐实：`ModEquipmentSourceFactsReader` 组合 `RequestAssetInventoryAsync` + `ReadSourceUnitFactsAsync` + 装备目录（后者是 §4 #14 的廉价直读，不在本表内） |
| 8 | `UnitGeometrySummary` | Node | Persistent | UnitMeshReader | `UnitStructure` | 结构性：`ModUnitStructureSummary.Create(unit)` 的入参就是解码后的 Unit |
| 9 | `UnitStructure` | Node | Operation | UnitMeshReader | `PatchCatalog`, `PatchEntryPayload` | 代码坐实：`ReadUnitAsync` 经 `ReadEntriesThroughIndexAsync` 取条目 + 解码时读 payload |
| 10 | `MaterialDependencyClosure` | Node | Persistent | Composite | `ReferenceEdges` | 结构性：闭包是"材质→贴图"边集上的传递闭包 |
| 11 | `CompositeBoneDependencyClosure` | Node | Persistent | Composite | `ReferenceEdges` | 结构性：同上，沿"Unit→骨骼/Composite"展开 |
| 12 | `ReferenceEdges` | Node | Persistent | ReferenceGraphService | `PatchCatalog`, `PatchEntryPayload` | 代码坐实：`ReferenceGraphProducer`(DependencyGraph) 读索引 + `ReadMaterialTextureReferencesAsync` 读材质 payload 里的 TextureId |
| 13 | `ReferenceClosure` | Node | Persistent | ReferenceGraphService | `ReferenceEdges` | 结构性：`ModInformationKind.ReferenceGraph` 的 `EffectiveProperty` 就是本 kind，落盘内容即边集 |
| 14 | `GpuPayload` | Node | Operation | PatchEntryPayloadReader | `PatchEntryPayload` | 结构性：`ToCorePayload` 把 TocData/StreamData/GpuResourceData 一起搬过来 |
| 15 | `StreamPayload` | Node | Operation | PatchEntryPayloadReader | `PatchEntryPayload` | 同上 |
| 16 | `TocPayload` | Node | Operation | PatchEntryPayloadReader | `PatchEntryPayload` | 同上 |
| 17 | `GameDataMapping` | **GameData** | Persistent | GameDataMappingService | — | 上游是游戏数据索引 + metadata 目录（都是 Source，且不在本表内）。**源事实** |
| 18 | `Compatibility` | Node | Persistent | UnitVersionService | `AssetInventory` | 代码坐实：`UnitVersionInformationProducer` 在 `AnalyzeNodeAsync` 的产物上做版本探测（`ProbeAsync(analysis)`）；§4 #4 亦定为依赖 #2 |
| 19 | `DecorationDefinition` | Node | Persistent | Composite | `AssetInventory` | 代码坐实：`DecorationPackageSnapshotService` 先用 `ReadPatchIndexAsync` 定位来源 Patch，再按选中的 AssetKey 过滤 |
| 20 | `Maintenance` | Node | Persistent | MaintenanceAnalysisService | `AssetInventory` | **代码坐实（本表唯一"中心内部真按依赖顺序取上游"的边）**：`ProduceMaintenanceAndRemoveAsync` 先 `await RequestAssetInventoryAsync(...)`，再把 `inventory.Data` 传给生产者 |
| 21 | `Thumbnail` | Node | Persistent | ThumbnailService | — | 只读 Mod 目录里约定命名的图标文件（`ModIconLocator.TryResolve`）。**源事实** |

**源事实（SourceRoots，5 个）**：`Identity`、`ContentRevision`、`PatchCatalog`、`GameDataMapping`、`Thumbnail`。

### 18.3 为什么"源事实"要单独登记，而不是靠"依赖为空"表示

如果只靠"`DependsOn` 为空"来表达源事实，那么**新增一个 kind 却忘了写依赖**和**它确实没有上游**就无法区分——两者都是空集合。
现在 `Validate()` 强制"空依赖 ⟺ 在 `SourceRoots` 里"，于是：

- 新增 kind 忘了写依赖 → 启动期/测试期直接抛错，消息会告诉你"若它确实直接来自外部输入，请把它加入 SourceRoots"；
- 想把一个"其实有上游"的 kind 偷偷标成源事实 → 必须显式改 `SourceRoots`，是一个有意识的动作，评审时看得见。

### 18.4 一条方法论副产品：注释里的类名会污染死代码统计

`tools/check_dead_code.py` 按**词匹配**判断引用。写 §18.2 表时我在 `ModInformationFactRegistry.cs` 的注释里写了 `CanonicalDependencyClosure` 这个类名，
结果该工具立刻把这 7 个类型从"不可达"里划掉（不可达 40 → 33），而它们其实**仍然不可达**。

发现方式：`comm -13`（新增不可达为空）+ `comm -23`（消失的 7 个全在同一个文件）——两个方向一起看，异常立刻暴露。
处理：把注释改成"canonical 依赖闭包审计"这种不含标识符的措辞，不可达数回到 40。

> **教训**：引用类名做注释说明时，要么用不含标识符的措辞，要么接受统计漂移并在此登记。
> 这条要写进 §12.6 的方法论里。

---

## 19. 阶段 2 实施记录：显式依赖 DAG（已完成）

> 实施时间：2026-10-11　起点提交：`8db93ec`（阶段 1 收尾）
> 状态：**代码已实施并验证通过，尚未提交**。

### 19.1 阶段 2 的口径澄清

设计文档里"阶段 2"有两个互相冲突的定义：

- §6 迁移路线表：**收编 game-data 侧**（把 #8/#9/#10 登记为 `Scope=GameData` 的持久事实，让浏览器页/护甲页不再直调 `MapAsync`/`GetArchiveSummariesAsync`）；
- §15.5 与 §9：**显式依赖 DAG**（"要做的三件事：统一身份 + 显式依赖 + 分层归位"）。

**本次按 §15.5 的口径执行**，理由：D3 刚刚把 21 条边逐条坐实，DAG 是它的直接消费者；而 §6 的 game-data 收编会**改动页面代码**，
验收口径也随之变化（不再满足"零页面改动"），应当单独拍板。§6 那一版已单列为 §19.6 的待办。

### 19.2 实际改动（按文件）

**新增**

| 文件 | 内容 |
|---|---|
| `HD2ModCore/Domain/ModInformationFactGraph.cs` | 由登记表物化的有向图：`Nodes` / `DirectDependencies` / `TransitiveDependencies` / `TopologicalOrder` / `IsAcyclic`；`Build(descriptors)` 可对任意描述符字典构图（测试可造环验证） |

**修改**

| 文件 | 动作 |
|---|---|
| `HD2ModCore/Domain/ModInformationFactRegistry.cs` | 21 条 `DependsOn` 全部补满，每条附"依据"注释；新增 `SourceRoots`（5 个源事实）与 `Graph`；`Validate()` 从 2 项检查扩到 5 项 |
| `HD2ModCore/Infrastructure/CoreServices.cs` | `CreateModInformationCenter` 开头调用 `ModInformationFactRegistry.Validate()`——把"启动期自检"从文档承诺变成实际代码 |
| `HD2ModCore.Tests/ModInformationFactRegistryTests.cs` | 新增 23 条测试（15 条 `Theory` 锁边 + 8 条图/一致性事实） |

**`Validate()` 的 5 项检查**：

1. 每个 `ModInformationPropertyKind` 都登记了；
2. 条目数与枚举取值数一致（防重复登记）；
3. 每条依赖边指向已登记的 kind，且没有自环；
4. **依赖图无环**（有环意味着"要生产 A 必须先生产 A"）；
5. **空依赖 ⟺ 在 `SourceRoots` 里**（见 §18.3）。

### 19.3 为什么把 `Validate()` 接进组合根

阶段 1 时 `Validate()` 只被测试调用。这有两个问题：

- 文档 §15.2.2 明确承诺"**启动期自检**"，只被测试调用等于没兑现；
- 更实际的是——它会让这张表变成**只有测试引用的脚手架**，正是 §16 里被清理掉的那类东西。

接进 `CoreServices.CreateModInformationCenter` 之后：登记表与依赖图都进入了**生产可达路径**，且自检真的会在应用启动时跑一次。
检查本身是纯内存操作（21 个节点），开销可忽略；只有出现"编程错误"时才会抛错，而那种情况测试本来也会失败。

### 19.4 完成判据（对照 §7 护栏测试第 2 条）

| 判据 | 状态 | 证据 |
|---|---|---|
| 每个 kind 都声明了依赖，或显式登记为源事实 | ✅ | `EveryKindEitherDeclaresDependenciesOrIsAReviewedSourceRoot`（21/21 通过） |
| 依赖边全部指向已登记的 kind、无自环 | ✅ | `Validate()` 检查 3 + `DeclaredDependenciesOnlyReferenceRegisteredKinds` |
| 依赖图无环 | ✅ | `DependencyGraphIsAcyclic` |
| 拓扑序中上游排在下游之前 | ✅ | `TopologicalOrderPlacesEveryDependencyBeforeItsDependent` |
| 传递闭包覆盖全部间接上游 | ✅ | `TransitiveClosureCoversEveryIndirectUpstream` |
| 环检测本身可靠（不是"静默少节点"） | ✅ | `GraphReportsCyclesInsteadOfSilentlyDroppingNodes`（人造环，断言 `IsAcyclic == false` 且节点数不丢） |
| 15 条坐实的边被逐条锁住 | ✅ | `DeclaredDependencyEdgeExists`（`Theory`，15 个 `InlineData`） |
| `Maintenance → AssetInventory` 这条"真取上游"的边不回退 | ✅ | `MaintenanceIsTheEdgeTheCenterActuallyRequestsThroughTheCenter`（源码文本断言） |

### 19.5 验证结果

- **全解决方案重建：0 错误、13 行 / 11 条唯一警告**——与阶段 1 之后**完全一致**，未新增任何警告。
- **全量测试：830 通过 / 0 失败**（Decryptor 5、Adaptation 187、**Core 497**、Cli 141）；Core 从 474 增至 497，正好是新增的 23 条。
- **判据 5（死代码可达性）**：仍用 `git archive` 导出基线树逐条比对。

  | 时间点 | .cs 文件 | 声明类型 | 可达文件 | 不可达类型 |
  |---|---|---|---|---|
  | `2571504`（阶段 1 之前） | 649 | 1558 | 622 | 40 |
  | `8db93ec`（阶段 1 之后） | 653 | 1564 | 626 | 40 |
  | 阶段 2 之后 | 654 | 1565 | 627 | **40** |

  "新增不可达"与"消失的不可达"**两个方向都为空**——即没有任何一个原本可达的类型变不可达，也没有靠注释误标把死代码洗白。
- **`HD2ModManager/` 下 git 零改动** → "无任何页面/ViewModel 改动"成立。
- 磁盘格式、缓存策略、`RequestAsync` 签名**全部未动**。

### 19.6 阶段 2 之后剩下的两件事（需要拍板）

**E1 · §6 那一版"收编 game-data 侧"要不要做。**
它会把 #8/#9/#10 登记为 `Scope=GameData` 的持久事实，并让 `GameDataArchiveBrowserService.BuildAsync` 退化为纯投影。
**这会改动浏览器页与护甲页**，验收口径从"零页面改动"变成"页面不再直调 `MapAsync`/`GetArchiveSummariesAsync`"。

它还必须回答一个结构问题：`GameDataIndex` 与 `AssetMetadataCatalog` 这两个新事实**目前不在 21 个 kind 里**。
是扩枚举（会连带改 `Validate()` 的计数断言、`SourceRoots`、以及所有按 21 写的测试），还是挂在 `FactSubjectKind.GameData` 下作为新 kind？**这需要拍板。**

**实测出来的工作清单（2026-10-11，供 E1 排期用）**：

| 位置 | 现状 | 说明 |
|---|---|---|
| `HD2ModManager` | **零直调**，且完全不引用 `IDeployedOverrideGraphService` / `IGameDataMappingFactsService` | 已被 §19.8 的护栏测试锁死。Manager 是通过 `GameDataArchiveBrowserService` 这个**投影**间接消费的——这恰好是 §6 想要的形态 |
| `HD2ModCore` 业务服务 | **7 个文件共 10 处直调** | 才是真正要收编的对象 |

7 个文件分别是：

1. `GameDataArchiveBrowserService.cs` —— 三个服务全都直调（`GetArchiveSummariesAsync` + `MapAsync` + `_deployedGraphService.BuildAsync`），是**唯一的汇合点**，也是 E1 的主战场；
2. `ProfileOverrideGraphService.cs` —— `MapAsync`
3. `ProfileMaterialDiagnosticsService.cs` —— `MapAsync` ×2
4. `ModAssetRoleFactsService.cs` —— `MapAsync`
5. `AdvancedModAssetQueryService.cs` —— `MapAsync`
6. `MaterialDeliveryFactsService.cs` —— `MapAsync`
7. `ModAssetSummaryProjector.cs` —— `MapAsync`

> 注意第 2–7 项全是 `MapAsync`：它们都直接问 `IGameDataMappingFactsService` 要映射，而**该服务今天没有任何缓存**（§4 #10 标注"旁路无缓存"）。
> 所以 E1 的收益不只是"少一次直调"，而是**给这 6 个消费者同时接上缓存**。

**E2 · 阶段 3（分层归位）是否现在开始。**
它要消费 `FactDescriptor.CacheScope` 与 `FactProducerKind`（D5 保留的 11 个值），把四类节点（Source / PersistentFact / DerivedMemo / Operation）真正落到代码里。
DAG 已经就绪，依赖关系不再是障碍。

### 19.7 一句话总结

阶段 2 把 D3 坐实的 21 条依赖边物化成一张**可校验的 DAG**：无环、拓扑序正确、传递闭包完整，
并让"新增 kind 忘了声明依赖"这件事在**应用启动时**就失败，而不是等到某天返回陈旧数据才发现。
**零行为变化、零页面改动、零新增警告、不可达类型数不变。**

### 19.8 顺手补上的护栏：§7 护栏测试 1 的 Manager 半边

§7 的护栏测试 1 原本要求"`HD2ModManager` 与业务服务不得直接调用三个 game-data 旁路服务"。
它今天**整体加不上去**（业务服务那半边还在直调，见 §19.6 E1），但 **Manager 那半边已经成立**，于是先把能守的守住。

**新增用例** `ManagerDoesNotConsumeTheGameDataBypassServicesDirectly`（`ModInformationCenterArchitectureTests.cs`）：

- 判据 1：Manager 的生产源码里不得出现 `GetArchiveSummariesAsync`。这个成员名**全仓唯一**（只属于 `IAssetArchiveIndexService`），所以是精确判据，不会误伤同名方法。
- 判据 2：Manager 不得引用 `IDeployedOverrideGraphService` 或 `IGameDataMappingFactsService`。这两个服务没有足够独特的成员名可依赖，因此改用**类型引用**判定——Manager 连类型都不该看见。

**为什么这条护栏值得现在就加**：Manager 之所以现在"干净"，是因为它恰好走的是 `GameDataArchiveBrowserService` 这个投影。
但护甲页/浏览器页的**首开性能问题**（§13.2）很容易让人"顺手直调一下省一次投影"——那就是在中心外面又贴一张膏药。
有了这条用例，那种改动会在 CI 上直接失败。

**同批顺手做的**：把 `EnumerateProductionSources`（枚举项目下排除 `bin`/`obj` 的 .cs）抽成私有辅助方法，
现有用例的 `sourceFiles` 局部变量后续可复用它。

### 19.9 阶段 2 的测试增量

| 测试类 | 阶段 2 之前 | 之后 | 增量 |
|---|---|---|---|
| `ModInformationFactRegistryTests` | 15 | 38 | +23（15 条锁边的 `Theory` + 8 条图/一致性事实） |
| `ModInformationCenterArchitectureTests` | 7 | 8 | +1（§19.8 的 Manager 护栏） |
| **全量** | **807** | **831** | +24 |

## 20. 阶段 2 之后的第一批拍板执行记录（E1 + 清理，已完成）

> 实施时间：2026-10-11
> 状态：**代码已实施、已验证、已本地提交**（`d49f56b` / `4ae4f4e` / `842d497` / `312d6d6` / `7290fb9`）。
> **未推送远端**——按用户要求，推送由用户手动执行。

### 20.1 本轮拍板的三件事

| # | 拍板内容 | 结果 |
|---|---|---|
| 1 | §19.6 的 **E1**：把 game-data 侧收进登记表 | 做。枚举 21 → **23**，并补上缺失的缓存（§20.2 / §20.3） |
| 2 | §12 死代码簇 + 5 个零调用工厂 + 11 条警告 | 全删/全清，不可达类型 **40 → 0**（§20.4 / §20.5） |
| 3 | 远端推送 | 从此只做**本地提交**，推送由用户手动执行 |

用户在批准 E1 时附加了一条**硬约束**，它决定了 §20.3 的全部设计：

> "可以收缩 gamedata 侧，但要注意重建功能需要能完整从资产文件、加密的注册表文件中重新获取数据，避免有了缓存却丢失了真实性。"

### 20.2 E1：把 game-data 侧收进登记表（21 → 23）

**提交 `d49f56b`（4 文件，+73/−14）。**

`GameDataMapping` 原本被登记为"源事实、无上游"，但它其实是由两个输入拼出来的：
`FindAssetArchivesAsync`（来自 SQLite 资产索引）与 `AssetMetadataCatalogProvider.LoadAsync`（社区元数据）。
两者都被**表外直读**——这正是本设计要消灭的"旁路"形态。

| 动作 | 内容 |
|---|---|
| 新增两个 kind | `GameDataIndex`（生产者 `GameDataIndexService`）、`AssetMetadata`（生产者 `AssetMetadataCatalogReader`） |
| 登记为源事实 | 装好的游戏文件与那三个元数据文件**本身就是真相来源**，确实没有表内上游，因此进 `SourceRoots` |
| 连边 | `GameDataMapping.DependsOn = [GameDataIndex, AssetMetadata]`，即 §4 的"#10 依赖 #8 + #9"从散文变成表内事实 |
| 枚举追加在末尾 | 既有取值的数值保持不变——这些值会随内存诊断对象出行，重编号会让两个版本的日志对不上 |

于是 §19 的 5 条 `Validate()` 检查**全部继续成立**，包括最容易漏的判据 5（空依赖 ⟺ 在 `SourceRoots` 里）。

### 20.3 E1 的硬约束："缓存不得取代真实性"

**提交 `4ae4f4e`（4 文件，+373/−3）。**

先讲清楚为什么**必须有**这一步，否则 E1 只是把直调换了个名字：
`CreateGameDataMappingFactsService(paths)` **每次调用都 new 一个实例**，所以 6 个消费者各持一份，
实例级缓存等于零共享。而且该服务**今天完全没有缓存**——实测 `FindAssetArchivesAsync`
对每个请求的 asset key 各发一条 SQL。

新增 `HD2ModCore/Infrastructure/GameDataFactCache.cs`：进程级、有界（`Capacity = 16`）、
`Dictionary<string, object>` + 锁。**键完全由来源指纹构成**：

| 事实 | 键的组成 |
|---|---|
| `AssetMetadata` | 三个元数据文件各自的 `(path, length, mtime)`——该目录与 selector 无关，一次解析服务所有消费者；3 次 `stat` 取代重新解析两个大文本文件 |
| `GameDataMapping` | data 目录 + 索引 generation + 元数据 generation + selector 指纹——于是 per-key 的 SQL 循环按 `(索引, 元数据, selector)` 三元组跑一次，而不是每次调用跑一遍 |

**"缓存不取代真实性"是结构性保证，不是承诺**——四条，每条都有对应机制或测试：

1. **键即指纹**：来源一变键就变，旧值**不可达**。"游戏更新了却还返回旧数据"在结构上不可能发生。
2. **不落盘**：它只是记忆。任何时刻丢光它，代价是时间，不是数据。
3. **重建路径不读缓存**：`AssetArchiveIndexService.BuildOrRebuildAsync` 永远直接从游戏目录重扫。
   测试 `RebuildNeverConsumesTheFactCache` 对 `BuildOrRebuildAsync` 的**源码文本**断言：
   全文不得出现缓存类型名，且必须仍然调用 `_archiveIndexer.BuildAsync(` 与 `ComputeSourceFingerprintAsync(`。
4. **双向覆盖**：`GameDataFactCacheTests` 的 8 条用例同时验证两个方向——
   同一来源 → 只算一次（`Assert.Same` 跨两个不同实例）；来源被改 / 被删 / selector 换了 → **真实重算**，且结果反映新内容。

第 3 条是关键：**"重建能完整从资产文件与加密注册表重新取数"这件事是被测试钉住的，不是靠注释声明的。**
缓存只是旁路的加速器，删掉它一切照旧。

> `Clear()` 写出来后又**主动删掉**了：没有调用点，留着会多一个"只有测试引用"的成员，
> 反而可能引入 IDE0051 一类的新警告，并让"可达类型"这条判据变脏。有界性靠 `Set` 里
> `Entries.Count >= Capacity` 时整体清空实现。

### 20.4 死代码清理：40 → 0

**提交 `842d497`（34 文件，+16/−1829）。**

不可达类型从 **40 降到 0**，且 `check_dead_code.py` 的**两个方向都为空**——
既没有"原本可达的类型变不可达"，也没有靠注释里的类名把死代码洗白。

| 类别 | 数量 | 举例 |
|---|---|---|
| `HD2ModCore` | 8 | `IModInformationProducer`、`PatchGroupAnalysisSnapshot`（内含死掉的 `PatchGroupAnalysisCacheEntry`）、`DirectoryCopy`、`GitHubArchiveHashesSync`、`MurmurHash` |
| `HD2ModAdaptation` | 8 | `CanonicalDependencyClosure`、`CanonicalMaterialCompatibilityAnalyzer`、`Validation/PatchValidator` 等 |
| `HD2ModManager` | 9 | 4 个从未被 `x:Static` 引用的 Converter、`MarqueeBehavior`、`TabViewmodel`、`IntegrityService`、`TextEncodingUtil` |
| 零调用工厂 | 5 | `CreatePatchTocScanner` / `CreateGameDataAuthorityProvider` / `CreatePatchGroupAnalysisProvider` / `CreateDependencyGraphAnalysisProvider` / `CreateFullPatchAnalysisProvider` |

**5 个工厂的判据**（沿用 `CoreServices` 那条纪律）：接口 + 实现 + 工厂三者齐备，
但 `CoreServices.CreateXxx(` 的调用点为 **0** → 生产路径不存在。

**测试不是被删掉的，是被改造的**（这一条很重要，否则等于把判据一起删了）：

- `MaterialDeliveryFactsServiceTests` 的辅助方法改为返回元组，不再包一层已删除的 `PatchGroupAnalysisCacheEntry`；
- xUnit2031 / xUnit2029 的位置改用谓词重载（`Assert.Single(collection, predicate)`、`Assert.DoesNotContain`），
  而不是在 `Assert.Single/Empty` 外面套 `Where(...)`；
- 两个 CS0067（`FakeInformationCenter` / `AdvancedModAnalysisServiceTests` 里声明但从不触发的事件）
  用**显式接口实现 + 空访问器**消除，而不是 `#pragma` 或 `= null!`。

**唯一需要读懂才能删的一处**：`PageViewModels` 里 `_switchAction` / `_renameAction` 两个字段从未被赋值，
所以由它们守着的两对 `?.SyncFromPage();` **永远跑不到**。真正的切换/重命名路径是
`BottomBarCoordinator.BeginSwitchProfile()` / `BeginRenameProfile()`，浮动操作按钮走的就是这条。
而 `ProfileSwitchActionViewModel` / `ProfileRenameActionViewModel` **类必须保留**——
`FloatingActionCluster.xaml` 的 DataTemplate 仍在绑定它们。**只删字段，不删类。**

### 20.5 警告清零与 `TreatWarningsAsErrors`

**提交 `7290fb9`（`Directory.Build.props`）。**

那 11 条唯一警告（`HD2ModCore.Tests` 9 条：4× CS0067、4× xUnit2031、1× xUnit2029；
`HD2ModManager` 2 条：CS0649）在 §20.4 里被**真正清除**，而不是压制。
于是打开 `TreatWarningsAsErrors` 时，构建状态**一条都没变**——这正是它的前提条件。

**它的作用不是清掉这 11 条，而是让第 12 条进不来。** 注意范围：
它同时把 NuGet 审计警告（NU1901–NU1904）升级为错误，包括漏洞警告 NU1903——这是刻意的，
见 §20.6。若将来某个公告变得碍事，正确的出口是**加一条带公告链接注释的定点
`WarningsNotAsErrors`**，而不是把这个开关关回去。

### 20.6 顺带解决的一个真实漏洞：内嵌 SQLite 引擎过旧

打开 `TreatWarningsAsErrors` 的**当场**，构建就红了：

```
error NU1903: 包 "SQLitePCLRaw.lib.e_sqlite3" 2.1.6 具有已知的 高 严重性漏洞
              https://github.com/advisories/GHSA-2m69-gcr7-jv3q
```

这条警告此前一直存在，只是在警告列表里被忽略——**这就是 §20.5 要打开那个开关的理由。**

**提交 `312d6d6`。** 用 `sqlite3_libversion()` 直接对真实 DLL 探测，而不是只看元数据：

| 包版本 | 内嵌 SQLite | 说明 |
|---|---|---|
| **2.1.6** | **3.41.2** | 改动前构建实际产出的版本 |
| 2.1.11 | 3.49.1 | 公告列为受影响的最大版本 |
| **2.1.12** | **3.53.3** | **本次采用** |
| 2.1.13 | 3.53.3 | 原生二进制与 2.1.12 **逐字节相同** |

公告（CVE-2025-6965）要求 SQLite ≥ 3.50.2。选 **2.1.12** 而不是 2.1.13 的理由：
2.1.12 是上游**针对这条公告**发布的维护版（发布说明明写它存在的目的就是把 2.1.11 线迁到修好的原生库上）；
2.1.13 带着同一份原生二进制，换不来任何引擎版本收益，只多出没有发布说明的托管改动。

**只换原生引擎**：`Microsoft.Data.Sqlite` 仍是 8.0.13，整条 SQLitePCLRaw 线（core / provider / lib / bundle）
在 Core、Cli、Manager 三处**一致解析到 2.1.12**，归档索引的读写路径没有任何行为变化。

> 这条与 §20.3 的约束不冲突：缓存设计没有动，改的只是索引数据库底下的那个 SQLite 引擎版本。

### 20.7 验证结果

| 判据 | 结果 |
|---|---|
| 全解决方案重建（警告即错误） | **0 警告 / 0 错误** |
| 全量测试 | **833 通过 / 0 失败** |
| 不可达类型 | **40 → 0**，且"新增不可达 / 消失的不可达"两个方向皆空 |
| 依赖图 | 仍无环，`Validate()` 5 条检查全过（23 个 kind） |
| `HD2ModManager` 页面代码 | 除 §20.4 那 2 个死字段外**零改动** |
| 内嵌 SQLite | Debug 输出与 self-contained `win-x64` 发布产物**均为 3.53.3** |

**分装配测试数（阶段 2 之后 → 现在）**：

| 装配 | 之前 | 现在 | 变化 |
|---|---|---|---|
| `HD2ModCore.Tests` | 498 | **509** | +11（+8 缓存用例、+4 登记表用例、−1 随 `PatchValidator` 删除） |
| `HD2ModAdaptation.Tests` | 187 | **178** | −9（随 `CanonicalDependencyClosureTests` / `PatchValidatorTests` 删除） |
| `HD2ModCli.Tests` | 141 | **141** | 0 |
| `Decryptor.Tests` | 5 | **5** | 0 |
| **全量** | **831** | **833** | +2 |

> **一处需要留意的历史记账错误**：§19.5 写的是"830 通过（Core 497）"，但同一天的工作日志记的是"831 通过（Core 498）"。
> 两者差 1。以**工作日志为准**：因为 `498 + 11 = 509` 正好对得上本轮实测的 Core 509（`+8 +4 −1`），
> 而 `497 + 11 = 508` 对不上。§19.5 的 497/830 是笔误，不是另一次运行的结果。
> 上表的"之前"列因此采用 **498 / 831**。

### 20.8 仍未做的事

| 项 | 说明 |
|---|---|
| **§6 那一版的"页面收编"** | §20.2 只做了**登记表**这一层。让浏览器页/护甲页不再直调 `MapAsync` / `GetArchiveSummariesAsync` 属于**改页面**，验收口径要跟着变，**留待单独拍板** |
| **E2 · 阶段 3（分层归位）** | 消费 `FactDescriptor.CacheScope` 与 `FactProducerKind`，把四类节点（Source / PersistentFact / DerivedMemo / Operation）真正落到代码里。DAG 已就绪 |
| **§20.3 缓存的覆盖面** | 缓存挂在两个**源事实**上（`AssetMetadata` / `GameDataMapping`）。6 个业务服务仍在直调 `MapAsync`——但它们现在**共享同一份缓存**了，这正是 §20.2 想先拿到的收益 |
| **~~6 个业务服务仍直调 `MapAsync`~~** | **已完成（2026-10-11，§28）**。当时判断"缓存收益已到手、收口只是锦上添花"，实际收口后拿到了四样中心独有的东西：并发合并、按 FactKey 的生产诊断、尊重调用方声明的缓存作用域、取消跟随中心关闭。 |

### 20.9 一句话总结

本轮把 §19.6 的 E1 落地成**登记表内的两个新源事实 + 一份按来源指纹键控的进程级记忆**，
并用**源码文本断言**钉住"重建路径永不读缓存"，从而在拿到缓存收益的同时，
把"缓存取代真相"这件事变成**结构上做不到**，而不是"我们承诺不会"；
随后清空了全部死代码（不可达类型 40 → 0）与全部警告，并借警告即错误这道新护栏
逼出了一个**真实存在、此前被忽略**的内嵌 SQLite 漏洞（3.41.2 → 3.53.3）。
**零页面改动、零新增警告、833 测试全绿、5 个本地提交、未推送远端。**

## 21. 阶段 3 第一半：按来源指纹记忆部署图（已完成）

> 实施时间：2026-10-11
> 状态：**代码已实施、已验证、待提交**。

### 21.1 为什么先做这一步

§6 的阶段 2/3 是同一句话的两半："先把中心之外收进来"。阶段 2 的**页面那一半**还没做（§20.8），
而它的前置是一套"游戏数据侧作用域的请求类别"——中心今天所有请求都挂在 `ModNode` 上，
game-data 事实没有节点，这是**新的架构形态**，不是补几个方法。相比之下阶段 3 的缓存那一半：

- 只动**一个文件**，没有新接口、没有新请求类别；
- 打中的是 §13.2 记录的**已知性能热点**；
- 完成判据是**行为性**的（"部署未变时二次打开零重算"），可以真测。

所以先把这一半做掉，拿到确定的收益，再回头做阶段 2 的页面收编。

### 21.2 热点到底在哪

`DeployedOverrideGraphService.BuildAsync` 对**每个已部署 patch 文件**要：

1. `ValidateFileAsync` 里对**目标文件**算一次 SHA-256（与激活状态记录的 `ContentSha256` 比）；
2. 再对状态里记录的**源文件**算一次 SHA-256（确认源没被改过）；
3. 对每个 base 文件扫一遍 TOC。

而 `CoreServices.CreateDeployedOverrideGraphService()` **每次调用都 new 一个实例**
（`CreateGameDataArchiveBrowserService` 里就地 new），所以护甲页 / GameData 浏览器页
**每开一次就把上面这些全量重算一遍**，没有任何缓存。

**顺带查清的一件事**：`DerivedStateCoordinator` 里 `deployed` 是被**主动置空**的——

```csharp
// Do not scan GameData while a profile page is opening. It cannot affect the four player states.
deployed = null;
```

也就是说 L4 那一层根本不建部署图，`DeployedOverrideGraph?` 这个槽位恒为 null。
**生产路径上唯一的调用者就是 `GameDataArchiveBrowserService.BuildAsync`**，
热点分析因此收敛到一个点。

### 21.3 改动

**新增**

| 文件 | 内容 |
|---|---|
| `HD2ModCore/Infrastructure/SourceFingerprintMemo.cs` | 一份"按来源指纹键控"的进程内记忆（有界、整体清空、类型校验）。抽出来是为了让不同族事实各拿一份**独立预算** |
| `HD2ModCore/Infrastructure/DeployedGraphFactCache.cs` | 部署侧的持有者（上限 4）。契约与 `GameDataFactCache` 相同 |

**修改**

| 文件 | 动作 |
|---|---|
| `DeployedOverrideGraphService.cs` | `BuildAsync` **先算来源指纹**，命中就直接返回上次的图，跳过整套校验；未命中才走原路径，算完 `Set` 进记忆。新增 `ComputeSourceCacheKey` 与 `StatOrMissing` |
| `GameDataFactCache.cs` | 改为持有 `SourceFingerprintMemo(16)` 的薄壳（对外 API 不变，既有测试与源码断言照旧成立） |

**关键顺序问题**：`ComputeGeneration(dataFiles, state)` 原本在**最末尾**才调用，
但它只依赖已部署文件列表与激活状态——也就是**一开始就有的东西**。
把它需要的信息提前用于缓存键，就能在昂贵的校验之前决定"要不要做"。
`DeployedOverrideGraph.Generation` 的算法**没有改动**，所以对外的那个值不变。

### 21.4 键的覆盖面：为什么"源文件"必须在里面

键由三部分拼成：

| 部分 | 内容 |
|---|---|
| 激活状态 | `stateLoadFailed` 标志、`ProfileId`、`ProfileRevision`、以及每个记录项的 `TargetPath` / `ArchiveHex16` / `TargetPatchIndex` / `SidecarKind` / `Method` / `Length` / `ContentSha256` / `NodeId` |
| 已部署文件 | 每个文件的（名字小写, 长度, 修改时间 ticks）——文件名本身就带着 archive hex / 补丁索引 / sidecar 身份 |
| **记录里引用的源文件** | 每个 `SourcePath` 的（存在性, 长度, 修改时间 ticks） |

第三项是最容易被漏掉、也最危险的一项。`ValidateFileAsync` **会读源文件**
（`File.Exists(recorded.SourcePath)`、源文件长度、源文件 SHA-256）。
如果键只覆盖已部署文件，那么"部署的 patch 没动、它引用的源被改了"这件事**不会让键变化**，
缓存就会把上一次的"一切正常"当成事实返回——这正是 §20.3 那条硬约束要禁止的
**"缓存取代真实性"**。

**这一条不是推理出来的，是测出来的**（见 §21.5 的探针）。

### 21.5 完成判据与验证

判据用**实例同一性**与 `BuiltUtc`，而不是给测试加计数钩子：命中记忆时返回**同一个对象**、
`BuiltUtc` 保持第一次的值；真重算必然是另一个对象、`BuiltUtc` 是新的。

| 用例 | 锁住什么 |
|---|---|
| `RepeatedBuildOfAnUnchangedDeploymentIsServedFromTheMemo` | 部署未变 → `Assert.Same` + `BuiltUtc` 相同 = **零重算** |
| `ChangingADeployedPatchFileForcesAGenuineRebuild` | 改已部署文件 → 新对象 + `DeployedContentMismatch` |
| `ChangingARecordedSourceFileForcesAGenuineRebuild` | **只改源** → 新对象 + `DeployedSourceChanged` |
| `RemovingADeployedPatchFileForcesAGenuineRebuild` | 删已部署文件 → 新对象 + `RecordedTargetMissing` |

**构造这两条用例时踩到的一个坑（值得记住）**：真实的 `ApplyExecutor` **只支持硬链接与符号链接**
（`"Only hard-link and symbolic-link deployment are supported."`）。
链接之下"目标"与"源"本来就是同一个文件，改源必然同时改目标，**两件事分不开**。
所以"只改源"这条用例第一版**是假的**——它能通过，但不是因为指纹对，而是因为目标也被一起改了。
改用 `DeploymentMethod.Copy`（修复 / 导入流程与外部文件会记录这一种）手写激活状态，
才真正让目标与源分离。

**探针验证（"测一测这个测试"）**：把源文件那一段从 `ComputeSourceCacheKey` 里拿掉，
重跑这一组——

```
[FAIL] ChangingARecordedSourceFileForcesAGenuineRebuild
       Assert.NotSame() Failure: Values are the same instance
       失败! - 失败: 1，通过: 5
```

**失败的那一条正好是它**，证明这条用例是真的在钉"源文件在指纹里"，而不是碰巧通过。
探针随后已还原（实现文件里 `PROBE` 计数为 0）。

**整体验证**：

| 判据 | 结果 |
|---|---|
| 全解决方案重建（警告即错误） | **0 警告 / 0 错误** |
| 全量测试 | **837 通过 / 0 失败**（Decryptor 5、Adaptation 178、**Core 509 → 513**、Cli 141）——Core 的 +4 正好是本组新用例 |
| 接口 / 页面 | **零改动** |

### 21.6 为什么两族事实分成两份记忆

`GameDataFactCache`（游戏数据侧，上限 16）与 `DeployedGraphFactCache`（部署侧，上限 4）
各自持有 `SourceFingerprintMemo` 实例，而不是共用一个字典。理由：

- 两族事实的**体积差一个量级**（一个映射结果 vs 一整张部署图），访问节奏也不同；
- 共用时，部署图会把映射结果挤出去——而映射结果是上一轮刚花力气接上的；
- 更实际的是：**分开之后各自的预算才是稳定的**，"命中记忆"这类断言才是确定性的，
  否则任何一个并行测试往同一个字典里写都可能把它清空。

### 21.7 仍未做的一半

| 项 | 说明 |
|---|---|
| **登记 `DeployedGraph` kind** | ✅ **已在 §22 完成**（本轮）。登记为 `SubjectKind=GameData`、`CacheScope=Session` 的源事实 |
| **`GameDataArchiveBrowserService` 退化为纯投影** | ✅ **已在 §22.6 完成**。它现在只认识 `IGameDataFactSource` |
| **阶段 2 页面收编** | ✅ **前置已在 §22 解除**。中心补上了"无节点事实"形态，`RequestGameDataIndexAsync` / `RequestGameDataMappingAsync` / `RequestDeployedGraphAsync` 三个入口就位 |

> 关于 §4 #11 写的 `Scope=Deployment`：本轮登记时取的是 `CacheScope=Session`，**这是一处刻意的口径澄清**，
> 理由见 §22.10。

### 21.8 一句话总结

把 `DeployedOverrideGraphService` 从"每次全量重算"改成"**先算来源指纹，命中就跳过**"，
并用四条用例把"部署未变零重算"和"任何一样输入变了都必须真重算"钉住；
其中最关键的"源文件在指纹里"一条，是靠**先证伪自己**（把源移出指纹 → 用例必须失败）才确认成立的。
**零接口改动、零页面改动、零新增警告。**

---

## 22. 无节点事实的请求与失效形态（已完成）

> 这一节就是 §6.1 里那个"贯穿全局的前置"，也是 §21.7 里点名"仍未做的一半"。

### 22.1 问题：中心只会说"某个 Mod 的某个方面"

中心此前**所有**请求都是这个形状：

```csharp
ValueTask<ModInformationResult<T>> RequestXxxAsync(ModNode node, string modsRootDirectory, ModInformationRequest request, …)
```

由此派生出三件事，每一件都要求"有一个节点"：

| 机制 | 依赖节点的地方 |
|---|---|
| 任务账 | 每个属性一本 `ConcurrentDictionary<FactRevisionKey, Lazy<Task<…>>>`，键里的主体是 `FactSubject.Node(id)` |
| 取消 | `BeginNodeRequest(node.Id)` → 每节点一个 `CancellationTokenSource`，`InvalidateNodeAsync` 时 `Cancel()` |
| 失效 | `InvalidateNodeAsync(nodeId)` 按主体逐本账 `TryRemove`，并删各缓存层的该节点记录 |

而 game-data 侧与部署侧的事实**没有节点**：它们的身份是"游戏目录 + 来源指纹"。
`RequestXxxAsync(ModNode, …)` 套不上去——不是"少写几个方法"，而是**缺一种形态**。
这就是阶段 2 页面半边与阶段 3 登记半边一直没做的真正原因（§6.1 结尾那段话）。

### 22.2 身份：把作用域写进主体，而不是只写进修订

新增 `FactSubject.GameData(string scope)`（`FactSubject.GameData()` 保留，等价于 `scope = "global"`）：

```csharp
public static FactSubject GameData(string scope) { … return new(FactSubjectKind.GameData, scope); }
```

三个事实各自的作用域：

| 事实 | 主体 | 为什么不能只靠 `Kind` 区分 |
|---|---|---|
| `GameDataIndex` | `GameData("index")` | 只有一个索引，作用域恒定 |
| `GameDataMapping` | `GameData($"mapping:{SHA256(排序后的资产键集合)}")` | **不同批次的资产是不同的映射事实**。若共用 `"mapping"`，两个页面并发打开时后一个会拿到前一个的资产清单 |
| `DeployedGraph` | `GameData($"deployed:{游戏目录}")` | 不同游戏目录是不同部署图 |

> 口径说明：`Revision` 的语义是"**同一件事**的输入指纹"，而 `Subject` 的语义是"**哪一件事**"。
> 资产键集合与游戏目录决定的是"哪一件事"，所以它们进 `Subject`，`Revision` 继续留 `"auto"`
> （与 `FileFacts` 一致：真正的 generation 在生产完成后才写入结果）。
> 把"哪一件事"塞进 `Revision` 会让身份表述失真——`FactRevisionKey` 打印出来会像"同一个索引的两个版本"。

### 22.3 合并机制：一本账 + 类型还原

```csharp
private readonly ConcurrentDictionary<FactRevisionKey, Lazy<Task<ModInformationResult<object>>>> _gameDataTasks = new();
```

**只开一本账，不按属性分三本。** 因为 `FactRevisionKey` 里已经含 `ModInformationPropertyKind`，
三类事实在键上就不可能相撞；代价只是取出时要把 `object` 还原成具体类型：

```csharp
private static ModInformationResult<T> Unwrap<T>(ModInformationResult<object> result) where T : class
    => new(result.Data as T, result.Status, result.Kind, result.Generation, result.Issues,
           result.WasCoalesced, result.RefreshFailed, result.CacheHit);
```

`Data` 为 `null` 时保持 `null`（索引未建 / 生产失败），所以 `Unwrap` 不会把失败伪装成空结果。

其余与节点事实完全同构：`Lazy<Task<…>>`（`ExecutionAndPublication`）+ `GetOrAdd` + 归属方判 `WasCoalesced`
+ **`finally` 里 `TryRemove`**（所以这本账只合并"同时进行中"的请求，不是缓存）。

### 22.4 失效边界：无节点事实**不**参与 `InvalidateNodeAsync`

这是本轮唯一一处"少做一件事"的决定，所以单独交代。

`InvalidateNodeAsync` 里有八本账在按主体 `TryRemove`，第九本（`_gameDataTasks`）**刻意没写**：

> 无节点事实的输入是游戏目录、激活状态、资产索引与社区元数据文件——
> **没有一项来自 Mod 目录**。所以"某个 Mod 变了"不构成它们失效的理由。清掉只会让下一次请求白做一遍。

这条边界由行为用例钉住（见 §22.8）：先把一个索引请求卡在生产者里，然后对某个节点调 `InvalidateNodeAsync`，
再发一个**完全相同**的索引请求——它必须**仍然被合并**到第一个请求上（生产者只被调用 1 次）。

### 22.5 生产者从哪来：三个**可选**依赖，不新建 producer 接口

`ModInformationCenter` 的构造函数尾部追加三个可选参数：

```csharp
IAssetArchiveIndexService? assetArchiveIndexService = null,
IGameDataMappingFactsService? gameDataMappingFactsService = null,
IDeployedOverrideGraphService? deployedOverrideGraphService = null,
```

**为什么不照 `IModFileFactsProducer` / `IAssetInventoryProducer` 的样子各造一个 producer 接口？**
因为这三个 application 接口本身就是单方法的生产者形状：

| 事实 | 已有的接口 | 方法 |
|---|---|---|
| `GameDataMapping` | `IGameDataMappingFactsService` | `MapAsync(assetKeys)` |
| `DeployedGraph` | `IDeployedOverrideGraphService` | `BuildAsync(gameDataDirectory)` |
| `GameDataIndex` | `IAssetArchiveIndexService` | `GetFingerprintAsync()` + `GetArchiveSummariesAsync()`（两方法，唯一需要拼装的一个） |

再包一层只会得到三个 1:1 转发的空壳。可选的意义是**只读宿主可以不配置它们**：
此时请求返回 `Unavailable` + 一条 `XxxUnavailable` 警告，而不是抛异常——与 `_referenceGraphProducer`
等既有可选生产者的行为一致（中心里已有 5 处同样的写法）。

### 22.6 投影解耦：`IGameDataFactSource` 的两个实现

`GameDataArchiveBrowserService` 原先同时直调三个旁路服务。§7 护栏 1 要求业务服务不得直调它们，
于是引入一层只含三件事的来源接口：

```csharp
public interface IGameDataFactSource
{
    ValueTask<GameDataIndexFacts?> GetIndexAsync(CancellationToken cancellationToken = default);
    ValueTask<GameDataMappingFacts> GetMappingAsync(IReadOnlySet<AssetKey> assetKeys, CancellationToken cancellationToken = default);
    ValueTask<DeployedOverrideGraph> GetDeployedGraphAsync(string gameDataDirectory, CancellationToken cancellationToken = default);
}
```

两个实现：

| 实现 | 走哪条路 | 给谁用 |
|---|---|---|
| `CenterGameDataFactSource` | `IModInformationCenter`：统一身份、并发合并、诊断事件 | Manager |
| `DirectGameDataFactSource` | 直接问旁路服务，**刻意不创建也不刷新任何中心缓存** | CLI 只读检查（`gamedata browser` 必须是一条纯读命令） |

`GameDataIndexFacts` 把"指纹 + 全部归档摘要"包成**一个值**，理由是这两样是**同一次读取**的产物
（指纹决定摘要是否还新鲜）；拆成两个请求就会让"指纹是新的、摘要是旧的"成为可能。

投影的两个构造重载随之变成"来源 + 谁提供节点资产清单"：

```csharp
public GameDataArchiveBrowserService(IGameDataFactSource factSource, IModInformationCenter informationCenter)
public GameDataArchiveBrowserService(IGameDataFactSource factSource, IAssetInventoryProducer directInventory)
```

于是这个类**不再出现** `IAssetArchiveIndexService` / `IGameDataMappingFactsService` / `IDeployedOverrideGraphService`
中的任何一个名字——§7 护栏 1 的"业务服务半边"从此可以被测试固定下来。

### 22.7 失败语义：`Unavailable` 不是失败，而失败不能被静默成空

三类结果要分清，混起来 UI 就会显示成"什么都没有"，比报错更难查：

| 情形 | 中心返回 | 直读版返回 | 适配器 `CenterGameDataFactSource` 的动作 |
|---|---|---|---|
| 索引还没建 | `Unavailable`，`Data = null`，**`Issues` 为空** | `null` | `Unavailable` → 返回 `null`（两者语义对齐，`BuildAsync` 据此整页返回 `null`） |
| 生产真的失败 | `Failed`，`Data = null`，带 `Error` 级 issue | 抛异常 | **还原成 `InvalidOperationException`**，附上状态与全部 issue |
| 宿主没配置生产者 | `Unavailable` + `XxxUnavailable` 警告 | —（构造时就不可能发生） | 走"生产失败"那一支 |

第二行是本轮一个不显眼但重要的选择：中心的契约是"失败表达成 `Data = null` + `Issues`"，
而投影原本期望硬失败直接传播。适配器选择**把它还原成异常**而不是返回空——
"资产映射失败了"和"这批资产没有映射"是完全不同的两件事，后者会让页面安静地显示错误结论。

### 22.8 验证结果

**构建**：`dotnet build WpfApp1.sln -t:Rebuild` → **0 警告 / 0 错误**（10 个项目全量重建）。

**测试**：`dotnet test WpfApp1.sln` → **847 通过 / 0 失败**
（Decryptor 5、HD2ModAdaptation 178、**HD2ModCore 523**、HD2ModCli 141）。

Core 从 513 增到 523，正好是本轮新增的 10 条用例：

| 用例 | 钉住的性质 |
|---|---|
| `RequestGameDataIndexAsync_CoalescesConcurrentRequests` | 同一事实的并发请求合并成一次生产 |
| `RequestGameDataIndexAsync_WhenIndexIsNotBuilt_ReturnsUnavailableWithoutFailure` | "索引未建"是 `Unavailable` 且**不带 issue**，不是失败 |
| `RequestGameDataMappingAsync_DifferentAssetSetsProduceSeparately` | 不同资产批次**不**合并（§22.2 的核心） |
| `RequestGameDataMappingAsync_SameAssetSetInAnyOrderCoalesces` | 作用域令牌与枚举顺序无关 |
| `RequestDeployedGraphAsync_CoalescesSameDirectoryButNotDifferentOnes` | 同目录合并、异目录分开 |
| `NodeLessFacts_AreNotDisturbedByNodeInvalidation` | §22.4 的失效边界 |
| `MissingGameDataProducers_ReturnUnavailableInsteadOfThrowing` | 三个可选生产者缺失时返回 `Unavailable` |
| `NodeLessFacts_PublishDiagnosticsWhoseSubjectMatchesTheRegistry` | 诊断事件无 `NodeId`，且**运行时主体 == 登记表声明的主体** |
| `CenterGameDataFactSource_WhenIndexIsNotBuilt_ReturnsNullInsteadOfThrowing` | 适配器与直读版在"未建索引"上语义对齐 |
| `CenterGameDataFactSource_WhenProductionFails_ThrowsInsteadOfReportingEmpty` | 失败被还原成异常，不静默成空结果 |

**四条探针（先证伪自己）**：每条新用例都通过"故意破坏实现 → 用例必须失败"确认过它在承重，
而不是"写完就绿"。探针移除的实现点与观察到的失败：

| 探针 | 移除的东西 | 观察到的失败 |
|---|---|---|
| 1 | 映射主体的资产键集合令牌 | `DifferentAssetSetsProduceSeparately`：期望 2 次生产，实际 1 次 |
| 2 | §22.4 的失效边界（改成在 `InvalidateNodeAsync` 里 `_gameDataTasks.Clear()`） | `AreNotDisturbedByNodeInvalidation`：期望 1 次生产，实际 2 次 |
| 3 | 部署主体的游戏目录 | `CoalescesSameDirectoryButNotDifferentOnes`：期望 2 次生产，实际 1 次 |
| 4 | 索引主体的 `GameData` 种类（改成 `Node`） | `SubjectMatchesTheRegistry`：`OperationKey` 以 `Node:` 开头，登记表声明的是 `GameData` |

四条探针执行后均已还原，并以 `grep -rn 'PROBE-'` 确认为 0 处残留。

**一处顺带修好的测试**：`ModInformationFactRegistryTests.OnlyTheGameDataFactsUseTheGameDataSubject`
原先枚举"哪几条事实用 `GameData` 主体"，本轮 `DeployedGraph` 合法地成为第四条，
该列表随之更新。它同时是一道有价值的护栏——除这几条之外，任何 kind 若声明成 `GameData` 都会失败。
（§23 又把 `ArmorCoverage` 加成第五条。）

### 22.9 仍未做的

| 项 | 说明 |
|---|---|
| **阶段 4 护甲页改造** | 仍然是护甲页首开卡顿的主因（§13.2）。它要改为请求 `ArmorCoverage`（`Selector=护甲 archive ids`）+ 中心 Thumbnail，停止调用 `BuildAsync` |
| **阶段 5 合并缩略图双路** | `ThumbnailService` 降级为中心 Thumbnail 产品的渲染器 |
| **阶段 6 护栏测试的业务服务半边** | 现在条件具备了（§22.6），但本轮没有加架构测试 |
| **`GameDataMapping` 的 `CacheScope` 仍是 `Persistent`** | 它今天不落盘（中心侧无持久层）。登记表里写的是"允许的最长生命周期"上限，不是"实际会落盘"，所以不算错；但若将来真的落盘，需要单独裁决 |

### 22.10 一处口径澄清：`CacheScope` 不是"作用域"

§3 的对照表与 §4 #11 都把部署图写成 `Scope=Deployment`，而本轮登记时取的是
`CacheScope = Session`。这不是写错了，而是本文档里"Scope"这个词被用在了两个不同的轴上：

| 轴 | 取值 | 含义 | 代码里的载体 |
|---|---|---|---|
| **作用域**（主体维度） | `Node` / `GameData` / `Deployment` / `Library` / `Profile` / `Global` | "这是**哪一件事**" | `FactSubject`（`FactSubjectKind` + `Value`） |
| **缓存范围**（生命周期阶梯） | `None` / `Operation` / `Session` / `Persistent` | "这件事**最多**能活多久" | `ModInformationCacheScope`（`FactDescriptor.CacheScope`） |

`ModInformationCacheScope` 是一个**单调的生命周期阶梯**，不是主体维度。
若为了对齐文档而往阶梯里塞一个叫 `Deployment` 的值，它会和 `Operation` / `Session` 无法比较——
"部署图比 Session 长还是短"是个没有答案的问题。

所以：

- "部署图的作用域是 Deployment" → 由 `FactSubject.GameData($"deployed:{目录}")` 表达（§22.2）；
- "部署图的缓存范围" → `Session`，因为它的记忆是进程内的、按来源指纹键控的（`DeployedGraphFactCache`），**从不落盘**。

§4 #11 表格里的 `Scope=Deployment` 应读作"作用域"，而 `Scope` 列在登记表里指的是 `CacheScope`。
两处用词相同、含义不同，是本文档早期版本留下的歧义，这里一次性说清。

### 22.11 一句话总结

给中心补上第四种主体形态（`GameData`）与一本共用的进行中任务账，
让"游戏目录 + 来源指纹"这类事实第一次能走统一身份、并发合并与诊断事件；
并用**三个探针**（分别移除映射作用域、部署作用域、失效边界）证明新增用例真的在承重，
而不是"写完就绿"。


---

## 23. 护甲页改造：从"整包构建浏览器"到"只要覆盖事实"（已完成）

### 23.1 问题：为了两个字段构建整个浏览器

护甲页每次打开都会走 `ArmorRegistryPageViewModel.ReadArmorDisplayFactsAsync`，
它做的事是：对护甲目录里的每一件护甲，取**两个字段**——
是否命中 Unit 覆盖（`HasEffectiveUnitOverride`）、覆盖者的图标路径（`ImagePath`）。

而它的实现方式是构建**整个** GameData 归档浏览器：

```csharp
var browser = CoreServices.CreateGameDataArchiveBrowserService(paths, library.InformationCenter);
var snapshot = await browser.BuildAsync(library.Snapshot, library.ModsRootDirectory, gameData);
```

`BuildAsync` 里有一步 `GetAssetInventoryAsync`：**对每一个 Mod 节点**请求一次资产清单
（逐包解析 patch TOC、读条目、建资产表）。这就是 §1.3 与 §13.2 记录的"护甲页首开卡顿"根因——
打开护甲页的代价与**整个 Mod 库的规模**挂钩，而它真正需要的两个字段与 Mod 库无关。

### 23.2 事实的形状：只对"被问到的那批归档"作答

新增 `ArmorCoverage`（第 25 条 kind，第 4 条无节点事实）：

```csharp
public sealed record ArmorCoverageEntry(
    bool HasEffectiveUnitOverride,
    ModNodeId? UnitWinnerNodeId,
    ModNodeId? CoverWinnerNodeId);

public sealed record ArmorCoverageFacts(
    string CoverageGeneration,
    IReadOnlyDictionary<string, ArmorCoverageEntry> Archives,
    IReadOnlyList<CoreIssue> Issues);
```

两个赢家字段回答两个不同的问题，不是重复：

- `UnitWinnerNodeId` 只统计 Unit 类资产 → "这件护甲的**模型**被换了吗"；
- `CoverWinnerNodeId` 在无 Unit 赢家时退回到任意资产的赢家 → "这件护甲的**外观**被谁改了"。

页面拿 `CoverWinnerNodeId` 查图标、拿 `HasEffectiveUnitOverride` 做筛选，
与改造前逐条读浏览器覆盖层的判定逐字一致。

### 23.3 依赖三条上游，但喂给映射的键集合刻意更小

登记表的声明：

```csharp
[ModInformationPropertyKind.ArmorCoverage] = new(
    FactSubjectKind.GameData,
    ModInformationCacheScope.Session,
    FactProducerKind.Composite,
    [GameDataIndex, GameDataMapping, DeployedGraph]);
```

生产时中心按这个顺序**自己请求**上游（因此与页面直接请求时共用同一本合并账）。但注意喂给映射的资产键集合：

```csharp
var winnerKeys = deployed.Data.AssetChains
    .Where(chain => chain.Winner.NodeId is not null)
    .Select(chain => chain.AssetKey)
    .ToHashSet();
```

浏览器用的是"全部 Mod 的资产键"，而那需要逐节点资产清单——正是要摆脱的那一步。
覆盖结论只可能来自**已部署且有归属**的资产，所以这个更小的集合既够用又便宜。

**与浏览器覆盖层的等价性**（逐条核对，不是推断）：
浏览器 `effectiveAssetsByArchive` 的构造是
`foreach chain in deployed.AssetChains { if (!mapping.Assets.TryGetValue(chain.AssetKey, …)) continue; … }`。
本投影用的是同一张部署图与同一个映射服务，差别只有两点：
① 映射只覆盖"有赢家的部署链"的键——超出部分是赢家为 null 的链，护甲页本来就会过滤掉；
② 提前跳过赢家为 null 的链——浏览器把它们放进覆盖层，护甲页随后也过滤掉。
对护甲页真正读取的字段而言，两者结果相同。

### 23.4 为什么图标**不**进事实（护栏 3 反过来要求这么做）

§4 #23 把依赖写成 `11 + 10（+ 6 图标）`，本轮**没有**把图标纳入事实。理由是 §7 护栏 3：

> **改图标 → `ArmorCoverage` 不重算**

图标是对赢家节点的**库内查找**（`library.Get(id)` + 父链，全是内存字典访问），代价可忽略。
把它塞进事实会让"换个图标"变成"覆盖事实重算"，正好与护栏 3 相反。
所以图标留在页面里解析，事实只回答"被谁覆盖"。

### 23.5 "读不到" ≠ "没有覆盖"

这是本轮最需要小心的一条。三种"没有数据"必须区分开：

| 情形 | 事实的答复 | 页面的表现 |
|---|---|---|
| 索引里有这件护甲，但没有已部署资产落在它上面 | `HasEffectiveUnitOverride = false`（**确定**的否定） | "未命中 Unit 覆盖"，筛选器能筛到它 |
| 这件护甲**不在索引里** | 事实里**没有这条**（缺席） | 保持"未知"（`null`），两个筛选器都不选它 |
| 索引还没建好 | 整个事实 `Unavailable`、`Data = null` | 全部保持"未知" |

三种情形对应三种不同的正确行为；把后两种写成 `false`，就是从缺失数据里推出了一个否定事实——
正是 §4 里 `ModInformationPropertyStatus` 反复强调要避免的误判。

实现上分别落在三处：投影里的 `known` 过滤（只对索引里存在的归档作答）、
投影对缺席归档不作答、生产者在 `index.Data is null` 时返回 `Unavailable`。

### 23.6 页面改动与"不得回头"的护栏

`ReadArmorDisplayFactsAsync` 现在只有一句取数：

```csharp
var coverage = await library.InformationCenter.RequestArmorCoverageAsync(
    gameData, archiveIds,
    new ModInformationRequest(ModInformationKind.ArmorCoverage, "ArmorRegistryPage"));
```

父链计算与图标查找原样保留（它们本来就是内存操作）。
生产失败时记一条 `LogService.Warn`——否则"读失败"会静默变成"没有护甲被覆盖"。

新增一条结构护栏（`ModInformationCenterArchitectureTests`）：

```csharp
[Fact]
public void ArmorPageProjectsCoverageInsteadOfBuildingTheGameDataArchiveBrowser()
{
    var source = File.ReadAllText(…ArmorRegistryPageViewModel.cs);
    Assert.DoesNotContain("CreateGameDataArchiveBrowserService", source);
    Assert.DoesNotContain("BuildAsync", source);
    Assert.Contains("RequestArmorCoverageAsync", source);
}
```

一旦有人为了"省一次请求"把 `BuildAsync` 贴回去，这里立刻失败。

### 23.7 验证结果

- 全解决方案重建：**0 警告 / 0 错误**。
- 全量测试：**862 通过 / 0 失败**（Decryptor 5、Adaptation 178、**Core 523 → 538**、Cli 141）。
  Core 的 +15 正好是本轮新增用例：投影语义 11 条 + 中心无节点事实 3 条 + 结构护栏 1 条。
- 接口改动：`IModInformationCenter` +1 方法（两个测试替身同步补齐）；
  `ModInformationKind` / `ModInformationPropertyKind` / `EffectiveProperty` / 登记表各追加取值（**一律追加在末尾**）。
- 登记表：24 → **25** kind；`SourceRoots` 仍为 7（`ArmorCoverage` 是派生记忆，有依赖，不进源事实集合）。
- 页面代码：**只有护甲页取数那一段变了**，其余 2000 行未动；`GameDataArchiveBrowserService` 及其两个工厂未动（浏览器页、Mod 详情页仍走它）。
- 依赖图：`ArmorCoverage → {GameDataIndex, GameDataMapping, DeployedGraph}`，无环（`Validate()` 自检通过）。

### 23.8 一处发现：探针 D **没有**失败，说明两处守卫各自冗余

按 §21 的做法，每条新用例都要用"故意破坏实现 → 用例必须失败"确认承重。四条探针：

| 探针 | 移除的东西 | 观察到的失败 |
|---|---|---|
| A | 覆盖主体里的归档集合令牌 | `CoalescesTheSameArchiveSetButNotDifferentOnes`：期望 2，实际 1 |
| B | 投影里"只对索引中存在的归档作答"的过滤 | `AnArchiveMissingFromTheIndexIsAbsent…`：`armor-9` 出现在了结果里 |
| C | "索引未建 → Unavailable" | `WhenTheIndexIsNotBuilt_ReportsUnavailable…`：期望 `Unavailable`，实际 `Fresh` |
| D | 赢家排序里排除无主资产的过滤 | **没有失败** |

探针 D 的结果本身就是发现。原因：`Project` 里已经有一句 `if (chain.Winner.NodeId is null) continue;`，
无主链根本到不了 `RankWinners`。于是**两处守卫各自冗余**——单独移除任何一处，行为都不变：

- 只移除 `Project` 里的 `continue`（保留 `RankWinners` 的过滤）→ 用例**仍然通过**（已实测）；
- 只移除 `RankWinners` 的过滤（保留 `continue`）→ 用例**仍然通过**（已实测）；
- 两处同时移除 → 用例失败：3 条无主资产多于 1 条有主资产，`null` 组赢了，`HasEffectiveUnitOverride` 变成 `false`。

结论：这条用例承重的是**这对守卫**，而不是其中任何单独一处。
两处都保留是有理由的——`continue` 让"只有有归属的部署才进入累加"这个不变量在累加点就写明，
`RankWinners` 的过滤让它对任意输入都是全函数（不会因为将来别处调用而抛 `InvalidOperationException`）。
但"两处各自冗余"这件事必须记下来，否则下一个读代码的人会以为随便删一处没关系。

探针 D 也顺带暴露了原用例的弱点：它只断言"没有被覆盖"，
而"null 组赢了"恰好也给出"没有被覆盖"。于是把用例改成
`ADeployedChainWithoutAnOwningNodeCannotOutrankARealOwner`：
让无主链的资产**更多**，断言有主的那条**仍然赢**。
这样"null 组赢了"与"正确结果"才在断言上可分。

### 23.9 仍未做的

- **量化验收**：本轮只完成了结构性改造与护栏；"首开耗时下降多少"需要在装有真实 Mod 库的机器上实测（§6.1 已注明）。
- **`ThumbnailService` 双路合并**（阶段 5）：本轮未做；**下一轮已做**（§27），
  并且侦察证明 §26 把它判断成"与用户关注点无关的一致性清理"是错的——它同时修掉一个真实的陈旧图标 bug（§27.1）。
  护甲页的图标仍走库内查找，没有走中心 Thumbnail；§4 #23 括号里的"+ 6 图标"因此仍未落地——但按 §23.4，它**不该**落在 `ArmorCoverage` 上。
- **§7 护栏 3 的用例**："改图标 → ArmorCoverage 不重算"还没有写成测试。本轮把图标移出事实已经让这条性质在结构上成立，但还没有用例钉住它。
  → **§25.3 已用"依赖闭包（含传递）+ 生产代码"两条断言把它钉住**；仍然缺的只是"同一次部署变化下覆盖重算而缩略图不重算"那条对拍（§25.5）。
- **Manager 里其余的 `CreateAssetArchiveIndexService` 调用点**（`GameDataBrowserPageViewModels`、`ModDetailsPageViewModel`、`PageViewModels`、`ShellViewModel`）：这些是**索引状态/重建**用途（§4 #8 的 Operation 面），与"读事实"不是一回事，本轮未动。

### 23.10 一句话总结

把护甲页从"为两个字段构建整个 GameData 浏览器"改成"向中心要一条 `ArmorCoverage` 事实"，
于是打开护甲页的代价不再与 Mod 库规模挂钩；
并用一条结构护栏（不得再出现 `BuildAsync`）与四条探针把这件事钉住——
其中探针 D 没失败，反而查清了"两处守卫各自冗余"，这条发现比一条通过的用例更有价值。

---

## 24. 启动预热：把护甲映射页串进模组库页的预热阶段

### 24.1 用户实测与请求

用户在本机真实 Mod 库上实测：打开护甲映射页**速度飞快**，比改造前快了好几倍。
用户判断自己的机器性能较好、倍率偏低，在较差的机器上提升幅度会更大；
并决定**等这轮优化重构彻底结束后再发正式版**。

随后提出本轮的第二件事：把护甲映射页也挂进"启动时预热模组库页"的那个阶段，
让用户点开这一页时更无感。

### 24.2 预热前"打开护甲页"的三块代价

| 块 | 内容 | 量级 |
|---|---|---|
| **视图** | `ArmorRegistryPageView` 的视觉树构建 + measure/arrange | 中（正是现有视觉预热系统存在的原因） |
| **VM** | `ArmorRegistryPageViewModel` 构造：建服务、挂三个 `CollectionView` | 小 |
| **数据** | 目录（一次 SQLite 查询）+ 快照（几个小文本文件）+ **`ArmorCoverage` 事实** | 大（覆盖事实要读索引指纹、归档摘要、部署图并做映射） |

§23 砍掉的是数据块里最贵的旧路径（整包构建 GameData 浏览器）；
剩下这一块虽然已经很快，但仍是"打开页面"里唯一需要等 IO 的部分。

### 24.3 三层预热，各占一个空闲回合

| 层 | 位置 | 时机 | 为什么在这个时机 |
|---|---|---|---|
| **VM** | `ShellViewModel.QueueWorkspacePagePrewarm` | `ContextIdle`（首页渲染后） | 它的构造会给三个集合挂 `CollectionView`，而 **`CollectionView` 绑定的是创建它的那个 Dispatcher**——所以必须趁 UI 线程空闲时建，不能等预热数据时再顺手 `new` |
| **数据** | `ShellViewModel.QueueArmorRegistryPrewarm` → `WarmUpAsync()` | `ApplicationIdle`，**且在启动维护之后** | 见 §24.5 的闸门 |
| **视图** | `WorkspacePagePresentationController` 的预热链 | `ApplicationIdle`，profile → library → **armor** 各一个回合 | 沿用既有设计：一次空闲只建一张页面，输入永远优先于预热 |

视图预热的宿主 `PrewarmArmorPageHost` 是**单独一层、占满整个工作区**，而不是与
`PrewarmProfilePageHost` / `PrewarmLibraryPageHost` 并排成三列。两个理由：

1. 护甲页是**整页布局**（单页模式），占满工作区量到的宽高才与真正打开时一致；
2. 并排成三列会把另外两张页面的测量宽度一起改掉（每列从 1/2 变成 1/3），
   等于顺手改变了已经调好的预热条件。

### 24.4 单飞加载：预热与首开共用同一次

`ArmorRegistryPageViewModel` 新增一本单飞账本：

```csharp
private readonly object catalogLoadGate = new();
private Task? catalogLoadTask;

public Task InitializeAsync() => hasLoadedCatalog ? RefreshRuntimeStatusAsync() : EnsureCatalogLoadedAsync(isWarmUp: false);
public Task WarmUpAsync()      => hasLoadedCatalog ? Task.CompletedTask      : EnsureCatalogLoadedAsync(isWarmUp: true);

private Task EnsureCatalogLoadedAsync(bool isWarmUp)
{
    lock (catalogLoadGate) return catalogLoadTask ??= LoadCatalogOnceAsync(isWarmUp);
}
```

没有它会掉进两个坑，两个都不是理论问题：

- **各读一遍**：预热刚跑完，用户点开页面又读一遍目录、快照、覆盖事实；
- **空页面**：预热还在跑时用户点开页面，`RefreshAsync` 的 `if (IsBusy) return;` 直接返回，
  页面停在"尚未读取"，要等预热跑完才刷出来——**恰恰在最想让它无感的时候露出破绽**。

账本在 `finally` 里清空：加载失败时 `hasLoadedCatalog` 仍为 `false`，下一次打开能重试。

### 24.5 取数闸门：预热最容易踩的坑

这是本轮**唯一一个真正危险的地方**，也是唯一一个"多写十行代码换正确性"的地方。

视觉预热会在窗口刚 `Loaded` 时就把护甲页视图建出来，从而触发它的 `OnLoaded` → `InitializeAsync()`。
那个时刻比下面两件事都早：

1. **库快照还没加载** → 图标查不到（图标是对"覆盖者节点"的 `library.Get(...)` 查找），
   结果全是 `null`；
2. **启动部署还没跑完** → 覆盖事实算的是**我们马上就要改掉**的那份部署。

更糟的是这两件事都不会自愈：`hasLoadedCatalog` 一旦置位，之后打开页面只走
`RefreshRuntimeStatusAsync`（只重读快照），**不会重新取覆盖事实**。
于是用户看到的是一份"永远没有图标、覆盖停在旧部署"的护甲列表。

修法：给页面一个取数闸门，由 `ShellViewModel` 在启动维护结束时放行。

```csharp
// ArmorRegistryPageViewModel.LoadCatalogInputsAsync 的第一步
if (displayFactsReady is not null) await displayFactsReady.ConfigureAwait(false);
```

闸门放在 `InitializeLibraryAndRunStartupChecksAsync` 的 **`finally`** 里放行，而不是 `try` 末尾：
启动维护失败或被取消时卡住它，等于让用户打开护甲页后永远停在"尚未读取"。

> 这条闸门的意义不是"预热总是晚于它们"，而是**不再依赖这个时序假设**。
> 依赖时序假设的预热，只要将来有人调整预热链的顺序就会静默出错，而且错得看不出来。

### 24.6 预热路径不占 UI 线程

`RefreshAsync` 拆成两半：

- `LoadCatalogInputsAsync()` —— 目录、快照、覆盖事实。**纯 IO + 纯投影，不触碰任何绑定到视图的集合**，
  因此可以安全地在任意线程上执行。失败返回 `null` 而不是空值（"读不到"和"没有护甲"是两件事）。
- `ApplyCatalogInputs`（内联在 `RefreshAsync` 里）—— `ApplySnapshot`。**必须在 UI 线程**：
  `BulkObservableCollection` 挂着 `CollectionView`，从别的线程改会抛
  "does not support changes to its SourceCollection from a thread different from the Dispatcher thread"。

预热路径整体丢到线程池（`Task.Run(LoadCatalogInputsAsync)`），只有最后的 `ApplySnapshot` 回到 UI 线程。
顺带一个副作用是好的：`LoadCatalogInputsAsync` 内部的 `ConfigureAwait(false)` 让**页面首开路径**
的逐护甲投影循环也不再占 UI 线程。

### 24.7 预热失败

预热失败**只记一条日志，不弹提示**——用户从没打开过这个页面，不该为它收到一个错误。
但状态栏仍然写"读取失败"与原因：用户真打开时看到的是失败，而不是假装"没有护甲被覆盖"。

### 24.8 验证

- 全解决方案重建：**0 警告 / 0 错误**
- 全量测试：**870 通过 / 0 失败**（Decryptor 5、Adaptation 178、**Core 538 → 546**、Cli 141）
  → Core 的 +8 = 登记表 2 条（§25.3）+ 架构 2 条（§25.2）+ 预热 4 条（本节）
- 新增 `StartupPrewarmArchitectureTests`（4 条）把预热的三条正确性前提钉住：
  闸门排在第一次读盘之前；预热与首开共用同一本单飞账本；闸门放行写在 `finally` 里；
  视觉预热链以护甲页收尾且宿主齐备（`PrewarmArmor` 记录项 + `PrewarmArmorPageHost` + 接线）。
- 探针：把闸门从"第一步"挪到第一次读盘**之后** → `ArmorPageWaitsForItsDisplayFactsGateBeforeReadingAnything`
  按预期失败，随后已还原。

### 24.9 仍未做

- **量化验收**：本轮仍未实测"预热之后首开耗时降了多少"。结构上已经把它压到"应用一次快照"，
  但真实数字要在装有真实 Mod 库的机器上跑（§6.1 已注明）。
- **预热与"库在启动后被改动"的关系**：护甲页的显示事实本来就是"首次加载时的一份快照"，
  不随库变化自动刷新（这是改造前就有的契约，预热只是把"首次加载"提前）。
  真正需要处理的是**我们自己在启动时改库**的那一段，已由闸门解决。

### 24.10 一句话总结

护甲页的 VM、数据与视图各自在一个空闲回合里提前建好，
其中数据与首开**共用同一本单飞账本**，并且必须**先等一道闸门**——
因为预热会跑在"库快照已加载、启动部署已落定"之前，而那种情况下算出来的显示事实是错的、
且不会自愈。闸门不是优化，是正确性要求。

---

## 25. 护栏补齐（§7 护栏 1 与护栏 3）

### 25.1 护栏 1 的"业务服务半边"：文档口径过宽，已按实际收口

§7 原文写的是：

> **新增**：`HD2ModManager` 与业务服务不得直接调用
> `IDeployedOverrideGraphService.BuildAsync`、`IGameDataMappingFactsService.MapAsync`、
> `IAssetArchiveIndexService.GetArchiveSummariesAsync`。

按字面实现会立刻失败。实测（2026-10-11）`MapAsync` 在 Core 里另有 **7 个正当调用点，分布在 6 个文件**：

| 文件 | 它为什么需要映射事实 |
|---|---|
| `AdvancedModAssetQueryService` | 把领域资产键落到具体归档 |
| `MaterialDeliveryFactsService` | 源图的材质键需要映射才能定位 |
| `ModAssetRoleFactsService` | 判断资产角色 |
| `ModAssetSummaryProjector` | 模组卡片上的资产摘要 |
| `ProfileMaterialDiagnosticsService`（2 处） | 配置级材质诊断 |
| `ProfileOverrideGraphService` | 配置覆盖图 |

它们要的就是**映射事实本身**，禁掉等于禁掉真实业务逻辑——那不是护栏，是误伤。

所以这条护栏按**方法**收口，并且只收口"整包事实、只应有一个正当来源"的那两个：

| 收口对象 | 允许出现的文件 | 理由 |
|---|---|---|
| `IAssetArchiveIndexService.GetArchiveSummariesAsync` | 接口本身、`CoreServices`（装配点）、`ModInformationCenter`、两个事实源适配器、实现自己 | 归档摘要是"游戏数据侧"的**整包**事实 |
| `IDeployedOverrideGraphService`（**整个类型**） | 同上 | 部署图是"部署侧"的整包事实 |

映射（`MapAsync`）的收口方式是 `IGameDataFactSource` 那道**投影缝**，见下一条。

### 25.2 投影缝：把"只有一条路"钉死

```csharp
[Fact] GameDataArchiveBrowserProjectsThroughTheFactSourceOnly
```

`GameDataArchiveBrowserService` 只允许认识 `IGameDataFactSource`，
不得出现 `IAssetArchiveIndexService` / `IGameDataMappingFactsService` / `IDeployedOverrideGraphService`
任何一个名字；同时 `CoreServices` 里必须存在 `new CenterGameDataFactSource(` 与
`new DirectGameDataFactSource(`（中心版给 Manager，直读版给 CLI 只读检查）。

这条断言的价值不在今天（今天本来就是对的），而在明天：
只要有人为了省一层又把它接回旁路服务，这里立刻失败。

> **一处已知的脆性**：这两条是**源码文本**断言，"在注释里提到这些类型名"也会让它们失败。
> 本仓既有的同类护栏（`ManagerDoesNotConsumeTheGameDataBypassServicesDirectly`、
> `InformationCenterNoLongerBuildsStringCacheKeys`）也是这个口径，保持一致。
> 代价是"写一句'我们不用 X'"会被拦下——这可以接受，因为要绕过它必须明确改测试。

### 25.3 护栏 3：图标不进覆盖事实

§7 护栏 3 的第一条是"**改图标 → `ArmorCoverage` 不重算**"。
§23.4 已经在结构上让它成立（图标留在页面，不进事实），但当时还没有用例钉住。本轮补两条：

| 用例 | 断言 |
|---|---|
| `ArmorCoverageDependsOnItsThreeUpstreamsAndNeverOnThumbnails` | `ArmorCoverage` 的**直接依赖**与**传递闭包**都不含 `Thumbnail` |
| `ArmorCoverageProductionNeverTouchesThumbnails` | 覆盖事实的**生产代码**（含它委派的两个方法）里不出现 `Thumbnail` 或 `_thumbnailProducer` |

两条缺一不可：登记表管得住**声明**，管不住**实现**——
有人完全可以在生产方法里顺手读一次缩略图，而依赖闭包测试不会发现。

**传递闭包**那一条同样是必需的：经由任何中间事实（比如给 `GameDataMapping` 加一条 `Thumbnail` 边）
绕到缩略图，一样会让"改图标"变成"覆盖事实重算"。探针 1B 实测确认了这条断言确实会拦下间接依赖。

### 25.4 探针（本轮五条，全部按预期失败后还原）

| 探针 | 移除/添加的东西 | 观察到的失败 |
|---|---|---|
| 1 | 给 `ArmorCoverage` 直接加 `Thumbnail` 依赖 | `DependsOnItsThreeUpstreamsAndNeverOnThumbnails`：`Found: Thumbnail` |
| 1B | 给**上游** `GameDataMapping` 加 `Thumbnail` 依赖（间接） | 同上（证明传递闭包断言承重） |
| 2 | 在覆盖生产方法里加一句 `_ = _thumbnailProducer;` | `ProductionNeverTouchesThumbnails`：`Found: "_thumbnailProducer"` |
| 3 | 把取数闸门从"第一步"挪到第一次读盘**之后** | `ArmorPageWaitsForItsDisplayFactsGateBeforeReadingAnything` 失败 |
| 4 | 让 `ReplacementTargetDeriver` 直调 `GetArchiveSummariesAsync` | `WholePackageGameDataReadsAreConfined…`：`Infrastructure\ReplacementTargetDeriver.cs: 调用了 G…` |
| 5 | 在投影服务里留下 `IDeployedOverrideGraphService` 字样 | `ProjectsThroughTheFactSourceOnly`：`Found: "IDeployedOverrideGraphService"` |

执行后 `grep -rn 'PROBE-'` 确认为 0 处残留。

本轮全量验证（与 §24 同一轮）：重建 **0 警告 / 0 错误**；测试 **870 通过 / 0 失败**，
其中 Core **538 → 546**（+8 = 本节 4 条 + §24.8 的 4 条）。

### 25.5 仍未做的

- **护栏 3 的第二条**（"改部署 → `ArmorCoverage` 重算，但 `Thumbnail` 不重算"）：
  前半句由 §21 的 `DeployedOverrideGraphServiceTests` 覆盖（部署指纹变了必须真重算），
  后半句由 §25.3 的两条用例覆盖（覆盖事实与缩略图互不相干）；
  但**在中心层面**把"同一次部署变化下，覆盖重算而缩略图不重算"写成一条对拍用例**还没有做**。
  原因是中心的无节点账本在生产完成后即清账（§22.4），
  "重算/不重算"在中心层面只能通过上游生产者的调用次数观察，而那两个生产者的记忆在**实现内部**
  而非中心——所以这条对拍用例真正的落点是两个服务的实现测试，不是中心测试。
- **护栏 3 的第四条**（"关闭页面 → 取消该页面私有请求，不取消全局任务"）：**已由 §27 以另一种方式消解，不再需要单独立案。**
  它原本要保护的是"页面级取消不该连累 `ThumbnailService` 的全局预生成任务"。
  §27.6 删掉了 `ThumbnailService.CancelPendingGeneration()`——它在 `PreGenerateAll` 被删之后
  只是换一个**没人读**的 `CancellationTokenSource`，即"全局任务"本身已不存在。
  现在只剩**页面私有**的取消（各页 `_thumbnailCancellation` 与中心的节点级令牌），
  结构上不存在"取消私有请求却取消掉全局任务"的可能。
- **`ThumbnailService` 双路合并**（阶段 5）：§26 时未做；**随后按 §27 完成**（§27.1 推翻了 §26 的成本/收益判断）。

### 25.6 一句话总结

护栏 1 的"业务服务半边"按文档原文会误伤 7 个正当调用点，所以按**方法**收口到两个"整包事实"，
再靠 `IGameDataFactSource` 那道投影缝管住映射；
护栏 3 用"直接依赖 + 传递闭包 + 生产代码"三条断言把"图标不进覆盖事实"钉死，
并用五条探针逐条证明它们真的在承重。

---

## 26. 为什么本轮不做阶段 5（缩略图双路合并）

> **后记（同日）：本节结论已被 §27 部分推翻，保留原文以留档。**
> 侦察后发现两件事让成本/收益判断反了过来：
> ① §26.1 说的"必须把 `Generation` 搬进 `ModCardViewModel`"**不成立**——磁盘键可以由源路径本身算出来；
> ② §26.4 说的"它是纯一致性清理、与用户关注点无关"**是错的**——同路径换图标会永久显示旧图，是一个真实 bug（§27.1）。
> 阶段 5 随后按 §27 完成。

阶段 5 的目标是让 `ThumbnailService` 降级为中心 Thumbnail 产品的**渲染器**
（只解码 / 落盘，不再自己判新鲜），验收口径是"全库仅一套缩略图失效协议"。

本轮**没有做**，理由是成本、风险与收益不匹配：

### 26.1 现状：一半已经收编，剩下的那一半牵动同步绑定

`ThumbnailService` 的头注释已经写着"仅根据 `ThumbnailSourceFacts` 渲染"，也确实有了
`EnsureThumbnailAsync(ModThumbnailFacts, …)` 与 `GetExistingThumbnailPath(ModThumbnailFacts?, …)` 两个重载。
缺的是**磁盘键**：它仍然是源路径的 SHA-256，而不是中心事实里的 `Generation`。

代码里对此有明确注释：

> 兼容现有同步 UI 绑定：当前模型只保存源路径，故 generation 暂不能安全加入磁盘 key。

也就是说，要做到"只剩一套失效协议"，必须先把 `Generation` 一路搬到 `ModCardViewModel`，
而 `GetExistingThumbnailPath` 是在**同步属性**里被调用的（`LibraryPageViewModel` 有 4 处、
`PageViewModels` 有 3 处）。这是把一条同步绑定链改成异步取数，属于界面层改造，不是服务层改造。

### 26.2 删除路径也会一起塌

`DeleteCachedThumbnailsForSource(originalPath)` 靠"按源路径重算键"来删三个尺寸的缓存。
键一旦改成 `路径 + Generation`，这个方法就**删不掉任何东西**——generation 无法从路径反推。
替代方案只能是缓存目录整体清扫或另立一张旁路索引，两者都是**新的失效协议**，
正好与阶段 5 的验收口径（"只剩一套"）相反。

### 26.3 换键意味着全量重生成

键变了，现存所有缩略图文件都成为孤儿，第一次打开库页要重生成全库缩略图。
对一个大库，这是一次明显的、用户可感知的卡顿——而库页的缩略图管道正是刚刚优化过的地方。

### 26.4 判断

- 它是**一致性清理**，不是性能收益，与用户当前的关注点（打开页面的感知卡顿）无关；
- 用户已明确"等这轮优化重构彻底结束后再发正式版"——
  在发版前把一个**新的失效协议**引入一条刚优化过的热路径，风险收益比不对；
- 它的验收口径（"全库仅一套协议"）在没有把 `Generation` 搬进界面模型之前**不可能成立**，
  所以它不是一个"改一天就能收尾"的尾巴，而是一次独立的小改造。

结论：**留到下一轮单独做**，并且届时应先写设计（尤其是"键变成 `路径+Generation` 之后，
`DeleteCachedThumbnailsForSource` 与缓存清扫分别怎么办"），再动代码。

---

## 27. 阶段 5 设计：合并缩略图双路（§26 要求先写的设计）

§26 记了"本轮不做"以及三条理由。本轮接着做，但**理由要先被检验**——
侦察之后发现 §26 的三条理由里有一条被高估、另外两条被低估，而且**存在一个真实的陈旧图标 bug**。
本节先把事实摆清，再给设计。

### 27.1 先说结论：这不是"一致性清理"，它修一个真实 bug

`ThumbnailService` 的磁盘键只有源路径，所以**同一路径换了内容，缩略图永远不会更新**。
现在有两条界面路径会走到这个坑：

| 界面路径 | 它做了什么 | 结果 |
|---|---|---|
| `LibraryPageViewModel.UpdateIconAsync`（库页右键"更新图标"，第 216–239 行） | 把新图复制成 `icon{原扩展名}`，`AddAsync`，然后 `QueueThumbnailRefresh([guid])` | 扩展名没变 ⇒ 路径没变 ⇒ 磁盘键没变 ⇒ `EnsureThumbnailAsync` 看到 `File.Exists` 直接返回 `false` ⇒ **卡片仍显示旧图** |
| `ModDetailsPageViewModel` 更新图像（第 290–328 行） | `InvalidateNodeAsync` + `RequestThumbnailAsync(requireFresh: true)` + `EnsureThumbnailAsync(…, 72)` | 事实确实是新的，但磁盘键仍是旧路径 ⇒ 同上 ⇒ 卡片仍显示旧图 |

第二条路径的代码里还留着一句乐观的注释：

> 图像已经保存；缩略图失败时由下次库页刷新重试。

它不成立：下次库页刷新算出的仍然是同一个路径键，`File.Exists` 仍然为真，**永远不会重试成功**。
用户看到的现象是"我换了图标，卡片没变"，而且重启也不恢复——
只有走 `ModLibraryService` 的"替换 Mod 文件"流程（那里会显式删缓存）才会恢复。

所以阶段 5 的收益不只是"只剩一套协议"，还包括**修掉一个用户可见且不可自愈的 bug**。
§26.4 说它"与用户当前的关注点无关"，这一点判断错了。

### 27.2 侦察结论：今天其实有**两个不同的** generation 字符串

这是 §26 没有查清、也是设计必须先把住的地方。同一个节点的缩略图 generation，在两层各算一次，
**算法不同**：

| | 生产者 `ModThumbnailProducer.ProduceAsync` | 中心 `ModInformationCenter.ComputeThumbnailGeneration` |
|---|---|---|
| 有图标 | `SHA256("{nodeId:N}{文件名}:{长度}:{mtimeTicks}")` | `SHA256("{nodeId:N}\|{文件名小写}:{长度}:{mtimeTicks}")` |
| 无图标 | `SHA256("{nodeId:N}missing")` | `ComputeNodeGeneration(node, root)`（**整个 Mod 目录**的哈希） |

两处差别：少一个 `\|` 分隔符、少一次 `ToLowerInvariant()`、无图标时一个是常量哨兵一个是目录哈希。

**那么哪个算数？** 查 `RequestThumbnailAsync`（第 239–262 行）：

```csharp
var generation = request.Generation ?? ComputeThumbnailGeneration(node, modsRootDirectory);
var effectiveRequest = request with { Generation = generation };   // 从此不再为 null
```

于是 `ProduceThumbnailAndRemoveAsync` 里的 `request.Generation ?? data.Generation`
（第 927、928 行）**永远取左边**。结论：

- 事实缓存键、`ModInformationResult.Generation` 用的都是**中心那一份**；
- 生产者算出来的那一份只活在返回对象 `ModThumbnailFacts.Generation` 字段里，
  而**生产代码里没有任何地方读它**（界面只取 `facts.SourcePath`）。

也就是说：生产者那份 generation 目前是**装饰性的**。这既是"双路"的残留，
也意味着**统一它们不会改变任何现有行为**——这一点对下面的设计很关键（改动是零行为风险的）。

### 27.3 设计：把"指纹"抽成唯一定义，两处缓存都用它

新增一个 Core 侧的唯一定义（`HD2ModCore/Infrastructure/ThumbnailFingerprint.cs`）：

```csharp
public static class ThumbnailFingerprint
{
    public const string Missing = "missing";

    // 源图像的内容指纹：绝对路径 + 长度 + 修改时间。文件不存在时返回 Missing。
    public static string OfSource(string? sourcePath);

    // 节点级指纹 = nodeId 命名空间 + 源指纹。供事实缓存使用。
    public static string OfNode(ModNodeId nodeId, string? sourcePath);
}
```

然后三个消费者都改成调它：

| 消费者 | 改成 |
|---|---|
| `ModThumbnailProducer` | `generation = ThumbnailFingerprint.OfNode(node.Id, source)` |
| `ModInformationCenter.ComputeThumbnailGeneration` | `=> ThumbnailFingerprint.OfNode(node.Id, ModIconLocator.TryResolve(Path.Combine(root, node.RelativePath)))` |
| `ThumbnailService.GetThumbPath` | 磁盘键 = `ThumbnailFingerprint.OfSource(path)`（+ `_{size}.png`） |

两个副作用，都要显式记账：

1. **事实缓存键的形状变了**（`OfNode` 里内层是"源指纹的哈希"而不是裸字符串，
   且无图标分支从"目录哈希"变成常量 `missing`）。
   这是一次性的缓存未命中，自愈；而且无图标分支变成常量是**修正**：
   图标不存在时，无关文件的改动不该让缩略图事实失效。
2. **磁盘键变了** ⇒ 现存缩略图文件全部成为孤儿 ⇒ 见 27.4 与 27.5。

### 27.4 磁盘键变成"路径+内容"之后，删除路径与清扫分别怎么办

这是 §26.4 点名要先回答的问题。答案是**把两件事分开**：

> **失效协议**（"这份缩略图还算不算数"）只有一套：内容指纹。
> **垃圾回收**（"没人再引用的文件怎么删掉"）是另一件事，它不是失效协议，
> 因为它删错也不会让新鲜度判断出错，只影响磁盘占用。

具体：

- **`ModLibraryService` 第 867 行（替换 Mod 文件后删除）→ 删掉这个调用。**
  换了内容 ⇒ 指纹变了 ⇒ 新键必然指向另一个文件 ⇒ 旧文件自动失效。
  显式删除在这里已经**多余**，而且它是"第二套协议"的来源。
- **`ModLibraryService` 第 1040 行（删除 Mod 后删除）→ 保留，但挪到物理删除之前。**
  原因：键要能从源路径算出来，就必须在源文件还在的时候算。
  现在的顺序是先 `DeleteNodesWithResultAsync(deleteStoredFiles: true)` 再删缓存，
  此刻源文件已经没了，`OfSource` 会返回 `Missing`，**什么都删不掉**。
  把这段循环挪到第 980 行（已经捕获了 `thumbnailPaths`）之后、第 981 行之前即可。
  这是"尽早回收"，不是必需的协议：即使它漏删（例如图标在这之前已被替换过），
  漏掉的文件也只是孤儿，由下面的清扫兜底。
- **新增一次性清扫**（`ThumbnailService.SweepAsync`）：见 27.5，它同时承担"旧键迁移"。

### 27.5 升级首启不该重生成整库缩略图

键变了，如果什么都不做，升级后第一次打开库页会把全库缩略图重生成一遍。
库页的缩略图管道刚刚优化过，不该在发版前引入这样一次可感知的退化。

清扫因此做两件事，一趟完成：

1. **旧键迁移**：对每个"当前库里的图标路径"，
   如果新键文件不存在、旧键文件存在、且**旧缩略图的修改时间 ≥ 源图像的修改时间**，
   就把旧键文件 `File.Move` 到新键。
   "旧缩略图比源图新"恰好等价于"这份缩略图代表的正是当前内容"，所以改名是安全的；
   源图比缩略图新的情况（换过图但没删缓存）会被跳过，落到第 2 步当孤儿删掉。
   这是 `File.Move`，不是重新解码，成本是每 Mod 两次 stat。
2. **无主文件清扫**：枚举 `cache/thumbs/*.png`，凡是不在"当前库 × 尺寸"应得文件名集合里的，
   且**修改时间早于本次清扫开始时刻**的，删除。
   加"早于开始时刻"这一条是为了不和并发进行的缩略图写入抢文件。

两者都**幂等且与顺序无关**：即使库页刷新抢在前面把新键文件生成出来了，
清扫也只会把旧键文件当孤儿删掉——正确性不变，只是白生成一次。
所以清扫用 fire-and-forget 起跑即可，不需要和预热链精确排序。

### 27.6 顺手收口：`ThumbnailService` 里三个死方法

侦察时确认这三个公开方法**在生产代码里零调用点**（只有定义）：

- `PreGenerateAll(ModLibraryService, int[])`
- `EnsureThumbnailsAsync(IEnumerable<string?>, int, …)`（按路径的那个重载）
- `RegenerateThumbnailAsync(string?, int, …)`

它们是旧管道的遗留物，正好属于"合并双路"要收掉的东西，删除。

### 27.7 验收

| 验收项 | 判据 |
|---|---|
| 只剩一套失效协议 | 全库只有一个地方定义缩略图指纹（`ThumbnailFingerprint`）；`ModInformationCenter` / `ModThumbnailProducer` / `ThumbnailService` 都调它 |
| `ThumbnailService` 降级为渲染器 | 磁盘键由源内容决定，服务内部不再有"要不要重算"的判断（除 27.5 的一次性清扫） |
| 换图标立即生效 | 同路径换内容 ⇒ 指纹变 ⇒ 键变 ⇒ 新缩略图被渲染并被卡片读到（27.1 的 bug 消失） |
| 显式删除只剩一处 | `DeleteCachedThumbnailsForSource` 调用点从 2 处降到 1 处（且是 GC 性质） |
| 不重生成整库 | 升级首启时，旧键文件在源图未变的前提下被改名复用 |

测试落在两处：`HD2ModCore.Tests`（指纹行为 + 单一定义/调用点的架构断言）
与 `HD2ModCli.Tests` 无关（CLI 不碰缩略图磁盘缓存）。

### 27.8 本轮仍然不做的事

- 不把 `Generation` 搬进 `ModCardViewModel`。**这是 §26.1 的结论被推翻的地方**：
  不需要搬，因为磁盘键可以由**源路径本身**算出来（`OfSource(path)` 自己做一次 stat）。
  于是 `GetExistingThumbnailPath` 的 7 处同步调用点**一行都不用改**，
  §26.1 说的"把一条同步绑定链改成异步取数"整个不成立。
- 不引入旁路索引，不改 `ModCardViewModel` / `ModCardViewModel`-等价类的字段。
- 不做量化计时（用户已明确"先不急着测提升速度"）。

### 27.9 实测

改动文件：3 新增 + 7 修改。

| 文件 | 改动 |
|---|---|
| `HD2ModCore/Infrastructure/ThumbnailFingerprint.cs` | **新增**，指纹的唯一定义 |
| `HD2ModCore.Tests/ThumbnailFingerprintTests.cs` | **新增**，6 条语义用例 |
| `HD2ModCore.Tests/ThumbnailPipelineArchitectureTests.cs` | **新增**，4 条架构断言 |
| `HD2ModCore/Infrastructure/ModThumbnailProducer.cs` | generation 改为委托；删掉自算哈希 |
| `HD2ModCore/Infrastructure/ModInformationCenter.cs` | `ComputeThumbnailGeneration` 改为委托；无图标分支不再退化成目录哈希 |
| `HD2ModManager/Services/ThumbnailService.cs` | 磁盘键 = 内容指纹；新增 `SweepAsync`；删 3 个死方法 + `CancelPendingGeneration` + `DeleteCachedThumbnailsForSource` |
| `HD2ModManager/Services/ModLibraryService.cs` | 删两处手工删缓存；新增 `SweepThumbnailCacheAsync` |
| `HD2ModManager/ViewModels/ShellViewModel.cs` | 启动维护之后起跑一次性清扫 |
| `HD2ModManager/ViewModels/LibraryPageViewModel.cs` | 删 `CancelPendingGeneration` 调用 |
| `HD2ModManager/ViewModels/ModDetailsPageViewModel.cs` | 删 `CancelPendingGeneration` 调用；`RegenerateThumbnailAsync(imagePath)` → `RenderThumbnailAfterImageChangeAsync()`（原参数没人用） |

结果：全量编译 **0 警告 / 0 错误**；测试 **880 通过 / 0 失败**
（Decryptor 5、Adaptation 178、**Core 546 → 556**、Cli 141）。
Core 的 +10 = 指纹语义 6 + 缩略图架构 4，与新增用例数吻合。

顺带的一个小收益：读路径不再调 `Directory.CreateDirectory`（旧 `GetThumbPath` 每次都会调），
每张卡片的同步取路径少一次系统调用；stat 次数与旧实现持平（`OfSource` 的那一次取代了原来的 `File.Exists`）。

### 27.10 五条探针（逐条证明断言真的在承重）

沿用本仓库既有的"先证伪自己"做法：故意破坏被保护的那一行，重跑，看它是否真的失败。

| 探针 | 破坏方式 | 观察到的失败 |
|---|---|---|
| A | 生产者改回自己算 `SHA256` | `ThumbnailGenerationHasExactlyOneDefinition` |
| B | 读路径改用 `LegacyDiskKey` | `ThumbnailDiskKeyIsTheSharedContentFingerprint` |
| C | `ModLibraryService` 里塞回一次 `DeleteCachedThumbnailsForSource` | `TheLegacyThumbnailPipelineIsGone` |
| D | 把清扫调用挪到 `RunStartupChecksAsync` **之前** | `ThumbnailCacheGarbageIsCollectedInOnePlace` |
| E | `OfSource` 去掉 `\|长度\|mtime`，退化成"只有路径" | `ReplacingTheIconAtTheSamePathChangesTheFingerprint` **和** `SourceLengthParticipatesInTheFingerprint` |

五条全部按预期失败（6 个失败，探针 E 触发两条），其余 6 条不受影响的用例仍通过；
还原后 12 条全绿。



---

## 28. E1 收尾：把 6 个业务服务的映射请求收口到中心（已完成）

> 实施时间：2026-10-11。状态：**代码已实施、已验证**。
> 起因：§20.8 把这件事列为"仍未做"，并在 §19.6 E1 的实测清单里点名了那 7 个调用点。
> 这一节是它的设计 + 实施记录。

### 28.1 改造前的实际形态（实测，不是照抄 §20.8）

6 个服务各自持有 `IGameDataMappingFactsService`，直调 `MapAsync`：

| 服务 | 调用点 | 用途 |
|---|---|---|
| `AdvancedModAssetQueryService` | 1 | 高级资产表的 Game Data 目标 |
| `MaterialDeliveryFactsService` | 1 | 判断材质是否在原版游戏数据里 |
| `ModAssetRoleFactsService` | 1 | 统计"有 Game Data 覆盖"的资产数，决定 Mod 角色 |
| `ModAssetSummaryProjector` | 1（私有 `MapAsync` 包装） | 摘要投影 + Unit 标签叠加 |
| `ProfileMaterialDiagnosticsService` | 2 | 有效 Unit 的材质/贴图是否可由 Game Data 提供 |
| `ProfileOverrideGraphService` | 1 | 严格 AssetKey 赢家与归档重叠 |

**关键实测发现**：这 6 个服务**已经持有 `IModInformationCenter`**，而且其中几个**已经在用中心**问别的事实
（例：`ProfileOverrideGraphService` 就调了 `_informationCenter.RequestAssetInventoryAsync`）。
只有"要映射"这一件事绕过了中心。中心的入口 `RequestGameDataMappingAsync` 也**早就存在**。
唯一没接中心的是 `ModAssetSummaryProjector`（`CoreServices` 直接 `new` 了映射服务）。

### 28.2 为什么这不是"少一次调用"，以及收益到底是什么

**缓存收益已经拿到了**：§20.3 给映射服务加的进程级缓存（`GameDataFactCache` → `SourceFingerprintMemo`）
是 **static** 的，其源码注释明说"6 个消费者各 new 一个实例，实例级缓存等于零共享"——
所以 6 家早已共享同一份缓存。收口**不会**再多省一次映射计算。

只有走中心才拿得到的四样（这才是本轮收益）：

1. **并发合并**：`SourceFingerprintMemo` 只有结果记忆，**没有"正在算"的门**（读了源码确认）——
   并发问同一批资产会各算一遍。中心的 `_gameDataTasks` + `Lazy<Task>` 才会合并成一次生产。
2. **可观测性**：直调**不记**任何 `ProductionStarted` / `DiagnosticRecorded`，
   所以这 6 条路的命中率与生产耗时今天完全看不见——正是 §7 护栏 4 说的"该往哪细分由数据决定"所缺的数据。
3. **尊重调用方声明的缓存作用域**：`MapAsync` 的签名里**根本没有 scope 参数**，
   所以一个声明 `CacheScope.None` 的 CLI 只读操作照样会读写进程缓存。走中心才会按声明处理。
4. **取消跟随中心关闭**，而不是各自传一个 `CancellationToken`。

### 28.3 必须先回答的语义问题：`Data` 可能为 `null`

中心把"数据"与"诊断"分开表达：`ModInformationResult<T>.Data` 是 `T?`，
为 `null` 表示"索引还没建"或"生产失败"，原因在 `Issues` 里。

而改造前各服务直调 `MapAsync` 时，**绝大多数失败已被 `MapAsync` 内部吞成"空映射 + issues"**
（`FindAssetArchivesAsync` 抛错 → `matches = 空` + 一条 Error issue），只有灾难性失败才抛出来。

所以不能简单地"把返回值换成 `result.Data`"——那会把 6 个调用点同时变成可能拿到 `null`。
统一收敛在一处：

```csharp
// HD2ModCore/Infrastructure/GameDataMappingResultExtensions.cs
internal static GameDataMappingFacts AsMappingFacts(this ModInformationResult<GameDataMappingFacts> result)
    => result.Data ?? new GameDataMappingFacts(
        result.Generation ?? UnavailableGeneration, UnavailableGeneration, UnavailableGeneration,
        DateTimeOffset.UtcNow, new Dictionary<AssetKey, GameDataMappedAssetFact>(), result.Issues);
```

**一处已知的语义让步**（写下来而不是藏起来）：`Data` 为 `null` 时 `MappingGeneration` 只能是
`"unavailable"` 这个哨兵值，而它是**稳定**的，不等于"映射没变"。
`ModLibraryService` 会把 `ModAssetSummaryProjection.MappingGeneration` 当作"要不要复用上次摘要"的依据，
所以在"映射生产灾难性失败"这个窄场景下，它可能复用上一次的摘要而不是重建。
这不比改造前更糟（改造前那次重建同样拿不到映射），但口径确实变了，故记在这里。

### 28.4 设计选择：让"不该看见"变成"看不见"

把 `IGameDataMappingFactsService` 从这 6 个服务的构造函数里**整个删掉**，
而不是"留着字段但不调用"。理由：

- 与 §19.8 给 Manager 立的先例一致——"Manager 连类型都不该看见"。
  留着字段等于把"下次顺手直调一下"的成本压到一行；
- 留着字段会让字段变成"已赋值但从不读取"，在 `TreatWarningsAsErrors` 下本来就过不去。

代价是**测试替身必须改写**：这些用例过去靠 `new StubMappingService(...)` / `ThrowingMappingService` /
`EmptyMappingService` / `FakeMappingFactsService` 注入假映射。现在统一改成给 `FakeInformationCenter`
注入一个"键集合 → 映射事实"的委托（`GameDataMapping` 属性）。

**"未注入"这一档的语义**：生产中心在"索引没建 / 生产失败"时返回 `Data = null`，调用方必须能容忍，
所以替身未注入时**返回 `Data = null` 而不是抛异常**。
`MaterialDeliveryFactsServiceTests` 里那条"关掉映射时一次都不该问中心"的用例，
改用"注入一个一被调用就抛的委托"来表达——语义反而比原来更准确。

### 28.5 改动清单

| 文件 | 改动 |
|---|---|
| `Infrastructure/GameDataMappingResultExtensions.cs` | **新增**：`AsMappingFacts()` 统一收敛 `Data = null` |
| `Infrastructure/AdvancedModAssetQueryService.cs` | 删 `IGameDataMappingFactsService` 字段与构造参数；改走中心 |
| `Infrastructure/MaterialDeliveryFactsService.cs` | 同上；`includeGameDataMapping` 成为唯一的映射开关（原来还有 `mappingService is null` 这一档） |
| `Infrastructure/ModAssetRoleFactsService.cs` | 同上；保留原有的 try/catch 兜底 |
| `Infrastructure/ModAssetSummaryProjector.cs` | 构造参数换成 `IModInformationCenter`；私有 `MapAsync` 更名 `BuildMappingAsync`（它不只映射，还叠加 Unit 标签，且新名字避免与护栏判据混淆） |
| `Infrastructure/ProfileMaterialDiagnosticsService.cs` | 删字段与参数；**2 处**改走中心 |
| `Infrastructure/ProfileOverrideGraphService.cs` | 同上 |
| `Infrastructure/CoreServices.cs` | 6 个工厂表达式随之简化；`CreateModAssetSummaryProjector` 增加 `IModInformationCenter` 参数 |
| `HD2ModManager/Services/ModLibraryService.cs` | 投影器工厂调用补上 `_informationCenter` |
| `HD2ModCore.Tests/FakeInformationCenter.cs` | 新增 `GameDataMapping` 注入点并实现 `RequestGameDataMappingAsync` |
| `HD2ModCore.Tests/*Tests.cs`（5 个） | 假映射服务 → 委托注入；`ThrowingMappingService` / `EmptyMappingService` / `MappingService` / `DependencyMappingService` / `FakeMappingFactsService` 全部删除或改为纯函数 |

**一处顺带发现的既有怪味**：`MaterialDeliveryFactsService.cs` 历史上**没有 namespace 声明**，
类型落在全局命名空间里，所以新引入的扩展方法必须显式 `using HD2ModCore.Infrastructure;` 才可见。
本轮没有顺手把它搬进命名空间——那会动一个公开类型的全名，属于另一件事。

### 28.6 新护栏：`GameDataMappingIsRequestedThroughTheCenterOnly`

两条断言，缺一不可：

1. **直调**：四个生产项目里 `X.MapAsync(...)` 只允许出现在
   `ModInformationCenter.cs`（唯一请求入口）与 `DirectGameDataFactSource.cs`（存在的意义就是绕过中心，§25.2）；
2. **持有**：`IGameDataMappingFactsService` 这个类型名只允许出现在定义、实现、装配点、
   中心、以及两个事实源适配器（共 6 个文件）。

第 2 条不是多余的：只要某个服务重新持有该接口，"下次顺手直调一下"就只有一步之遥。

**判据用 `\.MapAsync\s*\(` 而不是裸 `MapAsync`**：仓里还有 `TryBuildChunkMapAsync` /
`BuildSingleBundleChunkOffsetMapAsync` 这类"同名尾巴"的方法，裸串会误伤。
这条正则实测在全仓四个生产项目里**恰好**命中那 2 个正当调用点。

与既有同类护栏一致的**已知脆性**：这是源码文本断言，"在注释里写出 `X.MapAsync(`"也会让它失败。
代价可接受——要绕过它必须明确改测试（§25.2 已就同一口径说明过）。

### 28.7 验收

| 项目 | 结果 |
|---|---|
| 全量编译（Debug） | **0 警告 / 0 错误** |
| 全量编译（**Release**） | **0 警告 / 0 错误**（`TreatWarningsAsErrors` 已开，Release 必须单独验一遍） |
| 全量测试 | **881 通过 / 0 失败**（Decryptor 5、Adaptation 178、**Core 556 → 557**、Cli 141） |
| 生产代码里的直调点 | `\.MapAsync\s*\(` 全仓**恰好 2 处**：`ModInformationCenter.cs` 与 `DirectGameDataFactSource.cs` |
| 持有接口的文件 | **恰好 6 个**：定义、实现、装配点、中心、两个事实源适配器 |

Core 的 +1 就是新护栏 `GameDataMappingIsRequestedThroughTheCenterOnly`。

**行为等价性**：本轮没有新增或删除任何测试用例的断言，只有替身注入方式的改写；
全量 881 条在改造前后语义不变（改造前 880 条 + 新增 1 条护栏）。

**一次自己踩的坑（记下来免得再踩）**：为了校验发布包，我把两个临时 Python 脚本写进了 `_diagnostics/`。
`DiagnosticsPatchSampleTests` 是**条件测试**——`_diagnostics` 目录不存在就跳过。我创建了这个空目录，
于是两条用例从"跳过"变成"真跑"，然后因为拿不到真实样本而失败（2 个失败）。
`stat` 显示该目录的 **Birth = 本次会话的写入时刻**，坐实了是我创建的；`rmdir` 删掉空目录即完全复原。
**结论：临时脚本放 `tmp/`（同样被 gitignore），不要放 `_diagnostics/`。**

### 28.8 两条探针（证明两条断言各自承重）

| 探针 | 破坏方式 | 观察到的失败 |
|---|---|---|
| 1 | 在 `ProfileOverrideGraphService` 上恢复"持有接口 + 直调"的完整旧形态（用**可选参数**保证测试项目仍可编译） | `GameDataMappingIsRequestedThroughTheCenterOnly` 报出 **2 条**违规，都指向该文件——一条"直调"、一条"持有" |
| 2 | 只在 `ModAssetRoleFactsService` 里加一句 `_ = typeof(IGameDataMappingFactsService).Name;`（只引用类型、不调用） | 同一条用例报出**恰好 1 条**违规（"持有"） |

探针 1 证明两条断言在真实回归形态下都会响；探针 2 证明第 2 条不是第 1 条的附庸。
两条均以 `grep -rn 'PROBE-'` 确认为 0 处残留，还原后直调点与接口引用都回到白名单内的精确形态。

### 28.9 本轮仍然不做的事

| 项 | 说明 |
|---|---|
| **E2 · 分层归位** | 消费 `FactDescriptor.CacheScope` / `FactProducerKind`，把四类节点真正落到代码里。**不建议现在做**：它会改所有读取路径的缓存层选择（刚优化过的热路径），且有一个未拍板的设计问题——请求的 scope 与登记表的 scope 如何合成（取较小值？超界报错？）。收益是结构性的，用户无感。 |
| **`GameDataMapping` 落盘** | 它声明 `Persistent` 但实际只有进程内记忆。**不是 bug**：`CacheScope` 是"最多允许"的上限。要"做"等于给 game-data 侧新建一套磁盘持久层，直接撞"缓存不能取代真相"这条红线。 |
| **量化验收** | 用户已明确"先不急着测提升速度"。本轮的四项收益（并发合并、可观测性、尊重 scope、取消生命周期）**都没有留下可复现的数字**——若要写进说明，需要一次带计时的实测。 |
| **`ModAssetSummaryProjector` 的 `"unavailable"` 代** | §28.3 记的语义让步：映射生产灾难性失败时 `MappingGeneration` 变成稳定哨兵值，可能让 `ModLibraryService` 复用上一次摘要。要不要给它一个"每次不同"的失败代，需要单独判断。 |

### 28.10 一句话总结

把 6 个业务服务的映射请求收口到信息中心，**收益不是"少算一次"**（缓存早在 §20.3 就共享了），
而是拿回中心才有的四样：并发合并、按 FactKey 的生产诊断、尊重调用方声明的缓存作用域、取消跟随中心关闭；
做法不是"留着依赖但不调用"，而是**把依赖整个删掉**，让"下次顺手直调一下"在类型层面就做不到，
再用一条源码文本护栏把这条缝钉死（两条断言、各有一条探针证明其承重）。
**零行为变化、零新增警告、881 测试全绿、Release 亦绿。**
