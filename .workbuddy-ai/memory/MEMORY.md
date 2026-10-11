# 项目长期记忆：HD2ModManager

## 项目定位
- 仓库 `WpfApp1` 是《绝地潜兵 2》(Helldivers 2) Mod 管理器与补丁适配工具集，GPLv3。
- **`ManagedMain` 已过时、不再使用，仅作留档**（用户明确说明）。
- **当前主目标项目是 hd2mod 系列**：`HD2ModManager`(WPF GUI)、`HD2ModCli`(无头 CLI `hd2mod.exe`)、
  `HD2ModCore`(共享核心)、`HD2ModAdaptation`、`Decryptor`、`HD2GameItemAssets` 及各 `*.Tests`。
- `HD2RuntimeArmorOverride` 是护甲运行时映射的 Lua mod 源码仓（非 csproj），
  其 lua 文件被 `HD2ModCore.csproj` 以 EmbeddedResource 内嵌。

## 关键架构
- 护甲映射三方协作：CLI `armor-registry` 命令组 / GUI 护甲映射页 / 游戏内 Lua 运行时，
  通过外置协议目录 `%LOCALAPPDATA%/CowboyBingus/Helldivers2/ArmorRegistry` 与同一把 `.manager.lock` 协作。
- CLI 的 "Agent 能力" = 技能导出体系：`CliSkillCatalog` 定义 27 个可安装能力 Skill（内嵌 SKILL.md），
  `capabilities --json` / `agent skills list|export`。安全契约：`inspect`/`plan`/`validate`/`apply --confirm`，无 `--dry-run`。
- 信息读取统一走 `ModInformationCenter`（磁盘 JSON 缓存 + generation + `ModInformationCacheScope` + `RequireFresh` + 请求合并）。
- **统一身份（阶段 1，2026-10-10 完成）**：全仓事实用同一把结构化键命名——
  `FactKey(FactSubject Subject, ModInformationPropertyKind Kind)`，缓存身份为 `FactRevisionKey(FactKey, Revision)`。
  `ModInformationFactRegistry` 是 **25** 个 `ModInformationPropertyKind` 的**单一真相**（主体种类 / 缓存范围 / 生产者 / 静态依赖），
  读取器的"是否允许持久化"校验由它驱动，不再有硬编码 kind 清单。
  （阶段 1 时为 21；阶段 2 新增 `GameDataIndex` / `AssetMetadata` → 23；§22 新增 `DeployedGraph` → 24；§23 新增 `ArmorCoverage` → 25。）
  `IModInformationReader.RequestAsync<T>(FactKey, request, FactReadOperation<T>, ct)` 是**唯一读取入口**，
  5 个具名读取方法（`ReadPatchIndexAsync` / `ReadPatchPayloadAsync` / `ReadUnitAsync` / `ReadUnitSummaryAsync` / `ReadSourceUnitFactsAsync`）
  都是它的单语句薄包装；L3 的 7 个进行中任务字典键已由 `"{nodeId}|{kind}|{generation}"` 字符串改为 `FactRevisionKey`。
  设计文档 §15（设计）+ §17（实施记录与决策清单）。
- **显式依赖 DAG（阶段 2，2026-10-11 完成）**：21 条 `DependsOn` 边全部逐条读代码坐实（依据写在
  `ModInformationFactRegistry.cs` 各条目注释里），由 `ModInformationFactGraph` 物化为有向图
  （`Nodes` / `DirectDependencies` / `TransitiveDependencies` / `TopologicalOrder` / `IsAcyclic`）。
  `ModInformationFactRegistry.Validate()` 有 5 项检查（全部登记 / 计数一致 / 依赖边合法无自环 / **无环** /
  **空依赖 ⟺ 在 `SourceRoots` 里**），并在 `CoreServices.CreateModInformationCenter` 开头调用 → 启动期自检。
  设计文档 §18（D3 逐条考证）+ §19（实施记录）。
- **E1 收编 game-data 侧（2026-10-11 完成登记表那一半）**：`GameDataIndex` / `AssetMetadata` 作为
  **源事实**入表，`GameDataMapping.DependsOn = [GameDataIndex, AssetMetadata]`。
  枚举值**追加在末尾**（这些值随内存诊断对象出行，重编号会让两版日志对不上）。
  **页面那一半（让浏览器页/护甲页不再直调 `MapAsync`/`GetArchiveSummariesAsync`）未做**，留待单独拍板。
  设计文档 §20。
- **`GameDataFactCache`（进程级、有界、键=来源指纹）** —— 存在的理由：`CreateGameDataMappingFactsService(paths)`
  **每次调用都 new 实例**，6 个消费者各持一份，实例级缓存等于零共享。
  硬约束（用户提出）：**"缓存不得取代真实性"——重建必须仍能完整从资产文件与加密注册表重新取数。**
  落地方式是**结构性保证**：① 键全由来源指纹构成（来源一变键就变）② 不落盘 ③
  `RebuildNeverConsumesTheFactCache` 用**源码文本断言**钉住 `AssetArchiveIndexService.BuildOrRebuildAsync`
  从不提及缓存且仍调 `BuildAsync(` / `ComputeSourceFingerprintAsync(` ④ 双向测试（同源只算一次 / 改·删·换 selector 必真实重算）。
- **内嵌 SQLite 引擎已定点固定**：`HD2ModCore.csproj` 直接引用 `SQLitePCLRaw.bundle_e_sqlite3 2.1.12`。
  原因：`Microsoft.Data.Sqlite 8.0.13` 传递引入 2.1.6，其 `e_sqlite3.dll` 内嵌 **SQLite 3.41.2**，
  命中 GHSA-2m69-gcr7-jv3q（CVE-2025-6965，要求 ≥ 3.50.2）。2.1.12 → 3.53.3。
  **光升 `Microsoft.Data.Sqlite` 修不了**（10.0.0 仍依赖 2.1.11，仍受影响），必须 pin SQLitePCLRaw。
