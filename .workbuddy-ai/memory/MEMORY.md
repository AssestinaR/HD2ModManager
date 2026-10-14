# HD2ModManager 项目长期记忆

> 详细论证链在各轮交流文档 `tmp/rig-lab/ROUND*.md` 与每日日志 `.workbuddy-ai/memory/YYYY-MM-DD.md` 里。本文件只留**规则**与**当前状态**。

## 约定
- 仓库 WpfApp1；只本地提交不推送。用户非程序员，讲通俗话；用户负责视觉验收，助手负责数值与自动检查。
- 临时脚本放 `tmp/rig-lab/`（gitignore 只放行 `*.py`，`*.md` 需 `git add -f`），不放 `_diagnostics/`。`docs/` 忽略，改前备份 `docs/_backup/`。不删 `.workbuddy-ai`。故障包与依赖留版本+hash，候选另存目录，不覆盖唯一证据。一律绝对路径。
- 修 bug 必做"破坏关键行"负对照；删代码同步改测试。**子代理 = 用户另开线程 + 交流文档（`*.md` 放 `tmp/rig-lab/`），不用 Agent 工具。**
- 路线：Python 快循环 → 离线 legacy patch 包 → 用户实机 → 确认后上 C#。只借 BatchTool 变换；不依赖 HD2IRG1 动态骨骼；**无 Lua 也必须能跑**。**发图前自己先肉眼看过。**
- **`tmp/` 暂时保留**（第 66 轮拍板）：等所有功能移进正式 C# 类库后再清理；新写的东西仍放 `tmp/rig-lab/`。

## 环境
- Python `C:/Users/Administrator/.workbuddy-ai/binaries/python/envs/default/Scripts/python.exe`（numpy/Pillow/scipy/lz4，无 matplotlib）；Blender `D:/SteamLibrary/steamapps/common/Blender/blender.exe`；7z `"/c/Program Files/7-Zip/7z.exe"`。
- SDK `docs/HD2SDK-CommunityEdition-3.8.0`（texconv 在 `deps/`）；游戏 data `D:/SteamLibrary/steamapps/common/Helldivers 2/data`；源素材 `D:/SteamLibrary/steamapps/common/Left 4 Dead 2/left4dead2/addons/`（`备份/` 下有八人包等 7z）；原版抽取 `E:/tmp/hd2-mgroot/`；mod 库 `E:/mod/`（`library.json` 在**库根** `E:/mod/library.json`）。
- CLI `HD2ModCli/bin/Debug/net10.0/hd2mod.exe`；管理器索引库 `E:/tmp/hd2-mgroot/data/indexes/asset-index.sqlite`。

## 🔴 当前状态（Lilium 交付）= 第 69 轮
- **`E:/mod/Lilium-Sancta-RE2310/`**（第 69 轮 · 定稿）
  - patch 562176 B sha256 `5e4e045c3e1cbc7c…`（**第 66~70 轮逐字节相同** —— 权重只在 GPU 侧载荷，patch 容器不动）；gpu 84229248 B sha256 **`e9b0bb91ca0b1383503caa959658471ec313211ca7adf7dc531668a6caa667f2`**。
  - 64 条目 = 22 unit(2 承载 + 20 隐藏) + 2 bone + 11 material + 29 texture。审计 `_verify_normalized.py` **27 项 26 通过 / 1 已知**（唯一已知 = 管理器隐藏占位 unit 丢零索引段的材质短 id，见下「直接替换原生护甲」）。
  - **第 70 轮（twist 实验版）已实测无效并回退**（详见下方手臂权重节）。⚠️ **秒级回退点**：`E:/tmp/round69-backup-deliver/`（第 69 轮完整交付包，gpu `e9b0bb91…`）。回退命令 `cp E:/tmp/round69-backup-deliver/{9ba626afa44a3aa3.patch_0,9ba626afa44a3aa3.patch_0.gpu_resources} E:/mod/Lilium-Sancta-RE2310/`，回退后须跑一次 `library sync` 刷新库节点 `9ff82517`。
  - 历史 gpu：70 `1ca095d5…`（`round70-final/` gpu `cddeb5df…` + `round69-an-out-backup/` 被 70 覆盖前的 `lilium-an-out/`；**已弃用**）/ 69 `e9b0bb91…` / 68 `de5f5dd1…`（`round68-an-out-backup/`+`round68-final/`）/ 67 `bb6e3008…`（`round67-an-out-full/`）/ 66 `a8854ba3…`（`round66-fingers-only-archive/`）；65 patch `52d131c5…`+gpu `6587ac4f…`（`round65-lilium-archive/`）；64 `729c4a65…`+`1f20c9cd…`（`round64-lilium-archive/`）。
  - ⚠️ `E:/tmp/round69-an-out-backup/` 现存的到底是 69 还是 70 的规整产物需用 sha256 现查（70 轮曾把它当第 69 轮输入的代称）。
  - 中间产物 `E:/mod/Lilium-Sancta-Native-RE2310/`（**勿启用**）。库节点 `9ff82517`(成品) / `a74a1022`(中间)。`library.json` 备份 `E:/tmp/library.json.bak-20261013-r66`。
  - `preview/`：`wrist_follow_round66_69_70.png`（跟随比例曲线，69 与 70 重合）与 `wrist_roll_round66_69_70.png`（留作第 70 轮记录）；`wrist_roll_round66_vs_69.png` 仍在。
  - **Lilium 其余 7 个角色未转**（只转了 `Lilium_Sancta_Zoey.vpk`）。

