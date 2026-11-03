# L4D2 → HD2 转换链路：现状盘点与差距分析（2026-11-03）

> 方法：5 个子代理并行只读审查（VPK 导入 / 材质 / 物理 / HD2 能力边界 / 外部资料），
> 主代理抽查关键断言 + 用 59 份真实转换报告做数据核对。
> 标注：【验证】主代理亲自复核过；【子代理】来自子代理报告；【推断】未验证。

---

## 0. 先用真实数据定住盘子

扫了 `E:\mod` 下 **59 份 `vpk2patch.json`**（真实导入产物）：

| 指标 | 数值 |
|---|---|
| 材质条目总数 | **2259** |
| 模板分布 | `advanced` **1284** / `alphaclip` **975** / `translucent` **0** / `original` **0** |
| 合并（mergedInto 非空） | 115 |
| 警告总数 | 184 |
| 其中 `$translucent` 相关 | **170（92%）** |
| 贴图缺失类警告 | ~10 |
| synthetic-base（底图缺失改合成） | 74 |

两个直接结论：

1. **生产路径只用两个模板**。`translucent` 和 `original` 在真实产物里一次都没出现——它们是实验档位，不是能力。
2. **超过 4 成材质走的是 `alphaclip`（硬裁）**，而它的能力是"逐像素镂空 + 底色"，**没有金属度**。所以"镂空件没有金属光泽"不是 bug，是模板能力的物理边界。

---

## 1. 已确认的真实缺陷（按"能修 / 影响面"排序）

### M【验证·已修正】`ParseTint` 的括号处理不完整 + 不分通道取 max

> ⚠️ **本条的初版结论是错的，这里已改正。** 初版声称"四分量 alpha 让 6 个真实样本里 4 个翻成 surface"——
> 那是我的复算脚本先把 `[]` 剥掉了，而 **C# 代码只剥 `{}`，不剥 `[]`**。用精确复刻的脚本重跑后
> （`tmp/tint-check.py`），真实行为是**两种写法各有各的错**：

`MaterialAppearance.cs:243-263`：只 `Replace("{","").Replace("}","").Replace(","," ")`，然后按空格切，
对每个 token `double.TryParse(NumberStyles.Float, InvariantCulture)`，再对**所有成功解析的值**取 `Max()`。

| 输入 | C# 实际 | 源侧 RGB 意图 | 判定 |
|---|---|---|---|
| `[.9 .9 .9 1]` | 0.9000 | 0.9000 | OK |
| `[.3 .3 .3 .8]` | 0.3000 | 0.3000 | OK |
| `[.2 .2 .2 1]` | 0.2000 | 0.2000 | OK |
| `[.216 .208 .204 1]` | 0.2080 | 0.2160 | 丢首分量（影响小）|
| `[.145 .1402 .1382 1]` | 0.1402 | 0.1450 | 丢首分量（影响小）|
| `[1 1 1 .2]` | 1.0000 | 1.0000 | OK |
| **`[1 0 0 1]`** | **0.0000** | **1.0000** | **纯红 tint 判成"没有 matcap"** |
| **`{.2 .2 .2 1}`** | **1.0000** | **0.2000** | **弱 matcap 判成满强度（alpha 进了 max）** |
| `{.9 .9 .9 1}` | 1.0000 | 0.9000 | alpha 进了 max（此处巧合仍 ≥0.9）|

**两个真实缺陷**：
1. **方括号没剥** ⇒ 首尾 token 解析失败 ⇒ 丢掉第 1 个 RGB 分量。首分量最大时直接归零
   （`[1 0 0 1]` → 0）；其余情况只是略偏小。
2. **花括号写法下 alpha 进了 max** ⇒ 弱 matcap 被当成满强度。

修法必须**同时**做三件事，缺一不可：正确剥离 `[]` 与 `{}`、按位置取**前三个分量**、只接受有限数。
只剥方括号（把 alpha 放进 max）会**制造**初版描述的那个误判；只 `.Take(3)` 仍会丢首分量。

另外 `$matcaplighting`（真实样本里恒为 `0.99`）我们完全没读。

> **方法论教训**：我用来"确证"的脚本没有复刻被测代码的语义（多剥了一种括号），于是把
> "脚本的假设"当成了"代码的缺陷"。凡是拿脚本复算真实数据来支撑结论的，必须先让脚本与被测代码
> 逐分支对齐，再用**真实代码**跑一遍同一组输入。

### A【验证】镂空遮罩取自 matcap 的 alpha，不是源底色的 alpha

`SourceMaterialConverter.cs:1705-1714`

```csharp
var rgba = loaded.Rgba;                 // 对 matcap 承载表面的材质，这里是 **matcap 图像**
var opacityMask = loaded.UseAlphaClip
    ? BuildOpacityMask(textureName, rgba, vtf.Width, vtf.Height, loaded.UseDither)
    : null;
```