- **"按来源指纹键控的进程内记忆"是通用手法**：`SourceFingerprintMemo`（有界、整体清空、类型校验）
  + 每族事实一个持有者，**各拿独立预算**（`GameDataFactCache`=16、`DeployedGraphFactCache`=4）。
  共用一个字典会让体积大的一族把另一族挤出去，也让"命中记忆"类断言变得不确定。
  **必须做成 static**：这些服务（`CreateGameDataMappingFactsService` / `CreateDeployedOverrideGraphService`）
  **每次调用都 new 实例**，实例级缓存等于零共享。
- **部署侧事实**：`DeployedOverrideGraphService.BuildAsync` 已改为"先算来源指纹、命中即返回"，
  跳过对每个已部署文件与其源文件的**双份 SHA-256** 与每个 base 的 TOC 扫描（§6 阶段 3 缓存半边，设计文档 §21）。
  键 = 激活状态内容 + 已部署文件 (名字,长度,mtime) + **源文件 (存在性,长度,mtime)**。
  源文件必须在键里，否则"部署没动、源被改了"会命中陈旧结论。
  注意：`DerivedStateCoordinator` 里 `deployed` 是被**主动置空**的（页面打开时不扫 GameData），
  所以**生产路径上唯一调用者就是 `GameDataArchiveBrowserService.BuildAsync`**。
- **`ApplyExecutor` 只支持硬链接与符号链接**（`"Only hard-link and symbolic-link deployment are supported."`）。
  因此测试里用 `ApplyExecutor` 铺出来的部署，"目标"与"源"是**同一个文件**，改一个必然改另一个。
  要构造"目标与源分离"的场景，必须用 `DeploymentMethod.Copy` 手写激活状态。
- **无节点事实的形态（2026-10-11 已落地，设计文档 §22）**：中心现在有第四种主体 `FactSubjectKind.GameData`。
  要点：① 作用域（资产键集合、游戏目录）写进 **`FactSubject.GameData(scope)`**，不写进 `Revision`
  ——"哪一件事"归 Subject，"同一件事的输入指纹"归 Revision；② 三类事实**共用一本**
  `ConcurrentDictionary<FactRevisionKey, Lazy<Task<ModInformationResult<object>>>> _gameDataTasks`，
  取出时用 `Unwrap<T>` 还原类型（键里已含 Kind，不会撞）；③ 无节点事实**刻意不参与 `InvalidateNodeAsync`**
  ——它们的输入不含 Mod 目录，清掉只会白做；④ 三个生产者依赖**可选**，缺失时返回 `Unavailable` 而非抛异常。
- **`CacheScope` ≠ 作用域**：`ModInformationCacheScope`（None/Operation/Session/Persistent）是**生命周期阶梯**，
  不是主体维度。设计文档早期写的 `Scope=Deployment` 指的是**主体作用域**，代码里由 `FactSubject` 表达；
  登记表的 `CacheScope` 取 `Session`（部署图的记忆是进程内、按指纹键控、从不落盘）。
- **`IGameDataFactSource` 是投影与旁路服务之间的唯一缝**：`CenterGameDataFactSource`（走中心，Manager 用）
  与 `DirectGameDataFactSource`（直问旁路服务、**刻意不建/不刷任何中心缓存**，CLI 只读用）。
  `GameDataArchiveBrowserService` 两个构造重载只收 `IGameDataFactSource`，**不再出现任何旁路服务名**。
  失败语义：中心把失败表达成 `Data=null + Issues`，适配器把它**还原成 `InvalidOperationException`**
  ——静默成空结果会让 UI 显示错误结论，比报错更难查。
- **`ArmorCoverage` 是无节点形态上的第四条事实（2026-10-11，设计文档 §23）**：
  护甲页此前为"是否被 Unit 覆盖 + 覆盖者图标"两个字段**整包构建** GameData 浏览器，
  而那一步会**对每个 Mod 请求一次资产清单**——首开卡顿的根因。
  现在只请求 `RequestArmorCoverageAsync(游戏目录, 护甲归档集合, …)`，
  中心内部按 `GameDataIndex → DeployedGraph → GameDataMapping` 顺序自己取上游（因此与页面直接请求共用同一本合并账）。
  **喂给映射的键刻意只是"已部署且有归属"的资产键**，不是"全部 Mod 资产键"——后者正是要摆脱的那一步。
  两条铁律：① **图标不进事实**（否则"换图标"会触发覆盖重算，与 §7 护栏 3 相反；图标是库内查找，留在页面）；
  ② **"读不到" ≠ "没有覆盖"**——不在索引里的归档必须**缺席**（页面显示"未知"），索引未建必须 `Unavailable`，
  只有"索引里有、但没被部署覆盖"才给确定的 `false`。
- **"先证伪自己"的方法论在 §23 又抓到一次**：探针 D（移除赢家排序里的无主过滤）**没有失败**——
  因为 `Project` 里已有一句 `continue` 把无主链挡在外面，**两处守卫各自冗余**（单独删任一处用例都仍通过，
  两处同删才失败）。发现冗余本身有价值；同时暴露原用例的弱点（"null 组赢了"也表现为"没被覆盖"），
  于是改成让无主链资产**更多**、断言有主的那条**仍然赢**，两种情形才在断言上可分。
- **护甲页启动预热（2026-10-11，设计文档 §24）**：护甲页的 **VM / 数据 / 视图**各占一个空闲回合预热
  （`ContextIdle` 建 VM → 启动维护后 `WarmUpAsync()` 取数 → 视觉预热链 profile→library→**armor**）。
  三条硬要求，缺一条就是"静默错数据"：
  1. **单飞加载**：`InitializeAsync` 与 `WarmUpAsync` 共用 `catalogLoadTask ??=`，
     否则要么各读一遍，要么预热在跑时用户点开页面被 `RefreshAsync` 的 `if (IsBusy) return;` 挡回**空页面**。
  2. **取数闸门 `displayFactsReady`**：视觉预热会在窗口刚 `Loaded` 时就建出护甲页视图并触发 `OnLoaded`，
     那**早于**"库快照已加载"与"启动部署已落定"。此时算出的显示事实是错的（图标查不到、覆盖算在旧部署上），
     而 `hasLoadedCatalog` 一旦置位就**不会自愈**。闸门由 `ShellViewModel` 在启动维护的 **`finally`** 里放行
     （放 `try` 末尾的话，启动维护失败/取消时用户会永远停在"尚未读取"）。
  3. **预热路径不占 UI 线程**：IO+投影拆成 `LoadCatalogInputsAsync`（可线程池）与 `ApplySnapshot`（**必须 UI 线程**，
     因为 `BulkObservableCollection` 挂着 `CollectionView`）。预热失败**只记日志不弹提示**。
  预热宿主 `PrewarmArmorPageHost` 是**单独一层占满工作区**（护甲页是整页布局），不与上排两张并排成三列。
