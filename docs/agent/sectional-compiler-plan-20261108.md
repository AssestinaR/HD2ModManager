# sectional 编译器实现计划（2026-11-08）

> **状态：待批准后开工。** 本文是这一轮重构的唯一口径。
> 前一份交接文档 `sectional-compile-handover-20261108.md` 的"故障机制"定位基本正确，但**架构结论与若干事实有误**，差异集中在附录 A；两者冲突时以本文为准。
> 所有决策号（D1…D14）都可以在 §2 找到出处与理由。

---

## 1. 目标

把「`sectional/` → `Overwrite/`」重写成一个**纯函数式编译器**：

```
Compile(sectional, 参数) → Overwrite
```

- 参数只有四项：**目标** / **section 开关** / **头身合并** / **材质开关**。
- 任何一项变化 ⇒ **从 `sectional/` 完整重算一次**。不增量、不缓存、不做"看起来没变就跳过"。
- **不依赖**旧的换甲链路：`vpk rekey` 子进程、`CanonicalCrossArmorOrchestrator`、`CliVpkRekey` 的规划器，一律不进编译路径。
- 旧链路里"一个来源 unit 只能落到一个目标 unit""目标单元必须先读游戏数据才知道形状"这些前提，**在这一版里全部不成立**，因为它们对"换 key"没有意义。

一句话：**先把地基打平（正确、可复现、无隐藏状态），优化留到之后单独做。**

---

## 2. 硬约束（用户逐条定下来的）

| 编号 | 约束 | 依据 |
|---|---|---|
| **D1** | `sectional/` 只读。编译器**一个字节都不写**它 | 用户：sectional 是权威事实，只由转换链路产出 |
| **D2** | 编译器**只写 `Overwrite/`** | 同上 |
| **D3** | 任何参数变化都重新编译；**不做增量** | 用户："任何一处变动都重新按新编译参数编译一次，确保绝对健壮" |
| **D4** | 四个功能是**同一个编译器的四个条件**，不是四条链路 | 用户："打包成编译器" |
| **D5** | 替换护甲 = **只改 unit id**（+ 落到正确的目标 unit 键），不走老的庞大换甲链路 | 用户："只需要能做到：将一个来源 unit id 改到另一个 id 还能正常用，就算成功" |
| **D6** | 目标护甲里**没被我们的来源 unit 占用的 unit 必须隐藏**，否则原版残件会显示 | 用户："这个是我漏掉了，你的注意非常正确" |
| **D7** | **披风槽（slot 1）永远保留原版**：我们的 patch 里**不出现**披风 unit 的键，既不替换也不隐藏 | 用户："披风槽应当是例外的，总是保留原版" |
| **D8** | 肩部（slot 8/9）**隐藏**（我们没有来源） | 用户："肩部两个部位跟着隐藏" |
| **D9** | 隐藏壳用**来源 unit 里的一个**做底，把模型数据换成极小三角片；**不读游戏数据** | 用户："直接用来源 unit 中其中一个…重点是来源总是可信的" |
| **D10** | 头身合并 = 一个判断 + 两种做法（见 §4.5） | 用户原始描述，逐字 |
| **D11** | 目标没有配套头盔**不报错**，只给 info 级提示 | 用户："完全不报错即可，只弹出一个 info 级的信息" |
| **D12** | 材质开关 = "要不要把 patch 拷过来"，没有其它语义 | 用户："最简简单，只是要不要复制 patch 过来而已" |
| **D13** | section 开关的"更快路径"**代码保留但不依赖**：本轮所有变更都走完整编译 | 用户："哪几种更快的优化模式可以不删，但暂时放着" |
| **D14** | 不追求"最小改动"，追求"最不可能出错"；旧的臃肿实现可以直接不用 | 用户："要做的简单粗暴，要够健壮，不能太依赖旧的东西" |

---

## 3. 数据事实（实测，作为规则依据）

以下数字都是用 `hd2mod` 只读命令 + 真索引库实测出来的，不是推断。

### 3.1 三层目录与 patch 形状（沙箱 `E:\tmp\rb2-mods\Lunalice_Bill`）

