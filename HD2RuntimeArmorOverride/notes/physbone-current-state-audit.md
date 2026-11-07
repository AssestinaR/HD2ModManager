# 物理骨骼（jigglebone → physbone）现状审计

- 审计日期：2026-11-03
- 范围：Source `.mdl`/`.qc` 的 `$jigglebone` → 转换期容器 → HD2 运行时 Lua 求解器 → 每帧写入 Unit 节点
- 方式：**只读代码 + 只读真实产物/日志**（未修改任何源文件）
- 结论出处一律给 `文件:行号`；凡涉及"真实资产有多少根骨/多长的杆"的数字，均由本文作者直接解析 `E:\mod` 下已部署的 `.gpu_resources` 得到（解析脚本见 §8，位于 `%TEMP%`，不在仓库内）

---

## 0. 结论先行（一页）

| # | 症状（用户原话） | 主要归因 | 定性 |
|---|---|---|---|
| 1 | 「不会自然下垂，而是飘扬起来甚至指向错误方向」 | **静止姿态丢失**（每节目标方向 = 上一节的模拟方向，不含 bind 的"静态方向增量"）＋ **反平行退化时输出 0 旋转** ＋ **约束被静默关闭**（`angleCon`+360° ⇒ 完全不限位） | 求解器数学 / 姿态 |
| 2 | 「闪电折痕（Z 字形）扭曲」 | **分叉骨树被拍平**（兄弟分支被当成链的后续节，制造出 154° / 143° 的假折角）＋ 近 180° 时 swing 轴数值退化 | 求解器数学（结构错误） |
| 3 | 「完全没有胶囊/球碰撞体」 | 全仓 **0 行** 碰撞代码，也没有任何"引擎侧碰撞查询 API 可用"的探测记录 | 能力缺失 |
| 4 | 「要通用，不能只修这一个」 | 频段/阻尼归一化把作者参数改写（袖子 k: 1→16，胸件 cPitch: 5→9.6），29 个参数里 18 个未用，`base_spring` 完全未实现 | 参数保真度 |
| 5 | 「不知道某模型为什么没有物理」 | 转换报告里 `physBones` 只在 `HD2_PHYSBONE_CONTAINER=1` 时才写，且只报骨数；真实报告里全是 `[]`。求解器**没有任何 CI 测试** | 可观测性 |

7 条已知缺陷：**6 条完全成立，1 条（第 1 条）成立但表述需要修正**（不是"第一帧输出 0°/90°"，而是"第一帧就把整条链掰直，并把 bind 的每节弯曲角一次性写进节点"，实测 21°/26°、18°/7°/13°/14° 这类值；见 §3.1）。

最可能解释两个主诉的一对根因，都在同一个函数 `step_tip` 里，且互相放大：
- **向上飘/反向下垂** = `physbone_compat.lua:713-714`（每节 prev 直接取上一节的模拟方向，丢掉了 bind 的静态方向增量 ⇒ 零力平衡态是"一条直线"而不是 bind 形状）；
- **Z 字折痕** = `physbone_compat.lua:437-449`（`build_chains` 用 DFS 把分叉树摊平成一维数组，兄弟分支被当作链的续节）+ `physbone_compat.lua:505-513`（近 180° 时 swing 轴退化，`local_rotations:893` 直接退化成单位旋转）。

真实资产里这两件事**同时发生在一个模型上**（`silent_code_shinano信浓_bill`：骨 96 有三个子骨 97/98/100，摊平后"99→100"的人造折角 = **154.3°**，而生产日志里"史上偏离绑定峰值 = 154.3°"，且该值在数千帧内**恒定不变** ← 静态写死在节点上的折痕）。

---

## 1. 全链路数据流

### 1.1 站点表

| 站 | 文件 | 做的事 | 关键行 |
|---|---|---|---|
| ① MDL 解析 | `HD2SourceModel/SourceModelReader.cs` | 读 `mstudiojigglebone_t`（120 B = flags u32 + 29×f32），按骨骼的 `numparts/proctype==5/procindex` 定位 | `:272-301`，常量 `:56-59` |
| ② 域模型 | `HD2SourceModel/SourceModel.cs` | `SourceJiggleBone`（29 参 + 7 个 flag 常量）；`SourceBone.Jiggle`；`DisplayName`（无名骨 → `source_extra_<idx>`，带命名空间前缀） | `:20-81`、`:52-58`、`:119-121` |
| ③ 事实表 | `HD2SourceModel/SourceModelInspector.cs` | `SourceJiggleBoneFact`：**骨名 + 父骨名 + flags + 世界 bind 位置（源单位=英寸）** + 29 参 | `:197-212`（`WorldBind.M14/M24/M34`） |
| ④ 转换期落盘 | `HD2SourceMigration/VpkConversion/PhysBoneRigSidecar.cs` | `physbone-rig.bin`（`HD2SRG01` v2）落在 `vpk2patch/`，**带链根锚点名**（`ApplyAnchors`） | `:35-52`、`:58-71` |
| ⑤ 锚点名求解 | `HD2SourceMigration/Retarget/Retargeter.JiggleBones.cs` | `PlanJiggleAnchors`：源父骨名 → 它回退到的 HD2 原生骨名；`PlanJiggleBones`：给每根 jiggle 骨分配**合成骨**（父→子拓扑排序） | `:153-187`、`:75-129` |
| ⑥ 插骨 | `HD2SourceMigration/VpkConversion/UnitTransformInfoAppender.cs` | 往 Unit 的 TransformInfo 末尾追加节点：**局部旋转 = 单位阵**、局部平移 = 父空间、世界矩阵沿用父骨旋转 | `:46-94`、`:60-84` |
| ⑦ 容器化 | `HD2SourceModel/PhysBoneContainer.cs` | `HD2SRG01` 定长记录（148 B v2）+ `HD2PBX01` 容器 + `HD2PBFT1` footer | `:39-109`、`:124-232` |
| ⑧ 嵌入 | `HD2SourceMigration/VpkConversion/PhysBoneContainerEmbedder.cs` | 追加到 `.gpu_resources` EOF（幂等：先 Strip 再 Append），写后回读校验 | `:114-162`、`:214-225` |
| ⑨ 保尾 | `HD2ModAdaptation/PatchReconstruction/PhysBoneCarrier.cs` | 任何重写 patch 的路径都要把尾部容器带走（不变量 I4） | `:283-365` |
| ⑩ Lua 载荷 | `HD2ModCore/Infrastructure/PhysBoneLuaSource.cs` | 把 `physbone_compat.lua` + `physbone_runtime.lua` 作为**嵌入资源**合成单文件外置包 | `:36-75`、`:135-138`(csproj) |
| ⑪ 部署模式 | `HD2ModCore/Domain/PhysicsMode.cs` | 三态：`Off` / `ExclusiveLuaEntry`（占 boot）/ `ExternalLuaEntry`（外置包），默认 **Off** | `:10-26`、`:36` |
| ⑫ 运行时发现/调度 | `HD2PhysBoneCompat/src/physbone_runtime.lua` | 找世界/玩家/护甲件 → 逐 unit 选容器 → attach → 每帧 `Compat.update` | `:266-393`、`:406-462`、`:475-684`、`:782-1062` |
| ⑬ 求解器 | `HD2PhysBoneCompat/src/physbone_compat.lua` | `build_chains` → `gains_of` → `step_tip` → `local_rotations` → 写 local pose | `:420-453`、`:546-622`、`:680-867`、`:879-905`、`:2368-2595` |

