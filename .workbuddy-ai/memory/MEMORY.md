# 项目长期记忆：HD2ModManager

> **索引 + 硬约束 + 易忘的坑**，不是细节仓库。细节在 `docs/*.md`（**被 .gitignore，改前先备份 `docs/_backup/`**）与 `.workbuddy-ai/memory/YYYY-MM-DD.md`（历史全文在 `_backup/`）。

## 项目定位
- 仓库 `WpfApp1`：《绝地潜兵 2》Mod 管理器与补丁适配工具集，GPLv3。`ManagedMain` 已过时。生产工程：`HD2ModManager`(WPF/net10)、`HD2ModCli`(net10，`hd2mod`)、`HD2ModCore`(net8)、`HD2ModAdaptation`(net8)、`Decryptor`、`HD2GameItemAssets` + `*.Tests`；依赖链 `Cli → Core → {Adaptation, GameItemAssets, Decryptor}`。
- `HD2RuntimeArmorOverride` = 护甲运行时映射的 Lua mod 源码（非 csproj），lua 被 `HD2ModCore.csproj` 内嵌。`CoreServices` 是**唯一装配点**（判"某服务是否被生产使用"看 `CoreServices.CreateXxx(` 调用点是否为 0）。

## 工作约定（硬约束）
- **【强制】只做本地提交，不推送远程**（用户手动推送）。**授权范围内的改动直接做，不反复请示。**
- **用户不是专业程序员**：涉及"要不要删/要做什么"，先用通俗语言说明作用与后果，再给建议。
- **临时脚本一律 `tmp/`（已 gitignore），不要写进 `_diagnostics/`** —— 后者是条件测试目录，一旦存在（哪怕空的）就会真跑并失败。**`tmp/rig-lab/` = 本功能的离线「斧」**（`*.py` 已入库，`out/` 忽略）。
- **写完"应能抓住某 bug"的用例后，主动破坏被保护的那一行再跑**；仍通过 = 用例是假的。删除类改动**必须同时改造其测试**。
- **`tools/check_dead_code.py` 是词匹配**（注释里写类名会误判可达）→ **必须两方向一起看**（`comm -13` + `comm -23`）。
- `Directory.Build.props`：`ImplicitUsings`/`Nullable`/`Deterministic` 开、**`TreatWarningsAsErrors=true`**（排除 `NU1901..1904`）。TargetFramework/OutputType/引用**刻意不在此文件**。
- 耗时：全解决方案 `dotnet test` **6–7 分钟**；`dotnet build -t:Rebuild` **20 秒**。**报测试计数要分项+增量；基线会变，报数前先跑一遍。**
- 发布：**轻量标签**；溯源靠包内 `cliVersion`（`1.0.0+<git 全哈希>`）；校验产物用 Python `zipfile`（**别用 PowerShell `Add-Type`**，被拦）。
- **【数据归属·硬约束】用户自定义数据一律存**模组库**；设置页简单设置存 `settings.json`；**能从游戏 data 重算的纯缓存**才放管理器目录。目标：**换管理器 = 无痛继承模组库；迁移模组库 = 只改库地址即可。** ⚠️ 2026-10-11 护甲页崩溃的根因就是违反了这条。**新增持久化文件前先问：属于库、属于设置、还是纯缓存？**
- `git commit` 提交**整个索引**，与 `git add` 传了哪些路径无关。
- **【环境】** ① Python 用隔离 venv `...\python\envs\default\Scripts\python.exe`（有 numpy+Pillow；**系统 python 无 PIL**）；② `git clone`/`curl` 打 GitHub 常失败 → 让用户下 zip；③ **Bash heredoc 调 Python/PowerShell 有时被拦**（SIGTERM）→ 优先写成 `tmp/*.py` 再执行；④ **`Edit` 对含大量反引号/emoji 的长中文行会匹配失败** → 用 Python `str.replace` 并回读校验；⑤ **`Remove-Item` 被 safe-delete 钩子接管**，让用户自己跑，**不要**用 `[System.IO.File]::Delete` 绕过；⑥ Blender 在 `D:\SteamLibrary\steamapps\common\Blender\blender.exe`（根 exe = **4.3.2**）。

