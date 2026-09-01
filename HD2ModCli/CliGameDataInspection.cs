using System.Globalization;
using System.Text;
using System.Text.Json;
using HD2ModCore.Application;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCli;

public sealed record CliGameDataDiagnostic(
	string Code,
	string Severity,
	bool Blocking,
	string Message,
	string? Path = null,
	string? Details = null);

public sealed record CliGameDataIndexInspection(
	string? GameDataDirectory,
	bool TargetIsValid,
	string IndexState,
	bool IsCurrent,
	GameDataIndexFingerprint? StoredFingerprint,
	string? CurrentSourceFingerprint,
	bool ArchiveHashesAvailable,
	IReadOnlyList<CliGameDataDiagnostic> Diagnostics)
{
	public bool IsReady => TargetIsValid && IsCurrent && !Diagnostics.Any(diagnostic => diagnostic.Blocking);
}

public sealed record CliGameDataArchiveListInspection(
	CliGameDataIndexInspection Index,
	IReadOnlyList<GameDataArchiveSummary> Archives,
	IReadOnlyList<CliGameDataDiagnostic> Diagnostics)
{
	public bool IsReady => Index.IsReady && !Diagnostics.Any(diagnostic => diagnostic.Blocking);
}

public sealed record CliGameDataArchiveDetailsInspection(
	CliGameDataIndexInspection Index,
	string RequestedArchiveId,
	GameDataArchiveDetails? Archive,
	IReadOnlyList<CliGameDataDiagnostic> Diagnostics)
{
	public bool IsReady => Index.IsReady && Archive is not null && !Diagnostics.Any(diagnostic => diagnostic.Blocking);
}

public sealed record CliGameDataAssetLookupInspection(
	CliGameDataIndexInspection Index,
	IReadOnlyList<AssetArchiveMatch> Matches,
	IReadOnlyList<CliGameDataDiagnostic> Diagnostics)
{
	public bool IsReady => Index.IsReady && !Diagnostics.Any(diagnostic => diagnostic.Blocking);
}

public sealed record CliGameDataOverrideInspection(
	string? GameDataDirectory,
	bool TargetIsValid,
	string InstallationSignature,
	DeployedOverrideGraph? OverrideGraph,
	IReadOnlyList<CliGameDataDiagnostic> Diagnostics)
{
	public bool IsReadable => TargetIsValid && OverrideGraph is not null;
	public bool IsTrusted => IsReadable && !Diagnostics.Any(diagnostic => diagnostic.Blocking);
}

public sealed record CliGameDataBrowserInspection(
	CliGameDataIndexInspection Index,
	GameDataArchiveBrowserSnapshot? Browser,
	IReadOnlyList<CliGameDataDiagnostic> Diagnostics)
{
	public bool IsReady => Index.IsReady && Browser is not null && !Diagnostics.Any(diagnostic => diagnostic.Blocking);
}

internal sealed record CliGameDataPage<T>(
	int TotalCount,
	int Offset,
	int Limit,
	IReadOnlyList<T> Items,
	string? NextCursor);

internal static class CliGameDataPaging
{
	private const int DefaultLimit = 100;

	public static CliGameDataPage<T> Slice<T>(
		IReadOnlyList<T> source,
		string kind,
		string generation,
		int? requestedLimit,
		string? cursor)
	{
		ArgumentNullException.ThrowIfNull(source);
		ArgumentException.ThrowIfNullOrWhiteSpace(kind);
		ArgumentException.ThrowIfNullOrWhiteSpace(generation);
		var limit = requestedLimit ?? DefaultLimit;
		var offset = 0;
		if (!string.IsNullOrWhiteSpace(cursor))
		{
			var decoded = Decode(cursor);
			if (!string.Equals(decoded.Kind, kind, StringComparison.Ordinal))
				throw new CliUserException("CursorKindMismatch", "cursor 不属于当前 GameData 查询类型。 ");
			if (!string.Equals(decoded.Generation, generation, StringComparison.OrdinalIgnoreCase))
				throw new CliUserException("CursorGenerationChanged", "GameData 索引或实际覆盖图在分页期间已变化；请从第一页重新读取。 ");
			offset = decoded.Offset;
		}
		if (offset < 0 || offset > source.Count)
			throw new CliUserException("CursorInvalid", "cursor 中的分页位置无效。 ");
		var items = source.Skip(offset).Take(limit).ToArray();
		var nextOffset = checked(offset + items.Length);
		return new CliGameDataPage<T>(
			source.Count,
			offset,
			limit,
			items,
			nextOffset < source.Count ? Encode(new CliGameDataCursor(kind, generation, nextOffset)) : null);
	}

