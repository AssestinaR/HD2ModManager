# HD2 材质：离线状态复核 + 待验测试清单

> ⚠️ **部分过时（2026-11-16 标记）。** 本文写于 2026-11-15 凌晨，**当天下午的 milestone 已推翻其中两处**：
> ① 「整段替换 SHEX 无效」（§九表格里 `build_custom_ps.py` 那行）是错的 —— 根因是 NOP token 与记录头字段，
> 换对了就能执行（见 `hd2-custom-shader-milestone-20261115.md`）；
> ② 本文给出的待验测试清单，多数已在当天跑完，结果见 milestone / `hd2-custom-shader-runbook-20261115.md`。
> 证据分级（§一）与踩坑补记（§十）仍然有效。**照着做之前先看 milestone。**

> 2026-11-15 02:30。用户已休息，**本轮无法获得任何游戏内测试反馈**。
> 本文把已有结论按"证据强度"重新分级，并给出用户回来后按顺序执行的测试清单。
> 配套：`hd2-payload-carrier-and-dynamic-emissive-20261114.md`（技术档案）、
> `hd2-cutout-metal-runbook-20261114.md`（移植手册）。

## 一、证据分级（这是本文最重要的部分）

之前几轮我有两次**过早上调结论**，这里按证据强度重排。

### A. 已实机确认（至少两次独立复现，或对照实验排除了解释）

