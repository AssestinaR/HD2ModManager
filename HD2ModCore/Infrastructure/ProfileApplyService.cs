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
		var duplicatePhysicalSources = FindDuplicatePhysicalSources(effectiveProfile, snapshot);
		if (duplicatePhysicalSources.Count > 0)
			return new ApplyResult(false, Array.Empty<ApplyOperationResult>(), null, duplicatePhysicalSources);
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

	private static IReadOnlyList<CoreIssue> FindDuplicatePhysicalSources(Profile profile, LibrarySnapshot snapshot)
	{
		var duplicates = profile.Entries
			.Select(entry => snapshot.Nodes.TryGetValue(entry.NodeId, out var node) ? node : null)
			.Where(node => node?.Metadata.Kind == ModNodeKind.Option
				&& !string.IsNullOrWhiteSpace(node.Metadata.SourcePackageGuid)
				&& !string.IsNullOrWhiteSpace(node.Metadata.SourcePackagePath)
				&& node.PatchGroups.Count > 0)
			.Cast<ModNode>()
			.GroupBy(node => node.Metadata.SourcePackageGuid + "\u001F" + node.Metadata.SourcePackagePath, StringComparer.OrdinalIgnoreCase)
			.Where(group => group.Select(node => node.Id).Distinct().Count() > 1)
			.ToArray();
		return duplicates.Select(group =>
		{
			var nodes = group.OrderBy(node => node.Metadata.Name, StringComparer.OrdinalIgnoreCase).ToArray();
			var sourcePath = nodes[0].Metadata.SourcePackagePath!;
			var names = string.Join("、", nodes.Select(node => node.Metadata.Name));
			return new CoreIssue(CoreIssueSeverity.Error, "DuplicatePhysicalOption", $"部署已取消：同一真实 Patch 目录被重复启用：{sourcePath}（{names}）。请关闭重复的相关选项后重试。", sourcePath, nodes[0].Id);
		}).ToArray();
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
		=> ProfileEffectiveProfileResolver.Resolve(profile, snapshot, optionStates);
}
