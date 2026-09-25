using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using HD2ModAdaptation.Analysis;
using HD2ModAdaptation.PatchReconstruction;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModCore.Application;
using HD2ModCore.Domain;
using AssetKey = HD2ModCore.Domain.AssetKey;

namespace HD2ModCore.Infrastructure;

public sealed record CustomModelConversionPlan(RuntimeArmorRegistryModel Model, CrossArmorTransferPlan Transfer);
public sealed record CustomModelConversionResult(RuntimeArmorRegistryModel Model, string OutputDirectory, string DefinitionPath);

/// <summary>Converts visible source Units through the existing direct-reuse writer, never editing the source Mod.</summary>
public sealed class CustomModelConversionService(StoragePaths paths, IModInformationReader reader)
{
	public async Task<IReadOnlyList<EquipmentUnitCatalogEntry>> InspectAsync(ModEquipmentSourceFacts facts, CancellationToken token = default)
	{
		var keys = facts.PreparedEntries.Values.SelectMany(entries => entries)
			.Where(entry => entry.AssetKey.TypeId == PatchUnitMeshReader.UnitTypeId)
			.Select(entry => new AssetKey(entry.AssetKey.TypeId, entry.AssetKey.FileId)).ToHashSet();
		var ownership = await CoreServices.CreateAssetArchiveIndexService(paths).FindAssetArchivesAsync(keys, token);
		return SelectSources(facts.SourceCandidates, ownership);
	}

	public static IReadOnlyList<EquipmentUnitCatalogEntry> SelectSources(
		IReadOnlyList<EquipmentUnitCatalogEntry> candidates, IReadOnlyList<AssetArchiveMatch> ownership)
	{
		// Shared Units alone cannot establish that a Mod replaces an archive.
		var exclusive = ownership.Where(match => match.Archives.Select(a => a.ArchiveId).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1)
			.Select(match => match.Archives[0].ArchiveId).ToHashSet(StringComparer.OrdinalIgnoreCase);
		return candidates.Where(candidate => exclusive.Contains(candidate.ArchiveId)
			&& (candidate.Category.Equals("Armor", StringComparison.OrdinalIgnoreCase)
				|| candidate.Category.Equals("Helmet", StringComparison.OrdinalIgnoreCase)))
			.OrderBy(candidate => candidate.DisplayName).ToArray();
	}

	public static CustomModelConversionPlan BuildPlan(EquipmentUnitCatalogEntry source, string name, string key,
		Func<string> nextId)
		=> BuildPlan([source], name, key, nextId);

