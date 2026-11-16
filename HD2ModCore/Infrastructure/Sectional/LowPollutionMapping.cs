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

	/// <summary>身体槽 + 手臂槽的合体 unit（规划 §17）：躯干外只有一个独有部件时挂它。</summary>
	public const string BodyArms = "body-arms";
}

/// <summary>布局文件里的一个压缩 unit。</summary>
/// <param name="Body">体型：<c>any</c>（没拆的槽，/1 布局一律是它）、<c>stocky</c>、<c>slim</c>（规划 §13.3）。</param>
public sealed record LowPollutionLayoutUnit(string Role, string Unit, string Body = LowPollutionBodies.Any);

/// <summary>布局文件里 <c>body</c> 的取值。</summary>
public static class LowPollutionBodies
{
	public const string Any = "any";
	public const string Stocky = "stocky";
	public const string Slim = "slim";

	internal static string Name(int body) => body == 0 ? Stocky : Slim;
}

/// <summary>
/// <c>Overwrite/low-pollution-layout.json</c> 的读出结果（P6 S2 写、S3 读）。
/// S3 只需要：几槽、头身合并与否、每个角色对应的压缩 unit ID。
/// </summary>
public sealed record LowPollutionLayout(string Mode, bool HeadMerge, IReadOnlyList<LowPollutionLayoutUnit> Units, string? Fingerprint = null)
{
	/// <summary>某角色的压缩 unit ID（16 位小写 hex）；没有这个槽时为 <c>null</c>。拆开的槽优先给通用的，没有则取第一个。</summary>
	public string? UnitOf(string role)
		=> (Units.FirstOrDefault(unit => unit.Role == role && unit.Body == LowPollutionBodies.Any)
			?? Units.FirstOrDefault(unit => unit.Role == role))?.Unit;

	private string? Pick(string role, string body)
		=> Units.FirstOrDefault(unit => unit.Role == role && unit.Body == body)?.Unit;

	/// <summary>
	/// 某体型（0 = 健壮 / 1 = 纤细）在这个槽里该链到的压缩 unit。退回顺序：该体型的 → 通用的 → 另一体型的（规划 §13.4 / Q2）。
	/// 没有这个槽时为 <c>null</c>。
	/// </summary>
	public string? UnitOf(string role, int body)
		=> Pick(role, LowPollutionBodies.Name(body)) ?? Pick(role, LowPollutionBodies.Any) ?? Pick(role, LowPollutionBodies.Name(1 - body));

	/// <summary>这个槽里该体型没有自己的 unit、也没有通用的，只能用另一体型顶上。</summary>
	public bool UsesOtherBody(string role, int body)
		=> Pick(role, LowPollutionBodies.Name(body)) is null && Pick(role, LowPollutionBodies.Any) is null
			&& Pick(role, LowPollutionBodies.Name(1 - body)) is not null;

	/// <summary>这个槽按体型拆成了两个不同的 unit（一个承载件不可能同时放下两份）。</summary>
	public bool IsSplit(string role)
		=> UnitOf(role, 0) is { } stocky && UnitOf(role, 1) is { } slim && !string.Equals(stocky, slim, StringComparison.Ordinal);

	/// <summary>这个槽里的全部压缩 unit（去重，顺序同布局文件）。</summary>
	public IReadOnlyList<string> UnitsOf(string role)
		=> [.. Units.Where(unit => unit.Role == role).Select(unit => unit.Unit).Distinct(StringComparer.Ordinal)];

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

	/// <summary>躯干槽没有独有部件时，身体承载件的优先顺序（规划 §16）：胯和腿最贴近身体，其次肩、手臂。</summary>
	private static readonly ArmorPart[] BodyCarrierPreference =
	[
		ArmorPart.Hips, ArmorPart.LeftLeg, ArmorPart.RightLeg,
		ArmorPart.LeftShoulder, ArmorPart.RightShoulder, ArmorPart.LeftArm, ArmorPart.RightArm,
	];

