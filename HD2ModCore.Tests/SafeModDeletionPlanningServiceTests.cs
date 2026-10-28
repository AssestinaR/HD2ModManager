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

	// 直接请求删除一个仍被存活主体引用的 Option：必须可删（不产生 Blocking），
	// 但要留一条警告，让确认流程能告诉用户"删掉之后主体还在、只是少了这个选项"。
	// A directly requested Option with a surviving host must be removable, with a warning.
	[Fact]
	public void DirectlyRequestedOption_WithSurvivingHost_IsRemovable_AndReportedAsWarning()
	{
		var host = Node("Host", "host", ModNodeKind.Standard);
		var option = Node("Option", "option", ModNodeKind.Option, [host.Id]);
		var snapshot = Snapshot(null, host, option);

		var plan = Service.Plan(snapshot, [option.Id], SafeModDeletionScope.Selected, SafeModDeletionSafetySnapshot.EmptyKnown);

		Assert.False(plan.IsBlocked);
		Assert.Equal([option.Id], plan.LibraryNodeRemovals.Select(item => item.NodeId).ToArray());
		var warning = Assert.Single(plan.Blockers, item => item.Code == "OptionSharedAcrossHosts");
		Assert.Equal(SafeModDeletionBlockerSeverity.Warning, warning.Severity);
		Assert.Contains(option.Id, warning.RelatedNodeIds);
		Assert.Contains(host.Id, warning.RelatedNodeIds);
		// 宿主没有任何需要回写的"子节点清单"，所以不存在残留关系要剥。
		Assert.Empty(plan.SurvivingOptionHostRelationsToStrip);
	}

	// 孤儿选项（父级早就不在了，只剩一条野引用）以前会因为 InvalidOptionHostReference
	// 被判 Blocking，于是永远删不掉。它自己就要消失，坏元数据跟着一起消失，不该拦。
	// An orphaned Option carrying a dangling host reference must still be deletable.
	[Fact]
	public void DirectlyRequestedOption_WithDanglingHostReference_IsNotBlocked()
	{
		var host = Node("Host", "host", ModNodeKind.Standard);
		var orphan = Node("Orphan", "orphan", ModNodeKind.Option, [host.Id]);
		orphan = orphan with { Metadata = orphan.Metadata with { HostModGuids = [Key(ModNodeId.New())] } };
		var snapshot = Snapshot(null, host, orphan);

		var plan = Service.Plan(snapshot, [orphan.Id], SafeModDeletionScope.Selected, SafeModDeletionSafetySnapshot.EmptyKnown);

		Assert.False(plan.IsBlocked);
		Assert.DoesNotContain(plan.Blockers, item => item.Code == "InvalidOptionHostReference");
		Assert.Equal([orphan.Id], plan.LibraryNodeRemovals.Select(item => item.NodeId).ToArray());
	}

	// 反过来：留下来的 Option 带着坏引用时必须继续拦，那是真的需要人工修。
	// （可达性由"子节点指向父节点"的关系决定，所以这里的坏引用只能是一条合法的宿主
	// 加一条垃圾值；纯野引用的选项根本不可达，也就不会被卷进这次删除。）
	// A surviving Option with a malformed host reference must still block.
	[Fact]
	public void SurvivingOption_WithMalformedHostReference_StillBlocks()
	{
		var host = Node("Host", "host", ModNodeKind.Standard);
		var broken = Node("Broken", "broken", ModNodeKind.Option, [host.Id]);
		broken = broken with { Metadata = broken.Metadata with { HostModGuids = [Key(host.Id), "not-a-guid"] } };
		var snapshot = Snapshot(null, host, broken);

		var plan = Service.Plan(snapshot, [host.Id], SafeModDeletionScope.WithUnsharedOptions, SafeModDeletionSafetySnapshot.EmptyKnown);

		Assert.True(plan.IsBlocked);
		Assert.Contains(plan.Blockers, item => item.Code == "InvalidOptionHostReference" && item.RelatedNodeIds.Contains(broken.Id));
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
