using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HD2ModCore.Application;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCli;

/// <summary>
/// A reviewed batch of conservative Same-key repairs. The Mod list and all
/// source Patch file hashes are frozen; only Selected is intentionally
/// editable by an Agent before validate/apply.
/// </summary>
public sealed record CliBatchPatchRepairPlanDocument(
	string Schema,
	int Version,
	DateTimeOffset CreatedUtc,
	string InputFingerprint,
	string ManagerRoot,
	string LibraryRoot,
	CliBatchPatchRepairGameDataSnapshot GameData,
	IReadOnlyList<CliBatchPatchRepairMod> Mods,
	IReadOnlyList<CliBatchPatchRepairDiagnostic> Diagnostics)
{
	public const string CurrentSchema = "hd2mod.batch-patch-repair-plan";
	public const int CurrentVersion = 1;

	public bool IsReady
		=> !Diagnostics.Any(diagnostic => diagnostic.Blocking)
			&& Mods.All(mod => !mod.Diagnostics.Any(diagnostic => diagnostic.Blocking));

	public IReadOnlyList<string> SelectedNodeIds
		=> Mods.Where(mod => mod.Selected).Select(mod => mod.NodeId).ToArray();
}

public sealed record CliBatchPatchRepairGameDataSnapshot(
	string Directory,
	string IndexState,
	bool IsIndexCurrent,
	string CurrentSourceFingerprint,
	string? IndexedSourceFingerprint);

public sealed record CliBatchPatchRepairMod(
	string NodeId,
	string Name,
	string RelativePath,
	bool Selected,
	bool IsOutdated,
	string CompatibilityStatus,
	int UnitCount,
	int ReadableUnitCount,
	int OutdatedUnitCount,
	bool CanAttemptGenerate,
	string SourceFingerprint,
	IReadOnlyList<CliBatchPatchRepairFile> SourceFiles,
	IReadOnlyList<CliBatchPatchRepairUnitEvidence> Evidence,
	IReadOnlyList<CliBatchPatchRepairDiagnostic> Diagnostics);

public sealed record CliBatchPatchRepairFile(
	string FileName,
	string ArchiveHex16,
	int PatchIndex,
	string SidecarKind,
	long Length,
	string Sha256);

public sealed record CliBatchPatchRepairUnitEvidence(
	string PatchFileName,
	string AssetKey,
	uint? Version,
	string? ReadError);

public sealed record CliBatchPatchRepairDiagnostic(
	string Code,
	string Severity,
	bool Blocking,
	string Message,
	string? Path = null,
	string? NodeId = null,
	string? Details = null);

internal sealed record CliBatchPatchRepairSourceSnapshot(
	IReadOnlyList<CliBatchPatchRepairFile> Files,
	IReadOnlyList<PatchGroupKey> PatchGroups,
	string Fingerprint);

internal sealed record CliBatchPatchRepairBuildResult(
	CliBatchPatchRepairPlanDocument Document,
	LibrarySnapshot Snapshot,
	StoragePaths Paths);

internal sealed record CliBatchPatchRepairValidationResult(
	CliBatchPatchRepairPlanDocument Document,
	CliBatchPatchRepairBuildResult? Current,
	IReadOnlyList<CliBatchPatchRepairDiagnostic> Diagnostics)
{
	public bool IsValid => Current is not null && !Diagnostics.Any(diagnostic => diagnostic.Blocking);
}

