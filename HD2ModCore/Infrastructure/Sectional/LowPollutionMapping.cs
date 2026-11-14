using System.Globalization;
using System.Text.Json;
using HD2ModAdaptation.Analysis;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure.KitInstance;

namespace HD2ModCore.Infrastructure.Sectional;

/// <summary>压缩 unit 的角色名（<c>low-pollution-layout.json</c> 的 <c>role</c>）。</summary>
public static class LowPollutionRoles
{
	public const string Head = "head";
	public const string Body = "body";
	public const string Arms = "arms";
	public const string HeadBody = "head-body";
}

/// <summary>布局文件里的一个压缩 unit。</summary>
public sealed record LowPollutionLayoutUnit(string Role, string Unit);

/// <summary>
/// <c>Overwrite/low-pollution-layout.json</c> 的读出结果（P6 S2 写、S3 读）。
/// S3 只需要：几槽、头身合并与否、每个角色对应的压缩 unit ID。
/// </summary>
public sealed record LowPollutionLayout(string Mode, bool HeadMerge, IReadOnlyList<LowPollutionLayoutUnit> Units, string? Fingerprint = null)
{
	/// <summary>某角色的压缩 unit ID（16 位小写 hex）；没有这个槽时为 <c>null</c>。</summary>
	public string? UnitOf(string role) => Units.FirstOrDefault(unit => unit.Role == role)?.Unit;

	/// <summary>布局里的全部压缩 unit。</summary>
	public IEnumerable<string> AllUnits => Units.Select(unit => unit.Unit);
}

public enum LowPollutionMappingStatus
{
	/// <summary>能映射：写映射行，并生成 Hider 规则。</summary>
	Mapped = 0,

	/// <summary>低污染模式下不可（缺独有承载件等）：不写映射，也不退回全量映射。</summary>
	Unavailable = 1,

	/// <summary>压缩 unit 还没部署进游戏数据：不写映射（否则军械库会崩）。</summary>
	NotDeployed = 2,
}

/// <summary>承载件 → 压缩 unit 的一行映射（16 位小写 hex）。</summary>
public sealed record LowPollutionLink(string Carrier, string Unit);

/// <summary>
/// 一个映射目标在低污染模式下的规划结果（规划文档 §11.2）。
/// </summary>
/// <param name="Links">只发承载件：<c>link=承载件,压缩 unit</c>。</param>
/// <param name="Hide">要交给 Hider 在这套的实例上隐藏的原版部件（目标全部非披风部件 − 承载件，2 槽加原版头盔）。</param>
/// <param name="Signature">Hider 规则的签名（承载件原 ID，两种身形各有一个承载件时有两个）。</param>
/// <param name="UnitSignature">备用签名：压缩 unit 的 ID（防 <c>resource_name</c> 返回覆盖后的 ID）。</param>
public sealed record LowPollutionMappingPlan(
	uint TargetItemId,
	string TargetName,
	string ModelKey,
	LowPollutionMappingStatus Status,
	string? Reason,
	IReadOnlyList<LowPollutionLink> Links,
	IReadOnlyList<string> Hide,
	IReadOnlyList<string> Signature,
	IReadOnlyList<string> UnitSignature,
	IReadOnlyList<string> Notes)
{
	internal static LowPollutionMappingPlan Fail(
		uint itemId, string name, string modelKey, LowPollutionMappingStatus status, string reason)
		=> new(itemId, name, modelKey, status, reason, [], [], [], [], []);
}

/// <summary>
/// 低污染映射规划器（P6 S3，规划 §11.2）：只回答「这个目标的哪些独有部件承载压缩 unit、哪些部件交给 Hider 隐藏」，纯函数，不碰盘。
/// <para>
/// 「独有」口径与专属补丁路线共用 <see cref="SectionalExclusivePlanner"/>：同名组放宽（<c>GroupItems</c> / <c>IsGroupUnique</c>）、
/// 配套头盔（<c>HelmetKitsOf</c>）、换色孪生文案（<c>TwinNamesOf</c>），不另写一套。
/// </para>
/// </summary>
internal static class LowPollutionMappingPlanner
{
	private const ulong UnitTypeId = 0xe0a48d0be9a7453f;

