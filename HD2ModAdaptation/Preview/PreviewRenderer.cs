using System.Numerics;

namespace HD2ModAdaptation.Preview;

/// <summary>
/// 软件光栅器（规划 §11）。纯 C#、无 GPU、无外部进程，所以它能在 Core / CLI / 管理器里
/// 跑出**逐字节一致**的结果，也能在 CI 里做 golden 测试。
/// <para>
/// 刻意保持朴素的几处，都是有理由的：
/// </para>
/// <list type="bullet">
/// <item><b>不做背面剔除（默认）</b>：缠绕方向反了的网格整块消失，比多画一层更难查。</item>
/// <item><b>法线用屏幕空间重心 + 1/z 做透视校正</b>：直接在屏幕空间插值法线会在近处变形。</item>
/// <item><b>只做半球环境光 + 一盏主光 + 边缘光</b>：这不是材质预览（v1 不贴图），
/// 目的是把形体、比例、穿插看清楚，而不是还原 PBR。</item>
/// <item><b>串行、无随机、无时间</b>：同样的输入永远同样的字节。</item>
/// </list>
/// </summary>
public static class PreviewRenderer
{
	/// <summary>
	/// 主光在**相机空间**里的方向（x 右 / y 上 / z 前）：从左上前方打过来。
	/// 刻意不写死世界方向 —— 世界固定的光会让转台一半视角背光（2026-11-23 实测：正视图整个发暗）。
	/// </summary>
	private static readonly Vector3 KeyLightCamera = Vector3.Normalize(new Vector3(-0.42f, 0.55f, -0.72f));

	/// <summary>不按材质分色时的黏土色。</summary>
	private static readonly Vector3 ClayColor = new(0.72f, 0.72f, 0.70f);

	/// <summary>渲染一张图。相机必须先量化（<see cref="PreviewCamera.Quantize"/>），这里会再做一次以兜住调用方。</summary>
	public static PreviewImage Render(PreviewScene scene, PreviewCamera camera, PreviewRenderOptions options)
	{
		ArgumentNullException.ThrowIfNull(scene);
		ArgumentNullException.ThrowIfNull(camera);
		ArgumentNullException.ThrowIfNull(options);
		options.Validate();

		var quantized = camera.Quantize();
		if (!quantized.IsRenderable)
			throw new ArgumentException("相机规格不可渲染（zoom 必须为正、pivot 必须是有限数）。", nameof(camera));

		var (internalWidth, internalHeight) = options.InternalSize;
		var setup = quantized.Resolve(scene.Bounds, (float)internalWidth / internalHeight);
		var pixels = new byte[internalWidth * internalHeight * 4];
		var depth = new float[internalWidth * internalHeight];
		FillBackground(pixels, options.Background);

		foreach (var mesh in scene.Meshes)
		{
			if (mesh.Vertices.Count < 3 || mesh.Triangles.Count == 0) continue;

			var cameraSpace = ToCameraSpace(setup, mesh.Vertices);
			var cullBackFaces = options.CullBackFaces;
			foreach (var triangle in mesh.Triangles)
			{
				RasterizeTriangle(
					pixels, depth, internalWidth, internalHeight, setup, cameraSpace,
					triangle, options, cullBackFaces);
			}
		}

		var image = new PreviewImage(internalWidth, internalHeight, pixels);
		return image.DownsampleBox(options.Supersample);
	}

	/// <summary>把一张网格的顶点变换到相机空间（每个网格只做一次）。</summary>
	private static CameraVertex[] ToCameraSpace(PreviewCameraSetup setup, IReadOnlyList<PreviewVertex> vertices)
	{
		var result = new CameraVertex[vertices.Count];
		for (var index = 0; index < vertices.Count; index++)
		{
			var vertex = vertices[index];
			var camera = setup.ToCamera(vertex.Position);
			var normal = vertex.Normal;
			if (normal.LengthSquared() < 1e-12f) normal = Vector3.UnitZ;
			result[index] = new CameraVertex(camera, Vector3.Normalize(normal), vertex.Position);
		}
		return result;
	}

	private readonly record struct CameraVertex(Vector3 Camera, Vector3 Normal, Vector3 World);

	private readonly record struct ClipVertex(Vector3 Camera, Vector3 Normal);

