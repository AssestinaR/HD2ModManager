using System.Collections.Concurrent;
using System.Text.Json;
using HD2ModCore.Application;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

// 作用：维护由信息产品构建的进程内跨 Mod 资产/引用索引，不参与基础部署。
// Purpose: Maintains an in-process cross-Mod asset/reference index without participating in deployment.
//
// 持久化按节点分文件（data/indexes/mod-data-index/<nodeid>.json）。以前整库写在一个 JSON 里：
// 删一个 Mod 要重新序列化全部 ~30000 条条目（实测 8.9 MB / 60-85 ms），而且删除失败时会把
// 5-20 MB 的临时文件永久留在磁盘上。分文件之后，删除只删一个小文件，写入只影响一个节点。
//
// Persistence is per node. The single-file layout forced a full ~30000-entry rewrite (8.9 MB, 60-85 ms)
// for every deleted Mod and leaked the temporary file whenever the atomic move failed.
public sealed class ModDataIndex : IModDataIndex
{
	private const int SchemaVersion = 1;
	private const string NodeFileExtension = ".json";
	private readonly ConcurrentDictionary<ModNodeId, IReadOnlyList<ModDataIndexEntry>> _entries = new();
	private readonly ConcurrentDictionary<ModNodeId, IReadOnlyList<ModDataIndexEntry>> _providers = new();
	private readonly ConcurrentDictionary<ModNodeId, IReadOnlyList<ModDataIndexEntry>> _consumers = new();
	// 旧版单文件布局，仅用于一次性迁移。
	// Legacy single-file layout, only read once during migration.
	private readonly string? _legacyPersistencePath;
	private readonly string? _nodesDirectory;
	private readonly object _persistenceLock = new();
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
	private volatile ModDataIndexStatus _status = ModDataIndexStatus.Unavailable;

	public ModDataIndex()
	{
	}

	public ModDataIndex(StoragePaths paths)
	{
		ArgumentNullException.ThrowIfNull(paths);
		_legacyPersistencePath = Path.Combine(paths.IndexDirectory, "mod-data-index.json");
		_nodesDirectory = Path.Combine(paths.IndexDirectory, "mod-data-index");
		Load();
	}

	public void Update(ModContentFacts inventory)
	{
		_providers[inventory.NodeId] = inventory.PatchGroups.SelectMany(group => group.AssetKeys.Select(key => new ModDataIndexEntry(inventory.NodeId, inventory.RelativePath, group.Id.SourceArchiveHex, group.Id.SourcePatchIndex, new AssetKey(key.TypeId, key.FileId), "Provider"))).ToArray();
		if (_status == ModDataIndexStatus.Unavailable) _status = ModDataIndexStatus.Partial;
		RebuildNode(inventory.NodeId);
	}

	public void Update(ReferenceGraphFacts graph)
	{
		_consumers[graph.NodeId] = graph.Analyses.SelectMany(analysis => analysis.References.Select(reference => new ModDataIndexEntry(graph.NodeId, graph.RelativePath, Path.GetFileName(analysis.Input.PatchTocFilePath), 0, new AssetKey(reference.TargetAssetKey.TypeId, reference.TargetAssetKey.FileId), "Consumer"))).ToArray();
		if (_status == ModDataIndexStatus.Unavailable) _status = ModDataIndexStatus.Partial;
		RebuildNode(graph.NodeId);
	}

	public ValueTask<IReadOnlyList<ModDataIndexEntry>> FindProvidersAsync(AssetKey assetKey, CancellationToken cancellationToken = default)
		=> ValueTask.FromResult<IReadOnlyList<ModDataIndexEntry>>(_entries.Values.SelectMany(value => value).Where(entry => entry.Relation == "Provider" && entry.AssetKey == assetKey).ToArray());

	public ValueTask<IReadOnlyList<ModDataIndexEntry>> FindConsumersAsync(AssetKey assetKey, CancellationToken cancellationToken = default)
		=> ValueTask.FromResult<IReadOnlyList<ModDataIndexEntry>>(_entries.Values.SelectMany(value => value).Where(entry => entry.Relation == "Consumer" && entry.AssetKey == assetKey).ToArray());

