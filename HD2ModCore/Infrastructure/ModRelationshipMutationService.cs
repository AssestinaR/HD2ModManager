using HD2ModCore.Application;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

/// <summary>
/// Owns structural Mod mutations that cross the library snapshot, relation
/// sidecar and host-scoped activation files. The desktop UI and CLI both call
/// this service so a relationship change has one validation and persistence
/// path.
/// </summary>
public sealed class ModRelationshipMutationService : IModRelationshipMutationService
{
	private readonly StoragePaths _paths;
	private readonly IModLibraryStore _store;
	private readonly OptionActivationStore _optionActivations;
	private readonly OptionActivationDefaultInitializer _defaultInitializer = new();
	private readonly ProfileStructuralMaintenanceService _profileStructuralMaintenance = new();

	public ModRelationshipMutationService(
		StoragePaths paths,
		IModLibraryStore store,
		OptionActivationStore optionActivations)
	{
		_paths = paths ?? throw new ArgumentNullException(nameof(paths));
		_store = store ?? throw new ArgumentNullException(nameof(store));
		_optionActivations = optionActivations ?? throw new ArgumentNullException(nameof(optionActivations));
	}

	public async ValueTask<LogicalModCreationResult> CreateLogicalContainerAsync(
		LibrarySnapshot snapshot,
		string name,
		string? notes = null,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		if (string.IsNullOrWhiteSpace(name))
			throw Failure(ModRelationshipMutationFailure.EmptyName, "逻辑 Mod 名称不能为空。");

		var normalizedName = name.Trim();
		var nodeId = ModNodeId.New();
		Directory.CreateDirectory(_paths.ModsDirectory);
		var usedRelativePaths = snapshot.Nodes.Values
			.Select(node => NormalizeRelativePath(node.RelativePath))
			.Where(path => !string.IsNullOrWhiteSpace(path))
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		var relativePath = CreateUniqueDirectoryName(normalizedName, usedRelativePaths);
		var directory = ResolvePhysicalDirectory(relativePath, requireExisting: false);
		var now = DateTimeOffset.UtcNow;
		var node = new ModNode(
			nodeId,
			relativePath,
			new ModNodeMetadata(
				normalizedName,
				string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
				now,
				now,
				ModNodeKind.Standard,
				IsLogicalContainer: true),
			Array.Empty<PatchGroupKey>(),
			Array.Empty<ModNodeId>());

		Directory.CreateDirectory(directory);
		try
		{
			ModNodeStorageProtocol.WriteLogicalMarker(directory, nodeId, cancellationToken);
			var nodes = new Dictionary<ModNodeId, ModNode>(snapshot.Nodes) { [nodeId] = node };
			var updated = snapshot with { Nodes = nodes, SavedUtc = now };
			await _store.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
			return new LogicalModCreationResult(updated, node, Changed: true);
		}
		catch
		{
			TryDeleteDirectory(directory);
			throw;
		}
	}

	public ValueTask<ModRelationshipMutationResult> AddOptionParentsAsync(
		LibrarySnapshot snapshot,
		ModNodeId hostId,
		IReadOnlyList<ModNodeId> optionIds,
		CancellationToken cancellationToken = default)
		=> MutateOptionParentsAsync(snapshot, hostId, optionIds, add: true, cancellationToken);

	public ValueTask<ModRelationshipMutationResult> RemoveOptionParentsAsync(
		LibrarySnapshot snapshot,
		ModNodeId hostId,
		IReadOnlyList<ModNodeId> optionIds,
		CancellationToken cancellationToken = default)
		=> MutateOptionParentsAsync(snapshot, hostId, optionIds, add: false, cancellationToken);

