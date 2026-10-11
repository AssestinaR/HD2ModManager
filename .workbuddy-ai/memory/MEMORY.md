# 项目长期记忆：HD2ModManager

> 细节在 `docs/*.md`（**被 .gitignore，改前先备份到 `docs/_backup/`**）与 `.workbuddy-ai/memory/YYYY-MM-DD.md`；本文件只留**结论、硬约束、易忘的坑**。

## 项目定位
- 仓库 `WpfApp1`：《绝地潜兵 2》Mod 管理器与补丁适配工具集，GPLv3。`ManagedMain` 已过时。
- 主目标 = hd2mod 系列：`HD2ModManager`(WPF)、`HD2ModCli`(`hd2mod.exe`)、`HD2ModCore`、`HD2ModAdaptation`、`Decryptor`、`HD2GameItemAssets` 及各 `*.Tests`。
- `HD2RuntimeArmorOverride` = 护甲运行时映射的 Lua mod 源码（非 csproj），lua 被 `HD2ModCore.csproj` 内嵌为 EmbeddedResource。

## 工作约定（必须遵守）
- **【强制】只做本地提交，不推送远程**（推送由用户手动执行）。**授权范围内的改动直接做，不反复请示。**
- **用户不是专业程序员**：涉及"要不要删/要做什么"，先用通俗语言说明作用与后果，再给建议。
- **临时脚本一律 `tmp/`（已 gitignore），不要写进 `_diagnostics/`** —— `DiagnosticsPatchSampleTests` 是条件测试，`_diagnostics` 一旦存在（哪怕空的）就会真跑并失败（880 绿 → 878/2 失败）。
- **【环境】Agent shell 里 `Remove-Item` 被 safe-delete 钩子接管，本机"删到回收站"必抛错**（两后端都在成功删除后报错）→ Agent shell 里任何删除脚本都会失败（典型受害者 `publish.ps1`）；让用户在自己 PowerShell 窗口跑；**不要**用 `[System.IO.File]::Delete` 绕过。
- **写完"应能抓住某 bug"的用例后，主动破坏被保护的那一行再跑**；仍通过 = 用例是假的（实测 `ApplyExecutor` 只支持硬链接 → "只改源"用例假通过）。也见过**两处守卫各自冗余**。删除类改动**必须同时改造其测试**。
- `CoreServices` 是唯一装配点：判"某服务是否被生产使用"看 `CoreServices.CreateXxx(` 调用点是否为 0。`git commit` 提交**整个索引**，与 `git add` 传了哪些路径无关。
- **`tools/check_dead_code.py` 是词匹配**：注释里写类名会把死代码误标为可达（40→33）。**必须两方向一起看**（`comm -13` + `comm -23`）。
- `TreatWarningsAsErrors` 已开启，但 `<WarningsNotAsErrors>NU1901..1904</...>` 排除 NuGet 审计警告（用户："本应用不担心被攻击"）。**只降级不静默**。反直觉：**NuGet 只对 net9.0+ 目标审计传递包**。
- 耗时：全解决方案 `dotnet test` 约 6–7 分钟；`dotnet build -t:Rebuild` 约 20 秒。**报测试计数要分项 + 增量；基线会变，报数前先跑一遍。**
- 发布：**轻量标签**；溯源靠包内 `cliVersion`（`1.0.0+<git 全哈希>`）；校验产物**别用 PowerShell `Add-Type`**（被拦），用 Python `zipfile`。`HD2ModCli/skills/` 有 28 个 `SKILL.md`，导出的是 **27** 个（`mod-management` 是 `IsLegacy:true`，27 是设计值）。

