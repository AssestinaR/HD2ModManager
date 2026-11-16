using HD2SourceModel;

namespace HD2SourceMigration.VpkConversion;

/// <summary>
/// 把一个「镂空材质」的三角形按 alpha 掩码<b>真的裁开</b>，让它不再需要 alpha 测试。
/// <para>
/// <b>为什么值得做。</b>HD2 里没有任何模板同时有「逐像素镂空」和「金属度」两个槽：
/// <c>alphaclip</c> 有镂空、没有金属度；<c>advanced</c> 反之。而源侧有一大批件是
/// 「金属 + 镂空」（实测 MourningVeil 的 14 个金属件全是 <c>$alphatest 1</c> + alpha 图案：
/// 项链、垂坠、发饰、鞋扣…）。它们只能二选一，于是金属件永远拿不到金属反射。
/// 把镂空**变成几何**就没有这个取舍了：几何本身没有洞，材质可以换成 <c>advanced</c>，
/// 金属度、法线、粗糙度全都可用。
/// </para>
/// <para>
/// <b>做法。</b>对目标材质的每个三角形，用底色 alpha 在三个角上采样：
/// 三个角都在里面 → 原样保留；都在外面 → 丢弃；跨边界 → 按中点四分裂递归（默认 2 层）。
/// 深度 2 意味着边界精度约 1/4 三角形，实测与原图硬裁的锯齿边相当，面数 +24%。
/// </para>
/// <para>
/// <b>刻意保守的地方。</b>只有 alpha 在<em>这个材质自己的三角形上</em>真的「有里有外」时才裁。
/// 有些材质的 UV 是一条缝（实测 <c>Mat_ACCMetal_Tiara</c> 的 U 只有 0.001 宽），
/// 所有采样都落在同一个纹素上 —— 那种情况裁下去会把整件删掉，所以直接跳过、
/// 保留原来的镂空材质。
/// </para>
/// </summary>
public static class CutoutMesh
{
	/// <summary>一个材质的 alpha 采样结果：它自己的三角形顶点里有多少个落在"可见"一侧。</summary>
	public sealed record MaterialAlpha(string Material, int Inside, int Total)
	{
		/// <summary>UV 栅格里被几何覆盖的格子数（0 = 没栅格化成功，调用方应退回顶点口径）。</summary>
		public int GridCovered { get; init; }

		/// <summary>被覆盖的格子里 alpha ≥ 阈值 的比例（0..1）。</summary>
		public double GridOpaqueRatio { get; init; }

		/// <summary>
		/// <b>被覆盖区域里最大的一块连成一片的"透明"区域，占覆盖面积的比例。</b>
		/// <para>
		/// 这一条是"真镂空"与"alpha 用错地方"唯一能分开的量：
		/// </para>
		/// <list type="bullet">
		/// <item>蕾丝 / 纹身的洞是<b>设计</b>：小且密、彼此独立 ⇒ 最大连通块占比很小；</item>
		/// <item>图集空白（月牙/耳坠踩到了别的件之间的空隙）是<b>连成一大片</b>的 ⇒ 占比很大。</item>
		/// </list>
		/// <para>
		/// 只看"裁掉多少"分不开这两类 —— 实测 MourningVeil 的胸部纹身（真镂空）被裁 54%，
		/// 而误用 alpha 的月牙是 55%，几乎一样。2026-11-14 就是栽在这里。
		/// </para>
		/// </summary>
		public double GridLargestHoleRatio { get; init; }
	}

	/// <summary>栅格化用的 UV 分辨率。256² 足够分辨"小洞密布"与"一大片空白"，且很快。</summary>
	private const int MeasureGridSize = 256;


