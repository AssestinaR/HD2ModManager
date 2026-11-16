using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure.Sectional;

/// <summary>专属切换的一处落点（预览用）：哪个来源部位落到了哪个独有 unit。</summary>
/// <param name="Cell">界面标签，如「躯干·健壮身形」。</param>
/// <param name="SourceFileId">来源 unit（16 位小写 hex）。</param>
/// <param name="TargetFileId">承载件（目标记录独有的原版 unit）。</param>
/// <param name="Layer">承载件所在层。</param>
/// <param name="Role">「模型」或「头」。</param>
/// <param name="HelmetName">落在头盔记录上时那条记录的名字；落在护甲上为 <c>null</c>。</param>
internal sealed record SectionalExclusivePlacement(
	string Cell, string SourceFileId, ulong TargetFileId, int Layer, string Role, string? HelmetName);

/// <summary>
/// 专属切换（P4a）的规划结果。纯数据，不碰磁盘。
/// </summary>
/// <param name="Succeeded">成功；失败时 <see cref="Gaps"/> 列出全部缺口。</param>
/// <param name="Replacements">来源 unit → 承载件（每个承载件只承载一个来源）。</param>
/// <param name="Hide">要交给 Hider 按实例隐藏的原版 unit（目标记录的全部非披风部件减去承载件，加上被替换头盔的其余部件）。</param>
/// <param name="Signature">签名：第一个是规则的 <c>all</c>，其余是备用签名（只在没有「两种身形共用」的承载件时出现）。</param>
internal sealed record SectionalExclusivePlan(
	bool Succeeded,
	uint ItemId,
	string Name,
	IReadOnlyList<string> Gaps,
	IReadOnlyList<SectionalReplacement> Replacements,
	IReadOnlyList<SectionalExclusivePlacement> Placements,
	IReadOnlyList<ulong> Hide,
	IReadOnlyList<ulong> Signature,
	SectionalHeadMergePlan HeadMerge,
	IReadOnlyList<string> HelmetNotes,
	IReadOnlyList<string> Info,
	IReadOnlyList<string> Warnings)
{
	public string FailureText => string.Join("；", Gaps);

	/// <summary>P4b：要把多个来源 unit 合并成一个再写到承载件上的清单；P4a 逐格承载时为空。</summary>
	public IReadOnlyList<SectionalExclusiveMerge> Merges { get; init; } = [];

	/// <summary>P4b：拥有承载件的同名记录（同名组放宽后，它们也会显示这个模型）；没有放宽时为空。</summary>
	public IReadOnlyList<string> GroupMates { get; init; } = [];

	/// <summary>走了合并模式（P4a 逐格承载不了）。</summary>
	public bool IsMerged => Merges.Count != 0;
}

/// <summary>
/// P4b 的一次几何合并：<see cref="SourceFileIds"/>（宿主在前）合成一个 unit，写到承载件 <see cref="CarrierFileId"/> 上。
/// </summary>
/// <param name="CarrierFileId">承载件（目标记录或其同名组独有的原版 unit）。</param>
/// <param name="CarrierSlot">承载件所在部位槽（2 = 躯干）。</param>
/// <param name="Layer">承载件所在层。</param>
/// <param name="CarrierPart">承载件部位的显示名（「躯干」「左肩」…）。</param>
/// <param name="SourceFileIds">被合并的来源 unit，第一个是宿主。</param>
/// <param name="Bodies">这个承载件服务的穿戴者身形（0 健壮、1 纤细）。</param>
internal sealed record SectionalExclusiveMerge(
	ulong CarrierFileId,
	int CarrierSlot,
	int Layer,
	string CarrierPart,
	IReadOnlyList<string> SourceFileIds,
	IReadOnlyList<int> Bodies)
{
	/// <summary>躯干在第一人称下由游戏隐藏，合并进去不会挡镜头；别的部位不保证。</summary>
	public bool IsTorso => CarrierPart == ArmorStructure.Display(ArmorPart.Torso);
}

/// <summary>下拉里一条记录的评估结果。<paramref name="Merged"/> = P4a 逐格承载不了、要走合并。</summary>
internal sealed record SectionalExclusiveCandidate(
	uint ItemId, string ArchiveId, string Name, bool Succeeded, string Summary, bool Merged = false);

