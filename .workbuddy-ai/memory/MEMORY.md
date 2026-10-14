# HD2ModManager 项目长期记忆

## 协作与边界
- 仓库：`E:/Data/source/repos/WpfApp1`；用户非程序员，中文解释通俗，用户负责游戏/视觉验收，助手负责代码、格式和数值核查。
- 只本地提交、不推送；临时脚本/交接放 `tmp/rig-lab/`；候选和证据另存，不覆盖唯一回退点；不删除 `.workbuddy-ai`。
- 路线：Python 探针/离线 patch → 用户实机 → 通过后再迁 C#。无 Lua 的 legacy 包必须可运行。交付目录 patch/gpu 改动后要执行 `library sync` 四段流程。
- rig-lab 负责 L4D2→HD2 转换；CLI 线程只负责把已有 C# mod 能力暴露成基层命令，不替 rig-lab 编写转换算法。

## 🔴🔴 判断权归用户（第 77b 轮用户明确要求，长期有效）
- **外观好不好只能由用户肉眼判断。** 涉及外观的实验**必须输出可视文件**（多视角渲染图 / `.blend` /
  游戏包）交用户看；**禁止**用标量指标（离轴距离、rms、倾斜角、柱形图）替用户下"好/坏"结论，
  也禁止据此否决一个方案。指标只作定位线索。
- 用户原话：「好不好不是表格、柱形图能表示的，我的最终目的是观感……哪怕它偏离了骨骼特别特别多，
  多又怎么样？你确定了它就一定不好吗？……ai 无法承担责任」。
- 现成工具：`tmp/rig-lab/_render_arm_npz_views.py`（npz → 手臂 5 视角 × N 变体同相机拼图）、
  `tmp/rig-lab/blender_build_variants.py`（多变体并排进一个 .blend，带 HD2 骨名顶点组 + 骨链红线）、
  `tmp/rig-lab/_probe_render_npz.py --pair`（全身四视图两两对比）。

## 环境
- Python：`C:/Users/Administrator/.workbuddy-ai/binaries/python/envs/default/Scripts/python.exe`；Blender：`D:/SteamLibrary/steamapps/common/Blender/blender.exe`。
- SDK：`docs/HD2SDK-CommunityEdition-3.8.0`；游戏 data：`D:/SteamLibrary/steamapps/common/Helldivers 2/data`；原版抽取：`E:/tmp/hd2-mgroot/`；mod 库：`E:/mod/`。
- L4D2 源目录使用 Lunalice 以外的包时，必须显式传 `addons=`/`packs=`/`model_vpk=`。
- ⚠️ 游戏 `data/` 里：`patch_0` → 基础护甲 mod；**`patch_1` → `E:/mod/MourningVeil-RE2310/`（我们的包）**；
  `patch_2` → `.runtime/lua/…`。要换游戏里跑的版本，**改 `patch_1` + `patch_1.gpu_resources` 两个符号链接**即可
  （`deploy_candidate.py` 硬编码只改 `patch_0`，不适合我们）。
- ⚠️ 环境**没有 matplotlib**（有 numpy / scipy / Pillow 12）⇒ 画图用 PIL，或直接用 `show_widget`。

## 🔴 MourningVeil 的**忠实重打包链**（第 77b 轮已逐字节复现，负对照通过）
`armor-normalize` 输出的 unit，其模型 stream（114213 顶点）与 `model.npz['ours_bind']`
**一一对应、逐位相同**（变换=恒等）⇒ 换一版 retarget 结果**只需换位置**：
```
set_unit_positions.py --src E:/tmp/mourning-an-out/9ba626afa44a3aa3.patch_0 --out <D1> --npz <npz>
reweight_forearm_twist.py --src <D1> --out <D2> --a 0.47 --b 0.88 --twist-frac 1.0 \
        --hand-start 0.90 --hand-end 1.12 --radius 0.10
align_forearm_to_axis.py --src <D2> --out <D3> --side left  --mode rotate --limb-bones forearm \
        --t0 -2.0 --t1 2.0 --fade 0.02 --strength 1.0
align_forearm_to_axis.py --src <D3> --out <D4> --side right --mode rotate --limb-bones forearm \
        --t0 -2.0 --t1 2.0 --fade 0.02 --strength 1.0
```
用 **OFF** npz 跑：gpu `4880a066…`(an-out) → `fbac8d42…`(r74) → `736132ac…`(r76 左) →
**`85b29d24…` = 交付包逐字节一致** ✔（patch 全程 `e4ed9d01…`）。**先跑这条负对照再信新结果。**
- 已用 ON npz 跑出候选：`E:/mod/MourningVeil-RE2310-r77bON/`（gpu `8e4214db…`），
  并已把游戏 `patch_1` 两个符号链接指向它；回退说明 `E:/tmp/r77b-deploy-revert-note.txt`。

