using System.Globalization;
using HD2ModCore.Decoration;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure.Sectional;

/// <summary>这一次编译出来的是哪种形态。</summary>
public enum SectionalCompileKind
{
	/// <summary>目标是自己：unit 键不变，只按 section 开关重算，并（可选）把合并写进注册表。</summary>
	OwnModel = 0,

	/// <summary>目标是游戏护甲：来源 unit 改名落到目标 unit 键上，其余目标件顶成隐藏壳。</summary>
	Armor = 1,
}

/// <summary>头身合并这一次**实际**的结果（不是开关状态）。</summary>
public enum SectionalHeadMergeOutcome
{
	Off = 0,
	AppliedOwnModel = 1,
	AppliedIntoArmorTorso = 2,
	SkippedNoFreeTorsoLayer = 3,
	NoHelmetKit = 4,
}

/// <summary>一次编译的结果。失败是**返回值**，不是异常。</summary>
/// <param name="Ok">成功与否。</param>
/// <param name="Error">失败原因（成功时为 <c>null</c>）。</param>
/// <param name="Kind">自身素体形态还是换甲形态。</param>
/// <param name="TargetName">目标显示名（自身素体时为空，由调用方补）。</param>
/// <param name="ReplacedUnits">写出去的改名副本数。</param>
/// <param name="HiddenUnits">写成隐藏壳的目标 unit 数。</param>
/// <param name="HeadMerge">头身合并实际结果。</param>
/// <param name="HiddenTriangles">隐藏壳的三角形总数（自身素体时为 0；有隐藏壳时必须为 1）。</param>
/// <param name="Info">不是错误、但用户该知道的话。</param>
/// <param name="Warnings">会让他看到少件/异样的话。</param>
public sealed record SectionalCompileResult(
	bool Ok,
	string? Error,
	SectionalCompileKind Kind,
	string? TargetName,
	int ReplacedUnits,
	int HiddenUnits,
	SectionalHeadMergeOutcome HeadMerge,
	int HiddenTriangles,
	IReadOnlyList<string> Info,
	IReadOnlyList<string> Warnings)
{
	/// <summary>失败。</summary>
	public static SectionalCompileResult Failure(string error, SectionalCompileKind kind = SectionalCompileKind.OwnModel)
		=> new(false, error, kind, null, 0, 0, SectionalHeadMergeOutcome.Off, 0, [], []);

	/// <summary>专属切换（P4a）这一次编出来的隐藏规则内容；普通编译为 <c>null</c>。</summary>
	public KitInstance.KitExclusiveSeed? Exclusive { get; init; }
}

