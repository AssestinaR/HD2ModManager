# L4D2 / Source `$jigglebone` 调研报告

> 目的：搞清楚 L4D2 角色模组里的"物理头发"在 **原版 Source 引擎**里到底是怎么算的，以便判断我们自己那个 HD2 端 Lua 弹簧求解器是"实现错了"还是"超越原版"，并给出可执行的对策。
>
> 调研时间：2026-11-03。取证方式：官方文档 + Valve 官方源码 + **本机 L4D2 安装包二进制取证**（3333 个官方 `.mdl` + 114 个订阅 VPK 里的 6008 个 `.mdl`）+ 第三方工具/作者资料。
>
> **证据分级**（全文用标记区分）：
> - `[官方文档]` Valve Developer Community wiki 明文
> - `[官方源码]` Valve 放出的 Source SDK 2013 源码
> - `[本机实测]` 我用脚本从本机 `.mdl` / `.dll` 里读出来的真数据（可复现，见 §8）
> - `[社区]` 论坛/指南/第三方工具作者的资料
> - `[推断]` 我的推理，**未经证实**

---

## 0. TL;DR（先看这三条）

1. **原版 jigglebone 完全没有碰撞体**。`CJiggleBones::BuildJiggleTransformations()` 里没有任何射线/球体/胶囊查询，只有"角度锥/平面夹紧 + 杆长约束"这类**解析约束**。所以"缺少碰撞导致的穿模/折痕"不是我们丢功能，**原版也没有**。`[官方源码]`
2. **有"回到目标姿态"的力，而且是唯一的回正力**。弹簧的误差项是 `goalTip - tipPos`，`goalTip` 就是**当帧骨骼动画（父级链条）算出来的目标位**。所以 jigglebone 本质是"**在动画姿态附近做弹簧扰动**"，不是自由物理。重力是**世界空间 −Z 上的一个加速度**，大小就等于 `tip_mass` 数值本身（不是质量）。`[官方源码]`
3. **`length` 不是"到子骨的距离"，而是"虚拟摆杆长度"，而且它决定下垂角**。Valve 自己的模型里 `length` 是实际子骨距离的 **1.2×~34×（中位数 2.4×）**；第三方动漫 mod 一律写死 `length 30`（实际骨距中位数 3.5 → **约 8.5×**）。稳态下垂角 ≈ `asin(tip_mass / (stiffness · length))`。**必须照抄作者的 `length`，不能换成真实骨距**，否则下垂角会成比例放大（例：`length 30 → 3.5`，下垂角从 ~8° 变成撞死在上限）。`[官方源码]+[本机实测]+[推断]`

---

## 1. 参数完整表（**官方真名**，含对你给的名单的勘误）

### 1.1 先勘误：你列的那批参数名里有一半不是 studiomdl 的 QC 键名

Valve 的 QC 解析器和磁盘结构体 `mstudiojigglebone_t` 的真名如下（`[官方源码]` `src/public/studio.h:186-252`，字段顺序与磁盘布局一致）：

```
flags(4B) + 29 个 float = 120 字节（v49）；v48 多 5 个 boing* float = 140 字节
```

| 你的名单 | 真实 QC 键 | 结论 |
|---|---|---|
| `pitch_stiffness` / `pitch_damping` | 同名 | ✅ 存在 |
| `along_stiffness` / `along_damping` | 同名 | ✅ 存在（**仅当 `allow_length_flex` 时生效**） |
| `angle_constraint` | 同名 | ✅ 存在（QC 写**度**，磁盘存**弧度**） |
| `tip_mass` | 同名 | ✅ 存在（语义不是质量，见 §3.3） |
| `is_flexible` / `base_spring` | `is_flexible` / `has_base_spring` | ⚠️ 组名是 `has_base_spring` |
| `pitch_friction` / `yaw_friction` | 同名 | ✅ 存在，但官方文档明确写"**已不再使用**"（源码里 `data->tipVel.Zero()` 代替，注释：*removed friction and velocity clipping against constraint - was causing simulation blowups (MSB 12/9/2010)*） |
| `pitch_rebound` / `yaw_rebound` | **`pitch_bounce` / `yaw_bounce`** | ❌ 名字不对。结构体字段是 `yawBounce` / `pitchBounce`；社区确实在用 `yaw_bounce` / `pitch_bounce`（`[社区]` Feromon32 的裙摆配方），但**wiki 未收录**，且求解器里**根本没读这两个字段**（`[官方源码]`：`yawBounce`/`pitchBounce` 在 jigglebones.cpp 中零引用）→ **写了也没效果** |
| `allow_pitch` / `allow_yaw` | **不存在** | ❌ 无此键。等价物是 `pitch_constraint` / `yaw_constraint`（不写就等于不约束） |
| `pitch_min` / `pitch_max` | **`pitch_constraint <min> <max>`** | ❌ 键名不对（磁盘上才是 `minPitch`/`maxPitch` 两个 float） |
| `along_min` / `along_max` | **不存在** | ❌ 无杆长上下限，只有布尔 `allow_length_flex` 开关 |
| `allow_stretch` | **`allow_length_flex`** | ❌ 键名不对 |
| `base_mass` | 同名 | ✅ 存在 |
| `base_stiffness` / `base_damping` | **`stiffness` / `damping`**（在 `has_base_spring{}` 块内） | ❌ 键名不对，**块内就叫 `stiffness`/`damping`**，容易和多块共存时踩坑 |
| `base_min` / `base_max` | **`left_constraint` / `up_constraint` / `forward_constraint`**（各 `<min> <max>`） | ❌ 键名不对 |

> 这一条很重要：如果你之前的实现是照那批名字写的，那说明参数来源本身是错的（很可能是某次 LLM 补全）。**请以本表 + §2 的磁盘字段顺序为准。**

### 1.2 完整参数表

弹簧/阻尼的**合法范围**来自 `[官方文档]`：stiffness 1..1000，damping 0..10。**注意源码里并没有做范围裁剪**，是纯粹的建议值。`[官方源码]`

#### 通用（`is_flexible` / `is_rigid` 都会有）

| QC 键 | 磁盘字段 | 单位 | 物理含义 | 典型值（实测，见 §4） |
|---|---|---|---|---|
| `length` | `length` | Source 单位（≈英寸） | **虚拟摆杆长度**：质量点距骨骼根部、沿骨骼 **+Z** 的距离。**不是到子骨的距离**（§3.5） | 官方 3–100；第三方 10/20/30（85% 是 30） |
| `tip_mass` | `tipMass` | **加速度**（u/s²） | 直接作为"世界 −Z 向加速度"加到 tip 上。**0 = 完全不受重力**（不是"轻"，是**没有**） | 官方 0/100/600/1000；第三方 0/200/500（93% 是 200） |

#### `is_flexible`（弹性杆，**唯一被真正大量使用的模式**）

