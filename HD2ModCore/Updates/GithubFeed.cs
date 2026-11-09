using System.Net;
using System.Text.RegularExpressions;

namespace HD2ModCore.Updates;

// 作用：不用 REST API（匿名每小时只许 60 次）取 GitHub Release 元数据：releases.atom、expanded_assets 页面。
// API 被限流或出错时作为兜底。这些页面是给浏览器看的 HTML，所以每个解析器都宽松、宁可少给也不乱猜：
// 没有摘要的资产照样列出，只是之后的下载会拒绝它。
// Purpose: Release metadata without the REST API (60 anonymous requests an hour): the releases Atom feed and the
// expanded-assets page. Parsers are tolerant and return less rather than guessing.
public static class GithubFeed
{
	private static readonly Regex RepoRe = new(@"^[A-Za-z0-9._-]+/[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant);
	private static readonly Regex TagRe = new(@"^[A-Za-z0-9._-]{1,100}$", RegexOptions.CultureInvariant);

	private static string Unescape(string s) => Regex.Replace(s, @"&(#x[0-9a-f]+|#\d+|[a-z]+);", m =>
	{
		var e = m.Groups[1].Value;
		if (e[0] == '#')
		{
			var ok = e[1] is 'x' or 'X' ? int.TryParse(e[2..], System.Globalization.NumberStyles.HexNumber, null, out var hex) : int.TryParse(e[1..], out hex);
			return ok && hex > 0 && hex <= 0x10ffff && !(hex >= 0xD800 && hex <= 0xDFFF) ? char.ConvertFromUtf32(hex) : m.Value;
		}
		return e.ToLowerInvariant() switch { "amp" => "&", "lt" => "<", "gt" => ">", "quot" => "\"", "apos" => "'", "nbsp" => " ", _ => m.Value };
	}, RegexOptions.IgnoreCase);

	/// <summary>Release 说明转纯文本：feed 里是转义过的 HTML。</summary>
	public static string HtmlToText(string html)
	{
		var s = Unescape(html);
		s = Regex.Replace(s, @"</(p|h[1-6]|ul|ol|pre|blockquote)>", "\n", RegexOptions.IgnoreCase);
		s = Regex.Replace(s, @"<li[^>]*>", "- ", RegexOptions.IgnoreCase);
		s = Regex.Replace(s, @"</li>", "\n", RegexOptions.IgnoreCase);
		s = Regex.Replace(s, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
		s = Regex.Replace(s, @"<[^>]+>", string.Empty);
		return Regex.Replace(Unescape(s), @"\n{3,}", "\n\n").Trim();
	}

	/// <summary>releases.atom 的条目，新的在前。草稿不会出现在 feed 里。</summary>
	public static List<GithubRelease> ParseReleasesAtom(string xml, string repo)
	{
		var list = new List<GithubRelease>();
		foreach (Match m in Regex.Matches(xml, @"<entry>([\s\S]*?)</entry>"))
		{
			var entry = m.Groups[1].Value;
			var tagRaw = Regex.Match(entry, "<link[^>]*href=\"[^\"]*/releases/tag/([^\"/]+)\"").Groups[1];
			if (!tagRaw.Success) continue;
			string tag;
			try { tag = Uri.UnescapeDataString(tagRaw.Value); } catch (UriFormatException) { continue; }
			if (!TagRe.IsMatch(tag)) continue;
			var title = Regex.Match(entry, @"<title>([\s\S]*?)</title>").Groups[1];
			var content = Regex.Match(entry, @"<content[^>]*>([\s\S]*?)</content>").Groups[1];
			var updated = Regex.Match(entry, @"<updated>([^<]+)</updated>").Groups[1];
			list.Add(new GithubRelease(
				tag,
				title.Success ? Unescape(title.Value).Trim() : tag,
				content.Success ? HtmlToText(content.Value) : string.Empty,
				false, false,
				$"https://github.com/{repo}/releases/tag/{tag}",
				updated.Success && DateTimeOffset.TryParse(updated.Value, out var at) ? at : null,
				Array.Empty<ReleaseAsset>()));
		}
		return list;
	}

	/// <summary>一个 Release 的 expanded-assets HTML 片段里的资产。</summary>
	public static List<ReleaseAsset> ParseExpandedAssets(string html, string repo, string tag)
	{
		var prefix = $"/{repo}/releases/download/{tag}/";
		var digests = new Dictionary<string, string>();
		foreach (Match m in Regex.Matches(html, "aria-label=\"Copy to clipboard digest for ([^\"]+)\"[^>]*?value=\"(sha256:[0-9a-f]{64})\"", RegexOptions.IgnoreCase))
			digests[Unescape(m.Groups[1].Value)] = m.Groups[2].Value.ToLowerInvariant();
		foreach (Match m in Regex.Matches(html, "value=\"(sha256:[0-9a-f]{64})\"[^>]*?aria-label=\"Copy to clipboard digest for ([^\"]+)\"", RegexOptions.IgnoreCase))
			digests[Unescape(m.Groups[2].Value)] = m.Groups[1].Value.ToLowerInvariant();
		var seen = new HashSet<string>();
		var assets = new List<ReleaseAsset>();
		foreach (Match m in Regex.Matches(html, "href=\"([^\"]+)\""))
		{
			var href = Unescape(m.Groups[1].Value);
			if (!href.StartsWith(prefix, StringComparison.Ordinal)) continue;
			string name;
			try { name = Uri.UnescapeDataString(href[prefix.Length..]); } catch (UriFormatException) { continue; }
			if (name.Length == 0 || name.Contains('/') || !seen.Add(name)) continue;
			assets.Add(new ReleaseAsset(name, "https://github.com" + href, digests.GetValueOrDefault(name)));
		}
		return assets;
	}

	private static void CheckRepo(string repo)
	{
		if (!RepoRe.IsMatch(repo)) throw new UpdateException(UpdateErrorCode.Http, "Unexpected repository name");
	}

	public static async Task<List<GithubRelease>> FetchReleasesAtomAsync(FetchFn fetch, string repo, CancellationToken ct)
	{
		CheckRepo(repo);
		var xml = await UpdateHttp.GetTextAsync(fetch, $"https://github.com/{repo}/releases.atom", ct).ConfigureAwait(false);
		var list = ParseReleasesAtom(xml, repo);
		if (list.Count == 0) throw new UpdateException(UpdateErrorCode.Http, "Release feed has no entries");
		return list;
	}

	public static async Task<List<ReleaseAsset>> FetchExpandedAssetsAsync(FetchFn fetch, string repo, string tag, CancellationToken ct)
	{
		CheckRepo(repo);
		if (!TagRe.IsMatch(tag)) throw new UpdateException(UpdateErrorCode.Http, "Unexpected release tag");
		var html = await UpdateHttp.GetTextAsync(fetch, $"https://github.com/{repo}/releases/expanded_assets/{Uri.EscapeDataString(tag)}", ct).ConfigureAwait(false);
		var assets = ParseExpandedAssets(html, repo, tag);
		if (assets.Count == 0) throw new UpdateException(UpdateErrorCode.Http, "Release page has no assets");
		return assets;
	}

	/// <summary>API 的失败是否能用 github.com 页面绕过。</summary>
	public static bool ApiFallbackCode(Exception e) => e is UpdateException { Code: UpdateErrorCode.RateLimited or UpdateErrorCode.Http };

	/// <summary>API 没给摘要时，先从官方 expanded-assets 页面补齐，再考虑镜像。</summary>
	public static async Task<IReadOnlyList<ReleaseAsset>> FillAssetDigestsAsync(FetchFn fetch, string repo, string tag, IReadOnlyList<ReleaseAsset> assets, CancellationToken ct)
	{
		if (assets.All(a => ReleaseJson.IsSha256Digest(a.Digest))) return assets;
		try
		{
			var web = await FetchExpandedAssetsAsync(fetch, repo, tag, ct).ConfigureAwait(false);
			return assets.Select(a =>
			{
				var extra = web.FirstOrDefault(b => b.Name == a.Name);
				return ReleaseJson.IsSha256Digest(a.Digest) || extra is null ? a : a with { Digest = extra.Digest };
			}).ToList();
		}
		catch (OperationCanceledException) { throw; }
		catch { return assets; } // 官方没发布摘要就是没有；是否拒绝由调用方决定。
	}
}

internal static class UpdateHttp
{
	public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

	/// <summary>读文本；状态码映射：403/429→限流，其他非 2xx→Http，异常/超时→Network。</summary>
	public static async Task<string> GetTextAsync(FetchFn fetch, string url, CancellationToken ct, string accept = "application/vnd.github+json, text/plain, */*")
	{
		using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
		cts.CancelAfter(Timeout);
		try
		{
			using var res = await fetch(url, new Dictionary<string, string> { ["Accept"] = accept }, cts.Token).ConfigureAwait(false);
			if (res.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests) throw new UpdateException(UpdateErrorCode.RateLimited, "Rate limited");
			if (!res.IsSuccessStatusCode) throw new UpdateException(UpdateErrorCode.Http, $"HTTP {(int)res.StatusCode}");
			return await res.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
		}
		catch (UpdateException) { throw; }
		catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
		catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException or InvalidOperationException or ArgumentException)
		{
			throw new UpdateException(UpdateErrorCode.Network, "Request failed or timed out", e);
		}
	}
}
