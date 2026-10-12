# 项目长期记忆：HD2ModManager

> 只留**结论、硬约束、易忘的坑**。细节在 `docs/*.md`（**被 .gitignore，改前先备份到 `docs/_backup/`**）与 `.workbuddy-ai/memory/YYYY-MM-DD.md`（历史全文在 `_backup/`）。

## 项目定位
- 仓库 `WpfApp1`：《绝地潜兵 2》Mod 管理器与补丁适配工具集，GPLv3。`ManagedMain` 已过时。生产工程：`HD2ModManager`(WPF/net10)、`HD2ModCli`(net10，`hd2mod`)、`HD2ModCore`(net8)、`HD2ModAdaptation`(net8)、`Decryptor`、`HD2GameItemAssets` + `*.Tests`；依赖链 `Cli → Core → {Adaptation, GameItemAssets, Decryptor}`。
- `HD2RuntimeArmorOverride` = 护甲运行时映射的 Lua mod 源码（非 csproj），lua 被 `HD2ModCore.csproj` 内嵌为 EmbeddedResource。`CoreServices` 是**唯一装配点**：判"某服务是否被生产使用"看 `CoreServices.CreateXxx(` 调用点是否为 0。

## 工作约定（硬约束）
- **【强制】只做本地提交，不推送远程**（用户手动推送）。**授权范围内的改动直接做，不反复请示。**
- **用户不是专业程序员**：涉及"要不要删/要做什么"，先用通俗语言说明作用与后果，再给建议。
- **临时脚本一律 `tmp/`（已 gitignore），不要写进 `_diagnostics/`** —— `DiagnosticsPatchSampleTests` 是条件测试，`_diagnostics` 一旦存在（哪怕空的）就会真跑并失败。**`tmp/rig-lab/` = 本功能的离线「斧」**（`*.py` 已入库，`out/` 忽略）。
- **写完"应能抓住某 bug"的用例后，主动破坏被保护的那一行再跑**；仍通过 = 用例是假的。删除类改动**必须同时改造其测试**。
- **`tools/check_dead_code.py` 是词匹配**（注释里写类名会误判可达）→ **必须两方向一起看**（`comm -13` + `comm -23`）。
- `Directory.Build.props`：`ImplicitUsings`/`Nullable`/`Deterministic` 开、**`TreatWarningsAsErrors=true`**（排除 `NU1901..1904`）。TargetFramework/OutputType/引用**刻意不在此文件**。
- 耗时：全解决方案 `dotnet test` **6–7 分钟**；`dotnet build -t:Rebuild` **20 秒**。**报测试计数要分项+增量；基线会变，报数前先跑一遍。**
- 发布：**轻量标签**；溯源靠包内 `cliVersion`（`1.0.0+<git 全哈希>`）；校验产物**别用 PowerShell `Add-Type`**（被拦），用 Python `zipfile`。
- **【数据归属·硬约束】用户自定义数据一律存**模组库**；设置页简单设置存 `settings.json`；**能从游戏 data 重算的纯缓存**才放管理器目录。目标：**换管理器 = 无痛继承模组库；迁移模组库 = 只改库地址即可。** ⚠️ 2026-10-11 护甲页崩溃的根因就是违反了这条。**新增持久化文件前先问：属于库、属于设置、还是纯缓存？**
- `git commit` 提交**整个索引**，与 `git add` 传了哪些路径无关。
- **【环境】** ① Python 用隔离 venv `...\python\envs\default\Scripts\python.exe`（有 numpy+Pillow；**系统 python 无 PIL**）；② **`git clone`/`curl` 打 GitHub 常失败** → 让用户下 zip；③ **Bash heredoc 调 Python/PowerShell 被拦** → 写成 `tmp/*.py` 再执行；④ **`Edit` 对含大量反引号/emoji 的长中文行会匹配失败** → 用 Python `str.replace` 并回读校验；⑤ **`Remove-Item` 被 safe-delete 钩子接管 → "删到回收站"必抛错**，让用户自己跑，**不要**用 `[System.IO.File]::Delete` 绕过。

## 大功能 B（当前方向）：L4D2 模型整体移植到 HD2
> 细节见 `docs/l4d2-to-hd2-physbone-migration.md`、`docs/l4d2-hd2-boundary.md`、**`docs/hd2-batchtool-sdk-findings.md`（§7 源码逐函数核对；§8 新管线；§9 三个真 bug；§10 参照 patch 做法；§11 正视站姿；§12 骨轴往返；§13 发布骨架=HD2原生；§14 mesh 枢轴与发布骨架解耦）**；数字全在 `.workbuddy-ai/memory/*.md`。