- **缩略图只有一套失效协议（2026-10-11，设计文档 §27）**：
  指纹抽成**唯一定义** `HD2ModCore/Infrastructure/ThumbnailFingerprint.cs`——
  `OfSource(path)` = `SHA256(绝对路径|长度|mtimeTicks)`（文件不存在 → 常量 `Missing`），
  `OfNode(nodeId, path)` = `SHA256(nodeId:N|OfSource)`（事实缓存的命名空间前缀）。
  事实缓存与磁盘缓存共用它；`ThumbnailService` 因此降级为**纯渲染器**（只解码/落盘，不再自己判新鲜）。
  三条容易忘的：
  1. **磁盘键必须含内容**，否则"同路径换图标"命中旧缩略图且**永不自愈**——
     这正是此前 `UpdateIconAsync`（库页"更新图标"）与 `ModDetailsPageViewModel` 更新图像的真实 bug。
  2. **失效协议只有一套，垃圾回收是另一件事**，且只有一个入口：启动维护之后一次性 `SweepAsync`
     （旧键 `File.Move` 改名 + 无主文件清扫；只删早于本次开始的文件，免得和并发写入抢）。
     所以 `ModLibraryService` 两处 `DeleteCachedThumbnailsForSource` 已**全部删除**（2 → 0）。
  3. 旧键只允许出现在清扫里（`LegacyDiskKey`）。改名条件是"旧缩略图不比源图旧"。
  顺带删掉旧管道遗留：`PreGenerateAll` / `EnsureThumbnailsAsync` / `ThumbnailService.RegenerateThumbnailAsync`
  三个零调用点方法，以及 `CancelPendingGeneration`（它只换一个**没人读**的 CTS，已是空操作）及其 4 处调用点。
- **`ModInformationFactRegistry` 的 `Validate()` 有 5 项检查**，启动期在 `CoreServices.CreateModInformationCenter` 开头跑。
  新增 kind 时若忘了写依赖或忘了登记 `SourceRoots`，启动就失败（不是测试失败）。
- **护栏 1 的"业务服务半边"按文档原文会误伤 7 个正当调用点**（`MapAsync` 在 Core 里另有 7 处合法调用，
  分布在 6 个文件：资产角色 / 材质投递 / 覆盖图 / 材质诊断……）。所以实际收口方式是：
  按**方法**禁 `GetArchiveSummariesAsync` 与整个 `IDeployedOverrideGraphService`（只允许出现在装配点、
  中心、两个事实源适配器与实现自己），映射则靠 `GameDataArchiveBrowserProjectsThroughTheFactSourceOnly` 那道投影缝。
  设计文档 §25。

### 25 个 kind 的真实生产情况（D3 的重要副产品，别忘）
**只有 9 个 kind 有真实生产者**（`ModInformationCenter` 内部），其余是声明占位 / 切片 / 请求判别位 / **中心之外生产**：
- L1 读取器真实生产 **4**：`PatchCatalog` / `PatchEntryPayload` / `UnitStructure` / `UnitGeometrySummary`
- L3 中心生产者真实生产 **8**（含 2 处重名）：`PatchCatalog`(FileFacts) / `AssetInventory` / `ReferenceClosure` /
  `Compatibility` / `UnitStructure`(AdvancedUnitAnalysis) / `Maintenance` / `Thumbnail` / **`ArmorCoverage`**
  （后四条无节点事实里，只有 `ArmorCoverage` 是**组合**生产者——它自己不读外部输入，
  而是先请求 `GameDataIndex`/`DeployedGraph`/`GameDataMapping` 再投影）
- **声明了但中心内无任何生产者 15**：`Identity`、`ContentRevision`、`UnitCatalog`、`UnitPartMapping`、
  `MaterialDependencyClosure`、`CompositeBoneDependencyClosure`、`ReferenceEdges`、`GpuPayload`、`StreamPayload`、
  `TocPayload`、`GameDataMapping`、`DecorationDefinition`，以及 **`GameDataIndex`、`AssetMetadata`**
  （后两个由中心**之外**的 `AssetArchiveIndexService` / `FileSystemAssetMetadataCatalogProvider` 生产——正是 E1 还没收编的那部分）
- 唯一一条"中心内部真按依赖顺序取上游"的边：**`Maintenance → AssetInventory`**
  （`ProduceMaintenanceAndRemoveAsync` 先 `RequestAssetInventoryAsync` 再喂给生产者）
- **源事实 `SourceRoots`（7）**：`Identity`、`ContentRevision`、`PatchCatalog`、`GameDataIndex`、`AssetMetadata`、
  **`DeployedGraph`**、`Thumbnail`
  （E1 把 `GameDataMapping` 移出，换成它的两个真实上游；§22 把 `DeployedGraph` 加入）
- **用 `GameData` 主体的 5 个 kind**：`GameDataIndex`、`AssetMetadata`、`GameDataMapping`、`DeployedGraph`、`ArmorCoverage`
  （由 `ModInformationFactRegistryTests.OnlyTheGameDataFactsUseTheGameDataSubject` 强制）

### 信息中心重构进度（2026-10-11；详见设计文档 §6.1 总进度表）
**阶段 0–6 全部完成；只剩量化验收（用户已明确"先不急着测提升速度"）。**
**另：§20.8 遗留的"6 个业务服务仍直调 `MapAsync`"也已在 2026-10-11 收口（设计文档 §28）**，
详见下方"未做项的实地状态"。
- 阶段 0（设计）/ 1（统一身份）：✅ 完成
- 阶段 2（收编 game-data 侧）：✅ 完成（登记表半边 + 页面半边：`GameDataArchiveBrowserService` 退化为纯投影）
- 阶段 3（收编部署侧）：✅ 完成（缓存半边 + 登记半边：`DeployedGraph` 入表、经中心统一身份与合并）
- 阶段 4（护甲页改造）：✅ 完成（改为请求 `ArmorCoverage`，不再 `BuildAsync`；结构护栏已加）
  ——**用户已在真实 Mod 库上实测"快了好几倍"**，但**没有留下可复现的数字**；
  正式版若要写进发布说明，需一次带计时的实测
