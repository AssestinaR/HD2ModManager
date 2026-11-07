# HD2 护甲/素体动态骨（物理）能力调研

> 状态：**调研记录（未定案）**。写于 2026-10-11，供后续需求澄清与方案选型使用。
> 本文只记录"实测/可溯源"的结论，并显式标注置信度与证据出处；
> 凡是推断，一律标 `inference`，不得当成事实使用。

---

## 0. 本轮要回答的问题

用户目标（原话要点）：

1. 现状：patch 类 mod 基本是替换护甲模型/材质资源；我们的映射功能也只是"自定义一个虚拟素体"，
   本质上还是游戏自己支持的模型。**游戏本体的模型基本没有动态骨骼**，导致二次元美少女的头发、
   裙子、大袖子像钢板。
2. 已有的解法：某个 mod（用户给的是 `HD2-Integrated-Armor-System-1.3.3`）提供"外置物理骨架支持"，
   但**非常消耗性能**；用户猜测它是"另起一套物理渲染系统，游戏算完一帧还要再拿给它算一次"。
3. 用户的诉求：**不是优化它、也不是重写它**，而是为我们的映射/自定义素体功能加强，
   让它可以**借用一部分游戏本体就有的物理计算**（披风物理、角色/敌人尸体布娃娃物理等）。
4. 用户的观察：该 mod 装几个角色就卡；多个玩家用同一套护甲时，一个人动会让所有人同步变化；
   但敌人尸体布娃娃上百具堆叠互撞不卡，玩家披风很飘逸 → 推断"本体有更优的物理"。
5. **第一阶段要求很低**：让护甲中"没怎么绑定骨骼/权重较低的区域"有一种**弹簧的、很 Q 弹**的效果，
   随玩家移动或触碰实体会有抖动。例如及腰长发像纸片一样小幅晃动。
6. 本轮任务：**先搜集信息，再与用户沟通确认真实需求**。

---

## 1. 结论摘要（先看这个）

| # | 结论 | 置信度 | 证据 |
|---|---|---|---|
| C1 | 游戏引擎是 **Autodesk Stingray（原 Bitsquid）**，它向 Lua 暴露了完整场景图（`stingray.Unit` / `World` / `Quaternion` …） | `format-confirmed` | §2.1 |
| C2 | `HD2IAS` 确实是"**另起一套物理**"：Lua 每帧从场景图读姿态 → 送原生插件解算 → 再把结果写回场景图并调用 `World.update_unit()`。用户的猜测**方向正确** | `format-confirmed` | §2.1 |
| C3 | `HD2IAS` 的卡顿来自"**每帧 Lua↔C++ 往返 + 逐骨写回 + 让引擎重算世界变换**"，不是它的 solver 本身 | `inference`（机制已确认，成本占比未量化） | §2.1 |
| C4 | `HD2IAS` **刻意不碰**游戏本体布娃娃：其原生插件里有明确字符串 `Only custom Mod physics; native ragdolls are unchanged.`，并且它反而**冻结**自己那套物理在尸体上的运行 | `format-confirmed` | §2.2 |
| C5 | 多玩家"传染"来自 `HD2IAS` 的 **shared body（共享素体）** 设计：多个 avatar 共用同一个 solver 实例 | `format-confirmed`（有 `shared_body_profile` / `shared_body_solves` / `shared consumer` 等 API 与校验串） | §2.3 |
| C6 | **游戏本体确实有一套引擎原生"抖动骨"系统**，但它是**固定 6 根装备挂点骨**，不是通用物理 | `format-confirmed` | §3.2 |
| C7 | 那 6 根骨头是：`support_mg`、`support`（父 Chest）、`attach_intelpad`、`attach_samplepouch`、`target_designator`（父 Spine1）、`sling`（父 Spine2） | `format-confirmed` | §3.2 |
| C8 | 官方 SDK **不支持新增自定义抖动骨**；模改社区的标准做法是**把顶点权重重画到上面那 6 根骨头**上 | `format-confirmed` | §3.2 |
| C9 | 游戏本体的**尸体布娃娃是引擎原生刚体系统**，且**是数据驱动的**：定义在动画状态机资源里（`RagdollItem { bone_index, params[9], … }`） | `format-confirmed` | §3.3 |
| C10 | 布娃娃是**全身**的，只在被击倒/死亡时激活；Lua 可以**触发**它（`can_ragdoll` → `start_ragdoll`），但不能"只让几根骨头布娃娃" | `format-confirmed`（触发）/ `inference`（不可局部化，基于机制推断） | §3.3 |
| C11 | "上百具尸体堆叠不卡"**不能**推出"引擎能同时模拟上百个活动刚体"：堆叠尸体是**已休眠（sleeping）**的，休眠刚体几乎零成本 | `inference`（引擎通用行为；未在 HD2 内实测计数） | §3.4 |
| C12 | 披风**没有**出现在护甲骨架里（骨架无披风骨）；`HD2IAS` 把披风当作一个独立的 "cape carrier / appearance group" 处理 → 披风更像**独立 Unit + 动画/挂点**，而非可借用的通用布料系统 | `format-confirmed`（无披风骨 + HD2IAS 的 carrier 处理）/ `inference`（其运动来源） | §3.5 |
| C13 | **我们自己已经具备"给 Unit 加骨 + 重写蒙皮"的能力**，而且是**在生产链路里跑通并测过的**：`CanonicalAppendBoneCompiler`、`CanonicalMeshSkinningRouter`、`CanonicalInverseJointMatrixCompiler`、`UnitMeshWriter.SerializeBoneInfo` | `project-proven` | §4.2 |
| C14 | 我们已经有 `CanonicalAvatarRigReader`，读的就是**modding wiki 让作者导入的那同一个 avatar 归档/Unit**（`18235e0c9ec0e636` / `5556372446766824087`）→ 我们天然拿得到全部全局骨名与层级 | `project-proven` | §4.2 |
| C15 | 我们现在的"自定义素体"是 **Unit 级逐字节复用 + 重新编号**，**不改骨骼、不改权重** → 想加物理，必须新增"改权重/加骨"的步骤 | `project-proven` | §4.1 |

**一句话**：用户"借用本体物理"的直觉**部分成立但落点要改**——
本体的**布娃娃借不了**（全身、只在倒地时激活），本体的**披风没有可借的通用物理**，
但本体**确实有一套原生的"抖动骨"二级运动系统**（6 根装备挂点骨），
这才是唯一现成的、可借的、且社区已在用的东西；而它的限制是**不能新增骨头**。
要突破"只有 6 根、且动作模式绑定装备语义"的限制，只能走"给 Unit 加骨 + 自己解算"的路，
**但那条路不必像 HD2IAS 那样卡**——见 §5 路线 B。

