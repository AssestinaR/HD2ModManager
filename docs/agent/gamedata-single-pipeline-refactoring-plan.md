# Game Data 单线路重构实施方案

## 1. 任务目标

将现有 Game Data 与 Patch 基础能力收敛为唯一线路：

```text
HD2ModManager
  -> HD2ModCore 应用服务
  -> HD2ModAdaptation 解析能力
  -> HD2ModCore SQLite 持久化与查询
  -> HD2ModManager 展示结果
```

最终只保留：

- `HD2ModAdaptation` 作为 Game Data package、TOC、payload 和 patch archive 的唯一二进制实现。
- `HD2ModCore` 作为索引构建、SQLite 持久化、缓存失效、进度、取消和诊断的唯一业务入口。
- `asset-index.sqlite` 作为唯一 Game Data 持久化索引。
- `HD2ModManager` 只调用 Core 的高层用例，不直接管理索引文件、元数据文件和缓存目录。

本任务不要求兼容旧的 JSON 索引缓存。它属于可重建派生数据，可以直接废弃。

## 2. 强制约束

工作代理实施时必须遵守：

1. 不允许 Manager 新增对 `HD2ModAdaptation` 的项目引用或类型引用。
2. 不允许 Core 新增第二套 Game Data 或 Patch 二进制解析实现。
3. Adaptation 不负责 SQLite、Manager 设置、UI 状态或应用目录策略。
4. Core 不自行解释 DSAR、bundled package、TOC entry 布局、LZ4 chunk 或 payload offset。
5. SQLite 是唯一持久化索引，不保留 `gamedata-archive-index.json`。
6. 索引失败必须结构化记录，不得使用无条件 `catch { continue; }` 静默忽略。
7. 索引构建必须支持取消和进度，不能阻塞 WPF UI 线程。
8. 修改已有文件时保留无关的用户改动，不进行无关重构。
9. 新增核心 C# 文件时，在 using 指令下方添加一句简短用途说明。

## 3. 当前需要移除的重复线路

### 3.1 删除 JSON 索引缓存线路

删除：

- `HD2ModCore/Infrastructure/CachedGameDataArchiveIndexProvider.cs`
- `HD2ModCore/Application/IGameDataArchiveIndexProvider.cs`，前提是完成调用迁移后没有其他必要用途
- `HD2ModCore/Infrastructure/GameDataArchiveIndexProvider.cs`，其构建职责应合并进新的 Core 索引应用服务
- `StoragePaths.GameDataArchiveIndexCachePath`
- `CoreServices.CreateGameDataArchiveIndexProvider`
- 与 `gamedata-archive-index.json` 专属的缓存 envelope、版本和指纹代码

不得删除 Adaptation 的 `GameDataArchiveIndex`、`GameDataArchiveFact`、`GameDataArchiveEntryFact` 和 `PatchAnalysisIssue`。这些是解析结果模型，可作为 Core 写入 SQLite 的输入。

### 3.2 删除 Core 的重复二进制实现

优先删除或迁移以下 Core 实现及其专属接口：

- `HD2ModCore/Infrastructure/GameDataPackageResolver.cs`
- Core 版本的 `PatchTocScanner`
- Core 版本的 `PatchEntryPayloadReader`
- 与 Adaptation 功能等价的 Unit mesh patch reader/editor
- Core 中承担 archive 二进制重建的 dry writer、planner 或 writer

删除前必须逐项搜索调用者。调用者按以下方式迁移：

- 业务编排保留在 Core。
- 二进制操作改为注入 Adaptation 接口或调用一个 Core 内部适配器。
- Core 对外应用接口不得暴露低层 scanner、offset、chunk 等实现细节。

不要机械删除仅负责通用文件部署、安全输出目录或业务计划的 Core 代码。只有与 Adaptation 二进制职责重复的部分需要移除。

## 4. 目标 Core API

新增一个高层应用接口，建议命名为 `IGameDataIndexService`。Manager 只依赖这个接口。