- 阶段 5（合并缩略图双路）：✅ **完成**（设计文档 §27）——
  指纹唯一定义 + 磁盘键含内容 + 单一 GC 入口；**顺带修掉"换图标后卡片不变"的真实 bug**。
  §26 曾判定"本轮不做"，其两条理由（必须把 Generation 搬进 `ModCardViewModel` / 纯一致性清理无用户价值）
  经侦察**都被推翻**，§26 顶部已加后记留档。
- 阶段 6（护栏测试）：✅ 完成（口径已澄清）——
  Manager 半边 + 登记表完整性 + 运行时主体与登记表一致性 + 护甲页不得构建浏览器 +
  整包读取收口与投影缝（§25.2）+ 图标不进覆盖事实（§25.3）+ 启动预热的正确性前提（§24.8）+
  缩略图单一定义与单一 GC（§27.7）；
  未写的两条（"改部署 → 覆盖重算但缩略图不重算"对拍、"关闭页面取消私有请求"）已在 §25.5 记明真正落点

## 已知性能热点（2026-10-10 分析）
- 护甲映射页首开卡顿根因：`ArmorRegistryPageViewModel.RefreshAsync` →
  `ReadArmorDisplayFactsAsync` 为每护甲取 2 个字段却全量构建 GameData 归档浏览器；
  ~~`DeployedOverrideGraphService.BuildAsync` 无缓存且对全部已部署 patch 做 SHA-256 校验~~
  → **✅ 2026-10-11 已修**（按来源指纹记忆，见上"部署侧事实"）；
  per-item 循环在 UI 线程且 O(catalog×archives)；`RebuildProjections` 在 UI 线程用文化比较排序。
- ~~**仍未解决**：护甲页仍然为"每护甲两个字段"而全量构建整个 GameData 归档浏览器~~ →
  **✅ 2026-10-11 已修**（阶段 4）：改为请求 `ArmorCoverage`，不再走 `BuildAsync`（§23）。
  **✅ 2026-10-11 用户实测确认**：打开护甲映射页"速度飞快，比之前快了好几倍"；
  用户判断自己机器性能好、倍率偏低，较差机器上提升会更大。
  并在启动预热阶段把这一页的 VM / 数据 / 视图一并预热（§24）。
  剩一件收尾：`RebuildProjections` 仍在 UI 线程用文化比较排序（未动，但已不再是首开的主要成本）。
- 详见 `2026-10-10.md`。

## 新大功能：护甲/素体动态骨（物理）（2026-10-11 调研，方案未定）

> 完整调研见 `docs/hd2-armor-physics-research.md`；本轮只调研 + 提需求问题，**未写生产代码**。

- **【硬约束】本功能只允许依赖游戏本体，不得依赖任何外部系统（尤其其他 mod）。**
  用户明确："我的插件不应当依赖任何外部系统（尤其是其他mod），而是只依赖游戏本体的内容。"
  → 唯一合法的依赖来源：游戏安装目录里的 `game.dll`、游戏自带资源包、游戏自己的 Lua 环境。
  → `HD2IAS` 在调研中**只作反面参照物**（① 证明"另起一套物理"会卡、卡在哪；② 说明多玩家
  传染来自它的共享素体设计），**一行代码、一个文件都不会进我们的东西**。
  → 这也是"FFI 改内存"被排在最后的原因：它要绑游戏版本指纹，本身就是最脆的依赖。
- **【心智模型】模型分三层，前两层焊死、第三层开放**（用户用"打包好的游戏机"打比方，已对齐）：
  ① **骨架拓扑（有哪些骨）** —— 打包时定，运行时可改 ❌（`Unit` 零 `create_*`）；
  ② **权重（哪块肉跟哪根骨动）** —— 打包时定，运行时不可改 ❌（`Mesh` 零顶点接口）；
  ③ **骨骼姿态（每根骨摆在哪、什么角度）** —— 运行时**完全开放，可逐帧改** ✅
  （`Unit.set_local_rotation`）。**我们要用的是第三层。**
  不是"整台游戏机焊死"，而是"**卡带槽焊死、卡带内容实时可写**"。
- **【方案边界（由此推出）】**：纯运行时**不能**让"没绑到可动骨头上的区域"动起来；
  **能**让"已经绑在某根骨头上的区域"抖起来。陷阱：头发若绑在 `head` 上（最常见），
  让 `head` 抖 = **整个头都在抖**，不是头发单独抖。
  → 出路两条，都要动模型文件：**A** 离线把头发权重重画到 6 根原生抖动骨上（社区做法）；
  **B** 离线加自己的骨 + 重写权重，运行时用弹簧驱动（更自由，且我们已有此能力）。
- **引擎 = Autodesk Stingray（原 Bitsquid）**，向 Lua 暴露完整场景图（`stingray.Unit/World/Quaternion/...`）。
  本仓 `docs/HD2SDK-CommunityEdition-3.8.0/stingray/` 就是它的资源解析器。
- **HD2IAS（HD2-Integrated-Armor-System）确实是"另起一套物理"**：每帧 Lua 读场景图姿态 →
  `hd2ir_push_avatar_pose` → 原生 solver → `hd2ir_retarget_bound_unit_pose` → Lua 逐骨
  `Unit.set_local_position/rotation` → **`World.update_unit()`**。自建格式 `HD2PHY1`
  （chain/joint/collider/link-collision；单次 256 骨、8192 link collisions、65536 candidates）。
- **本体唯一现成可借的物理 = 6 根原生"抖动骨"**（HD2 Modding Wiki《Jiggle Bones》原文）：
  `support_mg`/`support`（父 Chest）、`attach_intelpad`/`attach_samplepouch`/`target_designator`（父 Spine1）、
  `sling`（父 Spine2）。**SDK 不支持新增自定义抖动骨**；社区做法是**把顶点权重重画到这 6 根骨上**
  （必须与锚点骨 `chest`/`spine1`/`head` 混合，不能 100%）。
