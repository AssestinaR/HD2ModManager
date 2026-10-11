# 项目长期记忆：HD2ModManager

> 细节在设计文档 `docs/*.md` 与日志 `.workbuddy-ai/memory/YYYY-MM-DD.md` 里；本文件只留**结论、硬约束、易忘的坑**。

## 项目定位
- 仓库 `WpfApp1`：《绝地潜兵 2》(Helldivers 2) Mod 管理器与补丁适配工具集，GPLv3。
- **`ManagedMain` 已过时**，仅留档。
- **主目标 = hd2mod 系列**：`HD2ModManager`(WPF GUI)、`HD2ModCli`(`hd2mod.exe`)、`HD2ModCore`、
  `HD2ModAdaptation`、`Decryptor`、`HD2GameItemAssets` 及各 `*.Tests`。
- `HD2RuntimeArmorOverride` 是护甲运行时映射的 Lua mod 源码仓（非 csproj），lua 被 `HD2ModCore.csproj` 内嵌为 EmbeddedResource。

## 信息中心（阶段 0–6 已完成，2026-10-11）
- 统一身份：`FactKey(FactSubject, ModInformationPropertyKind)`；缓存身份 `FactRevisionKey(FactKey, Revision)`。
  `ModInformationFactRegistry` 是 **25 个 kind** 的单一真相；`IModInformationReader.RequestAsync<T>` 是**唯一读取入口**
  （5 个具名 Read 方法都是它的单语句薄包装）。`Validate()` 有 5 项检查，在 `CoreServices.CreateModInformationCenter`
  开头跑 → 新增 kind 忘写依赖/忘登记 `SourceRoots` 是**启动期失败**，不是测试失败。
- 依赖 DAG：21 条边逐条读代码坐实，`ModInformationFactGraph` 物化（含 `TopologicalOrder`/`IsAcyclic`）。
- 25 个 kind 里**只有 9 个有真实生产者**；**源事实 `SourceRoots` 7 个**：
  `Identity`/`ContentRevision`/`PatchCatalog`/`GameDataIndex`/`AssetMetadata`/`DeployedGraph`/`Thumbnail`。
  用 `GameData` 主体的 5 个：`GameDataIndex`/`AssetMetadata`/`GameDataMapping`/`DeployedGraph`/`ArmorCoverage`。
- **`CacheScope` ≠ 作用域**：它是生命周期阶梯（None/Operation/Session/Persistent），主体维度由 `FactSubject` 表达。
  `FactDescriptor.CacheScope` 全仓**只有一处**生产用途（`ModInformationReader.cs:402` 防呆校验）；
  `FactDescriptor.Producer` **读取处 0 个** → "缓存放哪层"实际由调用方写死，与登记表声明**未连通**。
- **按来源指纹键控的进程内记忆是通用手法**：`SourceFingerprintMemo` + 每族事实一个持有者，**各拿独立预算**
  （`GameDataFactCache`=16、`DeployedGraphFactCache`=4）；**必须 static**（服务每次调用都 new 实例）。
  硬约束（用户提出）：**缓存不得取代真实性** —— 键全由来源指纹构成、不落盘、用**源码文本断言**钉住重建路径
  （`RebuildNeverConsumesTheFactCache`）+ 双向测试。
- **`IGameDataFactSource` 是投影与旁路服务之间的唯一缝**（`CenterGameDataFactSource` / `DirectGameDataFactSource`）。
  失败语义：中心用 `Data=null + Issues` 表达失败，适配器**还原成异常** —— 静默成空结果比报错更难查。
  消费方一律用 `AsMappingFacts()` 收敛，不要各自判空。
- **`ArmorCoverage` 是无节点事实（第四种主体 `FactSubjectKind.GameData`）**：
  护甲页只为"是否被覆盖 + 覆盖者图标"两个字段就**整包构建 GameData 浏览器**（对每个 Mod 请求一次资产清单）
  → 首开卡顿根因。现改为只请求 `ArmorCoverage`。两条铁律：① **图标不进事实**；
  ② **"读不到" ≠ "没有覆盖"**（不在索引里必须缺席/未知，索引未建必须 `Unavailable`，只有"索引里有但没被部署覆盖"才给 `false`）。
