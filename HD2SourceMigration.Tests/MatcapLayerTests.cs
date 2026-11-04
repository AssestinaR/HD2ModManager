using HD2SourceMigration.VpkConversion;

namespace HD2SourceMigration.Tests;

/// <summary>
/// NekoToon / lilToon 的 matcap 合成：模式、tint、蒙版、采样坐标。
/// <para>
/// <b>为什么这些要单测。</b>合成的结果在产物里看不出来对不对——那只是一张底色贴图。
/// 参数写错（模式编号混用、tint 的 alpha 当成颜色、蒙版取错通道）的表现是"某几件亮了一档"，
/// 在游戏里几乎不可能归因。所以判据钉在纯函数上。
/// </para>
/// </summary>
public sealed class MatcapLayerTests
{
	// ── 模式解析（NekoToon 的编号）─────────────────────────────────────

	[Theory]
	[InlineData("0", MaterialAppearance.MatcapBlendMode.Off)]
	[InlineData("1", MaterialAppearance.MatcapBlendMode.Multiply)]
	[InlineData("2", MaterialAppearance.MatcapBlendMode.Add)]
	[InlineData("3", MaterialAppearance.MatcapBlendMode.Normal)]
	[InlineData("4", MaterialAppearance.MatcapBlendMode.Screen)]
	public void ParseMatcapBlendFollowsNekoToonNumbering(string raw, MaterialAppearance.MatcapBlendMode expected)
	{
		Assert.Equal(expected, MaterialAppearance.ParseMatcapBlend(raw));
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("abc")]
	[InlineData("5")]
	[InlineData("-1")]
	public void ParseMatcapBlendFallsBackToOff(string? raw)
	{
		// 缺省必须是"不用 matcap"：源侧没写这个键，就是那张图根本没参与渲染。
		Assert.Equal(MaterialAppearance.MatcapBlendMode.Off, MaterialAppearance.ParseMatcapBlend(raw));
	}

	// ── tint 的四个分量 ────────────────────────────────────────────────

	[Fact]
	public void ParseTintRgbaKeepsAllFourComponents()
	{
		var tint = MaterialAppearance.ParseTintRgba("[.5 .5 .5 1]");
		Assert.Equal(new[] { 0.5, 0.5, 0.5, 1.0 }, tint);
	}

	[Fact]
	public void ParseTintRgbaAcceptsSrgbScale()
	{
		var tint = MaterialAppearance.ParseTintRgba("[255 128 0 255]");
		Assert.Equal(1.0, tint[0], 6);
		Assert.Equal(128.0 / 255.0, tint[1], 6);
		Assert.Equal(0.0, tint[2], 6);
		Assert.Equal(1.0, tint[3], 6);
	}

	[Fact]
	public void ParseTintRgbaDefaultsToFullStrength()
	{
		Assert.Equal(new[] { 1.0, 1.0, 1.0, 1.0 }, MaterialAppearance.ParseTintRgba(null));
	}

	// ── 合成 ───────────────────────────────────────────────────────────

	/// <summary>单像素底色 + 单像素 matcap，没有法线也没有蒙版：三种模式各算一次。</summary>
	[Theory]
	[InlineData(MaterialAppearance.MatcapBlendMode.Add, 100, 50, 150)]
	[InlineData(MaterialAppearance.MatcapBlendMode.Multiply, 100, 128, 50)]      // 100 × (128/255) ≈ 50
	[InlineData(MaterialAppearance.MatcapBlendMode.Normal, 100, 50, 50)]
	// Screen = dst + src − dst×src：0.392 + 0.196 − 0.0769 = 0.511 ⇒ 130
	[InlineData(MaterialAppearance.MatcapBlendMode.Screen, 100, 50, 130)]
	public void BlendModesMatchLilToon(
		MaterialAppearance.MatcapBlendMode mode, byte dst, byte src, byte expected)
	{
		var albedo = Solid(dst, 1, 1);
		var matcap = Solid(src, 1, 1);

		MaterialAppearance.CompositeMatcap(
			albedo, 1, 1, matcap, 1, 1,
			normal: null, normalWidth: 0, normalHeight: 0,
			mask: null, maskWidth: 0, maskHeight: 0,
			tintRgba: [1, 1, 1, 1], mode);

		Assert.InRange(albedo[0], expected - 2, expected + 2);
	}

