# 派生缓存迁移基线（阶段 0）

> 审计日期：2026-07-24。范围：派生信息、分析缓存、信息中心绕过路径、缩略图缓存，以及 Manager 导入/派生状态编排。阶段 0 仅记录基线；未修改业务代码，未删除旧实现。

## 1. 实际实现与入口

| 文件 | 实际方法/职责 | 基线分类 |
|---|---|---|
| [HD2ModCore/Infrastructure/CoreServices.cs](../../HD2ModCore/Infrastructure/CoreServices.cs) | `CreateModInformationCenter(StoragePaths)` 组装 `ModInformationCenter`、`JsonModFileFactsCache`、`JsonModInformationCache`、各类 producer 和 `ModDataIndex`；多个 `Create*Service` 默认重新调用该工厂 | 待迁移：重复组装是绕过共享中心的主要风险；工厂本身必须保留 |
| [HD2ModCore/Infrastructure/ModInformationCenter.cs](../../HD2ModCore/Infrastructure/ModInformationCenter.cs) | `RequestAssetInventoryAsync`、`RequestReferenceGraphAsync`、`RequestAdvancedUnitAnalysisAsync`、`RequestThumbnailAsync`、`RequestUnitVersionAsync`、`RequestMaintenanceAnalysisAsync`；按 generation 合并进行中任务，并通过 `IModInformationCache` 读写持久缓存；`InvalidateNodeAsync` 清理任务、缓存和 `IModDataIndex` | 必须保留：当前统一信息产品边界和失效语义 |
| [HD2ModCore/Application/IModInformationCache.cs](../../HD2ModCore/Application/IModInformationCache.cs) | 非 FileFacts 信息产品的通用 `TryLoadAsync`/`TryLoadLatestAsync`/`SaveAsync`/节点删除接口 | 必须保留：统一缓存协议 |
| [HD2ModCore/Infrastructure/JsonModInformationCache.cs](../../HD2ModCore/Infrastructure/JsonModInformationCache.cs) | `IModInformationCache` 的 JSON 持久实现 | 待迁移：需纳入统一派生缓存生命周期，但阶段 0 不删除 |
| [HD2ModCore/Application/IPatchGroupAnalysisCacheStore.cs](../../HD2ModCore/Application/IPatchGroupAnalysisCacheStore.cs) | `CachedPatchGroupAnalysisProvider` 使用的 patch 分组分析缓存读写接口 | 暂不能删除：仍被 provider、诊断服务和旧查询路径使用 |
| [HD2ModCore/Application/IAdvancedModAnalysisService.cs](../../HD2ModCore/Application/IAdvancedModAnalysisService.cs) | `IAdvancedModAnalysisCacheStore` 的高级分析读写接口；与 `IAdvancedModAnalysisService` 并列定义 | 暂不能删除：高级详情/材料交付仍有直接读取需求 |
| [HD2ModCore/Infrastructure/CachedPatchGroupAnalysisProvider.cs](../../HD2ModCore/Infrastructure/CachedPatchGroupAnalysisProvider.cs) | `AnalyzeNodeAsync` 先按节点读取 `IPatchGroupAnalysisCacheStore`，校验版本、路径、AnalyzerVersion、源文件 fingerprints；未命中后调用 inner analyzer 并保存 | 待迁移：这是独立于信息中心的派生缓存绕过点；缓存校验逻辑必须先被等价承接 |
| [HD2ModCore/Infrastructure/SqliteModFactsStore.cs](../../HD2ModCore/Infrastructure/SqliteModFactsStore.cs) | 同时实现 `IPatchGroupAnalysisCacheStore`、`IAdvancedModAnalysisCacheStore`、`IReferenceGraphQueryIndex`、清理接口；保存 patch assets/references、普通快照和 advanced 快照 | 暂不能删除：仍是 SQLite 查询及旧缓存的实际存储；迁移前需拆分职责/验证数据兼容性 |
| [HD2ModCore/Infrastructure/AdvancedModAssetQueryService.cs](../../HD2ModCore/Infrastructure/AdvancedModAssetQueryService.cs) | 新构造函数走 `IModInformationCenter.RequestAdvancedUnitAnalysisAsync`；旧构造函数将 `IReferenceGraphQueryIndex` 强转为两个 cache store，`LoadAnalysisAsync` 直接 `TryLoadAdvancedAsync`，失败再 `TryLoadAsync` | 待迁移（旧构造函数）；暂不能删除（兼容构造和数据回退仍存在） |
| [HD2ModCore/Infrastructure/MaterialDeliveryFactsService.cs](../../HD2ModCore/Infrastructure/MaterialDeliveryFactsService.cs) | 无信息中心时直接通过 `IAdvancedModAnalysisCacheStore.TryLoadAdvancedAsync`；有信息中心时走高级分析服务/中心 | 待迁移：保留兼容回退，完成调用方收敛后再评估删除 |
| [HD2ModCore/Infrastructure/ProfileMaterialDiagnosticsService.cs](../../HD2ModCore/Infrastructure/ProfileMaterialDiagnosticsService.cs) | 先直接 `factsStore.TryLoadAsync(node.Id)`；未命中再调用 `analysisProvider.AnalyzeNodeAsync` | 待迁移：诊断仍绕过统一中心，并可能触发独立分析 |
| [HD2ModCore/Infrastructure/ModContentFactsService.cs](../../HD2ModCore/Infrastructure/ModContentFactsService.cs) | 通过注入的 `IPatchGroupAnalysisProvider.AnalyzeNodeAsync` 生成资产事实 | 待迁移：需确认 provider 是否切换到统一缓存/信息中心，不能直接删除 provider |
| [HD2ModManager/Services/DerivedStateCoordinator.cs](../../HD2ModManager/Services/DerivedStateCoordinator.cs) | 持有共享 `IModInformationCenter`；`RefreshCoreAsync` 根据 content/expected/deployed dirty 状态刷新；`GetAssetInventoryAsync` 对 active profile 节点调用 `RequestAssetInventoryAsync`；监听 Profile 和 ModContentFacts 变化 | 必须保留：当前 Manager 的共享投影和 generation/dirty 编排核心 |
| [HD2ModManager/Services/ImportService.cs](../../HD2ModManager/Services/ImportService.cs) | 可注入中心，否则自行 `CreateModInformationCenter`；导入后 `ReplaceSnapshot(..., buildDerivedData:false)`，`AnalyzeImportedAsync` 直接请求 `AssetInventory` | 待迁移：默认自建中心会造成实例分裂；应由上层共享 coordinator/center，阶段 0 不改行为 |
| [HD2ModManager/Services/ThumbnailService.cs](../../HD2ModManager/Services/ThumbnailService.cs) | 静态文件系统缩略图缓存：`EnsureThumbnailsAsync`、`RegenerateThumbnailAsync`、`GetExistingThumbnailPath`、`DeleteCachedThumbnailsForSource`；缓存目录为程序目录 `cache/thumbs`，key 为原图绝对路径 SHA-256+尺寸 | 待迁移：与 `IModInformationCache`/中心的 `RequestThumbnailAsync` 并存；静态缓存没有 generation/节点失效协议 |

