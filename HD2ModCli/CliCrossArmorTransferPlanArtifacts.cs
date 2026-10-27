using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HD2ModAdaptation.Analysis;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using AdaptationPatchTocEntry = HD2ModAdaptation.PatchReconstruction.PatchTocEntry;

namespace HD2ModCli;

/// <summary>
/// A reviewable, output-only Cross-armor request.  Only <see cref="Request"/>
/// is intentionally editable: validate rebuilds all catalog facts and mappings
/// from the current library and GameData before an output can be written.
/// </summary>
public sealed record CliCrossArmorTransferPlanDocument(
	string Schema,
	int Version,
	DateTimeOffset CreatedUtc,
	string InputFingerprint,
	CliCrossArmorTransferRequest Request,
	CliCrossArmorTransferSource Source,
	IReadOnlyList<CliCrossArmorEquipment> TargetCandidates,
	CliCrossArmorResolvedPlan Plan,
	IReadOnlyList<CliCrossArmorTransferDiagnostic> Diagnostics)
{
	public const string CurrentSchema = "hd2mod.cross-armor-transfer-plan";
	public const int CurrentVersion = 1;

	public bool IsReady
		=> Plan.CanContinue && !Diagnostics.Any(diagnostic => diagnostic.Blocking);
}

/// <summary>
/// Declarative user intent.  Archive IDs and AssetKeys must originate from an
/// inspect/plan document; plan output itself is an audit projection only.
/// </summary>
public sealed record CliCrossArmorTransferRequest(
	string SourceNodeId,
	string SourcePatchFileName,
	string GameDataDirectory,
	string? SourceArmorArchiveId,
	string? SourceHelmetArchiveId,
	string SourceBodyVariant,
	string BodyVariantPreference,
	string LayerPreference,
	IReadOnlyList<string> SelectedTargetArchiveIds,
	IReadOnlyList<CliCrossArmorManualMapping> ManualMappings,
	IReadOnlyList<string> ManualSuppressedTargetAssetKeys,
	bool ManualMode,
	bool AutoHideUnmappedTargetUnits);

public sealed record CliCrossArmorManualMapping(string TargetAssetKey, string SourceAssetKey);

public sealed record CliCrossArmorTransferSource(
	string NodeId,
	string ModName,
	string ModKind,
	string RelativePath,
	string? ContentGeneration,
	IReadOnlyList<CliCrossArmorPatchFamily> PatchFamilies,
	IReadOnlyList<CliCrossArmorEquipment> SourceCandidates);

public sealed record CliCrossArmorPatchFamily(
	string PatchFileName,
	string Fingerprint,
	IReadOnlyList<CliCrossArmorPatchFile> Files);

public sealed record CliCrossArmorPatchFile(string FileName, bool Exists, long Length, string? Sha256);

public sealed record CliCrossArmorEquipment(
	string ArchiveId,
	string Category,
	string DisplayName,
	IReadOnlyList<CliCrossArmorUnitPart> Parts)
{
	public string? CustomModelKey { get; init; }
}

public sealed record CliCrossArmorUnitPart(
	string AssetKey,
	int MeshInfoIndex,
	string MeshId,
	string PartKind,
	string Layer,
	string BodyVariant,
	string SemanticName,
	int Confidence,
	bool IsCullingMesh,
	string GeometryQuality,
	long StoredBytes,
	int VertexCount,
	int TriangleCount,
	IReadOnlyList<string> SharedArchiveIds);

public sealed record CliCrossArmorResolvedPlan(
	bool CanContinue,
	string? SelectedSourceArchiveId,
	IReadOnlyList<string> SelectedTargetArchiveIds,
	IReadOnlyList<CliCrossArmorMapping> Mappings,
	IReadOnlyList<CliCrossArmorImpact> Impacts,
	IReadOnlyList<CliCrossArmorTransferDiagnostic> Issues);

public sealed record CliCrossArmorMapping(
	string PhysicalTargetAssetKey,
	CliCrossArmorUnitPart Target,
	CliCrossArmorUnitPart? Source,
	bool WillReplace,
	int HitCount,
	string Reason,
	bool IsManual,
	bool IsSuppressed,
	IReadOnlyList<string> UsedByArchiveIds,
	IReadOnlyList<string> UsedByDisplayNames)
{
	/// <summary>除 Source 之外还要合并进同一个输出 Unit 的来源部件（装箱时目标格子不够用）。</summary>
	public IReadOnlyList<CliCrossArmorUnitPart> ExtraSources { get; init; } = [];
}

public sealed record CliCrossArmorImpact(
	string ArchiveId,
	string DisplayName,
	string SharedUnitAssetKey,
	string PartKind,
	string Layer);

public sealed record CliCrossArmorTransferDiagnostic(
	string Code,
	string Severity,
	bool Blocking,
	string Message,
	string? Path = null,
	string? NodeId = null,
	string? Details = null);

public sealed record CliCrossArmorGameDataSnapshot(
	string Directory,
	string IndexState,
	bool IsIndexCurrent,
	string CurrentSourceFingerprint,
	string? IndexedSourceFingerprint);

internal sealed record CliCrossArmorTransferBuildResult(
	CliCrossArmorTransferPlanDocument Document,
	StoragePaths Paths,
	ModNode SourceNode,
	string SourcePatchTocPath,
	IReadOnlyList<AdaptationPatchTocEntry> PreparedSourceEntries,
	CrossArmorTransferPlan CorePlan);

