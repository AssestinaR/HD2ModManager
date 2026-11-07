using HD2ModCore.Infrastructure;

namespace HD2ModCli;

internal sealed record CliRepairIndexResult(
	bool Ok,
	string? Operation,
	bool BaseIndexCurrent,
	bool AdvancedEquipmentFactsCurrent,
	IReadOnlyList<string> Diagnostics);

/// <summary>
/// `repair index`：把「重建 GameData 索引」那三步（plan → validate → apply）收成一条命令。
/// <para>
/// 三步分开是有意义的——审阅之后才写盘。但"我就是要重建一次索引"这种场合，
/// 让用户（或替他跑命令的 agent）记住三个命令名与一个中间文件路径，只是摩擦。
/// 这条路仍然要求 <c>--confirm</c>，并且走的是同一套 `BuildAsync` / `ExecuteAsync`，
/// 所以待办事项与诊断完全一致，不会出现"快捷路径做了不一样的事"。
/// </para>
/// <para>
/// 重建的是**整整一套**：基础索引 + Unit 部位事实。两半分开做会留下零条的部位事实
/// （基础重建会先清空那两张表），详见 P5-5。
/// </para>
/// </summary>
internal static class CliRepairIndex
{
	public static async ValueTask<CliRepairIndexResult> RunAsync(
		CliWorkspace workspace,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		if (string.IsNullOrWhiteSpace(workspace.GameDataRoot))
			throw new CliUserException("GameDataNotConfigured", "请用 --game-data 指定游戏 data 目录，或用 game locate。");

		var request = new CliGameDataIndexRequest("RebuildBase", workspace.GameDataRoot);
		var build = await CliGameDataIndexPlanArtifacts.BuildAsync(workspace, request, cancellationToken).ConfigureAwait(false);
		var execution = await CliGameDataIndexExecutor
			.ApplyAsync(build.Document, workspace, cancellationToken)
			.ConfigureAwait(false);

		return new CliRepairIndexResult(
			execution.Success,
			execution.Operation.ToString(),
			execution.BaseIndexCurrent,
			execution.AdvancedEquipmentFactsCurrent,
			execution.Diagnostics.Select(diagnostic => $"{diagnostic.Severity} {diagnostic.Code}：{diagnostic.Message}").ToArray());
	}
}
