using System.Text.Json;

namespace HD2ModCore.Updates;

public enum CheckPhase { Idle, Checking, Latest, Available, Error }

/// <param name="Offer">用户主动发起的检查失败、且换镜像可能有用时为真（见 UpdateMirrors）。</param>
public sealed record CheckView(CheckPhase Phase, DateTimeOffset? At = null, AppRelease? Release = null, UpdateErrorCode? Code = null, bool Offer = false, DigestSource? DigestSource = null);

public enum DownloadPhase { None, Downloading, Ready, Installing, Error }

public sealed record DownloadView(DownloadPhase Phase, string? Version = null, long Received = 0, long? Total = null, UpdateErrorCode? Code = null, bool Offer = false, DigestSource? DigestSource = null);

/// <summary>界面用的当前状态快照。</summary>
public sealed record AppUpdateView(
	string Current, bool CanInstall, string? InstallBlockedReason, AppUpdatePrefs Prefs,
	CheckView Check, DownloadView Download, string ReleasesUrl);

public sealed class AppUpdaterOptions
{
	public required string Current { get; init; }
	public required string Repo { get; init; }
	/// <summary>config 目录：偏好与检查记录。</summary>
	public required string ConfigDir { get; init; }
	/// <summary>下载与暂存目录（cache\app-update）。</summary>
	public required string RunDir { get; init; }
	/// <summary>安装变体（complete / requires-dotnet / compat-…）；null 表示开发构建，只提示不安装。</summary>
	public string? Variant { get; init; }
	public IUpdateInstaller? Installer { get; init; }
	public UpdateNetwork? Network { get; init; }
	public FetchFn? Raw { get; init; }
	/// <summary>测试用：Release 列表地址。默认官方 API；其他值必须是本机回环地址。</summary>
	public string? Feed { get; init; }
	public Func<DateTimeOffset>? Now { get; init; }
	public TimeSpan? FirstCheckDelay { get; init; }
	public TimeSpan? CheckInterval { get; init; }
	public DownloadOptions? Download { get; init; }
}

// 作用：管理器自身的更新：启动时、每天一次、按需检查 GitHub Release；有更新就给出版本与更新说明；
// 下载更新包时 GitHub 的 SHA-256 摘要与发布的 SHA256SUMS 必须一致；校验后交给安装器（解压、退出、替换、重启）。
// 开发构建（没有 update-package.json）只提示、给 Release 页面链接。
// Purpose: Updates of the manager itself. Checks this project's GitHub Releases at startup, daily and on request;
// downloads the package (GitHub's digest and SHA256SUMS must agree) and hands it to the installer.
public sealed class AppUpdater : IDisposable
{
	private static readonly TimeSpan DefaultFirstDelay = TimeSpan.FromSeconds(15);
	private static readonly TimeSpan DefaultInterval = TimeSpan.FromHours(24);

	private readonly AppUpdaterOptions _o;
	private readonly UpdateNetwork _network;
	private readonly string _feed;
	private readonly string _official;
	private readonly Func<string, bool> _assetOk;
	private readonly Func<DateTimeOffset> _now;
	private readonly JsonFileStore<AppUpdatePrefs> _prefsStore;
	private readonly UpdateCheckStore _checks;
	private readonly object _gate = new();
	private readonly CancellationTokenSource _lifetime = new();
	private AppUpdatePrefs _prefs;
	private CheckView _check = new(CheckPhase.Idle);
	private DownloadView _download = new(DownloadPhase.None);
	private UpdateCandidate? _candidate;
	private InstallRequest? _ready;
	private DigestSource? _digestSource;
	private CancellationTokenSource? _abort;
	private Task? _checking;
	private Timer? _timer;
	private bool _stopped;

	public event Action? Changed;

