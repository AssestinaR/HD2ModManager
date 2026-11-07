using System.Text.Json;
using System.Text.RegularExpressions;
using HD2ModCore.Application;
using HD2ModCore.Domain;
using Microsoft.Data.Sqlite;

namespace HD2ModCore.Infrastructure;

// 作用：以当前库快照为准，回收派生索引里属于已删除节点的残留。
// Purpose: Reclaims derived-index residue that belongs to nodes no longer present in the library.
//
// 删除一个 Mod 时，缓存失效是逐节点的、尽力而为的；任何一处失败（文件被占用、进程被结束、
// 走的是 CLI 删除路径）都会让该节点的派生数据永久留在磁盘上，而在此之前没有任何回收路径。
// 这些索引全部是可重建的缓存，所以"不在库里"就是充分的删除判据。
//
// Per-node invalidation is best effort. Any failure — a locked file, a terminated process, a CLI-side
// deletion that never touches the information center — leaves that node's derived data on disk forever,
// and nothing used to reclaim it. Every index touched here is a rebuildable cache, so "absent from the
// library" is a sufficient deletion criterion.
public sealed class ModIndexGarbageCollector : IModIndexGarbageCollector
{
	private static readonly TimeSpan TemporaryFileGrace = TimeSpan.FromMinutes(10);
	// JsonModInformationCache 的文件名：<kind>_<nodeId:N>_<generation>。
	// JsonModInformationCache file names: <kind>_<nodeId:N>_<generation>.
	private static readonly Regex PerNodeInformationFile = new(
		@"^(?<kind>[A-Za-z]+)_(?<node>[0-9a-fA-F]{32})_[0-9a-fA-F]{64}$",
		RegexOptions.Compiled | RegexOptions.CultureInvariant);

	private readonly StoragePaths _paths;

	public ModIndexGarbageCollector(StoragePaths paths)
		=> _paths = paths ?? throw new ArgumentNullException(nameof(paths));

	public async ValueTask<ModIndexGarbageCollectionResult> CollectAsync(
		IReadOnlyCollection<ModNodeId> liveNodeIds,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(liveNodeIds);
		var live = liveNodeIds.Select(nodeId => nodeId.Value.ToString("N")).ToHashSet(StringComparer.OrdinalIgnoreCase);
		var failures = new List<string>();

		var cacheFiles = CollectInformationCache(live, failures, cancellationToken);
		cacheFiles += CollectObsoleteCacheDirectories(failures, cancellationToken);
		var backups = CollectBackupDirectories(live, failures, cancellationToken);
		var temporaryFiles = CollectTemporaryFiles(failures);
		var factRows = CollectObsoleteFactsDatabase(failures);

		return new ModIndexGarbageCollectionResult(cacheFiles, factRows, backups, temporaryFiles, failures);
	}