| 结论 | 证据 |
|---|---|
| `data\` 下是符号链接，改文件只能改库、不能改 `data\` | 反查链接 + 部署校验脚本多次通过 |
| 子材质 = `parent=<根id>` + `gpuSize=0` + **`undat2=0`** | 忘了改 `undat2` 时整片消失，改对后恢复 |
| **镂空 + 金属**（`alphaclip` 子材质 + 自造掩码 + `BaseColorMetalMap.a` 当金属度） | 实机：身体/头盔/衣服出现圆形孔洞 + 金属感 |
| **镂空 + 金属（在 `alphaclip+` 上也成立）** | 2026-11-15 实机：换 `0xc6042e3403385d40` 当父材质，四槽全绑自造贴图、`OpacityThreshold=0.35`，身体在 + 有金属 + 有镂空。**该族是 hard clip，没有混合 ⇒ 拿不到透明** |
| **静态自发光**（`advanced` 子材质 + `EmissiveColor/Intensity` + `AlbedoEmissive.a` 当遮罩） | 实机：全身皮肤均匀品红 |
| **等长改立即数/指令** 会改变画面 | 作者材质 mat 组 `T` 值、抖动幅度多次生效 |
| 引擎**不校验** DXBC 校验和 | 作者载荷里那条记录的校验和本就是旧值，仍能渲染 |

### B. 已被推翻（不要再引用）

| 曾认为 | 现状 | 推翻它的实验 |
|---|---|---|
| "整条记录替换 → 引擎执行外来 SHEX" | ❌ **不成立** | 原始 TOC + 同一份替换侧车 → 仍是黑丝基线。当初的变化来自 TocData |
| 换成自己用 fxc 编的 SHEX 能生效（整段） | ❌ 不成立（完整版、混合版都不生效） | 两次实机均无变化 |
| "`SclarFieldOpacity` 参与逐像素不透明度" | ❌ 在 `advanced`/`8f66` 上实测无浓淡 | 真渐变图绑定后无变化 |
| "带贴图的逐像素半透明成立" | ❌ **撤回**。那次"浓淡"来自**同一张图同时当底色和掩码**，是亮度变化不是 alpha | 底色换成彩色条纹、掩码不变 → 透明变均匀 |

### C. 待验（有现象但没有对照实验）

| 现象 | 为什么还不能算成立 |
|---|---|
| 隐身族子材质 → "整身近乎全透明 + 胶质光泽" | **没做过 `Opacity=1` 的对照**。而且见下面第二节：该族载荷里没有任何一条读 `c_per_object`，`Opacity` 很可能根本不起作用 |
| 衣服/头盔有镂空 + 身体有金属 | 同一材质驱动多部件，但"衣服没有金属"说明衣服另有材质 id，尚未分离 |

## 二、本轮离线新发现：隐身族载荷读不到材质变量

把 `0x818cf042a3fa9aae` 的 4.6 MB 载荷拆开（36 条记录、4 种形态），用 `fxc /dumpbin` 反汇编：

| 记录 | 份数 | RDEF 声明 | SHEX | 作用 |
|---|---|---|---|---|
| 4,468 B | 9 | `global_viewport` + `context_camera` | 1,256 | 视口/相机相关 |
| **3,340 B** | 9 | **只有 `global_viewport`** | 616 | **隐身叠加 pass**：按顶点色查 6 色渐变表，`mov o0.w, l(0.25)` |
| 860 B | 9 | 无 cbuffer | 224 | 顶点类小着色器 |
| 644 B | 9 | 无 cbuffer | 104 | 同上 |

**没有一条记录声明 `c_per_object`** —— 而材质变量（含 `Opacity`）正是通过 `c_per_object` 传给着色器的。

⇒ **离线推断**：把皮肤材质挂到隐身族根材质下，`Opacity` 变化**不改变任何着色器的输出**。
那次看到的"全透明 + 胶质光泽"更可能是：**身体根本没被画出来**，我们看到的是那个
写死 `o0.w = 0.25` 的叠加 pass（它按顶点色渐变 + 镜面）。

**这条必须在下次上机时用 `Opacity=1` 作对照**（见测试清单 T1）。

## 三、唯一有效的手段：等长改指令

现状是"整段 SHEX 替换不生效"，所以自制着色器的形态只能是**在原 SHEX 内改指令**。可用的编辑窗口：

| 可改的东西 | 例子 | 已验证 |
|---|---|---|
| 浮点立即数 | `mov o0.w, l(0.25)` → `l(1.0)`；`add r.x, r.x, l(-0.21)` → `l(-0.5)` | ✅ |
| 整数立即数（索引/常量） | 抖动阈值、比较常量 | ✅ |
| 操作码替换（等长） | `mul`(54) ↔ `add`(0) ↔ `min`(51) 等单操作数指令互换 | 未单独验证，但属同一机制 |
| 操作数寄存器/掩码 | 把 `r1.w` 换成 `r2.x` | 未验证 |

**反汇编器**：不要用 `.dsh/tmp/sm5disasm.py`（遇到 `dcl_globalflags` 就断）。
用 **fxc**：

```powershell
$fxc = 'C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\fxc.exe'
& $fxc /nologo /dumpbin /Fc out.asm <记录.dxbc>
```

它对游戏自带的 blob 反汇编**完全正常**（本文第二节的表就是这么得到的），还能列出
cbuffer 成员的偏移与是否被使用 —— 这是判断"某变量到底有没有被读"的最快手段。

## 四、待验测试清单（用户回来后按顺序做）

> 每条都只改**一个**变量；执行方式统一为
> `python tools/use_variant.py <变体名>`（会自动跑部署校验）。

### T0 · 【最优先】让自制着色器的 SHEX **长度与原版一模一样**（离线可备，值得第一个测）

**推理链**（本轮离线得出，尚未实机）：

| 观察到的事实 | 推论 |
|---|---|
| 改 SHEX **内部**的立即数/指令 → 生效 | 引擎会执行 SHEX 的**新内容** |
| 整条记录替换（SHEX 长度 52,220 → 39,672 / 60） → 从不生效 | 卡点不在内容，在**长度/块尺寸** |
| 载荷头部存在 `(size, hash32)` 表 | 很可能按"记录的块尺寸结构"做键或校验 |

⇒ **把自编 PS 的 SHEX 补到恰好 52,220 B**（`ret` 之后补 0，或在 HLSL 里加不会被优化掉的死指令），
其余块（RDEF 9,900 / ISGN 248 / OSGN 44 / STAT 148）**原样照抄**，记录总长仍 62,652。
这样"块尺寸结构"与原来完全一致，唯一差别是 SHEX 的字节内容 —— 而这正是**已被证明会生效**的情形。

- **预期**：皮肤变纯品红（= 我们的代码被执行）
- **判据**：颜色变化，而不是"有没有崩溃"
- **若仍无变化** ⇒ 卡点在 `STAT`（内容哈希）或载荷头表，那就转"只改指令、不改长度"的路线
- **离线准备**：`tools/build_custom_ps.py` 已能做定长记录，只需加一个 `--pad-shex <字节数>` 选项

### T1 · 隐身族到底认不认 `Opacity`（必做，5 分钟）

- **变体**：`cloak_opacity1`（待生成）= 现状的 `all_cloak_translucent`，但 `Opacity = 1.0`
- **对照**：已有的 `all_cloak_translucent`（`Opacity = 0.5`）
- **预期**：若两者**完全一样** ⇒ `Opacity` 无效，之前的"真透明"结论作废
- **判据**：只对比"能不能看到身后的地板"

### T2 · 那个叠加 pass 是不是我们看到的东西（必做）

- **做法**：自带一份隐身族载荷（作为**我们自己的根材质**，`parent=0`），把
  `mov o0.w, l(0.25)` 的立即数（载荷内偏移 `0xc68`，见下）改成 `1.0`，皮肤材质挂到它下面
- **预期**：若整身变成**不透明的顶点色渐变表面** ⇒ 证实之前看到的就是这个 pass
- **判据**：不透明 vs 透明
- **注意**：需要新增一条 `parent=0` + 自带载荷的材质条目（作者那份材质就是这个形态，合法）

### T3 · 用"自带根材质"验证改指令能不能改出 alpha

- **做法**：同 T2 的根材质形态，把 `mov o0.w, l(0.25)` 改成别的常量（如 `0.75`/`0.0`），各测一次
- **判据**：画面透明程度随这个常量单调变化 ⇒ **等长改指令可以控制不透明度**

### T4 · 真正的目标：让 alpha 来自贴图

- **前提**：T3 成立
- **做法**：该 pass 目前不采样任何贴图。要"贴图控制 alpha"，需要一条 `sample` 指令 ——
  而它是**整段 SHEX 里没有的**，只能靠"借用同长度指令位"或改 `STAT` 之外的
  另一条采样路径实现。**这是硬骨头，先别做**，等 T1~T3 的结论出来再定。

### T5 · 镂空密度/图案调优（可选，纯观感）

- `tools/make_honeycomb_mask.py --radius / --pitch` 调孔洞密度；也可做方形阵列、条带
- 判据：孔洞大小/密度符合预期

## 五、离线可继续做的事（不需要用户）

1. **统计 `c_per_object` 的读取情况**：扫全库 550 个自带载荷的根材质，找出**哪些根材质的
   材质主 pass 真的声明并读取了 `c_per_object`**，并按 `Opacity`/`Albedoopacity01` 等
   关键字排序 —— 这份表能直接指出"可变透明的候选根材质"，取代目前靠猜测的做法。
2. **核对 `STAT` 的作用**：比较"整段替换"与"等长改指令"两种情况下 `STAT` 的差异，
   判断引擎是否用它做配对校验（这决定"整段替换"还有没有救）。
3. **检索 Stingray/Bitsquid 的透明管线资料**，确认角色表面是否有逐像素 alpha 的前例。

## 六、根材质载荷的 `c_per_object` 扫描（离线，56 份载荷）

材质变量（`Opacity`、`EmissiveIntensity`、`MetallicOpacity`…）是通过 `c_per_object` 传进着色器的。
所以"某根材质的 shader 到底认不认材质变量"，只要看它的材质主 pass 有没有声明这个 cbuffer 即可。
扫了手上 56 份载荷，**31 份读、25 份不读**：

| 类别 | 代表 | 结论 |
|---|---|---|
| **读 `c_per_object` / `c0`（能吃到材质变量）** | `d84d04634a1e2f60`(alphaclip,1.89MB)、`9fc2c6b713891a3b`(3.14MB)、`ef9b3fc2fc1ba61e`(2.79MB)、`a991793474328c30`、`ec555165044bb60a`、`7b9f6c5638fcb231`、`71ed0820a138df90`、`0cd35de1a979a0f5` | 变量这条路**有生理基础**；其中 alphaclip / 9fc2 / advanced 已实机确认能渲染 |
| **不读（材质变量无效）** | **`818cf042a3fa9aae`（隐身族）**、`8f669f365f24594e`、`25ea2c4516ba68fd`、**`e47e9db135ae02ef`（软片/VFX）**、以及一堆 `context_camera` 类 | 把材质挂在这些下面，**改变量不会有任何反应** |

**这直接推翻了两条此前的判断**：

1. 隐身族 `818cf04` 的载荷里最大那 9 条 pass 只声明 `global_viewport` + `context_camera`，
   **没有 `c_per_object`** ⇒ 之前的"`Opacity` 让它变透明"极可能是误判（见测试清单 T1）。
2. 软片/VFX 族 `e47e9db1` 也不读 `c_per_object` ⇒ 那条"粒子类能不能上角色网格"的实验
   即使网格认了，材质变量也不会生效。

**方法学收获**：以后挑候选根材质，先查它的主 pass 是否声明 `c_per_object`（脚本见下），
再谈能力 —— 比"看槽名/看载荷大小"可靠得多。

```powershell
# 快速判断任意载荷的主 pass 读不读材质变量（内联脚本已保存在会话记录里，
# 核心是解析 RDEF 的 cbuffer 名列表，找 c_per_object / c0）
```

## 八、作者那份着色器的完整改法（离线解析完成，2026-11-15）

用 `fxc /dumpbin` 把作者材质的主 pass（62,652 B）与本体 `advanced` 的同名 pass（60,412 B）
各自反汇编后逐行比对：**输入签名完全相同**，差异是 `dcl_temps 21 → 30`、指令槽 1,578 → 1,652，
以及**尾部新增的 24 条指令**。

### 他到底改了什么

```asm
; ── 本体 advanced 的尾部 ──
div_sat o0.xyz, r1.xyzx, r0.xyzx
mov     o0.w, l(1.000000)        ; ★ alpha 写死 1.0
ret

