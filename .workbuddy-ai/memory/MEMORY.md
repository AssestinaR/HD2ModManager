# 项目长期记忆：HD2ModManager

> 本文件只留**结论、硬约束、易忘的坑**。细节在 `docs/*.md`（**被 .gitignore，改前先备份到 `docs/_backup/`**）与 `.workbuddy-ai/memory/YYYY-MM-DD.md`。

## 项目定位
- 仓库 `WpfApp1`：《绝地潜兵 2》Mod 管理器与补丁适配工具集，GPLv3。`ManagedMain` 已过时。
- 生产工程：`HD2ModManager`(WPF/net10.0-windows)、`HD2ModCli`(net10.0，`hd2mod`)、`HD2ModCore`(net8.0)、`HD2ModAdaptation`(net8.0)、`Decryptor`、`HD2GameItemAssets` + `*.Tests`；依赖链 `Cli → Core → {Adaptation, GameItemAssets, Decryptor}`。
- `HD2RuntimeArmorOverride` = 护甲运行时映射的 Lua mod 源码（非 csproj），lua 被 `HD2ModCore.csproj` 内嵌为 EmbeddedResource。
- `CoreServices` 是**唯一装配点**：判"某服务是否被生产使用"看 `CoreServices.CreateXxx(` 调用点是否为 0。`CliApplication.cs` 是 5565 行单体分发。

## 工作约定（硬约束）
- **【强制】只做本地提交，不推送远程**（用户手动推送）。**授权范围内的改动直接做，不反复请示。**
- **用户不是专业程序员**：涉及"要不要删/要做什么"，先用通俗语言说明作用与后果，再给建议。
- **临时脚本一律 `tmp/`（已 gitignore），不要写进 `_diagnostics/`** —— `DiagnosticsPatchSampleTests` 是条件测试，`_diagnostics` 一旦存在（哪怕空的）就会真跑并失败。
- **【环境】Agent shell 里 `Remove-Item` 被 safe-delete 钩子接管 → 本机"删到回收站"必抛错**，任何删除脚本都会失败（受害者 `publish.ps1`）；让用户在自己 PowerShell 窗口跑；**不要**用 `[System.IO.File]::Delete` 绕过。
- **写完"应能抓住某 bug"的用例后，主动破坏被保护的那一行再跑**；仍通过 = 用例是假的。删除类改动**必须同时改造其测试**。
- **`tools/check_dead_code.py` 是词匹配**（注释里写类名会误判可达）→ **必须两方向一起看**（`comm -13` + `comm -23`）。
- `Directory.Build.props`：`ImplicitUsings`/`Nullable`/`Deterministic` 开，**`TreatWarningsAsErrors=true`**（`WarningsNotAsErrors=NU1901..1904` 排除 NuGet 审计；NuGet 只对 net9.0+ 目标审计传递包）。TargetFramework/OutputType/引用**刻意不在此文件**。
- 耗时：全解决方案 `dotnet test` 约 6–7 分钟；`dotnet build -t:Rebuild` 约 20 秒。**报测试计数要分项 + 增量；基线会变，报数前先跑一遍。**
- 发布：**轻量标签**；溯源靠包内 `cliVersion`（`1.0.0+<git 全哈希>`）；校验产物**别用 PowerShell `Add-Type`**（被拦），用 Python `zipfile`。
- `git commit` 提交**整个索引**，与 `git add` 传了哪些路径无关。

