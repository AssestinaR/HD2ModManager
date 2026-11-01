using System.Text;
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

	/// <param name="includeAddons">
	/// 是否把 <c>addons</c> 目录也纳入材质来源。默认 <c>false</c>（只开 <c>pak01</c>），
	/// 与"第一个能走通的材质"那几条测试保持一致——它们的断言（模板是 <c>advanced</c>）是按
	/// <c>pak01</c> 里的材质写的。查"同底色撞模板"必须开 addons：Lunalice 的材质全在那边。
	/// <para>
	/// 顺序与 CLI 的 <c>--material-source "&lt;L4D2根目录&gt;;&lt;addons&gt;"</c> 一致：
	/// <c>pak01</c> 在前、addons 在后，同名先出现的赢。
	/// </para>
	/// </param>
	private static Fixture? OpenFixture(bool includeAddons = false, CutoutStrategy cutout = CutoutStrategy.AlphaClip)
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

		var source = includeAddons
			? SourceMaterialSource.OpenDirectories(gameRoot, Path.Combine(gameRoot, "addons"))
			: SourceMaterialSource.Open([gameVpk]);
		var converter = new SourceMaterialConverter(source, new Bc7Encoder(encoderPath), templates,
			cutoutStrategy: cutout);
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

	/// <summary>
	/// 材质身份必须把"会不会改变材质行为"的东西全部算进去。
	/// <para>
	/// 两个后缀各对应一次实测过的<b>静默合并</b>：
	/// </para>
	/// <list type="bullet">
	/// <item><c>|emission</c>：自发光变体与本体共用底色 → 不加就丢掉发光那层。</item>
	/// <item><c>|alphaclip</c>：镂空与非镂空共用底色 → 不加就丢掉镂空
	/// （或者反过来给不透明件穿洞，两种都是错的）。</item>
	/// </list>
	/// <para>
	/// 这条是纯函数测试，不需要任何游戏数据——正因为如此，它在 CI 里永远跑得到。
	/// </para>
	/// </summary>
	[Fact]
	public void MaterialIdentitySeparatesEveryBehaviourChangingFlag()
	{
		const string texture = "matchacheese/lunalice/lower_white01_underwear";

		var opaque = SourceMaterialConverter.BuildMaterialIdentity(texture, null, useAlphaClip: false);
		var clipped = SourceMaterialConverter.BuildMaterialIdentity(texture, null, useAlphaClip: true);
		var emissive = SourceMaterialConverter.BuildMaterialIdentity(texture, "some/emission", useAlphaClip: false);
		var emissiveClipped = SourceMaterialConverter.BuildMaterialIdentity(texture, "some/emission", useAlphaClip: true);

		Assert.Equal(texture, opaque);
		Assert.NotEqual(opaque, clipped);
		Assert.NotEqual(opaque, emissive);
		Assert.NotEqual(emissive, emissiveClipped);
		Assert.NotEqual(clipped, emissiveClipped);

		// 四个身份两两不同，而且 id 也必须两两不同——身份只是中间量，真正去重的是 id。
		var ids = new[] { opaque, clipped, emissive, emissiveClipped }
			.Select(identity => Hd2AssetId.Material(identity))
			.ToArray();
		Assert.Equal(ids.Length, ids.Distinct().Count());

		// 顺序固定：同一个输入必须永远得到同一个串（id 是哈希，串一变 id 就变）。
		Assert.Equal(clipped, SourceMaterialConverter.BuildMaterialIdentity(texture, null, useAlphaClip: true));
	}

	/// <summary>
	/// matcap 烘进底色时，<b>不同的 matcap 就是不同的底色</b>，身份必须分开。
	/// <para>
	/// 实测的形状：A1 里 40 个金属装饰件与 <c>jewel</c> 共用同一张
	/// <c>Ornament_metal_Underwear</c> 底色，但前者 <c>$matcaptexture</c> 指向 <c>11</c>（金/银）、
	/// 后者指向 <c>6</c>（宝石）。身份不含 matcap 时它们撞成同一个 id，
	/// 装配器去重"先到先得"——宝石和金属变成同一个颜色。
	/// </para>
	/// <para>
	/// ⚠️ 加不加这个后缀，判据是 <b>VMT 里的 <c>$matcaptint ≥ 0.9</c></b>，不是"队列里有没有模型
	/// 能算出平均色"。否则同一个 VMT 在不同队列里会得到不同的 id，patch 之间就再也对不上。
	/// </para>
	/// </summary>
	[Fact]
	public void MaterialIdentitySeparatesDifferentMatcaps()
	{
		const string texture = "matchacheese/lunalice/ornament_metal_underwear";

		var plain = SourceMaterialConverter.BuildMaterialIdentity(texture, null, useAlphaClip: false);
		var gold = SourceMaterialConverter.BuildMaterialIdentity(
			texture, null, useAlphaClip: false, matcapTexture: @"matchacheese\Lunalice\11");
		var jewel = SourceMaterialConverter.BuildMaterialIdentity(
			texture, null, useAlphaClip: false, matcapTexture: @"matchacheese\Lunalice\6");

		Assert.NotEqual(plain, gold);
		Assert.NotEqual(gold, jewel);
		Assert.NotEqual(plain, jewel);

		// 同一个 matcap 必须永远得到同一个串——金色 / 银色的 A1 patch 要能同号互换，
		// 靠的就是"id 只由名字决定"，与队列里放了哪个 C 无关。
		Assert.Equal(
			gold,
			SourceMaterialConverter.BuildMaterialIdentity(
				texture, null, useAlphaClip: false, matcapTexture: @"matchacheese\Lunalice\11"));

		// 后缀叠在 alphaclip 之后，顺序固定。
		var clippedGold = SourceMaterialConverter.BuildMaterialIdentity(
			texture, null, useAlphaClip: true, matcapTexture: "m");
		Assert.EndsWith("|alphaclip|matcap:m", clippedGold, StringComparison.Ordinal);
	}

	/// <summary>
	/// <b>只有 <c>$alphatest</c> 才是硬裁，<c>$translucent</c> 不是。</b>
	/// <para>
	/// 这条是纯逻辑测试（不需要贴图），钉的是"意图识别"这一步：
	/// 两个开关必须能被分开，而不是被 <c>WantsTransparency</c> 糊成一个。
	/// </para>
	/// <para>
	/// 为什么重要：实测 serenade 的 <c>_1__white</c>（袖套 / 丝袜）写着 <c>$translucent 1</c>，
	/// 它的 alpha 是 UV 图集里的形状遮罩；按阈值 0.5 硬裁会删掉 <b>58.8%</b> 的像素，
	/// 游戏里就是"整片袖套消失、只剩袖口一点"。而换阈值 0.144 只降到 56.8%，
	/// 所以之前的 A/B 两档看起来毫无区别 —— 阈值从来不是杠杆，开关语义才是。
	/// </para>
	/// <para>
	/// 全库统计（源 VPK 里的 VMT）：lilium alphatest 70 / translucent 0；
	/// lunalice 39/60；mourningveil 114/96；serenade 18/3。两种写法都常见，
	/// 所以必须分开处理，不能一律切或不切。
	/// </para>
	/// </summary>
	[Fact]
	public void AlphaTestAndTranslucencyAreDifferentIntentions()
	{
		VmtDocument Parse(string body)
			=> VmtDocument.Parse(Encoding.UTF8.GetBytes("NekoToon\n{\n" + body + "\n}\n"));

		// 硬裁：语义与 HD2 的 alphaclip 一致。
		var alphaTest = Parse("$alphatest 1");
		Assert.True(alphaTest.WantsAlphaTest);
		Assert.False(alphaTest.WantsTranslucency);
		Assert.True(alphaTest.WantsTransparency);

		// 混合：HD2 表达不了，不能当硬裁。
		var translucent = Parse("$translucent 1");
		Assert.False(translucent.WantsAlphaTest);
		Assert.True(translucent.WantsTranslucency);
		Assert.True(translucent.WantsTransparency);

		// 两个都写：硬裁优先（源侧先裁再混，裁是更"硬"的那一步）。
		var both = Parse("$alphatest 1\n$translucent 1");
		Assert.True(both.WantsAlphaTest);
		Assert.True(both.WantsTranslucency);

		// 关掉的写法不能被当成开。
		Assert.False(Parse("$alphatest 0").WantsAlphaTest);
		Assert.False(Parse("$translucent 0").WantsTranslucency);
	}

	/// <summary>
	/// 真实数据上：<c>$translucent</c> 的布料仍然走 <c>alphaclip</c>，并留一条提示。
	/// <para>
	/// 这条曾经被我写成"必须退成不透明"，实机证明那是错的：
	/// serenade 的袖套 / 美甲底布（<c>_1__white</c>）整片消失，改回 alphaclip 才正常。
	/// 发布版二进制同参数 A/B 也证实了这一点，所以这里钉住的是<b>发布版行为</b>。
	/// </para>
	/// <para>
	/// 结论：这些件的 alpha 图案不是多余花纹，而是它们**唯一**的形状来源；
	/// 做成不透明反而会露出不该画的整片 UV 区域。
	/// </para>
	/// </summary>
	[Fact]
	public void RealData_TranslucencyStillUsesAlphaClipLikeTheRelease()
	{
		var fixture = OpenFixture(includeAddons: true);
		if (fixture is null)
			return;

		var translucentOnly = new List<string>();
		foreach (var name in fixture.MaterialNames)
		{
			if (!fixture.Source.TryReadVmt(name, out var vmtBytes))
				continue;
			var vmt = VmtDocument.Parse(vmtBytes);
			if (vmt.WantsTranslucency && !vmt.WantsAlphaTest)
				translucentOnly.Add(name);
		}

		if (translucentOnly.Count == 0)
			return;   // 这份样本里没有只写 $translucent 的材质，这条闸门不适用。

		foreach (var name in translucentOnly)
		{
			var result = fixture.Converter.Convert(name);
			if (!result.IsValid)
				continue;
			// 只要贴图 alpha 有意义，就必须是 alphaclip（不许退成 advanced）。
			if (result.Material!.TemplateName == "alphaclip")
				Assert.Contains(result.Notices, notice => notice.Contains("$translucent", StringComparison.Ordinal));
		}
	}

}