- **本体布娃娃借不了**：全身刚体、只在 `can_ragdoll`（被击倒/死亡）时激活；数据在动画状态机资源里
  （`RagdollItem{bone_index, params[9], ...}`，见 SDK `state_machine.py`）。Lua 只能**触发**
  （`RagdollOnCommand` 靠签名扫描找 `can_ragdoll`/`start_ragdoll`）。
- **"上百具尸体堆叠不卡"是误导**：那些是**休眠刚体**，成本≈0，不能推出"引擎能跑上百活动刚体"。
- **披风无可借物理**：护甲骨架（`hd2_skel.json`，~110 条）**没有披风骨**；HD2IAS 把披风当独立
  "cape carrier / appearance group"；Wiki 有独立《Cape Modding》章节。
- **【重要】我们自己已有"给 Unit 加骨 + 重写蒙皮"的能力，且在生产链路里**：
  `CanonicalAppendBoneCompiler`（被 `CanonicalDecorationUnitAppender` 调用）、`CanonicalBoneRebuilder`、
  `CanonicalLodBonePaletteCompiler`、`CanonicalInverseJointMatrixCompiler`、`CanonicalMeshSkinningRouter`、
  `UnitMeshWriter.SerializeBoneInfo`；测试 `CanonicalAppendBoneCompilerTests`。
- **`CanonicalAvatarRigReader` 读的就是 modding wiki 让作者导入的同一个 avatar 归档/Unit**
  （`18235e0c9ec0e636` / `5556372446766824087`）→ 全局骨名与层级天然可得。
- **现有"自定义素体"是 Unit 级逐字节复用 + 重新编号，不改骨骼/权重**
  （`docs/custom-model-conversion-20260925.md`：载荷与来源逐字节一致、不做跨 Unit 几何合并）→
  想加物理必须新增"改权重（必要时加骨）"的步骤。
- **HD2IAS 多玩家"传染"根因 = shared body**：`hd2ir_shared_body_profile` / `shared_body_solves` /
  `__hd2ir_shared_body_lifecycle`，多个 avatar 共用一个 solver 实例。**我们若做必须避免这个设计。**
- **HD2IAS 刻意不碰本体布娃娃**（原生插件字符串原文
  `Only custom Mod physics; native ragdolls are unchanged.`），且反过来**冻结**自己那套物理在尸体上运行
  （`hd2ir_freeze_corpse_physics`）。
- 用户下载目录里已备参考：`HD2PhysBone-ReferenceSource-main.zip`（C++ solver `spring_runtime.reference.hpp`
  = VRM SpringBone 移植：stiffness/drag/gravity/collider/angle-limit；Lua 框架；全套 Python 工具链）、
  `HD2IAS-1.0.4-update.zip`、`MMD2HD2 V1.1.341`、`HD2 Transmog (Foundation).zip`。
  解包目录 `tmp/hd2ias/`、`tmp/pb-ref/`（`tmp/` 已 gitignore，**不要**放 `_diagnostics/`）。

### 【第二轮权威取证，覆盖上面的推断】（详见调研文档 §10）

- **HD2 把引擎的物理命名空间全部裁掉了**（我们自己的游戏内 Lua 探测坐实）：
  `PhysicsWorld` / `Actor` / `Joint` / `Mover` / `Cloth` / `HumanIK` / `Vehicle` / `Raycast` / `NavigationMesh`
  **在 HD2 里都不存在**；`Unit.create_actor` / `create_custom_joint` / `create_cloth` 同样不存在。
  上游 Stingray 有这些（官方 Lua 参考共 3098 条签名），HD2 不要。
- **`Mesh` 完全没有骨骼/蒙皮接口 → Lua 碰不到顶点权重。** 这是最硬的约束：
  改权重、加骨、建物理，**纯运行时 Lua 都做不到**。
- **【决定性】运行时"附加临时骨骼"不可能**：逐条核对游戏内探测到的 `Unit` 全部 **119 个成员**，
  **没有任何 `create_*`**（上游 Stingray 有 `create_actor`/`create_cloth`/`create_joint`/
  `create_custom_joint`/`create_vehicle`，HD2 全砍）。`scene_graph_link(i, parent)` 只能
  **改已有节点的父子关系**，不能新增节点；`num_scene_graph_items` 是只读属性。
  上游 Stingray 同样**没有** `create_bone`/`add_scene_graph_item` → **任何版本的引擎都不允许 Lua 造骨**。
  `Mesh` 仅 **24 个成员**（`get_geometry_stats`/材质/可见性/自身变换），**无顶点或权重接口**。
- **运行时能做的只有"驱动已有的骨"**：`set_local_rotation` + `World.update_unit()`
  （HD2IAS 就是这条路，它慢是因为驱动 256 根骨，不是因为有原生插件）。
  → 所以"区域"**只能由骨骼本身决定**：只有某根骨**只被目标区域使用**时，驱动它才只让该区域动。
- **骨架里确实有一批"专用骨"**（只服务某个挂载物/装饰，不服务身体）：
  `backpack`、`attach_knife`、`attach_intelpad`、`attach_samplepouch`、`target_designator`、
  `sling`、`support`、`support_mg`、`r_shoulderarmour`、`l_shoulderarmour`、`cape1..cape8`，
  外加那 6 根原生抖动骨。**这是纯运行时方案唯一合法的"区域"来源。**
- **另一条此前未记录的机制**：`Unit.num_meshes` / `Unit.mesh(i)` / `Mesh.set_local_rotation` ——
  若某 mod 把头发/裙摆做成**独立 mesh 槽位**，它就是一个独立节点，可用**刚体弹簧**让它弹，
  **完全不需要权重**。（"像纸片一样弹弹的"可能正对应这条。）
- **"允许运行时生成缓存"能存什么**：能存 `bones()`/`scene_graph_parent`/`local_pose` 读得到的
  静止姿态、层级、名字→索引映射、弹簧状态；**不能存权重**（Lua 读不到）。
  缓存能**省时间**，不能**补能力**。
