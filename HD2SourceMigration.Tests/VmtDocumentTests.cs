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

	/// <summary>
	/// <c>patch</c> 材质：<c>include</c> 指向的基础材质 + 自己的 <c>insert</c>/<c>replace</c>。
	/// <para>
	/// 这是 L4D2 模组里最常见的写法（实测一个 34 份 VMT 的包里 29 份是 patch）。
	/// 旧解析器把嵌套块一律跳过，于是这些材质连 <c>$basetexture</c> 都读不出来，
	/// 整包材质静默消失——用户看到的是"材质包导入成功，游戏里没变"。
	/// </para>
	/// </summary>
	[Fact]
	public void Parse_PatchMaterialMergesIncludeInsertAndReplace()
	{
		var baseVmt = Encoding.UTF8.GetBytes("""
			VertexLitGeneric
			{
				"$basetexture" "placeholder/base"
				"$phong" "1"
				"$phongexponent" "50"
			}
			""");
		var patch = Encoding.UTF8.GetBytes("""
			patch
			{
				include "materials/a/b/base.vmt"
				insert
				{
					"$bumpmap" "a/b/norm"
					"$phong" "0"
				}
				replace
				{
					"$basetexture" "a/b/coat"
				}
			}
			""");

		var vmt = VmtDocument.Parse(patch, name => name.Equals("base", StringComparison.OrdinalIgnoreCase) ? baseVmt : null);

		Assert.True(vmt.IsPatch);
		Assert.Equal("materials/a/b/base.vmt", vmt.IncludePath);
		Assert.Equal("VertexLitGeneric", vmt.Shader);          // 继承自 include
		Assert.Equal("a/b/coat", vmt.BaseTexture);             // replace 覆盖
		Assert.Equal("a/b/norm", vmt.BumpMap);                 // insert 新增
		Assert.Equal("0", vmt.Value("$phong"));                // insert 也覆盖已存在的键
		Assert.Equal("50", vmt.Value("$phongexponent"));        // 没写就保留 include 的值
	}

	/// <summary>
	/// 真实模组形状：基础材质把 <c>$basetexture</c> 写成占位值，子材质用 <c>insert</c> 顶掉它。
	/// <para>
	/// 2026-10-28 用户反馈的"模型进游戏但完全没材质"就是这条被打破了：34 份材质全部因为
	/// <c>$basetexture</c> 还是占位值而合成失败。证据（两个不同作者的包都是同一形状）：
	/// </para>
	/// <code>
	/// materials/&lt;mod&gt;/shader/vmt_base.vmt   "VertexLitGeneric" { "$basetexture" "basetexture" }
	/// materials/&lt;mod&gt;/c_xxx_uber.vmt        patch { include "…/shader/vmt_base.vmt"
	///                                            insert { "$basetexture" "&lt;mod&gt;/…_d" } }
	/// </code>
	/// <para>
	/// 33 份子材质里 <c>replace</c> 出现 0 次，全部靠 <c>insert</c> 覆盖。若 <c>insert</c> 只补缺，
	/// 这些包在源引擎里根本找不到底色贴图，作者不可能没发现。
	/// </para>
	/// </summary>
	[Fact]
	public void Parse_PatchInsertOverridesAPlaceholderFromTheIncludedMaterial()
	{
		var baseVmt = Encoding.UTF8.GetBytes("""
			"VertexLitGeneric"
			{
				"$basetexture" "basetexture"   // 基础贴图路径，由 patch VMT 替换
				"$phong" "1"
			}
			""");
		var patch = Encoding.UTF8.GetBytes("""
			patch
			{
				include	"materials/diyu2026/GF2Exilium/Cheyanne/MaidenDebut/shader/vmt_base.vmt"
				insert
				{
					$basetexture	"diyu2026/GF2Exilium/Cheyanne/MaidenDebut/c_CheyanneSSR0101_slg_p1_body_uber_d"
					$phongboost	"1.245"
				}
			}
			""");

		var vmt = VmtDocument.Parse(patch, name => name.Equals("vmt_base", StringComparison.OrdinalIgnoreCase) ? baseVmt : null);

		Assert.Equal(
			"diyu2026/gf2exilium/cheyanne/maidendebut/c_cheyannessr0101_slg_p1_body_uber_d",
			vmt.BaseTexture);
		Assert.Equal("1", vmt.Value("$phong"));         // 只写了 insert 的键之外，include 的仍然保留
		Assert.Equal("1.245", vmt.Value("$phongboost"));
	}

	[Fact]
	public void Parse_PatchWithoutItsIncludeStillAppliesReplace()
	{
		// 被包含的材质不在这一批里时不能整份丢掉：replace 里的键仍然有效，
		// 上层据此还能判断底色。
		var patch = Encoding.UTF8.GetBytes("""
			patch
			{
				include "materials/missing.vmt"
				replace { "$basetexture" "x/y" }
			}
			""");

		var vmt = VmtDocument.Parse(patch, _ => null);
		Assert.True(vmt.IsPatch);
		Assert.Equal("x/y", vmt.BaseTexture);
	}

	[Fact]
	public void Parse_PatchIncludeCycleDoesNotRecurseForever()
	{
		// 坏模组里可能出现 A include B、B include A。挡不住就是栈溢出。
		var a = Encoding.UTF8.GetBytes("""patch { include "b.vmt" insert { "$basetexture" "a/t" } }""");
		var b = Encoding.UTF8.GetBytes("""patch { include "a.vmt" insert { "$phong" "1" } }""");

		var vmt = VmtDocument.Parse(a, name => name.Equals("b", StringComparison.OrdinalIgnoreCase) ? b : a);

		Assert.True(vmt.IsPatch);
		Assert.Equal("a/t", vmt.BaseTexture);
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
