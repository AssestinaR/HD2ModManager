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
		var nextLoadOrder = 0;
		foreach (var hostEntry in sourceEntries)
		{
			resolved.Add(hostEntry with { LoadOrder = nextLoadOrder++ });
			if (!snapshot.Nodes.TryGetValue(hostEntry.NodeId, out var host)
				|| host.Metadata.Kind != ModNodeKind.Standard)
				continue;

			var hostId = host.Id.Value.ToString("N");
			var enabledOptions = snapshot.Nodes.Values
				.Where(node => node.Metadata.Kind == ModNodeKind.Option)
				.Where(node => node.Metadata.HostModGuids?.Any(candidate => SameGuid(candidate, hostId)) == true)
				.Where(node => optionStates.IsEnabled(node.Id.Value.ToString("N"), hostId))
				.OrderBy(node => optionStates.GetEffectiveOrder(node.Id.Value.ToString("N"), hostId) ?? int.MaxValue)
				.ThenBy(node => node.Metadata.OptionOrder ?? int.MaxValue)
				.ThenBy(node => node.Id.Value)
				.ToArray();
			foreach (var option in enabledOptions)
			{
				// A shared option is physical patch content and must only appear once.
				if (!includedOptions.Add(option.Id)) continue;
				resolved.Add(new ProfileEntry(option.Id, nextLoadOrder++));
			}
		}
		return profile with { Entries = resolved };
	}

	private static bool SameGuid(string? left, string? right)
		=> Guid.TryParse(left, out var leftGuid) && Guid.TryParse(right, out var rightGuid)
			? leftGuid == rightGuid
			: string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