## rig-lab 当前状态
- 模型摆姿、骨架、单承载 Unit、显示和阴影问题已解决；当前剩余重点是小臂动作手感与材质观感。
- **Lilium-Sancta-RE2310**：第69轮可用包仍在 `E:/mod/Lilium-Sancta-RE2310/`；patch sha256 `5e4e045c3e1cbc7c…`，GPU sha256 `e9b0bb91ca0b1383…`。回退点 `E:/tmp/round69-final-rollback/`，不要删。
- **MourningVeil-RE2310**：**当前交付 = 第76b轮**（第74轮权重 + 第76轮左小臂 / 第76b轮右小臂的**绕肘刚体旋转**校正，双臂齐了）。
  patch sha256 `e4ed9d01765e13ad180f7ab6af86a99b2bc8703bd43c4c35e95d369a59c23cb8`（TOC 始终未动，从 r72 起未变），
  GPU sha256 **`85b29d243fc2523a27d8fb40703599190a9149cf1ac790a5d1e61552b8950602`**
  （r76 `736132ac…` → r75b `28bf6b55…` → r75a `aa6f6259…` → r74 `fbac8d42…` → r73 `bc14e0a2…` → r72 `2432fc5e…`）。
  回退点：`E:/tmp/r76b-ship-rollback/`（r76 只有左臂的字节）、`E:/tmp/r76-ship-rollback/`（r75b 字节）、
  `E:/tmp/r75b-ship-rollback/`（r75a 字节）、
  `E:/tmp/r75-pre-align-rollback/`（r74 字节）、`E:/tmp/r74-ship-rollback/`（r73）、
  `E:/tmp/r73-ship-rollback/`（r72）、`E:/tmp/r72-ship-rollback/`，都不要删。
  第76b轮 `library sync` 四段**已完成**（`changedNodeIds=[185f279d…]` → 复查 added/changed/missing 全空；
  library.json 节点 `contentFingerprint = 0a389ede…`，前值 `801558ff…`，再前值 `00b0e1f4…`）。

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

## 🔴🔴 第74轮：手腕错位 = 第73轮自己引入的 ROI 截断 bug（已修，但**游戏实测无改善**）
> ⚠️ **第75轮结论：这个修复不是主因。** 用户实机测试第74轮包（patch `e4ed9d01…` / gpu `fbac8d42…`）
> 反馈"看起来并没有改善"。原因：它只动 22 728 / 16 332 696 字节（0.14%），且只影响小臂末端 10%。
> **真正的根因见下方「第75轮」——小臂网格整体偏离骨骼轴。** 本节保留供追溯。

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
- 手腕处**仍有**一个星形褶皱，但在"两骨同 θ"与"手 θ + twist 0.78θ"下**完全一样** ⇒ **不是扭转比例造成的**。
  ⚠️ **第75轮用户澄清：那是「带有褶皱花边的手环」，是设计配件，不是缺陷** ⇒ 不再当缺陷处理。

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

## 🔴🔴 第75轮：手腕错位的**真根因** = 小臂网格整体偏离肘→腕骨骼轴（权重已排除）
- **权重已对齐原版，这条路到头**：新探针 `_probe_twist_lag.py` 测逐顶点实际转角因子
  `f = w_hand + 0.78*w_twist`。腕带 `|t|<=45 mm` 内 f 极差：**我们 0.233 / 原版 0.235**
  （θ=90° 都是 ~21°）。原版自己也有这 21°（0.78 扭转比的设计）⇒ **不是缺陷，继续调权重无效**。