| QC 键 | 磁盘字段 | 单位 | 物理含义 | 典型值 |
|---|---|---|---|---|
| `yaw_stiffness` | `yawStiffness` | 1/s² | 局部 **X（left）** 方向的位置弹簧刚度 | 官方 25–250；第三方 50（96%） |
| `yaw_damping` | `yawDamping` | 1/s | 同上方向的阻尼 | 官方 2–12；第三方 7（93%） |
| `pitch_stiffness` | `pitchStiffness` | 1/s² | 局部 **Y（up）** 方向刚度 | 官方 25–250；第三方 50 / 200 |
| `pitch_damping` | `pitchDamping` | 1/s | 同上方向阻尼 | 官方 2–12；第三方 7 |
| `along_stiffness` | `alongStiffness` | 1/s² | 局部 **Z（沿杆）** 方向刚度，**仅 `allow_length_flex` 时生效** | 几乎恒为 100（默认值） |
| `along_damping` | `alongDamping` | 1/s | 同上 | 0（94% 为 0） |
| `allow_length_flex` | 清 `flags & 0x20` | 布尔 | 允许杆长伸缩。**不写 = 杆长刚性 = `HAS_LENGTH_CONSTRAINT`** | 第三方 **95% 都不写**（保持刚性） |

> ⚠️ 默认值是"刚杆"。95% 的真实资产是 `IS_FLEXIBLE | HAS_ANGLE_CONSTRAINT | HAS_LENGTH_CONSTRAINT`（`[本机实测]`，3049/3392）。

#### 角度/方向约束（`is_flexible` / `is_rigid` 通用）

| QC 键 | 磁盘字段 | 单位 | 物理含义 | 典型值 |
|---|---|---|---|---|
| `angle_constraint` | `angleLimit` | **QC 度 / 磁盘弧度** | 圆锥最大偏角，超出就把 tip 夹回锥面 | 官方 0.9°–80°；第三方 35°（79%） |
| `yaw_constraint <min> <max>` | `minYaw`/`maxYaw` | 度 / 弧度 | 局部 XZ 平面内的 ±偏角极限（`atan2(localAlong.x, localAlong.z)`）。**文档警告：≥±90° 会翻转** | 官方 ±25°~±120°；第三方 ≈5% 使用，常见 ±120° |
| `pitch_constraint <min> <max>` | `minPitch`/`maxPitch` | 度 / 弧度 | 局部 YZ 平面内的上下极限（`atan2(localAlong.y, localAlong.z)`）。**头发强烈建议用这个而不是 yaw** | 官方 −135°~+90°；第三方 ≈5% |
| `yaw_friction` / `pitch_friction` | `yawFriction`/`pitchFriction` | 无量纲 | **已废弃**：源码在触限时直接 `tipVel.Zero()` | 少数资产 2–10，**无效果** |

#### `has_base_spring`（基部平移弹簧，整个骨骼平移，不旋转）

| QC 键 | 磁盘字段 | 单位 | 物理含义 | 典型值 |
|---|---|---|---|---|
| `stiffness` | `baseStiffness` | 1/s² | 基部三轴弹簧刚度 | 100（默认）/ 280 / 400 |
| `damping` | `baseDamping` | 1/s | 基部阻尼 | 2 / 3 / 10 |
| `base_mass` | `baseMass` | 加速度 (u/s²) | 基部重力强度，0 = 无重力 | 0（Valve 的 spitter 就是 0） |
| `left_constraint <min> <max>` | `baseMinLeft`/`baseMaxLeft` | Source 单位 | 局部 X 位移范围 | ±0.4 / ±2 |
| `up_constraint <min> <max>` | `baseMinUp`/`baseMaxUp` | 单位 | 局部 Y | ±0.1 / ±0.5 |
| `forward_constraint <min> <max>` | `baseMinForward`/`baseMaxForward` | 单位 | 局部 Z | −0.05..1.0 |
| `left_friction` / `up_friction` / `forward_friction` | `baseLeftFriction` 等 | 无量纲 | **这个是真起作用的**（基部弹簧的摩擦没被删） | 0 / 10 |

> `base_spring` 在 L4D2 官方资产里确实用了：`models/infected/spitter.mdl` 的 `boob_jiggle_L_0`、`belly_jiggle_0` 就是**纯 base spring**（`flags=0x40`，没有任何 tip 弹簧）。`[本机实测]` 第三方动漫 mod 里 **0 次使用**。

#### `is_rigid`（刚杆）

参数只有 `length` + `tip_mass` + 上述约束；求解器里和 `is_flexible` 走同一段代码，**唯一区别是不施加 yaw/pitch/along 弹簧力**（于是"重力把杆拽下去、约束把它挡住"）。`[官方源码]`

#### `is_boing`

`impact_speed` / `impact_angle` / `damping_rate` / `frequency` / `amplitude`。**v49（L4D2/CSGO）的 `mstudiojigglebone_t` 没有这 5 个字段**，只有 v48（GMod/TF2 系）有。`[官方源码]+[仓库既有结论 docs/agent/external/L4D2-AUTHORITATIVE-FACTS.md]`

---

## 2. 磁盘布局（交叉验证用）

`[官方源码]` `studio.h:185-252` + `[本机实测]`（我用独立 Python 实现读出来的，与仓库 C# `HD2SourceModel/SourceModelReader.cs` 的常量一致）：

```
mstudiobone_t（v44..v53，stride 216）:
  +0    int sznameindex        （相对该 bone 结构体自身）
  +4    int parent
  +96   matrix3x4 poseToBone   （12 float，行主序）
  +164  int proctype           （==5 即 STUDIO_PROC_JIGGLE）
  +168  int procindex          （**相对该 bone 结构体自身**的字节偏移，不是相对文件头！）

mstudiojigglebone_t（v49，120 字节）:
  +0   int   flags
  +4   float length
  +8   float tipMass
  +12  yawStiffness   +16 yawDamping   +20 pitchStiffness  +24 pitchDamping
  +28  alongStiffness +32 alongDamping
  +36  angleLimit
  +40  minYaw  +44 maxYaw  +48 yawFriction  +52 yawBounce
  +56  minPitch +60 maxPitch +64 pitchFriction +68 pitchBounce
  +72  baseMass +76 baseStiffness +80 baseDamping
  +84  baseMinLeft +88 baseMaxLeft +92 baseLeftFriction
  +96  baseMinUp   +100 baseMaxUp  +104 baseUpFriction
  +108 baseMinForward +112 baseMaxForward +116 baseForwardFriction
```

`flags` 位：`0x01 IS_FLEXIBLE`、`0x02 IS_RIGID`、`0x04 HAS_YAW_CONSTRAINT`、`0x08 HAS_PITCH_CONSTRAINT`、`0x10 HAS_ANGLE_CONSTRAINT`、`0x20 HAS_LENGTH_CONSTRAINT`、`0x40 HAS_BASE_SPRING`、`0x80 IS_BOING`。

> **有个大坑要提醒**：本机第三方 mod 的 `sznameindex` 指向的是**内嵌的 SMD 文本**，于是读出来的"骨骼名"是垃圾/纯数字（如 `749637530`）。`[本机实测]` 所以**不要用骨骼名做启发式判断**（"hair"/"ponytail"），要靠层级 + 几何。仓库 C# 里 `ReadBoneName` 的注释已经写了这一点。

---

## 3. 求解器算法（能照着实现的程度）

### 3.1 总览：**位置弹簧，不是角度弹簧**——但只在 3 个局部轴上分解

关键事实（`[官方源码]` `CJiggleBones::BuildJiggleTransformations`）：

