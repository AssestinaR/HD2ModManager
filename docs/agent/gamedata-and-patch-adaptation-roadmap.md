# Game Data 索引与 Patch 适配落地路线

**日期：** 2026-07-13  
**状态：** 规划基线  
**适用项目：** `HD2ModAdaptation`、`HD2ModCore`、`HD2ModManager`

---

## 1. 文档目标

本文记录 Game Data 资源索引、Item 资源关系、部件复用检测，以及 patch 内部分析迁移到 `HD2ModAdaptation` 的统一落地方案。

### 当前阶段性目标（只读优先）

当前阶段的实际目标是先建立可靠的“读取和展示”能力，不要求 Adaptation 立即具备写出或自动修复能力：

1. 能读取 GameData 目录中的 archive、TOC、AssetKey 和可确认的资源结构；
2. 能利用 `archivehashes.json` 将已存在的 archive hash 映射到显示名称/分类，并明确列出未映射 hash；
3. 在 Manager 设置页的“游戏 data 文件夹”卡片内提供一个“查看游戏资产状态”入口；
4. 点击入口后打开一个复用状态页任务窗口外壳的新窗口，内部使用可复制文本的只读信息卡片，展示 GameData 扫描结果，例如 Armor、Helmet 等已识别分类、archive 数量、已映射 hash、未映射 hash 和扫描问题；
5. Patch 读取主要服务于 Mod 详情页，只展示从该 patch 实际提取出的内容，不凭 GameData 或命名规则猜测未确认的资源关系；
6. 暂不要求 Manager 调用 patch writer、重建器或自动修复流程。

这里的“资产状态”是扫描和索引状态，不等同于“游戏兼容性验证”，也不等同于“所有 Item 业务关系已经建立”。

核心目标：

1. 将 patch 内部语义读取逐步迁移到 `HD2ModAdaptation`；
2. 为 Game Data 建立可增量更新的资源索引缓存；
3. 建立 Item → Unit/Material/Texture 的资源关系表；
4. 检测多个 Item 之间的 Unit、Composite、Mesh、Material 和 Texture 复用；
5. 让 Core/Manager 消费已经生成的事实和关系，避免重复扫描 patch 或 Game Data；
6. 严格依赖 Adaptation 的新功能不得调用 Core 旧 patch reader；Core 旧实现只能作为待迁移的兼容路径，不能作为新功能的数据源。

本阶段不重建 Core 已有的 Mod 资产业务功能。Mod 详情中的 patch/资产展示、资产标签、配置内覆盖检测以及派生缓存仍由 Core 负责；迁移内容是这些功能所依赖的 patch 文件事实读取实现和 facts 缓存来源。Core 继续负责缓存生命周期、业务投影和元数据补充，Adaptation 负责给出可持久化的 patch/GameData 事实。

当前 patch facts 缓存已按节点持久化，并以 patch 本体、`.stream`、`.gpu_resources` 的长度和修改时间进行失效判断，同时校验 Adaptation analyzer version。冲突检测工厂也必须接收实际的 `StoragePaths`，确保 Mod 详情和状态页使用同一数据根目录下的 facts 缓存。

---

## 2. 最终职责边界

```text
HD2ModAdaptation
  ├─ Patch TOC、payload、Unit、Material、Texture 的语义读取
  ├─ Game Data archive 读取
  ├─ Unit/Composite/Bone/Material/Texture 关系提取
  ├─ Patch group 分析
  ├─ Game Data 资源事实索引生成
  └─ 只读分析、依赖诊断和重建/写出能力

HD2ModCore
  ├─ Mod 库、manifest、文件完整性和 patch group fingerprint
  ├─ Game Data 索引 fingerprint、缓存生命周期和持久化
  ├─ archivehashes.json 的业务元数据投影
  ├─ AssetKey → archive/Item 的查询适配
  ├─ Mod 级聚合、标签、兼容性规则和派生缓存
  └─ 迁移完成前保留旧 patch 操作作为 fallback

HD2ModManager
  ├─ 编排索引构建和刷新
  ├─ 展示进度、状态、问题和复用组提示
  └─ 不直接解析 patch 或 Game Data 内部格式
```