	public ValueTask<ModDataIndexSummary> GetAssetRelationSummaryAsync(IReadOnlyCollection<AssetKey> assetKeys, ModNodeId? excludedNodeId = null, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(assetKeys);
		if (_status == ModDataIndexStatus.Unavailable) return ValueTask.FromResult(new ModDataIndexSummary(ModDataIndexStatus.Unavailable, 0, 0));
		var requested = assetKeys.ToHashSet();
		var counts = _entries.Values
			.SelectMany(value => value)
			.Where(entry => (!excludedNodeId.HasValue || entry.NodeId != excludedNodeId.Value) && requested.Contains(entry.AssetKey))
			.GroupBy(entry => entry.Relation)
			.ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
		return ValueTask.FromResult(new ModDataIndexSummary(
			_status,
			counts.GetValueOrDefault("Provider"),
			counts.GetValueOrDefault("Consumer")));
	}

	public ValueTask<ModDataIndexEntry?> ResolveFinalProviderAsync(AssetKey assetKey, Profile profile, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(profile);
		var order = profile.Entries
			.OrderBy(entry => entry.LoadOrder)
			.ThenBy(entry => entry.AddedUtc)
			.ThenBy(entry => entry.NodeId.Value)
			.Select((entry, index) => (entry.NodeId, Index: index))
			.ToDictionary(item => item.NodeId, item => item.Index);
		var result = _entries.Values
			.SelectMany(value => value)
			.Where(entry => entry.Relation == "Provider" && entry.AssetKey == assetKey && order.ContainsKey(entry.NodeId))
			.OrderBy(entry => order[entry.NodeId])
			.ThenBy(entry => entry.PatchIndex)
			.LastOrDefault();
		return ValueTask.FromResult(result);
	}

	public ValueTask RemoveNodeAsync(ModNodeId nodeId, CancellationToken cancellationToken = default)
	{
		var changed = _entries.TryRemove(nodeId, out _);
		changed |= _providers.TryRemove(nodeId, out _);
		changed |= _consumers.TryRemove(nodeId, out _);
		if (_entries.IsEmpty) _status = ModDataIndexStatus.Unavailable;
		if (changed) DeleteNodeFile(nodeId);
		return ValueTask.CompletedTask;
	}

	private void RebuildNode(ModNodeId nodeId)
	{
		var entries = _providers.TryGetValue(nodeId, out var providers) ? providers : Array.Empty<ModDataIndexEntry>();
		if (_consumers.TryGetValue(nodeId, out var consumers)) entries = entries.Concat(consumers).ToArray();
		_entries[nodeId] = entries;
		PersistNode(nodeId, entries);
	}

