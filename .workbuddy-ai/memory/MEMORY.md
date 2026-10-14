# HD2ModManager 项目长期记忆

> 论证链在各轮交流文档 `tmp/rig-lab/ROUND*.md` 与每日日志 `.workbuddy-ai/memory/YYYY-MM-DD.md`。本文件只留**规则**与**当前状态**。

## 约定
- 仓库 WpfApp1；只本地提交不推送。用户非程序员，讲通俗话；用户负责视觉验收，助手负责数值与自动检查。
- 临时脚本放 `tmp/rig-lab/`（gitignore 只放行 `*.py`，`*.md` 需 `git add -f`），不放 `_diagnostics/`。`docs/` 忽略，改前备份 `docs/_backup/`。不删 `.workbuddy-ai`。故障包与依赖留版本+hash，候选另存目录，不覆盖唯一证据。一律绝对路径。
- 修 bug 必做"破坏关键行"负对照；删代码同步改测试。**子代理 = 用户另开线程 + 交流文档（`*.md` 放 `tmp/rig-lab/`），不用 Agent 工具。**
- 路线：Python 快循环 → 离线 legacy patch 包 → 用户实机 → 确认后上 C#。只借 BatchTool 变换；不依赖 HD2IRG1 动态骨骼；**无 Lua 也必须能跑**。**发图前自己先肉眼看过。**
- **`tmp/` 暂时保留**（第 66 轮拍板）：等所有功能移进正式 C# 类库后再清理；新写的东西仍放 `tmp/rig-lab/`。
- 凡改过交付目录里的 patch/gpu 字节，**必须跑一次 `library sync`**（四段式，见「交付与部署」）。

## 环境
- Python `C:/Users/Administrator/.workbuddy-ai/binaries/python/envs/default/Scripts/python.exe`（numpy/Pillow/scipy/lz4，**无 matplotlib**）；Blender `D:/SteamLibrary/steamapps/common/Blender/blender.exe`；7z `"/c/Program Files/7-Zip/7z.exe"`。
- SDK `docs/HD2SDK-CommunityEdition-3.8.0`（texconv 在 `deps/`）；游戏 data `D:/SteamLibrary/steamapps/common/Helldivers 2/data`；源素材 `D:/SteamLibrary/steamapps/common/Left 4 Dead 2/left4dead2/addons/`。**源包目录**：Lunalice（`hd2_source.Packs` 默认）/ `E:/tmp/lilium/` / `E:/tmp/mourning/` —— **用后两者必须显式传 `addons=`/`packs=`/`model_vpk=`**。
- 原版抽取 `E:/tmp/hd2-mgroot/`；mod 库 `E:/mod/`（`library.json` 在**库根**）。
- CLI `HD2ModCli/bin/Debug/net10.0/hd2mod.exe`；管理器索引库 `E:/tmp/hd2-mgroot/data/indexes/asset-index.sqlite`。

## 🔴 当前交付状态
### Lilium-Sancta-RE2310（第 69 轮定稿，第 71 轮实验已回退）
- **`E:/mod/Lilium-Sancta-RE2310/`**：patch 562176 B sha256 `5e4e045c3e1cbc7c…`（第 68 轮起逐字节未动）；gpu 84229248 B sha256 **`e9b0bb91…`** = 第 69 轮原始字节。
- 64 条目 = 22 unit(2 承载 + 20 隐藏) + 2 bone + 11 material + 29 texture。审计 **27 项 26 通过 / 1 已知**（唯一已知 = 管理器隐藏占位 unit 丢零索引段材质短 id）。
- **🔴 秒级回退点 `E:/tmp/round69-final-rollback/`**（sha 已核）。回退 = `cp` 两文件回交付目录 ⇒ 跑 `library sync` 四段。**不要删。**
- 中间产物 `E:/mod/Lilium-Sancta-Native-RE2310/`（**勿启用**）。库节点 `9ff82517`(成品) / `a74a1022`(中间)。
- 历史 gpu：71 各档 mul 0.78/0.55/0.40、compress、gamma（`E:/tmp/r71-dark*/`，**已否定**）/ 70 twist `1ca095d5…`（已弃）/ 68 `de5f5dd1…` / 67 `bb6e3008…` / 66 `a8854ba3…`；65 patch `52d131c5…`+gpu `6587ac4f…` / 64 `729c4a65…`+`1f20c9cd…`。
- **Lilium 其余 7 个角色未转**（只转了 `Lilium_Sancta_Zoey.vpk`）。