`HD2ModAdaptation` 不引用 `HD2ModCore`。两者之间使用中立 DTO 或由 Core/Manager 提供适配器连接。

---

## 3. 已确认可复用的 Adaptation 能力

### 3.1 Patch 读取链路

以下实现继续作为唯一的 patch 内部读取基础，不新增第二套格式解析：

- `PatchTocScanner`
- `PatchEntryPayloadReader`
- `PatchUnitMeshReader`
- `GameDataUnitMeshReader`
- `MaterialDependencyResolver`
- `StingrayMaterialReferenceReader`
- `TemporaryMaterialCombiner`
- `UnitMeshReader`

### 3.2 使用边界

- `PatchMaterialDetector` 只能作为启发式 `MaterialModeHint`，不能作为完整性证明；
- `TemporaryMaterialCombiner` 只处理已经明确选择的模型 patch 和材料 patch；
- `MaterialDependencyResolver` 用于依赖闭包和缺失诊断，不保证所有真实样本都能闭合；
- `UnitMeshWriter`、重建器、transfer pipeline 只用于修复/重建，不在普通资产分析中调用；
- “能够读取”不等于“当前游戏能够接受”；旧 Unit/Composite 不能原样输出。

### 3.3 读取链路与写出链路的边界

必须区分以下两类验证：

| 验证 | 目的 | 是否影响当前只读目标 |
| --- | --- | --- |
| GameData archive/TOC smoke | 验证 GameData 能否扫描、索引和记录问题 | 直接相关 |
| Patch TOC/Unit/Material 读取 | 验证 Mod 详情所需的 patch 内容能否提取 | 直接相关 |
| Mesh transfer/reconstruction smoke | 验证旧 patch 模型能否按目标 Unit 重建并写出 | 不直接相关 |
| 游戏内加载/版本兼容验证 | 验证写出的新 patch 是否能被当前游戏接受 | 不属于当前只读目标 |

因此，真实 patch smoke 中出现的 `source and target stream layouts differ`，说明当前直接 MeshTransfer/重建路径不能处理该真实样本的 source/target stream layout 差异。它不会否定已完成的 GameData 索引、patch TOC 扫描、AssetKey 提取或 Mod 详情只读展示；它只会阻止后续把该样本直接送入模型 transfer、自动修复或 patch 写出流程。

同理，SDK-style smoke 中的 material slot 无法唯一解析和已有输出文件冲突，属于重建/写出测试的问题，不影响只读扫描。它们应作为后续重建专项 issue 保留，不能被误报为 GameData 资产缓存失效。

---

## 4. 总体数据层次

```text
Game Data archive 基础索引
        ↓
Item 资源关系表
        ↓
Unit/Composite/Mesh/Material/Texture 复用关系
        ↓
Patch group 语义分析
        ↓
Core Mod 聚合、完整性检查、兼容性提示
        ↓
Manager UI 展示和后续修复操作
```

所有层次都应保存 `SchemaVersion` 和 `SourceFingerprint`，避免不同解析器版本生成的数据混用。

---

## 5. 第一阶段：中立契约和测试夹具

### 目标

先定义跨项目传递的小型 DTO，验证边界，而不是立即改动整个 Core 派生链路。

### Adaptation 侧计划

新增只读分析契约，建议包含：

- `PatchGroupInput`
- `PatchGroupAnalysis`
- `GameDataArchiveInput`
- `GameDataArchiveIndex`
- `GameItemResourceInfo`
- `ResourceDependencyFact`
- `ResourceReuseGroup`
- `AdaptationAnalysisIssue`

DTO 只表达结构事实，不包含 Core 的 `ModNode`、SQLite 类型或 Manager UI 类型。

### 测试

优先使用现有合成 patch 测试夹具覆盖：

1. 单 patch 内嵌 Material；
2. 模型 patch + 明确材料 patch；
3. 缺少 Composite；
4. 缺少 Material/Texture；
5. `.stream` 或 `.gpu_resources` 缺失/越界；
6. 重复 AssetKey；
7. 多个 Item 共享 Unit/Material。

### 完成标准