	private static readonly IReadOnlySet<uint> EmptySet = new HashSet<uint>();

	/// <summary>头盔部件「独有」的拥有者集合：同套 / 同名的头盔与护甲记录（口径同专属路线合并那一遍的 HelmetUnique）。</summary>
	private static HashSet<uint> HelmetOwners(SectionalExclusivePlanner.Index index, GameDataAuthorityKit helmet)
		=> index.Kits
			.Where(other => string.Equals(other.DisplayName, helmet.DisplayName, StringComparison.Ordinal)
				|| (helmet.SetId != 0 && other.SetId == helmet.SetId))
			.Select(other => other.ItemId)
			.Append(helmet.ItemId)
			.ToHashSet();

	/// <summary>手臂槽承载件的优先顺序：手臂、肩，再到胯和腿；躯干与头盔槽不能放（第一人称下游戏隐藏它们，手臂要看得见）。</summary>
	private static readonly ArmorPart[] ArmsCarrierPreference =
	[
		ArmorPart.LeftArm, ArmorPart.RightArm, ArmorPart.LeftShoulder, ArmorPart.RightShoulder,
		ArmorPart.Hips, ArmorPart.LeftLeg, ArmorPart.RightLeg,
	];

	/// <param name="companions">
	/// 同一次应用里映射到<b>同一个素体</b>的其它目标（item id）。2 槽组合（护甲 + 头盔）要靠它认出「同套头盔也在映射」：
	/// 头已并进身体，头盔的独有部件空着，手臂槽可以放那里（规划 §11.2，Q1）。
	/// </param>
	internal static LowPollutionMappingPlan Plan(
		SectionalExclusivePlanner.Index index, LowPollutionLayout layout, uint itemId, string modelKey,
		IReadOnlySet<uint>? companions = null)
	{
		ArgumentNullException.ThrowIfNull(index);
		ArgumentNullException.ThrowIfNull(layout);
		var kit = index.Kits.FirstOrDefault(candidate => candidate.ItemId == itemId);
		if (kit is null)
			return LowPollutionMappingPlan.Fail(itemId, string.Empty, modelKey, LowPollutionMappingStatus.Unavailable, "索引里找不到目标记录。");
		return kit.Kind switch
		{
			GameDataEquipmentKitKind.Armor => PlanArmor(index, layout, kit, modelKey, companions ?? EmptySet),
			GameDataEquipmentKitKind.Helmet => PlanHelmet(index, layout, kit, modelKey, companions ?? EmptySet),
			_ => LowPollutionMappingPlan.Fail(itemId, kit.DisplayName, modelKey, LowPollutionMappingStatus.Unavailable, "目标不是护甲或头盔。"),
		};
	}

	private static LowPollutionMappingPlan Unavailable(GameDataAuthorityKit kit, string modelKey, string reason)
		=> LowPollutionMappingPlan.Fail(kit.ItemId, kit.DisplayName, modelKey, LowPollutionMappingStatus.Unavailable, reason);

	private static string Describe(IReadOnlyList<string> twins)
		=> $"和 {string.Join("、", twins.Take(3))}{(twins.Count > 3 ? " 等" : string.Empty)} 用的是同一套模型，只有颜色不同，没有部件能把它们分开";

	private static int SlotOf(ArmorPart part) => (int)(ArmorStructure.SlotOf(part) ?? 0u);