| 事实 | 值 |
|---|---|
| `sectional/9ba626afa44a3aa3.patch_0`（unit） | **7 条**条目，全部 `typeId=0xe0a48d0be9a7453f`；无附加依赖条目；TOC 291 514 B / GPU 31 552 374 B / stream 0 |
| `sectional/…patch_1`（material） | **186 条**：材质 94 条（`0xcd4238c6a0c69e32`，340 B）+ 贴图 92 条（`0xeac0b497876adedf`，1408 B）；**不含任何 unit** |
| 7 个 unit | `7d58c219e9260001..07`；slot 0（头）+ slot 2..7；全部 `body=Any`；躯干那件的自称是 `slot=Torso / pieceType=Undergarment / weight=Light`（**注意它不是目标槽位的自称**，见 §10 Q3） |
| 单个 unit 结构（torso） | version `10800438`、84 骨、5 个 mesh（lod 3/2/1/0 + 一个 `lod=-1`）、113 条材质绑定、2 个 stream |
| `Overwrite/` 现状 | 只有 `patch_0`/`patch_1` + 归属标记；**没有** `sections.json` / `components.json` / `custom-model.json` |
| `sectional` 与 `Overwrite` 的 `patch_0` | **SHA256 完全相同**（说明上次编译是"恒等"，且没有烘 section） |
| `wardrobe.json` | `headMergeIntoBody=true`，但生效层里没有那份改过的注册表 ⇒ **开关与产物已经不一致**（这正是 D3 的存在理由） |

### 3.2 目标套件的形状（真索引库，`gamedata equipment list/show`）

| 项 | 数字 |
|---|---|
| 护甲 kit | 138 套；**15~29 个 unit，平均 23.5** |
| 槽位集合 | `1..7` 必有；119 套有 `8,9`；13 套没有任何肩；4 套只有右肩；2 套只有左肩 |
| **披风** | **138/138 套都有** slot 1 / pc0 / Any，1 个 unit |
| 躯干层数 | **pc0/1（83 套）或 pc0/1/2（55 套）——没有一套少于 2 层** |
| 髋 | pc0/1（96）· 0/1/2（26）· 只有 1（12）· 1/2（4） |
| 腿 | pc0/1 为主（121/122）· 偶见 0/1/2 · 或只有 1 |
| 臂 | pc0/1（137/125）· 偶见 0/1/2 · 或只有 1 |
| 肩 | 几乎只有 pc0；有 1 套是 pc0/2 |
| 头盔 kit | 161 套；**1 个 unit**（157 套）/ 2 个（4 套：额外的 pc2 或 slot1）；形态恒为 `slot0/pc0/Any` |
| 护甲 setId 无配套头盔 | **7 / 116** |
| 每个 (slot, pieceType) | 通常 1~2 个 unit（Stocky/Slim 各一；Any 只算一个） |

### 3.3 索引能直接给出的东西

`EquipmentUnitCatalogService.GetEntriesAsync()` → `EquipmentUnitCatalogEntry { ArchiveId, Category, DisplayName, Parts[] }`，
`EquipmentUnitPart { UnitAssetKey, PartKind, Layer, BodyVariant, PieceType, Cell, … }`。

其中 **`Cell`（`ArmorCell` = Slot × Layer）是注册表给的权威格子**，`ArmorCell` 自己就带 `IsCape`、`IsFirstPersonHidden`，
注释里明确写着"替换护甲链路双向排除披风"。**规划一律用 `Cell`，不要用 `PartKind` 反推**（躯干内层与护甲层的网格名都是 `Torso_Armor_*`，分不出来）。

---

## 4. 编译器的形状

### 4.1 输入契约

```csharp
public sealed record SectionalCompileInput(
    string ModDirectory,
    string? TargetArchiveId,                                          // null/空 ⇒ 自身素体
    IReadOnlyDictionary<string, IReadOnlyCollection<int>> DisabledSections, // **来源** unit 键空间
    bool HeadMergeIntoBody,
    IReadOnlyList<string>? EnabledMaterialPatches);                   // null ⇒ 读设置文件 ⇒ 再退化为全部
```

- `TargetArchiveId` 的两种形态：
  - `custom-model:<key>`：目标是素体。**只有 key 等于本 Mod 自己 `sectional/custom-model.json` 的 key 时走 identity**；
    否则本轮**显式拒绝**（见 §10 待确认项 Q1，默认行为：报错，不静默）。
  - 游戏归档 id：按 `setId` 补齐配套的另一半，**只补 `Category ∈ {Armor, Helmet}`**，**排除 `Cape`**（披风是第三个归档，D7）。
- `DisabledSections` 永远按**来源键空间**传（与 `wardrobe.json` 一致），编译器负责翻译；空集 = 没有关闭项。

**前置条件（不满足就返回 `Ok=false` + 明确原因，不写盘）：**