### MourningVeil-RE2310（第 72 轮 · ✅ **已 sync**）
- **`E:/mod/MourningVeil-RE2310/`**：patch 581484 B sha256 `e4ed9d01765e13ad180f7ab6af86a99b2bc8703bd43c4c35e95d369a59c23cb8`（**第 6 步前后不变**，权重在 GPU 侧）；gpu 117215104 B sha256 **`2432fc5e946a6b6694e58b5848297a64fc719b89d0539223e217e7ae0d748bff`**（第 6 步补跑后；补跑前 `4880a066…`）。
- **82 条目 / 22 unit = 2 承载（`cae35ee15168145f` Slim + `9841b16544c8fbae` Stocky）+ 20 隐藏**。审计 27/26+1 已知。
- 源 `E:/mod/MourningVeil-Native-RE2310/`(62 条目/2 unit，GUID `5eeb135a-f6bb-4a87-b106-7061dede1f3b`)；apply 输出 `E:/tmp/mourning-an-out/`；plan `E:/tmp/r72-an-plan.json`。
- 预览 `preview/ship_overview.png` + `forearm_roll_before_after.png`(90°) + `forearm_roll_before_after_180.png`（**均已肉眼确认**）。README.txt 已写（含第 6 步与 twist_frac 三档）。
- 新工具 `tmp/rig-lab/assemble_mourning.py`（参数化版，取代 Lilium 硬编码常量）、`tmp/rig-lab/_make_r72_preview.py`、`_probe_mv_profile.py`、`_probe_roll_whole.py`。
- **🔴 秒级回退点 `E:/tmp/r72-ship-rollback/`**（第 6 步之前的两文件，sha 已核）。**不要删。**
- 库节点 **`185f279d-0ca0-4acd-a87d-6f2aa115e7b0`**，contentFingerprint `1aa64bbd…`。
- 用户实机：**「结果相当好」**，但第 66 轮小臂问题复现 ⇒ 已补跑第 6 步（见「手臂权重」节）。
- **仅转 Zoey 一个角色**（八人包其余 7 个未转）。

## 🔴 头盔 = 独立 archive（第 72 轮查清，解决历史矛盾）
- **RE-2310 一套护甲 = 两个 archive、两个 item**：
  - 躯干/四肢 archive `d923906f8b815987`，category **Armor**（22 unit）
  - 头部头盔 archive **`b4d15b145b3f7677`**，category **Helmet**（**只有 1 个 unit = `0xaf431352d9b95e83`**）
  - 两者 displayName 都叫 `RE-2310“荣誉卫队”`，是独立记录。第 35 轮渲染图判据**正确**，第 13 轮"不是头盔"的更正**作废**。
- **`armor-normalize` 永远管不到头盔**（`ArmorNormalizationService.cs`）：第 39 行只遍历 `Category == "Armor"`；第 52 行注释原文 *"The archive is the visibility boundary: every non-target Unit belonging to this armor must be hidden"*；`hidden` 按 **ArchiveId 精确匹配**取 `TargetCandidates`。⇒ **想隐藏头盔，patch 里必须有头盔那个 unit**。
- 另有 `equipment helmet-merge`（要求 patch 同时含 Armor+Helmet 集合）与 `host-derived plan helmet enable|disable`（只作用于 mod 自己带头盔的场景）—— **两条都不适用**。`host-derived` 里有 **`Overwrite/` 目录**概念（`<mod>\Overwrite`）+ `hasHelmetMarker`，备查。
- 官方 `custom-model convert <GUID> <来源Archive[,头盔Archive]> <素体名称>` 自带头盔 archive 参数，但走 **RuntimeArmorRegistry（Lua 运行时）路线**，与离线 patch 路线不同。
- **用户已自备隐藏头盔 mod**：`RE-2310“荣誉卫队”隐藏头盔`，GUID `0532b11b-e0a7-4587-a948-8194691285dd`（CrossArmor 隐藏 `af431352d9b95e83`，patch 5500 B）。⇒ **部署顺序：替换包在前，隐藏头盔 mod 在后。**
- **CLI 四缺（已交另一线程）**：① skill 没写"护甲头盔在另一 archive"；② 替换护甲不能引用目标头盔 archive；③ 无"生成自定义 id 隐藏 unit"；④ 无装备注册表搜索（item↔archive↔assetkey）。