## 🔴 手臂权重：第 66~70 轮这条线（同一根旋钮）—— **第 69 轮定稿，第 70 轮已回退**
「小臂从纯肘骨过渡到纯手骨的这段权重带有多宽 / 挂在哪根骨上」= 唯一在动的参数。

| 轮 | 参数 | 带宽 | 180° 拧转最差边 | 备注 |
|---|---|---|---|---|
| 66 | `smoothstep(0.22L, 0.82L)`，全挂 `l_hand` | 0.22–0.82 | 41.3 mm（纯几何） | 手感"橡皮筋"（软） |
| 67 | `smoothstep(0.78L, 0.92L)`（**仅 40 mm**） | 40 mm | **83.2 / 100.0 mm 真撕裂** | 网格边 32.8 mm 横跨整条带 ⇒ bug |
| 68 | `smoothstep(0.50L, 1.00L)` | 144 mm | 37.8 mm（几何地板） | 撕裂从手腕移到小臂中段 |
| **69** | `smoothstep(0.22L, 1.00L)`，全挂 `l_hand`（`--twist-frac 0`） | 63–287 mm | 37.8 mm（几何地板） | **定稿**：66 起点 + 68 终点；用户"基本可用" |
| 70 | 同 69，但远端 **50/50 分给 `l_hand` / `l_hand_twist`**，ROI 扩到手+手指 | 同 69 | A 54.2/46.6 · B 37.8/37.8 · C 29.4/25.2 | **实验版，实机"手指变差"⇒ 已回退** |

- **🔴🔴 第 70 轮实机结论（这条线到此为止）**：用户实测反馈「**手指弯曲似乎变差了一点，小臂几乎一样**」⇒ 落在**假设 A**（游戏**只转 `l_hand`，`l_hand_twist` 转速更低/几乎不动**）。原因：远端一半权重挂到一根**不转**的骨上 ⇒ 手指/手腕跟随量减半 ⇒ 手指弯曲变差；小臂远端本来只受一半影响 ⇒ 看不出来。**⇒ twist 骨对本模型无收益，第 69 轮（`--twist-frac 0.0`）为定稿，此方向不再尝试。**
- **🔴 几何地板 37.8 mm（改不掉）**：180° 拧转下即使一条边两端权重**完全相同**，长度也会变 `2 × 垂直分量`。本模型最差等权边（`comp 3772`，半径 47/28 mm，`w 1.000/1.000`）= 37.8 mm ⇒ **任何权重方案的下界**。所以"又硬又不裂"在只改权重的范围内做不到，根治要**加密腕部网格**。
- **🔴 HD2 骨架有 twist 骨**（第 68 轮实测；第 67 轮"没有 twist 骨"的结论**已作废**）：`l/r_hand_twist`（slot 64/70）、`l/r_shoulder_twist`、`l/r_forearm_twist`、`l_wrist_twist`。`l_hand_twist` parent = `l_elbow`、head 与 `l_hand` 完全重合、叶子骨，**我们导出 unit 里权重 0**（所以按"带权骨"过滤时看不见）。
- **原版用 twist 骨的铁证**：原版左臂护甲 `unit_0x4a213e0ae8acf74b.toc`（归档 `aaebfbedc571fd7a`）`l_hand_twist` **权重 74.5**；原版手臂皮肤 `g_torso_arm_l_male`（unit `0x586e33e020cbf80f`，归档 `00cf0ef7df912a6e`，LOD0 2448 v，palette 25）里 `l_hand_twist` 65.2 / `l_hand` 61.8。**原版没有 `l_forearm_twist`。** ⚠️ **但"原版这么用"≠"游戏会照原版那样驱动"—— 本模型上实测 twist 骨几乎不动（见第 70 轮实机结论）。**
  - 原版手部剖面（沿 `elbow→hand`，L=287.4 mm）：腕部 t/L 0.93~1.08 **hand 与 twist 同时有权重**（twist 0.34~0.53 / hand 0.18~0.39）；**指尖 t/L>1.3 是 ~100% 手指骨、twist ≈ 0**。⇒ **"手和手指一起纳入分配"必须理解成"只重分配非手指部分，手指留 100% 手指骨"。**
  - ⚠️ 该 unit 的 `bone_index` **就是 palette slot**（`names[slot]=hashlist[real_indices[slot]]` 正确）；但 `ti.matrices[ti_idx]` 的 head 与该 slot 主导顶点质心**对不上**（slot0=`boss` 质心却在肩膀）⇒ **原版 unit 的 matrices 语义与我们的导出 unit 不同**，读原版权重别用 head 做几何判据。