	/// <summary>
	/// <b>只测量、不动几何</b>：按每个材质自己的三角形顶点采底色 alpha，报"裁下去会丢掉多少"。
	/// <para>
	/// 存在的理由：几何裁切（<see cref="Cut"/>）是显式选项，默认不开；但"这份 alpha 用在这件上
	/// 会不会把件打穿"这个问题**必须默认就能回答** —— 材质的 alphaclip 决策在默认路径上是开着的，
	/// 它需要这个结论才不会把整片装饰件戳成筛子（实测 MourningVeil 的月牙/耳坠）。
	/// </para>
	/// </summary>
	public static IReadOnlyList<MaterialAlpha> Measure(
		SourceGeometry geometry,
		IReadOnlyList<string> materialNames,
		Func<string, Func<double, double, double>?> alphaSamplerFor,
		IReadOnlyCollection<string> stems,
		double threshold = 0.5)
	{
		ArgumentNullException.ThrowIfNull(geometry);
		ArgumentNullException.ThrowIfNull(materialNames);
		ArgumentNullException.ThrowIfNull(alphaSamplerFor);
		ArgumentNullException.ThrowIfNull(stems);

		var wanted = new HashSet<string>(
			stems.Where(s => !string.IsNullOrWhiteSpace(s)).Select(StemOf), StringComparer.OrdinalIgnoreCase);
		if (wanted.Count == 0)
			return [];

		// 先按材质下标分桶，避免每个材质都全扫一遍三角形。
		var trianglesByMaterial = new Dictionary<int, List<SourceTriangle>>();
		foreach (var triangle in geometry.Triangles)
			trianglesByMaterial.TryAdd(triangle.MaterialIndex, []);
		foreach (var triangle in geometry.Triangles)
			trianglesByMaterial[triangle.MaterialIndex].Add(triangle);

		var results = new List<MaterialAlpha>();
		var grid = new float[MeasureGridSize * MeasureGridSize];
		var coveredFlags = new bool[MeasureGridSize * MeasureGridSize];

		for (var index = 0; index < materialNames.Count; index++)
		{
			if (!wanted.Contains(StemOf(materialNames[index])))
				continue;
			var sampler = alphaSamplerFor(StemOf(materialNames[index]));
			if (sampler is null)
				continue;
			if (!trianglesByMaterial.TryGetValue(index, out var triangles) || triangles.Count == 0)
				continue;

			// 顶点口径（老闸门，留着做交叉校验）。
			var inside = 0;
			var total = 0;
			foreach (var triangle in triangles)
			{
				foreach (var vertexIndex in (ReadOnlySpan<int>)[triangle.A, triangle.B, triangle.C])
				{
					var vertex = geometry.Vertices[vertexIndex];
					total++;
					if (sampler(vertex.U, vertex.V) >= threshold)
						inside++;
				}
			}

			// UV 栅格口径：这才能看见"洞是小而密"还是"一大片"。
			Array.Fill(grid, -1f);
			Array.Clear(coveredFlags);
			var covered = 0;
			var opaque = 0;
			foreach (var triangle in triangles)
			{
				var a = geometry.Vertices[triangle.A];
				var b = geometry.Vertices[triangle.B];
				var c = geometry.Vertices[triangle.C];
				RasterizeTriangle(grid, coveredFlags, a.U, a.V, b.U, b.V, c.U, c.V, sampler, threshold,
					ref covered, ref opaque);
			}

			var largestHole = covered > 0 ? LargestTransparentBlob(grid, coveredFlags, threshold) / (double)covered : 0;

			results.Add(new MaterialAlpha(StemOf(materialNames[index]), inside, total)
			{
				GridCovered = covered,
				GridOpaqueRatio = covered > 0 ? opaque / (double)covered : 1.0,
				GridLargestHoleRatio = largestHole,
			});
		}

		return results.OrderBy(item => item.Material, StringComparer.OrdinalIgnoreCase).ToArray();
	}

