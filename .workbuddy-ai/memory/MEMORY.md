# 项目长期记忆：HD2ModManager

> **本文件 = 索引 + 硬约束 + 易忘的坑**，不是细节仓库。细节在 `docs/*.md`（**被 .gitignore，改前先备份 `docs/_backup/`**）与 `.workbuddy-ai/memory/YYYY-MM-DD.md`。

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
- **【环境】** ① Python 用隔离 venv `...\python\envs\default\Scripts\python.exe`（有 numpy+Pillow；**系统 python 无 PIL**）；② `git clone`/`curl` 打 GitHub 常失败 → 让用户下 zip；③ **Bash heredoc 调 Python/PowerShell 被拦** → 写成 `tmp/*.py` 再执行；④ **`Edit` 对含大量反引号/emoji 的长中文行会匹配失败** → 用 Python `str.replace`/`append` 并回读校验；⑤ **`Remove-Item` 被 safe-delete 钩子接管** → 让用户自己跑，**不要**用 `[System.IO.File]::Delete` 绕过；⑥ Blender 在 `D:\SteamLibrary\steamapps\common\Blender\blender.exe`（根 exe = **4.3.2**）。

## 大功能 B（当前方向）：L4D2 模型整体移植到 HD2
> 细节见 `docs/l4d2-to-hd2-physbone-migration.md`、`docs/l4d2-hd2-boundary.md`、**`docs/hd2-batchtool-sdk-findings.md`**（§7 源码逐函数核对；§8 新管线；§9 三个真 bug；§10 参照 patch；§11 站姿；§12 骨轴往返；§13 发布骨架；§14 mesh 枢轴解耦；§15 网格平移；§16 模型完整性；§17 躯干缩放；§18 区域兜底；§19 手臂重拟合）。

### 用户界定的目标（第 39 轮，最高优先级）
- **不是"一键转换"黑盒，而是"录一个人工操作流的宏"**：① 把源 T-pose 模型摆成参照的站姿/比例（**纯数学变换**，只借 BatchTool 的变换部分）；② 按参照的权重风格重刷权重；③ 全自动 legacy 流程。
- **骨架与模型没有直接联系，骨架只和权重有关（间接影响模型）。** 参照 patch 骨架**一根骨没动**（= HD2 原生），"比例优化"全在**网格层**。HD2 标准骨架/游戏帧模拟图**只是对照手段**。
- 先只做第 ① 步：把网格摆好 → 提取干净网格、丢掉一切源骨/源权重 → 再用干净 HD2 原生骨架按参照风格上权重。**无权重时游戏帧必须 == 绑定帧**。
- **【渲染判据·最重要】bind 恒等会掩盖真问题**：`D` 在绑定态恒等 ⇒ 绑定态网格永不变形，指标全漂亮，**哪怕对应表是错的**。只有 `A[b]·inv(S_bind[b])` 的渲染（`out_idle`）能与参照 patch 直接对比。

### 当前管线（`tmp/rig-lab/hd2_style.py`）—— 必用组合
`--align yaw --spine-map mesh --fallback region --leg-tilt 5.7 --leg-splay 4.0 --mesh-down 0.060 --mesh-back 0.100 --upper-scale 1.12 --upper-pivot-z 1.15 --upper-blend 0.45 --arm-fit 1.0 --arm-perp 0.0 --arm-smooth 8`