- 状态量是**世界空间的一个点** `tipPos` / `tipVel`，不是四元数/欧拉角；
- 但它只沿**父级目标矩阵的 3 个轴**做分解：`goalLeft = col0`、`goalUp = col1`、`goalForward = col2`；
- 而且默认（95% 的资产）带 `HAS_LENGTH_CONSTRAINT`，每帧结束把 `tipPos` 投影回半径 `length` 的球面 → **净效果等价的自由度只有 2 个（yaw 偏 + pitch 偏）**，along 方向被锁死。这就是文档说"rotational motion only, 中心在骨骼根部"的由来。

### 3.2 伪代码（严格照抄源码，包括顺序）

```text
每帧，对每根 proctype==JIGGLE 的骨骼（父级已算完，所以天然支持多级串联）：

  goalMX  = 该骨骼的动画/父级目标矩阵（世界空间 3x4）
  base    = goalMX 平移列
  left    = goalMX 第0列 ; up = 第1列 ; fwd = 第2列
  goalTip = base + length * fwd

  # --- 状态管理与"保温"逻辑 ---
  if 该骨第一次出现:                       init(tipPos=goalTip, tipVel=0)
  if (now - lastUpdate) > 1.2*frametime:   重新 init    # 被禁用过又启用
  if lastLeft 为零:                        lastLeft = left

  dt = now - lastUpdate
  if dt < 1ms:   dt = 1ms
  elif dt > 1/45s:                         # cl_jiggle_bone_framerate_cutoff 默认 45
      boneMX = goalMX ; return             # ★ 掉帧时整根骨骼直接跳过模拟
  lastUpdate = now

  # ---------- A. tip 弹簧（旋转部分）----------
  if flags & (IS_FLEXIBLE|IS_RIGID):
      tipAccel.z -= tipMass                # ★ 重力：世界空间，向下，大小=tipMass
      if IS_FLEXIBLE:
          err = goalTip - tipPos
          lx = dot(left,err) ; ly = dot(up,err) ; lz = dot(fwd,err)
          vx = dot(left,tipVel) ; vy = dot(up,tipVel)
          yawAccel   = yawStiffness   * lx - yawDamping   * vx
          pitchAccel = pitchStiffness * ly - pitchDamping * vy
          if flags & HAS_LENGTH_CONSTRAINT:
              tipAccel += yawAccel*left + pitchAccel*up
          else:                            # allow_length_flex
              vz = dot(fwd,tipVel)
              alongAccel = alongStiffness*lz - alongDamping*vz
              tipAccel += yawAccel*left + pitchAccel*up + alongAccel*fwd
      # 显式（半隐式）欧拉，dt 已经在上面夹过
      tipVel += tipAccel*dt ; tipPos += tipVel*dt ; tipAccel = 0

      # ---------- B. 约束，顺序固定 ----------
      if flags & (HAS_YAW_CONSTRAINT|HAS_PITCH_CONSTRAINT):
          along = tipPos - base
          lAlong = (dot(left,along), dot(up,along), dot(fwd,along))
          if HAS_YAW_CONSTRAINT:
              yawErr = atan2(lAlong.x, lAlong.z)          # 注意是 x 对 z
              if yawErr < minYaw or yawErr > maxYaw:
                  yaw = clamp(yawErr, minYaw, maxYaw)
                  limitMX = concat(goalMX, rotY(yaw))      # 在父级系里转 yaw
                  tipPos = base + dot(limitUp,along)*limitUp + dot(limitFwd,along)*limitFwd
                  tipVel = 0                               # ★ 直接清零（旧版本的摩擦被删了）
                  重算 along / lAlong
          if HAS_PITCH_CONSTRAINT:
              # 完全对称，yawErr 换成 atan2(lAlong.y, lAlong.z)，limitMX = goalMX*rotX(pitch)
              # 触限：tipPos = base + dot(limitLeft,along)*limitLeft + dot(limitFwd,along)*limitFwd
              #        tipVel = 0

      fwdDir = normalize(tipPos - base)

      if flags & HAS_ANGLE_CONSTRAINT:
          ang = acos(dot(fwdDir, fwd)) ; if dot<0: ang = 2π - ang
          if ang > angleLimit:
              r = length * sin(angleLimit)
              tipPos = goalTip - r * normalize(goalTip - tipPos)
              fwdDir = normalize(tipPos - base)

      if flags & HAS_LENGTH_CONSTRAINT:
          tipPos = base + length * fwdDir                  # ★ 刚杆 → 投影回球面
          tipVel -= dot(tipVel, fwdDir) * fwdDir           # 去掉径向速度

      # ---------- C. 拼矩阵 ----------
      leftNew = normalize(cross(up, fwdDir))
      if dot(leftNew, lastLeft) < 0: leftNew = -leftNew    # ★★ 防 180° 翻转（关键！）
      lastLeft = leftNew
      upNew = cross(fwdDir, leftNew)
      boneMX = [leftNew, upNew, fwdDir, base]              # 列向量拼装

  # ---------- D. 基座弹簧（平移部分，独立分支）----------
  if flags & HAS_BASE_SPRING:
      baseAccel.z -= baseMass                              # 世界空间重力
      baseAccel += baseStiffness*(base - basePos) - baseDamping*baseVel
      baseVel += baseAccel*dt ; basePos += baseVel*dt ; baseAccel = 0
      把 (basePos-base) 分解到 left/up/fwd，逐轴 clamp 到 [min,max]
      触限的那一轴按 对应的 *_friction 施加面内摩擦
      basePos = base + lErr.x*left + lErr.y*up + lErr.z*fwd
      baseVel = (basePos - baseLastPos)/dt ; baseLastPos = basePos
      if 没有 tip 弹簧: boneMX = goalMX
      boneMX 平移列 = basePos
```

### 3.3 单位与量纲（这是移植时最容易错的地方）

- `stiffness` 的量纲是 **1/s²**（`accel = k·error`，error 是长度）→ `ω_n = sqrt(stiffness) rad/s`。
- `damping` 的量纲是 **1/s** → 阻尼比 `ζ = damping / (2·sqrt(stiffness))`。
  - 例：`k=50, c=7` → `ω_n = 7.07 rad/s ≈ 1.13 Hz`，`ζ = 0.50`（欠阻尼，会抖 2~3 下）——**这就是"典型 jiggle"的甜点**。
  - `k=50, c=2`（Valve 的 braid 第一根）→ `ζ=0.14`，抖很久。
- **`tip_mass` 不是质量**：源码是 `tipAccel.z -= tipMass`，直接当加速度用。Source 的真实重力是 600 u/s²，所以 `tip_mass 200` ≈ **0.33 倍真实重力**，`tip_mass 1000`（Witch 的刘海）= **1.67 倍重力**。
- **`length` 会在重力项里起分母作用**（见 §3.5）。

### 3.4 静止姿态：重力在哪一步、回正力是什么

`[官方源码]` 明确的执行顺序：**先加重力加速度 → 再算弹簧加速度 → 再欧拉积分**，也就是说重力**每帧**都被加到 `tipAccel` 上，而弹簧每帧都把它拉回 `goalTip`。