	public AppUpdater(AppUpdaterOptions options)
	{
		_o = options;
		_official = $"https://api.github.com/repos/{options.Repo}/releases";
		if (options.Feed is not null && options.Feed != _official && !(Uri.TryCreate(options.Feed, UriKind.Absolute, out var f) && UpdateNetwork.IsLoopback(f)))
			throw new ArgumentException("Update feed must be the project releases API or a loopback test URL");
		_feed = options.Feed ?? _official;
		var prefix = $"https://github.com/{options.Repo}/releases/download/";
		_assetOk = _feed == _official ? url => url.StartsWith(prefix, StringComparison.Ordinal) : url => Uri.TryCreate(url, UriKind.Absolute, out var u) && UpdateNetwork.IsLoopback(u);
		_now = options.Now ?? (() => DateTimeOffset.UtcNow);
		_prefsStore = new JsonFileStore<AppUpdatePrefs>(Path.Combine(options.ConfigDir, "app-update.json"));
		_prefs = (_prefsStore.Load() ?? new AppUpdatePrefs()).Sanitized();
		_network = options.Network ?? new UpdateNetwork(() => _prefs.Network, options.Raw);
		_checks = new UpdateCheckStore(options.ConfigDir);
		RestoreCachedCheck();
	}

	public string RunDir => _o.RunDir;

	private bool Loopback => _feed != _official;

	private TimeSpan Interval => _o.CheckInterval ?? DefaultInterval;

	private void RestoreCachedCheck()
	{
		var cached = _checks.Get();
		if (cached is null) return;
		if (cached.Current == _o.Current && cached.List is not null)
		{
			_digestSource = cached.DigestSource;
			ApplyReleaseList(cached.List, cached.At);
		}
		else if (cached.Error is UpdateErrorCode.Network or UpdateErrorCode.RateLimited or UpdateErrorCode.Http or UpdateErrorCode.BadResponse)
			_check = new CheckView(CheckPhase.Error, cached.At, Code: cached.Error);
	}

	private string? BlockedReason => _o.Installer is null ? "no-installer" : _o.Variant is null ? "dev-build" : null;

	public AppUpdateView View()
	{
		lock (_gate)
		{
			var d = _download.Phase == DownloadPhase.None || _digestSource is null ? _download : _download with { DigestSource = _digestSource };
			return new AppUpdateView(_o.Current, BlockedReason is null, BlockedReason, _prefs, _check, d, $"https://github.com/{_o.Repo}/releases");
		}
	}

	private void Raise()
	{
		try { Changed?.Invoke(); } catch { /* 监听者的错误不能破坏更新 */ }
	}

	/// <summary>清理上次的残留并安排检查。</summary>
	public void Start()
	{
		Directory.CreateDirectory(_o.RunDir);
		ZipUpdateInstaller.CleanupStage(_o.RunDir);
		UpdateDownloader.CleanupParts(_o.RunDir);
		try
		{
			foreach (var f in Directory.EnumerateFiles(_o.RunDir, "*.zip")) File.Delete(f);
		}
		catch (IOException) { }
		catch (UnauthorizedAccessException) { }
		Schedule(Max(_o.FirstCheckDelay ?? DefaultFirstDelay, _checks.Remaining(_now(), Interval)));
	}

	private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

	public AppUpdateView SetPrefs(bool? autoCheck = null, bool? autoUpdate = null, string? skipped = null, bool clearSkipped = false,
		DownloadSourceMode? source = null, string? mirrorId = null, string? customMirror = null)
	{
		lock (_gate)
		{
			var next = _prefs with
			{
				AutoCheck = autoCheck ?? _prefs.AutoCheck,
				AutoUpdate = autoUpdate ?? _prefs.AutoUpdate,
				Skipped = clearSkipped ? null : skipped ?? _prefs.Skipped,
				DownloadSource = source ?? _prefs.DownloadSource,
				MirrorId = mirrorId ?? _prefs.MirrorId,
				CustomMirror = customMirror ?? _prefs.CustomMirror,
			};
			_prefs = next.Sanitized();
			_prefsStore.Save(_prefs);
		}
		Schedule(Max(_o.FirstCheckDelay ?? DefaultFirstDelay, _checks.Remaining(_now(), Interval)));
		Raise();
		return View();
	}

