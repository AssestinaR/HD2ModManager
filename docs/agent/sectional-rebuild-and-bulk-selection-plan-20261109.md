# sectional 系列重构建 + 多选局部批量重构 · 规划（2026-11-09）

> 本篇是**实施计划**，口径正本是 `docs/agent/sectional-compile-base-20261108.md`（四段链路）。
> 一句话：这次要补的是四段链路里 **「转换」段** 的第二个实现（`legacy2sectional`），
> 外加一个**入口**（多选底栏的「重构」按钮 + 点击时检测）。
> 与既有口径冲突时以本篇为准，并回写 `sectional-compile-base`。

---

## 0. 结论先行

1. `sectional` 与 `vpk2patch` **从 `sectional/` 往后完全同构**，编译段已经是共用的，不需要改。
2. 两者唯一的差别是「来源」和「来源 → `sectional/` 的那一步」，而**后者现在根本不存在**，
   只有一个名字 `legacy2sectional` 出现在文档里（全仓代码零引用）。
3. 现有「生成sectional版」不是这条链路：它是**生成一条新 patch** 再把它搬进 `sectional/`，
   根上什么都不留，也没有任何"转换依据"文件 —— 所以它的产物不可重构建。
4. 本次要做两件事：**把转换段补齐**（来源落位 + 依据文件 + `legacy2sectional`），
   以及**多选底栏的局部批量重构入口**。

---

## 1. 已核实的事实（含证据）

### 1.1 两档存储形态（同一套"根=来源、sectional=产物"结构）

| | 根上（来源） | `sectional/`（产物） |
|---|---|---|
| `Vpk2Patch` | `*.vpk` × N、`vpk2patch.json`、`wardrobe.json` | `*.patch_N` + `.gpu_resources`、`patches.json`、`sections.json`、`components.json`、`custom-model.json`、`physbone-rig.bin` |
| `Sectional` | **来源 patch（`*.patch_N`）+ 转换依据文件** ← 本次新增的目标形态 | 同上 |

实测样本：`E:\mod\Elise_Bill`（vpk2patch）根上 4 个 `.vpk` + `vpk2patch.json`(45KB) + `wardrobe.json`；
`sectional/` 里 `patch_0..3` 合计约 53MB，`patches.json` 逐个 patch 记 `role`/`patchIndex`/`sourceVpkName`。

### 1.2 现有链路的实际形状

**vpk2patch（完整）**

- 导入：只把来源 `.vpk` 平铺进条目根，**不转换**（`VpkImportService`，注释原文"导入只做两件事：把每组来源放进一个条目，然后对每个条目跑一次标准重构建"）。
- 转换 = 重构：一根方法（`VpkRebuildService.RunAsync`）
  - `:143` 根上找来源 VPK，0 个直接失败
  - `:165` 读 `vpk2patch.json`（命名空间 + 转换参数）
  - `:258` CLI 转到临时目录
  - `:292-324` 材质塌陷守门（少一个材质就拒绝替换）
  - `:346` `VpkPayloadSwap.Swap` 整批换进 `sectional/`
  - `:351+` section 组按**这次转出来的** `sections.json` 重算
  - `:401-440` 回写 `vpk2patch.json`
  - `:481` `ModCompileService.CompileAsync` → `Overwrite/`
- 编译：`ModCompileService` → `SectionalCompiler`。**只读 `sectional/` + `wardrobe.json` 条件，不关心来源。**
- 部署：`ModDeployableFiles.Resolve` 第一分支（`ManagedOverwriteDirectory.IsManaged` + 可读 TOC）→ **只看 `Overwrite/`**。

**sectional（只有"生成即完成"）**

