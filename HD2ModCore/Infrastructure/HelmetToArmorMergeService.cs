using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HD2ModAdaptation.Analysis;
using HD2ModAdaptation.PatchReconstruction;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModAdaptation.PatchReconstruction.UnitMesh.Canonical;
using HD2ModCore.Application;
using HD2ModCore.Domain;
using AdaptationAssetKey = HD2ModAdaptation.PatchReconstruction.AssetKey;
using AdaptationPatchTocEntry = HD2ModAdaptation.PatchReconstruction.PatchTocEntry;
using AdaptationPatchUnitMesh = HD2ModAdaptation.PatchReconstruction.UnitMesh.PatchUnitMesh;
using AdaptationGameDataPackageResolver = HD2ModAdaptation.PatchReconstruction.GameDataPackageResolver;
using CoreAssetKey = HD2ModCore.Domain.AssetKey;

namespace HD2ModCore.Infrastructure;

// 作用：识别并执行“将来源 Helmet 追加到来源 Torso，同时隐藏原 Helmet”的流程。
// Purpose: Identifies and executes the helmet-to-armor merge while hiding the source Helmet Unit.
//
// This service deliberately owns only source-set planning and Patch I/O. The actual mesh
// transformation is delegated to the existing Canonical decoration appender and hidden-unit
// builder so the two workflows cannot drift apart.
public sealed class HelmetToArmorMergeService : IHelmetToArmorMergeService
{
	private readonly IModEquipmentSourceFactsReader equipmentFacts;
	private readonly IModInformationReader informationReader;

	public HelmetToArmorMergeService(
		IModEquipmentSourceFactsReader equipmentFacts,
		IModInformationReader informationReader)
	{
		this.equipmentFacts = equipmentFacts ?? throw new ArgumentNullException(nameof(equipmentFacts));
		this.informationReader = informationReader ?? throw new ArgumentNullException(nameof(informationReader));
	}

	public async ValueTask<HelmetToArmorMergeInspection> InspectAsync(
		ModNode source,
		string modsRootDirectory,
		string gameDataDirectory,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(source);
		ArgumentException.ThrowIfNullOrWhiteSpace(modsRootDirectory);
		ArgumentException.ThrowIfNullOrWhiteSpace(gameDataDirectory);

		var operationId = Guid.NewGuid();
		var context = ModInformationRequestContext.Create(
			ModInformationCacheScope.Operation,
			operationId: operationId,
			sessionId: operationId,
			operationName: "HelmetToArmorMergeInspect");
		try
		{
			var result = await equipmentFacts.ReadAsync(
				source,
				modsRootDirectory,
				new ModInformationRequest(
					ModInformationKind.AssetInventory,
					"HelmetToArmorMerge",
					Context: context)
				{
					Property = ModInformationPropertyKind.UnitGeometrySummary
				},
				cancellationToken).ConfigureAwait(false);
			var candidates = BuildCandidates(result);
			var issues = result.Issues.ToList();
			if (candidates.Count == 0)
			{
				issues.Add(new CoreIssue(
					CoreIssueSeverity.Warning,
					"HelmetArmorPairMissing",
					"当前 Mod 没有同时包含可读取胸部与 Any 头盔的完整模型集合。",
					source.RelativePath,
					source.Id));
			}
			return new HelmetToArmorMergeInspection(
				source.Id,
				result.ContentGeneration,
				result.SourcePatchTocPaths,
				candidates,
				issues);
		}
		finally
		{
			informationReader.ClearOperation(operationId);
		}
	}

