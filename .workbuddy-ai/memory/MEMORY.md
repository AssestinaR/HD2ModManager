# HD2ModManager 项目长期记忆

## 协作与边界
- 仓库：`E:/Data/source/repos/WpfApp1`；用户非程序员，中文解释通俗，用户负责游戏/视觉验收，助手负责代码、格式和数值核查。
- 只本地提交、不推送；临时脚本/交接放 `tmp/rig-lab/`；候选和证据另存，不覆盖唯一回退点；不删除 `.workbuddy-ai`。
- 路线：Python 探针/离线 patch → 用户实机 → 通过后再迁 C#。无 Lua 的 legacy 包必须可运行。交付目录 patch/gpu 改动后要执行 `library sync` 四段流程。
- rig-lab 负责 L4D2→HD2 转换；CLI 线程只负责把已有 C# mod 能力暴露成基层命令，不替 rig-lab 编写转换算法。

## 环境
- Python：`C:/Users/Administrator/.workbuddy-ai/binaries/python/envs/default/Scripts/python.exe`；Blender：`D:/SteamLibrary/steamapps/common/Blender/blender.exe`。
- SDK：`docs/HD2SDK-CommunityEdition-3.8.0`；游戏 data：`D:/SteamLibrary/steamapps/common/Helldivers 2/data`；原版抽取：`E:/tmp/hd2-mgroot/`；mod 库：`E:/mod/`。
- L4D2 源目录使用 Lunalice 以外的包时，必须显式传 `addons=`/`packs=`/`model_vpk=`。

## rig-lab 当前状态
- 模型摆姿、骨架、单承载 Unit、显示和阴影问题已解决；当前剩余重点是小臂动作手感与材质观感。
- **Lilium-Sancta-RE2310**：第69轮可用包仍在 `E:/mod/Lilium-Sancta-RE2310/`；patch sha256 `5e4e045c3e1cbc7c…`，GPU sha256 `e9b0bb91ca0b1383…`。回退点 `E:/tmp/round69-final-rollback/`，不要删。
- **MourningVeil-RE2310**：第74轮已修「交接档被 ROI 截断」bug；patch sha256 `e4ed9d01765e13ad180f7ab6af86a99b2bc8703bd43c4c35e95d369a59c23cb8`（TOC 始终未动），GPU sha256 `fbac8d425140bffa5865f92b30c53c0c11355539f8e5178da674688fe0c566cf`（r73 `bc14e0a2…` → r72 `2432fc5e…`）。回退点 `E:/tmp/r74-ship-rollback/`（r73 字节）、`E:/tmp/r73-ship-rollback/`（r72）、`E:/tmp/r72-ship-rollback/`，都不要删。第74轮 `library sync` 四段**已完成**（apply 新增0/更新1 → 复查 0/0/0；library.json 节点 `contentFingerprint = 4cbb4127…`）。

