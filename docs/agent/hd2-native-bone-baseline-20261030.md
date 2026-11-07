# HD2 原生骨骼 / 物理能力基线（不含 Lua、不改游戏本体）

> **⚠️ 本文只记录「原生能力基线」（骨骼宇宙 / 资产格式 / Lua 面）。**
> **实测结论、实施方案与交接技艺在 `docs/hd2-physics-roadmap-and-handover.md`。**
> 目的：把 L4D2 的物理骨骼（Source `$jigglebone`）移植到 HD2 之前，先量清楚
> **HD2 在最基础的条件下能支持哪些骨、哪些骨真的会动**。
> 约束：不写 Lua、不打运行时补丁、不改游戏本体文件；只考虑"换/加我们自己的资产"。
>
> 标记约定：`format-confirmed` = 直接从游戏数据里读出来的字节事实；
> `inference` = 由数据推出来的判断，未经游戏内实测。

---

## 0. 一句话结论

**"HD2 几乎没有物理骨骼"这句话只对玩家成立，对引擎不成立。**

- 引擎有一套**通用的、按 Unit 声明**的物理骨系统：全库 **52 个 Unit**、**3121 条物理骨条目**、
  单个 Unit **最多 32 根**、**骨名任意**、**106 种参数组合**。`format-confirmed`
- 玩家被卡死在 **6 根**，不是因为引擎只有 6 根，而是因为**玩家 avatar 是唯一一个共享资产**：
  它的 90 骨骨架和那份 12.3 MB 动画状态机是一份，谁都不能只改一半。
- 护甲 / 头盔 Unit **没有自己的骨架、没有状态机**，它们的可动骨**必须是 avatar 已有的那 90 根之一**。
  `format-confirmed`

⇒ **原生上限：玩家身上能产生二级运动的骨 = 6 根，全在躯干高度，参数不可调，加不了第 7 根。**

---

## 1. 取证方法（可复现）

工具：`tmp/gdscan`（临时只读扫描器，直接读 `D:\SteamLibrary\steamapps\common\Helldivers 2\data`，
走本仓 `GameDataPackageResolver` + `AdaptationPatchTocScanner`，不解包、不落盘游戏目录）。

```powershell
dotnet build tmp\gdscan\gdscan.csproj -c Release
tmp\gdscan\bin\Release\net8.0\gdscan.exe boneaudit tmp\gdscan\boneaudit.json
```

一次遍历 **3567 个包**，按类型抽出全部 `bones`（**3074** 个）与 `state_machine`（**2099** 个），
解析出骨名表、LOD 表、以及状态机里的物理条目表（`RagdollItem`：`bone_index + 9×float32 + 8 字节 hash + 2×uint32`），
再用**同一 FileId 的 `bones` 资源**把 `bone_index` 翻成骨名。

> 为什么能用 FileId 关联：Stingray 的一个 Unit 是**一族同 FileId 的子资源**
> （`unit` / `bones` / `state_machine` / `physics` / `ragdoll_profile` / `ik_skeleton` / `cloth`）。
> 玩家 avatar 的 90 骨、6 条物理条目、23 具布娃娃、以及 12 MB 动画状态机，
> FileId 全是 `0x4d1c334d294dfa97`。`format-confirmed`

产物：`tmp/gdscan/boneaudit.json`（gitignored，可随时重跑）。

---

## 2. HD2 到底有几套骨架

全库 3074 个 `bones` 资源里，**骨名集合有 555 种**。但只有三套和"玩家/护甲"有关：

### 2.1 玩家 avatar —— 唯一一套，90 根

`archive 18235e0c9ec0e636` / `FileId 0x4d1c334d294dfa97` / `bones` **1484 字节**

| 事实 | 值 |
|---|---|
| 骨数 | **90** |
| LOD 层数 | 6，每层骨数 `[90, 82, 59, 35, 32, 30]` |
| 同一 FileId 上的其它资源 | `unit` 21504 B、`state_machine` **12 305 624 B**、`ragdoll_profile` 82484 B、`ik_skeleton` 5096 B、`physics` 300 B |
| 全库唯一性 | 含 `support_mg` 的 `bones` 资源**全库只有这一个** |

完整 90 骨表见附录 A。分三类：

| 类 | 数量 | 说明 |
|---|---:|---|
| 真实身体骨 | 63 | `boss/spine1/spine2/chest/neck/head/hips`、四肢 + 手指 + twist + ball/toe |
| 引擎/辅助节点 | 9 | `StingrayEntityRoot`、`FbxAxisSystem_ConvertNode`、`skeleton`、`root`、`boss_aim`、`head_aim`、`weapon_aim`、`aim_weapon`、`climb_ref` |
| 装备挂点 / 披风 | 18 | `attach_*`(5)、`backpack`、`pistol`、`r/l_shoulderarmour`、`cape1..8`、`support`、`support_mg`、`sling`、`target_designator` |

### 2.2 护甲 / 头盔 Unit —— **没有骨架**

以 AF-52「禁锢」右臂 Unit `0x5c58b133661ee515`（16144 字节）为例，把它整份扫一遍找
`bonehash.txt` 命中的 32 位哈希，得到**唯一一段连续的节点名表（38 个节点，偏移 5336）**：

```
StingrayEntityRoot, FbxAxisSystem_ConvertNode, skeleton, root, boss, spine1, spine2, chest,
torso_culling, r_clavicle, r_shoulder, r_shoulder_twist, r_elbow, r_arm_culling,
r_hand_twist, r_hand, r_{pinky,ring,middle,index,thumb}_finger{1,2,3},
game_mesh, grp_torso_arm_r_male, g_torso_arm_r_male{_shadow,_LOD2,_LOD1,'' }
```

38 个节点里 **28 个命中 avatar 的 90 骨表**，另外 10 个是：

| 节点 | 性质 |
|---|---|
| `skeleton` | 场景图根节点（avatar 的 unit 里也有，但不在 `bones` 资源里） |
| `torso_culling`、`r_arm_culling` | **avatar 骨架里没有**的"剔除辅助骨" |
| `game_mesh`、`grp_torso_arm_r_male`、`g_torso_arm_r_male{_shadow,_LOD2,_LOD1,''}` | 网格 / LOD 分组节点 |
| （1 个空名） | 哈希不在 `bonehash.txt` 里的未知名 |

- 这个 FileId 在 `bones` 表和 `state_machine` 表里**都不存在**（AF-52 的 7 个 Unit 逐个查过，全是 0）。
  `format-confirmed`
- **附带一条对 L1 有利的证据**：护甲 Unit 里已经存在 `*_culling` 这种 avatar 骨架没有的节点，
  说明"Unit 里放非 avatar 骨名"这件事本身引擎是接受的 —— 只是那些节点不需要动，
  所以没人验证过它们会不会跟随父级姿态。
- 也就是说：护甲 Unit 的可动性**完全来自"按名字哈希挂到 avatar 骨上"**，
  它自己既没有动画源，也没有物理源。

### 2.3 引擎的通用"每 Unit 物理"—— 这才是 HD2 的物理能力真相

