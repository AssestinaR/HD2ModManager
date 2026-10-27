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

	private static readonly string[] FactTables =
	[
		"asset_references",
		"mod_assets",
		"patch_groups",
		"mod_fact_snapshots",
		"advanced_mod_analysis_snapshots",
	];

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
		var indexNodes = CollectDataIndexNodes(live, failures, cancellationToken);
		var backups = CollectBackupDirectories(live, failures, cancellationToken);
		var temporaryFiles = CollectTemporaryFiles(failures);
		var factRows = await CollectFactRowsAsync(live, failures, cancellationToken).ConfigureAwait(false);

		return new ModIndexGarbageCollectionResult(cacheFiles, indexNodes, factRows, backups, temporaryFiles, failures);
	}

	private int CollectInformationCache(HashSet<string> live, List<string> failures, CancellationToken cancellationToken)
	{
		var directory = Path.Combine(_paths.IndexDirectory, "mod-information");
		if (!Directory.Exists(directory)) return 0;

		var removed = 0;
		foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
		{
			cancellationToken.ThrowIfCancellationRequested();
			var name = Path.GetFileNameWithoutExtension(path);
			var match = PerNodeInformationFile.Match(name);
			bool dead;
			if (match.Success)
			{
				dead = !live.Contains(match.Groups["node"].Value);
			}
			else
			{
				// 快照级 FileFacts 缓存：只有当它能被解析、并且 filesByNode 里的节点一个都不在库中时
				// 才删除。无法判定形状的文件一律保留——宁可留垃圾也不误删活数据。
				// Snapshot-level FileFacts: only removed when it parses AND none of its covered nodes
				// is live. Files with an unrecognized shape are always kept.
				var covered = TryReadCoveredNodes(path);
				if (covered is null || covered.Length == 0) continue;
				dead = covered.All(node => !live.Contains(node));
			}

			if (!dead) continue;
			try
			{
				TryClearReadOnly(path);
				File.Delete(path);
				removed++;
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
			{
				failures.Add($"{name}: {exception.Message}");
			}
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

	private int CollectDataIndexNodes(HashSet<string> live, List<string> failures, CancellationToken cancellationToken)
	{
		var removed = 0;
		var directory = Path.Combine(_paths.IndexDirectory, "mod-data-index");
		if (Directory.Exists(directory))
		{
			foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var id)) continue;
				if (live.Contains(id.ToString("N"))) continue;
				try
				{
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

		// 按节点分文件的布局一旦就位，旧版单文件就是纯死重。
		// Once the per-node layout exists the legacy single file is pure dead weight.
		var legacy = Path.Combine(_paths.IndexDirectory, "mod-data-index.json");
		if (Directory.Exists(directory) && Directory.EnumerateFiles(directory, "*.json").Any() && File.Exists(legacy))
		{
			try
			{
				TryClearReadOnly(legacy);
				File.Delete(legacy);
				removed++;
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
			{
				failures.Add($"{Path.GetFileName(legacy)}: {exception.Message}");
			}
		}
		return removed;
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
			Path.Combine(_paths.IndexDirectory, "mod-data-index"),
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

	private async ValueTask<int> CollectFactRowsAsync(HashSet<string> live, List<string> failures, CancellationToken cancellationToken)
	{
		var databasePath = _paths.ModFactsDbPath;
		if (!File.Exists(databasePath)) return 0;

		var removed = 0;
		try
		{
			await using var connection = new SqliteConnection($"Data Source={databasePath}");
			await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
			var existingTables = await ReadTableNamesAsync(connection, cancellationToken).ConfigureAwait(false);
			await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
			foreach (var table in FactTables)
			{
				// 这个库是惰性建表的：表不存在是正常状态，不是失败。
				// Tables are created lazily, so a missing table is a normal state rather than a failure.
				if (!existingTables.Contains(table)) continue;

				var dead = new List<string>();
				await using (var select = connection.CreateCommand())
				{
					select.Transaction = (SqliteTransaction)transaction;
					select.CommandText = $"SELECT DISTINCT node_id FROM {table}";
					await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
					while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
					{
						var node = reader.GetString(0);
						if (!live.Contains(node)) dead.Add(node);
					}
				}

				foreach (var node in dead)
				{
					await using var delete = connection.CreateCommand();
					delete.Transaction = (SqliteTransaction)transaction;
					delete.CommandText = $"DELETE FROM {table} WHERE node_id = $node";
					delete.Parameters.AddWithValue("$node", node);
					removed += await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
				}
			}
			await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
		{
			failures.Add($"mod-facts.sqlite: {exception.Message}");
		}
		return removed;
	}

	private static async ValueTask<HashSet<string>> ReadTableNamesAsync(SqliteConnection connection, CancellationToken cancellationToken)
	{
		var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
		await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) names.Add(reader.GetString(0));
		return names;
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
