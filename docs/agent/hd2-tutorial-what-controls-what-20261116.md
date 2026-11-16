# 教程二：半透明、自发光、金属、镂空、动态 —— 分别由什么控制

> 2026-11-15/16。给 `cutout-metal-real`。
> 教程一（`hd2-tutorial-parent-root-shader-20261115.md`）讲的是"改哪里"；
> 这一篇讲**"改的是哪一行"** —— 你在那边卡住十有八九是缺这个。
>
> 配一份**教学补丁**：`E:\tmp\hd2-teach\9ba626afa44a3aa3.patch_73\`（已构建、已过四道门）。

---

## 0. 心智模型（一句话）

> **这条像素着色器返回什么，屏幕上就是什么。没有"透明开关"，没有"发光开关"，没有"金属参数"。**

你之前一直在找"引擎怎么接受我的材质/载荷"，但真正的问题是：
**我们从这条 pass 里拿到的是一张白纸 —— 像素的颜色和透明度由我们写死。**

引擎只负责三件事：把输入喂给你、按 **pass 自带的混合状态** 把 `o0` 混进屏幕、按 `discard` 丢弃像素。
其余全靠你写。

---

## 1. 五条能力，各自就是一行代码

| 能力 | 由什么控制 | 代码 |
|---|---|---|
| **半透明** | `o0.w` 这个数 | `o0 = float4(color, a)`；`a` 越小越透。这条 pass 本身是混合的，所以写什么都按 alpha 混 |
| **自发光** | 你往 `o0.rgb` 里**加**的亮度 | `o0.rgb = base + glow`；`glow` 越大越亮。引擎不会替你区分"受光"和"自发光" |
| **金属** | 你自己算的高光，也是加进 `o0.rgb` | `spec = pow(saturate(dot(N,H)), GLOSS); o0.rgb += sun * spec * K` |
| **镂空** | `discard` 关键字 | `if (mask < T) discard;` 一条语句搞定，硬边 |
| **动态** | `cb0[43].w`（游戏时间，秒） | 任何带 `t` 的表达式：`sin(t*rate)`、`frac(t*rate)` |

**没有的东西**（这才是你一直找不到的）：
- 没有 `EmissiveIntensity` / `Opacity` 这类"材质参数"被传进这条 pass —— 这条 pass 的 cbuffer 只有相机/太阳/集群光照，**没有任何每材质的标量**。
- 没有"引擎自带的金属光照" —— 这条 pass 不给你 BRDF，给了你也得自己算。
- 材质槽里那些参数（`Metallic`、`EmissiveIntensity`…）在**另一条 pass**（`62,652 × 10`，1 RT）里，
  而我们在实机上把那条整个涂成亮青，**画面毫无变化** —— 它对这套网格不画。

**结论**：想调"金属度""发光强度""透明度"，唯一的地方就是你自己的着色器常量 + 贴图通道。

---

## 2. 为什么你之前的 6 次实验都不成立（一句话版）

| 你们的做法 | 为什么不行 |
|---|---|
| 把 advanced 的载荷搬到别的材质上 | 载荷是**绑材质**的编译产物：里面 88 个 pass 哈希，与 `d84d0463` 只交集 1 个。搬过去，对方要找的 pass 不存在 |
| 给**子材质**设 `undat2`/`gpuSize` | MourningVeil 的 92 个材质全是子材质（`undat2=0`），这份文件里没有"子材质带载荷"的形态 |
| 覆盖本体根材质（`patch_73`） | 形态对，但**记录布局错了**：RDEF 9900→12172、SHEX 49980→39672、STAT 148 变成 PRIV 8184。四道门里 G1 直接不过 |

搭一次正确形态的记录只有三条硬规矩：
1. 五个 chunk 的**名字、顺序、长度**与 donor 逐项相同，只换 SHEX 里的字节；
2. `ret` 之后补 **真 NOP `0x0100003A`**（`0x01000038` 是另一条指令 → 整条 pass 被丢）；
3. 读的输入分量 **⊆ 记录 ISGN 的掩码**（越界 → dxilconv 空指针崩溃）。

---

## 3. 教学补丁：一次启动，五种效果全看到

```
E:\tmp\hd2-teach\9ba626afa44a3aa3.patch_73\
    9ba626afa44a3aa3.patch_0                 2472 B
    9ba626afa44a3aa3.patch_0.gpu_resources 2928416 B
    .hd2modmanager-mod.json                   新 mod id
