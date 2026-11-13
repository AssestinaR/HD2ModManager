using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure.Sectional;

/// <summary>头身合并这一次**实际**会怎么做（不是开关状态）。</summary>
/// <remarks>
/// 开关打开不等于合并发生了：目标是自身素体、或目标躯干只有一层、或目标没有配套头盔时，
/// 都合并不了。界面必须照这里说话，否则用户翻一次开关、进游戏发现没变化，就再也信不过它。
/// </remarks>
internal enum SectionalHeadMergePlan
{
	/// <summary>关着。</summary>
	Off = 0,

	/// <summary>目标是自身素体：只改 <c>custom-model.json</c> 里头的挂载格。</summary>
	OwnModelRegistry = 1,

	/// <summary>目标是原版护甲：头改投目标躯干的空层，头盔套件全部隐藏。</summary>
	IntoArmorTorso = 2,

	/// <summary>目标躯干只有一层可承载，装不下「身体 + 头」两件几何。</summary>
	SkippedNoFreeTorsoLayer = 3,

	/// <summary>目标没有配套头盔：头无处安放（不是错误，只提示）。</summary>
	NoHelmetKit = 4,
}

/// <summary>来源侧的一个 unit：<c>sectional/sections.json</c> 里的 (fileId, 格子)。</summary>
/// <param name="FileId">16 位小写 hex，不带 <c>0x</c>（与 sections.json / wardrobe.json 一致）。</param>
/// <param name="Cell">注册表口径的「部位槽 × 层」。</param>
internal sealed record SectionalSourceUnit(string FileId, ArmorCell Cell);

