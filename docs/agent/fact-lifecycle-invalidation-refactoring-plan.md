# Manager 事实生命周期与失效条件重构方案

**日期：** 2026-07-16  
**状态：** 已完成代码考察，待按阶段实施  
**范围：** `HD2ModManager`、`HD2ModCore` 的事实缓存、派生投影、页面按需计算与 same-AssetKey 候选生成。  
**不在本轮范围：** 改变已游戏验证的 target-shell、骨骼转码或材质拆分二进制写出语义。

---

## 1. 目标与非目标

### 1.1 目标

将当前“页面打开即启动分析”与“任意内容变化即粗粒度刷新”的模式，重构为统一的：

> **事实来源 + 输入指纹 + 失效事件 + 刷新策略 + 消费方式**

每一项数据必须能回答：

1. 它是原始事实、持久化派生事实、会话投影，还是一次性操作结果？
2. 它依赖哪些具体输入？
3. 哪些事件会使它失效？
4. 失效后是自动后台重建、按需读取、用户点击触发，还是永不自动重算？
5. 是否可持久化，持久化记录的 schema/analyzer 版本和 fingerprint 是什么？
6. 页面是否允许直接读取原始 Patch/Game Data；若允许，理由是什么？

本轮完成后，普通 Mod 详情只读取已存在的轻量事实与当前 Profile 投影；它不得因为被打开而扫描大 Patch、读取 Unit GPU 数据，或发起 same-key 完整计划。

### 1.2 非目标

- 不将内部回读、序列化成功误称为游戏正确性证明。
- 不为重建候选增加“自己生成、自己判定可用”的语义预检。
- 不把所有事实简单分成永久/临时三级；失效由依赖事件组合定义。
- 不以每天一次作为 Mod 内容事实的主失效机制。Mod 内容应该由文件 fingerprint 精确驱动；“每日检查”只适合低成本的外部输入检查调度。

---

## 2. 已确认的现状

### 2.1 当前事实与缓存基础

| 项目 | 现有实现 | 当前有效性机制 | 结论 |
|---|---|---|---|
| Mod Patch 资产与引用边 | `SqliteModFactsStore` | `CachedPatchGroupAnalysisProvider` 对 base/stream/gpu 文件的路径、长度、最后写入时间及 analyzer 版本比较 | 已是正确的持久化正向事实基础 |
| Mod 内容 generation | `ModContentFactsService` | 当前 Patch group 文件名、长度、写入时间的 SHA-256 | 已存在，可成为统一依赖版本 |
| Mod 反向引用 | `asset_references` + `ix_asset_refs_target` | 保存正向事实时替换单个 node 的所有边；查询时按 target AssetKey | 已可按 AssetKey 查询，不必再次扫描 Mod |
| Game Data AssetKey → Archive | `AssetArchiveIndexService` | Game Data + `archivehashes.json` source fingerprint | 已存在且能识别游戏更新后的 stale |
| Game Data 元数据映射 | `GameDataMappingFactsService` | index fingerprint + 元数据文件 fingerprint | 已有 generation，但没有统一事件传播 |
| 活动 Profile 覆盖图 | `ProfileOverrideGraphService` | Profile revision + active Mod content generation + mapping generation | generation 设计正确，但构建时重复读取 content facts |
| Profile 材质诊断 | `ProfileMaterialDiagnosticsService` | Profile 变化时由 `DerivedStateCoordinator` 重建 | 已优先读 SQLite facts，不再重复 Patch 二进制分析 |
| 已部署实际图 | `DeployedOverrideGraphService` | Data 文件集合与 activation state 的 generation | 正确属于部署状态，而不是 Mod 内容事实 |
| same-key 输出报告 | `ModSameKeyReconstructionService` | 输出目录下 JSON/Markdown/checklist | 应保留为操作审计和外部测试证据，不进入普通派生链 |

### 2.2 已确认的性能/生命周期问题

1. `ModDetailsPageViewModel` 构造时同时启动：
   - `RefreshMaterialPackagingStateAsync()`；
   - `RefreshMaterialDeliveryFactsAsync()`；
   - `RefreshSameKeyReconstructionStateAsync()`；
   - `RefreshAdvancedAssetsAsync()`。

2. `DerivedStateCoordinator.SnapshotChanged` 后，详情页又启动高级资产查询和 same-key 完整计划；普通详情打开不需要这些任务。

