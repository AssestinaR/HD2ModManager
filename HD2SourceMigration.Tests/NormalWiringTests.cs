using HD2SourceMigration.VpkConversion;

namespace HD2SourceMigration.Tests;

/// <summary>
/// 法线图接线的两条纯规则：<c>$bumpstrength</c> 怎么解析、源法线像素怎么变成
/// HD2 <c>Nar</c> 槽要的形状（<b>RGB = 法线、A = 粗糙度</b>）。
/// <para>
/// <b>为什么这两条要单测。</b>它们决定"法线到底有没有接上"，而这件事在产物里<b>看不出来</b>：
/// 槽里一直有一张图，区别只是那张图是平面法线还是真法线。真机上"细节糊了"和"法线歪了"
/// 又长得很像，所以判据必须钉在这里。
/// </para>
/// </summary>
public sealed class NormalWiringTests
{
	// ── $bumpstrength 解析 ───────────────────────────────────────────────

	[Theory]
	[InlineData(null, 1.0)]
	[InlineData("", 1.0)]
	[InlineData("   ", 1.0)]
	[InlineData("1.0", 1.0)]
	[InlineData(".7", 0.7)]
	[InlineData("2", 2.0)]
	[InlineData("[2]", 2.0)]
	[InlineData("[ 0.5 ]", 0.5)]
	public void ParseBumpStrengthReadsTheDocumentedForms(string? raw, double expected)
	{
		Assert.Equal(expected, MaterialAppearance.ParseBumpStrength(raw), 6);
	}

	[Theory]
	[InlineData("abc")]
	[InlineData("NaN")]
	[InlineData("Infinity")]
	[InlineData("0")]
	[InlineData("-1")]
	public void ParseBumpStrengthFallsBackToOneOnGarbage(string raw)
	{
		// 写坏一个可选的观感参数，不该让整个材质失败——1.0 就是"照源侧原样"。
		Assert.Equal(1.0, MaterialAppearance.ParseBumpStrength(raw), 6);
	}

	[Fact]
	public void ParseBumpStrengthClampsAbsurdValues()
	{
		Assert.Equal(4.0, MaterialAppearance.ParseBumpStrength("99"), 6);
		Assert.Equal(0.01, MaterialAppearance.ParseBumpStrength("0.000001"), 6);
	}

	// ── OpenGL 约定的法线图 ─────────────────────────────────────────────

	/// <summary>
	/// 作者把 OpenGL 约定的法线图命名成 <c>*_Normal_OpenGL</c>（实测 huahua 的 Chiyu 整套都是）。
	/// 起源默认要 DirectX，绿通道方向相反——**不翻就会让凹凸看着是反的**，
	/// 实机表现正是"毛衣的阴影像是倒着做的"，而且只有这一套有这个问题。
	/// </summary>
	[Theory]
	[InlineData("cloth1_normal_opengl", true)]
	[InlineData("Hair_Normal_OpenGL", true)]
	[InlineData("body_normal_open_gl", true)]
	[InlineData("arm_gl", true)]
	[InlineData("Mat_Underwear_N", false)]
	[InlineData("_4_n", false)]
	[InlineData("", false)]
	[InlineData(null, false)]
	public void DetectsOpenGlNormalMapsByName(string? name, bool expected)
	{
		Assert.Equal(expected, SourceMaterialConverter.LooksOpenGlNormal(name));
	}

	[Fact]
	public void FlippingAnOpenGlNormalInvertsOnlyTheGreenChannel()
	{
		var dx = new byte[] { 128, 192, 0, 255 };
		var gl = new byte[] { 128, 192, 0, 255 };

		MaterialAppearance.RebuildTangentNormal(dx, 1.0, flipY: false, roughness: 0);
		MaterialAppearance.RebuildTangentNormal(gl, 1.0, flipY: true, roughness: 0);

		Assert.Equal(dx[0], gl[0]);
		Assert.Equal(255 - dx[1], gl[1]);
	}

	// ── 法线像素重建 ─────────────────────────────────────────────────────