## 大功能 B（当前方向）：L4D2 模型整体移植到 HD2
> 细节见 `docs/l4d2-to-hd2-physbone-migration.md`、`docs/l4d2-hd2-boundary.md`、**`docs/hd2-batchtool-sdk-findings.md`**（§8 新管线；§9 三个真 bug；§10 参照 patch；§11 站姿；§12 骨轴往返；§13 发布骨架；§14 枢轴解耦；§15 网格平移；§16 完整性；§17 躯干缩放；§18 区域兜底；§19/§20 手臂重拟合与复审；**§21 手臂角度**）。

### 用户界定的目标（第 39 轮，最高优先级）
- **不是"一键转换"黑盒，而是"录一个人工操作流的宏"**：① 把源 T-pose 模型摆成参照的站姿/比例（**纯数学变换**，只借 BatchTool 的变换部分）；② 按参照的权重风格重刷权重；③ 全自动 legacy 流程。
- **骨架与模型没有直接联系，骨架只和权重有关（间接影响模型）。** 参照 patch 骨架**一根骨没动**（= HD2 原生），"比例优化"全在**网格层**。HD2 标准骨架/游戏帧模拟图只是对照手段。
- 先只做第 ① 步：网格摆好 → 提取干净网格、丢掉一切源骨/源权重 → 再用干净 HD2 原生骨架按参照风格上权重。**无权重时游戏帧必须 == 绑定帧**。
- **【渲染判据·最重要】bind 恒等会掩盖真问题**：`D` 在绑定态恒等 ⇒ 绑定态网格永不变形、指标全漂亮，**哪怕对应表是错的**。只有 `A[b]·inv(S_bind[b])` 的渲染（`out_idle`）能与参照 patch 直接对比。

### 当前管线（`tmp/rig-lab/hd2_style.py`）—— 必用组合
```
--align yaw --spine-map mesh --fallback region --leg-tilt 5.7 --leg-splay 4.0
--mesh-down 0.060 --mesh-back 0.100 --upper-scale 1.12 --upper-pivot-z 1.15
--upper-blend 0.45 --arm-fit 1.0 --arm-up 0.55 --arm-perp 0.0 --arm-smooth 8
--arm-taper -0.05 0.22 --arm-rot 9.90 -17.25 21.50 --arm-rot-smooth 8
```
实验室环境变量 `HD2_ARM_ROT` / `HD2_ARM_ROT_SMOOTH` 可让**所有**写死参数的探针一起切换（探针显式传 `arm_rot` 时以探针为准）。