	private async ValueTask<ModRelationshipMutationResult> MutateOptionParentsAsync(
		LibrarySnapshot snapshot,
		ModNodeId hostId,
		IReadOnlyList<ModNodeId> optionIds,
		bool add,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		ArgumentNullException.ThrowIfNull(optionIds);
		cancellationToken.ThrowIfCancellationRequested();
		if (optionIds.Count == 0)
			throw Failure(ModRelationshipMutationFailure.EmptySelection, "至少选择一个 Mod。");

		var selected = new List<ModNodeId>();
		var selectedSet = new HashSet<ModNodeId>();
		foreach (var optionId in optionIds)
		{
			if (!selectedSet.Add(optionId))
				throw Failure(ModRelationshipMutationFailure.DuplicateSelection, $"重复选择了 Mod：{Key(optionId)}。", hostId, optionId);
			selected.Add(optionId);
		}

		_ = ResolveHost(snapshot, hostId);
		var timestamp = DateTimeOffset.UtcNow;
		var originalGraph = OptionRelationshipGraph.Create(snapshot);
		var beforeActivation = _optionActivations.CreateSnapshot();
		var nodes = new Dictionary<ModNodeId, ModNode>(snapshot.Nodes);
		var changedNodeIds = new List<ModNodeId>();
		var addedRelationIds = new List<ModNodeId>();
		var removedRelationIds = new List<ModNodeId>();
		var sidecarNodes = new List<(ModNode Before, ModNode After)>();

		foreach (var sourceId in selected)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (!nodes.TryGetValue(sourceId, out var source))
				throw Failure(ModRelationshipMutationFailure.SourceNotFound, $"未找到来源 Mod：{Key(sourceId)}。", hostId, sourceId);
			if (sourceId == hostId)
				throw Failure(ModRelationshipMutationFailure.SelfRelation, "不能将 Mod 设置为自己的选项。", hostId, sourceId);
			if (source.Metadata.Kind is ModNodeKind.Decoration or ModNodeKind.ReShade)
				throw Failure(ModRelationshipMutationFailure.SourceKindUnsupported, $"“{source.Metadata.Name}”不能作为 Option。", hostId, sourceId);

			var currentHosts = ParseAndValidateHosts(snapshot, source);
			var nextHosts = new HashSet<ModNodeId>(currentHosts);
			var relationChanged = add ? nextHosts.Add(hostId) : nextHosts.Remove(hostId);
			if (!add && source.Metadata.Kind != ModNodeKind.Option)
				throw Failure(ModRelationshipMutationFailure.SourceKindUnsupported, $"“{source.Metadata.Name}”不是 Option，不能移除 Option 父级。", hostId, sourceId);
			if (!add && !relationChanged)
				throw Failure(ModRelationshipMutationFailure.DirectRelationNotFound, $"“{source.Metadata.Name}”不是当前主体的直属 Option。", hostId, sourceId);

			if (relationChanged)
			{
				if (add) addedRelationIds.Add(sourceId); else removedRelationIds.Add(sourceId);
				var becomesStandard = !add && nextHosts.Count == 0;
				var hasNoPayload = source.PatchGroups.Count == 0;
				var keepsLogicalContainer = hasNoPayload
					&& (source.Metadata.IsLogicalContainer
						|| originalGraph.GetDirectOptions(sourceId).Count > 0);
				var metadata = source.Metadata with
				{
					Kind = becomesStandard ? ModNodeKind.Standard : ModNodeKind.Option,
					HostModGuids = becomesStandard
						? null
						: nextHosts.OrderBy(value => value.Value).Select(Key).ToArray(),
					OptionOrder = becomesStandard ? null : source.Metadata.OptionOrder,
					SuppressDefaultDirectOptionActivation = becomesStandard ? false : source.Metadata.SuppressDefaultDirectOptionActivation,
					// The marker describes an intentionally empty structural node,
					// not every Option. Patch-bearing Options remain ordinary physical
					// packages while still being valid parents in the relation graph.
					IsLogicalContainer = becomesStandard
						? keepsLogicalContainer
						: hasNoPayload,
					ModifiedUtc = timestamp,
				};
				var updatedSource = source with { Metadata = metadata };
				nodes[sourceId] = updatedSource;
				changedNodeIds.Add(sourceId);
				sidecarNodes.Add((source, updatedSource));
			}
			else if (add && source.Metadata.Kind == ModNodeKind.Option)
			{
				// An old/hand-edited profile may still contain an Option. The
				// relationship conversion remains idempotent, but its Profile entry
				// must still be released below.
				ValidatePhysicalDirectory(source);
			}
		}

