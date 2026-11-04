using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

/// <summary>
/// 就地烘焙的翻译：来源键空间 → 目标键空间。
/// <para>
/// 守三件事：① 换过甲（目标 id ≠ 来源 id）时按 <c>sourceUnit</c> 翻；
/// ② 恒等路径（没有 <c>sourceUnit</c>）时把键当成来源键；③ <b>丢掉的条目必须数出来</b>——
/// 用户勾了开关却没生效，只会以为功能坏了。
/// </para>
/// </summary>
public sealed class VpkSectionBakePlanTests
{
	private const string SourceTorso = "5e8bbfd9e68e0002";
	private const string TargetTorso = "af431352d9b95e83";
	private const string SourceHelmet = "5e8bbfd9e68e0001";
	private const string TargetHelmet = "ea846ef460cc5ba0";

	private static WardrobeSectionMapDocument Map(params WardrobeSectionMapUnit[] units)
		=> new(
			WardrobeSectionMapDocument.CurrentSchema,
			new WardrobeSectionMapPatch("/tmp/x.patch_0", "abc", 1, null),
			WardrobeSectionMapDocument.ExpectedSectionRecordSize,
			WardrobeSectionMapDocument.ExpectedNumIndicesOffset,
			WardrobeSectionMapDocument.TocRelativeOffsets,
			units);

	private static WardrobeSectionMapUnit Unit(string fileId, string? sourceUnit, int sections = 4)
		=> new(
			fileId,
			"Torso",
			"Undergarment",
			"Any",
			"Medium",
			sections,
			true,
			[
				.. Enumerable.Range(0, sections).Select(index => new WardrobeSectionMapEntry(
					index,
					"aabbccddeeff0011",
					10,
					"aabbccddeeff0011-10",
					[])),
			])
		{
			SourceUnit = sourceUnit,
		};

	private static Dictionary<string, IReadOnlyCollection<int>> Disabled(string unit, params int[] sections)
		=> new(StringComparer.Ordinal) { [unit] = sections };

	[Fact]
	public void Translate_RekeysFromSourceToTargetWhenTheyDiffer()
	{
		var map = Map(Unit(TargetTorso, SourceTorso));

		var translation = VpkSectionBakePlan.Translate(Disabled(SourceTorso, 1, 3), map);

		var single = Assert.Single(translation.DisabledByTargetUnit);
		Assert.Equal(TargetTorso, single.Key);
		Assert.Equal([1, 3], single.Value);
		Assert.False(translation.HasDropped);
	}

	[Fact]
	public void Translate_TreatsTheKeyAsItsOwnSourceWhenSourceUnitIsMissing()
	{
		// 恒等路径（目标是自己的素体）不写 sourceUnit：那时键就是来源键。
		var map = Map(Unit(SourceTorso, null));

		var translation = VpkSectionBakePlan.Translate(Disabled(SourceTorso, 2), map);

		Assert.Equal([2], Assert.Single(translation.DisabledByTargetUnit).Value);
	}

	[Fact]
	public void Translate_CountsEntriesThatHaveNoLandingPoint()
	{
		var map = Map(Unit(TargetTorso, SourceTorso));

		var translation = VpkSectionBakePlan.Translate(
			new Dictionary<string, IReadOnlyCollection<int>>(StringComparer.Ordinal)
			{
				[SourceTorso] = [1],
				[SourceHelmet] = [0, 2],
			},
			map);

		Assert.Single(translation.DisabledByTargetUnit);
		Assert.True(translation.HasDropped);
		Assert.Equal(1, translation.DroppedUnits);
		Assert.Equal(2, translation.DroppedSections);
		Assert.Contains("1 个部件", VpkSectionBakePlan.Describe(translation), StringComparison.Ordinal);
	}

	[Fact]
	public void Translate_AppliesOneSourceToEveryTargetItLandedOn()
	{
		// 合并方向是一对多：同一个来源 unit 的几何可能被挂到多个目标 unit 上，那几处都要关。
		var map = Map(Unit(TargetTorso, SourceTorso), Unit(TargetHelmet, SourceTorso));

		var translation = VpkSectionBakePlan.Translate(Disabled(SourceTorso, 3), map);

		Assert.Equal(2, translation.DisabledByTargetUnit.Count);
		Assert.All(translation.DisabledByTargetUnit.Values, sections => Assert.Equal([3], sections));
	}

	[Fact]
	public void Translate_AcceptsLooseKeysAndIgnoresEmptyEntries()
	{
		var map = Map(Unit(TargetTorso, SourceTorso));
		var loose = new Dictionary<string, IReadOnlyCollection<int>>(StringComparer.Ordinal)
		{
			["0x" + SourceTorso.ToUpperInvariant()] = [2],
			["   "] = [9],
			[SourceHelmet] = [],
		};

		var translation = VpkSectionBakePlan.Translate(loose, map);

		Assert.Equal([2], Assert.Single(translation.DisabledByTargetUnit).Value);
		Assert.False(translation.HasDropped);
	}

	[Fact]
	public void Translate_ReturnsEmptyForNoInput()
	{
		var map = Map(Unit(TargetTorso, SourceTorso));

		Assert.Empty(VpkSectionBakePlan.Translate(null, map).DisabledByTargetUnit);
		Assert.Empty(VpkSectionBakePlan.Translate(new Dictionary<string, IReadOnlyCollection<int>>(), map).DisabledByTargetUnit);
		Assert.Empty(VpkSectionBakePlan.Describe(VpkSectionBakeTranslation.Empty));
	}
}
