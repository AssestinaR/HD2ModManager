using System.Text.Json;
using System.Text.RegularExpressions;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCli;

internal sealed record CliLibrarySnapshotRepairResult(
	bool Ok,
	string? Error,
	/// <summary>这次有没有真的写盘。<c>false</c> = 预演。</summary>
	bool Written,
	/// <summary>重建后的节点数。</summary>
	int NodeCount,
	/// <summary>从旧 <c>library.json</c> 文本里救回来的 id 条数。</summary>
	int RecoveredNodeCount,
	/// <summary>旧快照（能读出多少个节点）。<c>0</c> 表示读不出来或文件不在。</summary>
	int SnapshotNodeCount,
	/// <summary>磁盘上看起来是 Mod 的文件夹数。</summary>
	int DiskModCount,
	/// <summary>重建后**会换掉 id** 的 Mod 条数。为 0 才说明"这次重建不会打断任何引用"。</summary>
	int WouldReassignIds,
	/// <summary>会被换 id 的相对路径（前 20 条，够人核对）。</summary>
	IReadOnlyList<string> Reassignments,
	string LibraryPath,
	string NextStep);

/// <summary>
/// 从磁盘上的 Mod 文件夹重建 <c>library.json</c>。
/// <para>
/// <b>它一定不能在"库能正常加载"的时候被当成唯一手段</b>——库坏了才需要它，所以它从头到尾
/// 不读 <c>library.json</c>（那条路会先抛异常）。扫文件夹、用各 Mod 自己的
/// <c>.hd2modmanager-mod.json</c> 恢复名字/备注/持久 id，剩下的（patch 组、指纹）本来就该由扫描算。
/// </para>
/// <para>
/// <b>能救的 id 一定要救。</b>配置条目与护甲映射都是按节点 id 引用的。给一个已有 Mod 换新 id
/// 等于把那些引用悄悄打断——那不叫修复。所以就算 JSON 已经坏了，也按文本把
/// <c>id</c> / <c>relativePath</c> 配对捞出来当种子；捞不到的才分配新 id。
/// </para>
/// <para>
/// <b>默认预演，<c>--confirm</c> 才写。</b>这条命令是"库已经坏了"时的最后手段，
/// 而它本身会重写节点 id。以前它默认直接写盘，于是"想先看看会变成什么样"这件事本身就有代价。
/// 现在预演与写入走同一份计算，预演只少最后那句 <c>SaveAsync</c>。
/// </para>
/// <para>
/// <b>不碰 profiles.json。</b>配置是用户输入，存在另一个文件里；这里只重写节点那一半。
/// </para>
/// </summary>
internal static class CliLibrarySnapshotRepair
{
	private static readonly Regex NodePair = new(
		"\"id\"\\s*:\\s*\"(?<id>[0-9a-fA-F-]{36})\"\\s*,\\s*\"relativePath\"\\s*:\\s*\"(?<path>(?:[^\"\\\\]|\\\\.)*)\"",
		RegexOptions.Compiled | RegexOptions.CultureInvariant);

