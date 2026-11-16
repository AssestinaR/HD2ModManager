# 里程碑：自定义材质生产线全线打通（2026-11-15）

> 一句话：**一个我们自己编译的像素着色器，在零售版里同时做到了**
> 贴图驱动的逐像素半透明 + 硬边镂空（`discard`）+ 自算金属高光 + 贴图多色，
> 且已接上引擎时间做动态发光。
>
> 全部走的是 `data\*.patch_N` 文件层，**没有改游戏本体**。

---

## 1. 实机确认清单（每一条都有用户回执）

| 能力 | 变体 | 观测 |
|---|---|---|
| 自编 DXBC 被执行 | `famA_valid_signed_magenta` | 全身均匀品红 |
| 采样贴图并输出 | `famA_ramp_both` | 明暗渐变 + 可见贴图接缝 |
| 贴图 alpha → 逐像素真半透明 | `famA_alpha_only` | 透过黑丝看到皮肤 |
| 硬边镂空（`discard`）**独立成立** | `famA_cutout_probe`（`uv.x<0.30` 确定性丢弃） | 边界笔直、能看见背后的缺口 |
| 金属高光（法线+视线+太阳，自算） | `famA_trimetal` | 随角度移动的高光 |
| **四者同时成立** | `famA_diag` | 肉色透明 / 不透明 / 金属光泽 / 锋利镂空 / 多色 同时可见 |
| 贴图换图即换效果 | `famA_trimetal3` → `famA_diag` | 换 4096² BC7 贴图，孔洞/色带/透明全跟着变 |
| 动态发光（时间驱动） | `famA_product3` | 透明度呼吸（约 3 秒周期，35%↔100%），肉眼明显 |
| **成品外观** | `famA_product3` | 整体发黑、窄高光、稀疏镂空孔、透明度起伏 —— 四项同时成立 |

> 注意：`famA_cutout_alpha`（第一版"镂空+透明"）的证据**不可靠**——它没有把 alpha 钉死，
> 所以当时的"缺口"可能只是 alpha≈0 的透明。真正的证明是 `famA_cutout_probe`。

### 1.1 曝光：数值看着对、画面发白的真凶（2026-11-15）

写在 `o0` 里的是**线性**颜色，引擎写 sRGB 目标时会做 `pow(linear, 1/2.2)`：

```
0.118(底色) + 0.035(环境) + 0.10(高光) ≈ 0.25 线性  ->  sRGB ≈ 0.53 = 中亮灰
```

连续两版"乳白、黑色被盖住"都是这个原因，不是高光太强、也不是通道错。
修法是整体乘一个 `EXPOSURE`：把线性压到 ~0.02 才真的发黑。**以后调外观先算线性值，别看屏幕下结论。**

### 1.2 动态发光的可见性

亮度呼吸（±8%）在暗底上几乎看不见；**改成透明度呼吸**（`a` 在 35%~100% 之间摆）一眼可见。
时间源：`global_viewport[43].w`（秒），见 §2.2。

---

## 2. 关键的 pass 事实（都是实测/反汇编/官方 SDK 交叉验证过的）

### 2.1 可见 pass = 3 RT 光照 pass（`60,504 × 8`）

| 项 | 值 |
|---|---|
| 记录尺寸 / 条数 | 60,504 B × 8 |
| chunk 布局 | RDEF 8,092 / ISGN 252 / OSGN 92 / SHEX 51,828 / STAT 148 |
| 输出 | `o0` float4（最终颜色）、`o1` float（透明深度权重）、`o2` float4 |

**输入语义（反汇编确认）**

| 寄存器 | 内容 | 证据 |
|---|---|---|
| `v2.xyz` | **法线** | `dp3 v2,v2 → rsq → mul` 标准归一化 |
| `v3.xyz` | **世界坐标** | `add r35.xyz, -v3.xyzx, cb0[2].xyzx` |
| `v7.xy` | 材质 UV | `t2.Sample(s0, v7.xy)` |
| `v0` | SV_Position | — |

**RDEF 绑定**：材质贴图**只有 `t2 = __tex_roughness_map`**；其余是 `t8 __tex_sun_shadow_map`、`t16` 光探针、
8 个 cbuffer（b1 相机、b2 太阳方向、b3 太阳颜色……）。**没有 albedo 贴图**。

另一条 `62,652 × 10`（1 RT）是材质 pass，绑 `__tex_nar` / `__tex_albedo_emissive` / `__tex_illuminate_data`，
把它整组涂成亮青**游戏内毫无变化** ⇒ 对这套网格不画（大概率是 UI 预览用）。

### 2.2 全局 cbuffer 布局（来自 HellForge SDK，官方注释称"就是游戏的寄存器"）

`D:\Program Files\HellForge\app\sdk\shaders\hellforge_shader.hlsli`

```
cbuffer global_viewport : register(b0) { float4 hf_viewport[51]; }   // 816 B
  [2].xyz     相机世界坐标
  [11..13].y  相机前向轴（camera_inv_view 的行）
  [43].w      游戏时间（秒）
  [44].x      帧 delta time
  [44].zw     渲染目标像素尺寸

mesh 宿主 SV_TARGET1 = hf_weight(视深) * 像素 alpha
hf_weight(d) = 0.1*exp(-0.004328d) + exp(-0.086562d) + 10*exp(-0.28854d) + 20*exp(-0.865618d)
```