- 不新增重复 TOC、payload 或 Unit 解析器；
- 所有失败都返回结构化 issue；
- 取消操作能够及时结束；
- DTO 不依赖 Core 和 Manager。

---

## 6. 第二阶段：Game Data 基础索引

### 目标

建立低成本的 archive 和 AssetKey 索引，使后续查询不必反复扫描 Game Data TOC。

### 第一版索引内容

每个 archive 保存：

- archive 名称和相对路径；
- archive hex；
- 显示名称和分类；
- TOC 格式/版本信息；
- `AssetKey`；
- type ID、file ID；
- TOC、stream、gpu resource 的偏移和长度；
- 资源所在 archive；
- 解析状态和问题。

第一版不读取所有 mesh、material 和 texture payload，只保存定位信息。

### 建议查询

```text
FindArchivesByAsset(AssetKey)
FindEntriesByType(TypeId)
FindEntry(ArchiveName, AssetKey)
GetArchiveStatus(ArchiveName)
```

### 缓存策略

Core 负责缓存生命周期，Adaptation 负责生成事实：

```text
GameDataIndexManifest
  ├─ SchemaVersion
  ├─ ParserVersion
  ├─ GameDataRoot
  ├─ ArchiveHashesFingerprint
  ├─ ArchiveCount
  ├─ IndexedArchiveCount
  ├─ SourceFingerprint
  └─ BuiltUtc
```

启动时先执行低成本检查：

- 相对路径；
- 文件长度；
- LastWriteTimeUtc；
- archivehashes.json fingerprint；
- parser/schema 版本。

发现变化后，仅重建变化 archive；结构版本变化时执行全量重建。需要时再对单个 archive 使用 SHA-256。

### 重要规则

`archivehashes.json` 主要提供：

```text
ArchiveHex → 显示名称/分类
```

它不替代 TOC 索引，也不包含完整的 Item 资源关系。

---

## 7. 第三阶段：Item 资源关系表

### 目标

回答“某个 Item 内部包含哪些 Unit、Composite、Material 和 Texture”。

### 输入

- `archivehashes.json` 中的分类、archive hex 和名称；
- Game Data 基础索引；
- Adaptation 的 Unit/Material/Texture 读取能力。

### 输出内容

每个 Item 建议保存：

- Item 名称、分类、archive hex；
- 直接资源和依赖资源；
- Unit、Composite、Bone、Mesh、Material、Texture 的 AssetKey；
- 每个资源的来源 archive；
- 引用方向；
- 解析状态；
- 缺失依赖；
- `SourceFingerprint`。

必须区分：

```text
DirectAssets       Item 直接拥有/定位到的资源
DependencyAssets   由 Unit/Material 引用的资源
SharedCandidates   可能被其他 Item 共享的资源
```

### 读取策略

1. 根据 archivehashes 找到 Item 对应 archive；
2. 从基础索引确定候选 Unit；
3. 读取 Unit 的结构和依赖；
4. 解析 Composite/Bone；
5. 提取 Material 引用；
6. 解析 Material 的 Texture 引用；
7. 记录完整闭包或缺失 issue；
8. 写入 Item 关系表。

如果一个 Item 不能唯一确定 Unit，不允许静默猜测，应保存候选列表和歧义 issue。

---

## 8. 第四阶段：部件复用检测

### 目标

发现多个 Item 是否共享实际资源，并为 Mod 替换完整性检查提供依据。

### 复用级别

```text
ExactUnitReuse       直接共享同一 Unit
CompositeReuse       共享 Composite Unit
MeshReuse            共享可识别 mesh/部件
MaterialReuse        共享 Material
TextureOnlyReuse     仅共享 Texture
```

建议输出：

- `GroupId`；
- Item 成员；
- 共享 AssetKey；
- 复用级别；
- 置信度；
- 解释文本；
- 发现该关系的 source fingerprint。

不要把所有共享 Texture 的 Item 都视为同一个模型复用组。UI 风险提示建议：

- 共享 Unit：高风险；
- 共享 Composite/Mesh：中风险；
- 共享 Material：低风险或材质影响提示；
- 仅共享 Texture：信息提示。

