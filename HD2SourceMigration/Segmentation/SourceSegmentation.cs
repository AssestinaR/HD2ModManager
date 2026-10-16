using HD2SourceModel;

namespace HD2SourceMigration.Segmentation;

/// <summary>一份网格来自哪里。</summary>
public enum MeshGroupKind
{
	/// <summary>身体本体的一部分（由固定 bodypart 按骨骼归属切开）。</summary>
	BodyPart,

	/// <summary>一个可切换的 L4D2 bodygroup（衣服/配件开关），整块作为一个额外 Unit。</summary>
	Component,
}

/// <summary>
/// 一份待写成 Unit 的网格。
/// <para>
/// 顶点与三角形都用<b>源几何的下标</b>表示，不复制坐标——切分只是"把下标分组"，
/// 坐标始终只有 <c>RetargetResult.Output</c> 那一份。这样"切分不改变形变"这件事在数据结构上就是显然的。
/// </para>
/// </summary>
/// <param name="Name">可读名字：部位名，或 bodygroup 的原始名字（如 <c>裙Dress_Skirt</c>）。</param>
/// <param name="Region">
/// <see cref="MeshGroupKind.BodyPart"/> 时是它自己的部位；
/// <see cref="MeshGroupKind.Component"/> 时是该配件<b>语义上</b>落在哪个部位（按多数顶点推出来），用来选槽位。
/// </param>
/// <param name="TriangleIndices">指向 <see cref="SourceGeometry.Triangles"/> 的下标，升序。</param>
/// <param name="VertexIndices">指向 <see cref="SourceGeometry.Vertices"/> 的下标，升序去重。</param>
public sealed record MeshGroup(
	string Name,
	MeshGroupKind Kind,
	BodyRegion Region,
	int SourceBodyPartIndex,
	int SourceVariantCount,
	IReadOnlyList<int> TriangleIndices,
	IReadOnlyList<int> VertexIndices)
{
	public int TriangleCount => TriangleIndices.Count;

	public int VertexCount => VertexIndices.Count;
}

/// <summary>
/// 把装配好的源几何切成"每份一个 Unit"。
/// <para>
/// <b>切分的两条依据都是数据里的，没有一条是猜的：</b>
/// </para>
/// <list type="number">
/// <item><b>L4D2 的 bodypart 本来就是不相交的。</b>实测 Lunalice 的 32 个 bodypart
/// 顶点区间两两不相交（重叠对数 0/31），所以"拆"实际上是<b>分组</b>而不是切割——
/// 不需要重新切网格，也不需要重算权重边界。</item>
/// <item><b>固定 bodypart（只有 1 个变体）才是身体本体</b>，按骨骼归属切成七份；
/// <b>多变体的 bodypart 是可切换开关</b>，每个整体作为一个额外 Unit。判据是变体数，不是名字。</item>
/// </list>
/// </summary>
public static class SourceSegmentation
{
	/// <summary>一次切分的结果。</summary>
	public sealed record Result(
		IReadOnlyList<MeshGroup> Groups,
		IReadOnlyList<string> Diagnostics)
	{
		public IEnumerable<MeshGroup> BodyParts => Groups.Where(group => group.Kind == MeshGroupKind.BodyPart);

		public IEnumerable<MeshGroup> Components => Groups.Where(group => group.Kind == MeshGroupKind.Component);
	}

