using System.Text.Json;
using HD2ModAdaptation.Analysis;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCli;

/// <summary>一次「把 VPK 模组的 7 个 Unit 换到某件护甲上」的结果。</summary>
internal sealed record VpkRekeyResult(
	string ModGuid,
	string ModDirectory,
	string SourcePatch,
	string OutputPatch,
	IReadOnlyList<string> TargetArchiveIds,
	int ReplacedUnits,
	int HiddenUnits,
	IReadOnlyList<string> Warnings)
{
	/// <summary>逐条映射（部位/层/身形 ← 来源 Unit → 目标 Unit），验收时直接看这里。</summary>
	public IReadOnlyList<string> Mappings { get; init; } = [];

	/// <summary>目标护甲的显示名（与 <see cref="TargetArchiveIds"/> 一一对应）。</summary>
	public IReadOnlyList<string> TargetNames { get; init; } = [];

	/// <summary>
	/// 这次没有做跨护甲搬运，而是把来源 patch 原样退回 <c>Overwrite/</c>（目标就是这个 Mod 自己的素体）。
	/// <para>
	/// 单独一个布尔而不是让调用方去读 <see cref="Mappings"/> 的文案：界面要据此换一句话
	/// （"已退回自定义素体"而不是"替换 7 个 Unit、隐藏 0 个网格"），而文案随时会改。
	/// </para>
	/// </summary>
	public bool RestoredFromSource { get; init; }
}

/// <summary>
/// 把 <c>vpk2patch</c> 转出来的 7 个 Unit <b>换到某件护甲自己的 Unit 键上</b>，
/// 并把该护甲其余槽位（肩、盔甲层……）用隐藏 Unit 顶掉。
/// <para>
/// <b>为什么产物写进 <c>Overwrite/</c> 而不是覆盖 <c>vpk2patch/</c>。</b>
/// <c>vpk2patch/</c> 里那份是<b>来源</b>：它还带着我们自己的 7 个 Unit id，是"再换一件护甲"
/// 的唯一输入。覆盖掉它，用户切一次目标护甲就再也切不回来了。
/// <c>Overwrite/</c> 的既有语义正好是"同名 patch 顶掉根上那份"，部署时以它为准——
/// 于是"来源不动、变体生效、切回就是删掉 Overwrite"三件事同时成立。
/// </para>
/// <para>
/// <b>为什么不做"逐网格重建"。</b>来源 Unit 本身就是合法的 HD2 Unit，
/// 跨护甲链路默认走 <c>DirectSourceUnitReuse</c>：把来源 payload 原样挂到目标键上。
/// 既快，也不会因为重采样把几何改坏。
/// </para>
/// </summary>
internal static class CliVpkRekey
{
	/// <summary>默认目标：RE-2310「荣誉卫队」。护甲与头盔分属两个归档，所以是两个 id。</summary>
	public static readonly IReadOnlyList<string> DefaultTargetArchiveIds = ["d923906f8b815987", "b4d15b145b3f7677"];

	/// <summary>来源 patch 的家：<c>vpk2patch/</c>。</summary>
	private const string SourceDirectoryName = ModNodePayloadLayout.VpkPatchDirectoryName;

