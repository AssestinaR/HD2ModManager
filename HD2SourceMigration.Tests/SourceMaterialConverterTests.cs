using HD2SourceMigration.Retarget;
using HD2SourceMigration.VpkConversion;

namespace HD2SourceMigration.Tests;

/// <summary>
/// 源 VMT + VTF → HD2 <c>Material</c> + <c>Texture</c> 的端到端合成。
/// <para>
/// 这条链的验收标准不是"跑通了"，而是产物<b>同时满足三张契约</b>：
/// 贴图是带 DX10 的 BC7、材质是已知模板且槽位键没被动过、底色 alpha 已清零。
/// 最后一条尤其重要——不清零会让<b>整件护甲自发光</b>，是"看起来能跑但完全不对"的典型。
/// </para>
/// <para>依赖真实游戏数据与 texconv，缺任一项时整体跳过。</para>
/// </summary>
public sealed class SourceMaterialConverterTests
{
	private sealed record Fixture(
		SourceMaterialConverter Converter,
		SourceMaterialSource Source,
		IReadOnlyList<string> MaterialNames);

	private static Fixture? OpenFixture()
	{
		var encoderPath = Bc7Encoder.Locate(Environment.GetEnvironmentVariable("HD2_TEXCONV"));
		var templates = Hd2MaterialTemplateLibrary.Locate();
		if (encoderPath is null || templates is null)
			return null;

		var gameRoot = Environment.GetEnvironmentVariable("HD2_L4D2_ROOT");
		if (string.IsNullOrWhiteSpace(gameRoot) || !Directory.Exists(gameRoot))
			gameRoot = @"D:\SteamLibrary\steamapps\common\Left 4 Dead 2\left4dead2";
		var gameVpk = Path.Combine(gameRoot, "pak01_dir.vpk");
		if (!File.Exists(gameVpk))
			return null;

		var modelVpk = Environment.GetEnvironmentVariable("HD2_LEGACY_TEST_VPK");
		if (string.IsNullOrWhiteSpace(modelVpk) || !File.Exists(modelVpk))
			modelVpk = Path.Combine(gameRoot, "addons", "Lunalice_Zoey.vpk");
		if (!File.Exists(modelVpk))
			return null;

		var inputs = SourceRetargetPipeline.LoadSource(modelVpk, "survivor_teenangst");
		if (inputs.Model.MaterialPaths.Count == 0)
			return null;

		var source = SourceMaterialSource.Open([gameVpk]);
		var converter = new SourceMaterialConverter(source, new Bc7Encoder(encoderPath), templates);
		return new Fixture(converter, source, inputs.Model.MaterialPaths);
	}

	/// <summary>找第一个能整条走通的材质——模型引用的材质有一部分不在 pak01 里。</summary>
	private static MaterialSynthesisResult? ConvertFirst(Fixture fixture, out string used)
	{
		foreach (var name in fixture.MaterialNames)
		{
			var result = fixture.Converter.Convert(name);
			if (result.IsValid)
			{
				used = name;
				return result;
			}
		}

		used = string.Empty;
		return null;
	}

	[Fact]
	public void ConvertProducesABc7TextureAndAnAdvancedMaterial()
	{
		var fixture = OpenFixture();
		if (fixture is null)
			return;

		var result = ConvertFirst(fixture, out var used);
		Assert.NotNull(result);

		var material = result!.Material!;
		Assert.Equal("advanced", material.TemplateName);
		Assert.Equal(Hd2AssetId.MaterialPrefix, Hd2AssetId.PrefixOf(material.Id));
		// 底色 1 张 + 共享的法线/金属度 2 张。
		Assert.Equal(3, result.Textures.Count);

		var texture = result.Textures[0];
		Assert.Equal(Hd2AssetId.TexturePrefix, Hd2AssetId.PrefixOf(texture.Id));
		Assert.Equal(340, texture.TocData.Length);
		Assert.Equal("BC7_UNORM_SRGB", texture.FormatName);
		Assert.True(texture.Width > 0 && texture.Height > 0, $"{used} 尺寸为 0");

		// 材质里槽 5 必须指向这张新贴图。
		Assert.Equal(texture.Id, material.TextureIds[SourceMaterialConverter.AdvancedAlbedoSlot]);

		// 材质的 TocData 必须能被自己的读取器读回来，且只有槽 5 变了。
		var parsed = Hd2MaterialAsset.Read(material.TocData);
		var template = Hd2MaterialTemplateLibrary.Locate()!.Load("advanced");
		for (var i = 0; i < template.TextureIds.Count; i++)
		{
			// 底色在槽 5；槽 2（法线+粗糙）与槽 3（金属）被**共享**贴图替换，其余保持模板原值。
			if (i == SourceMaterialConverter.AdvancedAlbedoSlot)
				Assert.Equal(texture.Id, parsed.TextureIds[i]);
			else if (i == SourceMaterialConverter.AdvancedNarSlot || i == SourceMaterialConverter.AdvancedMetallicSlot)
				Assert.NotEqual(template.TextureIds[i], parsed.TextureIds[i]);
			else
				Assert.Equal(template.TextureIds[i], parsed.TextureIds[i]);
			Assert.Equal(template.TextureKeys[i], parsed.TextureKeys[i]);
		}
	}

	/// <summary>
	/// 底色 alpha 必须已经清零。不清零会让整件护甲自发光——这是"看起来能跑但完全不对"
	/// 的典型，所以单独钉一条。
	/// </summary>
	[Fact]
	public void BaseColorAlphaIsZeroed()
	{
		var fixture = OpenFixture();
		if (fixture is null)
			return;

		var result = ConvertFirst(fixture, out _);
		if (result is null)
			return;

		var texture = result.Textures[0];
		// 把 BC7 解回 RGBA 再查 alpha——直接看压缩块里的 alpha 不可靠。
		var dds = Hd2TextureAsset.BuildDds(texture.Width, texture.Height, Hd2TextureAsset.Bc7UnormSrgb, [texture.GpuData]);
		var encoderPath = Bc7Encoder.Locate(Environment.GetEnvironmentVariable("HD2_TEXCONV"));
		if (encoderPath is null)
			return;
		var rgba = new Bc7Encoder(encoderPath).DecodeToRgba(dds);

		// DecodeToRgba 返回**裸像素**（第 27 轮起剥掉了 DDS 头），所以长度就是 w*h*4。
		Assert.Equal(texture.Width * texture.Height * 4, rgba.Length);
		for (var offset = 3; offset < rgba.Length; offset += 4)
			Assert.Equal(0, rgba[offset]);
	}

	/// <summary>
	/// 材质来源里找不到材质时必须<b>明确失败</b>，不能悄悄回落到模板的默认贴图 id
	/// ——那会产出"引用游戏里某个不相干贴图"的材质，比缺贴图更难查。
	/// </summary>
	[Fact]
	public void UnknownMaterialFailsInsteadOfFallingBackToTemplateDefaults()
	{
		var fixture = OpenFixture();
		if (fixture is null)
			return;

		var result = fixture.Converter.Convert("definitely-not-a-real-material-xyz");
		Assert.False(result.IsValid);
		Assert.Null(result.Material);
		Assert.NotEmpty(result.Diagnostics);
	}
}
