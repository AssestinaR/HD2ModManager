# 交接简报：HD2「物理兼容 Lua」的系统化做法

> ⚠️ **历史存档（2026-11-01 归档）**：当前状态与结论以 `docs/physbone-status.md` 为准。
> 本文件保留是为了追溯当时的推理过程与实测数据，**不要当成现行规范**。


> 给专家（GPT-6-ASTRA）的简报。**目的不是修某一条链，而是回答"这套物理兼容 Lua 应该怎么做才对"**。
> 本文件自带全部必要事实，不需要你去仓库里翻找历史。文末 §9 是文件地图，只在需要深挖时再用。

---

## 0. 一句话现状

已经能在 Helldivers 2（Autodesk Stingray 引擎）里，把一个 L4D2 转换过来的双马尾头盔护甲，
用**我们自己插入的 6 根场景图节点** + **一个纯 Lua 弹簧求解器**驱动出可见的、有阻尼的摆动。
求值链第一次完整跑通（2026-10-31 实机确认），但整套东西是**为打通管线而临时拼出来的**，
现在需要一个系统化的设计，才能推广到 57 根骨、多种资产、长期维护。

**用户明确说：双马尾只挂上一根不是重点，重点是"系统性兼容 Lua 制作方式"。**

---

## 1. 目标与验收标准

用户原话的验收标准极其简单，也极其苛刻：

> 不管穿模、不管碰撞。**只要看起来不像钢板就行** —— 要有阻尼，要有自然下垂。

反面案例（用户原话）："头发像钢板 / 袖子像钢板 / 长袍后摆像钢板"。

即：**观感驱动**，不是数值驱动。判据是"走动时滞后、停下回弹一两下、转身时跟不上头、静止完全不动"。

最终形态：`HD2SourceMigration`（C# 转换器）批量转换 L4D2 资产时，
把源模型的 `$jigglebone` 参数自动带下来，运行时由 Lua 兼容层消费 —— 转换即得物理，无需手调。

---

## 2. 硬约束：决定设计的那些现实

### 2.1 引擎与工具链

| 项 | 事实 |
|---|---|
| 引擎 | Autodesk Stingray（Helldivers 2 用的定制版），资源类型哈希已知 |
| 脚本 | Lua 5.1 / LuaJIT，**没有 `string.unpack`**，没有 `math.degrees` |
| 调试 | **没有任何调试器、没有 stack trace 出口、没有 console**。唯一通道：往文件里写日志 |
| 崩溃形态 | 调错引擎函数 = **整进程消失（AV 0xC0000005）**，`pcall` 拦不住，没有 Lua 错误，没有 WER 记录 |
| 迭代成本 | 每次验证要启动游戏 + 进场景，分钟级；崩溃排查曾一轮耗掉数小时 |

### 2.2 Lua 侧可用 API（已 dump，117 个 Unit 函数中相关部分）

- `Unit.num_scene_graph_items / has_node / node(unit, id32) -> 1基下标`
- `Unit.local_pose / local_rotation / local_position / world_position / world_rotation`
- `Unit.set_local_pose / set_local_rotation / set_local_position`
- `World.update_unit(world, unit)` —— 写完本地变换后必须调用，否则画面不刷新
- `Application.string_to_id32(text)` —— 名字 → 节点 id
- 数学库：`Matrix4x4`(40 fn) / `Quaternion`(29 fn) / `Vector3`(42 fn)，均为**引擎表 + 句柄对象**

### 2.3 实测不可用 / 危险的算子（**这是设计的硬边界**）

| 算子 | 实测结果 |
|---|---|
| `Matrix4x4.multiply(a, b)` | **两种参数顺序都产出 `is_valid=false`** 的矩阵 |
| `Quaternion.multiply(引擎句柄, …)` | 得 `inf, -nan, -nan, -nan` |
| `S.Quaternion(0,0,0,1)`（直接调用构造器） | **进程当场死亡**，无错误、无日志 |
| `Matrix4x4.is_valid` | 只查 NaN/inf，**放过 1e14**（曾把头发拉成一根针） |
| 缓存的姿态句柄 | **约 8 秒后失效**，之后 `Matrix4x4.translation` 返回垃圾值，且再也写不进去 |
| 加载期调用引擎函数 | 在场景还没稳定时批量扫描组件 → AV 崩 |

**唯一可靠的构造路径（已验证）**：

