# B — 游戏本体单 unit 的 section 容量与结构（只读测量）

日期 2026-11-04 · 仓库 `E:\Data\source\repos\WpfApp1` · 只读，未 build、未改仓库、未改游戏目录

---

## 0 一句话答案

**游戏本体没有任何一个 unit 超过 16 节（LOD0 mesh 口径）；护甲 unit 一律 1 节/LOD。**
所以"单 unit 能装多少节"这件事，**游戏本体给不出上界** —— 它只是从没超过 16。
而我们自己的产物已经在用 25 节/unit（`lunalice_bill` Torso），**已经越过本体的最大值**。
⇒ 16 是"本体观测到的最大"，**不是天花板**，不能当限制写进设计。

---

## 1 走的是哪条路（先说清可靠性）

`unitprobe.exe` **在本目录上用不起来**，原因不是工具问题，是数据问题：

| 事实 | 证据 |
|---|---|
| `data\` 下 122 个 `9ba626afa44a3aa3.patch_*` **全部是符号链接**，指向 `E:\mod\...`（管理器部署态） | `Get-Item ... \| Select LinkType,Target`；`dir` 显示 `<SYMLINK>`，长度 0 |
| 因此 `data\` 里**没有一个游戏本体 patch TOC 文件** | 122 个 patch + 122 gpu + 35 stream 里，122 个 patch 全是链接 |
| 本体内容在 **31 个 `bundles.NN.nxa`**（约 20 GB）+ `bundles.nxa` 索引里 | `GameDataPackageResolver.cs:184-208` |
| 45 个 16-hex 命名的 DSAR 包（`02582f3da1f8daf5` 等）只含 Lua/配置，**unit 条目 0 个** | 解析后 typeId 只有 `0x504b55235d21440e`(34767)、`0x535a7bd3e650d799`、`0xaf32095c82f2b070` |

于是改用 **Python 直读**（脚本在 `E:\tmp\investigate\sectional-u0\`：`probe.py` / `probe3.py` / `probe5.py`）：

1. 解 `bundles.nxa`（DSAR，chunk 0 解压后是包表：count@0x10，条目 0x18 字节，item 0x10 字节）；
2. 按 item 的 `(archiveOffset, bundleOffset, bundleIndex)` 定位到 `bundles.NN.nxa` 的 chunk；
3. 只解压 **TOC 所在的 chunk**（chunk 表里已有 uncompressedSize，可不解压就算出落点），**完全不解顶点**；
4. 按 `PatchTocScanner` / `UnitMeshReader` 的字段偏移解析 unit → mesh → section。

**可靠性**：同一份解析代码在我们自己的产物上复算 `lunalice_bill\vpk2patch\...patch_0`，
得到 Helmet **22** / Torso **25**，与 `unitprobe.exe` 的 `nsec=22` / `nsec=25` **逐值一致**（交叉验证通过）。

**抽样说明**：不是抽样，是**全量**。9725 个包全扫，6732 个有 TOC，**2562 个含 unit**；
unit 条目 48020 个 → **8019 个不同 unit fileId**（同一 unit 在多个包里重复出现，按 fileId 去重）。
其中 **5472 个（68%）解析出 mesh 结构**；未解析的两类：
- 2833 个 `meshInfoOffset == 0`：TOC 里 nameHash/bonesRef/compositeRef/meshInfo/materials **全为 0**，即**没有任何几何**的 unit（spawner/标记类），按 0 节计；
- 554 个 composite 背书的 unit 读不出（见 §5 注），占 7%，不影响下面的最大值判断。
耗时：全量扫描 35–40 s / 解压约 520 MB。

---

## 2 每 unit section 数分布（问题 1、2）

口径：**该 unit 的 LOD0 mesh 的 section 数**（`lod=-1` 的深度代理 mesh 单独算，见 §5）。

| 样本 | n | max | p99 | p95 | 中位数 | 均值 | >50 | >80 | >100 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| **全部 unit** | 5472 | **16** | 4.0 | 3.0 | 1.0 | 1.40 | **0** | **0** | **0** |
| 带 `HelldiverCustomizationSlot_*`（护甲/角色） | 1757 | 4 | 3.0 | 2.0 | 1.0 | 1.44 | 0 | 0 | 0 |
| `pieceType = Armor` | 1019 | 3 | 2.0 | 2.0 | 1.0 | 1.10 | 0 | 0 | 0 |

直方图（全部 unit，0..16+）：`0:2836  1:1562  2:923  3:117  4:25  5:3  6:3  9:2  16:1`

**>50 / >80 / >100 的 unit：一个都没有**（三个区间名单为空）。这就是问题 2 的答案：
**本体不存在超过 100 节的 unit，也不存在超过 16 节的 unit。**

最大值单位（唯一 16 节的）：`e37434e0b34db81f` slot=`content/fac_helldivers/vehicles/frv/frv_superearth`
（FRV 载具，5 级 LOD，LOD0 16 节 16 材质，50611 顶点）。其后是 9、9、7、6…

**关键读法**：本体节数少不是因为引擎限制，而是**本体一件护甲只用 1 个材质**。
护甲 unit 的实测样子（`137557103b22b358`，Torso/Armor/Stocky/Medium）：

```
mesh0 lod=-1 nsec=1 nIdx=36      ← 深度代理/剔除壳（设计稿 §3.1 说的那个 mesh0）
mesh1 lod=3  nsec=1 nIdx=28530
mesh2 lod=2  nsec=1 nIdx=14268
mesh3 lod=1  nsec=1 nIdx=35676
mesh4 lod=0  nsec=1 nIdx=71346   ← LOD0：1 节、1 材质、10833 顶点
```

⇒ **"一个 unit 装几十节"在本体上从来不是原生状态**。设计稿 §2.1 里"游戏本体躯干实测 52 节"
**无法复现**：`lunalice_bill` 的 Torso 现在是 25 节（unitprobe 与我的解析器都这么说），
52 应是当时另一个构建/跨 unit 合计，**不能当"本体 52 节"引用**。

---

## 3 同槽多段（问题 3）

**有，但只在道具类 unit 上出现，而且很稀少。**

- 全库 5472 个 unit 中，**12 个 mesh** 出现"2 段及以上共用同一个 `MaterialSlotId`"，集中在 **2 个 unit**：
  - `67dc32dca4f02d33`：某级 LOD 的 mesh **nsec=5、nmat=1** ⇒ 5 段全部指向 `materialIndex 0`，
    即**5 段共用一个材质槽**（另几级 LOD 是 nsec=3/nmat=2、nsec=3/nmat=1）；
  - `74599e56f72f9d7e`：lod 4/5/6/9 各 **nsec=2、nmat=1**。
- 护甲/角色 unit：**0 例**（每个 LOD mesh 只有 1 段，谈不上共用）。

**对我们的含义**：`CanonicalDecorationUnitAppender.cs:185-188` 那句"保留同槽多段会产生 non-SDK section table"
**不是引擎规则，只是 SDK 生成器的自我约束** —— 游戏本体自己就有"nmat=1 却 5 段"的资产在跑。
但**反证强度有限**：这些是道具/场景件，不是被护甲 UI 频繁换装的角色件。
建议：**不要为了这条去省"强制每段独占槽"**，因为它成本低（`bindingsBySlot[slot] = materialId` 本来就能多槽同材质），
而省掉它的收益只是少几个槽。**先按"每段独占槽"实现，把这条记为"引擎不禁止，但未被角色件验证"。**

---

## 4 护甲套件的 unit 组成（问题 4）

### 4.1 7 槽之外还有 2 个槽

本体 `HelldiverCustomizationSlot_*` 一共 **9 个**（unit 数）：

```
Torso 394 · Hip 288 · LeftArm 197 · RightArm 193 · LeftShoulder 162 · RightShoulder 160
RightLeg 132 · LeftLeg 132 · Helmet 99
```

`pieceType` 三种：`Armor 1019 · Undergarment 630 · Accessory 108`；
`bodyType`：`Stocky 636 · Slim 629 · Any 492`；`weight`：`Medium 1414 · Heavy 174 · Light 169`。

⇒ **7 标准槽之外就是 `LeftShoulder` / `RightShoulder`**（pieceType 多为 `Armor`，少数 `Accessory`）。
没有 Cape / 护胸板这类独立槽位 —— 胸甲是 Torso 的一部分，披风不在 customization 槽位体系里。

### 4.2 套件实例（按"同一套材质 id 集合"分组，2844 组）

**套件 A**（7 个 unit，Medium，带护肩）：

| unit fileId | slot | pieceType | bodyType | LOD0 节数 |
|---|---|---|---|---:|
| `897abcbaa487afe8` | Helmet | Armor | Any | 1 |
| `26aae8b522a2c53d` | Torso | Armor | Slim | 1 |
| `b3222572d8945432` | Torso | Armor | Slim | 1 |
| `b3e2e26299afa723` | Torso | Armor | Stocky | 1 |
| `bcf47cef44aab934` | Torso | Armor | Stocky | 1 |
| `307a30e47ffa7d2b` | LeftShoulder | Armor | Slim | 1 |
| `9b068acb159af977` | LeftShoulder | Armor | Stocky | 1 |

**套件 B**（6 个 unit，Medium，左右护肩 + 胯部 Accessory）：

| unit fileId | slot | pieceType | bodyType | LOD0 节数 |
|---|---|---|---|---:|
| `0edf3119007d3e55` | LeftShoulder | Armor | Slim | 1 |
| `56582d7804a107bc` | LeftShoulder | Armor | Stocky | 1 |
| `7cf9e6dc59f819fb` | RightShoulder | Armor | Slim | 1 |
| `b31cbbb559d1ae9e` | RightShoulder | Armor | Stocky | 1 |
| `df609078420f0304` | Hip | **Accessory** | Stocky | 1 |
| `e314a6ccf2ca3d68` | Hip | **Accessory** | Slim | 1 |

**读法**：一套护甲 = **每槽 × 每身形各一个 unit**（Slim/Stocky 两个变体是**两个不同 unit**，
`body=Any` 的件才共用）。护肩是**独立 unit、独立槽**，不是 Torso 的一部分。
⇒ 把非 7 槽几何并进 Torso，碰到的第一件事是：**Slim/Stocky 两个变体的护肩要分别并**，
并进同一个 Torso 后**只能靠 section 区分身形**（否则健壮/纤细会同时出现）。
「layer」在本体的 unit 数据里**不存在**这个字段（只有 `lod` / `groupIndex` / `tintSlot`），
设计稿 §3.1 说的层序应来自护甲注册表而非 unit。

---

## 5 LOD 结构（问题 5）

- **LOD 级数**：含多级 LOD 的 unit 2597 个；级数分布 `4级:1798 · 5级:314 · 9级:267 · 8级:84 · 6级:40 · 3级:20 · 10级:12`…
  护甲 unit 是 **4 级（lod 0-3）+ 1 个 lod=-1 深度代理**。
- **各级 section 数是否一致**：**不一致，且不罕见** ——
  `2597` 个多 LOD unit 中，**1626 个（62.6%）各级节数完全相同**，**971 个（37.4%）不同**。
  不同者的形态几乎全是"高 LOD 掉段"：`(LOD0=2, 其他=1) 818 例`、`(3,1) 106`、`(4,1) 31`、`(6,1) 6`。
- **护甲 unit 是"一致"的那一类**：每级 LOD 都恰好 1 段（见 §2 的 5-mesh 实例）。

⇒ 设计稿 §10 硬约束 4「LOD 家族节数必须与 LOD0 基准一致」**不是引擎普遍规律**（本体 37% 违反）。
它是**我们自己的工程约束**（按同一下标关段才安全），这个理由成立，但**不能写成"引擎要求"**。
建议改写为："我们只对**节数与 LOD0 相等**的 LOD 写开关（`BuildSectionDocument` 已这么做），
节数不等的 LOD 不参与开关"——本体数据支持"不等是合法的、只是那些 LOD 不能按下标对齐"。

**深度代理**：`lod=-1` 的 mesh 广泛存在（2110 个 unit 恰好 1 个，最多 60 个，629 个 unit 没有）。
护甲件的那 1 个 `lod=-1` mesh 只有 36 个索引 —— 与设计稿 §3.1「mesh0 是深度代理/剔除壳」一致。

---

## 6 对设计稿的影响

| 设计稿条目 | 结论 |
|---|---|
| §2.1「游戏本体躯干实测 52 节」 | **撤回**。本体护甲 1 节/LOD；52 是 mod 侧数字。当前 `lunalice_bill` Torso = **25 节** |
| §3.2「单 unit 的 section 数上限」 | **本体无法给上界**（最大 16，且是载具）。真正的上界证据只有"我们自己的产物"：**25 节/unit 在产线在用**，另有 52 节被运行期探针写过 0（`HANDOFF` §11.2），说明**引擎能吃 ≥52 段** |
| §3.2「造一个 100–150 节的极限样本」 | **仍需要，但目的变了**：不是"证明 25 节能跑"，而是**找到失败点**（100？150？还是根本不失败）。样本应该是"把一个 unit 的 LOD0 灌到 N 节"的纯容量探针，不需要 donor 几何 |
| §2.2「强制每段独占槽」 | **保留**（成本低）。本体有同槽多段（nmat=1 却 5 段），说明引擎不禁止，但没在角色件上验证过 |
| §10 硬约束 4「LOD 家族节数必须与 LOD0 一致」 | **降级为工程约定**（本体 37% 不一致）。保留"不等就不写开关"的做法，措辞改成我们的选择 |
| §4.4 归属槽规则（非 7 槽 → Torso） | 可行，但要加一条：**Slim/Stocky 两个变体的护肩是两个 unit**，并进同一 Torso 后必须靠 section 区分身形 |

**给 U0 的最小实验清单（收敛后）**：
1. 纯容量探针：一个 unit 的 LOD0 依次灌 25 / 50 / 100 / 150 节，进游戏看是否加载、帧率、显存 —— **这是唯一还缺的证据**；
2. 同槽多段：造一个"2 段共槽"的角色件进游戏，验证不被合并（本体只在道具上验证过）；
3. 跨槽合并后遮挡：LeftShoulder → Torso，同机位 A/B（原计划不变）。

---

## 7 复现

```
cd E:\tmp\investigate\sectional-u0
python probe5.py units5.jsonl     # 全量扫本体 unit（约 40 s）→ 每 unit 每 mesh 的节数/材质槽
python analyze2.py units5.jsonl   # 分布 + 同槽统计 + 槽位词表 → analyze2.txt
python analyze3.py units5.jsonl   # LOD 一致性 + top 列表 → analyze3.txt
python probe6.py mods             # 对照组：扫 139 个已部署 mod patch → mod_units.json
```
产物：`units5.jsonl`（5472 unit）、`units_mats.json`、`mod_units.json`、`analyze2.txt`、`analyze3.txt`、`analyze4.txt`。

**已知缺口（不影响上面结论）**：
- 554 个 composite 背书 unit 未展开 —— 本体 unit 的 `nameHash` 恒为 0，而 `ReadComposite` 按 nameHash 查表，故这条路对本体无效（该 554 个占 7%，且分布在全库各处，不是护甲集中区）；
- 45 个 hash DSAR 包与 `.nxa` 内的 Lua/材质资源未解析（与 section 数无关）。