1. `sectional/` 存在，且 `patches.json` 里能定位到 unit patch（`role=unit`）；
2. `sectional/sections.json` 存在（组件的唯一来源）；
3. identity 模式额外要求 `sectional/custom-model.json` 存在且能读出 key（否则无从判断"这就是我自己"）。

### 4.2 输出契约

`Overwrite/` 里最终只应该有这些：

| 文件 | 说明 |
|---|---|
| `<archive>.patch_0`（+ `.gpu_resources` / `.stream`） | 编译出的 unit patch |
| 选中的材质 patch（+ sidecar） | 逐个原样拷贝，不做任何处理 |
| `sections.json` | 从**输出 patch** 重建；记录的是**烘焙前**的 `numIndices`（顺序约束见 §4.7） |
| `components.json` | `sectional/components.json` 里的 unit 键按 `Replacements` 翻译后写入（仅作兜底：读侧优先权威侧） |
| `custom-model.json` | **仅**"自身素体 + 合并开"时存在 |
| `.hd2-vpk-rekey-overwrite` | 归属标记（复用现有标记，不新增，见 §6） |

结果对象（不是异常）：

```csharp
public enum SectionalCompileKind { OwnModel, Armor }
public enum HeadMergeOutcome { Off, AppliedOwnModel, AppliedIntoArmorTorso, SkippedNoFreeTorsoLayer, NoHelmetKit }

public sealed record SectionalCompileResult(
    bool Ok, string? Error,
    SectionalCompileKind Kind,
    int ReplacedUnits, int HiddenUnits,
    HeadMergeOutcome HeadMerge,
    IReadOnlyList<string> Info, IReadOnlyList<string> Warnings);
```

### 4.3 流水线（顺序固定，一共 8 步）

```
① 读条件：wardrobe.json（目标/section/合并）、Mod 设置文件（材质）
② 读 sectional：patches.json（哪个是 unit / 哪些是 material）、sections.json、custom-model.json
③ 读目标：索引库 → 目标 kit 的 unit 列表（护甲 + 头盔，排除披风）
④ 规划（纯函数，不写盘）→ SectionalCompilePlan
⑤ 在临时目录生成 unit patch（改名副本 + 隐藏壳），文件名与来源一致
⑥ sections.json：从**临时 patch** 重建（此刻记录的是烘焙前原值）
⑦ 烘焙 section 开关（改的是临时 patch）
⑧ 物化 Overwrite（一次事务）+ 写结构文件
```

**⑧ 只能物化一次。** 现有 `ModCompileService` 在"自身素体"路径上物化了两次，第二次把第一次刚写的结构文件删掉
（详见附录 B-1）。新实现从结构上只调用一次 `OverwriteMaterializer.Materialize`，且结构文件一律在它**之后**写。

**identity（自身素体）模式不重写 patch**：把来源 patch 原样装进临时目录再烘焙，逐字节保持不变（只有 Armor 模式才走 §4.10 的改名写入）。
这样"自身素体 + 没关任何 section"的产物与 `sectional/patch_0` 逐字节相同，是一条能自动断言的强不变式（§8-1）。

### 4.4 规划算法（`SectionalCompilePlan`）

输入：来源 unit 列表（来自 `sectional/sections.json`：`fileId / slot / pieceType / bodyType / 各 section`；
    来源的 `ArmorCell` 用 `ArmorCell.FromRegistryCoordinates(slot, pieceType)` 构造）
+ 目标 kit 的 unit 列表（来自索引，直接带 `Cell`）。
输出：`Replacements`（`来源 unit → 目标 key` 列表，1→N）、`Hidden`（目标 key 列表）、`HeadMerge` 结果、`Info/Warnings`。

```
// 一、每个来源 unit 找承载层
if 同一个 slot 出现多个来源 unit:
    只保留 pieceType 最小的那个（本体优先），其余 Warnings += "同一部位的第二个来源 unit 不会被写出"
if 来源 unit 的 Cell 未知（IsKnown == false）:
    Warnings += "来源 unit 0x… 没有权威格子，跳过"
    continue

for each 来源 unit S:
    if S.Cell.IsCape (slot == 1):
        跳过   // 来源本来就不该有披风；有也不写
        continue
    if S.Cell 是 slot 0（头）:
        交给 §4.5 处理（普通模式落到头盔 slot0；合并模式落到躯干空层）
        continue

    targets = 目标 kit 里 Cell.Slot == S.Cell.Slot 的 unit
    if targets 为空:
        Warnings += "目标没有 {slot} 这个部位：来源 unit 0x… 不会写出去，那一块会消失"
        continue

    carrier = targets 里最大的 Cell.Layer
    carriers = targets 里 Cell.Layer == carrier
    for each T in carriers:
        Replacements += (S → T.Key)        // 同一个来源可复制成多份（Stocky + Slim）
    其余 targets（Layer < carrier）→ 稍后进 Hidden

// 二、隐藏集 = 目标全集 − 我们替换掉的 − slot 1（披风）
Hidden = 目标 kit 的所有 unit
       − Replacements 用到的 key
       − Cell.IsCape 的 key          // D7：披风永远不碰
       // Cell 未知（IsKnown == false）的一律进 Hidden：保守优先，宁可多隐藏一个
```

