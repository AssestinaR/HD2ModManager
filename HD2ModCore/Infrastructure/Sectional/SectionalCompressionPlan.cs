using System.Text.Json;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure.Sectional;

/// <summary>压缩槽的角色。</summary>
internal enum SectionalCompressionRole
{
	/// <summary>头盔（头单独一槽）。</summary>
	Head = 0,

	/// <summary>躯干 + 胯 + 左右腿。</summary>
	Body = 1,

	/// <summary>左右臂 + 左右肩。</summary>
	Arms = 2,

	/// <summary>头 + 躯干 + 胯 + 左右腿（头身合并开：头的几何并进躯干）。</summary>
	HeadBody = 3,
}

/// <summary>压缩的来源 unit：<c>sections.json</c> 里的一行（只取规划要用的三列）。</summary>
/// <param name="FileId">16 位小写 hex。</param>
/// <param name="Part">部位；认不出来为 <c>null</c>。</param>
/// <param name="BodyType">身形（<c>Any</c>/<c>Slim</c>/<c>Stocky</c>），缺省按 Any。</param>
internal sealed record SectionalCompressionSource(string FileId, ArmorPart? Part, string? BodyType);

/// <summary>一个压缩 unit：它的 ID，以及并进来的来源部位（顺序固定，第一个就是 ID 所有者）。</summary>
internal sealed record SectionalCompressionGroup(
	SectionalCompressionRole Role,
	string UnitId,
	IReadOnlyList<SectionalCompressionMember> Members)
{
	/// <summary>ID 取自哪个部位（= 第一个成员）。</summary>
	public ArmorPart IdFrom => Members[0].Part;
}

/// <param name="Part">来源部位。</param>
/// <param name="SourceUnitId">来源里这个部位的 unit ID。</param>
internal sealed record SectionalCompressionMember(ArmorPart Part, string SourceUnitId);

/// <summary>
/// 压缩规划结果。<see cref="Supported"/> 为假时 <see cref="Reason"/> 说明为什么不压缩（调用方照常编译并把它当警告给用户）。
/// </summary>
internal sealed record SectionalCompressionPlan(
	bool Supported,
	string? Reason,
	bool HeadMerge,
	IReadOnlyList<SectionalCompressionGroup> Groups,
	IReadOnlyList<SectionalCompressionMember> Passthrough)
{
	/// <summary>3 槽或 2 槽（以实际存在的槽为准，缺部位时更少）。</summary>
	public string Mode => HeadMerge ? "two-slot" : "three-slot";

	/// <summary>至少一个槽要合并几何（成员 ≥2）。</summary>
	public bool NeedsMerge => Groups.Any(group => group.Members.Count > 1);

	/// <summary>压缩后还留在 patch 里的全部 unit ID（压缩 unit + 原样保留的披风/肩）。</summary>
	public IReadOnlyList<string> SurvivingUnitIds
		=> [.. Groups.Select(group => group.UnitId).Concat(Passthrough.Select(member => member.SourceUnitId))];

	internal static SectionalCompressionPlan NotSupported(string reason, bool headMerge)
		=> new(false, reason, headMerge, [], []);
}

/// <summary>
/// 压缩规划器（S2）：<b>只回答「哪几个来源 unit 合成哪个压缩 unit、落在哪个 ID」</b>，不做 IO。规则写在规划文档 §10.3/§10.4。
/// </summary>
internal static class SectionalCompressionPlanner
{
	private static readonly ArmorPart[] BodyOrder = [ArmorPart.Torso, ArmorPart.Hips, ArmorPart.LeftLeg, ArmorPart.RightLeg];
	// 肩并进手臂槽（用户 2026-11-14 决定）：ID 仍取左臂 → 右臂，肩排在后面，没有臂时才轮到肩。
	private static readonly ArmorPart[] ArmOrder =
		[ArmorPart.LeftArm, ArmorPart.RightArm, ArmorPart.LeftShoulder, ArmorPart.RightShoulder];

