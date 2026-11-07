# 重构计划（2026-11-06 起）

> 配套决策记录：`docs/dual-surface-evaluation-20261106.md`（第 9~15 节是本轮讨论）。
> 本文件只写**要做什么、按什么顺序、怎么验收**，不重复论证。

---

## 0. 已锁定的架构决定

这七条不再讨论，后续所有实现服从它们。

| # | 决定 |
|---|---|
| D1 | **一个 sectional / vpk2patch mod = 一个自洽条目**。材质不再作为独立 Option mod，「家族」概念取消。 |
| D2 | **根目录**放来源（`*.vpk` 或 `*.patch_n`），平铺；它们**不生效**，只是权威来源。 |
| D3 | **`sectional/`** 放转换产物，**全部 patch 组平铺按序号**（unit 恒为 `patch_0`，材质 `patch_1..patch_N` 向后排）。patch 文件名保持默认 `9ba626afa44a3aa3.patch_N`。**不建子目录。** |
| D4 | `sectional/` 里有一份**结构文件**，标记每个 patch 组是谁（unit / 哪个材质变体 / 来源哪个 VPK）。 |
| D5 | **`Overwrite/` 是唯一生效层**：只含启用的材质 patch + 修改后的 unit patch。关闭的材质 patch 留在 `sectional/`，不部署。 |
| D6 | 旧库迁移**一次性**，不做双读兼容。迁移前整库备份。 |
| D7 | 派生数据不设防：`sectional/` 可随时重生成；`Overwrite/` 可随时由 `sectional/` + 用户选择重建。只有来源与用户输入（名字/备注/section 开关/材质选择/映射）不可丢。 |

**由此产生一条必须写死的规则**：每个槽位在 `sectional/` **顶层必须有一份默认版**。因为 `Overwrite` 只能「同名顶替」，不能新增槽位——少了默认版就会出现「明明启用了却没生效」。

---

## 1. 目录形态（目标）

```
<库>/<mod>/
  来源.vpk                     ← 来源（vpk2patch 类，可多个：模型 + 材质）
  来源.patch_0 …               ← 来源（sectional 类）
  sectional/
      9ba626afa44a3aa3.patch_0              ← unit（恒为 0）
      9ba626afa44a3aa3.patch_0.gpu_resources
      9ba626afa44a3aa3.patch_1              ← 材质槽位默认版
      9ba626afa44a3aa3.patch_2 … patch_N    ← 其余材质变体，平铺
      patches.json                          ← 【新】结构文件：每个 patch 组是谁
      custom-model.json                     ← 转换产物（显式路径读，不参与部署）
      sections.json
      components.json
  Overwrite/
      9ba626afa44a3aa3.patch_0              ← 修改后的 unit patch（rekey 产物）
      9ba626afa44a3aa3.patch_1              ← 启用的材质（从 sectional 物化）
      sections.json                          ← Overwrite 自己的 section 绑定
  mod.json                                 ← 【新】名字/备注/持久 id/来源清单/启用选择
  option.json                              ← 仅"真的是别人的 Option"的 mod 才有
  icon.*
```

**两个枚举器必须分开**（这是 P1 的核心，也是现在最容易出错的地方）：

| 枚举器 | 目录 | 用途 |
|---|---|---|
| `EnumerateDeployableFiles` | `[node, Overwrite/]` | 部署、部署索引、"哪些文件会进游戏" |
| `EnumerateModPatchInventory` | `[node, sectional/, Overwrite/]`（去重） | 库扫描、`patchGroups`、内容指纹、内容事实 |

> **为什么必须分开**：如果只有一个枚举器且它只认 `Overwrite`，新导入的 mod（还没物化 Overwrite）会被 `ModLibrarySynchronizer` 判成"没有 patch 的残留目录"而**从库里消失**。如果它只认 `sectional`，关闭的材质又会被部署。两个概念本来就是两件事。

**指纹 ≠ 载荷**：`contentFingerprint` 覆盖 `sectional/` + `Overwrite/` + 结构文件（衡量"内容有没有变"），而不是部署集合。

---

## 2. 阶段表

每阶段结束都必须：`dotnet build` 0 警告 0 错误、Core + CLI 测试全绿、不丢用户输入。

### P0 记账与权威收尾（小，先做）

| # | 事项 | 位置 |
|---|---|---|
| P0-1 | ✅ `meta` 与数据**同事务**写（原来 13 条 `SetMetaAsync` 在 `CommitAsync` 之后逐条 autocommit） | `AssetArchiveIndexService.cs:474-485`、`AdvancedEquipmentIndexService.cs:301-302`、`SqliteSchema.SetMetaAsync` 新增带事务重载 |
| P0-2 | ~~删死枚举 `GameDataIndexState.Invalid`~~ **取消**：它并不是死的——CLI 的 `CliGameDataIndexPlanArtifacts.cs:304` 真的会产出它，只有 GUI 路径不产出。真正的问题是两端语义不一致，留给 P6 统一 | — |
| P0-3 | ~~删 `DerivedStateCoordinator` 的 `deployed` 死字段~~ **暂缓**：`DeployedGraph` 是 `DerivedStateSnapshot` 的组成部分，且参与 `ModUserStatusProjector.Project`。删它要动玩家可见状态投影，风险与收益不成比例。改为**加注释说明它恒为 null 及原因** | `DerivedStateCoordinator.cs:48,135,146` |
| P0-4 | 修 `PageViewModels.cs:2241-2247` 的注释（它说"只处理缺失"，实际 `effectiveRebuildAll` 每次都翻成 true） | 同文件 |

**验收**：`gamedata index apply` 后 `advancedEquipmentFactsCurrent: true`；重建后 `meta` 与数据一致；无行为变化。

### P1 纯删除瘦身（零风险，先做完）

| # | 删什么 | 依据 |
|---|---|---|
| P1-1 | `MaintenanceAnalysis` 整条链（接口方法 / producer / 落盘 / `ModCompatibilityAnalyzer` 注入 / 枚举值 / 中文映射） | 零生产调用方 |
| P1-2 | `data/indexes/mod-data-index/` 持久化 + 3 个死查询方法；详情页那一行改查 `SqliteModFactsStore` | 与 sqlite 逐条重复 |
| P1-3 | `mod-information/Thumbnail_*.json` 落盘 | 内容就是一次 `File.Exists` |
| P1-4 | `mod-facts.sqlite` 两张 `*_snapshots` 表（含 `cache_json` blob）——写成 DROP 迁移 | 无 INSERT |
| P1-5 | 三件套死代码：`ModInformationPropertyKind.DecorationDefinition` / `StoragePaths.DecorationActivationsPath` / `ModAssetRoleFactsService` | 零读写 |
| P1-6 | `ModIndexGarbageCollector` 加「同 kind+node 只留最新 generation」 | 实测 44 个孤儿 |