**头身合并**在规则一之前分叉（见 §4.5）。

不变式（写测试断言）：

1. `Hidden ∩ Replacements = ∅`
2. `Hidden` 里没有任何 `Cell.IsCape`
3. `Replacements` 的每个目标 key 都属于目标 kit
4. 每份 `Replacements` 的**来源**都是 sectional 里的一个 unit
5. 同一个目标 key 不会出现两次（一个 key 只能被写一次）
6. `Replacements ∪ Hidden ∪ 披风 = 目标 kit 全集`（没有第三个桶）

### 4.5 头身合并（D10）

**判断**：`HeadMergeIntoBody == true` 且有"头"这件来源 unit（slot 0）。

#### ① 目标是自定义素体（Identity）

- unit patch **一个字节都不改**（仍然 7 份、键不变）。
- 只改注册表：`sectional/custom-model.json` → **复制一份改过头的**写到 `Overwrite/custom-model.json`。
- 头件：`slot 0 / pieceType 0` → `slot 2 / pieceType = 躯干槽里第一个空层（从 0 往上找，跳过已被占用的）`。
- 躯干槽已满 → 不合并，`HeadMerge = SkippedNoFreeTorsoLayer` + warning。
- `sectional/` 那份**不动**（D1）。关闭合并时必须删掉 `Overwrite/custom-model.json`（否则生效层永远读上一次的合并形态）。

> 现有 `WriteMergedCustomModel` 已经实现这段（`ModCompileService.cs:222-267`），复用即可；只补两条：
> (a) 返回值要能区分"做了/没做"，让界面不再撒谎（§4.9）；
> (b) 界面文案不许写死"第 1 层"。

#### ② 目标是原版护甲（Armor）

- **头不进头盔套件**，改为落到**目标躯干槽**里一个"没被来源躯干占用"的层。
- 具体取层（数据驱动）：
  ```
  torsoLayers = 目标 torso 槽所有 Cell.Layer 去重，降序
  bodyLayer   = torsoLayers[0]            // 身体占最外层（老链路的实测结论：内层会被外层的剔除代理挡掉）
  headLayer   = torsoLayers[1]            // 头占次外层；没有第二层 ⇒ 不合并
  ```
- 身体（来源 torso）→ `bodyLayer` 的全部 unit（每个身形一份）。
- 头（来源 slot 0）→ `headLayer` 的全部 unit（每个身形一份）。
- `headLayer` 以下的层 → 隐藏。
- **头盔套件（除 slot 1）全部隐藏**（否则玩家戴的 vanilla 头盔会和并进来的头重叠）。

> 全库 138 套护甲的躯干层数都 ≥2，所以 `headLayer` 缺失在当前库里是 0 例；校验仍保留（数据会变）。

#### ③ 目标没有配套头盔（7/116）

- **不把它当错误**（D11）：`Info += "这件护甲在游戏里没有配套头盔 item：头将无处安放（其余部件照常编译）"`，`HeadMerge = NoHelmetKit`。
- 编译继续，头这件来源 unit 不写出（其余照常）。

### 4.6 隐藏壳（D9）

**一个隐藏单元，改 key 复制 N 份。** 底座从**我们自己的来源 unit**里取：

```
base = 来源 unit 里 sections 最少的那个（平手取 fileId 升序）   // 确定性，保证可复现
payload = PatchEntryPayloadReader.ReadPayloadAsync(base entry)   // 本地，不读游戏 data
model   = new UnitMeshReader().Read(payload.TocData, payload.GpuResourceData)
self    = new GameDataUnitMesh(base.Key, "sectional", payload, model)
hidden  = new CanonicalHiddenUnitBuilder().Build(
              self,
              UnitTransformInfo.Empty,
              minifyCullingMeshes: **true**);
```

产出 `CanonicalHiddenUnitOutput.Entry`（完整 TocData / GpuData），随后每个隐藏 key 克隆一份（只换 key）。

