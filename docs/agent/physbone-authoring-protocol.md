# PhysBone 参数协议（作者 / 转换器 / 运行时三方契约）

> 目的：让"物理"成为**资产自带的数据**，而不是"为某个模型手调的一堆常数"。
> 现状：求解器里已经**没有**任何逐资产自由常数（`MASS_BASE` 已删除），
> 所有行为都由源模型的 29 个参数 + 资产自身的骨骼几何决定。
> 本文定义这些参数怎么存、怎么解释、怎么校验。

---

## 1. 为什么可以做到"换资产不用重调"

| 求解器需要的量 | 从哪来 | 是否需要人调 |
|---|---|---|
| 刚度 `k`、阻尼 `c` | 源模型 `yawStiffness/yawDamping/pitchStiffness/pitchDamping` | 否（直接读） |
| 重力幅值 `g` | 源模型 `tipMass` × 单位比例 | 否（自动换算） |
| 杆长 `L` | 源模型 `length` × 单位比例 | 否（自动换算） |
| 每节的 bind 偏移 | **运行时从 Unit 的真实世界坐标读** | 否（几何自推导） |
| 每节的初始方向 / 正交基 | 同上，叉乘得到 | 否（几何自推导） |
| 锚点（挂在谁身上） | rig 里记录的父骨名 | 否（转换器写） |
| 单位比例 | 转换器实测（源骨距 vs HD2 骨距） | 否（可自动量） |

**结论：一个资产能不能"转换即得物理"，只取决于三件事**——
① 源模型带 `$jigglebone` 参数；② 转换器把参数与骨名带下来；③ 运行时能找到那些骨。
**没有第四件**（不需要每资产调参、不需要观感调校、不需要人工试）。

---

## 2. 数据模型：每根骨一条记录

字段（顺序固定，`HD2SRG01` v2，148 字节/骨）：

| 偏移 | 类型 | 字段 | 说明 |
|---|---|---|---|
| 0 | u32 | `boneIndex` | 源模型里的骨索引（仅作追溯） |
| 4 | i32 | `parentBoneIndex` | 源父骨索引；用于建链（父不是 jiggle 骨 ⇒ 链根） |
| 8 | u32 | `nameOffset` | 骨名在字符串区的偏移（**运行时用它找节点**） |
| 12 | u32 | `parentNameOffset` | 父骨名（**运行时用它找锚点**，如 `head` / `chest` / `pelvis`） |
| 16 | u32 | `flags` | Valve 的 jiggle flags（见 §3.1） |
| 20 | 3×f32 | `worldPos` | 源 bind 世界坐标（仅作离线校验/追溯，运行时不用） |
| 32 | 29×f32 | `params` | Valve `mstudiojigglebone_t` 的 29 个参数，顺序见 §3.2 |

头 32 字节：`HD2SRG01` magic + 版本 + 骨数 + 字符串区偏移/长度 + 记录长度。

**刻意不存的东西**（不要加）：bind 旋转、bind 偏移、正交基、世界矩阵。
这些量必须由运行时**从插入后的节点当场读**——存下来就会和真实资产漂移，
而且句柄类数据在引擎里活不过 0.1 秒（实测量化见交接文档 §5.0 E6）。

---

## 3. 参数语义（这一节是协议的核心，必须写进作者文档）

### 3.1 `flags`（位）

| 位 | 名字 | 我们是否实现 |
|---|---|---|
| `0x01` | `JIGGLE_IS_FLEXIBLE` | ✅ 主线 |
| `0x02` | `JIGGLE_IS_RIGID` | ⏳ 未实现（当前按 flexible 处理，日志会标注降级） |
| `0x04` | `HAS_YAW_CONSTRAINT` | ⏳ 未实现（非对称限位平面） |
| `0x08` | `HAS_PITCH_CONSTRAINT` | ⏳ 未实现 |
| `0x10` | `HAS_ANGLE_CONSTRAINT` | ✅ 实现（锥面限位，用 `angleLimit`） |
| `0x20` | `HAS_LENGTH_CONSTRAINT` | ✅ 实现（钉在半径 `L` 的球面，且**不加** along 弹簧） |
| `0x40` | `HAS_BASE_SPRING` | ⏳ 未实现（链根二次弹簧） |

L4D2 样本常见值 `0x31 = flexible | angC | lenC`，正好落在"已实现"的三种里。
**未实现的位必须在运行时日志里明确写"降级"**，不许静静当 flexible。

### 3.2 29 个参数（顺序固定）

```
length, tipMass, yawStiffness, yawDamping, pitchStiffness, pitchDamping,
alongStiffness, alongDamping, angleLimit,
minYaw, maxYaw, yawFriction, yawBounce,
minPitch, maxPitch, pitchFriction, pitchBounce,
baseMass, baseStiffness, baseDamping,
baseMinLeft, baseMaxLeft, baseLeftFriction,
baseMinUp, baseMaxUp, baseUpFriction,
baseMinForward, baseMaxForward, baseForwardFriction
```

### 3.3 映射规则（照抄 Valve 方程，不用调）

