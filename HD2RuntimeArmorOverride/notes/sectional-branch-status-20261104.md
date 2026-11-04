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

### 第 4 步（`ModLibraryService`）的真实范围 —— 已量清

`ModLibraryService.cs` 1929 行里有 **137 行**提到 Decoration/HostDerived。而且这些 API **仍有 4 个 VM 在调**，
所以第 4 步不是"改一个文件"，而是"改一个中枢 + 清 4 个 VM"：

| API | 仍被谁调用 |
|---|---|
| `ToggleDecorationForAllAvailableHostsAsync` | `LibraryPageViewModel` |
| `SetDecorationEnabledForHostAsync` | `ModDetailsPageViewModel` |
| `ApplyDecorationActivationBatchAsync` | `ShellViewModel` |
| `GetDecorationActivationSummary` | `LibraryPageViewModel` |
| `IsDecorationEnabledForHost` | `ModDetailsPageViewModel` |
| `GetDecorationsForHost` | `ModDetailsPageViewModel` |
| `IsDecorationAvailableForHost` | `BottomBarCoordinator` / `ShellViewModel` |
| `FlushPendingDecorationRebuildsAsync` | `ShellViewModel`（11 处引用，含部署/删除前置） |

还有两条**必须小心**的交叉：
- `ModLibraryService.cs:961` 的 `FlushPendingDecorationRebuildsAsync` 在**部署前置**里；
- `:1026-1072` 的删除路径把装饰停用、失败回滚、`deletedDecorations` 与 option 处理混在一个事务里。

⇒ 第 4 步要连带改 `LibraryPageViewModel` / `ModDetailsPageViewModel` / `ShellViewModel` /
`BottomBarCoordinator` 四个文件，其中 `ShellViewModel` 2500+ 行。**必须一轮完整预算，不能在收尾时赶。**

---

## ⚠️ 方案修正：装饰的"宿主附加面板"同时服务选项 —— 不能连带删

这是本轮最重要的发现，它改变了第 4 步的做法。

`ModDetailsPageView.xaml:316-334` 那个列表（`HostAttachmentList`）：

```
HeaderTitle="可为该 Mod 启用的附加选项"
HeaderSummary="已列出支持当前主体的装饰与选项。"
SelectionRequested="OnHostDecorationSelectionRequested"   → 设 scope = "DecorationHost:{ModId}"
RelationshipDragOperation="RemoveOptionParent"
```

⇒ **`DecorationHost:` 这个作用域名是历史命名，它服务的是"选项宿主"机制**。
因此下面这一整套**必须保留**（它们是选项功能的一部分，库里 121 个选项靠它）：

| 保留项 | 为什么 |
|---|---|
| `IsDecorationHostSelection` / `DecorationHost:` scope | 选项宿主面板的选中作用域 |
| `HasHostDecorations` / `HostDecorations` 集合 | 面板标题就写着"装饰与选项" |
| `ShowEnableDecorations` / `ShowDisableDecorations` | 判断条件里同时含 `HasSelectedOptions` |
| `ToggleDecoration` 行动作 | 对选项同样生效 |
| `ModNodeKind.Option` 那一整套 | 活的主力功能 |

**结论：装饰链的"退场"只能在装饰专属的部分做。** 一开始按名字把所有 `Decoration*` 都当末梢，
是错的判断。

---

## 装饰节点现在只剩一条进库路径

删掉 `CreateDecorationAsync`（`b1ceef3`，无任何调用者）之后：

- ✅ "生成装饰 Mod"这条路**彻底没了**：页面 → CLI → 服务入口，三层全删
- ⚠️ "用户把一个含 `decoration.json` 的文件夹丢进库"这条路**还活着**
  （`ObjectTreeImporter.cs:59` 按标记文件认）

这两者的处理方式不同，而且后者不是清理，是一个**行为决定**：

| 选项 | 后果 |
|---|---|
| A. 当成普通 Mod 导入 | `PatchGroups` 为空，进库但不可部署；用户看到一条"没内容"的条目 |
| B. 明确拒绝并说明 | 导入时报"装饰包已不再支持，请改用分件模组"，最清楚 |
| C. 保持现状（认成 Decoration 但功能已无） | 库里有这类记录的话会显示但没法操作 |

**这个决定该由用户拍板。** 定了它，第 4–7 步才能一次做完：
定了 B 就连 `ObjectTreeImporter` 的判定一起删，`ModNodeKind.Decoration` 才能安全移除。

---

