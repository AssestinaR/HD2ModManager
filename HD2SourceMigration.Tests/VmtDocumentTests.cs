using System.Text;
using HD2SourceMigration.Retarget;
using HD2SourceMigration.VpkConversion;

namespace HD2SourceMigration.Tests;

/// <summary>
/// VMT（Valve KeyValues 文本）解析。VMT 是手写的，格式瑕疵很常见，
/// 所以这里钉的是"<b>瑕疵不能变成静默丢数据</b>"——丢掉的代价是"这组材质没有底色"，
/// 比直接报错更难查。
/// </summary>
public sealed class VmtDocumentTests
{
	private static VmtDocument Parse(string text) => VmtDocument.Parse(Encoding.UTF8.GetBytes(text));

	[Fact]
	public void Parse_ReadsShaderAndFlatTopLevelValues()
	{
		// 原始字符串字面量里单个反斜杠就是字面量，别多转义。
		var vmt = Parse("""
			// 一个典型的女武神式 VMT
			VertexLitGeneric
			{
				"$basetexture" "models\survivors\zoey\body"
				"$bumpmap"     "models/survivors/zoey/body_normal"
				"$translucent" "1"
				"$alphatest"   "0"
			}
			""");

		Assert.Equal("VertexLitGeneric", vmt.Shader);
		// 路径规范：反斜杠转正斜杠、去掉 materials/ 前缀、转小写。
		Assert.Equal("models/survivors/zoey/body", vmt.BaseTexture);
		Assert.Equal("models/survivors/zoey/body_normal", vmt.BumpMap);
		Assert.True(vmt.WantsTransparency);          // $translucent 1 就够了
	}

	[Fact]
	public void NormalizePathCollapsesRepeatedSeparators()
	{
		// 真 VMT 里会写重（一层来自 materials/ 前缀，一层来自路径本身）。
		Assert.Equal("models/a/b", VmtDocument.NormalizePath("materials\\models\\\\a\\b"));
	}

	[Fact]
	public void Parse_TreatsMissingQuotesAndBomAndCommentsAsNonFatal()
	{
		var bytes = new List<byte> { 0xEF, 0xBB, 0xBF };
		bytes.AddRange(Encoding.UTF8.GetBytes("""
			UnlitGeneric   // 行尾注释
			{
				$basetexture models/foo/bar    // 没引号
				$alphatest 1
			}
			"""));

		var vmt = VmtDocument.Parse(bytes.ToArray());
		Assert.Equal("UnlitGeneric", vmt.Shader);
		Assert.Equal("models/foo/bar", vmt.BaseTexture);
		Assert.True(vmt.WantsTransparency);
	}

	[Fact]
	public void Parse_SkipsNestedBlocksSoProxiesDoNotLeakIntoTheFlatTable()
	{
		var vmt = Parse("""
			VertexLitGeneric
			{
				"$basetexture" "base"
				Proxies
				{
					"TextureScroll"
					{
						"texturescrollvar" "$basetexturetransform"
						"texturescrollrate" "0.1"
					}
				}
				"$matcap" "1"
			}
			""");

		Assert.Equal("base", vmt.BaseTexture);
		Assert.True(vmt.HasMatCap);
		// 嵌套块里的键不能污染顶层表。
		Assert.Null(vmt.Value("texturescrollvar"));
		Assert.Null(vmt.Value("texturescrollrate"));
	}

	/// <summary>
	/// <c>$matcaptexture</c> 缺省时回落到 <c>$basetexture</c> —— VMT 的常见写法，
	/// 金属装饰件的底色就是从这里来的。
	/// </summary>
	[Fact]
	public void MatCapTextureFallsBackToBaseTextureWhenTheKeyIsAbsent()
	{
		var withKey = Parse("""VertexLitGeneric { "$matcap" "1" "$matcaptexture" "mc" "$basetexture" "base" }""");
		Assert.Equal("mc", withKey.MatCapTexture);

		var withoutKey = Parse("""VertexLitGeneric { "$matcap" "1" "$basetexture" "base" }""");
		Assert.Equal("base", withoutKey.MatCapTexture);

		var noMatCapAtAll = Parse("""VertexLitGeneric { "$basetexture" "base" }""");
		Assert.False(noMatCapAtAll.HasMatCap);
		Assert.Null(noMatCapAtAll.MatCapTexture);
	}

	[Theory]
	[InlineData("\"1\"", true)]
	[InlineData("\"0\"", false)]
	[InlineData("\"true\"", true)]
	[InlineData("\"false\"", false)]
	[InlineData("\"off\"", false)]
	[InlineData("", true)]            // 裸键（无值）当作开
	public void IsEnabledAcceptsTheCommonSpellings(string rawValue, bool expected)
	{
		var text = rawValue.Length == 0
			? "VertexLitGeneric { \"$translucent\" }"
			: $"VertexLitGeneric {{ \"$translucent\" {rawValue} }}";
		Assert.Equal(expected, Parse(text).IsEnabled("$translucent"));
	}

	[Fact]
	public void Parse_OfEmptyOrGarbageInputYieldsAnEmptyDocumentRatherThanThrowing()
	{
		Assert.Equal(string.Empty, Parse(string.Empty).Shader);
		Assert.Null(Parse(string.Empty).BaseTexture);
		Assert.Null(Parse("{{{").BaseTexture);
	}

