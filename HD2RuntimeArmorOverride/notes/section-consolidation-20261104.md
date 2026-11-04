# section 主干化设计（2026-11-04 v2）

> **v2 说明**：v1 把"装饰"当成要**保留**的机制，结论是"section 只能减、加几何留在生成期、
> 两者不该合并"。真需求相反 —— **装饰要变形态**：它应该是"把后置 patch 的独立 unit 吸收成
> 命名 section 组"的一次性升级动作，不是一个长期存在的运行时机制。
> v1 里经核实仍然成立的调查事实、硬约束、以及 astra 审出的工程要求，全部保留在本文。
>
> 三份线别调查报告在 `HD2RuntimeArmorOverride/notes/section-survey-20261104/`。

---

## 0 一句话

**一个 Mod = 7 个标准槽位（每槽按身形最多 2 个 unit）+ 一张 section 表 + 一张组表。**
所有"变体"都是组，所有"切换"都是组开关。装饰、option 里的独立 unit、护甲级掩码、
运行时 Lua —— 这些末梢要么被吸收成主干上的一次性动作，要么剪掉。

**更准确的说法（2026-11-04 用户确认）：这次加强等于让 legacy Mod 走上 VPK 那条同一条管线。**
不是"给 legacy 加一个 section 编辑器"，而是**取消"两种 Mod"这件事** ——
今天代码里靠 `有 sections.json` 区分"是不是 VPK 版"，标准化之后这个判据自然变成
"是不是标准化过的 Mod"，于是组件树、目标护甲、头身合并、素体登记全部自动对 legacy 生效，
一行判据都不用改。

---

## 1 主干模型

```
Mod
 ├─ 7 个标准槽位          Helmet / Torso / Hip / LeftArm / RightArm / LeftLeg / RightLeg
 │    └─ 每槽按身形一个 unit（Any 的只写一份，Slim/Stocky 分开）
 │    └─ section        每块几何一个，带名字（源材质名 / 后置包名 / 用户命名）
 ├─ 组表                 section 组 = 有名字的一组 (unit, section)
 │    ├─ 猫耳    → Torso#3
 │    ├─ 披风    → Torso#7, Torso#8
 │    └─ 外套    → Torso#11, Hip#2
 └─ 隐藏状态             哪些 section 不画（唯一的权威数据）
```

三条不变量：

1. **一个 Mod 只占 7 个标准槽位**，其余槽位（**只有 `LeftShoulder` / `RightShoulder` 两个**，外加隐藏壳）一律不写或写空。
   > 本体实测：`HelldiverCustomizationSlot_*` 一共 9 个槽，7 标准槽之外就这两个；**没有 Cape、没有护胸板这类独立槽位** —— 胸甲是 Torso 的一部分，披风不在 customization 槽位体系里。
2. **所有可切换的东西都表达成组**，不存在"unit 级替换"这种第二套机制。
3. **组、开关、树是同一份数据的三个视图** —— 不是三份状态互相同步。

用户能做的：新增组（勾几个 section → 命名）、删除组（几何仍在，section 恢复默认可见）、
重命名组、开关组。

---

### 1.1 现状（实测；**必须用 `-LiteralPath` 数**，库里有目录名带 `[` `]`）

| 项 | 数量 | 说明 |
|---|---:|---|
| Mod 目录 | **133** | = 132 个 VPK 派生 + 1 个 legacy |
| 有转换报告 | 132 | 那 1 个 legacy 没有报告 |
| 含 unit 的模型 Mod | **11** | |
| 其中有 `sections.json` | **11** | **模型类 100% 覆盖，没有缺口** |
| 有 `components.json`（开关/组定义） | 9 | 缺的 2 个是 `[huahua]Chiyu公式服` 与 `l4n 信浓` |
| 有 `custom-model.json`（素体身份） | 11 | |
| 有 `wardrobe.json`（隐藏状态） | 10 | |
| 0 unit / 0 材质 / 1~2 贴图 | 98 | 贴图覆盖包，见 §2.4 |
| 0 unit / 材质 > 0 | 23 | 材质包 |
| **legacy 形态（无 `vpk2patch/`，只有 patch 文件）** | **1** | `【材质版】隐藏物品合集 (1)-隐藏大部分披风`，且它是材质类，不是多 unit 模型 |

