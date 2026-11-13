using System.Globalization;
using HD2GameItemAssets;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure.KitInstance;

/// <summary>
/// 转专属的输入：Mod 里的 Unit / 材质 / 贴图条目，以及 Mod 自己的引用关系。
/// 引用关系只含 Mod 自己的内容（Unit → 材质表里的材质，材质 → 它引用的贴图）。
/// </summary>
public sealed record KitConversionModContent(
	IReadOnlyCollection<ulong> Units,
	IReadOnlyCollection<ulong> Materials,
	IReadOnlyCollection<ulong> Textures,
	IReadOnlyDictionary<ulong, IReadOnlyList<ulong>> UnitMaterials,
	IReadOnlyDictionary<ulong, IReadOnlyList<ulong>> MaterialTextures);

public enum KitHideReason
{
	/// <summary>这个原版共用部件的 Mod 版本被搬到了目标套的独有部件上，原件在目标套身上要藏掉。</summary>
	MovedAway,

	/// <summary>Mod 改过它绑定的原版材质（规划 D4：视为要把它藏掉或换掉）。</summary>
	MaterialReplaced,
}

public sealed record KitHideEntry(ulong Unit, KitHideReason Reason, IReadOnlyList<ulong> Materials);

/// <param name="SameSlot">目标部件与来源部件在目标套里同一个 slot。</param>
/// <param name="SameBody">……并且同一个身形。</param>
public sealed record KitUnitMove(ulong From, ulong To, bool SameSlot, bool SameBody);

/// <summary>
/// 一次转专属的完整预案（规划 §6.2 的输出）。失败时只有 <see cref="FailureReason"/> 有意义。
/// </summary>
public sealed record KitConversionPlan
{
	public required uint TargetItemId { get; init; }
	public string TargetName { get; init; } = string.Empty;
	public string RuleName { get; init; } = string.Empty;
	public bool Succeeded { get; init; }
	public string? FailureReason { get; init; }

	/// <summary>已经在目标套独有（或 D9 的同名共用）部件上、原地保留的 Mod 部件。</summary>
	public IReadOnlyList<ulong> CarrierUnits { get; init; } = [];

	/// <summary>搬迁表：Mod 里共用部件 → 目标套的空闲独有部件。</summary>
	public IReadOnlyList<KitUnitMove> UnitMoves { get; init; } = [];

	/// <summary>改 ID 表：材质（原 ID → 新 ID）。</summary>
	public IReadOnlyDictionary<ulong, ulong> MaterialRemaps { get; init; } = new Dictionary<ulong, ulong>();

	/// <summary>改 ID 表：贴图（原 ID → 新 ID）。</summary>
	public IReadOnlyDictionary<ulong, ulong> TextureRemaps { get; init; } = new Dictionary<ulong, ulong>();

	/// <summary>去掉的全局材质覆盖（不再以原 ID 输出）。</summary>
	public IReadOnlyList<ulong> RemovedMaterials { get; init; } = [];

	/// <summary>只被去掉的材质引用的贴图，一并去掉；另含「Mod 自己的、没有任何材质引用」的贴图（见 <see cref="OrphanTextures"/>）。</summary>
	public IReadOnlyList<ulong> RemovedTextures { get; init; } = [];

	/// <summary><see cref="RemovedTextures"/> 里没有任何材质引用的那几张（非原版）：留着没人用，手工迁移（规划 §8）也是去掉的。</summary>
	public IReadOnlyList<ulong> OrphanTextures { get; init; } = [];

	/// <summary>目标套身上要隐藏的原版部件（含原因）。</summary>
	public IReadOnlyList<KitHideEntry> Hide { get; init; } = [];

	/// <summary>签名：<c>all</c> / <c>none</c>（16 位小写 hex）与半径。</summary>
	public IReadOnlyList<string> All { get; init; } = [];
	public IReadOnlyList<string> None { get; init; } = [];
	public double Radius { get; init; } = KitConversionPlanner.DefaultRadius;

	/// <summary>仍然全局生效（做不到按实例隔离）的 Mod 部件。</summary>
	public IReadOnlyList<ulong> GlobalUnits { get; init; } = [];

	/// <summary>仍然全局生效的原版贴图。</summary>
	public IReadOnlyList<ulong> GlobalTextures { get; init; } = [];

	/// <summary>D9：目标套没有独有部件，借用了同名记录共用的部件承载。预览要点名这些记录：穿它们的人这个部位也会显示 Mod 模型。</summary>
	public bool UsesSharedCarrier { get; init; }
	public IReadOnlyList<KitPollutionAffectedKit> AlsoShowsModKits { get; init; } = [];

