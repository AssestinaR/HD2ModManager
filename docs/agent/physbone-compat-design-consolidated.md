# 物理兼容 Lua：三方评审汇总与定稿（2026-10-31）

> ⚠️ **历史存档（2026-11-01 归档）**：当前状态与结论以 `docs/agent/physbone-status.md` 为准。
> 本文件保留是为了追溯当时的推理过程与实测数据，**不要当成现行规范**。


> 三个独立来源评审的交叉验证结果：
> - **A** = 本机默认模型子代理（10 节备忘 + 22 步行动清单）
> - **B** = `aisz/gpt-6-astra`（10 节备忘，偏保守，强调可验证性与准入证书）
> - **C** = `openwrt/global:gpt-6-astra`（**逐条交叉验证**，带源码行号，抓到 3 条可离线证伪的硬缺陷）
>
> 本文只保留**三方一致 + 已被我方离线复现证实**的结论。分歧处给出裁定和证据。
> 输入简报：`docs/agent/agent-brief-physbone-compat-design.md`

---

## 0. 三方共识（不需要再讨论）

| # | 共识 | 三方 |
|---|---|---|
| S1 | **四层架构（资产/骨架/运行时/观测）+ 容器 + 探针 + 命令通道全部保留**，这是本项目最有价值的部分 | A B C |
| S2 | **换掉求解器内核**：独立 1-DOF 角振子 + 锚点位置二阶差分 + 手调 `MASS_BASE` 是结构性错误，不是参数没调好 | A B C |
| S3 | **移植 Valve 的方程，不移植它的矩阵代码**；`tipMass` 在源码里只用于重力，**不是惯性质量** | A B C |
| S4 | **旋转合成放纯 Lua 数字空间**，引擎边界只保留 `axis_angle` → `from_quaternion_position` → `set_local_pose` → `update_unit` | A B C |
| S5 | **绝不跨帧保存引擎句柄**；`release` 现在用缓存句柄写回是真 bug | A B C |
| S6 | **驱动信号改成"目标尖端滞后"**（goalTip = 锚点位姿 + bind 偏移），二阶差分整条链路删掉 | A B C |
| S7 | **但跳变/长帧/停更的重置与 dt 夹必须保留**（C 修正了 A 的"全部删除"） | B C |
| S8 | **离线可测的部分要真的离线测**：语法/纯数字/假引擎/故障注入/回放，四级 | A B C |
| S9 | **风险挡在离线（准入证书）比运行时容错可靠一个数量级**，因为 AV 拦不住 | A B C |

---

## 1. 我已离线复现的 5 个硬缺陷（不是意见，是事实）

| # | 缺陷 | 位置 | 复现/证据 | 后果 |
|---|---|---|---|---|
| **D1** | **漂移护栏从未执行过** | `physbone_compat.lua` L972-974 | `local tx,ty,tz = pcall(f)` ⇒ `tx` 是 **boolean**，`tz` 直接丢值。`type(tx)=="number"` 恒为 false | "防头发拉成针"的最后一道闸是**死代码**。§26 里那条护栏一直不存在 |
| **D2** | **`0x31` 被解释错了** | 简报 §4.1、历史文档 | `studio.h` L157-163：`0x31 = flexible(0x01) \| angC(0x10) \| lenC(0x20)`，**不含 baseSpring(0x40)** | 后续移植会走错分支：**该走定长约束分支**（无 along 弹簧），且**不进 yaw/pitch 平面限位分支** |
| **D3** | **`params.length` 漏了单位换算** | L439-442 | `a or (b * scale)` 的优先级 ⇒ 有 `params.length` 时 `scale` 被绕过。实测 `segmentLength=30`（应为 0.5715） | 接容器路径后驱动小 52 倍；现在还"能动"是因为**探针在别处预先换成了米**（掩盖） |
| **D4** | **`pose_sane` 只查行列式，不查正交性** | L583-600 | `diag(2,0.5,1)` 能通过（det=1，但含非均匀缩放） | 非刚体矩阵可被写进节点 |
| **D5** | **`M.release` 优先用 attach 时缓存的 pose 句柄** | L1025-1029 | 与同文件 L521 自己写的"句柄约 8 秒失效"自相矛盾；且不走任何校验 | 中后期 release 会把坏矩阵写进节点（头发变针），且**数字重建退路几乎永远走不到** |

附带（非缺陷，但影响判据）：`MASS_BASE=300` 的实际数值偏差已算清 ——
样本 `k=50, c=7, tipMass=200`：现有实现 `ωn=8.66, ζ=0.606` vs Valve 的 `ωn=7.07, ζ=0.495`。

---

## 2. 关键分歧与裁定

### 分歧 1：`ωn = √k` 能不能直接当结论？

- A 说"不需要任何自由常数"；C 说"只在固定目标系、无活跃约束、线性区间成立"。
- **裁定**：以 C 为准，但结论不变。源码事实：`yawAccel = k·e − c·v`（**无质量除法**，L111/L114），
  故单轴线性模 `ωn=√k, ζ=c/(2√k)`。**但**：rigid 分支、长度投影、重力、限位生效时观测频率会偏，
  不能拿它当"最终骨角频率"的精确预测 —— 只能当**映射的起点**，再用实测 ringdown 校准（见 §4 L3）。
- 实践含义：**这不是"理论不严谨"，而是"参数映射有了唯一可证伪的起点"**，比现在的 `MASS_BASE` 强得多。

### 分歧 2：能不能把 `maxAccel / positionSmoothing / chainBudget` 全删？

- A 说全删；C 指出 Valve 自己也有 `dt` 钳位 `[0.001, 0.0333]` 与 `>0.5 s 重置`，且目标瞬移 100 m 一样会炸。
- **裁定**：**删"补丁"，留"护栏"**。
  - 删：`MASS_BASE`、`maxAccel`（二阶差分专用）、`positionSmoothing`（二阶差分专用）、
    `chainBudget`（限位改相对 goal 后不需要）、逐级衰减、虚拟杆长。
  - 留并加强：`dt` 夹 + 子步、瞬移 snap、停更重置、有限性/正交性检查、预算。

### 分歧 3：`axis_angle` 是否支持任意轴？