> ⚠️ **这条对排期有直接影响**：本机**没有**一个「多 unit 槽位占满」的 legacy 模型 Mod 可以当拆件样本。
> U3 的验证数据必须自己造 —— 最省事的做法是拿**游戏本体的一件护甲 patch** 当 donor 侧
> （它天然是多槽位占满的），配上我们自己的 7 unit 产物当主体。

### 1.2 这不是"加强 legacy"，是多了一个新品类

**2026-11-04 用户定调**：这是一个**新功能**，不是对 legacy 模组的加强 ——
把 legacy 模组变成「受管理器管理的、section 级可自定义的模组」。

于是「Mod 只有两种」：

| 种类 | 含义 | 产物 |
|---|---|---|
| **`legacy`**（旧版模组） | 用户丢进来的原始 patch / VPK，管理器只负责部署 | 一堆 patch 文件 |
| **`sectional`**（分件模组） | 由管理器拆件产出的、每块几何都可寻址可命名的模组 | 7 unit + section 表 + 组表 + 素体身份 |

VPK 转出来的东西**本来就是 sectional**，只是以前没有一个名字把它和 legacy 分开 ——
今天靠"有没有 `sections.json`"隐式区分，靠"VPK 动作卡"隐式承载。
这次把这件事**显式化**：`vpk` 这个词从品类名里退场（它只是 sectional 的一种来源）。

### 1.3 `kind` 轴必须拆成两个轴

现在 `library.json` 的 `kind` 是 `standard | option | decoration`，它把
**"这是什么"** 和 **"它跟谁有关系"** 混在一个轴里。不拆的话 `option` 与 `sectional` 会打架
—— 一个分件模组当然也可以被挂成别人的选项。

| 轴 | 取值 | 回答什么 |
|---|---|---|
| **种类** | `legacy` \| `sectional` | 它是什么 |
| **关系** | 独立 \| 宿主选项（hosted） | 它挂在谁身上 |

库里 121 个 `option` 只是"被挂载的 legacy"，拆轴之后它们的种类仍然是 `legacy`，
关系是"宿主选项" —— 语义一下就对上了。

### 1.4 命名（建议，待拍板）

| 代号 | 中文 | 评价 |
|---|---|---|
| **`sectional`** | **分件模组** | ✅ 推荐：与 `legacy`（整包）构成**粒度**词对，直观；不撞现有任何词；`hd2mod mod split` 做动词也顺 |
| `block` | 区块模组 | 直译 section，但"区块"在中文里更像地形/数据块，不像"可开关的部件" |
| `assembled` | 装配模组 | 仓库里"装配"已被 `asm_*` 那套占用，会撞 |

选 `sectional` 的理由：它和 `legacy` 在**同一个语义维度**上成对（整包 ↔ 分件），
而"标准化""装配"都是在描述**过程**，不是在描述**它是什么**。

配套命名：

```
代码     ModKind.Legacy | ModKind.Sectional
JSON     "kind": "legacy" | "sectional"
命令     hd2mod mod split <Mod> [--apply]     ← 拆件（默认预演）
         hd2mod mod split --all
界面     旧版模组 / 分件模组
产物目录 分件模组的 payload 目录（现在叫 vpk2patch，建议改名但需迁移，见 §9.5）
```

---

## 2 为什么这条路技术上成立（已核实，不是估计）

### 2.1 「7 个槽位 / 7 个 unit」已经是现有产物的形状

实测 11 个 VPK 模型 Mod 的 `sections.json`：

