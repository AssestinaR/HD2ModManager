using System.Text.Json.Nodes;
using HD2ModCore.Decoration;
using HD2ModCore.Infrastructure.Sectional;

namespace HD2ModCore.Infrastructure;

/// <summary>一次编译的结果。</summary>
public sealed record ModCompileOutcome(
	bool Ok,
	string? Error,
	bool Rekeyed,
	string? TargetName,
	IReadOnlyList<string> Notes)
{
	public static ModCompileOutcome Failure(string error, IReadOnlyList<string>? notes = null)
		=> new(false, error, false, null, notes ?? Array.Empty<string>());

	/// <summary>
	/// 换甲那一步的原始结果——<b>现在是兼容投影</b>，由 <see cref="SectionalCompiler"/> 的结果算出来。
	/// <para>
	/// 界面已经习惯读它的 <c>HeadMerge</c> / <c>Warnings</c> / <c>ReplacedUnits</c>，所以在改编译器时
	/// 保留了这个形状，避免同时改两处。没换甲（自身素体）时它仍然有值：这样"头身合并到底做了没有"
	/// 在两条路上都能回答。
	/// </para>
	/// </summary>
	public VpkRekeyOutcome? Rekey { get; init; }

	/// <summary>专属切换（P4a）编出来的隐藏规则内容；普通编译为 <c>null</c>。调用方据此同步 workspace 记录。</summary>
	public KitInstance.KitExclusiveSeed? Exclusive { get; init; }

	/// <summary>专属多选（P5）：实际编进去的每个目标的规则种子；普通编译为空。</summary>
	public IReadOnlyList<KitInstance.KitExclusiveSeed> ExclusiveTargets { get; init; } = [];

	/// <summary>专属多选：被跳过的目标（编译失败）。</summary>
	public IReadOnlyList<SectionalTargetFailure> SkippedTargets { get; init; } = [];

	/// <summary>专属多选：写进 <c>Overwrite/</c> 的承载件份数、合并模型份数。</summary>
	public int CarrierCount { get; init; }

	public int MergedModelCount { get; init; }
}

/// <summary>
/// <b>编译层的入口</b>：把用户意图（目标 / section 开关 / 头身合并 / 材质开关）交给
/// <see cref="SectionalCompiler"/>，再投影成界面认识的形状。
/// <para>
/// 这一层只做三件事：读 <c>wardrobe.json</c> 补默认值、调编译器、投影结果。
/// <b>它不写 <c>wardrobe.json</c></b>——目标是用户意图，只能在"用户做了那个动作"时落盘。
/// </para>
/// <para>
/// 这里也保留"头身合并在自身素体形态下改注册表"的实现（<see cref="WriteMergedCustomModel"/>）：
/// 它改的是 <c>Overwrite/custom-model.json</c>，<c>sectional/</c> 那份一个字节都不动。
/// </para>
/// </summary>
public static class ModCompileService
{
	/// <summary>头的注册件所在槽位。</summary>
	internal const int HeadSlot = 0;

	/// <summary>胸部槽位（头身合并后头件落在这里）。</summary>
	internal const int ChestSlot = 2;

	/// <summary>合并后头件从这一层开始找空位。</summary>
	internal const int MergeLayer = 1;

