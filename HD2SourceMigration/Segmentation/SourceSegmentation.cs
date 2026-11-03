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

	/// <summary>
	/// 它是所属 bodypart 里的第几个 model（只有 <see cref="MeshGroupKind.Component"/> 有意义）。
	/// <para>
	/// 同一 bodypart 的多个 model 在引擎里是<b>互斥</b>的（bodygroup 选项，同一时刻只画一个），
	/// 挑"默认那一个"时要用这个下标——它就是 MDL 里写着的顺序，不是猜的。
	/// </para>
	/// </summary>
	public int SourceModelIndex { get; init; } = -1;
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

		/// <summary>
		/// <b>逐顶点</b>的部位（下标与 <c>SourceGeometry.Vertices</c> 对齐）。
		/// <para>
		/// 为什么要把这个交出去：一组网格的三角形可能横跨好几个部位。
		/// 实测 <c>l4n silent_code_shinano信浓</c> —— 它的 26 个 bodypart 是**整套衣服**
		/// （素体 / bodyacc / 手套 / 袖套 / 高跟鞋…），没有"左右腿"这种 bodypart。
		/// 按"整组取主导区域"分，26 个组只落进 5 个槽，**右腿和右臂一个 Unit 都没有**，
		/// 上机就是"身体转换不出来"。有了这张表，装配阶段就能**按三角形**归位。
		/// </para>
		/// </summary>
		public BodyRegion[] VertexRegions { get; init; } = [];
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

		// bodypart 下标 → 变体数。多变体 = 可切换开关。
		//
		// 数是 **MDL 里声明的 model 个数**，空变体也算一个：这些包把"关掉"做成一个 0 顶点的空 model
		// （Crowbar 的 $bodygroup blank 约定），它本身就是"作者把这一块标成了可切换"的证据。
		// 所以这里不能只数"有几何的"——那会把配件降级成本体，等于把开关删掉。
		var variantsByBodyPart = new int[model.BodyParts.Count];
		var firstIndexOfName = new Dictionary<string, int>(StringComparer.Ordinal);
		for (var i = 0; i < model.BodyParts.Count; i++)
		{
			variantsByBodyPart[i] = model.BodyParts[i].Models.Count;
			firstIndexOfName.TryAdd(model.BodyParts[i].Name, i);
		}

		// 固定 bodypart 按部位收三角形；可切换 bodypart 各成一份。
		var bodyPartTriangles = new Dictionary<BodyRegion, List<int>>();
		foreach (var region in BodyRegions.All)
			bodyPartTriangles[region] = [];

		var partIndex = 0;
		foreach (var part in geometry.Parts)
		{
			// 下标优先用几何自带的那一份（空变体会让"Parts 下标"和"MDL 下标"错位，见 SourceGeometryPart）。
			var bodyPartIndex = part.BodyPartIndex >= 0 && part.BodyPartIndex < variantsByBodyPart.Length
				? part.BodyPartIndex
				: firstIndexOfName.GetValueOrDefault(part.BodyPart, -1);
			var variantCount = bodyPartIndex >= 0 ? variantsByBodyPart[bodyPartIndex] : 1;
			if (variantCount > 1)
			{
				var triangles = Range(part.TriangleStart, part.TriangleCount);
				components.Add((partIndex, new MeshGroup(
					part.BodyPart,
					MeshGroupKind.Component,
					DominantRegion(geometry, vertexRegion, triangles),
					bodyPartIndex,
					variantCount,
					triangles,
					CollectVertices(geometry, triangles))
				{
					SourceModelIndex = part.ModelIndex,
				}));
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
		return new Result(groups, diagnostics) { VertexRegions = vertexRegion };
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
	private static BodyRegion RegionOfTriangle(IReadOnlyList<BodyRegion> vertexRegion, SourceTriangle triangle)
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

	/// <summary>
	/// 把每个配件并进它<b>主导部位</b>的那份本体，得到"每个部位一份"的切分。
	/// <para>
	/// <b>为什么需要这条路。</b>配件各成一份 Unit 是最终形态，但它要求游戏侧能正确注册并开关
	/// 几十个 Unit；在那一层还没验证通过之前，把配件融进本体能让<b>模型是完整的</b>，
	/// 从而把"材质对不对"和"Unit 注册对不对"这两件事分开查——否则一件都测不了。
	/// </para>
	/// <para>
	/// 归属用的是 <see cref="MeshGroup.Region"/>，也就是配件<b>语义上</b>落在哪个部位
	/// （<see cref="DominantRegion"/> 按多数顶点推出来的）。这个字段本来就是为选槽位算的，
	/// 这里复用同一个判据，不另立一套。
	/// </para>
	/// <para>
	/// <b>两条硬规则（实测各踩过一次，两次的现象都是"整个身体不见了"）：</b>
	/// </para>
	/// <list type="number">
	/// <item><b>同一 bodypart 只取一个变体。</b>同 bodypart 的多个 model 在引擎里互斥
	/// （bodygroup 选项，同一时刻只画一个），全并进去等于把两套衣服叠着画。
	/// 取 <see cref="MeshGroup.SourceModelIndex"/> 最小的那个——就是 MDL 里的第 0 个，
	/// 也就是游戏默认显示的那个，不是我们挑的。</item>
	/// <item><b>没有本体就自己造一份，绝不丢。</b>身体本身也可能是可切换 bodypart
	/// （"胸大小""体型""衣服开/关"这类开关下挂着的就是整件身体）。这时该部位在固定 bodypart 里
	/// 一个三角形都没有，等不到"有本体可挂"；照旧丢掉的话，出来的 Unit 只剩头——
	/// 实测「L4N silent_code_shinano」就是这样：17 个 bodypart 里没有一个是固定的躯干，
	/// 于是 26 份里 25 份被当配件、全部丢掉，只剩 face + 发型。</item>
	/// </list>
	/// </summary>
	/// <returns>
	/// 合并后的每部位网格，以及"这次做了什么取舍 / 补了什么"的说明。
	/// 说明不是日志装饰：<b>被略过的变体与被补出来的部位都必须让人看得见</b>，
	/// 否则下一次又是"模型少了一半，产物看不出任何异常"。
	/// </returns>
	public static MergeResult MergeComponentsIntoBodyParts(
		SourceGeometry geometry,
		IReadOnlyList<MeshGroup> groups,
		IReadOnlyList<BodyRegion>? vertexRegions = null)
	{
		ArgumentNullException.ThrowIfNull(geometry);
		ArgumentNullException.ThrowIfNull(groups);

		var notes = new List<string>();
		var merged = new Dictionary<BodyRegion, List<int>>();

		// **按三角形归位，而不是按整组取主导区域。**
		//
		// 一组网格的三角形可以横跨好几个部位。旧实现让整组只落进它的"主导区域"，
		// 于是**衣服式模型**（bodypart 是素体/bodyacc/手套/袖套/高跟鞋…，没有"左右腿"）
		// 会只落进 5 个槽，右腿右臂一个 Unit 都没有 —— 上机就是"身体转换不出来"。
		//
		// `vertexRegions` 为 null 时退回旧行为（整组用一个区域），保证既有调用方与测试不变。
		void Place(IEnumerable<int> triangles, BodyRegion fallback)
		{
			foreach (var index in triangles)
			{
				if (index < 0 || index >= geometry.Triangles.Count)
					continue;
				var region = vertexRegions is { Count: > 0 }
					? RegionOfTriangle(vertexRegions, geometry.Triangles[index])
					: fallback;
				if (!merged.TryGetValue(region, out var list))
				{
					list = [];
					merged[region] = list;
				}

				list.Add(index);
			}
		}

		var hostRegions = new HashSet<BodyRegion>();
		foreach (var group in groups.Where(group => group.Kind == MeshGroupKind.BodyPart))
		{
			hostRegions.Add(group.Region);
			Place(group.TriangleIndices, group.Region);
		}

		var host = groups
			.Where(group => group.Kind == MeshGroupKind.BodyPart)
			.ToDictionary(group => group.Region);

		// ① 同一 bodypart 只取默认变体。
		//
		// 按 SourceModelIndex 排序后取第一个"有三角形"的：空变体（0 顶点）在 Split 里根本不会成为
		// 一个 group，所以走到这里的每个变体都自带几何；仍按三角形数兜一层，防止将来有人放宽那一条。
		var keptComponents = new List<MeshGroup>();
		var skippedByBodyPart = new List<(MeshGroup Chosen, int SkippedCount, int SkippedTriangles)>();
		foreach (var sameBodyPart in groups
			.Where(group => group.Kind == MeshGroupKind.Component)
			.GroupBy(group => group.SourceBodyPartIndex))
		{
			var ordered = sameBodyPart
				.OrderBy(group => group.SourceModelIndex)
				.ThenBy(group => group.Name, StringComparer.Ordinal)
				.ToArray();
			var chosen = ordered.FirstOrDefault(group => group.TriangleCount > 0) ?? ordered[0];
			keptComponents.Add(chosen);

			var dropped = ordered.Where(group => !ReferenceEquals(group, chosen) && group.TriangleCount > 0).ToArray();
			if (dropped.Length != 0)
				skippedByBodyPart.Add((chosen, dropped.Length, dropped.Sum(group => group.TriangleCount)));
		}

		// 说明合并成两句：一句"放弃了哪些变体"，一句"补出了哪些部位"。
		// 逐件各报一行会把通知刷成十几条，而能被采取的动作其实只有两句话那么多。
		if (skippedByBodyPart.Count != 0)
		{
			notes.Add(
				$"{skippedByBodyPart.Count} 个可切换部件只并入了默认变体（共放弃 {skippedByBodyPart.Sum(item => item.SkippedTriangles)} 个三角形）："
				+ string.Join("、", skippedByBodyPart.Select(item => item.Chosen.Name))
				+ "。同一 bodypart 的变体在源侧互斥（同时画出来会重影），一个 Unit 里只能放一个；"
				+ "要另一套变体就换变体重转。");
		}

		// ② 并进各自部位（按三角形归位，见上面的 Place）。
		foreach (var component in keptComponents)
			Place(component.TriangleIndices, component.Region);

		// "补出来的部位" = 收到了三角形、但源侧没有任何固定 bodypart 落在那一区域。
		var synthesized = merged.Keys
			.Where(region => !hostRegions.Contains(region) && merged[region].Count != 0)
			.OrderBy(region => region)
			.ToList();

		if (synthesized.Count != 0)
		{
			notes.Add(
				$"部位 {string.Join("、", synthesized.OrderBy(region => region).Select(region => region.Name()))} "
				+ "在源模型的固定 bodypart 里没有任何三角形（整块几何都挂在可切换部件里），已各自补出一份本体 Unit；"
				+ "不补的话这些部位的几何会被整段丢掉。");
		}

		// 输出顺序 = 部位顺序（槽位升序），补出来的部位也按这个顺序落位。
		var result = new List<MeshGroup>(merged.Count);
		foreach (var region in BodyRegions.All)
		{
			if (!merged.TryGetValue(region, out var triangles) || triangles.Count == 0)
				continue;

			triangles.Sort();
			var template = host.GetValueOrDefault(region);
			result.Add(template is null
				? new MeshGroup(
					region.Name(),
					MeshGroupKind.BodyPart,
					region,
					-1,
					1,
					triangles,
					CollectVertices(geometry, triangles))
				: template with
				{
					TriangleIndices = triangles,
					VertexIndices = CollectVertices(geometry, triangles),
				});
		}

		return new MergeResult(result, notes);
	}

	/// <summary><see cref="MergeComponentsIntoBodyParts"/> 的结果。</summary>
	/// <param name="Groups">每个部位一份，按槽位升序。</param>
	/// <param name="Notes">这次的取舍说明：略过的变体、补出来的部位。</param>
	public sealed record MergeResult(IReadOnlyList<MeshGroup> Groups, IReadOnlyList<string> Notes);
}
