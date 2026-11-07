# L4D2 / Source 引擎权威事实表

抓取日期：2026-11-03 · 来源清单见 [`INDEX.md`](./INDEX.md)
规则：**每条事实后面都标出处**；抓不到或来源没写的一律标「未证实」，不推测成本文结论。

出处简写：

- `[VDC jiggle]` = https://developer.valvesoftware.com/wiki/$jigglebone
- `[VDC skeleton]` = https://developer.valvesoftware.com/wiki/Skeleton
- `[VDC vlg]` = https://developer.valvesoftware.com/wiki/VertexLitGeneric
- `[VDC phong]` = https://developer.valvesoftware.com/wiki/$phong
- `[VDC envmap]` = https://developer.valvesoftware.com/wiki/$envmap
- `[VDC envmapmask]` = https://developer.valvesoftware.com/wiki/$envmapmask
- `[VDC selfillum]` = https://developer.valvesoftware.com/wiki/$selfillum
- `[VDC alphatest]` = https://developer.valvesoftware.com/wiki/$alphatest
- `[VDC translucent]` = https://developer.valvesoftware.com/wiki/$translucent
- `[VDC detail]` = https://developer.valvesoftware.com/wiki/$detail
- `[PBR2S]` = https://developer.valvesoftware.com/wiki/PBR-2-Source
- `[mdlc README]` = https://github.com/MoRanYue/mdlc（README.md）
- `[mdlc src/xxx:行]` = 同仓库源码，文件名 + 行号（2026-11-03 的 `main`）

---

## 1. jigglebone（`$jigglebone`）

### 1.1 总体语义与语法

- `$jigglebone` 是 QC 命令，**Source 2007 起所有 Source 游戏**可用；把骨骼标记为「jiggle bone」，引擎在运行时动态模拟（抖动天线、松软肉体、下垂耳朵等）。`[VDC jiggle]`
- 语法：`$jigglebone <name> { <property group> { <property> <value> [<value>] ... } }`，`<name>` 是**关节 tip 的名字**（joint tip 名，不是父骨骼名）。`[VDC jiggle]`
- 恰好四种属性组：`is_rigid`、`is_flexible`、`has_base_spring`、`is_boing`（仅 v48 / GMod 分支）。每组都可省略。`[VDC jiggle]`
- **互斥**：`is_rigid` 与 `is_flexible` **不能同时用**；`has_base_spring` 与 `is_boing` **不能同时用**。`[VDC jiggle]`
- 一个 `$jigglebone` 可以同时有 `is_rigid`/`is_flexible` + `has_base_spring`（示例 `FloppyAndBouncy`）。`[VDC jiggle]`
- 引擎侧实现是**一个扁平记录 + 后写覆盖**：同一个键在多个块里出现时，按 QC 中出现顺序写入，**后写的赢**；`flags` 是按位 OR 累积（只有 `0x20` 会被 `allow_length_flex` 清掉）。实测 `jig47`/`jig48` 双向验证。`[mdlc src/compile.rs:7356-7370]`

### 1.2 ★「质量点沿骨骼局部 +Z 伸出」约定（关键约定）

> **Warning: The simulated tip of jiggle bones extends in the +Z axis of the bone, bones intended to be jiggle bones should always be orientated to account for this fact when creating them in the model editor.** `[VDC jiggle]`

即：模拟质量点/摆动端永远在骨骼局部 **+Z** 方向上，所以建骨时必须让骨骼的 +Z 指向「希望摆动的那一端」。

补充约束：`is_flexible` 下「在离骨骼基部设定距离处的 +Z 轴上放置一个模拟质量，骨像弹性杆，**只产生旋转运动**，旋转中心在 jiggle bone 的基部」。`[VDC jiggle]`

另有两条官方警告：`$proceduralbones` 作为父级时可能出现意外抖动（应把程序化骨骼的轴调成与父骨相同轴）；若希望 jigglebone 先受动画影响再抖，动画要编到单独 `.mdl` 再用 `$include` 引入。`[VDC jiggle]`

### 1.3 四组属性组各自的语义

| 属性组 | 质量点在哪 | 运动类型 | 出处 |
|---|---|---|---|
| `is_flexible` | 骨骼基部起、沿 +Z 的 `length` 距离处 | **仅旋转**（像弹性杆），旋转中心在骨骼基部 | `[VDC jiggle]` |
| `is_rigid` | 骨骼**末端** | **仅旋转**（像刚性棍子），旋转中心在骨骼基部 | `[VDC jiggle]` |
| `has_base_spring` | **骨骼基部** | **仅平移**（整根骨骼在 X/Y/Z 上位移） | `[VDC jiggle]` |
| `is_boing` | —（不涉及质量点） | 正弦 squash & stretch 缩放/拉伸；骨骼速度超过阈值时触发 | `[VDC jiggle]` |

### 1.4 每个参数的含义与合法范围

#### is_flexible 专有

