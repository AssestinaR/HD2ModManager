# 内部 Mod、配置与自动部署实施计划

本文档把 [说明.md](说明.md) 第七章转化为可执行实施步骤。实施时以 `说明.md` 为产品与架构定义，以本文档为阶段顺序、验收门槛和回归清单。

## 一、实施原则

1. 按“Core 事实与业务能力 → Manager 投影与交互”的顺序实施，不先在 UI 中拼接临时结论。
2. 每个阶段都必须独立编译、通过相关测试并形成可回退提交；不得一次性同时改完 Profile、部署、事实层和全部 UI。
3. 内部 Mod 始终保持平铺：一个 Mod 一个目录，只扫描目录顶层 Patch 文件；不引入内部 manifest、选项树或作者互斥关系。
4. `bak/`、导入暂存目录和修复暂存目录不进入活动扫描、部署或冲突事实。
5. 配置成员身份就是启用状态；删除 `ProfileEntry.Enabled` 后不保留双重语义。
6. “正在编辑的配置”和“唯一活动配置”必须拆开：前者是 Manager 页面选择状态，后者是 Core 持久化业务状态。
7. 配置预期图和 Data 实际部署图是两份事实。预计成功不得冒充实际部署成功。
8. AssetKey 严格竞争按 `TypeID + FileID` 判断；archive 只作为目标映射和普通用户提示维度，不能参与严格竞争键。
9. 自动部署缓冲和部署通道属于业务流程；Manager 的后台任务服务只投影任务状态，不成为调度权威源。
10. 新增 C# 文件须在 using 指令下方添加简短用途注释。

## 二、必须先固定的模型决策

### 1. Profile 权威状态

Core 的持久化库状态需要包含：

- `Profiles`：全部配置。
- `ActiveProfileId`：零或一个活动配置。
- `ProfileEntry`：仅包含 `NodeId`、`LoadOrder`、`AddedUtc`。

Manager 单独维护 `SelectedProfileId`，仅表示当前页面正在编辑哪个配置。切换编辑对象不等于启用配置，也不触发部署。

当前测试阶段不迁移旧 `Enabled` 字段。库快照版本升级后，旧测试数据允许删除重建；读取到不受支持版本时应给出明确诊断，不可静默猜测。

### 2. Patch 组稳定身份

新增稳定 Patch 组身份，至少由以下字段组成：

```text
ModNodeId + SourceArchiveHex + SourcePatchIndex
```

同组的 base、stream、gpu_resources 是文件组成，不是三个独立 Patch 组。部署后的 `TargetPatchIndex` 是运行结果，不能参与源 Patch 组身份。

### 3. 四类事实及 generation

统一事实服务至少发布四类不可变快照：

- `ModContentFacts`：Mod、Patch 组、文件、AssetKey、解析问题、内容 generation。
- `GameDataMappingFacts`：AssetKey 对应的全部目标 archive、对象名、类型及映射 generation。
- `ProfileOverrideGraph`：指定 Profile revision 的预计顺序、竞争链、winner、覆盖比例和问题。
- `DeployedOverrideGraph`：Data revision 下的实际 Patch 组、来源、AssetKey winner、竞争和异常。

缓存键必须显式包含相应 generation，禁止只凭 UI 刷新时机判断缓存有效性。

### 4. 激活状态

把 `activation-state.json` 的私有序列化 record 提升为 Core 公共领域模型与存储接口。状态至少记录：

- schema version、ProfileId、Profile revision、部署时间。
- 源 Patch 组身份。
- 每个源文件与目标文件。
- 源编号与目标编号。
- 部署方式。
- 文件长度和用于验证复制结果的内容指纹。
- 部署问题摘要或完成标记。

清理范围仍由顶层 Patch 文件名规则决定，不能只依赖激活状态。

## 三、阶段实施步骤

### 当前进度（2026-07-14）