## 护甲映射页 20261011 回归（只分析，未改代码）
> 详见 `docs/armor-registry-page-20261011-regression.md`（切点 `30a4f83`，区间 43 提交）。
- **"护甲页卡"已修好**：整包构建 GameData 浏览器 → 只请求 `ArmorCoverage`（960ms → 230ms）。
- **一个根因、两种症状，判据 = 工作区有没有 `map_model`**：**有** → **启动即崩溃** `KeyNotFoundException`（`ApplySnapshot` **:519** ← `RefreshAsync` :403 ← `LoadCatalogOnceAsync` :351 ← `ArmorRegistryPageView.OnLoaded` :31 的 `async void` 无 try/catch）；抛错后 `:556` 的 `fingerprint` 未赋值 → `RefreshRuntimeStatusAsync` :451 再 `ApplySnapshot` → **不重启无法恢复**。**无**（全 `map=`）→ 优雅显示 `未知护甲 {id}`。机制：首循环跳过模型映射（`SourceItemId` 恒 null）→ `TargetItemId` **永不补 `Unknown`**，次循环却用**直接索引器** `Choices[mapping.TargetItemId]`。**缺陷本区间未改**，改的只是"什么时候炸"（护甲页**视图**现改为启动即创建：`PrewarmArmorPageHost`）。
- `未知护甲 {id}` 唯一来源 = `Choices` 缺该 `ItemId` ← SQLite `game_data_authority_kits`（**文件不存在直接返回空数组**）。`data/` 随 `BaseDirectory` 走 → **新版解压到新目录**是"目录读到 0 条"最可能成因。`RefreshAsync` 不检查目录条数（:402 无条件置 `hasLoadedCatalog=true`），**无复位路径**。
- **回归 #2（破坏性）**：`ModLibraryResidualCleanupService.Cleanup(...)` 对库根直接子目录若"未被快照引用"且"空/全是元数据" → **`Directory.Delete(recursive: true)`**，零迁移保护、不走回收站。= 截图 A `模型包部署失败，尚未编译护甲注册。`（:1523）头号嫌疑。
- **未解决**：现场"目录为什么是空的"（候选 A 整体 0 条 / **候选 B 目录正常但某 `map_model` 的 target 不在索引里**）。**最快判别：日志 `Unhandled UI exception` 括号里的数字 = 缺失 `ItemId`。**
- **P0 兜底已落实 ✅（2026-10-11 22:5x，+35/−4）**：`ApplySnapshot` 里对 `Choices` 的**读取**一律走新私有方法 `ResolveChoice`（`TryGetValue` + `Unknown` + `LogService.Warn` + 补进 `Choices`；**写入 3 处不动**）；`LoadCatalogOnceAsync` 加 `catch (Exception)` → Error 日志 + 页面状态，`finally` 复位单飞账本。新增 `HD2ModCore.Tests/ArmorRegistryPageResilienceArchitectureTests.cs`（3 条）。**根因（归属守卫）未动**——它会改"谁能看到自己的映射"的产品语义（老用户升级后可能看到空列表），留待拍板；详见 `docs/armor-registry-page-20261011-regression.md` §12。**两处隐患**：① `RefreshRuntimeStatusAsync` **检查** `IsBusy` 但**从不设置**；② `settings.json` 有两个，locale 从"从不被写"的那个读。

