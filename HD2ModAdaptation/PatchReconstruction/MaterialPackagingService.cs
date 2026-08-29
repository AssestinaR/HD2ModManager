using System.Security.Cryptography;
using HD2ModAdaptation.Analysis;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;

namespace HD2ModAdaptation.PatchReconstruction;

// Purpose: Plans and writes payload-preserving material splits and explicit material-winner packages.
public sealed class MaterialPackagingService
{
	private readonly IPatchTocScanner scanner;
	private readonly IPatchEntryPayloadReader payloadReader;
	private readonly IPatchGroupAnalyzer analyzer;
	private readonly PatchSubsetWriter subsetWriter;

	public MaterialPackagingService(
		IPatchTocScanner? scanner = null,
		IPatchEntryPayloadReader? payloadReader = null,
		IPatchGroupAnalyzer? analyzer = null,
		PatchSubsetWriter? subsetWriter = null)
	{
		this.scanner = scanner ?? new PatchTocScanner();
		this.payloadReader = payloadReader ?? new PatchEntryPayloadReader();
		this.analyzer = analyzer ?? new PatchGroupAnalyzer();
		this.subsetWriter = subsetWriter ?? new PatchSubsetWriter(this.scanner, this.payloadReader);
	}

	public async ValueTask<MaterialPackagingInspection> InspectAsync(string patchTocPath, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(patchTocPath);
		var path = Path.GetFullPath(patchTocPath);
		var analysis = await analyzer.AnalyzeAsync(new PatchGroupInput(path), cancellationToken).ConfigureAwait(false);
		var entries = await scanner.ScanEntriesAsync(path, cancellationToken).ConfigureAwait(false);
		var keys = entries.Select(entry => entry.AssetKey).ToHashSet();
		var units = keys.Where(key => key.TypeId == PatchUnitMeshReader.UnitTypeId).ToHashSet();
		var materials = keys.Where(key => key.TypeId == MaterialDependencyResolver.MaterialTypeId).ToHashSet();
		var textures = keys.Where(key => key.TypeId == MaterialDependencyResolver.TextureTypeId).ToHashSet();
		var requiredMaterials = analysis.References.Where(reference => reference.Kind == PatchReferenceKind.UnitMaterial).Select(reference => reference.TargetAssetKey).ToHashSet();
		var embeddedMaterials = requiredMaterials.Where(materials.Contains).ToHashSet();
		var externalMaterials = requiredMaterials.Where(key => !materials.Contains(key)).ToHashSet();
		var materialReferences = analysis.References.Where(reference => reference.Kind == PatchReferenceKind.MaterialTexture).ToArray();
		var embeddedClosureTextures = materialReferences.Where(reference => embeddedMaterials.Contains(reference.SourceAssetKey) && textures.Contains(reference.TargetAssetKey)).Select(reference => reference.TargetAssetKey).ToHashSet();
		var missingClosureTextures = materialReferences.Where(reference => embeddedMaterials.Contains(reference.SourceAssetKey) && !textures.Contains(reference.TargetAssetKey)).Select(reference => reference.TargetAssetKey).ToHashSet();
		return new MaterialPackagingInspection(path, entries, analysis.References, analysis.Issues, units, materials, textures, requiredMaterials, embeddedMaterials, externalMaterials, embeddedClosureTextures, missingClosureTextures);
	}

	public async ValueTask<MaterialSplitPlan> PlanSplitAsync(string patchTocPath, CancellationToken cancellationToken = default)
	{
		var inspection = await InspectAsync(patchTocPath, cancellationToken).ConfigureAwait(false);
		return PlanSplit(inspection);
	}