- 缩略图**只有一套失效协议**（`ThumbnailFingerprint.cs` 唯一定义，事实缓存与磁盘缓存共用）；
  **磁盘键必须含内容**，否则"同路径换图标"命中旧缩略图且**永不自愈**（这是真实 bug，已修）；
  GC 只有一个入口（启动维护后一次性 `SweepAsync`）。
- 护甲页启动预热：VM/数据/视图各占一个空闲回合；三条硬要求 —— **单飞加载**（`catalogLoadTask ??=`）、
  **取数闸门 `displayFactsReady`**（由 `ShellViewModel` 在启动维护的 **`finally`** 里放行）、
  **预热不占 UI 线程**（`ApplySnapshot` 必须 UI 线程）。
- 内嵌 SQLite 已定点固定：`SQLitePCLRaw.bundle_e_sqlite3 2.1.12`（3.53.3）。
  光升 `Microsoft.Data.Sqlite` **修不了** CVE-2025-6965（10.0.0 仍依赖 2.1.11）。
- `ApplyExecutor` **只支持硬链接与符号链接** → 测试里"目标"与"源"是**同一个文件**；
  要构造"目标与源分离"必须用 `DeploymentMethod.Copy` 手写激活状态。
- 已完成 v1.5 发布（切点 `5c56314`，**纯本地标签，未推送**）。

## 大功能 A：护甲/素体动态骨（物理）—— 调研完成，方案未定
> 详见 `docs/hd2-armor-physics-research.md`（§10/§11 为权威取证）。

- **【硬约束】只允许依赖游戏本体，不得依赖任何外部系统（尤其其他 mod）。**
  `HD2IAS` **只作反面参照物**，一行代码都不会进我们的东西。这也是"FFI 改内存"排在最后的原因（要绑版本指纹，最脆）。
- **【心智模型】三层，前两层焊死、第三层开放**：① 骨架拓扑 ❌ ② 权重 ❌ ③ **骨骼姿态 ✅ 可逐帧改**。
  不是"整台游戏机焊死"，而是"**卡带槽焊死、卡带内容实时可写**"。
- 引擎 = **Autodesk Stingray（原 Bitsquid）**。**HD2 裁掉了全部物理命名空间**
  （`PhysicsWorld`/`Actor`/`Joint`/`Mover`/`Cloth`/`HumanIK`/`Vehicle`/`Raycast`/`NavigationMesh` 均不存在）。
  `Unit` **119 个成员，零 `create_*`**；`Mesh` **24 个成员，零顶点/权重接口** → **纯运行时 Lua 改不了权重、加不了骨**。
- **HD2 把整套 ECS 给了 Lua**：`EntityManager` 21 个成员全在（含 13 个组件访问器），**但 `actor_component` 被砍**
  → **能造实体，造不出物理**。
- **三个不需改内存的官方入口**：`Unit.set_animation_bone_mode(unit,'ignore')`（把骨从动画驱动里摘出来）、
  `Unit.set_bones_lod`、`AnimationBlenderComponent.crossfade`（*"without using an animation state machine"*）。
- **HD2 独有、上游零条的抖动骨 API**：`update_jiggle_bone_data` / `update_jiggle_bone_orientation` /
  `debug_draw_*jiggle*`。**只能驱动状态机里已存在的 6 根骨**，不能加第 7 根、不能改参数（`params[9]` 烘焙在状态机资源里）。
- 6 根原生抖动骨：`support_mg`/`support`(父 Chest)、`attach_intelpad`/`attach_samplepouch`/`target_designator`(父 Spine1)、
  `sling`(父 Spine2)。玩家骨架 **90 骨 / 6 LOD**，**无空余抖动骨槽位**。
- **"上百具尸体堆叠不卡"是误导**：那些是**休眠刚体**，成本≈0。
- 本体披风 = Havok Cloth（全库只有 3 个 cloth 资源）；布娃娃 = Havok `ragdoll_profile`，
  只在 `can_ragdoll`（被击倒/死亡）激活 → **借不了**。
