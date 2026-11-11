# sectional 与 vpk2patch 的两处显示不对称（2026-11-10）

用户实机测完 11 件 sectional 产物后提出的两条：

1. **资产标签不对**：`水墨牛牛-20261110-172224` 在 Mod 条目与详情页显示的是 `RS-100“制裁者”`（游戏护甲），
   而 vpk2patch 类的产物显示的是**自己的素体名**。
2. **section 树不显示**：vpk2patch 类 Mod 能打开「分件编辑」并看到 7 个部位，sectional 产物看到的是一棵空树
   （详情页那个入口干脆是灰的，因为 `CanOpenSectionalEditor = IsVpkMod && HasVpkComponentTree`）。

两条都能落到**同一句话**上：sectional 产物没有把"这 11 个 unit 各是什么"写下来 / 认出来。
vpk2patch 那条链路写了，所以它看起来是对的。

---

## 一、两类产物的 unit 键不一样（这是全部不对称的源头）

| | vpk2patch（`.vpk` 转出来的） | sectional（legacy patch 转出来的） |
|---|---|---|
| unit 键 | **转换时新生成的**：`<12 位命名空间>0001..0007` | **沿用来源的游戏 unit id**：`097f0cb9568afcd1`… |
| 游戏数据里查得到吗 | 查不到（是不存在的键） | 查得到，属于 `RS-100“制裁者”` |
| `sections.json` 的 unit 身份 | 转换器**自己写**（`Helmet/Undergarment/Any`） | **全空**（来源 unit 自己不带 customization 串） |
| 注册表里的素体 | 13 个 piece（7 槽 × 身形，头盔 Any） | 11 个 piece（同形） |

`sectional/sections.json`（实机产物）：

```json
{ "fileId": "00a8b09bf2f697f7", "slot": "", "pieceType": "", "bodyType": "", "weight": "", "sectionCount": 7 }
```

`vpk2patch/sections.json`（恋星milfy_bill）：

```json
{ "fileId": "1ee1f8c7bffe0001", "slot": "Helmet", "pieceType": "Undergarment", "bodyType": "Any", … }
```

## 二、资产标签为什么错

`ModAssetSummaryProjector.BuildMappingAsync` 把"运行时注册表里的素体"叠到游戏数据映射上，但**只在游戏数据
查不到这个 unit 时才叠**：

```csharp
if (owners.TryGetValue(key.FileId, out var archives)
    && (!assets.TryGetValue(key, out var existing) || existing.TargetArchives.Count == 0))
    assets[key] = new(...CustomModel...);
```

- vpk2patch 的 unit 键是自造键 ⇒ 游戏数据查不到 ⇒ 走这条叠加 ⇒ 标签 = **素体名**（凑巧"正确"）。
- sectional 的 unit 键是真实游戏键 ⇒ 游戏数据查到 `Armor / RS-100“制裁者”` ⇒ **不叠加** ⇒ 标签 = 护甲名。

而 sectional 产物的语义并不是"替换 RS-100"：它是一具**自定义素体**，注册表 `mappings` 里它被挂到
`FS-05“神射手”`、`DS-191“天蝎”`、`DP-53“自由救星”` 上（`custom-a3c276a9c3a347549883ea0ffee1a3be`）。
护甲名是这个产物的**实现细节**（我们复用了这些 unit 壳），不是它的身份。

**修法**：unit 属于**本 Mod 自己**登记的素体时，用它标注，**压过**游戏数据的护甲映射。
保留原来的"游戏数据查不到才叠加"作为兜底（别人家的素体键、被解包出来单独导入的 patch）。

为什么必须是"本 Mod 自己的素体"而不是"任何素体"：sectional 产物把 11 个**真实游戏 unit id** 登记进了自己的
素体里。若按"任何素体拥有"就覆盖，那么**任何**替换 RS-100 的手写 Mod 都会被标成"水墨牛牛"。判据只能是
"这个 Mod 目录里的 `custom-model.json` 就是它"。

## 三、section 树为什么不显示

`VpkModWardrobeService.ReadSectionMapAsync` 读到表了，`VpkModWardrobeTree.Build` 也建树了，但
`IsBaseSlotUnit` 要求 `pieceType == Undergarment || slot == Helmet` —— 11 个 unit 的 `slot`/`pieceType`
全是空串，一个都进不来，于是 `HasRoots == false`。

同一份空身份还打断了另外两条链路（都是读 `sections.json` 的槽位）：

- `SectionalCompiler.CellOf`：认不出部位 ⇒ 全是 `ArmorCell.Unknown` ⇒ **换甲/编译到目标护甲**对 sectional 产物无从匹配。
- `CliVpkRekey.BuildSourceEntry`：`PartKindOf("")` = Unknown ⇒ 整份 source entry 一个 part 都没有，直接抛
  `VpkSourceUnitsEmpty`。