- A 说"支持"（85% 置信）；C 说"数学上可行，但一手证据只覆盖基轴"。
- **裁定**：**当作未验证**。这是整条合成路线的地基，必须用 E3 实验定死（§5）。
  失败则退到"复合轴近似"（小角下 `axis ≈ (θy·up + θx·left)/|·|`，>20° 有误差）。

### 分歧 4：`to_elements` 是行主序还是列主序？

- 现有代码两处自相矛盾（`quat_from_12` 行主序 vs `pose_sane` 列主序）。
- 两者引用的"证据"（单位旋转 `1 0 0 / 0 1 0 / 0 0 1`）**对转置不变，数学上无法区分**。
- **裁定**：**当前是未定状态**，必须 E2 定死。这是三方公认最危险的"看起来确定的错误结论"。
  附带发现：`quat_from_12` 还有**步长错位**（取 1,2,3 / 5,6,7 / 9,10,11），对单位阵会解出
  `(0, −0.408, 0.408, 0.816)` —— 连单位旋转都算错，说明它**至少一处肯定错**。

### 分歧 5：Valve 参考文件是不是 L4D2 的真值？

- C 指出：该文件来自 `HL2-Ghosting-Team/src`，含 dummy overlay；且**不含** `EnsureBBoxIsInPixels` /
  `m_flJiggleAge`（我简报 §6 的表述来自早期侦察笔记，**是错的，已作废**）。
- **裁定**：它是**方程真值**，不是**版本真值**。所以判据是"移植对了"（与 C++ 直译对拍），
  不是"和 L4D2 逐位一致"。

---

## 3. Valve 方程：移植时必须照做的 7 条源码事实

按我们样本 `flags=0x31`（flexible + angleConstraint + **lengthConstraint**，**无** yaw/pitch 平面限位、
**无** along、**无** baseSpring）走的分支：

| # | 事实 | 源码 | 对我们的含义 |
|---|---|---|---|
| **F1** | 弹簧作用在**尖端质点的世界位置**上 | L99-130 | 状态是 `tipPos/tipVel`（世界向量），不是角度 |
| **F2** | **没有质量除法**：`a = k·e − c·v` | L111/L114 | `ωn=√k, ζ=c/(2√k)`；不需要 `MASS_BASE` |
| **F3** | `tipMass` **只**用于重力：`tipAccel.z -= tipMass` | L94 | 静止下垂角 `θ ≈ tipMass/(k·length)`（样本 7.6°，正好"自然下垂"） |
| **F4** | 误差与速度都**投影到 goal 基**（`goalLeft/Up/Forward`） | L102-124 | 天然 3D、天然随朝向旋转 ⇒ 现实现"丢世界 X 轴"的问题**结构性消失** |
| **F5** | 阻尼用 **绝对** `tipVel`（不是相对目标速度） | L107-108 | **匀速走动会产生持续滞后** —— 这正是"起步拖后"的来源，不是副作用，别"修正"掉 |
| **F6** | 积分是**半隐式** Euler：先更新速度再用新速度更新位置 | L136-137 | 注释写 "simple euler" 是误导；照 F6 写 |
| **F7** | `lenC` 命中 ⇒ **不加 along 弹簧**，只 `tipAccel += yawAccel·goalLeft + pitchAccel·goalUp` | L118-130 | 我们的样本走这条 ⇒ `alongStiffness/alongDamping` 可以**直接砍掉** |

另外三条必须照抄的时序：**积分 → 清本步加速度 → yaw → pitch → angle → length → 由 forward 重建姿态 → base spring**；
`dt` 钳 `[0.001, 0.0333]`（低帧率会慢动作，注释明确说了）；`>0.5 s` 未更新则 `Init`。

**姿态重建不需要任何矩阵乘法**（L391-408）：`forward = normalize(tipPos − goalBase)`，
`left = normalize(cross(goalUp, forward))`，`up = cross(forward, left)`。

---

## 4. 定稿架构（收敛后）

```
L0 离线（C# 转换器）
  .mdl 29 参数 + 骨层级/骨距 + 目标 HD2 骨架
    → compat_spec（29 f32 原样 + flags）
    → bind_geometry（每节 bind 局部平移、锚点偏移、实测 unitScale）
    → derived（ωn/ζ/g/limit + mappingVersion）
    → admission（准入证书：骨数/链单调/limit≤π/2/ωn∈[2,20]/ζ∈[0.1,0.8]/unitScale 离散<5%/锚点存在/无环）
    → HD2PBX01 容器（schema 版本 + ABI gate）

L1 参数平面（纯数字，无引擎）
L2 求解器（纯函数 step_chain(state, goal, dt) → dirs[]，零 S / 零 io / 零 os.clock）
L3 引擎适配（唯一危险区，恰好 5 个原语）
     node_of / read_world_pos / read_world_quat / write_local_rot / flush
     + CapabilityProfile（一张表决定上轴、下标基、布局、授权构造路径、禁用算子）
L4 驱动时机（状态机 + 预算 + 看护）
     BOOT → WAIT_UNIT → WAIT_STABLE → SNAPSHOT → BIND_VERIFY → RUN
          →(DEGRADED | RESYNC | QUARANTINE | ABORT_RIGID)
L5 观测与自愈（probe.log 人读 / trace.csv 机读 / cmd.txt 热调 / breadcrumb 面包屑）
```

**三条不可协商的分层规则**（这不是风格，是崩溃约束的推论）：

1. **L1/L2 里出现 `S.` 就是 bug** —— 危险面必须可枚举，才能"数得清会不会崩"。
2. **L3 只走 CapabilityProfile 授权过的路径** —— 引擎更新只改这张表 + 重跑一致性电池，求解器一行不动。
3. **L5 永不反向写 L2~L4 的状态** —— 否则"日志撒谎"和"观测改变行为"会同时发生。

**隔离的优先级**（前两条是主力，后三条是兜底）：
离线准入证书 > 面包屑 + 崩溃循环自检（自动 quarantine）> 单链降级 > 帧预算降档 > 无共享可变状态。

---

## 5. 必须先做的 6 个探针（每个 1 次重启，全部有数值判据）

**这 6 个数决定上面所有设计是"被证据确立"还是"被注释声称"。**

