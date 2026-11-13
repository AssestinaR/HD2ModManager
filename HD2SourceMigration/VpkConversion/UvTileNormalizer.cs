namespace HD2SourceMigration.VpkConversion;

/// <summary>
/// 把落在 0..1 之外"整格"上的三角形，按<b>整数</b>平移回 0..1 那一格。
/// <para>
/// <b>为什么要做。</b>Source 的贴图默认按 wrap 采样，所以作者常把镜像那一半的 UV 整体挪一格
/// （U 或 V 加 1）来避开重叠，源游戏里看不出任何区别。HD2 不吃这一套：实测某转换模型
/// 一侧袖子 4620 个面的 U 落在 1..2，游戏里整只袖子不显示，而同一个 Unit 里
/// U 在 0..1 的手套、袖口都正常；把 U 超出 0..1 的面标红，标红范围与游戏里消失的部分完全一致。
/// （同一模型头部有 7376 个面是 V 落在 1..2，游戏里脸是好的——所以经验上是 U 出格会消失，
/// V 出格不消失。机制未证实，这里两个轴一起归位：在 wrap 采样下平移整数格采样结果不变，
/// 归位后两种情况都不再依赖游戏怎么对待出格 UV。）
/// </para>
/// <para>
/// <b>按三角形平移，不按顶点。</b>平移量取三角形 UV 重心的整数部分：同一个三角形的三个顶点
/// 平移量相同，三角形内部的插值不变。一个顶点若被平移量不同的三角形共用，就为每种平移量
/// 各复制一份顶点（原下标留给第一次出现的那种）——否则只能二选一，另一侧的三角形会被拉开。
/// </para>
/// <para>
/// 跨格子的三角形（自身 UV 跨度超过 1，平铺贴图常见）平移后仍会有一部分在 0..1 之外，
/// 那属于贴图本身的平铺，不在这里处理。
/// </para>
/// </summary>
public static class UvTileNormalizer
{
	/// <summary>归位结果：新几何 + 被平移的三角形数 + 为此复制的顶点数。</summary>
	public readonly record struct Result(RetargetedMesh Mesh, int ShiftedTriangles, int DuplicatedVertices);

	public static Result Apply(RetargetedMesh mesh)
	{
		ArgumentNullException.ThrowIfNull(mesh);

		var vertices = new List<RetargetedVertex>(mesh.Vertices);
		// 每个原顶点：已经分配过的平移量 → 用哪个下标。
		var assigned = new Dictionary<(int Vertex, int Du, int Dv), int>();
		var claimed = new bool[mesh.Vertices.Count];
		var shiftedTriangles = 0;
		var duplicated = 0;

		var sections = new RetargetedSection[mesh.Sections.Count];
		for (var s = 0; s < mesh.Sections.Count; s++)
		{
			var source = mesh.Sections[s];
			var triangles = new (int A, int B, int C)[source.Triangles.Count];
			for (var t = 0; t < source.Triangles.Count; t++)
			{
				var (a, b, c) = source.Triangles[t];
				var va = mesh.Vertices[a];
				var vb = mesh.Vertices[b];
				var vc = mesh.Vertices[c];
				var du = -(int)MathF.Floor((va.U + vb.U + vc.U) / 3f);
				var dv = -(int)MathF.Floor((va.V + vb.V + vc.V) / 3f);
				if (du != 0 || dv != 0)
					shiftedTriangles++;

				triangles[t] = (Resolve(a, du, dv), Resolve(b, du, dv), Resolve(c, du, dv));
			}

			sections[s] = new RetargetedSection(source.SourceMaterialIndex, triangles);
		}

		if (shiftedTriangles == 0)
			return new Result(mesh, 0, 0);

		return new Result(new RetargetedMesh(vertices, sections), shiftedTriangles, duplicated);

		int Resolve(int vertex, int du, int dv)
		{
			if (assigned.TryGetValue((vertex, du, dv), out var existing))
				return existing;

			var original = mesh.Vertices[vertex];
			var moved = du == 0 && dv == 0 ? original : original with { U = original.U + du, V = original.V + dv };
			int index;
			if (!claimed[vertex])
			{
				// 第一次用到这个顶点：原地改，不复制。
				claimed[vertex] = true;
				vertices[vertex] = moved;
				index = vertex;
			}
			else
			{
				vertices.Add(moved);
				index = vertices.Count - 1;
				duplicated++;
			}

			assigned[(vertex, du, dv)] = index;
			return index;
		}
	}
}
