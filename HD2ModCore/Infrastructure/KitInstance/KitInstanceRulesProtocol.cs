using System.Globalization;
using System.Text;

namespace HD2ModCore.Infrastructure.KitInstance;

/// <summary>
/// 一条「按实例隐藏」规则：<see cref="All"/> 全部在场且 <see cref="None"/> 都不在场，
/// 并且都落在同一个角色身上（<see cref="Radius"/> 米内）时，认定该角色穿的是目标套，
/// 把 <see cref="Hide"/> 里的 Unit 实例隐藏。ID 一律 16 位小写 hex。
/// </summary>
public sealed record KitInstanceRule(
	string Name,
	double Radius,
	IReadOnlyList<string> All,
	IReadOnlyList<string> None,
	IReadOnlyList<string> Hide);

public sealed record KitInstanceRulesDocument(int Revision, IReadOnlyList<KitInstanceRule> Rules)
{
	public static KitInstanceRulesDocument Empty(int revision) => new(revision, []);
}

/// <summary>
/// <c>kit-instance.rules</c> 协议（规划 §2）。Lua 端的解析在 <c>kit_instance_hider.lua</c> 的
/// <c>M.parse_rules</c>，两边的约束必须保持一致：任何一侧认为非法的文件，另一侧也必须认为非法。
/// <para>
/// 比规划多一条约束：<c>radius</c> 不得小于 1e-6。序列化只写 6 位小数，更小的半径会被写成 0，
/// 而 Lua 端会把整份文件判为无效。
/// </para>
/// </summary>
public static class KitInstanceRulesProtocol
{
	public const string FileName = "kit-instance.rules";

	/// <summary>软映射（低污染模式）的规则文件：由映射页「应用」写，格式与 <see cref="FileName"/> 相同；Lua 两个文件都读，规则取并集。</summary>
	public const string MappingFileName = "kit-instance.mapping.rules";
	public const int SchemaVersion = 1;
	// P6 S3 扩容（规划 §11.6，lupa 压测：每帧开销与规则数、hide 长度无关）。每个规则文件各自适用。
	public const int MaximumBytes = 2 * 1024 * 1024;
	public const int MaximumRules = 1024;
	public const int MaximumAll = 8;
	public const int MaximumNone = 8;
	public const int MaximumHide = 256;
	public const int MaximumNameLength = 64;
	public const double MinimumRadius = 1e-6;

	public static bool IsValidId(string? id)
	{
		if (id is null || id.Length != 16) return false;
		foreach (var ch in id)
			if (ch is not ((>= '0' and <= '9') or (>= 'a' and <= 'f'))) return false;
		return true;
	}