## 大功能 B（当前方向）：L4D2 模型整体移植到 HD2
> 详见 `docs/l4d2-to-hd2-physbone-migration.md`、`docs/l4d2-hd2-boundary.md`。
- **转向原因**：判断"给老 HD2 mod 批量刷权重加物理"太难 → 改为把 L4D2 动漫角色**整体移植**。旧项目自述失败 = "T-pose 摆 A-pose 太难"、"骨骼尺寸差距大"、**每次改动必须进游戏实测**无法高速迭代。
- **决定性发现**：源模型自带完整 jigglebone 参数。扫 **97 VPK / 6566 MDL** → **147 个带 jigglebone**（0 解析失败）。Zoey 主模型 `survivor_teenangst.mdl`：**127 骨 / 57 jiggle bone**。**骨名读不到**（`sznameindex` 指向内嵌 SMD 文本）**但父级索引有效** → 靠"层级 + bind pose 位置"重建。仿真方向依赖骨的 **+Z 轴**。
- **离线数学已量化**：**两侧世界 bind 同用一个公式** `world = inverse(bind)`（Source = `inverse(poseToBone)`@+96；HD2 = `inverse(inverse_bind)`）。⚠️ **不要靠 pos/quat 沿父链累加**（忽略 `posscale`/`rotscale`/`qAlignment`，静默塌陷）。**真实肢体长度比只有 0.868..1.135**；`clavicle->shoulder` 3.195 与 `neck->head` 2.615 是**"关节语义不同"而非尺寸差距**。**单一均匀缩放必然失败**（最优 scale=0.99，最大残差 **0.2515 m**）→ **解 = 逐骨相似变换，父→子一遍解完**。
- **关键未知（生死线，不得当事实）**：X1 **两张表分别能长到多大？** transform **107→164**、蒙皮调色板 **90→147**（不接受则方案作废）；X2 追加骨后 HD2 原有动画是否仍正确驱动前 90 骨；X3 Lua 每帧驱动 57 骨 × 每玩家的成本；X4 无骨名的额外骨如何稳定标识；X5 Source `is_flexible` 弹簧能否用二阶积分等价复现；X6 `set_local_rotation` 的局部/世界语义与 Source 的 +Z 约定是否一致；X7 目标 Unit（RE-2310 Slim Torso 已知可用）。
- **三层分工（待验证）**：① 身体骨（~20 ValveBiped）离线映射到 HD2 原生 90 骨 → 运行时由 HD2 动画驱动（免费）；② 额外骨（头发/裙摆）**原样追加在骨架末尾**（不改原有索引）→ 运行时由 Lua 按 Source 参数驱动；③ 顶点：身体区域重映射到身体骨，头发/裙摆区域直接指向额外骨（索引随骨平移，**不需重映射权重**）。**"运行时骨骼转译"不存在**。
- **【用户已拍板】** ① 接受不转译 L4D2 动画；② 额外骨驱动 = Lua 复现 Source 风格参数，**硬约束：维持游戏本体物理的低消耗，绝不引入 HD2IAS 类卡顿**；③ 第一阶段先做 A1~A3；④ 旧项目可改，多阶段目标 = 旧项目负责 **VPK 转化** ＋ WpfApp1 提供 **Lua 插件动骨支持**，**倾向分开**（怕耦合屎山）。
- **架构边界**：**"缝"必须落在「版本化文件格式」上，不是目录上。** （反例：旧项目用绝对路径引用本仓 → 已焊死）**隔离来自接口，不来自目录。** 唯一判据：改动一边时另一边要不要跟着改？**缝 = 一个版本化的「动骨声明文件」**（`schema` 必填；只有转换器写、运行时读、**永不回写**）。三角色：① 转换器（唯一懂 VPK/ValveBiped/jigglebone）② 管理器（只装/校验/展示）③ Lua 运行时（只读声明）。Lua 运行时与护甲映射**同仓但独立成 mod**。
- **性能反向设计**（逐条对 HD2IAS 三个慢因）：只驱动声明里列的额外骨／纯 Lua 二阶积分无原生调用／**每 Unit 一份独立状态**。
- **"斧"（离线回路，零游戏启动即可量化）**：`tmp/physbone-recon/` + `tmp/rig-lab/`（`rig_map.py`/`export_obj.py`/`a4_matrix.py`）。