### 1.2 每一站"保留 / 丢弃"了什么

| 数据 | ①读出 | ③事实表 | ④⑧二进制 | ⑬运行时消费 | 备注 |
|---|---|---|---|---|---|
| 骨名 | ✓（常读不出 → 占位名） | ✓ `BoneName` | ✓ 名字表 | ✓ `has_node` 解析节点 | 占位名跨模型重名（已加命名空间） |
| 父骨**下标** | ✓ | ✓ | ✓ `i32 @+4` | ✓ 只用于建链 | |
| 父骨**名字** | ✓ | ✓ | ✓ v2 新增 | ✓ 链根锚点 | v1 无此项 |
| flags | ✓ | ✓ | ✓ | **只用 3 个位**：0x04/0x08/0x10；0x01/0x02/0x20/0x40 **从不读** | 见 §4.1 |
| 世界 bind 位置 | ✓ 英寸 | ✓ | ✓ `3×f32` | **只被 `apply_world_segments` 用于旧核** | 新核只用它算 `segmentLength`（旧核专用）→ 实际近乎死数据 |
| 29 个参数 | ✓ | ✓ | ✓ | 只用 11 个 | 见 §3.5 |
| **bind 局部旋转 / bind 矩阵** | ✓(`PoseToBone`) | ✗ | ✗ | ✗（运行时假设"局部旋转 = 单位阵"） | 见 §6 注 |
| **顶点↔骨权重** | ✓ | 只统计计数 | ✗ | ✗ | 权重在转换期就烘进网格 |
| 合成节点父下标 | — | ✓ `RetargetExtraBone.ParentIndex` | ✗ | ✗ | 运行时**不读**，自己重新用"父是否也是 jiggle 骨"建链 → 分叉信息在这里丢失 |

> **这是本审计最重要的一张表**：`build_chains` 之所以能把分叉拍平，是因为容器里**没有**"父节点在 HD2 节点表里的下标"，运行时只能靠"父骨是不是 jiggle 骨"重新推链；而推链用的是 DFS 摊平，不是树。

---

## 2. 真实数据（客观事实，非推测）

解析 `E:\mod\*_Bill\{vpk2patch,Overwrite}\*.gpu_resources` 尾部容器（8 个模型、14 份 rig，去重后 8 份）：

| 模型 | 骨数 | 链数 | **分叉** | flags 集合 | 杆长/真实骨距（L÷spacing） |
|---|---|---|---|---|---|
| Lilium_Sancta_Bill | 54 | 28 | 0 | 0x11, 0x31, 0x3d | 0.63 / 3.8 / 24.8 |
| Elise_Bill | 55 | 33 | 0 | 0x11, 0x31 | 6.6 / 9.3 / 23.1 |
| Manuka_JABIS_Bill | 56 | 30 | 0 | 0x11, 0x31 | 4.2 / 12.3 / 25.0 |
| Manuka_LaFiore_Bill | 51 | 38 | 0 | 0x11,0x31,0x35 | 4.2 / 12.4 / 22.3 |
| MourningVeil_Bill | 56 | 39 | 0 | 0x11, 0x31 | 1.7 / 5.2 / 16.5 |
| **silent_code_shinano信浓_bill** | **68** | **26** | **1** | 0x31, 0x35, **0x51** | 2.2 / 5.9 / **91.7** |
| lunalice_bill | 57 | 39 | 0 | 0x11,0x31,0x39 | 3.7 / 8.5 / 9.6 |
| serenade_bill | 55 | 31 | 0 | 0x11, 0x31 | 1.9 / 7.5 / 10.2 |

（"中位 L/spacing = 3.8~12.4" 意味着**杆长普遍是真实骨距的 4~12 倍**，最长 91.7 倍。不是边缘案例，是常态。）

分叉链（唯一一条，信浓）：

```
骨 96 (0x35) ──┬── 97 (0x35)
               ├── 98 (0x31) ── 99 (0x31)
               └── 100 (0x31) ── 101 (0x31)
build_chains 摊平结果: [96, 97, 98, 99, 100, 101]
相邻节夹角(由 bind 位置算) = 50.0°, 44.3°, 154.3°, 143.5°
                              ↑人造假折角（99 与 100 是堂兄弟，不是父子）
```

生产日志（`%LOCALAPPDATA%\hd2physbone.log`，239,854 行，2026-11-03 最后一局）：

```
驱动心跳 unit=8 链=299 玩家=1 分帧=2 dt=0.0116 本帧 写入=385 …
       偏离绑定峰值=154.3°(史上154.3°) 最离谱=CYCR_Hat_DR_4(限0°)
```

- **154.3°** 与上表那个"人造假折角"数值完全一致，且**数千帧恒定不变** ⇒ 静态折痕写死在节点上。
- 该 log 里 `最离谱=` 抽样全部是 `(限0°)`（不限位的骨）；史上峰值最高到 **172.0°**（`jba8bfae0187b/source_extra_93` = Lilium 大袖子根），100°+ 的几乎全是袖子/飘带 ⇒ 与 §4.2"约束静默关闭"一致。
- 同 log 中 `限位来源：cone×N`（**从未出现 `yawPitchAxes`**），与 §4.2 的静态分析（分轴限位在生产里永不触发）一致。
- attach 统计 `可用链 39 条（缺节点 0、缺锚点 0）`：锚点映射本身目前是全命中的，**不是**"缺锚点静默丢弃"在起作用。