---

## 2. 现有两个 mod 的真实实现

### 2.1 HD2IAS（HD2-Integrated-Armor-System 1.3.3）

**产物结构**（解开用户给的嵌套 zip 得到）：

```
HD2-Integrated-Armor-System-1.3.3/
  feed.json                       签名更新清单（payload 是 base64，内含每次发布的文件哈希）
  client-config.json              RSA 公钥 + 更新地址
  updates/1.0.4 .. 1.3.3/*.zip    历次增量包
    RuntimePackage/
      HD2IndependentRig.addon64        2.25 MB 原生插件（C++20 /O2 /GL /WX）
      HD2IndependentRig.build.json     能力清单
      package.json                     运行时文件与哈希清单
      runtime.lua                      518 KB，合并进 boot.lua 的 Lua 主体
      independent_loader.lua           3 KB，Bingus Shared Loader v15 入口
      dxgi.dll                         5.5 MB，注入用代理 DLL
    TransmogSupport/*.patch_1          43 MB + 1.09 GB gpu_resources + 660 MB stream
    app/HD2综合护甲系统.{dll,exe,pri}   WPF 客户端
```

**每帧数据流**（`runtime.lua` 实证）：

```text
_G.update / _G.render 被 hook
  → Lua 用 stingray.Unit 读每个 avatar 各骨的 parent-relative local_pose
  → C.push_avatar(unit_id, root, 骨数, 骨数组)          ← 原生插件
  → C.begin_frame / C.end_frame                          ← 原生 solver 解算
  → C.retarget_bound_unit_pose(...) 返回修正后的姿态
  → Lua 用 Unit.set_local_position / set_local_rotation 逐骨写回
  → World.update_unit()                                   ← 让引擎重算世界变换
```

**证据**：
- FFI 声明 `hd2ir_scene_input_v1 / scene_output_v1 / status_v1`；
- API 表含 `begin_frame / push_avatar / bind_unit / end_frame / retarget_bound_unit`；
- 计数统计：`W.update_unit` ×12、`U.set_local_position` ×10、`U.set_local_rotation` ×6、
  `U.local_pose` ×2、`U.scene_graph_parent` ×6、`U.num_scene_graph_items` ×23；
- 文件头注释自述：*"multi-avatar pose feed and guarded Unit driver"*、
  *"reads each configured armour Unit's parent-relative local pose, writes the exact same values back
  to every weighted node, then calls `World.update_unit()`"*。

**自建物理格式 `HD2PHY1`**（原生插件字符串）：

- 能力结构体字段：`max_chains / max_joints / max_colliders / max_chain_links / max_instances /
  max_contacts / max_link_collisions / max_candidates`；
  实测上限：`max_link_collisions = 8192`、`max_candidates = 65536`、单次 256 骨；
- 概念：chain（骨链）、joint（关节）、collider（碰撞体：capsule 单端/双端、root collider）、
  link collision（链间碰撞）、angle limit、joint collision filter、collision group；
- 校验串：`physics global constraints did not converge`、`physics partial constraints did not converge`
  → 是**迭代式约束求解器**；
- 每套护甲需要作者产出的 `.rigbin`（`HD2PHY1` / `.requires-clothing-v1.rigbin`）。

**性能相关的自我暴露**：
- `physics_control.ini` 选项：`Player physics` / `Teammate physics` / `Corpse physics` /
  `Disable all custom physics` / `Freeze corpse custom physics`；
- 1.1.10 更新日志：*"修复了多个展示角色互相阻断"*、*"添加了 Lua 查询复用和 GC 参数调整"*；
- 1.1.6：*"修复了 DX12 空载时多余的预览绘制回调"*、*"修复了预览停用时多余的姿态采集"*；
- 1.2.2：*"修复了回调内持枪数据缓存"*、*"修复了展示角色物理历史路由切换"*；
- 1.3.3：*"优化了游戏显存占用"*。

→ 这些都是**"回调里做的事情太多"**的典型症状，与"每帧 Lua↔C++ 往返 + 逐骨写回"的判断一致。

### 2.2 HD2IAS 明确不碰本体布娃娃

原生插件字符串（原文）：

```
Only custom Mod physics; native ragdolls are unchanged. Settings save automatically.
Freeze corpse custom physics
Disable all custom physics
Only affects custom Mod physics on corpses
```

配合 Lua 侧的 `corpse-copy-v4`、`__hd2ir_corpse_freeze`、`hd2ir_freeze_corpse_physics`
（对每具尸体调用以**退役/冻结它自己那套物理**），可得：

> **HD2IAS 的作者知道尸体上的物理很贵，所以自己主动把它关掉了。**
> 这既证明"本体布娃娃 ≠ 它那套物理"，也侧面证明"它那套物理在尸体规模下确实撑不住"。

### 2.3 多玩家"传染"的机制

原生插件暴露：

```
hd2ir_shared_next_incarnation        / hd2ir_set_avatar_incarnation / hd2ir_forget_shared_avatar
hd2ir_shared_body_profile(unit)      → 该 unit 是否走"共享素体"
hd2ir_shared_body_solves()           → 共享解算次数
```

Lua 侧有 `__hd2ir_shared_body_lifecycle`、`shared_body(profile, …)`、
以及校验串 `shared consumer also claims per-Unit physics`、`shared projects use incompatible source skeletons`、
`non-rigid disabled shared clothing output`。

`HD2IAS` 的 1.3.0 更新日志也写了：*"添加了队友护甲系统与多人使用提醒"*、*"添加了队友护甲与幻化共用资源"*。

> **结论**：当多个玩家穿同一套护甲时，客户端把它们归到**同一个"共享素体 profile"**、
> 用**同一个 solver 实例**解算 → 一个人动，共享的物理状态就变，其他人跟着变。
> 这不是 bug，是"为了省性能而共用"的代价。**我们如果做，必须避免这个设计。**

---

## 3. 游戏本体到底有什么物理

### 3.1 引擎

`Autodesk Stingray`（原 Bitsquid）——`format-confirmed`：
- `runtime.lua` 全程使用 `stingray.Application / World / Unit / Quaternion / Vector3 / Matrix4x4 /
  IdString64 / Network / GameSession`；
- 本仓 `docs/HD2SDK-CommunityEdition-3.8.0/stingray/` 就是该引擎的资源解析器
  （`unit.py` / `state_machine.py` / `animation.py` / `bones.py` / `composite_unit.py`）。

### 3.2 引擎原生"抖动骨"（**这是唯一现成可借的东西**）

