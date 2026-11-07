# Mod 信息中心落地规划

## 1. 文档目的

本文规划在 HD2ModCore 与 HD2ModManager 中建立统一的 Mod 信息中心（Mod Information Center），用于统一编排 Mod 派生信息的查询、生产、缓存、失效、并发去重和诊断。

本轮只规划，不直接修改现有代码。

核心目标不是增加一个“万能分析服务”，而是建立一条统一的信息生产总线：

```text
业务需求
    ↓
Mod 信息中心
    ├─ 查询缓存
    ├─ 判断 generation / producer version
    ├─ 合并相同请求
    ├─ 调度指定信息生产器
    ├─ 原子保存结果
    └─ 返回结果与诊断
```

## 2. 必须固定的总原则

### 2.1 基础部署与深度分析彻底解耦

基础部署只允许依赖：

- `profiles.json` 中的活动配置、成员和顺序；
- `library.json` 中的 ModNode 与 `RelativePath`；
- Mod 库目录中的实际 Patch 文件；
- 文件系统级 Patch 文件索引；
- 部署能力检测；
- Data 目录实际状态。

基础部署不得依赖：

- `mod-facts.sqlite`；
- AssetKey 资产分析缓存；
- Unit/Material/Texture 引用链；
- Unit 版本检测；
- Mesh、LOD、骨骼或材质闭包分析。

部署所需的文件索引应由独立的文件事实生产器直接生成，而不是从 `ModContentFacts` 间接提取。

### 2.2 所有派生信息必须有明确等级

禁止使用含义模糊的“分析 Mod”请求。每个请求必须明确所需的信息产品：

1. `FileFacts`
2. `AssetInventory`
3. `ReferenceGraph`
4. `UnitVersion`
5. `AdvancedUnitAnalysis`
6. `Thumbnail`
7. `MaintenanceAnalysis`

不同产品独立判断缓存有效性、独立失败、独立生产，不因为请求低等级信息而自动生产高等级信息。

### 2.3 信息中心负责编排，不负责所有专业解析

信息中心负责：

- 接收请求；
- 查询缓存；
- 判断缓存是否可用；
- 合并相同请求；
- 调度生产器；
- 保存生产结果；
- 返回统一结果；
- 记录请求来源和诊断。

信息中心不负责：

- 直接解析 Patch 二进制；
- 直接解析 Unit；
- 直接生成缩略图；
- 直接规划部署；
- 直接决定配置覆盖规则；
- 直接实现高级重构业务。

### 2.4 三类状态必须独立建模

以下状态属于不同事实来源，禁止以其中任一状态冒充另一种状态：

1. **Mod 库状态**：Mod 是否已导入、目录和 Patch 文件是否完整、名称、备注、图像及维护健康状态；
2. **配置预计状态**：指定 Profile 的 AssetKey 覆盖链、最终提供者和可选材质诊断；
3. **实际部署状态**：Game Data 目录中实际存在的链接或副本，以及它是否与活动配置一致。

`DerivedStateCoordinator` 只能编排和投影这些独立状态，不能将它们合成为含义不明的“Mod 已生效”结论。配置编辑、激活配置与应用部署也必须是独立命令：配置编辑只写 `profiles.json`；激活只切换活动 Profile；只有应用部署命令允许写入 Game Data。

### 2.5 导入与普通管理的最小信息层级

导入成功的同步最低条件仅为文件已安全入库、已创建 ModNode 且 `FileFacts` 可生成；此后用户必须立刻能够编辑元数据、加入配置、删除或部署。

- `AssetInventory` 可以在导入后后台预热，用于预计覆盖预览；
- `ReferenceGraph` 是可取消、可延期的材质诊断信息，失败不得影响普通库管理、配置保存或部署；
- `UnitVersion` 是独立的库级异步健康状态，不属于普通内容事实；
- `AdvancedUnitAnalysis` 只能由高级工具显式请求。

## 3. 当前实际问题与目标结构

### 3.1 当前主要问题

当前多个入口可以各自触发内容分析：