	[Fact]
	public void NormalizePathStripsTheMaterialsPrefixAndLowercases()
	{
		Assert.Equal("a/b", VmtDocument.NormalizePath("materials\\A\\B"));
		Assert.Equal("a/b", VmtDocument.NormalizePath("A/B"));
	}

	/// <summary>
	/// 模型 addon 自己<b>不带材质定义</b>——这条断言钉住了一个会决定 M3 输入范围的事实。
	/// <para>
	/// 实测 <c>Lunalice_Zoey.vpk</c>：<b>没有 `.vmt`</b>。它确实带 <c>.vtf</c>，但那些是
	/// <c>materials/vgui/*.vtf</c>（HUD/选人图），不是模型贴图。MDL 的材质表给的是<b>裸名</b>
	/// （<c>Shinano_face</c>、<c>ACC_Metal_1</c>…），既不是完整路径、也不在包里。
	/// </para>
	/// <para>
	/// （我一开始把这里写成"没有 vmt/vtf 且只有 13 个条目"，被这条测试当场证伪：
	/// 包里有 vgui 的 vtf，条目也远不止 13 个。所以断言只保留真正成立的那一条。）
	/// </para>
	/// <para>
	/// 推论：M3 的输入不止源 VPK，必须再给一组「材质来源 VPK」并在其中<b>按文件名搜</b>。
	/// 这条一旦失败，说明那个前提变了，得回头重新确认。
	/// </para>
	/// </summary>
	[Fact]
	public void RealData_TheModelAddonCarriesNoVmtDefinitions()
	{
		var vpkPath = Environment.GetEnvironmentVariable("HD2_LEGACY_TEST_VPK");
		if (string.IsNullOrWhiteSpace(vpkPath) || !File.Exists(vpkPath))
			vpkPath = @"D:\SteamLibrary\steamapps\common\Left 4 Dead 2\left4dead2\addons\Lunalice_Zoey.vpk";
		if (!File.Exists(vpkPath))
			return;

		using var archive = HD2SourceModel.VpkArchive.Open(vpkPath);
		Assert.DoesNotContain(archive.Entries, entry => entry.Extension.Equals("vmt", StringComparison.OrdinalIgnoreCase));

		var inputs = SourceRetargetPipeline.LoadSource(vpkPath, "survivor_teenangst");
		Assert.NotEmpty(inputs.Model.MaterialPaths);
		// 裸名：没有目录分隔符。
		Assert.All(inputs.Model.MaterialPaths, path => Assert.DoesNotContain('/', path));
		// 裸名也不在包里的 vgui 贴图集合中——即"引用了但包里没有定义"。
		var vtfNames = archive.Entries
			.Where(entry => entry.Extension.Equals("vtf", StringComparison.OrdinalIgnoreCase))
			.Select(entry => entry.FileName.ToLowerInvariant())
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		Assert.DoesNotContain(inputs.Model.MaterialPaths[0].ToLowerInvariant(), vtfNames);
	}

	/// <summary>
	/// 解析器要能吃下<b>真实的</b> L4D2 原版 VMT（从 <c>pak01</c> 里随机取一批）。
	/// 合成文本证明不了真实格式：原版 VMT 大量使用 <c>Proxies</c> / 无引号路径 /
	/// 反斜杠 + <c>materials/</c> 双重前缀。解析不出底色是最容易静默发生的失败（会表现为白模）。
	/// </summary>
	[Fact]
	public void RealData_StockL4D2VmtFilesParseWithABaseTexture()
	{
		var gameRoot = Environment.GetEnvironmentVariable("HD2_L4D2_ROOT");
		if (string.IsNullOrWhiteSpace(gameRoot) || !Directory.Exists(gameRoot))
			gameRoot = @"D:\SteamLibrary\steamapps\common\Left 4 Dead 2\left4dead2";
		var gameVpk = Path.Combine(gameRoot, "pak01_dir.vpk");
		if (!File.Exists(gameVpk))
			return;

		using var archive = HD2SourceModel.VpkArchive.Open(gameVpk);
		var samples = archive.Entries
			.Where(entry => entry.Extension.Equals("vmt", StringComparison.OrdinalIgnoreCase))
			.Where(entry => entry.Path.StartsWith("materials/models/", StringComparison.OrdinalIgnoreCase))
			.OrderBy(entry => entry.Path, StringComparer.Ordinal)
			.Take(40)
			.ToArray();
		Assert.NotEmpty(samples);

		var withBase = 0;
		foreach (var entry in samples)
		{
			var vmt = VmtDocument.Parse(archive.Read(entry));
			Assert.False(string.IsNullOrWhiteSpace(vmt.Shader), $"{entry.Path} 解析不出着色器名");
			if (!string.IsNullOrWhiteSpace(vmt.BaseTexture))
				withBase++;
		}

		// 原版模型材质绝大多数都有 $basetexture；留出余量给纯 matcap / 特殊着色器。
		Assert.True(withBase * 2 >= samples.Length,
			$"{samples.Length} 份原版 VMT 里只有 {withBase} 份解析出 $basetexture");
	}
}
