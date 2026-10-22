using HD2ModCore.Application;
using HD2ModAdaptation.Analysis;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

// 作用：扫描节点目录内所有 .patch_n 文件的 TOC 来聚合资产键集合（缓存到内存以避免重复扫描）。
// Purpose: Aggregates AssetKeys by scanning TOCs of .patch_n files within a node directory (in-memory cached to avoid re-scans).
public sealed class AssetKeySetProvider : IAssetKeySetProvider
{
	private readonly IPatchFileNameParser _parser;
	private readonly IPatchTocScanner _scanner;
	private readonly IPatchGroupAnalysisProvider? _analysisProvider;
	private readonly IModInformationReader? _informationReader;
	private readonly ModInformationRequestContext? _readerContext;
	private readonly Dictionary<string, HashSet<AssetKey>> _cache = new(StringComparer.OrdinalIgnoreCase);

	public AssetKeySetProvider(IPatchFileNameParser parser, IPatchTocScanner scanner)
	{
		_parser = parser ?? throw new ArgumentNullException(nameof(parser));
		_scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
	}

	public AssetKeySetProvider(IPatchGroupAnalysisProvider analysisProvider)
	{
		_analysisProvider = analysisProvider ?? throw new ArgumentNullException(nameof(analysisProvider));
		_parser = null!;
		_scanner = null!;
	}

	// 作用：冲突检测通过统一读取器取得 TOC 资产目录；旧扫描器/provider 重载仍保留给兼容和测试。
	// Purpose: Routes conflict-key discovery through the unified reader while retaining legacy overloads.
	public AssetKeySetProvider(IModInformationReader informationReader)
	{
		_informationReader = informationReader ?? throw new ArgumentNullException(nameof(informationReader));
		_readerContext = ModInformationRequestContext.Create(
			ModInformationCacheScope.Session,
			operationName: "AssetKeySet",
			memoryBudgetBytes: 32L * 1024L * 1024L);
		_parser = new PatchFileNameParser();
		_scanner = null!;
	}

	public async ValueTask<IReadOnlySet<AssetKey>> GetAssetKeysAsync(ModNode node, string modsRootDirectory, CancellationToken cancellationToken = default)
	{
		if (node is null)
		{
			throw new ArgumentNullException(nameof(node));
		}
		if (string.IsNullOrWhiteSpace(modsRootDirectory))
		{
			throw new ArgumentException("Value cannot be null or whitespace.", nameof(modsRootDirectory));
		}

		var nodeDir = Path.Combine(modsRootDirectory, node.RelativePath);
		var key = Path.GetFullPath(nodeDir);

		lock (_cache)
		{
			if (_cache.TryGetValue(key, out var cached))
			{
				return cached;
			}
		}

		var result = new HashSet<AssetKey>();
		if (!Directory.Exists(nodeDir))
		{
			Cache(key, result);
			return result;
		}

		if (_informationReader is not null)
		{
			foreach (var filePath in ModNodePayloadLayout.EnumerateEffectiveFilesByName(nodeDir).Values
				.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (!TryIsBasePatch(filePath)) continue;
				var index = await _informationReader.ReadPatchIndexAsync(
					new ModInformationReadRequest(filePath, _readerContext, NodeId: node.Id),
					cancellationToken).ConfigureAwait(false);
				if (index.Data is null) continue;
				foreach (var entry in index.Data.Entries)
				{
					result.Add(new AssetKey(entry.AssetKey.TypeId, entry.AssetKey.FileId));
				}
			}
			Cache(key, result);
			return result;
		}

		if (_analysisProvider is not null)
		{
			var analyses = await _analysisProvider.AnalyzeNodeAsync(node, modsRootDirectory, cancellationToken).ConfigureAwait(false);
			foreach (var asset in analyses.SelectMany(analysis => analysis.Assets))
			{
				result.Add(new AssetKey(asset.AssetKey.TypeId, asset.AssetKey.FileId));
			}
			Cache(key, result);
			return result;
		}

		foreach (var filePath in ModNodePayloadLayout.EnumerateEffectiveFilesByName(nodeDir).Values)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var name = Path.GetFileName(filePath);
			if (!_parser.TryParse(name, out var info) || info is null)
			{
				continue;
			}
			if (info.SidecarKind != PatchSidecarKind.Base)
			{
				continue;
			}

			var keys = await _scanner.ScanAssetKeysAsync(filePath, cancellationToken).ConfigureAwait(false);
			result.UnionWith(keys);
		}

		Cache(key, result);
		return result;
	}

	private bool TryIsBasePatch(string filePath)
		=> _parser is not null
			&& _parser.TryParse(Path.GetFileName(filePath), out var info)
			&& info is not null
			&& info.SidecarKind == PatchSidecarKind.Base;

	private void Cache(string key, HashSet<AssetKey> value)
	{
		lock (_cache)
		{
			_cache[key] = value;
		}
	}
}