	/// <summary>把一个 UV 三角形按格心采样铺进栅格（只处理落在 [0,1] 内的部分）。</summary>
	private static void RasterizeTriangle(
		float[] grid,
		bool[] coveredFlags,
		double au, double av, double bu, double bv, double cu, double cv,
		Func<double, double, double> sampler,
		double threshold,
		ref int covered,
		ref int opaque)
	{
		const int n = MeasureGridSize;
		double minU = Math.Min(au, Math.Min(bu, cu));
		double maxU = Math.Max(au, Math.Max(bu, cu));
		double minV = Math.Min(av, Math.Min(bv, cv));
		double maxV = Math.Max(av, Math.Max(bv, cv));

		var x0 = Math.Max(0, (int)Math.Floor(minU * n));
		var x1 = Math.Min(n - 1, (int)Math.Ceiling(maxU * n));
		var y0 = Math.Max(0, (int)Math.Floor(minV * n));
		var y1 = Math.Min(n - 1, (int)Math.Ceiling(maxV * n));
		if (x1 < x0 || y1 < y0)
			return;

		var denominator = ((bv - cv) * (au - cu)) + ((cu - bu) * (av - cv));
		if (Math.Abs(denominator) < 1e-12)
			return;

		for (var y = y0; y <= y1; y++)
		{
			for (var x = x0; x <= x1; x++)
			{
				var pu = (x + 0.5) / n;
				var pv = (y + 0.5) / n;
				var w0 = (((bv - cv) * (pu - cu)) + ((cu - bu) * (pv - cv))) / denominator;
				var w1 = (((cv - av) * (pu - cu)) + ((au - cu) * (pv - cv))) / denominator;
				var w2 = 1.0 - w0 - w1;
				const double eps = -1e-6;
				if (w0 < eps || w1 < eps || w2 < eps)
					continue;

				var offset = (y * n) + x;
				if (coveredFlags[offset])
					continue;
				coveredFlags[offset] = true;
				covered++;

				var value = sampler(pu, pv);
				grid[offset] = (float)value;
				if (value >= threshold)
					opaque++;
			}
		}
	}

	/// <summary>被覆盖区域里最大的一块连成一片的"透明"格子数（4 邻接）。</summary>
	private static int LargestTransparentBlob(float[] grid, bool[] coveredFlags, double threshold)
	{
		const int n = MeasureGridSize;
		var visited = new bool[grid.Length];
		var stack = new Stack<int>();
		var largest = 0;
		for (var start = 0; start < grid.Length; start++)
		{
			if (visited[start] || !coveredFlags[start] || grid[start] >= threshold)
				continue;

			var size = 0;
			stack.Push(start);
			visited[start] = true;
			while (stack.Count > 0)
			{
				var offset = stack.Pop();
				size++;
				var x = offset % n;
				var y = offset / n;
				if (x > 0) TryPush(offset - 1);
				if (x < n - 1) TryPush(offset + 1);
				if (y > 0) TryPush(offset - n);
				if (y < n - 1) TryPush(offset + n);

				void TryPush(int next)
				{
					if (visited[next] || !coveredFlags[next] || grid[next] >= threshold)
						return;
					visited[next] = true;
					stack.Push(next);
				}
			}

			if (size > largest)
				largest = size;
		}

		return largest;
	}

	/// <summary>裁一个材质的结果（用于报告"裁前/裁后各多少面、有没有被跳过"）。</summary>
	public sealed record MaterialCut(
		int MaterialIndex,
		string Material,
		int Before,
		int After,
		string? Skipped);

