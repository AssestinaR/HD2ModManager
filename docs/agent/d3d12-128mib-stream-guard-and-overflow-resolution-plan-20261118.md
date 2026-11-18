# D3D12 128 MiB 单流显存块上限守卫与合并超限交互处置方案（2026-11-18）

## 1. 背景与根因

Helldivers 2 内部 `D3D12MemoryAllocator` 的单个堆块上限为 **`128 MiB`（`0x08000000` = `134,217,728` 字节）**。当任意单个 `Unit` 的某条 `Stream` 顶点缓冲区（`VertexBufferSize`）或索引缓冲区（`IndexBufferSize`）达到或超过 `134,217,728` 字节时，游戏引擎分配器返回 `nullptr` 并在 `helldivers2.exe + 0x6f34bd`（`IID_PPV_ARGS(&allocation.m_pResource)`）解引用空指针必现崩溃。

本次实测复现（`Airi 2.3`）由三重因素叠加触发：
1. 源模型将高精度 LOD0 网格原样复制 4 份填入 `LOD0..LOD3`；
2. 用户的 `wardrobe.json` 同时启用了两套互斥全身变体（`本体带裙子` + `本体无裙子`）并开启「头身合并」，低污染合体槽 `body-arms`（`0x6acfd07071fceeff`）合入 `2,138,376` 个顶点；
3. `10800438` 外壳升级后，`RightArm` 外壳模板携带了无对应权重组（仅单组 `bone_weight#0`）的全零 `bone_index#1`、`bone_index#2`，在 `CanonicalDecorationUnitAppender` 合并时传染给整条合并流的所有顶点（`+8 B`/顶点，步长由 `56 B` 涨至 `64 B`，总计 `136,656,064 B = 130.33 MiB > 128 MiB`）。

---

## 2. 用户确认的目标与设计边界

根据本轮与用户确认的结论，修复由以下四部分组成，不引入有损自动减面或静默删远景 LOD：

1. **剪除无权重支撑的僵尸骨骼索引组（`bone_index#1+`）**：
   - 在 `CanonicalAppendBoneCompiler` 重写合并顶点语义时，若某个顶点仅含 `bone_weight#0`（不存在同下标 `Type == 7 && Index == component.Index` 权重组件），剔除 `Type == 6 && Index > 0` 的冗余 `bone_index` 组件，避免 donor 外壳的空骨骼组传染整条合并流。
   - **不变式保留**：若宿主 `targetUnit` 自身流模板（`template`）本来就声明多组 `bone_index` 且覆盖全部最终语义，`CanonicalStreamContractCompiler.CoversAllFinalSemantics` 仍原样保留宿主流契约；若测试或输入显式构造多组最终组件直传 `CanonicalStreamContractCompiler`，原有契约行为保持不变。

2. **`CanonicalUnitRebuilder` 底层 `128 MiB` 单流缓冲区硬守卫**：
   - 定义公开常量 `CanonicalUnitRebuilder.MaxD3D12SingleBufferBytes = 128u * 1024u * 1024u`（`134,217,728` 字节）与稳定诊断码 `CanonicalUnitRebuilder.StreamBufferLimitDiagnosticCode = "StreamBufferExceedsD3D12BlockLimit"`。
   - 序列化每条 `Stream` 时，若 `vertexSize >= MaxD3D12SingleBufferBytes` 或 `indexSize >= MaxD3D12SingleBufferBytes`，拒绝产出该 `Unit` 并返回上述诊断码及精确字节数、步长、顶点数信息，确保任何路径都不会静默写出进游戏必崩的 Patch。
   - `SectionalUnitMerger` 在检测到 `StreamBufferExceedsD3D12BlockLimit` 时抛出专用的 `SectionalMergeBufferOverflowException`（继承自 `InvalidDataException`，保留现有 `IsExpected` 异常捕获兼容，同时携带精确超限字节数、步长与参与合并的 `SourceFileIds`）。

