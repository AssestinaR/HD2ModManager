# sectional 分支进度（2026-11-04）

> ⚠️ **先看这一条：库里 `lunalice_bill` 的载荷没了（见文末「数据事故」）。**
> 与本分支的代码无关，但影响验证与用户数据，必须先处理。

> 分支 `sectional`，worktree 在 `.agents/worktrees/sectional`，从 master `dc2da4e` 拉出。
> 设计稿：`section-consolidation-20261104.md`；U0 结论：`sectional-u0-findings-20261104.md`。
> **测试基线（每次提交前都跑全量）：1740 通过 / 0 失败 / 1 跳过。**

---

## 已完成

| 步 | 提交 | 内容 |
|---|---|---|
| 建分支 | `7cfa623` | worktree + 实测基线（1691 通过，比记录的 1685 多 6 条 Core 测试） |
| U1 机械改名 | `772ed65` | `VpkComponentSwitch*` → `SectionGroup*`（零行为变化） |
| U1 组模型 | `292a7ff` | 组表带 id / source / exclusiveGroup；schema → `hd2mod.section-groups/1`；**旧格式零迁移读入**（8 份真实 `components.json` 实测通过） |
| U2a 组编辑 | `054fba0` | `SectionGroupEditor`：新建 / 改名 / 删除 / 设互斥，纯函数 + 9 条测试 |
| U2b 组编辑器 | `e6e181e` | 改名 / 删除 / 新建 + 独立·耦合·失效三态 + **勾选即保存**（含"防抖写错目标"那个 bug 的修复） |
| W1 包围盒 | `74a59aa` | `CanonicalCullingBounds` 三模式 + 诊断；**默认 Preserve，三条在役路径字节不变** |
| W2 可寻址 | `c95535c` | `uniqueSlotPerSection`；测试直接钉住"共用槽会并段 / 唯一槽保边界" |
| U3a 规划器 | `1e6c656` | `SectionalSplitPlanner`（7 槽 × 身形，绝不跨身形合并）+ **互斥组强制**（变体替换的全部实现） |
| U3b 段来源账 | `9ebf1ec` | 追加结果带 `Origins`，供拆件器把 donor 几何翻成组员 |
| U5a 删死代码 | daaaea | 装饰快照转发壳、孤儿 XAML 模板 |
| U4 就地烘焙 | c8cf30e | 「应用」不再重跑换甲；端到端验证过（幂等 + 可逆） |
| U3c 拆件执行器 | 49af034 | 读 patch / 合并 / 写回 / 产 sections.json + 组表；端到端验证过（7 units / 92 段 / 组表 100% 覆盖） |
| V0 配方 + 死字段容错 | 7363a71 | 验证配方；三个死字段不再阻断工作区读取 |
| U5b 掩码链停分发 | `1f33815` | 九个死 Lua 不再内嵌进 Core（文件保留，只停内嵌） |
| 拆件器两处 bug | `5b136dc` | 读 payload 的读取器 + 合并目标的组员切分；sidecar 落盘 |
| V0 离线验证 | `7ef81fa` | 合成样本跑过三条判据，全过 |

### 几个值得记住的判断

- **`components.json` 这个文件名没改**：改名要迁移库里 8 份已有文件，而这件事没有用户可见收益。
  它现在装的是组表，属于和 `vpk2patch/` 同一类的命名债。
- **`SectionGroup` 不加 `defaultEnabled`**：开关状态本来就在 `wardrobe.json.disabled` 里，
  再加一个"默认值"只会多一个会和权威数据打架的真相来源。
- **变体替换不需要新字段**：基础件与各变体同属一个 `exclusiveGroup`，选一个变体就等于把基础件关掉。
- **`Origins` 在段数被合并改变时留空而不是报诊断**：段数变化在默认路径上是正常现象，
  把它变成诊断会让三条在役路径整体失败。

---

## 还没做

### V0 只剩实机那一段（离线三条已跑过，全过）

`sectional-v0-verification-20261104.md` 里离线两条判据可以立刻跑；
实机那三条（位置 / 遮挡 / 崩不崩）必须有人进游戏看。

**前置**：需要一份多 unit 的 legacy 模型样本。本机没有——唯一的 legacy 形态是纯材质包。
造样本的两条路写在配方 §1.1。

### U5 剩下的两处大退场（刻意没做）

这两处不是"删几个文件"，而是**删掉一整套仍在编译、仍在测试覆盖里的子系统**。
astra 审核时明确建议按层退场而不是整包删，所以留给人拍板：