## 🔴🔴 第73轮：小臂橡皮筋根因已确定（真实动作矩阵已取到）
- **玩家骨架 `0x4d1c334d294dfa97`（90 根）**；玩家动画**全在归档 `18235e0c9ec0e636`**（2222 个 `bone_count=90`）。资源 file_id = `murmur64_hash(ASCII 路径)`。
- **`l_hand` 与 `l_hand_twist` 是 `l_elbow` 的并列子骨、头部重合**（局部平移都是 `[0.28718,0,0]`；`l_elbow` 是 `[0.30274,0,0]`=上臂）。**`l_hand_twist` 的局部旋转永远是 `[x,0,0,w]` ⇒ 单自由度纯扭转骨**，`l_hand` 是三自由度。
- **游戏确实在驱动 `l_hand_twist`**：300 个真实动画里它与 `l_hand` 的扭转**相关系数 median 0.98、全部 ≥0.95**，角度比 **median 0.78**；它的 swing 恒 0（反向验证小臂轴 = 肘局部 +X，长 287.18 mm）。
- **原版规则（决定性对照，原版左臂皮肤 unit `0x4a213e0ae8acf74b`，小臂 L=287.4 mm）**：`l_elbow → l_hand_twist → l_hand`；**`l_hand` 在整条小臂上权重恒 ≤0.013**，只在 t/L>0.93 才接管。**原版 RE-2310 护甲 unit 只有 8 槽、无手臂骨**（HD2 手臂是独立皮肤 unit）。
- **我们的包**（第72轮）小臂中段 t/L 0.62 有 **`l_hand` 0.53 / `l_hand_twist` 0.00**，四个 twist 骨全 0 ⇒ **根因：`l_hand` 旋转原点在手腕（离小臂中段 11 cm），屈腕即把中段绕腕摆出 = 橡皮筋**。
- **推翻第68/70轮否定 twist 的两条依据**：① 前提「游戏只转 l_hand」是错的；② 旧 `--twist` 负对照给两骨**同一个旋转矩阵**，两骨 head 重合 ⇒ 位移只取决于权重之和 ⇒「三档结果一样」是**无效对照**。正确模型：**swing 只由 `l_hand` 携带**。
- **第73轮参数**：`--a 0.47 --b 0.88 --twist-frac 1.0 --hand-start 0.90 --hand-end 1.12 --radius 0.10`（`--hand-start/--hand-end` 是新增第三档，默认 1.01/1.21 = no-op，已用「旧参数重跑 = gpu 逐字节相同」验证）。中段 `l_hand` 权重 0.235→0.000。
- **探针（全只读）**：`hd2_gamedata.py`（Python bundled 读取器，与 CLI 逐字节一致）、`_probe_anim_tracks.py`（动画解码，布局 `[clip][u32 len][clip][u32 len]` 双 clip）、`_probe_wrist_rest/split/dump/action.py`、`_probe_forearm_weights.py`、`_probe_vanilla_arm_profile.py`、`_probe_wrist_bend_render.py`、`_probe_wrist_twist_strain.py`。
- ⚠️ **解码坑**：swing/twist 分解的符号必须取「`tw[3]` 翻正之后」的 `tw[0..2]·axis`，否则四元数双覆盖 `q→-q` 造成 252° 假跳变、相关系数变负。
- ⚠️ **`library sync plan` 必须给 `--output <file>`；`apply` 必须给 `--confirm`**（只给 `--plan` 会报 `LibrarySyncTargetChanged`）。
- **仍未做**：`l_shoulder_twist` 同样被归零（原版上臂有 0.113~0.218）；第6步仍未进主链；原版曲线不是 smoothstep。