### 用户界定的目标（第 39 轮，最高优先级，写在所有"好看"之前）
- **我们做的不是"一键转换"黑盒，而是"录一个人工操作流的宏"**：① 把源 T-pose 模型摆成参照的站姿/比例（**纯数学变换**，只借 BatchTool 的变换部分，其余逻辑与我们无关，且可以继续优化它）；② 按参照的权重风格重刷权重；③ 全自动 legacy 流程。
- **骨架与模型没有直接联系，骨架只和权重有关（间接影响模型）。** 参照 patch 的骨架**一根骨没动**（= HD2 原生），"比例优化"全在**网格层**。HD2 标准骨架/游戏帧模拟图**只是对照手段**，不是目标。
- **本轮只要一件事：把网格摆好**（摆成参照的形状/结构/比例）。摆好后**提取干净网格、丢掉一切源骨/源权重**，再拿干净的 HD2 原生骨架按参照风格上权重。
- **无权重时游戏帧必须 == 绑定帧**（"任何动画都驱动不了角色，模型恒定保持变体 A-pose"）。

### 🔴🔴🔴 第 40 轮（2026-10-12）：**网格整体平移：下移 6.0 cm、后移 10.0 cm**（详见 findings §15）
- **用户要求一步一步来**：本轮**只做第 ① 步（整体平移）**，并且指出**前后方向也有偏移**，要加**后移**；出图改成 **2 列 × 3 行 = 6 图**（左列我们、右列参照；上中下 = 正/侧/**俯**），两边都叠 HD2 原生骨架。
- **实现**：`hd2_style.retarget(..., mesh_shift=(dx,dy,dz))` —— **只平移烘焙后的 `out`**（骨架绝不移动，`out_idle` 由 `out` 派生 ⇒ 自动跟随）。CLI `--mesh-down` / `--mesh-back`。
- **取值不靠猜**：`probe_fit_shift.py` 网格暴力搜索"把我们网格平移到参照网格"的最优刚性平移，目标 = 我们的采样点到参照最近点的**中位距离**（中位数，防头发/尖刺主导）：
  | 拟合范围 | dy（后移） | dz（下移） | 中位残差 |
  |---|---|---|---|
  | 全身 | **−0.100** | **−0.060** | 44 mm |
  | 仅腿（下半 45%） | −0.082 | −0.080 | 31 mm |
  | 躯干+头 | −0.100 | −0.070 | 42 mm |
  ⇒ **采用全身最优：后移 0.100 m、下移 0.060 m**。用户明确说**不必把鞋底锁死在参照鞋底上**（"来源模型的腿部较长，鞋底陷入地板几乎是必定的"）⇒ 鞋底比参照低 7 mm 是**可接受**的，不要为了齐平牺牲整体。
- **⚠️ 踩坑**：用"脚底贴片质心/中位数"当锚点会**严重误导** —— 我们的脚 y 中位数 `+0.145` vs 参照 `−0.087`（差 23 cm），但两者**顶点密度分布不同**（我们集中在脚尖、参照集中在脚跟），中位数根本不是同一个几何位置。**必须用"点到面"的最近邻距离**才是几何正确的度量。
- **新工具**：`tri_overlay.py`（2 列 × 3 行 6 图，每行独立缩放、两列共享同一缩放，带 **Y=0/X=0 参考竖线**）、`probe_shift_view.py`（**通道叠加**：我们=绿、参照=品红、重叠=白 —— 判断位移最直观）、`crop_tri.py` / `crop_panel.py` / `crop_cols.py`、`probe_axes.py`（核实左右轴）、`probe_fit_shift.py`。
- **验证未回退**：平移后 `idle vs bind = 6.38e-16 m`、`发布骨架 vs HD2 = 0.00e+00 m`。

### 🔴🔴🔴 第 39 轮（2026-10-12）：**mesh 烘焙枢轴 与 发布骨架 必须解耦**（详见 findings §14）
- **症状**：上一轮 `skeleton='native'` 让 idle 恒等了，但**绑定态网格本身**就坏了（左手像"伞/细长鬼爪"，被甩到身前 3 个头远；左臂撕裂、右臂基本正常）。
- **根因（一句话）**：A-pose 烘焙是 `D[b] = D[p]·rs[b]·basis_i[b]·inv(rs[b])` —— 它**绕骨 b 的关节旋转网格**。所以**枢轴必须在网格内部**。`skeleton='native'` 把 `rs` 从 `rest_sym`（吸附到**源**关节，在网格内）换成了 `rest_t`（**HD2** 自己的关节，HD2 骨架比源网格宽得多 ⇒ 枢轴在网格**外**）⇒ 旋转把网格甩飞。
- **修法**：**只把「发布哪套骨架」和「绕哪套 rest 烘网格」拆开**。
  - 网格烘焙**永远**用吸附后的 `rest_sym`（= 第 2 列 snap 的行为，视觉正确的那套）；
  - `skeleton='native'` 只改**步骤 4b**：`S_bind[b] = rest_hd2[b]` 一字不改。
  - ⇒ `rest_hd2·inv(S_bind) = I` ⇒ **游戏帧 = 绑定帧**，且绑定帧 = snap 的好看网格。
- **实测**：`A bind vs B bind max diff 0.00e+00 m`；`A idle==bind 6.59e-16 m`；**5 模型 `edge_mean/edge_max/bad%` 与 snap 逐位相同**（`edge_max` 1.88–5.83，**不再是上一轮的 70–127**），`rig head = 0`、`idle max = 0.0000`。**零逐模型调参。**
- **出图**：`quad_overlay.py` = **4 行 × 3 列**（bind 正 / bind 侧 / GAME 正 / GAME 侧 × **A=native** / B=snap / C=参照），每列叠骨架 ⇒ 列 A 的 **1≡3、2≡4 完全重合**。裁图 `crop_cols.py`、`crop_colA.py`。
- **教训**：**改一个开关不要同时动两件独立的事**；"idle 恒等"和"绑定态好看"是**两个独立条件**，各自有各自的自由度（前者 = 发布骨架，后者 = 烘焙枢轴）。

### 🔴🔴 第 38 轮：**发布骨架 = HD2 原生一字不改**（详见 findings §13）
- **机制**：运行时 `v_world = Σ w_b·A[b]·inv(S_bind[b])·v_bind`；idle 时 `A[b]=rest_hd2[b]` ⇒ **只有 `S_bind==rest_hd2` 时恒等**。旧 `skeleton='snap'`（把 HD2 关节吸附到源关节）⇒ 每帧被拽 = 「鞋跟没了 / 脚踝小腿断裂 / 脖子拉长 / 裙子拆成两片」（第 37 轮只把撕裂 0.568→0.183 m，**没归零**）。
- **量化「网格该做的变换」**（`probe_mesh_vs_rig.py`）：① 网格最低点 z 我们 **+0.0057** / 参照 **−0.0542** ⇒ **需下移 ~6.0 cm**；② 网格高 我们 **1.9644** / 参照 **2.1127** ⇒ **比值 1.0755**；③ 手掌够不到：`l_hand` 偏 **0.15 m** / `r_hand` 偏 **0.69 m**；④ **归一化关节位置（÷自身身高）我们与参照结构完全一致** ⇒「下移 6 cm + X 缩放」**等效于把网格适配到 2.113 m 的身高**。

### 🔴🔴 第 37 轮：**发布的骨架骨轴曾是错的**（详见 findings §12）
- BatchTool 的「工作骨架」≠「发布骨架」：`rigging.py:_convert_pose_space` 是 `POSE_SPACE`(INITIAL↔TPOSE) × `BODY_AXIS_SPACE`(INITIAL↔SYMMETRIC) 两个状态机；**两套 basis 表互为逆元**（30 条 `initial ≈ inv(tpose)`，≤2e-5）⇒ 两次烘焙在**骨架局部旋转上抵消**、在**网格上叠加** ⇒ **发布的骨架 = HD2 原生，网格 = 源模型的 A-pose**。
- 修法 `axis_roundtrip=True`（默认）：`rest_sym` 局部再乘 `basis_t`；**新增步骤 4b** 把 `S_A=D·rest_sym` 的骨轴 `SYMMETRIC→INITIAL`（`L←L·inv(Dd)`，**head 必须回填**，否则右肩/肘/手被推到 0.25/0.81/1.27 m 外）。效果：朝向差 **43.90°/180° → 0.02°/0.05°**，`>5°` 骨 **45/107 → 0/107**。
- **⚠️ 更正**：`*_wxyz` 是 **w 在前**；20 条 delta 里**只有 2 条 180°**（`r_clavicle` 绕 +Z、`r_shoulderarmour`）。**`--no-axis-deltas` 不是正确修法**。

### 🔴 管线参数与三个真 bug（2026-10-12 用参照 patch 反查；详见 findings §9）
- **必用组合**：`--align yaw --spine-map mesh --fallback distance --leg-tilt 5.7 --leg-splay 4.0`。
  - `--align kabsch` 把身体转歪 2.8° ⇒ 脖子跑到髋**前** 1.75 cm（HD2 是**后** 2.22 cm）；根因 = 两侧 20 关节集**不全等**（源 `Pelvis` 在髋下 12 cm）。
  - `Spine→spine1` 让躯干每帧拉伸 26.5 cm（HD2 `spine1` 与 `hips` **完全重合**）。
  - 鞋被绑到膝盖 ⇒ 穿地 13.5 cm（源鞋父级 `Bip01_L_Calf`）。
- **【方法论·最重要】bind 恒等会掩盖真问题。** `D` 在绑定态恒等 ⇒ 绑定态指标全漂亮，**哪怕对应表是错的**。只有 `A[b]·inv(S_bind[b])` 的渲染能与参照 patch 直接对比。
- **`--leg-tilt`**：绕踝转 thigh/knee +θ；其他骨整体平移；foot/ball/toe **钉住**；网格同步混合变形。⚠️ 腿倾段的网格变换**必须**用 `resolve_bone_map(...)` 与主管线共用同一份骨映射（首版各查各的 ⇒ 鞋跟着躯干平移，抵消了髋前移）。tilt≈5.6° 复现 HD2 腿部站姿（踝在髋后 9.86 cm）。
- **`--leg-splay`**：绕腿根 `{l,r}_thigh` 绕 +Y 转 ±θ；thigh/knee 拿旋转、foot/ball/toe 拿**反向旋转**（踝刚性跟随 ⇒ **恰好塌缩成纯平移**）。**实测侧视逐点不变（max|Δy|=0）⇒ 纯正视操作。** 参照骨架 7.07° / 网格 4.0° 是两套角度 ⇒ **选 4.0°（沿用作者"少掰"的做法）**；⚠️ 归一化必须用全身高；⚠️ 命名别用 `R`（会遮蔽外层对齐矩阵）。
- **【第 4 个真 bug·未修】脚落点整体前移 ~6 cm**：管线 `t` **只锁头关节** ⇒ 脚底贴片质心 我们 `y +0.0427` / 参照 `−0.0198`。X 那一半已被 `--leg-splay` 顺手修掉；**Y 那一半仍未动**。修法 = XY 锚点改成"脚底贴片"（Z 仍锁头关节）。
- **头/尺寸（已降级）**：源 `Head1` 是**颅底**、HD2 `head` 是**颅顶** ⇒ 语义差；参照用"几何骑在关节上"解决（同一 `head` 关节，参照头部几何在其上 0.368 m、我们 0.293 m ⇒ **我们小 ~20%**）⇒ 降级为"可选微调"。

### 待办（网格变换，用户已给完整方案；**用户要求一步一步来**）
> 用户原话：「**我知道这变换很难做，如果你没有把握，我们可以继续沟通交流，先停下来不直接改也行，不要贪功冒进**」。
1. ✅ **整体平移**（下移 6.0 cm / 后移 10.0 cm）—— 已完成；2. **上半身放大**（目标手掌够到 HD2 手掌骨；用户指出**参照的上衣连衣裙与我们是同一模型**，可用特征点求缩放比；整体高比 1.0755 是起点）；3. **接缝渐变缩放**（躯干/大腿接缝，PS 羽化式）；4. **手臂少弯**（降低 `basis_i` 里手臂的旋转量，尤其肘部；**不是拉长手臂**）；5. **头部比例**（参照二次元大号头）。
> **分工**：用户**空间感强、善于发现问题**但**对距离/数值不敏感**；我**视觉空间判断弱**但**数值/拟合可靠** ⇒ **用户负责"指出哪里不对"，我负责"用数值定出多少"**。
- 后续：提取干净网格 → 按参照风格重刷权重 → 上 C#（`HD2SourceMigration`）→ 产出离线 legacy 版 patch 包 → 进游戏实测（用户负责）。**动态物理骨骼仍要支持**（L4D2 风味 `jiggle_data.lua`，添头）；**硬要求：没有 Lua 支持 legacy 包也必须能跑**；**不依赖 `HD2IRG1` 的动态骨骼**。

## 坐标约定 & 参照 patch
- **HD2 坐标：+Y = 前，+Z = 上，+X = 角色的「右」**（**角色左侧在 −X**）。⚠️ 早期笔记写成"+X = 左"是**笔误**；实测 HD2 原生 `l_foot` x = **−0.1908**、`r_foot` = **+0.1908**（`patch_view.foot_metrics(side=-1)` 也按 −X = 左脚）。**`l_foot` 关节不在脚心**：HD2 原生 踝在髋后 9.60 cm / 源 ≈ 同 ⇒ **本来就一致**。
- 参照 patch = `E:\mod\【4.28】Chiffon-LUNALICE替换RE-2310`（E 盘 = 用户**测试**库，可随便操作；D 盘 steam = **实际在用**）。容器 `9ba626afa44a3aa3.patch_0`(216 KB)+`.gpu_resources`(219 MB)+`.stream`(0 B)，**无 manifest.json**；**35 条目**（23 Unit+2 Bone+10 未知），**只 9 个 Unit 有几何**；**渲染选 `cae35ee15168145f,17da843a4908511e,91eb7be84016ceab,7336c6b662522f0e` + 头 `af431352d9b95e83`**（RE-2310 头盔 = 该 id，标签是 `Torso_Undergarment_Slim_lod3`，几何是"脸+猫耳+长发"；容器里**没有 unit 叫 helmet** ⇒ **不要从游戏本体取头盔**）。加上它完整参照 **z[−0.054,+2.059]、总高 2.113 m**。
- **【关键判据】补丁骨架与 HD2 原生逐位一致**（13 根骨 delta 全 0），只多出 `*_culling`/`*_twist` 辅助骨 ⇒ **"比例优化"全在网格层，骨骼照抄 HD2。** **读取三坑**：① **Unit 骨矩阵是行向量约定（平移 flat 12,13,14）**，与 `E.read_hd2_rig`（已转置）相反 —— 读错**不报错**；② bone 列表**前 9 项是场景图节点**，真骨从 `skeleton` 起，`boss` 是髋部根运动骨；③ **Unit 骨名是空的**，靠 32-bit hash 查 `bonehash`。

## 旧项目 / 新工具（WpfApp1 内重写，进行中）
- **旧项目编译不过（预先存在）**，最后提交 **2026-09-03**；**用户已拍板：在 WpfApp1 内重写，不修旧项目。** 方案：**`HD2SourceModel`**（net8，**零引用**，编译期强制不认识 HD2）✅ + **`HD2SourceMigration`**（**唯一同时引两侧**）✅骨架（待换新数学）+ `HD2ModCli` 薄子命令 + 各自 `*.Tests`。HD2 侧复用 `UnitMeshReader.ReadTransformInfoFromUnitToc` + `UnitBoneNamesReader`；`HD2ModAdaptation/.../Canonical/` 有 **31 个 `Canonical*` 编译器**（"给 Unit 加骨 + 重写蒙皮"能力现成）；矩阵统一 `System.Numerics.Matrix4x4`（float、行向量）。**Source 格式权威在 `HD2SourceModel/*.cs` 注释。** ⚠️ **实测三次证伪规范**（合成样本测不出，只有真实文件能证伪）：① **VPK 条目步长与版本号无关**（97/97）；② **MDL 骨步长 216 B 对 v44/48/49/53 全成立**；③ **bodypart/model 名是 UTF-8**（如 `裙Dress_Skirt`）。

## 其他（细节全在 `docs/`）
- **大功能 A（已被 B 取代）**：引擎 = Stingray(Bitsquid)，**裁掉全部物理命名空间**；`Unit`/`Mesh` **零 `create_*` / 零顶点权重接口** ⇒ **纯运行时 Lua 改不了权重、加不了骨、造不出物理**。**改内存边界：改「数据」✅ / 改「代码」❌**。**【硬约束】只允许依赖游戏本体，不得依赖任何外部系统（尤其其他 mod）**。
- **信息中心（阶段 0–6 已完成）**：`ModInformationFactRegistry` = **25 kind** 单一真相，`RequestAsync<T>` = 唯一读取入口，`Validate()` 在 `CreateModInformationCenter` 开头跑（新增 kind 忘登记 `SourceRoots` = **启动期失败**）。铁律：**缓存不得取代真实性**（键全由来源指纹构成、不落盘、双向测试）；**「读不到」≠「没有覆盖」**；**图标不进事实**。
- **护甲映射页 20261011 回归**（`docs/armor-registry-page-20261011-regression.md`）：兜底已落实（`8d06fcb`/`5b79acc`），**根因未动** —— **拿「升级必被摧毁的凭据」(`deployment`) 守护「升级必被保留的文件」(`protocol`) ⇒ 设计上不可能可靠**；崩溃三条件：①点过应用 ②库无 `workspace.json` ③`deployment` 不存在。**回归 #2（破坏性，未动）**：`ModLibraryResidualCleanupService.Cleanup(...)` 会 `Directory.Delete(recursive: true)` 删库根"未被快照引用且空"的子目录。
