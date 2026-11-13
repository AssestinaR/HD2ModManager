# 交叉审核材料：三个社区 PR 的合并与两处评审跟进

日期：2026-11-13　仓库：`AssestinaR/HD2ModManager`（私有约定见文末）
用途：请外部专家独立复核下面**第 2、3 节的判断与改动**。本文档自包含，不需要仓库访问权限。

---

## 0. 背景

- 这是个 Windows 桌面的《绝地潜兵 2》Mod 管理器（.NET 10 WPF + .NET 8 核心库 + .NET 10 CLI）。
- 其中一块是**游戏内 Lua 运行时**，在游戏进程里跑（Lua 5.1 语义，无 JIT），负责把 VRChat / L4D2
  来源的物理骨数据（Source `jigglebone`）在 HD2 的骨架上模拟出来。运行时文件约 4300 行，
  `physbone_compat.lua` 约 3200 行（求解器 + 参数换算）。
- 另一块是**转换器**（VBSP/VPK → HD2 patch），C#。
- 本次有三个社区 PR，我已审核并合入主线。其中两处我做了**评审跟进**（即由我改的、不是作者写的），
  这两处最需要外部复核。

合并后的提交（master）：

| 提交 | 内容 |
|---|---|
| `bdc12c6` | #23 UV 整格归位（作者 PR，squash） |
| `ac76db6` | #24 身份 API 探针改回默认关闭（作者 PR，squash） |
| `7b69fb9` | #25 单轴限位 + 心跳独立计数（作者 PR，因与 #24 冲突，本地解冲突后 merge） |
| `c6aaca6` | **评审跟进**：心跳间隔 0 = 关；锥对分轴骨不生效写进注释（我写的） |

验证：`dotnet build -c Debug` 0 警告 0 错误；`dotnet test` 2224 通过 / 1 跳过 / 0 失败；
Lua 回归 486 通过 / 0 失败（`python tools/physbone-harness/run_physbone_regressions.py`，LuaJIT 跑）。

---

## 1. 求解器里三条互相关联的约束（读懂 2、3 节的前提）

在 `physbone_compat.lua` 的 `step_tip`（每骨每帧一次）里，约束分三类，**互斥地只走一条**：

```lua
-- ① 分轴约束（yaw / pitch 各自区间）：夹角色向量的分量
if gains.axisLimits then
    if devYaw   > gains.limitYawMax   then devYaw,   overYaw   = gains.limitYawMax,   1
    elseif devYaw < gains.limitYawMin then devYaw,   overYaw   = gains.limitYawMin,  -1 end
    if devPitch > gains.limitPitchMax then devPitch, overPitch = gains.limitPitchMax, 1
    elseif devPitch < gains.limitPitchMin then devPitch, overPitch = gains.limitPitchMin, -1 end
end

-- ② 锥约束（angleCon，总偏角上限）：注意 `not gains.axisLimits`
local dot = nx * prevX + ny * prevY + nz * prevZ
if not gains.axisLimits and gains.limit > 0 and dot < gains.cosLimit then
    ... 把方向拉回锥面 ...
end
```

- **② 对分轴骨不生效**，这是早就有的设计，注释里的理由是「锥是相对父节的，会累加」。
- 因此对「既有分轴约束又有 angleCon」的骨（下面那个真实样本就是），**锥从来没有生效过**。
- `gains_of` 负责把 Source `jigglebone` 的 flag/参数换算成 `axisLimits / limitYawMin..Max /
  limitPitchMin..Max / limit（锥角）/ cosLimit`。

---

## 2. 请重点复核的三件事

### 2.1 #24：把「身份 API 探针」从默认开改回默认关 —— 这是翻回自己两周前的决定

**背景**：运行时里有个诊断探针 `probe_identity_api`，成组调用 11 个**引擎接口**
（`has_mesh` / `bones` / `World.entities` / `scene_graph_link` …），用来研究「同款模型命中多件时
能不能认出哪一件才是玩家」。它的签名没有验证过。

- 2026-11-12 有人明确要求**默认开启**（原注释：「有依据的崩溃是正常测试行为；低效率才是真正的坏事」），
  机制是「一个进程把 11 个接口全跑，进度落盘，崩了下次从下一个继续」，最多崩几次就把答案收齐。
- 2026-11-13 社区实测：**原生访问违例（0xC0000005）时 `pcall` 拦不住**。触发条件是
  `matchedCount > 1`（同款命中多件），而舰船里有人台 ⇒ 普通玩家一进舰船就会连崩数次，
  日志分别停在第 3、5、11 项调用之前。而探针结果只写日志、**不参与任何判定**。

