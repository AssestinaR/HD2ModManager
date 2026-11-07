# 外部权威资料索引

抓取日期：**2026-11-03**（全部条目同批抓取）
用途：为 L4D2 / Source 引擎侧的 QC、VMT、二进制规格提供**有出处**的事实依据。
提炼稿见 [`L4D2-AUTHORITATIVE-FACTS.md`](./L4D2-AUTHORITATIVE-FACTS.md)。

## 任务指定的 5 个来源

| 来源名 | URL | 抓取日期 | 能回答什么问题 | 是否抓取成功 |
|---|---|---|---|---|
| VDC `$jigglebone` | https://developer.valvesoftware.com/wiki/$jigglebone | 2026-11-03 | 四个属性组（is_flexible / is_rigid / has_base_spring / is_boing）各自语义；每个参数的含义与合法范围；约束的角度约定；「质量点沿骨骼局部 +Z 伸出」约定 | ✅ 成功（正文完整） |
| VDC `Skeleton` | https://developer.valvesoftware.com/wiki/Skeleton | 2026-11-03 | 隐式根骨骼；各引擎分支 `MAXSTUDIOBONES`（含 L4D2 = 128）；骨骼层级、joint / attachment / 蒙皮概念 | ✅ 成功（正文完整） |
| VDC `VertexLitGeneric` | https://developer.valvesoftware.com/wiki/VertexLitGeneric | 2026-11-03 | 模型的默认 shader；透明度（`$translucent` / `$alphatest` / `$nocull` / `$additive` / `$alpha`）、`$selfillum`、`$detail` 系列、`$flesh` / `$cloak` 等 | ⚠️ **部分成功**：页面本身可读，但 **`## Reflection` 一节是空的** —— `$phong*` / `$envmap*` 这些键已不在本页（wiki 拆成了独立变量页）。故用下面的「补充页」补齐，未编造 |
| VDC `PBR-2-Source` | https://developer.valvesoftware.com/wiki/PBR-2-Source | 2026-11-03 | PBR-2-Source 是什么工具（Python，koerismo，把 PBR 材质转到 Source；用 Phong + `$envmap` 近似 roughness/metalness）；它支持哪些游戏 | ✅ 成功（**第二次**：首次直取被 Anubis 反爬页拦下，`?action=raw` 变体成功）。⚠️ 内容只有工具简介：**没有** PBR→phong 的具体换算公式，**支持列表里也没有 L4D2**（列的是 HL2:EP2 / Portal / TF2 / Portal 2 / Alien Swarm / GMod / CS:GO / Strata） |
| GitHub `MoRanYue/mdlc` | https://github.com/MoRanYue/mdlc | 2026-11-03 | L4D2 官方 `studiomdl.exe` 的实测/反汇编口径：studio **v49**、`mstudiojigglebone_t` **120 B**（v48 = 140 B）、骨骼上限 **127**（GMod 255）、格式上限 256、每 mesh 65536 顶点、材质表 32768；QC 覆盖清单 | ✅ 成功（README 正文完整）。⚠️ `raw.githubusercontent.com` 走抓取代理回 404，改用本机 `Invoke-WebRequest` 直取源码文件（`src/format.rs`、`src/target.rs`、`src/model.rs`、`src/compile.rs`、`src/mdl_writer.rs`、`src/vvd.rs`），引文均注明文件与行号 |

## 补充来源（同一 wiki / 同一仓库，为补齐 `$phong` 家族与二进制规格而抓）