- **三种"游戏怎么驱动"假设（`_probe_twist_roll.py`，180°）**：**A 只转 `l_hand` 54.2/46.6 mm；B 两根都转 37.8/37.8 mm（= 第 69 轮）；C 只转 twist 29.4/25.2 mm**。**✅ 实机已判定 = A**（手指变差 ⇒ twist 骨转得少）。⇒ 结论：**只挂 `l_hand`（第 69 轮）是对 A/B 都安全、代价为零的选择。**
- 工具：`reweight_arm_twist.py`（第 70 轮，`--a/--b/--twist-frac/--radius/--hand-limit`，`--no-hand` 退回旧行为）、`reweight_forearm_twist.py`（第 68/69 轮定稿工具）、`reweight_forearm.py`（第 67 轮）、`_probe_twist_roll.py`、`_probe_wrist_roll*.py`、`_plot_wrist_follow.py`、`_probe_vanilla_wrist.py`/`_probe_vanilla_hand.py`、`_probe_forearm_action.py`。
- **🔴 `_probe_forearm_action.py` 的盲点**：只测**腕部弯折**（绕 `cross(axis,[0,0,1])` 30°），**从未测拧转/旋前** ⇒ 第 67 轮的"0.0 mm 全绿"掩盖了拧转撕裂。**以后验收必须加拧转项。**
- **🔴 `_probe_vanilla_wrist.py` 的关键突破**：CLI 的 `GameDataPackageResolver.GetPackageResourceAsync` 对 bundled 归档**所有分区都用基名找包**，但 `bundles.nxa` 里一个归档其实是**三个包**：`<id>`(TOC) / `<id>.gpu_resources` / `<id>.stream`。后果：`gamedata extract asset` 抽原版 unit **只写出 `.toc`**（GPU 文件不出），`GameDataUnitMeshReader` 也拿不到原版 GPU。**未改产品代码**，探针自己实现 DSAR chunk walk + `bundles.nxa` 包表绕开。
- **权重只改 GPU 侧**：`_probe_weightdiff.py` 逐字节比对（第 70 轮 vs 第 66 轮）= **63542/16917432 = 0.376% PASS**，只落 `bone_weight`+`bone_index`，两流各 31771，8394 顶点，**声明字段之外 0 字节**。
- **⚠️ 每轮重绑的通用前提**：只重分配「本来挂在 `l_elbow`/`l_hand`/`l_hand_twist` 上的权重」，**手指骨权重一个字节不动**（第 70 轮实测 118014 顶点上变化 `0.000e+00`）。**不能按 `hand≥0.95` 排除**（手部皮肤与手指是**同一连通块**，会撕皮肤）。

## 模型与骨骼
- 骨架不动：`S_bind == rest_hd2`；移动/缩放角色只动网格。HD2 +X右 +Y前 +Z上，左肢在 -X，单位米。Unit 存行向量矩阵，平移 flat12..14；`read_hd2_rig` 转列约定（`hd2_patch` 近期去转置，调用者须核查）。
- 摆姿已全部验收（`hand_tilt=3`、`finger_pose=True`）。手指逐节残余摆姿 `Rl=min_arc(s,Q.T@t)`，枢轴须过 post，末节取 HD2 局部 +X；发布 `rest_hd2` 不变，`rest_sym` 是工作枢轴。
- 权重：六区 body_region+附件身份；同侧同指可信三角面重心转移+置信度回退；交界留渐变。`weight_half4.py` 先合并同骨再 2048 格点最大余数，121704 行 half 精确和 1。
- **🔴 坐标系纠错（别再踩）**：源侧 `short[nm]['world_m']` **已经是米**（顶点 `v['pos']` 才是英寸）；源轴是 **X = 左右（+ = 角色左）、Z = 上**，**不是** Valve 常规的「X 前 / Y 左」。**跨模型比较必须用管线自己的 `align_pts` + `t`，不要自己猜轴。**
- **🔴 T-pose vs A-pose**：HD2 rest 是 **A-pose**，`rest_sym` 是烘进 T-pose 的工作骨架，源模型是 **T-pose** ⇒ **不能拿 T-pose 的源关节直接减 HD2 的关节**（会得到 400~570 mm 假误差）。要比就先各自对齐进同一空间，只用「段长 / 关节相对位置」这类姿态无关量。
- **🔴 对齐机制**：`retarget` 1a/1b = yaw 拟合 `R` + 统一缩放 `s` + 平移 `t`；**缩放锚在"头顶高度"、平移锚在"头顶"**。

