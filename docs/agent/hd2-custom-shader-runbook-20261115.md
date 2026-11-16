# HD2 自定义着色器：实施手册（可直接交接给另一个会话）

> 版本 2026-11-15。适用范围：Helldivers 2（Autodesk Stingray 引擎，**D3D12 + dxilconv**）中，
> 通过"自带编译好的 DXBC 记录"替换某个渲染 pass，实现引擎原生不提供的材质效果。
>
> **本文只写"怎么做"和"坑在哪"。** 过程记录、失败过的假设、多模型会诊内容在：
> `hd2-shader-diff-evidence-20261115.md`（技术档案）、`hd2-s1-stack-report.md`（崩溃链）、
> `hd2-offline-status-and-testplan-20261115.md`（状态分级）、`hd2-shader-evidence-audit-20261115.md`（审计）。
>
> 已在实机确认可用的效果：**贴图 alpha 逐像素驱动的半透明**（当前部署版本）。

---

## 0. TL;DR（实施会话照这五步走）

```powershell
cd E:\Data\source\repos\WpfApp1

# 1) 写 HLSL（模板见 §4.2），编译并让 SHEX 长度对齐到 51,828
# 2) 构建记录（自动：定长 NOP 填充 + 头字段 + 复签 + fxc 验证）
python tools\build_signed_ps.py --hlsl <你的.hlsl> `
    --payload "E:\mod\【常服】Milfy 替换 CE-07-材质\.abtest-backup\20261114-1720-pristine\9ba626afa44a3aa3.patch_1.gpu_resources" `
    --donor-size 60504 --shex-size 51828 --out <输出.bin> --stat ours --verify

# 3) 过门禁（四道，任一失败禁止部署）
python tools\gate_build.py record --record <输出.bin>.rec0.dxbc `
    --donor "…\9ba626afa44a3aa3.patch_1.gpu_resources" --donor-size 60504 `
    --ours-asm <输出.bin>.rec0.asm

# 4) 组装变体 + 部署（自动跑部署校验）
python tools\use_variant.py <变体名>

# 5) 上机看判据（§7）
```

回退：`python tools\use_variant.py baseline`（原始黑丝基线，永远是安全点）。

---

## 1. 前置知识（五分钟）

### 1.1 材质是什么

HD2 的材质 = **`<id>.patch_N`（TOC）** + **`<id>.patch_N.gpu_resources`（侧车）**。

- TOC 里是条目表：`Material` / `Texture`，各带 `tocOff/tocSize/gpuOff/gpuSize`。
- 材质的 TocData 里有 **槽位表**（槽名哈希 → 贴图 id）和 **变量表**（变量哈希 → 值）。
- 侧车里是**若干 DXBC 记录**：每条记录 = 一个渲染 pass 的着色器。记录**只按尺寸**在文件里排列，
  同尺寸可能有多份（同一 pass 的不同变体）。

一条记录的布局（我们实际操作的全部内容）：

```
"DXBC" + [4:20]校验和 + u32@20(=1) + u32@24=总长 + u32@28=块数
+ 块偏移表(每块 4 B) + 各块："RDEF"/"ISGN"/"OSGN"/"SHEX"/"STAT"/"PRIV" + u32 长度 + 数据
```

### 1.2 哪些 pass 可见（**最关键的一条**）

作者材质 `0xe577db2cf97fcddc` 的侧车里有三组记录，**实测结论**：

| 记录组 | 尺寸/份数 | 角色 | 游戏内是否可见 |
|---|---|---|---|
| 家族 A | **60,504 × 8** | 延迟光照合成（`cb1=c_ui_3d`，3 个 RT，读 GBuffer 粗糙度） | ✅ **可见，改它必见效** |
| 家族 A' | 60,416×2 / 60,444×2 / 60,476×8 | 同一着色器的其它变体（同资源表） | 同上 |
| 材质/UI 组 | 62,652 × 10 | 单 RT，`c_ui_3d` | ❌ **游戏内不可见**（只在军械库预览里有意义） |
| 46k 组 | 46,300/328/360/388 各 1 | 其它 | 未观察到影响 |

> **教训**：早期三轮"整段替换无效"的实验全打在 62,652 上 —— 那组根本不可见。
> **动手前先确认目标组是 60,504。**