**验收**：功能无变化；`data/indexes` 变小；启动不变慢。

### P2 布局单一权威（最大，核心）

| # | 事项 |
|---|---|
| P2-1 | 引入 `ModStorageKind`（`Logical` / `Legacy` / `Sectional` / `Vpk2Patch`），**一处判定**：根有 `.vpk`→Vpk2Patch；根有 `.patch_n` 且有 `sectional/`→Sectional；有 patch 无 `sectional/`→Legacy；无 patch 有 marker→Logical。存不存：**不存**（目录的函数），但 `library.json` 可缓存一份便于排查 |
| P2-2 | 拆 `EnumerateDeployableFiles` / `EnumerateModPatchInventory` 两个枚举器 |
| P2-3 | `vpk2patch/` → `sectional/` 改名（含一次性迁移） || P2-4 | 新增 `sectional/patches.json` 结构文件（schema `hd2mod.patch-groups/1`）：`patches[]{index, role:"unit"|"material", variant?, sourceVpk, units?, materials?, textures?}` |
| P2-5 | `DiscoverPatchGroups` 改用 Inventory，避免新 mod 被判成残留目录 |
| P2-6 | 指纹覆盖 Inventory + 结构文件 |

**验收**：老库迁移一次成功（55 节点全部保留）；`deploy plan` 与迁移前**逐字节等价**（迁移不改部署结果）；`library sync inspect` 无漂移。

#### P2-3 执行规范（测绘结果，必须照做）

**三类 token 不能混**。盲替换会同时改掉下面后两类，那是灾难：

| token | 例子 | 动吗 |
|---|---|---|
| **目录名** `vpk2patch/` | `ModNodePayloadLayout.VpkPatchDirectoryName` | **要改** → `sectional` |
| **CLI 子命令名** `hd2mod vpk2patch` | `CliApplication.cs:1999,2012,2017,7698`、`VpkImportService.cs:551,714`、`VpkRebuildService.cs:180` | **不动**（用户敲的命令） |
| **报告文件名** `vpk2patch.json`（在 mod **根上**） | `VpkConversionReport.cs:112` | **不动** |

**最危险的三处：跨程序集字面量，编译不会报错。** `PhysBoneRigSidecar.cs:87`、`PhysBoneContainerEmbedder.cs:233`、`PhysBoneCarrier.cs:372` 各写了一遍 `Path.Combine(modDirectory, "vpk2patch")`。原因是依赖方向：`HD2ModCore → HD2ModAdaptation`、`HD2SourceMigration → HD2ModAdaptation`，反向不成立，所以这两层拿不到 Core 的常量。

漏改的症状（全部静默）：物理参数消失（`Locate()` 返 null → 回退或报"来源没有 jigglebone"）；`EnumeratePatchFiles` 只剩根目录 → 嵌 0 个容器，日志里 `patches: 0` 看着像"来源本来就没有"。

⇒ **建议把常量下沉到 `HD2ModAdaptation`**（它是 Core 与 SourceMigration 的共同依赖，是唯一能三处共享的地方）；否则必须加跨程序集一致性单测。

**执行顺序（每步可编译、可回滚）**：

1. 加 `LegacyVpkPatchDirectoryName = "vpk2patch"`，零行为变化。
2. **读兼容**：`EnumeratePayloadDirectories` 改 `[根, 新名?, 旧名?]`（**最关键**，决定旧库还能不能部署）；`VpkCustomModelFile:31-32`、`CustomModelCatalog:140-141`、`CustomModelRecovery:118-120,128-132` 加第三项；`ObjectTreeImporter:148` 的 `IsVpkPatchDirectoryName` 同时认两名字（否则旧库残留目录会被当成独立选项节点递归导入 → 同一批 patch 部署两次）。
3. 翻常量 `VpkPatchDirectoryName` → `"sectional"`，**与第 4 步同一 commit**。
4. 改三个跨程序集字面量。**第 3、4 步之间不可中断**。
5. 写盘侧验证：`SectionalModBuilder:57`、`VpkImportService:384`、`ModLibraryImporter:1103`、`VpkPayloadSwap:52` 都走 helper，新建 Mod 落在新名下。
6. 测试：24 处字面量（6 个文件）换成常量；保留 2~3 个用例显式用 `LegacyVpkPatchDirectoryName` 覆盖"旧库形状"。
7. 一次性迁移：把每个 mod 的 `vpk2patch/` 目录改名（保只读属性，失败跳过并记账）。
8. 收口：删旧名读分支 + 同步用户可见文案（`VpkPayloadSwap:94`、`ImportService:373`、`CliVpkRekey:144,930`、`VpkRebuildService:119,138,147,265,332,336`、`ModDetailsPageViewModel.Vpk:417,823,836,840`、`ShellViewModel:2452`、`CliApplication:3146`）。
9. 文档：`HD2RuntimeArmorOverride/notes/` 18 篇只在头部加一句"目录名已于 2026-11 改为 sectional"，**不批量替换**（否则历史调查记录变得不可信）。`docs/` 20 篇同理。`HD2ModCli/skills/**` 无需改。

**其余要点**：
- 覆盖顺序**不受改名影响**（顺序由 `[根, 子目录]` 数组顺序决定，与名字无关），坏的是"是否进入第二层"的 `Directory.Exists` 判据（7 处）。
- `ModExporter.cs:249` / `CliModExportPlanArtifacts.cs:516` 只跳过 `"Overwrite"`、**不跳过 payload 目录** ⇒ 导出包结构会从 `vpk2patch/` 变成 `sectional/`（用户可见变化）。P4-5 重做导出时一并统一。
- 别改错目录：`.agents/worktrees/sectional/` 是另一个 worktree，它的 `ModNodePayloadLayout.cs:31` 仍是旧值。

### P3 变体槽位 + 材质折并（含迁移）

| # | 事项 |
|---|---|
| P3-1 | 材质启用状态 → 物化到 `Overwrite/`（硬链接优先，失败回退复制） |
| P3-2 | 启用/停用命令与 GUI 入口（单选） |
| P3-3 | deploy 守卫：sectional mod 无 `Overwrite/` 时报 blocking「还没有生成生效层」 |
| P3-4 | 一次性迁移：43 个材质 Option 折进各自宿主（搬 patch → 重编号 → 删旧节点 → 折并 profile 条目/option 激活） |
| P3-5 | 迁移时把当前启用的材质记为槽位初值（不让用户看到外观变化） |

**验收**：一个 mod 内切换材质 → 部署结果随之改变；关闭的材质不部署；原外观与迁移前一致。