- **HD2 自己加了上游没有的抖动骨 C 函数**：`Unit.update_jiggle_bone_data` /
  `Unit.update_jiggle_bone_orientation` / `Unit.debug_draw_jiggle_bone` /
  `Unit.debug_draw_all_jiggle_bones` / `Unit.debug_draw_all_constraint_bones`。
  这是**唯一现成的运行时抖动接口**，且只能驱动**状态机里已存在的 6 根骨**（不能加第 7 根、不能改参数）。
- **6 根抖动骨已用游戏本体数据逐一核对**（玩家状态机 `ragdoll_count = 6`，用 `bone_index` 反查骨名）：
  26 `attach_intelpad`、68 `target_designator`、81 `support`、63 `sling`、71 `support_mg`、54 `attach_samplepouch`；
  参数 `params[9]` **烘焙在状态机资源里**，Lua 不可写。
- **玩家骨架 = 90 骨 / 6 LOD**（bones 资源 1484 字节）；含 `cape1..cape8` 但**无空余抖动骨槽位**。
- **披风 = Havok Cloth**（`hkClothCollidable`），有独立 unit+bones+cloth；全库 `cloth` 资源**只有 3 个**。
  **布娃娃 = Havok**（`ragdoll_profile` 82484 字节，23 具刚体 + `hkRagdollConstraint002`）。
- 游戏自带 Lua 资产全库仅 **12 个**，**没有一个用 jiggle** → 抖动由原生 C++ 驱动。

### 【第三轮权威取证：改内存 ≠ 改逻辑；HD2 其实把 ECS 全给了】（详见调研文档 §11）

**先把"映射链路靠什么成事"说准（纠正关键误解）**：映射**不是**"能随便改内存"，而是"**拨开关**"：
`armor_registry_runtime.lua` 用 **FFI + `WriteProcessMemory`** 只写 `manager + 0x995` 的 **1 个字节**
pending 标志，让**游戏自己去重读** `ArmorRegistry` 目录 —— 真正的映射逻辑在游戏原生代码里。
三条硬自律（脚本注释原文）：`Writable data only; do not change page protection or write code.`
（只写可写数据段 `state==0x1000 && protection==4|8`、不改页保护、不写代码）。
绑定构建：模块是 **`game.dll`**、PE 时间戳 `1790149348`、image size `60719104`；
还用 **E8 site 解析**反推内部实现地址（`Unit.is_child` 4267568 / `Unit.world` 4224528 /
`World.link_unit` 0x3eb860）→ **极其脆弱，游戏一更新全废**。
→ **能力边界：改「数据」可行 ✅ / 改「代码」不可行 ❌。**

**"改骨架驱动链路"五种含义的判定**：
A 改**骨骼本地变换** ✅ 已可行（`Unit.set_local_rotation`，`i` 是场景图节点索引，HD2IAS 走这条）；
B1 **新增骨骼** ❌（`Unit` 零 `create_*`，上游也无 `create_bone`）；
B2 改**已有节点父子** ✅（`scene_graph_link(i, parent)`，`parent=nil` 解除）；
C 改**蒙皮权重** ❌（`Mesh` 零顶点接口）；D 改**抖动骨参数** ⚠️ 原理可行但需定位；
E 改**驱动逻辑** ❌ 不做。

**本轮新发现（上一轮低估）—— HD2 把整套 ECS 暴露给了 Lua**：
`EntityManager` **21 个成员全在**（`create`/`spawn`/`destroy`/`alive` + **13 个组件访问器**：
`unit_component`/`scene_graph_component`/`animation_blender_component`/
`animation_state_machine_component`/`mesh_component`/`transform_component`/`render_data_component`/
`data_component`/`tag_component`/`script_component`/`flow_component`/`debug_name_component`/
`shading_environment_blend_component`）。**但 `actor_component` 被砍**（上游有）
→ **能造实体，造不出物理**。

**三个不需改内存、不需绑构建的官方入口**：
1. `Unit.set_animation_bone_mode(unit, mode)`，mode 含 `ignore`（官方原文 *"Bone animations are
   ignored; they will not affect the unit's bone nodes."*）/ `transform`(默认) / `rotation` / …
   → **可把骨骼从动画驱动里"摘出来"**。
2. `Unit.set_bones_lod(unit, lod_level)` → 不在 LOD 里的骨**不被动画求值**，完全归我们。
3. `AnimationBlenderComponent`（`EntityManager.animation_blender_component(world)`）——
   `create`/`destroy`/`crossfade(entity, animation, layer, blend_time, loop)`/`set_time`/`set_speed`/
   `bone_mode`/`root_mode`/`delta_transform`/`set_bones_lod`…；官方原文：*"Plays an animation directly
   on the entity **without using an animation state machine**."* → **可绕过状态机直接叠动画层**。
   `AnimationBlenderBoneMode` / `AnimationBlenderRootMode` **两个枚举在 HD2 里是暴露的表**。

**两个易踩的坑**：
- `is_child` / `world` 是 **HD2 独有**的 `Unit` 成员，但映射脚本**仍用 FFI 调它们的 RVA**
  → **"在方法表里出现" ≠ "能直接调用"**。
- `Unit.bones()` 官方说明是"**state machine** 里的骨名列表"，与 `num_scene_graph_items`
  （场景图节点数）**不是同一套索引** → 后续工作必须先对齐"骨名 ↔ 场景图索引"。
- `Application.can_get` 实测：**startup/sample_1/sample_2 返回 false，sample_3~6 才 true**
  → 任何"上手就改"的方案都会**静默失败**。

**结论**：**能改骨架驱动链路，但"改内存"不是那条路。** 我们大概率**不需要**走内存 ——
`set_local_rotation` + `set_animation_bone_mode(ignore)` 已能实现"把一批骨从动画手里接管、
逐帧写自己的弹簧结果"，这正是 HD2IAS 所做事情的**官方版本**；差别只在 HD2IAS 每帧驱动
**全部**骨并调 `World.update_unit()`，我们只驱动**少数**目标骨。

