using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace HD2ModCore.Domain;

// section 组的领域模型（落盘在 `components.json`，文件名是历史包袱，见 SectionGroupFile）。
//
// 它回答一个问题：**这个 Mod 有哪些"有名字的可开关部件"，每个部件由哪几块几何组成。**
//
// 为什么是独立文件而不是塞进 sections.json：
//   · sections.json 描述"这个 patch 里有什么"（构建产物，偏移与指纹），
//   · components.json 描述"用户能关什么"（源侧意图 + 用户手建，跨 patch 汇总）。
// 两者的失效条件不同：重建 patch 会让前者全变、后者大多不变。混在一起会让"只想改组名字"
// 变成"重写整份偏移表"。
//
// 为什么成员存 (unit, section 下标) 而不是 section 身份：
//   这份文件是**刚转换完就写**的，那时 sections.json 就在旁边，身份与下标一一对应；
//   而烘焙只需要下标（numIndices 的落点由 sections.json 提供）。存身份要多一次对齐，
//   换来的能力（跨重建复用）本来也不成立——重建后几何全变，组必须重转。
//   ⚠️ 这条在「拆件」之后会变：一次追加就可能重排目标原有的 section 边界，
//   所以拆件产物必须走来源身份（见 sectional-u0-findings §6.4）。这是 U3 的事。
//
// **开关的方向是统一的：组"开着" = 它的成员几何画出来。**
// 源侧 `$no_draw` 包与后置 unit 吸收出来的组都按这个方向表达，
// 界面因此不需要区分"这个组是隐藏型还是显示型"。

/// <summary>组是怎么来的。界面要能告诉用户"这条是自动识别的还是我自己建的"。</summary>
public enum SectionGroupSource
{
	/// <summary>源侧 <c>$no_draw</c> 覆盖包翻译出来的（默认值，兼容老文件）。</summary>
	NoDraw = 0,

	/// <summary>后置 patch 的独立 unit 被拆件吸收进来的。</summary>
	DonorUnit = 1,

	/// <summary>用户手建的。</summary>
	Manual = 2,

	/// <summary>
	/// 从模型自带的 <c>$BodyGroup</c>（可切换 bodypart 的多个变体）推导出来的。
	/// <para>
	/// 它和 <see cref="NoDraw"/> 描述的是同一类东西（"哪些几何算一个开关"），但数据来源不同：
	/// <c>$no_draw</c> 是**另一个 VPK** 里的材质覆盖，而 bodygroup 就在模型自己里面。
	/// 所以这类组不需要任何外部文件跟着 Mod 走，重建时永远能重新推导出来。
	/// </para>
	/// </summary>
	BodyGroup = 3,
}

/// <summary>
/// 组里的一个成员：某个 unit 的哪几节。<c>Unit</c> 是 16 位小写 hex（与 <c>sections.json</c> 同口径）。
/// </summary>
public sealed record SectionGroupTarget(string Unit, IReadOnlyList<int> Sections);

/// <summary>
/// 一个 section 组：名字 + 它的成员 + 溯源信息。
/// <para>
/// <see cref="Materials"/> 是源侧材质裸名，保留它是为了"这个组凭什么包含这块"可回答——
/// 用户报"关了猫耳结果耳朵还在"时，要先看材质有没有对上。
/// </para>
/// <para>
/// <see cref="ExclusiveGroup"/> 是互斥组名：同名互斥组内**最多一个**处于开启状态。
/// 它一次解决两件事——同一槽位多个预装变体只能选一个，以及贴图覆盖型选项的冲突。
/// </para>
/// </summary>
public sealed record SectionGroup(
	string Name,
	string SourceVpk,
	IReadOnlyList<string> Materials,
	IReadOnlyList<SectionGroupTarget> Members)
{
	/// <summary>
	/// 稳定 id。<b>改名不变</b>，所以它才是界面选中态、互斥关系、将来任何按组存的东西的锚。
	/// 空表示这份文件是旧格式写的——用 <see cref="EffectiveId"/> 现算一个，不需要迁移。
	/// </summary>
	public string Id { get; init; } = string.Empty;

	/// <summary>来源。旧文件没有这个字段，默认按 <see cref="SectionGroupSource.NoDraw"/> 读。</summary>
	public SectionGroupSource Source { get; init; } = SectionGroupSource.NoDraw;

	/// <summary>互斥组名；null = 不参与互斥。</summary>
	public string? ExclusiveGroup { get; init; }

	/// <summary>这个组实际覆盖多少块几何。0 表示没匹配上，界面不该把它画成可用组。</summary>
	public int SectionCount => Members.Sum(target => target.Sections.Count);

	/// <summary>
	/// 有效 id：显式给了就用它，否则按 <c>(Name, 成员掩码)</c> 现算一个稳定值。
	/// <para>
	/// <b>为什么允许现算。</b>库里已有 9 份旧格式文件，它们没有 id 字段。
	/// 现算比"读的时候迁移一遍"安全：迁移要写文件，而写文件在只读载荷目录里是有代价的操作。
	/// 而且今天没有任何持久化状态按组 id 存（开关状态是按 (unit, section) 存的），
	/// 所以现算的 id 只要在同一份文件内稳定就够了；一旦第一次保存，真 id 就落盘了。
	/// </para>
	/// </summary>
	public string EffectiveId => string.IsNullOrEmpty(Id) ? SectionGroupIdentity.Derive(Name, Members) : Id;
}

