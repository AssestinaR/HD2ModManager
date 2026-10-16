using HD2SourceMigration.VpkConversion;

namespace HD2SourceMigration.Tests;

/// <summary>
/// 底色压暗。这是<b>外观</b>修正，不是正确性修正——所以判据要写清"改了什么、没改什么"：
/// 压 RGB、不碰 alpha、不碰小图、不碰法线/金属/粗糙（后三者在别的槽，本类根本接触不到）。
/// </summary>
public sealed class AlbedoAdjustmentTests
{
	private static byte[] BuildRgba(int width, int height, byte r, byte g, byte b, byte a = 255)
	{
		var rgba = new byte[width * height * 4];
		for (var offset = 0; offset < rgba.Length; offset += 4)
		{
			rgba[offset + 0] = r;
			rgba[offset + 1] = g;
			rgba[offset + 2] = b;
			rgba[offset + 3] = a;
		}

		return rgba;
	}

	[Fact]
	public void MultiplyModeScalesRgbAndLeavesAlphaAlone()
	{
		var rgba = BuildRgba(64, 64, 200, 100, 50, 0);
		AlbedoAdjustment.Default.Apply(rgba, 64, 64);

		Assert.Equal(80, rgba[0]);    // 200 * 0.4
		Assert.Equal(40, rgba[1]);    // 100 * 0.4
		Assert.Equal(20, rgba[2]);    //  50 * 0.4
		Assert.Equal(0, rgba[3]);     // alpha 不动
	}

	/// <summary>
	/// 小图是占位图（纯色 / 灰度格），压暗它们只会让本该纯白的地方变灰。
	/// Python 侧的默认阈值就是 64，这条钉住它。
	/// </summary>
	[Fact]
	public void PlaceholderSizedTexturesAreSkipped()
	{
		var tiny = BuildRgba(1, 1, 255, 255, 255);
		AlbedoAdjustment.Default.Apply(tiny, 1, 1);
		Assert.Equal(255, tiny[0]);

		var atLimit = BuildRgba(63, 64, 255, 255, 255);
		AlbedoAdjustment.Default.Apply(atLimit, 63, 64);
		Assert.Equal(255, atLimit[0]);

		var atThreshold = BuildRgba(64, 64, 255, 255, 255);
		AlbedoAdjustment.Default.Apply(atThreshold, 64, 64);
		Assert.Equal(102, atThreshold[0]);   // 255 * 0.4
	}

	[Fact]
	public void GammaModeDimsMidtonesAndKeepsFullWhite()
	{
		var adjustment = new AlbedoAdjustment(AlbedoDarkenMode.Gamma, 2.0);
		var rgba = BuildRgba(64, 64, 128, 255, 0);
		adjustment.Apply(rgba, 64, 64);

		// 255 * (128/255)^2 ≈ 64；全白保持全白；全黑保持全黑。
		Assert.InRange(rgba[0], 63, 65);
		Assert.Equal(255, rgba[1]);
		Assert.Equal(0, rgba[2]);
	}

	[Fact]
	public void ApplyRejectsMismatchedBuffersAndBadFactors()
	{
		var adjustment = AlbedoAdjustment.Default;
		Assert.Throws<InvalidDataException>(() => adjustment.Apply(new byte[7], 1, 1));
		Assert.Throws<InvalidDataException>(() => adjustment.Apply(new byte[64 * 64 * 4], 32, 32));
		Assert.Throws<ArgumentOutOfRangeException>(
			() => new AlbedoAdjustment(AlbedoDarkenMode.Multiply, 0).Apply(new byte[64 * 64 * 4], 64, 64));
		Assert.Throws<ArgumentOutOfRangeException>(
			() => new AlbedoAdjustment(AlbedoDarkenMode.Multiply, double.NaN).Apply(new byte[64 * 64 * 4], 64, 64));
	}

	[Fact]
	public void AppliesToIsTheSamePredicateApplyUses()
	{
		var adjustment = AlbedoAdjustment.Default;
		Assert.False(adjustment.AppliesTo(1, 1));
		Assert.False(adjustment.AppliesTo(4096, 32));
		Assert.True(adjustment.AppliesTo(2048, 2048));
	}
}