**修法**：转换时把每个 unit 的身份写进 `sections.json`，取值源与 `custom-model.json` **同一个**（拆件计划）：

- `slot` = 计划里该 target 的槽位（`Helmet / Torso / Hip / LeftArm / RightArm / LeftLeg / RightLeg`）
- `bodyType` = 计划里的身形（`Any` / `Slim` / `Stocky`）
- `pieceType` = **`Undergarment`**：素体定义里每个 piece 的层号一律 0（见 `SectionalModelDefinition`），
  两层必须一致；这一层"一个槽一个 unit、层由 section 开关表达"就是那个决定。
- `weight` 不写（来源侧本来没有这个事实，不编）

编译（`SectionalCompiler`）重新建表时把身份**继承**过去：自身素体按 fileId 直接对齐；换甲时按
`plan.Replacements`（目标键 → 来源键）对齐。否则 `Overwrite/sections.json` 又会是空身份。

**存量产物要重构建一次**才有身份（这是产物内容的变化，不是程序读了别的文件）。

## 四、不做的事

- 不改 unit 键体系（不给 sectional 产物生成新命名空间）：现有产物已实机验证可用，那是一次大改。
- 不给空身份的老表加"退化成全部 unit"的猜测：树会画出"未标注槽位"，比空树更误导。
- 不动资产标签对**非本 Mod 素体**的兜底语义。

## 五、验收

1. `sections.json` 的 11 个 unit 带上槽位/身形/`Undergarment`；`IsBaseSlotUnit` 命中；7 个部位 × 2 身形可见。
2. 资产标签与详情页：`CustomModel` 一组、名字 = 素体名；不再出现 `RS-100“制裁者”`。
3. 别人的手写 Mod 替换同一批 unit 时，标签仍是 `RS-100“制裁者”`（不串味）。
4. `dotnet build -c Debug` 0 警告 0 错误；全量测试绿。

## 六、实测（真机产物 + 真实索引库）

用 `水墨牛牛-20261110-172224` 的 `sectional/<hex>.patch_0` 与它的 `sectional-source.json`（21 条坐标）
重跑一次拆件（输出到临时目录，不动库里的产物）：

```
intents=21  valid=True  warnings=11（都是既有的 DecorationTransformExpansionFailed，与本次改动无关）
--- 落盘的 sections.json --- units=11 带部位=11
  00a8b09bf2f697f7 slot=RightLeg piece=Undergarment body=Any
  097f0cb9568afcd1 slot=RightArm piece=Undergarment body=Slim
  126d33b362aa8b48 slot=LeftLeg piece=Undergarment body=Any
  34150b9559aad20e slot=LeftArm piece=Undergarment body=Slim
  4a52e2b0edd45dd4 slot=LeftArm piece=Undergarment body=Stocky
  4ebbfde8692577ff slot=Torso    piece=Undergarment body=Slim
  7245b46261345371 slot=RightArm piece=Undergarment body=Stocky
  75c5e56949cfe382 slot=Hip      piece=Undergarment body=Slim
  79383d82e3bf6b03 slot=Hip      piece=Undergarment body=Stocky
  a0cc3d2318d9e237 slot=Helmet   piece=Undergarment body=Any
  ecc8df6ccc7721bd slot=Torso    piece=Undergarment body=Stocky
--- 组件树 --- 身形分支=2  HasRoots=True
  健壮: 头盔(5 段), 胸部(6 段), 胯部(7 段), 左腿(4 段), 右腿(7 段), 左臂(8 段), 右臂(8 段)
  纤细: 头盔(5 段), 胸部(6 段), 胯部(7 段), 左腿(4 段), 右腿(7 段), 左臂(8 段), 右臂(8 段)
```

标签侧的事实核对（`asset-index.sqlite`）：

- 产物 11 个 unit 全部落在 `1e2ba3712fa1b030 / Armor / RS-100“制裁者”` —— 这就是修复前显示的标签。
- 产物自己的 `sectional/custom-model.json` key = `custom-a3c276a9c3a347549883ea0ffee1a3be`，
  工作区注册表里那具素体（名字 `水墨牛牛-20261110-172224`）恰好持有这 11 个 unit ⇒ 现在标签取素体名。

## 七、遗留

**存量 sectional 产物要重构建一次**才有身份（`sections.json` 是产物内容，不是运行时读法）。
库里那条 `水墨牛牛-20261110-172224` 在重构建之前，组件树仍然是空的——这是事实，不做"猜一个部位"的兜底。