- 入口：`PageActionKind.CustomModel`「生成sectional版」（`ModDetailsPageViewModel.Actions.cs:51`）。
- `CustomModelConversionService.GenerateAsync`：从来源 Mod 的 TOC 里挑子集 → **写一条新的 patch** + `custom-model.json`（新 unit id）。
- `SectionalModBuilder.BuildAsync`：
  - `:57-67` `RelocateToPayloadDirectory` 把**新 patch + custom-model.json 搬进** `sectional/` → **根被清空**
  - `:99-101` `SectionalSplitter.ExecuteAsync(payload, gameData, payload)` 拆 7 槽
  - `:110-116` `CustomModelSectionalCompaction.PruneMergedPiecesAsync` 剪枝
- 之后直接 `ImportPathAsync` 进库。**没有重构，没有依据文件。**

### 1.3 关键判据的现状

- `IsVpkMod(dir)` = `sectional/` 或 `Overwrite/` 里有 `sections.json`（`VpkModWardrobeService.ResolveEffectivePatchDirectory`）。
- 「生成sectional版」的可见性 = `CanUsePatchTools && !IsVpkMod`（`ModDetailsPageViewModel.Vpk.cs:92`）
  ⇒ **来源只能是 legacy**（vpk2patch 与 sectional 都不会出现这个动作）。这一点与需求一致，已核实。
- `RebuildVpk`（「用来源重构建」）可见性 = `HasSourceVpk`，注释明写"转出来的分件模组没有来源包"。

### 1.4 已有的、可直接复用的零件

| 零件 | 位置 | 用途 |
|---|---|---|
| `PatchSubsetWriter.WriteSelectedAsync(sourceToc, outDir, selections)` | `HD2ModAdaptation/PatchReconstruction` | 按 asset key 从来源 patch 抽一份干净 patch |
| `SectionalSplitter.ExecuteAsync(sourcePatchOrDirectory, gameData, outputDir)` | `HD2ModCore/Infrastructure/SectionalSplitter.cs` | 7 槽拆件；输入可以是**目录** |
| `CustomModelSectionalCompaction.PruneMergedPiecesAsync` | 同上目录 | 剪掉并入宿主的悬空组件 |
| `SectionalPatchManifestFile.Build/TryWrite` | 同上 | 生成 `sectional/patches.json` |
| `SectionalCompiler.CompileAsync` | `HD2ModCore/Infrastructure/Sectional/` | 编译（**已与来源解耦**） |
| `ManagedOverwriteDirectory.Prepare/Commit` | 同上 | `Overwrite/` 归属与事务 |
| `OptionRelationshipGraph.GetDescendantOptions(id)` | `HD2ModCore/Infrastructure` | 递归取后代选项，带环保护 + 去重 |
| `VpkBulkRebuildService.EnumerateTargets/RunAsync` | `HD2ModManager/Services` | 批量目标枚举 + 并发执行器 |

---

## 2. 目标模型

```
导入（拷贝来源 patch 到根 + 写依据文件）
   → 转换 legacy2sectional（读根上来源 + 依据文件 → 写 sectional/）
   → 编译（条件：目标护甲 / section 开关 / 材质开关 → 写 Overwrite/）
   → 部署（只看 Overwrite/）
```

`vpk2patch` 与 `legacy2sectional` 只在**第二段**不同；第三、四段完全共用，一行不改。

---

## 3. 协议变更

### 3.1 新增 `sectional-source.json`（转换依据，放 Mod 根）

```jsonc
{
  "schema": "hd2mod.sectional-source/1",
  "generatedUtc": "2026-11-09T22:00:00+08:00",

  // ① 用户选择的权威设置：来源里要转成 sectional 的护甲 / 头盔（**来源选择，不是目标**）
  //    任意一者可空，但不能都空 ⇒ 都空时"没有来源"，不允许提交
  "armor":  { "archiveId": "…", "name": "…" },   // 可 null
  "helmet": { "archiveId": "…", "name": "…" },   // 可 null

  // ② 来源条目（主 + 子级递归）。顺序要留住：部署按"同名后者覆盖"，来源之间
  //    的覆盖关系必须可复现
  "sources": [
    { "nodeId": "…", "name": "…", "order": 0, "relativePath": "…" }
  ],

  // ③ 干净资产白名单：由 ① 的 unit 表推出，转换时据此从来源抽
  "assetKeys": {
    "units":     ["…"],
    "materials": ["…"],
    "textures":  ["…"]
  }
}
```