3. 材质打包状态的 `MaterialPackagingApplicationService.InspectAsync()` 先调用底层 `InspectAsync()`，再调用 `PlanSplitAsync()`；而 `PlanSplitAsync()` 会再次调用 `InspectAsync()`。仅为按钮状态，当前会对同一 Patch 做至少两轮完整分析。

4. same-key 的 `InspectAsync()` 会检查 Game Data index、扫描源 Patch、读取每个 source Unit、读取 current target Unit，并运行 `UnitMeshAdaptationPlanner.BuildPlan()`；后者会 retarget/minify 并尝试序列化。它是重型“完整候选规划”，不应被普通详情或页面刷新自动调用。

5. `PageViewModel` 本身不实现 `IDisposable`；`ShellViewModel` 虽会尝试释放旧页面，但 `ModDetailsPageViewModel` 没有取消任务，也没有解除 `_derivedState.SnapshotChanged` 订阅。因此反复打开详情可能遗留订阅和并发任务。

6. `DerivedStateCoordinator` 只维护 `content/expected/deployed` 三个粗粒度 dirty flag；并且 `contentDirty` 时只重新取活动 Profile 的内容 facts。它适合当前 Profile 投影，但不是整个库事实调度器。

7. `ModLibraryService.RefreshDerivedDataAsync()` 虽支持传入新增 Mod 子集，导入流程也正确使用该能力，但启动 `RestoreStableLibraryProjectionAsync()` 仍请求全库投影；所幸 Patch 分析 provider 有持久化 fingerprint 缓存，代价主要是逐 node 枚举/投影，而不是必然完整二进制扫描。

8. `SqliteModFactsStore.OpenAsync()` 当前每次打开连接都会执行 `DROP TABLE IF EXISTS unit_mesh_parts;`。即使该表目前未由同一建表语句创建，这也是持久化事实初始化中的危险遗留行为；在扩展 schema 或迁移前必须先移除并确认其历史用途，不能让生命周期重构把它带入新设计。

---

## 3. 统一模型：事实项声明，而非固定分级

### 3.1 类型

| 类型 | 定义 | 示例 |
|---|---|---|
| `SourceState` | 可观察输入，不由系统派生 | Mod 文件、Game Data 目录、Profile 顺序、部署记录、元数据文件 |
| `PersistentFact` | 可持久化、可由 fingerprint 重建的事实 | Patch AssetKey、Unit→Material、Material→Texture、Game Data AssetKey→Archive |
| `SessionProjection` | 由当前会话选择/状态组合出的内存视图 | 当前活动 Profile 覆盖图、材质诊断、部署覆盖图 |
| `PageProjection` | 为某页面组织已有事实的轻量投影 | 普通详情摘要、高级资产行、材质交付文本 |
| `OnDemandOperation` | 用户明确触发、可能读写大文件的操作 | 拆分材质、合并材质、生成重建候选、重建 Game Data 索引 |
| `ExternalValidation` | 不能由本程序独立证明的结论 | Blender 加载记录、游戏内模型/材质/骨骼测试记录 |

`PersistentFact` 不等于永不变。每一个事实都必须登记 fingerprint 与失效触发器。

### 3.2 统一描述字段

后续新增的事实定义和调度注册都应包含：

```text
FactKey                 // 如 ModContent(nodeId)、GameDataIndex、ProfileGraph(profileId)
Kind                    // PersistentFact / SessionProjection / ...
Dependencies            // 输入 generation/fingerprint 的集合
InvalidatedBy           // 监听的领域事件集合
RefreshPolicy           // StartupLowPriority / Incremental / OnDemand / ManualOnly
Persistence             // SQLite / 输出目录报告 / MemoryOnly
ConcurrencyKey          // 同 key 只允许一个执行任务
CancellationPolicy      // 页面关闭取消 / 全局任务不可取消 / 新版本到来取消旧任务
VerificationScope       // InternalStructural / ExternalRequired / None
```

这不是再造独立缓存：`ModFactsDbPath`、Game Data SQLite index、`DerivedStateCoordinator` 快照、`BackgroundTaskService` 都是该模型的既有承载物；本轮只补齐统一的声明、事件与消费边界。

---

## 4. 事实清单、依赖与失效条件

### 4.1 持久化 Mod 事实

