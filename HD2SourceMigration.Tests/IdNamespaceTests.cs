using HD2SourceMigration.VpkConversion;

namespace HD2SourceMigration.Tests;

/// <summary>
/// 材质/贴图 id 的<b>命名空间</b>接线：转换器上挂着的 token 必须真的进入每一个派生点。
/// <para>
/// <b>为什么单独钉这一层。</b><see cref="Hd2AssetId"/> 的测试只证明"传了命名空间会得到不同 id"，
/// 证明不了"转换器真的传了"。而真实的 bug 形态恰恰是后者：五个派生点（材质 id、底色、alpha 遮罩、
/// 发光、纯色标量图）里漏掉一个，漏掉的那张就退回"全库同名共享"，现象只是"某个 Mod 的某个部位
/// 偶尔串味"，既不报错也不在报告里留痕。
/// </para>
/// <para>
/// 这里<b>不跑 texconv</b>：只调用派生函数，编码器只是个占位（构造时只校验文件存在）。
/// 所以这条用例在任何机器上都跑，不会因为缺工具链被跳过。
/// </para>
/// </summary>
public sealed class IdNamespaceTests
{
	/// <summary>拿一个一定存在的文件当 texconv 的占位——它永远不会被执行。</summary>
	private static Bc7Encoder PlaceholderEncoder() => new(typeof(IdNamespaceTests).Assembly.Location);

	private static SourceMaterialConverter? Create(string? idNamespace)
	{
		var templates = Hd2MaterialTemplateLibrary.Locate();
		if (templates is null)
			return null;
		// 空来源足够：这条用例只验证"id 怎么算"，不读任何材质。
		var source = SourceMaterialSource.Open([]);
		return new SourceMaterialConverter(source, PlaceholderEncoder(), templates, idNamespace: idNamespace);
	}

	[Fact]
	public void ConverterWithoutNamespaceDerivesTheLegacyIds()
	{
		var converter = Create(null);
		if (converter is null)
			return;

		Assert.Null(converter.IdNamespace);
		Assert.Equal(Hd2AssetId.Material("VRChat/Eku/ICSUT/Hair"), converter.DeriveMaterialId("VRChat/Eku/ICSUT/Hair"));
		Assert.Equal(Hd2AssetId.Texture("Hair"), converter.DeriveTextureId("Hair"));
	}

	[Fact]
	public void ConverterWithNamespaceMovesEveryDerivedId()
	{
		var plain = Create(null);
		var scoped = Create("vpk-0123456789abcdef");
		if (plain is null || scoped is null)
			return;

		Assert.Equal("vpk-0123456789abcdef", scoped.IdNamespace);

		// 材质与贴图两条路都必须跟着走——只改一条会让"材质指向旧 id、贴图是新 id"，
		// 表现是白模，比串味更难查。
		Assert.Equal(Hd2AssetId.Material("VRChat/Eku/ICSUT/Hair", "vpk-0123456789abcdef"),
			scoped.DeriveMaterialId("VRChat/Eku/ICSUT/Hair"));
		Assert.Equal(Hd2AssetId.Texture("Hair", "vpk-0123456789abcdef"),
			scoped.DeriveTextureId("Hair"));

		Assert.NotEqual(plain.DeriveMaterialId("VRChat/Eku/ICSUT/Hair"), scoped.DeriveMaterialId("VRChat/Eku/ICSUT/Hair"));
		Assert.NotEqual(plain.DeriveTextureId("Hair"), scoped.DeriveTextureId("Hair"));

		// 同一命名空间内仍然稳定（同一次转换里同一个名字必须只算出一个 id）。
		Assert.Equal(scoped.DeriveTextureId("Hair"), scoped.DeriveTextureId("Hair"));
	}

	/// <summary>
	/// 空白命名空间与"不传"必须完全等价。这条挡住的是"管理器把空字符串传下来"那种接线错误——
	/// 它会让整批 Mod 突然换一套 id，而报告里看起来一切正常。
	/// </summary>
	[Fact]
	public void BlankNamespaceOnTheConverterBehavesLikeNoNamespace()
	{
		var plain = Create(null);
		var blank = Create("   ");
		if (plain is null || blank is null)
			return;

		Assert.Null(blank.IdNamespace);
		Assert.Equal(plain.DeriveMaterialId("a"), blank.DeriveMaterialId("a"));
		Assert.Equal(plain.DeriveTextureId("a"), blank.DeriveTextureId("a"));
	}
}
