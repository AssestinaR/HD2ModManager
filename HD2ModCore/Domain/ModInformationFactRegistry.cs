namespace HD2ModCore.Domain;

// 作用：把全部细粒度属性显式登记成一张表，取代"靠人记住 kind 与主体/缓存范围的对应关系"。
// Purpose: Registers every fine-grained property as an explicit table instead of relying on memory.
// 当前条目数由 Count 给出，并由 Validate() 与枚举取值数强制对齐（新增 kind 漏登记会直接失败）。
//
// 登记规则（与现有 L1 行为一致，不引入新策略）：
//   - PatchEntryPayload / UnitStructure / GpuPayload / StreamPayload / TocPayload 是大对象或按需切片，
//     沿用 ModInformationReader.Validate 的既有约束，声明为 Operation（拒绝 Persistent）。
//   - 其余属性是可重建的事实，声明为 Persistent。
//
// DependsOn 的口径（阶段 2 的 DAG 边）：
//   一条边 A → B 表示"生产 A 时必须先拿到 B 的数据"。
//   每条边都在代码里逐条核对过，依据写在对应条目的注释里；没有任何一条是猜测。
//   没有上游事实的 kind 必须同时出现在 SourceRoots 里（Validate 会强制这个对应关系）。
public static class ModInformationFactRegistry
{
	private static readonly Dictionary<ModInformationPropertyKind, FactDescriptor> Descriptors = new()
	{
		// 依据：节点身份来自 library.json（外部输入 = Source），不依赖任何其它事实。
		[ModInformationPropertyKind.Identity] = new(
			FactSubjectKind.Node,
			ModInformationCacheScope.Persistent,
			FactProducerKind.Source),

		// 依据：内容指纹由文件 mtime/length 直接算出（ModInformationReader.ComputeFileRevision），同样没有上游事实。
		[ModInformationPropertyKind.ContentRevision] = new(
			FactSubjectKind.Node,
			ModInformationCacheScope.Persistent,
			FactProducerKind.Source),

		// 依据：直接读 Mod 目录里的 patch TOC 文件（PatchWorkspaceReader.ReadIndexAsync），
		// 以及 ModFileFactsProducer 的文件系统扫描。二者都只读外部输入，没有上游事实。
		[ModInformationPropertyKind.PatchCatalog] = new(
			FactSubjectKind.Node,
			ModInformationCacheScope.Persistent,
			FactProducerKind.PatchCatalogReader),

		// 依据：ModInformationReader.ReadPatchPayloadAsync(entry, …) 必须由"已读到的 TOC 条目"驱动，
		// 条目正是 PatchCatalog 的内容。去掉这条边就无法定位 payload 在文件中的偏移。
		[ModInformationPropertyKind.PatchEntryPayload] = new(
			FactSubjectKind.Node,
			ModInformationCacheScope.Operation,
			FactProducerKind.PatchEntryPayloadReader,
			[ModInformationPropertyKind.PatchCatalog]),

		// 依据：ModContentFactsService.GetNodeFactsAsync → IPatchGroupAnalysisProvider.AnalyzeNodeAsync
		// → ModInformationPatchGroupAnalysisProvider → InformationReader.ReadPatchIndexAsync。
		// 即资产清单的资产条目本身来自 PatchCatalog 的条目投影（CreateResult 里 assets 由 entries 生成）。
		[ModInformationPropertyKind.AssetInventory] = new(
			FactSubjectKind.Node,
			ModInformationCacheScope.Persistent,
			FactProducerKind.AssetInventoryService,
			[ModInformationPropertyKind.PatchCatalog]),

		// 依据：Unit 目录是 AssetInventory 中 IsUnit 资产的子集
		// （ModAssetStructureAssetFact.IsUnit 由 analysis.Assets 的 asset.IsUnit 汇总而来），
		// 因此不存在"有 Unit 目录却没有资产清单"的合法状态。
		[ModInformationPropertyKind.UnitCatalog] = new(
			FactSubjectKind.Node,
			ModInformationCacheScope.Persistent,
			FactProducerKind.AssetInventoryService,
			[ModInformationPropertyKind.AssetInventory]),

		// 依据：ModEquipmentSourceFactsReader.ReadAsync 组合三样东西——
		//   RequestAssetInventoryAsync（AssetInventory）、
		//   equipmentCatalog（GameData 侧廉价直读，见设计文档 §4 #14，不是本表内的 kind）、
		//   ReadSourceUnitFactsAsync（UnitGeometrySummary，用于 EquipmentUnitSourcePartBinder.Bind）。
		[ModInformationPropertyKind.UnitPartMapping] = new(
			FactSubjectKind.Node,
			ModInformationCacheScope.Persistent,
			FactProducerKind.Composite,
			[ModInformationPropertyKind.AssetInventory, ModInformationPropertyKind.UnitGeometrySummary]),

		// 依据：ModUnitStructureSummary.Create(unit) 的入参是"完整解码后的 Unit"，即 UnitStructure 的内容。
		// 当前实现为了不把完整 Unit 留在缓存里而重新解码一遍（见 ReadUnitSummaryAsync 注释），
		// 但语义上摘要仍是从 Unit 归纳出来的——这条边正是阶段 2 要落成真实复用的地方。
		[ModInformationPropertyKind.UnitGeometrySummary] = new(
			FactSubjectKind.Node,
			ModInformationCacheScope.Persistent,
			FactProducerKind.UnitMeshReader,
			[ModInformationPropertyKind.UnitStructure]),

		// 依据：ReadUnitAsync 里 (a) patchEntries 为空时经 ReadEntriesThroughIndexAsync → ReadPatchIndexAsync
		// 拿条目（PatchCatalog），(b) PatchUnitMeshReader 解码时需要 payload 字节（PatchEntryPayload）。
		[ModInformationPropertyKind.UnitStructure] = new(
			FactSubjectKind.Node,
			ModInformationCacheScope.Operation,
			FactProducerKind.UnitMeshReader,
			[ModInformationPropertyKind.PatchCatalog, ModInformationPropertyKind.PatchEntryPayload]),

		// 依据：材质闭包 = 在"材质→贴图"引用边之上求传递闭包
		// （canonical 依赖闭包审计先读 Unit 的 material bindings，再沿 TextureId 展开）。
		// 边集由 ReferenceEdges 提供，闭包本身只是边集上的计算。
		[ModInformationPropertyKind.MaterialDependencyClosure] = new(
			FactSubjectKind.Node,
			ModInformationCacheScope.Persistent,
			FactProducerKind.Composite,
			[ModInformationPropertyKind.ReferenceEdges]),

		// 依据：与材质闭包同理，只是沿"Unit→骨骼/Composite"引用展开（PatchUnitMeshReader 的
		// IsBoneResolvedFromPatch / IsCompositeResolvedFromPatch 即这条边的落地形态）。
		[ModInformationPropertyKind.CompositeBoneDependencyClosure] = new(
			FactSubjectKind.Node,
			ModInformationCacheScope.Persistent,
			FactProducerKind.Composite,
			[ModInformationPropertyKind.ReferenceEdges]),

		// 依据：ReferenceGraphProducer（depth=DependencyGraph）→ AnalyzeNodeAsync：
		//   ReadPatchIndexAsync 拿条目（PatchCatalog），
		//   ReadMaterialTextureReferencesAsync → ReadPayloadAsync 读材质 payload 里的 TextureId（PatchEntryPayload）。
		[ModInformationPropertyKind.ReferenceEdges] = new(
			FactSubjectKind.Node,
			ModInformationCacheScope.Persistent,
			FactProducerKind.ReferenceGraphService,
			[ModInformationPropertyKind.PatchCatalog, ModInformationPropertyKind.PatchEntryPayload]),

		// 依据：闭包是边集上的传递闭包；ModInformationKind.ReferenceGraph 的 EffectiveProperty 就是本 kind，
		// 而它落盘的内容（ReferenceGraphFacts.Analyses 里的 References）正是边集本身。
		[ModInformationPropertyKind.ReferenceClosure] = new(
			FactSubjectKind.Node,
			ModInformationCacheScope.Persistent,
			FactProducerKind.ReferenceGraphService,
			[ModInformationPropertyKind.ReferenceEdges]),

		// 依据：三个载荷切片都取自同一个 PatchEntryPayload 对象
		// （ToCorePayload 把 TocData / StreamData / GpuResourceData 一起搬过来），不存在独立来源。
		[ModInformationPropertyKind.GpuPayload] = new(
			FactSubjectKind.Node,
			ModInformationCacheScope.Operation,
			FactProducerKind.PatchEntryPayloadReader,
			[ModInformationPropertyKind.PatchEntryPayload]),

		[ModInformationPropertyKind.StreamPayload] = new(
			FactSubjectKind.Node,
			ModInformationCacheScope.Operation,
			FactProducerKind.PatchEntryPayloadReader,
			[ModInformationPropertyKind.PatchEntryPayload]),

		[ModInformationPropertyKind.TocPayload] = new(
			FactSubjectKind.Node,
			ModInformationCacheScope.Operation,
			FactProducerKind.PatchEntryPayloadReader,
			[ModInformationPropertyKind.PatchEntryPayload]),

		// 依据：GameDataMappingFactsService.MapAsync 的产物由两个上游拼出——
		//   FindAssetArchivesAsync（来自 GameDataIndex 的 SQLite 索引）与
		//   AssetMetadataCatalogProvider.LoadAsync（即 AssetMetadata）。
		// 二者都是游戏目录侧的输入事实，现在已作为独立 kind 登记在表内（见下方两条），
		// 于是这条依赖不再是"表外直读"，而是设计文档 §4 #10 依赖 #8+#9 的落地形态。
		[ModInformationPropertyKind.GameDataMapping] = new(
			FactSubjectKind.GameData,
			ModInformationCacheScope.Persistent,
			FactProducerKind.GameDataMappingService,
			[ModInformationPropertyKind.GameDataIndex, ModInformationPropertyKind.AssetMetadata]),

		// 依据：AssetArchiveIndexService 直接从安装好的游戏 data 目录与 authority（加密注册表/实体百科）
		// 构建 SQLite 索引，其身份由 source_fingerprint + built_utc 表达（ComputeSourceFingerprintAsync）。
		// 它没有任何表内上游——游戏文件本身就是真相来源，所以登记为源事实。
		[ModInformationPropertyKind.GameDataIndex] = new(
			FactSubjectKind.GameData,
			ModInformationCacheScope.Persistent,
			FactProducerKind.GameDataIndexService),

		// 依据：FileSystemAssetMetadataCatalogProvider 只读三个社区元数据文件
		// （archivehashes.json / friendlynames.txt / typehash.txt），不依赖任何表内事实。
		[ModInformationPropertyKind.AssetMetadata] = new(
			FactSubjectKind.GameData,
			ModInformationCacheScope.Persistent,
			FactProducerKind.AssetMetadataCatalogReader),

		// 依据：UnitVersionInformationProducer 在 AnalyzeNodeAsync 的产物（资产清单）上做版本探测
		// （UnitVersionProbe.ProbeAsync(analysis)）；设计文档 §4 #4 也把 UnitVersion 定为依赖 #2 AssetInventory。
		[ModInformationPropertyKind.Compatibility] = new(
			FactSubjectKind.Node,
			ModInformationCacheScope.Persistent,
			FactProducerKind.UnitVersionService,
			[ModInformationPropertyKind.AssetInventory]),

		// 依据：装饰定义从 Mod 的资产清单里挑出可替换的 Unit 与材质
		// （DecorationPackageSnapshotService 先用 ReadPatchIndexAsync 定位来源 Patch，
		// 再按选中的 AssetKey 过滤，AssetKey 来自 AssetInventory）。
		[ModInformationPropertyKind.DecorationDefinition] = new(
			FactSubjectKind.Node,
			ModInformationCacheScope.Persistent,
			FactProducerKind.Composite,
			[ModInformationPropertyKind.AssetInventory]),

		// 依据（代码坐实，不是推断）：ModInformationCenter.ProduceMaintenanceAndRemoveAsync
		// 先 await RequestAssetInventoryAsync(...)，再把 inventory.Data 传给 _maintenanceProducer.ProduceAsync(node, inventory.Data, …)。
		// 这是本表里唯一一条"中心内部真实按依赖顺序取上游"的边。
		[ModInformationPropertyKind.Maintenance] = new(
			FactSubjectKind.Node,
			ModInformationCacheScope.Persistent,
			FactProducerKind.MaintenanceAnalysisService,
			[ModInformationPropertyKind.AssetInventory]),

		// 依据：ModThumbnailProducer 只读 Mod 目录里约定命名的图标文件（ModIconLocator.TryResolve），
		// 不做任何 Patch 分析，因此没有上游事实。
		[ModInformationPropertyKind.Thumbnail] = new(
			FactSubjectKind.Node,
			ModInformationCacheScope.Persistent,
			FactProducerKind.ThumbnailService),
	};

