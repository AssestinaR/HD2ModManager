using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCli;

/// <summary>
/// A non-destructive Same-key reconstruction request.  The only editable
/// intent is the registered Mod, one of its existing base Patch file names,
/// and the GameData directory.  Canonical recipes remain Core-owned.
/// </summary>
public sealed record CliSameKeyReconstructionPlanDocument(
	string Schema,
	int Version,
	DateTimeOffset CreatedUtc,
	string InputFingerprint,
	CliSameKeyReconstructionRequest Request,
	CliSameKeyReconstructionInspection Inspection)
{
	public const string CurrentSchema = "hd2mod.same-key-reconstruction-plan";
	public const int CurrentVersion = 1;

	public bool IsReady
		=> Inspection.Summary?.CanAttemptGenerate == true
			&& !Inspection.Diagnostics.Any(diagnostic => diagnostic.Blocking);
}

public sealed record CliSameKeyReconstructionRequest(
	string SourceNodeId,
	string SourcePatchFileName,
	string GameDataDirectory);

public sealed record CliSameKeyReconstructionInspection(
	string SourceNodeId,
	string SourceModName,
	IReadOnlyList<string> AvailableSourcePatchFileNames,
	string? SelectedSourcePatchFileName,
	CliSameKeyPatchFamilyFingerprint? SourcePatch,
	CliSameKeyGameDataSnapshot? GameData,
	CliSameKeyReconstructionSummary? Summary,
	IReadOnlyList<CliSameKeyReconstructionUnit> Units,
	IReadOnlyList<CliSameKeyDiagnostic> Diagnostics);

public sealed record CliSameKeyPatchFamilyFingerprint(
	string Algorithm,
	string Value,
	IReadOnlyList<CliSameKeyPatchFamilyFile> Files);

public sealed record CliSameKeyPatchFamilyFile(
	string FileName,
	bool Exists,
	long Length,
	string? Sha256);

public sealed record CliSameKeyGameDataSnapshot(
	string Directory,
	string IndexState,
	bool IsIndexCurrent,
	string CurrentSourceFingerprint,
	string? IndexedSourceFingerprint);

public sealed record CliSameKeyReconstructionSummary(
	bool CanAttemptGenerate,
	bool IsGameDataIndexCurrent,
	int SourceUnitCount,
	int ReplacementUnitCount,
	int MinifyOnlyUnitCount,
	int ReplacementMeshCount,
	int MinifiedMeshCount,
	int SharedTargetUnitCount,
	int BlockingIssueCount,
	int WarningCount);

public sealed record CliSameKeyReconstructionUnit(
	string AssetKey,
	string? TargetArchiveId,
	IReadOnlyList<string> MatchingArchiveIds,
	bool IsGeometryEligible,
	bool IsSharedTarget,
	bool HasFullTargetShellCoverage,
	bool HasExperimentalCandidate,
	IReadOnlyList<CliSameKeyDiagnostic> Diagnostics);

public sealed record CliSameKeyDiagnostic(
	string Code,
	string Severity,
	bool Blocking,
	string Message,
	string? Path = null,
	string? NodeId = null,
	string? Details = null);

internal sealed record CliSameKeyInspectionBuild(
	CliSameKeyReconstructionInspection Inspection,
	ModNode Source,
	string? SourcePatchPath,
	StoragePaths Paths);

internal sealed record CliSameKeyReconstructionBuildResult(
	CliSameKeyReconstructionPlanDocument Document,
	CliSameKeyInspectionBuild InspectionBuild);

internal sealed record CliSameKeyReconstructionValidationResult(
	CliSameKeyReconstructionPlanDocument Document,
	CliSameKeyReconstructionBuildResult? Current,
	IReadOnlyList<CliSameKeyDiagnostic> Diagnostics)
{
	public bool IsValid => Current?.Document.IsReady == true && !Diagnostics.Any(diagnostic => diagnostic.Blocking);
}

