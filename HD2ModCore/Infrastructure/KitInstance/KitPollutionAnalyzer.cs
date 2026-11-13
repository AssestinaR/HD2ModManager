using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure.KitInstance;

/// <summary>游戏数据里「某套 → 某个 Unit」的一条引用（<c>game_data_authority_pieces</c> 的一行，只留分析要用的列）。</summary>
public readonly record struct KitPieceRef(uint ItemId, uint Slot, ulong UnitFileId);

/// <summary>一套被 Mod 连带影响的护甲，以及原因。</summary>
/// <param name="Units">Mod 覆盖的、且这套直接引用的原版 Unit。</param>
/// <param name="Materials">Mod 覆盖的、且这套的某个 Unit 绑定了的原版材质（材质分析可用时才有）。</param>
public sealed record KitPollutionAffectedKit(
	uint ItemId,
	string Name,
	IReadOnlyList<ulong> Units,
	IReadOnlyList<ulong> Materials)
{
	/// <summary>
	/// 游戏数据里有可读的名字。没有本地化名的记录，<c>display_name</c> 是空的或「0x2cbae902」这种哈希，
	/// 拿它点名用户看不懂，所以排在最后，并显示成「未命名护甲」。
	/// </summary>
	public bool IsNamed => !string.IsNullOrWhiteSpace(Name) && !Name.StartsWith("0x", StringComparison.OrdinalIgnoreCase);

	/// <summary>点名文案用的名字：有名字就只写名字，没有就写「未命名护甲」。</summary>
	public string Label => IsNamed ? Name : "未命名护甲";

	/// <summary>详细列表用：带上 item_id，同名的两条记录（比如玩家版与 NPC 版）也能分清。</summary>
	public string DetailedLabel => IsNamed ? $"{Name}（{ItemId}）" : $"未命名护甲（{ItemId}）";
}

public enum KitPollutionCauseKind
{
	Unit,
	Material,
}

/// <summary>一个原因：Mod 覆盖的某个原版 Unit 或材质，以及它连带影响的护甲（排序同 <see cref="KitPollutionResult.Kits"/>）。</summary>
public sealed record KitPollutionCause(
	KitPollutionCauseKind Kind,
	ulong Id,
	IReadOnlyList<KitPollutionAffectedKit> Kits)
{
	public string KindLabel => Kind == KitPollutionCauseKind.Unit ? "部件" : "材质";
	public string ShortId => Id.ToString("x16", System.Globalization.CultureInfo.InvariantCulture)[..8] + "…";
}

/// <summary>污染分析结果。</summary>
/// <param name="VanillaTextureCount">Mod 覆盖的原版贴图数。本阶段不反查它们影响谁，只如实报数。</param>
/// <param name="MaterialAnalysisAvailable">材质 → Unit 映射是否参与了本次分析（缓存没好时为 false，只有 Unit 部分的结论）。</param>
/// <param name="Causes">按影响套数从多到少排的原因；只含至少影响一套的。</param>
public sealed record KitPollutionResult(
	IReadOnlyList<KitPollutionAffectedKit> Kits,
	int VanillaTextureCount,
	bool MaterialAnalysisAvailable,
	IReadOnlyList<KitPollutionCause>? Causes = null)
{
	public IReadOnlyList<KitPollutionCause> CauseList => Causes ?? [];

	public int UnitCauseCount => CauseList.Count(cause => cause.Kind == KitPollutionCauseKind.Unit);
	public int MaterialCauseCount => CauseList.Count(cause => cause.Kind == KitPollutionCauseKind.Material);

	public const int PollutionThreshold = 2;

	/// <summary>规划 §5.2：只影响一套不算污染。</summary>
	public bool IsPolluted => Kits.Count >= PollutionThreshold;

	public static KitPollutionResult None(bool materialAnalysisAvailable = false)
		=> new([], 0, materialAnalysisAvailable, []);
}

