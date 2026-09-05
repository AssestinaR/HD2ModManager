using HD2ModCore.Application;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCli;

/// <summary>
/// Thin CLI-side orchestration around Core mutation primitives. It owns no UI
/// state and never deploys, refreshes derived facts, or opens WPF services.
/// Every caller must supply an already-leased <see cref="CliLibraryWriteSession"/>.
/// </summary>
internal static class CliLibraryMutationOperations
{
	public static async ValueTask<LogicalModCreationResult> CreateLogicalModAsync(
		CliLibraryWriteSession session,
		string name,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(session);
		var snapshot = await RequireSnapshotAsync(session, cancellationToken).ConfigureAwait(false);
		var optionActivations = await CreateValidatedOptionActivationStoreAsync(session, cancellationToken).ConfigureAwait(false);
		var service = CoreServices.CreateModRelationshipMutationService(session.Paths, optionActivations);
		try
		{
			await session.VerifyReadyForMutationAsync(cancellationToken).ConfigureAwait(false);
			return await service
				.CreateLogicalContainerAsync(snapshot, name, cancellationToken: cancellationToken)
				.ConfigureAwait(false);
		}
		catch (ModRelationshipMutationException exception)
		{
			throw ToCliUserException(exception);
		}
	}

	public static async ValueTask<ModRelationshipMutationResult> AddOptionParentsAsync(
		CliLibraryWriteSession session,
		string hostSelector,
		IReadOnlyList<string> optionSelectors,
		CancellationToken cancellationToken = default)
		=> await MutateOptionParentsAsync(session, hostSelector, optionSelectors, add: true, cancellationToken).ConfigureAwait(false);

	public static async ValueTask<ModRelationshipMutationResult> RemoveOptionParentsAsync(
		CliLibraryWriteSession session,
		string hostSelector,
		IReadOnlyList<string> optionSelectors,
		CancellationToken cancellationToken = default)
		=> await MutateOptionParentsAsync(session, hostSelector, optionSelectors, add: false, cancellationToken).ConfigureAwait(false);

	private static async ValueTask<ModRelationshipMutationResult> MutateOptionParentsAsync(
		CliLibraryWriteSession session,
		string hostSelector,
		IReadOnlyList<string> optionSelectors,
		bool add,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(hostSelector);
		ArgumentNullException.ThrowIfNull(optionSelectors);
		if (optionSelectors.Count == 0)
			throw new CliUserException("OptionParentSelectionRequired", "请至少提供一个要设置关系的 Mod GUID 或唯一 GUID 前缀。");

		var snapshot = await RequireSnapshotAsync(session, cancellationToken).ConfigureAwait(false);
		var host = CliProjections.ResolveMod(snapshot.Nodes, hostSelector);
		var optionIds = optionSelectors
			.Select(selector => CliProjections.ResolveMod(snapshot.Nodes, selector).Id)
			.ToArray();
		var optionActivations = await CreateValidatedOptionActivationStoreAsync(session, cancellationToken).ConfigureAwait(false);
		var service = CoreServices.CreateModRelationshipMutationService(session.Paths, optionActivations);
		try
		{
			await session.VerifyReadyForMutationAsync(cancellationToken).ConfigureAwait(false);
			return add
				? await service.AddOptionParentsAsync(snapshot, host.Id, optionIds, cancellationToken).ConfigureAwait(false)
				: await service.RemoveOptionParentsAsync(snapshot, host.Id, optionIds, cancellationToken).ConfigureAwait(false);
		}
		catch (ModRelationshipMutationException exception)
		{
			throw ToCliUserException(exception);
		}
	}

	public static SafeModDeletionPlan CreateDeletionPlan(
		CliLibraryState state,
		IReadOnlyList<string> selectors,
		SafeModDeletionScope scope)
	{
		ArgumentNullException.ThrowIfNull(state);
		ArgumentNullException.ThrowIfNull(selectors);
		var nodeIds = selectors
			.Select(selector => CliProjections.ResolveMod(state.Snapshot.Nodes, selector).Id)
			.ToArray();
		return CreateDeletionPlan(state.Snapshot, nodeIds, scope, CreateDeletionSafetySnapshot(state.DecorationActivations));
	}