; ── 作者的尾部 ──
add     r22.xyz, -v1.xyzx, cb1[4].xyzx    ; V = 相机位置 - 世界位置
dp3     r21.w, r22.xyzx, r22.xyzx
rsq     r21.w, r21.w
mul     r22.xyz, r21.wwww, r22.xyzx       ; 归一化
dp3_sat r21.w, r21.xyzx, r22.xyzx         ; N·V
add     r21.x, r21.w, l(-0.210000)        ; N·V − 0.21
mul     r21.x, r21.x, l(1.265823)         ; × 1/(1−0.21)
max     r21.x, r21.x, l(0.000000)         ; saturate → t
mad     r21.y, r21.x, l(-2.0), l(3.0)     ; 3−2t
mul     r21.x, r21.x, r21.x               ; t²
mul     r21.x, r21.x, r21.y               ; t²(3−2t) = smoothstep
mad     r21.yz, v3.xxyx, l(0,0.5,0.5,0), l(0,0.5,0,0)   ; 屏幕 UV
max     r21.yz, r21.yyzy, l(0,0.500122,0.000122,0)
min     r21.yz, r21.yyzy, l(0,0.999878,0.499878,0)
sample_l_indexable(texture2d) r21.yz, r21.yzyy, t7.zxyw, s0, l(0)   ; 采样抖动纹理
mul     r21.z, r21.z, l(0.266000)         ; × 0.266
mad     r21.x, -r21.z, r21.x, l(1.0)      ; 1 − dither·curve
mul_sat r27.w, r21.x, r21.y
mov     o0.w, r27.wwww                    ; ★ alpha = 逐像素计算值
ret
```

### 结论（**离线推断，待实机**）

1. **`o0.w` 就是材质的不透明度**（本体是常量 1.0）。作者把它换成了 `N·V` + 抖动纹理的逐像素函数
   ⇒ 之前"deferred 光照权重"的解释可以退场。
2. 该 pass **本来就在采样贴图**（`t8`=`__tex_albedo_emissive`、`t7`=`__tex_detail_normal_leather`、`t5`、`t4`…），
   所以"贴图驱动的 alpha"在这条 pass 上是**天然可行**的，不需要新着色器。
3. 作者靠"加长记录"实现（49,980 → 52,220）。在他的自带载荷里这是自洽的；
   我们要改的话，**最好仍保持等长**（理由见四节 T0）。

### 具体改法（下一步的实作方案）

作者尾部有两个**没人再读**的临时寄存器 `r28`/`r29`：

```asm
mul_sat r27.w, r21.x, r21.y
mov r28.xyzw, l(0,0,0,0)      ; ← 可改写
mov r29.xyzw, l(1,1,1,1)      ; ← 可改写
mov r0.xyz,   l(0,0,0,0)
...
mov o0.w, r27.wwww            ; ← 把源寄存器改成 r28.w（1 个 dword）
```

把前两条 `mov` 换成一条与现有形式完全相同的采样：

```asm
sample_l_indexable(texture2d) r28.xyzw, v3.xyxx, t8.xyzw, s1     ; t8 = 我们的底色贴图
```

再把 `mov o0.w, r27.wwww` 改成 `mov o0.w, r28.wwww` —— **三个改动点、全部等长**，
`o0.w` 于是等于**我们贴图的 alpha**，即"带贴图的逐像素真透明"。

**注意**：`v3.xy` 是这条 pass 采底色用的同一组 UV（`sample r6, v3.xyxx, t8.xyzw, s1`），
所以贴图与底色天然对齐。

**待办（需要写编码器）**：实现一个"在 SHEX 内等长改写指令块"的工具
（需要 `nop`(56)、`mov`(52)、`sample_l_indexable`(71) 三个编码器）。
这是自制材质真正落地的最后一块工程。已有基础：
`docs/agent/...` 里的 token 格式说明 + `fxc /dumpbin` 可用来验证每条改写的编码是否合法
（把改好的 SHEX 抽出来丢给 fxc，能反汇编回我们期望的指令即编码正确）。

## 八-B、外部资料佐证（Stingray 的 gbuffer 材质确实带逐像素 opacity）

另一款使用同一引擎（Bitsquid/Stingray）的游戏 **Vermintide** 的社区 Mod 框架里，公开了引擎的
**shader node 定义**，其中 `standard_base` 节点声明：

```lua
"34259752-b962-4b65-92c3-903a57338519" = { name = "opacity"          type = { scalar: ["HAS_OPACITY"] }          domain = "pixel" }
"7a9306c6-95ae-4cdb-9fef-0eedacce4e83" = { name = "opacity_threshold" type = { scalar: ["HAS_OPACITY_THRESHOLD"] } domain = "pixel" }
...
gbuffer_material = { inherit: ["culling_base_gbuffer"] ... }
// with refraction we are essentially taking over the blending in our shader, so we disable alpha blending
```

来源：[Vermintide-Mod-Framework `standard_base.shader_node`](https://raw.githubusercontent.com/Vermintide-Mod-Framework/Vermintide-Mod-Framework/4960fb5e0e71e9b6ba8ebb3bb37fea4fd94d530d/vmf/core/stingray_renderer/output_nodes/standard_base.shader_node)、
[Stingray Renderer Walkthrough #4: Sorting](http://bitsquid.blogspot.com/2017/02/stingray-renderer-walkthrough-4-sorting.html)。

**三条可用的推论（离线，待实机确认）**：

1. **逐像素 `opacity` 是引擎的标准输入**，落在 `domain = "pixel"` ⇒ 与"作者把 `o0.w` 写成逐像素函数"
   完全吻合，`o0.w` = 材质不透明度的判断得到独立佐证。
2. **gbuffer 材质默认支持 alpha 混合**（注释里说只有折射那条路为了自己在 shader 里接管混合才关掉它）。
   这解释了我们观察到的现象：同一个材质，换个 pass 就从"实心"变成"半透"。
3. **`HAS_OPACITY` / `HAS_OPACITY_THRESHOLD` 是编译期特性位**（shader 的 `type` 里带 feature 列表）
   ⇒ 混合与否由**编译进着色器的特性**决定，不是材质侧能开关的。
   这也解释了为什么"继承一个有透明能力的根材质"必须靠**换根材质**，而不能靠改变量。

顺带：`StingrayPBS` 在 Maya 里的透明材质就是 `presets/Standard_Transparent` + 接一个 `opacity`
材质变量 —— 工作流层面也印证了同一条通路。[Autodesk 帮助](https://help.autodesk.com/cloudhelp/2025/CHS/Maya-LightingShading/files/GUID-C84FB2BF-94E4-4F5F-83DD-C1A22907081B.htm)

## 八-C、SHEX 容器的头部布局（本轮新发现，之前一直踩这个坑）

```
SHEX 块 = "SHEX" + u32 payload_size
payload = u32 [未知/版本]  +  u32 dword_count  +  指令流
                                    ↑ 实测 0x000032ff = 13055 = 52220/4（**含前两个 dword 在内的总 dword 数**）