## 摆姿/权重常量
- 对着 Lunalice 目视调、Lilium 沿用：`HEAD_SCALE=8/7`、`ARM_ELBOW_OPEN=6.5°`、`ARM_HAND_TILT=3°`、`MESH_SHIFT=(0,-0.100,-0.090)`、`upper_scale=1.12`。
- **第 66 轮改成按模型拟合**：`retarget` 新增 `arm_scratch_upper_shift`，`delta = u * gain + shift`（`u` = T-pose 肩→肘单位向量，由 `rest_sym` 取；`shift` 按**左臂**给、右臂 X 取反），与 `gain` 走**同一套 ramp**（`arm_stretch_field`，肩端 0、肘以下强制 1）⇒ 肩缝钉住、`shift` 默认 0 时老行为逐字节不变。**拟合值 `gain = 0.0814 m`、`shift = (0.0014, 0.0097, 0.0005) m`**（由出货网格反解，非目视）。效果：手+指总位移中位 32.5 → 5.2 mm。
- **🔴 教训：拟合函数必须拿"出货量"当参考，不要拿中间量**（第 66 轮踩过：`resid(g)` 用未加 gain 的关节当参考 ⇒ 最优值差 0.05）。**🔴 基准只能在出货网格上量**（A-pose vs T-pose 直接相减是假误差）。
- **遗留**：拇指末节 12.2 mm、食指 ~4.8 mm 是**形状差**（源侧拇指段长 Lunalice 64.71 vs Lilium 58.68 = 短 9.3%、食指短 8.5%，中指/无名指/小指 ±2.5% 内）⇒ 刚性平移修不了，要修得做「按指沿轴缩放」（新机制，需单独验收）。

## 历史轮次要点（细节见 `tmp/rig-lab/ROUND*.md`）
- **第 65 轮「链规则」**（飘袖绑到小指的真凶）：Lilium `source_extra_93..106` 是挂在 `L/R_UpperArm` 下的**飘袖专属骨**（hops 3~7），HD2 无对应骨 ⇒ `region` 兜底把候选池取成「祖先整棵子树（含 15 根手指）+ 同肢体」再按**到关节的距离**取最近 ⇒ 飘袖伸到 1.6 m ⇒ 落到 `*_pinky_finger1`（渗漏 3016 顶点，Lunalice 为 0）。**修法**：**一串未映射的骨头（父子成链）是一个刚体件，整条必须绑到同一根 HD2 骨**，由链的**根**决定（主循环跳过链内子骨只记 `hops`，循环后把 `resolved[parent]` 反复传播下来，父骨始终未解析则退回旧规则）。**不是**"一个祖先一根骨"（Lunalice 裙子有 8 根**独立**挂脊椎的孤儿，合法分到 `chest`/`spine2`）；**不能用距离阈值**（飘带链 `d_best/d_anc` 沿链平滑爬升 0.25→0.96，任何阈值都把链从中间切开）。效果：渗漏 **3016→0**，飘带 3789 顶点全为单骨权重 ⇒ 零撕裂。负对照：Lunalice 重跑 `model.npz` 逐字节相同；把链规则改成 `if False and …` 重跑 ⇒ 与修正前逐字节相同（`734b7ac4…`）。
- **第 67 轮「小臂橡皮筋」根因**：皮肤 `shinano_body` 的 hand 权重是**沿「肘→腕」单调爬升的斜坡、全程无刚性段** ⇒ 整条小臂都听手腕骨。修法（`reweight_forearm.py`）：ROI = 距「肘→腕」轴 <10cm；ROI 内连通块沿轴跨度 <60mm ⇒ **刚性小块**取斜坡上单一比例；其余按 hand 质量重分配到 `smoothstep(0.78L,0.92L,t)`；差值**只在「手骨子树 ↔ 同侧肘骨」间搬运**（肘不够借 shoulder）。**这条 40 mm 窄带就是第 68 轮 bug 的来源。**
- **第 68 轮「腕缝拧转撕裂」**：用户报「动臂+腕时腕缝接不上，最坏约一个手腕的距离」，并问「我们是不是缺了一份骨骼？」⇒ **不缺**：工作骨架与导出 unit 都是 **107 根、1:1 对应**，`l_hand_twist` 的 parent/head/children 与原版完全一致（截图里 `0xA5C8115B` 是**小臂区域未命名骨**，原版对应位是 `l_arm_culling`）。真因 = 第 67 轮窄带 + 我们对 `l_hand_twist` 零权重。
- npz 指纹（第 65/66 轮）：`out/lilium`=方案B `8070eacd…`、`out/lilium-g05`=第65轮交付版 `a7f22301…`、`out/lilium-before-chainrule`=`734b7ac4…`、`out/blender`=Lunalice `990f588d…`。反证基线：`dump_blender.py` 默认 → geometry `05e8f5f876a2c32e` / weights `ae49cfeeb8329340` / 121704v·159363t·107骨 / rest `7bb46590ac416116`。