## 第72轮小臂结论（已写回 `tmp/rig-lab/ROUND72_FOREARM_BRIEF.md` §11、§12、§13）
- RE-2310 头盔是独立 `Helmet` archive，不受 `armor-normalize` 的 `Armor` archive 隐藏边界影响。用户已有隐藏头盔 mod；部署顺序为替换护甲包在前、隐藏头盔 mod 在后。
- 材质规则：材质 ID 使用 `$basetexture` 全路径；底色贴图 ID 只取 basename。同 basename 且内容不同会互相覆盖；同一皮肤被多个材质引用时必须共享 basename/ID。
- ⚠️ **本节的以下四条已被第73轮推翻/修正**（保留供追溯）：
- “17 根远端后代骨分别拉动小臂”是错误因果：当前小臂窗口的手指骨权重为 0；父子层级不会反向把子骨动画传给父骨。问题应拆为权重带宽、真实腕部动作矩阵、网格壳/接缝三部分。
- 原版旧读数 `65.2/61.8`、`0.234/0.340` 作废：原版 `bone_index` 必须按材质 remap 解码，且多索引护甲不能用单组探针截断。修正探针已对多组/非单位变换显式拒绝；最新拒绝是预期负对照，不是故障。
- 正确原版皮肤剖面显示肘→腕前 `hand_twist`→掌侧 `hand` 的空间分工，但不能推出固定 40:60、不能证明游戏动作中 twist 与 hand 的矩阵关系，也不能据此直接改包。第70轮只否定具体的“50:50 + 扩大 ROI”实验，不否定 twist 机制。
- `edge JUMP=|d_i-d_j|` 不是边长变化；真实边长变化应比较变形前后边长，`37.8 mm`“几何地板”已撤销。候选扫描仅是人工30°/180°压力筛查，多壳混合、左右汇总和无真实动画矩阵，不能代替实机。
- 保留第69版，不复制小腿窄带、不做固定比例 twist。若用户授权后续实验，先固定同一壳/边集合，扫描多档 `a=.30/.35/.40/.45,b=1.0`，同时看中线、真实边应变、接缝和多视角；候选另存、两体型/可见 LOD/SDK 反导入后再实机。
- 第6步小臂重权必须成为正式管线步骤，不能靠人工记忆补跑；C# 移植前需保留 remap、half4、逐字节字段、LOD/两体型和回读门禁。
- **专家复核后的第4处自查错误**：`grep reweight_*.py` 显示**没有任何脚本重绑过小腿/膝**（`reweight_forearm_twist.py` 只碰 `l/r_elbow`、`l/r_hand`、`l/r_hand_twist`）⇒「管线给肘→腕与膝→踝同一套参数」不成立，小臂不可与小腿直接类比。
- **按壳拆分（`tmp/rig-lab/_probe_forearm_shells.py`，只读）**：小臂近轴带里 MV 有 3 壳（`shinano_body` 皮肤 / `mat_glove` 护手 / `mat_metal` **只在 0.88–1.05 腕环、几乎刚性**）；Lilium 只有 `manuka_body` 皮肤贯穿，其余在近端或腕部。**承担变形的是皮肤壳（+护手），甲/腕环不参与。**
- **`a=.30/.35/.40/.45,b=1.0` 按壳扫描**：`a` 后移 ⇒ 中段跟随（橡皮筋）单调→0，皮肤壳 180° roll 真实边长变化单调↑；`.45` 前无断崖，`.78..0.92` 才崩（第67轮复现）。**应变峰值在 `t/L≈0.62–0.68` 小臂中段（可见）**，不在腕部。壳间滑动随 `a` 后移略降 ⇒ 不会撕开甲/皮肤。
- **🔴 没有单一可移植参数**：同样 `.22→.40`，MV 皮肤 roll 只 +13%（10.7→12.1 mm），Lilium **+63%**（16.4→26.8 mm）⇒ 印证专家「`.40` 不是新定稿」，三模型不能套一个配方。
- **本轮决策：不改交付包**（保留第69轮基线）。两指标都基于**人工 180° 滚转**，无真实动作矩阵无法判定游戏实际 roll 量。**阻塞项**：取真实 `l_elbow/l_hand/l_hand_twist` 动画矩阵；可行性已确认——SDK 自带 `docs/HD2SDK-CommunityEdition-3.8.0/stingray/animation.py`（`StingrayAnimation` 逐骨 pos/rot/scale 轨道），但定位动画资源 + 骨索引映射是新工作。