- 启动恢复修复：已完成。旧版 Profile 列表会自动升级为 v2 状态文件；无法解析的 Profile 文件会先备份为 `profiles.json.invalid-时间戳.bak`，再重建空状态，避免启动崩溃。
- 阶段 0：已完成。已固定顶层 Patch 扫描、`bak/` 排除、连续重编号和安全清理等基线，并完成全量测试回归。
- 阶段 1：已完成。已移除 `ProfileEntry.Enabled`，加入 Profile revision 和 Core 权威 `ActiveProfileId`，拆分 Manager 的编辑配置与活动配置，并移除逐项启用/禁用 UI。
- 阶段 2：已完成。已建立稳定 Patch 组身份、sidecar 文件事实、AssetKey 集、结构化问题和按 Mod 计算的内容 generation；库派生数据、资产摘要和部署索引已接入同一内容事实服务。
- 阶段 3：已完成。已建立保留全部目标 archive 的 Game Data 映射事实、索引/元数据 generation、严格 AssetKey 预计竞争图、独立 archive 潜在重叠和逐 Mod 覆盖状态；Mod 详情预计状态已接入新图。
- 阶段 4：已完成。已发布公共 Activation State/存储接口，部署执行器具备源文件预检、不可中断提交区、完整文件验证、原子状态发布、失败确定性清理和幂等停用；部署覆盖解析已改用公共状态模型。
- 阶段 5：已完成。已建立实际 Data 扫描与 Activation State 对账、来源/哈希/链接验证、目标序号 winner 图和部署 generation；旧部署覆盖解析器已收敛为新图的兼容投影。
- 阶段 6A：已完成原始缓冲协调器，并在端到端测试后重新定义为无等待的即时串行部署；保留 revision 合并、最新活动配置重读、停用安全清理和 Manager 后台任务投影。
- 阶段 7：已完成。已建立普通用户状态投影，配置页和库页不再依赖技术身份展示状态；双槽 Mod 库会排除正在编辑配置的成员并在配置变化后同步刷新。
- 阶段 8：已完成。普通 Mod 详情已收敛为用户信息、资产标签和状态卡；高级 Patch/AssetKey 诊断独立成窗口；Game Data archive 窗口已改为消费 Core 浏览器查询快照，不再在 Code-behind 聚合覆盖事实。
- 阶段 9：已完成。已删除旧同步 ActivationService、旧 ModAssetOverrideAnalyzer 和旧部署 overlay 兼容链路；全量 Core 回归、普通/高级页面接线和隔离输出 Manager 构建均已通过。
- 端到端修正轮：已落地推荐 Mod 库目录 `steamapps\common\HD2ModManager\mods`（失败回退便携目录，已有便携库不静默迁移）、统一外部 Mod 库路径、硬链接/符号链接自动能力探测、无复制部署、无等待即时串行协调器、符号链接差异化验证、主页部署能力与三个修复入口，以及 Profile 变化后的可取消状态重算。推荐目录迁移采用复制、逐文件 SHA-256 校验、保留旧库并重启载入。
- 派生状态修正轮：已建立长期存活的 `DerivedStateCoordinator`，统一缓存 Mod 内容事实、活动配置预计图和实际部署图；普通状态改为纯内存投影，库页、配置页和详情页不再各自重新扫描全库。内容、活动 Profile、部署完成和映射变化按 generation/dirty 范围后台刷新；普通详情删除同步等待、全库状态扫描、预计图重建和冲突扫描，只读取共享快照，高级技术计算继续按需进入高级详情。
- 后续人工验收重点：活动配置变更、切换、停用、Data 篡改、普通/高级页面状态一致性，以及运行中 Manager 退出后执行标准输出目录构建。

## 阶段 0：建立基线与特征测试

### 目标

在破坏性模型调整前固定当前有效行为，并把已知错误语义写成失败测试。

### 工作项

1. 记录 Core、Manager 当前构建结果和 Core 全量测试结果。
2. 为以下行为补充特征测试：
   - Patch 文件索引只扫描 Mod 目录顶层。
   - `bak/` 不参与扫描。
   - Apply 计划按 Profile 顺序连续重编号。
   - Apply 执行前清理 Data 顶层可识别 Patch 文件，但不删除普通文件。
   - source Patch index 与 target Patch index 可以不同。