## 🔴 材质冲突/共存机制（id 决定覆盖）
`make_id(prefix,name) = (prefix<<60) | (sha256(name)[:8] 小端 & 0x0FFF_FFFF_FFFF_FFFF)`；`0xB`=贴图 / `0xA`=材质 / `0xD`=unit。
- **材质 id** = `make_id(0xA, 'mat/' + <name>)`，`name` = `$basetexture` **全路径**。
- **底色贴图 id** = `make_id(0xB, 'tex/base/<basename>')` —— **只取 basename**。
- **冲突（互斥）**：两包提供**同名 `$basetexture`（同 basename ⇒ 同 id）但内容不同** ⇒ 后加载覆盖前者。
- **共存**：提供**不同 basename** ⇒ 不同 id。
- MourningVeil 实测：`m_danahaireri_white` 被 C1|C2 共享（同名不同图）⇒ **C1/C2 二选一**；`mat_accgem`/`mat_accmetal`/`mat_clothb`/`mat_gem1_shoes` 被 B1|B2|B3 共享（同名同图）⇒ **B1/B2/B3 三选一**（决定服装色）。A1 主体 / D 瞳色 / E 肌肤各提供不同贴图名 ⇒ 可共存。
- **用户强调**：「替换皮肤的材质就需要几种材质均使用同样的 id」⇒ 同一张皮肤被多个 VMT 引用时，必须映射到同一 basename，否则 id 分散、替换只生效一部分。
- **待办**：贴图 id 只取 basename ⇒ 跨包同名贴图会撞车。要加「包族命名空间」，但**必须保留同族内同名覆盖**且不能改既有 id。

## 🔴 手臂权重：第 66~70 轮这条线（**第 69 轮定稿，第 70 轮已回退，不再尝试**）
「小臂从纯肘骨过渡到纯手骨的权重带有多宽 / 挂哪根骨」= 唯一在动的参数。

| 轮 | 参数 | 带宽 | 180° 拧转最差边 | 备注 |
|---|---|---|---|---|
| 66 | `smoothstep(0.22L, 0.82L)`，全挂 `l_hand` | 0.22–0.82 | 41.3 mm | 手感"橡皮筋"（软） |
| 67 | `smoothstep(0.78L, 0.92L)`（仅 40 mm） | 40 mm | **83.2 / 100.0 mm 真撕裂** | 网格边 32.8 mm 横跨整条带 ⇒ bug |
| 68 | `smoothstep(0.50L, 1.00L)` | 144 mm | 37.8 mm（几何地板） | 撕裂移到小臂中段 |
| **69** | `smoothstep(0.22L, 1.00L)`，全挂 `l_hand`（`--twist-frac 0`） | 63–287 mm | 37.8 mm | **定稿**：66 起点 + 68 终点 |
| 70 | 同 69，远端 50/50 分给 `l_hand`/`l_hand_twist` | 同 69 | A 54.2/46.6 | **实机"手指变差"⇒ 已回退** |

**🔴 第 72 轮：第 6 步不在主链里（重大流程缺陷）**
- **五步主链** `dump_blender.py → build_patch → fix_materials → fix_appearance → armor-normalize`
  **没有调用 `reweight_forearm_twist.py`**（`grep -rln "reweight_forearm" --include=*.py` 全空）。
  它是**独立后处理步骤**，第 68/69 轮是给 Lilium **单独补跑**的。
  ⇒ **MourningVeil 首版从源头漏了**，出货的是「源模型 L4D2 的 LBS 权重经 `resolve()` 原样映射」
  （Valve 把小臂整片挂 `L_Forearm` ⇒ 映射后整片挂 `l_elbow`）。用户实机复现第 66 轮的手感。
- **MourningVeil 首版实测剖面**（L=287.4 mm）：交界带落在 **t/L 0.1~0.4（非常靠肘）**，
  远端 0.8~0.9 已是 `hand 0.995`。补跑 69 参数后交界带推到 **0.3~0.9**；左右各 1107/1113 = **2220 顶点**。
- **⚠️ 迁 C# 时小臂权重必须是链路一等公民**，不能靠人的记忆补。

**🔴 `--twist-frac` 三档实测（第 72 轮，决定性数值证据）**
180° 拧转、**整条小臂**（`_probe_roll_whole.py`，旧 `_probe_wrist_roll.py` 只看 `t>0.75L`、对小臂中段是盲的）：

| twist_frac | med | p99 | MAX |
|---|---|---|---|
| **0.00** | 7.09 mm | **29.23 mm** | 42.0 mm ← 定稿 |
| 0.35 | 5.49 mm | 36.15 mm | 44.1 mm |
| 0.60 | 3.80 mm | **58.23 mm** | 64.5 mm ← 明显更差 |

