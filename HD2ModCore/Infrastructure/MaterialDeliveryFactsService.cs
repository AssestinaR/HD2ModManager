using HD2ModAdaptation.Analysis;
using HD2ModCore.Application;
using HD2ModCore.Domain;
// 本文件（历史原因）没有 namespace 声明，类型落在全局命名空间里，
// 所以要显式引入 Infrastructure 才能看到 AsMappingFacts 这个扩展方法。
using HD2ModCore.Infrastructure;
using AssetKey = HD2ModAdaptation.PatchReconstruction.AssetKey;

// Purpose: Classifies material delivery from persisted Patch graph facts and identifies complete single-Mod providers.
public sealed class MaterialDeliveryFactsService : IMaterialDeliveryFactsService
{
	private const ulong UnitTypeId = 0xe0a48d0be9a7453f;
	private const ulong MaterialTypeId = 0xeac0b497876adedf;
	private const ulong TextureTypeId = 0xcd4238c6a0c69e32;
	private readonly IModInformationCenter informationCenter;
	private readonly string modsRootDirectory;
	private readonly IReferenceGraphQueryIndex? referenceIndex;

	public MaterialDeliveryFactsService(IModInformationCenter informationCenter, HD2ModCore.Infrastructure.StoragePaths paths, IReferenceGraphQueryIndex? referenceIndex = null)
	{
		this.informationCenter = informationCenter ?? throw new ArgumentNullException(nameof(informationCenter));
		modsRootDirectory = (paths ?? throw new ArgumentNullException(nameof(paths))).ModsDirectory;
		this.referenceIndex = referenceIndex;
	}

	public async ValueTask<MaterialDeliveryFacts> GetAsync(ModNodeId nodeId, LibrarySnapshot librarySnapshot, CancellationToken cancellationToken = default, bool includeCandidates = true, bool includeGameDataMapping = true)
	{
		ArgumentNullException.ThrowIfNull(librarySnapshot);
		var source = await LoadReferenceGraphAsync(nodeId, librarySnapshot, cancellationToken).ConfigureAwait(false);
		if (source is null)
		{
			return new MaterialDeliveryFacts(nodeId, MaterialDeliveryMode.Unknown, 0, 0, 0, 0, 0, Array.Empty<MaterialDeliveryCandidate>(), new[] { "当前 Mod 的 ReferenceGraph 信息不可用。" });
		}

		var sourceGraph = BuildGraph(source);
		var mappedOriginalMaterials = !includeGameDataMapping
			? new HashSet<AssetKey>()
			: (await informationCenter.RequestGameDataMappingAsync(
					sourceGraph.Materials.Select(key => new HD2ModCore.Domain.AssetKey(key.TypeId, key.FileId)).ToHashSet(),
					new ModInformationRequest(ModInformationKind.GameDataMapping, "MaterialDeliveryFacts"),
					cancellationToken).ConfigureAwait(false)).AsMappingFacts().Assets
				.Where(pair => pair.Value.TargetArchives.Count != 0)
				.Select(pair => new AssetKey(pair.Key.TypeId, pair.Key.FileId))
				.ToHashSet();
		var isMaterialOnly = sourceGraph.UnitCount == 0 && sourceGraph.Materials.Count != 0;
		var selfMaterialReferences = sourceGraph.References
			.Where(reference => reference.Kind is PatchReferenceKind.UnitMaterial or PatchReferenceKind.MaterialTexture
				&& sourceGraph.Assets.Contains(reference.SourceAssetKey)
				&& sourceGraph.Assets.Contains(reference.TargetAssetKey)
				&& (reference.TargetAssetKey.TypeId == MaterialTypeId || reference.SourceAssetKey.TypeId == MaterialTypeId))
			.Select(reference => new MaterialReferenceFact(reference.SourceAssetKey, reference.TargetAssetKey, reference.Kind))
			.ToArray();
		if (sourceGraph.UnitCount == 0)
		{
			return new MaterialDeliveryFacts(nodeId, isMaterialOnly ? MaterialDeliveryMode.MaterialOnly : MaterialDeliveryMode.NoMaterialDependencies, 0, 0, 0, 0, 0, Array.Empty<MaterialDeliveryCandidate>(), new[] { isMaterialOnly ? "当前 Mod 仅包含材质/贴图资产。" : "当前 Mod 不含 Unit；不需要模型重建材质策略。" }, GameDataMappedMaterialKeys: mappedOriginalMaterials, SelfMaterialReferences: selfMaterialReferences, IsMaterialOnly: isMaterialOnly);
		}
		if (sourceGraph.RequiredMaterials.Count == 0)
		{
			return new MaterialDeliveryFacts(nodeId, MaterialDeliveryMode.NoMaterialDependencies, sourceGraph.UnitCount, 0, 0, 0, 0, Array.Empty<MaterialDeliveryCandidate>(), new[] { "未发现 Unit → Material 引用。" });
		}

		var embedded = sourceGraph.RequiredMaterials.Intersect(sourceGraph.Materials).ToHashSet();
		var external = sourceGraph.RequiredMaterials.Except(sourceGraph.Materials).ToHashSet();
		var missingEmbeddedTextures = GetMissingTextures(embedded, sourceGraph);
		var embeddedClosure = GetClosureKeys(embedded, sourceGraph);
		var candidates = !includeCandidates || external.Count == 0
			? Array.Empty<MaterialDeliveryCandidate>()
			: await FindCandidatesAsync(nodeId, external, librarySnapshot, cancellationToken).ConfigureAwait(false);
		var notices = new List<string>();
		var mode = ResolveMode(embedded.Count, external.Count, missingEmbeddedTextures.Count, candidates);
		if (missingEmbeddedTextures.Count != 0) notices.Add($"内嵌材质闭包缺少 {missingEmbeddedTextures.Count} 个 Texture。");
		if (external.Count != 0 && candidates.All(candidate => !candidate.IsComplete)) notices.Add($"外部引用 {external.Count} 个 Material，但库内没有完整单一材质提供方。");
		if (mode == MaterialDeliveryMode.ExternalResolved) notices.Add("可在保留材质包的前提下只重建模型 Unit。" );
		if (mode == MaterialDeliveryMode.EmbeddedComplete) notices.Add("材质闭包完整，可作为整体 Mod 重建。" );
		if (mode == MaterialDeliveryMode.Mixed) notices.Add("内嵌与外部材质混用；后续重建前需要用户确认交付策略。" );

		return new MaterialDeliveryFacts(nodeId, mode, sourceGraph.UnitCount, sourceGraph.RequiredMaterials.Count, embedded.Count, external.Count, missingEmbeddedTextures.Count, candidates, notices, embeddedClosure, isMaterialOnly, mappedOriginalMaterials, selfMaterialReferences);
	}

