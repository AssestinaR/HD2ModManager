using HD2ModCore.Application;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

// Purpose: Centralizes file-system-derived library facts such as directories, icons, patch indexes and asset summaries.
public sealed class LibraryDerivedDataService : ILibraryDerivedDataService
{
	private readonly IModInformationCenter _informationCenter;
	private readonly ModAssetSummaryProjector _assetSummaryProjector;

	public LibraryDerivedDataService(IModInformationCenter informationCenter, ModAssetSummaryProjector assetSummaryProjector)
	{
		_informationCenter = informationCenter ?? throw new ArgumentNullException(nameof(informationCenter));
		_assetSummaryProjector = assetSummaryProjector ?? throw new ArgumentNullException(nameof(assetSummaryProjector));
	}

	public async ValueTask<DerivedLibraryData> BuildAsync(LibrarySnapshot snapshot, string modsRootDirectory, string? gameDataDirectory = null, IReadOnlySet<ModNodeId>? nodeIds = null, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		if (string.IsNullOrWhiteSpace(modsRootDirectory))
		{
			throw new ArgumentException("Value cannot be null or whitespace.", nameof(modsRootDirectory));
		}

		var contentFacts = await GetAssetInventoryAsync(snapshot, modsRootDirectory, nodeIds, cancellationToken).ConfigureAwait(false);
		var issues = new List<CoreIssue>(contentFacts.Values.SelectMany(facts => facts.Issues));
		var selectedNodes = snapshot.Nodes.Values.Where(node => nodeIds is null || nodeIds.Contains(node.Id)).ToArray();
		var factsByNode = selectedNodes.Where(node => contentFacts.ContainsKey(node.Id)).ToDictionary(node => node, node => contentFacts[node.Id]);
		var summaryProjection = await _assetSummaryProjector.ProjectManyWithGenerationAsync(factsByNode, cancellationToken).ConfigureAwait(false);
		var summaries = summaryProjection.Summaries;
		var nodes = new Dictionary<ModNodeId, DerivedModNodeData>();

		foreach (var node in selectedNodes)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (nodeIds is not null && !nodeIds.Contains(node.Id))
			{
				continue;
			}
			var directory = ResolveNodeDirectory(modsRootDirectory, node.RelativePath);
			var directoryExists = Directory.Exists(directory);
			if (!contentFacts.TryGetValue(node.Id, out var nodeContentFacts))
			{
				continue;
			}
			var patchFiles = nodeContentFacts.ToPatchFileIndex();

			summaries.TryGetValue(node.Id, out var assetSummary);
			var assetStructure = summaryProjection.Structures?.GetValueOrDefault(node.Id);
			var unitCompatibility = ModUnitCompatibilityReport.FromEvidence(nodeContentFacts.PatchGroups.SelectMany(group => group.UnitVersions ?? Array.Empty<UnitVersionEvidence>()));

			var nodeIssues = issues.Where(i => i.NodeId == node.Id).ToList();
			nodes[node.Id] = new DerivedModNodeData(
				NodeId: node.Id,
				RelativePath: node.RelativePath,
				AbsoluteDirectory: directory,
				DirectoryExists: directoryExists,
				IconPath: ModIconLocator.TryResolve(directory),
				PatchFiles: patchFiles,
				ContentFacts: nodeContentFacts,
				AssetSummary: assetSummary,
				UnitCompatibility: unitCompatibility,
				Issues: nodeIssues,
				AssetStructure: assetStructure);
		}

		return new DerivedLibraryData(DateTimeOffset.UtcNow, nodes, issues, summaryProjection.MappingGeneration);
	}

	private async ValueTask<IReadOnlyDictionary<ModNodeId, ModContentFacts>> GetAssetInventoryAsync(
		LibrarySnapshot snapshot,
		string modsRootDirectory,
		IReadOnlySet<ModNodeId>? nodeIds,
		CancellationToken cancellationToken)
	{
		var result = new Dictionary<ModNodeId, ModContentFacts>();
		foreach (var node in snapshot.Nodes.Values)
		{
			if (nodeIds is not null && !nodeIds.Contains(node.Id)) continue;
			var inventory = await _informationCenter.RequestAssetInventoryAsync(
				node,
				modsRootDirectory,
				new ModInformationRequest(ModInformationKind.AssetInventory, "LibraryRefresh"),
				cancellationToken).ConfigureAwait(false);
			if (inventory.Data is not null) result[node.Id] = inventory.Data;
		}
		return result;
	}

	private static string ResolveNodeDirectory(string modsRootDirectory, string relativePath)
	{
		return Path.GetFullPath(Path.Combine(modsRootDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar)));
	}
}