**改动**（`physbone_runtime.lua`，一行条件）：

```lua
-- 改前
if probeSwitch == nil or probeSwitch ~= 0 then
-- 改后
if probeSwitch ~= nil and probeSwitch ~= 0 then
```

并加了一条静态 guard 用例钉住「默认关」，防止以后被人改回去：

```lua
check("身份 API 探针默认关闭（只在开关非 0 时才调）",
	wholeStripped:find("probeSwitch ~= nil and probeSwitch ~= 0", 1, true) ~= nil
		and wholeStripped:find("probeSwitch == nil or", 1, true) == nil)
```

**想请专家判断**：

1. 这个取舍对吗？（代价：以后想收集身份判据必须手动改调参文件；收益：普通玩家不再被连带崩溃）
2. 有没有更好的中间态？例如「只保留纯读接口（`has_mesh` / `bones`），把 `scene_graph_link`
   这种**写**接口永久移除」——但纯读接口同样会 AV，`pcall` 同样拦不住，所以我不确定白名单
   是否真能降低风险，还是只是把概率摊薄。
3. 「进程内 AV 拦不住」这个判断在 Windows + 游戏自有 Lua 宿主里，是否有可靠的隔离手段
   （SEH？子进程？只读内存的 API 验证？）值得做吗，还是必然要「崩一次换一条信息」？

### 2.2 #25 的注释更正：我判断作者（和 PR 描述）的技术说法是错的

作者给单轴受限的链恢复自由度的改动本身我认为是对的（见 2.4）。但他在注释/PR 里写：

> 没约束的轴按「不限」（分量界限 ±1）处理，**总偏角仍由锥（angleCon）兜住**。

我认为这句不成立：见第 1 节 ② 的 `not gains.axisLimits`，**锥对分轴骨根本不生效**。
所以改动后的真实语义是「这一轴按作者区间夹、另一轴完全自由、总偏角不受锥约束」。

**统计意义**：这类骨会偏离作者标注的「锥 50°」吗？会。约束只剩刚度/阻尼的动力学。

**我的改写**（已合入，`physbone_compat.lua` 约 731 行）：

```lua
-- **只约束了一个轴时，另一个轴不能跟着被夹成 [0, 0]。**
-- 求解器只要 `axisLimits` 打开就会把 yaw / pitch **两个分量都夹一遍**；没约束的那个轴
-- 若留着初值 0，就等于"这个方向完全锁死"。
-- 2026-11-13 实机（某转换模型的三条长发链，flags=0x35：只有 yawCon + angleCon）：
-- pitch 被锁在 0°，长发只能往一侧偏一点，跑动时最主要的前后甩全没了，看着像一根焊丝；
-- 同一颗头上走锥限位的前发/侧发却照常在动（日志里摆到 35°）。
-- 没约束的轴按"不限"（分量界限 ±1）处理。
-- ⚠️ **别把锥（angleCon）当成兜底**：`step_tip` 里锥对分轴骨**根本不生效**
-- （`if not gains.axisLimits and gains.limit > 0 …`，理由是锥相对父节、会累加）。
-- 所以单轴受限的骨实际是「这一轴按作者区间夹、另一轴完全自由、总偏角不受锥约束」，
-- 只由刚度/阻尼的动力学兜住。这与 VRChat 一致：作者只写 yaw 限位，pitch 就是自由的。
if axisLimits then
    if not yawSet then yawMin, yawMax = -1, 1 end
    if not pitchSet then pitchMin, pitchMax = -1, 1 end
end
```

**想请专家判断**：

1. 我对代码的判断与改写措辞是否准确、有没有说错或说得过头？
2. 「作者只写 yaw 限位 ⇒ pitch 自由」是否符合 VRChat PhysBones 的真实语义？
   （我的依据是 VRChat 的分轴限位是按轴独立配置的，没配的轴不夹；但请专家确认。）
3. **更根本的问题**：单轴受限时，是否应该让锥也生效（即把第 1 节 ② 的条件放宽）？
   那是行为改动：好处是「作者标了 50° 就真的不会超过 50°」；风险是锥相对父节会累加、
   可能出现「卡在墙外再弹开」的观感问题。我倾向**不动**，只把话说清楚——请专家评估这个保守选择。

### 2.3 #25 的心跳间隔：`0` 的语义

运行时每 N 帧打一行「驱动心跳」（证明驱动块真的被进过、并给出当帧关键数字）。原代码：

