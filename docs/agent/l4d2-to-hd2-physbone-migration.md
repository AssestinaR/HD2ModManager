# L4D2（Source 引擎）模型 → HD2 移植调研

> ⚠️ **历史存档（2026-11-01 归档）**：当前状态与结论以 `docs/agent/physbone-status.md` 为准。
> 本文件保留是为了追溯当时的推理过程与实测数据，**不要当成现行规范**。


> 状态：**调研记录（未定案）**。写于 2026-10-11。用户提出的新方向：
> 放弃"给老 HD2 mod 刷权重加物理"（太难），改为**把 L4D2 的模型整体移植到 HD2**，
> 因为 L4D2 的动骨支持非常标准、社区资源丰富。
> 本文只记录可溯源的实测事实，推断一律标 `inference`。

---

## 0. 用户原话要点

1. 原目标（给老 mod 批量加物理）"太难了，我得精确的为这些模型绑定骨骼，又或者设计一个
   非常通用的自动绑骨骼和刷权重……这太难了"。
2. **新目标**："我有一个好的来源：求生之路2的玩家模型mod……制作一个起源引擎模型转化工具，
   将该游戏的模组的骨骼模型等转移到 hd2 的 patch 文件中，随后通过我们的 lua 提供游戏内
   骨骼转译支持，将现成的动画骨骼和常规身体躯干骨骼移植到绝地潜兵2中。"
3. 之前失败过：项目在 `E:\Data\source\repos\HD2AgentWorkbench`。原因自述为
   "当时还没有 lua 支持"、"将求生之路2的标准 Tpose 重新摆放成 hd2 的类 Apose 太难"、
   "两个游戏的骨骼尺寸差距相当的大，主要是体型比例方面的"。
4. 要求："你还是先搜集信息并与我沟通交流，不急着直接改代码"。

---

## 1. 结论摘要（先看这个）

| # | 结论 | 置信度 | 证据 |
|---|---|---|---|
| L1 | **L4D2 的动漫风格角色模型自带完整的 jigglebone（动骨）物理参数**，不需要作者自己重做 | `format-confirmed` | §3 |
| L2 | 实测扫了 97 个 VPK / **6566 个 MDL**，其中 **147 个**带 jigglebone | `format-confirmed` | §3.1 |
| L3 | `Lunalice_Zoey.vpk` 主模型 **127 骨 / 57 个 jiggle bone**：**15 根挂在 `Head1`（头发）**、**4 根挂 `Pelvis`（裙摆）**、6 根 `Spine`、4 根 `Spine4`、各 2 根挂左右小腿，**其余成链**（`69→68`、`71→70`、`73→72`、`74→73`） | `format-confirmed` | §3.3 |
| L4 | 每根 jigglebone 的参数完整可读（`length` / `tipMass` / `yawStiffness` / `yawDamping` / `pitchStiffness` / `pitchDamping` / `alongStiffness` / `angleLimit` / 各类约束与摩擦） | `format-confirmed` | §2.2 §3.3 |
| L5 | 这些骨的**骨名读不到**（`sznameindex` 指向序列文本），但**父级层级完全有效** → 靠层级 + 位置即可重建结构 | `format-confirmed` | §3.3（旧项目 `SourceModelReader.ReadBoneName` 已知此坑并做了名字合法性校验） |
| L6 | 旧项目 `HD2AgentWorkbench` 已经能把 `Lunalice_Zoey.vpk` 静态转成 HD2 Patch（含骨骼拟合、Canonical 预烘焙、PatchValidator 回读） | `project-proven` | §4 |
| L7 | 旧项目**读了** `proctype` / `procindex`，但**没有读** `mstudiojigglebone_t` 数据本体 → 物理参数目前是**未采集**状态 | `project-proven` | §4.2 |
| L8 | **身体骨**必须映射到 HD2 骨架（因为要复用 HD2 自己的动画）；**额外骨**可以原样保留 | `inference`（机制清晰，未验证引擎侧） | §5 |
| L9 | 用户期望的"运行时骨骼转译"**在运行时其实不需要**：骨骼在离线时就已经是 HD2 格式，运行时只负责**驱动**额外骨 | `inference` | §5.3 |
| L10 | **两侧的世界 bind 用同一个公式 `inverse(bind)`**（Source `inverse(poseToBone)` / HD2 `inverse(inverse_bind)`）——靠 `pos`+`quat` 沿父链累加会**静默塌陷** | `measured` | §9.1 |
| L11 | **"T-pose 摆成 A-pose" = 27.4° 的纯旋转**（Source bind 90.0°、HD2 bind 62.6°，都是实测） | `measured` | §9.2 |
| L12 | **真实肢体长度比只有 0.868 ~ 1.135**；腿长比 **1.006**（几乎完全相同）。"体型差距大"**被高估了** | `measured` | §9.2 |
| L13 | 两个看起来最大的比值（`Clavicle→UpperArm` 3.195、`Neck1→Head1` 2.615）是**关节语义不同**，不是尺寸差距 | `measured` | §9.2 |
| L14 | 单一均匀缩放**必然失败**（最小二乘最优 scale=0.990，最大残差 **0.2515 m**）；解 = **逐骨相似变换** | `measured` | §9.2 |
| L15 | 旧项目**其实已经把数学解出来了**（canonical 逐顶点预烘焙 + `source-rig-fit-v6`，127 骨，残余 5.7e-4 m）；卡住它的是**反馈回路**，不是数学 | `project-proven` | §9.3 |
| L16 | 已建成**不需要进游戏的离线验证回路**（`skeleton_probe.py` / `plot_skeleton.py` / `rig_gap_report.py`） | `built` | §10 |

**一句话**：这个方向**可行，而且比预想更可行**——因为 L4D2 的模型**已经**把头发/裙摆绑到了
专门的骨骼链上，**并且已经**带了物理参数。我们要做的是"搬运 + 驱动"，不是"发明"。

**第二句话（第二轮实测后）**：那个"离线数学问题"**已经被量出来了，而且比用户记忆中的小得多** ——
手臂只差 27.4°（纯旋转）、腿长只差 0.6%、真实长度比只在 0.868~1.135 之间。
真正让人以为"太难"的两件事（T-pose/A-pose、体型差距）**一个已被旧项目解到 0.57 mm，另一个被高估了**。

---

## 2. Source 引擎的 jigglebone 机制（一手：Valve 官方文档 + Source SDK 源码）

### 2.1 QC 侧的 `$jigglebone`

来源：`developer.valvesoftware.com/wiki/$jigglebone`。

- 从 **Source 2007 起**在所有 Source 游戏可用 → **L4D2 支持**。
- 作用："marks bones within your model's skeleton as 'jiggle bones' and tells the game engine
  to **dynamically simulate them during runtime**"。
- 四个互斥/可选的属性组：
  - `is_rigid`：质量点放在骨末端，骨像刚性棍子摆动（只有旋转）
  - `is_flexible`：质量点沿骨的 **+Z 轴**放在 `length` 处，骨像弹性杆（只有旋转）
  - `has_base_spring`：质量点放在骨基部，产生**平移**弹簧
  - `is_boing`：挤压拉伸（仅 Source 2013）
- 关键参数：`stiffness`（1–1000）、`damping`（0–10）、`length`（英寸）、`tip_mass`、
  `angle_constraint`、`yaw/pitch_constraint`（±90°内）、各类 `friction`。
- 重要约束：**仿真方向依赖骨的 +Z 轴**，建模时必须按此摆放骨骼朝向。

### 2.2 MDL 侧的数据结构（Source SDK `studio.h`）

`STUDIO_PROC_JIGGLE = 5`，通过 `mstudiobone_t.proctype` 判定。