/// <summary>
/// 专属切换规划器（P4a，规划 <c>kit-instance-vpk-exclusive-plan-20261114.md</c> §4.2 / §4.4）。
/// <para>
/// 与 <see cref="SectionalCompilePlanner"/> 刻意分开：那一个是「整套共用层 + 全局空壳」的老做法，
/// 专属关闭时必须一字节不变；这一个是「只写目标记录自己独有的 unit、其余交给 Hider 按实例隐藏」。
/// </para>
/// <para>
/// <b>格子是 (部位, 穿戴者身形)。</b>原版 unit 的 <c>raw_body_type</c>：0 = 健壮、1 = 纤细、3 = 通用（两种身形都会生成）。
/// 所以「健壮穿戴者」能看到 raw 0 ∪ raw 3 的 unit，「纤细穿戴者」看到 raw 1 ∪ raw 3。
/// 每个格子里挑「只被目标记录引用」的 unit，最外层优先、同级按 ID；通用 unit 被两种身形共用时落点相同，不会重复写。
/// </para>
/// </summary>
internal static class SectionalExclusivePlanner
{
	public const int MaximumHide = 128;
	public const double Radius = 0.3;

	/// <summary>每个 unit 被哪几个 item_id 引用（含头盔与披风记录）。一次建好，下拉里对每条记录复用。</summary>
	internal sealed class Index
	{
		internal required IReadOnlyList<GameDataAuthorityKit> Kits { get; init; }
		internal required Dictionary<ulong, HashSet<uint>> Owners { get; init; }
	}

	internal static Index BuildIndex(IReadOnlyList<GameDataAuthorityKit> kits)
	{
		ArgumentNullException.ThrowIfNull(kits);
		var owners = new Dictionary<ulong, HashSet<uint>>();
		foreach (var kit in kits)
		{
			foreach (var piece in kit.Pieces)
			{
				var file = piece.UnitAssetKey.FileId;
				if (file == 0) continue;
				if (!owners.TryGetValue(file, out var set)) owners[file] = set = [];
				set.Add(kit.ItemId);
			}
		}

		return new Index { Kits = kits, Owners = owners };
	}

	/// <summary>评估每一条护甲记录能不能专属（下拉用）。</summary>
	internal static IReadOnlyList<SectionalExclusiveCandidate> Evaluate(
		Index index, IReadOnlyList<SectionalSourceUnit> source, bool headMerge)
	{
		var list = new List<SectionalExclusiveCandidate>();
		foreach (var kit in index.Kits.Where(kit => kit.Kind == GameDataEquipmentKitKind.Armor))
		{
			var plan = BuildWithMerge(index, source, kit.ItemId, headMerge);
			var status = !plan.Succeeded
				? "不可：" + ShortGaps(plan.Gaps)
				: plan.IsMerged
					? "可专属·合并（实验）：模型合并进 " + string.Join("、", plan.Merges.Select(merge => merge.CarrierPart).Distinct())
					: "可专属";
			list.Add(new SectionalExclusiveCandidate(
				kit.ItemId, kit.ArchiveId, kit.DisplayName, plan.Succeeded, status, plan.IsMerged));
		}

		return list;
	}

	/// <summary>
	/// 先按 P4a 逐格承载；<b>能承载就原样返回（与 <see cref="Build"/> 完全一致）</b>。
	/// 承载不了再进入合并模式（P4b，规划 <c>kit-instance-vpk-merge-plan-20261114.md</c> §3.1）。
	/// </summary>
	internal static SectionalExclusivePlan BuildWithMerge(
		Index index, IReadOnlyList<SectionalSourceUnit> source, uint itemId, bool headMerge)
	{
		var strict = Build(index, source, itemId, headMerge);
		return strict.Succeeded ? strict : BuildCore(index, source, itemId, headMerge, merge: true);
	}

	private static string ShortGaps(IReadOnlyList<string> gaps)
		=> gaps.Count <= 3 ? string.Join("、", gaps) : string.Join("、", gaps.Take(3)) + $" 等 {gaps.Count} 处";

	internal readonly record struct Unit(ulong File, ulong Type, int Slot, int Layer, uint RawBody);

	// ── 「独有」口径（专属补丁路线与 P6 S3 的低污染映射共用，不许各写一套）──────────────────

