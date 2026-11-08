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
	internal static SectionalCompilePlan Build(
		IReadOnlyList<SectionalSourceUnit> source,
		IReadOnlyList<SectionalTargetUnit> targets,
		bool headMerge)
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
		if (headMerge && head is not null && torso is not null)
		{
			var torsoLayers = targetBySlot.TryGetValue(torsoSlot, out var torsoTargets)
				? torsoTargets.Select(target => target.Cell.Layer).Distinct().OrderByDescending(layer => layer).ToArray()
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
				: slotTargets.Max(target => target.Cell.Layer);

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
			.ToArray();

		return new SectionalCompilePlan(replacements, hidden, merge, info, warnings);
	}
}
