using HD2SourceMigration.VpkConversion;

namespace HD2SourceMigration.Tests;

/// <summary>
/// 材质总账（<see cref="VpkMaterialGap"/>）的口径。
/// <para>
/// 这份记录是"上机那块为什么不对"唯一能回答问题的数据，所以它的<b>计数口径</b>本身
/// 就是契约：模型要 N 个、真实落地 M 个、差的那部分属于哪一类。错一位数，
/// 报告就会显示"全部落地"，而游戏里明明有一块灰面/一块不见了。
/// </para>
/// <para>
/// 尤其注意 <see cref="VpkMaterialGap.FallbackMaterials"/>：兜底灰材质<b>有 id、也写进了
/// patch</b>，所以它天然会被算进"已落地"。但它不是源侧那个材质——必须单独计一类。
/// </para>
/// </summary>
public sealed class VpkMaterialGapTests
{
	[Fact]
	public void CleanWhenEveryRequiredMaterialResolved()
	{
		var gap = new VpkMaterialGap(RequiredCount: 5, ResolvedCount: 5, NoDraw: [], Missing: []);
		Assert.True(gap.IsClean);
		Assert.Equal(0, gap.UnresolvedCount);
		Assert.Empty(gap.FallbackMaterials);   // 来源没给 Fallback 时也要能用，不能 null 崩
	}

	[Fact]
	public void FallbackCountsAsUnresolvedEvenThoughItHasAnId()
	{
		// 关键口径：兜底占位不能算"解决"。
		var gap = new VpkMaterialGap(
			RequiredCount: 5,
			ResolvedCount: 4,               // 调用方已经把兜底排除掉了
			NoDraw: [],
			Missing: [],
			Fallback: ["mat_accpreal", "mat_accpreal_tiara"]);

		Assert.Equal(2, gap.FallbackMaterials.Count);
		Assert.Equal(1, gap.UnresolvedCount);
		Assert.False(gap.IsClean);
	}

	[Fact]
	public void TheThreeGapKindsAreReportedSeparately()
	{
		var gap = new VpkMaterialGap(
			RequiredCount: 10,
			ResolvedCount: 6,
			NoDraw: ["hair_hidden", "outline_hidden"],
			Missing: ["broken_material"],
			Fallback: ["no_pack_material"]);

		Assert.Equal(4, gap.UnresolvedCount);          // 2 noDraw + 1 missing + 1 fallback
		Assert.Equal(2, gap.NoDraw.Count);
		Assert.Single(gap.Missing);
		Assert.Single(gap.FallbackMaterials);
	}

	[Fact]
	public void UnresolvedIsNeverNegative()
	{
		// 防御性口径：ResolvedCount 若被上游算多了，不能报出负数（那会让界面显示"解决 -3 个"）。
		var gap = new VpkMaterialGap(RequiredCount: 2, ResolvedCount: 2, NoDraw: [], Missing: []);
		Assert.Equal(0, gap.UnresolvedCount);
	}
}