## 2. 已确认调用链

### 2.1 信息中心主链

- `ShellViewModel` 创建一个中心并注入 `DerivedStateCoordinator`。
- `DerivedStateCoordinator.RefreshAsync` → `RefreshCoreAsync` → `GetAssetInventoryAsync` → `IModInformationCenter.RequestAssetInventoryAsync` → `ModInformationCenter` 的 generation 缓存/并发合并 → `JsonModInformationCache`（命中）或 `ModContentFactsService` → `IPatchGroupAnalysisProvider.AnalyzeNodeAsync`。
- `ModInformationCenter` 还统一承载 ReferenceGraph、MaintenanceAnalysis、UnitVersion、AdvancedUnitAnalysis、Thumbnail 的缓存读写；`InvalidateNodeAsync` 负责节点级清理。

### 2.2 分析 provider 绕过链

- `ModContentFactsService`、`ReferenceGraphProducer`、`UnitVersionInformationProducer`、`AdvancedUnitAnalysisProducer`、`AssetKeySetProvider` 均直接依赖 `IPatchGroupAnalysisProvider.AnalyzeNodeAsync`。
- `CachedPatchGroupAnalysisProvider.AnalyzeNodeAsync` → `IPatchGroupAnalysisCacheStore.TryLoadAsync` → 未命中 `AdaptationPatchGroupAnalysisProvider.AnalyzeNodeAsync` → `SaveAsync`。
- `ProfileMaterialDiagnosticsService` 的 `factsStore.TryLoadAsync` 和 provider fallback、`AdvancedModAssetQueryService` 旧构造的两个 store fallback、`MaterialDeliveryFactsService` 无中心 fallback，均是需要迁移审计的独立读取路径。