#### P3-4b「生成sectional版」时的材质收集 —— **漏记，未做**（2026-11-06 更正口径）

入口是**已经存在的**那个动作：详情页汉堡菜单里的「生成sectional版」
（`PageActionKind.CustomModel`，`ModDetailsPageViewModel.Actions.cs`）。
它现在做的只是把**模型本体**转成 sectional 产物；缺的是同一步里把配套材质一起收进来。

**正确流程（用户 2026-11-06 明确更正）**：

1. 点「生成sectional版」→ 弹出底栏，要求选择**一件护甲**和**一顶头盔**。
2. 读出这两个目标归档里**所有 unit 的材质引用**（材质 id 集合）。
3. 在 Mod 库里找**提供这些材质 id 的 Mod**：
   - 目标是"纯材质 mod"——只含材质与贴图两类资源；
   - 也允许它含其他资源，但**只取被引用到的材质与贴图**。
4. 把**模型本体 + 这些材质/贴图**一起转换成一个 sectional 产物。

**先前我记的口径是错的**：我写成"索引它子级的材质类 Mod"（按库里的父子关系找）。
真实口径是**按目标护甲的 unit 材质引用反查库内提供者**——父子关系只是旧库的存放形态，
折并之后父子关系已经不存在了，靠它找必然找不到。

**已有的原语（不用新写）**：

| 需要什么 | 现成的 |
|---|---|
| 读某个 unit 的材质引用 | `UnitMaterialReferenceReader.ReadReferenceBindings(tocData)`（`CanonicalCrossArmorOrchestrator.cs:962` 在用）|
| 目标归档里有哪些 unit | `game_data_unit_parts` / 装备权威表（`AdvancedEquipmentIndexService`）|
| 库内谁提供某个材质 id | `IReferenceGraphQueryIndex.FindProviderFactsAsync(assetKeys)`（现在是内存实现）|
| 那个 Mod 是不是"纯材质" | 引用图里的资产类型：只有 material + texture 即纯材质 |

**验收**：选好护甲与头盔后生成 sectional 版，产物里包含被引用材质对应的 patch 组
（`patches.json` 里能看到材质选项），且**不需要**用户事后手动去找材质包。

#### P6-6b 复制 Mod 的**界面入口** —— 只做了 CLI，用户看不到

`mod clone` 已实现并有实测（`c99440d`：命名空间不同、材质 id 零重叠、两套外观可同时部署）。
但文档把它**只**登记在"CLI 门面"下：

- `dual-surface-evaluation` §11.3 第 4 点："这条功能天生适合做成 `hd2mod mod clone <GUID> --variant-name`"
- 本计划 P6-6 也列在「⑦ CLI 门面」

于是我的验收标准停在"有一条命令、命令能跑通"，而**用户根本看不到它**。
**验收**：详情页有一个入口（动作或卡片）能"复制为变体"，复制完新 Mod 出现在库里。

#### 验收纪律（新增，防止同类遗漏）

每个阶段收尾时，必须逐条走一遍**用户可见面**，而不是只看"代码写完、测试通过"：

| 面 | 要看什么 |
|---|---|
| 库页 | 新增/消失了什么，名字对不对 |
| **详情页** | 每张卡的**标题与内容**是否还成立（这次的「VPK 动作」标题就是这样漏的）|
| 配置页 | 条目、顺序、启用状态 |
| 部署结果 | `deploy plan` 的行数/来源是否随操作改变 |
| CLI | 命令是否出现在 `capabilities` 与 `--help` 里 |
| 新导入路径 | **转换产物的形状**是否符合当前设计（P3-4b 就是这样漏的）|

**"机制级通过"不等于"需求满足"。** 我在 ④/⑦ 声称完成时，依据是测试、CLI 行为与真机数据比对，
没有一条是"用户能在界面上做到这件事"——所以 UI 残留与缺失入口对我而言是**构造上不可见的**。

#### P3 / P2-2 执行规范（测绘结果，推翻我原先的"改载荷枚举"写法）

**不要改 `EnumeratePayloadDirectories`。** 它是 `ModLibrarySynchronizer.DiscoverPatchGroups`、`ModLibrarySynchronizer.ComputeFingerprint`、`ObjectTreeImporter.CollectPatchGroups` 的**共同基座**（都走 `EnumeratePayloadFiles`）。改它 ⇒ 所有 VPK 转换 mod 的 patch 组变成空 ⇒ `ModLibrarySynchronizer.cs:63` 不产出记录、`:152-166` 把已登记节点的 `PatchGroups` 清空 ⇒ `ApplyPlanner.cs:89-97` 因 `PatchGroups.Count == 0` **静默 continue、连 warning 都没有** ⇒ **"部署成功、游戏里什么都没变"**。新导入的包还会在 `ModLibraryImporter.cs:178` 被判"没有可导入内容"而丢弃。

**正确做法：加一个新函数，不动基座。**

| 函数 | 目录 | 谁用 |
|---|---|---|
| `EnumeratePayloadFiles` / `EnumeratePayloadDirectories`（**保持不变**） | `[根, sectional/]` | `DiscoverPatchGroups`、`ComputeFingerprint`、`ObjectTreeImporter.CollectPatchGroups`、`ComputeFileGeneration`、`ComputeNodeGeneration` |
| **新增** `EnumerateDeployableFilesByName` | `Overwrite/` 顶层（同名配对 + TOC 可读才接受） | `PatchFileIndexBuilder.cs:42`、`ModInformationPatchGroupAnalysisProvider.cs:101`、`ModContentFactsService.cs:221`、`ModContentFactsService.cs:268`（缓存代，**必须一起改**）、`AssetKeySetProvider.cs:75,104` |

**指纹方向不能反过来。** `ModLibrarySynchronizer.ComputeFingerprint` 只看基座、**不含 Overwrite**。如果改成只算 Overwrite：sectional 变化不再触发"内容变了"（缓存不失效、`ModLibraryService.cs:547-556` 不重部署），而换甲/重建每次都 bump ⇒ **方向完全相反**。所以：

- `contentFingerprint` 继续看**基座**（sectional + 根，外加 `patches.json`）；
- 另立一个**只跟 Overwrite 的派生代**给部署用；`ModInformationCenter.ComputeFileGeneration`（:985）已额外拼 Overwrite 顶层全量，保留这个做法。

**Overwrite 现在是"四个写者、只有一个带 marker"**：`HostDerivedOverwriteService`（带 marker，且 `HelmetToArmorMerged=false` 时**整目录删除**）+ `CliVpkRekey`（:174/:260-270/:291-308/:425-451）+ `ModDetailsPageViewModel.Vpk.cs:920` + `VpkRebuildService.cs:316-350`。