	private static string Encode(CliGameDataCursor cursor)
		=> Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(cursor, CliOutput.JsonOptions)))
			.TrimEnd('=').Replace('+', '-').Replace('/', '_');

	private static CliGameDataCursor Decode(string cursor)
	{
		try
		{
			var normalized = cursor.Replace('-', '+').Replace('_', '/');
			var padding = normalized.Length % 4;
			if (padding != 0) normalized = normalized.PadRight(normalized.Length + 4 - padding, '=');
			var decoded = JsonSerializer.Deserialize<CliGameDataCursor>(Encoding.UTF8.GetString(Convert.FromBase64String(normalized)), CliOutput.JsonOptions);
			if (decoded is null || string.IsNullOrWhiteSpace(decoded.Kind) || string.IsNullOrWhiteSpace(decoded.Generation) || decoded.Offset < 0)
				throw new InvalidDataException("cursor 内容不完整。 ");
			return decoded;
		}
		catch (Exception exception) when (exception is FormatException or JsonException or InvalidDataException)
		{
			throw new CliUserException("CursorInvalid", $"无法解析 GameData cursor：{exception.Message}");
		}
	}

	private sealed record CliGameDataCursor(string Kind, string Generation, int Offset);
}

/// <summary>
/// Strictly read-only queries over the current GameData archive index and
/// deployment graph.  The optional full browser intentionally uses the Core
/// direct inventory path, so it does not populate desktop derived-data caches.
/// </summary>
internal static class CliGameDataInspection
{
	public static async ValueTask<CliGameDataIndexInspection> BuildIndexAsync(
		CliWorkspace workspace,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		if (string.IsNullOrWhiteSpace(workspace.ManagerRoot))
			throw new CliUserException("GameDataManagerRootRequired", "gamedata 命令需要 --manager-root <管理器目录>，以读取资产索引和可选显示资料。 ");
		if (string.IsNullOrWhiteSpace(workspace.GameDataRoot))
			throw new CliUserException("GameDataDirectoryRequired", "gamedata 命令需要 --game-data <游戏 data 目录>，或可读取的管理器 GameDataFolder 设置。 ");

		var diagnostics = new List<CliGameDataDiagnostic>();
		if (!TryNormalizeReadOnlyDirectory(workspace.GameDataRoot, out var gameDataDirectory, diagnostics))
		{
			return new CliGameDataIndexInspection(
				workspace.GameDataRoot,
				false,
				"invalid",
				false,
				null,
				null,
				false,
				DistinctDiagnostics(diagnostics));
		}
		var resolvedGameDataDirectory = gameDataDirectory
			?? throw new InvalidOperationException("只读 GameData 路径验证成功后缺少规范化目录。");

		var paths = new StoragePaths(workspace.ManagerRoot, workspace.LibraryRoot);
		var archiveHashesAvailable = File.Exists(paths.ArchiveHashesPath);
		string archiveHashes;
		try
		{
			archiveHashes = archiveHashesAvailable
				? await File.ReadAllTextAsync(paths.ArchiveHashesPath, cancellationToken).ConfigureAwait(false)
				: "{}";
			if (archiveHashesAvailable && !CliGameDataIndexPlanArtifacts.IsValidCommunityArchiveHashesJson(archiveHashes))
			{
				diagnostics.Add(new CliGameDataDiagnostic("ArchiveHashesInvalid", "warning", false, "archivehashes.json 格式无效；将忽略社区显示资料并继续使用 GameData 权威事实。", paths.ArchiveHashesPath));
				archiveHashes = "{}";
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			diagnostics.Add(new CliGameDataDiagnostic("ArchiveHashesUnreadable", "warning", false, $"无法读取 archivehashes；将忽略社区显示资料并继续使用 GameData 权威事实：{exception.Message}", paths.ArchiveHashesPath, exception.ToString()));
			archiveHashes = "{}";
		}

		try
		{
			var status = await CoreServices.CreateAssetArchiveIndexService(paths)
				.GetIndexStatusAsync(resolvedGameDataDirectory, archiveHashes, cancellationToken)
				.ConfigureAwait(false);
			if (!status.IsCurrent)
			{
				diagnostics.Add(new CliGameDataDiagnostic(
					"GameDataIndexNotCurrent",
					"error",
					true,
					"GameData 资产索引不可用、无效或已过期；请先执行 gamedata index plan rebuild → validate → apply，或在桌面管理器设置页重建资产索引。",
					resolvedGameDataDirectory));
			}
			return new CliGameDataIndexInspection(
				status.GameDataDirectory,
				true,
				status.State.ToString().ToLowerInvariant(),
				status.IsCurrent,
				status.StoredFingerprint,
				status.CurrentSourceFingerprint,
				archiveHashesAvailable,
				DistinctDiagnostics(diagnostics));
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or FormatException)
		{
			diagnostics.Add(new CliGameDataDiagnostic("GameDataIndexUnreadable", "error", true, $"无法读取资产索引：{exception.Message}", paths.DbPath, exception.ToString()));
			return new CliGameDataIndexInspection(resolvedGameDataDirectory, true, "unavailable", false, null, null, archiveHashesAvailable, DistinctDiagnostics(diagnostics));
		}
	}

	public static async ValueTask<CliGameDataArchiveListInspection> ListArchivesAsync(
		CliWorkspace workspace,
		CancellationToken cancellationToken = default)
	{
		var index = await BuildIndexAsync(workspace, cancellationToken).ConfigureAwait(false);
		var diagnostics = new List<CliGameDataDiagnostic>(index.Diagnostics);
		if (string.IsNullOrWhiteSpace(workspace.ManagerRoot)) return new CliGameDataArchiveListInspection(index, Array.Empty<GameDataArchiveSummary>(), DistinctDiagnostics(diagnostics));
		try
		{
			var archives = await CoreServices.CreateAssetArchiveIndexService(new StoragePaths(workspace.ManagerRoot, workspace.LibraryRoot))
				.GetArchiveSummariesAsync(cancellationToken).ConfigureAwait(false);
			return new CliGameDataArchiveListInspection(index, archives, DistinctDiagnostics(diagnostics));
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
		{
			diagnostics.Add(new CliGameDataDiagnostic("GameDataArchiveListFailed", "error", true, exception.Message, Details: exception.ToString()));
			return new CliGameDataArchiveListInspection(index, Array.Empty<GameDataArchiveSummary>(), DistinctDiagnostics(diagnostics));
		}
	}

	public static async ValueTask<CliGameDataArchiveDetailsInspection> ShowArchiveAsync(
		CliWorkspace workspace,
		string archiveId,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(archiveId))
			throw new CliUserException("GameDataArchiveRequired", "gamedata archive show 需要一个 archive ID。 ");
		var index = await BuildIndexAsync(workspace, cancellationToken).ConfigureAwait(false);
		var diagnostics = new List<CliGameDataDiagnostic>(index.Diagnostics);
		if (string.IsNullOrWhiteSpace(workspace.ManagerRoot)) return new CliGameDataArchiveDetailsInspection(index, archiveId, null, DistinctDiagnostics(diagnostics));
		try
		{
			var details = await CoreServices.CreateAssetArchiveIndexService(new StoragePaths(workspace.ManagerRoot, workspace.LibraryRoot))
				.GetArchiveDetailsAsync(archiveId, cancellationToken).ConfigureAwait(false);
			if (details is null)
			{
				diagnostics.Add(new CliGameDataDiagnostic("GameDataArchiveNotFound", "error", true, $"资产索引中不存在 archive：{archiveId}。"));
			}
			return new CliGameDataArchiveDetailsInspection(index, archiveId, details, DistinctDiagnostics(diagnostics));
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
		{
			diagnostics.Add(new CliGameDataDiagnostic("GameDataArchiveDetailsFailed", "error", true, exception.Message, archiveId, exception.ToString()));
			return new CliGameDataArchiveDetailsInspection(index, archiveId, null, DistinctDiagnostics(diagnostics));
		}
	}

	public static async ValueTask<CliGameDataAssetLookupInspection> FindAssetsAsync(
		CliWorkspace workspace,
		IReadOnlyList<string> rawKeyPairs,
		CancellationToken cancellationToken = default)
	{
		var keys = ParseAssetKeyPairs(rawKeyPairs);
		var index = await BuildIndexAsync(workspace, cancellationToken).ConfigureAwait(false);
		var diagnostics = new List<CliGameDataDiagnostic>(index.Diagnostics);
		if (string.IsNullOrWhiteSpace(workspace.ManagerRoot)) return new CliGameDataAssetLookupInspection(index, Array.Empty<AssetArchiveMatch>(), DistinctDiagnostics(diagnostics));
		try
		{
			var matches = await CoreServices.CreateAssetArchiveIndexService(new StoragePaths(workspace.ManagerRoot, workspace.LibraryRoot))
				.FindAssetArchivesAsync(keys, cancellationToken).ConfigureAwait(false);
			return new CliGameDataAssetLookupInspection(index, matches, DistinctDiagnostics(diagnostics));
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
		{
			diagnostics.Add(new CliGameDataDiagnostic("GameDataAssetLookupFailed", "error", true, exception.Message, Details: exception.ToString()));
			return new CliGameDataAssetLookupInspection(index, Array.Empty<AssetArchiveMatch>(), DistinctDiagnostics(diagnostics));
		}
	}

	public static async ValueTask<CliGameDataOverrideInspection> InspectOverridesAsync(
		CliWorkspace workspace,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		var diagnostics = new List<CliGameDataDiagnostic>();
		if (!TryNormalizeReadOnlyDirectory(workspace.GameDataRoot, out var gameDataDirectory, diagnostics))
			return new CliGameDataOverrideInspection(workspace.GameDataRoot, false, "unverified", null, DistinctDiagnostics(diagnostics));
		var resolvedGameDataDirectory = gameDataDirectory
			?? throw new InvalidOperationException("只读 GameData 路径验证成功后缺少规范化目录。");
		try
		{
			var deployment = await CoreServices.CreateDeployedOverrideGraphService()
				.BuildAsync(resolvedGameDataDirectory, cancellationToken).ConfigureAwait(false);
			diagnostics.AddRange(deployment.Issues.Select(CreateDiagnostic));
			return new CliGameDataOverrideInspection(resolvedGameDataDirectory, true, "unverified", deployment, DistinctDiagnostics(diagnostics));
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or DirectoryNotFoundException or FileNotFoundException)
		{
			diagnostics.Add(new CliGameDataDiagnostic("GameDataOverrideInspectionFailed", "error", true, exception.Message, resolvedGameDataDirectory, exception.ToString()));
			return new CliGameDataOverrideInspection(resolvedGameDataDirectory, true, "unverified", null, DistinctDiagnostics(diagnostics));
		}
	}

	// Compatibility alias retained while callers move from the overly broad
	// "deployment" wording to the precise Data-Patch override terminology.
	public static ValueTask<CliGameDataOverrideInspection> InspectDeploymentAsync(
		CliWorkspace workspace,
		CancellationToken cancellationToken = default)
		=> InspectOverridesAsync(workspace, cancellationToken);

	public static async ValueTask<CliGameDataBrowserInspection> BuildBrowserAsync(
		CliWorkspace workspace,
		CancellationToken cancellationToken = default)
	{
		var index = await BuildIndexAsync(workspace, cancellationToken).ConfigureAwait(false);
		var diagnostics = new List<CliGameDataDiagnostic>(index.Diagnostics);
		if (!index.TargetIsValid || string.IsNullOrWhiteSpace(index.GameDataDirectory))
			return new CliGameDataBrowserInspection(index, null, DistinctDiagnostics(diagnostics));
		var state = await workspace.LoadLibraryAsync(cancellationToken).ConfigureAwait(false);
		if (string.IsNullOrWhiteSpace(workspace.ManagerRoot) || string.IsNullOrWhiteSpace(workspace.LibraryRoot))
			throw new CliUserException("LibraryNotConfigured", "gamedata browser 需要明确的 Mod 库。 ");
		try
		{
			var paths = new StoragePaths(workspace.ManagerRoot, workspace.LibraryRoot);
			await using var reader = CoreServices.CreateModInformationReader();
			var browser = CoreServices.CreateDirectGameDataArchiveBrowserService(paths, reader);
			var snapshot = await browser.BuildAsync(state.Snapshot, workspace.LibraryRoot, index.GameDataDirectory, cancellationToken).ConfigureAwait(false);
			if (snapshot is null)
				diagnostics.Add(new CliGameDataDiagnostic("GameDataBrowserIndexMissing", "error", true, "资产索引不存在，无法构建 GameData 浏览器。", index.GameDataDirectory));
			else
				diagnostics.AddRange(snapshot.Issues.Select(CreateDiagnostic));
			return new CliGameDataBrowserInspection(index, snapshot, DistinctDiagnostics(diagnostics));
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or DirectoryNotFoundException or FileNotFoundException)
		{
			diagnostics.Add(new CliGameDataDiagnostic("GameDataBrowserFailed", "error", true, exception.Message, index.GameDataDirectory, exception.ToString()));
			return new CliGameDataBrowserInspection(index, null, DistinctDiagnostics(diagnostics));
		}
	}

	private static IReadOnlySet<AssetKey> ParseAssetKeyPairs(IReadOnlyList<string> rawKeyPairs)
	{
		if (rawKeyPairs.Count == 0 || rawKeyPairs.Count % 2 != 0)
			throw new CliUserException("GameDataAssetKeyPairRequired", "gamedata asset find 需要一组或多组 <TypeID十六进制> <FileID十六进制>。 ");
		var keys = new HashSet<AssetKey>();
		for (var index = 0; index < rawKeyPairs.Count; index += 2)
		{
			keys.Add(ParseAssetKeyPair(rawKeyPairs[index], rawKeyPairs[index + 1]));
		}
		return keys;
	}

	internal static AssetKey ParseAssetKeyPair(string rawTypeId, string rawFileId)
		=> new(ParseHexUInt64(rawTypeId, "TypeID"), ParseHexUInt64(rawFileId, "FileID"));

	private static ulong ParseHexUInt64(string value, string label)
	{
		var normalized = value.Trim();
		if (normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) normalized = normalized[2..];
		if (normalized.Length == 0 || normalized.Length > 16
			|| !ulong.TryParse(normalized, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var parsed))
		{
			throw new CliUserException("GameDataAssetKeyInvalid", $"{label} 必须是最多 16 位的十六进制无符号整数：{value}");
		}
		return parsed;
	}

	// Index, archive and deployment inspection must work against an extracted or
	// copied GameData sample as well as a live game installation.  Unlike a
	// write-capable deployment, this path deliberately does not require a folder
	// named "data", a sibling bin directory, or helldivers2.exe.
	private static bool TryNormalizeReadOnlyDirectory(
		string? value,
		out string? directory,
		ICollection<CliGameDataDiagnostic> diagnostics)
	{
		directory = null;
		if (string.IsNullOrWhiteSpace(value))
		{
			diagnostics.Add(new CliGameDataDiagnostic("GameDataDirectoryRequired", "error", true, "必须指定要浏览的 GameData 目录。"));
			return false;
		}
		try
		{
			var candidate = value.Trim();
			if (candidate.IndexOf('\0') >= 0)
			{
				diagnostics.Add(new CliGameDataDiagnostic("GameDataPathInvalid", "error", true, "GameData 路径包含无效字符。", candidate));
				return false;
			}
			directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
			if (!Directory.Exists(directory))
			{
				diagnostics.Add(new CliGameDataDiagnostic("GameDataDirectoryMissing", "error", true, "要浏览的 GameData 目录不存在。", directory));
				return false;
			}
			return true;
		}
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
		{
			diagnostics.Add(new CliGameDataDiagnostic("GameDataPathInvalid", "error", true, exception.Message, value, exception.ToString()));
			return false;
		}
	}

	private static CliGameDataDiagnostic CreateDiagnostic(CoreIssue issue)
		=> new(
			issue.Code,
			issue.Severity == CoreIssueSeverity.Error ? "error" : "warning",
			issue.Severity == CoreIssueSeverity.Error,
			issue.Message,
			issue.FilePath,
			issue.ExceptionMessage);

	private static IReadOnlyList<CliGameDataDiagnostic> DistinctDiagnostics(IEnumerable<CliGameDataDiagnostic> diagnostics)
		=> diagnostics
			.GroupBy(item => $"{item.Code}\u001f{item.Path}\u001f{item.Message}", StringComparer.Ordinal)
			.Select(group => group.OrderByDescending(item => item.Blocking).First())
			.OrderByDescending(item => item.Blocking)
			.ThenBy(item => item.Code, StringComparer.Ordinal)
			.ToArray();
}
