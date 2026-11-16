# HD2 着色器差异证据包（给分析 agent 用）

> 生成时间：2026-11-15 08:2x。所有反汇编由 `fxc /dumpbin` 产出（权威，非自研解码器）。
> 游戏：Helldivers 2（Autodesk Stingray / Bitsquid）。材质 = 自定义 root material + 自带 DXBC 载荷。

## 一、这是什么场景

HD2 的材质是一份 `TocData`（槽位 + 变量）+ 一份 `.gpu_resources` 侧车（内含若干 **DXBC 记录**，每条记录 = 一个渲染 pass 的着色器）。

社区作者可以**自带编译好的着色器**（下面三份样本都是），所以"自定义着色器"在这个引擎上是**合法且已实现过**的。

我们的目标：做一个**贴图驱动的逐像素真半透明**材质（alpha 来自我们自己的贴图）。现成的引擎材质族都做不到（要么 hard clip 镂空，要么整体均匀透明）。

## 二、材料清单（相对本文件所在目录 `.dsh/analysis/shaders/`）

| 文件 | 记录 | 说明 |
|---|---|---|
| `vanilla_advanced_60412.asm` | 60,412 B | **基准**：本体 `advanced` 材质的材质主 pass（`0xef9b3fc2fc1ba61e`） |
| `author_hosiery_62652.asm` | 62,652 B | **黑丝作者**（`0xe577db2cf97fcddc`）的材质主 pass：改自 vanilla advanced |
| `diff_author_vs_vanilla.txt` | — | **+77 / −3**：作者只追加了尾部一段 |
| `holobag_63052.asm` | 63,052 B | **像素背包**（`0xdec4b7db2e8095f3`）的材质主 pass |
| `diff_holo_vs_vanilla.txt` | — | **+120 / −437**：改动较大 |
| `familyA_60504.asm` | 60,504 B | 作者载荷里另一组 pass（延迟光照/曝光合成）。**与原版 advanced 完全不同**，diff +1880/−1723 |
| `diff_familyA_vs_vanilla.txt` | — | 同上 |

三份样本签名对比：

| | vanilla | 作者黑丝 | 像素背包 |
|---|---|---|---|
| ISGN | 248 B / 9 元素 | 248 / 9（相同） | 248 / 9（相同） |
| OSGN | 44 B / 1 元素（SV_TARGET0 xyzw） | 44（相同） | 44（相同） |
| 指令槽 | 1,578 | 1,652 | ~1,7xx |
| `dcl_temps` | 21 | 30 | 35 |
| 尾部 `o0.w` | `mov o0.w, l(1.000000)` | `mov o0.w, r27.wwww` ← **逐像素** | `mov o0.w, l(1.000000)` ← **没动** |

## 三、作者的改法（已确认，diff 只有 +77/−3）

```asm
; 尾部替换：原本 mov o0.w, l(1.0) 这段被换成
add     r22.xyz, -v1.xyzx, cb1[4].xyzx     ; V = 相机位置 - 世界位置
dp3     r21.w, r22.xyzx, r22.xyzx
rsq     r21.w, r21.w
mul     r22.xyz, r21.wwww, r22.xyzx        ; 归一化 V
dp3_sat r21.w, r21.xyzx, r22.xyzx          ; N·V
add     r21.x, r21.w, l(-0.210000)         ; N·V − 0.21
mul     r21.x, r21.x, l(1.265823)          ; × 1/(1−0.21)
max     r21.x, r21.x, l(0.000000)          ; saturate → t
mad     r21.y, r21.x, l(-2.0), l(3.0)      ; 3−2t
mul     r21.x, r21.x, r21.x                ; t²
mul     r21.x, r21.x, r21.y                ; t²(3−2t) = smoothstep
mad     r21.yz, v3.xxyx, l(0,0.5,0.5,0), l(0,0.5,0,0)  ; 屏幕 UV
max     r21.yz, r21.yyzy, l(0,0.500122,0.000122,0)
min     r21.yz, r21.yyzy, l(0,0.999878,0.499878,0)
sample_l_indexable(texture2d) r21.yz, r21.yzyy, t7.zxyw, s0, l(0)  ; 抖动纹理
mul     r21.z, r21.z, l(0.266000)          ; × 0.266
mad     r21.x, -r21.z, r21.x, l(1.0)       ; 1 − dither·curve
mul_sat r27.w, r21.x, r21.y
mov     o0.w, r27.wwww                     ; ★ alpha = 逐像素
```