3. 为以下目标语义先写失败测试：
   - 严格竞争忽略 source archive，仅按 `TypeID + FileID` 聚合。
   - 编辑非活动配置不触发部署。
   - 活动配置变更会合并为唯一缓冲任务。
   - 停用配置最终清空 Patch 和 activation state。

### 验收门槛

- 现有成功测试继续通过。
- 新的目标语义测试能够稳定暴露当前缺口，而不是依赖时间竞态。

## 阶段 1：收敛 Profile 模型与活动配置权威源

### 目标

先消除 `Enabled` 和“编辑配置等于活动配置”的根本歧义。

### Core 工作项

1. 从 `ProfileEntry` 删除 `Enabled` 及兼容构造函数。
2. 从 `IModLibraryManager` 和 `ModLibraryManager` 删除 `SetProfileEntryEnabledAsync()`。
3. 给库权威状态加入 `ActiveProfileId`，并提供原子操作：
   - 设置活动配置。
   - 停用活动配置。
   - 删除活动配置时将活动配置置空，不自动选择另一个配置。
4. 增加 Profile revision。成员、顺序、活动 Mod 内容事实变化时可得到新的 revision；仅重命名不应改变部署内容 revision。
5. `ApplyPlanner`、预计覆盖分析器和所有调用点改为使用 Profile 的全部条目。
6. 更新 JSON 存储版本和明确的不兼容版本错误。

### Manager 工作项

1. 将 `ProfileService` 中的 `ActiveKey` 拆为：
   - `SelectedProfileId/SelectedProfile`：页面编辑对象。
   - `ActiveProfileId/ActiveProfile`：Core 权威活动对象。
2. 新建配置后只选中编辑，不隐式启用。
3. 删除、重命名、切换页面选择均不得意外切换活动配置。
4. 暂时保留手动部署入口用于阶段性验证，但不得再读取 `Enabled`。

### 测试

- Profile 添加、移除、排序后顺序归一化。
- 设置活动配置唯一且持久化。
- 删除活动配置后活动状态为空。
- 非活动配置编辑只改变自身 revision。
- Apply 计划包含 Profile 全部成员。

### 验收门槛

- 全仓库不存在 `ProfileEntry.Enabled`、`SetProfileEntryEnabledAsync` 和按 Enabled 过滤的业务代码。
- 选择配置与活动配置可在 UI 状态中独立变化。

## 阶段 2：建立统一 Mod 内容事实

### 目标

用一个 Core 服务统一回答“一个平铺 Mod 实际包含哪些 Patch 组和 AssetKey”。

### 工作项

1. 定义 Patch 组、组文件、AssetKey、解析问题和内容指纹领域模型。
2. 合并现有 `PatchFileIndexBuilder`、Patch group analysis、`ModAssetAnalyzer` 的重复职责：
   - 文件枚举与文件名解析只保留一条入口。
   - TOC/二进制解析委托 Adaptation。
   - Core 负责把解析结果关联到 `ModNodeId` 和稳定 Patch 组身份。
3. 内容 fingerprint 至少包含相对路径、长度、最后写入时间；需要强验证时按需计算哈希。
4. 缓存按单个 Mod 增量失效，不因一个 Mod 变化重扫全部库。
5. 明确忽略项：`bak/`、子目录、临时文件、非 Patch 文件和不完整 sidecar。
6. 保留问题而非吞掉异常：缺 base、TOC 损坏、文件缺失、重复组均生成结构化诊断。

### 测试

- 多组、多 sidecar、稀疏 source index 正确归组。
- 同名或同编号但不同 Mod 的 Patch 组身份不冲突。
- 修改单个文件只使对应 Mod generation 变化。
- 损坏 Patch 不阻止其他 Mod 产生事实。

### 验收门槛

- 部署计划、Mod 详情和冲突分析都消费同一内容事实服务。
- Manager 不再自行枚举 Patch 文件。

