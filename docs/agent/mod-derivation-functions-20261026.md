# 「造一个新 Mod」的十个功能 · 护甲映射 · 自定义素体 · VPK 支持

**日期：** 2026-10-26
**状态：** 只做盘点，不改代码。
**配套：** `mod-capability-fusion-20261026.md`（能力总览与操作页融合）、`armor-unit-facts-audit-20261025.md`（部位事实）。

本文把上一轮笼统说的"10 个造"逐个拆开，再加上护甲映射、自定义素体、VPK 三块。

---

## 0. 先说三个结构性事实

### 0.1 Mod 详情页是个"发射台"，不是一个"操作台"

22 个命令的实际分工：

| 类型 | 数量 | 说明 |
|---|---|---|
| 打开独立页面/窗口 | **13** | 合并后是 **11 个不同页面**（材质三个命令共用一个页面） |
| 当场干活 | 8 | 刷新、改图像、加配置、删除、头盔合并切换、模型解析、依赖图测试、依赖图对比 |
| 打开文件夹 | 1 | — |

所以现在不是"一个页面有 22 个按钮"，而是**"一个页面 + 11 个独立页面/窗口"**，
用户每点一个按钮就被丢到一个新页面，那个页面有自己的来源选择、自己的输出目录、自己的生成按钮。

### 0.2 所有"造"共享同一段结尾，各自实现了一遍

```
[选来源] → [选目标] → [预览] → [选输出目录] → [生成] → [可选：自动导入 Mod 库]
```

三份独立的底栏内容 XAML（`CustomModelConversion` / `MaterialPackaging` / `SameKeyRebuild`）
+ 跨护甲页内联的一套，就是这段结尾的四份实现。

### 0.3 全部只写新目录，一个缓存都不失效

除了「头盔合并切换」和「编辑装饰」，10 个"造"都只往新目录写，不碰 `library.json`、
不碰部署图、不碰任何索引。**这决定了它们可以安全地合并成一个骨架**——
用户在骨架里怎么试都不会弄脏现有状态。

---

## 1. 十个"造"逐个拆解

### 1.1 修复 patch / 重构 patch（Same-key Canonical 重构）

| 项 | 内容 |
|---|---|
| 用户想解决的问题 | "游戏更新后这个 Mod 的模型错位/不显示了" |
| 用户输入 | 只选 Mod（可选输出目录、是否导入） |
| 内部做什么 | 对每个失效的 Unit 做 **same-key 重构**：保留 AssetKey 不变，用 Canonical 重建器重新装配 Unit 的骨骼组 / 流 / mesh / 材质绑定 / GPU 资源 |
| 读 | 来源 Patch 全部条目、`asset-index.sqlite` 的 Unit 版本事实 |
| 写 | **新目录**（默认 `Output/`） |
| 前置 | 有 Patch 内容（`HasPatchContent`） |
| 失败模式 | 源 Unit 版本已是最新 → 无候选；Patch 家族指纹漂移 → 拒绝 |
| CLI | `patch repair inspect/plan/validate/apply`（单）、`patch repair batch ...`（批量） |
| GUI | 「修复 patch」「重构 patch」两个按钮 → 同一个 `OpenSameKeyRebuild()` |

⚠ 两个按钮指向同一个 `RebuildSameKeyCommand`，但界面上是两个名字。这是**命名重复**，
不是两种功能——融合时应该只留一个。

### 1.2 Patch 资源重打包（Repackaging）

| 项 | 内容 |
|---|---|
| 用户想解决的问题 | "这个 Mod 的材质/贴图挤在一个大 patch 里，我想拆开或换掉其中几个" |
| 用户输入 | 勾选要打包的 AssetKey（默认只选**无冲突**候选）、输出目录 |
| 内部做什么 | 把选中的完整 AssetKey Payload 从来源 Patch 搬到新的独立 Patch |
| 读 | 来源 Patch 组、整条 AssetKey Payload |
| 写 | **新目录** |
| 前置 | 有 Patch 内容 |
| 特点 | **只允许选已有完整 Payload 的 AssetKey**，不做任何重新编码 |
| CLI | `patch repackage inspect/plan/validate/apply` |
| GUI | 「Patch 资源重打包」→ `OpenPatchResourceRepackaging()` → 独立页 |

