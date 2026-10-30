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
	private static Fixture? OpenFixture(bool includeAddons = false)
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
	/// 真实数据上的回归闸门：<b>同一张底色同时被"要镂空"和"不要镂空"的材质使用时，
	/// 必须产出两个不同的 HD2 材质</b>，而不是撞成一个。
	/// <para>
	/// 这一条对应的正是用户报的"镂空蕾丝没做"：NekoToon 这套模型里大量材质共用同一张
	/// "贴图表"底色，实测 <c>lower_white01_underwear</c> 一张被 11 个材质用（7 蕾丝 + 4 普通）。
	/// 身份只按底色算时它们撞成同一个 id，装配器去重"先到先得"——每一处冲突都是 advanced 赢，
	/// 蕾丝的 alphaclip 模板被静默丢掉。
	/// </para>
	/// </summary>
	[Fact]
	public void RealData_SharedBaseTextureStillYieldsBothAnOpaqueAndACutoutMaterial()
	{
		var fixture = OpenFixture(includeAddons: true);
		if (fixture is null)
			return;

		// 第一遍只解析 VMT（很便宜），按底色分组，找出"组内既有要透明的、也有不要的"那些组。
		// 只转换这些组——转换要跑 texconv 编 BC7，全模型 92 个材质要两分钟，
		// 而这条测试关心的是"同底色撞不撞"，不需要碰其余的。
		var byBaseTexture = new Dictionary<string, List<(string Name, bool Wants)>>(StringComparer.Ordinal);
		foreach (var name in fixture.MaterialNames)
		{
			if (!fixture.Source.TryReadVmt(name, out var vmtBytes))
				continue;
			var vmt = VmtDocument.Parse(vmtBytes);
			if (string.IsNullOrWhiteSpace(vmt.BaseTexture))
				continue;
			if (!byBaseTexture.TryGetValue(vmt.BaseTexture!, out var list))
				byBaseTexture[vmt.BaseTexture!] = list = [];
			list.Add((name, vmt.WantsTransparency));
		}

		var contested = byBaseTexture
			.Where(pair => pair.Value.Select(item => item.Wants).Distinct().Count() > 1)
			.ToArray();
		Assert.True(contested.Length > 0,
			"样本里没有一张底色同时被要镂空和不要镂空的材质使用——这条闸门失去意义");

		var cutouts = 0;
		foreach (var (baseTexture, items) in contested)
		{
			var ids = new List<(bool Wants, ulong Id, string Template, string Name)>();
			foreach (var (name, wants) in items)
			{
				var result = fixture.Converter.Convert(name);
				if (result.IsValid)
					ids.Add((wants, result.Material!.Id, result.Material!.TemplateName, name));
			}

			// 要透明的那一侧**未必**都真的切成 alphaclip：贴图 alpha 可能不表达镂空，
			// 那正是 TextureAlpha.IsMeaningful 要挡的（装饰件常写 $alphatest 1 但贴图是 1×1）。
			// 但两侧的 id 必须完全不相交——交集非空就说明模板被静默合并掉了。
			var opaqueIds = ids.Where(item => !item.Wants).Select(item => item.Id).ToHashSet();
			var clippedIds = ids.Where(item => item.Wants).Select(item => item.Id).ToHashSet();
			var shared = opaqueIds.Intersect(clippedIds).ToArray();
			Assert.True(shared.Length == 0,
				$"{baseTexture}：不透明与镂空两侧共用了材质 id "
				+ string.Join("、", shared.Select(id => $"0x{id:x16}")));

			cutouts += ids.Count(item => item.Wants && item.Template == "alphaclip");
		}

		// 整条 alphaclip 路径必须真的被走到过，否则上面那条"id 不相交"可能只是
		// 因为一个都没切成 alphaclip 而空转。
		Assert.True(cutouts > 0, "争议底色里一个 alphaclip 材质都没切出来——镂空这条路没被验证到");
	}
}
