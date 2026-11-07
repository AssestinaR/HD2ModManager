using System.Diagnostics;
using System.Net;

namespace HD2ModCore.Updates;

public enum DownloadSourceMode { Ask, Auto, Mirror }

/// <summary>下载来源设置：ask=默认直连，用户主动操作失败后询问是否换镜像；auto=直连失败自动换镜像；mirror=优先走所选镜像。</summary>
public sealed record UpdateNetworkSettings(DownloadSourceMode Mode, string MirrorId, string CustomMirror)
{
	public static readonly UpdateNetworkSettings Default = new(DownloadSourceMode.Ask, string.Empty, string.Empty);
}

// 作用：更新相关的出站请求策略（直连 / 镜像 / 自动回退）与镜像测速排序。只用于管理流量。
// Purpose: Outbound policy for update traffic: direct, mirror, or automatic fallback, plus mirror probing.
public sealed class UpdateNetwork
{
	private static readonly HashSet<string> GithubHosts = new(StringComparer.OrdinalIgnoreCase) { "github.com", "api.github.com" };
	private static readonly TimeSpan OrderTtl = TimeSpan.FromMinutes(10);
	private readonly FetchFn _raw;
	private readonly Func<UpdateNetworkSettings> _settings;
	private readonly Func<DateTimeOffset> _now;
	private readonly TimeSpan _probe;
	private readonly object _gate = new();
	private readonly Dictionary<string, (DateTimeOffset At, string[] Ids)> _orders = new();
	private readonly Dictionary<string, Task<IReadOnlyList<Mirror>>> _probes = new();

	public FetchFn Direct { get; }

	public UpdateNetwork(Func<UpdateNetworkSettings> settings, FetchFn? raw = null, Func<DateTimeOffset>? now = null, TimeSpan? probe = null)
	{
		_settings = settings;
		_raw = raw ?? DefaultRaw();
		_now = now ?? (() => DateTimeOffset.UtcNow);
		_probe = probe ?? TimeSpan.FromSeconds(5);
		Direct = FollowDirect;
	}

	public UpdateNetworkSettings Settings => _settings();