/// <summary>组 id 的推导规则。三处（读旧文件、建新组、测试）必须用同一份，否则会出现"两个 id 指向同一个组"。</summary>
public static class SectionGroupIdentity
{
	/// <summary>id 前缀。让人一眼看出这是组 id 而不是 unit id。</summary>
	public const string Prefix = "g";

	/// <summary>
	/// 由 <c>(名字, 成员掩码)</c> 推出一个稳定 id。
	/// <para>
	/// 用 SHA256 前 12 个 hex 而不是序号：序号会在"删一个组再加一个"之后撞上旧 id，
	/// 而按内容算的 id 只有内容完全相同才会撞——那种情况本来就是同一个组。
	/// 成员顺序先规范化，所以"先加 A 再加 B"和"先加 B 再加 A"得到同一个 id。
	/// </para>
	/// </summary>
	public static string Derive(string name, IReadOnlyList<SectionGroupTarget> members)
	{
		ArgumentNullException.ThrowIfNull(members);
		var builder = new StringBuilder();
		builder.Append(name ?? string.Empty);
		builder.Append('|');
		foreach (var target in members
			.OrderBy(target => target.Unit, StringComparer.Ordinal)
			.ThenBy(target => target.Sections.Count))
		{
			builder.Append(target.Unit);
			builder.Append(':');
			foreach (var section in target.Sections.Order())
				builder.Append(section.ToString(CultureInfo.InvariantCulture)).Append(',');
			builder.Append(';');
		}

		var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
		return Prefix + Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
	}
}

/// <summary>
/// 识别到、但没能落成组的包（材质在 patch 里找不到）。
/// <para>
/// <b>必须报出来。</b>用户装了"猫耳关"却看不到组，只会以为功能没做；
/// 说清"它藏的是 <c>xxx</c>，这个 patch 里没有这个材质"才是可照做的信息。
/// </para>
/// </summary>
public sealed record SectionGroupUnmatched(string Name, string SourceVpk, string Reason);

/// <summary><c>components.json</c> 的内存表示。<see cref="Schema"/> 是版本闸门。</summary>
public sealed record SectionGroupDocument(
	string Schema,
	IReadOnlyList<SectionGroup> Groups,
	IReadOnlyList<SectionGroupUnmatched> Unmatched)
{
	/// <summary>当前格式标识。</summary>
	public const string CurrentSchema = "hd2mod.section-groups/1";

	/// <summary>
	/// 旧格式标识（"组件开关"时代）。**只读不写**：读进来就升成当前格式，
	/// 下次保存自动变成新格式。这样 9 份已有文件不用迁移脚本。
	/// </summary>
	public const string LegacySchema = "hd2mod.vpk-components/1";

	/// <summary>能读的格式。顺序即优先级，第一项是写出时用的。</summary>
	public static IReadOnlyList<string> ReadableSchemas { get; } = [CurrentSchema, LegacySchema];

	public static SectionGroupDocument Empty { get; } = new(CurrentSchema, [], []);

	/// <summary>按名字找组；找不到返回 null。改名要走 id，不要走名字。</summary>
	public SectionGroup? FindById(string id)
		=> Groups.FirstOrDefault(group => string.Equals(group.EffectiveId, id, StringComparison.Ordinal));

	/// <summary>
	/// 同一互斥组里已经开着的那些组。<paramref name="isEnabled"/> 由调用方给
	/// （开关状态在 <c>wardrobe.json</c>，不在本文件里）。
	/// </summary>
	public IReadOnlyList<SectionGroup> EnabledInExclusiveGroup(string exclusiveGroup, Func<SectionGroup, bool> isEnabled)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(exclusiveGroup);
		ArgumentNullException.ThrowIfNull(isEnabled);
		return
		[
			.. Groups.Where(group =>
				string.Equals(group.ExclusiveGroup, exclusiveGroup, StringComparison.Ordinal)
				&& isEnabled(group)),
		];
	}
}