### 5.0 实测结果（2026-10-31 18:37，实机，**八个实验全绿**）

| # | 结论 | 证据 |
|---|---|---|
| **E0** | **24 个原语全部存在**（`Unit.world_rotation` / `Quaternion.to_elements` / `Quaternion.from_elements` / `Matrix4x4.from_elements` / `Unit.set_local_rotation` / `Unit.world_pose` / `Quaternion.rotate` 一个不缺） | `present=24/24` |
| **E2** | **`to_elements` = packed9（旋转 1..9 + 平移 10..12），且旋转是「转置」**：把这 9 个数当行主序读得到 Rᵀ | 平移 `packed9=1.3e-08` vs `per_row=5.0e-01`；旋转 `原样=6.8e-01` vs `转置=1.1e-08` |
| E2 附带 | **`Matrix4x4.rotation(pose)` → `to_elements` 不可用**（两种解释误差都是 1.0 —— 它返回的不是 3×3 旋转矩阵） | `原样=1.00 转置=1.00` |
| **E3** | **`axis_angle` 支持任意轴，但必须传单位轴**：单位斜轴 err=`4.2e-08`；同一方向非单位轴 err=`6.0e-01` ⇒ **引擎不会自己归一化** | `单位斜轴=4.16e-08 / 非单位斜轴=6.05e-01 / 基轴=1.46e-08` |
| **E5b** | **`Quaternion.to_elements` = x,y,z,w**，范数 1 | `0, 0, 0.173648, 0.984808`，`|q|=1.000000` |
| **E1** | **上轴 = +Z** | `head−hips` Δ=`(0.001, 0.032, 0.716)`，`|Δ|=0.717 m` |
| **E4** | `update_unit` 每帧一次 × 60：avg/min/max 全是 `0.000 ms` —— **但这是"什么都没写"时的数字**，很可能是脏标记短路 | `avg=0.000`（见下方注意事项） |
| **E5** | **`world_rotation` 拿到的是动画值**（我们没写任何东西，它自己变了） | `Δq=1.28`，同期位置变化 `4.87 m`（玩家在走动） |
| **E6** | **缓存句柄基本不可用**：与"当场重读"在 **0.10 s** 就分叉，且差值 `inf` ⇒ 句柄是**临时对象**，不是"8 秒后失效" | `首次分叉 frame=11205 ≈ 0.10 s 差值=inf` |
| E7 | **`Unit.bones(护甲件)` 会打死游戏**（卡住约 3 秒后崩溃，pcall 拦不住 AV）⇒ 已改为仅 cmd `rescan=1` 手动触发；骨名改离线读补丁 | 日志停在 E7 首行之前 |

**由实测直接推出的实现铁律**：

1. **重力方向 = `-Z`**（不要再假设 Y-up）。
2. **旋转构造必须先把轴归一化**（引擎不替你归一化）——这条不写，斜轴姿态会静默错 0.6 rad。
3. **任何引擎句柄（pose / quaternion / vector）必须当场转成数字**，连 0.1 秒都不能留。
   `M.release` 用缓存 `base.pose` 写回、`snapshot_pose` 存 `snap.rot` 都是**必删**的路径。
4. **`quat_from_12` 两处都错**（打包方式 + 转置），删；`snapshot_pose` 里经 `Matrix4x4.rotation` 反解四元数那条路也删（那条路本身返回的不是 3×3）。
5. **`pose_sane` 一直是对的**：它取 `e[1..9]` 当 3×3 算行列式，而行列式对转置不变 —— 纯属侥幸，但要保留这个写法并加注释说明为什么侥幸也对。
6. **E4 的 0.000 ms 不能当"flush 免费"**：那是没写任何姿态时的数字（引擎很可能整帧短路）。必须**在真实写入之后**重量，否则会得出"可以每帧多次 flush"的错误结论。


| # | 实验 | 判据 | 失败则 |
|---|---|---|---|
| **E1** | 上轴：打 `head` / `hips` 的 `world_position` | Δ 主分量 \|Δ\|∈[0.5,0.9] m ⇒ 定 `gravityAxis` | 重力方向错 90°，头发往侧面垂 |
| **E2** | **`to_elements` 布局**：`axis_angle(up,20°)` → 打 12 个数 vs 手算 3×3 逐元素 | 非转置误差 <1e-4 者胜 ⇒ **定死布局**，改正/删除 `quat_from_12` | 解出共轭四元数 = 反着转 |
| **E3** | `axis_angle` **任意轴**：`normalize(1,2,3)`、0.4 rad vs 手算 Rodrigues | 逐元素 <1e-4 且无 AV | 退复合轴近似（>20° 有误差） |
| **E4** | `World.update_unit` 耗时（`os.clock` 包住，3 资产/57 骨） | 单次 <2 ms、60 fps 不掉帧 | 只能每 N 帧 flush 一次 |
| **E5** | `Quaternion.to_elements`（读锚点旋转用）是否存在/顺序 | 写 10° 后读 anchor 世界旋转应**不含**这 10°（确认拿到的是动画值） | Tier B 拿不到 → 只做 Tier A |
| **E6** | 句柄寿命：缓存 pose 句柄每 60 帧读 `translation` | 记录第几帧开始偏离 >1e-3（预期 ~8 s） | 证明 D5 的严重性 |

**Tier 切分**（让"至少能交付"和"最好"解耦）：

- **Tier A**：只读 `world_position`（**已验证**）⇒ 3D 驱动 + 重力下垂 + 正确 ζ，修掉 D1(丢轴)/噪声放大；
  **代价：转身/转头的转动惯性拿不到**。
- **Tier B**：加 `read_world_quat` ⇒ 转动惯性（"转身时跟不上头"这条验收才真正满足）。

---

## 6. 参数映射（定稿）