	public static async ValueTask<CliModDeletionExecutionResult> DeleteAsync(
		CliLibraryWriteSession session,
		IReadOnlyList<string> selectors,
		SafeModDeletionScope scope,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(session);
		ArgumentNullException.ThrowIfNull(selectors);
		var snapshot = await RequireSnapshotAsync(session, cancellationToken).ConfigureAwait(false);
		if (selectors.Count == 0)
			throw new CliUserException("ModDeletionSelectionRequired", "请至少提供一个要删除的 Mod GUID 或唯一 GUID 前缀。");

		var optionActivations = await CreateValidatedOptionActivationStoreAsync(session, cancellationToken).ConfigureAwait(false);
		var decorationActivations = await CreateValidatedDecorationActivationStoreAsync(session, cancellationToken).ConfigureAwait(false);
		var requestedIds = selectors
			.Select(selector => CliProjections.ResolveMod(snapshot.Nodes, selector).Id)
			.ToArray();
		var safety = CreateDeletionSafetySnapshot(decorationActivations.CreateSnapshot().EnabledHostsByDecoration);
		var plan = CreateDeletionPlan(snapshot, requestedIds, scope, safety);
		if (plan.IsBlocked)
		{
			return new CliModDeletionExecutionResult(
				CliModDeletionStatus.Blocked,
				plan,
				AppliedPlan: null,
				snapshot,
				Array.Empty<ModNodeId>(),
				plan.LibraryNodeRemovals.Select(removal => removal.NodeId).ToArray(),
				Array.Empty<ModNodeId>(),
				Array.Empty<ModStorageDeletionResult>(),
				OptionActivationCleanup: null,
				DecorationActivationCleanup: null,
				PostCommitError: null);
		}

		var physical = await DeletePlannedStorageAsync(session.Paths, snapshot, plan, cancellationToken).ConfigureAwait(false);
		if (physical.RemovedNodeIds.Count == 0)
		{
			return new CliModDeletionExecutionResult(
				CliModDeletionStatus.StorageNotRemoved,
				plan,
				AppliedPlan: null,
				snapshot,
				physical.RemovedNodeIds,
				physical.FailedNodeIds,
				physical.SkippedNodeIds,
				physical.StorageResults,
				OptionActivationCleanup: null,
				DecorationActivationCleanup: null,
				PostCommitError: null);
		}

		var appliedPlan = CreateDeletionPlan(snapshot, physical.RemovedNodeIds, SafeModDeletionScope.Selected, safety);
		if (appliedPlan.IsBlocked)
		{
			return new CliModDeletionExecutionResult(
				CliModDeletionStatus.PostCommitIncomplete,
				plan,
				appliedPlan,
				snapshot,
				physical.RemovedNodeIds,
				physical.FailedNodeIds,
				physical.SkippedNodeIds,
				physical.StorageResults,
				OptionActivationCleanup: null,
				DecorationActivationCleanup: null,
				PostCommitError: "物理目录已移除，但重新验证的逻辑删除计划被安全规则阻断；库快照未写入。请使用管理器检查并恢复。" );
		}

		var updatedSnapshot = ApplyDeletionPlan(snapshot, appliedPlan);
		var sidecarBackups = SynchronizeSurvivingRelationshipStorage(session.Paths, snapshot, updatedSnapshot, cancellationToken);
		LibrarySnapshot persisted;
		try
		{
			persisted = await session.SaveSnapshotAsync(updatedSnapshot, cancellationToken).ConfigureAwait(false);
		}
		catch
		{
			foreach (var backup in sidecarBackups) RestoreRelationshipSidecars(backup);
			throw;
		}
		try
		{
			await session.VerifyReadyForMutationAsync(cancellationToken).ConfigureAwait(false);
			var optionCleanup = await CleanupOptionActivationsAsync(optionActivations, appliedPlan, cancellationToken).ConfigureAwait(false);
			var decorationCleanup = await CleanupDecorationActivationsAsync(decorationActivations, appliedPlan, cancellationToken).ConfigureAwait(false);
			return new CliModDeletionExecutionResult(
				CliModDeletionStatus.Completed,
				plan,
				appliedPlan,
				persisted,
				physical.RemovedNodeIds,
				physical.FailedNodeIds,
				physical.SkippedNodeIds,
				physical.StorageResults,
				optionCleanup,
				decorationCleanup,
				PostCommitError: null);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
		{
			return new CliModDeletionExecutionResult(
				CliModDeletionStatus.PostCommitIncomplete,
				plan,
				appliedPlan,
				persisted,
				physical.RemovedNodeIds,
				physical.FailedNodeIds,
				physical.SkippedNodeIds,
				physical.StorageResults,
				OptionActivationCleanup: null,
				DecorationActivationCleanup: null,
				PostCommitError: exception.Message);
		}
	}

	public static async ValueTask<CliProfileMutationExecutionResult> CreateProfileAsync(
		CliLibraryWriteSession session,
		string name,
		bool makeActive,
		CancellationToken cancellationToken = default)
		=> await ApplyProfileMutationAsync(
			session,
			snapshot => CoreServices.CreateProfileMutationService().CreateProfile(snapshot, name, makeActive),
			cancellationToken).ConfigureAwait(false);

	public static async ValueTask<CliProfileMutationExecutionResult> DeleteProfileAsync(
		CliLibraryWriteSession session,
		string profileSelector,
		CancellationToken cancellationToken = default)
		=> await ApplyProfileMutationForSelectedProfileAsync(
			session,
			profileSelector,
			(snapshot, profile) => CoreServices.CreateProfileMutationService().DeleteProfile(snapshot, profile.Id),
			cancellationToken).ConfigureAwait(false);

	public static async ValueTask<CliProfileMutationExecutionResult> RenameProfileAsync(
		CliLibraryWriteSession session,
		string profileSelector,
		string name,
		CancellationToken cancellationToken = default)
		=> await ApplyProfileMutationForSelectedProfileAsync(
			session,
			profileSelector,
			(snapshot, profile) => CoreServices.CreateProfileMutationService().RenameProfile(snapshot, profile.Id, name),
			cancellationToken).ConfigureAwait(false);

	public static async ValueTask<CliProfileMutationExecutionResult> SetActiveProfileAsync(
		CliLibraryWriteSession session,
		string profileSelector,
		CancellationToken cancellationToken = default)
		=> await ApplyProfileMutationForSelectedProfileAsync(
			session,
			profileSelector,
			(snapshot, profile) => CoreServices.CreateProfileMutationService().SetActiveProfile(snapshot, profile.Id),
			cancellationToken).ConfigureAwait(false);

	public static async ValueTask<CliProfileMutationExecutionResult> ClearActiveProfileAsync(
		CliLibraryWriteSession session,
		CancellationToken cancellationToken = default)
		=> await ApplyProfileMutationAsync(
			session,
			snapshot => CoreServices.CreateProfileMutationService().SetActiveProfile(snapshot, null),
			cancellationToken).ConfigureAwait(false);

	public static async ValueTask<CliProfileMutationExecutionResult> AddProfileEntriesAsync(
		CliLibraryWriteSession session,
		string profileSelector,
		IReadOnlyList<string> nodeSelectors,
		CancellationToken cancellationToken = default)
		=> await ApplyProfileEntriesAsync(
			session,
			profileSelector,
			nodeSelectors,
			(snapshot, service, profile, nodeIds) => service.AddEntries(snapshot, profile.Id, nodeIds),
			cancellationToken).ConfigureAwait(false);

	public static async ValueTask<CliProfileMutationExecutionResult> RemoveProfileEntriesAsync(
		CliLibraryWriteSession session,
		string profileSelector,
		IReadOnlyList<string> nodeSelectors,
		CancellationToken cancellationToken = default)
		=> await ApplyProfileEntriesAsync(
			session,
			profileSelector,
			nodeSelectors,
			(snapshot, service, profile, nodeIds) => service.RemoveEntries(snapshot, profile.Id, nodeIds),
			cancellationToken).ConfigureAwait(false);

	public static async ValueTask<CliProfileMutationExecutionResult> InsertProfileEntriesAsync(
		CliLibraryWriteSession session,
		string profileSelector,
		IReadOnlyList<string> nodeSelectors,
		int oneBasedInsertionPosition,
		CancellationToken cancellationToken = default)
		=> await ApplyProfileEntriesAsync(
			session,
			profileSelector,
			nodeSelectors,
			(snapshot, service, profile, nodeIds) => service.InsertEntries(snapshot, profile.Id, nodeIds, oneBasedInsertionPosition - 1),
			cancellationToken).ConfigureAwait(false);

	public static async ValueTask<CliProfileMutationExecutionResult> MoveProfileEntriesAsync(
		CliLibraryWriteSession session,
		string profileSelector,
		IReadOnlyList<string> nodeSelectors,
		int oneBasedInsertionPosition,
		CancellationToken cancellationToken = default)
		=> await ApplyProfileEntriesAsync(
			session,
			profileSelector,
			nodeSelectors,
			(snapshot, service, profile, nodeIds) => service.MoveEntries(snapshot, profile.Id, nodeIds, oneBasedInsertionPosition - 1),
			cancellationToken).ConfigureAwait(false);

	public static async ValueTask<CliProfileMutationExecutionResult> ReorderProfileEntriesAsync(
		CliLibraryWriteSession session,
		string profileSelector,
		IReadOnlyList<string> nodeSelectors,
		CancellationToken cancellationToken = default)
		=> await ApplyProfileEntriesAsync(
			session,
			profileSelector,
			nodeSelectors,
			(snapshot, service, profile, nodeIds) => service.ReorderEntries(snapshot, profile.Id, nodeIds),
			cancellationToken).ConfigureAwait(false);

	public static async ValueTask<CliNodeMetadataMutationResult> RenameAsync(
		CliLibraryWriteSession session,
		string selector,
		string requestedName,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(requestedName))
			throw new CliUserException("ModNameRequired", "Mod 名称不能为空。请提供非空名称。");
		var snapshot = await RequireSnapshotAsync(session, cancellationToken).ConfigureAwait(false);
		var before = CliProjections.ResolveMod(snapshot.Nodes, selector);
		var normalizedName = requestedName.Trim();
		if (string.Equals(before.Metadata.Name, normalizedName, StringComparison.Ordinal))
			return new CliNodeMetadataMutationResult(before, before, Changed: false);

		var updated = before with
		{
			Metadata = before.Metadata with
			{
				Name = normalizedName,
				ModifiedUtc = DateTimeOffset.UtcNow,
			},
		};
		var persisted = await SaveNodeAsync(session, snapshot, updated, cancellationToken).ConfigureAwait(false);
		return new CliNodeMetadataMutationResult(before, persisted, Changed: true);
	}