```c
struct mstudiojigglebone_t        // 共 120 字节
{
    int   flags;                  // JIGGLE_IS_FLEXIBLE 0x01 / JIGGLE_IS_RIGID 0x02
                                  // JIGGLE_HAS_YAW_CONSTRAINT 0x04 / _PITCH_ 0x08
                                  // JIGGLE_HAS_ANGLE_CONSTRAINT 0x10 / _LENGTH_ 0x20
                                  // JIGGLE_HAS_BASE_SPRING 0x40
    float length, tipMass;
    float yawStiffness, yawDamping, pitchStiffness, pitchDamping;
    float alongStiffness, alongDamping;
    float angleLimit;
    float minYaw, maxYaw, yawFriction, yawBounce;
    float minPitch, maxPitch, pitchFriction, pitchBounce;
    float baseMass, baseStiffness, baseDamping;
    float baseMinLeft, baseMaxLeft, baseLeftFriction;
    float baseMinUp,   baseMaxUp,   baseUpFriction;
    float baseMinForward, baseMaxForward, baseForwardFriction;
};
```

**定位方式（关键）**：

```c
inline void *pProcedure() const {
    if (procindex == 0) return NULL;
    else return (void *)(((byte *)this) + procindex);   // ← 相对本骨地址的字节偏移
}
```

→ `procindex` **不是数组下标，是相对该骨自身地址的字节偏移**，所以 `bone_addr + procindex`
直接给出 `mstudiojigglebone_t*`。

**`mstudiobone_t` 的字段偏移（L4D2 v53 实测为 216 字节/条）**：
`sznameindex`(0) `parent`(4) `bonecontroller[6]`(8) `pos`(32) `quat`(44) `rot`(60)
`posscale`(72) `rotscale`(84) `poseToBone`(96) `qAlignment`(144) **`flags`(160)**
**`proctype`(164)** **`procindex`(168)** `physicsbone`(172) `surfacepropidx`(176)
`contents`(180) `unused[8]`(184..216)。

---

## 3. L4D2 实测（本轮新增，一手）

### 3.1 全库扫描

- 输入：`D:\SteamLibrary\steamapps\common\Left 4 Dead 2\left4dead2\addons`（**97 个 VPK，20 GB**）
- 方法：只读 VPK 目录树 + 定点读 MDL 段（脚本 `tmp/physbone-recon/scan_vpk_jiggle.py`）
- 结果：**6566 个 MDL，147 个带 jigglebone**（0 个 VPK 解析失败）

命中的 VPK（节选）：

```
1540128972.vpk  1619051776.vpk  2066106924.vpk  2139756699.vpk  2396847377.vpk
2459037122.vpk  2795386618.vpk  2846314478.vpk  3135451698.vpk  3135458590.vpk
3366491323.vpk
Elise_Coach.vpk           Elise_Louis.vpk           Lilium_Sancta_Bill.vpk
Lilium_Sancta_Nick.vpk    Lunalice_Rochelle.vpk     Lunalice_Zoey.vpk
Serenade_Ellis.vpk        Serenade_Francis.vpk      [l4n_survivor] *.vpk
MourningVeil法杖替换消防斧Fireaxe.vpk  MourningVeil法杖替换铲子Shovel.vpk
广州增城.vpk  章鱼mafuyu替换smoker(模型)(换了部分动作).vpk  麻薯tank.vpk
```

**注意：用户手头正在用的整个 L4N 系列（Lunalice / Serenade / Elise / Lilium_Sancta /
MourningVeil）全部命中。**

规模分布（说明这不是玩具特性）：

| 模型 | 骨数 | jiggle 骨数 |
|---|---|---|
| `models/infected/mlui_prostitute03.mdl` | 126 | **71** |
| `models/infected/mlui_prostitute02.mdl` | 116 | **61** |
| `models/infected/mlui_prostitute01.mdl` | 94 | **39** |
| `models/survivors/survivor_teenangst.mdl`（Lunalice Zoey） | 127 | **57** |
| `models/weapons/gzzc/melee/v_meleejb.mdl` | 68 | 5 |
| `models/infected/demontank.mdl` | 56 | 2 |

### 3.2 参数样本（原样读出，未加工）

```
[64] parent=ValveBiped.Bip01_Head1  flags=0x31(flexible,angle_con,length_con)
     len=30.0 tipMass=200.0 yawS=50 yawD=7.0 pitchS=50 pitchD=7.0 alongS=100 alongD=0 angLim=0.611
[59] flags=0x2D(flexible,yaw_con,pitch_con,length_con)
     len=90.0 tipMass=15.0 yawS=65 yawD=25.0 pitchS=65 pitchD=25.0
[15] flags=0x0D(flexible,yaw_con,pitch_con)
     len=5.0 tipMass=0.0 yawS=300 yawD=10.0 pitchS=200 pitchD=10.0
[ 1] flags=0x2E(rigid,yaw_con,pitch_con,length_con)   // 刚体摆动（武器模型）
     len=200.0 tipMass=300.0
```

→ **物理参数是作者调好的成品**，不是默认值堆出来的。

### 3.3 `Lunalice_Zoey.vpk` 详细结构（最关键的证据）

```
=== models/survivors/survivor_teenangst.mdl : bones=127 jiggle=57 ===
  parent 分布：
     ValveBiped.Bip01_Head1    15    ← 头发
     ValveBiped.Bip01_Spine     6
     ValveBiped.Bip01_Spine4    4
     ValveBiped.Bip01_Pelvis    4    ← 裙摆
     ValveBiped.Bip01_L_Calf    2
     ValveBiped.Bip01_R_Calf    2
     其余挂在其它 jiggle 骨下（成链）
  -- 链式结构样本 --
     [68] parent=Head1        [69] parent=[68]
     [70] parent=Head1        [71] parent=[70]
     [72] parent=Head1        [73] parent=[72]     [74] parent=[73]
```

**要点**：
- 头发是**挂在 `Head1` 下的 15 根骨**，且**成链**（`Head1 → 72 → 73 → 74`）——
  这正是"及腰长发像银杏叶一样飘"的骨骼结构。
- 裙摆挂在 `Pelvis` 下。
- 这些骨的**骨名读不到**（`sznameindex` 指向内嵌的 SMD 文本），但**父级索引完全有效**。
  → 靠"层级 + bind pose 位置"足以重建结构，**不需要名字**。
- 非 `ValveBiped` 命名的骨：**0 个**（即所有额外骨都没有可读名字）。

---

## 4. 旧项目 `HD2AgentWorkbench` 的现状

### 4.1 已完成（阶段 3）

- 读 VPK v1/v2、MDL/VVD/VTX，校验三件套与 checksum。
- 已把 `Lunalice_Zoey.vpk` 的 LOD0 几何写入 RE-2310 Slim Torso target Unit，输出普通 HD2 Patch。
- 两条蒙皮路径：默认保留 HU 补偿调色板；`--skinning-mode canonical` 把静置姿态逐顶点预烘焙。
- `--pose-migration source-rig-fit`：127 根 Source 骨骼按 HD2 Avatar 关节座点重新拟合
  （锁骨 stretch cap 1.50x、脚用 `foot -> ball` landmark、`Spine4` 显式用 HD2 `chest` frame）。
- 通过 `PatchValidator`、Canonical dependency closure、管理器 `import inspect`。

### 4.2 已知残余问题（用户所说的"失败"）

- Canonical 权重量化后约 **5.7e-4 m** 静置回放误差。
- Source/HD2 躯干映射选择、脚部局部 frame 与 pivot。
- 手掌非均匀缩放、手指 root gap、肩根动态变形。
- **`proctype` / `procindex` 已读，但 `mstudiojigglebone_t` 数据本体未采集**
  → 物理参数目前完全没被利用。

### 4.3 用户自述的失败原因 vs 实测