该 pass 的贴图绑定（RDEF）：`t8=__tex_albedo_emissive`、`t7=__tex_detail_normal_leather`、`t5=__tex_illuminate_data`、`t4=__tex_nar`、`t2=__tex_detail_normal_porcelain`、`t3/…` 等；cbuffer：`cb0=global_viewport`、`cb1=c_ui_3d`、`cb2=c_per_object`、`cb3=c0`。
其中已有现成采样：`sample_indexable r6.xyzw, v3.xyxx, t8.xyzw, s1`（即 albedo 及其 alpha，写进 r6）。
**但 `r6.w` 在后续被反复覆写**，所以不能直接在尾部读它。

## 四、已实测的硬事实（不要推翻，除非有证据）

1. **改 SHEX 内部的立即数 → 生效**（多次实机验证；例如改作者曲线常量，画面随之变化）。
2. **整段替换 SHEX → 一律不生效**，已试 6 种形态：
   - 纯自编记录（RDEF 100 B）
   - 原版 RDEF/STAT + 我们的 SHEX
   - 原版 RDEF/ISGN/OSGN/STAT + 我们的 SHEX 补到与原始**完全同长**
   - 同上，另加 `u32@20` 从 0 修正为 1
   - 我们自己的 ISGN/OSGN + 我们的 SHEX
   - 打在不同记录组（62,652 / 60,504）
   结果一律是"回退"（可见 pass 丢失 → 表现为黑丝消失/光腿），**我们写的代码没有生效**（用"全零输出应当全黑"做过判别，未变黑）。
3. 记录级重建器已验证**字节透明**（把原记录拆块再拼回，输出与原记录逐字节相同）。
4. 原版记录的 `u32@20`（DXBC 头）**676 条全部为 1**；我们早期重建写成 0，但修正后仍失败。
5. `fxc /dumpbin` 与 `D3DDisassemble` **都拒绝对"校验和被改动过"的 blob 反汇编**（HRESULT E_FAIL），所以改动后的记录无法自验证。
6. 家族 A（60,504）尾部是曝光/色调整合（`mul_sat o2.x, r0.x, l(0.5)`、`mov o0.xyzw, r1.xyzw`），**不含**作者那种压暗曲线于尾部；但整个 51,828 B 里确实含有 `-0.21` / `0.266` 常量（作者也改了它）。
7. 该 shader 里 **`nop` 只有 1 条**，几乎没有空白槽可注入。
8. 游戏日志**不记录**着色器创建失败（只有包加载日志）。

## 五、要你们回答的问题（核心）

- **Q1**：为什么"改立即数"能被接受，而"整段换 SHEX"不被接受？请从 DXBC/Stingray 的可能校验点逐一排查（chunk 布局、STAT 语义、RDEF 资源表、校验和、驱动的 shader cache、payload 头部的 `(size,hash32)` 表等），并指出**最可能的一个**和**验证它的最小实验**。
- **Q2**：在不改变 SHEX 长度、不新增指令的前提下，仅靠"替换等长指令 / 改操作码 / 改操作数与寄存器号"，能否实现"把某个已有纹理采样的 alpha 接到 `o0.w`"？请给出**具体可行的指令序列**（利用现成的 `sample r6, v3, t8, s1` 或其它已有采样，以及尾部可利用的寄存器）。
- **Q3**：如果必须新增指令，有没有"不改变文件长度"的办法？（例如把某个较长指令的多个 dword 重解释成两条更短的指令、利用 `if`/`else` 分支里的跳转、利用未使用的寄存器做计算等）
- **Q4**：像素背包那份（+120/−437）到底改了什么？它有没有引入**逐像素**的量？有没有可复用的手法？
- **Q5**：有没有可能**不必改着色器**，而是通过材质变量/RDEF 资源表/其它 TocData 字段，让引擎启用 alpha blending？（Stingray 的 `gbuffer_material` 有 `HAS_OPACITY` 特性位，是否可能通过材质侧触发？）

## 六、外部线索

