namespace HD2ModCore.Domain;
/// <summary>
/// 真映射模式的执行结果。
/// <para>
/// <b>为什么放在 Domain 而不是 Infrastructure。</b>它要挂到
/// <see cref="RuntimeArmorRegistryApplyResult"/> 上一起回给调用方（界面/CLI），
/// 而 Domain 不能反向依赖 Infrastructure。记录本身只有四个原始字段，没有行为依赖。
/// </para>
/// <param name="Applied">实际写进注册表的映射条数。</param>
/// <param name="Skipped">被跳过的映射条数（目标护甲或来源件表找不到）。</param>
/// <param name="Written">是否真的写了文件。<c>false</c> 时 <paramref name="Message"/> 说明为什么没写。</param>
public sealed record ArmorRegistryDataMappingResult(
	int Applied,
	int Skipped,
	bool Written,
	string? Message = null)
{
	public static readonly ArmorRegistryDataMappingResult Nothing = new(0, 0, false, null);

	/// <summary>被写成 <c>0</c>（隐藏）的格子数。来源没覆盖到的部位都算在这里。</summary>
	public int Hidden { get; init; }

	/// <summary>换成本源 unit 的格子数。</summary>
	public int Replaced { get; init; }
}

public sealed record RuntimeArmorRegistryModel(
	string Key,
	uint? SourceItemId,
	IReadOnlyList<RuntimeArmorRegistryModelPiece>? Pieces = null,
	string? Name = null);

/// <summary>
/// 一条视觉组件 = 护甲结构里的一个 (body, slot, pieceType) → unit。
/// <para>
/// <c>Body</c> 是**原始编码**（0=Stocky、1=Slim、3=Any），不是 <c>UnitMeshBodyVariant</c>；
/// 两者在 0 上错位，转换只能走 <see cref="ArmorStructure.ToBodyVariant"/>。
/// <c>PieceType</c> 是该部位的第几个 unit 槽（0/1/2），不是部位编号。结构全貌见 <see cref="ArmorStructure"/>。
/// </para>
/// </summary>
public sealed record RuntimeArmorRegistryModelPiece(
	uint Body,
	string Unit,
	uint Slot,
	uint PieceType,
	byte Tone = 0,
	bool Enabled = true,
	bool IsDefaultBase = false,
	bool IsReserved = false,
	uint? OriginalBody = null)
{
	/// <summary>
	/// 这一件是**被挪过来的**时，写下它原来的格子（形如 <c>slot0/layer0</c>）；空 = 原生件。
	/// <para>
	/// 只给"头身合并"用：素体的头原本是「头盔槽 / 层 0」，合并后被挪到胸部的某个空层。
	/// 没有这个字段，事后没人能证明"某个胸甲层的那件就是头盔层 0 挪过去的"——看到的只是
	/// 两条互不相干的记录，也没法据此还原。
	/// </para>
	/// </summary>
	public string? MovedFrom { get; init; }
}

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
//
// ⚠️ **2026-11-05 起取消了「退回协议副本」这一档。** 映射结构的唯一来源是模组库里的那份 JSON；
// 它不在，映射结构就是空的（<see cref="Empty"/>）。协议是**派生文件**：换库之后它属于上一个库，
// 读它等于把旧库的映射带进新库。用户口径："每次更换 mod 库也必定只信任 mod 库中的映射结构文件"。
public enum RuntimeArmorRegistryDocumentSource
{
	// 读的是本模组库的工作区文件（正常情况）。
	Workspace,
	// 本库没有工作区文件 ⇒ 映射结构为空（唯一会出现的"映射消失"场景）。
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
	bool RuntimeAcknowledged)
{
	/// <summary>
	/// 真映射这一层的结果（只有真的动过 <c>generated_customization_armor_sets.dl_bin</c> 时非 null）。
	/// <para>
	/// <b>为什么必须带出来。</b>这个值以前被直接丢掉，于是"没有一条映射能应用""备份基准失效
	/// 无法还原""当前文件被外部替换"这些明确的失败原因<b>一个字都不会到界面</b>，
	/// 用户看到的就是"点了没反应"（2026-11-05 反馈：真映射关不掉 / Steam 校验后打不开）。
	/// </para>
	/// </summary>
	public ArmorRegistryDataMappingResult? DataMapping { get; init; }
}