| 指标 | 值 |
|---|---|
| 带物理条目的状态机 | **348 条**（去重 FileId 后 **52 个 Unit**） |
| 物理骨条目总数 | **3121** |
| 单 Unit 条目数分布 | 1, 2, 3, 4, 6, 7, 8, 12, 13, 14, 20, 26, **32** |
| 不同骨名集合 | **39 种** |
| 不同参数组合 | **106 种** |
| 状态机体积 | 548 B（4 根旗骨）～ 46 KB（112 骨的角色）～ 12.3 MB（玩家，含全部动画） |

代表样本（`format-confirmed`）：

| 骨集合 | 数量 | 挂在哪 | 干什么 |
|---|---:|---|---|
| `banner_top/_01`, `banner_bottom/_01` | 4 | 旗帜 Unit | 旗面摆动 |
| `jiggle_chain_{l,r,mid,li,ri}_01..04` | 20 | 角色 Unit | 三束长发/触须链 |
| `jiggle1..jiggle32` | **32** | 某个 Unit | 全库最多的单 Unit 物理骨数 |
| `butt_jiggle`, `chest_*_jiggle`, … | 26 | 大型敌役 | 皮肉抖动 |
| `l_elbow/l_shoulder/r_hand/r_shoulder/l_hand/r_elbow` | 6 | 悬吊尸体类 Unit | **直接给主骨架骨上物理** |
| `tongue1..4` | 4 | 敌役 | 舌头 |
| `belly_entrails1_1..` | 13 | 敌役 | 内脏 |
| `antenna_short_01..`, `hose_01..`, `cables_inner/outer`, `flag_*`, `front_hip_plate1`, `l_torso_plate_front1_1` | 2–20 | 各种道具 / 敌人 | 天线、软管、线缆、旗、装甲片 |

**参数空间**（9 个 float32，取自 106 种组合的实测范围）：

| 槽位 | 实测范围 | 推断含义 |
|---|---|---|
| p0 | 0.5 ～ 10 | 权重 / 刚度 |
| p1 | −9.8，少数 −2 | 重力 |
| p2 | 0 ～ 1 | 线性阻尼 |
| p3 | 0 ～ 1 | 角阻尼 |
| p4 | 0.0349 ～ 3.1416 rad（2° ～ 180°） | 角度限值 |
| p5 | 0 或 1 | 未知 |
| p6/p7/p8 | 多为 0，`sling` 是 40/80/35 | 摆幅 / 轴向限值 |

`kind`（条目里那个 enum）分布：`kind=2` 1964 条、`kind=1` 849 条、`kind=3` 308 条。

**关键判断（`inference`）**：这套系统**不认识骨名**。它只认"这个 Unit 的 bones 资源第 N 根"。
所以引擎侧**没有 6 根上限、没有名字限制**。玩家的 6 根是**资产内容**的限制，不是引擎能力。

---

## 3. 玩家那 6 根，具体能干什么

`FileId 0x4d1c334d294dfa97` 的 `state_machine` 里 `ragdoll_count = 6`，逐条解析：

| bone_index | 骨名 | 父骨 | 世界坐标 z | p0 权重 | p2 阻尼 | p4 角度限值 | p6/7/8 | 社区评价 |
|---:|---|---|---:|---:|---:|---:|---|---|
| 54 | `attach_samplepouch` | spine1 | 1.112 | 1 | 0 | 10° | — | 下半身好 |
| 26 | `attach_intelpad` | spine1 | 1.123 | 0.5 | 0 | 10° | −10/10/0 | 下半身好 |
| 68 | `target_designator` | spine1 | — | 2.5 | 0.8 | 10° | — | 只水平动 |
| 63 | `sling` | spine2 | — | 1.5 | 0.8 | 10° | **40/80/35** | 乱动 |
| 71 | `support_mg` | chest | 1.404 | 5 | 0.8 | 5° | — | 整体不错 |
| 81 | `support` | chest | — | 5 | 0.8 | 5° | — | 整体不错 |

（父骨两条独立证据：① 状态机骨序 vs 场景图节点序一致；② Modding Wiki《Jiggle Bones》原表。
世界坐标取自 AQ SDK 的 `avatar_source.json`。`format-confirmed`）

### 3.1 三条硬限制

1. **位置全在躯干**：1.11 ～ 1.40 m，即腰椎到胸口。
   **头、颈、髋、肩、肘、腕、膝上一个都没有。**
2. **角度限值很小**：5°～10°（只有 `sling` 是 40/80/35）。这是"装备晃动"的量级，不是"头发飘"的量级。
3. **参数不可改**：参数烘焙在状态机资源里；Lua 侧只有
   `Unit.update_jiggle_bone_data` / `update_jiggle_bone_orientation` 这种"驱动"接口，
   没有"新增第 7 根"或"改参数"的接口。`format-confirmed`（见 `docs/hd2-armor-physics-research.md` §10.1）

### 3.2 一个有用的细节：6 根里只有 3 根有动画

社区 SDK 的"公开动画骨架"是 **63 根**（`avatar_source.json`，`schema: HD2AvatarSourceSkeleton1`），
是 90 骨的真子集，缺 27 根。缺的里面包含：

```
support, sling, target_designator          ← 3 根物理骨
cape1..cape8, attach_knife, backpack, pistol,
r/l_shoulderarmour, attach_hand_r/l, attach_weapon, attach_cam_1,
boss_aim, head_aim, weapon_aim, aim_weapon, climb_ref,
StingrayEntityRoot, FbxAxisSystem_ConvertNode
```

**`inference`**：`support` / `sling` / `target_designator` 没有动画通道 ⇒ 它们的姿态**100% 由物理解算给出**，
正是"纯物理骨"该有的样子；而 `support_mg` / `attach_intelpad` / `attach_samplepouch`
有动画通道 ⇒ 是"动画 + 物理叠加"，适合挂装备、不适合当飘带。
（这一点值得在游戏内实测确认：若成立，这 3 根是最干净的移植目标。）

---

## 4. 社区 SDK（Blender 插件）给出了什么边界

| 位置 | 内容 | 对我们的意义 |
|---|---|---|
| `HD2SDK-CommunityEdition-3.8.0/stingray/bones.py` | 只序列化 4 张表：`NumNames / NumLODLevels / BoneHashes / LODLevels / Names` | **bones 资源里没有父子关系、没有物理参数**。父链在 Unit 的场景图里，物理参数在状态机里 |
| `hashlists/bonehash.txt` | 4913 条 32 位哈希→骨名 | 护甲 Unit 没有 bones 资源，只能靠这张表反查节点名 |
| `hashlists/friendlynames.txt` | 4036 条资产路径 | 只能查资产路径，查不到 Unit/骨 |
| `hashlists/typehash.txt` | 类型哈希表 | `bones=0x18dead01056b72e9`、`state_machine=0xa486d4045106165c`、`cloth=0xd7014a50477953e0`、`ragdoll_profile=0x1d59bd6687db6b33`、`ik_skeleton=0x57a13425279979d7` |
| Wiki《Jiggle Bones》 | *"Currently, the SDK does not support adding custom jiggle bones to models. However, we can decently fake the effect using existing bones on the Helldiver player rig."* | 与上面的数据完全吻合 |

