# HD2 物理骨骼：实测结论 + 实施方案（交接文档）

> **这份文档是给"上下文压缩后的我"看的。**
> 它承载三轮实测（L0 骨嫁接 / L1 护甲自带状态机 / P3 资产注入）的全部结论、格式技艺与踩坑记录。
> 读完这一份就能接着干，不需要重新试错。
>
> 关联文档：
> - `docs/hd2-native-bone-baseline-20261030.md` —— 原生能力基线（骨骼宇宙、Lua 面、注册层普查）
> - `docs/hd2-armor-physics-research.md` —— 早期物理调研（Lua API 一手探测、布娃娃/披风）
> - `docs/hd2physbone-embedded-patch-protocol.md` —— `HD2PBX01` 容器协议（已定稿，未实现 C#）
> - `tmp/milltina-test/说明*.md` —— 各轮测试包的现场说明

---

## 0. 一句话现状

**加自定义物理骨这条链路已经全线打通（2026-10-31，见 §13）。**
离线：往护甲 Unit 插一个节点 + 重指向 + 重算骨骼矩阵（**且必须把头部 0x44 跟着 +64**）。
运行时：Lua 每帧写这个节点的姿态，只换旋转、保留平移、限幅。
实测：bind pose 与原版一致，驱动时乳房完整变形、无拉伸。

纯 patch 那一档（6 根骨 + 可调参数）也仍然可用（§3.3 P3-1）。

---

## 1. 结论速查

### 1.1 纯 patch 能力矩阵（最终版）

| 能力 | 结论 | 依据 |
|---|---|---|
| 改 6 根骨的物理参数 | ✅ **可用** | P3-1：重封 12.3 MB SM，`support_mg` 角度限值 5°→20°，进游戏晃动明显变大 |
| 选骨 / 选轴心 | ✅ 有限 | 轴心 = 骨原点，硬绑。6 根里只有 `support` 在正中线 |
| 加骨 | ❌ | P3-2b / P3-2c 双双失败（见 §4） |
| 自定义轴心 | ❌ | 连带加骨失败 |
| 换驱动信号（去掉瞄准耦合） | ❌ | 6 根骨全是 `chest/spine1/spine2` 的子骨 |

### 1.2 三条已实测成立的机制

1. **引擎接受对任意 archive id 的 patch**：`18235e0c9ec0e636.patch_0`（玩家骨架归档）能被覆盖，同 id 的 `state_machine` 覆盖生效。
2. **`state_machine` 的 `bone_index` 指向 `bones` 资源的下标**，9 个 float 参数由我们写 → 参数完全可控。
3. **`BoneInfo.RealIndices` + `BoneMatrices` 是成对的两张表**，重指向必须同时改，否则网格被搬进另一根骨的坐标系。

### 1.3 唯一可行的增强路径

```
离线：往【护甲 Unit】的场景图插节点（16 KB/件，不碰玩家骨架）
运行时：Lua 每帧写这个节点的世界姿态
参数：走 HD2PBX01 容器塞进 patch，不写进 Lua 源码
```

不碰 `bones` 资源、不碰 3522 份动画、不碰状态机、不碰玩家骨架。
每帧成本 = 骨数 × 1 次 `set_local_pose`，随骨数线性。

---

## 2. 已验证的硬事实

### 2.1 HD2 的骨骼宇宙

**玩家 avatar 是唯一一份共享骨架**（`format-confirmed`）：

| 项 | 值 |
|---|---|
| 归档 / FileId | `18235e0c9ec0e636` / `0x4d1c334d294dfa97` |
| `bones` | **1484 字节**，`nameCount=90 / lodCount=6`，LOD `[90,82,59,35,32,30]` |
| `unit` | **21504 字节**，**107 节点**，`BonesRef=0x4d1c334d294dfa97`，`CompositeRef=0xc6a69260cc389bcd`，`streamInfoOffset=0`（几何在 composite 里，场景图在本 payload 内） |
| `state_machine` | **12,305,624 字节**，`ragdoll_count=6`，`ragdoll_offset=12,305,288`（**表正好收在 EOF**） |
| 同 FileId 另有 | `ragdoll_profile` 82484 B（23 具刚体）、`ik_skeleton` 5096 B、`physics` 300 B |
| 归档内家底 | 3522 `animation` / 30 `unit` / 9 `bones` / 6 `state_machine` / 14 `physics` / 2 `cloth` / 2 `ik_skeleton` / 1 `ragdoll_profile` |

**6 根抖动骨**（`state_machine` 里 `ragdoll_count=6`，逐条解析）：

| bone_index | 骨名 | 父骨 | 挂点世界坐标 | p0 权重 | p4 角度限值 | p6/7/8 |
|---:|---|---|---|---:|---:|---|
| 26 | `attach_intelpad` | spine1 | (0.098, −0.145, 1.123) | 0.5 | 10° | −10/10/0 |
| 68 | `target_designator` | spine1 | (−0.235, 0.013, 1.105) | 2.5 | 10° | — |
| 81 | `support` | chest | (−0.022, −0.226, 1.397) | 5 | **5°** | — |
| 63 | `sling` | spine2 | (0.154, 0.150, 1.100) | 1.5 | 10° | 40/80/35 |
| 71 | `support_mg` | chest | (−0.181, −0.270, 1.404) | 5 | **5°** | — |
| 54 | `attach_samplepouch` | spine1 | (0.155, −0.089, 1.112) | 1 | 10° | — |

坐标系：**+Y = 前，+Z = 上**（`backpack`/`cape1..8` 在 −Y，`aim_weapon` 在 +Y）。

**引擎的每 Unit 物理是通用的**（`format-confirmed`，全库扫描）：

| 指标 | 值 |
|---|---|
| 带物理条目的状态机 | 348 条 → 去重 **52 个 Unit** |
| 物理骨条目总数 | **3121** |
| 单 Unit 最多 | **32 根**（`jiggle1..jiggle32`） |
| 不同骨名集合 | 39 种 |
| 不同参数组合 | 106 种 |
| 全库 `bones` 资源 | 3074 个（555 种骨名集合） |
| 全库 `state_machine` | 2099 个 |

⇒ **引擎侧没有 6 根上限、没有名字限制。6 根是"玩家这份资产"的限制，不是引擎能力。**

**护甲 Unit 没有自己的骨架**（AF-52 右臂 `0x5c58b133661ee515`，16144 字节）：

- 38 个场景图节点：28 个命中 avatar 90 骨表 + `skeleton` + 2 个 `*_culling` + `game_mesh`/`grp_*`/`g_*`
- 该 FileId 在 `bones` 表、`state_machine` 表里**都不存在**
- 全库 `bones` 资源里含 `support_mg` 的**只有玩家那一个**
- 躯干类护甲 Unit 的 `bonesRef = 0`（不引用 bones 资源）

### 2.2 资产格式（本轮最重要的技艺）

#### Unit 头部（payload 起始 0x00..0x80，全部 uint32）

| 偏移 | 含义 |
|---|---|
| 0x00 | NameHash 低 32 位 |
| 0x08 | **BonesRef**（uint64） |
| 0x10 | **CompositeRef**（uint64） |
| 0x20 | 另一个名字哈希（uint64） |
| 0x28 | 常量 2 |
| 0x2c | version（玩家/护甲都是 `0x00a4cd36`） |
| 0x30 | unreversedLodGroupListDataOffset |
| 0x34 | **transformInfoOffset** |
| 0x38 / 0x3c | 144 / 128（两段小结构的大小） |
| 0x44 / 0x48 | 未解（护甲 Unit 非零：9984 / 12384；玩家 Unit 为 0） |
| 0x4c | customizationInfoOffset |
| 0x50 | 未知（玩家非零 0x3A80；护甲为 0） |
| 0x54 | 未知（多为 0） |
| 0x58 | **boneInfoOffset** |
| 0x5c | **streamInfoOffset**（composite 型为 0） |
| 0x60 | **endingOffset** |
| 0x64 | **meshInfoOffset** |
| 0x70 | **materialsOffset** |

#### TransformInfo 块（**16 + n × 136 字节**，四个数组**分开**存放）

```
+0        count (uint32)
+4/8/12   reserved × 3
+16                locals   [n × 64]   Rotation[9]@0, Position[3]@36, Scale[3]@48, Padding@60
+16 + n*64         matrices [n × 64]   16 × float32，世界 bind（行向量约定，平移在 [12..14]）
+16 + n*128        entries  [n × 4]    u16 Increment, u16 ParentIndex（ParentIndex 是普通下标，自指=根）
+16 + n*132        hashes   [n × 4]    uint32 名字哈希
块尾 = +16 + n*136
```

**插一个节点 = 重建这四个数组 + 把块之后的内容整体后移 136 + 头部 5 个偏移各 +136**
（`0x50 / 0x58 / 0x5c / 0x60 / 0x64 / 0x70` 里**原本非零的那些**；**0 表示该段不存在，绝不能加**）。

玩家 Unit 实测：`transformInfoOffset=0x190`，count=107，块尾 `0x3A78`；
`chest` = 节点 **[28]**，父 26，世界位移 (−0.0000, −0.0294, 1.3437)。

#### BoneInfo（**调色板槽 = 索引 + 骨骼矩阵，成对**）

```
每个 BoneInfo（对应一个材质 / LOD 组）：
  RealIndices[i]  uint32     第 i 槽 → TransformInfo.NameHashes 的下标
  BoneMatrices[i] 64 字节    第 i 槽的 4×4 骨骼矩阵（NumBones 个，紧密排列）
  Remaps[materialIndex].FakeIndices[fake] → RealIndices 的下标
```

**不变式（残差 0，21 个 Unit × 全部槽验证）**：

```
BoneMatrices[i] · TransformInfo.Matrices[RealIndices[i]]  ==  X
⇒ BoneMatrices[i] = X · inverse(world_i)          X = 该 BoneInfo 的网格节点变换，组内恒定
蒙皮：v' = v · BoneMatrices[i] · world_bone(t)；静止姿态退化为 v' = v · X
```

**重指向的两条安全条件**（缺一不可）：

```
① BoneMatrices_B = BoneMatrices_A · world_A · inverse(world_B)      （矩阵一致）
② 该骨的「运行时姿态」== Unit 里写死的 TransformInfo 姿态            （姿态一致）
```

②不成立时网格被拖到该骨的真实运行位置 —— `sling` 就是这种情况（它被武器系统摆到武器上），**已出局**。

本仓 `CanonicalInverseJointMatrixCompiler.Build` 算的正是 `mesh · inverse(bone)`，与不变式一致 —— **生产链路本来就是对的**。

#### bones 资源

```
+0   nameCount (uint32)
+4   lodCount  (uint32)
+8   lodCount × float32（未知，通常 0）
     nameCount × uint32 哈希
     lodCount  × uint32 LOD 骨数（前缀长度语义，递减）
     NUL 分隔的名字表
```

玩家：`8 + 6*4 + 90*4 + 6*4 = 416` 字节表头，其余 1068 字节是名字。

#### state_machine

```
+0..+75   19 个 uint32 头部：
          0 unk(=22)  1 layer_count  2 layer_data_offset  3 anim_events_count  4 anim_events_offset
          5 anim_vars_count  6 anim_vars_offset  7 blend_mask_count  8 blend_mask_offset
          9..16 unk00..unk03 的 size/off
          17 ragdoll_count   18 ragdoll_offset
ragdoll 表：ragdoll_offset 起，每条 56 字节 = bone_index(u32) + params[9](float32×9) + hash(u64) + kind(u32) + pad(u32)
```

**玩家的 ragdoll 表正好收在文件末尾**（`offset + count*56 == 文件长度`）⇒ 加条目只需**追加 56 字节 + 把 count 加 1**，零重排。

#### animation

```
+0x00  0
+0x04  **骨数**（玩家归档里所有动画都是 90）
+0x08  时长（秒，float）
+0x0c  数据长度（= 文件大小）
```

**这一条是纯 patch 加骨失败的直接原因。**

### 2.3 哈希算法

```
boneHash = murmur64(name, seed=0) >> 32
```

已验证 7 个骨名逐位吻合：`chest=0x37ef0820`、`torso_culling=0xa620a54c`、`spine1=0x58d886fb`、
`support_mg=0x07fa5ada`、`sling=0xec864c61`、`head=0x8c5570c9`、`hips=0x668f6a68`。
`jig_breast = 0xdd5eb5fe`。

### 2.4 三层边界

| 层 | 在哪 | 能改吗 | 对物理有用吗 |
|---|---|---|---|
| **资产层** | `data\*.patch_N` 覆盖包内资产 | ✅ 任意类型 | ✅ **物理参数的唯一所在** |
| **注册层** | `data\game\generated_*.dl_bin`（55 个）+ `dl_library.dl_typelib` | ✅ 解封/重封（libsodium `crypto_box`） | ❌ **无物理配置**（全库普查：`ragdoll`/`jiggle`/`havok`/`collision`/`constraint` 命中 **0**；`generated_body_settings` 只有 116 字节、纯指针） |
| **代码层** | `game.dll` + Lua 暴露面 | ❌ 不再能写内存 | ❌ 物理创建接口被整层裁掉 |

注册层的真实用途是**登记**：`generated_customization_armor_sets`（护甲→unit 表，我们已经在写）、
`generated_entities`（实体→unit→名字，47 MB，**不含组件表**：明文里 `*Component*` 命中 0）。

### 2.5 Lua 的物理面

**没有"造物理"，只有"接管骨骼"**：

- ❌ 命名空间全被裁：`PhysicsWorld`(19) / `Actor`(43) / `Joint`(41) / `Mover`(16) / `Cloth`(3) / `HumanIK`(235) / `Vehicle`(46) / `Raycast` / `NavigationMesh`
- ❌ `EntityManager.actor_component` 被砍（上游 20 个成员里有）→ 能造实体，造不出物理
- ❌ `Unit.create_actor` / `create_custom_joint` / `create_cloth`
- ❌ `Mesh` 没有骨骼/权重接口
- ❌ 布娃娃：`can_ragdoll` / `start_ragdoll` / `is_ragdolled` **不在方法表里**（社区靠签名扫描 RVA，写内存不可用后这条路断了）
- ✅ `EntityManager`（21 成员 + 13 组件访问器）
- ✅ `SceneGraphComponent`：`world_pose` / `set_local_pose` / `set_local_rotation` / `set_parent` / `node_index` / `num_nodes`
- ✅ `Unit.set_animation_bone_mode(unit, 'ignore')`、`Unit.set_bones_lod(unit, n)`
- ✅ `AnimationBlenderComponent.crossfade(...)`（绕过状态机叠动画）
- ✅ `Unit.animation_set_constraint_target(unit, i, value)`
- ✅ HD2 独有：`Unit.update_jiggle_bone_data` / `update_jiggle_bone_orientation`

⚠️ **`SceneGraphComponent` 上游 26 个方法里没有任何"创建节点"** → **Lua 不能凭空加骨**，
骨必须离线写进资产，Lua 只负责每帧写姿态。这正是 AQ 版 `HD2IRG1` 的做法。

---

## 3. 实测记录

### 3.1 L0：用现有 6 根骨嫁接（社区做法）

样本：`VRchat_Milltina_Asmodeus 可脱穿 替换RS-100 制裁者 (2).7z`（纯 L0：只有 patch + manifest，无 Lua、无自定义骨）。

- 胸部的 6401 个顶点（权重和 3252.3）挂在 **`support_mg`** 上，其中 2840 个权重 > 0.75
- 另外 6 个部件几乎每个顶点都带一点点 **`attach_intelpad`** 残留权重（权重转移的渣）
- 现象：站着不动只转镜头就晃 —— 因为 6 根骨全是 `chest/spine1/spine2` 子骨，而这条链正是瞄准驱动的

**轴心对照实验**（只改调色板，权重不动，逐变体只差 12 个 uint32 + 12×64 字节矩阵）：

| 变体 | 骨 | 挂点→胸部质心 | 结果 |
|---|---|---:|---|
| V0 | `support_mg` | 0.553 m | 斜着晃（轴心偏右 18 cm、靠后 24 cm） |
| V1 | `sling` | 0.186 m | **被拉到武器上、跟着武器动** → 该骨被武器系统摆走，出局 |
| V2 | `attach_samplepouch` | 0.342 m | 位置正常，比基线略大，偏"平着晃" |
| V3 | `attach_intelpad` | 0.374 m | 位置正常（略低），像被小腹一点往下拉 |
| V4 | `support` | 0.471 m | **正中线、胸高**，"揉馒头"——六根里几何上最接近的 |
| V5 | `chest`（零物理） | — | **完全正常、不晃** ⇒ 蒙皮与矩阵都对，锅全在物理侧 |

### 3.2 L1：给护甲 Unit 自带 bones + state_machine —— 否定

| 变体 | 做法 | 结果 |
|---|---|---|
| L1-A | 把已有节点 `torso_culling` 搬到胸部、6401 顶点重指向它 | **胸部塌成薄层、被拉到右臂附近** ⇒ 加权到「avatar 骨架里没有对应名字」的节点 = 顶点塌陷 |
| L1-B0/B1/B2 | A + `bonesRef` + 状态机（极简 / 真模板） | 与 A 无区别（A 已塌，无法判读） |
| L1-D0 | **原包几何一个字节不动**，只把 `bonesRef` 0 → bones 资源 | 与原包无区别 ⇒ **`bonesRef` 可安全设置** |
| L1-D1 | D0 + SM 1 条，`bone_index=17`（106 骨表的 `chest`），20° | **与原包无区别** |
| L1-D2 | 不动 `bonesRef`，SM 1 条 `bone_index=7`（avatar 90 骨表的 `chest`） | **与原包无区别** |

⇒ **护甲 Unit 自己的 `state_machine` 不会被 tick。** 只是"在同一 FileId 下多放一份 SM 资产"不够。
⇒ 副产品：**`bonesRef` 从 0 指到 patch 里的 bones 资源是无害的。**

### 3.3 P3-1：玩家 SM 参数注入 —— **成功**

- 抽出 `state_machine`（12,305,624 B）→ 改 `support_mg`（bone 71）的 `params[4]`：`0.0873`(5°) → `0.349`(20°)
- 重封成 `18235e0c9ec0e636.patch_0`（248 字节头部 + 12,305,624 字节 payload，单条目）
- 与原始 SM **全量比对：只差 3 个字节**，偏移 12,305,532（= `ragdoll_offset + 4*56 + 4 + 4*4`）
- **进游戏实测：晃动幅度明显变大** ✅

⇒ 三条同时成立：引擎接受对该 archive 的 patch、同 id SM 覆盖生效、物理条目按我们写的参数工作。

### 3.4 P3-2 系列：给玩家骨架加骨 —— 否定

| 变体 | bones | unit | SM | 结果 |
|---|---|---|---|---|
| P3-2 | 91 名，LOD0=91 | 108 节点 | 7 条（`bone_index=90`） | 5–20 秒后崩（模型先消失→画面静止→崩） |
| P3-2b | 91 名，LOD0=91 | 108 节点 | **原样 6 条** | **仍坏**：多次运行结果各异（直接崩 / 模型整体消失但游戏活着 / 只有乳房区域网格消失 / 过场后崩） |
| P3-2c | 91 名，**LOD0 保持 90** | 108 节点 | 7 条 | **不崩了**，但：玩家整体材质丢失（品红/晶紫缺省）、挂在玩家身上的 unit 逐渐消失（大衣、乳房区域网格） |

转储（3 份，都是 `0xC0000005`）：

| 转储 | 故障偏移（helldivers2.exe） | 越界读地址 |
|---|---|---|
| 第 1 轮 P3-2 | `+0xd96c9c` | `0x0000031bbcbe0100` |
| P3-2b run2 | `+0x64a62b` | `0x0000021e0b3c26f0` |
| P3-2b run4 | `+0xd96c9c` | `0x000003eaf3db5140` |

**同类故障、不同位置、每次垃圾地址都不同 ⇒ 堆被写坏，不是确定性校验失败。**
这正是"多次运行结果差异巨大"的原因。

---

## 4. 为什么纯 patch 加骨失败（死结）

- 新骨**放进 LOD0** → 动画资产每份都写死 **90** 根骨（`+0x04`），对不上 → 堆被写坏 → 非确定性崩溃。
- 新骨**排除出 LOD0** → 堆不坏了，但它不是"真正被动画求值的骨"，绑定/剔除链在它身上失配 → 材质解析失败 + unit 逐个消失。

**没有一个索引能同时满足"动画能对齐"和"物理能用"。**
要让新骨可用必须同时改 **3522 份动画**，不可行。

---

## 5. 实施方案

### 5.1 管理器侧：SM 参数注入作为"自管理资产"