	private bool LoadNodeFiles()
	{
		if (string.IsNullOrWhiteSpace(_nodesDirectory) || !Directory.Exists(_nodesDirectory)) return false;
		var loaded = false;
		foreach (var path in Directory.EnumerateFiles(_nodesDirectory, "*" + NodeFileExtension))
		{
			if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var id)) continue;
			var entries = TryReadEntries(path);
			if (entries is null) continue;
			Apply(entries);
			loaded = true;
		}
		return loaded;
	}

	private void Load()
	{
		var loaded = LoadNodeFiles();
		// 旧版单文件存在时补齐它里面尚未按节点落盘的部分，然后删掉它。迁移是幂等的：
		// 中途失败只会留下"部分节点已迁移 + 旧文件还在"，下次启动继续补齐。
		// The legacy file is merged for any node not yet persisted per node, then deleted. The migration
		// is idempotent, so an interrupted run simply continues on the next start.
		if (!string.IsNullOrWhiteSpace(_legacyPersistencePath) && File.Exists(_legacyPersistencePath))
			MigrateLegacyFile(onlyMissing: loaded);
		UpdateStatus();
	}

	private bool MigrateLegacyFile(bool onlyMissing)
	{
		if (string.IsNullOrWhiteSpace(_legacyPersistencePath) || !File.Exists(_legacyPersistencePath)) return false;
		try
		{
			var envelope = JsonSerializer.Deserialize<PersistedIndex>(File.ReadAllText(_legacyPersistencePath), JsonOptions);
			if (envelope is null || envelope.SchemaVersion != SchemaVersion) return false;
			foreach (var group in envelope.Entries.GroupBy(entry => entry.NodeId))
			{
				if (onlyMissing && _entries.ContainsKey(group.Key)) continue;
				Apply(group.ToArray());
				PersistNode(group.Key, _entries[group.Key]);
			}
			// 迁移完成后删掉旧文件：它是一份会被永久误认为"权威索引"的 9 MB 死重。
			// Delete the legacy file once migrated; otherwise it stays as 9 MB of misleading dead weight.
			TryDeleteFile(_legacyPersistencePath);
			return true;
		}
		catch (IOException) { return false; }
		catch (UnauthorizedAccessException) { return false; }
		catch (JsonException) { return false; }
	}

	private void Apply(IReadOnlyList<ModDataIndexEntry> entries)
	{
		if (entries.Count == 0) return;
		var nodeId = entries[0].NodeId;
		_entries[nodeId] = entries;
		var providers = entries.Where(entry => entry.Relation == "Provider").ToArray();
		if (providers.Length != 0) _providers[nodeId] = providers;
		var consumers = entries.Where(entry => entry.Relation == "Consumer").ToArray();
		if (consumers.Length != 0) _consumers[nodeId] = consumers;
	}

	private void UpdateStatus()
	{
		var hasAny = !_entries.IsEmpty;
		_status = hasAny ? ModDataIndexStatus.Ready : ModDataIndexStatus.Unavailable;
	}

	private void PersistNode(ModNodeId nodeId, IReadOnlyList<ModDataIndexEntry> entries)
	{
		if (string.IsNullOrWhiteSpace(_nodesDirectory)) return;
		lock (_persistenceLock)
		{
			var path = Path.Combine(_nodesDirectory, nodeId.Value.ToString("N") + NodeFileExtension);
			WriteAtomically(path, entries);
		}
	}

	private void DeleteNodeFile(ModNodeId nodeId)
	{
		if (string.IsNullOrWhiteSpace(_nodesDirectory)) return;
		lock (_persistenceLock)
		{
			TryDeleteFile(Path.Combine(_nodesDirectory, nodeId.Value.ToString("N") + NodeFileExtension));
		}
	}

	private static IReadOnlyList<ModDataIndexEntry>? TryReadEntries(string path)
	{
		try
		{
			return JsonSerializer.Deserialize<ModDataIndexEntry[]>(File.ReadAllText(path), JsonOptions);
		}
		catch (IOException) { return null; }
		catch (UnauthorizedAccessException) { return null; }
		catch (JsonException) { return null; }
	}

	private static void WriteAtomically(string path, IReadOnlyList<ModDataIndexEntry> entries)
	{
		string? temporary = null;
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
			File.WriteAllText(temporary, JsonSerializer.Serialize(entries, JsonOptions));
			File.Move(temporary, path, true);
			temporary = null;
		}
		catch (IOException) { }
		catch (UnauthorizedAccessException) { }
		finally
		{
			// 以前这里没有 finally：File.Move 失败（目标被占用、磁盘满）时，一个 5-20 MB 的临时文件
			// 就永久留在 data/indexes 里。实测已积 94 个 / 580 MB。
			// This used to leak the temporary file whenever File.Move failed.
			if (temporary is not null) TryDeleteFile(temporary);
		}
	}

	private static void TryDeleteFile(string path)
	{
		try
		{
			if (!File.Exists(path)) return;
			var attributes = File.GetAttributes(path);
			if ((attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
			File.Delete(path);
		}
		catch (IOException) { }
		catch (UnauthorizedAccessException) { }
	}

	private sealed record PersistedIndex(int SchemaVersion, DateTimeOffset BuiltUtc, IReadOnlyList<ModDataIndexEntry> Entries);
}
