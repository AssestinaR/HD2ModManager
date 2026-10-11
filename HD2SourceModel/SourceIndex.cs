namespace HD2SourceModel;

/// <summary>
/// VTX 里一个 strip group 的索引数据。
/// <para>
/// <paramref name="OriginalVertices"/> 是"原始顶点号"表：VTX 顶点本身不带位置，只带一个指回
/// 所属 mesh 的下标，真正的全局顶点号要再叠加 model 与 mesh 的起点
/// （<c>model.VertexOffset + mesh.VertexStart + 原始顶点号</c>）。
/// </para>
/// <para>
/// <paramref name="Indices"/> 是三角形索引表，每 3 个构成一个三角形，值指向
/// <paramref name="OriginalVertices"/> 的下标。实测文件里这一层已经是三角形列表，
/// 不需要再按 strip 解码。
/// </para>
/// </summary>
/// <param name="OriginalVertices">每个 VTX 顶点指回的原始顶点号。</param>
/// <param name="Indices">三角形索引表（每 3 个一组）。</param>
public sealed record SourceIndexGroup(
	IReadOnlyList<ushort> OriginalVertices,
	IReadOnlyList<ushort> Indices);

/// <summary>VTX 里一个 mesh 的 strip group 集合。</summary>
public sealed record SourceIndexMesh(IReadOnlyList<SourceIndexGroup> Groups);

/// <summary>VTX 里一个 LOD 的 mesh 集合；只有 LOD0 参与几何装配。</summary>
public sealed record SourceIndexLod(IReadOnlyList<SourceIndexMesh> Meshes);

/// <summary>VTX 里一个 model 的 LOD 集合。</summary>
public sealed record SourceIndexModel(IReadOnlyList<SourceIndexLod> Lods);

/// <summary>VTX 里一个 bodypart 的 model 集合。层级与 MDL 的 bodypart/model/mesh 一一对应。</summary>
public sealed record SourceIndexBodyPart(IReadOnlyList<SourceIndexModel> Models);