	/// <summary>立即检查。已有检查在跑就加入它；下载或安装中拒绝。</summary>
	public Task CheckAsync(bool manual = false, string? mirror = null)
	{
		lock (_gate)
		{
			if (_stopped) return Task.CompletedTask;
			if (_download.Phase is DownloadPhase.Downloading or DownloadPhase.Installing) throw new UpdateException(UpdateErrorCode.Busy, "Update download in progress");
			_checking ??= RunCheckThenReleaseAsync(manual, mirror);
			return _checking;
		}
	}

	private async Task RunCheckThenReleaseAsync(bool manual, string? mirror)
	{
		try { await Task.Yield(); await RunCheckAsync(manual, UpdateMirrors.ById(mirror, _prefs.CustomMirror)).ConfigureAwait(false); }
		finally
		{
			lock (_gate) _checking = null;
			Schedule(Interval);
		}
	}

	private async Task RunCheckAsync(bool manual, Mirror? mirror)
	{
		var ct = _lifetime.Token;
		lock (_gate) _check = new CheckView(CheckPhase.Checking);
		Raise();
		var attemptedAt = _now();
		try
		{
			_checks.Save(new CheckRecord(attemptedAt, null, null, null, null));
			var fetch = _network.Source(mirror?.Id, Loopback);
			var meta = await fetch.OfficialFirstAsync(f => ReleaseListAsync(f, ct), list =>
			{
				var selected = ReleasePicker.Pick(list, _o.Current, _assetOk, _o.Variant);
				return selected is null || _o.Variant is null || (selected.Package is not null && selected.Sums is not null && ReleaseJson.IsSha256Digest(selected.Package.Digest));
			}).ConfigureAwait(false);
			if (_stopped) return;
			lock (_gate) _digestSource = meta.Source;
			ApplyReleaseList(meta.Value, _now());
			var c = _candidate;
			// 只缓存被选中的那一个 Release：说明有上限，资产只留相关的两个。
			var selectedList = c is null ? new List<GithubRelease>() : new List<GithubRelease>
			{
				new($"v{c.Release.Version}", c.Release.Name, c.Release.Notes, false, c.Release.Prerelease, c.Release.Url, c.Release.PublishedAt,
					new[] { c.Package, c.Sums }.Where(a => a is not null).Select(a => a!).ToList()),
			};
			_checks.Save(new CheckRecord(attemptedAt, _o.Current, selectedList, null, _digestSource));
		}
		catch (Exception e) when (e is not OutOfMemoryException)
		{
			if (_stopped) return;
			var code = e is UpdateException ue ? ue.Code : e is OperationCanceledException ? UpdateErrorCode.Cancelled : UpdateErrorCode.BadResponse;
			var offer = manual && _network.Settings.Mode == DownloadSourceMode.Ask && UpdateErrors.MirrorHelps(code);
			lock (_gate) _check = new CheckView(CheckPhase.Error, _now(), Code: code, Offer: offer);
			_checks.Save(new CheckRecord(attemptedAt, null, null, code, null));
		}
		Raise();
		await AutoUpdateIfWantedAsync().ConfigureAwait(false);
	}

	private async Task AutoUpdateIfWantedAsync()
	{
		UpdateCandidate? c;
		AppUpdatePrefs prefs;
		lock (_gate) { c = _candidate; prefs = _prefs; }
		if (_stopped || c is null || _o.Installer is null || _o.Variant is null || !prefs.AutoUpdate || prefs.Skipped == c.Release.Version || _check.Phase != CheckPhase.Available) return;
		try
		{
			await DownloadAsync().ConfigureAwait(false);
			// 自动安装只在程序空闲时进行；否则停在「已下载」，等用户手动安装。
			if (!_stopped && _prefs.AutoUpdate && _prefs.Skipped != c.Release.Version && _o.Installer.IsIdle) await InstallAsync(automatic: true).ConfigureAwait(false);
		}
		catch (UpdateException) { /* 下载/安装失败记录在下载状态里 */ }
	}

