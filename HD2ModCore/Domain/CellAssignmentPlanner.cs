using HD2ModAdaptation.Analysis;

namespace HD2ModCore.Domain;

/// <summary>一个目标桶最终拿到了什么。</summary>
public enum CellAssignmentKind
{
	/// <summary>同格子、同身形，一对一。</summary>
	Natural,
	/// <summary>来源是通用网格（或来源该格子只有一个身形），一个来源 unit 同时服务目标的两个身形。</summary>
	Broadcast,
	/// <summary>同槽位、换了层。</summary>
	LayerShift,
	/// <summary>换到了别的槽位。</summary>
	Rerouted,
	/// <summary>多个来源 unit 合进同一个目标 unit。</summary>
	Merged,
	/// <summary>没有来源，按"自动隐藏"处理。</summary>
	Hidden,
}

/// <summary>
/// 一个目标桶的分配结果。
/// </summary>
/// <param name="Cell">目标格子。</param>
/// <param name="Body">目标桶的身形。</param>
/// <param name="Target">目标该桶原本的 unit；<c>null</c> 表示目标没有这个桶（纯新增，理论上不会出现）。</param>
/// <param name="Sources">分给这个桶的来源 unit。空 = 隐藏；1 个 = 直接替换；2 个以上 = 合并。</param>
/// <param name="Kind">怎么分到的。</param>
public sealed record CellAssignment(
	ArmorCell Cell,
	UnitMeshBodyVariant Body,
	EquipmentUnitPart? Target,
	IReadOnlyList<EquipmentUnitPart> Sources,
	CellAssignmentKind Kind)
{
	public bool IsHidden => Sources.Count == 0;
	public bool RequiresMerge => Sources.Count > 1;
}

/// <summary>
/// 替换护甲的**装箱**规划：目标 kit 的格子是容量固定的箱子，来源 kit 的可见部件是货。
/// </summary>
/// <remarks>
/// 为什么是"来源找箱子"而不是"箱子找来源"：后者在来源比目标多的时候会**静默丢掉**来源的部件。
/// 一个来源部件丢了，mod 的模型就少一块，而用户不会知道少的是哪块。
///
/// 放置顺序（固定，保证同一输入产出同一结果）：
/// <list type="number">
///   <item><description><b>P1 自然 / 广播</b>：同格子。来源是通用网格时，一个 unit 同时服务目标两个身形。</description></item>
///   <item><description><b>P2 同槽改层</b>：同槽位还有空桶。</description></item>
///   <item><description><b>P3 跨槽改挂</b>：池子 = 胯 / 腿 / 臂 / 肩。胸与头不进池子（见下）。</description></item>
///   <item><description><b>P4 合并</b>：剩下的来源部件合进已经占用的桶，优先同槽。</description></item>
///   <item><description><b>P5 隐藏</b>：目标上仍然空着的桶。</description></item>
/// </list>
///
/// 两条硬规则来自游戏本身的行为：
/// <list type="bullet">
///   <item><description>**胸（躯干）与头（头盔）的部件不能外挂**：这两个部位在第一人称下被隐藏，
///   挂到别处会让它们在第一人称里冒出来。所以它们的多余部件只能原地合并。</description></item>
///   <item><description>**披风双向排除**：披风挂到别的部位、或别的部件挂进披风槽，都会渲染异常。
///   替换护甲链路不碰披风，所以来源的披风部件不参与、目标的披风桶不分配也不隐藏。</description></item>
/// </list>
///
/// 任意网格挂任意非披风部位都能正确渲染，所以改挂只需要挑一个空桶，不需要改注册表。
/// 分尸归属会跟着改挂走，但玩家死后很快切队友视角，这条按用户要求忽略。
/// </remarks>
public static class CellAssignmentPlanner
{
	/// <summary>可以互相改挂的槽位池：胯、双腿、双臂、双肩。不含头（0）、披风（1）、胸（2）。</summary>
	private static readonly HashSet<int> ReroutePool = BuildReroutePool();

	/// <summary>部位之间的"接近程度"，只用于排序，不影响可行性。0 = 同槽。</summary>
	private static readonly Dictionary<(int From, int To), int> RegionDistance = BuildRegionDistance();

