using System.Text.RegularExpressions;

namespace HD2ModCore.Updates;

/// <summary>一次请求：发出去、不自动跟随重定向（重定向由调用方按白名单处理）。</summary>
public delegate Task<HttpResponseMessage> FetchFn(string url, IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken);

public sealed record Mirror(string Id, string Host, string Base);

// 作用：无法直连 github.com 的用户使用的公共 GitHub 镜像。所有镜像都把原始 URL 作为路径后缀
// （`<base><https://github.com/...>`）；下载仍然要核对 SHA-256，元数据先试官方、再信镜像给的摘要。
// Purpose: Public GitHub mirrors for people who cannot reach github.com. Every mirror takes the original URL as a path suffix.
public static class UpdateMirrors
{
	public const string CustomId = "custom";

	/// <summary>按推荐度排序：对话框提供前两个。</summary>
	public static readonly IReadOnlyList<Mirror> All = new[]
	{
		new Mirror("ghfast", "ghfast.top", "https://ghfast.top/"),
		new Mirror("gh-proxy", "gh-proxy.com", "https://gh-proxy.com/"),
		new Mirror("ghproxy-net", "ghproxy.net", "https://ghproxy.net/"),
	};

	private static readonly HashSet<string> MirroredHosts = new(StringComparer.OrdinalIgnoreCase) { "github.com", "api.github.com" };
	private const int MaxRedirects = 8;

	/// <summary>
	/// 用户自己的镜像前缀，规范化（以 / 结尾）：空串表示未设置，null 表示不可接受。
	/// 只收 https、公网 DNS 名（不收 IP 字面量、单段名、本地名），不带账号、查询串、片段，避免手误或恶意值把请求指向本机/内网。
	/// </summary>
	public static string? NormalizeCustom(string? raw)
	{
		var text = raw?.Trim();
		if (string.IsNullOrEmpty(text)) return string.Empty;
		if (text.Length > 200 || text.Any(c => c <= ' ' || c == '\u007f')) return null;
		if (!Uri.TryCreate(text, UriKind.Absolute, out var u)) return null;
		var h = u.IdnHost.ToLowerInvariant();
		if (u.Scheme != "https" || !string.IsNullOrEmpty(u.UserInfo) || !string.IsNullOrEmpty(u.Query) || !string.IsNullOrEmpty(u.Fragment) || !u.IsDefaultPort) return null;
		if (!Regex.IsMatch(h, @"^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+$") || Regex.IsMatch(h, @"^[\d.]+$")) return null;
		if (h == "localhost" || Regex.IsMatch(h, @"\.(localhost|local|internal|lan|home|corp|test|invalid)$")) return null;
		var path = u.AbsolutePath;
		return path.EndsWith('/') ? $"https://{h}{path}" : $"https://{h}{path}/";
	}

	public static Mirror? Custom(string? raw)
	{
		var b = NormalizeCustom(raw);
		return string.IsNullOrEmpty(b) ? null : new Mirror(CustomId, new Uri(b).Host, b);
	}

	/// <summary>id「custom」解析为已保存的自定义前缀（界面永远不传 URL）。</summary>
	public static Mirror? ById(string? id, string? custom) => id == CustomId ? Custom(custom) : All.FirstOrDefault(m => m.Id == id);

	public static IReadOnlyList<Mirror> Offered(string? custom)
	{
		var own = Custom(custom);
		return All.Take(2).Concat(own is null ? Array.Empty<Mirror>() : new[] { own }).ToList();
	}

	private static bool RedirectOk(Uri u, Mirror mirror) =>
		u.Scheme == "https" && string.IsNullOrEmpty(u.UserInfo)
		&& (u.Host == new Uri(mirror.Base).Host || u.Host == "github.com" || u.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));

	/// <summary>
	/// 把 GitHub URL 经镜像发出去；其他 URL 一律拒绝。重定向在这里手动跟随：镜像会返回路径绝对的
	/// `Location: /https://github.com/...`，且只允许跳到镜像自己或 GitHub。
	/// </summary>
	public static FetchFn Wrap(Mirror mirror, FetchFn raw) => async (url, headers, ct) =>
	{
		if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) throw new ArgumentException("Invalid URL");
		if (u.Scheme != "https" || !MirroredHosts.Contains(u.Host) || !string.IsNullOrEmpty(u.UserInfo))
			throw new InvalidOperationException($"Refusing to send {u.Host} through a mirror");
		var current = mirror.Base + u.AbsoluteUri;
		for (var hop = 0; hop <= MaxRedirects; hop++)
		{
			var res = await raw(current, headers, ct).ConfigureAwait(false);
			var code = (int)res.StatusCode;
			var location = code is >= 300 and < 400 ? res.Headers.Location : null;
			if (location is null) return res;
			res.Dispose();
			var next = new Uri(new Uri(current), location);
			if (!RedirectOk(next, mirror)) throw new InvalidOperationException($"Refusing a redirect to {next.Host}");
			current = next.AbsoluteUri;
		}
		throw new InvalidOperationException("Too many redirects");
	};
}