```

它覆盖本体根材质 `0xef9b3fc2fc1ba61e`（`parent=0`、`undat2=gpuSize=2,928,416`、载荷头 `+4=0xef9b3fc2`，
TocData 与本体逐字节相同），把三组大记录全部换成同一段**教学着色器**：

| 组 | 记录 | 条数 | SV_Target |
|---|---|---|---|
| #7 | 60,412 | ×10 | 1 |
| #39 | 26,876 | ×3 | 4 |
| #43 | 35,576 | ×9 | **5** |

三组都过了四道门（G1 布局逐项一致 / G2 校验和 / G3 读集为空 / G4 ret 后真 NOP）。

### 3.1 着色器做了什么（`hlsl/g7.hlsl`、`g39.hlsl`、`g43.hlsl`，可直接读）

```hlsl
cbuffer VP : register(b0) { float4 vp[51]; };
struct PSIn { float4 pos : SV_Position; };          // 只读 SV_Position，任何 ISGN 都安全

PSOut main(PSIn i)
{
    float  t = vp[43].w;                            // 游戏时间
    float2 p = i.pos.xy;                            // 屏幕坐标

    float s = frac((p.y + p.x * 0.6) * 0.020 - t * 0.25);
    if (s < 0.35) discard;                          // ← 镂空：屏幕空间斜条纹

    float a = 0.20 + 0.80 * (0.5 + 0.5 * sin(t * 2.0 - p.y * 0.010));   // ← 半透明带状呼吸

    float  g = 0.35 + 0.65 * (0.5 + 0.5 * sin(t * 3.0));               // ← 自发光呼吸
    float3 c = float3(0.0, 0.55, 0.32) * g;                            //   祖母绿

    PSOut o;  o.o0 = float4(c, a);  /* 其余目标同色 */  return o;
}
```

### 3.2 部署与判据

```powershell
# 1) 拷进 mod 库
Copy-Item 'E:\tmp\hd2-teach\9ba626afa44a3aa3.patch_73' 'E:\mod\' -Recurse -Force
# 2) 关掉其它覆盖同一个 bundle 的 mod（尤其原来的 9ba626afa44a3aa3.patch_73）
# 3) 管理器刷新 → 启用 adv-root-teaching-fx → 部署 → 进游戏
```

| 看到 | 含义 |
|---|---|
| 身上出现**祖母绿斜条纹**、条纹边缘是硬的 | 着色器被执行、`discard` 生效 |
| 条纹**以约 3 秒周期明暗呼吸** | 时间源 `cb0[43].w` 可用、自发光项生效 |
| 条纹之间有**宽度周期性变化的透明带**（能透到背后） | `o0.w` 参与混合、半透明成立 |
| **完全没变化** | 覆盖本体根材质这条路线在你们环境里不成立（见教程一 §8） |

⚠️ 会污染所有用 advanced 材质的物体（护甲、武器都可能条纹化），属预期。回退 = 关掉这个 mod。

---

## 4. 从教学版到生产版：把"屏幕空间"换成"贴图"

教学版用 `pos.xy` 是为了绕开 ISGN 语义不确定。生产版要换成材质自己的 UV 和贴图：

```hlsl
// 生产版骨架（我们实机跑通的形态）
float4 s = t2.Sample(s0, uv);          // t2 = __tex_roughness_map（每材质独立）
if (s.r < CUT_THRESH) discard;         // R 通道 = 镂空遮罩
float  a = s.a;                        // A 通道 = 逐像素透明度
float3 base = float3(s.g, s.b, s.g);   // G,B = 底色（R 不参与，否则遮罩会染色）
float3 col  = base * (AMBIENT + (1-AMBIENT)*saturate(dot(N,L)))
            + sun * pow(saturate(dot(N,H)), GLOSS) * METAL_K;