/// <summary>目标侧的一个 unit：装备权威表里的一件。</summary>
/// <param name="Key">Unit 资源键（typeId + fileId）。</param>
/// <param name="Cell">注册表口径的「部位槽 × 层」。</param>
internal sealed record SectionalTargetUnit(AssetKey Key, ArmorCell Cell)
{
	/// <summary>写盘用的 16 位小写 hex 键（用于 sections.json 的 <c>SourceUnit</c> 对照与禁用集合翻译）。</summary>
	public string FileId => Key.FileId.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// 一件<b>被牵连</b>的护甲：不在目标套装里，但和目标套装共用了 unit，所以换甲后它也会显示我们的几何。
/// </summary>
/// <param name="Name">显示名（只用于提示）。</param>
/// <param name="Units">它的全部件（不含披风也可以，规划器自己会跳过披风）。</param>
internal sealed record SectionalCollateralKit(string Name, IReadOnlyList<SectionalTargetUnit> Units);

/// <summary>「来源 unit → 目标 unit」的一条映射。同一个来源可以有多条（每个身形一条）。</summary>
internal sealed record SectionalReplacement(SectionalSourceUnit Source, SectionalTargetUnit Target);

/// <summary>
/// 一次编译的**规划结果**。纯数据 + 纯函数，不碰磁盘，可以离线单测。
/// </summary>
/// <param name="Replacements">要写出去的改名副本（一个来源可以复制成多份）。</param>
/// <param name="Hidden">要顶成隐藏壳的目标 unit（不含披风）。</param>
/// <param name="HeadMerge">头身合并这一次实际会怎么做。</param>
/// <param name="Info">不是错误、但用户该知道的话（info 级）。</param>
/// <param name="Warnings">会让他看到少件/异样的话（warning 级）。</param>
internal sealed record SectionalCompilePlan(
	IReadOnlyList<SectionalReplacement> Replacements,
	IReadOnlyList<SectionalTargetUnit> Hidden,
	SectionalHeadMergePlan HeadMerge,
	IReadOnlyList<string> Info,
	IReadOnlyList<string> Warnings)
{
	/// <summary>替换了几份（= 写出去的改名副本数）。</summary>
	public int ReplacedCount => Replacements.Count;

	/// <summary>隐藏了几份。</summary>
	public int HiddenCount => Hidden.Count;
}

/// <summary>
/// 编译规划器：**只回答"哪个来源 unit 写到哪个目标 unit、哪些目标 unit 要隐藏"**，不做任何 IO。
/// <para>
/// <b>规则只有四条</b>（2026-11-08 用户逐条确认）：
/// </para>
/// <list type="number">
/// <item>披风（slot 1）**完全不碰**：既不替换、也不隐藏——它是独立 item，游戏实现方式与别处不同。</item>
/// <item>每个来源 unit 落到目标**同部位的最外层**；该层里每个身形各写一份（Stocky / Slim 各一个 unit）。</item>
/// <item>目标同部位其余层、以及我们没有来源的槽位（肩），全部隐藏。</item>
/// <item>头身合并：头不进头盔套件，改投目标躯干里一个"没被身体占用的层"；头盔套件全部隐藏。</item>
/// </list>
/// <para>
/// <b>为什么用 <see cref="ArmorCell"/> 而不是网格名反推。</b>躯干的内层与护甲层网格名都是
/// <c>Torso_Armor_*</c>，内容上分不出来；注册表给的 (slot, piece_type) 才是权威格子。
/// </para>
/// </summary>
internal static class SectionalCompilePlanner
{
	/// <summary>规划一次。</summary>
	/// <param name="source">来源 unit（来自 <c>sectional/sections.json</c>）。</param>
	/// <param name="targets">目标套件的全部 unit（护甲 + 头盔；**不含披风归档**）。</param>
	/// <param name="headMerge">是否要求头身合并。</param>
	/// <param name="kitsByUnit">
	/// 每个目标 unit 被哪几件装备（kit，按归档 id）引用。<c>null</c> = 不知道，按并集规划（老行为）。
	/// <para>
	/// <b>为什么需要它。</b>同一个 setId 下可能有<b>两件护甲</b>（实测 B-08「轻装枪手」与
	/// FS-38「根除者」同属一套，大部分 unit 共用）。把两件的件压成一个扁平集合后，"最外层"
	/// 取的是并集的最外层——胸部、胯部的 pc2 只有 FS-38 有，于是上衣和裙子只写进了 FS-38
	/// 独有的 unit，B-08 自己的胸部/胯部反而被顶成隐藏壳，进游戏就是"上衣、裙子整块没了"。
	/// 有了这张表，承载层只从<b>每件护甲都有的层</b>里选，见 <see cref="CarrierLayers"/>。
	/// </para>
	/// </param>
	internal static SectionalCompilePlan Build(
		IReadOnlyList<SectionalSourceUnit> source,
		IReadOnlyList<SectionalTargetUnit> targets,
		bool headMerge,
		IReadOnlyDictionary<AssetKey, IReadOnlyCollection<string>>? kitsByUnit = null,
		IReadOnlyList<SectionalCollateralKit>? collateral = null,
		IReadOnlySet<AssetKey>? protectedUnits = null)
	{
		ArgumentNullException.ThrowIfNull(source);
		ArgumentNullException.ThrowIfNull(targets);

		var info = new List<string>();
		var warnings = new List<string>();
		var replacements = new List<SectionalReplacement>();

		var helmetSlot = (int)(ArmorStructure.SlotOf(ArmorPart.Helmet) ?? 0u);
		var capeSlot = (int)(ArmorStructure.SlotOf(ArmorPart.Cape) ?? 1u);
		var torsoSlot = (int)(ArmorStructure.SlotOf(ArmorPart.Torso) ?? 2u);

		// ① 来源：丢掉没有权威格子的，同一部位只留最里那个（= 本体；配件层不参与替换）。
		foreach (var unit in source.Where(unit => !unit.Cell.IsKnown))
			warnings.Add($"来源 unit 0x{unit.FileId} 没有权威格子（部位/层未定位）：不会写出去。");

		var bySlot = source
			.Where(unit => unit.Cell.IsKnown)
			.GroupBy(unit => unit.Cell.Slot)
			.OrderBy(group => group.Key)
			.ToDictionary(
				group => group.Key,
				group => group
					.OrderBy(unit => unit.Cell.Layer)
					.ThenBy(unit => unit.FileId, StringComparer.Ordinal)
					.ToArray());

		foreach (var (slot, units) in bySlot.Where(pair => pair.Value.Length > 1))
			warnings.Add(
				$"部位「{ArmorStructure.Display(ArmorStructure.FromSlot((uint)slot))}」有 {units.Length} 个来源 unit："
				+ $"只写出最里那一个（0x{units[0].FileId}），其余不会写出去。");

		// ② 目标按部位分组。格子未知的目标不进分组 → 它们会自动落到隐藏集（保守优先）。
		var targetBySlot = targets
			.Where(target => target.Cell.IsKnown)
			.GroupBy(target => target.Cell.Slot)
			.ToDictionary(
				group => group.Key,
				group => group
					.OrderBy(target => target.Cell.Layer)
					.ThenBy(target => target.Key.FileId)
					.ToArray());

		var head = bySlot.TryGetValue(helmetSlot, out var headUnits) ? headUnits[0] : null;
		var torso = bySlot.TryGetValue(torsoSlot, out var torsoUnits) ? torsoUnits[0] : null;

		// ③ 头身合并先定层：身体占最外层、头占次外层。
		//    为什么身体在外层：护甲件的 mesh0 是 lod=-1 的深度剔除代理，挂在更内层的几何会被它挡掉。
		var merge = SectionalHeadMergePlan.Off;
		var bodyLayer = -1;
		var headLayer = -1;
		// 每个部位"可以承载"的层（从外到内）。只算一次：头身合并与逐部位落点必须看同一份答案。
		var carrierLayersBySlot = targetBySlot.ToDictionary(
			pair => pair.Key,
			pair => CarrierLayers(pair.Key, pair.Value, kitsByUnit, info, warnings));

		if (headMerge && head is not null && torso is not null)
		{
			var torsoLayers = carrierLayersBySlot.TryGetValue(torsoSlot, out var layersOfTorso)
				? layersOfTorso
				: [];
			if (torsoLayers.Length < 2)
			{
				merge = SectionalHeadMergePlan.SkippedNoFreeTorsoLayer;
				warnings.Add(
					$"目标躯干的可用层只有 {torsoLayers.Length} 层，装不下「身体 + 头」两件几何："
					+ "本次不做头身合并，头仍由头盔套件承载。");
			}
			else
			{
				merge = SectionalHeadMergePlan.IntoArmorTorso;
				bodyLayer = torsoLayers[0];
				headLayer = torsoLayers[1];
			}
		}

		// ④ 逐部位：披风不管，头单独，其余落到最外层（合并时躯干改落到 bodyLayer）。
		foreach (var (slot, units) in bySlot)
		{
			if (slot == capeSlot) continue;
			if (slot == helmetSlot) continue;

			var unit = units[0];
			if (!targetBySlot.TryGetValue(slot, out var slotTargets) || slotTargets.Length == 0)
			{
				warnings.Add(
					$"目标没有「{ArmorStructure.Display(ArmorStructure.FromSlot((uint)slot))}」这个部位："
					+ $"来源 unit 0x{unit.FileId} 不会写出去，那一块会消失。");
				continue;
			}

			var carrier = merge == SectionalHeadMergePlan.IntoArmorTorso && slot == torsoSlot
				? bodyLayer
				: carrierLayersBySlot[slot][0];

			foreach (var target in slotTargets.Where(target => target.Cell.Layer == carrier))
				replacements.Add(new SectionalReplacement(unit, target));
		}

		// ⑤ 头：合并时落到躯干空层，否则落到头盔套件的 slot 0。
		if (head is not null)
		{
			if (merge == SectionalHeadMergePlan.IntoArmorTorso && headLayer >= 0
				&& targetBySlot.TryGetValue(torsoSlot, out var torsoTargets))
			{
				var placed = 0;
				foreach (var target in torsoTargets.Where(target => target.Cell.Layer == headLayer))
				{
					replacements.Add(new SectionalReplacement(head, target));
					placed++;
				}

				info.Add(
					$"头身合并：头落到「{ArmorStructure.Display(ArmorPart.Torso)}」第 {headLayer} 层（{placed} 个 unit），"
					+ $"身体落到第 {bodyLayer} 层；头盔套件的件全部隐藏。");
			}
			else if (targetBySlot.TryGetValue(helmetSlot, out var helmetTargets) && helmetTargets.Length != 0)
			{
				// 头盔套件通常只有一个 unit（slot0/pc0/Any）；少数带第二个件，那一个走隐藏。
				var lowest = helmetTargets.Min(target => target.Cell.Layer);
				foreach (var target in helmetTargets.Where(target => target.Cell.Layer == lowest))
					replacements.Add(new SectionalReplacement(head, target));
			}
			else
			{
				merge = SectionalHeadMergePlan.NoHelmetKit;
				info.Add("这件护甲在游戏里没有配套头盔 item：头将无处安放，其余部件照常编译。");
			}
		}

		// ⑥ 隐藏集 = 目标全集 − 替换掉的 − 披风。
		var replaced = replacements.Select(replacement => replacement.Target.Key).ToHashSet();
		var hidden = targets
			.Where(target => !replaced.Contains(target.Key))
			.Where(target => !target.Cell.IsCape)
			.OrderBy(target => target.Key.FileId)
			.ToList();

		// ⑦ 被牵连的护甲：它们和目标套装共用 unit，换甲后同样显示我们的几何，
		//    但自己独有的件（装饰层、肩、腰包……）还是原版，叠在外面（实测 CE-74「破裂者」）。
		if (collateral is { Count: > 0 })
			HideCollateralExtras(collateral, protectedUnits, bySlot.Keys.ToHashSet(), replaced, hidden, targets, info);

		return new SectionalCompilePlan(replacements, hidden, merge, info, warnings);
	}

