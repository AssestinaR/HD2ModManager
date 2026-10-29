namespace HD2ModCore.Domain;

// 「这些护甲条目包含哪些 unit」的事实（信息中心的无节点事实）。
//
// 来源是游戏注册文件（generated_customization_armor_sets），不是
// game_data_authority_pieces 那张派生表——后者是缓存，源头永远比缓存可信。
// 注意：当初支撑这条选择的症状（RE-2310 头盔套 0 行、护甲套缺一个槽位）在
// authority_pieces_schema = 2 之后已经不复现（实测 0/422 个 kit 丢行），
// 所以别再用「那张表会丢行」当理由，也别据此认为两条路径会给出不同答案。
//
// 为什么无节点：注册文件描述的是**游戏数据**，与哪个 Mod 提供模型无关；
// 谁提供这个 unit 由部署图（DeployedGraph）决定。身份因此是"游戏目录 + 这一批护甲条目"，
// 与 ArmorCoverage / UnitSectionStructure 同一套做法。
//
// 为什么把 MissingItems 也带上：注册文件里**没有 kit** 与 kit 存在但**一个 unit 都没有**
// 是两种不同的坏法——前者是数据缺失，后者是这件护甲确实没有几何。
// 界面要能分辨，否则只能显示一棵莫名其妙的空树。
//
// Node-less: the registration file describes game data, not whichever mod supplies the model.
// Identity is the game directory plus the batch of armour items, like ArmorCoverage.
// MissingItems is carried separately because "no kit at all" and "a kit with no units" are
// different failures and the UI has to tell them apart.

/// <summary>
/// 一批护甲条目的 unit 表。<c>UnitsByItem</c> 的键是护甲条目 itemId，
/// 值是该条目（含组合护甲的各个成员 kit）去重后的 unit 键。
/// </summary>
public sealed record ArmourUnitTableFacts(
	string UnitTableGeneration,
	IReadOnlyDictionary<uint, IReadOnlyList<string>> UnitsByItem,
	IReadOnlyList<uint> MissingItems,
	IReadOnlyList<CoreIssue> Issues)
{
	/// <summary>某个条目的 unit 键；没有条目时返回空（而不是 null，调用方不必再判空）。</summary>
	public IReadOnlyList<string> ForItem(uint itemId)
		=> UnitsByItem.TryGetValue(itemId, out var units) ? units : Array.Empty<string>();

	/// <summary>把多个条目的 unit 键并起来去重——组合护甲就是多个条目。</summary>
	public IReadOnlyList<string> ForItems(IEnumerable<uint> itemIds)
	{
		ArgumentNullException.ThrowIfNull(itemIds);
		var merged = new SortedSet<string>(StringComparer.Ordinal);
		foreach (var itemId in itemIds)
		{
			foreach (var unit in ForItem(itemId))
				merged.Add(unit);
		}
		return merged.ToArray();
	}
}
