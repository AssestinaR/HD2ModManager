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
		// 占位名现在带**按来源模型派生**的命名空间（`j<12hex>/source_extra_N`），见 SourceBone.DisplayName。
		Assert.Equal(
			jiggleParents.Length - 33,
			jiggleParents.Count(name => name.Contains("source_extra_", StringComparison.Ordinal)));
	}

	[Fact]
	public void RealVpk_Zoey_JiggleParametersAndTheTwinTailChainMatchTheIndependentParse()
	{
		var vpkPath = ResolveVpk();
		if (vpkPath is null)
			return;

		var inspection = SourceModelInspector.Inspect(vpkPath, ModelStem);

		// 这份数字同时被两条互不共享代码的链路确认过：
		//   · 管理器（C# 读 mdl）
		//   · 独立的 Python 直读 mdl（tmp/physbone-recon/export_jiggle_json.py）
		// 数值一旦漂移，就说明解析或序列化的字段顺序出了问题。
		Assert.Equal(57, inspection.JiggleBones.Count);
		Assert.All(inspection.JiggleBones, bone =>
		{
			Assert.False(string.IsNullOrWhiteSpace(bone.BoneName));
			Assert.True(bone.ParentBoneIndex >= -1 && bone.ParentBoneIndex < inspection.BoneCount);
			Assert.Contains("flexible", bone.FlagNames);
		});

		// 作者用了两套预设，不是一套：
		//   · 头发/裙摆那 55 根：30 单位长、末端质量 200、yaw/pitch 各 50 刚度 7 阻尼
		//   · 脊柱那 2 根（Spine4 下的）：20 单位长、末端质量 0、pitch 刚度 200 —— 明显更"硬"
		// 把两套都钉住，才能区分"我们读对了"和"我们只读对了大多数"。
		var hairPreset = inspection.JiggleBones.Where(bone => bone.Parameters.Length == 30f).ToArray();
		var spinePreset = inspection.JiggleBones.Where(bone => bone.Parameters.Length == 20f).ToArray();
		Assert.Equal(55, hairPreset.Length);
		Assert.Equal(2, spinePreset.Length);
		Assert.All(hairPreset, bone =>
		{
			Assert.Equal(200f, bone.Parameters.TipMass, 3);
			Assert.Equal(50f, bone.Parameters.YawStiffness, 3);
			Assert.Equal(7f, bone.Parameters.YawDamping, 3);
			Assert.Equal(50f, bone.Parameters.PitchStiffness, 3);
		});
		Assert.All(spinePreset, bone =>
		{
			Assert.Equal(0f, bone.Parameters.TipMass, 3);
			Assert.Equal(5f, bone.Parameters.YawDamping, 3);
			Assert.Equal(200f, bone.Parameters.PitchStiffness, 3);
		});

		// 双马尾：Head1 下最长的一条 3 节链 72 → 73 → 74，另一侧 75 → 76 → 77 镜像。
		var byIndex = inspection.JiggleBones.ToDictionary(bone => bone.BoneIndex);
		Assert.Equal(72, byIndex[73].ParentBoneIndex);
		Assert.Equal(73, byIndex[74].ParentBoneIndex);
		Assert.Equal(75, byIndex[76].ParentBoneIndex);
		Assert.Equal(76, byIndex[77].ParentBoneIndex);

		// 两条链的根挂在同一个父骨上，且世界 bind 位置在 X 上互为镜像。
		// 注意镜像轴是 X：这里的坐标来自 WorldBind（逆 bind 矩阵），与 mdl 里骨结构的
		// 局部偏移不是同一个坐标系——按局部偏移的直觉会写错轴。
		Assert.Equal(byIndex[72].ParentBoneIndex, byIndex[75].ParentBoneIndex);
		Assert.Equal("ValveBiped.Bip01_Head1", byIndex[72].ParentName);
		Assert.Equal(byIndex[72].WorldPosition.X, -byIndex[75].WorldPosition.X, 2);
		Assert.Equal(byIndex[72].WorldPosition.Z, byIndex[75].WorldPosition.Z, 2);

		// 角度限值只有两档：马尾这一整条 0.611 rad（约 35°），另有一批更紧的 0.209。
		Assert.Equal(0.611f, byIndex[72].Parameters.AngleLimit, 3);
		Assert.Equal(0.611f, byIndex[73].Parameters.AngleLimit, 3);
		Assert.Equal(0.611f, byIndex[74].Parameters.AngleLimit, 3);
		var distinctLimits = inspection.JiggleBones
			.Select(bone => Math.Round(bone.Parameters.AngleLimit, 3))
			.Distinct()
			.OrderBy(value => value)
			.ToArray();
		Assert.Equal(new[] { 0.209, 0.611 }, distinctLimits);
	}
}
