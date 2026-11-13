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
}

/// <summary>下拉里一条记录的评估结果。</summary>
internal sealed record SectionalExclusiveCandidate(uint ItemId, string ArchiveId, string Name, bool Succeeded, string Summary);

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
			var plan = Build(index, source, kit.ItemId, headMerge);
			list.Add(new SectionalExclusiveCandidate(
				kit.ItemId, kit.ArchiveId, kit.DisplayName, plan.Succeeded,
				plan.Succeeded ? "可专属" : "不可：" + ShortGaps(plan.Gaps)));
		}

		return list;
	}

	private static string ShortGaps(IReadOnlyList<string> gaps)
		=> gaps.Count <= 3 ? string.Join("、", gaps) : string.Join("、", gaps.Take(3)) + $" 等 {gaps.Count} 处";

	private readonly record struct Unit(ulong File, ulong Type, int Slot, int Layer, uint RawBody);

	private static string BodyLabel(int body) => body == 0 ? "健壮身形" : "纤细身形";

	private static bool Covers(uint rawBody, int body) => rawBody == (uint)body || rawBody is not (0u or 1u);

	/// <summary>落点标签里的身形：通用（raw 3）承载件两种身形共用，不能说成某一种身形。</summary>
	private static string ShownBody(Unit carrier, int body) => carrier.RawBody is 0u or 1u ? BodyLabel(body) : "通用";

	private static string ShownCell(int slot, Unit carrier, int body) => $"{PartName(slot)}·{ShownBody(carrier, body)}";

	private static string PartName(int slot) => ArmorStructure.Display(ArmorStructure.FromSlot((uint)slot));

	private static IReadOnlyList<Unit> UnitsOf(GameDataAuthorityKit kit)
		=> kit.Pieces
			.Where(piece => piece.UnitAssetKey.FileId != 0)
			.Select(piece => new Unit(
				piece.UnitAssetKey.FileId, piece.UnitAssetKey.TypeId, (int)piece.Slot, (int)piece.PieceType, piece.RawBodyType))
			.DistinctBy(unit => (unit.File, unit.Slot, unit.Layer, unit.RawBody))
			.ToArray();

	private static string Hex(ulong value) => value.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);

	private static SectionalTargetUnit TargetOf(Unit unit)
		=> new(new AssetKey(unit.Type, unit.File), ArmorCell.FromRegistryCoordinates(unit.Slot, unit.Layer));

	/// <summary>规划一次。</summary>
	internal static SectionalExclusivePlan Build(
		Index index, IReadOnlyList<SectionalSourceUnit> source, uint itemId, bool headMerge)
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

		// ① 护甲部位：头与披风除外。头盔槽位在护甲记录上也可能有件（少数），那些只进隐藏集。
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
		var helmetKits = index.Kits
			.Where(candidate => candidate.Kind == GameDataEquipmentKitKind.Helmet
				&& (candidate.SetId == kit.SetId
					// 有的护甲与自己的头盔套装号不同（DS-42），只按 set_id 会漏掉：同名也算配套。
					|| string.Equals(candidate.DisplayName, kit.DisplayName, StringComparison.Ordinal)))
			.OrderBy(candidate => candidate.ItemId)
			.ToArray();
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
							.Where(unit => index.Owners.TryGetValue(unit.File, out var owners) && owners.Count == 1)
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
						Assign(head, carrier, ShownBody(carrier, body), "模型", name, signature: false, body);
					foreach (var unit in UnitsOf(helmetKit).Where(unit => unit.Slot != capeSlot))
						helmetHide.Add(unit.File);
					replaced++;
					helmetNotes.Add($"模型的头落在 {name} 上，只有戴这顶头盔时才显示；该头盔的其余部件隐藏。");
				}

				if (replaced == 0)
					helmetNotes.Add("没有任何配套头盔记录能承载头：保留原版头盔，模型的头不会出现。");
				else
					helmetNotes.Add("提示：被替换的头盔记录自己的部件也会显示模型的头，穿别的护甲戴这顶头盔时同样会看到。");
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
		var signature = new List<ulong>();
		var both = sigBody[0].Intersect(sigBody[1]).Order().ToArray();
		if (both.Length != 0) signature.Add(both[0]);
		else
		{
			foreach (var set in sigBody.Where(set => set.Count != 0))
				signature.Add(set.Order().First());
		}

		if (signature.Count == 0 && hide.Length != 0)
			warnings.Add("没有任何身体承载件可当签名：不会生成隐藏规则。");

		info.Add(hide.Length == 0
			? "目标没有需要隐藏的原版部件，不需要隐藏规则。"
			: $"有 {hide.Length} 个原版部件会在穿这套的角色身上被隐藏（下次部署后生效）。");
		return new SectionalExclusivePlan(
			true, itemId, kit.DisplayName, [], replacements, placements, hide, signature.Distinct().ToArray(),
			headResult, helmetNotes, info, warnings);

		static SectionalExclusivePlan Fail(uint item, string name, string reason)
			=> new(false, item, name, [reason], [], [], [], [], SectionalHeadMergePlan.Off, [], [], []);
	}
}
