using System.Globalization;
using System.Text.Json.Nodes;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModCore.Decoration;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure.Sectional;

/// <summary>
/// 低污染模式（P6 S2）的编译分支：把自身素体压缩成 3 个（头身合并开时 2 个）unit。
/// 规则见规划文档 <c>kit-instance-mapping-deploy-plan-20261114.md</c> §10。
/// <para>
/// 与 <c>CompileOwnModelAsync</c> 刻意分开：开关关着时那一条一行都不变。几何合并复用 <see cref="SectionalUnitMerger"/>
/// （含 S1 的物理骨自检），这里只负责「谁和谁合、落在哪个 ID、产物怎么描述」。
/// </para>
/// </summary>
public static partial class SectionalCompiler
{
	/// <param name="Result">压缩完成（成功或失败）；为 <c>null</c> 表示这个素体不能压缩，调用方应照常编译。</param>
	/// <param name="NotCompressedReason"><paramref name="Result"/> 为 <c>null</c> 时的原因。</param>
	private sealed record LowPollutionOutcome(SectionalCompileResult? Result, string? NotCompressedReason);

	private static async ValueTask<LowPollutionOutcome> CompileLowPollutionOwnModelAsync(
		string modDirectory,
		VpkHostContext context,
		string payload,
		string sourcePatch,
		WardrobeSectionMapDocument sourceMap,
		IReadOnlyDictionary<string, IReadOnlyCollection<int>>? disabledSections,
		bool headMergeIntoBody,
		OverwriteSelection selection,
		CancellationToken cancellationToken)
	{
		var plan = SectionalCompressionPlanner.Plan(
			[.. sourceMap.Units.Select(unit => new SectionalCompressionSource(
				unit.FileId, ArmorStructure.TryParse(unit.Slot), unit.BodyType))],
			headMergeIntoBody);
		if (!plan.Supported)
			return new LowPollutionOutcome(null, plan.Reason);

		if (plan.NeedsMerge && (string.IsNullOrWhiteSpace(context.GameDataFolder) || !Directory.Exists(context.GameDataFolder)))
		{
			return new LowPollutionOutcome(
				SectionalCompileResult.Failure("低污染模式要合并几何，需要读玩家头像骨架：请先在设置页指定游戏目录。"), null);
		}

		var preStaging = CreateStagingDirectory();
		var staging = CreateStagingDirectory();
		try
		{
			var info = new List<string>();
			var warnings = new List<string>();

			// ① 先在来源副本上烘好关闭的 section（来源 unit 键空间，与自身素体、专属合并同一条路径），再合并几何。
			//    合并之后 section 序号变了，不能再按来源键烘。
			var pre = OverwritePatchInstaller.Install(preStaging, sourcePatch).PatchPath;
			var preBuilt = await WardrobeSectionMapFile.BuildAsync(pre, cancellationToken).ConfigureAwait(false);
			var preDocument = WardrobeSectionMapFile.ApplyUnitIdentities(
				preBuilt.Document, SourceUnitIdentities(sourceMap, plan: null));
			var preBaked = await WardrobeSectionBaker
				.BakeAsync(pre, preDocument, disabledSections ?? EmptySections, cancellationToken)
				.ConfigureAwait(false);
			warnings.AddRange(preBaked.Warnings);

			// ② 每个压缩槽：多成员合并（ID 所有者放最前），单成员原样改名到同一个 ID。
			UnitTransformInfo? avatar = plan.NeedsMerge
				? await SectionalUnitMerger.ReadAvatarAsync(context.GameDataFolder!, cancellationToken).ConfigureAwait(false)
				: null;
			var renames = new List<SectionalUnitRename>();
			var merged = new List<SectionalMergedOutput>();
			foreach (var group in plan.Groups)
			{
				var key = UnitKey(group.UnitId);
				if (group.Members.Count == 1)
				{
					renames.Add(new SectionalUnitRename(group.UnitId, key));
					continue;
				}

				var unit = await SectionalUnitMerger
					.MergeAsync(pre, new SectionalUnitMergeSpec([.. group.Members.Select(member => member.SourceUnitId)]), avatar!, cancellationToken)
					.ConfigureAwait(false);
				merged.Add(new SectionalMergedOutput([key], unit));
			}

			foreach (var member in plan.Passthrough)
				renames.Add(new SectionalUnitRename(member.SourceUnitId, UnitKey(member.SourceUnitId)));

			var patch = await SectionalUnitPatchWriter
				.WriteAsync(pre, staging, renames, [], new SourceUnitHiddenUnitSource(), cancellationToken, merged)
				.ConfigureAwait(false);
			var staged = Path.Combine(staging, Path.GetFileName(pre));

			// ③ 表按压缩后的 patch 重建；压缩 unit 的 ID 就是 ID 所有者的来源 ID，身份按 ID 直接对齐。
			var built = await WardrobeSectionMapFile.BuildAsync(staged, cancellationToken).ConfigureAwait(false);
			var documented = WardrobeSectionMapFile.ApplyUnitIdentities(
				built.Document, SourceUnitIdentities(sourceMap, plan: null));
			var baked = await WardrobeSectionBaker
				.BakeAsync(staged, documented, EmptySections, cancellationToken)
				.ConfigureAwait(false);

			var materialized = OverwriteMaterializer.Materialize(
				modDirectory, selection, staged, ManagedOverwriteMarkers.VpkRekey);
			if (!materialized.Succeeded)
				return new LowPollutionOutcome(SectionalCompileResult.Failure(materialized.Error ?? "物化生效层失败。"), null);

			// 结构文件只能写在物化**之后**：物化器会重开目录。合并后序号已变，组表不写（读侧永远优先权威侧）。
			// 合体 unit（规划 §17）不进结构表：它没有部位身份，只经映射行用到，详情页的部件列表里不该出现它。
			var bodyArms = plan.Groups.Where(group => group.Role == SectionalCompressionRole.BodyArms)
				.Select(group => group.UnitId).ToHashSet(StringComparer.OrdinalIgnoreCase);
			var structure = bodyArms.Count == 0
				? documented
				: documented with { Units = [.. documented.Units.Where(unit => !bodyArms.Contains(unit.FileId))] };
			await WriteStructureAsync(modDirectory, structure, components: null, cancellationToken).ConfigureAwait(false);
			WriteLowPollutionRegistry(modDirectory, payload, plan, warnings);
			WriteLowPollutionLayout(modDirectory, plan, LowPollutionFreshness.Fingerprint(payload, disabledSections, headMergeIntoBody));

			info.Add(
				$"低污染模式：素体压缩成 {plan.Groups.Count} 个 unit"
				+ $"（{string.Join("、", plan.Groups.Select(group => $"{LowPollutionLayoutFile.RoleName(group.Role)}=0x{group.UnitId}"))}）；"
				+ "合并后的部件不再支持就地开关 section，改勾选请再点一次重构建。");
			if (plan.Passthrough.Count != 0)
				info.Add($"{string.Join("、", plan.Passthrough.Select(member => ArmorStructure.Display(member.Part)))}的 unit 原样保留，没有并入压缩槽。");
			warnings.AddRange(patch.Warnings);
			warnings.AddRange(baked.Warnings);
			warnings.AddRange(materialized.Warnings);

			return new LowPollutionOutcome(
				new SectionalCompileResult(
					true, null, SectionalCompileKind.OwnModel, null, patch.RewrittenUnitCount, 0,
					headMergeIntoBody ? SectionalHeadMergeOutcome.AppliedOwnModel : SectionalHeadMergeOutcome.Off,
					0, info, warnings),
				null);
		}
		catch (Exception exception) when (IsExpected(exception))
		{
			return new LowPollutionOutcome(SectionalCompileResult.Failure("低污染模式编译失败：" + exception.Message), null);
		}
		finally
		{
			DeleteQuietly(staging);
			DeleteQuietly(preStaging);
		}
	}

