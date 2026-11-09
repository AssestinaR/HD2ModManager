using System.Text.Json;
using System.Text.RegularExpressions;

namespace HD2ModCore.Updates;

/// <summary><paramref name="ApiUrl"/>：REST API 的资产地址（Accept: application/octet-stream 取内容），github.com/releases/download 不可用时的备用路径；用之前必须过 AppUpdater 的校验。</summary>
public sealed record ReleaseAsset(string Name, string Url, string? Digest, long? Size = null, string? ApiUrl = null);

public sealed record GithubRelease(
	string TagName, string? Name, string? Body, bool Draft, bool Prerelease, string? HtmlUrl,
	DateTimeOffset? PublishedAt, IReadOnlyList<ReleaseAsset> Assets);

/// <summary>界面上展示的新版本。</summary>
public sealed record AppRelease(string Version, string Name, string Notes, string Url, DateTimeOffset? PublishedAt, bool Prerelease);

public sealed record UpdateCandidate(AppRelease Release, ReleaseAsset? Package, ReleaseAsset? Sums);

public static class ReleaseJson
{
	public static bool IsSha256Digest(string? digest) => digest is not null && Regex.IsMatch(digest, "^sha256:[0-9a-fA-F]{64}$");

	/// <summary>GitHub REST API 的 releases 列表。结构不对就抛 BadResponse。</summary>
	public static List<GithubRelease> ParseApiList(string json)
	{
		try
		{
			using var doc = JsonDocument.Parse(json);
			if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new UpdateException(UpdateErrorCode.BadResponse, "Release list is not an array");
			var list = new List<GithubRelease>();
			foreach (var r in doc.RootElement.EnumerateArray())
			{
				if (r.ValueKind != JsonValueKind.Object) continue;
				var assets = new List<ReleaseAsset>();
				if (r.TryGetProperty("assets", out var arr) && arr.ValueKind == JsonValueKind.Array)
				{
					foreach (var a in arr.EnumerateArray())
					{
						var name = Str(a, "name");
						var url = Str(a, "browser_download_url");
						if (name is null || url is null) continue;
						long? size = a.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt64(out var n) ? n : null;
						assets.Add(new ReleaseAsset(name, url, Str(a, "digest"), size, Str(a, "url")));
					}
				}
				DateTimeOffset? published = DateTimeOffset.TryParse(Str(r, "published_at"), out var p) ? p : null;
				list.Add(new GithubRelease(
					Str(r, "tag_name") ?? string.Empty, Str(r, "name"), Str(r, "body"),
					Bool(r, "draft"), Bool(r, "prerelease"), Str(r, "html_url"), published, assets));
			}
			return list;
		}
		catch (JsonException)
		{
			throw new UpdateException(UpdateErrorCode.BadResponse, "Release list is not valid JSON");
		}
	}

	private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
	private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}

public static class ReleasePicker
{
	public const int NotesLimit = 20_000;
	public const string SumsName = "SHA256SUMS";

	/// <summary>发布包的文件名（由 tools\PublishRelease.ps1 生成）：HD2ModManager-1.5.0-win-x64-complete.zip。</summary>
	public static string PackageName(string version, string variant) => $"HD2ModManager-{version}-win-x64-{variant}.zip";

	/// <summary>
	/// 比 <paramref name="current"/> 新的最新 Release。草稿不算；稳定版只升到稳定版，预发布版可升到更新的预发布版。
	/// URL 通不过 <paramref name="assetOk"/> 的资产被忽略。<paramref name="variant"/> 为 null 时不挑安装包（只提示）。
	/// </summary>
	public static UpdateCandidate? Pick(IEnumerable<GithubRelease> list, string current, Func<string, bool> assetOk, string? variant)
	{
		var onPre = (AppVersion.Parse(current)?.Pre.Count ?? 0) > 0;
		UpdateCandidate? best = null;
		foreach (var r in list)
		{
			var version = AppVersion.FromTag(r.TagName);
			if (r.Draft || version is null) continue;
			var pre = AppVersion.Parse(version)!.Pre.Count > 0 || r.Prerelease;
			if (pre && !onPre) continue;
			if (AppVersion.Compare(version, current) <= 0 || (best is not null && AppVersion.Compare(version, best.Release.Version) <= 0)) continue;
			var assets = r.Assets.Where(a => assetOk(a.Url)).ToList();
			var notes = r.Body ?? string.Empty;
			var name = r.Name;
			if (string.IsNullOrEmpty(name)) name = r.TagName;
			best = new UpdateCandidate(
				new AppRelease(
					version,
					name.Length > 200 ? name[..200] : name,
					notes.Length > NotesLimit ? notes[..NotesLimit] : notes,
					r.HtmlUrl is not null && r.HtmlUrl.StartsWith("https://", StringComparison.Ordinal) ? r.HtmlUrl : string.Empty,
					r.PublishedAt,
					pre),
				variant is null ? null : assets.FirstOrDefault(a => a.Name == PackageName(version, variant)),
				assets.FirstOrDefault(a => a.Name == SumsName));
		}
		return best;
	}
}

public static class ChecksumFile
{
	/// <summary>sha256sum 格式（`&lt;hex&gt;  &lt;name&gt;`）里 <paramref name="name"/> 的哈希（小写）；没有则 null。</summary>
	public static string? SumFor(string text, string name)
	{
		foreach (var line in text.Split('\n'))
		{
			var m = Regex.Match(line.Trim(), @"^([0-9a-fA-F]{64}) [ *](.+)$");
			if (m.Success && m.Groups[2].Value == name) return m.Groups[1].Value.ToLowerInvariant();
		}
		return null;
	}
}