```lua
local heartbeat = math.max(1, math.floor(tune.heartbeatFrames or options.heartbeatFrames or 600))
if (runtime.beatFrames % heartbeat) == 0 then
```

**问题**：调参文件（纯文本 `key = value`）里 `0` 一律表示「关」（`devLog` / `skirt` 等都这么用）。
用户照直觉写 `heartbeatFrames = 0` 想关掉心跳时，Lua 里 `0` 是**真值**，`or` 不会落到 600，
`math.max(1, 0) = 1` ⇒ **每帧一条心跳**，一场长局能刷出几万行。
（**初版本文档在这里写错了一句**：我写成「写负数则变成负数取模（永不等于 0）」，实际
`math.max(1, -5)` 同样是 1 ⇒ 负数也是每帧一条。专家复核指出了这一处，已改。）

**最终落地**（按专家建议的写法，更短且覆盖 `0 < N < 1` 取整后关闭）：

```lua
local heartbeat = math.floor(tune.heartbeatFrames or options.heartbeatFrames or 600)
if heartbeat < 1 then heartbeat = 0 end
if heartbeat > 0 and (runtime.beatFrames % heartbeat) == 0 then
```

同时把逐链明细的间隔也统一成「`<= 0` = 关」，不再依赖 `n % 0 = nan` 静默失效
（专家意见：那是依赖底层取模行为，负值还会变成「反向周期」，语义说不清）。

**我的改法**（初版；复核后已按上面的写法简化）：

```lua
local tune = tune_values(runtime)
-- 调参文件 `heartbeatFrames = N` 优先，其次安装参数，最后 600。
-- **0 或负数 = 关掉心跳**（调参文件里 0 一律表示关，`devLog` / `skirt` 都是这个约定）。
local heartbeat = options.heartbeatFrames or 600
if tune.heartbeatFrames ~= nil then heartbeat = tune.heartbeatFrames end
heartbeat = heartbeat > 0 and math.floor(heartbeat) or 0
if heartbeat > 0 and (runtime.beatFrames % heartbeat) == 0 then
```

**想请专家判断**：

1. `0 = 关` 的选择是否合理？（另一种选择是「0 视为非法，回到默认 600」——但那等于**猜**用户
   的意图，我倾向不猜。）
2. 写法有没有更简洁且不丢语义的形式？`heartbeat > 0 and math.floor(heartbeat) or 0`
   依赖 Lua 的 `and/or` 惯用法，`math.floor` 返回 0 时也正确（Lua 里 0 为真），
   但可读性一般——是否值得换成显式 `if`？
3. 同一函数里还有一处 `(runtime.beatFrames % (options.debugDetailFrames or 240)) == 0`：
   若用户把它写成 0，Lua 里 `n % 0` 是 `nan`（不抛异常），比较恒假 ⇒ **静默关闭**，
   我认为这是可接受的（而且 `math.max(1, …)` 那种写法在这里反而会变成「每帧一条」，
   更糟），所以**没有动**。请专家确认这个判断。

### 2.4 #25 的主体改动本身（作者写的，我审过但没改）

`gains_of` 里原来只给「带约束的轴」算区间，另一轴留初值 `[0, 0]`；而求解器只要 `axisLimits`
打开就会两个分量都夹。于是 `flags = 0x35`（yawCon + angleCon，没有 pitchCon）的链 pitch 被锁在 0°，
长发前后甩全没了（实机表现：像一根焊丝）。改成：

```lua
local yawSet, pitchSet = false, false
if flag_set(flags, M.FLAG_YAW_CONSTRAINED) then
    local lo = (tonumber(params.minYaw) or 0) * relax
    local hi = (tonumber(params.maxYaw) or 0) * relax
    if lo ~= 0 or hi ~= 0 then yawMin, yawMax = bound(lo), bound(hi); axisLimits, yawSet = true, true end
end
if flag_set(flags, M.FLAG_PITCH_CONSTRAINED) then
    ... 同理 pitchSet ...
end
if axisLimits then
    if not yawSet then yawMin, yawMax = -1, 1 end
    if not pitchSet then pitchMin, pitchMax = -1, 1 end
end
```

（`bound(θ) = sin(θ)`，区间是「单位向量在该轴上的分量」，所以 ±1 就是「不夹」。）

我核过的等价性：双轴都约束的旧资产不变；「flag 有但 min/max 都是 0」时该轴也不夹，
而原来这种情况 `axisLimits` 根本没打开 ⇒ 行为一致。

---

## 3. 顺带请看一眼：#23 UV 整格归位（新几何处理，作者 PR）