| 源参数 | 目标量 | 映射 |
|---|---|---|
| `yawStiffness` | `ωn_yaw = √k_y` | 直接 |
| `yawDamping` | `ζ_yaw = c_y/(2√k_y)` | 直接 |
| `pitchStiffness` / `pitchDamping` | 同上（**现实现完全忽略 pitch**，源里两者常不同） | 直接 |
| `tipMass` | 重力加速度幅值 `g = tipMass · u`（**不是质量**） | 线性 |
| `length` | 虚拟杆长 `L` | **优先用实测骨距**；源字段仅校验（覆盖 D3） |
| `angleLimit` | 单骨相对**自己 goal** 的最大偏离角 | 直接（rad），不再需要 `chainBudget` |
| `along*` | — | **砍掉**（样本有 `lenC`，Valve 不走 along 分支） |
| `base*`（12 个） | 链根基础弹簧 | **砍掉**（样本无 `baseSpring`；tip 侧已有重力下垂） |
| `min/maxYaw/Pitch`、`friction`、`bounce` | 限位/摩擦/回弹 | 样本 flags 无 yawC/pitchC ⇒ **不进该分支**，暂不实现 |

**自标定的三级**（这是"不必每个资产手调"的答案）：

- **L1 几何自标定（离线，必做）**：`u = |骨距_HD2 m| / |骨距_源 unit|`，
  各骨比值离散度 <5% 才算合格；同时直接得到米制的 `L`，**不信源 `length` 字段**。
- **L2 静垂校验**：预测 `θ = tipMass/(k·length)`，与实测静止下垂角比 ±20%。
- **L3 脉冲响应辨识（在线，命令通道）**：发一次激励 → 记 600 帧 `θ(t)` → 零穿越间隔得 `f0_meas`、
  对数衰减率得 `ζ_meas`，与 `(√k/2π, c/2√k)` 比：
  - 各资产比值一致（σ/μ<10%）→ 全局修正系数写进 profile，**永不手调**；
  - 不一致（差 >30%）→ **映射故事作废**，说明这批参数不是这个版本 `jigglebones.cpp` 生成的。

---

## 7. 观感 → 可回归判据（用户那几句原话的翻译）

| 用户原话 | 可测判据 | 阈值 |
|---|---|---|
| "走动时滞后" | 1.5 Hz 输入下 θ 的相位滞后 | 20°–50° |
| "停下回弹一两下" | 过冲次数 / 5% 稳定时间 | 1–3 次 / 0.5–1.2 s |
| "转身时跟不上头" | 世界 yaw 180°/0.5s 时 tip 相对 goal 的峰值角滞后 | ≥15°（**需 Tier B**） |
| "静止完全不动" | 静止 3 s 内 θ 漂移 / 抖动 RMS | <0.5° / <0.15° |
| **"不像钢板"** | **G4 活度**：120 帧窗口内 `max|θ|>0.5°` 至少一次 | 否则 WARN `solver alive but frozen` |
| "有自然下垂" | 静止下垂角 θ_rest | 3°–15°，且 = `tipMass/(k·L)` ±20% |
| 不"贴死限位" | 触顶帧占比 | <15% |
| 不"甩针" | `|θ|max ≤ limit×1.02` 且 NaN=0 | — |
| 不"消失" | 零写入帧占比 | <0.5% |

**G4 活度检测是最重要的一条新闸**：
"钢板" = 数值全合法、写入全成功、角度恒为 0 —— 现有三道闸（写前/写后/零写入）**全都抓不到**。
必须新增结构断言：bind 旋转≈单位、`|tip−base| ≈ L×1.05`、活度、gains 与观感带的偏离。

---

## 8. 落地顺序（每步的"前置"是上一步的验收）

| 阶段 | 内容 | 验收 |
|---|---|---|
| **P0 取证** | E1–E6 六个探针 | 六个数值判据全部落表 |
| **P1 封危险口** | 修 D1（护栏）、D5（release 句柄）、D4（正交性）、D3（单位）；删 L931 的 `Quaternion.multiply`；所有写入口统一走数字重建 + 同一套校验 | 超过 8 秒后 release 不回写坏矩阵；非法输入零写入且有原因码 |
| **P2 内核** | 换成尖端点簧（Tier A）+ 单一 `axis_angle` 摆动；删 `MASS_BASE/maxAccel/positionSmoothing/chainBudget`；加 G4 | 日志 `f0=1.125 Hz ζ=0.495`（样本）；**pitch 第一次真正生效**；重力下垂可见；L1 解析对拍 <3% |
| **P3 数据契约** | 参数从容器读；补 `bind_geometry` + `unitScale` 实测 + 准入证书；rig 补 flags 与 rest 朝向 | 改容器参数不需改 lua；`unitScale` 离散 <5%；样本正确解码为 `flexible+angC+lenC` |
| **P4 Tier B** | 读锚点四元数 → 转动惯性 | "转身滞后 ≥15°"达标 |
| **P5 规模化** | 3 资产（头盔/袖子/披风，不同锚点、不同 bind 旋转）+ 57 骨压测 | 总帧成本 <6 ms；**人为让资产 B 失败，A/C 的 trace 逐帧不变** |
| **P6 LOD** | LOD1..4 权重重指**同一批节点**（节点是 Unit 级，不需要 ×5 插骨）；调色板容量先离线验 | 走远到 LOD3 仍在摆 |
| **P7 C# 闭环** | `HD2SourceMigration` 出容器 → `HD2ModCli` 校验 → 运行时 ABI gate | 转换一个**没见过的** L4D2 资产，零手调能跑 |

**先砍掉的**：碰撞/穿模、Havok/原生六槽、Lua 动态改权重、`along*`、`base*`、非对称限位/摩擦/回弹、
逐资产 `MASS_BASE`、"与 Valve 逐位一致"的说法。

**LOD 退路（先写进设计再动手）**：若 LOD1..4 调色板无法扩容，则**距离阈值冻结尾帧姿态**
（"走远了只是不摆了"），而不是"跳回钢板"。

---

## 8.5 已知缺陷（**待修，不阻塞当前测试**，2026-10-31 用户实测）