- `useAlphaClip` 的**判定**用的是 `baseAlpha`（源底色的 alpha，见 `:1435-1440` 那段刻意"不重新测"的注释）；
- 但**遮罩的内容**用的是 `rgba`，而 `:1434` 已经把它换成了 matcap 图像。

结果：判定"这个件要镂空" → 进 alphaclip → 但遮罩是全不透明的球面图 alpha → **镂空实际不生效**。
这是"同一个变量两种语义"的典型缺陷，**修法很局部**：遮罩永远用源底色那份 alpha（另存一份，或把 `baseAlpha` 的 rgB 保留下来）。

### B【验证】合成的中性底色把 alpha 置 0，被当成"整体半透明"

`SourceMaterialConverter.cs:1310` 用 `SolidRgba(4, 4, grey, grey, grey, 0)` 造合成底色；
`TextureAlpha.IsUniformlyTranslucent => Spread < 3 && Max <= 200` → `0/0` 恒为 true；
`:1528-1533` 于是 `useAlphaClip = true`。

后果：**任何"底图缺失 + 写了 `$alphatest 1`"的材质被强制切 alphaclip**，即使它是金属。
而 `:1305-1306` 的注释写的恰好相反（"这样不会被误判成 alphaclip"）。

修法：合成底色的 alpha 不参与镂空判定（没有源 alpha 就是"无信息"，不是"半透明"）。

### C【子代理】Metal 模式下「syntheticBase + 无 `$matcaptexture`」直接 Failed

`matcapIsSurface` 的表达式里含 `syntheticBase`（`:1354-1357`），进 `:1405` 分支后 `:1411` 判 `matcapTextureName` 为空 → `LoadOutcome.Failed`。
兜底（"退成中性灰、不显示品红"）**只在 Bake 档生效**，生产用的 Metal 档反而失效。

⚠️ 数据核对：59 份报告里 `没有 $matcaptexture` 这条失败**命中 0 次** —— 说明当前库里没触发（大概率因为 `MatCapTexture` 会回落到 `$basetexture`）。所以这是**潜在缺陷，不是当前主因**，优先级下调。

### D【子代理】`$phong` / `$envmap` 全家桶完全没读

`VmtDocument` 一共只读 11 个键（`rg -o '"\$[a-zA-Z0-9_]+"'` 实测）。**零读取**：

```
$phong $phongexponent $phongtint $phongboost
$envmap $envmapmask $normalmapalphaenvmapmask
$selfillum $selfillumtint $detail $nocull $alphatestreference $lightwarptexture
```

而金属判定的**唯一依据**是 matcap 文件名关键词表（`MaterialAppearance.cs:66-100`，都不中 → 非金属）。
这是"用文件名猜"替代"查作者写的意图"。

### E【验证】`$bumpmap` 读了但零消费者 → 所有材质法线都是平面 (128,128,255)

`VmtDocument.cs:65` 有属性，全仓库无调用点。NekoToon 的高质量法线图（BC5）**完全没进包**。
HD2 侧法线通道是逐像素可用的（`advanced` 槽 2 RGB、`alphaclip` 槽 0 RGB），所以这是"能力有、没接线"。

### F【子代理·未验证】物理：作者参数被运行时二次改写

`physbone_compat.lua:637-675`：ω 夹到 `[4,12]`、ζ 夹到 `[0.4,1.2]`，且**用 yaw 的 ζ 覆盖 pitch 的阻尼**（`:667-672`）。
作者精心调的 stiffness/damping 在频段外会被整体替换。

同时 **29 个参数里 18 个完全没被使用**：`along*`×2、`yaw/pitch friction/bounce`×4、`base*`×9；
7 个 flag 位里只有 `0x04/0x08/0x10` 被读，`0x01(flexible)/0x02(rigid)/0x20/0x40` 全不读。
⇒ `has_base_spring`（链根平移弹簧）**完全没实现**，`is_rigid` 与 `is_flexible` 不分。

### G【子代理】物理：杆方向来自"节点连线"，不是 Valve 的"骨骼局部 +Z"

`compat:1710-1721`：方向 = 本节节点 → 下一节节点的世界连线；末节用入射方向；单节链用"锚点 → 本节"。
Valve 的语义是**质点沿骨骼局部 +Z 伸出 length**。合成节点的 local 旋转恒为单位阵（`UnitTransformInfoAppender.cs:26`），
所以源骨朝向**在整条链上从未被导出**（容器只存 3 个 float 位置）。

代价（代码注释自己记的实测）：Lilium 头发相邻 jiggle 骨只差 1.2 源单位（≈2 cm），而 `length` 是 30 单位（≈57 cm）
→ 方向是噪声 → 一节正一节反 → **锯齿/闪电形折弯**。

