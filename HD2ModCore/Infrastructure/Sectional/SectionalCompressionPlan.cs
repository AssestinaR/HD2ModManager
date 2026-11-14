using System.Text.Json;
using HD2ModAdaptation.Analysis;
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

	/// <summary>
	/// 身体槽（body 或 head-body）+ 手臂槽合成一个（规划 §17）：躯干外只有一个独有部件的护甲用它整套映射。
	/// 额外生成，不替代任何槽；unit ID 由身体槽的 ID 所有者派生（<see cref="SectionalCompressionPlanner.DeriveBodyArmsId"/>）。
	/// </summary>
	BodyArms = 4,
}

/// <summary>压缩的来源 unit：<c>sections.json</c> 里的一行（只取规划要用的三列）。</summary>
/// <param name="FileId">16 位小写 hex。</param>
/// <param name="Part">部位；认不出来为 <c>null</c>。</param>
/// <param name="BodyType">身形（<c>Any</c>/<c>Slim</c>/<c>Stocky</c>），缺省按 Any。</param>
internal sealed record SectionalCompressionSource(string FileId, ArmorPart? Part, string? BodyType);

/// <summary>一个压缩 unit：它的 ID，以及并进来的来源部位（顺序固定，第一个就是 ID 所有者）。</summary>
/// <param name="Body">
/// 体型：没拆的槽（全 Any）是 <see cref="HD2ModAdaptation.Analysis.UnitMeshBodyVariant.Any"/>；
/// 拆开的槽（有 Stocky/Slim 成员）每个体型一个压缩 unit，Any 成员两边各并一份（规划 §13.2）。
/// </param>
internal sealed record SectionalCompressionGroup(
	SectionalCompressionRole Role,
	string UnitId,
	IReadOnlyList<SectionalCompressionMember> Members,
	HD2ModAdaptation.Analysis.UnitMeshBodyVariant Body = HD2ModAdaptation.Analysis.UnitMeshBodyVariant.Any)
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
		=> [.. Groups.Select(group => group.UnitId).Concat(Passthrough.Select(member => member.SourceUnitId)).Distinct(StringComparer.Ordinal)];

	/// <summary>至少一个槽按体型拆成了两份（决定 layout 用 /2）。</summary>
	public bool HasBodySplit => Groups.Any(group => group.Body != HD2ModAdaptation.Analysis.UnitMeshBodyVariant.Any);

	internal static SectionalCompressionPlan NotSupported(string reason, bool headMerge)
		=> new(false, reason, headMerge, [], []);
}

/// <summary>
/// 压缩规划器（S2）：<b>只回答「哪几个来源 unit 合成哪个压缩 unit、落在哪个 ID」</b>，不做 IO。规则写在规划文档 §10.3/§10.4。
/// </summary>
internal static class SectionalCompressionPlanner
{
	internal static ArmorPart[] BodyPartOrder => BodyOrder;

	/// <summary>合体 unit 的 ID：<c>SHA256("hd2mod.low-pollution.body-arms:" + 身体槽 ID 所有者的来源 ID)</c> 前 8 字节（规划 §17）。</summary>
	internal static string DeriveBodyArmsId(string ownerSourceUnitId)
	{
		var hash = System.Security.Cryptography.SHA256.HashData(
			System.Text.Encoding.UTF8.GetBytes("hd2mod.low-pollution.body-arms:" + ownerSourceUnitId.ToLowerInvariant()));
		var id = Convert.ToHexString(hash[..8]).ToLowerInvariant();
		return id == "0000000000000000" ? "0000000000000001" : id;
	}
	internal static ArmorPart[] ArmPartOrder => ArmOrder;

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