另有一份 2026-11-03 的探针会话（`%LOCALAPPDATA%\hd2pbx_probe.txt`）：

```
[physbone] 链 source_extra_64 第 1 节方向退化（长度 0.00e+00），退回 +Y 占位
…（336 根骨全部出现此行，bind 方向全部 = (0.00 1.00 0.00)，段长全部 = 1.000 m）
```

即**一次性地所有 bind 世界位置读到同一个值**（场景图世界矩阵尚未刷新 / 读的是错 unit），代码只打一行日志、然后**用 +Y 当所有方向**继续驱动。见 §4.4。

---

## 3. 7 条已知缺陷逐条核实

### 3.1 缺陷 1：`step_tip` 没有把静态方向增量乘进去 —— **成立（表述需修正）**

**位置**：`HD2PhysBoneCompat/src/physbone_compat.lua:701-715`

```lua
-- 父段前向：第 1 节用**动画**方向，其余用上一节的**模拟**方向（这就是链式弯曲）
if k == 1 then
    local s = item.segDirLocal[1]
    local gqx, gqy, gqz, gqw = qx, qy, qz, qw
    if item.goalQ ~= nil then gqx, gqy, gqz, gqw = item.goalQ[1], … end
    prevX, prevY, prevZ = qrot(gqx, gqy, gqz, gqw, s[1], s[2], s[3])   -- ✓ bind 方向
else
    local d = item.dirs[k - 1]
    prevX, prevY, prevZ = d[1], d[2], d[3]                              -- ✗ 直接抄上一节
end
```

- 第 1 节的 `prev` = **bind 方向**（对），第 2 节起 `prev` = **上一节的模拟方向**（错）。于是"零力平衡态"= 一条**直线**，而不是 bind 的形状。bind 里每节之间的夹角（即"静态方向增量"）被抹掉。
- 目标尖端 `g = base + L·prev`（`:719-721`）；首帧 `tip.ready == false` 时直接把尖端放到 `g`（`:722-726`）⇒ **第一帧就把整条链掰成直线**，然后 `local_rotations` 把这个弯曲差一次性写进每节节点。

**离线实测**（`%TEMP%\pb_repro.lua`，逐行复刻 attach 的几何构造后直接调 `M.step_tip` / `M.local_rotations`）：

```
A 头发（bind 是竖直直线，Lilium 64..67 真数据）  第 1~3 帧各节输出转角(度)=[0.0, 0.0, 0.0, 0.0]   ← 直线链看不出问题
B 同参数、bind 弯 20°/节                        第 1 帧=[0.0, 21.3, 26.0, 0.0]，第 2/3 帧完全相同 ← 永久变形
C 大袖子（Lilium 93..101 真数据，7 节）          第 1 帧=[0.8, 18.1, 7.0, 13.4, 14.2, 1.5, 0.8]
```

- 所以"第一帧就输出 0°/90°"这个描述**不准确**：准确的说法是"第一帧就输出**每节的 bind 弯曲角**"，而**首节恒为 0**（因为首节 prev 是对的）——这正是实测里所有链"首节 0°、后面乱掉"的形态。
- 为什么 Lilium 的头发"看起来正常"、袖子/信浓的头发出问题：头发 bind 链本身就接近直线（spacing 均等、方向一致），增量≈0；袖子/马尾是弯的，增量最大 18°；信浓那条还叠了 154° 的人造折角。

**影响**：静态姿态不保。① 视觉上整条链被"掰直"，即"该垂下来的变直/变翘"；② 每节都带一个恒定的非零旋转，等于把网格永久折弯（等于绕过 bind 做了一次额外的姿态烘焙）；③ 后续所有弹簧运动都在这个错误基线上叠加，`angleLimit` 也是相对这个错误直线判定的，造成"约束明明写了却没效果"。

**修法方向**（不要实施）：在 attach 时为每节预算"**静止相对旋转**" `restDelta[k] = swing(segDirLocal[k-1] → segDirLocal[k])`（在锚点局部系里，一次性算好，每帧零成本），然后

```lua
prev_k = qrot(restDelta[k], dirs[k-1])    -- 而不是 dirs[k-1]
```

这样零力平衡态 = bind 形状，链式弯曲照旧（父节动，子节按静止相对姿态跟随）。等价写法：`prev_k = R_anchor · acc_{k-1} · segDirLocal[k]`（把本节 bind 方向按"已累积的旋转"带过去），与 `local_rotations` 的 `acc` 保持同一套语义即可。

### 3.2 缺陷 2：`build_chains` 把分叉骨骼树拍平成单链 —— **成立，且比"丢分支"更糟**

**位置**：`physbone_compat.lua:420-453`

```lua
for _, bone in ipairs(bones) do
    local isRoot = byIndex[bone.parent] == nil      -- 父不是 jiggle 骨 ⇒ 链根
    if isRoot then
        local chain, stack = {}, { bone }
        while #stack > 0 do
            local current = table.remove(stack)
            chain[#chain + 1] = current
            local kids = children[current.index] or {}
            for i = #kids, 1, -1 do stack[#stack + 1] = kids[i] end   -- 所有子节点都压栈
        end
        chains[#chains + 1] = chain
    end
end
```

- **不是"丢掉分支"**，而是把所有分支**串到同一个数组里**。下游三处都按"chain 里相邻两节是父子"来用：
  - `step_tip:713` `prev = dirs[k-1]`（把兄弟当前驱）；
  - `local_rotations:885-899` `acc = acc · S_k`（把兄弟的累积旋转带进来）；
  - `attach:1629-1639` `basePoses[index] / nodes[index]` 按 chain 顺序对齐（这一点是对的）。
- 后果：**分支 C 的旋转是在分支 B 的累积坐标系里算的**。也就是说，写进节点的旋转"相对父节点"这个前提被破坏，分支 C 会带着分支 B 的全部累积旋转被拧一下。
- 真实数据里唯一一条分叉链（§2）在摊平后产生 **154.3° / 143.5°** 的人造折角，正好等于生产日志里那个恒定不变的 `偏离绑定峰值=154.3°`。