界面里还有「轮换解决冲突」按钮——说明多个 Mod 抢同一个 AssetKey 时，用户可以轮换选择。

### 1.3 规整护甲 patch 包（Armor Normalize）

| 项 | 内容 |
|---|---|
| 用户想解决的问题 | "这个护甲 Mod 的 7 个 Unit 长得不对，身形覆盖也不全" |
| 用户输入 | 只选 Mod |
| 内部做什么 | `ArmorNormalizationService.InspectAsync`：对每个 Armor 来源，按 **5 个角色**（`Roles`：Head / Torso / LeftArm / RightArm / Pelvis，其中 Pelvis 覆盖胯 + 双腿）挑"标准承载 Unit"，检查身形覆盖（Slim/Stocky/Any），生成计划 |
| 读 | Mod 的 Unit 几何事实（`UnitGeometrySummary`）、装备表、`asset-index` |
| 写 | **新目录** |
| 前置 | 有 Patch + 装备表可用 |
| 诊断 | 每个角色给出"选中了哪个 Unit / 为什么没选中 / 缺哪个身形" |
| CLI | `equipment armor-normalize inspect/plan/validate/apply` |
| GUI | 「规整护甲patch包」→ `OpenArmorNormalization()` |

**关键**：它依赖上一轮统一的部位判定（`PartKind != Unknown`、`HasRenderableGeometry`、`!IsCullingMesh`），
是"部位事实"最直接的消费者之一。

### 1.4 头盔合并到盔甲（Helmet Merge，两种落点）

| 项 | 内容 |
|---|---|
| 用户想解决的问题 | "我想让护甲的头盔和身体一起换" |
| 两种落点 | **导出新 Mod**（`helmet-merge apply`）／**临时并进现有主体**（`host-derived plan helmet`） |
| 用户输入 | 导出：选 Mod + 输出目录；临时：只选 Mod（切换） |
| 内部做什么 | 把 Armor/Any Helmet 的 Unit 追加到胸部 kit，并生成**隐藏头盔 Unit**（`wardrobe_hidden_unit.*`） |
| 读 | 来源 Patch、装备表、主体当前装饰状态 |
| 写 | 导出 → **新目录**；临时 → 主体 `Overwrite/` |
| 前置 | 有 Patch；临时模式还要求已部署 |
| 冲突检查 | validate 会拒绝"胸部/待隐藏头盔 Unit 重叠" |
| CLI | `equipment helmet-merge inspect/plan/validate/apply`、`host-derived plan helmet` |
| GUI | 「头盔合并到盔甲并输出」+「切换」两个按钮 |

⚠ 这是"同一件事两种落点"的典型。融合时应该是一个操作 + 一个"应用方式：临时 / 导出"选择。

### 1.5 跨护甲替换（Cross-Armor）

| 项 | 内容 |
|---|---|
| 用户想解决的问题 | "我想让 A 护甲显示成 B 护甲的外观" |
| 用户输入 | 来源 Mod、来源 Armor/Helmet、来源身形、优先身形、目标护甲多选、输出目录 |
| 内部做什么 | 按 `(body, slot, piece_type)` 把来源 Unit 映射到目标 Unit；缺槽用隐藏 Unit 填满；未命中部件可自动隐藏 |
| 读 | 来源 Patch、`asset-index` 的**部位事实**（要求 `Confidence == 100`）、装备表 |
| 写 | **新目录** |
| 前置 | 部位事实必须可信（`PartKind != Unknown && Confidence == 100`） |
| 关键选项 | 手动模式、自动隐藏其余部件、自动多选通用部件、自动勾选头盔 |
| 预览 | 左表「来源可见部件」、右表「目标预测」（部位/结果/ID/目标/尺寸/命中·复用/身形） |
| CLI | `equipment cross-armor inspect/plan/validate/apply` |
| GUI | 「生成替换其他护甲的模组」→ 独立规划页 |

**这是上一轮审计里问题最集中的地方**：两列的"部位"和"目标"分别绑 `PartKind`（混合来源）和
`SemanticName`（5 种拼法，含 `1447930109_lod0` 这种兜底名）。

