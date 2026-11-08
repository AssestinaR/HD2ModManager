# VPK 镂空（alpha）材质的落地方式（2026-11-01）

> 起因：用户报「镂空材质在军械库里**隐形**、会被环境中的着色器污染」。
> 这份文件记的是**查到了什么**与**还没定的是什么**，不是结论。

## 1. 先看参考模组：它根本没做材质镂空

样本 `【4.28赠品】Chiffon-LUNALICE替换RS-6`（一个能正常显示蕾丝/荷叶边/发丝的角色替换包）。

`9ba626afa44a3aa3.patch_0` 共 47 个条目：**27 unit / 4 material / 16 texture**。
整具角色只有 4 个材质：

| 材质 | 父模板 | 槽数 | 说明 |
|---|---|---|---|
| `0x82303887…` | `advanced` | 11 | 布 + 金属，只覆写 3 张贴图，其余 8 槽继承游戏贴图 |
| `0xee9af19a…` | `advanced` | 11 | 同上 |
| `0x15980b58…` | `0x8f669f365f24594e` | 13 | 游戏本体的**角色/皮肤材质**（不是模板） |
| `0xadaceb34…` | `0x8f669f365f24594e` | 13 | 同上 |

`0x8f669f365f24594e` 不在 SDK 的 `Global_MaterialParentIDs` 里。把槽位哈希反查
`tools/ref/HD2SDK-CommunityEdition/hashlists/shadervariables.txt` 才认出来：
它是 `SubsurfaceOpacity` / `ClearcoatRoughnessAoSpec` / `BloodTiler` / `WoundNormal` /
`WoundData` / `WoundDerivative` 这一套——游戏的角色皮肤材质，模组**继承**它。

**关键实测：两张 4096² 底色的 alpha 通道全是 0～4**（解 BC7 → R8G8B8A8，16.7 M 像素）；
16 张贴图全是 `BC7_UNORM_SRGB`，没有一张 BC4/单通道遮罩。

=> 这个模组的蕾丝、荷叶边、发丝**全部是真实几何**，材质层面一个 alpha 都没用。
这解释了两件事：它永远不会隐形，也永远不会被环境着色器带跑——因为它从头到尾
没离开过 `advanced` 这条 PBR 通路。

## 2. `alphaclip` 在 HD2 里其实是「隐形着色器」

扫 `HD2ModManager\mods` 下**全部** patch 的材质父模板：

```
128  advanced
 76  alphaclip
  7  0x8f669f365f24594e（角色材质）
  4  alphaclip+
  6  emissive   ...
```

76 个 `alphaclip` 的来源：

| 来源 | 数量 |
|---|---|
| 【前置MOD】隐藏舰桥NPC头部 | 40 |
| 【材质版】隐藏物品合集（平板 / 样本容器 / 民主官头 / 腰间平板 / 舰长 / 阴影） | 20 |
| `lunalice_bill-b1/b2`（**我们自己转的**） | 16 |

**60 / 76 来自「隐藏某某」模组**——那些模组的全部目的就是让东西消失。
解一份（隐藏舰长）的材质：3 个槽全部指向游戏贴图，11 个变量全是模板默认值。
它做的就是「把目标材质换成 clip 着色器」这一个动作。

唯一一个拿 `alphaclip+` 做衣服的模组（`VRchat_Milltina 可脱穿 材质球-衣服-*`）：
每个衣服材质都配了一对——一个 `advanced`、一个 `alphaclip+`，`OpacityThreshold`
保持模板默认 **0.144**。那是「穿上 / 脱掉」的衣柜开关，不是蕾丝镂空。

而**所有** mod 里的 `advanced` 都是 `OpacitySharpness = 1.0` / `OpacityOffset = 0.0`，
`SclarFieldOpacity` 指向两张共享游戏贴图之一——没人拿它做硬边镂空。

## 3. 槽位与变量对照（查这一节就够）

`alphaclip`（3 槽 / 11 变量）：

| 槽 | 键 | 名字 |
|---|---|---|
| 0 | `0x1d57dcf3` | `NormalXyAoRoughMap` |
| 1 | `0xcbde381b` | `OpacityClipMap` |
| 2 | `0x848ba63b` | `BaseColorMetalMap`（底色 + 金属度共用一张） |

`alphaclip+`（4 槽）：多了 `0xca6f2cf1` `EmissiveFStop10IntensityMap`。
`OpacityThreshold` 默认 **0.144**（`alphaclip` 是 **0.5**）。

`advanced`（11 槽）：

| 槽 | 键 | 名字 |
|---|---|---|
| 0 | `0xa72cb013` | `BloodTiler` |
| **1** | **`0x479fb1ef`** | **`SclarFieldOpacity`** ← 唯一的不透明度来源 |
| 2 | `0x4c567810` | `Nar`（RGB 法线 / A 粗糙度） |
| 3 | `0x90b84a53` | `IlluminateData`（金属度） |
| 4 | `0xe719da57` | `DetailNormalLeather` |
| 5 | `0xe67ac0c7` | `AlbedoEmissive`（底色，A = 自发光） |
| 6 | `0x736a0029` | `WoundNormal` |
| 7 | `0x04fb61ad` | `DetailNormalPorcelain` |
| 8 | `0xd2f99d38` | `WeatheringSpecial` |
| 9 | `0xf8e31d7b` | `WoundData` |
| 10 | `0xa59f5e11` | `WoundDerivative` |

相关变量：`OpacitySharpness` `0xcb8c5728`（模板默认 1.0）、
`OpacityOffset` `0xccddd706`（默认 0.0）、`OpacityThreshold` `0x529a4aaf`。

游戏角色材质 `0x8f669f365f24594e` 把 `SubsurfaceOpacity` 槽配
`OpacitySharpness = 64` + `OpacityOffset = -0.5` —— 效果是
`saturate((mask - 0.5) × 64)`，过渡区几乎为零，**这才是硬边镂空**。

## 4. 我们的现状与三档假设

`SourceMaterialConverter.TryLoad` 的判据是「VMT 写了 `$alphatest`/`$translucent`
**且** 贴图 alpha 真在表达形状」→ 换到 `alphaclip` 模板。这条路的四个疑点：