/// <summary>
/// Builds and validates a complete batch-repair plan without modifying the
/// library, source Patch files, information caches or GameData.
/// </summary>
internal static class CliBatchPatchRepairPlanArtifacts
{
	public static async ValueTask<CliBatchPatchRepairBuildResult> BuildAsync(
		CliWorkspace workspace,
		IReadOnlyList<string>? nodeSelectors = null,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		if (string.IsNullOrWhiteSpace(workspace.ManagerRoot))
			throw new CliUserException("BatchRepairManagerRootRequired", "patch repair batch 需要 --manager-root，以验证管理器进程、库写租约与库状态。 ");
		if (string.IsNullOrWhiteSpace(workspace.LibraryRoot))
			throw new CliUserException("LibraryNotConfigured", "patch repair batch 需要明确的 Mod 库。请传入 --library 或 --manager-root。 ");
		if (string.IsNullOrWhiteSpace(workspace.GameDataRoot))
			throw new CliUserException("BatchRepairGameDataRequired", "patch repair batch 需要 --game-data <游戏 data 目录>，或可读取的管理器 GameDataFolder 设置。 ");

		var libraryState = await workspace.LoadLibraryAsync(cancellationToken).ConfigureAwait(false);
		var nodes = ResolveNodes(libraryState.Snapshot, nodeSelectors);
		var paths = new StoragePaths(workspace.ManagerRoot, workspace.LibraryRoot);
		var diagnostics = new List<CliBatchPatchRepairDiagnostic>();
		var gameDataBefore = await ReadGameDataSnapshotAsync(paths, workspace.GameDataRoot, diagnostics, cancellationToken).ConfigureAwait(false);

		await using var reader = CoreServices.CreateModInformationReader();
		var versionProducer = CoreServices.CreateUnitVersionInformationProducer(reader);
		var reconstruction = CoreServices.CreateModSameKeyReconstructionService(paths, reader);
		var modDocuments = new List<CliBatchPatchRepairMod>(nodes.Count);

		foreach (var node in nodes)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var sourceDirectory = ResolveSourceDirectory(workspace.LibraryRoot, node.RelativePath);
			var source = await CaptureSourceAsync(sourceDirectory, cancellationToken).ConfigureAwait(false);
			var modDiagnostics = new List<CliBatchPatchRepairDiagnostic>();
			if (source.Files.Count == 0)
			{
				modDiagnostics.Add(new CliBatchPatchRepairDiagnostic(
					"BatchRepairNoPatchFiles",
					"warning",
					false,
					"当前 Mod 没有可识别的 Patch 文件；本次不会尝试修复。",
					sourceDirectory,
					CliProjections.FormatId(node.Id)));
			}

			ModUnitCompatibilityReport report;
			IReadOnlyList<UnitVersionEvidence> evidence;
			try
			{
				var facts = await versionProducer.ProduceAsync(node, workspace.LibraryRoot, cancellationToken).ConfigureAwait(false);
				report = facts.Report;
				evidence = report.Evidence;
				modDiagnostics.AddRange(facts.Issues.Select(issue => CreateModDiagnostic(issue, node)));
			}
			catch (Exception exception) when (exception is IOException
				or UnauthorizedAccessException
				or InvalidDataException
				or InvalidOperationException
				or DirectoryNotFoundException
				or FileNotFoundException)
			{
				report = new ModUnitCompatibilityReport(UnitCompatibilityStatus.Unreadable, 0, 0, 0, Array.Empty<UnitVersionEvidence>());
				evidence = Array.Empty<UnitVersionEvidence>();
				modDiagnostics.Add(new CliBatchPatchRepairDiagnostic(
					"BatchRepairUnitVersionInspectionFailed",
					"warning",
					false,
					$"Unit 版本检查失败，本次跳过该 Mod：{exception.Message}",
					sourceDirectory,
					CliProjections.FormatId(node.Id),
					exception.ToString()));
			}

			var selected = report.IsOutdated;
			var canAttemptGenerate = false;
			if (selected && gameDataBefore.IsIndexCurrent)
			{
				try
				{
					var state = await reconstruction.InspectAsync(
						node,
						workspace.LibraryRoot,
						gameDataBefore.Directory,
						cancellationToken).ConfigureAwait(false);
					canAttemptGenerate = state.CanWrite;
					modDiagnostics.AddRange(state.Issues.Select(issue => CreateEligibilityDiagnostic(issue, node)));
					if (!canAttemptGenerate)
						modDiagnostics.Add(new CliBatchPatchRepairDiagnostic(
							"BatchRepairNotRepairable",
							"warning",
							false,
							"已确认 Unit 过时，但当前 Mod 未通过 Canonical 重构资格检查；apply 会跳过它。",
							sourceDirectory,
							CliProjections.FormatId(node.Id)));
				}
				catch (Exception exception) when (exception is IOException
					or UnauthorizedAccessException
					or InvalidDataException
					or InvalidOperationException
					or DirectoryNotFoundException
					or FileNotFoundException)
				{
					modDiagnostics.Add(new CliBatchPatchRepairDiagnostic(
						"BatchRepairEligibilityInspectionFailed",
						"warning",
						false,
						$"Canonical 修复资格检查失败，本次将由 apply 再次处理并在失败时跳过：{exception.Message}",
						sourceDirectory,
						CliProjections.FormatId(node.Id),
						exception.ToString()));
				}
			}
			else if (selected)
			{
				modDiagnostics.Add(new CliBatchPatchRepairDiagnostic(
					"BatchRepairGameDataUnavailable",
					"warning",
					false,
					"已确认 Unit 过时，但 GameData 索引当前不可用；请先维护索引后再 apply。",
					gameDataBefore.Directory,
					CliProjections.FormatId(node.Id)));
			}

			if (report.Status == UnitCompatibilityStatus.Unreadable)
			{
				modDiagnostics.Add(new CliBatchPatchRepairDiagnostic(
					"BatchRepairUnitVersionUnreadable",
					"warning",
					false,
					"Unit 版本无法完整确认，本次不会默认选择该 Mod。",
					sourceDirectory,
					CliProjections.FormatId(node.Id)));
			}

			var afterSource = await CaptureSourceAsync(sourceDirectory, cancellationToken).ConfigureAwait(false);
			if (!string.Equals(source.Fingerprint, afterSource.Fingerprint, StringComparison.OrdinalIgnoreCase))
			{
				modDiagnostics.Add(new CliBatchPatchRepairDiagnostic(
					"BatchRepairSourceChangedDuringInspection",
					"error",
					true,
					"来源 Patch 在 Unit/资格扫描期间发生变化；请等待写入结束后重新生成计划。",
					sourceDirectory,
					CliProjections.FormatId(node.Id)));
			}
			source = afterSource;

			modDocuments.Add(new CliBatchPatchRepairMod(
				CliProjections.FormatId(node.Id),
				node.Metadata.Name,
				node.RelativePath,
				selected,
				report.IsOutdated,
				report.Status.ToString(),
				report.UnitCount,
				report.ReadableUnitCount,
				report.OutdatedUnitCount,
				canAttemptGenerate,
				source.Fingerprint,
				source.Files,
				evidence.Select(item => new CliBatchPatchRepairUnitEvidence(
					item.PatchFileName,
					FormatAssetKey(item.UnitAssetKey),
					item.Version,
					item.ReadError)).ToArray(),
				DistinctDiagnostics(modDiagnostics)));
		}