	/// <summary>
	/// <b>这条是整件事的核心回归。</b>ATI2N / BC5 法线图<b>只有 RG</b>（官方文档写着
	/// "丢弃法线的 B 通道"），解出来 B = 0、A = 255。直接塞进 HD2，法线就变成
	/// "Z=0 的躺平法线"——整片朝侧面受光。重建之后 B 必须是 255（平面法线的 Z）。
	/// </summary>
	[Fact]
	public void Bc5StyleGarbageBlueChannelIsRebuilt()
	{
		// 两个像素：平面的 (128,128,B=0,A=255)，以及偏一点的 (192,128,B=0,A=255)。
		var rgba = new byte[]
		{
			128, 128, 0, 255,
			192, 128, 0, 255,
		};

		MaterialAppearance.RebuildTangentNormal(rgba, strength: 1.0, flipY: false, roughness: 89);

		Assert.Equal(128, rgba[0]);
		Assert.Equal(128, rgba[1]);
		Assert.Equal(255, rgba[2]);      // ← 重建出来的 Z
		Assert.Equal(89, rgba[3]);       // ← A 换成粗糙度（原来是 255）

		Assert.Equal(192, rgba[4]);
		Assert.Equal(128, rgba[5]);
		Assert.InRange(rgba[6], 210, 245);   // x=+0.506 ⇒ z≈0.862 ⇒ 约 238
		Assert.Equal(89, rgba[7]);
	}

	[Fact]
	public void StrengthScalesTheDeviationAndKeepsTheNormalUnitLength()
	{
		var weak = new byte[] { 192, 128, 0, 255 };
		var strong = new byte[] { 192, 128, 0, 255 };

		MaterialAppearance.RebuildTangentNormal(weak, strength: 0.5, flipY: false, roughness: 0);
		MaterialAppearance.RebuildTangentNormal(strong, strength: 2.0, flipY: false, roughness: 0);

		// 0.5 强度把 XY 拉回中间：192 → 161；2.0 强度推到 255（夹紧）。
		Assert.InRange(weak[0], 158, 164);
		Assert.Equal(255, strong[0]);

		// 无论强度多大，解出来的三元组都得是单位向量（Z 是按 1−x²−y² 重建的）。
		foreach (var rgba in new[] { weak, strong })
		{
			var x = (rgba[0] / 255.0 * 2.0) - 1.0;
			var y = (rgba[1] / 255.0 * 2.0) - 1.0;
			var z = (rgba[2] / 255.0 * 2.0) - 1.0;
			Assert.Equal(1.0, Math.Sqrt((x * x) + (y * y) + (z * z)), 1);
		}
	}

	[Fact]
	public void FlipYMirrorsOnlyTheGreenChannel()
	{
		var kept = new byte[] { 128, 192, 0, 255 };
		var flipped = new byte[] { 128, 192, 0, 255 };

		MaterialAppearance.RebuildTangentNormal(kept, strength: 1.0, flipY: false, roughness: 0);
		MaterialAppearance.RebuildTangentNormal(flipped, strength: 1.0, flipY: true, roughness: 0);

		Assert.Equal(kept[0], flipped[0]);       // R 不受影响
		Assert.Equal(192, kept[1]);
		Assert.InRange(flipped[1], 60, 66);      // 192 的镜像 ≈ 63
		Assert.Equal(kept[2], flipped[2]);       // Z 对称 ⇒ 一样
	}

	[Fact]
	public void FlatNormalStaysFlatAtAnyStrength()
	{
		// 起源/起源系的平面法线写的是 128，而解码是 (v/255)*2−1 ⇒ 128 解出来是 +0.004 而不是 0。
		// 所以"平的"在这里只能是**近似不变**（±1 个量化单位，被 BC7 一压就没了），
		// 断言写死 128 会把一个不存在的问题钉成回归。
		foreach (var strength in new[] { 0.25, 1.0, 3.0 })
		{
			var rgba = new byte[] { 128, 128, 0, 255 };
			// flipY 不参与这条：翻的是 Y 的符号，而 128 的偏离本来就不对称（见上），
			// 翻完会落在 126 而不是 128 —— 那是同一个"没偏移"，不是缺陷。
			MaterialAppearance.RebuildTangentNormal(rgba, strength, flipY: false, roughness: 51);
			Assert.InRange(rgba[0], 127, 129);
			Assert.InRange(rgba[1], 127, 129);
			Assert.Equal(255, rgba[2]);      // Z 恒为平的那一档
			Assert.Equal(51, rgba[3]);
		}
	}

	[Fact]
	public void RejectsPixelBuffersThatAreNotRgba()
	{
		Assert.Throws<InvalidDataException>(() =>
			MaterialAppearance.RebuildTangentNormal(new byte[] { 1, 2, 3 }, 1.0, false, 0));
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			MaterialAppearance.RebuildTangentNormal(new byte[] { 1, 2, 3, 4 }, 0, false, 0));
	}
}

