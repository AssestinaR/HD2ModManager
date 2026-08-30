using HD2ModCore.Application;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

public sealed class SafeModDeletionPlanningServiceTests
{
	[Fact]
	public void WithUnsharedOptions_UsesHostGuidGraph_DeletesOnlyUnsharedClosure_AndPreservesSharedOptions()
	{
		var rootA = Node("Root A", "root-a", ModNodeKind.Standard);
		var rootB = Node("Root B", "root-b", ModNodeKind.Standard);
		var unshared = Node("Unshared", "option-unshared", ModNodeKind.Option, [rootA.Id]);
		var nested = Node("Nested", "option-nested", ModNodeKind.Option, [unshared.Id]);
		var shared = Node("Shared", "option-shared", ModNodeKind.Option, [rootA.Id, rootB.Id]);
		var sharedChild = Node("Shared child", "option-shared-child", ModNodeKind.Option, [shared.Id]);
		var childOnlyNoise = Node("Legacy children noise", "legacy-noise", ModNodeKind.Option);
		rootA = rootA with { Children = [childOnlyNoise.Id] };
		var profile = new Profile(
			ProfileId.New(),
			"Test profile",
			DateTimeOffset.UtcNow,
			null,
			[new ProfileEntry(rootA.Id, 0), new ProfileEntry(unshared.Id, 1)]);
		var snapshot = Snapshot(profile, rootA, rootB, unshared, nested, shared, sharedChild, childOnlyNoise);

		var plan = Service.Plan(snapshot, [rootA.Id], SafeModDeletionScope.WithUnsharedOptions, SafeModDeletionSafetySnapshot.EmptyKnown);

		Assert.False(plan.IsBlocked);
		Assert.Equal([rootA.Id, unshared.Id, nested.Id], plan.LibraryNodeRemovals.Select(item => item.NodeId).ToArray());
		Assert.Equal([unshared.Id, nested.Id], plan.NonSharedDescendantRemovals.Select(item => item.NodeId).ToArray());
		Assert.DoesNotContain(plan.LibraryNodeRemovals, item => item.NodeId == childOnlyNoise.Id);
		var retained = Assert.Single(plan.PreservedSharedOptions);
		Assert.Equal(shared.Id, retained.OptionId);
		Assert.Equal([rootA.Id], retained.RemovedHostIds);
		Assert.Equal([rootB.Id], retained.SurvivingHostIds);
		var stripped = Assert.Single(plan.SurvivingOptionHostRelationsToStrip);
		Assert.Equal(shared.Id, stripped.OptionId);
		Assert.Equal(rootA.Id, stripped.RemovedHostId);
		Assert.Equal([rootB.Id], stripped.RemainingHostIds);
		Assert.Equal([rootA.Id, unshared.Id], plan.ProfileEntriesToClean.Select(item => item.NodeId).ToArray());
		Assert.Contains(plan.ActivationCleanupKeys, item => item.Kind == SafeModDeletionActivationCleanupKind.OptionActivationForRelation
			&& item.PrimaryNodeId == shared.Id && item.SecondaryNodeId == rootA.Id);
		Assert.Equal(["option-nested", "option-unshared", "root-a"], plan.PhysicalRelativePaths.Select(item => item.RelativePath).ToArray());
	}

	[Fact]
	public void SelectedScope_LeavesOptionsInLibrary_ButPlansTheirDirectHostRelationRemoval()
	{
		var root = Node("Root", "root", ModNodeKind.Standard);
		var direct = Node("Direct", "direct", ModNodeKind.Option, [root.Id]);
		var nested = Node("Nested", "nested", ModNodeKind.Option, [direct.Id]);
		root = root with { Children = [nested.Id] };
		var snapshot = Snapshot(null, root, direct, nested);

		var plan = Service.Plan(snapshot, [root.Id], SafeModDeletionScope.Selected, SafeModDeletionSafetySnapshot.EmptyKnown);

		Assert.False(plan.IsBlocked);
		Assert.Equal([root.Id], plan.LibraryNodeRemovals.Select(item => item.NodeId).ToArray());
		Assert.Empty(plan.NonSharedDescendantRemovals);
		Assert.Empty(plan.PreservedSharedOptions);
		var relation = Assert.Single(plan.SurvivingOptionHostRelationsToStrip);
		Assert.Equal(direct.Id, relation.OptionId);
		Assert.Equal(root.Id, relation.RemovedHostId);
		Assert.Empty(relation.RemainingHostIds);
		Assert.DoesNotContain(plan.SurvivingOptionHostRelationsToStrip, item => item.OptionId == nested.Id);
	}

