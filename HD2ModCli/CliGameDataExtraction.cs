using System.Text;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCli;

/// <summary>
/// 一个可提取资产的稳定描述。64 位 ID 一律投影为 0x 十六进制字符串，
/// 既避免 JSON 数字精度丢失，也便于直接粘回 CLI 的后续命令。
/// </summary>
public sealed record CliGameDataAssetEntry(
	string ArchiveId,
	string TypeId,
	string TypeName,
	string FileId,
	long TotalSize,
	uint TocDataSize,
	uint StreamSize,
	uint GpuResourceSize);

public sealed record CliGameDataExtractFile(string Part, string Path, long Size);

public sealed record CliGameDataExtractListInspection(
	string? GameDataDirectory,
	bool TargetIsValid,
	IReadOnlyList<CliGameDataAssetEntry> Assets,
	IReadOnlyList<CliGameDataDiagnostic> Diagnostics)
{
	public bool IsReady => TargetIsValid && !Diagnostics.Any(diagnostic => diagnostic.Blocking);
}

/// <summary>show 与 asset 共用：show 时 Files 为空，asset 时 Files 记录实际写出的文件。</summary>
public sealed record CliGameDataExtractAssetInspection(
	string? GameDataDirectory,
	bool TargetIsValid,
	CliGameDataAssetEntry? Asset,
	IReadOnlyList<CliGameDataExtractFile> Files,
	IReadOnlyList<CliGameDataDiagnostic> Diagnostics)
{
	public bool IsReady => TargetIsValid && Asset is not null && !Diagnostics.Any(diagnostic => diagnostic.Blocking);
}

public sealed record CliGameDataExtractArchiveInspection(
	string? GameDataDirectory,
	bool TargetIsValid,
	string RequestedArchiveId,
	int AssetCount,
	int FileCount,
	long TotalBytes,
	string? OutputDirectory,
	string? ManifestPath,
	IReadOnlyList<CliGameDataDiagnostic> Diagnostics)
{
	public bool IsReady => TargetIsValid && OutputDirectory is not null && !Diagnostics.Any(diagnostic => diagnostic.Blocking);
}

// 作用：把"直接读取游戏数据包"的能力暴露给 CLI，让 Mod 作者与我们自己都不必再写临时工具。
//       与 gamedata archive / asset 等命令不同，这条路径不依赖管理器的 SQLite 资产索引，
//       也不要求 --manager-root：它只读游戏包本身，因此在一台没有装管理器的机器上同样可用。
// Purpose: Exposes direct game-package reading to the CLI so neither Mod authors nor we need
//          throwaway tools. Unlike the index-backed gamedata archive/asset commands this path
//          needs no SQLite index and no --manager-root: it only reads the game packages, so it
//          works on a machine that has no manager installed.
internal static class CliGameDataExtraction
{
	private const string AllTypesToken = "all";

	public static async ValueTask<CliGameDataExtractListInspection> ListAsync(
		CliWorkspace workspace,
		string? typeSpec,
		string? archiveId,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		var diagnostics = new List<CliGameDataDiagnostic>();
		if (!CliGameDataInspection.TryNormalizeReadOnlyDirectory(workspace.GameDataRoot, out var gameDataDirectory, diagnostics))
		{
			return new CliGameDataExtractListInspection(
				workspace.GameDataRoot, false, Array.Empty<CliGameDataAssetEntry>(), CliGameDataInspection.DistinctDiagnostics(diagnostics));
		}

		var directory = gameDataDirectory!;
		if (!TryResolveTypeFilter(typeSpec, out var typeIds, diagnostics))
		{
			return new CliGameDataExtractListInspection(
				directory, true, Array.Empty<CliGameDataAssetEntry>(), CliGameDataInspection.DistinctDiagnostics(diagnostics));
		}

		try
		{
			var scan = await CoreServices.CreateGameDataAssetCatalogService(directory)
				.EnumerateAsync(typeIds, NormalizeArchiveId(archiveId), cancellationToken).ConfigureAwait(false);
			diagnostics.AddRange(scan.Issues.Select(CliGameDataInspection.CreateDiagnostic));
			return new CliGameDataExtractListInspection(
				directory, true, scan.Assets.Select(CreateEntry).ToArray(), CliGameDataInspection.DistinctDiagnostics(diagnostics));
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or ArgumentException)
		{
			diagnostics.Add(new CliGameDataDiagnostic(
				"GameDataExtractListFailed", "error", true, $"无法直接读取游戏数据包：{exception.Message}", directory, exception.ToString()));
			return new CliGameDataExtractListInspection(
				directory, true, Array.Empty<CliGameDataAssetEntry>(), CliGameDataInspection.DistinctDiagnostics(diagnostics));
		}
	}