| 参数 | 含义 | 范围/单位 | 出处 |
|---|---|---|---|
| `yaw_stiffness` | 局部 yaw 旋转轴上的弹簧刚度 | 合法值 **1..1000**（10 很松，500 很硬） | `[VDC jiggle]` |
| `yaw_damping` | 局部 yaw 轴的阻尼；小值抖得夸张，大值更细腻 | 合法值 **0..10**（0 = 永远振荡，10 = 几乎立刻停） | `[VDC jiggle]` |
| `pitch_stiffness` | 局部 pitch 轴刚度 | 同上 1..1000 | `[VDC jiggle]` |
| `pitch_damping` | 局部 pitch 轴阻尼 | 同上 0..10 | `[VDC jiggle]` |
| `along_stiffness` | 沿弹簧轴（长度方向）的刚度，**仅当写了 `allow_length_flex` 才生效** | 同 stiffness | `[VDC jiggle]` |
| `along_damping` | 沿长度方向阻尼，同上前提 | 同上 | `[VDC jiggle]` |
| `allow_length_flex` | 无参数；允许长度也弹性变化（默认长度是刚性的） | 标志位 | `[VDC jiggle]` |
| `length` | 模拟质量点离基部的距离，**单位英寸**；应等于骨骼长度，写大于骨长会得到奇怪且不可预测的运动 | 英寸（float） | `[VDC jiggle]` |
| `tip_mass` | 模拟质量受**重力**影响的程度：**0 关闭重力影响，1000 是重质量** | 0..1000（wiki 只给语义与两端值） | `[VDC jiggle]` |

#### is_rigid 专有

| 参数 | 含义 | 范围/单位 | 出处 |
|---|---|---|---|
| `length` | 同上（英寸，应匹配骨长） | 英寸 | `[VDC jiggle]` |
| `tip_mass` | 同上（0 = 无重力影响，1000 = 重） | 0..1000 | `[VDC jiggle]` |

#### has_base_spring 专有

| 参数 | 含义 | 范围/单位 | 出处 |
|---|---|---|---|
| `stiffness` | 基部弹簧刚度 | 1..1000（同 Springs 节） | `[VDC jiggle]` |
| `damping` | 基部弹簧阻尼 | 0..10（同 Springs 节） | `[VDC jiggle]` |
| `left_constraint` | 沿**局部 X 轴**允许的最大位移 | 距离（**不是角度**） | `[VDC jiggle]` + `[mdlc src/compile.rs:7393-7394]` |
| `up_constraint` | 沿**局部 Y 轴**允许的最大位移 | 距离 | 同上 |
| `forward_constraint` | 沿**局部 Z 轴**允许的最大位移 | 距离 | 同上 |
| `left_friction` / `up_friction` / `forward_friction` | 顶到对应约束平面时的摩擦力 | 「不应超过 20 单位」（wiki 对 yaw/pitch friction 的措辞） | `[VDC jiggle]` |
| `base_mass` | 基部弹簧受重力影响的程度 | 语义同 tip_mass（wiki 未给上限） | `[VDC jiggle]` |

#### is_boing 专有（**仅 v48 分支 / GMod**，v49 的 `mstudiojigglebone_t` 没有这 5 个槽）

| 参数 | 含义 | 范围/单位 | 出处 |
|---|---|---|---|
| `impact_speed` | 触发 boing 的速度阈值（示例 20）；wiki 注：在部分游戏里「任何移动都会触发」 | float | `[VDC jiggle]` |
| `impact_angle` | 对运动方向的敏感度，「只有与指定角度对齐的运动才触发」；**逻辑范围 0..180（度）** | 度进、**cos 出**（落盘是余弦值） | `[VDC jiggle]` + `[mdlc src/compile.rs:7549-7553]` |
| `damping_rate` | boing 衰减速度；0.0 = 永不停止（无限增长/回弹），1.0 = 很快消失，≥3.0 = 几乎立刻归位 | float | `[VDC jiggle]` |
| `frequency` | 频率；0.0 = 完全不动作画，40.0 = 极快 | float | `[VDC jiggle]` |
| `amplitude` | 缩放/拉伸幅度 | float | `[VDC jiggle]` |

`is_boing` 缺省值（mdlc 实测官方写盘值，**不是角度**）：`impact_speed 100`、`impact_angle 0.7071`（= 45° 的 cos，官方字面量，不是 `cos(45°)=0.70710677`）、`damping_rate 0.25`、`frequency 30`、`amplitude 0.35`。`[mdlc src/model.rs:4904-4923]`

### 1.5 ★约束的角度 / 弧度 / 距离约定

- `angle_constraint`：最大角偏转，形成锥形边界。`[VDC jiggle]`
- `yaw_constraint <min> <max>` / `pitch_constraint <min> <max>`：沿局部 yaw / pitch 轴的**最小与最大角偏转**，「**角值不应超过 ±90 度**」；≥ ±90° 会导致 jiggle bone 翻转，绕不过去时应旋转骨骼改用另一个轴。`[VDC jiggle]`
- **单位链条（决定性结论）**：只有 `yaw_constraint` / `pitch_constraint` / `angle_constraint` 是**角度** —— QC 里写**度**，写盘时**转弧度**，公式是 `d * π / 180`，**只转一次**（实测 `angle_constraint 60` → `1.0471976` = π/3）。`[mdlc src/compile.rs:7388-7391]`
- **`left_constraint` / `up_constraint` / `forward_constraint` 不是角度、不转弧度**（实测 `jig38`：QC 的 `-0.5 0.5` → 盘上 `baseMinLeft = -0.5`）。`[mdlc src/compile.rs:7393-7394]`
- `is_boing` 的 `impact_angle` 是唯一「度进 → cos 出」的键。`[mdlc src/compile.rs:7549-7553]`
- 其余角度类 QC 键的同一约定（供对照）：`$definebone` 的旋转、`$attachment` 的 rotation、`$maxeyedeflection` 都是**度进、落盘弧度或 cos**。`[mdlc README]` + `[mdlc src/compile.rs:6185-6238]`