### 4.1 AQ 版（Estecsky 魔改）是怎么"支持 PhysBone"的 —— 它就是 Lua

`tmp/pb-ref/sdk-raw/`（AQ 版 SDK 的独立模块）里有三件事：

1. `hd2_system_rig_format.py` —— `HD2IRG1` 二进制格式：`source_bones[]`（名字 + 父索引 + rest_local）
   + 每 Unit 的 `profiles[]`（`target_bones[]`，每条带 `palette_slot` / `source_index` /
   `translation_scale` / 一堆 flag）。flag 里有 `inherit_parent_pose`、`source_parent_space`、
   `scene_graph_index`、`diagnostic_motion`、`observe_only`、`dynamic_pose_match` ——
   **这就是"自定义追加骨跟随父级姿态"的完整语义**。
2. `hd2_system_unit_rig_profiles.py` —— 从已保存的 Unit 场景图生成 profile；
   注释里明写：*"Scene-node authoring uses the proven Lua writer."*
3. `hd2_system_independent_bone_closure.py` —— 注释：*"All body pieces use the same authored public
   animation skeleton, including unweighted IK/twist/grip landmarks."*

⇒ **AQ 版的"自定义外部动画骨 / 独立辅助骨 / 自动整合 PhysBone / 身体共享物理解算"，
全部依赖一个 Lua 写入器在运行时把姿态写进场景图节点。** 这正是我们要避免的那一层。
我们自己的 `docs/hd2physbone-embedded-patch-protocol.md`（`HD2PBX01` 容器 + ReShade/addon64 读取）
是同一层的另一种实现。

---

## 5. 能力边界（三层）

| 层 | 手段 | 玩家身上能加物理骨吗 | 现状 |
|---|---|---|---|
| **L0 原生** | 只替换/新增我们自己的 Unit 资产（护甲、头盔、模型） | **不能**。能动的只有 avatar 那 90 根，其中 6 根有物理 | ✅ 已验证（本轮） |
| **L1 待验证** | 给**护甲 Unit 自己**内联 `bones` + `state_machine`（游戏里的物理道具就是这么干的） | **也许能** | ❓ 一次游戏内实验就能定性 |
| **L2 已证明** | 额外骨 + 运行时写入器（AQ 的 Lua / 我们的 HD2PBX01 + ReShade） | 能，任意数量 | ✅ 社区已跑通，但要装插件 |

### 5.1 为什么 L0 加不了第 7 根

要把一根新骨变成"会动的物理骨"，需要三样同时成立：

1. 骨存在于 **avatar 的 90 骨**里（护甲 Unit 只能挂到已有名字上）；
2. avatar 的**状态机**里有一条指向它的物理条目；
3. 参数合理。

第 2 步只有两条路，都很贵：

- **替换 avatar 的 `bones` + `state_machine`**：那个状态机是 **12.3 MB**，里面装着玩家的**全部动画**。
  换掉它 = 换掉全部动画，等于重做角色。不可行。
- **只替换 `bones`**：新骨存在但没有动画、没有物理条目 ⇒ 静止。无用。

### 5.2 L1 为什么值得一试（本轮最重要的新线索）

游戏里 **52 个 Unit** 用的是"Unit 自己带 `bones` + `state_machine`"这个结构，例如
`0x846bc06c6b213a06`（112 骨，含 `spine2/chest/hips/l_elbow/r_hand` 这些**和玩家同名**的骨
＋ 6 根 `*_plate1` 物理骨，状态机 46 KB）。

**假设**：如果我们把护甲 Unit 改造成同样的结构 —— 保留现有 38 个 avatar 同名节点（继续按名字挂到 avatar 骨上），
**再加 N 根自己的辅助骨 + 一份只有物理条目的极小状态机** —— 那么：

- 辅助骨的父节点是 avatar 同名节点 ⇒ 继承父级运行时姿态（`inference`）；
- 物理条目让辅助骨在父级姿态上再叠一层二级运动 ⇒ **就是 PhysBone，但是原生的**；
- 代价：护甲 Unit 的体积从 16 KB 涨到 ~30 KB，不需要 Lua，不需要改本体。

**风险 / 未知**：
- 护甲 Unit 是"被 avatar 实例化并挂上去"的，它自己的状态机**会不会被 tick**？（未验证）
- 护甲 Unit 的节点名**不在** avatar 骨架里时，引擎是"留在模型空间"还是"跟随父节点"？（未验证）
- 一个 patch 归档里能不能带 `bones` / `state_machine` 类型的条目？（未验证，技术上应该可以）

---

## 6. 对 L4D2 移植的直接含义

### 6.1 源侧规模（实测，前一轮扫描 `tmp/physbone-recon/jiggle-scan.log`）

- 扫描 **6566 个 mdl**，其中 **147 个**有 `$jigglebone`
- 每个模型的 jiggle 骨数：**中位数 18**，最多 **105**
- 分桶：1–2 根 21 个 / 3–5 根 20 个 / 6–10 根 25 个 / 11–30 根 12 个 / **31+ 根 69 个**

### 6.2 用 L0（6 根躯干骨）能覆盖的

| 源侧部位 | 能否落地 | 用哪根 |
|---|---|---|
| 披风 / 背带 / 背包带 | ✅ | `support_mg`（chest，p0=5 偏硬） |
| 腰侧挂件、腰带、枪套 | ✅ | `attach_intelpad` / `attach_samplepouch`（spine1） |
| 裙摆**上半**、围裙 | ⚠️ 勉强 | `target_designator` / `sling`（spine1/2） |
| 裙子下摆、长裙整体 | ❌ | 没有髋/腿上的物理骨 |
| 头发、双马尾、呆毛 | ❌ | **头/颈上没有物理骨** |
| 长袖、袖口飘带 | ❌ | 肩/肘/腕上没有物理骨 |
| 尾巴、触手 | ❌ | 没有髋/尾骨 |

⇒ **结论：L4D2 那 147 个模型里，绝大多数 jigglebone 在 L0 下无处可落。**
真正能原样移植的只有"靠近胸口/腰椎、幅度小"的挂件类。

### 6.3 还有一个必须提前知道的坑

前一轮已记录：**Nick / Ellis 的长袖飘带在源 bind pose 里就是"伸出去 1.7 m"的形状**
（`docs/hd2-batchtool-sdk-findings.md` §8.5）。不带物理驱动时它们会**保持这个静态形状**。
所以"没有物理"不是"不动"，而是"定在了一个明显错误的位置"。这决定了：
**在物理方案定下来之前，这类模型的袖子必须先按部位裁掉或按未约束姿态重摆。**

---

## 7. 建议的下一步（三选一，按成本排序）

| 编号 | 实验 | 成本 | 能定什么 |
|---|---|---|---|
| **E1** | 给一个护甲 Unit 内联 `bones` + 极小 `state_machine`（只有物理条目），进游戏看那几根辅助骨动不动 | 1 天 | **决定 L1 是否存在**。成了就是原生 PhysBone，整条路线重写 |
| **E2** | 只加一根辅助骨、不加物理，权重刷一小块布，看它是否跟随父骨 | 半天 | E1 的前置：区分"没被 tick"还是"没跟随" |
| **E3** | 用现有 6 根骨真做一次移植（选一个裙摆/披风明显的模型），进游戏量视觉效果 | 1 天 | 决定 L0 的"够用线"在哪 |