```lua
local q    = S.Quaternion.axis_angle(S.Vector3(0, 0, 1), angle)         -- 可靠
local pose = S.Matrix4x4.from_quaternion_position(q, S.Vector3(bx, by, bz)) -- 可靠
```

即：**不能做矩阵合成，只能从零构造**。这是移植 Valve 参考实现时最大的障碍（见 §6）。

另一个关键实测事实：`Matrix4x4.to_elements` 返回 **12 个数 = 列主序 3×4**，
旋转占前 9 个、平移在最后 3 个。（曾按行主序解析，把平移混进旋转 → 行列式=0 → 好矩阵被判坏，整轮排查白费。）

### 2.4 已经排除死的路线（**不要再往这些方向设计**，已有一手取证）

| 路线 | 为什么排除 | 出处 |
|---|---|---|
| **借用引擎原生抖动骨**（社区 wiki 的 6 根：`support_mg / support / attach_intelpad / attach_samplepouch / target_designator / sling`） | 玩家状态机里 `ragdoll_count=6`，**参数烘焙在资源里，Lua 不可写**；不能新建第 7 根；90 骨里这 6 个槽位已全部占用（`cape1..cape8` 属披风单元）；且这 6 根本身是装备挂点，动作模式固定（`sling` 乱动、`target_designator` 只水平动），拿来当长发/袖子没有观感可言 | `docs/hd2-armor-physics-research.md` §3.2、§10.2 |
| `Unit.update_jiggle_bone_data` / `update_jiggle_bone_orientation` 当通用抖动入口 | 只能驱动**状态机里已存在的那 6 根**；函数签名未知（只在识别出的 API 列表里出现过，从未实调） | 同上 §10.1、§11.4 U5 |
| Havok 布娃娃 / 布娃娃约束 | 只在 `can_ragdoll`（倒地/死亡）时激活；Lua **完全没有** `PhysicsWorld / Actor / Joint` 接口 | 同上 §10.3、§10.4 |
| 披风布料（Havok Cloth） | 全库只有 **3 个** `cloth` 资源，不是通用机制；Lua 没有 Cloth 接口；披风是独立 unit + 独立 bones | 同上 §10.3 |
| 纯 Lua 改顶点权重 | `Mesh` 完全没有骨骼/蒙皮接口 ⇒ 权重**只能在离线改资产**时改 | 同上 §10.1 |
| 用 `AnimationBlenderComponent` / `set_animation_bone_mode(ignore)` 让引擎自己算 | 只是"让引擎别覆盖我们的写入"，不产生二级运动 | `docs/hd2-armor-physics-research.md` §11.2 |

**结论：二级运动只能由我们自己算**（离线插节点 + 重写权重 + 运行时 Lua 求解）。
这就是为什么本项目的核心难点在"求解器 + 引擎适配"，而不在"找现成机制"。

### 2.5 矩阵合成约定（实测）

`world = local · parentWorld`（**行向量**约定）。注意：我们自己的 C# 插骨工具里那一处写的是
`inv(parent)·world`（顺序相反）。在"静止姿态下父子都是单位旋转"时不暴露，但推广到带旋转的骨时会错。

---

## 3. 已经跑通的四层架构

```
① 资产层：patch 文件内嵌 HD2PBX01 容器（放源模型的 jiggle 参数 + 骨名映射）
        ↓  运行时读取
② 骨架层：Unit 的 TransformInfo 里插入新节点（jiggle 骨链）+ 扩容骨调色板 + 重写顶点权重
        ↓  场景图节点号
③ 运行时层：physbone_compat.lua —— 建链、推增益、每帧积分、构造姿态、写回引擎
        ↓  日志 / 命令文件
④ 观测层：探针 boot 脚本（替换资产的 boot），把主逻辑包在 pcall 里，每帧打状态；
          命令通道 = 一个文本文件（改参数/重置/释放不需要重启游戏）
```

### 3.1 资产层：`HD2PBX01` 容器

- 布局：header 48 B + 多条记录（名字 + sha256 + 数据）+ footer 56 B（`HD2PBFT1`，在文件尾）
- 已实现（C#）：`HD2SourceModel/PhysBoneContainer.cs`（`Build / AppendTo / StripIfPresent`）
- 内容记录之一：`hd2_spring_rig.bin`，magic `HD2SRG01` v2，**148 B/骨**：
  `boneIndex:u32 | parentBoneIndex:i32 | nameOff:u32 | parentNameOff:u32 | flags:u32 | worldPos:3×f32 | 29×f32 参数`