	/// <summary>
	/// 把 <paramref name="stems"/> 里的材质裁成几何。
	/// </summary>
	/// <param name="geometry">源几何（顶点 + 三角形 + 分段）。</param>
	/// <param name="materialNames">源材质路径（下标与三角形里的 <c>MaterialIndex</c> 对齐）。</param>
	/// <param name="alphaSamplerFor">给定材质裸名，返回它的底色 alpha 采样器；返回 null 表示读不到。</param>
	/// <param name="stems">要裁的材质裸名（不区分大小写）。只有它们会被动。</param>
	/// <param name="threshold">alpha 阈值（与 <c>$alphatestreference</c> 的 0.5 一致）。</param>
	/// <param name="depth">四分裂的最大深度（0 = 只丢弃整块外的三角形）。</param>
	public static (SourceGeometry Geometry, IReadOnlyList<MaterialCut> Results) Cut(
		SourceGeometry geometry,
		IReadOnlyList<string> materialNames,
		Func<string, Func<double, double, double>?> alphaSamplerFor,
		IReadOnlyCollection<string> stems,
		double threshold = 0.5,
		int depth = 2)
	{
		ArgumentNullException.ThrowIfNull(geometry);
		ArgumentNullException.ThrowIfNull(materialNames);
		ArgumentNullException.ThrowIfNull(alphaSamplerFor);
		ArgumentNullException.ThrowIfNull(stems);
		if (stems.Count == 0)
			return (geometry, []);

		var wanted = new HashSet<string>(stems.Where(s => !string.IsNullOrWhiteSpace(s)), StringComparer.OrdinalIgnoreCase);
		var indices = new List<int>();
		for (var index = 0; index < materialNames.Count; index++)
		{
			if (wanted.Contains(StemOf(materialNames[index])))
				indices.Add(index);
		}

		if (indices.Count == 0)
			return (geometry, []);

		// 每个目标材质：先看它自己的三角形上 alpha 有没有"里有外"。没有就不裁（见类型注释）。
		var samplers = new Dictionary<int, Func<double, double, double>>();
		var skipped = new Dictionary<int, string>();
		foreach (var materialIndex in indices)
		{
			var stem = StemOf(materialNames[materialIndex]);
			var sampler = alphaSamplerFor(stem);
			if (sampler is null)
			{
				skipped[materialIndex] = "读不到底色（没有 $basetexture 或贴图不在来源里）";
				continue;
			}

			var inside = 0;
			var total = 0;
			foreach (var triangle in geometry.Triangles)
			{
				if (triangle.MaterialIndex != materialIndex)
					continue;
				foreach (var vertexIndex in (ReadOnlySpan<int>)[triangle.A, triangle.B, triangle.C])
				{
					var vertex = geometry.Vertices[vertexIndex];
					total++;
					if (sampler(vertex.U, vertex.V) >= threshold)
						inside++;
				}
			}

			if (total == 0)
			{
				skipped[materialIndex] = "没有几何";
				continue;
			}

			if (inside == 0)
			{
				skipped[materialIndex] = "alpha 全在外面（UV 是缝隙或掩码全 0）—— 裁了会整件消失";
				continue;
			}

			if (inside == total)
			{
				skipped[materialIndex] = "alpha 全在里面（没有真的镂空）—— 不需要裁";
				continue;
			}

			// **裁掉大半也不行。**
			//
			// 实测 MourningVeil（2026-11-14，用户实机报"挂钟耳坠和胸前月牙被打出不该有的洞"）：
			// `mat_accmetal_necklace` / `mat_metal1_earings` 都声明了 `$alphatest 1`，底图是那张被
			// 十几个材质共用的装饰件图集；它们几何用到的像素里 40%~55% 的 alpha 接近 0。
			// 照裁的结果就是"一片完整的件被打成筛子"，而源侧观感是完整的。
			//
			// 一个会删掉大半件的"镂空"不是镂空，是这份 alpha 用错了地方。这类件按不透明处理：
			// 少一层透明，但绝不打穿。
			if (inside * 2 < total)
			{
				skipped[materialIndex] =
					$"alpha 只有 {inside * 100.0 / total:F0}% 的顶点算「可见」—— 裁掉大半会把件打穿，按不透明处理";
				continue;
			}

			samplers[materialIndex] = sampler;
		}

		if (samplers.Count == 0)
			return (geometry, indices.Select(index => new MaterialCut(
				index, StemOf(materialNames[index]), Count(geometry, index), Count(geometry, index),
				skipped.GetValueOrDefault(index))).ToArray());

		var vertices = new List<SourceVertex>(geometry.Vertices);
		var midpoints = new Dictionary<(int, int), int>();
		var triangles = new List<SourceTriangle>(geometry.Triangles.Count);

		// **按分段逐段处理**：Part 的三角形区间是"连续窗口"，裁完必须重建，
		// 否则 `DescribeNoDrawParts` / section 归属会指向正确的区间之外。
		var parts = new List<SourceGeometryPart>(geometry.Parts.Count);
		var cursor = 0;
		foreach (var part in geometry.Parts)
		{
			var start = triangles.Count;
			var end = Math.Min(part.TriangleStart + part.TriangleCount, geometry.Triangles.Count);
			for (var triangleIndex = Math.Max(0, part.TriangleStart); triangleIndex < end; triangleIndex++)
			{
				var triangle = geometry.Triangles[triangleIndex];
				if (!samplers.TryGetValue(triangle.MaterialIndex, out var sampler))
				{
					triangles.Add(triangle);
					continue;
				}

				Split(triangle, 0, sampler);
			}

			// **顶点区间保持原样**：新顶点是**追加**的，原下标一个都没动，
			// 所以 `VertexStart/VertexCount` 仍然成立。只有三角形区间变了。
			// （曾经在这里按"新三角形用到的顶点"重算区间，结果窗口被放宽，
			//   下游按窗口去查顶点字典时抛 KeyNotFoundException —— 别重算。）
			parts.Add(part with
			{
				TriangleStart = start,
				TriangleCount = triangles.Count - start,
			});
			cursor = triangles.Count;
		}

		// 不在任何分段里的三角形（理论上不该有）：原样接在最后，绝不丢。
		for (var triangleIndex = cursor; triangleIndex < geometry.Triangles.Count; triangleIndex++)
		{
			var triangle = geometry.Triangles[triangleIndex];
			if (!samplers.TryGetValue(triangle.MaterialIndex, out var sampler))
			{
				triangles.Add(triangle);
				continue;
			}

			Split(triangle, 0, sampler);
		}

		var result = new SourceGeometry(vertices, triangles, parts);
		var report = indices
			.Select(index => new MaterialCut(
				index,
				StemOf(materialNames[index]),
				Count(geometry, index),
				Count(result, index),
				skipped.GetValueOrDefault(index)))
			.ToArray();
		return (result, report);

		void Split(SourceTriangle triangle, int level, Func<double, double, double> sampler)
		{
			var a = vertices[triangle.A];
			var b = vertices[triangle.B];
			var c = vertices[triangle.C];
			var inside = (sampler(a.U, a.V) >= threshold ? 1 : 0)
				+ (sampler(b.U, b.V) >= threshold ? 1 : 0)
				+ (sampler(c.U, c.V) >= threshold ? 1 : 0);

			if (inside == 3)
			{
				triangles.Add(triangle);
				return;
			}

			if (inside == 0)
				return;

			if (level >= depth)
			{
				// 到最深一层还跨边界：按重心决定归属（边缘精度就到 1/4 三角形为止）。
				if (sampler((a.U + b.U + c.U) / 3, (a.V + b.V + c.V) / 3) >= threshold)
					triangles.Add(triangle);
				return;
			}

			var ab = Midpoint(triangle.A, triangle.B, a, b);
			var bc = Midpoint(triangle.B, triangle.C, b, c);
			var ca = Midpoint(triangle.C, triangle.A, c, a);
			var materialIndex = triangle.MaterialIndex;
			Split(new SourceTriangle(triangle.A, ab, ca, materialIndex), level + 1, sampler);
			Split(new SourceTriangle(ab, triangle.B, bc, materialIndex), level + 1, sampler);
			Split(new SourceTriangle(ca, bc, triangle.C, materialIndex), level + 1, sampler);
			Split(new SourceTriangle(ab, bc, ca, materialIndex), level + 1, sampler);
		}

		int Midpoint(int left, int right, SourceVertex a, SourceVertex b)
		{
			var key = left < right ? (left, right) : (right, left);
			if (midpoints.TryGetValue(key, out var existing))
				return existing;

			// 骨骼权重**继承较重的那一端**，不做混合：每顶点硬上限是 3 组权重，
			// 混合两组不同骨骼会超出上限，而布料/金属这类件两端权重本来就几乎一致。
			var weight = a.Weight0.Weight >= b.Weight0.Weight ? a.Weight0 : b.Weight0;
			vertices.Add(new SourceVertex(
				Lerp(a.Position, b.Position),
				Lerp(a.Normal, b.Normal),
				(a.U + b.U) / 2,
				(a.V + b.V) / 2,
				weight,
				a.Weight1,
				a.Weight2));
			var index = vertices.Count - 1;
			midpoints[key] = index;
			return index;
		}
	}

	/// <summary>材质路径的最后一段（去掉 <c>.vmt</c>），与三角里的下标口径一致。</summary>
	public static string StemOf(string path)
		=> Path.GetFileNameWithoutExtension(VmtDocument.NormalizePath(path));

	private static Vec3 Lerp(Vec3 a, Vec3 b)
		=> new((a.X + b.X) / 2, (a.Y + b.Y) / 2, (a.Z + b.Z) / 2);

	private static int Count(SourceGeometry geometry, int materialIndex)
	{
		var count = 0;
		foreach (var triangle in geometry.Triangles)
		{
			if (triangle.MaterialIndex == materialIndex)
				count++;
		}

		return count;
	}
}
