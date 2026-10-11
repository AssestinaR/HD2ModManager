namespace HD2SourceModel;

/// <summary>
/// 把 MDL（结构）、VVD（顶点与权重）、VTX（索引）三者装配成一份可直接蒙皮的几何。
/// <para>
/// 装配的核心是那条<b>全局顶点号</b>公式：
/// <c>global = model.VertexOffset + mesh.VertexStart + VTX 里的原始顶点号</c>。
/// 少加任何一项都会让三角形指向错误的顶点——表现为网格"炸开成尖刺"，而不是报错。
/// </para>
/// <para>
/// 与 <see cref="SourceModelReader"/> 不同，这里遇到结构不一致会<b>抛异常</b>而不是记诊断：
/// 顶点错位的几何没有任何用处，继续产出一份"看起来完整"的网格只会把错误带到下游。
/// </para>
/// <para>
/// Assembles MDL + VVD + VTX into skinnable geometry.  The pivotal detail is the global vertex
/// index <c>model.VertexOffset + mesh.VertexStart + original</c>; dropping any term silently
/// points triangles at the wrong vertices ("explodes into spikes").  Unlike the MDL reader this
/// throws on structural mismatch, because a mis-indexed mesh is worthless and a plausible-looking
/// partial result would just carry the error downstream.
/// </para>
/// </summary>
public static class SourceGeometryBuilder
{
	/// <summary>
	/// 用 MDL 的结构 + VVD 的顶点 + VTX 的索引装配几何。
	/// </summary>
	/// <param name="model">MDL 解析结果（提供 bodypart / model / mesh 层级与顶点起点）。</param>
	/// <param name="vertices">VVD 的 LOD0 顶点（下标即全局顶点号）。</param>
	/// <param name="indexBodyParts">VTX 的索引树，顺序必须与 MDL 的 bodypart 一致。</param>
	public static SourceGeometry Build(
		SourceModelDocument model,
		IReadOnlyList<SourceVertex> vertices,
		IReadOnlyList<SourceIndexBodyPart> indexBodyParts)
	{
		ArgumentNullException.ThrowIfNull(model);
		ArgumentNullException.ThrowIfNull(vertices);
		ArgumentNullException.ThrowIfNull(indexBodyParts);

		var bodyParts = model.BodyParts;
		if (bodyParts.Count != indexBodyParts.Count)
		{
			throw new InvalidDataException(
				$"MDL 有 {bodyParts.Count} 个 bodypart，VTX 有 {indexBodyParts.Count} 个，无法配对。");
		}

		var outputVertices = new List<SourceVertex>();
		var triangles = new List<SourceTriangle>();
		var parts = new List<SourceGeometryPart>();

		for (var bodyPartIndex = 0; bodyPartIndex < bodyParts.Count; bodyPartIndex++)
		{
			var mdlPart = bodyParts[bodyPartIndex];
			var vtxPart = indexBodyParts[bodyPartIndex];
			if (mdlPart.Models.Count != vtxPart.Models.Count)
			{
				throw new InvalidDataException(
					$"bodypart {mdlPart.Name}：MDL 有 {mdlPart.Models.Count} 个 model，VTX 有 {vtxPart.Models.Count} 个。");
			}

			for (var modelIndex = 0; modelIndex < mdlPart.Models.Count; modelIndex++)
			{
				var mdlModel = mdlPart.Models[modelIndex];
				var vtxModel = vtxPart.Models[modelIndex];

				// 顶点数为 0 的 model 是"没被编译进来的变体"，直接跳过。
				if (mdlModel.VertexCount == 0)
					continue;

				if (vtxModel.Lods.Count == 0)
					throw new InvalidDataException($"model {mdlModel.Name} 在 VTX 里没有 LOD。");

				var lod0 = vtxModel.Lods[0];
				if (lod0.Meshes.Count != mdlModel.Meshes.Count)
				{
					throw new InvalidDataException(
						$"model {mdlModel.Name}：MDL 有 {mdlModel.Meshes.Count} 个 mesh，VTX LOD0 有 {lod0.Meshes.Count} 个。");
				}

				var partVertexStart = outputVertices.Count;
				var partTriangleStart = triangles.Count;

				for (var meshIndex = 0; meshIndex < mdlModel.Meshes.Count; meshIndex++)
				{
					var mesh = mdlModel.Meshes[meshIndex];
					foreach (var group in lod0.Meshes[meshIndex].Groups)
					{
						var baseIndex = outputVertices.Count;
						foreach (var original in group.OriginalVertices)
						{
							if (original >= mesh.VertexCount)
							{
								throw new InvalidDataException(
									$"VTX 原始顶点号 {original} 超出 mesh 的顶点数 {mesh.VertexCount}。");
							}

							var global = mdlModel.VertexOffset + mesh.VertexStart + original;
							if ((uint)global >= (uint)vertices.Count)
							{
								throw new InvalidDataException(
									$"全局顶点号 {global} 超出 VVD LOD0 的 {vertices.Count} 个顶点。");
							}

							outputVertices.Add(vertices[global]);
						}

						var indices = group.Indices;
						for (var slot = 0; slot + 2 < indices.Count; slot += 3)
						{
							triangles.Add(new SourceTriangle(
								baseIndex + indices[slot],
								baseIndex + indices[slot + 1],
								baseIndex + indices[slot + 2],
								mesh.MaterialIndex));
						}
					}
				}

				parts.Add(new SourceGeometryPart(
					mdlPart.Name,
					mdlModel.Name,
					partVertexStart,
					outputVertices.Count - partVertexStart,
					partTriangleStart,
					triangles.Count - partTriangleStart));
			}
		}

		return new SourceGeometry(outputVertices, triangles, parts);
	}
}