建议顺序 **E2 → E1 → E3**：E2/E1 都很便宜，且直接决定要不要走 L2（Lua）。
E3 无论 E1 结果如何都需要，因为它给的是"用户能不能接受"的答案。

---

## 附录 A：玩家 avatar 完整 90 骨表

`archive 18235e0c9ec0e636` / `FileId 0x4d1c334d294dfa97` / `bones`，LOD 骨数 `[90,82,59,35,32,30]`

| # | 骨名 | hash | 类别 |
|---:|---|---|---|
| 0 | `StingrayEntityRoot` | `0x4a182741` | 引擎 |
| 1 | `FbxAxisSystem_ConvertNode` | `0x7f30e61c` | 引擎 |
| 2 | `boss` | `0x9b115563` | 身体 |
| 3 | `spine1` | `0x58d886fb` | 身体 |
| 4 | `hips` | `0x668f6a68` | 身体 |
| 5 | `boss_aim` | `0x3aa35d36` | 辅助 |
| 6 | `spine2` | `0x5a4bbaa2` | 身体 |
| 7 | `chest` | `0x37ef0820` | 身体 |
| 8 | `r_clavicle` | `0xb2bcd839` | 身体 |
| 9 | `l_clavicle` | `0xc4787b4e` | 身体 |
| 10 | `l_foot` | `0x8eb19d6a` | 身体 |
| 11 | `l_shoulder` | `0x03752c98` | 身体 |
| 12 | `r_shoulder` | `0x1620b2ce` | 身体 |
| 13 | `r_elbow` | `0x31e34dac` | 身体 |
| 14 | `r_hand` | `0x248004f5` | 身体 |
| 15 | `l_elbow` | `0x1ca2cd66` | 身体 |
| 16 | `attach_hand_r` | `0x4fdd3337` | 挂点 |
| 17 | `l_hand` | `0x5c0c68d3` | 身体 |
| 18 | `r_foot` | `0x46ddad11` | 身体 |
| 19 | `attach_hand_l` | `0x4194399d` | 挂点 |
| 20 | `r_knee` | `0x3ac8154a` | 身体 |
| 21 | `r_thigh` | `0x748f4281` | 身体 |
| 22 | `climb_ref` | `0x269b7660` | 辅助 |
| 23 | `weapon_aim` | `0x89723b1e` | 辅助 |
| 24 | `l_knee` | `0x8fd2dfec` | 身体 |
| 25 | `l_thigh` | `0x8e1c9de6` | 身体 |
| 26 | `attach_intelpad` | `0x8464fb0e` | 挂点 / **物理** |
| 27 | `root` | `0xbccf91e5` | 引擎 |
| 28 | `aim_weapon` | `0x17cc8a35` | 辅助 |
| 29 | `attach_weapon` | `0xb298a52a` | 挂点 |
| 30 | `r_ball` | `0x68bbeb52` | 身体 |
| 31 | `l_ball` | `0xecd5bb39` | 身体 |
| 32 | `neck` | `0xe9ecca21` | 身体 |
| 33 | `head_aim` | `0x8ae2e60a` | 辅助 |
| 34 | `head` | `0x8c5570c9` | 身体 |
| 35 | `r_hand_twist` | `0x9310b36c` | 身体 |
| 36–40 | `r_{thumb,index,middle,ring,pinky}_finger1` | — | 身体 |
| 41 | `cape2` | `0xf60f949a` | 披风 |
| 42 | `cape3` | `0x6da25ec6` | 披风 |
| 43 | `l_middle_finger1` | `0xe4767875` | 身体 |
| 44 | `attach_knife` | `0x96f77332` | 挂点 |
| 45 | `r_shoulder_twist` | `0x82403d12` | 身体 |
| 46 | `cape7` | `0xba2e9532` | 披风 |
| 47 | `l_pinky_finger1` | `0x7a19e054` | 身体 |
| 48 | `cape6` | `0x23d310e7` | 披风 |
| 49 | `cape1` | `0x8f5f24ee` | 披风 |
| 50 | `cape5` | `0x41225fd0` | 披风 |
| 51 | `l_ring_finger1` | `0x7881f640` | 身体 |
| 52 | `cape4` | `0x24e80249` | 披风 |
| 53 | `cape8` | `0x19079487` | 披风 |
| 54 | `attach_samplepouch` | `0x000922d7` | 挂点 / **物理** |
| 55 | `backpack` | `0x7778af66` | 挂点 |
| 56 | `l_index_finger1` | `0xc7d1ff28` | 身体 |
| 57 | `pistol` | `0xccf501c7` | 挂点 |
| 58 | `l_thumb_finger1` | `0xb7d89ff0` | 身体 |
| 59–62 | `l_pinky_finger2`, `l_thumb_finger2`, `l_shoulder_twist`, `r_thumb_finger2` | — | 身体 |
| 63 | `sling` | `0xec864c61` | 挂点 / **物理** |
| 64 | `r_shoulderarmour` | `0x062582db` | 挂点 |
| 65–67 | `l_index_finger2`, `r_thumb_finger3`, `r_index_finger2` | — | 身体 |
| 68 | `target_designator` | `0x019cd5f2` | 挂点 / **物理** |
| 69–70 | `r_index_finger3`, `l_shoulderarmour` | — | 身体 / 挂点 |
| 71 | `support_mg` | `0x07fa5ada` | 挂点 / **物理** |
| 72–80 | `r_middle_finger2`, `l_middle_finger2`, `l_ring_finger2`, `l_hand_twist`, `r_pinky_finger3`, `r_pinky_finger2`, `r_ring_finger3`, `r_ring_finger2`, `r_middle_finger3` | — | 身体 |
| 81 | `support` | `0xe80d437b` | 挂点 / **物理** |
| 82 | `attach_cam_1` | `0x9af673da` | 挂点 |
| 83–89 | `r_toe`, `l_toe`, `l_thumb_finger3`, `l_pinky_finger3`, `l_ring_finger3`, `l_middle_finger3`, `l_index_finger3` | — | 身体 |

## 附录 B：玩家 avatar Unit 的场景图节点序（89 个，可据此推父链）

`FileId 0x4d1c334d294dfa97` 的 `unit` 资源，节点名表在偏移 14552：

```
skeleton, root, climb_ref, attach_cam_1,
boss, boss_aim, hips, r_thigh, pistol, r_knee, r_foot, r_ball, r_toe,
l_thigh, l_knee, l_foot, l_ball, l_toe,
spine1, attach_samplepouch, attach_intelpad, target_designator, attach_knife,
spine2, sling, chest, backpack, support_mg, support, attach_weapon, aim_weapon,
cape1..cape8,
l_clavicle, l_shoulderarmour, l_shoulder, l_shoulder_twist, l_elbow, l_hand, attach_hand_l,
l_{pinky,ring,middle,index,thumb}_finger{1,2,3}, l_hand_twist,
r_clavicle, r_shoulderarmour, r_shoulder, r_shoulder_twist, r_elbow, r_hand_twist, r_hand,
r_{pinky,ring,middle,index}_finger{1,2,3}, attach_hand_r, weapon_aim,
r_thumb_finger{1,2,3}, neck, head, head_aim
```