第一批重点验证：

- Armor；
- Helmet；
- Cape；
- Weapon。

后续根据实际索引结果再扩展到 Vehicle、NPC、Automaton、Terminid、Backpack 等类型。

---

## 9. 第五阶段：Patch group 分析迁移

### 输入

Core 先根据已完成的文件级索引提供：

```text
PatchGroupInput
  ├─ ArchiveHex16
  ├─ PatchIndex
  ├─ BasePatchPath
  ├─ StreamPath
  ├─ GpuResourcesPath
  ├─ ContentHash
  └─ 明确选定的相关材料 patch
```

### Adaptation 负责

1. 扫描 patch TOC；
2. 收集 AssetKey 和类型计数；
3. 读取 Unit；
4. 解析 Composite/Bone；
5. 提取 Material/Texture 引用；
6. 执行依赖闭包检查；
7. 返回结构事实和 issue；
8. 对分离式 patch 保留模型/材料关系，不盲目合并全部候选。

### Core 负责

1. 将 Adaptation AssetKey 映射到 metadata；
2. 生成用户可见标签；
3. 按 patch group 保存 summary；
4. 聚合到 Mod 级 summary；
5. 使用已生成的 group facts 进行兼容性分析；
6. 按 group fingerprint 缓存结果。

### 迁移策略

```text
新 Adaptation 链路
        ↓ 验证通过
Manager 改用新链路
        ↓
Core 旧 analyzer 仅保留兼容调用
        ↓
删除 Core 重复 patch 操作
```

兼容调用不得被新功能依赖；后续应将 Core 旧 `PatchTocScanner`、`PatchEntryPayloadReader`、`AssetKeySetProvider` 以及直接读取 patch payload 的 analyzer/服务逐步替换为 Adaptation contract，再删除重复实现。

---

## 10. 第六阶段：Mod 替换完整性提示

当 Mod 修改某个 AssetKey 时：

1. 查询 Game Data Item 关系表；
2. 查询对应复用组；
3. 比较 Mod patch group 覆盖的 AssetKey；
4. 判断是否只覆盖了共享组的一部分；
5. 生成风险等级和说明。

示例提示逻辑：

```text
修改了共享 Unit：提示可能同时影响多个 Item。
只覆盖共享组部分资源：提示可能出现模型/材质不一致。
完整覆盖复用组：提示已覆盖关联资源，但仍需兼容性验证。
```

这只是分析和提示，不自动扩展输出范围。

---

## 11. 暂不实现的内容

以下内容不进入第一轮 Game Data/Patch 分析：

- 自动 patch 重建；
- 自动 Material/Texture 传播；
- 自动选择未知目标 archive；
- 把旧 Unit 原样复制到新 patch；
- 以“能够回读”证明游戏兼容；
- 仅依靠 `PatchMaterialDetector` 判定 patch 完整性；
- 为所有 Item 自动推导业务语义；
- 直接把所有共享资源合并为一个复用组；
- “应用到整个护甲组”的批量重建功能。

这些功能必须在索引、关系图和真实样本验证完成后单独设计。

---

## 12. 推荐实施顺序

### P0：契约和夹具

- 定义 Adaptation 只读分析 DTO；
- 确认 patch group 输入边界；
- 补齐缺失/重复/越界测试。

### P1：Game Data 基础索引

- 复用现有 Game Data package resolver 和 TOC scanner；
- 生成 archive/AssetKey 定位索引；
- 加入 fingerprint、schema 和增量重建；
- 先验证 `CW-22 Kodiak` 等已知 archive。

### P2：Item 关系表

- 建立 Item → Unit/Material/Texture graph；
- 记录直接资源、依赖资源和问题；
- 接入现有 `archivehashes.json` 元数据。

### P3：复用检测

- 优先实现 ExactUnitReuse、CompositeReuse；
- 再验证 MeshReuse、MaterialReuse；
- 生成分级风险提示。

### P4：Patch group Adaptation 分析

- 新分析器只读 patch group；
- 复用现有 scanner/reader/resolver；
- Core 做语义投影和 Mod 聚合；
- 旧 Core analyzer 保留 fallback。