⇒ **远端权重分给 `l_hand_twist` 在「游戏只转 `l_hand`」前提下会制造新撕裂**（不转的骨上权重"卡住",
与手骨撕开一道缝）⇒ **这就是第 70 轮实机"手指变差"的数值解释**。
（`--twist` 模拟「twist 骨也转」，三档结果完全一致 ⇒ `l_hand_twist` head 与 `l_hand` 重合、无法区分。）

- **🔴 第 70 轮实机结论**：落在**假设 A**（游戏**只转 `l_hand`**，`l_hand_twist` 几乎不动）⇒ **twist 骨对本模型无收益，`--twist-frac 0.0` 为定稿，此方向不再尝试。**
- **🔴 几何地板 37.8 mm（改不掉）**：180° 拧转下即使边两端权重完全相同，长度也变 `2 × 垂直分量`。最差等权边（`comp 3772`，半径 47/28 mm）= 37.8 mm ⇒ **任何权重方案的下界**。根治要**加密腕部网格**（新机制，未做）。
- **🔴 HD2 骨架有 twist 骨**：`l/r_hand_twist`（slot 64/70）、`l/r_shoulder_twist`、`l/r_forearm_twist`、`l_wrist_twist`。`l_hand_twist` parent = `l_elbow`、head 与 `l_hand` 重合、叶子骨，**我们导出 unit 里权重 0**。
- **原版用 twist 骨**：原版左臂护甲 `unit_0x4a213e0ae8acf74b.toc`（归档 `aaebfbedc571fd7a`）`l_hand_twist` 权重 74.5；原版手臂皮肤 `g_torso_arm_l_male`（`0x586e33e020cbf80f`）`l_hand_twist` 65.2 / `l_hand` 61.8。**原版没有 `l_forearm_twist`。** 原版手部剖面：腕部 t/L 0.93~1.08 hand 与 twist 同时有权重；指尖 t/L>1.3 是 ~100% 手指骨 ⇒ **"手和手指一起纳入"必须理解成"只重分配非手指部分"**。
- ⚠️ 原版 unit 的 `ti.matrices[ti_idx]` head 与主导顶点质心**对不上** ⇒ **原版 unit 的 matrices 语义与我们的导出 unit 不同**，读原版权重别用 head 做几何判据。
- 工具：`reweight_forearm_twist.py`(68/69/72)、`reweight_arm_twist.py`(70)、`reweight_forearm.py`(67)、`_probe_twist_roll.py`、`_probe_wrist_roll*.py`、**`_probe_roll_whole.py`(72 整条小臂)**、`_probe_mv_profile.py`(72)、`_plot_wrist_follow.py`、`_probe_vanilla_wrist.py`/`_probe_vanilla_hand.py`、`_probe_forearm_action.py`。
- **🔴 `_probe_forearm_action.py` 盲点**：只测腕部弯折，**从未测拧转/旋前** ⇒ 第 67 轮"0.0 mm 全绿"掩盖了拧转撕裂。**以后验收必须加拧转项。**
- **🔴 `_probe_wrist_roll.py` 盲点（第 72 轮补）**：选区是 `t > 0.75L`，**对小臂中段的撕裂完全看不见**。已由 `_probe_roll_whole.py`（`0.05L < t < 1.05L`）补上。**渲染图里"拧成一条线"可能是均匀变形的视觉假象，必须配 p99/MAX 数值才能判"撕裂 vs 均匀"。**

- **🔴 `_probe_vanilla_wrist.py` 突破**：CLI 的 `GameDataPackageResolver.GetPackageResourceAsync` 对 bundled 归档**所有分区都用基名找包**，但 `bundles.nxa` 里一个归档是**三个包**：`<id>`(TOC) / `<id>.gpu_resources` / `<id>.stream`。后果：`gamedata extract asset` 抽原版 unit **只写出 `.toc`**。**未改产品代码**，探针自己实现 DSAR chunk walk + `bundles.nxa` 包表绕开。
- **权重只改 GPU 侧**：`_probe_weightdiff.py` = **63542/16917432 = 0.376% PASS**，只落 `bone_weight`+`bone_index`，声明字段之外 0 字节。
- **⚠️ 每轮重绑通用前提**：只重分配「本来挂在 `l_elbow`/`l_hand`/`l_hand_twist` 上的权重」，**手指骨权重一字节不动**。**不能按 `hand≥0.95` 排除**（手部皮肤与手指是**同一连通块**）。

