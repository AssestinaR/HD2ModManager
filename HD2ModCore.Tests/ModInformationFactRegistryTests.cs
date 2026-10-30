using HD2ModAdaptation.PatchReconstruction.PatchWorkspace;
using HD2ModCore.Application;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using AdaptationPatchTocEntry = HD2ModAdaptation.PatchReconstruction.PatchTocEntry;

namespace HD2ModCore.Tests;

// 作用：把"统一身份"（阶段 1）的完成判据固化成测试——登记表完整、读取器只有一条入口、信息中心不再手拼字符串键。
// Purpose: Pins the Stage 1 "unified identity" completion criteria: complete registry, one reader entry point, no hand-built string keys in the center.
public sealed class ModInformationFactRegistryTests
{
	// ── 判据 1：全部属性都登记 ──────────────────────────────────────────────────

	[Fact]
	public void EveryPropertyKindIsRegistered()
	{
		ModInformationFactRegistry.Validate();

		var declared = Enum.GetValues<ModInformationPropertyKind>();

		Assert.Equal(27, declared.Length);
		Assert.Equal(declared.Length, ModInformationFactRegistry.Count);
		Assert.All(declared, kind => Assert.True(
			ModInformationFactRegistry.IsRegistered(kind),
			$"新增的 ModInformationPropertyKind 未在 ModInformationFactRegistry 中登记：{kind}。"));
	}

	[Fact]
	public void DescribeResolvesEveryKindToTheSameDescriptorInstance()
	{
		foreach (var kind in Enum.GetValues<ModInformationPropertyKind>())
		{
			Assert.True(ModInformationFactRegistry.TryDescribe(kind, out var descriptor), $"未登记：{kind}。");
			Assert.NotNull(descriptor);
			Assert.Same(descriptor, ModInformationFactRegistry.Describe(kind));
		}
	}

	[Fact]
	public void DescribeRejectsAnUnregisteredKind()
		=> Assert.Throws<KeyNotFoundException>(() => ModInformationFactRegistry.Describe((ModInformationPropertyKind)9999));

	[Fact]
	public void DeclaredDependenciesOnlyReferenceRegisteredKinds()
	{
		foreach (var kind in ModInformationFactRegistry.RegisteredKinds)
		{
			foreach (var dependency in ModInformationFactRegistry.Describe(kind).Dependencies)
			{
				Assert.True(ModInformationFactRegistry.IsRegistered(dependency), $"{kind} 依赖了未登记的 {dependency}。");
				Assert.NotEqual(kind, dependency);
			}
		}
	}

	// 与 ModInformationReader.Validate 的既有约束保持一致：大对象/按需切片不得声明为可持久化。
	[Fact]
	public void HeavyPayloadPropertiesAreDeclaredOperationScoped()
	{
		foreach (var kind in new[]
		{
			ModInformationPropertyKind.PatchEntryPayload,
			ModInformationPropertyKind.UnitStructure,
			ModInformationPropertyKind.GpuPayload,
			ModInformationPropertyKind.StreamPayload,
			ModInformationPropertyKind.TocPayload,
		})
		{
			Assert.Equal(ModInformationCacheScope.Operation, ModInformationFactRegistry.Describe(kind).CacheScope);
		}
	}

