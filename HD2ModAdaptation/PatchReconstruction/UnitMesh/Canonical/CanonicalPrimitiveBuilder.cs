using System.Numerics;

namespace HD2ModAdaptation.PatchReconstruction.UnitMesh.Canonical;

// Purpose: Builds primitive geometry (triangle/quad/cube/sphere) encoded against a TARGET unit's
// vertex stream ABI, so that CanonicalMeshPreparation.TryPrepare can encode it afterwards.
//
// 关键：分量表不能自己造。目标 unit 的流可能只有 6 个分量（例如隐藏模板：
// position / normal / uv0 / uv1 / bone_weight / bone_index），顺序、格式、语义都由
// UnitStreamInfo.Components 决定。这里按它逐项填值，与 CanonicalPlaceholderMinifier 同一套做法：
//   0 position     -> 顶点位置
//   1 normal       -> 面法线
//   2 tangent      -> 切线
//   3 bitangent    -> 副切线
//   4 uv           -> uv 集（component.Index 区分 uv0 / uv1）
//   5 color        -> 白
//   6 bone_index   -> 全 0（骨骼槽 0）
//   7 bone_weight  -> [1,0,0,0]（100% 权重给骨骼槽 0）
public enum CanonicalPrimitiveShape
{
	Triangle,
	Quad,
	Cube,
	Sphere
}

/// <summary>四边形/三角形所在平面：Xy = 法线朝 +Z；Yz = 法线朝 +X（Blender 正视图那个平面）。</summary>
public enum CanonicalPrimitivePlane
{
	Xy,
	Yz,
	Xz
}

public sealed record CanonicalPrimitiveSpec(
	CanonicalPrimitiveShape Shape,
	float Width = 0.15f,
	float Height = 0.15f,
	float Depth = 0f,
	Vector3 Center = default,
	int Segments = 12,
	uint MaterialSlotId = 0,
	uint MaterialIndex = 0,
	bool FlipFacing = false,
	CanonicalPrimitivePlane Plane = CanonicalPrimitivePlane.Xy,
	bool NoSkin = false,
	ulong? MaterialId = null,
	// 引擎贴图的 V 轴自上而下（VTF 约定），而纹理坐标自下而上，所以默认翻一次 v。
	// 依据：sim.py「VTF 的 V 轴朝下，纹理坐标朝上 → 采样时翻 v」、MatCapSampler.cs:103。
	bool FlipV = true,
	// 背景板：>0 时在原板后面再放一块同形状、放大的板，单独一个 section（MaterialIndex=1），
	// 用来给半透明材质提供已知背景。语义：倍数（1.25 = 大 25%），0 = 不加。
	float BackdropScale = 0f,
	// 后方板沿法线方向的偏移（米），正值向法线负方向退开
	float BackdropOffset = 0.02f,
	// 背景板横向错开（米，沿 Y）：>0 时两块板并排，不重叠，便于分别观察/测量
	float BackdropOffsetY = 0f);

public sealed record CanonicalPrimitiveBuildResult(
	UnitRawMeshData? Mesh,
	IReadOnlyList<CanonicalPlanDiagnostic> Diagnostics)
{
	public bool IsValid => Mesh is not null && Diagnostics.Count == 0;
}