- **"改内存"不是那条路**：映射脚本只写 `manager+0x995` 的 **1 个字节** pending 标志让游戏自己重读；
  三条硬自律 `Writable data only; do not change page protection or write code.`；
  绑 `game.dll` + PE 时间戳 `1790149348` + E8 site 解析 → **游戏一更新全废**。
  **能力边界：改「数据」可行 ✅ / 改「代码」不可行 ❌。**
- **两个易踩的坑**：`is_child`/`world` 是 HD2 独有成员但脚本仍用 FFI 调其 RVA
  → **"在方法表里出现" ≠ "能直接调用"**；`Unit.bones()` 是**状态机**骨名列表，与 `num_scene_graph_items`
  （场景图节点数）**不是同一套索引**。`Application.can_get` 在 startup/sample_1/sample_2 返回 **false**。
- **未确认（不得当事实）**：`update_jiggle_bone_data` 的**参数签名**（探测只调白名单，**没真的调用过**）、
  抖动骨参数有无运行时副本、两个枚举的实际值、`can_get` 何时变 true。
- **我们已有"给 Unit 加骨 + 重写蒙皮"能力且在产线里**：`CanonicalAppendBoneCompiler`（被
  `CanonicalDecorationUnitAppender` 调用）、`CanonicalBoneRebuilder`、`CanonicalLodBonePaletteCompiler`、
  `CanonicalInverseJointMatrixCompiler`、`CanonicalMeshSkinningRouter`。
- **CLI 直读游戏数据已落地**：`hd2mod gamedata extract list|show|asset|archive`（不需 `--manager-root`、
  不依赖 SQLite；护栏是"不得落在游戏 data 内" + "非空目录需 `--overwrite`"）。
  实测 `extract list bones` = 3062，与全库直方图一致。

## 大功能 B（当前方向）：L4D2 模型整体移植到 HD2
> 详见 `docs/l4d2-to-hd2-physbone-migration.md`。**纯调研 + 沟通，未改生产代码。**

**转向原因**：用户判断"给老 HD2 mod 批量刷权重加物理"太难，改为把 L4D2 动漫角色**整体移植**。
自述上次（项目在 `E:\Data\source\repos\HD2AgentWorkbench`）失败原因是"T-pose 摆成 HD2 类 A-pose 太难"、
"两游戏骨骼尺寸差距大"、以及**每次改动必须进游戏实测**导致无法高速迭代。

### 决定性发现：L4D2 动漫模型自带完整 jigglebone 参数
- 实测扫 **97 VPK / 6566 MDL** → **147 个带 jigglebone**（0 解析失败），含用户手头**整个 L4N 系列**。
- `Lunalice_Zoey.vpk` 主模型 `survivor_teenangst.mdl`：**127 骨 / 57 jiggle bone**。
  父级：**`Head1` 15 根（头发）**、`Pelvis` 4（裙摆）、`Spine` 6、`Spine4` 4、左右小腿各 2，**其余成链**。
- **这些骨的骨名读不到**（`sznameindex` 指向内嵌 SMD 文本）**但父级索引完全有效** → 靠"层级 + bind pose 位置"重建。
- Source 侧：`$jigglebone`（Source 2007+，L4D2 支持）；MDL 侧 `STUDIO_PROC_JIGGLE=5`（判据 `proctype`）；
  **`procindex` 是相对该骨自身地址的字节偏移** → `mstudiojigglebone_t`（120 字节）。
  `mstudiobone_t` v53 = **216 字节**；偏移 `flags`(160) `proctype`(164) `procindex`(168)。
  仿真方向依赖骨的 **+Z 轴**。

### 【2026-10-11 本轮新增】离线数学问题已被量化，且答案确实存在
- **两侧世界 bind 用同一个公式**：`world = inverse(bind)`。
  Source 是 `inverse(poseToBone)`（`mstudiobone_t+96`，12 float，row-major/列向量）；
  HD2 是 `inverse(inverse_bind)`。
  **旧项目 `LunaliceHuPoseBake.cs:244` 就是这么做的。**
  ⚠️ **不要靠 pos/quat 沿父链累加**（会忽略 `posscale`/`rotscale`/`qAlignment`，静默塌陷）—— 已踩过。
