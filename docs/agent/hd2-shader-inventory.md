# HD2 材质能力矩阵（游戏里到底有哪些着色器可用）

> 复现：`dotnet run --project tools/ShaderMatrix -- "<游戏 data 目录>"`（只读游戏包，不写任何游戏文件）。
> 本文数字取自 2026-11-07 的本机游戏数据。

## 一、结论摘要

1. **可用着色器是 380 个，不是 10 个。** SDK 只随包发了 10 个 `.material` 模板（`advanced` / `alphaclip` / `original` / …），
   而游戏数据里有 50,580 个 material 资产、10,121 个不同 file id、**381 个不同父 id，其中 380 个是根材质**。
   转换器一直只在 3 个里挑（`advanced` / `alphaclip` / 少量 `original`）。
2. **「镂空 + 金属度不能共存」是 `alphaclip` 的局限，不是引擎的局限。** 有 **11 个** shader 同时具备
   `BaseColorMetalMap`（金属）与不透明度/镂空槽，覆盖 1,643 个用例；其中 `0x8d49d5aa14036548` 还额外带清漆、视差和虹彩。
3. **L4D2 那批材质的视觉要素几乎都有对应物**：清漆 21 个、绒毛（丝绒/布）9 个、次表面 70 个、Ramp 26 个、
   虹彩 14 个、调色板/LUT 238 个、贴花/图案 27 个。
4. **没有 MatCap shader，也没有各向异性 shader**（各 0 个）。所以 matcap 只能继续烘进底色/金属度，
   不能指望运行时采样 —— 这条我们之前踩过的坑有了明确解释。
5. **不能自己写着色器。** 根材质的 `parent` 是 0，即链条终点；引擎按根材质 id 绑定编译好的着色器变体。
   我们可以在链条中插入自己的材质层、可以任意挑根材质、可以造贴图/LUT/贴花，但造不出新的着色器变体。

## 二、结构：材质资产是一条链

```
unit ──引用──> 材质实例 (.material)  ──parent──> 根材质 (.material, parent=0)  ──(引擎内部)──> 编译好的着色器
                    ↑ 我们可以造                    ↑ 只能从这 380 个里挑              ↑ 造不出来
```

材质资产的 TocData 只有三段数据：`父材质 id (u64)` / `槽键 (u32) → 贴图 id (u64)` / `着色器变量值`。
三段都是纯数据，没有代码。因此「自定义材质」＝ 选一个根材质 + 填这三段，**不需要任何引擎改造**。

`KnownParentIds`（SDK 常量表）里的 10 个名字，就是 380 个根材质里最常用的 10 个。

## 三、能力分布（380 个根材质）

| 能力 | 判定依据 | shader 数 | 覆盖用例 |
|---|---|---|---|
| 不透明度 / 镂空 | 槽名或变量含 `Opacity` | 128 | 11,805 |
| 金属（贴图槽） | 有 `BaseColorMetalMap` 槽 | 29 | 6,172 |
| 金属（变量） | 变量含 `Metalness` / `Metallic` | 42 | 7,333 |
| **金属 + 不透明度同材质** | 两者同时满足 | **11** | **1,643** |
| 清漆 Clearcoat | 变量含 `Clearcoat` | 21 | 1,401 |
| 绒毛 Fuzz（丝绒/布） | 变量含 `Fuzz` | 9 | 4,785 |
| 次表面 Ss / Subsurface | 变量含 `SsDiffusion` / `Subsurface` | 70 | 14,573 |
| Ramp（渐变/卡通用） | 变量含 `Ramp` | 26 | 5,082 |
| 自发光 | 槽/变量含 `Emissive` | 186 | 15,311 |
| 调色板 / LUT | 槽/变量含 `Palette` / `Lut` | 238 | 31,743 |
| 贴花 / 图案 | 槽含 `Decal` / `Pattern` | 27 | 5,029 |
| 细节法线层 | 槽含 `Detail` | 103 | 25,751 |
| 视差 Parallax | 槽/变量含 `Parallax` | 30 | 1,087 |
| 虹彩 Iridescence | 槽/变量含 `Iridescen` | 14 | 532 |
| 伤口 Wound | 槽/变量含 `Wound` | 12 | 770 |
| 天气/脏污 Weathering | 槽/变量含 `Weathering` | 91 | 25,030 |
| MatCap | — | **0** | 0 |
| 各向异性 Aniso | — | **0** | 0 |

## 四、关键候选（按我们已知的痛点排）

