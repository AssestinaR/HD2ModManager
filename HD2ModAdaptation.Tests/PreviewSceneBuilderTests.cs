using System.Numerics;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModAdaptation.Preview;
using Xunit;

namespace HD2ModAdaptation.Tests;

// 作用：锁住预览场景构建器的口径 —— 取哪些网格、怎么取顶点、索引怎么重映射、包围球怎么算。
// 这些一旦悄悄变了，渲染出来的图会"看着有东西但其实缺了一半"，而那是最难查的失败。
//
// 夹具统一用"一个方形面片"（6 顶点 / 2 三角形）当最小可渲染网格：仓库既有的
// UnitGeometryFactsBuilder.HasRenderableGeometry 要求 顶点>3 且 三角形>1，
// 一个三角形会被判成占位网格 —— 那样测的就不是"能画"，而是"会被丢掉"。
// Purpose: Pins the preview scene-builder contract (mesh selection, vertex extraction,
// index remapping, bounding sphere). Fixtures use a two-triangle quad as the smallest
// renderable mesh, because the repository's own HasRenderableGeometry predicate rejects
// single-triangle meshes as placeholders.
public sealed class PreviewSceneBuilderTests
{
	// ---- 顶点抽取 ----------------------------------------------------------------

	[Fact]
	public void BuildScene_ExtractsPositionsNormalsAndMaterialSlot()
	{
		var unit = Unit("00000000000000a1", Quad(0, 0, materialSlot: 7));

		var scene = PreviewSceneBuilder.BuildScene([unit]);

		var mesh = Assert.Single(scene.Meshes);
		Assert.Equal(6, mesh.Vertices.Count);
		Assert.Equal(2, mesh.Triangles.Count);
		Assert.All(mesh.Triangles, triangle => Assert.Equal(7u, triangle.MaterialSlotId));
		Assert.Equal((0, 1, 2), (mesh.Triangles[0].A, mesh.Triangles[0].B, mesh.Triangles[0].C));
		Assert.Equal((3, 4, 5), (mesh.Triangles[1].A, mesh.Triangles[1].B, mesh.Triangles[1].C));
		Assert.Equal(new Vector3(1, 0, 0), mesh.Vertices[1].Position);
		Assert.Equal(new Vector3(0, 0, 1), mesh.Vertices[0].Normal);
	}

	[Fact]
	public void BuildScene_DropsTrianglesThatReferenceVerticesWithoutPosition()
	{
		// 6 个有坐标的顶点 + 3 个只有 uv 的顶点；三角形全部指向那 3 个读不到 position 的顶点。
		// 它们必须被丢掉（不能拿 (0,0,0) 顶上），于是这个网格一个可画三角形都不剩。
		var vertices = new List<UnitRawVertexRecord>
		{
			Vertex(0, 0, 0, 0), Vertex(1, 1, 0, 0), Vertex(2, 0, 1, 0),
			Vertex(3, 1, 1, 0), Vertex(4, 0, 0, 1), Vertex(5, 1, 0, 1),
			UvOnly(6), UvOnly(7), UvOnly(8),
		};
		var raw = new UnitRawMeshData(0, 0, 0, 0,
			[
				new UnitRawMeshSectionData(0, 0,
					[new UnitTriangleIndices(6, 7, 8), new UnitTriangleIndices(6, 8, 0)]),
			],
			[],
			vertices);

		var scene = PreviewSceneBuilder.BuildScene([Unit("00000000000000a2", raw)]);

		Assert.True(scene.IsEmpty);
		Assert.Equal(2, scene.Stats.DroppedTriangles);
		Assert.Contains(scene.Stats.Notes, note => note.Contains("0 个可用三角形"));
	}

	[Fact]
	public void BuildScene_RemapsSparseVertexIndices()
	{
		var vertices = new List<UnitRawVertexRecord>
		{
			Vertex(5, 0, 0, 0), Vertex(7, 1, 0, 0), Vertex(9, 0, 1, 0),
			Vertex(11, 1, 1, 0), Vertex(13, 0, 0, 1), Vertex(15, 1, 0, 1),
		};
		var raw = new UnitRawMeshData(0, 0, 0, 0,
			[new UnitRawMeshSectionData(0, 0, [new UnitTriangleIndices(5, 7, 9), new UnitTriangleIndices(11, 13, 15)])],
			[],
			vertices);

		var scene = PreviewSceneBuilder.BuildScene([Unit("00000000000000a3", raw)]);

		var mesh = Assert.Single(scene.Meshes);
		Assert.Equal(6, mesh.Vertices.Count);
		// 顺序按原索引升序：5 → 0、7 → 1、9 → 2、11 → 3、13 → 4、15 → 5。
		Assert.Equal((0, 1, 2), (mesh.Triangles[0].A, mesh.Triangles[0].B, mesh.Triangles[0].C));
		Assert.Equal((3, 4, 5), (mesh.Triangles[1].A, mesh.Triangles[1].B, mesh.Triangles[1].C));
		Assert.Equal(new Vector3(1, 0, 0), mesh.Vertices[1].Position);
	}