**① 装饰链退场**（`DecorationHostRebuildService` 788 行 / `HostDerivedOverwriteService` /
`DecorationActivationStore` / `DecorationPlanDocument` / 两个装饰页面 / CLI 的
`decoration *` 与 `host-derived *`）
- 现网 0 个装饰，机制上已被 `SectionalSplitter` 取代（后者把 donor 几何直接并进 7 槽）。
- 但 `CanonicalDecorationUnitAppender` 是它的底层，**拆件器也在用**——底层必须留，
  能退的只有它上面那层"独立包 + 启用状态 + 重建宿主"。
- 建议顺序：先停 GUI 入口 → 观察一个版本 → 再删 CLI → 最后删 Core 服务与测试。

**② 护甲级掩码链退场**（`wardrobe.workspace.json` / `wardrobe.masks` /
`WardrobeMaskCompiler` / `WardrobeMaskProjection` / `WardrobeMaskFile` /
`armor_section_*.lua` / `armor_wardrobe_masks.lua` / CLI `wardrobe compile|project|validate`）
- 运行时那条链**已经不可能生效**（读它的 Lua 第一行 `require('ffi')`，本版引擎没有 ffi）。
- 但 Core 里的身份规范化（`WardrobeSectionFingerprint`）与投影思路，将来做
  "护甲级策略 → 生成该护甲的烘焙产物"时还能复用，所以按层归档而不是直接删。
- `wardrobe sections` 要留（补表用）。

### 三个"小删除"——**核过之后发现都不是死代码，已纠正**

早先那份清单（来自 U0 的 A 线报告）说这三处没人用，逐条核实后不成立：

| 项 | 实际状态 |
|---|---|
| `vpk2patch --unit-ids` | **活的**。`CliApplication.ResolveUnitIdentities`（:3091）被 `source convert`（:2698）与 `vpk2patch`（:3489）两处调用。它只是**没写进帮助文本**、管理器也从不传——是"未文档化的可选参数"，不是死代码。 |
| `WardrobeSectionSource` + `RequestUnitSectionStructureAsync` | **活的**。`ModInformationCenter.cs:472` 调用它，而 `RequestUnitSectionStructureAsync` 是 `IModInformationCenter` 上的公开方法（有实现、有 Fake、有测试）。删它要动公开接口。 |
| 装饰 `.bin` 旧路线 | **只读不可达**：`Payloads` 全仓唯一的赋值是 `CliDecorationEditPlanArtifacts.cs:713` 的 `Payloads = []`，**没有写入者**；`DecorationHostRebuildService.cs:77-85` 只是读。这条是真的，但它在一整套要退场的子系统里，单独删没意义。 |

教训与之前几次相同：**"没人调"要先按当前代码查调用点，不能照抄报告结论。**
`--unit-ids` 那条特别典型——它不在帮助里，所以看起来像死参数，实际两个命令都在用。

---

## 合并前检查清单

- [ ] 全量测试绿（当前 1739 / 0 / 1）
- [ ] legacy Mod 重构建一次，产物与拆件时逐字节一致（U3 的验收）
- [ ] V0 至少做过一次
- [ ] 确认没有把材质方向会话正在改的文件带进来（`HD2SourceMigration/VpkConversion/*`）
- [ ] master 合进分支一次，解决 `CliApplication.cs` 等热点文件的冲突

---

## 数据事故：`E:\mod\lunalice_bill` 的载荷与来源 VPK 不见了

发现时间 2026-11-04 14:05 左右（做 U4 端到端验证、想拿它当样本时）。

**现状**

```
E:\mod\lunalice_bill\
    vpk2patch\                       ← 目录本身还在
        physbone-rig.bin   10974 B   ← 只剩这一个文件（11-03 12:23）
```

**丢掉的**：来源 `.vpk`（约 12 MB）、`vpk2patch\*.patch_0` + `.gpu_resources` + `.stream`、
`sections.json`、`components.json`、`custom-model.json`、`wardrobe.json`。
目录的修改时间是 **2026-11-04 12:53:41**。

**连带影响**

- `library.json` 里这个节点**还在**（`kind=standard`），但 `patchGroups=0`、`children=0`
  —— 说明管理器之后重扫过，发现没有可部署的 patch。
- **12 个 option 把它当宿主，且全部 `enabled=true`**。它们现在挂在一个没有内容的宿主上。

**已排除的**

- 不是 `VpkPayloadSwap` 的换载荷操作：库里**没有** `.hd2modmanager-swap-backups` 目录
  （那个操作会先备份）。
