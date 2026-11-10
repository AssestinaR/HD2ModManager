namespace HD2ModCore.Domain;

/// <summary>
/// 拆件规划器看到的一个来源 unit。<b>只描述，不带几何</b>——规划必须是纯的、能离线测的。
/// </summary>
/// <param name="UnitId">16 位小写 hex。</param>
/// <param name="Slot">unit 自带的槽位名（<c>Torso</c> / <c>LeftShoulder</c> / 空…）。</param>
/// <param name="BodyType">身形（<c>Any</c> / <c>Slim</c> / <c>Stocky</c>）。</param>
/// <param name="SectionCount">这个 unit 有多少节几何。</param>
/// <param name="Name">显示名（unit 名或 bodypart 名），用来给组起名。</param>
public sealed record SectionalSplitSource(
	string UnitId,
	string Slot,
	string BodyType,
	int SectionCount,
	string Name = "",
	string PieceType = "");

/// <summary>一个输出 unit：把 <see cref="DonorUnitIds"/> 的几何并进 <see cref="HostUnitId"/>。</summary>
/// <param name="PieceType">宿主件的层类型（<c>Armor</c> / <c>UnderGarment</c> / <c>Accessory</c>…）。
/// 素体定义的 piece 层号由它换算 —— 与 vpk2patch 那边一样，从**计划**取，不回读产物。</param>
public sealed record SectionalSplitTarget(
	string Slot,
	string BodyType,
	string HostUnitId,
	IReadOnlyList<string> DonorUnitIds,
	string PieceType = "")
{
	/// <summary>这个输出 unit 会承载几个来源 unit 的几何。</summary>
	public int SourceCount => DonorUnitIds.Count + 1;
}

/// <summary>
/// 一个要建出来的组。
/// <para>
/// <see cref="ExclusiveGroup"/> 非空时，同一互斥组里**最多一个**开启——基础件与各变体同组，
/// 于是"选一个变体"就等于"把基础件关掉"。这就是"变体替换"的全部实现，
/// 不需要第二个"替换"字段。
/// </para>
/// </summary>
public sealed record SectionalSplitGroup(
	string Name,
	string ExclusiveGroup,
	IReadOnlyList<string> MemberUnitIds,
	bool EnabledByDefault);

/// <summary>一个没能进任何输出 unit 的来源 unit，以及为什么。</summary>
public sealed record SectionalSplitDropped(string UnitId, string Reason);

/// <summary>拆件计划。<b>可以预演、可以逐条解释</b>，这是它存在的理由。</summary>
public sealed record SectionalSplitPlan(
	IReadOnlyList<SectionalSplitTarget> Targets,
	IReadOnlyList<SectionalSplitGroup> Groups,
	IReadOnlyList<SectionalSplitDropped> Dropped,
	IReadOnlyList<string> Warnings)
{
	public int SourceUnitCount => Targets.Sum(target => target.SourceCount) + Dropped.Count;
}

/// <summary>
/// 把"一个 Mod 里的一堆 unit"规划成"7 个标准槽 × 身形"的分件结构。
/// <para>
/// <b>纯函数。</b>不读文件、不碰几何 —— 这样"预演说会做什么"和"真做"能共用同一份判断，
/// 也让这套规则能离线测（本机没有任何"多槽位占满"的 legacy 模型 Mod 可当样本）。
/// </para>
/// <para>
/// <b>三条规则，都有理由：</b>
/// </para>
/// <list type="number">
/// <item><b>只按 (槽位, 身形) 分组，绝不跨身形合并。</b>游戏按件记录的 <c>body</c> 字段挑 unit；
/// 把 Slim 与 Stocky 的几何并进同一个 <c>Any</c> unit，两套会同时出现。</item>
/// <item><b>7 标准槽之外的槽位并进 Torso。</b>几何位置由骨骼决定，槽位只是分类；
/// 本体 9 个 customization 槽里，7 标准槽之外只有两个护肩槽。</item>
/// <item><b>每个 (槽位, 身形) 里挑一个宿主，其余都是 donor。</b>宿主 = 自带该槽位的那个 unit
/// （其次节数最多、再次 id 最小）——它承载"基础外观"，其余并进去当变体。</item>
/// </list>
/// </summary>
public static class SectionalSplitPlanner
{
	/// <summary>7 个标准槽。顺序固定，界面与产物都按这个顺序。</summary>
	public static IReadOnlyList<string> StandardSlots { get; } =
		["Helmet", "Torso", "Hip", "LeftArm", "RightArm", "LeftLeg", "RightLeg"];