/// <summary>
/// <b>sectional 编译器</b>：<c>sectional/</c> + 四个条件 ⇒ <c>Overwrite/</c>。
/// <para>
/// <b>它是纯函数。</b>每次调用都从 <c>sectional/</c> 完整重算，不增量、不缓存、不看盘上现有产物。
/// 这样"开关翻了、产物没变"这类不一致从结构上就不可能发生。
/// </para>
/// <para>
/// <b>它只写 <c>Overwrite/</c>。</b><c>sectional/</c> 是权威事实，一个字节都不动。
/// </para>
/// <para>
/// <b>它不读游戏 data。</b>目标套件的形状来自索引库（装备权威表），来源 unit 来自
/// <c>sectional/</c>，隐藏壳由来源 unit 现造。换甲只是"改 unit 键"，不需要读目标 unit 本身。
/// </para>
/// </summary>
public static partial class SectionalCompiler
{
	/// <summary>编译一次。</summary>
	/// <param name="modDirectory">这个 Mod 的目录。</param>
	/// <param name="context">宿主上下文（只用到 <see cref="VpkHostContext.Paths"/>：索引库位置）。</param>
	/// <param name="targetArchiveId">
	/// 目标。<c>null</c>/空 = 自己；<c>custom-model:&lt;key&gt;</c> = 必须是本 Mod 自己的素体；
	/// 其余 = 游戏护甲归档 id。
	/// </param>
	/// <param name="disabledSections">要关掉的 section（**来源** unit 键空间）。</param>
	/// <param name="headMergeIntoBody">要不要做头身合并。</param>
	/// <param name="enabledMaterialPatches">
	/// 要装进生效层的材质 patch 文件名。<c>null</c> = 读 Mod 设置文件；那里也没有 = 全部。
	/// </param>
	/// <param name="exclusiveItemId">
	/// 专属切换（P4a）：非空时按「只写目标记录自己独有的 unit + 隐藏规则」编译，<paramref name="targetArchiveId"/> 必须是游戏护甲。
	/// <c>null</c> = 老做法，行为与以前完全一致。
	/// </param>
	public static async ValueTask<SectionalCompileResult> CompileAsync(
		string modDirectory,
		VpkHostContext context,
		string? targetArchiveId,
		IReadOnlyDictionary<string, IReadOnlyCollection<int>>? disabledSections,
		bool headMergeIntoBody,
		IReadOnlyList<string>? enabledMaterialPatches,
		Action<string>? onStage = null,
		CancellationToken cancellationToken = default,
		uint? exclusiveItemId = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(modDirectory);
		ArgumentNullException.ThrowIfNull(context);

		if (!Directory.Exists(modDirectory))
			return SectionalCompileResult.Failure($"Mod 目录不存在：{modDirectory}");

		// ① 读条件：target 决定形态；自建素体必须先确认"这就是我自己"。
		var ownModelKey = VpkCustomModelFile.TryReadKey(modDirectory);
		var armorArchiveId = ResolveArmorArchive(targetArchiveId, ownModelKey, out var ownModel, out var targetError);
		if (targetError is not null)
			return SectionalCompileResult.Failure(targetError);

		// ② 读 sectional：载荷目录、unit patch、section 表。
		var payload = ModNodePayloadLayout.TryGetExistingVpkPatchDirectory(modDirectory);
		if (payload is null)
			return SectionalCompileResult.Failure("找不到 sectional 产物（没有载荷目录）：先转换一次再编译。");

		var sourcePatch = await ResolveUnitPatchAsync(payload, cancellationToken).ConfigureAwait(false);
		if (sourcePatch is null)
			return SectionalCompileResult.Failure("sectional 里没有基底 unit patch：先转换一次再编译。");

		var mapPath = Path.Combine(payload, WardrobeSectionMapFile.FileName);
		if (!File.Exists(mapPath))
			return SectionalCompileResult.Failure($"sectional 里没有 {WardrobeSectionMapFile.FileName}：它是组件开关的唯一来源，请重新转换。");

		var sourceMap = await WardrobeSectionMapFile.ReadAsync(mapPath, cancellationToken).ConfigureAwait(false);
		var selection = new OverwriteSelection(enabledMaterialPatches);

		if (ownModel)
		{
			onStage?.Invoke("正在按当前条件编译生效层…");
			return await CompileOwnModelAsync(
				modDirectory, payload, sourcePatch, sourceMap, disabledSections, headMergeIntoBody, selection, cancellationToken)
				.ConfigureAwait(false);
		}

		if (exclusiveItemId is { } exclusiveItem)
		{
			onStage?.Invoke("正在把来源 unit 落到目标护甲独有的部件上…");
			return await CompileExclusiveArmorAsync(
				modDirectory, context, payload, sourcePatch, sourceMap, exclusiveItem, disabledSections, headMergeIntoBody, selection, cancellationToken)
				.ConfigureAwait(false);
		}

		onStage?.Invoke($"正在把来源 unit 换到目标护甲的键上…");
		return await CompileArmorAsync(
			modDirectory, context, payload, sourcePatch, sourceMap, armorArchiveId!, disabledSections, headMergeIntoBody, selection, cancellationToken)
			.ConfigureAwait(false);
	}

	// ── 自身素体：键不变，只重算 section 与（可选的）注册表 ─────────────────────

