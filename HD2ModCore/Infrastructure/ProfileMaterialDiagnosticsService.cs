using HD2ModAdaptation.Analysis;
using HD2ModCore.Application;
using HD2ModCore.Domain;
using DomainAssetKey = HD2ModCore.Domain.AssetKey;

namespace HD2ModCore.Infrastructure;

// 作用：仅消费完整高级缓存生成 Profile 的材质依赖诊断。
// Purpose: Resolves final profile winners before traversing Unit → Material → Texture references.
public sealed class ProfileMaterialDiagnosticsService : IProfileMaterialDiagnosticsService
{
	private const ulong UnitTypeId = 0xe0a48d0be9a7453f;
	private const ulong MaterialTypeId = 0xeac0b497876adedf;
	private const ulong TextureTypeId = 0xcd4238c6a0c69e32;
	private readonly IModInformationCenter informationCenter;
	private readonly IAssetArchiveIndexService? indexService;
	private readonly IReferenceGraphQueryIndex referenceIndex;

	public ProfileMaterialDiagnosticsService(IModInformationCenter informationCenter, IAssetArchiveIndexService? indexService, IReferenceGraphQueryIndex referenceIndex)
	{
		this.informationCenter = informationCenter ?? throw new ArgumentNullException(nameof(informationCenter));
		this.indexService = indexService;
		this.referenceIndex = referenceIndex ?? throw new ArgumentNullException(nameof(referenceIndex));
	}