internal sealed record CliCrossArmorTransferValidationResult(
	CliCrossArmorTransferPlanDocument Document,
	CliCrossArmorTransferBuildResult? Current,
	IReadOnlyList<CliCrossArmorTransferDiagnostic> Diagnostics)
{
	public bool IsValid
		=> Current is not null && Current.Document.IsReady && !Diagnostics.Any(diagnostic => diagnostic.Blocking);
}

/// <summary>
/// CLI protocol over the desktop application's real source-fact reader,
/// physical-Unit planner and Canonical output path.  It intentionally retains
/// the desktop limitation of exactly one source base Patch group.
/// </summary>
internal static class CliCrossArmorTransferPlanArtifacts
{
	public static async ValueTask<CliCrossArmorTransferBuildResult> BuildAsync(
		CliWorkspace workspace,
		string sourceSelector,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		if (string.IsNullOrWhiteSpace(sourceSelector))
			throw new CliUserException("CrossArmorSourceRequired", "equipment cross-armor plan 需要一个来源 Mod GUID 或唯一前缀。 ");
		var state = await workspace.LoadLibraryAsync(cancellationToken).ConfigureAwait(false);
		var source = CliProjections.ResolveMod(state.Snapshot.Nodes, sourceSelector);
		return await BuildForSourceAsync(workspace, source, requested: null, applyDefaults: true, cancellationToken).ConfigureAwait(false);
	}

	public static async ValueTask<CliCrossArmorTransferBuildResult> BuildAsync(
		CliWorkspace workspace,
		CliCrossArmorTransferRequest request,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		ArgumentNullException.ThrowIfNull(request);
		if (!Guid.TryParse(request.SourceNodeId, out var sourceId))
			throw new CliUserException("CrossArmorSourceIdInvalid", "跨护甲计划的 SourceNodeId 不是有效 GUID。 ");
		var state = await workspace.LoadLibraryAsync(cancellationToken).ConfigureAwait(false);
		if (!state.Snapshot.Nodes.TryGetValue(new ModNodeId(sourceId), out var source))
			throw new CliUserException("CrossArmorSourceNotFound", "跨护甲计划引用的来源 Mod 已不在当前库中。 ");
		return await BuildForSourceAsync(workspace, source, request, applyDefaults: false, cancellationToken).ConfigureAwait(false);
	}

