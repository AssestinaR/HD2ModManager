using HD2ModCore.Domain;

namespace HD2ModCore.Application;

// Purpose: Builds the optional, expensive equipment mesh facts used only by cross-armor transfer.
//
// 只有"全量重算"和"补齐缺失"两个入口：曾经还有一个 `BuildOrRefreshAsync`，它自己再跑一遍
// 基础索引来拿 stream 布局，零调用方，已经删掉。
public interface IAdvancedEquipmentIndexService
{
	ValueTask<bool> IsCurrentAsync(CancellationToken cancellationToken = default);

	ValueTask RebuildAllUnitPartFactsAsync(
		string gameDataDirectory,
		IProgress<IndexBuildProgress>? progress = null,
		CancellationToken cancellationToken = default);

	ValueTask BuildMissingUnitPartFactsAsync(
		string gameDataDirectory,
		IProgress<IndexBuildProgress>? progress = null,
		CancellationToken cancellationToken = default);
}
