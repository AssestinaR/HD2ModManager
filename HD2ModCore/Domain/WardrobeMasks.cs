using System.Globalization;
using System.Text.RegularExpressions;

namespace HD2ModCore.Domain;

// 衣柜 section 掩码的领域模型（交接文档 §4.1 的"三层掩码"）。
// 这里只表达"哪个 section 被禁用"，不表达"怎么写到内存"——后者是运行时的职责。
// Wardrobe section mask domain model (the three-layer mask from the handoff, §4.1).
// It expresses *which* section is disabled, never *how* to write memory; that is the runtime's job.

/// <summary>
/// 目标的掩码模式。inherit = 实时继承档案级来源掩码；override = 只用自己的独立掩码。
/// </summary>
public enum WardrobeMaskMode
{
	Inherit,
	Override,
}

/// <summary>
/// 一个 section 的语义身份：unit + LOD0 mesh 里的 section 下标 + 内容指纹。
/// 故意不含偏移：偏移属于构建产物（sections.json），掩码只记语义。
/// </summary>
public readonly record struct WardrobeSectionIdentity(string Unit, int SectionIndex, string Fingerprint);

/// <summary>档案级（来源）掩码条目。只记录"禁用"。</summary>
public sealed record WardrobeSourceMaskEntry(string Unit, int SectionIndex, string Fingerprint)
{
	public WardrobeSectionIdentity Identity => new(Unit, SectionIndex, Fingerprint);
}

/// <summary>映射目标独立掩码条目。只记录"禁用"。</summary>
public sealed record WardrobeTargetMaskEntry(uint TargetItemId, string Unit, int SectionIndex, string Fingerprint)
{
	public WardrobeSectionIdentity Identity => new(Unit, SectionIndex, Fingerprint);
}

/// <summary>
/// <c>wardrobe.masks</c> 的内存表示。内容必须与文件一一对应，排序在序列化时统一做。
/// </summary>
public sealed record WardrobeMaskDocument(
	int Revision,
	bool Enabled,
	IReadOnlyList<WardrobeSourceMaskEntry> SourceMasks,
	IReadOnlyList<WardrobeTargetMaskEntry> TargetMasks,
	IReadOnlyDictionary<uint, WardrobeMaskMode> TargetModes)
{
	/// <summary>文件不存在时的等价物：全启用、无条目。</summary>
	public static WardrobeMaskDocument Empty { get; } = new(
		0,
		true,
		Array.Empty<WardrobeSourceMaskEntry>(),
		Array.Empty<WardrobeTargetMaskEntry>(),
		new Dictionary<uint, WardrobeMaskMode>());

	public bool IsEmpty => SourceMasks.Count == 0 && TargetMasks.Count == 0 && TargetModes.Count == 0;
}

/// <summary>
/// 指纹与 unit ID 的规范化。三份语言（C# / Lua / Python）都按这一份规则实现。
/// 指纹一律按字符串拼接，绝不做数值运算——MaterialId 是 64 位、按小端读，
/// 一旦做位运算就很容易把高低双字写反，而结果"看起来仍然像一个哈希"。
/// </summary>
public static partial class WardrobeSectionFingerprint
{
	/// <summary>材质缺失时的占位符。</summary>
	public const string MissingMaterial = "none";

	private const string UnitPattern = "^[0-9a-f]{16}$";

	[GeneratedRegex(UnitPattern, RegexOptions.CultureInvariant)]
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

/// <summary>
/// 把掩码文档编译成查表结构。编辑器和运行时都用同一套生效规则，
/// 避免"界面显示已关、运行时仍可见"这类两边各写一遍导致的偏差。
/// </summary>
public sealed class WardrobeMaskView
{
	private readonly HashSet<(string Unit, int SectionIndex, string Fingerprint)> source;
	private readonly Dictionary<uint, HashSet<(string Unit, int SectionIndex, string Fingerprint)>> targets;
	private readonly Dictionary<uint, WardrobeMaskMode> modes;

	private WardrobeMaskView(
		bool enabled,
		HashSet<(string, int, string)> source,
		Dictionary<uint, HashSet<(string, int, string)>> targets,
		Dictionary<uint, WardrobeMaskMode> modes)
	{
		Enabled = enabled;
		this.source = source;
		this.targets = targets;
		this.modes = modes;
	}

	/// <summary>掩码总开关。关闭时一切视为启用，但内容保留。</summary>
	public bool Enabled { get; }

	public static WardrobeMaskView Create(WardrobeMaskDocument document)
	{
		ArgumentNullException.ThrowIfNull(document);
		var source = new HashSet<(string, int, string)>();
		foreach (var entry in document.SourceMasks)
			source.Add((entry.Unit, entry.SectionIndex, entry.Fingerprint));
		var targets = new Dictionary<uint, HashSet<(string, int, string)>>();
		foreach (var entry in document.TargetMasks)
		{
			if (!targets.TryGetValue(entry.TargetItemId, out var set))
			{
				set = new HashSet<(string, int, string)>();
				targets.Add(entry.TargetItemId, set);
			}
			set.Add((entry.Unit, entry.SectionIndex, entry.Fingerprint));
		}
		var modes = new Dictionary<uint, WardrobeMaskMode>(document.TargetModes);
		return new WardrobeMaskView(document.Enabled, source, targets, modes);
	}

	/// <summary>目标的掩码模式；未声明时为 inherit。</summary>
	public WardrobeMaskMode ModeOf(uint targetItemId)
		=> modes.TryGetValue(targetItemId, out var mode) ? mode : WardrobeMaskMode.Inherit;

	/// <summary>档案级掩码是否禁用了该 section。</summary>
	public bool IsSourceDisabled(string unit, int sectionIndex, string fingerprint)
		=> Enabled && source.Contains((unit, sectionIndex, fingerprint));

	/// <summary>
	/// 对某个映射目标，该 section 是否被禁用。
	/// override 时**不再**继承来源掩码——这是交接文档 §4.1 的明确规则，不是叠加。
	/// </summary>
	public bool IsDisabled(uint targetItemId, string unit, int sectionIndex, string fingerprint)
	{
		if (!Enabled)
			return false;
		if (ModeOf(targetItemId) == WardrobeMaskMode.Override)
			return targets.TryGetValue(targetItemId, out var set)
				&& set.Contains((unit, sectionIndex, fingerprint));
		return source.Contains((unit, sectionIndex, fingerprint));
	}

	/// <summary>
	/// 整体禁用判定：一个 unit 的**全部** section 都被禁用时返回 true，
	/// 运行时据此不注册该 unit 的 Piece。空列表返回 false——没有 section 不等于"全关"。
	/// </summary>
	public bool IsUnitFullyDisabled(uint targetItemId, string unit, IReadOnlyList<WardrobeSectionIdentity> sections)
	{
		ArgumentNullException.ThrowIfNull(sections);
		if (sections.Count == 0 || !Enabled)
			return false;
		foreach (var section in sections)
		{
			if (!string.Equals(section.Unit, unit, StringComparison.Ordinal))
				return false;
			if (!IsDisabled(targetItemId, section.Unit, section.SectionIndex, section.Fingerprint))
				return false;
		}
		return true;
	}
}
