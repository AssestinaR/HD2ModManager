using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

/// <summary>
/// 信息中心的 section 结构事实（无节点）。
///
/// 这里锁的是"事实的形态与身份"，不是解析细节——解析在 WardrobeSectionSourceTests 里测。
/// 关键是：这份事实必须能原样还原成映射表条目，否则"编辑器看到的结构"和
/// "sections.json 里的结构"会各走一路。
/// </summary>
public sealed class UnitSectionStructureFactsTests
{
	private const string UnitA = "af431352d9b95e83";

	private static WardrobeSectionMapUnit MapUnit()
		=> new(
			UnitA,
			"Helmet",
			"Undergarment",
			"Any",
			"Medium",
			2,
			true,
			new[]
			{
				new WardrobeSectionMapEntry(0, "a47787804c45e1a9", 8852, "a47787804c45e1a9-8852", new[]
				{
					new WardrobeSectionToggle(0, 0, 0, "0x00002010", 26556),
				}),
				new WardrobeSectionMapEntry(1, "a648fd251c740b12", 1024, "a648fd251c740b12-1024", new[]
				{
					new WardrobeSectionToggle(0, 0, 1, "0x00002020", 3072),
				}),
			});

	[Fact]
	public void From_ThenToMapUnits_RoundTrips()
	{
		var facts = new UnitSectionStructureFacts(
			"gen",
			new Dictionary<string, UnitSectionStructureFact>(StringComparer.Ordinal)
			{
				[UnitA] = UnitSectionStructureFact.From(MapUnit()),
			},
			Array.Empty<string>(),
			Array.Empty<CoreIssue>());

		var restored = Assert.Single(facts.ToMapUnits());
		Assert.Equal(UnitA, restored.FileId);
		Assert.Equal("Helmet", restored.Slot);
		Assert.Equal(2, restored.SectionCount);
		Assert.Equal("a47787804c45e1a9-8852", restored.Sections[0].Fingerprint);
		Assert.Equal(26556u, restored.Sections[0].Toggles[0].NumIndices);
	}

	[Fact]
	public void Find_AcceptsPrefixAndUppercase()
	{
		var facts = new UnitSectionStructureFacts(
			"gen",
			new Dictionary<string, UnitSectionStructureFact>(StringComparer.Ordinal)
			{
				[UnitA] = UnitSectionStructureFact.From(MapUnit()),
			},
			Array.Empty<string>(),
			Array.Empty<CoreIssue>());

		Assert.NotNull(facts.Find("0x" + UnitA.ToUpperInvariant()));
		Assert.Null(facts.Find("0000000000000000"));
		Assert.Null(facts.Find(null));
	}

	[Fact]
	public void Scope_SortsUnitKeysSoTheSameBatchIsTheSameFact()
	{
		// 同一批 unit 的顺序不同，必须落到同一个作用域——否则同一份结构会被缓存成好几份。
		var first = ModInformationCenter.UnitSectionStructureScope(
			new HashSet<string>(StringComparer.Ordinal) { "b000000000000002", "a000000000000001" });
		var second = ModInformationCenter.UnitSectionStructureScope(
			new HashSet<string>(StringComparer.Ordinal) { "0xa000000000000001", "B000000000000002" });
		Assert.Equal(first, second);
		Assert.Equal("a000000000000001,b000000000000002", first);
	}
}