**目标**：让纯 patch 那一档（6 根骨 + 可调参数）变成产品能力，玩家不用手动搬文件、不用管游戏更新。

#### 数据流

```
启动 / 玩家点"部署"
  ├─ 读管理设置：是否启用"物理参数"功能（默认关）
  ├─ 若启用：
  │   ├─ 计算当前原版 state_machine 的指纹（SHA256）
  │   ├─ 与缓存里的指纹比对
  │   │   ├─ 相同 → 复用已生成的 patch
  │   │   └─ 不同（游戏更新）→ 重新抽取 → 注入 → 重封 → 更新缓存
  │   └─ 把该 patch 排到部署计划的【最后一位】
  └─ 部署
```

#### 关键设计点

1. **自管理资产，不进玩家的 mod 列表。**
   落在管理器的私有目录（如 `<管理器目录>/ManagedPhysics/<archiveId>.patch_<n>`），
   在部署计划里标记为 `isGenerated: true`，UI 上不显示为可勾选的 mod。

2. **部署永远最后一位。**
   部署时扫描计划里所有 `(archiveId, patchIndex)` 占用，取 `max + 1`；
   并在部署状态记录里显式记下"这一条是管理器生成的"，下次重新生成前先删旧的。
   **必须有冲突检测**：如果两个 mod 都想写同一个 `patch_N`，谁覆盖谁取决于顺序 —— 这一点要在计划阶段就报出来。

3. **重抽必须从原版 bundles 抽，绝不能从"已部署的 patch"抽。**
   否则每更新一次就把上一版的补丁叠进新基线，补丁会一代代累积、越滚越偏。
   实现上要么抽之前先把自己那条 patch 移走，要么按 `(archiveId, entryKey)` 直接读 `bundles.*.nxa`
   （`GameDataPackageResolver` 支持，但要绕开 data 目录下的 patch 文件）。

4. **版本判据用原版 SM 的 SHA256。**
   12 MB 算一次几百毫秒，启动时可接受；或只在 `bundles` 的 mtime/size 变了时才重算。

5. **生成流程（已验证可用）**
   ```
   抽取：hd2mod gamedata extract asset 0xa486d4045106165c 0x4d1c334d294dfa97 `
           --game-data "<游戏 data 目录>" --output <dir> --overwrite --json
         → 产出 <dir>\state_machine_0x4d1c334d294dfa97.toc（正好 12,305,624 字节，未压缩，已实测）
         ⚠️ 这条命令不接受 --manager-root；只接受位置参数 + --game-data/--output/--overwrite/--json
   注入：改 params[9]（见 §6 的 appendsm / 直接改某条 params）
   重封：PatchArchiveWriter.WriteAsync(srcPatch, outDir, edits=[], additions=[SM 条目], removedEntries=全部源条目)
   命名：<archiveId>.patch_0 + .stream(0B) + .gpu_resources(0B)
   ```

6. **参数集设计（给玩家看的）**
   每根骨 9 个 float 里真正有意义的是：`p0 权重/刚度`、`p1 重力`、`p2 线阻尼`、`p3 角阻尼`、`p4 角度限值`。
   建议 UI 只暴露"幅度"一个滑杆，内部映射到 `p0` 与 `p4`；
   预设可以按"部位"给：腰侧挂件→`attach_intelpad`、裙摆上半→`support`、披风/背带→`support_mg`。
   **`sling` 一律不提供**（它被武器系统摆走，见 §2.2 条件②）。

### 5.2 Lua 侧：最小接口

**只做两件事**：

```
① 读父骨世界矩阵：SceneGraphComponent.world_pose(entity, nodeIndex)
② 写新骨世界姿态：SceneGraphComponent.set_local_pose(entity, nodeIndex, matrix)
   （或用 Unit.update_jiggle_bone_data 驱动已有的 6 根）
```

**为什么这样能绕开"只转镜头就晃"**：
引擎的抖动骨在父空间解算，父骨（`chest`）被瞄准旋转 → 弹簧被激励。
我们自己算的话，从父骨世界矩阵取**平移加速度**（对世界位置二阶差分），把旋转分量丢掉，
弹簧只响应"人真的在动"，不响应"视角在转"。这是引擎侧做不到、必须由我们做的事。

**参数不写进 Lua**：走 `HD2PBX01` 容器（`docs/hd2physbone-embedded-patch-protocol.md` 已定稿），
由 patch 携带，Lua 只读。这样"参数跟着 mod 走"，Lua 代码可以是通用的一份。

**帧成本**：每根骨 1 次 `set_local_pose`。10 根骨 = 每帧 10 次调用，
与"外接一整套物理系统"完全不是一个量级 —— **帧率代价取决于加几根骨，不取决于要不要用 Lua。**

### 5.3 离线验证回路

物理"好不好看"是最贵的迭代循环，必须能在离线做：

1. 取真实 `animation` 资产（36659 个可选）→ 解出骨骼轨迹
2. 离线跑弹簧解算器 → 导出新骨轨迹
3. 用现有渲染工具（`tmp/rig-lab/patch_view.py` 等）出对比图
4. 参数调好后再进游戏验证一次

这样进游戏只做最终确认，不做参数搜索。

### 5.4 里程碑

| 阶段 | 内容 | 验收 |
|---|---|---|
| **M-A** | 管理器侧 SM 参数注入（自管理资产 + 更新检测 + 部署最后一位） | 游戏更新后自动重生成；玩家不搬文件；参数改动在游戏里可见 |
| **M-B** | `HD2PBX01` 的 C# 读写 + 贯通所有 patch writer | 容器在重建/合并/导出后仍在，记录 hash 不变 |
| **M-C** | 离线解算器 + 渲染对比回路 | 同一份动画，离线解算结果与游戏内一致（可对拍） |
| **M-D** | Lua 侧最小接口（读父骨 / 写新骨） | 10 根骨不影响帧率；只转镜头不晃 |
| **M-E** | 护甲 Unit 加骨 + 刷权重（离线，已有工具） | 新骨跟随父骨、可被 Lua 驱动 |

---

## 6. 工具与复现命令

### 6.1 `tmp/m2-probe`（本轮主力工具，gitignored 临时工程）

```powershell
$exe = 'tmp\m2-probe\bin\Release\net8.0\m2probe.exe'
$hash = 'HD2ModManager\Resources\AssetMetadata\bonehash.txt'
```

| 命令 | 作用 |
|---|---|
| `toc <patch>` | 列出 patch 条目 |
| `dumptoc <patch> <typeHex> <fileHex> <out>` | 从 patch 里取出某个 payload + 打印 Unit 头部 |
| `hash <name...>` | `murmur64(name)>>32` |
| `weights <patch> <hashFile>` | 每根骨的真实权重分布（Type6/7 → Remaps → RealIndices → NameHashes） |
| `nodes <patch> <hashFile>` | 每个节点的世界静置位置 |
| `entries <patch> <hashFile> [unitHex]` | 每个节点的 Increment / ParentIndex / 位置 |
| `infos <patch> <hashFile> [unitHex]` | 每个 BoneInfo 的 X 与骨名统计 |
| `matrices <patch> <hashFile> [unitHex]` | 前几个槽的 `stored` 与 `world` 矩阵 |
| `matrel <patch> <hashFile>` | **校验 `stored·world == X` 是否逐槽恒定**（静止姿态不变） |
| `repoint <in> <out> <src> <dst> <hashFile>` | 只改 `RealIndices`（**会搬网格，别单独用**） |
| `addpivot <in> <out> <pivot> <anchor> <source> <hashFile> [--at x y z]` | 搬节点到枢轴 + 重指向 + 重算矩阵（**两条条件都满足**） |
| `addbones <in> <out> <name...> [--keep-lod0]` | 给 bones 资源追加骨名 |
| `addunitnode <in> <out> <name...> --parent <骨> --at x y z` | 给 Unit 的 TransformInfo 插节点 |
| `patchaddnode <patch> <outDir> <new> <parent> <x> <y> <z> <probeBone>` | 对 patch 里所有用到 probeBone 的 Unit 插节点并重建 |
| `appendsm <in> <out> <boneIndex> <p0..p8>` | SM 末尾追加一条物理条目 |
| `mksmpatch <sm> <outDir> <archiveId> <fileId> <boneIndex> <paramIndex> <value> <srcPatch>` | 改一条参数并重封成同 id patch |
| `mkavatarpatch <outDir> <archiveId> <bones> <unit> <sm> <srcPatch>` | 三合一封包 |
| `cmpavatar <patch> <unitHex> <gameData> <archive> <hashFile>` | 比较护甲 Unit 与 avatar 的节点变换（**复合型会失败，见 §7**） |

### 6.2 `tmp/gdscan`（全库扫描器）

```powershell
tmp\gdscan\bin\Release\net8.0\gdscan.exe boneaudit tmp\gdscan\boneaudit.json   # 全库 bones + state_machine
tmp\gdscan\bin\Release\net8.0\gdscan.exe dump <typeHex> <fileHex> <outDir>     # 从游戏包抽资产
tmp\gdscan\bin\Release\net8.0\gdscan.exe pkg <archive>                         # 列归档条目
tmp\gdscan\bin\Release\net8.0\gdscan.exe type <typeHex>                        # 列某类型全部条目
tmp\gdscan\bin\Release\net8.0\gdscan.exe sm <typeHex> <fileHex>                # 解析状态机物理表
```

### 6.3 `.dl_bin` 解封 / 重封

```powershell
tmp\decodeall\bin\Release\net8.0\decodeall.exe 'D:\...\data\game' 'tmp\dlbin-plain'   # 全部解封成 .plain
tmp\plain-dump\bin\Release\net8.0\plain-dump.exe <in.dl_bin> <out.plain>
tmp\reseal\bin\Release\net8.0\reseal.exe <sealed|*.plain> <out> [offset hexbyte]      # 重封 + 自校验
```

### 6.4 管理器 / 库 / 部署（CLI）

```powershell
$cli='HD2ModCli\bin\Debug\net10.0\hd2mod.exe'
$mgr='HD2ModManager\bin\Debug\net10.0-windows'
$gd ='D:\SteamLibrary\steamapps\common\Helldivers 2\data'

& $cli library sync plan --manager-root $mgr --library E:\mod --output plan.json --json
& $cli library sync validate --plan plan.json --manager-root $mgr --json
& $cli library sync apply --plan plan.json --manager-root $mgr --confirm --json

& $cli profile create '<名称>' --manager-root $mgr --confirm --json
& $cli profile add '<配置>' <GUID> --manager-root $mgr --confirm --json
& $cli profile remove '<配置>' <GUID> --manager-root $mgr --confirm --json
& $cli profile set-active '<配置>' --manager-root $mgr --confirm --json

& $cli deploy plan active --manager-root $mgr --game-data $gd --output dp.json --json
& $cli deploy validate --plan dp.json --manager-root $mgr --game-data $gd --json
& $cli deploy apply --plan dp.json --manager-root $mgr --game-data $gd --confirm --json
```

**管理器运行时 CLI 写库会被拒**（`ManagerProcessRunning`）。用户已授权：可以直接
`Stop-Process -Name HD2ModManager, helldivers2 -Force` 后继续。

部署用的是**符号链接**（`Get-Item` 的 `Target` 指向库里的文件，`Length` 显示 0 是正常的）。

### 6.5 崩溃转储解析（minidump）

关键偏移：`Exception` 流在目录里 type=6；`ModuleList` type=4。
`MINIDUMP_MODULE` = **108 字节**（`BaseOfImage(8) SizeOfImage(4) CheckSum(4) TimeDateStamp(4) NameRva(4) VersionInfo(52) Cv(8) Misc(8) R0(8) R1(8)`）。
`MINIDUMP_EXCEPTION_STREAM` = `ThreadId(4) pad(4) Code(4) Flags(4) Record(8) Address(8) NParams(4) pad(4) Info[15](120) Ctx(8)`。

---

## 7. 踩过的坑（每条都是真金白银）

1. **调色板槽是「索引 + 矩阵」一对。** 只改 `RealIndices` 会把网格整体搬进另一根骨的坐标系（"乳房被拉到武器上"）。
2. **重指向还要满足"运行时姿态 == 写死姿态"。** `sling` 违反 → 出局。
3. **`bonesRef = 0` 表示"不引用 bones 资源"，不是"引用 0 号"。** 护甲躯干 Unit 都是 0。
4. **插节点时，头部偏移字段里为 0 的绝不能 +delta。** 护甲 Unit 的 `0x50` 是 0，加了 136 就变成非法偏移（"out-of-range counts or offsets"）。
5. **`0x5c`（streamInfoOffset）也要跟着 +delta。** 玩家 Unit 它是 0（composite），护甲 Unit 非零 —— 我第一版漏了它，护甲 Unit 全部解析失败。
6. **`PatchArchiveWriter` 传空 `GpuResourceData` 会清空该 Unit 的顶点数据**（表现为解析出 0 根骨、gpu 从 57.8 MB 掉到 15.1 MB）。必须用 `PatchEntryPayloadReader.ReadPayloadAsync` 把原始 payload 原样带回。
7. **`GameDataUnitMeshReader` 读不了玩家 avatar 的 Unit**（composite 型，报 "Composite Unit payload does not contain the requested Unit name hash"）。要拿它的 TransformInfo 只能按偏移手工解析。
8. **`bones` 资源是动画的索引空间**，动画里写死了骨数（90）。改骨架骨数 = 越界。
9. **抖动骨的轴心硬绑在骨原点**，`BoneMatrices` 只决定网格坐在哪，动不了旋转中心。
10. **`support_mg` 离体轴最远（0.301 m）且是瞄准链的直接下游**，拿它做胸部是最差选择之一；`support` 在正中线，是六根里唯一"像胸的轴心"的。
11. **`generated_*.dl_bin` 里没有物理配置**（关键词普查 0 命中），别指望从注册层拿到物理。
12. **运行时不能加骨**：上游 `SceneGraphComponent` 26 个方法里没有"创建节点"。
13. **PowerShell 单引号字符串里的 `` `n `` 不是转义**，用它生成 C# 代码会把字面反引号写进文件（本会话踩了两次）。

---

## 8. 未解之谜（诚实边界，别当事实用）

| # | 未知 | 为什么重要 | 怎么验 |
|---|---|---|---|
| ~~U1~~ | ~~`0x44`/`0x48` 的含义~~ | **已解决（见 §13）**：`0x44` 是指向**矩阵数组内部**的绝对偏移，插节点必须 +64；`0x48` 不在槽边界上，含义仍未明，但**不动它是对的** | ✅ 2026-10-31 实测 |
| U2 | 抖动骨求解器对"没有动画轨的骨"能否工作 | 决定 Lua 加骨后是否还要喂一条假动画 | Lua 加骨后直接写姿态，看是否正常 |
| U3 | `Unit.update_jiggle_bone_data` 的参数签名 | 若能直接驱动，Lua 侧可以更薄 | 游戏内 `pcall` 试调，错误信息暴露参数个数 |
| U4 | `AnimationBlenderBoneMode` / `RootMode` 的枚举值 | 关系到 `bone_mode=ignore` 的可用性 | HD2 里这两个表是暴露的，可现场遍历 |
| U5 | 动画 `+0x04` 之外的骨数约束（是否有第二处） | 决定"加骨"是否真的完全无解 | 把某一份动画的 `+0x04` 改成 91，配 91 骨骨架，看是否崩 |

> U5 值得单独试一次：如果**只改一份动画**就能让 91 骨骨架在某个场景不崩，那"改 3522 份"就从"不可能"变成"批处理"。

---

## 9. 环境与路径

| 项 | 路径 |
|---|---|
| 仓库 | `E:\Data\source\repos\WpfApp1` |
| 测试库 | `E:\mod`（25+ 个 mod，含 P3-1/P3-2/P3-2b/P3-2c/P3-3 测试件） |
| 管理器 | `HD2ModManager\bin\Debug\net10.0-windows` |
| CLI | `HD2ModCli\bin\Debug\net10.0\hd2mod.exe` |
| 游戏数据 | `D:\SteamLibrary\steamapps\common\Helldivers 2\data` |
| 7z | `HD2ModManager\bin\Debug\net10.0-windows\third_party\7zip\7z.exe` |
| bonehash | `HD2ModManager\Resources\AssetMetadata\bonehash.txt`（4913 条，**十进制**哈希在前） |
| 测试包现场 | `tmp\milltina-test\`（含 `说明.md` / `说明-L1.md` / `说明-P3-2-P3-3.md`） |
| 解封后的注册表 | `tmp\dlbin-plain\`（55 个 `.plain`） |

**当前游戏目录状态**：已恢复成正常 Mod（`18235e0c9ec0e636.patch_0` 已移除），玩家骨架是原版。

---

## 10. 给未来的我的三句话

1. **纯 patch 的天花板是"6 根骨 + 可调参数"，这一档已经打通（P3-1），不要再试图加骨。**
2. **加骨必须 Lua，而且只需要"离线插节点 + 运行时写姿态"两件事**，别去碰 `bones` 资源、动画、状态机。
3. **所有格式技艺在 §2.2，所有坑在 §7。** 动手前先把这两节读一遍，能省掉一整轮试错。

---

## 11. Lua 侧实测（2026-10-31，全部在真实游戏进程内跑出来）

> 工具在 `tmp/lua-boot/`（`probe.lua` + `build_probe.py` + `remove_probe.ps1`）。
> 本节所有数字都是实测，不是推断。**先读 §11.6，那条否定结论会改变你要做的事。**

### 11.1 结论速查

| 问题 | 答案 | 依据 |
|---|---|---|
| 能不能用 Lua 接管游戏启动脚本 | ✅ **可以** | 覆盖 boot 资源 + 重放原版 501 字节字节码，游戏一切正常 |
| 能不能在 Lua 里找到玩家 avatar / 护甲 unit | ✅ **可以** | 指纹法，§11.4 |
| 能不能改护甲 unit **已有骨**的姿态 | ❌ **无效** | §11.6，本轮最重要的否定结论 |
| `Unit.set_local_pose` 本身有效吗 | ✅ 有效（回读与我们写的一致） | §11.6 |
| 能不能在运行时新建场景图节点 | ❌ | `SceneGraphComponent` 19 个方法里没有 create/add |
| Lua 每帧开销 | 可忽略 | 单骨单帧 2 次调用，实测无帧率变化 |

### 11.2 投放通道（已跑通，可复现）

- `data/settings.ini` 里 `boot_script = "boot"` ⇒ 引擎**只执行一个** Lua 资源。
- boot 资源 = `lua` 类型 `0xa14e8dfa2cd117e2` / FileId `0xf476df93691895fa`，归档 `9ba626afa44a3aa3`。
- **原版 boot 只有 509 字节** = 8 字节前缀（`u32 长度 + u32 版本=1`）+ **501 字节 Lua 5.1 字节码**。
  它只定义 `init / shutdown / update / render` 四个全局，引用 `stingray / Application / build / release / require / debug / rawget / _G / jit / off`。
  ⇒ 覆盖 boot 极安全：**先原样重放这 501 字节，再跑我们的代码**，游戏原行为一点不少。
- 复现：
  ```powershell
  # 1) 抽原版 boot
  hd2mod gamedata extract asset 0xa14e8dfa2cd117e2 0xf476df93691895fa `
      --game-data "<游戏 data>" --output tmp\lua-boot\vanilla --overwrite --json
  # 2) 合成 + 打包 + 落盘（默认序号 11）
  python tmp\lua-boot\build_probe.py --patch-index 11
  # 3) 把 out\ 下三个文件拷进 <游戏 data>\，重启游戏
  # 4) 收工
  pwsh tmp\lua-boot\remove_probe.ps1
  ```
- **必须用三位十进制转义 `\ddd`。** 仓库里的 `LuaPatchComposer.EscapeLua` 用的是 `"\" + value`（不补零），
  而 Lua 5.1 的 `\ddd` 最多吃三位 —— 字节码里 `\1` 后面紧跟 ASCII 数字会被读成 `\12`。
  `build_probe.py` 已改成补零版本。**这是 `LuaPatchComposer` 的一个真实隐患，值得单独修。**
- 补丁序号必须是**最大**的（已有 `patch_0..10` ⇒ 用 11），因为同名资源引擎只执行序号最大的那份。
- 归档结构：104 字节头 + 80 字节条目 + `u32 载荷长度 + u32 版本(2) + UTF-8 源码`；同名空 `.stream` / `.gpu_resources` 一起放。

### 11.3 运行时控制通道（本轮最大的效率提升）