	private static async ValueTask<SectionalCompileResult> CompileOwnModelAsync(
		string modDirectory,
		string payload,
		string sourcePatch,
		WardrobeSectionMapDocument sourceMap,
		IReadOnlyDictionary<string, IReadOnlyCollection<int>>? disabledSections,
		bool headMergeIntoBody,
		OverwriteSelection selection,
		CancellationToken cancellationToken)
	{
		var info = new List<string>();
		var warnings = new List<string>();
		var staging = CreateStagingDirectory();

		try
		{
			// **不重写 patch**：把来源 patch 原样装进临时目录，"自身素体 + 没关 section"的产物
			// 逐字节等于 sectional 里那份——这是一条能自动断言的强不变式，也保证我们没在暗改东西。
			var staged = OverwritePatchInstaller.Install(staging, sourcePatch).PatchPath;

			// 顺序必须是"先建表（记原值）再烘焙"：反过来会把隐藏后的 0 记成原值，以后永远打不开。
			var built = await WardrobeSectionMapFile.BuildAsync(staged, cancellationToken).ConfigureAwait(false);
			// 身份从来源表继承：patch 可以重装，unit 自己的 customization 依然是空的。
			var documented = WardrobeSectionMapFile.ApplyUnitIdentities(
				built.Document, SourceUnitIdentities(sourceMap, plan: null));
			var baked = await WardrobeSectionBaker
				.BakeAsync(staged, documented, disabledSections ?? EmptySections, cancellationToken)
				.ConfigureAwait(false);
			warnings.AddRange(baked.Warnings);
			if (baked.SectionsDisabled != 0)
				info.Add($"已把 {baked.SectionsDisabled} 个 section 烘成关闭状态。");

			var materialized = OverwriteMaterializer.Materialize(
				modDirectory, selection, staged, ManagedOverwriteMarkers.VpkRekey);
			if (!materialized.Succeeded)
				return SectionalCompileResult.Failure(materialized.Error ?? "物化生效层失败。");
			warnings.AddRange(materialized.Warnings);

			// 结构文件只能写在物化**之后**：物化器会重开目录。
			var components = await SectionGroupFile.TryReadAsync(payload, cancellationToken).ConfigureAwait(false);
			await WriteStructureAsync(modDirectory, documented, components, cancellationToken).ConfigureAwait(false);

			var merge = SectionalHeadMergeOutcome.Off;
			var effectiveRegistry = Path.Combine(
				ModNodePayloadLayout.GetOverwriteDirectory(modDirectory), VpkCustomModelFile.FileName);
			if (headMergeIntoBody)
			{
				var message = ModCompileService.WriteMergedCustomModel(modDirectory, info, out var applied);
				if (message is not null)
					(applied ? info : warnings).Add(message);
				merge = applied ? SectionalHeadMergeOutcome.AppliedOwnModel : SectionalHeadMergeOutcome.SkippedNoFreeTorsoLayer;
			}
			else if (File.Exists(effectiveRegistry))
			{
				// 关掉合并时必须删掉派生那份，否则生效层永远读上一次的合并布局。
				VpkPayloadSwap.ClearReadOnlyForWrite(effectiveRegistry);
				File.Delete(effectiveRegistry);
				info.Add("已清掉上一次编译留在生效层里的素体注册表。");
			}

			return new SectionalCompileResult(
				true, null, SectionalCompileKind.OwnModel, null, 0, 0, merge, 0, info, warnings);
		}
		catch (Exception exception) when (IsExpected(exception))
		{
			return SectionalCompileResult.Failure("编译失败：" + exception.Message);
		}
		finally
		{
			DeleteQuietly(staging);
		}
	}

	// ── 游戏护甲：改名副本 + 隐藏壳 ─────────────────────────────────────────