1. `alphaclip` 只有 3 个槽、**没有任何 PBR 输入**（没有 `Nar`、没有 `IlluminateData`、
   没有 LUT）。切过去等于把材质降级成最小着色器 → 怀疑这就是「被环境着色器污染」。
2. `BaseColorMetalMap` 的 alpha 被 `ZeroAlpha` 清零。在这个模板里底色和金属度共用一张图，
   金属度住在 alpha 里 → 镂空件的金属全丢。
3. `OpacityThreshold` 留在模板默认 **0.5**。源侧蕾丝 alpha 大量落在 0.2～0.4 →
   一刀切下去整片被裁光 → 怀疑这就是「隐形」。
4. `NormalXyAoRoughMap` 填的是平面法线 → 镂空件没有法线细节。

对应三档（`--cutout a|b|c`，见 `CutoutStrategy`）：

| 档 | 模板 | 关键参数 | 保住 PBR？ | 依据 |
|---|---|---|---|---|
| **A** | `alphaclip` | `OpacityThreshold` 0.5 | ✗ | 历史行为 |
| **B** | `alphaclip` | `OpacityThreshold` 0.144 | ✗ | SDK `alphaclip+` 默认值；Milltina 现役模组原样保留 |
| **C** | `advanced` | 遮罩→`SclarFieldOpacity`，`Sharpness` 64 / `Offset` −0.5 | ✓ | 游戏角色材质实测；**但没人这么用过，属推测** |

A 与 B 只差一个浮点数，能干净地回答「是不是阈值切太狠」。
C 是唯一保住 PBR 的方案，但**必须实机验证**。

### 一个刻意的不变量

**镂空档位不进材质身份串**（`BuildMaterialIdentity` 只有 `|alphaclip`）。
理由：它和 `MatCapMode` 一样是转换器的**全局模式**，不是材质属性。
同一份源材质无论用哪一档转，都必须是同一个 id——否则「只重转某一个材质包」
会让 id 全变一遍，而模型 patch 里引用的还是旧 id，材质静默失效。
`SourceMaterialConverterTests.CutoutStrategyDoesNotChangeMaterialIdentity` 守着这条。

## 5. 测试包与判据

`artifacts/cutout-test/` 下三档，**只换一个文件**：

```
E:\mod\lunalice基础材质b1服装色白\vpk2patch\9ba626afa44a3aa3.patch_0
```

同目录的 `.gpu_resources`（67 MB）三档与现用文件 **SHA256 完全一致**，不要动。
这个目录在游戏里以软链接生效（data 目录下的 `patch_16` 指向它），
所以覆盖文件即可，不用重新导入 / 部署，只需重启游戏。

> ⛔ 已过时（2026-11-08）：那是当时拿软链接指着**基底目录**做单文件实验的临时做法。
> 现行口径：基底目录（2026-11 改名 `sectional/`）是**只由转换链产出、谁都不许碰**的权威事实，
> 生效层只有 `Overwrite/`（编译基座产物）。要做单文件实验，请在 `Overwrite/` 那份产物上做。
> 见 `docs/agent/sectional-compile-base-20261108.md` §1.2。

已核对：
- A 档产物与现用 patch **SHA256 完全一致**（说明命名空间 `vpk-060d479e4bc677d0`
  与那次导入完全对得上）；
- B / C 只有那 8 个镂空材质的 TocData 不同，条目键与 gpu 偏移全部一致；
- C 档填的 4 个槽（遮罩 / 平面法线 / 共享 PBR / 底色）全部指向本包内的贴图。

看 8 个材质对应的部位：`lace_white01_*`（猫耳 / 裙摆 / 头饰 / 颈饰 / 内衣 / 袖口 /
背后缎带，最显眼）、`headdress_white01_lace_*`、`lower_white01_lace_*`、
`shoes_white01_lace_*`、`shoes_02_lace`、`dress_white01_lace_*`、`glove_white01_lace_*`。

## 6. 待办

- [ ] 用户实机看三档，定一档；定完产品侧只留一档，`--cutout` 退化成诊断开关。
- [ ] 若三档都不对：下一批假设是「镂空必须做成几何」（参考模组的做法），
      那这条就该从"材质转换"降级成"报告出来让作者自己建模"。
- [ ] `BaseColorMetalMap` 的 alpha 该写金属度而不是 0（B 档没动它，因为蕾丝是布；
      金属件镂空时才要紧）。
- [ ] `NormalXyAoRoughMap` 目前是平面法线，源侧 `$bumpmap` 在镂空路径上被丢掉了。

## 7. 排查用的一次性脚本（`tmp/`，可删）

`vpkpeek.py`（读 L4D2 VPK）、`matpeek.py`（解 HD2 材质 TocData）、
`texpeek.py`（读贴图头）、`shaderhash.py`（槽位/变量哈希反查）、
`patchscan.py`（扫全库父模板分布）、`opacityscan.py`（扫不透明度槽与参数）、
`modmat.py`（按 mod 名导出材质）、`patchdiff.py` / `verify_variants.py`（对拍）。

---

# 追加（2026-11-01 晚）：实机结果与后续修复

## 8. 三档镂空实验的结论：此路不通

用户实机结果：

- **A ≈ B，看不出区别** —— 阈值 0.5 与 0.144 没有任何可见差异。
- **C 完全不可取** —— 那些件仍然是不透明的。

C 为什么必然无效，后来查清了：现役模组里 `advanced` 的 `SclarFieldOpacity` 槽装的是
**1×1 `R8_UNORM` 标量**（实测 Crimson Kitty 的 `0xc0b94ae8509a1bd4` 与 `0x812b33c5a3ebdb86`
都是 1×1 R8）。它是**整体不透明度标量**，不是逐像素遮罩，根本没有能力做镂空。
=> `CutoutStrategy.AdvancedOpacity` 已删除，`--cutout c` 现在会明确报错并说明原因。

A≈B 说明阈值不是那个杠杆。剩下的可能性（未验证）：`OpacityClipMap` 的采样通道或极性
与我们写的 `(a,a,a)/A=255` 不一致。**这条线暂时停在这里**，不再投入。

