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
