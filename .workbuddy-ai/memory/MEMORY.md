# 项目长期记忆：HD2ModManager

> 只留**结论、硬约束、易忘的坑**。细节在 `docs/*.md`（**被 .gitignore，改前先备份 `docs/_backup/`**）与 `.workbuddy-ai/memory/YYYY-MM-DD.md`（历史全文在 `_backup/`）。

## 项目定位
- 仓库 `WpfApp1`：《绝地潜兵 2》Mod 管理器与补丁适配工具集，GPLv3。`ManagedMain` 已过时。生产工程：`HD2ModManager`(WPF/net10)、`HD2ModCli`(net10,`hd2mod`)、`HD2ModCore`(net8)、`HD2ModAdaptation`(net8)、`Decryptor`、`HD2GameItemAssets` + `*.Tests`；依赖链 `Cli → Core → {Adaptation, GameItemAssets, Decryptor}`。
- `HD2RuntimeArmorOverride` = 护甲运行时映射的 Lua mod 源码（非 csproj），lua 被 `HD2ModCore.csproj` 内嵌为 EmbeddedResource。`CoreServices` 是**唯一装配点**（判"某服务是否被生产使用"看 `CoreServices.CreateXxx(` 调用点是否为 0）。

## 工作约定（硬约束）
- **【强制】只做本地提交，不推送远程**。**授权范围内的改动直接做，不反复请示。**
- **用户不是专业程序员**：涉及"要不要删/要做什么"，先用通俗语言说明作用与后果，再给建议。
- **临时脚本一律 `tmp/`（已 gitignore），不要写进 `_diagnostics/`** —— 后者是条件测试目录，一旦存在（哪怕空的）就会真跑并失败。**`tmp/rig-lab/` = 本功能的离线「斧」**（`*.py` 已入库，`out/` 忽略）。
- **写完"应能抓住某 bug"的用例后，主动破坏被保护的那一行再跑**；仍通过 = 用例是假的。删除类改动**必须同时改造其测试**。
- **`tools/check_dead_code.py` 是词匹配**（注释里写类名会误判可达）→ **必须两方向一起看**（`comm -13` + `comm -23`）。
- `Directory.Build.props`：`ImplicitUsings`/`Nullable`/`Deterministic` 开、**`TreatWarningsAsErrors=true`**（排除 `NU1901..1904`）。TargetFramework/OutputType/引用**刻意不在此文件**。
- 耗时：全解决方案 `dotnet test` **6–7 分钟**；`dotnet build -t:Rebuild` **20 秒**。**报测试计数要分项+增量；基线会变，报数前先跑一遍。**
- 发布：**轻量标签**；溯源靠包内 `cliVersion`（`1.0.0+<git 全哈希>`）；校验产物用 Python `zipfile`（**别用 PowerShell `Add-Type`**，被拦）。
- **【数据归属·硬约束】用户自定义数据一律存**模组库**；设置页简单设置存 `settings.json`；**能从游戏 data 重算的纯缓存**才放管理器目录。目标：**换管理器 = 无痛继承模组库；迁移模组库 = 只改库地址即可。** ⚠️ 2026-10-11 护甲页崩溃的根因就是违反了这条。**新增持久化文件前先问：属于库、属于设置、还是纯缓存？**
- `git commit` 提交**整个索引**，与 `git add` 传了哪些路径无关。
- **【环境】** ① Python 用隔离 venv `...\python\envs\default\Scripts\python.exe`（有 numpy+Pillow；**系统 python 无 PIL**）；② `git clone`/`curl` 打 GitHub 常失败 → 让用户下 zip；③ **Bash heredoc 调 Python/PowerShell 被拦** → 写成 `tmp/*.py` 再执行；④ `Edit` 对含大量反引号/emoji 的长中文行会匹配失败 → 用 Python `str.replace` 并回读校验；⑤ `Remove-Item` 被 safe-delete 钩子接管 → 让用户自己跑，**不要**用 `[System.IO.File]::Delete` 绕过。

## 大功能 B（当前方向）：L4D2 模型整体移植到 HD2
> 细节见 `docs/l4d2-to-hd2-physbone-migration.md`、`docs/l4d2-hd2-boundary.md`、**`docs/hd2-batchtool-sdk-findings.md`（§7 源码逐函数核对；§8 新管线；§9 三个真 bug；§10 参照 patch；§11 站姿；§12 骨轴往返；§13 发布骨架=HD2原生；§14 mesh 枢轴解耦；§15 网格平移）**；数字全在 `.workbuddy-ai/memory/*.md`。

