using HD2SourceModel;

namespace HD2SourceMigration.VpkConversion;

/// <summary>一条骨骼影响：HD2 骨的<b>名字哈希</b> + 权重。用哈希而不是下标，因为下标是每个 Unit 自己的调色板里的位置。</summary>
public readonly record struct RetargetedInfluence(uint BoneHash, float Weight);

/// <summary>
/// 一个已经重定向到 HD2 空间的顶点，带法线与 UV。
/// <para>
/// 权重在这里<b>已经按 HD2 骨合并过</b>：多个源骨常常解到同一根 HD2 骨（<c>dump_blender.py</c>
/// 里那步"duplicates must be summed before anything is written"），不合并会让同一根骨在
/// 四个槽里出现两次，游戏读到的是两倍权重。
/// </para>
/// </summary>
public sealed record RetargetedVertex(
	Vec3 Position,
	Vec3 Normal,
	float U,
	float V,
	IReadOnlyList<RetargetedInfluence> Influences);

/// <summary>
/// 一份几何里按<b>源材质</b>分好的一段。
/// <para>
/// 为什么必须按源材质分段、而不是把整份压成一个 section：Unit 的 section 是"这段三角形用哪个材质"
/// 的唯一载体，材质索引挂在 section 上。压成一个 section 就等于宣布"整份只用一个材质"，
/// 后面无论怎么分配材质槽都没有落脚点。源侧的归属是
/// <c>SourceTriangle.MaterialIndex</c>（<c>SourceGeometryBuilder.cs:113</c> 从
/// <c>studiohdr</c> 的 mesh 记录里带出来的），它是<b>数据</b>，不是猜的。
/// </para>
/// </summary>
/// <param name="SourceMaterialIndex">源模型的材质下标（索引进 <c>SourceModelDocument.MaterialPaths</c>）。</param>
/// <param name="Triangles">本段三角形，下标已压缩到本份几何的顶点表内。</param>
public sealed record RetargetedSection(int SourceMaterialIndex, IReadOnlyList<(int A, int B, int C)> Triangles);

/// <summary>一份待写成 Unit 的几何。顶点已经按本份压缩过（下标 0..N-1），三角形按源材质分段。</summary>
public sealed record RetargetedMesh(
	IReadOnlyList<RetargetedVertex> Vertices,
	IReadOnlyList<RetargetedSection> Sections)
{
	public int VertexCount => Vertices.Count;

	public int TriangleCount => Sections.Sum(section => section.Triangles.Count);

	public int SourceMaterialCount => Sections.Select(section => section.SourceMaterialIndex).Distinct().Count();
}