	/// <summary>非标准槽位并进这里。</summary>
	public const string FallbackSlot = "Torso";

	/// <summary>
	/// 额外的"肩"槽并进这里。
	/// <para>
	/// 肩膀不是 7 标准槽之一，但它**有明确归属**：左肩是左臂的一部分、右肩是右臂的一部分。
	/// 并进 <see cref="FallbackSlot"/>（躯干）会让肩甲跟着躯干动、跟着躯干被关，
	/// 而游戏里它是跟手臂一起摆的。所以这里单列一条映射，不再落到兜底。
	/// </para>
	/// </summary>
	private static readonly IReadOnlyDictionary<string, string> ExtraSlotMapping =
		new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
		{
			["LeftShoulder"] = "LeftArm",
			["RightShoulder"] = "RightArm",
		};

	/// <summary>身形缺省值。与本体 <c>Any</c> 的口径一致。</summary>
	public const string DefaultBody = "Any";

	/// <summary>
	/// 槽位归一：认得的标准槽原样返回（大小写不敏感），左右肩并进左右臂，
	/// 其余一律进 <see cref="FallbackSlot"/>。
	/// </summary>
	public static string MapSlot(string? slot)
	{
		if (string.IsNullOrWhiteSpace(slot))
			return FallbackSlot;
		var trimmed = slot.Trim();
		foreach (var standard in StandardSlots)
		{
			if (string.Equals(standard, trimmed, StringComparison.OrdinalIgnoreCase))
				return standard;
		}

		return ExtraSlotMapping.TryGetValue(trimmed, out var mapped) ? mapped : FallbackSlot;
	}

	public static string NormalizeBody(string? body)
		=> string.IsNullOrWhiteSpace(body) ? DefaultBody : body.Trim();