| 事实 | 依赖 | 失效事件 | 刷新策略 | 实现归属 |
|---|---|---|---|---|
| Patch group 文件事实 | Mod 目录文件集合 | `ModAdded`、`ModContentChanged`、`ModRemoved` | 导入后仅受影响 node；启动低优先级核验 fingerprint | `ModContentFactsService` |
| Patch AssetKey 清单 | 同上 + analyzer version | 同上 + analyzer schema/version 变更 | 同上 | `CachedPatchGroupAnalysisProvider` + `SqliteModFactsStore` |
| Unit→Material、Material→Texture 边 | 同上 + analyzer version | 同上 + analyzer schema/version 变更 | 同上 | `asset_references` |
| Mod 内/库内反向引用 | 以上正向边的索引投影 | node 增加、变更、删除 | 同一 SQLite 事务中删除/写入该 node 的边；不做全库扫描 | `ix_asset_refs_target` |
| 轻量 Mod 文件/图标/资产摘要 | content generation + metadata generation | Mod 文件变更、元数据变更、Game Data mapping generation 变更 | node 级投影；元数据/Game Data 变化时按需重投影 | `LibraryDerivedDataService` |

**实施要求：**

- `SqliteModFactsStore` 的 schema version 必须真正迁移或清空重建；目前仅写入 `schema_info`，不会在 version 变化时迁移。
- 在开始任何 schema 扩展前，移除并调查 `SqliteModFactsStore.OpenAsync()` 中无条件的 `DROP TABLE IF EXISTS unit_mesh_parts;`；连接打开不能具有销毁事实表的副作用。
- source fingerprint 使用长度/时间可快速工作；对于“时间和长度未变但内容被替换”的极少数情况，提供用户“重新分析此 Mod”与“重新分析全部 Mod”入口。不得在每次详情打开时计算全文件 SHA-256。
- `ModRemoved` 必须调用 `IModFactsStore.DeleteAsync()`；否则反向引用会保留已删除 Mod 的幽灵消费者。需核查并补上 library 删除路径。

### 4.2 Game Data 与元数据事实

| 事实 | 依赖 | 失效事件 | 刷新策略 | 实现归属 |
|---|---|---|---|---|
| Game Data Archive 索引 | Game Data archive 文件 + `archivehashes.json` | `GameDataFingerprintChanged`、索引 schema/classifier version 变更、用户手动重建 | 启动时低优先级仅检查；stale 时排入可取消后台任务，不阻塞 UI | `AssetArchiveIndexService` |
| Unit part 分类 | Game Data index + classifier version | 同上 + `UnitPartAnalyzerVersion` 变更 | 与索引同事务重建 | `AssetArchiveIndexService` |
| Asset type/friendly name/archive 元数据 | metadata 文件 fingerprint | `MetadataChanged`、用户更新元数据 | 按需读取/投影，避免全库同步二进制扫描 | `GameDataMappingFactsService` |
| AssetKey→Game Data mapping | Game Data index generation + metadata generation | 任一 generation 变更 | Session cache，按 `AssetKey` 集合分块或 node 级缓存 | `GameDataMappingFactsService` |

**启动行为：**

1. UI 先启动。
2. 低资源任务读取当前 index fingerprint 与 metadata 文件 fingerprint。
3. 如果 Game Data index `Current`，不重建。
4. 如果 `Missing/Stale/Invalid`，将“需要重建”显示在状态页并排入 `BackgroundTaskService`；默认低优先级，用户可取消或立即执行。
5. 成功重建后发布 `GameDataIndexRebuilt(newGeneration)`，使依赖 mapping 的 SessionProjection 失效；不直接重新解析所有 Mod Patch。

### 4.3 Profile 与部署投影

| 投影 | 依赖 | 失效事件 | 刷新策略 | 实现归属 |
|---|---|---|---|---|
| 活动 Profile 覆盖图 | active profile revision、其成员 content generation、mapping generation | `ProfileChanged`、相关 `ModContentChanged`、`GameDataIndexRebuilt`、`MetadataChanged` | 合并事件后重建一次，只限活动 Profile | `DerivedStateCoordinator` |
| Profile 材质诊断 | 上项 + Mod facts 引用边 | 同上 | 紧随覆盖图或共享其 provider/winner 数据 | `DerivedStateCoordinator` |
| 部署覆盖图 | Data patch 文件指纹 + activation state | `DeploymentCompleted`、`DeploymentDeactivated`、用户点击刷新 | 只刷新部署图，不触碰 content/profile 图 | `DerivedStateCoordinator` |
| 用户状态文字 | 上述 session projection | 对应投影换代 | 纯内存同步投影 | `ModUserStatusProjector` |

