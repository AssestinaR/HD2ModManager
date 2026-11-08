# VPK 导入的两个问题：跨 Mod 资产 id 冲突 + L4N 组件开关（2026-10-30）

> 本轮只做情报与工序，没有改码。所有数字都是拿用户给的两个包实测出来的。

> ⛔ 已过时（2026-11-08）：本文把 `vpk2patch/` 当成"现在往哪写 / 在哪读"的地方，一律按 **`sectional/`** 读
> （2026-11 改名；老库里仍是 `vpk2patch/`，读侧两个名字都认）。另：详情页「切换」/「应用」/「重构建」现在
> 统一走编译基座 `ModCompileService.CompileAsync` 产出 `Overwrite/`；装饰重建、批量修复仍直接写它，属已知债。
> 见 `docs/agent/sectional-compile-base-20261108.md` §1.2/§1.3。

## 0. 样本

| 样本 | 位置 | 内容 |
|---|---|---|
| 【vrchat】eku-见习奇术师.7z | `C:\Users\Administrator\Downloads\` | 8 个 survivor 模型包（同一模型）+ 2 个材质包（`sfw` / `sfw-nekotoon`，各 147 MB） |
| 【PUBLIC】Milfy_MeltyDoll八人包.7z | 同上 | 8 个 survivor 模型包 + `基础材质.vpk`（31 VMT / 34 VTF） |

已解到 `tmp/eku-mod/` 与 `tmp/melty-mod/`（**别删**，下面所有数字都出自这两份）。
探针：`tmp/probe_vpk.py`（VPK 目录树 + VTF 头）、`tmp/probe_materials.py`（VMT/VTF 全量转储）、
`tmp/probe_collide.py`（id 撞车计算）、`tmp/probe_refs.py`（只算被材质引用的贴图）。

## 1. 问题 A：两个 Mod 装在一起，头发/脸的贴图串味 —— 根因已用数据钉死

### 1.1 id 是怎么来的

`Hd2AssetId`（`HD2SourceMigration/VpkConversion/Hd2AssetId.cs:50-53`）：

```
材质 id = sha256($basetexture 全路径 [+ |emission |alphaclip |matcap:…])[0..8]  → 高 4 位 0xA
贴图 id = sha256(basename)[0..8]                                              → 高 4 位 0xB
```

**贴图 id 只吃 basename，不含目录、不含 Mod 身份。** 这一条是刻意的（贴图覆盖包靠它生效：
`SourceMaterialConverter.TryConvertTexture` 产出的 id 必须与材质那条路算出来的一致，
见 `SourceMaterialConverter.cs:781-790`），但它同时意味着**任何两个 Mod 的同名贴图必然同 id**。

### 1.2 实测撞了什么

两个包各自被材质引用的贴图名集合，交集只有三个：

| basename | eku 里 | melty 里 | 字节相同？ |
|---|---|---|---|
| `hair` | `materials/vrchat/eku/icsut/hair.vmt` | `matchacheese/milfy_maid/hair.vmt` | **不同**：4096² DXT5 / 1 mip / 16.8 MB vs 2048² DXT1 / 12 mips / 2.8 MB |
| `face` | `…/face.vmt` | `…/face.vmt` | **不同**：4096² DXT5 vs 2048² DXT5 |
| `white` | `eye_base.vmt` 的 `$EmissiveBlendTexture` | 31 份材质都写 `vgui/white` | 引擎贴图，两个包里都没有 → 都走"找不到"分支，无害 |

于是两个 Mod 都产出 **贴图 id `0xb9da05ba9b15c24d`（hair）** 与 **`0xbcc7429fb7d98202`（face）**，
负载完全不同。两个 Mod 同时启用时，游戏按 `(typeId, id)` 查资产，只有一份能赢——
输的那个 Mod 的头发材质采样到赢家的头发图集，表现就是"**大披散头发上散落着一堆小号头发贴图**"。

这解释了全部三个现象：

- 为什么只有同时装两个 VPK Mod 才出现（单个 Mod 自己内部 id 一致，没有第二个写者）；
- 为什么是头发/脸这种**通用名**（`hair` / `face` / `body` / `costume`），而不是作者特有的名字；
- 为什么开发机上单独测一个包复现不出来（`5eb653e` 之前的手工实测就是这个条件）。

### 1.3 材质 id 也会撞（用户的判断是对的，只是这一对还没撞上）

这一对没撞，是因为两个作者用了不同的 `$basetexture` 目录（`VRChat/Eku/ICSUT/…` vs
`matchacheese\Milfy_Maid\…`）。但**同一角色的两个皮肤包**会写同一套源路径
（`models/survivors/survivor_zoey/…`），材质 id 就会逐条相同。这正是用户说的
"来源模型均为 L4D2 中某个角色的材质，可能它们的材质 id 一致"。

### 1.4 批内已经有检查，跨 Mod 完全没有

`VpkPatchAssembler.cs:595-610` 在**一次转换内部**报了 `贴图 id 冲突` 警告；
跨 Mod 没有任何人检查——因为 id 里压根没有"哪个 Mod"这一维。

### 1.5 修法（按顺序）

| 步 | 做什么 | 为什么这个顺序 |
|---|---|---|
| A0 | 造两个小 VPK 夹具（同名 `hair.vtf`、不同像素），钉住"不同命名空间 → 不同 id" | 先把判据钉死，后面每一步都有回归 |
| A1 | `Hd2AssetId` 加可选命名空间：`Derive(prefix, ns + "/" + name)`；无 ns 时与今天**逐位相同** | 保住 Python 对拍与旧产物的字节不变 |
| A2 | 贯通 `vpk2patch --id-namespace <token>` → `ConvertVpkAsync` → `VpkPatchAssembler` → `SourceMaterialConverter`（含 opacity mask / emission / matcap 那几个派生点） | **一批共用一个 token 是硬约束**：模型 patch 引用的材质 id 必须与材质 patch 写出的相等 |
| A3 | token 写进 `vpk2patch.json`（`idNamespace`），重转/排查都能看见 | 否则"这个 Mod 的 id 凭什么这样"无法回答 |
| A4 | 回归：现有 8 个样本包在无 ns 下报告与今天完全一致 | 证明没有副作用 |
| A5 | 发布说明 + 导入报告里一句"旧版转换的 Mod 需要重导一次才能拿到隔离后的 id" | 用户已定：**不为存量 Mod 做检查/迁移**，那这句就是唯一告知渠道 |

**已定的取舍**（2026-10-30，用户选）

- 命名空间 = **来源包名哈希**（不是批次 GUID）：重转同一份包得到同一套 id。
- 存量 Mod：**只在新转换里修，不做部署期冲突检查、不做就地改写**。代价是装了旧 Mod 的用户
  不重导就仍然串味，而且不会收到任何提示——所以 A5 那句话必须写进发布说明。

**没选的方案与原因**

- 贴图 id 改成 `hash(目录 + basename)`：能修这一对，但修不了"同角色两个皮肤包"（路径相同），
  而后者正是用户描述的场景。
- 贴图 id 改成 `hash(像素)`：会**破坏贴图覆盖包**（`TryConvertTexture` 靠 basename 对齐材质引用），
  且必须先把所有贴图解码一遍才能算 id（每个包多几十秒）。
- 只在部署期拦：不改数据，只能报错不能修；两个 Mod 都是用户想要的，拦下来等于让他二选一。

## 2. 问题 B：L4N 组件开关 → 命名开关/预设

### 2.1 L4N 的开关到底是什么（62 轮已定论，不重开）

`6da401e` 的结论：L4N 的"部件实时切换"就是 Source 原生 `$BodyGroup`；作者那 31 个 `XXX关.vpk`
是给**没装 L4N** 的玩家的材质层降级方案（`$no_draw` 覆盖）。HD2 侧没有 bodygroup 运行时，
但有 section 开关（`WardrobeSectionMap` + `WardrobeSectionBaker`）——这就是等价物。

### 2.2 名字其实已经有了，只是没传到界面

`SourceSegmentation.cs:49-53`：**多变体的 bodypart = 可切换开关**，每个整体作为一份 Unit；
固定 bodypart 才按骨归属切成七份本体。实测 Lunalice 32 个 bodypart = 30 可切换 + 2 固定，
名字带作者的中文标注（`裙Dress_Skirt` / `猫耳CatEars` / `厚底高跟platform shoes`，
`RealSourceModelTests` 已钉住 UTF-8 解码）。

但管理器导入时传的是 `--components merge`（`VpkImportService.cs:334`），
把配件并进它主导的身躯槽位 → **开关的身份当场丢掉**，`sections.json` 里只剩
`(unit, sectionIndex, MaterialId)`，界面自然只能显示数字。

### 2.3 `$no_draw` 开关包现在是被丢掉的

`VpkImportPlanner.cs:271-276`：整包都是 `$no_draw` → `Unsupported`，理由写的是
"HD2 没有'不绘制'这种材质，所以「开关」在 HD2 里没有等价物"。这句话现在不成立了。

实测样本（`tmp/issue-20261028/x/shuli/组件材质/`）：

```
【VRM】橘雪莉 披风 关.vpk
  addoninfo.txt  addontitle "【VRM】橘雪莉 披风 关"
  materials/diyu2026/…/off/orange_mantle.vmt     → VertexlitGeneric { $no_draw 1 … }
