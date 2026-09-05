using HD2ModCore.Application;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

// 作用：基于 IModLibraryStore 的库管理器实现，提供删除节点/维护 Profile/更新元数据等操作。
// Purpose: IModLibraryStore-backed library manager providing delete node, profile maintenance and metadata updates.
public sealed class ModLibraryManager : IModLibraryManager
{
	private readonly StoragePaths _paths;
	private readonly IModLibraryStore _store;
	private readonly IModStorageDeletionService _storageDeletion;
	private readonly ProfileStructuralMaintenanceService _profileStructuralMaintenance = new();

	public ModLibraryManager(StoragePaths paths, IModLibraryStore store, IModStorageDeletionService? storageDeletion = null)
	{
		_paths = paths ?? throw new ArgumentNullException(nameof(paths));
		_store = store ?? throw new ArgumentNullException(nameof(store));
		_storageDeletion = storageDeletion ?? new ModStorageDeletionService(_paths);
	}

	public async ValueTask<LibrarySnapshot> LoadOrCreateAsync(CancellationToken cancellationToken = default)
	{
		var loaded = await _store.TryLoadAsync(cancellationToken).ConfigureAwait(false);
		if (loaded is not null)
		{
			return loaded;
		}

		var empty = new LibrarySnapshot(
			Version: 1,
			SavedUtc: DateTimeOffset.UtcNow,
			Nodes: new Dictionary<ModNodeId, ModNode>(),
			Profiles: new List<Profile>(),
			ActiveProfileId: null);

		await _store.SaveAsync(empty, cancellationToken).ConfigureAwait(false);
		return empty;
	}

	public async ValueTask<LibrarySnapshot> UpsertNodeAsync(ModNode node, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(node);
		var snapshot = await LoadOrCreateAsync(cancellationToken).ConfigureAwait(false);
		var nodes = new Dictionary<ModNodeId, ModNode>(snapshot.Nodes)
		{
			[node.Id] = node
		};
		var updated = snapshot with { Nodes = nodes, SavedUtc = DateTimeOffset.UtcNow };
		await _store.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
		return updated;
	}

	public async ValueTask<LibrarySnapshot> DeleteNodeAsync(ModNodeId nodeId, bool deleteStoredFiles, CancellationToken cancellationToken = default)
		=> await DeleteNodesAsync([nodeId], deleteStoredFiles, cancellationToken).ConfigureAwait(false);

	public async ValueTask<LibrarySnapshot> DeleteNodesAsync(IReadOnlyList<ModNodeId> nodeIds, bool deleteStoredFiles, CancellationToken cancellationToken = default)
	{
		var result = await DeleteNodesWithResultAsync(nodeIds, deleteStoredFiles, cancellationToken).ConfigureAwait(false);
		if (result.HasFailures) throw new IOException(BuildDeletionFailureMessage(result));
		return result.Snapshot;
	}

	public async ValueTask<ModLibraryDeletionResult> DeleteNodesWithResultAsync(
		IReadOnlyList<ModNodeId> nodeIds,
		bool deleteStoredFiles,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(nodeIds);
		var snapshot = await LoadOrCreateAsync(cancellationToken).ConfigureAwait(false);
		var requestedIds = nodeIds.Where(snapshot.Nodes.ContainsKey).Distinct().ToArray();
		if (requestedIds.Length == 0)
			return new ModLibraryDeletionResult(snapshot, Array.Empty<ModNodeId>(), Array.Empty<ModNodeId>(), Array.Empty<ModStorageDeletionResult>());

		var storageResults = new List<ModStorageDeletionResult>(requestedIds.Length);
		var removableIds = new HashSet<ModNodeId>();
		var failedIds = new List<ModNodeId>();
		foreach (var nodeId in requestedIds)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var node = snapshot.Nodes[nodeId];
			var storageResult = deleteStoredFiles
				? await _storageDeletion.DeleteAsync(node.RelativePath, cancellationToken).ConfigureAwait(false)
				: new ModStorageDeletionResult(node.RelativePath, ModStorageDeletionState.NotRequested);
			storageResults.Add(storageResult);
			if (storageResult.CanRemoveLibraryNode) removableIds.Add(nodeId);
			else failedIds.Add(nodeId);
		}