- **骨骼与原版逐位相同**（`l_elbow head [-0.4723 -0.0539 1.3236]`、`l_hand head [-0.6566 0.1401 1.2189]`，
  小臂 287.4 mm）⇒ **骨骼不能改**（游戏动画按这套骨长写）。
- **真凶**：判据 = 顶点投影到垂直于骨轴的平面，看是否**跨过 0**（骨轴在网格内 ⇒ 有正有负）。
  `spine1→spine2` −246.8..+236.8 ✅ / `l_thigh→l_knee` −114.8..+131.1 ✅ /
  `l_knee→l_foot` −53.8..+100.9 ✅ / `l_clavicle→l_elbow` −153.2..+128.5 ✅ /
  **`l_elbow→l_hand` −1.2..+81.3 ❌（只在单侧，骨轴贴小臂边缘）**。
  按材质拆也一致：我们 mat1 −0.4..+80.4、mat7 −1.3..+81.3；原版 mat0 −41.6..+74.1（跨过）。
  偏移方向 `+N_=(0.725,0.689,0)`（单位空间）= **朝身体中线内侧＋朝前**；腕带 ~36 mm、靠肘 ~46 mm。
- **为什么正好是"钟表指针"**：扭转轴 = 肘→腕连线，轴落在小臂边缘时"自转"必然附带
  **绕偏心圆公转** `= 2*偏心*sin(θ/2)`；偏心 40 mm、θ=90° ⇒ 约 57 mm。**偏心 0 才是纯自转。**
  ⇒ 用户第74轮判断（骨骼不在网格中心 + 手臂是"纯角度"摆放）**完全成立**，且范围是**整条小臂**，不止手腕。
- **证据图**：`tmp/rig-lab/_log/r75/`（6 张）。关键：`r75-bones-ours.png` vs `r75-bones-vanilla.png`
  ——红线骨链在原版穿手臂正中、在我们贴小臂上缘；`r75-overlay-0.15.png` 同坐标系叠加。
- **Blender 不需要用户亲自操作**：① `blender --background --python` 可无人值守；
  ② 本轮修法用不上 Blender（Surface Deform 会把外形也改成原版，用户明确"不动外形"），
  只需一个**顶点位置平滑校正位移场**，与 `reweight_forearm_twist.py` 同层。
- **新增只读探针**：`_probe_twist_lag.py`、`_probe_arm_axis.py`、`_probe_arm_sections.py`、
  `_probe_arm_overlay.py`、`_probe_arm_bone_overlay.py`、`_probe_arm_coverage.py`、
  `_probe_twist_orbit.py`、`_probe_arm_components.py`、`_probe_hand_axis.py`、
  `_probe_field_diff.py`、`_probe_normals_check.py`、`_probe_patch_units.py`。
  文档 `tmp/rig-lab/ROUND75_WRIST_ALIGNMENT.md`。

### ✅ 第75轮下半场：左小臂校正位移场**已实现、已验收、已部署**
- 脚本 **`tmp/rig-lab/align_forearm_to_axis.py`**：
  `d(v) = -strength * alpha(t/L) * limbWeight(v) * e(t)`。`e(t)` = 逐站最小二乘圆拟合的
  管轴偏移（slab .04 / step .02 / radius .12，只留 n≥10 且 rms≤4 mm）；`alpha` 余弦渐隐
  （`t0 .34` 为 0、`.62` 为 1、`t1 1.30` 回 0 ⇒ **肘与整条上臂完全不动**）；
  `limbWeight` = 顶点在 **`l_elbow + l_hand_twist`** 上的权重和（`--limb-bones forearm`）
  ⇒ **手/手指零位移**；法线用局部形变逆转置（化简为 `n' = normalize(n - axis*(d'(t)·n))`）。
- **关键经验：小臂偏移 44 mm 而网格边长只有 14 mm ⇒ 需要 ≥100 mm 的渐变距离才不撕裂。**
  第一版（`t0 .06 fade .16`，46 mm 渐变）在肘部把 14 mm 边拉到 24 mm（**1.72×**），作废。