建议能力：

```text
GetStatusAsync(cancellationToken)
BuildOrRebuildAsync(progress, cancellationToken)
GetArchiveSummariesAsync(cancellationToken)
FindAssetArchivesAsync(assetKeys, cancellationToken)
VoteArchivesAsync(assetKeys, filterSettings, cancellationToken)
ClearAsync(cancellationToken)
```

具体 C# 命名可服从项目现有风格，但不得继续要求 Manager 传入：

- Game Data 目录字符串
- `archivehashes.json` 原始 JSON
- SQLite 路径
- 缓存目录路径

Core 应通过现有设置、`IGameDataLocator`、`StoragePaths` 和资产元数据 provider 自行获取这些依赖。

状态结果至少包含：

- 状态：缺少配置、缺少目录、未建立、最新、过期、部分成功、失败。
- Game Data 规范化路径。
- 建立时间。
- archive 总数、成功数、失败数、entry 总数。
- source fingerprint、schema version、parser version。
- 结构化问题列表或问题摘要。

构建结果至少包含：

- 是否完成、是否取消、是否部分成功。
- archive 与 entry 统计。
- 每个失败 archive 的错误代码和消息。
- 新索引的 fingerprint 与建立时间。

## 5. Adaptation 解析边界

Core 的索引服务必须通过 Adaptation 完成以下工作：

1. 枚举规范 package 名称。
2. 读取 Legacy、DSAR 和 bundled package TOC。
3. 判断 legacy/slim entry offset。
4. 将 TOC 转换为 `GameDataArchiveFact` 和 `GameDataArchiveEntryFact`。
5. 对不可解析 archive 产生 `PatchAnalysisIssue`。

推荐复用并完善：

- `HD2ModAdaptation/PatchReconstruction/IGameDataPackageResolver.cs`
- `HD2ModAdaptation/PatchReconstruction/GameDataPackageResolver.cs`
- `HD2ModAdaptation/Analysis/IGameDataArchiveIndexer`
- `HD2ModAdaptation/Analysis/GameDataArchiveIndexer.cs`

如果 Core 需要稳定注入入口，可以在 Core Infrastructure 创建一个很薄的适配器，但该适配器只能转换类型或调用 Adaptation，不得复制解析算法。

## 6. SQLite 目标模型

以现有 `asset-index.sqlite` 为基础升级 schema，不新建第二个数据库。

建议表结构：

### `index_metadata`

- `key TEXT PRIMARY KEY`
- `value TEXT NOT NULL`

至少写入：

- `schema_version`
- `parser_version`
- `built_utc`
- `game_data_directory`
- `source_fingerprint`
- `archives_total`
- `archives_indexed`
- `archives_failed`
- `entries_total`

### `archives`

- `archive_id TEXT PRIMARY KEY`
- `archive_hex TEXT NULL`
- `display_name TEXT NULL`
- `category TEXT NULL`
- `uses_slim_entry_offset INTEGER NOT NULL`
- `source_fingerprint TEXT NULL`
- `indexed INTEGER NOT NULL`

### `archive_entries`

- `archive_id TEXT NOT NULL`
- `entry_index INTEGER NOT NULL`
- `type_id INTEGER NOT NULL`
- `file_id INTEGER NOT NULL`
- `toc_data_offset INTEGER NOT NULL`
- `stream_offset INTEGER NOT NULL`
- `gpu_resource_offset INTEGER NOT NULL`
- `toc_data_size INTEGER NOT NULL`
- `stream_size INTEGER NOT NULL`
- `gpu_resource_size INTEGER NOT NULL`
- `unknown1 INTEGER NOT NULL`
- `unknown2 INTEGER NOT NULL`
- `unknown3 INTEGER NOT NULL`
- `unknown4 INTEGER NOT NULL`

建议约束和索引：