- 现状：容器**能造、能读、能校验**，但**运行时还没真正从它取参数**（参数目前还是 Lua 常量，见 §5.4）

### 3.2 骨架层：插节点 + 权重重写（已完成并按实测验证）

- 头盔 Unit 从 114 节点 → 120 节点（左右各 3 节 jiggle 链）
- `0x44`（节点在 matrices 数组里的绝对字节偏移）插入时需 **+64**（不是 +136）；`0x48` 含义未知，不动
- 骨调色板 3 → 9 槽；`Remaps[material].FakeIndices[fake] → RealIndices 位置` 这套间接层已摸清
- LOD0 网格 25344 顶点、22 个 section、stride 44（`pos@0 normal@12 uv@16 uv@24 weight@32 index@40`）
- 权重选择用**连通岛**而不是包围盒（包围盒把前发的顶点也圈进去了，导致前发被拉成针）
- 权重按 k/2048 量化，每顶点权重和严格 = 2048

### 3.3 运行时层：`HD2PhysBoneCompat/src/physbone_compat.lua`（1049 行，纯 Lua，无依赖）

对外只有四个入口：`load_rig / build_chains / attach / update / release`（外加 `parse_container`）。
设计意图：**引擎适配（S 层）与算法分离**，`S` 作为参数注入，便于离线测试。

### 3.4 观测层：探针 + 命令通道

- 探针 = 替换资产的 `boot` 脚本，`rawset(_G,"update",…)` 包住 `tick`，全程 pcall
- 分阶段：`stage 0` 只判定就绪 / `1` 只 attach 不驱动 / `2` 完整驱动 —— 用来二分崩溃点
- 命令通道 `%LOCALAPPDATA%\hd2pbx_cmd.txt`：整文件替换语义，探针每 60 帧比对内容变化；
  支持 `mode / maxaccel / smoothing / chainbudget / stage / reset=<ms> / release=<ms> / dump=<ms>`
- **这是本项目最有价值的工程资产之一**：改参数、重置、放开骨头，全程不重启游戏

---

## 4. 现有求解器逐段解剖

### 4.1 参数（来自源模型，29 个 f32，顺序固定）

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
flags 位：`0x01 flexible / 0x02 rigid / 0x04 yawC / 0x08 pitchC / 0x10 angC / 0x20 lenC / 0x40 baseSpring`。
本案样本 `flags=0x31`（flexible | angC | baseSpring）。

Lunalice 双马尾实测样本（两侧三节完全相同）：
`length=30, tipMass=200, yawStiffness=50, yawDamping=7, pitchStiffness=50, pitchDamping=7, angleLimit=0.611`。

### 4.2 建链

`build_chains(bones)`：父不是 jiggle 骨的即为链根，DFS 展开成有序链（父→子）。
节点名直接用源骨名（因为节点是我们自己插的，不存在"源名→HD2名"的解析问题）；
链根另存 `parentName`，运行时映射到 HD2 锚点骨（`head / chest / spine2`）。

### 4.3 增益推导（**这里是最大的"拍"**）

```lua
local MASS_BASE = 300.0   -- 唯一的自由常数，拍出来的
local mass  = math.max(tipMass / MASS_BASE, 0.05)
local omega = math.sqrt(yawStiffness / mass)
local zeta  = yawDamping / (2 * math.sqrt(yawStiffness * mass))  -- 夹到 [0.05, 1.5]
```
依据：源引擎的 29 个参数是**调校增益、不是 SI 单位**，求解器闭源。
这里假设"（刚度, 阻尼, 质量）→ (ωn, ζ)"这个映射是线性的，靠一个自由常数校准。

### 4.4 积分（标准阻尼受迫振子，逐骨两自由度）

```lua
function M.step_spring(spring, dt, ax, ay, az)
  local axes = { ay, az }                      -- ⚠️ 世界 Y→yaw、世界 Z→pitch，**丢掉世界 X**
  for axis = 1, 2 do
    local drive = axes[axis] / spring.segmentLength
    local a = -omega*omega*state[axis] - 2*zeta*omega*velocity[axis] + drive
    velocity[axis] = velocity[axis] + a*dt
    state[axis]    = state[axis]    + velocity[axis]*dt
    -- 触限位：位置夹住，速度 ×-0.25 留一点回弹
  end
end
```
- 段长 `segmentLength` 用**源模型的虚拟杆长**（`length=30` 源单位 ≈ 0.5715 m），不是真实骨距（≈0.14 m）。
  理由：作者刻意把杆拉长以变软；用真实骨距会让弹簧硬一倍以上。