### 1.6 官方钳位（**与 wiki 的措辞有出入，两条都记下**）

- 反编译 `FUN_004542d0`（被 `is_flexible` 的 6 个 stiffness/damping 与 `has_base_spring` 的 `stiffness`/`damping` 调用）：`if v <= 1000 { if 0 <= v { v } else { 0 } } else { 1000 }` ⟹ **实际钳到 [0, 1000]**。实测：`yaw_stiffness 5000 → 1000`、`yaw_damping -5 → 0`、`pitch_stiffness 1000.5 → 1000`、`stiffness 5000 → 1000`、`damping -3 → 0`。`[mdlc src/model.rs:4890-4902]`
- **`yaw_friction` / `yaw_bounce` / `pitch_friction` / `pitch_bounce` 不钳位**（实测 5000 / 6000 / −7 原值透传）。`[mdlc src/model.rs:4900-4901]`
- ⟹ 结论：wiki 的「damping 合法 0..10」是**推荐/语义**范围；编译器**不会**把 11 拒绝或截到 10，而是一直接受到 1000。`[VDC jiggle]` + `[mdlc src/model.rs:4890-4902]`

### 1.7 缺省值与二进制布局

- 缺省（mdlc 实测、官方写盘值）：`length = 10`、六个 `*_stiffness = 100`、`*_damping = 0`、`tip_mass = 0`、`base_mass = 0`、base 三轴 `max = +100`、`min = −100`。`[mdlc src/model.rs:4880-4902]` + `[mdlc src/compile.rs:7449-7462]`
- `flags` 位定义（受控实验钉死）：`0x01 IS_FLEXIBLE`（`is_flexible` 块出现）、`0x02 IS_RIGID`（`is_rigid` 出现）、`0x04 HAS_YAW_CONSTRAINT`、`0x08 HAS_PITCH_CONSTRAINT`、`0x10 HAS_ANGLE_CONSTRAINT`、`0x20 HAS_LENGTH_CONSTRAINT`（**块出现即置位**，`allow_length_flex` 清掉）、`0x40 HAS_BASE_SPRING`、`0x80 IS_BOING`（**仅 v48 有这一位**）。`[mdlc src/model.rs:4855-4877]` + `[mdlc src/compile.rs:7372-7386]`
- `mstudiojigglebone_t` 大小：**v49 = 120 字节**；**v48 = 140 字节**（多 5 个 `boing*` float，+20 B）。这是 v48/v49 **唯一改变字节布局**的结构。`[mdlc src/format.rs:20-37, 104-124]`
- 目标骨骼找不到时：官方**只打一行** `jigglebone "%s" unused` 并**丢弃该条**（不报错、不影响其它 jigglebone）。`[mdlc src/compile.rs:7413-7446]`
- 目标 v49 时写 `is_boing` 会**报错**（v48 的 `studio.h` 有该位与 5 个字段，v49 的 `hl2sdk-l4d2`/`doi`/`csgo` grep `boing` 零命中）。`[mdlc src/model.rs:4871-4877]` + `[mdlc README]`
- 宏技巧：`$definemacro` 可复用一套 jigglebone 参数；被 `$renamebone` 改名的骨骼要用**新名字**列在宏里。`[VDC jiggle]`

---

## 2. VMT 表达「金属 / 光泽 / 透明 / 自发光」的键

### 2.1 光泽 / 金属（Phong + envmap 两条路径）

