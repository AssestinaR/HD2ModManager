# 全量资源归属 / 名字 / 分类：现状与根因

**日期：** 2026-10-26
**目的：** 下一轮专业编辑器的前置条件——让 3567 个 archive 每一个都有归属、名字、分类。
**数据：** 本机 `asset-index.sqlite`（2026-10-25 14:37 UTC 构建）+ 游戏 `data/game/`。

---

## 0. 结论：不是"信息不够"，是**最大的那个事实源根本没解析成功**

| 指标 | 值 | 覆盖率 |
|---|---:|---:|
| archive 总数 | 3567 | — |
| **有真实名字** | **779** | **21.8%** |
| ├ 权威（注册表 + strings，15 语言） | 389 | 10.9% |
| └ 社区（archivehashes.json） | 390 | 10.9% |
| **有分类**（除 Other 外） | **513** | **14.4%** |
| **有归属**（被 kit 引用） | **421** | **11.8%** |
| 含 unit 的 archive | 2562 | 71.8% |
| ├ unit 条目总数 | 48020 | — |
| └ 有部位事实的 | 298 | **8.4%** |

**`game_data_authority_entities` 是 0 行。**

`game_data_authority_issues` 里只有一条，就是根因：

```
GameDataAuthorityGeneratedEntitiesInvalid
file: D:\...\data\game\generated_entities.dl_bin   (47 MB)
System.IO.InvalidDataException:
  EncyclopediaEntryComponentData instance was not found with a recognized layout.
  at GeneratedEntityEncyclopediaReader.FindRequiredPayload(...) line 245
  at GeneratedEntityEncyclopediaReader.Read(...) line 140
```

`generated_entities.dl_bin` 是**仅次于 game.dll 的第二大文件**（47 MB），
它记录"游戏里每个实体 → 用哪个 unit → 叫什么名字 → 属于哪类装备"。
它解析失败，于是：

- 3146 个 archive（88.2%）**没有任何归属**
- `GameDataArchiveSemanticClassifier` 的"实体百科"分支**永远走不到**
- 武器 / NPC / 载具 / 场景全部落进 `Other`

**这一条不修，后面所有分类工作都是在猜。**

---

## 1. 现有事实源盘点

| 源 | 位置 | 规模 | 现在用来做什么 | 能不能更多 |
|---|---|---|---|---|
| 注册表 `generated_customization_armor_sets` | `data/game/` 441 KB | 422 kit / 3527 件 | 护甲/头盔/披风的**权威**名字与四元组 | 已用尽 |
| 本地化 `strings` | 包内 | 389 archive × 15 语言 | 同上 | 已用尽 |
| **`generated_entities.dl_bin`** | `data/game/` **47 MB** | 未知（解析失败） | **完全没用上** | **最大金矿** |
| 社区 `archivehashes.json` | `data/resources/` 35 KB | **689 条 / 42 类** | 只当低置信兜底 | 分类粒度远大于我们 |
| `friendlynames.txt` | `data/resources/` 229 KB | 哈希 → 名字 / 内容路径 | **完全没用上** | 可给大量资源命名 |
| `bonehash.txt` | `data/resources/` 131 KB | meshId → 骨名 | 部位分类 | 已用（但见审计 N12） |
| archive 内容构成 | `archive_entries` | 48020 unit / 材质 / 贴图 | 只用来筛 armour/helmet | 可做启发式分类 |

### 1.1 社区分类比我们细得多

`archivehashes.json` 的 42 个分类：

```
装备类：Armor(121) Helmet(115) Cape(81) Backpack(13) Guard Dog(6) Turret(12)
武器类：Assault Rifle(11) Marksman Rifle(5) Submachine Gun(8) Shotgun(10)
        Explosive(2) Energy-Based(9) Special Primary(3) Special Secondary(9)
        Melee(7) Pistol(8) Support Weapon(30) Special Throwable(15)
        Standard Throwable(5) Ammo(1)
改件类：Mag(25) Scope(11) Attachment(34)
敌人类：Terminid(29) Automaton(22) Illuminate(16)
        Automaton: Jet Brigade(7) Automaton: Incineration Corps(5)
        Automaton: Cyborg Legion(2) NPC(2) Vehicle(10)
场景类：World Props(6) POI Props(5) Terminid Props(18) Automaton Props(3)
        Mega Cities(2) World Pickups(4) Samples(9) Super Destroyer(2)
        Icons(2) Loading Screens(2) Unreleased(2)
```