	public static async ValueTask<ModCompileOutcome> CompileAsync(
		string modSelector,
		string modDirectory,
		VpkHostContext context,
		IReadOnlyList<string>? targetArchiveIds = null,
		IReadOnlyDictionary<string, IReadOnlyCollection<int>>? disabledSections = null,
		Action<string>? onStage = null,
		CancellationToken cancellationToken = default,
		// 头身合并：默认跟随 wardrobe.json；显式传值时以调用方为准（界面翻开关那一下）。
		bool? headMergeIntoBody = null,
		// 专属切换（P4a）：非空 = 只写 targetArchiveIds[0] 那一套里 item_id 对应记录的独有部件；null = 老做法。
		uint? exclusiveItemId = null,
		// 专属多选（P5）：非空时优先于 exclusiveItemId；每个目标各自规划、一起写进同一份 Overwrite。
		IReadOnlyList<uint>? exclusiveItemIds = null,
		// 有目标编译失败时：自动跳过（「继续并记住」）/ 问用户（回调）/ 都没有则整体失败、什么都不写。
		bool? autoSkipFailedTargets = null,
		SectionalTargetFailureHandler? onTargetFailures = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(modSelector);
		ArgumentException.ThrowIfNullOrWhiteSpace(modDirectory);
		if (!Directory.Exists(modDirectory))
			return ModCompileOutcome.Failure($"Mod 目录不存在：{modDirectory}");

		var wardrobe = await VpkModWardrobeService
			.ReadAsync(modDirectory, cancellationToken).ConfigureAwait(false);
		var target = targetArchiveIds is { Count: > 0 }
			? targetArchiveIds[0]
			: (string.IsNullOrWhiteSpace(wardrobe.TargetArmorArchiveId) ? null : wardrobe.TargetArmorArchiveId);
		var disabled = disabledSections ?? wardrobe.State.Disabled;
		var headMerge = headMergeIntoBody ?? wardrobe.HeadMergeIntoBody;
		// 没有显式指定目标（重构建、CLI 沿用「上次用的是哪件」）时，也沿用上次的专属状态：
		// 否则专属切换过的 Mod 一重构建就悄悄退回成「写全局空壳」的老做法。
		if (exclusiveItemId is null && exclusiveItemIds is null && targetArchiveIds is not { Count: > 0 } && wardrobe.Exclusive)
		{
			// 多目标记录整份沿用；旧的单目标文件读成只有一项的列表（见 VpkWardrobeFile.ExclusiveTargets）。
			var recorded = wardrobe.ExclusiveTargets.Select(item => item.ItemId).ToArray();
			if (recorded.Length > 1) exclusiveItemIds = recorded;
			else exclusiveItemId = wardrobe.TargetItemId;
		}

		var result = await SectionalCompiler
			.CompileAsync(
				modDirectory, context, target, disabled, headMerge, null, onStage, cancellationToken, exclusiveItemId,
				exclusiveItemIds, autoSkipFailedTargets ?? wardrobe.AutoSkipFailedTargets, onTargetFailures)
			.ConfigureAwait(false);
		if (!result.Ok)
			return ModCompileOutcome.Failure(result.Error ?? "编译失败。", result.Warnings);

		// 目标名：**目标是自己那具素体时一律叫「自定义素体」**。
		// 素体的名字（`水墨牛牛-…`）属于产物本身，不该跑到"目标"这个位置上——
		// 用户在看到"已换到 水墨牛牛（自定义素体）"时会以为换到了另一具素体。
		var targetName = result.TargetName
			?? (target is not null
				&& target.StartsWith(RuntimeArmorRegistryService.CustomModelArchiveIdPrefix, StringComparison.OrdinalIgnoreCase)
					? "自定义素体"
					: (string.IsNullOrWhiteSpace(wardrobe.TargetArmorName) ? "自定义素体" : wardrobe.TargetArmorName));
		var notes = new List<string>(result.Info);
		notes.AddRange(result.Warnings);

		return new ModCompileOutcome(
			true, null, result.Kind == SectionalCompileKind.Armor, targetName, notes)
		{
			Exclusive = result.Exclusive,
			ExclusiveTargets = result.ExclusiveTargets,
			SkippedTargets = result.SkippedTargets,
			CarrierCount = result.CarrierCount,
			MergedModelCount = result.MergedModelCount,
			Rekey = new VpkRekeyOutcome(
				true,
				null,
				result.ReplacedUnits,
				result.HiddenUnits,
				string.IsNullOrWhiteSpace(target) ? [] : [target!],
				[targetName])
			{
				// 自己就是目标：这一层没有搬运过几何。
				RestoredFromSource = result.Kind == SectionalCompileKind.OwnModel,
				Warnings = result.Warnings,
				HeadMerge = result.HeadMerge switch
				{
					SectionalHeadMergeOutcome.AppliedOwnModel => "applied",
					SectionalHeadMergeOutcome.AppliedIntoArmorTorso => "applied",
					SectionalHeadMergeOutcome.SkippedNoFreeTorsoLayer => "single-layer",
					SectionalHeadMergeOutcome.NoHelmetKit => "no-helmet-kit",
					_ => "off",
				},
			},
		};
	}