	/// <summary>Release 列表：先 API；被限流或出错时用 github.com 页面（仅官方源）。</summary>
	private async Task<List<GithubRelease>> ReleaseListAsync(FetchFn fetch, CancellationToken ct)
	{
		try
		{
			var json = await UpdateHttp.GetTextAsync(fetch, $"{_feed}?per_page=30", ct).ConfigureAwait(false);
			var releases = ReleaseJson.ParseApiList(json);
			var selected = ReleasePicker.Pick(releases, _o.Current, _assetOk, _o.Variant);
			if (selected?.Package is not null && !ReleaseJson.IsSha256Digest(selected.Package.Digest))
			{
				var i = releases.FindIndex(r => AppVersion.FromTag(r.TagName) == selected.Release.Version);
				if (i >= 0) releases[i] = releases[i] with { Assets = await GithubFeed.FillAssetDigestsAsync(fetch, _o.Repo, releases[i].TagName, releases[i].Assets, ct).ConfigureAwait(false) };
			}
			return releases;
		}
		catch (Exception e) when (e is not OperationCanceledException && _feed == _official
			&& (GithubFeed.ApiFallbackCode(e) || (e is UpdateException { Code: UpdateErrorCode.Network } && _network.Source().CanFallback)))
		{
			var list = await GithubFeed.FetchReleasesAtomAsync(fetch, _o.Repo, ct).ConfigureAwait(false);
			var found = ReleasePicker.Pick(list, _o.Current, _ => true, null);
			var i = found is null ? -1 : list.FindIndex(r => AppVersion.FromTag(r.TagName) == found.Release.Version);
			if (i >= 0) list[i] = list[i] with { Assets = await GithubFeed.FetchExpandedAssetsAsync(fetch, _o.Repo, list[i].TagName, ct).ConfigureAwait(false) };
			return list;
		}
	}

	private void ApplyReleaseList(IReadOnlyList<GithubRelease> list, DateTimeOffset at)
	{
		var found = ReleasePicker.Pick(list, _o.Current, _assetOk, _o.Variant);
		lock (_gate)
		{
			// 版本变了：已下载的文件不再是页面提供的那个。
			if (found?.Release.Version != _candidate?.Release.Version && _download.Phase != DownloadPhase.None) DropDownload();
			_candidate = found;
			_check = found is null
				? new CheckView(CheckPhase.Latest, at)
				: new CheckView(CheckPhase.Available, at, found.Release, DigestSource: _digestSource);
		}
	}

	private UpdateCandidate Downloadable()
	{
		if (BlockedReason is not null) throw new UpdateException(UpdateErrorCode.NotInstallable, "This build cannot install updates");
		var c = _candidate ?? throw new UpdateException(UpdateErrorCode.NothingNew, "No newer version known");
		if (_download.Phase is DownloadPhase.Downloading or DownloadPhase.Installing) throw new UpdateException(UpdateErrorCode.Busy, "Download in progress");
		return c;
	}

	/// <summary>后台开始下载（界面从状态跟踪进度）；不能开始时立刻抛出。</summary>
	public void StartDownload(string? mirror = null)
	{
		UpdateCandidate c;
		CancellationTokenSource abort;
		// 检查与占位必须在同一个临界区里：否则两次点击都能通过检查，各自启动一个下载。
		lock (_gate) { c = Downloadable(); abort = ClaimDownload(c); }
		Raise();
		_ = Task.Run(async () =>
		{
			try { await RunDownloadAsync(c, abort, mirror, manual: true).ConfigureAwait(false); }
			catch (UpdateException) { /* 已记录在下载状态 */ }
		});
	}

	/// <summary>下载并校验更新包；完成时返回。</summary>
	public async Task DownloadAsync(string? mirror = null, bool manual = false)
	{
		UpdateCandidate c;
		CancellationTokenSource abort;
		lock (_gate)
		{
			c = Downloadable();
			if (_download.Phase == DownloadPhase.Ready && _download.Version == c.Release.Version) return;
			abort = ClaimDownload(c);
		}
		Raise();
		await RunDownloadAsync(c, abort, mirror, manual).ConfigureAwait(false);
	}