## 护甲映射页 20261011 回归（只分析，未改代码）
> 详见 `docs/armor-registry-page-20261011-regression.md`。20260928 切点 `30a4f83`，区间 43 提交。
- **"护甲页卡"确已修好**：整包构建 GameData 浏览器 → 只请求 `ArmorCoverage`（960ms → 230ms）。
- **【已实测证实】一个根因、两种症状，判据 = 工作区有没有 `map_model`**：
  - **有** → **启动即未处理异常崩溃** `KeyNotFoundException`（`ApplySnapshot` **:519** ← `RefreshAsync` :403 ← `LoadCatalogOnceAsync` :351 ← `ArmorRegistryPageView.OnLoaded` :31 的 `async void` 无 try/catch → 逃到全局）。本机 293 条；用户实测复现。**再开一次再炸**（`:519` 抛错 → `:556` 的 `fingerprint` 未赋值 → `RefreshRuntimeStatusAsync` **:451** 又 `ApplySnapshot`）→ **不重启无法恢复**。
  - **无**（全 `map=`）→ 优雅地全显示 `未知护甲 {id}`（用户截图 B）。
  - 机制：`ApplySnapshot` 首循环 `if (mapping.SourceItemId is not { } sourceItemId) continue;` **跳过模型映射**（`SourceItemId` 恒 null）→ `TargetItemId` **永不补 `Unknown`**；次循环却用**直接索引器** `Choices[mapping.TargetItemId]`（**:519**）。
  - **缺陷本区间未改（旧版同样会炸）**，本次改的是"什么时候炸"：护甲页**视图**现在启动时就创建（新增 `PrewarmArmorPageHost` + 视觉预热三元组）→ 用户没做任何事就崩；旧版要手动点开才崩。
- `未知护甲 {id}` 唯一来源 = `Choices` 缺该 `ItemId`；`Choices` ← `cachedCatalog` ← `ReadArmorCatalogAsync()` ← SQLite `game_data_authority_kits`（**文件不存在直接返回空数组**）。相关文件本区间**逐字节未变** ⇒ 只改"什么时候读"。`data/` 随 `BaseDirectory` 走 → **新版解压到新目录**是"目录读到 0 条"最可能的现场成因。`RefreshAsync` 不检查目录条数（`:402` 无条件置 `hasLoadedCatalog=true`），**无复位路径**。
- **回归 #2（破坏性）**：`ModLibraryResidualCleanupService.Cleanup(...)` 对库根直接子目录若"未被快照引用"且"空/全是元数据" → **`Directory.Delete(recursive: true)`**，**零迁移保护、不走回收站**。= 截图 A `模型包部署失败，尚未编译护甲注册。`（`:1523`）头号嫌疑。
- **仍未解决**：现场"目录为什么是空的"。候选 A（目录整体 0 条）与 **候选 B（目录正常但某 `map_model` 的 target 不在索引里 —— 不需目录为空也能同样崩溃，更易解释"只有部分用户"）**。**最快判别：日志 `Unhandled UI exception` 括号里的数字 = 缺失 `ItemId`，拿它去用户索引查一次即可定性。**
- **P0 修复（与根因无关，必须修）**：`ApplySnapshot` 不许对 `Choices` 用直接索引器（改 `TryGetValue` + `Unknown` 兜底）；`OnLoaded` 的 `async void` 必须包 try/catch。
- **"忘做旧版兼容/迁移"不成立**：相关文件本区间一行未改，`schema=2` 早在 `399439e`(09-25) 就进了 20260928 发布。本机 `runtime.status` Oct 2 / `revision=90` → 最近两次改动**从未下发**。**两处隐患**：① `RefreshRuntimeStatusAsync` **检查** `IsBusy` 但**从不设置** → 可与 `RefreshAsync` 并发；② `settings.json` 有两个，locale 从"从不被写"的那个读 → 恒为默认（既有差异）。

## 大功能 B（当前方向）：L4D2 模型整体移植到 HD2
> 详见 `docs/l4d2-to-hd2-physbone-migration.md`、`docs/l4d2-hd2-boundary.md`。**纯调研 + 离线验证，未改生产代码。** 由**另一 agent 线程**负责，勿与护甲页回归混谈。

