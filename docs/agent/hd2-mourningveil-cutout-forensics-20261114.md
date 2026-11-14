# MourningVeil 镂空取证与修复记录（2026-11-14）

> 这份文档记录的是**有证据的结论**与**踩过的坑**，不是计划。
> 前置资料：`docs/agent/external/nekotoon-shader-notes.md`（NekoToon 的官方参数文档摘录 + `.vcs` 容器格式）。

## 0. 最终结论（实机确认）

用户上机确认：**项链（细链）、胸挂（月牙）、耳坠（银钟）三者都恢复成 L4D2 的样子，胸部纹身的镂空也在。**

实现方式是**源侧规则本身**：

```
$alphatest / $translucent 声明 ⇒ 用「该材质自己的底图 alpha」在 0.5 处硬裁
```

三件在各自 UV 足迹上的实测裁切量：

| 件 | 材质 | 底图 | 遮罩 id | 裁切量 | L4D2 观感 |
|---|---|---|---|---|---|
| 项链 | `mat_accmetal_necklace` | `Mat_ACCMetal` | `0xb5d6300fb75ca18b` | **38.4%** | 细链 ✓ |
| 胸挂 | `mat_metal_dress_acc` | `Mat_Metal` | `0xb4515b7296dcc9cf` | **3.1%** | 完整 ✓ |
| 耳坠 | `mat_metal1_earings` | `Mat_Metal` | `0xb4515b7296dcc9cf` | **2.5%** | 完整 ✓ |

> 胸挂与耳坠共用同一张遮罩是**正确的**——它们的底图都是 `Mat_Metal`，内容本来就相同。

## 1. 真正的两个根因

### 1.1 遮罩 id 跟着"被改名的贴图名"走（代码 bug，已修）

金属件为了做 F0 平涂，会把 `textureName` 改写成 `<matcap>_f0`。而镂空遮罩的 id 由
`textureName + "_opacity"` 派生 ⇒ **所有共用同一个 matcap 的金属件拿到同一个遮罩 id**，
内容只保留先到的那一份。

实测后果：耳坠 / 胸挂 / 项链三件都挂 `0xbc21456cdb7e0902`，而那张图**逐像素等于
`Mat_ACCMetal` 的 alpha**（平均绝对差 0.0、逐像素一致 100%）——它们是 `Mat_Metal` 的件。
于是裁切位置与件毫无关系，"不该镂空的地方被打出洞"。

**修法**：遮罩 id 跟着**源底图名**（`$basetexture` 裸名）走，不跟派生名走。
见 `SourceMaterialConverter.BuildBaseColorTexture` 里 `maskNameSource` 那段。

### 1.2 分析工具把 V 方向弄反了（工具 bug，已修；**不影响产物**）

`E:\tmp\milfy` 的 UV 足迹光栅化原来用 `row = (1-v)·H`。用"足迹是否落在有画区域"做判据：

| 件 | 旧映射 `(1-v)` | 改正 `v·H` |
|---|---|---|
| 项链 | 26.8% | **61.6%** |
| 胸挂 | 7.9% | **96.6%** |
| 耳坠 | 11.4% | **94.4%** |

对照图：`E:\tmp\mvscan\uvflip-proof.png`（用户确认改正后的 B 图才是对的）。

⚠️ **这个 bug 只影响我的分析统计，不影响产物**：转换器生成底图与遮罩用的是**同一次解码**，
两者方向天然一致。但它让此前所有"按件统计"（alpha 占比、连片空白、mask1 对比……）
全部建立在镜像足迹上，**由此推出的每一条结论都作废**——这是后面绕了很远的根本原因。

## 2. 被移除的自造规则（教训）

在错误统计的驱动下，我一共加了四条"判据"，全部已删除：

| 自造规则 | 当时的"理由" | 为什么错 |
|---|---|---|
| `syntheticBase` ⇒ 不裁 | 合成底色的件遮罩取到了别人的像素 | 治的是症状；真因是 1.1 的 id 问题 |
| 最大连片空白 ≥15% ⇒ 不裁 | 以为"碎块不能裁、整条缝能裁" | 统计量在镜像足迹上算的，方向一改就全变 |
| 声明 `$mask1` ⇒ 用它的 A 通道作裁切源 | mask1 的 A 恒 255 | 同上，且引入新的取源分支 |
| `$alphatest`-only ⇒ 一律不裁 | 由"耳坠/月牙看着是完整的"反推 | 同上 |

**结论：判据必须建立在方向正确的数据上；数据错了，任何阈值都只会"修一件坏一件"。**

## 3. NekoToon 的机器码事实（已核实，可复核）

- `neko_toon_ps30.vcs`（2.1 MB）解出 **576 个着色器排列**，全部可解码；
- **`texkill`（DX9 唯一的丢弃像素指令）出现 0 次** ⇒ **NekoToon 从不丢弃像素**；
- 所以源侧的"镂空"在原理上是**混合**，而 HD2 的 `alphaclip` 是硬裁。两者在
  "零区后面有东西"的件上不等价（这正是我早期误判"耳坠不可能完整"的来源）。
- 复现脚本：`E:\tmp\mvscan\vcs_sm3b.py`；容器格式见 external 文档 §4。

## 4. 可复现的取证工具（都在 `E:\tmp`）

| 工具 | 作用 |
|---|---|
| `milfy vpkvmts <vpk> [过滤] [导出目录]` | 用**生产读取器**列/导出 VMT，打印解析后的键（含 AT/TR/A2C/ATR/A/A2/M1/M2） |
| `milfy vpkvmttext <vpk> <名字>` | 打印某份 VMT 的**原文**（含注释） |
| `milfy tex <vpk> <out> <贴图名...>` | 导出贴图 PNG + alpha PNG，打印格式与 alpha 分布 |
| `milfy cover <模型vpk> <材质vpk>` | 每件 UV 足迹统计（**注意：V 方向已修正为 `row=v*H`**）+ 覆盖掩膜 |
| `milfy depth <模型vpk> <材质vpk>` | 每件自遮挡深度（射线-三角形） |
| `milfy raster <尺寸> ... [flip]` | 逐材质配色渲染；`flip` 从另一侧看（用于确认正/背面） |

> `HD2_CUTOUT_DEBUG_FILE=<路径>` 会让转换器把**每个材质的判据输入**写成 TSV
> （`wantsAlphaTest`/`coverage`/`isMeaningful`/`useAlphaClip`/`resolvedTextureName`/`opacityTexId` 等），
> 这是定位"这条链到底读了什么"的唯一可靠手段 —— `Console` 输出会被 `mod rebuild` 的管线吞掉。

## 5. 待办

- **头冠 / 腰胯小灯笼 / 宝石的光泽**：目前这些件走的是"非金属"路径，`env` / `highlight`
  这类 matcap 名被 `MaterialAppearance.NonMetallicMatcapMarkers` 排除，于是一律按电介质处理
  （哑光）。下一步要按数据决定是否把 `env` 放回金属判定（见后续记录）。