	public async ValueTask<HelmetToArmorMergeResult> ExecuteAsync(
		ModNode source,
		string sourceDirectory,
		string outputDirectory,
		string gameDataDirectory,
		IReadOnlyCollection<HelmetToArmorMergeCandidate>? selectedCandidates = null,
		CancellationToken cancellationToken = default,
		IProgress<HelmetToArmorMergeProgress>? progress = null,
		HelmetToArmorMergeOutputMode outputMode = HelmetToArmorMergeOutputMode.FirstHelmetOnly)
	{
		ArgumentNullException.ThrowIfNull(source);
		ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
		ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
		ArgumentException.ThrowIfNullOrWhiteSpace(gameDataDirectory);

		var sourceRoot = Path.GetFullPath(sourceDirectory);
		var outputRoot = Path.GetFullPath(outputDirectory);
		if (!Directory.Exists(sourceRoot))
			return Failure(outputRoot, "HelmetArmorSourceDirectoryMissing", "头盔合并找不到来源 Patch 目录。", sourceRoot);
		if (!Directory.Exists(gameDataDirectory))
			return Failure(outputRoot, "HelmetArmorGameDataMissing", "头盔合并找不到当前 Game Data 目录。", gameDataDirectory);
		if (string.Equals(sourceRoot, outputRoot, StringComparison.OrdinalIgnoreCase))
			return Failure(outputRoot, "HelmetArmorOutputEqualsSource", "头盔合并输出目录不能与来源 Patch 目录相同。", outputRoot);

		var patches = EnumerateBasePatches(sourceRoot).ToArray();
		if (patches.Length == 0)
			return Failure(outputRoot, "HelmetArmorPatchMissing", "来源 Mod 没有可处理的 Patch。", sourceRoot);

		// Candidate inspection belongs to the library-root view. Execution may instead read from a
		// generated Overwrite directory, so callers must pass the inspected candidate objects in.
		var selected = selectedCandidates?.ToDictionary(candidate => candidate.CandidateId, StringComparer.OrdinalIgnoreCase)
			?? new Dictionary<string, HelmetToArmorMergeCandidate>(StringComparer.OrdinalIgnoreCase);
		if (selected.Count == 0)
			return Failure(outputRoot, "HelmetArmorCandidatesMissing", "没有选择可执行的 Armor/Helmet 模型集合。", sourceRoot);

		var selectionIssues = ValidateSelectedCandidates(selected.Values, outputMode);
		if (selectionIssues.Count != 0)
			return new HelmetToArmorMergeResult(false, outputRoot, 0, 0, 0, selectionIssues);
		var primaryCandidates = SelectPrimaryCandidates(selected.Values);

		var operationId = Guid.NewGuid();
		var context = ModInformationRequestContext.Create(
			ModInformationCacheScope.Operation,
			operationId: operationId,
			sessionId: operationId,
			operationName: "HelmetToArmorMergeExecute");
		var issues = new List<CoreIssue>();
		var processedPatches = 0;
		var mergedArmorUnits = 0;
		var hiddenHelmetUnits = 0;
		try
		{
			var avatar = await new CanonicalAvatarRigReader(
				new AdaptationGameDataPackageResolver(gameDataDirectory))
				.ReadTransformInfoAsync(cancellationToken: cancellationToken)
				.ConfigureAwait(false);
			var targetReader = new GameDataUnitMeshReader(new AdaptationGameDataPackageResolver(gameDataDirectory));
			var patchTotal = patches.Length;
			for (var patchIndex = 0; patchIndex < patches.Length; patchIndex++)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var patchPath = patches[patchIndex];
				var patchFileName = Path.GetFileName(patchPath);
				progress?.Report(new HelmetToArmorMergeProgress($"正在读取 {patchFileName}", patchIndex, patchTotal));

				var indexResult = await informationReader.ReadPatchIndexAsync(
					new ModInformationReadRequest(
						patchPath,
						context,
						ContentView: ModInformationContentView.Source,
						NodeId: source.Id),
					cancellationToken).ConfigureAwait(false);
				if (indexResult.Data is null)
				{
					issues.AddRange(indexResult.State.Diagnostics.Select(diagnostic =>
						new CoreIssue(CoreIssueSeverity.Warning, diagnostic.Code, diagnostic.Message, patchPath, source.Id)));
					continue;
				}

				var entries = indexResult.Data.Entries;
				var byKey = entries.ToDictionary(entry => entry.AssetKey);
				var patchCandidates = primaryCandidates
					.Where(candidate => string.Equals(candidate.SourcePatchFileName, patchFileName, StringComparison.OrdinalIgnoreCase))
					.ToArray();
				if (patchCandidates.Length == 0) continue;

				var unitCache = new Dictionary<AdaptationAssetKey, AdaptationPatchUnitMesh>();
				async ValueTask<AdaptationPatchUnitMesh> ReadUnitAsync(AdaptationAssetKey key)
				{
					if (unitCache.TryGetValue(key, out var cached)) return cached;
					if (!byKey.TryGetValue(key, out var entry))
						throw new InvalidDataException($"Patch 中不存在 Unit 0x{key.FileId:x16}。");
					var read = await informationReader.ReadUnitAsync(
						entry,
						entries,
						PatchUnitDependencyPolicy.RequirePatchLocalComposite,
						new ModInformationReadRequest(
							patchPath,
							context,
							ContentView: ModInformationContentView.Source,
							NodeId: source.Id),
						canonicalSource: false,
						cancellationToken).ConfigureAwait(false);
					if (read.Data is null)
						throw new InvalidDataException($"无法读取 Unit 0x{key.FileId:x16}。");
					unitCache[key] = read.Data;
					return read.Data;
				}

				var armorPlans = new List<ArmorAppendPlan>();
				var plannedArmorTargets = new HashSet<(AdaptationAssetKey ArmorUnitKey, int MeshInfoIndex, AdaptationAssetKey HelmetUnitKey)>();
				var helmetPlans = new Dictionary<AdaptationAssetKey, HelmetHidePlan>();
				foreach (var candidate in patchCandidates)
				{
					var helmetKey = ToAdaptation(candidate.HelmetPart.UnitAssetKey);
					if (!byKey.ContainsKey(helmetKey))
					{
						issues.Add(new CoreIssue(CoreIssueSeverity.Warning, "HelmetUnitMissing", $"来源 Patch 缺少头盔 Unit 0x{helmetKey.FileId:x16}。", patchPath, source.Id));
						continue;
					}
					var helmetUnit = await ReadUnitAsync(helmetKey).ConfigureAwait(false);
					AddHelmetPlan(helmetPlans, helmetKey, candidate, helmetUnit);
					foreach (var armorPart in candidate.ArmorParts)
					{
						var armorKey = ToAdaptation(armorPart.UnitAssetKey);
						if (!byKey.ContainsKey(armorKey))
						{
							issues.Add(new CoreIssue(CoreIssueSeverity.Warning, "ArmorUnitMissing", $"来源 Patch 缺少胸部 Unit 0x{armorKey.FileId:x16}。", patchPath, source.Id));
							continue;
						}
						if (plannedArmorTargets.Add((armorKey, armorPart.MeshInfoIndex, helmetKey)))
							armorPlans.Add(new ArmorAppendPlan(armorKey, armorPart.MeshInfoIndex, helmetKey, candidate, helmetUnit));
					}
				}
				// Every candidate remains a source of a helmet Unit that must be hidden
				// in the main output. This is essential when several Armor Archives share
				// one chest Unit but point at different Helmets: a later variant only
				// replaces the chest Unit and must not reveal an alternate helmet.
				if (outputMode == HelmetToArmorMergeOutputMode.SharedArmorVariants)
				{
					foreach (var candidate in selected.Values.Where(candidate =>
						string.Equals(candidate.SourcePatchFileName, patchFileName, StringComparison.OrdinalIgnoreCase)))
					{
						var helmetKey = ToAdaptation(candidate.HelmetPart.UnitAssetKey);
						if (!byKey.ContainsKey(helmetKey)) continue;
						var helmetUnit = await ReadUnitAsync(helmetKey).ConfigureAwait(false);
						AddHelmetPlan(helmetPlans, helmetKey, candidate, helmetUnit);
					}
				}

				var edits = new List<PatchUnitMeshEditResult>();
				// One physical Unit must yield at most one Patch edit. A source Armor can
				// legitimately expose multiple torso MeshInfo records (for example
				// Slim/Stocky on a shared Unit), so aggregate every target Mesh family
				// before calling the Canonical appender once.
				foreach (var unitGroup in armorPlans.GroupBy(plan => plan.ArmorUnitKey))
				{
					cancellationToken.ThrowIfCancellationRequested();
					var target = await ReadUnitAsync(unitGroup.Key).ConfigureAwait(false);
					var inputsByTargetMesh = new Dictionary<int, List<CanonicalDecorationAppendInput>>();
					foreach (var targetGroup in unitGroup.GroupBy(plan => plan.ArmorMeshInfoIndex))
					{
						var targetRaw = target.Model.RawMeshData.SingleOrDefault(raw => raw.MeshInfoIndex == targetGroup.Key);
						var targetMesh = target.Model.Meshes.SingleOrDefault(mesh => mesh.Index == targetGroup.Key);
						if (targetRaw is null || targetMesh is null)
						{
							issues.Add(new CoreIssue(CoreIssueSeverity.Warning, "ArmorMeshMissing", $"胸部 Unit 0x{target.Entry.AssetKey.FileId:x16} 缺少可写 MeshInfo {targetGroup.Key}。", patchPath, source.Id));
							continue;
						}

						foreach (var lodTarget in ResolveVisibleLodFamily(target.Model, targetRaw, targetMesh))
						{
							if (!inputsByTargetMesh.TryGetValue(lodTarget.Mesh.Index, out var inputs))
								inputsByTargetMesh[lodTarget.Mesh.Index] = inputs = [];
							foreach (var plan in targetGroup)
							{
								var sourceMesh = ResolveSourceLod(plan.HelmetUnit.Model, plan.Candidate.HelmetPart.MeshInfoIndex, lodTarget.Raw.LodIndex);
								if (sourceMesh is null) continue;
								inputs.Add(new CanonicalDecorationAppendInput(
									CreateFragment(plan.HelmetUnit, sourceMesh.Value.Mesh, sourceMesh.Value.Raw),
									MaterialNamespace(plan.Candidate.CandidateId)));
							}
						}
					}

					if (inputsByTargetMesh.Count == 0)
					{
						issues.Add(new CoreIssue(CoreIssueSeverity.Warning, "HelmetLodPairMissing", $"头盔没有与胸部 Unit 0x{target.Entry.AssetKey.FileId:x16} 对应的可见 LOD。", patchPath, source.Id));
						continue;
					}
					var append = new CanonicalDecorationUnitAppender().TryAppendLodFamily(
						target,
						inputsByTargetMesh.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<CanonicalDecorationAppendInput>)pair.Value),
						avatar);
					if (!append.IsValid || append.Edit is null)
					{
						issues.AddRange(append.Diagnostics.Select(diagnostic =>
							new CoreIssue(CoreIssueSeverity.Warning, diagnostic.Code, diagnostic.Message, patchPath, source.Id)));
						continue;
					}
					edits.Add(append.Edit);
					mergedArmorUnits++;
				}