/// <summary>
/// 污染分析（规划 §5.2）。纯函数：所有数据由调用方传入，没有任何 IO，可以离线单测。
/// <para>
/// 受影响的套 = 引用了「Mod 覆盖的原版 Unit」的所有套 ∪ 有 Unit 绑定了「Mod 覆盖的原版材质」的所有套。
/// 披风（slot 1）与 unit 0 不算。非原版资源（文件 ID 不在游戏数据索引里）是 Mod 自己新增的，不污染别人。
/// </para>
/// </summary>
public static class KitPollutionAnalyzer
{
	public const ulong UnitTypeId = 0xe0a48d0be9a7453f;
	public const ulong MaterialTypeId = 0xeac0b497876adedf;
	public const ulong TextureTypeId = 0xcd4238c6a0c69e32;

	/// <summary>披风所在的 slot。披风不参与污染判断（规划 §0 部件集 P(A)）。</summary>
	public const uint CapeSlot = 1;

	/// <param name="modAssets">Mod 覆盖的资源键（<c>ModContentFacts.Structure.Assets</c>）。</param>
	/// <param name="isVanilla">资源键是否在游戏数据索引的 <c>assets</c> 表里。</param>
	/// <param name="pieces">全部「套 → Unit」引用；披风与 unit 0 在这里过滤。</param>
	/// <param name="kitNames">item_id → <c>display_name</c>。</param>
	/// <param name="unitsByMaterial">材质 → 绑定它的 Unit 集合；材质缓存没好时传 <c>null</c>。</param>
	public static KitPollutionResult Analyze(
		IEnumerable<AssetKey> modAssets,
		Func<AssetKey, bool> isVanilla,
		IEnumerable<KitPieceRef> pieces,
		IReadOnlyDictionary<uint, string> kitNames,
		IReadOnlyDictionary<ulong, IReadOnlyCollection<ulong>>? unitsByMaterial)
	{
		ArgumentNullException.ThrowIfNull(modAssets);
		ArgumentNullException.ThrowIfNull(isVanilla);
		ArgumentNullException.ThrowIfNull(pieces);
		ArgumentNullException.ThrowIfNull(kitNames);

		var modUnits = new HashSet<ulong>();
		var modMaterials = new HashSet<ulong>();
		var textureCount = 0;
		foreach (var key in modAssets.Distinct())
		{
			if (key.TypeId is not (UnitTypeId or MaterialTypeId or TextureTypeId)) continue;
			if (!isVanilla(key)) continue;
			switch (key.TypeId)
			{
				case UnitTypeId: modUnits.Add(key.FileId); break;
				case MaterialTypeId: modMaterials.Add(key.FileId); break;
				default: textureCount++; break;
			}
		}

		var itemsByUnit = new Dictionary<ulong, HashSet<uint>>();
		foreach (var piece in pieces)
		{
			if (piece.Slot == CapeSlot || piece.UnitFileId == 0) continue;
			if (!itemsByUnit.TryGetValue(piece.UnitFileId, out var items))
				itemsByUnit[piece.UnitFileId] = items = [];
			items.Add(piece.ItemId);
		}

		var unitReasons = new Dictionary<uint, SortedSet<ulong>>();
		foreach (var unit in modUnits)
		{
			if (!itemsByUnit.TryGetValue(unit, out var items)) continue;
			foreach (var item in items) Reason(unitReasons, item).Add(unit);
		}

		var materialReasons = new Dictionary<uint, SortedSet<ulong>>();
		if (unitsByMaterial is not null)
		{
			foreach (var material in modMaterials)
			{
				if (!unitsByMaterial.TryGetValue(material, out var units)) continue;
				foreach (var unit in units)
				{
					if (!itemsByUnit.TryGetValue(unit, out var items)) continue;
					foreach (var item in items) Reason(materialReasons, item).Add(material);
				}
			}
		}

		var kits = unitReasons.Keys.Concat(materialReasons.Keys)
			.Distinct()
			.Select(item => new KitPollutionAffectedKit(
				item,
				kitNames.TryGetValue(item, out var name) ? name : string.Empty,
				unitReasons.TryGetValue(item, out var u) ? u.ToArray() : [],
				materialReasons.TryGetValue(item, out var m) ? m.ToArray() : []))
			.OrderBy(kit => kit, KitOrder)
			.ToArray();

		var causes = kits
			.SelectMany(kit => kit.Units.Select(id => (Kind: KitPollutionCauseKind.Unit, Id: id, Kit: kit))
				.Concat(kit.Materials.Select(id => (Kind: KitPollutionCauseKind.Material, Id: id, Kit: kit))))
			.GroupBy(entry => (entry.Kind, entry.Id))
			.Select(group => new KitPollutionCause(
				group.Key.Kind, group.Key.Id, group.Select(entry => entry.Kit).OrderBy(kit => kit, KitOrder).ToArray()))
			.OrderByDescending(cause => cause.Kits.Count)
			.ThenBy(cause => cause.Kind)
			.ThenBy(cause => cause.Id)
			.ToArray();

		return new KitPollutionResult(kits, textureCount, unitsByMaterial is not null, causes);
	}

