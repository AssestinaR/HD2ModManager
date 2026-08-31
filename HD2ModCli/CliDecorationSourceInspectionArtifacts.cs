using HD2ModCore.Application;
using HD2ModCore.Decoration;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCli;

/// <summary>
/// Read-only source discovery for the Decoration workflow.  It deliberately
/// uses the operation-only Mod facts reader, so an Agent can learn the exact
/// selectable Units without warming or changing desktop caches.
/// </summary>
public sealed record CliDecorationSourceInspection(
	string SourceNodeId,
	string SourceModName,
	string SourceModKind,
	string? ContentGeneration,
	IReadOnlyList<string> SourcePatchFileNames,
	IReadOnlyList<CliDecorationSourceUnitCandidate> UnitCandidates,
	CliDecorationSuggestedAttachment SuggestedAttachment,
	IReadOnlyList<CliDecorationSourceInspectionDiagnostic> Diagnostics)
{
	public bool IsReady
		=> UnitCandidates.Count != 0 && !Diagnostics.Any(diagnostic => diagnostic.Blocking);
}

public sealed record CliDecorationSourceUnitCandidate(
	string SelectionId,
	string ArchiveId,
	string ArchiveCategory,
	string ArchiveDisplayName,
	string UnitAssetKey,
	int MeshInfoIndex,
	string Part,
	string Layer,
	string BodyVariant,
	string SemanticName,
	bool IsCulling,
	string GeometryQuality,
	long StoredBytes,
	int VertexCount,
	int TriangleCount,
	IReadOnlyList<string> SharedArchiveIds,
	bool IsDefaultSelected);

public sealed record CliDecorationSuggestedAttachment(
	string PreferredSourceArchiveId,
	string TargetPart,
	string TargetBodyVariant,
	string DualVariantMode,
	bool ReplaceWhenSourcePartLayerMatches,
	IReadOnlyList<string> SourcePartLayers);

public sealed record CliDecorationSourceInspectionDiagnostic(
	string Code,
	string Severity,
	bool Blocking,
	string Message,
	string? Path = null,
	string? NodeId = null,
	string? Details = null);