- 同引擎的另一款游戏 Vermintide 有公开的 shader node 定义（`vmf/core/stingray_renderer/output_nodes/standard_base.shader_node`），其中 `standard_base` 有 `{ name = "opacity", type = { scalar: ["HAS_OPACITY"] }, domain = "pixel" }`，且 `gbuffer_material` 注释提到 "with refraction we are essentially taking over the blending in our shader, so we disable alpha blending"。
- Bitsquid 博客《Stingray Renderer Walkthrough #4: Sorting》提到渲染上下文/排序与 layer 系统（命令里含 shader system 与 layer system 的位域）。

---

# 九、崩溃转储的结论（2026-11-15，已定论）

## 9.1 真实故障点

一次实机崩溃的 minidump（`PACKER_665835500_22488.dmp`，15.1 MB）经解析（`tools/dmp_extract.py`）
并由子代理 S1 用系统 DLL 的 `.pdata` 完整展开，调用链为：

```
game+736F84
  → D3D12Core+7BC34 → +16C77 → D3D12+1707 → D3D12Core+50FDB → +7D246 → +158CD → +15A87C
  → dxilconv+B3A2 → +2575 → +CAD7 → +EB40 → +B633 → +1E4A6
  → dxilconv+44414        ← mov eax,[rcx+54h]，RCX=0  ⇒ 原始空指针崩溃
⇒ 异常分发 → UnhandledExceptionFilter → game 崩溃处理链 → +9F335D → +9F616D（读 [rax+58]，RAX=0，二次崩溃）
```

原文与证据：`docs/agent/hd2-s1-stack-report.md` / `.json`，复核脚本 `hd2-s1-stack-analysis.py`。
WinDbg 最小复核命令（无需改动任何文件）：

```
.cxr 0000001a164f70c0 ; .exr 0000001a164f75b0 ; r ; kv ; u 00007ff990144414 L4
```

**要点**：
- 崩溃发生在 **`dxilconv.dll`（微软的 DXBC→DXIL 转换器）内部**，不是引擎容错问题；
- 我最初看到的 `game+0x9F616D 读 0x58` 是**异常处理里的二次崩溃**，一度误导了归因；
- 游戏走 **D3D12**，我们的 `ps_5_0` 必须经 dxilconv 转成 DXIL。

## 9.2 由此确立的构建门（硬性）

| 门 | 检查内容 | 工具 |
|---|---|---|
| G1 | 记录总长与 5 个 chunk 尺寸与原版逐项一致 | `tools/build_signed_ps.py`（`构造 == 目标` 断言） |
| G2 | DXBC 校验和复签（微软定制 MD5） | 同上（内联实现，已用本体 cso 验证逐字节一致） |
| G3 | `fxc /dumpbin` 能反汇编且尾部指令符合预期 | 同上（`--verify`） |
| **G4** | **着色器读取的输入分量 ⊆ 记录 ISGN 提供的掩码** | 见下（本轮新增，抓到了 `v7.z` 越界） |
| G5 | SHEX 填充只用 `0x0100003A`（真 NOP），且位于 `ret` 之后 | `build_signed_ps.py` 常量 |

### G4 的实现（把 fxc 的汇编当输入，避免自研解码器）

```text
1) fxc /dumpbin 记录.dxbc               → 读 "Input signature" 表，得到 {寄存器: 提供掩码}
2) fxc /dumpbin 我们的编译产物.dxbc      → 读 dcl_input_ps 行，得到 {寄存器: 读取掩码}
3) 断言：每个寄存器的 读取 ⊆ 提供，否则拒绝构建
```

**这是本轮最有价值的沉淀**：它把"能编译"与"能被 dxilconv 接受"之间的那道坎，变成一条可离线检查的断言。
已知触发案例：`COLOR0` 被 fxc 打包进 `v7.zw`，导致 `dcl_input_ps v7.z`，而 ISGN 只给 `v7.xy`。

### 规避手法

- 让每个语义**独占一个寄存器**（把会被打包的短向量声明成 `float4`），使 fxc 的打包结果与记录 ISGN 对齐；
- 只读签名确实提供的分量（例如 ISGN 里 `TEXCOORD3` 只有 `.w`，就只读 `.w`）；
- `build_signed_ps.py` 现在保持原版 ISGN，因此**必须**按上面两条约束写 HLSL。

## 9.3 环境事实（从转储得到）