### 用户界定的目标（第 39 轮，最高优先级）
- **不是"一键转换"黑盒，而是"录一个人工操作流的宏"**：① 把源 T-pose 模型摆成参照的站姿/比例（**纯数学变换**，只借 BatchTool 的变换部分，其余逻辑与我们无关，可继续优化）；② 按参照的权重风格重刷权重；③ 全自动 legacy 流程。
- **骨架与模型没有直接联系，骨架只和权重有关（间接影响模型）。** 参照 patch 的骨架**一根骨没动**（= HD2 原生），"比例优化"全在**网格层**。HD2 标准骨架/游戏帧模拟图**只是对照手段**，不是目标。
- **先只做第 ① 步：把网格摆好**（摆成参照的形状/结构/比例）。摆好后**提取干净网格、丢掉一切源骨/源权重**，再拿干净 HD2 原生骨架按参照风格上权重。
- **无权重时游戏帧必须 == 绑定帧**（"任何动画都驱动不了角色，模型恒定保持变体 A-pose"）。

### 🔴 第 41 轮（2026-10-12）：**模型完整性 —— 几何一直在数据里，是出图函数把它删了**（详见 findings §16）
- 用户发现“双马尾没了、鞋后跟厚底没了”。**查明：VPK 内几何齐全**（`platform shoes` 11472v/13122t、`CatEars`、`HeadDress*`、`Dress_Skirt` 25041v、`Base` 18557v …，共 **121704 verts / 159363 tris / 127 源骨**）。读取层没丢件。
- **真凶 = `drop_outlier_tris` 的 `floor=0.06 m`**（**display-only**）：阈值 `max(8×median, 0.06)` = **0.0600 m** 正好切在三角形边长 p99（0.0645）⇒ **丢 510 顶点** = `Base` 270（**马尾**，z 0.964–1.596，骨 `source_extra_72–77`+`Head1`）+ `platform shoes` 133（**鞋后跟厚底**，z −0.062–0.008）+ 裙子/丝带/袜口/刀。
- **顺带证伪旧注释**：**不存在 1.8 m/1.0 m 的“长三角形尖刺”**。逐模型最大边（src/src1/out 三态）zoey 0.191 / coach 0.121 / nick 0.337 / ellis 0.131。远离身体的几何是**整块 body part**：nick `LongSleeve`（y 到 1.78，桥接三角形最长仅 0.16 m）、ellis `Tail`（y 到 1.04）⇒ **边长规则既删不掉它、又误删真实几何**。
- **修法（已实现）**：`drop_outlier_tris` 改为**半径规则** —— 中心 = 顶点中位数，`cut = 0.85 × height`，三角形“任一顶点超半径”才隐藏；返回 `(kept, (n_hidden, cut))`。实测：**zoey/rochelle/coach 隐藏 0**（马尾+鞋底回来），nick 隐藏 579 tris（LongSleeve 远端），ellis 隐藏 691 tris（Tail 远端）。`0.85` 的依据 = 逐 body part 最大半径（鞋 1.26–1.43 / `Base` 1.19–1.37 / `LongSleeve` 1.85 / `Tail` 1.67）。
- **教训**：**“数据里没有” ≠ “图里看不到”**。任何会删数据的启发式，都必须先证明它删的确实是垃圾。
- 新探针：`probe_missing2.py`（按 body part 归属丢失顶点）、`probe_spikes*.py`（三态最大边）、`probe_blob*.py`、`probe_radius.py`（逐部位半径）、`probe_filter_check.py`、`probe_full_view.py`、`probe_filter_compare.py`（新旧对比图）。