	[Fact]
	public void OffModeLeavesTheAlbedoUntouched()
	{
		var albedo = Solid(100, 1, 1);
		var matcap = Solid(255, 1, 1);

		MaterialAppearance.CompositeMatcap(
			albedo, 1, 1, matcap, 1, 1, null, 0, 0, null, 0, 0, [1, 1, 1, 1],
			MaterialAppearance.MatcapBlendMode.Off);

		Assert.Equal(100, albedo[0]);
	}

	/// <summary><c>$matcaptint</c> 的 alpha 是**整体强度**：0 就等于"这一层不生效"。</summary>
	[Fact]
	public void TintAlphaScalesTheWholeLayer()
	{
		var albedo = Solid(100, 1, 1);
		var matcap = Solid(200, 1, 1);

		MaterialAppearance.CompositeMatcap(
			albedo, 1, 1, matcap, 1, 1, null, 0, 0, null, 0, 0, [1, 1, 1, 0],
			MaterialAppearance.MatcapBlendMode.Normal);

		Assert.Equal(100, albedo[0]);
	}

	/// <summary><c>$mask1</c> 的 <b>R 通道</b>是逐像素强度；G/B/A 不参与 matcap。</summary>
	[Fact]
	public void MaskRedChannelGatesTheLayer()
	{
		var albedo = Solid(100, 1, 1);
		var matcap = Solid(200, 1, 1);

		// R=0（其余通道拉满，证明只取 R）
		var off = Solid(0, 1, 1);
		off[1] = 255;
		off[2] = 255;
		off[3] = 255;
		MaterialAppearance.CompositeMatcap(
			albedo, 1, 1, matcap, 1, 1, null, 0, 0, off, 1, 1, [1, 1, 1, 1],
			MaterialAppearance.MatcapBlendMode.Normal);
		Assert.Equal(100, albedo[0]);

		// R=255 ⇒ 整层生效
		var on = Solid(255, 1, 1);
		MaterialAppearance.CompositeMatcap(
			albedo, 1, 1, matcap, 1, 1, null, 0, 0, on, 1, 1, [1, 1, 1, 1],
			MaterialAppearance.MatcapBlendMode.Normal);
		Assert.InRange(albedo[0], 198, 202);
	}

	/// <summary>采样坐标跟着法线走：法线 R=255（切线空间 +X）应当采到 matcap 的右边缘。</summary>
	[Fact]
	public void NormalXyDrivesTheSampleUv()
	{
		// 2×1 的 matcap：左黑右白
		var matcap = new byte[2 * 4];
		matcap[0] = 0; matcap[1] = 0; matcap[2] = 0; matcap[3] = 255;
		matcap[4] = 255; matcap[5] = 255; matcap[6] = 255; matcap[7] = 255;

		// 法线 R=255 ⇒ u=1；G=128 ⇒ v≈0.5
		var normal = new byte[] { 255, 128, 255, 0 };

		var albedo = Solid(0, 1, 1);
		MaterialAppearance.CompositeMatcap(
			albedo, 1, 1, matcap, 2, 1, normal, 1, 1, null, 0, 0, [1, 1, 1, 1],
			MaterialAppearance.MatcapBlendMode.Normal);

		Assert.InRange(albedo[0], 250, 255);   // 采到右半边（白）
	}

	[Fact]
	public void RejectsOversizedClaims()
	{
		Assert.Throws<InvalidDataException>(() => MaterialAppearance.CompositeMatcap(
			new byte[4], 4, 4, new byte[4], 1, 1, null, 0, 0, null, 0, 0, [1, 1, 1, 1],
			MaterialAppearance.MatcapBlendMode.Add));
		Assert.Throws<InvalidDataException>(() => MaterialAppearance.CompositeMatcap(
			new byte[64], 4, 4, new byte[4], 4, 4, null, 0, 0, null, 0, 0, [1, 1, 1, 1],
			MaterialAppearance.MatcapBlendMode.Add));
	}

	private static byte[] Solid(byte value, int width, int height)
	{
		var rgba = new byte[width * height * 4];
		for (var i = 0; i < rgba.Length; i += 4)
		{
			rgba[i] = value;
			rgba[i + 1] = value;
			rgba[i + 2] = value;
			rgba[i + 3] = 255;
		}

		return rgba;
	}
}