**未确认（不得当事实）**：U5 `update_jiggle_bone_data` 的**参数签名**（探测只调白名单
`invokes_discovered_functions: "allowlisted_id_conversion_and_can_get_only"`，**没真的调用过**）；
U6 抖动骨 `params[9]` 内存里**有无运行时副本**；U7 两个枚举的**实际值**；U8 `can_get` 何时变 true。

### 新增生产能力：CLI 直读/提取游戏数据（`hd2mod gamedata extract`）

- 四条命令 **都不需要 `--manager-root`**、也不依赖 SQLite 索引，直读游戏包：
  `extract list [<类型|all>] [<归档>]` / `extract show` / `extract asset --output` / `extract archive --output`。
- 无 `--confirm`（属 `explicit-export`），改用两道输出护栏：**不得落在游戏 data 内**、**非空目录需 `--overwrite`**。
- Core 侧：`AssetTypeNameTable`（内嵌 `typehash.txt`，与管理器同源）、
  `GameDataAssetCatalogService`（直读枚举/三段读取）、`CoreServices.CreateGameDataAssetCatalogService`。
- Agent 能力 Skill：`gamedata-extraction`。
- 实测：`extract list bones` = 3062（与全库直方图一致）；提取玩家 bones 资源 = 1484 字节，
  解析 `nameCount=90 / lodCount=6`，10 个关键骨名全部命中 → 与 `Decryptor` 路径逐字节一致。

## 工作约定
- **【环境】Agent 的 PowerShell 工具里 `Remove-Item` 被 safe-delete 钩子接管，而这台机器上"删除到回收站"必定抛错。**
  两条后端都在**成功删除之后**报错，C 盘与 E 盘一样，且回收站策略并未被禁用
  （`NoRecycleFiles` 不存在、`E:\$Recycle.Bin` 正常）：
  * `genie-trash` 二进制（`%GENIE_TRASH_DIR%\win32-x64.exe`）：exit code 1，
    `ERROR <path>: Error during a trash operation: Unknown { description: "Some operations were aborted" }`；
  * 钩子的内置回退 `[Microsoft.VisualBasic.FileIO.FileSystem]::DeleteFile(..., SendToRecycleBin)`：
    `调用"DeleteFile"时发生异常:"无法找到指定文件。"`。
  实测 `exists after = False` —— 目标**确实被删掉了**，只是报错。
  后果：**任何在 Agent shell 里做删除的脚本都会失败**（钩子 fail-closed + 脚本 `$ErrorActionPreference='Stop'`），
  典型受害者是 `publish.ps1`（第一次 `Remove-PublishOwnedPath` 就中止；2026-10-11 复现两次）。
  应对：**让用户在他自己的 PowerShell 窗口跑**（钩子只存在于 Agent shell 里，不在普通窗口），
  或经用户明确授权后在本次调用里中和钩子的删除路径。
  **不要**用 `[System.IO.File]::Delete` 之类静默绕过——那等于把"删到回收站"降级成永久删除。
- **【强制】远端以后不再推送——助手只做本地提交，推送由用户手动执行。**（2026-10-11 用户明确指令，覆盖此前所有相关讨论。）
- **发版计划（2026-10-11 用户决定）**：等这轮优化重构**彻底结束后**再发正式版给用户。
  含义：发版前不做"引入新失效协议 / 动刚优化过的热路径"这类收益不明确、风险明确的改动。
  > 阶段 5 曾按这条被推迟（§26），但用户随后明确"继续文档中的阶段"，
  > 而侦察发现它**不是**"收益不明确"——它修一个用户可见且不自愈的 bug（§27.1），
  > 且用"旧键改名迁移"避开了全量重生成。所以做了，并把风险点如实报给用户：
  > **它确实改了缩略图磁盘键（一条刚优化过的热路径）**，一次性迁移是唯一的缓解手段。
  > **2026-10-11 收尾：阶段 0–6 全部完成，已发布 `v1.5`**（切点 `5c56314`，纯本地标签，未推送）。
  > 两个产物：`complete`（79.6 MB，含 `bin\dotnet` 私有运行时 .NET 10.0.12）与
  > `requires-dotnet`（9.4 MB，不含 `bin\dotnet`），均通过独立布局/完整性校验。
- **发布约定（2026-10-11 确立）**：
  - 标签用**轻量标签**（仓库既有 `v1.3`/`v1.4` 都是 `type=commit`），不写附注。
  - **产物溯源靠包内 `cliVersion`**：`hd2mod-docs/skills/hd2mod-agent-skills.manifest.json` 里的
    `cliVersion` 形如 `1.0.0+<git 全哈希>`，`+` 后面就是构建时的提交 → 校验产物是否来自发布切点最直接的办法。
  - 校验产物**不要用 PowerShell 的 `Add-Type`**（被安全策略拦），改用 Python `zipfile` 读条目。
  - `HD2ModCli/skills/` 下有 28 个 `SKILL.md`，但导出/打包的是 **27** 个 ——
    少的是 `mod-management`（`CliSkillCatalog` 里 `IsLegacy: true`，被 `ListCapabilitySkills()` 过滤）。
    **27 是设计值**，不是漏打包。
- **未做项的实地状态（2026-10-11 实测，动手前先看这条）**：
  - `FactDescriptor.CacheScope` 全仓**只有一处**生产用途：`ModInformationReader.cs:402` 的防呆校验；
    `FactDescriptor.Producer`（`FactProducerKind`，13 值）**读取处 0 个，连测试都不读**。
    → "缓存放哪层"实际由调用方在 `ModInformationRequestContext.Create(scope)` 写死，与登记表声明**未连通**。
  - ~~6 个直调 `MapAsync` 的服务~~ **已收口（2026-10-11，设计文档 §28）**：
    `IGameDataMappingFactsService` 已从这 6 个服务的构造函数里**整个删掉**，改为只向
    `IModInformationCenter.RequestGameDataMappingAsync` 要映射；新护栏
    `GameDataMappingIsRequestedThroughTheCenterOnly` 钉住（直调点全仓**恰好 2 处**、
    持有该接口的文件**恰好 6 个**）。收口拿到的不是"少算一次"（缓存早在 §20.3 就 static 共享了），
    而是：并发合并（`SourceFingerprintMemo` **无"正在算"的门**）、按 FactKey 的生产诊断、
    尊重调用方声明的缓存作用域（`MapAsync` **无 scope 参数**）、取消跟随中心关闭。
    **新增硬约定**：中心返回的 `ModInformationResult<T>.Data` 可能为 null（索引未建/生产失败），
    消费方一律用 `AsMappingFacts()`（`GameDataMappingResultExtensions`）收敛，不要各自判空。
  - `GameDataMapping` 声明 `Persistent` 但只做进程内记忆——**不是 bug**（`CacheScope` 是"最多允许"的上限）；
    要落盘等于新建持久层，撞"缓存不能取代真相"红线。
  - §20.8 表里"页面收编"那一行**已过期**（§22.6 + §23.6 已完成）。