### 1.3 这块材质的效果是怎么产生的

作者的"黑丝"不是贴图，而是**光照 pass 涂黑 + 逐像素 alpha**：

```asm
; 作者版本尾部
... 计算 N·V + 抖动纹理 → r27.w
mov o0.xyz, <黑>          ; RGB 被作者清零 → 画黑
mov o0.w, r27.wwww        ; alpha = 不透明度（逐像素）
```

`o0.w` **就是材质的不透明度，且真的参与混合**（实机证据：基线本身就是"边缘黑、中心透"）。
所以要实现"贴图驱动的半透明"，只需要让 `o0.w` 等于我们要的值。

### 1.4 贴图怎么进到 pass 里

延迟光照 pass 的资源表**全是引擎全局**（阴影图、BRDF、云、环境探针），只有一个 2D 材质通道：

| 寄存器 | 名字 | 来源 |
|---|---|---|
| `t2` | `__tex_roughness_map` | GBuffer 粗糙度 ← 材质槽 `RoughnessMap`（`0xc567338d`）→ 贴图 `0x8354a7fad81a2a29` |
| `s0` | `__samp_roughness_map` | 采样器 |
| 采样 UV | **`v7.xy`**（= `TEXCOORD6`） | **材质 UV**（实测：能看到贴图接缝，证明不是屏幕 UV） |

贴图条目：`0x8354a7fad81a2a29`，`gpuOff=8,231,872`，`gpuSize=22,369,648`
= **4096×4096 / 13 级 mip / BC7_UNORM**（原条目就是这个格式，不要改）。

---

## 2. 已确证 / 未确证（别越界）

### ✅ 实机确认

| 能力 | 证据 |
|---|---|
| 自带编译的 DXBC 能被引擎执行 | 全身变品红（`famA_valid_signed_magenta`） |
| 能在着色器里采样贴图并输出 | 明暗渐变自发光，可见贴图接缝（`famA_ramp_both`） |
| **贴图 alpha 驱动逐像素真半透明** | 透过黑丝看到皮肤、左右肢体明暗不同（`famA_alpha_only`，**当前部署**） |
| 整段替换 SHEX 是合法的 | 引擎**不校验** DXBC 校验和（作者本人记录就是坏校验和且能渲染） |

### ⚠️ 未验证 / 已知边界

- 动态发光（随时间变化）：`cb0[43].w` 是 `time`，机制可行但**未实机**。
- 镂空（`clip()`）与半透明的组合：机制可行但**未实机**。
- 金属（`BaseColorMetalMap.a`）与自定义着色器的组合：两条路都单独验证过，**未组合**。
- `t2` 的通道语义：我们拿 `texel.x` 当 alpha 并**覆盖该贴图像素**，所以语义无所谓；
  若要用**原始**贴图当 alpha，需先确认它是不是作者画的美术图集（会诊指出它更像服装图集）。
- 其它 pass（62,652 等）里的 o0.w 语义未验证。

---

## 3. 目录与工具

| 路径 | 作用 |
|---|---|
| `.dsh/variants/<名字>/` | 变体库。每个 = 4 个文件（`patch_0` / `patch_0.gpu_resources` / `patch_1` / `patch_1.gpu_resources`）+ `NOTE.txt` |
| `.dsh/roots/<id>/` | 取出来的根材质裸 TocData + 载荷（备用） |
| `tools/build_signed_ps.py` | **核心构建器**：编译 → 定长 NOP 填充 → 建记录 → 复签 → fxc 验证 |
| `tools/gate_build.py` | **门禁**：G1 块布局 / G2 校验和 / G3 输入读集 / G4 NOP 与 ret |
| `tools/use_variant.py` | 一键切换变体 + 自动部署校验（`--list` 列出全部） |
| `tools/verify_deployed.py` | 部署硬校验（符号链接解析 + sha256 + 索引链连续性），必须打印 `DEPLOYED OK` |
| `tools/embed_texture_data.py` | 原地等长替换贴图像素（自动读 TOC 定位 `gpuOff/gpuSize`，自动处理 DX10 头） |
| `tools/dmp_extract.py` | 崩溃转储拆解（异常/模块/栈候选/内存里的 DXBC） |
| `tools/make_honeycomb_mask.py` | 生成镂空掩码（PNG → texconv → BC7） |
| `tools/compose_material.py` / `add_texture.py` | 组合材质 / 追加贴图条目（走"换根材质"那条路时才用） |

