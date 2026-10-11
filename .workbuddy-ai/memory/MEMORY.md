# 项目长期记忆：HD2ModManager

> 只留**结论、硬约束、易忘的坑**。细节在 `docs/*.md`（**被 .gitignore，改前先备份到 `docs/_backup/`**）与 `.workbuddy-ai/memory/YYYY-MM-DD.md`（历史全文在 `_backup/`）。

## 项目定位
- 仓库 `WpfApp1`：《绝地潜兵 2》Mod 管理器与补丁适配工具集，GPLv3。`ManagedMain` 已过时。
- 生产工程：`HD2ModManager`(WPF/net10.0-windows)、`HD2ModCli`(net10.0，`hd2mod`)、`HD2ModCore`(net8.0)、`HD2ModAdaptation`(net8.0)、`Decryptor`、`HD2GameItemAssets` + `*.Tests`；依赖链 `Cli → Core → {Adaptation, GameItemAssets, Decryptor}`。
- `HD2RuntimeArmorOverride` = 护甲运行时映射的 Lua mod 源码（非 csproj），lua 被 `HD2ModCore.csproj` 内嵌为 EmbeddedResource。
- `CoreServices` 是**唯一装配点**：判"某服务是否被生产使用"看 `CoreServices.CreateXxx(` 调用点是否为 0。

## 工作约定（硬约束）
- **【强制】只做本地提交，不推送远程**（用户手动推送）。**授权范围内的改动直接做，不反复请示。**
- **用户不是专业程序员**：涉及"要不要删/要做什么"，先用通俗语言说明作用与后果，再给建议。
- **临时脚本一律 `tmp/`（已 gitignore），不要写进 `_diagnostics/`** —— `DiagnosticsPatchSampleTests` 是条件测试，`_diagnostics` 一旦存在（哪怕空的）就会真跑并失败。**`tmp/rig-lab/` = 本功能的离线「斧」。**
- **写完"应能抓住某 bug"的用例后，主动破坏被保护的那一行再跑**；仍通过 = 用例是假的。删除类改动**必须同时改造其测试**。
- **`tools/check_dead_code.py` 是词匹配**（注释里写类名会误判可达）→ **必须两方向一起看**（`comm -13` + `comm -23`）。
- `Directory.Build.props`：`ImplicitUsings`/`Nullable`/`Deterministic` 开、**`TreatWarningsAsErrors=true`**（排除 `NU1901..1904`）。TargetFramework/OutputType/引用**刻意不在此文件**。
- 耗时：全解决方案 `dotnet test` **6–7 分钟**；`dotnet build -t:Rebuild` **20 秒**。**报测试计数要分项+增量；基线会变，报数前先跑一遍。**
- 发布：**轻量标签**；溯源靠包内 `cliVersion`（`1.0.0+<git 全哈希>`）；校验产物**别用 PowerShell `Add-Type`**（被拦），用 Python `zipfile`。
- **【数据归属·硬约束】用户自定义数据一律存**模组库**；设置页简单设置存 `settings.json`；**能从游戏 data 重算的纯缓存**才放管理器目录。目标：**换管理器 = 无痛继承模组库；迁移模组库 = 只改库地址即可。** ⚠️ 2026-10-11 护甲页崩溃的根因就是违反了这条。**新增持久化文件前先问：属于库、属于设置、还是纯缓存？**
- `git commit` 提交**整个索引**，与 `git add` 传了哪些路径无关。
- **【环境】** ① Python 用隔离 venv `C:\Users\Administrator\.workbuddy-ai\binaries\python\envs\default\Scripts\python.exe`（有 numpy+Pillow；**系统 python 无 PIL**）；② **`git clone`/`curl` 打 GitHub 常失败** → 让用户下 zip；③ **Bash heredoc 里调 Python/PowerShell 被安全策略拦** → 写成 `tmp/*.py` 再执行；④ **`Edit` 对含大量反引号/emoji 的长中文行会匹配失败** → 用 Python `str.replace` 并回读校验；⑤ **Agent shell 里 `Remove-Item` 被 safe-delete 钩子接管 → 本机"删到回收站"必抛错**（受害者 `publish.ps1`）；让用户自己跑；**不要**用 `[System.IO.File]::Delete` 绕过。