/// <summary>
/// CLI-only plan/validation façade over Canonical Same-key reconstruction.
/// It deliberately uses a fresh operation-scoped information reader so
/// inspect and validate do not request or persist Manager-derived caches.
/// </summary>
internal static class CliSameKeyReconstructionPlanArtifacts
{
	public static async ValueTask<CliSameKeyInspectionBuild> InspectAsync(
		CliWorkspace workspace,
		string sourceNodeSelector,
		string? sourcePatchFileName,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		if (string.IsNullOrWhiteSpace(sourceNodeSelector))
			throw new CliUserException("SameKeySourceRequired", "patch repair inspect 需要一个已登记 Mod 的 GUID 或唯一前缀。");
		if (string.IsNullOrWhiteSpace(workspace.ManagerRoot))
			throw new CliUserException("SameKeyManagerRootRequired", "patch repair 需要 --manager-root，以读取当前 GameData 索引与 archivehashes。 ");
		if (string.IsNullOrWhiteSpace(workspace.LibraryRoot))
			throw new CliUserException("LibraryNotConfigured", "patch repair 需要明确的 Mod 库。请传入 --library 或 --manager-root。 ");
		if (string.IsNullOrWhiteSpace(workspace.GameDataRoot))
			throw new CliUserException("SameKeyGameDataRequired", "patch repair 需要 --game-data <游戏 data 目录>，或可读取的管理器 GameDataFolder 设置。 ");

		var state = await workspace.LoadLibraryAsync(cancellationToken).ConfigureAwait(false);
		var source = CliProjections.ResolveMod(state.Snapshot.Nodes, sourceNodeSelector);
		var paths = new StoragePaths(workspace.ManagerRoot, workspace.LibraryRoot);
		var basePatches = FindBasePatches(source, workspace.LibraryRoot);
		var availableNames = basePatches.Select(path => Path.GetFileName(path)).ToArray();
		var selectedPath = ResolveSelectedPatch(basePatches, sourcePatchFileName);
		if (selectedPath is null)
		{
			var diagnostics = new List<CliSameKeyDiagnostic>();
			if (basePatches.Count == 0)
			{
				diagnostics.Add(new CliSameKeyDiagnostic(
					"SameKeyPatchRequired", "error", true, "当前 Mod 没有可用的 Patch 主文件。", NodeId: CliProjections.FormatId(source.Id)));
			}
			else if (string.IsNullOrWhiteSpace(sourcePatchFileName))
			{
				diagnostics.Add(new CliSameKeyDiagnostic(
					"SameKeySourcePatchSelectionRequired",
					"warning",
					false,
					basePatches.Count == 1
						? "未能自动选择来源 Patch。请传入该 Mod 的 base Patch 文件名。"
						: "该 Mod 包含多个 base Patch。请从 availableSourcePatchFileNames 中明确选择一个文件名。",
					NodeId: CliProjections.FormatId(source.Id)));
			}
			else
			{
				diagnostics.Add(new CliSameKeyDiagnostic(
					"SameKeySourcePatchNotFound",
					"error",
					true,
					"指定的来源 Patch 不属于当前 Mod，或已不存在。",
					NodeId: CliProjections.FormatId(source.Id)));
			}
			return new CliSameKeyInspectionBuild(
				new CliSameKeyReconstructionInspection(
					CliProjections.FormatId(source.Id),
					source.Metadata.Name,
					availableNames,
					null,
					null,
					null,
					null,
					Array.Empty<CliSameKeyReconstructionUnit>(),
					diagnostics),
				source,
				null,
				paths);
		}

		try
		{
			var gameData = Path.GetFullPath(workspace.GameDataRoot);
			var beforePatch = await ComputePatchFamilyFingerprintAsync(selectedPath, cancellationToken).ConfigureAwait(false);
			var beforeGameData = await ReadGameDataSnapshotAsync(paths, gameData, cancellationToken).ConfigureAwait(false);
			await using var reader = CoreServices.CreateModInformationReader();
			var reconstruction = CoreServices.CreateModSameKeyReconstructionService(paths, reader);
			var reconstructionState = await reconstruction.InspectAsync(
				source,
				workspace.LibraryRoot,
				gameData,
				cancellationToken,
				sourcePatchTocPath: selectedPath).ConfigureAwait(false);
			var afterPatch = await ComputePatchFamilyFingerprintAsync(selectedPath, cancellationToken).ConfigureAwait(false);
			var afterGameData = await ReadGameDataSnapshotAsync(paths, gameData, cancellationToken).ConfigureAwait(false);
			var diagnostics = reconstructionState.Issues.Select(CreateDiagnostic).ToList();
			if (reconstructionState.CanWrite)
			{
				diagnostics.Add(new CliSameKeyDiagnostic(
					"SameKeyExecutionValidationDeferred",
					"warning",
					false,
					"计划已验证来源、同 ID Target 与 GameData 索引；Canonical 骨骼、材质与网格 ABI 的最终兼容性会在 apply 实际读取 Target Unit 时再次验证。失败不会发布输出。",
					selectedPath,
					CliProjections.FormatId(source.Id)));
			}
			if (!string.Equals(beforePatch.Value, afterPatch.Value, StringComparison.OrdinalIgnoreCase))
			{
				diagnostics.Add(new CliSameKeyDiagnostic(
					"SameKeySourceChangedDuringInspection",
					"error",
					true,
					"来源 Patch 在扫描期间发生变化；请等待写入结束后重新检查。",
					selectedPath,
					CliProjections.FormatId(source.Id)));
			}
			if (!GameDataSnapshotsEqual(beforeGameData, afterGameData))
			{
				diagnostics.Add(new CliSameKeyDiagnostic(
					"SameKeyGameDataChangedDuringInspection",
					"error",
					true,
					"GameData 或其索引输入在扫描期间发生变化；请重新检查。",
					gameData));
			}
			if (!string.Equals(Path.GetFullPath(reconstructionState.SourcePatchTocPath ?? string.Empty), Path.GetFullPath(selectedPath), StringComparison.OrdinalIgnoreCase))
			{
				diagnostics.Add(new CliSameKeyDiagnostic(
					"SameKeySelectedPatchMismatch",
					"error",
					true,
					"底层重构服务没有使用 CLI 固定的来源 Patch；本次计划已拒绝。",
					selectedPath,
					CliProjections.FormatId(source.Id)));
			}

			var inspection = new CliSameKeyReconstructionInspection(
				CliProjections.FormatId(source.Id),
				source.Metadata.Name,
				availableNames,
				Path.GetFileName(selectedPath),
				afterPatch,
				afterGameData,
				CreateSummary(reconstructionState, diagnostics),
				CreateUnits(reconstructionState.Plan),
				diagnostics);
			return new CliSameKeyInspectionBuild(inspection, source, selectedPath, paths);
		}
		catch (CliUserException) { throw; }
		catch (Exception exception) when (exception is IOException
			or UnauthorizedAccessException
			or InvalidDataException
			or InvalidOperationException
			or DirectoryNotFoundException
			or FileNotFoundException)
		{
			throw new CliUserException("SameKeyInspectionFailed", $"无法完成 Same-key 重构检查：{exception.Message}");
		}
	}