| # | 现象 | 用户原话 / 复现 | 严重度 | 我的初判与方向 |
|---|---|---|---|---|
| **K1** | **切换护甲模型会让游戏卡住并崩溃**（在军械库切换头盔即触发） | "物理系统无法容忍玩家切换模型……切换头盔，此时游戏立刻就会卡住并崩溃" | **高**（正常游玩必经路径） | 最可能：会话里缓存了**旧 Unit 的引用**（`session.unit`）与节点下标，换装时旧 Unit 被销毁/替换，此后每帧对**失效 Unit** 调 `world_position` / `set_local_pose` → 原生 AV。方向：①每帧校验 Unit 身份（`debug_name`/id）与 `num_scene_graph_items`，一变就 `release` + 丢弃会话；②监听换装/场景切换事件主动停；③军械库/菜单阶段干脆不驱动（只在"场景稳定 + 有 unit"时跑，已有就绪闸可复用）。**注意**：这类问题 pcall 拦不住，必须靠"事前判断"而不是"事后捕获"。 |
| **K2** | 怀疑**切换地图**同样会崩 | 同上（未单独验证） | 高 | 与 K1 同源（Unit 生命周期），一并修。 |
| **K3** | 怀疑**进游戏时没找到对应头盔**也会崩 | 同上（未单独验证） | 中 | 与 K1 同源：锚点/节点解析失败时是否仍在写？应统一走 `ABORT_RIGID`。 |

修这三条时用现成工具：`hd2pbx_boot.txt` 面包屑 + minidump 解析脚本（`tmp/lua-check/dump-digest.py` / `dump-stack.py`）。
**当前决定：先不动**，按既定顺序把"整批转换 + 57 骨观感"验完，再专门开一轮修 K1–K3。

| # | 现象 | 根因 | 状态 |
|---|---|---|---|
| **K4** | **纯材质 VPK 转换全部失败**：`Value cannot be null. (Parameter 'info')` | 我在 `VpkPatchAssembler` 里"无条件追加合成骨"时踩到了 `avatarTransformInfo` 允许为 null 的纯材质路径（`plans.Count == 0` 时不校验它）。`UnitTransformInfoAppender` 现已改成 **null 进 null 出** 并加了回归测试 | ✅ 已修（2026-10-31） |




---

## 9. 不确定性台账（三方合并，按危险度排序）

| # | 不确定 | 置信 | 错了会怎样 | 怎么定死 |
|---|---|---|---|---|
| **U1** | `to_elements` 行/列主序 | **50/50** | `quat_from_12` 解出共轭 ⇒ **反着转**；最容易被误诊成"轴映射错了" | **E2** |
| U2 | `axis_angle` 任意轴 | 85% | 整条合成路线失效 | **E3** |
| U3 | Stingray 上轴 Y-up 还是 Z-up | 不确定 | 重力偏 90°，头发往侧面垂 | **E1** |
| U4 | 29 参数是否由**这个版本**的 cpp 生成 | 75% | "√k 直接可用"的故事作废 | §6 L3 脉冲辨识 |
| U5 | L4D2 各版本的 flags 语义是否一致 | 中 | 走错分支（如误信 baseSpring） | 多资产 flags 统计 |
| U6 | 锚点 `world_rotation` 是动画值还是我们写入值的反馈 | 80% | 正反馈自激 | **E5** |
| U7 | 袖子/披风的 bind 旋转是否仍是单位阵 | 未知 | §5 的合成推导失效 | SNAPSHOT 断言 + 3 资产实测 |
| U8 | 各 LOD 调色板能否扩容 | 未知 | P6 走不通 | 离线二进制检查 |
| U9 | `update_unit` 真实耗时 | 完全没测 | 唯一瓶颈 | **E4** |
| U10 | Valve 限位分支读未赋值的 `limitVel.x`（源码自身缺陷） | 已确认 | 该分支不能当黄金结果 | 记录差异，不照抄 |

---

## 10. 一致性电池：已落地，怎么跑

代码：`tmp/lua-boot/probe_conform.lua`（`conf-1.0.0`，40700 字节，**默认纯只读**）
打包：`python tmp/lua-boot/build_probe.py --probe tmp/lua-boot/probe_conform.lua --no-compat --patch-index 15`
离线干跑：`tmp/lua-check/conform-stub-test.lua`（假引擎跑满 3200 帧，验证判据方向，三种模拟布局都对）

**时间线**（60 fps 计，进场景后）：

| 帧 | 事件 |
|---|---|
| 600 | 第一次采样（**加载期到此为止不调任何引擎函数**） |
| 900 | 就绪（两次采样相距 300 帧、位移 <0.30 m；连续 3 次不稳定则带警告放行） |
| 900–933 | E0 E2 E3 E5b E1 E7 E4 依次各占一帧 |
| ~930 | E5 开始观察 15 秒 —— **这 15 秒请走动 / 转身 / 转头** |
| ~933 | E6 开始观察 30 秒（不用管它） |
| ~2830 | 打印 `==== 一致性电池结果 ====` 汇总表 |

**控制通道**（`%LOCALAPPDATA%\hd2pbx_cmd.txt`，每 60 帧读、内容变化才生效）：

```powershell
pwsh tmp/set-cmd.ps1 -Pairs "rescan=1"                      # 重新做 E7 骨名普查
pwsh tmp/set-cmd.ps1 -Pairs "unitdbg=9ba626afa44a3aa3"      # 指定护甲件（Unit.debug_name 里的 #ID[..]）
pwsh tmp/set-cmd.ps1 -Pairs "unitindex=2108"                # 或直接给 unit 下标
pwsh tmp/set-cmd.ps1 -Pairs "bonename=jiggle_L1;write=1"    # 写入闭环测试（写完立刻按单位旋转写回）
pwsh tmp/set-cmd.ps1                                        # 清空
```

**判读要点**：

- `E2` 会直接给出 `打包=packed9|per_row` + `旋转=原样|转置`，以及 12 个原始数字 —— 从此 `quat_from_12` 该改成什么、`pose_sane` 该取哪 9 个，不再有争议。
- `E1` 给出 `up=+Y|+Z`，这直接决定重力往哪加。
- `E3` 若 FAIL ⇒ 姿态合成退到"复合轴近似"，P2 的写法要改。
- `E4` 若 >2 ms ⇒ 不能每帧 flush，需要改成每 N 帧。
- `E5` 若 FAIL（四元数没变化）⇒ 先确认自己真的走动/转身了；确实动了还不变 ⇒ Tier B（转动惯性）做不了。
- `E6` 期望是**几十秒后失效**；如果 30 秒都没分叉，说明"不能缓存句柄"这条可能是错的（对我们是好消息）。
- `E7` 列出护甲件里 `jig/hair/tail/source_extra` 类骨名 + 节点号 —— 这是 P2 里 attach 要用的名字。