	public static SectionalSplitPlan Plan(IReadOnlyList<SectionalSplitSource> units)
	{
		ArgumentNullException.ThrowIfNull(units);
		var warnings = new List<string>();
		var dropped = new List<SectionalSplitDropped>();
		var targets = new List<SectionalSplitTarget>();
		var groups = new List<SectionalSplitGroup>();

		// 没有几何的 unit 什么都不承载，直接丢（本体有 2833 个这种 spawner/标记类 unit）。
		var usable = new List<SectionalSplitSource>();
		foreach (var unit in units)
		{
			if (string.IsNullOrWhiteSpace(unit.UnitId))
			{
				dropped.Add(new SectionalSplitDropped(string.Empty, "unit id 为空。"));
				continue;
			}

			if (unit.SectionCount <= 0)
			{
				dropped.Add(new SectionalSplitDropped(unit.UnitId, "这个 unit 没有任何 section（spawner / 标记类），没有几何可搬。"));
				continue;
			}

			usable.Add(unit);
		}

		if (usable.Count == 0)
		{
			warnings.Add("没有任何带几何的 unit，拆不出东西。");
			return new SectionalSplitPlan([], [], dropped, warnings);
		}

		// 槽位归一会把非标准槽并进 Torso；这里先统计一遍，好把"并了哪些"说出来。
		var remapped = usable
			.Where(unit => !string.Equals(MapSlot(unit.Slot), unit.Slot?.Trim(), StringComparison.OrdinalIgnoreCase))
			.GroupBy(unit => unit.Slot ?? string.Empty)
			.OrderBy(group => group.Key, StringComparer.Ordinal)
			.ToArray();
		foreach (var group in remapped)
		{
			warnings.Add($"槽位「{group.Key}」不是 7 个标准槽之一，它的 {group.Count()} 个 unit 会并进 {FallbackSlot}。");
		}

		// 身形族规则（2026-11-10 定）：同一槽位最多两种形态 —— {Any} 或 {Stocky, Slim}。
		// 只要该槽出现过任一具体身形，就拆成两种身形；该槽里的 Any unit **两种身形都要**，
		// 因为 Any 的本义就是"这一份同时服务两种身形"（见 ArmorStructure 顶部第 2 条）。
		// 只有整槽都是 Any 时，才是一个 Any 槽。
		var buckets = new Dictionary<(string Slot, string Body), List<SectionalSplitSource>>();
		foreach (var slotGroup in usable.GroupBy(unit => MapSlot(unit.Slot)))
		{
			var hasSpecificBody = slotGroup.Any(unit =>
				!string.Equals(NormalizeBody(unit.BodyType), DefaultBody, StringComparison.OrdinalIgnoreCase));
			string[] family = hasSpecificBody ? ["Stocky", "Slim"] : [DefaultBody];
			foreach (var unit in slotGroup)
			{
				var body = NormalizeBody(unit.BodyType);
				string[] bodyTargets = string.Equals(body, DefaultBody, StringComparison.OrdinalIgnoreCase) ? family : [body];
				foreach (var bodyTarget in bodyTargets)
				{
					if (!buckets.TryGetValue((slotGroup.Key, bodyTarget), out var list))
						buckets[(slotGroup.Key, bodyTarget)] = list = [];
					if (!list.Contains(unit)) list.Add(unit);
				}
			}
		}

		foreach (var bucket in buckets
			.OrderBy(pair => StandardSlots.ToList().IndexOf(pair.Key.Slot))
			.ThenBy(pair => pair.Key.Body, StringComparer.Ordinal))
		{
			var members = bucket.Value.ToArray();
			var host = ChooseHost(members, bucket.Key.Slot);
			var donors = members
				.Where(unit => !ReferenceEquals(unit, host))
				.OrderBy(unit => unit.UnitId, StringComparer.Ordinal)
				.ToArray();

			targets.Add(new SectionalSplitTarget(
				bucket.Key.Slot,
				bucket.Key.Body,
				host.UnitId,
				[.. donors.Select(unit => unit.UnitId)],
				host.PieceType));

			// 基础件与各变体同属一个互斥组：选一个变体就等于把基础件关掉。
			var exclusive = $"{bucket.Key.Slot}/{bucket.Key.Body}";
			var slotLabel = bucket.Key.Slot;
			var bodySuffix = string.Equals(bucket.Key.Body, DefaultBody, StringComparison.OrdinalIgnoreCase)
				? string.Empty
				: $"（{bucket.Key.Body}）";
			groups.Add(new SectionalSplitGroup(
				$"{slotLabel}{bodySuffix}",
				exclusive,
				[host.UnitId],
				EnabledByDefault: true));

			for (var index = 0; index < donors.Length; index++)
			{
				var donor = donors[index];
				groups.Add(new SectionalSplitGroup(
					DonorGroupName(donor, slotLabel, index, donors.Length) + bodySuffix,
					exclusive,
					[donor.UnitId],
					EnabledByDefault: false));
			}

			if (members.Length > 1)
			{
				warnings.Add($"{bucket.Key.Slot}/{bucket.Key.Body} 有 {members.Length} 个来源 unit，"
					+ $"并成 1 个输出 unit；其中 {donors.Length} 个变成变体组，默认关闭。");
			}
		}

		// 同一个 (槽位, 身形) 里如果既有 Any 又有具体身形，游戏按 body 挑 unit 时会歧义。
		foreach (var slot in targets.Select(target => target.Slot).Distinct(StringComparer.Ordinal))
		{
			var bodies = targets.Where(target => target.Slot == slot).Select(target => target.BodyType).ToArray();
			if (bodies.Any(body => string.Equals(body, DefaultBody, StringComparison.OrdinalIgnoreCase))
				&& bodies.Any(body => !string.Equals(body, DefaultBody, StringComparison.OrdinalIgnoreCase)))
			{
				warnings.Add($"{slot} 同时存在 Any 与具体身形的 unit：游戏按件记录的 body 挑 unit，"
					+ "这两套会各自成为独立的输出 unit，实际显示哪一套取决于件记录，建议只保留一种。");
			}
		}

		return new SectionalSplitPlan(targets, groups, dropped, warnings);
	}

	/// <summary>
	/// 挑宿主：优先"自带该槽位"的那个（它本来就是这一槽的主体），其次节数最多，再次 id 最小。
	/// <b>顺序必须确定</b>，否则同一份输入两次跑出不同的产物，预演也就不可信了。
	/// </summary>
	private static SectionalSplitSource ChooseHost(IReadOnlyList<SectionalSplitSource> members, string slot)
		=> members
			.OrderByDescending(unit => string.Equals(unit.Slot?.Trim(), slot, StringComparison.OrdinalIgnoreCase))
			.ThenByDescending(unit => unit.SectionCount)
			.ThenBy(unit => unit.UnitId, StringComparer.Ordinal)
			.First();

	private static string DonorGroupName(SectionalSplitSource donor, string slot, int index, int total)
	{
		if (!string.IsNullOrWhiteSpace(donor.Name))
			return donor.Name.Trim();
		return total == 1 ? $"{slot} 变体" : $"{slot} 变体 {index + 1}";
	}
}