	private static AssetKey UnitKey(string fileId)
		=> new(PatchUnitMeshReader.UnitTypeId, ulong.Parse(fileId, NumberStyles.HexNumber, CultureInfo.InvariantCulture));

	/// <summary>
	/// 素体注册表：只留压缩 unit 的注册件，被吸收部位的注册件剪掉（与 <c>CustomModelSectionalCompaction</c> 同口径，不留悬空引用）。
	/// 写进生效层，<c>sectional/</c> 那份是权威留档、一个字节都不动。
	/// </summary>
	private static void WriteLowPollutionRegistry(string modDirectory, string payload, SectionalCompressionPlan plan, List<string> warnings)
	{
		var source = Path.Combine(payload, VpkCustomModelFile.FileName);
		if (!File.Exists(source))
		{
			warnings.Add("低污染模式：sectional 里没有 custom-model.json，生效层没有写素体注册表。");
			return;
		}

		if (JsonNode.Parse(File.ReadAllText(source)) is not JsonObject document || document["pieces"] is not JsonArray pieces)
		{
			warnings.Add("低污染模式：custom-model.json 结构不认识，生效层没有写素体注册表。");
			return;
		}

		var surviving = plan.SurvivingUnitIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
		// 合体 unit（规划 §17）不登记：它只经映射行用到，不是素体自己穿的部件；来源注册件里也没有它的 ID。
		var splitRoles = plan.Groups.Where(group => group.Body != HD2ModAdaptation.Analysis.UnitMeshBodyVariant.Any
				&& group.Role != SectionalCompressionRole.BodyArms)
			.Select(group => group.Role).ToHashSet();
		var splitUnits = plan.Groups.Where(group => splitRoles.Contains(group.Role)).Select(group => group.UnitId)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		var kept = new JsonArray();
		foreach (var piece in pieces.OfType<JsonObject>())
		{
			var unit = piece["unit"]?.GetValue<string>();
			// 拆开的槽另行处理（下面按体型写 body 0 / 1）；没拆的槽照旧：留压缩 unit 的注册件。
			if (unit is not null && surviving.Contains(unit) && !splitUnits.Contains(unit))
				kept.Add(piece.DeepClone());
		}

		// 拆开的槽：写 body 0（壮实）/ 1（纤细）两条，各指自己体型的压缩 unit；缺某个体型时那一条指另一体型的（规划 §13.3 / §13.6 Q2）。
		foreach (var role in plan.Groups.Where(group => splitRoles.Contains(group.Role)).Select(group => group.Role).Distinct())
		{
			var groups = plan.Groups.Where(group => group.Role == role).ToArray();
			var slotPart = groups.SelectMany(group => group.Members.Select(member => member.Part))
				.Distinct().OrderBy(part => Array.IndexOf(SlotPartOrder(role), part) is var at and >= 0 ? at : int.MaxValue).First();
			var slot = ArmorStructure.SlotOf(slotPart);
			foreach (var (body, raw) in new[] { (HD2ModAdaptation.Analysis.UnitMeshBodyVariant.Stocky, 0), (HD2ModAdaptation.Analysis.UnitMeshBodyVariant.Slim, 1) })
			{
				var group = groups.FirstOrDefault(candidate => candidate.Body == body)
					?? groups.First(candidate => candidate.Body != body);
				var template = pieces.OfType<JsonObject>().FirstOrDefault(piece =>
					string.Equals(piece["unit"]?.GetValue<string>(), group.UnitId, StringComparison.OrdinalIgnoreCase));
				var entry = (JsonObject?)template?.DeepClone() ?? new JsonObject { ["pieceType"] = 0 };
				entry["body"] = raw;
				entry["unit"] = group.UnitId;
				if (slot is not null) entry["slot"] = (int)slot.Value;
				kept.Add(entry);
			}
		}

		document["pieces"] = kept;
		var destination = Path.Combine(ModNodePayloadLayout.GetOverwriteDirectory(modDirectory), VpkCustomModelFile.FileName);
		VpkPayloadSwap.ClearReadOnlyForWrite(destination);
		File.WriteAllText(destination, document.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
	}

	private static ArmorPart[] SlotPartOrder(SectionalCompressionRole role) => role switch
	{
		SectionalCompressionRole.Head => [ArmorPart.Helmet],
		SectionalCompressionRole.Arms => SectionalCompressionPlanner.ArmPartOrder,
		_ => SectionalCompressionPlanner.BodyPartOrder,
	};

	private static void WriteLowPollutionLayout(string modDirectory, SectionalCompressionPlan plan, string fingerprint)
	{
		var destination = Path.Combine(ModNodePayloadLayout.GetOverwriteDirectory(modDirectory), LowPollutionLayoutFile.FileName);
		VpkPayloadSwap.ClearReadOnlyForWrite(destination);
		File.WriteAllText(destination, LowPollutionLayoutFile.Serialize(plan, fingerprint));
	}

	/// <summary>上一次压缩留在生效层里的描述文件：不再压缩（或换了目标）时必须删掉，否则 S3 会按过期布局写映射。</summary>
	private static void DeleteStaleLowPollutionLayout(string modDirectory)
	{
		var overwrite = ModNodePayloadLayout.GetOverwriteDirectory(modDirectory);
		foreach (var name in new[] { LowPollutionLayoutFile.FileName, LowPollutionFreshness.UnsupportedMarkerFileName })
		{
			var path = Path.Combine(overwrite, name);
			if (!File.Exists(path)) continue;
			VpkPayloadSwap.ClearReadOnlyForWrite(path);
			File.Delete(path);
		}
	}
}
