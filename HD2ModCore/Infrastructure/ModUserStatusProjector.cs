using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

// Purpose: Projects already-built facts into concise Mod states without performing file-system or archive work.
public static class ModUserStatusProjector
{
	public static IReadOnlyDictionary<ModNodeId, ModUserStatus> Project(
		LibrarySnapshot snapshot,
		ProfileId? selectedProfileId,
		IReadOnlyDictionary<ModNodeId, ModContentFacts> content,
		ProfileOverrideGraph? expected,
		ProfileMaterialDiagnostics? materialDiagnostics,
		DeployedOverrideGraph? actual,
		OptionActivationSnapshot? optionStates = null)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		var selected = selectedProfileId is { } selectedId ? snapshot.Profiles.FirstOrDefault(profile => profile.Id == selectedId) : null;
		var active = snapshot.ActiveProfileId is { } activeId ? snapshot.Profiles.FirstOrDefault(profile => profile.Id == activeId) : null;
		var activations = optionStates ?? new OptionActivationSnapshot(
			new Dictionary<string, IReadOnlyDictionary<string, OptionHostState>>(StringComparer.OrdinalIgnoreCase));
		var selectedIds = selected?.Entries.Select(entry => entry.NodeId).ToHashSet() ?? [];
		var activeIds = active?.Entries.Select(entry => entry.NodeId).ToHashSet() ?? [];
		if (selected is not null)
			selectedIds.UnionWith(ProfileEffectiveProfileResolver.Resolve(selected, snapshot, activations).Entries.Select(entry => entry.NodeId));
		if (active is not null)
			activeIds.UnionWith(ProfileEffectiveProfileResolver.Resolve(active, snapshot, activations).Entries.Select(entry => entry.NodeId));
		var expectedIsCurrent = active is not null
			&& expected is not null
			&& expected.ProfileId == active.Id
			&& expected.ProfileRevision == active.Revision;
		var diagnosticsAreCurrent = active is not null
			&& materialDiagnostics is not null
			&& materialDiagnostics.ProfileId == active.Id
			&& materialDiagnostics.ProfileRevision == active.Revision;
		var statuses = new Dictionary<ModNodeId, ModUserStatus>();
		foreach (var node in snapshot.Nodes.Values)
		{
			var inSelected = selectedIds.Contains(node.Id);
			var inActive = activeIds.Contains(node.Id);
			if (inActive)
			{
				var nodeDiagnostics = diagnosticsAreCurrent ? materialDiagnostics!.Items.Where(item => item.NodeId == node.Id).ToArray() : Array.Empty<ProfileMaterialDiagnostic>();
				var coverage = expectedIsCurrent ? expected!.Coverages.FirstOrDefault(item => item.NodeId == node.Id) : null;
				var coveredBy = expectedIsCurrent
					? expected!.AssetChains
						.Where(chain => chain.Entries.Any(entry => entry.NodeId == node.Id) && chain.Winner.NodeId != node.Id)
						.Select(chain => chain.Winner.ModName)
						.Distinct(StringComparer.OrdinalIgnoreCase)
						.ToArray()
					: Array.Empty<string>();
				var allMaterialsMissing = diagnosticsAreCurrent
					&& nodeDiagnostics.Any(item => item.Kind == ProfileMaterialDiagnosticKind.MissingMaterial && item.Summary == "无可用材质");
				statuses[node.Id] = allMaterialsMissing
					? new ModUserStatus(node.Id, ModUserStatusKind.MissingMaterial, "缺材质", "没有任何可用材质。", inSelected, true, coveredBy)
					: coverage?.OverriddenAssetKeys > 0
						? new ModUserStatus(node.Id, ModUserStatusKind.Overridden, "被覆盖", FormatCoverageSummary(coverage, coveredBy), inSelected, true, coveredBy)
						: new ModUserStatus(node.Id, ModUserStatusKind.Enabled, "已启用", "已加入活动配置。", inSelected, true);
				continue;
			}
			statuses[node.Id] = new ModUserStatus(node.Id, ModUserStatusKind.Stored, "仅存储", "尚未启用。", inSelected, false);
		}
		return statuses;
	}

	private static string FormatCoverageSummary(ProfileModCoverage coverage, IReadOnlyList<string> coveredBy)
	{
		var names = coveredBy.Take(2).ToArray();
		var owner = names.Length == 0 ? "后加载 Mod" : string.Join("、", names) + (coveredBy.Count > names.Length ? " 等" : string.Empty);
		return coverage.FullyOverridden
			? $"全部资产已被 {owner} 覆盖。"
			: $"{coverage.OverriddenAssetKeys}/{coverage.TotalAssetKeys} 个资产被 {owner} 覆盖。";
	}
}