	public MaterialSplitPlan PlanSplit(MaterialPackagingInspection inspection)
	{
		ArgumentNullException.ThrowIfNull(inspection);
		var notices = new List<string>();
		if (inspection.UnitAssetKeys.Count == 0) notices.Add("未发现 Unit；仍会把当前 Patch 的 Material 与可读取 Texture 拆为独立材质包。");
		if (inspection.Issues.Count != 0) notices.Add("Patch 引用解析存在问题；输出会保留原始 Payload，但请在游戏或 Blender 中复核。");
		if (inspection.MissingEmbeddedTextureAssetKeys.Count != 0) notices.Add($"材质闭包引用了 {inspection.MissingEmbeddedTextureAssetKeys.Count} 个当前 Patch 未提供的 Texture；拆分结果会保留该外部引用。");
		var unknown = inspection.Entries.Where(entry => entry.AssetKey.TypeId is not PatchUnitMeshReader.UnitTypeId and not PatchUnitMeshReader.CompositeUnitTypeId and not MaterialDependencyResolver.MaterialTypeId and not MaterialDependencyResolver.TextureTypeId).ToArray();
		if (unknown.Length != 0) notices.Add($"存在 {unknown.Length} 个未支持类型资源；它们会留在模型包，无法确认是否间接引用材质包资源。");
		var materialKeys = inspection.MaterialAssetKeys.ToHashSet();
		var textureKeys = inspection.References
			.Where(reference => reference.Kind == PatchReferenceKind.MaterialTexture && materialKeys.Contains(reference.SourceAssetKey) && inspection.TextureAssetKeys.Contains(reference.TargetAssetKey))
			.Select(reference => reference.TargetAssetKey)
			.ToHashSet();
		var moved = materialKeys.Concat(textureKeys).ToHashSet();
		var model = inspection.Entries.Select(entry => entry.AssetKey).Where(key => !moved.Contains(key)).ToHashSet();
		if (materialKeys.Count == 0) notices.Add("当前 Patch 不含 Material，无法生成材质包。");
		return new MaterialSplitPlan(inspection, materialKeys.Count != 0, model, moved, notices);
	}

	public async ValueTask<MaterialCandidateCompatibility> CheckCandidateAsync(string sourcePatchTocPath, string candidatePatchTocPath, bool requireAllExternalMaterials, CancellationToken cancellationToken = default)
	{
		var source = await InspectAsync(sourcePatchTocPath, cancellationToken).ConfigureAwait(false);
		var candidate = await InspectAsync(candidatePatchTocPath, cancellationToken).ConfigureAwait(false);
		var matching = source.RequiredMaterialAssetKeys.Intersect(candidate.MaterialAssetKeys).ToHashSet();
		var candidateReferences = candidate.References.Where(reference => reference.Kind == PatchReferenceKind.MaterialTexture && matching.Contains(reference.SourceAssetKey)).ToArray();
		var missingTextures = candidateReferences.Select(reference => reference.TargetAssetKey).Where(key => !candidate.TextureAssetKeys.Contains(key) && !source.TextureAssetKeys.Contains(key)).ToHashSet();
		var missingExternalMaterials = requireAllExternalMaterials ? source.ExternalMaterialAssetKeys.Except(candidate.MaterialAssetKeys).ToHashSet() : new HashSet<AssetKey>();
		var notices = new List<string>();
		if (missingTextures.Count != 0) notices.Add($"候选材质闭包引用了 {missingTextures.Count} 个未由源或候选 Patch 提供的 Texture；会保留外部引用。");
		if (missingExternalMaterials.Count != 0) notices.Add($"候选材质包未覆盖 {missingExternalMaterials.Count} 个外部 Material；生成结果会保留这些外部引用。");
		if (candidate.Issues.Count != 0) notices.Add("候选 Patch 引用解析存在问题；输出会保留原始 Payload，请在游戏或 Blender 中复核。");
		if (matching.Count == 0) notices.Add("候选 Mod 没有提供源 Unit 所引用的同 AssetKey Material。");
		return new MaterialCandidateCompatibility(source, candidate, matching, missingExternalMaterials, missingTextures, matching.Count != 0, notices);
	}

