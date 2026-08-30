using HD2ModCore.Application;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCli;

/// <summary>
/// Owns the Core cross-process library lease for one CLI mutation. Future
/// mod/profile/deployment commands should obtain this session instead of
/// opening a JSON store and writing it directly.
/// </summary>
internal sealed class CliLibraryWriteSession : IAsyncDisposable
{
	private readonly CliWorkspace _workspace;
	private readonly JsonModLibraryStore _store;
	private readonly CliWriteTargetMode _mode;
	private ILibraryWriteLease? _lease;

	internal CliLibraryWriteSession(
		CliWorkspace workspace,
		CliWriteTarget target,
		CliWriteTargetMode mode,
		StoragePaths paths,
		JsonModLibraryStore store,
		LibrarySnapshot? snapshot,
		ILibraryWriteLease lease)
	{
		_workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
		Target = target ?? throw new ArgumentNullException(nameof(target));
		_mode = mode;
		Paths = paths ?? throw new ArgumentNullException(nameof(paths));
		_store = store ?? throw new ArgumentNullException(nameof(store));
		Snapshot = snapshot;
		_lease = lease ?? throw new ArgumentNullException(nameof(lease));
	}

	public CliWriteTarget Target { get; }
	public CliWriteTargetMode Mode => _mode;
	public StoragePaths Paths { get; }
	public LibrarySnapshot? Snapshot { get; private set; }

	/// <summary>
	/// Revalidates the desktop-process and target-file invariants, then refreshes
	/// the strict snapshot while the write lease remains held.
	/// </summary>
	public async ValueTask<LibrarySnapshot?> ReloadSnapshotAsync(CancellationToken cancellationToken = default)
	{
		var validation = await ValidateCurrentTargetAsync(cancellationToken).ConfigureAwait(false);
		Snapshot = validation.Snapshot!;
		return Snapshot;
	}

	/// <summary>
	/// Checks that the session still targets the same Manager/library pair and
	/// that no desktop Manager instance appeared before an auxiliary mutation.
	/// It is intended for operations that write files alongside library.json.
	/// </summary>
	public async ValueTask VerifyReadyForMutationAsync(CancellationToken cancellationToken = default)
	{
		_ = await ValidateCurrentTargetAsync(cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Persists a library snapshot only after the same strict target check used
	/// at session acquisition. It then reloads the normalized persisted state.
	/// </summary>
	public async ValueTask<LibrarySnapshot> SaveSnapshotAsync(
		LibrarySnapshot snapshot,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		await VerifyReadyForMutationAsync(cancellationToken).ConfigureAwait(false);
		ThrowIfDisposed();
		await _store.SaveAsync(snapshot, cancellationToken).ConfigureAwait(false);
		var persisted = await ReloadSnapshotAsync(cancellationToken).ConfigureAwait(false);
		return persisted ?? throw new CliUserException(
			"LibrarySnapshotUnreadable",
			"保存后未能重新读取 library.json；CLI 未继续执行后续写入。");
	}

	/// <summary>
	/// Persists only library.json under the same lease and target checks as a
	/// normal mutation. It exists for reviewed filesystem reconciliation, where
	/// rewriting profiles.json would be an unrelated side effect.
	/// </summary>
	public async ValueTask<LibrarySnapshot> SaveNodesOnlyAsync(
		LibrarySnapshot snapshot,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		await VerifyReadyForMutationAsync(cancellationToken).ConfigureAwait(false);
		ThrowIfDisposed();
		await _store.SaveNodesOnlyAsync(snapshot, cancellationToken).ConfigureAwait(false);
		var persisted = await ReloadSnapshotAsync(cancellationToken).ConfigureAwait(false);
		return persisted ?? throw new CliUserException(
			"LibrarySnapshotUnreadable",
			"保存后未能重新读取 library.json；CLI 未继续执行后续写入。");
	}

	public async ValueTask DisposeAsync()
	{
		var lease = Interlocked.Exchange(ref _lease, null);
		if (lease is not null)
			await lease.DisposeAsync().ConfigureAwait(false);
	}

	private async ValueTask<CliWriteTargetValidation> ValidateCurrentTargetAsync(CancellationToken cancellationToken)
	{
		ThrowIfDisposed();
		var validation = await CliWriteTargetGuard.ValidateAsync(_workspace, _mode, cancellationToken).ConfigureAwait(false);
		validation.ThrowIfInvalid();
		if (!CliWriteTargetGuard.TargetsMatch(Target, validation.Target))
		{
			throw new CliUserException(
				"WriteTargetChanged",
				"当前写入会话期间，管理器或 Mod 库目标已变化；请结束会话并重新执行命令。");
		}
		return validation;
	}

	private void ThrowIfDisposed()
	{
		if (Volatile.Read(ref _lease) is null)
			throw new ObjectDisposedException(nameof(CliLibraryWriteSession));
	}
}