	// 游戏数据侧的四条事实（设计文档 §4 #8/#9/#10/#11）都以 GameData 为主体，
	// 其余事实都是按 Mod 节点命名的。
	// 注意 DeployedGraph（#11）虽然在"部署侧"这条线上，但它的主体同样是 GameData——
	// 它读的是游戏 Data 目录与激活状态，与某个 Mod 目录无关（§22.2）。
	// ArmorCoverage（#23）是第四条 GameData 主体的事实：它由上面三条组合而来，同样不挂在任何节点上；
	// "哪一批护甲"由主体里的作用域表达，而不是靠换个主体种类。
	[Fact]
	public void OnlyTheGameDataFactsUseTheGameDataSubject()
	{
		ModInformationPropertyKind[] gameDataKinds =
		[
			ModInformationPropertyKind.GameDataIndex,
			ModInformationPropertyKind.AssetMetadata,
			ModInformationPropertyKind.GameDataMapping,
			ModInformationPropertyKind.DeployedGraph,
			ModInformationPropertyKind.ArmorCoverage,
			ModInformationPropertyKind.UnitSectionStructure,
			ModInformationPropertyKind.ArmourUnitTable,
		];

		Assert.All(gameDataKinds, kind => Assert.Equal(
			FactSubjectKind.GameData,
			ModInformationFactRegistry.Describe(kind).SubjectKind));

		Assert.All(
			ModInformationFactRegistry.RegisteredKinds.Where(kind => !gameDataKinds.Contains(kind)),
			kind => Assert.Equal(FactSubjectKind.Node, ModInformationFactRegistry.Describe(kind).SubjectKind));
	}

	// #10 由 #8 与 #9 拼出：去掉任一条边，映射就无法定位 archive 或拿到显示名。
	[Fact]
	public void GameDataMappingDependsOnBothGameDataInputs()
	{
		var dependencies = ModInformationFactRegistry.Describe(ModInformationPropertyKind.GameDataMapping).Dependencies;

		Assert.Contains(ModInformationPropertyKind.GameDataIndex, dependencies);
		Assert.Contains(ModInformationPropertyKind.AssetMetadata, dependencies);
	}

	// 两个游戏数据输入事实直接来自游戏目录/元数据文件，必须被登记为源事实。
	[Fact]
	public void GameDataInputsAreDeclaredAsSourceFacts()
	{
		Assert.Contains(ModInformationPropertyKind.GameDataIndex, ModInformationFactRegistry.SourceRoots);
		Assert.Contains(ModInformationPropertyKind.AssetMetadata, ModInformationFactRegistry.SourceRoots);
		Assert.DoesNotContain(ModInformationPropertyKind.GameDataMapping, ModInformationFactRegistry.SourceRoots);
	}

	// #23 护甲覆盖由 #8/#9/#10/#11 组合而来，而**图标不在其中**——这是 §7 护栏 3 的结构半边。
	//
	// 把缩略图变成覆盖事实的依赖（哪怕只是间接依赖），"换个 Mod 图标"就会变成"覆盖事实重算"，
	// 与护栏要的效果正好相反。图标是对赢家节点的库内查找（内存字典 + 父链），代价可忽略，
	// 所以它留在页面里做，不进事实（§23.4）。
	[Fact]
	public void ArmorCoverageDependsOnItsThreeUpstreamsAndNeverOnThumbnails()
	{
		var dependencies = ModInformationFactRegistry.Describe(ModInformationPropertyKind.ArmorCoverage).Dependencies;

		Assert.Contains(ModInformationPropertyKind.GameDataIndex, dependencies);
		Assert.Contains(ModInformationPropertyKind.GameDataMapping, dependencies);
		Assert.Contains(ModInformationPropertyKind.DeployedGraph, dependencies);

		// 直接依赖与**传递闭包**都要检查：经由任何中间事实绕到 Thumbnail 一样会破坏护栏。
		Assert.DoesNotContain(ModInformationPropertyKind.Thumbnail, dependencies);
		Assert.DoesNotContain(
			ModInformationPropertyKind.Thumbnail,
			ModInformationFactRegistry.Graph.TransitiveDependencies(ModInformationPropertyKind.ArmorCoverage));
	}