```

**指令流从 `SHEX+16` 开始**（fourcc 4 + size 4 + 两个头 dword 8）。

之前"按长度字段遍历"总是错位，就是因为把起点当成了 `SHEX+12`（漏掉第二个头 dword）。

## 八-D、可改写的空间已确认（`r28` / `r29` 是死代码）

对作者那份 pass 做了寄存器生命期检查：

```
r27：L1888 写 xyz → L1901 写 w → L1910 被 mov o0.w 读     （唯一的 alpha 来源）
r28：L1902 只写一次（l(0,0,0,0)），全程再未被读            ← 死代码，可借用
r29：L1903 只写一次（l(1,1,1,1)），全程再未被读            ← 同上
```

⇒ 把这两条 `mov` 换成一条 `sample ... r28.xyzw, v3.xyxx, t8.xyzw, s1`（`t8` = `__tex_albedo_emissive`），
再把 `mov o0.w, r27.wwww` 的源换成 `r28.wwww`，就能让 `o0.w` = **我们贴图的 alpha**。
两处改动合计约 12 dword 的空间，足够放一条 sample + 若干 `nop`。

### 但**改写器本轮没做完**（诚实记录）

我尝试自己按 token 长度遍历来定位指令，两次都错位（一次漏了上面的 8 字节头，一次被
`dcl_globalflags` 的立即数带偏）。**这个方向的问题不是引擎，是我自己的字节层解码不可靠。**

**下一步应采用闭环做法，不要靠手算偏移**：

1. 先用 `fxc /dumpbin /Fc` 拿到**权威反汇编**，确定要改哪几条、按顺序是第几条；
2. 在 SHEX 指令流里按 **dword 模式搜索**定位（例如 `mov o0.w, r27.wwww` 的 5 个 dword），
   而不是靠计算的偏移；
3. 等长替换后，**把改好的记录再交给 fxc 反汇编** ——
   能正常反汇编且出现预期指令 ⇒ 编码正确；报错或指令不对 ⇒ 立刻回退。
   这一步让"改指令"变成**可离线自验证**的，不需要用户上机。

### ⛔ 离线自验证这条路被封死（本轮实测，重要限制）

想把"改好的记录再交给 fxc 反汇编"当作自验证，**做不到**：

| 尝试 | 结果 |
|---|---|
| 未改动的原始记录 | ✅ fxc 正常反汇编（101 行） |
| 改了 4 字节立即数的记录 | ❌ `failed writing`，asm 为空 |
| 把 16 字节校验和清零后再 dump | ❌ 同样失败 |

⇒ fxc 对**校验和不匹配的 blob 一律拒绝反汇编**，而校验和算法未解（试过 10+ 种 MD5 变体）。
**绕开 fxc 也不行**：直接 P/Invoke `d3dcompiler_47.dll` 的 `D3DDisassemble` 对改过的 blob 返回
`HRESULT 0x80004005 (E_FAIL)` —— 它同样校验。**这两条路都已实测封死，不必再试。**

### ✅ 可行的替代：让 fxc **生成**目标指令的字节，而不是验证

思路换一下 —— 我们要的从来不是"验证任意字节"，而是"**拿到一条正确编码的指令**"：

1. 写一段最小 HLSL，让 fxc 编出**形状完全相同**的指令，例如：
   ```hlsl
   Texture2D<float4>  t : register(t0);
   SamplerState       s : register(s0);
   float4 main(float4 uv : TEXCOORD0, float4 col : COLOR0) : SV_Target
   { return t.Sample(s, uv.xy); }
   ```
2. 这份 blob 的**校验和是合法的**，所以 `fxc /dumpbin` 能正常反汇编 ⇒
   从它的 SHEX 里**取出那条 `sample` 的确切字节**（对照 asm 行确认是哪几个 dword）。
3. 把那段字节搬进目标 SHEX，**只改操作数里的寄存器号**（t#、s#、目标 r#）。
   这些编号字段的位置可以用"同一 blob 内两条同类指令的字节差异"经验确定。
4. 由于第 2 步是 fxc 亲手生成的，**编码本身一定是合法的**；风险只剩"操作数改错"。

这条路径把"不可验证"降级成"编码可信 + 操作数需谨慎"，是目前最现实的做法。


## 八-E、两个样本的定位（goal ② 完成）

| 样本 | `o0.w`（不透明度） | 主要改动 | 用途 |
|---|---|---|---|
| 本体 `advanced` | `mov o0.w, l(1.000000)` | 基准 | — |
| **作者丝袜** `0xe577db2c…` | `mov o0.w, r27.wwww` | N·V + 抖动纹理 → **逐像素不透明度**（+74 槽，`dcl_temps` 21→30） | **唯一动过 opacity 的样本 —— 做透明要抄的就是它** |
| **全息背包** `0xdec4b7db…` | `mov o0.w, l(1.000000)` | `dcl_temps` 30→35、代码结构不同，但**完全不碰不透明度** | 自发光/扫光类效果的模板 |

⇒ "真透明"只需抄作者那一份；全息背包与透明无关（它动的是自发光通路）。

## 九、资源与工具索引


| 资源 | 位置 |
|---|---|
| 八个根材质（裸 TocData + 载荷） | `.dsh\roots\<id>\`（12.6 MB） |
| 变体库（每个 = 基线 + 一个能力） | `.dsh\variants\`（含 NOTE.txt 说明） |
| 一键切换 + 部署校验 | `python tools\use_variant.py --list` / `<变体名>` |
| 权威反汇编 | `fxc /dumpbin /Fc out.asm <记录.dxbc>`（**只对未改动的记录有效**） |
| 自研反汇编（备用） | `tools\shex_disasm.py`（有操作数解码误差，遇到 `dcl_globalflags` 会断，仅作参考） |
| SHEX 指令定位（按长度遍历） | `tools\shex_locate.py`（**起点要用 `SHEX+16`，见八-C**） |
| 等长 dword 模式替换 | `tools\shex_patch.py`（自带 fxc 复验；对改过的 blob 复验会失败，见八-D 的限制） |
| 自制 PS 编译 + 定长记录 | ~~`tools\build_custom_ps.py`~~ → **`tools\build_signed_ps.py`**（本行原文"整段替换无效"已被 11-15 milestone 推翻） |
| 蜂窝掩码生成 | `tools\make_honeycomb_mask.py` |
| 材质合成 / 加贴图 / 原地写贴图 | `tools\compose_material.py` / `add_texture.py` / `embed_texture_data.py` |
| 部署硬校验 | `tools\verify_deployed.py` |
| 镂空+金属 移植手册 | `docs\agent\hd2-cutout-metal-runbook-20261114.md` |

## 十、踩坑补记（新增）

1. **"比对类"结论必须打印双方内容**，不能只给 true/false。本轮修掉两个工具里的
   `d[s+o+8:end]`（少加 `s`）bug，它的症状是**静默返回空块**，足以让整个签名比对失真。
2. **任何"某个变量起作用了"的结论，必须有"变量取另一个值"的对照**。`Opacity` 至今没有对照。
3. **同一张贴图不能既当底色又当掩码** —— 否则看到的明暗变化无法归因给 alpha。