| 源参数 | 求解器量 | 公式 | 备注 |
|---|---|---|---|
| `yawStiffness` | `ωn_yaw` | `√k` | **无质量除法**（源码 `a = k·e − c·v`） |
| `yawDamping` | `ζ_yaw` | `c / (2√k)` | |
| `pitchStiffness/Damping` | 同上（pitch 通道） | 同上 | 与 yaw **分别**取值 |
| `tipMass` | 重力幅值 `g` | `tipMass × 0.01905` m/s² | **不是质量**（源码 `tipAccel.z -= tipMass`） |
| `length` | 虚拟杆长 `L` | `length × 0.01905` m | 作者常刻意拉长以变软 |
| `angleLimit` | 每节锥面半角 | 直接用（rad） | **相对本节自己的目标方向**，不是相对世界 |
| `along*` | — | 走 `lenC` 分支时**不使用** | 只有 flags 无 `0x20` 才需要 |
| `base*` | — | ⏳ 未实现 | |
| `min/maxYaw/Pitch`、`friction`、`bounce` | — | ⏳ 未实现 | |

**单位常量**：Source 1 单位 = 0.75 英寸 = **0.01905 m**。
`length` 与 `tipMass` 是**两种量**，必须用两个独立比例换算（长度→米、加速度→米/s²）。
2026-10-31 踩过：混用同一个比例导致 `g=200`（应为 3.81），静垂角算成 81.9°，
头发被重力顶死在 35° 限位上、看起来"总是指向正下方"。

### 3.4 观感自带的自检带（不需要人调，但要能判"这片参数合理吗"）

| 指标 | 公式 | 长发典型 |
|---|---|---|
| 固有频率 | `f0 = √k / 2π` | 0.9 – 1.5 Hz |
| 阻尼比 | `ζ = c / (2√k)` | 0.2 – 0.5 |
| 静止下垂角 | `θ = atan(g / (k·L))` | 3° – 15° |

样本（`k=50, c=7, tipMass=200, length=30`）：`f0=1.125 Hz`、`ζ=0.495`、`θ=7.6°`。
**这三个数超带不代表坏了**，只代表作者写得极端；但转换器应当把"出带"记进准入证书。

---

## 4. 存储与注入：用 `HD2PBX01` 容器（推荐）

```
patch 文件（<archiveId>.patch_N）
  └─ 资源 "…/physbone"（或任意约定名）
        └─ HD2PBX01 容器
              ├─ record "spring_rig"  → HD2SRG01 v2（§2 的骨参数表）
              ├─ record "admission"   → JSON/二进制：离线体检结果（可选但推荐）
              └─ record "authoring"   → 作者署名/工具版本/映射版本（可选）
```

**为什么是容器而不是新字段/新资源类型**：

1. 容器**已经在生产**（`HD2SourceModel/PhysBoneContainer.cs`，CLI：`hd2mod source physbone <dir>`），
   不引入第二种真相；
2. 它是"patch 内嵌"的，天然跟着资产走，不需要额外分发文件；
3. 加新记录只需加 `Entry(name, data)`，**向后兼容**（老运行时忽略不认识的记录）；
4. 读取端已实现（`M.parse_container` / `M.find_record` / `M.load_rig`），单元测试齐全。

**版本与兼容**：`HD2SRG01` 版本号 + 记录名双保险。
运行时遇到不认识的版本 → **不驱动**（而不是猜），并落一条结构化日志。

---

## 5. 三方职责

| 角色 | 负责 | 不负责 |
|---|---|---|
| **源模型 / Mod 作者** | 提供 `$jigglebone` 参数（QC 或 MDL） | 不需要懂求解器 |
| **转换器（`HD2SourceMigration`）** | 读参数 → 写 rig → 建容器 → 实测单位比例 → 产准入证书 | 不写任何求解器常数 |
| **运行时（`physbone_compat.lua`）** | 读容器 → 建链 → 从真实节点推几何 → 每帧积分 → 写姿态 | 不含逐资产常数、不存句柄 |

作者想**手写**参数时，只需要产出 §2 那张表（148 B/骨），工具：
`tools/hd2physbone_patch_container.py`（封装/校验/拆分）。

---

## 6. 准入检查（离线，决定"这个资产能不能上线"）

转换时逐资产跑，全部通过才产容器；不通过则**产一个"刚性版"**并写明原因码：

| 检查 | 判据 | 失败原因码 |
|---|---|---|
| 链结构 | 无环、链长 ≤ 8、每链根有父骨名 | `RIG_CYCLE` / `RIG_TOO_LONG` / `RIG_NO_ANCHOR` |
| 参数有限 | 29 个数全是有限值 | `RIG_NONFINITE` |
| 观感带 | `f0/ζ/θ` 落在 §3.4 区间（超带只告警不拒绝） | `RIG_OUT_OF_BAND`（警告） |
| 单位比例 | 各骨比值离散度 < 5% | `UNIT_INCONSISTENT` |
| 骨名可解析 | 每节骨名在目标 Unit 里存在 | `NODE_MISSING` |
| 权重覆盖 | 每个 jiggle 骨在 LOD0 上都有 ≥ N 个顶点 | `WEIGHT_EMPTY` |

运行时侧对应 `ABORT_RIGID` / `QUARANTINE`（见交接文档 §4 的状态机）。

---

## 7. 已知边界（写进文档，别让作者以为是 bug）

1. 碰撞、自碰撞、穿模：**不做**（验收标准外）。
2. `rigid` / 非对称 yaw-pitch 限位 / 摩擦回弹 / `baseSpring` / `along`：**未实现**，
   遇到这些 flags 的资产会在日志里标"降级"。
3. 只驱动**被权重引用**的骨：LOD1..4 的权重需要转换器一并改指（否则走远跳回刚性）。
4. 每帧成本随骨数线性增长：57 骨约 0.06–0.09 ms（纯 Lua），另有每链 2 次引擎读 + 1 次 `update_unit`。
5. 观感最终仍需人眼确认：数值判据能保证"不钢板"，不能保证"好看"。