	// 与上一条配套：覆盖事实的**生产代码**里也不得出现缩略图。
	// 登记表管得住声明，管不住实现——有人完全可以在生产方法里顺手读一次缩略图，
	// 依赖闭包测试不会发现。这里把这一段生产代码（含它委派的那两个方法）整体抠出来看。
	[Fact]
	public void ArmorCoverageProductionNeverTouchesThumbnails()
	{
		var center = ReadSource("HD2ModCore", "Infrastructure", "ModInformationCenter.cs");
		var start = center.IndexOf("private async Task<ModInformationResult<object>> ProduceArmorCoverageAndRemoveAsync", StringComparison.Ordinal);
		var end = center.IndexOf("private async Task<ModInformationResult<object>> ProduceDeployedGraphAndRemoveAsync", StringComparison.Ordinal);

		Assert.True(start >= 0 && end > start, "无法定位护甲覆盖的生产代码。");
		var body = center[start..end];

		Assert.DoesNotContain("Thumbnail", body);
		Assert.DoesNotContain("_thumbnailProducer", body);
		// 反向锚点：确认抠出来的确实是那条生产链路，不是一段空壳。
		Assert.Contains("RequestGameDataIndexAsync", body);
		Assert.Contains("RequestDeployedGraphAsync", body);
		Assert.Contains("RequestGameDataMappingAsync", body);
		Assert.Contains("ArmorCoverageProjection.Project", body);
	}

	// ── 判据 6（阶段 2）：依赖边逐条声明，且依赖图是一张无环 DAG ────────────────

	// 空依赖 ⟺ 显式登记为源事实。这样"新增 kind 却忘了写依赖"会立刻失败，
	// 而"确实没有上游事实"必须由人显式登记进 SourceRoots，不会被静默放过。
	[Fact]
	public void EveryKindEitherDeclaresDependenciesOrIsAReviewedSourceRoot()
	{
		foreach (var kind in ModInformationFactRegistry.RegisteredKinds)
		{
			var hasDependencies = ModInformationFactRegistry.Describe(kind).Dependencies.Count > 0;
			var isDeclaredSource = ModInformationFactRegistry.SourceRoots.Contains(kind);

			Assert.True(
				hasDependencies != isDeclaredSource,
				$"{kind} 的依赖声明与源事实登记不一致（依赖数 {ModInformationFactRegistry.Describe(kind).Dependencies.Count}，"
				+ $"是否登记为源事实 {isDeclaredSource}）。");
		}
	}

	// 逐条锁住"读代码坐实"的那批边——这些是 D3 的成果，改动它们必须是有意识的。
	[Theory]
	[InlineData(ModInformationPropertyKind.PatchEntryPayload, ModInformationPropertyKind.PatchCatalog)]
	[InlineData(ModInformationPropertyKind.AssetInventory, ModInformationPropertyKind.PatchCatalog)]
	[InlineData(ModInformationPropertyKind.UnitCatalog, ModInformationPropertyKind.AssetInventory)]
	[InlineData(ModInformationPropertyKind.UnitGeometrySummary, ModInformationPropertyKind.UnitStructure)]
	[InlineData(ModInformationPropertyKind.UnitStructure, ModInformationPropertyKind.PatchCatalog)]
	[InlineData(ModInformationPropertyKind.UnitStructure, ModInformationPropertyKind.PatchEntryPayload)]
	[InlineData(ModInformationPropertyKind.ReferenceEdges, ModInformationPropertyKind.PatchEntryPayload)]
	[InlineData(ModInformationPropertyKind.ReferenceClosure, ModInformationPropertyKind.ReferenceEdges)]
	[InlineData(ModInformationPropertyKind.MaterialDependencyClosure, ModInformationPropertyKind.ReferenceEdges)]
	[InlineData(ModInformationPropertyKind.CompositeBoneDependencyClosure, ModInformationPropertyKind.ReferenceEdges)]
	[InlineData(ModInformationPropertyKind.GpuPayload, ModInformationPropertyKind.PatchEntryPayload)]
	[InlineData(ModInformationPropertyKind.StreamPayload, ModInformationPropertyKind.PatchEntryPayload)]
	[InlineData(ModInformationPropertyKind.TocPayload, ModInformationPropertyKind.PatchEntryPayload)]
	[InlineData(ModInformationPropertyKind.Compatibility, ModInformationPropertyKind.AssetInventory)]
	[InlineData(ModInformationPropertyKind.Maintenance, ModInformationPropertyKind.AssetInventory)]
	[InlineData(ModInformationPropertyKind.GameDataMapping, ModInformationPropertyKind.GameDataIndex)]
	[InlineData(ModInformationPropertyKind.GameDataMapping, ModInformationPropertyKind.AssetMetadata)]
	public void DeclaredDependencyEdgeExists(ModInformationPropertyKind kind, ModInformationPropertyKind dependency)
		=> Assert.Contains(dependency, ModInformationFactRegistry.Describe(kind).Dependencies);