这个顺序本身就是父链的强证据（父节点必在子节点之前，同一父的子节点相邻）：
`attach_samplepouch/attach_intelpad/target_designator/attach_knife → spine1`、
`sling → spine2`、`backpack/support_mg/support/cape1..8 → chest` —— 与 Wiki 原表逐条吻合。

## 附录 C：本轮否掉的三个"看起来可行"的猜想

1. **"披风的布料求解器可以借给护甲"** —— 不行。全库 `cloth` 资源**只有 3 个**
   （玩家两件披风 + 1 个其它），且 Lua 侧没有 `Cloth` 接口。披风是**独立 Unit + 独立 66/44 骨 + 独立 Havok Cloth**。
2. **"布娃娃可以当物理用"** —— 不行。`ragdoll_profile` 是 23 具 Havok 刚体的**全身**布娃娃，
   只在 `can_ragdoll`（被击倒/死亡）时激活，站立行走时不参与。
3. **"状态机里再加一条就行"** —— 对**道具**成立，对**玩家**不成立：
   玩家的状态机是那份 12.3 MB 的动画资源，改它等于重做全部动画。

---

## 附录 D：社区早期"只靠官方骨"的 L0 样本解剖（Milltina → RS-100 制裁者）

样本：`C:\Users\Administrator\Downloads\VRchat_Milltina_Asmodeus 可脱穿 替换RS-100 制裁者 (2).7z`
（2026-07-13 打包，作者未署名）。用户反馈：**胸部有摇晃，但效果很差；角色不动、只扭镜头也在晃。**

### D.1 包里有什么（`format-confirmed`）

| 项 | 内容 |
|---|---|
| 归档 | 10 个 patch（`9ba626afa44a3aa3.patch_0..9`），全部挂在 Lua boot 载体归档下 |
| 选项 | 本体 / 上衣 / 丝袜 / 内裤 / 鞋子 / 尾巴 / 翅膀 / 大衣健壮 / 下摆短 / 下摆长 |
| 资产类型 | **只有 `unit` 和 `bones`**。没有 `state_machine`、没有 `physics`、没有 `cloth` |
| Lua / 插件 / 外置文件 | **完全没有**（包内只有 patch、`.gpu_resources`、`icon.png`、`manifest.json`） |

⇒ 这是一个纯 L0 样本：**一个新骨都没加**。

### D.2 它到底动了哪根骨（`format-confirmed`）

本体 patch 的 9 个几何 Unit 里，躯干 Unit（`0x4ebbfde8692577ff` / `0xecc8df6ccc7721bd`，Slim/Stocky 两版）的 LOD0 权重分布：

| 骨 | 命中顶点 | 权重和 | 加权质心 | 权重分位 |
|---|---:|---:|---|---|
| `chest` | 9781 | 6582.6 | (0.028, −0.011, 0.181) | ≤0.25:2058 / >0.75:**5831** |
| **`support_mg`** | **6401** | **3252.3** | (0.028, **+0.209**, 0.060) | ≤0.25:3117 / >0.75:**2840** |
| `l_shoulder` | 3779 | 3232.7 | (−0.213, −0.017, 0.134) | >0.75:3156 |
| `neck` | 1578 | 496.9 | (0.029, −0.040, 0.303) | ≤0.5:1470 |
| `r_shoulder` | 782 | 231.6 | | |
| `spine2` | 444 | 88.3 | | |
| `head` | 206 | 66.0 | | |
| `hips` | 304 | 35.4 | | |

**结论：胸部那一块被刷到了 `support_mg` 上，其中 2840 个顶点的权重 > 0.75（近乎刚性跟随）。**
（该 Unit 的节点表与 avatar 完全同帧：`chest` 在 (0, −0.029, 1.344)、`neck` 在 (0, −0.041, 1.550)、
`head` 在 (0, −0.025, 1.691)，与 `avatar_source.json` 逐位吻合 ⇒ 坐标系没有镜像。
`+Y = 前方`：`backpack` 在 y=−0.184、`cape1..8` 在 y=−0.14…−0.41（都在背后），
`attach_weapon` 在 y=+0.803、`aim_weapon` 在 y=+2.014（都在身前）。
所以 `support_mg` 质心 y=+0.209 是**身前**，就是胸。）

**附带发现：`attach_intelpad` 的残留权重。** 另外 6 个部件里，几乎**每个顶点**都带一点点
`attach_intelpad` 权重（命中数 ≈ 部件总顶点数）：

| 部件 | `attach_intelpad` 命中 / 权重和 | 平均权重 |
|---|---|---|
| 大衣健壮 | 23556 / 2790.2 | 0.118 |
| 下摆长 | 16605 / 825.5 | 0.050 |
| 下摆短 | 14037 / 596.0 | 0.042 |
| 本体（腿） | 17292 / 3374.7 | 0.195 |
| 翅膀 | 6266 / 459.1 | 0.073 |
| 尾巴 | 5518 / 2945.1 | 0.534 |

`attach_intelpad` 也是 6 根抖动骨之一 ⇒ **裙子/大衣/翅膀/尾巴都跟着一起抖**。
这是权重转移（transfer weights）留下的残渣，不是有意设计。

### D.3 为什么"只扭镜头也在晃"

三条原因叠加，且每一条都能从数据里读出来：

1. **挂错父骨。** `support_mg` 是 `chest` 的子骨，而 **`chest` 正是 HD2 用来做瞄准的那根骨**
   （旁边就有 `boss_aim` / `aim_weapon` / `weapon_aim` / `head_aim` 四个瞄准辅助节点）。
   转镜头 = 转 chest = 给弹簧一个**角速度冲击**。
   真实胸部物理应当响应**世界空间的躯干加速度**，而不是瞄准旋转。所以"站着不动只转视角"成了最强的激励。
2. **支点离身体 30 cm。** 6 根抖动骨的挂点全在**体外**（它们是装备挂点）：

   | 骨 | 挂点世界坐标 | 离最近体轴的水平距离 |
   |---|---|---:|
   | `attach_intelpad` | (0.098, −0.145, 1.123) | **0.152 m** |
   | `attach_samplepouch` | (0.155, −0.089, 1.112) | **0.166 m** |
   | `support` | (−0.022, −0.226, 1.397) | 0.198 m |
   | `sling` | (0.154, 0.150, 1.100) | 0.236 m |
   | `target_designator` | (−0.235, 0.013, 1.105) | 0.239 m |
   | **`support_mg`** | (−0.181, −0.270, 1.404) | **0.301 m** |

   作者选的 `support_mg` 是**离身体最远**的那一根。绕一个体外 30 cm 的点旋转，5° 就能让胸部顶点
   位移 2.6 cm 且方向是"横移 + 整体转"，不是"软组织形变" —— 视觉上就是"胸口一块板在滑"。
