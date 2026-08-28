namespace HD2ModCore.Domain;

// Purpose: Aggregates file-system-derived facts for one mod node so UI layers do not rescan scattered details.
public sealed record DerivedModNodeData(
	ModNodeId NodeId,
	string RelativePath,
	string AbsoluteDirectory,
	bool DirectoryExists,
	string? IconPath,
	IReadOnlyList<IndexedPatchFile> PatchFiles,
	ModContentFacts ContentFacts,
	ModAssetSummary? AssetSummary,
	ModUnitCompatibilityReport UnitCompatibility,
	IReadOnlyList<CoreIssue> Issues,
	ModAssetStructureFacts? AssetStructure = null);
