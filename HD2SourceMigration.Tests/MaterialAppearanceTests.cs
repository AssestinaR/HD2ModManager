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
		// 这两个值是照参考模组实测定的，改它们等于改观感——所以钉在这里。
		Assert.Equal(191, MaterialAppearance.MetallicValue);
		Assert.Equal(89, MaterialAppearance.RoughnessValue);
		Assert.Equal(0.9, MaterialAppearance.MatCapMinTint);
	}
}

/// <summary>测试里用的单表达式管道，省一个局部变量。</summary>
internal static class TestPipe
{
	public static TResult Let<T, TResult>(this T value, Func<T, TResult> map) => map(value);
}
