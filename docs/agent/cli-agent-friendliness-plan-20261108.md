# CLI 面向 agent 的可用性改造（2026-11-08）

来源：另一个会话提交的 14 条使用反馈（`mod rebuild` 报 ModNotFound、`--id-namespace` 二次哈希、
部分失败仍 exit 0、没有 patch 观测量、前置条件分两轮报……）。本文把每条落到代码位置，
定下改法与错误码，然后再动手。

**已核对的结论：两条的机制与反馈不符**，但修正之后问题更严重（见 §2）。

---

## 0. 用户已定的两条口径

1. **退出码先不动**（仍为 0）。先只加结构化字段，等管理器读上字段之后再切 `4 = 部分成功`。
2. **`repair library-snapshot` 改为默认不写**，`--confirm` 才写。

---

## 1. 现状体检（哪些已经做对了）

不要重写。这些是底子：

| 已有的 | 位置 |
|---|---|
| 全命令 `--json` + machine-readable `code` | `CliApplication.WriteError` |
| `capabilities` 命令（agent 自述） | `CliApplication:114` |
| `--vpk-list` 绕开命令行长度上限 | `CliArguments` |
| 写操作要 `--confirm` | 多处 |
| `Diagnostics`（中止）/ `Warnings`（降级）/ `Skipped`（源侧说不画）三分 | `VpkPatchPackage` |
| `materialGap.missing` 已按"部件会被移除"单独记 | `VpkMaterialGap` |
| 已有测试钉住"少参数不能退化成 UnknownCommand" | `CustomModelSectionalCliTests` |

所以这轮是**补洞 + 收敛**，不是重构。

---

## 2. 两条需要修正的反馈

### 2.1 `mod rebuild` 报 ModNotFound —— 不是"库快照不存在"

`CliWorkspace.LoadLibraryAsync`（`:148`）在 `library.json` 缺失时抛的是
`LibrarySnapshotNotFound`，文案是「未找到 Mod 库状态文件」。既然看到的是 `ModNotFound`，
说明**文件在、里面没有这个 GUID** ⇒ 快照**过期**。

真正的问题更大：`ResolveMod`（`CliProjections.cs:455`）只拿到一个字典，它不知道快照有几个节点、
磁盘上有几个文件夹，于是三种不同故障共用一句「未找到 Mod」。

### 2.2 `repair library-snapshot` "回写 mod json" —— 不是回写，是换 id

`CliLibrarySnapshotRepair.RunAsync` 只 `store.SaveAsync(library.json)`，**不写**
`.hd2modmanager-mod.json`。但它做的事更难查：

- 它靠**正则**从旧的 `library.json` 文本里捞 `"id" …, "relativePath" …` 配对（`:33-35`）。
  今天的库 11/11 命中，但那是序列化顺序碰巧如此。
- 文件缺失或顺序变了 ⇒ `seeds` 为空 ⇒ `InspectAsync` 给**每个 Mod 发新 id**。
  配置条目、护甲映射工作区、`.hd2modmanager` 里的映射全部变成孤儿。

---

## 3. 改动清单

### A. 库快照：把"过期"和"没这个 Mod"分开

* `CliProjections.ResolveMod` 增加诊断重载：失败时带上
  `snapshotNodes` / `diskDirectories` / `savedUtc`。
* 节点数 ≠ 磁盘文件夹数 ⇒ **新错误码 `LibrarySnapshotStale`**，文案给出修复命令。
* `LibrarySnapshotNotFound` 的文案补上"怎么建"。
* `repair library-snapshot`：
  - 默认 **dry-run**（`written:false`），`--confirm` 才写；
  - 结果带 `snapshotNodes` / `seedsRecovered` / `diskMods` / `wouldReassignIds`
    （旧 id → 新 id 的条数，按 `relativePath` 配对比较）；
  - dry-run 与写入共用同一份计算结果，不许两条路径算出不同结论。

### B. `--id-namespace` 形状守卫

值匹配 `^vpk-[0-9a-fA-F]{16}$` ⇒ 抛 `IdNamespaceLooksLikeToken`，提示改用
`--id-namespace-token`。**不自动识别**——自动识别会把"我传错了"变成另一种静默。

### C. 部分失败的结构化字段（退出码仍 0）

顶层 `--json` 增加：

```json
{ "ok": true, "partial": true,
  "missing": [ { "vpk": "…", "materials": ["a","b"], "count": 47 } ],
  "warnings": [ "…" ] }
```

`partial = missing.Count > 0`，数据全部来自既有的 `package.MaterialGap`，
**不改 `HD2SourceMigration`**。人类可读输出末尾追加一行
「⚠️ 本次有 N 个源材质彻底缺失（部件会被移除）：见 missing」。

### D. 前置条件一遍报全

`vpk2patch` 开跑前统一探测：游戏 data / 骨名哈希表 / avatar Toc / 资产索引（装备关系表）。
缺多个 ⇒ 一次抛 `PrerequisitesMissing`，逐行写「缺什么 → 用哪个参数/命令补」。
探测失败一律降级为"这一项没准备好"，不吞原始错误信息。

### E. 选择器统一

`ResolveMod` 接受 **名字 / GUID / GUID 前缀**，歧义时列出候选；与其它命令一致。
（`ResolveProfile` 已经是这个行为，照抄。）

