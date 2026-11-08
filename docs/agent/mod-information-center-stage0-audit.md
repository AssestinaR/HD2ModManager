# Mod 信息中心阶段 0 调用审计

> 审计时间：2026-07-23。本文记录当前代码中的实际调用关系，不代表目标架构已经完成。

> ⛔ 已过时（2026-11-08）：这里的"当前"指 2026-07-23。三个名字此后变了，矩阵里按旧名读会扑空：
> `ModUserStatusService` → `ModUserStatusProjector.Project`（`HD2ModManager/Services/DerivedStateCoordinator.cs`）；
> `CachedPatchGroupAnalysisProvider` → `ModInformationPatchGroupAnalysisProvider`；
> `ImportAnalysisGate` 已删除（源码 0 命中，"只覆盖导入入口"这条问题因此不再以此形式存在）。
> 其余入口名（`ProfileApplyService` / `ModLibraryService` / `DerivedStateCoordinator` /
> `IModInformationCenter` / `IModFileFactsProducer` 等）仍在，矩阵的其余部分仍是 2026-07-23 的快照。

## 调用矩阵

| 入口 | 当前生产/读取 | 范围 | SQLite | Adaptation/深度解析 | 失败影响 |
|---|---|---|---|---|---|
| `ProfileApplyService.ApplyAsync` | 通过 `ModContentFactsService` 获取 `ModContentFacts`，再投影文件索引 | 活动 Profile 成员 | 是（经缓存 Provider） | 是，DependencyGraph | 直接阻断部署 |
| `ModLibraryService.RefreshDerivedDataAsync` | `LibraryDerivedDataService` → `ModContentFactsService` | 全库或指定 NodeId | 是 | 是，DependencyGraph | 阻断本次派生刷新 |
| `ModLibraryService.RebuildIndex(true)` | 同上，同步等待 | 全库 | 是 | 是 | 可能阻断库加载 |
| `DerivedStateCoordinator.RefreshCoreAsync` | 先获取活动成员内容事实，再调用预计图和材质诊断 | 活动 Profile | 是 | 是 | 统一写入 `LastError`，影响派生快照 |
| `ProfileOverrideGraphService.BuildAsync` | 自行调用 `IModContentFactsService` | Profile 成员 | 是 | 是 | 预计图失败 |
| `ProfileMaterialDiagnosticsService.BuildAsync` | 直接读 `IModFactsStore`，必要时调用分析 Provider | Profile 成员 | 是 | 是 | 材质诊断失败 |
| `ImportService.AnalyzeImportedAsync` | 通过 `ModLibraryService.RefreshDerivedDataAsync` | 导入 NodeId | 是 | 是 | 导入后的后台派生失败 |
| `ModUserStatusService.GetStatusesAsync` | 自行调用内容事实，再调用预计图/部署图 | 当前实现由调用者决定，内容事实可全库 | 是 | 是 | 状态构建失败或局部降级 |
| `GameDataArchiveBrowserService.BuildAsync` | 读取内容事实并构建 Game Data 浏览快照 | 全库/传入快照 | 是 | 是 | 浏览快照失败 |
| `AdvancedModAnalysisService` | 独立高级缓存和 Full Provider | 单 Mod | 是 | 是，Full | 高级功能失败 |

## 已确认问题

1. 基础部署当前仍经由 `IModContentFactsService`，因此依赖 SQLite 与 Adaptation；这是阶段 2 的首要隔离目标。
2. `DerivedStateCoordinator` 与 `ModLibraryService` 都可以触发内容事实生产，前者还会让 `ProfileOverrideGraphService` 再次请求同一事实。
3. `ImportAnalysisGate` 只覆盖导入入口，不能覆盖库刷新、预计图、部署和状态入口。
4. `PatchFileIndexBuilder` 已具备纯文件系统扫描能力，且不访问 SQLite 或 Adaptation，适合作为部署 FileFacts 的第一版生产器。
5. `ModLibraryService.Remove` 已清理 SQLite facts，但没有统一的信息中心删除协议；后续应迁移到中心仓库删除。

## 阶段 0.5 边界结论

- 导入落库可以在 `buildDerivedData: false` 下完成；当前 Shell 后续异步分析仍会触发深度内容事实，应改为先请求 FileFacts、再后台请求更高等级。
- 元数据编辑路径使用 `buildDerivedData: false`，未发现必然 Patch 分析入口。
- Profile 编辑路径只写 JSON；预计图刷新可能异步触发内容事实，需后续迁移到共享 AssetInventory。
- `ProfileApplyService` 必须只依赖文件索引；部署不应调用任何内容分析服务。

## 并发观察

当前未发现统一请求去重层。`CachedPatchGroupAnalysisProvider` 的缓存命中检查与保存不是由全局中心串行协调；不同服务实例可同时执行相同 Mod 的分析并写 SQLite。阶段 1 将增加按 NodeId、信息等级和 generation 合并请求的中心入口，阶段 2 先确保部署完全绕开该链路。

## 已完成的首轮落地

- 新增 `IModInformationCenter`、`IModInformationRequest`、`ModInformationResult<T>` 和信息等级枚举。
- 新增 `ModInformationCenter`，对 FileFacts/AssetInventory 提供统一入口，并对相同请求合并进行中任务；调用者取消只取消等待，不取消共享生产任务。
- 新增 `IModFileFactsProducer` / `ModFileFactsProducer`，明确 FileFacts 的纯文件系统生产边界。
- `ProfileApplyService` 现在只通过信息中心请求 `FileFacts`，部署路径不再调用 `ModContentFactsService`、Adaptation 或 SQLite。
- `ImportService.AnalyzeImportedAsync` 现在通过信息中心请求单 Mod `AssetInventory`，不再使用独立的全局分析闸门或直接刷新旧内容事实链。
- `ProfileOverrideGraphService` 与 `DerivedStateCoordinator` 已改为共享同一个信息中心实例请求 `AssetInventory`，避免预计图和派生刷新各自启动同一层生产。
- `LibraryDerivedDataService` 的生产组装已接入信息中心，普通库派生刷新不再在生产配置中直接启动旧内容事实入口。
- 信息中心已增加统一诊断事件，记录来源、信息等级、generation、耗时、合并状态、结果状态和 issues。
- `ModUserStatusService` 已改为通过信息中心请求 `AssetInventory`，不再由生产配置直接触发全库旧内容事实入口。
- FileFacts 已增加可选 JSON 持久缓存：按文件 generation 命名、成功后临时文件原子替换；缓存读写失败自动回退文件系统生产。
- Mod 删除时已清理 FileFacts JSON 缓存，原有 SQLite facts 清理仍保留。
- 新增并发去重测试；部署聚焦测试 9/9 通过，信息中心测试 1/1 通过，Manager 构建成功。

当前仍未完成：持久化信息仓库、generation 缓存状态、其他业务入口迁移、AssetInventory/ReferenceGraph 拆分，以及全库生命周期清理协议。