	public static async ValueTask<CliSameKeyReconstructionBuildResult> BuildAsync(
		CliWorkspace workspace,
		string sourceNodeSelector,
		string sourcePatchFileName,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(sourcePatchFileName))
			throw new CliUserException("SameKeySourcePatchRequired", "patch repair plan 必须提供一个来源 Patch 主文件名。请先运行 patch repair inspect <Mod> --json 查看候选。 ");
		var inspectionBuild = await InspectAsync(workspace, sourceNodeSelector, sourcePatchFileName, cancellationToken).ConfigureAwait(false);
		if (inspectionBuild.SourcePatchPath is null || inspectionBuild.Inspection.SelectedSourcePatchFileName is null)
		{
			var issue = inspectionBuild.Inspection.Diagnostics.FirstOrDefault(diagnostic => diagnostic.Blocking)
				?? new CliSameKeyDiagnostic("SameKeySourcePatchRequired", "error", true, "未能选择来源 Patch。 ");
			throw new CliUserException(issue.Code, issue.Message);
		}
		var request = new CliSameKeyReconstructionRequest(
			CliProjections.FormatId(inspectionBuild.Source.Id),
			inspectionBuild.Inspection.SelectedSourcePatchFileName,
			inspectionBuild.Inspection.GameData?.Directory ?? Path.GetFullPath(workspace.GameDataRoot!));
		var document = new CliSameKeyReconstructionPlanDocument(
			CliSameKeyReconstructionPlanDocument.CurrentSchema,
			CliSameKeyReconstructionPlanDocument.CurrentVersion,
			DateTimeOffset.UtcNow,
			ComputeInputFingerprint(request, inspectionBuild.Inspection),
			request,
			inspectionBuild.Inspection);
		return new CliSameKeyReconstructionBuildResult(document, inspectionBuild);
	}

	public static async ValueTask<CliSameKeyReconstructionPlanDocument> ReadDocumentAsync(
		string path,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(path))
			throw new CliUserException("SameKeyPlanRequired", "请通过 --plan <Same-key重构计划.json> 提供计划文件。 ");
		var fullPath = CliOutput.NormalizeDirectoryPath(path, "SameKeyPlanPathInvalid");
		if (!File.Exists(fullPath))
			throw new CliUserException("SameKeyPlanNotFound", $"Same-key 重构计划不存在：{fullPath}");
		try
		{
			var json = await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
			var document = JsonSerializer.Deserialize<CliSameKeyReconstructionPlanDocument>(json, CliOutput.JsonOptions);
			if (document is null)
				throw new CliUserException("SameKeyPlanUnreadable", "Same-key 重构计划为空或无法解析。 ");
			if (!string.Equals(document.Schema, CliSameKeyReconstructionPlanDocument.CurrentSchema, StringComparison.Ordinal)
				|| document.Version != CliSameKeyReconstructionPlanDocument.CurrentVersion)
			{
				throw new CliUserException(
					"SameKeyPlanVersionUnsupported",
					$"Same-key 重构计划版本不受支持。需要 {CliSameKeyReconstructionPlanDocument.CurrentSchema} v{CliSameKeyReconstructionPlanDocument.CurrentVersion}；请重新生成计划。 ");
			}
			if (string.IsNullOrWhiteSpace(document.InputFingerprint)
				|| document.Request is null
				|| string.IsNullOrWhiteSpace(document.Request.SourceNodeId)
				|| string.IsNullOrWhiteSpace(document.Request.SourcePatchFileName)
				|| string.IsNullOrWhiteSpace(document.Request.GameDataDirectory)
				|| document.Inspection is null)
			{
				throw new CliUserException("SameKeyPlanUnreadable", "Same-key 重构计划缺少来源、GameData 或检查结果。 ");
			}
			return document;
		}
		catch (CliUserException) { throw; }
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
		{
			throw new CliUserException("SameKeyPlanUnreadable", $"无法读取 Same-key 重构计划：{exception.Message}");
		}
	}

	public static async ValueTask<CliSameKeyReconstructionValidationResult> ValidateAsync(
		CliSameKeyReconstructionPlanDocument document,
		CliWorkspace workspace,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(document);
		ArgumentNullException.ThrowIfNull(workspace);
		var diagnostics = new List<CliSameKeyDiagnostic>();
		CliSameKeyReconstructionBuildResult? current = null;
		try
		{
			var currentGameData = workspace.GameDataRoot;
			if (string.IsNullOrWhiteSpace(currentGameData)
				|| !string.Equals(Path.GetFullPath(currentGameData), Path.GetFullPath(document.Request.GameDataDirectory), StringComparison.OrdinalIgnoreCase))
			{
				diagnostics.Add(new CliSameKeyDiagnostic(
					"SameKeyGameDataTargetChanged",
					"error",
					true,
					"当前 --game-data 与计划固定的 GameData 目录不一致；请在同一目标上重新生成计划。 "));
				return new CliSameKeyReconstructionValidationResult(document, null, diagnostics);
			}
			current = await BuildAsync(workspace, document.Request.SourceNodeId, document.Request.SourcePatchFileName, cancellationToken).ConfigureAwait(false);
		}
		catch (CliUserException exception)
		{
			diagnostics.Add(new CliSameKeyDiagnostic(exception.Code, "error", true, exception.Message));
			return new CliSameKeyReconstructionValidationResult(document, null, diagnostics);
		}

		if (!string.Equals(document.InputFingerprint, current.Document.InputFingerprint, StringComparison.OrdinalIgnoreCase))
		{
			diagnostics.Add(new CliSameKeyDiagnostic(
				"SameKeyInputChanged",
				"error",
				true,
				"来源 Patch、GameData 指纹或当前索引解析结果已变化；请重新生成计划并审阅。 "));
		}
		diagnostics.AddRange(current.Document.Inspection.Diagnostics.Where(diagnostic => diagnostic.Blocking));
		return new CliSameKeyReconstructionValidationResult(document, current, diagnostics);
	}

	internal static async ValueTask<SameKeyReconstructionOperationResult> GenerateAsync(
		CliSameKeyReconstructionBuildResult build,
		string outputDirectory,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(build);
		if (build.InspectionBuild.SourcePatchPath is null)
			throw new CliUserException("SameKeySourcePatchRequired", "未能解析计划固定的来源 Patch。 ");
		await using var reader = CoreServices.CreateModInformationReader();
		var reconstruction = CoreServices.CreateModSameKeyReconstructionService(build.InspectionBuild.Paths, reader);
		return await reconstruction.GenerateCandidateAsync(
			build.InspectionBuild.Source,
			build.InspectionBuild.Paths.ModsDirectory,
			build.Document.Request.GameDataDirectory,
			outputDirectory,
			cancellationToken,
			sourcePatchTocPath: build.InspectionBuild.SourcePatchPath).ConfigureAwait(false);
	}

	private static IReadOnlyList<string> FindBasePatches(ModNode source, string libraryRoot)
	{
		var sourceDirectory = ResolveSourceDirectory(libraryRoot, source.RelativePath);
		if (!Directory.Exists(sourceDirectory)) return Array.Empty<string>();
		var parser = CoreServices.CreatePatchFileNameParser();
		return Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.TopDirectoryOnly)
			.Where(path => parser.TryParse(Path.GetFileName(path), out var info) && info?.SidecarKind == PatchSidecarKind.Base)
			.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
			.ToArray();
	}

	private static string ResolveSourceDirectory(string libraryRoot, string relativePath)
	{
		var root = Path.GetFullPath(libraryRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		var directory = Path.GetFullPath(Path.Combine(root, relativePath));
		if (string.Equals(directory, root, StringComparison.OrdinalIgnoreCase)
			|| (!directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
				&& !directory.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
		{
			throw new CliUserException("SameKeySourcePathInvalid", "当前 Mod 的存储路径不在目标 Mod 库内。 ");
		}
		return directory;
	}

	private static string? ResolveSelectedPatch(IReadOnlyList<string> basePatches, string? sourcePatchFileName)
	{
		if (string.IsNullOrWhiteSpace(sourcePatchFileName))
			return basePatches.Count == 1 ? basePatches[0] : null;
		if (!string.Equals(sourcePatchFileName, Path.GetFileName(sourcePatchFileName), StringComparison.Ordinal))
			return null;
		return basePatches.FirstOrDefault(path => string.Equals(Path.GetFileName(path), sourcePatchFileName, StringComparison.OrdinalIgnoreCase));
	}

	private static async ValueTask<CliSameKeyPatchFamilyFingerprint> ComputePatchFamilyFingerprintAsync(
		string patchTocPath,
		CancellationToken cancellationToken)
	{
		var files = new List<CliSameKeyPatchFamilyFile>();
		foreach (var path in new[] { patchTocPath, patchTocPath + ".stream", patchTocPath + ".gpu_resources" })
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (!File.Exists(path))
			{
				files.Add(new CliSameKeyPatchFamilyFile(Path.GetFileName(path), false, 0, null));
				continue;
			}
			var info = new FileInfo(path);
			await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 128, FileOptions.SequentialScan);
			files.Add(new CliSameKeyPatchFamilyFile(
				Path.GetFileName(path),
				true,
				info.Length,
				Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false))));
		}
		var value = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CliOutput.ToJson(files))));
		return new CliSameKeyPatchFamilyFingerprint("sha256-patch-family-v1", value, files);
	}

	private static async ValueTask<CliSameKeyGameDataSnapshot> ReadGameDataSnapshotAsync(
		StoragePaths paths,
		string gameDataDirectory,
		CancellationToken cancellationToken)
	{
		var gameData = Path.GetFullPath(gameDataDirectory);
		if (!Directory.Exists(gameData))
		{
			return new CliSameKeyGameDataSnapshot(gameData, "missing", false, string.Empty, null);
		}
		var archiveHashes = File.Exists(paths.ArchiveHashesPath)
			? await File.ReadAllTextAsync(paths.ArchiveHashesPath, cancellationToken).ConfigureAwait(false)
			: "{}";
		var status = await CoreServices.CreateAssetArchiveIndexService(paths)
			.GetIndexStatusAsync(gameData, archiveHashes, cancellationToken)
			.ConfigureAwait(false);
		return new CliSameKeyGameDataSnapshot(
			status.GameDataDirectory,
			status.State.ToString().ToLowerInvariant(),
			status.IsCurrent,
			status.CurrentSourceFingerprint,
			status.StoredFingerprint?.SourceFingerprint);
	}

	private static bool GameDataSnapshotsEqual(CliSameKeyGameDataSnapshot left, CliSameKeyGameDataSnapshot right)
		=> string.Equals(left.Directory, right.Directory, StringComparison.OrdinalIgnoreCase)
			&& string.Equals(left.IndexState, right.IndexState, StringComparison.Ordinal)
			&& string.Equals(left.CurrentSourceFingerprint, right.CurrentSourceFingerprint, StringComparison.OrdinalIgnoreCase)
			&& string.Equals(left.IndexedSourceFingerprint, right.IndexedSourceFingerprint, StringComparison.OrdinalIgnoreCase);

	private static CliSameKeyReconstructionSummary CreateSummary(
		ModSameKeyReconstructionState state,
		IReadOnlyList<CliSameKeyDiagnostic> diagnostics)
	{
		return new CliSameKeyReconstructionSummary(
			state.CanWrite,
			state.IsGameDataIndexCurrent,
			state.Plan?.SourceUnitCount ?? 0,
			state.ReplacementUnitCount,
			state.MinifyOnlyUnitCount,
			state.ReplacementMeshCount,
			state.MinifiedMeshCount,
			state.SharedTargetUnitCount,
			diagnostics.Count(diagnostic => diagnostic.Blocking),
			diagnostics.Count(diagnostic => string.Equals(diagnostic.Severity, "warning", StringComparison.OrdinalIgnoreCase)));
	}

	private static IReadOnlyList<CliSameKeyReconstructionUnit> CreateUnits(SameKeyReconstructionPlan? plan)
		=> plan?.Units.Select(unit => new CliSameKeyReconstructionUnit(
			FormatAssetKey(unit.UnitAssetKey),
			unit.TargetArchive?.ArchiveId,
			unit.MatchingArchives.Select(archive => archive.ArchiveId).OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray(),
			unit.IsGeometryEligible,
			unit.IsSharedTarget,
			unit.HasFullTargetShellCoverage,
			unit.HasExperimentalCandidate,
			unit.Issues.Select(CreateDiagnostic).ToArray())).ToArray()
			?? Array.Empty<CliSameKeyReconstructionUnit>();

	private static CliSameKeyDiagnostic CreateDiagnostic(CoreIssue issue)
		=> new(
			issue.Code,
			issue.Severity == CoreIssueSeverity.Error ? "error" : "warning",
			issue.Severity == CoreIssueSeverity.Error,
			issue.Message,
			issue.FilePath,
			issue.NodeId is { } nodeId ? CliProjections.FormatId(nodeId) : null,
			issue.ExceptionMessage);

	private static string ComputeInputFingerprint(
		CliSameKeyReconstructionRequest request,
		CliSameKeyReconstructionInspection inspection)
	{
		var payload = CliOutput.ToJson(new
		{
			request.SourceNodeId,
			request.SourcePatchFileName,
			gameData = inspection.GameData is null ? null : new
			{
				inspection.GameData.Directory,
				inspection.GameData.IndexState,
				inspection.GameData.CurrentSourceFingerprint,
				inspection.GameData.IndexedSourceFingerprint,
			},
			sourcePatch = inspection.SourcePatch is null ? null : new
			{
				inspection.SourcePatch.Algorithm,
				inspection.SourcePatch.Value,
			},
			units = inspection.Units.Select(unit => new
			{
				unit.AssetKey,
				unit.TargetArchiveId,
				unit.IsGeometryEligible,
				unit.IsSharedTarget,
				issues = unit.Diagnostics.Select(issue => new { issue.Code, issue.Blocking }).OrderBy(issue => issue.Code, StringComparer.Ordinal).ToArray(),
			}).OrderBy(unit => unit.AssetKey, StringComparer.Ordinal).ToArray(),
		});
		return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
	}

	private static string FormatAssetKey(AssetKey assetKey)
		=> $"0x{assetKey.TypeId:x16}/0x{assetKey.FileId:x16}";
}