		// 来源按（部位, 体型）索引：同一体型同一部位只能有一个来源 unit（Any 同时算两个体型）。
		var byPart = new Dictionary<(ArmorPart Part, UnitMeshBodyVariant Body), string>();
		foreach (var unit in source)
		{
			if (unit.Part is null)
				return SectionalCompressionPlan.NotSupported($"来源 unit 0x{unit.FileId} 认不出是哪个部位", headMerge);
			var body = ArmorStructure.ParseBody(unit.BodyType);
			if (body is not (UnitMeshBodyVariant.Any or UnitMeshBodyVariant.Stocky or UnitMeshBodyVariant.Slim))
				return SectionalCompressionPlan.NotSupported(
					$"来源 unit 0x{unit.FileId}（{ArmorStructure.Display(unit.Part.Value)}）是 {unit.BodyType} 分体", headMerge);
			if (!byPart.TryAdd((unit.Part.Value, body), unit.FileId))
				return SectionalCompressionPlan.NotSupported(
					body == UnitMeshBodyVariant.Any
						? $"部位「{ArmorStructure.Display(unit.Part.Value)}」有多个来源 unit"
						: $"部位「{ArmorStructure.Display(unit.Part.Value)}」的{ArmorStructure.DisplayBody(body)}体型有多个来源 unit", headMerge);
		}

		foreach (var part in byPart.Keys.Select(key => key.Part).Distinct())
		{
			if (!byPart.ContainsKey((part, UnitMeshBodyVariant.Any))) continue;
			foreach (var body in new[] { UnitMeshBodyVariant.Stocky, UnitMeshBodyVariant.Slim })
			{
				if (byPart.ContainsKey((part, body)))
					return SectionalCompressionPlan.NotSupported(
						$"部位「{ArmorStructure.Display(part)}」的{ArmorStructure.DisplayBody(body)}体型同时有通用和专属两个来源 unit", headMerge);
			}
		}

		var groups = new List<SectionalCompressionGroup>();
		void Add(SectionalCompressionRole role, IReadOnlyList<ArmorPart> order)
		{
			string? Find(ArmorPart part, UnitMeshBodyVariant body) => byPart.TryGetValue((part, body), out var id) ? id : null;

			var split = order.Any(part => Find(part, UnitMeshBodyVariant.Stocky) is not null || Find(part, UnitMeshBodyVariant.Slim) is not null);
			if (!split)
			{
				var members = order.Where(part => Find(part, UnitMeshBodyVariant.Any) is not null)
					.Select(part => new SectionalCompressionMember(part, Find(part, UnitMeshBodyVariant.Any)!)).ToList();
				if (members.Count != 0)
					groups.Add(new SectionalCompressionGroup(role, members[0].SourceUnitId, members));
				return;
			}

			// 拆开的槽：壮实 = 壮实成员 + Any 成员，苗条 = 苗条成员 + Any 成员；ID 取该体型第一个专属成员，没有则第一个 Any 成员。
			foreach (var body in new[] { UnitMeshBodyVariant.Stocky, UnitMeshBodyVariant.Slim })
			{
				var specific = order.Where(part => Find(part, body) is not null)
					.Select(part => new SectionalCompressionMember(part, Find(part, body)!)).ToList();
				var any = order.Where(part => Find(part, UnitMeshBodyVariant.Any) is not null)
					.Select(part => new SectionalCompressionMember(part, Find(part, UnitMeshBodyVariant.Any)!)).ToList();
				var owner = specific.Count != 0 ? specific[0] : any.Count != 0 ? any[0] : null;
				if (owner is null) continue;
				// ID 所有者放最前，其余按部位顺序（Any 成员两边各并一份）。
				var members = new List<SectionalCompressionMember> { owner };
				members.AddRange(order
					.Select(part => specific.FirstOrDefault(member => member.Part == part) ?? any.FirstOrDefault(member => member.Part == part))
					.Where(member => member is not null && member != owner)!);
				groups.Add(new SectionalCompressionGroup(role, owner.SourceUnitId, members, body));
			}
		}

		if (headMerge)
		{
			// 头并进躯干：ID 所有者仍排在躯干类部位之前，头盔最后（规划 §10.4）。
			Add(SectionalCompressionRole.HeadBody, [.. BodyOrder, ArmorPart.Helmet]);
		}
		else
		{
			Add(SectionalCompressionRole.Head, [ArmorPart.Helmet]);
			Add(SectionalCompressionRole.Body, BodyOrder);
		}