### P5：Manager 接入

- Manager 调用索引构建服务；
- 展示脏状态、构建进度和问题；
- 使用 group summary 和 reuse group 提示；
- 不让 UI 直接依赖 Adaptation 内部模型。
- 可使用 debug 版 Mod 目录进行临时工具验证或 Manager 人工冒烟测试：
        `E:\Data\source\repos\WpfApp1\HD2ModManager\bin\Debug\net10.0-windows\mods`；
- 首轮只接入只读分析、缓存状态和问题展示，不接入自动修复或批量重建。

#### P5-a：游戏资产状态查看（当前优先级）

Manager 设置页的“游戏 data 文件夹”卡片可增加只读入口，建议流程如下：

```text
设置页：游戏 data 文件夹
        ↓ 点击“查看游戏资产状态”
只读任务窗口（复用状态页任务窗口外壳）
        ↓
可复制文本的信息卡片
        ├─ GameData 路径和扫描时间
        ├─ archive/TOC 总数与扫描成功/失败数量
        ├─ archivehashes.json 已映射 hash
        ├─ 未映射 hash
        ├─ 已识别的 Armor/Helmet 等分类或名称
        └─ 解析问题和缺失项
```

该窗口只消费 Core 投影后的 DTO/文本模型，不能让 XAML 直接依赖 Adaptation 内部解析类型。扫描结果应支持复制；刷新、取消、失败和使用缓存结果等状态应沿用现有任务窗口语义。

#### P5-b：Mod 详情中的 Patch 内容

Mod 详情只展示 patch 实际扫描到的内容，例如 patch 文件组、AssetKey、资源类型计数、Unit/Material/Texture 读取结果、缺失依赖和结构化 issue。没有可靠关系来源时显示“未能确认映射”，不得根据 archive 名称或文件名静默推导 Item。

### P6：清理重复实现

该阶段暂缓，待本轮分析、Manager 接入和真实样本验证完成后再重新评估。仅当满足以下条件后执行：

- 新链路通过 Adaptation 单元测试；
- 真实 Game Data 验证通过；
- 真实内嵌/分离 patch 验证通过；
- Manager 已完成依赖迁移；
- Core fallback 至少经过一个版本周期验证；
- 已确认没有其他 Core 服务依赖旧 patch 实现。

然后再删除 Core 中重复的 patch scanner、payload reader、Unit/Material 操作，并保留必要的 Core 适配器和文件级索引能力。

### 当前落地进展补充

- 本阶段 Manager 的目标是委托 Adaptation 生成并缓存两类只读事实：GameData 资产索引，以及单个 Mod 的 patch 资产信息；
- GameData 资产索引服务于设置页查看原版 GameData 的 archive、Item、Unit、Material、Texture 等关系，也服务于后续派生缓存和 patch 资产对比，避免重复解析 GameData；
- Mod patch 资产缓存服务于 Mod 详情页展示实际包含的 patch 和 AssetKey，并与 GameData 索引对比生成可确认的资产标签；同一配置文件内的 Mod 也可基于这些事实检测覆盖关系；
- Manager 不再提供全库 `Adaptation` aggregate 分析按钮；该入口属于偏离当前目标的额外功能，已移除。底层 Adaptation/Core 解析、索引和缓存工具保留，供上述流程复用；
- 现有 `LibraryDerivedDataService` 和 Core 的资产分析/索引能力继续保留，直到新的单 Mod patch 事实缓存完成替换并经过验证；
- Core 测试已通过 `157/157`，Adaptation 测试已通过 `59/59`；
- 当前服务输入仍要求上层提供已经确认的 `PatchGroupInput` 和 `GameItemInput`，真实目录到语义输入的精确映射及 Manager 接入留在下一步；
- Patch group 路径映射已前移到 Core `PatchGroupInputFactory`：根据 fingerprint scanner 返回的文件名自动组装 `.patch`、`.stream` 和 `.gpu_resources` 路径；
- 节点分析服务现在只要求上层提供 `GameItemInput`，不再要求手工传入 patch 路径；
- GameData archive 到 Item 的业务语义映射仍不自动猜测，必须由已确认的 metadata/关系来源提供；
- 已新增 Core `IGameDataArchiveIndexProvider`/`GameDataArchiveIndexProvider`：通过 `IGameDataLocator` 定位当前 GameData，读取 metadata catalog，使用 Adaptation `GameDataArchiveIndexer` 构造只读 archive TOC index，并将其注入 `AdaptationAnalysisInput.GameDataIndex`；
- provider 在 GameData 目录不存在时返回 null，在存在时以 resolver 实际枚举到的 package names 为准，不从 `archivehashes.json` 盲目制造包路径；
- 已对 Manager debug mods 目录做只读样本扫描：3 个 mod 目录均包含同一组完整文件 `9ba626afa44a3aa3.patch_0`、`.stream`、`.gpu_resources`；
- 已确认 debug 版 Manager `settings.json` 配置的真实 GameData 路径为 `D:\SteamLibrary\steamapps\common\Helldivers 2\data`；目录统计为 312 个文件、52 个 package-like 文件和 78 个 patch base；
- 已完成真实 GameData archive TOC smoke：直接读取目录中的 30 个基础 archive，得到 30 个 archive、20,700 个 TOC entries、0 个 issues；样本均为 slim entry offset。为避免普通 TOC 索引无意义地提前解压完整 bundled 数据库，resolver 现优先枚举目录中的直接 archive，仅在没有直接 archive 时才初始化 bundled database fallback；
- 当前构建仍有既有的 `SharpCompress` 安全警告和 Windows Registry 平台兼容性警告，不属于本次改动引入的问题。

