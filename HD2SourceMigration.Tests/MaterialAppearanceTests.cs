using System.Text;
using HD2SourceMigration.VpkConversion;

namespace HD2SourceMigration.Tests;

/// <summary>
/// NekoToon matcap 的判定与烘焙。判错的后果很具体：
/// <b>金属装饰件渲染成纯黑</b>（该烘没烘），或者<b>高光被烤死在底色里</b>（不该烘却烘了）。
/// </summary>
public sealed class MaterialAppearanceTests
{
	private static VmtDocument Parse(string text) => VmtDocument.Parse(Encoding.UTF8.GetBytes(text));

	[Fact]
	public void ATintAtOrAboveTheThresholdMeansTheMatCapIsTheSurface()
	{
		Assert.True(Parse("""VertexLitGeneric { "$matcap" "1" "$matcaptint" "{1 1 1}" }""")
			.Let(MaterialAppearance.Evaluate).MatCapIsTheSurface);

		// 阈值是 0.9，边界要包含。
		Assert.True(Parse("""VertexLitGeneric { "$matcap" "1" "$matcaptint" "{0.9 0.9 0.9}" }""")
			.Let(MaterialAppearance.Evaluate).MatCapIsTheSurface);

		// 只差一点就不该烘：那只是视角相关的叠加高光，PBR 自会处理。
		Assert.False(Parse("""VertexLitGeneric { "$matcap" "1" "$matcaptint" "{0.89 0.89 0.89}" }""")
			.Let(MaterialAppearance.Evaluate).MatCapIsTheSurface);
	}

	[Fact]
	public void WithoutAMatCapNothingIsBaked()
	{
		Assert.False(Parse("""VertexLitGeneric { "$basetexture" "base" }""")
			.Let(MaterialAppearance.Evaluate).MatCapIsTheSurface);
		Assert.False(Parse("""VertexLitGeneric { "$matcap" "0" "$basetexture" "base" }""")
			.Let(MaterialAppearance.Evaluate).MatCapIsTheSurface);
	}

	/// <summary>
	/// <c>$matcaptint</c> 缺失时不该烘——没有 tint 就等于没有 matcap 强度。
	/// <c>$matcaptexture</c> 单独出现时 <see cref="VmtDocument.HasMatCap"/> 仍为真，
	/// 但 tint 为 0，所以判定仍是"不烘"。
	/// </summary>
	[Fact]
	public void AMatCapTextureWithoutATintIsNotBaked()
	{
		var verdict = MaterialAppearance.Evaluate(Parse("""VertexLitGeneric { "$matcaptexture" "mc" }"""));
		Assert.True(verdict.HasMatCap);
		Assert.Equal(0, verdict.Tint);
		Assert.False(verdict.MatCapIsTheSurface);
	}

	[Theory]
	[InlineData("{1 1 1}", 1.0)]
	[InlineData("255 255 255", 1.0)]      // 255 制
	[InlineData("{0.9 0.9 0.9}", 0.9)]
	[InlineData("1 0 0", 1.0)]            // 取最大值
	[InlineData("{229.5 0 0}", 0.9)]      // 255 制 + 小数
	[InlineData("", 0.0)]
	[InlineData("{0 0 0}", 0.0)]
	[InlineData("garbage", 0.0)]
	public void TintParsingAcceptsBothRangesAndTakesTheMax(string raw, double expected)
	{
		Assert.Equal(expected, MaterialAppearance.ParseTint(raw), 3);
	}

	[Fact]
	public void BakeAddsTheScaledMatCapMeanAndClamps()
	{
		// 底色 15（源侧近黑），matcap 平均 200、tint 1.0 → 215。
		var rgba = MaterialAppearance.SolidRgba(4, 15, 15, 15, 0);
		MaterialAppearance.BakeMatCap(rgba, [200, 100, 250], 1.0);
		Assert.Equal(215, rgba[0]);
		Assert.Equal(115, rgba[1]);
		// 15 + 250 = 265 → 夹到 255。
		Assert.Equal(255, rgba[2]);
		// alpha 不动。
		Assert.Equal(0, rgba[3]);
	}

	[Fact]
	public void BakeRejectsMalformedInput()
	{
		Assert.Throws<ArgumentException>(() => MaterialAppearance.BakeMatCap(new byte[4], [1, 2], 1));
		Assert.Throws<InvalidDataException>(() => MaterialAppearance.BakeMatCap(new byte[7], [1, 2, 3], 1));
		Assert.Throws<ArgumentOutOfRangeException>(() => MaterialAppearance.BakeMatCap(new byte[4], [1, 2, 3], -1));
	}