	public static async ValueTask<CliNodeMetadataMutationResult> SetNotesAsync(
		CliLibraryWriteSession session,
		string selector,
		string? requestedNotes,
		CancellationToken cancellationToken = default)
	{
		var snapshot = await RequireSnapshotAsync(session, cancellationToken).ConfigureAwait(false);
		var before = CliProjections.ResolveMod(snapshot.Nodes, selector);
		var normalizedNotes = string.IsNullOrWhiteSpace(requestedNotes) ? null : requestedNotes.Trim();
		if (string.Equals(before.Metadata.Notes, normalizedNotes, StringComparison.Ordinal))
			return new CliNodeMetadataMutationResult(before, before, Changed: false);

		var updated = before with
		{
			Metadata = before.Metadata with
			{
				Notes = normalizedNotes,
				ModifiedUtc = DateTimeOffset.UtcNow,
			},
		};
		var persisted = await SaveNodeAsync(session, snapshot, updated, cancellationToken).ConfigureAwait(false);
		return new CliNodeMetadataMutationResult(before, persisted, Changed: true);
	}

	public static async ValueTask<CliOptionRelationshipMutationResult> SetOptionEnabledAsync(
		CliLibraryWriteSession session,
		string optionSelector,
		string hostSelector,
		bool enabled,
		CancellationToken cancellationToken = default)
	{
		var snapshot = await RequireSnapshotAsync(session, cancellationToken).ConfigureAwait(false);
		var option = CliProjections.ResolveMod(snapshot.Nodes, optionSelector);
		var host = CliProjections.ResolveMod(snapshot.Nodes, hostSelector);
		var store = await CreateValidatedOptionActivationStoreAsync(session, cancellationToken).ConfigureAwait(false);
		var service = CoreServices.CreateOptionRelationshipWriteService();
		try
		{
			var mutation = await service
				.SetHostScopedOptionEnabledAsync(snapshot, store, host.Id, option.Id, enabled, cancellationToken)
				.ConfigureAwait(false);
			return new CliOptionRelationshipMutationResult(mutation, InitializedStateCount: 0);
		}
		catch (OptionRelationshipMutationException exception)
		{
			throw ToCliUserException(exception);
		}
	}

