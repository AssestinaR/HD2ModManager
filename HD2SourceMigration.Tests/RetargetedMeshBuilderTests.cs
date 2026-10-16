using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2SourceModel;
using HD2SourceMigration.Retarget;
using HD2SourceMigration.Segmentation;
using HD2SourceMigration.VpkConversion;

namespace HD2SourceMigration.Tests;

/// <summary>
/// M2 写 Unit 前的几何准备：把一份切分结果变成"可以直接编码进 Unit 的顶点"。
/// <para>
/// 这里钉住的是<b>游戏侧真的会读</b>的那几条约束，错了不会报错、只会变形或丢权重：
/// </para>
/// <list type="number">
/// <item>每顶点最多四条影响（<c>vec4_half</c> 权重 + <c>vec4_uint8</c> 下标）。</item>
/// <item>权重和必须是 1——不然蒙皮会把顶点往骨头之间拉偏。</item>
/// <item>每一条影响的骨哈希都必须能在 HD2 avatar 骨架里找到，否则调色板编译会失败。</item>
/// <item><b>多个源骨解到同一根 HD2 骨时必须先合并</b>：不合并会在同一根骨上留下两条影响，
/// 四槽读法会把它当成两根不同的骨。</item>
/// </list>
/// </summary>
public sealed class RetargetedMeshBuilderTests
{
	private const string DefaultVpkPath =
		@"D:\SteamLibrary\steamapps\common\Left 4 Dead 2\left4dead2\addons\Lunalice_Zoey.vpk";

	private const string DefaultAvatarTocPath =
		@"E:\Data\source\repos\WpfApp1\tmp\rig-lab\avatar\unit_0x4d1c334d294dfa97.toc";

	private const string DefaultBoneHashPath =
		@"E:\Data\source\repos\WpfApp1\tmp\pb-ref\HD2PhysBone-ReferenceSource-main\kit\tools\bonehash.txt";

	private const string ModelStem = "survivor_teenangst";

	private static string? Resolve(string variable, string fallback)
	{
		var configured = Environment.GetEnvironmentVariable(variable);
		if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
			return configured;
		return File.Exists(fallback) ? fallback : null;
	}

	[Fact]
	public void RealData_EveryBuiltVertexHasBoundedNormalisedResolvableInfluences()
	{
		var vpkPath = Resolve("HD2_LEGACY_TEST_VPK", DefaultVpkPath);
		var tocPath = Resolve("HD2_AVATAR_TOC", DefaultAvatarTocPath);
		var boneHashPath = Resolve("HD2_BONEHASH", DefaultBoneHashPath);
		if (vpkPath is null || tocPath is null || boneHashPath is null)
			return;

		var inputs = SourceRetargetPipeline.LoadSource(vpkPath, ModelStem);
		var rig = SourceRetargetPipeline.LoadRig(tocPath, boneHashPath);
		var result = SourceRetargetPipeline.Run(inputs, rig);
		var avatar = UnitMeshReader.ReadTransformInfoFromUnitToc(File.ReadAllBytes(tocPath));
		var split = SourceSegmentation.Split(inputs.Model, inputs.Geometry, new BodyRegionResolver(rig), result.Resolved);

		var known = avatar.NameHashes.ToHashSet();
		foreach (var group in split.Groups)
		{
			var mesh = RetargetedMeshBuilder.Build(group, inputs.Geometry, result, avatar);
			Assert.Equal(group.VertexCount, mesh.VertexCount);
			Assert.Equal(group.TriangleCount, mesh.TriangleCount);

			foreach (var vertex in mesh.Vertices)
			{
				Assert.InRange(vertex.Influences.Count, 1, 4);
				var total = vertex.Influences.Sum(influence => influence.Weight);
				// 权重是 float32（编码目标是 vec4_half），所以容差按 float32 的尺度定；
				// 编码前 CanonicalMeshPreparation 会再归一化一次，游戏拿到的和是精确 1。
				Assert.True(Math.Abs(total - 1.0) < 1e-6, $"{group.Name} 的权重和是 {total}");
				Assert.All(vertex.Influences, influence => Assert.Contains(influence.BoneHash, known));
				// 合并过的证据：同一根骨不会出现两次。
				Assert.Equal(vertex.Influences.Count, vertex.Influences.Select(item => item.BoneHash).Distinct().Count());
			}

			// 三角形按源材质分段；每段非空，且所有下标都落在本份顶点表内。
			Assert.NotEmpty(mesh.Sections);
			Assert.All(mesh.Sections, section => Assert.NotEmpty(section.Triangles));
			Assert.Equal(mesh.Sections.Count, mesh.Sections.Select(section => section.SourceMaterialIndex).Distinct().Count());
			Assert.Equal(group.TriangleCount, mesh.TriangleCount);
			foreach (var (a, b, c) in mesh.Sections.SelectMany(section => section.Triangles))
			{
				Assert.InRange(a, 0, mesh.VertexCount - 1);
				Assert.InRange(b, 0, mesh.VertexCount - 1);
				Assert.InRange(c, 0, mesh.VertexCount - 1);
			}
		}
	}
}