public sealed class CanonicalPrimitiveBuilder
{
	public CanonicalPrimitiveBuildResult TryBuild(
		CanonicalPrimitiveSpec spec,
		UnitStreamInfo stream,
		int meshInfoIndex = 0)
	{
		ArgumentNullException.ThrowIfNull(spec);
		ArgumentNullException.ThrowIfNull(stream);
		if (spec.Width <= 0 || spec.Height <= 0)
			return new(null, [new("PrimitiveSizeInvalid", $"原语尺寸必须为正：{spec.Width} x {spec.Height}。")]);
		if (spec.Shape == CanonicalPrimitiveShape.Sphere && spec.Segments < 3)
			return new(null, [new("PrimitiveSegmentsInvalid", $"球体分段至少 3，收到 {spec.Segments}。")]);
		if (stream.VertexStride == 0 || stream.Components.Count == 0)
			return new(null, [new("PrimitiveStreamInvalid", "目标流没有可编码的分量表。")]);

		var (positions, normals, uvs, indices) = spec.Shape switch
		{
			CanonicalPrimitiveShape.Triangle => BuildTriangle(spec),
			CanonicalPrimitiveShape.Quad => BuildQuad(spec),
			CanonicalPrimitiveShape.Cube => BuildCube(spec),
			CanonicalPrimitiveShape.Sphere => BuildSphere(spec),
			_ => throw new ArgumentOutOfRangeException(nameof(spec))
		};

		var vertices = new List<UnitRawVertexRecord>(positions.Length);
		for (var i = 0; i < positions.Length; i++)
		{
			var components = new List<UnitVertexComponentValue>(stream.Components.Count);
			foreach (var component in stream.Components)
				components.Add(Encode(component, positions[i], normals[i], uvs[i], spec.NoSkin, spec.FlipV));
			vertices.Add(new UnitRawVertexRecord((uint)i, Array.Empty<byte>(), components));
		}

		var triangles = indices.Select(t => new UnitTriangleIndices((uint)t.A, (uint)t.B, (uint)t.C)).ToArray();
		var sections = new List<UnitRawMeshSectionData>
		{
			new(spec.MaterialIndex, spec.MaterialSlotId, triangles)
		};

		if (spec.BackdropScale > 0f)
		{
			// 背景板：同形状放大 BackdropScale 倍，沿法线反方向退开 BackdropOffset，
			// 顶点追加到同一 stream，三角形单独成一个 section（MaterialIndex=1）。
			var back = spec with
			{
				Width = spec.Width * spec.BackdropScale,
				Height = spec.Height * spec.BackdropScale,
				// 沿所在平面的法线反方向退开：Yz→-X，Xz→-Y，Xy→-Z
				Center = (spec.Plane switch
					{
						CanonicalPrimitivePlane.Yz => spec.Center - new Vector3(spec.BackdropOffset, 0f, 0f),
						CanonicalPrimitivePlane.Xz => spec.Center - new Vector3(0f, spec.BackdropOffset, 0f),
						_ => spec.Center - new Vector3(0f, 0f, spec.BackdropOffset)
					})
					+ new Vector3(0f, spec.BackdropOffsetY, 0f),
				MaterialIndex = spec.MaterialIndex + 1
			};
			var (bp, bn, bu, bi) = spec.Shape switch
			{
				CanonicalPrimitiveShape.Triangle => BuildTriangle(back),
				CanonicalPrimitiveShape.Quad => BuildQuad(back),
				CanonicalPrimitiveShape.Cube => BuildCube(back),
				CanonicalPrimitiveShape.Sphere => BuildSphere(back),
				_ => throw new ArgumentOutOfRangeException(nameof(spec))
			};
			var baseIndex = vertices.Count;
			for (var i = 0; i < bp.Length; i++)
			{
				var comps = new List<UnitVertexComponentValue>(stream.Components.Count);
				foreach (var component in stream.Components)
					comps.Add(Encode(component, bp[i], bn[i], bu[i], spec.NoSkin, spec.FlipV));
				vertices.Add(new UnitRawVertexRecord((uint)(baseIndex + i), Array.Empty<byte>(), comps));
			}
			var backTris = bi.Select(t => new UnitTriangleIndices((uint)(baseIndex + t.A), (uint)(baseIndex + t.B), (uint)(baseIndex + t.C))).ToArray();
			sections.Add(new UnitRawMeshSectionData(spec.MaterialIndex + 1, spec.MaterialSlotId + 1, backTris));
			triangles = triangles.Concat(backTris).ToArray();
		}
		var mesh = new UnitRawMeshData(
			MeshInfoIndex: meshInfoIndex,
			MeshId: 0,
			LodIndex: 0,
			StreamIndex: 0,
			Sections: sections.ToArray(),
			Triangles: triangles,
			Vertices: vertices);
		return new(mesh, Array.Empty<CanonicalPlanDiagnostic>());
	}