### A1~A4 已完成 ✅（2026-10-11；数字全部实测）
- **A1**：`hd2mod gamedata extract asset` 取玩家 avatar Unit TocData（**21504 字节**）。transform **107** 条，名字解析 97/107，**最大误差 0.262 mm**。**`Matrices[i]` = row-major，平移在 `m[12..14]`** → **骨架来源从"外部常量"换成"游戏本体"**。骨架分两张表 transform **107** vs 蒙皮调色板 **90**。
- **A2**：`T_i = H_σ(i) · S_i^-1`（S 先 ×0.0254 换米）。对应表 **50 对**（20 身体骨 + 30 手指），**0 缺失**；三指标全 PASS。段长比（HD2/Source）：骨盆→胸 **0.9258**、胸→颈 0.8678、颈→头 2.6154、臂 **1.0352**、腿 **0.9092**。
- ⚠️ **两个陷阱**：① **两侧矩阵存储约定不同，必须转置**（Source = row-major **列向量**，平移 flat **3,7,11**；HD2 = row-major **行向量**，平移 flat **12,13,14**）—— 不转置 → 段长全算成 **0.0000**；② **脚部落点改变腿拉伸比 10%**：`L_Foot→l_foot` **0.909** / `→l_ball` **1.006**。
- **A3**：VPK(MDL/VVD/VTX) → 逐顶点 LBS → OBJ + 渲染。Zoey **121704 顶点 / 159363 三角形**。索引公式 `global = mdlModel.VertexOffset + mdlMesh.VertexStart + orig`（漏掉 mesh 起点会**炸成尖刺**）。**决定性发现**：**两引擎都把骨骼放在局部 +X 轴上**；**`T = H·S⁻¹` 不是错，是"太严格"** —— HD2 躯干/头链 **roll（绕骨轴）**与 Source 差 ~90°（腿 2.8° ✅ / 躯干·颈·头 70.8° ❌）⇒ **rest-pose 重定向只取 swing**（最短弧对齐骨轴 + 平移对齐原点）；**根骨 `Pelvis→hips` 只平移不旋转**。
- **A4（5 模型 × 2 roll 模式；`tmp/rig-lab/a4_matrix.py` + `a4-report.json`，venv 装 numpy 2.5.4 用 Kabsch）**：
  - **① 对应表完全通用**：5 模型（zoey/rochelle/coach/nick/ellis，骨数 127/127/125/124/125）**全部 50/50 命中、0 缺失** ⇒ `ValveBiped.Bip01_` 前缀在全幸存者通用；**rochelle 与 zoey 指标逐位相同**（`survivor_producer.mdl` 就是 `survivor_teenangst.mdl` 换名）。
  - **② 空间变换是真 180° 绕 Z 旋转**：Kabsch **175.3°~179.9°，det 恒 +1.0000** ⇒ **不是镜像、左右不互换**；与旧项目 `CreateScale(-1,-1,1)` **完全一致**。
  - **③ roll 误差结构性**（5 模型 torso roll 全 70.8°、腿全 2.8° ⇒ 可写进常量）；**swing 数值上优于 frame**（判别力最强是 **edge max**：zoey swing **49.2** vs frame **125.9**；刚体旋转不改边长，只能看极值/全局拟合残差）。
  - **⑤ 唯一病态 = Nick**：最大回退跳数 **7**（其余 3~4），dev max **3.36 m**，两者**严格同向**。最差区域全是 `source_extra_*`（骨名读不出的头发/裙摆/大袖子 helper）⇒ **"追加骨 + Lua 驱动"正是解药**。
  - **⑥ 身高膨胀 11.6%~14.2%**（最差 ellis 1.142），**5 模型全部 +11% ⇒ 系统性**，必须做成固定修正项。
  - **方法论（又踩两坑）**：① `|dst−src|` 是**假诊断**（主要成分就是那 180° 空间旋转）→ **要么用旋转不变量（边长比），要么先拟合掉全局刚体运动**；② **不要用单根骨的帧当全局映射**（混进 `Pelvis→hips` 的 90° 骨轴差）→ **全局量必须从点云拟合**。
  - **下一批修正（性价比排序）**：① 回退→追加骨；② 显式指定躯干/头关节语义（消灭身高膨胀）；③ 脚部改 `l_ball` 落点；④ 30 根手指逐指判定；⑤ 纳入 `TorsoScale = 1.042`。

