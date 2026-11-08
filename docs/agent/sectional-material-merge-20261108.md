# sectional 化：材质与主体为什么一直是分离的（2026-11-08）

> 结论先说：**导入从来没有按"一个自洽条目"做过**，重构建只取第一个产出子目录，
> 迁移器只改名不摊平。三处叠在一起，就是"重构多少次都没用、只有删掉重加才行"。

## 1. 需求（`refactor-plan-20261106.md` 的 D1/D3/D5）

| 决定 | 内容 |
|---|---|
| D1 | 一个 sectional / vpk2patch mod = **一个自洽条目**；材质不再作为独立 Option mod |
| D2 | 根目录平铺**来源**（`*.vpk`），它们在库里不生效，只是权威来源 |
| D3 | `sectional/` 里**全部 patch 组平铺按序号**：unit = `patch_0`，材质 `patch_1..patch_N`，**不建子目录** |
| D5 | `Overwrite/` 是唯一生效层：unit patch + 启用的材质 patch |

## 2. 实际代码（三处缺陷，逐条对应）

### 2.1 导入：每个 VPK 一个任务、一个 Mod

`HD2ModManager/Services/VpkImportService.BuildConversionWorkload` 原来是：

```csharp
jobs.Add(new ConversionJob(group.ModelVpkPath, …, IsModel: true));
foreach (var material in group.Materials)
    jobs.Add(new ConversionJob(material, …, IsModel: false));   // ← 每个材质包各建一个 Mod
```

而每个 job 又各建一个 staging 目录（目录名 = VPK 名）。于是"模型的材质在别的包里"这种
最常见的模组，导进来必然是**模型一个条目 + N 个材质包条目**（后者再被挂成 Option）。
只有"模型 VPK 自带材质"的包（例如 `恋星milfy_bill`）才是自洽的——那正是用户实测里
唯一正常的那一个。

### 2.2 转换：一次批转换的产出是「每个输入 VPK 一个子目录」

`HD2ModCli/CliApplication`（`vpk2patch` 的契约）：

> 一条命令、整批进队、逐个出 patch、自动识别类型。整批构成解析集合，
> 但产出仍然是"每个 VPK 一个 patch"。

实现上每个输入 VPK 得到 `--output` 下的一个子目录。而**重构建**原来这样取产物：

```csharp
var produced = FindProducedPatchDirectory(convertRoot);   // ← 只取第一个含 patch 的子目录
```

模型通常排在最前，于是"模型 patch 进来了、材质全丢"。重转多少次都一样。
若换一条路把整个输出根搬进 `sectional/`，就得到一堆子目录——而下游所有枚举器都只扫顶层
（`ModNodePayloadLayout.EnumerateFiles`、`PatchFileIndexBuilder`、`MaterialOptionSettings`、
`SectionalPatchManifestFile.ReadArchiveHex16`），这些 patch 等于不存在。

### 2.3 迁移：只改名，不摊平

`ModContentLayoutMigration` 明确写着"目录里的文件一个字节都不用动"，只把
`vpk2patch/` 改名为 `sectional/`。老库里那些"按包名建的子目录"被原样搬进去，
与 D3 的"不建子目录"直接冲突。而 `ModLibraryImporter.CopyVpkPatchPayload` 只复制**顶层**
文件——嵌套来源走一次导入就是"导入成功、一个 patch 都没有"。

## 3. 修法

### 3.1 `VpkPayloadCollector`（新增，Core）

把一次批转换的**全部**产出子目录收成一份平铺载荷：

- 序号只看转换报告：模型那份 `unitPatch` → `patch_0`，其余材质 patch → `patch_1..N`
  （报告缺失时退化为"模型目录的 patch_0 当 unit"，模型目录按 `custom-model.json` 认）；
- 每个 base patch 的 `.gpu_resources` / `.stream` **跟着它一起改名**（TOC 里写的是 sidecar 的
  偏移与长度，拆开就是"读出来是错的、但不报错"）；
- 结构文件（`sections.json` / `components.json` / `custom-model.json` / `physbone-rig.bin`）
  只从**模型目录**取一份。

导入与重构建都走它：`VpkImportService.ConvertOneAsync`、`VpkRebuildService`。

### 3.2 导入：一个 group 一个任务

`BuildConversionWorkload` 改成"模型 + 它自己的全部材质包 = 一个 `ConversionJob`"，
一次 `vpk2patch` 转换、一个产出目录、一个 Mod；根上平铺**整组**来源 VPK。

逐 VPK 的类型用新的 CLI 开关 `--treat-as-list`（每行 `<路径>\t model|material`）传：
`--treat-as` 会作用于整次调用的每一个输入，混着传必然把材质包当角色转
（死在 `MDL 骨骼数量异常：0`）。类型结论来自规划阶段（`--dry-run` 的 `modelGroups`）。

### 3.3 材质选项的名字

`SectionalPatchManifestFile.Build` 原来把所有 `materialPatch` 都叫「模型内置材质」。
合并成一个条目之后，材质选项表里 N+1 行全同名，用户分不出谁是谁。现在：
模型自己那次转换产出的材质叫「模型内置材质」，**别人的材质包产出的叫那个 VPK 的名字**。