	public async ValueTask<MaterialPackagingWriteResult> SplitAsync(string sourcePatchTocPath, string modelOutputDirectory, string materialOutputDirectory, CancellationToken cancellationToken = default)
	{
		var plan = await PlanSplitAsync(sourcePatchTocPath, cancellationToken).ConfigureAwait(false);
		if (!plan.IsApproved) throw new InvalidOperationException(string.Join(Environment.NewLine, plan.Blockers));
		var model = await subsetWriter.WriteAsync(sourcePatchTocPath, modelOutputDirectory, plan.ModelAssetKeys, cancellationToken: cancellationToken).ConfigureAwait(false);
		var material = await subsetWriter.WriteAsync(sourcePatchTocPath, materialOutputDirectory, plan.MaterialAssetKeys, cancellationToken: cancellationToken).ConfigureAwait(false);
		var verification = await VerifyEquivalentAsync(sourcePatchTocPath, new[] { model.TocFilePath, material.TocFilePath }, cancellationToken).ConfigureAwait(false);
		return new MaterialPackagingWriteResult(new[] { model, material }, verification);
	}

	public async ValueTask<MaterialPackagingWriteResult> MergeAsync(string sourcePatchTocPath, string candidatePatchTocPath, string outputDirectory, bool requireAllExternalMaterials, bool onlyReferencedAssets = true, bool replaceExistingMaterials = true, CancellationToken cancellationToken = default)
	{
		var compatibility = await CheckCandidateAsync(sourcePatchTocPath, candidatePatchTocPath, requireAllExternalMaterials, cancellationToken).ConfigureAwait(false);
		if (!compatibility.IsCompatible) throw new InvalidOperationException(string.Join(Environment.NewLine, compatibility.Blockers));
		var sourceEntries = compatibility.Source.Entries;
		var candidateMaterials = onlyReferencedAssets
			? compatibility.MatchingMaterialAssetKeys
			: compatibility.Candidate.MaterialAssetKeys;
		var candidateTextures = compatibility.Candidate.References
			.Where(reference => reference.Kind == PatchReferenceKind.MaterialTexture
				&& candidateMaterials.Contains(reference.SourceAssetKey)
				&& compatibility.Candidate.TextureAssetKeys.Contains(reference.TargetAssetKey))
			.Select(reference => reference.TargetAssetKey)
			.ToHashSet();
		var selectedCandidateEntries = compatibility.Candidate.Entries
			.Where(entry => (entry.AssetKey.TypeId == MaterialDependencyResolver.MaterialTypeId && candidateMaterials.Contains(entry.AssetKey))
				|| (entry.AssetKey.TypeId == MaterialDependencyResolver.TextureTypeId && candidateTextures.Contains(entry.AssetKey)))
			.ToArray();
		var winners = sourceEntries.ToDictionary(entry => entry.AssetKey);
		foreach (var candidateEntry in selectedCandidateEntries)
		{
			if (replaceExistingMaterials || !winners.ContainsKey(candidateEntry.AssetKey))
				winners[candidateEntry.AssetKey] = candidateEntry;
		}
		if (replaceExistingMaterials)
			RemoveSupersededSourceTextureClosures(
				winners,
				compatibility.Source,
				compatibility.Candidate,
				candidateMaterials);
		var winnerEntries = winners.Values.ToArray();
		var write = await subsetWriter.WriteAsync(sourcePatchTocPath, outputDirectory, winnerEntries.Select(entry => new PatchSubsetSelection(entry.SourceFilePath, entry.AssetKey)).ToArray(), cancellationToken: cancellationToken).ConfigureAwait(false);
		var verification = await VerifyEquivalentAsync(winnerEntries, new[] { write.TocFilePath }, cancellationToken).ConfigureAwait(false);
		return new MaterialPackagingWriteResult(new[] { write }, verification);
	}