	private async ValueTask<IReadOnlyList<MaterialDeliveryCandidate>> FindCandidatesAsync(ModNodeId sourceNodeId, IReadOnlySet<AssetKey> requiredExternalMaterials, LibrarySnapshot snapshot, CancellationToken cancellationToken)
	{
		if (referenceIndex is not null)
		{
			var providers = await referenceIndex.FindProviderFactsAsync(requiredExternalMaterials, cancellationToken).ConfigureAwait(false);
			return providers
				.Where(provider => provider.NodeId != sourceNodeId && snapshot.Nodes.ContainsKey(provider.NodeId))
				.GroupBy(provider => provider.NodeId)
				.Select(group =>
				{
					var covered = group.Select(provider => provider.AssetKey).ToHashSet();
					var node = snapshot.Nodes[group.Key];
					return new MaterialDeliveryCandidate(
						group.Key,
						node.Metadata.Name,
						covered.Count,
						0,
						covered.Count == requiredExternalMaterials.Count,
						covered.Select(key => new AssetKey(key.TypeId, key.FileId)).ToHashSet());
				})
				.OrderByDescending(candidate => candidate.IsComplete)
				.ThenByDescending(candidate => candidate.CoveredMaterialCount)
				.ThenBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
				.ToArray();
		}

		var candidates = new List<MaterialDeliveryCandidate>();
		foreach (var node in snapshot.Nodes.Values.Where(node => node.Id != sourceNodeId).OrderBy(node => node.Metadata.Name, StringComparer.OrdinalIgnoreCase))
		{
			cancellationToken.ThrowIfCancellationRequested();
			// 候选扫描只消费已有 ReferenceGraph 产品；不可用节点降级为“不参与候选”，不触发高级分析。
			var cached = await LoadReferenceGraphAsync(node.Id, snapshot, cancellationToken).ConfigureAwait(false);
			if (cached is null) continue;
			var graph = BuildGraph(cached);
			var covered = requiredExternalMaterials.Intersect(graph.Materials).ToHashSet();
			if (covered.Count == 0) continue;
			var missingTextures = GetMissingTextures(covered, graph);
			candidates.Add(new MaterialDeliveryCandidate(node.Id, node.Metadata.Name, covered.Count, missingTextures.Count, covered.Count == requiredExternalMaterials.Count && missingTextures.Count == 0, GetClosureKeys(covered, graph)));
		}
		return candidates
			.OrderByDescending(candidate => candidate.IsComplete)
			.ThenByDescending(candidate => candidate.CoveredMaterialCount)
			.ThenBy(candidate => candidate.MissingTextureCount)
			.ThenBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
			.ToArray();
	}

