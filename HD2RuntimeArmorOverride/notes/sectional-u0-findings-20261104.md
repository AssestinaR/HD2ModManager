# U0 事实核查结论（sectional 拆件方案，2026-11-04）

> 设计稿：`section-consolidation-20261104.md`。本文只回答"这条路到底成不成立"，
> 不改设计、不写代码。原始报告在 `notes/sectional-survey-20261104/`。
>
> 方法：A 线用可运行探针 `E:\tmp\secprobe` 端到端跑产品代码（`TryAppendLodFamily` → 出 TOC 字节
> → 用 `UnitMeshReader` 重读回来数 section），是**实测**不是推断；
> C 线是代码级核查 + 在役生产路径取证；B 线是游戏本体扫描（Python 解析 `.nxa`）；
> 界面一项是我自查。仓库改动为零。

---

## 0 四问四答

| # | 问题 | 答案 | 证据强度 |
|---|---|---|---|
| 1 | donor 与目标共用同一材质时会不会并成一段？ | **会**，而且范围比设计稿写的大 | ✅ 端到端实测 |
| 2 | 跨槽搬几何，位置会不会变？ | **不会** | ✅ 代码 + 在役生产路径 |
| 3 | 单 unit 能装多少节？ | 已证 **25**；本体最大只有 16，**不构成上界**；100+ 未证 | 🟡 实测 + 推断 |
| 4 | 组数 / 界面撑不撑得住？ | 撑得住（默认折叠，150 行不是问题） | ✅ 代码 |

**结论：方案成立，但有一处必须改产品代码，且挖出一个与拆件无关的现存缺陷。**

---

## 1 并段：会，而且是三处，不是一处

### 1.1 实测（每段 2 个三角形，端到端）

| 组 | 目标 unit | donor | 最终段数 | 结果 |
|---|---|---|---:|---|
| ① 对照：材质不同 | 1 段 M1 | 1 段 M2 | **2** | ✅ 边界保住 |
| ② 共用目标材质 | 1 段 M1 | 1 段 M1 | **1** | ❌ 并了（4 tri） |
| ③ 两 donor 共用目标材质 | 1 段 M1 | A:M1, B:M1 | **2** | 🟡 部分并（A 并入，B 独立） |
| ④ 目标没有该材质 | 1 段 M1 | A:M3, B:M3 | **3** | ✅ 全保住 |
| ⑤b 目标两槽同材质、donor 也同材质 | 2 段 M1 | 1 段 M1 | **2** | ❌ donor 并进槽 77 |
| **⑥ 目标自己同槽两段、donor 材质完全不同** | 2 段同槽 77 | 1 段 M2 | **2** | ❌ **目标自己两段被并** |
| ⑦ 同 ⑥ 但 donor 共用该材质 | 2 段同槽 77 | 1 段 M1 | **1** | ❌ 三段全并 |

### 1.2 三条与设计稿不符的结论

1. **破口不是"一个 donor"，而是"每一个 occurrence 落在目标槽数组长度内的 section"。**
   ③ 里只有第一个 donor 被并，第二个反而拿到新槽。所以"会不会并"取决于
   **该材质在目标侧已有几个槽**，不是"是不是同一个材质"这么简单。
2. **并段是整张最终 mesh 范围，不只作用于追加段。** ⑥ 最反直觉：donor 用的是完全不同的材质，
   目标自己原本同槽的两段照样被并成一段。⇒ **任何一次 append 都会重排目标原有的 section 边界。**
   这一条对设计稿 §6「身份稳定」是致命的：组的成员存的是 `(unit, sectionIndex)`，
   而一次 append 就可能让下标整体位移。
3. **规避可行，但必须改产品代码。** 多个 slot 指向同一个 `MaterialId` 完全合法 ——
   实测 ③/④ 里产品代码自己就产出了 `[77→0x111, 2260070994→0x111]`，
   仓库既有测试也断言过 `[(900,0x222),(901,0x222)]`。
   但**只关 `PreferMeshLocalSlotReuse` 没用**（L3：退到 unit 级查找后 `occurrence=0` 仍命中目标槽）。
   真正起作用的是"让这一段的 `PreferredTargetSlotId` 与目标槽不同"，
   而 `BuildMaterialProvenance`（`CanonicalDecorationUnitAppender.cs:178-180`）把这个写死了，
   **现有两个 flag 的任意组合都做不到**。需要一个 `uniqueSlotPerSection` 开关。

### 1.3 代价

