# 外部包认领（External Package Adoption）

日期：2026-11-09（2026-11-12 按评审意见修订）　状态：已实现

## 要解决的问题

从别的 mod manager 拷进 `mods\` 的包不被识别。实测一个 41 个文件夹的库只收录了 26 个。

**根因不是格式不支持**，是重扫只认一层：

- `ModLibrarySynchronizer.InspectAsync` 用 `EnumerateDirectories(root, "*", TopDirectoryOnly)` 枚举库根；
- `DiscoverPatchGroups` 经 `ModNodePayloadLayout.EnumeratePayloadFiles` 只读「节点根 + `sectional/`」，不递归；
- 拿不到 patch 且没有 logical marker 就**静默 `continue`**，零日志。

而导入链路本来就是递归且完整的（`ObjectTreeImporter` 递归、`ModLibraryImporter.PlanManifestImport` 按 `Options/Include` 建 Option 节点、`StandardModManifest` 早就能读第三方 manifest）。所以缺的不是解析，是「认领」这一步。

## 判据（`ExternalPackageAdoptionPlanner`）

一个顶层目录会被认领，当且仅当它满足：

| 条件 | 说明 |
|---|---|
| 名字不以 `.` 开头 | 库根上的点目录全是管理器记账目录：`.hd2modmanager*`、`.runtime`（形状恰好也是 `…/9ba…patch_0`） |
| 不是 `sectional` / `vpk2patch` / `Overwrite` | 转换产物的层目录 |
| 不在待删排除集里 | 已登记删除 |
| 没有 `.hd2modmanager-mod.json` | 曾经是受管 Mod，布局本来就是扁平的 |
| 没有 `.hd2modmanager-logical.json` | 重扫已经能收 |
| **子树里有 base patch** | 判据就这一句。本层有没有**不影响**认领 |
| 不是 reparse point；文件数 ≤ 200000 | junction 的移动与硬链接都不可靠；超限疑似不是 Mod 包 |

### 「本层也有 patch」为什么也要认领

旧判据是「本层没有、子树里有」，于是 `包名/patch_0` + `包名/选项/patch_0` 被当成正常 Mod 收下，
子目录里的选项既不进库也不报错。**而这恰好是 `ModFolderTreeExporter`（导出为文件夹）的产物形状**——
用户把导出包解压丢进 `mods\`，得到的就是这个结果。

现在这种形状也认领：本层内容作为 Mod 本体，子目录各成一个选项（`PlanHeuristicImport` 本来就这么做）。
结果里会带一条 warning，说明哪些内容来自子目录。

### 已经被收进库的目录只报告、不认领

如果该目录在库快照里已经有节点（典型来源：它本层有 patch，启动同步先把它当普通 Mod 收了），
规划器**不会**认领它，只报一条 `Skipped`：

> 已作为普通 Mod 收录，但子目录里还有 N 个归档没进库（重扫只读节点根与 `sectional/`）。

为什么不动手：认领会给同一批内容再建一套节点，旧节点连同它的启用状态、配置条目引用一起作废。
对刚拷进来的包无所谓，对用了几个月的 Mod 就是数据丢失。要收全的话：

1. 把该文件夹移出 `mods\`；
2. 校验一次（条目会消失）；
3. 移回 `mods\`，再校验——这一次它是未受管目录，会按外部包认领。

### 不参与判定的东西

`ResolvePackageRoot` 的「唯一子目录 → 包根下移」**不参与**认领判定。它只决定导入时取哪个目录当根；
掺进来会让 `X/manifest.json + X/本体/` 与 `X/本体/` 两种形状走不同分支。

## 流程

```
判定候选 → 写 importing 标记 → 硬链接导入（逐单元记录已落库节点）
        → 写 imported 标记 → 源目录移进 <库>.adopted → 在**新位置**写 moved 标记