## 模型与骨骼
- 骨架不动：`S_bind == rest_hd2`；移动/缩放角色只动网格。HD2 +X右 +Y前 +Z上，左肢在 -X，单位米。Unit 存行向量矩阵，平移 flat12..14；`read_hd2_rig` 转列约定（`hd2_patch` 近期去转置，调用者须核查）。
- 摆姿已全部验收（`hand_tilt=3`、`finger_pose=True`）。手指逐节残余摆姿 `Rl=min_arc(s,Q.T@t)`，枢轴须过 post，末节取 HD2 局部 +X；发布 `rest_hd2` 不变，`rest_sym` 是工作枢轴。
- 权重：六区 body_region+附件身份；同侧同指可信三角面重心转移+置信度回退；交界留渐变。`weight_half4.py` 先合并同骨再 2048 格点最大余数，121704 行 half 精确和 1。
- **🔴 坐标系纠错**：源侧 `short[nm]['world_m']` **已经是米**（顶点 `v['pos']` 才是英寸）；源轴是 **X = 左右（+ = 角色左）、Z = 上**，**不是** Valve 常规的「X 前 / Y 左」。**跨模型比较必须用管线自己的 `align_pts` + `t`。**
- **🔴 T-pose vs A-pose**：HD2 rest 是 **A-pose**，`rest_sym` 是烘进 T-pose 的工作骨架，源模型是 **T-pose** ⇒ **不能拿 T-pose 的源关节直接减 HD2 的关节**（400~570 mm 假误差）。
- **🔴 对齐机制**：`retarget` 1a/1b = yaw 拟合 `R` + 统一缩放 `s` + 平移 `t`；**缩放锚在"头顶高度"、平移锚在"头顶"**。

## 摆姿/权重常量
- Lunalice 目视值、Lilium/MourningVeil 沿用：`HEAD_SCALE=8/7`、`ARM_ELBOW_OPEN=6.5°`、`ARM_HAND_TILT=3°`、`MESH_SHIFT=(0,-0.100,-0.090)`、`upper_scale=1.12`、`leg_tilt=5.7`、`leg_splay=4.0`、`align=yaw`、`spine_map=mesh`、`fallback=region`、`skeleton=native`。
- **第 66 轮按模型拟合**：`retarget` 新增 `arm_scratch_upper_shift`，`delta = u * gain + shift`（`u` = T-pose 肩→肘单位向量，由 `rest_sym` 取；`shift` 按**左臂**给、右臂 X 取反），与 `gain` 走**同一套 ramp** ⇒ 肩缝钉住、`shift` 默认 0 时老行为逐字节不变。**拟合值 `gain = 0.0814 m`、`shift = (0.0014, 0.0097, 0.0005) m`**。手+指总位移中位 32.5 → 5.2 mm。
- **🔴 教训：拟合函数必须拿"出货量"当参考，不要拿中间量**。**🔴 基准只能在出货网格上量**（A-pose vs T-pose 直接相减是假误差）。
- **遗留**：拇指末节 12.2 mm、食指 ~4.8 mm 是**形状差**（源侧拇指段长短 9.3%、食指短 8.5%）⇒ 刚性平移修不了，要修得做「按指沿轴缩放」（新机制）。