**`minifyCullingMeshes: true` 是强制的，不是可选项。**
`SameKeyCanonicalUnitRebuilder.cs:112` 在 `false` 时会把 `lod == -1` 的 mesh **原样保留**——而我们的每个 unit 都带一个 `lod=-1` 网格。
不传 `true`，隐藏壳会画出**一整个我们的模型**。这个开关全项目还没有任何调用点传过 `true`，所以：
**这是一条新代码路径，必须进验收（§8-8）**，测试要断言隐藏份的三角形总数 = 1。

尺寸预期：TOC ≈ 20 KB/份、GPU ≈ 1.3 KB（同内容去重只留一份）。RE-2310 这种 24 unit 的套件：**合并关 12 个隐藏份 / 合并开 11 个**，≈ 240 KB，对 31 MB 的 patch 可忽略。

抽一个 10 行接口，默认实现用上面这条；备用实现才走 `WardrobeHiddenUnitTemplate`（4.2 KB 内置模板，见附录 A-4）：

```csharp
internal sealed record HiddenUnitPayload(
    byte[] TocData, byte[] GpuResourceData,
    ulong Unknown1, ulong Unknown2, uint Unknown3, uint Unknown4);  // 后四个字段取底座条目的值，不写 0

internal interface IHiddenUnitSource {
    HiddenUnitPayload Create();   // 已含 minify，调用方只负责改 key
}
```

### 4.7 section 开关

- 状态在 `wardrobe.json`，键是**来源** unit；编译器负责翻到目标键（1→N）。
- **顺序必须是：先 `WardrobeSectionMapFile.BuildAsync` 再 `WardrobeSectionBaker.BakeAsync`。**
  反过来会把"隐藏后的 0"记成"原值"，以后永远打不开（附录 B-2 是现有实现踩的坑）。
- 烘焙器本身的语义不动（幂等、可逆、写前校验"当前值 ∈ {0, 原值}"）。
- 写进 `Overwrite/sections.json` 的是**烘焙前**那份 map（含 `SourceUnit` 对照，供将来就地烘焙翻译）。
- **本轮不依赖"就地烘焙"**（D13）：`ApplyVpkComponentsAsync` 里那条快捷路径要**禁用**，所有变更都走完整编译。

### 4.8 材质开关

- 选哪个拷哪个：`EnabledMaterialPatches`（null ⇒ 设置文件 ⇒ 全部材质 patch），**原样拷贝 patch + sidecar**，不解析、不重写。
- 一个都不选也是合法状态：`Overwrite/` 里只剩 unit patch（部署侧"`Overwrite` 有归属标记 + 可读 TOC ⇒ 只部署它"因此仍然正确）。
- 与 unit 无关（实测材质 patch 里没有 unit 条目），所以材质开关**不影响**任何 unit 决策。

### 4.9 界面口径（不许再撒谎）

- "有没有真的合并"由 `SectionalCompileResult.HeadMerge` 决定，**不由开关状态决定**。
- 禁止硬编码"第 1 层"（实际层号是算出来的）。
- 替换/隐藏的条数如实显示；"某个部位没有落点"这类必须让用户看见的 warning 留在卡片上。
- **GUI 现有的两处判据要改掉**：`outcome.Rekey?.HeadMerge`（字符串五态）与 `outcome.Rekey?.RestoredFromSource`
  都是老链路的产物；新结果里没有 `VpkRekeyOutcome`。`ModCompileOutcome` 保留外壳（`Ok/Error/Rekeyed/TargetName/Notes`）
  作为兼容投影，但合并/OwnModel 的判断要改成读 `SectionalCompileResult`。

### 4.10 可复现性与写盘细节

- **输出条目顺序固定**：先按 `Replacements`（来源 unit 在 sectional 里的顺序 × 目标层降序 × `fileId` 升序），再按 `Hidden`（`fileId` 升序）。
  顺序固定是"同样输入编译两次逐字节一致"（§8-9）的前提。
- **写 patch 的形态**（仅 Armor 模式）：`PatchArchiveWriter.WriteAsync(source: sectional patch, out: 临时目录, edits: 空, additionalEntries: 改名副本 + 隐藏份, removedEntries: 7 个来源条目)`。
- 每个来源 unit 的 payload **只读一次**（内存 ~31 MB 峰值），多份副本共用同一个 `byte[]` 实例，写入侧的按内容去重会把它折叠成一个 GPU blob。
- 隐藏份的所有条目共用**同一份** TocData/GpuData 字节（只有 `AssetKey` 不同）。
- 临时产物落在 `%TEMP%/hd2-sectional-compile/<guid>/`，成功/失败都在 `finally` 里删；**失败时保留**一份副本并在 `Error` 里给出路径（沿用现有 `VpkRebuildService` 的"失败留现场"习惯）。