	private static async ValueTask<SectionalCompileResult> CompileArmorAsync(
		string modDirectory,
		VpkHostContext context,
		string payload,
		string sourcePatch,
		WardrobeSectionMapDocument sourceMap,
		string armorArchiveId,
		IReadOnlyDictionary<string, IReadOnlyCollection<int>>? disabledSections,
		bool headMergeIntoBody,
		OverwriteSelection selection,
		CancellationToken cancellationToken)
	{
		var snapshot = await ArmorAuthorityIndexReader.TryReadAsync(context.Paths, cancellationToken).ConfigureAwait(false);
		if (snapshot is null)
			return SectionalCompileResult.Failure(
				"索引库里没有装备权威表：请先在设置页重建游戏资产索引。", SectionalCompileKind.Armor);

		var selected = snapshot.Kits.FirstOrDefault(kit =>
			string.Equals(kit.ArchiveId, armorArchiveId, StringComparison.OrdinalIgnoreCase));
		if (selected is null)
			return SectionalCompileResult.Failure(
				$"装备权威表里找不到归档 {armorArchiveId}。", SectionalCompileKind.Armor);

		// 同一套装备的护甲 + 头盔归档；**披风是第三个原子，永远不碰**。
		// 一套里可能不止一件护甲（B-08 与 FS-38 同 setId、大量 unit 共用），所以还要记下
		// "每个 unit 被哪几件引用"：规划器靠它只在各件都有的层上落几何。
		var setKits = snapshot.Kits
			.Where(kit => kit.SetId == selected.SetId
				&& kit.Kind is GameDataEquipmentKitKind.Armor or GameDataEquipmentKitKind.Helmet)
			.ToArray();
		var kitsByUnit = setKits
			.SelectMany(kit => kit.Pieces.Select(piece => (
				Key: new AssetKey(piece.UnitAssetKey.TypeId, piece.UnitAssetKey.FileId),
				kit.ArchiveId)))
			.GroupBy(pair => pair.Key)
			.ToDictionary(
				group => group.Key,
				group => (IReadOnlyCollection<string>)group
					.Select(pair => pair.ArchiveId)
					.Distinct(StringComparer.OrdinalIgnoreCase)
					.ToArray());
		var targets = setKits
			.SelectMany(kit => kit.Pieces)
			.Select(piece => new SectionalTargetUnit(
				new AssetKey(piece.UnitAssetKey.TypeId, piece.UnitAssetKey.FileId),
				ArmorCell.FromRegistryCoordinates((int)piece.Slot, (int)piece.PieceType)))
			.GroupBy(target => target.Key)
			.Select(group => group.First())
			.ToArray();
		if (targets.Length == 0)
			return SectionalCompileResult.Failure(
				$"{selected.DisplayName} 的权威表里没有任何 unit。", SectionalCompileKind.Armor);

		var source = sourceMap.Units
			.Select(unit => new SectionalSourceUnit(unit.FileId, CellOf(unit)))
			.ToArray();

		// 被牵连的护甲：不在这套里、但和这套共用了 unit（改写共用 unit 等于也改了它们）。
		// 只看护甲：头盔是独立 item，别的头盔与这套头盔共用件时，那是另一类问题，这里不碰。
		var setArchiveIds = setKits.Select(kit => kit.ArchiveId).ToHashSet(StringComparer.OrdinalIgnoreCase);
		// 只算**会被我们写到**的件（替换或隐藏）。披风件从不写，而 slot1 那个不渲染的件被几十件护甲
		// 共用——把它算进来，会把半个军械库都当成被牵连的护甲。
		var setUnitKeys = targets
			.Where(target => target.Cell.IsKnown && !target.Cell.IsCape)
			.Select(target => target.Key)
			.ToHashSet();
		var collateralKits = snapshot.Kits
			.Where(kit => kit.Kind == GameDataEquipmentKitKind.Armor
				&& !setArchiveIds.Contains(kit.ArchiveId)
				&& kit.Pieces.Any(piece => setUnitKeys.Contains(new AssetKey(piece.UnitAssetKey.TypeId, piece.UnitAssetKey.FileId))))
			.ToArray();
		// 圈外护甲（既不在这套、也没被牵连）还在用的件：一个都不能动，见 HideCollateralExtras。
		var groupArchiveIds = setArchiveIds
			.Concat(collateralKits.Select(kit => kit.ArchiveId))
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		var protectedUnits = snapshot.Kits
			.Where(kit => kit.Kind is GameDataEquipmentKitKind.Armor or GameDataEquipmentKitKind.Helmet
				&& !groupArchiveIds.Contains(kit.ArchiveId))
			.SelectMany(kit => kit.Pieces)
			.Select(piece => new AssetKey(piece.UnitAssetKey.TypeId, piece.UnitAssetKey.FileId))
			.ToHashSet();
		var collateral = collateralKits
			.Select(kit => new SectionalCollateralKit(
				kit.DisplayName,
				kit.Pieces
					.Select(piece => new SectionalTargetUnit(
						new AssetKey(piece.UnitAssetKey.TypeId, piece.UnitAssetKey.FileId),
						ArmorCell.FromRegistryCoordinates((int)piece.Slot, (int)piece.PieceType)))
					.GroupBy(target => target.Key)
					.Select(group => group.First())
					.ToArray()))
			.ToArray();

		var plan = SectionalCompilePlanner.Build(
			source, targets, headMergeIntoBody, kitsByUnit, collateral, protectedUnits);
		var staging = CreateStagingDirectory();

		try
		{
			var renames = plan.Replacements
				.Select(replacement => new SectionalUnitRename(replacement.Source.FileId, replacement.Target.Key))
				.ToArray();
			var hiddenKeys = plan.Hidden.Select(hidden => hidden.Key).ToArray();
			var patch = await SectionalUnitPatchWriter
				.WriteAsync(sourcePatch, staging, renames, hiddenKeys, new SourceUnitHiddenUnitSource(), cancellationToken)
				.ConfigureAwait(false);

			var staged = Path.Combine(staging, Path.GetFileName(sourcePatch));

			// 表按**输出去重前**的 patch 建：此刻记的是烘焙前原值，将来"打开"才写得回去。
			var built = await WardrobeSectionMapFile
				.BuildAsync(staged, cancellationToken, sourceUnits: BuildSourceUnitMap(plan))
				.ConfigureAwait(false);
			// 换甲之后 unit 键换成了目标护甲的，但"这块几何是什么"没变：
			// 身份按 plan 的 目标键 → 来源键 对齐搬过去，否则 Overwrite 里的表又成了空身份。
			var documented = WardrobeSectionMapFile.ApplyUnitIdentities(
				built.Document, SourceUnitIdentities(sourceMap, plan));
			var translated = TranslateDisabled(disabledSections, plan.Replacements);
			var baked = await WardrobeSectionBaker
				.BakeAsync(staged, documented, translated, cancellationToken)
				.ConfigureAwait(false);

			var materialized = OverwriteMaterializer.Materialize(
				modDirectory, selection, staged, ManagedOverwriteMarkers.VpkRekey);
			if (!materialized.Succeeded)
				return SectionalCompileResult.Failure(materialized.Error ?? "物化生效层失败。", SectionalCompileKind.Armor);

			await WriteStructureAsync(
				modDirectory, documented, await TranslateGroupsAsync(payload, plan, cancellationToken).ConfigureAwait(false),
				cancellationToken).ConfigureAwait(false);
			DeleteStaleCustomModel(modDirectory);

			var info = new List<string>(plan.Info);
			var warnings = new List<string>(plan.Warnings);
			if (baked.SectionsDisabled != 0)
				info.Add($"已把 {baked.SectionsDisabled} 个 section 烘成关闭状态。");
			info.Add(
				$"已换到「{selected.DisplayName}」：写出去 {patch.RewrittenUnitCount} 份改名副本、"
				+ $"顶了 {patch.HiddenUnitCount} 个隐藏壳。");
			warnings.AddRange(patch.Warnings);
			// 极小占位的判据：每个 mesh 恰好 1 个三角形。对不上就说明 minify 没生效，
			// 隐藏壳会画出一个完整的模型——那是最难查的一类"进游戏才发现"。
			if (patch.HiddenUnitCount != 0 && patch.HiddenTriangles != patch.HiddenMeshCount)
				warnings.Add(
					$"隐藏壳的三角形数 {patch.HiddenTriangles} 与 mesh 数 {patch.HiddenMeshCount} 不一致："
					+ "极小占位没生效，进游戏可能会看到本该消失的原版件。");
			warnings.AddRange(baked.Warnings);
			warnings.AddRange(materialized.Warnings);

			return new SectionalCompileResult(
				true, null, SectionalCompileKind.Armor, selected.DisplayName,
				patch.RewrittenUnitCount, patch.HiddenUnitCount,
				plan.HeadMerge switch
				{
					SectionalHeadMergePlan.IntoArmorTorso => SectionalHeadMergeOutcome.AppliedIntoArmorTorso,
					SectionalHeadMergePlan.SkippedNoFreeTorsoLayer => SectionalHeadMergeOutcome.SkippedNoFreeTorsoLayer,
					SectionalHeadMergePlan.NoHelmetKit => SectionalHeadMergeOutcome.NoHelmetKit,
					_ => SectionalHeadMergeOutcome.Off,
				},
				patch.HiddenTriangles, info, warnings);
		}
		catch (Exception exception) when (IsExpected(exception))
		{
			return SectionalCompileResult.Failure("换甲编译失败：" + exception.Message, SectionalCompileKind.Armor);
		}
		finally
		{
			DeleteQuietly(staging);
		}
	}