- 主键或唯一键：`(archive_id, entry_index)`。
- 查询索引：`(type_id, file_id)`。
- 查询索引：`(archive_id, type_id, file_id)`。
- 查询索引：`(type_id)`。

### `archive_issues`

- `id INTEGER PRIMARY KEY`
- `archive_id TEXT NOT NULL`
- `code TEXT NOT NULL`
- `message TEXT NOT NULL`
- `created_utc TEXT NOT NULL`

原有 `assets` 和 `asset_archives` 表可以在完成查询迁移后删除。资产出现次数优先使用 SQL 聚合查询；只有基准测试证明性能不足时，才增加可重建统计表或视图。

所有 `ulong` AssetKey 和 offset 写入 SQLite 时必须沿用项目中经过测试的无符号位模式转换，避免大于 `long.MaxValue` 的 ID 损坏。

## 7. 指纹与事务策略

只保留一种 source fingerprint 算法，由 Core 定义并写入 SQLite。

指纹至少覆盖：

- 规范化 Game Data 路径。
- 实际参与索引的 package 名称。
- 影响 package 内容的底层文件名称、大小和最后修改时间，或 Adaptation 提供的 package fingerprint。
- 资产元数据版本或 `archivehashes.json` 内容摘要。
- Adaptation parser version。
- SQLite schema version。

构建必须使用临时数据库或完整事务：

1. 解析并写入新索引。
2. 记录所有 archive issue。
3. 完成 schema 和完整性校验。
4. 成功后原子替换正式数据库，或提交完整事务。
5. 取消或致命失败时保留旧的可用索引。

“部分成功”可以提交，但必须在 metadata 和结果中明确标记，并保留失败 archive 的 issue。状态检查不能把部分成功显示为完全正常。

## 8. Manager 迁移要求

修改 `HD2ModManager/ViewModels/PageViewModels.cs` 中的资产索引逻辑：

- 移除 `File.ReadAllText(_paths.ArchiveHashesPath)`。
- 移除对 SQLite 指纹和状态的同步等待。
- 移除 `ClearAssetAnalysisCache()` 及 ViewModel 直接删除目录的行为。
- 移除 ViewModel 对索引前置条件和失效规则的业务判断。
- 改为异步调用 Core 的 `IGameDataIndexService`。
- UI 仅负责展示 Core 返回的状态、进度、统计和问题摘要。
- `async void` 只允许保留在命令/事件边界；实际逻辑下沉到可等待的 `Task` 方法。

设置页的 Game Data 索引查看窗口也必须通过同一个 Core 服务查询，不得自行构造另一条索引访问路径。

索引重建完成后，需要失效的其他派生数据由 Core 统一处理。Manager 不知道具体缓存目录。

## 9. Patch 能力收敛要求

完成 Game Data 索引迁移后，继续收敛 Patch 基础能力：

1. `CoreServices` 不再公开 Core 版本的低层 `CreatePatchTocScanner`、`CreatePatchEntryPayloadReader` 和等价 writer 工厂。
2. Core 的检测、修复和依赖分析用例内部调用 Adaptation。
3. Manager 保持只调用 `IModUnitRepairService` 等 Core 用例接口。
4. Adaptation 的 `PatchArchiveWriter` 作为 archive 重建和写出的唯一标准实现。
5. Core 可以保留输出目录策略、备份、回滚、任务结果和库刷新，但不能保留另一套 TOC 重建算法。
6. 每迁移一组调用者后再删除对应旧接口和实现，确保中间提交可编译、可测试。

## 10. 推荐实施顺序

### 阶段 A：建立唯一索引入口

1. 增加 `IGameDataIndexService` 请求、状态、进度和结果模型。
2. 扩展 SQLite schema 以保存完整 archive entry 和 issue。
3. 让 Core 服务调用 Adaptation `GameDataArchiveIndexer`。
4. 将 Adaptation facts 在事务中写入 SQLite。
5. 迁移现有 SQLite 查询能力。

### 阶段 B：迁移 Manager

