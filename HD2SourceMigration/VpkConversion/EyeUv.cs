using HD2SourceModel;

namespace HD2SourceMigration.VpkConversion;

/// <summary>
/// 眼球材质（<c>EyeRefract</c> / <c>Eyes</c>）的颜色图，以及它在 UV 上真正画着眼睛的那块矩形。
/// <para>
/// <b>为什么必须单独记这块矩形。</b>眼球颜色图（<c>$iris</c>）几乎都是<b>图集</b>：
/// 一张 1024² 的图里只有中间一小块画着眼睛，四周是平色的眼白/填充。而
/// <c>EyeRefract</c> 的采样坐标<b>不是模型 UV</b>——Source 的 <c>eyes.vsh</c> 写得很明确：
/// </para>
/// <para>
/// <c>oT1 = (dot($IrisU, worldPos), dot($IrisV, worldPos))</c>，注释也写着
/// 「Texture 1 is a planar projection used for the iris」（见 Valve <c>source-sdk-2013</c>
/// 的 <c>materialsystem/stdshaders/eyes.vsh</c>）。投影向量由引擎按眼球位置与
/// <c>$EyeballRadius</c> 每帧给出，模型自己的 UV 完全不参与——实测这份模组的眼球网格
/// 28 个顶点 UV <b>全是 (0,1)</b>。
/// </para>
/// <para>
/// 直接拿模型 UV 去采这张图 ⇒ 整个眼球采样同一个像素 ⇒ 一块纯色。所以要把眼睛那块矩形
/// 重新摊到眼球上：<b>U 跟模型的 Y（左右）、V 跟 Z（上下）</b>，与源侧那条平面投影同构。
/// </para>
/// </summary>
/// <param name="Texture">颜色图名（<c>$iris</c> 的值，已去掉目录与扩展名）。</param>
/// <param name="U0">内容区左边界（0..1）。</param>
/// <param name="V0">内容区上边界（0..1，<b>V=0 是图像顶部</b>）。</param>
/// <param name="U1">内容区右边界。</param>
/// <param name="V1">内容区下边界。</param>
public sealed record EyeColorSource(string Texture, double U0, double V0, double U1, double V1)
{
	public double CenterU => (U0 + U1) / 2;

	public double CenterV => (V0 + V1) / 2;

	/// <summary>内容区半宽（UV）。= 1 表示整张图都是眼睛，没有填充边。</summary>
	public double HalfU => Math.Max(1e-6, (U1 - U0) / 2);

	/// <summary>内容区半高（UV）。</summary>
	public double HalfV => Math.Max(1e-6, (V1 - V0) / 2);

	/// <summary>这张图是不是"整张就是一只眼睛"（没有可识别的填充边）。</summary>
	public bool IsWholeTexture => U0 <= 1e-6 && V0 <= 1e-6 && U1 >= 1 - 1e-6 && V1 >= 1 - 1e-6;

	public string Describe() =>
		$"{Texture}（眼睛画在 U[{U0:F3},{U1:F3}] × V[{V0:F3},{V1:F3}]，面积 {((U1 - U0) * (V1 - V0)):P0}）";
}

/// <summary>
/// 眼球 UV 重建：找出颜色图里"画着眼睛的那块矩形"，并把眼球网格的 UV 按
/// 模型的 Y/Z 重新投影上去。
/// <para>
/// <b>这是着色器语义，不是针对某个模组的补丁。</b>凡是 <c>$basetexture</c> 缺失、
/// 颜色写在 <c>$iris</c> 里的材质都走这条路（<see cref="SourceMaterialConverter"/> 的
/// <c>ShaderColorKeys</c>）；判据是"<b>VMT 的键 + 贴图的实际内容</b>"，与模组名、
/// 材质名、文件名都无关。
/// </para>
/// </summary>
public static class EyeUv
{
	/// <summary>判定"这个像素不是填充色"的每通道阈值（0..255）。</summary>
	/// <remarks>
	/// 24 是实测取的值：填充区在 DXT 压缩下会有 ±6 的块噪声，而睫毛/眼白/瞳孔的对比都在 40 以上。
	/// 取更小会把压缩噪声当成内容，内容区会涨到整张图。
	/// </remarks>
	public const int ContentThreshold = 24;

	/// <summary>内容区覆盖到这个比例以上，就认为"整张图都是眼睛"（没有填充边）。</summary>
	public const double WholeTextureCoverage = 0.92;