	// ---- 网格选择 ----------------------------------------------------------------

	[Fact]
	public void BuildScene_SkipsCullingBodiesStaticMeshesAndHigherLods()
	{
		var infos = new[]
		{
			MeshInfo(0, 0),
			MeshInfo(1, 0, culling: true),
			MeshInfo(2, 0, isStatic: true),
			MeshInfo(3, 1),
		};
		var raws = new[] { Quad(0, 0), Quad(1, 0), Quad(2, 0), Quad(3, 1) };

		var scene = PreviewSceneBuilder.BuildScene([new PreviewUnitSource("00000000000000a4", Model(infos, raws))]);

		Assert.Single(scene.Meshes);
		Assert.Equal(4, scene.Stats.Meshes);
		Assert.Equal(1, scene.Stats.SkippedCullingBodies);
		Assert.Equal(1, scene.Stats.SkippedStaticMeshes);
		Assert.Equal(1, scene.Stats.SkippedHigherLods);
		Assert.Equal(0, scene.Stats.SkippedFallbackLods);
		Assert.Equal(0, scene.Stats.SkippedPlaceholders);
	}

	[Fact]
	public void BuildScene_KeepsRenderableMinusOneMeshesWhenThereIsNoLod0()
	{
		// 单 LOD 的 unit 会把 LodIndex 报成 -1。只要它有真几何，照画。
		var model = Model(Quad(0, -1));
		var scene = PreviewSceneBuilder.BuildScene([new PreviewUnitSource("00000000000000a5", model)]);
		Assert.Single(scene.Meshes);
		Assert.Equal(-1, scene.Meshes[0].LodIndex);
		Assert.Equal(0, scene.Stats.SkippedFallbackLods);
		Assert.Equal(0, scene.Stats.SkippedPlaceholders);
	}

	[Fact]
	public void BuildScene_DropsTheThreeVertexPlaceholderMesh()
	{
		// 实测（Elise_Bill 素体）：有的 unit 同时带 lod=0 和一份 3 顶点 1 三角形的 lod=-1 占位网格。
		// 两套都画会在模型上多出一个三角形碎片；而占位网格本身也不是几何。
		var infos = new[] { MeshInfo(0, 0), MeshInfo(1, -1) };
		var raws = new[]
		{
			Quad(0, 0),
			Mesh(1, -1, [(0, 0, 0), (1, 0, 0), (0, 1, 0)], [(0, 1, 2, 0)]),
		};

		var scene = PreviewSceneBuilder.BuildScene([new PreviewUnitSource("00000000000000a8", Model(infos, raws))]);

		var mesh = Assert.Single(scene.Meshes);
		Assert.Equal(0, mesh.LodIndex);
		Assert.Equal(1, scene.Stats.SkippedFallbackLods);
	}

	[Fact]
	public void BuildScene_TreatsATwoTriangleMeshAsRenderableButNotAOneTriangleOne()
	{
		// 判据来自仓库既有的 UnitGeometryFactsBuilder.HasRenderableGeometry（顶点>3 且 三角形>1）。
		var oneTriangle = Model(Mesh(0, 0,
			[(0, 0, 0), (1, 0, 0), (0, 1, 0), (1, 1, 0)],
			[(0, 1, 2, 0)]));
		var scene = PreviewSceneBuilder.BuildScene([new PreviewUnitSource("00000000000000a9", oneTriangle)]);
		Assert.True(scene.IsEmpty);
		Assert.Equal(1, scene.Stats.SkippedPlaceholders);
	}

	// ---- 包围球 ------------------------------------------------------------------

	[Fact]
	public void BuildScene_ComputesBoundingSphereFromFarthestVertex()
	{
		// 单位立方体的 8 个角：中心在原点，半径 = √3。
		var corners = new (float X, float Y, float Z)[]
		{
			(-1, -1, -1), (1, -1, -1), (-1, 1, -1), (1, 1, -1),
			(-1, -1, 1), (1, -1, 1), (-1, 1, 1), (1, 1, 1),
		};
		var vertices = corners.Select((corner, index) => Vertex((uint)index, corner.X, corner.Y, corner.Z)).ToList();
		var triangles = new List<UnitTriangleIndices>
		{
			new(0, 1, 2), new(1, 3, 2), new(4, 6, 5), new(5, 6, 7),
			new(0, 4, 1), new(1, 4, 5), new(2, 3, 6), new(3, 7, 6),
			new(0, 2, 4), new(2, 6, 4), new(1, 5, 3), new(3, 5, 7),
		};
		var raw = new UnitRawMeshData(0, 0, 0, 0, [new UnitRawMeshSectionData(0, 0, triangles)], [], vertices);

		var scene = PreviewSceneBuilder.BuildScene([Unit("00000000000000a6", raw)]);

		Assert.Equal(Vector3.Zero, scene.Bounds.Center);
		Assert.Equal(MathF.Sqrt(3f), scene.Bounds.Radius, 4);
		Assert.Equal(new Vector3(-1, -1, -1), scene.Bounds.Min);
		Assert.Equal(new Vector3(1, 1, 1), scene.Bounds.Max);
		Assert.Equal(new Vector3(2, 2, 2), scene.Bounds.Size);
	}

