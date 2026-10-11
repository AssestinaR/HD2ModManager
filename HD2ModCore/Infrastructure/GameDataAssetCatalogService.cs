using HD2ModCore.Application;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

/// <summary>一个资产的三段载荷。TOC 数据内联在包内，Stream 与 GPU 资源通常是独立区段。</summary>
public enum GameDataAssetPart
{
	TocData,
	Stream,
	GpuResources,
}

/// <summary>
/// 只读描述：某个归档里的一个资产条目。所有偏移都是相对其所在存储区段的原始值，
/// 服务负责把它们翻译成真实字节，调用方不需要知道 Legacy / DSAR / Bundled 的差别。
/// </summary>
public sealed record GameDataAssetDescriptor(
	string ArchiveId,
	bool UsesBundledStorage,
	ulong TypeId,
	ulong FileId,
	ulong TocDataOffset,
	uint TocDataSize,
	ulong StreamOffset,
	uint StreamSize,
	ulong GpuResourceOffset,
	uint GpuResourceSize)
{
	public AssetKey AssetKey => new(TypeId, FileId);

	public string TypeName => AssetTypeNameTable.Resolve(TypeId);

	public long TotalSize => (long)TocDataSize + StreamSize + GpuResourceSize;

	public uint SizeOf(GameDataAssetPart part) => part switch
	{
		GameDataAssetPart.TocData => TocDataSize,
		GameDataAssetPart.Stream => StreamSize,
		GameDataAssetPart.GpuResources => GpuResourceSize,
		_ => 0,
	};
}

public sealed record GameDataAssetScanResult(
	IReadOnlyList<GameDataAssetDescriptor> Assets,
	IReadOnlyList<CoreIssue> Issues);

// 作用：不依赖 SQLite 索引，直接遍历游戏数据包并列出/读取任意资产。
//       与 GameDataArchiveBrowserService 的区别在于：后者回答"哪些资产被覆盖"，
//       本服务回答"游戏里到底有哪些资产、它们的字节是什么"，因此必须覆盖全部包，
//       而不是只看被 Data-Patch 覆盖过的那部分。
// Purpose: Walks the game data packages directly to list and read arbitrary assets, with no SQLite index.
//          Where GameDataArchiveBrowserService answers "which assets are overridden", this answers
//          "which assets exist and what are their bytes", so it must cover every package.
public sealed class GameDataAssetCatalogService
{
	private readonly string _gameDataDirectory;
	private readonly IGameDataPackageResolver _resolver;
	private readonly IPatchTocScanner _scanner;

	public GameDataAssetCatalogService(
		string gameDataDirectory,
		IGameDataPackageResolver? resolver = null,
		IPatchTocScanner? scanner = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(gameDataDirectory);
		_gameDataDirectory = Path.GetFullPath(gameDataDirectory);
		_resolver = resolver ?? new GameDataPackageResolver(_gameDataDirectory);
		_scanner = scanner ?? new PatchTocScanner();
	}

	/// <summary>
	/// 列出资产。typeIds 为空表示不按类型过滤；archiveId 非空表示只扫一个归档（快路径）。
	/// 单个包读取失败不会中断整体枚举：失败原因以 CoreIssue 形式一并返回。
	/// </summary>
	public async ValueTask<GameDataAssetScanResult> EnumerateAsync(
		IReadOnlyCollection<ulong>? typeIds = null,
		string? archiveId = null,
		CancellationToken cancellationToken = default)
	{
		var assets = new List<GameDataAssetDescriptor>();
		var issues = new List<CoreIssue>();
		var packages = await ResolvePackagesAsync(archiveId, issues, cancellationToken).ConfigureAwait(false);

		foreach (var package in packages)
		{
			cancellationToken.ThrowIfCancellationRequested();
			GameDataPackageToc? toc;
			try
			{
				toc = await _resolver.GetPackageTocAsync(package, cancellationToken).ConfigureAwait(false);
			}
			catch (Exception exception) when (exception is IOException or InvalidDataException or EndOfStreamException or OverflowException)
			{
				issues.Add(new CoreIssue(CoreIssueSeverity.Warning, "GameDataPackageTocUnreadable",
					$"无法读取游戏数据包 TOC：{package}：{exception.Message}", package, ExceptionMessage: exception.ToString()));
				continue;
			}

			if (toc is null) continue;

			IReadOnlyList<PatchTocEntry> entries;
			try
			{
				entries = _scanner.ScanEntries(toc.Data, package, toc.UsesSlimEntryOffset);
			}
			catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException or OverflowException or ArgumentOutOfRangeException)
			{
				issues.Add(new CoreIssue(CoreIssueSeverity.Warning, "GameDataPackageTocInvalid",
					$"无法解析游戏数据包条目表：{package}：{exception.Message}", package, ExceptionMessage: exception.ToString()));
				continue;
			}

			foreach (var entry in entries)
			{
				if (typeIds is { Count: > 0 } && !typeIds.Contains(entry.AssetKey.TypeId)) continue;
				assets.Add(new GameDataAssetDescriptor(
					package,
					toc.UsesSlimEntryOffset,
					entry.AssetKey.TypeId,
					entry.AssetKey.FileId,
					entry.TocDataOffset,
					entry.TocDataSize,
					entry.StreamOffset,
					entry.StreamSize,
					entry.GpuResourceOffset,
					entry.GpuResourceSize));
			}
		}

