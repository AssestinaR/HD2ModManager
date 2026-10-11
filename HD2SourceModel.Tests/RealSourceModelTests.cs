namespace HD2SourceModel.Tests;

/// <summary>
/// 真实 VPK 回归：只有本机装了求生之路 2 且样本文件存在时才跑，否则静默跳过。
/// <para>
/// 合成样本验证"解析逻辑对不对"，这个回归验证"对格式的理解对不对"——两者缺一不可：
/// 合成样本是我按同一份理解造的，只有真实文件能证伪那份理解。
/// </para>
/// <para>
/// 用环境变量 <c>HD2_LEGACY_TEST_VPK</c> 指向别的 VPK 即可换样本。
/// </para>
/// </summary>
public sealed class RealSourceModelTests
{
	private const string DefaultVpkPath =
		@"D:\SteamLibrary\steamapps\common\Left 4 Dead 2\left4dead2\addons\Lunalice_Zoey.vpk";

	private const string ModelStem = "survivor_teenangst";

	/// <summary>源模型的骨骼名带这个前缀；剥掉它才能与 A2 的对应表对上。</summary>
	private const string ValveBipedPrefix = "ValveBiped.Bip01_";

	private static string ShortName(string name) =>
		name.StartsWith(ValveBipedPrefix, StringComparison.Ordinal)
			? name[ValveBipedPrefix.Length..]
			: name;

	private static string? ResolveVpk()
	{
		var configured = Environment.GetEnvironmentVariable("HD2_LEGACY_TEST_VPK");
		if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
			return configured;

		return File.Exists(DefaultVpkPath) ? DefaultVpkPath : null;
	}

	private static VpkEntry FindModel(VpkArchive archive) =>
		archive.WithExtension("mdl")
			.Where(entry => Path.GetFileName(entry.Path).Contains(ModelStem, StringComparison.OrdinalIgnoreCase))
			.OrderBy(entry => Path.GetFileName(entry.Path).Length)
			.ThenBy(entry => Path.GetFileName(entry.Path), StringComparer.Ordinal)
			.First();

	[Fact]
	public void RealVpk_Zoey_MatchesTheDocumentedCounts()
	{
		var vpkPath = ResolveVpk();
		if (vpkPath is null)
			return;

		using var archive = VpkArchive.Open(vpkPath);
		var mdlEntry = FindModel(archive);
		var stem = mdlEntry.Path[..^4];

		var model = SourceModelReader.Read(archive.Read(mdlEntry));
		Assert.DoesNotContain(model.Diagnostics, item => item.Code == "NonInvertibleBindMatrix");

		// A1~A4 离线验证时测得的基准数字。
		Assert.Equal(127, model.Bones.Count);
		Assert.Equal(57, model.JiggleBones.Count());
		Assert.Equal(32, model.BodyParts.Count);

		var vertices = SourceVertexReader.Read(archive.Read(archive.Find(stem + ".vvd")!), out _, out var lod0Count);

		// VVD 里存的是 121511 个唯一顶点；装配后是 121704，因为 strip group 之间会复制顶点做缓存局部性。
		Assert.Equal(121511, lod0Count);
		Assert.Equal(121511, vertices.Count);

		var indexEntry = archive.Find(stem + ".dx90.vtx")
			?? archive.WithExtension("vtx")
				.Where(entry => Path.GetFileName(entry.Path)
					.StartsWith(Path.GetFileName(stem), StringComparison.OrdinalIgnoreCase))
				.OrderBy(entry => Path.GetFileName(entry.Path).Length)
				.ThenBy(entry => Path.GetFileName(entry.Path), StringComparer.Ordinal)
				.First();

		var indexBodyParts = SourceIndexReader.Read(archive.Read(indexEntry), out _, out _);
		var geometry = SourceGeometryBuilder.Build(model, vertices, indexBodyParts);

		Assert.Equal(121704, geometry.Vertices.Count);
		Assert.Equal(159363, geometry.Triangles.Count);

		// 逐块对照：这几个数字把整条 MDL→VTX→VVD 链路都钉住了——顶点起点、mesh 起点、
		// strip group 索引任何一环读错，分块计数都会变。
		// bodypart 名带作者的中文标注（例如 "裙Dress_Skirt"），所以按后缀匹配。
		Assert.Equal(32, geometry.Parts.Count);
		var skirt = geometry.Parts.Single(part => part.BodyPart.EndsWith("Dress_Skirt", StringComparison.Ordinal));
		Assert.Equal(25041, skirt.VertexCount);
		Assert.Equal(30140, skirt.TriangleCount);
		var shoes = geometry.Parts.Single(part => part.BodyPart.EndsWith("platform shoes", StringComparison.Ordinal));
		Assert.Equal(11472, shoes.VertexCount);
		Assert.Equal(13122, shoes.TriangleCount);
		var head = geometry.Parts.Single(part => part.BodyPart == "head");
		Assert.Equal(6450, head.VertexCount);
		Assert.Equal(9876, head.TriangleCount);
	}

	[Fact]
	public void RealVpk_Zoey_ReadsTheAuthorsUtf8BodyPartAnnotations()
	{
		var vpkPath = ResolveVpk();
		if (vpkPath is null)
			return;

		using var archive = VpkArchive.Open(vpkPath);
		var model = SourceModelReader.Read(archive.Read(FindModel(archive)));

		// 名字是 UTF-8；用 ASCII 解会变成一串 '?'，下游的"哪一块是裙子"就全靠猜了。
		Assert.Contains(model.BodyParts, part => part.Name == "裙Dress_Skirt");
		Assert.Contains(model.BodyParts, part => part.Name == "猫耳CatEars");
		Assert.Contains(model.BodyParts, part => part.Name == "厚底高跟platform shoes");
	}

	[Fact]
	public void RealVpk_Zoey_JiggleBonesHangOffTheExpectedParents()
	{
		var vpkPath = ResolveVpk();
		if (vpkPath is null)
			return;

		using var archive = VpkArchive.Open(vpkPath);
		var model = SourceModelReader.Read(archive.Read(FindModel(archive)));
		var byIndex = model.Bones.ToDictionary(bone => bone.Index);

		// 头发挂在 Head1 下、裙摆挂在 Pelvis 下——"额外骨"最典型的两个归属。
		// 骨名普遍读不出来（source_extra_N），所以这里同时钉住"有名字的那几个归属"与"没名字的占位名"。
		var jiggleParents = model.JiggleBones
			.Select(bone => ShortName(byIndex[bone.ParentIndex].DisplayName))
			.ToArray();

		Assert.Equal(57, jiggleParents.Length);
		Assert.Equal(15, jiggleParents.Count(name => name == "Head1"));
		Assert.Equal(4, jiggleParents.Count(name => name == "Pelvis"));
		Assert.Equal(6, jiggleParents.Count(name => name == "Spine"));
		Assert.Equal(4, jiggleParents.Count(name => name == "Spine4"));
		Assert.Equal(2, jiggleParents.Count(name => name == "L_Calf"));
		Assert.Equal(2, jiggleParents.Count(name => name == "R_Calf"));

		// 剩下的是骨名读不出来的 helper（大袖子之类），必须落在 source_extra_N 上而不是空串。
		Assert.Equal(
			jiggleParents.Length - 33,
			jiggleParents.Count(name => name.StartsWith("source_extra_", StringComparison.Ordinal)));
	}
}