	public static ValueTask<CliGameDataExtractAssetInspection> ShowAsync(
		CliWorkspace workspace,
		AssetKey key,
		string? archiveId,
		CancellationToken cancellationToken = default)
		=> LocateOrExtractAsync(workspace, key, archiveId, outputDirectory: null, overwrite: false, cancellationToken);

	public static ValueTask<CliGameDataExtractAssetInspection> ExtractAssetAsync(
		CliWorkspace workspace,
		AssetKey key,
		string? archiveId,
		string outputDirectory,
		bool overwrite,
		CancellationToken cancellationToken = default)
		=> LocateOrExtractAsync(workspace, key, archiveId, outputDirectory, overwrite, cancellationToken);

	public static async ValueTask<CliGameDataExtractArchiveInspection> ExtractArchiveAsync(
		CliWorkspace workspace,
		string archiveId,
		string outputDirectory,
		bool overwrite,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		var diagnostics = new List<CliGameDataDiagnostic>();
		var normalizedArchive = NormalizeArchiveId(archiveId);
		if (!CliGameDataInspection.TryNormalizeReadOnlyDirectory(workspace.GameDataRoot, out var gameDataDirectory, diagnostics))
		{
			return new CliGameDataExtractArchiveInspection(
				workspace.GameDataRoot, false, normalizedArchive ?? archiveId, 0, 0, 0, null, null, CliGameDataInspection.DistinctDiagnostics(diagnostics));
		}

		var directory = gameDataDirectory!;
		if (normalizedArchive is null)
		{
			diagnostics.Add(new CliGameDataDiagnostic("GameDataArchiveRequired", "error", true, "gamedata extract archive 需要一个归档 ID。"));
			return new CliGameDataExtractArchiveInspection(
				directory, true, archiveId, 0, 0, 0, null, null, CliGameDataInspection.DistinctDiagnostics(diagnostics));
		}

		if (!TryResolveOutputDirectory(directory, outputDirectory, overwrite, diagnostics, out var resolvedOutput))
		{
			return new CliGameDataExtractArchiveInspection(
				directory, true, normalizedArchive, 0, 0, 0, null, null, CliGameDataInspection.DistinctDiagnostics(diagnostics));
		}

		var target = resolvedOutput!;
		var service = CoreServices.CreateGameDataAssetCatalogService(directory);
		GameDataAssetScanResult scan;
		try
		{
			scan = await service.EnumerateAsync(typeIds: null, normalizedArchive, cancellationToken).ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or ArgumentException)
		{
			diagnostics.Add(new CliGameDataDiagnostic(
				"GameDataExtractReadFailed", "error", true, $"无法直接读取游戏数据包：{exception.Message}", directory, exception.ToString()));
			return new CliGameDataExtractArchiveInspection(
				directory, true, normalizedArchive, 0, 0, 0, null, null, CliGameDataInspection.DistinctDiagnostics(diagnostics));
		}

		diagnostics.AddRange(scan.Issues.Select(CliGameDataInspection.CreateDiagnostic));
		if (scan.Assets.Count == 0 && !diagnostics.Any(diagnostic => diagnostic.Blocking))
		{
			diagnostics.Add(new CliGameDataDiagnostic("GameDataArchiveEmpty", "error", true, $"归档中未找到可提取的资产：{normalizedArchive}。", directory));
		}

		var manifest = new List<object>();
		var fileCount = 0;
		long totalBytes = 0;
		foreach (var asset in scan.Assets)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var assetDirectory = Path.Combine(target, SanitizeFileName(asset.TypeName));
			var written = new List<CliGameDataExtractFile>();
			try
			{
				await WriteAssetPartsAsync(service, asset, assetDirectory, overwrite, written, cancellationToken).ConfigureAwait(false);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
			{
				diagnostics.Add(new CliGameDataDiagnostic(
					"GameDataExtractWriteFailed", "error", true,
					$"写入资产 {Describe(asset.AssetKey)} 失败：{exception.Message}", assetDirectory, exception.ToString()));
				break;
			}

			fileCount += written.Count;
			totalBytes += written.Sum(file => file.Size);
			manifest.Add(new
			{
				typeId = $"0x{asset.TypeId:x16}",
				asset.TypeName,
				fileId = $"0x{asset.FileId:x16}",
				asset.TocDataSize,
				asset.StreamSize,
				asset.GpuResourceSize,
				files = written.Select(file => Path.GetRelativePath(target, file.Path)).ToArray(),
			});
		}

		string? manifestPath = null;
		if (!diagnostics.Any(diagnostic => diagnostic.Blocking))
		{
			manifestPath = Path.Combine(target, "extract-manifest.json");
			var document = new
			{
				archiveId = normalizedArchive,
				gameDataDirectory = directory,
				assetCount = manifest.Count,
				fileCount,
				totalBytes,
				assets = manifest,
			};
			try
			{
				await CliOutput.WriteExplicitFileAsync(manifestPath, CliOutput.ToJson(document), overwrite, cancellationToken).ConfigureAwait(false);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				diagnostics.Add(new CliGameDataDiagnostic(
					"GameDataExtractWriteFailed", "error", true, $"写入提取清单失败：{exception.Message}", manifestPath, exception.ToString()));
				manifestPath = null;
			}
		}

		return new CliGameDataExtractArchiveInspection(
			directory, true, normalizedArchive, manifest.Count, fileCount, totalBytes, target, manifestPath,
			CliGameDataInspection.DistinctDiagnostics(diagnostics));
	}