	public static CellAssignmentResult Plan(
		ArmorCellTable source,
		ArmorCellTable target,
		CrossArmorBodyVariantPreference preference,
		bool autoHideUnmapped = true,
		bool allowReroute = true,
		bool allowMerge = true)
	{
		ArgumentNullException.ThrowIfNull(source);
		ArgumentNullException.ThrowIfNull(target);
		var preferredBody = preference == CrossArmorBodyVariantPreference.Slim
			? UnitMeshBodyVariant.Slim
			: UnitMeshBodyVariant.Stocky;

		var issues = new List<CoreIssue>();
		var assignments = new List<MutableAssignment>();
		var targetSlots = target.Slots
			.Where(slot => !slot.Cell.IsCape)
			.Select(slot => new MutableAssignment(slot.Cell, slot.Body, slot.Part))
			.ToList();

		// 来源池：排除披风。每个来源桶只能被"消费"一次；被 P1 广播复用的不算消费。
		var pool = source.Slots
			.Where(slot => !slot.Cell.IsCape)
			.Select(slot => slot.Part)
			.DistinctBy(part => part.UnitAssetKey)
			.OrderBy(part => part.Cell.Slot)
			.ThenBy(part => part.Cell.Layer)
			.ThenBy(part => BodyOrder(part.BodyVariant))
			.ThenBy(part => part.UnitAssetKey.FileId)
			.ToList();

		// ---- P1：同格子 ---------------------------------------------------------
		// 按格子成组处理，不能一个桶一个桶地看：来源只有一个具体身形时，它要先服务
		// 同身形那个桶、再广播给另一个身形；如果先处理同身形的桶并把它从池子里消费掉，
		// 另一个桶就永远等不到广播了。
		var used = new HashSet<AssetKey>();
		foreach (var cell in targetSlots.Select(slot => slot.Cell).Distinct())
		{
			var buckets = targetSlots.Where(slot => slot.Cell == cell).ToArray();
			var cellSources = pool.Where(part => part.Cell == cell).ToArray();
			if (cellSources.Length == 0) continue;

			// 通用网格：一个 unit 同时服务这个格子的每个身形。
			var universal = cellSources.FirstOrDefault(part => part.BodyVariant == UnitMeshBodyVariant.Any);
			if (universal is not null)
			{
				// 目标这个格子只有一个 Any 桶时，"通用网格填通用桶"其实是一对一，别标成广播。
				var kind = buckets.Length == 1 && buckets[0].Body == UnitMeshBodyVariant.Any
					? CellAssignmentKind.Natural
					: CellAssignmentKind.Broadcast;
				foreach (var bucket in buckets) bucket.Assign([universal], kind);
				used.Add(universal.UnitAssetKey);
				continue;
			}

			// 来源只有一个具体身形 → 精确命中同身形，其余身形广播（用户确认的规则）。
			if (cellSources.Length == 1)
			{
				var only = cellSources[0];
				foreach (var bucket in buckets)
				{
					var kind = bucket.Body == only.BodyVariant
						? CellAssignmentKind.Natural
						: CellAssignmentKind.Broadcast;
					bucket.Assign([only], kind);
				}
				used.Add(only.UnitAssetKey);
				continue;
			}

			// 来源是成对（Slim + Stocky）：先一对一。
			foreach (var bucket in buckets)
			{
				var exact = cellSources.FirstOrDefault(part => part.BodyVariant == bucket.Body);
				if (exact is null) continue;
				bucket.Assign([exact], CellAssignmentKind.Natural);
				used.Add(exact.UnitAssetKey);
			}

			// 目标该桶是 Any、来源是成对：按"优先身形"挑一个，另一个留作多余件。
			foreach (var bucket in buckets.Where(slot => slot.IsFree && slot.Body == UnitMeshBodyVariant.Any))
			{
				var preferred = cellSources.FirstOrDefault(part => part.BodyVariant == preferredBody)
					?? cellSources.FirstOrDefault();
				if (preferred is null) continue;
				bucket.Assign([preferred], CellAssignmentKind.Broadcast);
				used.Add(preferred.UnitAssetKey);
			}
		}
		pool.RemoveAll(part => used.Contains(part.UnitAssetKey));

		// ---- P2 / P3：改挂 ------------------------------------------------------
		if (allowReroute)
		{
			foreach (var part in pool.ToArray())
			{
				var free = targetSlots.Where(slot => slot.IsFree).ToArray();
				if (free.Length == 0) break;

				// 胸/头部件永远不外挂。
				var inPool = !part.Cell.IsFirstPersonHidden && ReroutePool.Contains(part.Cell.Slot);
				var candidates = (inPool ? free : free.Where(slot => slot.Cell.Slot == part.Cell.Slot)).ToArray();
				if (candidates.Length == 0) continue;

				var destination = candidates
					.Where(slot => slot.AcceptsBody(part.BodyVariant))
					.OrderBy(slot => Distance(part.Cell.Slot, slot.Cell.Slot))
					.ThenBy(slot => slot.Cell.Layer)
					.ThenBy(slot => BodyOrder(slot.Body))
					.FirstOrDefault();
				if (destination is null) continue;

				var kind = destination.Cell.Slot == part.Cell.Slot
					? CellAssignmentKind.LayerShift
					: CellAssignmentKind.Rerouted;
				destination.Assign([part], kind);
				pool.Remove(part);
			}
		}

		// ---- P4：合并 -----------------------------------------------------------
		if (allowMerge && pool.Count != 0)
		{
			foreach (var part in pool.ToArray())
			{
				var occupied = targetSlots
					.Where(slot => !slot.IsFree)
					.Where(slot => slot.AcceptsBody(part.BodyVariant))
					// 胸/头部件只能合进同槽（不能借道别的部位）。
					.Where(slot => !part.Cell.IsFirstPersonHidden || slot.Cell.Slot == part.Cell.Slot)
					.ToArray();
				if (occupied.Length == 0) continue;

				var destination = occupied
					.OrderBy(slot => Distance(part.Cell.Slot, slot.Cell.Slot))
					// 合并方向是"往高处并"：多余件并进该部位**已有层里最高的那一层**。
					// 这跟用户给的兜底链一致（多余的第 2 层并进第 1 层；只有第 0 层时全并进第 0 层），
					// 也让"装饰层并进护甲层"这种视觉分组保持合理。
					.ThenByDescending(slot => slot.Cell.Layer)
					.ThenBy(slot => BodyOrder(slot.Body))
					.First();
				destination.Merge(part);
				pool.Remove(part);
			}
		}

		// ---- P5：隐藏 -----------------------------------------------------------
		foreach (var assignment in targetSlots)
		{
			if (!assignment.IsFree) continue;
			if (!autoHideUnmapped)
			{
				assignment.Skip();
				continue;
			}

			assignment.Kind = CellAssignmentKind.Hidden;
		}

		// ---- 报告 ---------------------------------------------------------------
		foreach (var part in pool)
		{
			issues.Add(new CoreIssue(
				CoreIssueSeverity.Warning,
				"CellAssignmentSourceDropped",
				$"来源部件 0x{part.UnitAssetKey.FileId:x16}（{part.Cell.Display}）找不到可放置的目标 unit，未写入计划。",
				null,
				null));
		}
		foreach (var part in source.Unplaced)
		{
			issues.Add(new CoreIssue(
				CoreIssueSeverity.Warning,
				"CellAssignmentSourceUnplaced",
				$"来源 unit 0x{part.UnitAssetKey.FileId:x16} 在原版注册表里没有权威归属，未参与自动分配。",
				null,
				null));
		}
		foreach (var duplicate in source.Duplicates)
		{
			issues.Add(new CoreIssue(
				CoreIssueSeverity.Warning,
				"CellAssignmentSourceDuplicate",
				$"来源在 {duplicate.Cell.Display} / {ArmorStructure.DisplayBody(duplicate.Body)} 上出现了多个 unit，只采用了其中一个。",
				null,
				null));
		}

		var assignmentsOut = targetSlots
			.Where(assignment => assignment.Kind is not null)
			.OrderBy(assignment => assignment.Cell.Slot)
			.ThenBy(assignment => assignment.Cell.Layer)
			.ThenBy(assignment => BodyOrder(assignment.Body))
			.Select(assignment => assignment.ToRecord())
			.ToArray();

		return new CellAssignmentResult(assignmentsOut, pool.ToArray(), issues);
	}

