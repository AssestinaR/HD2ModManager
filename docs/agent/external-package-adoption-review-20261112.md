# 外部包认领：评审意见落实记录

日期：2026-11-12　对应分支：`feat/external-package-adoption`（+ 单独拆出的 `fix/undeclared-payload-options`）

评审的结论是「契约不破」，问题集中在「嵌套目录会留在磁盘上」与「入口没有确认」。下面逐条记录改了什么、为什么这么改。

## P1　GUI 在启动路径上无确认就动手 → 改成显式动作

**改法**

- `ModLibraryService.SynchronizeAsync` **不再认领**。它恢复成「只做同步」，参数表里也不再出现认领相关的东西。
- 新增两个方法：
  - `DetectExternalPackagesAsync` —— 纯只读探测（只跑 `ExternalPackageAdoptionPlanner`），给界面用来先问一句；
  - `AdoptExternalPackagesAsync` —— 真正认领。
- 库页「校验 Mod 库」的流程变成：**探测 → 确认框 → 认领 → 同步**。
  确认框列出包名、归档数、副本目录、保留期，以及「看起来像但不会处理」的目录及原因。
- 认领后的对账基线（原来写在 `SynchronizeAsync` 里）改成一个私有字段 `_pendingAdoptionBaseline`，
  由下一次同步消费。这样既保住了「刚导入的节点不被当成外部新增」，又不用把认领塞回同步。

**没改的**：启动时仍然会同步（那是既有行为），但同步不再顺手搬包。

## P2　「导入成功但移动失败」永久留一份嵌套源目录 → 三态标记 + 真的只补移动

**改法**

标记状态从 `importing / imported` 扩成 `importing / imported / moved`：

| 状态 | 下一轮行为 |
|---|---|
| `imported`（待移动） + 节点还在库 | **只补移动**（`CompletePendingMove`），绝不重新导入 |
| `imported` + 节点已不在库 | 标记过期，删标记重新导入 |
| `moved` + 节点还在库 | 用户从副本目录还原的，只报告不动手 |
| `moved` + 节点已不在库 | 标记过期，删标记重新导入 |

**顺带修掉的一个真 bug**：`moved` 标记原先写在**已经被移走**的原路径上——`WriteMarker` 静默吞掉
`IOException`，于是副本目录里留着的是 `imported`。用户把它还原回库根时，会被判成「待移动」再搬一次
（`AdoptAsync_RestoredPackage_IsNotTrashedAgain` 就是靠这个失败暴露出来的）。现在标记写在移动后的新位置。

**文案与文档**：失败详情从「下一轮只重试移动」改成「标记已记为「待移动」，下次认领只补移动，不会重新导入」，
并且现在是真的；`docs/agent/external-package-adoption-20261109.md` 的流程表也按三态重写。

## P3　回收站只进不出 → 副本移出库外，加保留期

**改法**