**实施要求：**

- `ProfileOverrideGraphService` 不应再次通过 `IModContentFactsService` 遍历/查询活动 Mod；应接收 `DerivedStateCoordinator` 已取得的 `content` facts，或定义一个由同一 snapshot 提供的重载。这样可保证一个刷新周期内同一 content generation 只读取一次。
- `ProfileMaterialDiagnosticsService` 仍使用 SQLite facts 是正确方向；下一步应输入已计算的赢家链，避免再次重复建立 provider/winner 字典。
- 以事件 payload 传递 `AffectedNodeIds`，不要仅用 `ModContentFactsChanged` 无参数事件。若不影响 active Profile，则不得使 expected/material diagnostics dirty。

### 4.4 页面投影与一次性操作

| 项目 | 应依赖 | 允许触发时机 | 禁止行为 |
|---|---|---|---|
| 普通 Mod 详情 | `ModLibraryService.DerivedData` + `DerivedStateCoordinator.Snapshot` | 页面创建/快照换代时读取内存 | 不得扫描 Patch、不得创建材质服务、不得 same-key `InspectAsync()` |
| 高级资产详情 | SQLite Mod facts + mapping cache + 当前 Profile projection | **高级窗口实际打开后**按需加载；支持取消 | 不得在普通详情构造时加载 DataGrid 行 |
| 材质交付摘要 | `MaterialDeliveryFactsService`（SQLite facts） | 高级窗口打开后或普通详情需要轻量摘要时 | 不得调用 `MaterialPackagingService.InspectAsync()` |
| 材质拆分按钮 | 持久化正向事实计算的可拆分性 | 高级窗口打开后读取；点击前可再核对 source fingerprint | 不得为显示按钮状态扫描 patch 两次 |
| 材质候选列表 | SQLite 反向/资产事实 | 点击“用材质包生成…”后再计算 | 不得遍历所有 Patch 的 `CheckCandidateAsync()` |
| same-key 生成候选 | source fingerprint + 当前 Game Data index | **仅用户点击生成** | 不得在页面加载、详情刷新、快照变化时创建 `SameKeyReconstructionPlan` |
| 游戏/Blender 验证 | 用户记录/外部工具 | 候选产生后 | 不得由内部回读判定“游戏通过” |

---

## 5. same-key：从预检改为一次性候选生成

### 5.1 决策

移除 UI 自动调用 `IModSameKeyReconstructionService.InspectAsync()` 的模式。普通/高级详情不再显示“已预检可写”。

点击“生成正式验证候选”后，**同一次操作**完成：

1. 检查不可避免的输入前提：单 Patch、可访问 Game Data、Game Data index 当前；
2. 读取 source Unit 和 current target Unit；
3. 选择已定义安全规则允许的 mapping；
4. 写 current target shell；
5. 执行最小结构性写出完整性检查；
6. 写出 candidate、报告和游戏/Blender 验收清单。

这不是“预检通过”。成功只表示“候选已生成，并通过本程序的最低结构性约束”；游戏或 Blender 测试才是外部可用性证据。

### 5.2 保留的最低结构性保护

下列条件属于写入器不变量，不能因为取消预检而删除：

- 不保留旧 source Unit/Composite Unit payload；
- 每个输出 current target mesh 都要 transfer 或 minify；
- 不写 selected unknown/experimental mapping；
- 输出 AssetKey 不重复；
- 输出 Unit 集合与生成计划一致；
- 生成失败时不得留下被标记为成功的候选目录。

`VerifyOutputAsync()` 的结果在文案上改称“内部结构检查”，不得称为“验证通过”。它无法替代游戏/Blender。

### 5.3 代码改造目标

1. 将 `WriteTestCopyAsync()` 改为自行建立 ephemeral plan，不先调用 `InspectAsync()`。
2. `InspectAsync()` 降级为可选的、显式用户诊断命令，或后续删除；不在 `ModDetailsPageViewModel` 调用。
3. `ModSameKeyReconstructionState` 不再是按钮启用的先决条件；按钮仅受基本输入状态（有 Mod、无进行中任务、Game Data 设置）控制。
4. 输出报告记录 source content generation、Game Data index generation、planner/mapping rule version、selected/minified mesh，并新增 `ExternalValidation = Pending`。
5. 可选新增验证记录文件，例如 `external-validation.json`，由用户选择“Blender 已加载”或“游戏已测试”时写入；此记录是历史证据，不反向改变写出器语义。

