using System.IO;

namespace HD2ModCore.Infrastructure;

/// <summary>一次编译的结果。</summary>
/// <param name="Ok">是否成功。</param>
/// <param name="Error">失败原因。</param>
/// <param name="Rekeyed">这次有没有真的做换甲（有目标护甲才会做）。</param>
/// <param name="TargetName">换到了哪件护甲（显示名）；没换甲为 <c>null</c>。</param>
/// <param name="Notes">值得告诉用户的话。</param>
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
	/// 换甲那一步的原始结果（没换甲时为 <c>null</c>）。
	/// <para>
	/// 留给调用方显示细节用的（替换了几个 Unit、是不是"退回来源"）——编译基座本身只关心成败。
	/// </para>
	/// </summary>
	public VpkRekeyOutcome? Rekey { get; init; }
}

/// <summary>
/// **编译基座**：把一个 Mod 的 <c>sectional/</c>（标准产物）按当前条件编译成
/// <c>Overwrite/</c>（唯一生效层）。
/// <para>
/// <b>目录契约（物理层面只有这三层）。</b>
/// </para>
/// <list type="number">
/// <item>根：来源（<c>*.vpk</c> / <c>*.patch_N</c>）。只作权威事实，不参与生效。</item>
/// <item><c>sectional/</c>：patch 级权威事实。**只由转换链路（vpk2patch / legacy2sectional）产出**，
/// 形状固定（自定义素体 + 标准 7 槽位 + 材质 patch 组）。任何别的链路都不许碰它。</item>
/// <item><c>Overwrite/</c>：编译产物。**只有这里**会被"条件"改变。</item>
/// </list>
/// <para>
/// <b>条件是什么。</b>目标护甲、section 开关、材质开关——以及以后新增的任何功能。
/// 它们全部作为这里的入参，<b>不允许</b>各自写 <c>Overwrite/</c>。
/// </para>
/// <para>
/// <b>为什么只读配置、不写配置。</b>目标护甲与开关是**用户意图**，只能在"用户做了那个动作"时落盘
/// （<c>wardrobe.json</c> / <c>.hd2modmanager-mod.json</c>）。编译只读它们；让编译顺手写回去，
/// 会把"派生"变成"事实"，一次回退值就能永久改写用户的选择（2026-11-08 实测过）。
/// </para>
/// <para>
/// <b>这一版刻意不做的事。</b>不做增量、不做缓存、不做"只改一处就少算一点"的优化；
/// 每次编译都从 <c>sectional/</c> 完整算一遍。等链路稳定了再单独优化编译层。
/// </para>
/// </summary>
public static class ModCompileService
{
	/// <param name="modSelector">库节点标识（换甲要用它定位节点）。</param>
	/// <param name="modDirectory">这个 Mod 的目录。</param>
	/// <param name="context">宿主上下文（游戏 data 与库路径）。</param>
	/// <param name="targetArchiveIds">
	/// 目标护甲。**<c>null</c> = 读 <c>wardrobe.json</c>**；都为空 = 不换甲（编译成自定义素体形态）。
	/// </param>
	/// <param name="disabledSections">
	/// section 开关。<c>null</c> = 读 <c>wardrobe.json</c>。
	/// 调用方传"界面上当前这份"是刻意的：用户可能刚勾掉几个还没点应用。
	/// </param>
	public static async ValueTask<ModCompileOutcome> CompileAsync(
		string modSelector,
		string modDirectory,
		VpkHostContext context,
		IReadOnlyList<string>? targetArchiveIds = null,
		IReadOnlyDictionary<string, IReadOnlyCollection<int>>? disabledSections = null,
		Action<string>? onStage = null,
		CancellationToken cancellationToken = default,
		// 头身合并：**CLI 实验条件**，默认跟随 wardrobe.json。
		// 它不是常规编译条件（常规链路里没有它），见 docs/agent/sectional-compile-base-20261108.md。
		bool? headMergeIntoBody = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(modSelector);
		ArgumentException.ThrowIfNullOrWhiteSpace(modDirectory);
		if (!Directory.Exists(modDirectory))
			return ModCompileOutcome.Failure($"Mod 目录不存在：{modDirectory}");

		var notes = new List<string>();
		var wardrobe = await VpkModWardrobeService
			.ReadAsync(modDirectory, cancellationToken).ConfigureAwait(false);
		var targets = targetArchiveIds is { Count: > 0 }
			? targetArchiveIds
			: (string.IsNullOrWhiteSpace(wardrobe.TargetArmorArchiveId)
				? null
				: new[] { wardrobe.TargetArmorArchiveId });
		var disabled = disabledSections ?? wardrobe.State.Disabled;
		var headMerge = headMergeIntoBody ?? wardrobe.HeadMergeIntoBody;

		// 条件①：目标护甲（+ 同一次里烘进去的 section 开关）。
		var rekeyed = false;
		string? targetName = null;
		VpkRekeyOutcome? rekeyOutcome = null;
		if (targets is not null)
		{
			var rekey = await VpkRekeyService.RunAsync(
				modSelector, modDirectory, context, targets, disabled,
				onStage, cancellationToken,
				// 编译只读配置：目标护甲由"用户切换"那个动作落盘，不在这里写。
				recordTarget: false,
				headMergeIntoBody: headMerge).ConfigureAwait(false);
			if (!rekey.Ok)
				return ModCompileOutcome.Failure(rekey.Error ?? "换甲失败。", notes);
			rekeyed = true;
			rekeyOutcome = rekey;
			targetName = rekey.TargetNames.FirstOrDefault();
			foreach (var warning in rekey.Warnings) notes.Add(warning);
		}

		// 条件②：材质开关（启用集合在 Mod 自己的设置文件里，物化器读它）。
		// unit 层沿用上一步产出的换甲产物；没有换甲就取基底那份。
		var layer = OverwriteMaterializer.Materialize(modDirectory, OverwriteSelection.Default);
		if (!layer.Succeeded)
			return new ModCompileOutcome(false, layer.Error ?? "物化生效层失败。", rekeyed, targetName, notes) { Rekey = rekeyOutcome };
		foreach (var warning in layer.Warnings) notes.Add(warning);

		return new ModCompileOutcome(true, null, rekeyed, targetName, notes) { Rekey = rekeyOutcome };
	}
}
