using HD2SourceModel;
using HD2SourceMigration.VpkConversion;

namespace HD2SourceMigration.Tests;

/// <summary>
/// UV 整格归位：源侧把镜像半边的 UV 挪到 1..2 那一格（Source 按 wrap 采样看不出来），
/// HD2 里那些面不显示（实测某模型一侧袖子整只消失）。这里钉住归位的几条不变量。
/// </summary>
public sealed class UvTileNormalizerTests
{
	private static RetargetedVertex V(float u, float v, float x = 0)
		=> new(new Vec3(x, 0, 0), new Vec3(0, 0, 1), u, v, [new RetargetedInfluence(1, 1f)]);

	private static RetargetedMesh Mesh(IReadOnlyList<RetargetedVertex> vertices, params (int, int, int)[] triangles)
		=> new(vertices, [new RetargetedSection(0, triangles)]);

	[Fact]
	public void InRangeMesh_IsReturnedUnchanged()
	{
		var mesh = Mesh([V(0.1f, 0.1f), V(0.9f, 0.1f), V(0.5f, 0.9f)], (0, 1, 2));

		var result = UvTileNormalizer.Apply(mesh);

		Assert.Same(mesh, result.Mesh);
		Assert.Equal(0, result.ShiftedTriangles);
		Assert.Equal(0, result.DuplicatedVertices);
	}

	[Fact]
	public void TriangleInTileOne_IsShiftedBackByWholeTile()
	{
		var mesh = Mesh([V(1.1f, 0.2f), V(1.9f, 0.2f), V(1.5f, 0.8f)], (0, 1, 2));

		var result = UvTileNormalizer.Apply(mesh);

		Assert.Equal(1, result.ShiftedTriangles);
		Assert.Equal(0, result.DuplicatedVertices);
		var us = result.Mesh.Vertices.Select(vertex => vertex.U).ToArray();
		Assert.Equal([0.1f, 0.9f, 0.5f], us, new ToleranceComparer(1e-5f));
		// V 本来就在 0..1，不能动。
		Assert.Equal([0.2f, 0.2f, 0.8f], result.Mesh.Vertices.Select(vertex => vertex.V).ToArray(), new ToleranceComparer(1e-5f));
	}

	[Fact]
	public void NegativeTile_IsShiftedUp_OnBothAxes()
	{
		var mesh = Mesh([V(-0.9f, -0.9f), V(-0.1f, -0.9f), V(-0.5f, -0.1f)], (0, 1, 2));

		var result = UvTileNormalizer.Apply(mesh);

		Assert.All(result.Mesh.Vertices, vertex =>
		{
			Assert.InRange(vertex.U, 0f, 1f);
			Assert.InRange(vertex.V, 0f, 1f);
		});
	}

	[Fact]
	public void SharedVertex_UsedByDifferentTiles_IsDuplicated_AndOtherDataKept()
	{
		// 顶点 1、2 被两个三角形共用：一个在 0..1 格，一个在 1..2 格（镜像接缝）。
		var vertices = new[] { V(0.2f, 0.5f, x: 0), V(0.99f, 0.2f, x: 1), V(0.99f, 0.8f, x: 2), V(1.8f, 0.5f, x: 3) };
		var mesh = Mesh(vertices, (0, 1, 2), (1, 3, 2));

		var result = UvTileNormalizer.Apply(mesh);

		// 第二个三角形重心 U = (0.99+1.8+0.99)/3 = 1.26 → 平移 -1；它用到的 1、2 要各复制一份。
		Assert.Equal(1, result.ShiftedTriangles);
		Assert.Equal(2, result.DuplicatedVertices);
		Assert.Equal(6, result.Mesh.VertexCount);

		var triangles = result.Mesh.Sections[0].Triangles;
		Assert.Equal((0, 1, 2), triangles[0]);
		var shifted = triangles[1];
		// 第一个三角形的顶点保持原样。
		Assert.Equal(0.99f, result.Mesh.Vertices[1].U, 5);
		// 第二个三角形的三个顶点都平移了一格，位置/权重跟着原顶点走。
		Assert.Equal(-0.01f, result.Mesh.Vertices[shifted.A].U, 5);
		Assert.Equal(0.8f, result.Mesh.Vertices[shifted.B].U, 5);
		Assert.Equal(-0.01f, result.Mesh.Vertices[shifted.C].U, 5);
		Assert.Equal(1.0, result.Mesh.Vertices[shifted.A].Position.X);
		Assert.Equal(3.0, result.Mesh.Vertices[shifted.B].Position.X);
		Assert.Equal(2.0, result.Mesh.Vertices[shifted.C].Position.X);
		Assert.Same(vertices[1].Influences, result.Mesh.Vertices[shifted.A].Influences);
	}

	[Fact]
	public void SectionsAndMaterials_ArePreserved()
	{
		var vertices = new[] { V(1.1f, 0.1f), V(1.9f, 0.1f), V(1.5f, 0.9f), V(0.1f, 0.1f), V(0.9f, 0.1f), V(0.5f, 0.9f) };
		var mesh = new RetargetedMesh(vertices, [new RetargetedSection(7, [(0, 1, 2)]), new RetargetedSection(3, [(3, 4, 5)])]);

		var result = UvTileNormalizer.Apply(mesh);

		Assert.Equal([7, 3], result.Mesh.Sections.Select(section => section.SourceMaterialIndex).ToArray());
		Assert.Equal(mesh.TriangleCount, result.Mesh.TriangleCount);
	}

	[Fact]
	public void TriangleStraddlingAnIntegerBoundary_StillLeavesOneVertexOutside()
	{
		// U = 0.75 / 1.0 / 1.25：重心 1.0 → 平移 −1 → −0.25 / 0.0 / 0.25。
		// 重心进了 [0,1)，跨度也小于 1，但仍有一个顶点在外。
		// 这是**有意保留**的边界行为：整数平移在 wrap 采样下等价，但引擎侧对越界 UV 的预处理
		// （校验、面剔除…）是否也等价没有查证 —— 钉住它，免得以后有人以为"归位后一定都在 0..1 里"。
		var mesh = Mesh([V(0.75f, 0.5f), V(1.0f, 0.5f), V(1.25f, 0.5f)], (0, 1, 2));

		var result = UvTileNormalizer.Apply(mesh);

		Assert.Equal(1, result.ShiftedTriangles);
		Assert.Equal(0, result.DuplicatedVertices);
		var us = result.Mesh.Vertices.Select(vertex => vertex.U).ToArray();
		Assert.Equal([-0.25f, 0.0f, 0.25f], us, new ToleranceComparer(1e-5f));
		Assert.Contains(us, u => u < 0f);
	}

	private sealed class ToleranceComparer(float tolerance) : IEqualityComparer<float>
	{
		public bool Equals(float x, float y) => Math.Abs(x - y) <= tolerance;

		public int GetHashCode(float obj) => 0;
	}
}