### 2.3 Manager 侧绕过/并存链

- `CoreServices.CreateModInformationCenter` 在多个 `Create*Service` 默认工厂中重复调用；如果上层不使用带 center 的重载，会产生多个中心、多个内存 in-flight 字典和多个 `ModDataIndex`。
- `ImportService` 默认自行创建中心；`ShellViewModel` 的实际导入队列已注入 `_derivedState.InformationCenter`，但其他直接构造仍需持续审计。
- `ThumbnailService` 被 `LibraryPageViewModel`、`ProfilePageViewModel`、`ModDetailsPageViewModel`、`ModLibraryService` 直接调用；详情页另外调用 `IModInformationCenter.RequestThumbnailAsync`，形成两套缩略图缓存。
- `ModDetailsPageViewModel` 直接调用注入的 dependency/full provider 的 `AnalyzeNodeAsync`（依赖图和 Full 两处），属于高级详情按需分析路径，不应在没有等价缓存验证前删除。

## 3. 分类结论与风险

### 必须保留

1. `IModInformationCenter` 及 `ModInformationCenter` 的 request、generation、coalescing、取消和节点失效语义。
2. `IModInformationCache` 协议及现有数据读取能力，直到所有产品确认迁移并完成旧数据兼容验证。
3. `DerivedStateCoordinator` 的共享快照、dirty 标记和 Profile/内容变化监听。
4. `SqliteModFactsStore` 的查询能力和现有清理能力；其多接口实现暂不能直接删。
5. `AnalyzeNodeAsync` 的真实分析 provider 作为最终计算能力，以及高级详情按需计算能力。

### 待迁移

1. `CreateModInformationCenter` 的重复默认组装：优先在应用组合根统一实例，再逐步移除业务层默认创建。
2. `CachedPatchGroupAnalysisProvider`、`ProfileMaterialDiagnosticsService`、旧版 `AdvancedModAssetQueryService` 构造/回退、`MaterialDeliveryFactsService` 无中心回退：统一改为由中心或明确的单一派生缓存门面读取。
3. `ThumbnailService` 与中心 Thumbnail 产品的双缓存：先定义唯一 generation、失效和文件落盘策略，再合并调用。
4. `ImportService.AnalyzeImportedAsync` 与 `DerivedStateCoordinator` 的刷新触发关系：导入完成后应避免重复分析/重复刷新，但阶段 0 不改变顺序。

### 暂不能删除

- 任一旧接口、SQLite 表/字段、兼容构造函数和 fallback 分支。
- `IPatchGroupAnalysisProvider.AnalyzeNodeAsync` 及其 Full/DependencyGraph/Inventory 深度差异。
- `SqliteModFactsStore` 的普通与 advanced 快照读写，直到迁移后有数据迁移和回滚方案。
- 静态 `ThumbnailService`，直到所有 UI 使用方、删除清理和重生成行为均有等价实现。

主要风险：中心实例分裂导致缓存命中率和失效不一致；同一节点同时生成不同深度分析；旧 SQLite 快照与新 generation 语义不一致；缩略图按绝对路径 hash 导致移动/替换文件不自然失效；导入通知、中心失效和 coordinator 刷新存在重复工作或竞态；删除旧 fallback 可能破坏未走新构造函数的测试/调用方。

## 4. 阶段 0 验收清单

- [x] 实际搜索并审计 `CreateModInformationCenter`、`IModInformationCache`、`IPatchGroupAnalysisCacheStore`、`IAdvancedModAnalysisCacheStore`、`AnalyzeNodeAsync`、`ThumbnailService`、`ImportService`、`DerivedStateCoordinator`。
- [x] 记录核心接口、实现、具体方法和调用链，区分统一路径与绕过路径。
- [x] 明确必须保留、待迁移、暂不能删除项及迁移风险。
- [x] 未修改业务代码，未删除旧实现。
- [ ] 后续阶段为每个待迁移路径确定唯一 owner、generation 来源、失效事件和回滚策略。
- [ ] 后续阶段增加缓存命中/未命中、重复分析、中心实例数和缩略图双写的可观测性。
- [ ] 后续阶段完成迁移后的全量构建、核心缓存/导入/缩略图测试和旧 SQLite 数据回归验证。