【VRM】橘雪莉 描边 关.vpk
  materials/diyu2026/…/outlineoff/outline_base.vmt
```

结构很规整：**一个开关 = 一个 VPK = 一份 `$no_draw` VMT，basename 就是它要藏的那份材质**。
所以"开关名 → 掩码"是纯数据推导，不需要猜：

```
开关名（addoninfo 标题去掉模组名） → VMT basename → 材质 id（与转换同一套 identity）
→ sections.json 里 MaterialId 相同的 section 集合 → (unit, sectionIndex) 掩码
```

### 2.4 修法（按顺序）

| 步 | 做什么 |
|---|---|
| B1 | `sections.json` 升 `hd2mod.section-map/3`：unit 记 `sourceName`（bodypart 名）、section 记 `sourceMaterial`。缺字段照旧可用（向后兼容） |
| B2 | 树节点显示名字：`猫耳CatEars`、`Hair`（现在是 `Unit 3` / `Section 0`）；数字退到副标题 |
| B3 | 开关包从"跳过"改成 `VpkImportPlanner` 的新类型 `ComponentSwitch`（带 `addonTitle` 方括号名 + VMT 名集合），并**从解析集合里剔除**（见 2.6，否则开关 VMT 会顶掉真材质） |
| B4 | 转换后处理：两个来源都归约成材质集合 → 对应 section 掩码 → **按掩码去重合并**（见 2.7）→ 写 `vpk2patch/components.json`，并落进 `wardrobe.json` 的预设段 |
| B5 | 界面：预设按钮 → 写 `VpkWardrobeState` → 走现有 `ApplyAsync` 烘焙（**不新增烘焙路径**）；开关名显示主名 + 别名 |
| B6 | 回归：`WardrobeSectionMapFile` / `VpkModWardrobeService` 的旧文件仍能读；两个 Manuka 包分别识别出 **15 / 14** 个开关且无重复 |

### 2.5 Manuka 类包：没有 L4N 开关，开关全在材质层（实测）

样本：`【SHOP】Manuka_JABIS八人包ver101`、`【SHOP】Manuka_LaFiore八人包ver101`（都是解压好的目录）。

```
[l4n_survivor] Manuka_JABIS_.vpk      95 MB   addoninfo: "Metadata generated by to_l4n_survivor"
Manuka_JABIS_<8 个 survivor>.vpk     12-20 MB 每个 1 个 survivor.mdl + 1 个 v_arms.mdl
Manuka_JABIS基础材质.vpk               89 MB  26 VMT / 41 VTF
附加材质\Manuka_JABIS<组件>关.vpk       ~0 MB  每个 1-3 份 $no_draw VMT
附加材质\R18\…内裤关 / 文胸关.vpk       ~0 MB
```

**`[l4n_survivor]` 不是"L4N 开关"，只是把 8 个包合并成一个**（`to_l4n_survivor` 生成的元数据），
模型本身**没有任何可切换 bodypart**：

```
Manuka_JABIS_Zoey.vpk / survivor_teenangst     5 bodyparts，0 个多变体
[l4n_survivor] … / survivor_teenangst          5 bodyparts，0 个多变体（head / Body×3 / clamped1）
```

所以 B1/B2 那种"从 bodypart 推导开关"在这里一条都产不出来——用户的观察是对的。

**开关的真实机制是 Source 的 `$cdmaterials` 搜索路径。** mdl 里有两级材质目录：

```
Matchacheese\Manuka_JABIS\opt\     ← 第一级
Matchacheese\Manuka_JABIS\         ← 第二级
```

材质名只是 `c_white_a_ear`，引擎先找 `materials/matchacheese/manuka_jabis/opt/c_white_a_ear.vmt`，
找不到再落到 `materials/matchacheese/manuka_jabis/c_white_a_ear.vmt`。开关包只提供**第一级**那份
（`$no_draw 1`），于是"装了这个包 → 猫耳不画；不装 → 落回真材质 → 猫耳正常"。
基础材质包里一个 `opt/` 条目都没有（26 份 VMT 全在上一级），这正是这套机制的证据。

开关名就在 `addonTitle` 的方括号里：`addonTitle "Manuka JABIS[猫耳关]"`（survivor 包用的是同一个约定：
`[替换 Zoey]`）。文件名同样是 `Manuka_JABIS猫耳关.vpk`。

两个包的开关数：JABIS **15** 个（含 `R18\` 2 个）、LaFiore **14** 个。
命名不只有 `X关`：LaFiore 里有一个 `Manuka_LaFiore切换纹身.vpk`（切换，不是关），
所以名字解析要同时吃 `X关` / `切换X` 两种写法，不能只按"去掉关字"。
LaFiore 还额外带了一份**没打包**的 `附加材质\Manuka_LaFiore美甲关\`（addoninfo + 2 份 VMT），
与同名 VPK 重复——按目录枚举 VPK 时不会命中它，正好不会重复。

### 2.6 ⚠️ 这套机制和我们的"按裸名找材质"撞车了

`SourceMaterialSource.Index` 只按 **FileName（裸名）** 建索引，`TryAdd` **先到先得**
（`SourceMaterialSource.cs:147-150`）。开关包的 `opt/c_white_a_ear.vmt` 与真材质
`c_white_a_ear.vmt` **裸名完全相同**，所以：

- 谁先入索引谁赢。今天 `附加材质\` 是子目录，整批排序时排在同级文件之后，真材质侥幸赢；
- 一旦开关包排到前面（换个目录结构、或者用户只把 `附加材质` 摊平），
  **该部件会静默失去材质**（`$no_draw` → 跳过），不报错、不警告。

修法（并入 B3）：把开关包**从解析集合里排除**。判据要便宜——开关包的特征很硬：
`无 mdl、无 vtf、条目数 ≤ 4、总字节 < 64 KB`，再对这几份 VMT 解析确认全是 `$no_draw`。
几百个包的 L4D2 addons 目录里只会命中个位数，不会拖慢索引。

### 2.7 去重：开关的身份 = 它隐藏的 section 集合

两个来源最后都归约成"材质集合"，所以掩码可以直接比：

| 来源 | 名字 | 掩码 |
|---|---|---|
| 多变体 bodypart（Lunalice 那类） | bodypart 名（`猫耳CatEars`） | 该 bodypart 用到的材质 → 对应 section |
| `$no_draw` 开关包（Manuka 这类） | `addonTitle` 方括号里的名字（去掉"关"） | VMT 裸名 → 同裸名材质 → 对应 section |

**判据：把掩码排序后当键，相同即同一个开关，合并成一个。** 名字优先级：
bodypart 名 > `addonTitle` 方括号 > 文件名去前缀。两个名字都留着当别名，界面显示主名。

不做"名字相似就合并"：`手环关` 藏的是 `manuka_costume`，而 `manuka_costume` 也可能被整件衣服用
——按名字合并会把两件不同的东西并成一个，按掩码合并则诚实地承认"这两个开关是同一件事"。
共用一个材质的部件会一起关，这与源侧 `$no_draw` 的粒度完全一致，是忠实行为。

## 3. 顺手排掉的两个嫌疑（都不成立，别再回头查）

**① Mipmap 顺序。** 两个包共 136 个 VTF，逐份按 `VtfFile.MipSize` 重算链尾，
**只有 10 个对不上，全是 cubemap**（`cub_*`，6 面）。其余 126 个（含 12 mips 的
`Milfy_Maid/Hair.vtf`、10 mips 的 `yeguang.vtf`、非压缩 BGR888 的 `toon_light.vtf`）
`header + 缩略图 + mip 链 == 文件长度`，误差 0。缩略图也确认是**单层** DXT1
（16×16 = 128 字节），不是带 mip 的链——`VtfFile.cs:195-199` 的假设对这批文件成立。

**② 两个材质包互相覆盖（`sfw` vs `sfw-nekotoon`）。** 两包同名贴图确实存在，
但都属同一个 Mod（同一批导入、同一套 id），先到先得是**同一份 Mod 内部的合并**，
不会跨 Mod 串味；真正的问题在 Mod 之间。

### 遗留小坑（低优先级，不在本轮）

`VtfFile.Read` 不认 cubemap（6 面）。今天走不到：`$envmap` 不在转换范围内，
而"纯贴图包"分支（`VpkPatchAssembler.cs:517-531`）只对整个 VPK 里每张 VTF 调
`TryConvertTexture`。若哪天某包把 cubemap 当底色，或纯贴图包里带了 cubemap，
会静默读出**第 0 面**当 2D 贴图（不报错，`Trailing` 是剩余 5 面）。判据已经有了：
`Trailing != 0` 就该拒绝，现在只当成备注。

## 4. 已定的三件事（2026-10-30）

1. **命名空间 token** = 来源包名哈希。重转同一份包得到同一套 id；两个不同包恰好同名才会撞。
2. **存量 Mod**：不做检查、不做就地改写，只在新转换里修。代价是旧 Mod 不重导仍然串味且无提示，
   所以发布说明里必须有一句"旧版转换的 Mod 请重导一次"。
3. **L4N 开关落在 section 级**：保持 `--components merge`（Unit 数仍是 7），靠 B1 的命名 +
   掩码让用户看懂。**这使 B1 从"增强"变成"必需"**——没有 section 名字，section 级开关就还是数字。
   附带一条限制要在界面上说清：材质槽被多部件共用时（62 轮实测
   `Ornament_metal_Underwear` 被 38 个槽引用），关掉一个开关会连带关掉共用同材质的东西；
   这与源侧 `$no_draw` 的粒度完全一致，是忠实行为，不是 bug。

## 5. 工序结论

先 A 后 B。A 是数据正确性问题（贴图串味 = 用户看到的就是错的）；B 是可用性问题（数字看不懂），
改的是产物 schema 与展示，适合在 A 落地后连同重转一起验证。

A 内部顺序：A0 夹具 → A1 派生 → A2 贯通 → A3 落盘 → A4 回归 → A5 发布说明。
B 内部顺序：B1 schema（含命名）→ B2 显示 → B3 识别开关包 → B4 解析掩码 → B5 预设 UI → B6 回归。

## 6. 实施结果（2026-10-30，两笔提交）

| 提交 | 内容 |
|---|---|
| `6fb64f6` | A0–A3：`Hd2AssetId` 可选命名空间、转换器五个派生点收口、`vpk2patch --id-namespace`、管理器按来源包名派生 token、报告回显 |
| `1a954d0` | B1–B5：`sections.json` 升 /3 带名字、树显示名字、识别 `$no_draw` 开关包并剔除解析集合、翻译成掩码写 `components.json`、详情页开关列表 |

### 实测数字

**A（命名空间）** 用两个各带 `hair.vtf`、像素不同的 4×4 夹具：

| | 材质 id | 贴图 id |
|---|---|---|
| 无命名空间（今天的行为） | `0xa9761c4f644c1a0d` | `0xb9da05ba9b15c24d` ← 两个包**完全相同** |
| 有命名空间 | modA `0xa717250916253d6a` / modB `0xaca972b07a39e962` | modA `0xb386d83334c08dfd` / modB `0xb9de803f09ea2911` |

`0xb9da05ba9b15c24d` 正是 eku 与 melty 那两个真包 `Hair` 撞出来的 id——夹具复现的就是线上那个 bug。

**B（组件开关）** 用 `Manuka_JABIS_Zoey.vpk` + 全部 15 个附加材质包：

```
识别 14 个 $no_draw 开关包（第 15 个是"文身关"，见下），全部解析成功、0 个未匹配：
  猫耳 1 节 · 尾巴 1 节 · 光环 1 节 · 泪钻 1 节 · 手环 1 节 · 文胸 1 节
  上衣 2 节 · 鞋子 2 节 · 项链 3 节 · 内裤 3 节 · 美甲 4 节 · 热裤 4 节 · 腿带 4 节 · 外套 6 节