## 11. P1 已完成（2026-10-31 18:45，离线验证，未进游戏）

改动全在 `HD2PhysBoneCompat/src/physbone_compat.lua`，三处都是"删掉错误路径 + 修正一处优先级 bug"，
没有引入任何新机制。

| # | 改了什么 | 依据 | 回归测试 |
|---|---|---|---|
| **P1-a** | **删掉两条句柄路径**：`snapshot_pose` 里经 `Matrix4x4.rotation` 反解四元数、`attach` 里存 `snap.pose` / `snap.rot`、`release` 优先用缓存句柄写回。改为：`snapshot_pose` 只存 **12 个数**（packed9 原样），`release` 用 `stored9_to_axis_angle` + `axis_angle` + `from_quaternion_position` 重建 | 一致性电池 **E6**：缓存句柄 0.10 s 就变 `inf`；**E2 附**：`Matrix4x4.rotation()` 这条路返回的不是 3×3 | `release 写回 3 根骨` / `release 实际调用了 set_local_pose` / **`release 不依赖 Quaternion.from_elements`** |
| **P1-b** | **修好从未执行过的漂移护栏**：`local tx,ty,tz = pcall(...)` → `local ok,tx,ty,tz = pcall(...)`，判断加 `ok` | 离线复现：`tx` 拿到的是成功布尔，`type(tx)=="number"` 恒假 | **`漂移 2 m 时护栏触发`**（日志：`[护栏] 相对锚点偏离 2.00 m，这条链已被停用`）+ `稳定状态不误报` |
| **P1-c** | **修 `params.length` 漏换算**：`(a) or (b)*scale` → `((a) or (b)) * scale` | 实测旧版 `segmentLength=30`（应 0.5715），驱动小 52 倍 | `segmentLength=0.5715` |
| **P1-d** | 删除 `quat_from_12` / `make_quat`（两处假设都错，且已无调用方） | E2 | 全模块 `rg` 无残留引用 |

回归测试：`tmp/lua-check/p1-regression-test.lua`（8 项断言全绿；含"故意把末节推出去 2 m"
和"故意把 `Quaternion.from_elements` 换成会报警的探针"两个负向用例）。

**P1 之后仍然欠着的（P2 的范围）**：求解器内核还是"两轴角振子 + 锚点位置二阶差分 + `MASS_BASE`"，
即定稿 §2 判定的结构性错误。P2 要把它换成 Valve 的尖端点簧（Tier A：只用已验证的 `world_position`），
并按 §5.0 的铁律：`ωn=√k`、`ζ=c/(2√k)`、重力 `-Z`、姿态用**归一化轴**的单次 `axis_angle` 构造。


## 12. P2 已完成（2026-10-31 18:50，数值回归全绿，待实机观感确认）

内核换成 **Valve 尖端点簧**。`physbone_compat.lua` 的结构变化：

| 项 | 旧（角度振子） | 新（尖端核） |
|---|---|---|
| 状态 | 每骨两个解耦角（yaw/pitch） | 每骨一个**世界尖端位置 + 速度** |
| 驱动 | 锚点世界位置二阶差分 + 低通 + 钳位 | **goalTip 与 tipPos 的差**（不需要任何加速度输入） |
| 增益 | `MASS_BASE=300` 当惯性质量 | `ωn=√k`、`ζ=c/(2√k)`，**零自由常数** |
| pitch | 靠 `Quaternion.multiply`，实际失效 | `localError.y` 一路到底，**天然生效** |
| 重力 | 无 | `-Z × (tipMass×unitScale)` ⇒ 静垂角 `atan(g/(kL))` |
| 转身惯性 | **完全没有**（这是"像钢板"的主因之一） | 有（锚点旋转把 bind 偏移与切向轴一起带走） |
| 姿态 | `axis_angle(z,yaw)` | **归一化轴**的 `axis_angle` + `from_quaternion_position` |
| 补丁常数 | `MASS_BASE / maxAccel / positionSmoothing / chainBudget` | 全删（只剩 `jumpThreshold` 与 dt 夹子步） |

纯数字部分（`M.step_tip` / `M.local_rotations` / `M.gains_of` / `M._q`）**不碰引擎**，
`tmp/lua-check/p2-solver-test.lua` 用它做数值回归（全部对标解析解）：

| 断言 | 实测 | 解析 |
|---|---|---|
| 固有频率 `f0` | 1.123 Hz | 1.125 Hz（√k/2π） |
| 阻尼比 `ζ` | 0.143 | 0.141（c/2√k） |
| 静垂角 | 7.66° | 7.59°（atan(g/(kL))） |
| 角度限位 | 20.05° | 20.05°（limit） |
| 三节链累积弯曲 | 7.66° / 7.80° / 8.11° | 每节独立 + 层级叠加 |
| `local_rotations` 净旋转复现目标方向 | 误差 1.7e-15 | 0 |
| **转身 180°/0.5 s 的尖端滞后** | **峰值 128.6°** | 旧核恒为 0 |
| NaN | dt=1/240…1/30 各 12000 步 | 无 |

**两个自审抓到的错**（都是"看起来对"的坑）：
1. `local_rotations` 最初拿 `acc·seg` 去和 `q⁻¹·dir` 求增量 —— 起点用了**已经转过的方向**，
   总弯曲会偏大。正确是把目标方向换到**父空间**再求增量。由 T6 断言抓出。
2. 测 ζ 时对 `|偏角|` 找峰 —— 整流信号一个周期两个峰，周期被算成一半、ζ 也被算成一半。
   改成对**带符号**偏角找峰后与解析值吻合（0.143 vs 0.141）。

**必须显式声明的单位契约**：`options.rigLengthUnit = "source" | "meter"`。
源模型导出的 rig 长度是源单位（×0.01905），运行时合成的 rig 已经是米 —— 不说清就会二次换算。
探针里传 `segmentUnitScale = 1.0`（等价 meter）。