### 1.6 一键转为自定义素体（Custom Model Convert）

| 项 | 内容 |
|---|---|
| 用户想解决的问题 | "我想把这个 Mod 的模型变成一套可以复用/可自由拼装的素体" |
| 用户输入 | 来源 Armor + 来源 Helmet（各至多一个）、素体名称、输出目录 |
| 内部做什么 | 把来源 Archive 的可见 Unit **复制成全新 Unit ID**，写成独立 Patch，并把素体登记进**护甲注册表** |
| 读 | 来源 Patch、装备表、`asset-index` 部位事实 |
| 写 | **新目录** + `armor-registry.protocol` 里的 `model_piece` 条目 |
| 前置 | 来源必须有**独占 Unit**（不能与别的护甲共用）；`CanPlanCrossArmorTransfer` |
| 部位决定 | `CustomModelConversionService.SlotFor`：部位 → 注册表槽位（上一轮已修，肩甲不再折成躯干） |
| CLI | `custom-model inspect` / `custom-model convert` |
| GUI | 「一键转为自定义素体」→ `OpenCustomModelConversionAsync()` |

⚠ **这是唯一一个"造"同时写库外目录和注册表的操作**，所以它不可完全撤销：
删掉输出目录不会删掉注册表里的素体条目。

### 1.7 生成装饰 Mod（Decoration Package）

| 项 | 内容 |
|---|---|
| 用户想解决的问题 | "我想把某个 Mod 里的装饰件挂到另一个 Mod 的护甲上" |
| 用户输入 | 来源 Mod、目标主体 Mod、Unit 选择、附着策略、输出目录 |
| 内部做什么 | 把选中的装饰 Unit 打进独立包，声明它附着到哪个主体 |
| 读 | 来源 Patch、装备表、`DecorationHostTargetBinder`（决定挂到主体的哪个 mesh） |
| 写 | **新目录** |
| 前置 | 来源有 Patch |
| CLI | `decoration package plan/validate/apply`、`decoration inspect source` |
| GUI | 「生成装饰 Mod（以来源 Mod）」→ `OpenDecorationPlan()` |

附着策略由 `DecorationHostTargetBinder` 决定：要求主体 Unit **只有一个可见 LOD0 mesh**，
多部件的 Unit 必须拒绝（否则会把装饰挂错地方）。

### 1.8 批量生成装饰（Batch Decoration）

| 项 | 内容 |
|---|---|
| 与 1.7 的区别 | 一次对**多个主体**生成；每个主体一个输出包 |
| 用户输入 | 来源 Mod、主体多选（全选 Mod / 全选 / 反选）、选项过滤 |
| 额外选项 | 「来源部位与目标部位完全一致时替换而不注入」 |
| 写 | **新目录 × N** |
| CLI | 无对应单命令（GUI 侧循环调用 `decoration package`） |
| GUI | 「批量生成装饰（以主体 Mod）」→ `OpenBatchDecorationPlan()` |

### 1.9 编辑装饰 Mod（Decoration Edit）

| 项 | 内容 |
|---|---|
| 用户想解决的问题 | "已经导入的装饰包，我想改它选了哪些 Unit / 挂到哪" |
| 用户输入 | 装饰编辑规格（JSON）：Unit 选择、附件策略、部位、身形、DualVariantMode |
| 内部做什么 | 原子更新已导入的装饰定义，**回建所有已启用主体**的 Overwrite |
| 读 | `library.json`、来源 Patch、当前主体状态 |
| 写 | `library.json` + 所有相关主体的 `Overwrite/` |
| 前置 | 必须是已导入的装饰 Mod |
| 失败处理 | 失败时回建已完成的主体（不是全部丢下不管） |
| CLI | `decoration edit export/inspect/plan/validate/apply` |
| GUI | 「编辑装饰 Mod」→ `OpenDecorationEditor()` |

⚠ **这是"造"里唯一会改库内产物的**。它和 1.7/1.8 共用"装饰"这个词，但作用完全不同。

### 1.10 材质打包（三个命令，一个页面）