- 三节参数相同 ⇒ **不做逐级衰减**，链式弯曲来自层级本身。
- 单节限位用源值（35°），不按链长摊开（摊开正是"只有根部摆、末梢不动"的成因之一）。
- 整链另有一道 `chainBudget`（≈1.1 rad）兜底。

### 4.5 驱动信号

取**锚点骨（head）的世界位置**，指数低通后做二阶差分得加速度：

```lua
smoothed = smoothed + alpha*(p - smoothed)          -- alpha = positionSmoothing (默认 0.2)
velocity = (p - prev)/dt ;  accel = (velocity - prevVelocity)/dt
-- 跳变检测：一帧位移 > 0.25 m ⇒ 视为不连续，清速度、静默 30 帧
-- 幅值钳位：|accel| ≤ maxAccel（默认 10 m/s²）
```
理由：这是"引擎侧做不到、必须我们自己算"的那一步；没有现成的加速度 API 可用。

### 4.6 姿态构造与写入（**受 §2.3 约束的产物**）

```lua
q    = Quaternion.axis_angle(Vector3(0,0,1), yaw)        -- + pitch 时再乘一个（不可靠，见下）
pose = Matrix4x4.from_quaternion_position(q, Vector3(base.px, base.py, base.pz))
-- 三道检查：is_valid → pose_sane(12 元素有限且不大、旋转部 det∈[0.5,1.5]) → set_local_pose
```
依据：我们插入的 6 个节点**基准 local 旋转是单位阵**（验尸日志：`1 0 0 / 0 1 0 / 0 0 1` + 平移），
所以不需要"基准旋转 × 增量"，只需要"增量 + 基准平移"。
pitch 分量因 `Quaternion.multiply` 不可用而**实际很少生效**（代码里带 pcall 兜底，失败就退化成只有 yaw）。

### 4.7 三层防护

1. 写前：`is_valid` + `pose_sane`（有限、量级、行列式）
2. 写后：**相对锚点**的漂移护栏（`|(末端−锚点)现在 − (末端−锚点)bind| > 1.5 m` ⇒ 停用这条链）
   —— 曾经用绝对世界坐标，人一走就误杀
3. 会话级：连续 N 帧零写入 ⇒ 丢掉会话、回到就绪状态重新挂（应对句柄 8 秒失效）

### 4.8 性能

57 根骨的预算下 0.06–0.09 ms/帧（纯 Lua 积分 + 写姿态）。
每帧还有每链 2 次 `world_position`（驱动 + 护栏），以及一次 `World.update_unit` 全量重算 —— 后者成本未测。

---

## 5. 已知欠帐（逐条，按对"系统性"的威胁度排序）

### 5.1 轴映射是错的（威胁度最高）
`axes = { accelY, accelZ }` —— 世界 X 被丢掉；且"世界 Y 对应骨的 yaw"这个假设只在角色朝向固定时成立。
正确做法：用 `Unit.world_rotation(unit, node)` 把世界加速度投到节点局部系，再分配给 yaw/pitch。

### 5.2 只驱动 LOD0
LOD1..4 的头发顶点权重仍在 `head` 上。走远 → 跳回刚性 → 用户看到的"钢板"复现。
系统性做法需要考虑：是给每个 LOD 都插链（骨数 ×5？），还是让 LOD1+ 直接引用 LOD0 的骨（骨调色板共享？）。

### 5.3 pitch 自由度实际不可用
`Quaternion.multiply` 不可用 ⇒ 无法把 yaw 四元数与 pitch 四元数合成。
需要一条不依赖该算子的合成路径（例如自己用四元数数学在 Lua 里做乘法，再 `from_elements`？`Quaternion.from_elements` 存在）。

### 5.4 参数仍在 Lua 常量
`MASS_BASE / maxAccel / chainBudget / positionSmoothing` 都是硬编码默认值，
容器里已经有真参数却没用上。这是"系统化"的必要一步：**同一份 lua 必须能被上百个资产驱动**。