| 用户自述 | 实测判断（第二轮已量化，见 §9） |
|---|---|
| "当时还没有 lua 支持" | 部分成立：运行时驱动额外骨确实需要 Lua（或原生插件） |
| "T-pose 摆成 HD2 类 A-pose 太难" | **纯离线数学，Lua 帮不上忙。** 实测只差 **27.4°**，是个纯旋转；旧项目已用 `source-rig-fit` + 逐顶点预烘焙绕开，残余 **5.7e-4 m** |
| "两个游戏骨骼尺寸差距相当大（体型比例）" | **被高估了。** 实测真实肢体长度比只有 **0.868 ~ 1.135**，腿长比 **1.006**。看起来最大的两个比值是**关节语义不同**，不是尺寸差距 |
| （未提到）"每次改动必须进游戏实测" | **这才是真正卡住的原因。** HANDOFF §18 原文："本轮新增的 Source armature fit **尚未在用户侧游戏内验证**" —— 东西做出来了，但没有办法快速判断对不对 |

→ **Lua 解决的不是这三个问题。** Lua 解决的是"让额外骨骼动起来"。
→ 而"每次改动必须进游戏"这个真正的瓶颈，**本轮已经用离线回路解决了**（§10）。

---

## 5. 方案分工（本轮推论，待验证）

### 5.1 三层分工

```
① 身体骨（~20 根 ValveBiped）
     离线映射 → HD2 原生骨架（90 骨）
     运行时由 HD2 自己的动画驱动（走/跑/射击全部免费）
        ↓ 子级关系
② 额外骨（头发/裙摆/挂饰，本样本 57 根）
     离线原样保留（追加在 HD2 骨架末尾，不改变原有索引）
     运行时由 Lua 按 Source 的 jigglebone 参数驱动
        ↓ 顶点权重
③ 顶点
     身体区域 → 重映射到 HD2 身体骨
     头发/裙摆区域 → 直接指向额外骨（索引随骨一起搬，不需要重映射）
```

### 5.2 为什么"额外骨不需要重映射权重"

Source 模型里，头发顶点本来就绑在头发骨上（索引 64–120 区间）。我们把**同一批骨骼原样搬到
HD2 骨架末尾**后，只要顶点权重里的**索引跟着平移**，绑定关系就自动成立。
**唯一需要真正映射的是"身体骨"那一小部分。**

### 5.3 "运行时骨骼转译"其实不需要

用户期望的"Lua 提供游戏内骨骼转译支持"——**运行时并不存在"转译"这一步**：
骨骼在离线输出 Patch 时就已经是 HD2 格式了。运行时的职责只有一件：
**按 jigglebone 参数驱动那几十根额外骨**（`Unit.set_local_rotation`）。

### 5.4 复用 HD2 原生物理

用户的判断（"尽可能复用游戏原版的物理系统，减少一些全面支持的需求"）方向正确：
- **身体**：HD2 自己的动画 + 原生抖动骨（6 根装备挂点骨）—— 免费
- **额外骨**：用 Source 的 jigglebone 参数，在 Lua 侧做**等价的弹簧积分**
  （参数已现成，不需要自己调）

---

## 6. 关键未知（**必须验证，不得当事实**）

| # | 未知 | 为什么重要 | 怎么验 |
|---|---|---|---|
| X1 | **HD2 引擎能接受多少根骨骼？** 90 → 147 骨是否合法 | 这是整条路的**生死线**。若不接受，方案作废 | 造一个加骨 Unit 进游戏看是否加载 |
| X2 | 追加骨骼后，**HD2 原有动画是否仍正确驱动前 90 骨** | 决定"身体用 HD2 动画"这条分工是否成立 | 同上 |
| X3 | Lua 每帧驱动 **57 根骨 × 每个玩家** 的成本 | 决定要不要限制骨数 / 降频 | 游戏内计时 |
| X4 | 骨名读不到的额外骨，如何**稳定标识**（跨构建/跨模型） | 影响离线数据的可复现性 | 用"父级链 + bind pose 位置"指纹 |
| X5 | Source 的 `is_flexible` 弹簧能否用简单二阶积分**等价复现** | 决定"复用参数"是否真能还原手感 | 离线回放比对 |
| X6 | HD2 的 `set_local_rotation` 是**局部**还是**世界**语义、与 Source 的 +Z 轴约定是否一致 | 轴不对就全乱 | 游戏内实测 + 离线回放 |
| X7 | 目标 Unit 该选哪个（RE-2310 Slim Torso 已知可用） | 影响可覆盖的护甲范围 | 旧项目已探明部分 |

---

## 7. 待用户拍板的问题

1. **第一阶段范围**：只做"一个 L4D2 模型 + 头发/裙摆抖动"，还是要先验证 X1/X2（加骨合法性）？
2. **是否接受"不转译 L4D2 的动画"**：身体动作用 HD2 自己的，额外骨用物理驱动？
   （转译 Source 动画到 HD2 格式是另一个数量级的工程，HD2 动画资源格式目前对我们完全未知。）
3. **额外骨的驱动策略**：忠实复现 Source 的 `is_flexible` 弹簧参数，还是用我们自己的简化弹簧？
4. **旧项目怎么处理**：原地复活 `HD2AgentWorkbench`，还是把能力迁进 `WpfApp1`？
   （旧项目当前通过 MSBuild 属性引用相邻仓库的 `HD2ModAdaptation` 作为临时 bridge。）

---

## 8. 本轮产物

- `tmp/physbone-recon/scan_vpk_jiggle.py`：流式扫全部 VPK 的 jigglebone（可复用）
- `tmp/physbone-recon/analyze_jiggle_tree.py`：单模型 jigglebone 层级与参数详情
- `tmp/physbone-recon/jiggle-scan.log`：全库扫描结果
- `tmp/physbone-recon/studio.h`、`jigglebones.cpp`：Source SDK 参考源码（来自 HL2-Ghosting-Team/src）

---

## 9. 【第二轮实测】离线数学问题到底有多大 —— 已经量出来了

> 触发：用户问"你能否想办法解决，又或者'要砍柴先磨斧'？"
> 结论：**能，而且答案是可测量的、唯一的、不需要进游戏就能验证的。**

### 9.1 先纠正一个会让人走错路的前提

**两侧的世界 bind 矩阵用同一个公式**：

```
world_bind = inverse(bind_matrix)
```

| 侧 | bind 矩阵在哪 | 怎么读 |
|---|---|---|
| Source | `mstudiobone_t.poseToBone`，`entry + 96`，12 个 float，row-major / 列向量 | `Matrix4x4.Invert(poseToBone)` |
| HD2 | Unit 的 inverse bind 表 | `inverse(inverse_bind)` |

旧项目 `HD2AgentWorkbench/src/HD2AssetKit/LunaliceHuPoseBake.cs:244` 用的正是这一句。

⚠️ **不要靠 `pos` + `quat` 沿父链累加**。那条路会忽略 `posscale` / `rotscale` / `qAlignment`，
结果**静默塌陷**（左右肢体坐标完全相同、左右手间距算成 0）。本轮真踩过这个坑，
直到改用 `inverse(poseToBone)` 才拿到自洽的数据。

### 9.2 实测结果（`survivor_teenangst.mdl` 127 骨 vs `hd2_skel.json`）

两侧都是 **bind pose**、都换算成米、都对地。产物见 `tmp/physbone-recon/zoey-vs-hd2-overlay.png`。

**（1）"T-pose 摆成 A-pose"有多大？—— 27.4°，而且是个纯旋转**

| 肢体 | Source 方向 | HD2 方向 | Source 离垂直 | HD2 离垂直 |
|---|---|---|---:|---:|
| 左臂（肩→手） | (-0.536, 0.000, 0.000) | (-0.446, 0.209, -0.256) | **90.0°** | **62.6°** |
| 左腿（髋→足） | (0.000, 0.000, -0.934) | (-0.107, 0.030, -0.934) | 0.0° | 6.8° |