### 3.4 生效层：导入/重构建之后必须物化 `Overwrite/`

导入与重构建结束时都调 `OverwriteMaterializer.Materialize(modDirectory, OverwriteSelection.Default)`：
unit（基底那份，或已有的换甲产物）+ 启用的材质。不做这一步，部署会按"基底全上"把
`sectional/` 里所有材质变体一起上，同名资源互相覆盖——用户看到的颜色取决于部署顺序。

只在"这是 VPK 转换产物"（根上有 `vpk2patch.json`）时物化：手写的 sectional Mod 没有报告，
贸然物化会把它自己的 patch 藏进变体里。

### 3.5 旧库归拢：只做两步，不写第二个迁移器

`hd2mod library family-merge plan|apply`（Core：`ModFamilyMerger`）：

1. 把同 `idNamespace` 的材质包 Mod 根上的**来源 VPK 搬进模型 Mod 的根**；
2. 对模型跑**一次重构建**（它自然产出平铺的 `patch_0..N`）。

搬 VPK 是不可出错的动作（文件挪位置，语义不变），产物形状交给重构建保证。
搬完的空壳交给既有的 `library sync apply` + `library cleanup`，不另写删除逻辑。

判据只用现存事实：报告里的 `idNamespace`（同一批转出来的凭据）+ `custom-model.json` 在不在。
一个命名空间里出现两个模型、或同名 VPK 大小不同（两份都可能是对的）→ 跳过并说明，不猜。

### 3.6 `doctor` 抓"载荷层里出现子目录"

`CliDoctor` 新增 `ModPayloadNested`（error）：`sectional/` 或 `Overwrite/` 顶层出现子目录
就报错，并给出修复动作。这类形状以前完全静默——文件明明在那里，部署却什么都没有。

## 4. 验收（本次实测）

| 项 | 结果 |
|---|---|
| 真实三 VPK 批转换（`Elise_Bill` + 两个材质包） | 输出根下**三个子目录**，各自带自己的 `patch_0` |
| 同一份产出走 `VpkPayloadCollector` | `patch_0`（模型 230 KB）/ `patch_1`（29 KB）/ `patch_2`（524 B），**无子目录** |
| 真库副本（Elise 家族）走 `ModFamilyMerger.Plan` | 识别出 `vpk-748bd2ca1f793950` 家族：模型 + 2 个材质包 |
| 单元测试 | collector 4 条、family-merge 4 条、选项名 2 条；全量 2004 通过 / 1 跳过 |

## 5. 旧库怎么收敛（用户操作）

```powershell
hd2mod library family-merge plan  --manager-root <管理器目录> --library <Mod库目录> --json
hd2mod library family-merge apply --manager-root <管理器目录> --library <Mod库目录> --game-data <游戏data目录> --confirm --json
hd2mod library sync apply   --manager-root <管理器目录> --library <Mod库目录> --plan <plan.json> --confirm --json
hd2mod library cleanup      --manager-root <管理器目录> --library <Mod库目录> --confirm --json
```

## 6. 材质选项没有名字（2026-11-08 第二轮）

现象：导入与重构建都正常了，但详情页「材质选项」三行显示成 **材质 1 / 材质 2 / 材质 3**，
顺序还是 2/3/4。

根因有两层，都在"报告"上：

1. **报告的 patch 文件名是「每个来源 VPK 自己的编号」**（都叫 `<archive>.patch_0`）。
   收成平铺载荷后编号变成 `patch_0/1/2`，但报告没跟着改 ——
   `patches.json` 的构建靠报告把 patch 文件翻译成"谁是什么"，翻译不到就只剩
   `patchIndex == 0 ? Unit : Material` 的兜底，**选项名只能是空**。
2. **写 `patches.json` 的时机在报告之前**：`VpkPayloadSwap.Swap` 里顺手写了一份，
   用的是**上一份**报告。归拢/合并后新出现的材质 patch 在旧报告里根本没有，名字自然全丢。

修法：

- `VpkPayloadCollector` 在收集的同时**改写报告**（`Result.ReportJson`）：把每个来源 VPK 的
  `unitPatch` / `materialPatch` 换成平铺后的编号；顺带用原始 VPK 名把被 CLI 洗成小写的
  `name` 还原（否则选项名会显示成 `elise基础材质a1主体`）。
- 导入与重构建都改写成**这份改写过的报告**。
- `VpkPayloadSwap` **不再写** `patches.json`；新增 `SectionalPatchManifestFile.Rebuild`，
  由调用方在"新报告已经落地"之后调用（重构建 ⑤c、导入换载荷之后）。
- 顺序显示修正：`MaterialOptionSettings.Read` 的默认 `Order` 改成"材质之间的第几个"（从 0 起），
  与 `Move` 写的 0 基位置同一口径 —— 以前直接用 `PatchIndex`，第一行会显示成「顺序 2」。