```

关键一条：`猫耳` 与 `尾巴` **各自 1 节**，虽然 `c_white_a_ear` / `c_white_a_tail` 共用同一张
`C_White_A` 底色、算出同一个材质 id。能分开是因为 `sections.json` 现在记的是**逐节**的源材质名
（`WardrobeSectionSourceName`，带材质 id 自校验），而不是"按材质 id 反查到的第一个名字"。

`Manuka_LaFiore` 那套：14 个开关包 = **13 个 `$no_draw`** + 1 个换贴图的（`切换纹身`）。

### 没做到的（明确留白）

1. **换贴图型开关**（`文身关` / `切换纹身`）：它不写 `$no_draw`，而是把 `$basetexture` 从
   `Manuka_body_Tattoo` 换回 `Manuka_body`。section 开关只能"不画"，表达不了"换一张图"。
   今天它被当成普通材质包处理（会产出一个没人引用的材质），下一轮要么做成材质变体开关，
   要么至少报一句"这个开关 HD2 表达不了"。
2. **bodygroup 推导的开关**没有做。merge 模式下配件已并进身躯槽位，几何层面回答不了
   "哪些 section 属于它"；而作者那批 `XXX关` 包本来就是同一批组件的 1:1 枚举（62 轮实测），
   按包识别既准确又不重复。真要做 unit 级开关，得先切到 `--components all`。
3. **开关名还可能带模组名**（`橘雪莉 披风` 而不是 `披风`）：`addonTitle` 没有方括号时我们不知道
   哪一段是模组名。要做就得拿整批 VPK 名求公共前缀——留作打磨项。
4. **UI 没有实机验收**：管理器一直开着，我只能构建到 `tmp\manager-verify\`。开关列表的实际观感
   需要用户重导一次 Manuka 包再看。

## 7. 实机反馈后的第二轮（2026-10-30，提交 `4ff1c4b`）

用户实测报的四件事，根因各不相同，都不是"上一轮漏了测试"能概括的：

| # | 现象 | 根因 | 处置 |
|---|---|---|---|
| 1 | 导入后 patch "自动变成 RE-2310" | `ImportService` 导入含 Unit 的产物后立刻调 `vpk rekey`，而 CLI 缺省目标是 RE-2310 | **不再自动换甲**：转换产物以自定义素体模式存在，日志说明下一步去哪 |
| 2 | 换回自定义素体提示失败 | 目标是自己的素体时，跨护甲链路要"把 Unit 搬到它自己身上" | 用 **Unit 键集合**判恒等目标；命中就把来源 patch + sidecar + components.json 复制进 Overwrite |
| 3 | 换甲后开关全部"指向的 Unit 已不在这份 patch 里" | 掩码是 `(unit, section)`，换甲把 Unit 换到目标护甲的键上了 | `vpk rekey` 用**这次计划的映射**翻译 components.json；管理器优先读生效目录、回退来源目录并逐条校验 |
| 4 | VPK 的素体能被单独删，留下"无头 vpk2patch" | 注册表里没有"素体属于哪个 Mod"这一维 | 删前扫全库 `custom-model.json`，被 VPK 持有的拒绝删除并说明"请移除那个 Mod" |

三条判据值得记下来：

- **恒等目标看键集合，不看名字也不看键**：自定义素体的键是转换时生成的，名字用户可以改，
  键可以复制。只有"两边 Unit 集合相同"能说明"换了等于没换"。
- **翻译不上的开关保留、清空掩码**：名字与材质仍然有用（说明这个部件在新目标上没有落点），
  清空掩码让界面能明说"它现在无处可关"，而不是画一个点了没反应的开关。
- **`vpk2patch/` 是内容层，独立部署**（`ModNodePayloadLayout` 的注释写得很清楚），
  所以"不自动换甲"不会让 Mod 变成摆设——它照样进游戏，只是没有替用户改掉某件护甲。

`VpkCustomModelFile` 落在 Core：详情页要用它把默认目标设成"自己的素体"，护甲映射页要用它
拒绝删除。两边共用一份读法，也才能离线测。

## 8. 第三轮：把"权威 / 派生"这条线摆正（2026-10-30）

用户纠正了我对 VPK Mod 目录的模型，这条纠正比前面所有修补都重要：

| 位置 | 是什么 | 会不会变 | 部署吗 |
|---|---|---|---|
| Mod 根 | 来源 `.vpk` —— 它是"VPK 版 Mod"的身份标记 | 不变 | 不 |
| `vpk2patch/` | **权威事实**：这个 VPK 翻译出来是什么（patch / sections / components / custom-model） | **不变** | 不（除非没选目标） |
| `Overwrite/` | **派生生效版**：vpk2patch + 目标护甲 + 勾选状态 | 每次改设置都重新生成 | 是 |

> ⛔ 已过时（2026-11-08）：这张表的三层关系仍然成立，两处措辞过期 ——
> ① 基底目录现名 **`sectional/`**（老库 `vpk2patch/`，读侧两个名字都认）；"不部署"的前提也改了：生效的是编译产物 `Overwrite/`。
> ② "每次应用都从 `vpk2patch` 重新生成 `Overwrite`" 现在由**编译基座**统一执行：
> `CompileAsync` 从 `sectional/` 完整算一遍（有目标 → 换甲，section 开关随之烘进去 → 材质物化进 `Overwrite/`），
> 不做增量、不做缓存。见 `docs/agent/sectional-compile-base-20261108.md` §2。

所以第 7 节里我做错了两处：

1. 把翻译后的 `components.json` 当成"搬进 Overwrite 的权威表"，界面还优先读 Overwrite。
   **权威表天然属于 `vpk2patch/`**：它描述的是"这个 VPK 有哪些部件开关"，与目标护甲无关。
   现在 `ReadComponentsAsync` / `ReadSectionMapAsync` 都是**权威优先、生效兜底**。
2. "应用"是就地烘 `Overwrite`。就地改会让派生品慢慢偏离权威事实。现在**每次应用都从
   `vpk2patch` 重新生成 `Overwrite`**：换甲 + 烘焙一次做完（`vpk rekey --disabled`），
   因为只有换甲手里有"来源 → 目标"的完整映射。

于是组件卡片（树 + 开关）永远描述**权威侧**的事实，不会因为换过一次目标就"指向的 Unit 不见了"；
`wardrobe.json` 里存的勾选状态也永远在来源键空间，换多少次目标都不会失效。

`vpk2patch/` 里那份 patch 因此真的是"备份 + 权威"，用户改坏了任何设置都能重新生成——
这也解释了为什么"重新导入一次"永远能救回来。

## 9. 收尾（2026-10-30，提交 `ad9a302`）

- 拒绝删除的提示改成能直接照做的一句：`mourningveil_bill 被 <VPK Mod 名> 绑定，请移除对应模组。`
  `ReadOwners` 从"key 集合"变成"key → Mod 名"，名字取 `custom-model.json` 的 `name`，
  缺了退回目录名。
- 顺手修状态不同步：树上勾掉一节，开关列表那一行不会重画（两者是同一份状态的两个视图）。
- 清掉这几轮的死代码：`VpkModWardrobeService.ApplyAsync` / `FindPatchFile`、
  `VpkRekeyOutcome.Mappings`、`VpkSwitchItemViewModel.HasMultipleSources` / `SourceHint`。

全量：Core 839 / Cli 189 / SourceMigration 165（+1 跳过）/ Adaptation 182 / SourceModel 51。




