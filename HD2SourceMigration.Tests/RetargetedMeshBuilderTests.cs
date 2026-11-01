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
		// 生产路径的顺序是"先追加合成骨，再建网格"（VpkPatchAssembler）。
		// 这里必须照做，否则 jiggle 骨的权重哈希落在 avatar 的 NameHashes 之外，会被当成越界丢掉。
		var avatar = UnitTransformInfoAppender.Append(
			UnitMeshReader.ReadTransformInfoFromUnitToc(File.ReadAllBytes(tocPath)), result.ExtraBones)!;
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

	/// <summary>
	/// 合成骨（jiggle 骨）的节点位置必须和它管辖的顶点贴在一起。
	/// <para>
	/// 为什么必须钉这条：合成骨的位置过去直接用"对齐后的源关节"，**没有经过 A-pose 等姿态变换**，
	/// 而网格顶点经过了。结果在 Lilium 上，袖子骨离自己的顶点 0.65~1.31 m（头发只差 0.17 m，
	/// 因为头几乎没被姿态改动），实机上就是"骨骼在动、那块布却横着捅出去一米多"。
	/// 修法是把关节位置用锚点的 <c>Deform</c> 搬过去；这条断言就是它的守门员。
	/// </para>
	/// </summary>
	[Fact]
	public void RealData_JiggleBonesSitInsideTheirVertices()
	{
		var vpkPath = Environment.GetEnvironmentVariable("HD2_WEIGHT_VPK") ?? Resolve("HD2_LEGACY_TEST_VPK", DefaultVpkPath);
		var tocPath = Resolve("HD2_AVATAR_TOC", DefaultAvatarTocPath);
		var boneHashPath = Resolve("HD2_BONEHASH", DefaultBoneHashPath);
		if (vpkPath is null || tocPath is null || boneHashPath is null)
			return;

		var stem = Environment.GetEnvironmentVariable("HD2_WEIGHT_STEM") ?? ModelStem;
		var inputs = SourceRetargetPipeline.LoadSource(vpkPath, stem);
		var rig = SourceRetargetPipeline.LoadRig(tocPath, boneHashPath);
		var result = SourceRetargetPipeline.Run(inputs, rig);
		if (result.ExtraBones.Count == 0)
			return;   // 这份资产没有 jiggle 骨，没什么可判的

		var avatar = UnitTransformInfoAppender.Append(
			UnitMeshReader.ReadTransformInfoFromUnitToc(File.ReadAllBytes(tocPath)), result.ExtraBones)!;

		// 节点世界位置
		var nodeByHash = new Dictionary<uint, (double X, double Y, double Z)>();
		var hashToIndex = new Dictionary<uint, int>();
		for (var i = 0; i < avatar.NameHashes.Count; i++)
		{
			hashToIndex[avatar.NameHashes[i]] = i;
			var v = avatar.Matrices[i].Values;
			nodeByHash[avatar.NameHashes[i]] = (v[12], v[13], v[14]);
		}

		// 每个骨哈希 → 它管辖（权重 ≥0.5）的顶点质心
		var sums = new Dictionary<uint, (double X, double Y, double Z, double W)>();
		var split = SourceSegmentation.Split(inputs.Model, inputs.Geometry, new BodyRegionResolver(rig), result.Resolved);
		foreach (var group in split.Groups)
		{
			var mesh = RetargetedMeshBuilder.Build(group, inputs.Geometry, result, avatar);
			foreach (var vertex in mesh.Vertices)
			{
				foreach (var influence in vertex.Influences)
				{
					if (influence.Weight < 0.5) continue;
					var acc = sums.TryGetValue(influence.BoneHash, out var previous) ? previous : default;
					sums[influence.BoneHash] = (
						acc.X + vertex.Position.X * influence.Weight,
						acc.Y + vertex.Position.Y * influence.Weight,
						acc.Z + vertex.Position.Z * influence.Weight,
						acc.W + influence.Weight);
				}
			}
		}

		var worst = 0.0;
		var worstName = string.Empty;
		foreach (var extra in result.ExtraBones)
		{
			if (!sums.TryGetValue(extra.NameHash, out var acc) || acc.W <= 0) continue;
			var cx = acc.X / acc.W;
			var cy = acc.Y / acc.W;
			var cz = acc.Z / acc.W;
			var node = nodeByHash[extra.NameHash];
			var distance = Math.Sqrt(
				(cx - node.X) * (cx - node.X) + (cy - node.Y) * (cy - node.Y) + (cz - node.Z) * (cz - node.Z));
			if (distance > worst) { worst = distance; worstName = extra.Name; }
		}

		// 0.5 m 是个宽松上限：正常应当远小于它（Lilium 修好后最大 0.318 m，修前 1.31 m）。
		Assert.True(worst < 0.5, $"合成骨离自己的顶点太远：{worstName} = {worst:F3} m（上限 0.5 m）");
	}
}