	public IReadOnlyList<ulong> HideUnits => Hide.Select(entry => entry.Unit).ToArray();

	public static KitConversionPlan Failure(uint item, string name, string reason)
		=> new() { TargetItemId = item, TargetName = name, Succeeded = false, FailureReason = reason };
}

/// <summary>候选项：Mod 覆盖的 Unit 里至少有一个属于这套。不能转的带原因。</summary>
public sealed record KitConversionCandidate(uint ItemId, string Name, KitConversionPlan Plan)
{
	public bool CanConvert => Plan.Succeeded;
	public string Label => string.IsNullOrWhiteSpace(Name) || Name.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
		? $"未命名护甲（{ItemId}）"
		: $"{Name}（{ItemId}）";
}

/// <summary>
/// 转专属规划器（规划 §6.2）。纯函数：所有数据由调用方传入，没有任何 IO，可以离线单测。
/// <para>
/// 与规划相比的两处补充（用户 2026-11-14 确认）：
/// ① Mod 里的贴图（不论原版与否）如果<b>只被被去掉的材质</b>引用，一并去掉；
/// ② 空闲独有部件的挑选顺序：同 slot 同身形 → 同 slot → 任意，同级按 ID 排序。
/// </para>
/// </summary>
public static class KitConversionPlanner
{
	public const double DefaultRadius = 0.3;