o0 = float4(saturate(col * EXPOSURE), a);
```

四个通道的分工就是**参数面板**：换贴图即换外观，不用重编译。
贴图必须是 **4096² / BC7 / 13 级 mip / 像素数据正好 22,369,648 B**，用
`tools\embed_texture_data.py --patch <patch_1> --texture 0x8354a7fad81a2a29 --dds <x.dds>` 原地替换。

---

## 5. 每条 pass 的 ISGN 语义**不一样**，必须自己查

这就是"猜错就崩"的地方。同一个根材质里：

| 记录组 | 法线 | 世界坐标 | UV |
|---|---|---|---|
| `60,504`（我们 Milfy 那条 3 RT） | `v2.xyz` | `v3.xyz` | `v7.xy` |
| `35,576`（本体根 5 RT） | `v2.xyz` | **`v1.xyz`** | **`v3.xy`** |

查法（两条 grep 就够）：

```powershell
# 1) 把记录单独切出来反汇编
fxc /nologo /dumpbin /Fc out.asm rec.dxbc
# 2) 找归一化 -> 法线；找 `add -vX.xyzx, cb0[2].xyzx` -> 世界坐标（cb0[2] 是相机位置）
Select-String out.asm -Pattern 'dp3\s+\S+,\s+v\d\.xyz'
Select-String out.asm -Pattern 'cb0\[2\]'
# 3) 找 `sample... rX.xyxx, vY.xyxx, t2` -> UV
Select-String out.asm -Pattern 'sample_indexable.*v\d\.xyxx'
```

---

## 6. 三个会咬人的数值坑

1. **曝光**：`o0` 是**线性**值，屏幕上是 `pow(linear, 1/2.2)`。
   `0.25 线性 → 0.53 屏幕`，看着就是"乳白"。我们连着两版以为是高光太强。
   **内部乘一个 `EXPOSURE`（成品用 0.20），把线性压到 ~0.02 才真的发黑。**
2. **动态呼吸**：调**亮度**几乎看不见（±8% 在暗底上），调**透明度**一眼可见
   （`a` 在 35%~100% 之间摆）。
3. **自发光很容易盖掉一切**：从 `EMIS_K 0.1` 起步，每档翻倍试。

| 线性 | 屏幕 | 观感 |
|---|---|---|
| 0.01 | 0.12 | 很暗 |
| 0.02 | 0.17 | 暗 |
| 0.05 | 0.26 | 深灰 |
| 0.25 | 0.53 | 中亮灰（"乳白"） |
| 0.50 | 0.73 | 亮 |

---

## 7. 工具箱（我工作区 `E:\Data\source\repos\WpfApp1\tools\`，用绝对路径调）

| 工具 | 用途 |
|---|---|
| `payload_map.py <载荷>` | 列出每一组记录：stage / ISGN / OSGN / SHEX / **SV_Target 数** / 条数 / 偏移 |
| `material_graph.py <mod 目录>` | 谁自带载荷、谁是子材质、parent 是谁 |
| `build_signed_ps.py --hlsl a.hlsl --payload <载荷> --donor-size N --shex-size M --out <出> --stat ours --verify` | 编译 + 定长 NOP + 建记录 + 复签 |
| `gate_build.py record --record <rec> --donor <载荷> --donor-size N --ours-asm <asm>` | **四道门** |
| `teaching_fx_build.py --payload <载荷> --out <出> --groups 7,43,39` | 批量给若干组套"教学着色器"（本补丁就是它生成的） |
| `payload_probe_build.py --payload <载荷> --out <出> --paint 7:1,0,0,1` | 按组号涂纯色 |
| `embed_texture_data.py` / `make_stocking_texture.py` / `make_diag_texture.py` | 贴图链路 |

---

## 8. 建议顺序（别再跳步）

1. **跑教学补丁**，确认"覆盖本体根材质"这条路在你们机器上成立（§3.2）。这是总闸，没这一步后面全是空谈。
2. 成立之后，用 `teaching_fx_build.py --groups <你查到的那一组>` 把范围缩到**一组**，确认是哪条 pass。
3. 再把屏幕空间换成材质 UV + 贴图（§4），这时才需要处理你们的宝石材质和它的贴图槽。
4. 最后才谈"只影响宝石"：在贴图里埋标记色、着色器分支走默认/自定义（教程一 §4）。
