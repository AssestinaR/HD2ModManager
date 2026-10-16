using HD2SourceModel;

namespace HD2SourceMigration.VpkConversion;

/// <summary>一张已解码的贴图（RGBA8）。</summary>
public sealed record MatCapImage(byte[] Rgba, int Width, int Height)
{
	public static MatCapImage? TryCreate(byte[] rgba, int width, int height) =>
		width > 0 && height > 0 && rgba.Length == width * height * 4 ? new MatCapImage(rgba, width, height) : null;
}

/// <summary>
/// 按<b>三角形面积加权</b>求一个材质上 matcap 贴图的平均色。
/// <para>
/// <b>为什么是面积加权、而不是整图平均。</b>一张 matcap 常常被多个材质共用，每个材质只覆盖它
/// 的一小部分 UV。整图平均会把别处的高光算进来，烘出来的底色整体偏亮。
/// </para>
/// <para>
/// <b>为什么用质心采样、而不是逐像素。</b>HD2 这边底色和 matcap <b>没有逐像素对应关系</b>
/// ——源侧的 matcap 走的是另一套 UV（<c>fix_appearance.py:39</c> 的做法也是取平均）。
/// 三角形质心足以反映"这片面朝哪、反射多亮"，而逐像素采样只会引入一套根本无法对齐的对应关系。
/// </para>
/// <para>
/// 退化三角形（面积 0）会被跳过；一个材质一个三角形都没有时返回 <c>null</c>
/// ——<b>不能返回零</b>，那等于"把底色乘 0"，会把整个材质烧成黑。
/// </para>
/// </summary>
public static class MatCapSampler
{
	/// <summary>
	/// 求 <paramref name="materialIndex"/> 这个材质所占三角形上的 matcap 平均色（0..255，三分量）。
	/// </summary>
	public static double[]? Compute(SourceGeometry geometry, int materialIndex, MatCapImage image)
	{
		ArgumentNullException.ThrowIfNull(geometry);
		ArgumentNullException.ThrowIfNull(image);

		var accumulated = new double[3];
		var totalArea = 0.0;
		foreach (var triangle in geometry.Triangles)
		{
			if (triangle.MaterialIndex != materialIndex)
				continue;
			if (!TryGetTriangle(geometry, triangle, out var a, out var b, out var c))
				continue;

			var area = TriangleArea(a.Position, b.Position, c.Position);
			if (area <= 0)
				continue;

			// 质心 UV：三个角 UV 的算术平均，再按环绕方式处理跨边界。
			var u = (a.U + b.U + c.U) / 3.0;
			var v = (a.V + b.V + c.V) / 3.0;
			var sample = Sample(image, u, v);
			for (var channel = 0; channel < 3; channel++)
				accumulated[channel] += sample[channel] * area;
			totalArea += area;
		}

		if (totalArea <= 0)
			return null;
		for (var channel = 0; channel < 3; channel++)
			accumulated[channel] /= totalArea;
		return accumulated;
	}

	private static bool TryGetTriangle(
		SourceGeometry geometry, SourceTriangle triangle,
		out SourceVertex a, out SourceVertex b, out SourceVertex c)
	{
		a = default;
		b = default;
		c = default;
		if ((uint)triangle.A >= (uint)geometry.Vertices.Count
			|| (uint)triangle.B >= (uint)geometry.Vertices.Count
			|| (uint)triangle.C >= (uint)geometry.Vertices.Count)
		{
			return false;
		}

		a = geometry.Vertices[triangle.A];
		b = geometry.Vertices[triangle.B];
		c = geometry.Vertices[triangle.C];
		return true;
	}

	private static double TriangleArea(Vec3 a, Vec3 b, Vec3 c)
	{
		var ab = b - a;
		var ac = c - a;
		// 叉积长度的一半。
		var cross = new Vec3(
			(ab.Y * ac.Z) - (ab.Z * ac.Y),
			(ab.Z * ac.X) - (ab.X * ac.Z),
			(ab.X * ac.Y) - (ab.Y * ac.X));
		return cross.Length / 2.0;
	}

	/// <summary>按 UV 取最近邻像素。UV 超出 0..1 时按整数部分环绕（VTF 默认就是 wrap）。</summary>
	private static double[] Sample(MatCapImage image, double u, double v)
	{
		var x = Wrap((int)Math.Floor(Wrap(u) * image.Width), image.Width);
		// VTF 的 V 轴自上而下，纹理坐标自下而上，所以这里要翻。
		var y = Wrap((int)Math.Floor((1.0 - Wrap(v)) * image.Height), image.Height);
		var offset = ((y * image.Width) + x) * 4;
		return [image.Rgba[offset], image.Rgba[offset + 1], image.Rgba[offset + 2]];
	}

	private static double Wrap(double value)
	{
		if (!double.IsFinite(value))
			return 0;
		var wrapped = value - Math.Floor(value);
		return wrapped < 0 ? wrapped + 1 : wrapped;
	}

	private static int Wrap(int value, int size)
	{
		if (size <= 0)
			return 0;
		var wrapped = value % size;
		return wrapped < 0 ? wrapped + size : wrapped;
	}
}