	[Fact]
	public void WithUnsharedOptions_ReachableCycle_IsBlocked_AndDoesNotAutoRemoveCycleMembers()
	{
		var root = Node("Root", "root", ModNodeKind.Standard);
		var alpha = Node("Alpha", "alpha", ModNodeKind.Option, [root.Id]);
		var beta = Node("Beta", "beta", ModNodeKind.Option, [alpha.Id]);
		alpha = alpha with { Metadata = alpha.Metadata with { HostModGuids = [Key(root.Id), Key(beta.Id)] } };
		var snapshot = Snapshot(null, root, alpha, beta);

		var plan = Service.Plan(snapshot, [root.Id], SafeModDeletionScope.WithUnsharedOptions, SafeModDeletionSafetySnapshot.EmptyKnown);

		Assert.True(plan.IsBlocked);
		Assert.Contains(plan.Blockers, item => item.Code == "OptionRelationshipCycle" && item.Severity == SafeModDeletionBlockerSeverity.Blocking);
		Assert.Equal([root.Id], plan.LibraryNodeRemovals.Select(item => item.NodeId).ToArray());
		Assert.Empty(plan.NonSharedDescendantRemovals);
		Assert.Contains(plan.SurvivingOptionHostRelationsToStrip, item => item.OptionId == alpha.Id && item.RemovedHostId == root.Id);
	}

	[Fact]
	public void GeneratedContentAndEnabledDecorations_AreReportedAsBlockingRollbackRequirements()
	{
		var host = Node("Helmet merged host", "host", ModNodeKind.Standard, helmetToArmorMerged: true);
		var decoration = Node("Decoration", "decoration", ModNodeKind.Decoration);
		var snapshot = Snapshot(null, host, decoration);
		var safety = new SafeModDeletionSafetySnapshot(
			IsDecorationActivationStateKnown: true,
			EnabledDecorations: [new SafeModDeletionEnabledDecoration(decoration.Id, host.Id)],
			IsManagedOverwriteStateKnown: true,
			ManagedOverwrites:
			[
				new SafeModDeletionManagedOverwrite(
					SafeModDeletionManagedOverwriteKind.Decoration,
					decoration.Id,
					"host/Overwrite",
					[host.Id],
					CanSafelyRollback: false),
			]);

		var plan = Service.Plan(snapshot, [host.Id, decoration.Id], SafeModDeletionScope.Selected, safety);

		Assert.True(plan.IsBlocked);
		Assert.Contains(plan.Blockers, item => item.Code == "HelmetToArmorRollbackRequired");
		Assert.Contains(plan.Blockers, item => item.Code == "EnabledDecorationRollbackRequired");
		Assert.Contains(plan.Blockers, item => item.Code == "ManagedOverwriteRollbackRequired");
		Assert.Contains(plan.ActivationCleanupKeys, item => item.Kind == SafeModDeletionActivationCleanupKind.DecorationActivationForDecoration
			&& item.PrimaryNodeId == decoration.Id);
		Assert.Contains(plan.ActivationCleanupKeys, item => item.Kind == SafeModDeletionActivationCleanupKind.DecorationActivationForHost
			&& item.PrimaryNodeId == host.Id);
	}