3. **参数是装备参数，不是肉参数。** `support_mg` 烘焙值：
   `weight=5, gravity=−9.8, linDamp=0.8, angDamp=0.5, 角度限值=5°`。
   刚度大（5）、行程小（5°）。而且这个 `−9.8` 是**沿骨的某个固定局部轴**施加的，
   而 `support_mg` 的静置朝向是斜的（其模型空间旋转矩阵的第一行
   `(−0.042, 0.507, 0.861)` ⇒ 局部 X 轴与世界竖直只差约 31°，局部 Z 轴差 60°+）。
   换句话说，这 6 根骨各自的"重力方向"在世界空间里都不一样 ——
   社区对它们的描述（`target_designator` "只水平动"、`sling` "乱动"）本身就是这件事的症状。

### D.4 这个样本给我们的三条设计规则

1. **L0 嫁接在机制上是成立的**：引擎确实会驱动，不需要 Lua、不需要新骨。这是本轮最有力的正面证据。
2. **但 L0 的质量上限由"挂点位置"决定，不由参数决定**（参数改不了）。
   任何蒙到抖动骨上的网格，都会绕一个**体外 15–30 cm** 的支点旋转。
   ⇒ L0 只适合"本来就挂在那个挂点上的东西"（腰侧挂件、背带、裙摆上半），
   不适合"要看起来像软组织/布料形变"的东西。
3. **绝对不要用 `support_mg`**：它离体轴最远（0.301 m）且是瞄准链的直接下游。
   要用就用离体轴最近的 `attach_intelpad`（0.152 m）或 `attach_samplepouch`（0.166 m），
   并且**必须和锚点骨混合权重**（`chest`/`spine1` 给大头），不能像这个样本那样让 2840 个顶点
   > 0.75 权重刚性挂在抖动骨上。

### D.5 复现方式

```powershell
7z x "<rar>" -otmp\milltina
tmp\m2-probe\bin\Release\net8.0\m2probe.exe nodes   "tmp\milltina\本体\9ba626afa44a3aa3.patch_0" HD2ModManager\Resources\AssetMetadata\bonehash.txt
tmp\m2-probe\bin\Release\net8.0\m2probe.exe weights "tmp\milltina\本体\9ba626afa44a3aa3.patch_0" HD2ModManager\Resources\AssetMetadata\bonehash.txt
```

`m2probe weights` 是本轮给 `tmp/m2-probe` 加的命令：按 `Type=6`（骨索引）+ `Type=7`（权重）
经 `BoneInfo.Remaps → RealIndices → TransformNameHashes` 还原每根骨的实际权重分布。
`m2probe nodes` 额外打印每个节点的世界静置位置（取自 `TransformInfo.Matrices[12..14]`）。

### D.6 对照实验：只换骨、不动权重（v2 已修正，待游戏内实测）

要判断"难看"是**骨选错了**还是**结构性无解**，最干净的做法是把两件事拆开测：
只改调色板（`RealIndices` + 配套的 `BoneMatrices`），**顶点权重、坐标、UV、材质、`.gpu_resources` 全部逐字节不变**。

| 变体 | 改挂到 | 挂点→胸部权重点质心 | weight / 角度限值 / 摆幅 | 估算表面位移 | 用途 |
|---|---|---:|---|---:|---|
| V0 | `support_mg`（原样） | 0.553 m | 5 / 5° / — | ~4.8 cm | 基线 |
| V1 | `sling` | **0.186 m** | 1.5 / 10° / 40,80,35 | ~3.2 cm | 支点最近、摆幅最大 |
| V2 | `attach_samplepouch` | 0.342 m | 1 / 10° / — | ~5.9 cm | 最软 |
| V3 | `attach_intelpad` | 0.374 m | 0.5 / 10° / — | ~6.5 cm | 最软 + 最低刚度 |
| V4 | `support` | 0.471 m | 5 / 5° / — | ~4.1 cm | 同刚度但支点在正中线 |
| **V5** | `chest`（**零物理**） | — | — | 0 | **分离"晃"与"蒙皮本身难看"** |

产出：`tmp/milltina-test/<变体>/9ba626afa44a3aa3.patch_{0,3}`（每个变体只有 456 KB，侧车用硬链接）。

判定逻辑：

- 五个有物理的**都**在"只转镜头"时晃 ⇒ **结构性**（6 根抖动骨全挂在 `chest/spine1/spine2` 下游，
  而这条链正是瞄准驱动的那条），换骨救不了，L0 的上限就此封顶。
- 只有 V0 晃得离谱、V1 明显更好 ⇒ **支点距离**问题，L0 还有优化空间。
- V5 和 V0 只差"晃"⇒ 蒙皮本身没问题，锅全在物理；V5 也难看 ⇒ 锅在权重/蒙皮。

### D.7 一轮无效实验换来的格式知识：调色板槽是「索引 + 骨骼矩阵」一对

第一版只改了 `RealIndices`，进游戏的结果是**乳房被整体搬到奇怪位置**
（`support` → 背后扇子骨、`sling` → 腰部、`chest` → 小腹前、`attach_samplepouch` → 右肘后），
而"晃"几乎没有区别。原因是漏了一半：

```
每个 BoneInfo（对应一个材质 / LOD 组）里有两张等长的表：
  RealIndices[i]  uint32     第 i 个调色板槽 → TransformInfo.NameHashes 的下标
  BoneMatrices[i] 64 字节    第 i 个调色板槽的 4x4 骨骼矩阵

实测不变式（残差 0，21 个 Unit × 全部槽验证）：
  BoneMatrices[i] · TransformInfo.Matrices[RealIndices[i]]  ==  X
  ⇒ BoneMatrices[i] = X · inverse(world_i)          （X = 该 BoneInfo 的网格节点变换，组内恒定）
```

蒙皮是 `v' = v · BoneMatrices[i] · world_bone(t)`；静止姿态下 `world_bone(t) = world_i`，
于是退化成 `v' = v · X` —— 网格停在原位。

**只改索引不改矩阵**，静止姿态就变成 `v' = v · X · inverse(world_A) · world_B`：
把这块网格**整体搬进骨 B 的坐标系**（绕骨 A 的原点转，再落到骨 B 的位置）。
所以乳房才会跑到别的骨头上，而"晃"的幅度因为杠杆臂被搬到 ~0.5 m 而**几乎不变** —— 这解释了
"五个变体晃动看不出区别，但位置全错"。

正确写法：`BoneMatrices_B = BoneMatrices_A · world_A · inverse(world_B)`，与索引同时写。

**旁证**：本仓 `CanonicalInverseJointMatrixCompiler.Build` 算的正是
`mesh · inverse(bone)`（`inverse(transpose(mesh) * transpose(bone))` 再转置 = `mesh · inverse(bone)`），
与上面这条不变式一致 —— 也就是说生产链路本来就是对的，错的只是我这个临时探针。

### D.8 一条可复用的工程结论