	private static LowPollutionMappingPlan PlanArmor(
		SectionalExclusivePlanner.Index index, LowPollutionLayout layout, GameDataAuthorityKit kit, string modelKey,
		IReadOnlySet<uint> companions)
	{
		var bodyUnit = layout.UnitOf(LowPollutionRoles.HeadBody) ?? layout.UnitOf(LowPollutionRoles.Body);
		if (bodyUnit is null)
			return Unavailable(kit, modelKey, "素体的压缩布局里没有躯干槽（body / head-body）。");
		var armsUnit = layout.UnitOf(LowPollutionRoles.Arms);

		var capeSlot = SlotOf(ArmorPart.Cape);
		var helmetSlot = SlotOf(ArmorPart.Helmet);
		var torsoSlot = SlotOf(ArmorPart.Torso);
		var groupItems = SectionalExclusivePlanner.GroupItems(index, kit);
		var tUnits = SectionalExclusivePlanner.UnitsOf(kit)
			.Where(unit => unit.Type == UnitTypeId && unit.Slot != capeSlot)
			.ToArray();
		var bodies = Enumerable.Range(0, 2).Where(body => tUnits.Any(unit => SectionalExclusivePlanner.Covers(unit.RawBody, body))).ToArray();
		if (bodies.Length == 0)
			return Unavailable(kit, modelKey, "目标记录没有可承载的部件。");

		bool Unique(SectionalExclusivePlanner.Unit unit) => SectionalExclusivePlanner.IsGroupUnique(index, groupItems, unit.File);

		// ① 躯干承载件（固定）：每种身形一个，层数大的优先，同层按 ID。
		var torso = new Dictionary<int, SectionalExclusivePlanner.Unit>();
		var missing = new List<int>();
		foreach (var body in bodies)
		{
			var found = tUnits
				.Where(unit => unit.Slot == torsoSlot && SectionalExclusivePlanner.Covers(unit.RawBody, body))
				.DistinctBy(unit => unit.File)
				.Where(Unique)
				.OrderByDescending(unit => unit.Layer)
				.ThenBy(unit => unit.File)
				.ToArray();
			if (found.Length == 0) missing.Add(body);
			else torso[body] = found[0];
		}

		if (missing.Count != 0)
		{
			var twins = SectionalExclusivePlanner.TwinNamesOf(index, groupItems, tUnits.Select(unit => unit.File));
			return Unavailable(kit, modelKey, twins.Count != 0
				? Describe(twins)
				: missing.Count == bodies.Length
					? "躯干部件都被别的护甲共用，没有独有躯干可以承载"
					: $"{SectionalExclusivePlanner.BodyLabel(missing[0])}的躯干部件被别的护甲共用，没有独有躯干可以承载");
		}

		var links = new Dictionary<ulong, string>();
		foreach (var unit in torso.Values) links[unit.File] = bodyUnit;
		var armsOnHelmets = new SortedSet<string>(StringComparer.Ordinal);

		// ② 手臂承载件：躯干、头盔槽之外的任意独有部件（Q1：找不到时整套不可，不做半套）。
		if (armsUnit is not null)
		{
			var used = links.Keys.ToHashSet();
			var preference = ArmsCarrierPreference.Select(SlotOf).ToList();
			foreach (var body in bodies)
			{
				var candidates = tUnits
					.Where(unit => unit.Slot != torsoSlot && unit.Slot != helmetSlot
						&& SectionalExclusivePlanner.Covers(unit.RawBody, body) && !used.Contains(unit.File))
					.DistinctBy(unit => unit.File)
					.Where(Unique)
					.OrderBy(unit => preference.IndexOf(unit.Slot) is var at and >= 0 ? at : 100 + unit.Slot)
					.ThenByDescending(unit => unit.Layer)
					.ThenBy(unit => unit.File)
					.ToArray();
				if (candidates.Length != 0)
				{
					links[candidates[0].File] = armsUnit;
					continue;
				}

				// Q1 第 2 步：2 槽组合（同套头盔也映射到这个素体）时，头已并进身体，头盔的独有头部件空着，手臂放那里。
				// 只有躯干槽和头盔在第一人称下被游戏隐藏——这里放的是头盔记录自己的部件，穿这顶头盔时手臂才会显示。
				// 3 槽组合时头盔的独有部件已被头占用，不能再放手臂。
				var fallback = layout.HeadMerge ? HelmetArmsCarrier(index, kit, companions, body, used) : null;
				if (fallback is null)
				{
					return Unavailable(kit, modelKey, "除躯干外没有别的独有部件可以放手臂槽（手臂不能放躯干上：第一人称下游戏会把躯干隐藏）"
						+ (layout.HeadMerge
							? (SectionalExclusivePlanner.HelmetKitsOf(index, kit).Any(helmet => companions.Contains(helmet.ItemId))
								? "，同套头盔的独有头部件也没有可用的"
								: "；2 槽下可把同套头盔一起映射，用它的独有头部件放手臂")
							: string.Empty));
				}

				links[fallback.Value.Unit.File] = armsUnit;
				armsOnHelmets.Add($"{fallback.Value.Helmet.DisplayName}（{fallback.Value.Helmet.ItemId}）");
			}
		}

		// ③ 隐藏集：目标记录的全部非披风部件 − 承载件；2 槽（头已并进身体）再加同套/同名头盔记录的全部非披风部件。
		var hide = tUnits.Select(unit => unit.File).ToList();
		var notes = new List<string>();
		if (layout.HeadMerge)
		{
			foreach (var helmetKit in SectionalExclusivePlanner.HelmetKitsOf(index, kit))
			{
				hide.AddRange(SectionalExclusivePlanner.UnitsOf(helmetKit)
					.Where(unit => unit.Type == UnitTypeId && unit.Slot != capeSlot)
					.Select(unit => unit.File));
			}

			notes.Add("头身合并（2 槽）：原版头盔的部件会在穿这套的角色身上隐藏（0.3 米半径是否覆盖头盔 unit 需实机确认）。");
		}
		else
		{
			notes.Add("3 槽：只映射躯干和手臂；模型的头不显示，戴什么头盔就显示什么头盔（想显示模型的头，再映射同名头盔）。");
		}

		if (armsOnHelmets.Count != 0)
			notes.Add($"手臂放在头盔 {string.Join("、", armsOnHelmets)} 的独有部件上（头已并进身体，这个部件空着）：只有同时戴着这顶头盔，手臂才会显示。");

		var mates = links.Keys
			.Where(file => index.Owners.TryGetValue(file, out var owners) && owners.Count > 1)
			.SelectMany(file => index.Owners[file])
			.Where(owner => owner != kit.ItemId)
			.Distinct()
			.Select(owner => index.Kits.FirstOrDefault(candidate => candidate.ItemId == owner))
			.Where(candidate => candidate is not null)
			.Select(candidate => $"{candidate!.DisplayName}（{candidate.ItemId}）")
			.Order(StringComparer.Ordinal)
			.ToArray();
		if (mates.Length != 0)
			notes.Add($"承载件同时被同名记录 {string.Join("、", mates)} 引用：穿它们的角色也会显示这个模型。");

		var signature = torso.OrderBy(pair => pair.Key).Select(pair => pair.Value.File).Distinct().Select(SectionalExclusivePlanner.Hex).ToArray();
		return Mapped(kit, modelKey, links, hide, signature, bodyUnit, notes);
	}