## 工程
- 管理器 WPF/net10、CLI net10；Core/Adaptation/GameItemAssets/SourceModel/SourceMigration net8。CoreServices 唯一装配点，SourceModel 零项目引用。Nullable/ImplicitUsings/Deterministic、警告当错误（除 NU1901..1904）。legacy 包需无 Lua 可用；**运行时无建网格/改权重接口，迁移离线做**。
- 自定义数据属模组库，简单配置属 `settings.json`，可重算缓存属管理器目录。
- **覆盖语义**：patch 序号大的后加载，覆盖同 AssetKey 的较小者。

## 材质（第 60 轮 M4–M8）
- 护甲必须 `advanced`（basic = "trash bag prop"）。advanced 自有槽 **2**=Normal/AO/Roughness（RGB 法线 / Alpha 粗糙度）、**3**=Metallic、**5**=Color/Emission Mask（RGB 底色 / Alpha 自发光，须=0）；其余 8 槽留模板自带游戏贴图。槽位键与 armorlut/basic **零重合**，键不对 ⇒ 贴图永远绑不上 ⇒ 全黑。
- 透明 = **材质父类属性**，不是贴图属性。`alphaclip` = "supports an alpha mask"，3 槽 0=Normal/AO/Roughness、1=Alpha Mask、2=BaseColor/Metallic，`OpacityThreshold`=0.5。**白=可见、黑=透明**，无部分透明。遮罩在 **RGB 灰度**里，不在 alpha 通道。`translucent` 无底色槽；`armorlut` 无 alpha 槽。
- 源侧：透明在 **VTF alpha**（DXT5），VMT 用 `$translucent 1` / `$alphatest 1` opt-in。**同一 `$basetexture` 被多个 VMT 共用 ⇒ 必须遍历模型自己的 92 槽，不能只看每组主 VMT**。
- 源侧 92 槽全是 **NekoToon**：`底色×光照 + matcap×$matcaptint`（`$matcap 2`=相加）。装饰件 38 槽底色是 1×1 (15,15,15) 近黑，观感全在 matcap（`11` 82% 亮银 / `6` 14% 宝石 / `Matcap8` 4% 珍珠）。只搬底色 ⇒ 纯黑。
- M8 两条数据驱动规则（零硬编码）：**A** `opts_in`（组内任一 VMT 生效 translucent/alphatest）且 `has_alpha`（VTF alpha 跨度≥3/255 且 <250 像素≥0.5%）→ alphaclip + alpha 遮罩@0.5；否则不动。**B** `max($matcaptint)≥0.9` → 底色 += matcap 三角加权均值×tint，金属度 191、粗糙度 89；<0.9 一字节不改。
- 一个 HD2 材质只能一个底色 ⇒ 组内多 matcap 只能加权平均；Unit 材质表 14 项已实机验证，**不能改数量**。
- VTF 布局：`[16×16 缩略图 128B][mip N-1]…[mip 0]`，缩略图在链首、mip0 在尾部；`hd2_vtf.VTF.read` 从尾部剥。
- UV：HD2 约定 `row = v*H`（无 V 翻转），游戏采样 **uv0**（uv1/uv2 无关）。判形状必须用 UV 三角覆盖图（`_probe_uvplot2.py` 按三角形整体取整数偏移），包围盒不算。
- **🔴🔴 第 71 轮：「起源→HD2 发灰发白」的根因 = shader 不同，不是我们的 bug**（用户提问，只诊断未改）。
  - **底色链路逐像素忠实**：`_probe_colorgamma.py` 源 VTF 的 sRGB 包装 vs 我们用的 linear 包装，**DXT1/DXT5 比值全 = 1.000**（只有 `_n` 法线图 ~0.67，那是**正确的**，法线是线性数据不该 sRGB 解码）。`_probe_color_faithful.py` 源色 vs 出货色逐组对照：皮肤/衣服 6 组比值 **0.999~1.001**；只有 4 组 matcap 注入组（gray/gray1/mat_diamond_b/ribbon_1）被我们**主动**加亮（比值 1.46~10.5），是 M8 规则 B，不是 bug。视差图 `E:/tmp/r71-color/src_vs_shipped.png` 已肉眼确认。
  - **真凶 = L4D2 NekoToon 把 albedo 乘一个 ≤0.30 的系数**：全部 46 个 VMT 是 NekoToon，公式 `final = albedo × lightwarp(lighting) × $lightingscale(0.8) + matcap × $matcaptint(.31)`。**实测 `lightwarp` 上限只有 ~0.30**（facewarp/basewarp 76/255、skinwarp 64/255，全曲线只在 55~76 之间）。⇒ L4D2 里 albedo 被压进 0~0.30 ⇒ 白不过曝、黑不发灰、暗处也看得清。
  - **HD2 `advanced` 是标准 PBR**：baseColor 直接当 albedo、全场光照、**无 0.30 天花板、无 per-material `$lightingscale`**。同一纹素：皮肤中间调 L4D2 亮 70 vs HD2 亮 245；白布 79 vs 255（饱和）；近黑布 30 vs 32（几乎不变）。示意图 `E:/tmp/r71-color/why_washed.png` 已肉眼确认，与用户描述完全吻合。
  - ⚠️「黑色变淡灰」不是被乘亮，是 **HD2 环境光/曝光把暗部抬起来**（L4D2 暗部被 0.30 压住）。
  - **原版 HD2 贴图全是线性 `_UNORM`，从无 `_SRGB`**（RE-2310 27 张：R8G8B8A8_UNORM×9 / R16G16B16A16_FLOAT×8 / BC7_UNORM×7 / BC1/BC5/BC3 各 1）。我们用 `BC7_UNORM_SRGB(99)` 标签**不一定错**（采样时转线性，理论等价），但**与原版不同，备查**。
  - 工具：`_probe_colorgamma.py`、`_probe_color_faithful.py`、`_probe_skin_color.py`、`_probe_washedout_math.py`、`_probe_why_figure.py`、`_probe_vanilla_tex3.py`；输出 `E:/tmp/r71-color/`。
  - **可选对策（未做，待拍板）**：① albedo 乘 0.3~0.5 压暗（最直接，牺牲 PBR 张力）② 降底色亮度/提高 roughness ③ 补 HD2 侧 AO/暗部解决"黑变灰"。**不要改 gamma 标签**（已证明无 bug）。

