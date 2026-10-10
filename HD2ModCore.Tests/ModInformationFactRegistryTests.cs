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
	// ── 判据 1：21 个属性全部登记 ────────────────────────────────────────────────

	[Fact]
	public void EveryPropertyKindIsRegistered()
	{
		ModInformationFactRegistry.Validate();

		var declared = Enum.GetValues<ModInformationPropertyKind>();

		Assert.Equal(21, declared.Length);
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

	[Fact]
	public void GameDataMappingIsTheOnlyGameDataScopedFact()
	{
		Assert.Equal(
			FactSubjectKind.GameData,
			ModInformationFactRegistry.Describe(ModInformationPropertyKind.GameDataMapping).SubjectKind);

		Assert.All(
			ModInformationFactRegistry.RegisteredKinds.Where(kind => kind != ModInformationPropertyKind.GameDataMapping),
			kind => Assert.Equal(FactSubjectKind.Node, ModInformationFactRegistry.Describe(kind).SubjectKind));
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

	private static string ReadSource(params string[] relativeSegments)
		=> File.ReadAllText(Path.Combine([FindRepositoryRoot(), .. relativeSegments]));

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