## CLI 线当前方向
- `capabilities --json` 当前 148 条；已增加 patch 条目原语、compose/edit/hide-unit、装备权威表等能力。纯转换命令写新目录，不直接改库/游戏。
- C# 已有 `UnitMeshModel`、几何事实、骨名、PatchWorkspace/变更集等数据视图，但 CLI 引用不足；优先暴露 `unit show/geometry/bones/vertices`，再做 patch diff/audit、材质冲突。
- 已核实 bundled 归档读取 bug：GPU/stream 是带后缀的独立包条目，解析器传基名导致只抽出 `.toc`；这是 CLI/底座 P0。不要把 R4 `opt\`、R5 bodygroup 多 Unit 当作 CLI 线程职责。

## 架构分层约定（用户授权 CLI 线程可编辑 Core/Adaptation 类库）
- 依赖单向：`Manager/Cli → Core → {Adaptation, L4D2Support, GameItemAssets, Decryptor}`。生产者不反向依赖 Core，消费者不直连生产者。
- C# ProjectReference 是**传递的** ⇒ 分层编译层面拦不住，**只靠约定**。
- 正确模板：Core/Application 定接口 → Core/Infrastructure 实现并包装生产者 → `CoreServices.CreateXxx()` 工厂暴露 → 消费者调接口（范例 `IAdvancedUnitAnalysisProducer`，`CoreServices.cs:100`）。
- 两处缺口：Core 接口**重读轻写**（无 patch 写器接口 ⇒ Cli 直连 `PatchArchiveWriter`）；Core **无 Source 接口**（⇒ Cli 直连 `HD2SourceModel`，`CliApplication.cs:1879`）。
- 已有 `HD2SourceModel`（源格式解析）+ `HD2SourceMigration`（源→HD2 骨架映射，**孤立无人引用**）。**不要新建第三个 Source 项目**；L4D2Support **先定接口再建项目**。
- 新功能一律走 Core 接口；旧代码（6 处 Cli→Adaptation、1 处 Cli→SourceModel）遇到再收敛。评估详见 `docs/cli/HD2ModCli-layer-boundaries-assessment.md`。

## 未完成但不得冒充完成
- 未获取真实游戏动作的 `l_elbow/l_hand/l_hand_twist` 矩阵时间序列；未完成原版多索引护甲正确解码。
- 未生成或部署第72轮新候选包；未完成分壳形状/接缝验收；未把小臂指标迁入 C#。

## 🔴🔴 第74轮：手腕错位 = 第73轮自己引入的 ROI 截断 bug（已修）
- `reweight_forearm_twist.py` 的 ROI 是 `(rad<radius)&(t>0)&(t<L)`，**`t` 从【肘】算起**（`rel = pos - E`）。
- 第73轮新增的交接档 `--hand-start 0.90 --hand-end 1.12` **按 t/L 跨过腕（=1.0）**，被 ROI **在腕处硬切**：
  t/L 0.98 twist 0.688 → **t/L 1.02 twist 0.000（断崖）**。原版是 0.750 → 0.290 → 0.049 的平滑过渡。
- **后果**：同一轴向位置的两条壳转角不同（扭转 60°，twist 骨 0.78 同转，腕带 |t|<45mm）：
  `mat 0`（41 顶点，w_twist 0.816）转 **47.8°**；`mat 1`（1680 顶点，w_twist 0.337）转 **60.0°** ⇒ **差 12.2°**（θ=150° 时约 20°）。即用户报的"针柄在2点、针尖在3点"。
- **修正三处**：① `t_end = max(L, hand_end*L)`；② `arm_names` 收窄为 `{elbow, hand, twist}`（原为**整个手部子树**，ROI 过腕会抹掉手指权重）；③ 改动判定改为**与顺序无关**。
- **修正后剖面**：0.94→0.906 / 0.98→0.688 / 1.02→0.421 / 1.06→0.207 / 1.10→0.025（原版 0.915/0.750/0.290/0.049/0.008）。台阶消失。
- **验证**：字节负对照 22728/16332696（0.139%），全在 `bone_weight`+`bone_index`，字段外 0 字节，两 stream 对称，TOC 未动。用 r72 参数重跑，ROI 加长只动**每侧 10 个顶点**（t/L 1.02~1.12 手根残留肘权重）⇒ 修正非回归。
- ⚠️ 因此**「旧参数重跑 = 逐字节相同」这条负对照不再成立**（算法改动，非参数默认值问题）。
- ⚠️ **本 unit 的 `remaps` 是恒等**（8 材质 × 107 全 identity）⇒ `reweight_forearm_twist.py` 不 remap 读 `bone_index` 对我们是安全的；**换 unit 前必须补 remap**。

## 🔴 第74轮：探针/格式的既有缺陷与真相（会污染旧结论）
- `_probe_wrist_roll_render.py` 的 `wh` 有**两个 bug**：① 没按材质 `remaps` 解码 `bone_index`；② 没过滤"只被 LOD0 索引缓冲引用的顶点"。⇒ 第73轮 `preview/forearm_bend_before_after.png` 的**手骨权重着色是错的**（形变结论仍成立，因为靠骨名统计）。**第74轮收尾已修**（另补 mesh→unit 变换：行主序、平移在最后一行；`_probe_wrist_bend_render.py` 复用同一 `load()` 一并修好），图已按 R72→R74 重出。`preview/` 不参与 library.json 指纹。
- `ti.entries[i][1]` 是**父骨的 ti 索引**（不是 slot 索引）；`bi.real_indices` 是 slot→ti。
- unit 的 mesh transform 是**行主序、平移在最后一行**：`pos @ M[:3,:3] + M[3,:3]`。
- **只有被 LOD0 索引缓冲引用的顶点才有意义**；其余属别的 LOD，`bone_index` 未经 remap。
- 几何对照（`_probe_wrist_center.py`）：截面质心离骨骼轴 t=-60 我们 29.4 / 原版 13.5 mm；腕环(|t|≤20) 我们 19.97 / 原版 15.36 mm ⇒ 我们的网格相对骨骼**确实比原版偏得多**（与用户"纯角度摆放"判断方向一致，但不能单独定论）。权重过渡带已与原版一致（t=-20 都是 0.231）。
- 手腕处**仍有**一个星形褶皱，但在"两骨同 θ"与"手 θ + twist 0.78θ"下**完全一样** ⇒ **不是扭转比例造成的**，未定位。

## 🔴 重定向的**结构性**缺陷（用户判断成立）+ 工具调研结论
- 我们的 `retarget` 是 **yaw 拟合 R + 统一缩放 s + 平移 t（锚头顶）**，即**整具模型一次刚体变换**，**没有关节级对齐**。源模型 T-pose 与 HD2 A-pose 的手臂角度/骨长比例一有差，误差全堆到关节，手腕最敏感。
- **建议**：升级为「**分段刚体 + 平滑混合**」= 一次 LBS 重定向：① 源骨↔HD2 骨映射（C# 侧 `HD2SourceMigration` 已有雏形）；② 每骨求"源骨 rest → HD2 骨 rest"的刚体变换；③ 用"顶点到骨段距离"混合（类 bone heat）；④ 应用。
- **可借的开源工具/思路**（详见 `tmp/rig-lab/ROUND74_POSE_MATCHING_TOOLS.md`）：
  - **gret `Mesh: Retarget`**（github.com/greisane/gret，RBF；**网格要求源/目标同拓扑**，骨架可用）
  - **AutoTPose**（github.com/4onstudios/AutoTPose，几何启发式识别上臂/前臂对齐 T-pose，有 headless CLI）
  - **LM_SwapRestPose**（github.com/luca-malisan/LM_SwapRestPose，rest pose 互换）
  - **pycpd**（CPD 非刚性点集配准，纯 numpy）
  - **Blender 内置**：`Data Transfer`（Vertex Groups → Nearest Face Interpolated，**权重迁移**）、`Surface Deform`/`Mesh Deform`/`Shrinkwrap`、`Automatic Weights`、`Copy Transforms` + `Apply Pose as Rest Pose`
  - **UniRig**（SIGGRAPH 2025，github.com/VAST-AI-Research/UniRig）、**RigNet**（SIGGRAPH 2020）
  - **BodySlide / Outfit Studio**（上古卷轴/辐射社区，开源的"服装适配不同体型/骨架"标准工具）
  - 学术：Deformation Transfer (Sumner 2004)、Non-rigid ICP (Amberg 2007)、CPD (Myronenko 2010)、ARAP (Sorkine 2007)、Mesh Contraction (Au 2008)