		Add(SectionalCompressionRole.Arms, ArmOrder);

		// 身体 + 手臂合体（规划 §17）：两槽都在时额外生成，成员与 ID 所有者同身体槽，ID 改用派生值（来源 ID 已被身体槽占用）。
		var bodyRole = headMerge ? SectionalCompressionRole.HeadBody : SectionalCompressionRole.Body;
		if (groups.Any(group => group.Role == bodyRole) && groups.Any(group => group.Role == SectionalCompressionRole.Arms))
		{
			var before = groups.Count;
			Add(SectionalCompressionRole.BodyArms, headMerge ? [.. BodyOrder, ArmorPart.Helmet, .. ArmOrder] : [.. BodyOrder, .. ArmOrder]);
			for (var at = before; at < groups.Count; at++)
				groups[at] = groups[at] with { UnitId = DeriveBodyArmsId(groups[at].UnitId) };
		}

		if (groups.Count == 0)
			return SectionalCompressionPlan.NotSupported("来源里没有头、躯干、手臂类部位", headMerge);

		// 披风：不并入任何槽，原样保留（顺序按部位固定）。
		var grouped = groups.SelectMany(group => group.Members.Select(member => member.Part)).ToHashSet();
		var passthrough = ArmorStructure.All
			.Where(part => !grouped.Contains(part))
			.SelectMany(part => new[] { UnitMeshBodyVariant.Any, UnitMeshBodyVariant.Stocky, UnitMeshBodyVariant.Slim }
				.Where(body => byPart.ContainsKey((part, body)))
				.Select(body => new SectionalCompressionMember(part, byPart[(part, body)])))
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
	/// <summary>没有任何槽按体型拆开时写的 schema（全 Any 素体，字节与 S2 起一直一样）。</summary>
	internal const string Schema = "hd2mod.low-pollution-layout/1";

	/// <summary>有槽按体型拆开时写的 schema：每个 unit 多一个 <c>body</c>（any / stocky / slim）。读取器两个都认。</summary>
	internal const string SchemaV2 = "hd2mod.low-pollution-layout/2";

	private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
	{
		WriteIndented = true,
		DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
	};

	private sealed record PartLine(string Part, string SourceUnit);

	private sealed record UnitLine(string Role, string Unit, string IdFrom, IReadOnlyList<PartLine> Parts, string? Body = null);

	private sealed record PassthroughLine(string Part, string Unit);

	private sealed record Document(
		string Schema,
		string Mode,
		bool HeadMerge,
		IReadOnlyList<UnitLine> Units,
		IReadOnlyList<PassthroughLine> Passthrough,
		string? Fingerprint = null);

	/// <summary>序列化（不含尾部换行）。</summary>
	internal static string Serialize(SectionalCompressionPlan plan, string? fingerprint = null)
	{
		ArgumentNullException.ThrowIfNull(plan);
		// 全 Any 素体仍写 /1、不带 body，字节与此前完全一致（已部署的映射和指纹都不受影响）。
		var split = plan.HasBodySplit;
		var document = new Document(
			split ? SchemaV2 : Schema,
			plan.Mode,
			plan.HeadMerge,
			[.. plan.Groups.Select(group => new UnitLine(
				RoleName(group.Role),
				group.UnitId,
				ArmorStructure.Name(group.IdFrom),
				[.. group.Members.Select(member => new PartLine(ArmorStructure.Name(member.Part), member.SourceUnitId))],
				split ? ArmorStructure.BodyName(group.Body) : null))],
			[.. plan.Passthrough.Select(member => new PassthroughLine(ArmorStructure.Name(member.Part), member.SourceUnitId))],
			fingerprint);
		return JsonSerializer.Serialize(document, Options);
	}

	internal static string RoleName(SectionalCompressionRole role) => role switch
	{
		SectionalCompressionRole.Head => "head",
		SectionalCompressionRole.Body => "body",
		SectionalCompressionRole.Arms => "arms",
		SectionalCompressionRole.BodyArms => "body-arms",
		_ => "head-body",
	};
}