	public static OptionAllHostsRelationshipSnapshot InspectOptionAllHosts(
		CliLibraryState state,
		string optionSelector)
	{
		ArgumentNullException.ThrowIfNull(state);
		var option = CliProjections.ResolveMod(state.Snapshot.Nodes, optionSelector);
		if (option.Metadata.Kind != ModNodeKind.Option)
		{
			throw new CliUserException(
				"OptionKindInvalid",
				$"Mod “{option.Metadata.Name}”不是选项 Mod，不能查看主体启用范围。 ");
		}
		return CoreServices.CreateOptionRelationshipWriteService()
			.GetOptionAllHostsSnapshot(state.Snapshot, state.OptionActivations, option.Id);
	}

	public static async ValueTask<CliOptionAllHostsMutationResult> SetOptionEnabledForAllHostsAsync(
		CliLibraryWriteSession session,
		string optionSelector,
		bool enabled,
		CancellationToken cancellationToken = default)
	{
		var snapshot = await RequireSnapshotAsync(session, cancellationToken).ConfigureAwait(false);
		var option = CliProjections.ResolveMod(snapshot.Nodes, optionSelector);
		var store = await CreateValidatedOptionActivationStoreAsync(session, cancellationToken).ConfigureAwait(false);
		var service = CoreServices.CreateOptionRelationshipWriteService();
		try
		{
			await session.VerifyReadyForMutationAsync(cancellationToken).ConfigureAwait(false);
			var mutation = await service
				.SetOptionEnabledForAllDirectHostsAsync(snapshot, store, option.Id, enabled, cancellationToken)
				.ConfigureAwait(false);
			return new CliOptionAllHostsMutationResult(option, enabled, mutation);
		}
		catch (OptionRelationshipMutationException exception)
		{
			throw ToCliUserException(exception);
		}
	}