**影响**：这条链上的所有分支都错向；数值上表现为**一个恒定的大角度折痕**（Z 字），并把 154° 塞进 `local_rotations` 的 swing 分支 —— 那是 swing 轴最不稳定的一段（见 3.3）。

**修法方向**：`build_chains` 返回**树**（`{ root, children = {…} }`），或返回"每根骨 → 父骨在链内的下标"；`step_tip` / `local_rotations` 改成按**父下标**取 `prev` / `acc`（保留一个 `accByIndex` 表，天然的父先子后顺序）。顺便：容器 v3 直接把"父节点在 HD2 节点表里的下标"写进去（现在 `RetargetExtraBone.ParentIndex` 在转换期已知、运行时却拿不到），就不必再从骨名/父子关系反推。

### 3.3 缺陷 3：`swing_axis` 平行/反平行返回 nil → 180° 变成零旋转 —— **成立**

**位置**：`physbone_compat.lua:505-513`

```lua
local function swing_axis(fx, fy, fz, tx, ty, tz)
	local dot = fx * tx + fy * ty + fz * tz
	if dot > 0.9999995 or dot < -0.9999995 then return nil end   -- 平行 / 反平行
	local cx, cy, cz = fy*tz - fz*ty, fz*tx - fx*tz, fx*ty - fy*tx
	local length = math.sqrt(cx*cx + cy*cy + cz*cz)
	if length < 1e-9 then return nil end
	…
end
```

两个调用点，两个不同的坏结果：

1. `local_rotations:892-893`：`if sx == nil then sx,sy,sz,angle = 0,0,1,0` ⇒ **需要 180° 时输出单位旋转**，节点原地不动（指向错误方向）。
2. `step_tip:827-849`（锥限位）：`else nx,ny,nz = prevX,prevY,prevZ` ⇒ **把尖端钉回 prev**，即"该折的那一下被抹平"。

**离线实测**（样本 D：第 2 节 bind 方向 (0,0,+1)，目标方向 (0,0,-1)，夹角正好 180°）：

```
D 反平行/回折   第 1 帧各节输出转角(度)=[0.0, 0.0, 0.0]   dirs=(0,0,-1) (0,0,-1) (0,0,-1)
                ↑ 第 2 节需要 180°，实际输出 0°
```

**更严重的近邻问题**（同一段代码，比"正好 180°"常见得多）：`dot` 落在 -0.9999995 … -0.99 之间时 `length ≈ sinθ ≈ 0.02~0.14`，叉积方向由浮点噪声主导 ⇒ **轴每帧随机翻转** ⇒ 视觉上就是**抖动/闪电形**。真实资产里"相邻节夹角 154°"（信浓）恰好落在这个区间。

**修法方向**：反平行时取任一与 `f` 正交的确定性轴（例如 `f` 与 world-up 叉积，退化则与 world-x 叉积）并给 180°；近反平行时（比如 `dot < -0.99`）也走同一分支，保证轴连续。同时 `local_rotations` 里 `sx == nil` 不得退化成单位旋转 —— 应该报错/钳到限位并记一次可观测计数。

### 3.4 缺陷 4：`normalize_gains` 夹 [4,12] / [0.4,1.2]，且用 yaw 的 ζ 覆盖 pitch —— **成立**

**位置**：`physbone_compat.lua:637-675`

```lua
M.OMEGA_BAND = { min = 4, max = 12 }      -- :637
M.ZETA_BAND  = { min = 0.4, max = 1.2 }   -- :641
…
local newOmegaPitch = math.max(M.OMEGA_BAND.min, math.min(M.OMEGA_BAND.max, omegaPitch))
gains.kPitch = newOmegaPitch * newOmegaPitch
gains.cPitch = 2 * newZeta * newOmegaPitch    -- :670  ← newZeta 来自 yaw，pitch 的 ζ 被丢掉
```

用真实参数跑 `gains_of`（`normalize=false` vs 默认）：

| 骨 | 作者原值 | 归一化后 | 变化 |
|---|---|---|---|
| Lilium 64（头发 k50/c7，ang 35°，0x31） | ω=7.07 ζ=0.495（yaw=pitch） | **原封不动** | 设计意图正确：频段内的骨逐位不变 |
| Lilium 87（胸件 k50/c5/**kPitch 200**/cPitch 5，0x11） | ζpitch = **0.177** | kPitch 200→**144**，cPitch 5→**9.60** | pitch 阻尼 ×1.92，而且用的是 **yaw 的 ζ=0.4**（作者 ζpitch 0.177 直接消失） |
| Lilium 93（大袖子 k1/c10，360°，0x3d） | ω=1，ζ=5 | k **1→16**，ζ 5→1.2 | 刚度 ×16 |
| Lilium 92（袖子 k**0**/c10，360°） | ω=0.03，ζ=158（作者要的是**零弹簧、纯阻尼**） | k **0→16**，ζ→1.2 | 语义反转：给了一根 ωn=4 的弹簧 |

**影响**：① 分轴 pitch 手感被抹平（同一根骨的两个方向被强行同阻尼）；② 作者明确写 `stiffness=0`（自由悬挂布）被塞进一根硬弹簧；③ ω 上限 12 顺带**保证了数值稳定**（半隐式欧拉的稳定域 h·ω<2，h≤1/60 ⇒ ω<120 都稳），所以**不能只删夹取不加子步**（见 §4.5）。

**修法方向**：ζ 必须分轴（`newZetaPitch` 独立算）；`k=0` 要区分"作者想要自由悬挂"而不是夹到 4；`OMEGA_BAND` 至少做成 `options` 可配（现在写死模块级常量，运行时调不了）；若删夹取，必须在 `update_tip` 里把子步数按 `h·ωmax ≤ 0.5` 反算。

### 3.5 缺陷 5：29 个参数里 18 个完全没用 —— **成立**

`M.parameterNames`（`physbone_compat.lua:157-169`）与 C# 的写出顺序（`PhysBoneContainer.cs:91-109`）一致。逐个 grep `physbone_compat.lua` / `physbone_runtime.lua` 的引用：