	/// <summary>
	/// 2 槽：头已并进身体。同套护甲也映射到这个素体（组合）= 这条头盔本身没有要映射的东西（它的原版部件由护甲的规则隐藏，
	/// 独有的头部件可能被护甲拿去放手臂），整体算「已映射」；单独映射头盔 = 不可。
	/// </summary>
	private static LowPollutionMappingPlan HelmetPlanTwoSlot(
		SectionalExclusivePlanner.Index index, GameDataAuthorityKit kit, string modelKey, IReadOnlySet<uint> companions)
	{
		var armorPaired = companions.Any(id => index.Kits.FirstOrDefault(other => other.ItemId == id) is { Kind: GameDataEquipmentKitKind.Armor } armor
			&& SectionalExclusivePlanner.HelmetKitsOf(index, armor).Any(helmet => helmet.ItemId == kit.ItemId));
		return armorPaired
			? new LowPollutionMappingPlan(kit.ItemId, kit.DisplayName, modelKey, LowPollutionMappingStatus.Mapped, null, [], [], [], [],
				["头身合并（2 槽）：头已并进身体，头盔本身不写映射；它的原版部件由同套护甲的规则隐藏，独有的头部件可能被护甲拿去放手臂。"])
			: Unavailable(kit, modelKey, "素体头身合并（2 槽），头已并进身体，头盔不可单独映射；请映射护甲");
	}