**机制（每条都已实测，勿回退）**
- **§8 基础数学** = BatchTool 那套：**一个均匀缩放 + 一个平移**（围绕脚底 ⇒ 源脚底精确落 HD2 脚底；`TorsoScale=1.042` 是算出来的）；**HD2 骨的 head := 源映射骨的世界位置**（**只改位置**，朝向/骨长仍是 HD2 自己的）；**30 骨常量局部旋转表**（A↔T，全是手臂+手）+ **20 条右侧骨轴 delta**；**吸附在绑定之前 ⇒ 绑定态恒等**。5 模型 **edge_mean 0.9989、bad>25% ≤0.58%**，**零逐模型调参**。要点：① 缩放高度必须在 HD2 空间沿 **+Z** 量，脚底点用**底部 1 cm 顶点片质心**；② **AXIS delta 与 head 搬移必须交错在同一"父先子后"循环**（否则被搬走父骨的**未映射子骨**会掉队）。
- **§12 `axis_roundtrip=True`（默认，`--legacy-axis` 关）**：BatchTool「工作骨架」≠「发布骨架」，两套 basis 表**互为逆元**（30 条 `initial≈inv(tpose)` ≤2e-5）⇒ 骨架局部旋转**抵消**、网格**叠加** ⇒ **发布骨架 = HD2 原生，网格 = 源模型的 A-pose**。做法：`rest_sym` 局部再乘 `basis_t`；**步骤 4b** 把 `S_A=D·rest_sym` 的骨轴 `SYMMETRIC→INITIAL`（`L←L·inv(Dd)`，**head 必须回填**，否则右肩/肘/手被推到 0.25/0.81/1.27 m 外）。效果：朝向差 **43.90°/180° → 0.02°/0.05°**，`>5°` 骨 **45/107 → 0/107**。
- **§13 `skeleton='native'`（默认）**：`S_bind[b] = rest_hd2[b]` **一字不改** ⇒ `rest_hd2·inv(S_bind)=I`，**5 模型 idle 网格差 0.00e+00 m**。`snap` 保留供 A/B（每帧被拽 = 鞋跟没了/脚踝断裂/脖子拉长/裙子两片）。**§14**：A-pose 烘焙**绕骨关节旋转网格** ⇒ **枢轴必须在网格内**；网格烘焙**永远**用吸附后的 `rest_sym`，`native` 只改**步骤 4b**（两件事必须解耦）。
- **§15 mesh 平移**：`mesh_shift=(dx,dy,dz)` **只平移烘焙后的 `out`**，骨架绝不移动。当前 **下移 6.0 / 后移 10.0 cm**（拟合：dy −0.100 / dz −0.060，中位残差 44 mm）。
- **§17 mesh 上半身缩放**：`upper_scale`/`upper_pivot_z`（默认 None = `hips` 的 z）/`upper_blend`。`z0/z1 = piv ± blend`；`w = smoothstep((z−z0)/(z1−z0))`；`s = 1+(scale−1)·w`；`out = piv+(out−piv)·s`。**只动网格**。当前 **1.12 / 1.15 / 0.45**。
- **§18 兜底 `fallback='region'`（默认）**：源骨里没有 HD2 对应物的（`source_extra_*` = jiggle/头发/丝带/裙摆）**不能只按骨关节最近**（会把一条源骨链拆到不同肢体）。改为**距离 + 同肢体约束**：沿 HD2 层级上溯，遇 `l/r_clavicle`→`l/r_arm`、`l/r_thigh`→`l/r_leg`、`neck`→`head`，否则 `torso`；`pool = {c∈cand : limb(c)==limb(ancestor)}`，空则退回全部候选。`resolve_bone_map(...)` 新增 `hd2_children`/`hd2_parents`。**⚠️ 不能用「祖先的子树」当约束**：HD2 的 `hips` 与 `spine1` 是 `boss` 下的**兄弟**，`subtree(hips)` 只有腿（12 根）⇒ 裙子会被砍到大腿。`distance` 保留供对照。
- **§19 `arm_fit`（默认 1.0）/`arm_perp`（0.0）/`arm_smooth`（8）—— 手臂重拟合**：逐骨残差 `d[b] = 发布头(rest_shipped[b]) − 工作头(D[b]·rest_sym[b])`（**非手臂骨置零**；实测 `l_shoulder` 0.182 / `l_elbow` 0.183 / `l_hand` 0.167 m）；顶点位移 = **按 LBS 权重混合残差**，**只作用在网格**（骨架绝不移动），再 **Laplacian 平滑 `arm_smooth` 遍**；`arm_perp=0` 只取**沿臂轴分量**（`u·(d·u)`）—— 因为 `mesh_shift` 已承担垂直部分，全 3D 残差会**重复计算**（arm_perp=1 时手臂比参照后移 13 cm）。**不加平滑则 `edge max` 2.15→12.43**（胸口/锁骨 14 个 >5x 极小三角，权重跳变）⇒ 平滑 8 遍后降到 6.90（arm_perp=0 时 **3.15**、bad>25% **1.00%**）。根因是**工作关节（源关节）≠ HD2 关节**（肘靠内 8.6 cm、指尖 5.5 cm、整臂后移 ~9 cm），**不是"弯"**；每段长度本来就对。A/B：肘 **0.194→0.282**（骨 0.286，差 3 mm）、指尖 **0.642→0.729**。**纯数学、不碰骨架、不做骨骼对齐**。
- **`--leg-tilt <deg>`**：绕踝 `0.5*(l_foot+r_foot)` 转 thigh/knee +θ；其他骨整体平移（**躯干竖直不旋转**）；foot/ball/toe **钉住**；网格同步混合变形。⚠️ 腿倾段的网格变换**必须**用 `resolve_bone_map(...)` 与主管线共用同一份骨映射。tilt≈5.6° 精确复现 HD2/参照站姿（踝在髋后 9.86 cm）。
- **`--leg-splay <deg>`**：绕腿根 `{l,r}_thigh` 绕 +Y 转 ±θ；thigh/knee 拿旋转、foot/ball/toe 拿**反向旋转**（塌缩成纯平移）。**侧视逐点不变（max|Δy|=0）**。选 4.0°；⚠️ 归一化用全身高；⚠️ 命名别用 `R`（会遮蔽外层对齐矩阵）。

