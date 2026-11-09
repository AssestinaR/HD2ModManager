# 「镂空 + 金属」候选根材质：离线筛查

> 2026-11-09。只读游戏数据，不改任何转换代码、不进游戏。
> 前置：`docs/agent/hd2-shader-inventory.md`（380 个根材质的能力矩阵）、
> `docs/agent/hd2-cutoutmetal-ab.md`（⛔ 已废弃的换根实验）、
> `docs/agent/vpk-cutout-strategies-20261101.md`（A/B/C 三档实证）。

## 一、这次想回答什么

11-07 的能力矩阵说「有 11 个根材质同时具备金属槽与镂空槽」，11-08 试了其中 2 个
（`0xec555165044bb60a` / `0x8d49d5aa14036548`）**都失败**，于是整条线回退。

回退之后留下的是一个**没有被验证过的怀疑**：`alphaclip` 只有镂空、没有金属度。
这次要回答的是更底层的一句：**镂空掩码和金属度到底会不会抢同一个通道？**

## 二、方法（全部可复现）

1. `tools/ShaderMatrix` 重跑能力矩阵：

   ```
   dotnet run -c Debug --project tools/ShaderMatrix -- "<游戏 data>" \
       tools/ref/HD2SDK-CommunityEdition/hashlists/shadervariables.txt \
       E:\tmp\screen\matrix.json E:\tmp\screen\matrix.md
   ```

   本次：material 资产 50,580 个、381 个不同父 id、380 个根材质。

2. **槽名不用猜了。** `hashlists/shadervariables.txt`（4,028 行）里有槽名 → 哈希的对照，
   例如 `OpacityClipMap 0xcbde381b`、`BaseColorMetalMap 0x848ba63b`。
   于是根材质和子材质的每个槽都能直接读成名字。

3. 抽样**游戏本体的真实子材质**（matrix.json 的 `Samples`），提取 toc + 贴图，
   用通道统计判断每个槽的哪个通道真正承载了什么。

## 三、三条关键事实（都有实测数字）

### 3.1 `BaseColorMetalMap` 的 **alpha 就是金属度**

同一张槽取两个样本：

| 样本 | 来源 | RGB 均值 | **A 均值 / 范围** |
|---|---|---|---|
| `0x79c2071a7d93fe84` 的 `BaseColorMetalMap` | 纯金属档 `0x9fc2c6b713891a3b` 的子材质 | 142.7 / 139.6 / 136.8 | **167.4 / [0,255]** |
| `0x131be11cb9a50cf7` 的 `BaseColorMetalMap` | `0xec555165044bb60a` 的子材质（`MetallicOpacity=0`） | 74.0 / 49.8 / 12.4 | **0.0 / [0,4]** |

金属件的 alpha 有真实分布，电介质件的 alpha 恒为零 ⇒
**RGB = 底色，A = 金属度**，与 11-01 的推测一致，这次有数据了。

### 3.2 `OpacityClipMap` 是**独立的槽、独立的贴图**，而且没有 alpha

同一个子材质（`0x131be11cb9a50cf7`）的 `OpacityClipMap`：

```
1024x1024 BC1_UNORM   R=G=B 均值 106.7 [0,255]   A 恒为 255
```

BC1 没有 alpha 通道，掩码走的是**颜色通道**，而且是逐像素的真实分布（0…255 都有）。

⇒ **镂空和金属度分别住两张不同的贴图**：`OpacityClipMap`（灰度）与
`BaseColorMetalMap.alpha`。**结构上没有"抢通道"这回事。**

这条直接推翻了我们原来的心理模型（"金属度住在 alpha 里 → 镂空件的金属全丢"）：
金属度确实住在 alpha 里，但那是**另一张图**的 alpha，不是掩码那张。

### 3.3 四个 `*Opacity` 变量是逐贴图的**权重开关**

`0xec555165044bb60a` 这个根材质声明了：

```
BaseColorOpacity=1  MetallicOpacity=1  NormalCcnormOpacity=1  RoughnessOpacity=1
UseParallax=0  UvOffset=0/0  UvScale=1/1
```