	/// <param name="resolvedSourceToHd2">M1 解出的"源骨下标 → HD2 骨下标"绑定表。</param>
	public static Result Split(
		SourceModelDocument model,
		SourceGeometry geometry,
		BodyRegionResolver resolver,
		IReadOnlyDictionary<int, int> resolvedSourceToHd2)
	{
		ArgumentNullException.ThrowIfNull(model);
		ArgumentNullException.ThrowIfNull(geometry);
		ArgumentNullException.ThrowIfNull(resolver);
		ArgumentNullException.ThrowIfNull(resolvedSourceToHd2);

		var diagnostics = new List<string>();
		var vertexRegion = ResolveVertexRegions(geometry, resolver, resolvedSourceToHd2);
		var bodyParts = new List<MeshGroup>();
		var components = new List<(int PartIndex, MeshGroup Group)>();

		// bodypart 名 → 变体数。多变体 = 可切换开关。
		var variantsByName = new Dictionary<string, int>(StringComparer.Ordinal);
		foreach (var bodyPart in model.BodyParts)
			variantsByName[bodyPart.Name] = bodyPart.Models.Count;

		// 固定 bodypart 按部位收三角形；可切换 bodypart 各成一份。
		var bodyPartTriangles = new Dictionary<BodyRegion, List<int>>();
		foreach (var region in BodyRegions.All)
			bodyPartTriangles[region] = [];

		var partIndex = 0;
		foreach (var part in geometry.Parts)
		{
			var variantCount = variantsByName.GetValueOrDefault(part.BodyPart, 1);
			if (variantCount > 1)
			{
				var triangles = Range(part.TriangleStart, part.TriangleCount);
				components.Add((partIndex, new MeshGroup(
					part.BodyPart,
					MeshGroupKind.Component,
					DominantRegion(geometry, vertexRegion, triangles),
					partIndex,
					variantCount,
					triangles,
					CollectVertices(geometry, triangles))));
			}
			else
			{
				for (var t = part.TriangleStart; t < part.TriangleStart + part.TriangleCount; t++)
				{
					var triangle = geometry.Triangles[t];
					bodyPartTriangles[RegionOfTriangle(vertexRegion, triangle)].Add(t);
				}
			}

			partIndex++;
		}

		foreach (var region in BodyRegions.All)
		{
			var triangles = bodyPartTriangles[region];
			if (triangles.Count == 0)
			{
				diagnostics.Add($"部位 {region.Name()} 没有任何三角形；源模型的固定 bodypart 里没有它。");
				continue;
			}

			triangles.Sort();
			bodyParts.Add(new MeshGroup(
				region.Name(),
				MeshGroupKind.BodyPart,
				region,
				-1,
				1,
				triangles,
				CollectVertices(geometry, triangles)));
		}

		// 顺序稳定且有意义：**七份本体在前**（槽位升序），配件在后（按源 bodypart 顺序）。
		// 这样每份本体都拿到自己槽位的 pieceType 0，也就是"底层"；配件从 1 开始叠上去。
		var groups = new List<MeshGroup>(bodyParts.Count + components.Count);
		groups.AddRange(bodyParts);
		groups.AddRange(components.OrderBy(item => item.PartIndex).Select(item => item.Group));
		return new Result(groups, diagnostics);
	}

	/// <summary>逐顶点算部位。主骨 = 三组权重里最大的那个；权重全零或绑不上的归胸。</summary>
	private static BodyRegion[] ResolveVertexRegions(
		SourceGeometry geometry,
		BodyRegionResolver resolver,
		IReadOnlyDictionary<int, int> resolvedSourceToHd2)
	{
		var result = new BodyRegion[geometry.Vertices.Count];
		for (var i = 0; i < result.Length; i++)
		{
			var vertex = geometry.Vertices[i];
			var weights = new[]
			{
				(vertex.Weight0.BoneIndex, vertex.Weight0.Weight),
				(vertex.Weight1.BoneIndex, vertex.Weight1.Weight),
				(vertex.Weight2.BoneIndex, vertex.Weight2.Weight),
			};

			var best = -1;
			var bestWeight = 0.0f;
			foreach (var (boneIndex, weight) in weights)
			{
				if (weight <= bestWeight)
					continue;
				best = boneIndex;
				bestWeight = weight;
			}

			result[i] = best >= 0
				? resolver.RegionOfVertex(resolvedSourceToHd2, best)
				: BodyRegion.Chest;
		}

		return result;
	}

	/// <summary>
	/// 三角形归哪一份：三个顶点里占多数的那一份。跨边界的三角形必然要选一边——
	/// 因为切分不影响形变（两边用的是同一套骨架与同一批权重），选哪边只影响"它归哪个槽管"。
	/// </summary>
	private static BodyRegion RegionOfTriangle(BodyRegion[] vertexRegion, SourceTriangle triangle)
	{
		var a = vertexRegion[triangle.A];
		var b = vertexRegion[triangle.B];
		var c = vertexRegion[triangle.C];
		if (a == b || a == c)
			return a;
		if (b == c)
			return b;
		return a;
	}

	private static BodyRegion DominantRegion(
		SourceGeometry geometry, BodyRegion[] vertexRegion, IReadOnlyList<int> triangles)
	{
		var counts = new Dictionary<BodyRegion, int>();
		foreach (var index in triangles)
		{
			var triangle = geometry.Triangles[index];
			foreach (var vertex in new[] { triangle.A, triangle.B, triangle.C })
				counts[vertexRegion[vertex]] = counts.GetValueOrDefault(vertexRegion[vertex]) + 1;
		}

		return counts.Count == 0
			? BodyRegion.Chest
			: counts.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key).First().Key;
	}

	private static List<int> Range(int start, int count)
	{
		var result = new List<int>(count);
		for (var i = 0; i < count; i++)
			result.Add(start + i);
		return result;
	}

	private static List<int> CollectVertices(SourceGeometry geometry, IReadOnlyList<int> triangles)
	{
		var seen = new HashSet<int>();
		foreach (var index in triangles)
		{
			var triangle = geometry.Triangles[index];
			seen.Add(triangle.A);
			seen.Add(triangle.B);
			seen.Add(triangle.C);
		}

		var result = seen.ToList();
		result.Sort();
		return result;
	}
}