- **回正力 = 弹簧误差项本身**：`err = goalTip - tipPos`。`goalTip` 来自当期动画/父级的 `goalMX`。所以：
  - `tip_mass = 0` 时，骨头**严格贴着动画姿态**（无下垂）；
  - `tip_mass > 0` 时，稳态解是"在目标姿态基础上偏移一个下垂角 θ"，满足
    **`sinθ ≈ tip_mass / (stiffness · length)`**（θ 是 tip 相对目标方向偏转的角度，小角近似）。
  - 举例（第三方主流配方 `tip_mass 200 / stiffness 50 / length 30`）：`200/(50·30)=0.133` → **θ≈7.7°**。这个量级刚好是"自然下垂"的样子。
  - 如果把 `length` 换成真实骨距 3.5：`200/(50·3.5)=1.14` → 无解，**直接撞上 `angle_constraint`（35°）被夹死**，看起来就是"头发笔直下垂、僵死"。**如果重力符号或坐标系弄反，就是同一个式子的负号错误 → 头发向上飘。**

> 这条基本锁定了你们的 bug 方向：`向上飘扬/错误方向下垂` = 重力方向/符号或坐标系 handness 问题；`过于僵死或过度下垂` = 用了真实骨距而不是作者的 `length`。

### 3.5 `length` 到底是什么（**给 parent 的专项回答**）

**结论：`length` 是"虚拟摆杆长度"，与"到子骨的距离"无关。它只出现在三个地方：**

1. `goalTip = base + length·forward` —— 决定质量点在哪；
2. 角度锥半径 `r = length·sin(angleLimit)`；
3. 刚杆投影 `tipPos = base + length·forward`。

**证据一（源码）**：`[官方源码]` 全文只有这三处使用 `length`，**没有任何一处与子骨位置比较或校验**。wiki 那句 "This value should match the length of the bone" 是**建议**，不是语义。

**证据二（Valve 自己没照做）**：`[本机实测]` 我把 24 根官方 jigglebone 的 `length` 和真实子骨距离比了一遍：

| 模型 | 骨骼 | `length` | 真实子骨距 | 比值 |
|---|---|---|---|---|
| survivor_producer | `ValveBiped.pony01` | 12 | 4.09 | **2.94×** |
| survivor_producer | `ValveBiped.braid01_01` | 7 | 2.70 | 2.59× |
| survivor_producer | `ValveBiped.braid01_03` | 7 | 2.10 | 3.33× |
| witch | `jiggle_bangA_1` | 5 | 3.84 | 1.30× |
| spitter | `pigtail_jiggle_R_0` | 7 | 2.68 | 2.61× |
| v_molotov | `jiggle_rag_dn1` | 15 | 1.31 | **11.4×** |
| v_molotov | `jiggle_rag_up0` | 30 | 0.88 | **34.2×** |
| hulk | `jiggle_tongue01` | 5 | 0.93 | 5.35× |

→ **中位数 2.4×，范围 1.23×~34×。Valve 从来没让 `length` 等于骨长。** 你们实测到的 3.8× 完全落在这个分布里，属于正常。

**证据三（第三方作者更不照做）**：`[本机实测]` 10 个动漫 mod 的 3392 条记录里，`length` **只有 10 / 20 / 30 三个取值**（`30` 占 3141 条 = 93%），而真实骨距中位数 3.5。**人家就是拿它当"灵敏度旋钮"在调。**

**所以移植时必须**：`length_new = length_orig`（原封不动），并且**不要**用真实骨距去反推 stiffness。如果非要用真实骨距 `d` 做杆长（例如你们为了让网格跟随真实骨骼），必须做增益换算：

```
若杆长从 L 改成 d，为保持角响应一致：  stiffness_new = stiffness_orig · (L / d)
                                       damping_new   = damping_orig      # 注意：阻尼不需要换算
                                       tip_mass_new  = tip_mass_orig · (d / L)   # 保持重力下垂角不变
```
`[推断]`（由 `sinθ ≈ tip_mass/(k·L)` 与 `ω_n=sqrt(k)` 推出；`k·L` 不变则角刚度不变，`tip_mass/L` 不变则下垂角不变。）

### 3.6 有没有碰撞？

**没有。一点都没有。** `[官方源码]`

- `jigglebones.cpp` 全文没有任何 collision/physics 接口调用；它只接收 `(boneIndex, currenttime, jigglebone_t*, goalMX)`，输出一个矩阵。没有世界查询、没有自碰撞、没有骨-骨互斥。
- jigglebone **之间也不通讯**：每根骨骼的 `tipPos/tipVel` 独立存在 `m_jiggleBoneState` 链表里。
- 多级串联（`hair01→hair02→hair03`）**靠父子矩阵自然传播**：`hair02` 的 `goalMX` 来自已经算好的 `hair01` 的实际（含抖动的）矩阵，所以上一节的抖动量会带动下一节。这是**运动学串联**，不是"链式约束求解器"。`[官方源码]`（`BuildJiggleTransformations` 在骨骼正序循环里被调用，传入父级算完的 `goalMX`）

> 另外注意：ragdoll 的 `$collisionjoints`（vphysics 布娃娃）和 `$jigglebone` 是**两套完全独立的系统**。jigglebone 骨骼一般不参与 ragdoll 碰撞体。

**所以**：用户抱怨的"缺碰撞"，在原版 L4D2 里就是**没有**的东西。我们的 HD2 端如果加了碰撞（HD2 的 Stingray 物理是真物理），那是**超越原版**，不是补 bug。要不要做是产品决策，但**不能拿"原版有碰撞"当需求依据**。

### 3.7 "Z 字形折痕"的可能成因（`[推断]`，逐条可验证）

1. **`lastLeft` 翻转保护没实现**（最常见）。源码那三行：
   ```cpp
   Vector left = CrossProduct(goalUp, forward); left.NormalizeInPlace();
   if (DotProduct(left, data->lastLeft) < 0.0f) left = -left;   // 翻转 180° 修正
   data->lastLeft = left;
   ```
   当杆摆过 `goalUp` 轴时叉积结果会突然反转 180°，导致**骨骼的 roll 突变**→ 网格沿骨骼扭转 → 视觉上就是 Z 字折痕。如果我们的 look-at/四元数构造没有这个历史依赖的符号修正，一定会出现。
2. **角约束投影把 tip 拍平**：`angle_constraint` / `yaw_constraint` 触限时 `tipVel = 0` 且 tipPos 被投到平面，姿态会"啪"地跳。
3. **每顶点 3 权重组 + 长链**：L4D2 每顶点只有 3 个权重组，长头发链在骨骼数量多时权重被截断，扭转时会出现折痕。这是**原版也有的已知限制**（127 骨上限、3 权重），不是我们的 bug。
4. 我们自己的求解器若用"角度弹簧 + 欧拉角"，在 pitch 接近 ±90° 时会出现 gimbal/翻转，也会 Z 字。→ **建议直接用"世界空间 tip 点 + 球面投影"这套表示，天然免疫。**

---

## 4. 真实作者写法样本 + 统计

### 4.1 Valve 官方（`[本机实测]`，L4D2 `pak01_dir.vpk`，3333 个 `.mdl` 里 **只有 22 个**用了 jigglebone）

官方用 jigglebone 的资产清单（值得注意：**主角模型里只有 Coach 有**）：