- 游戏进程加载：`D3D12.dll`、`D3D12Core.dll`、`dxilconv.dll`（同时也有 `d3d11.dll`）；
- 显卡驱动：NVIDIA（`nvwgf2umx.dll` / `nvgpucomp64.dll`）+ AMD 驱动文件亦被加载；
- 转储里只捕获了 ~10 MB 虚拟地址，**没有**我们注入的记录（`hits.txt` 全 0），所以无法从转储直接看我们的 blob；
- 转储里另有 5 个 DXBC 容器（3 个 `RDEF:100` 的小着色器），均非我方产物。

---

# 十、★ 里程碑：贴图驱动的逐像素真半透明（2026-11-15 实机确认）

## 10.1 结论

用**自己编译的像素着色器** + **自己的贴图**，在 HD2 里实现了
**由贴图 alpha 逐像素控制的真半透明**（能透过黑丝看到皮肤，左右肢体明暗/透明度随贴图渐变）。

实机观察（用户确认）：
- 背面可见**贴图接缝** ⇒ UV 是**材质 UV**（`v7.xy`），不是屏幕 UV；
- 可透过全身黑丝看到肉体皮肤 ⇒ `o0.w` 真的参与混合；
- 正面左右手臂/左右腿的颜色与透明度不同 ⇒ 逐像素、由贴图决定。

## 10.2 可工作的完整配方

| 项 | 值 |
|---|---|
| 宿主 pass | 家族 A 的 `60,504 B × 8` 记录（延迟光照；`cb1=c_ui_3d`，3 个 RT） |
| 记录构造 | `RDEF`(8,092) / `ISGN`(252) / `OSGN`(92) / `SHEX`(51,828) / `STAT`(148)，**与原始逐块同尺寸** |
| 着色器签名 | 与记录一致；**只读 ISGN 提供的分量**（见 §9.2 G4） |
| 采样槽 | `t2 = __tex_roughness_map`，采样器 `s0`，UV = `v7.xy`（材质 UV） |
| 贴图来源 | 材质槽 `0xc567338d = RoughnessMap` → 贴图 `0x8354a7fa`（4096²，22,369,648 B，BC7 + 完整 mip） |
| 输出 | `o0 = float4(0, 0, 0, c)` —— RGB 黑（黑丝的黑），alpha = 贴图采样值 |
| 校验和 | 微软定制 MD5 复签（引擎不校验，但 fxc 校验，复签后才能离线反汇编验证） |
| NOP 填充 | `0x0100003A`，放在 `ret` 之后 |

**最终 HLSL 骨架**（其余是填充死代码，用来把 SHEX 撑到 52,220−8 字节）：

```hlsl
Texture2D<float4> gTex : register(t2);
SamplerState      gSmp : register(s0);
// 输入声明：让每个语义独占一个寄存器，与记录 ISGN 对齐
PSOut main(VSOut i) {
    float c = gTex.SampleLevel(gSmp, i.uv6.xy, 0.0).x;   // i.uv6 = TEXCOORD6 = v7
    o.o0 = float4(0, 0, 0, c);                            // 黑 + 逐像素 alpha
    o.o1 = 0; o.o2 = float4(0,0,0,1);
}
```

## 10.3 达成过程中的三个"必须"（缺一不可）

1. **NOP 必须是 `0x0100003A`**。写成 `0x01000038` 会在 `ret` 后留下非法指令 —— 这是此前"整段替换无效"的真正原因。
2. **必须过 G4**：着色器读取的输入分量 ⊆ 记录 ISGN 提供的掩码。
   否则 **dxilconv 会空指针崩溃**（§9.1 的完整调用链就是这么来的）。
3. **整段替换是可行的**（引擎不校验 DXBC 校验和 —— 作者本人的记录就是坏校验和且能用）。

## 10.4 已验证变体（`.dsh/variants/`）

| 变体 | 用途 | 实机结果 |
|---|---|---|
| `famA_valid_signed_magenta` | 证明"我们自己编的着色器会被执行" | 全身均匀品红 |
| `famA_ramp_both` | 同一采样值进 RGB + alpha | 明暗渐变自发光，可见贴图接缝 |
| **`famA_alpha_only`** | **RGB 归零、alpha = 采样值** | **★ 目标达成：贴图驱动逐像素半透明** |
| `famA_fixed_inputmask` | 只修 G4、不换贴图 | 不崩、黑丝消失（alpha≈0，符合预期） |
| `baseline` | 原始黑丝基线 | 正常 |