		var gameDataAfter = await ReadGameDataSnapshotAsync(paths, workspace.GameDataRoot, diagnostics, cancellationToken).ConfigureAwait(false);
		if (!GameDataSnapshotsEqual(gameDataBefore, gameDataAfter))
		{
			diagnostics.Add(new CliBatchPatchRepairDiagnostic(
				"BatchRepairGameDataChangedDuringInspection",
				"error",
				true,
				"GameData 或其资产索引输入在批量扫描期间发生变化；请重新生成计划。",
				gameDataAfter.Directory));
		}
		var stableMods = modDocuments
			.OrderBy(mod => mod.Name, StringComparer.OrdinalIgnoreCase)
			.ThenBy(mod => mod.NodeId, StringComparer.Ordinal)
			.ToArray();
		var document = new CliBatchPatchRepairPlanDocument(
			CliBatchPatchRepairPlanDocument.CurrentSchema,
			CliBatchPatchRepairPlanDocument.CurrentVersion,
			DateTimeOffset.UtcNow,
			ComputeInputFingerprint(workspace.ManagerRoot, workspace.LibraryRoot, gameDataAfter, stableMods),
			Path.GetFullPath(workspace.ManagerRoot),
			Path.GetFullPath(workspace.LibraryRoot),
			gameDataAfter,
			stableMods,
			DistinctDiagnostics(diagnostics));
		return new CliBatchPatchRepairBuildResult(document, libraryState.Snapshot, paths);
	}

	public static async ValueTask<CliBatchPatchRepairPlanDocument> ReadDocumentAsync(
		string path,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(path))
			throw new CliUserException("BatchRepairPlanRequired", "请通过 --plan <批量 Patch 修复计划.json> 提供计划文件。 ");
		var fullPath = CliOutput.NormalizeDirectoryPath(path, "BatchRepairPlanPathInvalid");
		if (!File.Exists(fullPath))
			throw new CliUserException("BatchRepairPlanNotFound", $"批量 Patch 修复计划不存在：{fullPath}");
		try
		{
			var json = await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
			var document = JsonSerializer.Deserialize<CliBatchPatchRepairPlanDocument>(json, CliOutput.JsonOptions);
			if (document is null)
				throw new CliUserException("BatchRepairPlanUnreadable", "批量 Patch 修复计划为空或无法解析。 ");
			if (!string.Equals(document.Schema, CliBatchPatchRepairPlanDocument.CurrentSchema, StringComparison.Ordinal)
				|| document.Version != CliBatchPatchRepairPlanDocument.CurrentVersion)
			{
				throw new CliUserException(
					"BatchRepairPlanVersionUnsupported",
					$"批量 Patch 修复计划版本不受支持。需要 {CliBatchPatchRepairPlanDocument.CurrentSchema} v{CliBatchPatchRepairPlanDocument.CurrentVersion}；请重新生成计划。 ");
			}
			if (string.IsNullOrWhiteSpace(document.InputFingerprint)
				|| string.IsNullOrWhiteSpace(document.ManagerRoot)
				|| string.IsNullOrWhiteSpace(document.LibraryRoot)
				|| document.GameData is null
				|| string.IsNullOrWhiteSpace(document.GameData.Directory)
				|| document.Mods is null
				|| document.Diagnostics is null
				|| document.Mods.Count != document.Mods.Select(mod => mod.NodeId).Distinct(StringComparer.OrdinalIgnoreCase).Count()
				|| document.Mods.Any(mod => string.IsNullOrWhiteSpace(mod.NodeId)
					|| !Guid.TryParse(mod.NodeId, out _)
					|| string.IsNullOrWhiteSpace(mod.RelativePath)
					|| string.IsNullOrWhiteSpace(mod.SourceFingerprint)
					|| mod.SourceFiles is null
					|| mod.Evidence is null
					|| mod.Diagnostics is null))
			{
				throw new CliUserException("BatchRepairPlanUnreadable", "批量 Patch 修复计划缺少目标、GameData、Mod 或诊断信息，或包含重复 Mod。 ");
			}
			return document;
		}
		catch (CliUserException) { throw; }
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
		{
			throw new CliUserException("BatchRepairPlanUnreadable", $"无法读取批量 Patch 修复计划：{exception.Message}");
		}
	}

	public static async ValueTask<CliBatchPatchRepairValidationResult> ValidateAsync(
		CliBatchPatchRepairPlanDocument document,
		CliWorkspace workspace,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(document);
		ArgumentNullException.ThrowIfNull(workspace);
		var diagnostics = new List<CliBatchPatchRepairDiagnostic>();
		if (!PathsEqual(document.ManagerRoot, workspace.ManagerRoot)
			|| !PathsEqual(document.LibraryRoot, workspace.LibraryRoot)
			|| string.IsNullOrWhiteSpace(workspace.GameDataRoot)
			|| !PathsEqual(document.GameData.Directory, workspace.GameDataRoot))
		{
			diagnostics.Add(new CliBatchPatchRepairDiagnostic(
				"BatchRepairTargetChanged",
				"error",
				true,
				"当前管理器、Mod 库或 GameData 目标与计划不同；请重新生成计划。 "));
			return new CliBatchPatchRepairValidationResult(document, null, diagnostics);
		}

		CliBatchPatchRepairBuildResult? current = null;
		try
		{
			current = await BuildAsync(
				workspace,
				document.Mods.Select(mod => mod.NodeId).ToArray(),
				cancellationToken).ConfigureAwait(false);
		}
		catch (CliUserException exception)
		{
			diagnostics.Add(new CliBatchPatchRepairDiagnostic(exception.Code, "error", true, exception.Message));
			return new CliBatchPatchRepairValidationResult(document, null, diagnostics);
		}

		if (!string.Equals(document.InputFingerprint, current.Document.InputFingerprint, StringComparison.OrdinalIgnoreCase))
		{
			diagnostics.Add(new CliBatchPatchRepairDiagnostic(
				"BatchRepairInputChanged",
				"error",
				true,
				"来源 Mod、Patch 文件内容、Unit 版本证据或 GameData 索引已变化；请重新生成计划并审阅。 "));
		}

		var currentById = current.Document.Mods.ToDictionary(mod => mod.NodeId, StringComparer.OrdinalIgnoreCase);
		foreach (var planned in document.Mods)
		{
			if (!currentById.TryGetValue(planned.NodeId, out var now))
			{
				diagnostics.Add(new CliBatchPatchRepairDiagnostic(
					"BatchRepairModMissing",
					"error",
					true,
					$"计划中的 Mod 已不存在：{planned.NodeId}",
					NodeId: planned.NodeId));
				continue;
			}
			if (!ModEquivalentExceptSelected(planned, now))
			{
				diagnostics.Add(new CliBatchPatchRepairDiagnostic(
					"BatchRepairPlanTampered",
					"error",
					true,
					$"计划中除 selected 外的 Mod 事实已被改写：{now.Name}。请重新生成计划。",
					NodeId: planned.NodeId));
			}
			if (planned.Selected && !now.IsOutdated)
			{
				diagnostics.Add(new CliBatchPatchRepairDiagnostic(
					"BatchRepairSelectionNotOutdated",
					"error",
					true,
					$"计划选择了当前已不再确认过时的 Mod：{now.Name}。请重新生成计划或取消该选择。",
					NodeId: planned.NodeId));
			}
		}
		var plannedIds = document.Mods.Select(mod => mod.NodeId).ToHashSet(StringComparer.OrdinalIgnoreCase);
		foreach (var extra in current.Document.Mods.Where(mod => !plannedIds.Contains(mod.NodeId)))
		{
			diagnostics.Add(new CliBatchPatchRepairDiagnostic(
				"BatchRepairPlanScopeChanged",
				"error",
				true,
				$"当前目标范围包含计划未冻结的 Mod：{extra.Name}。请重新生成计划。",
				NodeId: extra.NodeId));
		}
		diagnostics.AddRange(current.Document.Diagnostics);
		return new CliBatchPatchRepairValidationResult(document, current, DistinctDiagnostics(diagnostics));
	}

	internal static async ValueTask<CliBatchPatchRepairSourceSnapshot> CaptureSourceAsync(
		string sourceDirectory,
		CancellationToken cancellationToken)
	{
		if (!Directory.Exists(sourceDirectory))
			return new CliBatchPatchRepairSourceSnapshot(
				Array.Empty<CliBatchPatchRepairFile>(),
				Array.Empty<PatchGroupKey>(),
				ComputeSourceFingerprint(Array.Empty<CliBatchPatchRepairFile>()));

		var parser = CoreServices.CreatePatchFileNameParser();
		var files = new List<CliBatchPatchRepairFile>();
		foreach (var path in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.TopDirectoryOnly)
			.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (!parser.TryParse(Path.GetFileName(path), out var info) || info is null) continue;
			var fileInfo = new FileInfo(path);
			await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan | FileOptions.Asynchronous);
			var sha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
			files.Add(new CliBatchPatchRepairFile(
				fileInfo.Name,
				info.ArchiveHex16,
				info.PatchIndex,
				info.SidecarKind.ToString(),
				fileInfo.Length,
				sha256));
		}

		var groups = files
			.Where(file => string.Equals(file.SidecarKind, PatchSidecarKind.Base.ToString(), StringComparison.Ordinal))
			.Select(file => new PatchGroupKey(file.ArchiveHex16, file.PatchIndex))
			.Distinct()
			.OrderBy(group => group.ArchiveHex16, StringComparer.OrdinalIgnoreCase)
			.ThenBy(group => group.PatchIndex)
			.ToArray();
		return new CliBatchPatchRepairSourceSnapshot(files, groups, ComputeSourceFingerprint(files));
	}

	private static IReadOnlyList<ModNode> ResolveNodes(LibrarySnapshot snapshot, IReadOnlyList<string>? selectors)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		if (selectors is null || selectors.Count == 0)
			return snapshot.Nodes.Values
				.OrderBy(node => node.Metadata.Name, StringComparer.OrdinalIgnoreCase)
				.ThenBy(node => node.Id.Value)
				.ToArray();
		return selectors
			.Select(selector => CliProjections.ResolveMod(snapshot.Nodes, selector))
			.GroupBy(node => node.Id)
			.Select(group => group.First())
			.OrderBy(node => node.Metadata.Name, StringComparer.OrdinalIgnoreCase)
			.ThenBy(node => node.Id.Value)
			.ToArray();
	}

	private static async ValueTask<CliBatchPatchRepairGameDataSnapshot> ReadGameDataSnapshotAsync(
		StoragePaths paths,
		string gameDataDirectory,
		ICollection<CliBatchPatchRepairDiagnostic> diagnostics,
		CancellationToken cancellationToken)
	{
		var gameData = Path.GetFullPath(gameDataDirectory);
		if (!Directory.Exists(gameData))
		{
			diagnostics.Add(new CliBatchPatchRepairDiagnostic("BatchRepairGameDataMissing", "error", true, "GameData 目录不存在。", gameData));
			return new CliBatchPatchRepairGameDataSnapshot(gameData, "missing", false, string.Empty, null);
		}
		if (!File.Exists(paths.ArchiveHashesPath))
		{
			diagnostics.Add(new CliBatchPatchRepairDiagnostic("BatchRepairArchiveHashesMissing", "error", true, "缺少 archivehashes.json；请先维护在线资产信息。", paths.ArchiveHashesPath));
			return new CliBatchPatchRepairGameDataSnapshot(gameData, "archivehashes-missing", false, string.Empty, null);
		}

		GameDataIndexStatus status;
		try
		{
			var archiveHashes = await File.ReadAllTextAsync(paths.ArchiveHashesPath, cancellationToken).ConfigureAwait(false);
			status = await CoreServices.CreateAssetArchiveIndexService(paths)
				.GetIndexStatusAsync(gameData, archiveHashes, cancellationToken)
				.ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is IOException
			or UnauthorizedAccessException
			or InvalidDataException
			or InvalidOperationException
			or JsonException)
		{
			diagnostics.Add(new CliBatchPatchRepairDiagnostic(
				"BatchRepairGameDataIndexInspectionFailed",
				"error",
				true,
				$"无法读取当前 GameData 资产索引：{exception.Message}",
				gameData,
				Details: exception.ToString()));
			return new CliBatchPatchRepairGameDataSnapshot(gameData, "unreadable", false, string.Empty, null);
		}
		if (!status.IsCurrent)
			diagnostics.Add(new CliBatchPatchRepairDiagnostic(
				"BatchRepairGameDataIndexNotCurrent",
				"error",
				true,
				"GameData 资产索引不可用或已过期；请先执行 gamedata index plan/apply。",
				gameData));
		return new CliBatchPatchRepairGameDataSnapshot(
			status.GameDataDirectory,
			status.State.ToString(),
			status.IsCurrent,
			status.CurrentSourceFingerprint,
			status.StoredFingerprint?.SourceFingerprint);
	}

	private static string ResolveSourceDirectory(string libraryRoot, string relativePath)
	{
		var root = Path.GetFullPath(libraryRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		var directory = Path.GetFullPath(Path.Combine(root, relativePath));
		if (string.Equals(directory, root, StringComparison.OrdinalIgnoreCase)
			|| (!directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
				&& !directory.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
			throw new CliUserException("BatchRepairSourcePathInvalid", $"Mod 的存储路径不在目标 Mod 库内：{relativePath}");
		return directory;
	}

	private static string ComputeInputFingerprint(
		string managerRoot,
		string libraryRoot,
		CliBatchPatchRepairGameDataSnapshot gameData,
		IReadOnlyList<CliBatchPatchRepairMod> mods)
	{
		var payload = CliOutput.ToJson(new
		{
			managerRoot,
			libraryRoot,
			gameData,
			mods = mods.Select(mod => new
			{
				mod.NodeId,
				mod.Name,
				mod.RelativePath,
				mod.IsOutdated,
				mod.CompatibilityStatus,
				mod.UnitCount,
				mod.ReadableUnitCount,
				mod.OutdatedUnitCount,
				mod.CanAttemptGenerate,
				mod.SourceFingerprint,
				files = mod.SourceFiles,
				evidence = mod.Evidence,
			}).ToArray(),
		});
		return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
	}

	private static string ComputeSourceFingerprint(IReadOnlyList<CliBatchPatchRepairFile> files)
	{
		var payload = CliOutput.ToJson(files
			.OrderBy(file => file.FileName, StringComparer.OrdinalIgnoreCase)
			.Select(file => new { file.FileName, file.ArchiveHex16, file.PatchIndex, file.SidecarKind, file.Length, file.Sha256 })
			.ToArray());
		return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
	}

	private static bool ModEquivalentExceptSelected(CliBatchPatchRepairMod left, CliBatchPatchRepairMod right)
		=> string.Equals(
			CliOutput.ToJson(new
			{
				left.NodeId,
				left.Name,
				left.RelativePath,
				left.IsOutdated,
				left.CompatibilityStatus,
				left.UnitCount,
				left.ReadableUnitCount,
				left.OutdatedUnitCount,
				left.CanAttemptGenerate,
				left.SourceFingerprint,
				left.SourceFiles,
				left.Evidence,
			}),
			CliOutput.ToJson(new
			{
				right.NodeId,
				right.Name,
				right.RelativePath,
				right.IsOutdated,
				right.CompatibilityStatus,
				right.UnitCount,
				right.ReadableUnitCount,
				right.OutdatedUnitCount,
				right.CanAttemptGenerate,
				right.SourceFingerprint,
				right.SourceFiles,
				right.Evidence,
			}),
			StringComparison.Ordinal);

	private static bool GameDataSnapshotsEqual(CliBatchPatchRepairGameDataSnapshot left, CliBatchPatchRepairGameDataSnapshot right)
		=> string.Equals(left.Directory, right.Directory, StringComparison.OrdinalIgnoreCase)
			&& string.Equals(left.IndexState, right.IndexState, StringComparison.Ordinal)
			&& left.IsIndexCurrent == right.IsIndexCurrent
			&& string.Equals(left.CurrentSourceFingerprint, right.CurrentSourceFingerprint, StringComparison.OrdinalIgnoreCase)
			&& string.Equals(left.IndexedSourceFingerprint, right.IndexedSourceFingerprint, StringComparison.OrdinalIgnoreCase);

	private static CliBatchPatchRepairDiagnostic CreateModDiagnostic(CoreIssue issue, ModNode node)
		=> new(
			issue.Code,
			"warning",
			false,
			issue.Message,
			issue.FilePath,
			CliProjections.FormatId(node.Id),
			issue.ExceptionMessage);

	private static CliBatchPatchRepairDiagnostic CreateEligibilityDiagnostic(CoreIssue issue, ModNode node)
		=> new(
			issue.Code,
			issue.Severity == CoreIssueSeverity.Error ? "error" : "warning",
			false,
			issue.Message,
			issue.FilePath,
			CliProjections.FormatId(node.Id),
			issue.ExceptionMessage);

	private static IReadOnlyList<CliBatchPatchRepairDiagnostic> DistinctDiagnostics(IEnumerable<CliBatchPatchRepairDiagnostic> diagnostics)
		=> diagnostics
			.GroupBy(diagnostic => new { diagnostic.Code, diagnostic.Severity, diagnostic.Blocking, diagnostic.Message, diagnostic.Path, diagnostic.NodeId, diagnostic.Details })
			.Select(group => group.First())
			.ToArray();

	private static bool PathsEqual(string? left, string? right)
	{
		if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
			return string.IsNullOrWhiteSpace(left) && string.IsNullOrWhiteSpace(right);
		try
		{
			return string.Equals(
				Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
				Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
				StringComparison.OrdinalIgnoreCase);
		}
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
		{
			return false;
		}
	}

	private static string FormatAssetKey(AssetKey key)
		=> $"0x{key.TypeId:x16}:0x{key.FileId:x16}";
}