⇒ 在新规则下（Overwrite = 唯一可部署层），`RemoveManagedOverwrite` 等于**删掉整个可部署载荷**。必须先把 Overwrite 变成"有主、幂等、可回滚"的唯一物化者，再谈"只认 Overwrite"。

**另有两个已知坑**：
- `HelmetToArmorMergeService.cs:475-487` 会在 Overwrite 下建**子目录变体**，而 effective 枚举只看顶层（`TopDirectoryOnly`）⇒ 子目录里"已物化"的文件被静默忽略。
- `ApplyPlanner.cs:89-97` 对 `PatchGroups.Count == 0` 静默 continue。必须改成能区分"基底有 patch 但 Overwrite 无对应可部署文件"并给 error/warning。

**`patchGroups` 的语义必须拆**：它记录"基底存储里有哪些 patch 组"，**不等于"可部署"**。否则要么 `ApplyPlanner` 静默跳过，要么 `CliModAnalysis.cs:201` 对每个未物化 mod 报错。

#### 执行顺序修正（2026-11-06，P2 做到一半时发现）

**P2-2 不能先于"Overwrite 归属统一"落地。** 现在 `Overwrite` 里"非同名载荷存在就不部署"这条规则
（`ModNodePayloadLayout.cs:115-147`）是**唯一**挡着"残留文件变成一次额外部署"的东西。
一旦把部署层改成"只认 Overwrite"，这条规则必须换成另一种保证——而那种保证的前提是
**Overwrite 是"有主、幂等、可回滚"的唯一物化者**。

现状是**四个写者、只有一个带 marker**：

| 写者 | 带归属标记 |
|---|---|
| `HostDerivedOverwriteService` | ✅（且 `HelmetToArmorMerged=false` 时**整目录删除**） |
| `CliVpkRekey`（换甲 + sections.json + components.json + 就地烘） | ❌ |
| `ModDetailsPageViewModel.Vpk.cs:920`（就地烘 numIndices） | ❌ |
| `VpkRebuildService.cs:316-350` | ❌ |

⇒ 在新规则下 `RemoveManagedOverwrite` 等于**删掉整个可部署载荷**。所以 P3 的顺序必须是：

1. 先把四个写者收敛成一个物化器（带 marker、幂等、可回滚）；
2. 再接 `EnumerateDeployableFilesByName`；
3. 最后才做变体槽位与材质折并迁移。

**第 1 步已完成**（`88bb80a`）：`ManagedOverwriteDirectory` 成为归属与事务的唯一入口，
四个写者全部认标记。

#### 第 2 步之前必须先解开的一处冲突（2026-11-06 发现）

Overwrite 里现在放着**换甲产物**（unit 被搬到目标护甲自己的键上）。而按目标模型，
Overwrite 应当是：

```
Overwrite = f(section 启用状态, 切换护甲目标状态, 切换材质状态)
```

也就是说**换甲与材质选择是同一层的两个输入**，不是两个各写各的写者。如果只是"再加一个
材质物化器也去写 Overwrite"，它会把换甲产物覆盖成未换甲的基底 unit patch——两套结果互相打架。

⇒ 正确形态是：**换甲不再直接写 Overwrite，而是产出一份"unit 层"结果交给物化器；
物化器把「unit 层 + 启用的材质 patch」一起写成 Overwrite。** 即
`OverwriteMaterializer.Materialize(selection)` 是唯一写者，`rekey` 成为它的一个输入。

这一步会同时动 CLI 的 `vpk rekey` 与 GUI 的「应用/切换」，所以放在下一步单独做，
不夹在归属收敛里。

### P4 mod 自描述

| # | 事项 |
|---|---|
| P4-1 | `mod.json`（schema `hd2mod.mod/1`）：`id`（持久 node id）/ `name` / `notes` / `sources[]` / `variants{slot: chosen}` |
| P4-2 | 优先级**文件夹赢**；写路径先写 `mod.json` 再写 `library.json`；写失败报错但不阻断 |
| P4-3 | 同步器读 `mod.json` + 按持久 id 解析 host（取代旧的 logical marker 与 `option.json` 猜测） |
| P4-4 | `.hd2modmanager-logical.json` 退役（**先给残留清理加目录保护**） |
| P4-5 | 导出 = 复制文件夹（排除 `*.bak-*` / `*.backup-*`） |

**验收**：把一个 mod 文件夹拷进另一个库并重载 → 名字/备注/结构/section 开关/材质选择全在；原库无变化。

### P5 缓存改内存

| # | 事项 |
|---|---|
| P5-1 | `AssetInventory` / `ReferenceGraph` 改会话内存（实测 100 ms/mod） |
| P5-2 | `mod-facts.sqlite` 改内存（CLI 完全不依赖它，只影响 GUI） |
| P5-3 | gamedata：删 `assets` / `asset_archives`（改视图）+ `archive_entries.df` 死列 |
| P5-4 | 4 张 0-reader 表：留一张 `localized_name` 收口，其余删 |
| P5-5 | `unit_parts` / `stream_layouts` 纳入同一次重建（现在服务层两个入口） |
| P5-6 | 社区 hash 表**切断联网**，只留随包快照 |

**验收**：功能不变；配置页打开仍在可接受范围；`gamedata status` 一处给出索引与之全部新鲜度。

### P6 CLI 门面

| # | 事项 |
|---|---|
| P6-1 | `--manager-root` 默认成 exe 目录；`status` 没配好时**必须报 issue** |
| P6-2 | 单命令 `--help`；帮助第一屏指向 `hd2mod-docs\SKILL.md` |
| P6-3 | `doctor --json`（四目录自举 + 库完整性 + 索引新鲜度 + 映射一致性 + 素体状态 + hash 表年龄，每项带 `code/observed/expected/fix`） |
| P6-4 | `repair` 家族（workspace / custom-model-names / library-snapshot / index） |
| P6-5 | `mod rebuild <GUID> [--all]` |
| P6-6 | `mod clone <GUID> --variant-name`（= rebuild + 新 token + 不复用 model + 家族整批 + 删身份文件 + import + 挂宿主） |
| P6-7 | `library cleanup --backups --dry-run` + `library status` 的 `reclaimableBytes` |
| P6-8 | 清掉开发目录残留 `bin\hd2mod.exe`（构建后） |

**验收**：`docs/dual-surface-evaluation-20261106.md` §2 那 7 个故障里 ≥5 个能用 CLI 独立修好。

### P7 架构债（可延后）

| # | 事项 |
|---|---|
| P7-1 | Core 三件套（`GameDataPackageResolver` 501 行含 DSAR/LZ4 / `PatchTocScanner` / `PatchEntryPayloadReader`）收敛到 Adaptation |
| P7-2 | Manager 不再直接 `using HD2ModAdaptation.*` |
| P7-3 | 索引指纹纳入 `archivehashes` / `friendlynames` / `typehash` |