	/// <summary>按目标分量的类型与格式填值；格式名沿用目标，编码交给 CanonicalMeshPreparation。</summary>
	private static UnitVertexComponentValue Encode(
		UnitStreamComponentInfo component, Vector3 position, Vector3 normal, Vector2 uv, bool noSkin, bool flipV)
	{
		var floatValues = component.Type switch
		{
			0 => component.FormatName == "float" ? [position.X] : [position.X, position.Y, position.Z, 1f],
			1 => [normal.X, normal.Y, normal.Z, 0f],
			2 => [1f, 0f, 0f, 0f],
			3 => [0f, 1f, 0f, 0f],
			4 => component.Index == 0 ? [uv.X, flipV ? 1f - uv.Y : uv.Y, 0f, 0f] : [0f, 0f, 0f, 0f],
			5 => [1f, 1f, 1f, 1f],
			7 => noSkin ? [0f, 0f, 0f, 0f] : [1f, 0f, 0f, 0f],
			_ => Array.Empty<float>()
		};
		var uintValues = component.Type == 6 ? [0u, 0u, 0u, 0u] : Array.Empty<uint>();
		return new(component.Type, component.TypeName, component.Format, component.FormatName,
			component.Index, floatValues, uintValues, Array.Empty<byte>());
	}

	// ---- 形状（局部空间，+Z 朝外）----