	/// <summary>系统代理自动生效；不自动跟随重定向（由 <see cref="FollowDirect"/> 按白名单跟随）。</summary>
	public static FetchFn DefaultRaw()
	{
		var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = true, ConnectTimeout = TimeSpan.FromSeconds(15) })
		{
			Timeout = Timeout.InfiniteTimeSpan,
		};
		client.DefaultRequestHeaders.UserAgent.ParseAdd("HD2ModManager-updater");
		return (url, headers, ct) =>
		{
			var req = new HttpRequestMessage(HttpMethod.Get, url);
			if (headers is not null) foreach (var (k, v) in headers) req.Headers.TryAddWithoutValidation(k, v);
			return client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
		};
	}

	private static bool DirectHostOk(Uri u, bool loopback) =>
		loopback ? IsLoopback(u) || DirectHostOk(u, false)
		: u.Scheme == "https" && string.IsNullOrEmpty(u.UserInfo) && (GithubHosts.Contains(u.Host) || u.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));

	public static bool IsLoopback(Uri u) => (u.Scheme is "http" or "https") && (u.IsLoopback || u.Host == "localhost");

	private async Task<HttpResponseMessage> FollowDirect(string url, IReadOnlyDictionary<string, string>? headers, CancellationToken ct) =>
		await FollowAsync(_raw, url, headers, ct, loopback: false).ConfigureAwait(false);

	internal static async Task<HttpResponseMessage> FollowAsync(FetchFn raw, string url, IReadOnlyDictionary<string, string>? headers, CancellationToken ct, bool loopback)
	{
		var current = new Uri(url);
		if (!DirectHostOk(current, loopback)) throw new InvalidOperationException("Refusing a non-GitHub URL");
		for (var hop = 0; hop <= 8; hop++)
		{
			var res = await raw(current.AbsoluteUri, headers, ct).ConfigureAwait(false);
			var code = (int)res.StatusCode;
			var location = code is >= 300 and < 400 ? res.Headers.Location : null;
			if (location is null) return res;
			res.Dispose();
			current = new Uri(current, location);
			if (!DirectHostOk(current, loopback)) throw new InvalidOperationException($"Refusing a redirect to {current.Host}");
		}
		throw new InvalidOperationException("Too many redirects");
	}

	private IReadOnlyList<Mirror> Mirrors()
	{
		var custom = UpdateMirrors.Custom(_settings().CustomMirror);
		return custom is null ? UpdateMirrors.All : UpdateMirrors.All.Append(custom).ToList();
	}

	/// <summary>并发发 1 字节探测请求给各镜像并按耗时排序（缓存 10 分钟）；失败的镜像排最后，之后仍可能再试。</summary>
	public Task<IReadOnlyList<Mirror>> OrderedMirrorsAsync(string url, CancellationToken ct)
	{
		var all = Mirrors();
		var key = $"{url}|{string.Join('|', all.Select(m => m.Base))}";
		lock (_gate)
		{
			if (_orders.TryGetValue(key, out var cached) && _now() - cached.At < OrderTtl)
				return Task.FromResult<IReadOnlyList<Mirror>>(cached.Ids.Select(id => all.FirstOrDefault(m => m.Id == id)).Where(m => m is not null).Select(m => m!).ToList());
			if (_probes.TryGetValue(key, out var running)) return running;
			var task = ProbeAsync(key, all, url, ct);
			_probes[key] = task;
			return task;
		}
	}

	private async Task<IReadOnlyList<Mirror>> ProbeAsync(string key, IReadOnlyList<Mirror> all, string url, CancellationToken ct)
	{
		try
		{
			var measured = await Task.WhenAll(all.Select(async (m, i) =>
			{
				var sw = Stopwatch.StartNew();
				var time = double.PositiveInfinity;
				using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
				cts.CancelAfter(_probe);
				try
				{
					using var res = await UpdateMirrors.Wrap(m, _raw)(url, new Dictionary<string, string> { ["Range"] = "bytes=0-0" }, cts.Token).ConfigureAwait(false);
					if (res.IsSuccessStatusCode) time = sw.Elapsed.TotalMilliseconds;
				}
				catch { /* 失败的镜像排最后 */ }
				return (m, time, i);
			})).ConfigureAwait(false);
			var sorted = measured.OrderBy(x => x.time).ThenBy(x => x.i).Select(x => x.m).ToList();
			lock (_gate)
			{
				if (_orders.Count >= 64) _orders.Remove(_orders.Keys.First());
				_orders[key] = (_now(), sorted.Select(m => m.Id).ToArray());
			}
			return sorted;
		}
		finally
		{
			lock (_gate) _probes.Remove(key);
		}
	}

	/// <summary>一次动作的请求策略；ask 模式下显式选择的镜像只对这次动作生效。</summary>
	public UpdateFetch Source(string? mirrorId = null, bool allowLoopback = false) => new(this, mirrorId, allowLoopback);

	public sealed class UpdateFetch
	{
		private readonly UpdateNetwork _net;
		private readonly string? _mirrorId;
		private readonly bool _allowLoopback;
		private readonly HashSet<string> _failedMirrors = new();
		private bool _skipDirect;
		private string _lastMirror = string.Empty;

		internal UpdateFetch(UpdateNetwork net, string? mirrorId, bool allowLoopback)
		{
			_net = net;
			_mirrorId = mirrorId;
			_allowLoopback = allowLoopback;
			Fetch = SendAsync;
		}

		public FetchFn Fetch { get; }

		/// <summary>最近一次请求实际走的来源。</summary>
		public DigestSource Last { get; private set; } = DigestSource.Github;

		public FetchFn Direct => _net.Direct;

		public UpdateNetworkSettings Settings => _net.Settings;

		/// <summary>非 ask 模式、或用户显式选了镜像时，直连失败后可以回退到镜像。</summary>
		public bool CanFallback => _net.Settings.Mode != DownloadSourceMode.Ask || !string.IsNullOrEmpty(_mirrorId);

		/// <summary>报告当前来源失败：此后跳过直连，并把刚用过的镜像拉黑。</summary>
		public void Failed()
		{
			_skipDirect = true;
			if (Last == DigestSource.Mirror) _failedMirrors.Add(_lastMirror);
		}

		private async Task<HttpResponseMessage> SendAsync(string url, IReadOnlyDictionary<string, string>? headers, CancellationToken ct)
		{
			var cfg = _net.Settings;
			var original = new Uri(url);
			if (!GithubHosts.Contains(original.Host))
			{
				// 非 GitHub 地址（本地测试服务）只在测试里放行。
				if ((cfg.Mode != DownloadSourceMode.Ask || !string.IsNullOrEmpty(_mirrorId)) && !(_allowLoopback && IsLoopback(original)))
					throw new InvalidOperationException("Refusing a non-GitHub download URL");
				return await FollowAsync(_net._raw, url, headers, ct, _allowLoopback).ConfigureAwait(false);
			}
			var selected = UpdateMirrors.ById(string.IsNullOrEmpty(_mirrorId) ? cfg.MirrorId : _mirrorId, cfg.CustomMirror) ?? UpdateMirrors.All[0];
			if (cfg.Mode == DownloadSourceMode.Ask)
			{
				if (!string.IsNullOrEmpty(_mirrorId)) { Last = DigestSource.Mirror; _lastMirror = selected.Id; return await UpdateMirrors.Wrap(selected, _net._raw)(url, headers, ct).ConfigureAwait(false); }
				Last = DigestSource.Github;
				return await _net.Direct(url, headers, ct).ConfigureAwait(false);
			}

			async Task<HttpResponseMessage> Try(FetchFn f, bool mirrored)
			{
				var res = await f(url, headers, ct).ConfigureAwait(false);
				var code = (int)res.StatusCode;
				if (code >= 500 || res.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden || (mirrored && !res.IsSuccessStatusCode))
				{
					res.Dispose();
					throw new HttpRequestException("Retryable outbound response");
				}
				return res;
			}

			Exception? error = null;
			if (cfg.Mode == DownloadSourceMode.Auto && !_skipDirect && string.IsNullOrEmpty(_mirrorId))
			{
				try { Last = DigestSource.Github; return await Try(_net.Direct, false).ConfigureAwait(false); }
				catch (Exception e) when (e is not OperationCanceledException) { error = e; }
				ct.ThrowIfCancellationRequested();
			}
			var ordered = await _net.OrderedMirrorsAsync(url, ct).ConfigureAwait(false);
			var list = (cfg.Mode == DownloadSourceMode.Mirror || !string.IsNullOrEmpty(_mirrorId)
				? new[] { selected }.Concat(ordered.Where(m => m.Id != selected.Id))
				: ordered).Where(m => !_failedMirrors.Contains(m.Id)).ToList();
			foreach (var m in list)
			{
				ct.ThrowIfCancellationRequested();
				try { Last = DigestSource.Mirror; _lastMirror = m.Id; return await Try(UpdateMirrors.Wrap(m, _net._raw), true).ConfigureAwait(false); }
				catch (Exception e) when (e is not OperationCanceledException) { error = e; }
			}
			if (cfg.Mode == DownloadSourceMode.Mirror && !ct.IsCancellationRequested)
			{
				Last = DigestSource.Github;
				return await _net.Direct(url, headers, ct).ConfigureAwait(false);
			}
			throw error ?? new HttpRequestException("No download source available");
		}

		/// <summary>元数据与摘要必须先试官方、官方不全或失败才信镜像给的。</summary>
		public async Task<(T Value, DigestSource Source)> OfficialFirstAsync<T>(Func<FetchFn, Task<T>> read, Func<T, bool>? complete = null)
		{
			if (!CanFallback) return (await read(Fetch).ConfigureAwait(false), DigestSource.Github);
			try
			{
				var value = await read(Direct).ConfigureAwait(false);
				if (complete is not null && !complete(value)) throw new InvalidOperationException("Official metadata is incomplete");
				return (value, DigestSource.Github);
			}
			catch (OperationCanceledException) { throw; }
			catch
			{
				var value = await read(Fetch).ConfigureAwait(false);
				return (value, Last);
			}
		}
	}
}