	public static async ValueTask<CliOptionRelationshipMutationResult> MoveOptionsAsync(
		CliLibraryWriteSession session,
		IReadOnlyList<string> optionSelectors,
		string hostSelector,
		int oneBasedInsertionPosition,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(optionSelectors);
		var snapshot = await RequireSnapshotAsync(session, cancellationToken).ConfigureAwait(false);
		var host = CliProjections.ResolveMod(snapshot.Nodes, hostSelector);
		var options = optionSelectors
			.Select(selector => CliProjections.ResolveMod(snapshot.Nodes, selector))
			.ToArray();
		if (options.Length == 0)
			throw new CliUserException("OptionSelectionRequired", "请至少提供一个要移动的选项 Mod。");

		var store = await CreateValidatedOptionActivationStoreAsync(session, cancellationToken).ConfigureAwait(false);
		var service = CoreServices.CreateOptionRelationshipWriteService();
		try
		{
			var before = service.GetHostSnapshot(snapshot, store, host.Id);
			if (oneBasedInsertionPosition > before.DirectOptions.Count + 1)
			{
				throw new CliUserException(
					"OptionPositionOutOfRange",
					$"主体“{host.Metadata.Name}”有 {before.DirectOptions.Count} 个直属选项；--position 的有效范围是 1 到 {before.DirectOptions.Count + 1}。");
			}
			var selectedIds = options.Select(option => option.Id).ToArray();
			if (selectedIds.Distinct().Count() != selectedIds.Length)
				throw new CliUserException("OptionSelectionDuplicate", "同一个选项不能在一次移动中重复提供。");
			var directIds = before.DirectOptions.Select(option => option.OptionId).ToHashSet();
			var unrelated = selectedIds.FirstOrDefault(optionId => !directIds.Contains(optionId));
			if (unrelated != default)
				throw new CliUserException(
					"OptionDirectRelationNotFound",
					$"选项“{CliProjections.FormatId(unrelated)}”不是主体“{host.Metadata.Name}”的直属选项。");

			var initializer = new OptionActivationDefaultInitializer();
			await session.VerifyReadyForMutationAsync(cancellationToken).ConfigureAwait(false);
			var initialized = await initializer
				.InitializeMissingStatesAsync(snapshot, store, [host.Id], enableDefaults: true, cancellationToken: cancellationToken)
				.ConfigureAwait(false);
			var mutation = await service
				.MoveDirectOptionsAsync(snapshot, store, host.Id, selectedIds, oneBasedInsertionPosition - 1, cancellationToken)
				.ConfigureAwait(false);
			return new CliOptionRelationshipMutationResult(mutation, initialized);
		}
		catch (OptionRelationshipMutationException exception)
		{
			throw ToCliUserException(exception);
		}
	}

	private static async ValueTask<LibrarySnapshot> RequireSnapshotAsync(
		CliLibraryWriteSession session,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(session);
		var snapshot = await session.ReloadSnapshotAsync(cancellationToken).ConfigureAwait(false);
		return snapshot ?? throw new CliUserException(
			"LibrarySnapshotMissing",
			"该操作只能修改已有的 Mod 库。请先导入至少一个 Mod，或在管理器中创建库状态。");
	}

	private static SafeModDeletionPlan CreateDeletionPlan(
		LibrarySnapshot snapshot,
		IReadOnlyCollection<ModNodeId> nodeIds,
		SafeModDeletionScope scope,
		SafeModDeletionSafetySnapshot safety)
		=> new SafeModDeletionPlanningService().Plan(snapshot, nodeIds, scope, safety);

	private static SafeModDeletionSafetySnapshot CreateDeletionSafetySnapshot(
		IReadOnlyDictionary<string, IReadOnlySet<string>> decorationActivations)
	{
		ArgumentNullException.ThrowIfNull(decorationActivations);
		var enabled = new List<SafeModDeletionEnabledDecoration>();
		foreach (var (rawDecorationId, rawHostIds) in decorationActivations)
		{
			if (!Guid.TryParse(rawDecorationId, out var decorationGuid)) continue;
			foreach (var rawHostId in rawHostIds)
			{
				if (!Guid.TryParse(rawHostId, out var hostGuid)) continue;
				enabled.Add(new SafeModDeletionEnabledDecoration(new ModNodeId(decorationGuid), new ModNodeId(hostGuid)));
			}
		}
		return new SafeModDeletionSafetySnapshot(
			IsDecorationActivationStateKnown: true,
			EnabledDecorations: enabled,
			IsManagedOverwriteStateKnown: true,
			ManagedOverwrites: Array.Empty<SafeModDeletionManagedOverwrite>());
	}