- 不是我的 `unitprobe` 读取（那次是 13:35，在文件消失之后；且不带 `--patch-numindices` 就是只读）。
- 全库只有这一个 Mod 缺载荷，其余 130+ 个正常。

**没找到可恢复的副本**：`E:` 全盘按名字搜 `*lunalice*`，只有 9 月的几个部署计划 JSON
和另一个仓库的源码；回收站查询因 COM 枚举卡住未完成。

**恢复办法**：用户手里应该还有原始的 `lunalice_bill.vpk`（L4D2 addons 目录），
重新导入即可——**但先要确认是什么删的**，否则导回去可能再被删一次。

**不能确定的原因**：12:53 前后本机有两个会话在工作（本会话的 U0 子代理在 12:20–12:50 读库；
另一个会话在做材质方向）。我没有证据指向任何一方，只能说清事实与时间点。

---

## 掩码链第三层：已完成一半（`bdfda20`），剩下的一半附正确顺序

**已删（`bdfda20`）**：`WardrobeMaskCompiler` / `WardrobeMaskProjection` / `WardrobeMaskFile` 三个类
+ 它们的三个测试文件 + CLI 的 compile / validate / project 三条命令。这三个类**只有 CLI 在用**，
所以它们是干净的叶子。`WardrobeMaskDiagnostic` 搬进了 `WardrobeMasks.cs`（`WardrobeWorkspaceFile` 还在用）。

**还没删**：`WardrobeMasks.cs`（MaskDocument / MaskView）、`WardrobeWorkspaceFile`、`WardrobeWorkspace`。
障碍与正确的删除顺序写在下面 —— 我第一次试的时候**顺序反了**（按叶子往上删），
三次索引算错、连锁出四处编译错误，整体回退过一次。第二次按依赖方向才成了现在这一半。

### 真正的障碍：`WardrobeTreeViewModel` 里有一条死分支

它现在只剩 VPK 那条活路（`IWardrobeTreeToggleStore`），但还留着护甲掩码编辑器的半截：

```
WardrobeTreeViewModel
  ├ 字段 workspacePath / workspace / followSource
  ├ 构造 (string workspacePath)
  ├ 属性 FollowSource        ← 里面调 WardrobeTree.MakeOwn / MakeInherit + Save()
  └ 方法 Load(uint,…) / Save() / Refresh()
        ↓ 依赖
  WardrobeWorkspaceFile（读写字 wardrobe.workspace.json）
  WardrobeWorkspace / WardrobeWorkspaceArmour / WardrobeMaskMode
  WardrobeTree.SetEnabled / MakeOwn / MakeInherit / Replace / Counts(workspace)
  WardrobeMaskDiagnostic  ← 它声明在 WardrobeMasks.cs 里，所以删那个文件会连带打断 WorkspaceFile
```

### 必须先做的一件事

`WardrobeMasks.cs` 里**混着两样东西**：

- **活的**：`WardrobeSectionIdentity` + `WardrobeSectionFingerprint`
  （`sections.json`、`ModInformationCenter`、`WardrobeSectionSource` 都在用）；
- **死的**：`WardrobeMaskDocument` / `WardrobeMaskEntry` / `WardrobeMaskView` / `WardrobeMaskDiagnostic`。

**先把活的拆到 `WardrobeSectionFingerprint.cs`，再删文件。**

### 正确的删除顺序（按依赖方向，从消费者到被消费者）

| 步 | 删什么 | 为什么在这一步 |
|---|---|---|
| 0 | 把 `WardrobeSectionIdentity` + `WardrobeSectionFingerprint` 拆出去 | 它们是活的，混在要被删的文件里 |
| 1 | `WardrobeTreeViewModel` 的 workspace 分支（字段 / 构造 / `FollowSource` / `Load` / `Save` / `Refresh` 里的工作区部分） | 它是**唯一的消费者**，先断开才谈得上删别的 |
| 2 | `WardrobeTree` 的 `SetEnabled` / `MakeOwn` / `MakeInherit` / `Replace` / `Counts(workspace)` | 只有第 1 步在用 |
| 3 | `WardrobeWorkspaceFile` + `WardrobeWorkspace` | 只有第 1 步在用 |
| 4 | `WardrobeMasks.cs`（掩码文档与视图）+ `WardrobeMaskCompiler` / `WardrobeMaskProjection` / `WardrobeMaskFile` | 第 1–3 步清完之后它们才没有消费者 |
| 5 | 四个测试文件 + `WardrobeCliTests` 里 compile/validate/project 那批 | 跟代码一起删 |

