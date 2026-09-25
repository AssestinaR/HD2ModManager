using HD2ModAdaptation.Analysis;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModCore.Application;

namespace HD2ModCore.Domain;

// Purpose: Describes one indexed Armor or Helmet archive and its current visual Unit meshes.
public sealed record EquipmentUnitCatalogEntry(
	string ArchiveId,
	string Category,
	string DisplayName,
	IReadOnlyList<EquipmentUnitPart> Parts)
{
	// Virtual targets own persistent Unit IDs, but are not GameData archives.
	public string? CustomModelKey { get; init; }
	public bool IsCustomModel => CustomModelKey is not null;
}

// Purpose: Describes one visible, non-LOD Unit mesh that can participate in source or target selection.
public sealed record EquipmentUnitPart(
	AssetKey UnitAssetKey,
	int MeshInfoIndex,
	uint MeshId,
	UnitMeshPartKind PartKind,
	UnitMeshPartLayer Layer,
	UnitMeshBodyVariant BodyVariant,
	string SemanticName,
	int Confidence,
	IReadOnlyList<string> SharedArchiveIds)
{
	public string PieceType { get; init; } = string.Empty;
	// Culling geometry changes what the player can see, but must be written back to
	// a culling MeshInfo rather than treated as an ordinary display LOD.
	public bool IsCullingMesh { get; init; }
	public long StoredBytes { get; init; }
	public int VertexCount { get; init; }
	public int TriangleCount { get; init; }
	public UnitMeshGeometryQuality GeometryQuality { get; init; } = UnitMeshGeometryQuality.Unreadable;
	public bool HasRenderableGeometry => GeometryQuality is UnitMeshGeometryQuality.Renderable or UnitMeshGeometryQuality.RenderableLod0;
	public string StoredSizeText => StoredBytes <= 0 ? "大小未知" : StoredBytes switch
	{
		>= 1024 * 1024 => $"{StoredBytes / (1024d * 1024d):0.0} MiB",
		>= 1024 => $"{StoredBytes / 1024d:0.0} KiB",
		_ => $"{StoredBytes} B"
	};
}

// Purpose: Selects the preferred source body shape only when an exact target-shape match cannot decide the assignment.
public enum CrossArmorBodyVariantPreference { Slim, Stocky }

// Purpose: Selects the preferred layer only when a same-layer candidate is unavailable.
public enum CrossArmorLayerPreference { Armor, Undergarment, Accessory }

// Purpose: Identifies one physical target Unit. Multiple selected archives can reference this one output object.
public sealed record CrossArmorPhysicalTargetKey(AssetKey UnitAssetKey);

// Purpose: Preserves a user-selected source Unit assignment across automatic plan refreshes.
public sealed record CrossArmorManualMapping(CrossArmorPhysicalTargetKey Target, AssetKey SourceUnitAssetKey);

// Purpose: Preserves a user decision to minify one physical target instead of accepting an automatic assignment.
public sealed record CrossArmorManualSuppression(CrossArmorPhysicalTargetKey Target);

// Purpose: Provides an immutable source/target preview for a cross-equipment transfer before any Patch is written.
public sealed record CrossArmorTransferPlan(
	IReadOnlyList<EquipmentUnitCatalogEntry> SourceCandidates,
	EquipmentUnitCatalogEntry? SelectedSource,
	IReadOnlyList<EquipmentUnitCatalogEntry> SelectedTargets,
	IReadOnlyList<CrossArmorTransferMapping> Mappings,
	IReadOnlyList<CrossArmorTransferImpact> Impacts,
	IReadOnlyList<CoreIssue> Issues)
{
	public bool CanContinue => SelectedSource is not null && SelectedTargets.Count != 0 && Mappings.Count != 0 && Issues.All(issue => issue.Severity != CoreIssueSeverity.Error);
}

// Purpose: Predicts a source Unit assignment or target Unit minification without writing an archive.
public sealed record CrossArmorTransferMapping(
	CrossArmorPhysicalTargetKey PhysicalTarget,
	EquipmentUnitPart Target,
	EquipmentUnitPart? Source,
	bool WillReplace,
	string Reason,
	IReadOnlyList<string> UsedByArchiveIds,
	IReadOnlyList<string> UsedByDisplayNames,
	bool IsManual,
	bool IsSuppressed)
{
	public int HitCount { get; init; }
}