- 导入完成后的 `AnalyzeImportedAsync`；
- `ModLibraryService` 派生数据刷新；
- `DerivedStateCoordinator` 内容刷新；
- `ProfileOverrideGraphService` 自行取得内容事实；
- `ProfileApplyService` 部署前取得内容事实；
- `ModUserStatusService` 仍可能进行全库内容获取；
- 高级材质诊断和高级 Mod 分析。

这些入口存在以下风险：

- 同一个 Mod 被重复分析；
- 多个 Provider 实例同时写 SQLite；
- 缓存有效性判断不统一；
- 分析失败影响范围不明确；
- 部署被不必要的 SQLite 或 Adaptation 分析阻断；
- 新功能继续各自创建新的缓存链路。

### 3.2 目标结构

```text
JSON 权威状态
├─ library.json
├─ profiles.json
└─ settings.json

Mod 信息中心
├─ FileFacts 请求
├─ AssetInventory 请求
├─ ReferenceGraph 请求
├─ UnitVersion 请求
├─ AdvancedAnalysis 请求
├─ Thumbnail 请求
└─ Maintenance 请求

生产器
├─ ModFileFactsProducer
├─ AssetInventoryProducer
├─ ReferenceGraphProducer
├─ UnitVersionProducer
├─ AdvancedAnalysisProducer
├─ ThumbnailProducer
└─ MaintenanceAnalysisProducer

缓存仓库
├─ 按 Mod / 信息类型存储
├─ 保存 generation
├─ 保存 producer version
├─ 原子发布
├─ 失败记录
└─ 按 Mod 统一删除

业务消费者
├─ Mod 库管理
├─ 配置预计状态
├─ 基础部署
├─ 高级诊断
└─ 高级修复工具
```

## 4. 信息产品定义

### 4.1 FileFacts：文件事实

这是基础部署唯一需要的 Mod 信息产品。

内容包括：

- ModNodeId；
- Mod 相对路径和绝对路径；
- 顶层 Patch 文件；
- archive hex；
- source Patch index；
- 规范化 Patch 顺序；
- base、stream、gpu_resources 的组关系；
- 文件长度；
- 最后写入时间；
- 文件是否存在、可读；
- 文件名解析问题；
- sidecar 缺失或无 base 问题；
- FileFacts generation。

生产器只枚举 Mod 目录顶层文件，不读取 Patch 内部二进制内容，不读取 SQLite。

使用者：

- 基础部署；
- Mod 库基础状态；
- 配置成员文件状态；
- 导入后的第一阶段文件检查。

失败影响：

- 文件不存在、文件名无法解析、sidecar 不完整：阻断相关 Patch 组部署；
- 不影响其他 Mod；
- 不应触发 AssetInventory 或高级分析。

### 4.2 AssetInventory：AssetKey 清单

内容包括：

- Patch 组；
- AssetKey；
- TypeId、FileId；
- 资产类型分类；
- TOC 数据大小；
- stream/gpu 规模；
- AssetInventory generation；
- TOC 扫描问题。

生产器可以调用 Adaptation 的 Inventory 分析，但不解析 Mesh、骨骼或完整引用闭包。

使用者：

- Mod 详情资产摘要；
- 配置预计 AssetKey 覆盖图；
- Game Data AssetKey 映射；
- 基础 Mod 状态提示。

失败影响：

- Mod 文件仍可管理；
- 基础部署原则上可以继续，只显示“资产信息不可用”；
- 不应覆盖或删除上一份有效缓存。

### 4.3 ReferenceGraph：引用关系

内容包括：

- Unit → Material；
- Material → Texture；
- 引用来源 Patch 组；
- 引用偏移、slot、reference index；
- 引用解析问题；
- ReferenceGraph generation。

这是配置材质诊断和材质相关功能需要的信息，不是基础部署信息。

失败影响：

- 配置页材质诊断显示“尚未完成”或“分析失败”；
- 不阻断 Mod 导入、配置排序或基础部署。