FXC：`C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\fxc.exe`
TEXCONV：`tools\ref\HD2SDK-CommunityEdition\deps\texconv.exe`

---

## 4. 生产线（逐步，每步都可验证）

### 4.1 选目标 pass

**只用 60,504 × 8 这组。** 侧车 = `patch_1.gpu_resources`（原始备份在 `.abtest-backup\20261114-1720-pristine\`）。

### 4.2 写 HLSL（**照抄这个模板**）

```hlsl
// t2 = __tex_roughness_map（材质 RoughnessMap 槽的贴图），s0 = 对应采样器
Texture2D<float4> gTex : register(t2);
SamplerState      gSmp : register(s0);

struct VSOut
{
    float4 pos : SV_POSITION;
    float4 uv0 : TEXCOORD0;   // 记录 ISGN: reg0..4 掩码 0x0f
    float4 uv1 : TEXCOORD1;
    float4 uv2 : TEXCOORD2;
    float4 uv3 : TEXCOORD3;
    float4 uv4 : TEXCOORD4;
    float4 uv5 : TEXCOORD5;   // reg5/6 掩码 0x07(xyz)
    float4 uv6 : TEXCOORD6;   // reg7 掩码 0x03(xy)  ← 材质 UV，声明成 float4 独占寄存器
    float2 col : COLOR0;      // reg8 掩码 0x03(xy)
};
struct PSOut
{
    float4 o0 : SV_TARGET0;
    float  o1 : SV_TARGET1;
    float4 o2 : SV_TARGET2;
};

PSOut main(VSOut i)
{
    // ── 填充死代码：把 SHEX 撑到目标长度（fxc 不会优化掉，因为有依赖）
    float acc = i.uv0.x + i.uv1.x + i.uv2.x + i.uv3.x + i.uv4.x + i.uv5.x + i.uv6.x + i.col.x;
    [unroll]
    for (int k = 0; k < 711; ++k)          // ← 这个数字要按 §4.3 调
    {
        acc = mad(acc, 1.0001, 0.0001);
        acc = mad(acc, 0.9999, -0.0001);
    }

    // ── 真正的逻辑
    float c = gTex.SampleLevel(gSmp, i.uv6.xy, 0.0).x;

    float m = (acc > 1e30) ? 99.0 : 0.0;   // 让 acc 保持"存活"，正常为 0
    PSOut o;
    o.o0 = float4(0.0, 0.0, 0.0, c);        // 黑丝的黑 + 贴图 alpha
    o.o1 = 0.0;
    o.o2 = float4(0.0, 0.0, 0.0, 1.0);
    return o;
}
```

**写 HLSL 的三条硬规则**

1. **输入声明必须让每个语义独占一个寄存器**（一律声明成 `float4`，除了 COLOR0 用 `float2`）。
   否则 fxc 会把短向量打包进前一个寄存器的 `.zw`，导致寄存器整体前移、UV 取错。
2. **只读 ISGN 真的提供的分量**（`reg7` 只有 `xy`、`reg8` 只有 `xy`）。
   读越界分量 → **dxilconv 空指针崩溃**（见 §6.1）。
3. 输出必须写满 3 个 RT（`o0.xyzw` / `o1.x` / `o2.xyzw`），与 OSGN 一致。

### 4.3 让 SHEX 长度精确等于 51,828

`build_signed_ps.py` 会补 NOP，但**只能补不能减**，所以先让编译产物 ≤ 51,828：

- 循环每加 1 轮 ≈ **+72 B**；
- 实测参考：模板逻辑 + **711 轮** ≈ 51,780 → 补 12 个 NOP → 正好 51,828；
- 改一行输出（多一次采样/乘法）通常会 ±几十字节，**每次改完都要重新量**。

量法：编译后读 SHEX 块尺寸，或直接让 `build_signed_ps.py` 报
`SHEX %d > 目标 %d`（超了就减一轮）。

### 4.4 构建记录

```powershell
python tools\build_signed_ps.py --hlsl <你的.hlsl> --payload <原始 patch_1.gpu_resources> `
    --donor-size 60504 --shex-size 51828 --out <输出.bin> --stat ours --verify
```