	/// <summary>内容区小于这个比例，就认为"没找到眼睛"（噪声），退回整张图。</summary>
	public const double MinimumCoverage = 0.002;

	/// <summary>
	/// 一张贴图里"画着东西的那块矩形"（UV，V=0 在图像顶部）。
	/// <para>
	/// 判据两步：① 取外框 2 像素的平均色当填充色（眼球图集四周一定是平色）；
	/// ② 逐像素与填充色的最大通道差 &gt; <see cref="ContentThreshold"/> 就算内容，取内容的包围盒。
	/// 内容几乎铺满整张图时返回 (0,0,1,1)——那时"整张图就是眼睛"，没有可裁的边。
	/// </para>
	/// </summary>
	public static (double U0, double V0, double U1, double V1) ContentRect(byte[] rgba, int width, int height)
	{
		var whole = (U0: 0d, V0: 0d, U1: 1d, V1: 1d);
		if (width < 4 || height < 4 || rgba.Length < width * height * 4)
			return whole;

		// ① 填充色 = 外框 2 像素的平均。
		long sumR = 0, sumG = 0, sumB = 0, sumA = 0;
		var samples = 0;
		for (var y = 0; y < height; y++)
		{
			var isBorderRow = y < 2 || y >= height - 2;
			for (var x = 0; x < width; x++)
			{
				if (!isBorderRow && x >= 2 && x < width - 2)
					continue;
				var offset = (y * width + x) * 4;
				sumR += rgba[offset];
				sumG += rgba[offset + 1];
				sumB += rgba[offset + 2];
				sumA += rgba[offset + 3];
				samples++;
			}
		}

		if (samples == 0)
			return whole;
		var backR = sumR / (double)samples;
		var backG = sumG / (double)samples;
		var backB = sumB / (double)samples;
		var backA = sumA / (double)samples;

		// ② 内容包围盒。
		int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
		long content = 0;
		for (var y = 0; y < height; y++)
		{
			for (var x = 0; x < width; x++)
			{
				var offset = (y * width + x) * 4;
				var diff = Math.Max(
					Math.Max(Math.Abs(rgba[offset] - backR), Math.Abs(rgba[offset + 1] - backG)),
					Math.Max(Math.Abs(rgba[offset + 2] - backB), Math.Abs(rgba[offset + 3] - backA)));
				if (diff <= ContentThreshold)
					continue;

				content++;
				if (x < minX) minX = x;
				if (x > maxX) maxX = x;
				if (y < minY) minY = y;
				if (y > maxY) maxY = y;
			}
		}

		var total = (double)width * height;
		if (content < total * MinimumCoverage || minX > maxX || minY > maxY)
			return whole;   // 没找到可辨认的内容：按"整张图就是眼睛"处理，不解也安全

		var boxArea = (double)(maxX - minX + 1) * (maxY - minY + 1) / total;
		if (boxArea >= WholeTextureCoverage)
			return whole;

		return (
			(double)minX / width,
			(double)minY / height,              // V=0 在图像顶部：与像素行同向
			(double)(maxX + 1) / width,
			(double)(maxY + 1) / height);
	}