	private static (Vector3[], Vector3[], Vector2[], (int A, int B, int C)[]) BuildTriangle(CanonicalPrimitiveSpec spec)
	{
		var w = spec.Width * 0.5f;
		var h = spec.Height * 0.5f;
		var c = spec.Center;
		var positions = new[]
		{
			new Vector3(c.X - w, c.Y - h, c.Z),
			new Vector3(c.X + w, c.Y - h, c.Z),
			new Vector3(c.X, c.Y + h, c.Z)
		};
		var normals = new[] { UnitZ, UnitZ, UnitZ };
		var uvs = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 1f) };
		var tris = spec.FlipFacing ? new[] { (0, 2, 1) } : new[] { (0, 1, 2) };
		return (positions, normals, uvs, tris);
	}

	private static (Vector3[], Vector3[], Vector2[], (int A, int B, int C)[]) BuildQuad(CanonicalPrimitiveSpec spec)
	{
		var w = spec.Width * 0.5f;
		var h = spec.Height * 0.5f;
		var c = spec.Center;
		Vector3[] positions;
		Vector3[] normals;
		if (spec.Plane == CanonicalPrimitivePlane.Xz)
		{
			// XZ 平面：宽沿 X，高沿 Z，法线朝 +Y（正对镜头）
			positions = new[]
			{
				new Vector3(c.X - w, c.Y, c.Z - h),
				new Vector3(c.X + w, c.Y, c.Z - h),
				new Vector3(c.X + w, c.Y, c.Z + h),
				new Vector3(c.X - w, c.Y, c.Z + h)
			};
			normals = new[] { UnitY, UnitY, UnitY, UnitY };
		}
		else if (spec.Plane == CanonicalPrimitivePlane.Yz)
		{
			// X=0 平面：宽沿 Y，高沿 Z，法线朝 +X（Blender 正视图那个平面）
			positions = new[]
			{
				new Vector3(c.X, c.Y - w, c.Z - h),
				new Vector3(c.X, c.Y + w, c.Z - h),
				new Vector3(c.X, c.Y + w, c.Z + h),
				new Vector3(c.X, c.Y - w, c.Z + h)
			};
			normals = new[] { UnitX, UnitX, UnitX, UnitX };
		}
		else
		{
			positions = new[]
			{
				new Vector3(c.X - w, c.Y - h, c.Z),
				new Vector3(c.X + w, c.Y - h, c.Z),
				new Vector3(c.X + w, c.Y + h, c.Z),
				new Vector3(c.X - w, c.Y + h, c.Z)
			};
			normals = new[] { UnitZ, UnitZ, UnitZ, UnitZ };
		}
		var uvs = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f) };
		var tris = spec.FlipFacing
			? new[] { (0, 2, 1), (0, 3, 2) }
			: new[] { (0, 1, 2), (0, 2, 3) };
		return (positions, normals, uvs, tris);
	}

	private static (Vector3[], Vector3[], Vector2[], (int A, int B, int C)[]) BuildCube(CanonicalPrimitiveSpec spec)
	{
		var w = spec.Width * 0.5f;
		var h = spec.Height * 0.5f;
		var d = (spec.Depth > 0 ? spec.Depth : spec.Width) * 0.5f;
		var c = spec.Center;
		var p = new List<Vector3>(24);
		var n = new List<Vector3>(24);
		var u = new List<Vector2>(24);
		var t = new List<(int, int, int)>(12);

		void Face(Vector3 origin, Vector3 du, Vector3 dv, Vector3 normal)
		{
			var b = p.Count;
			p.Add(origin);
			p.Add(origin + du);
			p.Add(origin + du + dv);
			p.Add(origin + dv);
			for (var i = 0; i < 4; i++) n.Add(normal);
			u.Add(new Vector2(0f, 0f));
			u.Add(new Vector2(1f, 0f));
			u.Add(new Vector2(1f, 1f));
			u.Add(new Vector2(0f, 1f));
			if (spec.FlipFacing)
			{
				t.Add((b, b + 2, b + 1));
				t.Add((b, b + 3, b + 2));
			}
			else
			{
				t.Add((b, b + 1, b + 2));
				t.Add((b, b + 2, b + 3));
			}
		}

		var x = new Vector3(2 * w, 0, 0);
		var y = new Vector3(0, 2 * h, 0);
		var z = new Vector3(0, 0, 2 * d);
		var o = new Vector3(c.X - w, c.Y - h, c.Z - d);
		Face(o + z, x, y, UnitZ);
		Face(o + x, -z, y, UnitX);
		Face(o + x + z, -x, y, -UnitZ);
		Face(o, z, y, -UnitX);
		Face(o + y, x, -z, UnitY);
		Face(o, x, z, -UnitY);
		return (p.ToArray(), n.ToArray(), u.ToArray(), t.ToArray());
	}

	private static (Vector3[], Vector3[], Vector2[], (int A, int B, int C)[]) BuildSphere(CanonicalPrimitiveSpec spec)
	{
		var r = spec.Width * 0.5f;
		var seg = Math.Max(3, spec.Segments);
		var ring = Math.Max(2, seg / 2);
		var c = spec.Center;
		var p = new List<Vector3>();
		var n = new List<Vector3>();
		var u = new List<Vector2>();
		var t = new List<(int, int, int)>();

		for (var i = 0; i <= ring; i++)
		{
			var v = (float)i / ring;
			var phi = v * MathF.PI;
			for (var j = 0; j <= seg; j++)
			{
				var f = (float)j / seg;
				var theta = f * MathF.Tau;
				var normal = new Vector3(
					MathF.Sin(phi) * MathF.Cos(theta),
					MathF.Cos(phi),
					MathF.Sin(phi) * MathF.Sin(theta));
				p.Add(c + normal * r);
				n.Add(normal);
				u.Add(new Vector2(f, v));
			}
		}

		var stride = seg + 1;
		for (var i = 0; i < ring; i++)
		{
			for (var j = 0; j < seg; j++)
			{
				var a = i * stride + j;
				var b = a + stride;
				if (spec.FlipFacing)
				{
					t.Add((a, a + 1, b));
					t.Add((a + 1, b + 1, b));
				}
				else
				{
					t.Add((a, b, a + 1));
					t.Add((a + 1, b, b + 1));
				}
			}
		}

		return (p.ToArray(), n.ToArray(), u.ToArray(), t.ToArray());
	}

	private static readonly Vector3 UnitX = new(1f, 0f, 0f);
	private static readonly Vector3 UnitY = new(0f, 1f, 0f);
	private static readonly Vector3 UnitZ = new(0f, 0f, 1f);
}