internal static class CliDecorationSourceInspectionArtifacts
{
	public static async ValueTask<CliDecorationSourceInspection> BuildAsync(
		CliWorkspace workspace,
		string sourceSelector,
		bool allowSnapshotDecoration = false,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		if (string.IsNullOrWhiteSpace(workspace.ManagerRoot))
			throw new CliUserException("DecorationInspectManagerRootRequired", "decoration inspect source 需要 --manager-root，以读取当前装备目录索引。 ");
		if (string.IsNullOrWhiteSpace(workspace.LibraryRoot))
			throw new CliUserException("LibraryNotConfigured", "decoration inspect source 需要明确的 Mod 库。 ");
		if (string.IsNullOrWhiteSpace(workspace.GameDataRoot))
			throw new CliUserException("DecorationInspectGameDataRequired", "decoration inspect source 需要 --game-data <游戏 data 目录>，或可读取的管理器 GameDataFolder 设置。 ");

		var state = await workspace.LoadLibraryAsync(cancellationToken).ConfigureAwait(false);
		var source = CliProjections.ResolveMod(state.Snapshot.Nodes, sourceSelector);
		var isSnapshotDecoration = allowSnapshotDecoration && source.Metadata.Kind == ModNodeKind.Decoration;
		if (source.Metadata.Kind is not (ModNodeKind.Standard or ModNodeKind.Option) && !isSnapshotDecoration)
		{
			throw new CliUserException(
				"DecorationSourceKindUnsupported",
				$"Mod “{source.Metadata.Name}”的类型为 {source.Metadata.Kind}，不能作为装饰模型来源。只有带 Patch 的普通 Mod、选项 Mod，或编辑时的 PatchSnapshot 装饰 Mod 可用。 ");
		}
		if (!isSnapshotDecoration && source.PatchGroups.Count == 0)
		{
			throw new CliUserException(
				"DecorationSourcePatchMissing",
				$"Mod “{source.Metadata.Name}”没有可读取的 Patch 主文件，不能作为装饰模型来源。 ");
		}

		var paths = new StoragePaths(workspace.ManagerRoot, workspace.LibraryRoot);
		var requestContext = ModInformationRequestContext.Create(
			ModInformationCacheScope.Operation,
			operationName: "CliDecorationSourceInspection");
		await using var reader = CoreServices.CreateModInformationReader();
		var factsReader = CoreServices.CreateDirectModEquipmentSourceFactsReader(paths, reader);
		try
		{
			var facts = await factsReader.ReadAsync(
				source,
				workspace.LibraryRoot,
				new ModInformationRequest(
					ModInformationKind.AssetInventory,
					"CliDecorationSourceInspection",
					Context: requestContext)
				{
					Property = ModInformationPropertyKind.UnitGeometrySummary,
				},
				cancellationToken).ConfigureAwait(false);

			var preferredArchive = DecorationPlanningDefaults.SelectPreferredArchiveId(facts.SourceCandidates);
			var candidates = facts.SourceCandidates
				.SelectMany(entry => entry.Parts.Select(part => CreateCandidate(entry, part, preferredArchive)))
				.GroupBy(candidate => candidate.SelectionId, StringComparer.Ordinal)
				.Select(group => group
					.OrderByDescending(candidate => candidate.StoredBytes)
					.ThenByDescending(candidate => candidate.VertexCount)
					.ThenBy(candidate => candidate.ArchiveId, StringComparer.OrdinalIgnoreCase)
					.First())
				.OrderBy(candidate => candidate.ArchiveDisplayName, StringComparer.OrdinalIgnoreCase)
				.ThenBy(candidate => candidate.Part, StringComparer.Ordinal)
				.ThenBy(candidate => candidate.BodyVariant, StringComparer.Ordinal)
				.ThenBy(candidate => candidate.Layer, StringComparer.Ordinal)
				.ThenBy(candidate => candidate.UnitAssetKey, StringComparer.Ordinal)
				.ThenBy(candidate => candidate.MeshInfoIndex)
				.ToArray();
			var selectedParts = facts.SourceCandidates
				.Where(entry => string.Equals(entry.ArchiveId, preferredArchive, StringComparison.OrdinalIgnoreCase))
				.SelectMany(entry => entry.Parts)
				.Where(part => !part.IsCullingMesh)
				.ToArray();
			var diagnostics = facts.Issues.Select(CreateDiagnostic).ToList();
			if (candidates.Length == 0)
			{
				diagnostics.Add(new CliDecorationSourceInspectionDiagnostic(
					"DecorationSourceCandidatesMissing",
					"error",
					true,
					"没有找到可用于装饰的可见装备 Unit。请确认来源 Patch、GameData 装备索引和 Unit 几何均可读取。",
					NodeId: CliProjections.FormatId(source.Id)));
			}

			return new CliDecorationSourceInspection(
				CliProjections.FormatId(source.Id),
				source.Metadata.Name,
				source.Metadata.Kind.ToString().ToLowerInvariant(),
				facts.ContentGeneration,
				facts.SourcePatchTocPaths.Select(Path.GetFileName).Where(name => !string.IsNullOrWhiteSpace(name)).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray()!,
				candidates,
				new CliDecorationSuggestedAttachment(
					preferredArchive,
					DecorationPlanningDefaults.ResolveTargetPart(selectedParts),
					DecorationPlanningDefaults.ResolveTargetBodyVariant(selectedParts),
					"AutoAssign",
					true,
					selectedParts
						.Select(part => DecorationPlanningDefaults.ToPartLayerKey(part.PartKind, part.Layer))
						.Distinct(StringComparer.OrdinalIgnoreCase)
						.OrderBy(value => value, StringComparer.Ordinal)
						.ToArray()),
				diagnostics);
		}
		finally
		{
			factsReader.ClearOperation(requestContext.OperationId);
		}
	}

	private static CliDecorationSourceUnitCandidate CreateCandidate(
		EquipmentUnitCatalogEntry entry,
		EquipmentUnitPart part,
		string preferredArchive)
	{
		var assetKey = FormatAssetKey(part.UnitAssetKey);
		return new CliDecorationSourceUnitCandidate(
			$"{entry.ArchiveId}|{assetKey}|{part.MeshInfoIndex}|{part.IsCullingMesh}",
			entry.ArchiveId,
			entry.Category,
			entry.DisplayName,
			assetKey,
			part.MeshInfoIndex,
			part.PartKind.ToString(),
			part.Layer.ToString(),
			part.BodyVariant.ToString(),
			part.SemanticName,
			part.IsCullingMesh,
			part.GeometryQuality.ToString(),
			part.StoredBytes,
			part.VertexCount,
			part.TriangleCount,
			part.SharedArchiveIds.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
			!part.IsCullingMesh && string.Equals(entry.ArchiveId, preferredArchive, StringComparison.OrdinalIgnoreCase));
	}

	private static CliDecorationSourceInspectionDiagnostic CreateDiagnostic(CoreIssue issue)
		=> new(
			issue.Code,
			issue.Severity == CoreIssueSeverity.Error ? "error" : "warning",
			issue.Severity == CoreIssueSeverity.Error,
			issue.Message,
			issue.FilePath,
			issue.NodeId is { } nodeId ? CliProjections.FormatId(nodeId) : null,
			issue.ExceptionMessage);

	private static string FormatAssetKey(AssetKey key)
		=> $"0x{key.TypeId:x16}/0x{key.FileId:x16}";
}