来源：HD2 Modding Wiki《Jiggle Bones》（`docs/dev/armor modding/jiggle-bones.md`）。

原文关键句：

> *"Currently, the SDK does not support adding custom jiggle bones to models.
> However, we can decently fake the effect using existing bones on the Helldiver player rig."*

可用的抖动骨（原文表格，含父骨与"手感"评价）：

| 骨名 | 父骨 | 社区评价 |
|---|---|---|
| `support_mg` | Chest | Works well overall |
| `support` | Chest | Works well overall |
| `attach_intelpad` | Spine1 | Works well with lower body |
| `attach_samplepouch` | Spine1 | Works well with lower body |
| `target_designator` | Spine1 | Only moves horizontally |
| `sling` | Spine2 | Moves irratically |

社区用法（原文流程）：把自定义模型的顶点**重新加权**到这些骨上，
并且**不能 100% 权重**——要和一个"锚点骨"（`chest` / `spine1` / `head` 等）混合，
抖动骨权重低一些（原文用 `VertexWeightMix` 调整）。

**这几根骨头是什么**：从名字看都是**装备挂点**
（支援武器挂点、情报板、样本袋、目标指示器、背带）。
它们的运动来源是引擎的**挂点二级运动**——`inference`：
按名字与社区描述的"各不相同、会乱动"，更像**惯性/弹簧驱动的程序化二级运动**，
而不是纯烘焙动画；但**我没有找到参数配置的位置**，这一点**未证实**（见 §6 未知项 U1）。

**对我们的意义**：
- ✅ 零额外性能开销（引擎本来就在算）；
- ✅ 不用写 solver、不用写原生插件、不用 hook；
- ✅ 只要**改权重**，而我们已经能改权重（§4.2）；
- ❌ 只有 6 根，且**动作模式固定、绑定装备语义**（例如 `sling` 乱动、`target_designator` 只水平动），
  拿来当"及腰长发"未必好看，也**不能调参数**；
- ❌ 官方 SDK 不支持新增抖动骨（工具层限制还是引擎层限制，**未证实**，见 §6 U2）。

### 3.3 游戏本体布娃娃（**借不了**）

来源：本仓 `docs/HD2SDK-CommunityEdition-3.8.0/stingray/state_machine.py`。

动画状态机资源里有 `RagdollItem`：

```python
class RagdollItem:
    def __init__(self):
        self.bone_index = 0
        self.params = [0] * 9      # 9 个 float32 参数
        self.unk_hash = 0
        self.unk_enum = 2
        self.unk = 0
```

并且 `StingrayStateMachine` 提供 `set_ragdoll(bone_index, params)` / `remove_ragdoll(bone_index)`
→ **布娃娃是"每根骨头一条记录"的数据，写在状态机资源里**，是引擎原生刚体系统。

Lua 侧可**触发**（来源：`Alomare/RagdollOnCommand`）：