```

- **硬链接**：`ImportLibraryResidentFolderAsync(folder, preferHardLinks: true)`。一个包十几个选项、单个 `gpu_resources` 几百 MB，同卷硬链接等于不复制（实测链接数 = 2）。跨卷时 `CopyFile` 自动回落 `File.Copy`。
- **标记是状态机**，不是装饰：导入成功与移走源目录是两次独立磁盘操作，中间失败会让下一轮重扫**再导入一遍**（同一个包在库里出现两三份）。

| 标记状态 | 含义 | 下一轮行为 |
|---|---|---|
| `importing`（10 分钟内） | 正在导入 | 跳过（避免一轮同步自我重入） |
| `importing`（超时，带 nodeIds） | 崩在半路，已落库一部分 | **先回滚**那些节点，再重新导入 |
| `importing`（超时，无 nodeIds） | 崩在开头 | 删标记，重新导入 |
| `imported` | 全部导入完成，只差搬走 | **只补移动**，绝不重复导入 |
| `moved` | 连搬也做完了 | 节点还在库里 ⇒ 判为用户从副本目录还原，只报告不动手；节点没了 ⇒ 标记过期，重新导入 |

- **多单元导入的回滚**：一次导入可能由多个单元组成（包本身 + 包里的压缩包），提交是逐个进行的。
  `ImportLibraryResidentFolderAsync` 的 `onUnitCommitted` 回调让认领服务知道「已经落了哪几个单元」，
  失败或取消时调 `RollbackImportAsync` 把它们删掉。回滚失败则**保留**标记，下一轮先再回滚一次——
  绝不「删标记重来」，那正是重复节点的来源。
- **选项默认态**：走 `OptionActivationDefaultInitializer`，标准父节点下的直属选项**默认全开**，仍可单独关。
- **限流**：单轮最多 25 个包，超出的记 `Queued`（排队中），下次继续。**排队不是被拒**，界面与 CLI 分开统计。

## 源目录去哪了

`<库>.adopted\<yyyyMMdd-HHmmss>-<8位 guid>\<原包名>` —— 与库**同级**，不在 `mods\` 里面。

为什么不在 `.hd2modmanager-trash`：

- 那是「删 Mod」的回收站，`ModLibraryResidualCleanupService` 把它列进排除名单，`ModStorageDeletionService` 是「移进去 → 立刻删掉」；
- 认领把它当长期存放点，就等于让库根长期背着一棵嵌套树；
- 而且链路是坏的：用户删掉认领出来的节点，删的只是平铺那份，副本仍持有数据（硬链接数 2 → 1），空间永远不释放。

放到库外同级目录后，`mods\` 底下彻底没有嵌套目录，也不需要跟扫描/清理的排除名单长期绑定。
同卷 ⇒ 仍然是改名，硬链接照旧。

**保留期 30 天**，每次认领时顺手清一次过期批次（只认自己建的批次名 `yyyyMMdd-HHmmss-xxxxxxxx`，
用户往这个目录里放别的东西不会被误删）。

**为什么不直接删源目录**：`PersistFlattenedTree` 只拷顶层文件 + `sectional/` + 外部安装文件。
包里没进库的东西（预览图、说明 txt、没有 base patch 的目录）只有这份副本还在。

### 从副本目录还原

1. 关掉管理器；
2. 把 `<库>.adopted\<批次>\<包名>` 移回库根（原名或任意名）；
3. 在 `library.json` 里删掉对应的认领节点（或直接让下一次同步把重复目录当残留清掉）；
4. 重新同步。标记里是 `moved`，所以不会被再搬一次。

## 入口

**认领不是自动的。** 一次认领会导入十几个包、把每个选项默认全开、并把源目录搬出库——
这些都不该由「打开管理器」或「刷新一下」触发。

- **GUI**：库页「校验 Mod 库」→ 先**只读探测**（`DetectExternalPackagesAsync`）→ 有候选就弹确认框
  （列出包名、归档数、副本目录、保留期、以及不会处理的目录及原因）→ 用户点「是」才认领
  （`AdoptExternalPackagesAsync`）→ 然后才同步。
- **启动同步不认领**：`RunStartupChecksAsync` 走的是 `SynchronizeAsync`，它现在只做同步。
- **CLI**：
  ```
  hd2mod library adopt --library <库>            # 只列候选
  hd2mod library adopt --library <库> --confirm  # 执行
  ```
  协议版本 1.27 → **1.28**（新增 `library adopt` 能力）。

## 已知限制

- **容器目录名可能带 `_2` 后缀**。导入时源目录还占着原名，`CreateUniqueDirectoryName` 会给逻辑容器（无 payload 的空壳节点）加后缀。纯显示层面，不影响部署。
- 认领要求库目录**可写**（会在包根上写标记）。库放在 `Program Files` 下时需要管理员权限，否则会被判为「目录不可写」跳过并记日志。
- **已经被同步收进库的目录只能报告、不能自动收全**（见上文「已经被收进库的目录只报告、不认领」）。
- 只统计「本层 + 直接子目录（深度 ≤ 4）」的 base patch。patch 只躺在 `sectional/` 的目录不计入候选——
  那是节点自身的载荷布局，重扫读得到，不需要认领。