## 护甲映射页 20261011 回归（兜底已落实，根因未动）
> 详见 `docs/armor-registry-page-20261011-regression.md`（切点 `30a4f83`）。
- **一个根因、两种症状，判据 = 工作区有没有 `map_model`**：**有** → 启动即崩 `KeyNotFoundException`（`ApplySnapshot` 抛 → `OnLoaded` 的 `async void` 无 try/catch；`fingerprint` 未赋值 ⇒ **不重启无法恢复**）。**无** → 优雅显示 `未知护甲 {id}`。**判别：日志 `Unhandled UI exception` 括号里的数字 = 缺失 `ItemId`。**
- **已落实 ✅** `8d06fcb`（`ResolveChoice` 兜底 + `LoadCatalogOnceAsync` catch/finally 复位单飞账本 + 韧性测试）、`5b79acc`（归属标记写在**协议旁边**、读取回退链、只读展示；**刻意不收紧归属判断**）。
- **根因形状**：护甲数据住**三处、生命周期不同**（协议 `%LocalAppData%` 跟用户 / `settings.json`+`data\` 跟安装 / `workspace.json`+`applied.json` 跟库）⇒ **拿「升级必被摧毁的凭据」守护「升级必被保留的文件」⇒ 设计上不可能可靠。** 崩溃三条件：①点过应用 ②库无 `workspace.json` ③`deployment` 不存在。**方向 = 不 gate（永不隐藏）+ 把缺失做成可见可操作状态。**
- **未做 P1**：`RefreshAsync` 读到 0 条时不该无条件置 `hasLoadedCatalog=true`；`ModLibraryFolder` 也是安装级的 ⇒ 游戏目录探测失败会**静默把模组库换成空目录**。**回归 #2（破坏性，未动）**：`ModLibraryResidualCleanupService.Cleanup(...)` 对库根直接子目录若「未被快照引用」且「空/全是元数据」→ `Directory.Delete(recursive: true)`，零迁移保护、不走回收站。

## 大功能 B（当前方向）：L4D2 模型整体移植到 HD2
> 详见 `docs/l4d2-to-hd2-physbone-migration.md`、`docs/l4d2-hd2-boundary.md`、**`docs/hd2-batchtool-sdk-findings.md`（§7 = BatchTool 源码逐函数核对；§8 = 新管线；§9 = 参照 patch 反查出的三个真 bug）**。

### ✅ 新管线已实现并实测（`tmp/rig-lab/hd2_style.py`）
- **数学 = BatchTool 那套（已实测，取代 `T = H·S⁻¹`）**：① **一个均匀缩放 + 一个平移**（围绕脚底缩放，两处 z 相消 ⇒ 源脚底精确落 HD2 脚底；**`TorsoScale=1.042` 是算出来的**）；② **HD2 骨的 head := 源映射骨的世界位置**（**逐骨只改位置，朝向与骨长仍是 HD2 自己的**）；③ **一张 30 骨常量局部旋转表**（A↔T，全是手臂+手；脊柱/颈/头/腿不参与）+ 20 条右侧骨轴 delta。**吸附发生在绑定之前 ⇒ 绑定态恒等、静止网格不变形** ⇒ 无身高膨胀/躯干对穿/长脖子（`T=H·S⁻¹` 把两侧 roll 分歧烘进网格，才需要"只用 swing"止损）。
- **公式**：`rot[b] = rot[p] @ inv(rest_hd2[p]) @ rest_hd2[b] @ D[b]`；`D[b] = D[p] · M_rest[b] · basis[b] · inv(M_rest[b])`。**HD2 独有骨的确定性补法**：`spine1=lerp(hips,spine2,.50)`、`chest=spine2`、`{side}_shoulder_twist=lerp(shoulder,elbow,.42)`、`{side}_hand_twist=wrist`、`{side}_shoulderarmour=lerp(clavicle,shoulder,.90)`、`{side}_toe=ball+(ball−ankle)*.35`、`head_tip=head+(head−neck)`。
- **官方流程**（BatchTool `README.md`）：导入 HD2 骨架并转 T-pose（FEMALE=启用双腿运动调整）→ 人物也摆 T-pose 对齐（**腿部无需对齐**）→ 选字典（`[source,target]`；`Spine→spine1`、`Chest→spine2`、`Upper Chest→chest`）→ 可选头部偏移+统一缩放 → 检查→骨骼吸附→顶点组重命名+重绑→ **必须**点"转回初态+原始骨轴" → 持握骨 → 部位切分。**⇒ 最终 rest = HD2 的 A-pose（网格被烘焙成 A-pose）。**
- 5 模型（zoey/rochelle/coach/nick/ellis ↔ `survivor_teenangst/producer/coach/gambler/mechanic`）**全部 edge_mean 0.9989、edge_max 1.29~2.87、bad>25% 0.14~0.58%**，**零逐模型调参**（旧 swing 最优：Zoey edge_max 49.21、bad 9.25%）。
- 实现要点：① **缩放高度必须在 HD2 空间沿 +Z 量**，脚底点用**底部 1 cm 顶点片质心**；② **AXIS delta 与 head 搬移必须交错在同一父先子后循环**（否则"被搬走父骨"的**未映射子骨**会掉队）。
- **已知模型自身瑕疵（非我们引入）**：Nick 的 `source_extra_97..106` 是**长袖 jiggle 骨链**，bind pose 袖子朝 +Y 伸出 **1.78 m**；Ellis 也有 1.04 m 的。**源 T-pose 里就有** ⇒ "必须靠物理/额外骨驱动"的直接证据。`drop_outlier_tris` 用**边长**判据剔不掉。
- **源骨架实测**：Zoey `Spine1`≡`Spine`（1.0986）、`Spine2`≡`Spine4`（1.2191）、`Spine1/Spine2` **零主导权重** ⇒ 有效躯干关节只 3 个；`Pelvis` 关节 0.8193 但网格质心 1.0820（**根骨不在髋**）；`L_UpperArm/Forearm/Hand` z 全等 1.4121（**T-pose 手臂严格水平**）；`L_Thigh/L_Calf/L_Foot` y 全等 −0.0369（**腿严格竖直**）；`Neck1` 1.4531、`Head1` 1.5069（**颅底**）。网格 z −0.0621..1.7948、y −0.2853..0.3454。
### 🔴 三个真 bug（2026-10-12 用参照 patch 反查出来；详见 findings §9）
- **【方法论·最重要】bind 恒等会掩盖真问题。** `D` 在绑定态恒等 ⇒ **绑定态网格完全不变形** ⇒ 任何绑定态指标（边长比/包围盒）都漂亮，**哪怕对应表是错的**。游戏里做的是 `A[b]·inv(S[b])`；取 `A` = HD2 原生 rest 就是**玩家实际看到的那一帧**。已在 `retarget` 加 **`out_idle`**（`rest_hd2 · inv(D·rest_sym) · out`）。**只有这个渲染能与参照 patch 直接对比。**
- **① `--align kabsch` 把身体转歪 2.8°** ⇒ 脖子跑到髋**前** 1.75 cm（HD2 是**后** 2.22 cm）＝ 用户看到的"身体前倾"。根因：两侧 20 关节集**不全等**（源 `Pelvis` 在髋下 12 cm、HD2 `hips` 就在髋上）。已加 `yaw_rotation()`（1-DOF，XY 投影 2D SVD）+ `--align yaw`（脖子→髋后 3.59 cm，**误差 3.97→1.37 cm**）。
- **② `Spine→spine1` 让躯干每帧拉伸 26.5 cm**（HD2 的 `spine1` 与 `hips` **完全重合**，都是 0.9735；源的 `Spine` 在骨盆上 27.9 cm）⇒ **`--spine-map mesh` 是必须的，不是可选调优**。
- **③ 鞋被绑到膝盖 ⇒ 穿地 13.5 cm**：源鞋 `source_extra_62/63` 的 MDL 父级是 `Bip01_L_Calf` ⇒ 父链回退绑到 `l_knee`（比 HD2 高 13.45 cm）。已加 **`--fallback distance`**（绑到关节位置最近的已映射骨）⇒ `out_idle` 最低 z **−0.138 → +0.024**。
- **✅ ④ "身体前倾"= 绑定姿态假象（2026-10-12 第 34 轮判定）**：游戏算的是 `A[b]·inv(R[b])·v`，站着不动时 `A` = HD2 自己的静止骨架 ⇒ **游戏显示的是"用 HD2 站姿重摆过"的那一帧**，而上一轮截图画的是**绑定姿态** `out`。把游戏那一帧（`out_idle`）渲染出来**是直的**，与参照 patch 站姿接近。**根因：管线少了 BatchTool 的最后一步「转回初态+原始骨轴」**——它把骨架还原成 HD2 自己的 rest、把"head 搬到源关节位置"的形变**烘焙进网格**。⇒ **发布应为 `rest = rest_hd2`（HD2 原生逐位）+ `mesh = out_idle`（方案 B，= BatchTool / 参照 patch 的做法）**。已数值验证（15 骨随机旋转的极端姿势）：与现状**平均差 1.9 mm / p99 2.1 cm / 最大 7.4 cm**，静止帧完全一致（不等来自 LBS 3 骨混合，单骨严格相等）⇒ **零风险**。判据表：髋落在脚长的 **绑定 28.1% / 游戏帧 61.5% / 参照 48.5%**（用户说的"对准脚跟"= 28.1% ⇒ 反证他看的是绑定姿态）。
- **⚠️ 头/尺寸：真的有问题，且单纯改缩放修不好**：源 `Head1`(颅底) z 1.5069，头几何在其上 **28.8 cm**（含帽子）；HD2 `head`(颅顶) 1.6908，几何只在其上 2.4 cm ⇒ **语义差**。自动缩放把颅底对到颅顶 ⇒ 总高 **1.99 m**（参照 1.77 / HD2 原生 ~1.70），网格顶比 HD2 head 高 **30.8 cm**。试过 `--scale 0.9236 --head-offset -0.2417`（按头顶对齐）：绑定 1.715 m ✓ 但**游戏帧仍 1.9175 m** ✗ ⇒ **必须改"头骨 ↔ 头几何"对应关系**，不是改缩放。**未决。**
- **⚠️ 一个刚体旋转修不好两件事**：源 = 腿**竖直** + 躯干前倾 3.3 cm；HD2 = 腿**后倾 9.86 cm** + 躯干后倾 2.22 cm。`yaw` 修躯干但腿变竖直；`kabsch` 保住腿后倾但躯干歪。**⇒ 采方案 B 后此问题自动消解**（发布 rest = HD2 rest，绑定姿态即 HD2 站姿）。

### 🔵 HD2 坐标约定（实测坐实）
- **+Y = 前，+X = 左，+Z = 上**。判据：补丁脚部网格 x∈[−0.212,−0.102]（左脚在 −X）、y∈[−0.145,+0.119]（脚长 0.264 m），踝 `l_foot` y=−0.1175 只离脚跟端 2.7 cm ⇒ 脚趾朝 +Y。
- **`l_foot` 关节不在脚心**：HD2 原生 hips −0.019 / ankle −0.1175 ⇒ 踝在髋后 **9.86 cm**；源 −0.0547 / −0.1522 ⇒ **9.75 cm**。**两者本来就一致。**

### 参照 patch（用户提供，`E:\mod\【4.28】Chiffon-LUNALICE替换RE-2310`；解析细节见 findings §9）
- E 盘 = 用户**测试** mod 库（可随便操作）；D 盘 steam 目录 = 用户**实际在用**的库。
- 容器 `9ba626afa44a3aa3.patch_0`(216 KB) + `.gpu_resources`(219 MB) + `.stream`(0 B)，**无 manifest.json**；**35 条目**（23 Unit + 2 Bone + 10 未知），**只有 9 个 Unit 有几何**。**渲染选 `cae35ee15168145f,17da843a4908511e,91eb7be84016ceab,7336c6b662522f0e`。**
- **【关键判据】补丁骨架与 HD2 原生逐位一致**，**只多出** `*_culling` / `*_twist` 辅助骨。⇒ **"作者比例独立优化"完全在网格层面，骨骼照抄 HD2。**
- **读取三坑**：① **Unit 骨矩阵是行向量约定（平移 flat 12,13,14）**，与 `E.read_hd2_rig`（已转置）相反 —— 读错**不报错**；② bone 列表**前 9 项是场景图节点**，真骨从 `skeleton` 起，`boss` 是髋部根运动骨；③ **Unit 骨名是空的**，靠 32-bit hash 查 `bonehash`。

### 旧项目 / 新工具（WpfApp1 内重写，进行中）
- **旧项目编译不过（预先存在，引用已被删除的类型）**，最后提交 **2026-09-03**。**用户已拍板：在 WpfApp1 内重写，不修旧项目。**
- 方案：**`HD2SourceModel`**（net8.0，**零引用**，编译期强制不认识 HD2）**✅** + **`HD2SourceMigration`**（**唯一同时引两侧**）**✅骨架**（待换成新数学）+ `HD2ModCli` 薄子命令（`source inspect` ✅）+ 各自 `*.Tests`。**HD2 侧复用 `UnitMeshReader.ReadTransformInfoFromUnitToc` + `UnitBoneNamesReader`**。`HD2ModAdaptation/PatchReconstruction/UnitMesh/Canonical/` 已有 31 个 `Canonical*` 编译器 = "给 Unit 加骨 + 重写蒙皮"能力现成。矩阵统一 `System.Numerics.Matrix4x4`（float、行向量）。
- **Source 格式细节权威在 `HD2SourceModel/*.cs` 注释**。⚠️ **实测三次证伪规范**（合成样本测不出，只有真实文件能证伪）：① **VPK 条目步长与版本号无关**（97/97 = "版本号 1 + 18 B 条目"）⇒ 两种都试，判据 = 正好吃掉整棵树；② **MDL 骨步长 216 B 对 v44/48/49/53 全成立**；③ **bodypart/model 名是 UTF-8**（如 `裙Dress_Skirt`）。

## 背景功能（细节全在 `docs/`）
- **【硬约束】只允许依赖游戏本体，不得依赖任何外部系统（尤其其他 mod）。** `HD2IAS` 只作反面参照物。
- **大功能 A（已被 B 取代）**：引擎 = Stingray(Bitsquid)，**裁掉全部物理命名空间**；`Unit`/`Mesh` **零 `create_*` / 零顶点权重接口** ⇒ **纯运行时 Lua 改不了权重、加不了骨、造不出物理**。**改内存边界：改「数据」✅ / 改「代码」❌**。
- **信息中心（阶段 0–6 已完成）**：`ModInformationFactRegistry` = **25 kind** 单一真相，`RequestAsync<T>` = 唯一读取入口，`Validate()` 在 `CreateModInformationCenter` 开头跑（新增 kind 忘登记 `SourceRoots` = **启动期失败**）。铁律：**缓存不得取代真实性**（键全由来源指纹构成、不落盘、双向测试）；**「读不到」≠「没有覆盖」**；**图标不进事实**。护甲页预热：**单飞加载**、闸门 `displayFactsReady`、**不占 UI 线程**。