	// ── 小工具 ─────────────────────────────────────────────────────────────

	private static readonly IReadOnlyDictionary<string, IReadOnlyCollection<int>> EmptySections
		= new Dictionary<string, IReadOnlyCollection<int>>(StringComparer.Ordinal);

	/// <summary>
	/// 判定目标形态。<paramref name="ownModel"/> 为真时"目标就是自己"。
	/// <para>
	/// <b>只认本 Mod 自己的素体 key。</b>选了**别人的**素体会明确拒绝：那种情形下 unit 键根本不用换，
	/// 静默当自己是"自身素体"就等于把用户选的目标悄悄忽略掉——最坏的一种失败方式。
	/// </para>
	/// </summary>
	private static string? ResolveArmorArchive(
		string? targetArchiveId,
		string? ownModelKey,
		out bool ownModel,
		out string? error)
	{
		error = null;
		ownModel = true;
		if (string.IsNullOrWhiteSpace(targetArchiveId))
			return null;

		if (!targetArchiveId.StartsWith(RuntimeArmorRegistryService.CustomModelArchiveIdPrefix, StringComparison.OrdinalIgnoreCase))
		{
			ownModel = false;
			return targetArchiveId;
		}

		ownModel = true;
		if (!RuntimeArmorRegistryService.TryReadCustomModelKey(targetArchiveId, out var key))
		{
			error = $"目标 id 认不出来：{targetArchiveId}";
			return null;
		}

		if (ownModelKey is not null && string.Equals(key, ownModelKey, StringComparison.OrdinalIgnoreCase))
			return null;

		error = $"不支持把 VPK 模组挂到另一个已注册素体上（目标「{key}」）。请选择这件 Mod 自己的素体，或选一件游戏护甲。";
		return null;
	}