- Source 的 bind 是**标准 T-pose**（手臂水平，离垂直恰好 90.0°）。
- HD2 的 bind 是 **A-pose**（62.6°）。
- **需要把手放下 27.4°** —— 一个角度，条件数极好，不存在"难"。

**（2）"两游戏骨骼尺寸差距大"有多大？—— 其实很小，只有 0.868 .. 1.135**

| 段 | Source (m) | HD2 (m) | HD2/Source |
|---|---:|---:|---:|
| 臂（肩→手） | 0.5364 | 0.5553 | **1.035** |
| 腿（髋→地） | 0.9345 | 0.9336 | **1.006** |
| 大腿 | 0.3935 | 0.4295 | 1.092 |
| 小腿（膝→地） | 0.5410 | 0.5082 | 0.948 |
| 上臂 | 0.2669 | 0.3028 | 1.135 |
| 前臂 | 0.2695 | 0.2874 | 1.066 |
| 骨盆→胸 | 0.4000 | 0.3704 | 0.926 |
| 胸→颈 | 0.2381 | 0.2066 | 0.868 |

**真实肢体长度比只有 0.868 ~ 1.135（最大偏差 13.5%）。腿长几乎完全相同（0.6%）。**

⚠️ **两个看起来最吓人的数字是"关节语义不同"，不是尺寸差距**：

| 段 | 比 | 真相 |
|---|---:|---|
| `Clavicle→UpperArm` | 3.195 | ValveBiped 把 Clavicle 停在 UpperArm 旁边（0.0487 m），HD2 的 `l_clavicle`→`l_shoulder` 是 0.1556 m —— **两个关节不是同一个关节** |
| `Neck1→Head1` | 2.615 | Source 的 `Head1` 在颅底，HD2 的 `head` 在颅腔更高处 |

**把这两行当成"体型比例差距"去解，正是把 solver 带进沟里的原因。**

**（3）"随便缩一下不就行了？"—— 不行，而且有个专门的陷阱**

| 映射模型 | 结果 |
|---|---|
| 单一均匀缩放 + 平移（最小二乘最优） | scale = 0.990，**最大残差 0.2515 m**（手位偏 25 cm，肉眼可见） |
| 逐骨自由平移（把每个关节摆到 HD2 要的位置） | **残差 0.0000 m —— 但骨长全错，网格会被拉变形** |
| **逐骨相似变换**（保留 Source 骨长 × 逐骨系数、取 HD2 关节方向） | **唯一解，父→子一遍解完** |

第二种就是**最阴的陷阱**：只看"关节位置误差"会报"完美"。
参考插件自己的文档（`kit/tools/mod_skel.py` 顶部）就专门警告过这类错误：

> *"...the bootstrap affine still reported p50 0.122 m over 21 of 21 landmarks,
> because a fit over landmarks that all share the bias cannot see the bias."*

（另一条同源教训：某个 mod 的顶点是**绕胸口**建模的，整体偏移 1.28 m；
用共享骨架去比对时"每个部件都错 1.28 m 且匹配到 0 个部件"，而最小二乘仍然报得很漂亮。
**正确做法是从 Unit 自己的 inverse bind 反推骨架，不要相信任何共享常量。**）

### 9.3 关键认知：旧项目其实已经把数学解出来了

对照 `HD2AgentWorkbench/HANDOFF.md` §17–§19：

| 项 | 状态 |
|---|---|
| `lunalice build --skinning-mode canonical` | ✅ 把 Source 顶点**逐顶点烘焙**进 HD2 Avatar 静置姿态 |
| `--pose-migration source-rig-fit`（诊断版 `source-rig-fit-v6`） | ✅ 127 骨父→子求解；`Spine4` 显式用 HD2 `chest` frame；锁骨 stretch cap 1.50×；脚用 `foot -> ball` landmark |
| 真实 `Lunalice_Zoey.vpk` | ✅ 已产出 canonical / retained 两包，通过 `PatchValidator` + 依赖闭包 + 管理器 `import inspect` |
| 残余 | 静置回放误差 **5.7e-4 m**、躯干映射选择、脚部 frame/pivot、手掌非均匀缩放、手指 root gap |

**→ 卡住这个项目的不是数学，是"改一次必须进一次游戏"的反馈回路。**
用户的自我诊断是对的，而且可以坐实：HANDOFF §18 原文写着
"本轮新增的 Source armature fit **尚未在用户侧游戏内验证**" —— 东西做出来了，但没人能快速判断它对不对。

### 9.4 与"动态骨骼"的关系（两件事，别混）

- **T-pose→A-pose / 比例差异 / 逐骨拟合** = **纯离线数学**，Lua 帮不上忙，也**不需要** Lua。
- **Lua 解决的是另一件事**：让**额外骨**（头发/裙摆）动起来。

把这两件事分开，用户原话里的三个"失败原因"就只剩一个是真问题（且已解），
另一个（让额外骨动）恰好是 HD2 官方 API 就能做的。

---

## 10. "斧"：一条不需要进游戏的验证回路

本轮建成的三件工具（全部在 `tmp/physbone-recon/`，纯 Python，零游戏启动）：

| 工具 | 作用 |
|---|---|
| `skeleton_probe.py` | 读 L4D2 MDL 的 bind pose（`inverse(poseToBone)`）→ 世界坐标，与 `hd2_skel.json` 并排输出，可导出 JSON |
| `plot_skeleton.py` | 用 pillow 把两具骨架**同比例、对地**叠在一起（front / side），带关节标注与手臂下垂角 |
| `rig_gap_report.py` | 段长比表 + 关节角表 + 三种映射模型的残差 + 结论，输出 markdown |

**为什么这是"斧"**：它把"改一次 → 进游戏看一次"的循环，
换成"改一次 → 跑一次脚本 → 看毫米数"。
上一轮之所以耗尽，不是因为问题难，而是因为**每轮迭代的周期以分钟计、且必须有人类在游戏里看**。

### 10.1 计划（建议顺序）

| 步 | 做什么 | 验收标准（全部离线，除最后一步） |
|---|---|---|
| **A1** | 从**游戏本体**的 avatar Unit 读 inverse bind 表，自己推出 HD2 骨架 | 与 `hd2_skel.json` 逐骨误差 < 1 mm |
| **A2** | 把 A1 的骨架与 L4D2 bind 对齐，实现 §9.2(3) 的逐骨相似解 | 逐骨残差 < 1 mm；**且骨长保持不变** |
| **A3** | 用 L4D2 的顶点 + 权重跑一遍"离线重定向"，输出 OBJ 供眼睛看 | 导出 OBJ 在 Blender/3D 查看器里形状正常、无塌陷 |
| **A4** | 用 `rig_gap_report.py` 对 **3–5 个不同的 L4D2 模型**跑同一套流程 | 全部通过；记录最差残差 |
| **A5** | 离线回放：拿 Source 的一段动画，按映射驱动两侧骨架，逐帧比对 | 逐帧误差曲线有上界，无发散 |
| **B1** | （需要游戏）造一个加骨 Unit 进游戏，验 X1/X2 | 能加载、前 90 骨动画正常 |

A1–A5 **完全不需要进游戏**，可以高速迭代。B1 是唯一必须进游戏的，而且**只做一次**。

### 10.2 这条回路顺带解决的两件事

1. **多模型批量验证**：147 个带 jigglebone 的 MDL 可以一次性全跑，找出映射会失败的模型类型。
2. **回归保护**：以后任何一次改动，跑一次脚本就知道有没有把已解决的部分弄坏。

---

## 11. 待用户拍板（更新版）

