using System.Text.Json;
using System.Text.RegularExpressions;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCli;

internal sealed record CliLibrarySnapshotRepairResult(
	bool Ok,
	string? Error,
	int NodeCount,
	int RecoveredNodeCount,
	string LibraryPath);

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
		var empty = new LibrarySnapshot(
			1,
			DateTimeOffset.UtcNow,
			seeds,
			Array.Empty<Profile>());
		var inspection = await synchronizer.InspectAsync(empty, libraryRoot, null, cancellationToken).ConfigureAwait(false);
		var applied = synchronizer.ApplyInspection(empty, inspection);
		await store.SaveAsync(applied.Snapshot, cancellationToken).ConfigureAwait(false);

		return new CliLibrarySnapshotRepairResult(
			true, null, applied.Snapshot.Nodes.Count, seeds.Count, paths.LibraryPath);
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