	/// <summary>
	/// 把被牵连护甲的"多余件"顶成隐藏壳。
	/// <para><b>三条不隐藏：</b></para>
	/// <list type="bullet">
	/// <item>披风与格子未知的件（与目标套装同一口径，保守优先）。</item>
	/// <item><paramref name="protectedUnits"/>：还被圈外护甲共用的件。隐藏它会让一件<b>完全没换模型</b>的
	/// 护甲凭空缺一块；实测 138 件护甲里有 58 件存在这种二级外溢，不能顺着链条一路隐藏下去。</item>
	/// <item>我们有来源、但这件护甲在该部位<b>一个替换件都没接到</b>：隐藏它的原件只会开个洞。</item>
	/// </list>
	/// </summary>
	private static void HideCollateralExtras(
		IReadOnlyList<SectionalCollateralKit> collateral,
		IReadOnlySet<AssetKey>? protectedUnits,
		IReadOnlySet<int> sourceSlots,
		IReadOnlySet<AssetKey> replaced,
		List<SectionalTargetUnit> hidden,
		IReadOnlyList<SectionalTargetUnit> targets,
		List<string> info)
	{
		var decided = targets.Select(target => target.Key).ToHashSet();
		var hiddenKeys = hidden.Select(target => target.Key).ToHashSet();
		foreach (var kit in collateral)
		{
			var coveredSlots = kit.Units
				.Where(unit => unit.Cell.IsKnown && replaced.Contains(unit.Key))
				.Select(unit => unit.Cell.Slot)
				.ToHashSet();
			var hiddenCount = 0;
			var keptShared = 0;
			var keptUncovered = 0;
			foreach (var unit in kit.Units)
			{
				if (decided.Contains(unit.Key) || hiddenKeys.Contains(unit.Key))
					continue;
				if (!unit.Cell.IsKnown || unit.Cell.IsCape)
					continue;
				if (protectedUnits?.Contains(unit.Key) == true)
				{
					keptShared++;
					continue;
				}
				if (sourceSlots.Contains(unit.Cell.Slot) && !coveredSlots.Contains(unit.Cell.Slot))
				{
					keptUncovered++;
					continue;
				}

				hidden.Add(unit);
				hiddenKeys.Add(unit.Key);
				hiddenCount++;
			}

			if (hiddenCount + keptShared + keptUncovered == 0)
				continue;
			var line = $"「{kit.Name}」与目标共用部件，换甲后也会显示这个模型：隐藏了它独有的 {hiddenCount} 个多余件";
			if (keptShared != 0)
				line += $"；{keptShared} 个还被别的护甲共用，保持原样";
			if (keptUncovered != 0)
				line += $"；{keptUncovered} 个所在部位没接到模型，保留原件以免开洞";
			info.Add(line + "。");
		}
	}

