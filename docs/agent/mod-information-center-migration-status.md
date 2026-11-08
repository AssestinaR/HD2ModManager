# Mod 信息中心全面迁移与旧实现清理清单

> 更新时间：2026-07-24
>
> 本文是当前代码状态的直接盘点，不是目标架构设想。凡是标记为“兼容路径”的代码，均表示当前仍可被调用，不能视为已经迁移完成。

> ⛔ **状态（2026-11-08）：本文的"兼容边界"已经被清掉了，§1「当前不能做的事情」不再成立。**
> 2026-07-24 之后，遗留兼容层按本文 §5 第 5 批执行完毕，逐项复核结果（`rg` 全仓，2026-11-08）：
> - `IModContentFactsService` **已删除**（代码里 0 处）；`GetLibraryFactsAsync` 0 处；
> - `IModFactsStore` **已删除**（0 处）；`SqliteModFactsStore` **类型已不存在**（只剩架构测试里那条"禁止 new"的正则）；
> - `mod_fact_snapshots` / `advanced_mod_analysis_snapshots` / `mod_assets` / `asset_references` 这些表名**全仓 0 处**，
>   遗留 SQLite schema 已处置；
> - `CreateModContentFactsService` / `CreateModFactsStore` / `CreateAdvancedModAnalysisCacheStore` 三个无中心便捷工厂**都已删除**；
> - §4.3 建议的拆分已经落地：`IModInformationCache`（`HD2ModCore/Application/IModInformationCache.cs`）+
>   `IReferenceGraphQueryIndex`（同目录）都在用；`IAdvancedUnitAnalysisCache` 这个拟名**没有采用**，高级分析走
>   `IAdvancedUnitAnalysisProducer`。
> 仍然有效、应当照做的：§5 的迁移顺序口径（"先统一生命周期、再删接口"）、§6 的验证清单、§4.3 的拆分方向。
> 仍然有效且**未复核**的是删除流程协调边界那一条（§7 最后一句），以及 Manager 侧"删除后清理是否已完全统一"。

## 1. 结论摘要

当前系统处于“主 UI 已统一共享中心，但删除和兼容边界仍在收敛”的状态：

- `IModInformationCenter` 已经能够统一编排 `FileFacts`、`AssetInventory`、`ReferenceGraph`、`UnitVersion`、`AdvancedUnitAnalysis`、`Thumbnail` 和 `MaintenanceAnalysis`。
- Manager 主 UI 已使用单一共享 `IModInformationCenter`；详情页、部署服务和主要 Manager 流程均通过该实例工作。
- `CoreServices` 中的 obsolete 无中心工厂仍保留，但仅作为测试、隔离和兼容入口；主 UI 不使用这些入口。
- `ReferenceGraph` 由信息中心生产，并通过 `IReferenceGraphIndexWriter` 写入 SQLite 反向索引；`SqliteModFactsStore` 仍保留查询、写入、删除职责。
- `ModLibraryService`/信息中心负责派生缓存及索引失效；Manager 不直接删除 SQLite 或 FileFacts，删除协调仍有分散边界。
- 当前失效隔离依靠 generation，以及节点取消 token 与节点身份校验；不存在独立的 epoch 计数器。

当前不能做的事情：

1. 不能直接删除 `IModContentFactsService`；它仍作为 `AssetInventory` producer/适配器及兼容测试边界保留。
2. 不能直接删除 `IModFactsStore` 或 `SqliteModFactsStore`；高级资产查询、材质交付、导入回滚和若干测试仍依赖它。
3. 不能只修改某一个服务的构造函数而不修改 `CoreServices`；否则 Manager 仍可能创建多个中心实例。

> ⛔ 已过时（2026-11-08）：第 1、2 条**都已经做了** —— 这两个接口与 `SqliteModFactsStore` 在代码里已不存在（见篇首横幅）。
> 当前真正还成立的只有第 3 条：改服务构造函数必须同步改 `CoreServices`，否则会多出中心实例。

## 2. 新实现当前已经具备的能力

### 2.1 信息中心

实现位置：`HD2ModCore/Infrastructure/ModInformationCenter.cs`

已经具备：

- 按信息产品分别请求和缓存；
- `Fresh`、`Cached`、`Stale`、`Failed`、`Unavailable` 状态；
- 相同 Node、信息类型和 generation 的并发请求合并；
- 旧缓存作为 stale fallback；
- 节点失效后阻止旧任务重新发布结果；
- JSON 派生缓存和 `ModDataIndex`；
- `ReferenceGraph` 结果写入 `IReferenceGraphIndexWriter` 提供的 SQLite 反向索引；
- 通过 generation 与节点取消 token/身份校验隔离失效请求；不存在独立 epoch 计数器；
- 节点删除时的缓存清理入口。