---

## 5. 模块与文件

新建（都在 `HD2ModCore/Infrastructure/Sectional/`）：

| 文件 | 职责 | 规模预估 |
|---|---|---|
| `SectionalCompiler.cs` | 流水线编排（§4.3 的 8 步）+ 结果对象 | ~250 行 |
| `SectionalCompilePlan.cs` | **纯函数**规划（§4.4 / §4.5），无 IO，可单测 | ~250 行 |
| `SectionalUnitPatchWriter.cs` | 改名副本 + 隐藏壳 + 写 patch（§4.6） | ~200 行 |
| `HiddenUnitSource.cs` | `IHiddenUnitSource` + 默认实现（来源 unit 现造） | ~60 行 |

改造：

| 文件 | 改动 |
|---|---|
| `HD2ModCore/Infrastructure/ModCompileService.cs` | `CompileAsync` 内部改为调用 `SectionalCompiler`；**删掉**对 `VpkRekeyService.RunAsync` 的调用；保留现有签名（GUI 三处调用点不动） |
| `HD2ModManager/ViewModels/ModDetailsPageViewModel.Vpk.cs` | 禁用就地烘焙入口；消息口径按 §4.9 |
| `HD2ModCore/Infrastructure/VpkRebuildService.cs` | 不动（它调的就是 `ModCompileService`） |

---

## 6. 复用的原语（不新写底层）

| 需要 | 现成实现 | 位置 |
|---|---|---|
| 读 patch 条目 / payload | `PatchTocScanner`、`PatchEntryPayloadReader` | `HD2ModAdaptation/PatchReconstruction` |
| 解析 unit 结构 | `UnitMeshReader.Read(tocData, gpuData, …)` | 同上 |
| 写 patch（条目表 + GPU 按内容去重） | `PatchArchiveWriter.WriteAsync(source, out, edits, additionalEntries, removedEntries, …)` | 同上 |
| 隐藏壳 | `CanonicalHiddenUnitBuilder.Build(GameDataUnitMesh, UnitTransformInfo, bool)` | `…/Canonical/CanonicalHiddenUnitOutputCache.cs` |
| 极小三角片 | `CanonicalPlaceholderMinifier`（3 顶点 / 1 三角形 / 保留 section 与材质序号） | `…/Canonical/CanonicalPlaceholderMinifier.cs` |
| 目标 kit 的 unit 列表 | `EquipmentUnitCatalogService.GetEntriesAsync()` + `ArmorCell` | `HD2ModCore/Domain/EquipmentUnitCatalog.cs`、`ArmorCell.cs` |
| 写 Overwrite 的事务原语 | `ManagedOverwriteDirectory`（`Prepare/Commit/Rollback/AdoptIfUnitLayer`） | `HD2ModCore/Infrastructure` |
| 装 patch + 装材质（一次事务） | `OverwriteMaterializer.Materialize(modDir, selection, unitLayerPatchPath, marker)` | 同上 |
| section 表 / 烘焙 | `WardrobeSectionMapFile.BuildAsync/WriteAsync`、`WardrobeSectionBaker.BakeAsync` | 同上 |
| 归属标记 | `ManagedOverwriteMarkers.VpkRekey`（复用，**不新增标记**） | `HD2ModCore/Decoration` |

> 不新增归属标记的理由：标记的唯一语义是"这个目录归管理器"，新增一个还要同步改 `ManagedOverwriteMarkers.All` 与所有判定点，收益为零。

---

## 7. 与旧链路的关系

| 组件 | 本轮处置 |
|---|---|
| `ModCompileService.CompileAsync` | **保留签名**，内部换成新编译器（GUI / `VpkRebuildService` 都不动） |
| `VpkRekeyService`（子进程壳） | **断开调用**，不删（见 Q2） |
| `CliVpkRekey` / `CanonicalCrossArmorOrchestrator` | **断开调用**，不删（见 Q2） |
| `vpk rekey --head-merge` 的同意门 | 不动（它约束的是 agent） |
| `TryBakeInPlaceAsync`（GUI 就地烘焙） | **禁用入口**，实现与测试保留（D13） |
| `vpk2patch --head-merge`（烘进 sectional） | 不动；协议外路径，与编译器无关 |
| `physbone embed-mod`（重构建里的子进程） | 不动，不在本轮范围 |
| `WardrobeHiddenUnitTemplate` | **保留**（`LuaDeploymentService` 旧衣柜运行时还在用），新编译器只是不依赖它 |