### 🔴 第 40 轮：**网格整体平移：下移 6.0 cm、后移 10.0 cm**（§15）
- `retarget(..., mesh_shift=(dx,dy,dz))` —— **只平移烘焙后的 `out`**（骨架绝不移动，`out_idle` 由 `out` 派生 ⇒ 自动跟随）。CLI `--mesh-down` / `--mesh-back`。
- 取值靠拟合（`probe_fit_shift.py`，目标 = 我们采样点到参照最近点的**中位距离**）：全身最优 **dy −0.100 / dz −0.060**（残差 44 mm）；仅腿 −0.082/−0.080；躯干+头 −0.100/−0.070。用户明确**不必把鞋底锁死在参照鞋底**。
- **⚠️ 踩坑**：用"脚底贴片质心/中位数"当锚点**严重误导**（我们 +0.145 vs 参照 −0.087，差 23 cm）—— 两网格**顶点密度分布不同**；**必须用"点到面"最近邻距离**。
- 新工具：`tri_overlay.py`（2 列 × 3 行 6 图，每行独立缩放、两列共享，带 Y=0/X=0 参考线）、`probe_shift_view.py`（**通道叠加** 我们=绿/参照=品红/重叠=白）、`crop_tri.py`/`crop_panel.py`/`crop_cols.py`、`probe_axes.py`、`probe_fit_shift.py`。
- 验证未回退：`idle vs bind 6.38e-16 m`、`发布骨架 vs HD2 0.00e+00 m`。

### 🔴 第 39 轮：**mesh 烘焙枢轴 与 发布骨架 必须解耦**（§14）
- 症状：`skeleton='native'` 让 idle 恒等，但**绑定态网格**坏了（左手"伞/细长鬼爪"甩到身前 3 头远、左臂撕裂、右臂基本正常）。
- 根因：A-pose 烘焙 `D[b]=D[p]·rs[b]·basis_i[b]·inv(rs[b])` **绕骨关节旋转网格** ⇒ **枢轴必须在网格内**。`native` 把 `rs` 从 `rest_sym`（吸附到**源**关节，在网格内）换成 `rest_t`（**HD2** 关节，骨架比源网格宽 ⇒ 枢轴在网格**外**）⇒ 甩飞。
- 修法：**拆开两件事** —— 网格烘焙**永远**用吸附后的 `rest_sym`；`skeleton='native'` 只改**步骤 4b**（`S_bind[b]=rest_hd2[b]` 一字不改）⇒ `rest_hd2·inv(S_bind)=I`。
- 实测：`A bind vs B bind 0.00e+00 m`；`A idle==bind 6.59e-16 m`；5 模型 `edge_max` 回到 1.88–5.83（不再 70–127）。
- **教训**：**"idle 恒等"和"绑定态好看"是两个独立条件** —— 前者由**发布骨架**决定，后者由**烘焙枢轴**决定。

### 🔴 第 38 轮：**发布骨架 = HD2 原生一字不改**（§13）
- 运行时 `v_world = Σ w_b·A[b]·inv(S_bind[b])·v_bind`；idle 时 `A[b]=rest_hd2[b]` ⇒ **只有 `S_bind==rest_hd2` 时恒等**。旧 `snap`（把 HD2 关节吸附到源关节）⇒ 每帧被拽 =「鞋跟没了 / 脚踝小腿断裂 / 脖子拉长 / 裙子拆成两片」。
- 量化"网格该做的变换"（`probe_mesh_vs_rig.py`）：① 网格最低点 z 我们 **+0.0057** / 参照 **−0.0542** ⇒ 下移 ~6 cm；② 网格高 我们 **1.9644** / 参照 **2.1127** ⇒ 比值 **1.0755**；③ 手掌够不到 `l_hand` 偏 0.15 m / `r_hand` 偏 0.69 m；④ **归一化关节位置（÷身高）与参照结构完全一致** ⇒「下移 6 cm + X 缩放」等效于把网格适配到 **2.113 m** 身高。

### 🔴 第 37 轮：**发布的骨架骨轴曾是错的**（§12）
- BatchTool「工作骨架」≠「发布骨架」：`_convert_pose_space` = `POSE_SPACE`(INITIAL↔TPOSE) × `BODY_AXIS_SPACE`(INITIAL↔SYMMETRIC)；**两套 basis 表互为逆元**（30 条 `initial≈inv(tpose)`，≤2e-5）⇒ 骨架局部旋转**抵消**、网格**叠加** ⇒ **发布骨架 = HD2 原生，网格 = 源模型的 A-pose**。
- `axis_roundtrip=True`（默认）：`rest_sym` 局部再乘 `basis_t`；**新增步骤 4b** 把 `S_A=D·rest_sym` 的骨轴 `SYMMETRIC→INITIAL`（`L←L·inv(Dd)`，**head 必须回填**，否则右肩/肘/手被推到 0.25/0.81/1.27 m 外）。效果：朝向差 **43.90°/180° → 0.02°/0.05°**，`>5°` 骨 **45/107 → 0/107**。
- ⚠️ 更正：`*_wxyz` 是 **w 在前**；20 条 delta 里**只有 2 条 180°**（`r_clavicle` 绕 +Z、`r_shoulderarmour`）。**`--no-axis-deltas` 不是正确修法**。

