namespace HD2ModCore.Domain;

// 作用：统一描述一次 Mod 信息产品请求及其来源。
// Purpose: Describes a Mod information request and its business source.
public sealed record ModInformationRequest(
	ModInformationKind Kind,
	string Source,
	string? Generation = null,
	bool RequireFresh = false,
	ModInformationSelector? Selector = null,
	ModInformationRequestContext? Context = null)
{
	private ModInformationRequestContext? _generatedContext;

	// 新读取器统一使用细粒度属性时，可通过 Property 覆盖旧的粗粒度 Kind。
	// The fine-grained reader may use Property while legacy producers continue using Kind.
	public ModInformationPropertyKind? Property { get; init; }

	public ModInformationPropertyKind EffectiveProperty => Property ?? Kind switch
	{
		ModInformationKind.FileFacts => ModInformationPropertyKind.PatchCatalog,
		ModInformationKind.AssetInventory => ModInformationPropertyKind.AssetInventory,
		ModInformationKind.ReferenceGraph => ModInformationPropertyKind.ReferenceClosure,
		ModInformationKind.UnitVersion => ModInformationPropertyKind.Compatibility,
		ModInformationKind.AdvancedUnitAnalysis => ModInformationPropertyKind.UnitStructure,
		ModInformationKind.MaintenanceAnalysis => ModInformationPropertyKind.Maintenance,
		ModInformationKind.Thumbnail => ModInformationPropertyKind.Thumbnail,
		ModInformationKind.GameDataIndex => ModInformationPropertyKind.GameDataIndex,
		ModInformationKind.GameDataMapping => ModInformationPropertyKind.GameDataMapping,
		ModInformationKind.DeployedGraph => ModInformationPropertyKind.DeployedGraph,
		_ => ModInformationPropertyKind.Identity,
	};

	public ModInformationSelector EffectiveSelector => Selector ?? ModInformationSelector.All;
	public ModInformationRequestContext EffectiveContext
	{
		get
		{
			if (Context is not null) return Context;
			return System.Threading.LazyInitializer.EnsureInitialized(
				ref _generatedContext,
				static () => ModInformationRequestContext.Create())!;
		}
	}
}