---

## 8. 验收用例（每条都要能自动断言）

| # | 场景 | 断言 |
|---|---|---|
| 1 | 自身素体 + 合并关 | `Overwrite/patch_0` 与 `sectional/patch_0` **逐字节相同**；无 `custom-model.json`；`sections.json` 存在 |
| 2 | 自身素体 + 合并开 | 注册表头件 `slot=2`、层 = 躯干第一个空层、unit 键不变；`sectional/custom-model.json` 的 SHA256 前后一致；`patch_0` 仍与 sectional 相同 |
| 3 | 游戏护甲 + 合并关（RE-2310） | 目标 kit 共 24 个 unit（护甲 23 + 头盔 1）；替换 **11** 份、隐藏 **12** 份、披风 **1** 份豁免；**不含**披风键 `0x3c33cf10a26cbb3e`；**不含**任何来源 unit 键；替换份的 GPU 字节与来源一致 |
| 4 | 游戏护甲 + 合并开 | 头落到躯干 pc1 的两个键；头盔键 `0xaf431352d9b95e83` 变为隐藏份；躯干 pc2 仍是我们的身体 |
| 5 | 关掉若干 section | 对应 dword 写 0；`sections.json` 记录的是**原值**；同样的开关再"打开"能写回原值（可逆） |
| 6 | 材质全关 | `Overwrite/` 只有 unit patch + 归属标记；隐藏份仍然存在；不报错 |
| 7 | 无配套头盔 | `Ok=true`、`HeadMerge=NoHelmetKit`、`Info` 有一条、其余部件正常写出 |
| 8 | 隐藏份有效性 | 输出 patch 里每个隐藏 key 的单元：mesh 数与原 unit 相同、**三角形总数 = 1**（证明 `minifyCullingMeshes: true` 真的生效） |
| 9 | 可复现 | 同样的输入编译两次，`Overwrite/` 逐字节一致 |
| 10 | 失败不留半成品 | 人为让 sectional 缺 unit patch ⇒ 返回 `Ok=false`，`Overwrite/` 保持上一次的内容 |

---

## 9. 测试计划

- **纯规划测试**（快，无 IO）：`SectionalCompilePlan` 的 6 条不变式 + 披风豁免 + 肩隐藏 + 身形复制 + 头身合并取层 + 单层躯干不合并 + 同槽位第二个来源被丢掉并告警。
- **隐藏壳测试**：用沙箱来源 unit 造一次隐藏壳，断言"三角形总数 = 1、mesh 数不变、TocData 可被 `UnitMeshReader` 重新解析"。
- **写盘测试**：临时目录里造一个最小 sectional（复用现有 `ModFixture` 风格），跑完整编译，断言用例 1/2/9/10。
- **真实样本测试**（可跳过）：沙箱 `E:\tmp\rb2-mods\Lunalice_Bill` 存在时，跑用例 3/4/6/7。
- 全量 `dotnet test` 必须绿；`dotnet build -c Debug` 0 警告 0 错误。

---

## 10. 决策（已拍板，2026-11-08）

| # | 问题 | 决定 |
|---|---|---|
| **Q1** | 目标选了**另一个**已注册素体（不是本 Mod 自己的） | **显式拒绝**，报错说明"不支持把 VPK 模组挂到另一个已注册素体上"。后续 GUI 优化成"素体只能选自己那一份" |
| **Q2** | `vpk rekey` / `CanonicalCrossArmorOrchestrator` 保留还是删除 | 本轮只**断开调用、不删**（编译器不再引用它们；CLI 那条留给 agent 调试） |
| **Q3** | 来源 unit 的**自称槽位**要不要跟着目标改写 | **不改**，只改 unit 键（与老链路的实际行为一致）。出现放置问题再加 `UnitCustomizationRewriter` |

## 11. 实现状态（2026-11-08 落地）

| 项 | 状态 |
|---|---|
| `SectionalCompilePlan.cs`（规划器，纯函数） | ✅ 已实现 + 9 条单测 |
| `SectionalHiddenUnit.cs`（隐藏壳来源） | ✅ 已实现（`minifyCullingMeshes: true`） |
| `SectionalUnitPatchWriter.cs`（改名副本 + 隐藏壳 + 写 patch） | ✅ 已实现 |
| `SectionalCompiler.cs`（8 步流水线 + 结果对象） | ✅ 已实现 |
| `ModCompileService.CompileAsync` 改为调编译器 | ✅ 已接线（GUI 三处调用点未动） |
| GUI：禁用就地烘焙、消息口径改为读实际结果 | ✅ 已改 |
| `dotnet build -c Debug` | ✅ 0 警告 0 错误 |
| `dotnet test -c Debug` | ✅ 2028 通过 / 1 跳过 |