	/// <summary>在锁内把状态原子地置为「下载中」，并创建这次下载的取消源。</summary>
	private CancellationTokenSource ClaimDownload(UpdateCandidate c)
	{
		var abort = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
		_abort = abort;
		_download = new DownloadView(DownloadPhase.Downloading, c.Release.Version);
		return abort;
	}

	private async Task RunDownloadAsync(UpdateCandidate c, CancellationTokenSource abort, string? mirror, bool manual)
	{
		var fetch = _network.Source(UpdateMirrors.ById(mirror, _prefs.CustomMirror)?.Id, Loopback);
		UpdateException Fail(UpdateErrorCode code)
		{
			var offer = manual && _network.Settings.Mode == DownloadSourceMode.Ask && UpdateErrors.MirrorHelps(code);
			lock (_gate) _download = new DownloadView(DownloadPhase.Error, c.Release.Version, Code: code, Offer: offer);
			Raise();
			return new UpdateException(code, $"Update download failed: {code}");
		}
		var last = DateTimeOffset.MinValue;
		try
		{
			if (c.Package is null || c.Sums is null) throw new UpdateException(UpdateErrorCode.NoPackage, "No package for this installation variant");
			if (!ReleaseJson.IsSha256Digest(c.Package.Digest)) throw new UpdateException(UpdateErrorCode.NoDigest, "Package has no SHA-256 digest");
			var file = Path.Combine(_o.RunDir, c.Package.Name);
			Directory.CreateDirectory(_o.RunDir);
			UpdateDownloader.CleanupParts(_o.RunDir);
			var sumMeta = await fetch.OfficialFirstAsync(f => UpdateHttp.GetTextAsync(f, c.Sums.Url, abort.Token)).ConfigureAwait(false);
			lock (_gate) _digestSource = _digestSource == DigestSource.Mirror || sumMeta.Source == DigestSource.Mirror ? DigestSource.Mirror : DigestSource.Github;
			var listed = ChecksumFile.SumFor(sumMeta.Value, c.Package.Name) ?? throw new UpdateException(UpdateErrorCode.NoDigest, "Package missing from SHA256SUMS");
			if ($"sha256:{listed}" != c.Package.Digest!.ToLowerInvariant()) throw new UpdateException(UpdateErrorCode.DigestMismatch, "SHA256SUMS and asset digest differ");
			var baseOptions = _o.Download ?? new DownloadOptions();
			await UpdateDownloader.DownloadAsync(fetch.Fetch, c.Package, file, abort.Token, new DownloadOptions
			{
				RetryDelays = baseOptions.RetryDelays,
				StallTimeout = baseOptions.StallTimeout,
				FreeBytes = baseOptions.FreeBytes ?? FreeBytesOf,
				SourceFailed = fetch.Failed,
				OnProgress = (received, total) =>
				{
					lock (_gate)
					{
						if (_download.Phase != DownloadPhase.Downloading) return;
						_download = _download with { Received = received, Total = total };
					}
					// 最多每 250 ms 通知一次：界面显示的是进度条，不是每个数据块。
					var t = _now();
					if (t - last >= TimeSpan.FromMilliseconds(250)) { last = t; Raise(); }
				},
			}).ConfigureAwait(false);
			abort.Token.ThrowIfCancellationRequested();
			lock (_gate)
			{
				_ready = new InstallRequest(file, listed, c.Release.Version);
				_download = new DownloadView(DownloadPhase.Ready, c.Release.Version);
			}
			Raise();
		}
		catch (Exception e) when (e is not OutOfMemoryException)
		{
			if (_stopped) throw new UpdateException(UpdateErrorCode.Cancelled, "Stopped");
			var code = abort.IsCancellationRequested ? UpdateErrorCode.Cancelled : e is UpdateException ue ? ue.Code : UpdateErrorCode.Network;
			throw Fail(code);
		}
		finally
		{
			lock (_gate) { if (_abort == abort) _abort = null; }
			abort.Dispose();
		}
	}