### 2.2 已经接入中心的主要功能

| 功能 | 当前状态 | 说明 |
| --- | --- | --- |
| 基础部署 | 已接入 | `ProfileApplyService` 使用 `FileFacts`，不应依赖 SQLite 或深度分析 |
| Mod 详情资产标签 | 已接入 | 直接请求 `AssetInventory`，不再只依赖旧 `AssetSummary` |
| Mod 详情 Unit 版本 | 已接入 | 使用中心 `UnitVersion` |
| ModDataIndex | 已接入 | 中心缓存命中时可恢复索引摘要 |
| 导入服务 | 部分接入 | 可注入中心；缺省路径会自行创建中心 |
| 普通库派生 | 已接入 | 通过共享中心请求 `AssetInventory`，由信息中心和 `ModLibraryService` 负责派生缓存及失效 |
| 配置预计覆盖图 | 已接入 | 通过共享中心请求 `AssetInventory` |
| 用户状态 | 已接入 | 通过共享中心请求 `AssetInventory` |
| 高级分析 | 已接入 | `AdvancedModAnalysisService` 由中心驱动；obsolete 无中心工厂仅保留兼容/隔离用途 |
| ReferenceGraph 材质诊断 | 已接入中心生产 | 信息中心生产并经 `IReferenceGraphIndexWriter` 写入 SQLite 反向索引 |

## 3. 仍在使用旧实现的地方

> ⛔ 已过时（2026-11-08）：本节两张表列的"旧用法"**已全部消失** —— `IModContentFactsService` 与
> `IModFactsStore` 都不在代码里了。保留作迁移过程的记录；要看现在谁在读什么，直接看当前代码。

### 3.1 旧内容事实服务的兼容边界

旧接口：`IModContentFactsService`（旧的 `GetLibraryFactsAsync` 全库入口不再是当前业务消费者路径）

| 文件/组件 | 当前旧用法 | 应迁移到 |
| --- | --- | --- |
| `GameDataArchiveBrowserService` | 通过信息中心请求 `AssetInventory` | 继续使用共享中心 |
| `LibraryDerivedDataService` | 通过信息中心请求 `AssetInventory` | 继续使用共享中心 |
| `ModUserStatusService` | 通过信息中心请求 `AssetInventory` | 继续使用共享中心 |
| `ProfileOverrideGraphService` | 通过信息中心请求 `AssetInventory` | 继续使用共享中心 |
| `ModContentFactsService` | `AssetInventory` 的 producer/适配器 | 暂时保留为中心内部兼容 producer |
| `IAssetInventoryProducer` | 中心使用的 producer 接口 | 保持中心内部边界 |
| `DerivedStateCoordinator` | 保存共享 `IModInformationCenter` | 不直接持有旧内容事实服务 |

### 3.2 obsolete 无中心工厂与共享中心

文件：`HD2ModCore/Infrastructure/CoreServices.cs`

当前状态：

- 主 UI 组合根由 `ShellViewModel` 创建并持有一个共享 `IModInformationCenter`，并将其传给库、导入、派生状态和部署相关服务；
- 各主要服务均有接收共享中心的工厂重载；
- obsolete 无中心便捷工厂仍会为测试、隔离或兼容场景自行组装中心，不能作为主 UI 生命周期依据；
- SQLite facts store 仍由信息中心用于 ReferenceGraph 反向索引写入，并由查询/删除路径使用，不等同于旧业务消费者回退；

当前目标/剩余边界：

- Manager 组合根只创建一个应用级 `IModInformationCenter`（已达到）；
- 所有需要中心的服务均通过构造函数接收该实例；
- `CoreServices` 的 obsolete 无中心便捷工厂只允许测试、一次性命令行或明确隔离的独立场景使用；
- 主 UI 不再调用这些无中心工厂。

### 3.3 仍直接使用 SQLite facts store 的地方

| 文件/组件 | 当前用途 | 是否能立即删除 |
| --- | --- | --- |
| `AdvancedModAssetQueryService` | 读取完整分析、资产表和反向引用 | 不能 |
| `MaterialDeliveryFactsService` | 查询材质相关 facts | 不能 |
| `ProfileMaterialDiagnosticsService` | 通过中心生产 `ReferenceGraph`，必要时查询 SQLite 反向索引 | 不能，需继续收敛查询边界 |
| `ModLibraryImporter` | 导入失败回滚时协调遗留清理 | 暂不能，需进一步统一删除流程 |
| `ModLibraryManager` | 管理库节点和文件删除，不直接删除 SQLite/FileFacts | 保留，派生失效由中心/库服务协调 |
| `ModLibraryService.Remove` | 调用 Manager 删除节点并请求信息中心失效 | 暂不能，删除协调边界仍需统一 |
| `CoreServices.CreatePatchGroupAnalysisProvider` | 分析 provider；不代表正常生产写入旧快照 | 保留兼容路径 |
| `CoreServices.CreateAdvancedModAnalysisCacheStore` | 历史兼容构造/测试边界 | 保留，正常生产链不使用旧快照 |