三条设计取舍：

- **只存输入，不存产物**。产物由 `sectional/patches.json` 描述。两个文件各管一头，避免两处真相。
- **`sources[].name` 必须存**：材质组的命名靠它（patch 文件本身没有名字），而库里条目可能已改名或删除。
- **白名单显式存**，不在转换时重推：重推依赖游戏资产索引的当前状态，一旦游戏更新导致 armor 表变化，
  重推会静默换一套 unit；存下来才能**比对并报告漂移**。

### 3.2 `patches.json` 字段泛化

- `SectionalPatchGroupEntry.SourceVpkName` → `SourceName`。
- 读旧库时 `sourceVpkName` 仍然认（`ReadableSchemas` 同款做法：旧键继续能反序列化）。
- 内置材质沿用现成常量 `SectionalPatchManifest.BuiltInMaterialOptionName = "模型内置材质"`。

### 3.3 `ModStorageKindResolver` 判据

顺序改为：

```
有来源 .vpk                     → Vpk2Patch
有 sectional-source.json        → Sectional     ← 新增，放在"有 payload"之前
有 payload 目录（sectional/ 或旧名）→ Sectional
根上有 patch                     → Legacy
```

放在"有 payload"之前的原因：导入那一刻 payload 还是空的，但依据文件已经写好，
节点必须立刻被判成 `Sectional`，否则详情页会给它挂上 legacy 的动作集。

`ModStorageKindResolver` 自述是"唯一判定处"，改这里即可；三个消费方
（部署解析 / 部署索引 / 内容事实与详情页）自动跟随。

---

## 4. 工作分解

### W1 协议层（`HD2ModCore`）

- 新增 `SectionalSourceDocument` 领域记录 + `SectionalSourceFile`（读/写/校验）。
- `ModStorageKindResolver` 增加依据文件分支。
- `SectionalPatchGroupEntry.SourceVpkName` → `SourceName`（兼容旧键）。
- **验收**：给定一份依据文件，`Resolve()` 返回 `Sectional`；读旧库不受影响。

### W2 「生成sectional版」改为「建条目 + 拷来源 + 立刻转换」

- 不再走 `CustomModelConversionService.GenerateAsync`（那是"生成新 patch"的路）。
- 新流程：
  1. 底栏沿用现有的 `CustomModelConversionBottomBarViewModel`（已有 `ArmorSources` / `HelmetSources` /
     「不包含」/ `SelectedSources`）——只需把来源集合从"当前这一个 Mod"扩成"主 Mod + 递归子级"。
  2. 由选中的 armor/helmet 算白名单（units / materials / textures）。
  3. 建条目目录 → **按 `order` 把来源 patch 平铺拷到根**（保留原文件名）→ 写 `sectional-source.json`。
  4. `ImportPathAsync` 导入。
  5. **立刻**跑一次 `legacy2sectional` 转换 + 编译（与 vpk 的 `RunImportRebuildAsync` 对称）。

> **W2 开工前的侦查结论（下一轮直接用，不要再翻一遍）：**
>
> - 现有入口 `ModDetailsPageViewModel.CustomModel.cs:11 OpenCustomModelConversionAsync`：
>   拿 `ModEquipmentSourceFacts` → 底栏选 armor/helmet → `SectionalModBuilder.BuildAsync`
>   （生成新 patch + 搬进 payload + 拆件 + 剪枝）→ `ImportPathAsync`。**要替换的正是中间那一步。**
> - 白名单怎么来：`CustomModelConversionService.BuildPlan(...)` 已经产出
>   `CustomModelConversionPlan(Transfer)`，其中 `Transfer.SelectedTargets[].Parts[].UnitAssetKey`
>   就是"这件护甲/头盔包含哪些 unit"。材质/贴图用
>   `MaterialDependencyResolver`（`MaterialTypeId` / `TextureTypeId`）从这些 unit 的引用闭包取。
> - 来源侧的事实：`ModEquipmentSourceFacts.SourcePatchTocPaths`（**只有 legacy 才有意义**，
>   因为入口对 vpk2patch / sectional 是隐藏的 —— `ShowPatchSettingsCard => !IsVpkMod`）。
> - 子级来源：`OptionRelationshipGraph.Create(snapshot).GetDescendantOptions(hostId)`
>   （W6 已用它做过作用域展开，同一份口径）。
> - 拷贝时**必须**给每个来源写 `SectionalSourceEntry.PatchFiles`：落盘后就没有"谁属于谁"了，
>   没有它材质组只能并成一组。