	// Maintenance 是本表里唯一一条"中心内部真实按依赖顺序取上游"的边，单独锁住防止回退。
	[Fact]
	public void MaintenanceIsTheEdgeTheCenterActuallyRequestsThroughTheCenter()
	{
		var center = ReadSource("HD2ModCore", "Infrastructure", "ModInformationCenter.cs");

		Assert.Contains("RequestAssetInventoryAsync(node, root, inventoryRequest", center);
		Assert.Contains("_maintenanceProducer!.ProduceAsync(node, inventory.Data", center);
	}

	[Fact]
	public void DependencyGraphIsAcyclic()
	{
		var graph = ModInformationFactRegistry.Graph;

		Assert.True(graph.IsAcyclic, "登记表声明的依赖边形成了环。");
		Assert.Equal(ModInformationFactRegistry.Count, graph.TopologicalOrder.Count);
	}

	// 拓扑序里"上游一定排在下游之前"，否则按序生产就会出现"用到还没算出来的东西"。
	[Fact]
	public void TopologicalOrderPlacesEveryDependencyBeforeItsDependent()
	{
		var graph = ModInformationFactRegistry.Graph;
		var position = graph.TopologicalOrder
			.Select((kind, index) => (kind, index))
			.ToDictionary(item => item.kind, item => item.index);

		foreach (var kind in graph.Nodes)
		{
			foreach (var dependency in graph.DirectDependencies(kind))
				Assert.True(
					position[dependency] < position[kind],
					$"拓扑序把 {kind} 排在了它的上游 {dependency} 之前。");
		}
	}

	[Fact]
	public void TransitiveClosureCoversEveryIndirectUpstream()
	{
		var graph = ModInformationFactRegistry.Graph;

		// UnitGeometrySummary → UnitStructure → {PatchCatalog, PatchEntryPayload} → PatchCatalog
		var closure = graph.TransitiveDependencies(ModInformationPropertyKind.UnitGeometrySummary);

		Assert.Contains(ModInformationPropertyKind.UnitStructure, closure);
		Assert.Contains(ModInformationPropertyKind.PatchEntryPayload, closure);
		Assert.Contains(ModInformationPropertyKind.PatchCatalog, closure);
		Assert.DoesNotContain(ModInformationPropertyKind.UnitGeometrySummary, closure);

		// 源事实的闭包必须是空的。
		Assert.All(
			ModInformationFactRegistry.SourceRoots,
			kind => Assert.Empty(graph.TransitiveDependencies(kind)));
	}

	// 图的环检测本身要可靠：给它一张人为造的环，必须报出来而不是静默少节点。
	[Fact]
	public void GraphReportsCyclesInsteadOfSilentlyDroppingNodes()
	{
		var descriptors = new Dictionary<ModInformationPropertyKind, FactDescriptor>
		{
			[ModInformationPropertyKind.Identity] = new(
				FactSubjectKind.Node, ModInformationCacheScope.Persistent, FactProducerKind.Source,
				[ModInformationPropertyKind.ContentRevision]),
			[ModInformationPropertyKind.ContentRevision] = new(
				FactSubjectKind.Node, ModInformationCacheScope.Persistent, FactProducerKind.Source,
				[ModInformationPropertyKind.Identity]),
		};

		var graph = ModInformationFactGraph.Build(descriptors);

		Assert.False(graph.IsAcyclic);
		Assert.Equal(2, graph.TopologicalOrder.Count);
	}