## 4. SQLite 的准确清理边界

> ⛔ 已过时（2026-11-08）：`SqliteModFactsStore` 这个类型已经不存在，本节"哪些要保留/哪些不能删表"的判断已失效；
> 全仓检索 `mod_fact_snapshots` / `mod_assets` / `asset_references` 均为 0 处。§4.3 的目标拆分则已经落地
> （`IModInformationCache` + `IReferenceGraphQueryIndex`）。

`SqliteModFactsStore` 当前是兼容性存储和反向索引实现，不是信息中心 JSON 缓存的替代品：

### 4.1 可以由信息中心替代的部分

- `ReferenceGraph` 通过 `IReferenceGraphIndexWriter` 写入的反向索引；
- 查询、写入和删除职责，以及仍在迁移中的兼容数据路径。

### 4.2 暂时必须保留的部分

- `mod_fact_snapshots`、`advanced_mod_analysis_snapshots`：仅作为遗留兼容 schema 保留，目前无正常生产读写链；不要删除表；
- `mod_assets`：高级资产表仍读取；
- `asset_references`：材质交付、消费者查询和部分诊断仍读取；
- `FindConsumerFactsAsync`：反向引用查询仍由 SQLite 索引提供。

### 4.3 目标拆分

最终不应继续保留一个含义模糊的 `IModFactsStore`。建议拆成：

1. `IAdvancedUnitAnalysisCache`：仅保存高级分析产品；
2. `IReferenceGraphQueryIndex`：仅支持反向引用查询；
3. `IModInformationCache`：保存中心统一管理的 JSON 产品；
4. 删除普通业务对 `IPatchGroupAnalysisCacheStore` 的依赖。

## 5. 推荐迁移顺序

### 第 1 批：统一中心生命周期（P0）

目标：先消除“同一应用多个中心”的根因，不改变业务结果。

1. 为所有主要 `CoreServices` 工厂增加显式 `IModInformationCenter` 参数；
2. 修改 `ShellViewModel`，将同一个中心传给 `ModLibraryService`、`DerivedStateCoordinator`、导入、部署、状态和详情相关服务；
3. 修改 `CreateModSameKeyReconstructionService`、`CreateAdvancedModAnalysisService`、`CreateProfileOverrideGraphService`、`CreateModUserStatusService`、`CreateProfileApplyService`；
4. 让主 UI 不再调用会自行创建中心的便捷工厂；
5. 添加/运行“同一组合根只创建一个中心”的构造和行为测试。

验收：同一个 Mod 的并发请求能够命中同一中心任务和同一缓存。

### 第 2 批：确认普通业务使用共享中心（P0，主要完成）

按以下顺序改造：

1. `LibraryDerivedDataService`；
2. `ProfileOverrideGraphService`；
3. `ModUserStatusService`；
4. `GameDataArchiveBrowserService`；
5. `DerivedStateCoordinator`。

当前各服务应并已基本做到：

- 只依赖 `IModInformationCenter`；
- 请求明确的 `AssetInventory`；
- 不再由业务层直接调用旧的 `GetLibraryFactsAsync` 全库入口；
- 单个 Mod 分析失败时保留其他 Mod 结果；
- 不因 `AssetInventory` 失败阻断基础部署或 JSON 配置保存。

验收：生产业务通过共享中心请求 `AssetInventory`；旧接口仅保留在中心内部 producer/适配器和兼容测试边界。

### 第 3 批：统一删除和导入回滚（P1）

1. 为中心补充或明确“删除节点全部派生缓存”的统一操作；
2. `ModLibraryService.Remove` 与 `ModLibraryManager` 的节点/文件删除和中心失效边界仍需统一；当前 Manager 不直接删除 SQLite/FileFacts；
3. 删除逻辑改为调用统一清理协调器；
4. `ModLibraryImporter` 回滚只清理本次导入产生的派生数据，不直接依赖普通 facts store；
5. 保证删除顺序为：JSON 权威状态提交、节点请求失效、派生缓存清理、文件目录删除、发布事件；具体事务失败必须可重试。

验收：删除后不会出现旧任务重新写回缓存；SQLite 若不可用也不影响基础删除和 FileFacts 清理。