## 装饰链退场：已完成到第 6 步，第 5 步的范围刚确认

### 已完成

| 步 | 提交 | 内容 |
|---|---|---|
| 1–2 | `e61bb92` `51735ad` | 两个页面 + 所有 UI 入口 + 16 个 CLI 派发臂 + 4 个测试文件 |
| 3 | `7c77850` | 八个不可达 CLI 类 + 40 个连带方法（`CliApplication.cs` 8028 → 7616） |
| — | `0eb650d` `b1ceef3` | `.bin` 旧路线字段级清理；无调用者的 `CreateDecorationAsync` |
| 导入 | `493749c` | 装饰包导入改为**明确拒绝** + 阻塞诊断（按用户决定） |
| 4–6 | `33a222c` | 枚举弃用 + 13 处行为分支清除 + 库读取层丢弃 |

### 三个必须记住的设计决定

**① 枚举值不删，只标 `[Obsolete]`。** `library.json` 的 kind 是字符串序列化的，
删成员会让旧库直接打不开。`[Obsolete]` 因此成了**精确的工作清单**（编译器用 CS0618
逐条报出来），清完之后只剩三处必须认得它的地方，都用 `#pragma` + 说明标注：

- `JsonModLibraryStore.DropUnsupportedNodes`——旧库里的装饰节点在**读取层**丢弃
- `SafeModDeletionPlanningService`——删遗留装饰节点时清它的装饰激活状态（否则留死引用）
- 三个测试文件——故意用废弃值当夹具，覆盖"旧库里存在装饰节点时怎么处理"

**② `HostDerivedOverwriteService` 是头盔合并的入口，不能删。**
`RebuildAsync(host, attachments, helmetMerged, …)` 同时收装饰附件与头盔合并标志；
装饰附件现在恒为空，但头盔合并在用。这是我自己在计划里标的"陷阱 1"，已确认成立。

**③ 宿主附加面板（`DecorationHost:` 作用域）服务选项，不能删。**
面板标题就是「可为该 Mod 启用的附加选项」。库里 121 个选项靠它。

### 第 5 步（Core 服务）的真实剩余范围

| 类型 | 仍被谁引用 | 能不能删 |
|---|---|---|
| `DecorationActivationStore` | `CoreServices` / `ModLibraryService`（8 处）/ CLI（2 处）/ 测试 | 能——要先清 `ModLibraryService` 的那 8 处与 CLI 的 2 处 |
| `DecorationHostRebuildService` | `HostDerivedOverwriteService` / `CoreServices` / 测试 | 装饰重建那部分能删；它也被 `UnitLodFamilySource` 的注释提到 |
| `HostDerivedOverwriteService` | `ModLibraryService` / CLI 批量修复 / `CoreServices` | **不能删**（头盔合并） |
| `DecorationPackageSnapshotService` | `CoreServices` / `ModInformationFactRegistry` / 测试 | 能 |
| `DecorationHostTargetBinder` | `DecorationHostRebuildService` / 测试 | 跟随上一个 |
| `DecorationPlanDocument` / `PayloadDocument` / `PlanningDefaults` | 上述服务 + 契约测试 | 跟随 |

⇒ 第 5 步仍是"清 `ModLibraryService` 的 8 处 + CLI 的 2 处 + 六个类型"，**外加**要决定
`HostDerivedOverwriteService` 的 `attachments` 参数是保留（恒空）还是收窄成纯头盔合并。

---

## 装饰链退场：UI 层已清空，只剩服务层

### 本轮完成（`1d77782` → `c017d4e`）

| 提交 | 内容 |
|---|---|
| `1d77782` | 库页 + 底部栏的 `IsDecoration` 死分支（含「显示装饰/隐藏装饰」筛选） |
| `12ca218` | 详情页宿主附件：装饰切换分支删掉，**行动作保留给选项** |
| `8d3d825` | 「附加装饰到宿主」整条功能链（7 个文件） |
| `c017d4e` | 详情页宿主附件列表只剩选项 |

**结果：七个装饰 API 在 UI 侧已无任何调用者。**

### 下一轮：删服务层

`ModLibraryService` 里这一簇可以整块删（现在只剩内部互相引用）：