	[Fact]
	public void GraphComputesClosureAndOrderOnASyntheticChain()
	{
		var descriptors = new Dictionary<ModInformationPropertyKind, FactDescriptor>
		{
			[ModInformationPropertyKind.Identity] = new(
				FactSubjectKind.Node, ModInformationCacheScope.Persistent, FactProducerKind.Source,
				[ModInformationPropertyKind.ContentRevision]),
			[ModInformationPropertyKind.ContentRevision] = new(
				FactSubjectKind.Node, ModInformationCacheScope.Persistent, FactProducerKind.Source,
				[ModInformationPropertyKind.PatchCatalog]),
			[ModInformationPropertyKind.PatchCatalog] = new(
				FactSubjectKind.Node, ModInformationCacheScope.Persistent, FactProducerKind.PatchCatalogReader),
		};

		var graph = ModInformationFactGraph.Build(descriptors);

		Assert.True(graph.IsAcyclic);
		Assert.Equal(
			[ModInformationPropertyKind.PatchCatalog, ModInformationPropertyKind.ContentRevision, ModInformationPropertyKind.Identity],
			graph.TopologicalOrder);
		Assert.Equal(
			[ModInformationPropertyKind.ContentRevision, ModInformationPropertyKind.PatchCatalog],
			graph.TransitiveDependencies(ModInformationPropertyKind.Identity));
	}

	// 依赖边指向未登记的 kind 时必须被剔除，而不是让图里出现悬空节点。
	[Fact]
	public void GraphIgnoresDependenciesThatAreNotRegistered()
	{
		var descriptors = new Dictionary<ModInformationPropertyKind, FactDescriptor>
		{
			[ModInformationPropertyKind.Identity] = new(
				FactSubjectKind.Node, ModInformationCacheScope.Persistent, FactProducerKind.Source,
				[(ModInformationPropertyKind)9999]),
		};

		var graph = ModInformationFactGraph.Build(descriptors);

		Assert.Empty(graph.DirectDependencies(ModInformationPropertyKind.Identity));
		Assert.Single(graph.Nodes);
	}

	// ── 身份类型本身的行为 ──────────────────────────────────────────────────────

	[Fact]
	public void FactKeySeparatesSubjectFromAspect()
	{
		var node = ModNodeId.New();
		var other = ModNodeId.New();

		Assert.Equal(
			FactKey.ForNode(node, ModInformationPropertyKind.AssetInventory),
			FactKey.ForNode(node, ModInformationPropertyKind.AssetInventory));
		Assert.NotEqual(
			FactKey.ForNode(node, ModInformationPropertyKind.AssetInventory),
			FactKey.ForNode(node, ModInformationPropertyKind.Thumbnail));
		Assert.NotEqual(
			FactKey.ForNode(node, ModInformationPropertyKind.AssetInventory),
			FactKey.ForNode(other, ModInformationPropertyKind.AssetInventory));
	}

	[Fact]
	public void MissingNodeFallsBackToTheEmptyGuidLikeTheOldKey()
		=> Assert.Equal(FactSubject.Node(new ModNodeId(Guid.Empty)), FactKey.ForNode(null, ModInformationPropertyKind.Identity).Subject);

	// 旧实现用 "StartsWith(\"{nodeId}|\")" 做前缀匹配，本用例锁住结构化匹配不会误命中别的主体。
	[Fact]
	public void NodeSubjectMatchIsStructuralRatherThanPrefixBased()
	{
		var node = ModNodeId.New();

		Assert.True(FactSubject.Node(node).IsNode(node));
		Assert.False(FactSubject.Node(ModNodeId.New()).IsNode(node));
		Assert.False(FactSubject.Library().IsNode(node));
		Assert.False(FactSubject.GameData().IsNode(node));
		Assert.False(FactSubject.GameAsset(1, 2).IsNode(node));
	}

