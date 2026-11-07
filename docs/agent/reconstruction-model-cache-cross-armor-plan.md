# HD2 Mod 重建、模型缓存与跨护甲替换：阶段规划与已完成基础

**日期：** 2026-07-15  
**状态：** 规划基线；前置的部件语义、材质事实、稳定缓存与诊断链已完成一轮真实样例验证。  
**适用项目：** `HD2ModAdaptation`、`HD2ModCore`、`HD2ModManager`。

---

## 1. 文档目的与核心原则

本文件用于在开始“旧 Mod 重建适配 / 模型缓存 / 跨护甲副本”之前固化已验证的事实、现有能力、禁止回退的旧方案与推荐阶段顺序，避免后续上下文压缩或换任务时重复实现已有功能。

### 1.1 最终用户目标

用户拥有一个替换某件物品（例如 CW-22）的漂亮自定义模型 Mod，但希望在装备另一件同类型物品（例如 B-01）时仍使用该模型。最终管理器应支持：

1. 自动将过时 Mod 的自定义模型重新适配到其原本替换的、当前游戏版本的同一 `Unit AssetKey`；
2. 从 Mod 提取经验证的模型部件，保存为可复用缓存资产；
3. 将缓存模型按明确部位、层、体型、骨骼和材质规则写到其他同类型护甲或头盔的当前 Unit 模板中，生成**新的测试副本 Mod**。

### 1.2 不可违反的安全规则

```text
旧 Mod 自定义内容 + 当前 Game Data target Unit 模板
    -> 新的 current-version patch
```

- **禁止**把旧 Unit/Composite Unit 原样保留到新输出；即使能回读，游戏仍可因旧资源版本而报 `0x44415441`（过时）。
- **禁止**把“二进制 header/layout 修补”称为自动修复；早期 `ModUnitRepairService` 已确认错误并已移除。
- **禁止**在 Mod 详情页解析大型 Patch/stream/GPU 或写 SQLite；详情与库页只能消费稳定 facts。
- **禁止**覆盖源 Mod；所有重建和迁移都先输出为新目录、新 Mod，供用户测试和导入。
- **禁止**把未知部位、附件、Slim↔Stocky 直接互配静默写出。
- **禁止**将缺少材质闭包的自定义 Material 静默复制到输出。

---

## 2. 项目职责边界

```text
HD2ModAdaptation
  - Patch/TOC/payload/Unit/Composite/Bone/Material/Texture 二进制读取
  - Mesh、骨骼、材质、纹理依赖与重建写出
  - 只读 Patch facts、section 级材质引用、分类与低层 DTO

HD2ModCore
  - SQLite facts 生命周期、Game Data 索引、Mod 库与 Profile 业务投影
  - 当前 Game Data target 定位、材质来源诊断、计划/输出目录编排
  - 不重新实现 Adaptation 的二进制格式解析

HD2ModManager
  - WPF 交互、计划预览、后台任务、用户确认、输出导入与部署状态
  - 不直接解析 Patch 或 Game Data payload
```

后续新增功能必须保持这条边界。新功能不能依赖 Core 的已废弃旧 Unit 修补实现。

---

## 3. 已完成且已验证的前置基础

### 3.1 Game Data 稳定索引

Game Data 索引位于：

- `data/indexes/asset-index.sqlite`

已索引：

- Archive、AssetKey、TOC/stream/GPU offsets 与 size；
- `AssetKey -> archive` 反向关系；
- archive 元数据、名称、分类；
- Armor Unit 的 mesh 部件语义事实。

当前 `GameDataPackageResolver` 已修复：当游戏目录同时有直接 archive 和 `bundles.nxa` 的 bundled archive 时，必须合并枚举两者。此前任一侧被遗漏会导致当前 Unit/Material 被误判为不存在。

**UI 使用要求：** Game Data 浏览、Mod 高级详情均只读索引和稳定 facts；不能打开详情才扫描资源。

