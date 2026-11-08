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

两套状态目录（各 ~1 GB，20 个文件 = 10 个材质 patch × TOC/GPU）：

- `E:\tmp\ab\state-A` —— `alphaclip`（历史行为）
- `E:\tmp\ab\state-B` —— `cutoutmetal`（镂空 + 金属）

切换：

```powershell
E:\tmp\ab\switch.ps1 -Variant B
```

脚本同时写 `sectional\` 与 `Overwrite\`：前者是来源载荷，后者会顶掉同名 patch，只改一处等于没改。
`patch_0`（换过甲的模型）两套共用 —— 材质 id 按名字派生，两套实测 92/92 完全一致。

回退到测试前：

```powershell
# Overwrite 与 sectional 的原始快照
E:\tmp\ab\state-pristine\
```

## 四、进游戏要看的

1. **金属件有没有出现金属反光**（这是 B 唯一的赌注）。
2. **镂空有没有坏**（蕾丝、镂空花纹还成不成形）。
3. **颜色变体还能不能换**（C1/C2 发色、D1/D2 瞳色）—— 这条在验第二节的第 2 条修法。

## 五、待验证

- 根材质自身带几百 KB～几 MB 的 `gpu_resources`（可能是默认贴图/LUT），换根材质会不会连带改默认外观。
- `BaseColorMetalMap.alpha` 到底是不是金属度 —— B 里给金属件写了 255，进游戏一眼可辨。
- 另外 370 个根材质的槽位语义没验证过，只按名字推断。
