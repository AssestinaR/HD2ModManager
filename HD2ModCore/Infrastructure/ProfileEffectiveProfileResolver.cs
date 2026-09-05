using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

// Resolves the exact profile expansion used for deployment: top-level profile
// entries first, then each enabled option tree in its host-scoped effective
// order. Keeping this public and UI-neutral lets CLI/agents inspect the same
// order the deployment pipeline will actually consume.
public static class ProfileEffectiveProfileResolver
{
	public static Profile Resolve(Profile profile, LibrarySnapshot snapshot, OptionActivationSnapshot optionStates)
	{
		ArgumentNullException.ThrowIfNull(profile);
		ArgumentNullException.ThrowIfNull(snapshot);
		ArgumentNullException.ThrowIfNull(optionStates);

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
			// Explicit logical containers are structural Profile anchors and do not
			// represent deployable content. Keep legacy unmarked empty Standard
			// entries in the projection for backwards compatibility; imported and
			// user-created logical entries carry the durable marker and are omitted
			// from the effective deployment list while their descendants expand.
			if (!snapshot.Nodes.TryGetValue(hostEntry.NodeId, out var host)
				|| host.Metadata.Kind is not (ModNodeKind.Standard or ModNodeKind.Option or ModNodeKind.ReShade))
			{
				resolved.Add(hostEntry with { LoadOrder = nextLoadOrder++ });
				continue;
			}

			if (host.Metadata.Kind != ModNodeKind.Standard
				|| !host.Metadata.IsLogicalContainer)
			{
				resolved.Add(hostEntry with { LoadOrder = nextLoadOrder++ });
			}
			if (host.Metadata.Kind is ModNodeKind.Standard or ModNodeKind.Option)
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