	[Fact]
	public void MissingRequestAndUnsafeRelativePath_AreBlocking_WithoutHidingTheExistingRemoval()
	{
		var unsafeNode = Node("Unsafe", "..\\outside", ModNodeKind.Standard);
		var missing = ModNodeId.New();
		var snapshot = Snapshot(null, unsafeNode);

		var plan = Service.Plan(snapshot, [unsafeNode.Id, missing], SafeModDeletionScope.Selected, SafeModDeletionSafetySnapshot.EmptyKnown);

		Assert.True(plan.IsBlocked);
		Assert.Equal([unsafeNode.Id], plan.LibraryNodeRemovals.Select(item => item.NodeId).ToArray());
		Assert.Contains(plan.RequestedNodes, item => item.NodeId == missing && !item.Exists && !item.WillRemove);
		Assert.Contains(plan.Blockers, item => item.Code == "RequestedNodeMissing");
		Assert.Contains(plan.Blockers, item => item.Code == "UnsafePhysicalRelativePath");
		var path = Assert.Single(plan.PhysicalRelativePaths);
		Assert.False(path.IsSafeRelativePath);
	}

	[Fact]
	public void MalformedHostMetadata_BlocksCascade_AndKeepsTheOptionForManualRepair()
	{
		var root = Node("Root", "root", ModNodeKind.Standard);
		var malformed = Node("Malformed", "malformed", ModNodeKind.Option, [root.Id]);
		malformed = malformed with { Metadata = malformed.Metadata with { HostModGuids = [Key(root.Id), "not-a-guid"] } };
		var snapshot = Snapshot(null, root, malformed);

		var plan = Service.Plan(snapshot, [root.Id], SafeModDeletionScope.WithUnsharedOptions, SafeModDeletionSafetySnapshot.EmptyKnown);

		Assert.True(plan.IsBlocked);
		Assert.Equal([root.Id], plan.LibraryNodeRemovals.Select(item => item.NodeId).ToArray());
		Assert.Contains(plan.Blockers, item => item.Code == "InvalidOptionHostReference" && item.RelatedNodeIds.Contains(malformed.Id));
		Assert.Contains(plan.SurvivingOptionHostRelationsToStrip, item => item.OptionId == malformed.Id && item.RemovedHostId == root.Id);
	}

	[Fact]
	public void DuplicateRequestedIds_AreCollapsedButReportedInRequestedProjection()
	{
		var root = Node("Root", "root", ModNodeKind.Standard);
		var snapshot = Snapshot(null, root);

		var plan = Service.Plan(snapshot, [root.Id, root.Id], SafeModDeletionScope.Selected, SafeModDeletionSafetySnapshot.EmptyKnown);

		var request = Assert.Single(plan.RequestedNodes);
		Assert.Equal(root.Id, request.NodeId);
		Assert.Equal(2, request.RequestCount);
		Assert.True(request.Exists);
		Assert.True(request.WillRemove);
		Assert.Single(plan.LibraryNodeRemovals);
	}

	private static SafeModDeletionPlanningService Service { get; } = new();

	private static LibrarySnapshot Snapshot(Profile? profile, params ModNode[] nodes)
		=> new(
			Version: 1,
			SavedUtc: DateTimeOffset.UtcNow,
			Nodes: nodes.ToDictionary(node => node.Id),
			Profiles: profile is null ? Array.Empty<Profile>() : [profile]);

	private static ModNode Node(
		string name,
		string relativePath,
		ModNodeKind kind,
		IReadOnlyList<ModNodeId>? hosts = null,
		bool helmetToArmorMerged = false)
		=> new(
			ModNodeId.New(),
			relativePath,
			new ModNodeMetadata(
				name,
				null,
				DateTimeOffset.UtcNow,
				null,
				kind,
				hosts?.Select(Key).ToArray(),
				HelmetToArmorMerged: helmetToArmorMerged),
			Array.Empty<PatchGroupKey>(),
			Array.Empty<ModNodeId>());

	private static string Key(ModNodeId nodeId) => nodeId.Value.ToString("N");
}
