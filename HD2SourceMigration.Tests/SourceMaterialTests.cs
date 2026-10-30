using System.Text;
using HD2SourceMigration.Retarget;
using HD2SourceMigration.VpkConversion;

namespace HD2SourceMigration.Tests;

/// <summary>
/// 自造资产 id 与"材质来源"定位。
/// <para>
/// id 派生的判据是<b>可复现且与 Python 逐位一致</b>：材质/贴图的 id 就是它们互相引用的键，
/// 差一位就变成"引用了不存在的资产"，而那种错在包里看不出来、只在游戏里表现为白模。
/// 所以这里钉的是确定性、高位前缀、低 60 位的掩码行为，以及"不同名字必然不同 id"。
/// </para>
/// </summary>
public sealed class SourceMaterialTests
{
	[Fact]
	public void DerivedIdsPutThePrefixInTheTopNibbleAndUseOnlySixtyBits()
	{
		var material = Hd2AssetId.Material("models/survivors/zoey/body");
		var texture = Hd2AssetId.Texture("body");

		Assert.Equal(Hd2AssetId.MaterialPrefix, Hd2AssetId.PrefixOf(material));
		Assert.Equal(Hd2AssetId.TexturePrefix, Hd2AssetId.PrefixOf(texture));
		Assert.True((material & ~Hd2AssetId.PayloadMask) == (Hd2AssetId.MaterialPrefix << 60));
	}

	[Fact]
	public void DerivedIdsAreDeterministicAndDistinctPerName()
	{
		// 同名同值——这是"包里引用得上"的前提。
		Assert.Equal(Hd2AssetId.Material("a"), Hd2AssetId.Material("a"));
		Assert.Equal(Hd2AssetId.Texture("a"), Hd2AssetId.Texture("a"));

		// 不同名不同值。
		Assert.NotEqual(Hd2AssetId.Material("a"), Hd2AssetId.Material("b"));

		// **同名不同前缀也必须不同**——材质和贴图共用一个命名空间时不能撞。
		Assert.NotEqual(Hd2AssetId.Material("a"), Hd2AssetId.Texture("a"));

		// 大小写敏感：路径本身是大小写敏感的。
		Assert.NotEqual(Hd2AssetId.Material("Body"), Hd2AssetId.Material("body"));
	}

	/// <summary>
	/// 与 Python 的 <c>make_id</c> 必须逐位一致。这里的期望值是按 Python 的定义
	/// （<c>(prefix&lt;&lt;60) | (sha256(name)[:8] 小端 &amp; mask)</c>）独立算出来的，
	/// 不是从本实现跑出来的——否则就成了自证。
	/// </summary>
	[Fact]
	public void DerivedIdsMatchThePythonDefinition()
	{
		static ulong Reference(ulong prefix, string name)
		{
			var digest = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(name));
			ulong payload = 0;
			for (var i = 7; i >= 0; i--)
				payload = (payload << 8) | digest[i];
			return (prefix << 60) | (payload & 0x0FFF_FFFF_FFFF_FFFF);
		}