## 通用转化链路（第 63 轮，Lilium 验证）
- **三段链**（全部 CLI 驱动、默认值 = 原 Lunalice 值）：
  1. `dump_blender.py --vpk <模型VPK> --model survivor_teenangst --out <dir>` → `model.npz` + `meta.json`（纯 Python，不需要 Blender；Blender 只用于目视 `.blend`）。
  2. `build_patch.py --npz … --source-vpk … --source-model … --addons … --model-vpk … --pack <每个材质VPK，可重复> --out … --name …` → basic 模板 patch（**结构与几何验证用**）。
  3. `fix_materials.py --src … --manifest … --template advanced --alpha-zero` → advanced；再 `fix_appearance.py --src … --out … --source-vpk/--addons/--pack …` → 加透明 + matcap（= M8 级外观）。
- **`--pack` 顺序即覆盖顺序**（后者胜），字母语义（A1/B/C/D/E/F）**工具完全不知道**，只按列表读。Lunalice：D发色/E瞳色/F描边；**Lilium：D描边/E发色/F瞳色** —— 同一套参数化照跑。
- **🔴 已修 bug**：`build_patch.py` 的 unit id 曾是字面量 `'unit/lunalice-rigtest'` ⇒ 任何源包都得到同一个 Unit id，两包同装会静默互相覆盖。现为 `'unit/' + args.name`。**新包必须给不同 `--name`**。
- **Lilium 源**：118014v/154066t/**124骨**（Lunalice 121704/159363/127，**严格子集，只少 3 个未用 `source_extra_*`**）；4 个透明组**全是 `$alphatest`** ⇒ 遮罩精确（Lunalice 那 7 组是 `$translucent` ⇒ 只能近似）。`gray`/`gray1`/`ribbon_1` 是 1×1 占位贴图。
- **待办**：贴图 id 只取 basename（`tex/base/<basename>`，材质 id 含目录故安全）⇒ 跨包同名贴图会撞车。要加「包族命名空间」，但**必须保留同族内同名覆盖**（第 61 轮 7 个 MAT 变体包靠它工作）且不能改既有 id。

## 材质变体包（第 61 轮）
- 八人包材质拆成**独立 patch**（`split_materials.py`，内建反证：默认包重跑必须逐字节复现 M8）：7 个已落 `E:\mod\Lunalice-RE2310-MAT-*`（B2服装色黑 / C1金属色金 / D2-D4发色 / E1-E2瞳色）。
- **覆盖前提已逐条核对**（`_probe_override_ids.py`）：7 包全部条目 id 都在 M8 内、type 一致 ⇒ 纯覆盖。**同族互斥**（`_probe_mat_overlap.py`）：D2/D3/D4 三选一、E1/E2 二选一；B2 与 C1/D*/E* 零重叠 ⇒ 服装色+金属色+发色+瞳色可同时开。
- **入库靠自动发现，不动 library.json**：`ModLibrarySynchronizer` 枚举库根全部顶层文件夹，含 base patch 即成节点；`ShellViewModel.RunStartupChecksAsync:1155` 启动自动同步。
- **蕾丝普查**：7 组真半透明（源侧全 `$translucent` 无 `$alphatest`），但 `alphaclip` 只能硬裁切、`translucent` 无底色槽 ⇒ 带纹理护甲**无法软透明**，0.5 阈值是近似。
- **材质开关 31 个 VPK 未实现**：仅 1 个可整组表达，29 个 PARTIAL。路①拆 Unit 材质表（根治、动已验收结构）②UV 遮罩涂黑（装饰件 38 槽共用 1×1 贴图，不可行）。