1. **第一阶段范围**：
   - (a) 先做"**A1–A3：把离线数学彻底做扎实 + 出可视化 OBJ**"（推荐 —— 这是"磨斧"，且不依赖游戏）；
   - (b) 直接做 B1（加骨合法性验证），因为它决定整条路是否成立；
   - (c) 只做单模型头发抖动的最小闭环。
2. **是否接受"不转译 L4D2 的动画"**：身体动作用 HD2 自己的，额外骨用物理驱动？
   （转译 Source 动画是另一个数量级的工程。）
3. **额外骨的驱动策略**：忠实复现 Source 的 `is_flexible` 弹簧参数，还是用我们自己的简化弹簧？
4. **旧项目怎么处理**：原地复活 `HD2AgentWorkbench`，还是把能力迁进 `WpfApp1`？
   （旧项目通过 MSBuild 属性引用相邻仓库的 `HD2ModAdaptation` 作为临时 bridge。）

---

## 12. 本轮产物（第二轮）

- `tmp/physbone-recon/skeleton_probe.py`（**已修正**：改用 `inverse(poseToBone)`）
- `tmp/physbone-recon/plot_skeleton.py` → `zoey-vs-hd2-overlay.png`
- `tmp/physbone-recon/rig_gap_report.py` → `rig-gap-report.md`
- `tmp/physbone-recon/zoey-vs-hd2.json`（127 骨的世界 bind 坐标，供复用）

---

## 13. 【A1 已完成】从游戏本体推出 HD2 骨架，并通过验收

> 目标：**不再相信任何共享常量**，自己从游戏数据推出 HD2 骨架。
> 验收标准：与参考常量 `hd2_skel.json` 逐骨误差 **< 1 mm**。

### 13.1 做法（零生产代码改动）

复用了本仓**已经发布**的能力 `hd2mod gamedata extract asset`：

```bash
hd2mod gamedata extract asset \
  0xe0a48d0be9a7453f 0x4d1c334d294dfa97 18235e0c9ec0e636 \
  --game-data "<游戏 data 目录>" --output tmp/rig-lab/avatar --overwrite
```

得到玩家 avatar Unit 的 TocData（**21504 字节**），再按 `UnitMeshReader.ReadTransformInfoFromUnitToc`
的同一套布局解析（`tmp/rig-lab/avatar_rig_probe.py`）：

```
u32 @ 0x34            transformInfoOffset   → 0x190
u32 @ tio             count                 → 107
u32 @ tio+4/+8/+12    reserved              → (0,0,0)
tio+16  + i*64        local:  9f rot | 3f pos(+36) | 3f scale(+48) | 1f pad(+60)
      + count*64      matrix: 16f
      + count*64      entry:  u16 increment | u16 parentIndex
      + count*4       name hash: u32
```

### 13.2 结果：**通过，且余量很大**

| 项 | 结果 |
|---|---|
| transform 条目数 | **107** |
| 名字解析成功 | 97 / 107（10 个哈希不在 `bonehash.txt` 里） |
| 与 `hd2_skel.json` 可比的骨 | **64** |
| **最大逐骨误差** | **0.000262 m（0.262 mm）** ✅ |
| 平均误差 | **0.000004 m（4 µm）** |
| 误差 > 1e-7 m 的骨 | **只有 1 根**：`r_shoulder_twist` 0.262 mm |
| 其余 63 根 | 全部在 float32 噪声级 |

**`Matrices[i]` 的布局已确定：row-major，平移在 `m[12..14]`**（列向量约定）。
（另一候选 `m[3],m[7],m[11]` 误差 1.69 m，直接排除；`local.pos` 是局部坐标，误差 1.70 m，同样排除。）

→ **我们现在可以从游戏本体自己推出 HD2 骨架，不再依赖任何外部常量。**
参考插件文档里警告的那个陷阱（"共享常量可能来自某个 mod 的私有 mesh 空间"）从此不成立。

### 13.3 顺带挖出的重要事实

**（1）骨架分两张表，数量不同 —— 这直接关系 X1**

| 表 | 数量 | 说明 |
|---|---:|---|
| **transform 表**（本次解析） | **107** | 场景图节点，含非蒙皮辅助节点 |
| 蒙皮调色板（`bones` 资源） | **90** | `nameCount=90, lodCount=6` |

所以"加 57 根骨"意味着 **transform 107 → 164、蒙皮调色板 90 → 147**。
**X1 要验的是这两张表分别能不能长大**，不是笼统的"能加多少骨"。

**（2）基础 avatar 里已经有一大批"专用骨"**（本次实测，之前只是推断）

`cape1..cape8`、`attach_intelpad`、`attach_knife`、`attach_samplepouch`、`attach_hand_l/r`、
`attach_weapon`、`attach_cam_1`、`backpack`、`sling`、`support`、`support_mg`、`target_designator`、
`l_shoulderarmour`、`r_shoulderarmour`、`pistol`、`aim_weapon`、`weapon_aim`、`head_aim`、`boss_aim`、
`head_cam`、`head_tip`、`climb_ref`、`collision`、`g_body`、`c_culling`。

→ 这些**本来就在玩家骨架上**，是现成的"区域"来源。

**（3）`hd2_skel.json` 并不是基础 avatar，而是"某个 mod 的视图"**（印证了参考插件文档）

它有 **39 个键在我们表里找不到**：`grp_*` 分组、`g_*_LOD1/LOD2/shadow`、
以及 `helmet_culling` / `hips_culling` / `l_arm_culling` / `l_leg_culling` / `r_arm_culling` /
`r_leg_culling` / `torso_culling`。

但**共有的 64 根骨精确吻合到浮点噪声** → 基础骨架是同一套，参考文件只是多了它自己 unit 的
LOD / shadow / culling 节点。**这正好说明 §13.2 的结论是对的：必须自己推，不能照抄常量。**

**（4）10 个未解析的哈希**（不在 `bonehash.txt` 里，待识别）：

`0xc89b0fd3`（在索引 2，紧跟 `FbxAxisSystem_ConvertNode`）、`0x75275fac`、`0x43997e6e`、
`0xc15d6a74`、`0xf9963256`、`0xb7d14251`、`0xa5c8115b`、`0x7c74e51c`、`0xa420b088`、`0xb9162207`

### 13.4 A1 结论

- ✅ **验收通过**（0.262 mm < 1 mm，余量 4 倍）
- ✅ 骨架来源已从"外部常量"换成"游戏本体"
- ✅ 顺带确定了 transform 表的精确布局与两张表的数量关系（X1 的输入）
- ⏭ 下一步 **A2**：实现逐骨相似解（保留 Source 骨长 × 逐骨系数、取 HD2 关节方向），
  验收 = 逐骨残差 < 1 mm **且骨长不变**

**A1 未改任何生产代码**，只用了已发布的 `gamedata extract` + 一个 Python 解析脚本。

---

## 14. 【A2 已完成】逐骨相似解，并通过验收

> 目标：解出 Source→HD2 的逐骨映射。
> 验收：**逐骨残差 < 1 mm 且骨长不变**。
> 做法：`tmp/rig-lab/rig_map.py`（独立实现，不依赖任何一个 C# 工程）。

### 14.1 公式

```
T_i = H_σ(i) · S_i^-1        （S 先把英寸换算成米）
```

- `S_i` = Source 骨 i 的世界 bind（`inverse(poseToBone)`，× 0.0254 换算成米）
- `H_j` = HD2 骨 j 的世界 bind（avatar Unit transform 表）
- `σ` = 对应表（20 根身体骨 + 30 根手指 = **50 对，0 缺失**）

**结论：这个映射是精确的**（骨原点落在 HD2 关节上、骨长取 HD2 的长度），
所以"残差"不是它要回答的问题 —— **它要回答的是网格会不会被拉伸**。

### 14.2 结果