	/// <summary>定位 unit patch：优先按 <c>patches.json</c> 的角色，再退回固定序号。</summary>
	private static async ValueTask<string?> ResolveUnitPatchAsync(string payload, CancellationToken cancellationToken)
	{
		var manifest = await SectionalPatchManifestFile.TryReadAsync(payload, cancellationToken).ConfigureAwait(false);
		var entry = manifest?.Patches
			.Where(patch => patch.Role == SectionalPatchRole.Unit)
			.OrderBy(patch => patch.PatchIndex)
			.FirstOrDefault();
		var archive = entry?.ArchiveHex16 ?? SectionalPatchManifestFile.ReadArchiveHex16(payload);
		var index = entry?.PatchIndex ?? 0;
		if (string.IsNullOrWhiteSpace(archive))
			return null;
		var path = Path.Combine(payload, archive + ".patch_" + index.ToString(CultureInfo.InvariantCulture));
		return File.Exists(path) ? path : null;
	}

	/// <summary>sections.json 里的一行 → 注册表格子。认不出来的部位一律 <see cref="ArmorCell.Unknown"/>。</summary>
	private static ArmorCell CellOf(WardrobeSectionMapUnit unit)
	{
		var part = ArmorStructure.TryParse(unit.Slot);
		if (part is null) return ArmorCell.Unknown;
		var slot = ArmorStructure.SlotOf(part.Value);
		if (slot is null) return ArmorCell.Unknown;
		return new ArmorCell((int)slot.Value, LayerOf(unit.PieceType));
	}

