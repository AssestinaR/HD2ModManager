using HD2SourceMigration.VpkConversion;

namespace HD2SourceMigration.Tests;

/// <summary>
/// 查明"发光变体"与它的本体在 VMT 上到底差在哪。
/// <para>
/// 背景（第 33 轮）：实测 `Shinano_face` 与 `Shinano_face_e` 指向**同一个 `$basetexture`**，
/// 而材质 id 只由 `$basetexture` 决定，所以两者被去重合并、**发光那层静默丢失**。
/// </para>
/// <para>
/// 要支持发光，先得知道源侧是用哪个键表达的 —— 是 <c>$emission</c> / <c>$selfillum</c> /
/// <c>$emissiveblendbasetexture</c>，还是别的。这条测试就是去问这件事。
/// </para>
/// <para>依赖真实游戏数据；找不到就跳过。</para>
/// </summary>
public sealed class EmissionVariantTests
{
	private static (VmtDocument Base, VmtDocument Variant, string[] Diff)? Load()
	{
		var gameRoot = Environment.GetEnvironmentVariable("HD2_L4D2_ROOT");
		if (string.IsNullOrWhiteSpace(gameRoot) || !Directory.Exists(gameRoot))
			gameRoot = @"D:\SteamLibrary\steamapps\common\Left 4 Dead 2\left4dead2";
		if (!Directory.Exists(gameRoot))
			return null;

		var source = SourceMaterialSource.OpenDirectories(gameRoot, Path.Combine(gameRoot, "addons"));
		if (!source.TryReadVmt("Shinano_face", out var baseBytes)
			|| !source.TryReadVmt("Shinano_face_e", out var variantBytes))
		{
			return null;
		}

		var baseVmt = VmtDocument.Parse(baseBytes);
		var variantVmt = VmtDocument.Parse(variantBytes);
		var keys = baseVmt.Values.Keys
			.Union(variantVmt.Values.Keys, StringComparer.OrdinalIgnoreCase)
			.Where(key => !string.Equals(baseVmt.Value(key), variantVmt.Value(key), StringComparison.OrdinalIgnoreCase))
			.OrderBy(key => key, StringComparer.Ordinal)
			.ToArray();
		return (baseVmt, variantVmt, keys);
	}

	/// <summary>两者必须指向**同一个**底色——这正是它们撞成同一个材质 id 的原因。</summary>
	[Fact]
	public void TheVariantSharesItsBaseTextureWithTheBase()
	{
		var loaded = Load();
		if (loaded is null)
			return;

		Assert.Equal(
			VmtDocument.NormalizePath(loaded.Value.Base.BaseTexture ?? string.Empty),
			VmtDocument.NormalizePath(loaded.Value.Variant.BaseTexture ?? string.Empty));
	}

	/// <summary>
	/// 差异必须是**发光**类的键。实测（第 35 轮）两个变体的差异键恰好是
	/// <c>$emissiontexture</c> / <c>$emissiontint</c> / <c>$outline</c> / <c>$outlinetexture</c>，
	/// 所以这里点名断言前两个 —— 它们是发光的载体。
	/// </summary>
	[Fact]
	public void TheVariantIsDistinguishedByEmissionKeys()
	{
		var loaded = Load();
		if (loaded is null)
			return;

		var diff = loaded.Value.Diff;
		Assert.NotEmpty(diff);
		// 实测（第 35 轮）两个变体的差异键恰好是：
		//   $emissiontexture、$emissiontint、$outline、$outlinetexture
		// 前两个是发光的载体 —— 所以**发光是可实现的**：
		// `advanced` 槽 5 的 **alpha 就是 emission**，把 $emissiontexture 的亮度写进去即可。
		// 后两个（描边）与发光无关，这里不断言。
		Assert.Contains(diff, key => key.Equals("$emissiontexture", StringComparison.OrdinalIgnoreCase));
		Assert.Contains(diff, key => key.Equals("$emissiontint", StringComparison.OrdinalIgnoreCase));
	}
}
