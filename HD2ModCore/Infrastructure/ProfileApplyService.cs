using HD2ModCore.Application;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

// 作用：仅基于 JSON 与 Mod 文件事实串联计划生成和部署执行，不触发内容分析。
// Purpose: Applies a profile from JSON and filesystem facts without content analysis.
public sealed class ProfileApplyService : IProfileApplyService
{
	private readonly IModInformationCenter _informationCenter;
	private readonly IApplyPlanner _planner;
	private readonly IApplyExecutor _executor;
	private readonly DeploymentCapabilityService _capabilityService;
	private readonly OptionActivationStore _optionActivations;
	private readonly ILuaDeploymentService _luaDeploymentService;

	public ProfileApplyService(
		IModInformationCenter informationCenter,
		IApplyPlanner planner,
		IApplyExecutor executor,
		DeploymentCapabilityService? capabilityService = null,
		StoragePaths? paths = null,
		OptionActivationStore? optionActivations = null,
		ILuaDeploymentService? luaDeploymentService = null)
	{
		_informationCenter = informationCenter ?? throw new ArgumentNullException(nameof(informationCenter));
		_planner = planner ?? throw new ArgumentNullException(nameof(planner));
		_executor = executor ?? throw new ArgumentNullException(nameof(executor));
		_capabilityService = capabilityService ?? new DeploymentCapabilityService();
		_optionActivations = optionActivations ?? new OptionActivationStore(Path.Combine((paths ?? new StoragePaths(AppContext.BaseDirectory)).ModsDirectory, "option-activations.json"));
		_luaDeploymentService = luaDeploymentService ?? new LuaDeploymentService();
	}

	public async ValueTask<ApplyResult> ApplyAsync(
		Profile profile,
		LibrarySnapshot snapshot,
		string modsRootDirectory,
		string gameDataDirectory,
		CancellationToken cancellationToken = default)
	{
		var capability = _capabilityService.Probe(modsRootDirectory, gameDataDirectory);
		if (!capability.IsAvailable || capability.Method is null)
		{
			return new ApplyResult(false, [], null, [new CoreIssue(CoreIssueSeverity.Error, "DeploymentUnavailable", capability.Error ?? capability.Summary, gameDataDirectory)]);
		}
		var fileFactsResult = await _informationCenter.RequestFileFactsAsync(
			snapshot,
			modsRootDirectory,
			new ModInformationRequest(ModInformationKind.FileFacts, "Deployment"),
			cancellationToken).ConfigureAwait(false);
		if (fileFactsResult.Data is null)
			return new ApplyResult(false, [], null, fileFactsResult.Issues);
		var allFacts = fileFactsResult.Data;
		var effectiveProfile = ResolveEffectiveProfile(profile, snapshot, _optionActivations.CreateSnapshot());
		var activeNodeIds = effectiveProfile.Entries.Select(entry => entry.NodeId).ToHashSet();
		var index = new PatchFileIndex(
			allFacts.BuiltUtc,
			allFacts.FilesByNode.Where(pair => activeNodeIds.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value),
			allFacts.Issues.Where(issue => issue.NodeId is null || activeNodeIds.Contains(issue.NodeId.Value)).ToList());
		var contentFacts = await RequestActiveContentFactsAsync(effectiveProfile, snapshot, modsRootDirectory, cancellationToken).ConfigureAwait(false);
		var preparation = await _luaDeploymentService.PrepareAsync(
			effectiveProfile,
			snapshot,
			index,
			modsRootDirectory,
			gameDataDirectory,
			contentFacts,
			cancellationToken).ConfigureAwait(false);
		if (preparation.HasErrors)
			return new ApplyResult(false, Array.Empty<ApplyOperationResult>(), null, preparation.Issues);

		var plan = await _planner.BuildPlanAsync(
			effectiveProfile,
			snapshot,
			preparation.PatchIndex,
			gameDataDirectory,
			new ApplyPlanAugmentation(preparation.GeneratedPatchGroups),
			cancellationToken).ConfigureAwait(false);
		plan = plan with
		{
			Issues = plan.Issues.Concat(preparation.Issues).ToArray(),
			ExternalFileMappings = preparation.ExternalFiles,
		};
		plan = plan with { DeploymentMethod = capability.Method.Value };
		return await _executor.ExecuteAsync(plan, cancellationToken).ConfigureAwait(false);
	}

	private async ValueTask<IReadOnlyDictionary<ModNodeId, ModContentFacts>> RequestActiveContentFactsAsync(
		Profile profile,
		LibrarySnapshot snapshot,
		string modsRootDirectory,
		CancellationToken cancellationToken)
	{
		var result = new Dictionary<ModNodeId, ModContentFacts>();
		foreach (var nodeId in profile.Entries.Select(entry => entry.NodeId).Distinct())
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (!snapshot.Nodes.TryGetValue(nodeId, out var node)) continue;
			var inventory = await _informationCenter.RequestAssetInventoryAsync(
				node,
				modsRootDirectory,
				new ModInformationRequest(ModInformationKind.AssetInventory, "DeploymentLua"),
				cancellationToken).ConfigureAwait(false);
			if (inventory.Data is not null) result[nodeId] = inventory.Data;
		}
		return result;
	}

	private static Profile ResolveEffectiveProfile(Profile profile, LibrarySnapshot snapshot, OptionActivationSnapshot optionStates)
	{
		var sourceEntries = profile.Entries
			.OrderBy(entry => entry.LoadOrder)
			.ThenBy(entry => entry.AddedUtc)
			.ThenBy(entry => entry.NodeId.Value)
			.ToArray();
		var resolved = new List<ProfileEntry>();
		var includedOptions = new HashSet<ModNodeId>();
		var optionGraph = OptionRelationshipGraph.Create(snapshot);
		var nextLoadOrder = 0;
		foreach (var hostEntry in sourceEntries)
		{
			resolved.Add(hostEntry with { LoadOrder = nextLoadOrder++ });
			if (!snapshot.Nodes.TryGetValue(hostEntry.NodeId, out var host)
				|| host.Metadata.Kind is not (ModNodeKind.Standard or ModNodeKind.Option))
				continue;

			AppendEnabledOptions(host.Id, new HashSet<ModNodeId> { host.Id });
		}
		return profile with { Entries = resolved };

		void AppendEnabledOptions(ModNodeId parentId, HashSet<ModNodeId> ancestors)
		{
			var parentKey = parentId.Value.ToString("N");
			var enabledOptions = optionGraph.GetDirectOptions(parentId)
				.Where(option => optionStates.IsEnabled(option.Id.Value.ToString("N"), parentKey))
				.OrderBy(option => optionStates.GetEffectiveOrder(option.Id.Value.ToString("N"), parentKey) ?? int.MaxValue)
				.ThenBy(option => option.Metadata.OptionOrder ?? int.MaxValue)
				.ThenBy(option => option.Id.Value)
				.ToArray();
			foreach (var option in enabledOptions)
			{
				// Malformed legacy option.json files cannot turn deployment into an
				// infinite loop. New imports are trees by construction.
				if (!ancestors.Add(option.Id)) continue;
				// A logical placeholder carries only the relationship. It is still
				// traversed, but never becomes a fake deployable patch entry.
				if (option.PatchGroups.Count > 0 && includedOptions.Add(option.Id))
					resolved.Add(new ProfileEntry(option.Id, nextLoadOrder++));
				AppendEnabledOptions(option.Id, ancestors);
				ancestors.Remove(option.Id);
			}
		}
	}
}