```
models/survivors/survivor_producer.mdl      ← Coach，小辫子×3 + 马尾×2
models/survivors/anim_teenangst.mdl
models/infected/{hulk,charger,smoker,boomer,boomette,witch,spitter}.mdl 及其 anim_*
models/v_models/{v_cola,v_bile_flask,v_molotov,v_pipebomb}.mdl
models/weapons/melee/v_electric_guitar.mdl
models/w_models/weapons/w_eq_bile_flask.mdl
models/infected/limbs/exploded_{boomer,boomette}.mdl
```

**样本 A：Coach 的马尾（3 级链里 2 级上弹簧）** —— 这是"头发"的最佳范本：

```qc
// models/survivors/survivor_producer.mdl  bone#30/31，parent 16 / 30
$jigglebone "ValveBiped.pony01" {          // flags=0x2d = FLEX|YAW|PITCH|LENGTH
    is_flexible {
        length 12          tip_mass 600
        yaw_stiffness 250  yaw_damping 12
        pitch_stiffness 250 pitch_damping 12
        along_stiffness 100
        yaw_constraint   -25 25      // minYaw/maxYaw = ±0.436 rad
        pitch_constraint -5  35      // -0.087 .. 0.611 rad
        angle_constraint 0           // 未设（flag 0x10 未置位）
    }
}
$jigglebone "ValveBiped.pony02" {          // 第二级：更软
    is_flexible {
        length 14          tip_mass 600
        yaw_stiffness 100  yaw_damping 6
        pitch_stiffness 100 pitch_damping 6
        along_stiffness 100
        yaw_constraint   -25 25
        pitch_constraint -5  50
    }
}
```

**样本 B：Coach 的小辫子（3 束 × 3~4 级，逐级变软）** —— 典型的"多级链公式"：

```qc
// braid01_01..04（bone#20..23），每级 parent 上一级
$jigglebone "ValveBiped.braid01_01" {
    is_flexible { length 7 tip_mass 100
        yaw_stiffness 25 yaw_damping 2  pitch_stiffness 25 pitch_damping 2
        yaw_constraint -65 65  pitch_constraint 0 65 }
}
$jigglebone "ValveBiped.braid01_02" {
    is_flexible { length 7 tip_mass 100
        yaw_stiffness 100 yaw_damping 8  pitch_stiffness 50 pitch_damping 5
        yaw_constraint -45 45  pitch_constraint 0 40 }
}
$jigglebone "ValveBiped.braid01_03" {
    is_flexible { length 7 tip_mass 100
        yaw_stiffness 100 yaw_damping 10 pitch_stiffness 50 pitch_damping 6
        yaw_constraint -45 45  pitch_constraint -10 25 }
}
$jigglebone "ValveBiped.braid01_04" {
    is_flexible { length 3 tip_mass 100
        yaw_stiffness 25 yaw_damping 8  pitch_stiffness 25 pitch_damping 6
        yaw_constraint -45 45  pitch_constraint -15 15 }
}
```
模式：**根部软（k=25、c=2）→ 中段硬（k=100、c=8~10）→ 末梢又变软（k=25、length 变短）**，且**约束逐级收窄**（±65° → ±45° → ±15°）。这是"链太长发散"的官方解法。

**样本 C：Witch 的刘海（重力最大的样例）** `bone#53..65`：

```qc
$jigglebone "ValveBiped.jiggle_bangA_1" {
    is_flexible { length 5 tip_mass 1000           // ★ 1.67 倍真实重力
        yaw_stiffness 100 yaw_damping 5 pitch_stiffness 100 pitch_damping 5
        pitch_constraint 10 80 }                   // 只允许往前下方摆
}
$jigglebone "ValveBiped.jiggle_bangA_2" {
    is_flexible { length 5 tip_mass 100
        yaw_stiffness 100 yaw_damping 5 pitch_stiffness 100 pitch_damping 5
        yaw_constraint -45 45 pitch_constraint -45 45 }
}
// 注意：第 1 级只用 pitch_constraint（避免 yaw ≥90° 的翻转 bug）
```

**样本 D：Spitter 的 base spring（唯一的官方 base_spring 实例）**：

```qc
$jigglebone "boob_jiggle_L_0" {          // flags=0x40，纯基部弹簧
    has_base_spring {
        stiffness 100  damping 2  base_mass 0
        left_constraint -2 2  up_constraint -2 2  forward_constraint -0.05 1.0
    }
}
$jigglebone "belly_jiggle_0" {
    has_base_spring {
        stiffness 280  damping 3  base_mass 0
        left_constraint -1 1  up_constraint -0.5 0.5  forward_constraint -0.15 0.45
    }
}
```

**样本 E：布料/瓶液（`length` 用得很随意的证据）**：

```qc
// v_molotov.mdl，瓶子布条 rag，5 级链，逐级放大 angleLimit
$jigglebone "ValveBiped.jiggle_rag_up0" { is_flexible { length 30 tip_mass 0
    yaw_stiffness 900 yaw_damping 10 pitch_stiffness 900 pitch_damping 10 angle_constraint 10 } }
$jigglebone "ValveBiped.jiggle_rag_up1" { is_flexible { length 15
    yaw_stiffness 300 yaw_damping 8  pitch_stiffness 300 pitch_damping 8 angle_constraint 40 } }
$jigglebone "ValveBiped.jiggle_rag_up2" { is_flexible { length 15
    yaw_stiffness 100 yaw_damping 6  pitch_stiffness 100 pitch_damping 6 angle_constraint 80 } }
$jigglebone "ValveBiped.jiggle_rag_up3" { is_flexible { length 15
    yaw_stiffness 100 yaw_damping 6  pitch_stiffness 100 pitch_damping 6 angle_constraint 80 } }
// 注意 tip_mass 0 —— 布料完全不掉，只跟随动画抖
```

### 4.2 第三方 L4D2 角色 mod（`[本机实测]`，10 个 VPK / 80 个 mdl / **3392 条 jigglebone**）

用的都是本机订阅的动漫角色 mod（`[l4n_survivor] Serenade_/MourningVeil_/Manuka_*/Lunalice_/Lilium_Sancta_/Elise_` 等）。

**flag 分布（说明作者实际只用了哪几种模式）**：

| 组合 | 条数 | 占比 |
|---|---|---|
| `IS_FLEXIBLE + HAS_ANGLE_CONSTRAINT + HAS_LENGTH_CONSTRAINT`（0x31） | 3049 | **89.9%** |
| `IS_FLEXIBLE + HAS_ANGLE_CONSTRAINT`（0x11，非刚杆） | 172 | 5.1% |
| `IS_FLEXIBLE + YAW + PITCH + ANGLE + LENGTH`（0x3d） | 135 | 4.0% |
| `IS_FLEXIBLE + PITCH + ANGLE + LENGTH`（0x39） | 20 | 0.6% |
| `IS_FLEXIBLE + YAW + ANGLE + LENGTH`（0x35） | 16 | 0.5% |

`IS_FLEXIBLE` **100%**；`is_rigid` **0 次**；`has_base_spring` **0 次**；`IS_BOING` **0 次**。

**参数众数（这就是"实战主流配方"）**：

