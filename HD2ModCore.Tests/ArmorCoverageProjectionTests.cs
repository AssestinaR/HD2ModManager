using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using AdaptationUnitMesh = HD2ModAdaptation.PatchReconstruction.UnitMesh.PatchUnitMeshReader;

namespace HD2ModCore.Tests;

// 作用：覆盖护甲覆盖视图的投影语义（设计文档 §4 #23、§23）。
// Purpose: Covers the projection semantics of the armor coverage view.
//
// 这些用例钉的是**判定**而不是实现：哪件护甲算被覆盖、谁算赢家、什么情况下必须回答"未知"而不是"没有"。
// 改造前这些判定散在护甲页的取数循环里，现在集中在 ArmorCoverageProjection 一处，于是可以逐条测。
public sealed class ArmorCoverageProjectionTests
{
	private static readonly ulong UnitType = AdaptationUnitMesh.UnitTypeId;
	private const ulong OtherType = 0x1234;

	[Fact]
	public void ADeployedUnitMakesTheArchiveCoveredByItsWinner()
	{
		var winner = ModNodeId.New();
		var key = new AssetKey(UnitType, 1);

		var facts = Project(
			Index("armor-1"),
			Mapping((key, "armor-1")),
			Deployed((key, winner, 0)),
			"armor-1");

		var entry = facts.Archives["armor-1"];
		Assert.True(entry.HasEffectiveUnitOverride);
		Assert.Equal(winner, entry.UnitWinnerNodeId);
		Assert.Equal(winner, entry.CoverWinnerNodeId);
	}

	[Fact]
	public void AnArchiveKnownToTheIndexButNotDeployedIsReportedAsNotCovered()
	{
		var key = new AssetKey(UnitType, 1);

		// 索引里有 armor-1，但没有任何已部署资产落在它上面。
		var facts = Project(
			Index("armor-1"),
			Mapping((key, "armor-2")),
			Deployed((key, ModNodeId.New(), 0)),
			"armor-1");

		// 必须给出**确定**的否定（false），而不是漏掉这条——漏掉会让页面显示成"未知"，
		// 于是"未命中 Unit 覆盖"这个筛选器再也筛不出它。
		var entry = facts.Archives["armor-1"];
		Assert.False(entry.HasEffectiveUnitOverride);
		Assert.Null(entry.UnitWinnerNodeId);
		Assert.Null(entry.CoverWinnerNodeId);
	}

	[Fact]
	public void AnArchiveMissingFromTheIndexIsAbsentSoThePageKeepsSayingUnknown()
	{
		var key = new AssetKey(UnitType, 1);

		// armor-9 不在索引里。索引里只有 armor-1。
		var facts = Project(
			Index("armor-1"),
			Mapping((key, "armor-9")),
			Deployed((key, ModNodeId.New(), 0)),
			"armor-1", "armor-9");

		// 读不到索引里的归档 ≠ 这件护甲没有被覆盖。把它写成 false 就是把"缺失"误判成"否定事实"。
		Assert.DoesNotContain("armor-9", facts.Archives.Keys);
		Assert.True(facts.Archives.ContainsKey("armor-1"));
	}

	[Fact]
	public void OnlyTheRequestedArchivesAreReported()
	{
		var key = new AssetKey(UnitType, 1);

		var facts = Project(
			Index("armor-1", "armor-2"),
			Mapping((key, "armor-2")),
			Deployed((key, ModNodeId.New(), 0)),
			"armor-1");

		// 问什么答什么：调用方没问 armor-2，事实里就不该出现它。
		Assert.Equal("armor-1", Assert.Single(facts.Archives.Keys));
	}

	[Fact]
	public void ADeployedChainWithoutAnOwningNodeDoesNotCountAsCoverage()
	{
		var key = new AssetKey(UnitType, 1);

		// Data 里存在这个补丁，但激活状态没有记录它属于哪个 Mod（赢家为 null）。
		var facts = Project(
			Index("armor-1"),
			Mapping((key, "armor-1")),
			Deployed((key, null, 0)),
			"armor-1");

		var entry = facts.Archives["armor-1"];
		Assert.False(entry.HasEffectiveUnitOverride);
		Assert.Null(entry.CoverWinnerNodeId);
	}

	[Fact]
	public void ADeployedChainWithoutAnOwningNodeCannotOutrankARealOwner()
	{
		var owner = ModNodeId.New();
		var owned = new AssetKey(UnitType, 1);
		var orphans = new[] { new AssetKey(UnitType, 2), new AssetKey(UnitType, 3), new AssetKey(UnitType, 4) };

		// 三条"无主"资产比一条"有主"资产多。赢家排序先看条数，所以如果不把无主链排除在外，
		// 这件护甲就会被判成"没有被覆盖"——一个从"读不到归属"推出来的否定事实。
		var facts = Project(
			Index("armor-1"),
			Mapping((owned, "armor-1"), (orphans[0], "armor-1"), (orphans[1], "armor-1"), (orphans[2], "armor-1")),
			Deployed((owned, owner, 0), (orphans[0], null, 1), (orphans[1], null, 1), (orphans[2], null, 1)),
			"armor-1");

		var entry = facts.Archives["armor-1"];
		Assert.True(entry.HasEffectiveUnitOverride);
		Assert.Equal(owner, entry.UnitWinnerNodeId);
		Assert.Equal(owner, entry.CoverWinnerNodeId);
	}

