using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

/// <summary>
/// 「这件护甲包含哪些 unit」的权威来源：游戏的注册文件（<c>generated_customization_armor_sets</c>）。
///
/// 为什么不读 <c>game_data_authority_pieces</c> 那张表：它是注册文件的**派生缓存**，
/// 而派生这一步会丢行——实测 RE-2310 的头盔套（item 1745464610）在那张表里是 0 行，
/// 护甲套还缺一个槽位。读源头就没有"缓存和源头不一致"这一类问题。
///
/// 只取 unit **键**，不取槽位/身形：那些从 unit 自己的 <c>CustomizationInfo</c> 取更准。
/// 实测件表里的 <c>slot</c> 编号与 unit 自身语义在 5/6/7 三个档位上不一致，
/// 两套编号本来就不是一回事，混用必然错位。
///
/// The authoritative "which units does this armour have" comes from the game's registration
/// file, not from game_data_authority_pieces — that table is a derived cache and we measured it
/// dropping rows (RE-2310's helmet kit has zero rows). Only unit keys are taken from here;
/// slot/body come from each unit's own CustomizationInfo, because the piece table's slot
/// numbering disagrees with the unit's own semantics.
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