	private static async ValueTask<CliGameDataExtractAssetInspection> LocateOrExtractAsync(
		CliWorkspace workspace,
		AssetKey key,
		string? archiveId,
		string? outputDirectory,
		bool overwrite,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		var diagnostics = new List<CliGameDataDiagnostic>();
		var files = new List<CliGameDataExtractFile>();
		if (!CliGameDataInspection.TryNormalizeReadOnlyDirectory(workspace.GameDataRoot, out var gameDataDirectory, diagnostics))
		{
			return new CliGameDataExtractAssetInspection(
				workspace.GameDataRoot, false, null, files, CliGameDataInspection.DistinctDiagnostics(diagnostics));
		}

		var directory = gameDataDirectory!;
		string? target = null;
		if (outputDirectory is not null
			&& !TryResolveOutputDirectory(directory, outputDirectory, overwrite, diagnostics, out target))
		{
			return new CliGameDataExtractAssetInspection(
				directory, true, null, files, CliGameDataInspection.DistinctDiagnostics(diagnostics));
		}

		var service = CoreServices.CreateGameDataAssetCatalogService(directory);
		GameDataAssetScanResult scan;
		try
		{
			scan = await service.EnumerateAsync([key.TypeId], NormalizeArchiveId(archiveId), cancellationToken).ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or ArgumentException)
		{
			diagnostics.Add(new CliGameDataDiagnostic(
				"GameDataExtractReadFailed", "error", true, $"无法直接读取游戏数据包：{exception.Message}", directory, exception.ToString()));
			return new CliGameDataExtractAssetInspection(
				directory, true, null, files, CliGameDataInspection.DistinctDiagnostics(diagnostics));
		}

		diagnostics.AddRange(scan.Issues.Select(CliGameDataInspection.CreateDiagnostic));
		var candidates = scan.Assets.Where(item => item.FileId == key.FileId).ToArray();
		if (candidates.Length == 0)
		{
			diagnostics.Add(new CliGameDataDiagnostic(
				"GameDataAssetNotFound", "error", true,
				$"游戏数据包中不存在该资产：{Describe(key)}。可用 gamedata extract list {AssetTypeNameTable.Resolve(key.TypeId)} 查看同类型资产。", directory));
			return new CliGameDataExtractAssetInspection(
				directory, true, null, files, CliGameDataInspection.DistinctDiagnostics(diagnostics));
		}

		if (candidates.Length > 1 && string.IsNullOrWhiteSpace(archiveId))
		{
			var archives = string.Join("、", candidates
				.Select(item => item.ArchiveId)
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.OrderBy(id => id, StringComparer.OrdinalIgnoreCase));
			diagnostics.Add(new CliGameDataDiagnostic(
				"GameDataAssetAmbiguous", "error", true,
				$"该资产存在于多个归档中：{archives}。请在命令末尾追加目标归档 ID 以消除歧义。", directory));
			return new CliGameDataExtractAssetInspection(
				directory, true, null, files, CliGameDataInspection.DistinctDiagnostics(diagnostics));
		}

		var asset = candidates[0];
		if (target is not null)
		{
			try
			{
				await WriteAssetPartsAsync(service, asset, target, overwrite, files, cancellationToken).ConfigureAwait(false);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
			{
				diagnostics.Add(new CliGameDataDiagnostic(
					"GameDataExtractWriteFailed", "error", true, $"写入提取结果失败：{exception.Message}", target, exception.ToString()));
			}
		}

		return new CliGameDataExtractAssetInspection(
			directory, true, CreateEntry(asset), files, CliGameDataInspection.DistinctDiagnostics(diagnostics));
	}

	private static async ValueTask WriteAssetPartsAsync(
		GameDataAssetCatalogService service,
		GameDataAssetDescriptor asset,
		string outputDirectory,
		bool overwrite,
		ICollection<CliGameDataExtractFile> files,
		CancellationToken cancellationToken)
	{
		var baseName = $"{SanitizeFileName(asset.TypeName)}_0x{asset.FileId:x16}";
		foreach (var part in new[] { GameDataAssetPart.TocData, GameDataAssetPart.Stream, GameDataAssetPart.GpuResources })
		{
			if (asset.SizeOf(part) == 0) continue;
			var data = await service.ReadPartAsync(asset, part, cancellationToken).ConfigureAwait(false);
			if (data is null || data.Length == 0) continue;
			var path = Path.Combine(outputDirectory, baseName + GameDataAssetCatalogService.FileNameSuffix(part));
			if (File.Exists(path) && !overwrite)
				throw new CliUserException("OutputAlreadyExists", $"输出文件已存在：{path}。如需覆盖，请显式传入 --overwrite。");
			Directory.CreateDirectory(outputDirectory);
			await File.WriteAllBytesAsync(path, data, cancellationToken).ConfigureAwait(false);
			files.Add(new CliGameDataExtractFile(PartLabel(part), path, data.Length));
		}
	}

	private static bool TryResolveTypeFilter(
		string? typeSpec,
		out IReadOnlyCollection<ulong>? typeIds,
		ICollection<CliGameDataDiagnostic> diagnostics)
	{
		typeIds = null;
		if (string.IsNullOrWhiteSpace(typeSpec)) return true;
		var trimmed = typeSpec.Trim();
		if (trimmed is "*" || string.Equals(trimmed, AllTypesToken, StringComparison.OrdinalIgnoreCase)) return true;

		var resolved = new HashSet<ulong>();
		foreach (var token in trimmed.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
		{
			if (AssetTypeNameTable.TryResolve(token, out var typeId))
			{
				resolved.Add(typeId);
				continue;
			}

			diagnostics.Add(new CliGameDataDiagnostic(
				"GameDataAssetTypeUnknown", "error", true,
				$"无法识别的资产类型：{token}。请使用类型名（如 unit、bones、state_machine）或 0x 十六进制哈希；输入 all 表示不按类型过滤。"));
			return false;
		}

		typeIds = resolved.Count == 0 ? null : resolved;
		return true;
	}

	private static bool TryResolveOutputDirectory(
		string gameDataDirectory,
		string outputDirectory,
		bool overwrite,
		ICollection<CliGameDataDiagnostic> diagnostics,
		out string? resolved)
	{
		resolved = null;
		if (string.IsNullOrWhiteSpace(outputDirectory))
		{
			diagnostics.Add(new CliGameDataDiagnostic("OutputRequired", "error", true, "提取命令必须显式提供 --output <输出目录>。"));
			return false;
		}

		try
		{
			var candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDirectory.Trim()));
			if (File.Exists(candidate))
			{
				diagnostics.Add(new CliGameDataDiagnostic("OutputPathInvalid", "error", true, "输出路径已是一个文件，无法作为目录使用。", candidate));
				return false;
			}

			// 提取结果绝不能落进游戏 data 目录，否则可能与游戏文件重名并覆盖权威数据。
			// Extraction must never land inside the game data directory, where it could shadow authoritative game files.
			if (IsSameOrNested(candidate, gameDataDirectory) || IsSameOrNested(gameDataDirectory, candidate))
			{
				diagnostics.Add(new CliGameDataDiagnostic(
					"OutputInsideGameData", "error", true, "输出目录不能位于游戏 data 目录内，也不能包含游戏 data 目录。", candidate));
				return false;
			}

			if (Directory.Exists(candidate) && !overwrite && Directory.EnumerateFileSystemEntries(candidate).Any())
			{
				diagnostics.Add(new CliGameDataDiagnostic(
					"OutputDirectoryNotEmpty", "error", true, "输出目录已存在且非空；如需写入，请显式传入 --overwrite。", candidate));
				return false;
			}

			resolved = candidate;
			return true;
		}
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
		{
			diagnostics.Add(new CliGameDataDiagnostic("OutputPathInvalid", "error", true, $"输出目录无效：{exception.Message}", outputDirectory));
			return false;
		}
	}