```
%LOCALAPPDATA%\hd2pbx_cmd.txt      每 60 帧读一次，内容变化才生效
  mode = off | write | translate | hide | show | probe | probe3 | verify | hold
  bone = 骨名（走 Application.string_to_id32 + Unit.node）
  axis = x | y | z        angle = 度        offset = 米
  unit = unit id 十六进制（十六进制匹配）   unitindex = World.units 的下标
  listarmor = 1            列出护甲候选
  target = avatar | armor  probe3 的目标
```

**改参数不用重启游戏**（一轮重启 ≈ 90 秒，本轮重启了 10 次）。日志写到 `%LOCALAPPDATA%\hd2pbx_probe.txt`，每条 `flush()`。

### 11.4 认人：在 2290 个 unit 里找到玩家和护甲

- **资源路径哈希 == 资产 FileId**（已核对：`stingray_hash("content/fac_helldivers/cha_avatar/avatar_helldiver")`
  正是 `0x4d1c334d294dfa97`）。`Application.string_to_id64(路径)` 也返回同一个值。
- 但 **`World.unit_by_id(world, id)` 实测返回 nil**，这条路不通；`Unit.id` 对大量 unit 返回 nil。
- **可靠指纹（实测有效）**：
  - **玩家 avatar** = `has_animation_state_machine(unit) == true` **且** `has_node(unit, string_to_id32("support_mg")) == true`
  - **护甲件** = `has_node(support_mg)` **且没有**动画状态机 **且** 节点数 90~120
- 实测：关卡 world 有 **2290** 个 unit；avatar 在 `unit[2108]/[2140]/[2173]`（每局不同），
  `nodes=107`、`Unit.bones` 返回 **90** 个名字（含 `support_mg/support/chest/attach_intelpad`）；
  护甲候选 **13** 个（`nodes=112/113`），`dbg=#ID[4a52e2b0edd45dd4]`。
- **`Unit.num_children` / `Unit.child` 不存在**；要用 `World.units(world)` 自己遍历。
- **一个 world 的 unit 数每帧都在变**（实测 2287→2310），所以普查必须**缓存数组本身**跨帧遍历，
  不能"实时数组 + 下标"，否则永远从头开始（踩过，连续 6 次 `census start`）。

### 11.5 实测到的 Lua API（一手，只记与物理相关的）

- **Unit 117 个函数**。有：`local_pose / world_pose / set_local_pose / local_rotation / set_local_rotation /
  local_position / set_local_position / world_position / world_rotation`、`node / has_node /
  num_scene_graph_items / scene_graph_parent`、`bones`、`debug_name / resource_name / id / name_hash`、
  `num_meshes / mesh`、`has_animation_state_machine / enable_animation_state_machine /
  disable_animation_state_machine / set_animation_bone_mode / set_animation_root_mode / set_bones_lod`、
  `update_jiggle_bone_data / update_jiggle_bone_orientation`、
  `debug_draw_jiggle_bone / debug_draw_all_jiggle_bones / debug_draw_all_constraint_bones`、
  `set_unit_visibility / set_mesh_visibility`、`enable_physics / disable_physics`、`project_skinned_decal`。
  **没有**：`num_children / child / link / unlink / set_parent / set_world_pose / node_name / create_actor`。
- **World 70 个**：`units / unit_by_id / unit_by_name / units_by_resource / entities / update_unit /
  update_animations / update_animations_with_callback / update_scene / spawn_unit / destroy_unit /
  link_unit / unlink_unit / num_units / debug_camera_pose`。
- **SceneGraphComponent 19 个**：`local_pose / world_pose / local_rotation / set_local_pose /
  set_local_rotation / local_position / set_local_position / node_index / num_nodes / parent /
  instances / create / destroy` —— **仍然没有任何"新建节点"**（与 §2.5 一致，这次是实测确认）。
- **EntityManager 19 个**：`unit_component / scene_graph_component / animation_blender_component /
  animation_state_machine_component / mesh_component / transform_component / …`。
- **UnitComponent 6 个**：`create / destroy / get_property / set_property / instances / unit`；
  **`instances` 实测返回 nil** ⇒ "entity → SceneGraphComponent"这条路暂时走不通。
- **`IdString32` 只有 `equal / from_hex / to_hex / to_string`**，**没有字符串构造** ⇒
  骨名转哈希必须走 `Application.string_to_id32`。

### 11.6 核心否定结论：护甲 unit 的**同名骨**写不进去

实测对象：护甲 unit `dbg=#ID[4a52e2b0edd45dd4]`，113 节点，`chest` = node 9，`head` = node 101。

| 实验 | 结果 |
|---|---|
| `set_local_pose(chest, pose·R)` 后**同一帧**回读 `local_pose(chest)` | **等于我们写进去的值**（`readback==wanted:true`） |
| 同上，同一帧读子节点世界坐标 | **完全没变**（`moved=0.0000`） |
| 每帧持续转 90°，x / y / z 三轴各试一遍 | `head − chest` 偏移恒定 ≈ (0.02, 0.02, 0.345)，**完全不跟随** |
| 一次性 `chest.local_position.z += 1.5`，之后每 30 帧回读 | **稳定保持 +1.5000 达 450 帧**，没有任何东西覆盖它 |
| 同上状态截图 | **人物完全正常**，看不出任何位移 |

⇒ **写进护甲 unit 的 local pose 会被保存下来，但对"avatar 骨架里同名"的骨不参与渲染。**
⇒ 那类骨的渲染变换是**按骨名从 avatar 重定向**过来的，与它自己的 local pose 无关。

**这条结论解释了社区为什么用 C++ addon 而不是纯 Lua 去驱动已有骨。**
它也把"纯 Lua 能做什么"压缩成一件事：**只能驱动 avatar 骨架里没有对应名字的节点。**
——这一条在 **§12 被正面证实**。

### 11.7 崩溃边界（拿游戏换来的，务必遵守）

| 调用 | 批量范围 | 结论 |
|---|---|---|
| `num_scene_graph_items` | 2290 个 unit 全跑过 | ✅ 安全 |
| `Unit.id` | 2290 个 unit 全跑过（大量返回 nil） | ✅ 安全 |
| `has_node` / `has_animation_state_machine` | 228+ 个 unit | ✅ 安全 |
| `debug_name` | 少量 | ✅ 安全（返回 `#ID[hex]` 字符串） |
| **`Unit.bones`** | 遍历到第 ~42 个就 **AV 崩溃**（`0xC0000005`，两次复现） | ❌ **禁止批量调**，只对已确认的 avatar / 目标件调 |
| **`Unit.mesh(unit, 0)`** | 崩溃（对没有 mesh 的 unit 越界） | ❌ 禁止 |
| **`Unit.resource_name`** | 崩溃 | ❌ 禁止（改用 `debug_name`） |

崩溃后游戏直接退出，`%APPDATA%\Arrowhead\Helldivers2\dumps` 留 `.dmp`。
**代价：每次崩溃 = 一次重启 + 一次重进舰船。**

### 11.8 对路线的影响（下一步该做什么）

- §1.3 那条路**仍然成立，而且现在有了直接证据支持**：
  唯一还能被 Lua 驱动的，是**一根 avatar 骨架里没有的骨**（`jig_breast`）——
  它没有重定向来源，世界变换只能由"父骨世界变换 × 我们写的 local pose"得到。
- 这正好是 **P3-3 已经做出来的包**（`18235e0c9ec0e636.patch_0` + `9ba626afa44a3aa3.patch_0`）。
- **下一步（M-E′）**：拿 P3-3 的护甲包 + 本轮的探针，用 `mode=hold` / `mode=write` 打在 `jig_breast` 上，
  看它会不会动。这同时回答 §8 的 **U2**（"抖动骨求解器对没有动画轨的骨能否工作"的前置问题）。
  - 动了 ⇒ Lua 路线成立，接着做弹簧（M-D）。
  - 不动 ⇒ 护甲 unit 的骨骼变换整体来自 avatar 重定向，纯 Lua 在这条线上彻底出局，
    必须回到原生状态机（§5.1 L1）或原生插件。
- 探针已经内置 `bone=jig_breast` 支持：只要 P3-3 的护甲包在，改一行 `hd2pbx_cmd.txt` 就能测，**不用改 Lua**。

### 11.9 本轮踩到的新坑（追加到 §7）

14. **`Unit.bones` / `Unit.mesh` / `Unit.resource_name` 批量调会 AV 崩溃**，不是抛 Lua 错误，`pcall` 拦不住。
15. **`set_local_pose` 成功 ≠ 生效**。必须用"回读 + 子节点世界坐标 + 截图"三件套交叉验证。
16. **按实时数组下标遍历会失效**：world 的 unit 数每帧变，要缓存数组。
17. **`Unit.num_children` / `Unit.child` 不存在**，别照着上游 Stingray 文档写。
18. **`IdString32` 不能从字符串构造**，只能 `Application.string_to_id32`。
19. **PowerShell 的 `Get-Content -Raw` + `Set-Content` 会毁 UTF-8 文件**（本机 AGENTS.md 已警告）；
   改源码一律用 `edit` / `sd`。

---

## 12. M-E′：新骨**确实**能被 Lua 驱动（2026-10-31 收尾，本轮最重要的正面结论）

### 12.1 做法

- `9ba626afa44a3aa3.patch_11` = P3-3 的**护甲侧**包（`jig_breast` + 6401 个胸部顶点重指向），
  `9ba626afa44a3aa3.patch_12` = Lua 探针。同归档不同序号，互不覆盖。
  **不需要 avatar 侧的包**（那个才会崩，见 §3.4）。
- 部署后探针认出 5 个护甲候选，其中 **2 个带 `jig_breast`**：
  `#ID[a0cc3d2318d9e237]` / `#ID[ecc8df6ccc7721bd]`。
  ⚠️ **其中 `a0cc3d2318d9e237` 是头盔件，不是躯干件**（详见 §12.5 V1）。
  探针当时按"节点数最多"选中了它，所以下面的观察全部发生在**头盔件**上。
- `Unit.node(unit, string_to_id32("jig_breast"))` → **node 113**，即该 unit 的最后一个节点（符合"追加在末尾"）。
- ⚠️ **`Unit.node` 返回 1 基下标**：`num_scene_graph_items=113` 时合法下标是 1..113。
  （`chest→9`、`head→101`、`jig_breast→113` 全部符合这个约定。）

### 12.2 结果（**已按用户的目视时间线逐条校正**）

用户报告的完整时间线，与探针实际收到的命令逐条对应：

| 用户看到的现象 | 当时我执行的 | 结论 |
|---|---|---|
| ① 一进游戏，**胸部乳房区域就是被拉伸的坏模型**，头盔正常 | cmd = `mode=off`，探针**一个字节都没写** | **这是 P3-3 护甲包自身的缺陷**，与 Lua 无关。见 §12.5 |
| ② 等了一会儿，**发簪下面的吊坠**突然朝人物面朝方向被拉伸 | `mode=hold`（+1.5，一次性）→ 紧接 `mode=translate offset=0.2`（每帧累加，2 秒 ≈ 48 m） | **写入真的进了渲染**：吊坠顶点带 `jig_breast` 权重，被拉走 |
| ③ 几秒后我又跑了一次指令，**整个头部消失** | `mode=hide` → `set_unit_visibility(unit, false)` | **不是姿态写入造成的**，是我把那个 unit 整个隐藏了 |
| ④ 重开游戏后，**吊坠晃了几秒就不晃了** | `mode=write angle=20 axis=x` 跑了 2 秒，然后 `mode=off` | **最干净的一次证据**：开 → 晃，关 → 停。全程没有镜头变化干扰 |

**⇒ 新骨（avatar 骨架里没有的骨）可以被 Lua 完全驱动。这条路成立。**
这是整个方案的转折点：之前所有"Lua 写不动骨骼"的迹象，全部只针对**同名骨**。

> ⚠️ **本节的第一版归因是错的**（把"头被甩飞"算在 48 m 位移头上）。
> 实际上 48 m 位移只拉走了**吊坠**，头消失是下一条指令 `set_unit_visibility(false)` 干的。
> 两件事发生在相邻的两条命令里，我混成了一件。**这条教训本身也值得记：一次只改一个变量，并记下每条命令的时间戳。**

### 12.3 必须记住的两个副作用

**（a）残留权重会拉出细丝。** 被 48 m 位移拉走的是**发簪吊坠**，不是胸部。原因就是 §3.1 记过的
"权重转移的渣"——那些顶点上残留着极小的 `jig_breast` 权重，位移 48 m 时哪怕 0.5% 权重也会被甩飞。

- ⇒ **弹簧必须限幅**（建议 ≤ 5 cm/帧、总位移 ≤ 0.3 m），否则会拉出细丝。
- ⇒ 也顺手得到一条**很好用的判据**：**能看见远处零件被拉丝，就说明这个骨真的接上渲染了。**
  比截图对比可靠得多（舰船里的镜头一直在漂，像素比对全是噪声）。

**（b）停写 ≠ 复位。** `hold` 测试里 +1.5 稳定保持了 450 帧，没有任何东西覆盖它。
也就是说**最后写入的姿态会一直留着**。
⇒ 弹簧必须有**显式复位**：关掉功能时要主动写回 bind pose，否则角色会歪着停在那儿。

### 12.5 三个待验问题的结论（V1 / V2 已定，V3 已收窄）

#### V1 ✅ 已解决：探针抓错了件——`a0cc3d2318d9e237` 是**头盔**

直接查管理器侧的权威表 `generated_customization_armor_sets`（解封后
`tmp/dlbin-plain/generated_customization_armor_sets.dl_bin.plain`，422 条 kit），
解析器 `tmp/armor-lookup.py`（格式照抄 `HD2GameItemAssets/CustomizationArmorSetsReader.cs`）：

```
kit(64B):  id@0 dlcId@4 setId@8 nameUpper@12 nameCased@16 desc@20
           rarity@24 passive@28 archiveHash@32(u64) kitType@40 bodyOffset@48(i64) bodyCount@56(i64)
body(24B): bodyType@0 pieceOffset@8(i64) pieceCount@16(i64)
piece(112B): unitId@0(u64) a@8 b@12 c@16 … tail@88
kitType: Armor=0 / Helmet=1 / Cape=2      （HD2ModCore/Domain/GameDataAuthorityFacts.cs）
```

| Unit | 属于哪个 kit | kitType | bodyType | 部位 |
|---|---|---|---|---|
| `0xa0cc3d2318d9e237` | `0x47bcec20`（**独立 1 件 kit**，setId 与主 kit 相同） | **1 = Helmet** | 3 | **头盔**（这个 mod 把动漫头+头发+发簪塞进了头盔槽） |
| `0xecc8df6ccc7721bd` | `0x4dd749c6`（主 kit，21 件） | 0 = Armor | 0 | **Slim 躯干** |
| `0x4ebbfde8692577ff` | `0x4dd749c6` | 0 = Armor | 1 | **Stocky 躯干** |
| `0x4a52e2b0edd45dd4` | `0x4dd749c6` | 0 = Armor | 0 | 躯干件（piece 2） |
| `0x7245b46261345371` / `0x79383d82e3bf6b03` | `0x4dd749c6` | 0 = Armor | 0 | 躯干件（piece 3 / 5） |

⇒ **这解释了全部现象**：隐藏头盔件 → 头没了；写 `jig_breast` → 发簪吊坠动（因为吊坠在头盔件里）。
⇒ **探针的选择启发式错了**：它挑"节点数最多且带 jig_breast 的候选"，而头盔件和躯干件都是 113 节点。
⇒ **正确目标应该是 `ecc8df6ccc7721bd`（Slim 躯干）**，那才是 6401 个胸部顶点所在。
⇒ 运行时没法按 `Unit.id` 选（它对这些 unit 返回 nil），但 `debug_name` 返回 `#ID[hex]`，
  **所以命令通道要加一个按 `debug_name` 匹配的选择器**（`unitdbg=<hex>`），这是下一步必做的小改动。

#### V2 ✅ 已解决：吊坠上的权重是**纯残留**

`m2probe weights` 对 P3-3 护甲包逐 unit 统计 `0xdd5eb5fe`（`jig_breast`）：

| Unit | hits | wsum | 质心 | 权重分布 |
|---|---:|---:|---|---|
| `0xecc8df6ccc7721bd`（Slim 躯干） | 6401 | 3252.3 | (0.028, **0.209**, 0.060) | ≤0.25:3117 / ≤0.5:184 / ≤0.75:260 / **>0.75:2840** |
| `0x4ebbfde8692577ff`（Stocky 躯干） | 6401 | 3252.3 | 同上 | 同上 |
| `0xa0cc3d2318d9e237`（头盔） | **6751** | **1056.3** | (0.062, **−0.215**, 0.339) | **6751 个全在 ≤0.25 档**，一个强权重都没有 |

⇒ 躯干件是**真权重**（2840 个顶点 >0.75，质心在身体前方 +Y），重指向是对的。
⇒ 头盔件是**纯残留**（平均权重 0.156，质心在身体后方 −Y、头部高度），跟 §3.1 记的"权重转移的渣"完全一致。
⇒ **弹簧必须限幅**，并且最好先做一次权重清洗（把 ≤0.25 的残留权重清掉）。

#### V3 🔶 已收窄：调色板不变式是**对的**，问题不在矩阵

`m2probe matrel` 对 P3-3 与原始本体包逐槽校验 `BoneMatrices[i] · world[RealIndices[i]] == X`：

| 包 | 结果 |
|---|---|
| P3-3 | 全部 unit **✅ 逐槽一致**，全局最大偏差 **0.000002** |
| 原始本体（对照） | 同上，偏差一致 |

⇒ **重算矩阵这一步没有错**（`BoneMatrices = X · inverse(world)` 仍然成立）。
⇒ 那么 bind pose 下"乳房被拉伸"只能来自**运行时**，不是资产里算错了。两个候选：
1. **§8 的 U1**：护甲 Unit 头部 `0x44`/`0x48`（9984 / 12384）指向 TransformInfo 的矩阵区，
   插节点后它们**陈旧了**。若引擎用它们取矩阵，就会读到错位的数据。
2. **运行时 `jig_breast` 的世界变换**不是写死的那份：非重定向骨在世界里可能取到单位根/单位矩阵，
   于是 `BoneMatrices[jig] · world_runtime` ≠ X，网格被整体搬走一个 `inverse(world_authored)`（≈1.38 m）。
   ——注意这条与我们**实测到的"写入有效"并不矛盾**：写入有效说明运行时世界变换确实来自场景图节点，
   但 bind 时的初值可能与资产里写死的值不一致。

**下一步定位 V3 的最小实验**：做一个"只插节点 + 重指向、不改别的"的最小包，
先只改**一个**躯干 unit（`ecc8df6ccc7721bd`），进游戏看 bind pose 是否正常。
- 正常 ⇒ P3-3 里 `addpivot` 那一步有问题（或 21 个 unit 一起改引入了别的错）。
- 仍然拉伸 ⇒ U1 成立，得先把 `0x44`/`0x48` 的含义解开（§8 U1）。

#### U1 离线验算（2026-10-31）：`0x44` 确实是**矩阵指针**，`0x48` 不是

拿头盔 unit（`a0cc3d2318d9e237`）原始 payload 算四个数组的真实区间（`ti=0x130, n=112`）：

```
locals   [0x140, 0x1D40)
matrices [0x1D40, 0x3940)
entries  [0x3940, 0x3B00)
hashes   [0x3B00, 0x3CC0)
```

| 头部字段 | 原值 | 落点 | 判定 |
|---|---|---|---|
| `0x44` | 9984 = `0x2700` | matrices 内，**正好 slot 39.000**，读出的 16 个 float 是合法 4×4（末列 `0,0,0,1`） | ✅ **绝对指针，指向某个矩阵** |
| `0x48` | 12384 = `0x3060` | matrices 内，**slot 76.5**（不对齐），读出的 float 不是合法矩阵 | ❌ 不是矩阵指针，含义仍未知 |
| `0x58` | 18256 | 块之后 | 插入节点时已正确 +136（→18392）✅ |
| `0x5c`/`0x60`/`0x64`/`0x70` | 非零 | 块之后 | 同上，已 +136 ✅ |

**关键推论**：插入一个节点后，`locals` 长 64 字节 ⇒ `matrices` 起点从 `0x1D40` 移到 `0x1D80`（**+64**）。
P3-3 里 `0x44` **没有跟着 +64**，所以它现在指向 slot **38** 而不是 39 —— 差一个槽。

**但这条大概率不是乳房拉伸的原因**：P3-3 把 22 个 unit 全都插了节点（含头盔），
而**头盔在游戏里显示是正常的**。⇒ **"插节点 + `0x44` 陈旧"本身不会破坏渲染**。
⇒ 躯干件的拉伸更可能来自**重指向**（`jig_breast` 那一槽的运行时世界变换），而不是插入。