| 参数 | 众数 | 条数 | 其它值 |
|---|---|---|---|
| `length` | **30** | 3141 | 10（135）、20（116） |
| `tip_mass` | **200** | 3141 | 500（135）、**0**（116） |
| `yaw_stiffness` | **50** | 3257 | 1（126）、0（9） |
| `pitch_stiffness` | **50** | 3141 | 1（126）、200（116） |
| `yaw_damping` / `pitch_damping` | **7** | 3141 | 10（135）、5（116） |
| `along_stiffness` | **100** | 3327 | 0（65） |
| `along_damping` | **0** | 3201 | 10（191） |
| `angle_constraint` | **0.61 rad = 35°** | 2681 | 0.30 rad=17°（315）、**6.28 rad=360°**（135）、0.21=12°（120） |
| `yawBounce` / `pitchBounce` | **恒 0** | 3392 | — |
| `yaw_friction`/`pitch_friction` | 0 | 3266/3257 | 2~10（约 130） |

**组合起来，第三方 mod 的实际"通用头发配方"就是：**

```qc
$jigglebone "<hair_bone>" {
    is_flexible {
        length          30
        tip_mass       200
        yaw_stiffness   50   yaw_damping    7
        pitch_stiffness 50   pitch_damping  7
        along_stiffness 100  along_damping  0
        angle_constraint 35
    }
}
```
→ `ω_n = √50 = 7.07 rad/s (1.13 Hz)`、`ζ = 0.50`、重力 = 0.33g、稳态下垂 ≈ `asin(200/(50×30)) ≈ 7.7°`，并且**只用 angle_constraint 一个锥**（不上 yaw/pitch 平面约束）。这就是"能用的最小配方"。

另外 135 条用了"另一套"（大角度 + 高重力 + 无质量感）：
```qc
is_flexible { length 10 tip_mass 500 yaw_stiffness 50 yaw_damping 10
              pitch_stiffness 50 pitch_damping 10 along_stiffness 100
              yaw_constraint -120 120 pitch_constraint -120 120
              angle_constraint 360 }
```
（`angle_constraint 360` 实际等于不约束；`yaw/pitch ±120°` 超出 wiki 的 ±90° 警告区 → 这批资产在原版里**很可能本来就会翻转**，等于作者"用 clamp 当兜底"。）

**多级链的真实形态（`[本机实测]`）**：同一个 mdl 里 `bone#67→68→69→70` 这样 4 级串联很常见，而且**每一级都是 jigglebone**（不是只给第一根上弹簧）：

```
bone#64 parent=12  ┐
bone#65 parent=12  ├ 4 束独立的头发，都挂在 bone#12（头）
bone#66 parent=12  │
bone#67 parent=12  ┘
bone#68 parent=67      ← 链第 2 级
bone#69 parent=68      ← 链第 3 级
bone#70 parent=69      ← 链第 4 级
bone#71 parent=12 ; #72 parent=71 ; #73 parent=72 ; #74 parent=73
bone#75 parent=12
```
→ **"只给第一根上弹簧"是错的**。原版做法是**链上每级都写 `$jigglebone`**，靠父子矩阵把抖动往下传（§3.6）。

### 4.3 社区配方（`[社区]` Feromon32/JiggleBones-QC-Creater README，作者按"尾巴/大衣/胸/道具/长发"分了类）

```qc
// 尾巴 tails
$jigglebone "bone" { is_flexible {
    length 6  tip_mass 250
    pitch_constraint -80 80  pitch_stiffness 55  pitch_damping 7
    yaw_stiffness 35  yaw_damping 7
    along_stiffness 100  along_damping 0
    yaw_constraint -80 80  angle_constraint 40 } }

// 大衣 coats —— 第一级用 is_rigid + 平面约束 + 摩擦，后面几级转 is_flexible
$jigglebone "bone1" { is_rigid {
    length 20  tip_mass 400
    pitch_constraint -80 -5  pitch_friction 2  pitch_bounce 0
    yaw_constraint -50 50    yaw_friction 2    yaw_bounce 0 } }
$jigglebone "bone2" { is_flexible { length 6 tip_mass 250
    pitch_stiffness 55 pitch_damping 7 yaw_stiffness 35 yaw_damping 7
    along_stiffness 100 along_damping 0 angle_constraint 40 } }
// bone3/4 继续，pitch_stiffness 递减 55→54→44，angle_constraint 递增 40→50→60

// 胸 breasts —— 纯 base spring
$jigglebone "bone" { has_base_spring {
    base_mass 0  stiffness 400  damping 10
    left_constraint -0.4 0.4   left_friction 0
    up_constraint -0.1 0.1     up_friction 0
    forward_constraint -0.5 0.5 forward_friction 0 } }

// 道具 items（"Cuba Jigglebone Attachments"）—— length 100 + tip_mass 0
$jigglebone "bone" { is_flexible {
    length 100  tip_mass 0
    pitch_stiffness 200 pitch_damping 10 yaw_stiffness 200 yaw_damping 10
    along_stiffness 100 along_damping 0 angle_constraint 20 } }
```

README 开头还有一句社区铁律（和 §3 的轴对称问题对应）：

> “ALL JIGGLE BONE MUST BE WATCHED IN **-Z** COORDINATES FOR PROPER PHYSICS IN THE GAME”

以及 SFM 讨论帖里的关键提醒：`[社区]`

> "if you're using Blender, this is *not* the same orientation as the bone is shown as in Blender. **Blender's bones use the Y-axis as their length, but Source assumes bones face towards their Z-axis.**"

→ **这正是"向上飘扬/方向错"的头号元凶候选**：Source 骨骼的"长度方向"是 **+Z**，而 Blender 是 **+Y**。任何在 Blender 侧/我们自己的转换里把"骨向量"取成 Y 或 -Z 的地方，都会让杆朝错方向。

### 4.4 `$bonemerge` / `$jointconstrain` / `$jointmassbias` / `$jointrotdamping` / `$collisionjoints` 在角色 mod 里用不用？

- `$bonemerge`：**用于**。L4D2 角色 mod 生态里非常常见（把帽子/饰品/武器挂到主角骨骼上做"配件"，如本机看到的 `Serenade武器小刀.vpk`、`MourningVeil法杖替换铲子.vpk` 这类）。NekoMDL 的更新日志里也专门提到"不要 collapse 被 `$Attachment`/`$Bonemerge` 用到的无权重骨骼"，说明它确实是常用功能。`[社区+官方文档]`
- `$collisionjoints`（ragdoll 碰撞体）：**用于 corpse/ragdoll，不参与 jigglebone**。上面 22 个官方模型之外，角色 mod 一般会各自写一套 ragdoll。（`[推断]` 基于两套系统独立；未逐 mod 统计 —— 见 §7 的"没查"清单）
- `$jointconstrain` / `$jointmassbias` / `$jointrotdamping`：这是 **ragdoll 关节**参数（`$collisionjoints` 块内），与 jigglebone 无关。**我没有找到任何把它们用于"头发物理"的例子**，也没有证据说不能这么用。`[推断]`

---

## 5. 工具清单（能不能编辑 / 能不能导出 / **能不能实时预览弹簧**）