| Mod | units | 全 Mod 节数 | 单 unit 最多 |
|---|---:|---:|---:|
| lunalice_bill | 7 | 98 | 25 |
| Lilium_Sancta_Bill | 7 | 67 | 18 |
| MourningVeil_Bill | 7 | 64 | 15 |
| serenade_bill | 7 | 60 | 17 |
| l4n 信浓 | 7 | 52 | 10 |
| Cow_QiPao_Milltina_Bill | 7 | 48 | 12 |
| Manuka_LaFiore_Bill | 7 | 47 | 9 |
| Manuka_JABIS_Bill | 7 | 45 | 8 |
| 旗袍chiffon | 7 | 24 | 6 |
| [huahua]Chiyu公式服 | 7 | 22 | 8 |
| Elise_Bill | 7 | 18 | 6 |

**全部 11 个都是 7 个 unit、全部 `Undergarment/Any`、正好覆盖 7 个标准槽。**
> ⚠️ 这里曾写「游戏本体躯干实测 52 节」，**已撤回**。本体护甲是 **1 节/LOD**（一件护甲只用 1 个材质），
> 全量扫 5472 个 unit 最大只有 16 节（且是载具）。52 是 mod 侧 / 运行期探针的数字 ——
> 它证明的是「引擎能寻址 ≥52 段」，不是「本体有 52 节」。详见 `sectional-u0-findings-20261104.md` §4。

⇒ "一个 unit 装几十个 section、靠开关选"**在 VPK 产物里已经是现状**（L2/L3）。
但**本体从来不用多段换装**（护甲 1 节/LOD），所以「原生状态」这个说法只在「我们自己的产物」范围内成立。

---

### 2.2 追加时 section 边界能不能保住（这是全案的承重结论，要说准）

`CanonicalDecorationUnitAppender` 末尾有一句无条件的 `MergeSectionsByMaterialSlot`
（`CanonicalDecorationUnitAppender.cs:189-197`），按 `(MaterialIndex, MaterialSlotId)` 分组把
三角形并起来。看起来像"两个部件会合成一个 draw call、再也分不开"。实际机制是这样的：

- 装饰路径的 provenance 带 `UsesTargetUnitMaterialSlotLookup: true` + `PreferMeshLocalSlotReuse: true`
  （`CanonicalDecorationUnitAppender.cs:178-180`），所以走的是
  `ResolveSdkTargetSlot(localTargetSlotsByMaterial, materialId, occurrence, …)`
  （`CanonicalUnitMaterialLayoutCompiler.cs:92-95`）。
- `localTargetSlotsByMaterial` = **目标 mesh 自己**的"材质 id → 槽数组"。
- `occurrence` = 本 mesh 内同一个 `materialId` 第几次出现（`:84-89`）。
- 若 `occurrence < 槽数组长度` → **复用目标的槽**；否则 `AllocateSlot(identity)`
  （`:146-148`），而 `identity` 里含 `SectionIndex`（`:90,203-204`）⇒ **每个 section 各得一个唯一槽**。

于是三种情况：

| 情况 | 结果 |
|---|---|
| 两块几何材质不同 | 槽不同 ⇒ 段不合并 ⇒ **可独立开关** ✅ |
| 两块几何材质相同、且目标自己没有这个材质 | 第 0 块走 `AllocateSlot`，第 1 块 `occurrence=1` 超出槽数组长度也走 `AllocateSlot` ⇒ 槽不同 ⇒ 段不合并 ✅ |
| **两块几何材质相同、且目标自己也有这个材质** | 第 0 块 `occurrence=0` **复用目标那个槽** ⇒ 与目标那一段合并 ❌ |

**⇒ 结论：跨来源追加基本不会丢 section 边界；唯一的破口是"donor 与目标共用同一个材质资源"。**

这不是设计问题，是**必须实测确认的边界**，而且要准备规避手段：
强制每段独占一个槽（多个槽可以指向同一个 MaterialId —— `bindingsBySlot[slot] = materialId`
本身就是这个形状），代价是材质槽数变多。这条要放进 §3 的验证清单。