### 🔴 管线参数与三个真 bug（§9）
- **必用组合**：`--align yaw --spine-map mesh --fallback distance --leg-tilt 5.7 --leg-splay 4.0`。
  - `kabsch` 把身体转歪 2.8°（脖子跑到髋**前** 1.75 cm）⇒ 用 `yaw`。
  - `Spine→spine1` 让躯干每帧拉伸 26.5 cm（HD2 `spine1` 与 `hips` 完全重合）⇒ `spine-map mesh`。
  - 鞋被绑到膝盖 ⇒ 穿地 13.5 cm（源鞋父级 `Bip01_L_Calf`）⇒ `fallback distance`。
- **【方法论·最重要】bind 恒等会掩盖真问题。** `D` 在绑定态恒等 ⇒ 绑定态指标全漂亮，**哪怕对应表是错的**。只有 `A[b]·inv(S_bind[b])` 的渲染能与参照 patch 直接对比。
- **`--leg-tilt`**：绕踝转 thigh/knee +θ；其他骨整体平移；foot/ball/toe **钉住**；网格同步混合变形。⚠️ 腿倾段的网格变换**必须**用 `resolve_bone_map(...)` 与主管线共用同一份骨映射。tilt≈5.6° 复现 HD2 腿部站姿（踝在髋后 9.86 cm）。
- **`--leg-splay`**：绕腿根绕 +Y 转 ±θ；thigh/knee 拿旋转、foot/ball/toe 拿**反向旋转**（塌缩成纯平移）。**侧视逐点不变（max|Δy|=0）**。选 4.0°（沿用作者"少掰"）；⚠️ 归一化用全身高；⚠️ 命名别用 `R`（会遮蔽外层对齐矩阵）。
- **【第 4 个真 bug·未修】脚落点整体前移 ~6 cm**：管线 `t` **只锁头关节** ⇒ 脚底贴片质心 我们 `y +0.0427` / 参照 `−0.0198`。X 那一半已被 `--leg-splay` 顺手修掉；**Y 那一半仍未动**。修法 = XY 锚点改成"脚底贴片"（Z 仍锁头关节）。
- **头/尺寸（已降级）**：源 `Head1` 是**颅底**、HD2 `head` 是**颅顶**；参照头部几何在 `head` 关节**上** 0.368 m、我们 0.293 m ⇒ **我们小 ~20%** ⇒ 降级为"可选微调"。

### 待办（网格变换；**用户要求一步一步来**）
1. ✅ 整体平移（下移 6.0 / 后移 10.0 cm）；**1.5 ✅ 保证模型完整性**（`drop_outlier_tris` 已改为半径规则，马尾+鞋底回来）；2. **上半身放大**（手掌够到 HD2 手掌骨；参照上衣连衣裙与我们是同一模型，可用特征点求缩放比；整体高比 1.0755 是起点）；3. **接缝渐变缩放**（躯干/大腿接缝，PS 羽化式）；4. **手臂少弯**（降 `basis_i` 里手臂旋转量，尤其肘部；**不是拉长手臂**）；5. **头部比例**（参照二次元大号头）。
> **分工**：用户**空间感强、善于发现问题**但**对距离/数值不敏感**；我**视觉空间判断弱**但**数值/拟合可靠** ⇒ 用户负责"指出哪里不对"，我负责"用数值定出多少"。
- 后续：提取干净网格 → 按参照风格重刷权重 → 上 C#（`HD2SourceMigration`）→ 产出离线 legacy 版 patch 包 → 进游戏实测（用户负责）。**动态物理骨骼仍要支持**（L4D2 风味 `jiggle_data.lua`，添头）；**硬要求：没有 Lua 支持 legacy 包也必须能跑**；**不依赖 `HD2IRG1` 的动态骨骼**。

