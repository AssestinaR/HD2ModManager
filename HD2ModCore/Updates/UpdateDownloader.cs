using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace HD2ModCore.Updates;

public sealed class DownloadOptions
{
	public Action<long, long?>? OnProgress { get; init; }
	public TimeSpan[] RetryDelays { get; init; } = { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10) };
	public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(30);
	/// <summary>目录的可用字节数；null 表示不检查。</summary>
	public Func<string, long?>? FreeBytes { get; init; }
	/// <summary>当前来源失败（用于让网络层换来源）。</summary>
	public Action? SourceFailed { get; init; }
}

// 作用：下载一个 Release 资产到文件：必须有 SHA-256 摘要；.part + 身份旁路文件支持断点续传；
// 卡住超时、重试、磁盘空间检查；摘要不符就删掉已下内容。成功后原子改名为目标文件。
// Purpose: Downloads one release asset to a file, with mandatory SHA-256, resumable .part files, stall timeout,
// retries and a disk-space check.
public static class UpdateDownloader
{
	private static readonly object Gate = new();
	private static readonly HashSet<string> Active = new(StringComparer.OrdinalIgnoreCase);

	public static async Task<string> DownloadAsync(FetchFn fetch, ReleaseAsset asset, string file, CancellationToken ct, DownloadOptions? options = null)
	{
		options ??= new DownloadOptions();
		var expected = Regex.Match(asset.Digest ?? string.Empty, "^sha256:([0-9a-f]{64})$", RegexOptions.IgnoreCase).Groups[1];
		if (!expected.Success) throw new UpdateException(UpdateErrorCode.NoDigest, "Release asset has no SHA-256 digest");
		var want = expected.Value.ToLowerInvariant();
		lock (Gate) { if (!Active.Add(file)) throw new UpdateException(UpdateErrorCode.Network, "Download already in progress"); }
		var part = file + ".part";
		var sidecar = part + ".json";
		var identity = $"{want}|{asset.Size?.ToString() ?? "-"}|{asset.Name}";
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(file)!);
			long SizeOf() => File.Exists(part) ? new FileInfo(part).Length : 0;
			void Drop() { TryDelete(part); TryDelete(sidecar); }
			var same = false;
			try { same = File.Exists(sidecar) && File.ReadAllText(sidecar) == identity; } catch (IOException) { }
			if (!same || (asset.Size is long cap && SizeOf() > cap)) Drop();
			File.WriteAllText(sidecar, identity);

			for (var attempt = 0; ; attempt++)
			{
				ct.ThrowIfCancellationRequested();
				var offset = SizeOf();
				// 元数据可能没有 size：整包已收齐时别卡在 HTTP 416。
				if (offset > 0 && asset.Size is null && await Sha256FileAsync(part, ct).ConfigureAwait(false) == want)
				{
					File.Move(part, file, true);
					TryDelete(sidecar);
					options.OnProgress?.Invoke(offset, offset);
					return want;
				}
				var remaining = asset.Size is long sz ? Math.Max(0, sz - offset) : (long?)null;
				var free = remaining is null ? null : options.FreeBytes?.Invoke(Path.GetDirectoryName(file)!);
				if (remaining is not null && free is not null && free < remaining) throw new UpdateException(UpdateErrorCode.DiskSpace, $"Not enough free disk space for {asset.Name}");
				try
				{
					using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
					stall.CancelAfter(options.StallTimeout);
					// 上次已收齐每个字节但被取消：不发请求（会 416），直接校验。
					if (!(asset.Size is long full && offset == full && offset > 0))
					{
						using var res = await fetch(asset.Url, offset > 0 ? new Dictionary<string, string> { ["Range"] = $"bytes={offset}-" } : null, stall.Token).ConfigureAwait(false);
						if (res.StatusCode == HttpStatusCode.PartialContent)
						{
							var cr = res.Content.Headers.ContentRange;
							if (cr is null || cr.From != offset || (asset.Size is long s2 && cr.Length != s2)) throw new UpdateException(UpdateErrorCode.Http, "Invalid partial response");
						}
						else if (res.StatusCode == HttpStatusCode.OK) offset = 0;
						else if ((int)res.StatusCode is 403 or 429) throw new UpdateException(UpdateErrorCode.RateLimited, "Rate limited");
						else throw new UpdateException(UpdateErrorCode.Http, $"HTTP {(int)res.StatusCode}");
						var length = res.Content.Headers.ContentLength;
						var total = asset.Size ?? (length is > 0 ? offset + length : null);
						var received = offset;
						options.OnProgress?.Invoke(received, total);
						await using var src = await res.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
						await using (var dst = new FileStream(part, offset > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read))
						{
							var buffer = new byte[128 * 1024];
							int n;
							stall.CancelAfter(options.StallTimeout);
							while ((n = await src.ReadAsync(buffer, stall.Token).ConfigureAwait(false)) > 0)
							{
								stall.CancelAfter(options.StallTimeout);
								await dst.WriteAsync(buffer.AsMemory(0, n), stall.Token).ConfigureAwait(false);
								received += n;
								options.OnProgress?.Invoke(received, total);
							}
						}
						if (asset.Size is long want2 && received != want2) throw new UpdateException(UpdateErrorCode.Network, "Incomplete download");
					}
					var actual = await Sha256FileAsync(part, ct).ConfigureAwait(false);
					if (actual != want)
					{
						Drop();
						throw new UpdateException(UpdateErrorCode.DigestMismatch, $"SHA-256 mismatch: {asset.Name}");
					}
					ct.ThrowIfCancellationRequested();
					File.Move(part, file, true);
					TryDelete(sidecar);
					return actual;
				}
				catch (UpdateException e) when (e.Code is not (UpdateErrorCode.Network or UpdateErrorCode.Http)) { throw; }
				catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
				catch (Exception e) when (e is UpdateException or HttpRequestException or IOException or OperationCanceledException or InvalidOperationException)
				{
					// 卡住超时（OperationCanceledException 但不是用户取消）与网络/5xx 一样可重试。
					if (attempt >= options.RetryDelays.Length)
						throw e as UpdateException ?? new UpdateException(UpdateErrorCode.Network, $"Download interrupted: {asset.Name}", e);
					options.SourceFailed?.Invoke();
					await Task.Delay(options.RetryDelays[attempt], ct).ConfigureAwait(false);
				}
			}
		}
		finally
		{
			lock (Gate) Active.Remove(file);
		}
	}

	public static async Task<string> Sha256FileAsync(string path, CancellationToken ct = default)
	{
		await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
		return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false)).ToLowerInvariant();
	}

	/// <summary>清掉没有对应 .part 的旧旁路文件、以及过期（7 天）的 .part。</summary>
	public static void CleanupParts(string dir)
	{
		try
		{
			foreach (var f in Directory.EnumerateFiles(dir, "*.part"))
				if (DateTime.UtcNow - File.GetLastWriteTimeUtc(f) > TimeSpan.FromDays(7)) { TryDelete(f); TryDelete(f + ".json"); }
			foreach (var f in Directory.EnumerateFiles(dir, "*.part.json"))
				if (!File.Exists(f[..^5])) TryDelete(f);
			foreach (var f in Directory.EnumerateFiles(dir, "*.tmp")) TryDelete(f);
		}
		catch (IOException) { }
		catch (UnauthorizedAccessException) { }
	}

	private static void TryDelete(string path)
	{
		try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
	}
}