	/// <param name="companions">
	/// 同一次应用里映射到<b>同一个素体</b>的其它目标（item id）。2 槽头盔按现行逻辑靠它判断同套护甲是否同在映射表里。
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
			GameDataEquipmentKitKind.Armor => PlanArmorGroup(index, layout, [kit.ItemId], modelKey)[kit.ItemId],
			GameDataEquipmentKitKind.Helmet => PlanHelmet(index, layout, kit, modelKey, companions ?? EmptySet),
			_ => LowPollutionMappingPlan.Fail(itemId, kit.DisplayName, modelKey, LowPollutionMappingStatus.Unavailable, "目标不是护甲或头盔。"),
		};
	}

	private static LowPollutionMappingPlan Unavailable(GameDataAuthorityKit kit, string modelKey, string reason)
		=> LowPollutionMappingPlan.Fail(kit.ItemId, kit.DisplayName, modelKey, LowPollutionMappingStatus.Unavailable, reason);

	private static string Describe(IReadOnlyList<string> twins)
		=> $"和 {string.Join("、", twins.Take(3))}{(twins.Count > 3 ? " 等" : string.Empty)} 用的是同一套模型，只有颜色不同，没有部件能把它们分开";

	private static int SlotOf(ArmorPart part) => (int)(ArmorStructure.SlotOf(part) ?? 0u);

	internal static IEnumerable<SectionalExclusivePlanner.Unit> ArmorUnits(GameDataAuthorityKit kit)
	{
		var capeSlot = SlotOf(ArmorPart.Cape);
		return SectionalExclusivePlanner.UnitsOf(kit).Where(unit => unit.Type == UnitTypeId && unit.Slot != capeSlot);
	}

	/// <summary>体型专属的部件（raw 0 / 1）；raw 3 的通用部件两种体型共用同一个 unit。</summary>
	private static bool IsBodySpecific(SectionalExclusivePlanner.Unit unit) => unit.RawBody is 0u or 1u;

	private static string SharedCarrierReason(GameDataAuthorityKit kit, string what)
		=> $"{kit.DisplayName} 的通用部件两种体型共用，模型的{what}分壮实 / 纤细两份，没法同时放下";

	/// <summary>Q2：某体型在这个槽里没有自己的模型，用另一体型顶上。</summary>
	private static void NoteFallback(LowPollutionLayout layout, string role, string what, IEnumerable<int> bodies, List<string> notes)
	{
		foreach (var body in bodies)
		{
			if (layout.UsesOtherBody(role, body))
				notes.Add($"{(body == 0 ? "健壮" : "纤细")}体型没有自己的{what}模型，用{(body == 0 ? "纤细" : "健壮")}体型的顶上，可能穿模。");
		}
	}

	private readonly record struct ArmorEntry(
		GameDataAuthorityKit Kit,
		SectionalExclusivePlanner.Unit[] Units,
		int[] Bodies);

	private readonly record struct CarrierHyperedge(
		ulong File,
		int RoleKind,
		string Unit,
		HashSet<uint> Owners,
		bool HasTorsoOnAll,
		int MaxSlotPref,
		int MinLayer);

	private readonly record struct AssignedHyperedge(
		ulong File,
		int RoleKind,
		string Unit,
		int[] BodiesCovered);

	/// <summary>
	/// 口径二：按选品器玩家表口径（<see cref="SectionalExclusivePlanner.IsNamed"/> 与 <see cref="KitNonPlayerArmors"/>）判断
	/// 某条索引拥有者是否参与承载件共识校验。未命名的内部记录（<c>0x…</c>）与「拿不到」的非玩家记录不拦截共用；
	/// 已实测会在船上出场的民主官 NPC（<see cref="KitNonPlayerArmors.DemocracyOfficerArmorId"/>）及所有玩家护甲仍参与校验。
	/// </summary>
	private static bool IsOwnerCheckedForConsensus(
		uint ownerId,
		IReadOnlyDictionary<uint, GameDataAuthorityKit> kitsById,
		IReadOnlySet<uint> activeSet)
	{
		if (activeSet.Contains(ownerId)) return true;
		if (!kitsById.TryGetValue(ownerId, out var ownerKit)) return true;
		if (!SectionalExclusivePlanner.IsNamed(ownerKit.DisplayName)) return false;
		if (ownerKit.Kind == GameDataEquipmentKitKind.Armor)
			return ownerId == KitNonPlayerArmors.DemocracyOfficerArmorId || !KitNonPlayerArmors.Contains(ownerId);
		if (ownerKit.Kind == GameDataEquipmentKitKind.Helmet)
			return !KitNonPlayerArmors.IsNonPlayerHelmet(ownerId);
		return true;
	}

	/// <summary>
	/// 方案 A（纯护甲联合共识，零跨槽头盔代臂）：把映射到同一个素体（<paramref name="modelKey"/>）的护甲放在一起选承载件。
	/// 任意候选部件必须满足游戏索引 <see cref="SectionalExclusivePlanner.Index.Owners"/> 中的<b>需校验拥有者</b>（全部玩家护甲、
	/// 民主官 NPC、未知类别 NPC；跳过未命名 <c>0x…</c> 与「拿不到」非玩家记录）都在本组活动集合里且使用同一条「承载件 → 压缩 unit」链接，
	/// 不满足的护甲整套剥除并迭代重算，直至全组共识成立。
	/// </summary>
	internal static Dictionary<uint, LowPollutionMappingPlan> PlanArmorGroup(
		SectionalExclusivePlanner.Index index,
		LowPollutionLayout layout,
		IReadOnlyList<uint> armorIds,
		string modelKey)
	{
		var results = new Dictionary<uint, LowPollutionMappingPlan>();
		var kitsById = index.Kits.ToDictionary(kit => kit.ItemId);
		var distinctIds = armorIds.Distinct().ToArray();

		var bodyRole = layout.UnitOf(LowPollutionRoles.HeadBody) is not null ? LowPollutionRoles.HeadBody : LowPollutionRoles.Body;
		if (layout.UnitOf(bodyRole) is null)
		{
			foreach (var id in distinctIds)
			{
				results[id] = kitsById.TryGetValue(id, out var kit)
					? Unavailable(kit, modelKey, "素体的压缩布局里没有躯干槽（body / head-body）。")
					: LowPollutionMappingPlan.Fail(id, string.Empty, modelKey, LowPollutionMappingStatus.Unavailable, "索引里找不到目标记录。");
			}

			return results;
		}

		var hasArms = layout.UnitOf(LowPollutionRoles.Arms) is not null;
		var torsoSplit = layout.IsSplit(bodyRole);
		var armsSplit = hasArms && layout.IsSplit(LowPollutionRoles.Arms);
		var baSplit = hasArms && layout.IsSplit(LowPollutionRoles.BodyArms);
		var helmetSlot = SlotOf(ArmorPart.Helmet);
		var torsoSlot = SlotOf(ArmorPart.Torso);
		var bodyPref = BodyCarrierPreference.Select(SlotOf).ToList();
		var armsPref = ArmsCarrierPreference.Select(SlotOf).ToList();

		var activeSet = new HashSet<uint>();
		var armorData = new Dictionary<uint, ArmorEntry>();
		foreach (var id in distinctIds)
		{
			if (!kitsById.TryGetValue(id, out var kit))
			{
				results[id] = LowPollutionMappingPlan.Fail(id, string.Empty, modelKey, LowPollutionMappingStatus.Unavailable, "索引里找不到目标记录。");
				continue;
			}

			var units = ArmorUnits(kit).ToArray();
			var bodies = Enumerable.Range(0, 2).Where(body => units.Any(unit => SectionalExclusivePlanner.Covers(unit.RawBody, body))).ToArray();
			if (bodies.Length == 0)
			{
				results[id] = Unavailable(kit, modelKey, "目标记录没有可承载的部件。");
				continue;
			}

			activeSet.Add(id);
			armorData[id] = new ArmorEntry(kit, units, bodies);
		}

		var solved = new Dictionary<uint, List<AssignedHyperedge>>();
		while (activeSet.Count != 0)
		{
			var eligibleFiles = activeSet
				.SelectMany(id => armorData[id].Units.Select(unit => unit.File))
				.Distinct()
				.Where(file => index.Owners.TryGetValue(file, out var owners)
					&& owners.Where(owner => IsOwnerCheckedForConsensus(owner, kitsById, activeSet)).All(activeSet.Contains))
				.OrderBy(file => file)
				.ToArray();

			var hyperedges = BuildHyperedges(
				index, layout, bodyRole, hasArms, torsoSplit, armsSplit, baSplit,
				torsoSlot, helmetSlot, bodyPref, armsPref, armorData, activeSet, eligibleFiles);

			var assigned = SolveConsensusAssignment(activeSet, armorData, hyperedges, hasArms);
			var failed = activeSet.Where(id => armorData[id].Bodies.Any(body =>
				!IsCellCovered(assigned[id], body, 0) || (hasArms && !IsCellCovered(assigned[id], body, 1)))).ToArray();
			if (failed.Length == 0)
			{
				solved = assigned;
				break;
			}

			foreach (var id in failed)
			{
				var entry = armorData[id];
				var beforePrune = new HashSet<uint>(activeSet);
				results[id] = Unavailable(entry.Kit, modelKey, DiagnoseArmorFailure(
					index, kitsById, layout, bodyRole, hasArms, torsoSplit, armsSplit, torsoSlot, helmetSlot,
					beforePrune, entry, assigned[id], hyperedges));
				activeSet.Remove(id);
			}
		}

		foreach (var id in activeSet.OrderBy(item => item))
			results[id] = BuildMappedArmorPlan(index, layout, bodyRole, hasArms, torsoSlot, armorData[id], solved[id], activeSet, modelKey);

		return results;
	}

	private static List<CarrierHyperedge> BuildHyperedges(
		SectionalExclusivePlanner.Index index,
		LowPollutionLayout layout,
		string bodyRole,
		bool hasArms,
		bool torsoSplit,
		bool armsSplit,
		bool baSplit,
		int torsoSlot,
		int helmetSlot,
		List<int> bodyPref,
		List<int> armsPref,
		Dictionary<uint, ArmorEntry> armorData,
		HashSet<uint> activeSet,
		ulong[] eligibleFiles)
	{
		var hyperedges = new List<CarrierHyperedge>();
		foreach (var file in eligibleFiles)
		{
			var owners = index.Owners[file].Where(activeSet.Contains).ToHashSet();
			for (var rk = 0; rk <= 2; rk++)
			{
				if (!TryCreateHyperedge(
					file, rk, owners, layout, bodyRole, hasArms, torsoSplit, armsSplit, baSplit,
					torsoSlot, helmetSlot, bodyPref, armsPref, armorData, out var edge))
					continue;
				hyperedges.Add(edge);
			}
		}

		return hyperedges;
	}

	private static bool TryCreateHyperedge(
		ulong file,
		int roleKind,
		HashSet<uint> owners,
		LowPollutionLayout layout,
		string bodyRole,
		bool hasArms,
		bool torsoSplit,
		bool armsSplit,
		bool baSplit,
		int torsoSlot,
		int helmetSlot,
		List<int> bodyPref,
		List<int> armsPref,
		Dictionary<uint, ArmorEntry> armorData,
		out CarrierHyperedge edge)
	{
		edge = default;
		if (roleKind == 1 && !hasArms) return false;
		var bodyUnit0 = layout.UnitOf(bodyRole, 0);
		var bodyUnit1 = layout.UnitOf(bodyRole, 1);
		var armsUnit0 = hasArms ? layout.UnitOf(LowPollutionRoles.Arms, 0) : null;
		var armsUnit1 = hasArms ? layout.UnitOf(LowPollutionRoles.Arms, 1) : null;
		var baUnit0 = hasArms ? layout.UnitOf(LowPollutionRoles.BodyArms, 0) : null;
		var baUnit1 = hasArms ? layout.UnitOf(LowPollutionRoles.BodyArms, 1) : null;
		if (roleKind == 2 && (baUnit0 is null || baUnit1 is null)) return false;

		bool cov0 = false, cov1 = false, allTorso = true, allBodySpecific = true;
		var minLayer = int.MaxValue;
		var maxPref = int.MinValue;
		var pref = roleKind == 0 ? bodyPref : armsPref;
		foreach (var owner in owners)
		{
			var oUnits = armorData[owner].Units.Where(unit => unit.File == file).ToArray();
			if (oUnits.Length == 0) return false;
			if (!oUnits.Any(unit => unit.Slot == torsoSlot)) allTorso = false;

			foreach (var ou in oUnits)
			{
				if (ou.Slot == helmetSlot) return false;
				if (roleKind is 1 or 2 && ou.Slot == torsoSlot) return false;
				if (!IsBodySpecific(ou)) allBodySpecific = false;
				if (ou.Layer < minLayer) minLayer = ou.Layer;
				var slotScore = ou.Slot == torsoSlot ? -1 : (pref.IndexOf(ou.Slot) is var idx and >= 0 ? idx : 100 + ou.Slot);
				if (slotScore > maxPref) maxPref = slotScore;
				if (SectionalExclusivePlanner.Covers(ou.RawBody, 0) && armorData[owner].Bodies.Contains(0)) cov0 = true;
				if (SectionalExclusivePlanner.Covers(ou.RawBody, 1) && armorData[owner].Bodies.Contains(1)) cov1 = true;
			}
		}

		if (!cov0 && !cov1) return false;
		var (u0, u1, isSplit) = roleKind switch
		{
			0 => (bodyUnit0, bodyUnit1, torsoSplit),
			1 => (armsUnit0, armsUnit1, armsSplit),
			_ => (baUnit0, baUnit1, baSplit),
		};
		if (cov0 && cov1 && !string.Equals(u0, u1, StringComparison.Ordinal)) return false;
		if (isSplit && !allBodySpecific) return false;
		var targetUnit = cov0 ? u0 : u1;
		if (targetUnit is null) return false;

		edge = new CarrierHyperedge(file, roleKind, targetUnit, [.. owners], allTorso, maxPref, minLayer);
		return true;
	}

	private static Dictionary<uint, List<AssignedHyperedge>> SolveConsensusAssignment(
		HashSet<uint> activeSet,
		Dictionary<uint, ArmorEntry> armorData,
		List<CarrierHyperedge> hyperedges,
		bool hasArms)
	{
		var assigned = activeSet.ToDictionary(id => id, _ => new List<AssignedHyperedge>());
		var usedFiles = new HashSet<ulong>();

		int[] CoveredBodies(ulong file, uint owner)
			=> armorData[owner].Bodies
				.Where(body => armorData[owner].Units.Any(unit => unit.File == file && SectionalExclusivePlanner.Covers(unit.RawBody, body)))
				.ToArray();

		bool CanAssign(CarrierHyperedge h)
		{
			if (usedFiles.Contains(h.File)) return false;
			foreach (var owner in h.Owners)
			{
				var bodies = CoveredBodies(h.File, owner);
				if (bodies.Length == 0) return false;
				foreach (var prev in assigned[owner])
				{
					if (!prev.BodiesCovered.Intersect(bodies).Any()) continue;
					if (prev.RoleKind == h.RoleKind) return false;
					if (string.Equals(prev.Unit, h.Unit, StringComparison.Ordinal)) return false;
					if (prev.RoleKind == 2 || h.RoleKind == 2) return false;
				}
			}

			return true;
		}

		void Commit(CarrierHyperedge h)
		{
			usedFiles.Add(h.File);
			foreach (var owner in h.Owners)
				assigned[owner].Add(new AssignedHyperedge(h.File, h.RoleKind, h.Unit, CoveredBodies(h.File, owner)));
		}

		bool LeavesAnyOwnerWithoutArms(CarrierHyperedge h)
		{
			if (!hasArms || h.RoleKind != 0) return false;
			foreach (var owner in h.Owners)
			{
				foreach (var body in CoveredBodies(h.File, owner))
				{
					if (IsCellCovered(assigned[owner], body, 1)) continue;
					var hasOtherArms = hyperedges.Any(other =>
						other.File != h.File
						&& other.Owners.Contains(owner)
						&& (other.RoleKind == 1 || other.RoleKind == 2)
						&& CoveredBodies(other.File, owner).Contains(body)
						&& CanAssign(other));
					if (!hasOtherArms) return true;
				}
			}

			return false;
		}

		void TryAssignCell(uint id, int body, int role, bool preferPrivate)
		{
			if (IsCellCovered(assigned[id], body, role)) return;
			var pick = hyperedges
				.Where(h => h.Owners.Contains(id)
					&& (h.RoleKind == role || h.RoleKind == 2)
					&& CoveredBodies(h.File, id).Contains(body)
					&& CanAssign(h))
				.OrderBy(h => LeavesAnyOwnerWithoutArms(h) ? 1 : 0)
				.ThenBy(h => preferPrivate && h.Owners.Count == 1 ? 0 : 1)
				.ThenBy(h => h.RoleKind == 2 ? 1 : 0)
				.ThenByDescending(h => h.RoleKind == 0 && h.HasTorsoOnAll)
				.ThenBy(h => h.Owners.Count)
				.ThenBy(h => h.MaxSlotPref)
				.ThenByDescending(h => h.MinLayer)
				.ThenBy(h => h.File)
				.FirstOrDefault();
			if (pick.File != 0) Commit(pick);
		}

		var roleCount = hasArms ? 2 : 1;
		// 1a：优先给没有单件独占退路的护甲（如共享承载件孪生）按可承载文件数从少到多（MRV）锁定安全共识部件，
		// 再由 1b 为其余细胞填独占或共享件。这样仅存少量非躯干共用承载件的记录（如 FS-05 玩家版）
		// 不会因拥有他处共享躯干的同组记录先抢占躯干而被剥夺 body-arms 共识解。
		var constrained = new List<(uint Id, int Body, int Role, int FeasibleFiles)>();
		foreach (var id in activeSet.OrderBy(item => item))
		{
			foreach (var body in armorData[id].Bodies)
			{
				for (var role = 0; role < roleCount; role++)
				{
					var matching = hyperedges.Where(h => h.Owners.Contains(id)
						&& (h.RoleKind == role || h.RoleKind == 2)
						&& CoveredBodies(h.File, id).Contains(body)).ToArray();
					if (!matching.Any(h => h.Owners.Count == 1))
					{
						var feasibleFiles = matching.Select(h => h.File).Distinct().Count();
						constrained.Add((id, body, role, feasibleFiles));
					}
				}
			}
		}

		foreach (var cell in constrained
			.OrderBy(item => item.FeasibleFiles)
			.ThenBy(item => item.Role)
			.ThenBy(item => item.Id)
			.ThenBy(item => item.Body))
			TryAssignCell(cell.Id, cell.Body, cell.Role, preferPrivate: false);

		foreach (var id in activeSet.OrderBy(item => item))
		{
			foreach (var body in armorData[id].Bodies)
			{
				for (var role = 0; role < roleCount; role++)
					TryAssignCell(id, body, role, preferPrivate: true);
			}
		}

		return assigned;
	}

	private static bool IsCellCovered(List<AssignedHyperedge> assigned, int body, int needRole)
		=> assigned.Any(edge => edge.BodiesCovered.Contains(body) && (edge.RoleKind == needRole || edge.RoleKind == 2));

	private static string DiagnoseArmorFailure(
		SectionalExclusivePlanner.Index index,
		IReadOnlyDictionary<uint, GameDataAuthorityKit> kitsById,
		LowPollutionLayout layout,
		string bodyRole,
		bool hasArms,
		bool torsoSplit,
		bool armsSplit,
		int torsoSlot,
		int helmetSlot,
		IReadOnlySet<uint> activeSet,
		ArmorEntry entry,
		List<AssignedHyperedge> assigned,
		List<CarrierHyperedge> hyperedges)
	{
		var missingBodies = entry.Bodies.Where(body => !IsCellCovered(assigned, body, 0)).ToArray();
		if (missingBodies.Length != 0)
		{
			if (torsoSplit && entry.Units.Any(unit => unit.Slot != helmetSlot
				&& !IsBodySpecific(unit)
				&& index.Owners.TryGetValue(unit.File, out var owners)
				&& owners.Where(owner => IsOwnerCheckedForConsensus(owner, kitsById, activeSet)).All(activeSet.Contains)))
			{
				return SharedCarrierReason(entry.Kit, "躯干");
			}

			var twins = SectionalExclusivePlanner.TwinNamesOf(index, activeSet, entry.Units.Select(unit => unit.File));
			if (twins.Count != 0) return Describe(twins);

			return missingBodies.Length == entry.Bodies.Length
				? "躯干部件都被别的护甲共用，没有安全的躯干承载件可用"
				: $"{SectionalExclusivePlanner.BodyLabel(missingBodies[0])}的躯干部件都被别的护甲共用，没有安全的躯干承载件可用";
		}

		if (hasArms)
		{
			var missingArms = entry.Bodies.Where(body => !IsCellCovered(assigned, body, 1)).ToArray();
			if (missingArms.Length != 0)
			{
				if (armsSplit && entry.Units.Any(unit => unit.Slot != torsoSlot && unit.Slot != helmetSlot
					&& !IsBodySpecific(unit)
					&& index.Owners.TryGetValue(unit.File, out var owners)
					&& owners.Where(owner => IsOwnerCheckedForConsensus(owner, kitsById, activeSet)).All(activeSet.Contains)))
				{
					return SharedCarrierReason(entry.Kit, "手臂");
				}

				if (hyperedges.Any(h => h.Owners.Contains(entry.Kit.ItemId) && h.RoleKind == 0 && h.HasTorsoOnAll))
					return "除躯干外没有可安全承载手臂的护甲部件（手臂不能放躯干或头盔上）";
				return "除躯干外没有别的护甲部件可以放手臂槽（方案 A 不跨槽借用头盔部件）";
			}
		}

		return "无法与同模型的其它护甲在全部拥有者上达成一致承载件";
	}

	private static LowPollutionMappingPlan BuildMappedArmorPlan(
		SectionalExclusivePlanner.Index index,
		LowPollutionLayout layout,
		string bodyRole,
		bool hasArms,
		int torsoSlot,
		ArmorEntry entry,
		List<AssignedHyperedge> assigned,
		HashSet<uint> activeSet,
		string modelKey)
	{
		var capeSlot = SlotOf(ArmorPart.Cape);
		var links = new Dictionary<ulong, string>();
		var signatureMap = new Dictionary<int, ulong>();
		var bodyArmsUnits = new List<string>();
		var bodyElsewhere = new SortedSet<string>(StringComparer.Ordinal);

		foreach (var edge in assigned)
		{
			links[edge.File] = edge.Unit;
			var matchedUnit = entry.Units.First(unit => unit.File == edge.File);
			if (edge.RoleKind is 0 or 2)
			{
				foreach (var body in edge.BodiesCovered)
					signatureMap[body] = edge.File;
				if (matchedUnit.Slot != torsoSlot)
					bodyElsewhere.Add(ArmorStructure.Display(ArmorStructure.FromSlot((uint)matchedUnit.Slot)));
			}

			if (edge.RoleKind == 2)
				bodyArmsUnits.Add(edge.Unit);
		}

		var hide = entry.Units.Select(unit => unit.File).ToList();
		var notes = new List<string>();
		if (layout.HeadMerge)
		{
			foreach (var helmetKit in SectionalExclusivePlanner.HelmetKitsOf(index, entry.Kit))
			{
				hide.AddRange(SectionalExclusivePlanner.UnitsOf(helmetKit)
					.Where(unit => unit.Type == UnitTypeId && unit.Slot != capeSlot)
					.Select(unit => unit.File));
			}

			notes.Add("头身合并（2 槽）：原版头盔的部件会在穿这套的角色身上隐藏（0.3 米半径内的同名部件都会隐藏）。");
		}
		else
		{
			notes.Add("3 槽：只映射躯干和手臂；模型的头不显示，戴什么头盔就显示什么头盔（想显示模型的头，再映射同名头盔）。");
		}

		if (bodyArmsUnits.Count != 0)
			notes.Add($"躯干外只有一个安全部件：身体和手臂合成一个 unit 挂在「{string.Join("、", bodyElsewhere)}」上，第一人称下模型的身体和手臂都可能露出来，需实机确认。");
		else if (bodyElsewhere.Count != 0)
			notes.Add($"躯干没有安全部件，身体改挂在「{string.Join("、", bodyElsewhere)}」上：第一人称下游戏只隐藏躯干和头盔，模型的身体可能露出来挡视野，需实机确认。");

		NoteFallback(layout, bodyRole, "躯干", entry.Bodies, notes);
		if (hasArms) NoteFallback(layout, LowPollutionRoles.Arms, "手臂", entry.Bodies, notes);

		var mates = links.Keys
			.Where(file => index.Owners.TryGetValue(file, out var owners) && owners.Count > 1)
			.SelectMany(file => index.Owners[file])
			.Where(owner => owner != entry.Kit.ItemId && activeSet.Contains(owner))
			.Distinct()
			.Select(owner => index.Kits.FirstOrDefault(candidate => candidate.ItemId == owner))
			.Where(candidate => candidate is not null)
			.Select(candidate => $"{candidate!.DisplayName}（{candidate.ItemId}）")
			.Order(StringComparer.Ordinal)
			.ToArray();
		if (mates.Length != 0)
			notes.Add($"承载件同时被同模型且链接一致的记录 {string.Join("、", mates)} 引用：穿它们的角色也会显示这个模型。");

		var signature = signatureMap.OrderBy(pair => pair.Key).Select(pair => pair.Value).Distinct().Select(SectionalExclusivePlanner.Hex).ToArray();
		return Mapped(entry.Kit, modelKey, links, hide, signature, [.. layout.UnitsOf(bodyRole), .. bodyArmsUnits.Distinct(StringComparer.Ordinal)], notes);
	}

	/// <summary>
	/// 2 槽：头已并进身体。同套护甲也在映射表里时，头盔本身没有要写入的链接或规则，照常记为「已映射」。
	/// </summary>
	private static LowPollutionMappingPlan HelmetPlanTwoSlot(
		SectionalExclusivePlanner.Index index, GameDataAuthorityKit kit, string modelKey, IReadOnlySet<uint> companions)
	{
		var armorPaired = companions.Any(id => index.Kits.FirstOrDefault(other => other.ItemId == id) is { Kind: GameDataEquipmentKitKind.Armor } armor
			&& SectionalExclusivePlanner.HelmetKitsOf(index, armor).Any(helmet => helmet.ItemId == kit.ItemId));
		return armorPaired
			? new LowPollutionMappingPlan(kit.ItemId, kit.DisplayName, modelKey, LowPollutionMappingStatus.Mapped, null, [], [], [], [],
				["头身合并（2 槽）：头已并进身体，头盔本身不写映射；它的原版部件在同套护甲可映射时由护甲规则隐藏。"])
			: Unavailable(kit, modelKey, "素体头身合并（2 槽），头已并进身体，头盔不可单独映射；请映射护甲");
	}

	private static LowPollutionMappingPlan PlanHelmet(
		SectionalExclusivePlanner.Index index, LowPollutionLayout layout, GameDataAuthorityKit kit, string modelKey,
		IReadOnlySet<uint> companions)
	{
		if (layout.HeadMerge)
			return HelmetPlanTwoSlot(index, kit, modelKey, companions);
		if (layout.UnitOf(LowPollutionRoles.Head) is null)
			return Unavailable(kit, modelKey, "素体的压缩布局里没有头槽。");
		var headSplit = layout.IsSplit(LowPollutionRoles.Head);

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

		var links = new Dictionary<ulong, string>();
		foreach (var (body, unit) in carriers)
		{
			var headUnit = layout.UnitOf(LowPollutionRoles.Head, body)!;
			if ((headSplit && !IsBodySpecific(unit)) || (links.TryGetValue(unit.File, out var existing) && existing != headUnit))
				return Unavailable(kit, modelKey, SharedCarrierReason(kit, "头"));
			links[unit.File] = headUnit;
		}

		var notes = new List<string> { "头盔映射：模型的头落在这顶头盔的独有部件上，只有戴这顶头盔时才显示；头盔的其余部件由 Hider 隐藏。" };
		NoteFallback(layout, LowPollutionRoles.Head, "头", bodies, notes);
		var signature = carriers.OrderBy(pair => pair.Key).Select(pair => pair.Value.File).Distinct().Select(SectionalExclusivePlanner.Hex).ToArray();
		return Mapped(kit, modelKey, links, all.Select(unit => unit.File), signature, layout.UnitsOf(LowPollutionRoles.Head), notes);
	}

	private static LowPollutionMappingPlan Mapped(
		GameDataAuthorityKit kit, string modelKey, Dictionary<ulong, string> links, IEnumerable<ulong> hide,
		IReadOnlyList<string> signature, IReadOnlyList<string> unitSignature, List<string> notes)
		=> new(
			kit.ItemId, kit.DisplayName, modelKey, LowPollutionMappingStatus.Mapped, null,
			[.. links.OrderBy(pair => pair.Key).Select(pair => new LowPollutionLink(SectionalExclusivePlanner.Hex(pair.Key), pair.Value))],
			[.. hide.Where(file => !links.ContainsKey(file)).Distinct().Order().Select(SectionalExclusivePlanner.Hex)],
			signature,
			unitSignature,
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
	/// <summary>
	/// 低污染规则的同角色判定半径（米，协议上限 1.0）。2026-11-14 实机（规划 §14.3–14.6）：TG-8 没藏住的真正原因是
	/// Hider 单世界 unit 上限截掉了玩家部件；实测部件离锚点 ≤ 0.06 米，任务里看到的 0.5–1.0 米是锚点位置晚一轮。
	/// Hider 0.4.3 当轮刷新锚点后收回 0.3，越小越不容易把贴身站着的队友的共用部件误藏（藏了不会恢复）。
	/// </summary>
	public const double Radius = 0.3;

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
			// 分体素体有两个躯干压缩 unit（壮实 / 纤细）：名字必须各不相同（-u、-u2…），否则协议判重名，整份规则写不进去
			// （2026-11-14 实机：埃吉尔分体后规则与 wardrobe.links 都没写成，CE-27 继续被旧映射污染）。
			for (var index = 0; index < plan.UnitSignature.Count; index++)
				signatures.Add((index == 0 ? "-u" : "-u" + (index + 1).ToString(CultureInfo.InvariantCulture), plan.UnitSignature[index]));

			var baseName = "lp-" + plan.TargetItemId.ToString(CultureInfo.InvariantCulture);
			foreach (var (suffix, id) in signatures)
			{
				for (var part = 0; part < chunks.Length; part++)
				{
					var name = baseName + suffix + (part == 0 ? string.Empty : "-h" + (part + 1).ToString(CultureInfo.InvariantCulture));
					var rule = new KitInstanceRule(name, LowPollutionHiderRules.Radius, [id], [], chunks[part]);
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
				|| !root.TryGetProperty("schema", out var schema)
					|| schema.GetString() is not (LowPollutionLayoutFile.Schema or LowPollutionLayoutFile.SchemaV2))
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

				// /1 没有 body：一律按 any。
				var body = unit.TryGetProperty("body", out var bodyElement) && bodyElement.ValueKind == JsonValueKind.String
					? bodyElement.GetString() : LowPollutionBodies.Any;
				if (body is not (LowPollutionBodies.Any or LowPollutionBodies.Stocky or LowPollutionBodies.Slim))
				{
					error = "布局文件里有不认识的体型。";
					return false;
				}

				list.Add(new LowPollutionLayoutUnit(role, id!, body!));
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
		var ready = new List<(int Position, uint ItemId, GameDataEquipmentKitKind Kind, string ModelKey, LowPollutionLayout Layout)>();
		var companionsByModel = new Dictionary<string, HashSet<uint>>(StringComparer.Ordinal);
		var armorsByModel = new Dictionary<string, (LowPollutionLayout Layout, List<uint> Ids)>(StringComparer.Ordinal);
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
			var modelKey = mapping.ModelKey!;
			if (!companionsByModel.TryGetValue(modelKey, out var companions))
			{
				companions = [];
				companionsByModel[modelKey] = companions;
			}
			companions.Add(kit.ItemId);

			if (kit.Kind == GameDataEquipmentKitKind.Armor)
			{
				if (!armorsByModel.TryGetValue(modelKey, out var group))
				{
					group = (layout, []);
					armorsByModel[modelKey] = group;
				}
				if (!group.Ids.Contains(kit.ItemId))
					group.Ids.Add(kit.ItemId);
			}

			ready.Add((plans.Count, kit.ItemId, kit.Kind, modelKey, layout));
			plans.Add(null!);
		}

		if (index is null) return plans;

		var armorPlansByModel = new Dictionary<string, Dictionary<uint, LowPollutionMappingPlan>>(StringComparer.Ordinal);
		foreach (var (modelKey, (layout, armorIds)) in armorsByModel)
			armorPlansByModel[modelKey] = LowPollutionMappingPlanner.PlanArmorGroup(index, layout, armorIds, modelKey);

		foreach (var item in ready)
		{
			if (item.Kind == GameDataEquipmentKitKind.Armor)
			{
				plans[item.Position] = armorPlansByModel[item.ModelKey][item.ItemId];
				continue;
			}

			// 映射到同一个素体的其它目标：2 槽组合要靠它认出「同套头盔也在映射」（Q1 / 保留头盔独立映射）。
			var companions = companionsByModel[item.ModelKey].Where(id => id != item.ItemId).ToHashSet();
			plans[item.Position] = LowPollutionMappingPlanner.Plan(index, item.Layout, item.ItemId, item.ModelKey, companions);
		}
		return plans;
	}
}