	[Fact]
	public void BuildScene_EmptyGeometryGetsAPositiveRadius()
	{
		var scene = PreviewSceneBuilder.BuildScene([]);
		Assert.True(scene.IsEmpty);
		Assert.Equal(PreviewBoundingSphere.Degenerate.Center, scene.Bounds.Center);
		Assert.True(scene.Bounds.Radius > 0f);
	}

	[Fact]
	public void BuildScene_CollapsedGeometryStillGetsAPositiveRadius()
	{
		// 顶点全部重合：半径算出 0，必须被兜成一个正数，否则相机取景会除零。
		var raws = new[]
		{
			Mesh(0, 0,
				[(0, 0, 0), (0, 0, 0), (0, 0, 0), (0, 0, 0), (0, 0, 0), (0, 0, 0)],
				[(0, 1, 2, 0), (3, 4, 5, 0)]),
		};

		var scene = PreviewSceneBuilder.BuildScene([Unit("00000000000000a7", raws[0])]);

		Assert.True(scene.Bounds.Radius > 0f);
	}

	// ---- 多 unit / 统计 -----------------------------------------------------------

	[Fact]
	public void BuildScene_AggregatesStatsAcrossUnits()
	{
		var scene = PreviewSceneBuilder.BuildScene(
			[
				Unit("00000000000000b1", Quad(0, 0)),
				Unit("00000000000000b2", Quad(0, 0)),
			],
			notes: null,
			patchCount: 2,
			unitEntries: 5,
			unitsFailed: 1);

		Assert.Equal(2, scene.Meshes.Count);
		Assert.Equal(2, scene.Stats.UnitsRead);
		Assert.Equal(5, scene.Stats.UnitEntries);
		Assert.Equal(1, scene.Stats.UnitsFailed);
		Assert.Equal(12, scene.Stats.Vertices);
		Assert.Equal(4, scene.Stats.Triangles);
		Assert.Contains("units=2/5", scene.Describe());
	}

	// ---- 夹具 --------------------------------------------------------------------

	private static PreviewUnitSource Unit(string hex, UnitRawMeshData raw)
		=> new(hex, Model(raw));

	/// <summary>最小可渲染网格：一个方形面片，6 顶点 / 2 三角形（平铺在 z = 0 平面）。</summary>
	private static UnitRawMeshData Quad(int meshInfoIndex, int lod, uint materialSlot = 0)
		=> Mesh(meshInfoIndex, lod,
			[(0, 0, 0), (1, 0, 0), (0, 1, 0), (1, 1, 0), (0, 0, 1), (1, 0, 1)],
			[(0, 1, 2, materialSlot), (3, 4, 5, materialSlot)]);

	private static UnitMeshModel Model(params UnitRawMeshData[] raws)
		=> Model(raws.Select(raw => MeshInfo(raw.MeshInfoIndex, raw.LodIndex)).ToArray(), raws);

	private static UnitMeshModel Model(IReadOnlyList<UnitMeshInfo> infos, UnitRawMeshData[] raws)
		=> new(
			1, 0, 0, 0, 0, 0, 0, 1, 0, 0,
			UnitCustomizationInfo.Empty,
			[],
			[],
			infos,
			[],
			[],
			raws);

	private static UnitMeshInfo MeshInfo(int index, int lod, bool culling = false, bool isStatic = false)
		=> new(
			index, 0, 0, lod, 0, 0, 1, 0, 1, 0,
			new UnitMeshSemanticInfo(
				string.Empty, "torso", "pt0", "any", "light", lod, index, culling, isStatic, lod is not 0 and not -1),
			[0],
			[]);

	private static UnitRawMeshData Mesh(
		int meshInfoIndex,
		int lod,
		IReadOnlyList<(float X, float Y, float Z)> positions,
		IReadOnlyList<(int A, int B, int C, uint MaterialSlot)> triangles)
	{
		var vertices = positions
			.Select((position, index) => Vertex((uint)index, position.X, position.Y, position.Z))
			.ToList();
		var indices = triangles
			.Select(triangle => new UnitTriangleIndices((uint)triangle.A, (uint)triangle.B, (uint)triangle.C))
			.ToList();
		return new UnitRawMeshData(
			meshInfoIndex, 0, lod, 0,
			[new UnitRawMeshSectionData(0, triangles.Count == 0 ? 0u : triangles[0].MaterialSlot, indices)],
			[],
			vertices);
	}

	private static UnitRawVertexRecord Vertex(uint index, float x, float y, float z)
		=> new(
			index,
			[],
			[
				new UnitVertexComponentValue(0, "position", 0, "vec3_float", 0, [x, y, z], [], []),
				new UnitVertexComponentValue(1, "normal", 0, "unk_normal", 0, [0f, 0f, 1f], [], []),
			]);

	private static UnitRawVertexRecord UvOnly(uint index)
		=> new(index, [], [new UnitVertexComponentValue(4, "uv", 0, "vec2_float", 0, [0.5f, 0.5f], [], [])]);
}