**历史 bug（勿回退）**
- **§9 三个真 bug**：① `--align kabsch` 把身体转歪 2.8°（脖子跑到髋**前** 1.75 cm；两侧 20 关节集**不全等**）⇒ **`yaw`**；② `Spine→spine1` 让躯干每帧拉伸 26.5 cm（HD2 `spine1`≡`hips`）⇒ **`--spine-map mesh`**；③ 鞋被绑到膝盖 ⇒ 穿地 13.5 cm（源鞋父级 `Bip01_L_Calf`）⇒ **`--fallback region`**（`distance` 是其前身）。
- **§16 模型完整性**：`drop_outlier_tris` 旧规则阈值 `0.0600 m` 正好切在三角形边长 **p99（0.0645）** ⇒ **误删 510 顶点**（`Base` **马尾** 270 + `platform shoes` **鞋后跟厚底** 133 + 裙子/丝带/袜口）。**已改为半径规则** `cut = 0.85 × height`（中心 = 顶点中位数）⇒ **zoey/rochelle/coach 隐藏 0**，nick 579 / ellis 691（只藏 `LongSleeve`/`Tail` 远端）。**不存在 1.8 m 尖刺**（三态最大边 0.12–0.34 m）。**教训：「数据里没有」≠「图里看不到」。**
- **【第 4 个真 bug·未修】脚落点整体前移 ~6 cm**：管线 `t` **只锁头关节**（文档却写 "locking the visible foot sole"）⇒ 脚底贴片质心 我们 `y +0.0427` / 参照 `−0.0198`。**X 那一半已被 `--leg-splay` 顺手修掉**；**Y 那一半仍未动**。修法 = XY 锚点改成"脚底贴片"（Z 仍锁头关节）。
- **头/尺寸（已降级）**：源 `Head1` 是**颅底**、HD2 `head` 是**颅顶**；参照头部几何在 `head` 关节**上** 0.368 m、我们 0.293 m ⇒ **我们小 ~20%** ⇒ 可选微调，非阻塞。
- **⚠️ 探针踩坑**：改网格的变换**作用于枢轴以上所有顶点**（手/头一起走），只对"躯干权重顶点"应用会得到**假结果**。`probe_torso*.py` / `probe_upper*.py`。

**不变量（每次改完必跑 `tri_overlay.py`，全部不得回退）**
```
mesh down 0.0600 back 0.1000 | upper scale x1.120 pivot z 1.150 blend 0.45
our zmin -0.0612 height 2.0731 | ref zmin -0.0542 height 2.1127
idle vs bind 6.76e-16 m | shipped rig vs HD2 7.22e-16 m
```

