using HD2SourceMigration.VpkConversion;

namespace HD2SourceMigration.Tests;

/// <summary>
/// lightwarp → 底色整体倍率。
/// <para>
/// <b>为什么值得单测。</b>实测这几个 ramp 的受光段恒为 (76,76,76)（skinwarp 是 64），
/// 也就是"受光 = 底色 × 0.30"。取错段（比如整条平均）会把影子里的亮度也算进来、
/// 压得更暗，而那种偏差在游戏里只会被当成"怎么有点黑"，几乎不可能归因到 ramp。
/// </para>
/// </summary>
public sealed class LightwarpTests
{
	/// <summary>实测形状：1 像素高的横条，左半是暗端（带色偏）、右半恒定。</summary>
	private static byte[] Ramp(int width, byte darkR, byte darkG, byte darkB, byte lit)
	{
		var rgba = new byte[width * 4];
		for (var x = 0; x < width; x++)
		{
			var litSide = x * 2 >= width;
			rgba[x * 4] = litSide ? lit : darkR;
			rgba[x * 4 + 1] = litSide ? lit : darkG;
			rgba[x * 4 + 2] = litSide ? lit : darkB;
			rgba[x * 4 + 3] = 255;
		}

		return rgba;
	}

	[Fact]
	public void FactorComesFromTheLitHalf()
	{
		// 实测值：basewarp 暗端 (50,55,58)、受光段 76
		var factor = MaterialAppearance.LightwarpFactor(Ramp(8, 50, 55, 58, 76), 8, 1);

		Assert.Equal(76.0 / 255.0, factor[0], 6);
		Assert.Equal(76.0 / 255.0, factor[1], 6);
		Assert.Equal(76.0 / 255.0, factor[2], 6);
	}

	[Fact]
	public void DarkEndIsIgnored()
	{
		// 暗端再黑也不影响结果：那是环境光的形状，HD2 那边由 PBR 自己算。
		var dark = MaterialAppearance.LightwarpFactor(Ramp(8, 0, 0, 0, 76), 8, 1);
		var bright = MaterialAppearance.LightwarpFactor(Ramp(8, 200, 200, 200, 76), 8, 1);
		Assert.Equal(dark, bright);
	}

	[Fact]
	public void FactorIsClampedSoTheBaseNeverGoesBlack()
	{
		var factor = MaterialAppearance.LightwarpFactor(Ramp(4, 0, 0, 0, 0), 4, 1);
		Assert.Equal(0.05, factor[0], 6);
	}

	[Fact]
	public void MultiplyKeepsAlphaAndScalesRgb()
	{
		var rgba = new byte[] { 200, 100, 50, 255 };
		MaterialAppearance.MultiplyRgb(rgba, [0.5, 0.5, 0.5]);
		Assert.Equal(100, rgba[0]);
		Assert.Equal(50, rgba[1]);
		Assert.Equal(25, rgba[2]);
		Assert.Equal(255, rgba[3]);   // 镂空信息在遮罩贴图里，绝不能被动
	}

	[Fact]
	public void RejectsMismatchedPixelBuffers()
	{
		Assert.Throws<InvalidDataException>(() =>
			MaterialAppearance.LightwarpFactor(new byte[8], 8, 4));
		Assert.Throws<InvalidDataException>(() =>
			MaterialAppearance.MultiplyRgb(new byte[3], [1, 1, 1]));
	}
}