### 3.2 Mod Patch 稳定事实缓存

Mod facts 位于：

- `data/indexes/mod-facts.sqlite`

每个 Mod 持久化：

- Patch group；
- AssetKey 集合与 payload size；
- Unit → Material；
- Material → Texture；
- 现在还包含 Unit 的 **MeshInfoIndex + section ordinal** 级 Material 引用上下文。

缓存实现：

- `CachedPatchGroupAnalysisProvider`
- analyzer/cache version：`patch-group-v4-section-materials`

任意旧 facts 会自动失效并重建，不需要用户手工删除 SQLite。

### 3.3 Unit 部件、层和体型稳定事实

已完成并经真实 Armor Game Data、真实 Mod 样例验证：

- `UnitMeshPartKind`：头部、胸口、胯部、左右臂、左右腿、左右肩甲、附件等；
- `UnitMeshPartLayer`：内部、护甲、附件、隐藏壳、静态；
- `UnitMeshBodyVariant`：`Slim` / `Stocky` / `Any`；
- 是 **Mesh 级** 而不是 Unit 级事实。

来源：

- SDK `CustomizationInfoOffset` 是 Unit header `0x4C`，不是错误的 `0x50`；
- SDK 全局 `bonehash.txt` 提供 MeshID 到骨骼/部件名的映射；
- Game Data 索引重建时只扫描 Armor archive，并持久化到 `game_data_unit_parts`。

变体规则：

```text
Slim -> Slim       精确
Stocky -> Stocky   精确
Any -> Slim/Stocky 可兼容，须显示为兼容替代
Slim <-> Stocky    非精确，禁止静默自动匹配
```

**重要架构结论：** Mod 的 Unit AssetKey 通常就是其替换的原版 Unit AssetKey；Mod 高级详情应映射 Game Data 稳定部件事实，不重新解析 Mod Unit 来猜部位。

### 3.4 原版隐藏壳与极小化网格

人工/自动流程都会将未使用的 target mesh 变为极小化占位几何，以隐藏原版模型但保留当前 Unit 壳结构。

已验证占位形态通常为：

```text
vertices <= 3
triangles <= 1
```

但它可以存在于 LOD0，也可以存在于 LOD1–LOD3。因此不能仅凭 `IsLod` 忽略。

已知规则：

- 有 replacement 的 target mesh 不应先被 minify；只 minify 未替换 mesh；
- 无 replacement 的 target Unit 仍必须输出 minify-only 壳；
- 最终 patch 必须包含相关目标集合的全量 target Unit，不可只输出“有自定义模型”的 Unit，否则游戏会回退原版部件。

实现参考：

- `PlaceholderUnitMeshMinifier`
- `UnitMeshAdaptationPlanner`
- `AdaptationPatchSmoke` 的 broad coverage 分支。

### 3.5 材质闭包、内嵌/分离模式与当前原版回退

已有的 Material 功能：

- Patch 内嵌 Material / Texture 检测；
- 分离模型包与材质包识别；
- 材质包拆分、合并、winner 验证；
- Material → Texture 依赖闭包；
- 高级详情与 Profile 级材质诊断；
- 模型 Mod 的材质交付模式分类：
  - `EmbeddedComplete`
  - `EmbeddedIncomplete`
  - `ExternalResolved`
  - `ExternalUnresolved`
  - `Mixed`

**新确认的关键结论：** 内嵌 Mod 和分离 Mod 都可能保留对原版 Game Data Material 的引用。Material ID 不在 Mod 内，不等于必须由库内材质包提供。

正确判断单位是：

```text
Unit AssetKey -> MeshInfoIndex -> section ordinal -> Material source
```

而不是仅仅：

```text
Unit -> Material ID
```

已接入 Profile 材质诊断的 current Game fallback：

