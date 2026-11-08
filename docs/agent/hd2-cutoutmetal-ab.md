> ⛔ **已废弃（2026-11-08 21:50）**：本项目已决定不再走「换根材质拿镂空+金属」这条路
> （根材质 0xec55 整族不渲染；0x8d49 那族没有真金属度），代码改动已整体回退（提交 `0d5b991`）。
> 本文仅作排查过程留档，**不要照着它做实验**。现状见 `docs/agent/hd2-material-rollback-20261108.md`。

# cutoutmetal 档位与「镂空 + 金属」A/B 测试包

> 2026-11-08。前置：`docs/agent/hd2-shader-inventory.md`（380 个根材质的能力矩阵）。

## 一、为什么会有这一档

`alphaclip` 有逐像素镂空但没有金属度、`advanced` 反之 —— 这个"二选一"过去被当成引擎限制。
实测是**模板**限制：游戏数据里有 11 个根材质同时带 `BaseColorMetalMap` 与不透明度/镂空槽。

选中的是 **`0xec555165044bb60a`**（全游戏 154 个材质在用），因为它与 `alphaclip` 的三个槽
**同名同序**：

| 下标 | 槽 | 与 alphaclip |
|---|---|---|
| 0 | `NormalXyAoRoughMap` | 相同 |
| 1 | `OpacityClipMap` | 相同 |
| 2 | `BaseColorMetalMap` | 相同 |
| 3 | `ParallaxMap` | 多出来的（留空） |

也就是说做 A/B 时，槽位语义不需要重新猜，唯一变量就是"用了另一个着色器"。

模板文件 `tools/ref/HD2SDK-CommunityEdition/materials/cutoutmetal.material` 是**从游戏根材质
直接生成的**（parent 指向根材质、槽键按上表、贴图 id 全清），不是手写常量表。

## 二、顺带挖出来的真 bug：派生贴图 id 只看源贴图名

### 现象

MourningVeil B1 的 92 个材质里有 47 个都指向 `mat_metal`。转换器日志里金属件算出来的是
`meanLum=200.0 / alpha=255`（正确的 F0），但包里的那条记录是 `17.4 / alpha=0` —— 布料的底色。
**转换器算得对，落盘被顶掉。**

### 原因

`DeriveTextureId(textureName)` 只由**源贴图名**决定。于是"共用同一张源底图、但外观处理不同"的
材质算出同一个 id，而一个 id 在包里只能占一条记录 —— 谁后写谁赢。

这也解释了更早那次"部署到游戏的包里还是源侧 17.4、转换器却算出 200"的悬案：不是部署链路，
是**包内自相覆盖**。

### 修法：只给"表面被换掉"的材质独占 id

```csharp
var baseColorId = surfaceOverridden          // 金属 F0 / 宝石中性色 / matcap 平涂
    ? DeriveTextureIdFor(textureName, texture.Pixels)   // 名字 + FNV-1a 64 内容指纹
    : DeriveTextureId(textureName);                     // 维持历史行为
```

试过并否掉的两个方案，都记在这里免得再走一遍：

| 方案 | 结果 |
|---|---|
| 全量内容指纹 id | 变体包交集 2 → 0（换发色/瞳色彻底失效）；包体 100 MB → 318 MB（92 个材质各存一份 2048² 底色），画面与"名字 id"完全一样 |
| 按"像素没变就用名字 id"分流 | 同上：`ZeroAlpha` 给所有材质清零 alpha、matcap 合成又几乎人人都有，判据几乎恒为"变了" |

最终只修**真正会打架**的那一类：我们主动把表面换成 F0 / 中性色 / 平涂的材质。
其余材质的 id 与历史行为完全一致，变体包与包体都不受影响。

## 三、A/B 包怎么用

> **第二版（2026-11-08 下午）**：第一版手工替换 `sectional/` + `Overwrite/` 里的材质 patch、
> 并手改 `patches.json` 的计数，结果**游戏一进就崩**。这一版两套产物全部由
> `hd2mod mod rebuild` 生成（与管理器 UI 同一条管线），不手写任何清单、不做局部搬运。

两套**完整**快照（`sectional` + `Overwrite` + `wardrobe.json` + `.hd2modmanager-mod.json`）：

- `E:\tmp\ab\clean-A` —— `HD2_CUTOUT_TEMPLATE=alphaclip`（基线）
- `E:\tmp\ab\clean-B` —— `HD2_CUTOUT_TEMPLATE=cutoutmetal`

切换（整包替换：先清旧产物再拷，避免上一套的残留 patch 被当成现役载荷）：

```powershell
pwsh -File E:\tmp\ab\switch-clean.ps1 -Variant A
pwsh -File E:\tmp\ab\switch-clean.ps1 -Variant B
```

**先 A 后 B**：A 是"换了管线但没换模板"。它若不崩，那么崩溃就与这个档位无关。

离线核对（两套逐项对比，只有一项不同）：

| 项 | A | B |
|---|---|---|
| B1 包里 68 个镂空件的父材质 | `alphaclip`（3 槽 / 11 变量） | **`cutoutmetal`（4 槽 / 7 变量）** |
| 其余 24 个非镂空件 | `advanced` | `advanced`（不变） |
| 平色金属底色（2048²） | sRGB 200 / **alpha 0** | sRGB 200 / **alpha 255** |
| 引用完整性（悬空引用） | 856 | 856（与 A 相同，无新增） |
| 载荷大小 | 620 MB | 620 MB |

那 856 条悬空引用是**两套共有**的（模型自带材质引用游戏本体贴图），A/B 差分没有新增项。

## 四、进游戏要看的

1. **能不能进游戏**（第一版崩在这里；这一版若仍崩，则与模板无关）。
2. **金属件有没有出现金属反光**（B 唯一的赌注）。
3. **镂空有没有坏**（蕾丝、镂空花纹还成不成形）。
4. **颜色变体还能不能换**（C1/C2 发色、D1/D2 瞳色）—— 验第二节的第 2 条修法。

## 五、旧快照（第一版，仅留档）

`E:\tmp\ab\state-pristine`（测试前的原始载荷）、`state-A` / `state-B`（第一版手工拼装，
会崩）。`switch.ps1` 是第一版用的，**别再用于正常链路**。

## 五、待验证

- 根材质自身带几百 KB～几 MB 的 `gpu_resources`（可能是默认贴图/LUT），换根材质会不会连带改默认外观。
- `BaseColorMetalMap.alpha` 到底是不是金属度 —— B 里给金属件写了 255，进游戏一眼可辨。
- 另外 370 个根材质的槽位语义没验证过，只按名字推断。
