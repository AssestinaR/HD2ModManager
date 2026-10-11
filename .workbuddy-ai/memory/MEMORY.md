# 项目长期记忆：HD2ModManager

> 只留**结论、硬约束、易忘的坑**。细节在 `docs/*.md`（**被 .gitignore，改前先备份到 `docs/_backup/`**）与 `.workbuddy-ai/memory/YYYY-MM-DD.md`。

## 项目定位
- 仓库 `WpfApp1`：《绝地潜兵 2》Mod 管理器与补丁适配工具集，GPLv3。`ManagedMain` 已过时。
- 生产工程：`HD2ModManager`(WPF/net10.0-windows)、`HD2ModCli`(net10.0，`hd2mod`)、`HD2ModCore`(net8.0)、`HD2ModAdaptation`(net8.0)、`Decryptor`、`HD2GameItemAssets` + `*.Tests`；依赖链 `Cli → Core → {Adaptation, GameItemAssets, Decryptor}`。
- `HD2RuntimeArmorOverride` = 护甲运行时映射的 Lua mod 源码（非 csproj），lua 被 `HD2ModCore.csproj` 内嵌为 EmbeddedResource。
- `CoreServices` 是**唯一装配点**：判"某服务是否被生产使用"看 `CoreServices.CreateXxx(` 调用点是否为 0。