> *"On a key press the mod runs the game's own knockdown sequence on the local Helldiver:
> `is_ragdolled`, then `can_ragdoll` (the game's own check of whether a ragdoll is allowed right now),
> then `start_ragdoll`. The game's per-frame ragdoll update ends it."*

并且强调：*"It never writes game memory itself; the game's own functions do."*
（这些原生函数是**签名扫描** game.dll 得到的，不是公开 API。）

**为什么借不了**：
- 它是**全身**倒地，不是"只让头发布娃娃"；
- 只在 `can_ragdoll` 为真时可用（被击倒/死亡），**站立行走时不可用**；
- 把护甲骨的 `RagdollItem` 塞进状态机 → 理论上会让它在倒地时变成刚体，
  但站立时**依然什么都不动**，与"走路时头发飘"完全不是一个需求。
  （此推断标 `inference`；§6 U3 列为可选验证。）

### 3.4 为什么"上百具尸体不卡"

**这是一个需要纠正的直觉。** 引擎（PhysX 系）的标准行为是：
刚体静止一段时间后进入 **sleeping（休眠）** 状态，休眠刚体不参与积分、不参与窄相碰撞检测，
成本接近 0。上百具尸体堆在一起**已经静止**，所以不卡。
一旦它们被爆炸掀飞、同时处于**活动**状态，成本立刻上升。

→ 所以"尸体堆叠不卡"**不能**作为"引擎能同时跑上百个活动刚体"的证据。
（标 `inference`：这是引擎通用行为，我**没有**在 HD2 里实测活动刚体数量上限。）

### 3.5 披风

- 护甲骨架（`HD2PhysBone` 参考源码里的 `kit/ref/hd2_skel.json`，共 ~110 个条目）**没有披风骨**：
  只有 `root/boss/spine1/spine2/chest/r_clavicle/r_shoulder/r_elbow/r_hand/hips/l_thigh/…/head`
  以及一批 `*_culling` 骨（`r_arm_culling`、`torso_culling`、`hips_culling`、`helmet_culling`、`l_leg_culling`）
  和 `grp_*` / `g_*` 分组。
- `HD2IAS` 把披风当作独立的 **cape carrier / appearance group** 处理
  （字符串：`body group has no room for the equipped cape carrier`、
  `cape preservation requires matching unique body groups`、`removing a foreign cape would empty the body group`）
  → 披风是**独立的挂载单元**。
- Modding Wiki 有独立的《Cape Modding》章节，披风有自己的归档与素材表
  → 披风是**一等资源**，不是护甲的一部分。

**结论**：披风的飘逸**很可能来自它自己的骨架 + 动画/挂点**，
不是一个"可以被护甲借用的通用布料求解器"。（标 `inference`；§6 U4。）

---

## 4. 我们自己的现有能力（决定"加强"落在哪）

### 4.1 现状：自定义素体 = 逐字节复用，不改骨骼与权重

`docs/custom-model-conversion-20260925.md` + `docs/custom-model-cross-armor-targets.md`：

- "一键转为自定义素体" = 取来源 Archive 的**可见 Unit**，用既有 Canonical direct-source-reuse 写包链路
  **重新编号**，输出新 Patch；**保留来源 TOC/GPU/stream 和依赖**；**不进行跨 Unit 几何合并**；
- 验证记录：*"真实 Lunalice 护甲来源生成 8 个新 Unit；TOC/GPU/Stream 载荷与来源逐字节一致"*；
- 之后靠运行时 Lua 协议把它映射到真实护甲上。

→ **所以现在的素体是"换 ID 的整包复用"，没有任何几何/权重/骨骼级加工。**
想加物理，必须新增一个"改权重（必要时加骨）"的步骤。

### 4.2 关键：**加骨与重写蒙皮的能力我们已经有了**

`HD2ModAdaptation/PatchReconstruction/UnitMesh/Canonical/` 下已有完整链路：

| 类型 | 作用 |
|---|---|
| `CanonicalAvatarRigReader` | 读**权威 player Avatar 的 TransformInfo**；常量就是 `AvatarArchiveName = "18235e0c9ec0e636"`、`AvatarUnitFileId = 5556372446766824087` —— **与 modding wiki 让作者导入的完全是同一个** |
| `CanonicalAppendBoneCompiler` | **追加几何后重建该 LOD 的共享骨调色板**（`CanonicalAppendBoneResult(Mesh, BoneInfo, Diagnostics)`）；已在生产链路中被 `CanonicalDecorationUnitAppender` 调用 |
| `CanonicalBoneRebuilder` / `CanonicalLodBonePaletteCompiler` | 重建 BoneInfo / 逐 LOD 骨调色板 |
| `CanonicalInverseJointMatrixCompiler` | 重算 inverse bind |
| `CanonicalMeshSkinningRouter` | 蒙皮路由（把顶点分到最终 section 的 palette 上） |
| `UnitMeshWriter.SerializeBoneInfo` | 序列化回写 BoneInfo |
| `UnitMeshModel` / `UnitBoneInfo` | 网格与骨信息的内存模型 |

并且**已有测试**：`HD2ModAdaptation.Tests/CanonicalAppendBoneCompilerTests.cs`。

→ **"把顶点权重重画到某根全局骨上"这件事，在我们的能力范围内。**
  唯一还没做的是"**写回时把目标骨的全局 hash 换成我们想用的那根**"这一层业务逻辑。

### 4.3 已有的"装饰"（Decoration）功能

`HD2ModCore/Domain` 里有 `ModNodeKind.Decoration`；`DecorationHostRebuildService` 会把装饰
作为 Overlay 重建到宿主护甲上（带 `.hd2-decoration-overwrite` 标记防止误删用户目录）。
`CanonicalDecorationUnitAppender` 正是调用 `CanonicalAppendBoneCompiler` 的地方。

→ 这是**离"往护甲上追加带权重的几何"最近的既有功能**，可作为实现落点的参照。

---

## 5. 三条候选路线

### 路线 A：借用本体抖动骨（改权重，不加骨）

```text
素体的某个 Unit
  → 读它的 BoneInfo / 局部调色板
  → 选定一根本体抖动骨（如 support_mg）
  → 把目标区域顶点的权重按比例重画到该骨（与锚点骨混合）
  → 重建 BoneInfo / inverse bind / 序列化回写
```

- **代价**：小。我们已有全部构件；不需要原生插件、不需要 hook、不需要 Lua 每帧往返。
- **性能**：**零额外开销**（引擎本来就在算这 6 根骨）。
- **效果**：有二级运动、会随移动晃动，但**不能调参**，动作模式由本体决定
  （`sling` 乱动、`target_designator` 只水平动），**不适合精细的头发/裙子**。
- **风险**：那 6 根骨头上原本挂着**原版装备**（支援武器、情报板…）。
  把我们的头发绑上去，会不会与装备的运动耦合/打架？**未知**（§6 U5）。
- **适用**：第一阶段"先看到会动"的最小验证；或作为兜底方案。

### 路线 B：给 Unit 加骨 + 自己解算，但写在**骨调色板**层（不碰 Lua 场景图）

这就是 `HD2PhysBone`（ccd775）的路线，**与 HD2IAS 的关键差别**：

| | HD2IAS | HD2PhysBone |
|---|---|---|
| 介入层 | **Lua 场景图**：每帧读姿态、写姿态、`World.update_unit()` | **GPU 骨调色板**：直接改上传堆里的骨骼矩阵 |
| 每帧成本 | Lua↔C++ 往返 × 骨数 × avatar 数，且让引擎重算世界变换 | 只改一段内存（CPU 可见的上传堆） |
| 需要的组件 | Lua 运行时 + 原生 solver + 注入 DLL | ReShade addon（`HD2PhysBoneProbe.addon64`）+ 内存定位（"needle"字节特征） |
| 尸体处理 | 主动冻结自己的物理 | 未观察到同类需求 |

`HD2PhysBone` 的 solver 是 **VRM SpringBone（UniVRM）的移植**，参数体系（源码实证）：

```cpp
struct chain {
  double stiffness, drag, gravity_power;
  vec3   gravity_dir;
  std::vector<double> j_stiff, j_drag, j_grav, j_hit, j_limit_angle, ...;
  std::vector<uint32_t> j_limit;      // 角限类型
  std::vector<uint32_t> groups;       // 参与碰撞的 collider 组
};
struct collider { collider_kind kind; ... };   // sphere / capsule / plane
```

→ **正是用户要的"弹簧、很 Q 弹"的模型**：`stiffness`（刚度）+ `drag`（阻尼）+ `gravity`（重力）。

- **代价**：大。需要原生插件（ReShade addon）+ 内存定位 + 资产管线（加骨、权重转移、rig 导出）。
- **性能**：预期远好于 HD2IAS（不碰 Lua、不让引擎重算）。
- **效果**：可调参、可碰撞、可角限——能做到"头发像纸片"。
- **风险**：内存定位（needle/特征码）随游戏版本变化；原生插件维护成本高。

### 路线 C：只用本体已有的骨头 + 我们自己的**轻量**弹簧（折中）

不做原生插件、不碰 Lua 场景图，但也不满足于 6 根固定骨：
在**已加骨**的前提下，用**游戏自身的更新回调**（Lua `update`）做一个**极简弹簧**，
只写少量骨、且只写 `set_local_rotation`（不改位置、不调 `update_unit` 全量重算）。

- 需要先验证：**只改旋转、不调 `update_unit`** 时，引擎是否会用新姿态绘制（§6 U6）。
  如果不调 `update_unit` 也能生效，成本会大幅低于 HD2IAS。

---

## 6. 未知项（**必须在写方案前验证**）

| # | 未知项 | 为什么重要 | 怎么验证 |
|---|---|---|---|
| U1 | 那 6 根抖动骨的运动是**物理/惯性驱动**还是**烘焙动画**？ | 决定路线 A 是不是"真物理" | 静止站立时观察是否仍在微动；蹲下/抬头时看装备是否独立跟随 |
| U2 | "不能新增抖动骨"是**工具限制**还是**引擎限制**？ | 决定路线 A 的天花板 | 找抖动骨的参数配置位置（可能在 avatar unit 或状态机）；或看社区是否有 hack |
| U3 | 把护甲骨的 `RagdollItem` 写进状态机，倒地时会不会真的布娃娃？ | 决定"借用布娃娃"是否完全无望 | 单点实验（高风险，需备份） |
| U4 | 披风的运动来源（独立骨架动画？挂点？布料？） | 决定"借披风"是否完全无望 | 拆披风 Unit 看骨数与骨骼名；对比走路/急停的形变 |
| U5 | 我们的头发绑到 `support_mg` 等骨上，会不会与**原版装备**耦合/打架？ | 路线 A 的可行性 | 静态实验：装备支援武器时观察 `support_mg` 的运动 |
| U6 | 只写 `set_local_rotation`、**不调 `World.update_unit()`** 时，画面会不会更新？ | 决定路线 C 的成本 | 单点 Lua 实验 |
| U7 | 加骨后，**每顶点 4 influences** 的上限会不会被顶爆？ | 决定改权重的自由度 | 读实际 Unit 的 stream 格式；现有 `CanonicalAppendBoneCompiler` 已处理 palette 重建，需确认 influence 上限 |

---

## 7. 与用户表述的差异（需要澄清的地方）

| 用户的表述 | 调研结论 |
|---|---|
| "游戏本体的模型支持基本上没有动态骨骼" | **基本正确**（护甲骨架确实没有动态骨），但**本体有 6 根原生抖动骨**可用 |
| "HD2IAS 相当于另起一套物理，游戏算完一帧还要再拿给它算一次" | **方向正确**（Lua 场景图往返 + `update_unit`），但准确的成本点是"每帧往返 + 逐骨写回 + 引擎重算"，而非"渲染" |
| "可以让它借用游戏本体的披风物理" | 披风**没有**可借的通用物理（无披风骨、是独立挂载单元） |
| "借用角色或敌人尸体的布娃娃物理" | 布娃娃是**全身**的，**只在倒地时**激活，**借不了** |
| "上百具敌人尸体堆叠互撞都不卡" | 那是**休眠刚体**，成本接近 0；**不能**推出"引擎能跑上百个活动刚体" |
| "第一阶段：没怎么绑定骨骼/权重较低的区域有弹簧 Q 弹效果" | **完全可行**，且我们已有加骨+改权重的构件（§4.2） |

---

## 8. 待确认的需求问题（见对话）

见与用户的对话；本文只列问题清单：

1. 效果标准：是"看得出会动"就够，还是必须**能调参**（刚度/阻尼/重力/摆幅）？
2. 是否要**碰撞**（头发与身体/地面/其他物体）？
3. 性能与效果的优先级：能否接受"先用本体 6 根抖动骨验证，效果一般但零成本"？
4. 是否需要"不改模型、直接对现有素体生效"，还是允许要求模型侧配合（重新画权重）？
5. 是否要求**每个玩家独立**（避免 HD2IAS 的共享素体传染）？
6. 作用范围：只覆盖我们自己的素体，还是任意 mod 的护甲模型？

---

## 9. 证据出处清单

**本地**（`E:\Data\source\repos\WpfApp1`）：
- `tmp/hd2ias/extracted/RuntimePackage/runtime.lua`（从用户给的嵌套 zip 解出，518 KB）
- `tmp/hd2ias/extracted/RuntimePackage/HD2IndependentRig.addon64`（2.25 MB，字符串提取）
- `tmp/hd2ias/extracted/RuntimePackage/HD2IndependentRig.build.json` / `package.json`
- `tmp/pb-ref/HD2PhysBone-ReferenceSource-main/`（用户下载目录里的参考源码）
  - `runtime/cpp/excerpts/spring_runtime.reference.hpp`（75 KB，solver）
  - `runtime/lua_patch/physbone_reference.lua`（Lua 参考框架）
  - `kit/ref/hd2_skel.json`（护甲骨架）
- `docs/HD2SDK-CommunityEdition-3.8.0/stingray/state_machine.py`（`RagdollItem`）
- `docs/hd2physbone-embedded-patch-protocol.md`（我们与 PhysBone 生态已有的协议）
- `docs/custom-model-conversion-20260925.md` / `docs/custom-model-cross-armor-targets.md`（素体现状）
- `HD2ModAdaptation/PatchReconstruction/UnitMesh/Canonical/*`（加骨与蒙皮能力）

**线上**：
- HD2 Modding Wiki《Jiggle Bones》《Armor Anatomy》《Cape Modding》《Rig Editing》
  （`boxofbiscuits97.github.io/HD2-Modding-Wiki`）
- `github.com/ccd775/GameModdingKnowledge/games/helldivers-2/`
  （`02_HD2_Resource_Contracts.md`、`03_Rigging_Weights_Deformation.md`）
- `github.com/Alomare/RagdollOnCommand`（Lua 触发本体布娃娃）
- `github.com/Estecsky/HD2-PhysBoneTool`（HD2IAS 作者的 Blender 物理骨插件）
- `github.com/Estecsky/HD2-Integrated-Armor-System`（HD2IAS 更新仓）

---

## 10. 权威取证（第二轮：用解密器直读游戏本体 + 我们自己的 Lua 环境探测）

> 第一轮（§1–§9）里有相当一部分是从 wiki 与第三方仓库**推断**出来的。本节全部换成
> **两类一手证据**：① 游戏本体自带的加密资源（用本仓 `Decryptor` 解出）；② 我们自己写进游戏的
> Lua 环境探测日志。凡与 §1–§9 冲突处，以本节为准。

### 10.1 引擎 Lua API 的完整面貌（一手）

- **上游引擎 = Autodesk Stingray（原 Bitsquid）**，官方完整 Lua API 参考公开可查：
  `help.autodesk.com/cloudhelp/ENU/Stingray-Help/lua_ref/functions.html` —— 共 **3098 条**函数签名。
  我们已把清单与签名抓下来（`tmp/stingray-api/sigs.json`）。
- 上游有、而 **HD2 里被整个裁掉**的命名空间（由我们自己的游戏内探测坐实，不是推断）：
  `PhysicsWorld`（上游 19 个函数，含 `create_joint` / `spawn_box|sphere|plane`）、
  `Actor`(43)、`Joint`(41)、`Mover`(16)、`Cloth`(3)、`HumanIK`(235)、`Vehicle`(46)、`Raycast`、`NavigationMesh`。
  → **HD2 的 Lua 里不存在任何物理世界、刚体、关节、布料或 IK 接口。**
  `Unit.create_actor` / `create_custom_joint` / `create_cloth` / `destroy_cloth` / `find_cloth` 同样不存在。
- `stingray` 在 HD2 里暴露 **130 个直接子成员**，其中与本次相关的有：
  `World`(72 成员)、`Unit`(119 成员)、`Mesh`、`Material`、`Level`、`ProceduralMesh`、`Broadphase`、
  `UnitSynchronizer`、`AnimationStateMachineComponent`、`MeshComponent`、`UnitComponent`、
  `SceneGraphComponent`、`TransformComponent`、`EntityManager`、`Prefab`、`Sensor`。
- **`Mesh` 完全没有骨骼/蒙皮接口** → **Lua 碰不到顶点权重**。这是本次最硬的一条约束：
  §5 路线 A（改权重重画到现有抖动骨）与路线 B/C（加骨）**都无法用纯 Lua 完成**。
- **HD2 自己新增了上游没有的抖动骨 API**（均为 C 函数，上游 Stingray 不存在）：
  `Unit.update_jiggle_bone_data`、`Unit.update_jiggle_bone_orientation`、
  `Unit.debug_draw_jiggle_bone`、`Unit.debug_draw_all_jiggle_bones`、`Unit.debug_draw_all_constraint_bones`。
  这是**唯一现成的"抖动骨"运行时接口**。
- 可用的运行时能力（已确认）：全局 `update(dt)` 每帧钩子（可 wrap）、`stingray.IdString64.from_hex/to_hex`、
  `stingray.Application.can_get('unit', id)`、`Application.make_hash`、`Application.set_resource_override`。
- 游戏自带的 Lua 资产（类型哈希 `a14e8dfa2cd117e2`）全库只有 **12 个**，且**没有任何一个使用 jiggle**。
  → 抖动是由**原生 C++** 驱动的，Lua 只是被暴露了接口。

### 10.2 玩家骨架与抖动骨：6 根，位置与名字全部对上（一手）

用 `Decryptor` 直读游戏包得到：

| 事实 | 值 |
|---|---|
| 玩家 avatar 归档 | `18235e0c9ec0e636`（bundles 内 slim 存储，4241 条目） |
| 玩家 bones 资源 | 类型 `0x18dead01056b72e9` / 文件 `0x4d1c334d294dfa97`，**1484 字节** |
| 格式 | `uint32 nameCount(90)` + `uint32 lodCount(6)` + LOD 表 + 90×4 哈希表 + LOD2 表 + NUL 分隔名字表 |
| 骨架骨名 | 含 `cape1..cape8`、`attach_knife`、`backpack`、`pistol`、`r_shoulderarmour`、`l_shoulderarmour`、`neck`、`head`、`boss`/`spine1`/`spine2`/`chest`/`hips` |
| 玩家状态机 | 同一 file id `0x4d1c334d294dfa97`，`ragdoll_count = 6` |

状态机里那 **6 条布娃娃/抖动条目**（每条 56 字节：`uint32 bone_index` + `float32 params[9]` +
`uint64 unk_hash` + `uint32 unk_enum` + `uint32 unk`），用 `bone_index` 反查骨名后
**与 wiki《Jiggle Bones》列出的 6 根完全吻合**：

| bone_index | 骨名 | params[9]（前 6 位：weight, gravity, ?, ?, 角度限值, ?） |
|---|---|---|
| 26 | `attach_intelpad` | `0.5, -9.8, 0, 0, 0.17453, 0, -10, 10, 0` |
| 68 | `target_designator` | `2.5, -9.8, 0.8, 0.5, 0.17453, 0, 0, 0, 0` |
| 81 | `support` | `5, -9.8, 0.8, 0.5, 0.08727, ...` |
| 63 | `sling` | `1.5, -9.8, 0.8, 0.5, 0.17453, 0, 40, 80, 35` |
| 71 | `support_mg` | `5, -9.8, 0.8, 0.5, 0.08727, ...` |
| 54 | `attach_samplepouch` | `1, -9.8, 0, 0.5, 0.17453, ...` |

**结论（对 §3.2 的加固）**：这 6 根骨的物理参数是**烘焙在状态机资源里的**，
Lua 只能"驱动"它们（`update_jiggle_bone_data` / `update_jiggle_bone_orientation`），
**不能新建第 7 根，也不能改这 6 根的参数**——参数只在离线改资源时可变。
骨架里也**没有空余的抖动骨槽位**（90 骨中这 6 根已全部占用，`cape1..cape8` 属于披风单元）。

### 10.3 布娃娃与披风：都是 Havok（一手）

- **布娃娃** = `ragdoll_profile` 资源（82484 字节），含 **23 具** `ragdoll_<部位>` 刚体
  （`boss` / `hips` / `spine1` / … / `l_thigh` / `r_hand`）+ `hkRagdollConstraint002` +
  `hkHingeConstraint002` + `ragdollMotors` + `Default Physics System Data`，
  源路径写着 `cha_avatar/avatar_helldiver`。**Havok 全身布娃娃，只在 `can_ragdoll` 时激活**（与 §3.3 一致）。
- **披风** = **Havok Cloth**：`cloth` 资源里含 `export_havok_cloth_medium`、`hkClothCollidable`、
  `collidableShapeType`、`capsuleTaper`、`heightfieldResolution`；
  披风有**独立 unit + 独立 bones + 独立 cloth**：`0x3c33cf10a26cbb3e`（66 骨，`medium_cape`）与
  `0xea846ef460cc5ba0`（44 骨，`shock_trooper_cape`）；骨架里有 `cloth_colliders` 节点与挂在身体骨上的
  `Collidable_*`。**Lua 里没有 Cloth 接口**（§10.1）→ 披风物理**确实借不了**（§3.5 结论不变，但现在有硬证据）。
- **全库类型直方图**（3552 个包，0 失败，5 秒扫完）：
  `unit` 48034、`physics` 39172、`animation` 36659、`bones` 3062、`state_machine` 2105、
  `ragdoll_profile` 184、`ik_skeleton` 27、**`cloth` 仅 3**（其中 2 个就在玩家 avatar 归档里）。
  → 布料资源在全游戏只有 3 个，**不是一种"可以随便借用的通用机制"**。

### 10.4 对用户三个难点的最终回答

1. **"游戏如何判断哪些物体需要进入布娃娃状态？"**
   → 不是靠 Lua 判断，而是**状态机资源里逐骨声明**：玩家状态机里恰好有 6 条单骨条目
   （`ragdoll_count = 6`），本质上就是"每根骨一条单骨物理条目"；
   而全身布娃娃另有一份独立的 `ragdoll_profile`（23 具 Havok 刚体）。
   两者都**只在倒地/死亡时**由引擎激活。
2. **"布娃娃状态的物理如何判断？"**
   → 由 `params[9]`（权重/重力/阻尼/角度限值等，见 §10.2 表）+ Havok 约束参数决定，
   **全部烘焙在资源里**；SDK 的 `SetBoneRagdollOperator` 给出的默认值是
   `Weight=0.0, Gravity=-9.8, Param3..9=0.0`。**Lua 不可写。**
3. **"我们如何把布娃娃或披风的物理嫁接到所需的区域？"**
   → **纯运行时 Lua 做不到**：HD2 不暴露 `PhysicsWorld`/`Actor`/`Joint`/`Cloth`，
   没有 `create_custom_joint` / `create_cloth`，`Mesh` 也没有权重接口。
   **唯一可用的运行时抖动入口是 `Unit.update_jiggle_bone_data` / `update_jiggle_bone_orientation`**，
   而它只能驱动**状态机里已存在的那 6 根骨**。

### 10.5 本轮新增的生产能力：CLI 直读与提取游戏数据

用户明确要求："让我们的管理器 CLI 版本支持提取游戏数据，方便 mod 作者，也方便我们直接用，
后续不需要再做临时工具。" 已落地（`hd2mod gamedata extract`）：

- 新增 `HD2ModCore/Infrastructure/AssetTypeNameTable.cs`：把类型哈希翻译成可读名，
  数据来自**内嵌的同一份 `typehash.txt`**（与管理器设置页同源，不产生第二份真相）。
- 新增 `HD2ModCore/Infrastructure/GameDataAssetCatalogService.cs`：**不依赖 SQLite 索引**
  直接遍历游戏包枚举/读取任意资产，支持按类型/归档过滤与 toc/stream/gpu 三段读取；
  单包失败只记 `CoreIssue` 不中断。
- CLI 新增四条命令（**都不需要 `--manager-root`**）：
  `gamedata extract list|show|asset|archive`，并配 `gamedata-extraction` Agent 能力 Skill。
- **实测校验**：用新命令提取玩家 bones 资源，得到 **1484 字节**，解析出 `nameCount=90 / lodCount=6`，
  且 `cape1..cape8`、`attach_intelpad`、`sling`、`support`、`support_mg`、`target_designator`、
  `attach_samplepouch`、`head`、`neck` **全部命中** → 直读路径与 `Decryptor` 路径**逐字节一致**。

---

## 11. 权威取证（第三轮：回答"能否用改内存的手段改写实体骨架驱动链路"）

> 起因：用户提问——"既然我们的运行时动态映射护甲链路能成，并且是通过修改内存直接成，
> 那么我们是否能运行时直接修改实体骨架驱动链路？"
> 本节先把"映射链路靠什么成事"说准，再把"改骨架驱动链路"拆成可判定的若干含义。
> 证据来源：① 本仓 `HD2RuntimeArmorOverride/src/armor_registry_runtime.lua`（1270 行，逐行读）；
> ② 我们自己的游戏内探测 `tmp/ArmorLuaSurface_v1-20260922.jsonl`；
> ③ 上游 Stingray 官方 Lua 参考 `tmp/stingray-api/`（`sigs.json` 3063 条 + `obj_*.html` 单页）。

### 11.1 先纠正一个关键理解：映射链路不是"能随便改内存"，而是"拨开关"

映射链路能成事，**不是因为**我们拿到了"任意改写游戏内存"的能力，而是因为：

1. **游戏本身就有**"读 `%LOCALAPPDATA%/CowboyBingus/Helldivers2/ArmorRegistry` 做映射"的功能；
   我们做的只是**把它的 pending 标志置 1**，让它去重读一遍。
   `refresh_pending_write` 写的是 `manager + 0x995` 处的**一个字节**。
2. **三条硬自律**（脚本注释原文）：`Writable data only; do not change page protection or write code.`
   —— 只写可写数据段（要求 `state == 0x1000` 且 `protection == 4 or 8`）、**不改页保护**、**不写代码**。
3. **绑定具体构建**：模块是 `game.dll`（不是 `helldivers2.exe`）；校验 PE 时间戳 `1790149348`
   与 image size `60719104`；对 `Unit.world` 还额外校验内部实现 `callee_rva == 0x9d8c0`。
4. **E8 site 解析**：它靠"在已知函数的已知偏移处找 `call rel32`"来**推导内部实现函数的地址**
   （`Unit.is_child` rva 4267568、`Unit.world` rva 4224528、`World.link_unit` rva 0x3eb860）。
   这是**极其脆弱**的做法——游戏一更新，全部失效。

→ **能力边界：改「数据」可行 ✅ / 改「代码」不可行 ❌。**
"骨架驱动链路"的**逻辑**在代码里，改不了；
但链路里**流动的数据**（骨骼变换、动画权重、抖动参数）在内存里，原理上可改。

### 11.2 "修改实体骨架驱动链路"的 5 种具体含义，逐条判定

| # | 含义 | 结论 | 依据（一手） |
|---|---|---|---|
| A | 改**骨骼本地变换** | ✅ **已可行**（HD2IAS 走的就是这条） | `Unit.local_pose/local_rotation/local_position(self, i)` 与 `set_*` 系列；`i` 是**场景图节点索引**（`num_scene_graph_items`） |
| B1 | **新增骨骼**（改拓扑） | ❌ 不可能 | HD2 `Unit` 119 个成员**零 `create_*`**；上游 Stingray 也**没有** `create_bone`；`Mesh` 无相关接口 |
| B2 | **改已有节点的父子关系** | ✅ 可以 | `Unit.scene_graph_link(self, i, parent)`，`parent` 传 `nil` 即解除 |
| C | 改**蒙皮权重** | ❌ 不可能 | `Mesh` 24 个成员**零顶点/权重接口**；权重在 `.gpu_resources` 里，Lua 碰不到 |
| D | 改**抖动骨参数**（`params[9]`） | ⚠️ 原理可行，**需先定位** | 参数烘焙在状态机资源；运行时是否有内存副本未验证（见 §11.4 U6） |
| E | 改**驱动逻辑本身**（接管/替换调度） | ❌ **不做** | 等同改代码，风险等级与改数据完全不同 |

### 11.3 本轮新发现：HD2 把整套 ECS 都暴露给了 Lua（上一轮低估了）

**HD2 的 `EntityManager` 21 个成员全部在线**：
`create` / `spawn` / `destroy` / `alive` / `debug_name` / `set_debug_name`，
外加 **13 个组件访问器**：`unit_component`、`scene_graph_component`、`animation_blender_component`、
`animation_state_machine_component`、`mesh_component`、`transform_component`、`render_data_component`、
`data_component`、`tag_component`、`script_component`、`flow_component`、`debug_name_component`、
`shading_environment_blend_component`。

**但 `EntityManager.actor_component` 被砍了**（上游有 `actor_component(world) : stingray.ActorComponent`）。
→ **能造实体，造不出物理。** 这是"物理被整层裁掉"的又一条硬证据（与 §10.1 一致）。

**三个此前未评估的官方入口**（都不需要改内存、不需要绑构建）：

1. **`Unit.set_animation_bone_mode(unit, mode)`**
   mode ∈ `ignore` / `transform`（默认）/ `position` / `rotation` / `scale` /
   `position_and_rotation` / `position_and_scale` / `rotation_and_scale`。
   官方说明原文：`ignore` = *"Bone animations are ignored; they will not affect the unit's bone nodes."*
   → **可以把骨骼从动画驱动里"摘出来"**，交给我们的弹簧算。
2. **`Unit.set_bones_lod(unit, lod_level)`**
   官方说明：*"The LOD level determines which bones in the animation will be evaluated."*
   → 不在该 LOD 里的骨**不被动画求值** → 完全归我们控制。
3. **`AnimationBlenderComponent`**（`EntityManager.animation_blender_component(world)` 取到）
   `create` / `destroy` / `destroy_all` / `crossfade(entity, animation, layer, blend_time, loop)` /
   `set_time` / `set_speed` / `bone_mode` / `set_bone_mode` / `root_mode` / `set_root_mode` /
   `delta_transform` / `set_bones_lod` / `lookup` / `instances` / `instance_ids` / `set_parent` / `spawned`。
   官方说明原文（`crossfade`）：*"Plays an animation directly on the entity **without using an
   animation state machine**."*
   → **可以绕过状态机，直接往实体上叠动画层。** 这是"附加驱动"最接近官方支持的形式。
   `AnimationBlenderBoneMode` 与 `AnimationBlenderRootMode` 两个枚举**在 HD2 里都是暴露的表**
   （见 §11.4 U7，枚举值可现场列出）。

**HD2 独有、上游零条的抖动骨 API**（`tmp/stingray-api/sigs.json` 里搜 `jiggle` = 0 条）：
`Unit.update_jiggle_bone_data`、`Unit.update_jiggle_bone_orientation`、
`Unit.debug_draw_jiggle_bone`、`Unit.debug_draw_all_jiggle_bones`、`Unit.debug_draw_all_constraint_bones`。
→ HD2 **自建了抖动骨系统**，并把**驱动入口暴露给了 Lua**。这是唯一现成的"抖动"运行时接口。

**HD2 独有的其它 `Unit` 成员**（上游无）：
`is_child`、`world`、`resource_name`、`has_animation_blender`、`project_skinned_decal`、
`set_ik_debug_drawing_enabled`、`add_new_trigger` / `remove_trigger` / `update_trigger_time`、
`terrain_update_height_field`、`landscape` / `num_landscapes`。
⚠️ **注意**：`is_child` 与 `world` 明明是 HD2 的成员，映射脚本**却仍用 FFI 去调它们的 RVA**
→ 说明"出现在方法表里" ≠ "能直接调用"。（可能因调用约定或绑定层限制。）

**另一个可用入口**：`Unit.animation_set_constraint_target(unit, index, value)`
—— *"Sets the value of a constraint target in the unit's animation state machine."*
`value` 可为 `Matrix4x4` / `Quaternion` / `Vector3`；**必须用点语法**（不能用冒号）。
→ 只能驱动**状态机里已定义**的 constraint target，不能凭空创建。

### 11.4 仍未确认的项（诚实边界，不得当事实用）

| # | 未知项 | 为什么未知 | 怎么验证 |
|---|---|---|---|
| U5 | `update_jiggle_bone_data` / `update_jiggle_bone_orientation` 的**参数签名** | 上游没有这两个函数；我们的探测脚本只调用了白名单（`invokes_discovered_functions: "allowlisted_id_conversion_and_can_get_only"`），**没真的调用过** | 游戏内用 `pcall` 试调，错误信息会暴露参数个数 |
| U6 | 抖动骨 `params[9]` 在内存里**有没有运行时副本**、副本在哪 | 未做内存取证 | 需 FFI 扫描 + 与资源里的值比对 |
| U7 | `AnimationBlenderBoneMode` / `AnimationBlenderRootMode` 的**枚举值**（是否含 additive 语义） | 枚举页 `AnimationBlenderBoneMode.html` 未抓到（404），只有返回类型引用 | **HD2 里这两个表是暴露的**，可现场遍历列出 |
| U8 | `Application.can_get` 的**有效时机** | 探测显示：startup / sample_1 / sample_2 返回 **false**，sample_3~sample_6 返回 **true** | 已部分确认；需确认"何时变 true"的判据 |

→ U8 的实测含义：**任何"上手就改"的方案都必须等到对象有效**，否则静默失败。

### 11.5 对本问题的结论

**能，但"改内存"不是那条路。**

- 要改的是**数据**（骨变换 / 动画层权重 / 抖动参数）→ 按稳定性排序：
  1. **官方 Lua 入口**（`set_local_rotation` + `set_animation_bone_mode(ignore)` + `AnimationBlenderComponent`）
     —— 最稳，**不需要绑构建指纹**；
  2. **官方抖动入口**（`update_jiggle_bone_data`）—— 需先探明签名（U5）；
  3. **FFI 直接改内存里的运行时数据** —— **最后手段**，绑构建，游戏一更新即失效。
- 要改的是**逻辑/代码**（替换骨架驱动调度）→ **不做**。映射链路能"改内存"，
  是因为它只写了**一个字节的数据标志**，不是因为它能改代码。
- **更重要的判断**：我们大概率**不需要**走内存。
  `Unit.set_local_rotation` + `Unit.set_animation_bone_mode(ignore)` 这个组合，
  已经能实现"把一批骨从动画手里接管过来、逐帧写我们自己的弹簧结果"
  —— 这正是 HD2IAS 在做的事，也是"运行时直接修改实体骨架驱动链路"的**官方版本**。
  差别只在：HD2IAS 每帧驱动**全部**骨并调 `World.update_unit()`；我们只驱动**少数**目标骨。

### 11.6 下一步（只读验证，零风险，待拍板）

1. 拿一个真实护甲 Unit，同时列出 `num_scene_graph_items` / `num_meshes` / `bones()`，
   搞清**骨名 ↔ 场景图索引**的对应关系（这是所有后续工作的地基）。
2. 遍历列出 `AnimationBlenderBoneMode` / `AnimationBlenderRootMode` 两个表的实际枚举值（补 U7）。
3. 用 `pcall` 试调一次 `update_jiggle_bone_data`，靠错误信息反推参数个数（补 U5）。
4. 验证"只写 rotation 不调 `World.update_unit()`，画面是否更新"——决定要不要付那份每帧成本。

