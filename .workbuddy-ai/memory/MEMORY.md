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
  `ModInformationFactRegistry` 是 **23** 个 `ModInformationPropertyKind` 的**单一真相**（主体种类 / 缓存范围 / 生产者 / 静态依赖），
  读取器的"是否允许持久化"校验由它驱动，不再有硬编码 kind 清单。
  （阶段 1 时为 21；阶段 2 之后新增 `GameDataIndex` / `AssetMetadata`，见下。）
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

### 24 个 kind 的真实生产情况（D3 的重要副产品，别忘）
**只有 8 个 kind 有真实生产者**（`ModInformationCenter` 内部），其余是声明占位 / 切片 / 请求判别位 / **中心之外生产**：
- L1 读取器真实生产 **4**：`PatchCatalog` / `PatchEntryPayload` / `UnitStructure` / `UnitGeometrySummary`
- L3 中心生产者真实生产 **7**（含 2 处重名）：`PatchCatalog`(FileFacts) / `AssetInventory` / `ReferenceClosure` /
  `Compatibility` / `UnitStructure`(AdvancedUnitAnalysis) / `Maintenance` / `Thumbnail`
- **声明了但中心内无任何生产者 14**：`Identity`、`ContentRevision`、`UnitCatalog`、`UnitPartMapping`、
  `MaterialDependencyClosure`、`CompositeBoneDependencyClosure`、`ReferenceEdges`、`GpuPayload`、`StreamPayload`、
  `TocPayload`、`GameDataMapping`、`DecorationDefinition`，以及 **`GameDataIndex`、`AssetMetadata`**
  （后两个由中心**之外**的 `AssetArchiveIndexService` / `FileSystemAssetMetadataCatalogProvider` 生产——正是 E1 还没收编的那部分）
- 唯一一条"中心内部真按依赖顺序取上游"的边：**`Maintenance → AssetInventory`**
  （`ProduceMaintenanceAndRemoveAsync` 先 `RequestAssetInventoryAsync` 再喂给生产者）
- **源事实 `SourceRoots`（7）**：`Identity`、`ContentRevision`、`PatchCatalog`、`GameDataIndex`、`AssetMetadata`、
  **`DeployedGraph`**、`Thumbnail`
  （E1 把 `GameDataMapping` 移出，换成它的两个真实上游；§22 把 `DeployedGraph` 加入）
- **用 `GameData` 主体的 4 个 kind**：`GameDataIndex`、`AssetMetadata`、`GameDataMapping`、`DeployedGraph`
  （由 `ModInformationFactRegistryTests.OnlyTheGameDataFactsUseTheGameDataSubject` 强制）

### 信息中心重构进度（2026-10-11；详见设计文档 §6.1 总进度表）
**阶段 0–3 已全部完成；阶段 4/5 与阶段 6 的业务服务半边未开始。**
- 阶段 0（设计）/ 1（统一身份）：✅ 完成
- 阶段 2（收编 game-data 侧）：✅ 完成（登记表半边 + 页面半边：`GameDataArchiveBrowserService` 退化为纯投影）
- 阶段 3（收编部署侧）：✅ 完成（缓存半边 + 登记半边：`DeployedGraph` 入表、经中心统一身份与合并）
- 阶段 4（护甲页改造）：❌ 未开始 —— **仍是护甲页首开卡顿的主因**
- 阶段 5（合并缩略图双路）：❌ 未开始
- 阶段 6（护栏测试）：🟡 Manager 半边 + 登记表完整性 + 运行时主体与登记表一致性；**业务服务半边架构测试尚未写**

## 已知性能热点（2026-10-10 分析）
- 护甲映射页首开卡顿根因：`ArmorRegistryPageViewModel.RefreshAsync` →
  `ReadArmorDisplayFactsAsync` 为每护甲取 2 个字段却全量构建 GameData 归档浏览器；
  ~~`DeployedOverrideGraphService.BuildAsync` 无缓存且对全部已部署 patch 做 SHA-256 校验~~
  → **✅ 2026-10-11 已修**（按来源指纹记忆，见上"部署侧事实"）；
  per-item 循环在 UI 线程且 O(catalog×archives)；`RebuildProjections` 在 UI 线程用文化比较排序。
- **仍未解决**：护甲页仍然为"每护甲两个字段"而**全量构建整个 GameData 归档浏览器**
  （`GameDataArchiveBrowserService.BuildAsync`），这才是首开卡顿的主因 →
  对应阶段 4（护甲页改造），未开始。
- 详见 `2026-10-10.md`。

## 工作约定
- **【强制】远端以后不再推送——助手只做本地提交，推送由用户手动执行。**（2026-10-11 用户明确指令，覆盖此前所有相关讨论。）
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
- 构建/测试耗时参考：全解决方案 `dotnet test` 约 7–8 分钟（Core 约 5.5 分、Cli 约 7 分，并行时以最慢者为准）；
  `dotnet build -t:Rebuild` 约 20 秒。