- **实测（`survivor_teenangst.mdl` vs `hd2_skel.json`，都是 bind pose，米，对地）**：
  - Source bind 是**标准 T-pose（手臂离垂直 90.0°）**；HD2 bind 是 **A-pose（62.6°）**
    → **需要放下手臂 27.4°**，这是**纯旋转**、条件数极好。
  - **真实肢体长度比只有 0.868 .. 1.135**：臂长比 **1.035**、**腿长比 1.006（几乎完全相同）**、
    大腿 1.092、上臂 1.135、前臂 1.066、骨盆→胸 0.926、胸→颈 0.868。
  - **看起来最吓人的两行是"关节语义不同"，不是尺寸差距**：`clavicle->shoulder` 3.195、
    `neck->head` 2.615 —— ValveBiped 把 Clavicle 停在 UpperArm 旁、Head1 在颅底，HD2 放得更远。
    **把它们当"尺寸差距"去解，正是把 solver 带进沟里的原因。**
  - **单一均匀缩放必然失败**：最小二乘最优 scale=0.99，**最大残差 0.2515 m（25 cm 的手位偏移）**。
    ⚠️ 参考插件自己的文档就警告过这个陷阱：*"a fit over landmarks that all share the bias cannot see the bias"*。
  - 解 = **逐骨相似变换（保留 Source 骨长 × 逐骨系数、取 HD2 关节方向），父→子一遍解完**。
- **旧项目其实已经解出来了**（HANDOFF §17–§19）：
  `lunalice build --skinning-mode canonical` 把 Source 顶点**逐顶点烘焙**进 HD2 Avatar 静置姿态；
  `--pose-migration source-rig-fit`（诊断版 `source-rig-fit-v6`）已支持 127 骨父→子求解，
  Source `Spine4` 显式用 HD2 `chest` frame，锁骨 stretch cap 1.50×，脚用 `foot -> ball` landmark。
  真实 `Lunalice_Zoey.vpk` 已产出 canonical/retained 两包并通过 `PatchValidator` + 依赖闭包 + 管理器 `import inspect`。
  **残余**：静置回放误差 **5.7e-4 m**、躯干映射选择、脚部 frame/pivot、手掌非均匀缩放、手指 root gap。
  → **卡住的不是数学，是"改一次必须进一次游戏"的反馈回路。**
- **本轮建成的"斧"（离线回路，全在 `tmp/physbone-recon/`）**：
  `skeleton_probe.py`（读 MDL bind → 世界坐标，输出 JSON）、
  `plot_skeleton.py`（pillow 叠加图：front / side，两具骨架同比例对地）、
  `rig_gap_report.py`（段长比表 + 关节角表 + 三种映射模型残差 + 结论）。
  **零游戏启动即可量化"差多少、差在哪"。**
- **关键未知（生死线，不得当事实）**：
  X1 **两张表分别能长到多大？** transform 表 **107→164**、蒙皮调色板 **90→147** 是否合法
  （不接受则方案作废）；X2 追加骨后 HD2 原有动画是否仍正确驱动前 90 骨；
  X3 Lua 每帧驱动 57 骨 × 每玩家的成本；X4 无骨名的额外骨如何稳定标识；
  X5 Source `is_flexible` 弹簧能否用二阶积分等价复现；X6 `set_local_rotation` 的局部/世界语义与 Source 的 +Z 约定是否一致；
  X7 目标 Unit 选择（RE-2310 Slim Torso 已知可用）。
- **三层分工（待验证）**：① 身体骨（~20 ValveBiped）离线映射到 HD2 原生 90 骨 → **运行时由 HD2 自己的动画驱动**（免费）；
  ② 额外骨（头发/裙摆，本样本 57 根）**原样追加在骨架末尾**（不改原有索引）→ **运行时由 Lua 按 Source 参数驱动**；
  ③ 顶点：身体区域重映射到身体骨，头发/裙摆区域**直接指向额外骨**（索引随骨一起平移，**不需要重映射权重**）。