**转向原因**：判断"给老 HD2 mod 批量刷权重加物理"太难 → 改为把 L4D2 动漫角色**整体移植**。旧项目 `HD2AgentWorkbench` 自述失败 = "T-pose 摆成 A-pose 太难"、"两游戏骨骼尺寸差距大"、**每次改动必须进游戏实测**无法高速迭代。

### 源模型自带完整 jigglebone 参数（决定性发现）
- 扫 **97 VPK / 6566 MDL** → **147 个带 jigglebone**（0 解析失败）。`Lunalice_Zoey.vpk` 主模型 `survivor_teenangst.mdl`：**127 骨 / 57 jiggle bone**，父级 `Head1` 15（头发）、`Pelvis` 4（裙摆）、`Spine` 6、`Spine4` 4、左右小腿各 2。
- **这些骨的骨名读不到**（`sznameindex` 指向内嵌 SMD 文本）**但父级索引有效** → 靠"层级 + bind pose 位置"重建。
- Source `$jigglebone`；MDL 侧 `STUDIO_PROC_JIGGLE=5`（判据 `proctype`）；**`procindex` 是相对该骨自身地址的字节偏移** → `mstudiojigglebone_t`（120 字节）。`mstudiobone_t` v53 = **216 字节**；`flags`(160)/`proctype`(164)/`procindex`(168)。仿真方向依赖骨的 **+Z 轴**。

