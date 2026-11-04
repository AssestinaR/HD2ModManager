using System.Globalization;
using System.Text.RegularExpressions;

namespace HD2ModCore.Domain;

// section 的**身份**与**指纹**。这两个是活的：`sections.json` 靠它算指纹、
// 组件树与烘焙靠它把"来源 unit 的第 3 节"对齐到"产物 unit 的第 3 节"。
//
// 它们原先与掩码模型（WardrobeMaskDocument / WardrobeMaskView）挤在同一个文件里。
// 掩码链退场时把它们拆出来——**它们与掩码无关**，是 section 表的通用工具。
//
// 三份语言（C# / Lua / Python）都按这一份规则实现。指纹一律按字符串拼接，绝不做数值运算 ——
// MaterialId 是 64 位、按小端读，一旦做位运算就很容易把高低双字写反，
// 而结果"看起来仍然像一个哈希"。

/// <summary>
/// 一个 section 的语义身份：unit + LOD0 mesh 里的 section 下标 + 内容指纹。
/// 故意不含偏移：偏移属于构建产物（<c>sections.json</c>），身份只记语义。
/// </summary>
public readonly record struct WardrobeSectionIdentity(string Unit, int SectionIndex, string Fingerprint);

/// <summary>
/// 指纹与 unit ID 的规范化。三份语言（C# / Lua / Python）都按这一份规则实现。
/// 指纹一律按字符串拼接，绝不做数值运算——MaterialId 是 64 位、按小端读，
/// 一旦做位运算就很容易把高低双字写反，而结果"看起来仍然像一个哈希"。
/// </summary>
public static partial class WardrobeSectionFingerprint
{
	/// <summary>材质缺失时的占位符。</summary>
	public const string MissingMaterial = "none";

	[GeneratedRegex("^[0-9a-f]{16}$", RegexOptions.CultureInvariant)]
	private static partial Regex UnitRegex();

	[GeneratedRegex("^(none|[0-9a-f]{16})-(\\d{1,10})$", RegexOptions.CultureInvariant)]
	private static partial Regex FingerprintRegex();

	/// <summary>由材质 ID 与三角形数构造指纹。</summary>
	public static string Format(ulong materialId, uint triangles)
		=> materialId.ToString("x16", CultureInfo.InvariantCulture)
			+ "-"
			+ triangles.ToString(CultureInfo.InvariantCulture);

	/// <summary>材质缺失时的指纹。</summary>
	public static string FormatWithoutMaterial(uint triangles)
		=> MissingMaterial + "-" + triangles.ToString(CultureInfo.InvariantCulture);

	/// <summary>三角形数由索引数推出（一个三角形三个索引）。</summary>
	public static uint TrianglesFromIndexCount(uint numIndices) => numIndices / 3;

	/// <summary>把 unit ID 规范成 16 位小写 hex（接受可选 0x 前缀）。</summary>
	public static bool TryNormalizeUnit(string? value, out string normalized)
	{
		normalized = string.Empty;
		if (string.IsNullOrWhiteSpace(value))
			return false;
		var text = value.Trim();
		if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
			text = text[2..];
		// 大小写必须归一：文件格式规定小写，但用户手改或从 JSON 里抄过来可能是大写。
		// 不归一的话同一个 unit 会变成两个键，掩码就"部分失效"——最难查的那种。
		text = text.ToLowerInvariant();
		if (!UnitRegex().IsMatch(text))
			return false;
		normalized = text;
		return true;
	}

	/// <summary>校验并规范化指纹（小写）。</summary>
	public static bool TryNormalizeFingerprint(string? value, out string normalized)
	{
		normalized = string.Empty;
		if (string.IsNullOrWhiteSpace(value))
			return false;
		var text = value.Trim().ToLowerInvariant();
		var match = FingerprintRegex().Match(text);
		if (!match.Success)
			return false;
		if (!uint.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out _))
			return false;
		normalized = text;
		return true;
	}
}