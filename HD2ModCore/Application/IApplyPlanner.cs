using HD2ModCore.Domain;

namespace HD2ModCore.Application;

// 作用：根据 Profile 与真实 patch 索引生成 ApplyPlan，用于预览与执行。
// Purpose: Builds an ApplyPlan from a Profile and real patch index for preview/execution.
public interface IApplyPlanner
{
	ValueTask<ApplyPlan> BuildPlanAsync(
		Profile profile,
		LibrarySnapshot snapshot,
		PatchFileIndex patchIndex,
		string gameDataDirectory,
		CancellationToken cancellationToken = default);

	ValueTask<ApplyPlan> BuildPlanAsync(
		Profile profile,
		LibrarySnapshot snapshot,
		PatchFileIndex patchIndex,
		string gameDataDirectory,
		ApplyPlanAugmentation augmentation,
		CancellationToken cancellationToken = default);
}