- 材质槽 +1/段，`UnitMaterialBinding` +1 条/段（增量很小）。
- **真正贵的是 `BoneInfo.Remaps` 条数 = 槽数**：每个多出来的槽要一份 fake-index 表
  （`groupHashes.Count * 4` 字节）。100 段 = 100 份 remap 表。
- 与"一个 Blender 材质槽 = 一个 section"的 SDK 惯例偏离更远。

### 1.4 顺带否掉一句注释

`CanonicalDecorationUnitAppender.cs:185-188` 说"保留同槽多段会产生 non-SDK section table"。
扫本体 **7132 个 unit / 47358 张 mesh**，其中 **12 张 mesh 存在同 mesh 内多段共用同一
`MaterialSlotId`**，最多一张 **5 段共用 1 槽**（`unit=0x67dc32dca4f02d33 mesh=2 lod=6`）。
⇒ 同槽多段是**引擎真实会产出的形状**，不是 non-SDK。
那句注释的真实理由只可能是"我们自己的 `BoneInfo.SetRemap` 按 material ordinal 建表，
同槽多段会让 remap 与 section 不再一一对应" —— 这是**本地实现的约束**，不是引擎的。
（但这 12 张是静态形状，不能证明"同槽多段可以被单独开关"。）

### 1.5 对拆件方案的影响：正中主场景

会中招需要三个条件同时成立：目标已有该 `MaterialId` 的槽 + donor 有 section 用同一个材质 +
该 section 在这个材质上是"第 k 个、k < 目标槽数"。

| 情形 | 会中招 | 常见度 |
|---|---|---|
| donor 与目标是**同一 Mod 的不同 unit，共用同一批材质** | **会** | **高 —— 正是"一个 legacy 大包拆成 7 槽"的主场景** |
| donor 用 `$no_draw` / 默认材质 | 不会 | 中 |
| donor 自带新材质 | 不会 | 高 |

**产品口径建议**：`uniqueSlotPerSection` 默认**关**（保持与 SDK 惯例一致）；
**`split` / 拆件路径打开它** —— 拆件的目的就是可寻址，多几个槽换边界，值。
并且 `sections.json` 的来源身份必须允许"多个来源段 → 一个产物段"的**一对多**映射，
否则被并掉的段在组里会静默 stale。

---

## 2 位置与遮挡：位置不变，但挖出两个真问题

### 2.1 位置由骨骼决定，槽位不参与

顶点位置 = 顶点 position（mesh-local）× **该 mesh 自己的 `TransformInfo` 矩阵**，再按**骨骼名哈希**蒙皮。
`(slot, pieceType, layer)` 完全不出现在这条算式里。

追加链路已经做了变换补偿：`CanonicalDecorationUnitAppender` 对每个来源算
`SourceToTargetLocal`，`CanonicalAppendMeshAssembler` 真的把顶点过一遍矩阵。
⇒ **只要走这条路，视觉位置不动。** 唯一会变的情形是绕过它直接拷顶点缓冲区。

**在役证据**：`CanonicalCrossArmorOrchestrator.cs:472-553` 的 `MergeSourceUnits` 分支
就是"多个来源 unit 合进同一个输出 unit"（目标格子比来源少时触发），
`:502` 注释写明"按 LOD 序号取，不校验部位族 —— 不同部位的 SemanticInfo 本来就不同"。
**这是发版行为，说明"一个 unit 里装多个部位的几何"引擎侧没有任何问题。**

### 2.2 真正依赖槽位的（引擎按槽位找 unit）

| # | 行为 | 依据 |
|---|---|---|
| 1 | 寻址：件记录四元组 `(itemId, body, slot, pieceType)` → unit | `ArmorStructure.cs:10-16` |
| 2 | 每部位最多 3 个 unit 槽，`pieceType` 就是槽序号 | `ArmorStructure.cs:54-62` |
| 3 | 某部位只有护甲层时"该部位唯一几何"的判定 | `CliVpkRekey.cs:619-624` |
| 4 | 隐藏槽位 = 可见 LOD 全最小化，但 `lod=-1` 深度代理**保留** | `CliPatchPrimitives.cs:524-530` |
| 5 | 隐藏 unit 的 id 必须真实存在，否则引擎缓存加载失败 → 军械库空白崩溃 | `WardrobeHiddenUnitTemplate.cs:11-14` |

其余（`UnitMeshPartKind/Layer` 枚举、承载层"外层优先"策略、`layer` 的文本启发式、
`MeshInfoIndex` 当身份、`IsCullingBody`、`AutoHideUnmappedTargetUnits`、两份相反的层排序）
都只是**我们自己的分类习惯**，引擎不读。