- **三档对比（θ=90° 拧腕质心公转 mm；腕带 t/L .90-1.00）**：
  A（肘子树权重，`t0 .06 t1 .98 fade .16`）腕带 25.8 / 拉伸 1.722 / 手不动；
  D（肘子树，`t0 .22 t1 1.25 fade .30`）腕带 **2.4** / 1.304 / **手被搬 ≤25 mm**；
  **G 定稿**（肘+扭转骨，`t0 .34 t1 1.30 fade .28`）腕带 **3.6**（原版 15.2）/ 1.366 / **手不动**。
- **验收**：偏心量 t/L .60-.84 由 24~36 → **0.4~2.7 mm**（原版 5.7~12.7）；腕环 22.1 → **10.8**
  （原版 12.3）。公转 .50-.65 7.4→1.1 / .65-.80 23.2→0.3 / .80-.90 28.0→17.5（残余）/
  **.90-1.00 29.7→3.6** / 1.00-1.10 22.4→12.0。**右臂逐项完全相同。**
- **逐字节**：TOC 逐字节相同；GPU 大小不变；改 **46794/16332696（0.2865%）**，全在
  `position`(19022×2)+`normal`(4375×2)；**缓冲区外 0 字节**；两 stream 对称。
  法线：被移动 2157 顶点"存储 vs 几何"夹角 9.07°→9.55°（几乎不变）。
- **已部署**：回退点 **`E:/tmp/r75-pre-align-rollback/`**（第74轮字节）。
  交付目录 patch `e4ed9d01…`（**未变**）/ gpu **`aa6f6259…`**。
  `library sync` 四段已跑，节点 `185f279d…` 指纹 `4cbb4127…` → `8cbec9b2…`，复查全空。
  manifest 加 `forearmAlign` 段；README 加「第75轮」整节 + `preview/` 6 张新图。
  ⚠️ **本节做法已被第75b轮取代**（见下方第75b轮节）：第75轮的位移场带中段 alpha 渐变，把小臂弄弯成字母 W。
- ⚠️ **踩坑**：`_probe_twist_orbit.py` 第一版 `t` 从**腕**算起 ⇒ `t/L>0.2` 选中的其实是
  **手部**几何（f 恒 1.000），误判"没生效"。**必须从肘算起。**
- **仍未做**：**只做了左臂**（右小臂未做）；校正把小臂往身体外侧+前方搬最多 **33 mm** ⇒
  **外形会变**，待实机验收；`t/L .80-.90` 残余 17.5 mm 公转；重定向本身仍是整体刚体。
- **已定论**：小臂是**带缺口的大半环**（24 格方位桶占 10~22 格、最大缺口 30~120°；需 ≥100 mm
  切片才有足够顶点）；**小臂→腕→手是一条连通曲面**（硬切在腕会撕开，必须用权重渐变）。

## 🔴🔴 第75b轮：第75轮那个位移场把小臂弄弯了 —— 改成刚体平移（已部署）
- **用户实机反馈**：搬动小臂不是"延长大臂"（内角 120° 的 **V**，延长大臂则肘部外移一点、V 保持完好），
  而是**从小臂一半位置往外搬** ⇒ 手臂变成 **W**。用户在白线（他画的手臂中心线）上看到中段折角。
- **数字确认（新探针 `_probe_arm_centerline.py`：逐站最小二乘圆拟合求网格中轴线 + SVD 直线拟合）**：
  小臂中轴线直线残差 原版 **2.90** / 改前 **3.90**（是直的）/ 改后 **7.69**（弯了）；
  倾斜 2.12 / 4.96 / **12.90°**；肘部偏移 11.9 / 43.1 / 50.2 mm。逐站剖面：t/L 0.02–0.38 偏移原封不动，
  到 0.62 砸到 0.4 mm ⇒ **折点 = alpha 渐变带（0.34→0.62）**。