1. 根据 Mod Unit AssetKey 通过 Game Data SQLite 找当前 archive；
2. 使用既有 `ArchiveUnitMeshReader` 读取一个 current target Unit；
3. 对同 `MeshInfoIndex + section ordinal` 找到 target section 的 Material；
4. 若 source section 是极小化占位，自动接受 `CurrentGameMaterialFallback`；
5. 若 source Material 与 target Material 相同，也自动接受；
6. 若可见 section 的 target Material 不同，显示 `CurrentGameMaterialCandidate`（`⚠`），不静默覆盖自定义材质；
7. 无映射且无 Mod/库内提供者时，仍报告真实缺失。

前台展示：

- `✓ 使用当前原版材质`：安全回退，不算缺失；
- `⚠ 发现当前原版材质候选`：可见自定义 section，需库内材质包或后续用户确认；
- `缺失材质/贴图`：真实阻塞项。

**重建写出规则：** current Game fallback 的 Material/Texture 不应复制进输出 patch；输出应引用当前游戏原版资源。自定义 Material 只有在闭包完整时才能复制。

**2026-07-15 分离材质包实证修正：** “闭包完整”不等于每个自定义 Material 都必须被嵌入到重建后的模型 Patch。网络上的常见交付方式是模型包只保留 Unit 对同一 Material `AssetKey` 的引用，而让用户在其后启用独立材质包；后者以相同 Material `AssetKey` 覆盖，并可只携带发生变化的 Texture，其余 Texture 继续由当前 Game Data 提供。对 `【全护甲】战术水手服Plum` 的只读审计确认：模型包有 701 个 Unit、只有 Unit 类型条目，引用两个 Material ID；七个换色材质包都提供同一个其中一个 Material ID，并各自只携带 3 个 Texture、Material 本身仍引用 11 个 Texture。这解释了为什么游戏更新通常只要求重新导出模型包：过时的是 Unit/mesh/GPU 结构，独立 Material/Texture provider 不依赖旧 Unit 结构。

因此，重建计划必须把材质来源分类为：

```text
CurrentGameFallback       -> 只引用当前 Game Data
ExternalPackageProvider   -> 保留/写入原 Material ID，不复制；要求选定材质包在部署顺序中位于模型包之后
EmbeddedCustom            -> 只在输出需要自包含时复制 Material 和它所需的非 Game Data Texture
Unresolved                -> 阻断或回退 current target material
```

有效闭包的验证范围应是 `重建模型包 + 用户选择的外置材质包 + 当前 Game Data`，而不是孤立要求模型包自身拥有全部 Material/Texture。仍须逐 section 验证 Material ID 与外置 provider 的 AssetKey 完全一致，并报告任何既不在 provider、也不在 Game Data 的 Texture。

### 3.6 材质拆分/合并现状

实现：

- `MaterialPackagingService`
- `MaterialPackagingApplicationService`

支持：

- 内嵌材质拆为“模型包 + 材质包”；
- 选择候选材质包生成内嵌版；
- 输出验证 AssetKey 集合、payload hash、有效 Unit → Material → Texture 图。

限制：

- 当前 UI 应用服务仅支持单 Mod 的单 Patch 文件组；多 patch group 后续单独扩展。
- 拆分/合并只是包装与闭包功能，不等于 Unit 重建。

### 3.7 Manager 启动和状态稳定性

最近完成：

- 库 facts 恢复不再阻塞 Manager 启动；启动先显示 UI，再后台恢复稳定投影；
- 未处理 WPF 异常会写入 `logs/manager_*.log` 并显示错误；
- ProfileService 随库快照更新重载，避免新导入 Mod 长期显示“状态未知”；
- 后台拆分/导入触发的 UI 刷新已切换至 Dispatcher，避免跨线程修改 `ObservableCollection`。

用户已验证当前状态恢复正常。

---

## 4. 已验证的重建能力与已知限制

### 4.1 可复用的重建能力

`HD2ModAdaptation` 已具备：

