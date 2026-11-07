using System.Reflection;
using System.Text.RegularExpressions;

namespace HD2ModCore.Updates;

// 作用：语义化版本（SemVer 2.0）的解析与比较，用于判断 Release 是否比当前程序新。
// Purpose: SemVer 2.0 parsing and precedence, used to decide whether a release is newer than the running app.
public sealed record SemVer(int Major, int Minor, int Patch, IReadOnlyList<string> Pre)
{
	public override string ToString() => $"{Major}.{Minor}.{Patch}" + (Pre.Count > 0 ? "-" + string.Join('.', Pre) : string.Empty);
}

public static class AppVersion
{
	// 两段式（如上游已有的 v1.4）按 x.y.0 处理；构建元数据（+xxx）忽略。
	private static readonly Regex Pattern = new(
		@"^(0|[1-9]\d*)\.(0|[1-9]\d*)(?:\.(0|[1-9]\d*))?(?:-((?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*))*))?(?:\+[0-9A-Za-z.-]+)?$",
		RegexOptions.CultureInvariant);

	public static SemVer? Parse(string? text)
	{
		if (string.IsNullOrEmpty(text) || text.Length > 64) return null;
		var m = Pattern.Match(text);
		if (!m.Success) return null;
		if (!int.TryParse(m.Groups[1].Value, out var major) || !int.TryParse(m.Groups[2].Value, out var minor)) return null;
		var patch = 0;
		if (m.Groups[3].Success && !int.TryParse(m.Groups[3].Value, out patch)) return null;
		var pre = m.Groups[4].Success ? m.Groups[4].Value.Split('.') : Array.Empty<string>();
		return new SemVer(major, minor, patch, pre);
	}

	/// <summary>规范化后的版本文本（x.y.z[-pre]）；不合法返回 null。</summary>
	public static string? Normalize(string? text) => Parse(text)?.ToString();

	/// <summary>Release 标签（v1.5.0 / 1.5.0）里的版本；不合法返回 null。</summary>
	public static string? FromTag(string? tag)
	{
		if (string.IsNullOrEmpty(tag)) return null;
		return Normalize(tag[0] is 'v' or 'V' ? tag[1..] : tag);
	}

	/// <summary>SemVer 优先级：a 比 b 旧时小于 0。不合法的版本排在最低。</summary>
	public static int Compare(string? a, string? b)
	{
		var x = Parse(a);
		var y = Parse(b);
		if (x is null || y is null) return (x is null ? 0 : 1) - (y is null ? 0 : 1);
		if (x.Major != y.Major) return x.Major.CompareTo(y.Major);
		if (x.Minor != y.Minor) return x.Minor.CompareTo(y.Minor);
		if (x.Patch != y.Patch) return x.Patch.CompareTo(y.Patch);
		if (x.Pre.Count == 0 || y.Pre.Count == 0) return (x.Pre.Count > 0 ? -1 : 0) - (y.Pre.Count > 0 ? -1 : 0);
		for (var i = 0; i < Math.Min(x.Pre.Count, y.Pre.Count); i++)
		{
			var p = x.Pre[i];
			var q = y.Pre[i];
			if (p == q) continue;
			var pn = p.All(char.IsAsciiDigit);
			var qn = q.All(char.IsAsciiDigit);
			if (pn && qn) return decimal.Parse(p).CompareTo(decimal.Parse(q));
			if (pn != qn) return pn ? -1 : 1;
			return string.CompareOrdinal(p, q) < 0 ? -1 : 1;
		}
		return x.Pre.Count.CompareTo(y.Pre.Count);
	}

	/// <summary>程序集的版本号（InformationalVersion 去掉 +构建信息）；读不到时为 0.0.0。</summary>
	public static string Current(Assembly? assembly)
	{
		var info = assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
		return Normalize(info) ?? "0.0.0";
	}
}