---

## 6. 领域事件与调度器

### 6.1 事件定义

建议新增领域事件总线或先在现有 service event 上扩充事件参数。第一版不需要引入通用消息中间件；可定义如下 record 并由单一 `FactLifecycleCoordinator` 消费：

```text
ApplicationStarted
LibraryNodesAdded(nodeIds)
LibraryNodesChanged(nodeIds)
LibraryNodesRemoved(nodeIds)
ProfileChanged(profileId, revision, changeKind)
ActiveProfileChanged(previousId, currentId)
GameDataFingerprintObserved(currentGeneration, state)
GameDataIndexRebuilt(previousGeneration, currentGeneration)
MetadataChanged(previousGeneration, currentGeneration)
DeploymentChanged(gameDataDirectory, deploymentGeneration)
UserRequestedFactRefresh(scope, nodeIds?)
```

`LibraryNodesChanged` 不应仅代表 metadata rename；应区分 `MetadataOnly` 与 `ContentChanged`。元数据修改不应触发 Patch facts 或 Profile graph 重建。

### 6.2 调度规则

| 事件 | 必做 | 不做 |
|---|---|---|
| `ApplicationStarted` | 恢复 SQLite facts；检查 Game Data index 状态；恢复活动 Profile session projection | 不扫描每个 Mod Patch；不运行重建候选规划 |
| `LibraryNodesAdded` | 分析新增 node，写事实和边，增量投影新增 node | 不重建未受影响 node 的 Patch facts |
| `LibraryNodesChanged` | 删除并重算受影响 node facts；若在活动 Profile，刷新 Profile projection | 不扫描全库 |
| `LibraryNodesRemoved` | `factsStore.DeleteAsync(nodeId)`；移除内存投影；若在活动 Profile，刷新 Profile projection | 不保留反向引用残留 |
| `ProfileChanged` | 若为活动 Profile，刷新 expected/material diagnostics；必要时通知部署 | 不刷新 Game Data index、所有 Mod facts |
| `GameDataIndexRebuilt` | mapping cache 换代；活动 Profile mapping/诊断失效；页面下次投影读取新 mapping | 不重新分析 Mod 二进制 |
| `MetadataChanged` | mapping generation 换代，按需重投影显示名称/分类 | 不重建 Game Data archive entries，除非 metadata 格式本身要求 |
| `DeploymentChanged` | 仅刷新 deployed graph | 不刷新 content/expected/material diagnostics |
| `PageOpened(Details)` | 只读取可用快照；按需订阅 | 不启动重型工作 |
| `PageClosed(Details)` | 取消页面私有查询，解除订阅 | 不取消全局导入/索引/部署任务 |

### 6.3 并发、取消和合并

- 每个 `FactKey` 维护一个 `ConcurrencyKey`：相同 key 不允许并行重复分析。
- 新 generation 到来时取消旧 generation 的可取消任务；结果提交前二次比较 fingerprint，过期结果不得写入当前 snapshot。
- 页面私有查询（高级表、筛选）有 `CancellationTokenSource`，关闭窗口/详情切换时取消。
- 全局任务（导入、Game Data index、部署）进入 `BackgroundTaskService`；需要增补可区分的 `RefreshFact` / `ValidateGameDataIndex` 等任务类型和合并 key。
- `DerivedStateCoordinator.RefreshAsync()` 当前“每次 refresh 都取消前一次”的策略可保留给 session projection，但需以事件合并后的 generation 作为刷新输入，不能让每次页面刷新触发它。

---

## 7. 分阶段实施计划

### Phase A：立即止血——页面生命周期和按需边界

**目标：** 普通详情不再启动重型任务，反复打开详情不会残留订阅或后台计算。

1. `PageViewModel` 实现 `IDisposable` 或增加虚拟 `Dispose()`；`ModDetailsPageViewModel`：
   - 保存 `_derivedState.SnapshotChanged` handler 并在 `Dispose()` 解除；
   - 增加页面私有 CTS，取消高级查询/延迟任务；
   - 所有异步结果在 UI 更新前检查 disposed/current request generation。
2. 从 `ModDetailsPageViewModel` 构造函数移除：
   - `RefreshMaterialPackagingStateAsync()`；
   - `RefreshSameKeyReconstructionStateAsync()`；
   - `RefreshAdvancedAssetsAsync()`。
