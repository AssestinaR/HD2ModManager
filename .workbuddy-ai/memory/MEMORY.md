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
- **【环境】** ① Python 用隔离 venv `C:\Users\Administrator\.workbuddy-ai\binaries\python\envs\default\Scripts\python.exe`（有 numpy+Pillow；**系统 python 无 PIL**）；② **`git clone`/`curl` 打 GitHub 常失败** → 让用户下 zip；③ **Bash heredoc 调 Python/PowerShell 被安全策略拦** → 写成 `tmp/*.py` 再执行；④ **`Edit` 对含大量反引号/emoji 的长中文行会匹配失败** → 用 Python `str.replace` 并回读校验；⑤ **Agent shell 里 `Remove-Item` 被 safe-delete 钩子接管 → "删到回收站"必抛错**；让用户自己跑；**不要**用 `[System.IO.File]::Delete` 绕过。

## 大功能 B（当前方向）：L4D2 模型整体移植到 HD2
> 细节见 `docs/l4d2-to-hd2-physbone-migration.md`、`docs/l4d2-hd2-boundary.md`、**`docs/hd2-batchtool-sdk-findings.md`（§7 BatchTool 源码逐函数核对；§8 新管线；§9 参照 patch 反查出的三个真 bug）**；数字全在 `.workbuddy-ai/memory/*.md`。

### ✅ 新管线已实现并实测（`tmp/rig-lab/hd2_style.py`，**取代旧 `T = H·S⁻¹`**）
- **数学 = BatchTool 那套**：① **一个均匀缩放 + 一个平移**（围绕脚底缩放 ⇒ 源脚底精确落 HD2 脚底；**`TorsoScale=1.042` 是算出来的**）；② **HD2 骨的 head := 源映射骨的世界位置**（**逐骨只改位置，朝向与骨长仍是 HD2 自己的**）；③ **一张 30 骨常量局部旋转表**（A↔T，全是手臂+手；脊柱/颈/头/腿不参与）+ 20 条右侧骨轴 delta。**吸附发生在绑定之前 ⇒ 绑定态恒等、静止网格不变形** ⇒ 无身高膨胀/躯干对穿/长脖子。公式：`rot[b] = rot[p] @ inv(rest_hd2[p]) @ rest_hd2[b] @ D[b]`，`D[b] = D[p] · M_rest[b] · basis[b] · inv(M_rest[b])`。**HD2 独有骨的确定性补法**：`spine1=lerp(hips,spine2,.50)`、`chest=spine2`、`{side}_shoulder_twist=lerp(shoulder,elbow,.42)`、`{side}_hand_twist=wrist`、`{side}_shoulderarmour=lerp(clavicle,shoulder,.90)`、`{side}_toe=ball+(ball−ankle)*.35`、`head_tip=head+(head−neck)`。
- **官方流程（BatchTool `README.md`）**：导 HD2 骨架转 T-pose → 人物摆 T-pose 对齐（**腿部无需对齐**）→ 选字典（`Spine→spine1`、`Chest→spine2`、`Upper Chest→chest`）→ 骨骼吸附 → 顶点组重命名+重绑 → **必须**点"转回初态+原始骨轴" → 持握骨 → 部位切分。**⇒ 最终 rest = HD2 的 A-pose（网格被烘焙成 A-pose）。**
- 5 模型（zoey/rochelle/coach/nick/ellis）**全部 edge_mean 0.9989、bad>25% ≤0.58%**，**零逐模型调参**（旧 swing 最优 bad 9.25%）。实现要点：① **缩放高度必须在 HD2 空间沿 +Z 量**，脚底点用**底部 1 cm 顶点片质心**；② **AXIS delta 与 head 搬移必须交错在同一父先子后循环**（否则"被搬走父骨"的**未映射子骨**会掉队）。
- **模型自身瑕疵（非我们引入）+ 源骨架实测**：Nick/Ellis 的 `source_extra_*` 长袖 jiggle 骨链在 **bind pose 就朝 +Y 伸出 1.78 m / 1.04 m**（**源 T-pose 里就有**）⇒ "必须靠额外骨/物理驱动"的直接证据。源骨架：`Spine1≡Spine`、`Spine2≡Spine4` 且**零主导权重** ⇒ 有效躯干关节只 3 个；`Pelvis` 关节 0.8193 但网格质心 1.0820（**根骨不在髋**）；**T-pose 手臂严格水平**、**腿严格竖直**；`Head1` 1.5069 = **颅底**（非颅顶）。