### Blender 观察工具（第 43 轮，全在 `tmp/rig-lab/`）
- 链：`dump_blender.py`（venv python）→ `out/blender/model.npz`+`meta.json`；`blender_build.py`（`blender.exe --background --factory-startup`；**根 exe = 4.3.2**，用它写 `.blend` 才能被 4.3/4.5/5.0 打开，反之不行）→ `zoey.blend`(79 MB)+`zoey.glb`(43 MB)；`blender_verify.py`（验证）、`blender_render.py`（Cycles 冒烟图，含 `FRONT/SIDE/BACK/HEAD_BACK/HEAD_FRONT`）。**换臂后旧图备份在 `out/blender/before-arm/`。**
- **骨轴**：两引擎骨都沿局部 **+X**（`export_obj.bone_axis`），**Blender 沿 +Y** ⇒ `head=rest_hd2[:3,3]`、`tail=head+L·rest_hd2[:3,0]`、`align_roll(rest_hd2[:3,2])`。所得骨系与 HD2 差一个**所有骨共用的固定旋转 M** ⇒ **在 `A·inv(S)` 里抵消 ⇒ 变形与游戏等价**（验证：静止态网格差 **2.4e-7 m**；掰 `l_elbow` 0.6 rad ⇒ 左前臂 **6423** 顶点动）。**骨长不在数据里** ⇒ 用子骨 head 在骨轴上的**投影**推导（全侧向子骨退回最近子骨；叶骨借父 0.6×；clip 0.01–0.55）⇒ **head/轴向精确，tail 仅供参考**。
- `.blend` 集合 `OURS`（bind 蓝 + game 绿隐藏 + 骨架）/`SOURCE`/`REFERENCE`（**只放实际对比的 5 个 unit**）/`REFERENCE_alt`（隐藏，⚠️ **patch 同时含 Slim+Stocky 两套体型**）。骨 0–8 = 场景图/辅助节点（`boss` = 髋部根运动骨）+ 10 根无名骨。⚠️ Blender 把相对 `render.filepath` 解析到 **`C:\out\`** ⇒ 传绝对路径；`me.validate()` 丢 8 个退化三角形。

### 待办（网格变换；**用户要求一步一步来**）
1. ✅ 整体平移（下移 6.0 / 后移 10.0 cm）；**1.5 ✅ 模型完整性**；**2. ✅ 上半身放大**（1.12/1.15/0.45，残差 0.0178 m）；**2.5 ✅ 区域兜底**（`fallback='region'`）；**4. ◐ 手臂**（第 45 轮，`arm_fit` 已实现并回归，待用户验收）；3. **⏸ 接缝渐变缩放（用户 2026-10-12 拍板：暂缓）** —— 用户看过 Blender 后确认「接缝处已经非常好了」，**先不做**；等①进游戏实测 ②多来源模型按同一数学变换转换后，若暴露接缝问题再回来做（第 42 轮 smoothstep 是雏形）；5. **头部比例**（参照二次元大号头，我们小 ~20%）；6. **下半身放大**。
> **分工**：用户**空间感强、善于发现问题**但**对距离/数值不敏感**；我**视觉空间判断弱**但**数值/拟合可靠** ⇒ 用户负责"指出哪里不对"，我负责"用数值定出多少"。
- 后续：提取干净网格 → 按参照风格重刷权重 → 上 C#（`HD2SourceMigration`）→ 产出离线 legacy 版 patch 包 → 进游戏实测（用户负责）。**动态物理骨骼仍要支持**（L4D2 风味 `jiggle_data.lua`，添头）；**硬要求：没有 Lua 支持 legacy 包也必须能跑**；**不依赖 `HD2IRG1` 的动态骨骼**。
- ⚠️ **nick 过冲（待用户拍板）**：`arm_fit` 是全局的（零逐模型调参），nick 是唯一"本来超长"的模型，肘 +0.090→**+0.191**、指尖 +0.126→+0.251。zoey/rochelle 肘 **+0.0055**、coach +0.026、ellis L+0.074/R−0.007，均改善。

## 坐标约定 & 参照 patch
- **HD2 坐标：+Y = 前，+Z = 上，+X = 角色的「右」**（左侧在 −X）。⚠️ 早期笔记"+X = 左"是**笔误**；实测 `l_foot` x = **−0.1908**、`r_foot` = **+0.1908**。**`l_foot` 关节不在脚心**：HD2 原生 踝在髋后 9.60 cm / 源 ≈ 同 ⇒ **本来就一致**。
- 关键关节（`rest_hd2`，与参照 patch 逐位一致）：`hips` z **0.9735**；`spine2` z 1.1633；`chest` z 1.3437；`neck` z **1.5500**；`head` z 1.6908；`l_shoulder` x −0.2101 z 1.4745；`l_elbow` x −0.4723 z 1.3236；`l_hand` x −0.6566 z 1.2189。`l_shoulder→l_hand` 骨长 **0.5553 m**（沿轴 t：elbow 0.286 / hand 0.555 / index_finger3 0.680 / middle_finger3 0.697）。
- 参照 patch = `E:\mod\【4.28】Chiffon-LUNALICE替换RE-2310`（E 盘 = 用户**测试**库，可随便操作；D 盘 steam = **实际在用**）。容器 `9ba626afa44a3aa3.patch_0`(216 KB)+`.gpu_resources`(219 MB)，**无 manifest.json**；**35 条目**（23 Unit+2 Bone+10 未知），**只 9 个 Unit 有几何**。
- **参照 unit 有 tag**（`bones` 前 9 项是场景图节点）。实际对比只用 5 个：`cae35ee15168145f` **躯干上衣**(z[0.423,1.715])、`17da843a4908511e` Hip、`91eb7be84016ceab`/`7336c6b662522f0e` 左右臂、`af431352d9b95e83` **RE-2310 头盔 = 脸+猫耳+长发**(z 到 **2.059**；容器里**没有 unit 叫 helmet** ⇒ **不要从游戏本体取头盔**)。⚠️ **patch 同时含 Slim+Stocky 两套体型**（另 4 个 unit）。加头后完整参照 **z[−0.054,+2.059]、总高 2.113 m**。
- **【关键判据】补丁骨架与 HD2 原生逐位一致**（13 根骨 delta 全 0），只多 `*_culling`/`*_twist` 辅助骨 ⇒ **"比例优化"全在网格层，骨骼照抄 HD2。** **读取三坑**：① **Unit 骨矩阵是行向量约定（平移 flat 12,13,14）**，与 `E.read_hd2_rig`（已转置）相反 —— 读错**不报错**；② bone 列表**前 9 项是场景图节点**，真骨从 `skeleton` 起，`boss` 是髋部根运动骨；③ **Unit 骨名是空的**，靠 32-bit hash 查 `bonehash`。

## 旧项目 / 新工具 / 其他（细节全在 `docs/`）
- **旧项目编译不过（预先存在）**，最后提交 **2026-09-03**；**用户已拍板：在 WpfApp1 内重写，不修旧项目。** 方案：`HD2SourceModel`（net8，**零引用**，编译期强制不认识 HD2）✅ + `HD2SourceMigration`（**唯一同时引两侧**）✅骨架 + `HD2ModCli` 薄子命令 + 各自 `*.Tests`。HD2 侧复用 `UnitMeshReader.ReadTransformInfoFromUnitToc` + `UnitBoneNamesReader`；`HD2ModAdaptation/.../Canonical/` 有 **31 个 `Canonical*` 编译器**；矩阵统一 `System.Numerics.Matrix4x4`（float、行向量）。**Source 格式权威在 `HD2SourceModel/*.cs` 注释。** ⚠️ **实测三次证伪规范**（合成样本测不出，只有真实文件能证伪）：① **VPK 条目步长与版本号无关**（97/97）；② **MDL 骨步长 216 B 对 v44/48/49/53 全成立**；③ **bodypart/model 名是 UTF-8**（如 `裙Dress_Skirt`）。
- **大功能 A（已被 B 取代）**：引擎 = Stingray(Bitsquid)，**裁掉全部物理命名空间**；`Unit`/`Mesh` **零 `create_*` / 零顶点权重接口** ⇒ **纯运行时 Lua 改不了权重、加不了骨、造不出物理**。**改内存边界：改「数据」✅ / 改「代码」❌**。**【硬约束】只允许依赖游戏本体，不得依赖任何外部系统（尤其其他 mod）**。
- **信息中心（阶段 0–6 已完成）**：`ModInformationFactRegistry` = **25 kind** 单一真相，`RequestAsync<T>` = 唯一读取入口，`Validate()` 在 `CreateModInformationCenter` 开头跑（新增 kind 忘登记 `SourceRoots` = **启动期失败**）。铁律：**缓存不得取代真实性**；**「读不到」≠「没有覆盖」**；**图标不进事实**。
- **护甲映射页 20261011 回归**（`docs/armor-registry-page-20261011-regression.md`）：兜底已落实（`8d06fcb`/`5b79acc`），**根因未动**（拿「升级必被摧毁的凭据」`deployment` 守护「升级必被保留的文件」`protocol` ⇒ 设计上不可能可靠）；崩溃三条件：①点过应用 ②库无 `workspace.json` ③`deployment` 不存在。**回归 #2（破坏性，未动）**：`ModLibraryResidualCleanupService.Cleanup(...)` 会 `Directory.Delete(recursive: true)` 删库根"未被快照引用且空"的子目录。