**验收**：转换后根上仍是来源 patch（未被搬走）；`sectional/` 有 unit 组 + 各来源材质组；
`Overwrite/` 被编译出来；再次点重构能复现同一份产物（除时间戳）。

### W3 `legacy2sectional` 转换服务（核心新增）

> **实施注记（开始写之前查到的，能省一大截）**：下面这些空想出来的步骤，仓库里基本都有现成件——
>
> | 要做的事 | 现成的东西 |
> |---|---|
> | 看一个 patch 里有什么、谁内嵌谁外置 | `MaterialPackagingService.InspectAsync` → `PatchTocEntry` + `PatchAssetReference` + `UnitAssetKeys / MaterialAssetKeys / TextureAssetKeys / EmbeddedMaterialAssetKeys / ExternalMaterialAssetKeys / RequiredMaterialAssetKeys` |
> | 把一个 patch 拆成「模型」与「材质」两份 | `MaterialPackagingService.SplitAsync(source, modelOutDir, materialOutDir)`，自带校验 |
> | 按 asset key 从多个来源抽一份干净 patch | `PatchSubsetWriter.WriteSelectedAsync(template, outDir, selections)` |
> | 类型 id | `PatchUnitMeshReader.UnitTypeId` / `MaterialDependencyResolver.MaterialTypeId` / `.TextureTypeId` |
>
> 所以 W3 是**装配**，不是从零造 patch 读写。真正的难点只剩两处：
> ① 依据文件要能回答"这个根上的 patch 属于哪个来源"（见下）；
> ② 产物命名与 `patches.json` 的 `patchIndex` 分配要和编译段约定一致。

输入：Mod 目录（根上来源 patch）+ `sectional-source.json`。
输出：`sectional/` 全套 + 刷新 `patches.json`。

步骤：

1. 读依据文件，取白名单；**校验与实际来源对不上时报告漂移、不静默**。
2. 按来源顺序枚举根上 patch。
3. 抽 unit：按白名单从来源 patch 里取 unit → 写临时目录的 unit patch。
4. 抽材质组：**每个来源 Mod 一条**材质 patch（该来源里被白名单引用到的材质 + 贴图）；
   unit patch 自身内嵌的材质 → **另存一份**，单独一条，选项名 `"模型内置材质"`。

   > **已定（2026-11-09）**：这两类**不互斥**。同一条材质允许同时出现在「模型内置材质」组和
   > 某个来源组里 —— 它们在 `sectional/` 里是**两份不同的 patch**（内容可以一样）。
   > 不做"去重到一处"的优化：那会让"内置材质"这一组的含义退化成"来源里没抽到的剩余"。
5. `SectionalSplitter.ExecuteAsync(临时目录, gameData, 临时产物目录)` 拆 7 槽（unit 恒为 `patch_0`）。
6. `CustomModelSectionalCompaction` 剪枝。
7. 校验（材质数不少于上次等守门，沿用 `VpkRebuildService` 的口径）→ 整批换进 `sectional/`（可回滚）。
8. 写/刷新 `sectional/patches.json`（`Role` / `SourceName` / `OptionName` / counts）。
9. 交给 `ModCompileService.CompileAsync` 编译 `Overwrite/`。