	private sealed class MutableAssignment(ArmorCell cell, UnitMeshBodyVariant body, EquipmentUnitPart target)
	{
		public ArmorCell Cell { get; } = cell;
		public UnitMeshBodyVariant Body { get; } = body;
		public EquipmentUnitPart Target { get; } = target;
		public List<EquipmentUnitPart> Sources { get; } = [];
		public CellAssignmentKind? Kind { get; set; }

		public bool IsFree => Sources.Count == 0 && Kind is null;

		public bool AcceptsBody(UnitMeshBodyVariant sourceBody)
			=> Body == UnitMeshBodyVariant.Any
				|| sourceBody == UnitMeshBodyVariant.Any
				|| Body == sourceBody;

		public void Assign(IReadOnlyList<EquipmentUnitPart> sources, CellAssignmentKind kind)
		{
			Sources.Clear();
			Sources.AddRange(sources);
			Kind = kind;
		}

		public void Merge(EquipmentUnitPart source)
		{
			Sources.Add(source);
			Kind = CellAssignmentKind.Merged;
		}

		public void Skip() => Kind = null;

		public CellAssignment ToRecord()
			=> new(Cell, Body, Target, Sources.ToArray(), Kind ?? CellAssignmentKind.Hidden);
	}

	private static int Distance(int from, int to)
	{
		if (from == to) return 0;
		if (RegionDistance.TryGetValue((from, to), out var distance)) return distance;
		return 100 + Math.Abs(from - to);
	}