	/// <param name="disabledSections">
	/// 要在<b>来源 Unit 键空间</b>里关掉的 section（来自 <c>wardrobe.json</c> 的勾选状态）。
	/// <para>
	/// 由这里烘焙、而不是让管理器事后再烘一遍：换甲会把 Unit 换到目标护甲的键上，
	/// 只有这里手里有"来源 → 目标"的完整映射。少一次映射传递，就少一处"两份表对不上"。
	/// </para>
	/// </param>
	public static async ValueTask<VpkRekeyResult> RunAsync(
		CliWorkspace workspace,
		string modSelector,
		IReadOnlyList<string> targetArchiveIds,
		string? outputRoot,
		IReadOnlyDictionary<string, IReadOnlyCollection<int>>? disabledSections = null,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		ArgumentException.ThrowIfNullOrWhiteSpace(modSelector);
		var targets = targetArchiveIds.Count == 0 ? DefaultTargetArchiveIds : targetArchiveIds;

		var state = await workspace.LoadLibraryAsync(cancellationToken).ConfigureAwait(false);
		var node = CliProjections.ResolveMod(state.Snapshot.Nodes, modSelector);
		if (string.IsNullOrWhiteSpace(workspace.LibraryRoot))
			throw new CliUserException("LibraryNotConfigured", "vpk rekey 需要 --library（或带 settings.json 的 --manager-root）。");
		var paths = new StoragePaths(workspace.ManagerRoot ?? Directory.GetCurrentDirectory(), workspace.LibraryRoot);

		// 目标分成两类，不能混着处理：
		//   · 游戏护甲归档 —— 有 setId、有配套头盔，可以按 setId 补齐；
		//   · 已注册的自定义素体 —— 不是游戏归档，没有 setId、没有配套头盔，unit 组来自
		//     armor-registry 工作区。把它丢给 ExpandTargetKitAsync 只会去装备表里查一个
		//     永远查不到的 id，然后静默退化成"就这一个归档"。
		var customModelKeys = targets
			.Select(target => RuntimeArmorRegistryService.TryReadCustomModelKey(target, out var key) ? key : null)
			.Where(key => key is not null)
			.Select(key => key!)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray();
		var archiveTargets = targets
			.Where(target => !RuntimeArmorRegistryService.TryReadCustomModelKey(target, out _))
			.ToArray();

		// 只给一个归档时，按 setId 把配套的另一半（护甲 ↔ 头盔）补上。
		// 为什么必须补：护甲与头盔在游戏里是两个归档、两个 kit，只换一半会让玩家的头还是原版的。
		if (archiveTargets.Length == 1 && customModelKeys.Length == 0 && !string.IsNullOrWhiteSpace(workspace.GameDataRoot))
			archiveTargets = (await ExpandTargetKitAsync(paths, archiveTargets[0], cancellationToken).ConfigureAwait(false)).ToArray();

		var modDirectory = Path.Combine(paths.ModsDirectory, node.RelativePath);
		if (!Directory.Exists(modDirectory))
			throw new CliUserException("ModDirectoryMissing", $"Mod 目录不存在：{modDirectory}");

		var sourceDirectory = Path.Combine(modDirectory, SourceDirectoryName);
		var sourcePatch = FindUnitPatch(sourceDirectory) ?? FindUnitPatch(modDirectory)
			?? throw new CliUserException("VpkPatchMissing",
				$"这个 Mod 里没有 vpk2patch 产出的 Unit patch。VPK 版 Mod 的 patch 应当放在 {SourceDirectoryName}/ 下。");
		var sectionMapPath = Path.Combine(Path.GetDirectoryName(sourcePatch)!, WardrobeSectionMapFile.FileName);
		if (!File.Exists(sectionMapPath))
			throw new CliUserException("VpkSectionsMissing",
				$"缺少 {WardrobeSectionMapFile.FileName}：它是「每个 Unit 有哪些 section」的唯一来源。请用新版 vpk2patch 重新转换。");
		var sectionMap = await WardrobeSectionMapFile.ReadAsync(sectionMapPath, cancellationToken).ConfigureAwait(false);

		var source = BuildSourceEntry(node, sectionMap);

		// 目标护甲的 Unit 元数据（partKind / bodyVariant / meshInfoIndex）只能从装备表读：
		// 这些字段决定"哪块几何落到哪个目标 Unit 上"，猜一个就会写错网格表位置。
		// 自定义素体的件表由护甲注册提供，形状与装备表条目一致，直接拼进同一份候选。
		var selected = new List<EquipmentUnitCatalogEntry>();
		if (archiveTargets.Length > 0)
		{
			var catalog = new EquipmentUnitCatalogService(paths);
			var entries = await catalog.GetEntriesAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
			var matched = entries
				.Where(entry => archiveTargets.Contains(entry.ArchiveId, StringComparer.OrdinalIgnoreCase))
				.ToArray();
			if (matched.Length == 0)
				throw new CliUserException("TargetArmorNotFound",
					$"游戏装备表里找不到这些归档：{string.Join("、", archiveTargets)}。");
			selected.AddRange(matched);
		}
		if (customModelKeys.Length > 0)
			selected.AddRange(await ResolveCustomModelTargetsAsync(paths, customModelKeys, cancellationToken).ConfigureAwait(false));
		if (selected.Count == 0)
			throw new CliUserException("TargetArmorNotFound", $"找不到任何目标：{string.Join("、", targets)}。");

		var destinationDirectory = Path.Combine(modDirectory, ModNodePayloadLayout.OverwriteDirectoryName);

		// 目标就是这个 Mod **自己**的素体：换甲等于"什么也不用换"。
		//
		// 为什么单独走一条路而不是让跨护甲链路算一遍：来源 Unit 与目标 Unit 是同一批键，
		// 计划里会变成"把 Unit 搬到它自己身上"，那不是一次换甲。用户点"转回自定义素体"
		// 想要的结果只是"回到刚转出来的样子"，而那份 patch 一直都在 vpk2patch/ 里——
		// 直接把它复制进 Overwrite 就是最忠实的答案，也最快（不读游戏数据、不重建几何）。
		if (IsIdentityTarget(source, selected))
		{
			var restored = await RestoreSourceAsync(
				sourcePatch, sourceDirectory, destinationDirectory, cancellationToken).ConfigureAwait(false);
			// 恒等路径上 Unit 键没变，掩码可以直接用。
			var identityBake = await BakeAsync(restored, destinationDirectory, disabledSections, cancellationToken)
				.ConfigureAwait(false);
			return new VpkRekeyResult(
				node.Id.Value.ToString("N"),
				modDirectory,
				sourcePatch,
				restored,
				selected.Select(entry => entry.ArchiveId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
				// 没有搬运任何几何：这里报 0 而不是映射条数，否则界面会说"替换 7 个 Unit"，
				// 而实际发生的是"把来源原样放回去"。
				0,
				0,
				[
					"目标就是这个 Mod 自己的素体：已把来源 patch 原样退回 Overwrite（Unit 键不变，开关继续有效）。",
					.. identityBake.Warnings,
				])
			{
				Mappings = ["直接退回来源（不做跨护甲搬运）"],
				TargetNames = selected.Select(entry => entry.DisplayName).Distinct(StringComparer.Ordinal).ToArray(),
				RestoredFromSource = true,
			};
		}

		// 计划只在真的要搬运几何时才需要：恒等目标那条路连游戏数据都不读。
		var (plan, mappingNotes) = BuildPlan(source, selected);
		if (!plan.CanContinue)
		{
			var detail = string.Join("；", plan.Issues.Select(issue => $"{issue.Code}: {issue.Message}"));
			throw new CliUserException("VpkRekeyPlanInvalid", $"换甲计划不成立：{detail}");
		}

		var gameData = workspace.GameDataRoot
			?? throw new CliUserException("GameDataRequired", "换甲需要 --game-data：目标 Unit 与隐藏 Unit 都从游戏本体读。");
		var ownsOutput = string.IsNullOrWhiteSpace(outputRoot);
		var output = Path.GetFullPath(ownsOutput
			? Path.Combine(Path.GetTempPath(), "hd2modmanager-vpk-rekey", Guid.NewGuid().ToString("N"))
			: outputRoot!);

		var result = await new CanonicalCrossArmorOrchestrator().ExecuteAsync(
			new CrossArmorTransferCandidateRequest(
				sourcePatch,
				gameData,
				output,
				plan,
				// 直接复用来源 Unit 的 payload：我们的 Unit 本来就是合法的 HD2 Unit，
				// 重建一遍只会引入采样误差，还慢一个数量级。
				DirectSourceUnitReuse: true,
				// 目标护甲里没被映射到的 Unit（肩、盔甲层…）一律隐藏。
				AutoHideUnmappedTargetUnits: true,
				// 隐藏 Unit 只构建一次、后面只改 key——这是"快速"的全部来源。
				UseSharedHiddenUnitTemplate: true),
			cancellationToken).ConfigureAwait(false);

		if (!result.IsSuccessful || string.IsNullOrWhiteSpace(result.OutputDirectory))
		{
			var detail = string.Join("；", result.Issues.Select(issue => $"{issue.Code}: {issue.Message}"));
			throw new CliUserException("VpkRekeyFailed", $"换甲失败：{detail}");
		}

		var produced = FindUnitPatch(result.OutputDirectory)
			?? throw new CliUserException("VpkRekeyNoOutput", "换甲没有产出 patch。");
		Directory.CreateDirectory(destinationDirectory);
		var destination = Path.Combine(destinationDirectory, Path.GetFileName(produced));

		// Overwrite 是"顶掉同名 patch"的一层：旧变体必须先清掉，否则 sidecar 会与新 TOC 不配。
		foreach (var stale in Directory.EnumerateFiles(destinationDirectory, Path.GetFileName(produced) + "*"))
			DeleteQuietly(stale);
		foreach (var suffix in new[] { "", ".gpu_resources", ".stream" })
		{
			var from = produced + suffix;
			if (File.Exists(from)) File.Copy(from, destination + suffix, overwrite: true);
		}

		// 换甲之后 Unit 键全变了，section 偏移也全变了：必须按**新 patch** 重算一份，
		// 否则组件树会指向旧偏移，烘焙会写到别的字段上。
		var rebuilt = await WardrobeSectionMapFile.BuildAsync(destination, cancellationToken).ConfigureAwait(false);
		await WardrobeSectionMapFile
			.WriteAsync(Path.Combine(destinationDirectory, WardrobeSectionMapFile.FileName), rebuilt.Document, cancellationToken)
			.ConfigureAwait(false);

		// 来源 Unit → 目标 Unit。开关表与烘焙都要用它，所以先算一次。
		var unitMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (var mapping in plan.Mappings)
		{
			if (mapping.Source is null)
				continue;
			var from = mapping.Source.UnitAssetKey.FileId.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
			var to = mapping.PhysicalTarget.UnitAssetKey.FileId.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
			unitMap[from] = to;
		}

		// 开关表跟着搬一份到 Overwrite：它的掩码是 (unit, section)，而换甲把 Unit 换到了目标护甲的键上。
		// **权威那份留在 vpk2patch/**（它描述的是"这个 VPK 有哪些部件开关"，与目标护甲无关）；
		// 这里写的是**这一份变体**的派生表，供部署后排查用。
		var sourceSwitches = await VpkComponentSwitchFile
			.TryReadAsync(Path.GetDirectoryName(sourcePatch)!, cancellationToken)
			.ConfigureAwait(false);
		if (sourceSwitches is not null)
		{
			await VpkComponentSwitchFile
				.WriteAsync(destinationDirectory, VpkComponentSwitchFile.Translate(sourceSwitches, unitMap), cancellationToken)
				.ConfigureAwait(false);
		}

		// 勾选状态也在来源键空间里，这里翻译完就地烘进新 patch。
		// 为什么不留给调用方事后再烘：那样它得自己再算一遍映射，两处映射一漂移就会"关错部件"。
		var translatedDisabled = TranslateDisabled(disabledSections, unitMap);
		var bake = await BakeAsync(destination, destinationDirectory, translatedDisabled, cancellationToken)
			.ConfigureAwait(false);

		// 临时输出里有 30 MB 的 gpu，切换几次就是几百 MB。产物已经拷进 Overwrite，这里清掉。
		if (ownsOutput)
		{
			try { if (Directory.Exists(output)) Directory.Delete(output, recursive: true); }
			catch (IOException) { }
			catch (UnauthorizedAccessException) { }
		}

		return new VpkRekeyResult(
			node.Id.Value.ToString("N"),
			modDirectory,
			sourcePatch,
			destination,
			selected.Select(entry => entry.ArchiveId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
			plan.Mappings.Count(mapping => mapping.WillReplace),
			result.MinifiedMeshCount,
			[.. mappingNotes, .. rebuilt.Skipped.Select(skip => $"{skip.FileId}：{skip.Reason}"), .. bake.Warnings])
		{
			Mappings = plan.Mappings
				.Select(mapping =>
					$"{mapping.Target.PartKind}/{mapping.Target.Layer}/{mapping.Target.BodyVariant}"
					+ $" ← 0x{mapping.Source!.UnitAssetKey.FileId:x16} → 0x{mapping.PhysicalTarget.UnitAssetKey.FileId:x16}")
				.ToArray(),
			TargetNames = selected.Select(entry => entry.DisplayName).Distinct(StringComparer.Ordinal).ToArray(),
		};
	}

	/// <summary>
	/// 把"来源键空间的禁用集合"翻译到目标键空间。映射不到目标 Unit 的条目直接丢掉
	/// （那个部件在新目标上没有落点，关它没有意义）。
	/// </summary>
	internal static IReadOnlyDictionary<string, IReadOnlyCollection<int>>? TranslateDisabled(
		IReadOnlyDictionary<string, IReadOnlyCollection<int>>? disabled,
		IReadOnlyDictionary<string, string> unitMap)
	{
		if (disabled is null || disabled.Count == 0)
			return null;
		var translated = new Dictionary<string, IReadOnlyCollection<int>>(StringComparer.OrdinalIgnoreCase);
		foreach (var (unit, sections) in disabled)
		{
			if (sections.Count == 0)
				continue;
			var key = unit.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? unit[2..] : unit;
			if (!unitMap.TryGetValue(key, out var mapped))
				continue;
			translated[mapped] = translated.TryGetValue(mapped, out var existing)
				? [.. existing.Concat(sections).Distinct().Order()]
				: [.. sections.Order()];
		}

		return translated.Count == 0 ? null : translated;
	}

	/// <summary>
	/// 把禁用集合烘进 <paramref name="patchPath"/>（用 <paramref name="directory"/> 里的 sections.json）。
	/// <para>
	/// 没有勾选就什么都不做——**不读表、不写盘**，保持"没改过 section"的产物与转换时逐字节一致。
	/// </para>
	/// </summary>
	private static async ValueTask<WardrobeSectionBakeResult> BakeAsync(
		string patchPath,
		string directory,
		IReadOnlyDictionary<string, IReadOnlyCollection<int>>? disabled,
		CancellationToken cancellationToken)
	{
		if (disabled is null || disabled.Count == 0)
			return new WardrobeSectionBakeResult(0, 0, 0, []);

		var sectionsPath = Path.Combine(directory, WardrobeSectionMapFile.FileName);
		if (!File.Exists(sectionsPath))
		{
			return new WardrobeSectionBakeResult(0, 0, 0,
				[$"缺少 {WardrobeSectionMapFile.FileName}：勾选状态没能烘进 patch。"]);
		}

		var map = await WardrobeSectionMapFile.ReadAsync(sectionsPath, cancellationToken).ConfigureAwait(false);
		return await WardrobeSectionBaker.BakeAsync(patchPath, map, disabled, cancellationToken).ConfigureAwait(false);
	}
	/// <summary>
	/// 目标是不是"这个 Mod 自己的素体"：两边 Unit 键集合完全相同。
	/// <para>
	/// 用键集合而不是"名字/键相等"来判：自定义素体的键由转换时生成，而用户在界面上选的是
	/// "哪个素体"——只有键集合能说明"换了等于没换"。也正因为如此，一个**复制出来**的素体
	/// （同一批 Unit）同样会被认成恒等目标，这是对的：几何确实没变。
	/// </para>
	/// </summary>
	internal static bool IsIdentityTarget(
		EquipmentUnitCatalogEntry source,
		IReadOnlyList<EquipmentUnitCatalogEntry> targets)
	{
		ArgumentNullException.ThrowIfNull(source);
		ArgumentNullException.ThrowIfNull(targets);
		if (targets.Count != 1)
			return false;
		var sourceIds = source.Parts.Select(part => part.UnitAssetKey).ToHashSet();
		var targetIds = targets[0].Parts.Select(part => part.UnitAssetKey).ToHashSet();
		return targetIds.Count != 0 && targetIds.SetEquals(sourceIds);
	}

	/// <summary>
	/// 把来源 patch 原样退回 `Overwrite/`：TOC、sidecar、开关表一起复制，section 表重算。
	/// <para>
	/// <b>为什么连 components.json 一起复制、不做翻译。</b>这条路上 Unit 键没变（根本没换甲），
	/// 掩码本来就对；翻译反而会把"没变"当成"变了"。重算 section 表是必须的：它带着本次构建的
	/// 偏移，而 Overwrite 里那份是给部署用的。
	/// </para>
	/// </summary>
	private static async ValueTask<string> RestoreSourceAsync(
		string sourcePatch,
		string sourceDirectory,
		string destinationDirectory,
		CancellationToken cancellationToken)
	{
		Directory.CreateDirectory(destinationDirectory);
		var destination = Path.Combine(destinationDirectory, Path.GetFileName(sourcePatch));
		foreach (var stale in Directory.EnumerateFiles(destinationDirectory, Path.GetFileName(sourcePatch) + "*"))
			DeleteQuietly(stale);

		foreach (var suffix in new[] { "", ".gpu_resources", ".stream" })
		{
			var from = sourcePatch + suffix;
			if (File.Exists(from))
				File.Copy(from, destination + suffix, overwrite: true);
		}

		var switches = await VpkComponentSwitchFile.TryReadAsync(sourceDirectory, cancellationToken).ConfigureAwait(false);
		if (switches is not null)
			await VpkComponentSwitchFile.WriteAsync(destinationDirectory, switches, cancellationToken).ConfigureAwait(false);

		var rebuilt = await WardrobeSectionMapFile.BuildAsync(destination, cancellationToken).ConfigureAwait(false);
		await WardrobeSectionMapFile
			.WriteAsync(Path.Combine(destinationDirectory, WardrobeSectionMapFile.FileName), rebuilt.Document, cancellationToken)
			.ConfigureAwait(false);
		return destination;
	}
	/// <summary>
	/// 把一个归档 id 扩成"这套装备的完整目标"：同 setId 的护甲 + 头盔。
	/// <para>
	/// 用户在下拉里选的是"哪套护甲"，而游戏里护甲与头盔是两个归档、两个 kit。
	/// 只换护甲会让头还是原版的——那种"身体换了、头没换"的结果比不换更难解释。
	/// </para>
	/// </summary>
	private static async ValueTask<IReadOnlyList<string>> ExpandTargetKitAsync(
		StoragePaths paths,
		string archiveId,
		CancellationToken cancellationToken)
	{
		var snapshot = await CliEquipmentFacts.LoadAsync(paths, cancellationToken).ConfigureAwait(false);
		var facts = CliEquipmentFacts.Project(snapshot, null, null);
		var self = facts.Kits.FirstOrDefault(kit =>
			kit.ArchiveId.Equals(archiveId, StringComparison.OrdinalIgnoreCase));
		if (self is null)
			return [archiveId];

		var siblings = facts.Kits
			.Where(kit => kit.SetId == self.SetId
				&& !kit.ArchiveId.Equals(archiveId, StringComparison.OrdinalIgnoreCase)
				&& (kit.Category.Equals("Armor", StringComparison.OrdinalIgnoreCase)
					|| kit.Category.Equals("Helmet", StringComparison.OrdinalIgnoreCase)))
			.Select(kit => kit.ArchiveId)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray();
		return [archiveId, .. siblings];
	}

	/// <summary>
	/// 把 <c>custom-model:&lt;key&gt;</c> 解析成护甲注册里的自定义素体件表。
	/// <para>
	/// 为什么要在这里抛而不是悄悄跳过：用户在下拉里选了"某个素体"，结果什么都没换，
	/// 比直接报"找不到这个素体"更难排查。三种失败各自说清：没注册过 / 没启用件 / 键为空。
	/// </para>
	/// </summary>
	private static async ValueTask<IReadOnlyList<EquipmentUnitCatalogEntry>> ResolveCustomModelTargetsAsync(
		StoragePaths paths,
		IReadOnlyList<string> modelKeys,
		CancellationToken cancellationToken)
	{
		var catalog = await CoreServices.CreateRuntimeArmorRegistryService(paths)
			.ReadCustomModelCatalogAsync(cancellationToken)
			.ConfigureAwait(false);
		var byKey = catalog
			.Where(entry => !string.IsNullOrWhiteSpace(entry.CustomModelKey))
			.ToDictionary(entry => entry.CustomModelKey!, StringComparer.OrdinalIgnoreCase);

		var result = new List<EquipmentUnitCatalogEntry>(modelKeys.Count);
		foreach (var key in modelKeys)
		{
			if (!byKey.TryGetValue(key, out var entry))
				throw new CliUserException("TargetCustomModelNotFound",
					$"护甲注册里找不到自定义素体“{key}”。请先在护甲映射页注册它并应用一次。");
			if (entry.Parts.Count == 0)
				throw new CliUserException("TargetCustomModelEmpty",
					$"自定义素体“{entry.DisplayName}”没有启用的组件，不能作为换甲目标。");
			result.Add(entry);
		}
		return result;
	}

	/// <summary>
	/// 从 <c>sections.json</c> 造一份"来源装备条目"。
	/// <para>
	/// 跨护甲链路要的元数据是 partKind / layer / bodyVariant / meshInfoIndex。
	/// 前三个 <c>sections.json</c> 里都有（它就是按 unit 的槽位、护甲层、身形建的）；
	/// <c>MeshInfoIndex</c> 在**来源**侧只用于排序，取 0 即可——真正决定落点的是目标侧的网格表。
	/// </para>
	/// </summary>
	private static EquipmentUnitCatalogEntry BuildSourceEntry(ModNode node, WardrobeSectionMapDocument map)
	{
		var parts = new List<EquipmentUnitPart>();
		foreach (var unit in map.Units)
		{
			var partKind = PartKindOf(unit.Slot);
			if (partKind == UnitMeshPartKind.Unknown)
				continue;
			var fileId = ulong.Parse(unit.FileId, System.Globalization.NumberStyles.HexNumber);
			parts.Add(new EquipmentUnitPart(
				new HD2ModCore.Domain.AssetKey(PatchUnitMeshReader.UnitTypeId, fileId),
				MeshInfoIndex: 0,
				MeshId: 0,
				partKind,
				LayerOf(unit.PieceType),
				BodyVariantOf(unit.BodyType),
				SemanticName: unit.Slot,
				Confidence: 100,
				SharedArchiveIds: []) { PieceType = unit.PieceType });
		}

		if (parts.Count == 0)
			throw new CliUserException("VpkSourceUnitsEmpty", "sections.json 里没有可用的 Unit。");

		var key = "vpk-mod:" + node.Id.Value.ToString("N");
		return new EquipmentUnitCatalogEntry(key, "CustomModel", node.Metadata.Name, parts) { CustomModelKey = key };
	}

	/// <summary>
	/// 手写映射：我们的每个 Unit 落到目标护甲**同部位**的 Unit 上。
	/// <para>
	/// 为什么不用自动规划器：自动规划器是按"语义族 + 层级 + 身形"去猜的，输入是装备表里的
	/// 真实归档；我们这里来源是"自己造的 7 个 Unit"，语义族无从比对。而映射关系其实是确定的——
	/// 同一部位就是同一部位，一个 <c>Any</c> 来源同时供 Slim 与 Stocky 两个目标（这正是
	/// 组件树里"链接"标记的由来）。写死比猜更可靠。
	/// </para>
	/// <para>
	/// <b>同部位有多层时外层优先：</b>Accessory → Armor → Undergarment，命中这件护甲该部位
	/// 存在的**最外层**，就用那层的全部 Unit（Slim / Stocky / Any 各一个），更内层交给隐藏。
	/// <para>
	/// 为什么是外层而不是内层（2026-11-01 实机对照得出，不是猜）：护甲件的 <c>mesh0</c> 是
	/// <c>lod=-1</c>、只写深度的剔除代理网格，形状就是整条前臂 / 小腿，而隐藏空壳会把它
	/// **原样保留**（见 <c>SameKeyCanonicalUnitRebuilder</c> 的"保留 target cutout"分支）。
	/// 于是挂在更内层的我们的几何会在那几圈被深度剔除，表现成"手套 / 丝袜整段不显示、
	/// 也不是品红"。把我们的几何换到外层、内层全部隐藏，实测手套与丝袜正常。
	/// </para>
	/// <para>
	/// 外层优先同时覆盖了原来的边界情况：有的护甲某个部位只有护甲层（AF-52「禁锢」的右臂
	/// 没有 <c>右臂－内衣</c>），那时护甲层就是最外层，本来就该由它承载。
	/// </para>
	/// </summary>
	internal static (CrossArmorTransferPlan Plan, IReadOnlyList<string> Notes) BuildPlan(
		EquipmentUnitCatalogEntry source,
		IReadOnlyList<EquipmentUnitCatalogEntry> targets)
	{
		var mappings = new List<CrossArmorTransferMapping>();
		var notes = new List<string>();
		foreach (var target in targets)
		{
			// 映射的单位是 **unit**，不是 (部位, 身形) 组合。注册素体里同一个 unit 常常同时
			// 挂在 body 0 和 body 1 上（"合并为 Any"之前的形状），按件遍历就会给同一个目标
			// unit 生成两条映射，计划校验直接以 DuplicateTargetMapping 拒掉整份计划。
			// A unit is the unit of work: two pieces sharing one unit must map once.
			var mappedUnits = new HashSet<AssetKey>();
			var fallbackNotedKinds = new HashSet<UnitMeshPartKind>();

			// **每个部位落在哪一层**：按 Undergarment → Armor → Accessory 的顺序找，
			// 命中第一个"这件护甲真有 Unit"的层。命中之后，同部位**更高层**的 Unit 全部隐藏。
			//
			// 为什么必须这么做：护甲层被隐藏的前提是同一个部位在更低的层上还有一个 Unit 顶着。
			// 有的护甲某个部位压根没有内衣层（实测 AF-52「禁锢」的右臂只有
			// 右臂－护甲－通用/健壮/纤细，没有 右臂－内衣），那时护甲层就是那个部位**唯一**
			// 的几何——按"护甲层一律隐藏"处理，等于既没写进我们的、又删掉了原版的，整条胳膊消失。
			// **外层优先**：Accessory → Armor → Undergarment，取这件护甲该部位存在的**最外层**。
			//
			// 为什么把"内层优先"改掉（这是实测结论，不是猜）：内层会被外层的**剔除代理网格**
			// 按深度挡掉。护甲件的 mesh0 是 lod=-1、只写深度的代理网格，形状就是整条前臂 /
			// 小腿——空壳它被原样保留（SameKeyCanonicalUnitRebuilder 的 "保留 target cutout"
			// 分支），于是挂在层 0 的我们的几何在那几圈被深度剔除，表现成"手套 / 丝袜整段不显示"。
			// 把我们的几何换到外层、内层全部隐藏，就绕开了那条路。
			var carrierLayerByKind = new Dictionary<UnitMeshPartKind, UnitMeshPartLayer>();
			foreach (var part in target.Parts)
			{
				if (part.Layer is UnitMeshPartLayer.Unknown or UnitMeshPartLayer.Culling or UnitMeshPartLayer.Static)
					continue;
				if (!carrierLayerByKind.TryGetValue(part.PartKind, out var current)
					|| LayerRank(part.Layer) > LayerRank(current))
				{
					carrierLayerByKind[part.PartKind] = part.Layer;
				}
			}

			foreach (var part in target.Parts)
			{
				var rank = LayerRank(part.Layer);
				// Culling / Static 是辅助 Unit（隐藏壳、静态网格），永远不承载我们的几何。
				if (rank == int.MaxValue)
					continue;

				var matched = source.Parts.FirstOrDefault(candidate => candidate.PartKind == part.PartKind);
				var isCarrier = carrierLayerByKind.TryGetValue(part.PartKind, out var carrier)
					&& carrier == part.Layer;

				if (matched is null || !isCarrier)
				{
					// 走隐藏（AutoHideUnmappedTargetUnits）。两条理由各说一次：
					// 没有来源 Unit；或者这一层不是承载层（同部位更低层已经承载了）。
					if (matched is null && isCarrier)
					{
						var note = $"{part.PartKind}/{part.Layer}/{part.BodyVariant} 没有对应来源 Unit，将走隐藏。";
						if (!notes.Contains(note)) notes.Add(note);
					}

					continue;
				}

				if (!mappedUnits.Add(part.UnitAssetKey))
					continue;

				// 承载层现在**总是**该部位存在的最外层（外层优先）。这条说明留着是因为
				// 它是排查依据：哪个部位由哪一层承载，直接决定进游戏会不会被剔除代理挡住。
				// 头盔不算——它天生就只有一层。
				if (part.PartKind != UnitMeshPartKind.Helmet && fallbackNotedKinds.Add(part.PartKind))
				{
					notes.Add($"{target.DisplayName} 的 {part.PartKind} 由 {part.Layer} 层承载来源 Unit"
						+ "（外层优先：内层会被外层护甲件的剔除代理按深度挡掉）。");
				}

				var mapping = new CrossArmorTransferMapping(
					new CrossArmorPhysicalTargetKey(part.UnitAssetKey),
					part,
					matched,
					WillReplace: true,
					Reason: isCarrier && part.Layer == UnitMeshPartLayer.Undergarment
						? $"同部位直用来源 Unit（{matched.PartKind}）"
						: $"该部位内衣层缺失，{part.Layer} 层直用来源 Unit（{matched.PartKind}）",
					UsedByArchiveIds: [target.ArchiveId],
					UsedByDisplayNames: [target.DisplayName],
					IsManual: false,
					IsSuppressed: false);
				mappings.Add(mapping);
			}
		}

		// 一个来源 Unit 一个落点都没有 = 它会整份消失。
		// 以前这里**一声不响**：AF-52 的右臂就是这么没的，直到玩家进游戏才发现。
		//
		// ⚠️ **必须跨目标判定。** 换一件护甲会同时给出护甲与头盔两个归档，而我们的 7 个 Unit
		// 分散在两边（Helmet 在头盔归档，其余在护甲归档）。按单个归档判，另一个归档的
		// 6 个 Unit 会被全部误报成"没有落点"。
		var mappedSourceIds = mappings
			.Select(mapping => mapping.Source!.UnitAssetKey.FileId)
			.ToHashSet();
		var targetNames = string.Join("、", targets.Select(target => target.DisplayName).Distinct(StringComparer.Ordinal));
		foreach (var sourcePart in source.Parts)
		{
			if (mappedSourceIds.Contains(sourcePart.UnitAssetKey.FileId))
				continue;

			var note = $"{targetNames} 上找不到 {sourcePart.PartKind} 的落点："
				+ $"来源 Unit 0x{sourcePart.UnitAssetKey.FileId:x16} 不会写出去，那一块会消失。";
			if (!notes.Contains(note)) notes.Add(note);
		}

		return (new CrossArmorTransferPlan(
			[source],
			source,
			targets,
			mappings,
			Impacts: [],
			Issues: []), notes);
	}

	/// <summary>
	/// 目标 Unit 的承载优先级：数字小的先承载。<see cref="int.MaxValue"/> = 不参与承载。
	/// <para>
	/// <b>为什么不是直接比枚举值。</b><c>UnitMeshPartLayer</c> 里 <c>Unknown</c> 排在
	/// <c>Undergarment</c> 之前（值 0），直接比大小会让"层没判出来"的 Unit 抢走承载权。
	/// <c>Culling</c> / <c>Static</c> 是隐藏壳与静态网格，属于辅助 Unit，写进我们的几何
	/// 只会得到"看不见的模型"，所以直接排除。
	/// </para>
	/// </summary>
	private static int LayerRank(UnitMeshPartLayer layer) => layer switch
	{
		UnitMeshPartLayer.Undergarment => 0,
		UnitMeshPartLayer.Armor => 1,
		UnitMeshPartLayer.Accessory => 2,
		// 层没判出来：能承载，但排在所有明确层之后（有别的选择就不用它）。
		UnitMeshPartLayer.Unknown => 3,
		_ => int.MaxValue,
	};

	// 部位名解析统一走 ArmorStructure（Hip / Pelvis / Head 这些旧写法都在它的别名表里），
	// 这里不再维护第二份 switch。
	private static UnitMeshPartKind PartKindOf(string slot)
		=> ArmorStructure.ToPartKind(ArmorStructure.TryParse(slot) ?? ArmorPart.Unknown);

	private static UnitMeshPartLayer LayerOf(string pieceType) => pieceType switch
	{
		"Armor" => UnitMeshPartLayer.Armor,
		"Accessory" => UnitMeshPartLayer.Accessory,
		_ => UnitMeshPartLayer.Undergarment,
	};

	private static UnitMeshBodyVariant BodyVariantOf(string bodyType)
		=> ArmorStructure.ParseBody(bodyType) is var body && body != UnitMeshBodyVariant.Other
			? body
			: UnitMeshBodyVariant.Any;

	private static string? FindUnitPatch(string directory)
	{
		if (!Directory.Exists(directory)) return null;
		return Directory.EnumerateFiles(directory, "*.patch_*", SearchOption.TopDirectoryOnly)
			.Where(path => !path.EndsWith(".gpu_resources", StringComparison.OrdinalIgnoreCase)
				&& !path.EndsWith(".stream", StringComparison.OrdinalIgnoreCase))
			.Where(IsUnitPatch)
			.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
			.FirstOrDefault();
	}

	/// <summary>
	/// 只认"含 Unit 条目"的 patch。材质包也是一个合法的 patch 文件，但换甲要用的是几何那份；
	/// 拿错会把材质当几何、报一个完全无关的错。
	/// </summary>
	private static bool IsUnitPatch(string path)
	{
		try
		{
			var bytes = File.ReadAllBytes(path);
			var entries = new PatchTocScanner().ScanEntries(bytes, Path.GetFileName(path));
			return entries.Any(entry => entry.AssetKey.TypeId == PatchUnitMeshReader.UnitTypeId);
		}
		catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
		{
			return false;
		}
	}

	private static void DeleteQuietly(string path)
	{
		try
		{
			var attributes = File.GetAttributes(path);
			if ((attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
			File.Delete(path);
		}
		catch (IOException) { }
		catch (UnauthorizedAccessException) { }
	}

	/// <summary>人类可读输出。</summary>
	public static string Format(VpkRekeyResult result)
	{
		var lines = new List<string>
		{
			$"换甲完成：{result.ModDirectory}",
			$"  来源 patch：{Path.GetFileName(result.SourcePatch)}（未改动）",
			$"  变体 patch：{result.OutputPatch}",
			$"  目标护甲：{string.Join("、", result.TargetArchiveIds)}",
			$"  替换 {result.ReplacedUnits} 个 Unit，隐藏 {result.HiddenUnits} 个网格",
			"  部署时以 Overwrite/ 下的这份为准；删掉它就回到 vpk2patch 里的自定义素体版。",
		};
		foreach (var warning in result.Warnings)
			lines.Add($"  ⚠️ {warning}");
		return string.Join(Environment.NewLine, lines);
	}

	public static object Project(VpkRekeyResult result) => new
	{
		ok = true,
		mod = result.ModGuid,
		modDirectory = result.ModDirectory,
		sourcePatch = result.SourcePatch,
		outputPatch = result.OutputPatch,
		targets = result.TargetArchiveIds,
		targetNames = result.TargetNames,
		replacedUnits = result.ReplacedUnits,
		hiddenMeshes = result.HiddenUnits,
		mappings = result.Mappings,
		restoredFromSource = result.RestoredFromSource,
		warnings = result.Warnings,
	};
}