- **【环境】不要把临时脚本写进 `_diagnostics/`** —— 用 `tmp/`（两者都被 gitignore）。
  `DiagnosticsPatchSampleTests` 是**条件测试**：`_diagnostics` 目录不存在就跳过；
  一旦这个目录存在（哪怕**空的**）它们就会真跑，然后因拿不到真实样本而失败。
  2026-10-11 实测踩过：写两个校验脚本时顺带创建了该目录 → 全量从 880 绿变成 878 通过 / 2 失败，
  删掉空目录即复原。判断方法：`stat _diagnostics/` 看 **Birth** 时间。
- **【强制】用户授权范围内的改动直接做，不反复请示。**（2026-10-11 用户明确指令。）
- 用户会先要求"只分析、不改代码"，确认后再实施；不要过早做低效/错误优化。
- 源码改动前先考察相关链路（缓存、复用、生命周期）。
- **用户不是专业程序员。** 凡是要询问"某个东西要不要删 / 要做什么"时，**必须先用通俗语言说明它有什么作用、删了会丢什么**，再给建议；不能只给结论或代号。
- **`TreatWarningsAsErrors` 已开启**（`Directory.Build.props`，2026-10-11）。前提是树已**真正零警告**（11 条被清除而非压制）。
  同时用 `<WarningsNotAsErrors>NU1901;NU1902;NU1903;NU1904</WarningsNotAsErrors>` 把 **NuGet 审计警告排除在"错误"之外**：
  用户明确表示"本应用不担心被攻击，这种警告类漏洞无关紧要"（单机 Mod 管理器，不对外服务、不处理不可信输入）。
  **只降级不静默**——警告照常打印，只是不再挡住构建。改这一行前先确认用户意图。
  > 反直觉事实：**NuGet 只对 net9.0+ 目标审计传递包**。所以 NU1903 只出现在 net10.0 的 Cli/Manager 上，
  > net8.0 的 `HD2ModCore` 从来不报（尽管 `dotnet list package --vulnerable --include-transitive` 能列出漏洞）。
- **写完"应该能抓住某个 bug"的用例后，主动把被保护的那一行破坏掉再跑一次。**
  如果它仍然通过，说明这条用例是假的。（2026-10-11 实测：第一版"只改源"的用例因为
  `ApplyExecutor` 只支持硬链接/符号链接、目标与源是同一个文件而**假通过**，靠探针才发现。）
- 删除类改动**必须同时改造其测试**（改判据形态），不能连测试一起删——否则等于把判据删了。
- `_diagnostics/` 下的工程一律是临时诊断工程，**从未被 git 跟踪**（untracked + gitignored）；
  生产代码不得依赖它们，该纪律由 `DiagnosticsIsolationArchitectureTests` 强制。
- **`docs/` 被 `.gitignore` 忽略**，设计文档无法提交、也无 git 历史 → 编辑前务必备份到 `docs/_backup/`。
- `CoreServices` 是唯一装配点：判断"某服务是否在生产中被使用"的判据是
  `CoreServices.CreateXxx(` 的调用点是否为 0（接口+实现+工厂齐备但零调用 = 生产路径不存在）。
- `git commit` 提交的是**整个索引**，与 `git add` 传了哪些路径无关 →
  提交前必须确认索引干净，或先用 `git reset` 清空再按路径暂存。**多提交拆分时每提交前核对 `git diff --cached --name-only | wc -l`。**
- **核验"死代码可达性没有倒退"的方法**：`git archive <基线提交> | tar -x -C <临时目录>`，
  在基线树与当前树各跑一次 `tools/check_dead_code.py`（脚本以自身路径定位仓库根），
  再用 `comm` 比对两份**不可达类型清单**——比只看总数更能证明"没有任何原本可达的类型变不可达"。
  当前基线：不可达类型 **0**（2026-10-11，`842d497` 之后）。
- **`tools/check_dead_code.py` 是词匹配 → 注释里写类名会把死代码误标为可达。**
  实测：在 `ModInformationFactRegistry.cs` 注释里写了 `CanonicalDependencyClosure`，
  该簇 7 个类型立刻从"不可达"消失（40 → 33），而它们其实仍不可达。
  **必须两个方向一起看**（`comm -13` 新增 + `comm -23` 消失）；只盯总数下降发现不了（下降看起来像好事）。
  写注释说明时改用不含标识符的措辞（如"canonical 依赖闭包审计"）。
- **探测 NuGet 包内嵌的原生 SQLite 版本**（判断漏洞公告是否真实影响构建产物，别只看元数据）：
  解包 `sqlitepclraw.lib.e_sqlite3` → 用 managed python + `ctypes.CDLL(...).sqlite3_libversion()` 读真实 DLL。
  非 win-x64 架构的 DLL 会报 `WinError 193`，忽略即可。
- 构建/测试耗时参考：全解决方案 `dotnet test` 约 6–7 分钟（Core 约 4.5 分、Cli 约 6 分，并行时以最慢者为准）；
  `dotnet build -t:Rebuild` 约 20 秒。
- **测试计数的"增量对账"**：每次报总数时同时报每个工程的分项与增量，
  并用"新增用例条数"核对增量（最近一轮：Core 556 → 557 = 映射收口护栏 1 条；总数 880 → 881）。
  > 历史：Core 546 → 556 = 缩略图指纹 6 + 缩略图架构 4（总数 870 → 880）。
  > **基线数会变，报数前先跑一遍**，别照抄这一行。
  只报总数的话，"某条用例被静默跳过"和"某条被误删"都看不出来。
