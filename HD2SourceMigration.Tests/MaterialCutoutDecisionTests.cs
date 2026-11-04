using HD2SourceMigration.VpkConversion;

namespace HD2SourceMigration.Tests;

/// <summary>
/// 镂空判定的两条纯规则：**要不要切 <c>alphaclip</c>**（<see cref="SourceMaterialConverter.ShouldUseAlphaClip"/>）
/// 与 **遮罩像素从哪份数据来**（<see cref="SourceMaterialConverter.BuildOpacityMaskPixels"/>）。
/// <para>
/// 这两条走的是同一段代码，但踩过两个互相独立的坑，而且都会表现成"用户看得见的错误"：
/// </para>
/// <list type="bullet">
/// <item>
/// <b>要不要切</b>：合成底色（底图缺失）的 alpha 被当成"整片均匀半透明"，
/// 于是任何"底图缺失 + <c>$alphatest 1</c>"的金属件都被强制切进 <c>alphaclip</c>——
/// 而 <c>alphaclip</c> 没有金属度槽，结果既没镂空也没金属。
/// </item>
/// <item>
/// <b>从哪来</b>：遮罩曾经取"底色像素"，但 matcap 承载表面的件会把底色**整份换成 matcap 图像**，
/// 于是判定说"要镂空"、遮罩却是那张球面图的 alpha（通常全不透明）⇒ 镂空静默失效。
/// </item>
/// </list>
/// <para>
/// 两者都不需要游戏数据、不需要 texconv，所以在这里用单测钉死——历史上它们是靠实机 A/B 反复猜的。
/// </para>
/// </summary>
public sealed class MaterialCutoutDecisionTests
{
	/// <summary>alpha 有形状：动态范围够大、覆盖率达到阈值。</summary>
	private static AlphaStats ShapedAlpha() => new(Min: 0, Max: 255, Coverage: 0.4);

	/// <summary>整片均匀半透明（实测 MourningVeil 玻璃鞋跟 alpha 恒 102）。</summary>
	private static AlphaStats UniformTranslucentAlpha() => new(Min: 102, Max: 102, Coverage: 1.0);

	/// <summary>整片不透明。</summary>
	private static AlphaStats OpaqueAlpha() => new(Min: 255, Max: 255, Coverage: 0.0);

	/// <summary>合成底色：我们自己填的 alpha=0，**不是信息**。</summary>
	private static AlphaStats SyntheticAlpha() => new(Min: 0, Max: 0, Coverage: 1.0);

	// ---------------------------------------------------------------- 要不要切

	[Fact]
	public void ShapedSourceAlphaSwitchesToAlphaClip()
	{
		Assert.True(SourceMaterialConverter.ShouldUseAlphaClip(
			wantsTransparency: true, hasSourceOpacity: true, ShapedAlpha()));
	}

	[Fact]
	public void UniformlyTranslucentSourceAlphaSwitchesToAlphaClip()
	{
		// 玻璃鞋跟：不认这一类就会变成不透明白塑料。
		Assert.True(SourceMaterialConverter.ShouldUseAlphaClip(
			wantsTransparency: true, hasSourceOpacity: true, UniformTranslucentAlpha()));
	}

	[Fact]
	public void OpaqueSourceAlphaDoesNotSwitch()
	{
		Assert.False(SourceMaterialConverter.ShouldUseAlphaClip(
			wantsTransparency: true, hasSourceOpacity: true, OpaqueAlpha()));
	}

	[Fact]
	public void NoSourceImageNeverSwitchesEvenWhenItsAlphaLooksTranslucent()
	{
		// ⚠️ 这是 B 那条的回归闸门。
		// 合成底色的 alpha 是 0/0：旧实现据此判出"整片均匀半透明"，于是把**金属件**
		// 也切进 alphaclip —— 既没有金属度（模板没有那个槽），镂空也做不出来。
		Assert.True(SyntheticAlpha().IsEmpty, "前提：合成 alpha 属于「整片为空」这一档");
		Assert.False(SyntheticAlpha().IsUniformlyTranslucent, "「整片为空」不再是「整片半透明」");

		Assert.False(SourceMaterialConverter.ShouldUseAlphaClip(
			wantsTransparency: true, hasSourceOpacity: false, SyntheticAlpha()));
	}