---

## 3. 迁移规范（P2-3 / P3-4 共用）

**一次性迁移**，不做双读兼容。要求：

1. **迁移前整库备份**：`<库>/.hd2modmanager-migration/<时间戳>/` 存 `library.json` / `profiles.json` / `option-activations.json` 的副本。
2. **幂等**：迁移标记写进库，重复运行不重复搬运。
3. **可回滚**：迁移前记录每个 mod 的目录快照（文件清单 + 大小 + mtime），失败时能列出"哪些没搬完"。
4. **不丢用户输入**：名字 / 备注 / section 开关 / 当前启用材质 / profile 成员关系 / 护甲映射，逐项有对应的搬运或显式"无法搬运"报告。
5. **只搬不猜**：任何无法判定的情况（例如某材质 Option 找不到宿主）→ 保留原地 + 报告，不删。

---

## 4. 每阶段的固定收尾动作

1. `dotnet build` 三个项目：0 警告 0 错误。
2. `dotnet test`：Core + CLI 全绿。
3. 更新本文件的进度勾选，并在 `docs/dual-surface-evaluation-20261106.md` 记一条"实施与设计的分歧"（如果有）。
4. 提交信息写清"为什么"，不只写"改了什么"。
5. 涉及库结构的改动，在真机库 `E:\mod` 上跑一次只读校验（`library sync inspect`），确认无漂移。

---

## 5. 进度

- [x] P0 记账与权威收尾（P0-1 已做；P0-2 取消——`Invalid` 是活的，CLI 会产出；P0-3 暂缓——删它要动玩家可见状态投影）
- [x] P1-1 删 `MaintenanceAnalysis` 整链（`884bd87`）
- [x] P1-2 删 `mod-data-index` 持久化（`735b625`）
- [x] P1-3 删 `mod-information/Thumbnail_*.json` 落盘（`96ed70c`，登记范围 → `Session`）
- [x] P1-4 删 `mod-facts.sqlite` 两张 `*_snapshots` 表（`96ed70c`，老库 `DROP TABLE IF EXISTS`）
- [x] P1-5 删三件套死代码（`96ed70c`）
- [x] P1-6 GC 补两条回收规则（`96ed70c`：同 kind+node 只留最新 generation；清死目录 `mod-data-index/`）
- [ ] **P2 布局单一权威** ← 进行中
  - [x] P2-3a 目录改名 `vpk2patch/` → `sectional/` + 读路径双名兼容（`a42a08e`）
  - [x] 三处跨程序集字面量收敛到 `HD2ModAdaptation.ModContentLayerNames`（`a42a08e`）
  - [x] P2-3b 一次性目录迁移（`26507a7`；GUI 在残留清理前、CLI 在写租约内）
  - [x] P2-1 `ModStorageKind` 一处判定（`ed4a178`；CLI 三处输出 `storageKind`）
  - [x] P2-4 `sectional/patches.json` 结构文件（`ed4a178`；导入与换载荷后各写一次）
  - [ ] P2-2 `EnumerateDeployableFilesByName`（Overwrite 顶层；基座不动）← **改到 P3 做**
  - [x] P2-3 真库改名**已执行**（2026-11-06）：49 个 `vpk2patch/` → `sectional/`；
        库 53 节点无增减；再对账 0 变化（已收敛）。备份在 `E:\tmp\lib-backup-20261106-150312`。
        过程中撞出一个真 bug 并修掉（`8a51be1`）：改名会作废 `library sync` 计划。
  - [ ] P2-3c 收口：删旧名读分支、同步用户可见文案 ← 已有真库跑过一轮，但仍建议再等一个版本
- [ ] P3 变体槽位 + 迁移
  - [x] P3-1 托管 Overwrite 归属收敛（`88bb80a`：`ManagedOverwriteDirectory` + 新标记 `VpkRekey`，四个写者全部认标记）
  - [x] P3-2a-i `OverwritePatchInstaller`（`35015ea`：装一份 patch + sidecar 进 Overwrite，换甲两条重复实现收敛）
  - [x] P3-4-i 折并只读计划 `MaterialOptionFoldPlanner`（`f439ba9`；真库试算 43 成员 / 0 阻断）
  - [x] P3-2a-ii `OverwriteMaterializer`（`c233c08`：Overwrite = unit 层 + 选中的那一份材质）
  - [x] P3-4-ii 折并执行器 `MaterialFoldExecutor`（`feecd89`：只往宿主里搬，不动库、不删成员）
  - [x] P3-4-iii `hd2mod library material-fold plan`（`2f2dbf8`：只读自查命令）
  - [x] P3-2a-iii 材质选项 = **多选**、每个选项一个 patch、模型自带的叫「模型内置材质」（`a929ea0`）
  - [x] P3-2a-iv 自查并删掉自己加的四处防御（`a929ea0`）
  - [x] P3-bundle `library material-fold apply`（`6c6e074`：搬 → 物化 → 退役一条命令；真库副本端到端验证通过）
  - [x] **P3-4 真库折并已执行**（2026-11-06）：9 个宿主、129 个文件、退役 43 个节点、无告警；
        库节点 53 → 10；备份在 `E:\tmp\fold-backup-20261106-152525`。
        **决定性验证**：折并前后 `deploy plan` 的 `(归档, sidecar 类型, 内容 sha)` 集合**完全相同**
        （81 条 / 76 种，两遍一致）——折并只改编号与归属，要部署的字节一个不少、一个不多。
        43 个退役材质包的文件夹仍在库根上（载荷已空），未被重新登记为节点。
  - [x] P3-2b 部署层切到 Overwrite（`5705eab` + `98eed83`：判定改成"看有哪一层"，见下）
  - [x] **判定算法（`98eed83`，用户纠正后重写）**
    ```
    ① Overwrite 里有有效 patch            ⇒ 只部署它，这条结束（对所有类型都适用，含 legacy）
    ② sectional / vpk2patch：有基底 ⇒ 就部署它；没有 ⇒ 跳过 + 说明
    ③ legacy / logical：根目录有 patch ⇒ 部署；没有 ⇒ 跳过 + 说明
    ```
    **判定不写盘**（`76a6bb5`）：把基底物化成 Overwrite 是写操作，只发生在用户
    真正动手的地方（改材质选择、折并），不发生在 `deploy plan` 这种"看一眼"的时候。
    实测真库：修之前跑一次计划会在 40 多个 Mod 里凭空造出 `Overwrite/`；修之后 8 → 8，不再新增。
    不逐个 patch 判断，只看"有哪一层"。**「每个槽位在基底里必须有一份默认版」这条规则作废**
    （那是我自己多加的约束，部署器根本不按槽位判断）。

    **"明明启用了却没生效"现在是具名诊断**：跳过某个 Mod 时产出
    `ModNotDeployable` 警告（带原因），经 `ApplyPlanner` 与 `CliDeploymentPlanArtifacts`
    进入 `deploy plan` 的 diagnostics。真机验证过：
    ```
    warning ModNotDeployable | 这个 Mod 是 Vpk2Patch 类，但没有基底目录（sectional/），没有可部署的 patch。
    ```
    （旧行为是 `ApplyPlanner` 里静默 `continue`。）
  - [x] P3-3 材质选项可切换（`0c31ccf`：`<mod>/.hd2modmanager-mod.json` + `mod material set` + `mod show` 选项表）
  - [x] P3-5 启用集合原样搬运 ← 折并时写入设置文件，观感不变