**关键区分**：引擎只按槽位**找到** unit；找到之后 unit 里的几何一律照画，不按槽位做空间过滤。
"把护肩几何塞进 Torso unit"在引擎侧合法，代价只是它不再拥有护肩槽位（设计稿 §4.5 已承认）。

### 2.3 问题一：`CullingBounds` 从不重算（**现存缺陷，与拆件无关**）

`MeshInfo` 里那 7 个 float（min xyz / max xyz / 半径）是引擎剔除用的盒子。
`UnitMeshWriter.cs:46-56` 只把 model 里的值**原样写回**；`UnitAuthor.cs:275-282` 注释明说
**"`CanonicalUnitRebuilder` 不动这几个字段，所以不重算的话留下的是外壳的包围盒；
顶点跑到包围盒外，游戏的剔除就会把网格丢掉"**，Python 契约检查断言 `vertices_outside == 0`。

⇒ **现有 Canonical 追加链路（装饰 / 头盔合并 / 多源合并）全都没重算这个盒子。**
把护肩、披风这类外扩几何并进 Torso 很容易让顶点出盒 → 整块被剔除。
表现为"几何整块消失但游戏不崩"。

**这是 U0 挖出的最大问题，而且它在现有功能里就已经存在，不是拆件引入的。**

### 2.4 问题二：隐藏 donor 槽位后它的 `lod=-1` 深度代理仍在原处

`lod=-1` 的 mesh 是 **depth-only 遮挡壳**（section 的 `MaterialSlotId` 都不在材质表里 ⇒ 没材质 ⇒ 只写深度）。
实测结论（2026-11-01 进游戏对照）：把几何挂在更内层时会被它按深度剔除，
表现成"手套 / 丝袜整段不显示、也不是品红"。

它**不能搬**（`CanonicalDecorationUnitAppender.cs:58` 拒绝 `LodIndex<0` 当宿主；
`LodIndex=-1` 没有 `BoneInfo`，既不能当 donor 也不能当宿主），
也**不能丢**（遮挡语义会变 + 失去合法的 `LOD -1 contract`）。
全仓唯一"连代理一起最小化"的开关是 `MinifyUnmappedCullingMeshes`，**没有任何调用点置 true**。

⇒ 隐藏壳要提供"连深度代理一起最小化"选项，U0 里要 A/B 对比两种隐藏壳。

### 2.5 两个附带事实

- **"隐藏"= 写空壳，不是"不写它"。** 写空壳（可见 LOD 全部压成 1e-4 退化三角形、
  `lod=-1` 代理保留）→ 不缺件、不 T-pose、不掉材质；不写 → 引擎回落原版件继续显示；
  写不存在的 id → 军械库空白崩溃。
- **追加只在同 LOD 内合法。** donor 与宿主必须 LOD 序号一致，且要按 LOD 家族整体重建，
  否则引擎会从"改过的近 LOD"切到"没改的远 LOD"。设计稿 §3.1 没提这条，要补。

---

## 3 容量：游戏本体不构成上界

### 3.1 本体 armor 的逐槽统计（比全语料最大值有用得多）

| 槽位 | unit 数 | 最大 section | 中位数 |
|---|---:|---:|---:|
| Torso | 394 | 10 | 4 |
| Hip | 288 | **13** | 4 |
| LeftArm | 197 | **13** | 7 |
| RightArm | 193 | **13** | 7 |
| LeftShoulder | 162 | 8 | 4 |
| RightShoulder | 160 | 6 | 4 |
| RightLeg | 132 | 10 | 5 |
| LeftLeg | 132 | 10 | 5 |
| Helmet | 99 | 5 | 4 |

全语料（含载具、道具）42 462 个 unit 的总体分布：最大 **16**、p99 5、p95 3、中位数 1，
**>20 段的 unit 一个都没有**。

### 3.2 我们自己的产物

`lunalice_bill`（unitprobe 实测）：Helmet **22** 段、Torso **25** 段、Hip 9 段
（另有 `lod=-1` 代理 mesh 各 1 段）。

**⇒ 本体最大 16（armor 最大 13）是"vanilla 内容保守"，不是引擎上界 —— 我们自己的产物已经做到 25，
是 vanilla armor 最大值的近 2 倍。** 本体分布只能说明"vanilla 从不逼近上限"，给不出天花板。

**已证：25 段可用。未证：100+ 段。** 拆件把 4–5 个 donor 并进一个 unit 可能到 100–150 段，
这一段**必须造极限样本实测**。这是 U0 里唯一还没答的问题。

### 3.3 顺带确认的两件事