- **🔴 关键洞察**：`limbW = w_elbow + w_hand_twist` 在小臂上**恒等于 1.000**（实测 t/L 0.36→1.20 全 1.000）
  ⇒ 只要**去掉 alpha 渐变**，位移在小臂上就是**常量 ⇒ 纯平移 ⇒ 数学上不可能弯**；过渡自动落到
  肘（权重 0→1）与腕（twist→hand 交接）两个关节。
- **🔴 手不能搬（决定性判据）**：用用户约定"手指骨必须落在网格里"——手骨→最近网格顶点距离
  原版 11.4/22.8 mm、**我们改前 6.1/16.6（比原版还好）**、整块含手一起搬 17.2/39.6（小指骨被甩出网格）
  ⇒ **手本来就摆得对**。方案 `align_forearm_rigid.py`（平移+绕肘旋转刚体）**否决**，记入 `rejectedCandidates`。
- **🔴 偏移方向（`_probe_arm_offset_dir.py`）**：原版平均偏移 9.9 mm（沿大臂方向仅 −3.3）；我们 31.2 mm
  （沿大臂方向 **−23.7**）⇒ 多出的部分落在大臂延长线上且远离肩膀 ⇒ 修正 = **肘部外移、V 保持不变**，
  与用户描述完全一致。
- **定稿（候选 K）**：`align_forearm_to_axis.py --side left --limb-bones forearm --t0 -2.0 --t1 2.0
  --fade 0.02 --strength 1.0`（即 alpha 全程 = 1，去掉中段渐变；`--limb-bones forearm` = 只含
  `l_elbow + l_hand_twist`，手完全不动）。⚠️ `--t1` 必须比整条手臂长（曾切 1.40 切在手上 ⇒ 手指 x7.3 撕裂）。
- **实测效果**：小臂中轴线 **倾斜 0.48°、肘部偏 0.8 mm、直线残差 2.13 mm**（均优于原版 2.12°/11.9/2.90）；
  逐站残余 0.2–8.3 mm；截面质心中段 36.2→**2.7 mm**（原版 14.9）、腕部 35.5→25.9（原版 29.1，打平）；
  mesh V 内角 139.34° vs 骨 140.36°（改前 146.76°）；手部 6.1/16.6→**5.9/12.4**。
- **逐字节**：TOC 逐字节相同；GPU 大小不变；改 **58264/16332696（0.3567%）**，全在
  `position`(54854)+`normal`(3410)；**缓冲区外 0 字节**；两 stream 对称；右臂逐项完全相同。
- **撕裂自检**：max 1.791 / min 0.625 / p99 1.421，最差边**全在肘部**（limbW 0.26↔0.81 相邻跳变，
  是权重图本身性质）。**代价：肘部整体外移约 40 mm，形变集中在那里。**
- **已部署**：回退点 **`E:/tmp/r75b-ship-rollback/`**（r75a 字节：patch `e4ed9d01…` / gpu `aa6f6259…`）。
  交付目录 gpu `aa6f6259…` → **`28bf6b55…`**，patch 未变。`library sync` 四段已跑，复查全空，
  节点 `185f279d…` 指纹 `8cbec9b2…` → **`00b0e1f4…`**。
- **新增文件**：探针 `_probe_arm_centerline.py`、`_probe_arm_offset_dir.py`、`_probe_hand_fit.py`、
  `_probe_limb_bones.py`；探索脚本 `align_forearm_rigid.py`（未采用）；改 `_make_r75_align_sheet.py`（加 CJK 字体）；
  文档 `ROUND75_WRIST_ALIGNMENT.md` §10；manifest 加 `forearmRigidAlign` 段 + `forearmAlign` 标 `supersededBy`；
  README 加「第75b轮」整节。证据图 `preview/forearm_align_before_after.png`（★总图 4 行）+ 前后对照多张。
- **仍未做**：**只做左侧**（右臂 tilt 5.04°、偏 43.4 mm，同参数应等价但未验证）；肘部台阶待实机验收
  （不可接受可试 `--strength 0.7`：残差 10.5 mm / max 拉伸 1.325）；`l_shoulder_twist` 仍被归零；第6步仍未进主链。