---

## 13. 验收标准

### Game Data

- 未变化时启动不重复全量扫描；
- 变化 archive 可增量重建；
- parser/schema 改变可触发全量重建；
- 索引失败不会破坏已有有效缓存；
- 查询可以返回资源来源和问题。

### Item

- 能查询 Item 对应 archive；
- 能返回 Unit、Composite、Material、Texture 关系；
- 缺失闭包有明确原因；
- 不能唯一确定时返回歧义，不静默猜测。

### 复用组

- 至少能识别直接共享 Unit；
- 能区分 Unit 复用和仅 Material/Texture 复用；
- 结果包含共享资源和解释；
- Mod 覆盖不完整时能生成提示。

### Patch

- patch 内部读取来自 Adaptation；
- Core 只负责文件组、索引、映射、聚合和缓存；
- 读分析不调用 writer/reconstructor；
- 旧 Core 实现仍可作为 fallback；
- 所有真实失败保留为 issue，不静默丢弃。

---

## 14. 当前下一步

当前已完成 P0-P4 第一版、Core 快照投影、独立 Adaptation 缓存基础设施、确定性 Patch/GameData fingerprint 请求构造，以及 Core-owned Mod 级聚合；旧 Core analyzer 仍保留为 fallback，Core 冗余暂不清理，Manager 只读接入已排入后续队列。缓存协调器已经具备命中、失效重建和 best-effort 持久化行为。

下一阶段继续完善 Core 边界，并优先落实 Manager 资产状态和 Mod 详情只读展示，不立即进入 patch 写出链路：

1. 将 Patch fingerprint 与 GameData index fingerprint 接入协调器调用方；
2. 增加 Mod 级 Adaptation summary 聚合，不让 UI 依赖 Adaptation 内部 DTO；
3. 继续补充真实 archive/patch 样本验证并修正临时 archive 映射；
4. 为设置页游戏 data 文件夹卡片规划并实现只读资产状态入口和任务窗口；
5. 将 Mod 详情的 patch 提取结果接入展示，严格区分已提取事实和未确认映射；
6. 使用上述 debug 版 Mod 目录进行临时工具验证，必要时协调 Manager 人工冒烟测试；
7. 将 Mesh transfer、material closure、target-shell 重建和游戏内兼容性作为独立后续阶段；
8. 在新链路稳定、真实样本和 Manager 验证完成后，再评估是否具备清理 Core fallback 和冗余实现的条件。

在 P0/P1 验证完成前，不进行大范围 domain 重构，也不删除 Core 旧 patch 实现。
