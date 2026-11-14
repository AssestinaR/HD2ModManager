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
/// <para>
/// P5 起支持多目标：每个目标各自规划，合成同一份 <c>Overwrite/</c>（见 <see cref="SectionalExclusiveCombiner"/>）。
/// 单目标的产物与以前逐字节一致。
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
		IReadOnlyList<uint> itemIds,
		IReadOnlyDictionary<string, IReadOnlyCollection<int>>? disabledSections,
		bool headMergeIntoBody,
		OverwriteSelection selection,
		bool autoSkipFailedTargets,
		SectionalTargetFailureHandler? onTargetFailures,
		CancellationToken cancellationToken)
	{
		var snapshot = await ArmorAuthorityIndexReader.TryReadAsync(context.Paths, cancellationToken).ConfigureAwait(false);
		if (snapshot is null)
			return SectionalCompileResult.Failure(
				"索引库里没有装备权威表：请先在设置页重建游戏资产索引。", SectionalCompileKind.Armor);

		var source = sourceMap.Units
			.Select(unit => new SectionalSourceUnit(unit.FileId, CellOf(unit)))
			.ToArray();
		var index = SectionalExclusivePlanner.BuildIndex(snapshot.Kits);

		// ① 每个目标各自按 P4a/P4b 规划。规划失败的目标记进 failures，和后面的合并失败走同一条「问用户」的路。
		var failures = new List<SectionalTargetFailure>();
		var planned = new List<SectionalExclusivePlan>();
		foreach (var itemId in itemIds.Distinct().Order())
		{
			var item = SectionalExclusivePlanner.BuildWithMerge(index, source, itemId, headMergeIntoBody);
			if (item.Succeeded) planned.Add(item);
			else failures.Add(new SectionalTargetFailure(itemId, item.Name, item.FailureText));
		}

		// ② 承载件冲突：被前面的目标以不同内容占用的目标记为失败。
		var combined = SectionalExclusiveCombiner.Combine(planned);
		failures.AddRange(combined.Conflicts);
		var accepted = combined.Accepted.ToList();

		var staging = CreateStagingDirectory();
		var preStaging = accepted.Any(item => item.IsMerged) ? CreateStagingDirectory() : null;
		try
		{
			// ③ P4b 合并：先在来源副本上烘好关闭的 section（来源 unit 键空间，与自身素体同一条路径），
			// 再从烘好的副本合并几何。合并之后 section 序号变了，不能再按来源键翻译着烘。
			var writeSource = sourcePatch;
			var preBakedSections = 0;
			var mergeWarnings = new List<string>();
			var mergedUnits = new Dictionary<string, SectionalMergedUnit>(StringComparer.Ordinal);
			if (preStaging is not null)
			{
				string? prepareError = null;
				HD2ModAdaptation.PatchReconstruction.UnitMesh.UnitTransformInfo? avatar = null;
				string? pre = null;
				if (string.IsNullOrWhiteSpace(context.GameDataFolder) || !Directory.Exists(context.GameDataFolder))
				{
					prepareError = "合并几何要读玩家头像骨架，需要游戏 data 目录：请先在设置页指定游戏目录。";
				}
				else
				{
					try
					{
						pre = OverwritePatchInstaller.Install(preStaging, sourcePatch).PatchPath;
						var preBuilt = await WardrobeSectionMapFile.BuildAsync(pre, cancellationToken).ConfigureAwait(false);
						var preDocument = WardrobeSectionMapFile.ApplyUnitIdentities(
							preBuilt.Document, SourceUnitIdentities(sourceMap, plan: null));
						var preBaked = await WardrobeSectionBaker
							.BakeAsync(pre, preDocument, disabledSections ?? EmptySections, cancellationToken)
							.ConfigureAwait(false);
						preBakedSections = preBaked.SectionsDisabled;
						mergeWarnings.AddRange(preBaked.Warnings);
						avatar = await SectionalUnitMerger.ReadAvatarAsync(context.GameDataFolder, cancellationToken).ConfigureAwait(false);
					}
					catch (Exception exception) when (IsExpected(exception))
					{
						prepareError = exception.Message;
					}
				}

				foreach (var item in accepted.Where(item => item.IsMerged).ToArray())
				{
					var reason = prepareError;
					if (reason is null)
					{
						try
						{
							foreach (var group in item.Merges.GroupBy(merge => string.Join(",", merge.SourceFileIds)))
							{
								if (mergedUnits.ContainsKey(group.Key)) continue;
								mergedUnits[group.Key] = await SectionalUnitMerger
									.MergeAsync(pre!, new SectionalUnitMergeSpec(group.First().SourceFileIds), avatar!, cancellationToken)
									.ConfigureAwait(false);
							}
						}
						catch (Exception exception) when (IsExpected(exception))
						{
							reason = exception.Message;
						}
					}

					if (reason is null) continue;
					accepted.Remove(item);
					failures.Add(new SectionalTargetFailure(item.ItemId, item.Name, reason));
				}

				if (accepted.Any(item => item.IsMerged)) writeSource = pre!;
			}

			// ④ 有目标失败：自动跳过（「继续并记住」）或问用户；回滚 = 什么都没写（此刻还没物化）。
			if (failures.Count != 0)
			{
				var proceed = false;
				if (accepted.Count != 0)
				{
					proceed = autoSkipFailedTargets
						|| (onTargetFailures is not null
							&& await onTargetFailures(failures, accepted.Count).ConfigureAwait(false));
				}

				if (!proceed)
				{
					var text = itemIds.Distinct().Count() == 1 && failures.Count == 1
						? $"不能专属切换到 {failures[0].Title}：{failures[0].Reason}"
						: $"有 {failures.Count} 个目标没能编译，已回滚、什么都没写：" + string.Join("；", failures.Select(item => $"{item.Title}：{item.Reason}"));
					return SectionalCompileResult.Failure(text, SectionalCompileKind.Armor) with { SkippedTargets = failures };
				}
			}

			combined = SectionalExclusiveCombiner.Combine(accepted);
			var anyMerged = combined.Merges.Count != 0;

			// 复用现有写出链路需要的 plan 形状：替换表照搬（去重后），隐藏集恒为空（不写空壳）。
			var multi = combined.Accepted.Count > 1;
			string Prefix(SectionalExclusivePlan item) => multi ? $"[{item.Name}（{item.ItemId}）] " : string.Empty;
			var first = combined.Accepted[0];
			var plan = new SectionalCompilePlan(
				combined.Replacements, [], first.HeadMerge,
				[.. combined.Accepted.SelectMany(item => item.Info.Select(line => Prefix(item) + line))],
				[.. combined.Accepted.SelectMany(item => item.Warnings.Select(line => Prefix(item) + line))]);

			var mergedOutputs = anyMerged
				? combined.Merges.Select(group => new SectionalMergedOutput(
					[.. group.Carriers.Select(carrier => new AssetKey(
						HD2ModAdaptation.PatchReconstruction.UnitMesh.PatchUnitMeshReader.UnitTypeId, carrier))],
					mergedUnits[string.Join(",", group.SourceFileIds)])).ToArray()
				: null;
			var renames = combined.Renames
				.Select(replacement => new SectionalUnitRename(replacement.Source.FileId, replacement.Target.Key))
				.ToArray();
			var patch = await SectionalUnitPatchWriter
				.WriteAsync(writeSource, staging, renames, [], new SourceUnitHiddenUnitSource(), cancellationToken, mergedOutputs)
				.ConfigureAwait(false);
			var staged = Path.Combine(staging, Path.GetFileName(writeSource));

			var built = await WardrobeSectionMapFile
				.BuildAsync(staged, cancellationToken, sourceUnits: BuildSourceUnitMap(plan))
				.ConfigureAwait(false);
			var documented = WardrobeSectionMapFile.ApplyUnitIdentities(
				built.Document, SourceUnitIdentities(sourceMap, plan));
			var translated = anyMerged ? EmptySections : TranslateDisabled(disabledSections, plan.Replacements);
			var baked = await WardrobeSectionBaker
				.BakeAsync(staged, documented, translated, cancellationToken)
				.ConfigureAwait(false);

			var materialized = OverwriteMaterializer.Materialize(
				modDirectory, selection, staged, ManagedOverwriteMarkers.VpkRekey);
			if (!materialized.Succeeded)
				return SectionalCompileResult.Failure(materialized.Error ?? "物化生效层失败。", SectionalCompileKind.Armor);

			await WriteStructureAsync(
				modDirectory, documented,
				// 合并产物的 section 已经重排，来源的组表对不上：不写（读侧永远优先权威侧，这只是兜底）。
				anyMerged ? null : await TranslateGroupsAsync(payload, plan, cancellationToken).ConfigureAwait(false),
				cancellationToken).ConfigureAwait(false);
			DeleteStaleCustomModel(modDirectory);

			var info = new List<string>(plan.Info);
			var warnings = new List<string>(plan.Warnings);
			if (baked.SectionsDisabled + preBakedSections != 0)
				info.Add($"已把 {baked.SectionsDisabled + preBakedSections} 个 section 烘成关闭状态。");
			info.Add(multi
				? $"已专属切换到 {combined.Accepted.Count} 套护甲（{string.Join("、", combined.Accepted.Select(item => item.Name + "（" + item.ItemId + "）"))}）："
					+ $"写出去 {patch.RewrittenUnitCount} 份改名副本，没有写任何全局空壳。"
				: $"已专属切换到「{first.Name}」（{first.ItemId}）：写出去 {patch.RewrittenUnitCount} 份改名副本，没有写任何全局空壳。");
			if (anyMerged)
				info.Add("合并（实验）：合并后的部件不再支持就地开关 section，改勾选请再点一次「切换」重新编译。");
			warnings.AddRange(mergeWarnings);
			warnings.AddRange(patch.Warnings);
			warnings.AddRange(baked.Warnings);
			warnings.AddRange(materialized.Warnings);

			var seeds = combined.Accepted.Select(KitExclusiveService.SeedOf).ToArray();
			return new SectionalCompileResult(
				true, null, SectionalCompileKind.Armor, multi ? $"{combined.Accepted.Count} 套护甲" : first.Name,
				patch.RewrittenUnitCount, 0,
				first.HeadMerge switch
				{
					SectionalHeadMergePlan.IntoArmorTorso => SectionalHeadMergeOutcome.AppliedIntoArmorTorso,
					SectionalHeadMergePlan.SkippedNoFreeTorsoLayer => SectionalHeadMergeOutcome.SkippedNoFreeTorsoLayer,
					SectionalHeadMergePlan.NoHelmetKit => SectionalHeadMergeOutcome.NoHelmetKit,
					_ => SectionalHeadMergeOutcome.Off,
				},
				0, info, warnings)
			{
				Exclusive = seeds[0],
				ExclusiveTargets = seeds,
				SkippedTargets = failures,
				CarrierCount = combined.CarrierCount,
				MergedModelCount = combined.Merges.Count,
			};
		}
		catch (Exception exception) when (IsExpected(exception))
		{
			return SectionalCompileResult.Failure("专属换甲编译失败：" + exception.Message, SectionalCompileKind.Armor);
		}
		finally
		{
			DeleteQuietly(staging);
			if (preStaging is not null) DeleteQuietly(preStaging);
		}
	}
}