## 🔴🔴 第76轮：钉死肘部、绕它旋转（用户否掉第75b的平移，已部署）
- **用户反馈**：第75b轮"从肘部开始平移其实不太好……当前版本肘部出现了内凹……小臂的根部会
  超出大臂一小段距离……我们不太需要肘部也向同一方向移动，而是能旋转肘部，只让腕部精准到
  指定位置最好。"
- **🔴 结构性理由（用户直觉正确）**：**`l_elbow` 的旋转中心就是肘关节本身** ⇒ 顶点偏不偏心都
  绕肘关节转 ⇒ **肘部偏心不影响肘部弯曲**；**`l_hand_twist` 的旋转中心在腕、轴是小臂骨轴**
  ⇒ 偏心 e 被拧转 θ 时**公转 2e·sin(θ/2)** ⇒ **只有腕带必须居中**。第75b为了腕部把肘部也拽走
  40 mm ⇒ 多此一举，代价就是"根部超出大臂 + 肘内角变凹"。
- **实现**（`align_forearm_to_axis.py --mode rotate`，新增）：`d(v) = limbWeight(v)·(R(ψ)·(p−E) − (p−E))`，
  `E` = `l_elbow` head；ψ 由「腕带（`--pivot-lo 0.86 --pivot-hi 1.06`）管子中心落到骨轴」闭式
  2×2 最小二乘解出 ⇒ **5.56°**。位移 = `|p−E|·ψ` ⇒ **肘关节处严格 0**、向腕线性增到 25.9 mm
  ⇒ **不可能新长折角**，且刚体旋转 ⇒ 形状零变形。`--limb-bones forearm` ⇒ 手/手指零位移。
- ⚠️ **两个坑**：① **手性** `ax×V_ = N_` ⇒ `N_×ax = V_`、`V_×ax = −N_`，写反就反向转
  （腕部偏移 23 → **46 mm**，正好翻倍）；② **拟合窗口必须限定腕带**——用整条小臂（0.20–1.00）
  拟合 ⇒ 最小二乘解是"管子与骨轴平行"，腕部仍留满 44 mm ⇒ 比不改还差。
- **实测**（逐站偏移 mm，t/L 0.07/0.11/0.19/0.31/0.51/0.73/0.83/0.93/0.99）：
  r74 41.9/41.2/40.1/36.8/33.3/25.0/25.2/26.1/23.2 → r76 **41.9**/39.8/38.2/25.6/18.1/6.7/1.6/**1.2**/**6.9**。
  **肘部一点没动，腕带修好，中段顺带改善。**
- ⚠️ **整段直线拟合 tilt 5.85 → 9.58° 不是变差**：肘部钉死后直线不可能既平行骨轴又过腕，
  管子是"从 41 mm 收敛到 0"。**外形没变**（只绕肘转 5.56°），但**网格 V 张开约 3°**
  （146.42 → 149.49°，骨 140.36°）—— 必然代价。
- **逐字节**：TOC 不变、GPU 大小不变、改 **65070/16332696（0.3984%）**，全在
  `position`(53574)+`normal`(11496)，**缓冲区外 0 字节**，两 stream 对称，右臂逐项相同。
- **手部**（骨→最近网格顶点）：原版 11.4/22.8、前 6.1/16.6、**后 5.8/11.3** ✔；
  **撕裂** max **1.404**（r75b 1.791）/ min 0.691 / p99 1.314，最差边**全在腕→手过渡带**
  （t/L 0.99–1.05），**肘部零形变**。
- **部署**：回退点 `E:/tmp/r76-ship-rollback/`（r75b 字节 gpu `28bf6b55…`）；交付目录
  patch `e4ed9d01…`（未变）/ gpu `28bf6b55…` → **`736132ac…`**；`library sync` 四段已跑，
  节点 `185f279d…` 指纹 `00b0e1f4…` → **`801558ff…`**，复查全空。
- **新增工具**（只读）：`_probe_arm_v_angle.py`（肘旁短窗口求可见 V 角）、
  `_render_arm_vplane.py`（在手臂自身弯曲平面内渲染，相机由骨骼定框 ⇒ 各版本同比例可比，
  `--center-on elbow|wrist --half` 出放大图）、`_compose_arm_strip.py`（带标题网格拼图）。