				foreach (var hide in helmetPlans.Values)
				{
					if (!byKey.TryGetValue(hide.UnitKey, out var sourceEntry)) continue;
					if (edits.Any(edit => edit.Entry.AssetKey == sourceEntry.AssetKey))
					{
						issues.Add(new CoreIssue(CoreIssueSeverity.Warning, "HelmetHideSkippedArmorEdit", $"头盔 Unit 0x{hide.UnitKey.FileId:x16} 同时被选为胸部输出，已跳过隐藏以避免覆盖该 Unit 编辑。", patchPath, source.Id));
						continue;
					}
					try
					{
						var targetHelmet = await targetReader.ReadAsync(
							hide.ArchiveId,
							ToAdaptation(hide.Candidate.HelmetPart.UnitAssetKey),
							allowGlobalDependencySearch: true,
							cancellationToken: cancellationToken).ConfigureAwait(false);
						var hidden = new CanonicalHiddenUnitBuilder().Build(targetHelmet, avatar);
						edits.Add(new PatchUnitMeshEditResult(
							sourceEntry,
							hide.SourceUnit.Payload,
							hidden.Entry.EffectiveTocData,
							hidden.Entry.EffectiveGpuData));
						hiddenHelmetUnits++;
					}
					catch (Exception exception) when (exception is IOException or InvalidDataException or EndOfStreamException or UnauthorizedAccessException or InvalidOperationException)
					{
						issues.Add(new CoreIssue(CoreIssueSeverity.Warning, "HelmetHideFailed", $"头盔 Unit 0x{hide.UnitKey.FileId:x16} 无法生成隐藏版：{exception.Message}", patchPath, source.Id, exception.ToString()));
					}
				}