### 离线数学：问题已量化，答案确实存在
- **两侧世界 bind 同用一个公式**：`world = inverse(bind)`（Source = `inverse(poseToBone)`@+96，row-major/**列向量**；HD2 = `inverse(inverse_bind)`）。⚠️ **不要靠 pos/quat 沿父链累加**（忽略 `posscale`/`rotscale`/`qAlignment`，静默塌陷）—— 已踩过。
- **真实肢体长度比只有 0.868..1.135**；`clavicle->shoulder` 3.195 与 `neck->head` 2.615 是**"关节语义不同"而非尺寸差距**。**单一均匀缩放必然失败**（最优 scale=0.99，**最大残差 0.2515 m**）→ **解 = 逐骨相似变换，父→子一遍解完**。旧项目 HANDOFF §17–§19 的 `--pose-migration source-rig-fit`(v6) 已做到（静置回放误差 **5.7e-4 m**）⇒ **卡住的不是数学，是"改一次必须进一次游戏"**。
- **关键未知（生死线，不得当事实）**：X1 **两张表分别能长到多大？** transform **107→164**、蒙皮调色板 **90→147**（不接受则方案作废）；X2 追加骨后 HD2 原有动画是否仍正确驱动前 90 骨；X3 Lua 每帧驱动 57 骨 × 每玩家的成本；X4 无骨名的额外骨如何稳定标识；X5 Source `is_flexible` 弹簧能否用二阶积分等价复现；X6 `set_local_rotation` 的局部/世界语义与 Source 的 +Z 约定是否一致；X7 目标 Unit（RE-2310 Slim Torso 已知可用）。
- **三层分工（待验证）**：① 身体骨（~20 ValveBiped）离线映射到 HD2 原生 90 骨 → 运行时由 HD2 动画驱动（免费）；② 额外骨（头发/裙摆）**原样追加在骨架末尾**（不改原有索引）→ 运行时由 Lua 按 Source 参数驱动；③ 顶点：身体区域重映射到身体骨，头发/裙摆区域直接指向额外骨（索引随骨平移，**不需重映射权重**）。**"运行时骨骼转译"不存在**。
- **"斧"（离线回路，零游戏启动即可量化"差多少、差在哪"）**：`tmp/physbone-recon/`（`skeleton_probe.py`/`plot_skeleton.py`/`rig_gap_report.py`）+ `tmp/rig-lab/`（`avatar_rig_probe.py`/`rig_map.py`/`export_obj.py`/`a4_matrix.py`）。
- **【用户已拍板】** ① 接受不转译 L4D2 动画；② 额外骨驱动 = Lua 复现 Source 风格参数，**硬约束：维持游戏本体物理的低消耗，绝不引入 HD2IAS 类卡顿**；③ **第一阶段先做 A1~A3**；④ 旧项目可改，多阶段目标 = 旧项目负责 **VPK 转化** ＋ WpfApp1 提供 **Lua 插件动骨支持**，**倾向分开**（怕耦合屎山）。

### 架构边界
- **同意分开，但"缝"必须落在「版本化文件格式」上，不是目录上。** 反例：旧项目 `HD2AssetKit.csproj` 用**绝对路径**引用 WpfApp1 → **两仓库已被焊死**。**隔离来自接口，不来自目录。** 唯一判据：改动一边时另一边要不要跟着改？
- **缝 = 一个版本化的「动骨声明文件」**（`schema` 必填；只有转换器写、运行时读、**永不回写**）。三角色：① 转换器（旧项目，唯一懂 VPK/ValveBiped/jigglebone）② 管理器（只装/校验/展示）③ Lua 运行时（只读声明）。Lua 运行时与护甲映射**同仓但独立成 mod**。
- **性能反向设计**（逐条对 HD2IAS 三个慢因）：只驱动声明里列的额外骨／纯 Lua 二阶积分无原生调用／**每 Unit 一份独立状态**。另加：静置休眠、LOD 剔除、每 Unit 每帧只调一次 `World.update_unit()`。

### A1 / A2 / A3 已完成 ✅（2026-10-11）
- **A1**：零生产代码改动，`hd2mod gamedata extract asset` 取玩家 avatar Unit TocData（**21504 字节**），按 `UnitMeshReader.ReadTransformInfoFromUnitToc` 同布局 Python 解析（字节布局见 docs）。transform **107** 条，名字解析 97/107，与 `hd2_skel.json` 可比 **64** 骨，**最大误差 0.262 mm** ✅。**`Matrices[i]` = row-major，平移在 `m[12..14]`** → **骨架来源从"外部常量"换成"游戏本体"**。顺带：① **骨架分两张表** transform **107** vs 蒙皮调色板 **90**（加 57 骨 = 107→164 / 90→147）；② 基础 avatar 本来就有一批专用骨（`cape1..8`、`attach_*`、`backpack`、`sling`、`support*`、`collision` 等）；③ **`hd2_skel.json` 不是基础 avatar**，是"某 mod 的视图"（39 个键我们没有）。
- **A2**：`tmp/rig-lab/rig_map.py`。`T_i = H_σ(i) · S_i^-1`（S 先 ×0.0254）。对应表 **50 对**（20 身体骨 + 30 手指），**0 缺失**；三指标全 PASS（外部校验 **0.262 mm**、段长误差 **0**、刚性 **3.9e-07**）。127 源骨 → 50 直接 / 77 回退。**网格拉伸数字**（HD2/Source 段长）：骨盆→胸 **0.9258**、胸→颈 **0.8678**、颈→头 2.6154、臂 **1.0352**、腿（髋→踝）**0.9092**。产出 `rig-map.json`。
- ⚠️ **两个陷阱**：① **两侧矩阵存储约定不同，必须转置**（Source = row-major **列向量**，平移 flat **3,7,11**；HD2 = row-major **行向量**，平移 flat **12,13,14**）—— 不转置 → 段长全算成 **0.0000**（A1 只用平移所以没暴露）；② **脚部落点改变腿拉伸比 10%**：`L_Foot→l_foot` = **0.909**、`→l_ball` = **1.006**。
- **A3**：`tmp/rig-lab/export_obj.py` VPK(MDL/VVD/VTX) → 逐顶点 LBS → OBJ + pillow 渲染。Zoey **121704 顶点 / 159363 三角形**。索引公式 `global = mdlModel.VertexOffset + mdlMesh.VertexIndexStart + orig`（漏掉 mesh 起点会**炸成尖刺**）。
- **A3 决定性发现**：**两个引擎都把骨骼放在局部 +X 轴上** → "沿骨方向"约定一致。**`T = H·S⁻¹` 不是错，是"太严格"**（假设两骨架局部坐标系逐骨对应），HD2 躯干/头链的 **roll（绕骨轴）**与 Source 差 ~90°：`roll = T旋转角 − swing` → **腿 2.8° ✅ / 躯干·颈·头平均 70.8° ❌**（`Spine4→chest` 88.72°、`Head1` 88.91°）。**rest-pose 重定向应只取 swing**（最短弧对齐骨轴 + 平移对齐原点）；**根骨 `Pelvis→hips` 只平移不旋转**（两 bone 语义不同，否则裙摆/helper 一起甩）。渲染验收：swing 行 = 完整站立角色；frame 行 = 裙子炸成伞、头发变尖刺。
- **交叉验证旧项目**：`TorsoHeadQuarterTurn = CreateRotationX(π/2)` 只对源骨 7/9/11/12（spine2/chest/neck/head）生效 —— 与 A3 的"躯干/头需 ~90° 绕骨轴修正"**完全吻合** ⇒ **独立复现旧项目为何必须写 `TorsoHeadQuarterTurn` 与 `RetainedRelativeHelper`**。

### A4 完成 ✅（2026-10-11）：5 个模型跑通，唯一病态是 Nick
> `tmp/rig-lab/a4_matrix.py` 一条命令跑 N 模型 × 2 模式（`export_obj.py` 加 `--json-out`/`--prefix`，裸 `main()` 收进 `__main__`）；venv 装了 **numpy 2.5.4**（Kabsch）。结果 `a4-report.json`。10 次全量 LBS ≈ 20 秒。
- **① 对应表完全通用**：5 模型（zoey/rochelle/coach/nick/ellis，骨数 127/127/125/124/125）**全部 50/50 命中、0 缺失** ⇒ `ValveBiped.Bip01_` 前缀在 L4D2 全幸存者通用。**rochelle 与 zoey 指标逐位相同**（`survivor_producer.mdl` 就是 `survivor_teenangst.mdl` 换名）。
- **② 空间变换是真 180° 旋转**：Kabsch 拟合 **175.3°~179.9° 绕 Z，det 恒 +1.0000** ⇒ **不是镜像、左右不互换**；与旧项目 `CreateScale(-1,-1,1)` **完全一致**（又一处独立复现）。
- **③ roll 误差是结构性的**：5 个模型 **torso roll 全 70.8°、腿全 2.8°** ⇒ 可写进转换器常量。
- **④ swing 数值上确实优于 frame**：判别力最强的是 **edge max**（zoey swing 49.2 vs frame 125.9）。**刚体旋转不改变边长** → "裙摆被转到错误方向"只有**极值**或**全局拟合残差**能看出来，边长均值看不出。
- **⑤ 唯一病态模型 = Nick**：最大回退跳数 **7**（其余 3~4），dev max **3.36 m**；**dev max 与最大回退跳数严格同向**（3→0.6~0.9 m、4→2.2 m、7→3.36 m）。最差区域**全是 `source_extra_*`**（骨名读不出的头发/裙摆/大袖子 helper）⇒ **回退只是临时兜底；"追加骨 + Lua 驱动"（三层分工②）正是解药**。
- **⑥ 身高膨胀 11.6%~14.2%**（最差 ellis 1.142），**5 模型全部 +11% 以上 ⇒ 系统性**，必须做成转换器固定修正项。
- **方法论（又踩两个坑）**：① `|dst−src|` 是**假诊断**（主要成分就是那 180° 空间旋转：手在 x≈±0.65，转 180° 就是 1.3 m）→ **要么用旋转不变量（边长比），要么先拟合掉全局刚体运动**；② **不要用单根骨的帧当全局映射**（`H_hips·S_pelvis⁻¹` 混进了 `Pelvis→hips` 的 90° 骨轴差，dev p50 虚高到 0.87 m）→ **全局量必须从点云拟合**。
- **下一批修正（性价比排序）**：① 回退→追加骨；② 显式指定躯干/头关节语义（消灭身高膨胀）；③ 脚部改 `l_ball` 落点（腿长比 0.909→1.006）；④ 30 根手指逐指判定；⑤ 纳入 `TorsoScale = 1.042`。

### 旧项目现状：绝对路径已修，但当前编译不过
- **绝对路径已修 ✅**：`HD2AssetKit.csproj` 改为按序探测三个常规位置 ＋ `-p:HD2ModAdaptationProject=`/`HD2_MOD_ADAPTATION_PROJECT` 覆盖 ＋ 失败时 `EnsureHD2ModAdaptationResolved` 抛带修复指引的错误（码 **HD2AW001**）。
- **编译不过（预先存在，非本轮引入）**：5 个错误全是引用 WpfApp1 里**已被删除**的类型（`...PatchReconstruction.Validation` 命名空间、`CanonicalDependencyClosure`、`CanonicalDependencyClosureRequest`、`CanonicalDependencyDiagnostic`、`CanonicalDependencyClosureResult`）—— **整个依赖闭包校验子系统被移除**。旧项目最后提交 **2026-09-03**。→ **架构结论的活证据**：两仓库被（a）绝对路径（b）对私有类型名的直接依赖**焊死**；改 WpfApp1 会**静默**打断旧项目。**待用户拍板**：修 5 处还是重写。**不影响 A1/A2/A3**（独立 Python）。
- **VPK 踩坑**：tree **没有 `num_extensions` 字段**（ext/dir/filename 三层各用空字符串终止）；entry 18 字节后还要跳 `preloadLength` 字节；内嵌数据偏移 = `headerSize + treeSize + entryOffset`。

## 背景功能（细节全在 `docs/`，这里只留还在生效的硬约束与结论）
- **【硬约束】只允许依赖游戏本体，不得依赖任何外部系统（尤其其他 mod）。** `HD2IAS` 只作反面参照物。
- **大功能 A（已被 B 取代）**：引擎 = **Autodesk Stingray（原 Bitsquid）**，**裁掉全部物理命名空间**；`Unit` 119 成员**零 `create_*`**、`Mesh` 24 成员**零顶点/权重接口** → **纯运行时 Lua 改不了权重、加不了骨、造不出物理**。尸体堆叠不卡 = **休眠刚体**；披风（Havok Cloth，全库 3 个）/ 布娃娃（`ragdoll_profile`，仅 `can_ragdoll`）**借不了**。**改内存边界：改「数据」✅ / 改「代码」❌**（绑 `game.dll` + PE 时间戳，游戏一更新全废）。官方入口：`set_animation_bone_mode('ignore')` / `set_bones_lod` / `AnimationBlenderComponent.crossfade`；**HD2 抖动骨 API 只能驱动状态机里已有的 6 根骨**。**产线已有"加骨+重写蒙皮"能力**（`CanonicalAppendBoneCompiler` 等 5 个）+ **CLI 直读游戏数据**（`hd2mod gamedata extract list|show|asset|archive`）。详见 `docs/hd2-armor-physics-research.md`。
- **信息中心（阶段 0–6 已完成）**：`ModInformationFactRegistry` = **25 kind** 单一真相，`RequestAsync<T>` = **唯一读取入口**，`Validate()` 在 `CreateModInformationCenter` 开头跑（新增 kind 忘登记 `SourceRoots` = **启动期失败**）。硬约束：**缓存不得取代真实性**（键全由来源指纹构成、不落盘、双向测试）。铁律：**"读不到" ≠ "没有覆盖"**；**图标不进事实**；缩略图**磁盘键必须含内容**（否则"同路径换图标"永不自愈）。护甲页预热三要求：**单飞加载**、**取数闸门 `displayFactsReady`**（`ShellViewModel` 的 `finally` 放行）、**不占 UI 线程**。内嵌 SQLite 定点 `SQLitePCLRaw.bundle_e_sqlite3 2.1.12`；`ApplyExecutor` **只支持硬链接与符号链接**。