## 材质（第 60 轮 M4–M8）
- 护甲必须 `advanced`（basic = "trash bag prop"）。advanced 自有槽 **2**=Normal/AO/Roughness（RGB 法线 / Alpha 粗糙度）、**3**=Metallic、**5**=Color/Emission Mask（RGB 底色 / Alpha 自发光，须=0）；其余 8 槽留模板自带。槽位键与 armorlut/basic **零重合**，键不对 ⇒ 全黑。
- 透明 = **材质父类属性**。`alphaclip` = "supports an alpha mask"，3 槽 0=Normal/AO/Roughness、1=Alpha Mask、2=BaseColor/Metallic，`OpacityThreshold`=0.5。**白=可见、黑=透明**，无部分透明。遮罩在 **RGB 灰度**里。`translucent` 无底色槽；`armorlut` 无 alpha 槽。
- 源侧：透明在 **VTF alpha**（DXT5），VMT 用 `$translucent 1` / `$alphatest 1` opt-in。**同一 `$basetexture` 被多个 VMT 共用 ⇒ 必须遍历模型自己的全部槽**。
- 源侧全是 **NekoToon**：`底色×光照 + matcap×$matcaptint`（`$matcap 2`=相加）。装饰件底色常是 1×1 近黑，观感全在 matcap。
- **M8 两条数据驱动规则（零硬编码）**：**A** `opts_in` 且 `has_alpha`（VTF alpha 跨度≥3/255 且 <250 像素≥0.5%）→ alphaclip + alpha 遮罩@0.5。**B** `max($matcaptint)≥0.9` → 底色 += matcap 三角加权均值×tint，金属度 191、粗糙度 89；<0.9 一字节不改。
- 一个 HD2 材质只能一个底色 ⇒ 组内多 matcap 只能加权平均；Unit 材质表 14 项已实机验证，**不能改数量**。
- VTF 布局：`[16×16 缩略图 128B][mip N-1]…[mip 0]`，缩略图在链首、mip0 在尾部；`hd2_vtf.VTF.read` 从尾部剥。
- UV：HD2 约定 `row = v*H`（无 V 翻转），游戏采样 **uv0**。判形状必须用 UV 三角覆盖图。
- **🔴 HD2 贴图资产结构**：`Texture.read(toc, gpu)` 把**整个 gpu chunk** 当 `pixels`；像素块起点 = `entry['gpu_offset']`。patch 容器（TocData）只存 **148 B DX10 DDS header**，不存像素 ⇒ **改贴图只动 `.gpu_resources`**。
- **🔴 `parse_vmt` 缺陷**：`hd2_source.parse_vmt` 把键小写化、只把白名单键放顶层，其余进 `_raw`；**不剥离行内 `//` 注释** ⇒ value 带尾随注释。`fix_appearance.active_keys()` 才正确剥离。

## 🔴 「起源→HD2 发灰发白」= shader 不同，不是转换器 bug（第 71 轮定论，**不再追究**）
- **底色链路逐像素忠实**：`_probe_colorgamma.py` 源 VTF sRGB 包装 vs 我们用的 linear 包装，**DXT1/DXT5 比值全 = 1.000**（`_n` 法线 ~0.67 是对的）。`_probe_color_faithful.py` 源色 vs 出货色：皮肤/衣服 6 组 **0.999~1.001**。
- **真凶 = L4D2 NekoToon 把 albedo 乘 ≤0.30 的系数**：`final = albedo × lightwarp(lighting) × $lightingscale(0.8) + matcap × $matcaptint`。**`lightwarp` 上限只有 ~0.30**（facewarp/basewarp 76/255、skinwarp 64/255）。
- **HD2 `advanced` 是标准 PBR**：baseColor 直接当 albedo、全场光照、**无 0.30 天花板、无 per-material `$lightingscale`**。同一纹素：皮肤中间调 L4D2 亮 70 vs HD2 亮 245；白布 79 vs 255。**「黑色变淡灰」是 HD2 环境光/曝光把暗部抬起来。**
- **原版 HD2 贴图全是线性 `_UNORM`，从无 `_SRGB`**（RE-2310 27 张）。我们用 `BC7_UNORM_SRGB(99)` 标签**不一定错**，但与原版不同，备查。
- **🔴 第 71 轮对策（压暗 albedo）已实机否定**：`darken_albedo.py` 的 mul/gamma/compress 三模式，用户反馈「只有整体变暗、仍发灰」⇒ **不要改 gamma / `_SRGB` 标签**（已证无 bug）。对策②③（降底色亮度、补 HD2 侧 AO）**未做，但已不再追究**。
- 工具：`_probe_colorgamma.py`、`_probe_color_faithful.py`、`_probe_skin_color.py`、`_probe_washedout_math.py`、`_probe_vanilla_tex*.py`、`_probe_mat_groups.py`、`darken_albedo.py`、`_probe_gpudiff.py`。

## 通用转化链路（第 63 轮，Lilium/MourningVeil 验证）
- **五段链**（CLI + py 混合）：
  1. `dump_blender.py --vpk <模型VPK> --model survivor_teenangst --out <dir>` → `model.npz` + `meta.json`（**必须在 `tmp/rig-lab/` 下跑**，它用相对路径 `avatar/…`）。
  2. `build_patch.py --npz … --source-vpk … --source-model … --addons … --model-vpk … --pack <材质VPK，可重复> --out … --name … --unit-mode native --native-unit 0x… --native-dir …` → basic patch。
  3. `fix_materials.py --src … --manifest … --template advanced --alpha-zero` → advanced。
  4. `fix_appearance.py …` → 加透明 + matcap（M8 级外观）。
  5. `hd2mod equipment armor-normalize inspect/plan/validate/apply` → 藏非承载 → `assemble_*.py` 组装交付目录。