- **仍未做**：**只做左侧**；**网格 V 张开 3°** 待实机判断；腕→手过渡带形变（max 1.404）待实机看皱褶；
  重定向本身仍是整体刚体（yaw + 统一缩放 + 平移），关节级对齐未做。

## 🔴 第77轮：摆姿管线**已经**"以源关节为枢轴"；⚠️ 第一版探针有 bug（已修）
- 用户提案：以起源 T-pose 肘骨（随大臂转动移动）为枢轴，把"源肘→源腕"对齐到"源肘→HD2腕"。
- **已实现**：`hd2_style.py:908` `joint()`=对齐后源关节 → `:918` `new_head[hn]=joint(sn)` ⇒ `rest_sym` 的 head 就是源关节；`:737-742` 网格始终绕 `rest_sym` 烘焙；`:1382` `D[b]=D[p]@rs@basis@inv(rs)` = 绕 rs 原点转；大臂 `:1271`/小臂 `:1322` 的 min-arc 枢轴 = 源肩/源肘。**源骨架确实随网格移动。**
- ⚠️ **`_probe_arm_aim_geometry.py` 第一版漏了 `hd2_style.py:957-981` 的「腿部倾斜」步骤**（`leg_tilt=5.7°` 绕脚踝旋转 + 把**所有非腿骨**平移 `delta = Rl(hips) − hips` = `(0, +0.0949, −0.0047)`）。漏掉后源肩 y 会差 9.5 cm，**上一轮给用户的 112.9 mm / 17.0° / 61.8 mm 全部作废**。修好后与管线转储**逐位吻合**。
- **修正后的真值**（左臂；右臂镜像）：源肩 vs HD2 肩 **187.4 mm**、源肘 vs HD2 肘 **281.0 mm**、源腕 vs HD2 腕 **360.8 mm**。加 gain + 肩转后**工作肘仍离 HD2 肘 167.7 mm**（沿 HD2 大臂 **−90.0** + 垂直 **141.4**）。`|HD2腕 − 我们肘| = 316.6 mm`，我们小臂 **288.5 mm**（HD2 287.4，长度本来对）。
- **两目标方向夹角 31.77°**（不是 17°）。落点：现行 **167.8 mm**；现行+6.5° open **136.0 mm**；**用户瞄准 28.1 mm**（= 恰好是小臂长度差 316.6−288.5）⇒ **用户的方案其实好得多**。
- 取舍：瞄准 HD2 腕 ⇒ 腕位准（28 mm）但小臂管**偏离 twist 轴 31.77°**；对齐 HD2 方向 ⇒ 管在轴上但腕偏 167.8 mm。**两者不可兼得，除非把肘挪过去**（= 选项 1）。
- **选项 1 实测（本轮）：指标全面变差 —— ⚠️ 但指标不等于观感，最终判断权在用户**（见上方「判断权归用户」）。`--arm_refit_scratch`（默认关）+ 四种分解系数（默认 .55/0、1.0/1.0、1.0/0、.55/1.0）同口径（npz 中轴线 `--radius 0.30`，左臂）：小臂管离 HD2 轴 OFF `46.5` → ON `56.6` / A `143.6` / B `83.4` / C `128.7` mm；直线度 rms OFF `5.06` → `8.74 / 5.25 / 8.39 / 3.35`；大臂倾斜 OFF `8.55°` → ON `6.29°`、B `12.16°`、C `22.85°`、A `24.38°`。机制上逐骨残差（肩 187.4 / 肘 167.7 / 手 136.0，垂直分量 173.5/162.9/132.9）**方向不一致**，作为平移场施加会剪歪手臂。**已输出可视文件交用户判断**（`preview/r77b_arm_views_left.png` / `_right.png`、`preview/r77b_fullbody_off_vs_on.png`、`E:/tmp/r77b-variants.blend`）。
- **🔴 ON 与 OFF 的 npz 只差顶点位置**：`ours_bind`/`ours_game` 有 54893 顶点不同（max **75.85 mm**）；`w_bone`/`w_val`、骨骼、`tris` **逐位相同** ⇒ 下游材质与权重步骤无需重算，只有位置相关的 `align_forearm_to_axis` 要重跑。
- **负对照**：开关关时 `model.npz` 全部数组与出货 npz **逐位一致**（几何指纹 `978d9d8ccc98d43d`），`arm re-fit` 行不出现 ⇒ 改动默认完全惰性。
- 新增/改动（**未动任何交付字节**）：`hd2_style.py`（+`arm_refit_scratch` 参数、`+HD2_ARM_REFIT_DUMP` 诊断转储）、`dump_blender.py`（+`--arm-refit-scratch`、`--arm-fit/--arm-up/--arm-perp/--arm-smooth/--arm-taper`）、新探针 `_probe_arm_aim_geometry.py`（已修）、`_probe_arm_npz_centerline.py`。
- 回退点：`E:/tmp/r77-ship-rollback/`（r76b 字节）、`E:/tmp/r77-src-rollback/`（hd2_style/dump_blender/build_patch 原样）。