> 先说最重要的结论：**我只找到一个"能实时预览 jigglebone 弹簧"的现成方案，就是引擎自己跑起来**。所有离线 Viewer 都只能看静态姿态和序列动画，**不跑那个积分器**。原因很硬：积分器 `CJiggleBones` 编在 `client.dll` 里（本机 `left4dead2\bin\client.dll` 里能 grep 到 `cl_jiggle_bone_debug*` 等 5 个 cvar 字符串），而 HLMV 是个独立小工具，只读 mdl 的静态数据。`[本机实测]+[推断]`

### 5.1 编译 / 解编译（QC ⇄ mdl）

| 工具 | 能干什么 | 不能干什么 | jiggle 相关 |
|---|---|---|---|
| **`studiomdl.exe`**（Valve 官方） | 编译 QC→mdl/vvd/vtx/phy | 闭源；L4D2 安装目录里**没有**，要从 Source SDK 或别的游戏目录拿 `[本机实测]` | 解析 `$jigglebone` 并写入 `mstudiojigglebone_t` |
| **NekoMDL**（studiomdl 的 64 位增强 fork）`[官方文档]` | 更高上限、glTF/FBX 直读、`-reloadhlmv` 编译后热重载 HLMV | 不是 Valve 官方；社区维护 | **有两根专门修 jigglebone 轴的命令：`$JiggleBoneRealign`、`$ReverseJiggleBoneRotation`** —— 说明"轴不对"是社区公认痛点 |
| **`mdlc`**（Rust 重写的独立编译器，MoRanYue/mdlc）`[社区]` | 跨平台、无依赖重写 `studiomdl`；v48/v49 布局都实现（含 `is_boing`） | 新项目，边界情况可能没覆盖 | 仓库既有文档 `docs/agent/external/L4D2-AUTHORITATIVE-FACTS.md` 的很多结论引的就是它（**这是你们最好的第二意见来源**） |
| **Crowbar**（ZeqMacaw）`[社区]` | **mdl → QC 反编译（含 `$jigglebone` 块）**、批量解包 VPK、调 HLMV 预览、批量编译 | 反编译不是 100% 无损（作者自称 ~97%）；v49 + `$sequence` 用 delta/predelta 时 jigglebone 与 pose 参数会串（v0.63 修过） | **这就是你要的"把 mdl 里的 jiggle 参数 dump 成结构化文本"的现成工具** —— 直接产出可读 QC。**建议拿它和你们 C# 解析器的输出做交叉验证** |
| **Blender Source Tools**（Artfunkel）`[官方文档]` | Blender ↔ SMD/DMX/QC 导入导出、`$definebone` 生成 | **不生成 `$jigglebone`**：QC 里的 jigglebone 块要手写或用脚本生成；也不模拟弹簧 | 有第三方脚本补这个洞（下一条） |
| **JiggleBones-QC-Creater**（Feromon32，Blender 插件脚本）`[社区]` | 选中骨架 → 一键把所有（或选中的）骨骼导出成 `$jigglebone` QC 文本到剪贴板；README 附带尾巴/大衣/胸/道具/长发 5 套现成配方 | 只生成文本，不模拟、不写回 mdl | **做"批量给链上每根骨加 jigglebone"最省事** |

### 5.2 查看 / 预览

| 工具 | 能干什么 | 不能干什么 | 能预览弹簧？ |
|---|---|---|---|
| **HLMV**（Valve 的 Half-Life Model Viewer） | 打开 mdl、切 sequence/bodygroup/skin、看骨骼、材质、attachments、hitbox | 不开 VPK（要先解包）；**不跑 jigglebone 积分器**；没有 physics 交互面板（interlopers 论坛明确说 viewer 只做"看模型和动画"）`[社区]` | ❌ |
| **HLMV++**（ImguRandom，HLMV 增强 fork）`[社区]` | HLMV 的 QoL 改进版（给 TF2 生态用得多） | 同上 | ❌（未见 jiggle 模拟的证据） |
| **Crowbar 的 Preview 按钮** | 一键用 HLMV 打开刚反编译/编译的模型 | 同 HLMV | ❌ |
| **Noesis** | 通用模型查看器 | **没有官方 Source mdl 支持**；我没找到可靠的 `.mdl` 插件 | ❌（且不建议依赖） |
| **MDLops** | ⚠️ **注意：这是《星球大战：旧共和国武士》的工具，与 Source 引擎无关**。不要被名字骗 | — | ❌ |
| **ValveResourceFormat / Source2Viewer** | Valve 官方开源的资源查看/转换（Source1+Source2），能读 mdl/vpk 并导出 glTF | 面向 Source 2 为主；jigglebone 参数不作为一等公民导出 | ❌ |
| **L4D2 本体** | **唯一能跑原版积分器的环境** | 需要游戏 | ✅ **是** |

### 5.3 开源的求解器实现（可以拿来做三点交叉验证）

| 来源 | 说明 |
|---|---|
| **`source-sdk-2013/src/public/jigglebones.cpp`** | **这就是原版本体**。约 400 行，纯 C++，零依赖（只要 `matrix3x4_t`/`Vector`）。**最省事的做法：把它移植成 C#/Lua 单文件，逐行对照**，而不是"重新设计一个弹簧"。这是本次调研最重要的建议。 |
| **`MoRanYue/mdlc`**（Rust） | 编译器侧（QC→mdl）的独立实现，可用来核对 flags 默认值/键名/单位换算 |
| **`Feromon32/JiggleBones-QC-Creater`**（Python，Blender） | 只有"生成 QC 文本"的逻辑，**不含求解器** |
| ⚠️ 各种"Vulkan/Unity 弹簧头发"示例（如 idokan 的 VulkanModelViewer） | **不是 Source 语义**（没有"回到动画目标的弹簧"、没有 angle_constraint、重力处理也不同）。**可以借代码结构，不能借行为** |

---

## 6. 最省事的"原版 vs 我们"对照方案

### 6.1 看原版（Ground Truth）——推荐 A

**A. 直接进 L4D2，用引擎自带调试可视化**（唯一 100% 保真，15 分钟能出结果）

1. 订阅/放好目标 mod，进任意战役（`map c1m1_hotel`）。
2. 控制台：
   ```
   sv_cheats 1
   cl_jiggle_bone_debug 1                       // 画出每根 jigglebone 的 base/tip 和杆
   cl_jiggle_bone_debug_yaw_constraints 1       // 画 yaw 限制平面
   cl_jiggle_bone_debug_pitch_constraints 1     // 画 pitch 限制平面
   cl_jiggle_bone_framerate_cutoff 0            // 关掉"低于 45fps 就跳过模拟"
   ```
   > 这 5 个 cvar 我在本机 `left4dead2\bin\client.dll` 里 grep 到了：`cl_jiggle_bone_debug`、`cl_jiggle_bone_debug_yaw_constraints`、`cl_jiggle_bone_debug_pitch_constraints`、**`cl_jiggle_bone_invert`**、**`cl_jiggle_bone_sanity`**（后两个 SDK2013 里没有，是 L4D2 特有的，作用我没有查到，**不要猜**）。`[本机实测]`
3. 做**受控激励**：站着不动（看稳态下垂角）→ 快速左右转身（看 yaw 响应）→ 前后跑动/急停（看 pitch 响应）→ 从台阶跳下（看一次性冲击回弹）。
4. **录屏 240fps/60fps**，逐帧量 tip 的偏移角 → 这就是你们求解器的验收基准。

**B. 解静态数据（零成本，做参数回归用）**

