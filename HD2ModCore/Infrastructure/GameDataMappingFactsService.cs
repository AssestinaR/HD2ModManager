using System.Security.Cryptography;
using System.Text;
using HD2ModCore.Application;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

// Purpose: Maps AssetKeys through the SQLite Game Data index and fingerprints both index and readable metadata inputs.
public sealed class GameDataMappingFactsService : IGameDataMappingFactsService
{
	private readonly IAssetArchiveIndexService _indexService;
	private readonly IAssetMetadataCatalogProvider _catalogProvider;
	private readonly StoragePaths _paths;

	public GameDataMappingFactsService(IAssetArchiveIndexService indexService, IAssetMetadataCatalogProvider catalogProvider, StoragePaths paths)
	{
		_indexService = indexService ?? throw new ArgumentNullException(nameof(indexService));
		_catalogProvider = catalogProvider ?? throw new ArgumentNullException(nameof(catalogProvider));
		_paths = paths ?? throw new ArgumentNullException(nameof(paths));
	}

	public async ValueTask<GameDataMappingFacts> MapAsync(IReadOnlySet<AssetKey> assetKeys, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(assetKeys);
		var fingerprint = await _indexService.GetFingerprintAsync(cancellationToken).ConfigureAwait(false);
		var indexGeneration = fingerprint?.SourceFingerprint ?? "missing";
		var metadataGeneration = ComputeMetadataGeneration();
		var mappingGeneration = Hash($"{indexGeneration}\n{metadataGeneration}");
		// 键 = 数据目录 + 索引代 + 元数据代 + 选择器指纹。
		// 前三段保证"游戏数据一变就重算"（这是本服务此前完全没有缓存、每次都要逐键查库的原因）；
		// 第四段把"要的是哪批资产"也放进身份，避免不同页面互相拿到对方的子集。
		var cacheKey = BuildCacheKey(mappingGeneration, assetKeys);
		if (GameDataFactCache.TryGet(cacheKey, out GameDataMappingFacts cached)) return cached;

		var issues = new List<CoreIssue>();
		var catalog = assetKeys.Count == 0
			? AssetMetadataCatalog.Empty
			: await _catalogProvider.LoadAsync(cancellationToken).ConfigureAwait(false);
		IReadOnlyList<AssetArchiveMatch> matches;
		try
		{
			matches = assetKeys.Count == 0
				? Array.Empty<AssetArchiveMatch>()
				: await _indexService.FindAssetArchivesAsync(assetKeys, cancellationToken).ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is not OperationCanceledException)
		{
			matches = Array.Empty<AssetArchiveMatch>();
			issues.Add(new CoreIssue(CoreIssueSeverity.Error, "GameDataMappingFailed", exception.Message, _paths.DbPath, ExceptionMessage: exception.ToString()));
		}

		var archivesByAsset = matches.ToDictionary(match => match.AssetKey, match => match.Archives);
		var mapped = new Dictionary<AssetKey, GameDataMappedAssetFact>();
		foreach (var assetKey in assetKeys.OrderBy(key => key.TypeId).ThenBy(key => key.FileId))
		{
			archivesByAsset.TryGetValue(assetKey, out var archives);
			var targets = (archives ?? Array.Empty<ArchiveMetadata>())
				.DistinctBy(archive => archive.ArchiveId, StringComparer.OrdinalIgnoreCase)
				.OrderBy(archive => archive.CategoryOrder)
				.ThenBy(archive => archive.ArchiveOrder)
				.ThenBy(archive => archive.ArchiveId, StringComparer.OrdinalIgnoreCase)
				.ToList();
			var file = catalog.FindFile(assetKey.FileId);
			var type = catalog.FindType(assetKey.TypeId);
			mapped[assetKey] = new GameDataMappedAssetFact(
				assetKey,
				file?.FriendlyName ?? assetKey.FileId.ToString(),
				type?.Name ?? $"0x{assetKey.TypeId:x16}",
				type?.Category ?? AssetTypeCategory.Unknown,
				targets);
		}

		var result = new GameDataMappingFacts(mappingGeneration, indexGeneration, metadataGeneration, DateTimeOffset.UtcNow, mapped, issues, catalog);
		GameDataFactCache.Set(cacheKey, result);
		return result;
	}

	// 作用：把选择器（要哪批资产）压成一个定长指纹，作为缓存身份的一部分。
	// Purpose: Compresses the selector (which assets are wanted) into a fixed-length fingerprint.
	private string BuildCacheKey(string mappingGeneration, IReadOnlySet<AssetKey> assetKeys)
	{
		var selector = new StringBuilder();
		foreach (var key in assetKeys.OrderBy(key => key.TypeId).ThenBy(key => key.FileId))
		{
			selector.Append(key.TypeId.ToString("x16")).Append(':').Append(key.FileId.ToString("x16")).Append(',');
		}

		return $"gamedata-mapping|{_paths.DataDirectory.ToLowerInvariant()}|{mappingGeneration}|{Hash(selector.ToString())}";
	}

	private string ComputeMetadataGeneration()
	{
		var builder = new StringBuilder();
		foreach (var path in new[] { _paths.AssetMetadataManifestPath, _paths.ArchiveHashesPath, _paths.FriendlyNamesPath, _paths.TypeHashesPath })
		{
			if (!File.Exists(path)) continue;
			var file = new FileInfo(path);
			builder.Append(file.Name.ToLowerInvariant()).Append(':').Append(file.Length).Append(':').Append(file.LastWriteTimeUtc.Ticks).AppendLine();
		}
		return Hash(builder.ToString());
	}

	private static string Hash(string value)
		=> Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