| 用了（11） | 未用（18） |
|---|---|
| `length`(5次)、`tipMass`(2)、`yawStiffness`(2)、`yawDamping`(2)、`pitchStiffness`(1)、`pitchDamping`(1)、`angleLimit`(2)、`minYaw`/`maxYaw`(各1)、`minPitch`/`maxPitch`(各1) | `alongStiffness`、`alongDamping`、`yawFriction`、`yawBounce`、`pitchFriction`、`pitchBounce`、`baseMass`、`baseStiffness`、`baseDamping`、`baseMinLeft`、`baseMaxLeft`、`baseLeftFriction`、`baseMinUp`、`baseMaxUp`、`baseUpFriction`、`baseMinForward`、`baseMaxForward`、`baseForwardFriction` |

**并且 `minYaw/maxYaw/minPitch/maxPitch` 是"读了但永远用不到"**（比完全没读更隐蔽）——见 §4.2。

**修法方向**：优先级 `base_spring`（0x51 真实存在）> `along_*`（伸缩）> friction/bounce（可先用 ζ 折中）> yaw/pitch 分轴限位（要先修 §4.2 的开关）。

### 3.6 缺陷 6：`has_base_spring` 没实现（12 个 `base*` 参数）—— **成立，且有真实资产命中**

`M.FLAG_BASE_SPRING = 0x40` 在 `physbone_compat.lua:524` 声明后**全文件再无引用**（`rg FLAG_` 只有 518-524 的声明 + `:567` 用 0x10）。C# 侧 `SourceJiggleBone.HasBaseSpring`（`SourceModel.cs:66`）也只是暴露了一个布尔。

真实命中：`silent_code_shinano信浓_bill` 的 flags 集合含 **`81 = 0x51 = flexible|angle_con|base_spring`**（其余 7 个模型没有）。也就是说**用户库里已经有一份资产在用它**，我们完全没实现，而且日志里不会提一个字。

**语义**（据 `docs/agent/external/L4D2-AUTHORITATIVE-FACTS.md:79-87`）：`base_*` 是**基部**弹簧，坐标是 left/up/forward 三个平面方向的 min/max + friction，与 `yaw/pitch`（尖端）是两套自由度。**修法方向**：base spring 是"锚点自身的二阶弹簧"，可以在同一套 `step_tip` 里加"锚点位移态"（3 个方向各一根弹簧 + 各自的 min/max/friction），或者退一步：先用 `base_stiffness/base_damping` 给链根加一个迟滞（延迟跟随 anchor 的平移/旋转），观感上明显优于"链根焊死在头上"。

### 3.7 缺陷 7：完全没有碰撞体 —— **成立（能力缺失）**

- `rg -i "collision|capsule|sphere|raycast|Physics\."` 在 `HD2PhysBoneCompat/**` 与全仓 `.lua`/`.cs` 里**零命中**（除 UI 里的 "CapsuleButtonGroup" 之类无关命名）。
- 求解器里唯一的"约束"就是 `gains.L` 的**定长球面约束**（`:740-853`）和角度限位，二者都是**相对自身/父节**的，与身体几何无关。
- 仓库里也**没有任何"引擎侧碰撞查询 API 是否可用"的探测记录**（`docs/` 全库 grep `raycast`/`PhysicsWorld`/`collision_filter` 零命中）——所以这条不只是"没做"，连可行性都还没探。
- 参考实现里有对应模块（`tmp/pb-ref/HD2-PhysBoneTool/physbone/core/contact.py`、`contact_feasibility.py`、`blender/collider_transform.py`），可以直接借它的数据模型（球/胶囊 + 每链 collider 组）。

**修法方向**（先探后做）：① 先做一次 API 探测（`Unit.world_position` 已知可用 ⇒ 至少可以用"锚点骨位置 + 半径"做**球形排斥**，纯 Lua、零新 API：链根附近用 `head`/`chest`/`hips`/`l_shoulder` 等的世界位置构造 3~5 个球，每子步把尖端推出球外 —— 这就是"效果略差但不缺失"）；② 若要真胶囊，需要引擎的线/胶囊求交，先探测 `World`/`Unit` 命名空间有没有。

---

## 4. 新发现的缺陷（文档/评审都没提到的）

### 4.1 【结构】`serialBase`（串行链基座）从来没被赋过值 —— 注释宣称的修法默认是关的

`physbone_compat.lua:688-695` 的注释说"这才是一条真正的链"，但 `item.serialBase` 在**全仓从未被赋值**（`rg serialBase` 只有注释 + 那一行 `if`；`tmp/lua-check/zigzag-lab4.lua:6` 也记了这件事）。生产实际走的是 `else` 分支：

```lua
-- :697-700
local ox, oy, oz = qrot(qx,qy,qz,qw, bone.offLocal[1], …)
bx, by, bz = apx + ox, apy + oy, apz + oz     -- 基座 = 锚点 + 本节 bind 偏移
```

⇒ **每节的基座是"本节真实骨位"，但目标方向是"上一节的模拟方向"、杆长 L 却是 4~92 倍的真实骨距**。三者不在一个尺度上：目标尖端被推到本节骨位 + 0.57 m，而下一节的基座在 2 cm 外。这既不是"每节独立弹簧"（因为方向在传），也不是"整链杆"（因为基座没传）。

**影响**：整链的形状与前 4~12 倍尺度无关的虚拟杆混在一起 —— 任何一节的方向误差都会在 `local_rotations` 里变成"相对父节的旋转"，于是视觉上是"每一节各转各的"，正是 Z 字/闪电的形态学来源。**这是缺陷 1 的同源问题**，两者应一起修（要么 `serialBase = true` 让基座也串起来，要么把 L 钳到真实骨距并把 rest-delta 补上）。

### 4.2 【约束】分轴限位（`axisLimits`）在整个真实库上**永不生效**；并且 15 根"360° 且带 angleCon"的骨**完全不限位**

`physbone_compat.lua:560-600`：

```lua
local angleLimit = math.abs(tonumber(params.angleLimit) or 0.6)
if angleLimit >= math.pi then angleLimit = 0 end          -- 360° ⇒ 0
…
if angleLimit == 0 and flags ~= nil then
    if not flag_set(flags, M.FLAG_ANGLE_CONSTRAINED) then -- 只有"没有 angleCon"才走分轴
        … limitYaw / limitPitch / axisLimits = true …      -- :568-589
    else
        limitSource = "coneZero"                            -- :598 ← 什么都不夹
    end
end
```