- `PatchTocScanner`、`PatchEntryPayloadReader`；
- `PatchUnitMeshReader`、`GameDataUnitMeshReader`；
- `UnitMeshWriter`：重写 Unit/Composite/GPU payload；
- `PatchArchiveWriter`、`SdkStylePatchReconstructor`：移除旧 Unit、加入 current target Unit、重组 patch；
- `MaterialDependencyResolver`：闭包解析与安全复制；
- `PlaceholderUnitMeshMinifier`；
- 新版 `Processing.MeshTransfer`、`BoneRemapper`、`MaterialMapper` 等处理链。

`StrictUnitMeshTransfer` 是已标记废弃的历史 fallback，后续新功能不应以它为主路径。

### 4.2 已验证的游戏行为

- 只要输出仍残留旧 Unit/Composite 或旧结构数据，游戏可能报 `0x44415441`（过时）；
- 用 current target Unit 模板重建并移除旧 source Unit 才是正确路线；
- 回读成功不是游戏兼容证明；必须有游戏内测试；
- source Material/Texture 闭包在真实 Chiffon 样例中常不完整，不能盲目传播；
- 多材质 mesh 的 bone remap 必须按 section/material 处理，不可只取 mesh 的第一个 section；
- target 未使用 mesh 不 minify 会造成原版残留；目标模型完整 shell 需要 minify-only Unit 输出。

### 4.3 已完成的冒烟工具

主要工具：

- `_diagnostics/AdaptationPatchSmoke`：target-shell 写出与 broad coverage；
- `_diagnostics/UnitSemanticProbe`：部件/层/体型来源验证；
- `_diagnostics/MaterialUsageProbe`：极小化 mesh 与旧材质 binding 关联；
- `_diagnostics/MaterialSlotFallbackProbe`：内嵌/分离 Mod 的 current Game section material 回退验证；
- `_diagnostics/MaterialPackagingSmoke`：拆分、合并、winner 图验证。

这些工具默认是本地诊断工具，通常被忽略文件规则排除，不应把其输出 artifact 提交到正式仓库。

---

## 5. 阶段 1：单 Mod、同 AssetKey 的重建适配

### 5.1 目标

将一个过时 Mod 的自定义模型重新适配到该 Mod 本来替换的、当前游戏同 `Unit AssetKey` 的 target Unit 壳。

$$
\text{old Mod custom meshes}
\rightarrow
\text{current Game Data Unit with same AssetKey}
\rightarrow
\text{new test-copy patch}
$$

不跨 archive，不让用户选择另一个物品，不做跨护甲猜测。

### 5.2 第一版 UI：先预览、后写出

在 Mod 高级详情中增加“重建适配（实验性）”入口。

第一步只创建可解释计划，显示：

- source patch / source Unit / target archive / target Unit；
- source mesh → target mesh 匹配；
- 部位、层、体型、LOD/culling/static；
- minify 的 target slots；
- replacement 的 target slots；
- section 级材质来源：`CurrentGameFallback`、`EmbeddedCustom`、`ExternalLibraryProvider`、`Unresolved`；
- 材质/纹理闭包与阻止原因；
- 将删除的旧 Unit / Composite；
- 预计输出 AssetKey、Unit、Composite、Material、Texture 数量；
- 所有低置信度或拒绝项。

只有用户确认高置信度计划后，才允许“生成测试副本”。

### 5.3 后台数据与服务设计

新增中立 DTO（名称可调整）：

```text
ReconstructionRequest
ReconstructionPlan
ReconstructionUnitPlan
ReconstructionMeshSectionPlan
ReconstructionMaterialSource
ReconstructionWriteResult
```

建议定位：

- Adaptation：`SameKeyUnitReconstructionPipeline` 或沿用/整理 SDK-style pipeline；
- Core：`IModReconstructionPlanningService`，负责选取 Mod node、事实、Game Data archive、材质提供者和输出目录；
- Manager：预览窗、确认、后台任务、导入新 Mod。