		// Joining a host turns ordinary Profile entries into child Options. The
		// same maintenance pass also handles stale entries and empty affected
		// hosts, avoiding a second Profile revision for one relationship change.
		var proposed = snapshot with { Nodes = nodes, Profiles = snapshot.Profiles, SavedUtc = timestamp };
		ValidateAcyclicRelations(proposed);
		var structuralMaintenance = _profileStructuralMaintenance.CleanupAfterMutation(
			snapshot,
			proposed,
			changedNodeIds
				.Concat(new[] { hostId })
				.Concat(selected)
				.Distinct()
				.ToArray(),
			add ? selectedSet : null,
			timestamp);
		proposed = structuralMaintenance.Snapshot;
		var affectedProfiles = structuralMaintenance.AffectedProfileIds;

		if (changedNodeIds.Count == 0 && affectedProfiles.Count == 0)
			return new ModRelationshipMutationResult(snapshot, hostId, Array.Empty<ModNodeId>(), Array.Empty<ModNodeId>(), Array.Empty<ModNodeId>(), Array.Empty<ProfileId>(), false);

		var backups = sidecarNodes
			.SelectMany(pair => GetSidecarPaths(pair.Before, pair.After).Select(path => CaptureFile(path)))
			.GroupBy(backup => backup.Path, StringComparer.OrdinalIgnoreCase)
			.Select(group => group.First())
			.ToArray();
		var committedSnapshot = false;
		try
		{
			foreach (var (before, after) in sidecarNodes)
				ModNodeStorageProtocol.SynchronizeNodeMetadata(ValidatePhysicalDirectory(before), after, cancellationToken);

			await _store.SaveAsync(proposed, cancellationToken).ConfigureAwait(false);
			committedSnapshot = true;
			if (add)
			{
				var initializations = _defaultInitializer
					.PlanMissingStates(proposed, beforeActivation, changedNodeIds.Append(hostId), cancellationToken: cancellationToken)
					.ToList();
				AssignHostLocalMissingOrders(proposed, hostId, initializations);
				if (initializations.Count > 0)
					await _optionActivations.InitializeMissingStatesAsync(initializations, cancellationToken).ConfigureAwait(false);
			}
			else
			{
				var removedRelations = removedRelationIds.Select(optionId => (Key(optionId), Key(hostId))).ToArray();
				if (removedRelations.Length > 0)
					await _optionActivations.RemoveRelationsAsync(removedRelations, cancellationToken).ConfigureAwait(false);
				foreach (var optionId in removedRelationIds)
				{
					if (proposed.Nodes.TryGetValue(optionId, out var node) && node.Metadata.Kind == ModNodeKind.Standard)
						await _optionActivations.RemoveAsync(Key(optionId), cancellationToken).ConfigureAwait(false);
				}
			}
		}
		catch
		{
			try
			{
				if (committedSnapshot) await _store.SaveAsync(snapshot, CancellationToken.None).ConfigureAwait(false);
				await _optionActivations.RestoreSnapshotAsync(beforeActivation, CancellationToken.None).ConfigureAwait(false);
				foreach (var backup in backups) RestoreFile(backup);
			}
			catch { }
			throw;
		}