## 阶段 3：建立 Game Data 映射与配置预计覆盖图

### 目标

在不接触 Data 实际部署状态的前提下，可靠计算指定配置“应该如何覆盖”。

### 工作项

1. 将内容事实中的每个 AssetKey 映射到 SQLite Game Data 索引的全部目标 archive、对象名和类型。
2. 映射结果保留零、一或多个目标，不强制归属单一 archive。
3. 重写预计覆盖分析：
   - 严格键为 `AssetKey(TypeId, FileId)`。
   - 竞争链按 Profile load order 和 Patch 组内部顺序稳定排序。
   - 最后有效覆盖者是预计 winner。
   - archive 交集单独形成普通用户潜在重叠提示。
4. 输出每个 Mod 的预计状态数据：总 AssetKey、胜出数、被覆盖数、是否全部失效、竞争对象和解析问题。
5. 缓存键包含 Profile revision、相关 Mod 内容 generation、Game Data 索引 generation 和资产元数据 generation。

### 测试

- 不同 source archive 的同一 AssetKey 仍形成竞争。
- 一个 AssetKey 映射多个目标 archive 时全部保留。
- 顺序调整后 winner 稳定改变。
- archive 重叠但 AssetKey 不同，只产生潜在重叠，不产生严格竞争。
- 空 Mod、损坏 Mod、缺少映射都有明确状态。

### 验收门槛

- Core 能在无 Manager、无 Data 部署的测试中完整生成预计覆盖图。
- 普通状态所需数据不必读取 UI 集合或 activation state。

## 阶段 4：公共 Activation State 与安全部署执行器

### 目标

让实际部署可追溯、可验证，并定义取消与失败的安全边界。

### 工作项

1. 新增 `IActivationStateStore`，统一负责原子读取、写入和删除状态文件。
2. `ApplyExecutor` 使用公共 activation 模型；删除其私有序列化 record。
3. 部署分为明确阶段：
   - 预检：读取最新快照、验证源文件、生成计划。
   - 准备：创建目标部署准备数据，不破坏当前 Data。
   - 提交：进入不可中断安全区，清理旧 Patch 并完成完整新集写入。
   - 验证：回扫 Data、验证文件来源和 Patch 组。
   - 发布：原子写入完成状态。
4. 取消只在安全检查点生效。提交阶段一旦开始，要么完成，要么执行确定性清理；不得留下一半旧配置加一半新配置。
5. 部署失败时写入结构化结果并删除不可信 activation state；Data 残留由实际图标记为异常。
6. 提供独立 `DeactivateAsync()`：等待当前提交安全点，删除所有可识别顶层 Patch 和 activation state。
7. 所有 Data 变更路径严格串行。

### 测试

- 状态文件可由写入端和读取端使用同一公共模型往返。
- 预检取消不改变 Data。
- 提交期间取消不会产生混合 Patch 集。
- 任意 sidecar 写入失败可检测且不会发布成功状态。
- 停用重复执行具有幂等性。
- 非 Patch 文件始终保留。

### 验收门槛

- `DeployedPatchOverlayResolver` 不再复制私有 activation JSON 结构。
- Apply 结果、activation state 和 Data 扫描三者可相互核验。

## 阶段 5：建立实际部署覆盖图

### 目标

统一回答“Data 现在实际有什么、来自哪里、谁真正胜出”。

### 工作项

1. 将现有临时 `DeployedPatchOverlayResolver` 收敛为 Core 公共实际事实服务。
2. 读取公共 activation state，同时独立扫描 Data 顶层 Patch；两者不一致时产生异常而不是丢弃一侧。
3. 对实际 base Patch 通过 Adaptation 扫描 AssetKey。
4. 按目标 Patch 序号计算实际严格竞争链和 winner。
5. 验证源/目标文件：链接目标、长度、内容指纹、sidecar 完整性和来源 Mod 是否仍存在。
6. 输出实际 deployment revision，供 Game Data、Mod 详情和状态页共同消费。