	/// <summary>
	/// 头身合并（自身素体形态）：把 <c>sectional/custom-model.json</c> 里头件的**注册件位置**
	/// 改到胸部第一个空层，写一份新的到 <c>Overwrite/</c>。<c>sectional/</c> 那份永远是权威留档。
	/// </summary>
	/// <returns>值得告诉用户的一句话；没做事时为 <c>null</c>。</returns>
	internal static string? WriteMergedCustomModel(string modDirectory, List<string> notes)
		=> WriteMergedCustomModel(modDirectory, notes, out _);

	/// <inheritdoc cref="WriteMergedCustomModel(string, List{string})"/>
	/// <param name="applied">这次真的写进生效层了吗（没写进去时界面不许说"已开启"）。</param>
	internal static string? WriteMergedCustomModel(string modDirectory, List<string> notes, out bool applied)
	{
		ArgumentNullException.ThrowIfNull(notes);
		applied = false;

		var payload = ModNodePayloadLayout.TryGetExistingVpkPatchDirectory(modDirectory);
		if (payload is null) return "头身合并没有执行：找不到 sectional 产物。";

		var source = Path.Combine(payload, VpkCustomModelFile.FileName);
		if (!File.Exists(source)) return "头身合并没有执行：sectional 里没有 custom-model.json。";

		var text = File.ReadAllText(source);
		if (JsonNode.Parse(text) is not JsonObject document
			|| document["pieces"] is not JsonArray pieces)
			return "头身合并没有执行：custom-model.json 结构不认识。";

		var head = pieces
			.OfType<JsonObject>()
			.FirstOrDefault(piece =>
				piece["slot"]?.GetValue<int>() == HeadSlot
				&& piece["pieceType"]?.GetValue<int>() == 0);
		if (head is null) return "头身合并没有执行：注册表里找不到头件（slot0 / layer0）。";

		var occupied = pieces
			.OfType<JsonObject>()
			.Where(piece => piece["slot"]?.GetValue<int>() == ChestSlot)
			.Select(piece => piece["pieceType"]?.GetValue<int>() ?? 0)
			.ToHashSet();
		var layer = Enumerable.Range(MergeLayer, 3 - MergeLayer + 1)
			.FirstOrDefault(candidate => !occupied.Contains(candidate), -1);
		if (layer < 0)
		{
			notes.Add("头身合并没有执行：胸部槽位已满（第 1~3 层都被占了）。");
			return null;
		}

		var body = head["body"]?.GetValue<int>() ?? 0;
		head["slot"] = ChestSlot;
		head["pieceType"] = layer;
		head["movedFrom"] = $"slot{HeadSlot}/layer0";

		var overwrite = ModNodePayloadLayout.GetOverwriteDirectory(modDirectory);
		Directory.CreateDirectory(overwrite);
		var destination = Path.Combine(overwrite, VpkCustomModelFile.FileName);
		// 库里的产物是只读的：不清属性写不进去，异常会被上层吞成"没合并"。
		VpkPayloadSwap.ClearReadOnlyForWrite(destination);
		File.WriteAllText(destination, document.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
		applied = true;
		return $"头身合并：body {body} 的头件已挂到胸部槽位第 {layer} 层（sectional 里的注册表未改动）。";
	}

}