### 2.3 三种"后置包"其实是同一件事的三个特例

| 后置包提供什么 | 现在怎么处理 | 主干化之后 |
|---|---|---|
| `$no_draw` 材质 | 组（关掉现有几何）—— **已支持**，9 个 Mod 共 6–30 个开关 | 不变 |
| 贴图 / 材质 | option（资源 id 覆盖）—— **已支持**，121 个 | 不变 |
| **独立 unit**（整套替换主体某个 unit） | **无路可走**（只有老的 unit 级映射，已死） | **组（预装几何 + 按组开关）** ← 新增 |

第三种就是用户要的。它不需要新机制 —— 它只是把前两种已经在用的"组"概念，
扩展到"组里的几何来自后置 patch 而不是主体"。

### 2.4 那 98 个「0 材质」包不是坏包（**纠正一条旧结论**）

`backlog-20261104.md` 曾记着"只换贴图的颜色变体包转出来一个材质都不含，装上什么都不会变"，
把 `Lunalice基础材质C1/D1~D4/E1~E3` 等列成缺口。**这条是误判，已撤回。**

实测 98 个包是 `0 Unit / 0 材质 / 1~2 贴图`，而它们的贴图 id 与基础材质包**完全一致**：

```
lunalice基础材质a1主体   发出 white  = 0xb25d6ffd2612dba8
lunalice基础材质d1发色白  发出 white  = 0xb25d6ffd2612dba8   ← 同一个 id，不同内容
lunalice基础材质d1发色白  发出 shinano_hair_ear = 0xb5040b92a97479e7（同上，id 一致）
【材质】奶牛旗袍…基础材质 发出 nail   = 0xb3423bad0a6178fd
【材质】奶牛旗袍…美甲 白   发出 nail   = 0xb3423bad0a6178fd   ← 7 个美甲变体全是这个 id
```

`vpk2patch-contract-20261016.md:212-219` 的发布脚本验收条件第 3 条正是这个机制。

**结论分三级，不能一步跳到"能用"：**

| 级别 | 内容 | 状态 |
|---|---|---|
| 已确认 | `0 Unit / 0 材质` **不是失败判据**；纯贴图产物是合法的选项形态 | ✅ 实测 |
| 静态可验证 | 产物提供的资源 id 确实被宿主当前材质引用；依赖闭包完整 | ⬜ 未做 |
| 实机已验证 | 指定组合 + 部署顺序 + 当前游戏版本下外观确实变了 | ⬜ 未做 |

**顺带查出一个真问题**：`Cow_QiPao_Milltina_Bill` 上 68 个选项**全部 `enabled=true`**，
而 `lunalice_bill` 是 12 个全开（正好每组一个）。同一资源 id 只能有一个提供者生效，
所以"68 个全开"里实际只有一个在起作用 —— 这正是 §5 给组加 `exclusiveGroup` 的直接原因。
但**先建"按资源 id 的提供者账本"再定策略**：重叠关系不是简单互斥
（A 提供 `{x}`、B 提供 `{x,y}`、C 提供 `{y}` 时 A 与 C 可以共存），
把连通分量做成单选组会错误禁止合法组合。

---

## 3 U0 已核查：结论与残留风险

完整结论见 `sectional-u0-findings-20261104.md`（经 astra 审核后修订，每条带证据等级）。
**要点：U0 只能标「离线结构核查完成」，不能标「拆件可行性已确认」。**

| 问题 | 结论 | 等级 |
|---|---|---|
| 共用材质会不会并段 | **会**；破口是「每个 occurrence 落在目标槽数组长度内的 section」，且并段收的是整张最终 mesh（连目标自己原有的同槽段也会被并） | L3 |
| 跨槽搬几何位置会不会变 | 代码上「有条件的坐标补偿成立」，**运行时未验证** | L1 |
| 单 unit 能装多少节 | 我方产物里**观察到** 25 段；本体数据口径未对齐，不能当上界 | L2/L3 |
| 组数 / 界面 | 结构上不构成障碍；**性能未测** | L1 |