**问题**：Source 按 wrap 采样，作者常把镜像那半边的 UV 整体挪一格（U 或 V +1）避开重叠，
源游戏看不出区别；HD2 里这些面**整片不显示**（实测：某模型一侧袖子 4620 个面 U 落在 1..2，
游戏里整只袖子消失，同 Unit 里 U 在 0..1 的手套袖口正常）。旁证：同一模型头部 7376 个面
V 落在 1..2，脸是好的 ⇒ 经验上是「U 出格会消失、V 出格不会」，**机制未查证**。

**做法**：按三角形平移整数格，三个顶点同量 ⇒ 三角形内插值不变，wrap 采样下等价。

```csharp
// 每个三角形的平移量取 UV 重心的整数部分
var du = -(int)MathF.Floor((va.U + vb.U + vc.U) / 3f);
var dv = -(int)MathF.Floor((va.V + vb.V + vc.V) / 3f);
triangles[t] = (Resolve(a, du, dv), Resolve(b, du, dv), Resolve(c, du, dv));

int Resolve(int vertex, int du, int dv)
{
    if (assigned.TryGetValue((vertex, du, dv), out var existing)) return existing;   // 同顶点同平移量只做一次
    var original = mesh.Vertices[vertex];
    var moved = du == 0 && dv == 0 ? original : original with { U = original.U + du, V = original.V + dv };
    int index;
    if (!claimed[vertex]) {           // 第一次用到这个顶点：原地改，占原下标
        claimed[vertex] = true;
        vertices[vertex] = moved;
        index = vertex;
    } else {                          // 同一顶点被「不同平移量」的三角形共用（镜像接缝）：复制一份
        vertices.Add(moved);
        index = vertices.Count - 1;
    }
    assigned[(vertex, du, dv)] = index;
    return index;
}
```

顶点类型是 C# record（`Position` / `Normal` / `U` / `V` / `IReadOnlyList<RetargetedInfluence>`），
`with { U = …, V = … }` 只改 UV，**骨骼权重与法线是同一份引用**。跨度超过 1 格的三角形
（平铺贴图）不处理。

**想请专家判断**：

1. 等价性论证有没有洞？（我核过的点：顶点共用时命中顺序无关、跨 section 共享顶点、
   负格 `floor(-0.5) = -1`、跨度 >1 跳过、无平移时原对象直返）
2. 顶点复制会不会有隐性代价：碎片化、索引位宽、下游物理骨子集按顶点算权重时被重复计入？
   （我查过：下游 `ExtraBoneSubset.Select` 在这之后调用，复制顶点带同一份权重引用，
   所以「用到了哪些骨」的集合不变；转换器里也没找到 65535 之类的顶点上限。）
3. 机制未证实（「U 出格会消失」），但这是**默认生效的全量几何改动**。我建议作者后续加个
   `HD2_NO_UV_TILE=1` 的 A/B 退路（仓库里同类实验性改动都有环境变量退路），
   **但这次没替他加**——请专家评：是否有必要在合并前就加？

---

## 4. 我明确不确定、希望被推翻的三点

1. **2.2 的保守选择**：单轴受限时让锥保持不生效（只改注释）。如果不该保守，正确的做法是什么？
2. **2.3 的 `0 = 关`** 是否应该推广成全局约定（所有「帧间隔类」开关都支持 0 = 关）？
   现在是「心跳支持、逐链明细静默失效」，不一致但都不崩。
3. **合并方式**：仓库其它 PR 都是 squash 成一条线性提交；这次 #25 因为与 #24 改同一段测试文件，
   我先本地解冲突再 merge，于是 `7b69fb9` 是一条标题为 `merge pr25` 的合并提交（不符合仓库
   的提交标题风格）。已推送、三个 PR 都已标记 merged。是否值得为了历史整洁去改写已发布的分支？

---

## 5. 附：怎么复现验证

```powershell
git clone https://github.com/AssestinaR/HD2ModManager && cd HD2ModManager
git checkout c6aaca6
dotnet build -c Debug            # 期望 0 警告 0 错误
dotnet test  -c Debug            # 期望 2224 通过 / 1 跳过 / 0 失败
python tools/physbone-harness/run_physbone_regressions.py   # 需要 luajit；期望 486 通过 / 0 失败
```

三个 PR 的原文与讨论：#23 / #24 / #25（`github.com/AssestinaR/HD2ModManager/pulls`）。
另有更长的审核记录在仓库 `docs/agent/pr-review-20261113.md`。
