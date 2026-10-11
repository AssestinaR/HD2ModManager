namespace HD2SourceModel;

/// <summary>jiggle 骨按父骨分组后的计数——"这些会晃的东西挂在谁身上"。</summary>
public sealed record SourceJiggleParent(string ParentName, int Count);

/// <summary>装配后的一块几何。</summary>
public sealed record SourcePartSummary(string BodyPart, string Model, int VertexCount, int TriangleCount);

/// <summary>
/// 一次源模型检查的全部结果。字段刻意选成"能直接拿去做判断"的量：
/// jiggle 骨的父骨分布决定额外骨挂在哪、bodypart 名决定哪一块是头发/裙摆、装配计数则验证整条链路读对了。
/// </summary>
public sealed record SourceModelInspection(
	string VpkPath,
	string ModelPath,
	string VertexPath,
	string IndexPath,
	int Version,
	string ModelName,
	int Checksum,
	int BoneCount,
	int NamedBoneCount,
	int JiggleBoneCount,
	IReadOnlyList<SourceJiggleParent> JiggleParents,
	int BodyPartCount,
	int MaterialCount,
	int Lod0VertexCount,
	int GeometryVertexCount,
	int TriangleCount,
	bool UsedV7IndexStride,
	IReadOnlyList<SourcePartSummary> Parts,
	IReadOnlyList<SourceModelDiagnostic> Diagnostics);

/// <summary>
/// 读一个源模型并产出一份"人看得懂、机器也看得懂"的检查报告。
/// <para>
/// 这是"从零读求生之路 2 模型"这条链路的验收工具：把 VPK → MDL → VVD → VTX 一路读通，
/// 并把结果与逐块计数摊开，任何一环读错都会在数字上暴露出来。
/// </para>
/// </summary>
public static class SourceModelInspector
{
	/// <summary>打开一个 VPK 并检查其中一个模型。</summary>
	/// <param name="vpkPath">VPK 文件路径。</param>
	/// <param name="modelStem">模型名片段；不给则取体积最大的 .mdl。</param>
	public static SourceModelInspection Inspect(string vpkPath, string? modelStem = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(vpkPath);
		using var archive = VpkArchive.Open(vpkPath);
		return Inspect(archive, Path.GetFullPath(vpkPath), modelStem);
	}

	/// <summary>检查一个已经打开的 VPK 里的模型（便于一次打开、多次检查）。</summary>
	public static SourceModelInspection Inspect(VpkArchive archive, string vpkPath, string? modelStem = null)
	{
		ArgumentNullException.ThrowIfNull(archive);
		var files = SourceModelLocator.Resolve(archive, modelStem);

		var model = SourceModelReader.Read(archive.Read(files.Mdl));
		var vertices = SourceVertexReader.Read(archive.Read(files.Vvd), out _, out var lod0VertexCount);
		var indexBodyParts = SourceIndexReader.Read(archive.Read(files.Vtx), out _, out var usedV7);
		var geometry = SourceGeometryBuilder.Build(model, vertices, indexBodyParts);

		var jiggleParents = model.JiggleBones
			.GroupBy(bone => ParentName(model, bone))
			.Select(group => new SourceJiggleParent(group.Key, group.Count()))
			.OrderByDescending(item => item.Count)
			.ThenBy(item => item.ParentName, StringComparer.Ordinal)
			.ToArray();

		return new SourceModelInspection(
			vpkPath,
			files.Mdl.Path,
			files.Vvd.Path,
			files.Vtx.Path,
			model.Version,
			model.Name,
			model.Checksum,
			model.Bones.Count,
			model.Bones.Count(bone => bone.HasName),
			jiggleParents.Sum(item => item.Count),
			jiggleParents,
			model.BodyParts.Count,
			model.MaterialPaths.Count,
			lod0VertexCount,
			geometry.Vertices.Count,
			geometry.Triangles.Count,
			usedV7,
			[.. geometry.Parts.Select(part =>
				new SourcePartSummary(part.BodyPart, part.Model, part.VertexCount, part.TriangleCount))],
			model.Diagnostics);
	}

	/// <summary>父骨名；没有父骨（根骨）或下标越界时给一个稳定的占位。</summary>
	private static string ParentName(SourceModelDocument model, SourceBone bone) =>
		bone.ParentIndex >= 0 && bone.ParentIndex < model.Bones.Count
			? model.Bones[bone.ParentIndex].DisplayName
			: "<root>";
}