| 命令 | 模式 | 干什么 |
|---|---|---|
| 材质拆分（`SplitEmbeddedMaterials`） | `splitEmbeddedMaterials: true` | 把内置材质拆成独立材质包 |
| 替换内置材质（`ReplaceEmbeddedMaterials`） | `requireAllExternalMaterials: false` | 用外部材质替换内置材质 |
| 只集成引用目标（`EmbedExternalMaterials`） | `requireAllExternalMaterials: true` | 只把被引用的材质集成进来（外链） |

| 项 | 内容 |
|---|---|
| 用户输入 | 选材质/贴图、输出目录、是否自动导入 |
| 内部做什么 | 基于**导入后的轻量引用图**（不需要高级分析）重新组织材质与贴图归属 |
| 读 | Patch 的材质绑定表、引用图 |
| 写 | **新目录** |
| GUI | 三个按钮 → `OpenMaterialPackaging(flags)` → 同一个页面 |
| 页面提示 | 「材质操作基于导入后的轻量引用图；无需执行高级分析」 |

⚠ 三个按钮进同一个页面、只差两个布尔。融合时应该是一个操作 + 一个"模式"下拉。

---

## 2. 护甲映射（Armor Registry）

这是**唯一一个"改运行时行为"而不是"造 Mod"的子系统**，也是唯一一个碰游戏数据文件的。

### 2.1 它解决什么

"让护甲 A 在游戏里显示成护甲 B 的外观，**不新增 unit、不改 gameplay**"。

### 2.2 三种模式（`ArmorMappingMode`）

| 模式 | 允许热切换 | 机制 |
|---|---|---|
| 防污染兼容模式（独占 lua 入口） | ✅ | Lua 调 `set_resource_override` 重定向 unit |
| 防污染兼容模式（外置 lua 入口兼容） | ✅ | 同上，但走用户已有的 lua 加载器 |
| 真映射模式 | ❌ | 直接改注册表件记录的 `unit` 字段 + 伪造内容校验和 |

### 2.3 真映射模式的完整链路

```
游戏注册文件（加密 .dl_bin）
  → 解密 → 件表（header64 + bodies3×24 + pieces×112）
  → 只改 +0 unit / +8 slot / +12 piece_type
  → 重算 MurmurHash64A 内容校验和（闭合式伪造）
  → 重新封装 → 写回
```

**不许碰**：`+16 weight`（gameplay 数值）、`+24..80` 八个资源引用、`+88 tone`。
空闲区 `+89..111`（23 字节，恒为 `7×00 7×FF 9×00`）用来放伪造的哈希。

### 2.4 信任模型（上一阶段做的）

| 不变量 | 内容 |
|---|---|
| I1 | 权威数据一律读**备份 B**，不读磁盘上被改过的原件 |
| I2 | 索引指纹对"我们自己的写入"不敏感 |
| I3 | B 必须与当前 buildid 一致 |
| I4 | 非真映射模式下注册表保持官方原样 |
| I5 | 真映射可从 B + 映射表重现 |

Steam 校验是**唯一可信的时刻**；校验完成后立刻用官方文件重建 B。

### 2.5 相关文件

| 文件 | 作用 |
|---|---|
| `armor-registry.protocol` | 映射表（`map=目标,来源`）、模式、revision |
| `armor-registry.workspace` | 工作区（掩码、自定义素体定义） |
| `wardrobe.links` | 编译后的 unit 对列表（兼容模式用） |
| `wardrobe.masks` | section 级开关（衣柜） |
| `backup/generated_customization_armor_sets.original.dl_bin` | 可信原件 |
| `backup/armor-data-mapping.state` | `pristineSha\|writtenSha\|pristineContent\|buildId\|dataDir` |

### 2.6 与"造 Mod"的关系

**护甲映射不是造 Mod**，它改的是游戏注册表。所以：
- 它不产出可导入的 Mod
- 它的产物在游戏数据目录里，不在 Mod 库
- 它需要一个全局页面（现在的「护甲映射」导航页），不属于某个 Mod

---

## 3. 自定义素体（Custom Body / Model）

这里有**两个容易混淆的东西**，名字都叫"素体"：

### 3.1 一次性的「转素体」动作（1.6）

`custom-model convert` → 复制 Unit 成新 ID → 写独立 Patch → 登记一条 `model_piece`。
**产物是一个新 Mod + 一条注册表条目。**

