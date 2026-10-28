using HD2ModCore.Domain;

namespace HD2ModCore.Application;

// 作用：高级 Profile 部署服务：构建真实索引、生成计划、执行并验证。
// Purpose: High-level profile apply service that builds the real index, plans, executes and verifies.
public interface IProfileApplyService
{
	ValueTask<ApplyResult> ApplyAsync(
		Profile profile,
		LibrarySnapshot snapshot,
		string modsRootDirectory,
		string gameDataDirectory,
		CancellationToken cancellationToken = default);
}