## 工作约定（硬约束）
- **【强制】只做本地提交，不推送远程**（用户手动推送）。**授权范围内的改动直接做，不反复请示。**
- **用户不是专业程序员**：涉及"要不要删/要做什么"，先用通俗语言说明作用与后果，再给建议。
- **临时脚本一律 `tmp/`（已 gitignore），不要写进 `_diagnostics/`** —— `DiagnosticsPatchSampleTests` 是条件测试，`_diagnostics` 一旦存在（哪怕空的）就会真跑并失败。**`tmp/rig-lab/` = 本功能的离线「斧」。**
- **【环境】Agent shell 里 `Remove-Item` 被 safe-delete 钩子接管 → 本机"删到回收站"必抛错**，任何删除脚本都会失败（受害者 `publish.ps1`）；让用户在自己 PowerShell 窗口跑；**不要**用 `[System.IO.File]::Delete` 绕过。
- **写完"应能抓住某 bug"的用例后，主动破坏被保护的那一行再跑**；仍通过 = 用例是假的。删除类改动**必须同时改造其测试**。
- **`tools/check_dead_code.py` 是词匹配**（注释里写类名会误判可达）→ **必须两方向一起看**（`comm -13` + `comm -23`）。
- `Directory.Build.props`：`ImplicitUsings`/`Nullable`/`Deterministic` 开、**`TreatWarningsAsErrors=true`**（排除 `NU1901..1904`）。TargetFramework/OutputType/引用**刻意不在此文件**。
- 耗时：全解决方案 `dotnet test` **6–7 分钟**；`dotnet build -t:Rebuild` **20 秒**。**报测试计数要分项+增量，报数前先跑一遍。**
- 发布：**轻量标签**；溯源靠包内 `cliVersion`（`1.0.0+<git 全哈希>`）；校验产物**别用 PowerShell `Add-Type`**（被拦），用 Python `zipfile`。
- **【数据归属·硬约束】用户自定义数据一律存**模组库**；设置页简单设置存 `settings.json`；**能从游戏 data 重算的纯缓存**才放管理器目录。目标：**换管理器 = 无痛继承模组库；迁移模组库 = 只改库地址即可。** ⚠️ 2026-10-11 护甲页崩溃的根因就是违反了这条（归属凭据写在**安装级** `data\`，却用它守护**用户级**协议）。**新增持久化文件前先问：属于库、属于设置、还是纯缓存？**
- `git commit` 提交**整个索引**，与 `git add` 传了哪些路径无关。
- **【环境】** ① Python 用隔离 venv `C:\Users\Administrator\.workbuddy-ai\binaries\python\envs\default\Scripts\python.exe`（有 numpy+Pillow；**系统 python 无 PIL**）；② **`git clone` 到 GitHub 常失败**（`schannel`/`502`）→ 改用 `curl` 打 GitHub API 取文件树 + `raw.githubusercontent.com` 取单文件，或让用户下 zip（见 `tmp/pb-ref/HD2-BatchTool-main/`）；③ **`Edit` 对含大量反引号/emoji 的长中文行会匹配失败** → 写记忆长行优先用 Python `str.replace` 并回读校验。

## 护甲映射页 20261011 回归（兜底已落实，根因未动）
> 详见 `docs/armor-registry-page-20261011-regression.md`（切点 `30a4f83`）。
- **一个根因、两种症状，判据 = 工作区有没有 `map_model`**：**有** → 启动即崩 `KeyNotFoundException`（`ApplySnapshot` 抛 → `OnLoaded` 的 `async void` 无 try/catch；`fingerprint` 未赋值 ⇒ **不重启无法恢复**）。**无** → 优雅显示 `未知护甲 {id}`（`Choices` 缺 `ItemId` ← SQLite `game_data_authority_kits`，**文件不存在返回空数组**）。**判别：日志 `Unhandled UI exception` 括号里的数字 = 缺失 `ItemId`。**
- **已落实 ✅** `8d06fcb`（`ResolveChoice` + `LoadCatalogOnceAsync` catch/finally + 韧性测试）、`5b79acc`（归属标记写在**协议旁边**、读取回退链、只读展示；**刻意不收紧归属判断**）。**根因形状（§13）**：护甲数据住**三处、生命周期不同**（协议 `%LocalAppData%` 跟用户 / `settings.json`+`data\` 跟安装 / `workspace.json`+`applied.json` 跟库）⇒ **拿「升级必被摧毁的凭据」守护「升级必被保留的文件」⇒ 设计上不可能可靠。** 崩溃三条件：①点过应用 ②库无 `workspace.json` ③`deployment` 不存在。⚠️ **`applied.json` 不能**当归属凭据。**方向 = 不 gate（永不隐藏）+ 把缺失做成可见可操作状态。** 未做 P1：`RefreshAsync` 读到 0 条时不该置 `hasLoadedCatalog=true`；`ModLibraryFolder` 也是安装级的 ⇒ 探测失败会**静默把模组库换成空目录**。**回归 #2（破坏性）**：`ModLibraryResidualCleanupService.Cleanup(...)` 对库根直接子目录若「未被快照引用」且「空/全是元数据」→ `Directory.Delete(recursive: true)`，零迁移保护。

## 大功能 B（当前方向）：L4D2 模型整体移植到 HD2
> 详见 `docs/l4d2-to-hd2-physbone-migration.md`、`docs/l4d2-hd2-boundary.md`、**`docs/hd2-batchtool-sdk-findings.md`（Estecsky 三仓库调研，含 `HD2IRG1` 格式全表）**。
- **转向原因**："给老 HD2 mod 批量刷权重加物理"太难 → 改为把 L4D2 动漫角色**整体移植**。旧项目自述失败 = "T-pose 摆 A-pose 太难"、"骨骼尺寸差距大"、**每次改动必须进游戏实测**。
- **决定性发现**：源模型自带完整 jigglebone 参数。扫 **97 VPK / 6566 MDL** → **147 个带 jigglebone**（0 解析失败）。Zoey 主模型：**127 骨 / 57 jiggle**。**骨名读不到**（`sznameindex` 指向内嵌 SMD 文本）**但父级索引有效** → 靠"层级 + bind pose 位置"重建。仿真方向依赖骨的 **+Z 轴**。
- **两侧世界 bind 同用一个公式** `world = inverse(bind)`（Source = `inverse(poseToBone)`@+96；HD2 = `inverse(inverse_bind)`）。⚠️ **不要靠 pos/quat 沿父链累加**（忽略 `posscale`/`rotscale`/`qAlignment`，静默塌陷）。
- **关键未知**：X1 表能长多大 → **部分已答：运行时上限 256**；X2 追加骨后 HD2 原动画是否仍正确驱动前 90 骨；X3 Lua 每帧驱动 57 骨×每玩家的成本；X4 无骨名的额外骨如何稳定标识；X5 Source `is_flexible` 弹簧能否用二阶积分等价复现；X6 `set_local_rotation` 的局部/世界语义；X7 目标 Unit。
- **三层分工**：① 身体骨（~20 ValveBiped）离线映射到 HD2 原生骨 → 运行时由 HD2 动画驱动（免费）；② 额外骨**原样追加在骨架末尾**（不改原有索引）→ 运行时由 Lua 按 Source 参数驱动；③ 顶点：身体区重映射到身体骨，头发/裙摆区指向额外骨。
- **【用户已拍板】** ① 接受不转译 L4D2 动画；② 额外骨驱动 = Lua 复现 Source 风格参数，**硬约束：维持游戏本体物理的低消耗，绝不引入 HD2IAS 类卡顿**；③ 第一阶段先做 A1~A3；④ 旧项目负责 **VPK 转化**，WpfApp1 提供 **Lua 插件动骨支持**，**倾向分开**（怕耦合屎山）；⑤ 路线 = **Python 快循环调参 → 出离线 legacy patch 包 → 用户进游戏测 → 确认后上 C#**。
- **架构边界**：**"缝"必须落在「版本化文件格式」上，不是目录上。** 判据：改一边时另一边要不要跟着改？**`HD2IRG1` 就是现成的那个缝**（schema+version+crc32+上限+纯旋转校验齐全）。三角色：① 转换器（唯一懂 VPK/ValveBiped/jigglebone）② 管理器（只装/校验/展示）③ Lua 运行时（只读声明）。⚠️ **不得依赖他们的 Lua producer 存在**——我们要自带读 `HD2IRG1` 的 Lua 实现。
- **性能反向设计**：只驱动声明里列的额外骨／纯 Lua 二阶积分无原生调用／**每 Unit 一份独立状态**。
- **"斧"（离线回路，零游戏启动即可量化）**：`tmp/rig-lab/`（`rig_map.py`/`export_obj.py`/`a4_matrix.py`/`probe_chain.py`/`probe_weights.py`/`compare.py`）+ `tmp/physbone-recon/`。

### A1~A4 已完成 ✅（数字全部实测；⚠️ 但 §7 之后要重估：**这个问题本可以不出现**）
> 全部数字见 `docs/l4d2-to-hd2-physbone-migration.md` §13~§17 + `tmp/rig-lab/a4-report.json`。
- **A1** 从游戏本体推出 HD2 骨架（avatar Unit TocData **21504 B**，transform **107** 条，**最大误差 0.262 mm**；`Matrices[i]` = row-major、平移在 `m[12..14]`）。**A2** 逐骨 `T_i = H_σ(i)·S_i^-1`，对应表 **50 对**（20 身体骨 + 30 手指）、**0 缺失**。⚠️ **两侧矩阵存储约定不同，必须转置**（Source = row-major **列向量**、平移 flat **3,7,11**；HD2 = row-major **行向量**、平移 flat **12,13,14**）—— 不转置 → 段长全算成 **0.0000**。
- **A3** 整张网格 LBS（Zoey **121704 顶点 / 159363 三角形**；索引公式 `global = mdlModel.VertexOffset + mdlMesh.VertexStart + orig`）。**决定性发现**：**两引擎都把骨骼放在局部 +X 轴上**；`T=H·S⁻¹` **不是错，是「太严格」** —— HD2 躯干/头链 **roll** 与 Source 差 ~90°（腿 2.8° ✅ / 躯干·颈·头 70.8° ❌）⇒ **rest-pose 重定向只取 swing**；**根骨 `Pelvis→hips` 只平移不旋转**。
- **A4（5 模型 × 2 roll 模式）**：对应表**完全通用**；空间变换是真 **180° 绕 Z 旋转**（det 恒 +1.0000 ⇒ **不是镜像**）；**swing 优于 frame**（edge max 49.2 vs 125.9）；**唯一病态 = Nick**（回退跳数 7、dev max 3.36 m）；**身高膨胀 11.6~14.2%**。**方法论**：`|dst−src|` 是**假诊断**（主要成分就是那 180° 旋转）→ 用旋转不变量或先拟合掉全局刚体；**全局量必须从点云拟合**，不能取单根骨的帧。

### 🔴 2026-10-11 关节/权重实测 + 已实现的三条修正（`probe_chain.py`/`probe_weights.py`/`export_obj.py`）
- **关节（Nick）**：源 Pelvis 0.819 → HD2 hips 0.9735；源 Spine 1.099 → HD2 **spine1 = 0.9735（与 hips 同点，退化）**；源 Spine2 **= Spine4 = 1.219**（退化）；源 Head1 1.507 → head **1.691（+0.184 ⇒ 长脖子）**；源 `L_UpperArm→l_shoulder` 段长比 **3.195（肩部压扁）**。
- **源两脚 x=±0.0767（0.153 m）vs HD2 ±0.1908（0.382 m）⇒ HD2 叉腿「稍息」vs 源直腿并拢。**
- **权重（dominant 顶点数 / 网格质心 z）**：Pelvis 14116/1.082（关节才 0.819）；Spine 3910/**1.166**；**Spine2 0**；Spine4 12254/**1.341**；Neck1 1580/1.466；Head1 22305/**1.634**；**L/R_Foot 0**；**鞋子 = `source_extra_62/63`（各 7182 顶点，质心 ±0.076,−0.102,0.057）回退到 `L/R_Calf`**。⇒ **源网格质心 ≈ HD2 关节**（Spine 1.166≈spine2 1.163；Spine4 1.341≈chest 1.344）；`Spine2`/`L_Foot`/`R_Foot` 零权重 ⇒ 真正决定外观的是**回退**。
- **① `--spine-map mesh`**：`Spine→spine2`（不是 `spine1`；后者与 hips 同点 ⇒ **躯干对穿**）。Nick edge max **49.21→27.07**、bad>25% **9.25%→7.77%**。
- **② `--transfer-bones legs`**：`L/R_Calf`+`L/R_Foot` 改 transfer（保留源相对父骨偏移）⇒ 鞋子不再外推。height 2.072→**2.036**、p95 1.3987→**1.2761**、bad>25%→**7.60%**。⚠️ **大腿不要 transfer**（会抬高 0.15 m，高度爆到 3.03 m）。
- **③ `--anchor neck`**：`Neck1`/`Head1` 让源**网格质心**落到 HD2 关节（而非源**关节**）。Zoey 身高膨胀 **+9.7%→+2.8%**、Nick **+11.5%→+4.9%**。⚠️ `neck+spine` **更差** ⇒ **只锚 neck**。
- **踩坑**：`origin(M)` 是**矩阵平移列**，不是「骨映射后落在哪」；要的是 `xform(M, 源骨原点)`。用错会让 transfer 比例失真 3.4 倍。

### 🔴🔴 2026-10-11 深夜：**BatchTool 那套数学读通了 —— 我们一直在解一个更难的问题**
> 详见 `docs/hd2-batchtool-sdk-findings.md` **§7**。源码在 `tmp/pb-ref/HD2-BatchTool-main/`（用户手动下载的 zip）。
- **按钮↔函数**：`转为烘焙 T-pose` = `convert_pose_space(TO_TPOSE)`；`转回初态+原始骨轴` = `convert_pose_space(TO_INITIAL)`（`ui/panels.py:62-65`、`ui/operators.py:203-252`、`utils/rigging.py:613-673`）。**吸附 = `snap_independent_to_hd2`（`workflows/mmd_mapping.py:859-959`）—— 之前完全没读。**
- **【决定性】BatchTool 里根本没有 `T = H·S⁻¹`。** 全部数学 = ①**一个均匀缩放+一个平移**（`scale = desired_height/source_height`，锁可见脚底[优先 `l_ball`]、对头顶、−0.015 m ⇒ **`TorsoScale=1.042` 是算出来的，不是魔法常量**）；②**HD2 骨的 head := 源映射骨的世界位置**（逐骨**只有位置**，朝向仍用 HD2 自己的 `tail−head`；手/指例外，从模板恢复绝对静止轴 + `align_roll`）；③**一张 30 骨常量局部旋转表**（A↔T）+ 20 条右侧骨轴 delta。
- **为什么它更稳**：吸附发生在**绑定之前** ⇒ **绑定态恒等，静止网格不变形** ⇒ 无身高膨胀/躯干对穿/长脖子。我们做 `T=H·S⁻¹` 时把两侧 **roll 分歧烘进了网格**，才需要"只用 swing"止损。**`l_foot` head := 源脚踝 ⇒ 腿自动保持源自己的直腿站姿**（「腿部无需对齐」的机制）；`FEMALE` 只是运行时再加一层修正。
- **`body_pose_profile.json` 实测**：`body_bones` 列 45 根但**只有 30 根有旋转值**（全是手臂+手；`chest` 在列表里但无值 ⇒ **脊柱/颈/头/腿完全不参与 A↔T**）；`l_shoulder` 34.31°、`l_elbow` 39.71°；**两张表精确共轭**（`tpose×initial` 残差 0.0000°）⇒ 往返严格可逆；`symmetric_axis_deltas_wxyz` 20 条**全右侧** 14.29~64.42° = **骨轴(roll)镜像差，与性别无关**。
- **HD2 独有骨的确定性补法**：`spine1=lerp(hips,spine2,.5)`、`chest=spine2`、`shoulder_twist=lerp(sh,el,.42)`、`hand_twist=wrist`、`shoulderarmour=lerp(clav,sh,.90)`、`toe=ball+(ball−ankle)*.35`、`head_tip=head+(head−neck)`。
- **旧项目可跑**：`HD2AgentWorkbench/src/hd2agent/bin/**Debug**/net8.0/hd2agent.exe`（Sep 8，与源码同步，支持 `--skinning-mode canonical --pose-migration source-rig-fit`）；**Release 版（Aug 31）过旧，不认识这些参数**。游戏 data = `D:/SteamLibrary/steamapps/common/Helldivers 2/data`。⚠️ 其 `source-rig-fit` **照样叉腿**（诊断 `l_foot=[−0.1908,…]`、`sourceScale=0.0264668=0.0254×1.042`、`retainedHelpers=67`）。
- **待用户拍板**：**改用 BatchTool 这套（均匀缩放 + 吸附骨 head + 常量 A-pose 表），还是继续修我们的逐骨 `T`。**

### 旧项目现状 / 新工具（WpfApp1 内重写，进行中）
- **旧项目绝对路径已修 ✅**，但**编译不过（预先存在）**：5 个错误全是引用 WpfApp1 里**已被删除**的类型。最后提交 **2026-09-03**。**用户已拍板：在 WpfApp1 内重写，不修旧项目。**
- 方案：**两个新工程** —— `HD2SourceModel`（net8.0，**零引用**，纯 Source 读取层，编译期强制"不认识 HD2"）**✅已落地** + `HD2SourceMigration`（net8.0，**唯一同时引两侧**）**✅骨架已落地**（50 对对应表 + 逐骨 `T=H·S⁻¹`）+ `HD2ModCli` 薄子命令（`source inspect` ✅）+ 各自 `*.Tests`。**HD2 侧复用 `UnitMeshReader.ReadTransformInfoFromUnitToc` + `UnitBoneNamesReader`**。
- 仓库此前**无** VPK/MDL/VVD/VTX/ValveBiped 读取代码（新能力）。`HD2ModAdaptation/PatchReconstruction/UnitMesh/Canonical/` 已有 31 个 `Canonical*` 编译器 = "给 Unit 加骨 + 重写蒙皮"能力现成。矩阵统一 `System.Numerics.Matrix4x4`（float、行向量）。
- **Source 格式细节权威在 `HD2SourceModel/*.cs` 注释**。回归基准 `Lunalice_Zoey.vpk` = **127 骨（62 根 `ValveBiped.*`，65 根 → `source_extra_N`）/ 57 jiggle / 32 bodypart / 121704 顶点 / 159363 三角形**。**5 个模型分处不同 VPK**（`Lunalice_Zoey/Rochelle`、`Elise_Coach`、`Lilium_Sancta_Nick`、`Serenade_Ellis`，均在 `D:/SteamLibrary/steamapps/common/Left 4 Dead 2/left4dead2/addons/`）。
- ⚠️ **实测三次证伪规范**（合成样本测不出，只有真实文件能证伪）：① **VPK 条目步长与版本号无关**（97/97 = "版本号 1 + 18 B 条目"）⇒ 两种都试，判据 = 正好吃掉整棵树；② **MDL 骨步长 216 B 对 v44/48/49/53 全成立**（184 几乎全失败）；③ **bodypart/model 名是 UTF-8**（如 `裙Dress_Skirt`）。

## 背景功能（细节全在 `docs/`）
- **【硬约束】只允许依赖游戏本体，不得依赖任何外部系统（尤其其他 mod）。** `HD2IAS` 只作反面参照物。
- **大功能 A（已被 B 取代）**：引擎 = Stingray(Bitsquid)，**裁掉全部物理命名空间**；`Unit`/`Mesh` **零 `create_*` / 零顶点权重接口** ⇒ **纯运行时 Lua 改不了权重、加不了骨、造不出物理**。**改内存边界：改「数据」✅ / 改「代码」❌**。
- **信息中心（阶段 0–6 已完成）**：`ModInformationFactRegistry` = **25 kind** 单一真相，`RequestAsync<T>` = 唯一读取入口，`Validate()` 在 `CreateModInformationCenter` 开头跑（新增 kind 忘登记 `SourceRoots` = **启动期失败**）。铁律：**缓存不得取代真实性**（键全由来源指纹构成、不落盘、双向测试）；**「读不到」≠「没有覆盖」**；**图标不进事实**；缩略图磁盘键必须含内容。护甲页预热：**单飞加载**、闸门 `displayFactsReady`、**不占 UI 线程**。