### 🔴 三个真 bug（2026-10-12 用参照 patch 反查；详见 findings §9）
- **【方法论·最重要】bind 恒等会掩盖真问题。** `D` 在绑定态恒等 ⇒ **绑定态网格完全不变形** ⇒ 任何绑定态指标都漂亮，**哪怕对应表是错的**。游戏里做的是 `A[b]·inv(S[b])`；取 `A` = HD2 原生 rest 就是**玩家实际看到的那一帧**（`retarget` 的 `out_idle` = `rest_hd2 · inv(D·rest_sym) · out`）。**只有这个渲染能与参照 patch 直接对比。**
- **① `--align kabsch` 把身体转歪 2.8°** ⇒ 脖子跑到髋**前** 1.75 cm（HD2 是**后** 2.22 cm）＝ 用户看到的"身体前倾"。根因：两侧 20 关节集**不全等**（源 `Pelvis` 在髋下 12 cm、HD2 `hips` 就在髋上）。已加 `--align yaw`（误差 3.97→1.37 cm）。**② `Spine→spine1` 让躯干每帧拉伸 26.5 cm**（HD2 `spine1` 与 `hips` **完全重合** 0.9735）⇒ **`--spine-map mesh` 是必须的。** **③ 鞋被绑到膝盖 ⇒ 穿地 13.5 cm**（源鞋父级 `Bip01_L_Calf`）⇒ 已加 **`--fallback distance`**（`out_idle` 最低 z **−0.138 → +0.024**）。
- **⚠️ 头/尺寸：真有问题，单纯改缩放修不好**：源 `Head1`(颅底) 上还有 **28.8 cm** 头几何（含帽子）；HD2 `head`(颅顶) 上只有 2.4 cm ⇒ **语义差**。自动缩放 ⇒ 总高 **1.99 m**（参照 1.77 / HD2 原生 ~1.70）。试 `--scale 0.9236 --head-offset -0.2417`：绑定 1.715 ✓ 但**游戏帧仍 1.9175** ✗ ⇒ **必须改"头骨 ↔ 头几何"对应关系**。**未决。**

### 🔴 第 34 轮（2026-10-12）：撤回"正确姿态" + 参照 patch 做法查清（详见 findings §10）
- **用户否决 `out_idle` 发布方案**（它把手臂拉成"诡异扭曲肢体"—— LBS 下把 HD2 rest 直接套到我们网格 ⇒ 臂/肩骨与网格不匹配 ⇒ 撕裂）；**我已看图确认属实** ⇒ **撤回**。**第二列（变体 A-pose = 绑定姿态 `out`）才是需求**：已解决身体前倾，缺点 = 腿缺 HD2/参照那种"中轴前倾"。
- **`--leg-tilt <deg>` 已实现且已修好**：绕踝 `0.5*(l_foot+r_foot)` 转 thigh/knee +θ；其他骨整体平移 `delta = Rl(hips)−hips`（**躯干竖直不旋转**）；foot/ball/toe **钉住**；**网格同步混合变形**。⚠️ **首版是坏的**（腿倾段用 `src_index_to_hd2` 查骨名 ⇒ 鞋骨不在表里 ⇒ 鞋跟着躯干平移，抵消了髋前移）⇒ 抽成 **`hd2_style.resolve_bone_map(...)`** 让腿倾段与蒙皮段共用同一份答案。**修好后 tilt ≈ 5.6° 精确复现 HD2/参照腿部站姿**（踝在髋后 9.86 cm；髋落脚长 28.1%→86.9% @0–10°）。
- **【第四列的真面目 · 已坐实】** ① **RE-2310 头盔 = `0xaf431352d9b95e83`**（标签写着 `Torso_Undergarment_Slim_lod3`，但几何就是"脸+猫耳+长发"；容器里**没有任何 unit 叫 helmet**）⇒ **不要从游戏本体取头盔**。加上它后完整参照 **z[−0.054,+2.059]、总高 2.113 m**。② **更正上一轮的错误比较**：参照含头 **2.113 m > 我们 1.987 m**，我们其实**矮 12.6 cm**（上轮拿"参照去掉头的 1.77 m"比错了）。③ **参照骨架与 HD2 原生逐位一致（13 根骨 delta 全 0.000000）⇒ 作者一根骨都没动，全部"比例优化"在网格层。** ④ 做法 = **网格围着骨架做**：同一个 `head` 关节，参照的头部几何在其上 **0.3678 m**（HD2 只有 0.024 m）、我们 **0.2931 m** ⇒ **我们已在做同一件事，只是头部比参照小 ~20%**；袖口超出手关节 8.9 cm、裙摆 0.756×0.759 m 同理。**⇒ 头/尺寸那条未决项降级为"可选微调"，不是阻塞项。**
- **【新增·第 4 个真 bug】脚落点整体前移 ~6 cm**：管线 `t` **只锁头关节**（文档却写 "locking the visible foot sole"；实际只有 Z 成立）⇒ 脚底贴片质心 我们 `y +0.0427` / 参照 `−0.0198`（**前移 6.25 cm**），`x −0.1138` / 参照 `−0.1576`（**内收 4.4 cm**）。参照作者是**把脚对齐到 HD2 的脚**的。**修法 = XY 锚点从"头关节"改成"脚底贴片"（Z 仍锁头关节）。未改，待用户拍板。**
- **暂缓项**：用 Lua 把 HD2 骨架比例（臂长/肩宽）模拟到接近 L4D2 —— **先了解，暂且不做**。