## 9. 金属材质丢失：根因是「没有 $basetexture 就被判失败」

用户报 `serenade_bill` 金属材质丢失。实测报告里 20 个材质全部是：

```
源材质 silver / matel2 / jem_white / gem_1 / cross / chain_nail … ：
  VMT 里没有 $basetexture
```

打开这些 VMT 一看，它们是 **NekoToon 着色器的金属 / 宝石件**，作者把 `$basetexture`
**注释掉了**，观感 100% 来自 `$matcapture`：

```
NekoToon {
    // $basetexture "matchacheese\Serenade\Accessories_3"     ← 被注释
    $matcap 2
    $matcaptexture "matchacheese\Serenade\Matcap_metal7"
    $matcaptint "[1 1 1 1]"
}
```

源侧渲染的其实就是「底色 = 0 + matcap × tint」。**这是合法写法，不是坏材质。**
旧代码判失败，于是整个金属件退回外壳护甲的材质 —— 这就是「丢失」。

### 修法

`SourceMaterialConverter.HasNoRealBaseTexture(vmt)` 统一认出两种"没有真底色"：

1. `$basetexture` 缺失；
2. `$basetexture` 是占位值 `"basetexture"` / `"base"`。

有 `$matcapture` 兜底时**合成一张纯黑 4×4 底色**（`VtfFile.Synthetic` + `SolidRgba(0,0,0,0)`），
并把 `matcapIsSurface` 无条件判真（底色不存在，观感只可能来自 matcap，与 tint 多大无关）。
没有 matcap 兜底时仍然失败，理由写清"观感来自 envmap / phong，HD2 模板没有对应槽"。

`ComputeMatCapMeans` 也同步放开：以前只算 `tint ≥ 0.9`，现在"没有真底色"的也算，
否则这些件烘不出颜色，只能是纯黑。

### 实测效果

| 包 | 修复前 | 修复后 |
|---|---|---|
| `serenade基础材质a1主体` | 7 材质 / **21 警告** | **30 材质 / 0 警告** |
| `mourningveil基础材质b1服装色黑` | 21 材质 / 1 警告 | **27 材质 / 0 警告** |

## 10. 鞋跟颜色错误：身份串漏了 matcap

`mourningveil` 的这三件共用 `Mat_Metal` 底色：

| 材质 | matcap | tint |
|---|---|---|
| `mat_cloth_shoes` | `Matcap_highlight1` | .3 |
| `mat_leather_shoes` | `Matcap_cloths4` | .2 |
| `mat_metal` | `Matcap_cloths4` | .2（与上一件 VMT 逐字相同） |

旧的 `BuildMaterialIdentity` **只在 `tint ≥ 0.9` 时**才加 `|matcap:<贴图>`，
于是 `mat_cloth_shoes` 与 `mat_leather_shoes` 算成同一个 id，
装配器按 id 去重「先到先得」——其中一件拿到别人的 matcap。这就是「鞋跟上有颜色错误」。

修法：**只要 VMT 声明了 matcap 就把 matcap 名与 tint 都写进身份串**，判据是纯 VMT 属性
（不看"队列里有没有模型能算平均色"），可复现。逐字相同的 VMT 仍然合并（那是对的）。

实测修复后：`mat_cloth_shoes` = `0xa9828971…`、`mat_leather_shoes` = `0xa8c2c4bf…`，
不再撞号。

### ⚠️ 代价：材质 id 变了，NekoToon 系的包都要重导一次

凡是带 matcap 的材质 id 都会变（多了 `|matcap:…|matcaptint:…`）。
旧的 id 本来就在产生错误颜色，所以这是修 bug 的一部分，不是副作用；
但**已有的 VPK 版 Mod 必须重新导入**才能拿到新材质。

### 残留疑点

`mat_metal` / `mat_metal1` / `mat_metal_shoes` 三件在报告里共用 `0xa3dd4af0…`
（= `mat_metal_shoes` 的 metal11 @ .9 那个身份），而 `mat_metal` 的 VMT 明写 `cloths4` @ .2。
用 b1 单独作材质来源复现同样结果，所以不是跨包解析的问题。
这三个源文件本身可疑：`mat_metal.vmt` 与 `mat_metal_shoes.vmt` 都只有 3508 字节、
开头是截断的乱码注释、结尾断在 `// 边�`；`mat_metal1.vmt` 更是 3502 字节的纯二进制垃圾。
**下一步**：确认这三个 VMT 在源 VPK 里是不是真的损坏（换一个读取器交叉验证），
再决定是"源侧问题、报出来"还是"我们的名字索引串了"。这很可能也是用户说的
「mourningveil 金属材质不太正确」。

## 11. 半透明材质能不能做：三个模板都不够

把 SDK 里三个跟不透明度有关的模板都查了一遍：

| 模板 | 逐像素不透明度 | 底色 | 结论 |
|---|---|---|---|
| `advanced` | ✗ `SclarFieldOpacity` 是 **1×1 R8 标量** | 有贴图槽 | 只能整体调不透明度 |
| `alphaclip` | ✓ `OpacityClipMap` | 有贴图槽 | **只能硬裁**（clip，不是 blend） |
| `translucent` | ✓ `NormalXyRoughnessOpacity`（RG=法线 XY、B=粗糙度、A=不透明度） | **只有 `BaseColor` 纯色三元组，没有底色贴图槽** | 只适合颜色均匀的透明体 |

=> **「带图案的逐像素半透明」在现有 10 个模板里做不到。** 能做的是：

1. **颜色均匀的透明件**（宝石 / 玻璃 / 水）：`translucent` + `BaseColor` 取源底色平均色。
   可行，但需要一个"底色是否近似纯色"的判据，且**零实机证据**（全库 0 个模组用它）。
2. 有图案的布 / 蕾丝：只能硬裁（`alphaclip`）或者不透明。
3. 或者做成几何（参考模组的做法）。

`translucent` 这条要做的话，是一个独立的、需要实机 A/B 的实验，不该和金属修复混在一起。

---