	/// <summary>2 槽组合时手臂的第二落点：同套且也在映射的头盔记录自己的独有头部件（头已并进身体，它空着）。</summary>
	private static (SectionalExclusivePlanner.Unit Unit, GameDataAuthorityKit Helmet)? HelmetArmsCarrier(
		SectionalExclusivePlanner.Index index, GameDataAuthorityKit armor, IReadOnlySet<uint> companions, int body, HashSet<ulong> used)
	{
		var helmetSlot = SlotOf(ArmorPart.Helmet);
		foreach (var helmet in SectionalExclusivePlanner.HelmetKitsOf(index, armor).Where(candidate => companions.Contains(candidate.ItemId)))
		{
			var owners = HelmetOwners(index, helmet);
			var found = SectionalExclusivePlanner.UnitsOf(helmet)
				.Where(unit => unit.Type == UnitTypeId && unit.Slot == helmetSlot
					&& SectionalExclusivePlanner.Covers(unit.RawBody, body) && !used.Contains(unit.File))
				.DistinctBy(unit => unit.File)
				.Where(unit => SectionalExclusivePlanner.IsGroupUnique(index, owners, unit.File))
				.OrderByDescending(unit => unit.Layer)
				.ThenBy(unit => unit.File)
				.FirstOrDefault();
			if (found.File != 0) return (found, helmet);
		}

		return null;
	}

	private static LowPollutionMappingPlan PlanHelmet(
		SectionalExclusivePlanner.Index index, LowPollutionLayout layout, GameDataAuthorityKit kit, string modelKey,
		IReadOnlySet<uint> companions)
	{
		if (layout.HeadMerge)
			return HelmetPlanTwoSlot(index, kit, modelKey, companions);
		var headUnit = layout.UnitOf(LowPollutionRoles.Head);
		if (headUnit is null)
			return Unavailable(kit, modelKey, "素体的压缩布局里没有头槽。");

		var capeSlot = SlotOf(ArmorPart.Cape);
		var helmetSlot = SlotOf(ArmorPart.Helmet);
		var all = SectionalExclusivePlanner.UnitsOf(kit).Where(unit => unit.Type == UnitTypeId && unit.Slot != capeSlot).ToArray();
		var heads = all.Where(unit => unit.Slot == helmetSlot).ToArray();
		if (heads.Length == 0)
			return Unavailable(kit, modelKey, "这条头盔记录没有头部位的件。");

		// 头盔部件「独有」= 拥有者都在 同套/同名的头盔记录 ∪ 同套/同名的护甲记录 里（口径同专属路线合并那一遍的 HelmetUnique）。
		var ownerSet = HelmetOwners(index, kit);
		var bodies = Enumerable.Range(0, 2).Where(body => heads.Any(unit => SectionalExclusivePlanner.Covers(unit.RawBody, body))).ToArray();
		var carriers = new Dictionary<int, SectionalExclusivePlanner.Unit>();
		var missing = new List<int>();
		foreach (var body in bodies)
		{
			var found = heads
				.Where(unit => SectionalExclusivePlanner.Covers(unit.RawBody, body))
				.DistinctBy(unit => unit.File)
				.Where(unit => SectionalExclusivePlanner.IsGroupUnique(index, ownerSet, unit.File))
				.OrderByDescending(unit => unit.Layer)
				.ThenBy(unit => unit.File)
				.ToArray();
			if (found.Length == 0) missing.Add(body);
			else carriers[body] = found[0];
		}

		if (missing.Count != 0)
		{
			var twins = SectionalExclusivePlanner.TwinNamesOf(index, ownerSet, heads.Select(unit => unit.File));
			return Unavailable(kit, modelKey, twins.Count != 0
				? Describe(twins)
				: "头盔的头部件都被别的头盔共用，没有独有部件可以承载模型的头");
		}

		var links = carriers.Values.DistinctBy(unit => unit.File).ToDictionary(unit => unit.File, _ => headUnit);
		var notes = new List<string> { "头盔映射：模型的头落在这顶头盔的独有部件上，只有戴这顶头盔时才显示；头盔的其余部件由 Hider 隐藏。" };
		var signature = carriers.OrderBy(pair => pair.Key).Select(pair => pair.Value.File).Distinct().Select(SectionalExclusivePlanner.Hex).ToArray();
		return Mapped(kit, modelKey, links, all.Select(unit => unit.File), signature, headUnit, notes);
	}

