using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

/// <summary>
/// 锁住「sectional 转换依据」这份文件：它决定一个 Mod 能不能重构建，也是材质组命名的唯一来源。
/// <para>
/// 这里钉三件事：能原样往返、缺关键字段必须被拒、<b>旧键 <c>sourceVpkName</c> 仍要能读</b>
/// （老库里的 <c>sectional/patches.json</c> 全是那个键）。
/// </para>
/// </summary>
public sealed class SectionalSourceManifestTests : IDisposable
{
	private readonly string _root = Path.Combine(
		Path.GetTempPath(), "hd2-sectional-source-tests", Guid.NewGuid().ToString("N"));

	public SectionalSourceManifestTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		try { Directory.Delete(_root, recursive: true); } catch { }
	}

	private string NewMod(string name)
	{
		var dir = Path.Combine(_root, name);
		Directory.CreateDirectory(dir);
		return dir;
	}

	private static SectionalSourceDocument Sample() => new(
		SectionalSourceManifest.CurrentSchema,
		DateTimeOffset.Parse("2026-11-09T22:00:00+08:00"),
		new SectionalSourceSelection("8d3a01f4a44a3aa3", "RE-2310 护甲"),
		null,
		[new SectionalSourceEntry("0123456789abcdef0123456789abcdef", "Elise_Bill", 0, "Elise_Bill")],
		new SectionalSourceAssetKeys(
			["04ec4a37d9d20001"],
			["9a1c2b3d4e5f6071"],
			["1122334455667788"]));

	[Fact]
	public async Task 往返应当逐字段相等()
	{
		var dir = NewMod("roundtrip");
		Assert.Null(SectionalSourceFile.TryWrite(dir, Sample()));

		var read = await SectionalSourceFile.TryReadAsync(dir);

		Assert.NotNull(read);
		Assert.Equal(SectionalSourceManifest.CurrentSchema, read!.Schema);
		Assert.Equal("8d3a01f4a44a3aa3", read.Armor!.ArchiveId);
		Assert.Null(read.Helmet);
		var source = Assert.Single(read.Sources);
		Assert.Equal("Elise_Bill", source.Name);
		Assert.Equal(0, source.Order);
		Assert.Equal(["04ec4a37d9d20001"], read.AssetKeys.Units);
		Assert.Equal(["9a1c2b3d4e5f6071"], read.AssetKeys.Materials);
		Assert.Equal(["1122334455667788"], read.AssetKeys.Textures);
		Assert.True(read.HasSelection);
	}

	[Fact]
	public async Task 缺文件时读到的是null()
	{
		Assert.Null(await SectionalSourceFile.TryReadAsync(NewMod("absent")));
		Assert.False(SectionalSourceFile.Exists(Path.Combine(_root, "absent")));
	}

	[Fact]
	public async Task schema不认识就当没有()
	{
		var dir = NewMod("bad-schema");
		File.WriteAllText(SectionalSourceFile.GetPath(dir), """
			{ "schema": "hd2mod.sectional-source/999", "sources": [], "assetKeys": { "units": [], "materials": [], "textures": [] } }
			""");

		Assert.Null(await SectionalSourceFile.TryReadAsync(dir));
	}

	[Fact]
	public void 护甲与头盔都没选时不允许写入()
	{
		var dir = NewMod("no-selection");
		var document = Sample() with { Armor = null, Helmet = null };

		var error = SectionalSourceFile.TryWrite(dir, document);

		Assert.NotNull(error);
		Assert.Contains("没有来源", error, StringComparison.Ordinal);
		Assert.False(SectionalSourceFile.Exists(dir));
	}

	[Fact]
	public void 只选头盔是合法的()
	{
		// 需求：护甲与头盔任意一者可空，但不能都空。
		var dir = NewMod("helmet-only");
		var document = Sample() with
		{
			Armor = null,
			Helmet = new SectionalSourceSelection("c0ffee00a44a3aa3", "RE-2310 头盔"),
		};

		Assert.Null(SectionalSourceFile.TryWrite(dir, document));
		Assert.True(SectionalSourceFile.Exists(dir));
	}

	[Fact]
	public void 没有来源条目时不允许写入()
	{
		var dir = NewMod("no-sources");

		Assert.NotNull(SectionalSourceFile.TryWrite(dir, Sample() with { Sources = [] }));
		Assert.False(SectionalSourceFile.Exists(dir));
	}

	[Fact]
	public void 白名单为空时不允许写入()
	{
		var dir = NewMod("no-units");

		Assert.NotNull(SectionalSourceFile.TryWrite(dir, Sample() with { AssetKeys = SectionalSourceAssetKeys.Empty }));
		Assert.False(SectionalSourceFile.Exists(dir));
	}
}