## 源侧组件开关（第 62 轮，L4N）
- **L4N 的「部件切换」= Source 原生 `$BodyGroup`**（不是贴图伪装）。权威：游戏根 `readme_l4n.txt` + `left4dead2/neko/mdl_extension.qc`。`$BodyGroup "名" { studio "a.smd" blank }`，**N 选一、`blank` 是隐藏项**；`$TextureGroup "skinfamilies"` = 材质预设；`$KeyValues { "left4neko" {...} }` 只管显示名/足 IK/默认缩放。平台是客户端 DLL 注入（dxgi.dll）。**`l4n_survivor` 是另一条线**（八人包→以玩家为单位的模型池），别混。
- **源模型实测**：Lunalice 主模型 **32 bodygroup = 30 可切换 + 2 固定（head/Base）**，顶点 121511，**顶点区间两两不相交 ⇒ 按 bodygroup 拆分干净**。三个 mdl 全 `numskinfamilies=1`。
- **31 个 `XXX关` VPK = 每个 bodygroup 的材质层降级方案**；因材质槽被多部件共用（`Ornament_metal_Underwear` 被 38 槽引用）必然连带隐藏 ⇒ 这就是 29/32 PARTIAL 的根因。**bodygroup 精确、材质泄漏。**
- **HD2 侧映射已成立**：`armor-registry.applied.json` 的 `models[].pieces[]` = `(body,unit,slot,pieceType,tone,enabled,…)` ⇒ **按 unit 逐个开关是现成能力**。`SlotFor`：Head 0 / Torso·Shoulder·Accessory 2 / Pelvis 3 / LeftLeg 4 / RightLeg 5 / LeftArm 6 / RightArm 7；`pieceType` = 同 (slot,body) 层号自增。
- **唯一未知数 = piece 数量上限**：原生 RE-2310 = 23、RE-1861 = 15、自定义 = 19；「expanded 19 piece」曾启动崩溃但未定因。⇒ **先拆 4~6 个实机验证再逐步加**。详见 `tmp/rig-lab/L4N_COMPONENT_ANALYSIS.md`。

## Lua 运行时动态切换盔甲 unit（第 63 轮分析，只记录不写码）
- **能力已完整存在，不需要新协议字段/新 Lua 接口** = 「重写协议文档 + revision 自增」，Lua 每秒轮询全文、重新规划、**只重建签名变了的那一个目标**。缺的只是管理器 UI/工作流。
- 协议键：`model_piece=<model>,<body>,<unit>,<slot>,<piece_type>,<tone>[,<enabled>]`、`piece_override=<owner>,<body>,<unit>,<slot>,<piece_type>,<enabled>`、`target_mode=<item>,follow|manual`、`composition_mode=full|existing_only`。
- **位置唯一性**：同一 `(body,slot,piece_type)` 只能一个 Unit ⇒ N 个可独立开关的部件需要 N 个不同 `piece_type`。合成时 `piece.enabled` 与 `piece_override` 两个开关串联；护栏 `#rows 1..16`、`total≤4096`。**签名 = 规范载荷，不含 revision**；**已发布 buffer 永不释放**（32 MiB / 8192 上限）⇒ 反复切换会累积内存。待做 P0~P4 见 `tmp/rig-lab/LUA_UNIT_SWITCH_TODO.md`。