		var ordered = assets
			.OrderBy(asset => asset.ArchiveId, StringComparer.OrdinalIgnoreCase)
			.ThenBy(asset => asset.TypeName, StringComparer.Ordinal)
			.ThenBy(asset => asset.FileId)
			.ToArray();
		return new GameDataAssetScanResult(ordered, issues);
	}

	/// <summary>读取一段载荷；该段声明长度为 0 或区段无法解析时返回 null。</summary>
	public async ValueTask<byte[]?> ReadPartAsync(
		GameDataAssetDescriptor asset,
		GameDataAssetPart part,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(asset);
		var size = asset.SizeOf(part);
		if (size == 0) return null;

		var offset = part switch
		{
			GameDataAssetPart.TocData => asset.TocDataOffset,
			GameDataAssetPart.Stream => asset.StreamOffset,
			_ => asset.GpuResourceOffset,
		};

		if (asset.UsesBundledStorage)
		{
			return await _resolver.GetPackageResourceAsync(asset.ArchiveId, offset, size, cancellationToken).ConfigureAwait(false);
		}

		// Legacy 布局把三段放在三个并列文件里；DSAR/Bundled 才把它们拼进同一个归档。
		// A legacy install keeps the three parts in three sibling files; only DSAR/Bundled pack them together.
		var fileName = part switch
		{
			GameDataAssetPart.TocData => asset.ArchiveId,
			GameDataAssetPart.Stream => asset.ArchiveId + ".stream",
			_ => asset.ArchiveId + ".gpu_resources",
		};
		return await ReadFileRangeAsync(Path.Combine(_gameDataDirectory, fileName), offset, size, cancellationToken).ConfigureAwait(false);
	}

	public static string FileNameSuffix(GameDataAssetPart part) => part switch
	{
		GameDataAssetPart.TocData => ".toc",
		GameDataAssetPart.Stream => ".stream",
		_ => ".gpu_resources",
	};

	private async ValueTask<IReadOnlyList<string>> ResolvePackagesAsync(
		string? archiveId,
		ICollection<CoreIssue> issues,
		CancellationToken cancellationToken)
	{
		if (!string.IsNullOrWhiteSpace(archiveId))
		{
			var normalized = Path.GetFileName(archiveId.Trim());
			var toc = await _resolver.GetPackageTocAsync(normalized, cancellationToken).ConfigureAwait(false);
			if (toc is null)
			{
				issues.Add(new CoreIssue(CoreIssueSeverity.Error, "GameDataArchiveNotFound",
					$"在游戏数据目录中找不到可解析的归档：{normalized}", normalized));
			}

			return [normalized];
		}

		return await _resolver.GetPackageNamesAsync(cancellationToken).ConfigureAwait(false);
	}

	private static async ValueTask<byte[]?> ReadFileRangeAsync(
		string path,
		ulong offset,
		uint size,
		CancellationToken cancellationToken)
	{
		if (!File.Exists(path)) return null;
		await using var stream = new FileStream(
			path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
			bufferSize: 64 * 1024, options: FileOptions.Asynchronous | FileOptions.SequentialScan);
		if (offset > (ulong)stream.Length || (ulong)stream.Length - offset < size) return null;
		stream.Position = checked((long)offset);
		var buffer = new byte[size];
		var read = 0;
		while (read < buffer.Length)
		{
			var count = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false);
			if (count == 0) return null;
			read += count;
		}

		return buffer;
	}
}