### 12.6 最小实验方案（按用户建议：用原版包 + 只改头盔）

**理由**（用户提出，采纳）：
1. P3-3 是"本体 patch → patchaddnode → addpivot → 重指向"层层叠出来的，**任何一步的错都会留在里面**，
   无法归因。必须从**原版** `本体 patch_0` 重新开始。
2. **头盔是独立单件 kit**（`0x47bcec20` 只有 1 个 piece），改它不牵扯那 21 件躯干 kit，
   出错范围最小、最容易判读。
3. 头盔件里已经有 `jig_breast` 的**纯残留权重**（6751 个顶点、全部 ≤0.25），
   足以产生**肉眼可见**的位移（§12.2 已经看到吊坠动），不需要先做权重转移。

**两阶段，每阶段只改一个变量：**

| 阶段 | 内容 | 判读 |
|---|---|---|
| **A** | 只往头盔 unit 插 `jig_breast` 节点，**不动任何权重、不动任何矩阵** | 头盔外观与原版逐像素一致 ⇒ 插入是安全的（`0x44` 陈旧无害）；有任何形变 ⇒ U1 成立，先修 `0x44`（+64） |
| **B** | A + 把头盔的残留权重重指向 `jig_breast` 并重算 `BoneMatrices` | bind pose 仍正常 ⇒ 重指向正确；然后 Lua 驱动，看吊坠是否跟随 |

**A 阶段必须先过**。它只需要"插节点"一个操作，是最干净的 U1 判据。

### 12.7 Stage A / B 实测结果（2026-10-31，**两个都通过**）

工具改动：`m2probe patchaddnode` 新增 `--only <fileIdHex>`，可只改指定 Unit。

#### Stage A：只插节点，不动权重/矩阵

```
patchaddnode <原版本体 patch> <out> jig_breast chest 0.0 0.197 1.378 support_mg --only a0cc3d2318d9e237
  → 0xa0cc3d2318d9e237 插入 jig_breast，节点[112]，父=chest[19]，其余 21 件逐字节不变
  → 头部字段：0x44 仍 9984（**未 +64，故意不改**）、0x58 18256→18392（+136，正确）
```

进游戏实测：**头盔/头发/发簪/吊坠全部正常，无任何形变**；乳房也正常（因为没动权重）。

⇒ **结论 1：往护甲 Unit 插节点是安全的。**
⇒ **结论 2：`0x44` 陈旧一格（slot 39→38）不影响渲染 ⇒ U1 不是渲染杀手。**
⇒ 所以 P3-3 的乳房拉伸**不可能来自插入这一步**。

#### Stage B：A + 重指向 + 重算矩阵（只改头盔一件）

```
addpivot <stage-a patch> <out> jig_breast chest support_mg <bonehash.txt> --at 0.0 0.197 1.378
  → 只有 0xa0cc3d2318d9e237 命中：枢轴 jig_breast[112]，父→chest[19]，**改写 4 个槽**
  → 其余 20 件全部"跳过（pivot=-1）"，确认只动了一件
  → weights：头盔的 support_mg 权重全部转到 dd5eb5fe（6751 个，wsum 1056.3，全部 ≤0.25）
  → matrel：全部 unit ✅ 逐槽一致（偏差 ≤2e-6）
```

进游戏实测：**头部/头发/吊坠位置完全正常，没有位移**。
然后 `mode=write angle=25 axis=x` 驱动 → `set_local_pose node=113 ok`，头部外观仍然正常。

⇒ **结论 3：重指向 + `BoneMatrices = X · inverse(world)` 重算是对的。**
⇒ **结论 4：P3-3 的乳房拉伸既不是插入、也不是重指向的通用错误**，
   而是**躯干件特有**的东西（强权重 2840 个 >0.75 / 21 件一起改 / 或那 7 件共享几何的变体互相干扰）。

**下一步**：把完全相同的 A→B 流程套到**单个躯干件** `ecc8df6ccc7721bd`（Slim 躯干，6401 个强权重顶点），
用 `--only ecc8df6ccc7721bd` 只改这一件。这样能把"躯干特有"的差异孤立出来。

#### Stage T（躯干单件）：**也通过** —— P3-3 的病根是"批量改 + 搬节点"

```
patchaddnode <原版本体 patch> ... --only ecc8df6ccc7721bd     → 只插节点，节点[112]，父=chest[19]
addpivot ... jig_breast chest support_mg --at 0.0 0.197 1.378 → 只命中这一件，改写 4 槽
weights: dd5eb5fe hits=6401 wsum=3252.3，>0.75:2840（与 P3-3 里躯干件的数字**完全相同**）
matrel : 0xecc8df6ccc7721bd 全局最大偏差 = 0 ✅
```

进游戏实测（探针 `withJig=1`，目标 = `ecc8df6ccc7721bd`）：

- **bind pose 正常，乳房没有被拉伸** —— 这与 P3-3 里"同一件、同样数字"却是坏的形成鲜明对照
- `mode=write angle=25 axis=x` 驱动 → `set_local_pose node=113 ok`，外形保持正常

⇒ **结论 5：躯干件的强权重（2840 个 >0.75）本身没问题，重指向和矩阵重算也没问题。**
⇒ **P3-3 的病根只剩两个候选**：
  1. **21 件一起改**（`patchaddnode` 无 `--only`，命中了所有用到 `support_mg` 的 unit；
     其中 7 件是 Slim/Stocky 变体，可能共享几何，批量改互相干扰）
  2. **`addpivot` 把节点从插入位置搬到别处**（本次 `--at` 恰好等于插入位置，等于没搬）

⇒ **做法修正**：以后一律 **`--only` 单件 + `--at` 与插入位置一致**，不要批量改。
   这条应该进 §7 的坑清单。

### 12.4 现在的完整结论

| 骨的类型 | Lua 能不能驱动 | 原因 |
|---|---|---|
| avatar 骨架里**有**同名骨（`chest` / `support_mg` / …） | ❌ | 每帧按骨名从 avatar 重定向，写进去的 local pose 被覆盖 |
| avatar 骨架里**没有**的骨（`jig_breast`） | ✅ | 没有重定向来源，世界变换 = 父骨世界变换 × 我们写的 local pose |

⇒ **§1.3 的方案成立，Lua 路线可以继续推进到弹簧（M-D）。**
下一步要做的就三件事：
1. 每帧读父骨（`chest`）的**世界变换**，取它的平移二阶差分，**丢掉旋转分量**（§5.2 那条"只转镜头不晃"）。
2. 用二阶弹簧积分算出 `jig_breast` 的局部旋转/位移，**限幅**后 `set_local_pose`。
3. 参数从 `HD2PBX01` 容器读，不写死在 Lua 里。

---

## 13. 2026-10-31 下半场：**加骨 + 驱动全线打通**（本文最重要的一节）

> 上半场（§11/§12）只证明了"能写进去、吊坠会动"，但**bind pose 一直是坏的**。
> 这一节把"为什么坏"彻底查清并修好，**最小可用链路已经跑通**。

### 13.1 唯一的根因：`0x44` 必须跟着矩阵区走 **+64**，不是 +136

```
Unit 头部 0x44 = 一个**指向矩阵数组内部**的绝对偏移（实测正好落在某个 64 字节槽的起点）
插入一个节点 ⇒ locals 数组 +64 ⇒ **矩阵区整体后移 64 字节**
⇒ 0x44 必须 +64，才能继续指向同一个槽
```

**这一个字节级错误，就是 P3-2 / P3-3 / 本轮所有"乳房被拉伸/内凹/变成大包"的唯一原因。**

判据链（每一步都只改一个变量，进游戏实拍）：

| 变体 | 改动 | 结果 |
|---|---|---|
| Stage A | 只插节点，不动权重/矩阵 | ✅ 正常（**但无顶点用新节点，所以不能证明任何事**） |
| Stage A + 0x44 未改 + 重指向 | P3-3 的做法 | ❌ 乳房被拉成巨大一坨 |
| 重指向到**已存在**的 `spine1`（不移动它） | 只为验证"重指向+重算矩阵"本身 | ✅ **完全正常** ⇒ 流程本身没问题 |
| 插节点 + 重指向 + **0x44 +64** | 本轮修复 | ✅ **完全正常，与原版一致** |
| 同上 + Lua 每帧驱动 ±12° | 最终目标 | ✅ **乳房完整、无拉伸、被驱动抬起** |

⚠️ **`0x48` 不要动**：它落在矩阵区里但**不在槽边界**上（slot 76.5），不是矩阵指针，含义仍未明。
实测把 0x44 修好、0x48 保持原样，一切正常。

### 13.2 顺带修掉的两个工具 bug（都会造成"旋转退化"）

**（a）3×3 旋转的行跨距写错。** `m2-probe` 里有两处（`InsertUnitNodes` 和 `addpivot`）：

```csharp
// 错：把 4×4 矩阵的前 9 个连续 float 当成 3×3
for (var k = 0; k < 9; k++) ... localP[k] ...
// 对：按行跨距 4 取
for (var row = 0; row < 3; row++) for (var col = 0; col < 3; col++)
    ... localP[row * 4 + col] ...
```

后果：单位阵被写成 `1,0,0, 0,0,1, 0,0,0`（**det = 0**，退化），引擎用 local 重算世界矩阵时该骨塌成一个点。
**这是"乳房被拉成丝"的第二层原因，两个 bug 叠加。**
判据：拿 `det(rot)` 检查 `locals[]`，正常必须是 **1.00000**。

**（b）`Matrix4x4.to_elements` 返回 12 个值（3×4），不是 16 个。**
用 `{ S.Matrix4x4.to_elements(m) }` 打包成 table 时只会拿到 12 个；按 16 个解析会错位。

### 13.3 Lua 侧写姿态的三条铁律

1. **必须缓存基准姿态，每帧从基准算。**
   `jig_breast` 这类非重定向骨的 local pose **不会被引擎每帧重置**（§12.3b 实测 +1.5 保持 450 帧）。
   若写"读当前 × delta 再写回"，就会**逐帧累加**，120 fps 下表现为"乳房疯狂旋转"。
2. **只替换旋转、必须保留原来的平移。**

   ```lua
   local basePos = Matrix4x4.translation(pose)
   local baseRot = Matrix4x4.rotation(pose)
   local newQuat = Quaternion.multiply(deltaQuat, Quaternion.from_matrix4x4(baseRot))
   local next = Matrix4x4.from_quaternion_position(newQuat, basePos)
   ```
   若简单做 `pose × delta`，平移分量会被一起转走 —— 实测 `world.y` 从 2.50 掉到 1.14（甩出 1.36 m）。
3. **写完调 `World.update_unit(world, unit)`**，否则这一帧渲染看不到。

### 13.4 现在的完整可用链路（照着做就行）

```powershell
$exe  = 'tmp\m2-probe\bin\Release\net8.0\m2probe.exe'
$hash = 'HD2ModManager\Resources\AssetMetadata\bonehash.txt'
$orig = '<原版本体 patch_0>'          # 一定要从原版重建，不要在 P3-x 上叠加

# 1) 只给目标 Unit 插节点，并且修正 0x44
& $exe patchaddnode $orig out\stageA jig_breast chest 0.0 0.197 1.378 support_mg `
       --only <unitFileId> --fix-0x44

# 2) 重指向 + 重算骨骼矩阵（--at 必须与插入位置一致，否则等于又搬了一次节点）
& $exe addpivot out\stageA\9ba626afa44a3aa3.patch_0 out\9ba626afa44a3aa3.patch_0 `
       jig_breast chest support_mg $hash --at 0.0 0.197 1.378

# 3) 把 stageA 的 .gpu_resources 复制过来，补一个 0 字节 .stream
```

**离线自检三件套（缺一不可）**：

| 检查 | 期望 |
|---|---|
| `locals[新节点]` 的 `det(rot)` | **1.00000**（不是 0） |
| `matrel` | 全部 Unit ✅ 逐槽一致，偏差 ≤2e-6 |
| `0x44` 变化量 | **+64 × 新增节点数** |

**运行时自检**：`Unit.node(unit, Application.string_to_id32('jig_breast'))` 返回**1 基**下标
（113 节点时返回 113，对应资产 0 基的 112）。

### 13.5 各部件归属（查 `generated_customization_armor_sets` 得到，工具 `tmp/armor-lookup.py`）

Milltina 这个 mod 的 `本体` patch 里有 22 个几何 Unit，分属两个 kit：

| Unit | kit | kitType | 部位 |
|---|---|---|---|
| `0xa0cc3d2318d9e237` | `0x47bcec20`（独立单件） | **1 = Helmet** | 头盔（含动漫头/头发/发簪/吊坠） |
| `0xecc8df6ccc7721bd` | `0x4dd749c6` | 0 = Armor | **Slim 躯干**（6401 个 `jig_breast` 真权重） |
| `0x4ebbfde8692577ff` | `0x4dd749c6` | 0 = Armor | Stocky 躯干 |
| `0x4a52e2b0edd45dd4` 等 | `0x4dd749c6` | 0 = Armor | 其它躯干件 |

`kitType` 枚举：`Armor=0 / Helmet=1 / Cape=2`（`HD2ModCore/Domain/GameDataAuthorityFacts.cs`）。
躯干件的 `jig_breast` 权重是**真权重**（2840 个 >0.75），头盔件的是**纯残留**（6751 个全 ≤0.25）。

### 13.6 无人值守测试方法（用户不在时也能跑）

`tools/game-input.py` + `tmp/run-variant.ps1`：

- 进游戏后会出现"无法加入房间"⇒ **SendInput 单击左键**越过（`PostMessage` 不行，Stingray 走 Raw Input）
- 开场动画结束后镜头在玩家背后 ⇒ **按 `S` 键让玩家转身面向镜头**，就能拍到正面
- `run-variant.ps1 -VariantDir <dir> -Tag <name> -ProbeCmd 'mode=...;bone=...'` 一条命令完成
  部署→启动→点击→转身→截图→打印关键日志

### 13.7 下一步（M-D 弹簧，起点已经很高）

链路已通，剩下就是**把正弦测试换成物理**：

1. 每帧读父骨（`chest`）的**世界变换**，取平移二阶差分，**丢掉旋转分量**
   —— 这是引擎侧做不到、必须由我们做的事（§5.2），也是"只转镜头不晃"的关键。
2. 二阶弹簧积分出 `jig_breast` 的**旋转增量**，按 §13.3 的方式合成到 base pose 上。
3. **必须限幅**（建议 ≤5 cm/帧、总位移 ≤0.3 m），否则残留权重会把远处零件拉成丝。
4. 参数从 `HD2PBX01` 容器读，不写死在 Lua 里（§5.1）。
5. 关掉功能时要**显式写回 bind pose** —— 停写 ≠ 复位（§12.3b）。

---

## 14. 新方向：从 L4D2 VPK 提取物理骨参数 → HD2PBX01 → 游戏内兼容层（2026-10-31 起）

### 14.1 为什么要走这条"奇怪的链路"（用户定的战略）

HD2 原生 mod **没有任何物理适配**，作者也做不了；我们自己的物理功能因此**拿不到即时反馈**，
只能等别的作者慢慢更新，迭代极慢。

⇒ 转而在**导入 L4D2 VPK 时**，把原作者**手调过的 `$jigglebone` 参数**提取出来，
随 patch 用 `HD2PBX01` 容器带进游戏，由**兼容层 Lua** 读取并复现 L4D2 观感。
这样"能立刻看到效果"，迭代速度从"等几周"变成"几分钟"。

**验收标准（用户原话的意思）**：不追求碰撞、允许穿模；
但**绝不能出现"头发像钢板 / 袖子像钢板 / 长袍后摆像钢板"**——必须有阻尼、会自然垂落。

### 14.2 首要样本

`D:\SteamLibrary\steamapps\common\Left 4 Dead 2\left4dead2\addons\备份\【SHOP】Lunalice八人包101.7z`
（98 MB，内含 8 个角色 VPK：Bill/Coach/Ellis/Francis/Louis/Nick/Rochelle/Zoey）