3. `OpenAdvancedDetails()` 后才开始：高级资产表、材质交付摘要、材质拆分状态。建议由高级窗口 Loaded 事件或独立 `AdvancedModDetailsViewModel` 承担，避免主详情 VM 同时承担两个页面生命周期。
4. 普通详情的 `SnapshotChanged` 只调用 `Refresh()`；它不应直接启动 I/O。
5. 为材质拆分/合并与生成候选按钮显示“首次操作时分析/生成”，而不是“后台预检中”。

**验证：**

- 连续打开至少 20 个普通详情，常驻内存不应随打开次数单调增长；
- 普通详情不产生 Patch 分析、same-key plan、Game Data Unit 读取日志/后台任务；
- 关闭详情后强制库/Profile refresh，不应由已关闭页面发起新任务；
- 现有普通详情状态、配置状态、删除/打开文件夹行为不回归。

### Phase B：材质能力完全接入稳定事实

**目标：** 材质按钮状态和候选发现不再重新扫描相同 Patch。

1. 增加基于 `PatchGroupAnalysisCacheEntry` 的 `MaterialPackagingFactsProjector`：由 Unit→Material、Material→Texture、资产集合直接得到 `ModMaterialPackagingState` 的大部分字段。
2. `IMaterialPackagingApplicationService.InspectAsync()` 改为接收/查询 facts，并只在执行写出前做 source fingerprint 再核对。
3. 底层 `MaterialPackagingService` 增加接受已有 `MaterialPackagingInspection`/`PatchGroupAnalysis` 的 overload，避免 `InspectAsync()` + `PlanSplitAsync()` 重复分析。
4. `FindCandidatesAsync()` 改为 SQLite facts 查询/集合运算，不循环调用每个候选 Patch 的 `CheckCandidateAsync()`；最终选中的候选在写出前再做一次 fingerprint 与闭包核对。
5. `MaterialDeliveryFactsService.FindCandidatesAsync()` 由逐 node `TryLoadAsync` 改为批量 SQLite 查询，作为优化项；语义不变。

**验证：**

- 材质状态从持久化 facts 得到的 required/embedded/external/texture 计数与现有实现样本一致；
- 新导入材质包只更新其自身事实及必要的库内候选索引；
- 目标模型 Mod 的材质摘要不因新 Mod 导入而重新解析其 Patch；
- 拆分、合并的写出前核对仍可阻止输入文件已变化的操作。

### Phase C：事件化事实生命周期协调器

**目标：** 用精确失效事件替代全局无参数 dirty。

1. 将 `ModLibraryService.ModContentFactsChanged` 升级为携带 `AffectedNodeIds` 和变更种类的事件。
2. 导入、删除、更新、重新分析分别发布 `Added/Changed/Removed`。
3. 补齐删除调用 `IModFactsStore.DeleteAsync()`，并在同一逻辑路径更新 `DerivedLibraryData`。
4. 新建 `FactLifecycleCoordinator`，初期只负责：
   - 启动时 Game Data index 状态检查；
   - 受影响 Mod facts 的增量重建；
   - 将精确事件送给 `DerivedStateCoordinator`。
5. `DerivedStateCoordinator` 改为接收：活动 Profile node 集合、受影响 node、mapping generation、deployment generation；只在依赖相交时刷新。
6. 修改 `ProfileOverrideGraphService` 与 `ProfileMaterialDiagnosticsService` 接收同一批已加载 facts/赢家结果，消除一个 refresh 周期内的重复读取。

**验证：**

- 导入不在活动 Profile 的 Mod：不重建活动 Profile graph；
- 导入活动 Profile Mod：仅重建该 node facts 和活动 Profile session projection；
- 删除 Mod 后反向引用查询不包含该 node；
- 改 Profile 顺序：不读取 Patch binary；
- Game Data index 重建后：只重投影 mapping 依赖的数据，不重扫 Mod Patch。

### Phase D：Game Data 自动低优先级状态维护

**目标：** 游戏更新后自动发现 index stale，但 UI 始终可用。

