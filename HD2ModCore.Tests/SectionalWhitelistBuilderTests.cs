using HD2ModAdaptation.Analysis;
using HD2ModCore.Infrastructure;
using AdaptationAssetKey = HD2ModAdaptation.PatchReconstruction.AssetKey;

namespace HD2ModCore.Tests;

/// <summary>
/// 锁住「干净资产白名单」的闭包规则：选中的 unit → 它引用的材质 → 那些材质引用的贴图。
/// <para>
/// 这条规则决定了产物里到底带哪些材质：漏掉一层，产物会变成"模型在、材质没了"；
/// 多带一层，用户会发现产物里混进了跟这套护甲无关的材质包。
/// </para>
/// </summary>
public sealed class SectionalWhitelistBuilderTests
{
	private const ulong UnitType = 0x1111111111111111;
	private const ulong MaterialType = 0xeac0b497876adedf;
	private const ulong TextureType = 0xcd4238c6a0c69e32;

	private static AdaptationAssetKey Unit(ulong id) => new(UnitType, id);
	private static AdaptationAssetKey Material(ulong id) => new(MaterialType, id);
	private static AdaptationAssetKey Texture(ulong id) => new(TextureType, id);

	private static PatchAssetReference UnitMaterial(ulong unit, ulong material)
		=> new(Unit(unit), Material(material), PatchReferenceKind.UnitMaterial, 0);

	private static PatchAssetReference MaterialTexture(ulong material, ulong texture)
		=> new(Material(material), Texture(texture), PatchReferenceKind.MaterialTexture, 0);

	[Fact]
	public void 闭包只收被选中unit可达的材质与贴图()
	{
		PatchAssetReference[] references =
		[
			UnitMaterial(1, 10),
			UnitMaterial(2, 20),
			MaterialTexture(10, 100),
			MaterialTexture(20, 200),
		];

		var keys = SectionalWhitelistBuilder.Build([Unit(1)], references);

		Assert.Single(keys.Units);
		Assert.Equal([SectionalWhitelistBuilder.Format(Material(10))], keys.Materials);
		Assert.Equal([SectionalWhitelistBuilder.Format(Texture(100))], keys.Textures);
	}

	[Fact]
	public void 贴图只跟材质走_不跟unit隔层跳()
	{
		// unit 没有直接引用贴图；若实现里把 MaterialTexture 也按 unit 过滤，产物会丢贴图。
		PatchAssetReference[] references =
		[
			UnitMaterial(1, 10),
			MaterialTexture(10, 100),
			MaterialTexture(99, 999),
		];

		var keys = SectionalWhitelistBuilder.Build([Unit(1)], references);

		Assert.Equal([SectionalWhitelistBuilder.Format(Texture(100))], keys.Textures);
	}

	[Fact]
	public void 多个unit共用同一材质时只收一次()
	{
		PatchAssetReference[] references =
		[
			UnitMaterial(1, 10),
			UnitMaterial(2, 10),
			MaterialTexture(10, 100),
		];

		var keys = SectionalWhitelistBuilder.Build([Unit(1), Unit(2)], references);

		Assert.Equal([SectionalWhitelistBuilder.Format(Material(10))], keys.Materials);
	}

	[Fact]
	public void 没有引用关系时材质与贴图为空_但unit仍在()
	{
		var keys = SectionalWhitelistBuilder.Build([Unit(1)], []);

		Assert.Single(keys.Units);
		Assert.Empty(keys.Materials);
		Assert.Empty(keys.Textures);
		Assert.Contains("没有引用任何材质", SectionalWhitelistBuilder.Describe(keys)!, StringComparison.Ordinal);
	}

	[Fact]
	public void 没有unit时给出人话说明()
	{
		var keys = SectionalWhitelistBuilder.Build([], []);

		Assert.Contains("没有任何可转换的 unit", SectionalWhitelistBuilder.Describe(keys)!, StringComparison.Ordinal);
	}

	[Fact]
	public void 格式与解析往返一致()
	{
		PatchAssetReference[] references = [UnitMaterial(1, 10), MaterialTexture(10, 100)];
		var keys = SectionalWhitelistBuilder.Build([Unit(1)], references);

		var parsed = SectionalWhitelistBuilder.Parse(keys);

		Assert.Contains(SectionalWhitelistBuilder.ToRef(Unit(1)), parsed);
		Assert.Contains(SectionalWhitelistBuilder.ToRef(Material(10)), parsed);
		Assert.Contains(SectionalWhitelistBuilder.ToRef(Texture(100)), parsed);
		Assert.Equal(3, parsed.Count);
	}
}