	public async ValueTask<ProfileMaterialDiagnostics> BuildAsync(Profile profile, LibrarySnapshot snapshot, string modsRootDirectory, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(profile);
		ArgumentNullException.ThrowIfNull(snapshot);
		var indexed = await referenceIndex.ReadProfileFactsAsync(profile.Entries.Select(entry => entry.NodeId).ToArray(), cancellationToken).ConfigureAwait(false);
		if (indexed.Assets.Count != 0) return await BuildFromIndexedFactsAsync(profile, snapshot, indexed, cancellationToken).ConfigureAwait(false);
		var issues = new List<CoreIssue>();
		var providers = new Dictionary<DomainAssetKey, List<Provider>>();
		foreach (var entry in profile.Entries.OrderBy(entry => entry.LoadOrder).ThenBy(entry => entry.AddedUtc).ThenBy(entry => entry.NodeId.Value))
		{
			if (!snapshot.Nodes.TryGetValue(entry.NodeId, out var node))
			{
				issues.Add(new CoreIssue(CoreIssueSeverity.Warning, "ProfileNodeMissing", $"Profile references missing Mod {entry.NodeId}.", NodeId: entry.NodeId));
				continue;
			}
			var analyses = await GetAnalysesAsync(node, modsRootDirectory, cancellationToken).ConfigureAwait(false);
			foreach (var analysis in analyses)
			{
				foreach (var issue in analysis.Issues) issues.Add(new CoreIssue(CoreIssueSeverity.Warning, issue.Code, issue.Message, NodeId: node.Id));
				var assets = analysis.Assets.Select(asset => ToDomain(asset.AssetKey)).ToHashSet();
				foreach (var asset in assets)
				{
					if (!providers.TryGetValue(asset, out var list)) providers[asset] = list = new List<Provider>();
					list.Add(new Provider(node.Id, node.Metadata.Name, entry.LoadOrder, analysis.Input.PatchTocFilePath, analysis.References.Where(reference => ToDomain(reference.SourceAssetKey) == asset).ToArray()));
				}
			}
		}

		var winners = providers.ToDictionary(pair => pair.Key, pair => pair.Value.OrderBy(provider => provider.LoadOrder).ThenBy(provider => provider.NodeId.Value).Last());
		var diagnostics = new List<ProfileMaterialDiagnostic>();
		var fallbackResolver = indexService is null ? null : new CurrentGameMaterialFallbackResolver(indexService);
		var reachableMaterials = new HashSet<DomainAssetKey>();
		var reachableTextures = new HashSet<DomainAssetKey>();
		var unitsWithMaterialBindings = new HashSet<DomainAssetKey>();
		var unitsWithAvailableMaterial = new HashSet<DomainAssetKey>();
		var referencedMaterials = winners.Where(pair => pair.Key.TypeId == UnitTypeId).SelectMany(pair => pair.Value.References.Where(reference => reference.Kind == PatchReferenceKind.UnitMaterial).Select(reference => ToDomain(reference.TargetAssetKey))).ToHashSet();
		var referencedTextures = winners.Where(pair => pair.Key.TypeId == MaterialTypeId).SelectMany(pair => pair.Value.References.Where(reference => reference.Kind == PatchReferenceKind.MaterialTexture).Select(reference => ToDomain(reference.TargetAssetKey))).ToHashSet();
		var unresolvedByProfile = referencedMaterials.Concat(referencedTextures).Where(key => !winners.ContainsKey(key)).ToHashSet();
		var mapping = (await informationCenter.RequestGameDataMappingAsync(unresolvedByProfile, new ModInformationRequest(ModInformationKind.GameDataMapping, "ProfileMaterialDiagnostics"), cancellationToken).ConfigureAwait(false)).AsMappingFacts();
		issues.AddRange(mapping.Issues);
		var availableInGameData = mapping.Assets.Where(pair => pair.Value.TargetArchives.Count != 0).Select(pair => pair.Key).ToHashSet();

		foreach (var (unitKey, unitProvider) in winners.Where(pair => pair.Key.TypeId == UnitTypeId))
		{
			foreach (var reference in unitProvider.References.Where(reference => reference.Kind == PatchReferenceKind.UnitMaterial))
			{
				unitsWithMaterialBindings.Add(unitKey);
				var material = ToDomain(reference.TargetAssetKey);
				if (!winners.TryGetValue(material, out var materialProvider))
				{
					var fallback = fallbackResolver is null ? null : await fallbackResolver.ResolveAsync(reference, cancellationToken).ConfigureAwait(false);
					if (fallback is not null)
					{
						var accepted = fallback.IsPlaceholderMesh || fallback.MaterialAssetKey == material;
						diagnostics.Add(new ProfileMaterialDiagnostic(
							unitProvider.NodeId,
							fallback.MaterialAssetKey,
							accepted ? ProfileMaterialDiagnosticKind.CurrentGameMaterialFallback : ProfileMaterialDiagnosticKind.CurrentGameMaterialCandidate,
							accepted ? "使用当前原版材质" : "发现当前原版材质候选",
							accepted
								? $"Unit 0x{unitKey.FileId:x16} 的 Mesh {reference.MeshInfoIndex} section {reference.ReferenceIndex} 使用当前 Game Data {fallback.ArchiveName} 的 Material。"
								: $"Unit 0x{unitKey.FileId:x16} 的可见 Mesh {reference.MeshInfoIndex} section {reference.ReferenceIndex} 可回退至当前 Game Data {fallback.ArchiveName} 的 Material，但为避免覆盖自定义材质，仍需要库内提供者或用户确认。",
							unitKey));
						if (accepted) continue;
					}
					if (availableInGameData.Contains(material))
					{
						unitsWithAvailableMaterial.Add(unitKey);
						continue;
					}
					diagnostics.Add(new ProfileMaterialDiagnostic(unitProvider.NodeId, material, ProfileMaterialDiagnosticKind.MissingMaterial, "缺失材质", $"有效 Unit 0x{unitKey.FileId:x16} 引用了未由当前 Profile 或 Game Data 提供的 Material。", unitKey));
					continue;
				}
				unitsWithAvailableMaterial.Add(unitKey);
				reachableMaterials.Add(material);
				foreach (var textureReference in materialProvider.References.Where(item => item.Kind == PatchReferenceKind.MaterialTexture))
				{
					var texture = ToDomain(textureReference.TargetAssetKey);
					if (!winners.ContainsKey(texture))
					{
						if (!availableInGameData.Contains(texture)) diagnostics.Add(new ProfileMaterialDiagnostic(materialProvider.NodeId, texture, ProfileMaterialDiagnosticKind.MissingTexture, "缺失贴图", $"有效 Material 0x{material.FileId:x16} 引用了未由当前 Profile 或 Game Data 提供的 Texture。", material));
						continue;
					}
					reachableTextures.Add(texture);
				}
			}
		}

		foreach (var unitKey in unitsWithMaterialBindings.Where(unit => !unitsWithAvailableMaterial.Contains(unit)))
		{
			var provider = winners[unitKey];
			diagnostics.Add(new ProfileMaterialDiagnostic(provider.NodeId, unitKey, ProfileMaterialDiagnosticKind.MissingMaterial, "无可用材质", $"有效 Unit 0x{unitKey.FileId:x16} 的所有材质均无法由当前 Profile 或 Game Data 提供。", unitKey));
		}

		foreach (var (asset, provider) in winners.Where(pair => pair.Key.TypeId == MaterialTypeId && !reachableMaterials.Contains(pair.Key)))
		{
			diagnostics.Add(new ProfileMaterialDiagnostic(provider.NodeId, asset, ProfileMaterialDiagnosticKind.NoEffectiveUnitConsumer, "未发现有效 Unit 使用此材质", "当前 Profile 的最终有效 Unit 没有引用该 Material。"));
		}
		foreach (var (asset, provider) in winners.Where(pair => pair.Key.TypeId == TextureTypeId && !reachableTextures.Contains(pair.Key)))
		{
			diagnostics.Add(new ProfileMaterialDiagnostic(provider.NodeId, asset, ProfileMaterialDiagnosticKind.UnreachableResource, "贴图无有效引用", "当前 Profile 的最终有效 Material 没有引用该 Texture。"));
		}

		return new ProfileMaterialDiagnostics(profile.Id, profile.Revision, DateTimeOffset.UtcNow, diagnostics.Distinct().ToArray(), issues);

	}