它做的事（每一步都对应一次踩坑）：

| 步骤 | 说明 |
|---|---|
| 编译 | fxc `ps_5_0` |
| 定长填充 | 用 **`0x0100003A`（真 NOP）** 补到 51,828；**绝不能写 `0x01000038`**（那是另一条指令，`ret` 后会变成非法着色器） |
| 建记录 | `RDEF`(8,092)/`ISGN`(252)/`OSGN`(92)/`SHEX`(51,828)/`STAT`(148)，全部与原始逐块同尺寸；**`u32@20` 必须写 1** |
| 复签 | 微软定制 MD5（引擎不校验，但 fxc 校验；复签后才能离线反汇编验证） |
| 自验证 | 让 fxc 反汇编产物，应能看到你的指令 + `ret` + 尾部 NOP |

### 4.5 过门禁（**强制**）

```powershell
python tools\gate_build.py record --record <输出.bin>.rec0.dxbc `
    --donor <原始 patch_1.gpu_resources> --donor-size 60504 --ours-asm <输出.bin>.rec0.asm
```

期望：

```
G1 ✓ 块布局与原版逐项一致
G2 ✓ 校验和与内容一致
G3 ✓ 读取分量 ⊆ ISGN 提供掩码
G4 ✓ ret 唯一，之后 N 个真 NOP(0x0100003A)
gate_record: 通过
```

G3 失败会明确列出 `v# 读[...] 提供[...] 缺[...]` —— **改 HLSL 输入声明直到通过**。

### 4.6 组装变体并部署