| 键 | 类型 | 是什么 | 取值范围 | 出处 |
|---|---|---|---|---|
| `$phong` | bool | 开启 Phong（漫反射低强度 + 高光顶点强度），由遮罩贴图逐像素控制强度；`VertexLitGeneric` 自 **Source 2006** 起可用 | 0/1 | `[VDC phong]` |
| `$phongexponent` | int | **全局** exponent 值；「覆盖存在的 exponent 遮罩」。物理含义 = 高光**锐利度** | 整数；无官方上下限（见 2.2 的方向说明） | `[VDC phong]` |
| `$phongexponenttexture` | texture | 逐像素 exponent 贴图。**R = exponent 遮罩（高光大小，0–255，0 = 大面积高光、255 = 小面积高光）；G = albedo tint 遮罩（仅当 `$phongalbedotint 1`，0–255）；B = 无用；A = `$rimlight` 遮罩（仅当 `$rimmask 1`）** | 纹理 | `[VDC phong]` |
| `$phongexponentfactor` | float | 把 exponent texture 的值乘上该系数（**仅 CS:GO 分支**）；默认 `0.0`，wiki 说更合理的默认是 `149.0` | float | `[VDC phong]` |
| `$phongboost` | float | Phong 亮度系数；**越大高光越强**，用于金属、玻璃；「boost = 0 视觉上等同于没有 phong」 | float，无上限 | `[VDC phong]` |
| `$phongtint` | RGB matrix | phong 反射的**颜色 tint**；**会覆盖 `$phongalbedotint`**；同时 tint `$rimlight` | `[r g b]` | `[VDC phong]` |
| `$phongfresnelranges` | matrix | Fresnel 三区间，默认 `[0 0.5 1]`；**同时影响 `$envmap`**（两者都开时） | `[min mid max]` | `[VDC phong]` |
| `$phongalbedotint` | bool | 用 `$basetexture` 的颜色 tint 反射；tint 量由 `$phongexponenttexture` 的 **G 通道**决定；**必须存在 exponent texture 才有效**；不能与 `$phongtint` 同用 | 0/1 | `[VDC phong]` |
| `$envmap` | texture | 环境立方图；常用 `env_cubemap`（地图编译时换成最近的 cubemap），也可给静态 cubemap 路径 | 路径 / `env_cubemap` | `[VDC envmap]` |
| `$envmaptint` | RGB matrix | 反射三通道强度，默认 `[1 1 1]`；⚠️ **在 VertexLitGeneric 上做了 gamma→linear 转换**：输入变成 `X^2.2`，写 0.25 实际约 5% 而不是 25% | 正值；可写成单个 float | `[VDC envmap]` |
| `$envmapmask` | texture | 高光遮罩：**白 = 全反射、黑 = 全哑光**；彩色遮罩会被当作逐像素 `$envmaptint` | 灰度纹理 | `[VDC envmapmask]` |
| `$normalmapalphaenvmapmask` | bool | 用 `$bumpmap` / `$normalmap` 的 **alpha 通道**当 specular 遮罩；**开了 phong 时它会成为 phong 的遮罩来源** | 0/1 | `[VDC envmapmask]` + `[VDC phong]` |
| `$basealphaenvmapmask` | bool | 用 `$basetexture` 的 alpha 当 specular 遮罩（注意 base alpha 语义在部分分支是**反相**的：透明处反射、不透明处哑光） | 0/1 | `[VDC envmapmask]` |
| `$envmapsphere` | bool | 把 envmap 当**球面图（spheremap）**用 —— 官方原文就是「using matcap shading like chrome textures」。（旧分支才有；DX9 下退化成 6 面相同的 cubemap） | 0/1 | `[VDC envmap]` |
| `$envmapcontrast` / `$envmapsaturation` | normal / RGB | 反射对比度 / 饱和度；**开 phong 时都不起作用** | 0..1 语义 | `[VDC envmap]` |
| `$envmapfresnel` / `$envmapfresnelminmaxexp` | float / vector | VertexLitGeneric 的 Fresnel 反射；默认用 `$phongfresnelranges`；`minmaxexp` 默认 `[0 1 2]` | float / vector | `[VDC envmap]` |
| `$envmaplightscale` | float | 用 lightmap（模型上用顶点光照）tint 反射，做粗糙的反射遮蔽；值 1 时无光的表面完全没有反射 | float（>1 会反转效果） | `[VDC envmap]` |

**组合规则（硬约束，必须知道）**

1. Phong 想显示出来，示例里**所有** phong 参数必须齐全（`$phong` + `$bumpmap` + `$phongexponent`（或 `$phongexponenttexture`）+ `$phongboost` + `$phongfresnelranges`），否则「Phong shading won't show up at all」（Insurgency 例外）。`[VDC phong]`
2. **`$envmapmask` 与 `$phong` 完全不兼容**：用了 `$envmapmask` 时 envmap 的遮罩默认改用 `$basetexture` 的 alpha；显式用 `$normalmapalphaenvmapmask` 则改 `$bumpmap` alpha 同时当 **phong** 遮罩。`[VDC envmapmask]` + `[VDC phong]`
3. `$basemapalphaphongmask` / `$basemapluminancephongmask` 会**覆盖** phong 遮罩来源；`$invertphongmask` 在部分分支反相的是 envmap 遮罩而不是 phong 遮罩（已知 bug）。`[VDC phong]`
4. 自 Source 2007 起，开 `$phong` 会**自动开启 `$halflambert`**（除 Black Mesa 外），需要 `$phongdisablehalflambert` 关闭；CS:GO 分支上该参数等于强制开。`[VDC phong]`
5. `$envmaplightscale` **不能与 `$envmapmask` 同用**（要改用 `$normalmapalphaenvmapmask`）。（GMod 实测）`[VDC envmap]`

### 2.2 ★`$phongexponent` 与「光泽度 / 粗糙度」的关系方向

**方向：exponent 越高 → 高光越小越紧 → 表面越光滑（gloss 高 / roughness 低）。** 反比于粗糙度。

出处是逐通道语义的原文：exponent 遮罩的 R 通道「**size of highlight, 0-255, where a value of 0 is a large highlight, 255 small**」`[VDC phong]`。即遮罩值越大 = 高光越**小** = 越像镜面；指数越小 = 高光铺得越大 = 越像粗糙漫反射。

配套的几个有出处的锚点（可用于估区间）：

- `$phongexponentfactor` 的「合理默认」被官方写成 **149.0**（而默认值是 0.0）⟹ 成品材质里 exponent 量级在**数十到一两百**。`[VDC phong]`
- CS:GO 官方 VMT 示例（`tile/hr_t/inferno/tile_a.vmt`）：`$phongexponent 80.0` + `$phongboost` 缺省 + `$phongamount [1 1 1 1]` ⟹ 瓷砖类半光滑表面 ≈ 80。`[VDC phong]`
- Phong 语法示例用 `$phongexponent 5`（配合 `$phongboost 1.0`）。`[VDC phong]`
- L4D2 的「envmap 遮罩 + phong + detail + emissive 全开」现成材质里用 `$phongexponent 1` + `$phongboost 10` ⟹ **低指数靠大 boost 补强度**是 L4D2 的常见写法。`[VDC selfillum]`
- PBR-2-Source 的做法：转到标准 shader 时「用 **Phong + `$envmap` 反射的组合**去近似 roughness 与 metalness」，导出时生成 VMT —— 它承认这是一条近似路径，**但页面没有给出任何换算公式**。`[PBR2S]`