### 测试

- 复制、硬链接、符号链接三种方式均能验证。
- source/target 编号不同仍能正确追溯来源。
- activation state 缺失、损坏、过期和 Data 被外部修改都有明确诊断。
- 实际 winner 按 target index，而非 Profile 顺序或 source index 计算。

### 验收门槛

- Game Data 窗口不再直接实例化部署解析器或同步阻塞读取 Data。
- 实际部署事实可由 Core 单独测试。

## 阶段 6A：将缓冲协调器收敛为即时串行部署

### 目标

把活动配置变更立即合并为自动部署，不阻塞 UI；删除已被实测证明没有必要的 10 秒等待。

### Core 工作项

1. 保留单一串行部署通道和可观察任务快照。
2. 删除倒计时、延迟器、缓冲取消令牌和 `Buffering` 阶段。
3. 每次活动配置相关变更只递增目标 revision；部署运行中只记录 dirty，当前部署完成后重新读取最新快照并至多追加一次部署。
4. 任务取得通道后立即读取 Core 权威库快照、活动 Profile 和最新内容事实；禁止使用通知时捕获的旧 Profile 对象。
6. 非活动配置修改不通知协调器。
7. 切换活动配置时持久化新活动配置并立即请求部署。
8. 停用命令等待提交安全点，执行 `DeactivateAsync()`，最后发布无活动配置状态。
9. 应用退出时若部署已进入提交安全区则等待完成或确定性清理。

### Manager 工作项

1. 将协调器投影为普通 Deployment、Deactivate 任务，删除 DeploymentBuffer。
2. 展示目标 revision、当前阶段和错误，不在 Manager 自己另开部署线程。
3. 通知只反映 Core 最终结果。

### 确定性测试

确定性测试不再依赖时钟：

- 空闲时变更立即产生部署。
- 部署期间连续变更只追加一次最新状态部署。
- 非活动配置变更零部署。
- 切换活动配置只部署新配置完整集。
- 停用最终无 Patch、无 activation state、无活动配置。

### 验收门槛

- 自动部署端到端可稳定测试，无 `Thread.Sleep` 和时间竞态。
- 任意时刻最多一个 Data 写任务。

## 阶段 7：重构配置页、双槽库与普通状态

### 目标

让 Manager UI 完全符合新语义，并删除旧手动启用模型。

### 工作项

1. 配置页移除逐项“启用/禁用”按钮、命令、批量动作、`Enabled` 属性和相关文案。
2. 增加明确的“设为活动配置”和“停用当前配置”操作；选中编辑配置时清楚标记其是否活动。
3. 配置列表中的所有 Mod 都显示为该配置成员，只提供移除和排序。
4. 双槽模式右侧库过滤为：全部内部 Mod减去左侧正在编辑配置的成员；单独库页仍显示全部 Mod。
5. 空状态改为“所有 Mod 都已加入此配置”。
6. 移除手动“应用配置”主按钮和 `ActivationService` 的同步入口；必要的诊断性“立即重试部署”只能调用协调器，不能绕过串行通道。
7. 删除所有 `.GetAwaiter().GetResult()` 的部署/UI 数据读取路径。
8. 普通状态由预计图与实际图投影为：仅存储、当前配置、已启用、局部覆盖、全部失效、损坏、过时、未知。

### 测试与人工验收

- 编辑非活动配置，任务列表不新增缓冲。
- 激活配置后出现一个缓冲任务并倒计时。
- 左右双槽不会重复显示同一 Mod。
- 移除/添加/排序活动配置成员会合并缓冲任务。
- UI 全程可操作，无同步磁盘或 TOC 扫描阻塞。

### 验收门槛

- Manager 中不存在逐项启用状态和直接部署文件业务。
- 手动部署入口不会绕开协调器。

## 阶段 8：普通 Mod 详情、高级详情与 Game Data 接线

### 目标

最后消费已稳定的 Core 事实，替换现有临时 UI 聚合。

### 普通详情