	[Fact]
	public void LibrarySnapshotSubjectIsOrderIndependent()
	{
		var first = ModNodeId.New();
		var second = ModNodeId.New();

		Assert.Equal(FactSubject.LibrarySnapshot([first, second]), FactSubject.LibrarySnapshot([second, first]));
		Assert.NotEqual(FactSubject.Library(), FactSubject.LibrarySnapshot([first]));
		Assert.NotEqual(FactSubject.LibrarySnapshot([first]), FactSubject.LibrarySnapshot([second]));
	}

	[Fact]
	public void RevisionStaysOutsideTheFactIdentity()
	{
		var key = FactKey.ForNode(ModNodeId.New(), ModInformationPropertyKind.Thumbnail);

		Assert.Equal(new FactRevisionKey(key, "a"), new FactRevisionKey(key, "a"));
		Assert.NotEqual(new FactRevisionKey(key, "a"), new FactRevisionKey(key, "b"));
		Assert.Equal(key, new FactRevisionKey(key, "a").Key);
	}

	[Fact]
	public void CacheKeyReportsWhetherItBelongsToANode()
	{
		var node = ModNodeId.New();
		var revision = ModContentRevision.Create("revision");

		Assert.True(ModInformationCacheKey.Create(FactKey.ForNode(node, ModInformationPropertyKind.Thumbnail), revision).BelongsTo(node));
		Assert.False(ModInformationCacheKey.Create(FactKey.ForNode(ModNodeId.New(), ModInformationPropertyKind.Thumbnail), revision).BelongsTo(node));
	}

	// ── 判据 2：5 个具名读取方法全部经由 RequestAsync<T>(FactKey) ─────────────────

	[Fact]
	public void EveryNamedReadForwardsToTheSingleRequestEntryPoint()
	{
		var source = ReadSource("HD2ModCore", "Infrastructure", "ModInformationReader.cs");
		var members = SplitMembers(source);

		foreach (var name in new[]
		{
			"ReadPatchIndexAsync",
			"ReadPatchPayloadAsync",
			"ReadUnitAsync",
			"ReadUnitSummaryAsync",
			"ReadSourceUnitFactsAsync",
		})
		{
			var matches = members.Where(member => member.Contains($"{name}(\n", StringComparison.Ordinal)).ToArray();
			var body = Assert.Single(matches);
			Assert.Contains("RequestAsync(", body);
		}

		// 真正的读取管道只允许存在一处：RequestAsync 内部。
		Assert.Equal(5, CountOccurrences(source, "return RequestAsync("));
		Assert.Equal(1, CountOccurrences(source, "TryGetCacheAsync<T>(cacheKey"));
		Assert.Equal(1, CountOccurrences(source, "ReadCoalescedAsync(cacheKey"));
		Assert.Equal(1, CountOccurrences(source, "CompleteAsync(cacheKey"));
		Assert.DoesNotContain("key.NodeId", source);
		Assert.DoesNotContain("key.Property", source);
	}

	// 行为证明：直接调用统一入口与调用具名方法命中同一条缓存记录。
	[Fact]
	public async Task DirectRequestAndNamedReadShareTheSameCacheEntry()
	{
		await using var reader = new ModInformationReader(new StubWorkspaceReader());
		var context = ModInformationRequestContext.Create(ModInformationCacheScope.Operation, operationId: Guid.NewGuid());
		var request = new ModInformationReadRequest("shared-entry.patch", context);

		var direct = await reader.RequestAsync(
			FactKey.ForNode(null, ModInformationPropertyKind.PatchCatalog),
			request,
			new FactReadOperation<PatchWorkspaceIndex>(
				request.SourcePath,
				token => Task.FromResult(new FactReadOutcome<PatchWorkspaceIndex>(new PatchWorkspaceIndex(request.SourcePath, [], [1]), 8))));

		var named = await reader.ReadPatchIndexAsync(request);

		Assert.Equal(ModInformationPropertyStatus.Fresh, direct.State.Status);
		Assert.Equal(ModInformationPropertyKind.PatchCatalog, direct.State.Kind);
		Assert.Equal(ModInformationPropertyStatus.Cached, named.State.Status);
		Assert.Same(direct.Data, named.Data);
	}