	/// <summary>规划一次。</summary>
	/// <param name="source">来源 unit。</param>
	/// <param name="headMerge">头身合并（= 2 槽）。</param>
	internal static SectionalCompressionPlan Plan(IReadOnlyList<SectionalCompressionSource> source, bool headMerge)
	{
		ArgumentNullException.ThrowIfNull(source);

		foreach (var unit in source)
		{
			if (unit.Part is null)
				return SectionalCompressionPlan.NotSupported($"来源 unit 0x{unit.FileId} 认不出是哪个部位", headMerge);
			if (ArmorStructure.ParseBody(unit.BodyType) != HD2ModAdaptation.Analysis.UnitMeshBodyVariant.Any)
				return SectionalCompressionPlan.NotSupported(
					$"来源 unit 0x{unit.FileId}（{ArmorStructure.Display(unit.Part.Value)}）是 {unit.BodyType} 分体", headMerge);
		}

		var byPart = new Dictionary<ArmorPart, string>();
		foreach (var unit in source)
		{
			if (!byPart.TryAdd(unit.Part!.Value, unit.FileId))
				return SectionalCompressionPlan.NotSupported(
					$"部位「{ArmorStructure.Display(unit.Part.Value)}」有多个来源 unit", headMerge);
		}

		List<SectionalCompressionMember> Members(IEnumerable<ArmorPart> parts)
			=> [.. parts.Where(byPart.ContainsKey).Select(part => new SectionalCompressionMember(part, byPart[part]))];

		var groups = new List<SectionalCompressionGroup>();
		void Add(SectionalCompressionRole role, List<SectionalCompressionMember> members)
		{
			if (members.Count != 0)
				groups.Add(new SectionalCompressionGroup(role, members[0].SourceUnitId, members));
		}

		if (headMerge)
		{
			// 头并进躯干：ID 所有者仍排在躯干类部位之前，头盔最后（规划 §10.4）。
			Add(SectionalCompressionRole.HeadBody, Members([.. BodyOrder, ArmorPart.Helmet]));
		}
		else
		{
			Add(SectionalCompressionRole.Head, Members([ArmorPart.Helmet]));
			Add(SectionalCompressionRole.Body, Members(BodyOrder));
		}

		Add(SectionalCompressionRole.Arms, Members(ArmOrder));

		if (groups.Count == 0)
			return SectionalCompressionPlan.NotSupported("来源里没有头、躯干、手臂类部位", headMerge);

		// 披风：不并入任何槽，原样保留（顺序按部位固定）。
		var grouped = groups.SelectMany(group => group.Members.Select(member => member.Part)).ToHashSet();
		var passthrough = ArmorStructure.All
			.Where(part => byPart.ContainsKey(part) && !grouped.Contains(part))
			.Select(part => new SectionalCompressionMember(part, byPart[part]))
			.ToArray();

		return new SectionalCompressionPlan(true, null, headMerge, groups, passthrough);
	}
}

/// <summary>
/// <c>Overwrite/low-pollution-layout.json</c>：压缩后的每个 unit 对应来源里哪些部位。S3 的软映射靠它反查。
/// <para>内容只依赖来源（槽顺序 → 部位顺序固定），同一个 Mod 重编逐字节一致。</para>
/// </summary>
internal static class LowPollutionLayoutFile
{
	internal const string FileName = "low-pollution-layout.json";
	internal const string Schema = "hd2mod.low-pollution-layout/1";

	private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

	private sealed record PartLine(string Part, string SourceUnit);

	private sealed record UnitLine(string Role, string Unit, string IdFrom, IReadOnlyList<PartLine> Parts);

	private sealed record PassthroughLine(string Part, string Unit);

	private sealed record Document(
		string Schema,
		string Mode,
		bool HeadMerge,
		IReadOnlyList<UnitLine> Units,
		IReadOnlyList<PassthroughLine> Passthrough);

	/// <summary>序列化（不含尾部换行）。</summary>
	internal static string Serialize(SectionalCompressionPlan plan)
	{
		ArgumentNullException.ThrowIfNull(plan);
		var document = new Document(
			Schema,
			plan.Mode,
			plan.HeadMerge,
			[.. plan.Groups.Select(group => new UnitLine(
				RoleName(group.Role),
				group.UnitId,
				ArmorStructure.Name(group.IdFrom),
				[.. group.Members.Select(member => new PartLine(ArmorStructure.Name(member.Part), member.SourceUnitId))]))],
			[.. plan.Passthrough.Select(member => new PassthroughLine(ArmorStructure.Name(member.Part), member.SourceUnitId))]);
		return JsonSerializer.Serialize(document, Options);
	}

	internal static string RoleName(SectionalCompressionRole role) => role switch
	{
		SectionalCompressionRole.Head => "head",
		SectionalCompressionRole.Body => "body",
		SectionalCompressionRole.Arms => "arms",
		_ => "head-body",
	};
}