	public static async ValueTask<CliCrossArmorTransferPlanDocument> ReadDocumentAsync(
		string path,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(path))
			throw new CliUserException("CrossArmorPlanRequired", "请通过 --plan <跨护甲计划.json> 提供计划文件。 ");
		var fullPath = CliOutput.NormalizeDirectoryPath(path, "CrossArmorPlanPathInvalid");
		if (!File.Exists(fullPath))
			throw new CliUserException("CrossArmorPlanNotFound", $"跨护甲计划不存在：{fullPath}");
		try
		{
			var document = JsonSerializer.Deserialize<CliCrossArmorTransferPlanDocument>(
				await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false),
				CliOutput.JsonOptions);
			if (document is null)
				throw new CliUserException("CrossArmorPlanUnreadable", "跨护甲计划为空或无法解析。 ");
			if (!string.Equals(document.Schema, CliCrossArmorTransferPlanDocument.CurrentSchema, StringComparison.Ordinal)
				|| document.Version != CliCrossArmorTransferPlanDocument.CurrentVersion)
			{
				throw new CliUserException("CrossArmorPlanVersionUnsupported",
					$"跨护甲计划版本不受支持。需要 {CliCrossArmorTransferPlanDocument.CurrentSchema} v{CliCrossArmorTransferPlanDocument.CurrentVersion}；请重新生成计划。 ");
			}
			if (string.IsNullOrWhiteSpace(document.InputFingerprint)
				|| document.Request is null
				|| string.IsNullOrWhiteSpace(document.Request.SourceNodeId)
				|| string.IsNullOrWhiteSpace(document.Request.SourcePatchFileName)
				|| string.IsNullOrWhiteSpace(document.Request.GameDataDirectory)
				|| document.Request.SelectedTargetArchiveIds is null
				|| document.Request.ManualMappings is null
				|| document.Request.ManualSuppressedTargetAssetKeys is null
				|| document.Source is null
				|| document.TargetCandidates is null
				|| document.Plan is null
				|| document.Diagnostics is null)
			{
				throw new CliUserException("CrossArmorPlanUnreadable", "跨护甲计划缺少来源、意图或审阅字段。 ");
			}
			return document;
		}
		catch (CliUserException) { throw; }
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
		{
			throw new CliUserException("CrossArmorPlanUnreadable", $"无法读取跨护甲计划：{exception.Message}");
		}
	}

	public static async ValueTask<CliCrossArmorTransferValidationResult> ValidateAsync(
		CliCrossArmorTransferPlanDocument document,
		CliWorkspace workspace,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(document);
		ArgumentNullException.ThrowIfNull(workspace);
		var diagnostics = new List<CliCrossArmorTransferDiagnostic>();
		if (string.IsNullOrWhiteSpace(workspace.GameDataRoot)
			|| !string.Equals(Path.GetFullPath(workspace.GameDataRoot), Path.GetFullPath(document.Request.GameDataDirectory), StringComparison.OrdinalIgnoreCase))
		{
			diagnostics.Add(new CliCrossArmorTransferDiagnostic(
				"CrossArmorGameDataTargetChanged", "error", true,
				"当前 --game-data 与计划固定的 GameData 目录不一致；请在同一目标上重新生成计划。"));
			return new CliCrossArmorTransferValidationResult(document, null, diagnostics);
		}

		CliCrossArmorTransferBuildResult? current = null;
		try
		{
			current = await BuildAsync(workspace, document.Request, cancellationToken).ConfigureAwait(false);
		}
		catch (CliUserException exception)
		{
			diagnostics.Add(new CliCrossArmorTransferDiagnostic(exception.Code, "error", true, exception.Message));
			return new CliCrossArmorTransferValidationResult(document, null, diagnostics);
		}

		if (!string.Equals(document.InputFingerprint, current.Document.InputFingerprint, StringComparison.OrdinalIgnoreCase))
		{
			diagnostics.Add(new CliCrossArmorTransferDiagnostic(
				"CrossArmorInputChanged", "error", true,
				"来源 Patch、来源/目标装备事实、GameData 指纹或基础资产索引已变化；请重新生成计划并重新审阅映射。"));
		}
		diagnostics.AddRange(current.Document.Diagnostics.Where(diagnostic => diagnostic.Blocking));
		return new CliCrossArmorTransferValidationResult(document, current, DistinctDiagnostics(diagnostics));
	}

	private static async ValueTask<CliCrossArmorTransferBuildResult> BuildForSourceAsync(
		CliWorkspace workspace,
		ModNode source,
		CliCrossArmorTransferRequest? requested,
		bool applyDefaults,
		CancellationToken cancellationToken)
	{
		EnsureWorkspace(workspace);
		var paths = new StoragePaths(workspace.ManagerRoot!, workspace.LibraryRoot);
		var gameData = Path.GetFullPath(workspace.GameDataRoot!);
		var sourceDirectory = ResolveSourceDirectory(paths, source);
		try
		{
			var beforeGameData = await ReadGameDataSnapshotAsync(paths, gameData, cancellationToken).ConfigureAwait(false);
			var beforePatchPaths = EnumerateBasePatchPaths(sourceDirectory);
			var beforePatchFamilies = await ComputePatchFamiliesAsync(beforePatchPaths, cancellationToken).ConfigureAwait(false);
			await using var reader = CoreServices.CreateModInformationReader();
			var factsReader = CoreServices.CreateDirectModEquipmentSourceFactsReader(paths, reader);
			var facts = await factsReader.ReadAsync(
				source,
				workspace.LibraryRoot!,
				new ModInformationRequest(
					ModInformationKind.AssetInventory,
					"CliCrossArmorPlan",
					Context: ModInformationRequestContext.Create(ModInformationCacheScope.None, operationName: "CliCrossArmorPlan"))
				{
					Property = ModInformationPropertyKind.UnitPartMapping
				},
				cancellationToken).ConfigureAwait(false);
			var afterPatchPaths = EnumerateBasePatchPaths(sourceDirectory);
			var afterPatchFamilies = await ComputePatchFamiliesAsync(afterPatchPaths, cancellationToken).ConfigureAwait(false);
			var afterGameData = await ReadGameDataSnapshotAsync(paths, gameData, cancellationToken).ConfigureAwait(false);
			var diagnostics = facts.Issues.Select(CreateDiagnostic).ToList();
			if (!PatchFamiliesEqual(beforePatchFamilies, afterPatchFamilies))
			{
				diagnostics.Add(new CliCrossArmorTransferDiagnostic(
					"CrossArmorSourceChangedDuringInspection", "error", true,
					"来源 Patch 家族在跨护甲检查期间发生变化；请等待来源写入完成后重新生成计划。", sourceDirectory, CliProjections.FormatId(source.Id)));
			}
			if (!PatchPathSetsEqual(afterPatchPaths, facts.SourcePatchTocPaths))
			{
				diagnostics.Add(new CliCrossArmorTransferDiagnostic(
					"CrossArmorSourcePatchSetMismatch", "error", true,
					"候选扫描读取到的 Patch 集合与当前来源目录不一致；CLI 已拒绝冻结不完整计划。", sourceDirectory, CliProjections.FormatId(source.Id)));
			}
			if (!GameDataSnapshotsEqual(beforeGameData, afterGameData))
			{
				diagnostics.Add(new CliCrossArmorTransferDiagnostic(
					"CrossArmorGameDataChangedDuringInspection", "error", true,
					"GameData 或资产索引输入在跨护甲检查期间发生变化；请重新生成计划。", gameData));
			}
			if (!afterGameData.IsIndexCurrent)
			{
				diagnostics.Add(new CliCrossArmorTransferDiagnostic(
					"CrossArmorGameDataIndexNotCurrent", "error", true,
					"当前 GameData 基础资产索引不可用或已过期；请先在管理器设置页重建资产索引。", gameData));
			}

			var sourcePatchPaths = facts.SourcePatchTocPaths
				.Select(Path.GetFullPath)
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
				.ToArray();
			if (sourcePatchPaths.Length != 1)
			{
				diagnostics.Add(new CliCrossArmorTransferDiagnostic(
					"CrossArmorSingleSourcePatchRequired", "error", true,
					$"跨护甲输出目前只支持一个来源 Patch 主文件组；当前检测到 {sourcePatchPaths.Length} 个。", sourceDirectory, CliProjections.FormatId(source.Id)));
			}

			var sourcePatchPath = sourcePatchPaths.Length == 1 ? sourcePatchPaths[0] : string.Empty;
			var request = requested ?? CreateInitialRequest(source, sourcePatchPath, gameData, facts.SourceCandidates);
			if (!string.Equals(Path.GetFullPath(request.GameDataDirectory), gameData, StringComparison.OrdinalIgnoreCase))
			{
				diagnostics.Add(new CliCrossArmorTransferDiagnostic(
					"CrossArmorRequestGameDataMismatch", "error", true,
					"计划意图中的 GameData 目录与当前 --game-data 不一致。", gameData));
			}
			if (!string.Equals(request.SourceNodeId, CliProjections.FormatId(source.Id), StringComparison.OrdinalIgnoreCase))
			{
				diagnostics.Add(new CliCrossArmorTransferDiagnostic(
					"CrossArmorRequestSourceMismatch", "error", true,
					"计划意图中的来源 Mod 与当前来源节点不一致。", NodeId: CliProjections.FormatId(source.Id)));
			}
			if (sourcePatchPaths.Length == 1 && !IsSafePatchFileName(request.SourcePatchFileName, Path.GetFileName(sourcePatchPath)))
			{
				diagnostics.Add(new CliCrossArmorTransferDiagnostic(
					"CrossArmorSourcePatchMismatch", "error", true,
					"计划固定的来源 Patch 文件名不属于当前唯一来源 Patch 组。", sourceDirectory, CliProjections.FormatId(source.Id)));
			}

			var targetCandidates = facts.TargetCandidates.Concat(
				await CoreServices.CreateRuntimeArmorRegistryService(paths).ReadCustomModelCatalogAsync(cancellationToken).ConfigureAwait(false)).ToArray();
			var resolution = ResolveRequest(request, facts.SourceCandidates, targetCandidates, diagnostics);
			CrossArmorTransferPlan corePlan;
			if (diagnostics.Any(diagnostic => diagnostic.Blocking))
			{
				corePlan = CreateBlockedPlan(facts.SourceCandidates, targetCandidates, resolution.SelectedTargetArchiveIds,
					"跨护甲输入未通过校验，尚未调用 Core 映射规划器。 ");
			}
			else
			{
				var catalog = CoreServices.CreateEquipmentUnitCatalogService(paths);
				corePlan = await catalog.CreatePlanAsync(
					facts.SourceCandidates,
					targetCandidates,
					resolution.PrimarySourceArchiveId,
					resolution.SourceBodyVariant,
					resolution.BodyVariantPreference,
					resolution.LayerPreference,
					resolution.SelectedTargetArchiveIds,
					resolution.ManualMappings,
					resolution.ManualSuppressions,
					request.ManualMode,
					resolution.AdditionalSourceArchiveIds,
					writePlannerDebugLog: false,
					// CLI 是 A/B 对比的入口：它显式走装箱规划器，与桌面端一致。
					plannerKind: CrossArmorPlannerKind.Cells,
					cancellationToken: cancellationToken).ConfigureAwait(false);
				diagnostics.AddRange(corePlan.Issues.Select(CreateDiagnostic));
			}

			var sourceProjection = new CliCrossArmorTransferSource(
				CliProjections.FormatId(source.Id),
				source.Metadata.Name,
				source.Metadata.Kind.ToString(),
				source.RelativePath,
				facts.ContentGeneration,
				afterPatchFamilies,
				facts.SourceCandidates.Select(CreateEquipment).OrderBy(item => item.Category, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.ArchiveId, StringComparer.Ordinal).ToArray());
			var targetProjection = targetCandidates.Select(CreateEquipment)
				.OrderBy(item => item.Category, StringComparer.OrdinalIgnoreCase)
				.ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
				.ThenBy(item => item.ArchiveId, StringComparer.Ordinal)
				.ToArray();
			var projectedPlan = CreateResolvedPlan(corePlan);
			var document = new CliCrossArmorTransferPlanDocument(
				CliCrossArmorTransferPlanDocument.CurrentSchema,
				CliCrossArmorTransferPlanDocument.CurrentVersion,
				DateTimeOffset.UtcNow,
				ComputeInputFingerprint(sourceProjection, targetProjection, afterGameData),
				request,
				sourceProjection,
				targetProjection,
				projectedPlan,
				DistinctDiagnostics(diagnostics));
			return new CliCrossArmorTransferBuildResult(
				document,
				paths,
				source,
				sourcePatchPath,
				string.IsNullOrWhiteSpace(sourcePatchPath) ? Array.Empty<AdaptationPatchTocEntry>() : facts.GetPreparedEntries(sourcePatchPath),
				corePlan);
		}
		catch (CliUserException) { throw; }
		catch (Exception exception) when (exception is IOException
			or UnauthorizedAccessException
			or InvalidDataException
			or DirectoryNotFoundException
			or FileNotFoundException
			or EndOfStreamException
			or OverflowException)
		{
			throw new CliUserException("CrossArmorInspectionFailed", $"无法完成跨护甲检查：{exception.Message}");
		}
	}

	private static void EnsureWorkspace(CliWorkspace workspace)
	{
		if (string.IsNullOrWhiteSpace(workspace.ManagerRoot))
			throw new CliUserException("CrossArmorManagerRootRequired", "equipment cross-armor 需要 --manager-root，以读取装备目录与当前 GameData 索引。 ");
		if (string.IsNullOrWhiteSpace(workspace.LibraryRoot))
			throw new CliUserException("LibraryNotConfigured", "equipment cross-armor 需要明确的 Mod 库。 ");
		if (string.IsNullOrWhiteSpace(workspace.GameDataRoot))
			throw new CliUserException("CrossArmorGameDataRequired", "equipment cross-armor 需要 --game-data <游戏 data 目录>，或可读取的管理器 GameDataFolder 设置。 ");
	}

	private static CliCrossArmorTransferRequest CreateInitialRequest(
		ModNode source,
		string sourcePatchPath,
		string gameDataDirectory,
		IReadOnlyList<EquipmentUnitCatalogEntry> sourceCandidates)
	{
		var armor = sourceCandidates.FirstOrDefault(candidate => string.Equals(candidate.Category, "Armor", StringComparison.OrdinalIgnoreCase));
		var helmet = sourceCandidates.FirstOrDefault(candidate => string.Equals(candidate.Category, "Helmet", StringComparison.OrdinalIgnoreCase));
		return new CliCrossArmorTransferRequest(
			CliProjections.FormatId(source.Id),
			string.IsNullOrWhiteSpace(sourcePatchPath) ? string.Empty : Path.GetFileName(sourcePatchPath),
			gameDataDirectory,
			armor?.ArchiveId,
			helmet?.ArchiveId,
			UnitMeshBodyVariant.Any.ToString(),
			CrossArmorBodyVariantPreference.Slim.ToString(),
			CrossArmorLayerPreference.Armor.ToString(),
			Array.Empty<string>(),
			Array.Empty<CliCrossArmorManualMapping>(),
			Array.Empty<string>(),
			ManualMode: false,
			AutoHideUnmappedTargetUnits: true);
	}

	private static CrossArmorRequestResolution ResolveRequest(
		CliCrossArmorTransferRequest request,
		IReadOnlyList<EquipmentUnitCatalogEntry> sourceCandidates,
		IReadOnlyList<EquipmentUnitCatalogEntry> targetCandidates,
		ICollection<CliCrossArmorTransferDiagnostic> diagnostics)
	{
		var sourceById = sourceCandidates.ToDictionary(candidate => candidate.ArchiveId, StringComparer.OrdinalIgnoreCase);
		var targetById = targetCandidates.ToDictionary(candidate => candidate.ArchiveId, StringComparer.OrdinalIgnoreCase);
		var sourceArmor = ResolveSourceArchive(request.SourceArmorArchiveId, "Armor", "CrossArmorSourceArmor", sourceById, diagnostics);
		var sourceHelmet = ResolveSourceArchive(request.SourceHelmetArchiveId, "Helmet", "CrossArmorSourceHelmet", sourceById, diagnostics);
		var primary = sourceArmor?.ArchiveId ?? sourceHelmet?.ArchiveId;
		if (primary is null)
		{
			diagnostics.Add(new CliCrossArmorTransferDiagnostic(
				"CrossArmorSourceEquipmentRequired", "error", true,
				"计划至少需要选择一个来源 Armor 或 Helmet Archive。"));
		}
		var additional = new[] { sourceArmor?.ArchiveId, sourceHelmet?.ArchiveId }
			.Where(id => !string.IsNullOrWhiteSpace(id) && !string.Equals(id, primary, StringComparison.OrdinalIgnoreCase))
			.Select(id => id!)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray();
		var targetIds = (request.SelectedTargetArchiveIds ?? Array.Empty<string>())
			.Where(id => !string.IsNullOrWhiteSpace(id))
			.ToArray();
		if (targetIds.Length != (request.SelectedTargetArchiveIds ?? Array.Empty<string>()).Count
			|| targetIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != targetIds.Length)
		{
			diagnostics.Add(new CliCrossArmorTransferDiagnostic(
				"CrossArmorTargetSelectionInvalid", "error", true,
				"SelectedTargetArchiveIds 不能包含空值或重复 Archive ID。"));
		}
		foreach (var targetId in targetIds)
		{
			if (!targetById.ContainsKey(targetId))
			{
				diagnostics.Add(new CliCrossArmorTransferDiagnostic(
					"CrossArmorTargetNotFound", "error", true,
					$"计划选择的目标 Archive 已不在当前 GameData 装备目录中：{targetId}。"));
			}
		}
		if (targetIds.Length == 0)
		{
			diagnostics.Add(new CliCrossArmorTransferDiagnostic(
				"CrossArmorTargetRequired", "error", true,
				"计划尚未选择目标装备。请从 targetCandidates 复制一个或多个 ArchiveId 到 request.selectedTargetArchiveIds。"));
		}

		var sourceVariant = ParseEnum<UnitMeshBodyVariant>(request.SourceBodyVariant, "SourceBodyVariant", diagnostics);
		var bodyPreference = ParseEnum<CrossArmorBodyVariantPreference>(request.BodyVariantPreference, "BodyVariantPreference", diagnostics);
		var layerPreference = ParseEnum<CrossArmorLayerPreference>(request.LayerPreference, "LayerPreference", diagnostics);
		var selectedSourceIds = new[] { primary }.Concat(additional).Where(id => !string.IsNullOrWhiteSpace(id)).Cast<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
		var selectableSourceParts = sourceCandidates
			.Where(candidate => selectedSourceIds.Contains(candidate.ArchiveId))
			.SelectMany(candidate => candidate.Parts)
			.Where(part => part.PartKind != UnitMeshPartKind.Unknown && part.Confidence == 100 && !part.IsCullingMesh)
			.Where(part => sourceVariant is null or UnitMeshBodyVariant.Unknown or UnitMeshBodyVariant.Any || part.BodyVariant == sourceVariant || part.BodyVariant == UnitMeshBodyVariant.Any)
			.ToArray();
		var sourcePartsByKey = selectableSourceParts.GroupBy(part => part.UnitAssetKey).ToDictionary(group => group.Key, group => group.ToArray());
		var selectedTargetParts = targetCandidates.Where(candidate => targetIds.Contains(candidate.ArchiveId, StringComparer.OrdinalIgnoreCase))
			.SelectMany(candidate => candidate.Parts)
			.Where(part => part.PartKind != UnitMeshPartKind.Unknown && part.Confidence == 100)
			.GroupBy(part => part.UnitAssetKey)
			.ToDictionary(group => group.Key, group => group.ToArray());

		var manualMappings = new List<CrossArmorManualMapping>();
		var mappedTargets = new HashSet<AssetKey>();
		foreach (var mapping in request.ManualMappings ?? Array.Empty<CliCrossArmorManualMapping>())
		{
			if (mapping is null || !TryParseAssetKey(mapping.TargetAssetKey, out var targetKey) || !TryParseAssetKey(mapping.SourceAssetKey, out var sourceKey))
			{
				diagnostics.Add(new CliCrossArmorTransferDiagnostic("CrossArmorManualMappingInvalid", "error", true, "每项 ManualMappings 都必须包含合法的 targetAssetKey 与 sourceAssetKey（0x类型/0x文件）。"));
				continue;
			}
			if (!mappedTargets.Add(targetKey))
			{
				diagnostics.Add(new CliCrossArmorTransferDiagnostic("CrossArmorManualMappingDuplicate", "error", true, $"同一目标 Unit 重复出现在 ManualMappings：{FormatAssetKey(targetKey)}。"));
				continue;
			}
			if (!selectedTargetParts.TryGetValue(targetKey, out var targetParts))
			{
				diagnostics.Add(new CliCrossArmorTransferDiagnostic("CrossArmorManualTargetUnavailable", "error", true, $"手动映射目标不属于当前已选且可替换的目标 Unit：{FormatAssetKey(targetKey)}。"));
				continue;
			}
			if (!sourcePartsByKey.TryGetValue(sourceKey, out var sourceParts))
			{
				diagnostics.Add(new CliCrossArmorTransferDiagnostic("CrossArmorManualSourceUnavailable", "error", true, $"手动映射来源不属于当前选定来源且可转移的 Unit：{FormatAssetKey(sourceKey)}。"));
				continue;
			}
			if (!targetParts.Select(part => part.PartKind).Intersect(sourceParts.Select(part => part.PartKind)).Any())
			{
				diagnostics.Add(new CliCrossArmorTransferDiagnostic("CrossArmorManualPartKindMismatch", "error", true, $"手动映射来源与目标没有相同的部位类型：{FormatAssetKey(sourceKey)} → {FormatAssetKey(targetKey)}。"));
				continue;
			}
			manualMappings.Add(new CrossArmorManualMapping(new CrossArmorPhysicalTargetKey(targetKey), sourceKey));
		}

		var suppressions = new List<CrossArmorManualSuppression>();
		var suppressed = new HashSet<AssetKey>();
		foreach (var rawKey in request.ManualSuppressedTargetAssetKeys ?? Array.Empty<string>())
		{
			if (!TryParseAssetKey(rawKey, out var targetKey))
			{
				diagnostics.Add(new CliCrossArmorTransferDiagnostic("CrossArmorManualSuppressionInvalid", "error", true, "ManualSuppressedTargetAssetKeys 只能包含 0x类型/0x文件 AssetKey。"));
				continue;
			}
			if (!suppressed.Add(targetKey))
			{
				diagnostics.Add(new CliCrossArmorTransferDiagnostic("CrossArmorManualSuppressionDuplicate", "error", true, $"同一目标 Unit 重复出现在隐藏列表：{FormatAssetKey(targetKey)}。"));
				continue;
			}
			if (mappedTargets.Contains(targetKey))
			{
				diagnostics.Add(new CliCrossArmorTransferDiagnostic("CrossArmorManualTargetConflicting", "error", true, $"同一目标 Unit 不能同时手动映射和手动隐藏：{FormatAssetKey(targetKey)}。"));
				continue;
			}
			if (!selectedTargetParts.ContainsKey(targetKey))
			{
				diagnostics.Add(new CliCrossArmorTransferDiagnostic("CrossArmorManualSuppressionUnavailable", "error", true, $"手动隐藏目标不属于当前已选且可替换的目标 Unit：{FormatAssetKey(targetKey)}。"));
				continue;
			}
			suppressions.Add(new CrossArmorManualSuppression(new CrossArmorPhysicalTargetKey(targetKey)));
		}

		return new CrossArmorRequestResolution(
			primary,
			additional,
			targetIds,
			sourceVariant ?? UnitMeshBodyVariant.Any,
			bodyPreference ?? CrossArmorBodyVariantPreference.Slim,
			layerPreference ?? CrossArmorLayerPreference.Armor,
			manualMappings,
			suppressions);
	}

	private static EquipmentUnitCatalogEntry? ResolveSourceArchive(
		string? id,
		string expectedCategory,
		string codePrefix,
		IReadOnlyDictionary<string, EquipmentUnitCatalogEntry> sourceById,
		ICollection<CliCrossArmorTransferDiagnostic> diagnostics)
	{
		if (string.IsNullOrWhiteSpace(id)) return null;
		if (!sourceById.TryGetValue(id, out var candidate))
		{
			diagnostics.Add(new CliCrossArmorTransferDiagnostic(codePrefix + "NotFound", "error", true, $"计划选择的来源 Archive 已不在当前来源 Mod 中：{id}。"));
			return null;
		}
		if (!string.Equals(candidate.Category, expectedCategory, StringComparison.OrdinalIgnoreCase))
		{
			diagnostics.Add(new CliCrossArmorTransferDiagnostic(codePrefix + "CategoryInvalid", "error", true, $"来源 Archive {id} 的类别不是 {expectedCategory}。"));
			return null;
		}
		return candidate;
	}

	private static TEnum? ParseEnum<TEnum>(string value, string field, ICollection<CliCrossArmorTransferDiagnostic> diagnostics)
		where TEnum : struct, Enum
	{
		if (Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)) return parsed;
		diagnostics.Add(new CliCrossArmorTransferDiagnostic("CrossArmorRequestEnumInvalid", "error", true, $"{field} 不是受支持的 {typeof(TEnum).Name} 枚举值：{value}。"));
		return null;
	}

	private static CrossArmorTransferPlan CreateBlockedPlan(
		IReadOnlyList<EquipmentUnitCatalogEntry> sourceCandidates,
		IReadOnlyList<EquipmentUnitCatalogEntry> targetCandidates,
		IReadOnlyList<string> selectedTargetIds,
		string message)
		=> new(
			sourceCandidates,
			null,
			targetCandidates.Where(candidate => selectedTargetIds.Contains(candidate.ArchiveId, StringComparer.OrdinalIgnoreCase)).ToArray(),
			Array.Empty<CrossArmorTransferMapping>(),
			Array.Empty<CrossArmorTransferImpact>(),
			[new CoreIssue(CoreIssueSeverity.Error, "CrossArmorRequestBlocked", message)]);

	private static CliCrossArmorResolvedPlan CreateResolvedPlan(CrossArmorTransferPlan plan)
		=> new(
			plan.CanContinue,
			plan.SelectedSource?.ArchiveId,
			plan.SelectedTargets.Select(target => target.ArchiveId).OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray(),
			plan.Mappings.Select(mapping => new CliCrossArmorMapping(
				FormatAssetKey(mapping.PhysicalTarget.UnitAssetKey),
				CreateUnitPart(mapping.Target),
				mapping.Source is null ? null : CreateUnitPart(mapping.Source),
				mapping.WillReplace,
				mapping.HitCount,
				mapping.Reason,
				mapping.IsManual,
				mapping.IsSuppressed,
				mapping.UsedByArchiveIds,
				mapping.UsedByDisplayNames)
			{
				ExtraSources = mapping.ExtraSources.Select(CreateUnitPart).ToArray(),
			}).OrderBy(mapping => mapping.PhysicalTargetAssetKey, StringComparer.Ordinal).ToArray(),
			plan.Impacts.Select(impact => new CliCrossArmorImpact(impact.ArchiveId, impact.DisplayName, FormatAssetKey(impact.SharedUnitAssetKey), impact.PartKind.ToString(), impact.Layer.ToString()))
				.OrderBy(impact => impact.ArchiveId, StringComparer.OrdinalIgnoreCase).ThenBy(impact => impact.SharedUnitAssetKey, StringComparer.Ordinal).ToArray(),
			plan.Issues.Select(CreateDiagnostic).ToArray());

	private static CliCrossArmorEquipment CreateEquipment(EquipmentUnitCatalogEntry entry)
		=> new(entry.ArchiveId, entry.Category, entry.DisplayName,
			entry.Parts.Select(CreateUnitPart).OrderBy(part => part.PartKind, StringComparer.Ordinal).ThenBy(part => part.Layer, StringComparer.Ordinal).ThenBy(part => part.AssetKey, StringComparer.Ordinal).ToArray()) { CustomModelKey = entry.CustomModelKey };

	private static CliCrossArmorUnitPart CreateUnitPart(EquipmentUnitPart part)
		=> new(
			FormatAssetKey(part.UnitAssetKey),
			part.MeshInfoIndex,
			$"0x{part.MeshId:x8}",
			part.PartKind.ToString(),
			part.Layer.ToString(),
			part.BodyVariant.ToString(),
			part.SemanticName,
			part.Confidence,
			part.IsCullingMesh,
			part.GeometryQuality.ToString(),
			part.StoredBytes,
			part.VertexCount,
			part.TriangleCount,
			part.SharedArchiveIds.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray());

	private static CliCrossArmorTransferDiagnostic CreateDiagnostic(CoreIssue issue)
		=> new(
			issue.Code,
			issue.Severity == CoreIssueSeverity.Error ? "error" : "warning",
			issue.Severity == CoreIssueSeverity.Error,
			issue.Message,
			issue.FilePath,
			issue.NodeId is { } nodeId ? CliProjections.FormatId(nodeId) : null,
			issue.ExceptionMessage);

	private static async ValueTask<IReadOnlyList<CliCrossArmorPatchFamily>> ComputePatchFamiliesAsync(
		IReadOnlyList<string> sourcePatchPaths,
		CancellationToken cancellationToken)
	{
		var families = new List<CliCrossArmorPatchFamily>();
		foreach (var patch in sourcePatchPaths.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
		{
			var files = new List<CliCrossArmorPatchFile>();
			foreach (var path in new[] { patch, patch + ".stream", patch + ".gpu_resources" })
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (!File.Exists(path))
				{
					files.Add(new CliCrossArmorPatchFile(Path.GetFileName(path), false, 0, null));
					continue;
				}
				var info = new FileInfo(path);
				await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 128, FileOptions.SequentialScan);
				files.Add(new CliCrossArmorPatchFile(Path.GetFileName(path), true, info.Length, Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false))));
			}
			families.Add(new CliCrossArmorPatchFamily(
				Path.GetFileName(patch),
				Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CliOutput.ToJson(files)))),
				files));
		}
		return families;
	}

	private static IReadOnlyList<string> EnumerateBasePatchPaths(string sourceDirectory)
	{
		if (!Directory.Exists(sourceDirectory)) return Array.Empty<string>();
		var parser = CoreServices.CreatePatchFileNameParser();
		return Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.TopDirectoryOnly)
			.Where(path => parser.TryParse(Path.GetFileName(path), out var info) && info?.SidecarKind == PatchSidecarKind.Base)
			.Select(Path.GetFullPath)
			.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
			.ToArray();
	}

	private static string ResolveSourceDirectory(StoragePaths paths, ModNode source)
	{
		var root = Path.GetFullPath(paths.ModsDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		var directory = Path.GetFullPath(Path.Combine(root, source.RelativePath));
		if (string.Equals(directory, root, StringComparison.OrdinalIgnoreCase)
			|| (!directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
				&& !directory.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
		{
			throw new CliUserException("CrossArmorSourcePathInvalid", "当前 Mod 的存储路径不在目标 Mod 库内。 ");
		}
		return directory;
	}

	private static bool IsSafePatchFileName(string requested, string actual)
		=> !string.IsNullOrWhiteSpace(requested)
			&& string.Equals(requested, Path.GetFileName(requested), StringComparison.Ordinal)
			&& string.Equals(requested, actual, StringComparison.OrdinalIgnoreCase);

	private static bool PatchFamiliesEqual(IReadOnlyList<CliCrossArmorPatchFamily> left, IReadOnlyList<CliCrossArmorPatchFamily> right)
		=> left.Count == right.Count
			&& left.Zip(right).All(pair => string.Equals(pair.First.PatchFileName, pair.Second.PatchFileName, StringComparison.OrdinalIgnoreCase)
				&& string.Equals(pair.First.Fingerprint, pair.Second.Fingerprint, StringComparison.OrdinalIgnoreCase));

	private static bool PatchPathSetsEqual(IReadOnlyList<string> left, IReadOnlyList<string> right)
		=> left.Count == right.Count
			&& left.Zip(right.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)).All(pair => string.Equals(Path.GetFullPath(pair.First), Path.GetFullPath(pair.Second), StringComparison.OrdinalIgnoreCase));

	private static async ValueTask<CliCrossArmorGameDataSnapshot> ReadGameDataSnapshotAsync(StoragePaths paths, string gameDataDirectory, CancellationToken cancellationToken)
	{
		var gameData = Path.GetFullPath(gameDataDirectory);
		if (!Directory.Exists(gameData)) return new CliCrossArmorGameDataSnapshot(gameData, "missing", false, string.Empty, null);
		var archiveHashes = File.Exists(paths.ArchiveHashesPath)
			? await File.ReadAllTextAsync(paths.ArchiveHashesPath, cancellationToken).ConfigureAwait(false)
			: "{}";
		var status = await CoreServices.CreateAssetArchiveIndexService(paths).GetIndexStatusAsync(gameData, archiveHashes, cancellationToken).ConfigureAwait(false);
		return new CliCrossArmorGameDataSnapshot(status.GameDataDirectory, status.State.ToString().ToLowerInvariant(), status.IsCurrent, status.CurrentSourceFingerprint, status.StoredFingerprint?.SourceFingerprint);
	}

	private static bool GameDataSnapshotsEqual(CliCrossArmorGameDataSnapshot left, CliCrossArmorGameDataSnapshot right)
		=> string.Equals(left.Directory, right.Directory, StringComparison.OrdinalIgnoreCase)
			&& string.Equals(left.IndexState, right.IndexState, StringComparison.Ordinal)
			&& string.Equals(left.CurrentSourceFingerprint, right.CurrentSourceFingerprint, StringComparison.OrdinalIgnoreCase)
			&& string.Equals(left.IndexedSourceFingerprint, right.IndexedSourceFingerprint, StringComparison.OrdinalIgnoreCase);

	private static string ComputeInputFingerprint(
		CliCrossArmorTransferSource source,
		IReadOnlyList<CliCrossArmorEquipment> targets,
		CliCrossArmorGameDataSnapshot gameData)
	{
		var payload = CliOutput.ToJson(new
		{
			source = new
			{
				source.NodeId,
				source.ModKind,
				source.RelativePath,
				source.ContentGeneration,
				patches = source.PatchFamilies.Select(item => new { item.PatchFileName, item.Fingerprint }).OrderBy(item => item.PatchFileName, StringComparer.Ordinal).ToArray(),
				candidates = source.SourceCandidates.OrderBy(item => item.ArchiveId, StringComparer.Ordinal).ToArray(),
			},
			targets = targets.OrderBy(item => item.ArchiveId, StringComparer.Ordinal).ToArray(),
			gameData = new { gameData.Directory, gameData.IndexState, gameData.CurrentSourceFingerprint, gameData.IndexedSourceFingerprint },
		});
		return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
	}

	private static bool TryParseAssetKey(string value, out AssetKey key)
	{
		key = default;
		var parts = value?.Split('/', StringSplitOptions.TrimEntries) ?? Array.Empty<string>();
		if (parts.Length != 2 || !TryParseHex(parts[0], out var typeId) || !TryParseHex(parts[1], out var fileId)) return false;
		key = new AssetKey(typeId, fileId);
		return true;
	}

	private static bool TryParseHex(string value, out ulong result)
		=> ulong.TryParse(value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out result);

	private static string FormatAssetKey(AssetKey key) => $"0x{key.TypeId:x16}/0x{key.FileId:x16}";

	private static IReadOnlyList<CliCrossArmorTransferDiagnostic> DistinctDiagnostics(IEnumerable<CliCrossArmorTransferDiagnostic> diagnostics)
		=> diagnostics.GroupBy(diagnostic => new { diagnostic.Code, diagnostic.Severity, diagnostic.Message, diagnostic.Path, diagnostic.NodeId })
			.Select(group => group.First()).ToArray();

	private sealed record CrossArmorRequestResolution(
		string? PrimarySourceArchiveId,
		IReadOnlyList<string> AdditionalSourceArchiveIds,
		IReadOnlyList<string> SelectedTargetArchiveIds,
		UnitMeshBodyVariant SourceBodyVariant,
		CrossArmorBodyVariantPreference BodyVariantPreference,
		CrossArmorLayerPreference LayerPreference,
		IReadOnlyList<CrossArmorManualMapping> ManualMappings,
		IReadOnlyList<CrossArmorManualSuppression> ManualSuppressions);
}
