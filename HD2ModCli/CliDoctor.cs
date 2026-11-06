using HD2ModCore.Infrastructure;

namespace HD2ModCli;

/// <summary>一条诊断结论。带上"看到什么 / 期望什么 / 怎么修"，让 agent 能自己动手。</summary>
internal sealed record CliDoctorIssue(
	string Code,
	string Severity,
	string Observed,
	string Expected,
	string? Fix);

internal sealed record CliDoctorReport(
	string? ManagerRoot,
	string? LibraryRoot,
	string? GameDataRoot,
	string? RegistryRoot,
	bool LibraryLoaded,
	int NodeCount,
	int MissingDirectoryCount,
	int ModWithoutPayloadCount,
	string GameDataIndexState,
	bool GameDataIndexReady,
	long ReclaimableBytes,
	int ReclaimableItems,
	IReadOnlyList<CliDoctorIssue> Issues)
{
	public bool IsHealthy => Issues.All(issue => issue.Severity != "error");
}

/// <summary>
/// 一条命令把所有诊断合起来。
/// <para>
/// <b>它只陈述事实并给出下一步动作，不替用户修任何东西。</b>用户（或替他跑命令的 agent）
/// 需要的是一份能照着做的清单——"库在哪、索引新不新、哪些 Mod 的文件夹不见了、
/// 有什么可以回收"——而不是一堆要自己拼的旁证。
/// </para>
/// </summary>
internal static class CliDoctor
{
	public static async ValueTask<CliDoctorReport> BuildAsync(
		CliWorkspace workspace,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		var issues = new List<CliDoctorIssue>();

		if (string.IsNullOrWhiteSpace(workspace.ManagerRoot))
			issues.Add(new CliDoctorIssue("ManagerRootNotConfigured", "warning",
				"没有可用的管理器目录，缓存、设置与索引都读不到。",
				"需要一个含 settings 的管理器运行目录。",
				"传 --manager-root <目录>（hd2mod 通常就在那个目录里）。"));
		if (string.IsNullOrWhiteSpace(workspace.LibraryRoot))
			issues.Add(new CliDoctorIssue("LibraryNotConfigured", "error",
				"没有可用的 Mod 库路径。",
				"需要一个含 library.json 的库目录。",
				"传 --library <目录>，或在管理器里设置一次 Mod 库。"));
		if (string.IsNullOrWhiteSpace(workspace.GameDataRoot))
			issues.Add(new CliDoctorIssue("GameDataNotConfigured", "error",
				"没有可用的游戏数据路径。",
				"需要 Helldivers 2 的 data 目录。",
				"传 --game-data <目录>，或用 game locate 让它自己找。"));

		var nodeCount = 0;
		var missingDirectory = 0;
		var withoutPayload = 0;
		var libraryLoaded = false;
		if (!string.IsNullOrWhiteSpace(workspace.LibraryRoot))
		{
			var state = await workspace.LoadLibraryAsync(cancellationToken).ConfigureAwait(false);
			libraryLoaded = true;
			nodeCount = state.Snapshot.Nodes.Count;
			foreach (var node in state.Snapshot.Nodes.Values)
			{
				var directory = Path.Combine(workspace.LibraryRoot, node.RelativePath);
				if (!Directory.Exists(directory)) { missingDirectory++; continue; }
				// 逻辑容器本来就没有载荷；除此之外"有目录但没有载荷"才是异常。
				if (!node.Metadata.IsLogicalContainer
					&& ModNodePayloadLayout.TryGetExistingVpkPatchDirectory(directory) is null
					&& !Directory.EnumerateFiles(directory, "*.patch_*", SearchOption.TopDirectoryOnly).Any())
					withoutPayload++;
			}
			if (missingDirectory > 0)
				issues.Add(new CliDoctorIssue("ModDirectoryMissing", "error",
					$"{missingDirectory} 个 Mod 的文件夹不在库里。",
					"每个库节点都应该有一个存在的文件夹。",
					"跑 `library sync inspect` 看是哪些；确认后 `library sync apply` 清掉悬空节点。"));
			if (withoutPayload > 0)
				issues.Add(new CliDoctorIssue("ModWithoutPayload", "warning",
					$"{withoutPayload} 个 Mod 既没有基底目录也没有根级 patch。",
					"非逻辑容器的 Mod 至少要有一份 patch 才有意义。",
					"跑 `mod show <GUID>` 复核；确认无用后手动删除该文件夹再 sync。"));
		}

		var indexState = "unknown";
		var indexReady = false;
		if (!string.IsNullOrWhiteSpace(workspace.GameDataRoot))
		{
			var inspection = await CliGameDataInspection.BuildIndexAsync(workspace, cancellationToken).ConfigureAwait(false);
			indexState = inspection.IndexState;
			indexReady = inspection.IsReady;
			if (!inspection.TargetIsValid)
				issues.Add(new CliDoctorIssue("GameDataTargetInvalid", "error",
					$"游戏数据目录不可用：{inspection.GameDataDirectory ?? "(未确定)"}",
					"指向 Helldivers 2 的 data 目录。",
					"用 game locate 重新定位，或传 --game-data <目录>。"));
			else if (!inspection.IsReady)
				issues.Add(new CliDoctorIssue("GameDataIndexNotReady", "warning",
					$"游戏数据索引状态：{inspection.IndexState}。",
					"索引应当是 current，这样部署与配方才有依据。",
					"跑 `gamedata index plan plan` 看要不要重建；需要时 `gamedata index plan apply --confirm`。"));
		}

		var (reclaimableBytes, reclaimableItems) = ScanReclaimable(workspace.LibraryRoot);
		if (reclaimableItems > 0)
			issues.Add(new CliDoctorIssue("ReclaimableJunk", "info",
				$"库里有 {reclaimableItems} 项可回收的备份目录，共 {reclaimableBytes / (1024.0 * 1024.0):F1} MB。",
				"这些是换甲/重转留下的中间产物，不影响功能。",
				"确认当前外观正常后可以手动删除 `*.bak-*` 与 `*.backup-*` 目录。"));

		return new CliDoctorReport(
			workspace.ManagerRoot,
			workspace.LibraryRoot,
			workspace.GameDataRoot,
			// 护甲注册表位置由核心统一解析（不是命令行参数），只报它解析成什么。
			HD2ModCore.Infrastructure.RuntimeArmorRegistryService.ResolveRegistryRoot(),
			libraryLoaded,
			nodeCount,
			missingDirectory,
			withoutPayload,
			indexState,
			indexReady,
			reclaimableBytes,
			reclaimableItems,
			issues);
	}

