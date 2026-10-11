namespace HD2ModCore.Domain;

public sealed record RuntimeArmorRegistryModel(
	string Key,
	uint? SourceItemId,
	IReadOnlyList<RuntimeArmorRegistryModelPiece>? Pieces = null,
	string? Name = null);

public sealed record RuntimeArmorRegistryModelPiece(
	uint Body,
	string Unit,
	uint Slot,
	uint PieceType,
	byte Tone = 0,
	bool Enabled = true,
	bool IsDefaultBase = false,
	bool IsReserved = false,
	uint? OriginalBody = null);

public sealed record RuntimeArmorRegistryPieceOverride(
	uint OwnerItemId,
	uint Body,
	string Unit,
	uint Slot,
	uint PieceType,
	bool Enabled = true);

public sealed record RuntimeArmorRegistryTargetMode(
	uint TargetItemId,
	bool FollowSource = true);

public sealed record RuntimeArmorRegistryMapping(
	uint TargetItemId,
	uint? SourceItemId = null,
	string? ModelKey = null)
{
	public bool IsModelMapping => !string.IsNullOrWhiteSpace(ModelKey);
}

public sealed record RuntimeArmorRegistryDocument(
	int Revision,
	bool Enabled,
	IReadOnlyList<RuntimeArmorRegistryMapping> Mappings,
	IReadOnlyList<RuntimeArmorRegistryModel>? Models = null,
	IReadOnlyList<RuntimeArmorRegistryPieceOverride>? PieceOverrides = null,
	IReadOnlyList<RuntimeArmorRegistryTargetMode>? TargetModes = null,
	string CompositionMode = "full")
{
	public IReadOnlyList<RuntimeArmorRegistryModel> ModelDefinitions { get; init; }
		= Models ?? Array.Empty<RuntimeArmorRegistryModel>();
	public IReadOnlyList<RuntimeArmorRegistryPieceOverride> PieceOverrideDefinitions { get; init; }
		= PieceOverrides ?? Array.Empty<RuntimeArmorRegistryPieceOverride>();
	public IReadOnlyList<RuntimeArmorRegistryTargetMode> TargetModeDefinitions { get; init; }
		= TargetModes ?? Array.Empty<RuntimeArmorRegistryTargetMode>();
}

public sealed record RuntimeArmorCatalogEntry(
	uint ItemId,
	string ArchiveId,
	string DisplayName,
	GameDataEquipmentKitKind KitKind = GameDataEquipmentKitKind.Armor,
	uint SetId = 0,
	uint DlcId = 0);

public readonly record struct RuntimeArmorProviderMember(
	uint ItemId,
	GameDataEquipmentKitKind KitKind,
	bool IsCustomModel = false);

public static class RuntimeArmorMappingPairing
{
	public static uint? FindUniqueSourceForTarget(
		IReadOnlyList<RuntimeArmorProviderMember> sources,
		GameDataEquipmentKitKind targetKind)
	{
		if (targetKind is not (GameDataEquipmentKitKind.Armor or GameDataEquipmentKitKind.Helmet))
			return null;
		var matches = sources
			.Where(source => source.IsCustomModel || source.KitKind == targetKind)
			.Select(source => source.ItemId)
			.Distinct()
			.Take(2)
			.ToArray();
		return matches.Length == 1 ? matches[0] : null;
	}
}

// 快照里的护甲注册文档是**从哪读到的**。
// 这不是诊断细节，而是页面必须说清楚的事实：工作区在模组库里（跟库走），
// 协议在用户目录里（跟用户走），两者生命周期不同——详见
// docs/armor-registry-page-20261011-regression.md §13。
// 工作区缺失时只能退回协议副本：那是用户映射**最后的一份存留**，绝不能隐藏，
// 但必须让用户知道"这不是本库的工作区"，否则他会以为映射本来就长这样。
public enum RuntimeArmorRegistryDocumentSource
{
	// 读的是本模组库的工作区文件（正常情况）。
	Workspace,
	// 本库没有工作区，退回读取用户目录里的协议副本。
	ProtocolFallback,
	// 既没有工作区也没有可用的协议。
	Empty,
}

public sealed record RuntimeArmorRegistrySnapshot(
	string RegistryRoot,
	string ProtocolPath,
	string Fingerprint,
	bool ProtocolExists,
	RuntimeArmorRegistryDocument Document,
	bool RuntimeStatusExists,
	bool RuntimeProcessRunning,
	bool RuntimeAcknowledged,
	IReadOnlyDictionary<string, string> RuntimeStatus,
	bool LocatorCacheExists,
	IReadOnlyDictionary<string, string> LocatorCache)
{
	public bool HasUnappliedChanges { get; init; }

	// 文档来源。默认 Workspace，这样既有的构造点无需改动。
	public RuntimeArmorRegistryDocumentSource DocumentSource { get; init; } = RuntimeArmorRegistryDocumentSource.Workspace;

	// 协议里记录的"归属模组库"（应用时写下的用户级标记）。
	// 用来判断这份协议是不是本库应用出来的：为空表示无从判断（旧版本写的协议）。
	public string? ProtocolOwnerWorkspace { get; init; }
}

public sealed record RuntimeArmorRegistryApplyResult(
	RuntimeArmorRegistryDocument Document,
	string Fingerprint,
	bool RuntimeProcessRunning,
	bool RuntimeAcknowledged);