	private async ValueTask<ReferenceGraphFacts?> LoadReferenceGraphAsync(ModNodeId nodeId, LibrarySnapshot snapshot, CancellationToken cancellationToken)
	{
		if (!snapshot.Nodes.TryGetValue(nodeId, out var node)) return null;
		var result = await informationCenter.RequestReferenceGraphAsync(
			node,
			modsRootDirectory!,
			new ModInformationRequest(ModInformationKind.ReferenceGraph, "MaterialDelivery"),
			cancellationToken).ConfigureAwait(false);
		return result.Data;
	}

	private static MaterialDeliveryMode ResolveMode(int embeddedCount, int externalCount, int missingEmbeddedTextureCount, IReadOnlyList<MaterialDeliveryCandidate> candidates)
	{
		if (externalCount == 0) return missingEmbeddedTextureCount == 0 ? MaterialDeliveryMode.EmbeddedComplete : MaterialDeliveryMode.EmbeddedIncomplete;
		if (embeddedCount != 0) return MaterialDeliveryMode.Mixed;
		return candidates.Any(candidate => candidate.IsComplete) ? MaterialDeliveryMode.ExternalResolved : MaterialDeliveryMode.ExternalUnresolved;
	}

	private static HashSet<AssetKey> GetMissingTextures(IReadOnlySet<AssetKey> materials, PatchGraph graph)
		=> graph.MaterialTextures
			.Where(pair => materials.Contains(pair.Material) && !graph.Textures.Contains(pair.Texture))
			.Select(pair => pair.Texture)
			.ToHashSet();

	private static HashSet<AssetKey> GetClosureKeys(IReadOnlySet<AssetKey> materials, PatchGraph graph)
		=> materials
			.Concat(graph.MaterialTextures.Where(pair => materials.Contains(pair.Material) && graph.Textures.Contains(pair.Texture)).Select(pair => pair.Texture))
			.ToHashSet();

	private static PatchGraph BuildGraph(ReferenceGraphFacts snapshot)
	{
		var analyses = snapshot.Analyses;
		var assets = analyses.SelectMany(analysis => analysis.Assets).Select(asset => asset.AssetKey).ToHashSet();
		var references = analyses.SelectMany(analysis => analysis.References).ToArray();
		return new PatchGraph(
			assets.Count(key => key.TypeId == UnitTypeId),
			assets.Where(key => key.TypeId == MaterialTypeId).ToHashSet(),
			assets.Where(key => key.TypeId == TextureTypeId).ToHashSet(),
			references.Where(reference => reference.Kind == PatchReferenceKind.UnitMaterial).Select(reference => reference.TargetAssetKey).ToHashSet(),
			assets,
			references,
			references.Where(reference => reference.Kind == PatchReferenceKind.MaterialTexture).Select(reference => (reference.SourceAssetKey, reference.TargetAssetKey)).ToArray());
	}

	private sealed record PatchGraph(
		int UnitCount,
		IReadOnlySet<AssetKey> Materials,
		IReadOnlySet<AssetKey> Textures,
		IReadOnlySet<AssetKey> RequiredMaterials,
		IReadOnlySet<AssetKey> Assets,
		IReadOnlyList<PatchAssetReference> References,
		IReadOnlyList<(AssetKey Material, AssetKey Texture)> MaterialTextures);
}