```
ToggleDecorationForAllAvailableHostsAsync   //  122-139
SetDecorationEnabledForHostAsync
ApplyDecorationActivationBatchAsync          //  141-150
GetDecorationActivationSummary               //  152-158
IsDecorationEnabledForHost（public）         //  160-162
GetDecorationsForHost                        //  164-165
IsDecorationAvailableForHost                 //  167-169
── 连带 ──
_pendingDecorationActivations / _decorationDebounce*
ScheduleDecorationActivationBatchAsync / ApplyDecorationActivationBatchCoreAsync
FlushPendingDecorationRebuildsAsync（**注意：部署前置在调它**，见下）
ReadEnabledDecorationsAsync / RebuildDecorationOverwritesAsync
GetAvailableDecorationHostIds / GetEffectiveDecorationHosts / IsDecorationEnabledForHost(private)
_decorationActivations（DecorationActivationStore）
```

然后删六个 Core 类型：`DecorationActivationStore` / `DecorationHostRebuildService` /
`DecorationPackageSnapshotService` / `DecorationHostTargetBinder` /
`DecorationPlanDocument` + `DecorationPayloadDocument` + `DecorationPlanningDefaults`。

**`HostDerivedOverwriteService` 保留**（头盔合并走它），但它的 `attachments` 参数会恒空。

### 一个必须先处理的交叉

`ModLibraryService.cs` 的**部署前置**里调 `FlushPendingDecorationRebuildsAsync`。
删它之前要确认：那条前置只是"把待合并的装饰冲刷掉"，删掉之后部署路径不受影响。
`_pendingDecorationActivations` 恒空，所以这个冲刷本来就是 no-op —— 但要**逐行确认**，
不能因为"恒空"就跳过检查（部署路径出错，用户看到的是游戏起不来）。

### 一个自己的错，记下来

上一轮我 grep `AttachSelectedDecorationsAsync` 时把 pattern 写成了 `...ToHost`，
于是判定"没有调用者"并删了方法，编译立刻报第 309 行还在用。
**grep 的 pattern 必须与真实标识符逐字符一致**；判"没人用"之前先确认 pattern 命中过。

---

## 装饰链退场：服务层第二批完成，剩最后一块（牵头盔合并）

### 本轮完成（`e93fa0b` → `5d22055`）

| 提交 | 内容 | 量 |
|---|---|---|
| `e93fa0b` | 九个叶子方法 + 三处适配 | `1877 → 1792` |
| `ffe803b` | 激活机制（8 个方法）+ 删除路径死块 + 5 个字段 + 7 处 flush 调用 | `1792 → 1536` |
| `5d22055` | `DecorationPackageSnapshotService` 零调用者，整个删 | Core 870 → 869 |

### 剩下的是同一块：`HostDerivedOverwriteService` 收窄成纯头盔合并

这是最后一块，**不能单独做**，因为它们互相咬合：

```
HostDerivedOverwriteRequest.EnabledDecorations   ← 装饰附件参数（现在恒空）
  ↑
HostDerivedOverwriteService                      ← **头盔合并走它，必须留**
  ↑ 依赖
DecorationHostRebuildService                     ← 可删（只为装饰服务）
  ↑ 依赖
DecorationHostTargetBinder / DecorationPlanDocument /
DecorationPayloadDocument / DecorationPlanningDefaults   ← 可删

另有一条平行线：
DecorationActivationStore                        ← 可删
  ↑
SafeModDeletionSafetySnapshot.IsDecorationActivationStateKnown / EnabledDecorations
SafeModDeletionEnabledDecoration
blocker "EnabledDecorationRollbackRequired"
  ↑ 约 54 处引用 / 7 个文件（Core 契约 + CLI + Manager + 测试）
```

**做法（下一轮）**：
1. `HostDerivedOverwriteRequest` 去掉 `EnabledDecorations`；`RebuildAsync` 只接 `helmetMerged`
2. 跟着删 `DecorationHostRebuildService` / `HostTargetBinder` / `PlanDocument` /
   `PayloadDocument` / `PlanningDefaults`（连同 `CoreServices` 工厂与测试）
3. `SafeModDeletionSafetySnapshot` 去掉两个装饰字段 + `SafeModDeletionEnabledDecoration`
   + 那条 blocker
4. `DecorationActivationStore` + CLI 的读取/清理 + `DecorationActivationCleanupResult`
5. `ModLibraryService` 里 `_decorationHostRebuild` 字段（第 3 步之后就没用了）

**为什么不在这一轮硬做**：第 3 步会改 Core 的删除安全契约，波及 CLI 与 Manager 两边，
是一个"改完必须整轮验证"的单元。这轮已经落了 4 个提交、全绿，停在这里比在半途好。

### 一个工具上的教训