### 3.2 注册表里的「素体定义」（`RuntimeArmorRegistryModel`）

| 字段 | 含义 |
|---|---|
| `Key` | 素体键（`custom/<名字>`） |
| `SourceItemId` | 可选：跟随某个来源护甲 |
| `Pieces` | 一组 `(Body, Unit, Slot, PieceType, Tone, Enabled, IsDefaultBase, IsReserved, OriginalBody)` |
| `Name` | 显示名 |

`Pieces` 就是**护甲结构的四元组**：`(body, slot, pieceType) → unit`。
所以素体本质上是一张"自己拼的件表"，可以任意组合身体/内衣/护甲/装饰件。

### 3.3 素体的编辑能力

| 能力 | 说明 |
|---|---|
| 合并为 Any | 把 Stocky+Slim 两条并成一条 `Body=3` |
| 拆分为双身形 | 把 `Body=3` 拆成 Stocky + Slim，并分配新 Unit ID |
| 补缺失身形 | 单身形素体补另一支 |
| 启用/禁用单个件 | 只影响已启用组件 |
| 跟随视觉来源 | 目标护甲跟随来源护甲的件表变化 |

**互斥规则**（`RuntimeArmorModelStructure`）：同一个 `(Slot, PieceType)` 要么一条 Any，
要么一对 Stocky+Slim，不能共存。这条规则和游戏注册表的实测结果完全一致（0 违例）。

### 3.4 素体 vs 护甲映射的边界

| | 素体 | 护甲映射 |
|---|---|---|
| 产物 | 新 Mod + 注册表条目 | 游戏注册表改动 |
| 谁消费 | 护甲映射（当作"来源"） | 游戏运行时 |
| 能单独用吗 | 不能，必须被映射引用 | 能 |

**所以素体是护甲映射的"素材"，不是独立功能。** 现在的 UI 把"转素体"放在 Mod 页、
把"素体编辑"放在护甲映射页，用户很难理解这两者的关系。

---

## 4. VPK 支持

VPK 支持是**独立的一整条链路**，不是"又一个造 Mod 功能"。

### 4.1 导入：VPK → patch

```
来源 VPK
  → 解压
  → VpkImportPlanner 规划（自动识别类型：含 .mdl → unit patch；只有 .vmt/.vtf → 材质/贴图 patch）
  → 逐个 vpk2patch
  → 交给普通导入管线
```

| 特点 | 说明 |
|---|---|
| 一个 VPK = 一个 patch | 只含它自己的资源 |
| 整批构成"解析集合" | 材质 VPK 从队列找模型（算 matcap 平均色要几何）；模型 VPK 从队列找材质 |
| 跳过必须给理由 | `VpkImportReport` 明确区分 Converted / Skipped / Failed |
| matcap 两种策略 | `bake`（面积加权平均色烘进底色，转换时固定）／`albedo`（matcap 当底色，可由不同 patch 提供） |
| 组件处理 | `merge`（默认，并进主导部位）/ `all`（每个配件一份 Unit）/ `none` / 指定名单 |

### 4.2 转换产物结构

```
<输出根>/<VPK 名>/
    <hash>.patch_0        ← Unit patch
    <hash>.patch_1        ← 材质包（给了 --material-source 时）
    vpk2patch/            ← 标记这是 VPK 版 Mod
```

**`vpk2patch/` 目录就是"VPK 版 Mod"的判据**（`IsVpkMod`）。

### 4.3 VPK 版 Mod 在 Mod 页的特殊待遇

| 行为 | 原因 |
|---|---|
| **不显示 patch 设置卡** | 那张卡上的按钮（修复/跨护甲/规整/头盔合并）都假设"这是一堆手写 patch"；VPK 的 patch 是转换产物，重转一次就全变，patch 级编辑会被覆盖 |
| 显示「动作卡」 | 换目标护甲 |
| 显示「衣物装饰组件卡」 | 勾选这个 VPK 里哪几块几何不画 |

要 patch 级编辑？提示用户把 `vpk2patch/` 里的 patch 拿出来当普通 Mod 导入。

### 4.4 换甲（vpk rekey）