引擎侧集成测试在 `tmp/lua-check/p1-regression-test.lua` 的 R4（假引擎跑 `attach → update_tip → 写姿态`，
120 帧写入 360 个姿态、零失败、G4 未误报）。


## 13. P3 进度（2026-10-31 19:5x）

### 13.1 P3-a 已完成并实机验证：参数是数据，不是代码

| 项 | 结果 |
|---|---|
| 参数来源 | `%LOCALAPPDATA%\hd2pbx_rig.bin`（裸 rig）或 `hd2pbx_rig.container.bin`（HD2PBX01） |
| 热改链路 | 改文件 → 命令通道发 `reset=<新值>` → 探针重新 attach 并重读文件（**游戏不退**） |
| 实机验证 | 阻尼 7 → 1.5（ζ 0.49 → 0.11）：末节峰峰值 **5.4° → 49.5°（9.2 倍）**，改完立刻可见 |
| 源模型原值回归 | 改回 7 后重挂，日志确认 `rig 来源=文件` + `单位：源单位 rig，length/tipMass 均按 0.01905 换算` |

工具：`tmp/make-rig.py`（从 57 骨源 rig 挑子集/整份导出/覆盖参数，同时产出裸 rig 与容器）。

### 13.2 完整 rig 的安全降级已验证

`tmp/lua-check/rig-all-smoke.lua`：
- 全量 57 骨 rig 对"只插了 6 个节点"的模型 → **0 链挂上、39 链记缺节点、驱动不抛错**
- 子集 6 骨 rig → **2 链挂上、每帧写 6 个姿态**

即"rig 里有、模型里没有的骨"是**安全跳过**，不会崩也不会静默出错。

### 13.3 新增：每根 jiggle 骨的顶点归属（完整版的依据）

`hd2mod source inspect <vpk>` 现在会给出每根 jiggle 骨在 LOD0 上**主导顶点数 / 参与顶点数**
（`SourceModelInspection.JiggleWeights`，口径：主导 = 该骨是该顶点权重最大的那根）。

Lunalice_Zoey 实测：**57 根全部有主导顶点**，最大的两根是 `source_extra_80`（2272）/ `source_extra_81`（1333），
其余多在 200–350。含义：

1. 完整版**没有一根骨是"空插"**，57 根都该驱动；
2. 这两根大骨（锚在 `Spine4`/chest、`tipMass=0`）带的是最大的一片布/发，观感上最显眼；
3. 这个数同时就是准入检查 `WEIGHT_EMPTY` 的判据 —— 为 0 的骨不插节点。

### 13.4 插骨清单（自动生成，可直接照着做）

`hd2mod source inspect <vpk> --json` → `tmp/physbone-plan.py` → **`tmp/physbone-out/jiggle-plan.md`**

每行给出：骨名 / HD2 锚点 / 主导顶点 / 参与顶点 / flags / `f0` / `ζ` / 静垂角 / 降级备注。
锚点里有 6 条是**链套链**（锚点本身就是 jiggle 骨，如 `source_extra_118 → 117`）；
运行时的锚点解析走 `has_node`，只要**父节点先插**就能查到 —— 插节点工具必须按父→子顺序处理。
只有 4 根命中未实现的 flags（`80`/`81` 无 `lenC` 且 `tipMass=0`）。

### 13.5 P3-b 实施计划（转换器侧）

现状：`Retargeter.ResolveBoneMap()`（`HD2SourceMigration/Retarget/Retargeter.BoneMap.cs`）
把所有**没有对端**的源骨按"肢体 + 最近已映射祖先"回退到某根 HD2 骨
（注释里明写 `source_extra_73/76/85` 是"肩旁头发与发带骨，只能落到头上"）。
于是 `RetargetedInfluence.BoneHash` 变成 `head` 的哈希、权重被合并 —— **头发被压平**。

要做的事（按依赖顺序）：

1. **`Retargeter`**：对 `bone.Jiggle != null` 的源骨**不再回退**，改为分配一根**合成骨**：
   - 名字 = 源骨显示名（`source_extra_72`）——运行时按这个名字找节点；
   - 父 = 其锚点的 HD2 下标；锚点本身是 jiggle 骨时用合成骨下标，天然支持链套链；
   - 世界 bind = 对齐后的源关节位置 + **单位局部旋转**（正好满足运行时"bind local 旋转 = 单位阵"契约）；
   - 写进 `_sourceToHd2` ⇒ `RetargetedInfluence` 自动带它自己的名字哈希，**权重不再被合并**。
2. **`UnitAuthor`**：`CanonicalTransformInfoExpander.Expand(...)` 之后把合成骨并进 `TransformInfo`
   （名字哈希 + 父下标 + 矩阵）；`CanonicalLodBonePaletteCompiler` 再按用到的哈希排调色板。
   现有代码已按影响哈希查 TransformInfo，查不到就报"骨哈希 … 不在外壳扩展后的 TransformInfo 里"，
   所以只要节点进去了，这条链路自己就通。
3. **准入检查**：转换后断言每根 jiggle 骨"主导顶点 > 0"且"调色板里有它的哈希"，
   否则 `WEIGHT_EMPTY` / `PALETTE_MISSING`。

开关：`RetargetOptions.IncludeJiggleBones`（**当前默认关**，见下），便于 A/B 与回滚。
验证顺序：离线转换 → 用 `hd2mod` 读回产物核对"节点数增加、权重落在新骨上" → 进游戏看观感。

### 13.6 P3-b 进度（2026-10-31 20:0x）

**已完成（编译通过、193 项测试全绿）**

| 项 | 内容 | 验证 |
|---|---|---|
| 骨名哈希 | 新增 `Hd2NameHash`：64 位 Stingray 哈希的**高 32 位**。口径不是猜的——拿游戏自己的 `bonehash.txt` 交叉验证 `head/chest/hips/spine1/l_knee` 五个名字全中，低 32 位一个都不中 | `Hd2NameHashTests`（8 项，含 5 个游戏真值） |
| 合成骨规划 | `Retargeter.PlanJiggleBones(...)`：给每根 jiggle 骨分配合成骨（名字=源骨显示名、父=锚点、世界 bind=对齐后关节），下标**接在原生骨之后**，天然不动原生映射；用"反复扫到不动为止"做拓扑序，支持**链套链**（子下标比父小也能正确解析） | `JiggleBonePlanTests`（5 项：原生下标不动 / 链式父优先 / 父未映射则跳过 / 无 jiggle 时空 / 关节取值） |
| 结果携带 | `RetargetResult.ExtraBones` + `RetargetExtraBone(Name, NameHash, ParentIndex, WorldOrigin)` | 编译期 |
| 开关 | `RetargetOptions.IncludeJiggleBones`，**默认关** | 默认关 ⇒ 零行为变化（187 项旧测试原样通过） |