- Crowbar 反编译 mdl → 得到带 `$jigglebone` 的 QC → 和你们 C# 解析器的输出逐字段 diff（`length/tipMass/6 个 stiffness-damping/angleLimit/min-max`）。这是"我们读对了参数没有"的验证，**不涉及动力学**。

**C.（不推荐）用 HLMV**：只能看静态/动画姿态，**看不到弹簧**，对验收没用。

### 6.2 看我们自己的结果

- 建议做一个**离线复算器**：把 `jigglebones.cpp` 逐行移植（C# 或 Python），喂**同样的 sequence 动画帧**（从 mdl 里读 bone 动画 / 或从 SMD 里读），输出每帧的 `boneMX`，和 §6.1-A 的录屏逐帧比对 tip 位置。
- 由于积分器是**显式欧拉 + 帧率相关**（`dt` 直接进积分，且 <45fps 直接跳过），**两边必须以相同 dt 跑**才可比。这是"看起来不一样"的常见假阳性来源。

---

## 7. 边界：能回答 / 不能回答

### 能（有明确出处）

- ✅ `$jigglebone` 的**全部真实 QC 键名、磁盘字段顺序、120 字节布局、flags 位** —— `[官方源码]` + `[本机实测]`
- ✅ 求解器的**逐行算法、执行顺序、单位、Euler 积分方式、约束投影细节、`lastLeft` 防翻转** —— `[官方源码]`
- ✅ **没有碰撞**、**没有摩擦**(tip 部分)、**有回正弹簧**、**重力是世界 −Z 的加速度且大小 = tip_mass** —— `[官方源码]`
- ✅ 多级串联靠父子矩阵传播，不是链式约束求解 —— `[官方源码]`
- ✅ `length` 不是骨长，是虚拟摆杆长度 + 决定下垂角 —— `[官方源码]` + 24 根官方骨实测 + 3392 条第三方统计
- ✅ 真实作者配方与参数分布（官方 22 个模型 / 第三方 3392 条）
- ✅ 工具能/不能做什么（见 §5）
- ✅ 预览：只有引擎本体能跑弹簧；`cl_jiggle_bone_debug` 可视化可用

### 不能 / 没查（**不猜**）

- ❌ **`cl_jiggle_bone_invert` / `cl_jiggle_bone_sanity` 的语义**：只在 L4D2 `client.dll` 里 grep 到字符串，SDK2013 源码里没有这两个 cvar，我没有找到任何文档。想确认只能在游戏里 `help cl_jiggle_bone_invert` 看描述。
- ❌ **`yaw_bounce` / `pitch_bounce` 的解析是否被 studiomdl 接受**：字段存在于磁盘结构体、社区 QC 里也在写，但 wiki 未收录，且**求解器不读**。我无法确认官方编译器是否报错。
- ❌ **`is_rigid` 与 `has_base_spring` 同时存在时**的精确行为：源码里两个分支是 if/else 关系（`if (HAS_BASE_SPRING) ... else if (IS_BOING) ...`），但 tip 部分是独立的 `if`，所以两者能叠加；组合语义我只能从代码推出，**没有实测资产佐证**（本机的第三方 mod 都没这么用）。
- ❌ **`HAS_LENGTH_CONSTRAINT` 与 `allow_length_flex` 的 flags 精确交互**（"块出现即置位 vs 只有 allow_length_flex 才清"）：我只有仓库既有文档（`[mdlc 源码]` 行号）的结论，**自己没跑实验验证**。
- ❌ **第三方 mod 里 `$collisionjoints` / ragdoll 关节参数的统计**：我没做（工作量大、和 jigglebone 无关）。`$jointconstrain`/`$jointmassbias`/`$jointrotdamping` 用于头发的例子**也没找到**。
- ❌ **`is_boing` 在 v49 的确切行为**：v49 结构体没有这 5 个字段，所以 L4D2 里应当无效；但我没做实机实验。
- ❌ **HLMV/HLMV++ 是否支持"播放动画时同时施加 jiggle"**：我只找到"viewer 只做看模型和动画"的论坛说法 + 积分器在 client.dll 的事实，**没有找到官方明确否认**。标 `[推断]`。
- ❌ **SFM / GMod 能否加载 v49 L4D2 模型并模拟 jigglebone**：没有可靠出处，**不下结论**。
- ⚠️ **`$jigglebone` 的 name 参数到底匹配"骨骼名"还是"joint tip 名"**：wiki 说 `<name>` 是 "the name of the joint tip"，但 studiomdl 实际是按**骨骼名**查找（找不到就打 `jigglebone "%s" unused` 并丢弃，仓库文档引 mdlc）。我倾向"就是骨骼名，wiki 措辞含糊"，但**没有决定性证据**。

---

## 8. 复现命令（本机实测部分）

我用的工具（都在 `%TEMP%\jb\`，**没有改动仓库任何文件**）：

```powershell
# 依赖：python 3.11 + pip install vpk
# 1) 统计官方模型：读 pak01_dir.vpk（3333 个 mdl 里 22 个有 jigglebone）
python scan_official.py

# 2) 统计第三方：读 left4dead2\addons\*.vpk + addons\workshop\*.vpk
python scan.py out.json "D:\SteamLibrary\...\addons\[l4n_survivor] Serenade_.vpk" ...

# 3) length vs 真实子骨距离（用 mstudiobone_t.poseToBone 求逆得骨骼模型空间位置）
python len.py "<vpk 路径>"

# 4) 确认 L4D2 里有 jiggle 调试 cvar
rg -a -o "cl_jiggle_bone_[a-z_]+" "D:\SteamLibrary\steamapps\common\Left 4 Dead 2\left4dead2\bin\client.dll" | sort -u
```

判定 mdl 版本的字段偏移：`id@0('IDST'), version@4, numbones@156, boneindex@160`，bone stride 216，`proctype@bone+164 == 5`，`procindex@bone+168`（**相对 bone 自身**）。

---

## 9. 给实现的三条具体动作

1. **把 `jigglebones.cpp` 移植成单文件，别自创模型**。特别是：
   - 状态用**世界空间 `tipPos/tipVel`**（不要用欧拉角/四元数弹簧）；
   - 每帧 `tipAccel.z -= tip_mass`（世界 Z 向下，**符号别搞反**）；
   - 每帧末**投影回半径 `length` 的球面并去掉径向速度**（`HAS_LENGTH_CONSTRAINT`）；
   - 拼矩阵时**必须实现 `lastLeft` 符号修正**（`if dot(left,lastLeft)<0 then left = -left`），这是 Z 字折痕的头号嫌疑；
   - `dt` 要夹在 `[1ms, 1/45s]`，超出就**跳过**（原版行为）。
2. **参数原样传递**。`length` 用作者的，不要用真实骨距；若必须改杆长，按 §3.5 的换算保 `k·L` 与 `tip_mass/L` 不变。
3. **碰撞别当需求**。原版没有；要做就明确标注"HD2 增强"。**先修重力和 `length`，再谈碰撞。** 用户看到的"向上飘"用 §3.4 的稳态式子一眼能定位是符号/坐标系问题（顺带检查 Source +Z 骨骼轴 ↔ Blender +Y ↔ HD2 up 轴的三段转换）。