| 来源名 | URL | 抓取日期 | 能回答什么问题 | 是否抓取成功 |
|---|---|---|---|---|
| VDC `$phong` | https://developer.valvesoftware.com/wiki/$phong | 2026-11-03 | `$phong` 语法与「所有 phong 参数必须齐全」的坑；`$phongexponent` / `$phongexponenttexture` 各通道语义；`$phongboost`、`$phongtint`、`$phongalbedotint`、`$phongfresnelranges`；phong mask 的四种来源与冲突 | ✅ 成功 |
| VDC `$envmap` | https://developer.valvesoftware.com/wiki/$envmap | 2026-11-03 | `$envmap env_cubemap`；`$envmaptint`（**gamma→linear 转换**）；`$envmapcontrast` / `$envmapsaturation` / `$envmapfresnel` / `$envmapsphere`（matcap 式球面图） | ✅ 成功 |
| VDC `$envmapmask`（含 `$normalmapalphaenvmapmask`） | https://developer.valvesoftware.com/wiki/$envmapmask | 2026-11-03 | 高光遮罩贴图；白=全反射；**与 `$phong` 完全不兼容**；`$basealphaenvmapmask` / `$normalmapalphaenvmapmask` 的 alpha 语义与反相规则 | ✅ 成功 |
| VDC `$selfillum`（重定向到 Glowing textures） | https://developer.valvesoftware.com/wiki/$selfillum | 2026-11-03 | `$selfillum` 语义、默认用 base alpha 当遮罩；与 `$translucent` / `$alphatest` / `$phong` 的不兼容；`$selfillumtint` / `$selfillummask`；`$detail`（blendmode 5/6）与 `$emissiveblend` 两条替代路径（含 L4D2 现成材质示例） | ✅ 成功 |
| VDC `$alphatest` | https://developer.valvesoftware.com/wiki/$alphatest | 2026-11-03 | 二进制透明；`$alphatestreference` 阈值（**VertexLitGeneric 默认 0.7**，必须显式写）；`$allowalphatocoverage` | ✅ 成功 |
| VDC `$translucent` | https://developer.valvesoftware.com/wiki/$translucent | 2026-11-03 | 半透明语义；关掉投影像素阴影；与 `$selfillum` / `$alphatest` 的冲突 | ✅ 成功 |
| VDC `$detail` | https://developer.valvesoftware.com/wiki/$detail | 2026-11-03 | `$detail` / `$detailscale`（默认 4）/ `$detailblendfactor`（0..1）/ `$detailblendmode` 0..11 全表 + VertexLitGeneric 的支持限制 | ✅ 成功 |
| VDC `Phong_materials` | https://developer.valvesoftware.com/wiki/Phong_materials | 2026-11-03 | （预期：phong 的完整教学与 exponent 直观说明） | ❌ **失败**：被 Anubis 反爬页拦下（要求 JS 工作量证明）。该页内容**未使用**，与本页相关的结论只取自 `$phong` 页 |
| VDC `$phongexponent` | https://developer.valvesoftware.com/wiki/$phongexponent | 2026-11-03 | （预期：exponent 单页） | ❌ **失败**：无此独立页面（抓取端返回 404）。exponent 的语义改从 `$phong` 页取 |

## 关于「抓不到」的诚实记录

1. **`Phong_materials` 与 `PBR-2-Source` 首次直取**都被 Anubis 反爬页拦住。PBR-2-Source 用 `?action=raw` 绕过了；`Phong_materials` 没找到可用变体，**内容一律未采信**。
2. **`VertexLitGeneric` 页的 Reflection 段是空的**，所以「`$phong*` 属于 VertexLitGeneric 的哪些参数」这一点，本资料库是**通过独立变量页**确认的（各页都标注了 `Shader(s): VertexLitGeneric`），不是从 VertexLitGeneric 页读到的。
3. **PBR-2-Source 没有给出 PBR→Source 的换算公式**，也没有 L4D2 条目。因此「roughness↔phongexponent 的数值映射」在本资料库里**只有方向与区间是有出处的**，具体公式标注为推断（见提炼稿）。
4. **「每顶点权重裁剪阈值」没找到任何权威数值**。mdlc 与 SDK 头文件里能确证的只有：每顶点最多 3 组权重、按权重降序取前 3、权重 ≤ 0 的丢弃、`|Σw − 1| > 1e-6` 时归一化。没有「阈值 0.1 / 1/255 之类」的官方数字，**不编造**。
5. **正文里 wiki 的角标（"in all games since X"）在抓取时被模板剥掉了游戏名**，很多地方只剩 `(in all games since )`。凡因此无法确定分支的，提炼稿里都写「未确定分支」而不是猜。