#### P3 完成标志

| 能力 | 入口 |
|---|---|
| 看折并计划 | `library material-fold plan`（只读） |
| 执行折并 | `library material-fold apply --confirm`（搬 → 物化 → 退役） |
| 看材质选项表 | `mod show`（`patchFileName` / `optionName` / `enabled`） |
| 切换材质 | `mod material set <GUID> <patch1,patch2\|none> --confirm` |
| 部署集合 | 只认物化过的 `Overwrite/`；未启用的材质留在基底目录、不部署 |

#### P3 端到端闭环（`5705eab`）

`Elise_Bill` + 3 个材质包 → `material-fold apply`（启用 A1主体）→ `deploy plan`：

| 部署计划里的来源 | 大小 | 去向 |
|---|---|---|
| `Overwrite/9ba….patch_0` | 230,218 B，sha `4913b8f6…` | patch_0（**换甲产物，字节与折并前一致**）|
| `Overwrite/9ba….patch_1` | 29,812 B | patch_1（唯一启用的材质）|
| ~~`patch_2` / `patch_3`~~ | — | **不在计划里**（未启用，留在基底目录）|

⇒ 折并前后进游戏的文件**一个不差、一个不多**。P3 的核心机制至此闭环。

#### P3-bundle 真库副本端到端验证（`6c6e074`）

复制 `Elise_Bill` + 3 个材质包 + 三个状态文件到 `E:\tmp\fold-test2`，跑 `material-fold apply`：

```
材质折并完成：1 个宿主、搬了 9 个文件、退役 3 个材质节点。
```

| 检查项 | 结果 |
|---|---|
| 基底目录新增 | `patch_1..3` + sidecar + `patches.json` ✅ |
| **Overwrite 的 patch_0 字节未变** | sha `4913b8f6…` / 230218 B，与折并前一致 ✅ |
| Overwrite 新增 | `patch_1`（唯一启用项）+ 归属标记 ✅ |
| 未启用的 `patch_2/patch_3` | 留在基底目录、**不进** Overwrite ✅ |
| 部署集合 | `patch_0 + patch_1`（+ sidecar + sections/components）✅ |
| 成员 payload | 已清空 ✅ |
| 库节点 | 53 → 50 ✅ |

**一处刻意的"只加不删"**：老库里每个换过甲的宿主，`Overwrite/` 有换甲产物却**没有归属标记**
（P3-1 之前写的）。整体重建会把 unit patch 换成未换甲版本、让用户选的护甲当场失效；
拒绝非托管目录则卡住每一个现有用户。所以迁移只做加法——把材质装进现有 Overwrite、
补一枚标记，不删任何东西。认领是迁移该做的事。

**启用状态是原样搬过来的**：折并前启用了哪些材质，折并后就往 Overwrite 里装哪些，
所以用户看到的观感不该有任何变化（P3-5 因此不再单独需要）。

#### 设计纠正：材质选项是**多选**，不是单选槽位（2026-11-06）

我一度把它做成"单选、折并后只保留顺序最靠后的那一个"。这是错的：

- 多个材质包同时启用、材质 id 互相冲突、后加载的覆盖先加载的——**这就是今天在用的方式**，
  而且有效。换到同一个 mod 里不会改变这件事。
- 管理器没有理由替用户做减法；限制"只能启用一个"除了制造麻烦以外完全没用。

正确形态：

| 项 | 规则 |
|---|---|
| 选项粒度 | **一个材质选项 = 一个 patch** |
| 选中数 | **任意多个**，勾了几个就部署几个 |
| 模型自带的材质 | 也是选项之一，名字固定 `模型内置材质` |
| `Overwrite/` | unit 层 + **所有启用**的材质 patch |

#### 自查：我自己加的防御（已删）

| 位置 | 原来 | 改法 |
|---|---|---|
| `OverwriteMaterializer` | 某个启用的材质缺失 → 否掉整次 | 跳过、点名、其余照常 |
| `MaterialOptionFoldPlanner` | 成员异常 → 阻断整个宿主 | 只跳过那个成员，其余照折 |
| `CliMaterialFoldPlan` | "同时启用 N 个"当警告 | 删掉，只陈述事实 |
| `SelectedTargetPatchFileName` | 推断"该留哪一个" | 删掉，启用状态就是集合 |

**保留的守卫只有两类**：保护用户文件（拒绝覆盖非管理器生成的 `Overwrite`）、环境事实
（目录/patch 不存在、计划已过期）。这两类都不替用户做决定。

#### P3-4 真库实跑（`2f2dbf8`）

```
hd2mod library material-fold plan --manager-root . --library E:\mod
→ 9 个宿主、43 个成员、0 个阻断项
```

**跑出一个必须修的错**：第一版"折并后应选中 patch_1"，但实际有宿主同时启用多个材质：

| 宿主 | 同时启用 | 折并后应保留 |
|---|---|---|
| Lilium_Sancta_Bill | 5 | `patch_11`（F1瞳色银灰）|
| Lunalice_Bill | **12**（全部）| `patch_12`（E3瞳色紫2）|
| 其余 7 个 | 1 | `patch_1` |

正确语义是**启用项里顺序最大的那个**（部署按顺序叠 patch，后者覆盖前者）。
另外顺序必须取 `OptionActivationSnapshot.EffectiveOrder`——`Metadata.OptionOrder`
在这些 mod 上普遍为 null，全当成 `int.MaxValue` 之后就只剩按名字排，
而名字顺序与部署顺序无关。

⇒ 这同时是用户抱怨的现场：**12 个材质全启用、只有一个赢**。折并之后它们变成
12 个可选槽位，只保留当前真正生效的那一个。

#### P3-2a 的真机基线（2026-11-06 实测）

`E:\mod` 里每个换过甲的 mod，`Overwrite/` 的内容都是同一形状：