**三个已知的「静默少东西」入口**（都不报错，都只在游戏里表现为「某块没了」）：

1. **同键并段** —— donor 的 section 与目标共用同一 `MaterialId` 时边界丢失；
2. **LOD 不齐跳过** —— `CanonicalCrossArmorOrchestrator.cs:497-512` 在 donor 缺对应 LOD 时直接 `continue`，合并仍报成功（只有全部没有共同 LOD 才失败）；
3. **深度代理遮挡** —— 隐藏 donor 槽位后它的 `lod=-1` depth-only 代理仍在原处，会按深度剔除新几何。

**外加一个与拆件无关的现存缺陷**：`CullingBounds` 从不重算（`UnitMeshWriter.cs:46-56` 只原样写回；`UnitAuthor.cs:275-282` 注释明说顶点出盒就会被剔除）。
装饰合并 / 头盔合并 / 多源合并三条在役路径都在这个缺陷下运行。

---

## 4 一条管线 + 一个"重建来源"抽象

**不要"给 legacy 加一套功能"。要让两者走同一条管线。** 今天代码里之所以有"两种 Mod"，
是因为**重建来源不同**：VPK 版有源 `.vpk`，legacy 版只有已经做好的 patch。除此之外
产物形状、编辑方式、换甲、头身合并、素体登记**全部可以一样**。

### 4.1 统一后的 Mod 形态

```
<mod>/
  source/                  ← 重建来源（唯一的差异点，二者选一）
      *.vpk                    VPK 版
      *.patch_* 快照           legacy 版
  vpk2patch/               ← 标准化产物（两者形状完全相同）
      <archive>.patch_0 (+ .gpu_resources / .stream)
      sections.json            7 unit、每节带来源标记与生成代
      components.json          组表（§5）
      custom-model.json        素体身份（两者都写，legacy 也有一套 7 unit 素体）
  Overwrite/               ← 换目标护甲后的变体（同样 7 unit）
  wardrobe.json            ← 隐藏状态 + 目标护甲 + 头身合并
```

`vpk2patch` 这个名字是历史包袱（legacy Mod 里它不叫"vpk 转 patch"），但改名要迁移，
**建议先留着，只在文档里写明它是"标准化产物目录"**。

### 4.2 由"统一管线"自动发生的事（不需要新代码）

| 能力 | 为什么自动成立 |
|---|---|
| 组件树 / 组表显示 | 判据是 `有 sections.json`（`VpkModWardrobeService.IsVpkMod`），legacy 标准化后自然成立 |
| 换目标护甲（`vpk rekey`） | 它吃的是 payload（7 unit）+ 目标护甲 unit 表，不关心 payload 从哪来 |
| 头身合并（`--head-merge`） | 同上 |
| 素体登记 / 挂到护甲映射页 | legacy 标准化时写一份 `custom-model.json` 就够 |
| section 组开关 / 就地烘焙 | 完全同一条路 |

### 4.3 唯一要新增的东西：把"重构建"抽成来源无关

`VpkRebuildService` 现在写死了"用 `.vpk` 重跑 vpk2patch"。抽成：

```
IModRebuildSource
  ├─ VpkSource        : 读 *.vpk  → 跑 vpk2patch
  └─ LegacySource     : 读 *.patch_* 快照 → 跑 split（多 unit 压成 7 unit）
                       两者输出同一个 payload 形状
```

这样"重构建"按钮对两种 Mod 都成立，只是背后读的东西不同 —— 而用户看到的、后续能吃到的
能力完全一致。

### 4.4 存量升级器（一次性）

`hd2mod mod split <Mod> [--apply]`：
输入一个已经导入的老 Mod（多 unit、unit 级替换），输出主干化产物。
- 默认**预演**：列出"会并哪些 unit 进哪个槽、会生成哪些组、会隐藏哪些 unit、预计节数"。
- `--apply` 才动手，且先备份（沿用 `VpkPayloadSwap` 的"先备份再删、保留文件不删"口径）。
- 可回滚：升级前的产物整体留一份。
- **它和 `LegacySource` 是同一份实现**，只是入口不同（一个对存量、一个对新导入）。