两条独立的死路（都用真实数据验证过）：

1. **0x35 / 0x39 / 0x3d 都带 0x10（angleCon）**，且 `angleLimit` 非 0（0.192 / 0.157 / 0.279）⇒ 直接跳过整个 `if`，`limitSource` 保持 `"cone"`，**`minYaw/maxYaw/minPitch/maxPitch` 永远用不上**。8 份真实 rig 里 `axisLimits` 可命中的骨数 = **0**；生产日志的 `限位来源` 也从未出现 `yawPitchAxes`。
2. **Lilium 的 15 根袖子骨（`angleLimit=2π` + `angleCon`）落进 `coneZero` ⇒ 连锥限位都没有**，一根骨都不夹。生产日志里 `最离谱=…(限0°)` 抽样全部是这种骨，史上峰值 172°。
   - 讽刺的是 `docs/physbone-next-plan-20261101.md:374` 已经写对了语义（"Valve 的 min/max yaw/pitch 是每根骨相对**自身静止姿态**的范围"），但实现被 `angleCon` 这个开关挡在外面。

**修法方向**：判据应改成"**作者给了非零 yaw/pitch 范围就用分轴限位**（相对于自身静止姿态），`angleCon`+`angleLimit≈2π` 视为"锥不限"，而不是"全都不要"；并在 attach 的 `限位来源` 统计里把这三类分开（`cone` / `yawPitchAxes` / `coneZero` 已存在字段，只是永远只会出现两种）。

### 4.3 【姿态】"静止姿态"的取得方式是对的，但它**没有任何校验**，且基准旋转被完全忽略

`attach:1653-1663` 用 `Unit.world_position` / `Unit.local_pose` 采 `bindWorld` / `basePoses`；`segDirLocal` 由这些世界位置差得到（`:1710-1736`）。**先给结论：这条路在正常情况下是对的**——因为插进来的合成节点局部旋转恒为单位阵（由 `UnitTransformInfoAppender:86-90` 保证），而祖先里唯一的外来旋转就是锚点，代码用 `conj(q)` 正好把它除掉 ⇒ `offLocal` / `segDirLocal` 等于 bind 几何，与 attach 那一帧是什么动画无关。这一点值得写下来，免得后人以为"attach 时的姿态会漏进静止姿态"而白改一轮。

真正的问题有三条：

- **① 无校验**：这个结论依赖"引擎已经把新插入节点的世界矩阵算出来了"。§4.4 的探针会话证明它会**整体失败**（336/336 根骨读到同一个位置），而代码只打一行日志就继续用 +Y 驱动。⇒ "静止姿态是否正确"必须有一条可观测判据（例如：`|offLocal|` 的中位数、相邻节点世界距离 > 2 mm），失败就把该链停用。
- **② 基准旋转被丢弃**：`write_pose:2236` / `write_pose_checked:2340` 只重建"我们的轴角 + 基准**平移**"，`basePoses` 里的旋转从不使用（`:2068-2070` 的注释把"单位阵"当既定事实）。一旦引擎侧（LOD、别的补丁、动画）动了这些节点的旋转，会被我们**静默覆盖**且无法察觉。
- **③ 容器里的 bind 世界位置是"源空间、英寸、未做重定向姿态变换"的**（`SourceJiggleBoneFact.WorldPosition` = `WorldBind.Origin`，见 `SourceModelInspector.cs:209`），**不能**直接拿来当 HD2 侧的 bind —— 合成骨位置在转换期被 `anchorDeform` 变换过（`Retargeter.cs:325-337`），那个结果只写进了 patch 的 TransformInfo 矩阵（`UnitTransformInfoAppender:69-91`），运行时读不到。所以"用容器里的 bind 位置替掉运行时采样"这个想法**不成立**；要固化 bind，只能给容器加 v3 字段（HD2 世界空间的 bind 位置 + bind 局部旋转），或让转换期把"每节 bind 方向（锚点局部系）"直接写进 rig。

### 4.4 【鲁棒性】bind 方向退化时**静默**退化成 `+Y`，并且照样驱动

`physbone_compat.lua:1723-1727`：

```lua
if length < 1e-6 then
    logger("[physbone] 链 %s 第 %d 节方向退化（长度 %.2e），退回 +Y 占位", …)
    sx, sy, sz, length = 0, 1, 0, 1
end
```

真实探针会话里 **336/336 根骨全部命中**（§2 末）—— 意味着那次会话的**全部** chain 都以 `(0,1,0)` 为静止方向驱动，视觉上等于所有头发被统一掰向 +Y。当前实现只打一行日志、不计数、不停用、不改 `limitSource`，也不会在心跳里体现。

**修法方向**：退化 ⇒ 该链 `disabled = true` 并计入 attach 汇总（"几何不可用 N 条"）。宁可不动，也不要拿一个假方向去驱动 336 根骨。

### 4.5 【数值】dt 与子步：**目前是安全的，但安全性依赖 §3.4 的 ω 夹取**（耦合，别单独拆）

`update_tip:2385-2391`：

```lua
if dt == nil or dt <= 0 or dt > 0.25 then dt = 1 / 60 end
if dt < 0.001 then dt = 0.001 end
local steps = 1
if dt > 1 / 60 then steps = math.min(4, math.ceil(dt / (1 / 60))) end
local h = dt / steps
```

- 半隐式欧拉的稳定域 `h·ω < 2`；当前 `ωmax = 12`（夹取后）且 `h ≤ 1/60`（或 dt=0.25 时 h=0.0625）⇒ `h·ω ≤ 0.75`，**稳**。
- 离线实测（样本 B，0.5 秒，dt = 1/240 … 0.1）**峰值转角逐位相同（26.04°）** ⇒ 帧率耦合**不是**当前症状的来源。
- ⚠️ 但若按 §3.4 建议删掉 ω 夹取，`yawStiffness=300`（真实样本存在）⇒ ω=17.3、`h=1/60` ⇒ `h·ω=0.29` 仍稳；而如果同时开 `serialBase` / 加碰撞（子步变贵），就必须把 `steps` 改成按 `h·ωmax ≤ 0.5` 反算，否则会真的"帧率一低就炸"。
- 另：`steps = min(4, …)` 在 dt=0.25 时 h=0.0625（4 步不够 15 步的量）——时间**没有丢**（`h` 乘对了），但精度下降；可接受。