## 🔴🔴 第76b轮：同一套"绕肘旋转"应用到右臂（已部署并验证）
- **用户反馈**：第76轮"很好，本次的问题看起来修复的不错，现在需要优化右臂"。
- **结论**：右臂**不需要新算法**。基线本就与左臂镜像一致（tilt 5.68 vs 5.85、肘偏 44.3 vs 44.1），
  同参数换 `--side right` 直接给出等价解：**ψ 5.65°**（左 5.56）、移动 **3465** 顶点（左 3442）、
  最大位移 **26.3 mm**（左 25.9）、max 拉伸 **1.402** / min 0.688 / p99 1.321（与左镜像一致）。
  命令：`align_forearm_to_axis.py --side right --mode rotate --limb-bones forearm --t0 -2.0 --t1 2.0 --fade 0.02 --strength 1.0 --report-edges`。
- **实测**（t/L 0.07/0.19/0.31/0.51/0.73/0.83/0.93/0.95/0.97/0.99）：改前 41.8/40.0/37.0/33.8/28.0/25.2/26.7/22.5/23.6
  → 改后 **42.0**/38.1/25.8/18.2/7.2/1.7/**5.7/5.9/6.9**。肘部**一点没动** ✔；腕带修好 ✔；
  手部 6.3/16.1 → **6.1/11.4** ✔；网格 V 146.59 → 149.72（与左 149.49 同量级）。
- **隔离验证（本轮新增的决定性检查）**：`_probe_side_isolation.py`（只读）对 3465 个移动顶点统计
  左/右臂子树权重 ⇒ **左臂 min/max 全 0.000**、右臂 0.001–1.000、**两侧都非零的顶点 0 个** ⇒
  "isolated to one side"（改右臂不会漏到左臂）。
- ⚠️ **右臂没有原版参照**：原版抽取单元 `0x4a213e0ae8acf74b` 只有左臂骨，
  `_probe_hand_fit.py --local <原版> --side right` 报 `KeyError: 'r_hand'` ⇒ 右臂只能自己 before/after + 与左镜像比对。
- **左臂回归**：中轴线/V 角/手部与第76轮**逐项相同** ⇒ 没碰左臂。
- **逐字节**：TOC 不变、GPU 大小不变、改 **66180/16332696（0.4052%）**，全在 `position`(26927×2)+`normal`(6163×2)，
  **缓冲区外 0 字节**，两 stream 对称。
- **部署**：回退点 `E:/tmp/r76b-ship-rollback/`（= 第76轮只有左臂的字节，gpu `736132ac…`）；
  交付 gpu `736132ac…` → **`85b29d24…`**，patch `e4ed9d01…` 未变；`library sync` 四段已跑，
  节点 `185f279d…` 指纹 `801558ff…` → **`0a389ede…`**，复查全空。
- **新增工具**（只读）：`_probe_side_isolation.py`（单侧隔离验证）、`_make_r76b_sheet.py`（右臂证据图）。
- **仍未做**：**网格 V 张开约 3°（双臂）** 待实机判断；腕→手过渡带形变（max 1.402）待实机看皱褶；
  重定向本身仍是整体刚体，关节级对齐未做。