- **"运行时骨骼转译"其实不存在**：骨骼在离线输出 Patch 时已是 HD2 格式。
- **【用户已拍板，2026-10-11】**
  ① **接受不转译 L4D2 动画**（原话："不然就算走路没问题，武器等动画都会出现问题"）。
  ② 额外骨驱动 = **Lua 复现 Source 风格参数**，但**硬约束：必须维持游戏本体物理的低消耗，
     绝不能引入 HD2IAS 类似的卡顿**。
  ③ **第一阶段先做 A1~A3**（离线数学做扎实），"即使动骨没法加，也能算阶段性胜利"。
  ④ 旧项目可以改；多阶段目标 = 旧项目负责 **VPK 转化**（生产带 L4D2 风味动骨的 legacy 适配模型）
     ＋ WpfApp1 提供 **Lua 插件动骨支持**。**用户倾向两者分开**（怕耦合变屎山）。

### 架构边界（2026-10-11 定，详见 `docs/l4d2-hd2-boundary.md`）
- **同意分开，但"缝"必须落在「版本化文件格式」上，不是目录上。**
  反例就在眼前：旧项目 `HD2AssetKit.csproj` 用**绝对路径**引用 WpfApp1 的 `HD2ModAdaptation.csproj`
  → **两个仓库却已被焊死**（换机器/clone 都构建不了）。**证明隔离来自接口，不来自目录。**
- **唯一判据**：改动一边时另一边要不要跟着改？要 → 缝没切对。
- **缝 = 一个版本化的「动骨声明文件」**（`schema` 必填；只有转换器写、运行时读、**永不回写**）。
  三角色：① **转换器**（旧项目，全仓唯一懂 VPK/ValveBiped/jigglebone）
  ② **管理器**（只装/校验/展示，不认识 Source）③ **Lua 运行时**（只读声明，不认识 Source）。
  Lua 运行时与护甲映射 **同仓但独立成 mod**（独立目录/协议/锁），避免互相打断。
- **性能反向设计**（逐条对 HD2IAS 的三个慢因）：只驱动声明里列的额外骨（身体骨交给 HD2 动画＝零成本）／
  纯 Lua 二阶积分无原生调用／**每 Unit 一份独立状态**（结构上不可能多玩家传染）。
  另加：静置休眠、LOD 剔除、每 Unit 每帧只调一次 `World.update_unit()`。
- **建议顺手修掉屎山种子**：把绝对路径改成相对探测 + 找不到时响亮报错（比本地 nupkg 便宜得多）。

### 【A1 已完成并通过验收 ✅ 2026-10-11】
- 做法（**零生产代码改动**）：复用已发布的 `hd2mod gamedata extract asset` 取玩家 avatar Unit 的
  TocData（**21504 字节**），按 `UnitMeshReader.ReadTransformInfoFromUnitToc` 同一布局用 Python 解析。
  布局：`u32@0x34` = transformInfoOffset；`u32@tio` = count；`tio+16` 起每条 64 字节
  （9f 旋转 | 3f 位置@+36 | 3f 缩放@+48 | 1f pad@+60）；随后 count×64 矩阵、count×4 条目
  （u16 increment | u16 parentIndex）、count×4 名字哈希。
- **结果**：transform 条目 **107**；名字解析 97/107；与 `hd2_skel.json` 可比 **64** 骨；
  **最大误差 0.000262 m（0.262 mm）** ✅（验收 < 1 mm，余量 4 倍）；平均 0.000004 m；
  **误差 > 1e-7 m 的只有 1 根**（`r_shoulder_twist`），其余 63 根在 float32 噪声级。
- **`Matrices[i]` 布局确定 = row-major，平移在 `m[12..14]`**（另两个候选错 1.69 m / 1.70 m）。
  → **骨架来源从"外部常量"换成"游戏本体"**；参考插件警告的"共享常量可能来自某个 mod 私有
  mesh 空间"陷阱从此不成立。