	[Fact]
	public void TheMetallicAndRoughnessConstantsMatchTheShippedRecipe()
	{
		// 这两个值直接决定"够不够金属"，改它们等于改观感——所以钉在这里。
		//
		// 历史（实机对照逐档走出来的）：
		//   191 / 89 → "氧化过的铝 / 珍珠的反光色，不够金属"（0.75 金属度留了 25% 漫反射）
		//   255 / 20 → 镀铬镜面，看着对但**实机更暗**（高光瓣太窄，环境被照成"亮点+大片暗"）
		//   255 / 51 → 丝光。高光瓣变宽、暗部被填平，整体比 0.08 更亮，金属感也还在。
		Assert.Equal(255, MaterialAppearance.MetallicValue);
		Assert.Equal(51, MaterialAppearance.RoughnessValue);
		Assert.Equal(51 / 255.0, MaterialAppearance.DefaultMetalRoughness);
		Assert.Equal(0.9, MaterialAppearance.MatCapMinTint);
	}

	/// <summary>
	/// <c>FlattenToMetalF0</c>：整片涂成<b>中性</b>平色，连色相也不保留。
	/// <para>
	/// 这条钉的是"转换结果不能取决于装了哪个可选的颜色变体包"——matcap 由 C1/C2、
	/// B1/B2/B3 这些可选包提供，一旦取它的色相，同一份源换个变体包金属色就变了。
	/// </para>
	/// </summary>
	[Fact]
	public void FlatteningToMetalF0IsNeutralAndDropsTheSourcePattern()
	{
		// 一张明显偏暖的输入（模拟金色系 matcap）也必须出中性。
		var warm = MaterialAppearance.SolidRgba(4, 250, 220, 180);
		MaterialAppearance.FlattenToMetalF0(warm, 0.96);

		var expected = MaterialAppearance.LinearToSrgbByte(0.96);
		for (var offset = 0; offset < warm.Length; offset += 4)
		{
			Assert.Equal(expected, warm[offset + 0]);
			Assert.Equal(expected, warm[offset + 1]);
			Assert.Equal(expected, warm[offset + 2]);
			// alpha 一律 0：底色不承担自发光，也不承担形状。
			Assert.Equal(0, warm[offset + 3]);
		}

		// 一张偏冷的输入（模拟银色系 matcap）结果必须**一模一样**。
		var cool = MaterialAppearance.SolidRgba(4, 180, 220, 250);
		MaterialAppearance.FlattenToMetalF0(cool, 0.96);
		Assert.Equal(warm, cool);

		Assert.Throws<ArgumentOutOfRangeException>(() => MaterialAppearance.FlattenToMetalF0(new byte[4], double.NaN));
		Assert.Throws<InvalidDataException>(() => MaterialAppearance.FlattenToMetalF0(new byte[7], 0.96));
	}

	/// <summary>
	/// <c>IsContentFreeBase</c>：只有"底色没有自己的**颜色**"时 matcap 才算承载表面。
	/// <para>
	/// 两个方向都有实机依据：
	/// </para>
	/// <list type="bullet">
	/// <item><b>不能只看 tint。</b>Lilium_Sancta 的花瓣底色是 <c>Mat_Ornament_Pearl</c> 真图
	/// （alpha 里就是花瓣形状），但 VMT 写着 <c>$matcaptint "[1 1 1 1]"</c>——
	/// 只看 tint 会把它们判成"matcap 就是表面"，连带把 alphaclip 关掉，
	/// 结果花瓣变成不透明白方块 + 金属色。</item>
	/// <item><b>"整片同色"不算没颜色。</b>MourningVeil 的玻璃鞋跟 <c>mat_gem1_shoes</c>
	/// 是 64×64 纯色 197——曾经被判成"没有内容"→ 金属 → 实机"玻璃变成了金属"。
	/// 一条纯色但真实的底色仍然**是一个颜色**。</item>
	/// </list>
	/// </summary>
	[Fact]
	public void ContentFreeBaseMeansTinyOrNearBlackOnly()
	{
		// 1×1 占位图：实测 gray / gray1 / m_metal / pearl 全是 1×1。
		Assert.True(MaterialAppearance.IsContentFreeBase(1, 1, [85, 85, 85, 255]));

		// 大片但整片近黑（实测 mat_metal 平均亮度 17）：颜色全来自 matcap。
		Assert.True(MaterialAppearance.IsContentFreeBase(64, 64, Solid(64, 15)));

		// 大片纯色但**明亮**（实测玻璃鞋跟 mat_gem1_shoes 平均 143/197）：
		// 它有自己的颜色，matcap 只是高光层 —— 不能判成"没有内容"。
		Assert.False(MaterialAppearance.IsContentFreeBase(64, 64, Solid(64, 150)));
		Assert.False(MaterialAppearance.IsContentFreeBase(64, 64, Solid(64, 197)));

		// 有细节的真贴图：不能判成"没有内容"，否则花瓣 / 衣服会被 matcap 顶掉。
		Assert.False(MaterialAppearance.IsContentFreeBase(64, 64, Checker(64)));
	}