	/// <param name="ruleName">写进规则文件的规则名，由调用方保证合法且唯一。</param>
	/// <param name="isVanilla">资源是否在游戏数据索引的 <c>assets</c> 表里；也用来给新 ID 查重。</param>
	/// <param name="unitsByMaterial">材质 → 绑定它的 Unit；材质缓存没好时为 <c>null</c>（有原版材质要处理时拒绝）。</param>
	public static KitConversionPlan Plan(
		uint targetItemId,
		string ruleName,
		KitConversionModContent mod,
		Func<AssetKey, bool> isVanilla,
		IReadOnlyList<KitPieceRef> pieces,
		IReadOnlyDictionary<uint, string> kitNames,
		IReadOnlyDictionary<ulong, IReadOnlyCollection<ulong>>? unitsByMaterial)
	{
		ArgumentNullException.ThrowIfNull(mod);
		ArgumentNullException.ThrowIfNull(isVanilla);
		ArgumentNullException.ThrowIfNull(pieces);
		ArgumentNullException.ThrowIfNull(kitNames);
		var name = kitNames.TryGetValue(targetItemId, out var n) ? n : string.Empty;
		KitConversionPlan Fail(string reason) => KitConversionPlan.Failure(targetItemId, name, reason);

		var valid = pieces.Where(piece => piece.Slot != KitPollutionAnalyzer.CapeSlot && piece.UnitFileId != 0).ToArray();
		var owners = new Dictionary<ulong, HashSet<uint>>();
		foreach (var piece in valid)
		{
			if (!owners.TryGetValue(piece.UnitFileId, out var set)) owners[piece.UnitFileId] = set = [];
			set.Add(piece.ItemId);
		}

		var targetRows = valid.Where(piece => piece.ItemId == targetItemId).ToArray();
		if (targetRows.Length == 0) return Fail("游戏数据里没有这套护甲的部件。");
		var targetUnits = targetRows.Select(piece => piece.UnitFileId).Distinct().OrderBy(id => id).ToArray();
		var targetUnitSet = targetUnits.ToHashSet();
		var placesByUnit = targetRows
			.GroupBy(piece => piece.UnitFileId)
			.ToDictionary(group => group.Key, group => group.Select(piece => (piece.Slot, piece.BodyType, piece.BodyVariant)).Distinct().ToArray());

		// ---- 承载池 ----------------------------------------------------------------------
		var uniqueUnits = targetUnits.Where(unit => owners[unit].Count == 1).ToArray();
		IReadOnlyList<uint> sameNameKits = [];
		ulong[] pool;
		var usesShared = false;
		if (uniqueUnits.Length > 0)
		{
			pool = uniqueUnits;
		}
		else
		{
			// D9：目标套没有独有部件。承载件只能从「只被同 display_name 的记录共用」的部件里选。
			if (!IsNamed(name))
				return Fail("这套护甲没有独有部件，而且游戏数据里没有可读的名字，找不到「同名记录」来共用承载件。");
			sameNameKits = kitNames
				.Where(pair => pair.Key != targetItemId && string.Equals(pair.Value, name, StringComparison.Ordinal)
					&& valid.Any(piece => piece.ItemId == pair.Key))
				.Select(pair => pair.Key)
				.OrderBy(id => id)
				.ToArray();
			if (sameNameKits.Count == 0)
				return Fail("这套护甲没有独有部件，也没有同名记录可以共用承载件。");
			var allowed = sameNameKits.Append(targetItemId).ToHashSet();
			pool = targetUnits.Where(unit => owners[unit].IsSubsetOf(allowed)).ToArray();
			if (pool.Length == 0)
				return Fail("这套护甲没有独有部件，它的部件都还被名字不同的护甲共用，没有只被同名记录共用的部件可以承载。");
			usesShared = true;
		}

		var poolSet = pool.ToHashSet();

		// ---- 分类 Mod 覆盖的原版 Unit ----------------------------------------------------
		var modUnitsVanilla = mod.Units.Where(unit => isVanilla(new AssetKey(KitPollutionAnalyzer.UnitTypeId, unit))).Distinct().OrderBy(id => id).ToArray();
		var inTarget = modUnitsVanilla.Where(targetUnitSet.Contains).ToArray();
		if (mod.Units.Count == 0)
			return Fail("这个 Mod 里没有部件（Unit），是纯材质或贴图 Mod：只能提示污染，不能转专属。");
		if (inTarget.Length == 0)
			return Fail($"这个 Mod 没有覆盖 {LabelOf(name, targetItemId)} 的任何部件。");

		var carriers = inTarget.Where(poolSet.Contains).ToArray();
		var needMove = inTarget.Where(unit => !poolSet.Contains(unit)).ToArray();
		var globalUnits = modUnitsVanilla.Where(unit => !targetUnitSet.Contains(unit)).ToArray();

		// ---- 搬迁（D5：不够就整个失败）---------------------------------------------------
		var free = pool.Where(unit => !carriers.Contains(unit)).OrderBy(id => id).ToList();
		var moves = new List<KitUnitMove>();
		foreach (var source in needMove)
		{
			if (free.Count == 0)
			{
				return Fail($"需要把 {needMove.Length} 个共用部件搬到 {LabelOf(name, targetItemId)} 的独有部件上，"
					+ $"但可用的空闲独有部件只有 {pool.Length - carriers.Length} 个，不够，已整体放弃（不做半套）。");
			}
			var sourcePlaces = placesByUnit[source];
			ulong? pick = null;
			(bool slot, bool body) quality = (false, false);
			foreach (var candidate in free)
			{
				var places = placesByUnit[candidate];
				var sameSlot = places.Any(target => sourcePlaces.Any(origin => origin.Slot == target.Slot));
				var sameBody = sameSlot && places.Any(target => sourcePlaces.Any(origin =>
					origin.Slot == target.Slot && origin.BodyType == target.BodyType && origin.BodyVariant == target.BodyVariant));
				// free 已按 ID 排序，所以同级里先遇到的就是 ID 最小的。
				if (pick is null || Rank(sameSlot, sameBody) > Rank(quality.slot, quality.body))
				{
					pick = candidate;
					quality = (sameSlot, sameBody);
				}
			}
			free.Remove(pick!.Value);
			moves.Add(new KitUnitMove(source, pick.Value, quality.slot, quality.body));
		}

		var movedTargets = moves.Select(move => move.To).ToHashSet();
		var finalCarriers = carriers.Concat(movedTargets).Distinct().OrderBy(id => id).ToArray();

		// ---- 材质 ------------------------------------------------------------------------
		var modMaterialsVanilla = mod.Materials.Where(material => isVanilla(new AssetKey(KitPollutionAnalyzer.MaterialTypeId, material)))
			.Distinct().OrderBy(id => id).ToArray();
		var referencedByUnits = mod.Units
			.SelectMany(unit => mod.UnitMaterials.TryGetValue(unit, out var list) ? list : [])
			.ToHashSet();
		var renamed = modMaterialsVanilla.Where(referencedByUnits.Contains).ToArray();
		var removed = modMaterialsVanilla.Where(material => !referencedByUnits.Contains(material)).ToArray();
		if (modMaterialsVanilla.Length > 0 && unitsByMaterial is null)
			return Fail("材质影响分析还没好（后台正在读取游戏数据），稍等一会儿再试。");

		var hide = new SortedDictionary<ulong, (KitHideReason Reason, SortedSet<ulong> Materials)>();
		foreach (var move in moves)
			hide[move.From] = (KitHideReason.MovedAway, []);
		foreach (var material in modMaterialsVanilla)
		{
			if (!unitsByMaterial!.TryGetValue(material, out var bound)) continue;
			foreach (var unit in bound)
			{
				if (!targetUnitSet.Contains(unit) || finalCarriers.Contains(unit)) continue;
				if (!hide.TryGetValue(unit, out var entry)) hide[unit] = entry = (KitHideReason.MaterialReplaced, []);
				entry.Materials.Add(material);
			}
		}

		if (hide.Count == 0)
			return Fail($"这个 Mod 在 {LabelOf(name, targetItemId)} 身上没有需要隐藏的原版部件，转专属没有意义。");
		if (hide.Count > KitInstanceRulesProtocol.MaximumHide)
			return Fail($"需要隐藏的原版部件有 {hide.Count} 个，超过规则协议的上限 {KitInstanceRulesProtocol.MaximumHide}。");

		// ---- 新 ID -----------------------------------------------------------------------
		var modUnitSet = mod.Units.ToHashSet();
		var materialSet = mod.Materials.ToHashSet();
		var textureSet = mod.Textures.ToHashSet();
		var usedNew = new HashSet<(ulong Type, ulong Id)>();
		ulong NewId(ulong type, ulong original, HashSet<ulong> existing)
		{
			for (var salt = 0; ; salt++)
			{
				var text = string.Create(CultureInfo.InvariantCulture, $"hd2modmanager/kit/{targetItemId}/{original:x16}");
				if (salt > 0) text += string.Create(CultureInfo.InvariantCulture, $"/{salt}");
				var candidate = StingrayHash.Murmur64A(text);
				if (candidate == 0 || existing.Contains(candidate)) continue;
				if (usedNew.Contains((type, candidate))) continue;
				if (isVanilla(new AssetKey(type, candidate))) continue;
				usedNew.Add((type, candidate));
				return candidate;
			}
		}

		var materialRemaps = new SortedDictionary<ulong, ulong>();
		foreach (var material in renamed)
			materialRemaps[material] = NewId(KitPollutionAnalyzer.MaterialTypeId, material, materialSet);

		// ---- 贴图 ------------------------------------------------------------------------
		var renamedSet = renamed.ToHashSet();
		var removedSet = removed.ToHashSet();
		var textureRemaps = new SortedDictionary<ulong, ulong>();
		var removedTextures = new List<ulong>();
		var orphanTextures = new List<ulong>();
		var globalTextures = new List<ulong>();
		foreach (var texture in mod.Textures.OrderBy(id => id))
		{
			var refs = mod.Materials.Where(material => mod.MaterialTextures.TryGetValue(material, out var list) && list.Contains(texture)).ToArray();
			var byRenamed = refs.Any(renamedSet.Contains);
			var byOtherKept = refs.Any(material => !renamedSet.Contains(material) && !removedSet.Contains(material));
			var byRemoved = refs.Any(removedSet.Contains);
			var vanillaTexture = isVanilla(new AssetKey(KitPollutionAnalyzer.TextureTypeId, texture));
			if (vanillaTexture && byRenamed)
			{
				textureRemaps[texture] = NewId(KitPollutionAnalyzer.TextureTypeId, texture, textureSet);
				if (byOtherKept) globalTextures.Add(texture);
			}
			else if (byRenamed || byOtherKept)
			{
				if (vanillaTexture) globalTextures.Add(texture);
			}
			else if (byRemoved)
			{
				removedTextures.Add(texture);
			}
			else if (!vanillaTexture && refs.Length == 0)
			{
				// Mod 自己新增的贴图、没有任何材质引用：留着没人用（原版材质不可能引用它）。
				removedTextures.Add(texture);
				orphanTextures.Add(texture);
			}
			else if (vanillaTexture)
			{
				globalTextures.Add(texture);
			}
		}

		// ---- 签名 ------------------------------------------------------------------------
		var all = new[] { Hex(finalCarriers[0]) };
		var none = new List<string>();
		IReadOnlyList<KitPollutionAffectedKit> alsoShows = [];
		if (usesShared)
		{
			// D9。「每个身形各取一个差异件」只在 S 里<b>真的带有承载件</b>的身形上才有意义：
			// 一个同名记录的穿戴者，只有簇里出现承载件，才可能误匹配 all；没有承载件的身形不可能匹配，不需要排除。
			// （实测：FS-05 三条记录的 3/3 身形部件集完全相同，且都没有左肩，按「每个身形」死板地找差异件会让转换必然失败。）
			var carrierSet = finalCarriers.ToHashSet();
			var diff = new SortedSet<ulong>();
			var shows = new List<KitPollutionAffectedKit>();
			foreach (var other in sameNameKits)
			{
				var rows = valid.Where(piece => piece.ItemId == other).ToArray();
				if (!rows.Any(piece => carrierSet.Contains(piece.UnitFileId))) continue;
				shows.Add(new KitPollutionAffectedKit(other, name, [], []));
				foreach (var body in rows.GroupBy(piece => (piece.BodyType, piece.BodyVariant)).OrderBy(group => group.Key))
				{
					if (!body.Any(piece => carrierSet.Contains(piece.UnitFileId))) continue;
					var first = body.Select(piece => piece.UnitFileId).Distinct().Where(unit => !targetUnitSet.Contains(unit)).OrderBy(id => id)
						.Select(unit => (ulong?)unit).FirstOrDefault();
					if (first is null)
					{
						return Fail($"同名记录 {LabelOf(name, other)} 在身形 {body.Key.BodyType}/{body.Key.BodyVariant} 下带有承载件，"
							+ $"但它的部件都在 {LabelOf(name, targetItemId)} 里，找不到用来把它和目标区分开的部件，无法排除它。");
					}
					diff.Add(first.Value);
				}
			}
			if (diff.Count > KitInstanceRulesProtocol.MaximumNone)
				return Fail($"区分同名记录需要 {diff.Count} 个排除部件，超过规则协议的上限 {KitInstanceRulesProtocol.MaximumNone}。");
			none.AddRange(diff.Select(Hex));
			alsoShows = shows;
		}

		return new KitConversionPlan
		{
			TargetItemId = targetItemId,
			TargetName = name,
			RuleName = ruleName,
			Succeeded = true,
			CarrierUnits = carriers,
			UnitMoves = moves,
			MaterialRemaps = materialRemaps,
			TextureRemaps = textureRemaps,
			RemovedMaterials = removed,
			RemovedTextures = removedTextures,
			OrphanTextures = orphanTextures,
			Hide = hide.Select(pair => new KitHideEntry(pair.Key, pair.Value.Reason, pair.Value.Materials.ToArray())).ToArray(),
			All = all,
			None = none,
			Radius = DefaultRadius,
			GlobalUnits = globalUnits,
			GlobalTextures = globalTextures,
			UsesSharedCarrier = usesShared,
			AlsoShowsModKits = alsoShows,
		};
	}

