using HD2SourceModel;

namespace HD2SourceMigration.VpkConversion;

/// <summary>
/// 裙摆"离体清空"：把裙类材质的顶点沿髋轴径向往外偏一点，让布从身体里出来。
/// <para>
/// <b>为什么必须在转换期做。</b>2026-11-09 的实机 + 离线证据把这条路钉死了：
/// 裙子上沿 <b>64% 的顶点刚性蒙在 <c>hips</c> 上</b>（Hip 单元 s1：10520 / 16512 个顶点引用），
/// 链骨只带得动另外 36%；把链根转到 60°、把同样旋转逐节叠满，画面都毫无变化——
/// 因为那片布和臀部是**同一个刚体**，相对位置永远不变。运行时没有任何杠杆能推动它。
/// 而绑定姿态下它就已经陷进身体 0~1.5 cm（离线量出来的），所以这只能是**几何问题**。
/// </para>
/// <para>
/// <b>为什么按材质名挑段。</b><c>RetargetedSection</c> 只带源材质下标，而"哪一段是裙子"
/// 这个信息源侧就有（<c>mat_ornament_metal_skirt</c> 之类），是数据不是猜的。
/// 按名字含 <c>skirt</c> 命中，别的衣服、别的 Mod 一个顶点都不动。
/// </para>
/// <para>
/// <b>为什么只动 X/Y。</b>HD2 是 Z 向上，髋轴竖直，径向偏移本来就该在水平面上；
/// 纵向（沿裤腿往下）偏会变成"裙摆变长"，观感更糟。
/// </para>
/// </summary>
public static class SkirtClearance
{
	/// <summary>源材质名里出现这个词就当成裙摆。</summary>
	public const string MaterialMarker = "skirt";

	/// <summary>腰线附近满额偏移；往下到这么多米内线性渐隐到 0（裙摆下摆不动）。</summary>
	public const double DefaultFalloffHeight = 0.25;

	/// <summary>默认偏移量（米）。实测上沿最深陷 1.54 cm，2 cm 留出余量。</summary>
	public const double DefaultAmount = 0.02;

	/// <summary>
	/// 返回一个把裙类顶点外推后的网格。<paramref name="amount"/> ≤ 0 或没有裙类段时**原样返回**
	/// （同一个实例），所以默认路径零成本、零字节变化。
	/// </summary>
	/// <param name="mesh">重定向后的网格（HD2 空间，Z 向上）。</param>
	/// <param name="materialPaths">源材质下标 → 材质路径（<c>SourceModelDocument.MaterialPaths</c>）。</param>
	/// <param name="pelvis">髋部中心（HD2 空间）；径向偏移的圆心。</param>
	/// <param name="amount">满额偏移（米）。</param>
	/// <param name="falloffHeight">渐隐高度（米）。</param>
	public static RetargetedMesh Apply(
		RetargetedMesh mesh,
		IReadOnlyList<string>? materialPaths,
		Vec3 pelvis,
		double amount,
		double falloffHeight = DefaultFalloffHeight)
	{
		ArgumentNullException.ThrowIfNull(mesh);
		if (amount <= 0 || materialPaths is null || falloffHeight <= 0)
			return mesh;

		var skirt = new bool[mesh.Sections.Count];
		var any = false;
		for (var index = 0; index < mesh.Sections.Count; index++)
		{
			var materialIndex = mesh.Sections[index].SourceMaterialIndex;
			if (materialIndex < 0 || materialIndex >= materialPaths.Count) continue;
			var path = materialPaths[materialIndex];
			if (path is not null && path.Contains(MaterialMarker, StringComparison.OrdinalIgnoreCase))
			{
				skirt[index] = true;
				any = true;
			}
		}
		if (!any)
			return mesh;

		// 收集裙类段用到的顶点（同一顶点被多段引用只处理一次）
		var touched = new bool[mesh.VertexCount];
		var count = 0;
		for (var index = 0; index < mesh.Sections.Count; index++)
		{
			if (!skirt[index]) continue;
			foreach (var (a, b, c) in mesh.Sections[index].Triangles)
			{
				foreach (var vertex in (ReadOnlySpan<int>)[a, b, c])
				{
					if (vertex < 0 || vertex >= touched.Length || touched[vertex]) continue;
					touched[vertex] = true;
					count++;
				}
			}
		}
		if (count == 0)
			return mesh;

		var vertices = new RetargetedVertex[mesh.VertexCount];
		for (var index = 0; index < mesh.VertexCount; index++)
		{
			var vertex = mesh.Vertices[index];
			if (!touched[index])
			{
				vertices[index] = vertex;
				continue;
			}

			var dx = vertex.Position.X - pelvis.X;
			var dy = vertex.Position.Y - pelvis.Y;
			var radius = Math.Sqrt((dx * dx) + (dy * dy));
			if (radius < 1e-6)
			{
				vertices[index] = vertex;
				continue;
			}

			// 高度权重：腰线附近满额，往下 falloffHeight 内线性到 0。
			var below = pelvis.Z - vertex.Position.Z;
			var factor = below <= 0 ? 1.0 : 1.0 - (below / falloffHeight);
			if (factor <= 0)
			{
				vertices[index] = vertex;
				continue;
			}

			var shift = amount * factor;
			vertices[index] = vertex with
			{
				Position = new Vec3(
					vertex.Position.X + (dx / radius) * shift,
					vertex.Position.Y + (dy / radius) * shift,
					vertex.Position.Z),
			};
		}

		return mesh with { Vertices = vertices };
	}
}