**归属槽的规则**（需要拍板，见 §9）：按 donor unit 自己的 `slot` 语义映射到 7 槽之一；
映射不到（Shoulder / Cape 这类）→ 并进 Torso。

### 4.5 一条必须说清的边界：标准化只适用于「整套替换」型

标准化把 Mod 收缩成 7 个标准槽位，意味着它**不再提供 `LeftShoulder` / `RightShoulder` 这两个槽位的内容**（本体 9 个 customization 槽里，7 标准槽之外就这两个；没有 Cape / 护胸板这类独立槽）。
这不是缺陷，是取舍，而且正好划出主干与末梢的分界：

| 类型 | 例子 | 处理 |
|---|---|---|
| **整套替换**（多槽位占满，替换整套外观） | VPK 转出来的素体、用户说的 legacy 大包 | **标准化 → 7 unit + 组**（主干） |
| **单件替换**（只给护肩、只给一件） | 单件护甲 Mod | **不标准化**，留在 unit 级映射（`wardrobe.links`） |

所以 `wardrobe.links`（"护甲 A 穿出 B 的样子"）**必须保留** —— 它是**跨 Mod**的 unit 级映射，
与 Mod **内部**的 section 组是两层不同的事，不该合并。这也回答了 v1 留下的
"`pieceOverrides` 该映射成什么"：它属于单件替换那一层，而那一层的主干是 unit 级映射，
不是 section 组。

---

## 5 组的数据形态

不新增文件 —— **把 `components.json` 从"开关表"升级成"组表"**。它现在就是
"名字 + 一组 (unit, sections)"，只是成员来源被限定成"源侧 `$no_draw` 推出来的"。

```
{
  "schema": "hd2mod.section-groups/1",
  "groups": [
    {
      "id": "g-cat-ears",              // 稳定 id，用户改名不变
      "name": "猫耳",
      "source": "noDraw" | "donorUnit" | "manual",
      "exclusiveGroup": "头部配件",     // 同组内只能开一个；null = 不互斥
      "defaultEnabled": false,          // 预装组默认关（否则所有变体同时出现）
      "members": [ { "unit": "af43…", "section": 3 } ]
    }
  ]
}
```

- **唯一的权威数据仍然是"哪些 section 不画"**（`wardrobe.json` 的 `disabled`）。
  组只是它的**视图定义** —— 开一个组 = 把它的 members 从 `disabled` 里移除。
- `exclusiveGroup` 一次解决两件事：预装变体（同一槽位多个变体只能选一个）、
  以及贴图覆盖型选项的冲突（v1 §7.4 悬而未决的那个问题）。
- `source` 是给界面用的：用户需要知道"这个组是自动识别出来的，还是我自己建的"。

---

## 6 身份稳定（组要在重建后活下来）

组存 `(unit, sectionIndex)` **不够**。重建一次下标就可能变，用户辛苦建的组会指向别的几何。
这正是 v1 §3.2 那段（astra 审核后重写的）要解决的问题，现在它是核心而不是附属：

- **`sections.json` 的每个 section 记来源**：`sourceUnit` + `sourceSlotId` + `materialId` +
  `triangles`，以及它在来源侧的 `sectionIndex`。
- **组的成员存来源身份**，不存产物下标；读取时按来源身份解析成当前下标。
  解析不到 → 标 stale 并**阻断**（不是只给提示），界面明说"这块在当前产物里找不到了"。
- **生成代**：`sections.json` 带 `generation`（来源版本 + 目标护甲 + 合并选项 + 生成器版本 +
  布局指纹）。组的解析只在同代内有效。
- **不校验可变 patch 的整文件 SHA**（这条硬约束不用破）——校验的是不含可变 dword 的结构指纹。

---

## 7 修剪清单（末梢）

