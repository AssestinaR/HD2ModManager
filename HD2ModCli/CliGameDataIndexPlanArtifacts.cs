using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCli;

/// <summary>
/// A plan-gated mutation of Manager-owned GameData indexes.  It never writes
/// GameData itself or Mod library content; it only rebuilds derived manager
/// state after rechecking the exact GameData and archive-metadata identity.
/// </summary>
public sealed record CliGameDataIndexPlanDocument(
	string Schema,
	int Version,
	DateTimeOffset CreatedUtc,
	string InputFingerprint,
	CliGameDataIndexRequest Request,
	CliGameDataIndexInputs Inputs,
	IReadOnlyList<CliGameDataDiagnostic> Diagnostics)
{
	public const string CurrentSchema = "hd2mod.gamedata-index-plan";
	public const int CurrentVersion = 1;

	public bool IsReady => !Diagnostics.Any(diagnostic => diagnostic.Blocking);
}

public sealed record CliGameDataIndexRequest(string Operation, string GameDataDirectory);

public sealed record CliGameDataIndexInputs(
	string ManagerRoot,
	CliGameDataIndexFile ArchiveHashes,
	string BaseIndexState,
	bool BaseIndexCurrent,
	string CurrentSourceFingerprint,
	string? StoredSourceFingerprint,
	bool AdvancedEquipmentFactsCurrent);

public sealed record CliGameDataIndexFile(string FileName, bool Exists, long Length, string? Sha256);

internal sealed record CliGameDataIndexPlanBuildResult(
	CliGameDataIndexPlanDocument Document,
	StoragePaths Paths,
	CliGameDataIndexOperation Operation);

internal sealed record CliGameDataIndexPlanValidationResult(
	CliGameDataIndexPlanDocument Document,
	CliGameDataIndexPlanBuildResult? Current,
	IReadOnlyList<CliGameDataDiagnostic> Diagnostics)
{
	public bool IsValid => Current is not null && Current.Document.IsReady && !Diagnostics.Any(diagnostic => diagnostic.Blocking);
}

internal enum CliGameDataIndexOperation
{
	RebuildBase,
	RebuildAllEquipmentFacts,
	BuildMissingEquipmentFacts,
}

/// <summary>
/// Builds and revalidates index-management plans without ever asking the
/// desktop information center to populate Mod facts. The plan is intentionally
/// narrow: it contains no arbitrary SQLite or GameData file operation.
/// </summary>
internal static class CliGameDataIndexPlanArtifacts
{
	public static ValueTask<CliGameDataIndexPlanBuildResult> BuildAsync(
		CliWorkspace workspace,
		string operationArgument,
		CancellationToken cancellationToken = default)
		=> BuildAsync(workspace, new CliGameDataIndexRequest(ParseOperation(operationArgument).ToString(), RequireGameDataDirectory(workspace)), cancellationToken);

