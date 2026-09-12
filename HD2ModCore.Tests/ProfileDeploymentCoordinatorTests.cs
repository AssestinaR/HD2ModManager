using HD2ModCore.Application;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

// Purpose: Verifies immediate deployment is coalesced and serialized without buffering.
public sealed class ProfileDeploymentCoordinatorTests
{
	[Fact]
	public async Task NotifyActiveProfileChanged_DeploysImmediately()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2-deployment-coordinator-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			var profile = new Profile(ProfileId.New(), "Active", DateTimeOffset.UtcNow, null, Array.Empty<ProfileEntry>(), Revision: 1);
			var library = new FakeLibraryManager(new LibrarySnapshot(1, DateTimeOffset.UtcNow, new Dictionary<ModNodeId, ModNode>(), [profile], profile.Id));
			var apply = new RecordingApplyService();
			var executor = new RecordingExecutor();
			await using var coordinator = new ProfileDeploymentCoordinator(library, apply, executor, new StoragePaths(root), () => root, bufferDuration: TimeSpan.Zero);

			coordinator.NotifyActiveProfileChanged();
			await apply.WaitForCallAsync();

			Assert.Equal(1, apply.CallCount);
			Assert.Equal(1, apply.LastProfile!.Revision);
			Assert.Equal(0, executor.DeactivateCount);
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}

	[Fact]
	public async Task NotifyActiveProfileChanged_UsesCurrentLuaPermission()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2-deployment-coordinator-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			var profile = new Profile(ProfileId.New(), "Active", DateTimeOffset.UtcNow, null, Array.Empty<ProfileEntry>(), Revision: 1);
			var library = new FakeLibraryManager(new LibrarySnapshot(1, DateTimeOffset.UtcNow, new Dictionary<ModNodeId, ModNode>(), [profile], profile.Id));
			var apply = new RecordingApplyService();
			var executor = new RecordingExecutor();
			await using var coordinator = new ProfileDeploymentCoordinator(
				library,
				apply,
				executor,
				new StoragePaths(root),
				() => root,
				bufferDuration: TimeSpan.Zero,
				luaPermissionProvider: () => LuaDeploymentPermission.AllowOpaque);

			coordinator.NotifyActiveProfileChanged();
			await apply.WaitForCallAsync();

			Assert.Equal(LuaDeploymentPermission.AllowOpaque, apply.LastLuaPermission);
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}

	[Fact]
	public async Task FlushAsync_SkipsPendingStabilityWindow_AndWaitsForDeployment()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2-deployment-coordinator-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			var profile = new Profile(ProfileId.New(), "Active", DateTimeOffset.UtcNow, null, Array.Empty<ProfileEntry>(), Revision: 2);
			var library = new FakeLibraryManager(new LibrarySnapshot(1, DateTimeOffset.UtcNow, new Dictionary<ModNodeId, ModNode>(), [profile], profile.Id));
			var apply = new RecordingApplyService();
			var executor = new RecordingExecutor();
			await using var coordinator = new ProfileDeploymentCoordinator(library, apply, executor, new StoragePaths(root), () => root, bufferDuration: TimeSpan.FromMinutes(1));

			coordinator.NotifyActiveProfileChanged();
			Assert.Equal(ProfileDeploymentStage.WaitingForStableState, coordinator.Status.Stage);
			var status = await coordinator.FlushAsync();

			Assert.Equal(ProfileDeploymentStage.Completed, status.Stage);
			Assert.Equal(1, apply.CallCount);
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}

	[Fact]
	public async Task NotifyActiveProfileChanged_ResetsStabilityWindow_AndDeploysOnlyOnce()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2-deployment-coordinator-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			var profile = new Profile(ProfileId.New(), "Active", DateTimeOffset.UtcNow, null, Array.Empty<ProfileEntry>());
			var library = new FakeLibraryManager(new LibrarySnapshot(1, DateTimeOffset.UtcNow, new Dictionary<ModNodeId, ModNode>(), [profile], profile.Id));
			var apply = new RecordingApplyService();
			var executor = new RecordingExecutor();
			await using var coordinator = new ProfileDeploymentCoordinator(library, apply, executor, new StoragePaths(root), () => root, bufferDuration: TimeSpan.FromMilliseconds(250));

			coordinator.NotifyActiveProfileChanged();
			await Task.Delay(125);
			coordinator.NotifyActiveProfileChanged();
			await Task.Delay(175);

			Assert.Equal(0, apply.CallCount);
			await apply.WaitForCallAsync();
			Assert.Equal(1, apply.CallCount);
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}

	[Fact]
	public async Task NotifyActiveProfileChanged_DuringDeactivation_RearmsDeploymentAfterDeactivation()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2-deployment-coordinator-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			var profile = new Profile(ProfileId.New(), "Active", DateTimeOffset.UtcNow, null, Array.Empty<ProfileEntry>());
			var library = new FakeLibraryManager(new LibrarySnapshot(1, DateTimeOffset.UtcNow, new Dictionary<ModNodeId, ModNode>(), [profile], profile.Id));
			var apply = new RecordingApplyService();
			var executor = new BlockingDeactivateExecutor();
			await using var coordinator = new ProfileDeploymentCoordinator(library, apply, executor, new StoragePaths(root), () => root, bufferDuration: TimeSpan.Zero);

			var deactivation = coordinator.DeactivateAsync();
			await executor.WaitForDeactivateAsync();
			coordinator.NotifyActiveProfileChanged();
			executor.CompleteDeactivate();
			await deactivation;
			await apply.WaitForCallAsync();

			Assert.Equal(1, apply.CallCount);
			Assert.Equal(1, executor.DeactivateCount);
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}

	[Fact]
	public async Task DeactivateAsync_CancelsPendingBufferAndUsesOnlySerializedDeactivate()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2-deployment-coordinator-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			var profile = new Profile(ProfileId.New(), "Active", DateTimeOffset.UtcNow, null, Array.Empty<ProfileEntry>());
			var library = new FakeLibraryManager(new LibrarySnapshot(1, DateTimeOffset.UtcNow, new Dictionary<ModNodeId, ModNode>(), [profile], profile.Id));
			var apply = new RecordingApplyService();
			var executor = new RecordingExecutor();
			await using var coordinator = new ProfileDeploymentCoordinator(library, apply, executor, new StoragePaths(root), () => root);
			await coordinator.DeactivateAsync();

			Assert.Equal(0, apply.CallCount);
			Assert.Equal(1, executor.DeactivateCount);
			Assert.Equal(ProfileDeploymentStage.Completed, coordinator.Status.Stage);
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}

	private sealed class RecordingApplyService : IProfileApplyService
	{
		private readonly TaskCompletionSource _called = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public int CallCount { get; private set; }
		public Profile? LastProfile { get; private set; }
		public LuaDeploymentPermission LastLuaPermission { get; private set; }
		public ValueTask<ApplyResult> ApplyAsync(Profile profile, LibrarySnapshot snapshot, string modsRootDirectory, string gameDataDirectory, CancellationToken cancellationToken = default, LuaDeploymentPermission luaPermission = LuaDeploymentPermission.DenyOpaque)
		{
			CallCount++;
			LastProfile = profile;
			LastLuaPermission = luaPermission;
			_called.TrySetResult();
			return ValueTask.FromResult(new ApplyResult(true, [], null, []));
		}
		public Task WaitForCallAsync() => _called.Task;
	}

	private sealed class RecordingExecutor : IApplyExecutor
	{
		public int DeactivateCount { get; private set; }
		public ValueTask<ApplyResult> ExecuteAsync(ApplyPlan plan, CancellationToken cancellationToken = default) => ValueTask.FromResult(new ApplyResult(true, [], null, []));
		public ValueTask<ApplyResult> DeactivateAsync(string gameDataDirectory, CancellationToken cancellationToken = default)
		{
			DeactivateCount++;
			return ValueTask.FromResult(new ApplyResult(true, [], null, []));
		}
	}

	private sealed class BlockingDeactivateExecutor : IApplyExecutor
	{
		private readonly TaskCompletionSource _deactivateCalled = new(TaskCreationOptions.RunContinuationsAsynchronously);
		private readonly TaskCompletionSource _completeDeactivate = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public int DeactivateCount { get; private set; }
		public ValueTask<ApplyResult> ExecuteAsync(ApplyPlan plan, CancellationToken cancellationToken = default) => ValueTask.FromResult(new ApplyResult(true, [], null, []));
		public async ValueTask<ApplyResult> DeactivateAsync(string gameDataDirectory, CancellationToken cancellationToken = default)
		{
			DeactivateCount++;
			_deactivateCalled.TrySetResult();
			await _completeDeactivate.Task.WaitAsync(cancellationToken);
			return new ApplyResult(true, [], null, []);
		}
		public Task WaitForDeactivateAsync() => _deactivateCalled.Task;
		public void CompleteDeactivate() => _completeDeactivate.TrySetResult();
	}

	private sealed class FakeLibraryManager : IModLibraryManager
	{
		public LibrarySnapshot Snapshot;
		public FakeLibraryManager(LibrarySnapshot snapshot) => Snapshot = snapshot;
		public ValueTask<LibrarySnapshot> LoadOrCreateAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(Snapshot);
		public ValueTask<LibrarySnapshot> UpsertNodeAsync(ModNode node, CancellationToken cancellationToken = default) => ValueTask.FromResult(Snapshot);
		public ValueTask<LibrarySnapshot> DeleteNodeAsync(ModNodeId nodeId, bool deleteStoredFiles, CancellationToken cancellationToken = default) => ValueTask.FromResult(Snapshot);
		public ValueTask<LibrarySnapshot> DeleteNodesAsync(IReadOnlyList<ModNodeId> nodeIds, bool deleteStoredFiles, CancellationToken cancellationToken = default) => ValueTask.FromResult(Snapshot);
		public ValueTask<ModLibraryDeletionResult> DeleteNodesWithResultAsync(IReadOnlyList<ModNodeId> nodeIds, bool deleteStoredFiles, CancellationToken cancellationToken = default)
			=> ValueTask.FromResult(new ModLibraryDeletionResult(Snapshot, Array.Empty<ModNodeId>(), Array.Empty<ModNodeId>(), Array.Empty<ModStorageDeletionResult>()));
		public ValueTask<ModStoragePendingCleanupResult> RecoverPendingStoredDeletionsAsync(CancellationToken cancellationToken = default)
			=> ValueTask.FromResult(new ModStoragePendingCleanupResult(0, 0, Array.Empty<string>()));
		public ValueTask<IReadOnlySet<string>> GetPendingStoredDeletionSynchronizationExclusionsAsync(CancellationToken cancellationToken = default)
			=> ValueTask.FromResult<IReadOnlySet<string>>(new HashSet<string>(StringComparer.OrdinalIgnoreCase));
		public ValueTask<LibrarySnapshot> UpsertProfileAsync(Profile profile, CancellationToken cancellationToken = default) => ValueTask.FromResult(Snapshot);
		public ValueTask<LibrarySnapshot> DeleteProfileAsync(ProfileId profileId, CancellationToken cancellationToken = default) => ValueTask.FromResult(Snapshot);
		public ValueTask<LibrarySnapshot> RenameProfileAsync(ProfileId profileId, string newName, CancellationToken cancellationToken = default) => ValueTask.FromResult(Snapshot);
		public ValueTask<LibrarySnapshot> SetActiveProfileAsync(ProfileId? profileId, CancellationToken cancellationToken = default) => ValueTask.FromResult(Snapshot);
		public ValueTask<LibrarySnapshot> AddProfileEntryAsync(ProfileId profileId, ModNodeId nodeId, CancellationToken cancellationToken = default) => ValueTask.FromResult(Snapshot);
		public ValueTask<LibrarySnapshot> AddProfileEntriesAsync(ProfileId profileId, IReadOnlyList<ModNodeId> nodeIds, CancellationToken cancellationToken = default) => ValueTask.FromResult(Snapshot);
		public ValueTask<LibrarySnapshot> RemoveProfileEntryAsync(ProfileId profileId, ModNodeId nodeId, CancellationToken cancellationToken = default) => ValueTask.FromResult(Snapshot);
		public ValueTask<LibrarySnapshot> RemoveProfileEntriesAsync(ProfileId profileId, IReadOnlyList<ModNodeId> nodeIds, CancellationToken cancellationToken = default) => ValueTask.FromResult(Snapshot);
		public ValueTask<LibrarySnapshot> MoveProfileEntryAsync(ProfileId profileId, ModNodeId nodeId, int direction, CancellationToken cancellationToken = default) => ValueTask.FromResult(Snapshot);
		public ValueTask<LibrarySnapshot> UpdateNodeMetadataAsync(ModNodeId nodeId, ModNodeMetadata metadata, CancellationToken cancellationToken = default) => ValueTask.FromResult(Snapshot);
	}
}
