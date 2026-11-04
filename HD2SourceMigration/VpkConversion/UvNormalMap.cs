using HD2SourceModel;

namespace HD2SourceMigration.VpkConversion;

/// <summary>
/// 把一个材质的几何**烘到它自己的 UV 空间**里，得到"逐纹素的世界（模型）空间法线"。
/// <para>
/// <b>为什么需要它。</b>源侧有两样东西是<b>按世界方向</b>算的，而法线贴图给的是**切线空间**
/// 的方向（"相对这个面"）：
/// </para>
/// <list type="bullet">
/// <item>matcap：<c>uv = 法线投到相机坐标系的 XY</c>（lilToon <c>lilCalcMatCapUV</c>）；</item>
/// <item>lightwarp：<c>U = N·L</c>，L 是世界光方向。</item>
/// </list>
/// <para>
/// 直接拿切线空间法线当世界方向用，得到的就是"法线贴图里的细节一抖、颜色/阴影就跳"——
/// 实机表现为脸上、发梢、衣褶上的一块块色斑。把几何烘进 UV 空间之后，逐纹素拿到的是
/// **随曲面朝向平滑变化**的法线，这两个功能才谈得上"翻译"。
/// </para>
/// <para>
/// <b>为什么通用。</b>它只依赖"顶点有位置/法线/UV、三角形有材质下标"，与具体哪个 Mod 无关；
/// 任何模型（包括以后新出的）都会走同一条路。烘焙失败（没有三角形、UV 退化）时返回
/// <c>null</c>，调用方退回"取贴图正中"，绝不因为烘不出来就让材质失败。
/// </para>
/// </summary>
public sealed record UvNormalMap(byte[] Rgba, int Width, int Height)
{
	/// <summary>按 UV（0..1）取模型空间法线（已归一化）。越界按边缘夹取。</summary>
	public (double X, double Y, double Z) Sample(double u, double v)
	{
		var width = Width;
		var height = Height;
		var x = Math.Clamp(u, 0, 1) * (width - 1);
		var y = Math.Clamp(v, 0, 1) * (height - 1);
		var at = (((int)Math.Round(y) * width) + (int)Math.Round(x)) * 4;
		var nx = (Rgba[at] / 255.0 * 2) - 1;
		var ny = (Rgba[at + 1] / 255.0 * 2) - 1;
		var nz = (Rgba[at + 2] / 255.0 * 2) - 1;
		var length = Math.Sqrt((nx * nx) + (ny * ny) + (nz * nz));
		if (length < 1e-6)
			return (0, 0, 1);
		return (nx / length, ny / length, nz / length);
	}
}

public static class UvNormalBake
{
	/// <summary>
	/// 把一个材质用到的三角形栅格化进 <paramref name="resolution"/>² 的 UV 空间。
	/// 没有可用三角形时返回 <c>null</c>。
	/// </summary>
	public static UvNormalMap? Bake(SourceGeometry geometry, int materialIndex, int resolution = 512)
	{
		ArgumentNullException.ThrowIfNull(geometry);
		if (resolution <= 0)
			throw new ArgumentOutOfRangeException(nameof(resolution));

		var pixels = new byte[resolution * resolution * 4];
		var covered = false;

		foreach (var triangle in geometry.Triangles)
		{
			if (triangle.MaterialIndex != materialIndex)
				continue;
			if ((uint)triangle.A >= (uint)geometry.Vertices.Count
				|| (uint)triangle.B >= (uint)geometry.Vertices.Count
				|| (uint)triangle.C >= (uint)geometry.Vertices.Count)
			{
				continue;
			}

			var a = geometry.Vertices[triangle.A];
			var b = geometry.Vertices[triangle.B];
			var c = geometry.Vertices[triangle.C];

			// UV → 纹素坐标（V 与贴图行同向：0 在上）。
			double ax = a.U * (resolution - 1), ay = a.V * (resolution - 1);
			double bx = b.U * (resolution - 1), by = b.V * (resolution - 1);
			double cx = c.U * (resolution - 1), cy = c.V * (resolution - 1);

			// 面积（顺带判退化：UV 上共线或重合的三角形没有可填的像素）
			var area = ((bx - ax) * (cy - ay)) - ((by - ay) * (cx - ax));
			if (Math.Abs(area) < 1e-9)
				continue;

			var minX = (int)Math.Floor(Math.Min(ax, Math.Min(bx, cx)));
			var maxX = (int)Math.Ceiling(Math.Max(ax, Math.Max(bx, cx)));
			var minY = (int)Math.Floor(Math.Min(ay, Math.Min(by, cy)));
			var maxY = (int)Math.Ceiling(Math.Max(ay, Math.Max(by, cy)));
			minX = Math.Max(0, minX);
			minY = Math.Max(0, minY);
			maxX = Math.Min(resolution - 1, maxX);
			maxY = Math.Min(resolution - 1, maxY);

			for (var y = minY; y <= maxY; y++)
			{
				for (var x = minX; x <= maxX; x++)
				{
					var px = x + 0.5;
					var py = y + 0.5;
					// 重心坐标（面积法）
					var w0 = (((bx - px) * (cy - py)) - ((by - py) * (cx - px))) / area;
					var w1 = (((cx - px) * (ay - py)) - ((cy - py) * (ax - px))) / area;
					var w2 = 1 - w0 - w1;
					if (w0 < -1e-6 || w1 < -1e-6 || w2 < -1e-6)
						continue;

					var nx = (w0 * a.Normal.X) + (w1 * b.Normal.X) + (w2 * c.Normal.X);
					var ny = (w0 * a.Normal.Y) + (w1 * b.Normal.Y) + (w2 * c.Normal.Y);
					var nz = (w0 * a.Normal.Z) + (w1 * b.Normal.Z) + (w2 * c.Normal.Z);
					var length = Math.Sqrt((nx * nx) + (ny * ny) + (nz * nz));
					if (length < 1e-6)
						continue;
					nx /= length;
					ny /= length;
					nz /= length;

					var at = ((y * resolution) + x) * 4;
					pixels[at] = Encode(nx);
					pixels[at + 1] = Encode(ny);
					pixels[at + 2] = Encode(nz);
					pixels[at + 3] = 255;
					covered = true;
				}
			}
		}

		return covered ? new UvNormalMap(pixels, resolution, resolution) : null;
	}

	/// <summary>把一个材质的 UV 法线按材质裸名索引出来（供转换器取用）。</summary>
	public static IReadOnlyDictionary<string, UvNormalMap> BakeAll(
		SourceGeometry geometry,
		IReadOnlyList<string> materialNames,
		int resolution = 512)
	{
		ArgumentNullException.ThrowIfNull(geometry);
		ArgumentNullException.ThrowIfNull(materialNames);

		var maps = new Dictionary<string, UvNormalMap>(StringComparer.OrdinalIgnoreCase);
		for (var index = 0; index < materialNames.Count; index++)
		{
			var stem = Path.GetFileNameWithoutExtension(VmtDocument.NormalizePath(materialNames[index]));
			if (string.IsNullOrWhiteSpace(stem) || maps.ContainsKey(stem))
				continue;
			var map = Bake(geometry, index, resolution);
			if (map is not null)
				maps[stem] = map;
		}

		return maps;
	}

	private static byte Encode(double unit)
		=> (byte)Math.Clamp(Math.Round((unit * 0.5 + 0.5) * 255.0, MidpointRounding.AwayFromZero), 0, 255);
}