3. **超限组件归因与互斥/变体冲突识别（`SectionalMergeOverflowAnalyzer`）**：
   - 当 `SectionalCompiler` 在低污染合并（`CompileLowPollutionOwnModelAsync`）或专属切换合并（`CompileExclusiveArmorAsync`）捕获到 `SectionalMergeBufferOverflowException` 时：
     - 读取 `sectional/components.json`（若存在）与 `sourcePatch` 各 `Unit` 的结构头（`ReadStructureOnlyAsync`，毫秒级只读各 section 的 `NumIndices`）以及当前生效的 `disabledSections`；
     - 计算每个**当前处于启用状态**且在本次超限 `SourceFileIds` 中拥有可见几何段的组件组（`SectionGroup`）对超限缓冲区的估算字节贡献（按该组在 `SourceFileIds` 内独占/归属的启用三角形索引占比乘以实际超限流字节数）；
     - 识别冲突标签并计算默认预选状态（`RecommendedKeepEnabled`）：
       1. **`ExplicitExclusive`（互斥组冲突）**：多个已启用组件共享同一个非空 `ExclusiveGroup` → 保留第 1 个勾选，其余默认取消勾选；
       2. **`SuspectedVariant`（疑似互斥变体）**：无显式 `ExclusiveGroup`，但两个及以上 `SectionGroup` 在本次合并涉及的来源 `Unit`（含全表 `Members`）中重叠覆盖 `>= 2` 个相同部位 `Unit`，且各自对超限流的估算占用均 `>= 16 MiB`（或占比 `>= 15%`） → 判定为全身/多部位同位变体叠穿（如 `本体带裙子` vs `本体无裙子`），保留第 1 个勾选，按顺序预取消后续重叠变体直至预计剩余流大小 `< 128 MiB`；
       3. **`None`（普通高占组件）**：其余参与合并的启用组件按估算占用降序列出，默认保持勾选（若前两类全部取消后预计仍 `>= 128 MiB`，再从最大项起预取消直至预计 `< 128 MiB`）。

4. **交互式三选一处置弹窗与状态持久化闭环**：
   - **宿主回调注入**：
     - 在 `VpkHostContext` 增加可选回调 `SectionalMergeOverflowHandler? OnMergeOverflow { get; init; }`；
     - `SettingsService.CreateVpkHostContext()` 默认挂载 UI 弹窗处理器（覆盖详情页切换/应用/头身合并/重构建、护甲映射页低污染重编、批量重构建、一键修复等全部 GUI 入口）；
     - CLI / 无头单测未传回调时 `OnMergeOverflow == null`：
       - 若 `CanFallbackToUnmerged == true`（自定义素体低污染模式），安全回退普通多槽素体（`CompileOwnModelAsync` + 写 `low-pollution-unsupported.json` 指纹标记），并在 `Warnings` 中明确列出超限数值与冲突组件名；
       - 若 `CanFallbackToUnmerged == false`（目标护甲专属切换合并），返回明确失败原因，点名超限数值与冲突组件名。
   - **WPF 弹窗（`MergeOverflowDialogWindow`）**：
     - 标题：`合并模型超出游戏显存上限（128 MiB）`；
     - 说明文字：指明 Mod 名称、超限合并槽位、当前单流大小与 `128.0 MiB` 上限，说明不处理进游戏必定崩溃；
     - 组件列表（复选框 + 友好短名称 + `[互斥组冲突]` / `[疑似互斥变体]` 标签 + `~XX.X MiB` 占用），默认按 `RecommendedKeepEnabled` 预选；
     - 底部实时汇总栏：随勾选变化实时刷新 `预计合并后单流：XX.X MiB / 128.0 MiB（安全 / 仍超限）`；
     - 三个操作按钮：
       1. **`一键关闭未勾选项并继续合并`（主按钮）**：当至少取消勾选了 1 个组件且预计大小 `< 128 MiB` 时可用；确认后将未勾选组件的成员段并入 `disabledSections` 重试合并（支持循环校验，若极端共享段导致重试后仍超限会更新实际字节数再次提示）；
       2. **`保留全部组件，退回非合并模式`（次按钮）**：`CanFallbackToUnmerged == true` 时可用（`false` 时禁用并显示提示「当前为目标护甲专属合并模式，无普通多槽素体可退；请关闭多余组件或改用自定义素体」）；选择后保留全部勾选，走 `CompileOwnModelAsync` 普通素体编译并写 `low-pollution-unsupported.json` 标记；
       3. **`取消`（取消按钮）**：中止本次编译，不改写 `wardrobe.json` 与 `Overwrite/`。
   - **`wardrobe.json` 与详情页内存态同步**：
     - 当用户在弹窗中选择「关闭未勾选项并继续合并」且编译成功时：
       - `SectionalCompileResult` 与 `ModCompileOutcome`、`VpkRebuildOutcome` 传出 `UpdatedDisabledSections`；
       - `ModCompileService.CompileAsync` 负责把更新后的 `Disabled` 原子写回磁盘 `wardrobe.json`（保证重构建、映射页、一键修复等所有非详情页入口都能持久化生效）；
       - `ModDetailsPageViewModel`（`ApplyVpkTargetArmorCommand`、`ApplyVpkComponentsAsync`、`ApplyHeadMergeAsync`、`RebuildVpkAsync`）在收到非空 `UpdatedDisabledSections` 时同步更新内存 `_vpkFile.State`、刷新 `VpkTree` 与 `_vpkGroups` 勾选，避免后续落盘把刚关掉的组件覆盖回去。