而我们的 `GameDataArchiveSemanticClassifier` 只有 5 个值：
`Armor / Helmet / Cape / Weapon / Other`，且社区标签只做兜底。

**42 类信息被压成 5 类，且只在权威判定失败时才用。**

---

## 2. 三层事实模型（建议）

### 2.1 名字（Name）

优先级从高到低，全部落进 `game_data_archive_names`，带 `source` 与 `confidence`：

| 优先级 | 来源 | 预期覆盖 | 置信度 |
|---|---|---|---|
| 1 | 注册表 + strings（本地化） | 389 | 100 |
| 2 | `generated_entities` 实体名 | 待定（修好后才知道） | 95 |
| 3 | 社区 `archivehashes.json` | 390 | 60 |
| 4 | `friendlynames.txt` 路径推断 | 待测 | 40 |
| 5 | 内容构成推断（`unit 数 + 材质数`） | 兜底 | 20 |

第 4 条值得单独试：`friendlynames.txt` 的格式是 `哈希<TAB>名字或路径`，
例如 `GL-21 Grenade Launcher`、`content/objectives/obj_common/oil_pump/oil_pump`。
如果 archive 的哈希在里面，直接就有名字或至少知道它在哪个内容目录下。

### 2.2 分类（Category）

| 优先级 | 来源 | 说明 |
|---|---|---|
| 1 | kit_kind（0/1/2） | Armor / Helmet / Cape，权威 |
| 2 | 实体 `equipment_type` | 修好解析器后可用 |
| 3 | 社区 42 类 | **保留原粒度，不要再压成 5 类** |
| 4 | 内容构成启发 | 见 2.3 |

**关键决定：分类表要能装下 42 类。** 现在 `category` 是单值字符串且只有 5 个值，
应该改成 `(archive_id, category, source, confidence)` 多行表——一个 archive 可以既是
`Terminid` 又是 `NPC`。

### 2.3 内容构成作为兜底信号

实测 3567 个 archive 的构成分布（U=unit，M=material，T=texture）：

| 构成 | 数量 | 大概是什么 |
|---|---:|---|
| U-MT | 2493 | 常规资源包（角色/道具/武器） |
| ---T | 422 | 纯贴图包 |
| ---- | 236 | 空 archive |
| --MT | 227 | 材质+贴图包 |
| U-M- | 29 | 只有 unit + 材质 |
| U--- | 22 | 只有 unit |
| --M- | 13 | 只有材质 |
| UCMT / U--T / UC-T | 18 | 含 Composite Unit |

**unit 数量是最强的分类信号**：

| unit 数 | archive 数（仅 Other） |
|---|---:|
| 0 | 882 |
| 1 | 496 |
| 2–5 | 502 |
| 6–20 | 532 |
| **21+** | **642** |

那 642 个"21+ units"的匿名 archive 几乎肯定是大角色/NPC/道具包
（最大的一个 `f4bf27b6edfdd763` 有 **390 个 unit**）。

### 2.4 归属（Ownership）

| 关系 | 现有来源 | 覆盖 |
|---|---|---|
| archive ← kit | `game_data_authority_kits` | 421 |
| archive ← entity | **空（解析器坏）** | 0 |
| archive ← archive（引用） | `archive_entries` + 跨 archive 引用 | 未算 |
| archive ← 无 | — | **3146** |

补上 entity 之后，还要补**引用图**：一个 unit 引用了别的 archive 里的材质/贴图，
这条边本身就是"归属"证据。

---

## 3. 要做的四件事（按收益排序）

| # | 事 | 收益 | 风险 |
|---|---|---|---|
| **1** | **修 `GeneratedEntityEncyclopediaReader`** | 可能是最大的一块：47 MB 的实体百科 | 中（要逆向新布局） |
| **2** | 把社区 42 类**全量导入**，不再压成 5 类 | 立刻 +689 条分类 | 低 |
| **3** | 用 `friendlynames.txt` 补名字 | 待测，可能很大 | 低 |
| **4** | 内容构成启发（unit 数 + 类型组合） | 兜底 3146 个 | 低 |