	/// <summary>候选套：Mod 覆盖的原版 Unit 里至少有一个属于它（披风不算）。每个候选都已规划好，不能转的带原因。</summary>
	public static IReadOnlyList<KitConversionCandidate> PlanCandidates(
		Func<uint, string> ruleNameFor,
		KitConversionModContent mod,
		Func<AssetKey, bool> isVanilla,
		IReadOnlyList<KitPieceRef> pieces,
		IReadOnlyDictionary<uint, string> kitNames,
		IReadOnlyDictionary<ulong, IReadOnlyCollection<ulong>>? unitsByMaterial)
	{
		ArgumentNullException.ThrowIfNull(ruleNameFor);
		ArgumentNullException.ThrowIfNull(mod);
		var modUnits = mod.Units.Where(unit => isVanilla(new AssetKey(KitPollutionAnalyzer.UnitTypeId, unit))).ToHashSet();
		var items = pieces
			.Where(piece => piece.Slot != KitPollutionAnalyzer.CapeSlot && piece.UnitFileId != 0 && modUnits.Contains(piece.UnitFileId))
			.Select(piece => piece.ItemId)
			.Distinct()
			.ToArray();
		return items
			.Select(item => new KitConversionCandidate(
				item,
				kitNames.TryGetValue(item, out var name) ? name : string.Empty,
				Plan(item, ruleNameFor(item), mod, isVanilla, pieces, kitNames, unitsByMaterial)))
			.OrderByDescending(candidate => candidate.CanConvert)
			.ThenByDescending(candidate => !candidate.Label.StartsWith("未命名", StringComparison.Ordinal))
			.ThenBy(candidate => candidate.Label, StringComparer.Ordinal)
			.ThenBy(candidate => candidate.ItemId)
			.ToArray();
	}