- **顺带挖出**：
  1. **骨架分两张表**：transform 表 **107** vs 蒙皮调色板 **90**（`bones` 资源 `nameCount=90, lodCount=6`）。
     → 加 57 骨 = transform 107→164、调色板 90→147。**X1 要分别验这两张表能否长大。**
  2. **基础 avatar 本来就有一大批专用骨**（实测，此前只是推断）：`cape1..cape8`、`attach_intelpad`、
     `attach_knife`、`attach_samplepouch`、`attach_hand_l/r`、`attach_weapon`、`attach_cam_1`、
     `backpack`、`sling`、`support`、`support_mg`、`target_designator`、`l/r_shoulderarmour`、
     `pistol`、`aim_weapon`、`weapon_aim`、`head_aim`、`boss_aim`、`head_cam`、`head_tip`、
     `climb_ref`、`collision`、`g_body`、`c_culling`。
  3. **`hd2_skel.json` 不是基础 avatar**，而是"某个 mod 的视图"：有 **39 个键**我们表里没有
     （`grp_*`、`g_*_LOD1/LOD2/shadow`、7 个 `*_culling`）；共有 64 骨精确吻合。
  4. **10 个未解析哈希**（不在 `bonehash.txt`）：`0xc89b0fd3`（索引 2）、`0x75275fac`、`0x43997e6e`、
     `0xc15d6a74`、`0xf9963256`、`0xb7d14251`、`0xa5c8115b`、`0x7c74e51c`、`0xa420b088`、`0xb9162207`。
- **下一步 A2**：逐骨相似解（保留 Source 骨长 × 逐骨系数、取 HD2 关节方向），
  验收 = 逐骨残差 < 1 mm **且骨长不变**。A3 = 离线重定向出 OBJ 用眼睛看。
- 工具：`tmp/rig-lab/avatar_rig_probe.py`；`tmp/physbone-recon/{skeleton_probe,plot_skeleton,rig_gap_report}.py`。

### 【A2 已完成并通过验收 ✅ 2026-10-11】
- `tmp/rig-lab/rig_map.py`（**独立实现**，不依赖任何 C# 工程编译）。
  公式 `T_i = H_σ(i) · S_i^-1`（S 先 ×0.0254 换成米）。对应表 **50 对**（20 身体骨 + 30 手指），**0 缺失**。
- 结果：① 外部校验（HD2 节点 vs `hd2_skel.json`）64 节点 **最大 0.000262 m** ✅；
  ② 映射后段长 vs HD2 段长 **最大误差 0.000000000 m** ✅；③ 映射刚性（T 奇异值偏离 (1,1,1) 3.9e-07）✅。
  覆盖：127 源骨 → 50 直接映射 / 77 回退最近映射祖先。
- **②的副产品才是真正的"网格拉伸"数字**（HD2/Source 段长）：骨盆→胸 **0.9258**、胸→颈 **0.8678**、
  颈→头 2.6154（关节语义不同）、臂 **1.0352**、腿（髋→踝）**0.9092**。
- ⚠️ **陷阱 1（会静默出错）：两侧矩阵存储约定不同，必须转置。**
  Source（`poseToBone`）= row-major **列向量**（平移 flat **3,7,11**）；
  HD2（avatar transform 表）= row-major **行向量**（平移 flat **12,13,14**）。
  不转置 → 段长全算成 **0.0000**。A1 只用平移所以没暴露，A2 要完整 4×4 才炸。
- ⚠️ **陷阱 2：脚部落点改变腿拉伸比 10%**：`L_Foot→l_foot` 得 **0.909**，`→l_ball` 得 **1.006**。
  即旧项目 HANDOFF 的未解决项"脚部 frame/pivot"，现已量化为可比较的数字。留给 A3 用眼睛定。
- 产出：`tmp/rig-lab/rig-map.json`（66 KB：127 源骨 + 107 HD2 节点 + 50 对映射的 4×4）。

### 【旧项目现状：绝对路径已修，但它当前编译不过 2026-10-11】
- **绝对路径已修 ✅**（便宜办法）：`HD2AssetKit.csproj` 改为按序探测三个常规位置 ＋
  `-p:HD2ModAdaptationProject=` / `HD2_MOD_ADAPTATION_PROJECT` 覆盖 ＋
  解析失败时由 `EnsureHD2ModAdaptationResolved` 目标抛带修复指引的错误（码 **HD2AW001**）。已验证生效。
- **但它编译不过（预先存在，非本轮引入）**：5 个错误全是引用了 WpfApp1 里**已被删除**的类型——
  `HD2ModAdaptation.PatchReconstruction.Validation`（命名空间）、`CanonicalDependencyClosure`、
  `CanonicalDependencyClosureRequest`、`CanonicalDependencyDiagnostic`、`CanonicalDependencyClosureResult`。
  **不是改名，是整个依赖闭包校验子系统被移除**（现存只有枚举 `CanonicalDependencyClosureValidation`
  与 `CanonicalPlanDiagnostic`）。旧项目最后提交 **2026-09-03**。