# 追加二（2026-11-01 晚·2）：官方 wiki 的答案 + 金属发黑 + 两个测试包

## 12. 官方 modding wiki 把三件事一次说清了

[HD2 Modding Wiki / Materials](https://boxofbiscuits97.github.io/HD2-Modding-Wiki/dev/materials/overview.html)
逐模板列了能力与代价，关键三条：

| 模板 | 关键 Pros / Cons |
|---|---|
| `advanced` | Pros: Metallic texture / Strongest Emissive / **Visible in selection screens**；Cons: **No transparency** / No environmental overlay |
| `alphaclip` | Pros: Transparency；Cons: **Not visible in selection screens** |
| `translucent` | 允许 alpha mask（hair / decals）；Cons: **Not visible in selection screens** |

推论（都很硬）：

1. **"镂空材质在军械库里隐形"是引擎行为，不是我们的 bug。** `alphaclip` / `translucent`
   的件在选择界面里本来就不画。之前那次 A/B/C 三档对照如果是在军械库看的，
   等于在一个根本显示不了这种材质的地方做判断 —— 这也是 A≈B 最可能的解释。
   ⇒ 镂空必须**进任务**看。
2. **`advanced` 根本没有透明度**，与实测"`SclarFieldOpacity` 是 1×1 R8 标量"完全对上。
   C 档死因至此双重确认。
3. 逐像素半透明只有 `alphaclip`（硬裁）与 `translucent`（软混）两条，
   两者都不在军械库显示；`translucent` 还没有底色贴图槽（只有 `BaseColor` 纯色）。

## 13. 金属发黑：matcap 当底色 + 金属度顶满 = 暗金属

用户反馈"金属终于能显示了，但几乎没有反光、太黑了"。

机制：matcap 是**加法高光图**，最亮那一块才是反光，整图平均亮度远低于它。
而管理器导入时用的是 `--matcap albedo`（见 `VpkImportService` 的固定参数），
即**把 matcap 直接当底色**；底色在 PBR 里就是金属的反射色。底色偏暗 + 金属度 ≈1
⇒ 暗金属，几乎不反光。

物理上更对的近似：**底色取金属的反射色（偏亮），高光交给 PBR 算。**
为此加了 `--matcap-gain <x>`（默认 1.0），两种 matcap 模式下都生效：

- `albedo` 模式：`MaterialAppearance.ScaleRgb(rgba, gain)`；
- `bake` 模式：`BakeMatCap(rgba, mean, tint × gain)`。

### 一个重要的验证

用与管理器**完全相同**的参数（`--components merge --matcap albedo`）重跑 serenade a1，
`gain=1.0` 的产物与用户正在用的 patch **逐字节相同**（patch `F95CDDBEFDB3C663…`、
gpu `1B356DE296E7DFBE`）。所以增益测试的基线是忠实的，不是近似。

四档增益（1.0 / 1.6 / 2.4 / 3.2）的 `patch_0` **全部相同**，只有 `gpu_resources` 变
⇒ 换一档只需换一个文件，不用重导。

## 14. 遮罩通道/极性四档

`--cutout` 现在有四档遮罩约定（阈值那条 A≈B 已证明不是杠杆）：

| 值 | 遮罩写法 |
|---|---|
| `a`（默认） | RGB=(alpha,alpha,alpha)、A=255（历史行为） |
| `rgb-inv` | 同上但取反 |
| `alpha` | RGB=255、A=alpha |
| `alpha-inv` | RGB=255、A=alpha 且取反 |

依据：HD2 的贴图文档写"Alpha 是整体透明度，0 全透明、255 全不透明"
（[Textures](https://boxofbiscuits97.github.io/HD2-Modding-Wiki/dev/textures/overview.html)），
所以着色器很可能采样 `.a` —— 而我们一直把 A 写成 255，等于遮罩没生效。

四档的 `patch_0` 逐字节相同（`EDE59C6A1C88EC04`），只有 gpu 变。
**需要先重导 lunalice**（身份串改了，旧 id 接不上），然后四档之间只换文件。
**必须在任务里看**，理由见 §12。

## 15. mourningveil 亮黄色鞋跟：仍未定位，但拿到了硬证据

用户反馈重导后"鞋跟仍然是一个亮黄色"。没修好，但查清了两件事：

1. **该 VPK 里有三个 VMT 是坏的**（`mourningveil基础材质b1服装色黑.vpk`）：

   | 条目 | 大小 | 内容 |
   |---|---|---|
   | `mat_metal.vmt` | 3508 B | 头部从 UTF-8 序列中间开始（截断），尾部断在 `// 边` |
   | `mat_metal_shoes.vmt` | 3508 B | 同上，头尾都被截断 |
   | `mat_metal1.vmt` | 3502 B | **纯二进制垃圾**（`92 24 00 00 00 00 aa aa aa aa 01 00 49 92 24` 循环） |

   VPK 树本身是干净的：104 个条目、偏移 0..45333239、**零重叠**、正好铺满数据段。
   所以不是读错位置，是这三份文件在源侧就是这样。

2. `mat_metal` / `mat_metal1` / `mat_metal_shoes` 三件在报告里共用一个材质 id
   （= `mat_metal_shoes` 的 metal11 @ .9 那个身份），而 `mat_metal` 的 VMT 明写 `cloths4` @ .2。
   用 b1 单独作材质来源复现同样结果，不是跨包解析。

   下一步：把这三份字节与"同一作者的其他包里的同名 VMT"逐字节比，看是"作者打包时截断了"
   还是"我们的名字索引串了"。这很可能就是"亮黄色鞋跟"与"金属不太正确"的共同来源。

## 16. 半透明：结论

- 带图案的逐像素半透明：**现有 10 个模板做不到**（`advanced` 无透明度；
  `alphaclip` 只能硬裁；`translucent` 无底色贴图槽）。
- 颜色均匀的透明件（宝石 / 玻璃 / 水）：`translucent` + `BaseColor` 取源底色平均色，
  理论可行，**但全库 0 个模组用过，且不在军械库显示**。要做的话是一个独立的实机实验。
- 无论哪条，**都不在选择界面显示**——验收必须进任务。

---

# 追加三（2026-11-01 晚·3）：金属发黑的真正根因是 F0，不是亮度

## 17. 量出来的事实

把用户现用 patch 里 silver 材质的底色（槽 5，= `matcap_metal7`）解出来量：

| 项目 | 值 |
|---|---|
| 尺寸 | 128×128 BC7_UNORM_SRGB |
| 平均 sRGB | 106（三通道 106/106/107） |
| **线性 F0** | **0.14** |
| 亮度分布 | p10=15 / p50=66 / p90=231 / p99=255（**双峰**） |

在 PBR 里金属的底色**就是**它的镜面反射率 F0（银 0.97 / 铝 0.91 / 铁 0.56）。
我们给的是 0.14 —— 一块"反射率 14% 的金属"，自然又黑又没反光。

根因不只是"值偏暗"，而是**语义搞错了**：这张 matcap 是"暗底 + 亮边"的高光图，
源侧是**叠加**在受光表面上的，不是表面颜色。把它当底色 = 把高光图案当成金属颜色。

## 18. 为什么"乘增益"救不回来（实测换算）

| 增益 | 底色平均 sRGB | 线性 F0 | 被压成纯白的像素 |
|---|---|---|---|
| 1.0 | 106 | 0.14 | 0% |
| 1.6 | 138 | 0.25 | 32% |
| 2.4 | 162 | 0.38 | 38% |
| 5.0 | 212 | 0.66 | 64% |

高光那一半先撞顶 ⇒ 严重非线性，靠乘法到不了 0.97。

## 19. 新增 `MatCapMode.Metal`

丢掉 matcap 的**亮度图案**，只保留它的**色相**，整片抬到目标 F0
（`SourceMaterialConverter.MetalF0 = 0.90`，`--matcap-gain` 作为它的乘数）：

```
out_rgb = LinearToSrgbByte(F0 × gain) × hue(matcap 平均色)
out_a   = 0
```

实测：`--matcap metal` 后底色 meanRGB=(242,242,244) → **F0 = 0.89**，且整片均匀。

同时 `MaterialAppearance` 新增 `LinearToSrgbByte` 与 `LevelToMetalF0`。

### 关键不变量

`--matcap metal` 与 `--matcap albedo` 的 **patch_0 逐字节相同**（F95CDDBEFDB3C663…），
只有 `gpu_resources` 变。因为金属档不改贴图 id、不改尺寸、不改材质槽位——
所以它是**纯换图**，四档之间互换不会引发任何 id 问题。

## 20. 四档测试包

`artifacts/metal-test/`，每档一个 `gpu_resources` 文件：

| 档 | 模式 | 实测 F0 | 亮度分布 |
|---|---|---|---|
| A 现状 | albedo，gain 1.0 | 0.14 | p50=66 p90=231（双峰） |
| B 提亮 | albedo，gain 2.4 | 0.38 | p50=159 p90=255（顶部糊） |
| C 金属 | metal，F0 0.89 | 0.89 | 均匀 |
| D 金属 | metal，F0 0.96 | 0.96 | 均匀 |

A 的 gpu 与用户现用文件 **SHA256 完全一致**（1B356DE296E7DFBE…）。
四档 patch_0 全同（F95CDDBEFDB3C663…）、gpu 大小全同（14.9 MB）。

判据：如果 C/D 像金属了 ⇒ F0 就是杠杆，把它做成默认，并考虑把 matcap 改用作
粗糙度或自发光（而不是底色）；如果 C/D 还是黑的 ⇒ HD2 的 advanced 没走环境反射，
需要去查它的着色器输入。

---

# 追加四（2026-11-01 18:15）：实机定档 + "整片消失"的根因

## 21. 实机结果：D 档（金属 F0 0.96）

用户实测四档：**C/D 特别像金属，D（银，F0 0.96）非常合适**。

=> `SourceMaterialConverter.MetalF0` 已改成 **0.96**，
`VpkImportService` 的默认参数由 `--matcap albedo` 改为 `--matcap metal`。
两种模式的贴图 id 与材质 id 完全相同，所以切换不破坏已有映射。

## 22. "袖套/丝袜/臂环整片消失"的根因：把 `$translucent` 当成了硬裁

用户现象：serenade 的袖套、丝袜、臂环**完全看不见**（不只是军械库，外部也没有），
只剩手掌与小臂附近一点袖口。

### 决定性证据

1. 全库扫 serenade 的 121 个 VMT，透明声明只有三种组合：

   | 组合 | 数量 | 例子 |
   |---|---|---|
   | 都没有 | 100 | 大多数 |
   | **只有 `$alphatest`** | **18** | `lace_white_*`（蕾丝，硬裁语义正确） |
   | **只有 `$translucent`** | **3** | `_1__white`（袖套/丝袜）、`_1__white_nail` |

   **消失的正是那 3 个 `$translucent`。** `$alphatest` 的 18 个（蕾丝）没问题。

2. `_1__white` 的 alpha 是 **UV 图集里的形状遮罩**（解 DXT5 alpha 块后打覆盖图，
   能看到 4×2 的岛屿，岛屿内 alpha=255，岛外为 0，`alpha>=128` 占 30.9%）。
   我们发出的 `_1__white_opacity` 覆盖图与源侧**一模一样**，所以遮罩本身是对的。

3. 但把这张遮罩按**阈值 0.5 硬裁**，实测裁掉 **58.8%** 的像素；
   换阈值 0.144 也只降到 56.8% —— 这就是为什么之前 A/B 两档"看不出区别"：
   **阈值从来不是杠杆**。而且那次是在军械库看的，而官方 wiki 明确写着
   `alphaclip`/`translucent` 的件**不在选择界面显示**（见 §12），所以那次对照本来就不成立。

### 语义差在哪

| 源侧 | 含义 | HD2 能表达吗 |
|---|---|---|
| `$alphatest 1` | 硬裁（低于参考值丢弃像素） | ✅ `alphaclip` 语义一致 |
| `$translucent 1` | **alpha 混合**（半透明） | ❌ `advanced` 无透明度；`alphaclip` 只能硬裁 |

把"混合"当"硬裁"，等于把"半透明"读成"低于阈值就删掉" → 大片表面被删。

### 修法

`VmtDocument` 拆出 `WantsAlphaTest`（`$alphatest`）与 `WantsTranslucency`（`$translucent`）：

- 只有 **`$alphatest`** 才切 `alphaclip`；
- 只有 `$translucent` 的材质**退成不透明**（`advanced`），并在报告里留一条 `Notices`
  警告说明"半透明无法表达，已退成不透明"——不静默。

实测（重转 serenade）：

```
a1  mats=30  warnings=1  → _1__white（advanced）+ 提示
b1  mats=19  warnings=1  → _1__white_nail（advanced）+ 提示
    lace_white* 仍然全部是 alphaclip（硬裁保留）
```

### ⚠️ 这条改动的风险面

`$translucent` 不是个别现象（源 VPK 里的 VMT 统计）：

| 作者族 | `$alphatest` | `$translucent` |
|---|---|---|
| lilium_sancta | 70 | 0 |
| lunalice | 39 | 60 |
| mourningveil | 114 | 96 |
| serenade | 18 | 3 |

也就是说 lunalice / mourningveil 各有大量 `$translucent` 材质，这次改动
**也会把它们从"硬裁"变成"不透明"**。对 serenade 这是修 bug（袖套回来了）；
对另外两族可能是"镂空消失"的回归 —— **必须各自重导一次确认**。

## 23. 测试变更

- 删掉旧的闸门 `RealData_SharedBaseTextureStillYieldsBothAnOpaqueAndACutoutMaterial`
  （它的前提"`$translucent` 也当硬裁"已作废）。
- 新增 `AlphaTestAndTranslucencyAreDifferentIntentions`（纯逻辑，钉住两种意图能分开）。
- 新增 `RealData_TranslucencyFallsBackToOpaqueWithANotice`（真实数据上必须 advanced + 有提示）。

---

# 追加五（2026-11-01 20:00）：与发布版二进制的正式 A/B

## 24. 先纠正我此前的错误结论

上一轮我把"两代导入并存"当成根因，**这是错的**：

1. 用户指出两套导入流程**命名空间完全隔离**，新旧产物互不通用 —— 所以它们不会互相覆盖成"缺材质"。
2. 用户更早就在**没有发布版 2 号产物**的时候报过缺失（那时库里只有测试版），所以与并存无关。
3. 补丁号大的本来就会覆盖补丁号小的。

教训：不该在没做 A/B 的情况下从"目录里有 `_2` 后缀"推出机制。

## 25. 正式的 A/B 方法（以后固定这么做）

- 发布版二进制：`artifacts/publish/HD2ModManager-win-x64-20261101-131435407-requires-dotnet/bin/hd2mod.exe`
  版本串 `1.0.0+754ad25…` ⇒ 对应提交 **754ad25**（2026-10-30 21:54）。
- 材质转换器最后一次提交：**9a41b43**（2026-10-30 17:13）。
- ⇒ 发布版 = 我所有材质改动**之前**的状态；我的改动全部未提交，A/B 是干净的。
- 做法：同一个输入、**同一组参数**（`--material-source / --id-namespace serenade-ab / --components merge`，
  不带 `--matcap`、不带 `--cutout`），分别用两个二进制产出，再逐项对比。

## 26. A/B 结果（这是实测，不是推断）

| 对比项 | 结果 |
|---|---|
| Unit 条目数 / TocData 大小 / gpu 大小 | **完全相同**（7 条，40838/39816/…/33476 全等） |
| 每个 unit 的 `materialBindingCount` | **完全相同**（70/69/32/24/8/40/12） |
| 60 个 section 的 `numIndices` | **完全相同**（没有任何 section 被隐藏） |
| 材质父模板 | 只有 2 处变化：`_1__white`、`_1__white_nail`：alphaclip → advanced |
| 其余材质 | 20 个从"转换失败"变成"成功产出"，其余逐个逐槽比对**内容一致** |
| 悬空贴图引用 | **0 处**（每个非占位槽都能在本批里找到贴图） |

## 27. 唯一一处真正影响"消失部位"的差异

按 `sections.json` 逐 section 对比材料归属，**23 / 60 个 section 的材料变了**，
形态全部是：**旧构建这里引用的 materialId 在报告里根本找不到（转换失败，等于悬空）→ 新构建有了真材质**。

例如：

| section | 旧 | 新 |
|---|---|---|
| unit0006 sec1 | （悬空，无材质） | `silver_glove` advanced |
| unit0007 sec1 | （悬空，无材质） | `silver_armacc` advanced |
| unit0004 sec3 | （悬空，无材质） | `silver_garter` advanced |
| unit0006 sec4 | `_1__white_nail` **alphaclip** | `_1__white_nail` **advanced** |

也就是说：**过去这些配件是"没有材质"的，靠引擎回退到外壳护甲的材质才看得见；
现在它们有了自己的材质**。用户看到的"消失"，发生在这之后。

## 28. 已经排除的方向

- ❌ 不是 section 被隐藏（numIndices 全等）
- ❌ 不是 unit 结构差异（TocData 大小、binding 数全等）
- ❌ 不是悬空贴图（0 处）
- ❌ 不是材质槽位错乱（逐槽比对一致）
- ❌ 不是两代导入并存（命名空间隔离 + 时间线反证）

## 29. 待确认（下一步唯一的岔路口）

同一批 section 里混着两类材质，它们的"消失"机制完全不同：

1. **金属类**（`silver_glove` / `silver_armacc` / `silver_garter`）：
   没有 `$basetexture`，走合成底色 + 金属 F0。若这类消失，嫌疑在金属通路。
2. **布料类**（`_1__white` / `_1__white_nail`）：
   模板从 alphaclip 改成 advanced（`$translucent` 那条修正）。若这类消失，嫌疑在镂空/不透明通路。

需要用户确认：**消失的那几件里，看得见的是不是只有金属件？还是连白布件也一起没了？**
在拿到这个之前不再改代码——避免又一次凭猜测动刀。

---

# 追加六（2026-11-01 19:52）：布料消失的根因确认并已回退

## 30. 定位

用户确认：**只有布料不见了，金属件都在。**

而 `$translucent` 恰好是**只有布料才用**的声明（serenade 全库 121 个 VMT 里只有 3 个：
`_1__white` 袖套底布、`_1__white_nail` ×2）。金属件用的是 `$alphatest` 或干脆没有透明声明。

⇒ 我那条"`$translucent` 退成不透明"的改动，命中的正是"只有布料"这个集合。**根因确认。**

## 31. 为什么"改成不透明反而不见了"（我原来的推理错在哪）

我以为 `$translucent` 的 alpha 只是"软过渡"，把它当硬裁会删掉 58.8% 的像素，所以不透明更好。
**错了**：这些件的 alpha 图案不是多余花纹，而是它们**唯一**的形状来源。
做成 alphaclip 时，遮罩把不属于该件的 UV 区域裁掉，形状才对；
做成不透明以后，整片 UV 区域都画出来，反而露出的全是错的区域 —— 看起来就像整件消失了。

换句话说：`$translucent` 在 Source 里是"混合"，但**在 HD2 里能用的近似只有硬裁**，
不透明是"更糟"的那一档，不是"更安全"的那一档。

## 32. 已回退

`useAlphaClip` 改回 `vmt.WantsTransparency && baseAlpha.IsMeaningful`（= 发布版行为）。
`WantsAlphaTest` / `WantsTranslucency` 的区分保留，但只用于**报告措辞**，不再影响模板选择。

## 33. 回退后的对照验证

| 检查 | 结果 |
|---|---|
| 与发布版模板不一致的材质 | **0 个**（回退前是 2 个） |
| `_1__white` / `_1__white_nail` 模板 | 两边都是 **alphaclip** |
| `_1__white_opacity` 遮罩像素 | **逐字节相同**（4 MB，SHA256 一致） |
| 布料 albedo 贴图 | 同一个 id、同一份内容 |
| 金属件 | 30 / 19 / 19 个材质全部产出（发布版是 7 / 10 / 10） |
| 真实失败 | **0** |

也就是说：**凡是发布版能做对的材质，现在逐个模板一致、遮罩逐字节一致**；
在此之上多出来的是发布版做不了的 20 个金属件。

## 34. 保留下来的改动（这次没有被否定）

- `MatCapMode.Metal` + `MetalF0 = 0.96`（用户实机四档对照选出的银档）
- 管理器默认 `--matcap metal`
- 没有 `$basetexture` 时合成底色（修"金属材质丢失"）
- matcap + tint 进材质身份串（修 mourningveil 鞋跟串色）
- metal 模式拿不到 matcap 贴图时不判失败（`jem_red` 的 `m7`）

## 35. 教训（写给下次的我）

1. **凡是"新版坏了旧版好"，先用发布包里的 `hd2mod.exe` 做同参数 A/B，再动代码。**
   发布包路径固定，二进制就在里面，这是最便宜也最可靠的取证手段。
2. 不要把"源侧语义"直接推成"HD2 该怎么做"。`$translucent` 的语义（混合）和
   HD2 能表达的东西（硬裁）不是一回事，而"看起来更安全"的近似（不透明）实测更糟。
3. 一次只改一个变量。这一轮把 5 个改动一起上，导致定位花了三轮。

---

## 5. 2026-11-02 更新：`BaseColorMetalMap` 的 alpha **不是**金属度（实机证伪）

第 4 节第 2 条曾断言「`BaseColorMetalMap` 是底色 + 金属度共用一张图，金属度住在 alpha 里」。
**这条是从槽位名字推出来的，没有实机验证过。现在验证了，结论是错的。**

做法：把 MourningVeil 的金属件（`mat_metal*` / `mat_glovemetal*` / `mat_accmetal*` 等
26 个 `alphaclip` 材质）的 `BaseColorMetalMap.alpha` 从 `0` 改成 `255`（= 金属度 1.0），
其余一律不动，实机对照。

结果：**金属观感完全没变化**。同一批改动里另一处（均匀半透明 → 抖动透明）在同一份
补丁上**肉眼可见地生效了**（玻璃鞋跟变透明），所以补丁确实被游戏加载了 ——
也就是说 `alphaclip` 的 `BaseColorMetalMap.alpha` 不参与金属度计算。

=> **`alphaclip` 确实没有可用的金属度输入**（第 2 节原来那句「没有任何 PBR 输入」是对的）。
第 4 节第 2 条与"待办"里对应的那条可以划掉了。

### 由此确定的结构性限制

`advanced` 有金属度（槽 3 `IlluminateData`）但没有逐像素不透明度
（`SclarFieldOpacity` 是 1×1 标量，已由 A/B/C 三档证伪）；
`alphaclip` 有逐像素镂空但没有金属度。

=> **"镂空 + 金属"在 `advanced` / `alphaclip` 里无法共存，必须二选一。**

### 下一步要验的假设

NekoToon 的装饰件贴图是一张**图集**，alpha 里那 60%+ 的透明区域很可能只是"图集空白"，
而网格 UV 本来就只落在不透明区。若是这样，镂空对它们**是冗余的** ——
那么"金属件一律走 `advanced`（保住金属度，放弃镂空）"就是正确的通用策略。

验法：把 MourningVeil 的金属件改成 `advanced`，实机看有没有露出整块 UV 方片。

- 没露 → 采用"金属优先"，问题解决。
- 露了 → 得换路子：游戏本体角色材质 `0x8f669f365f24594e`（13 槽）用
  `SubsurfaceOpacity` + `OpacitySharpness=64` / `OpacityOffset=-0.5` 做硬边镂空，
  且带完整 PBR。那才是"镂空 + 金属"唯一可能共存的地方，但槽位表还没整理。

### 附带发现：`重构建` 不会刷新 `vpk2patch.json`

实测 MourningVeil 的 `.patch_0` 时间戳是 22:02（新代码重构建过），但同目录的
`vpk2patch.json` 还是 12:19 的旧报告。报告与产物不一致会让"这次到底改了没有"没法判断
——排查时被它误导过一次。

> ⛔ 已过时（2026-11-08）：这条已经修掉——`VpkRebuildService` 换完载荷会重写 `vpk2patch.json`
> （只读属性临时清掉、写完补回），并顺带重建 `patches.json`。现在报告与产物是同步的，
> 这条"误导"不再成立。

---

## 6. 2026-11-02：读本体角色材质，拿到 `original` 模板的完整表

用户报"所有镂空材质在雪地会被雪图层覆盖"。为了找原因，直接读了游戏本体那份 13 槽材质
（`material 0x8f669f365f24594e`，归档 `18235e0c9ec0e636`，TOC 1200 字节），
并用 `gamedata extract` 把它和几个默认贴图都提出来了。

### 6.1 它的父模板是 SDK 里的 `original`，不是"未知材质"

扫 `tools/ref/HD2SDK-CommunityEdition/materials/*.material`（按槽位键的 u32 字节模式匹配），
`original` 命中了 `WeatheringSpecial` / `SubsurfaceOpacity` / `SclarFieldOpacity` /
`ClearcoatRoughnessAoSpec` / `EmissiveColor` / `AlbedoIridescence` / `TextureMap02` /
`DetailNormal1` / `Normal` —— 和那份 13 槽材质逐一对上。
**所以它就是我们能用的模板**（父 id `6101987038150196875`），不必"发明"。

### 6.2 13 个槽位（`original`）

| # | 键 | 名字 |
|---|---|---|
| 0 | `0x7ca0d044` | `ClearcoatRoughnessAoSpec` |
| 1 | `0xc985395a` | `EmissiveColor` |
| 2 | `0xa72cb013` | `BloodTiler` |
| 3 | `0x479fb1ef` | `SclarFieldOpacity` |
| 4 | `0xdf3ee984` | `DetailNormal1` |
| 5 | `0xcaed6cd6` | `Normal` |
| 6 | `0xd2f99d38` | `WeatheringSpecial` |
| 7 | `0xe7bd9019` | `SubsurfaceOpacity` |
| 8 | `0xd47db28b` | `TextureMap02` |
| 9 | `0xff2c91cc` | `AlbedoIridescence` |
| 10 | `0x736a0029` | `WoundNormal` |
| 11 | `0xf8e31d7b` | `WoundData` |
| 12 | `0xa59f5e11` | `WoundDerivative` |

### 6.3 `SclarFieldOpacity` 确认是**标量**（数据层面的定论）

它在游戏本体材质里默认指向 `0x12d4692531c1fd35`，提取出来实测是
**1×1、`R8_UNORM`、单字节**（`gpu_resources` 只有 1 字节）。
名字里的 "Sclar" 就是 scalar。

=> `advanced` 靠这个槽**做不了逐像素镂空**。第 4 节的 C 档（`advanced` + `SclarFieldOpacity`）
失败是必然的，不是调参问题。这条彻底钉死。

### 6.4 雪污染的原因：`alphaclip` **一个天气槽都没有**

同一个扫描给出的模板/槽位矩阵：

| 模板 | 逐像素镂空 | 金属度 | 天气/雪掩码 |
|---|---|---|---|
| `advanced` | ✗（`SclarFieldOpacity` 是标量） | ✓ `IlluminateData` | ✓ `WeatheringSpecial` |
| `alphaclip` | ✓ `OpacityClipMap` | ✗ | **✗ 完全没有** |
| `alphaclip+` | ✓ | ✗ | ✗ |
| `armorlut` | ✗ | ✗ | ✓ `WeatheringSpecial` / `WeatheringDirt` / `BurnScorchSnowMeltGlobalWetness` |
| **`original`** | **✓ `SubsurfaceOpacity`（待验）** | ✗ | **✓ `WeatheringSpecial`** |

`alphaclip` 只有 `NormalXyAoRoughMap` / `OpacityClipMap` / `BaseColorMetalMap` 三个槽，
**没有任何天气/雪/脏污输入**。所以雪图层在它上面**没有任何掩码可以挡**——这就是
"地表的雪出现在角色身上的镂空材质上"的直接原因。而 `advanced` 的件因为有
`WeatheringSpecial`（默认是一张 512×512 的 BC7 掩码图），所以不受影响。

### 6.5 结论与下一步

- **「镂空 + 金属 + 抗雪」三者不可能同时拿到**：没有任何模板同时有
  `OpacityClipMap`/`SubsurfaceOpacity` + `IlluminateData` + `WeatheringSpecial`。
- 但 **`original` 同时有「逐像素不透明度」和「天气掩码」** —— 它严格优于 `alphaclip`
  （`alphaclip` 两样都没有）。
- 所以下一步应该把**镂空件的模板从 `alphaclip` 换成 `original`**：
  预期能同时拿回「镂空」和「抗雪」，代价仍然是没有金属度。
  待验：`SubsurfaceOpacity` 是否真是逐像素（它默认指向 4×4 占位图，
  但占位图不代表槽位语义）；以及它需不需要配合 `OpacitySharpness`/`OpacityOffset`
  （本体这份实例里两个都是 0，不是文档里说的 64/−0.5）。

### 6.6 相关变量（本体那份实例的默认值，供参考）

`OpacitySharpness` 0 · `OpacityOffset` 0 · `SubsurfaceDiffusion` 0.7 ·
`SubsurfaceWrap` 0.14 · `SubsurfaceThickness` 0 · `SssIntensity` 0 ·
`ClearcoatIntensity` 1 · `ClearcoatRoughness` 0 · `MicroAoIntensity` 0 ·
`WeatheringTiling` 3 · `WoundPaintingEnabled` 0 · `WoundTileScale` 8 ·
`WoundParallaxScale` −0.1 · `BloodRoughness` 0.15 · `BloodSubsurface` 0.5 ·
`IThickness` 0.47 · `IStart` 0.18 · `IEnd` 0.71 · `IIntensity` 0.57 ·
`EmissiveIntensity` 0 · `DetailMixWeight` 0 · `BurnScorch` (0,0) ·
`BloodColor0` (0.3587, 0.2329, 0) · `BloodColor1` (0.4239, 0.0638, 0)