		if (removableIds.Count == 0)
			return new ModLibraryDeletionResult(snapshot, Array.Empty<ModNodeId>(), failedIds, storageResults);

		var timestamp = DateTimeOffset.UtcNow;
		var relationshipUpdates = ModRelationshipDeletionProjection.GetSurvivingOptionUpdates(
			snapshot,
			removableIds,
			timestamp);
		var nodes = new Dictionary<ModNodeId, ModNode>(snapshot.Nodes);
		foreach (var removedId in removableIds) nodes.Remove(removedId);
		foreach (var (nodeId, updatedNode) in relationshipUpdates)
			nodes[nodeId] = updatedNode;

		// Remove references from other nodes' children.
		foreach (var kv in nodes.ToList())
		{
			var updatedChildren = kv.Value.Children.Where(childId => !removableIds.Contains(childId)).ToList();
			if (updatedChildren.Count != kv.Value.Children.Count)
			{
				nodes[kv.Key] = kv.Value with { Children = updatedChildren };
			}
		}

		// Profile cleanup is intentionally performed in one pass with the deletion:
		// remove deleted references and reclaim only affected empty relationship
		// roots, so a single deletion produces at most one Profile revision.
		var updated = snapshot with { Nodes = nodes, Profiles = snapshot.Profiles, SavedUtc = timestamp };
		updated = _profileStructuralMaintenance
			.CleanupAfterMutation(
				snapshot,
				updated,
				removableIds.Concat(relationshipUpdates.Keys).Distinct().ToArray(),
				removableIds,
				timestamp)
			.Snapshot;
		var sidecarBackups = new List<RelationshipSidecarBackup>();
		try
		{
			foreach (var updatedNode in relationshipUpdates.Values)
			{
				var before = snapshot.Nodes[updatedNode.Id];
				if (!ModRelationshipDeletionProjection.RequiresStorageProtocolWrite(before, updatedNode)
					|| !TryResolveExistingManagedDirectory(updatedNode.RelativePath, out var directory))
					continue;
				sidecarBackups.Add(CaptureRelationshipSidecars(directory));
				ModNodeStorageProtocol.SynchronizeNodeMetadata(directory, updatedNode, cancellationToken);
			}
			await _store.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
		}
		catch
		{
			foreach (var backup in sidecarBackups) RestoreRelationshipSidecars(backup);
			throw;
		}
		return new ModLibraryDeletionResult(updated, removableIds.ToArray(), failedIds, storageResults);
	}

	public ValueTask<ModStoragePendingCleanupResult> RecoverPendingStoredDeletionsAsync(CancellationToken cancellationToken = default)
		=> _storageDeletion.RecoverPendingAsync(cancellationToken);

	public ValueTask<IReadOnlySet<string>> GetPendingStoredDeletionSynchronizationExclusionsAsync(CancellationToken cancellationToken = default)
		=> _storageDeletion.GetSynchronizationExclusionsAsync(cancellationToken);

	public async ValueTask<LibrarySnapshot> UpsertProfileAsync(Profile profile, CancellationToken cancellationToken = default)
	{
		if (profile is null)
		{
			throw new ArgumentNullException(nameof(profile));
		}

		var snapshot = await LoadOrCreateAsync(cancellationToken).ConfigureAwait(false);
		ValidateProfileEntries(snapshot, profile.Entries);
		var profiles = snapshot.Profiles.ToList();

		var index = profiles.FindIndex(p => p.Id == profile.Id);
		var normalized = profile with
		{
			ModifiedUtc = DateTimeOffset.UtcNow,
		};

		if (index >= 0)
		{
			profiles[index] = normalized;
		}
		else
		{
			profiles.Add(normalized with { CreatedUtc = DateTimeOffset.UtcNow });
		}

		var updated = snapshot with { Profiles = profiles, SavedUtc = DateTimeOffset.UtcNow };
		await _store.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
		return updated;
	}

	public async ValueTask<LibrarySnapshot> DeleteProfileAsync(ProfileId profileId, CancellationToken cancellationToken = default)
	{
		var snapshot = await LoadOrCreateAsync(cancellationToken).ConfigureAwait(false);
		var profiles = snapshot.Profiles.Where(p => p.Id != profileId).ToList();
		var activeProfileId = snapshot.ActiveProfileId == profileId ? null : snapshot.ActiveProfileId;
		var updated = snapshot with { Profiles = profiles, ActiveProfileId = activeProfileId, SavedUtc = DateTimeOffset.UtcNow };
		await _store.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
		return updated;
	}

	public async ValueTask<LibrarySnapshot> RenameProfileAsync(ProfileId profileId, string newName, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(newName))
		{
			throw new ArgumentException("Profile name cannot be empty.", nameof(newName));
		}

		var snapshot = await LoadOrCreateAsync(cancellationToken).ConfigureAwait(false);
		var profiles = snapshot.Profiles.ToList();
		var index = profiles.FindIndex(p => p.Id == profileId);
		if (index < 0)
		{
			return snapshot;
		}

		var normalizedName = newName.Trim();
		if (profiles.Any(p => p.Id != profileId && string.Equals(p.Name, normalizedName, StringComparison.OrdinalIgnoreCase)))
		{
			throw new InvalidOperationException($"Profile name already exists: {normalizedName}");
		}

		profiles[index] = profiles[index] with { Name = normalizedName, ModifiedUtc = DateTimeOffset.UtcNow };
		var updated = snapshot with { Profiles = profiles, SavedUtc = DateTimeOffset.UtcNow };
		await _store.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
		return updated;
	}

	public async ValueTask<LibrarySnapshot> SetActiveProfileAsync(ProfileId? profileId, CancellationToken cancellationToken = default)
	{
		var snapshot = await LoadOrCreateAsync(cancellationToken).ConfigureAwait(false);
		if (profileId is not null && snapshot.Profiles.All(profile => profile.Id != profileId.Value))
		{
			throw new InvalidOperationException($"Profile does not exist: {profileId.Value.Value:N}");
		}

		if (snapshot.ActiveProfileId == profileId)
		{
			return snapshot;
		}

		var updated = snapshot with { ActiveProfileId = profileId, SavedUtc = DateTimeOffset.UtcNow };
		await _store.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
		return updated;
	}

	public async ValueTask<LibrarySnapshot> AddProfileEntryAsync(ProfileId profileId, ModNodeId nodeId, CancellationToken cancellationToken = default)
	{
		var snapshot = await LoadOrCreateAsync(cancellationToken).ConfigureAwait(false);
		if (!snapshot.Nodes.ContainsKey(nodeId))
		{
			throw new InvalidOperationException($"Mod node does not exist: {nodeId.Value:N}");
		}
		if (snapshot.Nodes[nodeId].Metadata.Kind is not (ModNodeKind.Standard or ModNodeKind.ReShade)) return snapshot;

		return await UpdateProfileEntriesAsync(
			profileId,
			entries =>
			{
				if (entries.Any(e => e.NodeId == nodeId))
				{
					return entries;
				}

				var nextOrder = entries.Count == 0 ? 0 : entries.Max(e => e.LoadOrder) + 1;
				entries.Add(new ProfileEntry(nodeId, nextOrder));
				return NormalizeEntryOrder(entries);
			},
			cancellationToken).ConfigureAwait(false);
	}

	public async ValueTask<LibrarySnapshot> AddProfileEntriesAsync(ProfileId profileId, IReadOnlyList<ModNodeId> nodeIds, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(nodeIds);
		if (nodeIds.Count == 0) return await LoadOrCreateAsync(cancellationToken).ConfigureAwait(false);

		var snapshot = await LoadOrCreateAsync(cancellationToken).ConfigureAwait(false);
		var distinctIds = nodeIds.Distinct().ToArray();
		var missing = distinctIds.FirstOrDefault(nodeId => !snapshot.Nodes.ContainsKey(nodeId));
		if (missing != default && !snapshot.Nodes.ContainsKey(missing))
		{
			throw new InvalidOperationException($"Mod node does not exist: {missing.Value:N}");
		}
		var deployableIds = distinctIds
			.Where(nodeId => snapshot.Nodes[nodeId].Metadata.Kind is ModNodeKind.Standard or ModNodeKind.ReShade)
			.ToArray();
		if (deployableIds.Length == 0) return snapshot;

		return await UpdateProfileEntriesAsync(
			profileId,
			entries =>
			{
				var known = entries.Select(entry => entry.NodeId).ToHashSet();
				foreach (var nodeId in deployableIds)
				{
					if (known.Add(nodeId)) entries.Add(new ProfileEntry(nodeId, entries.Count));
				}
				return NormalizeEntryOrder(entries);
			},
			cancellationToken).ConfigureAwait(false);
	}

	public async ValueTask<LibrarySnapshot> RemoveProfileEntryAsync(ProfileId profileId, ModNodeId nodeId, CancellationToken cancellationToken = default)
	{
		return await UpdateProfileEntriesAsync(
			profileId,
			entries => NormalizeEntryOrder(entries.Where(e => e.NodeId != nodeId).ToList()),
			cancellationToken).ConfigureAwait(false);
	}

	public async ValueTask<LibrarySnapshot> RemoveProfileEntriesAsync(ProfileId profileId, IReadOnlyList<ModNodeId> nodeIds, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(nodeIds);
		if (nodeIds.Count == 0) return await LoadOrCreateAsync(cancellationToken).ConfigureAwait(false);
		var removed = nodeIds.ToHashSet();
		return await UpdateProfileEntriesAsync(
			profileId,
			entries => NormalizeEntryOrder(entries.Where(entry => !removed.Contains(entry.NodeId)).ToList()),
			cancellationToken).ConfigureAwait(false);
	}

	public async ValueTask<LibrarySnapshot> MoveProfileEntryAsync(ProfileId profileId, ModNodeId nodeId, int direction, CancellationToken cancellationToken = default)
	{
		if (direction == 0)
		{
			return await LoadOrCreateAsync(cancellationToken).ConfigureAwait(false);
		}

		return await UpdateProfileEntriesAsync(
			profileId,
			entries =>
			{
				var ordered = entries.OrderBy(e => e.LoadOrder).ThenBy(e => e.AddedUtc).ToList();
				var index = ordered.FindIndex(e => e.NodeId == nodeId);
				if (index < 0)
				{
					return ordered;
				}

				var target = direction < 0 ? index - 1 : index + 1;
				if (target < 0 || target >= ordered.Count)
				{
					return NormalizeEntryOrder(ordered);
				}

				(ordered[index], ordered[target]) = (ordered[target], ordered[index]);
				return NormalizeEntryOrder(ordered);
			},
			cancellationToken).ConfigureAwait(false);
	}


	public async ValueTask<LibrarySnapshot> UpdateNodeMetadataAsync(ModNodeId nodeId, ModNodeMetadata metadata, CancellationToken cancellationToken = default)
	{
		var snapshot = await LoadOrCreateAsync(cancellationToken).ConfigureAwait(false);
		var nodes = new Dictionary<ModNodeId, ModNode>(snapshot.Nodes);
		if (!nodes.TryGetValue(nodeId, out var node))
		{
			return snapshot;
		}

		nodes[nodeId] = node with { Metadata = metadata, RelativePath = node.RelativePath };
		var updated = snapshot with { Nodes = nodes, SavedUtc = DateTimeOffset.UtcNow };
		await _store.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
		return updated;
	}

	private async ValueTask<LibrarySnapshot> UpdateProfileEntriesAsync(ProfileId profileId, Func<List<ProfileEntry>, IReadOnlyList<ProfileEntry>> update, CancellationToken cancellationToken)
	{
		var snapshot = await LoadOrCreateAsync(cancellationToken).ConfigureAwait(false);
		var profiles = snapshot.Profiles.ToList();
		var index = profiles.FindIndex(p => p.Id == profileId);
		if (index < 0)
		{
			return snapshot;
		}

		var profile = profiles[index];
		var entries = profile.Entries.OrderBy(e => e.LoadOrder).ThenBy(e => e.AddedUtc).ToList();
		var updatedEntries = update(entries);
		if (updatedEntries.SequenceEqual(profile.Entries))
		{
			return snapshot;
		}

		profiles[index] = profile with
		{
			Entries = updatedEntries,
			ModifiedUtc = DateTimeOffset.UtcNow,
			Revision = checked(profile.Revision + 1),
		};

		var updated = snapshot with { Profiles = profiles, SavedUtc = DateTimeOffset.UtcNow };
		await _store.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
		return updated;
	}

	private static IReadOnlyList<ProfileEntry> NormalizeEntryOrder(IReadOnlyList<ProfileEntry> entries)
	{
		return entries
			.Select((entry, index) => entry with { LoadOrder = index })
			.ToList();
	}

	private bool TryResolveExistingManagedDirectory(string relativePath, out string directory)
	{
		directory = string.Empty;
		if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath)) return false;
		try
		{
			var root = Path.GetFullPath(_paths.ModsDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			var candidate = Path.GetFullPath(Path.Combine(root, relativePath));
			if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
				|| !Directory.Exists(candidate)) return false;
			directory = candidate;
			return true;
		}
		catch (ArgumentException) { return false; }
		catch (NotSupportedException) { return false; }
		catch (PathTooLongException) { return false; }
	}

	private static RelationshipSidecarBackup CaptureRelationshipSidecars(string directory)
		=> new(
			CaptureSidecar(ModNodeStorageProtocol.LogicalMarkerPath(directory)),
			CaptureSidecar(ModNodeStorageProtocol.OptionRelationPath(directory)));

	private static SidecarFileBackup CaptureSidecar(string path)
	{
		if (!File.Exists(path)) return new SidecarFileBackup(path, false, null, null);
		return new SidecarFileBackup(path, true, File.ReadAllBytes(path), File.GetAttributes(path));
	}

	private static void RestoreRelationshipSidecars(RelationshipSidecarBackup backup)
	{
		RestoreSidecar(backup.LogicalMarker);
		RestoreSidecar(backup.OptionRelation);
	}

	private static void RestoreSidecar(SidecarFileBackup backup)
	{
		try
		{
			if (!backup.Exists)
			{
				if (File.Exists(backup.Path))
				{
					var current = File.GetAttributes(backup.Path);
					if ((current & FileAttributes.ReadOnly) != 0)
						File.SetAttributes(backup.Path, current & ~FileAttributes.ReadOnly);
					File.Delete(backup.Path);
				}
				return;
			}

			Directory.CreateDirectory(Path.GetDirectoryName(backup.Path)!);
			if (File.Exists(backup.Path))
			{
				var current = File.GetAttributes(backup.Path);
				if ((current & FileAttributes.ReadOnly) != 0)
					File.SetAttributes(backup.Path, current & ~FileAttributes.ReadOnly);
			}
			File.WriteAllBytes(backup.Path, backup.Content!);
			if (backup.Attributes is { } attributes) File.SetAttributes(backup.Path, attributes);
		}
		catch { }
	}

	private static void ValidateProfileEntries(LibrarySnapshot snapshot, IReadOnlyList<ProfileEntry> entries)
	{
		var seen = new HashSet<ModNodeId>();
		foreach (var entry in entries)
		{
			if (!seen.Add(entry.NodeId))
				throw new InvalidOperationException($"Profile contains duplicate Mod entry: {entry.NodeId.Value:N}");
			if (!snapshot.Nodes.TryGetValue(entry.NodeId, out var node))
				throw new InvalidOperationException($"Profile references a missing Mod node: {entry.NodeId.Value:N}");
			EnsureNodeCanBeProfileEntry(node);
		}
	}

	private static void EnsureNodeCanBeProfileEntry(ModNode node)
	{
		if (node.Metadata.Kind is not (ModNodeKind.Standard or ModNodeKind.ReShade))
			throw new InvalidOperationException($"{node.Metadata.Kind} Mod cannot be added to a profile: {node.Metadata.Name}");
	}

	private static string BuildDeletionFailureMessage(ModLibraryDeletionResult result)
	{
		var details = result.Failures
			.Select(failure => $"{failure.RelativePath}: {failure.Detail ?? "物理目录无法删除。"}")
			.Take(3)
			.ToArray();
		return details.Length == 0
			? "Mod 存储目录无法删除，库快照未提交。"
			: "部分 Mod 存储目录无法删除，未移除对应库记录：" + string.Join("；", details);
	}

	private sealed record RelationshipSidecarBackup(SidecarFileBackup LogicalMarker, SidecarFileBackup OptionRelation);
	private sealed record SidecarFileBackup(string Path, bool Exists, byte[]? Content, FileAttributes? Attributes);
}
