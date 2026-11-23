using System.Numerics;
using HD2ModAdaptation.Preview;

namespace HD2ModAdaptation.Tests;

/// <summary>预览测试的共享夹具：直接构造 <see cref="PreviewScene"/>（记录类型都是 public，不必过 unit 解码）。</summary>
internal static class PreviewTestScenes
{
	/// <summary>用一个网格集合造场景，包围球按顶点算。</summary>
	public static PreviewScene Scene(params PreviewMesh[] meshes)
	{
		var min = new Vector3(float.PositiveInfinity);
		var max = new Vector3(float.NegativeInfinity);
		foreach (var mesh in meshes)
		{
			foreach (var vertex in mesh.Vertices)
			{
				min = Vector3.Min(min, vertex.Position);
				max = Vector3.Max(max, vertex.Position);
			}
		}

		if (meshes.Length == 0) return new PreviewScene([], PreviewBoundingSphere.Degenerate, StatsOf(0, 0, 0));

		var center = (min + max) * 0.5f;
		var radius = 1f;
		foreach (var mesh in meshes)
		{
			foreach (var vertex in mesh.Vertices)
			{
				radius = MathF.Max(radius, Vector3.Distance(vertex.Position, center));
			}
		}

		var vertices = meshes.Sum(mesh => mesh.Vertices.Count);
		var triangles = meshes.Sum(mesh => mesh.Triangles.Count);
		return new PreviewScene(
			meshes,
			new PreviewBoundingSphere(center, radius) { Min = min, Max = max },
			StatsOf(1, vertices, triangles));
	}

	/// <summary>单位立方体（半边长 1），12 个三角形，6 个面同材质。</summary>
	public static PreviewScene Cube(uint materialSlot = 1)
	{
		var positions = new Vector3[8];
		var index = 0;
		foreach (var z in new[] { -1f, 1f })
		{
			foreach (var y in new[] { -1f, 1f })
			{
				foreach (var x in new[] { -1f, 1f })
				{
					positions[index++] = new Vector3(x, y, z);
				}
			}
		}

		// 顶点顺序：0(-1,-1,-1) 1(1,-1,-1) 2(-1,1,-1) 3(1,1,-1) 4(-1,-1,1) 5(1,-1,1) 6(-1,1,1) 7(1,1,1)
		var faces = new (int A, int B, int C)[]
		{
			(0, 2, 1), (1, 2, 3),   // -Z
			(4, 5, 6), (5, 7, 6),   // +Z
			(0, 1, 4), (1, 5, 4),   // -Y
			(2, 6, 3), (3, 6, 7),   // +Y
			(0, 4, 2), (2, 4, 6),   // -X
			(1, 3, 5), (3, 7, 5),   // +X
		};

		var vertices = positions.Select(position => new PreviewVertex(position, Vector3.Normalize(position), Vector2.Zero)).ToArray();
		var triangles = faces.Select(face => new PreviewTriangle(face.A, face.B, face.C, materialSlot)).ToArray();
		return Scene(new PreviewMesh("00000000000000c1", 0, 0, PreviewMeshKind.Visual, vertices, triangles));
	}

	/// <summary>一个朝 ±Y 的方形面片（半边长 <paramref name="half"/>，位于给定 y）。</summary>
	public static PreviewMesh Quad(float y, float half, uint materialSlot, bool reverseWinding)
	{
		var a = new Vector3(-half, y, -half);
		var b = new Vector3(half, y, -half);
		var c = new Vector3(half, y, half);
		var d = new Vector3(-half, y, half);
		var vertices = new[] { a, b, c, d }
			.Select(position => new PreviewVertex(position, new Vector3(0, 1, 0), Vector2.Zero))
			.ToArray();

		var triangles = reverseWinding
			? new[] { new PreviewTriangle(0, 1, 2, materialSlot), new PreviewTriangle(0, 2, 3, materialSlot) }
			: new[] { new PreviewTriangle(0, 2, 1, materialSlot), new PreviewTriangle(0, 3, 2, materialSlot) };
		return new PreviewMesh("00000000000000c2", 0, 0, PreviewMeshKind.Visual, vertices, triangles);
	}


	/// <summary>
	/// 各向不对称的场景：一个 2×1×1 的长方体 + 一个偏在右前上方的小方块 + 一个偏左后方的小方块。
	/// 立方体太对称（正/左/后三个视图像素完全一样），锁不住视角回归，所以 golden 用这个。
	/// </summary>
	public static PreviewScene Asymmetric()
	{
		var body = Box(new Vector3(-1f, -0.5f, -0.5f), new Vector3(1f, 0.5f, 0.5f), 1);
		var markerRight = Box(new Vector3(0.6f, 0.5f, 0.2f), new Vector3(1.2f, 1.1f, 0.8f), 2);
		var markerLeft = Box(new Vector3(-1.4f, -1.1f, -0.1f), new Vector3(-0.9f, -0.6f, 0.4f), 3);
		return Scene(body, markerRight, markerLeft);
	}

	/// <summary>轴对齐长方体（12 个三角形）。</summary>
	public static PreviewMesh Box(Vector3 min, Vector3 max, uint materialSlot)
	{
		var corners = new[]
		{
			new Vector3(min.X, min.Y, min.Z), new Vector3(max.X, min.Y, min.Z),
			new Vector3(min.X, max.Y, min.Z), new Vector3(max.X, max.Y, min.Z),
			new Vector3(min.X, min.Y, max.Z), new Vector3(max.X, min.Y, max.Z),
			new Vector3(min.X, max.Y, max.Z), new Vector3(max.X, max.Y, max.Z),
		};
		var faces = new (int A, int B, int C)[]
		{
			(0, 2, 1), (1, 2, 3),
			(4, 5, 6), (5, 7, 6),
			(0, 1, 4), (1, 5, 4),
			(2, 6, 3), (3, 6, 7),
			(0, 4, 2), (2, 4, 6),
			(1, 3, 5), (3, 7, 5),
		};
		var vertices = corners.Select(corner => new PreviewVertex(corner, Vector3.UnitZ, Vector2.Zero)).ToArray();
		var triangles = faces.Select(face => new PreviewTriangle(face.A, face.B, face.C, materialSlot)).ToArray();
		return new PreviewMesh("00000000000000c3", 0, 0, PreviewMeshKind.Visual, vertices, triangles);
	}
	private static PreviewSceneStats StatsOf(int patches, int vertices, int triangles)
		=> new(patches, 1, 1, 0, 1, 1, vertices, triangles, 0, 0, 0, 0, 0, 0);
}
