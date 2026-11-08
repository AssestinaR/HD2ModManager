using HD2ModCore.Infrastructure;

namespace HD2ModCli;

/// <summary>
/// 把「选择器在快照里找不到」这句废话，变成一句能直接照做的诊断。
/// <para>
/// <b>为什么需要它。</b>CLI 只读 <c>library.json</c>（库快照），不扫目录；而管理器自己会扫描，
/// 所以同一个 Mod"管理器里明明在、CLI 说找不到"是完全可能的。实测代价：一位使用者被
/// 「未找到 Mod」引去反复核对 guid 与名字，最后跑了 <c>repair library-snapshot</c>，
/// 而那条路在快照缺失时会**给每个 Mod 重新分配 id**——整条事故链从这里开始。
/// </para>
/// <para>
/// 「缺的是索引」和「缺的是 Mod」是两种完全不同的故障，报错必须分开。
/// </para>
/// </summary>
internal static class CliLibraryStateDiagnostics
{
	/// <summary>
	/// 把 <see cref="CliProjections.ResolveMod"/> 抛出的"找不到"补上磁盘对照。
	/// 只处理 `ModNotFound` / `ModSelectorInvalid`；其它异常原样抛出。
	/// </summary>
	internal static CliUserException Explain(CliWorkspace workspace, CliLibraryState state, string selector, CliUserException original)
	{
		if (original.Code is not ("ModNotFound" or "ModSelectorInvalid")) return original;
		var libraryRoot = workspace.LibraryRoot;
		if (string.IsNullOrWhiteSpace(libraryRoot) || !Directory.Exists(libraryRoot)) return original;

		int onDisk;
		try
		{
			onDisk = CountModFolders(libraryRoot);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return original;
		}

		var snapshot = state.Snapshot.Nodes.Count;
		var repairCommand = $"hd2mod repair library-snapshot --library \"{libraryRoot}\" --manager-root \"{workspace.ManagerRoot}\"";
		// 磁盘上的 Mod 比快照多 ⇒ 快照过期。这是最常见的一种，单列一个错误码：
		// 它和"你的 selector 写错了"需要的动作完全不同。
		if (onDisk > snapshot)
		{
			return new CliUserException("LibrarySnapshotStale",
				$"Mod 库快照已过期：快照里 {snapshot} 个 Mod，磁盘上 {onDisk} 个（{libraryRoot}）。"
				+ $"selector 给的是 '{selector}'。CLI 只读快照、不扫目录，所以新导入的 Mod 查不到。"
				+ $"先跑 {repairCommand}（默认**不写盘**）看 wouldReassignIds，"
				+ "再加 --confirm 真的重写；或者启动一次管理器让它自己扫描。");
		}

		return new CliUserException(original.Code,
			$"{original.Message} 磁盘上 {libraryRoot} 下有 {onDisk} 个 Mod 文件夹，与快照的 {snapshot} 个"
			+ $"{(onDisk == snapshot ? "一致" : "不一致")}。"
			+ "如果那个 Mod 确实在库里，先跑 repair library-snapshot 的 dry-run（不带 --confirm）核对。");
	}

	/// <summary>数一数库根下有多少个"看起来是 Mod"的文件夹：以各 Mod 自己的设置文件为判据。</summary>
	internal static int CountModFolders(string libraryRoot)
	{
		var count = 0;
		foreach (var directory in Directory.EnumerateDirectories(libraryRoot))
		{
			var name = Path.GetFileName(directory);
			if (name.StartsWith('.')) continue;
			if (File.Exists(Path.Combine(directory, ModSettingsFile.FileName))) count++;
		}
		return count;
	}
}