		return new ModRelationshipMutationResult(
			proposed,
			hostId,
			changedNodeIds.Distinct().ToArray(),
			addedRelationIds.Distinct().ToArray(),
			removedRelationIds.Distinct().ToArray(),
			affectedProfiles.ToArray(),
			true);
	}

	private ModNode ResolveHost(LibrarySnapshot snapshot, ModNodeId hostId)
	{
		if (!snapshot.Nodes.TryGetValue(hostId, out var host))
			throw Failure(ModRelationshipMutationFailure.HostNotFound, $"未找到目标主体 Mod：{Key(hostId)}。", hostId);
		if (host.Metadata.Kind is not (ModNodeKind.Standard or ModNodeKind.Option))
			throw Failure(ModRelationshipMutationFailure.HostKindUnsupported, $"“{host.Metadata.Name}”不能作为 Option 主体。", hostId);
		return host;
	}

	private static IReadOnlySet<ModNodeId> ParseAndValidateHosts(LibrarySnapshot snapshot, ModNode source)
	{
		var result = new HashSet<ModNodeId>();
		foreach (var rawHost in source.Metadata.HostModGuids ?? Array.Empty<string>())
		{
			if (!Guid.TryParse(rawHost, out var parsed))
				throw Failure(ModRelationshipMutationFailure.InvalidExistingRelation, $"“{source.Metadata.Name}”包含无效的 Option 主体 GUID：{rawHost}。", sourceId: source.Id);
			var hostId = new ModNodeId(parsed);
			if (!snapshot.Nodes.TryGetValue(hostId, out var host)
				|| host.Metadata.Kind is not (ModNodeKind.Standard or ModNodeKind.Option))
				throw Failure(ModRelationshipMutationFailure.InvalidExistingRelation, $"“{source.Metadata.Name}”包含不存在或不支持的 Option 主体：{rawHost}。", sourceId: source.Id);
			result.Add(hostId);
		}
		return result;
	}

	private static void AssignHostLocalMissingOrders(
		LibrarySnapshot snapshot,
		ModNodeId hostId,
		ICollection<OptionActivationInitialization> initializations)
	{
		var graph = OptionRelationshipGraph.Create(snapshot);
		var ordered = graph.GetDirectOptions(hostId)
			.Select((option, index) => (Option: Key(option.Id), Order: index))
			.ToDictionary(item => item.Option, item => item.Order, StringComparer.OrdinalIgnoreCase);
		if (ordered.Count == 0) return;
		var hostKey = Key(hostId);
		foreach (var item in initializations.ToArray())
		{
			if (!string.Equals(item.HostId, hostKey, StringComparison.OrdinalIgnoreCase)
				|| !ordered.TryGetValue(item.OptionId, out var order)) continue;
			var replacement = item with { EffectiveOrder = order };
			initializations.Remove(item);
			initializations.Add(replacement);
		}
	}

	private void ValidateAcyclicRelations(LibrarySnapshot snapshot)
	{
		var edges = new Dictionary<ModNodeId, IReadOnlyList<ModNodeId>>();
		foreach (var option in snapshot.Nodes.Values.Where(node => node.Metadata.Kind == ModNodeKind.Option))
		{
			var parents = ParseAndValidateHosts(snapshot, option);
			edges[option.Id] = parents.ToArray();
		}

		var state = new Dictionary<ModNodeId, byte>();
		var path = new List<ModNodeId>();
		foreach (var nodeId in edges.Keys) Visit(nodeId);

		void Visit(ModNodeId current)
		{
			if (state.TryGetValue(current, out var currentState))
			{
				if (currentState == 1)
				{
					var start = path.IndexOf(current);
					var cycle = (start >= 0 ? path.Skip(start) : path).Append(current).ToArray();
					throw Failure(ModRelationshipMutationFailure.CycleDetected, $"Option 关系形成循环：{string.Join(" -> ", cycle.Select(Key))}。", cyclePath: cycle);
				}
				if (currentState == 2) return;
			}

			state[current] = 1;
			path.Add(current);
			if (edges.TryGetValue(current, out var parents))
				foreach (var parent in parents) Visit(parent);
			path.RemoveAt(path.Count - 1);
			state[current] = 2;
		}
	}

	private IEnumerable<string> GetSidecarPaths(ModNode before, ModNode after)
	{
		var directory = ValidatePhysicalDirectory(before);
		yield return ModNodeStorageProtocol.LogicalMarkerPath(directory);
		yield return ModNodeStorageProtocol.OptionRelationPath(directory);
	}

	private string ValidatePhysicalDirectory(ModNode node)
		=> ResolvePhysicalDirectory(node.RelativePath, requireExisting: true);

	private string ResolvePhysicalDirectory(string relativePath, bool requireExisting)
	{
		var normalized = NormalizeRelativePath(relativePath);
		if (string.IsNullOrWhiteSpace(normalized) || Path.IsPathRooted(normalized))
			throw Failure(ModRelationshipMutationFailure.StoragePathInvalid, $"Mod 存储路径无效：{relativePath}。");
		var root = Path.GetFullPath(_paths.ModsDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		var full = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
		if (!IsWithin(root, full))
			throw Failure(ModRelationshipMutationFailure.StoragePathInvalid, $"Mod 存储路径越界：{relativePath}。");
		if (requireExisting && !Directory.Exists(full))
			throw Failure(ModRelationshipMutationFailure.StorageMissing, $"Mod 存储目录不存在：{relativePath}。");
		return full;
	}

	private string CreateUniqueDirectoryName(string displayName, ISet<string> usedRelativePaths)
	{
		var baseName = SanitizeFileName(displayName);
		var candidate = baseName;
		var suffix = 2;
		while (usedRelativePaths.Contains(candidate) || Directory.Exists(Path.Combine(_paths.ModsDirectory, candidate)))
			candidate = $"{baseName}_{suffix++}";
		return candidate;
	}

	private static FileBackup CaptureFile(string path)
	{
		if (!File.Exists(path)) return new FileBackup(path, false, null, null);
		return new FileBackup(path, true, File.ReadAllBytes(path), File.GetAttributes(path));
	}

	private static void RestoreFile(FileBackup backup)
	{
		try
		{
			if (!backup.Exists)
			{
				DeleteFileIfPresent(backup.Path);
				return;
			}
			var attributes = File.Exists(backup.Path) ? File.GetAttributes(backup.Path) : FileAttributes.Normal;
			if ((attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(backup.Path, attributes & ~FileAttributes.ReadOnly);
			Directory.CreateDirectory(Path.GetDirectoryName(backup.Path)!);
			File.WriteAllBytes(backup.Path, backup.Content!);
			if (backup.Attributes is { } original) File.SetAttributes(backup.Path, original);
		}
		catch { }
	}

	private static void DeleteFileIfPresent(string path)
	{
		if (!File.Exists(path)) return;
		try
		{
			var attributes = File.GetAttributes(path);
			if ((attributes & FileAttributes.ReadOnly) != 0)
				File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
			File.Delete(path);
		}
		catch (FileNotFoundException) { }
	}

	private static void TryDeleteDirectory(string directory)
	{
		try
		{
			if (!Directory.Exists(directory)) return;
			foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
			{
				try { File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly); } catch { }
			}
			Directory.Delete(directory, true);
		}
		catch { }
	}

	private static string NormalizeRelativePath(string? value)
		=> string.IsNullOrWhiteSpace(value) || value == "." ? string.Empty : value.Replace('\\', '/').Trim('/');

	private static string SanitizeFileName(string value)
	{
		var result = value.Trim();
		foreach (var invalid in Path.GetInvalidFileNameChars()) result = result.Replace(invalid, '_');
		return string.IsNullOrWhiteSpace(result) ? "逻辑 Mod" : result;
	}

	private static bool IsWithin(string root, string path)
		=> string.Equals(root, path, StringComparison.OrdinalIgnoreCase)
			|| path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

	private static string Key(ModNodeId id) => id.Value.ToString("N");

	private static ModRelationshipMutationException Failure(
		ModRelationshipMutationFailure failure,
		string message,
		ModNodeId? hostId = null,
		ModNodeId? sourceId = null,
		IReadOnlyList<ModNodeId>? cyclePath = null)
		=> new(failure, message, hostId, sourceId, cyclePath);

	private sealed record FileBackup(string Path, bool Exists, byte[]? Content, FileAttributes? Attributes);
}