### 第 4 批：迁移 ReferenceGraph 和高级查询（P1/P2）

1. `ProfileMaterialDiagnosticsService` 已通过中心请求 `ReferenceGraph`；
2. `MaterialDeliveryFactsService` 已通过中心 ReferenceGraph/反向索引查询；
3. `AdvancedModAssetQueryService` 读取中心 `AdvancedUnitAnalysis` 和 ReferenceGraph 查询结果；
4. 将消费者反向查询从混合 `IModFactsStore` 拆成明确接口；
5. 保持 `mod_fact_snapshots`/`advanced_mod_analysis_snapshots` 作为遗留兼容 schema，不删除表；正常生产链不再依赖其读写。

验收：材质诊断和高级资产表不再通过 `IModContentFactsService` 触发普通分析，且高级缓存与普通缓存独立失效。

### 第 5 批：移除旧接口和 SQLite 遗留职责（P2）

只有完成前四批并确认无生产调用后，才执行：

1. 删除业务层对 `IModContentFactsService` 的依赖；
2. 将 `ModContentFactsService` 降为中心内部 producer，之后删除旧公共接口；
3. 删除 `IModFactsStore` 中普通 patch facts 相关方法；
4. 将 SQLite 查询/写入/删除职责拆分为明确的反向索引和其他专用接口；
5. 删除无调用的 `CreateModContentFactsService`、旧构造函数和兼容重载；
6. 在完成兼容期和数据确认后，最终处置遗留 SQLite schema；当前不得删除 `mod_fact_snapshots` 或 `advanced_mod_analysis_snapshots` 表；
7. 最后再考虑删除 `SqliteModFactsStore` 本体。

## 6. 每一批必须执行的验证

### 编译和测试

- `HD2ModCore.Tests` 全量测试；
- 信息中心、缓存删除、索引、部署隔离相关测试；
- Manager 编译和启动验证；
- 删除 Mod 后重新导入同名/同路径 Mod 的回归验证。

### 静态调用检查

每批完成后检查以下旧入口数量：

- `GetLibraryFactsAsync`：目标为业务层 0，仅中心内部 producer/适配器或兼容测试边界保留；
- `CreateModContentFactsService`：目标为测试或中心内部保留，主 UI 为 0；
- `CreateModInformationCenter(paths)`：目标为组合根单点调用；
- `CreateModFactsStore`：目标为高级索引/迁移兼容路径，普通 UI 为 0；
- `IModContentFactsService`：目标为中心内部 producer/适配器接口，业务消费者为 0；
- `IModFactsStore`：目标为拆分后的高级/反向索引专用接口，普通管理为 0。

### 用户操作边界

| 操作 | 应使用 | 不应使用 |
| --- | --- | --- |
| 导入成功 | `FileFacts` | SQLite、ReferenceGraph、AdvancedUnitAnalysis |
| 普通库刷新 | `FileFacts` + `AssetInventory` | 旧全库 `GetLibraryFactsAsync` |
| 配置预计覆盖 | `AssetInventory` + mapping | 高级分析 |
| 基础部署 | `FileFacts` | AssetInventory、SQLite、Adaptation 深度分析 |
| 材质诊断 | `ReferenceGraph` | 普通内容事实旧入口 |
| 高级资产表 | `AdvancedUnitAnalysis` + 引用查询 | 混合旧 facts store |
| 删除 Mod | 中心统一失效/清理 | UI 手动删除多套缓存 |

## 7. 当前完成判定

当前已达到“可进行定向人工测试”条件，但仍不能宣称“全面迁移完成”：

- 生产业务不再直接调用 `GetLibraryFactsAsync`；
- 主 UI 全部使用同一个共享 `IModInformationCenter`；
- 删除流程不再由多个组件分别清理缓存（尚未完成）；
- `IModFactsStore` 已按高级缓存和反向查询职责拆分；
- SQLite 不再承载普通信息中心产品；
- Core 和 Manager 全量测试通过；
- 基础部署在 SQLite、AssetInventory 和高级分析不可用时仍能独立完成；
- 旧接口、旧构造函数和旧工厂均已通过静态搜索确认无业务调用。

全面迁移仍待：删除流程统一、旧 schema 最终处置、obsolete 无中心工厂未来移除。

> ⛔ 已过时（2026-11-08）：这行"仍待"的三项里，**旧 schema 最终处置**与 **obsolete 无中心工厂移除**都已做完
> （schema 表名 0 处；`CreateModContentFactsService` / `CreateModFactsStore` / `CreateAdvancedModAnalysisCacheStore` 已删）。
> 只剩**删除流程统一**这一项未复核，别再把另外两项当待办。
