using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

/// <summary>
/// 「这件护甲包含哪些 unit」的权威来源：游戏的注册文件（<c>generated_customization_armor_sets</c>）。
///
/// 为什么不读 <c>game_data_authority_pieces</c> 那张表：它是注册文件的**派生缓存**，
/// 而缓存和源头是两件事——源头永远对，缓存要靠一次派生步骤维持正确。
/// 读源头就没有「缓存和源头不一致」这一类问题。
///
/// ⚠ 这条理由最初是靠「实测那张表会丢行」支撑的（RE-2310 的头盔套 0 行、护甲套缺一个槽位）。
/// <b>那个症状现在不复现</b>：`authority_pieces_schema = 2` 把插入改成
/// `ON CONFLICT … RETURNING` 之后，实测 422 个 kit 里 0 个丢行、RE-2310 有 1 行、
/// 138 个护甲套全部齐 2..7 六槽。所以这里是「按原则读源头」，不是「因为缓存有 bug」——
/// 评估这条路径的代价时别按「缓存会丢行」来估。
///
/// 只取 unit **键**，不取槽位/身形：那些从 unit 自己的 <c>CustomizationInfo</c> 取更准。
/// 实测件表里的 <c>slot</c> 编号与 unit 自身语义在 5/6/7 三个档位上不一致，
/// 两套编号本来就不是一回事，混用必然错位。
///
/// The authoritative "which units does this armour have" comes from the game's registration
/// file, not from game_data_authority_pieces — that table is a derived cache, and the source
/// of truth is the source. Only unit keys are taken from here; slot/body come from each unit's
/// own CustomizationInfo, because the piece table's slot numbering disagrees with the unit's
/// own semantics. NOTE: the "the cache drops rows" symptom that originally motivated this is
/// gone since authority_pieces_schema = 2 (measured: 0 of 422 kits lose rows).
/// </summary>
public static class ArmourUnitTableSource
{
	/// <summary>
	/// 取这些护甲条目（含组合护甲的各个成员 kit）的全部 unit 键。
	/// 组合护甲必须按成员逐个取——只查第一个 item 会漏掉其余成员的全部 unit。
	/// </summary>
	public static IReadOnlyList<string> ReadUnitIds(
		GameDataAuthoritySnapshot snapshot,
		IReadOnlyCollection<uint> itemIds)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		ArgumentNullException.ThrowIfNull(itemIds);
		if (itemIds.Count == 0)
			return Array.Empty<string>();

		var wanted = itemIds.ToHashSet();
		var units = new HashSet<string>(StringComparer.Ordinal);
		foreach (var kit in snapshot.Kits)
		{
			if (!wanted.Contains(kit.ItemId))
				continue;
			foreach (var piece in kit.Pieces)
			{
				// 只要 unit 类资产：件表里也可能挂材质/贴图之类的引用。
				if (piece.UnitAssetKey.TypeId != PatchUnitMeshReader.UnitTypeId)
					continue;
				var fileId = piece.UnitAssetKey.FileId;
				if (fileId == 0)
					continue;
				units.Add(fileId.ToString("x16", System.Globalization.CultureInfo.InvariantCulture));
			}
		}

		return units.Order(StringComparer.Ordinal).ToArray();
	}

	/// <summary>
	/// 这些 item 里有哪些在注册文件里**根本没有 kit**。空 kit 与"kit 存在但一个 unit 都没有"
	/// 是两种不同的坏法，界面要能分辨：前者是数据缺失，后者是这件护甲确实没有几何。
	/// </summary>
	public static IReadOnlyList<uint> FindMissingItems(
		GameDataAuthoritySnapshot snapshot,
		IReadOnlyCollection<uint> itemIds)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		ArgumentNullException.ThrowIfNull(itemIds);
		var present = snapshot.Kits.Select(kit => kit.ItemId).ToHashSet();
		return itemIds.Where(id => !present.Contains(id)).Order().ToArray();
	}
}
