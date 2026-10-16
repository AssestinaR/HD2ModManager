using HD2SourceMigration.VpkConversion;

namespace HD2SourceMigration.Tests;

/// <summary>
/// alpha 判定的两条阈值。这是 alphaclip 切换的判据，判错的后果很具体：
/// <b>该镂空的地方成一片实心</b>，或者<b>不该镂空的地方被挖出洞</b>。
/// </summary>
public sealed class TextureAlphaTests
{
	/// <summary>造一片 RGBA：<paramref name="alphaOf"/> 决定每个像素的 alpha。</summary>
	private static byte[] BuildRgba(int pixels, Func<int, byte> alphaOf)
	{
		var rgba = new byte[pixels * 4];
		for (var i = 0; i < pixels; i++)
		{
			rgba[(i * 4) + 0] = 128;
			rgba[(i * 4) + 1] = 128;
			rgba[(i * 4) + 2] = 128;
			rgba[(i * 4) + 3] = alphaOf(i);
		}

		return rgba;
	}

	[Fact]
	public void FullyOpaqueAlphaIsNotMeaningful()
	{
		// DXT1 常量解出来就是恒 255。只看覆盖率会把它误判成"有 alpha"——所以必须有 spread 阈值。
		var stats = TextureAlpha.Measure(BuildRgba(1000, _ => 255));
		Assert.Equal(0, stats.Spread);
		Assert.Equal(0, stats.Coverage);
		Assert.False(stats.IsMeaningful);
	}

	[Fact]
	public void AWideAlphaRangeHidingOnlyAFewPixelsIsNotMeaningful()
	{
		// 有动态范围、但透明像素只有 0.1%（低于 0.5% 阈值）——那是噪声，不是镂空。
		var stats = TextureAlpha.Measure(BuildRgba(1000, i => i == 0 ? (byte)0 : (byte)255));
		Assert.Equal(255, stats.Spread);
		Assert.Equal(0, stats.Min);   // 第一个像素就是 0
		Assert.True(stats.Coverage < AlphaStats.CoverageThreshold);
		Assert.False(stats.IsMeaningful);
	}

	[Fact]
	public void ARealCutoutIsMeaningful()
	{
		// 一半透明、一半不透明——典型的蕾丝/镂空。
		var stats = TextureAlpha.Measure(BuildRgba(1000, i => i % 2 == 0 ? (byte)0 : (byte)255));
		Assert.Equal(255, stats.Spread);
		Assert.Equal(0, stats.Min);
		Assert.Equal(255, stats.Max);
		Assert.Equal(0.5, stats.Coverage, 3);
		Assert.True(stats.IsMeaningful);
	}

	[Fact]
	public void ANarrowButWidespreadAlphaIsNotMeaningful()
	{
		// 整片都是同一个值 → spread = 0。
		var flat = TextureAlpha.Measure(BuildRgba(1000, _ => 254));
		Assert.Equal(0, flat.Spread);
		Assert.False(flat.IsMeaningful);

		// 边界：spread 恰好达到阈值，但**覆盖率是 0**——唯一的 252 不小于 250。
		// 这条钉的是"两个条件缺一不可"：只看 spread 就会把它判成镂空。
		// （注意均匀值永远 spread = 0，所以这个用例必须靠"一个值不齐"来构造。）
		var atSpreadThreshold = TextureAlpha.Measure(BuildRgba(1000, i => i == 0 ? (byte)252 : (byte)255));
		Assert.Equal(AlphaStats.SpreadThreshold, atSpreadThreshold.Spread);
		Assert.Equal(0, atSpreadThreshold.Coverage);
		Assert.False(atSpreadThreshold.IsMeaningful);

		// 两个条件都满足才算：spread = 255、覆盖率 1%。
		var both = TextureAlpha.Measure(BuildRgba(1000, i => i < 10 ? (byte)0 : (byte)255));
		Assert.Equal(255, both.Spread);
		Assert.True(both.Coverage >= AlphaStats.CoverageThreshold);
		Assert.True(both.IsMeaningful);
	}

	[Fact]
	public void MeasureRejectsMisalignedBuffers()
	{
		Assert.Throws<InvalidDataException>(() => TextureAlpha.Measure([]));
		Assert.Throws<InvalidDataException>(() => TextureAlpha.Measure(new byte[7]));
	}
}