### 5.4 材质决策

每个 section：

| 来源 | 输出策略 |
|---|---|
| `CurrentGameFallback` | 引用当前游戏资源；不复制 Material/Texture |
| `EmbeddedCustom` | 仅在完整闭包时复制 Material/Texture |
| `ExternalLibraryProvider` | 使用已确定库内材质包；根据输出策略打包或保持分离 |
| `CurrentGameMaterialCandidate` | 第一版默认阻止或要求明确确认 |
| `Unresolved` | 阻止写出 |

### 5.5 完成与测试标准

1. 选一个已知旧 Mod；
2. UI 预览可解释且无隐式猜测；
3. 输出到新目录，原 Mod 未改变；
4. 自动导入测试副本；
5. 回读验证无重复 AssetKey、无旧 Unit 泄漏；
6. 游戏内验证：可进入、模型显示、无原版残留、骨骼正常、材质状态符合计划；
7. 至少验证一个内嵌材质和一个分离材质样例。

---

## 6. 阶段 2：提取模型为缓存资产（暂不迁移）

### 6.1 目标

建立可复用、可验证的模型缓存资产，但不立即允许用户写到另一个物品。

第一期只支持：

```text
Armor -> Armor
Helmet -> Helmet
```

### 6.2 UI 功能

在 Mod 高级详情中：

- 展示可提取的可见 mesh；
- 显示部位、层、体型、mesh 几何统计、骨骼覆盖、section 材质来源；
- 用户勾选要缓存的 mesh/section；
- “提取到模型缓存”；
- 展示缓存条目、验证状态、删除操作；
- 不提供跨物品生成按钮。

### 6.3 缓存必须保存的内容

不允许只保存裸 GPU bytes。每个缓存资产至少保存：

- source Mod/node、Patch group、source Unit AssetKey；
- source fingerprint 与 analyzer version；
- 已选择的 MeshInfoIndex / section；
- stream layout、raw mesh、sections、vertices、triangles；
- skeleton/bone remap 信息；
- 部位、层、体型稳定事实；
- `CurrentGameFallback` / embedded / external 材质来源；
- Material/Texture 闭包、来源与失败原因；
- Game Data index fingerprint；
- 提取时间、验证结果。

### 6.4 完成标准

- 重启后缓存仍可列出；
- source Mod 改动或 Game Data 指纹变化时缓存明确标记为过期；
- 用户能从 UI 看出哪些 mesh 可安全迁移、哪些材质未闭合；
- 缓存提取不重写 source Mod。

---

## 7. 阶段 3：跨护甲生成副本

### 7.1 目标

用户选择缓存模型与目标 Armor，生成一个新的目标替换 Mod。源 Mod 不被修改。

### 7.2 第一版必须是“计划预览”

用户选择：

1. 缓存模型资产；
2. target Armor；
3. Slim / Stocky 偏好；
4. 是否允许 `Any` 兼容；
5. 各部位的手工确认映射。

系统必须显示：

- 可精确匹配；
- `Any` 兼容匹配；
- 缺失 target 部位；
- 源/目标 layer 不匹配；
- shared Unit 影响；
- section 级材质来源和闭包；
- minify target shell 的范围；
- 所有阻塞项。

### 7.3 自动候选规则

最少要求：

$$
\text{same part kind}
\land
\text{same side}
\land
\text{same layer}
\land
\text{visual}
\land
\neg\text{LOD}
\land
\neg\text{static}
$$

体型：

- `Any` 可作为 Slim/Stocky 的兼容候选；
- Slim ↔ Stocky 默认禁止；
- Unknown / Accessory 必须人工确认。

几何大小、mesh index、slot ID 只能用于候选排序和解释，不能单独决定跨护甲落位。

### 7.4 共享 Unit 风险

目标 archive 可能与其他护甲共享相同 Unit AssetKey。生成副本前必须：

