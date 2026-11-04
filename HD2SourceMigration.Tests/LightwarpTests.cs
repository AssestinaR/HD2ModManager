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

	// ── 当"卡通阴影曲线"烘进底色 ──────────────────────────────────────

	/// <summary>正面朝光的法线：查到的就是受光段，归一化后 = 1.0 ⇒ 底色一点不该动。</summary>
	[Fact]
	public void LitSurfaceIsLeftAlone()
	{
		var albedo = SinglePixel(200);
		var normal = SinglePixel3(128, 128, 255);   // 平坦法线（+Z）
		MaterialAppearance.BakeLightwarpShading(
			albedo, 1, 1, Ramp(8, 50, 55, 58, 76), 8, 1, normal, 1, 1);

		Assert.InRange(albedo[0], 196, 200);
	}

	/// <summary>
	/// 背光面带上 ramp 暗端的**色偏**：basewarp 暗端 (50,55,58) / 受光 76 ⇒
	/// 蓝压得最少、红最多——也就是"偏冷的阴影"，正是作者写进去的东西。
	/// </summary>
	[Fact]
	public void BackFacingSurfaceGetsTheShadowTint()
	{
		var albedo = SinglePixel(204);
		var normal = SinglePixel3(128, 128, 0);    // 背向光（−Z 方向的重建结果接近 0）
		MaterialAppearance.BakeLightwarpShading(
			albedo, 1, 1, Ramp(8, 50, 55, 58, 76), 8, 1, normal, 1, 1);

		var r = albedo[0];
		var g = albedo[1];
		var b = albedo[2];
		// 冷阴影 = 蓝保留得最多：(50,55,58) 归一化后是 (0.66,0.72,0.76)，所以 B > G > R。
		Assert.True(b > g && g > r, $"阴影应当偏冷（B>G>R），实际 ({r},{g},{b})");
		Assert.True(r < 204, "背光面必须比受光面暗");
	}

	[Fact]
	public void ShadowNeverBlackensAChannelBelowOneFifth()
	{
		// ramp 暗端是纯 0：归一化后按 0.2 兜底，不会把某个通道彻底压没。
		var albedo = SinglePixel(255);
		var normal = SinglePixel3(128, 128, 0);
		MaterialAppearance.BakeLightwarpShading(
			albedo, 1, 1, Ramp(4, 0, 0, 0, 76), 4, 1, normal, 1, 1);
		Assert.InRange(albedo[0], 45, 55);
	}

	[Fact]
	public void NoNormalMapMeansNoShadingChange()
	{
		// 没有法线图时按"正面朝光"算 ⇒ 落在受光段 ⇒ 不动。
		var albedo = SinglePixel(180);
		MaterialAppearance.BakeLightwarpShading(
			albedo, 1, 1, Ramp(8, 50, 55, 58, 76), 8, 1, normal: null, normalWidth: 0, normalHeight: 0);
		Assert.InRange(albedo[0], 176, 180);
	}

	[Fact]
	public void BrokenRampIsIgnored()
	{
		var albedo = SinglePixel(120);
		MaterialAppearance.BakeLightwarpShading(
			albedo, 1, 1, Ramp(4, 0, 0, 0, 0), 4, 1, SinglePixel3(128, 128, 255), 1, 1);
		Assert.Equal(120, albedo[0]);   // 全黑 ramp 更像坏素材：不动
	}

	/// <summary>单像素底色（灰度 value）。</summary>
	/// <summary>
	/// **half-lambert 是"阴影不反"的关键**：NekoToon 写的是 `$$lightingrange 0.5`，
	/// 也就是把 N·L 折到 0..1 的中段。少了它，正侧面朝前（N·L≈0）会被判成阴影，
	/// 实机就是"毛衣上的阴影像是反着做的"。
	/// </summary>
	[Fact]
	public void HalfLambertKeepsSideFacingSurfacesLit()
	{
		// 光从 +X（正前方）来，法线朝上（与光垂直）：物理 N·L = 0，half-lambert = 0.5
		var albedo = SinglePixel(200);
		MaterialAppearance.BakeLightwarpShading(
			albedo, 1, 1, Ramp(8, 50, 55, 58, 76), 8, 1,
			SinglePixel3(128, 128, 255), 1, 1,
			lightX: 1, lightY: 0, lightZ: 0, halfLambert: true);

		Assert.InRange(albedo[0], 196, 200);   // 落在受光段 ⇒ 不动
	}

	[Fact]
	public void PhysicalNdotLWouldShadowThatSameSurface()
	{
		// 同一组输入，关掉 half-lambert：N·L = 0 ⇒ ramp 的暗端 ⇒ 明显变暗（这正是上一版的错）
		var albedo = SinglePixel(200);
		MaterialAppearance.BakeLightwarpShading(
			albedo, 1, 1, Ramp(8, 50, 55, 58, 76), 8, 1,
			SinglePixel3(128, 128, 255), 1, 1,
			lightX: 1, lightY: 0, lightZ: 0, halfLambert: false);

		Assert.True(albedo[0] < 150, $"应当明显变暗，实际 {albedo[0]}");
	}

	private static byte[] SinglePixel(byte value) => [value, value, value, 255];

	/// <summary>单像素法线（切线空间 0..255 编码）。</summary>
	private static byte[] SinglePixel3(byte r, byte g, byte b) => [r, g, b, 0];
}