	public static bool IsValidName(string? name)
	{
		if (name is null || name.Length < 1 || name.Length > MaximumNameLength) return false;
		foreach (var ch in name)
			if (ch is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-')) return false;
		return true;
	}

	/// <summary>单条规则是否合规；不合规时给出原因。</summary>
	public static bool TryValidate(KitInstanceRule rule, out string error)
	{
		ArgumentNullException.ThrowIfNull(rule);
		if (!IsValidName(rule.Name)) { error = $"规则名不合法：'{rule.Name}'（需要 [a-z0-9_-]{{1,64}}）"; return false; }
		if (double.IsNaN(rule.Radius) || !(rule.Radius >= MinimumRadius && rule.Radius <= 1.0))
		{
			error = $"规则 {rule.Name} 的 radius 必须在 [{MinimumRadius.ToString("0.######", CultureInfo.InvariantCulture)}, 1.0] 内。";
			return false;
		}
		return TryValidateList(rule.Name, "all", rule.All, 1, MaximumAll, out error)
			&& TryValidateList(rule.Name, "none", rule.None, 0, MaximumNone, out error)
			&& TryValidateList(rule.Name, "hide", rule.Hide, 1, MaximumHide, out error);
	}

	private static bool TryValidateList(
		string ruleName, string field, IReadOnlyList<string>? list, int minimum, int maximum, out string error)
	{
		error = string.Empty;
		if (list is null || list.Count < minimum || list.Count > maximum)
		{
			error = $"规则 {ruleName} 的 {field} 个数必须在 {minimum}..{maximum} 之间。";
			return false;
		}
		foreach (var id in list)
		{
			if (!IsValidId(id))
			{
				error = $"规则 {ruleName} 的 {field} 含非法 ID：'{id}'（需要 16 位小写 hex）。";
				return false;
			}
		}
		return true;
	}

	/// <summary>
	/// 把规则按顺序装进上限：超过条数或字节数的后面几条丢弃（调用方应给 Warning），保证写出的文件永远能被 Lua 接受。
	/// 不合规的规则（名字、ID、半径、个数）不在这里处理，仍由 <see cref="Serialize"/> 抛异常。
	/// </summary>
	public static IReadOnlyList<KitInstanceRule> FitToLimits(IReadOnlyList<KitInstanceRule> rules, out int dropped)
	{
		ArgumentNullException.ThrowIfNull(rules);
		var kept = new List<KitInstanceRule>(Math.Min(rules.Count, MaximumRules));
		// 头部固定开销（schema/revision/注释）留 128 字节余量；每条规则的字节数与 Serialize 的写法一致。
		var bytes = 128;
		foreach (var rule in rules)
		{
			if (kept.Count >= MaximumRules) break;
			var line = 5 + 5 + rule.Name.Length + 8 + 8 + 5 + rule.All.Count * 17 + 6 + rule.None.Count * 17 + 6 + rule.Hide.Count * 17 + 2;
			if (bytes + line > MaximumBytes) break;
			bytes += line;
			kept.Add(rule);
		}

		dropped = rules.Count - kept.Count;
		return kept;
	}

	/// <summary>写出规则文本。内容不合规（含超过 64 条或 64 KiB）时抛 <see cref="InvalidDataException"/>。</summary>
	public static string Serialize(KitInstanceRulesDocument document)
	{
		ArgumentNullException.ThrowIfNull(document);
		if (document.Revision < 0) throw new InvalidDataException("revision 不能为负。");
		if (document.Rules.Count > MaximumRules) throw new InvalidDataException($"规则超过 {MaximumRules} 条。");
		var names = new HashSet<string>(StringComparer.Ordinal);
		var builder = new StringBuilder();
		builder.Append("schema=").Append(SchemaVersion).Append('\n');
		builder.Append("revision=").Append(document.Revision.ToString(CultureInfo.InvariantCulture)).Append('\n');
		builder.Append("# 由 HD2 Mod Manager 生成，每次部署整份重写，请勿手改。\n");
		foreach (var rule in document.Rules)
		{
			if (!TryValidate(rule, out var error)) throw new InvalidDataException(error);
			if (!names.Add(rule.Name)) throw new InvalidDataException($"规则名重复：{rule.Name}");
			builder.Append("rule=name:").Append(rule.Name)
				.Append(";radius:").Append(rule.Radius.ToString("0.0#####", CultureInfo.InvariantCulture))
				.Append(";all:").Append(string.Join(',', rule.All))
				.Append(";none:").Append(string.Join(',', rule.None))
				.Append(";hide:").Append(string.Join(',', rule.Hide))
				.Append('\n');
		}
		var text = builder.ToString();
		if (Encoding.UTF8.GetByteCount(text) > MaximumBytes)
			throw new InvalidDataException($"规则文件超过 {MaximumBytes} 字节。");
		return text;
	}

	/// <summary>
	/// 解析规则文本。与 Lua 端同口径：schema 必须为 1、revision 必填、未知键与未知规则字段忽略，
	/// 其余任何不合规都使整份无效。
	/// </summary>
	public static bool TryParse(string? text, out KitInstanceRulesDocument? document, out string error)
	{
		document = null;
		if (text is null) { error = "文本为空。"; return false; }
		if (Encoding.UTF8.GetByteCount(text) > MaximumBytes) { error = "文件过大。"; return false; }
		int? schema = null;
		int? revision = null;
		var rules = new List<KitInstanceRule>();
		var names = new HashSet<string>(StringComparer.Ordinal);
		foreach (var rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
		{
			var line = rawLine.Trim();
			if (line.Length == 0 || line[0] == '#') continue;
			var equals = line.IndexOf('=');
			if (equals <= 0 || !IsKey(line.AsSpan(0, equals).TrimEnd()))
			{
				error = $"无法识别的行：{Truncate(line)}";
				return false;
			}
			var key = line[..equals].TrimEnd();
			var value = line[(equals + 1)..].Trim();
			switch (key)
			{
				case "schema":
					if (schema is not null) { error = "schema 重复。"; return false; }
					if (value != "1") { error = $"不支持的 schema：{value}"; return false; }
					schema = 1;
					break;
				case "revision":
					if (revision is not null) { error = "revision 重复。"; return false; }
					if (value.Length == 0 || !value.All(char.IsAsciiDigit)
						|| !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedRevision))
					{
						error = "revision 不是非负整数。";
						return false;
					}
					revision = parsedRevision;
					break;
				case "rule":
					if (rules.Count >= MaximumRules) { error = "规则过多。"; return false; }
					if (!TryParseRule(value, names, out var rule, out error)) return false;
					rules.Add(rule!);
					break;
			}
		}
		if (schema is null) { error = "缺少 schema。"; return false; }
		if (revision is null) { error = "缺少 revision。"; return false; }
		document = new KitInstanceRulesDocument(revision.Value, rules);
		error = string.Empty;
		return true;
	}

	private static bool IsKey(ReadOnlySpan<char> key)
	{
		if (key.IsEmpty) return false;
		foreach (var ch in key)
			if (!(char.IsAsciiLetterOrDigit(ch) || ch == '_')) return false;
		return true;
	}

	private static bool TryParseRule(string value, HashSet<string> names, out KitInstanceRule? rule, out string error)
	{
		rule = null;
		var fields = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var rawSegment in value.Split(';'))
		{
			var segment = rawSegment.Trim();
			if (segment.Length == 0) continue;
			var colon = segment.IndexOf(':');
			if (colon <= 0 || !IsKey(segment.AsSpan(0, colon).TrimEnd()))
			{
				error = $"规则字段不合法：{Truncate(segment)}";
				return false;
			}
			var key = segment[..colon].TrimEnd();
			if (key is not ("name" or "radius" or "all" or "none" or "hide")) continue; // 未知字段忽略
			if (!fields.TryAdd(key, segment[(colon + 1)..].Trim()))
			{
				error = $"规则字段重复：{key}";
				return false;
			}
		}

		if (!fields.TryGetValue("name", out var name) || !IsValidName(name)) { error = "规则名不合法。"; return false; }
		if (!names.Add(name)) { error = $"规则名重复：{name}"; return false; }
		if (!fields.TryGetValue("radius", out var radiusText) || !IsPlainDecimal(radiusText)
			|| !double.TryParse(radiusText, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var radius))
		{
			error = $"规则 {name} 的 radius 不合法。";
			return false;
		}
		if (!fields.TryGetValue("all", out var allText) || !fields.TryGetValue("hide", out var hideText))
		{
			error = $"规则 {name} 缺少 all 或 hide。";
			return false;
		}
		if (!TryParseIds(allText, out var all) || !TryParseIds(fields.GetValueOrDefault("none") ?? string.Empty, out var none)
			|| !TryParseIds(hideText, out var hide))
		{
			error = $"规则 {name} 含非法 ID 列表。";
			return false;
		}
		var candidate = new KitInstanceRule(name, radius, all, none, hide);
		if (!TryValidate(candidate, out error)) return false;
		rule = candidate;
		return true;
	}

	// 只接受 123 / 1.5 / .5 这种写法：Lua 端用同一个形状，避免 0x1、1e-1、inf 这类 tonumber 能吃的写法。
	private static bool IsPlainDecimal(string text)
	{
		if (text.Length == 0) return false;
		var dots = 0;
		var digits = 0;
		foreach (var ch in text)
		{
			if (ch == '.') dots++;
			else if (char.IsAsciiDigit(ch)) digits++;
			else return false;
		}
		return dots <= 1 && digits > 0;
	}

	private static bool TryParseIds(string text, out string[] ids)
	{
		if (text.Length == 0) { ids = []; return true; }
		var parts = text.Split(',');
		ids = new string[parts.Length];
		for (var index = 0; index < parts.Length; index++)
		{
			ids[index] = parts[index].Trim();
			if (!IsValidId(ids[index])) return false;
		}
		return true;
	}

	private static string Truncate(string text) => text.Length <= 40 ? text : text[..40];
}
