# 删除 Mod 条目：慢与残留的实测报告与修复

日期：2026-10-27
范围：`HD2ModManager` 删除入口 → `ModLibraryService.RemoveManyAsync` → `ModLibraryManager` / `ModStorageDeletionService` / `ModInformationCenter` / `ModLibraryResidualCleanupService`

本文所有数字都在**当前实机数据**上测得，不是估算：

- Mod 库：`D:\SteamLibrary\steamapps\common\HD2ModManager\mods`，`library.json` 120 个节点
- 管理器根：`HD2ModManager\bin\Debug\net10.0-windows`
- 索引缓存：`data/indexes/mod-information` 955 个文件 / 30.6 MB；`mod-data-index.json` 8.9 MB；`mod-facts.sqlite` 20.8 MB

---

## 一、症状与结论

用户报告两条：删除慢；删除后残留。

| 症状 | 根因 | 状态 |
|---|---|---|
| 删除慢 | `JsonModFileFactsCache.DeleteNodeAsync` 为删一个节点把整个 `mod-information` 目录（955 文件 / 30.6 MB）全量打开并 JSON 解析一遍 | 已修，单节点 150 ms → **2 ms** |
| 文件夹残留 | `Directory.Delete(recursive:true)` 不是原子的：删掉能删的文件后在被占用的那个上失败，留下"空目录"或"目录 + 少量残余文件" | 已修（提示 + 重试 + 清扫） |
| 空文件夹堆到用户手动清 | 残留清扫器 `ModLibraryResidualCleanupService` 被放在 `if (!result.FilesystemChanged) return false;` **之后**，而一个无主目录永远不会让 `FilesystemChanged` 变 true —— 清扫器等于死代码 | 已修 |
| 含 json/文本的残留不被清 | 清扫器的白名单只认管理器自己的元数据文件，`mod.json` / `readme.txt` 之类一律保留 | 已修（放宽 + 记录明细） |
| 缓存/索引永久残留 | `InvalidateNodeAsync` 顺序调 4 个缓存，第一个抛异常后面三个都不执行；且 CLI 删除完全不碰信息中心 | 已修（逐个隔离 + 启动期 GC） |
| 磁盘只增不减 | `ModDataIndex.Persist()` 没有 `finally`，`.tmp` 泄漏 94 个 / 580 MB；替换备份 1.2 GB 无回收路径 | 已修（finally + GC） |

---

## 二、慢：删除一个 Mod 的实际开销

| 步骤 | 修复前 | 修复后 |
|---|---|---|
| `JsonModFileFactsCache.DeleteNodeAsync` | **150 ms 热 / 3694 ms 冷** | **2 ms**（首次迁移 765 ms，一次性） |
| `ModDataIndex.RemoveNodeAsync` | 60–85 ms（重写 8.9 MB） | **0 ms**（只删一个小文件） |
| `JsonModInformationCache.DeleteNodeAsync` | 4 ms | 4 ms |
| `SqliteModFactsStore.DeleteAsync` | ~10–30 ms | ~10–30 ms |
| 8 节点批量失效 | 1222 ms | **55 ms** |

基准（真实数据副本，`Release`）：

```
修复前                                         修复后
[A] 打开+解析全部 949 个文件  2913 ms          首次（旧缓存迁移）      765 ms
[B] DeleteNodeAsync ×8        1222 ms          DeleteNodeAsync ×8       55 ms
    （每个 136–175 ms）                            （首个 41 ms，其余 2 ms）
[D] ModDataIndex.RemoveNodeAsync ×8  486 ms    RemoveNodeAsync ×8        0 ms
[D] ModDataIndex ctor          154 ms          ctor                    167 ms（不变）
```

磁盘删除本身不是瓶颈：这些 Mod 每个只有 4–7 个文件（最大 2 GB），`Directory.Delete` 是纯元数据操作。

### 修复方式

`JsonModFileFactsCache` 的文件名只含 generation，不含节点归属，所以以前只能全量扫描。
现在额外维护 `mod-information/owners.index`（generation → 该文件覆盖的节点集合）：