// Purpose: Explains another indexed equipment archive affected because it shares a Unit that will be written.
public sealed record CrossArmorTransferImpact(
	string ArchiveId,
	string DisplayName,
	AssetKey SharedUnitAssetKey,
	UnitMeshPartKind PartKind,
	UnitMeshPartLayer Layer);

// Purpose: Chooses whether a cross-armor Unit keeps source material references directly or only uses fully resolved source material closures.
public enum CrossArmorMaterialBindingMode
{
	PreserveSourceReferences,
	RequireCompleteSourceClosure
}

// Purpose: Captures the approved source Patch, read-only mapping plan, and output destination for one isolated cross-armor candidate.
public sealed record CrossArmorTransferCandidateRequest(
	string SourcePatchTocPath,
	string GameDataDirectory,
	string OutputDirectory,
	CrossArmorTransferPlan Plan,
	CrossArmorMaterialBindingMode MaterialBindingMode = CrossArmorMaterialBindingMode.PreserveSourceReferences,
	IReadOnlyList<HD2ModAdaptation.PatchReconstruction.PatchTocEntry>? PreparedSourceEntries = null,
	IProgress<CrossArmorTransferProgress>? Progress = null,
	bool ExpandLodFamilyMappings = true,
	bool AutoHideUnmappedTargetUnits = true,
	// Cross-armor production now defaults to direct source Unit reuse.  The
	// legacy full Canonical cross-armor route remains an internal compatibility
	// fallback only; callers should not opt into it for production output.
	bool DirectSourceUnitReuse = true,
	bool UseSharedHiddenUnitTemplate = true);

// Purpose: Provides stage timing and bounded-work progress during cross-armor candidate generation.
public sealed record CrossArmorTransferProgress
{
	// Purpose: Exposes a stable machine identifier separately from safe user-facing stage text.
	public string Stage { get; }
	public string StageId => Stage;
	public string StageText { get; }
	public int Completed { get; }
	public int Total { get; }
	public TimeSpan Elapsed { get; }

	public CrossArmorTransferProgress(string stage, int completed, int total, TimeSpan elapsed)
		: this(stage, stage, completed, total, elapsed)
	{
	}

	public CrossArmorTransferProgress(string stageId, string stageText, int completed, int total, TimeSpan elapsed)
	{
		if (string.IsNullOrWhiteSpace(stageId)) throw new ArgumentException("阶段 ID 不能为空。", nameof(stageId));
		if (completed < 0) throw new ArgumentOutOfRangeException(nameof(completed));
		if (total < 0) throw new ArgumentOutOfRangeException(nameof(total));
		if (total > 0 && completed > total) throw new ArgumentOutOfRangeException(nameof(completed));
		if (elapsed < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(elapsed));
		Stage = stageId;
		StageText = string.IsNullOrWhiteSpace(stageText) ? stageId : stageText;
		Completed = completed;
		Total = total;
		Elapsed = elapsed;
	}

	// Purpose: Adapts the legacy progress payload to the shared operation telemetry contract.
	public OperationProgressEvent ToOperationProgressEvent(
		Guid operationId,
		OperationState state = OperationState.Progress,
		long sequence = 0,
		Guid? parentOperationId = null,
		string? message = null)
		=> new OperationProgressEvent(
			operationId,
			parentOperationId,
			OperationKind.CrossArmorTransfer,
			OperationStage.Processing,
			state,
			Completed,
			Total,
			message ?? StageText,
			null,
			DateTimeOffset.UtcNow,
			sequence,
			StageId,
			StageText);
}

// Purpose: Reports the independent cross-armor candidate output without changing the source Mod or any profile.
public sealed record CrossArmorTransferCandidateResult(
	bool IsSuccessful,
	string? OutputDirectory,
	string? ReportPath,
	int OutputUnitCount,
	int ReplacementMeshCount,
	int MinifiedMeshCount,
	IReadOnlyList<CoreIssue> Issues)
{
	public bool IsCommitted { get; init; }
	public bool HasWarnings { get; init; }
}