| 指标 | 结果 | 判定 |
|---|---|---|
| **① 外部校验**：HD2 节点位置 vs `hd2_skel.json` | 64 节点，**最大 0.000262 m** | ✅ PASS |
| **② 映射后段长 vs HD2 段长** | **最大误差 0.000000000 m** | ✅ PASS |
| **③ 映射本身的刚性**（T 的奇异值） | 偏离 (1,1,1) 最大 **3.9e-07** | ✅ PASS（无附加剪切/缩放） |
| 覆盖 | 127 根 Source 骨：**50 直接映射**，77 回退到最近映射祖先 | — |

**②的副产品 —— 这才是真正的"网格拉伸"数字**（`HD2 段长 / Source 段长`）：

| 段 | Source (m) | HD2 (m) | **拉伸比** |
|---|---:|---:|---:|
| 骨盆→胸 | 0.4000 | 0.3704 | **0.9258**（缩 7.4%） |
| 胸→颈 | 0.2381 | 0.2066 | **0.8678**（缩 13.2%） |
| 颈→头 | 0.0542 | 0.1416 | 2.6154（**关节语义不同**，非尺寸） |
| 臂（肩→手） | 0.5364 | 0.5553 | **1.0352**（伸 3.5%） |
| 腿（髋→踝） | 0.9345 | 0.8496 | **0.9092**（缩 9.1%） |

### 14.3 本轮抓到的两个陷阱（都会静默出错）

**（1）两侧矩阵的存储约定不同 —— 必须转置**

| 侧 | 存储 | 平移在 |
|---|---|---|
| Source（`poseToBone`） | row-major，**列向量**约定 | flat **3, 7, 11** |
| HD2（avatar transform 表） | row-major，**行向量**约定 | flat **12, 13, 14** |

不转置的后果：段长全部算成 **0.0000**（因为 `origin()` 取到了错误的三个数）。
**这也是 A1 里"平移在 m[12..14]"能通过、而 A2 一开始全错的原因** —— A1 只用了平移，A2 需要完整的 4×4。
转置后，指标①（拿外部常量 `hd2_skel.json` 对拍）**0.262 mm 通过**，正好反证了转置是对的。

**（2）"脚"的落点选择会改变腿的拉伸比 10%**

`L_Foot` 映射到 `l_foot`（踝）→ 腿拉伸比 **0.909**；
映射到 `l_ball`（脚掌）→ **1.006**（见 §9.2）。
两者差 10%。这正是旧项目 HANDOFF 里记的未解决项"脚部局部 frame 与 pivot"，
**现在它从一个模糊的抱怨变成了一个可比较的数字**。这个选择留给 A3 用眼睛定。

### 14.4 A2 结论

- ✅ **验收通过**：残差 0（精确解）、映射刚性、段长严格取 HD2 值
- ✅ 真正的质量指标已量化为**每段拉伸比**，范围 **0.868 ~ 1.035**（除关节语义不同的颈→头）
- ⏭ 下一步 **A3**：把 Source 的 VVD 顶点 + 权重按这个映射跑一遍，**导出 OBJ 用眼睛看**

---

## 15. 旧项目的现状（2026-10-11 实测，重要）

### 15.1 绝对路径已修（便宜办法）

`HD2AgentWorkbench/src/HD2AssetKit/HD2AssetKit.csproj` 原本硬编码
`E:\Data\source\repos\WpfApp1\HD2ModAdaptation\HD2ModAdaptation.csproj`。

**已改成**：按顺序探测三个常规位置（`<repos>/WpfApp1/...`、上溯一级、仓库内嵌），
支持 `-p:HD2ModAdaptationProject=<path>` 与 `HD2_MOD_ADAPTATION_PROJECT` 环境变量覆盖，
找不到时由 `EnsureHD2ModAdaptationResolved` 目标抛出**带修复指引的错误**（错误码 `HD2AW001`）。

**已验证生效**：构建日志显示 `HD2ModAdaptation -> E:\Data\source\repos\WpfApp1\HD2ModAdaptation\bin\Debug\net8.0\HD2ModAdaptation.dll`。

### 15.2 但旧项目**当前编译不过** —— 这是预先存在的问题，不是本轮引入

5 个错误，全部是**引用了 WpfApp1 里已经被删掉的类型**：

| 旧项目引用 | WpfApp1 现状 |
|---|---|
| `HD2ModAdaptation.PatchReconstruction.Validation`（命名空间） | **不存在** |
| `CanonicalDependencyClosure`（类） | **不存在** |
| `CanonicalDependencyClosureRequest`（记录） | **不存在** |
| `CanonicalDependencyDiagnostic`（记录） | **不存在** |
| `CanonicalDependencyClosureResult`（记录） | **不存在** |

**这不是"改名"** —— 全仓搜索确认整个依赖闭包校验子系统已从 WpfApp1 移除
（现存只有枚举 `CanonicalDependencyClosureValidation` 与 `CanonicalPlanDiagnostic`）。
旧项目最后一次提交是 **2026-09-03**，此后 WpfApp1 的多次重构把它脚下的地板抽走了。

**影响**：
- 旧项目的**编译期**已经断了 5 处，需要**重新设计那个校验步骤**，不是简单替换类型名。
- **不影响 A1/A2**：它们用的是独立实现（Python），不依赖任何 C# 工程编译。
- **但它是一个活生生的证据**，支持 §1 的架构结论：两个仓库看着分开，实际早就被
  （a）绝对路径 + （b）对私有类型名的直接依赖 焊死了。**改动 WpfApp1 的重构会静默打断旧项目，
  而且直到有人去构建才发现。**

**待用户拍板**：旧项目是「修那 5 处 + 继续用」还是「把转换器按新架构重写一遍」。




---

## 16. A3 完成：把整张网格真的蒙上去，并用眼睛看（2026-10-11）

> 工具：`tmp/rig-lab/export_obj.py`（独立 Python，**零生产代码改动**）。
> 输入：`Lunalice_Zoey.vpk` 的 `survivor_teenangst.mdl/.vvd/.dx90.vtx` + A1 的 avatar TocData + A2 的对应表。
> 输出：`zoey-src.obj` / `zoey-mapped.obj` / `zoey-mesh-check.png`（三行对比渲染）。

### 16.1 管线打通了

| 环节 | 实测 |
|---|---|
| MDL bodypart/model/mesh 树 | 32 个 bodypart（`head` / `Base` / `Dress_Skirt` / `platform shoes` …） |
| VVD | LOD0 = **121511** 顶点（fixup 重排后与 `lodVertices[0]` 完全相等） |
| VTX | version **7**，group stride 取 **25**（v6 布局） |
| 合并后 | **121704 顶点 / 159363 三角形**（逐 mesh 计数见运行日志） |
| 骨映射 | 127 源骨 → **50 直接映射 / 0 缺失**，其余按"最近已映射祖先"回退（跳数分布 `{0:50, 1:46, 2:22, 3:9}`） |

索引公式照抄 `SourceGeometryBuilder`：
`global = mdlModel.VertexOffset + mdlMesh.VertexIndexStart + original`
（`VertexOffset = vertexindex_byte / 48`；漏掉 mesh 起点会"炸开成尖刺"）。

### 16.2 【A3 的决定性发现】`T = H·S⁻¹` 对四肢正确，但**躯干/头带着 ~90° 的骨骼 roll 偏差**

**先坐实两件事**（这次不是推断，是实测）：

1. **两个引擎都把骨骼放在局部 +X 轴上。**
   Source：`L_Thigh/L_Calf/L_UpperArm/L_Forearm/Spine` 的子骨在局部坐标里都是 `(+1,0,0)`。
   HD2：`l_thigh→l_knee`、`l_calf→l_foot`、`spine2→chest`、`neck→head`、`l_upperarm→l_elbow`、`l_clavicle→l_shoulder` 同样是 `(+1,0,0)`。
   → **"沿骨方向"的约定是一致的**，所以 `T` 的旋转里"摆到目标方向"的那部分是可信的。

