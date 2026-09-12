using HD2ModCore.Application;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

// Purpose: Runs one serialized deployment lane with a resettable stability window and immediate flush support.
public sealed class ProfileDeploymentCoordinator : IProfileDeploymentCoordinator
{
	private readonly IModLibraryManager _libraryManager;
	private readonly IProfileApplyService _profileApplyService;
	private readonly IApplyExecutor _applyExecutor;
	private readonly StoragePaths _paths;
	private readonly Func<string?> _gameDataDirectoryProvider;
	private readonly IDeploymentDelay _delay;
	private readonly TimeSpan _bufferDuration;
	private readonly Func<LuaDeploymentPermission> _luaPermissionProvider;
	private readonly SemaphoreSlim _lane = new(1, 1);
	private readonly object _sync = new();
	private Task? _worker;
	private CancellationTokenSource? _bufferCancellation;
	private bool _deactivationRequested;
	private bool _deploymentRequestedDuringDeactivation;
	private long _requestedRevision;
	private long _handledRevision;
	private LuaDeploymentPermission _luaPermission = LuaDeploymentPermission.DenyOpaque;
	private DateTimeOffset? _bufferStartedUtc;
	private DateTimeOffset? _bufferEndsUtc;
	private ProfileDeploymentStatus _status = ProfileDeploymentStatus.Idle;

	public ProfileDeploymentCoordinator(
		IModLibraryManager libraryManager,
		IProfileApplyService profileApplyService,
		IApplyExecutor applyExecutor,
		StoragePaths paths,
		Func<string?> gameDataDirectoryProvider,
		IDeploymentDelay? delay = null,
		TimeSpan? bufferDuration = null,
		Func<LuaDeploymentPermission>? luaPermissionProvider = null)
	{
		_libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
		_profileApplyService = profileApplyService ?? throw new ArgumentNullException(nameof(profileApplyService));
		_applyExecutor = applyExecutor ?? throw new ArgumentNullException(nameof(applyExecutor));
		_paths = paths ?? throw new ArgumentNullException(nameof(paths));
		_gameDataDirectoryProvider = gameDataDirectoryProvider ?? throw new ArgumentNullException(nameof(gameDataDirectoryProvider));
		_delay = delay ?? new SystemDeploymentDelay();
		_bufferDuration = bufferDuration ?? TimeSpan.FromSeconds(2);
		_luaPermissionProvider = luaPermissionProvider ?? (() => LuaDeploymentPermission.DenyOpaque);
		if (_bufferDuration < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(bufferDuration));
	}

	public ProfileDeploymentStatus Status { get { lock (_sync) return _status; } }
	public event EventHandler<ProfileDeploymentStatus>? StatusChanged;

	public void NotifyActiveProfileChanged()
	{
		ProfileDeploymentStatus? waitingStatus = null;
		lock (_sync)
		{
			_luaPermission = _luaPermissionProvider();
			_requestedRevision++;
			if (_deactivationRequested)
			{
				_deploymentRequestedDuringDeactivation = true;
				return;
			}
			waitingStatus = ArmStabilityWindowLocked();
			EnsureWorkerLocked();
		}
		if (waitingStatus is not null) SetStatus(waitingStatus);
	}

	public async Task<ProfileDeploymentStatus> FlushAsync(CancellationToken cancellationToken = default, LuaDeploymentPermission? luaPermission = null)
	{
		Task worker;
		lock (_sync)
		{
			if (_deactivationRequested)
			{
				_deploymentRequestedDuringDeactivation = true;
				throw new InvalidOperationException("正在停用活动配置，无法立即部署。");
			}
			_requestedRevision++;
			_luaPermission = luaPermission ?? _luaPermissionProvider();
			CancelStabilityWindowLocked();
			EnsureWorkerLocked();
			worker = _worker!;
		}

		await worker.WaitAsync(cancellationToken).ConfigureAwait(false);
		return Status;
	}