	private static LowPollutionMappingPlan Mapped(
		GameDataAuthorityKit kit, string modelKey, Dictionary<ulong, string> links, IEnumerable<ulong> hide,
		IReadOnlyList<string> signature, string unitSignature, List<string> notes)
		=> new(
			kit.ItemId, kit.DisplayName, modelKey, LowPollutionMappingStatus.Mapped, null,
			[.. links.OrderBy(pair => pair.Key).Select(pair => new LowPollutionLink(SectionalExclusivePlanner.Hex(pair.Key), pair.Value))],
			[.. hide.Where(file => !links.ContainsKey(file)).Distinct().Order().Select(SectionalExclusivePlanner.Hex)],
			signature,
			[unitSignature],
			notes);
}

/// <summary>
/// 把低污染映射规划结果编译成 Hider 规则（规划 §11.2 / §11.3）。
/// <para>
/// 每个目标的每个签名各一条：<b>承载件原 ID</b>（<c>lp-&lt;item&gt;</c>、<c>-s2</c>…）和 <b>压缩 unit ID</b>（<c>-u</c>）。
/// 后者防的是「<c>resource_name</c> 在映射覆盖后返回覆盖后的 ID」：两种返回都有一条能触发。
/// </para>
/// <para>
/// <b>取并集为什么安全。</b>压缩 unit 签名会在<i>每个</i>穿「映射到同一个素体的护甲」的角色身上成立，于是所有目标的隐藏集在这些角色身上都生效。
/// 隐藏集只含<b>原版部件 ID</b>，从不含承载件（见下面的全局扣除）也不含压缩 unit。X 的隐藏部件 S 出现在 Y 的角色身上，
/// S 就是 X、Y 共用的部件；Y 同样被映射，Y 的共用部件本来就要藏；S 不会是 Y 独有的（它和 X 共用）。
/// 所以多藏的只会是本来就该藏的，或者根本不在场的部件。例外只有「混搭」：玩家穿着 Y 的同时戴着别套的头盔，
/// 而这顶头盔的部件恰好在某个已映射护甲的隐藏集里（2 槽目标的隐藏集含同套头盔）——那是想要的结果（2 槽下原版头盔本来就该藏）。
/// </para>
/// <para>
/// <b>全局扣除承载件</b>：任何一个目标的承载件都不进任何规则的隐藏集。同名组里的两条记录可能共有某个部件，
/// 它在甲是「非承载件」、在乙是承载件；乙的角色身上它显示模型，不能被甲的规则藏掉。
/// </para>
/// </summary>
public static class LowPollutionHiderRules
{
	public static IReadOnlyList<KitInstanceRule> Build(IEnumerable<LowPollutionMappingPlan> plans)
	{
		ArgumentNullException.ThrowIfNull(plans);
		var mapped = plans.Where(plan => plan.Status == LowPollutionMappingStatus.Mapped).OrderBy(plan => plan.TargetItemId).ToArray();
		var allCarriers = mapped.SelectMany(plan => plan.Links.Select(link => link.Carrier)).ToHashSet(StringComparer.Ordinal);
		var rules = new List<KitInstanceRule>();
		var seen = new HashSet<string>(StringComparer.Ordinal);

		foreach (var plan in mapped)
		{
			var hide = plan.Hide.Where(id => !allCarriers.Contains(id)).ToArray();
			if (hide.Length == 0) continue;
			var chunks = hide.Chunk(KitInstanceRulesProtocol.MaximumHide).ToArray();
			var signatures = new List<(string Suffix, string Id)>();
			for (var index = 0; index < plan.Signature.Count; index++)
				signatures.Add((index == 0 ? string.Empty : "-s" + (index + 1).ToString(CultureInfo.InvariantCulture), plan.Signature[index]));
			foreach (var unit in plan.UnitSignature) signatures.Add(("-u", unit));

			var baseName = "lp-" + plan.TargetItemId.ToString(CultureInfo.InvariantCulture);
			foreach (var (suffix, id) in signatures)
			{
				for (var part = 0; part < chunks.Length; part++)
				{
					var name = baseName + suffix + (part == 0 ? string.Empty : "-h" + (part + 1).ToString(CultureInfo.InvariantCulture));
					var rule = new KitInstanceRule(name, SectionalExclusivePlanner.Radius, [id], [], chunks[part]);
					var content = id + "|" + string.Join(",", chunks[part]);
					if (seen.Add(content)) rules.Add(rule);
				}
			}
		}

		return rules;
	}
}