### 4.4 UnitVersion：版本检测

内容包括：

- Unit AssetKey；
- source 文件；
- 版本字段；
- 当前 Game Data 版本参照；
- 是否可能过时；
- 检测 generation。

当前 `UnitVersionProbe` 的固定偏移读取可以作为生产器内部实现，但不应绑定到每次普通内容事实刷新。

库投影应将其明确显示为 `Unknown`、`Current`、`PossiblyOutdated` 或 `Failed`。该状态仅用于提示维护需求，绝不能自动修复、阻断普通 Mod 管理、配置保存或基础部署。

触发时机：

- 导入时单 Mod 检测；
- Mod 文件 generation 变化时重新检测；
- Game Data 版本索引变化时批量检测；
- 用户主动请求维护检测。

### 4.5 AdvancedUnitAnalysis：高级结构分析

内容包括：

- Unit 完整结构；
- Mesh、LOD、section；
- 骨骼和 TransformInfo；
- 材质绑定；
- 可重构性诊断；
- 模型转移准备数据。

只允许按需生产：

- 高级详情页；
- 重构工具；
- 跨护甲替换工具；
- 模型缓存生产流程。

失败不得影响 Mod 基础管理和基础部署。

### 4.6 Thumbnail：缩略图

缩略图请求应独立于 Patch 分析：

- 输入为源图像路径和图像 generation；
- 输出为指定尺寸缩略图；
- 生成失败只影响展示；
- 用户更换图片后只使对应 Thumbnail 缓存失效；
- 不应触发 Patch 分析。

### 4.7 ModDataIndex：跨 Mod 资产与引用索引

`AssetInventory` 与 `ReferenceGraph` 的持久化结果应支持跨 Mod 聚合查询，形成只读的“Mod 版 Data 索引”。它不是新的深度分析等级，而是已有信息产品的索引投影。

至少提供：

- `FindProviders(AssetKey)`：哪些 Mod/PatchGroup 提供该 AssetKey；
- `FindConsumers(AssetKey)`：哪些 Mod/PatchGroup 的资源引用该 AssetKey；
- 按指定 Profile 和加载顺序解析最终提供者；
- 以相关 Mod generation 与索引 schema version 作为有效性条件；
- Mod 删除时删除其索引行，不影响其他 Mod。

该索引服务于外置材质包、引用诊断和高级工具查询；它不参与基础部署，也不能强制导入等待完整引用链生产。

## 5. 缓存仓库设计原则

### 5.1 统一缓存键

缓存键至少应包含：

```text
ModNodeId
InformationKind
InputGeneration
ProducerVersion
SchemaVersion
```

不同信息产品不能共用一个无法区分层级的缓存条目。

### 5.2 generation 来源

FileFacts generation 至少由以下内容构成：

- 相对文件名；
- 文件长度；
- 最后写入时间；
- Patch 组关系。

此外还必须纳入：

- `FileFacts` producer version 与 schema version；
- 已解析文件的 archive hex、source Patch index 与 sidecar kind；
- 顶层目录中无法解析但可能为 Patch 文件的文件名及其诊断结果。

这样在文件名解析规则、Patch 分组规则或事实结构升级后，旧缓存不会被错误复用。

AssetInventory 和 ReferenceGraph 应依赖 FileFacts generation。

UnitVersion 应依赖：

- FileFacts generation；
- Game Data 版本/映射 generation。

AdvancedUnitAnalysis 应依赖：

- FileFacts generation；
- Game Data 目标结构 generation；
- Producer version。

Thumbnail 应依赖：

- 原始图像路径；
- 图像长度和最后写入时间；
- 缩略图尺寸；
- Thumbnail producer version。

### 5.2.1 FileFacts 的无仓库部署路径

`FileFacts` 必须能够在没有 SQLite、缓存目录不可写或缓存仓库初始化失败时直接从 Mod 目录生成。持久缓存仅用于优化，不能成为生成或读取文件事实的前置条件。