- 通过 Game Data SQLite 找出该 Unit 被哪些 archive 使用；
- UI 明确提示影响范围；
- 第一版默认按 AssetKey 输出，允许用户知情确认；
- 不应伪装为“仅影响一个物品”。

### 7.5 写出标准

- 所有 target Unit 使用 current Game Data 模板；
- 有 replacement：保留目标 slot 并迁移 source mesh；
- 无 replacement：minify-only target Unit；
- 删除旧 source Unit/Composite；
- current Game fallback 材质不复制；
- custom Material/Texture 必须闭合；
- 输出独立 Mod；
- 进行回读、引用图、重复 AssetKey 和 stale Unit 检查；
- 游戏内测试后才扩展候选范围。

---

## 8. 推荐实施顺序

```text
当前状态：section 级材质来源与 current Game fallback 已上线

下一步：阶段 1 计划预览（只读）
    ↓
阶段 1 写出测试副本（同 AssetKey）
    ↓
真实游戏内验证：内嵌 + 分离材质样例
    ↓
阶段 2 模型缓存提取与缓存浏览 UI
    ↓
阶段 3 跨护甲计划预览（不写出）
    ↓
阶段 3 跨护甲测试副本写出
```

不要跳过“计划预览”和“新副本输出”。每步都必须有：

```text
后台契约 + 前台 UI + 可解释报告 + 回归测试 + 真实样例测试
```

---

## 9. 当前工作区与验证状态

### 最近正式代码变更（尚未提交，等待后续前台验证/阶段确认）

- section 级 Mod Material facts；
- current Game section Material fallback；
- bundled/direct archive 联合枚举；
- 启动稳定性与后台 UI Dispatcher 修复；
- 删除错误旧 `ModUnitRepairService`；
- 材质交付模式与 UI 摘要。

### 最近成功验证

- `HD2ModAdaptation.Tests`：79/79；
- `HD2ModCore.Tests`：193/193；
- Manager 构建：`HD2ModManager/bin/Debug/net10.0-windows-section-material-fallback`；
- `git diff --check` 无输出。

### 已知警告（本阶段未处理）

- `StrictUnitMeshTransfer` obsolete 警告；新功能应使用 Processing 路径；
- SharpCompress / SQLitePCLRaw NuGet 安全警告；
- Windows Registry 的 CA1416 平台警告。

---

## 10. 禁止重复实现清单

后续开始工作前先检查并复用：

- 当前 Game Data AssetKey/archive 定位：`IAssetArchiveIndexService`；
- 原版 target Unit 读取：`IArchiveUnitMeshReader` / `ArchiveUnitMeshReader`；
- Patch facts 缓存：`IModFactsStore`、`CachedPatchGroupAnalysisProvider`；
- section 级 source Material 事实：`PatchAssetReference.MeshInfoIndex`、`ReferenceIndex`、`IsPlaceholderMesh`；
- 材质闭包/复制：`MaterialDependencyResolver`；
- 拆分/合并：`MaterialPackagingService`；
- hidden shell minify：`PlaceholderUnitMeshMinifier`；
- target shell 写出：`UnitMeshWriter`、`PatchArchiveWriter`、SDK-style reconstructor；
- 部件/层/体型：`UnitMeshPartClassifier` 和 `game_data_unit_parts`；
- Profile 状态与后台刷新：`DerivedStateCoordinator`、`ModUserStatusProjector`；
- WPF 线程更新：所有 `ObservableCollection` 变更必须回到 Dispatcher。

---

## 11. 继续任务时的第一步

开始阶段 1 前：

1. 阅读本文件；
2. 读取 `docs/hd2-patch-mesh-retargeting-notes.md`、`docs/unit-part-semantic-facts-design.md`；
3. 检查当前 Git status，避免误把 `_diagnostics` 输出加入提交；
4. 以“同 AssetKey、只读计划预览”为唯一范围创建 DTO 和 UI；
5. 不要直接开始跨护甲写出。