- **vanilla 一个护甲套件 = 6–7 个 unit**，典型组成是 `Helmet + Torso×2（Slim/Stocky）
  + Hip + LeftShoulder + RightShoulder …`，每个 unit 只有 4 段左右。
  ⇒ "多槽位占满"的 legacy 形态是真实存在的，而**7 个标准槽只是它的子集**（缺护肩两个槽），
  这正是设计稿 §4.5 那条取舍的来源。
- **`lod=-1` 代理 mesh 可以很多**：本体有 unit 带 **60 个** `lod<0` mesh
  （分布从 0 到 60 都有，1 个最常见）。所以 §2.4 那条"隐藏壳的深度代理可能挡路"
  在极端件上不是小概率事件。

---

## 4 界面：不是架构问题

- 树是**扁平化行**（`TreeRowProjection` 压成 Depth / ZIndex），`ItemsControl` + **固定行高**。
- 默认只展开到 `BodyType`（`WardrobeTreeNodeViewModel.cs:49`），unit 层是**折叠**的 ⇒
  初次只画 7 个 unit 行，展开一个才画它的 section。
- `CanContentScroll="False"` ⇒ **目前没有虚拟化**，但注释写明"固定行高换来……将来打开虚拟化
  不需要重新量行"。150 行要开虚拟化只是加一个 `VirtualizingStackPanel` + 改一个属性。

⇒ **一个 Mod 50 个组 / 150 个 section 不是架构问题**，是"记得开虚拟化"和"默认折叠"的体验问题。

---

## 5 设计稿要改的六处

| # | 位置 | 改成什么 |
|---|---|---|
| 1 | §2.2 | 破口定义改成"每个 occurrence 落在目标槽数组长度内的段"；补"并段是整张最终 mesh 范围"（⑥）；补实测表；结论改成"不共用同一 `MaterialId` 时能保住，共用时会丢" |
| 2 | §3.0 | 判据不是"两段、两个 materialSlotId"，而是"**每段三角形数 == 追加前该段三角形数**"——并段后段数下降但几何总量不变，只看总数会漏 |
| 3 | §3.1 | 位置问题**已经答了**：位置不变。改成"位置已确认不变；剩下的是 `CullingBounds` 与深度代理两件事" |
| 4 | §3.2 | 本体上界 16 不构成上界；改成"已证 25，需造 100 段极限样本" |
| 5 | §6 | 补一条硬事实：**一次 append 会重排目标原有的 section 边界**（⑥），所以组的成员不能只存 `(unit, sectionIndex)`，必须存来源身份 + 允许一对多 |
| 6 | §5 / §8 | 组表加"本段合并了几个来源段"，界面在组里明示"这块和主体绑在一起" |

---

## 6 由此新增的两个工作项（排在 U1 之前）

### W1 `CullingBounds` 重算（**现存缺陷，独立于拆件**）

- 范围：`CanonicalUnitRebuilder` / `UnitAuthor` 写完 TOC 后按合并后的顶点重算那 7 个 float
  （照抄 `UnitAuthor.UpdateCullingBounds`）。
- 影响：装饰合并、头盔合并、多源合并三条在役路径全部受益。
- 判据：合并后的 unit，`vertices_outside == 0`（仓库已有 Python 契约检查断言这一条）。

### W2 `uniqueSlotPerSection` 开关

- 范围：`BuildMaterialProvenance` 给追加段按 `(SectionIndex, MaterialId)` 算稳定 hash 当
  `PreferredTargetSlotId`；默认关，`split` 路径打开。
- 代价：`BoneInfo.Remaps` 条数 = 槽数。
- 判据：拆件产物里"来源段数 == 产物段数"；`split` 预演做段数对账，不一致就列出被并的材质。

---

## 7 下一步建议

1. **先做 W1**（现存缺陷，独立价值，且拆件必须依赖它）。
2. **造 100 段极限样本**（唯一未答的问题）。用 `E:\tmp\secprobe` 的夹具扩到 100 段即可，
   不用进游戏就能知道"能不能生成、TOC 有多大"；进游戏只验"能不能加载"。
3. **W2 与 `split` 一起做**，因为它的开关只在拆件路径上打开。
4. **实机 A/B 留给最后**：位置已确认不变，剩下只有深度代理一条需要看图。

---

## 附：三份原始报告

| 文件 | 内容 |
|---|---|
| `A-material-merge.md` | 并段的端到端实测（7 组）、规避方案、否掉「non-SDK」注释 |
| `C-placement.md` | 位置/遮挡机制、真正依赖槽位的 5 条、`CullingBounds` 缺陷、实机 A/B 方案 |
| `B-capacity.md` | 本体 unit 容量扫描（若未交稿，容量结论以本文 §3 为准） |