| 内容 | 说明 |
|---|---|
| `9ba626afa44a3aa3.patch_0` + `.gpu_resources` | 换甲产物（unit 被搬到目标护甲自己的键上） |
| `sections.json` | 按**换甲后**的 patch 重算的 section 绑定 |
| `components.json`（部分 mod 有） | 翻译到目标键空间的开关表 |

而宿主自己的 `sectional/` 里**只有 `patch_0`**（`恋星milfy_bill` 例外：它有一个 3 条 VMT 的
`patch_1`，那是模型自带的兜底材质，不是变体）。

⇒ **材质变体今天不在宿主目录里**，它们仍是兄弟 Option mod。所以 P3-2a-ii 的物化器
必须先有"材质 patch 在宿主里"这个前提 —— 也就是说 **P3-4 的折并是 P3-2b 的前置**，
两者不能分两轮各做一半而不留中间可用状态。

#### P3-4 折并的真库试算（2026-11-06，`MaterialOptionFoldPlanner`）

按 `library.json` 的宿主—Option 关系 + 各自基底目录实算：

| 宿主 | 现有 patch | 折入后 | 成员数 |
|---|---|---|---|
| `Lilium_Sancta_Bill` | `[0]` | `patch_1..patch_12` | 12 |
| `Lunalice_Bill` | `[0]` | `patch_1..patch_12` | 12 |
| `MourningVeil_Bill` | `[0]` | `patch_1..patch_10` | 10 |
| `Elise_Bill` | `[0]` | `patch_1..patch_3` | 3 |
| `[huahua]Chiyu公式服` | `[0]` | `patch_1..patch_2` | 2 |
| `Manuka_JABIS_Bill` / `Manuka_LaFiore_Bill` / `l4n…信浓_bill` | `[0]` | `patch_1` | 各 1 |
| `恋星milfy_bill` | `[0, 1]` | `patch_2` | 1（宿主自带的 `patch_1` 是兜底材质，不冲突） |

```
hosts with option children: 9
members total: 43   blockers: 0
```

**0 个阻断项**，每个成员的基底目录里都恰好一份 `patch_0`。说明折并的输入形状完全规整。

#### 由此确定：P3 必须**一次原子交付**

折并会让宿主里出现最多 13 个 patch。**只要部署还读 `sectional/`，这 13 份就会同时部署、
按序号互相覆盖 —— 用户看到的材质是错的。** 所以下面三步不能分三轮回：

1. 折并（把成员 patch 搬成 `patch_1..N`、来源 VPK 平铺进宿主根、退役成员节点）
2. 物化（`Overwrite/` = `patch_0` + **选中的那一份**材质 patch）
3. 部署层切到 `Overwrite`（`EnumerateDeployableFilesByName`）

在此之前，现有部署语义（`sectional/` + `Overwrite` 同名覆盖）必须保持**一字节都不动**。
- [ ] P4 mod 自描述
  - [x] P4-1 `mod.json` 字段：`id` / `name` / `notes` / `enabledMaterialPatches`（`1f85883`）
  - [x] P4-2 文件夹赢：扫描读它、改名/备注同时写它、老库一次性认领（`1f85883`）
  - [ ] P4-3 `.hd2modmanager-logical.json` 退役（**先给残留清理加目录保护**）
  - [ ] P4-4 导出 = 复制文件夹（排除 `*.bak-*`）
- [ ] P5 缓存改内存
  - [x] P5-1 / P5-2 Mod 事实改会话内存（`cd86dae`：删掉 `SqliteModFactsStore` 178 行 SQL 管道，21.7 MB → 0）
  - [x] P5-2b 废弃库整文件回收（`c522c2a`：GC 不再按行修剪，直接删 `mod-facts.sqlite` + `-wal` + `-shm`）
  - [ ] ~~P5-3 gamedata：删 `assets` / `asset_archives`（改视图）+ `archive_entries.df` 死列~~
        —— **评估后不做**：实测 `archive_entries` 不完整，`asset_archives` 无法由它推出。见下
  - [x] P5-4 删掉五张「只写不读」的表（`2a370c3`：`archive_domains` / `game_data_archive_names` /
        `game_data_authority_entities|issues|resources`，全仓库零 SELECT；真机 16 → 11 张表，
        数据量不变，文件大小不变——`DROP TABLE` 只标页为空闲）
  - [x] P5-5 `unit_parts` / `stream_layouts` 纳入同一次重建（`94467c1`）
        ——**这是正确性问题不是一致性问题**：`ClearIndexDataAsync` 会清空这两张表，
        而 `RebuildBase` 原来不重建它们 ⇒ 命令报成功、索引里零条部位事实。
        真机：`部位事实当前` 从 False 变 True；行数恢复 16,569 / 7,415；
        代价是 RebuildBase 从 21.6 s 变 88.4 s

#### P5-3 调查结论：两次实测都失败，需要先弄清投票链路（2026-11-06）

计划写的是「删 `assets` / `asset_archives`（改视图）+ `archive_entries.df` 死列」。
**用真库索引反推之后，四个事实：**

| 事实 | 证据 |
|---|---|
| `df` = **重复频次**，不是死列 | `IsFiltered(df, totalArchives, settings)` 每次都读 |
| `archive_entries.df` 是**硬编码的 1**（占位符），真实频次只在 `assets.df` | 建表插入 `VALUES(...,1,...)` |
| `asset_archives` 与 `archive_entries` **逐行相同**（357,695 = 357,695） | 真库 `asset-index.sqlite` 实测 |
| 抽 12 个资产核对：`assets.df` == 条目数 == 不重复归档数 == `asset_archives` 行数 | 同上，全部一致 |

⇒ 「`assets` 改视图」一定会读到占位符 1，投票会算错。**这半边放弃。**
「`asset_archives` 改视图」看起来安全（行数完全相同），实测仍有 1 条用例红：
`ReplacementTargetDeriverTests.DeriveAsync_ReturnsTopAndOthers` —— A 应得 3 票，只得到 1 票。
说明投票链路里还有我没看清的东西（条目表可能不是"每个资产每个归档一行"的唯一来源）。

**两次尝试都完整撤回，未提交。** 过程中学到两条对以后有用的：

1. **`SchemaVersion` 进了索引指纹** → 改 schema 会自动触发重建，不用担心新旧库混杂。
2. **`DROP TABLE IF EXISTS` 遇到视图会报错并中止整个 schema 脚本** ——
   表现是后面所有建表都没执行，报 `no such table: game_data_authority_kits`。
   一次性"表→视图"迁移必须放在 C# 侧按 `sqlite_master.type` 判断，不能写进脚本。

⇒ 要做这一项，下一步是**把 `VoteArchivesAsync` / `GetArchivesForAssetAsync` 的实际取数打印出来对照**，
而不是继续改代码试错。