- 删除：一次 O(1) 查表，只删命中的文件；
- 旧缓存（无 `owners.index`）：只对**未登记**的文件退回一次全量扫描，扫完把归属补登记，之后不再重复；
- 同目录里 `JsonModInformationCache` 的按节点文件按文件名正则直接排除，不再被误解析。

`ModDataIndex` 改成按节点分文件 `data/indexes/mod-data-index/<nodeid>.json`，删除只删一个小文件；
旧版单文件在首次启动时幂等迁移后删除。

`RemoveManyAsync` 的 `Chunk(4)` 并发改成串行——对"扫全目录"没有收益，而且并发删除同一批文件正是
`UnauthorizedAccessException` 的来源。

---

## 三、残留：六类可证的垃圾，合计约 1.9 GB

| 残留物 | 实测 | 生成机制 | 修复后 |
|---|---|---|---|
| `data/indexes/mod-data-index.json.*.tmp` | **94 个 / 580.8 MB** | `ModDataIndex.Persist()` 写临时文件后 `File.Move`，**没有 finally**，失败被 `catch(IOException)` 吞掉 | 新代码加 finally；旧残片由启动期 GC 清 |
| `data/mod-replacement-backups/` | **6 个目录 / 1203.5 MB，全部属于已删除节点** | `ReplaceStoredFilesAsync` 每次替换内容前整目录备份，没有任何删除调用 | 启动期 GC 按"节点是否还在库中"回收 |
| `data/indexes/mod-information/` | **102 个死节点 / 225 个文件 / 4.26 MB** | 逐节点失效；失效链断掉时整片留下；目录没有 GC | 启动期 GC + 失效链隔离 |
| `mod-data-index` 死条目 | 30027 条中 **4359 条属于 43 个死节点（14.5%）** | 只在失效链跑到时才删 | 按节点分文件 + GC |
| `mod-facts.sqlite` | **40 个死节点**（`patch_groups`/`mod_assets`），28 个在 `asset_references` | 同上 | 启动期 GC |
| `mods/net10.0-windows/` | 152 MB / 1369 文件 | 库目录里一份 2026-08-18 的旧便携安装 | 不是删除路径产物；现由残留清扫器覆盖（见下） |

死节点占比：`mod-information` 102/222、`mod-data-index` 43/155、`mod-facts` 40/151。
三个独立缓存给出同一个量级的泄漏率，说明不是偶发。

---

## 四、文件夹残留的完整链条（已复现）

用管理器自己的删除服务在隔离目录上复现：

```
=== 无占用
    elapsed: 68 ms   state=DeletedImmediately   dirExists=False   ✓

=== 目录里有文件被独占打开
    elapsed: 776 ms  state=PendingRestart
    detail=立即清理失败：Access to the path '...' is denied. 已登记 2 个路径等待系统重启后删除。
    removed=1        ← 库节点被删掉了
    dirExists=True   ← 目录还在
    trashExists=True ← 还留下一个空的 .hd2modmanager-trash/<guid>/ 事务目录
```

而 `Directory.Delete(recursive: true)` 的失败形态是**部分删除**（实测）：

```
锁定 file4.patch_0 后递归删除 → 抛出
删除后目录里只剩：file4.patch_0        ← 其余文件（含 mod.json / notes.txt）全被删掉了
```

两者叠加就是用户看到的形状：**文件夹还在、patch 已经没了、只剩几个 json/文本文件**。

链条：

1. 第一次删除：目录被占用 → 移动失败 → 登记 `PendingRestart`，库节点移除，**整个目录留在原地**。
2. 下次启动：`RecoverPendingAsync` 在**原路径**上重试 `Directory.Delete(recursive:true)`。
3. 此时大部分文件已可删，只剩个别小 json/文本仍被占用 → 递归删除删掉其余一切，在那个文件上失败。
4. 结果：目录 + 一个 json/文本。而这一步之后不会再登记重启删除（`RestartRequested` 已经是 true）。

### 为什么空文件夹一直堆到用户手动清

`ModLibraryResidualCleanupService` 本来就存在，判据也对（未被库引用 + 无 patch + 无外部资源），
但它被放在 `SynchronizeAsync` 的 `if (!result.FilesystemChanged) return false;` **之后**：