	private int CollectInformationCache(HashSet<string> live, List<string> failures, CancellationToken cancellationToken)
	{
		var directory = Path.Combine(_paths.IndexDirectory, "mod-information");
		if (!Directory.Exists(directory)) return 0;

		// 先把文件名分好类，再决定删谁——"同一节点同一类型只留最新 generation"必须**先看到
		// 全部候选**才能判断谁是新的，边遍历边删会把新的当成旧的删掉。
		// 实测 2026-11-06：201 个节点留下 245 个 AssetInventory 文件，44 个是旧 generation 残留；
		// 旧逻辑只按"节点是否还在库中"删，永远碰不到它们。
		var perNode = new List<(string Path, string Kind, string Node, string Generation, DateTime WrittenUtc)>();
		var snapshotCandidates = new List<string>();
		foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
		{
			cancellationToken.ThrowIfCancellationRequested();
			var name = Path.GetFileNameWithoutExtension(path);
			var match = PerNodeInformationFile.Match(name);
			if (match.Success)
			{
				DateTime written;
				try { written = File.GetLastWriteTimeUtc(path); }
				catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { continue; }
				perNode.Add((path, match.Groups["kind"].Value, match.Groups["node"].Value, name, written));
				continue;
			}
			// 快照级 FileFacts 缓存：只有当它能被解析、并且 filesByNode 里的节点一个都不在库中时
			// 才删除。无法判定形状的文件一律保留——宁可留垃圾也不误删活数据。
			// Snapshot-level FileFacts: only removed when it parses AND none of its covered nodes
			// is live. Files with an unrecognized shape are always kept.
			var covered = TryReadCoveredNodes(path);
			if (covered is null || covered.Length == 0) continue;
			if (covered.All(node => !live.Contains(node))) snapshotCandidates.Add(path);
		}

		var doomed = new List<string>(snapshotCandidates);
		foreach (var group in perNode.GroupBy(entry => (entry.Kind, entry.Node)))
		{
			cancellationToken.ThrowIfCancellationRequested();
			// 节点已不在库中 ⇒ 这一组全删；否则只留最近写入的那一份（同一代重复时按文件名定序，
			// 保证结果可复现）。
			if (!live.Contains(group.Key.Node))
			{
				doomed.AddRange(group.Select(entry => entry.Path));
				continue;
			}
			var keep = group
				.OrderByDescending(entry => entry.WrittenUtc)
				.ThenByDescending(entry => entry.Generation, StringComparer.Ordinal)
				.First();
			doomed.AddRange(group.Where(entry => !string.Equals(entry.Path, keep.Path, StringComparison.Ordinal))
				.Select(entry => entry.Path));
		}

		var removed = 0;
		foreach (var path in doomed)
		{
			cancellationToken.ThrowIfCancellationRequested();
			try
			{
				TryClearReadOnly(path);
				File.Delete(path);
				removed++;
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
			{
				failures.Add($"{Path.GetFileNameWithoutExtension(path)}: {exception.Message}");
			}
		}
		return removed;
	}

	/// <summary>
	/// 删掉已经**没有读者**的派生缓存目录。
	/// <para>
	/// <c>mod-data-index/</c> 曾按节点存"哪些资产属于哪个 mod"，与 <c>mod-facts.sqlite</c>
	/// 逐条重复，唯一读者是详情页一行字；2026-11-06 改为直接查 SQL 索引后，这份目录不再有任何
	/// 读写方。留着它只会让人以为"这里还有东西要维护"，所以在这里一次性清掉。
	/// </para>
	/// <para>
	/// 它整体可再生（旧版本重新跑一次就会重建），所以整目录删除是安全的；删不掉只记账，不阻断。
	/// </para>
	/// </summary>
	private int CollectObsoleteCacheDirectories(List<string> failures, CancellationToken cancellationToken)
	{
		var directory = Path.Combine(_paths.IndexDirectory, "mod-data-index");
		if (!Directory.Exists(directory)) return 0;
		var removed = 0;
		try
		{
			foreach (var path in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories))
			{
				cancellationToken.ThrowIfCancellationRequested();
				TryClearReadOnly(path);
			}
			removed = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Count();
			Directory.Delete(directory, recursive: true);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
		{
			failures.Add($"mod-data-index: {exception.Message}");
			return 0;
		}
		return removed;
	}

	private static string[]? TryReadCoveredNodes(string path)
	{
		try
		{
			using var stream = File.OpenRead(path);
			using var document = JsonDocument.Parse(stream);
			var root = document.RootElement;
			var facts = root.ValueKind == JsonValueKind.Object && TryGetPropertyIgnoreCase(root, "facts", out var envelopeFacts)
				? envelopeFacts
				: root;
			if (facts.ValueKind != JsonValueKind.Object
				|| !TryGetPropertyIgnoreCase(facts, "filesByNode", out var filesByNode)
				|| filesByNode.ValueKind != JsonValueKind.Object)
				return null;
			return filesByNode.EnumerateObject().Select(property => property.Name).ToArray();
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or JsonException)
		{
			return null;
		}
	}

	private int CollectBackupDirectories(HashSet<string> live, List<string> failures, CancellationToken cancellationToken)
	{
		var directory = Path.Combine(_paths.DataDirectory, "mod-replacement-backups");
		if (!Directory.Exists(directory)) return 0;

		var removed = 0;
		foreach (var path in Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly))
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (!Guid.TryParseExact(Path.GetFileName(path), "N", out var id)) continue;
			if (live.Contains(id.ToString("N"))) continue;
			try
			{
				ClearAttributesRecursive(path);
				Directory.Delete(path, recursive: true);
				removed++;
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
			{
				failures.Add($"{Path.GetFileName(path)}: {exception.Message}");
			}
		}
		return removed;
	}

	private int CollectTemporaryFiles(List<string> failures)
	{
		var removed = 0;
		var cutoff = DateTime.UtcNow - TemporaryFileGrace;
		foreach (var directory in new[]
		{
			_paths.IndexDirectory,
			Path.Combine(_paths.IndexDirectory, "mod-information"),
		})
		{
			if (!Directory.Exists(directory)) continue;
			foreach (var path in Directory.EnumerateFiles(directory, "*.tmp"))
			{
				try
				{
					// 只清掉明显已经没人管的临时文件，避免和正在写入的调用者抢文件。
					// Only reclaim temporaries that are clearly abandoned.
					if (File.GetLastWriteTimeUtc(path) > cutoff) continue;
					TryClearReadOnly(path);
					File.Delete(path);
					removed++;
				}
				catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
				{
					failures.Add($"{Path.GetFileName(path)}: {exception.Message}");
				}
			}
		}
		return removed;
	}

	/// <summary>
	/// 回收已废弃的 <c>mod-facts.sqlite</c>。
	/// <para>
	/// 这些事实现在只活在会话内存里（<see cref="InMemoryModFactsStore"/>），文件没人读了。
	/// 以前这里只会修剪它的行、把文件永远留着（21.7 MB）；既然不读了，整个删掉才叫回收。
	/// </para>
	/// </summary>
	private int CollectObsoleteFactsDatabase(List<string> failures)
	{
		var databasePath = _paths.ModFactsDbPath;
		if (!File.Exists(databasePath)) return 0;
		try
		{
			// WAL 模式会留下两个同名旁路文件，一起清掉才算真的回收。
			foreach (var suffix in new[] { "", "-wal", "-shm" })
			{
				var path = databasePath + suffix;
				if (File.Exists(path)) File.Delete(path);
			}
			return 1;
		}
		catch (Exception exception) when (exception is IOException
			or UnauthorizedAccessException
			or System.Security.SecurityException)
		{
			failures.Add($"mod-facts.sqlite: {exception.Message}");
			return 0;
		}
	}

	private static void ClearAttributesRecursive(string directory)
	{
		foreach (var path in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories).Prepend(directory))
			TryClearReadOnly(path);
	}

	private static void TryClearReadOnly(string path)
	{
		try
		{
			var attributes = File.GetAttributes(path);
			var cleared = attributes & ~(FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System);
			if (cleared != attributes) File.SetAttributes(path, cleared);
		}
		catch (IOException) { }
		catch (UnauthorizedAccessException) { }
	}

	private static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
	{
		foreach (var property in element.EnumerateObject())
		{
			if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
			{
				value = property.Value;
				return true;
			}
		}
		value = default;
		return false;
	}
}