	public static async ValueTask<CliGameDataIndexPlanBuildResult> BuildAsync(
		CliWorkspace workspace,
		CliGameDataIndexRequest request,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		ArgumentNullException.ThrowIfNull(request);
		EnsureManagerWorkspace(workspace);
		var operation = ParseOperation(request.Operation);
		var gameData = CliOutput.NormalizeDirectoryPath(request.GameDataDirectory, "GameDataDirectoryInvalid");
		var paths = new StoragePaths(workspace.ManagerRoot!, workspace.LibraryRoot);
		var diagnostics = new List<CliGameDataDiagnostic>();
		if (!Directory.Exists(gameData))
		{
			diagnostics.Add(new CliGameDataDiagnostic("GameDataDirectoryMissing", "error", true, "GameData 目录不存在。", gameData));
			return CreateResult(request with { Operation = operation.ToString(), GameDataDirectory = gameData }, paths, operation, new CliGameDataIndexInputs(paths.AppRootDirectory, new CliGameDataIndexFile("archivehashes.json", false, 0, null), "missing", false, string.Empty, null, false), diagnostics);
		}

		var archiveHashes = await ReadArchiveHashesAsync(paths, diagnostics, cancellationToken).ConfigureAwait(false);
		var status = await ReadBaseIndexStatusAsync(paths, gameData, archiveHashes.Content, archiveHashes.File.Exists, diagnostics, cancellationToken).ConfigureAwait(false);
		var advancedCurrent = await ReadAdvancedEquipmentStatusAsync(paths, diagnostics, cancellationToken).ConfigureAwait(false);
		if (!archiveHashes.File.Exists)
		{
			diagnostics.Add(new CliGameDataDiagnostic(
				"ArchiveHashesMissing", "error", true,
				"缺少 archivehashes.json；请先在管理器设置页更新在线资产信息。", paths.ArchiveHashesPath));
		}
		if (status.State == GameDataIndexState.Invalid)
		{
			diagnostics.Add(new CliGameDataDiagnostic("ArchiveHashesInvalid", "error", true, "archivehashes.json 无法解析，不能安全建立或验证资产索引。", paths.ArchiveHashesPath));
		}
		if (operation is CliGameDataIndexOperation.RebuildAllEquipmentFacts or CliGameDataIndexOperation.BuildMissingEquipmentFacts)
		{
			if (!status.IsCurrent)
			{
				diagnostics.Add(new CliGameDataDiagnostic(
					"GameDataIndexNotCurrent", "error", true,
					"计算 Armor / Helmet Unit 部位事实前，基础 GameData 资产索引必须是当前版本。请先执行 gamedata index plan rebuild。", gameData));
			}
		}

		var inputs = new CliGameDataIndexInputs(
			paths.AppRootDirectory,
			archiveHashes.File,
			status.State.ToString().ToLowerInvariant(),
			status.IsCurrent,
			status.CurrentSourceFingerprint,
			status.StoredFingerprint?.SourceFingerprint,
			advancedCurrent);
		var normalizedRequest = request with { Operation = operation.ToString(), GameDataDirectory = gameData };
		return CreateResult(normalizedRequest, paths, operation, inputs, diagnostics);
	}