	private static void RemoveSupersededSourceTextureClosures(
		IDictionary<AssetKey, PatchTocEntry> winners,
		MaterialPackagingInspection source,
		MaterialPackagingInspection candidate,
		IReadOnlySet<AssetKey> selectedCandidateMaterials)
	{
		// The Patch analyzer is the authority for deciding whether a texture is
		// still reachable. If either side was only partially understood, preserve
		// every source payload rather than guessing about a dependency closure.
		if (source.Issues.Count != 0 || candidate.Issues.Count != 0) return;

		var replacedSourceMaterials = selectedCandidateMaterials
			.Where(source.MaterialAssetKeys.Contains)
			.ToHashSet();
		if (replacedSourceMaterials.Count == 0) return;

		var supersededTextures = source.References
			.Where(reference => reference.Kind == PatchReferenceKind.MaterialTexture
				&& replacedSourceMaterials.Contains(reference.SourceAssetKey)
				&& source.TextureAssetKeys.Contains(reference.TargetAssetKey))
			.Select(reference => reference.TargetAssetKey)
			.ToHashSet();
		if (supersededTextures.Count == 0) return;

		var stillReferencedTextures = source.References
			.Where(reference => reference.Kind == PatchReferenceKind.MaterialTexture
				&& source.MaterialAssetKeys.Contains(reference.SourceAssetKey)
				&& !replacedSourceMaterials.Contains(reference.SourceAssetKey))
			.Select(reference => reference.TargetAssetKey)
			.Concat(candidate.References
				.Where(reference => reference.Kind == PatchReferenceKind.MaterialTexture
					&& selectedCandidateMaterials.Contains(reference.SourceAssetKey))
				.Select(reference => reference.TargetAssetKey))
			.ToHashSet();

		foreach (var textureKey in supersededTextures.Except(stillReferencedTextures))
			winners.Remove(textureKey);
	}

	private async ValueTask<MaterialPackagingVerification> VerifyEquivalentAsync(string sourcePatchTocPath, IReadOnlyList<string> outputPatchTocPaths, CancellationToken cancellationToken)
		=> await VerifyEquivalentAsync(await scanner.ScanEntriesAsync(sourcePatchTocPath, cancellationToken).ConfigureAwait(false), outputPatchTocPaths, cancellationToken).ConfigureAwait(false);

	private async ValueTask<MaterialPackagingVerification> VerifyEquivalentAsync(IReadOnlyCollection<PatchTocEntry> expectedEntries, IReadOnlyList<string> outputPatchTocPaths, CancellationToken cancellationToken)
	{
		var failures = new List<string>();
		var expected = expectedEntries.ToDictionary(entry => entry.AssetKey);
		var actualEntries = new List<PatchTocEntry>();
		foreach (var path in outputPatchTocPaths) actualEntries.AddRange(await scanner.ScanEntriesAsync(path, cancellationToken).ConfigureAwait(false));
		if (!expected.Keys.ToHashSet().SetEquals(actualEntries.Select(entry => entry.AssetKey))) failures.Add("输出 AssetKey 集合与批准 winner 集合不一致。");
		if (actualEntries.GroupBy(entry => entry.AssetKey).Any(group => group.Count() != 1)) failures.Add("输出包含重复 AssetKey。");
		foreach (var actual in actualEntries)
		{
			if (!expected.TryGetValue(actual.AssetKey, out var source)) continue;
			var left = await payloadReader.ReadPayloadAsync(source, cancellationToken).ConfigureAwait(false);
			var right = await payloadReader.ReadPayloadAsync(actual, cancellationToken).ConfigureAwait(false);
			if (!Hash(left.TocData).SequenceEqual(Hash(right.TocData)) || !Hash(left.StreamData).SequenceEqual(Hash(right.StreamData)) || !Hash(left.GpuResourceData).SequenceEqual(Hash(right.GpuResourceData))) failures.Add($"资源 0x{actual.AssetKey.TypeId:x16}/0x{actual.AssetKey.FileId:x16} 的 Payload hash 不一致。");
		}
		var expectedGraph = await ReadWinnerGraphAsync(expected.Values, cancellationToken).ConfigureAwait(false);
		var actualGraph = await ReadGraphAsync(outputPatchTocPaths, cancellationToken).ConfigureAwait(false);
		if (!expectedGraph.SetEquals(actualGraph)) failures.Add("输出 Unit → Material → Texture winner 图不等价。");
		return new MaterialPackagingVerification(failures.Count == 0, expected.Count, actualEntries.Count, expectedGraph.Count, actualGraph.Count, failures);
	}