**另注**：`E:\tmp\lunalice-shop\` 里**已经解压好了**这批 VPK，测试时直接用那边，不必再解 7z。
`tmp\lunalice\` 里另存了 Zoey / Rochelle 两个。

先聚焦**头部**：这个角色是双马尾，两条长马尾最适合验证"移动时有阻尼、静止时自然垂下"。

### 14.3 L4D2 mdl 解析的硬事实（**踩了一整轮才定下来**）

| 项 | 值 / 规则 |
|---|---|
| 文件头 | `IDST`，`bone_count` @156、`bone_offset` @160（int32） |
| 骨骼结构 `mstudiobone_t` | **216 字节**（L4D2 v53；由 `_probe_char_vpks.py` 佐证） |
| 骨名偏移 `sznameindex` | **相对「该骨结构自身起点」**：`name = boneEntryOffset + sznameindex`。❌ 不是绝对偏移，❌ 也不是相对骨数组起点 |
| 父骨 | `boneEntryOffset + 4`（int32），**0 基**，-1 = 根 |
| 静置位置 / 旋转 | pos @ `+32`（3×f32）、quat @ `+44`（4×f32） |
| 物理类型 | `proctype` @ `+164`；**jigglebone = 5** |
| 物理数据偏移 | `procindex` @ `+168`，数据在 `boneEntryOffset + procindex` |
| jiggle 结构 | **120 字节** = `int flags` + **29 × float** |
| ⚠️ 骨名表只覆盖前一部分骨 | 这个模型 127 骨但名字只到 ~62 号；后面那些骨的 `sznameindex` 落在别的文本块里。**祖先名可解析**，所以用"父链名 + 静置坐标"识别链条即可 |

**jiggle flags 位**：`0x01 flexible / 0x02 rigid / 0x04 yawConstrained / 0x08 pitchConstrained /
0x10 angleConstrained / 0x20 lengthConstrained / 0x40 baseSpring`

**29 个 float 的顺序**（`scan_vpk_jiggle.py` 里的 `JNAMES`，已沿用）：
`length, tipMass, yawStiffness, yawDamping, pitchStiffness, pitchDamping, alongStiffness, alongDamping,
angleLimit, minYaw, maxYaw, yawFriction, yawBounce, minPitch, maxPitch, pitchFriction, pitchBounce,
baseMass, baseStiffness, baseDamping, baseMinLeft, baseMaxLeft, baseLeftFriction,
baseMinUp, baseMaxUp, baseUpFriction, baseMinForward, baseMaxForward, baseForwardFriction`

### 14.4 已跑通的工具（都在 `tmp/physbone-recon/`）

| 脚本 | 作用 |
|---|---|
| `jiggle_compact.py` | VPK 树解析 + mdl 骨/jiggle 解析（**这个文件里的 `read_bones` 是正确实现**，别的脚本可 import 它） |
| `jiggle_report.py` | 按父骨分组输出 jiggle 清单（名字 + 静置坐标 + 参数） |
| `export_jiggle_json.py` | **产物流**：导出完整 JSON（含 params、flags、父子链 `chains[]`） |

已产出：`tmp/physbone-export/lunalice_zoey_teenangst_jiggle.json`

### 14.5 Lunalice / Zoey 角色模型实测数据（`survivor_teenangst_light.mdl`）

- **127 骨，57 根 jiggle 骨，39 条链**（链长分布：2~3）
- 参数高度统一：`len=30, tipMass=200, yawS=50, yawD=7, pitS=50, pitD=7, angleLimit=0.611(≈35°)`
  （少数 `angleLimit=0.209(≈12°)`；脊柱上的两条是 `len=20, mass=0, yawS=50/5, pitS=200/5`）
- flags 绝大多数是 `0x31 = flexible + angleConstrained + lengthConstrained`
- **头部 15 根**，全部挂在 `Bip01_Head1` 下，且**成左右镜像对**（Z 坐标一正一负）。
  其中**最长的两条链（各 3 节）**是：
  ```
  root=72  pos=(+1.4728, +2.2874, −4.4927)   ← 一侧马尾
  root=75  pos=(+1.4728, +2.2874, +4.4927)   ← 另一侧马尾（镜像）
  ```
  ⇒ 这两条就是**双马尾**，是"阻尼/自然垂落"的验证目标。
- 其余成对分布在 `Bip01_Pelvis`（裙摆/后腰）、`Bip01_Spine`、`Bip01_L/R_Calf`、`Bip01_Spine4`。

### 14.6 待解决 / 下一步

1. **骨名补全**：从 mdl 里找到后段骨的真正名字（候选：文件后部的文本块看起来像内嵌 SMD/QC；
   或改从 `.vtx`/`.vvd` 交叉验证）。**不阻塞参数提取**，但影响骨名映射的可读性。
2. **映射到 HD2 骨**：L4D2 用 `ValveBiped.Bip01_*`，HD2 用 90 骨表；
   现有 `HD2SourceMigration` 里已有对应关系表（`hd2_style.py` 的 `Spine→spine1` 等），先复用。
3. **接入 VPK 导入链路**：管理器可直接导入 VPK。需要在导入时同时产出
   `HD2PBX01` 容器（承载 jiggle 参数）+ 声明"哪些新骨要挂在哪个 HD2 骨下"。
4. **兼容层 Lua**：读容器 → 对每条链做二阶弹簧积分 → 写场景图节点姿态。
   **链路已在 §13 打通**，这里只是把"正弦测试"换成"按 L4D2 参数驱动 + 链式传播"。
5. **钢板判据**：先在离线渲染里做出"静止/行走"两帧对比，确认有垂落与滞后，再进游戏。

> ⚠️ 复用 §13 的成果时别忘了三条铁律（缓存基准姿态、只换旋转保留平移、写完 update_unit），
> 以及"停写 ≠ 复位"——关功能必须显式写回 bind pose。


### 14.7 离线弹簧验证（已完成，2026-10-31）

工具：`tmp/physbone-recon/sim_spring.py`
产物：`tmp/physbone-export/spring-vs-rigid.png`（刚性 vs 弹簧，侧视+正视 9 帧）
　　　`tmp/physbone-export/spring-timeline.png`（时程曲线，**最有力的证据**）

**做法**：把双马尾两条链（root=72 / root=75，各 3 节）按作者的 L4D2 参数
（`k=50, c=7, tipMass=200, angleLimit=35°`）跑阻尼受迫振子，
头部给真实走路起伏（2.5 cm @1.9 Hz），走 2 秒后平滑收尾 0.4 秒再静止。

**结果（数值）**

| 节 | 峰值偏离 | 说明 |
|---|---|---|
| 第 1 节 | **±17.3°** | 挂在 Head1 下，直接受头部激励 |
| 第 2 节 | ±10.3° | 相位滞后于第 1 节 |
| 第 3 节 | ±7.7° | 滞后最多 ⇒ **鞭梢效应** |
| 末梢相对头部的最大位移差 | **5.6 cm** | 一条约 40 cm 的马尾，滞后 5.6 cm |

**结论（验收判据）**

- 刚性跟随（θ≡0）在图上是一条 **零直线** —— 那正是"钢板"的行为。
- 用作者参数跑出来的是**逐节相位滞后 + 幅值递减 + 运动停止后自行衰减**。
  ⇒ **不是钢板，阻尼和自然垂落都在。** ✅

**必须记住的标定边界（诚实说明）**

- L4D2 的 jiggle 求解器闭源，那 29 个参数是**游戏调校增益**，不是 SI 单位，
  所以**绝对时间尺度无法从参数推导**。
- 模型里唯一的自由常数是"末端质量归一化基准"。它与 ωn、ζ 的关系：
  `m = tipMass / base`，`ωn = √(k/m)`，`ζ = c / (2√(k·m))`。
  取 `base = 300` 得 `ωn = 8.66 rad/s、ζ = 0.61`，此时**不撞 35° 限位**、观感合理。
- 敏感度扫描（同样输入下末梢偏离）：

  | base | ωn | ζ | 末梢偏离 | 撞限位 |
  |---|---|---|---|---|
  | 33 | 2.87 | 0.20 | 6.6 cm | 否 |
  | 100 | 5.00 | 0.35 | 6.8 cm | 否 |
  | **300** | **8.66** | **0.61** | **5.6 cm** | **否** |
  | 1000 | 15.81 | 1.11 | 1.7 cm | 否 |

  ⇒ **`ζ` 由作者参数直接决定，是"松紧"的可靠指标；`ωn` 的绝对尺度要靠实测校准。**
  校准办法（下一步）：拿 L4D2 里同一个 mod 的实机录屏，量马尾的摆动周期，
  反解出 base —— 之后所有 mod 都共用这个常数。

**踩过的两个坑（做离线解算器时一定会再遇到）**

1. **振幅单位**：Source 里 1 单位 ≈ 0.75 英寸 ≈ 1.9 cm。按"米"填 5.5 cm 的起伏
   实际会变成 0.8 g 的加速度，头发会甩到限位 —— 看起来像"疯了一样"。
   真实走路头部起伏只有 2–3 cm。
2. **收尾必须平滑**：直接把振幅归零 = 无穷加速度 = 假的大甩动。
   要用 0.4 秒的 cos² 包络降下来，才看得到真实的自由衰减。
---

## 15. 兼容层能力实测：链式传播 ✅（2026-10-31）

> 上一轮（§14.7）验证的是 **L4D2 侧成立**——那是前提，不是未知。
> 真正的未知是**我们这层能不能承载**。本节回答其中最要害的一条。

### 15.1 风险清单（按致命程度排序）

| # | 未知 | 状态 |
|---|---|---|
| **A** | **链式传播**：只改父节点的 local pose，子节点的世界变换会不会跟着动？ | ✅ **已测：会** |
| **B** | **每帧预算**：57 根 jiggle 骨 × 每帧 2 次调用 的帧率代价 | ⬜ 待测 |
| **C** | **时序**：写入是否同帧到达蒙皮矩阵上传 | ⬜ 待测（单骨已见同帧生效） |
| D | 沿轴伸缩（`alongStiffness` / `lengthConstrained`）：L4D2 里头发甩动会略微拉长 | ⬜ 待测（4×4 矩阵理论上可带缩放） |
| E | 限位语义（`angleLimit` 是相对动画姿态的偏角） | ⬜ 待测（需自己在 Lua 里实现） |

**A 是唯一的生死项**：马尾/裙摆/袖子全是链。不传播 ⇒ 只能做"整个部件一起晃"，做不出鞭梢，等于废掉。

### 15.2 实验设计（最小、决定性）

在**头盔件**（`0xa0cc3d2318d9e237`，独立单件 kit，干净）里插两个节点做成父子链：

```
jig_a  [112]  父 = chest[19]    世界 (0, 0.197, 1.378)
jig_b  [113]  父 = jig_a[112]   世界 (0, 0.197, 1.178)   local 相对 jig_a = (0, 0, −0.20)
```

然后把吊坠的残留权重（`support_mg` 那 6751 个顶点）重指向 `jig_b`
—— **只有当有顶点挂在子节点上，画面上才能看出链是否传播**。

**判据（与单位无关，最干净）**：子相对父的方向向量 `d = worldB.pos − worldA.pos`。

- 父转 θ，`d` 跟着转 θ ⇒ **引擎在合成父子链**
- `d` 方向不变 ⇒ 引擎不合成，必须自己算累积变换

### 15.3 结果：**引擎确实合成父子链** ✅

```
静置          d = (−0.0379, −0.1962, −0.0074)   |d| = 0.2000
写 jig_a 绕 x 转 40.00°：
  n=1         d = (+0.0972, −0.1748, −0.0029)   |d| = 0.2000   转角 = 40.00°
  n=31        d = (+0.0963, −0.1753, −0.0036)   |d| = 0.2000   转角 = 39.70°
  n=61        d = (+0.0972, −0.1747, −0.0049)   |d| = 0.2000   转角 = 40.00°
  n=121       d = (+0.1024, −0.1715, −0.0095)   |d| = 0.2000   转角 = 41.74°
```

- **`|d|` 全程死咬 0.2000**（链是刚性的，没有塌陷）
- **`d` 的方向转了 40.00°，正好等于我们写给父骨的角度**

⇒ **我们只需要每帧写链上每一根骨的 `local pose`，引擎自己往下传。**
⇒ 不需要自己算累积变换，也不需要写世界姿态。
⇒ **L4D2 那种"逐节相位滞后"的鞭梢效果，这层能承载。**

（后续 n=151/181 读到 63°/62°，是因为角色待机动画本身在动 chest，`d` 的基准是更早的姿势；
不影响结论——`|d|` 始终 0.2000。）

### 15.4 这轮又踩的两个坑

1. **Lua 5.1 / LuaJIT 没有 `math.degrees`**（也没有 `math.rad` 之外的三角函数换算）。
   用了它 → 抛错 → 被 `pcall` 吞掉 → **日志静默缺行**，排查了很久。
   已改成 `local function deg(rad) return rad * 180 / math.pi end`。
2. **命令文件只在"内容变化"时才重读**。第一遍读时 armor 还没发现，
   所以依赖配置回调去解析后续字段（如 `childbone`）会永远拿不到。
   **凡是"发现 unit 之后才能解析"的字段，必须在用到它的地方惰性解析。**

### 15.5 复现命令

```powershell
$exe  = 'tmp\m2-probe\bin\Release\net8.0\m2probe.exe'
$hash = 'HD2ModManager\Resources\AssetMetadata\bonehash.txt'
$orig = '<原版本体 patch_0>'

# 1) 插父节点
& $exe patchaddnode $orig tmp\chain\s1 jig_a chest 0.0 0.197 1.378 support_mg `
       --only a0cc3d2318d9e237 --fix-0x44
# 2) 插子节点（父 = jig_a）
& $exe patchaddnode tmp\chain\s1\9ba626afa44a3aa3.patch_0 tmp\chain\s2 jig_b jig_a 0.0 0.197 1.178 `
       support_mg --only a0cc3d2318d9e237 --fix-0x44
# 3) 把吊坠权重挂到子节点（不移动节点）
& $exe addpivot tmp\chain\s2\9ba626afa44a3aa3.patch_0 tmp\chain\out\9ba626afa44a3aa3.patch_0 `
       jig_b chest support_mg $hash --at 0.0 0.197 1.178 --keep-pivot

# 4) 探针命令（新增 unitdbg 精确选件 + chain 模式）
# hd2pbx_cmd.txt:
#   mode=chain
#   bone=jig_a
#   childbone=jig_b
#   axis=x
#   angle=40
#   unitdbg=a0cc3d2318d9e237
```

### 15.6 下一步（顺序已定）

1. **B：57 骨预算**——把 Lunalice 的 57 根骨真插进去（离线批量插节点），
   Lua 每帧写 57 个，量帧率与 `Application.time_since_launch` 差值。
   若代价过高，退路是"只驱动离镜头近的链"或降频。
2. **C/D/E** 视 B 的结果再定。
3. **最后**才接 VPK 导入——因为**导入产物的结构取决于 A~E 的结论**。
   A 已确认"只需 local pose"，所以导入侧要产出的是
   **每根新骨的名字 + 父骨 + rest pose + jiggle 参数**，不需要预烘焙世界矩阵。


### 15.7 B：57 骨预算实测 ✅（2026-10-31）

**做法**：一次往头盔件插 **57 个节点**（`jig_1..jig_57`，父都是 chest），
单元从 112 节点涨到 **169 节点**；Lua 每帧对这 57 根骨做
`local_pose → 换旋转 → set_local_pose → update_unit`。
在同一局游戏里用改命令文件的方式切三态对比：

| 状态 | 帧率 | Lua 每帧耗时 |
|---|---|---|
| 基线 `mode=off` | **106–108 fps** | 0.000 ms |
| `mode=stress` **57 骨** | **106.0 / 106.1 / 106.7 fps** | **0.057 / 0.088 ms** |
| `mode=stress` 1 骨（对照） | 106.6 / 107.3 fps | 0.018 / 0.040 ms |

（注：舰船里游戏自己锁在 ~107 fps；早期读到 380 fps 是加载/菜单阶段的未锁帧。）

**结论**

- **57 根骨每帧只花 0.06–0.09 ms**，约 **1.5 µs / 骨**。
- 107 fps 的帧预算约 9.3 ms ⇒ 单角色 57 骨占 **不到 1%**。
- 按 4 人小队都开物理估算：4 × 57 = 228 骨 ≈ **0.35 ms**，仍然很宽裕。
- **169 节点的单元渲染完全正常**，没有任何破坏（截图已验）。

⇒ **B 不是问题。社区的 HD2IAS 卡顿不是因为"写了多少骨"，
而是因为它还要额外送原生插件、每次都让引擎重算世界变换。**
我们这条路是纯场景图写入，成本线性且极低。

### 15.8 能力测试小结

| # | 未知 | 结论 |
|---|---|---|
| **A** | 链式传播 | ✅ **引擎会合成父子链**，只需写 local pose |
| **B** | 57 骨预算 | ✅ **0.06–0.09 ms/帧**，可忽略 |
| **C** | 时序（是否同帧到达蒙皮上传） | 🔶 单骨已见同帧生效，链上待复核（优先级低） |
| D | 沿轴伸缩（`alongStiffness`） | ⬜ 待测；4×4 矩阵理论上可带缩放 |
| E | 限位语义 | ⬜ 待测；需在 Lua 里实现 |

**⇒ 兼容层的两条生死项都通过了。可以接 VPK 导入了。**

### 15.9 新增工具能力（m2probe / 探针）

| 能力 | 用法 |
|---|---|
| 一次插多根骨 | `patchaddnode` 的 `<new>` 参数支持**逗号分隔列表** |
| 按单元精确选件 | 命令文件 `unitdbg=<fileIdHex>`（`Unit.id` 不可靠，`debug_name` 返回 `#ID[hex]`） |
| 预算测量 | 命令文件 `mode=stress; prefix=jig_; count=57; axis=x; angle=8` |
| 帧率采样 | 探针**始终**每 600 帧输出一行 `[fps]`，含 Lua 每帧耗时 |
---

## 16. VPK 导入链路现状盘点（2026-10-31）

### 16.1 结论：**提取端已经做完了，缺的只是"输出"**

原本以为要从零写 mdl 的 jiggle 解析，实际盘点发现**管理器里早就实现了**：

| 位置 | 内容 |
|---|---|
| `HD2SourceModel/SourceModelReader.cs:171` | `ReadJiggleBone` —— 读 `proctype == 5`，取 `procindex`，解出 **flags + 全部 29 个参数** |
| `HD2SourceModel/SourceModel.cs` | `record SourceJiggleBone(int Flags, float Length, float TipMass, float YawStiffness, …)`，**字段顺序与我们独立解析的完全一致** |
| `HD2SourceModel/SourceModelInspector.cs:64` | 汇总 `JiggleBoneCount` 与 `JiggleParents`（按父骨分组） |

而且 `SourceModelReader.cs:178` 的注释已经写在点上：
> *"procindex 是相对「该骨骼结构体自身地址」的字节偏移，不是相对文件头。"*

——**这正是我这一轮独立踩了一遍才发现的东西。** 我们自己的仓库早就知道。

`source inspect` 的实测输出也与独立解析逐项吻合：

```
boneCount 127 / namedBoneCount 62 / jiggleBoneCount 57
jiggleParents: Head1×15, Spine×6, Pelvis×4, Spine4×4, L_Calf×2, R_Calf×2, …
```

### 16.2 唯一的缺口：参数没被输出

`SourceModelInspector` 只把 **数量** 和 **父骨分布** 写进报告，**把 29 个参数丢掉了**。
所以要让参数进 `HD2PBX01`，只需：

1. 在 `SourceModelInspector` 的报告里补上 `jiggleBones[]`（含 flags/参数/父骨/rest pose）
2. 在 `vpk2patch` 产物里写出 `HD2PBX01` 容器（协议已定稿，见
   `docs/hd2physbone-embedded-patch-protocol.md`）
3. Lua 兼容层读容器 → 每帧驱动（链路已由 §13/§15 验证）

### 16.3 顺带发现的一个问题（待单独排查）

`vpk2patch` 需要护甲索引（`ArmorIndexUnavailable`），但索引的 equipment 表建不起来：

```
gamedata index plan rebuild  → ok
gamedata index plan units-all → ok
gamedata index apply (units-all) → GameDataIndexPostconditionFailed
   advancedEquipmentFactsCurrent 仍为 false
```

⇒ `units-all` 的**后置条件永远不满足**。这看起来是索引维护链路的一个真实缺陷，
与物理功能无关，但会**卡住 vpk2patch 这条主路径**。需要单独查。

**临时绕行**：`source inspect` 是纯只读路径、不需要护甲索引，可以正常跑
（也正因如此，这一轮的盘点没被这个缺陷挡住）。

### 16.4 这一条对排期的意义

原计划"从零做 mdl 解析 + 参数提取"→ 实际**只剩"把已有数据接上输出"**。
工作量从几天降到很小。下一步直接改 `SourceModelInspector` 的输出结构即可。


### 16.5 更正：上一节的"索引缺陷"是我调用姿势错了（2026-10-31）

上一节说「`units-all` 后置条件永远不满足，看起来是索引维护链路的真实缺陷」——**这条判断错了**，
误导来自一个更基础的问题：

> **CLI 解析索引数据库路径用的是「当前工作目录」，不是 `--manager-root`。**

证据：
- 报错里给的库路径是 `E:\Data\source\repos\WpfApp1\data\indexes\asset-index.sqlite`，
  而 `E:\Data\source\repos\WpfApp1\data` **根本不存在**；
- 真正的库在 `HD2ModManager\bin\Debug\net10.0-windows\data\indexes\asset-index.sqlite`（108 MB）。

**把工作目录切到管理器目录后，`vpk2patch` 直接跑通：**

```
hd2mod vpk2patch <Zoey.vpk> --output <out> --game-data <data> --manager-root <mgr> \
    --avatar-toc <unit_0x4d1c334d294dfa97.toc> --bone-hash <bonehash.txt> --json
  → ok: true
  → 7 个 Unit，37 个 group，patch 0.19 MB + gpu_resources 30 MB
  → 条目 0xe0a48d0be9a7453f / 0x52b033ed9b3b0001 .. 0007
```

⇒ **`vpk2patch` 主路径是通的。** 之前所有 `ArmorIndexUnavailable` 都是路径问题。

**⚠️ 以后跑 CLI 必须把工作目录设成管理器目录**，或者等这个路径解析改掉：

```powershell
--workdir 'E:\Data\source\repos\WpfApp1\HD2ModManager\bin\Debug\net10.0-windows'
```

**仍然存在（但不阻塞我们）的一个独立状态问题**：
即使在正确工作目录下，`gamedata index inspect` 的 `advancedEquipmentFactsCurrent` 依然是 `false`，
所以 `gamedata index apply (units-all)` 的后置检查仍然会失败。
但 `vpk2patch` 并不需要那个标志——它只需要库里**已经有护甲表**（已满足）。
⇒ 这是索引维护链路自己的小缺陷，与物理功能无关，记录在案即可。

**材质缺失是正常的**：这个 VPK 只含模型，材质在另外几个 VPK（`Lunalice基础材质A1主体` 等）。
`vpk2patch` 支持传一批 VPK 作为解析集合，把材质 VPK 一起放进去就不会有那些
`源材质 … 找不到 .vmt` 的警告。

### 16.6 第一步已落地：参数输出 + HD2PBX01 容器（2026-10-31）

**改了哪些生产代码**

| 文件 | 改动 |
|---|---|
| HD2SourceModel/SourceModelInspector.cs | 新增 SourceJiggleFlags（flags 位 → 名字）、SourceJiggleBoneFact（单根骨的全部事实）、SourceModelInspection.JiggleBones |
| HD2SourceModel/PhysBoneContainer.cs | **新增**。PhysBoneSpringRig.Serialize（→ hd2_spring_rig.bin）与 PhysBoneContainer.Build/AppendTo（→ HD2PBX01） |
| HD2ModCli/CliApplication.cs | source inspect 打印逐骨参数表；**新增命令** source physbone |

**新命令**

`powershell
hd2mod source physbone <VPK> [<模型名片段>] --output <目录> [--overwrite] --json
`
产出两份文件：
- hd2_spring_rig.bin — 定长二进制，运行时兼容层直接读
- physbone-container.bin — 组装好的 HD2PBX01 容器（后续嵌进 patch 的 .gpu_resources）

**hd2_spring_rig.bin 布局（自描述、版本化）**

`
magic "HD2SRG01"(8) | version u32 | boneCount u32 | nameTableOffset u32
| nameTableSize u32 | recordSize u32 | 保留 u32          = 32 字节头
每根骨 144 字节：boneIndex u32 | parentBoneIndex i32 | nameOffset u32 | flags u32
                | worldPos 3×f32 | 29×f32 参数（顺序同 SourceJiggleBone 声明）
名字表：NUL 结尾 UTF-8
`

**三重交叉验证（全部通过）**

| 验证 | 方法 | 结果 |
|---|---|---|
| 容器格式 | **用仓库里独立的 Python 实现**（	ools/hd2physbone_patch_container.py 的 ind_container）读 C# 生成的容器 | ✅ 起点/长度/版本/flags/记录名/载荷 SHA-256 全部正确，载荷逐字节一致 |
| rig 内容 | 把 C# 生成的 hd2_spring_rig.bin 解回来，与**我完全独立解析 mdl 得到的 JSON** 比对 | ✅ 57 根骨的 index / parent / flags / 29 参数**全部一致** |
| 回归 | 全解决方案 dotnet build -c Debug | ✅ 0 警告 0 错误 |

