using HD2ModCore.Infrastructure;

namespace HD2ModCli;

/// <summary>
/// `mod rebuild`：用 Mod 自己的来源 VPK + 报告里记着的参数重出一遍产物。
/// <para>
/// 逻辑一行都不新写——`VpkRebuildService` 已经在 Core 里，它知道怎么重放
/// `resolutionSet` 与 `conversion`（这两个漏一个产物就完全不同）。
/// 这里只负责把命令行的三样东西翻成它的入参：库节点、目录、宿主上下文。
/// </para>
/// </summary>
internal static class CliModRebuild
{
	internal static async ValueTask<VpkRebuildOutcome> RunAsync(
		CliWorkspace workspace,
		HD2ModCore.Domain.ModNode node,
		double? headScale,
		Action<string>? onStage,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		ArgumentNullException.ThrowIfNull(node);

		// 头部缩放是 Core 的全局几何参数（不进产物记录，见 VpkRebuildService.HeadScale）。
		// 命令行一次调用就是一个进程，在这里写一次即可；退出就没了，不会串到别的调用。
		VpkRebuildService.HeadScale = headScale;

		var libraryRoot = workspace.LibraryRoot
			?? throw new CliUserException("LibraryNotConfigured", "请用 --library 指定 Mod 库。");
		var managerRoot = workspace.ManagerRoot
			?? throw new CliUserException("ManagerRootRequired", "请用 --manager-root 指定管理器目录。");

		// 选择器**在调用方解析**：那里还握着 CliLibraryState 与库根目录，能在"找不到"时
		// 比对磁盘给出一句可行动的诊断（CliLibraryStateDiagnostics）。这里拿到的已经是节点。
		var directory = Path.Combine(libraryRoot, node.RelativePath);
		if (!Directory.Exists(directory))
			return VpkRebuildOutcome.Failed($"Mod 目录不存在：{directory}");

		return await VpkRebuildService.RunAsync(
			node.Id.Value.ToString("N"),
			directory,
			new VpkHostContext(workspace.GameDataRoot, new StoragePaths(managerRoot, libraryRoot)),
			// null = 用 wardrobe.json 里记着的那一套（这个 Mod 上次的配置）。
			targetArchiveIds: null,
			disabledSections: null,
			onStage,
			cancellationToken).ConfigureAwait(false);
	}
}