| 项 | 内容 |
|---|---|
| 用户输入 | 目标护甲（下拉，默认 RE-2310）、可选目标 archive 列表 |
| 内部做什么 | 调外部 `hd2mod vpk rekey`，把 VPK 的 **7 个 Unit** 换到目标护甲上，并生成隐藏 Unit |
| 为什么走外部进程 | 换甲要读装备表、读目标 Unit、生成隐藏 Unit，CLI 里已经跑通；管理器再实现一遍就是两份会漂移的实现 |
| 写 | Mod 目录里的 patch（换 key） |
| 记忆 | 成功后把"指向哪件护甲"写进 `wardrobe.json` |

### 4.5 组件树与烘焙（`wardrobe.json`）

| 字段 | 含义 |
|---|---|
| `ComponentTreeEnabled` | 卡片是否启用（和勾选状态放同一个文件，避免"卡片开了但勾选丢了"） |
| `State` | 勾选状态（哪些 section 不画） |
| `TargetArmorArchiveId` / `TargetArmorName` | 当前指向哪件护甲（树标题要用） |

两条设计决定：

1. **状态放 Mod 目录，不放注册表**——它只对这一个 Mod 有意义，放进注册表会和衣柜掩码混成一件事。
2. **读 Overwrite 优先**——换过目标护甲后实际生效的是 `Overwrite/` 那份，组件树必须画"正在生效的那份"。

### 4.6 VPK 与护甲映射的关系

| | VPK 换甲 | 护甲映射 |
|---|---|---|
| 改什么 | Mod 自己 patch 里的 Unit key | 游戏注册表 / 运行时 override |
| 影响范围 | 只有这个 Mod | 全局 |
| 可共存吗 | 可以：VPK 换甲后再用护甲映射把它指到别的护甲 | |

**它们不冲突，但用户会分不清。** "换甲"这个词在 VPK 卡和护甲映射页各出现一次，含义完全不同。

---

## 5. 横向对比

| 功能 | 读 GameData | 读索引 | 写注册表 | 写库状态 | 写库产物 | 写库外 | 可撤销 |
|---|---|---|---|---|---|---|---|
| 修复/重构 patch | 是 | 是 | — | — | — | 是 | 是 |
| 资源重打包 | — | — | — | — | — | 是 | 是 |
| 规整护甲包 | 是 | 是 | — | — | — | 是 | 是 |
| 头盔合并（导出） | 是 | — | — | — | — | 是 | 是 |
| 头盔合并（临时） | 是 | — | — | — | **是** | — | 是（关闭） |
| 跨护甲 | 是 | 是（部位事实） | — | — | — | 是 | 是 |
| 转素体 | 是 | 是 | **是** | — | — | 是 | **部分** |
| 装饰包 | 是 | — | — | — | — | 是 | 是 |
| 批量装饰 | 是 | — | — | — | — | 是 | 是 |
| 编辑装饰 | — | — | — | **是** | **是** | — | 是 |
| 材质打包 | — | — | — | — | — | 是 | 是 |
| VPK 导入 | 是 | — | — | **是**（导入） | — | 是 | 是 |
| VPK 换甲 | 是 | — | — | — | 是 | — | 是（再换一次） |
| VPK 组件烘焙 | — | — | — | — | 是 | — | 是 |
| 护甲映射 | 是 | — | — | — | — | **游戏目录** | 是 |

**只有 5 个操作会写库内状态或产物**：头盔合并（临时）、编辑装饰、VPK 导入、VPK 换甲、VPK 组件烘焙。
其余全部只写库外新目录。

---

## 6. 对上一轮融合设计的修正

上一轮我按"改自己 / 造一个新的 / 看"三分。拆细之后要修正两点：

### 6.1 "造"里其实有两个子类

| 子类 | 成员 | 特征 |
|---|---|---|
| **纯派生**（只读 + 写新目录） | 修复、重打包、规整、头盔合并导出、跨护甲、装饰包、批量装饰、材质包 | 可以随便试，无副作用 |
| **派生 + 登记**（还要写别处） | 转素体（+注册表）、VPK 导入（+库） | 会产生"删掉输出目录也清不掉"的残留 |

融合时第二类必须**明确告知副作用**，不能和第一类共用同一个"生成"按钮而不加区分。

