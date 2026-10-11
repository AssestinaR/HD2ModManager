using HD2ModCore.Application;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

// Purpose: Builds reusable Mod role facts without inspecting payloads beyond the cached dependency graph.
public sealed class ModAssetRoleFactsService : IModAssetRoleFactsService
{
	private readonly IModInformationCenter informationCenter;
	private readonly IReferenceGraphQueryIndex referenceIndex;

	public ModAssetRoleFactsService(
		IModInformationCenter informationCenter,
		IReferenceGraphQueryIndex referenceIndex)
	{
		this.informationCenter = informationCenter ?? throw new ArgumentNullException(nameof(informationCenter));
		this.referenceIndex = referenceIndex ?? throw new ArgumentNullException(nameof(referenceIndex));
	}

	public async ValueTask<ModAssetRoleFacts> GetAsync(ModNode node, string modsRootDirectory, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(node);
		ArgumentException.ThrowIfNullOrWhiteSpace(modsRootDirectory);

		var graphResult = await informationCenter.RequestReferenceGraphAsync(
			node,
			modsRootDirectory,
			new ModInformationRequest(ModInformationKind.ReferenceGraph, "ModAssetRoleFacts"),
			cancellationToken).ConfigureAwait(false);
		if (graphResult.Data is null)
			return new ModAssetRoleFacts(node.Id, graphResult.Generation ?? "unavailable", DateTimeOffset.UtcNow, ModAssetRole.Unknown, 0, 0, 0, 0, graphResult.Issues);

		var assets = graphResult.Data.Analyses
			.SelectMany(analysis => analysis.Assets)
			.Select(asset => asset.AssetKey)
			.ToHashSet();
		var externalDependencies = graphResult.Data.Analyses
			.SelectMany(analysis => analysis.References)
			.Select(reference => reference.TargetAssetKey)
			.Where(target => !assets.Contains(target))
			.ToHashSet();
		var issues = new List<CoreIssue>(graphResult.Issues);
		var mappedCount = 0;
		try
		{
			var mappingKeys = assets
				.Select(asset => new AssetKey(asset.TypeId, asset.FileId))
				.ToHashSet();
			var mappings = (await informationCenter.RequestGameDataMappingAsync(mappingKeys, new ModInformationRequest(ModInformationKind.GameDataMapping, "ModAssetRoleFacts"), cancellationToken).ConfigureAwait(false)).AsMappingFacts();
			mappedCount = mappings.Assets.Values.Count(asset => asset.TargetArchives.Count != 0);
			issues.AddRange(mappings.Issues);
		}
		catch (Exception exception) when (exception is not OperationCanceledException)
		{
			issues.Add(new CoreIssue(CoreIssueSeverity.Warning, "ModAssetRoleMappingFailed", exception.Message, node.RelativePath, node.Id, exception.ToString()));
		}

		var consumers = await referenceIndex.FindConsumerFactsAsync(assets, cancellationToken).ConfigureAwait(false);
		var incomingCount = consumers.Count(consumer => consumer.NodeId != node.Id);
		var role = ResolveRole(mappedCount != 0, incomingCount != 0);
		return new ModAssetRoleFacts(node.Id, graphResult.Data.Generation, DateTimeOffset.UtcNow, role, assets.Count, mappedCount, externalDependencies.Count, incomingCount, issues);
	}

	private static ModAssetRole ResolveRole(bool hasGameDataOverrides, bool hasIncomingConsumers)
		=> (hasGameDataOverrides, hasIncomingConsumers) switch
		{
			(true, true) => ModAssetRole.GameDataOverrideAndDependencyProvider,
			(true, false) => ModAssetRole.GameDataOverride,
			(false, true) => ModAssetRole.DependencyComponent,
			_ => ModAssetRole.Independent,
		};
}