	private static int BodyOrder(UnitMeshBodyVariant body) => body switch
	{
		UnitMeshBodyVariant.Slim => 0,
		UnitMeshBodyVariant.Stocky => 1,
		UnitMeshBodyVariant.Any => 2,
		_ => 3,
	};

	private static HashSet<int> BuildReroutePool()
	{
		var pool = new HashSet<int>();
		foreach (var part in new[] { ArmorPart.Hips, ArmorPart.LeftLeg, ArmorPart.RightLeg, ArmorPart.LeftArm, ArmorPart.RightArm, ArmorPart.LeftShoulder, ArmorPart.RightShoulder })
		{
			if (ArmorStructure.SlotOf(part) is { } slot) pool.Add((int)slot);
		}
		return pool;
	}

	// 只用于排序。数值越小越"接近"，分尸时的归属也越自然。
	private static Dictionary<(int From, int To), int> BuildRegionDistance()
	{
		var pairs = new (ArmorPart From, ArmorPart To, int Distance)[]
		{
			(ArmorPart.Hips, ArmorPart.LeftLeg, 1),
			(ArmorPart.Hips, ArmorPart.RightLeg, 1),
			(ArmorPart.LeftLeg, ArmorPart.RightLeg, 1),
			(ArmorPart.LeftArm, ArmorPart.LeftShoulder, 1),
			(ArmorPart.RightArm, ArmorPart.RightShoulder, 1),
			(ArmorPart.LeftArm, ArmorPart.RightArm, 2),
			(ArmorPart.LeftShoulder, ArmorPart.RightShoulder, 2),
			(ArmorPart.Hips, ArmorPart.LeftArm, 3),
			(ArmorPart.Hips, ArmorPart.RightArm, 3),
			(ArmorPart.LeftLeg, ArmorPart.LeftArm, 3),
			(ArmorPart.RightLeg, ArmorPart.RightArm, 3),
		};
		var map = new Dictionary<(int, int), int>();
		foreach (var (from, to, distance) in pairs)
		{
			if (ArmorStructure.SlotOf(from) is not { } a || ArmorStructure.SlotOf(to) is not { } b) continue;
			map[((int)a, (int)b)] = distance;
			map[((int)b, (int)a)] = distance;
		}
		return map;
	}
}

/// <summary>装箱结果。</summary>
/// <param name="Assignments">每个目标桶一条，已按 槽 → 层 → 身形 排序。</param>
/// <param name="Unassigned">没能放进任何桶的来源 unit。</param>
public sealed record CellAssignmentResult(
	IReadOnlyList<CellAssignment> Assignments,
	IReadOnlyList<EquipmentUnitPart> Unassigned,
	IReadOnlyList<CoreIssue> Issues)
{
	public int NaturalCount => Assignments.Count(a => a.Kind == CellAssignmentKind.Natural);
	public int BroadcastCount => Assignments.Count(a => a.Kind == CellAssignmentKind.Broadcast);
	public int ReroutedCount => Assignments.Count(a => a.Kind is CellAssignmentKind.LayerShift or CellAssignmentKind.Rerouted);
	public int MergedCount => Assignments.Count(a => a.Kind == CellAssignmentKind.Merged);
	public int HiddenCount => Assignments.Count(a => a.Kind == CellAssignmentKind.Hidden);
}