	[Fact]
	public void ANonUnitAssetStillNamesACoverWinnerButDoesNotCountAsAUnitOverride()
	{
		var winner = ModNodeId.New();
		var key = new AssetKey(OtherType, 1);

		var facts = Project(
			Index("armor-1"),
			Mapping((key, "armor-1")),
			Deployed((key, winner, 0)),
			"armor-1");

		// 两个字段回答两个不同的问题：外观被谁改了（有答案），模型是否被换（没有）。
		var entry = facts.Archives["armor-1"];
		Assert.False(entry.HasEffectiveUnitOverride);
		Assert.Null(entry.UnitWinnerNodeId);
		Assert.Equal(winner, entry.CoverWinnerNodeId);
	}

	[Fact]
	public void TheWinnerIsTheNodeCoveringTheMostUnitAssetsInThatArchive()
	{
		var few = ModNodeId.New();
		var many = ModNodeId.New();
		var keys = new[] { new AssetKey(UnitType, 1), new AssetKey(UnitType, 2), new AssetKey(UnitType, 3) };

		var facts = Project(
			Index("armor-1"),
			Mapping((keys[0], "armor-1"), (keys[1], "armor-1"), (keys[2], "armor-1")),
			Deployed((keys[0], few, 0), (keys[1], many, 0), (keys[2], many, 0)),
			"armor-1");

		// 覆盖的资产条数多者胜——这与改造前护甲页的排序口径逐字一致。
		Assert.Equal(many, facts.Archives["armor-1"].UnitWinnerNodeId);
	}

	[Fact]
	public void ATieOnAssetCountIsBrokenByTheHigherTargetPatchIndex()
	{
		var older = ModNodeId.New();
		var newer = ModNodeId.New();
		var keys = new[] { new AssetKey(UnitType, 1), new AssetKey(UnitType, 2) };

		var facts = Project(
			Index("armor-1"),
			Mapping((keys[0], "armor-1"), (keys[1], "armor-1")),
			Deployed((keys[0], older, 3), (keys[1], newer, 9)),
			"armor-1");

		// 同样覆盖一条时，目标补丁索引更大的那个是最终生效者。
		Assert.Equal(newer, facts.Archives["armor-1"].UnitWinnerNodeId);
	}

	[Fact]
	public void MappingWithoutAnyTargetArchiveDoesNotInventCoverage()
	{
		var key = new AssetKey(UnitType, 1);

		// 资产键在索引里查不到目标归档（空列表），于是没有任何护甲被它覆盖。
		var facts = Project(
			Index("armor-1"),
			Mapping((key, null)),
			Deployed((key, ModNodeId.New(), 0)),
			"armor-1");

		Assert.False(facts.Archives["armor-1"].HasEffectiveUnitOverride);
	}

	[Fact]
	public void TheGenerationTracksEveryInputSoAStaleBatchCannotLookFresh()
	{
		var key = new AssetKey(UnitType, 1);
		var index = Index("armor-1");
		var mapping = Mapping((key, "armor-1"));
		var deployed = Deployed((key, ModNodeId.New(), 0));

		var baseline = ArmorCoverageProjection.Project(index, mapping, deployed, new HashSet<string> { "armor-1" });
		var sameAgain = ArmorCoverageProjection.Project(index, mapping, deployed, new HashSet<string> { "armor-1" });
		var otherMapping = ArmorCoverageProjection.Project(index, mapping with { MappingGeneration = "other" }, deployed, new HashSet<string> { "armor-1" });

		Assert.Equal(baseline.CoverageGeneration, sameAgain.CoverageGeneration);
		Assert.NotEqual(baseline.CoverageGeneration, otherMapping.CoverageGeneration);
	}

	private static ArmorCoverageFacts Project(
		GameDataIndexFacts index,
		GameDataMappingFacts mapping,
		DeployedOverrideGraph deployed,
		params string[] archiveIds)
		=> ArmorCoverageProjection.Project(index, mapping, deployed, archiveIds.ToHashSet(StringComparer.OrdinalIgnoreCase));

	private static GameDataIndexFacts Index(params string[] packageNames)
		=> new(
			new GameDataIndexFingerprint("data", DateTimeOffset.UtcNow, packageNames.Length, packageNames.Length, 0, "index-fingerprint"),
			packageNames.Select(name => new GameDataArchiveSummary(name, name, "Armor", 1, "正常")).ToArray());

	// 目标归档传 null 表示"这个资产键在索引里查不到任何归档"。
	private static GameDataMappingFacts Mapping(params (AssetKey Key, string? Archive)[] targets)
		=> new(
			"mapping-generation",
			"index-generation",
			"metadata-generation",
			DateTimeOffset.UtcNow,
			targets.ToDictionary(
				target => target.Key,
				target => new GameDataMappedAssetFact(
					target.Key,
					"file",
					"type",
					AssetTypeCategory.Model,
					target.Archive is null ? [] : [new ArchiveMetadata(target.Archive, "Armor", target.Archive)])),
			[]);

	private static DeployedOverrideGraph Deployed(params (AssetKey Key, ModNodeId? Winner, int TargetIndex)[] chains)
		=> new(
			"data",
			"deployment-generation",
			DateTimeOffset.UtcNow,
			null,
			0,
			[],
			chains
				.Select(chain => new DeployedAssetOverrideChain(
					chain.Key,
					[new DeployedAssetOverrideEntry("archive", chain.TargetIndex, null, chain.Winner, true)]))
				.ToArray(),
			[]);
}