### 5.5 `quat_from_12` 仍按行主序解析
现在没走这条路（走的是从零构造），但"切纯数字路径"时必须先改。

### 5.6 未实现的源特性
`alongStiffness/alongDamping`（沿轴伸缩）、`yawFriction/pitchFriction`（摩擦）、
`yawBounce/pitchBounce`（回弹系数）、`base*` 一整套（链根基础弹簧）、`min/max` 非对称限位。

### 5.7 矩阵合成顺序隐患
`world = local · parentWorld`（行向量）。C# 插骨工具里有一处写反了，静止态不暴露。

### 5.8 每条链每帧的 `world_position` 调用
带旋转的锚点（比如挂 `chest` 的袖子）会有额外误差；且成本随链数线性增长。

### 5.9 失败模式不够结构化
现在的错误都是"其中一个引擎调用返回 nil"→ 只打一行日志。没有明确的错误分类
（API 缺失 / 句柄过期 / 数值非法 / 节点越界），排查仍要靠人读日志。

### 5.10 与 C# 侧的契约还不闭环
`HD2SourceModel` 能产出 rig，`HD2ModCli` 能产出容器，`HD2SourceMigration` 还没接；
运行时读取容器的那一段虽然在模块里（`parse_container / load_rig`）但**没在实机跑通过**。

---

## 6. 参考物：Valve 的参考实现（`tmp/physbone-recon/jigglebones.cpp`，16961 B）

Source SDK 的 `CJiggleBones` 全程用**矩阵合成**：
`goalBase = 父骨动画矩阵 × 本骨偏移`、累计多个 `goal` 的四元数、再乘回父矩阵。
它还包含：`EnsureBBoxIsInPixels`（把"像素级"参数换算成当前分辨率下的长度）、
`Stiffness / Damping / Friction / Bounce` 的摩擦-碰撞模型、`m_flJiggleAge` 时序、
以及 `along` 轴伸缩。

**本项目最大的移植障碍**：它的核心全靠矩阵/四元数乘法，而这两种算子在我们的引擎里不可用（§2.3）。
所以"照搬"只能是**方程与常数照搬，构造换成从零构造**——这就引出了专家最需要回答的问题之一：
**是继续自研，还是把 Valve 的方程改写成"从零构造"的形式？**

另一个障碍：`EnsureBBoxIsInPixels` 里的"像素→世界长度"换算依赖渲染分辨率，
而我们拿不到这个量（或者说还没找到）。`length` 字段的语义因此不完全确定。

---

## 7. 现有的验证手段（能用的、不能用的）

**能离线跑（不进游戏）**：
- Lua 模块自检：`luajit -e "local M=assert(loadfile(...))(); print(type(M.attach), type(M.update), type(M.release))"`
  —— 这条能挡住"少一个 `end` 导致整段函数被吞"这类灾难（真踩过）
- 用最小引擎 stub 跑逻辑：`tmp/lua-check/attach-loop-test.lua` 已经能用假 `S` 跑通 `attach` 全流程
  （2026-10-31 靠它离线复现了"两条链只挂一条"的 bug：`logger + return` 被写在了 for 循环体内）
- 二进制检查：`tmp/check-jig2.py / check-jig3.py`（权重和、骨调色板、染色一致性）

**不能做**：
- 没有单元测试框架，没有 mock 的引擎行为
- 不能回放录制的运动数据（引擎不给你历史位置）
- 不能热重载 lua（改脚本要重进场景）

**进游戏后的最小判定集**（现在靠人眼 + 日志）：
`applied`（帧计数）/ `written`（实际写入骨数）/ `各节yaw`（逐节偏角）/ `峰值`
/ `链数` / `零写入帧数`。

---

## 8. 需要专家回答的问题

**Q1（架构）** 在"引擎算子残缺 + 无调试器 + 崩溃即进程消失"的环境里，这套运行时应该怎么分层，
才能做到：算法可离线单测、引擎差异可隔离、单个资产出问题不影响其它资产？

**Q2（求解器选型）** 自研阻尼受迫振子（现状）vs 忠实移植 Valve 的 `jigglebones.cpp`（改写成"从零构造"）：
哪条路更可能达到"不像钢板"的观感，且在 29 个闭源调校参数面前更可控？给出理由与判据。

