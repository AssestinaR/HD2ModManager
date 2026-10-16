using HD2SourceModel;
using HD2SourceMigration.Retarget;
using HD2SourceMigration.Segmentation;

namespace HD2SourceMigration.Tests;

/// <summary>
/// M2 切分的回归测试：源模型必须被切成"七份本体 + 每个可切换 bodygroup 一份配件"，
/// 而且三角形<b>恰好覆盖一次</b>（不重不漏）。
/// <para>
/// 需要样本 VPK、HD2 avatar TocData 与骨名表，缺任何一样就静默跳过（与其余真实数据测试同一约定）。
/// </para>
/// <para>
/// 这里钉住的四条不变量，每一条错了都会在游戏里以不同方式显形：
/// </para>
/// <list type="number">
/// <item><b>不重不漏</b>：漏一份 = 模型上少一块；重一份 = 同一块被画两次（z-fighting 或双倍顶点）。</item>
/// <item><b>七份本体都在</b>：缺哪个槽，哪件原版护甲就盖不住自定义素体对应的部位。</item>
/// <item><b>配件数 = 多变体 bodypart 数</b>：判据是"变体数 &gt; 1"，不是名字——这样换一个模型也成立。</item>
/// <item><b>每份只引用自己的顶点</b>：Unit 各有自己的顶点缓冲，引用别人的顶点会读到越界数据。</item>
/// </list>
/// </summary>
public sealed class RealSegmentationTests
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

	private static SourceSegmentation.Result? Split(out SourceModelDocument model, out SourceGeometry geometry)
	{
		model = null!;
		geometry = null!;
		var vpkPath = Resolve("HD2_LEGACY_TEST_VPK", DefaultVpkPath);
		var tocPath = Resolve("HD2_AVATAR_TOC", DefaultAvatarTocPath);
		var boneHashPath = Resolve("HD2_BONEHASH", DefaultBoneHashPath);
		if (vpkPath is null || tocPath is null || boneHashPath is null)
			return null;

		var inputs = SourceRetargetPipeline.LoadSource(vpkPath, ModelStem);
		var rig = SourceRetargetPipeline.LoadRig(tocPath, boneHashPath);
		var result = SourceRetargetPipeline.Run(inputs, rig);
		model = inputs.Model;
		geometry = inputs.Geometry;
		return SourceSegmentation.Split(inputs.Model, inputs.Geometry, new BodyRegionResolver(rig), result.Resolved);
	}

	[Fact]
	public void RealData_SegmentationCoversEveryTriangleExactlyOnce()
	{
		var split = Split(out _, out var geometry);
		if (split is null)
			return;

		var owner = new Dictionary<int, string>();
		var overlaps = new List<string>();
		foreach (var group in split.Groups)
		{
			foreach (var index in group.TriangleIndices)
			{
				if (!owner.TryAdd(index, group.Name))
					overlaps.Add($"三角形 {index} 同时属于 {owner[index]} 与 {group.Name}");
			}
		}

		Assert.Empty(overlaps.Take(5));
		Assert.Equal(geometry.Triangles.Count, owner.Count);
	}

	[Fact]
	public void RealData_SegmentationProducesAllSevenBodyParts()
	{
		var split = Split(out _, out _);
		if (split is null)
			return;

		var regions = split.BodyParts.Select(group => group.Region).ToHashSet();
		Assert.Equal(BodyRegions.All.OrderBy(region => region), regions.OrderBy(region => region));
		Assert.All(split.BodyParts, group => Assert.True(group.TriangleCount > 0, $"{group.Name} 是空的"));
	}

	[Fact]
	public void RealData_EveryMultiVariantBodyPartBecomesOneComponent()
	{
		var split = Split(out var model, out _);
		if (split is null)
			return;

		var switchable = model.BodyParts.Where(bodyPart => bodyPart.Models.Count > 1).Select(bodyPart => bodyPart.Name);
		var components = split.Components.Select(group => group.Name).ToHashSet(StringComparer.Ordinal);
		Assert.Equal(switchable.OrderBy(name => name, StringComparer.Ordinal), components.OrderBy(name => name, StringComparer.Ordinal));
	}

	[Fact]
	public void RealData_EveryGroupOnlyReferencesItsOwnVertices()
	{
		var split = Split(out _, out var geometry);
		if (split is null)
			return;

		foreach (var group in split.Groups)
		{
			var declared = group.VertexIndices.ToHashSet();
			foreach (var index in group.TriangleIndices)
			{
				var triangle = geometry.Triangles[index];
				Assert.Contains(triangle.A, declared);
				Assert.Contains(triangle.B, declared);
				Assert.Contains(triangle.C, declared);
			}

			Assert.Equal(group.VertexIndices.OrderBy(value => value), group.VertexIndices);
			Assert.Equal(group.TriangleIndices.OrderBy(value => value), group.TriangleIndices);
		}
	}
}