	// 作用：被评审确认的"源事实"集合——直接来自外部输入、确实没有上游事实的 kind。
	// Purpose: Reviewed set of source facts which genuinely have no upstream fact.
	// 说明：Validate() 会强制"空依赖 ⟺ 在这个集合里"，于是"新增 kind 却忘了声明依赖"会立刻失败，
	//       而"确实没有上游"必须由人显式登记，不会被静默放过。
	private static readonly HashSet<ModInformationPropertyKind> SourceRootKinds =
	[
		ModInformationPropertyKind.Identity,
		ModInformationPropertyKind.ContentRevision,
		ModInformationPropertyKind.PatchCatalog,
		ModInformationPropertyKind.GameDataIndex,
		ModInformationPropertyKind.AssetMetadata,
		ModInformationPropertyKind.Thumbnail,
	];

	private static readonly Lazy<ModInformationFactGraph> GraphLazy = new(() => ModInformationFactGraph.Build(Descriptors));

	// 作用：登记表条目数，供自检断言使用。
	// Purpose: Number of registered entries, used by the self-check assertion.
	public static int Count => Descriptors.Count;

	public static IReadOnlyCollection<ModInformationPropertyKind> RegisteredKinds => Descriptors.Keys;

	// 作用：源事实集合（无上游依赖的 kind），供测试与诊断核对。
	// Purpose: The source-fact set (kinds without upstream dependencies), for tests and diagnostics.
	public static IReadOnlyCollection<ModInformationPropertyKind> SourceRoots => SourceRootKinds;