- 新增 `StoragePaths.AdoptedArchiveDirectory` = `<库>.adopted`（与库同级，不在 `mods\` 里面）。
- 落点：`<库>.adopted\<yyyyMMdd-HHmmss>-<8位 guid>\<原包名>`。同卷 ⇒ 仍是改名，硬链接照旧。
- 保留期 30 天，每次认领顺手清一次；只删自己建的批次名（`yyyyMMdd-HHmmss-xxxxxxxx`），
  用户往这个目录里放别的东西不会被误删。清理结果走 `ExternalPackageAdoptionResult.Notes` 进日志。
- 从回收站还原的说明改成从副本目录还原。

**没有改的**：仍然是「移走」而不是「删掉」。`PersistFlattenedTree` 只拷顶层文件 + `sectional/` + 外部安装文件，
包里没进库的东西（预览图、说明 txt、没有 base patch 的目录）只有这份副本还在——所以不做
「按有没有残余分档删除」，一律保留，靠保留期收。

## P4　「本层有 patch、子树也有 patch」不认领 → 判据放宽 + 已收录的只报告

**改法**

- 判据从「本层没有 base patch，但子树里有」改成「**子树里有 base patch**」。本层有没有不再排除。
  这种形状会认领：本层内容作为 Mod 本体，子目录各成一个选项（`PlanHeuristicImport` 本来就这么做），
  并在结果里带一条 warning。这正是「导出为文件夹」产物解压进 `mods\` 的形状。
- `ScanSubtree` 现在同时给出「本层 base patch 数」与「子目录 base patch 数」，用来区分
  「正常 Mod」（本层有、子树没有 → 不认领）与「本层 + 子树都有」（认领 + 警告）。

**一处保守处理（评审允许的兜底）**：如果该目录**已经被同步收进库**（库快照里有同路径节点），
规划器只报一条 `Skipped`：

> 已作为普通 Mod 收录，但子目录里还有 N 个归档没进库（重扫只读节点根与 `sectional/`）。

不动手的原因：认领会给同一批内容再建一套节点，旧节点连同启用状态、配置条目引用一起作废。
对刚拷进来的包无所谓，对用了几个月的 Mod 就是数据丢失。文档里给了三步手动收全的办法
（移出 → 校验 → 移回 → 校验）。

**关于「patch 只在 `包名/sectional/`」**：那条路重扫读得到（`ModNodePayloadLayout` 本来就读 `sectional/`），
不会丢内容，所以规划器仍然把它排除在候选之外，文档里写明了原因。

## P5　多单元导入中途取消/失败会重复导入 → 逐单元记账 + 回滚

**改法**

- `IModLibraryImporter.ImportLibraryResidentFolderAsync` 增加 `onUnitCommitted` 回调：每提交完一个单元
  就回调一次该单元落库的全部节点 id。
- 认领服务把这些 id 逐个写进 `importing` 标记（进程被杀在半路也能恢复）。
- 失败或取消时调新增的 `IModLibraryImporter.RollbackImportAsync(ids)`：删目录 → 失效缓存 → 从快照摘掉节点，
  并清掉父节点 `Children` 里的悬空 id。
  - **顺序是先删目录、删不动就抛，绝不先改快照**：反过来的话目录还在而快照里没有节点，
    下一轮重扫会把它当「外部新增」重新收进来——正是回滚要避免的重复。
  - 回滚成功 → 删标记；回滚失败 → **保留**标记（写回 `importing` + 已提交 id），下一轮先再回滚一次。
- 过期的 `importing` 标记同理：先回滚标记里记着的节点，再重新导入。

## P6　单轮上限的文案 → 新增 `Queued`，与 `Skipped` 分开

- `ExternalPackageAdoptionStatus` 增加 `Queued`；超上限的包记 `Queued`，详情写「排队中：单轮上限 25 个，下次认领继续」。
- GUI 分开统计：`adopted / queued / failed / skipped` 四行；排队那一行明说「下次校验会继续认领」。
- CLI 汇总行变成「导入 N 个，排队 N 个，跳过 N 个，失败 N 个」，标记用 `+ … - !`。

## 合入前那两件小事

- **rebase 到 master 触发 CI**：本地 `master` 已经包含 `8669211`（`git merge-base master feat/…` = 分支头），
  所以分支无法再 rebase 到它上面。CI 需要在推分支之后由远端触发，这一步留给推送时做。
- **协议 1.27 → 1.28**：`CliProjections.ProtocolVersion` 已经是 `1.28`，`capabilities` 里
  `library adopt` 的描述也更新成了新的副本目录；release notes 里应当单独提一句「新增 `library adopt` 能力」。

## 附带那个修复：已拆成独立分支

`fix/undeclared-payload-options`，基于 `5238554`（认领提交之前），只含 payload 修复的 3 个文件：

- `HD2ModCore/Infrastructure/ModLibraryImporter.cs`（`PlanManifestImport` 收编未声明的 payload 目录）
- `HD2ModCore/Domain/ImportResult.cs`（`UnownedPayloadPaths`）
- `HD2ModCore.Tests/StandardManifestImportTests.cs`（回归用例）

认领服务里那 3 行（把 `UnownedPayloadPaths` 写进 detail）留在认领分支——那是认领的展示逻辑，
不属于 payload 修复本身。

## 验证

- `dotnet build -c Debug`：0 警告 0 错误
- `dotnet test -c Debug`：2131 通过 / 1 跳过 / 0 失败
- 新增/改写的用例（`ExternalPackageAdoptionServiceTests`）：
  - `AdoptAsync_PendingMoveMarker_OnlyMovesWithoutImportingAgain`（P2）
  - `AdoptAsync_PartialImportFailure_RollsBackCommittedUnits`（P5）
  - `AdoptAsync_RootAndNestedPatch_AdoptsAndWarns`（P4）
  - `AdoptAsync_ManagedDirectoryWithNestedPayload_IsReportedNotAdopted`（P4 保守分支）
  - `AdoptAsync_PrunesExpiredArchiveBatches`（P3）
  - `AdoptAsync_BeyondRoundLimit_IsQueuedNotRejected`（P6）