	/// <summary>件级 PieceType 字符串 → 层号（0 内层 / 1 护甲 / 2 装饰）。</summary>
	private static int LayerOf(string? pieceType) => pieceType?.Trim().ToLowerInvariant() switch
	{
		"armor" => 1,
		"accessory" => 2,
		_ => 0,
	};

	/// <summary>目标 unit 键 → 来源 unit 键（写进 sections.json 的 <c>SourceUnit</c>，供将来就地烘焙翻译）。</summary>
	private static IReadOnlyDictionary<string, string> BuildSourceUnitMap(SectionalCompilePlan plan)
	{
		var map = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var replacement in plan.Replacements)
			map[replacement.Target.FileId] = replacement.Source.FileId;
		return map;
	}

	/// <summary>
	/// 从来源表取出每个 unit 的身份（<c>sections.json</c> 里的槽位/层/身形/重量）。
	/// <para>
	/// 自身素体合成（键不变）直接按 <c>fileId</c> 对齐；换到目标护甲时按
	/// <paramref name="plan"/> 的 <c>目标键 → 来源键</c> 对齐——几何搬过去了，身份跟着搬。
	/// 找不到来源的 unit（目标是隐藏壳之类）不写身份：宁可空着，也不要编一个。
	/// </para>
	/// </summary>
	private static IReadOnlyDictionary<string, WardrobeSectionMapUnitIdentity> SourceUnitIdentities(
		WardrobeSectionMapDocument sourceMap,
		SectionalCompilePlan? plan)
	{
		ArgumentNullException.ThrowIfNull(sourceMap);
		var bySource = new Dictionary<string, WardrobeSectionMapUnitIdentity>(StringComparer.OrdinalIgnoreCase);
		foreach (var unit in sourceMap.Units)
		{
			if (string.IsNullOrWhiteSpace(unit.Slot) && string.IsNullOrWhiteSpace(unit.PieceType))
				continue;
			bySource[unit.FileId] = new WardrobeSectionMapUnitIdentity(unit.Slot, unit.PieceType, unit.BodyType, unit.Weight);
		}

		if (plan is null)
			return bySource;

		var translated = new Dictionary<string, WardrobeSectionMapUnitIdentity>(StringComparer.OrdinalIgnoreCase);
		foreach (var replacement in plan.Replacements)
		{
			if (bySource.TryGetValue(replacement.Source.FileId, out var identity))
				translated[replacement.Target.FileId] = identity;
		}

		return translated;
	}

	/// <summary>把"来源键空间的禁用集合"翻译成"目标键空间"。映射不到的条目直接丢掉。</summary>
	private static IReadOnlyDictionary<string, IReadOnlyCollection<int>> TranslateDisabled(
		IReadOnlyDictionary<string, IReadOnlyCollection<int>>? disabled,
		IReadOnlyList<SectionalReplacement> replacements)
	{
		if (disabled is null || disabled.Count == 0)
			return EmptySections;

		var bySource = new Dictionary<string, List<string>>(StringComparer.Ordinal);
		foreach (var replacement in replacements)
		{
			if (!bySource.TryGetValue(replacement.Source.FileId, out var list))
				bySource[replacement.Source.FileId] = list = [];
			if (!list.Contains(replacement.Target.FileId))
				list.Add(replacement.Target.FileId);
		}

		var result = new Dictionary<string, IReadOnlyCollection<int>>(StringComparer.Ordinal);
		foreach (var (rawUnit, sections) in disabled)
		{
			if (sections.Count == 0) continue;
			var unit = NormalizeUnitKey(rawUnit);
			if (unit.Length == 0 || !bySource.TryGetValue(unit, out var targets)) continue;
			foreach (var target in targets)
			{
				result[target] = result.TryGetValue(target, out var existing)
					? [.. existing.Concat(sections).Distinct().Order()]
					: [.. sections.Distinct().Order()];
			}
		}

		return result;
	}

	private static string NormalizeUnitKey(string? unit)
	{
		if (string.IsNullOrWhiteSpace(unit)) return string.Empty;
		var text = unit.Trim();
		if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
		return text.ToLowerInvariant();
	}

	/// <summary>
	/// 组表（<c>components.json</c>）翻译：来源 unit 键 → 目标 unit 键，**一个来源可以变成多份**。
	/// <para>
	/// 写进生效层只是兜底：读侧永远优先权威侧。翻译不出来（来源表不存在）时返回 <c>null</c>，不写。
	/// </para>
	/// </summary>
	private static async ValueTask<SectionGroupDocument?> TranslateGroupsAsync(
		string payload,
		SectionalCompilePlan plan,
		CancellationToken cancellationToken)
	{
		var document = await SectionGroupFile.TryReadAsync(payload, cancellationToken).ConfigureAwait(false);
		if (document is null) return null;

		var bySource = new Dictionary<string, List<string>>(StringComparer.Ordinal);
		foreach (var replacement in plan.Replacements)
		{
			if (!bySource.TryGetValue(replacement.Source.FileId, out var list))
				bySource[replacement.Source.FileId] = list = [];
			if (!list.Contains(replacement.Target.FileId))
				list.Add(replacement.Target.FileId);
		}

		var groups = new List<SectionGroup>(document.Groups.Count);
		foreach (var group in document.Groups)
		{
			var members = new List<SectionGroupTarget>(group.Members.Count);
			foreach (var member in group.Members)
			{
				if (!bySource.TryGetValue(NormalizeUnitKey(member.Unit), out var targets))
					continue;
				foreach (var target in targets)
				{
					var existing = members.FirstOrDefault(candidate =>
						string.Equals(candidate.Unit, target, StringComparison.Ordinal));
					if (existing is null)
						members.Add(new SectionGroupTarget(target, [.. member.Sections.Order()]));
					else
						members[members.IndexOf(existing)] = existing with
						{
							Sections = [.. existing.Sections.Concat(member.Sections).Distinct().Order()],
						};
				}
			}

			groups.Add(group with { Members = [.. members.OrderBy(member => member.Unit, StringComparer.Ordinal)] });
		}

		return document with { Groups = groups };
	}

	/// <summary>写生效层的结构文件（表 + 组表）。必须在物化**之后**调用：物化器会重开目录。</summary>
	private static async ValueTask WriteStructureAsync(
		string modDirectory,
		WardrobeSectionMapDocument sections,
		SectionGroupDocument? components,
		CancellationToken cancellationToken)
	{
		var overwrite = ModNodePayloadLayout.GetOverwriteDirectory(modDirectory);
		Directory.CreateDirectory(overwrite);
		await WardrobeSectionMapFile
			.WriteAsync(Path.Combine(overwrite, WardrobeSectionMapFile.FileName), sections, cancellationToken)
			.ConfigureAwait(false);

		if (components is not null)
			await SectionGroupFile.WriteAsync(overwrite, components, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>换甲之后必须删掉上一次合并留下的派生注册表，否则素体目录还按合并形态算。</summary>
	private static void DeleteStaleCustomModel(string modDirectory)
	{
		var path = Path.Combine(ModNodePayloadLayout.GetOverwriteDirectory(modDirectory), VpkCustomModelFile.FileName);
		if (!File.Exists(path)) return;
		VpkPayloadSwap.ClearReadOnlyForWrite(path);
		File.Delete(path);
	}

	private static string CreateStagingDirectory()
	{
		var path = Path.Combine(Path.GetTempPath(), "hd2-sectional-compile", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(path);
		return path;
	}

	private static bool IsExpected(Exception exception)
		=> exception is IOException or InvalidDataException or InvalidOperationException
			or UnauthorizedAccessException or System.Security.SecurityException or FormatException;

	private static void DeleteQuietly(string directory)
	{
		try
		{
			if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
		}
		catch (IOException) { }
		catch (UnauthorizedAccessException) { }
	}
}