变体 = 4 个文件；`patch_0` 用 `.abtest-backup\20261114-1740-patch0\` 里那份（它只含身体材质，未改）。

```powershell
# 建目录 + 拷 4 个文件（patch_1 用原始 TOC，patch_1.gpu_resources 用你的输出）
python tools\use_variant.py <变体名>        # 部署 + 自动校验
python tools\verify_deployed.py --mod '【常服】Milfy 替换 CE-07-材质'   # 应打印 DEPLOYED OK
```

**注意**：`data\` 下的 patch 文件都是符号链接，改 `data\` 无效，必须改 `E:\mod\<库目录>`。

### 4.7 换贴图（可选）

```powershell
# 1) 生成与条目同尺寸的 DDS（本例 4096² / BC7 / 13 mips → 像素数据必须 = 22,369,648 B）
& tools\ref\HD2SDK-CommunityEdition\deps\texconv.exe -f BC7_UNORM -y -o <目录> <你的.png>
# 2) 核对：DDS 文件大小 - 148（DX10 头）= 条目 gpuSize
# 3) 原地写入（工具自己从 TOC 读 gpuOff/gpuSize）
python tools\embed_texture_data.py --patch <变体的 patch_1> --texture 0x8354a7fad81a2a29 --dds <你的.dds>
```

尺寸不对会直接报错；`mipChainSize(width,height,fmt,mips) == gpuSize` 是硬约束（§11 的 G5）。

---

## 5. 五个"必须"（少一个就失败）

| # | 必须 | 失败表现 |
|---|---|---|
| 1 | NOP 用 `0x0100003A` | `ret` 后留非法指令 → 着色器被拒 / 崩溃 |
| 2 | **着色器读的输入分量 ⊆ 记录 ISGN 提供的掩码** | **dxilconv 空指针崩溃**（最凶） |
| 3 | 记录头 `u32@20 = 1` | 记录被判非法 |
| 4 | 五个块的尺寸与原始逐项一致 | 各种静默失效 |
| 5 | 校验和复签 | 只影响离线验证（引擎不校验），但**不复签就没法反汇编自查** |

---

## 6. 三个典型故障与定位

### 6.1 游戏崩溃

**症状**：游戏闪退，`%LOCALAPPDATA%\Temp\PACKER_*.dmp` 生成转储。

**流程**：

```powershell
python tools\dmp_extract.py <dump> --outdir .dsh\analysis\dump
# 看 summary.txt：异常码 / 地址 / 模块归属
#   - 若 RIP 落在 dxilconv.dll → 着色器被转换器拒绝（先查 §5 第 2 条：G3 门）
#   - 注意 game+0x9F616D 读 0x58 是**异常处理里的二次崩溃**，不是根因
```

**已确认的根因案例**：崩溃版读 `v7.z`（ISGN 只给 `xy`）→ `dxilconv+0x44414` 的
`mov eax,[rcx+54h]` 且 `RCX=0` → 崩溃。修复 = 过 G3 门。

### 6.2 变"光腿"（黑丝消失）

说明**这个 pass 被跳过了**（不是我们代码跑出来的效果）。常见原因：

- 着色器创建失败（G3 缺失分量 / 结构非法）；
- 或你的 alpha 恰好为 0（这版输出 `float4(0,0,0,0)` 本来就该是光腿）。
  → 判据：把 alpha 改成常量 `0.75` 再测，若黑丝回来 = 代码生效、只是采样值不对。

### 6.3 完全没变化

**目标 pass 不可见**（最可能是打在 62,652 上），或 TocData 没生效。
→ 先用一个"必变"的探针确认（输出品红，`famA_valid_signed_magenta` 的做法）。

---

## 7. 上机判据（一次只看一件事）

| 输出写法 | 预期现象 | 能证明什么 |
|---|---|---|
| `o0 = float4(1,0,1,1)` | 全身均匀品红 | 我们的代码被执行 |
| `o0 = float4(c,c,c,c)` | 灰渐变 + 可见接缝 | 采样与 UV 正确 |
| **`o0 = float4(0,0,0,c)`** | **黑丝 + 透明度随贴图变化** | **目标达成** |
| `o0 = float4(0,0,0,0.75)` | 均匀半透明黑丝 | 混合通道通（不依赖贴图） |

**不要用"全零输出应该全黑"当判据** —— 这块材质的 `o0.xyz` 本来就是黑的（作者清零了），
`alpha=0` 的预期外观就是光腿。这条曾把成功判成失败。

---

## 8. 回退与安全检查

```powershell
python tools\use_variant.py --list        # 看所有变体
python tools\use_variant.py baseline      # 回到原始黑丝基线（永远安全）
```

每次部署后 `use_variant.py` 会自动跑 `verify_deployed.py`，**必须看到 `DEPLOYED OK`**。

已知可用变体：

| 变体 | 用途 |
|---|---|
| `famA_alpha_only` | ★ 当前部署，贴图驱动逐像素半透明（成功） |
| `famA_ramp_both` | 采样值同时进 RGB+alpha（灰渐变自发光） |
| `famA_valid_signed_magenta` | 品红探针（证明代码被执行） |
| `famA_fixed_inputmask` | 只修 G3、不换贴图（不崩、光腿） |
| `baseline` | 原始黑丝 |

---

## 9. 下一步可做的事（按难度）

| 目标 | 做法 | 风险 |
|---|---|---|
| 换真实贴图 | §4.7，尺寸/mip 对齐即可 | 低 |
| **镂空** | HLSL 加 `clip(c - 阈值)`，可与半透明共存 | 低（G3 别踩） |
| **动态发光** | `cb0[43].w` = `time`；采样时加 `time` 偏移或做强度脉动 | 中（要用 cbuffer，需确认读取不越界） |
| 金属 | 与 `BaseColorMetalMap.a` 那条已通路组合 | 中（未验证组合） |
| 机制深挖 | 写最小 D3D12 harness，用平凡 VS + 本记录建 PSO，看是 AV 还是 `E_INVALIDARG` | 无（离线） |

---

## 10. 交接注意事项

1. **不要碰 `data\`**（全是符号链接）；只改 `E:\mod\<库目录>`。
2. **每次只改一个变量**，并保留变体；失败时先回 `baseline`。
3. **每个构建都跑门禁**（§4.5），不要靠"看起来能编译"。
4. 62,652 那组**不要浪费时间**（游戏内不可见）。
5. 判断"是否生效"永远用**可见的强信号**（品红 / 灰渐变），不要用"黑丝在不在"这种多义现象。
6. 崩溃了先抓转储（§6.1），不要靠猜。
7. 本手册的方法建立在"作者材质 `0xe577db2cf97fcddc` 的载荷结构"上；
   换别的 mod 要重新确认记录尺寸、ISGN 掩码与资源寄存器（用 fxc 反汇编 + `gate_build.py` 的思路）。