	/// <summary>
	/// 实测事故（2026-11-04）：`二阶堂希罗` 的裙子材质 <c>hirocloth2</c> 只写 <c>$translucent</c>，
	/// 底色 alpha 整片 ≈ 0。旧判据把它当"整片半透明" → <c>alphaclip</c> → 遮罩整张黑 →
	/// **裙子在游戏里干净地消失**（实测 10 张遮罩 R 均值 1、非零像素 0.47%）。
	/// 把阈值从 0.5 降到 0.144 也没救：0 小于任何阈值。
	/// </summary>
	[Fact]
	public void EmptyAlphaWithOnlyTranslucencyDoesNotSwitch()
	{
		var empty = new AlphaStats(Min: 0, Max: 1, Coverage: 0.995);
		Assert.True(empty.IsEmpty);

		Assert.False(SourceMaterialConverter.ShouldUseAlphaClip(
			wantsTransparency: true, hasSourceOpacity: true, empty, wantsAlphaTest: false));
	}

	/// <summary>
	/// 但「隐藏物品」那类模组是**故意**让整件不画的：它们写 <c>$alphatest</c>、alpha 整片为 0，
	/// 靠 <c>alphaclip</c> + 全黑遮罩达成"东西消失"。这一档必须保留，否则那些隐藏件会重新出现。
	/// </summary>
	[Fact]
	public void EmptyAlphaWithAlphaTestKeepsAlphaClip()
	{
		var empty = new AlphaStats(Min: 0, Max: 0, Coverage: 1.0);

		Assert.True(SourceMaterialConverter.ShouldUseAlphaClip(
			wantsTransparency: true, hasSourceOpacity: true, empty, wantsAlphaTest: true));
	}

	/// <summary>
	/// 更隐蔽的一档：alpha 动态范围是满的（0..255）、覆盖率也够，但**能看见的像素只有 0.5%**。
	/// 旧判据（只看动态范围 + "有像素不是全不透明"）会认成"alpha 在表达形状" → alphaclip →
	/// 整件被裁到只剩几个像素。实测就是 `hirocloth2` 那 10 张遮罩的形状。
	/// </summary>
	[Fact]
	public void SparseAlphaDoesNotSwitchEvenWithFullRange()
	{
		var sparse = new AlphaStats(Min: 0, Max: 255, Coverage: 0.995) { VisibleCoverage = 0.0047 };

		Assert.False(sparse.IsMeaningful, "能看见的像素只有 0.47%，不算「在表达形状」");
		Assert.False(SourceMaterialConverter.ShouldUseAlphaClip(
			wantsTransparency: true, hasSourceOpacity: true, sparse, wantsAlphaTest: false));
	}

	/// <summary>真正的镂空件（蕾丝）绝大多数像素可见，只有一小部分是"眼"——这一档必须仍然切 alphaclip。</summary>
	[Fact]
	public void NormalCutoutAlphaStillSwitches()
	{
		var lace = new AlphaStats(Min: 0, Max: 255, Coverage: 0.35) { VisibleCoverage = 0.65 };

		Assert.True(SourceMaterialConverter.ShouldUseAlphaClip(
			wantsTransparency: true, hasSourceOpacity: true, lace, wantsAlphaTest: true));
	}

	[Fact]
	public void VmtThatDoesNotWantTransparencyNeverSwitches()
	{
		// 装饰件常写 $alphatest 1 但贴图是 1×1 无 alpha；反过来没写开关的件也不该被 alpha 统计带走。
		Assert.False(SourceMaterialConverter.ShouldUseAlphaClip(
			wantsTransparency: false, hasSourceOpacity: true, ShapedAlpha()));
	}

	// ---------------------------------------------------------------- 遮罩从哪来

	/// <summary>构造一张 <paramref name="width"/>×<paramref name="height"/> 的 RGBA 图。</summary>
	private static byte[] Image(int width, int height, Func<int, (byte R, byte G, byte B, byte A)> pixel)
	{
		var rgba = new byte[width * height * 4];
		for (var i = 0; i < width * height; i++)
		{
			var (r, g, b, a) = pixel(i);
			rgba[(i * 4) + 0] = r;
			rgba[(i * 4) + 1] = g;
			rgba[(i * 4) + 2] = b;
			rgba[(i * 4) + 3] = a;
		}

		return rgba;
	}