部署请求应明确使用 `RequireFresh` 策略：若文件 generation 已改变，必须重新枚举顶层文件，而不能以过期缓存继续部署。普通库展示和后台预热可以使用旧结果，但必须标记为 `Stale`。

### 5.3 生产失败处理

必须采用“成功后发布”的策略：

```text
读取旧缓存
    ↓
发现过期
    ↓
生产新缓存到临时结果
    ├─ 成功：原子发布新缓存
    └─ 失败：保留旧缓存，记录失败诊断
```

不能在生产开始时先删除旧缓存，也不能因为一次生产异常把有效结果替换为空列表。

返回结果需要区分：

- `Fresh`：当前 generation 的新结果；
- `Cached`：已验证有效的缓存；
- `Stale`：存在旧结果但输入已变化；
- `Failed`：本次生产失败；
- `Unavailable`：尚无结果且无法生产。

缓存数据状态与本次请求结果必须分开表达，不能用单一互斥枚举混合两者。例如刷新失败但可返回旧数据时，结果应表达为“返回 `Stale` 数据，同时本次刷新为 `Failed` 并携带诊断”；部署方再根据请求策略决定是否接受该数据。

### 5.4 请求去重与并发控制

信息中心需要按以下维度合并进行中的请求：

```text
ModNodeId + InformationKind + InputGeneration
```

多个请求者同时请求相同信息时：

- 只运行一个生产任务；
- 其他请求等待同一任务；
- 生产完成后所有请求获得同一个结果；
- 记录所有请求来源，而不是重复生产。

不同信息等级是否可以并发，需要按生产器明确规定。至少同一个 Mod 的同一种缓存生产必须串行。

共享生产任务不得直接使用任一等待者的 `CancellationToken`。调用者取消时只停止等待该结果；只有信息中心自身关闭、明确失效或无消费者且生产器支持安全取消时，才可取消底层生产任务。

删除 Mod 时必须使该 NodeId 的进行中请求失效，并在生产完成、发布缓存前再次确认 NodeId 仍存在且 generation 仍匹配。这样可避免删除后的旧任务完成并重新写入已删除 Mod 的缓存。

### 5.5 请求来源

每次请求记录：

- 来源名称；
- 请求类型；
- ModNodeId；
- 信息等级；
- 输入 generation；
- 开始和结束时间；
- 是否命中缓存；
- 是否合并到已有任务；
- 成功、失败或使用旧缓存；
- 诊断信息。

建议来源枚举包括：

- `Import`
- `LibraryRefresh`
- `ProfilePreview`
- `Deployment`
- `MaterialDiagnostics`
- `AdvancedDetails`
- `RepairTool`
- `UserRefresh`

## 6. 各业务流程目标依赖

### 6.1 导入流程

```text
导入文件
    ↓
写入 library.json 和 Mod 文件
    ↓
请求 FileFacts
    ↓
返回 Mod 可管理状态
    ↓
后台请求 AssetInventory
    ↓
按需请求 ReferenceGraph / UnitVersion
```

导入成功的最低条件是文件已安全落库并能生成基本 ModNode，不要求高级分析成功。

### 6.2 Mod 库管理

名称、备注、图片修改只更新 JSON 或 Thumbnail 缓存，不触发 Patch 分析。

删除 Mod 时：

1. 从所有 Profile 删除 NodeId；
2. 更新 `profiles.json`；
3. 删除 Mod 文件目录；
4. 请求信息中心删除该 NodeId 的全部缓存产品；
5. 删除失败任务和请求记录中的可清理数据；
6. 发布库变化事件。

### 6.3 配置管理

配置增删和排序只更新 `profiles.json`，不写 Data。

预计覆盖状态请求：

- 先使用已有 FileFacts/AssetInventory；
- 不足时由信息中心生产所需等级；
- 不自动生产 AdvancedUnitAnalysis；
- ReferenceGraph 只在材质诊断区域明确请求。

### 6.4 基础部署

部署必须直接请求 FileFacts：