### 6.2 护甲映射和 VPK 不属于"Mod 页"

| 子系统 | 归属 |
|---|---|
| 护甲映射（含素体编辑） | 全局页面，因为它改游戏注册表 |
| VPK 导入 | 导入流程的一部分，不是 Mod 页的操作 |
| VPK 换甲 + 组件烘焙 | Mod 页，但**只在 VPK 版 Mod 上出现** |

所以 Mod 页的"派生"分区里，**最多只有 9 个类型**（不含护甲映射），
而且对 VPK 版 Mod 要整体替换成 VPK 卡。

### 6.3 修正后的"派生"骨架

```
目标类型：[替换其他护甲] [头盔合并] [装饰包] [批量装饰] [材质包]
          [规整护甲包] [修复失效 Unit] [资源重打包] [转为自定义素体 ⚠]

（选定后展开）
来源：<当前 Mod，只读>
目标：<按类型变化——护甲多选 / 主体多选 / 材质多选 / 无>
预览：<按类型变化——映射表 / 影响面 / 材质清单>
输出：<统一：默认 Output/<Mod名>+<时间戳>+<动作>/，可改>
副作用：<⚠ 转为自定义素体会同时写护甲注册表；其余只写目录>
[生成]  [生成并导入]
```

---

## 7. 需要你定的两件事

1. **「转素体」算"造"还是"映射素材"？** 它同时写库外目录和注册表，两种归类都说得通，
   但决定了它放在 Mod 页还是护甲映射页。
2. **VPK 版 Mod 的页面**：是把"派生"分区整体替换成 VPK 卡，还是两者并存但 VPK 卡置顶？
   现在是整体替换（`ShowPatchSettingsCard => CanUsePatchTools && !IsVpkMod`）。

---

## 8. 附：上一轮"命名统一"漏掉的四处（本轮发现）

上一轮我声称"全部收敛到 `ArmorStructure`"，实际上**漏了四处字符串字面量**。
漏的原因很具体：我用 `rg -n 'Pelvis|"Head"'` 验证，而 PowerShell 5.1 在把参数传给原生命令时
**会吞掉内嵌的双引号**，实际执行的是 `rg -n 'Pelvis|Head'`——于是 `HeaderSize`、`Head1` 之类的
噪声把真正的命中淹没了，我误判成"没有残留"。正确的写法是 `\x22Head\x22`，或者直接用 `pwsh`（7.x）。

| 位置 | 残留 | 影响 |
|---|---|---|
| `ArmorNormalizationService.cs:215` | `Roles = ["Head", "Torso", "LeftArm", "RightArm", "Pelvis"]` | 规整护甲包的诊断/计划里显示 "Head" / "Pelvis" |
| `ArmorNormalizationService.cs:227` | `RoleName(...)` 返回 "Head" / "Pelvis" | 同上 |
| `DecorationPlanningDefaults.cs:29` | 返回 `"Head"` 或 `"Torso"` | 装饰目标部位 |
| `DecorationPlanPageViewModel.cs:52` | `TargetParts = ["Head", ...]` | 装饰页的"目标部位"下拉显示 **Head** |
| `BatchDecorationPlanPageViewModel.cs:328` | 同上 | 批量装饰页同上 |

`RoleMatches` 本身是对的（`"Head" => kind == UnitMeshPartKind.Helmet`），所以**功能没坏，只有显示不一致**。

其余命中都是**故意的**：
- `CliDecorationEditPlanArtifacts.cs:170` 的 `["Head", "Pelvis", "Hip", "Accessory"]` 是旧规格别名，保留兼容
- `CliSourceConvert.cs:37` 的 `"Hip"` 是**游戏侧字符串**（写进 unit 的 `CustomizationInfo`），不能改
- `SourceToHd2RigMap` / `VpkImportPlanner` 里的 `Pelvis` / `Head1` 是 **Source 引擎骨骼名**，与护甲部位无关
- 测试里的 `"Head"` 大多是游戏数据或测试夹具

**建议**：这五处改成 `ArmorStructure.Name(...)` / `Display(...)`，属于纯文案修正，
风险为零，但会改变装饰页下拉的选项文本（"Head" → "头盔" 或 "helmet"），需要顺手确认规格文件兼容。