	public static CustomModelConversionPlan BuildPlan(IReadOnlyList<EquipmentUnitCatalogEntry> sources, string name, string key,
		Func<string> nextId)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name);
		ValidateSources(sources);
		var pieces = new List<RuntimeArmorRegistryModelPiece>();
		var mappings = new List<CrossArmorTransferMapping>();
		var targets = new List<EquipmentUnitPart>();
		var layers = new Dictionary<(uint Slot, uint Body), uint>();
		var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var original in sources.SelectMany(source => source.Parts).Where(p => !p.IsCullingMesh)
			.GroupBy(p => p.UnitAssetKey).Select(group => group.OrderByDescending(p => p.Confidence).First())
			.OrderByDescending(p => p.StoredBytes).ThenBy(p => p.UnitAssetKey.FileId))
		{
			var slot = SlotFor(original);
			var body = original.BodyVariant switch
			{
				UnitMeshBodyVariant.Stocky => 0u, UnitMeshBodyVariant.Slim => 1u,
				UnitMeshBodyVariant.Any => 3u,
				_ => throw new InvalidDataException($"Unit {original.UnitAssetKey.FileId:x16} 身形未知，不能安全转换。")
			};
			// Any occupies both columns; it must not overlap a concrete-body row.
			var layer = body == 3 ? Math.Max(layers.GetValueOrDefault((slot, 0u)), layers.GetValueOrDefault((slot, 1u)))
				: layers.GetValueOrDefault((slot, body));
			if (body == 3) { layers[(slot, 0)] = layer + 1; layers[(slot, 1)] = layer + 1; }
			else layers[(slot, body)] = layer + 1;
			var id = nextId();
			if (!usedIds.Add(id)) throw new InvalidDataException("生成了重复的自定义 Unit ID。");
			pieces.Add(new(body, id, slot, layer));
			var part = original with
			{
				UnitAssetKey = new AssetKey(PatchUnitMeshReader.UnitTypeId, ulong.Parse(id, NumberStyles.HexNumber)),
				PieceType = layer.ToString(CultureInfo.InvariantCulture),
				SharedArchiveIds = []
			};
			targets.Add(part);
			mappings.Add(new(new(part.UnitAssetKey), part, original, true, "保留来源 Unit 数据并分配新 ID",
				["custom-model:" + key], [name], true, false));
		}
		if (pieces.Count == 0) throw new InvalidDataException("所选 Archive 没有可转换的可见 Unit。");
		var target = new EquipmentUnitCatalogEntry("custom-model:" + key, "CustomModel", name, targets) { CustomModelKey = key };
		return new(new(key, null, pieces, name), new(sources, sources[0], [target], mappings, [], []));
	}

	private static void ValidateSources(IReadOnlyList<EquipmentUnitCatalogEntry> sources)
	{
		if (sources.Count == 0 || sources.Any(source => !source.Category.Equals("Armor", StringComparison.OrdinalIgnoreCase)
				&& !source.Category.Equals("Helmet", StringComparison.OrdinalIgnoreCase))
			|| sources.GroupBy(source => source.Category, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
			throw new InvalidDataException("请选择一份护甲、一份头盔或两者各一份。");
	}

	private static uint SlotFor(EquipmentUnitPart part) => part.PartKind switch
	{
		UnitMeshPartKind.Head => 1,
		UnitMeshPartKind.Torso => 2,
		UnitMeshPartKind.Pelvis => 3,
		UnitMeshPartKind.LeftLeg => 4,
		UnitMeshPartKind.RightLeg => 5,
		UnitMeshPartKind.LeftArm => 6,
		UnitMeshPartKind.RightArm => 7,
		UnitMeshPartKind.LeftShoulder or UnitMeshPartKind.RightShoulder => 2,
		_ when new[] { "pelvis", "hip", "leg", "thigh", "lower", "skirt" }
			.Any(word => part.SemanticName.Contains(word, StringComparison.OrdinalIgnoreCase)) => 3,
		UnitMeshPartKind.Accessory => 2,
		_ => throw new InvalidDataException($"无法确认 Unit {part.UnitAssetKey.FileId:x16} 的上/下半身归属。")
	};

	public Task<CustomModelConversionResult> GenerateAsync(ModEquipmentSourceFacts facts, string archiveId,
		string name, string outputRoot, string gameData, CancellationToken token = default)
		=> GenerateAsync(facts, [archiveId], name, outputRoot, gameData, token);

	public async Task<CustomModelConversionResult> GenerateAsync(ModEquipmentSourceFacts facts, IReadOnlyList<string> archiveIds,
		string name, string outputRoot, string gameData, CancellationToken token = default,
		IProgress<CrossArmorTransferProgress>? progress = null)
	{
		var candidates = await InspectAsync(facts, token);
		var sources = archiveIds.Select(id => candidates.SingleOrDefault(item => item.ArchiveId.Equals(id, StringComparison.OrdinalIgnoreCase))
			?? throw new InvalidDataException("来源无效：必须存在仅属于该护甲/头盔 Archive 的 Unit。")).ToArray();
		ValidateSources(sources);
		if (facts.SourcePatchTocPaths.Count == 0)
			throw new InvalidDataException("来源 Mod 不包含 Patch。");
		// Use complete Unit payload size, not the original game's mesh size.
		var sizes = facts.PreparedEntries.Values.SelectMany(entries => entries)
			.GroupBy(e => new AssetKey(e.AssetKey.TypeId, e.AssetKey.FileId))
			.ToDictionary(g => g.Key, g => g.Max(e => (long)e.TocDataSize + e.StreamSize + e.GpuResourceSize));
		sources = sources.Select(source => source with { Parts = source.Parts.Select(p => p with { StoredBytes = sizes.GetValueOrDefault(p.UnitAssetKey) }).ToArray() }).ToArray();
		var registry = new RuntimeArmorRegistryService(paths);
		var snapshot = await registry.ReadSnapshotAsync(token);
		var forbidden = snapshot.Document.ModelDefinitions.SelectMany(m => m.Pieces ?? [])
			.Select(p => p.Unit).Concat(facts.PreparedEntries.Values.SelectMany(e => e).Select(e => e.AssetKey.FileId.ToString("x16")))
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		var ids = new Queue<string>();
		// A monotonically numbered block gives larger Units the earlier IDs.
		var seed = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8)) & 0x7fffffffffff0000UL;
		for (ulong i = 1; i <= (ulong)sources.Sum(source => source.Parts.Count); i++)
		{
			var id = (seed + i).ToString("x16");
			if (forbidden.Contains(id)) throw new InvalidDataException("自定义 ID 冲突，请重新生成。");
			ids.Enqueue(id);
		}
		var key = "custom-" + Guid.NewGuid().ToString("N");
		var plan = BuildPlan(sources, name.Trim(), key, () => ids.Dequeue());
		var collisions = await CoreServices.CreateAssetArchiveIndexService(paths).FindAssetArchivesAsync(
			plan.Transfer.SelectedTargets.SelectMany(t => t.Parts).Select(p => p.UnitAssetKey).ToHashSet(), token);
		if (collisions.Any(match => match.Found)) throw new InvalidDataException("生成的 Unit ID 与原版资产冲突，请重试。");
		var safeName = string.Concat(name.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
		var output = Path.Combine(Path.GetFullPath(outputRoot), $"{safeName}-{DateTime.Now:yyyyMMdd-HHmmss}-{key[^6..]}");
		var sourceDirectories = facts.SourcePatchTocPaths.Select(p => Path.GetDirectoryName(Path.GetFullPath(p))!).ToArray();
		if (sourceDirectories.Any(dir => output.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
			|| output.StartsWith(Path.GetFullPath(gameData).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
			throw new InvalidDataException("输出位置不能位于来源 Mod 或游戏 data 内。");
		Directory.CreateDirectory(output);
		var stage = Path.Combine(output, ".conversion-source");
		var registered = false;
		try
		{
			progress?.Report(new("staging", "正在整理护甲与头盔来源资源", 0, 0, TimeSpan.Zero));
			// Combine split source patches before handing them to the existing writer.
			var winners = facts.PreparedEntries.Values.SelectMany(e => e)
				.GroupBy(e => e.AssetKey).Select(g => g.OrderBy(e => new PatchFileNameParser().Parse(e.SourceFileName).PatchIndex).Last()).ToArray();
			var combined = await new PatchSubsetWriter().WriteSelectedAsync(facts.SourcePatchTocPaths[0], stage,
				winners.Select(e => new PatchSubsetSelection(e.SourceFilePath, e.AssetKey)).ToArray(), cancellationToken: token);
			// Keep the loadable carrier separate from the selected armor identity.
			var stagedPath = Path.Combine(stage, CanonicalCrossArmorOrchestrator.ArmorOutputPatchFileName);
			if (!string.Equals(combined.TocFilePath, stagedPath, StringComparison.OrdinalIgnoreCase))
			{
				foreach (var suffix in new[] { "", ".gpu_resources", ".stream" })
					if (File.Exists(combined.TocFilePath + suffix)) File.Move(combined.TocFilePath + suffix, stagedPath + suffix);
			}
			var result = await CoreServices.CreateCanonicalCrossArmorOrchestrator(paths, reader).ExecuteAsync(
				new(stagedPath, gameData, output, plan.Transfer, Progress: progress, AutoHideUnmappedTargetUnits: false, ExpandLodFamilyMappings: false), token);
			if (!result.IsSuccessful) throw new InvalidDataException(string.Join("；", result.Issues.Select(i => i.Message)));
			var definition = Path.Combine(output, "custom-model.json");
			progress?.Report(new("registering", "正在保存自定义素体定义", 0, 0, TimeSpan.Zero));
			await File.WriteAllTextAsync(definition, JsonSerializer.Serialize(plan.Model, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }), token);
			// Re-read after generation, preserving edits made while the writer was running.
			var latest = await registry.ReadSnapshotAsync(token);
			await registry.SaveWorkspaceAsync(latest.Document with { ModelDefinitions = latest.Document.ModelDefinitions.Append(plan.Model).ToArray() }, token);
			registered = true;
			return new(plan.Model, output, definition);
		}
		finally
		{
			// This fixed child contains only our staging copy, never source data.
			var resolvedStage = Path.GetFullPath(stage);
			if (!string.Equals(Path.GetDirectoryName(resolvedStage), Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase))
				throw new InvalidOperationException("临时输出目录边界校验失败。");
			try { if (Directory.Exists(resolvedStage)) Directory.Delete(resolvedStage, recursive: true); }
			catch (IOException) { /* A failed cleanup must not overwrite the generation result. */ }
			if (!registered)
				await File.WriteAllTextAsync(Path.Combine(output, "conversion-incomplete.txt"),
					"生成或素体登记未完成。请勿安装此输出。", CancellationToken.None);
		}
	}
}
