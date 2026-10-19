using System.Globalization;
using System.Text.RegularExpressions;

namespace HD2ModCore.Domain;

// 衣柜 section 掩码的领域模型（交接文档 §4.1 的"三层掩码"）。
//
// 关键取舍：编译产物里**不做继承**。
// 管理器里的权威数据是 mod 库内的未编译源文件——视觉来源保存自己的禁用状态，
// 映射目标除非被编辑器显式声明为"例外"，否则复用视觉来源。那份数据里"继承"是
// 一个活的关系（来源改了目标立刻跟着变），只有管理器需要知道。
//
// 编译产物是给 Lua 读的**扁平投影**：一个目标对应一个掩码集，掩码集里就是
// 最终要隐藏的那些 section。这样做的原因是"多个视觉来源"——不同目标可以映射到
// 不同的来源，全局只有一份来源掩码根本表达不了。运行时因此也不需要任何
// inherit/override 判断，只有两次查表。
//
// The compiled artefact does no inheritance. The authoritative data lives in the mod
// library's uncompiled source, where "inherit the visual source" is a live relation the
// manager maintains. What ships to Lua is a flat projection: target -> mask set. That is
// what makes multiple visual sources expressible at all, and it leaves the runtime with
// two table lookups and no inheritance rules to get wrong.

/// <summary>
/// 一个 section 的语义身份：unit + LOD0 mesh 里的 section 下标 + 内容指纹。
/// 故意不含偏移：偏移属于构建产物（sections.json），掩码只记语义。
/// </summary>
public readonly record struct WardrobeSectionIdentity(string Unit, int SectionIndex, string Fingerprint);

/// <summary>掩码集里的一条：某个集合要隐藏的某个 section。</summary>
public sealed record WardrobeMaskEntry(uint SetId, string Unit, int SectionIndex, string Fingerprint)
{
	public WardrobeSectionIdentity Identity => new(Unit, SectionIndex, Fingerprint);
}

/// <summary>
/// <c>wardrobe.masks</c> 的内存表示。内容必须与文件一一对应，排序在序列化时统一做。
/// </summary>
public sealed record WardrobeMaskDocument(
	int Revision,
	bool Enabled,
	IReadOnlyList<WardrobeMaskEntry> Entries,
	IReadOnlyDictionary<uint, uint> TargetSets)
{
	/// <summary>文件不存在时的等价物：全启用、无条目、无目标关联。</summary>
	public static WardrobeMaskDocument Empty { get; } = new(
		0,
		true,
		Array.Empty<WardrobeMaskEntry>(),
		new Dictionary<uint, uint>());

	public bool IsEmpty => Entries.Count == 0 && TargetSets.Count == 0;
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

/// <summary>
/// 把掩码文档编译成查表结构。编辑器和运行时都用同一套生效规则，
/// 避免"界面显示已关、运行时仍可见"这类两边各写一遍导致的偏差。
/// </summary>
public sealed class WardrobeMaskView
{
	private readonly Dictionary<uint, HashSet<(string Unit, int SectionIndex, string Fingerprint)>> sets;
	private readonly Dictionary<uint, uint> targetSets;

	private WardrobeMaskView(
		bool enabled,
		Dictionary<uint, HashSet<(string, int, string)>> sets,
		Dictionary<uint, uint> targetSets)
	{
		Enabled = enabled;
		this.sets = sets;
		this.targetSets = targetSets;
	}

	/// <summary>掩码总开关。关闭时一切视为启用，但内容保留。</summary>
	public bool Enabled { get; }

	public static WardrobeMaskView Create(WardrobeMaskDocument document)
	{
		ArgumentNullException.ThrowIfNull(document);
		var sets = new Dictionary<uint, HashSet<(string, int, string)>>();
		foreach (var entry in document.Entries)
		{
			if (!sets.TryGetValue(entry.SetId, out var bucket))
			{
				bucket = new HashSet<(string, int, string)>();
				sets.Add(entry.SetId, bucket);
			}
			bucket.Add((entry.Unit, entry.SectionIndex, entry.Fingerprint));
		}
		return new WardrobeMaskView(document.Enabled, sets, new Dictionary<uint, uint>(document.TargetSets));
	}

	/// <summary>目标关联的掩码集；未关联时返回 null（= 不隐藏任何 section）。</summary>
	public uint? SetOf(uint targetItemId)
		=> targetSets.TryGetValue(targetItemId, out var setId) ? setId : null;

	/// <summary>某个掩码集是否隐藏了该 section。空集恒为 false。</summary>
	public bool IsDisabledInSet(uint setId, string unit, int sectionIndex, string fingerprint)
		=> Enabled
			&& sets.TryGetValue(setId, out var bucket)
			&& bucket.Contains((unit, sectionIndex, fingerprint));

	/// <summary>
	/// 对某个映射目标，该 section 是否被禁用。
	/// 目标没有关联掩码集时一律可见——这正是"显式声明了例外但一条都没关"的落点。
	/// </summary>
	public bool IsDisabled(uint targetItemId, string unit, int sectionIndex, string fingerprint)
	{
		if (!Enabled)
			return false;
		var setId = SetOf(targetItemId);
		return setId is not null && IsDisabledInSet(setId.Value, unit, sectionIndex, fingerprint);
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