	private static byte[] Mask(byte[] source, int width, int height, bool inAlpha = false, bool inverted = false, bool dithered = false)
		=> SourceMaterialConverter.BuildOpacityMaskPixels(
			source, width, height,
			inAlpha: inAlpha,
			inverted: inverted,
			dithered: dithered,
			ditherGain: 1.0,
			ditherScale: 1,
			ditherDensity: 1.0,
			ditherPattern: DitherPattern.InterleavedGradient);

	[Fact]
	public void MaskReadsTheAlphaChannelNotTheColour()
	{
		// 逐像素给"颜色与 alpha 完全不相关"的输入：如果遮罩取的是 RGB，结果会完全不同。
		var source = Image(4, 1, i => ((byte)(10 * i), (byte)(20 * i), (byte)(30 * i), i switch
		{
			0 => (byte)0,
			1 => (byte)85,
			2 => (byte)170,
			_ => (byte)255,
		}));

		var mask = Mask(source, 4, 1);

		Assert.Equal(new byte[] { 0, 85, 170, 255 }, new[] { mask[0], mask[4], mask[8], mask[12] });
		// 默认档 A 恒 255（"RGB 携带形状"）。
		Assert.Equal(255, mask[3]);
		Assert.Equal(255, mask[7]);
		// RGB 三通道相同（灰度遮罩）。
		Assert.Equal(mask[4], mask[5]);
		Assert.Equal(mask[4], mask[6]);
	}

	/// <summary>
	/// <b>核心断言：遮罩必须来自被显式传入的那份像素。</b>
	/// <para>
	/// 这条测试存在的理由很直接：调用点曾经传的是"底色"（matcap 承载表面时它已经是
	/// matcap 图像），于是镂空静默失效。现在调用点传的是 <c>MaskRgba</c>（源底色），
	/// 而这里锁住"函数确实只用传入的那份数据、不偷读别处的状态"。
	/// </para>
	/// </summary>
	[Fact]
	public void MaskComesFromExactlyTheArrayItWasGiven()
	{
		var sourceShape = Image(2, 1, i => ((byte)0, (byte)0, (byte)0, i == 0 ? (byte)0 : (byte)255));
		var matcapLike = Image(2, 1, _ => ((byte)200, (byte)200, (byte)200, (byte)255));

		var fromSource = Mask(sourceShape, 2, 1);
		var fromMatcap = Mask(matcapLike, 2, 1);

		Assert.Equal(0, fromSource[0]);      // 源底色第一个像素是镂空的
		Assert.Equal(255, fromMatcap[0]);    // 球面图那块是全不透明的
		Assert.NotEqual(fromSource[0], fromMatcap[0]);
	}

	[Fact]
	public void InvertedSwapsVisibility()
	{
		var source = Image(2, 1, i => ((byte)0, (byte)0, (byte)0, i == 0 ? (byte)0 : (byte)255));
		var mask = Mask(source, 2, 1, inverted: true);
		Assert.Equal(255, mask[0]);
		Assert.Equal(0, mask[4]);
	}

	[Fact]
	public void MaskInAlphaChannelKeepsGreyWhiteAndPutsTheValueInAlpha()
	{
		var source = Image(2, 1, i => ((byte)0, (byte)0, (byte)0, i == 0 ? (byte)0 : (byte)255));
		var mask = Mask(source, 2, 1, inAlpha: true);
		Assert.Equal(255, mask[0]);   // RGB 恒 255
		Assert.Equal(0, mask[3]);     // 值在 alpha
		Assert.Equal(255, mask[7]);
	}

	[Fact]
	public void DitheringProducesOnlyTwoValues()
	{
		// 源 alpha 全 128（均匀半透明）→ 抖动后每个像素只能是 0 或 255。
		var source = Image(16, 16, _ => ((byte)0, (byte)0, (byte)0, (byte)128));
		var mask = Mask(source, 16, 16, dithered: true);

		for (var offset = 0; offset < mask.Length; offset += 4)
		{
			var value = mask[offset];
			Assert.True(value is 0 or 255, $"第 {offset / 4} 个像素抖出了中间值 {value}");
		}
	}

	[Fact]
	public void ShortSourceBufferIsRejectedInsteadOfReadingPastTheEnd()
	{
		// 尺寸说是 4×1（16 字节），实际只给 12 字节：必须抛，不能悄悄读到数组外。
		var source = new byte[12];
		var exception = Assert.Throws<InvalidDataException>(() => Mask(source, 4, 1));
		Assert.Contains("16", exception.Message);
		Assert.Contains("12", exception.Message);
	}
}
