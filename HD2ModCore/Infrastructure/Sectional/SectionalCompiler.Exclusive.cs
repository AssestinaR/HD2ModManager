using System.Globalization;
using HD2ModCore.Decoration;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure.KitInstance;

namespace HD2ModCore.Infrastructure.Sectional;

/// <summary>
/// 专属切换（P4a）的编译分支。与 <c>CompileArmorAsync</c> 刻意分开：那一条在「专属」关闭时必须一字节不变。
/// <para>
/// 区别只有三点：落点来自 <see cref="SectionalExclusivePlanner"/>（目标记录自己独有的 unit）；
/// <b>不写任何全局空壳</b>（隐藏全部交给规则文件里的 Hider）；<b>不处理被连带的护甲</b>（只改写独有 unit，本来就不会连带）。
/// </para>
/// </summary>
public static partial class SectionalCompiler
{
	/// <summary>
	/// 读来源 unit（<c>sectional/sections.json</c> 里的 fileId + 格子）。专属规划与预览共用，口径与编译一致。
	/// </summary>
	internal static async ValueTask<IReadOnlyList<SectionalSourceUnit>?> ReadSourceUnitsAsync(
		string modDirectory, CancellationToken cancellationToken)
	{
		var payload = ModNodePayloadLayout.TryGetExistingVpkPatchDirectory(modDirectory);
		if (payload is null) return null;
		var mapPath = Path.Combine(payload, WardrobeSectionMapFile.FileName);
		if (!File.Exists(mapPath)) return null;
		var map = await WardrobeSectionMapFile.ReadAsync(mapPath, cancellationToken).ConfigureAwait(false);
		return map.Units.Select(unit => new SectionalSourceUnit(unit.FileId, CellOf(unit))).ToArray();
	}

	private static async ValueTask<SectionalCompileResult> CompileExclusiveArmorAsync(
		string modDirectory,
		VpkHostContext context,
		string payload,
		string sourcePatch,
		WardrobeSectionMapDocument sourceMap,
		uint itemId,
		IReadOnlyDictionary<string, IReadOnlyCollection<int>>? disabledSections,
		bool headMergeIntoBody,
		OverwriteSelection selection,
		CancellationToken cancellationToken)
	{
		var snapshot = await ArmorAuthorityIndexReader.TryReadAsync(context.Paths, cancellationToken).ConfigureAwait(false);
		if (snapshot is null)
			return SectionalCompileResult.Failure(
				"索引库里没有装备权威表：请先在设置页重建游戏资产索引。", SectionalCompileKind.Armor);

		var source = sourceMap.Units
			.Select(unit => new SectionalSourceUnit(unit.FileId, CellOf(unit)))
			.ToArray();
		var exclusive = SectionalExclusivePlanner.Build(
			SectionalExclusivePlanner.BuildIndex(snapshot.Kits), source, itemId, headMergeIntoBody);
		if (!exclusive.Succeeded)
			return SectionalCompileResult.Failure(
				$"不能专属切换到 {exclusive.Name}（{itemId}）：{exclusive.FailureText}", SectionalCompileKind.Armor);

		// 复用现有写出链路需要的 plan 形状：替换表照搬，隐藏集恒为空（不写空壳）。
		var plan = new SectionalCompilePlan(
			exclusive.Replacements, [], exclusive.HeadMerge, exclusive.Info, exclusive.Warnings);
		var staging = CreateStagingDirectory();
		try
		{
			var renames = plan.Replacements
				.Select(replacement => new SectionalUnitRename(replacement.Source.FileId, replacement.Target.Key))
				.ToArray();
			var patch = await SectionalUnitPatchWriter
				.WriteAsync(sourcePatch, staging, renames, [], new SourceUnitHiddenUnitSource(), cancellationToken)
				.ConfigureAwait(false);
			var staged = Path.Combine(staging, Path.GetFileName(sourcePatch));

			var built = await WardrobeSectionMapFile
				.BuildAsync(staged, cancellationToken, sourceUnits: BuildSourceUnitMap(plan))
				.ConfigureAwait(false);
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
				$"已专属切换到「{exclusive.Name}」（{itemId}）：写出去 {patch.RewrittenUnitCount} 份改名副本，没有写任何全局空壳。");
			warnings.AddRange(patch.Warnings);
			warnings.AddRange(baked.Warnings);
			warnings.AddRange(materialized.Warnings);

			var hex = (ulong value) => value.ToString("x16", CultureInfo.InvariantCulture);
			var seed = new KitExclusiveSeed(
				itemId,
				exclusive.Name,
				SectionalExclusivePlanner.Radius,
				exclusive.Signature.Take(1).Select(hex).ToArray(),
				exclusive.Signature.Skip(1).Select(hex).ToArray(),
				exclusive.Hide.Select(hex).ToArray());

			return new SectionalCompileResult(
				true, null, SectionalCompileKind.Armor, exclusive.Name,
				patch.RewrittenUnitCount, 0,
				exclusive.HeadMerge switch
				{
					SectionalHeadMergePlan.IntoArmorTorso => SectionalHeadMergeOutcome.AppliedIntoArmorTorso,
					SectionalHeadMergePlan.SkippedNoFreeTorsoLayer => SectionalHeadMergeOutcome.SkippedNoFreeTorsoLayer,
					SectionalHeadMergePlan.NoHelmetKit => SectionalHeadMergeOutcome.NoHelmetKit,
					_ => SectionalHeadMergeOutcome.Off,
				},
				0, info, warnings)
			{
				Exclusive = seed,
			};
		}
		catch (Exception exception) when (IsExpected(exception))
		{
			return SectionalCompileResult.Failure("专属换甲编译失败：" + exception.Message, SectionalCompileKind.Armor);
		}
		finally
		{
			DeleteQuietly(staging);
		}
	}
}
