using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using HD2ModCore.Decoration;

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

	/// <summary>换甲那一步的原始结果（没换甲时为 <c>null</c>）。</summary>
	public VpkRekeyOutcome? Rekey { get; init; }
}

/// <summary>
/// **编译基座**：把 <c>sectional/</c>（标准产物）按条件编译成 <c>Overwrite/</c>（唯一生效层）。
/// <para>
/// <b>顺序是固定的</b>（2026-11-08 按用户给的算法定）：
/// </para>
/// <list type="number">
/// <item>按 section 开关决定哪些组件要烘焙；</item>
/// <item>看目标：<b>自定义素体</b> ⇒ unit 键不变，头身合并直接改 <c>custom-model.json</c>
/// （写进 <c>Overwrite/</c>，<c>sectional/</c> 那份只作权威留档）；
/// <b>原版护甲</b> ⇒ 记下来，留到第 3 步；</item>
/// <item>替换 unit 键：来源 unit ↔ 目标护甲 unit（目标就是自身素体时不变）；</item>
/// <item>材质：装启用的材质 patch 层。</item>
/// </list>
/// <para>
/// <b>sectional 是只读的。</b>这里所有写入只落在 <c>Overwrite/</c>；落盘顺序上结构文件
/// （<c>custom-model.json</c> / section 表 / 开关表）必须在物化**之后**写——物化器会重开目录。
/// </para>
/// <para>
/// <b>不做增量、不做缓存</b>：每次从 <c>sectional/</c> 完整算一遍。
/// </para>
/// </summary>
public static class ModCompileService
{
	/// <summary>头的注册件所在槽位。</summary>
	private const int HeadSlot = 0;

	/// <summary>胸部槽位（头身合并后头件落在这里）。</summary>
	private const int ChestSlot = 2;

	/// <summary>合并后头件所在的层（第 1 层 = 身体之上的那一层）。</summary>
	private const int MergeLayer = 1;

	public static async ValueTask<ModCompileOutcome> CompileAsync(
		string modSelector,
		string modDirectory,
		VpkHostContext context,
		IReadOnlyList<string>? targetArchiveIds = null,
		IReadOnlyDictionary<string, IReadOnlyCollection<int>>? disabledSections = null,
		Action<string>? onStage = null,
		CancellationToken cancellationToken = default,
		// 头身合并：默认跟随 wardrobe.json；CLI 实验开关可显式指定。
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

		// ② 目标分类：自身素体（或没选目标）⇒ unit 键不变；否则是原版护甲。
		var isOwnModel = targets is null
			|| targets.All(target => target.StartsWith(RuntimeArmorRegistryService.CustomModelArchiveIdPrefix, StringComparison.OrdinalIgnoreCase));

		var rekeyed = false;
		string? targetName = null;
		VpkRekeyOutcome? rekeyOutcome = null;
		if (!isOwnModel)
		{
			// ②+③ 原版护甲：unit 键替换，头身合并在换甲里一起做（按目标胸部的实际层数）。
			var rekey = await VpkRekeyService.RunAsync(
				modSelector, modDirectory, context, targets!, disabled,
				onStage, cancellationToken,
				recordTarget: false,
				headMergeIntoBody: headMerge).ConfigureAwait(false);
			if (!rekey.Ok)
				return ModCompileOutcome.Failure(rekey.Error ?? "换甲失败。", notes);
			rekeyed = true;
			rekeyOutcome = rekey;
			targetName = rekey.TargetNames.FirstOrDefault();
			foreach (var warning in rekey.Warnings) notes.Add(warning);
		}
		else
		{
			// ②+③ 自身素体：unit 键不变；section 开关烘进 unit；头身合并改 custom-model.json。
			onStage?.Invoke("正在按当前条件编译生效层…");
			var identity = await CompileOwnModelAsync(
				modDirectory, disabled, headMerge, notes, cancellationToken).ConfigureAwait(false);
			if (identity is not null)
				return ModCompileOutcome.Failure(identity, notes);
			// 目标就是自身素体：报出来，别让调用方按"换甲"去解释。
			targetName = string.IsNullOrWhiteSpace(wardrobe.TargetArmorName)
				? "自定义素体"
				: wardrobe.TargetArmorName;
		}

		// ④ 材质层（物化器会重开 Overwrite，所以结构文件在它之后写）。
		var layer = OverwriteMaterializer.Materialize(modDirectory, OverwriteSelection.Default);
		if (!layer.Succeeded)
			return new ModCompileOutcome(false, layer.Error ?? "物化生效层失败。", rekeyed, targetName, notes)
			{
				Rekey = rekeyOutcome,
			};
		foreach (var warning in layer.Warnings) notes.Add(warning);

		// 头身合并（自身素体）：物化之后把改过的注册表写进 Overwrite。
		// 物化器重开目录，先写就会丢——这是 CLI 那条恒等路径踩过的同一个坑。
		var effectiveRegistry = Path.Combine(
			ModNodePayloadLayout.GetOverwriteDirectory(modDirectory), VpkCustomModelFile.FileName);
		if (isOwnModel && headMerge)
		{
			var merge = WriteMergedCustomModel(modDirectory, notes);
			if (merge is not null)
				notes.Add(merge);
		}
		else if (File.Exists(effectiveRegistry))
		{
			// 关掉合并（或换成原版护甲）时必须把派生那份删掉：
			// 留着它，生效层就永远读的是上一次合并过的布局——开关翻回去、游戏里却没变。
			VpkPayloadSwap.ClearReadOnlyForWrite(effectiveRegistry);
			File.Delete(effectiveRegistry);
			notes.Add("已清掉上一次编译留在生效层里的素体注册表。");
		}

		return new ModCompileOutcome(true, null, rekeyed, targetName, notes) { Rekey = rekeyOutcome };
	}