**Q3（参数映射）** 29 个 Source 调校参数 → (ωn, ζ, 驱动增益) 的映射应该怎么设计，
才不需要每换一个资产就重调 `MASS_BASE`？是否存在可自标定（self-calibrating）的做法，
例如用"静止下垂角"或"已知长度"反解？

**Q4（驱动信号）** 用锚点世界位置的二阶差分取加速度是否是正确的输入？
在只有 `world_position / world_rotation` 可用、没有 velocity/accel API 的前提下，
应该怎样做才稳（滤波、参考系、丢轴问题）？

**Q5（合成的替代路径）** `Quaternion.multiply` 与 `Matrix4x4.multiply` 都不可用时，
怎样在这个引擎里做"父骨旋转 × 本骨增量"的合成？可行的候选：
（a）纯 Lua 自己实现四元数/矩阵乘法，只把结果通过 `from_elements` 交给引擎；
（b）只用 `axis_angle` 的复合轴（先算合成旋转轴与角）；
（c）改用 `set_local_rotation` 直接写四元数（如果它能吃 Lua 数字构造的四元数）。
请评估风险与验证顺序。

**Q6（稳定性）** 现在的三层防护（写前数值检查 / 写后相对漂移 / 会话级零写入重挂）够不够？
"句柄 8 秒失效""加载期不能调引擎函数""调用错函数即进程死亡"这类约束下，
一个健壮的运行时状态机应该长什么样？

**Q7（可验证性）** 怎样把"观感"变成可回归的判据？
（例如：录一段真实运动轨迹 → 离线喂给求解器 → 检查角度曲线是否符合"滞后/回弹/不超限"；
或与 Valve 参考实现在同一输入下的输出做对比。）哪些量值得记进日志？

**Q8（推广与收敛）** 从 6 根骨推广到 57 根、从 1 个资产推广到上百个资产、
从 LOD0 推广到 LOD1..4，工程上应该按什么顺序做？哪些可以先砍掉？
最终怎么收敛进 `HD2SourceMigration`（C# 转换器）与 `HD2ModCli`（容器生产）？

---

## 9. 文件地图（需要深挖时再看，不必全读）

| 想了解 | 看这里 |
|---|---|
| **整体交接与踩坑史（2500 行，最全）** | `docs/hd2-physics-roadmap-and-handover.md` §20–§26 |
| 求解器源码（1049 行） | `HD2PhysBoneCompat/src/physbone_compat.lua` |
| Valve 参考实现 | `tmp/physbone-recon/jigglebones.cpp` |
| Source jiggle 机制 / MDL 结构 / L4D2 实测参数 | `docs/l4d2-to-hd2-physbone-migration.md` §2–§3 |
| 引擎原生能力边界（**回答"为什么不借现成机制"**） | `docs/hd2-armor-physics-research.md` §10.1–§10.4 |
| 容器格式与读取方案 | `docs/hd2physbone-embedded-patch-protocol.md` |
| C# 容器/rig 生产 | `HD2SourceModel/PhysBoneContainer.cs`、`HD2SourceModel/SourceModel.cs`（`SourceJiggleBone`） |
| 探针（运行时观测与命令通道） | `tmp/lua-boot/probe.lua`（59419 B） |
| 探针打包 | `tmp/lua-boot/build_probe.py` |
| 离线逻辑测试 | `tmp/lua-check/attach-loop-test.lua` |
| rig 数据 | `tmp/physbone-out/hd2_spring_rig.bin`、`tmp/physbone-export/*.json` |

**环境路径**：仓库 `E:\Data\source\repos\WpfApp1`；游戏数据 `D:\SteamLibrary\steamapps\common\Helldivers 2\data`；
日志 `%LOCALAPPDATA%\hd2pbx_probe.txt`；luajit `C:\Users\Administrator\AppData\Local\Programs\LuaJIT\bin\luajit.exe`。

---

## 10. 希望专家产出什么

1. **一份设计备忘**（直接写进答案或指定文件），明确：分层边界、求解器选型、参数映射方案、驱动信号方案。
2. **一份按优先级排的行动清单**：哪些先做、哪些砍掉、每一步的验收判据是什么。
3. **明确指出你认为不确定的地方**，不要假装确定 —— 本项目被"看起来很确定的错误结论"坑过多次。
4. 如果结论是"现在的自研方向错了"，请直说，并给出替代方案。