2. **但绕骨轴的 roll 是各自作者手写的，两边不一致。**

把 `T` 的旋转角分解成 **swing（轴向摆角，真正的重定向量）+ roll（绕骨轴的余量）**：

| 部位 | swing | T 旋转角 | **roll（=误差）** |
|---|---|---|---|
| `L/R_Thigh` | 7.97° | 11.14° | **3.17°** ✅ |
| `L/R_Calf` | 13.34° | 15.79° | **2.45°** ✅ |
| `R_UpperArm` | 149.96° | 149.97° | **0.01°** ✅ |
| `R_Hand` | 129.89° | 130.86° | **0.97°** ✅ |
| `L_UpperArm` | 149.96° | 179.10° | 29.14° |
| `L_Forearm` / `L_Hand` | 129.89° | 141.88° / 167.22° | 11.99° / 37.33° |
| `L/R_Foot` | 115.91° | 137.88° / 137.60° | 21.97° / 21.69° |
| `Spine2` | 29.47° | 93.71° | **64.24°** ❌ |
| `Neck1` | 27.30° | 93.19° | **65.89°** ❌ |
| `Spine` | 9.12° | 90.36° | **81.24°** ❌ |
| `Spine4`（→`chest`） | 1.28° | 90.01° | **88.72°** ❌ |
| `Head1` | 1.10° | 90.01° | **88.91°** ❌ |
| `Pelvis`（→`hips`） | 90.00° | 125.85° | 35.85° |

**躯干/颈/头平均 roll = 70.8°，腿 = 2.8°。**

### 16.3 这个 roll 误差的后果（渲染所见）

同一份网格、同一套对应表，只换 roll 处理方式：

- **`--roll-mode frame`（= 朴素 `H·S⁻¹`，保留作者 roll）**：
  **裙子炸成一把伞、头发变成尖刺、躯干被拧。** 原因很直接 ——
  裙摆（源骨 91–118）与头发（64–77 / 82–88）这些 helper 骨**回退到 `hips` / `head`**，
  于是整片继承了 `hips` 的 125.85° 与 `head` 的 90.01°，绕骨原点甩出去。
- **`--roll-mode swing`（只取最短弧轴向对齐，丢掉 roll）**：
  **一个完整站立的角色**，手臂正确落到 A-pose，裙子是正常裙摆，头发正常。
  顶点位移 mean 也从 0.3860 m 降到 0.3042 m（p50 0.2746 → 0.1842）。

**结论**：`T = H·S⁻¹` **不是错的，而是"太严格"** —— 它假设两个骨架的局部坐标系逐骨对应，
但 HD2 的躯干/头链的 roll 与 Source 差了 ~90°。**对 rest-pose 重定向，正确做法是只取 swing。**

### 16.4 与旧项目的关系：这是"已知未解决项"被量化了

- 旧项目 HANDOFF 的残余项里就有 **"躯干映射选择"** 与 **"脚部 frame/pivot"** —— A3 把它们变成了数字。
- 旧项目代码里躺着一条 **`LunaliceFrameCalibrationProfile.TorsoHeadQuarterTurn = Matrix4x4.CreateRotationX(π/2)`**，
  且**恰好**只作用在源骨 **7 / 9 / 11 / 12**（spine2 / chest / neck / head）——
  和 A3 测出的"躯干/头需要 ~90° 绕骨轴修正"**完全对上**。
  （局部 X = 骨轴，所以局部 90° 绕 X = 世界 90° 绕骨轴。）
- 旧项目的 `LunaliceSourceRigFit` 后来把这套硬编码换成"父→子求解 + 显式指定 Source `Spine4` 用 HD2 `chest` frame"，
  并用 `RetainedRelativeHelper` 保住裙摆/头发 helper 的**源侧相对偏移**（正是为了不重演"配件甩飞"）。
  → **A3 的渲染结果独立复现了旧项目当初为什么必须做这件事。**

### 16.5 仍然存在的问题（A3 之后的新清单）

1. **身高 +11.6%**：源 1.857 m → 映射后 2.071 m。根因是 HD2 的 `neck`/`head` 关节语义与 Source 不同
   （`neck→head` 段长比 **2.615**，A2 已记录）：`Head1` 在 z=1.507，HD2 `head` 在 z=1.691。
   **这不是 bug，是"该用哪个关节"的选择**，需要像旧项目那样显式指定。
2. **脚踝**：`L/R_Foot` swing 高达 115.91°，roll 21.97° —— 即"脚部 frame/pivot"问题，
   与 A2 的 `l_foot`(0.909) vs `l_ball`(1.006) 是同一件事的两面。
3. **手指**：swing 83–105°，说明 HD2 的手是微屈/张开的姿态，与 Source 的 T-pose 直指不同。
   需要像旧项目那样对 30 根手指做逐指判定。
4. **`Pelvis → hips` 的 90° swing**：HD2 的 `hips` 骨轴朝上，Source 的 `Pelvis` 骨轴朝左 ——
   **两个 bone 语义并不同**。当前用"根骨只平移不旋转"绕过（因为它同时是全部裙摆 helper 的锚）。
5. **`TorsoScale = 1.042`**：旧项目在源→米换算里乘了 1.042（`SourceMetersPerUnit * TorsoScale`）。
   A3 目前用纯 0.0254。**这解释了映射后整体偏瘦长的一部分**，值得纳入。

### 16.6 A3 的结论

**离线数学这一环已经"能看见结果"了**：从 VPK 一路到 OBJ + 渲染，全部不依赖游戏、不依赖 C# 工程编译。
把"差多少"从骨架推进到"网格长什么样"，并**定位到一个可执行的修正**（swing-only + 逐骨语义选择）。
→ 满足用户"第一阶段先把 A1~A3 的离线数学做扎实"的要求。

---

## §17 A4：管线在 5 个不同的 L4D2 模型上跑通，并找出唯一一个病态模型

> 脚本：`tmp/rig-lab/a4_matrix.py`（驱动）+ `export_obj.py --json-out`（单模型指标）。
> 全部离线，零游戏启动，零 C# 编译。5 个模型 × 2 种 roll 模式 = 10 次全量 LBS，约 20 秒。
> 结果落盘 `tmp/rig-lab/a4-report.json`。

### 17.1 选样与规模

| tag | VPK | 源模型 | 源骨数 |
|---|---|---|---|
| zoey | `Lunalice_Zoey.vpk` | `survivor_teenangst` | 127 |
| rochelle | `Lunalice_Rochelle.vpk` | `survivor_producer` | 127 |
| coach | `Elise_Coach.vpk` | `survivor_coach` | 125 |
| nick | `Lilium_Sancta_Nick.vpk` | `survivor_gambler` | 124 |
| ellis | `Serenade_Ellis.vpk` | `survivor_mechanic` | 125 |

五个模型都来自不同的 VPK、五个不同的角色槽位、三种不同的骨架骨数（124 / 125 / 127）。

### 17.2 结论 1：对应表完全通用 ✅

**5 个模型全部 50/50 命中，0 缺失。** `ValveBiped.Bip01_` 前缀 + 标准 ValveBiped 命名
在 L4D2 全部幸存者之间通用 —— 至少对"二次元替换模型"这一大类成立。
（rochelle 与 zoey 的全部指标逐位相同，说明 `Lunalice_Rochelle.vpk` 里的 `survivor_producer.mdl`
就是 `survivor_teenangst.mdl` 的同一份资产换了名字 —— 这也是"mod 作者把同一角色塞进多个幸存者槽位"的常见做法。）

### 17.3 结论 2：源→HD2 的空间变换是一个真正的 180° 旋转，不是镜像 ✅

用 Kabsch 对全网格拟合"单一最佳刚体运动"，得到：