	private static bool IsSameOrNested(string candidate, string parent)
	{
		var normalizedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
		var normalizedCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
		if (string.Equals(normalizedCandidate, normalizedParent, StringComparison.OrdinalIgnoreCase)) return true;
		return normalizedCandidate.StartsWith(normalizedParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
	}

	private static CliGameDataAssetEntry CreateEntry(GameDataAssetDescriptor asset)
		=> new(
			asset.ArchiveId,
			$"0x{asset.TypeId:x16}",
			asset.TypeName,
			$"0x{asset.FileId:x16}",
			asset.TotalSize,
			asset.TocDataSize,
			asset.StreamSize,
			asset.GpuResourceSize);

	private static string Describe(AssetKey key)
		=> $"{AssetTypeNameTable.Resolve(key.TypeId)} (type=0x{key.TypeId:x16}, file=0x{key.FileId:x16})";

	private static string PartLabel(GameDataAssetPart part) => part switch
	{
		GameDataAssetPart.TocData => "toc",
		GameDataAssetPart.Stream => "stream",
		_ => "gpu_resources",
	};

	private static string? NormalizeArchiveId(string? archiveId)
		=> string.IsNullOrWhiteSpace(archiveId) ? null : Path.GetFileName(archiveId.Trim());

	private static string SanitizeFileName(string value)
	{
		var invalid = Path.GetInvalidFileNameChars();
		var builder = new StringBuilder(value.Length);
		foreach (var character in value)
		{
			builder.Append(Array.IndexOf(invalid, character) >= 0 ? '_' : character);
		}

		return builder.Length == 0 ? "unknown" : builder.ToString();
	}
}