**注意（不编造）**：本次抓到的来源里**没有**任何「`exponent = f(roughness)` 的数值公式」。`Phong_materials` 页（最可能给出直观解释的那页）抓取失败。所以公式层面的映射必须当作工程推断，见第 4 节。

### 2.3 自发光（emissive）

| 键 | 类型 | 是什么 | 范围/默认 | 出处 |
|---|---|---|---|---|
| `$selfillum` | bool | 表面自发光、独立于环境光照；**默认用 `$basetexture` 的 alpha 通道当遮罩**；遮罩值 < 1.0 时变成「最小亮度」效果 | 0/1 | `[VDC selfillum]` |
| `$selfillumtint` | RGB matrix | 自发光颜色 tint | 默认 `[1 1 1]` | `[VDC selfillum]` |
| `$selfillummaskscale` | normal | 自发光强度缩放 | 默认 1.0 | `[VDC selfillum]` |
| `$selfillummask` | texture | 专用遮罩贴图（Source 2007 分支**必须**用它）；VertexLitGeneric 上带 `$bumpmap` 时不生效（Alien Swarm 例外） | 纹理 | `[VDC selfillum]` |
| `$emissiveblend*` | 多个 | 另一条自发光路径（`$EmissiveBlendEnabled` / `...Strength` / `...BaseTexture` / `...Tint` / `...FlowTexture` / `...ScrollVector`）；**兼容透明**，可做流动效果 | — | `[VDC selfillum]` |
| `$detail` + `$detailblendmode 5/6` | texture + int | 又一条自发光替代路径（见 2.5） | — | `[VDC selfillum]` + `[VDC detail]` |

**冲突（对转换器极重要）**

- `$selfillum` **不能与 `$translucent` 或 `$alphatest` 同用于模型**；替代方案是 `UnlitGeneric` 或用 `$detail`（blendmode 5/6）。`[VDC selfillum]`
- `$selfillum` **与 `$phong` 互斥**（按代理关系互斥）：selfillum 只配 `$normalmap`，phong 只配 `$bumpmap`；绕过办法是 `$selfillummask` + `$basemapalphaphongmask`。`[VDC selfillum]`
- L4D2 现实约束：近战武器用了 `$phong` 就不能用 `$selfillum`，而 `$detail` 又被血迹层占掉 ⟹ 只能用 `$emissiveblend`。`[VDC selfillum]`
- `$selfillum_envmapmask_alpha` **替代** `$selfillum`（两者同用会报错、材质不渲染），且它的遮罩会被**乘 8**。`[VDC selfillum]` + `[VDC envmapmask]`

### 2.4 透明

