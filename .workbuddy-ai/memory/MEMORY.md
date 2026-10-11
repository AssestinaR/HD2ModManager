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
  并用"新增用例条数"核对增量（最近一轮：Core 546 → 556 = 缩略图指纹 6 + 缩略图架构 4；总数 870 → 880）。
  只报总数的话，"某条用例被静默跳过"和"某条被误删"都看不出来。