- **`--pack` 顺序即覆盖顺序**（后者胜），字母语义（A/B/C/D/E）**工具完全不知道**。Lunalice：D发色/E瞳色/F描边；Lilium：D描边/E发色/F瞳色；MourningVeil：B服装色/C发色/D瞳色/E肌肤。
- **🔴 已修 bug**：`build_patch.py` 的 unit id 曾固定 ⇒ 两包同装静默互相覆盖。现为 `'unit/' + args.name`。**新包必须给不同 `--name`**。
- **第 72 轮修的 6 个真 bug**（都在 py 工具，非产物）：
  - `build_patch.py`：flat 分支 `white` 未定义（`UnboundLocalError`）→ 惰性 `flat_white()`；Valve 内建纯色贴图 `black` 找不到 → 新增 `BUILTIN_SOLID` 表 + `builtin_solid()`。
  - `fix_materials.py`：材质名对不上贴图名时的回退 → `solid/<stem>` / `flat_white`。
  - `fix_appearance.py`：3 张悬空 matcap 跳过+权重重归一；日志引用未定义 `names`；内建底色组 `arr=None` 时验证段下标崩溃。
- **🔴 未做负对照**：上述三脚本改动后**未重跑 Lilium/Lunalice 验证逐字节不变**（既定约束"修 bug 必做破坏关键行负对照"）。
- **源规模参考**：Lunalice 121704v/159363t/127骨/92槽；Lilium 118014v/154066t/124骨/92槽（严格子集）；MourningVeil 114213v/147440t/**107骨**/**58槽**。

## 材质变体包（第 61 轮）/ 源侧组件开关（第 62 轮）/ Lua 切换（第 63 轮分析）
- 八人包材质拆成**独立 patch**（`split_materials.py`）：7 个已落 `E:\mod\Lunalice-RE2310-MAT-*`。**同族互斥**：D2/D3/D4 三选一、E1/E2 二选一。
- 蕾丝普查：7 组真半透明，但 `alphaclip` 只能硬裁切 ⇒ 0.5 阈值是近似。材质开关 31 个 VPK 未实现（仅 1 个可整组表达）。
- **L4N 的「部件切换」= Source 原生 `$BodyGroup`**（N 选一、`blank` 隐藏）。Lunalice 主模型 **32 bodygroup = 30 可切换 + 2 固定**，顶点区间两两不相交 ⇒ 按 bodygroup 拆分干净。HD2 侧映射已成立。**唯一未知数 = piece 数量上限**（原生 23/15，自定义 19）⇒ 先拆 4~6 个实机验证。详见 `tmp/rig-lab/L4N_COMPONENT_ANALYSIS.md`。
- **Lua 运行时动态切换已具备能力**（重写协议 + revision 自增）。协议键 `model_piece=` / `piece_override=` / `target_mode=` / `composition_mode=`。**已发布 buffer 永不释放**（反复切换累积内存）。见 `tmp/rig-lab/LUA_UNIT_SWITCH_TODO.md`。

## 交付与部署
- **CLI 用法（实测）**：`library sync` 与 `equipment armor-normalize` 都是 `inspect|plan|validate|apply` 四段；**只有 `inspect <GUID>` / `plan <GUID>` 带 GUID**，`validate --plan` / `apply --plan … --output <目录> --confirm` **不带 GUID**；`apply` 的输出目录**必须不存在**。带空格的 `--game-data` 在 bash 里要加引号。写入命令会被 `ManagerProcessRunning` 拦（需关闭桌面端）。`plan` 输出文件已存在要加 `--overwrite`。
- **🔴 库节点 GUID ↔ sync plan 临时 id 不同**：`library sync inspect` 报的是**临时 id**，后续命令要用 `mod list` 里的**真实 GUID**。
- **🔴 `library sync` 四段参数（第 72 轮踩坑修正）**：`inspect` / `plan` / **`validate`** / **`apply`** —— **后两者也必须带 `--manager-root` + `--library`**，只给 `--plan` 会报 `LibrarySyncTargetChanged`（`recomputedInputFingerprint: null`）。且 plan 里固化的**目标指纹与 CLI 参数绑定**，跨次调用要**重新 plan 再 validate**。`validate` 成功标志 `ok/isValid: true`；`apply` 成功标志 `execution.success: true`（正常时 `physicalFiles/profiles/activationState: not-modified`，只更新库索引事实）。
- `gamedata extract asset|archive|show|list`（unit TypeID = `0xe0a48d0be9a7453f`）。**查 64 位 id 要转有符号 64 位**（SQLite 存的是 int64）。
- **符号链接铁律**：`New-Item -ItemType SymbolicLink` 本机失败 ⇒ 一律 `MSYS=winsymlinks:nativestrict ln -s`，先建后删（写 .tmp 再 `mv -f`）。
- **覆盖语义**：patch 序号大的后加载，覆盖同 AssetKey 的较小者。
- 长期工具：`fix_appearance.py`(M8 构建)、`fix_materials.py`、`fix_textures.py`(外科替换范式)、`_probe_matfix/_probe_matchain/_probe_canddiff/_probe_weightdiff`。
- 待办（暂不做）：军械库渲染支持 `alphaclip`；管理器直接导入 VPK 作一等公民；法线贴图/`$emission`/描边未接；**bundled 归档 GPU/stream 读不到的产品 bug**。