	// ── 判据 3：信息中心内部不再手拼 "{nodeId}|{kind}|{generation}" ──────────────

	[Fact]
	public void InformationCenterNoLongerBuildsStringCacheKeys()
	{
		var source = ReadSource("HD2ModCore", "Infrastructure", "ModInformationCenter.cs");

		Assert.DoesNotContain("|{effectiveRequest.Kind}|", source);
		Assert.DoesNotContain("StartsWith($\"{nodeId}|\"", source);
		Assert.DoesNotContain("ConcurrentDictionary<string,", source);
		Assert.Contains("ConcurrentDictionary<FactRevisionKey,", source);
	}

	private static string[] SplitMembers(string source)
		=> source.Split("\n\tpublic ", StringSplitOptions.RemoveEmptyEntries);

	private static int CountOccurrences(string source, string value)
	{
		var count = 0;
		var index = source.IndexOf(value, StringComparison.Ordinal);
		while (index >= 0)
		{
			count++;
			index = source.IndexOf(value, index + value.Length, StringComparison.Ordinal);
		}
		return count;
	}

	// 统一成 LF 再交给断言：本文件里的扫描都是"跨行片段"（"\n\tpublic "、"签名(\n"），
	// 一旦读到的源码是 CRLF，这些片段就永远匹配不上，断言会退化成"集合为空"。
	// 工作区到底是 LF 还是 CRLF 取决于 checkout 时机器的 core.autocrlf，
	// 所以这里不能假设——不归一化就等于让测试结果随开发机配置变化（CI 就是这么红的）。
	// Normalize to LF before asserting: the scans here are multi-line snippets
	// ("\n\tpublic ", "signature(\n"), which can never match CRLF text and degrade the
	// assertions into "empty collection". Whether the worktree is LF or CRLF depends on
	// core.autocrlf at checkout time, so it must not be assumed here — without
	// normalization the test result follows the developer's machine config (which is
	// exactly how CI went red).
	private static string ReadSource(params string[] relativeSegments)
		=> File.ReadAllText(Path.Combine([FindRepositoryRoot(), .. relativeSegments]))
			.Replace("\r\n", "\n", StringComparison.Ordinal);

	private static string FindRepositoryRoot()
	{
		var directory = new DirectoryInfo(AppContext.BaseDirectory);
		while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WpfApp1.sln")))
			directory = directory.Parent;
		return directory?.FullName ?? throw new DirectoryNotFoundException("Unable to locate the repository root.");
	}

	private sealed class StubWorkspaceReader : IPatchWorkspaceReader
	{
		public ValueTask<IReadOnlyList<AdaptationPatchTocEntry>> ReadEntriesAsync(string sourcePatchTocPath, CancellationToken cancellationToken = default)
			=> ValueTask.FromResult<IReadOnlyList<AdaptationPatchTocEntry>>([]);

		public ValueTask<PatchWorkspaceIndex> ReadIndexAsync(string sourcePatchTocPath, CancellationToken cancellationToken = default)
			=> ValueTask.FromResult(new PatchWorkspaceIndex(sourcePatchTocPath, [], [1]));

		public ValueTask<PatchWorkspace> ReadAsync(string sourcePatchTocPath, CancellationToken cancellationToken = default)
			=> ValueTask.FromResult(new PatchWorkspace(sourcePatchTocPath, [], []));
	}
}