验证脚本留在 	mp/verify-pbx.py、	mp/verify-rig.py（**这两个脚本值得沉淀进仓库测试**）。

**实测数据（Lunalice / Zoey）**

`
57 根 jiggle 骨，rig 9178 字节，容器 9356 字节
双马尾链在参数表里直接可见：72 → 73 → 74（3 节）
`

**还差的一步**：把容器挂进 pk2patch 的产物（追加到 <patch>.gpu_resources 末尾）。
容器读写两端都已验证，这一步只是接线。

### 16.7 第二步落地：容器已进 vpk2patch 产物（2026-10-31）

**改了什么**

| 文件 | 改动 |
|---|---|
| HD2SourceModel/PhysBoneContainer.cs | 新增 StripIfPresent（去重，保证重复转换幂等） |
| HD2ModCli/CliApplication.cs | pk2patch 在转换完成后，把 HD2PBX01 容器追加到每个 patch 的 .gpu_resources 末尾；报告里新增 physBones[] |
| HD2SourceModel.Tests/PhysBoneContainerTests.cs | **新增** 8 条容器测试 |
| HD2SourceModel.Tests/PhysBoneSpringRigTests.cs | **新增** 5 条 rig 测试 |
| HD2SourceModel.Tests/RealSourceModelTests.cs | 新增 1 条真实 VPK 的参数/链拓扑钉桩 |

**关键设计（为什么这么接）**

1. **单独一步、单独兜错**：物理数据是附加载荷，不改 TOC、不参与引擎解析。
   让它去拖垮整批转换不合理；但失败必须**可见**（走 Console.Error 警告，不静默）。
2. **模型一致性校验**：SourceModelInspector 会自己重新定位 mdl。如果它定位到的模型
   与转换用的不是同一个，**跳过**而不是硬塞——张冠李戴比没有更糟。
3. **幂等**：StripIfPresent 先去掉可能存在的旧容器。
   实测重跑 pk2patch 后 .gpu_resources 字节数**完全不变**。

**端到端实测（Lunalice / Zoey）**

`
vpk2patch → ok
physBones: patch=9ba626afa44a3aa3.patch_0, bones=57, rigBytes=9178, containerBytes=9356
用独立 Python 实现 inspect：
  gpuLength            31496786
  maxTocReferencedGpuEnd 31487430
  container.start      31487430     ← 紧贴 TOC 引用区之后
  container.length     9356
  prefixLength         31487430
  records: hd2_spring_rig.bin 9178 bytes  sha256=8f7d76f4…
  unreferencedBytesAtEnd 9356        ← 尾部只有这一层容器
`

**测试结果**

| 项目 | 结果 |
|---|---|
| HD2SourceModel.Tests | ✅ **66 通过**（含 14 条新增） |
| HD2ModCli.Tests | ✅ **195 通过**（vpk2patch 的既有契约没被破坏） |
| 全解决方案构建 | ✅ 0 警告 0 错误 |

**这一环闭合了**：源模型 → 参数提取 → 二进制 rig → HD2PBX01 容器 → patch 产物，
全部经过**两套独立实现**互相验证。

**下一步**：Lua 兼容层读容器 → 按参数驱动场景图节点（§13/§15 已验证链路）。
---

## 17. 兼容层运行时（2026-10-31）

实现：`HD2PhysBoneCompat/src/physbone_compat.lua`（纯 Lua，无 FFI、无内存访问）。
离线自检：`tmp/lua-compat/test_parse.lua`（用 luajit 跑，不依赖游戏）。

### 17.1 它做四件事

1. **找容器**：在若干候选路径里试 `data/<archiveId>.patch_N.gpu_resources`，
   从 EOF 的 `HD2PBFT1` footer 回推 `HD2PBX01` 容器起点（不看绝对偏移）。
2. **解 rig**：取出 `hd2_spring_rig.bin`，解析 57 根骨的名字/父骨名/flags/位置/29 参数。
3. **建链**：按父子关系串成链（链根 = 父骨不是 jiggle 骨的那些）。
4. **每帧驱动**：锚点世界位置二阶差分 → 加速度 → 逐节弹簧积分 → 只换旋转写回 local pose。

### 17.2 Lua 5.1 的硬约束（这一节能省后来人半天）

**没有 `string.unpack`，也没有 `math.degrees`。** 逐字节手解：

```lua
u16 = b0 + b1*256
u32 = b0 + b1*256 + b2*65536 + b3*16777216
i32 = (v >= 2^31) and v - 2^32 or v
u64 = u32(low) + u32(high) * 4294967296      -- 注意 >2^53 会丢精度，容器长度远小于它
f32 = IEEE754 手工还原（符号/指数/尾数，含 0 与非规格化）
```

`math.degrees` 在 LuaJIT 里是 `nil`——用了会抛错，而错误被 `pcall` 吞掉后表现是
**日志静默缺行**，极难查（§15.4 记过同一条）。

### 17.3 rig 格式升到 v2：加父骨名字

v1 只有父骨**索引**（源模型的骨下标）。运行时要把链根锚到 HD2 的骨上，
但 HD2 侧拿不到源模型的骨表——**只有名字能跨过去**。所以 v2：

```
每根骨 148 字节（v1 是 144）：
  boneIndex u32 | parentBoneIndex i32 | nameOffset u32 | parentNameOffset u32
  | flags u32 | worldPos 3×f32 | 29×f32 参数
名字表：骨名 + 父骨名，都 NUL 结尾
```

配套的锚点映射（`M.default_anchor_map`，与仓库 `hd2_style.py` 一致）：

| 源骨 | HD2 骨 |
|---|---|
| `Head1` | `head` |
| `Spine4` | `chest` |
| `Spine2` / `Spine1` / `Spine` | `spine2` / `spine1` / `spine1` |
| `Pelvis` | `hips` |
| `L_Calf` / `R_Calf` | `l_knee` / `r_knee` |

### 17.4 三条铁律的落地（都写在代码注释里）

| 铁律 | 代码位置 |
|---|---|
| 缓存基准姿态，每帧从基准算 | `attach` 里存 `basePoses`，`update` 里始终基于它 |
| 只换旋转、保留平移 | `from_quaternion_position(combined, originalTranslation)` |
| 写完 `World.update_unit` | `update` 末尾 |

另外两条实测得来的修正：

- **段长要自己求**：rig 存的是世界 bind 位置（用来放骨），不是相对父骨的偏移。
  段长按"到父骨世界位置的距离"算，父骨不在 rig 里就退到"到第一个子骨的距离"。
  用错会让弹簧明显偏刚或偏软（第一版误用 `|worldPos|` 得到 61 而不是 7.37）。
- **dt 要夹住**：首帧或卡顿时 `dt` 异常会让弹簧一步打飞。`dt > 0.1` 时按 1/60 处理。

### 17.5 离线自检结果（`luajit tmp/lua-compat/test_parse.lua`）

```
容器：version=1 length=10752 记录=1
  - hd2_spring_rig.bin    10574 字节
rig：version=2 骨数=57
链：共 39 条，最长 3 节，3 节的链 2 条
  3 节链: 72 -> 73 -> 74
  3 节链: 75 -> 76 -> 77
骨 72 的父骨名 = ValveBiped.Bip01_Head1
锚点映射：Head1 -> head
骨 72 的弹簧：ωn=8.66 rad/s  ζ=0.61  限角=35.0°  段长=7.37
积分 600 步：峰值偏离 31.13°（限角 35.0°）   ← 不越限、不发散
PASS
```

**这是"C# 写 / Lua 读"的跨语言验证**：容器、rig 头、记录字段、名字表、
29 个参数的顺序与数值，两边完全对上。

### 17.6 还差什么才能进游戏看到头发在动

**权重。** 现在能插节点、能驱动、能读到参数——但如果没有任何顶点加权到这些新骨上，
写进去的姿态就没有可见效果（这正是 §12 那个"Stage A 能过是因为没人用新节点"的教训）。

⇒ 需要一个**权重映射步骤**：把源模型里落在 jiggle 骨上的顶点权重，
搬到 HD2 Unit 新插的对应节点上。这一步属于转换器（`HD2SourceMigration`），
不在兼容层里。做完它，§15 验证过的驱动链路才会产生可见的头发/裙摆摆动。

**建议顺序**：
1. 先在**手工包**上验证整条回路（插 N 个节点 + 把某块顶点重指向 + 兼容层驱动）。
   我们已经有 `patchaddnode` / `addpivot` 和头盔件这个干净样本。
2. 再把"重指向"自动化进转换器：按源骨名 → 新节点名 → 顶点权重重映射。
---

## 18. 两个误判的复盘 + 测试纪律（2026-10-31 末）

这一轮我连续两次把"现象"和"我能验证的机制"过早连成因果，值得逐条记下来。

### 18.1 症状与真因（一一对应，不要混为一谈）

| 症状 | 真因 | 证据 |
|---|---|---|
| **面部材质品红**，头发/衣物/装饰正常 | **材质 id 的命名空间分裂** | 不传 `--id-namespace` 时，两次转换产出的材质 id 完全不同。实测：固定命名空间后，`a1主体` 的材质 id 在两次运行间**逐位相同**（`0xa103101a3cd5fbe1` …） |
| **部署报成功，但游戏 data 里没有任何 patch** | **激活的配置文件是空壳**（`entries = 0`） | 部署计划里 `effectiveEntryIds: []`、`patches: 0`。切回有 13 条目的配置后，规划 26 个文件、实际落 13 个符号链接 |

**教训（写死）：先确认"游戏里到底部署了什么"，再解释画面。**
我这几轮所有的判读都建立在一个我没验证过的假设上——"文件是我刚转的那一份"。
实际有几轮是空配置在跑，看到的根本不是新一轮的产物。

### 18.2 命名空间的硬规则

> **同一个模型的所有组件（模型 VPK + 全部材质 VPK）必须在同一批里转换，或显式共享同一个 `--id-namespace`。**

为什么：`6fb64f6`「材质/贴图 id 加来源命名空间」之后，材质/贴图 id 会带上一份"来源命名空间"。
不指定时它按批次生成——于是：
- 模型补丁里 Unit 引用的材质 id 来自**批次 A**
- 材质包写出的材质 id 来自**批次 B**
- 两者对不上 ⇒ 引擎找不到材质 ⇒ **品红占位**

现象很有欺骗性：**只有"贴图在另一个包里"的那些材质会坏**。
Lunalice 的面部正是这种——`shinano_face.vmt` 在 A1 主体，而 `shinano_face.vtf` 在 E1/E2/E3 瞳色包。
头发、衣物的 VMT 与 VTF 同在一个包，所以看起来"大部分正常、只有脸坏了"。

**测试纪律**：不要为了省时间把同一个模型的组件分多批导入。
分批 = 分裂命名空间 = 你会花几个小时去查一个自己造出来的 bug。

### 18.3 这是产品级风险，不只是我测试姿势的问题

玩家最常见的操作就是"先装角色，后补一个发色/瞳色包"。按现在的默认行为，**这会直接导致品红**。
所以值得单独修：

- `--id-namespace` 不该是"不传就随机"；
- 更合理：默认从**内容**推导（例如源 VPK 集合的指纹），或同一批 queue 自动共享并**持久化**，
  让后续追加的包能复现同一套 id；
- 修之前，至少要在追加材质包时给出明确警告。

### 18.4 我另外那个真 bug（与上面无关，但也得记）

上一轮我在 `vpk2patch` 里把 `HD2PBX01` 物理容器**追加到了转换输出的 `.gpu_resources`**。
下游的导入 / `vpk rekey` 把那份文件当受管 GPU 数据流，尾部多出的 10752 字节让记账错位。

```
未追加  31,487,430 字节  sha adf1d3f0a70eb8e5
追加后  31,498,182 字节                      ← 差 10752，正是容器大小
```

已改为**默认关闭**（`HD2_PHYSBONE_CONTAINER=1` 才开），并在代码注释里留了对照数据。
容器应该在**交付阶段**加，不在纯净的转换输出里。
---

## 19. 测试环境纪律 + 下一步（上下文压缩前交接）

### 19.1 测试环境纪律（用户明确要求，必须遵守）

| 规则 | 说明 |
|---|---|
| **只用 `测试配置文件`** | 不要新建、不要切换配置文件。用户能看见管理器、游戏、mod 库的变化，但**配置文件的变化他很难发现**——一旦我改了而他不知道，就会拿错误的前提去判读，闹出"部署成功但没文件"那类乌龙。 |
| **改了明显的状态要当场说明** | 关管理器 / 起停游戏 / 动测试库 / 部署 mod —— 这些用户看得见，可以做，但要明说。 |
| **先确认部署状态，再解释画面** | 判读前先看两样：部署计划里的 `effectiveEntryIds`，以及游戏 `data` 目录里实际有哪些 patch。 |
| **同一模型的组件不要分包转换** | 模型 VPK 与全部材质 VPK 必须同一批，或显式共享 `--id-namespace`；否则材质 id 分裂 ⇒ 品红（详见 §18.2）。 |

> 遗留：我建过一个空配置 `Lunalice-RE2310-物理测试`（entries=0，无害）和一个 `luna-*` 系列 mod 目录。
> 库里现在的 13 个 `lunalice*` 节点是用户自己 GUI 导入的。清理前先问。

### 19.2 已验证可复用的东西（别再重做）

| 能力 | 位置 | 状态 |
|---|---|---|
| 插节点（含头部 `0x44` 必须 +64 的修正） | `tmp/m2-probe` `patchaddnode --only <id> --fix-0x44` | ✅ |
| 调色板重指向 + 骨骼矩阵重算 | `tmp/m2-probe` `addpivot --keep-pivot` | ✅ |
| jiggle 参数提取（管理器侧） | `SourceModelReader.ReadJiggleBone` + `SourceModelInspector.JiggleBones` | ✅ |
| rig 二进制（v2，含父骨名）+ `HD2PBX01` 容器 | `HD2SourceModel/PhysBoneContainer.cs` | ✅ 两套独立实现互相验证 |
| `source physbone` 命令（出 rig + 容器） | `HD2ModCli` | ✅ |
| Lua 兼容层（读容器 → 建链 → 弹簧 → 驱动） | `HD2PhysBoneCompat/src/physbone_compat.lua` | ✅ 离线自检通过 |
| 链式传播 / 57 骨预算 | — | ✅ 实测：引擎自动合成父子链；57 骨 0.06–0.09 ms/帧 |
| 无人值守测试 | `tools/game-input.py` + `tmp/run-variant.ps1` | ✅ 点击进房 + 按 S 转身 + 截图 |

**当前唯一缺口**：转换出来的 Unit 里**没有 jiggle 节点**，源模型的权重也**没有**搬到新节点上。
实测证据：`attach：可用链 0 条（缺节点 39）` ⇒ 头发只能刚性跟随（钢板）。

### 19.3 下一步：插节点 + 权重映射（采用 B 方案）

**为什么用 B（独立后处理）而不是直接改转换器**：
现在还没证明"插骨 + 权重映射"在真实 Lunalice 模型上视觉正确。
先做**输入转换产物 → 输出插好骨的 patch** 的后处理，验证有效再合并进 `HD2SourceMigration`，
失败也不污染主链路。

**B 的具体步骤**

1. **一次批量**转换：`Lunalice_Zoey.vpk` + A1 + B1 + C1 + D1 + E1，带固定 `--id-namespace`
   （绝不分包，见 §19.1）。
2. 读 rig（57 根骨）→ 对目标护甲 Unit **批量插 57 个节点**：
   父节点按 rig 的 `parentName` 映射到 HD2 骨（`Head1→head` / `Spine4→chest` / `Pelvis→hips` / …），
   世界位置用 rig 里的 `WorldPosition`（源单位，需换算：1 单位 = 0.01905 m）。
3. **权重映射**：源模型里落在 jiggle 骨上的顶点，其调色板槽位重指向新节点，
   并按不变式 `BoneMatrices = X · inverse(world_new)` 重算矩阵（复用 `addpivot` 的逻辑）。
4. 部署 → 进游戏 → 看双马尾是否在走动时摆动、停下后衰减。
5. 视觉通过后，再把这两步收敛进 `HD2SourceMigration` 的转换器，并补回归测试。

**风险与回退**：节点数从 114 涨到 ~171，之前实测 169 节点渲染正常（§15.7）；
若出现材质/剔除异常，先怀疑 LOD 组，再看 §8 的 U1（`0x44` 已修，`0x48` 未知但未动）。

### 19.4 未完成但已记录的事项

- `--id-namespace` 默认行为应改为**稳定**（内容指纹或持久化），否则"后补材质包"必然品红（§18.3）。
- `C`（写入时序）/ `D`（沿轴伸缩）/ `E`（限位语义）三项能力尚未实测，优先级低于上面的缺口。
- 兼容层参数目前是 Lua 侧常量（`MASS_BASE = 300`、`maxAccel = 10`、`chainBudget = 0.55`），
  后续应从 `HD2PBX01` 容器读，做到"参数跟着 mod 走"。

---

## 20. 2026-10-31 晚：**头发真的动起来了**（§19.3 的 B 方案，实机通过）

§19.2 那条"唯一缺口"到此**关闭**：转换产物里现在可以有 jiggle 节点，顶点权重也真的切到了新节点上。

### 20.1 做了什么

工具：`tmp/m2-probe` 新增两个命令。

| 命令 | 作用 |
|---|---|
| `hairstats <patch> <unitHex> [--minverts N] [--top K]` | 把 LOD0 网格按**连通块**切开，报每块的顶点数/包围盒/质心/离 head 最远距离。用来找头发，尤其是双马尾。 |
| `addjiggle <patch> <outDir> <unitHex> [--side both|left|right] [--prefix jig_tail] [--nodes 3] [--topz 1.66] [--minabsx 0.10] [--dry]` | 插链节点 + **扩容调色板** + **逐顶点重写 index/weight**。 |

实机脚本：`tmp/run-jiggle.ps1`（部署 `patch_13`=模型变体、`patch_14`=探针 → 进房 → 按 S → 截图 rest / bendL / bendR）。

### 20.2 为什么必须改顶点，而不是 `addpivot` 那种重指向

转换产物里**所有头发顶点的主骨都是 `head`**（源模型那 57 根 jiggle 骨在重定向时全塌到 head）。
`addpivot` 是"整槽重指向"，一刀切下去头皮和发梢会一起跟着同一根骨头转 —— 等于把钢板换成另一块钢板。

要的是**沿发束渐变的权重**，所以：

```
调色板（BoneInfo）    3 槽 → 9 槽      每份 remap 追加 6 个 fake 下标（身份映射）
顶点 index 分量       改 fake 下标      前两槽写 (A, B)
顶点 weight 分量      改 half 权重      吸附到 k/2048，整数和恰为 2048
```

**为什么各 BoneInfo 槽数必须一致**：新槽用一个 **fake 下标**引用，而 fake 下标是"该 BoneInfo 自己的数组位置"。
只有各份槽数相同，才能用同一组 fake 值覆盖所有材质段；而**一个顶点可能同时属于多个 section**，
顶点自己不知道属于哪个材质段，所以不可能按材质段写不同的 fake 值。头盔 unit 实测 5 份 BoneInfo 全是 3 槽，满足。

### 20.3 链的几何：从网格长出来，不从源 rig 搬

§19.3 原计划是"用 rig 里的 `WorldPosition`"。**实测不可行**：rig 的坐标是**源空间**（英寸、Y 向后、
脚底 z≈18、头顶 z≈70），而转换后的 Unit 是 HD2 模型空间（米、+Y 前、+Z 上、头顶 z≈1.69），
两者之间没有一个可信的映射（重定向对上身/头部/四肢各做了不同的缩放与旋转）。

所以改成：**链的几何从网格自己算，源 rig 只提供链条数与物理参数**。

```
候选顶点 = 主骨是 head 且 z <= topz 且 |x| >= minabsx
左右分组 = sign(x)
每侧按 z 等分成 N 段，关节位置 = 参数 k/N 处那一小段薄片的质心
权重     = 沿 z 的线性坡度：段 k 内 A=(k==0 ? head : node[k-1])、B=node[k]，按 frac 混合
```

实测（Bill 版头盔 unit `0xaf431352d9b95e83`，LOD0 25344 顶点）：

```
候选 2026 个（左 897 / 右 1129）
左侧 z∈[0.759,1.660] 长 0.901 m   →  jig_tail_L1 (-0.201,-0.046,1.621) / L2 (-0.190,-0.028,1.404) / L3 (-0.311,-0.180,1.035)
右侧 同上镜像
分段直方图 1179, 271, 576
```