	public async Task DeactivateAsync(CancellationToken cancellationToken = default)
	{
		Task? worker;
		long deactivationRevision;
		lock (_sync)
		{
			_deactivationRequested = true;
			deactivationRevision = ++_requestedRevision;
			CancelStabilityWindowLocked();
			worker = _worker;
		}
		if (worker is not null)
		{
			try { await worker.ConfigureAwait(false); } catch (OperationCanceledException) { }
		}
		await _lane.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			SetStatus(new ProfileDeploymentStatus(ProfileDeploymentStage.Deactivating, null, 0, null, null, "正在清理 Data 中的 Patch。", null));
			var gameData = _gameDataDirectoryProvider();
			if (string.IsNullOrWhiteSpace(gameData))
			{
				SetStatus(new ProfileDeploymentStatus(ProfileDeploymentStage.Failed, null, 0, null, null, "未设置游戏数据目录。", null));
				return;
			}
			var result = await _applyExecutor.DeactivateAsync(gameData, CancellationToken.None).ConfigureAwait(false);
			SetStatus(new ProfileDeploymentStatus(result.Success ? ProfileDeploymentStage.Completed : ProfileDeploymentStage.Failed, null, 0, null, null, result.Success ? "已停用活动配置并清理 Patch。" : "停用活动配置失败。", result));
		}
		finally
		{
			_lane.Release();
			ProfileDeploymentStatus? waitingStatus = null;
			lock (_sync)
			{
				_deactivationRequested = false;
				_handledRevision = Math.Max(_handledRevision, deactivationRevision);
				_worker = null;
				if (_deploymentRequestedDuringDeactivation || _requestedRevision > _handledRevision)
				{
					_deploymentRequestedDuringDeactivation = false;
					waitingStatus = ArmStabilityWindowLocked();
					EnsureWorkerLocked();
				}
			}
			if (waitingStatus is not null) SetStatus(waitingStatus);
		}
	}

	private async Task RunAsync()
	{
		await _lane.WaitAsync().ConfigureAwait(false);
		try
		{
			while (true)
			{
				long targetRevision;
				lock (_sync)
				{
					if (_deactivationRequested || _handledRevision == _requestedRevision) return;
					targetRevision = _requestedRevision;
				}

				await WaitForStableStateAsync(targetRevision).ConfigureAwait(false);
				lock (_sync)
				{
					if (_deactivationRequested) return;
					if (targetRevision != _requestedRevision) continue;
				}

				var snapshot = await _libraryManager.LoadOrCreateAsync().ConfigureAwait(false);
				var profile = snapshot.ActiveProfileId is { } activeId ? snapshot.Profiles.FirstOrDefault(item => item.Id == activeId) : null;
				if (profile is null)
				{
					lock (_sync) _handledRevision = targetRevision;
					SetStatus(ProfileDeploymentStatus.Idle);
					continue;
				}

				lock (_sync)
				{
					if (_deactivationRequested) return;
				}
				var gameData = _gameDataDirectoryProvider();
				if (string.IsNullOrWhiteSpace(gameData))
				{
					lock (_sync) _handledRevision = targetRevision;
					SetStatus(new ProfileDeploymentStatus(ProfileDeploymentStage.Failed, profile.Id, targetRevision, null, null, "未设置游戏数据目录。", null));
					continue;
				}

				// Reload immediately before planning so notifications never capture an obsolete profile snapshot.
				snapshot = await _libraryManager.LoadOrCreateAsync().ConfigureAwait(false);
				profile = snapshot.ActiveProfileId is { } latestActiveId ? snapshot.Profiles.FirstOrDefault(item => item.Id == latestActiveId) : null;
				if (profile is null)
				{
					lock (_sync) _handledRevision = targetRevision;
					SetStatus(ProfileDeploymentStatus.Idle);
					continue;
				}
				SetStatus(new ProfileDeploymentStatus(ProfileDeploymentStage.Deploying, profile.Id, targetRevision, null, null, "正在部署最新活动配置。", null));
				ApplyResult result;
				try
				{
					result = await ApplyWithFreshSnapshotRetryAsync(profile, snapshot, gameData).ConfigureAwait(false);
				}
				catch (Exception exception)
				{
					var issue = new CoreIssue(CoreIssueSeverity.Error, "DeploymentUnhandledException", exception.Message, ExceptionMessage: exception.ToString());
					var failure = new ApplyResult(false, Array.Empty<ApplyOperationResult>(), null, [issue]);
					SetStatus(new ProfileDeploymentStatus(ProfileDeploymentStage.Failed, profile.Id, targetRevision, null, null, FormatFailureMessage("活动配置部署发生未处理异常", failure), failure));
					lock (_sync) _handledRevision = targetRevision;
					continue;
				}
				lock (_sync) _handledRevision = targetRevision;
				SetStatus(new ProfileDeploymentStatus(result.Success ? ProfileDeploymentStage.Completed : ProfileDeploymentStage.Failed, profile.Id, targetRevision, null, null, result.Success ? "活动配置已部署。" : FormatFailureMessage("活动配置部署失败", result), result));
			}
		}
		finally
		{
			_lane.Release();
			lock (_sync)
			{
				if (!_deactivationRequested) _worker = null;
			}
		}
	}

	private async Task<ApplyResult> ApplyWithFreshSnapshotRetryAsync(Profile profile, LibrarySnapshot snapshot, string gameData)
	{
		var result = await _profileApplyService.ApplyAsync(
			profile,
			snapshot,
			_paths.ModsDirectory,
			gameData,
			CancellationToken.None,
			_luaPermission).ConfigureAwait(false);
		if (!IsFileFactsInvalidated(result)) return result;

		// A content commit can invalidate a snapshot-wide FileFacts production
		// while deployment is preparing. Reload both the library snapshot and the
		// active profile before retrying so all derived products use one generation.
		var latest = await _libraryManager.LoadOrCreateAsync().ConfigureAwait(false);
		var latestProfile = latest.ActiveProfileId is { } latestActiveId
			? latest.Profiles.FirstOrDefault(item => item.Id == latestActiveId)
			: null;
		if (latestProfile is null) return result;
		return await _profileApplyService.ApplyAsync(
			latestProfile,
			latest,
			_paths.ModsDirectory,
			gameData,
			CancellationToken.None,
			_luaPermission).ConfigureAwait(false);
	}

	private static bool IsFileFactsInvalidated(ApplyResult result)
		=> result.Issues.Any(issue => string.Equals(issue.Code, "FileFactsInvalidated", StringComparison.Ordinal));

	private async Task WaitForStableStateAsync(long targetRevision)
	{
		while (true)
		{
			DateTimeOffset? endsUtc;
			CancellationToken token;
			lock (_sync)
			{
				if (_deactivationRequested || targetRevision != _requestedRevision) return;
				endsUtc = _bufferEndsUtc;
				token = _bufferCancellation?.Token ?? CancellationToken.None;
			}

			if (endsUtc is null || endsUtc <= _delay.UtcNow) return;
			try
			{
				await _delay.DelayAsync(endsUtc.Value - _delay.UtcNow, token).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				// A later mutation or FlushAsync reset the stability window.
			}
		}
	}

	private ProfileDeploymentStatus ArmStabilityWindowLocked()
	{
		CancelStabilityWindowLocked();
		_bufferStartedUtc = _delay.UtcNow;
		_bufferEndsUtc = _bufferStartedUtc.Value + _bufferDuration;
		_bufferCancellation = new CancellationTokenSource();
		return new ProfileDeploymentStatus(
			ProfileDeploymentStage.WaitingForStableState,
			null,
			_requestedRevision,
			_bufferStartedUtc,
			_bufferEndsUtc,
			"正在等待配置变更稳定。",
			null);
	}

	private void CancelStabilityWindowLocked()
	{
		_bufferCancellation?.Cancel();
		_bufferCancellation?.Dispose();
		_bufferCancellation = null;
		_bufferStartedUtc = null;
		_bufferEndsUtc = null;
	}

	private void EnsureWorkerLocked()
	{
		if (_worker is null || _worker.IsCompleted) _worker = RunAsync();
	}

	private void SetStatus(ProfileDeploymentStatus status)
	{
		lock (_sync) _status = status;
		StatusChanged?.Invoke(this, status);
	}

	private static string FormatFailureMessage(string prefix, ApplyResult result)
	{
		var details = result.Issues
			.Where(issue => issue.Severity == CoreIssueSeverity.Error)
			.Select(issue =>
			{
				var path = string.IsNullOrWhiteSpace(issue.FilePath) ? string.Empty : $" 路径={issue.FilePath}";
				return $"[{issue.Code}] {issue.Message}{path}";
			})
			.Take(5)
			.ToArray();
		return details.Length == 0 ? $"{prefix}，但未返回结构化错误信息。请查看部署日志。" : $"{prefix}：{string.Join("；", details)}";
	}

	public async ValueTask DisposeAsync()
	{
		Task? worker;
		lock (_sync)
		{
			_deactivationRequested = true;
			CancelStabilityWindowLocked();
			worker = _worker;
		}
		if (worker is not null)
		{
			try { await worker.ConfigureAwait(false); } catch (OperationCanceledException) { }
		}
		_bufferCancellation?.Dispose();
		_lane.Dispose();
	}
}