---

## 3. 涉及文件清单

1. **Adaptation 层**：
   - `HD2ModAdaptation/PatchReconstruction/UnitMesh/Canonical/CanonicalAppendBoneCompiler.cs`：剔除无对应 `bone_weight` 的冗余 `bone_index#1+` 组件。
   - `HD2ModAdaptation/PatchReconstruction/UnitMesh/Canonical/CanonicalUnitRebuilder.cs`：增加 `MaxD3D12SingleBufferBytes`（`128 MiB`）单流顶点/索引缓冲区硬守卫与诊断码。
2. **Core 层**：
   - `HD2ModCore/Infrastructure/Sectional/SectionalMergeOverflowModels.cs`（新增）：定义 `SectionalMergeBufferOverflowException`、归因数据模型、回调委托 `SectionalMergeOverflowHandler` 与归因分析器 `SectionalMergeOverflowAnalyzer`。
   - `HD2ModCore/Infrastructure/Sectional/SectionalUnitMerger.cs`：识别 `StreamBufferExceedsD3D12BlockLimit` 并抛出结构化 `SectionalMergeBufferOverflowException`。
   - `HD2ModCore/Infrastructure/VpkHostContext.cs`：新增 `OnMergeOverflow` 可选回调与 `ModDisplayName` 可选展示名。
   - `HD2ModCore/Infrastructure/Sectional/SectionalCompiler.cs`、`SectionalCompiler.LowPollution.cs`、`SectionalCompiler.Exclusive.cs`：接入超限归因、交互重试/退回普通素体/取消分支，以及 `UpdatedDisabledSections` 回传。
   - `HD2ModCore/Infrastructure/ModCompileService.cs`、`HD2ModCore/Infrastructure/VpkRebuildService.cs`：当 `UpdatedDisabledSections` 非空时持久化写回 `wardrobe.json` 并向调用方透传。
3. **WPF UI 层**：
   - `HD2ModManager/Views/MergeOverflowDialogWindow.cs`（新增）：主题一致的三选一交互窗口（支持组件勾选调整、冲突徽标、实时 MiB 预估）。
   - `HD2ModManager/Services/SettingsService.cs`：在 `CreateVpkHostContext()` 中挂载 UI 线程调度的 `MergeOverflowDialogWindow` 回调。
   - `HD2ModManager/ViewModels/ModDetailsPageViewModel.Vpk.cs`、`ModDetailsPageViewModel.VpkRebuild.cs`：同步 `UpdatedDisabledSections` 到 `_vpkFile`、组件组勾选与树状态。
4. **测试层**：
   - `HD2ModAdaptation.Tests/`：新增/扩展单测覆盖 `CanonicalAppendBoneCompiler` 死 `bone_index#1+` 剔除与 `CanonicalUnitRebuilder` `128 MiB` 守卫诊断。
   - `HD2ModCore.Tests/`：新增单测覆盖 `SectionalMergeOverflowAnalyzer`（显式互斥组、疑似全身变体识别、预选计算）及 `SectionalCompiler` 超限下的「一键关闭组件重试并写回 `wardrobe.json`」「退回普通素体」「取消」「无回调兜底」四条路径。

---

## 4. 验收与回归标准

1. `dotnet build -c Debug`：**0 警告、0 错误**。
2. `dotnet test -c Debug`：全量测试套件通过。
3. 针对真实 `Airi 2.3` 数据验证：
   - 死 `bone_index#1/#2` 裁剪后，`body-arms` 步长由 `64 B` 降回 `56 B`，单流大小由 `136.66 MB` 降至 `119.75 MB`（`< 128 MiB`）；
   - `SectionalMergeOverflowAnalyzer` 对 `Airi 2.3` 的 `components.json` 能准确识别 `本体无裙子` 与 `本体带裙子` 为「疑似互斥变体」并给出单键关闭预选方案。