### 20.4 离线自检（全部通过）

| 检查 | 期望 | 实测 |
|---|---|---|
| 新增节点 `det(rot)` | 1.0 | **1.000000 × 6** |
| 全 120 节点 `det(rot)` | 无退化 | 0 个 != 1 |
| `matrel`（BoneMatrices·world == X，5 份 × 9 槽） | 偏差 ≤2e-6 | **0**（修改前后都 0） |
| 新节点 `world == local · parentWorld` | ≤1e-6 | **1.79e-07 × 6**（与原生节点 2.5e-08 同量级） |
| 节点数 | 114 → 120 | 114 → 120 |
| toc 大小 | +816 + 4680 | 25830 → 31326 |
| gpu_resources | 不变 | 31501478 → 31501478 |

### 20.5 实机结果

```
[d24] ARMOR nodes=120 bones=- dbg=#ID[af431352d9b95e83] explicit=false   ← patch_13 确实生效
[chain] parent=115 child=116 静置 d=(+0.0135 +0.0028 -0.2175) |d|=0.2180
[chain] n=1   转角=34.96°  |d|=0.2180
[chain] n=511 转角=35.25°  |d|=0.2180
```

- `rest`（`tmp/lua-boot/jig-rest.png`）：**与插骨前逐像素无差**（静止姿态 = 资产里写死的姿态）。
- `bendL` / `bendR`（`tmp/lua-boot/jig-bendL.png` / `-bendR.png`）：发丝**整段平滑扫出去**，
  根部连着 head、中段渐变、梢部跟随，**没有拉伸、没有撕裂、没有品红**。

⇒ "头发像钢板"这条在 LOD0 上已经不成立了。

### 20.6 一个重要修正：local 矩阵的合成顺序

本轮为了自检写了个 python 脚本（`tmp/check-jig2.py`、`check-jig3.py`）去标定
`TransformInfo` 四个数组之间的合成关系，结论是：

```
world = local · parentWorld          （行向量约定，与 matrel 那条 v' = v·BM·world 一致）
```

用**已有节点**验证：`local·parent` 残差 5e-8 ~ 2.5e-8；反过来 `parent·local` 残差 0.47 ~ 1.88 —— 差一个数量级以上的顺序错误。

⚠️ **上一轮的 `InsertUnitNodes` / `addpivot` 写的是 `inv(parent)·world`（顺序反了）**。
它当时能"看起来正常"，是因为引擎在静止时用的是资产里写死的世界矩阵、local 不参与重算；
一旦 local 参与（被驱动、或引擎重算），这个节点就会错位。
`addjiggle` 里的 `InsertChainNodes` 已按标定结果写成 `Mul(world, InvertRigid(parent))`。
**那两个旧命令待修**（改动极小，但要重新跑一遍回归）。

### 20.7 与 §19.3 计划的偏离（有意为之）

| §19.3 原计划 | 实际做法 | 原因 |
|---|---|---|
| 先批量转 Zoey + A1..E1 | 直接用**已经部署好的** `E:\mod\lunalice_bill\Overwrite\*.patch_0` | 它已经是 rekey 到 RE-2310、材质正确、无品红的产物。重新批量转 6 个 VPK 要几分钟且有 `--id-namespace` 品红风险，而这一轮要验的是**字节手术**，不是转换链路。少一个变量。 |
| 链位置用 rig 的 `WorldPosition` | 从网格算 | 见 20.3，源空间与模型空间没有可信映射 |
| 57 根骨全插 | 只插双马尾 2×3 | 先把"调色板扩容 + 逐顶点权重"这条路验穿，57 根只是把同一段代码跑 39 次 |

### 20.8 已知限制与下一步

**限制**

- 只重写了 **LOD0** 顶点。LOD1..4 的头发仍全在 `head` 上 ⇒ 走远/换 LOD 时马尾会**跳回刚性**。
  扩展方式很直接：`RawMeshData` 逐个 LOD 跑同一套规则（每个 LOD 有自己的 mesh/stream/vertexBase）。
- 链节点的**旋转**沿用 head，所以链段的"朝向"不等于发束的真实走向。
  弹簧只需要"段长 + 限位"，当前够用；若要 `alongStiffness`（沿轴伸缩）就必须把朝向算对。
- 目前 `jig_tail_*` 名字不在 `bonehash.txt` 里，`weights`/`entries` 只能打印哈希。

**下一步（按价值排序）**

1. **接上物理**：把 `jig_tail_L1..R3` 喂给 `HD2PhysBoneCompat`（`attach` 的 `anchor_name` 已支持自定义链），
   用 rig 里那条 3 节点马尾链的参数（len=30 / yawS=50 / yawD=7 / ang=0.611）。
   这一步之后就是"走动时摆动、停下后衰减"的真物理，不再是 Lua 写死角度。
2. **推广到 57 根骨**：把 `addjiggle` 的"选顶点 → 建链 → 扩调色板 → 写权重"参数化，
   按 rig 的 39 条链逐条跑；同时把 LOD1..4 一起重写。
3. **收敛进转换器**：这套字节手术最终要变成 `HD2SourceMigration` 的一步（转换时就知道每个顶点的源骨权重，
   比事后从几何反推准得多 —— 那时根本不需要 `topz/minabsx` 这种阈值）。
4. 修 `InsertUnitNodes` / `addpivot` 的 local 合成顺序（20.6）。

### 20.9 当前游戏目录状态（用户可见，必须记住）

为了验证，我往游戏 `data` 目录塞了**两个不受管理器管辖**的文件：

```
data\9ba626afa44a3aa3.patch_13 (+ .gpu_resources)   ← 插了双马尾链的模型变体
data\9ba626afa44a3aa3.patch_14 (+ .gpu_resources / .stream)  ← 探针 Lua
```

- 它们**没有**进 `activation-state.json`，管理器下次部署可能报 `UnmanagedPatchDetected`。
- 清理：删掉这两个前缀的 5 个文件即可；`patch_0..12` 是管理器的符号链接，**没动过**。
- 探针命令通道仍是 `%LOCALAPPDATA%\hd2pbx_cmd.txt`，收尾时已写回 `mode=off`。

---

## 21. 2026-10-31 深夜：崩溃定位 + 测试环境改造（不再反复重启游戏）

### 21.1 这轮的两个真 bug（都有日志证据）

| 现象 | 证据 | 根因 | 修法 |
|---|---|---|---|
| 物理只"正常"约 2 秒，随后头发形状**冻结** | `n=720 写入 6 骨` → `n=840 写入 0 骨`，此后永远 0 | `attach` 缓存的 `Unit.local_pose` **是引擎临时句柄**，约 8 秒后失效；之后 `Matrix4x4.translation(base)` 返回垃圾 | 基准姿态改存**纯数字**（位置 3 + 四元数 4），每帧用 `make_quat` 重建 |
| 头发被拉成**一根巨针** | `末节=(-717872980557824 …)` 即 1e14；`跳过=31672` | `Matrix4x4.is_valid` 只查 NaN/inf，**拦不住 1e14**；坏矩阵被写进引擎 | 写前`pose_sane`：12 元素有限且 <1e6，旋转行列式 ∈ [0.5,1.5] |
| 加载场景时**连崩两次** | 用户实测 | 在加载过程中每 30 帧探测 `state.armor` 的节点；`Unit.*` 对**失效句柄是 AV 崩溃**，`pcall` 拦不住 | 只在 `discover()` 刚成功的那一帧碰 unit，采样节拍改为 300 帧一次 |

### 21.2 架构修正（用户提出，采纳）

**外部脚本不再判断"什么时候能写"**，只负责往 cmd 文件填参数。就绪判据、attach 时机、
失败回退**全部在 Lua 侧**：

```
wait  —— 每 300 帧 discover() 一次；成功则记录 (位置, 节点数) 作为采样点
         两次采样（间隔 2.5 s）位置位移 <= 5 cm 且节点数一致 ⇒ 判定场景稳定
live  —— 驱动；连续 45 帧零写入 或 护栏触发 ⇒ 退回 wait
```

这条 300 帧节拍是刻意的：每个采样期只读一次节点，把"碰到正在销毁的 unit"的机会压到最低。

### 21.3 新增护栏（三层，任一层都能单独挡住巨针）

1. **写前矩阵检查** `pose_sane`：量级 + 行列式，比 `is_valid` 严得多。
2. **写后漂移检测**：末节离 bind 超过 **1.5 m** ⇒ 该链清零并停用。
   正常摆幅（链长 ≈ 0.6 m）远小于它，而坏矩阵是 1e14 量级 —— 两头差得足够开。
3. **零写入回退**：连续 45 帧一根都没写进去 ⇒ 退回 `wait` 重新就绪，不再永久冻住。

### 21.4 测试环境：一个文件 + 一组指令，改参数**不必重启**

`tmp/set-params.ps1` 写 `%LOCALAPPDATA%\hd2pbx_cmd.txt`；游戏内 Lua 每 60 帧检查
**内容是否变化**，变了才整份重读（所以每次调用都要写全参数）。

| 行 | 含义 |
|---|---|
| `mode=jigphys` / `mode=off` | 开 / 停 |
| `maxaccel=3` | 驱动强度（走路量级 3；更飘给 5） |
| `smoothing=0.2` | 位置低通，越大越抗噪、越迟钝 |
| `chainbudget=0.55` | 整条链累计偏角上限（rad） |
| `reset=<ms>` | 丢掉会话 + 重新就绪采样 + 重挂（值带时间戳保证"内容变化"） |
| `release=<ms>` | 把**基准姿态写回**再放开头发（直接停 update 会把偏角冻在头上） |
| `dump=<ms>` | 把当前 phase / 活跃链数 / 重挂次数 / 参数写进日志 |

配套 API：`PhysBone.release(session)`（写回基准姿态并清空弹簧）。

### 21.5 现在的判读方式

日志 `%LOCALAPPDATA%\hd2pbx_probe.txt`，`[jigphys]` 开头的行按时间读：

```
就绪采样 1/2 → 场景还在变（…）→ 就绪 → 开始驱动（链 2 条，nodes=120，accel 上限 3.0）
n=300 写入=6 跳过=0 峰值=4.2° 锚点加速度=7.1 各节yaw=[2.1,1.7,0.8 | …] 重挂=0
```

出现 `[physbone][失败] 第一次写入失败：node#N <原因>` 或 `[physbone][护栏] 末节偏离 bind …`
就是坏在哪一步的直接证据，不需要再猜。

---

## 22. 分阶段探测定位到的四次崩溃真因（2026-10-31）

把物理流程切成 `stage=0/1/2` 三段、每段都落日志之后，四次"进游戏就崩"一次性定位到**唯一一句代码**：

```lua
-- 罪魁祸首（已删除）：
if make_quat(S, 0, 0, 0, 1) == nil then   -- 内部 S.Quaternion(0,0,0,1)
```

**`S.Quaternion(0,0,0,1)` 直接打死进程**：日志停在上一行，没有 Lua 错误、没有 Windows 崩溃记录 —— 进程就没了。
我加它的本意是"验证构造器能不能用"，**试探性调用引擎构造器这个做法本身就是错的**。

判据链（每步只看日志停在哪）：

| stage | 做到哪一步 | 结果 |
|---|---|---|
| 0 | 只做就绪判定（`discover()` + 读一次世界位置） | ✅ 通过，游戏活着 |
| 1 | 组装 rig + `attach` | ✅ 通过（`attach 进入` / `链数 2` / `可用链 1 条`） |
| 2 | 每帧 `update` | ❌ 见 §22.2 |

顺带确认的能力（`release` 指令实测）：

```
[jigphys] 指令 release：已把 3 根骨写回基准姿态
```

⇒ **`set_local_pose` 对我们插入的节点有效**，写姿态这条路本身是通的。

### 22.2 当前两个未解项（下一步）

1. **live 分支的 `update` 从未执行**：`dump` 显示 `phase=live`，但 `jigApplied` 停在 0、没有任何 `n=` 日志。
   已加 `live 心跳`（每 60 帧一行）来区分"没进分支"与"进了没走到日志"。
2. **2 条链只挂上 1 条**：离线跑 `M.build_chains` 得到正确的 2 条（各 3 节），
   所以跳过发生在 `attach` 内部。已加逐链日志：
   `[physbone] 链 jig_tail_L1：3 节，节点 3 个，基准姿态=成功/失败`。

### 22.3 引擎数学 API 的真实清单（以后不要再猜）

`rawget(S, "<命名空间>")` 遍历得到（`Matrix4x4` 40 个 / `Quaternion` 29 个 / `Vector3` 42 个）：

```
Matrix4x4: from_elements from_quaternion from_quaternion_position from_axes from_translation
           to_elements is_valid is_valid_for_physics multiply inverse rotation translation ...
Quaternion: from_elements to_elements from_matrix4x4 matrix4x4 multiply inverse axis_angle
            conjugate slerp lerp normalize ...
Vector3:   to_elements distance lerp multiply normalize make_axes element set_xyz ...
```

⇒ "基准姿态存数字"这条路可以走 `Quaternion.to_elements` / `from_elements`（存在，且 `from_elements` 实测可调用）。
`Matrix4x4.is_valid_for_physics` 比 `is_valid` 更严，应替换掉后者。

---

## 23. 2026-10-31 收尾状态（A/B 分离成功，剩一处控制流矛盾）

### 23.1 本轮最重要的方法：A/B 对照

把补丁换成一个**什么都不做**的 boot（`tmp/lua-boot/probe_noop.lua`，只写一行日志）后：

| 补丁 | 加载场景 |
|---|---|
| NOOP（空探针） | ✅ 正常进游戏 |
| 完整探针 | ❌ 崩 |

⇒ **崩溃来自探针代码，不是资产、不是环境。** 这是四轮下来第一次把"是不是我写的"分离干净。

顺着这个结论定位到两处**只在加载期跑一次的大批量引擎调用**，已停用：

- `census()`：对 **2291 个 unit** 逐个调用 `num_scene_graph_items / debug_name / has_node`（`MIN_CENSUS_UNITS = math.huge`）
- API 清单 dump（`API_DUMP_AT_FRAME = 1e9`，清单已在 §22.3 拿到）

停用后完整探针也能正常进游戏。

### 23.2 已实测通过的部分（可以当作地基）

```
[jigphys] rig jig_tail_L1 id=true node=115 世界=(…)        <- 6 个节点全部解析成功
[physbone] attach 进入 / 链数 2 / 链 #1/2：节数=3            <- 建链
[physbone] attach：可用链 1 条（缺节点 0、缺锚点 0）
[jigphys] 指令 release：已把 3 根骨写回基准姿态              <- set_local_pose 确实生效
```

### 23.3 唯一的未解矛盾（下一步就查这一个）

```
[jigphys] 阶段心跳 phase=live 会话=有 applied=0     ← 每 60 帧，一直有
（却从来没有 [jigphys] live applied=… 这一行）
```

心跳与驱动块在**源码里紧挨着**（composed_boot.lua 1583 → 1589 行），前者一直执行、后者从未执行，
且**没有** `[tick][Lua错误]`。这三条同时成立是自相矛盾的，静态读代码已无法解释。

已加一次性进入标记 `[jigphys] DRIVE-ENTER phase=… applied=… session=…`：

- 标记出现 ⇒ 驱动块能进，问题在它内部的某次调用（下一轮只需在块内二分）
- 标记不出 ⇒ 心跳与驱动不在同一执行范围，需要把 composed boot 里那一带的字节码打出来对照

### 23.4 顺手记下的两条硬教训

1. **绝不能试探性调用引擎构造器**：`S.Quaternion(0,0,0,1)` 直接打死进程（§22.1）。能力判断只做类型检查。
2. **诊断型的批量扫描要放在"场景已稳定"之后**，否则会在加载期碰到正在销毁的 unit。
   `census` 这类"全库遍历"在菜单/加载阶段等于拿命换信息。

---

## 24. 2026-10-31 结案：物理跑不起来的真正原因（一个 `end`）

### 24.1 根因

`HD2PhysBoneCompat/src/physbone_compat.lua` 里，`M.attach` **少了一个收尾 `end`**，
于是从 `function M.update(...)` 开始的约 200 行被**嵌进了 `M.attach` 的函数体**。

- Lua 语法完全正确、`luajit -bl` 编译通过、`end` 总数也是平衡的（多出来的那个落在 `M.update` 末尾）。
- 结果：`M.update` 在模块加载时**根本不会被定义** ⇒ `PhysBone.update == nil`。

离线一行就能验：

```bash
luajit -e "local M=assert(loadfile('HD2PhysBoneCompat/src/physbone_compat.lua'))(); print(type(M.update))"
# 修前：nil      修后：function
```

实机表现（就是这一路所有"怪现象"的来源）：

```
[drive] #1 C update ok=false 返回=boot.lua:1600: attempt to call field 'update' (a nil value)
```

因为驱动块用的是 `pcall(function() return PhysBone.update(...) end)`，**每帧都失败但只影响这一块**，
表现为"进了驱动分支、`applied` 永远是 0、没有相位变化、没有护栏触发"。

### 24.2 为什么排查了这么久（值得记住的教训）

| 掩盖因素 | 后果 |
|---|---|
| 外层 `pcall(tick)` 静默吞错误 | 四轮日志里一个字都没有 |
| 错误日志那版没进构建 | "没有 `[tick][Lua错误]`"被我当成了"没有错误" |
| `attach` 恰好定义在缺失的 `end` **之前** | 它能用，于是"模块加载正常"这个假设一直成立 |
| 症状伪装成控制流问题 | 我一直在查 phase 分支，实际是函数不存在 |

**判据应该换成离线自检**：模块改完先跑

```lua
print(type(M.attach), type(M.update), type(M.release))
```

一行就能否掉。这条应写进 `HD2PhysBoneCompat` 的改动纪律。

### 24.3 当前状态（重启一次即可确认）

已部署：`patch_13`（插骨+权重的模型）+ `patch_14`（修好的探针，103504 字节），`mode=jigphys stage=2`。

预期日志：

```
[drive] #1 C update ok=true 返回=3
[jigphys] live applied=60 written=3 链=1 峰值=x.x° 各节yaw=[…]
```

出现 `ok=true` 就说明弹簧真的在跑，接下来纯调手感。

### 24.4 接下来要做的事（按顺序）

1. **确认 update 在跑**（一次重启）。`written` 应为 3（左链 3 节）或 6（找到第 2 条链后）。
2. **修"2 条链只挂 1 条"**：`链数 2` 但只有 `链 #1/2` 落日志。现在循环已是下标式且每轮必打日志，
   重启后看 `链 #2/2` 是否出现；若不出现，把 `M.build_chains` 的返回值与 `ipairs` 换成显式 `for i=1,#chains`
   再打一次 `chains[i]` 的类型。
3. **只驱动 LOD0 的问题**：LOD1..4 的头发权重仍全在 `head`，走远会跳回刚性（§20.8）。
4. **轴映射**：现在拿世界 Y 当 yaw、世界 Z 当 pitch、**丢掉世界 X**（§19.4）。正确做法是用
   `Unit.world_rotation` 把加速度转到节点局部坐标系。`S.Quaternion` 有 `inverse / rotate`
   （见 §22.3 清单），但**绝不能试探性调用构造器**（§22.1）。
5. **基准姿态改纯数字**：`Quaternion.to_elements` / `from_elements` 都存在（§22.3），
   可彻底摆脱"句柄 8 秒失效"（§21.1）。现在是句柄+数字双存、数字优先的过渡态。
6. **参数收敛到容器**：`MASS_BASE/maxAccel/chainBudget` 仍在 Lua 侧常量，应从 `HD2PBX01` 读。
7. 最后才是**推广到 57 根骨 + 收敛进 `HD2SourceMigration`**（§19.3 的第 5 步）。

### 24.5 环境备忘

- 部署：`patch_13`（模型）/ `patch_14`（探针，`data\9ba626afa44a3aa3.patch_1[34]*`），
  都不受管理器管辖，下次部署可能报 `UnmanagedPatchDetected`；删这两个前缀即可。
- 调参/指令（**不必重启游戏**，改文件即生效）：
  ```powershell
  pwsh tmp/set-params.ps1 -MaxAccel 3      # 参数
  pwsh tmp/set-params.ps1 -stage 0/1/2     # 分阶段：只判定 / 只 attach / 完整驱动
  pwsh tmp/set-params.ps1 -Reset 1         # 重新就绪并重挂
  pwsh tmp/set-params.ps1 -Release 1       # 写回基准姿态、放开头发
  pwsh tmp/set-params.ps1 -Dump 1          # 打印状态
  ```