### F. 未知命令给最近匹配

`UnknownCommand` 时用编辑距离找最接近的命令名，输出 `usage`。零匹配也给 `hd2mod help`。

### G. `capabilities --json` 补"每个命令需要什么"

把「哪些命令需要 `--library` / `--manager-root` / `--game-data` / `--confirm`」
做成机器可读字段，替代"靠试"。

### H. 小摩擦（顺手）

* `--reuse-model` 报错补一句"要给的是 `custom-model.json` 的路径，不是模型 key"。
* `mod rebuild` 结尾给出下一步（部署命令 / 管理器里点哪）。
* `patch inspect`（**新增**，见 §5 的取舍）。

---

## 4. 退出码（这一轮不动，先把规范定下来）

| 码 | 含义 | 现状 |
|---|---|---|
| 0 | 成功（**可能 `partial:true`**） | 不变 |
| 2 | 用法/校验错误，或操作失败 | 不变（这两件事现在混在一起） |
| 3 | 运行期失败（I/O 等） | 预留 |
| 4 | 部分成功 | **等管理器读 `partial` 之后再切** |
| 130 | 已取消 | 不变 |

`ok` 的语义固定为「命令跑完了」，`partial` 表示"有东西没到位"。
**切换 4 之前必须同步改管理器**（`VpkImportService.PlanAsync`、`VpkRebuildService` 现在按
`exitCode != 0` 判失败）。

---

## 5. 取舍：这一轮不做

| 项 | 原因 |
|---|---|
| `patch inspect` 的贴图解码（尺寸/格式/均值/alpha） | 需要 texconv 与贴图解析，是独立一块；先做结构层（资产分组 + 材质→贴图引用图 + 悬空引用），解码下轮 |
| `vpk2patch --json` 里的扁平 patch 序号 | 影响 `sectional/` 编号约定，要先确认消费方；先只补 `subdirectory` 字段 |
| `gamedata extract list` 的 cursor 字段 | 已有分页，只是没写进 JSON 文档；下轮统一 |

---

## 6. 验收

1. `dotnet build -c Debug` 0 警告 0 错误；`dotnet test` 全绿。
2. 每条改动至少一条测试：错误码、字段名、dry-run 不写盘、preflight 一次报全、
   选择器按名字命中、未知命令给建议。
3. 拿真实的库跑一遍：`mod rebuild <名字>` 能命中；`repair library-snapshot` 默认不写盘；
   `vpk2patch` 在材质缺失时 `partial:true` 且退出码仍是 0。

---

## 7. 落地情况（2026-11-08）

已实现：

| 项 | 位置 | 行为 |
|---|---|---|
| 库快照过期单列错误码 | `CliLibraryStateDiagnostics` + `CliApplication.ResolveModOrExplain` | 快照 N 个 vs 磁盘 M 个；`LibrarySnapshotStale` 带修复命令 |
| 选择器接受名字 | `CliProjections.ResolveMod` | 名字 / GUID / 前缀；歧义列候选 |
| 快照修复默认预演 | `CliLibrarySnapshotRepair` + `repair library-snapshot` | 默认不写盘；`--confirm` 才写；回报 `wouldReassignIds` 与逐条 `reassignments` |
| `--id-namespace` 形状守卫 | `CliArguments` | 值像 token ⇒ `IdNamespaceLooksLikeToken` |
| 部分失败字段化 | `vpk2patch` 的 JSON | `partial` / `missingMaterials` / `degradedMaterials` / `warnings`；退出码仍 0 |
| 前置条件一次报全 | `EnsureVpk2PatchPrerequisitesAsync` | `PrerequisitesMissing` 逐条列出；索引只在批次含模型时查 |
| 未知命令给建议 | `BuildUnknownCommandMessage` | 编辑距离 ≤3 时点名最接近的命令 |
| `--reuse-model` 报错 | `ResolveReuseModelPath` | 识别"给的是模型 key" |
| `mod rebuild` 下一步 | `FormatModRebuild` | 给出部署命令 |

实测（真实环境）：

```
repair library-snapshot --library E:\mod           → written:false，11→11，wouldReassignIds:0，library.json 哈希未变
mod rebuild no-such-mod（快照 11 / 磁盘 13）        → LibrarySnapshotStale（带修复命令）
vpk2patch <真实 VPK> --manager-root … --game-data …  → ok:true partial:false degradedMaterials:45 warnings:90
vpk2patch <真实 VPK>（缺 --game-data 与骨名表）        → PrerequisitesMissing 一次列 2 项
```

未做（明确留给下一轮）：

* `patch inspect` 的贴图解码（尺寸/格式/均值/alpha）——需要 texconv 与贴图解析，是独立一块。
* `vpk2patch --json` 的扁平 patch 序号（会影响 `sectional/` 编号约定，先确认消费方）。
* `capabilities --json` 补"每个命令需要哪些参数"：`CreateCapabilities` 里那张 `supports`
  表**已经与派发脱节**（缺 `vpk2patch` / `mod rebuild` / `patch` 大半）。补它得先让那张表
  由派发生成，否则只是把过期内容写得更正式。
* `gamedata extract list` 的 cursor 文档。