**验收**：产物形状与 vpk2patch 完全一致（编译段不需要任何 `if (kind == ...)`）。

#### W3b 实施细节（已核对的签名，别再去翻）

| 环节 | API | 坑 |
|---|---|---|
| 扫描来源 patch 里的键 | `new PatchTocScanner().ScanEntriesAsync(path)` → `PatchTocEntry.AssetKey` | 只取 `<archive>.patch_N` 本体，跳过 `.gpu_resources` / `.stream`（用 `PatchFileNameParser` 判 sidecar） |
| 写一组 patch | `new PatchSubsetWriter().WriteSelectedAsync(template, outDir, selections)` | **输出文件名 = 模板的文件名**（`PatchArchiveWriter` 用 `Path.GetFileName(source)`，见 `PatchArchiveWriter.cs:57`）。所以要么选对模板让名字直接正确，要么写完再改名。unit 组恒为 `<archive>.patch_0` |
| 拆 7 槽 | `SectionalSplitter.ExecuteAsync(sourceDirOrPatch, gameData, outputDir)` | 输入可以就是含 unit + 材质 patch 的暂存目录；输出建议另给一个目录，避免边读边写 |
| 换载荷 | `VpkPayloadSwap.Swap(payloadDirectory, producedDirectory, backupRoot)` | 保留 `custom-model.json` 与 `sections.json`（`PreservedFileNames`）；失败会自己还原 |
| 清单 | `SectionalPatchManifestFile.Rebuild(modDir, utc, previous)` + `TryWrite(payload, document)` | 先读 previous，用户写的 optionName/order/notes 跨重建要留住 |
| 编译 | `ModCompileService.CompileAsync(modSelector, modDir, VpkHostContext, targetArchiveIds?, disabledSections?, onStage?, ct, headMergeIntoBody?)` | 目标/开关传 `null` 时读 `wardrobe.json` |

**唯一无法在本机验证的部分**：库里没有 legacy 源条目（`【材质版】隐藏物品合集` 是材质包、
`[huahua]Chiyu公式服` 是空壳），所以 W3b 的端到端只能在用户提供来源素材后真机跑。
在那之前，W3b 的验收以「单元测试 + 形状检查（产物目录里 patch_0 是 unit、材质组从 patch_1 起）」
为准，并在代码里把失败路径写清楚（不静默）。

### W4 批量执行器按形态分派

- `VpkBulkRebuildService.RunAsync` 每个目标先 `Resolve` 存储形态：
  `Vpk2Patch` → `VpkRebuildService.RunAsync`；`Sectional` → `legacy2sectional`。
- 目标枚举 `EnumerateTargets` 的判据从「有 `vpk2patch.json`」改成「sectional 系列」（见 W6）。
- **验收**：同一次批量里混合两档，逐条结果正确。

### W5 多选底栏「重构」按钮

按已定的分工（按钮不判断类型）：

| 层 | 改动 |
|---|---|
| `BottomBarCoordinator` | 新增 `ShowRebuild`（**只看作用域**，恒 true）+ `RefreshState()` 补 `OnPropertyChanged` |
| `SelectionActionBarPresentation` | 一个构造参数 + 一行转发属性 |
| `SelectionActionBarContent.xaml` | 一颗 `AppSecondaryButton`「重构」，位置在「设置分类」之后、「删除/移除」之前 |
| `MainWindow.xaml.cs:290-305` | 传 `shell.RebuildSelectedCommand` |
| `ShellViewModel` | 新命令：调检测器 → 0 个则提示"当前选择里没有可重构的 Mod（含其子级）"，≥1 个则 `StartBulkVpkRebuild(targets, "重构选中")` |

作用域：`IsLibrarySelection`（模组库）、`IsProfileSelection`（配置页）、
`IsDecorationHostSelection`（Mod 详情页的选项列表，`DecorationHost:{id}`）。

### W6 检测器（唯一的判定处）