- 日志：`%LOCALAPPDATA%\hd2pbx_probe.txt`
- **诊断大批量扫描必须等场景稳定**（§23.1）；`census()` 与 API dump 已停用。

### 24.6 修掉缺失 `end` 之后（同轮实测）

```
[drive] #2 C update ok=true 返回=0        ← update 真的在跑
[jigphys] 阶段心跳 phase=live 会话=有 applied=8099   ← 每帧都在推进
```

⇒ 结构性修复生效。**但 `written` 恒为 0**，且两个"本该触发的自报"都没有出现：

- 没有 `[physbone][失败] 第一次写入失败：node#N <原因>`（写在 `M.update` 的写入循环里）
- 没有 `驱动失效（零写入 45 帧…）⇒ 重新就绪`（写在 probe 的驱动块里、紧跟在 applied 自增之后）
- 也没有 `[jigphys] live applied=…`（在 `applied % 60 == 0` 时打印，而 applied 每帧 +1，必然命中）

三条同时缺席 ⇒ **`applied` 自增之后的代码一被执行就抛错**，而外层 `pcall(tick)` 的错误日志在最��那版构建里没能确认存在。
下一步（优先级最高、改动极小）：

1. 在 `applied` 自增之后立刻加一行 `log("[drive] #%d D applied=%d written=%s", driveCount, applied, tostring(written))`，
   再在 `% 60` 块前后各加一行；**用 pcall 分别包住 `%60` 块与零写入判断**，把错误消息打进日志。
2. 同时确认 `rawset(_G,"update", …)` 里那段错误日志确实进了构建（`rg -c tickErrorLogged <patch>` 应为 2）。
3. `written == 0` 本身也要单独查：若 `M.update` 正常跑完却一根不写，只可能落在
   `session == nil / #sessions == 0 / basePoses[k] == nil` 三者之一，各自都有一行现成日志，应当能直接看到。

### 24.7 紧接着的第二个 bug：`started` 被删掉，驱动块每帧抛错

修掉 §24.1 的缺失 `end` 后（`update ok=true`），`applied` 从 0 涨到 8099，
但 `state.mode = "jigphys"` **始终没生效**（fps 行一直是 `mode=none`）、
`live applied=…` 不打、零写入回退也不触发。

两行相邻却只执行前一行 ⇒ 中间那行抛错。它是：

```lua
state.luaAccum = (state.luaAccum or 0) + (os.clock() - started)   -- started 已不存在
```

我把驱动块改写时把 `local started = os.clock()` 一起删了，只剩这一行引用它 ⇒ `number - nil` 每帧抛错。
而 `[tick][Lua错误]` 只打印**一次**（`state.tickErrorLogged` 早先已被别的错误置位），所以全程静默。

**教训（和 §24.2 同类，值得并进纪律里）**：

- 改写一块代码后，必须检查**被删掉的局部变量是否仍被引用**（`luajit -bl` 查不出，运行期才炸）。
- "某一行之后全都不执行" 是最强的定位信号：**它上面那一行就是凶手**。
- 一次性日志（`*Logged` 标志）会掩盖后续同类错误 —— 排查期应改成"每 N 帧最多打一条"，而不是只打一次。

### 24.8 用户退出时的转储分析（`PACKER_858634484_29628.dmp`，8.3 MB）

解析出来的硬事实：

```
异常码   0xC0000005  (ACCESS_VIOLATION, read)
标志     0x10        非续行式
故障地址 0x7ff60738a62b  →  helldivers2.exe + 0x64a62b   （153 个已加载模块中命中）
读取目标 0x1500b3b26f0   （野指针：0x1500… 这种位型很像带 tag 的指针/被写坏的 userdata）
```

补充事实：

- 故障落在**游戏主程序自身**的内存范围，**不是**显卡驱动（无 `nvwgf2umx`/`d3d12` 相关），也不是新加载的 DLL。
- 转储里搜不到 `physbone` / `boot.lua` / `jigphys` / `stack traceback` / `attempt to` 任何一种字符串
  ⇒ **没有任何直接证据指向我们的 Lua**。
- 这次是**用户主动退出**时产生的转储，属于**关停路径**，不是对局中途崩溃。

判读：目前**不能**把它归因于物理兼容层；但也不能完全排除"我们的 boot 补丁让关停路径走到别的分支"。
如果要彻底分离，最小代价的做法是**用 NOOP 探针复现一次同样的"进游戏→退出"**：
NOOP 也出转储 ⇒ 与我们无关（引擎自身的关停问题）；NOOP 不出 ⇒ 我们的 Lua 影响了关停，再查。

⚠️ 这条不与 §24.3/§24.4 的物理验证冲突：**退出时崩溃不阻塞物理验证**，可以并行。

### 24.9 测试机架已验证通过，卡点收窄到 `M.update` 内部（2026-10-31 末）

修掉 §24.7 的 `started` 之后，机架**全部工作**：

```
[drive] D applied=1 written=0 mode=jigphys zero=0          ← 驱动块在跑、mode 生效、written 可见
[jigphys] 驱动失效（零写入 45 帧，护栏=false）⇒ 重新就绪    ← 零写入检测与自动重挂都正常
[physbone] attach：可用链 1 条                              ← 重挂成功
```

⇒ **机架（就绪判定 / attach / 驱动循环 / 零写入检测 / 自动恢复 / 热调参）已经是可用状态。**

剩下的唯一问题：`M.update` 返回 **0**，且**没有** `[physbone][失败]` 那行。
`M.update` 里唯一"不写日志就返回 0"的路径只有开头这一句：

```lua
if session == nil or #session.sessions == 0 then return 0 end
```

而 `attach` 报告的是「可用链 1 条」。⇒ **交给 update 的会话里，`sessions` 是空的**。
下一步只需在 `M.update` 开头加一行诊断：

```lua
session.logger and session.logger("[physbone] update 入口：session=%s sessions=%s 首链节数=%s",
    tostring(session ~= nil), tostring(#(session.sessions or {})),
    tostring(session.sessions and session.sessions[1] and session.sessions[1].chain and #session.sessions[1].chain))
```

（需在 `attach` 里 `result.logger = logger`。）

### 24.10 一个必须澄清的历史事实

§13 里那些"乳房被驱动""双马尾弯 35°"的实拍，用的是**探针自己的直接写入路径**
（`mode=chain` / `mode=write`：probe.lua 直接 `Unit.set_local_pose`），
**从来没有经过 `PhysBone.update`**。

所以"我们明明已经做到有物理"与"兼容层的 update 今天才第一次真正被调用"这两件事**并不矛盾**：
能写姿态（已验证）与能算弹簧并批量写入（今天才发现 `M.update` 一直没被定义）是两条不同的路。

### 24.11 最后一处：`to_elements` 是列主序 3×4，我的安全检查按行主序取元素

验尸日志（一次性打点）直接把答案印出来了：

```
[physbone][insane] 元素个数=12 基四元数=0.0000,0.0000,0.0000,1.0000
[physbone][insane] 矩阵=1.0000 0.0000 0.0000 0.0000 1.0000 0.0000 0.0000 0.0000 1.0000 -0.0746 0.1910 0.0955
```

这个矩阵**完全合法**：前 9 个数是单位旋转，最后 3 个是平移。
`Matrix4x4.to_elements` 返回 **12 个数 = 列主序 3×4**：
`c0(3) c1(3) c2(3) t(3)`，即旋转连续占 `e[1..9]`，平移在 `e[10..12]`。

而 `pose_sane` 按"行主序"取 `e[1,2,3] / e[5,6,7] / e[9,10,11]` 当旋转，
把平移分量 `-0.0746 / 0.1910` 混进了行列式 ⇒ 算出 det = 0 ⇒ **好矩阵被判成坏矩阵**，
`written` 恒为 0、且只有 `insane` 一条线索。

修法：旋转直接取 `e[1..9]`（行列式对转置不变，列主序/行主序不影响 det）。已修并部署。

⚠️ `quat_from_12` 里也有同样的"行主序"假设，目前用句柄路径优先所以没暴露；
若将来切回纯数字路径（§24.4 第 5 条），**必须一并改**，否则单位阵以外的旋转会算错。

### 24.12 一条方法上的总结（这条最值钱）

这轮从"物理完全不工作"到"定位到一处索引错误"，靠的不是读代码，而是**分层打点**：

```
阶段（stage 0/1/2）→ 分支进入（DRIVE-ENTER）→ 步骤（A/B/C）→ 失败原因（false/insane）→ 数字验尸（元素+四元数）
```

每一层只回答一个问题，且**每一层都留下痕迹**。前面反复卡住，全部是因为某一层没有痕迹、
或者"用来留下痕迹的那行代码本身坏了"（pcall 吞错 / 一次性日志 / logger 为 nil）。

### 24.13 卡点收敛：这个引擎里"把两个旋转合成"做不到

三条路都试过了，全部失败在同一个位置——**把一个小的旋转加到基准姿态上**：

| 做法 | 结果 |
|---|---|
| 四元数相乘（`Quaternion.multiply(delta, baseQuat)`) | `combined=inf,-nan,-nan,-nan` |
| 矩阵相乘 `Matrix4x4.multiply(delta, base.pose)` | `is_valid=false` |
| 矩阵相乘另一种顺序 `Matrix4x4.multiply(base.pose, delta)` | `is_valid=false` |

而**原样写回**基准姿态是**成功的**（`release` 指令实测写了 3 根骨，头发没炸）。

⇒ 缺的能力非常具体：**"组合旋转"这一步在本引擎的 Lua 绑定里不可用**（至少对我们拿到的这些句柄类型不可用）。

### 24.14 下一步（三条都是小改，按优先级）

1. **从零构造，而不是组合**。验尸日志里那个基准姿态的旋转部分是**单位阵**
   （`1 0 0 / 0 1 0 / 0 0 1`，平移 `-0.0746 0.1910 0.0955`），
   所以根本不需要 base 的旋转：
   ```lua
   local q = S.Quaternion.axis_angle(axis, angle)          -- 已验证可用
   local pose = S.Matrix4x4.from_quaternion_position(q, S.Vector3(bx, by, bz))
   ```
   （yaw 与 pitch 需要两个轴时，先试 `axis_angle` 合成的四元数相乘是否可用 —— 注意
   §24.13 里失效的是"和引擎句柄相乘"，两个 `axis_angle` 之间未必失效。）
2. **直接改元素**：API 清单里有 `Matrix4x4.element / set_element / set_rotation / set_translation`，
   可以就地修改基准姿态的旋转分量，完全不做乘法。
3. **用引擎接口设置旋转**：`Unit.set_local_rotation(unit, node, quat)` 与 `local_rotation` 成对存在，
   把"算好的四元数"交给引擎，由引擎自己组姿态。

### 24.15 给接手者的一句话

到这里，**物理的每个零件都验证过了**：插骨、权重、建链、弹簧求解、写姿态（原样）、
每帧驱动循环、零写入自愈、热调参。**唯一缺的是"旋转合成"这一种操作**。
换一条不用合成的构造方式即可，不需要再动数据侧或转换侧的任何东西。

---

## 25. 2026-10-31 17:28 —— **物理跑通了（实机确认）**

```
[jigphys] live applied=4620 written=3 链=1 峰值=0.1° 各节yaw=[0.1,0.0,0.0]
[drive] D applied=4621 written=3 mode=jigphys zero=0
```

`written=3` 每帧稳定、`mode=jigphys`、`zero=0`、`applied` 持续增长 ⇒
**弹簧求解 → 姿态构造 → 写入引擎 这条链第一次完整跑通。**

（峰值只有 0.1~0.6°，是因为当时站着不动；走动/转身时才会出现几度到十几度的摆角。）

### 25.1 最后三块拼图（各一个具体原因）

| # | 问题 | 真因 | 修法 |
|---|---|---|---|
| 1 | `update` 是 nil | `M.attach` 少一个 `end`，`M.update` 整段被吞进 attach 体内 | 补 `end`（离线 `type(M.update)` 可验） |
| 2 | 驱动块每帧抛错但静默 | `state.luaAccum = … - started`，而 `started` 在改写时被删 | 删掉该行 |
| 3 | 写不进任何姿态 | ①`pose_sane` 按行主序取元素，而 `to_elements` 是**列主序 3×4**（平移混进行列式 ⇒ det=0）；②`Quaternion.multiply` 与引擎四元数句柄相乘得 `inf/NaN`；③`Matrix4x4.multiply` 两种顺序都产出非法矩阵 | **从零构造**：`axis_angle` → `from_quaternion_position(q, 基准平移)`（利用"我们插的节点基准旋转是单位阵"这一实测事实） |

### 25.2 现在可以做的事

**调手感**（改文件即生效，不必重启）：

```powershell
pwsh tmp/set-params.ps1 -MaxAccel 5      # 更飘；3 是走路量级
pwsh tmp/set-params.ps1 -MaxAccel 2      # 更稳
pwsh tmp/set-params.ps1 -Release 1       # 把头发交还引擎（对比用）
pwsh tmp/set-params.ps1 -Dump 1          # 打印状态
```

判据：静止完全不动 / 起步滞后 / 停下回弹一两下 / 转角时跟不上头。

### 25.3 已知欠帐（按优先）

1. **2 条链只挂上 1 条**（`链数 2` 但只有 `链 #1/2` 落日志）。现在环里已有逐链日志，重启即可看。
2. **只驱动 LOD0**：LOD1..4 头发权重仍全在 head，走远会跳回刚性（§20.8）。
3. **轴映射**：世界 Y→yaw、世界 Z→pitch、**丢掉世界 X**（§19.4）。正确做法是用 `Unit.world_rotation` 把加速度转到局部系。
4. **参数仍在 Lua 常量**（`MASS_BASE` / `maxAccel` / `chainBudget`），应从 `HD2PBX01` 读。
5. `quat_from_12` 里仍有"行主序"假设（现在走句柄/从零构造，未暴露；切数字路径前必须改，§24.11）。
6. 推广 57 根骨 + 收敛进 `HD2SourceMigration`。

### 25.4 环境

- 部署：`patch_13`（模型，插骨+权重）/ `patch_14`（探针，105744 字节），均不受管理器管辖。
- 日志：`%LOCALAPPDATA%\hd2pbx_probe.txt`
- 退出的转储（§24.8）：AV in `helldivers2.exe+0x64a62b`，与物理无关，可用 NOOP 补丁做 A/B 分离。

---

## 26. 交接总表（2026-10-31 17:47，压缩上下文前的最后状态）

### 26.1 一句话现状

**物理已跑通并肉眼可见**：双马尾其中一根在走动/转身时摆动，弹簧有阻尼、停下会回弹、静止不动。
**两根马尾只挂上一根**；改成"每侧各 attach 一次"会导致**加载场景崩溃**，已回退。

### 26.2 本轮验证过的实证

```
[jigphys] live applied=2220 written=3 链=1 峰值=3.4° 各节yaw=[3.4,3.4,3.4]
```

- `written=3` 每帧稳定 ⇒ 三节都在写
- **`各节yaw` 三节相等** ⇒ 链式累加成立（末端累计 ≈ 3 倍单节）。
  改动前是 `[8.4,7.1,4.7]`（根大末梢小）= "整条刚性绕根转"，这就是用户报的"只在根部摆动"。
- 早前一轮还实测到 `峰值=8.4° / -7.3°` 的正反摆动，说明弹簧+阻尼在工作。

### 26.3 让链式弯曲成立的三处常数（已生效）

| 项 | 改前 | 改后 | 依据 |
|---|---|---|---|
| 逐级衰减 | `accel × 0.85` 每级 | **去掉** | 源模型三节参数完全相同，累加来自层级 |
| 单节限位 | `angleLimit ÷ 链长`（11.7°） | **每节 35°** | 源模型三节都是 `angleLimit=0.611` |
| 段长 | 真实骨距 0.26 / 0.34 m | **虚拟杆长 0.5715 m** | 源模型 `length=30`（源单位），刻意拉长以变软 |

另：`chainBudget` 建议 ≥1.0 rad（现用 1.1），否则会再压一次幅度。

### 26.4 护栏已改为"相对锚点"

旧版用**绝对世界坐标**比较末端位置 ⇒ 人一走就把"角色位移"误判成"姿态崩坏"（实测 `偏离 bind 1.5 m` 误杀）。
现改为：

```
偏离 = |(末端 − head) 现在 − (末端 − head) 绑定时|
```

人走动时两者一起平移被抵消。阈值仍 1.5 m。

### 26.5 两根马尾：现状与下一步

- 现象：`链数 2` 但 `attach：可用链 1 条`，逐链日志**只有 `链 #1/2`**，第 2 轮迭代从未出现（即使已改成下标循环）。
- 已尝试：**每侧单独 rig + attach 一次再合并** → **加载场景时崩溃**，已回退（107088 字节版本）。
- 下一步二分（每次一次进出游戏）：
  1. 保留两侧独立 attach，但**先只 attach 左**（右不调用）→ 若崩，说明与 attach 次数无关
  2. 两侧独立 attach，但**分帧**（第 N 帧左、第 N+300 帧右）→ 若好，说明是"同帧两次扫描"的问题
  3. 回头查 `M.build_chains`：在 attach 里把 `chains` 的每一项 `chain[1].name` 与 `#chain` 打出来，
     确认第 2 项到底是什么（这是唯一还没直接观测过的东西）

### 26.6 完整移植 `jigglebones.cpp` 的障碍（重要，别重复踩）

Valve 的求解器全程用**矩阵合成**（`goalBase = 父骨动画矩阵 × 本骨偏移`）。
本项目实测：**`Matrix4x4.multiply` 对引擎句柄相乘产出非法矩阵**（`is_valid=false`），
**`Quaternion.multiply` 与引擎四元数句柄相乘得 `inf/NaN`**。所以不能逐字照搬。

可行路线：**方程与常数照搬，构造换成"从零构造"**——
```lua
q = S.Quaternion.axis_angle(axis, angle)                    -- 已验证
pose = S.Matrix4x4.from_quaternion_position(q, S.Vector3(bx, by, bz))
```
（利用我们插入的 6 个节点**基准 local 旋转是单位阵**这一实测事实，见 §24.11 的验尸矩阵。）

### 26.7 测试机架（已验证，改参数不必重启）

```powershell
pwsh tmp/set-params.ps1 -MaxAccel 9 -ChainBudget 1.1 -Smoothing 0.15   # 调幅度
pwsh tmp/set-params.ps1 -Release 1                                     # 交还引擎（对比基线）
pwsh tmp/set-params.ps1 -Dump 1                                        # 打印状态
pwsh tmp/set-params.ps1 -stage 0/1/2                                   # 分阶段：只判定 / 只 attach / 完整驱动
```

日志：`%LOCALAPPDATA%\hd2pbx_probe.txt`
部署：`patch_13`（模型，插 6 节点 + 权重）/ `patch_14`（探针，107088 字节）
构建：`python tmp/lua-boot/build_probe.py --patch-index 14` 然后复制三个后缀到游戏 data。

### 26.8 改动纪律（这轮用血换来的，务必遵守）

1. **改完模块先跑离线三项自检**：`type(M.attach) / type(M.update) / type(M.release)` 都必须是 `function`
   —— 这能挡住"少一个 `end` 导致整段函数被吞进别的函数体"（踩过）。
2. **改写一段代码后，检查被删掉的局部变量是否仍被引用**（`luajit -bl` 查不出，运行期才炸；踩过 `started`）。
3. **不要试探性调用引擎构造器**：`S.Quaternion(0,0,0,1)` 直接打死进程（踩过）。
4. **诊断型批量扫描必须等场景稳定**：`census()` 遍历 2291 个 unit 曾在加载期崩溃（踩过，已停用）。
5. **排查期不要用"只打一次"的日志**：一次性的 `*Logged` 标志会掩盖后续同类错误（踩过三次）。
6. **日志函数要在作用域内存在**：`M.update` 里没有 `logger`，用它写失败日志会让错误自己吞掉自己（踩过）。

### 26.9 欠帐清单（优先级）

1. 第二根马尾（§26.5）
2. 只驱动 LOD0，LOD1..4 头发仍在 head 上，走远会跳回刚性
3. 轴映射：世界 Y→yaw、世界 Z→pitch，**丢掉世界 X**；应改为把加速度投影到节点局部系
4. 参数仍在 Lua 常量，应从 `HD2PBX01` 容器读
5. `quat_from_12` 仍有"行主序"假设（现在未走到该路径，切数字路径前必须改，§24.11）
6. 完整求解器移植（摩擦/回弹/沿轴约束/base spring，§26.6）
7. 推广到 57 根骨 + 收敛进 `HD2SourceMigration`