**机制（全部已实测，勿回退；推导与数字见 findings 对应 §）**
- **§8** 一个均匀缩放 + 一个平移（围绕脚底）+ **HD2 骨的 head := 源映射骨的世界位置**（只改位置）+ 30 骨常量旋转表 + 20 条右侧骨轴 delta。**吸附在绑定之前 ⇒ 绑定态恒等。** ⚠️ 缩放高度沿 HD2 **+Z** 量（脚底 = 底部 1 cm 顶点片质心）；**AXIS delta 与 head 搬移必须交错在同一"父先子后"循环**。
- **§12** 工作骨架与发布骨架两套 basis 表互为逆元 ⇒ 骨架旋转**抵消**、网格**叠加** ⇒ **发布骨架 = HD2 原生，网格 = 源模型的 A-pose**。
- **§13/§14** `skeleton='native'`（默认）：`S_bind[b] = rest_hd2[b]` 一字不改 ⇒ **idle 网格差 0**。**枢轴必须解耦**：网格烘焙永远用吸附后的 `rest_sym`，`native` 只改步骤 4b。
- **§15/§17** `mesh_shift` 只平移烘焙后的 `out`（−6.0 cm / −10.0 cm）；`upper_scale/pivot_z/blend`(1.12/1.15/0.45) = 只动网格的 smoothstep 缩放。**骨架绝不移动。**
- **§18** `fallback='region'`：`source_extra_*`（jiggle/头发/丝带/裙摆）不能只按骨关节最近 ⇒ **距离 + 同肢体约束**（上溯到 `l/r_clavicle`→`arm`、`l/r_thigh`→`leg`、`neck`→`head`，否则 `torso`）。**⚠️ 不能用「祖先的子树」**：`hips` 与 `spine1` 是 `boss` 下的**兄弟**。
- **§19/§20** `arm_fit`(1.0)/`arm_up`(0.55)/`arm_perp`(0)/`arm_smooth`(8)/`arm_taper`(−0.05,0.22)：逐骨残差（非手臂骨置零）**按 LBS 权重混合**、只作用在网格 + Laplacian 平滑。根因 = **工作关节（源关节）≠ HD2 关节**，**不是"弯"**。**`arm_taper` 必须开**：`l_shoulder` 点云沿臂轴跑到 **t=−0.085**（上胸/腋窝被绑给了肩骨）⇒ 否则**腋窝裂开 / 大臂"断裂"**。`arm_perp` 默认 0（全量会把手臂推到参照**后面 12 cm**）。
- **§21** `arm_rot`(9.90/−17.25/21.50)/`arm_rot_smooth`(8)：**平移修位置、旋转修方向**。**俯视 X-Y 本来就对上、正视 X-Z 不对 ⇒ 修正轴 = 绕世界 Y**。三段嵌套、**ROOT→TIP**（每段转完把**子关节**一起搬走，否则肘偏 5.5 cm）、正=抬高、只加在 `out` 上。**权重必须用源模型自己的 LBS 权重**（0 肩/1 肘/2 腕三组）—— 自造"沿臂轴位置斜坡"会**静默地把腿卷进来**（`u` 指向斜下方，脚在 `t=+0.6` > 腕 0.555）。正视折角 21.5°→**5.2°**（参照 3.6）；边长指标不劣于旋转前；腋窝未复发。剩余：**俯视漂 2–4°**、肘折角 3D 29.42 vs 24.96。
- **`--leg-tilt`**(5.7) 绕踝转 thigh/knee、foot/ball/toe 钉住；**`--leg-splay`**(4.0) 绕腿根绕 +Y 转 ±θ、末端反向（**侧视逐点不变**）。⚠️ 腿倾段网格变换**必须**用 `resolve_bone_map(...)` 共用同一份骨映射；命名别用 `R`。

**历史 bug（勿回退）**
- **§9 三个真 bug**：① `--align kabsch` 把身体转歪 2.8° ⇒ **`yaw`**；② `Spine→spine1` 让躯干每帧拉伸 26.5 cm ⇒ **`--spine-map mesh`**；③ 鞋被绑到膝盖 ⇒ 穿地 13.5 cm ⇒ **`--fallback region`**。
- **§16 完整性**：旧阈值 0.0600 m 正好切在三角形边长 **p99** ⇒ **误删 510 顶点** ⇒ 已改为半径规则 `cut = 0.85 × height`。**教训：「数据里没有」≠「图里看不到」。**
- **【第 4 个真 bug·未修】脚落点前移 ~6 cm**：管线 `t` 只锁头关节。**X 那一半已被 `--leg-splay` 顺手修掉**；**Y 那一半仍未动**。修法 = XY 锚点改成"脚底贴片"。
- **头/尺寸（已降级）**：源 `Head1` 是颅底、HD2 `head` 是颅顶；参照头部几何在 `head` **上** 0.368 m、我们 0.293 m ⇒ 我们小 ~20%（非阻塞）。
- **⚠️ 探针踩坑**：① 改网格的变换**作用于枢轴以上所有顶点**，只对"躯干权重顶点"应用会得到**假结果**；② **bind 恒等会掩盖真问题**；③ 分段窗口若按当前几何重算，参数改变会把顶点挪过窗口边界 ⇒ 数字乱跳/变号；④ **"沿某条轴的位置"要先问这条轴指向哪、还有谁会投影到它上面**。

**不变量（每次改完必跑 `probe_invariants.py`，全部不得回退）**
```
mesh down 0.0600 back 0.1000 | upper scale x1.120 pivot z 1.150 blend 0.45
our zmin -0.0612 height 2.0731 | ref zmin -0.0542 height 2.1127
idle vs bind 6.76e-16 m | shipped rig vs HD2 7.22e-16 m
edge mean 1.0733 p99 1.4368 max 2.93 bad>25% 2.29%
```