## 坐标约定 & 参照 patch
- **HD2 坐标：+Y = 前，+Z = 上，+X = 角色的「右」**（角色左侧在 −X）。⚠️ 早期笔记写成"+X = 左"是**笔误**；实测 HD2 原生 `l_foot` x = **−0.1908**、`r_foot` = **+0.1908**。**`l_foot` 关节不在脚心**：HD2 原生 踝在髋后 9.60 cm / 源 ≈ 同 ⇒ **本来就一致**。
- 参照 patch = `E:\mod\【4.28】Chiffon-LUNALICE替换RE-2310`（E 盘 = 用户**测试**库，可随便操作；D 盘 steam = **实际在用**）。容器 `9ba626afa44a3aa3.patch_0`(216 KB)+`.gpu_resources`(219 MB)+`.stream`(0 B)，**无 manifest.json**；**35 条目**（23 Unit+2 Bone+10 未知），**只 9 个 Unit 有几何**；**渲染选 `cae35ee15168145f,17da843a4908511e,91eb7be84016ceab,7336c6b662522f0e` + 头 `af431352d9b95e83`**（RE-2310 头盔 = 该 id，标签 `Torso_Undergarment_Slim_lod3`，几何"脸+猫耳+长发"；容器里**没有 unit 叫 helmet** ⇒ **不要从游戏本体取头盔**）。加它完整参照 **z[−0.054,+2.059]、总高 2.113 m**。
- **【关键判据】补丁骨架与 HD2 原生逐位一致**（13 根骨 delta 全 0），只多出 `*_culling`/`*_twist` 辅助骨 ⇒ **"比例优化"全在网格层，骨骼照抄 HD2。** **读取三坑**：① **Unit 骨矩阵是行向量约定（平移 flat 12,13,14）**，与 `E.read_hd2_rig`（已转置）相反 —— 读错**不报错**；② bone 列表**前 9 项是场景图节点**，真骨从 `skeleton` 起，`boss` 是髋部根运动骨；③ **Unit 骨名是空的**，靠 32-bit hash 查 `bonehash`。

## 旧项目 / 新工具（WpfApp1 内重写，进行中）
- **旧项目编译不过（预先存在）**，最后提交 **2026-09-03**；**用户已拍板：在 WpfApp1 内重写，不修旧项目。** 方案：**`HD2SourceModel`**（net8，**零引用**，编译期强制不认识 HD2）✅ + **`HD2SourceMigration`**（**唯一同时引两侧**）✅骨架 + `HD2ModCli` 薄子命令 + 各自 `*.Tests`。HD2 侧复用 `UnitMeshReader.ReadTransformInfoFromUnitToc` + `UnitBoneNamesReader`；`HD2ModAdaptation/.../Canonical/` 有 **31 个 `Canonical*` 编译器**（"给 Unit 加骨 + 重写蒙皮"能力现成）；矩阵统一 `System.Numerics.Matrix4x4`（float、行向量）。**Source 格式权威在 `HD2SourceModel/*.cs` 注释。** ⚠️ **实测三次证伪规范**（合成样本测不出，只有真实文件能证伪）：① **VPK 条目步长与版本号无关**（97/97）；② **MDL 骨步长 216 B 对 v44/48/49/53 全成立**；③ **bodypart/model 名是 UTF-8**（如 `裙Dress_Skirt`）。

## 其他（细节全在 `docs/`）
- **大功能 A（已被 B 取代）**：引擎 = Stingray(Bitsquid)，**裁掉全部物理命名空间**；`Unit`/`Mesh` **零 `create_*` / 零顶点权重接口** ⇒ **纯运行时 Lua 改不了权重、加不了骨、造不出物理**。**改内存边界：改「数据」✅ / 改「代码」❌**。**【硬约束】只允许依赖游戏本体，不得依赖任何外部系统（尤其其他 mod）**。
- **信息中心（阶段 0–6 已完成）**：`ModInformationFactRegistry` = **25 kind** 单一真相，`RequestAsync<T>` = 唯一读取入口，`Validate()` 在 `CreateModInformationCenter` 开头跑（新增 kind 忘登记 `SourceRoots` = **启动期失败**）。铁律：**缓存不得取代真实性**（键全由来源指纹构成、不落盘、双向测试）；**「读不到」≠「没有覆盖」**；**图标不进事实**。
- **护甲映射页 20261011 回归**（`docs/armor-registry-page-20261011-regression.md`）：兜底已落实（`8d06fcb`/`5b79acc`），**根因未动** —— **拿「升级必被摧毁的凭据」(`deployment`) 守护「升级必被保留的文件」(`protocol`) ⇒ 设计上不可能可靠**；崩溃三条件：①点过应用 ②库无 `workspace.json` ③`deployment` 不存在。**回归 #2（破坏性，未动）**：`ModLibraryResidualCleanupService.Cleanup(...)` 会 `Directory.Delete(recursive: true)` 删库根"未被快照引用且空"的子目录。