### 我的教训

**我按"从叶子往上"删（先删 `WardrobeMasks.cs` 这类被依赖的类型），方向反了。**
依赖方向要求**从消费者往被消费者**删。另外在这种 400+ 行的文件里靠"往上找 `/// <summary>`、往下找 `}"` 猜方法边界"是不可靠的——
我因此把 `RunSectionsAsync` 一起删掉过一次。正确做法是先 `rg -n '^\t(internal|private) static'` 拿到准确行号，
再按**降序**用显式区间删。

---

## 装饰链退场：第 1–2 步已完成（`e61bb92` / `51735ad`），第 3 步起未动

**规模**：`rg -l 'Decoration'` → **110 个文件**。`DecorationPlanDocument` 单独牵扯 16 个文件，
`ModNodeKind.Decoration` 有 **19 个消费点**（Core 导入器 / CLI / 管理器 / 测试）。

**一个有利条件**：现网 `library.json` 里 `kind=Decoration` 的节点 **0 个**，
所以删这个枚举值**不需要数据迁移**。

### 依赖顺序（消费者 → 被消费者）

| 步 | 删什么 | 谁在用 | 风险 |
|---|---|---|---|
| 1 | ✅ **已完成** `e61bb92`：两个页面 + 三个 `ShellViewModel` 路由 + 两个 `WorkspacePageType` 成员 + 两条页动作 + 两个命令 + `PageActionKind` 两个成员 + 详情页「装饰详情」卡 + UiSnapshot stub | 只有 UI 自己 | 低 |
| 2 | ✅ **已完成** `e61bb92`：「编辑装饰 Mod」动作 + `EditDecorationCommand` + `CanEditDecoration` + `PageActionKind.EditDecoration` | UI | 低 |
| 3 | ✅ **入口已摘** `51735ad`：16 个派发臂（含 `host-derived` 全部）+ 4 个测试文件。**八个 artifact/executor 类仍留着**（已成不可达代码，删它们要连带清 `CliApplication.cs` 尾部那批只服务它们的格式化辅助方法） | 只有 CLI | 中 |
| 4 | `ModLibraryService` 的装饰路径：`ScheduleDecorationActivationBatchAsync` / `FlushPendingDecorationRebuildsAsync` / `RebuildDecorationOverwritesAsync` / `ReadEnabledDecorationsAsync` / `RebuildHostOverwriteAsync` | 只有第 1、3 步 | **高**——`ModLibraryService` 是中枢文件，且 `HostDerivedOverwriteService` 是装饰 + 头盔合并共用的入口 |
| 5 | `HostDerivedOverwriteService` / `DecorationHostRebuildService` / `DecorationActivationStore` / `DecorationPackageSnapshotService` / `DecorationHostTargetBinder` / `DecorationPlanDocument` / `DecorationPayloadDocument` / `DecorationPlanningDefaults` | 只有第 4 步 | 中 |
| 6 | `ModNodeKind.Decoration` 及其 19 个消费点；`ObjectTreeImporter` / `ModImportPlanner` / `ModLibraryImporter` 的装饰分支 | 第 1–5 步之后 | 中——枚举值一删，编译器会把所有漏掉的点报出来，这反而是好事 |
| 7 | 测试：`DecorationEditCliTests` / `DecorationManagementCliTests` / `HostDerivedOverwriteCliTests` + Core 里那 4 个提到 Decoration 的测试文件 | 跟代码一起 | 低 |

### 三个必须先看清的陷阱

1. **`HostDerivedOverwriteService` 是装饰与头盔合并共用的入口**（`DecorationHostRebuildService.cs:73-95` 里
   "先装饰、再把头盔合并叠上去"）。删它之前要确认常规线的头盔合并走的是 `HelmetToArmorMergeService` 而不是它。
2. **`CanonicalDecorationUnitAppender` 绝对不能删**——它是 `SectionalSplitter` 的底层。
   名字里有 Decoration，但它是"把 donor 几何并进宿主"的唯一实现。
3. **`CliBatchPatchRepairExecutor` 同时服务"批量修 patch"和"装饰"**，只能删装饰那一段，
   不能整文件删。

### 为什么这一轮没做

按上面的规模，这是 110 个文件、7 个阶段、其中第 4 步动的是 `ModLibraryService` 这个中枢。
本会话在**小得多的**删除上（掩码链）已经因为顺序反了回退过一次；在剩余预算内做装饰链，
最可能的结果是留一个半删的编译不过的分支。**先把它量清、排好序，等一拍再做。**