## 直接替换原生护甲（第 59 轮建立，第 64/72 轮用于 Lilium/MourningVeil）
- **工具 = `hd2mod equipment armor-normalize`**（**不是** cross-armor）。按「The archive is the visibility boundary」**自动把目标护甲 archive 里除承载位以外的全部 unit 换成隐藏占位**（`CanonicalPlaceholderMinifier`：3 顶点、0.0001 m 三角形、保留 culling 壳）。
- **`build_patch.py --unit-mode native --native-unit 0x…[可重复] --native-dir <游戏 unit toc 目录>`** 把整模型写进**真游戏 unit id**，每个 id 带自己的 240 B `customization_data`。protocol 写 `enabled=false`。
- **`--unit-mode` 四选一**：`fresh`(自造 id，需 Lua) / `native`(真 id) / `same-key`(9 个真身体 id，整模型会**画 9 份**) / `both`。
- **RE-2310 Armor archive**（`d923906f8b815987`）**22 unit** = 8 身体（Torso `cae35ee15168145f`/`9841b16544c8fbae`、Pelvis `17da843a4908511e`/`45d79d84b8933a7f`、LeftArm `91eb7be84016ceab`/`9a5e6dc74cdb08a3`、RightArm `7336c6b662522f0e`/`4f7bce4ca49ccda8`，各 Slim/Stocky）+ 14 其他部位。`SelectPair` 约束：单 unit 只能落 `BodyVariant==Any`；RE-2310 躯干只有 Slim/Stocky ⇒ **必须一次给两个身形**。
- **已知瑕疵（管理器侧，非我们代码）**：隐藏占位 unit 会丢「零索引 section」的材质短 id（`num_indices==0` ⇒ 惰性）⇒ 不阻断 ⇒ 审计恒为 **27 项 26 通过 / 1 已知**。
- 工具：`_probe_nativeids.py`、`_probe_dangling.py`、`_probe_render_patchunit.py`（**从出货 patch 渲染**，非 npz）、`_verify_normalized.py`（**须传 `--our-unit <承载id列表>`**，默认值是 Lilium 的）、`assemble_re2310.py`(Lilium 硬编码) / `assemble_mourning.py`(参数化)。

## 工程
- 管理器 WPF/net10、CLI net10；Core/Adaptation/GameItemAssets/SourceModel/SourceMigration net8。CoreServices 唯一装配点，SourceModel 零项目引用。Nullable/ImplicitUsings/Deterministic、警告当错误（除 NU1901..1904）。legacy 包需无 Lua 可用；**运行时无建网格/改权重接口，迁移离线做**。
- 自定义数据属模组库，简单配置属 `settings.json`，可重算缓存属管理器目录。
- **`ArmorNormalizationService` 关键位置**：第 39 行 `Where(Category=="Armor")`；第 52-61 行 hidden 推导（ArchiveId 精确匹配）；第 149-175 行 hidden 写盘（`CanonicalHiddenUnitBuilder.Build(vanilla, avatarTransforms)` 可从 GameData 读原版并追加为 `additions`）。
- **`ModEquipmentSourceFactsReader.cs:214`**：`SourceCandidates = EquipmentUnitSourcePartBinder.Bind(targetCandidates, unitFacts)` —— 靠 patch 里的 unit 去和游戏装备目录绑定。