**"改调色板"是比"改顶点权重"便宜一个数量级的实验手段，但它是一个二元操作。**
`RealIndices` 与 `BoneMatrices` 必须成对改；只改索引 = 把网格搬进另一根骨的坐标系。
把这一对改对之后，等于把"用这个槽的**所有**顶点"整体改挂到另一根骨上，
不需要重编码顶点流、不需要重算归一化、不需要碰 `.gpu_resources`
—— 做"同一套权重换不同骨"的 A/B 时，这是唯一不会引入第二个变量的做法。
（`tmp/m2-probe` 的 `repoint` 命令按此实现，`matrel` 用来校验静止姿态不变。）

### D.9 游戏内实测结果（v2，2026-10-30）

| 变体 | 骨 | 轴心 | 静止位置 | 晃动表现 |
|---|---|---|---|---|
| **V5** | `chest`（零物理） | — | **完全正常** | **不晃** |
| V0 | `support_mg` | (0.181, −0.270, 1.404) 偏右 18 / 靠后 24 cm | 正常 | 斜着晃 |
| V1 | `sling` | (0.154, 0.150, 1.100) 侧挂武器处 | **被拉到武器上** | **与武器晃动完全一致** |
| V2 | `attach_samplepouch` | (0.155, −0.089, 1.112) 右后腰 | 正常 | 比基线略大，偏"平着晃" |
| V3 | `attach_intelpad` | (0.098, −0.145, 1.123) 右后腰更低 | 正常（略低） | 像被小腹一点向左下/向右下拉 |
| V4 | `support` | (−0.022, −0.226, 1.397) **正中线**、胸高 | 正常 | "揉馒头" |

三条结论：

1. **V5 正常 ⇒ 蒙皮与骨骼矩阵完全正确，全部问题都在物理侧。** 这也反向验证了 D.7 的矩阵修正是对的。
2. **五个有物理的变体，在角色不动、只转镜头时全都晃** ⇒ **"只转镜头就晃"是结构性的**，
   与选哪根骨无关。原因：6 根抖动骨全是 `chest`/`spine1`/`spine2` 的子骨，
   而这条链正是瞄准驱动的那条；L0 能拿到的驱动信号永远是"瞄准角速度"，不是"世界空间加速度"。
3. **每个变体的手感差异，完全由"轴心在哪"决定**（这是 L0 唯一的旋钮）：
   轴心在右后 → 斜着晃；轴心更低 → 向下拉；轴心在正中线且与胸同高 → 左右对称地转（"揉馒头"）。
   六根里**只有 `support` 在正中线上**，所以它是几何上最接近"胸的轴心"的一根。

### D.10 新规则：重指向只保证"在**作者写死的 bind 姿态**下不位移"

V1 暴露了一条 D.7 没覆盖的前提。`matrel` 已验证 V1 的 `BoneMatrices[i]·world_i` 逐槽恒定，
即"在 Unit 自己写死的节点变换下"网格一定停在原位；但进游戏后乳房仍被拖到武器上。
⇒ **`sling` 的运行姿态与 Unit 里写死的姿态不一致**（它是侧挂武器的载体，被武器系统摆过去）。

所以重指向的安全条件是两条，不是一条：

```
① BoneMatrices_B = BoneMatrices_A · world_A · inverse(world_B)        （矩阵一致）
② 该骨的「运行时姿态」== Unit 里写死的 TransformInfo 姿态             （姿态一致）
```

②不成立时，网格会被拖到该骨的真实运行位置。`sling` 就是这种情况 ⇒ **它从可用骨名单里出局**。
可用骨实际只剩 5 根：`support` / `support_mg` / `attach_samplepouch` / `attach_intelpad` / `target_designator`。

### D.11 L0 的四个限制，以及 L1 能解决几个

| L0 的限制 | 实测影响 | L1（护甲 Unit 自带 bones + 极小 state_machine）能否解决 |
|---|---|---|
| 只有 6 根骨（实可用 5） | 头发/袖口/裙摆下摆无骨可用 | ✅ 自己加，任意数量 |
| 轴心在体外 15–30 cm | 只能"整块板滑动"，不能"软组织形变" | ✅ 自己的骨，轴心想放哪放哪 |
| 参数烘焙不可调 | 只能 5°/10°、weight 0.5–5 | ✅ 状态机里那 9 个 float 由我们写 |
| 驱动信号是瞄准链 | 站着不动只转镜头就晃 | ❌ 新骨仍是 `chest` 的子骨 |

**所以 L1 一次能解决 3 条，只有第 4 条必须靠 Lua/外部系统**（在运行时把父级的角速度减掉，
让弹簧只响应世界空间的平移加速度）。这正好把"必须上 Lua"的范围缩小到**一件事**：
不是"加骨"、不是"解算"、不是"参数"，而只是**换驱动信号**。

---

## 附录 E：L1 验证（给护甲 Unit 自带 bones + state_machine）—— 否定

### E.1 两轮实验

| 轮 | 变体 | 做法 | 结果 |
|---|---|---|---|
| 1 | L1-A | 把已有节点 `torso_culling` 搬到胸部质量中心、父设 `chest`、把 6401 个顶点重指向它 | **胸部塌成薄层、被拉到右臂附近**；左乳更严重 |
| 1 | L1-B0/B1/B2 | A + `bonesRef` + 状态机（极简 / 真模板） | 与 A 无区别 |
| 1 | L1-C0 | A 经 `PatchArchiveWriter` 原样重打包 | 与 A 无区别 |
| 2 | L1-D0 | **原包几何一个字节不动**，只把 `bonesRef` 0 → bones 资源 | 与原包无区别 |
| 2 | L1-D1 | D0 + 状态机 1 条，`bone_index=17`（106 骨表里的 `chest`），`weight=1 / 角度限值 20°` | **与原包无区别** |
| 2 | L1-D2 | 不碰 `bonesRef`，状态机 1 条 `bone_index=7`（avatar 90 骨表里的 `chest`），同参数 | **与原包无区别** |

### E.2 两条结论

1. **加权到「avatar 骨架里没有对应名字」的节点 = 顶点塌陷。**
   引擎不会用 Unit 里写死的变换去摆这种节点；它给不出姿态，顶点就退化（压扁成一层），
   连接三角形被拉向某个默认位置。
   ⇒ **纯 patch 下无法"发明"新的枢轴**。这条比"不跟随父骨"更彻底。
2. **护甲 Unit 自己的 `state_machine` 不会被 tick。**
   D1（bonesRef + SM，106 骨表下标）和 D2（不动 bonesRef，avatar 90 骨表下标）两种解释都试了，
   把 20° 的物理条目加在**整个胸部 9781 个顶点**上，肉眼**完全没有变化**。
   ⇒ 只是"在同一个 FileId 下多放一份 `state_machine` 资产"不够，被挂载的 Unit 没有自己的驱动链。

顺带两条有用的副产品：
- `bonesRef` 从 0 指到 patch 里的 bones 资源是**无害的**（D0 与原包一致）。
- `PatchArchiveWriter` 如果收到空 `GpuResourceData` 会**清空该 Unit 的顶点数据**
  （表现为解析出 0 根骨、gpu_resources 从 57.8 MB 掉到 15.1 MB）。必须用
  `PatchEntryPayloadReader.ReadPayloadAsync` 把原始 payload 原样带回去。

### E.3 纯 patch 的能力边界（最终版）