	/// <summary>同名记录组：同名护甲记录 ∪ 自己（P4b 放宽口径）。</summary>
	internal static HashSet<uint> GroupItems(Index index, GameDataAuthorityKit kit)
		=> index.Kits
			.Where(candidate => candidate.Kind == GameDataEquipmentKitKind.Armor
				&& string.Equals(candidate.DisplayName, kit.DisplayName, StringComparison.Ordinal))
			.Select(candidate => candidate.ItemId)
			.Append(kit.ItemId)
			.ToHashSet();

	/// <summary>护甲部件在放宽口径下是否独有：拥有者都在同名组里。</summary>
	internal static bool IsGroupUnique(Index index, IReadOnlySet<uint> groupItems, ulong file)
		=> index.Owners.TryGetValue(file, out var owners) && owners.All(groupItems.Contains);

	/// <summary>某护甲的配套头盔记录：同套，或同名（有的护甲与自己的头盔套装号不同，如 DS-42）。</summary>
	internal static GameDataAuthorityKit[] HelmetKitsOf(Index index, GameDataAuthorityKit kit)
		=> index.Kits
			.Where(candidate => candidate.Kind == GameDataEquipmentKitKind.Helmet
				&& (candidate.SetId == kit.SetId
					|| string.Equals(candidate.DisplayName, kit.DisplayName, StringComparison.Ordinal)))
			.OrderBy(candidate => candidate.ItemId)
			.ToArray();

	/// <summary>
	/// 组外、拥有给定全部部件的其它有名字护甲：几何完全相同的「孪生」护甲（只差颜色）。
	/// 孪生的只有没名字的记录时说成「另一套未命名的护甲记录」。
	/// </summary>
	internal static List<string> TwinNamesOf(Index index, IReadOnlySet<uint> groupItems, IEnumerable<ulong> files)
	{
		HashSet<uint>? common = null;
		foreach (var file in files.Distinct())
		{
			if (!index.Owners.TryGetValue(file, out var owners)) return [];
			if (common is null) common = [.. owners];
			else common.IntersectWith(owners);
		}

		if (common is null) return [];
		common.ExceptWith(groupItems);
		var named = index.Kits
			.Where(other => common.Contains(other.ItemId) && IsNamed(other.DisplayName))
			.Select(other => other.DisplayName)
			.Distinct(StringComparer.Ordinal)
			.Order(StringComparer.CurrentCulture)
			.ToList();
		if (named.Count == 0 && index.Kits.Any(other => common.Contains(other.ItemId))) named.Add("另一套未命名的护甲记录");
		return named;
	}

	/// <summary>游戏里有正式名字的护甲；没名字的记录显示成 <c>0x…</c>，不是玩家能穿的本体护甲。</summary>
	internal static bool IsNamed(string? displayName)
		=> !string.IsNullOrWhiteSpace(displayName) && !displayName.StartsWith("0x", StringComparison.OrdinalIgnoreCase);

	internal static string BodyLabel(int body) => body == 0 ? "健壮身形" : "纤细身形";

	internal static bool Covers(uint rawBody, int body) => rawBody == (uint)body || rawBody is not (0u or 1u);

	/// <summary>落点标签里的身形：通用（raw 3）承载件两种身形共用，不能说成某一种身形。</summary>
	private static string ShownBody(Unit carrier, int body) => carrier.RawBody is 0u or 1u ? BodyLabel(body) : "通用";

	private static string ShownCell(int slot, Unit carrier, int body) => $"{PartName(slot)}·{ShownBody(carrier, body)}";

	private static string PartName(int slot) => ArmorStructure.Display(ArmorStructure.FromSlot((uint)slot));

	internal static IReadOnlyList<Unit> UnitsOf(GameDataAuthorityKit kit)
		=> kit.Pieces
			.Where(piece => piece.UnitAssetKey.FileId != 0)
			.Select(piece => new Unit(
				piece.UnitAssetKey.FileId, piece.UnitAssetKey.TypeId, (int)piece.Slot, (int)piece.PieceType, piece.RawBodyType))
			.DistinctBy(unit => (unit.File, unit.Slot, unit.Layer, unit.RawBody))
			.ToArray();