`[2].xyz` 与我反汇编得到的 `cb0[2].xyz` 完全吻合，互为交叉验证。

> ⚠️ **`o1` 的权重公式属于 HellForge 自己的 mesh 宿主（decal/transparent/refract），不是这条光照 pass 的规范。**
> 2026-11-15 实测：把 `o1` 从写死 1.0 改成 `hf_weight(视深)*alpha`，**看不到任何距离衰减**。
> 结论：这条 pass 的 `o1` 不是那个合成权重（或本就不参与视觉）。当它 inert，别再拿它当依据。

---

## 3. 生产线（现在是四条命令）

```powershell
# 1) 画贴图（镂空孔 / 透明度 / 颜色全在图里）
python tools\make_stocking_texture.py --out E:\tmp\hd2-stocking          # 产品版：蜂窝孔 + 竖向渐变
python tools\make_diag_texture.py    --out E:\tmp\hd2-stocking          # 诊断版：色带 + 海浪 alpha

# 2) 转 BC7 + 13 mip（4096²，像素数据必须正好 22,369,648 B）
texconv -f BC7_UNORM -m 13 -y -o E:\tmp\hd2-stocking E:\tmp\hd2-stocking\xxx.png

# 3) 原地替换材质槽里的那张贴图（长度不变，不动任何 TOC 字段）
python tools\embed_texture_data.py --patch <变体 patch_1> --texture 0x8354a7fad81a2a29 --dds ...\xxx.dds

# 4) 编译着色器 + 定长填充 + 建记录 + 复签
python tools\build_signed_ps.py --hlsl <hlsl> --payload <上一步产物> \
    --donor-size 60504 --shex-size 51828 --out <变体 patch_1.gpu_resources> --stat ours --verify

# 验收：四道门
python tools\gate_build.py record --record <...>.rec0.dxbc --donor <baseline payload> \
    --donor-size 60504 --ours-asm <...>.rec0.asm

# 部署 + 校验
python tools\use_variant.py <变体名>
```

**三个必须不变**：五个 chunk 的名字/顺序/长度与 donor 逐项相同；`ret` 之后补真 NOP `0x0100003A`；
读的输入分量 ⊆ ISGN 掩码（`v2.xyz` / `v3.xyz` / `v7.xy`）。

---

## 4. 贴图就是参数面板

`t2` 一张图的四个通道各管一件事，**换图即换效果，不用重编译**：

| 通道 | 作用 |
|---|---|
| R | 镂空遮罩（`R < 0.5` 丢弃） |
| G,B | 底色（`float3(g, b, g)`，R 不参与，避免遮罩染色） |
| A | 逐像素透明度 |

> ⚠️ **取色式的副作用**：`float3(g, b, g)` 输出的 R 与 B 恒等，所以**纯蓝做不出来**
> （要 R=0 且 B=255 同时成立）。可稳定区分的只有 白 `(255,255)` / 洋红 `(255,40)` /
> 绿 `(40,255)` / 灰 `(40,40)`。
> 2026-11-15 踩过：诊断贴图里某波段把 G、B 都写成 40，屏幕上显示成灰色而不是蓝色，
> 一度被误当成"通道错乱"。实际是生成器的调色板写错，引擎通道正常（镂空孔由 R=0 打出，可证）。

着色器顶部常量（要改得重编译）：`CUT_THRESH` / `BASE_TINT` / `AMBIENT` / `GLOSS` / `METAL_K` /
`RIM_K` / `OPACITY` / `PULSE_RATE` / `PULSE_K`。

---

## 5. 仍然没做的

| 项 | 状态 |
|---|---|
| 把常量接到材质变量上（改数值免编译） | 未做。光照 pass 里**没有材质变量 cbuffer**，只能靠贴图或重编译 |
| 动态发光的其它形态（流动/闪烁） | 只用到了 `sin(t*rate)` 做透明度呼吸；`frac(t*rate)` 的流动式未试 |
| HF 实验台回归 | 这批结论全部来自零售版，HF 侧未同步 |
| 别的模型 / 别的材质族 | 只验证了 `e577db2c`（Milfy CE-07 的 11 槽 advanced 根材质）这一条 |

### 5.1 现在可复现的成品变体

| 变体 | 用途 |
|---|---|
| `famA_product3` | **成品**：黑丝袜，镂空 + 半透明 + 窄高光 + 透明度呼吸 |
| `famA_product2` | 上一版（更亮，留作对比） |
| `famA_diag` | **诊断版**：白/洋红/绿/灰四色带 + 海浪 alpha，用于一眼判断 alpha/镂空是否生效 |
| `famA_cutout_probe` | 单项判据：`uv.x<0.30` 确定性丢弃，证明 `discard` 成立 |
| `baseline` | 作者原始产物，出问题一律回这里 |

参数入口：贴图四通道（R 镂空 / G,B 底色 / A 透明度）在 `*.png` 里改；
着色器常量（`EXPOSURE` / `CUT_THRESH` / `METAL_K` / `ALPHA_PULSE_MIN` / `PULSE_RATE` …）在 HLSL 顶部改，改完重跑 §3 的第 4 步。
