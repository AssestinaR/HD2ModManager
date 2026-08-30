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

	private static CliUserException ToCliUserException(OptionRelationshipMutationException exception)
		=> new($"Option{exception.Failure}", exception.Message);
}

internal sealed record CliNodeMetadataMutationResult(ModNode Before, ModNode After, bool Changed);

internal sealed record CliOptionRelationshipMutationResult(
	OptionRelationshipMutationSnapshot Mutation,
	int InitializedStateCount);