	/// <summary>
	/// 自身素体（unit 键不变）的编译：把 sectional 的 unit patch 装进 <c>Overwrite/</c>，
	/// 按 section 开关烘焙，再交回物化器统一装层。
	/// </summary>
	/// <returns>失败原因；<c>null</c> 表示成功。</returns>
	private static async ValueTask<string?> CompileOwnModelAsync(
		string modDirectory,
		IReadOnlyDictionary<string, IReadOnlyCollection<int>> disabled,
		bool headMerge,
		List<string> notes,
		CancellationToken cancellationToken)
	{
		var payload = ModNodePayloadLayout.TryGetExistingVpkPatchDirectory(modDirectory);
		if (payload is null)
			return "找不到 sectional 产物（没有载荷目录）：先转换一次再编译。";

		var archive = SectionalPatchManifestFile.ReadArchiveHex16(payload);
		var unitPatch = Path.Combine(payload, $"{archive}.patch_0");
		if (!File.Exists(unitPatch))
			return $"sectional 里没有基底 unit patch（{Path.GetFileName(unitPatch)}）。";

		var staging = Path.Combine(Path.GetTempPath(), "hd2-compile-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(staging);
		try
		{
			var staged = OverwritePatchInstaller.Install(staging, unitPatch).PatchPath;
			// **每次都在这一份上烘一遍**，不判"有没有要关的 section"：
			// 编译层的约定就是"从 sectional 重新算"，少一次判断就少一处会与状态不一致的地方
			// （没有关的 section 时烘焙器自己写 0 个 dword，不会动文件）。
			var map = await WardrobeSectionMapFile
				.ReadAsync(Path.Combine(payload, WardrobeSectionMapFile.FileName), cancellationToken)
				.ConfigureAwait(false);
			var bake = await WardrobeSectionBaker
				.BakeAsync(staged, map, disabled, cancellationToken).ConfigureAwait(false);
			foreach (var warning in bake.Warnings) notes.Add(warning);
			if (bake.SectionsDisabled != 0)
				notes.Add($"已把 {bake.SectionsDisabled} 个 section 烘成关闭状态。");

			var materialized = OverwriteMaterializer.Materialize(
				modDirectory, OverwriteSelection.Default, staged, ManagedOverwriteMarkers.VpkRekey);
			if (!materialized.Succeeded)
				return materialized.Error ?? "生效层没有装成。";
			foreach (var warning in materialized.Warnings) notes.Add(warning);

			// 结构文件按**装进去的**那份 patch 重算，写进生效层。
			var overwrite = ModNodePayloadLayout.GetOverwriteDirectory(modDirectory);
			var installed = Path.Combine(overwrite, Path.GetFileName(staged));
			var rebuilt = await WardrobeSectionMapFile.BuildAsync(installed, cancellationToken).ConfigureAwait(false);
			await WardrobeSectionMapFile
				.WriteAsync(Path.Combine(overwrite, WardrobeSectionMapFile.FileName), rebuilt.Document, cancellationToken)
				.ConfigureAwait(false);
			var switches = await SectionGroupFile.TryReadAsync(payload, cancellationToken).ConfigureAwait(false);
			if (switches is not null)
				await SectionGroupFile
					.WriteAsync(Path.Combine(overwrite, SectionGroupFile.FileName), switches, cancellationToken)
					.ConfigureAwait(false);
			return null;
		}
		finally
		{
			try { Directory.Delete(staging, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
		}
	}

	/// <summary>
	/// 头身合并：把 <c>sectional/custom-model.json</c> 里头件的**注册件位置**改到胸部第 1 层，
	/// 写一份新的到 <c>Overwrite/</c>。<c>sectional/</c> 那份永远是权威留档、一个字节都不动。
	/// <para>
	/// 改的是 <c>slot</c> / <c>pieceType</c>：unit id 不变（同一个 unit，只是挂到别的部位/层），
	/// 另外留一条 <c>movedFrom</c> 记录它原来在哪儿——换回来时靠它。
	/// </para>
	/// </summary>
	/// <returns>值得告诉用户的一句话；没做事时为 <c>null</c>。</returns>
	internal static string? WriteMergedCustomModel(string modDirectory, List<string> notes)
	{
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
		File.WriteAllText(destination, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
		return $"头身合并：body {body} 的头件已挂到胸部槽位第 {layer} 层（sectional 里的注册表未改动）。";
	}
}
