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
		var nestedPayload = new List<string>();
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

				// 载荷层里出现子目录 = patch 不在顶层 = 下游一律看不到它们。
				//
				// 这是 2026-11-08 用户实测的形状：迁移把老库里"按包名建的子目录"原样搬进了
				// `sectional/`，于是部署索引、材质选项表、重构建**全都扫不到**，
				// 表现是"部署成功、游戏里什么都没变"，而文件明明在那里。
				foreach (var layer in new[]
				{
					ModNodePayloadLayout.TryGetExistingVpkPatchDirectory(directory),
					Directory.Exists(ModNodePayloadLayout.GetOverwriteDirectory(directory))
						? ModNodePayloadLayout.GetOverwriteDirectory(directory)
						: null,
				})
				{
					if (layer is null) continue;
					if (!Directory.EnumerateDirectories(layer).Any()) continue;
					nestedPayload.Add(Path.Combine(node.RelativePath, Path.GetFileName(layer)));
					break;
				}
			}
			if (nestedPayload.Count > 0)
				issues.Add(new CliDoctorIssue("ModPayloadNested", "error",
					$"{nestedPayload.Count} 个 Mod 的载荷层里有子目录（patch 必须平铺在顶层）："
					+ string.Join("、", nestedPayload.Take(5))
					+ (nestedPayload.Count > 5 ? $"…（共 {nestedPayload.Count} 个）" : string.Empty),
					"`sectional/` 与 `Overwrite/` 顶层只放 patch 文件与结构文件；"
					+ "部署索引、材质选项表、重构建全部只扫顶层。",
					"这些 patch 现在等于不存在。跑 `library family-merge plan` 看要不要归拢，"
					+ "再对涉及的 Mod 各跑一次 `mod rebuild`。"));
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

		// 与 `library cleanup` 用同一份扫描：医生说的"可回收"必须就是清理会删的。
		var reclaimable = CliLibraryCleanup.Scan(workspace.LibraryRoot ?? string.Empty);
		var reclaimableBytes = reclaimable.Sum(entry => entry.Bytes);
		var reclaimableItems = reclaimable.Count;
		if (reclaimableItems > 0)
			issues.Add(new CliDoctorIssue("ReclaimableJunk", "info",
				$"库里有 {reclaimableItems} 项可回收的备份目录，共 {reclaimableBytes / (1024.0 * 1024.0):F1} MB。",
				"这些是换甲/重转留下的中间产物，不影响功能。",
				"`library cleanup --backups --confirm` 会删掉这些目录；先不带 --confirm 跑一次可以看到明细。"));

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

}