第 1 条是硬骨头但必须做——**没有实体百科，"其他 archive 有归属"这件事只能靠猜**。

---

## 3.1 已完成（2026-10-26，提交 `003250e` + `4c037f5`）

### 第 1 条：实体百科解析器修好了

根因不是文件坏了，是**三张表的表长变了**。用内嵌 GameData 密钥解出 47 MB 明文后实测：

| 表 | 索引项 | 组件数 | 组件长 | 旧值 |
|---|---:|---:|---:|---|
| EncyclopediaEntryComponentData | 1350 | 675 | 56 | 1314 / 657 |
| UnitComponentData | 3438 | 1720 | 136 | 3334 / 1668 |
| EquipmentComponentData | 702 | 352 | 232 | 674 / 338 |

`EntitySettingsHashmap` 仍是 4096×32（改成 5120 会有 1024 条越界）。

**分段正确性的决定性证据**：每个实体的 `componentIndex` 唯一占一个组件槽，
实测覆盖 675/675、1719/1720、351/352——1:1 且只差空槽。这种吻合不可能是巧合。

顺带把"硬编码表长"改成**可推导**：常量对不上时，`DeriveLayout` 从数据自己推
（索引项形状 `eid(u64)|ci(u32)|0(u32)`，先扫到第一个形状不符的位置，再往回找
让组件区整除且通过校验的长度），推导成功会在 `catalog.DerivedLayouts` 留痕。
下次游戏更新不会再让整块事实静默消失。

### 第 2、3、4 条：内容域推断

`ArchiveContentProfiler` 从 `friendlynames.txt` 的内容路径推域：
`content/<key>/<subject>/…` 投票决定域与主题。三条边界：

1. 只填空——权威与社区结论一律不覆盖；
2. 纯资源名不参与分类（`GL-21 Grenade Launcher` 是名字不是域）；
3. 构成兜底的名字（`unit×9`、`贴图包`）不唯一 → 只记分类，不改显示名。

新增 `archive_domains(archive_id,domain,subject,source,confidence)`，可多值、带来源。

### 前后对比（同一份数据）

| 指标 | 修前 | 修后 |
|---|---:|---:|
| 有真实名字 | 779（21.8%） | **2009（56.3%）** |
| 仅哈希 | 2788 | 1558 |
| `category='Other'` | 3054（85.6%） | **0** |
| 分类种类 | 5 | 17 |
| `archive_domains` 覆盖 | — | **3567（100%）** |
| `game_data_authority_entities` | 0 | 665 |
| `game_data_authority_issues` | 1（错误） | 0 |

分类分布：UnitBundle 863、Environment 754、Weapon 357、TexturePack 262、Data 221、
MaterialPack 220、Helmet 161、Armor 137、Cape 123、Empty 107、Objective 105、
Facility 90、LevelSettings 70、SharedArt 55、Audio 33、Planet 8、Level 1。

### 还剩什么

**1558 个 archive 仍只显示哈希。** 它们确实没有任何名字来源：
`friendlynames.txt` 里没有它们的内容路径，实体百科也没引用它们。
但每一个都有分类和 unit 计数，资源树可以显示成「9 unit · 0x0214…」而不是裸哈希。

另外 `friendlynames.txt` 本身有数据质量问题：存在 `ortifications/banner_02`
（丢了首字母 f）、`y_detained_prisoners_02` 这类截断条目，所以不能用它当资源名，
只能取其中的 `content/` 路径做分类。

**社区 42 类还没全量导入**（第 2 条只做了一半）：现在 675 个社区覆盖的 archive
已经有分类，但社区的多值分类（一个 archive 既是 `Terminid` 又是 `NPC`）
还没有进 `archive_domains`。这是下一步。


---

## 4. 对下一轮编辑器的影响

资源树需要每个节点有：**身份（哈希）、名字、分类、归属、内容摘要**。

按当前覆盖率，树里会有 78% 的节点显示成 `f4bf27b6edfdd763` 这种裸哈希。
这不能算"专业级"。

所以顺序必须是：**先补事实（本轮四件事）→ 再做树**。
否则树做出来只是把"一堆哈希"换了个更好看的摆法。