| 能力 | 纯 patch | 说明 |
|---|---|---|
| 有物理 | ✅ | 6 根骨（`sling` 因随武器驱动而不可用，实为 5 根），任何网格都能挂 |
| 换轴心 | ❌ | 轴心 = 骨原点，硬绑；`BoneMatrices` 只决定网格坐在哪，动不了旋转中心 |
| 调参数 | ❌ | 烘焙在 avatar 那份 12.3 MB 状态机里，改它 = 重做全部动画 |
| 加骨 | ❌ | 非 avatar 骨名的节点会塌陷（E.2 第 1 条） |
| 换驱动信号 | ❌ | 6 根骨全是 `chest/spine1/spine2` 的子骨，永远被瞄准链激励 |

**五条里只有第一条成立。** 这就是纯 patch 的天花板，已经量完了。

### E.4 对"必须上 Lua"的范围界定

Lua/外部系统需要提供的，恰好是上表后四行：

1. **加骨**：运行时往 Unit 的场景图里插节点（AQ 版的 `HD2IRG1` + Lua 写入器做的就是这个）。
2. **换轴心**：新骨放在哪里由我们决定，天然解决。
3. **调参数**：弹簧参数由我们的解算器持有，不碰状态机。
4. **换驱动信号**：把父级的角速度减掉，让弹簧只响应世界空间的平移加速度——
   这是"只转镜头就晃"的唯一解法。

这四件事都只需要**每帧写少量骨骼变换**，成本随"物理骨数量"线性增长，与"是否外接一整套物理系统"无关。
换句话说：**帧率代价取决于我们加几根骨，而不是取决于要不要用 Lua。**

---

## 附录 F：P3 实测 —— 给玩家骨架加骨会崩，根因是动画资产里写死的骨数

### F.1 现象

只装 `18235e0c9ec0e636.patch_0`（bones 90→91 / unit 107→108 / SM 6→7），
进游戏约 5–20 秒后**玩家模型先消失、画面静止约 1 秒、随后崩溃**。多次重试崩溃时间不同，
最长一次玩家行为基本正常。

### F.2 崩溃转储解析

`PACKER_828759187_53116.dmp`（17 MB，minidump）：

```
ExceptionCode    = 0xC0000005  (ACCESS_VIOLATION，读)
ExceptionAddress = 0x00007ff607ad6c9c
                  → helldivers2.exe + 0xd96c9c
FaultingAddress  = 0x0000031bbcbe0100   ← 不是合法指针，是从内存读出来的垃圾
ThreadId         = 90132
Modules          = 168
```

（注：dump 由 runtime.dll 的崩溃处理器写出，但**故障地址在 helldivers2.exe**。）

### F.3 根因：`animation` 资产头部写死了骨数 = 90

抽玩家归档里的一份动画资产（`animation` / `0xf9761fd85530ae0f`，321,008 字节）看头部：

| 偏移 | 值 | 含义 |
|---|---|---|
| `+0x00` | 0 | — |
| `+0x04` | **90** | **骨数**（正好等于玩家骨架原骨数） |
| `+0x08` | 4.9837 | 时长（秒） |
| `+0x0c` | 321008 | 数据长度（等于文件大小） |

玩家归档 `18235e0c9ec0e636` 里有 **3522 份 `animation`**，每份都按 **90 根骨**排布轨道数据。
把骨架加到 91 根后，引擎求值第 91 根骨时读到动画数据末尾之外 → 把垃圾当指针 → AV。

**⇒ "给玩家骨架加骨" 不是改 3 份资产，而是要同时改 3522 份动画。** 这条路基本堵死。

### F.4 唯一可能的绕法：把新骨排除在动画 LOD 之外

`bones` 资源的 LOD 表是 `[90, 82, 59, 35, 32, 30]`。如果它是**前缀长度**，
那么 LOD0 只覆盖前 90 根骨。做法：`nameCount` 加到 91、但 **LOD0 保持 90**
→ 新骨落在所有 LOD 之外 → 动画永远不求值它，而状态机的 `bone_index` 仍能按索引找到它。

待验证（`P3-2c`）。若仍崩，则说明 LOD 表不是这个语义，或动画骨数还有别的约束。

### F.5 顺带确立的两条硬事实

1. **`bones` 资源是动画的索引空间**：动画的骨数必须与它一致，否则越界。
2. **`state_machine` 的 `bone_index` 指向 `bones` 资源的下标**（P3-1 已证：改 `bone_index=71`
   的参数就改了 `support_mg` 的抖动）。

### F.6 P3-2b / P3-2c 实测：两种放法都坏，纯 patch 加骨正式终结

| 变体 | bones | SM | 结果 |
|---|---|---|---|
| **P3-2b** | nameCount=91，**LOD0=91** | 原样 6 条 | **堆被写坏**：多次运行结果各不相同（直接崩 / 模型整体消失但游戏活着 / 只有乳房区域网格消失 / 过场后崩）。转储都是 `0xC0000005`，但落在**两个不同偏移**（`+0xd96c9c`、`+0x64a62b`），越界读的地址每次都不一样 → 典型堆损坏，不是确定性校验失败 |
| **P3-2c** | nameCount=91，**LOD0 保持 90** | 7 条 | **不崩了**，但：① 玩家整体材质丢失（品红/晶紫缺省材质）② 挂在玩家身上的 unit 逐渐消失（大衣、乳房区域网格）；退出游戏也不报错 |

两种放法正好卡在一个死结上：

- 新骨**放进 LOD0** → 动画资产（每份都写死 **90** 根骨，见 F.3）对不上 → 堆被写坏 → 非确定性崩溃。
- 新骨**排除出 LOD0** → 堆不坏了，但它不是一个"真正被动画求值的骨"，绑定/剔除链在它身上失配 → 材质解析失败 + unit 逐个消失。

**没有一个索引能同时满足"动画能对齐"和"物理能用"。⇒ 纯 patch 加骨到此终结。**

（补充证据：动画资产头部 `+0x04 = 90` 与骨架原骨数完全一致；玩家归档 3522 份动画都是这个值。
要让新骨可用必须同时改 3522 份动画，不可行。）

### F.7 纯 patch 的最终能力矩阵

| 能力 | 结论 | 依据 |
|---|---|---|
| 改 6 根骨物理参数 | ✅ **可用** | P3-1 实测生效（`bone_index` + 9 个 float，重封 12.3 MB SM） |
| 选骨 / 选轴心 | ✅ 有限可用 | 轴心=骨原点，6 根里只有 `support` 在正中线；`sling` 因随武器驱动而不可用 |
| 加骨 | ❌ | F.6 |
| 自定义轴心 | ❌ | F.6（加骨失败连带） |
| 换驱动信号（去掉瞄准耦合） | ❌ | 6 根骨全是 `chest/spine1/spine2` 子骨 |

⇒ **纯 patch 能拿到的就是"6 根骨 + 可调参数"**。这已经比社区现状（参数烘焙不可调）强一档，
但要"真正的物理"必须上 Lua，而且 Lua 要做的事已经被压缩到很小：
**往护甲 Unit 的场景图插一个节点 + 每帧写它的世界姿态**——不碰 `bones` 资源、不碰动画、不碰状态机。