删方法的脚本里那条"区块内不许出现禁删方法名"的校验**太严**：
`ApplyDecorationActivationBatchCoreAsync` 引用了要保留的 `RebuildDecorationOverwritesAsync`，
**引用是合法的，只有定义不能落在区块里**。改成只校验"区块内不许出现另一个方法签名"之后才对。
护栏要拦的是"删过头"，不是"提到过"。

---

## 「生成sectional版」端到端验证配方（2026-11-04 实测通过）

这是**第一次**用真实包把 `换甲 → 拆件 → 剪枝 → 导入` 走通。配方照抄即可复现。

### 输入

`【4.28-赠品】Chiffon-Rock ala clothing替换UF-16督察员.rar`（10.2 MB，解压 123 MB）。

解压后只有 4 个文件，**一个 patch**：

```
9ba626afa44a3aa3.patch_0              196 KB     几何
9ba626afa44a3aa3.patch_0.gpu_resources  118 MB
9ba626afa44a3aa3.patch_0.stream        1.0 MB
```

### 为什么这个包能用

`custom-model inspect` 的判据是"Mod 的 unit 取代了某件原版护甲/头盔"（独占归属）。
这个包替换 UF-16「督察员」，所以两个来源都在：

```
19b5a68e61d7a9d7  [Armor]   UF-16"督察员"  16 个部件
fb025c06b0408e5f  [Helmet]  UF-16"督察员"  1 个部件
```

**库里原有的 133 个 Mod 一个都不满足** —— 11 个已是自定义素体（unit 是新生成的，不归属任何
原版 archive），119 个是材质包。所以这个按钮只对"还没转换的、取代原版护甲的 Mod"生效。

### 步骤（用临时库，不碰真实库 `E:\mod`）

```powershell
$exe='HD2ModCli\bin\Debug\net10.0\hd2mod.exe'
$mgr='E:\Data\source\repos\WpfApp1\HD2ModManager\bin\Debug\net10.0-windows'   # 借它的游戏资产索引
$lib='E:\tmp\e2e-lib'    # 临时库
$gd='D:\SteamLibrary\steamapps\common\Helldivers 2\data'

& $exe import plan  <解压目录> --manager-root $mgr --library $lib --output E:\tmp\e2e-plan.json --json
& $exe import apply --manager-root $mgr --library $lib --plan E:\tmp\e2e-plan.json --confirm --json
& $exe custom-model inspect <节点id> --manager-root $mgr --library $lib --game-data $gd --json
& $exe custom-model sectional plan  <节点id> '19b5a68e61d7a9d7,fb025c06b0408e5f' <名字> --manager-root $mgr --library $lib --game-data $gd
& $exe custom-model sectional apply <节点id> '19b5a68e61d7a9d7,fb025c06b0408e5f' <名字> ... --confirm
```

### 实测结果

```
plan : 13 个来源 unit → 11 个输出 unit · 13 个 section 组
       Torso/Slim 承载 3 个来源（合并 2 个）；LeftShoulder 并进 Torso
apply: 11 个输出 unit · 13 组 · 11 个组件（剪掉 2 个悬空组件）· 导入 1 个节点

产物 : 单 patch 142 KB
       components.json 13 组（schema hd2mod.section-groups/1）
       sections.json   11 units / 24 sections（schema hd2mod.section-map/3）
       custom-model.json pieces = 11（13 来源 − 2 个被合并掉的 donor）
```

### 那次跑出来的两个真 bug（单测都抓不到）

1. **就地拆件会把源 patch 锁死**：writer 用 `FileShare.None` 打开目标，同时又要从"源 patch"
   拷贝 payload（同名同目录）⇒ Windows 报"文件正被另一进程使用"。
   改成先进 `.sectional-staging-<guid>/` 再搬回。
2. **`custom-model.json` 的悬空 pieces**：合并掉 donor unit 后，指向它的 `pieceType` 1/2
   条目会悬空。见 `CustomModelSectionalCompaction`（两处持久化一起改）。

### 还没做的验证

**多 patch 的"删除被折叠 patch"没有端到端跑过** —— `apply` 要写库，被"管理器正在运行"的
护栏挡住（用户当时开着管理器）。它由 3 条单测覆盖
（`RemoveConsumedPatches_*`：删 patch 与 sidecar / 单 patch 不动 / 写别处不动源）。

多 patch 的**叠加读取**倒是验过了：把同一个 patch 复制成 `patch_0` + `patch_1` 导入，
`sectional plan` 得到 **13 个来源而不是 26** —— 说明叠加去重生效了。