	private async ValueTask<HashSet<string>> ReadWinnerGraphAsync(IEnumerable<PatchTocEntry> entries, CancellationToken cancellationToken)
	{
		var result = new HashSet<string>(StringComparer.Ordinal);
		foreach (var group in entries.GroupBy(entry => entry.SourceFilePath, StringComparer.OrdinalIgnoreCase))
		{
			var selected = group.Select(entry => entry.AssetKey).ToHashSet();
			var analysis = await analyzer.AnalyzeAsync(new PatchGroupInput(group.Key), cancellationToken).ConfigureAwait(false);
			foreach (var edge in analysis.References.Where(reference => selected.Contains(reference.SourceAssetKey))) result.Add(EdgeKey(edge));
		}
		return result;
	}

	private async ValueTask<HashSet<string>> ReadGraphAsync(IEnumerable<string> paths, CancellationToken cancellationToken)
	{
		var result = new HashSet<string>(StringComparer.Ordinal);
		foreach (var path in paths)
		{
			var analysis = await analyzer.AnalyzeAsync(new PatchGroupInput(path), cancellationToken).ConfigureAwait(false);
			foreach (var edge in analysis.References) result.Add(EdgeKey(edge));
		}
		return result;
	}

	private static byte[] Hash(byte[] data) => SHA256.HashData(data);
	private static string EdgeKey(PatchAssetReference edge) => $"{edge.Kind}:{edge.SourceAssetKey.TypeId:x16}/{edge.SourceAssetKey.FileId:x16}>{edge.TargetAssetKey.TypeId:x16}/{edge.TargetAssetKey.FileId:x16}";
}

public sealed record MaterialPackagingInspection(string PatchTocPath, IReadOnlyList<PatchTocEntry> Entries, IReadOnlyList<PatchAssetReference> References, IReadOnlyList<PatchAnalysisIssue> Issues, IReadOnlySet<AssetKey> UnitAssetKeys, IReadOnlySet<AssetKey> MaterialAssetKeys, IReadOnlySet<AssetKey> TextureAssetKeys, IReadOnlySet<AssetKey> RequiredMaterialAssetKeys, IReadOnlySet<AssetKey> EmbeddedMaterialAssetKeys, IReadOnlySet<AssetKey> ExternalMaterialAssetKeys, IReadOnlySet<AssetKey> EmbeddedTextureClosureAssetKeys, IReadOnlySet<AssetKey> MissingEmbeddedTextureAssetKeys);
public sealed record MaterialSplitPlan(MaterialPackagingInspection Inspection, bool IsApproved, IReadOnlySet<AssetKey> ModelAssetKeys, IReadOnlySet<AssetKey> MaterialAssetKeys, IReadOnlyList<string> Blockers);
public sealed record MaterialCandidateCompatibility(MaterialPackagingInspection Source, MaterialPackagingInspection Candidate, IReadOnlySet<AssetKey> MatchingMaterialAssetKeys, IReadOnlySet<AssetKey> MissingMaterialAssetKeys, IReadOnlySet<AssetKey> MissingTextureAssetKeys, bool IsCompatible, IReadOnlyList<string> Blockers);
public sealed record MaterialPackagingVerification(bool IsSuccessful, int ExpectedAssetCount, int ActualAssetCount, int ExpectedGraphEdgeCount, int ActualGraphEdgeCount, IReadOnlyList<string> Failures);
public sealed record MaterialPackagingWriteResult(IReadOnlyList<PatchArchiveFileWriteResult> Outputs, MaterialPackagingVerification Verification);