仅显示名称、图像、备注、资产标签卡、当前状态卡和高级详情入口。状态由统一投影服务生成，不展示 AssetKey、archive hash 或 Patch 序号。

### 高级详情

展示：

- 源 Patch 组和 sidecar 文件。
- AssetKey 与全部 Game Data 目标。
- 配置预计竞争链和 winner。
- 实际目标 Patch 序号、竞争链和 winner。
- 内容、映射、部署问题。
- 受控修复、替换和回滚入口。

### Game Data 窗口

1. 删除 Code-behind 中的 `BuildOverlays()` 业务聚合。
2. 通过 Core 查询服务一次获取 archive 级投影和 AssetKey 级详情。
3. archive 级展示潜在重叠、预计影响和实际生效 Mod。
4. AssetKey 详情展示完整预计/实际竞争链。
5. 所有查询异步、可取消；窗口关闭后不得继续更新 UI。

### 验收门槛

- 普通页面无技术噪音。
- 高级详情和 Game Data 对同一 AssetKey 给出一致 winner。
- UI 不再依赖临时 `DeployedPatchOverlayResolver`。

## 阶段 9：删除旧链路并完成端到端回归

### 删除项

- `ProfileEntry.Enabled` 兼容代码。
- `SetProfileEntryEnabledAsync()` 及 Manager 对应命令。
- 手动同步 `ActivationService` 或其绕过协调器的路径。
- ApplyExecutor 私有 activation state record。
- Game Data 窗口 Code-behind 覆盖聚合。
- 仅为旧语义服务的缓存、状态字段和文案。

### 端到端场景

1. 创建两个配置并分别编辑，确认只有活动配置触发部署。
2. 快速添加、移除、排序多个 Mod，确认只部署最新 revision。
3. 部署中继续修改，确认当前提交完成后只追加一次最新状态部署。
4. 切换配置，确认旧 Patch 被部署任务统一清理，新配置完整生效。
5. 停用配置，确认 Data 中所有可识别 Patch 和 activation state 被删除。
6. 修改活动 Mod 顶层 Patch，确认内容事实失效并自动重部署。
7. 修改 `bak/` 内容，确认不刷新活动内容 generation、不触发部署。
8. 人工篡改 Data，确认实际图报告异常，预计图不被污染。
9. 验证普通详情、高级详情和 Game Data 三处状态一致。
10. 在索引、导入和分析并行运行时验证部署通道仍严格串行且 UI 不冻结。

### 最终门槛

- Core 全量测试通过。
- Manager 构建无错误、无新增警告。
- 关键即时部署与 dirty 合并测试可重复运行。
- 所有 Data 写入只经过部署协调器与执行器。
- 更新 [说明.md](说明.md)“当前阶段”，把已完成设计改为已落地能力。

## 四、建议提交批次

为降低回归风险，建议按以下提交边界落地：

1. `test: lock profile and deployment baseline`
2. `refactor: remove profile entry enabled state`
3. `feat: persist authoritative active profile and revision`
4. `feat: unify flat mod patch content facts`
5. `feat: build game data mapping and expected override graph`
6. `refactor: publish activation state contract and safe executor`
7. `feat: build deployed override graph`
8. `feat: add buffered serialized deployment coordinator`
9. `refactor: align profile and split library UI`
10. `feat: add simple and advanced mod status projections`
11. `refactor: replace game data window overlay aggregation`
12. `test: complete deployment workflow regression suite`

## 五、落地顺序结论

正式落地必须从阶段 0 开始，阶段 1 至阶段 6 是主链路，不可跳过。阶段 7 和阶段 8 只能在 Core 已能稳定提供预计图、实际图与部署任务快照后接入。

第一轮实际编码范围建议限定为“阶段 0 + 阶段 1”：先固定回归测试、移除 `Enabled`、拆分编辑配置与活动配置、建立 Core 权威 `ActiveProfileId`/revision，并继续暂用现有手动 Apply 验证新 Profile 模型。该轮稳定后再进入统一内容事实，避免同时破坏持久化、部署和 UI。