	/// <summary>
	/// 稳定顺序：有名字的在前、按名字，再按 item_id；没名字的（哈希名）垫底。
	/// 同一份输入永远得到同一份列表（部署文案与界面一致）。
	/// </summary>
	private static readonly Comparer<KitPollutionAffectedKit> KitOrder = Comparer<KitPollutionAffectedKit>.Create((a, b) =>
	{
		var named = b.IsNamed.CompareTo(a.IsNamed);
		if (named != 0) return named;
		var name = a.IsNamed ? string.CompareOrdinal(a.Name, b.Name) : 0;
		return name != 0 ? name : a.ItemId.CompareTo(b.ItemId);
	});

	/// <summary>「1 个原版部件、9 个原版材质」。</summary>
	public static string DescribeReplaced(KitPollutionResult result)
	{
		ArgumentNullException.ThrowIfNull(result);
		var parts = new List<string>();
		if (result.UnitCauseCount > 0) parts.Add($"{result.UnitCauseCount} 个原版部件");
		if (result.MaterialCauseCount > 0) parts.Add($"{result.MaterialCauseCount} 个原版材质");
		return string.Join("、", parts);
	}

	/// <summary>
	/// 按顺序取最多 <paramref name="count"/> 个不重名的点名（同名的玩家版与 NPC 版只点一次）。
	/// <paramref name="complete"/> 表示点到的名字已经覆盖了全部护甲（可以写「共」而不是「等」）。
	/// </summary>
	public static IReadOnlyList<string> PickLabels(IReadOnlyList<KitPollutionAffectedKit> kits, int count, out bool complete)
	{
		ArgumentNullException.ThrowIfNull(kits);
		var labels = new List<string>();
		var covered = 0;
		foreach (var kit in kits)
		{
			if (labels.Contains(kit.Label, StringComparer.Ordinal)) { covered++; continue; }
			if (labels.Count >= count) break;
			labels.Add(kit.Label);
			covered++;
		}
		complete = covered == kits.Count && kits.All(kit => kit.IsNamed);
		return labels;
	}

	private static SortedSet<ulong> Reason(Dictionary<uint, SortedSet<ulong>> map, uint item)
	{
		if (!map.TryGetValue(item, out var set)) map[item] = set = [];
		return set;
	}

	/// <summary>
	/// 部署问题文案（规划 §9 P2）：「『X』替换了 9 个原版材质，会连带改变 A、B 等 N 套护甲，可在详情页转专属」。
	/// 先说原因，再点名最多 2 个名字；名字已经覆盖全部护甲时写「共 N 套」，否则写「等 N 套」。
	/// </summary>
	public static string FormatDeploymentWarning(string modName, KitPollutionResult result)
	{
		ArgumentNullException.ThrowIfNull(result);
		var shown = string.Join("、", PickLabels(result.Kits, 2, out var complete));
		var joiner = complete ? "共" : "等";
		var replaced = DescribeReplaced(result);
		var cause = replaced.Length == 0 ? string.Empty : $"替换了 {replaced}，";
		return $"『{modName}』{cause}会连带改变 {shown} {joiner} {result.Kits.Count} 套护甲，可在详情页转专属";
	}
}