	/// <summary>
	/// 一个部位可以承载来源几何的层，<b>从外到内</b>排好（结果至少有一层）。
	/// <para>
	/// 只有一件装备用到这个部位（或不知道归属）时，就是这个部位全部的层。
	/// 多件装备共用这个部位时，只取<b>每一件都有的层</b>：落在那里的几何每件护甲都画得出来，
	/// 其余层（某一件独有的装饰层）顶成隐藏壳。各件没有共同层时退回并集，并明确告警——
	/// 那种情况下总有一件会缺这块，不能悄悄发生。
	/// </para>
	/// </summary>
	private static int[] CarrierLayers(
		int slot,
		IReadOnlyList<SectionalTargetUnit> slotTargets,
		IReadOnlyDictionary<AssetKey, IReadOnlyCollection<string>>? kitsByUnit,
		List<string> info,
		List<string> warnings)
	{
		var all = slotTargets.Select(target => target.Cell.Layer).Distinct().OrderByDescending(layer => layer).ToArray();
		if (kitsByUnit is null)
			return all;

		var layersByKit = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
		foreach (var target in slotTargets)
		{
			if (!kitsByUnit.TryGetValue(target.Key, out var kits))
				continue;
			foreach (var kit in kits)
			{
				if (!layersByKit.TryGetValue(kit, out var layers))
					layersByKit[kit] = layers = [];
				layers.Add(target.Cell.Layer);
			}
		}

		if (layersByKit.Count <= 1)
			return all;

		var common = all.Where(layer => layersByKit.Values.All(layers => layers.Contains(layer))).ToArray();
		var part = ArmorStructure.Display(ArmorStructure.FromSlot((uint)slot));
		if (common.Length == 0)
		{
			warnings.Add(
				$"部位「{part}」被同套 {layersByKit.Count} 件装备使用，但它们没有共同的层："
				+ $"按最外层（第 {all[0]} 层）承载，其中有的护甲会缺这一块。");
			return all;
		}

		if (common[0] != all[0])
			info.Add(
				$"部位「{part}」被同套 {layersByKit.Count} 件装备共用：改用每件都有的第 {common[0]} 层承载"
				+ $"（第 {string.Join("、", all.Except(common))} 层只有其中一部分装备有，顶成隐藏）。");
		return common;
	}
}