### 4.6 【坐标系】除重力外，求解器是坐标系无关的；重力是**唯一**硬编码世界轴的量

- 所有几何量都经过 `q` / `conj(q)` 往返（`:1664-1673`、`:1729`、`local_rotations:880-890`），所以"Source Z-up vs HD2 Y-up"这类轴交换**不需要**在运行时做，也没有做错 —— 转换期 C# 侧已经用 `Mat3._alignment`（Kabsch/Yaw/RotZ180）统一到 HD2 世界空间（`Retargeter.cs:183-237`，高度沿 +Z 量）。
- **唯一没被验证的自由量**是重力方向：`:737` `local az = accYaw*lz + accPitch*uz - gains.g`，硬编码 `-Z`。依据是"一致性电池 E1 实测世界 +Z 朝上"（`:734` 注释），而 `docs/physbone-status.md:93` 又记着"patch 里 Unit 的网格/骨骼是 **up = −Y**"。
- 因此有一个**尚未排除**的可能：如果 `Unit.world_position/world_rotation` 对挂在 avatar 下的子 unit 返回的是**unit 局部空间**（up=−Y）而不是世界空间，那么 `-Z` 的重力在这个空间里是**水平方向** ⇒ 头发会朝着"侧面/上后方"飘，而不是下垂。这正好是用户主诉的形状之一。
- **怎么一次判死**（建议作为修复第 0 步，10 分钟就能做）：在游戏里让角色待机，读 `head` 与 `hips` 两个节点的 `world_position`，取差值 `d`；若 `d.z` 明显为正且 `|d|≈0.5 m` ⇒ 返回的是世界空间（Z-up，重力 -Z 正确）；若 `d.y` 才是主轴 ⇒ 返回的是 unit 局部空间，重力方向必须换成局部 up 的负向。同一份日志里顺带打出 `world_rotation` 的轴角，即可确认。

### 4.7 【参数保真】单位换算在 C# 与 Lua 之间不一致（0.0254 vs 0.01905）

- C#：源单位 = **1 英寸 = 0.0254 m**（`Retargeter.cs:99-101,146`、`SourceToHd2RigMap.cs:54`、`RetargetOptions.cs:233`）。
- Lua：`SOURCE_UNIT_TO_METERS = 0.75 * 0.0254 = 0.01905`（`physbone_compat.lua:473-476`），用于 `length` 与 `tipMass`。
- 两者相差 **25%**。对 `L` 与 `g` 同比例，所以静垂角 `atan(g/(kL))` 不变（这也解释了为什么没人发现）；但 `L` 相对**真实骨距**小了 25%，直接放大 §4.1 的"杆长 ≠ 骨距"错配。
- `docs/agent/external/L4D2-AUTHORITATIVE-FACTS.md:67` 明确 `length` 单位是**英寸**，且"应等于骨骼长度；写大于骨长会得到奇怪且不可预测的运动"。⇒ 建议统一用 0.0254，并把"L/spacing 中位数 3.8~12.4"当成一次显式的作者参数不合法告警（而不是静默照抄）。

### 4.8 【文档与现实】"三种约束形式"其实只实现了两种，第三种（分轴）是死代码

见 §4.2。`docs/physbone-status.md` 与 `physbone-next-plan-20261101.md:188-222`（§6.0b）都宣称"源模型有三种约束形式，过去只实现了锥"，但代码里分轴那条**永远进不去**。这是"文档说做了、代码没做"的一类；建议在文档里就地标注。

### 4.9 【可观测性】三处缺口（这是"为什么这些缺陷能活到现在"的答案）

1. **转换报告**：`HD2ModCli/CliApplication.cs:2463-2507` 的 `physBones` 只在 `HD2_PHYSBONE_CONTAINER=1` 时生成，且只报 `{patch, model, bones, rigBytes, containerBytes, replaced}`。实测：`E:\mod` 下 **198 份 `vpk2patch.json` 里 `physBones` 非空的有 0 份**。没有 chain 数、没有锚点命中率、没有"哪些链因为缺骨被丢"。
2. **运行时**：`attach` 有 `缺节点 N / 缺锚点 N` 汇总（`:1850-1851`）和 `限位来源` 统计（`:1855-1881`），但**没有**：分叉数、退化方向数、`normalized=true` 的骨数、`axisLimits` 命中数、`swing_axis` 退化次数、每链的 `L/spacing`。§4.4 那种"336 根骨全部用 +Y"的事故，在汇总里完全看不见。
3. **测试**：`HD2PhysBoneCompat/test/` 只有 `mp_harness.lua`，而它把 `Compat` **整个换成假的**（`mp_harness.lua:119-157`）⇒ 求解器 0 覆盖。真正的求解器实验脚本在 `tmp/lua-check/`（`p2-solver-test.lua`、`zigzag-lab1..4.lua`、`axis-limit-*.lua`、`sleeve-lab*.lua`），而 `HD2RuntimeArmorOverride/scripts/Test.ps1` **一个都没跑**（`rg PhysBone|mp_harness Test.ps1` 零命中）。这就是 §3.1 / §4.2 / §4.4 这类"数学错了但没人发现"的结构性原因。

---

## 5. 对照产品要求：把症状归到正确的一类

| 用户原话 | 归类 | 对应根因（行号） |
|---|---|---|
| 「不会自然下垂而是飘扬起来」 | **坐标/姿态（主）** | 静态方向增量丢失 `:713-714`；`serialBase` 从未开启 `:693`；约束全关 `:566-600`；重力轴待验证 `:737`（§4.6） |
| 「指向错误方向下垂」 | **姿态 + 数学** | 反平行→零旋转 `:505-513` + `:893`；分叉错系 `:437-449`；退化 +Y `:1723-1727` |
| 「闪电折痕 / Z 字扭曲」 | **数学（结构）** | 分叉拍平 `:437-449`（真实案例造出 154.3°）＋近 180° swing 轴噪声 `:505-513`；杆长≫骨距 `:1684-1701`（只记录不处理） |
| 「没有胶囊/球碰撞体」 | **能力缺失** | 全仓 0 行；连 API 可行性都未探测（§3.7） |
| 「要通用，满足任何来源 mod」 | **参数保真 + 能力** | 归一化改写作者参数 `:643-675`（袖子 k 1→16、胸件 cPitch 5→9.6、k=0 被塞成 16）；18/29 参数未用；`base_spring` 未实现（真实资产 0x51 命中） |
| （隐含）「这个 mod 为什么没物理」 | **可观测性** | §4.9 三处缺口 |