| 对象 | 处理 | 理由 |
|---|---|---|
| `decoration.json` / 装饰包 / `decoration-activations.json` / `DecorationActivationStore` / `DecorationHostRebuildService` / `HostDerivedOverwriteService` / 两个装饰页面 | **被"吸收成组"取代** | 现网 0 个装饰；它的能力（把 donor 几何搬进宿主）保留，机制（独立包 + 启用状态 + 重建宿主）不需要了 |
| option 里"提供独立 unit"的那一类 | **升级时吸收成组** | 只保留"提供贴图/材质"的 option |
| 护甲级 section 掩码（`wardrobe.workspace.json` / `wardrobe.masks` / `WardrobeMaskCompiler` / `WardrobeMaskProjection` / `armor_section_*.lua` / `armor_wardrobe_masks.lua` / CLI `wardrobe compile\|project\|validate`） | **按层退场**（停分发 → CLI 弃用一个周期 → Core 归档） | 运行时 Lua 因缺 `ffi` 加载即退出；管理器零写入点；权威数据无编辑器。**但"按护甲保存隐藏策略"这个能力不判死**，它将来可以改成"护甲级策略 → 生成该护甲的烘焙产物" |
| `pieceOverrides` / `targetModes` / `CompositionMode` | **字段留、编辑入口不恢复、校验从致命降为警告** | 现网 0 条、无运行时消费；但坏数据现在会让整份工作区读不出来，一个没人消费的字段不该有这种权力 |
| 组件编辑器（已删） | 由**组编辑器**取代 | 只剩孤儿 XAML 要清 |
| 头身合并（VPK / 常规两份） | **留**，但抽共用的"候选识别 + 物理可行性判定" | 两者对"层"的排序方向**相反**（`CliVpkRekey.cs:844-852` vs `HelmetToArmorMergeService.cs:706-712`）；且光统一排序不够，因为两者对骨骼绑定、unit 槽位、容量限制的要求本来就不同 —— 得出不同结果可以合法，但要说得出理由 |
| `WardrobeSectionSource` / `RequestUnitSectionStructureAsync` / `vpk2patch --unit-ids` / `DecorationPatchSnapshotService` / `.bin` 旧路线 / `ArmorPieceEditorTemplate` | **直接删** | 逐条已确认无在役调用者 |

**明确保留、不算末梢的两样**（见 §4.5）：

- `wardrobe.links`（"护甲 A 穿出 B 的样子"）：它是**跨 Mod** 的 unit 级映射，
  与 Mod **内部**的 section 组是两层不同的事。
- 头身合并的两份实现。

---

## 8 分批（**顺序经 astra 审核后重排**）