1. 启动后通过已有 `GetIndexStatusAsync()` 读取 fingerprint。
2. `Missing/Stale/Invalid` 时建立可取消后台任务，不在构造函数同步跑 `BuildOrRebuildAsync()`。
3. UI 显示“当前索引/需要重建/后台重建中/已取消”；用户可立即启动或取消。
4. 成功后发布 `GameDataIndexRebuilt`，失效 mapping 与活动 Profile projection。
5. 将 index schema、`UnitPartAnalyzerVersion` 和 metadata generation 纳入明确的 index/mapping version 比较；版本变化必须触发正确 rebuild 或提示。

**验证：**

- 模拟 archive hash 改变后，状态页标为 stale；
- 不打开详情也能看见重建任务；
- 重建完成后重建候选操作使用新 index generation；
- 未变更 Game Data 的第二次启动只检查 fingerprint，不扫描 archive。

### Phase E：same-key 直接生成候选

**目标：** 去除自动完整计划与“预检通过”语义，保留必要的写出不变量和外部测试记录。

1. UI 改为“生成验证候选”，不加载 `SameKeyReconstructionSummary` 计划摘要；按钮不依赖 `InspectAsync()`。
2. 将 `WriteTestCopyAsync()` 改为一次性 BuildPlan + Write，或新增 `GenerateCandidateAsync()` 以避免语义混乱。
3. 按钮点击时做基础条件检查：当前 Game Data index、source Patch 数量、输出目录；其他 mapping/serialization 错误作为生成过程错误返回。
4. 输出报告明确标记：`InternalStructuralChecks` 与 `ExternalValidationPending`。
5. 保留 `InspectAsync()` 仅供明确的开发诊断命令，或在迁移完成后删除接口及 UI 依赖。
6. 候选输出结构检查使用“内部一致性”文案；游戏/Blender为唯一外部接受标准。

**验证：**

- 打开任意普通/高级详情不读取 source/target Unit；
- 点击生成只执行一次 source/target 读取与候选构建，不出现“先 Inspect 再重复构建”；
- 当前 Chiffon 生成、材质 provider 联用、游戏正常加载的实测链路不回归；
- 输出报告保留用于问题复现，不声称游戏已通过。

### Phase F：可观察性、基准与回归保护

1. 为每个重型事实/操作记录：fact key、输入 generation、命中缓存/重建、耗时、估算读取字节、取消原因。
2. 状态页或开发诊断页显示最近事实刷新，而不是只有模糊“正在分析”。
3. 添加集成测试：
   - unchanged fingerprint 不重新调用 analyzer；
   - `ModRemoved` 删除 facts/反向边；
   - 只影响非活动 node 的变更不刷新 profile graph；
   - 同 key 并发请求共享执行；
   - 关闭详情取消私有查询并解除订阅。
4. 建立手工性能基准：冷启动、热启动、导入单 Mod、连续打开 20 个详情、打开高级详情、生成候选、材质拆分。

---

## 8. 实施顺序与兼容约束

1. 先做 Phase A，再做 Phase B。两者直接消除用户已观察到的内存尖峰和重复引用分析。
2. Phase C/D 完成后再改 same-key UI；这样生成操作可消费可靠的 generation/event 基础。
3. 任何阶段都不能将大 Patch/Unit model 保存在 `DerivedStateSnapshot` 或详情页字段中。快照只保存不可变、轻量的事实投影和 generation。
4. 任何持久化 cache schema、analyzer、mapping rule 或 classifier 版本变化都必须令缓存显式失效，不能静默混用旧数据。
5. 生成候选前检查 source content generation 和 Game Data index generation；若在生成期间变更，丢弃结果并要求重新生成。
6. 不把 `MaterialPackagingService`、same-key 输出器的内部结构检查作为游戏验证；文档和 UI 用词必须保持区分。

---

## 9. 完成标准

本重构完成的最低验收标准：

- 普通详情在打开和切换时不启动 MaterialPackaging、AdvancedAssetQuery 或 same-key plan；
- 连续浏览详情不因事件订阅泄漏使内存或任务数量累积；
- Mod 资产和引用边只在对应 Mod 文件/分析版本变化后重新读取；
- 删除 Mod 会清除其反向引用事实；
- Game Data 更新后会被检测并安排低优先级索引重建，而未更新时不重扫 archive；
- 活动 Profile 投影只因相关 Mod、Profile、Game Data mapping 或部署状态变化而刷新；
- same-key 候选只在用户点击时执行一次构建/写出，输出后由 Blender/游戏进行外部测试；
- Chiffon “重构模型包 + 材质拆分 provider 包”当前已游戏验证的正常模型、材质、骨骼行为不回归。