	/// <summary>
	/// 画一个三角形：近平面裁剪 → 投影 → 光栅化。
	/// 裁剪是必须的：不裁的话，跨过相机的三角形会被投到屏幕外，表现为一大片错误的覆盖。
	/// </summary>
	private static void RasterizeTriangle(
		byte[] pixels,
		float[] depth,
		int width,
		int height,
		PreviewCameraSetup setup,
		CameraVertex[] vertices,
		PreviewTriangle triangle,
		PreviewRenderOptions options,
		bool cullBackFaces)
	{
		var v0 = vertices[triangle.A];
		var v1 = vertices[triangle.B];
		var v2 = vertices[triangle.C];

		// 近平面裁剪：全部在后就整体丢弃，全部在前就原样用，跨越时才切多边形。
		var inside0 = v0.Camera.Z > setup.Near;
		var inside1 = v1.Camera.Z > setup.Near;
		var inside2 = v2.Camera.Z > setup.Near;
		if (!inside0 && !inside1 && !inside2) return;

		var color = options.TintByMaterial ? MaterialColor(triangle.MaterialSlotId) : ClayColor;

		if (inside0 && inside1 && inside2)
		{
			DrawScreenTriangle(pixels, depth, width, height, setup, color, cullBackFaces,
				new ClipVertex(v0.Camera, v0.Normal),
				new ClipVertex(v1.Camera, v1.Normal),
				new ClipVertex(v2.Camera, v2.Normal));
			return;
		}

		var polygon = ClipAgainstNear(
			[
				new ClipVertex(v0.Camera, v0.Normal),
				new ClipVertex(v1.Camera, v1.Normal),
				new ClipVertex(v2.Camera, v2.Normal),
			],
			setup.Near);

		for (var index = 1; index + 1 < polygon.Count; index++)
		{
			DrawScreenTriangle(pixels, depth, width, height, setup, color, cullBackFaces,
				polygon[0], polygon[index], polygon[index + 1]);
		}
	}

	/// <summary>Sutherland–Hodgman：只对 z &gt; near 这一个平面裁剪。</summary>
	private static List<ClipVertex> ClipAgainstNear(IReadOnlyList<ClipVertex> input, float near)
	{
		var output = new List<ClipVertex>(4);
		var count = input.Count;
		for (var index = 0; index < count; index++)
		{
			var current = input[index];
			var next = input[(index + 1) % count];
			var currentInside = current.Camera.Z > near;
			var nextInside = next.Camera.Z > near;

			if (currentInside) output.Add(current);

			if (currentInside != nextInside)
			{
				var denominator = next.Camera.Z - current.Camera.Z;
				if (MathF.Abs(denominator) < 1e-9f) continue;
				var t = (near - current.Camera.Z) / denominator;
				output.Add(new ClipVertex(
					Vector3.Lerp(current.Camera, next.Camera, t),
					Vector3.Lerp(current.Normal, next.Normal, t)));
			}
		}
		return output;
	}

	private static void DrawScreenTriangle(
		byte[] pixels,
		float[] depth,
		int width,
		int height,
		PreviewCameraSetup setup,
		Vector3 color,
		bool cullBackFaces,
		ClipVertex a,
		ClipVertex b,
		ClipVertex c)
	{
		var projectedA = Project(setup, a.Camera, width, height);
		var projectedB = Project(setup, b.Camera, width, height);
		var projectedC = Project(setup, c.Camera, width, height);

		var area = Edge(projectedA.Position, projectedB.Position, projectedC.Position);
		if (area == 0f || float.IsNaN(area)) return;

		if (area < 0f)
		{
			if (cullBackFaces) return;
			// 缠绕反了：交换两个顶点让面积为正，法线跟着换。
			(projectedB, projectedC) = (projectedC, projectedB);
			(b, c) = (c, b);
			area = -area;
		}

		var minX = Math.Max(0, (int)MathF.Floor(MathF.Min(projectedA.Position.X, MathF.Min(projectedB.Position.X, projectedC.Position.X))));
		var maxX = Math.Min(width - 1, (int)MathF.Ceiling(MathF.Max(projectedA.Position.X, MathF.Max(projectedB.Position.X, projectedC.Position.X))));
		var minY = Math.Max(0, (int)MathF.Floor(MathF.Min(projectedA.Position.Y, MathF.Min(projectedB.Position.Y, projectedC.Position.Y))));
		var maxY = Math.Min(height - 1, (int)MathF.Ceiling(MathF.Max(projectedA.Position.Y, MathF.Max(projectedB.Position.Y, projectedC.Position.Y))));
		if (minX > maxX || minY > maxY) return;

		var viewForward = setup.Forward;
		var keyLight = Vector3.Normalize(
			setup.Right * KeyLightCamera.X + setup.Up * KeyLightCamera.Y + viewForward * KeyLightCamera.Z);
		for (var y = minY; y <= maxY; y++)
		{
			var py = y + 0.5f;
			var row = y * width;
			for (var x = minX; x <= maxX; x++)
			{
				var px = x + 0.5f;
				var w0 = Edge(projectedB.Position, projectedC.Position, new Vector2(px, py));
				if (w0 < 0f) continue;
				var w1 = Edge(projectedC.Position, projectedA.Position, new Vector2(px, py));
				if (w1 < 0f) continue;
				var w2 = Edge(projectedA.Position, projectedB.Position, new Vector2(px, py));
				if (w2 < 0f) continue;

				w0 /= area;
				w1 /= area;
				w2 /= area;

				// 1/z 在屏幕空间是线性的，用它做深度测试和透视校正。
				var invZ = w0 * projectedA.InverseZ + w1 * projectedB.InverseZ + w2 * projectedC.InverseZ;
				if (invZ <= 0f || float.IsNaN(invZ)) continue;

				var pixelIndex = row + x;
				if (invZ <= depth[pixelIndex]) continue;

				var n0 = a.Normal * (w0 * projectedA.InverseZ);
				var n1 = b.Normal * (w1 * projectedB.InverseZ);
				var n2 = c.Normal * (w2 * projectedC.InverseZ);
				var normal = n0 + n1 + n2;
				var normalLength = normal.Length();
				var unitNormal = normalLength > 1e-6f ? normal / normalLength : Vector3.UnitZ;
				// 双面渲染：法线一律朝相机，否则背面会是纯黑。
				if (Vector3.Dot(unitNormal, viewForward) > 0f) unitNormal = -unitNormal;

				var shaded = Shade(color, unitNormal, viewForward, keyLight);

				var offset = pixelIndex * 4;
				pixels[offset] = ToByte(shaded.X);
				pixels[offset + 1] = ToByte(shaded.Y);
				pixels[offset + 2] = ToByte(shaded.Z);
				pixels[offset + 3] = 255;
				depth[pixelIndex] = invZ;
			}
		}
	}

