using HD2ModCore.Domain;

namespace HD2ModCore.Application;

public interface IAssetArchiveIndexService
{
	ValueTask<bool> IndexExistsAsync(CancellationToken cancellationToken = default);

	ValueTask<GameDataIndexFingerprint?> GetFingerprintAsync(CancellationToken cancellationToken = default);

	ValueTask<IReadOnlyList<GameDataArchiveSummary>> GetArchiveSummariesAsync(
		CancellationToken cancellationToken = default);

	ValueTask<GameDataArchiveDetails?> GetArchiveDetailsAsync(
		string packageName,
		CancellationToken cancellationToken = default);

	ValueTask<IReadOnlyDictionary<AssetKey, IReadOnlyList<GameDataUnitPartFact>>> GetUnitPartFactsAsync(
		IReadOnlySet<AssetKey> unitAssetKeys,
		CancellationToken cancellationToken = default);

	/// <summary>
	/// 每个归档里**有多少个 Unit 条目**（含不可读的）。
	/// </summary>
	/// <remarks>
	/// 只给"已命中"的多数判定用：覆盖事实只能说出"哪些 unit 被换了"，说不出"一共有几个"，
	/// 于是"换了一个也算命中"把轻微污染也算成命中。分母必须从索引里取。
	/// 返回的字典只包含确实有 Unit 的归档；调用方对缺失的键按 0 处理。
	/// </remarks>
	ValueTask<IReadOnlyDictionary<string, int>> GetUnitCountsAsync(
		IReadOnlyCollection<string> archiveIds,
		CancellationToken cancellationToken = default)
		// 默认实现只服务于**没有索引的替身**（测试桩）：返回空表，调用方按"分母未知"处理。
		// 真正的实现必须查库——把这里做成默认值而不是抛异常，是因为这条数据只影响
		// 「已命中」的严格程度，不该让一个桩在无关用例里炸掉。
		=> ValueTask.FromResult<IReadOnlyDictionary<string, int>>(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));

	ValueTask<IReadOnlyList<GameDataStreamLayoutFact>> FindStreamLayoutsAsync(
		IReadOnlyList<GameDataStreamComponentFact> components,
		uint vertexStride,
		bool requireSkinned = false,
		CancellationToken cancellationToken = default);

	ValueTask<IReadOnlyList<GameDataStreamLayoutFact>> GetStreamLayoutsAsync(
		CancellationToken cancellationToken = default);

	ValueTask<GameDataIndexStatus> GetIndexStatusAsync(
		string gameDataDirectory,
		string archiveHashesJson,
		CancellationToken cancellationToken = default);

	ValueTask BuildOrRebuildAsync(
		string gameDataDirectory,
		string archiveHashesJson,
		IProgress<IndexBuildProgress>? progress = null,
		CancellationToken cancellationToken = default);

	ValueTask<IReadOnlyList<AssetArchiveMatch>> FindAssetArchivesAsync(
		IReadOnlySet<AssetKey> assetKeys,
		CancellationToken cancellationToken = default);

	ValueTask<IReadOnlyDictionary<string, int>> VoteArchivesAsync(
		IReadOnlySet<AssetKey> assetKeys,
		IndexFilterSettings filterSettings,
		CancellationToken cancellationToken = default);
}
