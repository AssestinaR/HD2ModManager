using AdaptationPatchTocEntry = HD2ModAdaptation.PatchReconstruction.PatchTocEntry;

namespace HD2ModCore.Domain;

// 作用：描述一个 Mod 可作为装备替换来源的部件映射、真实几何和已准备 TOC 条目。
// Purpose: Describes a Mod's equipment source mappings, real geometry, and prepared TOC entries.
public sealed record ModEquipmentSourceFacts(
	ModNodeId NodeId,
	string? ContentGeneration,
	IReadOnlyList<string> SourcePatchTocPaths,
	IReadOnlyList<EquipmentUnitCatalogEntry> SourceCandidates,
	IReadOnlyList<EquipmentUnitCatalogEntry> TargetCandidates,
	IReadOnlyDictionary<string, IReadOnlyList<AdaptationPatchTocEntry>> PreparedEntries,
	IReadOnlyList<ModSourceUnitFacts> Units,
	IReadOnlyList<CoreIssue> Issues)
{
	public bool HasUsableSource => SourcePatchTocPaths.Count != 0 && SourceCandidates.Count != 0;

	/// <summary>
	/// 来源 Patch 里的**全部** Unit 键（不只是能被装备目录标注的那些）。
	/// </summary>
	/// <remarks>
	/// 存在的理由：替换护甲页要先把"这个 mod 到底带了哪些 unit"立刻列出来，
	/// 而逐个 unit 解析网格判断可见性是秒级的重活。有了这份清单，页面可以先用
	/// TOC + 注册表格子把表格画出来，可见性再后台补。
	/// </remarks>
	public IReadOnlyList<AssetKey> SourceUnitKeys { get; init; } = [];

	public IReadOnlyList<AdaptationPatchTocEntry> GetPreparedEntries(string patchTocPath)
	{
		if (string.IsNullOrWhiteSpace(patchTocPath)) return Array.Empty<AdaptationPatchTocEntry>();
		return PreparedEntries.TryGetValue(Path.GetFullPath(patchTocPath), out var entries)
			? entries
			: Array.Empty<AdaptationPatchTocEntry>();
	}
}
