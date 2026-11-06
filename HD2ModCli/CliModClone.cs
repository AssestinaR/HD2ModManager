using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCli;

internal sealed record CliModCloneResult(
	bool Ok,
	string? Error,
	string? CloneDirectory,
	string? CloneName,
	string? IdNamespace,
	IReadOnlyList<string> Notes);

/// <summary>
/// `mod clone` 的命令行外壳。复制逻辑本身在 <see cref="ModCloneService"/>：
/// 详情页的「复制为变体」走的是同一份实现，两处各写一遍迟早会不一样。
/// </summary>
internal static class CliModClone
{
	public static async ValueTask<CliModCloneResult> RunAsync(
		CliWorkspace workspace,
		CliLibraryState state,
		string selector,
		string variantName,
		Action<string>? onStage,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		ArgumentNullException.ThrowIfNull(state);
		ArgumentException.ThrowIfNullOrWhiteSpace(variantName);

		var libraryRoot = workspace.LibraryRoot
			?? throw new CliUserException("LibraryNotConfigured", "请用 --library 指定 Mod 库。");
		var managerRoot = workspace.ManagerRoot
			?? throw new CliUserException("ManagerRootRequired", "请用 --manager-root 指定管理器目录。");

		var node = CliProjections.ResolveMod(state.Snapshot.Nodes, selector);
		var result = await ModCloneService.CloneAsync(
			libraryRoot,
			managerRoot,
			workspace.GameDataRoot,
			Path.Combine(libraryRoot, node.RelativePath),
			node.Metadata.Name,
			node.RelativePath,
			variantName,
			onStage,
			cancellationToken).ConfigureAwait(false);

		return new CliModCloneResult(
			result.Ok, result.Error, result.CloneDirectory, result.CloneName, result.IdNamespace, result.Notes);
	}
}