	private async ValueTask<ProfileMaterialDiagnostics> BuildFromIndexedFactsAsync(Profile profile, LibrarySnapshot snapshot, ProfileIndexedFacts indexed, CancellationToken cancellationToken)
	{
		var order = profile.Entries.OrderBy(entry => entry.LoadOrder).ThenBy(entry => entry.AddedUtc).ThenBy(entry => entry.NodeId.Value).Select((entry, index) => (entry, index)).ToDictionary(item => item.entry.NodeId, item => item);
		var winners = indexed.Assets.Where(asset => order.ContainsKey(asset.NodeId)).GroupBy(asset => asset.AssetKey).ToDictionary(group => group.Key, group => group.OrderBy(asset => order[asset.NodeId].entry.LoadOrder).ThenBy(asset => order[asset.NodeId].index).Last().NodeId);
		var winnerRefs = indexed.References.Where(reference => winners.TryGetValue(reference.SourceAssetKey, out var nodeId) && nodeId == reference.NodeId).ToArray();
		var unitMaterial = winnerRefs.Where(reference => reference.RelationKind == (int)PatchReferenceKind.UnitMaterial).ToArray();
		var materialTexture = winnerRefs.Where(reference => reference.RelationKind == (int)PatchReferenceKind.MaterialTexture).ToArray();
		var unresolved = unitMaterial.Select(reference => reference.TargetAssetKey).Concat(materialTexture.Select(reference => reference.TargetAssetKey)).Where(asset => !winners.ContainsKey(asset)).ToHashSet();
		var mapped = (await informationCenter.RequestGameDataMappingAsync(unresolved, new ModInformationRequest(ModInformationKind.GameDataMapping, "ProfileMaterialDiagnostics"), cancellationToken).ConfigureAwait(false)).AsMappingFacts();
		var availableGameData = mapped.Assets.Where(pair => pair.Value.TargetArchives.Count != 0).Select(pair => pair.Key).ToHashSet();
		var textureTargets = materialTexture.GroupBy(reference => reference.SourceAssetKey).ToDictionary(group => group.Key, group => group.Select(reference => reference.TargetAssetKey).ToArray());
		bool IsMaterialAvailable(AssetKey material)
		{
			if (!winners.ContainsKey(material)) return availableGameData.Contains(material);
			if (!textureTargets.TryGetValue(material, out var textures) || textures.Length == 0) return true;
			return textures.Any(texture => winners.ContainsKey(texture) || availableGameData.Contains(texture));
		}
		var diagnostics = new List<ProfileMaterialDiagnostic>();
		foreach (var group in unitMaterial.GroupBy(reference => reference.SourceAssetKey))
		{
			if (group.Any(reference => IsMaterialAvailable(reference.TargetAssetKey))) continue;
			var unit = group.Key;
			var nodeId = winners[unit];
			diagnostics.Add(new ProfileMaterialDiagnostic(nodeId, unit, ProfileMaterialDiagnosticKind.MissingMaterial, "无可用材质", $"有效 Unit 0x{unit.FileId:x16} 的所有材质均无法由当前 Profile 或 Game Data 提供。", unit));
		}
		return new ProfileMaterialDiagnostics(profile.Id, profile.Revision, DateTimeOffset.UtcNow, diagnostics, mapped.Issues);
	}

	private async ValueTask<IReadOnlyList<PatchGroupAnalysis>> GetAnalysesAsync(ModNode node, string modsRootDirectory, CancellationToken cancellationToken)
	{
		if (informationCenter is not null)
		{
			var result = await informationCenter.RequestReferenceGraphAsync(
				node,
				modsRootDirectory,
				new ModInformationRequest(ModInformationKind.ReferenceGraph, "MaterialDiagnostics"),
				cancellationToken).ConfigureAwait(false);
			return result.Data?.Analyses ?? Array.Empty<PatchGroupAnalysis>();
		}
		return Array.Empty<PatchGroupAnalysis>();
	}

	private static DomainAssetKey ToDomain(HD2ModAdaptation.PatchReconstruction.AssetKey assetKey) => new(assetKey.TypeId, assetKey.FileId);
	private sealed record Provider(ModNodeId NodeId, string ModName, int LoadOrder, string PatchTocPath, IReadOnlyList<PatchAssetReference> References);
}