		private readonly record struct Projected(Vector2 Position, float InverseZ);

	private static Projected Project(PreviewCameraSetup setup, Vector3 camera, int width, int height)
	{
		var inverseZ = 1f / MathF.Max(camera.Z, 1e-6f);
		var ndcX = camera.X * inverseZ / (setup.TanHalfFov * setup.AspectRatio);
		var ndcY = camera.Y * inverseZ / setup.TanHalfFov;
		return new Projected(
			new Vector2((ndcX * 0.5f + 0.5f) * width, (0.5f - ndcY * 0.5f) * height),
			inverseZ);
	}

	private static float Edge(Vector2 a, Vector2 b, Vector2 point)
		=> (b.X - a.X) * (point.Y - a.Y) - (b.Y - a.Y) * (point.X - a.X);

	private static Vector3 Shade(Vector3 albedo, Vector3 normal, Vector3 viewForward, Vector3 keyLight)
	{
		// 半球环境光：朝上的面更亮，朝下的面更暗（Z 向上）。
		var ambient = 0.34f + 0.30f * MathF.Max(normal.Z, 0f);
		var diffuse = MathF.Max(Vector3.Dot(normal, keyLight), 0f);
		var rim = MathF.Pow(1f - MathF.Abs(Vector3.Dot(normal, viewForward)), 3f) * 0.16f;
		var lit = ambient + 0.78f * diffuse;
		return albedo * lit + new Vector3(rim, rim, rim);
	}

	/// <summary>材质槽 → 低饱和色相。同一个材质槽永远同一颜色，跨机器一致。</summary>
	public static Vector3 MaterialColor(uint materialSlotId)
	{
		// 槽号实测在 1e8～4e9 量级：`slot * 0.618f` 这种写法在 float 里连小数位都留不下，
		// 19 个不同的槽会塌成同一个色相（2026-11-23 实际踩到）。所以先在整数域散列。
		var scrambled = materialSlotId * 2654435761u;
		var hue = ((scrambled >> 8) & 0xFFFF) / 65536f;
		return HslToRgb(hue, 0.32f, 0.66f);
	}

	private static Vector3 HslToRgb(float hue, float saturation, float lightness)
	{
		if (saturation <= 0f) return new Vector3(lightness, lightness, lightness);
		var q = lightness < 0.5f ? lightness * (1f + saturation) : lightness + saturation - lightness * saturation;
		var p = 2f * lightness - q;
		return new Vector3(Channel(p, q, hue + 1f / 3f), Channel(p, q, hue), Channel(p, q, hue - 1f / 3f));
	}

	private static float Channel(float p, float q, float t)
	{
		if (t < 0f) t += 1f;
		if (t > 1f) t -= 1f;
		if (t < 1f / 6f) return p + (q - p) * 6f * t;
		if (t < 1f / 2f) return q;
		if (t < 2f / 3f) return p + (q - p) * (2f / 3f - t) * 6f;
		return p;
	}

	private static void FillBackground(byte[] pixels, PreviewBackground background)
	{
		if (background == PreviewBackground.Transparent) return;
		var value = background switch
		{
			PreviewBackground.Gray => (byte)128,
			PreviewBackground.Black => (byte)0,
			_ => (byte)255,
		};
		for (var offset = 0; offset < pixels.Length; offset += 4)
		{
			pixels[offset] = value;
			pixels[offset + 1] = value;
			pixels[offset + 2] = value;
			pixels[offset + 3] = 255;
		}
	}

	private static byte ToByte(float value)
		=> value <= 0f ? (byte)0 : value >= 1f ? (byte)255 : (byte)MathF.Round(value * 255f, MidpointRounding.AwayFromZero);
}