### 🔵 坐标约定 & 参照 patch
- **HD2 坐标：+Y = 前，+X = 左，+Z = 上**（判据：补丁脚部网格左脚在 −X、脚趾朝 +Y，脚长 0.264 m）。**`l_foot` 关节不在脚心**：HD2 原生 踝在髋后 9.86 cm / 源 9.75 cm ⇒ **本来就一致**。
- 参照 patch = `E:\mod\【4.28】Chiffon-LUNALICE替换RE-2310`（E 盘 = 用户**测试**库，可随便操作；D 盘 steam = **实际在用**）。容器 `9ba626afa44a3aa3.patch_0`(216 KB)+`.gpu_resources`(219 MB)+`.stream`(0 B)，**无 manifest.json**；**35 条目**（23 Unit+2 Bone+10 未知），**只 9 个 Unit 有几何**；**渲染选 `cae35ee15168145f,17da843a4908511e,91eb7be84016ceab,7336c6b662522f0e` + 头 `af431352d9b95e83`**。
- **【关键判据】补丁骨架与 HD2 原生逐位一致**，只多出 `*_culling`/`*_twist` 辅助骨 ⇒ **"作者比例独立优化"完全在网格层面，骨骼照抄 HD2。** **读取三坑**：① **Unit 骨矩阵是行向量约定（平移 flat 12,13,14）**，与 `E.read_hd2_rig`（已转置）相反 —— 读错**不报错**；② bone 列表**前 9 项是场景图节点**，真骨从 `skeleton` 起，`boss` 是髋部根运动骨；③ **Unit 骨名是空的**，靠 32-bit hash 查 `bonehash`。

### 旧项目 / 新工具（WpfApp1 内重写，进行中）
- **旧项目编译不过（预先存在，引用已被删除的类型）**，最后提交 **2026-09-03**。**用户已拍板：在 WpfApp1 内重写，不修旧项目。** 方案：**`HD2SourceModel`**（net8.0，**零引用**，编译期强制不认识 HD2）**✅** + **`HD2SourceMigration`**（**唯一同时引两侧**）**✅骨架**（待换成新数学）+ `HD2ModCli` 薄子命令（`source inspect` ✅）+ 各自 `*.Tests`。**HD2 侧复用 `UnitMeshReader.ReadTransformInfoFromUnitToc` + `UnitBoneNamesReader`**；`HD2ModAdaptation/.../Canonical/` 已有 31 个 `Canonical*` 编译器 = "给 Unit 加骨 + 重写蒙皮"能力现成。矩阵统一 `System.Numerics.Matrix4x4`（float、行向量）。**Source 格式细节权威在 `HD2SourceModel/*.cs` 注释。** ⚠️ **实测三次证伪规范**（合成样本测不出，只有真实文件能证伪）：① **VPK 条目步长与版本号无关**（97/97 = "版本号 1 + 18 B 条目"）；② **MDL 骨步长 216 B 对 v44/48/49/53 全成立**；③ **bodypart/model 名是 UTF-8**（如 `裙Dress_Skirt`）。

## 其他（细节全在 `docs/`）
- **大功能 A（已被 B 取代）**：引擎 = Stingray(Bitsquid)，**裁掉全部物理命名空间**；`Unit`/`Mesh` **零 `create_*` / 零顶点权重接口** ⇒ **纯运行时 Lua 改不了权重、加不了骨、造不出物理**。**改内存边界：改「数据」✅ / 改「代码」❌**。**【硬约束】只允许依赖游戏本体，不得依赖任何外部系统（尤其其他 mod）**；`HD2IAS` 只作反面参照物。
- **信息中心（阶段 0–6 已完成）**：`ModInformationFactRegistry` = **25 kind** 单一真相，`RequestAsync<T>` = 唯一读取入口，`Validate()` 在 `CreateModInformationCenter` 开头跑（新增 kind 忘登记 `SourceRoots` = **启动期失败**）。铁律：**缓存不得取代真实性**（键全由来源指纹构成、不落盘、双向测试）；**「读不到」≠「没有覆盖」**；**图标不进事实**。护甲页预热：**单飞加载**、闸门 `displayFactsReady`、**不占 UI 线程**。
- **护甲映射页 20261011 回归**（`docs/armor-registry-page-20261011-regression.md`，切点 `30a4f83`）：兜底已落实（`8d06fcb`/`5b79acc`），**根因未动**——**拿「升级必被摧毁的凭据」(`deployment`) 守护「升级必被保留的文件」(`protocol`) ⇒ 设计上不可能可靠**；崩溃三条件：①点过应用 ②库无 `workspace.json` ③`deployment` 不存在；方向 = 不 gate + 把缺失做成可见可操作状态。**回归 #2（破坏性，未动）**：`ModLibraryResidualCleanupService.Cleanup(...)` 会 `Directory.Delete(recursive: true)` 删库根"未被快照引用且空"的子目录，零迁移保护、不走回收站。