```text
ActiveProfile
    ↓
LibrarySnapshot
    ↓
FileFacts
    ↓
PatchFileIndex
    ↓
ApplyPlanner
    ↓
ApplyExecutor
```

部署不请求 AssetInventory、ReferenceGraph、UnitVersion 或 AdvancedUnitAnalysis。

### 6.5 高级维护

过时检测和修复分离：

```text
UnitVersion / MaintenanceAnalysis
    ↓
标记 Mod 可能过时
    ↓
用户进入高级工具
    ↓
AdvancedUnitAnalysis
    ↓
生成修复计划
    ↓
写出新 Patch
    ↓
重新请求 FileFacts / AssetInventory
```

修复工具不得直接修改缓存而不更新对应 generation。

## 7. 对现有代码的映射

### 7.1 可保留并转为生产器的组件

- `ModContentFactsService`：拆出其中的文件事实生产部分和资产事实投影部分；
- `AdaptationPatchGroupAnalysisProvider`：作为 AssetInventory/ReferenceGraph 生产器底层；
- `PatchGroupAnalyzer`：继续负责 Patch 内部解析；
- `UnitVersionProbe`：转为 UnitVersion 生产器；
- `SqliteModFactsStore`：逐步收敛为缓存仓库实现；
- `ThumbnailService`：转为 Thumbnail 生产器；
- `AdvancedModAnalysisService`：转为 AdvancedUnitAnalysis 生产器。

### 7.2 需要改为消费者的组件

- `ProfileOverrideGraphService`：消费已生成的 FileFacts/AssetInventory，不自行触发重复分析；
- `ProfileMaterialDiagnosticsService`：消费 ReferenceGraph，不自行决定如何生成缓存；
- `LibraryDerivedDataService`：消费信息中心结果，负责投影而非生产所有事实；
- `DerivedStateCoordinator`：负责刷新范围和请求信息等级，不直接承担分析实现；
- `ProfileApplyService`：只请求 FileFacts；
- `ModUserStatusService`：应逐步收敛到共享派生快照，避免全库自行分析。

### 7.3 需要重点盘点的旧入口

- `ModLibraryService.RebuildIndex(buildDerivedData: true)`；
- `ModUserStatusService.GetStatusesAsync()`；
- `ProfileOverrideGraphService.BuildAsync()`；
- `GameDataArchiveBrowserService.BuildAsync()`；
- `ImportService.AnalyzeImportedAsync()`；
- `DerivedStateCoordinator.RefreshCoreAsync()`。

盘点目标不是立即删除，而是明确每个入口：

- 是否只查询缓存；
- 是否触发生产；
- 触发什么信息等级；
- 是否可以消费上游已获得的结果；
- 失败是否应影响当前操作。

## 8. 全面迁移调用矩阵

以下矩阵是当前代码盘点结果。这里的“旧入口”暂不等于可以立即删除的代码；必须先完成消费者迁移、共享生命周期收敛和回归验证，再删除兼容构造函数及旧存储职责。