**已经做了（2026-11-06，临时诊断用例打印后删除）——结论：P5-3 的「改视图」半边做不到。**

同一份夹具（3 个归档 a/b/c，资产 (1,1) 在三个里都有、(1,2) 在 a/c、(1,3) 只在 a）：

```
archives_total(meta)   => 3
entries(1,1)           => [b|0|1]              ← archive_entries 里只有 1 条
assets(1,1)            => [3]                  ← df = 3，确实是"出现在几个归档里"
asset_archives(1,1)    => [a ; b ; c]          ← 3 条，完整
asset_archives(1,2)    => [a ; c]
votes                  => a=3 b=1 c=2          ← 正确结果
```

**`archive_entries` 并不完整**（这里只有 1 条），而 `asset_archives` 是全的。
所以 `asset_archives` **不是** `archive_entries` 的冗余副本，改成视图必然丢行——
这正是投票用例从 a=3 掉到 a=1 的原因。

真库里两者行数相同（357,695）只是那份数据的巧合，不能当普遍性质用。

⇒ **P5-3 的「把 `assets` / `asset_archives` 改视图」判定为不可做**，除非先查清
`archive_entries` 为什么会缺行（谁在过滤条目、按什么条件）。`archive_entries.df` 那个占位符 1
同理需要一并处理。这一项从这里起标为**评估后不做**，不再重复尝试。
  - [ ] P5-6 社区 hash 表切断联网 ← **需要用户决定**：`GitHubAssetMetadataSyncService`
        是**用户手动触发**（界面「同步资产元数据」/ `assets metadata plan`），不是启动时自动联网。
        删掉它等于删一个功能，不属于"修 bug"，所以没有擅自做
- [ ] P6 CLI 门面
  - [x] P6-3 `doctor`（`d7b536a`：一条命令 + 每条结论带 observed/expected/fix + 退出码 0/2）
  - [x] P6-7 `library cleanup --backups`（`5b26b35`：先报大小、`--confirm` 才删；与 doctor 共用扫描）
  - [ ] P6-4 `repair` 家族
  - [x] 前置（`88fc05d`）：VPK 重转/换甲那簇服务搬进 Core
        （`VpkRebuildService` / `VpkModWardrobeService` / `Hd2ModProcess` / `VpkRekeyService` /
        `VpkPhysBoneEmbedder`，约 1100 行，只依赖 `System.*` + `HD2ModCore`）。
        唯一耦合点是 `SettingsService`，换成显式参数 `VpkHostContext(GameDataFolder, Paths)`。
  - [x] P6-5 `mod rebuild`（`097f83f`：真机跑通，7 Unit / 25 材质 / 换甲 / section 烘焙）
  - [x] P6-6 `mod clone`（`c99440d`：真机验证材质 id **零重叠**，两套外观可同时部署）
  - [x] P6-4 `repair` 家族
    - [x] `repair library-snapshot`（`db06a40`：从磁盘重建，**按文本抢救原 id**，不碰 profiles.json）
    - [x] `repair index`（`587f4f4`：plan→validate→apply 三步收成一条，走同一套实现）
    - [x] 顺带 `custom-model rename`（`9109abd`：只改 name，不重造 unit id；修 `TryReadName` 漏子目录 + 只读文件写不进）
    - [ ] `repair workspace`（补建管理器数据目录）—— 价值低，未做
  - [x] P6-9 `custom-model rename`（`9109abd`）
  - [x] P6-10 `settings get/set`（`c3fbb6d`：真映射模式这类只存在设置文件里的开关，没有 GUI 也能改）

**验收（`docs/dual-surface-evaluation-20261106.md` §2 的 7 个故障，要求 ≥5 个能独立修好）**

| # | 故障 | 状态 | 命令 |
|---|---|---|---|
| 1 | 素体名变 `custom-xxxx` | ✅ | `custom-model rename`（`9109abd`）|
| 2 | 应用映射"237 条全跳过" | ✅ 多步 | `gamedata index …` → `armor-registry apply` |
| 3 | 映射页一片「读取失败」 | ✅ | `armor-registry status`（报行号）→ `armor-registry restore --confirm`（`5190e67`：apply 前留滚动备份）|
| 4 | mod 看得见但不生效 | ✅ | `doctor` → `deploy plan/validate/apply` |
| 5 | 库元数据加载失败 / 库看起来空 | ✅ | `repair library-snapshot`（`db06a40`，保住原 id）|
| 6 | sectional 组件树不显示 | ✅（落点仍需指定）| `wardrobe sections` |
| 7 | 真映射关不掉 / 开了没效果 | ✅ | `settings set ArmorMappingMode 1 --confirm`（`c3fbb6d`）|

**7 / 7 可独立修好**，超过验收线（≥5）。
- [ ] P7 架构债

### P1 收尾时的实测数字

| 项 | 数字 | 说明 |
|---|---|---|
| `mod-information` 文件数 | 164（4 个非标准形状被保留） | generation 孤儿当前为 0——真机索引在本次重构前刚重建过 |
| `mod-data-index` 残留 | 53 文件 / 2.4 MB | 会被 P1-6 的新规则一次性清掉 |
| `MaintenanceAnalysis` 落盘 | 0 文件 | 印证"零生产调用方" |

### P2-3a/b 的真机验证（2026-11-06）

`E:\mod` 上 49 个 mod 仍是旧名 `vpk2patch/`、0 个新名——正好是改名当天最坏的情形。
用新构建跑只读 `hd2mod library sync inspect`：

| 指标 | 结果 | 含义 |
|---|---|---|
| `added` | 0 | 没有 Mod 被当成新目录 |
| `missing` | 0 | 没有 Mod 被当成"已删除" |
| `changed` | 52 | **全部是指纹差异**，`patchGroups` 数量一个都没变 |
| `patchGroups` 变了的节点 | **0** | 读兼容成立：49 个旧名目录的 patch 组全部仍然可见 |

⇒ 如果只认新名，这里会是 `changed` 全为 0、`missing` 全为 52，且部署计划静默跳过。

**指纹差异是既有的、与本次改动无关**：`library.json` 存于 12:52:00，而 49 个
`9ba…patch_0` 的 mtime 集中在 **12:52–12:56**（晚于存档时间），指纹含 mtime，
所以全部判为"内容变了"。跑一次 `library sync apply` 重新记录即可，不需要任何修复动作。

### 环境注意事项

`helldivers2.exe` 运行时，CLI 的 5 个 `deploy` 类测试会因 `GameProcessRunning` 守卫而失败——**这是设计行为，不是回归**。跑全量测试前先确认游戏已退出；管理器运行时则会锁住 DLL 导致 Manager 构建 MSB3027。