| 键 | 类型 | 是什么 | 范围/默认 | 出处 |
|---|---|---|---|---|
| `$alphatest` | bool | 二进制不透明度：白=不透明、黑=全透明，中间值被**四舍五入到 0/1**（除非开 `$allowalphatocoverage`）；比 `$translucent` 快得多、排序永远正确 | 0/1 | `[VDC alphatest]` |
| `$alphatestreference` | float | 阈值：`.3` = 更厚的形状，`.7` = 更薄的形状。⚠️ **不默认 0.5** —— `LightmappedGeneric`/`UnlitGeneric`/**`VertexLitGeneric` 默认 0.7**，务必显式写 | 0..1 | `[VDC alphatest]` + `[VDC translucent]` |
| `$allowalphatocoverage` | bool | 把 alpha test 变成多级半透明（需要 MSAA）；开启后 `$alphatestreference` 不再影响外观（仍影响投影像素阴影遮罩） | 0/1 | `[VDC alphatest]` |
| `$translucent` | bool | 用 `$basetexture` 的 alpha 做**逐像素半透明**；**会完全关掉投影贴图产生的阴影**；与 `$selfillum`、`$alphatest`/`$alphatestreference` 不兼容 | 0/1 | `[VDC translucent]` |
| `$alpha` | bool/float | 整张材质统一缩放不透明度（cheap）；部分分支不支持（wiki 标了确认存疑） | — | `[VDC vlg]` |
| `$additive` | bool | 类似 `$translucent` 的完整 alpha 遮罩，但用**颜色相加**而非相乘 ⟹ 永远更亮 | 0/1 | `[VDC vlg]` |
| `$nocull` | bool | **关闭背面剔除**（双面）。薄片、叶片、布料必需；配 `$flashlightnolambert` 可避免手电从背面照不亮 | 0/1 | `[VDC vlg]` |

### 2.5 `$detail`（细节层 —— 也是 L4D2 的「自发光」与「破 sv_pure」逃生口）

| 键 | 是什么 | 范围/默认 | 出处 |
|---|---|---|---|
| `$detail <texture>` | 近距离叠加的高分辨率细节贴图，按 `$detailblendmode` 变亮/变暗 | 纹理 | `[VDC detail]` |
| `$detailscale` | 平铺次数 | **默认 4**；128px 细节贴图常用 7~8；可写成 `[4 8]` 分轴 | `[VDC detail]` |
| `$detailblendfactor` | 细节层影响强度（多数模式类似 `$alpha`） | 0 = 无效果，1 = 全效 | `[VDC detail]` |
| `$detailblendmode` | 12 种混合模式 0..11：0 DecalModulate、1 Additive、2 Translucent Detail、3 Blend Factor Fade、4 Translucent Base、5 **Unlit Additive（自发光）**、6 Unlit Additive Threshold Fade、7 Two-Pattern DecalModulate、8 Multiply（≈AO）、9 Base Mask via Detail Alpha、10 Self-Shadowed Bumpmap、11 SSBump Albedo | int 0..11 | `[VDC detail]` |
| `$detailtint` | 细节层颜色 tint（VertexLitGeneric 上带 `$bumpmap`/`$phong` 时不工作） | `[r g b]` | `[VDC detail]` |

VertexLitGeneric 的限制（官方表）：**10、11 不支持**；0–4 恒可用（有无 `$bumpmap` 都行）；**5、6 只在有 `$phong` 或没有 `$bumpmap` 时可用**；7–9 只在没有 `$bumpmap` 时可用。`[VDC detail]`

L4D2 的 `$detail` 自发光配方（原文示例）：黑色底图 + 发光区域上色 + `$detailscale 1` + `$detailblendmode 5`（或 6；两者都没有时用 1，但会受光照影响）+ 可选 `$detailblendfactor` 调暗。`[VDC selfillum]`

### 2.6 shader 本体与其它相关

- 模型最常用 shader 是 `VertexLitGeneric`；**不要用在世界几何上**（会有光照 bug）。`[VDC vlg]`
- 支持的其它块：透明度（`$alpha`/`$nocull`/`$additive`）、光照（`$selfillum`、`$lightwarptexture`）、效果（`$flesh`、`$treeSway`）、cloak（TF2 Spy：`$cloakpassenabled` / `$cloakfactor` / `$cloakcolortint` / `$refractamount`）。`[VDC vlg]`
- `$notint` / `$allowdiffusemodulation`：阻止 `$color2` / `rendercolor` 给材质染色。`[VDC vlg]`

---

## 3. Source 硬限制

### 3.1 骨骼数

| 目标 | studio 版本 | 骨骼上限 | 出处 |
|---|---|---|---|
| **Left 4 Dead 2** | **49** | **127** 根（官方判据 `if (0x7f < numbones)`；报错文案印的 `max 128` 是官方笔误，写 128 就会被拒） | `[mdlc src/target.rs:11-35]` |
| Garry's Mod | 48 | **255**（Facepunch 把常量改到 `0xff`，纯 SMD 输入同样能到 255） | `[mdlc src/target.rs:11-35]` |
| Source SDK 2013 / TF2 / CS:S | 48 | 127（推定：源码 `MAXSTUDIOBONES == 128`，未逐二进制核对） | `[mdlc src/target.rs:15]` |
| CS:GO | 49 | 255（推定：本机没有该编译器） | `[mdlc src/target.rs:16]` |
| **格式硬上限** | — | **256**：三处骨骼下标都是 **1 字节**（顶点 `mstudioboneweight_t.bone[]`、VTX `Vertex_t.boneID[]`、动画 `mstudioanim_t.bone`），超过 255 的骨骼无法被引用；超限**直接报错，不静默截断** | `[mdlc src/target.rs:44-53]` + `[mdlc README]` |

wiki 侧的同一件事（口径差异记录）：`MAXSTUDIOBONES` = Source 2013 **128**、**L4D2 128**、GMod 256、SFM 256、CS:GO 256，且这个数**包含隐式根骨骼**，所以用户骨骼实际是 `MAXSTUDIOBONES − 1`。`[VDC skeleton]`
⟹ 与 mdlc 的 127 一致（128 含隐式根 − 1 = 127 可自定义）。**注意 mdlc 的 127 是「优化之后的骨骼表条数」**（未被引用的骨骼会被丢掉，`$attachment` 引用的算被引用），SMD 里的节点数 ≠ 最终骨骼数。`[mdlc README]`

### 3.2 每顶点权重数

- **每顶点最多 3 组权重**：`mstudioboneweight_t` 是定长 `bone[3]` + `weight[3]` + `boneCount`，常量名 `MAX_NUM_BONES_PER_VERT`；mdlc 里对应 `MAX_BONES_PER_VERT = 3`。`[mdlc src/vvd.rs:18-26, 368-372]` + `[mdlc src/model.rs:5899]`
- VVD 顶点 `mstudiovertex_t` **stride 48 字节**：`+0x00 float[3] weight`、`+0x0C u8[3] bone`（**全局**骨骼下标）、`+0x0F u8 boneCount`（有效数 1..3）、`+0x10 float[3] position`、`+0x1C float[3] normal`、`+0x28 float[2] texCoord`；切线 `SourceVector4D` 16 字节。`[mdlc src/vvd.rs:18-29]`
- 转换顺序（官方等价）：按权重**降序排序 → 取前 3 → 丢弃 weight ≤ 0 的项 → 若 `|Σw − 1| > 1e-6` 则归一化**；全为 0 的顶点直接报错。`[mdlc src/compile.rs:1615-1699]`
- `$staticprop` 会把骨骼表塌缩成 1 根，所有顶点写成 `bone=[0,0,0] bone_count=1 weight=[1.0,0,0]`（官方产物实测正是如此）。`[mdlc src/compile.rs:1701-1719]`

### 3.3 权重裁剪阈值

- **没有找到任何权威数值阈值。** mdlc 源码里唯一的「裁剪」就是上面那条：前 3 + `weight ≤ 0` 丢弃 + 偏差超过 `1e-6` 才归一化（归一化阈值是**浮点残差判据**，不是「小权重裁剪」）。`[mdlc src/compile.rs:1615-1699]`
- VDC 的 `Skeleton` 页只讲「顶点权重是 float，默认 1.0」，没有阈值。`[VDC skeleton]`
- ⟹ 结论：**「丢弃小于 X 的权重」这条规则在本次来源里没有出处**；工程上按「前 3 + 归一化」实现即可。若一定要设阈值，那属于转换器自己的策略，不是 Source 的规格。

### 3.4 材质数

- **格式上限 32768 条**：material/skin 表用的是**有符号 16 位**引用（`mstudiotextureref_t` 下标 0..=32767），所以表最多 32768 条；且 `numskinref` 恒等于 `numtextures`（语料 3301/3301 验证）。`[mdlc src/mdl_writer.rs:130-134, 1926-1928]`
- **studiomdl 的「人为」上限是 32 个材质**：这不是格式约束，mdlc **明确不复刻**。`[mdlc README]`（"格式上限：只保留格式能表示的那些" 一节）

### 3.5 顶点数

- **每 mesh（= 一个材质）最多 65536 顶点**：VTX 的 `origMeshVertID` 是 `uint16`，下标 `0..=65535`；判据是 `n > 65536`（**不是 `>=`**，恰好 65536 合法）。官方 `studiomdl` 遇到超限**直接拒绝**（`ERROR: too many indices in source`）。`[mdlc src/mdl_writer.rs:93-99, 7109-7125]`
- **每 model 顶点上限 44,739,242**（由偏移/位宽算术推出）；官方 studiomdl 的 `MAXSTUDIOVERTS = 65536` 是**引擎运行时顶点缓存**的人为上限，mdlc 不复刻。`[mdlc src/mdl_writer.rs:115, 3206-3215]` + `[mdlc README]`
- **三角形数无上限**；**LOD 最多 8 档**（含 LOD 0，引擎 `MAX_NUM_LODS`）。`[mdlc README]`
- 顶点超限时 mdlc 的默认行为是**按三角形顺序把超限 mesh 切成多个 mesh（每块 ≤ 65536）**，仍放在同一个 model、共用同一材质（不新增 bodypart，否则 `$bodygroup` 按下标选择会错位）。`[mdlc README]`

### 3.6 版本与其它二进制事实

- studio 版本：L4D2 / CS:GO 一系 = **49**；Source 2013 一系（GMod / TF2 / CS:S / HL2:DM / Portal / BMS）= **48**。两者磁盘差异**只有四处**（其余尺寸相同）：`mstudiojigglebone_t` **120 vs 140**；`mstudiobone_t` 的 surfaceprop 字段（216 = 216）；`mstudioanimdesc_t`（100 = 100）；`studiohdr_t`（408 = 408）。`.vvd` / `.vtx` **完全不受版本影响**。`[mdlc src/format.rs:20-37]`
- 不支持 v44（缺 `studiohdr2_t`、`mstudiolinearbone_t` 等一整套结构）。`[mdlc src/format.rs:49-54]`
- 四件套（`.mdl` / `.vvd` / `.dx90.vtx` / `.phy`）共享同一个 `checksum` 配对令牌；它**不是内容哈希**，引擎与 Crowbar 只比较、从不计算。`[mdlc src/vvd.rs:40-43]` + `[mdlc README]`
- 骨骼顺序：根骨骼必须排最前，`parent` 只能指向更靠前的骨骼。`[mdlc README]`
- `$weightlist` 族的实测上限：每表 **128** 条、共 **127** 张表（官方实测，不是头文件里写的 16/32）。`[mdlc src/model.rs:426-442, 6488-6523]`

---

## 4. 对 HD2 转换器的直接含义

### 4.1 「靠 matcap 文件名关键词判金属」可以换成哪些确定性判据

现在的做法（文件名里出现 matcap / chrome / metal 之类就判金属）是**启发式**。抓到的权威资料给出了**确定性**判据，按可靠性排序：

1. **`$envmapsphere` + `$envmap`** —— 这是「matcap」在 Source 里的**正规表达**：官方原文就是「should be a spheremap (**using matcap shading like chrome textures**)」。所以「matcap」这个词在 VMT 里有对应的键，而不是只能靠文件名猜。`[VDC envmap]`
   ⟹ 规则：`$envmapsphere 1` 且 `$envmap` 指向某个 cubemap/球面图 ⟹ 该材质是**反射/铬**语义，可确定映射到金属或高光泽。
2. **`$phong 1` + `$bumpmap` + (`$phongexponent` | `$phongexponenttexture`) + `$phongboost`**（四件套齐全）⟹ 有真实高光通道。这是最强的「金属/光滑」结构性判据，同时也是**判断该材质是否真的有高光**的前置条件（缺一个就不显示）。`[VDC phong]`
3. **`$normalmapalphaenvmapmask 1`** ⟹ 有逐像素的 specular/phong 遮罩，且遮罩来源是法线贴图的 alpha。金属/非金属常常就编码在这张 alpha 里 —— **这是可以真正读出来的数据**，比文件名强得多。`[VDC envmapmask]` + `[VDC phong]`
4. **`$envmapmask`**（存在且与 phong 不同用）⟹ 有独立的反射遮罩贴图；`白 = 全反射`。可直接按遮罩覆盖率算 metallicity 权重。`[VDC envmapmask]`
5. **`$basealphaenvmapmask 1`** ⟹ base 贴图 alpha 被当反射遮罩（注意语义可能是**反相**的）。`[VDC envmapmask]`
6. **`$envmaptint` / `$phongtint` / `$phongalbedotint` 的具体数值** ⟹ 反射颜色与强度。⚠️ `$envmaptint` 在 VertexLitGeneric 上做了 `x^2.2` 转换，反向映射时必须**先开方**再当线性反射率用。`[VDC envmap]`
7. **`$phongexponent` / `$phongexponenttexture` 的 R 通道** ⟹ 逐像素光泽度（见 4.2），可直接当 gloss map 用。`[VDC phong]`

**判定优先级建议**（同为有出处的事实拼出的规则）：`$envmapsphere`（明说 matcap） > `$phongexponenttexture` R 通道（逐像素、可算量） > `$phong` 四件套（结构性） > `$envmapmask`/`$normalmapalphaenvmapmask`（遮罩存在性） > 文件名关键词（兜底）。

**明确不能靠的**：`$envmapcontrast` / `$envmapsaturation`（开 phong 时**不起作用**）、`$phongboost`（只调亮度，0 等价于没 phong，不能当金属证据）。`[VDC envmap]` + `[VDC phong]`

**另外两条转换器必须处理的硬冲突**（否则转换出来的 VMT 会「看起来对但不显示」或直接不渲染）：

- `$envmapmask` 与 `$phong` **不能共存** ⟹ 要把 HD2 的「金属 + 高光」拆成 `$normalmapalphaenvmapmask` 路线或纯 envmap 路线之一。`[VDC envmapmask]`
- `$selfillum` 与 `$phong`（按 bumpmap 代理）、与 `$translucent`/`$alphatest` 都互斥 ⟹ 自发光必须走 `$emissiveblend` 或 `$detail blendmode 5/6`。`[VDC selfillum]`

### 4.2 `$phongexponent → roughness` 的换算方向与大致区间

**方向（有出处，确定）**：**`exponent` 越大 ⟹ 高光越小 ⟹ 越光滑 ⟹ roughness 越小**。官方对 exponent 遮罩的定义是「size of highlight, 0 = large highlight, 255 = small」`[VDC phong]`。所以是**反比/倒数关系**，不是正比 —— 任何「exponent × k = roughness」的线性写法都是错的。

**大致区间（有出处的锚点）**：

| 观察点 | exponent | 说明 | 出处 |
|---|---|---|---|
| Phong 语法示例 | 5 | 基础示例值，配合 `$phongboost 1.0` | `[VDC phong]`
| CS:GO 瓷砖官方 VMT | 80 | 半光滑工业表面 | `[VDC phong]`
| `$phongexponentfactor` 「合理默认」 | 149 | 官方注释说默认 0.0 不合理，合理值 149 | `[VDC phong]`
| L4D2 全效果现成材质 | 1 | 但 `$phongboost 10`（低指数靠大 boost 补） | `[VDC selfillum]`
| exponent **遮罩**通道 | 0..255 | 逐像素，0 = 大面积高光、255 = 极小高光 | `[VDC phong]`

⟹ 工程上可用的映射区间：**exponent ∈ [1, 255] 覆盖 roughness 从「很粗糙」到「近镜面」**；实际成品集中在 **5..150**。

**换算公式：本次来源没有给出，属于推断（明确标注，非出处）**。若需要落一个公式，最保守的做法是用 Blinn-Phong 的标准对应关系（业界通用、**不在本次抓取来源中**）：

- `exponent ≈ 2 / roughness⁴ − 2`（即 `roughness ≈ (2 / (exponent + 2))^0.25`）
  对应：`exp 1 → r≈0.84`、`exp 5 → r≈0.66`、`exp 20 → r≈0.48`、`exp 80 → r≈0.36`、`exp 149 → r≈0.31`、`exp 255 → r≈0.28`。
- 这条曲线的**上界收敛得太慢**（exp 255 仍有 r≈0.28），与 Source 实际观感（150 以上已经很镜面）不完全吻合；因此更实用的做法是**分段线性**，把 `exp 5 → r 0.7`、`exp 40 → r 0.45`、`exp 150 → r 0.2`、`exp ≥200 → r 0.05` 钉成查表，再用 `exp:` 遮罩的 R 通道（0..255）逐像素插值。

⚠️ 反向（roughness → VMT）时还必须记住：`$phongexponent` 是**全局**一个数，逐像素只能走 `$phongexponenttexture` 的 **R 通道**；而 `$phongexponent` 一旦写了就会**覆盖** exponent 遮罩。`[VDC phong]`

### 4.3 顺手记下的、对本项目最可能的坑

1. 给 L4D2 编模型必须 **studio v49 + 骨骼 ≤ 127**；写成 128 会被官方编译器拒（报错文案里的 `max 128` 是笔误）。`[mdlc src/target.rs:24-35]`
2. **`mstudiojigglebone_t` 在 v49 是 120 B、v48 是 140 B**；把 `is_boing` 写进 v49 模型会（也应当）报错。`[mdlc src/format.rs:104-124]` + `[mdlc src/model.rs:4871-4877]`
3. HD2 网格常见的「一根顶点 4 个以上影响骨」必须先降到 **3**（按权重降序），再归一化 —— 且**没有官方权重裁剪阈值可参照**。`[mdlc src/compile.rs:1615-1699]`
4. 一个材质（mesh）顶点数 > **65536** 会被官方 studiomdl 直接拒绝，需要拆分（mdlc 的做法是切 mesh 而不是切 bodypart）。`[mdlc src/mdl_writer.rs:93-99]` + `[mdlc README]`
5. 材质表实际可到 32768 条，但 studiomdl 的人为上限是 32 —— 转换器若生成大量材质，**要按 32 这一档做保守设计**。`[mdlc README]`