| 批 | 内容 | 判据 |
|---|---|---|
| **V0 实机垂直切片（架构门）** | 见 `sectional-u0-findings-20261104.md` §6.1：样本要同时含宿主同槽两段 + 同材质 donor + 不同骨骼权重 + 多 LOD；做跨槽搬运 + donor 隐藏 + 来源映射 + 单段归零，进游戏验；**再重建一次验开关不漂移** | 每段只影响自己 / 动画不漂 / 近远不丢件 / 代理影响按四格隔离。**任何一步失败 ⇒ 禁止宣布「7 个物理 unit 的通用拆件方案成立」** |
| **V1 容量梯度** | 用**候选最终编码**造（不是脱离方案的 100 段文件），梯度 16/25/32/64/100/150，真实骨骼规模 | 每 mesh 段数、每 LOD 槽/remap 数、骨骼集合大小、顶点与索引量、**真正提交的段数**、渲染 pass 成本、多角色同屏负载 |
| **V2 可寻址契约** | 先测**最便宜的**那条：开关跳过 `MergeSectionsByMaterialSlot`；失败再测唯一槽路线。覆盖宿主 + donor，全链路 provenance，覆盖权威 `disabled` 迁移 | 承诺可独立关闭的来源其产物段集合不得交叠；关闭任一成员不影响其他成员；重建后开关不漂移 |
| **U1 组模型** | §5 组表格式 + §6 来源身份（不可变来源快照标识，材质/三角形只作校验）+ 生成代；`components.json` 升级 | 老 `components.json` 能原样读进来（`source: noDraw`），不需要迁移；界面能区分**独立 / 耦合 / 失效**三态 |
| **U2 组编辑器** | 详情页：树 + 组列表 + 新建/删除/重命名/开关；组/开关/树三视图统一 | 建组 → 开关 → 重建 → 组还在；改名不影响开关状态；耦合组**不显示**两个貌似独立的复选框 |
| **U3 统一管线（拆件）** | §4.3 `IModRebuildSource` 抽出 + §4.4 `split`（= `LegacySource` 的存量入口） | 一个多 unit 的老 Mod 升级后：7 unit、组表齐、其余槽全隐藏、**重构建按钮可用**；预演与真做一致；**重构建一次产物与升级时逐字节一致** |
| **U4 生效成本** | 就地烘焙（不再重跑整条换甲）+ 勾选即保存 | 勾一下 3 秒内 patch 变了；隐藏→恢复后字节复原；切页不丢 |
| **U5 修剪** | §7 的退场与删除 | 测试全绿；库能正常打开；无孤儿 XAML / 死命令 |

**为什么 V0 在最前**：v1 把「位置已确认不变、只剩深度代理要看图」当排期依据，
但那只证明了「有条件的坐标补偿成立」。§3 列出的三个「静默少东西」入口，
**任何一条都足以推翻「压成 7 个物理 unit」这个形态**。把最可能推翻方案的实验排到投入最大之后，
是上一版最大的判断错误。

**W1（`CullingBounds`）分两段**：先做实现 + 诊断、只在实验/拆件路径启用；三条在役路径默认行为不动，
逐路径用固定样本验证后再推广。第一版取保守策略（保留原有效范围 + 补入来源范围与余量），
**不要直接照抄静态顶点紧盒算法**（会缩掉动画预留范围）。


---

## 9 待确认

1. **归属槽规则**：donor unit 的 `slot` 映射到 7 槽之一，映射不到（Shoulder / Cape）并进 Torso ——
   还是"所有非 7 槽的几何一律并进 Torso"？我倾向后者：规则简单、可预测，而且几何位置由骨骼决定，
   槽位只是分类。但如果某些护甲件的可见性依赖槽位，就得按前者。
2. **组的默认粒度**：一个后置包 = 一个组，还是一个后置包里的每个材质 = 一个组？
   （后者更细，但 30 个开关的包会变成 30 个组。）
3. **预装组默认关还是开**：我倾向默认关（否则所有变体同时出现），但那样"装上去什么都没变"，
   用户可能以为坏了 —— 需要在界面上把"有 N 个组待选"说清楚。
4. **目标形态是「7 个 unit」还是「7 槽 × 身形（最多 14 个 unit）」？**
   本体一套护甲是每槽 × 每身形各一个 unit（Slim/Stocky 是两个不同 unit）。
   如果 legacy mod 提供两套身形，压进同一个 `body=Any` unit 会让两套几何同时出现。
   我倾向「7 槽 × 身形」，合并规则是**只在同一 `(slot, body)` 内合并**。这条要在 V0 之前定。
5. **`U0` 的极限样本用哪件护甲**：现在只需一件带 `LeftShoulder` 的护甲（本体有 162 个 LeftShoulder unit），不再需要「护胸板」—— 那个槽位不存在。
5. **`vpk2patch` 目录要不要改名**：叫 `payload` / `normalized` 更准，但改名要迁移。
   我建议先不改，只在文档与界面文案里说明它是"标准化产物目录"。
6. **单件替换型 Mod 要不要保留**（§4.5）：如果保留，它走 `wardrobe.links` 那条路，
   不参与 section 组；如果不要，产品就只剩"整套替换"一种 Mod，更简单但少一类能力。