| 入口/组件 | 当前读取或生产方式 | 实际信息等级 | 当前缓存/副作用 | 目标接入方式 | 优先级 |
| --- | --- | --- | --- | --- | --- |
| `LibraryDerivedDataService` | 有中心时逐节点请求 `AssetInventory`，无中心时调用 `GetLibraryFactsAsync` | `AssetInventory` | 中心 JSON；兼容路径直连旧 producer | 强制注入共享中心；保留 `DerivedLibraryData` 作为 UI 投影 | P0 |
| `ProfileOverrideGraphService` | 有中心时逐节点请求 `AssetInventory`，无中心时调用 `GetLibraryFactsAsync` | `AssetInventory` + GameData mapping | 中心 JSON；兼容路径直连旧 producer | 收敛为中心构造；删除 nullable 兼容分支 | P0 |
| `ModUserStatusService` | 有中心时全库请求 `AssetInventory`，无中心时调用 `GetLibraryFactsAsync` | `AssetInventory` + expected/deployed projection | 中心 JSON；预计图和实际部署独立 | 依赖共享中心；失败按节点降级，不再允许旧全库入口 | P0 |
| `GameDataArchiveBrowserService` | 直接全库调用 `GetLibraryFactsAsync`，再做 archive mapping | `AssetInventory` + mapping + deployed graph | 旧 producer 无统一缓存语义 | 改为中心逐节点请求 `AssetInventory`；中心只负责 Mod 派生事实 | P1 |
| `ProfileApplyService` | 已经使用中心 `FileFacts` | `FileFacts` | JSON FileFacts cache | 保持现状，作为部署隔离基线 | 已完成 |
| `ModDetailsPageViewModel` | 直接请求中心 `AssetInventory`/`UnitVersion` | `AssetInventory` + `UnitVersion` | 中心 JSON、`ModDataIndex` | 保持现状，继续清理旧摘要回退 | 已完成 |
| `DerivedStateCoordinator` | 中心获取活动配置 `AssetInventory`，仍持有旧 producer 字段 | `AssetInventory` | 共享中心 + 旧 producer 兼容成员 | 移除旧 producer 注入和直接调用 | P1 |
| `ModLibraryImporter` | 导入本身写 JSON；可选旧 `IModFactsStore` 仅用于回滚清理 | 无；最低后续为 `FileFacts` | 可能触碰 SQLite | 导入提交后由上层请求中心 `FileFacts`；回滚改为中心失效/清理 | P1 |
| `ModLibraryManager` | 删除时可选调用 `IModFactsStore.DeleteAsync` | 无；删除清理 | SQLite 删除副作用 | 由共享中心统一 `InvalidateNodeAsync`；SQLite 清理在仍有高级数据时单独处理 | P1 |
| `ModLibraryService.Remove` | 显式删除 SQLite、FileFacts JSON，再调用中心失效 | 无；删除清理 | 多套缓存删除，顺序和生命周期分散 | 统一由中心清理 FileFacts/派生 JSON；另行注入高级事实清理器 | P0 |
| `MaterialDeliveryFactsService` | 直接读 `IModFactsStore` | `ReferenceGraph`/完整 patch facts | SQLite | 明确标记为高级材质诊断；改为中心 `ReferenceGraph`，必要时保留查询索引 | P2 |
| `AdvancedModAssetQueryService` | 直接读 SQLite 完整分析和消费者反向索引 | `AdvancedUnitAnalysis`/`ReferenceGraph` | SQLite 资产表和引用表 | 改为中心高级产品；反向消费者查询需要先确认是否由 `ReferenceGraph` 产品承载 | P2 |
| `ProfileMaterialDiagnosticsService` | 既可读 provider/SQLite，也可读中心 ReferenceGraph | `ReferenceGraph` + mapping | 新旧双路径 | 统一中心路径；SQLite 仅作为过渡或独立查询索引 | P2 |
| `CoreServices` 各工厂 | 多处无条件 `new ModInformationCenter`、`ModContentFactsService`、`SqliteModFactsStore` | 取决于下游 | 容易产生多个中心、多个 SQLite provider | 所有业务工厂增加并优先传递共享中心；无参便捷工厂仅保留测试/独立 CLI 用途 | P0 |

### 8.1 SQLite 职责分类

`SqliteModFactsStore` 不能在第一批迁移中整体删除，因为它同时承载三种不同职责：

1. **可被信息中心取代的派生快照**：`mod_fact_snapshots` 中用于普通资产清单或旧 patch facts 的内容，目标是由 `AssetInventory`/`ReferenceGraph` 的 JSON 产品接管。
2. **高级分析缓存**：`advanced_mod_analysis_snapshots`，目标是由 `AdvancedUnitAnalysis` 产品接管，迁移完成前必须保留。
3. **反向引用查询索引**：`mod_assets`、`asset_references` 以及 `FindConsumerFactsAsync`，当前被高级资产表、材质交付和部分诊断使用。它可能是独立的查询投影，而不是普通信息中心缓存；必须先定义 ReferenceGraph 的查询能力或新的只读索引接口，不能直接删除。