### Blender 观察工具（第 43 轮，全在 `tmp/rig-lab/`）
- 链：`dump_blender.py`（venv python）→ `out/blender/model.npz`+`meta.json`；`blender_build.py`（`blender.exe --background --factory-startup`）→ `zoey.blend`+`zoey.glb`；`blender_render.py`（Cycles CPU，**约 8 分钟**）。**每次重建前先备份到 `out/blender/before-<本轮>/`**。
- **骨轴**：两引擎骨都沿局部 **+X**，**Blender 沿 +Y** ⇒ `head=rest_hd2[:3,3]`、`tail=head+L·rest_hd2[:3,0]`、`align_roll(rest_hd2[:3,2])`。所得骨系与 HD2 差一个**所有骨共用的固定旋转 M** ⇒ **在 `A·inv(S)` 里抵消 ⇒ 变形与游戏等价**。**骨长不在数据里** ⇒ 用子骨 head 的投影推导 ⇒ head/轴向精确，tail 仅供参考。
- 集合 `OURS`（bind 蓝 + game 绿隐藏 + 骨架）/`SOURCE`/`REFERENCE`（只放实际对比的 5 个 unit）/`REFERENCE_alt`（隐藏，⚠️ **patch 同时含 Slim+Stocky 两套体型**）。骨 0–8 = 场景图/辅助节点（`boss` = 髋部根运动骨）+ 10 根无名骨。⚠️ Blender 把相对 `render.filepath` 解析到 **`C:\out\`** ⇒ 传绝对路径。

### 待办（网格变换；**用户要求一步一步来**）
1. ✅ 整体平移；**1.5 ✅ 完整性**；**2. ✅ 上半身放大**；**2.5 ✅ 区域兜底**；**4. ◐ 手臂**（45 轮 `arm_fit` → 45b `arm_up`+`arm_taper` → **45c `arm_rot` 角度**；**用户第 3 次验收中**）；3. **⏸ 接缝渐变缩放（用户 2026-10-12 拍板：暂缓）**；5. **头部比例**；6. **下半身放大**。
> **分工**：用户**空间感强、善于发现问题**但**对距离/数值不敏感**；我**视觉空间判断弱**但**数值/拟合可靠** ⇒ 用户"指出哪里不对"，我"用数值定出多少"。
- 后续：提取干净网格 → 按参照风格重刷权重 → 上 C#（`HD2SourceMigration`）→ 出离线 legacy 版 patch 包 → 进游戏实测（用户负责）。**动态物理骨骼仍要支持**（L4D2 风味 `jiggle_data.lua`）；**硬要求：没有 Lua 支持 legacy 包也必须能跑**；**不依赖 `HD2IRG1` 的动态骨骼**。
- ⚠️ **三个未决项**：① **nick 过冲**（唯一"本来超长"的模型，+0.189）；② **俯视图漂 2–4°**（要两全需**每段自己的旋转轴** = 3 个"轴+角"常量；用户若说俯视仍 OK 就不做）；③ 若腋窝缝再现，下一个嫌疑是 **A-pose 烘焙在肩部的旋转**（加 shoulder blend）。

## 坐标约定 & 参照 patch
- **HD2 坐标：+Y = 前，+Z = 上，+X = 角色的「右」**（左侧在 −X）。⚠️ 早期笔记"+X = 左"是**笔误**；实测 `l_foot` x = **−0.1908**。**`l_foot` 关节不在脚心**：HD2 原生踝在髋后 9.60 cm / 源 ≈ 同。
- 关键关节（`rest_hd2`，与参照 patch 逐位一致）：`hips` z **0.9735**；`spine2` 1.1633；`chest` 1.3437；`neck` **1.5500**；`head` 1.6908；`l_shoulder` x −0.2101 z 1.4745；`l_elbow` x −0.4723 z 1.3236；`l_hand` x −0.6566 z 1.2189。`l_shoulder→l_hand` 骨长 **0.5553 m**（沿轴 t：elbow 0.286 / hand 0.555 / index_finger3 0.680 / middle_finger3 0.697）。
- 参照 patch = `E:\mod\【4.28】Chiffon-LUNALICE替换RE-2310`（E 盘 = 用户**测试**库；D 盘 steam = **实际在用**）。容器 `9ba626afa44a3aa3.patch_0`(216 KB)+`.gpu_resources`(219 MB)，**无 manifest.json**；**35 条目**，**只 9 个 Unit 有几何**。实际对比只用 5 个：`cae35ee15168145f` 躯干上衣、`17da843a4908511e` Hip、`91eb7be84016ceab`/`7336c6b662522f0e` 左右臂、`af431352d9b95e83` **RE-2310 头盔 = 脸+猫耳+长发**(z 到 **2.059**；容器里**没有 unit 叫 helmet** ⇒ 不要从游戏本体取头盔)。加头后完整参照 **z[−0.054,+2.059]、总高 2.113 m**。
- **【关键判据】补丁骨架与 HD2 原生逐位一致**（13 根骨 delta 全 0），只多 `*_culling`/`*_twist` 辅助骨 ⇒ **"比例优化"全在网格层。** **读取三坑**：① **Unit 骨矩阵是行向量约定（平移 flat 12,13,14）**，与 `E.read_hd2_rig`（已转置）相反 —— 读错**不报错**；② bone 列表**前 9 项是场景图节点**，真骨从 `skeleton` 起，`boss` 是髋部根运动骨；③ **Unit 骨名是空的**，靠 32-bit hash 查 `bonehash`。

## 旧项目 / 新工具 / 其他（细节全在 `docs/`）
- **旧项目编译不过（预先存在）**，最后提交 **2026-09-03**；**用户已拍板：在 WpfApp1 内重写，不修旧项目。** 方案：`HD2SourceModel`（net8，**零引用**）✅ + `HD2SourceMigration`（**唯一同时引两侧**）✅骨架 + `HD2ModCli` 薄子命令 + 各自 `*.Tests`。HD2 侧复用 `UnitMeshReader.ReadTransformInfoFromUnitToc` + `UnitBoneNamesReader`；`HD2ModAdaptation/.../Canonical/` 有 **31 个 `Canonical*` 编译器**；矩阵统一 `System.Numerics.Matrix4x4`（float、行向量）。**Source 格式权威在 `HD2SourceModel/*.cs` 注释。** ⚠️ **实测三次证伪规范**：① VPK 条目步长与版本号无关；② MDL 骨步长 216 B 对 v44/48/49/53 全成立；③ bodypart/model 名是 UTF-8。
- **大功能 A（已被 B 取代）**：引擎 = Stingray(Bitsquid)，裁掉全部物理命名空间；`Unit`/`Mesh` **零 `create_*` / 零顶点权重接口** ⇒ **纯运行时 Lua 改不了权重、加不了骨、造不出物理**。**改内存边界：改「数据」✅ / 改「代码」❌**。**【硬约束】只允许依赖游戏本体，不得依赖任何外部系统（尤其其他 mod）**。
- **信息中心（阶段 0–6 已完成）**：`ModInformationFactRegistry` = **25 kind** 单一真相，`RequestAsync<T>` = 唯一读取入口，`Validate()` 在 `CreateModInformationCenter` 开头跑（新增 kind 忘登记 `SourceRoots` = **启动期失败**）。铁律：**缓存不得取代真实性**；**「读不到」≠「没有覆盖」**。
- **护甲映射页 20261011 回归**（`docs/armor-registry-page-20261011-regression.md`）：兜底已落实（`8d06fcb`/`5b79acc`），**根因未动**（拿「升级必被摧毁的凭据」`deployment` 守护「升级必被保留的文件」`protocol`）；崩溃三条件：①点过应用 ②库无 `workspace.json` ③`deployment` 不存在。**回归 #2（破坏性，未动）**：`ModLibraryResidualCleanupService.Cleanup(...)` 会 `Directory.Delete(recursive: true)` 删库根"未被快照引用且空"的子目录。
