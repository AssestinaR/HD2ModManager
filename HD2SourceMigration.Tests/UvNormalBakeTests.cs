using HD2SourceModel;
using HD2SourceMigration.VpkConversion;

namespace HD2SourceMigration.Tests;

/// <summary>
/// 把几何烘进 UV 空间得到的「世界方向法线」。
/// <para>
/// <b>为什么这条是 matcap / lightwarp 的前提。</b>源侧这两样都按**世界方向**算
/// （matcap 用视角法线、lightwarp 用 N·L），而法线贴图给的是**切线空间**的方向。
/// 直接拿切线空间法线去查球面图/曲线，细节一抖颜色就跳——实机就是脸上、发梢、衣褶上的色斑。
/// </para>
/// </summary>
public sealed class UvNormalBakeTests
{
	/// <summary>一个正方形，四个角各带一个已知法线；UV 铺满 0..1。</summary>
	private static SourceGeometry Quad(params Vec3[] normals)
	{
		var vertices = new List<SourceVertex>();
		var uvs = new (float U, float V)[] { (0, 0), (1, 0), (1, 1), (0, 1) };
		for (var i = 0; i < 4; i++)
			vertices.Add(new SourceVertex(new Vec3(i % 2, i / 2, 0), normals[i], uvs[i].U, uvs[i].V, default, default, default));

		var triangles = new List<SourceTriangle>
		{
			new(0, 1, 2, 0),
			new(0, 2, 3, 0),
		};
		return new SourceGeometry(vertices, triangles, []);
	}

	[Fact]
	public void BakeFillsTheUvSpaceWithTheSurfaceNormal()
	{
		var plusZ = new Vec3(0, 0, 1);
		var map = UvNormalBake.Bake(Quad(plusZ, plusZ, plusZ, plusZ), materialIndex: 0, resolution: 32);

		Assert.NotNull(map);
		var (x, y, z) = map!.Sample(0.5, 0.5);
		Assert.Equal(0.0, x, 2);
		Assert.Equal(0.0, y, 2);
		Assert.Equal(1.0, z, 2);
	}

	/// <summary>朝向随位置变化时，烘出来的法线也应当跟着变（这正是"世界方向"的意义）。</summary>
	[Fact]
	public void BakeInterpolatesVertexNormals()
	{
		// 左边朝 −X、右边朝 +X（中间从 0 平滑过渡）
		var left = new Vec3(-1, 0, 0);
		var right = new Vec3(1, 0, 0);
		var map = UvNormalBake.Bake(Quad(left, right, right, left), materialIndex: 0, resolution: 64);

		Assert.NotNull(map);
		var leftSample = map!.Sample(0.1, 0.5);
		var rightSample = map.Sample(0.9, 0.5);
		Assert.True(leftSample.X < 0, $"左侧应当朝 −X，实际 {leftSample.X:F2}");
		Assert.True(rightSample.X > 0, $"右侧应当朝 +X，实际 {rightSample.X:F2}");
	}

	[Fact]
	public void OtherMaterialsAreNotBaked()
	{
		Assert.Null(UvNormalBake.Bake(Quad(new Vec3(0, 0, 1), new Vec3(0, 0, 1), new Vec3(0, 0, 1), new Vec3(0, 0, 1)),
			materialIndex: 3, resolution: 16));
	}

	[Fact]
	public void DegenerateTrianglesAreSkipped()
	{
		// 三个顶点 UV 重合 ⇒ 没有可填的像素
		var vertices = new List<SourceVertex>
		{
			new(new Vec3(0, 0, 0), new Vec3(0, 0, 1), 0.5f, 0.5f, default, default, default),
			new(new Vec3(1, 0, 0), new Vec3(0, 0, 1), 0.5f, 0.5f, default, default, default),
			new(new Vec3(0, 1, 0), new Vec3(0, 0, 1), 0.5f, 0.5f, default, default, default),
		};
		var geometry = new SourceGeometry(vertices, [new SourceTriangle(0, 1, 2, 0)], []);
		Assert.Null(UvNormalBake.Bake(geometry, 0, 16));
	}

	[Fact]
	public void BakeAllIndexesByMaterialName()
	{
		var plusZ = new Vec3(0, 0, 1);
		var maps = UvNormalBake.BakeAll(
			Quad(plusZ, plusZ, plusZ, plusZ),
			["materials\\a\\body.vmt"],
			resolution: 16);

		Assert.True(maps.ContainsKey("body"));
	}
}