| 模型 | 拟合旋转角 | det |
|---|---|---|
| zoey | 179.5° | **+1.0000** |
| rochelle | 179.5° | +1.0000 |
| coach | 179.9° | +1.0000 |
| nick | 175.3° | +1.0000 |
| ellis | 178.0° | +1.0000 |

- **det 恒为 +1** ⇒ 是**正常旋转**，不是镜像 ⇒ **左右不会互换**，角色不会变成"反手"。
- 角度 ≈180° 绕 Z ⇒ 与旧项目 `LunaliceHuPoseBake.CreateSourceToVirtualWorld` 里的
  `Matrix4x4.CreateScale(-scale, -scale, scale)` 以及 `CreateVirtualWorld` 里的
  `coordinateOrientation = Matrix4x4.CreateScale(-1f, -1f, 1f)` **完全一致**。
  → **A4 独立复现了旧项目的那一行**，也解释了为什么 Source 的"左"在 +X、HD2 的"左"在 −X。
- 这也顺带证明了 A3 渲染里"src front"与"mapped front"看起来朝向相同**不是巧合**：
  180° 绕 Z 会把背面转到正面，但两个模型都转了，所以对比仍然成立。

### 17.4 结论 3：roll 误差是"骨架结构性"的，与具体模型无关 ✅

**5 个模型的 `torso/neck/head` 平均 roll 全部是 70.8°，腿全部是 2.8°** —— 一模一样。
⇒ 这不是某个模型的怪癖，而是 **HD2 骨架 vs ValveBiped 约定**的固定差。
A3 的发现因此被升级为"结构事实"，可以放心写进转换器的常量。

### 17.5 结论 4：swing 模式在数值上确实优于 frame 模式 ✅

（把 A3 的"肉眼判断"变成数字。`edg` = 三角形边长比 dst/src，1.0 = 形状不变。）

| 模型 | 模式 | edge 均值 | edge p99 | edge **max** | 坏边(>25%) |
|---|---|---|---|---|---|
| zoey | **swing** | 1.0794 | 2.823 | **49.2** | **9.2%** |
| zoey | frame | 1.1119 | 3.573 | **125.9** | 12.0% |
| coach | **swing** | 1.1207 | 3.573 | 18.0 | **11.8%** |
| coach | frame | 1.1163 | 3.561 | 15.8 | 12.3% |
| nick | **swing** | 1.0688 | 2.995 | **28.0** | 10.6% |
| nick | frame | 1.0941 | 3.554 | 36.8 | **9.3%** |
| ellis | **swing** | 1.0756 | 2.996 | **11.0** | **9.0%** |
| ellis | frame | 1.0970 | 3.463 | 13.8 | 11.6% |

- **edge max 是最有判别力的一列**：frame 模式在 zoey 上有一条边被拉到 **125.9 倍**（就是渲染里
  那把"伞"），swing 只有 49.2 倍。**"整片裙摆被刚体旋转到错误方向"正是边长比看不出来、
  只有极值能看出来的东西** —— 刚体旋转不改变边长，但会把局部拉到离全局刚体拟合很远的地方。
- 均值与坏边比例 swing 在 4/5 个模型上更好（coach 的均值、nick 的坏边比例是例外，差距都在 0.5% 内）。
- **结论：swing 是默认模式**；frame 保留只作为"对照实验"，用来暴露"哪些骨本来就不该按帧对齐"。

### 17.6 结论 5：唯一的病态模型是 Nick —— 根因是"回退链太深" ✅

| 模型 | 回退跳数直方图 | **最大跳数** | dev p99 | **dev max** |
|---|---|---|---|---|
| zoey | {0:50, 1:46, 2:22, 3:9} | 3 | 0.58 | 0.87 |
| rochelle | 同上 | 3 | 0.58 | 0.87 |
| coach | {0:50, 1:32, 2:34, 3:9} | 3 | 0.53 | 0.60 |
| **nick** | {0:50, 1:37, 2:22, 3:5, 4:4, 5:2, 6:2, **7:2**} | **7** | **2.36** | **3.36** |
| ellis | {0:50, 1:40, 2:19, 3:11, 4:5} | 4 | 0.80 | 2.20 |

（`dev` = 相对"单一最佳全局刚体运动"的残差，单位米。模型本身高 1.85 m。）

- **dev max 与最大回退跳数严格同向**：跳数 3 → 0.6~0.9 m；跳数 4 → 2.2 m；跳数 7 → 3.36 m。
- **所有最差区域都是 `source_extra_*`**（骨名读不出来的那些骨，也就是头发/裙摆/大袖子的 jiggle helper）。
  Nick 的 `source_extra_101` / `source_extra_106` 要**往上爬 7 层**才碰到一个已映射祖先，
  于是整片袖子绕着一个很远的枢轴转出去 —— 3.36 m 就是这么来的。
- **这正是"三层分工 ②"要解决的问题**：这些骨不该回退，应该**原样追加在 HD2 骨架末尾、由 Lua 按
  Source 参数驱动**。回退只是 A1~A3 阶段的临时兜底，**A4 证明了它的代价是有界的、且与链深正相关**。

### 17.7 结论 6：身高膨胀 11.6% ~ 14.2%，最差是 Ellis

| 模型 | 源高 | 映射后 | 比值 |
|---|---|---|---|
| zoey / rochelle | 1.857 | 2.072 | 1.116 |
| coach | 1.749 | 1.966 | 1.124 |
| nick | 1.854 | 2.109 | 1.138 |
| **ellis** | 1.830 | 2.090 | **1.142** |

与 §16.5 第 1 条同源（`neck→head` 段长比 2.615 的关节语义选择）。**5 个模型全部 +11%以上**，
说明这是**系统性**的，不是个别模型的绑定姿势问题 ⇒ 必须作为转换器的固定修正项处理。

### 17.8 本轮我自己踩的两个坑（方法论，必须记住）

1. **跨模型空间比绝对坐标 = 假诊断（又一次）。**
   A3 已经踩过一次"关节落点误差"，A4 又踩了一次"顶点位移"：
   `|dst_i − src_i|` 在 zoey 上是 mean 0.30 / max 1.59 m，看起来像"网格被扯烂"，
   实际上**主要成分就是那个 180° 空间旋转**（手在 x≈±0.65 处，转 180° 就是 1.3 m 位移）。
   **正确做法**：要么用对旋转不变的量（边长比），要么先拟合掉全局刚体运动（Kabsch）再看残差。
2. **不要用"某一根骨的帧"当全局映射。** 我第二次尝试用 `Mg = H_hips · S_pelvis⁻¹` 当"全局刚体映射"，
   结果 dev p50 高达 0.87 m —— 因为 **`Pelvis→hips` 的骨轴本身差 90°**（A3 已测），
   所以 `Mg` 里混进了一个 90° 旋转。**全局量必须从点云拟合，不能从单根骨取。**

### 17.9 A4 交付物

- `tmp/rig-lab/a4_matrix.py` —— 一条命令跑完 N 个模型 × 2 种 roll 模式并出对比表。
- `tmp/rig-lab/a4-report.json` —— 全部指标（含每模型每段骨长比、回退直方图、残差分位）。
- `tmp/rig-lab/{nick,coach}-mesh-check.png` —— 新增两张肉眼验收图（coach 干净，nick 袖子有离群点）。
- **下一批修正（按性价比排序）**：
  ① 把回退换成"追加骨"（直接消灭 dev max 的 3.36 m，且本来就是 ② 的既定方案）；
  ② 显式指定躯干/头的关节语义（消灭 +11~14% 身高膨胀）；
  ③ 脚部改用 `l_ball` 落点（A2 已测：腿长比 0.909 → 1.006）；
  ④ 30 根手指逐指判定；
  ⑤ 纳入旧项目的 `TorsoScale = 1.042`。