	private static async ValueTask<CliPhysicalDeletionResult> DeletePlannedStorageAsync(
		StoragePaths paths,
		LibrarySnapshot snapshot,
		SafeModDeletionPlan plan,
		CancellationToken cancellationToken)
	{
		var removalById = plan.LibraryNodeRemovals.ToDictionary(removal => removal.NodeId);
		var plannedIds = removalById.Keys.ToHashSet();
		var graph = OptionRelationshipGraph.Create(snapshot);
		var pending = plan.PhysicalRelativePaths
			.OrderBy(path => path.RelativePath, StringComparer.OrdinalIgnoreCase)
			.ToList();
		var removed = new HashSet<ModNodeId>();
		var failed = new HashSet<ModNodeId>();
		var results = new List<ModStorageDeletionResult>();
		var storage = new ModStorageDeletionService(paths);

		while (pending.Count > 0)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var readyIndex = pending.FindIndex(path => IsReady(path));
			if (readyIndex < 0) break;
			var path = pending[readyIndex];
			pending.RemoveAt(readyIndex);
			var result = await storage.DeleteAsync(path.RelativePath, cancellationToken).ConfigureAwait(false);
			results.Add(result);
			if (result.CanRemoveLibraryNode)
			{
				foreach (var nodeId in path.NodeIds) removed.Add(nodeId);
			}
			else
			{
				foreach (var nodeId in path.NodeIds) failed.Add(nodeId);
			}
		}

		var skipped = pending
			.SelectMany(path => path.NodeIds)
			.Where(nodeId => !removed.Contains(nodeId) && !failed.Contains(nodeId))
			.Distinct()
			.OrderBy(nodeId => nodeId.Value)
			.ToArray();
		return new CliPhysicalDeletionResult(
			removed.OrderBy(nodeId => nodeId.Value).ToArray(),
			failed.OrderBy(nodeId => nodeId.Value).ToArray(),
			skipped,
			results);