## 10.5 下一步可做的事（同一机制即可扩展）

- **换真实贴图**：把 4096² 渐变替换成目标图案（无缝图不会有接缝）；尺寸/格式按条目对齐。
- **镂空**：着色器里加 `clip(c - 阈值)` 即得逐像素硬裁，可与半透明共存。
- **动态发光**：`cb0` 里有 `time`，采样时加时间偏移或做强度脉动即可（此前只能靠现成材质族）。
- **金属**：`BaseColorMetalMap.a` 那条已验证的通路与这套不冲突，可组合。

---

# 十一、转储分析的补充与更正（2026-11-15，9 路 agent + 3 路会诊后）

## 11.1 被证伪的两个猜测（不必再试）

| 猜测 | 结论 | 证据 |
|---|---|---|
| "我把 4K 贴图换成了 BC7，格式不匹配导致崩溃" | **不可能成立** | 原条目本来就是 `4096² / mips=13 / BC7_UNORM / gpuSize=22,369,648`；我们只换像素，**TocData 340 B 逐字节未变**；`13 级 mip 链 == 22,369,648` 精确相等；texconv 双向解码正常（原图 R49/G92/B0/A255，与我们写入的渐变都能正确解出） |
| "贴图需要换成 BC5/2K 才能修崩溃" | 同上，方向错误 | 同上 |

## 11.2 归因已被一对"单变量对照"锁死

`famA_roughness_alpha_v2`（崩）与 `famA_alpha_only`（通）**逐字节只差 176 B**：

- 8 条记录 × (16 B 校验和 + 6 B 输入声明操作数) = 176 B
- **22,369,648 B 的贴图载荷字节级相同**，TOC 未动

⇒ 贴图因素在因果层被排除；差别就在**着色器的输入读集**。

## 11.3 一处会诊错误（已否决，勿写进文档）

会诊C 主张崩溃版"同时越界读 `v7.z` 与 `v4.x`"。**这是错的**：原版 ISGN 二进制里
`reg4` 的 mask = **0x0f（xyzw，四分量全提供）**，只是原版着色器**只用了 `.w`**。
会诊C 把 fxc 汇编表里的 `Used` 列误当成 `Mask` 列。

→ **真正的越界只有 `v7.z` 一处**（原版 `reg7` mask = `0x03`）。§9.2 / §10.3 的表述无需修改。

## 11.4 新增构建门 G5（贴图 mip 链）

`mipChainSize(width, height, fmt, mips) == gpuSize`，否则拒绝打包。本次本来就是绿的，属防回归。

## 11.5 工具与用法

```powershell
# 记录级门禁（G1 块布局 / G2 校验和 / G3 输入读集 / G4 NOP 与 ret）
python tools\gate_build.py record --record <我们的记录.dxbc> --donor <原版载荷> `
    --donor-size 60504 --ours-asm <fxc 反汇编>
```

G3 现在**直接解析 ISGN 二进制**（不再解析 fxc 的文本表格 —— 那张表的 `Used` 列曾被误读成 `Mask`）。

## 11.6 转储中那些"阴性"证据不可用

`hits.txt` 里 5 个已知校验和全部 0 命中，**连原始 `matpass` 也是 0** —— 说明这份 10.1 MB 的
dump 根本没覆盖到这些页，所以"没命中"既不能证明"我们的 blob 没加载"，也不能证明别的。
**阴性结果没有阳性对照时不要引用。**

## 11.7 不建议再做"归因型"实机测试

矩阵里唯一没被覆盖、且本来就要做的格子是**产品格**（修好的着色器 + 目标真实贴图），
而它**已经通过了**（`famA_alpha_only`）。其余格子（原始 SHEX+原图、坏 SHEX+原图…）
结果可预测，只补表、不改变任何决策 —— **不值得再消耗重启**。

机制层若还想深挖，正确做法是**离线**：用平凡 VS + 我们那条 60,504 B 记录原样建 D3D12 PSO，
看是 AV 落在 `dxilconv+0x44414` 还是返回 `E_INVALIDARG`。可无限重复、无崩溃风险。