因此清理顺序应为：先停止普通业务写入旧快照，再迁移高级消费者，最后将剩余 SQLite 缩减为明确命名的高级查询索引；确认无调用后才删除 `IModFactsStore` 的兼容接口。

### 8.2 生命周期结论

`ShellViewModel` 创建的中心应成为 Manager 应用生命周期内的共享实例。当前 `CoreServices` 中 `CreateModSameKeyReconstructionService`、`CreateAdvancedModAnalysisService`、`CreateProfileOverrideGraphService`、`CreateModUserStatusService` 等便捷路径会重新创建中心或旧 producer，存在缓存不共享、任务不去重和删除失效不完整的风险。迁移时应为这些工厂增加显式中心重载，并让上层组合根始终传入同一实例；无中心重载不得再被主 UI 使用。

## 9. 分阶段落地计划

### 阶段 0：建立事实与调用审计

只读审计，不改业务行为：

1. 列出所有分析入口；
2. 标记每个入口读取/生产的信息等级；
3. 记录每个入口是否写 SQLite；
4. 记录是否全库、活动配置或单 Mod；
5. 记录失败如何传播；
6. 确认主 UI 实际使用哪些旧入口；
7. 用日志或测试确认同一 Mod 的并发请求路径。

验收：形成调用矩阵，能够回答任意一条分析请求由谁生产、写入哪里、影响谁。

### 阶段 0.5：领域边界与生命周期验收

在引入任何新接口前，先以自动化测试和调用审计固定以下用户操作边界：

| 用户操作 | 允许请求的信息 | 禁止行为 |
| --- | --- | --- |
| 导入成功 | `FileFacts` | 阻塞于 SQLite、引用图或 Unit 分析 |
| Mod 名称、备注、图像编辑 | 无 | 任何 Patch 扫描 |
| 配置增删和排序 | 已有或按需 `AssetInventory` | 写 Game Data、触发高级分析 |
| 材质诊断 | `ReferenceGraph` | 阻塞配置保存或基础部署 |
| 激活配置 | 无 | 直接写 Game Data |
| 应用部署 | `FileFacts` | 请求 Adaptation、SQLite、Game Data AssetKey 映射 |
| 过时检测 | `UnitVersion` | 自动修复、阻断普通管理 |
| 重构、材质工具、护甲替换 | `AdvancedUnitAnalysis` 及所需依赖信息 | 覆盖普通缓存或隐式启动 |

验收：每项操作均可从日志和测试证明其请求的信息等级与禁止行为相符；库状态、配置预计状态和实际部署状态均可独立失败、独立展示。
- 后续人工验证发现详情页资产标签可能长期显示“资产未解析”，原因是详情页只读取旧 `DerivedModNodeData.AssetSummary`，而该页的高级表格已经直接读取信息中心 AssetInventory。现已让详情页直接请求并保存 AssetInventory 结果，同时在缓存命中时恢复 ModDataIndex，避免显示滞后于高级详情页；相关 10 项 Core 回归测试通过。

### 阶段 1：定义统一接口，不改变现有生产器

新增概念接口：

```text
IModInformationCenter
IModInformationRequest
IModInformationResult
IModFactsRepository
IModInformationProducer
```

先允许旧的 `ModContentFactsService` 作为内部适配器，不立即拆解全部实现。

验收：至少可以通过统一入口请求 FileFacts 和 AssetInventory，并返回统一状态。

### 阶段 2：先接管 FileFacts 和基础部署

1. 从 `ModContentFactsService` 中独立出文件事实生产器；
2. 让 `PatchFileIndexBuilder` 成为部署索引的直接来源；
3. 修改 `ProfileApplyService` 只请求 FileFacts；
4. 保持 ApplyPlanner 和 ApplyExecutor 的公共行为；
5. 验证 SQLite 不可用时仍可部署；
6. 验证 AssetInventory 失败不影响部署。

这是收益最大、风险最低的阶段。

### 阶段 3：统一导入和普通库派生状态