## 交付与部署
- 链路：`hd2mod library sync` 注册新包（只动节点，先备份 `library.json`）→ `armor-normalize`。`armor-normalize` **只认原生 Unit id**（改名 id 一律 `armors:[]`）。
- **CLI 用法（实测）**：`library sync` 与 `equipment armor-normalize` 都是 `inspect|plan|validate|apply` 四段；**只有 `inspect <GUID>` / `plan <GUID>` 带 GUID**，`validate --plan` / `apply --plan … --output <目录> --confirm` **不带 GUID**；`apply` 的输出目录**必须不存在**。带空格的 `--game-data` 在 bash 里要用数组/引号，否则被拆词。写入命令会被 `ManagerProcessRunning` 拦 —— 先确认那个 PID 是否真还在。`gamedata extract asset|archive|show|list`（unit TypeID = `0xe0a48d0be9a7453f`）。查 64 位 id 要转有符号 64 位（SQLite）。
- **符号链接铁律**：`New-Item -ItemType SymbolicLink` 本机失败（无权限）且先删后建 ⇒ 一律 `MSYS=winsymlinks:nativestrict ln -s`，先建后删（写 .tmp 再 `mv -f`）。
- **id 公式**（决定覆盖能否生效）：`make_id(prefix,name) = (prefix<<60) | (sha256(name)[:8] 小端 & 0x0FFF_FFFF_FFFF_FFFF)`；`0xB` 贴图 / `0xA` 材质 / `0xD` unit。底色 = `make_id(0xB,'tex/base/<basetexture>')`、遮罩 = `tex/mask/<stem>`、装饰件 = `tex/albedo|rough|metal/<stem>`。
- 当前 Lunalice：**M8 已部署**（`E:/mod/Lunalice-RE2310-M8/`，patch 573384 B、gpu 101546624 B），回退目标 M7；回退记录 `E:/tmp/deploy-revert-note.txt`。候选源 `E:/tmp/rig-lab/cand-m8/`。
- 长期工具：`fix_appearance.py`(M8 构建)、`fix_materials.py`、`fix_textures.py`(外科替换范式)、`_probe_matfix/_probe_matchain/_probe_canddiff/_probe_weightdiff`、`_probe_pkg8*/_probe_lace`。
- 待办（暂不做）：军械库渲染模式支持 alphaclip；管理器直接导入 VPK 作一等公民（翻译成 overwrite 里的 patch）。

## 直接替换原生护甲（第 59 轮建立，第 64 轮用于 Lilium）
- **工具 = `hd2mod equipment armor-normalize`**（**不是** cross-armor）。按 `ArmorNormalizationService.cs`「The archive is the visibility boundary」**自动把目标护甲 archive 里除承载位以外的全部 unit 换成隐藏占位**（`CanonicalPlaceholderMinifier`：3 顶点、0.0001 m 三角形、保留 culling 壳）。
- **`build_patch.py --unit-mode native --native-unit 0x…[可重复] --native-dir <游戏 unit toc 目录>`** 把整模型写进**真游戏 unit id**，每个 id 带自己的 240 B `customization_data`。`pieces` 在 native 模式下硬编码 (3,0,0) 且 protocol 写 `enabled=false` —— 只是记录，不生效。
- **`--unit-mode` 四选一**：`fresh`(自造 id，需 Lua) / `native`(真 id) / `same-key`(9 个真身体 id，整模型会**画 9 份**) / `both`。单 mesh 模型要用 `native` + 只落一个部位。
- **RE-2310「荣誉卫队」结构**（归档 `d923906f8b815987`）：**22 unit** = 8 身体（Torso/Pelvis/LeftArm/RightArm × Slim/Stocky = `cae35ee15168145f`/`9841b16544c8fbae`/`17da843a4908511e`/`45d79d84b8933a7f`/`91eb7be84016ceab`/`9a5e6dc74cdb08a3`/`7336c6b662522f0e`/`4f7bce4ca49ccda8`）+ 14 其他部位。完整替换包 = 22 + 1 个**借来的 Head id** `af431352d9b95e83` = 23 条目。`SelectPair` 约束：单 unit 只能落 `BodyVariant==Any`；RE-2310 躯干只有 Slim/Stocky ⇒ **必须一次给两个身形**。
- **已知瑕疵（管理器侧，非我们代码）**：CLI 的隐藏占位 unit 会丢掉「零索引 section」的材质短 id（`num_indices==0` ⇒ 不绘制、功能惰性；原版本身也有同类悬空引用如 `0x093fc934`）⇒ 不阻断。审计因此恒为 **27 项 26 通过 / 1 已知**。
- 工具：`_probe_nativeids.py`、`_probe_dangling.py`、`_probe_render_patchunit.py`（**从出货 patch 字节里**渲染 unit；`ti.matrices` 行主序 `p'=[p,1]@M`，可见 LOD 是单位阵）、`_verify_normalized.py`、`_probe_weightdiff.py`、`assemble_re2310.py`（组装交付目录，哈希现算）。
- **🔴 `assemble_re2310.py` 会覆盖 `preview/`**：它从 `Lilium-Sancta-Native-RE2310/preview/`（只有 8 张 mask/metal）`rmtree+copytree` ⇒ 每次组装都会删掉新图。**对策：要留的图同时放进 Native 包的 preview/**。`E:/tmp/lilium-an-out` + `E:/tmp/lilium-an-plan.json` 是它的**硬编码输入**，换产物时要先把旧的挪走、再把新规整目录改名成这两个名字。
