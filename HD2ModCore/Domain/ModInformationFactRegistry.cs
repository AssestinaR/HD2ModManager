namespace HD2ModCore.Domain;

// 作用：把 21 个细粒度属性显式登记成一张表，取代"靠人记住 kind 与主体/缓存范围的对应关系"。
// Purpose: Registers all 21 fine-grained properties as an explicit table instead of relying on memory.
//
// 登记规则（与现有 L1 行为一致，不引入新策略）：
//   - PatchEntryPayload / UnitStructure / GpuPayload / StreamPayload / TocPayload 是大对象或按需切片，
//     沿用 ModInformationReader.Validate 的既有约束，声明为 Operation（拒绝 Persistent）。
//   - 其余属性是可重建的事实，声明为 Persistent。
// DependsOn 只填已经能在代码里坐实的静态依赖；动态依赖留给阶段 2 的显式依赖 DAG。
public static class ModInformationFactRegistry
{
	private static readonly Dictionary<ModInformationPropertyKind, FactDescriptor> Descriptors = new()
	{
		[ModInformationPropertyKind.Identity] = new(
			FactSubjectKind.Node,
			ModInformationCacheScope.Persistent,
			FactProducerKind.Source),

		[ModInformationPropertyKind.ContentRevision] = new(
			FactSubjectKind.Node,
			ModInformationCacheScope.Persistent,
			FactProducerKind.Source),

		[ModInformationPropertyKind.PatchCatalog] = new(
			FactSubjectKind.Node,
			ModInformationCacheScope.Persistent,
			FactProducerKind.PatchCatalogReader),

		// Payload 必须由一个已读到的 TOC 条目驱动，因此依赖 PatchCatalog。
		[ModInformationPropertyKind.PatchEntryPayload] = new(
			FactSubjectKind.Node,
			ModInformationCacheScope.Operation,
			FactProducerKind.PatchEntryPayloadReader,
			[ModInformationPropertyKind.PatchCatalog]),

		[ModInformationPropertyKind.AssetInventory] = new(
			FactSubjectKind.Node,
			ModInformationCacheScope.Persistent,
			FactProducerKind.AssetInventoryService),

		[ModInformationPropertyKind.UnitCatalog] = new(
			FactSubjectKind.Node,
			ModInformationCacheScope.Persistent,
			FactProducerKind.AssetInventoryService),

		[ModInformationPropertyKind.UnitPartMapping] = new(
			FactSubjectKind.Node,
			ModInformationCacheScope.Persistent,
			FactProducerKind.Composite),

		// 摘要是由完整 Unit 归纳出来的，因此依赖 UnitStructure。
		[ModInformationPropertyKind.UnitGeometrySummary] = new(
			FactSubjectKind.Node,
			ModInformationCacheScope.Persistent,
			FactProducerKind.UnitMeshReader,
			[ModInformationPropertyKind.UnitStructure]),

		// 完整 Unit 的几何需要先解出 Payload，因此依赖 PatchEntryPayload。
		[ModInformationPropertyKind.UnitStructure] = new(
			FactSubjectKind.Node,
			ModInformationCacheScope.Operation,
			FactProducerKind.UnitMeshReader,
			[ModInformationPropertyKind.PatchEntryPayload]),

		[ModInformationPropertyKind.MaterialDependencyClosure] = new(
			FactSubjectKind.Node,
			ModInformationCacheScope.Persistent,
			FactProducerKind.Composite),

		[ModInformationPropertyKind.CompositeBoneDependencyClosure] = new(
			FactSubjectKind.Node,
			ModInformationCacheScope.Persistent,
			FactProducerKind.Composite),

		[ModInformationPropertyKind.ReferenceEdges] = new(
			FactSubjectKind.Node,
			ModInformationCacheScope.Persistent,
			FactProducerKind.ReferenceGraphService),

		[ModInformationPropertyKind.ReferenceClosure] = new(
			FactSubjectKind.Node,
			ModInformationCacheScope.Persistent,
			FactProducerKind.ReferenceGraphService),

		// 三个载荷切片都取自同一个 Payload。
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

		// 唯一与具体 Mod 无关的全局事实：主体是游戏数据，不是某个节点。
		[ModInformationPropertyKind.GameDataMapping] = new(
			FactSubjectKind.GameData,
			ModInformationCacheScope.Persistent,
			FactProducerKind.GameDataMappingService),

		[ModInformationPropertyKind.Compatibility] = new(
			FactSubjectKind.Node,
			ModInformationCacheScope.Persistent,
			FactProducerKind.UnitVersionService),

		[ModInformationPropertyKind.DecorationDefinition] = new(
			FactSubjectKind.Node,
			ModInformationCacheScope.Persistent,
			FactProducerKind.Composite),

		[ModInformationPropertyKind.Maintenance] = new(
			FactSubjectKind.Node,
			ModInformationCacheScope.Persistent,
			FactProducerKind.MaintenanceAnalysisService),

		[ModInformationPropertyKind.Thumbnail] = new(
			FactSubjectKind.Node,
			ModInformationCacheScope.Persistent,
			FactProducerKind.ThumbnailService),
	};

	// 作用：登记表条目数，供自检断言使用。
	// Purpose: Number of registered entries, used by the self-check assertion.
	public static int Count => Descriptors.Count;

	public static IReadOnlyCollection<ModInformationPropertyKind> RegisteredKinds => Descriptors.Keys;

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

	// 作用：启动期/测试期自检——新增一个 ModInformationPropertyKind 却忘记登记时，这里必须失败。
	// Purpose: Startup/test self-check: adding a ModInformationPropertyKind without registering it must fail here.
	public static void Validate()
	{
		var declared = Enum.GetValues<ModInformationPropertyKind>();
		var missing = declared.Where(kind => !Descriptors.ContainsKey(kind)).ToArray();
		if (missing.Length > 0)
			throw new InvalidOperationException($"以下 Mod 信息属性尚未在 ModInformationFactRegistry 中登记：{string.Join('、', missing)}。");

		if (Descriptors.Count != declared.Length)
			throw new InvalidOperationException($"登记表条目数 {Descriptors.Count} 与 ModInformationPropertyKind 的取值数 {declared.Length} 不一致，可能存在重复登记。");
	}
}