/// <summary>布局文件的读取（与写在 <see cref="LowPollutionLayoutFile"/>）。</summary>
internal static class LowPollutionLayoutReader
{
	/// <summary>读生效层里的布局文件。文件不存在返回 <c>false</c> 且 <paramref name="error"/> 为 <c>null</c>；读不出来 / 坏格式返回 <c>false</c> 且给原因。</summary>
	internal static bool TryRead(string modDirectory, out LowPollutionLayout? layout, out string? error)
	{
		layout = null;
		error = null;
		var path = Path.Combine(ModNodePayloadLayout.GetOverwriteDirectory(modDirectory), LowPollutionLayoutFile.FileName);
		if (!File.Exists(path)) return false;
		try
		{
			return TryParse(File.ReadAllText(path), out layout, out error);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			error = exception.Message;
			return false;
		}
	}

	internal static bool TryParse(string json, out LowPollutionLayout? layout, out string? error)
	{
		layout = null;
		error = null;
		try
		{
			using var document = JsonDocument.Parse(json);
			var root = document.RootElement;
			if (root.ValueKind != JsonValueKind.Object
				|| !root.TryGetProperty("schema", out var schema) || schema.GetString() != LowPollutionLayoutFile.Schema)
			{
				error = "布局文件的 schema 不认识。";
				return false;
			}

			if (!root.TryGetProperty("units", out var units) || units.ValueKind != JsonValueKind.Array)
			{
				error = "布局文件没有 units。";
				return false;
			}

			var list = new List<LowPollutionLayoutUnit>();
			foreach (var unit in units.EnumerateArray())
			{
				var role = unit.TryGetProperty("role", out var roleElement) ? roleElement.GetString() : null;
				var id = unit.TryGetProperty("unit", out var unitElement) ? unitElement.GetString() : null;
				if (role is null || !KitInstanceRulesProtocol.IsValidId(id))
				{
					error = "布局文件里有不合法的 unit 项。";
					return false;
				}

				list.Add(new LowPollutionLayoutUnit(role, id!));
			}

			layout = new LowPollutionLayout(
				root.TryGetProperty("mode", out var mode) ? mode.GetString() ?? string.Empty : string.Empty,
				root.TryGetProperty("headMerge", out var headMerge) && headMerge.ValueKind == JsonValueKind.True,
				list,
				root.TryGetProperty("fingerprint", out var fingerprint) && fingerprint.ValueKind == JsonValueKind.String ? fingerprint.GetString() : null);
			return true;
		}
		catch (JsonException exception)
		{
			error = exception.Message;
			return false;
		}
	}
}