**真样本验收（沙箱 `Lunalice_Bill` + RE-2310）实测结果：**

| 断言 | 结果 |
|---|---|
| 自身素体 + 合并关：unit patch 逐字节相同 | ✅ |
| 自身素体 + 合并开：只改 `Overwrite/custom-model.json`，`sectional/` 的 SHA256 不变 | ✅ |
| 换甲：来源 unit 键一个都不残留 | ✅ |
| 换甲：披风归档的 unit 键一个都不出现 | ✅ |
| 换甲：**替换 11 份 / 隐藏 12 份**（与 §3.2 的手算表一致） | ✅ |
| 隐藏壳：三角形数在 1..16（每个 mesh 一个三角形，真模型是几万） | ✅ |

复跑方式（样本存在时才跑，否则静默跳过）：

```
$env:HD2_TEST_SECTIONAL_MOD='E:\tmp\rb2-mods\Lunalice_Bill'
$env:HD2_TEST_MANAGER_ROOT='…\HD2ModManager\bin\Debug\net10.0-windows'
dotnet test HD2ModCore.Tests --filter "FullyQualifiedName~SectionalCompile"
```

**还没做的**（明确排在后面）：

- 就地烘焙入口仍是禁用状态（`EnableInPlaceBake = false`），等"编译戳"一起做。
- GUI 还没把素体目标限制成"只能选自己那一份"（现在选别人的会得到编译器报错）。
- 旧链路仍未删除（Q2）。
- 编译层没有进度回调（一次编译几百毫秒，先不加）。

---

## 附录 A：对上一份交接文档的更正

| 上一份文档的说法 | 实际 |
|---|---|
| `HD2ModCli/CliVpkRekey.cs`（959 行） | **1047 行**（行号引用基本正确，只有行数错） |
| `HD2ModAdaptation/…/EquipmentUnitCatalog.cs` | 该文件**不存在**；实际是 `HD2ModCore/Domain/EquipmentUnitCatalog.cs` |
| `CliEquipmentFacts.cs`（129 行） | **140 行** |
| "`movedFrom` … 换回来时靠它" | **没有任何代码读 `movedFrom`**；关掉合并靠"删掉派生文件"，不是靠回读 |
| "头身合并是**第三处**实现" | 实际有 **4 处**：`HelmetToArmorMergeService`（独立页）/ `vpk2patch --head-merge`（烘进 sectional）/ `vpk rekey --head-merge`（按层推算）/ 编译期改注册表 |
| §0"编译层必须自己完成替换护甲，不得通过子进程"是"设计要求" | 这是**倒推**出来的结论。真实回归点是 `249b93f` 给 CLI 加了 `--head-merge-consent` 硬门，而 `VpkRekeyService` 不传 —— GUI 被自己的门打死。最小修复本是 1 行；本轮之所以重写编译器，是因为 D14（要简单粗暴、不依赖旧物），不是因为"必须" |

## 附录 B：本轮发现的现有缺陷（不在本计划范围内，但要记录）

1. **编译层把材质物化跑了两遍**：`CompileOwnModelAsync` 内部一次、`CompileAsync:119` 再一次；第二次的 `Prepare` 把整个目录挪走，
   于是第一次刚写的 `sections.json` / `components.json` 被自己删掉（`5a6d7a2` 引入）。沙箱里 `Overwrite/` 没有 `sections.json` 就是这个后果。
2. **`sections.json` 记录的是"烘焙后"的值**：恒等路径与自身素体路径都是"先 bake 再 Build"，于是隐藏过的 section 被记成 `NumIndices=0`，
   将来"打开"会写回 0 —— 看起来成功、其实没恢复。只有换甲主路径的顺序是对的。
3. **就地烘焙实际是死路**：它要求 `Overwrite/sections.json`，而（1）每次都把它删掉 ⇒ GUI 用户永远走"全量重跑"。
4. **`isOwnModel` 只看 `custom-model:` 前缀**：选**别人的**素体也会被判成自身素体，于是 unit 键不变、目标被静默忽略（→ Q1）。
5. **界面会撒谎**：自身素体 + 合并开时，无论 `WriteMergedCustomModel` 成功与否都说"头件已挂到胸部槽位第 1 层"（层号还是写死的）。