- `EnumerateTargets(snapshot, root, scopeIds = null)`：
  - `scopeIds == null` → 全库（现有「一键重构」入口，行为不变）
  - 否则 → 对每个 id 用 `OptionRelationshipGraph.GetDescendantOptions` 展开后代 → 按 sectional 系列判据过滤 → 去重
- 判据统一为 **`ModStorageKind ∈ {Sectional, Vpk2Patch}`**（即"有 payload 目录 或 有依据文件"）。
- 点击时才调用（绑定属性里**不做任何磁盘 IO**）。

### W7 文案按 origin 分开

现在写死"一键重构所有 VPK 模组"的三处：

| 位置 | 现状 |
|---|---|
| `VpkBulkRebuildBottomBarContent.xaml:36` | 进度行标题 |
| `ShellViewModel:2632` | 后台任务标题 |
| `ShellViewModel:2634` | `origin: "模组库"` |
| `ShellViewModel:2506` | "等它结束后在「一键重构」里再跑一次" |

---

## 5. 不变量（不可违反）

1. `sectional/` 只由转换链路写；编译、换甲、开关、合并一律不碰（`sectional-compile-base` §1.2）。
2. `Overwrite/` 只由编译写。
3. 编译段**不得**出现按 `ModStorageKind` 的分支 —— 一旦出现，说明对称被破坏。
4. 转换必须"先写临时目录、校验通过才整批换"，失败时原产物不动（沿用 `VpkPayloadSwap` 语义）。
5. 底栏的可见性**不得**依赖任何磁盘检查；检测只在点击时发生。
6. `patches.json` 是派生的、允许缺失/过期；依据文件是权威输入。两者不得互相推导。

---

## 6. 已知坑与已做的取舍

1. **根上来源 patch 会不会被部署？** 稳态不会：`ModDeployableFiles.Resolve` 第一分支要求
   `Overwrite/` 是管理器物化的（`ManagedOverwriteDirectory.IsManaged`），且编译一定写这个标记
   ⇒ 编译过之后只看 `Overwrite/`。
   **已决定：首次转换失败的兜底不做**（用户判断这是过度防御；部署器在无法部署时会跳过）。
2. **`IsVpkMod` 会隐藏来源入口**：vpk2patch 与 sectional 都不显示「生成sectional版」⇒ 来源只能是 legacy。
   这个既有行为正好是需求，**不要动**。
3. **同名 patch 冲突**：多个来源若有同名文件（同 archive 同序号），平铺拷贝时后者覆盖前者。
   **已决定：来源按 `order` 顺序排列即可**，不为它另造改名规则。
4. **`SectionalSplitter` 会把多个源 patch 折叠成一条产物 patch**（`RemovedSourcePatchCount`）。
   所以「拿 `sectional/` 里的产物再拆一遍」**不是**重构，源必须来自根。
5. **材质组与 unit 内嵌材质的边界** —— 见 §9 未决。

---

## 7. 验收标准

**协议层**
- [ ] 依据文件写出后，`Resolve()` 立刻返回 `Sectional`（即使 payload 还空）。
- [ ] 旧库（无依据文件）判定结果与今天完全一致。

**转换**
- [ ] 转换后根上来源 patch 一个不少、文件名未变。
- [ ] `sectional/` 里 unit 恒为 `patch_0`，材质组从 `patch_1` 起；每个来源 Mod 一条；内置材质名叫「模型内置材质」。
- [ ] `patches.json` 的 `sourceName` 与 `sources[].name` 对得上。
- [ ] 连续转换两次，产物除时间戳外一致（可复现）。

**重构入口**
- [ ] 三个作用域（库 / 配置 / 详情页选项）都能看到「重构」按钮。
- [ ] 选中里没有 sectional 系列（含后代）时点击 → 只弹提示，什么都不发生。
- [ ] "一键重构所有"与"重构选中"的底栏文案、消息中心标题、日志 origin 互不串。
- [ ] 批量目标里混合两档时，逐条结果正确。