| 根材质 | 用例 | 槽位 | 能解决什么 |
|---|---|---|---|
| `0x8d49d5aa14036548` | 128 | `BaseColorMetalMap` `ClearcoatNormXyIntensityRoughnessMap` `NormalXyAoRoughMap` `OpacityClipMap` `ParallaxMap` `SpecIriIntensityIriThicknessMap` | **镂空 + 金属 + 清漆 + 视差 + 镜面虹彩**同材质。`ScalarfieldOpacitySharpness` 可调硬边 |
| `0x29238a857efb2bd3` | 39 | 同上 | 同上，但默认 `UseParallax=1`，并带天气侵蚀变量 |
| `0xec555165044bb60a` | 154 | `BaseColorMetalMap` `NormalXyAoRoughMap` `OpacityClipMap` `ParallaxMap` | 最朴素的 **PBR + 镂空**；额外有 `BaseColorOpacity`/`MetallicOpacity`/`NormalCcnormOpacity`/`RoughnessOpacity` 四个逐通道不透明开关，以及 `UvScale`/`UvOffset` |
| `0xb3567213c20d45f7` | 909 | `BaseColorMetalMap` `NormalOpacity` `Wear` | 不透明度塞在法线图 alpha 里，再叠 `WearAmount` 磨损。护甲上用得最多的那一档 |
| `0x2d75cc65d25ef565` | 741 | `Tex0` `Tex1` `Tex2` | **布料/丝绒档**：`FuzzIntensiy`/`FuzzColor`/`FuzzSpeculer` + `ClearcoatIntensity` + `SsDiffusion`/`SsThickness` + `OpacityThreshold` |
| `0x3f8549c59ce6d71d` | 820 | `BaseColorMetalMap` `NormalXyAoRoughMap` | 干净 PBR 金属，无镂空 |
| `0x9fc2c6b713891a3b` | 2,735 | `BaseColorMetalMap` `NormalXyAoRoughMap` | 同上，用例最多的一档 |

## 五、对转换器的含义

- **镂空件不再需要「进 alphaclip 就丢金属度」**，也不必再去裁网格：把父材质换成
  `0x8d49d5aa14036548` / `0xec555165044bb60a` 一档，镂空与金属度可以同时存在。
- 判断依据应当从「模板名」迁移到「根材质有没有那个槽」——`Hd2MaterialTemplateLibrary.HasTextureSlot`
  已经是这个形状，只是候选表要从 10 个扩到 380 个。
- 布料/皮肤/自发光各自有更贴切的档位（`0x2d75cc65d25ef565`、带 `Ss*` 的一组、`emissive`），
  不必全都塞进 `advanced` 再用变量硬凑。
- MatCap 必须继续烘（没有 MatCap shader），这条维持现状。

## 六、边界与风险

- 380 个根材质里，只有 10 个是 SDK 明确命名并验证过的。其余 370 个的**槽位语义**（哪个通道放金属、
  哪个放粗糙度）要靠实测确认，不能只按名字推断。
- 根材质自身也带 `gpu_resources`（几百 KB 到几 MB），里面可能是它引用的默认贴图/LUT；
  换根材质可能连带改变默认外观，必须 A/B 对比。
- 同一 file id 在多个归档里有副本（数据补丁会覆盖），本工具取 `TocData` 最大的那份；实机加载的是优先级最高的那份，
  两者是否一致需要在具体案例上验证。
- 这份工具只回答「有哪些」，不回答「哪个好看」。后者仍需我们在游戏里逐个看。

## 七、待验证：根材质声明了几个变量才算数

同名的 SDK 模板与游戏根材质，变量表并不一致：

| 名字 | SDK 模板 | 游戏根材质 |
|---|---|---|
| `advanced` | 11 槽 / 42 变量 | 12 槽 / **77** 变量 |
| `alphaclip` | 3 槽 / 11 变量 | 3 槽 / 12 变量 |
| `basic+` | 3 槽 / 10 变量 | 3 槽 / **3** 变量 |

两个方向都要弄清楚：

- `advanced` 的根材质多出 35 个变量 —— 这些是我们**从没接过的开关**，是能力增量。
- `basic+` 的根材质只声明 3 个变量，而 SDK 模板照旧写 10 个（`Wetness`、`DirtGlobalAmount`、`WeatheringVariant` …）——
  如果引擎只认根材质声明过的 id，我们那些写入就是**静默无效**的。这能解释一部分「改了参数没反应」。
  验证方式：拿一个根材质声明数明显少于 SDK 模板的档位，只改一个 SDK 独有变量，看画面/像素是否有变化。