### 旧项目现状 / 新工具（WpfApp1 内重写，进行中）
- **旧项目绝对路径已修 ✅**，但**编译不过（预先存在）**：5 个错误全是引用 WpfApp1 里**已被删除**的类型（`...PatchReconstruction.Validation`、`CanonicalDependencyClosure*`）。旧项目最后提交 **2026-09-03**。**用户已拍板：在 WpfApp1 内重写，不修旧项目。**
- 方案：**两个新工程** —— `HD2SourceModel`（net8.0，**零引用**，纯 Source 格式读取层，编译期强制"不认识 HD2"）+ `HD2SourceMigration`（net8.0，引用 `HD2SourceModel`+`HD2ModAdaptation`，映射+产出层）+ `HD2ModCli` 薄子命令 + 各自 `*.Tests`。
- **仓库内此前完全没有 VPK/MDL/VVD/VTX/ValveBiped/mstudio 读取代码**（新能力）。`HD2ModAdaptation/PatchReconstruction/UnitMesh/Canonical/` 已有 31 个 `Canonical*` 编译器= "给 Unit 加骨 + 重写蒙皮"能力现成。矩阵约定：仓库统一 `System.Numerics.Matrix4x4`（float、行向量）。
- **Source 格式细节权威在 `HD2SourceModel/*.cs` 文档注释**（VPK/MDL/VVD/VTX 字节布局、`mstudiojigglebone_t` 29 参数、索引公式）。回归基准 `Lunalice_Zoey.vpk`/`survivor_teenangst.mdl` = **127 骨（62 根读到 `ValveBiped.*`，65 根名字读成 SMD 文本垃圾 → 落 `source_extra_N`）/ 57 jiggle / 32 bodypart / 121511 VVD 顶点（装配后 121704）/ 159363 三角形**。
- ⚠️ **实测三次证伪规范**（合成样本测不出、只有真实文件能证伪）：① **VPK 条目步长与版本号无关**（97/97 是"版本号 1 + 18 字节条目"）⇒ 两种步长都试，判据 = 正好吃掉整棵树；② **MDL 骨结构步长 216 字节对 v44/48/49/53 全成立**（184 几乎全失败）；③ **bodypart/model 名是 UTF-8**（作者中文标注如 `裙Dress_Skirt`、`猫耳CatEars`）。另：VTX stripGroup 步长 v6=25 / v7=33，头版本号不足以判断 ⇒ 两种都试。

## 背景功能（细节全在 `docs/`）
- **【硬约束】只允许依赖游戏本体，不得依赖任何外部系统（尤其其他 mod）。** `HD2IAS` 只作反面参照物。
- **大功能 A（已被 B 取代）**：引擎 = **Autodesk Stingray（原 Bitsquid）**，**裁掉全部物理命名空间**；`Unit` 119 成员**零 `create_*`**、`Mesh` 24 成员**零顶点/权重接口** → **纯运行时 Lua 改不了权重、加不了骨、造不出物理**。尸体堆叠不卡 = **休眠刚体**；披风/布娃娃**借不了**。**改内存边界：改「数据」✅ / 改「代码」❌**。官方入口 `set_animation_bone_mode('ignore')` / `set_bones_lod` / `crossfade`；**HD2 抖动骨 API 只能驱动状态机里已有的 6 根骨**。详见 `docs/hd2-armor-physics-research.md`。
- **信息中心（阶段 0–6 已完成）**：`ModInformationFactRegistry` = **25 kind** 单一真相，`RequestAsync<T>` = **唯一读取入口**，`Validate()` 在 `CreateModInformationCenter` 开头跑（新增 kind 忘登记 `SourceRoots` = **启动期失败**）。硬约束：**缓存不得取代真实性**（键全由来源指纹构成、不落盘、双向测试）。铁律：**"读不到" ≠ "没有覆盖"**；**图标不进事实**；缩略图**磁盘键必须含内容**。护甲页预热三要求：**单飞加载**、**取数闸门 `displayFactsReady`**、**不占 UI 线程**。`ApplyExecutor` **只支持硬链接与符号链接**。