**回归**
- [ ] 整个解决方案 0 警告 0 错误；`dotnet test -c Debug` 全绿。
- [ ] vpk2patch 的既有链路（导入 → 重构建 → 编译 → 部署）行为不变。

---

## 8. 验证计划

**单测（HD2ModCore.Tests）**
- 依据文件读写与版本兼容（旧键 `sourceVpkName`）。
- `ModStorageKindResolver` 四种形态 + 依据文件分支。
- 目标展开：选中 + 递归后代、去重、环保护、非 sectional 的后代被过滤。
- 白名单推导：给定 armor/helmet 事实 → units/materials/textures。

**真机**
- 需要一个 legacy 源条目（根上只有 `*.patch_N`）。本机现有库里没有现成样本
  （`【材质版】隐藏物品合集` 是材质包、`[huahua]Chiyu公式服` 是空壳），
  验证时需按用户指定的来源素材构造。
- 走完整链：生成 → 检查根/`sectional/`/`Overwrite/` 三层 → 部署 → 游戏内确认。

---

## 9. 未决事项

无。讨论中提出的最后一条（材质组与 unit 内嵌材质的边界）已于 2026-11-09 定案，见 W3 第 4 步的引注：

> 两类材质组**不互斥**；同一条材质允许同时出现在「模型内置材质」组与某个来源组里，
> 在 `sectional/` 中是两份不同的 patch。

其余决策见 §3.1、§6。

---

## 10. 本轮明确不做

- 首次转换失败的兜底（用户已决定不做，后续再议）。
- 编译层的增量 / 缓存。
- `Overwrite/` 其它直接写者的收敛（`HostDerivedOverwriteService` / 折并 / 批量修复 / physbone 嵌入）。
- 「复制为变体」等既有功能的行为调整。

---

## 11. 执行顺序与进度断点

**顺序**（后面的依赖前面的，不要跳）：

```
W1 协议层  →  W6 检测器  →  W5 底栏按钮 + W7 文案  →  W3 legacy2sectional  →  W4 按形态分派  →  W2 生成流程改造
```

先把"判定"与"入口"做完（W1/W6/W5/W7），因为那部分可以独立验证；再做真正干活的 W3；
最后把 W3 接进批量执行器（W4）和生成流程（W2）。

**每步的收尾要求**（缺一步就不算完成）：

1. `dotnet build -c Debug` 0 警告 0 错误（构建前先 `Stop-Process -Name HD2ModManager -Force`）。
2. `dotnet test -c Debug` 全绿。
3. 更新本节的进度表。
4. 提交（提交信息写清"这一步做了什么、为什么"）。

**进度表**（每完成一项就在状态列打勾并写上提交号）：

| 项 | 内容 | 状态 |
|---|---|---|
| W1 | `sectional-source.json` 读写 + `ModStorageKind` 判据 + `SourceVpkName`→`SourceName` | ✅ 完成 |
| W6 | `EnumerateTargets` 支持作用域（选中 + 递归后代）+ sectional 系列判据 | ✅ 完成 |
| W5 | 多选底栏「重构」按钮（可见性只看作用域，命令调检测器） | ✅ 完成 |
| W7 | 三处硬编码文案按 origin 分开 | ✅ 完成 |
| W3a | 规划层：来源 → 哪个组装哪些 key（`SectionalConversionPlanner`，纯函数 + 13 条用例） | ✅ 完成 |
| W3b | IO 装配：扫描 → 抽键 → 落盘三组 → 拆 7 槽 → 换载荷 → `patches.json` → 编译（`Legacy2SectionalConverter`） | ✅ 完成 |
| W4 | 批量执行器按存储形态分派 | ✅ 完成 |
| W2 | 「生成sectional版」改为：建条目 + 平铺拷来源 + 写依据 + 导入 + 立刻转换 | 进行中 |

**中断后怎么接上**：读本节进度表 → 读 §4 对应工作项 → `git log --oneline -5` 看最后一步做到哪 →
`git status` 看有没有未提交的半成品（**不要**在编译不过的状态下离开）。