- → **这是架构结论的活证据**：两仓库看着分开，实际被（a）绝对路径（b）对私有类型名的直接依赖**焊死**；
  改 WpfApp1 会**静默**打断旧项目，直到有人构建才发现。
- **待用户拍板**：修那 5 处继续用，还是按新架构重写转换器。**不影响 A1/A2**（独立 Python 实现）。
- **VPK 踩坑**：tree **没有 `num_extensions` 字段**（ext/dir/filename 三层各用空字符串终止）；
  entry 18 字节后还要跳 `preloadLength` 字节；内嵌数据偏移 = `headerSize + treeSize + entryOffset`。

## 工作约定（必须遵守）
- **【强制】不再推送到远程 —— 助手只做本地提交，推送由用户手动执行。**
- **【强制】用户授权范围内的改动直接做，不反复请示。**
- **用户不是专业程序员。** 凡涉及"某东西要不要删/要做什么"，**必须先用通俗语言说明它有什么作用、删了会丢什么**，再给建议。
- **【环境】不要把临时脚本写进 `_diagnostics/`** —— 用 `tmp/`（两者都 gitignore）。
  `DiagnosticsPatchSampleTests` 是**条件测试**：`_diagnostics` 一旦存在（哪怕空的）就会真跑并失败（实测 880 绿 → 878 通过/2 失败）。
- **【环境】Agent shell 里 `Remove-Item` 被 safe-delete 钩子接管，而这台机器"删到回收站"必抛错**
  （两条后端都在**成功删除之后**报错，`exists after = False`）。后果：**任何在 Agent shell 里做删除的脚本都会失败**
  （典型受害者 `publish.ps1`）。应对：让**用户在他自己的 PowerShell 窗口跑**。
  **不要**用 `[System.IO.File]::Delete` 绕过（那等于把"删到回收站"降级成永久删除）。
- **写完"应该能抓住某个 bug"的用例后，主动把被保护的那一行破坏掉再跑一次**；若仍通过，说明用例是假的。
  （实测教训：`ApplyExecutor` 只支持硬链接 → "只改源"的用例**假通过**，靠探针才发现。）
  也见过**两处守卫各自冗余**（单独删任一处都仍通过，两处同删才失败）—— 发现冗余本身有价值。
- 删除类改动**必须同时改造其测试**（改判据形态），不能连测试一起删。
- **`docs/` 被 `.gitignore` 忽略** → 编辑前务必备份到 `docs/_backup/`。
- `CoreServices` 是唯一装配点：判"某服务在生产中是否被使用"看 `CoreServices.CreateXxx(` 调用点是否为 0。
- `git commit` 提交的是**整个索引**，与 `git add` 传了哪些路径无关 → 提交前确认索引干净。
- **`tools/check_dead_code.py` 是词匹配 → 注释里写类名会把死代码误标为可达**（实测 40 → 33）。
  **必须两个方向一起看**（`comm -13` 新增 + `comm -23` 消失）；只盯总数下降发现不了。
- `TreatWarningsAsErrors` 已开启，但用 `<WarningsNotAsErrors>NU1901;NU1902;NU1903;NU1904</...>` 排除 NuGet 审计警告
  （用户明确"本应用不担心被攻击"）。**只降级不静默**。反直觉：**NuGet 只对 net9.0+ 目标审计传递包**。
- 构建/测试耗时：全解决方案 `dotnet test` 约 6–7 分钟；`dotnet build -t:Rebuild` 约 20 秒。
- **报测试计数时同时报分项与增量**（只报总数看不出"某条被静默跳过"或"被误删"）。**基线数会变，报数前先跑一遍。**
- 发布：标签用**轻量标签**；产物溯源靠包内 `cliVersion`（`1.0.0+<git 全哈希>`）；
  校验产物**不要用 PowerShell `Add-Type`**（被拦），改用 Python `zipfile`。
  `HD2ModCli/skills/` 有 28 个 `SKILL.md`，但导出/打包的是 **27** 个（少的 `mod-management` 是 `IsLegacy: true`，**27 是设计值**）。