抽样里出现过 `MetallicOpacity=0`、`NormalCcnormOpacity=0.073/0.448`、`RoughnessOpacity=0.646`。
⇒ 它们是乘在对应贴图上的标量。**`MetallicOpacity=0` 就是"这张图不用"**——
11-08 观察到 `0x8d49` 那族"没有真金属度（只有固定 `SpecularTextureless=0.5`）"，
在这个模型下应当先看这些权重，而不是直接判"这族没有金属"。

## 四、候选表：谁把镂空与金属放在**两个独立槽**上

判定口径（比 11-07 的 11 个更宽）：槽名或变量名含 `Opacity`/`Clip` ∧ 含 `Metal`。
共 **23 个**根材质，其中 **9 个**同时具备 `OpacityClipMap` 与 `BaseColorMetalMap`
**两个独立的槽**（`★`）：

| 根材质 | 用例 | 槽数 | 变量 | 槽位 | 备注 |
|---|---|---|---|---|---|
| `0xc6042e3403385d40` ★ | 194 | 4 | 13 | BaseColorMetalMap · EmissiveFStop10IntensityMap · NormalXyAoRoughMap · OpacityClipMap | = SDK `alphaclip+` 根；多一张自发光图 |
| `0xec555165044bb60a` ★ | 154 | 4 | 7 | BaseColorMetalMap · NormalXyAoRoughMap · OpacityClipMap · ParallaxMap | 11-08 候选一（整族不渲染，原因未查明） |
| `0xd84d04634a1e2f60` ★ | 129 | 3 | 12 | BaseColorMetalMap · NormalXyAoRoughMap · OpacityClipMap | **= SDK `alphaclip` 根；槽序与我们现役模板完全一致** |
| `0x8d49d5aa14036548` ★ | 128 | 6 | 18 | + Clearcoat… · ParallaxMap · SpecIri… | 11-08 候选二（该族 `SpecularTextureless=0.5`） |
| `0x29238a857efb2bd3` ★ | 39 | 6 | 22 | 同 `0x8d49` | 同族，默认 `UseParallax=1` |
| `0x2bf04e7c3cbdab76` ★ | 32 | 6 | 23 | 同 `0x8d49` | 同族 |
| `0xa991793474328c30` ★ | 28 | 7 | 28 | + ClothDetailNormal | 多一层布料细节法线 |
| `0xf2681c300e163313` ★ | 15 | 3 | 11 | 同 `0xd84d` | `alphaclip` 同形，用例少 |
| `0x71ed0820a138df90` ★ | 11 | 5 | 10 | + EmissiveMap | 三槽 + 自发光 + 视差 |

其余 14 个把金属放在**变量**里（`Metallic…` 标量）或放在单独的 `MetallicMap`/`Mra` 槽，
但**没有独立镂空槽**，例如 `0xb3567213c20d45f7`（909 用例，镂空在 `NormalOpacity` 的 alpha）、
`0x5a4597ed9ab5f84f`（12 槽 `Mra`）、`0x84d1f2c7e35cad5e`（独立 `MetallicMap`+`RoughnessMap`）。
这些要么属于"不透明度在别处"，要么需要另外确认通道语义，本轮不作为首选。

## 五、结论

1. **结构上没有阻塞。** 9 个候选把镂空掩码与金属度放在两张不同贴图上，互不干扰。
2. **最省的落点是 `0xd84d04634a1e2f60`（SDK `alphaclip` 根）**，理由：
   - 3 个槽，和我们现在 `alphaclip` 模板的槽序完全一致（Normal / OpacityClip / BaseColorMetal）；
   - 它的变量里**没有任何金属开关**（只有 Dirt/Weathering/`OpacityThreshold`/`ResolutionSetting`），
     金属度纯粹由 `BaseColorMetalMap.alpha` 决定 —— 少一个可能踩错的旋钮；
   - 129 个游戏本体用例，是"真实在用"的档位。
