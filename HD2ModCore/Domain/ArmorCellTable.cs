using HD2ModAdaptation.Analysis;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;

namespace HD2ModCore.Domain;

/// <summary>
/// 一个"桶"：某个格子在某个身形下对应的那一个 unit。
/// </summary>
/// <remarks>
/// 一个格子最多有两个桶（Slim / Stocky），或者只有一个 <see cref="UnitMeshBodyVariant.Any"/> 桶。
/// 实测 2379 个原版格子里没有第三种形态——不会出现"只有 Slim 没有 Stocky"的半满格子。
/// </remarks>
public sealed record ArmorCellSlot(ArmorCell Cell, UnitMeshBodyVariant Body, EquipmentUnitPart Part)
{
	/// <summary>该桶能不能接收指定身形的来源部件。</summary>
	public bool Accepts(UnitMeshBodyVariant sourceBody)
		=> Body == UnitMeshBodyVariant.Any
			|| sourceBody == UnitMeshBodyVariant.Any
			|| Body == sourceBody;
}

/// <summary>
/// 一个 kit 的格子表：把"一堆 unit"折成 <c>(格子, 身形) → unit</c>。
/// </summary>
/// <remarks>
/// 为什么要这张表：替换护甲的本质是**装箱**——目标 kit 的格子是容量固定的箱子，
/// 来源 kit 的可见部件是货。旧方案拿网格名去猜格子，而躯干的内层与护甲网格名完全相同
/// （都是 <c>Torso_Armor_*</c>），猜不出来。注册表直接给出格子，于是不需要猜。
///
/// 没有权威格子的 unit 不会被丢掉：它们进 <see cref="Unplaced"/>，由界面照实列出，
/// 只能靠手动映射使用。
/// </remarks>
public sealed class ArmorCellTable
{
	private readonly Dictionary<(ArmorCell Cell, UnitMeshBodyVariant Body), ArmorCellSlot> byBucket;

	private ArmorCellTable(
		IReadOnlyList<ArmorCellSlot> slots,
		IReadOnlyList<EquipmentUnitPart> unplaced,
		IReadOnlyList<ArmorCellSlot> duplicates)
	{
		Slots = slots;
		Unplaced = unplaced;
		Duplicates = duplicates;
		byBucket = slots.ToDictionary(slot => (slot.Cell, slot.Body));
	}

	/// <summary>每个 (格子, 身形) 一个代表 unit，按 槽 → 层 → 身形 排序。</summary>
	public IReadOnlyList<ArmorCellSlot> Slots { get; }

	/// <summary>在注册表里查不到格子的 unit。它们只能靠手动映射参与。</summary>
	public IReadOnlyList<EquipmentUnitPart> Unplaced { get; }

	/// <summary>
	/// 同一个 (格子, 身形) 上出现了多个 unit。原版数据里不该发生；真发生了说明来源 kit
	/// 被改过，或者索引把两个不同 kit 的 unit 混进了同一张表。界面要报出来，不能悄悄丢。
	/// </summary>
	public IReadOnlyList<ArmorCellSlot> Duplicates { get; }

	public IReadOnlyList<ArmorCell> Cells => Slots.Select(slot => slot.Cell).Distinct().ToArray();

	public bool IsEmpty => Slots.Count == 0;

	public static ArmorCellTable Empty { get; } = new([], [], []);

	public static ArmorCellTable Build(IEnumerable<EquipmentUnitPart> parts)
	{
		ArgumentNullException.ThrowIfNull(parts);
		var unplaced = new List<EquipmentUnitPart>();
		var duplicates = new List<ArmorCellSlot>();
		var best = new Dictionary<(ArmorCell Cell, UnitMeshBodyVariant Body), EquipmentUnitPart>();

		foreach (var part in parts)
		{
			if (!part.Cell.IsKnown)
			{
				unplaced.Add(part);
				continue;
			}

			var key = (part.Cell, part.BodyVariant);
			if (!best.TryGetValue(key, out var existing))
			{
				best.Add(key, part);
				continue;
			}

			// 同一个桶里只留一个 unit。选几何更"实"的那个，与旧规划器的取代表口径一致。
			var winner = IsPreferred(part, existing) ? part : existing;
			var loser = ReferenceEquals(winner, part) ? existing : part;
			duplicates.Add(new ArmorCellSlot(key.Cell, key.BodyVariant, loser));
			best[key] = winner;
		}

		var slots = best
			.Select(pair => new ArmorCellSlot(pair.Key.Cell, pair.Key.Body, pair.Value))
			.OrderBy(slot => slot.Cell.Slot)
			.ThenBy(slot => slot.Cell.Layer)
			.ThenBy(slot => BodyOrder(slot.Body))
			.ThenBy(slot => slot.Part.UnitAssetKey.FileId)
			.ToArray();

		return new ArmorCellTable(
			slots,
			unplaced.OrderBy(part => part.UnitAssetKey.FileId).ToArray(),
			duplicates);
	}

	/// <summary>精确命中：同格子、同身形。</summary>
	public ArmorCellSlot? FindExact(ArmorCell cell, UnitMeshBodyVariant body)
		=> byBucket.GetValueOrDefault((cell, body));

	/// <summary>通用网格：同格子、身形为 Any。</summary>
	public ArmorCellSlot? FindAny(ArmorCell cell)
		=> byBucket.GetValueOrDefault((cell, UnitMeshBodyVariant.Any));

	/// <summary>该格子上所有的桶（用于"来源 Any 广播到目标的两个身形"）。</summary>
	public IReadOnlyList<ArmorCellSlot> SlotsOf(ArmorCell cell)
		=> Slots.Where(slot => slot.Cell == cell).ToArray();

	public bool Contains(ArmorCell cell) => Slots.Any(slot => slot.Cell == cell);

	private static bool IsPreferred(EquipmentUnitPart candidate, EquipmentUnitPart current)
	{
		var rank = UnitGeometryRanker.GetRank(candidate.GeometryQuality)
			.CompareTo(UnitGeometryRanker.GetRank(current.GeometryQuality));
		if (rank != 0) return rank > 0;
		if (candidate.TriangleCount != current.TriangleCount) return candidate.TriangleCount > current.TriangleCount;
		if (candidate.VertexCount != current.VertexCount) return candidate.VertexCount > current.VertexCount;
		if (candidate.StoredBytes != current.StoredBytes) return candidate.StoredBytes > current.StoredBytes;
		return candidate.UnitAssetKey.FileId < current.UnitAssetKey.FileId;
	}

	private static int BodyOrder(UnitMeshBodyVariant body) => body switch
	{
		UnitMeshBodyVariant.Slim => 0,
		UnitMeshBodyVariant.Stocky => 1,
		UnitMeshBodyVariant.Any => 2,
		_ => 3,
	};
}