	internal static string Hex(ulong value) => value.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);

	private static SectionalTargetUnit TargetOf(Unit unit)
		=> new(new AssetKey(unit.Type, unit.File), ArmorCell.FromRegistryCoordinates(unit.Slot, unit.Layer));

	/// <summary>规划一次（P4a：逐格承载，任何一格没有独有部件就整体失败）。</summary>
	internal static SectionalExclusivePlan Build(
		Index index, IReadOnlyList<SectionalSourceUnit> source, uint itemId, bool headMerge)
		=> BuildCore(index, source, itemId, headMerge, merge: false);

	/// <summary>P4b 主承载件的优先顺序：躯干（第一人称由游戏隐藏），其次胯、腿、肩。</summary>
	private static readonly ArmorPart[] CarrierPreference =
	[
		ArmorPart.Torso, ArmorPart.Hips, ArmorPart.LeftLeg, ArmorPart.RightLeg, ArmorPart.LeftShoulder, ArmorPart.RightShoulder,
	];

	private static SectionalExclusivePlan BuildCore(
		Index index, IReadOnlyList<SectionalSourceUnit> source, uint itemId, bool headMerge, bool merge)
	{
		ArgumentNullException.ThrowIfNull(index);
		ArgumentNullException.ThrowIfNull(source);

		var helmetSlot = (int)(ArmorStructure.SlotOf(ArmorPart.Helmet) ?? 0u);
		var capeSlot = (int)(ArmorStructure.SlotOf(ArmorPart.Cape) ?? 1u);
		var torsoSlot = (int)(ArmorStructure.SlotOf(ArmorPart.Torso) ?? 2u);

		var kit = index.Kits.FirstOrDefault(candidate => candidate.ItemId == itemId && candidate.Kind == GameDataEquipmentKitKind.Armor);
		if (kit is null) return Fail(itemId, string.Empty, $"索引里找不到护甲记录 {itemId}。");

		var gaps = new List<string>();
		var info = new List<string>();
		var warnings = new List<string>();

		foreach (var unit in source.Where(unit => !unit.Cell.IsKnown))
			warnings.Add($"来源 unit 0x{unit.FileId} 没有权威格子（部位/层未定位）：不会写出去。");
		var bySlot = source
			.Where(unit => unit.Cell.IsKnown)
			.GroupBy(unit => unit.Cell.Slot)
			.ToDictionary(
				group => group.Key,
				group => group.OrderBy(unit => unit.Cell.Layer).ThenBy(unit => unit.FileId, StringComparer.Ordinal).First());
		foreach (var group in source.Where(unit => unit.Cell.IsKnown).GroupBy(unit => unit.Cell.Slot).Where(group => group.Count() > 1))
			warnings.Add($"部位「{PartName(group.Key)}」有 {group.Count()} 个来源 unit：只写出最里那一个（0x{bySlot[group.Key].FileId}）。");

		var head = bySlot.GetValueOrDefault(helmetSlot);
		var torso = bySlot.GetValueOrDefault(torsoSlot);
		var mergeActive = headMerge && head is not null && torso is not null;
		if (headMerge && !mergeActive)
			warnings.Add("要求头身合并，但来源缺少头或躯干：本次不合并。");

		var tUnits = UnitsOf(kit).Where(unit => unit.Slot != capeSlot).ToArray();
		var replacements = new List<SectionalReplacement>();
		var placements = new List<SectionalExclusivePlacement>();
		var carriers = new HashSet<ulong>();
		var carrierBySource = new Dictionary<ulong, string>();   // 承载件 → 来源（冲突检测）
		var sigBody = new[] { new HashSet<ulong>(), new HashSet<ulong>() };

		void Assign(SectionalSourceUnit sourceUnit, Unit target, string cell, string role, string? helmetName, bool signature, int body)
		{
			if (signature) sigBody[body].Add(target.File);
			if (carrierBySource.TryGetValue(target.File, out var existing))
			{
				if (!string.Equals(existing, sourceUnit.FileId, StringComparison.Ordinal))
					gaps.Add($"{cell}：承载件 0x{Hex(target.File)} 同时被两个来源占用（{role}与另一件），头身合并无法同时落下");
				return;   // 同一来源同一承载件：两种身形共用的通用 unit，只写一次
			}

			carrierBySource[target.File] = sourceUnit.FileId;
			carriers.Add(target.File);
			replacements.Add(new SectionalReplacement(sourceUnit, TargetOf(target)));
			placements.Add(new SectionalExclusivePlacement(cell, sourceUnit.FileId, target.File, target.Layer, role, helmetName));
		}

		// P4b：同名记录组。「独有」放宽成「拥有者都在这个组里」（规划 §3.1 第 3 点，F2）。
		var groupItems = GroupItems(index, kit);
		// 组外、拥有目标全部（非披风）部件的其它有名字护甲：几何完全相同的「孪生」护甲。
		List<string> TwinNames(IReadOnlyCollection<Unit> units) => TwinNamesOf(index, groupItems, units.Select(unit => unit.File));

		var merges = new List<SectionalExclusiveMerge>();
		var sigPreferred = new[] { new HashSet<ulong>(), new HashSet<ulong>() };   // 合并模式：每种身形的主承载件（签名优先取它）

		// ① P4b 合并模式：主承载件 + 逐格承载 + 缺的格子合并进主承载件。
		void PlanMerged()
		{
			var sourceSlots = bySlot.Keys.Where(slot => slot != capeSlot && slot != helmetSlot).Order().ToArray();
			var bodies = Enumerable.Range(0, 2).Where(body => tUnits.Any(unit => Covers(unit.RawBody, body))).ToArray();
			bool IsGroupUnique(ulong file) => SectionalExclusivePlanner.IsGroupUnique(index, groupItems, file);
			Unit[] Unique(int slot, int body) => tUnits
				.Where(unit => unit.Slot == slot && Covers(unit.RawBody, body))
				.DistinctBy(unit => unit.File)
				.Where(unit => IsGroupUnique(unit.File))
				.OrderByDescending(unit => unit.Layer)
				.ThenBy(unit => unit.File)
				.ToArray();

			// 每种身形一个主承载件：躯干优先，其次胯、腿、肩。
			var primary = new Unit?[2];
			foreach (var body in bodies)
			{
				foreach (var part in CarrierPreference)
				{
					var found = Unique((int)(ArmorStructure.SlotOf(part) ?? 0u), body);
					if (found.Length == 0) continue;
					primary[body] = found[0];
					break;
				}

				if (primary[body] is null)
					gaps.Add($"{BodyLabel(body)}没有任何独有部件可以承载合并后的模型（躯干、胯、腿、肩都被其它护甲共用）");
			}

			if (gaps.Count != 0)
			{
				// 最常见的原因：这套护甲和别的护甲用的是同一套模型，只有材质/颜色不同（如 B-01 与 TR-40、TR-9 与 DP-40）。
				// 这种情况没有任何部件能区分它们，直接说清楚，比逐身形列「被共用」更有用。
				var twins = TwinNames(tUnits);
				if (twins.Count != 0)
				{
					gaps.Clear();
					gaps.Add($"和 {string.Join("、", twins.Take(3))}{(twins.Count > 3 ? " 等" : "")} 用的是同一套模型，只有颜色不同，没有部件能把它们分开");
				}

				return;
			}

			foreach (var body in bodies) sigPreferred[body].Add(primary[body]!.Value.File);

			// 每个格子：有自己的独有承载件就逐格承载；没有的记进 missing，合并进主承载件。头身合并的「头」当一个伪部位，
			// 自己的承载件是躯干第二层。
			var own = new Dictionary<(int Slot, int Body), Unit>();
			var missing = new[] { new SortedSet<int>(), new SortedSet<int>() };
			foreach (var body in bodies)
			{
				foreach (var slot in sourceSlots)
				{
					var found = Unique(slot, body);
					if (found.Length == 0) missing[body].Add(slot);
					else own[(slot, body)] = found[0];
				}

				if (!mergeActive) continue;
				var torsoUnits = Unique(torsoSlot, body);
				if (torsoUnits.Length >= 2 && !missing[body].Contains(torsoSlot)) own[(helmetSlot, body)] = torsoUnits[1];
				else missing[body].Add(helmetSlot);
			}

			// 两种身形共用同一个主承载件（通用 unit）：它只有一份几何，缺口取并集。
			var sharedPrimary = bodies.Length == 2 && primary[0]!.Value.File == primary[1]!.Value.File;
			if (sharedPrimary)
			{
				var union = missing[0].Union(missing[1]).ToArray();
				foreach (var body in bodies)
					foreach (var slot in union)
					{
						missing[body].Add(slot);
						own.Remove((slot, body));
					}
			}

			// 需要合并的身形：主承载件所在部位的来源也并进去（它不再单独承载）。
			var mergedFiles = new HashSet<ulong>();
			foreach (var body in bodies)
			{
				if (missing[body].Count == 0) continue;
				var carrier = primary[body]!.Value;
				if (!mergedFiles.Add(carrier.File)) continue;

				var served = sharedPrimary ? bodies : [body];
				var slots = new SortedSet<int>(missing[body]);
				foreach (var servedBody in served)
					own.Remove((carrier.Slot, servedBody));
				slots.Add(carrier.Slot);

				var ordered = new List<SectionalSourceUnit>();
				foreach (var slot in slots.OrderBy(slot => slot == carrier.Slot ? -1 : slot == helmetSlot ? int.MaxValue : slot))
				{
					var unit = bySlot.GetValueOrDefault(slot);
					if (unit is not null && ordered.All(item => item.FileId != unit.FileId)) ordered.Add(unit);
				}

				if (ordered.Count == 0) continue;
				var part = PartName(carrier.Slot);
				carrierBySource[carrier.File] = "merge:" + Hex(carrier.File);
				carriers.Add(carrier.File);
				foreach (var servedBody in served) sigBody[servedBody].Add(carrier.File);
				replacements.Add(new SectionalReplacement(ordered[0], TargetOf(carrier)));
				foreach (var unit in ordered)
				{
					placements.Add(new SectionalExclusivePlacement(
						PartName(unit.Cell.Slot), unit.FileId, carrier.File, carrier.Layer, $"模型·合并进{part}", null));
				}

				merges.Add(new SectionalExclusiveMerge(
					carrier.File, carrier.Slot, carrier.Layer, part, ordered.Select(unit => unit.FileId).ToArray(), served));
			}

			// 其余格子逐格承载（与 P4a 同一套落点与签名规则）。
			foreach (var ((slot, body), unit) in own.OrderBy(pair => pair.Key.Slot).ThenBy(pair => pair.Key.Body))
			{
				if (slot == helmetSlot && head is not null)
					Assign(head, unit, ShownCell(torsoSlot, unit, body), "头", null, signature: true, body);
				else if (bySlot.TryGetValue(slot, out var sourceUnit))
					Assign(sourceUnit, unit, ShownCell(slot, unit, body), "模型", null, signature: true, body);
			}
		}

		// ① 护甲部位：头与披风除外。头盔槽位在护甲记录上也可能有件（少数），那些只进隐藏集。
		if (merge) PlanMerged();
		else
		foreach (var (slot, sourceUnit) in bySlot.OrderBy(pair => pair.Key))
		{
			if (slot == capeSlot || slot == helmetSlot) continue;
			var slotUnits = tUnits.Where(unit => unit.Slot == slot).ToArray();
			if (slotUnits.Length == 0)
			{
				gaps.Add($"目标没有「{PartName(slot)}」部位");
				continue;
			}

			var need = mergeActive && slot == torsoSlot ? 2 : 1;
			for (var body = 0; body <= 1; body++)
			{
				var cellUnits = slotUnits.Where(unit => Covers(unit.RawBody, body)).DistinctBy(unit => unit.File).ToArray();
				if (cellUnits.Length == 0) continue;
				var unique = cellUnits
					.Where(unit => index.Owners.TryGetValue(unit.File, out var owners) && owners.Count == 1)
					.OrderByDescending(unit => unit.Layer)
					.ThenBy(unit => unit.File)
					.ToArray();
				var cell = $"{PartName(slot)}·{BodyLabel(body)}";
				if (unique.Length < need)
				{
					gaps.Add(unique.Length == 0
						? $"{cell}没有独有部件"
						: $"{cell}只有 {unique.Length} 个独有部件，头身合并需要 {need} 个");
					continue;
				}

				Assign(sourceUnit, unique[0], ShownCell(slot, unique[0], body), "模型", null, signature: true, body);
				if (need == 2 && head is not null)
				{
					Assign(head, unique[1], ShownCell(slot, unique[1], body), "头", null, signature: true, body);
				}
			}
		}

		// ② 头盔（§4.4）。
		var helmetNotes = new List<string>();
		var helmetKits = HelmetKitsOf(index, kit);
		var helmetHide = new HashSet<ulong>();
		var headResult = SectionalHeadMergePlan.Off;
		if (mergeActive)
		{
			headResult = SectionalHeadMergePlan.IntoArmorTorso;
			foreach (var helmetKit in helmetKits)
				foreach (var unit in UnitsOf(helmetKit).Where(unit => unit.Slot != capeSlot))
					helmetHide.Add(unit.File);
			helmetNotes.Add(helmetKits.Length == 0
				? "头身合并：头落到躯干第二层；这套没有配套头盔记录。"
				: $"头身合并：头落到躯干第二层，同套 {helmetKits.Length} 条头盔记录的原版部件全部隐藏。");
		}
		else if (head is not null)
		{
			// P4b（仅合并这一遍）：头盔承载件也按同名组放宽——拥有者全在「同套 ∪ 同名」的头盔记录里就算独有。
			// 典型：FS-05 的头盔 unit 被 8 条同名头盔记录共用，严格独有永远找不到，头会被丢掉（原版头盔扣在脖子上）。
			// 非合并那一遍（P4a）保持严格判断，结果不变。
			var helmetOwnerSet = helmetKits.Select(candidate => candidate.ItemId).Append(itemId).ToHashSet();
			bool HelmetUnique(ulong file) => index.Owners.TryGetValue(file, out var owners)
				&& (merge ? owners.All(helmetOwnerSet.Contains) : owners.Count == 1);
			var relaxedHelmetCarriers = new HashSet<ulong>();
			if (helmetKits.Length == 0)
			{
				headResult = SectionalHeadMergePlan.NoHelmetKit;
				helmetNotes.Add("这套没有配套头盔记录：头无处安放，其余部件照常处理。");
			}
			else
			{
				var replaced = 0;
				foreach (var helmetKit in helmetKits)
				{
					var hUnits = UnitsOf(helmetKit).Where(unit => unit.Slot == helmetSlot).ToArray();
					var name = $"{helmetKit.DisplayName}（{helmetKit.ItemId}）";
					if (hUnits.Length == 0)
					{
						helmetNotes.Add($"头盔 {name}：没有头部位的件，保留原版。");
						continue;
					}

					var perBody = new List<(int Body, Unit Carrier)>();
					var missing = new List<string>();
					for (var body = 0; body <= 1; body++)
					{
						var cellUnits = hUnits.Where(unit => Covers(unit.RawBody, body)).DistinctBy(unit => unit.File).ToArray();
						if (cellUnits.Length == 0) continue;
						var unique = cellUnits
							.Where(unit => HelmetUnique(unit.File))
							.OrderByDescending(unit => unit.Layer).ThenBy(unit => unit.File).ToArray();
						if (unique.Length == 0) missing.Add(BodyLabel(body));
						else perBody.Add((body, unique[0]));
					}

					if (missing.Count != 0 || perBody.Count == 0)
					{
						helmetNotes.Add(
							$"头盔 {name}：{(missing.Count != 0 ? string.Join("、", missing) : "头部位")}没有独有部件，保留原版头盔（不替换、不隐藏）。");
						continue;
					}

					foreach (var (body, carrier) in perBody)
					{
						Assign(head, carrier, ShownBody(carrier, body), "模型", name, signature: false, body);
						if (index.Owners.TryGetValue(carrier.File, out var carrierOwners) && carrierOwners.Count > 1)
							relaxedHelmetCarriers.Add(carrier.File);
					}

					foreach (var unit in UnitsOf(helmetKit).Where(unit => unit.Slot != capeSlot))
						helmetHide.Add(unit.File);
					replaced++;
					helmetNotes.Add($"模型的头落在 {name} 上，只有戴这顶头盔时才显示；该头盔的其余部件隐藏。");
				}

				if (replaced == 0)
					helmetNotes.Add("没有任何配套头盔记录能承载头：保留原版头盔，模型的头不会出现。");
				else
					helmetNotes.Add("提示：被替换的头盔记录自己的部件也会显示模型的头，穿别的护甲戴这顶头盔时同样会看到。");
				if (relaxedHelmetCarriers.Count != 0)
				{
					// 头盔承载件被多条同名头盔记录共用：它们都会显示这个头。
					var sharing = helmetKits.Count(candidate => UnitsOf(candidate).Any(unit => relaxedHelmetCarriers.Contains(unit.File)));
					helmetNotes.Add($"同名的 {sharing} 条头盔记录都会显示这个头（它们共用同一个头部 unit）。");
				}
			}
		}

		if (gaps.Count != 0)
			return new SectionalExclusivePlan(false, itemId, kit.DisplayName, gaps, [], [], [], [], headResult, helmetNotes, info, warnings);

		// ③ 隐藏集：目标记录的全部非披风部件减去承载件，加上被处理头盔记录的其余部件。
		var hide = tUnits.Select(unit => unit.File)
			.Concat(helmetHide)
			.Where(file => !carriers.Contains(file))
			.Distinct()
			.Order()
			.ToArray();
		if (hide.Length > MaximumHide)
		{
			return new SectionalExclusivePlan(
				false, itemId, kit.DisplayName,
				[$"需要隐藏的原版部件有 {hide.Length} 个，超过规则上限 {MaximumHide}"],
				[], [], [], [], headResult, helmetNotes, info, warnings);
		}

		// ④ 签名：两种身形都有的承载件优先（一个 all 就够）；否则每种身形各取 ID 最小的一个，生成备用规则。
		// 合并模式下签名取主承载件（规划 §3.1 第 4 点）：它是躯干时离根节点最近，0.3 米半径内最稳。
		var sigSets = new[] { sigBody[0], sigBody[1] };
		if (merges.Count != 0)
		{
			for (var body = 0; body <= 1; body++)
			{
				var preferred = sigPreferred[body].Where(carriers.Contains).ToHashSet();
				if (preferred.Count != 0) sigSets[body] = preferred;
			}
		}

		var signature = new List<ulong>();
		var both = sigSets[0].Intersect(sigSets[1]).Order().ToArray();
		if (both.Length != 0) signature.Add(both[0]);
		else
		{
			foreach (var set in sigSets.Where(set => set.Count != 0))
				signature.Add(set.Order().First());
		}

		if (signature.Count == 0 && hide.Length != 0)
			warnings.Add("没有任何身体承载件可当签名：不会生成隐藏规则。");

		info.Add(hide.Length == 0
			? "目标没有需要隐藏的原版部件，不需要隐藏规则。"
			: $"有 {hide.Length} 个原版部件会在穿这套的角色身上被隐藏（下次部署后生效）。");

		var groupMates = new List<string>();
		if (merge)
		{
			foreach (var item in merges)
			{
				info.Add(
					$"合并（实验）：{item.SourceFileIds.Count} 个模型部位合并成一个 unit，写到「{item.CarrierPart}」承载件 0x{Hex(item.CarrierFileId)} 上。");
				if (!item.IsTorso)
					warnings.Add(
						$"模型合并进了「{item.CarrierPart}」而不是躯干：游戏在第一人称下只隐藏躯干和头盔，第一人称可能看到模型（会挡镜头），需要实机确认。");
			}

			// 同名组放宽：承载件被同名记录共用时，穿它们的角色也会显示这个模型。
			var shared = carriers.Where(file => index.Owners.TryGetValue(file, out var owners) && owners.Count > 1).Order().ToArray();
			if (shared.Length != 0)
			{
				foreach (var mate in index.Kits
					.Where(candidate => candidate.Kind == GameDataEquipmentKitKind.Armor
						&& candidate.ItemId != itemId && groupItems.Contains(candidate.ItemId)
						&& candidate.Pieces.Any(piece => shared.Contains(piece.UnitAssetKey.FileId)))
					.OrderBy(candidate => candidate.ItemId))
				{
					groupMates.Add($"{mate.DisplayName}（{mate.ItemId}）");
				}

				warnings.Add(
					$"承载件 {string.Join("、", shared.Select(file => "0x" + Hex(file)))} 同时被同名记录 {string.Join("、", groupMates)} 引用："
					+ "穿它们的角色也会显示这个模型。");
			}
		}

		return new SectionalExclusivePlan(
			true, itemId, kit.DisplayName, [], replacements, placements, hide, signature.Distinct().ToArray(),
			headResult, helmetNotes, info, warnings)
		{
			Merges = merges,
			GroupMates = groupMates,
		};

		static SectionalExclusivePlan Fail(uint item, string name, string reason)
			=> new(false, item, name, [reason], [], [], [], [], SectionalHeadMergePlan.Off, [], [], []);
	}
}