修法候选：把源骨的局部基（forward/left/up）加进 `HD2SRG01` 容器（v3，记录 148→184 字节）。
⚠️ 但 `compat:879-905 + 2226-2240` 把"净 swing"直接当 local 旋转写回，单位阵是**硬契约**——
改 bind 旋转必须同步改成 `bind · S`，否则整条链固定偏一个角度。

### H【子代理】VPK 导入：任一单元失败 → 整个 patch 消失

`VpkPatchAssembler.cs:501-502`：`diagnostics` 非空就 `return null`。
与材质层"把包写出来、把问题喊出来"的取向矛盾。贴图 id 只按 basename 派生（`Hd2AssetId.cs:65-67`，注释自称"已知隐患"）使撞名并不边缘。

### I【子代理】VPK 导入：管理器从不看 CLI 退出码

`VpkImportService.cs:645-651` 只判空输出。CLI 因 `registryError` 返回 2 时，管理器照常继续 → **静默留下半成品**（patch 有、素体没登记）。

### J【子代理】CLI 单文件转换走的是 Bake 而不是 Metal

`CliApplication.cs:2753-2768` 完全不传 matcapMode → 构造器默认 `Bake`；而管理器导入传 `--matcap metal`。
同一个源包用两条路径转会得到**不同观感**。纯配置不一致。

### K【验证】天气变量只关了 2 个

`alphaclip` 一共 11 个天气/脏污变量，`SourceMaterialConverter.cs:1030-1033` 只写 `DirtGlobalAmount=0`、`WeatheringCoverage=0`。
其余 `DirtAoCoverage` / `DirtDetailMasking` / `WeatheringMasking` / `DirtGradientMax` / `WeatheringVariant` 仍是模板默认 1.0。
用户实测"雪污染已解决"，说明 `DirtGlobalAmount` 确实是总闸——但**把其余 5 个也清零没有风险**，属于廉价加固。

### L【验证】`KnownParentIds` 把 `basic+OLD` 的 id 标成了 `basic+`

`Hd2MaterialAsset.cs:56,64`：`3430705909399566334` 实测是 `basic+OLD`。只影响诊断打印，但说明"表被当成权威"有风险。

---

## 2. 能力边界（不可修的，别浪费时间）

| 想要 | HD2 现实 | 证据 |
|---|---|---|
| 镂空 **且** 金属 | 没有任何模板兼得。`advanced` 有金属度没逐像素不透明；`alphaclip` 反之；`original` 已证伪 | 文档 §6 + `MetalCutoutPolicy` |
| 真正的逐像素半透明混合 | 唯一有混合语义的 `translucent` **没有底色贴图槽**（只能平涂），且军械库不显示 | 文档 |
| 逐像素金属度（部分金属的布料） | 结构上 `advanced` 槽 3 是贴图（可逐像素），但转换器**只写纯色**（`BuildSolidTexture`）⇒ 目前等价于标量 | `SourceMaterialConverter.cs:1137-1142` |
| 双层 UV | HD2 顶点只有一个 `uv` ⇒ matcap 只能烘平均值 | 顶点组件表 |
| 双面渲染（`$nocull`） | 无表达 | — |

---

## 3. 建议的快速修复顺序

第一梯队（局部、可单测、不依赖实机）：
1. **A** 镂空遮罩用源底色 alpha（真 bug，改动 ~20 行）
2. **B** 合成底色不参与镂空判定（真 bug，改动 ~5 行）
3. **J** CLI 单文件路径对齐 Metal 档（1 行）
4. **I** 管理器检查 CLI 退出码（几行，可靠性）
5. **K** 天气变量全清零（几行，加固）
6. **L** 修正 `basic+OLD` 的父 id 表（1 行）

第二梯队（需要设计决策 + 实机验证）：
7. **D** 把 `$phong` / `$envmap` 纳入金属判定，替掉名字猜测
8. **E** 接上 `$bumpmap` → HD2 法线槽（可能有坐标系/绿通道方向问题，需 A/B）
9. **F** 撤回或参数化 `normalize_gains`（物理，需实机对照）
10. **G** 给容器加骨骼局部基（协议 v3，两侧同步）

第三梯队（结构性）：
11. **H** 失败降级粒度从"整包"改到"单材质"
12. `has_base_spring` / `is_rigid` 补齐（物理完整性）

---

## 4. 仍缺的输入

- L4D2 侧权威资料尚未落到 `docs/external/`（下载任务失败一次，未重跑）。
  需要的：`$jigglebone` 参数语义与合法范围、VMT 参数全集、Source 硬限制、NekoToon/L4N 参数表。
- 没有任何"NekoToon 真实 VMT"样本在仓库里（`tmp/` 样本全是 KKS 材质，无 `$matcap` 键），
  所以 `$matcapture` vs `$matcaptexture` 的拼写疑点无法离线确认。