	/// <summary>
	/// 库里的中间产物：换甲/重转留下的 <c>*.bak-*</c>、<c>*.backup-*</c>。
	/// 它们通常在**每个 Mod 文件夹里面**（如 <c>&lt;mod&gt;/vpk2patch.bak-2026…</c>），
	/// 所以两层都要看。
	/// </summary>
	private static (long Bytes, int Items) ScanReclaimable(string? libraryRoot)
	{
		if (string.IsNullOrWhiteSpace(libraryRoot) || !Directory.Exists(libraryRoot)) return (0, 0);
		long bytes = 0;
		var items = 0;

		static bool IsJunk(string name)
			=> name.Contains(".bak-", StringComparison.OrdinalIgnoreCase)
			|| name.Contains(".backup-", StringComparison.OrdinalIgnoreCase);

		long SizeOf(string directory)
		{
			try
			{
				return new DirectoryInfo(directory)
					.EnumerateFiles("*", SearchOption.AllDirectories)
					.Sum(file => file.Length);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				// 读不到就当 0：这是"可以回收多少"的估计，不是账目。
				return 0;
			}
		}

		foreach (var top in Directory.EnumerateDirectories(libraryRoot))
		{
			if (IsJunk(Path.GetFileName(top)))
			{
				items++;
				bytes += SizeOf(top);
				continue;
			}
			foreach (var inner in Directory.EnumerateDirectories(top))
			{
				if (!IsJunk(Path.GetFileName(inner))) continue;
				items++;
				bytes += SizeOf(inner);
			}
		}
		return (bytes, items);
	}
}