	public static async ValueTask<CliGameDataIndexPlanDocument> ReadDocumentAsync(string path, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(path))
			throw new CliUserException("GameDataIndexPlanRequired", "请通过 --plan <GameData索引计划.json> 提供计划文件。 ");
		var fullPath = CliOutput.NormalizeDirectoryPath(path, "GameDataIndexPlanPathInvalid");
		if (!File.Exists(fullPath))
			throw new CliUserException("GameDataIndexPlanNotFound", $"GameData 索引计划不存在：{fullPath}");
		try
		{
			var document = JsonSerializer.Deserialize<CliGameDataIndexPlanDocument>(
				await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false),
				CliOutput.JsonOptions);
			if (document is null)
				throw new CliUserException("GameDataIndexPlanUnreadable", "GameData 索引计划为空或无法解析。 ");
			if (!string.Equals(document.Schema, CliGameDataIndexPlanDocument.CurrentSchema, StringComparison.Ordinal)
				|| document.Version != CliGameDataIndexPlanDocument.CurrentVersion)
			{
				throw new CliUserException("GameDataIndexPlanVersionUnsupported", $"GameData 索引计划版本不受支持。需要 {CliGameDataIndexPlanDocument.CurrentSchema} v{CliGameDataIndexPlanDocument.CurrentVersion}；请重新生成计划。 ");
			}
			if (string.IsNullOrWhiteSpace(document.InputFingerprint)
				|| document.Request is null
				|| string.IsNullOrWhiteSpace(document.Request.Operation)
				|| string.IsNullOrWhiteSpace(document.Request.GameDataDirectory)
				|| document.Inputs is null
				|| document.Diagnostics is null)
			{
				throw new CliUserException("GameDataIndexPlanUnreadable", "GameData 索引计划缺少请求、输入或诊断信息。 ");
			}
			_ = ParseOperation(document.Request.Operation);
			return document;
		}
		catch (CliUserException) { throw; }
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
		{
			throw new CliUserException("GameDataIndexPlanUnreadable", $"无法读取 GameData 索引计划：{exception.Message}");
		}
	}

	public static async ValueTask<CliGameDataIndexPlanValidationResult> ValidateAsync(
		CliGameDataIndexPlanDocument document,
		CliWorkspace workspace,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(document);
		ArgumentNullException.ThrowIfNull(workspace);
		var diagnostics = new List<CliGameDataDiagnostic>();
		if (string.IsNullOrWhiteSpace(workspace.GameDataRoot)
			|| !string.Equals(Path.GetFullPath(workspace.GameDataRoot), Path.GetFullPath(document.Request.GameDataDirectory), StringComparison.OrdinalIgnoreCase))
		{
			diagnostics.Add(new CliGameDataDiagnostic("GameDataIndexTargetChanged", "error", true, "当前 --game-data 与计划固定的 GameData 目录不一致；请重新生成计划。"));
			return new CliGameDataIndexPlanValidationResult(document, null, diagnostics);
		}
		CliGameDataIndexPlanBuildResult? current = null;
		try
		{
			current = await BuildAsync(workspace, document.Request, cancellationToken).ConfigureAwait(false);
		}
		catch (CliUserException exception)
		{
			diagnostics.Add(new CliGameDataDiagnostic(exception.Code, "error", true, exception.Message));
			return new CliGameDataIndexPlanValidationResult(document, null, diagnostics);
		}
		if (!string.Equals(document.InputFingerprint, current.Document.InputFingerprint, StringComparison.OrdinalIgnoreCase))
		{
			diagnostics.Add(new CliGameDataDiagnostic(
				"GameDataIndexInputChanged", "error", true,
				"GameData 文件、archivehashes、基础索引状态或 Unit 部位事实状态已变化；请重新生成计划并确认当前输入。"));
		}
		diagnostics.AddRange(current.Document.Diagnostics.Where(diagnostic => diagnostic.Blocking));
		return new CliGameDataIndexPlanValidationResult(document, current, DistinctDiagnostics(diagnostics));
	}

	internal static CliGameDataIndexOperation ParseOperation(string value)
		=> value.Trim().ToLowerInvariant() switch
		{
			"rebuild" or "rebuildbase" => CliGameDataIndexOperation.RebuildBase,
			"units-all" or "rebuildallequipmentfacts" => CliGameDataIndexOperation.RebuildAllEquipmentFacts,
			"units-missing" or "buildmissingequipmentfacts" => CliGameDataIndexOperation.BuildMissingEquipmentFacts,
			_ => throw new CliUserException("GameDataIndexOperationInvalid", "gamedata index plan 仅支持 rebuild、units-all 或 units-missing。 "),
		};

	private static CliGameDataIndexPlanBuildResult CreateResult(
		CliGameDataIndexRequest request,
		StoragePaths paths,
		CliGameDataIndexOperation operation,
		CliGameDataIndexInputs inputs,
		IReadOnlyList<CliGameDataDiagnostic> diagnostics)
	{
		var document = new CliGameDataIndexPlanDocument(
			CliGameDataIndexPlanDocument.CurrentSchema,
			CliGameDataIndexPlanDocument.CurrentVersion,
			DateTimeOffset.UtcNow,
			ComputeInputFingerprint(request, inputs),
			request,
			inputs,
			DistinctDiagnostics(diagnostics));
		return new CliGameDataIndexPlanBuildResult(document, paths, operation);
	}

	private static void EnsureManagerWorkspace(CliWorkspace workspace)
	{
		if (string.IsNullOrWhiteSpace(workspace.ManagerRoot))
			throw new CliUserException("GameDataIndexManagerRootRequired", "gamedata index 命令需要 --manager-root <管理器目录>，以定位 Manager 资产索引。 ");
		if (!Directory.Exists(workspace.ManagerRoot))
			throw new CliUserException("ManagerRootNotFound", $"管理器目录不存在：{workspace.ManagerRoot}");
		if (string.IsNullOrWhiteSpace(workspace.SettingsPath) || !File.Exists(workspace.SettingsPath) || workspace.Settings is null)
			throw new CliUserException("ManagerSettingsRequired", "gamedata index 命令要求 --manager-root 中存在可读取的 settings.json。 ");
	}

	private static string RequireGameDataDirectory(CliWorkspace workspace)
	{
		EnsureManagerWorkspace(workspace);
		if (string.IsNullOrWhiteSpace(workspace.GameDataRoot))
			throw new CliUserException("GameDataDirectoryRequired", "gamedata index 命令需要 --game-data <游戏 data 目录>，或可读取的管理器 GameDataFolder 设置。 ");
		return workspace.GameDataRoot;
	}

	private static async ValueTask<(CliGameDataIndexFile File, string Content)> ReadArchiveHashesAsync(
		StoragePaths paths,
		ICollection<CliGameDataDiagnostic> diagnostics,
		CancellationToken cancellationToken)
	{
		var path = paths.ArchiveHashesPath;
		if (!File.Exists(path)) return (new CliGameDataIndexFile(Path.GetFileName(path), false, 0, null), "{}");
		try
		{
			var info = new FileInfo(path);
			var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
			var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
			return (new CliGameDataIndexFile(Path.GetFileName(path), true, info.Length, hash), content);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			diagnostics.Add(new CliGameDataDiagnostic("ArchiveHashesUnreadable", "error", true, $"无法读取 archivehashes：{exception.Message}", path));
			return (new CliGameDataIndexFile(Path.GetFileName(path), true, 0, null), "{}");
		}
	}

	private static async ValueTask<GameDataIndexStatus> ReadBaseIndexStatusAsync(
		StoragePaths paths,
		string gameData,
		string archiveHashes,
		bool archiveHashesAvailable,
		ICollection<CliGameDataDiagnostic> diagnostics,
		CancellationToken cancellationToken)
	{
		try
		{
			return await CoreServices.CreateAssetArchiveIndexService(paths)
				.GetIndexStatusAsync(gameData, archiveHashesAvailable ? archiveHashes : "{}", cancellationToken)
				.ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or FormatException)
		{
			diagnostics.Add(new CliGameDataDiagnostic("GameDataIndexUnreadable", "error", true, $"无法读取 GameData 资产索引状态：{exception.Message}", paths.DbPath));
			return new GameDataIndexStatus(GameDataIndexState.Invalid, null, gameData, string.Empty);
		}
	}

	private static async ValueTask<bool> ReadAdvancedEquipmentStatusAsync(
		StoragePaths paths,
		ICollection<CliGameDataDiagnostic> diagnostics,
		CancellationToken cancellationToken)
	{
		try { return await CoreServices.CreateAdvancedEquipmentIndexService(paths).IsCurrentAsync(cancellationToken).ConfigureAwait(false); }
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
		{
			diagnostics.Add(new CliGameDataDiagnostic("AdvancedEquipmentIndexUnreadable", "warning", false, $"无法确认 Armor / Helmet Unit 部位事实状态：{exception.Message}", paths.DbPath));
			return false;
		}
	}

	private static string ComputeInputFingerprint(CliGameDataIndexRequest request, CliGameDataIndexInputs inputs)
	{
		var payload = CliOutput.ToJson(new
		{
			request.Operation,
			request.GameDataDirectory,
			inputs.ManagerRoot,
			archiveHashes = new { inputs.ArchiveHashes.Exists, inputs.ArchiveHashes.Length, inputs.ArchiveHashes.Sha256 },
			inputs.CurrentSourceFingerprint,
			inputs.StoredSourceFingerprint,
			inputs.BaseIndexState,
			inputs.BaseIndexCurrent,
			inputs.AdvancedEquipmentFactsCurrent,
		});
		return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
	}

	private static IReadOnlyList<CliGameDataDiagnostic> DistinctDiagnostics(IEnumerable<CliGameDataDiagnostic> diagnostics)
		=> diagnostics.GroupBy(diagnostic => new { diagnostic.Code, diagnostic.Severity, diagnostic.Message, diagnostic.Path })
			.Select(group => group.First()).ToArray();
}