1. 导入完成首先请求 FileFacts；
2. AssetInventory 改为后台请求；
3. 普通库页消费 FileFacts/AssetInventory；
4. 统一 generation 和错误显示；
5. 取消普通流程对高级分析的隐式请求；
6. 统一删除时的缓存清理。

### 阶段 4：统一预计覆盖图

1. ProfileOverrideGraphService 改为接收或请求共享 AssetInventory；
2. 不再重复调用内容分析；
3. 引入 Profile revision、Mod generation、Game Data mapping generation；
4. 缓存预计图结果；
5. 预计图失败不影响 Profile JSON 和基础部署。

### 阶段 5：接入 ReferenceGraph 和 UnitVersion

1. 将引用链与资产清单分离；
2. 材质诊断明确请求 ReferenceGraph；
3. UnitVersion 只在导入、Mod 变化或 Game Data 版本变化时生产；
4. 普通库页面只消费已有结果；
5. 失败保留旧缓存并显示状态。

### 阶段 6：接入高级工具

最后再接入：

- Full Unit 分析；
- Mesh/LOD/骨骼分析；
- 跨护甲候选；
- Patch 重构；
- 模型缓存；
- 材质拆分和合并。

高级工具只能通过信息中心请求高级信息，不得绕过仓库自行写入同一类缓存。

## 9. 测试与验收重点

### 9.1 部署隔离

- SQLite 文件不存在时，FileFacts 和部署仍成功；
- AssetInventory 生产失败时，部署仍可执行；
- ReferenceGraph 生产失败时，部署仍可执行；
- 高级分析失败时，部署仍可执行；
- 缺少实际 Patch 文件时，部署明确失败并返回路径。

### 9.2 请求去重

- 同一 Mod、同一信息等级、同一 generation 的并发请求只生产一次；
- 不同请求来源可以共享同一生产任务；
- 失败任务不会留下半成品缓存；
- 下一次请求可以重新生产。

### 9.3 generation 失效

- Patch 文件长度变化会使 FileFacts 失效；
- Patch 文件时间变化会使 FileFacts 失效；
- 文件组变化会使 FileFacts 失效；
- FileFacts 变化会使 AssetInventory 和 ReferenceGraph 失效；
- Game Data 映射变化会使预计覆盖图失效；
- 图片变化只使 Thumbnail 失效。

### 9.4 删除清理

删除 Mod 后验证：

- JSON 中不再存在 NodeId；
- 所有 Profile 都不再引用 NodeId；
- Mod 目录已删除；
- FileFacts、AssetInventory、ReferenceGraph、UnitVersion、AdvancedAnalysis 均已删除或标记废弃；
- 不影响其他 Mod 的缓存；
- 不影响 Data 中当前已部署内容，除非用户明确执行重新部署或停用。

## 10. 暂不纳入第一阶段的内容

以下内容不应在第一阶段一起重构：

- 全部 SQLite 表结构重写；
- 高级 Unit 分析算法改写；
- Patch 重构业务改写；
- 材质拆分、合并流程改写；
- UI 页面全面重构；
- Data 实际覆盖图重写；
- 缩略图底层算法重写。

第一阶段只需要固定信息中心接口、请求等级、缓存状态和基础部署隔离边界。

## 11. 最终验收标准

当信息中心架构完成后，应满足：

1. 任意业务代码都不能直接绕过信息中心生产 Mod 派生缓存；
2. 基础部署只依赖 JSON 和 Mod 文件事实；
3. 相同信息请求不会重复生产；
4. 缓存失败不会自动删除有效旧结果；
5. 高级分析失败不会阻断 Mod 管理和基础部署；
6. 每个缓存都能回答来源、输入 generation、生产版本和失败原因；
7. 删除 Mod 可以统一清理全部相关缓存；
8. 新功能必须声明自己需要的信息等级，不得笼统调用“完整分析”；
9. 普通状态、配置预计状态、实际 Data 状态和高级修复状态互不冒充；
10. 信息中心是编排总线，而不是承载所有业务逻辑的超级类。