3. **历史结论「alphaclip 没有金属度」没有被实验证伪。** 11-08 走的是换根路线，
   而且两次失败的原因都不是"没有金属通道"：一次是整族不渲染（原因仍未知），
   一次是该族 `SpecularTextureless=0.5`（固定高光）+ 综合权重没调。
   「明写出来的 alpha=0 丢掉了金属度」这一条是**我们自己的产物**问题，不是模板能力问题。

## 六、建议的最小实验（不换根材质）

在**现役** `alphaclip` 模板上，只改一处：

> 判成金属的镂空件，把 `BaseColorMetalMap` 的 **alpha 从 0 写成金属度值**
> （`MaterialAppearance.MetallicValue`，现成判据 `matcapIsMetal` 已在用），其余一律不动。

这样做的风险面最小：不换父材质（避开"整族不渲染"这个未知坑）、槽序不变、
`NormalXyAoRoughMap` 的 A（粗糙度）也不动。

离线即可核对：补丁里那张 `BaseColorMetalMap` 的 **A 均值应从 0 变成 ≈245**。
实机只需看一次：金属件有没有反光、镂空边缘有没有因为金属没有漫反射而发白。

## 七、顺带查出一处**回归**（`ebbf215` 引入，需要复核）

用现役代码转 `uv1_black_dress`（源侧 DXT5、alpha 均值 251）时，镂空**没有生效**：

```
#cutout uv1_black_dress: alpha(min=0,max=255,coverage=2.4%,visible=98.5%) meaningful=True
  | wantsTransparency=True alphatest=True hasSource=True opaque=True fullyTransparent=False
  => useAlphaClip=False template=advanced
```

原因是 2026-11-08 21:58 那次「半透明判据加两端阈值」（`ebbf215`）里的这条：

```csharp
public bool IsEffectivelyOpaque => Coverage <= EffectiveOpaqueCoverageMax;  // 0.05
```

`Coverage` = alpha < 250 的像素占比。这块布料的镂空只有 **2.4%** 的像素，于是被判成
「实质不透明」→ 直接关掉镂空、模板退回 `advanced`（它没有 `OpacityClipMap` 槽）。

**影响面（实测）**：

| 模组 | 材质 | `useAlphaClip=True` | `opaque=True` 且 `wantsTransparency=True`（被压掉） |
|---|---|---|---|
| MourningVeil（11 个包全给） | 102 | 74 | **0** |
| Elise | 12 | 10 | **2**（`uv1_black` / `uv1_black_dress`） |

也就是说：**镂空面积小于 5% 的件会被整件判成不透明、丢掉镂空**。薄纱、蕾丝、
小面积镂空花纹都落在这一档。MourningVeil 没受影响（它的镂空件面积都大于 5%），
Elise 的裙子受影响。

**建议判据改法**：不要用"透明像素占比"，改用"**alpha 有没有空间结构**"——

```csharp
// 现在：透明面积够小就算不透明（会误杀小面积镂空）
// 建议：alpha 基本恒定且接近 255，才算"作者根本没在用 alpha 表达形状"
public bool IsEffectivelyOpaque => !IsEmpty && Max >= 250 && (Max - Min) <= 8;
```

原始的意图（作者写了 `$alphatest` 但贴图压根没有透明区）用这条能更准确地命中，
而"有 2.4% 镂空"这种真实的形状不会被误判。这条改动会影响所有模组，需要单独一轮验证。

2. **`0xec555165044bb60a` 整族不渲染的原因仍未查明。** 现在有了一个"正版子材质"
   （`0x131be11cb9a50cf7`，parent = 该根，4 槽 + 7 变量全部有真实贴图）可以逐项 diff，
   比 11-08 那次凭猜测修要靠谱。

## 八、复现用的临时产物

| 内容 | 位置 |
|---|---|
| 能力矩阵（JSON / Markdown） | `E:\tmp\screen\matrix.json`、`matrix.md` |
| 正版子材质（0xec55 / 纯金属档） | `E:\tmp\screen\child-ec55\`、`child-metal\` |
| 抽样的金属/掩码贴图 | `E:\tmp\screen\bcm-mo1\`、`bcm-metal\`、`clip-mo1\` |
| 正版根材质 0xec55（含 103 KB 着色器） | `E:\tmp\holobag\stock-root\` |
