using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HD2ModAdaptation.Analysis;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCli;

/// <summary>
/// An audited, output-only request for appending existing Helmet geometry to
/// existing Armor units.  Candidate IDs are always reconstructed from the
/// current source Mods before output is permitted.
/// </summary>
public sealed record CliHelmetToArmorMergePlanDocument(
	string Schema,
	int Version,
	DateTimeOffset CreatedUtc,
	string InputFingerprint,
	CliHelmetToArmorMergeRequest Request,
	IReadOnlyList<CliHelmetToArmorMergeSource> Sources,
	IReadOnlyList<CliHelmetToArmorMergeCandidate> Candidates,
	IReadOnlyList<CliHelmetToArmorMergeSelection> Selections,
	IReadOnlyList<CliHelmetToArmorMergeDiagnostic> Diagnostics)
{
	public const string CurrentSchema = "hd2mod.helmet-to-armor-merge-plan";
	public const int CurrentVersion = 1;

	public bool IsReady
		=> Selections.Count > 0 && !Diagnostics.Any(diagnostic => diagnostic.Blocking);
}

public sealed record CliHelmetToArmorMergeRequest(
	IReadOnlyList<string> SourceNodeIds,
	string GameDataDirectory);

public sealed record CliHelmetToArmorMergeSource(
	string NodeId,
	string ModName,
	IReadOnlyList<CliHelmetToArmorPatchFamily> PatchFamilies,
	IReadOnlyList<CliHelmetToArmorMergeDiagnostic> Diagnostics);

public sealed record CliHelmetToArmorPatchFamily(
	string PatchFileName,
	string Fingerprint,
	IReadOnlyList<CliHelmetToArmorPatchFamilyFile> Files);

public sealed record CliHelmetToArmorPatchFamilyFile(
	string FileName,
	bool Exists,
	long Length,
	string? Sha256);

public sealed record CliHelmetToArmorMergeCandidate(
	string SelectionId,
	string SourceNodeId,
	string CandidateId,
	string SourcePatchFileName,
	string ArmorArchiveId,
	string HelmetArchiveId,
	string DisplayName,
	IReadOnlyList<CliHelmetToArmorUnitPart> ArmorParts,
	CliHelmetToArmorUnitPart HelmetPart,
	IReadOnlyList<string> SharedArmorArchiveIds,
	IReadOnlyList<string> SharedHelmetArchiveIds,
	bool IsSharedArmorTarget,
	bool IsSharedHelmetTarget);

public sealed record CliHelmetToArmorUnitPart(
	string AssetKey,
	string BodyVariant,
	int MeshInfoIndex,
	string Layer);

public sealed record CliHelmetToArmorMergeSelection(
	string SourceNodeId,
	string CandidateId);

public sealed record CliHelmetToArmorMergeDiagnostic(
	string Code,
	string Severity,
	bool Blocking,
	string Message,
	string? Path = null,
	string? NodeId = null,
	string? Details = null);

internal sealed record CliHelmetToArmorMergeSourceBuild(
	ModNode Node,
	HelmetToArmorMergeInspection Inspection,
	CliHelmetToArmorMergeSource Projection);

internal sealed record CliHelmetToArmorMergeBuildResult(
	CliHelmetToArmorMergePlanDocument Document,
	StoragePaths Paths,
	IReadOnlyDictionary<string, CliHelmetToArmorMergeSourceBuild> SourcesByNodeId,
	IReadOnlyDictionary<string, HelmetToArmorMergeCandidate> CandidatesBySelectionId);

internal sealed record CliHelmetToArmorMergeValidationResult(
	CliHelmetToArmorMergePlanDocument Document,
	CliHelmetToArmorMergeBuildResult? Current,
	IReadOnlyDictionary<ModNodeId, IReadOnlyList<HelmetToArmorMergeCandidate>> ResolvedSelections,
	IReadOnlyList<CliHelmetToArmorMergeDiagnostic> Diagnostics)
{
	public bool IsValid => Current is not null && ResolvedSelections.Count > 0 && !Diagnostics.Any(diagnostic => diagnostic.Blocking);
}