	// 作用：由登记表物化出的依赖图（直接边 + 传递闭包 + 拓扑序）。
	// Purpose: The dependency graph materialized from the table (direct edges + closure + topological order).
	public static ModInformationFactGraph Graph => GraphLazy.Value;

	// 作用：取得某个属性的描述符；未登记时直接抛错，避免静默走到"没有策略"的路径。
	// Purpose: Gets a property descriptor; throws when unregistered so no silent "no policy" path exists.
	public static FactDescriptor Describe(ModInformationPropertyKind kind)
		=> Descriptors.TryGetValue(kind, out var descriptor)
			? descriptor
			: throw new KeyNotFoundException($"未登记的 Mod 信息属性：{kind}。请在 ModInformationFactRegistry 中补登记。");

	public static bool TryDescribe(
		ModInformationPropertyKind kind,
		[System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out FactDescriptor? descriptor)
		=> Descriptors.TryGetValue(kind, out descriptor);

	public static bool IsRegistered(ModInformationPropertyKind kind) => Descriptors.ContainsKey(kind);

	// 作用：启动期/测试期自检——登记表必须自洽，否则直接失败。
	// Purpose: Startup/test self-check: the table must be self-consistent or it fails immediately.
	// 检查项：
	//   1. 每个 ModInformationPropertyKind 都登记了；
	//   2. 条目数与枚举取值数一致（防重复登记）；
	//   3. 每条依赖边指向的 kind 都已登记，且没有自环；
	//   4. 依赖图无环（有环意味着"要生产 A 必须先生产 A"）；
	//   5. 空依赖 ⟺ 在 SourceRoots 里。
	public static void Validate()
	{
		var declared = Enum.GetValues<ModInformationPropertyKind>();
		var missing = declared.Where(kind => !Descriptors.ContainsKey(kind)).ToArray();
		if (missing.Length > 0)
			throw new InvalidOperationException($"以下 Mod 信息属性尚未在 ModInformationFactRegistry 中登记：{string.Join('、', missing)}。");

		if (Descriptors.Count != declared.Length)
			throw new InvalidOperationException($"登记表条目数 {Descriptors.Count} 与 ModInformationPropertyKind 的取值数 {declared.Length} 不一致，可能存在重复登记。");

		foreach (var (kind, descriptor) in Descriptors)
		{
			foreach (var dependency in descriptor.Dependencies)
			{
				if (dependency == kind)
					throw new InvalidOperationException($"{kind} 声明了指向自己的依赖。");
				if (!Descriptors.ContainsKey(dependency))
					throw new InvalidOperationException($"{kind} 依赖了未登记的属性 {dependency}。");
			}

			var hasDependencies = descriptor.Dependencies.Count > 0;
			var isDeclaredSource = SourceRootKinds.Contains(kind);
			if (hasDependencies && isDeclaredSource)
				throw new InvalidOperationException($"{kind} 被登记为源事实（SourceRoots），却声明了依赖 {string.Join('、', descriptor.Dependencies)}。");
			if (!hasDependencies && !isDeclaredSource)
				throw new InvalidOperationException($"{kind} 没有任何上游依赖，但也没有被登记为源事实。若它确实直接来自外部输入，请把它加入 SourceRoots；否则请补上依赖边。");
		}

		var graph = Graph;
		if (!graph.IsAcyclic)
			throw new InvalidOperationException($"依赖图存在环：{string.Join(" → ", graph.TopologicalOrder.Select(kind => kind.ToString()))}。");

		if (graph.TopologicalOrder.Count != declared.Length)
			throw new InvalidOperationException($"依赖图的拓扑序长度 {graph.TopologicalOrder.Count} 与属性数 {declared.Length} 不一致，说明存在无法排序的节点。");
	}
}