		foreach (var name in new[] { "a", "models/survivors/zoey/body", "Shinano_face", "" })
		{
			if (name.Length == 0)
				continue;
			Assert.Equal(Reference(0xA, name), Hd2AssetId.Material(name));
			Assert.Equal(Reference(0xB, name), Hd2AssetId.Texture(name));
		}
	}

	[Fact]
	public void DeriveRejectsEmptyNamesAndOutOfRangePrefixes()
	{
		Assert.Throws<ArgumentException>(() => Hd2AssetId.Derive(0xA, "  "));
		Assert.Throws<ArgumentOutOfRangeException>(() => Hd2AssetId.Derive(0x10, "x"));
	}

	/// <summary>
	/// 用户实测的那个 bug：两个不同作者的 VPK Mod 都带 <c>Hair.vtf</c>（4096² DXT5 与 2048² DXT1），
	/// 名字相同、内容不同。没有命名空间时它们算出<b>同一个贴图 id</b>，两个 Mod 同时装上就只有一个
	/// 能生效，另一个 Mod 的头发会采样到别人的图集。
	/// <para>
	/// 这条钉的是"加上命名空间之后，同名不同来源必须是不同 id"，以及"同一个来源仍然稳定"。
	/// </para>
	/// </summary>
	[Fact]
	public void NamespaceSeparatesTheSameNameComingFromDifferentMods()
	{
		var eku = Hd2AssetId.NamespaceFor("【vrchat】eku-见习奇术师");
		var melty = Hd2AssetId.NamespaceFor("【PUBLIC】Milfy_MeltyDoll八人包");

		// 没有命名空间时就是今天的行为：同名同 id（这正是串味的根因）。
		Assert.Equal(Hd2AssetId.Texture("Hair"), Hd2AssetId.Texture("Hair", null));

		// 有命名空间时：两个来源不同 id，同一个来源稳定。
		Assert.NotEqual(Hd2AssetId.Texture("Hair", eku), Hd2AssetId.Texture("Hair", melty));
		Assert.Equal(Hd2AssetId.Texture("Hair", eku), Hd2AssetId.Texture("Hair", eku));
		Assert.NotEqual(Hd2AssetId.Texture("Hair"), Hd2AssetId.Texture("Hair", eku));

		// 材质 id 同理：同一个角色的两个皮肤包会写同一套 $basetexture 路径。
		const string sharedBase = "models/survivors/survivor_zoey/body";
		Assert.NotEqual(Hd2AssetId.Material(sharedBase, eku), Hd2AssetId.Material(sharedBase, melty));

		// 命名空间不能吃掉高位前缀，否则类型判断全废。
		Assert.Equal(Hd2AssetId.TexturePrefix, Hd2AssetId.PrefixOf(Hd2AssetId.Texture("Hair", eku)));
		Assert.Equal(Hd2AssetId.MaterialPrefix, Hd2AssetId.PrefixOf(Hd2AssetId.Material(sharedBase, eku)));
	}

	/// <summary>
	/// 命名空间 token 由来源包名派生，必须是"同名同 token、异名异 token"，
	/// 且对空白与大小写不敏感——同一个包被复制成不同大小写的名字时不该变成另一套 id。
	/// </summary>
	[Fact]
	public void NamespaceForIsStableAcrossCaseAndWhitespaceAndDistinctPerSource()
	{
		var a = Hd2AssetId.NamespaceFor("Milfy_MeltyDoll八人包");
		Assert.Equal(a, Hd2AssetId.NamespaceFor("  MILFY_MELTYDOLL八人包  "));
		Assert.Equal(a, Hd2AssetId.NamespaceFor("milfy_meltydoll八人包"));
		Assert.NotEqual(a, Hd2AssetId.NamespaceFor("eku-见习奇术师"));
		Assert.StartsWith("vpk-", a);
		Assert.Throws<ArgumentException>(() => Hd2AssetId.NamespaceFor("  "));
	}

	/// <summary>空命名空间必须与"不传"完全等价——旧产物与 Python 对拍都靠这一条。</summary>
	[Fact]
	public void BlankNamespaceIsTheSameAsNoNamespace()
	{
		foreach (var blank in new[] { "", "   " })
		{
			Assert.Equal(Hd2AssetId.Material("a"), Hd2AssetId.Material("a", blank));
			Assert.Equal(Hd2AssetId.Texture("a"), Hd2AssetId.Texture("a", blank));
		}
	}

	[Fact]
	public void SourceMaterialSourceIgnoresNonexistentPathsWithoutThrowing()
	{
		var source = SourceMaterialSource.Open([@"Z:\nope\a.vpk", @"Z:\nope\b.vpk"]);
		Assert.Empty(source.VpkPaths);
		Assert.False(source.TryReadVmt("anything", out _));
		Assert.False(source.TryReadVtf("anything", out _));
		Assert.False(source.ContainsVmt("anything"));
	}

	/// <summary>
	/// 真实数据：在 L4D2 的 <c>pak01</c> 里找模型自引用的材质。
	/// <para>
	/// 这条同时钉住第 3 轮那个发现的可操作版本：材质是<b>裸名</b>，
	/// 所以只能按名字搜；而且模型自己的 addon 里没有，得去别处找。
	/// </para>
	/// </summary>
	[Fact]
	public void RealData_ResolvesModelMaterialsByNameAcrossVpkSets()
	{
		var gameRoot = Environment.GetEnvironmentVariable("HD2_L4D2_ROOT");
		if (string.IsNullOrWhiteSpace(gameRoot) || !Directory.Exists(gameRoot))
			gameRoot = @"D:\SteamLibrary\steamapps\common\Left 4 Dead 2\left4dead2";
		var include = Path.Combine(gameRoot, "pak01_dir.vpk");
		if (!File.Exists(include))
			return;

		var source = SourceMaterialSource.Open([include]);
		Assert.True(source.VmtCount > 0, "pak01 里应当有 VMT");
		Assert.True(source.VtfCount > 0, "pak01 里应当有 VTF");

		// 随机取一个真实 VMT，解出它的 $basetexture，再按名字把贴图读出来。
		var vpkPath = Environment.GetEnvironmentVariable("HD2_LEGACY_TEST_VPK");
		if (string.IsNullOrWhiteSpace(vpkPath) || !File.Exists(vpkPath))
			vpkPath = Path.Combine(gameRoot, "addons", "Lunalice_Zoey.vpk");
		if (!File.Exists(vpkPath))
			return;

		var inputs = SourceRetargetPipeline.LoadSource(vpkPath, "survivor_teenangst");
		var resolved = 0;
		foreach (var name in inputs.Model.MaterialPaths.Take(40))
		{
			if (!source.TryReadVmt(name, out var vmtBytes))
				continue;
			var vmt = VmtDocument.Parse(vmtBytes);
			var baseTexture = vmt.BaseTexture;
			if (string.IsNullOrWhiteSpace(baseTexture))
				continue;
			// 底色的贴图不一定在同名文件上（例如 `_e` 发光图），所以这里只要求"解析得出名字"。
			Assert.DoesNotContain('\\', baseTexture);
			resolved++;
		}

		// 允许少数缺失（自定义材质不在这一个包里），但必须有解析成功的，
		// 否则说明查找方式错了而不是素材不在。
		Assert.True(resolved > 0, $"在 pak01 里按名字一个模型材质都没找到（共 {source.VmtCount} 个 VMT）");
	}
}