/// <summary>
/// 低污染映射的编排（规划 §11.1 / §11.4）：哪些映射走新路径、压缩 unit 是否已部署、逐目标规划。
/// </summary>
public static class LowPollutionMappingService
{
	/// <param name="modsDirectory">Mod 库根（定位素体所在 Mod 的生效层）。</param>
	/// <param name="authority">装备权威快照（只读索引，不读被我们改写过的注册表）。</param>
	/// <param name="mappings">要应用的映射。</param>
	/// <param name="deployedUnits">游戏数据里实际已部署的 unit ID。<c>null</c> = 无法核对，一律按未部署处理。</param>
	/// <returns>只含走低污染路径的映射（来源是压缩素体、生效层有布局文件）；别的映射不出现，由旧路径照常处理。</returns>
	public static IReadOnlyList<LowPollutionMappingPlan> Plan(
		string modsDirectory,
		GameDataAuthoritySnapshot authority,
		IReadOnlyList<RuntimeArmorRegistryMapping> mappings,
		IReadOnlySet<ulong>? deployedUnits)
	{
		ArgumentNullException.ThrowIfNull(authority);
		ArgumentNullException.ThrowIfNull(mappings);
		var plans = new List<LowPollutionMappingPlan>();
		var modelMappings = mappings.Where(mapping => mapping.IsModelMapping).ToArray();
		if (modelMappings.Length == 0) return plans;

		var catalog = CustomModelCatalog.Collect(modsDirectory);
		var kits = authority.Kits.ToDictionary(kit => kit.ItemId);
		SectionalExclusivePlanner.Index? index = null;
		foreach (var mapping in modelMappings)
		{
			if (!kits.TryGetValue(mapping.TargetItemId, out var kit)) continue;
			if (!catalog.Entries.TryGetValue(mapping.ModelKey!, out var entry)) continue;
			var found = LowPollutionLayoutReader.TryRead(entry.ModDirectory, out var layout, out var error);
			if (!found && error is null) continue;   // 没有布局文件 = 这个素体没压缩过：走旧路径。
			if (!found)
			{
				// 布局文件在、但读不出来：不能当成「没压缩」退回全量映射（那就是污染），要用户重构建。
				plans.Add(LowPollutionMappingPlan.Fail(kit.ItemId, kit.DisplayName, mapping.ModelKey!, LowPollutionMappingStatus.Unavailable,
					$"素体的低污染布局文件读不出来（{error}），请对该 Mod 再点一次一键重构建"));
				continue;
			}

			var undeployed = layout!.AllUnits
				.Where(unit => deployedUnits is null
					|| !ulong.TryParse(unit, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id)
					|| !deployedUnits.Contains(id))
				.ToArray();
			if (undeployed.Length != 0)
			{
				plans.Add(LowPollutionMappingPlan.Fail(kit.ItemId, kit.DisplayName, mapping.ModelKey!, LowPollutionMappingStatus.NotDeployed,
					$"素体的压缩 unit 还没部署进游戏（{string.Join("、", undeployed.Take(3).Select(unit => "0x" + unit))}），请先部署再应用映射"));
				continue;
			}

			index ??= SectionalExclusivePlanner.BuildIndex(authority.Kits);
			// 映射到同一个素体的其它目标：2 槽组合要靠它认出「同套头盔也在映射」（Q1）。
			var companions = modelMappings
				.Where(other => other.TargetItemId != mapping.TargetItemId
					&& string.Equals(other.ModelKey, mapping.ModelKey, StringComparison.Ordinal))
				.Select(other => other.TargetItemId)
				.ToHashSet();
			plans.Add(LowPollutionMappingPlanner.Plan(index, layout, mapping.TargetItemId, mapping.ModelKey!, companions));
		}

		return plans;
	}
}