	/// <summary>
	/// <c>IsUniformlyTranslucent</c>：alpha **整片均匀且明显 &lt; 255** = 整体半透明。
	/// <para>
	/// 实测 MourningVeil 的玻璃鞋跟 <c>mat_gem1_shoes</c> 底图 alpha 恒 102（≈40% 不透明）。
	/// <c>IsMeaningful</c> 要求 <c>Spread ≥ 3</c>，于是它被判成"没有 alpha"→ 不透明 →
	/// 实机是一块不透明白塑料，用户看到的就是"玻璃鞋跟变成了金属/塑料"。
	/// </para>
	/// <para>
	/// 阈值刻意取 <c>Max ≤ 200</c>：差几个量化单位的不透明图很常见，
	/// 把那种当半透明会让"本该不透明的东西突然透了"——那个方向的误判更难查。
	/// </para>
	/// </summary>
	[Fact]
	public void UniformlyLowAlphaMeansTheWholeSurfaceIsTranslucent()
	{
		// 玻璃鞋跟：整片 102。
		var glass = TextureAlpha.Measure(SolidAlpha(8, 102));
		Assert.False(glass.IsMeaningful);
		Assert.True(glass.IsUniformlyTranslucent);

		// 完全不透明：不是半透明。
		var opaque = TextureAlpha.Measure(SolidAlpha(8, 255));
		Assert.False(opaque.IsMeaningful);
		Assert.False(opaque.IsUniformlyTranslucent);

		// 只差几个量化单位的不透明图：不能当半透明（否则本该实心的东西会透）。
		Assert.False(TextureAlpha.Measure(SolidAlpha(8, 248)).IsUniformlyTranslucent);

		// 有形状的 alpha：走 IsMeaningful，不走这一条。
		var shaped = TextureAlpha.Measure(CheckerAlpha(8));
		Assert.True(shaped.IsMeaningful);
		Assert.False(shaped.IsUniformlyTranslucent);
	}

	/// <summary>一半像素全透明、一半全不透明：模拟"alpha 在表达形状"。</summary>
	private static byte[] CheckerAlpha(int size)
	{
		var rgba = new byte[size * size * 4];
		for (var offset = 0; offset < rgba.Length; offset += 4)
		{
			var opaque = (offset / 4) % 2 == 0;
			rgba[offset] = 200;
			rgba[offset + 1] = 200;
			rgba[offset + 2] = 200;
			rgba[offset + 3] = (byte)(opaque ? 255 : 0);
		}

		return rgba;
	}

	private static byte[] SolidAlpha(int size, byte alpha)
	{
		var rgba = new byte[size * size * 4];
		for (var offset = 0; offset < rgba.Length; offset += 4)
		{
			rgba[offset] = 200;
			rgba[offset + 1] = 200;
			rgba[offset + 2] = 200;
			rgba[offset + 3] = alpha;
		}

		return rgba;
	}

	private static byte[] Solid(int size, byte value)
	{
		var rgba = new byte[size * size * 4];
		for (var offset = 0; offset < rgba.Length; offset += 4)
		{
			rgba[offset] = value;
			rgba[offset + 1] = value;
			rgba[offset + 2] = value;
			rgba[offset + 3] = 255;
		}

		return rgba;
	}

	private static byte[] Checker(int size)
	{
		var rgba = new byte[size * size * 4];
		for (var offset = 0; offset < rgba.Length; offset += 4)
		{
			var value = (byte)((offset / 4) % 2 == 0 ? 240 : 40);
			rgba[offset] = value;
			rgba[offset + 1] = value;
			rgba[offset + 2] = value;
			rgba[offset + 3] = 255;
		}

		return rgba;
	}
}

/// <summary>测试里用的单表达式管道，省一个局部变量。</summary>
internal static class TestPipe
{
	public static TResult Let<T, TResult>(this T value, Func<T, TResult> map) => map(value);
}