internal static class CliHelmetToArmorMergePlanArtifacts
{
	public static async ValueTask<CliHelmetToArmorMergeBuildResult> BuildAsync(
		CliWorkspace workspace,
		IReadOnlyList<string> sourceNodeSelectors,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		ArgumentNullException.ThrowIfNull(sourceNodeSelectors);
		if (sourceNodeSelectors.Count == 0)
			throw new CliUserException("HelmetMergeSourceRequired", "equipment helmet-merge plan 至少需要一个来源 Mod GUID 或唯一前缀。 ");
		if (string.IsNullOrWhiteSpace(workspace.ManagerRoot))
			throw new CliUserException("HelmetMergeManagerRootRequired", "equipment helmet-merge 需要 --manager-root，以读取装备目录与当前 GameData 索引。 ");
		if (string.IsNullOrWhiteSpace(workspace.LibraryRoot))
			throw new CliUserException("LibraryNotConfigured", "equipment helmet-merge 需要明确的 Mod 库。 ");
		if (string.IsNullOrWhiteSpace(workspace.GameDataRoot))
			throw new CliUserException("HelmetMergeGameDataRequired", "equipment helmet-merge 需要 --game-data <游戏 data 目录>，或可读取的管理器 GameDataFolder 设置。 ");

		var state = await workspace.LoadLibraryAsync(cancellationToken).ConfigureAwait(false);
		var nodes = sourceNodeSelectors
			.Select(selector => CliProjections.ResolveMod(state.Snapshot.Nodes, selector))
			.GroupBy(node => node.Id)
			.Select(group => group.First())
			.OrderBy(node => node.Metadata.Name, StringComparer.OrdinalIgnoreCase)
			.ThenBy(node => node.Id.Value)
			.ToArray();
		var paths = new StoragePaths(workspace.ManagerRoot, workspace.LibraryRoot);
		var gameData = Path.GetFullPath(workspace.GameDataRoot);
		try
		{
			var beforeGameData = await ReadGameDataSnapshotAsync(paths, gameData, cancellationToken).ConfigureAwait(false);
			var sourceBuilds = new List<CliHelmetToArmorMergeSourceBuild>();
			await using var reader = CoreServices.CreateModInformationReader();
			var service = CoreServices.CreateDirectHelmetToArmorMergeService(paths, reader);
			foreach (var node in nodes)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var sourceDirectory = ResolveSourceDirectory(paths, node);
				var beforePatchPaths = EnumerateBasePatchPaths(sourceDirectory);
				var beforePatchFamilies = await ComputePatchFamiliesAsync(beforePatchPaths, cancellationToken).ConfigureAwait(false);
				var inspection = await service.InspectAsync(node, workspace.LibraryRoot, gameData, cancellationToken).ConfigureAwait(false);
				var afterPatchPaths = EnumerateBasePatchPaths(sourceDirectory);
				var afterPatchFamilies = await ComputePatchFamiliesAsync(afterPatchPaths, cancellationToken).ConfigureAwait(false);
				var projectionDiagnostics = inspection.Issues.Select(CreateDiagnostic).ToList();
				if (!PatchFamiliesEqual(beforePatchFamilies, afterPatchFamilies))
				{
					projectionDiagnostics.Add(new CliHelmetToArmorMergeDiagnostic(
						"HelmetMergeSourceChangedDuringInspection",
						"error",
						true,
						"来源 Patch 家族在头盔合并扫描期间发生变化；请等待来源写入完成后重新生成计划。",
						sourceDirectory,
						CliProjections.FormatId(node.Id)));
				}
				if (!PatchPathSetsEqual(afterPatchPaths, inspection.SourcePatchPaths))
				{
					projectionDiagnostics.Add(new CliHelmetToArmorMergeDiagnostic(
						"HelmetMergeSourcePatchSetMismatch",
						"error",
						true,
						"候选扫描读取到的 Patch 集合与当前来源目录不一致；CLI 已拒绝冻结不完整计划。",
						sourceDirectory,
						CliProjections.FormatId(node.Id)));
				}
				var source = new CliHelmetToArmorMergeSource(
					CliProjections.FormatId(node.Id),
					node.Metadata.Name,
					afterPatchFamilies,
					projectionDiagnostics);
				sourceBuilds.Add(new CliHelmetToArmorMergeSourceBuild(node, inspection, source));
			}
			var afterGameData = await ReadGameDataSnapshotAsync(paths, gameData, cancellationToken).ConfigureAwait(false);
			var diagnostics = sourceBuilds.SelectMany(build => build.Projection.Diagnostics).ToList();
			if (!GameDataSnapshotsEqual(beforeGameData, afterGameData))
			{
				diagnostics.Add(new CliHelmetToArmorMergeDiagnostic(
					"HelmetMergeGameDataChangedDuringInspection",
					"error",
					true,
					"GameData 或装备索引输入在头盔合并检查期间发生变化；请重新生成计划。 ",
					gameData));
			}
			if (!afterGameData.IsIndexCurrent)
			{
				diagnostics.Add(new CliHelmetToArmorMergeDiagnostic(
					"HelmetMergeGameDataIndexNotCurrent",
					"error",
					true,
					"当前 GameData 装备目录索引不可用或已过期；请先在管理器设置页重建资产索引。 ",
					gameData));
			}

			var candidates = sourceBuilds
				.SelectMany(build => build.Inspection.Candidates.Select(candidate => CreateCandidate(build.Node, candidate)))
				.OrderBy(candidate => candidate.SourceNodeId, StringComparer.Ordinal)
				.ThenBy(candidate => candidate.DisplayName, StringComparer.OrdinalIgnoreCase)
				.ThenBy(candidate => candidate.CandidateId, StringComparer.Ordinal)
				.ToArray();
			var candidatesById = sourceBuilds
				.SelectMany(build => build.Inspection.Candidates.Select(candidate => (SelectionId: CreateSelectionId(build.Node.Id, candidate.CandidateId), Candidate: candidate)))
				.ToDictionary(item => item.SelectionId, item => item.Candidate, StringComparer.Ordinal);
			var conflicting = FindConflictingSelectionIds(candidates);
			var selections = candidates
				.Where(candidate => !conflicting.Contains(candidate.SelectionId))
				.Select(candidate => new CliHelmetToArmorMergeSelection(candidate.SourceNodeId, candidate.CandidateId))
				.ToArray();
			if (conflicting.Count != 0)
			{
				diagnostics.Add(new CliHelmetToArmorMergeDiagnostic(
					"HelmetMergeConflictingCandidatesOmitted",
					"warning",
					false,
					$"已从默认选择中移除 {conflicting.Count} 个会对同一胸部 Mesh 追加不同头盔的候选。需要时请在 selections 中明确保留其中一个候选，再执行 validate。"));
			}
			if (candidates.Length == 0)
			{
				diagnostics.Add(new CliHelmetToArmorMergeDiagnostic(
					"HelmetMergeCandidatesMissing",
					"error",
					true,
					"所选来源中没有可用于头盔合并的完整 Armor/Helmet 模型集合。"));
			}
			if (selections.Length == 0 && candidates.Length != 0)
			{
				diagnostics.Add(new CliHelmetToArmorMergeDiagnostic(
					"HelmetMergeSelectionRequired",
					"warning",
					false,
					"没有安全的默认候选。请从 candidates 中选择不会冲突的一组或多组候选。"));
			}

			var request = new CliHelmetToArmorMergeRequest(nodes.Select(node => CliProjections.FormatId(node.Id)).ToArray(), gameData);
			var sources = sourceBuilds.Select(build => build.Projection).ToArray();
			var document = new CliHelmetToArmorMergePlanDocument(
				CliHelmetToArmorMergePlanDocument.CurrentSchema,
				CliHelmetToArmorMergePlanDocument.CurrentVersion,
				DateTimeOffset.UtcNow,
				ComputeInputFingerprint(request, sources, candidates, afterGameData),
				request,
				sources,
				candidates,
				selections,
				diagnostics);
			return new CliHelmetToArmorMergeBuildResult(
				document,
				paths,
				sourceBuilds.ToDictionary(build => CliProjections.FormatId(build.Node.Id), StringComparer.Ordinal),
				candidatesById);
		}
		catch (CliUserException) { throw; }
		catch (Exception exception) when (exception is IOException
			or UnauthorizedAccessException
			or InvalidDataException
			or InvalidOperationException
			or DirectoryNotFoundException
			or FileNotFoundException)
		{
			throw new CliUserException("HelmetMergeInspectionFailed", $"无法完成头盔合并检查：{exception.Message}");
		}
	}

	public static async ValueTask<CliHelmetToArmorMergePlanDocument> ReadDocumentAsync(string path, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(path))
			throw new CliUserException("HelmetMergePlanRequired", "请通过 --plan <头盔合并计划.json> 提供计划文件。 ");
		var fullPath = CliOutput.NormalizeDirectoryPath(path, "HelmetMergePlanPathInvalid");
		if (!File.Exists(fullPath))
			throw new CliUserException("HelmetMergePlanNotFound", $"头盔合并计划不存在：{fullPath}");
		try
		{
			var json = await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
			var document = JsonSerializer.Deserialize<CliHelmetToArmorMergePlanDocument>(json, CliOutput.JsonOptions);
			if (document is null)
				throw new CliUserException("HelmetMergePlanUnreadable", "头盔合并计划为空或无法解析。 ");
			if (!string.Equals(document.Schema, CliHelmetToArmorMergePlanDocument.CurrentSchema, StringComparison.Ordinal)
				|| document.Version != CliHelmetToArmorMergePlanDocument.CurrentVersion)
			{
				throw new CliUserException("HelmetMergePlanVersionUnsupported", $"头盔合并计划版本不受支持。需要 {CliHelmetToArmorMergePlanDocument.CurrentSchema} v{CliHelmetToArmorMergePlanDocument.CurrentVersion}。 ");
			}
			if (string.IsNullOrWhiteSpace(document.InputFingerprint)
				|| document.Request is null
				|| document.Request.SourceNodeIds is null
				|| document.Request.SourceNodeIds.Count == 0
				|| string.IsNullOrWhiteSpace(document.Request.GameDataDirectory)
				|| document.Sources is null
				|| document.Candidates is null
				|| document.Selections is null
				|| document.Diagnostics is null)
			{
				throw new CliUserException("HelmetMergePlanUnreadable", "头盔合并计划缺少来源、候选或选择信息。 ");
			}
			return document;
		}
		catch (CliUserException) { throw; }
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
		{
			throw new CliUserException("HelmetMergePlanUnreadable", $"无法读取头盔合并计划：{exception.Message}");
		}
	}

	public static async ValueTask<CliHelmetToArmorMergeValidationResult> ValidateAsync(
		CliHelmetToArmorMergePlanDocument document,
		CliWorkspace workspace,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(document);
		ArgumentNullException.ThrowIfNull(workspace);
		var diagnostics = new List<CliHelmetToArmorMergeDiagnostic>();
		CliHelmetToArmorMergeBuildResult? current = null;
		try
		{
			if (string.IsNullOrWhiteSpace(workspace.GameDataRoot)
				|| !string.Equals(Path.GetFullPath(workspace.GameDataRoot), Path.GetFullPath(document.Request.GameDataDirectory), StringComparison.OrdinalIgnoreCase))
			{
				diagnostics.Add(new CliHelmetToArmorMergeDiagnostic(
					"HelmetMergeGameDataTargetChanged", "error", true,
					"当前 --game-data 与计划固定的 GameData 目录不一致；请在同一目标上重新生成计划。"));
				return new CliHelmetToArmorMergeValidationResult(document, null, new Dictionary<ModNodeId, IReadOnlyList<HelmetToArmorMergeCandidate>>(), diagnostics);
			}
			current = await BuildAsync(workspace, document.Request.SourceNodeIds, cancellationToken).ConfigureAwait(false);
		}
		catch (CliUserException exception)
		{
			diagnostics.Add(new CliHelmetToArmorMergeDiagnostic(exception.Code, "error", true, exception.Message));
			return new CliHelmetToArmorMergeValidationResult(document, null, new Dictionary<ModNodeId, IReadOnlyList<HelmetToArmorMergeCandidate>>(), diagnostics);
		}

		if (!string.Equals(document.InputFingerprint, current.Document.InputFingerprint, StringComparison.OrdinalIgnoreCase))
		{
			diagnostics.Add(new CliHelmetToArmorMergeDiagnostic(
				"HelmetMergeInputChanged", "error", true,
				"来源 Patch、候选结构、GameData 指纹或装备索引已变化；请重新生成计划并重新审阅选择。"));
		}
		diagnostics.AddRange(current.Document.Diagnostics.Where(diagnostic => diagnostic.Blocking));
		var selections = ResolveSelections(document, current, diagnostics);
		return new CliHelmetToArmorMergeValidationResult(document, current, selections, diagnostics);
	}

	private static IReadOnlyDictionary<ModNodeId, IReadOnlyList<HelmetToArmorMergeCandidate>> ResolveSelections(
		CliHelmetToArmorMergePlanDocument document,
		CliHelmetToArmorMergeBuildResult current,
		ICollection<CliHelmetToArmorMergeDiagnostic> diagnostics)
	{
		if (document.Selections.Count == 0)
		{
			diagnostics.Add(new CliHelmetToArmorMergeDiagnostic("HelmetMergeSelectionRequired", "error", true, "计划没有选择任何模型集合。 "));
			return new Dictionary<ModNodeId, IReadOnlyList<HelmetToArmorMergeCandidate>>();
		}
		var result = new Dictionary<ModNodeId, List<HelmetToArmorMergeCandidate>>();
		var seen = new HashSet<string>(StringComparer.Ordinal);
		foreach (var selection in document.Selections)
		{
			var selectionId = CreateSelectionId(selection.SourceNodeId, selection.CandidateId);
			if (!seen.Add(selectionId))
			{
				diagnostics.Add(new CliHelmetToArmorMergeDiagnostic("HelmetMergeSelectionDuplicate", "error", true, $"计划重复选择了同一模型集合：{selectionId}。"));
				continue;
			}
			if (!current.CandidatesBySelectionId.TryGetValue(selectionId, out var candidate))
			{
				diagnostics.Add(new CliHelmetToArmorMergeDiagnostic("HelmetMergeCandidateNotFound", "error", true, $"计划选择的模型集合已不在当前来源中：{selectionId}。"));
				continue;
			}
			if (!Guid.TryParse(selection.SourceNodeId, out var rawNodeId))
			{
				diagnostics.Add(new CliHelmetToArmorMergeDiagnostic("HelmetMergeSourceIdInvalid", "error", true, $"计划来源 ID 无效：{selection.SourceNodeId}。"));
				continue;
			}
			var nodeId = new ModNodeId(rawNodeId);
			if (!result.TryGetValue(nodeId, out var candidates)) result[nodeId] = candidates = [];
			candidates.Add(candidate);
		}

		foreach (var group in result)
		{
			var projected = group.Value.Select(candidate => CreateCandidate(current.SourcesByNodeId[CliProjections.FormatId(group.Key)].Node, candidate)).ToArray();
			if (FindConflictingSelectionIds(projected).Count != 0)
			{
				diagnostics.Add(new CliHelmetToArmorMergeDiagnostic(
					"HelmetMergeTargetConflict",
					"error",
					true,
					$"来源 {CliProjections.FormatId(group.Key)} 的已选候选会向同一胸部 Mesh 追加不同头盔；请每个目标只保留一个候选。"));
			}

			foreach (var patchGroup in group.Value.GroupBy(candidate => candidate.SourcePatchFileName, StringComparer.OrdinalIgnoreCase))
			{
				var armorKeys = patchGroup
					.SelectMany(candidate => candidate.ArmorParts)
					.Select(part => part.UnitAssetKey)
					.ToHashSet();
				var overlappingHelmetKeys = patchGroup
					.Select(candidate => candidate.HelmetPart.UnitAssetKey)
					.Where(armorKeys.Contains)
					.Distinct()
					.OrderBy(key => key.FileId)
					.ToArray();
				if (overlappingHelmetKeys.Length == 0) continue;
				var names = string.Join(", ", overlappingHelmetKeys.Select(key => $"0x{key.FileId:x16}"));
				diagnostics.Add(new CliHelmetToArmorMergeDiagnostic(
					"HelmetMergeArmorHelmetUnitOverlap",
					"error",
					true,
					$"来源 {CliProjections.FormatId(group.Key)} / {patchGroup.Key} 中，选中的胸部 Unit 同时也是待隐藏的头盔 Unit（{names}）。此组合会让一次 Patch 编辑互相覆盖，CLI 已拒绝执行。"));
			}
		}

		return result.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<HelmetToArmorMergeCandidate>)pair.Value.ToArray());
	}

	internal static async ValueTask<HelmetToArmorMergeResult> ExecuteAsync(
		CliHelmetToArmorMergeBuildResult build,
		ModNodeId nodeId,
		IReadOnlyList<HelmetToArmorMergeCandidate> selectedCandidates,
		string outputDirectory,
		CancellationToken cancellationToken = default)
	{
		if (!build.SourcesByNodeId.TryGetValue(CliProjections.FormatId(nodeId), out var source))
			throw new CliUserException("HelmetMergeSourceNotFound", $"计划来源已不存在：{CliProjections.FormatId(nodeId)}。 ");
		await using var reader = CoreServices.CreateModInformationReader();
		var service = CoreServices.CreateDirectHelmetToArmorMergeService(
			source.Projection.PatchFamilies.Count == 0
				? throw new CliUserException("HelmetMergeSourcePatchMissing", "来源 Mod 没有可处理的 Patch。")
				: build.Paths,
			reader);
		return await service.ExecuteAsync(
			source.Node,
			ResolveSourceDirectory(build, source),
			outputDirectory,
			build.Document.Request.GameDataDirectory,
			selectedCandidates,
			cancellationToken).ConfigureAwait(false);
	}

	private static string ResolveSourceDirectory(CliHelmetToArmorMergeBuildResult build, CliHelmetToArmorMergeSourceBuild source)
		=> ResolveSourceDirectory(build.Paths, source.Node);

	private static string ResolveSourceDirectory(StoragePaths paths, ModNode source)
	{
		var root = Path.GetFullPath(paths.ModsDirectory)
			.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		var directory = Path.GetFullPath(Path.Combine(root, source.RelativePath));
		if (string.Equals(directory, root, StringComparison.OrdinalIgnoreCase)
			|| (!directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
				&& !directory.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
		{
			throw new CliUserException("HelmetMergeSourcePathInvalid", "当前 Mod 的存储路径不在目标 Mod 库内。 ");
		}
		return directory;
	}

	private static CliHelmetToArmorMergeCandidate CreateCandidate(ModNode source, HelmetToArmorMergeCandidate candidate)
	{
		var sourceId = CliProjections.FormatId(source.Id);
		return new CliHelmetToArmorMergeCandidate(
			CreateSelectionId(source.Id, candidate.CandidateId),
			sourceId,
			candidate.CandidateId,
			candidate.SourcePatchFileName,
			candidate.ArmorArchiveId,
			candidate.HelmetArchiveId,
			candidate.DisplayName,
			candidate.ArmorParts.Select(CreateUnitPart).ToArray(),
			CreateUnitPart(candidate.HelmetPart),
			CollectSharedArchiveIds(candidate.ArmorArchiveId, candidate.ArmorParts),
			CollectSharedArchiveIds(candidate.HelmetArchiveId, [candidate.HelmetPart]),
			CollectSharedArchiveIds(candidate.ArmorArchiveId, candidate.ArmorParts).Count > 1,
			CollectSharedArchiveIds(candidate.HelmetArchiveId, [candidate.HelmetPart]).Count > 1);
	}

	private static IReadOnlyList<string> CollectSharedArchiveIds(string ownerArchiveId, IEnumerable<EquipmentUnitPart> parts)
		=> parts
			.SelectMany(part => part.SharedArchiveIds)
			.Append(ownerArchiveId)
			.Where(id => !string.IsNullOrWhiteSpace(id))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
			.ToArray();

	private static CliHelmetToArmorUnitPart CreateUnitPart(EquipmentUnitPart part)
		=> new($"0x{part.UnitAssetKey.TypeId:x16}/0x{part.UnitAssetKey.FileId:x16}", part.BodyVariant.ToString(), part.MeshInfoIndex, part.Layer.ToString());

	private static HashSet<string> FindConflictingSelectionIds(IReadOnlyList<CliHelmetToArmorMergeCandidate> candidates)
	{
		var conflicts = new HashSet<string>(StringComparer.Ordinal);
		foreach (var group in candidates
			.SelectMany(candidate => candidate.ArmorParts.Select(part => new
			{
				candidate.SelectionId,
				candidate.SourceNodeId,
				candidate.SourcePatchFileName,
				part.AssetKey,
				part.MeshInfoIndex,
				HelmetKey = candidate.HelmetPart.AssetKey,
			}))
			.GroupBy(item => (item.SourceNodeId, item.SourcePatchFileName, item.AssetKey, item.MeshInfoIndex)))
		{
			if (group.Select(item => item.HelmetKey).Distinct(StringComparer.Ordinal).Count() <= 1) continue;
			foreach (var item in group) conflicts.Add(item.SelectionId);
		}
		return conflicts;
	}

	private static async ValueTask<IReadOnlyList<CliHelmetToArmorPatchFamily>> ComputePatchFamiliesAsync(
		IReadOnlyList<string> sourcePatchPaths,
		CancellationToken cancellationToken)
	{
		var families = new List<CliHelmetToArmorPatchFamily>();
		foreach (var patch in sourcePatchPaths.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
		{
			var files = new List<CliHelmetToArmorPatchFamilyFile>();
			foreach (var path in new[] { patch, patch + ".stream", patch + ".gpu_resources" })
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (!File.Exists(path))
				{
					files.Add(new CliHelmetToArmorPatchFamilyFile(Path.GetFileName(path), false, 0, null));
					continue;
				}
				var info = new FileInfo(path);
				await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 128, FileOptions.SequentialScan);
				files.Add(new CliHelmetToArmorPatchFamilyFile(Path.GetFileName(path), true, info.Length, Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false))));
			}
			families.Add(new CliHelmetToArmorPatchFamily(
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

	private static bool PatchFamiliesEqual(
		IReadOnlyList<CliHelmetToArmorPatchFamily> left,
		IReadOnlyList<CliHelmetToArmorPatchFamily> right)
		=> left.Count == right.Count
			&& left.Zip(right).All(pair => string.Equals(pair.First.PatchFileName, pair.Second.PatchFileName, StringComparison.OrdinalIgnoreCase)
				&& string.Equals(pair.First.Fingerprint, pair.Second.Fingerprint, StringComparison.OrdinalIgnoreCase));

	private static bool PatchPathSetsEqual(IReadOnlyList<string> left, IReadOnlyList<string> right)
		=> left.Count == right.Count
			&& left.Zip(right.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)).All(pair => string.Equals(
				Path.GetFullPath(pair.First),
				Path.GetFullPath(pair.Second),
				StringComparison.OrdinalIgnoreCase));

	private static async ValueTask<CliHelmetToArmorGameDataSnapshot> ReadGameDataSnapshotAsync(StoragePaths paths, string gameDataDirectory, CancellationToken cancellationToken)
	{
		var gameData = Path.GetFullPath(gameDataDirectory);
		if (!Directory.Exists(gameData)) return new CliHelmetToArmorGameDataSnapshot(gameData, "missing", false, string.Empty, null);
		var archiveHashes = File.Exists(paths.ArchiveHashesPath)
			? await File.ReadAllTextAsync(paths.ArchiveHashesPath, cancellationToken).ConfigureAwait(false)
			: "{}";
		var status = await CoreServices.CreateAssetArchiveIndexService(paths).GetIndexStatusAsync(gameData, archiveHashes, cancellationToken).ConfigureAwait(false);
		return new CliHelmetToArmorGameDataSnapshot(status.GameDataDirectory, status.State.ToString().ToLowerInvariant(), status.IsCurrent, status.CurrentSourceFingerprint, status.StoredFingerprint?.SourceFingerprint);
	}

	private static bool GameDataSnapshotsEqual(CliHelmetToArmorGameDataSnapshot left, CliHelmetToArmorGameDataSnapshot right)
		=> string.Equals(left.Directory, right.Directory, StringComparison.OrdinalIgnoreCase)
			&& string.Equals(left.IndexState, right.IndexState, StringComparison.Ordinal)
			&& string.Equals(left.CurrentSourceFingerprint, right.CurrentSourceFingerprint, StringComparison.OrdinalIgnoreCase)
			&& string.Equals(left.IndexedSourceFingerprint, right.IndexedSourceFingerprint, StringComparison.OrdinalIgnoreCase);

	private static CliHelmetToArmorMergeDiagnostic CreateDiagnostic(CoreIssue issue)
		=> new(issue.Code, issue.Severity == CoreIssueSeverity.Error ? "error" : "warning", issue.Severity == CoreIssueSeverity.Error, issue.Message, issue.FilePath, issue.NodeId is { } nodeId ? CliProjections.FormatId(nodeId) : null, issue.ExceptionMessage);

	private static string ComputeInputFingerprint(CliHelmetToArmorMergeRequest request, IReadOnlyList<CliHelmetToArmorMergeSource> sources, IReadOnlyList<CliHelmetToArmorMergeCandidate> candidates, CliHelmetToArmorGameDataSnapshot gameData)
	{
		var payload = CliOutput.ToJson(new
		{
			sourceNodeIds = request.SourceNodeIds.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
			gameData = new { gameData.Directory, gameData.IndexState, gameData.CurrentSourceFingerprint, gameData.IndexedSourceFingerprint },
			sources = sources.Select(source => new
			{
				source.NodeId,
				patches = source.PatchFamilies.Select(patch => new { patch.PatchFileName, patch.Fingerprint }).OrderBy(patch => patch.PatchFileName, StringComparer.Ordinal).ToArray(),
			}).OrderBy(source => source.NodeId, StringComparer.Ordinal).ToArray(),
			candidates = candidates.Select(candidate => new
			{
				candidate.SelectionId,
				candidate.SourcePatchFileName,
				candidate.ArmorArchiveId,
				candidate.HelmetArchiveId,
				armor = candidate.ArmorParts.Select(part => new { part.AssetKey, part.BodyVariant, part.MeshInfoIndex, part.Layer }).OrderBy(part => part.AssetKey, StringComparer.Ordinal).ToArray(),
				helmet = new { candidate.HelmetPart.AssetKey, candidate.HelmetPart.MeshInfoIndex, candidate.HelmetPart.Layer },
				candidate.SharedArmorArchiveIds,
				candidate.SharedHelmetArchiveIds,
			}).OrderBy(candidate => candidate.SelectionId, StringComparer.Ordinal).ToArray(),
		});
		return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
	}

	private static string CreateSelectionId(ModNodeId nodeId, string candidateId)
		=> CreateSelectionId(CliProjections.FormatId(nodeId), candidateId);

	private static string CreateSelectionId(string sourceNodeId, string candidateId)
		=> $"{sourceNodeId}|{candidateId}";
}

public sealed record CliHelmetToArmorGameDataSnapshot(
	string Directory,
	string IndexState,
	bool IsIndexCurrent,
	string CurrentSourceFingerprint,
	string? IndexedSourceFingerprint);