				if (edits.Count != 0)
				{
					await new PatchArchiveWriter().WriteAsync(
						patchPath,
						outputRoot,
						edits,
						overwriteExisting: true,
						cancellationToken: cancellationToken).ConfigureAwait(false);
					processedPatches++;
				}
				progress?.Report(new HelmetToArmorMergeProgress($"已完成 {patchFileName}", patchIndex + 1, patchTotal));
			}
			if (processedPatches == 0 || mergedArmorUnits == 0)
			{
				issues.Add(new CoreIssue(CoreIssueSeverity.Error, "HelmetArmorMergeNoOutput", "没有成功生成任何胸部合并结果。", outputRoot, source.Id));
				return new HelmetToArmorMergeResult(false, outputRoot, processedPatches, mergedArmorUnits, hiddenHelmetUnits, issues);
			}
			if (outputMode == HelmetToArmorMergeOutputMode.SharedArmorVariants)
			{
				await GenerateSharedArmorVariantsAsync(
					source,
					sourceRoot,
					outputRoot,
					selected.Values,
					primaryCandidates,
					gameDataDirectory,
					cancellationToken,
					issues).ConfigureAwait(false);
			}
			return new HelmetToArmorMergeResult(true, outputRoot, processedPatches, mergedArmorUnits, hiddenHelmetUnits, issues);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception exception) when (exception is IOException or InvalidDataException or EndOfStreamException or UnauthorizedAccessException or InvalidOperationException or KeyNotFoundException)
		{
			issues.Add(new CoreIssue(CoreIssueSeverity.Error, "HelmetArmorMergeFailed", exception.Message, outputRoot, source.Id, exception.ToString()));
			return new HelmetToArmorMergeResult(false, outputRoot, processedPatches, mergedArmorUnits, hiddenHelmetUnits, issues);
		}
		finally
		{
			informationReader.ClearOperation(operationId);
		}
	}

	private async Task GenerateSharedArmorVariantsAsync(
		ModNode source,
		string sourceRoot,
		string outputRoot,
		IEnumerable<HelmetToArmorMergeCandidate> selected,
		IReadOnlyList<HelmetToArmorMergeCandidate> primary,
		string gameDataDirectory,
		CancellationToken cancellationToken,
		ICollection<CoreIssue> issues)
	{
		var primaryByTarget = primary.ToDictionary(TargetKey, StringComparer.OrdinalIgnoreCase);
		var alternatives = selected
			.Where(candidate => primaryByTarget.TryGetValue(TargetKey(candidate), out var first)
				&& !string.Equals(first.CandidateId, candidate.CandidateId, StringComparison.OrdinalIgnoreCase))
			.GroupBy(candidate => candidate.CandidateId, StringComparer.OrdinalIgnoreCase)
			.Select(group => group.First())
			.ToArray();
		if (alternatives.Length == 0) return;

		var manifestOptions = new List<object>();
		var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var candidate in alternatives)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var safeName = SanitizeDirectoryName($"变体：{candidate.DisplayName} 使用头盔 0x{candidate.HelmetArchiveId}");
			var variantName = safeName;
			var suffix = 2;
			while (!usedNames.Add(variantName)) variantName = $"{safeName}_{suffix++}";
			var variantRoot = Path.Combine(outputRoot, variantName);
			var temporaryRoot = Path.Combine(Path.GetTempPath(), "HD2ModManager-HelmetVariant-" + Guid.NewGuid().ToString("N"));
			try
			{
				var temporaryResult = await ExecuteAsync(
					source,
					sourceRoot,
					temporaryRoot,
					gameDataDirectory,
					[candidate],
					cancellationToken,
					progress: null,
					outputMode: HelmetToArmorMergeOutputMode.FirstHelmetOnly).ConfigureAwait(false);
				if (!temporaryResult.IsSuccessful)
				{
					issues.Add(new CoreIssue(CoreIssueSeverity.Warning, "HelmetVariantFailed", $"无法生成差分变体“{variantName}”。", outputRoot, source.Id));
					continue;
				}
				var sourcePatch = Path.Combine(temporaryRoot, candidate.SourcePatchFileName);
				var selectedKeys = candidate.ArmorUnitKeys
					.Select(key => new AdaptationAssetKey(key.TypeId, key.FileId))
					.ToArray();
				if (!File.Exists(sourcePatch) || selectedKeys.Length == 0)
				{
					issues.Add(new CoreIssue(CoreIssueSeverity.Warning, "HelmetVariantPatchMissing", $"差分变体“{variantName}”缺少可提取的胸甲 Unit。", outputRoot, source.Id));
					continue;
				}
				Directory.CreateDirectory(variantRoot);
				await new PatchSubsetWriter().WriteSelectedAsync(
					sourcePatch,
					variantRoot,
					selectedKeys.Select(key => new PatchSubsetSelection(sourcePatch, key)).ToArray(),
					overwriteExisting: true,
					cancellationToken).ConfigureAwait(false);
				manifestOptions.Add(new
				{
					Name = $"{candidate.DisplayName}：使用头盔 {candidate.HelmetArchiveId}",
					Description = $"共享胸甲 {string.Join(", ", candidate.ArmorUnitKeys.Select(key => $"0x{key.FileId:x16}"))} 的差分头盔方案。",
					Include = new[] { variantName },
				});
			}
			catch (Exception exception) when (exception is IOException or InvalidDataException or EndOfStreamException or UnauthorizedAccessException or InvalidOperationException)
			{
				issues.Add(new CoreIssue(CoreIssueSeverity.Warning, "HelmetVariantFailed", $"差分变体“{variantName}”生成失败：{exception.Message}", outputRoot, source.Id, exception.ToString()));
			}
			finally
			{
				if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, recursive: true);
			}
		}

		if (manifestOptions.Count > 0)
		{
			var manifest = new
			{
				Version = 1,
				Name = source.Metadata.Name,
				Description = "头盔合并到盔甲输出；主 Patch 为首个头盔方案，子目录为共享胸甲差分变体。",
				Options = manifestOptions,
			};
			await File.WriteAllTextAsync(
				Path.Combine(outputRoot, "manifest.json"),
				JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }),
				cancellationToken).ConfigureAwait(false);
		}
	}

	private static string TargetKey(HelmetToArmorMergeCandidate candidate)
		=> candidate.SourcePatchFileName + "\u001f" + string.Join("|", candidate.ArmorParts
			.Select(part => $"{part.UnitAssetKey.TypeId:x16}:{part.UnitAssetKey.FileId:x16}:{part.MeshInfoIndex}")
			.OrderBy(value => value, StringComparer.OrdinalIgnoreCase));

	private static string SanitizeDirectoryName(string value)
	{
		var invalid = Path.GetInvalidFileNameChars();
		var safe = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
		return string.IsNullOrWhiteSpace(safe) ? "头盔合并变体" : safe;
	}

	private static IReadOnlyList<HelmetToArmorMergeCandidate> BuildCandidates(ModEquipmentSourceFacts facts)
	{
		var candidates = new List<HelmetToArmorMergeCandidate>();
		foreach (var group in facts.PreparedEntries)
		{
			var patchFileName = Path.GetFileName(group.Key);
			var unitKeys = group.Value
				.Where(entry => entry.AssetKey.TypeId == PatchUnitMeshReader.UnitTypeId)
				.Select(entry => new CoreAssetKey(entry.AssetKey.TypeId, entry.AssetKey.FileId))
				.ToHashSet();
			if (unitKeys.Count == 0) continue;

			var sourceEntries = facts.SourceCandidates
				.Select(entry => entry with { Parts = entry.Parts.Where(part => unitKeys.Contains(part.UnitAssetKey)).ToArray() })
				.Where(entry => entry.Parts.Count != 0)
				.ToArray();
			var armors = sourceEntries.Where(entry => string.Equals(entry.Category, "Armor", StringComparison.OrdinalIgnoreCase));
			var helmets = sourceEntries.Where(entry => string.Equals(entry.Category, "Helmet", StringComparison.OrdinalIgnoreCase));
			foreach (var armor in armors)
			{
				var armorParts = SelectTorsoParts(armor.Parts);
				if (armorParts.Count == 0) continue;
				foreach (var helmet in helmets.Where(helmet => string.Equals(helmet.DisplayName, armor.DisplayName, StringComparison.OrdinalIgnoreCase)))
				{
					var helmetPart = SelectHelmetPart(helmet.Parts);
					if (helmetPart is null) continue;
					var candidateId = BuildCandidateId(patchFileName, armor.ArchiveId, helmet.ArchiveId);
					if (candidates.Any(candidate => string.Equals(candidate.CandidateId, candidateId, StringComparison.OrdinalIgnoreCase))) continue;
					candidates.Add(new HelmetToArmorMergeCandidate(
						candidateId,
						patchFileName,
						armor.ArchiveId,
						helmet.ArchiveId,
						armor.DisplayName,
						armorParts,
						helmetPart));
				}
			}
		}
		return candidates
			.OrderBy(candidate => candidate.DisplayName, StringComparer.OrdinalIgnoreCase)
			.ThenBy(candidate => candidate.SourcePatchFileName, StringComparer.OrdinalIgnoreCase)
			.ThenBy(candidate => candidate.ArmorArchiveId, StringComparer.OrdinalIgnoreCase)
			.ToArray();
	}

	private static IReadOnlyList<CoreIssue> ValidateSelectedCandidates(
		IEnumerable<HelmetToArmorMergeCandidate> candidates,
		HelmetToArmorMergeOutputMode outputMode)
	{
		var issues = new List<CoreIssue>();
		var conflicts = candidates
			.SelectMany(candidate => candidate.ArmorParts.Select(part => new
			{
				Candidate = candidate,
				Patch = candidate.SourcePatchFileName,
				Unit = part.UnitAssetKey,
				MeshInfoIndex = part.MeshInfoIndex
			}))
			.GroupBy(item => (item.Patch, item.Unit, item.MeshInfoIndex))
			.Where(group => group
				.Select(item => item.Candidate.HelmetPart.UnitAssetKey)
				.Distinct()
				.Count() > 1);

		foreach (var conflict in conflicts)
		{
			issues.Add(new CoreIssue(
				CoreIssueSeverity.Warning,
				outputMode == HelmetToArmorMergeOutputMode.SharedArmorVariants
					? "HelmetArmorSharedTargetVariants"
					: "HelmetArmorSharedTargetFirstHelmet",
				$"胸甲目标 {conflict.Key.Unit.FileId:x16}/MeshInfo {conflict.Key.MeshInfoIndex} 对应多个头盔；将按当前输出策略处理。",
				conflict.Key.Patch));
			if (outputMode == HelmetToArmorMergeOutputMode.SharedArmorVariants)
				continue;
		}

		return issues;
	}

	private static IReadOnlyList<HelmetToArmorMergeCandidate> SelectPrimaryCandidates(
		IEnumerable<HelmetToArmorMergeCandidate> candidates)
	{
		var all = candidates.ToArray();
		return all
			.GroupBy(candidate => candidate.SourcePatchFileName + "\u001f" + string.Join(
				"|",
				candidate.ArmorParts
					.Select(part => $"{part.UnitAssetKey.TypeId:x16}:{part.UnitAssetKey.FileId:x16}:{part.MeshInfoIndex}")
					.OrderBy(value => value, StringComparer.OrdinalIgnoreCase)), StringComparer.OrdinalIgnoreCase)
			.Select(group => group.First())
			.ToArray();
	}

	private static IReadOnlyList<EquipmentUnitPart> SelectTorsoParts(IReadOnlyList<EquipmentUnitPart> parts)
	{
		var torso = parts
			.Where(part => part.PartKind == UnitMeshPartKind.Torso && !part.IsCullingMesh && part.HasRenderableGeometry)
			.GroupBy(part => (part.UnitAssetKey, part.BodyVariant))
			.Select(group => group
				.OrderByDescending(part => LayerRank(part.Layer))
				.ThenByDescending(part => part.StoredBytes)
				.ThenByDescending(part => part.Confidence)
				.ThenBy(part => part.MeshInfoIndex)
				.First())
			.ToArray();
		var any = torso.Where(part => part.BodyVariant == UnitMeshBodyVariant.Any).ToArray();
		if (any.Length != 0) return [any.OrderByDescending(part => part.StoredBytes).First()];
		return torso
			.Where(part => part.BodyVariant is UnitMeshBodyVariant.Slim or UnitMeshBodyVariant.Stocky)
			.GroupBy(part => part.BodyVariant)
			.Select(group => group.OrderByDescending(part => part.StoredBytes).First())
			.OrderBy(part => part.BodyVariant)
			.ToArray();
	}

	private static EquipmentUnitPart? SelectHelmetPart(IReadOnlyList<EquipmentUnitPart> parts)
		=> parts
			.Where(part => part.PartKind == UnitMeshPartKind.Head
				&& part.BodyVariant == UnitMeshBodyVariant.Any
				&& !part.IsCullingMesh
				&& part.HasRenderableGeometry)
			.OrderByDescending(part => LayerRank(part.Layer))
			.ThenByDescending(part => part.StoredBytes)
			.ThenByDescending(part => part.Confidence)
			.ThenBy(part => part.MeshInfoIndex)
			.FirstOrDefault();

	private static int LayerRank(UnitMeshPartLayer layer) => layer switch
	{
		UnitMeshPartLayer.Armor => 3,
		UnitMeshPartLayer.Undergarment => 2,
		UnitMeshPartLayer.Accessory => 1,
		_ => 0
	};

	private static string BuildCandidateId(string patchFileName, string armorArchiveId, string helmetArchiveId)
		=> $"{patchFileName}|{armorArchiveId}|{helmetArchiveId}";

	private static IEnumerable<string> EnumerateBasePatches(string directory)
	{
		var parser = new PatchFileNameParser();
		return Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
			.Where(path => parser.TryParse(Path.GetFileName(path), out var info) && info?.SidecarKind == PatchSidecarKind.Base)
			.OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
	}

	private static AdaptationAssetKey ToAdaptation(CoreAssetKey key) => new(key.TypeId, key.FileId);

	private static CanonicalDecorationFragment CreateFragment(AdaptationPatchUnitMesh unit, UnitMeshInfo mesh, UnitRawMeshData raw)
	{
		var stream = unit.Model.Streams.SingleOrDefault(item => item.Index == mesh.StreamIndex)
			?? throw new InvalidDataException($"来源头盔 MeshInfo {mesh.Index} 没有对应 Stream。");
		return new CanonicalDecorationFragment(
			mesh,
			raw,
			stream,
			unit.Model.Materials.ToArray(),
			unit.Model.BoneInfos.ToArray(),
			unit.Model.TransformInfo,
			unit.Model.TransformNameHashes.ToArray());
	}

	private static (UnitRawMeshData Raw, UnitMeshInfo Mesh)? ResolveSourceLod(UnitMeshModel model, int anchorMeshInfoIndex, int lodIndex)
	{
		var anchor = model.Meshes.SingleOrDefault(mesh => mesh.Index == anchorMeshInfoIndex);
		if (anchor is null) return null;
		var match = model.RawMeshData
			.Select(raw => (Raw: raw, Mesh: model.Meshes.SingleOrDefault(mesh => mesh.Index == raw.MeshInfoIndex)))
			.Where(item => item.Mesh is not null
				&& item.Raw.LodIndex == lodIndex
				&& item.Mesh!.SemanticInfo.IsVisualMesh
				&& SameLodFamily(anchor.SemanticInfo, item.Mesh.SemanticInfo))
			.OrderBy(item => item.Raw.MeshInfoIndex)
			.FirstOrDefault();
		return match.Mesh is null ? null : (match.Raw, match.Mesh);
	}

	private static IReadOnlyList<(UnitRawMeshData Raw, UnitMeshInfo Mesh)> ResolveVisibleLodFamily(UnitMeshModel model, UnitRawMeshData anchorRaw, UnitMeshInfo anchorMesh)
		=> model.RawMeshData
			.Select(raw => (Raw: raw, Mesh: model.Meshes.SingleOrDefault(mesh => mesh.Index == raw.MeshInfoIndex)))
			.Where(item => item.Mesh is not null
				&& item.Raw.LodIndex >= 0
				&& item.Mesh!.SemanticInfo.IsVisualMesh
				&& SameLodFamily(anchorMesh.SemanticInfo, item.Mesh.SemanticInfo))
			.OrderBy(item => item.Raw.LodIndex)
			.ThenBy(item => item.Mesh!.Index)
			.Select(item => (item.Raw, item.Mesh!))
			.ToArray();

	private static bool SameLodFamily(UnitMeshSemanticInfo anchor, UnitMeshSemanticInfo candidate)
	{
		if (!anchor.HasValue || !candidate.HasValue) return true;
		return string.Equals(anchor.Slot, candidate.Slot, StringComparison.OrdinalIgnoreCase)
			&& string.Equals(anchor.PieceType, candidate.PieceType, StringComparison.OrdinalIgnoreCase)
			&& string.Equals(anchor.BodyType, candidate.BodyType, StringComparison.OrdinalIgnoreCase)
			&& string.Equals(anchor.Weight, candidate.Weight, StringComparison.OrdinalIgnoreCase);
	}

	private static ulong MaterialNamespace(string candidateId)
	{
		var hash = SHA256.HashData(Encoding.UTF8.GetBytes(candidateId));
		return BitConverter.ToUInt64(hash, 0);
	}

	private static void AddHelmetPlan(
		IDictionary<AdaptationAssetKey, HelmetHidePlan> plans,
		AdaptationAssetKey key,
		HelmetToArmorMergeCandidate candidate,
		AdaptationPatchUnitMesh sourceUnit)
	{
		if (!plans.ContainsKey(key))
			plans.Add(key, new HelmetHidePlan(key, candidate.HelmetArchiveId, candidate, sourceUnit));
	}

	private static HelmetToArmorMergeResult Failure(string outputDirectory, string code, string message, string path)
		=> new(false, outputDirectory, 0, 0, 0, [new CoreIssue(CoreIssueSeverity.Error, code, message, path)]);

	private sealed record ArmorAppendPlan(
		AdaptationAssetKey ArmorUnitKey,
		int ArmorMeshInfoIndex,
		AdaptationAssetKey HelmetUnitKey,
		HelmetToArmorMergeCandidate Candidate,
		AdaptationPatchUnitMesh HelmetUnit);

	private sealed record HelmetHidePlan(
		AdaptationAssetKey UnitKey,
		string ArchiveId,
		HelmetToArmorMergeCandidate Candidate,
		AdaptationPatchUnitMesh SourceUnit);
}