1. 状态页/设置页通过高层服务读取状态。
2. 重建命令只传进度和取消令牌。
3. 索引查看窗口通过同一服务读取 archive 摘要。
4. 删除 Manager 文件读取、目录删除和同步等待。

### 阶段 C：删除 JSON 线路

1. 搜索并迁移 `IGameDataArchiveIndexProvider` 的全部调用者。
2. 删除 cached provider 和 JSON cache path。
3. 删除已有 `gamedata-archive-index.json` 的生成逻辑。
4. 保证旧 JSON 文件即使留在用户目录也不会被读取；可由 Core 的一次性清理逻辑删除，但不能由 Manager 删除。

### 阶段 D：删除 Core 重复解析

1. 将 `AssetArchiveIndexService`、`ArchiveDependencyResolver`、`ArchiveUnitMeshReader`、兼容性分析和修复服务迁移到 Adaptation resolver/scanner。
2. 删除 Core `GameDataPackageResolver`。
3. 迁移并删除 Core 重复 scanner、payload reader 和 archive 重建实现。
4. 收缩 `CoreServices`，只公开应用级服务。

### 阶段 E：验证与清理

1. 删除失去调用者的接口、DTO、工厂和测试夹具。
2. 更新总体说明和相关架构文档中的调用链。
3. 执行全量测试和真实样本烟雾测试。
4. 检查仓库中不再存在第二套 Game Data/Patch 二进制实现。

## 11. 测试要求

至少补充或调整以下测试：

- Adaptation 能索引 Legacy package。
- Adaptation 能索引 DSAR/slim package。
- Adaptation 能索引 bundled package。
- 无效 TOC 产生结构化 issue，不导致整个索引静默缺项。
- SQLite 可保存并回读所有 `GameDataArchiveEntryFact` 字段。
- 大于 `long.MaxValue` 的 TypeID/FileID 往返不变。
- `(TypeID, FileID)` 能查询到正确 archive。
- archive summary、类型查询和 vote 查询结果与迁移前一致。
- fingerprint 未变化时不重建。
- Game Data、元数据、parser version 或 schema version 变化时判定过期。
- 取消构建后旧数据库仍可查询。
- 致命失败后旧数据库仍可查询。
- 部分 archive 失败时状态为部分成功且 issue 可查询。
- Manager 状态刷新不阻塞 UI 线程。
- 旧 JSON 缓存存在时不会被读取。

优先运行：

- `HD2ModAdaptation.Tests`
- `HD2ModCore.Tests`
- `HD2ModManager` 编译

最终还需使用仓库 `_diagnostics` 下已有 Game Data/Patch 样本执行一次索引烟雾测试，并核对 archive 总数、失败数和关键 AssetKey 查询结果。

## 12. 完成标准

只有同时满足以下条件才算完成：

- 生产调用链只有 `Manager -> Core -> Adaptation -> SQLite`。
- 仓库不再生成或读取 `gamedata-archive-index.json`。
- Core 不再包含 Game Data package、DSAR、LZ4 chunk 或 bundled package 解析实现。
- Game Data 索引只扫描一次并写入唯一数据库。
- Manager 不直接读取索引元数据文件，不删除缓存目录，不同步阻塞异步索引调用。
- archive 解析失败具有可查询、可展示的结构化诊断。
- 取消或失败不会破坏旧索引。
- Core 和 Adaptation 测试通过，Manager 编译通过。
- `git diff --check` 通过。

## 13. 工作代理交付格式

工作代理完成后应报告：

1. 删除、迁移和新增了哪些文件。
2. 最终唯一调用链及关键接口。
3. SQLite schema 的实际变化。
4. 哪些 Core 重复解析实现已删除，哪些因现有调用者暂时保留以及原因。
5. 测试命令、通过数量和失败详情。
6. 真实样本烟雾测试的 archive 总数、成功数、失败数及 issue 摘要。
7. 仍存在的技术债务，不得将未完成项描述为已完成。