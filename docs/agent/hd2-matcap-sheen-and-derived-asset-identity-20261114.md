# matcap 光泽映射 与 派生贴图身份（2026-11-14）

> 姊妹文档：`docs/agent/hd2-mourningveil-cutout-forensics-20261114.md`（镂空取证与遮罩 id）。
> 本文记录两件事：① 金属/宝石"光泽"的正确映射；② 一类反复出现的 bug（派生贴图身份）及其标准化方案。

## 1. L4D2 到底怎么做出"黑亮的金属"

NekoToon 的官方参数文档（`nekotoon.vmt`，见 `external/nekotoon-shader-notes.md`）写明：

```
$MatCap  1  // 0禁用，1正片叠底Multiply，2相加Add, 3Normal, 4Screen
$MatCapTexture / $MatCapTint / $MatCapLighting
$mask1: r=matcap蒙版, g=lightwarp UV的V, b=matcap2蒙版, a=AO   （未打包通道默认纯白）
```

MourningVeil 的装饰件**全部写 `$matcap 2` = 相加**，即：

```
最终颜色 = 底色(uv) × $color + matcap(normalUV) × $MatCapTint
```

**关键点：matcap 的 UV 来自法线球面坐标（`u=n.x*0.5+0.5, v=0.5-n.y*0.5`），不是底图 UV。**
一件饰品只覆盖法线球面上极小一片 ⇒ 对这件而言，这笔加法几乎是**一个恒定颜色**。

实测（`milfy mcapmask`，模型 + 材质归档）：

| 件 | matcap | 法线覆盖整张 matcap 的面积 | 采样均值 RGB | 合成后亮度 mean/max |
|---|---|---|---|---|
| `mat_accdiadema_tiara` | `matcap_env3`（房间照片） | 1.0% | 11/11/10 | **12 / 152** |
| `mat_accmetal_tiara` | `matcap_metal11` | 0.3% | 20/17/16 | 132 / 220 |
| `mat_accpreal_tiara` | `matcap_highlight` | 0.2% | 18/18/18 | — |
| `mat_metal_gem_dress_acc` | `matcap_jewel9`（绿宝石） | 0.2% | 4/122/54 | **69 / 246** |
| `mat_metal_gem_earings` | `matcap_jewel9` | 0.01% | 3/113/56 | 46 / 123 |

⇒ "**特别黑亮的金属**"= 近黑底色 + 稀疏高光，而不是"整片亮银"。

## 2. 我们的映射（已实现）

| 源侧 | HD2 |
|---|---|
| 表面由 matcap 承载（`$matcaptint ≥ 0.9` 且底色无自有色） | **金属度给足**，不再按名字里的 `jewel`/`gem`/`env`/`highlight` 判成电介质 |
| 底色 | `base + matcap(法线UV) × $MatCapTint`（逐像素烘焙，UV 空间） |
| 金属 F0 | **该件 matcap 的均值**（带色相），不再是常量 253 |
| 织物（`cloth`）/`rim`/皮肤/毛发 | 仍是非金属（`ClassifyName` 里非金属词优先） |

**为什么用"平色 F0"而不是把加法整层烘进 albedo**：仓库早先实测过逐像素相加——
`Elise_Bill` 的脸/身体各被 +79 ⇒ 实机像"蒙了一层雾"（见 `MaterialAppearance.CanBakeIntoAlbedo`）。
平色不引入这层雾，同时保住明度差与色相。

**`MatcapMean` 的坑**：它是靠**几何采样**算的，而写材质 patch 的链**没有几何** ⇒ 恒为 null
⇒ 全部走常量 F0（253）⇒ 头冠/绿灯笼全变亮银。现在在金属件上用 "matcap 图自己的均值" 兜底
（env3 12.3 / jewel9 67.8 / metal11 31.2，与逐件采样同量级）。

## 3. 派生贴图身份（这一类 bug 的标准化）

### 已发生两次，形状相同

> **贴图 id 由"输入"派生，内容却由"这一件的 UV 空间"生成。**

| 次数 | 症状 | 错在哪 | 修法 |
|---|---|---|---|
| ① 遮罩 | 三件共用 `0xbc21456cdb7e0902`（逐像素等于 `Mat_ACCMetal` 的 alpha） | id 由 `textureName + "_opacity"` 派生，而金属件的 `textureName` 被改名成 `<matcap>_f0` ⇒ 所有共用同一 matcap 的件同 id | id 改为跟**源底图名**（`$basetexture` 裸名） |
| ② 底色 | 耳坠 UV 位置错乱 | 底色按件 UV 烘焙，但 id 写成 `matcap_metal11_f0` ⇒ 共用同一 matcap 的件同 id | id 改为跟**材质裸名**（`EncodeBc7Deduplicated` 仍按内容去重） |

### 规则（写下来，别再靠记性）

1. **派生贴图的内容依赖某件的数据时，id 必须包含那个件**（材质裸名）。
2. **只有常量才可以按名字共享**（`shared_flat_normal_` / `shared_pbr_` / `shared_nodraw_mask_`）。
3. 判定"能不能共享"不看 UV 足迹（实测 30 张共享里绝大多数是合法的常量共享，用足迹做门禁会误报），
   而看**内容的来源**。

### 建议的结构性检查（待实现）

所有贴图注册走单一入口并带"归属键"：

```
RegisterTexture(id, ownerKey, bytes)
    同一 id 被不同 ownerKey 再次注册 ⇒ 抛异常（打印两条 ownerKey）
ownerKey = "shared/<常量名>"      // 常量，多材质共享合法
         | "<材质裸名>|<用途>"    // 按件烘焙，必须唯一
```

零误报、失败在转换时、覆盖所有派生贴图（底色/遮罩/NAR/自发光）。

## 4. 复现工具

| 命令 | 作用 |
|---|---|
| `milfy neko <尺寸> <模型vpk> <材质vpk> [更多vpk…] [only=N] [flip]` | 按文档语义逐像素合成 `base + matcap(法线UV)×tint` 并渲染（**这是判断"源侧应该长什么样"的基准图**） |
| `milfy mcapmask <模型vpk> <材质vpk…>` | 每件法线覆盖到 matcap 的哪一块、采样均值多少 |
| `milfy tex / cover / depth / raster` | 贴图导出、UV 足迹、自遮挡深度、逐材质配色渲染 |

> 注：`milfy cover` 的 V 方向已修正为 `row = v·H`（旧版用 `(1-v)`，导致所有足迹统计镜像，
> 详见姊妹文档 §1.2）。
