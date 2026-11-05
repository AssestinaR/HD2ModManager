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

		// 逐纹素把"覆盖它的所有三角形"的法线累加起来。
		//
		// **为什么要累加而不是"后写的赢"**：同一个 UV 区域常常被**多个面共用**
		// （镜像 UV、尾巴上的叶片、内衣的左右两半）。后写的赢会让同一块贴图上的颜色
		// 在两个朝向之间来回跳，实机表现就是"一块块的色块，甚至像是颜色反了"。
		var count = resolution * resolution;
		var sumX = new double[count];
		var sumY = new double[count];
		var sumZ = new double[count];
		var hits = new int[count];
		var conflict = new bool[count];
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

					var index = (y * resolution) + x;
					if (hits[index] != 0)
					{
						// 已有朝向差不多相反的面覆盖同一纹素 ⇒ 这片 UV 是共用的，
						// 任何单一法线都会让其中一半看起来是错的。标记出来，最后退回"朝正面"。
						var sumLength = Math.Sqrt((sumX[index] * sumX[index]) + (sumY[index] * sumY[index]) + (sumZ[index] * sumZ[index]));
						if (sumLength > 1e-6
							&& (((sumX[index] * nx) + (sumY[index] * ny) + (sumZ[index] * nz)) / sumLength) < 0.5)
						{
							conflict[index] = true;
						}
					}

					sumX[index] += nx;
					sumY[index] += ny;
					sumZ[index] += nz;
					hits[index]++;
					covered = true;
				}
			}
		}

		if (!covered)
			return null;

		var pixels = new byte[count * 4];
		for (var index = 0; index < count; index++)
		{
			var at = index * 4;
			if (hits[index] == 0 || conflict[index])
			{
				// 没覆盖 / 有冲突：写成"朝正面"（128,128,255）。
				// matcap 于是采到图的正中、lightwarp 于是算作受光 —— 也就是**这两样在这块不生效**，
				// 而不是给一个方向错误的颜色。宁可不变，也不要错。
				pixels[at] = 128;
				pixels[at + 1] = 128;
				pixels[at + 2] = 255;
				pixels[at + 3] = 0;
				continue;
			}

			var length = Math.Sqrt((sumX[index] * sumX[index]) + (sumY[index] * sumY[index]) + (sumZ[index] * sumZ[index]));
			if (length < 1e-6)
			{
				pixels[at] = 128;
				pixels[at + 1] = 128;
				pixels[at + 2] = 255;
				pixels[at + 3] = 0;
				continue;
			}

			pixels[at] = Encode(sumX[index] / length);
			pixels[at + 1] = Encode(sumY[index] / length);
			pixels[at + 2] = Encode(sumZ[index] / length);
			pixels[at + 3] = 255;
		}

		return new UvNormalMap(pixels, resolution, resolution);
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