一句话：**当前症状不是"物理参数调得不好"，而是求解器把"静止姿态"和"链结构"这两件最基础的事丢了**；参数层面（碰撞/18 个未用参数）只是在此之上的欠账。

---

## 6. 修复优先级建议（含"哪些能一起改"）

### 批次 0（先做，1 小时内，不改行为只增加事实）
1. 重力轴判定（§4.6 的 10 分钟实验）——它会否决/确认后面整批工作的方向。
2. 把 `tmp/lua-check/` 里已有的求解器实验脚本接进 `Test.ps1`（先只跑 `p2-solver-test.lua`），让 §3 的每一条都有回归守门。
3. `attach` 汇总里加：分叉链数、退化方向骨数、`normalized` 骨数、`axisLimits` 命中数、`L/spacing` 中位数；`physBones` 报告去掉 `HD2_PHYSBONE_CONTAINER` 门控并加 chain/anchor 命中率。

### 批次 1（求解器数学，**必须一起改**，同一函数/同一语义）
4. §3.1 静止方向增量 + §4.1 `serialBase` 拍板（两者是同一件事的两半，分开改会互相抵消）。
5. §3.2 分叉：`build_chains` 返回父下标（或树），`step_tip`/`local_rotations` 按父下标取 `prev`/`acc`。
6. §3.3 反平行/近反平行：确定性轴 + 禁止"nil → 单位旋转"。
   > 4/5/6 共享 `item.dirs` / `segDirLocal` / `local_rotations` 的数据结构，**一次改完再测**，否则每改一个都会看到"另一个症状变严重"（历史注释里已经踩过两次）。

### 批次 2（参数保真，可独立）
7. §3.4 ζ 分轴 + `k==0` 语义 + `OMEGA_BAND` 可配（若放开夹取，同时按 §4.5 反算子步）。
8. §4.2 分轴限位开关修正（真实库 100% 受影响，且是"袖子被拉 172°"的直接原因）。
9. §4.7 单位统一到 0.0254 + `L/spacing` 越界告警。
10. §3.5/§3.6 `base_spring`（先做 0x51 命中的那 12 个参数）→ `along_*` → friction/bounce。

### 批次 3（能力缺失）
11. §3.7 碰撞：先做**球形排斥**（用现有 `world_position` + 锚点球，零新 API，"效果略差但不缺失"），再探引擎胶囊/射线 API。
12. §4.3 给"静止姿态"加校验（`|offLocal|` 中位数 / 相邻节点世界距离下限，失败即停用该链）；若要彻底脱离运行时采样，则给容器加 v3（HD2 世界空间 bind 位置 + 每节 bind 方向），不要试图直接复用现有的源空间 `WorldPosition`。

### 可以合并成一次提交的
- 批次 0 的 2+3（都是观测）。
- 批次 1 的 4+5+6（同一数据结构）。
- 批次 2 的 7+8+9（都只碰 `gains_of` / `normalize_gains`）。

### 明确不要做的事（依据仓库里已有的实测记录）
- 不要为了"让头发别翘"去放大 `g`（`:656-661` 已实测 ⇒ "被夹住的钢片"）。
- 不要为了修折痕去硬收杆长/限位（`:1695-1700` 已实测 ⇒ 更糟，折痕照旧）。
- 不要删 `ω` 夹取而不同时加子步（§4.5）。

---

## 7. 已知缺陷核实小结表

| 缺陷 | 结论 | 关键行 | 修正说明 |
|---|---|---|---|
| 1 `step_tip` 缺静态方向增量 | **成立** | `:713-714`（对照 `:702-711`） | 表述改为"首帧把整条链掰直、把 bind 每节弯曲角写进节点"，首节恒 0° |
| 2 `build_chains` 拍平分叉 | **成立** | `:435-448` | 不是"丢分支"，是"把兄弟分支当续节"；真实案例 154.3° |
| 3 `swing_axis` 退化 → 零旋转 | **成立** | `:505-513` + `:893` + `:828-849` | 另有"近 180° 轴噪声"，更常见 |
| 4 `normalize_gains` 夹带宽 + ζ 覆盖 | **成立** | `:637-675`，覆盖在 `:668-670` | 附实测数值（§3.4 表） |
| 5 18 个参数未用 | **成立** | `:157-169` vs 全文件引用 | 另：`minYaw/maxYaw/minPitch/maxPitch` 读了但永不生效（§4.2） |
| 6 `has_base_spring` 未实现 | **成立** | `:524` 声明后无引用 | 真实资产 `0x51` 命中（信浓） |
| 7 无碰撞体 | **成立** | 全仓 0 命中 | 连引擎 API 可行性都未探测 |

---

## 8. 复现方式（本文所有数字的来源）

```bash
# 1) 真实 rig 统计（容器 → HD2SRG01 → 链/分叉/flags/参数）
python %TEMP%\rigaudit.py "E:\mod\Lilium_Sancta_Bill\vpk2patch\9ba626afa44a3aa3.patch_0.gpu_resources"

# 2) 求解器离线复现（复刻 attach 几何 → step_tip → local_rotations）
luajit %TEMP%\pb_repro.lua
#   输出：样本 A 直线链恒 0°；样本 B 弯链首帧 [0, 21.3, 26.0, 0]；样本 C 袖子首帧 [0.8,18.1,7.0,13.4,14.2,1.5,0.8]；
#         样本 D 反平行输出 0°（应为 180°）；样本 E dt=1/240…0.1 峰值逐位相同

# 3) 生产日志取证
rg "偏离绑定峰值|限位来源|方向退化|缺节点" "%LOCALAPPDATA%\hd2physbone.log" | Select-Object -First 20
```

> 说明：`rigaudit.py` 与 `pb_repro.lua` 只写在 `%TEMP%`，**未**写入仓库；本次审计未修改任何源文件。