	/// <summary>预览（规划 §6.1 第 2 条）：逐条说明搬什么、藏什么、改什么、哪些做不到隔离。</summary>
	public static IReadOnlyList<string> DescribePreview(KitConversionPlan plan)
	{
		ArgumentNullException.ThrowIfNull(plan);
		var lines = new List<string>();
		var target = LabelOf(plan.TargetName, plan.TargetItemId);
		if (!plan.Succeeded)
		{
			lines.Add($"不能转成 {target} 专属：{plan.FailureReason}");
			return lines;
		}

		lines.Add($"目标：{target}。转换后，Mod 的内容只在穿着它的角色身上生效。");
		lines.Add($"识别方式：一个角色身上同时有 {string.Join("、", plan.All.Select(Short))}"
			+ (plan.None.Count > 0 ? $"、并且没有 {string.Join("、", plan.None.Select(Short))}" : string.Empty)
			+ $"（{plan.Radius.ToString("0.##", CultureInfo.InvariantCulture)} 米内），就认定他穿的是这套。");
		if (plan.UsesSharedCarrier)
		{
			lines.Add($"⚠ {target} 没有独有部件，只能借用同名记录共用的部件来承载。"
				+ $"穿 {string.Join("、", plan.AlsoShowsModKits.Select(kit => LabelOf(kit.Name, kit.ItemId)))} 的人，承载件所在的部位也会显示 Mod 模型。");
		}

		if (plan.CarrierUnits.Count > 0)
			lines.Add($"保留在原位的 Mod 部件 {plan.CarrierUnits.Count} 个：{string.Join("、", plan.CarrierUnits.Select(Short))}。");
		foreach (var move in plan.UnitMoves)
		{
			lines.Add($"搬迁：Mod 部件 {Short(move.From)} → {target} 的独有部件 {Short(move.To)}"
				+ (move.SameSlot ? (move.SameBody ? "（同部位同身形）" : "（同部位）") : "（部位不同，没有更合适的）")
				+ "；该独有部件原本的原版外观会被 Mod 内容取代。");
		}

		if (plan.Hide.Count > 0)
		{
			var moved = plan.Hide.Count(entry => entry.Reason == KitHideReason.MovedAway);
			var replaced = plan.Hide.Count - moved;
			lines.Add($"在 {target} 身上隐藏 {plan.Hide.Count} 个原版部件：{(moved > 0 ? $"{moved} 个因为被搬走" : string.Empty)}"
				+ (moved > 0 && replaced > 0 ? "，" : string.Empty)
				+ (replaced > 0 ? $"{replaced} 个因为 Mod 改了它们绑定的材质（视为要把它们藏掉或换掉）" : string.Empty) + "。");
		}

		if (plan.MaterialRemaps.Count > 0)
			lines.Add($"改 ID 的原版材质 {plan.MaterialRemaps.Count} 个（Mod 自己的部件改用新 ID），不再全局替换它们。");
		if (plan.TextureRemaps.Count > 0)
			lines.Add($"改 ID 的原版贴图 {plan.TextureRemaps.Count} 张。");
		if (plan.RemovedMaterials.Count > 0)
			lines.Add($"去掉 {plan.RemovedMaterials.Count} 个全局材质覆盖（Mod 自己的部件没用到它们）。");
		if (plan.RemovedTextures.Count > 0)
			lines.Add($"一并去掉 {plan.RemovedTextures.Count} 张贴图：{plan.RemovedTextures.Count - plan.OrphanTextures.Count} 张只被这些材质引用"
				+ (plan.OrphanTextures.Count > 0 ? $"，{plan.OrphanTextures.Count} 张没有任何材质引用" : string.Empty) + "。");

		if (plan.GlobalUnits.Count > 0)
			lines.Add($"仍全局生效：{plan.GlobalUnits.Count} 个 Mod 部件不属于 {target}，没法按实例隔离。");
		if (plan.GlobalTextures.Count > 0)
			lines.Add($"仍全局生效：{plan.GlobalTextures.Count} 张原版贴图没法按实例隔离。");
		return lines;
	}

	private static int Rank(bool sameSlot, bool sameBody) => sameBody ? 2 : sameSlot ? 1 : 0;
	private static bool IsNamed(string name) => !string.IsNullOrWhiteSpace(name) && !name.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
	private static string Hex(ulong id) => id.ToString("x16", CultureInfo.InvariantCulture);
	private static string Short(ulong id) => Hex(id)[..8] + "…";
	private static string Short(string hex) => hex[..8] + "…";

	private static string LabelOf(string name, uint item)
		=> IsNamed(name) ? $"{name}（{item}）" : $"未命名护甲（{item}）";
}