		bool IsReady(SafeModDeletionPhysicalPath path)
		{
			foreach (var nodeId in path.NodeIds)
			{
				if (!removalById.TryGetValue(nodeId, out var removal)) continue;
				if (removal.Reason == SafeModDeletionRemovalReason.Requested) return true;
				var plannedParents = graph.GetParents(nodeId).Where(plannedIds.Contains).ToArray();
				if (plannedParents.All(parentId => removed.Contains(parentId) || path.NodeIds.Contains(parentId))) return true;
			}
			return false;
		}
	}

	private static LibrarySnapshot ApplyDeletionPlan(LibrarySnapshot snapshot, SafeModDeletionPlan plan)
	{
		var removalIds = plan.LibraryNodeRemovals.Select(removal => removal.NodeId).ToHashSet();
		if (removalIds.Count == 0) return snapshot;
		var now = DateTimeOffset.UtcNow;
		var relationshipUpdates = ModRelationshipDeletionProjection.GetSurvivingOptionUpdates(snapshot, removalIds, now);
		var nodes = new Dictionary<ModNodeId, ModNode>();
		foreach (var node in snapshot.Nodes.Values.Where(node => !removalIds.Contains(node.Id)))
		{
			var children = node.Children.Where(childId => !removalIds.Contains(childId)).ToArray();
			var updated = relationshipUpdates.TryGetValue(node.Id, out var relationshipUpdated)
				? relationshipUpdated
				: node;
			nodes[node.Id] = updated with { Children = children };
		}

		var profileReady = snapshot with { Nodes = nodes, Profiles = snapshot.Profiles, SavedUtc = now };
		return CoreServices.CreateProfileStructuralMaintenanceService()
			.CleanupAfterMutation(
				snapshot,
				profileReady,
				removalIds.Concat(relationshipUpdates.Keys).Distinct().ToArray(),
				removalIds,
				now)
			.Snapshot;
	}

	private static IReadOnlyList<CliRelationshipSidecarBackup> SynchronizeSurvivingRelationshipStorage(
		StoragePaths paths,
		LibrarySnapshot before,
		LibrarySnapshot after,
		CancellationToken cancellationToken)
	{
		var backups = new List<CliRelationshipSidecarBackup>();
		try
		{
			foreach (var updated in after.Nodes.Values)
			{
				if (!before.Nodes.TryGetValue(updated.Id, out var original)
					|| !ModRelationshipDeletionProjection.RequiresStorageProtocolWrite(original, updated)
					|| !TryResolveExistingManagedDirectory(paths, updated.RelativePath, out var directory))
					continue;
				backups.Add(CaptureRelationshipSidecars(directory));
				ModNodeStorageProtocol.SynchronizeNodeMetadata(directory, updated, cancellationToken);
			}
			return backups;
		}
		catch
		{
			foreach (var backup in backups) RestoreRelationshipSidecars(backup);
			throw;
		}
	}

	private static bool TryResolveExistingManagedDirectory(StoragePaths paths, string relativePath, out string directory)
	{
		directory = string.Empty;
		if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath)) return false;
		try
		{
			var root = Path.GetFullPath(paths.ModsDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
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

	private static CliRelationshipSidecarBackup CaptureRelationshipSidecars(string directory)
		=> new(
			CaptureSidecar(ModNodeStorageProtocol.LogicalMarkerPath(directory)),
			CaptureSidecar(ModNodeStorageProtocol.OptionRelationPath(directory)));

	private static CliSidecarFileBackup CaptureSidecar(string path)
	{
		if (!File.Exists(path)) return new CliSidecarFileBackup(path, false, null, null);
		return new CliSidecarFileBackup(path, true, File.ReadAllBytes(path), File.GetAttributes(path));
	}

	private static void RestoreRelationshipSidecars(CliRelationshipSidecarBackup backup)
	{
		RestoreSidecar(backup.LogicalMarker);
		RestoreSidecar(backup.OptionRelation);
	}

	private static void RestoreSidecar(CliSidecarFileBackup backup)
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

	private static async ValueTask<OptionActivationCleanupResult> CleanupOptionActivationsAsync(
		OptionActivationStore store,
		SafeModDeletionPlan plan,
		CancellationToken cancellationToken)
	{
		var optionIds = plan.ActivationCleanupKeys
			.Where(item => item.Kind == SafeModDeletionActivationCleanupKind.OptionActivationForOption)
			.Select(item => item.PrimaryNodeId.Value.ToString("N"))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray();
		var hostIds = plan.ActivationCleanupKeys
			.Where(item => item.Kind == SafeModDeletionActivationCleanupKind.OptionActivationForHost)
			.Select(item => item.PrimaryNodeId.Value.ToString("N"))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray();
		return await store.CleanupAsync(optionIds, hostIds, cancellationToken).ConfigureAwait(false);
	}

	private static async ValueTask<DecorationActivationCleanupResult> CleanupDecorationActivationsAsync(
		DecorationActivationStore store,
		SafeModDeletionPlan plan,
		CancellationToken cancellationToken)
	{
		var decorationIds = plan.ActivationCleanupKeys
			.Where(item => item.Kind == SafeModDeletionActivationCleanupKind.DecorationActivationForDecoration)
			.Select(item => item.PrimaryNodeId.Value.ToString("N"))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray();
		var hostIds = plan.ActivationCleanupKeys
			.Where(item => item.Kind == SafeModDeletionActivationCleanupKind.DecorationActivationForHost)
			.Select(item => item.PrimaryNodeId.Value.ToString("N"))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray();
		return await store.CleanupAsync(decorationIds, hostIds, cancellationToken).ConfigureAwait(false);
	}

	private static async ValueTask<CliProfileMutationExecutionResult> ApplyProfileMutationForSelectedProfileAsync(
		CliLibraryWriteSession session,
		string profileSelector,
		Func<LibrarySnapshot, Profile, ProfileMutationResult> mutation,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(profileSelector);
		ArgumentNullException.ThrowIfNull(mutation);
		var snapshot = await RequireSnapshotAsync(session, cancellationToken).ConfigureAwait(false);
		var profile = CliProjections.ResolveProfile(snapshot.Profiles, snapshot.ActiveProfileId, profileSelector);
		return await ApplyProfileMutationAsync(session, _ => mutation(snapshot, profile), cancellationToken, snapshot).ConfigureAwait(false);
	}

	private static async ValueTask<CliProfileMutationExecutionResult> ApplyProfileEntriesAsync(
		CliLibraryWriteSession session,
		string profileSelector,
		IReadOnlyList<string> nodeSelectors,
		Func<LibrarySnapshot, IProfileMutationService, Profile, IReadOnlyList<ModNodeId>, ProfileMutationResult> mutation,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(nodeSelectors);
		ArgumentNullException.ThrowIfNull(mutation);
		if (nodeSelectors.Count == 0)
			throw new CliUserException("ProfileEntrySelectionRequired", "请至少提供一个 Mod GUID 或唯一 GUID 前缀。");
		var snapshot = await RequireSnapshotAsync(session, cancellationToken).ConfigureAwait(false);
		var profile = CliProjections.ResolveProfile(snapshot.Profiles, snapshot.ActiveProfileId, profileSelector);
		var nodeIds = nodeSelectors.Select(selector => CliProjections.ResolveMod(snapshot.Nodes, selector).Id).ToArray();
		return await ApplyProfileMutationAsync(
			session,
			_ => mutation(snapshot, CoreServices.CreateProfileMutationService(), profile, nodeIds),
			cancellationToken,
			snapshot).ConfigureAwait(false);
	}

	private static async ValueTask<CliProfileMutationExecutionResult> ApplyProfileMutationAsync(
		CliLibraryWriteSession session,
		Func<LibrarySnapshot, ProfileMutationResult> mutation,
		CancellationToken cancellationToken,
		LibrarySnapshot? knownSnapshot = null)
	{
		ArgumentNullException.ThrowIfNull(session);
		ArgumentNullException.ThrowIfNull(mutation);
		var before = knownSnapshot ?? await RequireSnapshotAsync(session, cancellationToken).ConfigureAwait(false);
		var result = mutation(before);
		if (!result.Succeeded)
			throw ToCliUserException(result);
		var after = result.Changed
			? await session.SaveSnapshotAsync(result.Snapshot, cancellationToken).ConfigureAwait(false)
			: before;
		return new CliProfileMutationExecutionResult(result, before, after);
	}

	private static async ValueTask<ModNode> SaveNodeAsync(
		CliLibraryWriteSession session,
		LibrarySnapshot snapshot,
		ModNode updatedNode,
		CancellationToken cancellationToken)
	{
		var nodes = new Dictionary<ModNodeId, ModNode>(snapshot.Nodes)
		{
			[updatedNode.Id] = updatedNode,
		};
		var persisted = await session.SaveSnapshotAsync(
			snapshot with { Nodes = nodes, SavedUtc = DateTimeOffset.UtcNow },
			cancellationToken).ConfigureAwait(false);
		return persisted.Nodes.TryGetValue(updatedNode.Id, out var stored)
			? stored
			: throw new CliUserException("ModMutationPersistenceFailed", "保存后未能重新定位刚刚修改的 Mod。");
	}

	private static async ValueTask<OptionActivationStore> CreateValidatedOptionActivationStoreAsync(
		CliLibraryWriteSession session,
		CancellationToken cancellationToken)
	{
		await session.VerifyReadyForMutationAsync(cancellationToken).ConfigureAwait(false);
		var path = Path.Combine(session.Paths.ModsDirectory, "option-activations.json");
		try
		{
			_ = await OptionActivationStore.TryLoadReadOnlySnapshotAsync(path, cancellationToken).ConfigureAwait(false);
			return new OptionActivationStore(path);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
		{
			throw new CliUserException("OptionActivationsUnreadable", $"无法安全读取 option-activations.json：{exception.Message}");
		}
	}

	private static async ValueTask<DecorationActivationStore> CreateValidatedDecorationActivationStoreAsync(
		CliLibraryWriteSession session,
		CancellationToken cancellationToken)
	{
		await session.VerifyReadyForMutationAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			_ = await DecorationActivationStore.TryLoadReadOnlySnapshotAsync(
				session.Paths.DecorationActivationsPath,
				cancellationToken).ConfigureAwait(false);
			return CoreServices.CreateDecorationActivationStore(session.Paths);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
		{
			throw new CliUserException("DecorationActivationsUnreadable", $"无法安全读取 decoration-activations.json：{exception.Message}");
		}
	}

	private static CliUserException ToCliUserException(OptionRelationshipMutationException exception)
		=> new($"Option{exception.Failure}", exception.Message);

	private static CliUserException ToCliUserException(ModRelationshipMutationException exception)
		=> new($"ModRelationship{exception.Failure}", exception.Message);

	private static CliUserException ToCliUserException(ProfileMutationResult result)
		=> new(
			$"Profile{result.Failure ?? ProfileMutationFailure.ProfileNotFound}",
			result.Message ?? "配置文件操作被安全规则拒绝。");
}

internal sealed record CliNodeMetadataMutationResult(ModNode Before, ModNode After, bool Changed);

internal sealed record CliOptionRelationshipMutationResult(
	OptionRelationshipMutationSnapshot Mutation,
	int InitializedStateCount);

internal sealed record CliOptionAllHostsMutationResult(
	ModNode Option,
	bool RequestedEnabled,
	OptionAllHostsMutationSnapshot Mutation);

internal sealed record CliProfileMutationExecutionResult(
	ProfileMutationResult Mutation,
	LibrarySnapshot Before,
	LibrarySnapshot After);

internal enum CliModDeletionStatus
{
	Blocked,
	StorageNotRemoved,
	Completed,
	PostCommitIncomplete,
}

internal sealed record CliPhysicalDeletionResult(
	IReadOnlyList<ModNodeId> RemovedNodeIds,
	IReadOnlyList<ModNodeId> FailedNodeIds,
	IReadOnlyList<ModNodeId> SkippedNodeIds,
	IReadOnlyList<ModStorageDeletionResult> StorageResults);

internal sealed record CliModDeletionExecutionResult(
	CliModDeletionStatus Status,
	SafeModDeletionPlan Plan,
	SafeModDeletionPlan? AppliedPlan,
	LibrarySnapshot Snapshot,
	IReadOnlyList<ModNodeId> RemovedNodeIds,
	IReadOnlyList<ModNodeId> FailedNodeIds,
	IReadOnlyList<ModNodeId> SkippedNodeIds,
	IReadOnlyList<ModStorageDeletionResult> StorageResults,
	OptionActivationCleanupResult? OptionActivationCleanup,
	DecorationActivationCleanupResult? DecorationActivationCleanup,
	string? PostCommitError);

internal sealed record CliRelationshipSidecarBackup(
	CliSidecarFileBackup LogicalMarker,
	CliSidecarFileBackup OptionRelation);

internal sealed record CliSidecarFileBackup(
	string Path,
	bool Exists,
	byte[]? Content,
	FileAttributes? Attributes);