	public static async ValueTask<CliLibrarySnapshotRepairResult> RunAsync(
		CliWorkspace workspace,
		bool writeBack,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		var libraryRoot = workspace.LibraryRoot
			?? throw new CliUserException("LibraryNotConfigured", "请用 --library 指定 Mod 库。");
		var managerRoot = workspace.ManagerRoot
			?? throw new CliUserException("ManagerRootRequired", "请用 --manager-root 指定管理器目录。");

		var paths = new StoragePaths(managerRoot, libraryRoot);
		var store = CoreServices.CreateModLibraryStore(paths);
		var synchronizer = new ModLibrarySynchronizer(new PatchFileNameParser());

		var seeds = RecoverNodeSeeds(paths.LibraryPath);
		// 旧快照的节点数：只为了在回报里说清"从几个变成几个"。读不出来就是 0。
		var snapshotNodeCount = TryCountSnapshotNodes(paths.LibraryPath);

		var empty = new LibrarySnapshot(
			1,
			DateTimeOffset.UtcNow,
			seeds,
			Array.Empty<Profile>());
		var inspection = await synchronizer.InspectAsync(empty, libraryRoot, null, cancellationToken).ConfigureAwait(false);
		var applied = synchronizer.ApplyInspection(empty, inspection);

		// "会换 id"的判据：同一个 relativePath 在种子里有 id，但重建后给的 id 不同。
		var seedIdByPath = seeds.Values
			.GroupBy(node => Normalize(node.RelativePath), StringComparer.OrdinalIgnoreCase)
			.ToDictionary(group => group.Key, group => group.First().Id, StringComparer.OrdinalIgnoreCase);
		var reassignments = new List<string>();
		foreach (var node in applied.Snapshot.Nodes.Values.OrderBy(node => node.RelativePath, StringComparer.OrdinalIgnoreCase))
		{
			if (!seedIdByPath.TryGetValue(Normalize(node.RelativePath), out var previous)) continue;
			if (previous == node.Id) continue;
			if (reassignments.Count < 20) reassignments.Add($"{node.RelativePath}（{previous.Value:N} → {node.Id.Value:N}）");
		}

		var wouldReassign = applied.Snapshot.Nodes.Values.Count(node =>
			seedIdByPath.TryGetValue(Normalize(node.RelativePath), out var previous) && previous != node.Id);

		if (writeBack)
			await store.SaveAsync(applied.Snapshot, cancellationToken).ConfigureAwait(false);

		return new CliLibrarySnapshotRepairResult(
			true,
			null,
			writeBack,
			applied.Snapshot.Nodes.Count,
			seeds.Count,
			snapshotNodeCount,
			CliLibraryStateDiagnostics.CountModFolders(libraryRoot),
			wouldReassign,
			reassignments,
			paths.LibraryPath,
			writeBack
				? "快照已重建。若刚才的 wouldReassignIds 不为 0，请检查配置条目与护甲映射是否仍指得对。"
				: "预演，**没有写盘**。核对 wouldReassignIds 之后再带 --confirm 重跑一次。");
	}

	private static string Normalize(string relativePath) => relativePath.Replace('\\', '/').Trim().TrimEnd('/');

	/// <summary>旧快照能读出几个节点；文件不在或读不出来返回 0。</summary>
	private static int TryCountSnapshotNodes(string libraryPath)
	{
		try
		{
			if (!File.Exists(libraryPath)) return 0;
			using var stream = File.OpenRead(libraryPath);
			using var document = JsonDocument.Parse(stream);
			return document.RootElement.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Object
				? nodes.EnumerateObject().Count()
				: 0;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
		{
			return 0;
		}
	}

	/// <summary>从（可能已损坏的）library.json 文本里捞出 <c>id</c> 与 <c>relativePath</c> 的配对。</summary>
	private static Dictionary<ModNodeId, ModNode> RecoverNodeSeeds(string libraryPath)
	{
		var seeds = new Dictionary<ModNodeId, ModNode>();
		if (!File.Exists(libraryPath)) return seeds;
		string text;
		try
		{
			text = File.ReadAllText(libraryPath);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return seeds;
		}

		foreach (Match match in NodePair.Matches(text))
		{
			if (!Guid.TryParse(match.Groups["id"].Value, out var id)) continue;
			string relativePath;
			try
			{
				relativePath = JsonSerializer.Deserialize<string>("\"" + match.Groups["path"].Value + "\"") ?? string.Empty;
			}
			catch (JsonException)
			{
				continue;
			}
			if (relativePath.Length == 0) continue;
			var nodeId = new ModNodeId(id);
			seeds[nodeId] = new ModNode(
				nodeId,
				relativePath,
				new ModNodeMetadata(Path.GetFileName(relativePath), null, DateTimeOffset.UtcNow, null),
				Array.Empty<PatchGroupKey>(),
				Array.Empty<ModNodeId>(),
				"recovered");
		}
		return seeds;
	}
}