```csharp
var result = await _synchronizer.SynchronizeAsync(...);
LastSynchronizationResult = result;
if (!result.FilesystemChanged) return false;     // ← 无主目录永远走不到下面
...
var residualCleanup = CoreServices.CreateModLibraryResidualCleanupService().Cleanup(...);
```

`FilesystemChanged = hasSnapshotChanges || missing.Count != 0`，而一个无主残留目录既不是新增节点、
也不是变更节点、也不是丢失节点 —— 所以它**永远**是 `false`，清扫器一次都没跑过。

---

## 五、改动清单

### 正确性

1. `ModInformationCenter.InvalidateNodeAsync`：4 个持久化缓存各自 `try/catch`，全部尝试后聚合抛出。
   以前第一个抛异常（FileFacts 不 catch `UnauthorizedAccessException`）就跳过后面三个。
2. `ModLibraryService.InvalidateContentNodeAsync`：失败只记日志不抛，避免一次成功的库变更被缓存问题打断。
3. `JsonModFileFactsCache`：catch 补 `UnauthorizedAccessException` / `SecurityException`。
4. `ModStorageDeletionService`：移动失败时删掉空的事务目录；删除失败时把"剩余 N 项"写进错误信息。
5. `SynchronizeAsync`：残留清扫 + 索引回收移到 `FilesystemChanged` 早退**之前**。
6. `ModLibraryResidualCleanupService`：未被库引用、无 patch、无外部资源的目录，即使只剩作者附带的
   json/文本也清理；`Removals` 记录被删目录与其文件清单，写进日志可回溯。
   仍然保留 `.vpk` / `.ini` / `.lua` / 未知二进制等载荷目录。
7. 删除结果提示：`PendingRestart` / `PendingCleanup` 明确列出目录名，并说明"仍在占用磁盘"。
8. CLI `mod delete apply`：删除后执行一次索引回收（CLI 没有信息中心，这是唯一的兜底）。

### 性能

9. `JsonModFileFactsCache` 增加 `owners.index` 归属索引，删除从 O(全部文件) 降到 O(1)。
10. `ModDataIndex` 按节点分文件，删除不再重写 8.9 MB。
11. `RemoveManyAsync` 的批量失效改串行。

### 回收

12. 新增 `ModIndexGarbageCollector`：启动同步时以当前库快照为准，回收
    `mod-information` 死节点缓存、`mod-data-index` 死节点文件与旧版单文件、
    `mod-facts.sqlite` 死节点行、`mod-replacement-backups` 死节点目录、超过 10 分钟的 `*.tmp`。

### 测试

新增 20 条：`ModIndexGarbageCollectorTests`(8)、`ModStorageDeletionServiceTests`(4)、
`JsonModFileFactsCacheTests`(+2)、`ModDataIndexTests`(+2)、`ModInformationCenterTests`(+1)、
`ModLibraryResidualCleanupServiceTests`(+2，并把 `Cleanup_UnrecognizedFile_IsKept` 改成
`Cleanup_UnrecognizedBinaryFile_IsKept` 以反映新策略)。

全量：Decryptor 5、SourceModel 50、Adaptation 185、Core 795、CLI 175、SourceMigration 137(+1 skipped)，0 失败。

---

## 六、下次启动会发生什么

管理器每次启动都会跑一次同步，因此下一次启动会：

1. 重试所有 `pending-deletions.json` 里的项；
2. 清扫库根目录里所有无主残留目录（空目录、只剩 json/文本的目录）；
3. 回收派生索引里属于已删节点的缓存、索引、事实行、替换备份与 `*.tmp`。

按当前实测，第 3 步会回收约 **1.9 GB**（580 MB 临时文件 + 1.2 GB 替换备份 + 4 MB 死缓存 + 152 MB 旧安装目录）。
日志会写：

```
索引回收：缓存文件=N，索引节点=N，事实行=N，替换备份=N，临时文件=N。
模组库残留清理：已删除 N 个 … 残留目录。<目录名>（<其中的文件名>）
```

如果某次清理删掉了不该删的东西，日志里的目录名与文件清单就是回溯依据。
