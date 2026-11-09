# 外部包认领（External Package Adoption）

日期：2026-11-09　状态：已实现（P0–P3）

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
| 不在待删排除集里、库里没有同路径节点 | 已受管 |
| 没有 `.hd2modmanager-mod.json` | 曾经是受管 Mod |
| 没有 `.hd2modmanager-logical.json` | 重扫已经能收 |
| **根上没有 base patch** | 有的话就是正常 Mod，重扫本来就能收 |
| 深度 ≤ 4 的子树里有 base patch | 判据就这一句：**本层没有，子树里有** |
| 不是 reparse point；文件数 ≤ 200000 | junction 的移动与硬链接都不可靠；超限疑似不是 Mod 包 |

> `ResolvePackageRoot` 的「唯一子目录 → 包根下移」**不参与**认领判定。它只决定导入时取哪个目录当根；掺进来会让 `X/manifest.json + X/本体/` 与 `X/本体/` 两种形状走不同分支。

## 流程

```
判定候选 → 写 importing 标记 → 硬链接导入（同卷）→ 写 imported 标记 → 源目录移进 .hd2modmanager-trash
```

- **硬链接**：`ImportLibraryResidentFolderAsync(folder, preferHardLinks: true)`。一个包十几个选项、单个 `gpu_resources` 几百 MB，同卷硬链接等于不复制（实测链接数 = 2）。跨卷时 `CopyFile` 自动回落 `File.Copy`。
- **标记是状态机**，不是装饰：导入成功与移走源目录是两次独立磁盘操作，中间失败会让下一轮重扫**再导入一遍**（同一个包在库里出现两三份）。
  - `importing` + 10 分钟内的标记 → 跳过（避免一轮同步自我重入）
  - `imported` + 有 nodeIds → **只补移动**，绝不重复导入
  - 导入抛异常 → 删标记、保源目录
- **选项默认态**：走 `OptionActivationDefaultInitializer`，标准父节点下的直属选项**默认全开**（用户要求），仍可单独关。
- **限流**：单轮最多 25 个包，剩下的下次同步继续（幂等保证安全）。

## 入口

- **GUI**：`ModLibraryService.SynchronizeAsync` 在同步**之前**调用认领。认领后对账基线换成认领后的快照，并把认领出的 id 并进 `AddedNodeIds`（否则界面显示「新增 0 个」）。
- **CLI**：
  ```
  hd2mod library adopt --library <库>            # 只列候选
  hd2mod library adopt --library <库> --confirm  # 执行
  ```
  协议版本 1.27 → **1.28**。

## 从回收站还原

认领把源目录整体移进 `<库>\.hd2modmanager-trash\<事务 guid>\<原包名>`，内容是原始布局（未改动）。要还原：

1. 关掉管理器；
2. 把该目录移回库根（原名或任意名）；
3. 在 `library.json` 里删掉对应的认领节点（或直接让下一次同步把重复目录当残留清掉）；
4. 重新同步。

## 已知限制

- **容器目录名可能带 `_2` 后缀**。导入时源目录还占着原名，`CreateUniqueDirectoryName` 会给逻辑容器（无 payload 的空壳节点）加后缀。纯显示层面，不影响部署。
- 认领要求库目录**可写**（会在包根上写标记）。库放在 `Program Files` 下时需要管理员权限，否则会被判为「目录不可写」跳过并记日志。
- 只处理「本层没有 patch、子树里有」这一种形状。已经是扁平布局的包仍走原来的重扫路径。