	private static long? FreeBytesOf(string dir)
	{
		try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dir))!).AvailableFreeSpace; } catch (Exception e) when (e is IOException or ArgumentException) { return null; }
	}

	public void Cancel()
	{
		CancellationTokenSource? a;
		lock (_gate) a = _abort;
		try { a?.Cancel(); } catch (ObjectDisposedException) { }
	}

	/// <summary>再校验一遍已下载的包并交给安装器；安装器随后让程序退出。</summary>
	public async Task InstallAsync(bool automatic = false)
	{
		var installer = _o.Installer;
		if (installer is null || _o.Variant is null) throw new UpdateException(UpdateErrorCode.NotInstallable, "This build cannot install updates");
		InstallRequest r;
		lock (_gate)
		{
			if (_ready is null || _download.Phase != DownloadPhase.Ready) throw new UpdateException(UpdateErrorCode.NotReady, "Package not downloaded");
			r = _ready;
			// 校验一个大 zip 要一会儿：状态先原子地置为「安装中」，期间的重复点击会得到 NotReady，不会并发安装。
			_download = new DownloadView(DownloadPhase.Installing, r.Version);
		}
		Raise();
		string actual;
		try { actual = await UpdateDownloader.Sha256FileAsync(r.File).ConfigureAwait(false); }
		catch (Exception e) when (e is IOException or UnauthorizedAccessException) { actual = string.Empty; } // 读不了包：按「包变了」处理，走下面的摘要不符分支
		if (_stopped || (automatic && (!_prefs.AutoUpdate || _prefs.Skipped == r.Version)))
		{
			lock (_gate) { if (_download.Phase == DownloadPhase.Installing) _download = new DownloadView(DownloadPhase.Ready, r.Version); }
			Raise();
			return;
		}
		if (actual != r.Sha256)
		{
			lock (_gate)
			{
				DropDownload();
				_download = new DownloadView(DownloadPhase.Error, r.Version, Code: UpdateErrorCode.DigestMismatch);
			}
			Raise();
			throw new UpdateException(UpdateErrorCode.DigestMismatch, "Package changed after download");
		}
		try { await Task.Run(() => installer.Install(r)).ConfigureAwait(false); }
		catch (UpdateException e)
		{
			// 包本身有问题（内容不符、开发构建）重试没有意义；没能关闭程序、磁盘不够这类情况，包还在，可以直接再试。
			var retryable = e.Code is UpdateErrorCode.Failed or UpdateErrorCode.DiskSpace;
			lock (_gate) _download = retryable ? new DownloadView(DownloadPhase.Ready, r.Version) : new DownloadView(DownloadPhase.Error, r.Version, Code: e.Code);
			Raise();
			throw;
		}
		catch (Exception e)
		{
			lock (_gate) _download = new DownloadView(DownloadPhase.Ready, r.Version);
			Raise();
			throw new UpdateException(UpdateErrorCode.Failed, "Installer hand-off failed", e);
		}
	}

	private void DropDownload()
	{
		try { _abort?.Cancel(); } catch (ObjectDisposedException) { }
		_ready = null;
		_download = new DownloadView(DownloadPhase.None);
		try { foreach (var f in Directory.EnumerateFiles(_o.RunDir, "*.zip*")) File.Delete(f); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
	}

	private void Schedule(TimeSpan delay)
	{
		lock (_gate)
		{
			_timer?.Dispose();
			_timer = null;
			if (_stopped || !_prefs.AutoCheck) return;
			_timer = new Timer(_ =>
			{
				var remaining = _checks.Remaining(_now(), Interval);
				if (remaining > TimeSpan.Zero) { Schedule(remaining); return; }
				_ = CheckAsync().ContinueWith(t => { if (t.IsFaulted) Schedule(Interval); }, TaskScheduler.Default); // 忙：下个周期再试
			}, null, delay, Timeout.InfiniteTimeSpan);
		}
	}

	public void Dispose()
	{
		lock (_gate)
		{
			_stopped = true;
			_timer?.Dispose();
			_timer = null;
		}
		try { _lifetime.Cancel(); } catch (ObjectDisposedException) { }
	}
}
