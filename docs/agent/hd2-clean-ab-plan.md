# 重做一次干净 A/B：计划

> 2026-11-08。上一版 A/B（手工替换 `sectional` + `Overwrite` 里的材质 patch、手改 `patches.json` 计数）
> 让游戏**一进就崩**。本文件先定清楚"这一次怎么做到干净"，再动手。

## 一、上一版可能崩在哪（要逐条排除）

| 可能原因 | 为什么可疑 | 这一版怎么避免 |
|---|---|---|
| 手改 `patches.json` 的 `materialCount` / `textureCount` | 清单是管理器部署与结构显示的唯一来源，手写就可能与内容脱节 | 全部产物都由**受支持的管线**生成，不手写任何清单 |
| 手工替换 10 个材质 patch、型号与清单的对应关系靠我自己的脚本推 | 一个下标错位就是"引用了不存在的贴图" | 同上：产出与清单同源 |
| `sectional` 与 `Overwrite` 各放一份同样的材质 patch（重复资产） | 旧版里两处内容相同，但归属标记写的还是旧内容 | 由 `mod rebuild` / `vpk rekey` 决定哪里放什么，不再两头写 |
| 68 个材质换了根材质、贴图从 35 张变 55 张（每张 2048² BC4MB） | 显存 +120 MB/包，若同时启用三个 B 包就是 +360 MB | 只启用一个 B 包；并在报告里写明显存代价 |
| 新档位的槽 3（`ParallaxMap`）留空 | 若该 shader 无条件解引用，可能空指针 | 作为"崩溃时第一个要试的开关"记在文档里 |

## 二、"干净"的定义（这一版遵守）

1. **单一变量**：A 与 B 只有 `--cutout-template` 不同（`alphaclip` / `cutoutmetal`），其余参数逐字相同。
2. **同源产物**：两套产物都由 `hd2mod mod rebuild` 生成 —— 它与管理器 UI 用的是同一条管线，
   清单、sections、组件、换甲结果一次性写全。
3. **可整包回退**：A、B 各自存一份完整的 `sectional` + `Overwrite` 快照，切换 = 整包替换，
   不做"只换其中十个文件"这种局部手术。
4. **先 A 后 B**：A 是基线（应当与你现在看到的一样），先确认"换了管线但没换模板"不崩，
   再上 B。这样崩了也立刻知道是模板还是管线。
5. **离线可验**：出包前逐条核对
   - 每个材质的父材质分布；
   - 金属底色的 `meanLum` / alpha 实测值；
   - **引用完整性**：材质引用的贴图 id 是否都能在本次载荷里找到（A 与 B 做差分，A 能闭环而 B 不能，就是崩因）；
   - 清单里的计数与包里实际条目数一致。

## 三、步骤

1. 重建库索引（`repair library-snapshot --confirm`）——`mod rebuild` 需要它。
2. `mod rebuild`（`HD2_CUTOUT_TEMPLATE=alphaclip`）→ 整包快照 `state-A`。
3. `mod rebuild`（`HD2_CUTOUT_TEMPLATE=cutoutmetal`）→ 整包快照 `state-B`。
4. 离线核对（上面五条），A/B 差分。
5. 安装 A，交给用户进游戏确认"没崩、观感与之前一致"。
6. 用户确认后切 B，看金属。

## 四、这一版明确不做

- 不手写/不编辑任何清单文件（`patches.json`、`components.json`、`sections.json`、`.hd2modmanager-mod.json`）。
- 不在 `sectional` 与 `Overwrite` 之间手工搬运单个 patch。
- 不新增第二个变量（不改 matcap 模式、不改 albedo factor、不改组件合并策略）。