	/// <summary>
	/// 把<paramref name="materialNames"/>里那几个"颜色来自 <c>$iris</c>"的材质的 UV 重算一遍。
	/// <para>
	/// <b>为什么必须整份几何一起处理。</b>UV 是<b>逐顶点</b>的：同一个顶点如果既被眼球三角形
	/// 用到、又被别处（头、脸）用到，改它就会毁掉另一处的贴图。所以只有"只被眼球材质用到"的
	/// 顶点才会被改，混用的顶点原样保留并记一条说明。
	/// </para>
	/// </summary>
	/// <param name="geometry">源几何。</param>
	/// <param name="materialNames">模型材质表（下标 = <c>SourceTriangle.MaterialIndex</c>）。</param>
	/// <param name="resolve">材质裸名 → 眼球颜色源；<c>null</c> 表示这个材质不是眼球。</param>
	public static (SourceGeometry Geometry, IReadOnlyList<string> Notes) Reproject(
		SourceGeometry geometry,
		IReadOnlyList<string>? materialNames,
		Func<string, EyeColorSource?> resolve)
	{
		ArgumentNullException.ThrowIfNull(geometry);
		ArgumentNullException.ThrowIfNull(resolve);
		if (materialNames is null || materialNames.Count == 0 || geometry.Triangles.Count == 0)
			return (geometry, []);

		// ① 哪些材质是眼球。
		var eyeByMaterial = new Dictionary<int, EyeColorSource>();
		for (var index = 0; index < materialNames.Count; index++)
		{
			var resolved = resolve(materialNames[index]);
			if (resolved is not null)
				eyeByMaterial[index] = resolved;
		}

		if (eyeByMaterial.Count == 0)
			return (geometry, []);

		// ② 每个顶点被哪些材质用到（判"混用"）。
		var materialsByVertex = new Dictionary<int, HashSet<int>>();
		foreach (var triangle in geometry.Triangles)
		{
			foreach (var vertex in stackalloc[] { triangle.A, triangle.B, triangle.C })
			{
				if (!materialsByVertex.TryGetValue(vertex, out var set))
					materialsByVertex[vertex] = set = [];
				set.Add(triangle.MaterialIndex);
			}
		}

		var projected = new Dictionary<int, (double U, double V)>();
		var notes = new List<string>();
		foreach (var (materialIndex, colorSource) in eyeByMaterial.OrderBy(pair => pair.Key))
		{
			var vertices = new HashSet<int>();
			foreach (var triangle in geometry.Triangles)
			{
				if (triangle.MaterialIndex != materialIndex)
					continue;
				vertices.Add(triangle.A);
				vertices.Add(triangle.B);
				vertices.Add(triangle.C);
			}

			if (vertices.Count == 0)
				continue;

			// ③ 眼球自己的包围盒：U 摊到 Y 跨度、V 摊到 Z 跨度。
			//    中心取包围盒中点而不是顶点均值：网格顶点在球面上的疏密不均匀，
			//    均值会被密集的一侧拽偏，瞳孔就不在眼球中心了。
			var minY = double.MaxValue;
			var maxY = double.MinValue;
			var minZ = double.MaxValue;
			var maxZ = double.MinValue;
			foreach (var index in vertices)
			{
				var position = geometry.Vertices[index].Position;
				minY = Math.Min(minY, position.Y);
				maxY = Math.Max(maxY, position.Y);
				minZ = Math.Min(minZ, position.Z);
				maxZ = Math.Max(maxZ, position.Z);
			}

			var centerY = (minY + maxY) / 2;
			var centerZ = (minZ + maxZ) / 2;
			var halfY = (maxY - minY) / 2;
			var halfZ = (maxZ - minZ) / 2;
			if (halfY <= 1e-6 || halfZ <= 1e-6)
			{
				notes.Add($"{materialNames[materialIndex]}：眼球在 Y/Z 上没有跨度，UV 投影做不了，保持原 UV。");
				continue;
			}

			var mixed = 0;
			foreach (var index in vertices)
			{
				if (materialsByVertex[index].Any(material => !eyeByMaterial.ContainsKey(material)))
				{
					mixed++;
					continue;
				}

				var position = geometry.Vertices[index].Position;
				// U 跟模型 Y（+Y 是角色左侧，观察者看到的是图像右侧）；V 跟 Z，且**取反**：
				// VTF/DDS 的 V=0 在图像顶部，而 +Z 是上方 —— 不取反眼睛就是倒着的。
				var u = colorSource.CenterU + (position.Y - centerY) / halfY * colorSource.HalfU;
				var v = colorSource.CenterV - (position.Z - centerZ) / halfZ * colorSource.HalfV;
				projected[index] = (u, v);
			}

			if (mixed != 0)
				notes.Add(
					$"{materialNames[materialIndex]}：有 {mixed} 个顶点同时被别的材质用到（UV 是逐顶点的），"
					+ "这些顶点保持原 UV —— 眼球那部分可能仍是一整块纯色。");
		}

		if (projected.Count == 0)
			return (geometry, notes);

		var vertices2 = geometry.Vertices.ToArray();
		foreach (var (index, uv) in projected)
		{
			var source = vertices2[index];
			vertices2[index] = source with { U = (float)uv.U, V = (float)uv.V };
		}

		notes.Add(
			$"眼球 UV 已按 $iris 的平面投影重算（U 跟模型 Y、V 跟模型 Z，共 {projected.Count} 个顶点）"
			+ "：源侧 EyeRefract 的采样坐标本来就不来自模型 UV（Valve eyes.vsh 的注释："
			+ "「Texture 1 is a planar projection used for the iris」），而这份网格的 UV 是常量。");

		return (geometry with { Vertices = vertices2 }, notes);
	}
}