**为什么默认关**：`resolved` 里 jiggle 骨的目标会指向 `原生骨数 + n`，
如果上层没把这些节点追加进 `TransformInfo`，`RetargetedMeshBuilder` 会拿到越界下标
（现在会报"骨哈希 … 不在外壳扩展后的 TransformInfo 里"）。所以必须与下一步**同批**开启。

**待办（下一步，一件）**：`VpkPatchAssembler` 在 `RetargetedMeshBuilder.Build(...)`（约 353 行）之前，
把 `retarget.ExtraBones` 追加进 `avatarTransformInfo`：
`UnitTransformInfo(Reserved0..2, LocalTransforms + n, Matrices + n, Entries + n, NameHashes + n)`
（`UnitLocalTransform` 用单位旋转、`UnitTransformMatrix` 用 `WorldOrigin` 平移、`UnitTransformEntry` 的父下标指向锚点）。
追加后 mesh builder 与 `UnitAuthor` 都拿到同一份扩展过的 TransformInfo，链路即可自洽；
需确认 `CanonicalTransformInfoExpander.Expand` 不会重排已追加节点的下标（这是开启开关前的最后一道检查）。

### 13.7 P3-b 已完成（2026-10-31 20:1x，**离线全链路跑通**）

| 项 | 结果 |
|---|---|
| 追加器 `UnitTransformInfoAppender` | 追加合成节点；约定来自工作补丁实测（`Increment` 恒 1、矩阵平移在 12/13/14、局部旋转单位阵）→ `UnitTransformInfoAppenderTests` 5 项 |
| 装配器接线 | `VpkPatchAssembler` 在建网格**之前**把 `retarget.ExtraBones` 追加进 TransformInfo，网格与 `UnitAuthor` 共用同一份 |
| 开关默认值 | `IncludeJiggleBones` **翻成默认开**（上层已接线） |
| 全量测试 | `HD2SourceMigration.Tests` **197 项全绿**（1 项既有跳过） |
| **真实转换验证** | `hd2mod vpk2patch Lunalice_Zoey.vpk` → 7 个 Unit **全部 171 节点**（= 114 原生 + **57 jiggle**）；未命名节点 **68 = 57 新增 + 11 既有**（分毫不差）；调色板里出现节点下标 **114..170**，说明顶点权重**真的落在新骨上** |

即：**"导入即得物理"的数据链路已经通了**（骨进去了、权重也在上面）。剩下的是"材质齐全的整批转换 + 进游戏看观感"。



## 14. 一句话

**缺的不是"更好的参数调法"，是"生成参数的方程"。方程 Valve 已经写好了，而且比现在的实现更简单**：
没有质量、没有自由常数、不需要加速度输入、不需要矩阵乘法、不需要限位预算。
现在为了绕开引擎缺失的算子而引入的四个补丁（`MASS_BASE`/`maxAccel`/`positionSmoothing`/`chainBudget`），
本质都在替代同一个缺失量 —— **goal tip**。补上它，四个补丁一起消失，29 个源参数开始自己说话。

**但在改方程之前，先花 6 次重启把 E1–E6 定死。** 我们已经因为"看起来确定的错误结论"浪费过好几轮：
`0x31` 的解释是错的、护栏是死代码、`length` 单位是漏的、`to_elements` 布局是没定的。
这四条没有一条能靠读代码或推理发现，只能靠探针。

### 参数策略：限位一律用作者值，刚度做"频段归一化"

实测（Lilium，2026-10-31）：

| 骨组 | 源参数 | ωn=√k | ζ=c/(2√k) | 限位 | 表现 |
|---|---|---|---|---|---|
| 头发 64/73… | k=50, c=7, tipMass=200, L=0.571 m | 7.07 | 0.495 | 35° | 正常 |
| **大袖子 93…101** | **k=1, c=10, tipMass=500, L=0.190 m** | **1.0** | **5.0** | **360°（无限制）** | **垂不下来、被胳膊拖着甩** |

结论：袖子的病不在限位（作者本来就是 360°），而在 **ωn=1 rad/s（周期 6.3 s）跟不上 1 Hz 的胳膊动作**。

策略（`M.normalize_gains`，随 `M.gains_of` 默认启用）：

- **限位：一律原样使用作者的 `angleLimit`**，不做任何夹取（历史教训：曾经夹到 14° → 头发变钢棍）。
- **不要放大重力来“守恒静垂角”**：试过 g = k·L·tanθ（袖子 g 9.5→152 m/s²），结果是重力压倒一切，
  尖端被定长约束压在球面最低点、胳膊怎么动都不动（实测首节转角变化 **0.0°**，玩家形容“被夹住的钢片”）。
  保持作者的 g 即可：静垂角从 89° 变 72°，但**世界空间里依然接近垂直**（7 节累加），且首节有 16° 可见摆动。
- **自然频率**夹进 [4, 12] rad/s，**阻尼比**夹进 [0.4, 1.2]。
  - 下界取 0.4 而不是 0.5：实测头发 ζ≈0.495，用 0.5 会把这批本就正常的骨也推一下（尖端摆幅 0.20 → 0.19 m）。
  - 反解：k = ωn²，c = 2ζ·ωn，g = k·L·tanθ。
- 频段内的骨**逐位不变**（头发 `normalized=false`）；袖子 ωn 1→4、ζ 5→1.2。

离线实测效果：袖子末节离垂直 40–56° 乱甩 → 稳定 **7°**；尖端水平摆幅 **0.28 → 0.05 m**；头发几乎不变。

纯数学测试要源参数原样映射时，传 `M.gains_of(params, lenScale, gravScale, { normalize = false })`。