实测（真实三 VPK 批转换的报告，走「收集 → 改写报告 → 重写 patches.json」）：

```
patch_0  role=Unit      name=<null>                     ← unit 不是材质选项，没有名字是对的
patch_1  role=Material  name=Elise基础材质A1主体
patch_2  role=Material  name=Elise基础材质B1光泽感肌肤
```

**已经坏了的老数据**：点一次「重构」就会重写报告与 `patches.json`，名字与顺序随之恢复。

## 7. 重建链路必须"干净"（2026-11-08 第三轮）

需求一句话：**重构建只是"把根上的来源重新变成标准产物，再按用户配置算出生效层"**。
它不该、也不能改动任何"用户配置"。实测里它改了，而且坏了三样东西。

### 7.1 契约（以后任何改动都按这张表判断）

| 类别 | 内容 | 重建时 |
|---|---|---|
| **输入（只读）** | 根上来源（`*.vpk` / `*.patch_N`） | 读 |
| | `wardrobe.json`：目标护甲、组件启用（`disabled`）、头身合并 | **读，不写** |
| | `.hd2modmanager-mod.json`：名字/备注/持久 id、**材质启用集合** | **读，不写** |
| | 旧 `patches.json`：`optionName` / `order` / `notes` / `imageFileName` | 读出来，重写时原样带回去 |
| | `components.json`（组件树）、`custom-model.json`（素体 id） | **原样保留**，不被新产物覆盖 |
| **输出（只写派生）** | `sectional/`：`patch_0..N` 平铺 + 平铺编号的 `patches.json` | 写 |
| | `vpk2patch.json`（改写过的报告） | 写 |
| | `Overwrite/`：`目标护甲 + 组件启用 + 材质启用` 算出的唯一生效层 | 写（**且只在真的重做了换甲、或从来还没有生效层时**） |
| | `library.json` 的 `patchGroups` / 指纹 | 由库服务写 |

**四条禁止**（全部来自实测事故）：

1. 重构建**不写** `wardrobe.json` —— 目标护甲/组件启用是用户意图，不是派生。
2. 重构建**不写** `.hd2modmanager-mod.json` —— 材质启用同理。
3. **不许用"自己的素体"当回退目标**：没有记录目标时就不重做换甲，而不是把回退值写死。
4. 没有重做换甲时，**不许覆盖现有 `Overwrite/`**：那是用户当前的外观。

### 7.2 这一轮修掉的五处污染

| # | 污染 | 后果 | 修法 |
|---|---|---|---|
| 1 | 重构建里先物化一遍"基底 unit + 材质"，然后才换甲 | 换甲失败/被跳过时，**基底 unit 留在生效层** → Mod 变成自己的素体 | 物化挪到最后、且只在"没重做换甲 + 已有生效层"时跳过 |
| 2 | 目标解析带"自己的素体"回退，且换甲成功后把结果写进 `wardrobe.json` | 回退值被**永久写死**，用户选的护甲再也回不来 | 只认显式记录；`VpkRekeyService(recordTarget:false)`，重建不写配置 |
| 3 | GUI 重构建**先**把内存里的 `_vpkFile` 落盘 | 页面状态旧/空时，目标护甲被覆盖 | 只在**成功之后**写"这次真正烘进去的配置" |
| 4 | 换载荷前没保住旧 `patches.json`；按名字认领会命中同源的第一条 | 选项名/顺序/备注丢失；同源多 patch 时错位 | 换载荷**前**读旧清单传进去；认领按 `(来源 VPK, 序号)` → `(来源 VPK, 角色)` → 序号 |
| 5 | 换载荷时用新产物覆盖 `components.json` / `custom-model.json` | 用户改过的组件树被冲掉、素体 id 被换 | 保留文件存在时**不覆盖** |

### 7.3 实测（沙箱副本，一个真实 Mod）

模拟用户配置：目标护甲 = O-3“自由之魂”、关闭 1 个 section、材质选项改名「我的发色」+ 顺序 5 + 备注、
`enabledMaterialPatches = [patch_1]`。然后跑一次重构建：

| 文件 | 结果 |
|---|---|
| `wardrobe.json`（目标 + 组件启用） | **字节未变** |
| `.hd2modmanager-mod.json`（材质启用） | **字节未变** |
| `sectional/components.json`（组件树） | **字节未变** |
| `sectional/custom-model.json`（素体 id） | **字节未变** |
| `sectional/patches.json` | 重写，但 `我的发色` / `order 5` / `我改过的备注` 全部保留 |
| `Overwrite/patch_0` | **字节未变**，仍是换甲产物（543 KB，不是基底的 291 KB） |

再测"没有记录目标"：`rekeyed=False`，`Overwrite` **字节未变**，提示语明确写"现有生效层保持原样"。

## 8. 还没做的

- GUI 里还没有 family-merge 的入口（只有 CLI）。归拢一次就够，但用户得会敲命令。
- 旧的 `library material-fold` 已被 family-merge 取代，暂未删除。
- 导入路径的改动需要一次真实 GUI 导入验证（模型 + 独立材质包的组合）。
