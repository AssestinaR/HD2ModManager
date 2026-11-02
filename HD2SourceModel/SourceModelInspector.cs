namespace HD2SourceModel;

/// <summary>jiggle 骨按父骨分组后的计数——"这些会晃的东西挂在谁身上"。</summary>
public sealed record SourceJiggleParent(string ParentName, int Count);

/// <summary>
/// <c>mstudiojigglebone_t.flags</c> 的位含义（源引擎的 <c>STUDIO_JIGGLE_*</c>）。
/// <para>
/// 为什么要把位拆成名字：报告是给人看的，而"0x31"里到底约束了什么必须一眼可见；
/// 测试也需要能直接断言"这几根是 angle+length 约束的"。
/// </para>
/// </summary>
public static class SourceJiggleFlags
{
	public const int Flexible = 0x01;
	public const int Rigid = 0x02;
	public const int YawConstrained = 0x04;
	public const int PitchConstrained = 0x08;
	public const int AngleConstrained = 0x10;
	public const int LengthConstrained = 0x20;
	public const int BaseSpring = 0x40;

	private static readonly (int Bit, string Name)[] Bits =
	[
		(Flexible, "flexible"),
		(Rigid, "rigid"),
		(YawConstrained, "yawConstrained"),
		(PitchConstrained, "pitchConstrained"),
		(AngleConstrained, "angleConstrained"),
		(LengthConstrained, "lengthConstrained"),
		(BaseSpring, "baseSpring"),
	];

	/// <summary>把 flags 拆成稳定的名字数组；未知位保持沉默（不编造含义）。</summary>
	public static IReadOnlyList<string> Describe(int flags) =>
		[.. Bits.Where(bit => (flags & bit.Bit) != 0).Select(bit => bit.Name)];
}

/// <summary>
/// 一根 jiggle 骨的全部事实：编号、名字、父骨、世界 bind 位置，以及 29 个弹簧参数。
/// <para>
/// 这是"从源模型搬到 HD2"真正要带走的那份数据——后面写进 <c>HD2PBX01</c> 容器的就是它。
/// Bin 位置给的是<b>世界 bind 平移</b>，运行时用它把新骨放到同一处，不需要再回读 mdl。
/// </para>
/// <para>
/// Everything the runtime needs about one spring bone: identity, parent, world-bind position,
/// and the full parameter block. This record is what the embedded container carries.
/// </para>
/// </summary>
public sealed record SourceJiggleBoneFact(
	int BoneIndex,
	string BoneName,
	int ParentBoneIndex,
	string ParentName,
	int Flags,
	IReadOnlyList<string> FlagNames,
	Vec3 WorldPosition,
	SourceJiggleBone Parameters);

/// <summary>装配后的一块几何。</summary>
public sealed record SourcePartSummary(string BodyPart, string Model, int VertexCount, int TriangleCount);

/// <summary>
/// 一根 jiggle 骨在 LOD0 上带多少顶点。
/// <para>
/// 为什么需要这个数：完整版要把每根 jiggle 骨插成独立节点，并把这些顶点从"被压平的父骨"
/// 改指到新节点上。若某根骨**一个顶点都没带**（VertexCount = 0），给它插节点纯属浪费，
/// 也说明转换链路读错了 —— 这正是准入检查里的 WEIGHT_EMPTY。
/// </para>
/// <para>
/// DominantCount 是"该骨权重最大"的顶点数，比 VertexCount 更能说明"这块几何归谁"：
/// 一根骨可能只以极小权重参与大量顶点，那种情况下它不该独立驱动那些顶点。
/// </para>
/// </summary>
public sealed record SourceJiggleWeightFact(string BoneName, int VertexCount, int DominantCount);

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
	IReadOnlyList<SourceJiggleBoneFact> JiggleBones,
	int BodyPartCount,
	int MaterialCount,
	int Lod0VertexCount,
	int GeometryVertexCount,
	int TriangleCount,
	bool UsedV7IndexStride,
	IReadOnlyList<SourcePartSummary> Parts,
	IReadOnlyList<SourceJiggleWeightFact> JiggleWeights,
	IReadOnlyList<SourceModelDiagnostic> Diagnostics)
{
	/// <summary>
	/// mdl 声明的材质目录（<c>cdtextures</c>，有序）。**这是"模型从哪些目录找材质"的权威答案**，
	/// 引擎按顺序找 <c>materials/&lt;目录&gt;/&lt;名字&gt;.vmt</c>，第一个存在的胜出。
	/// </summary>
	public IReadOnlyList<string> MaterialFolders { get; init; } = [];

	/// <summary>每个材质名 × 每个目录展开出的候选全路径（<see cref="SourceMaterialRef.Path"/>）。</summary>
	public IReadOnlyList<string> MaterialRefs { get; init; } = [];
}

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

		// 逐根 jiggle 骨的完整事实。按骨下标排序，保证输出稳定（便于 diff 与测试）。
		var jiggleBones = JiggleFacts(model);

		// 逐根 jiggle 骨统计它在 LOD0 上带了多少顶点（完整版插节点/改权重的依据）。
		// 口径：
		//   VertexCount   —— 该骨出现在任意一条 influence 里且权重 > 0；
		//   DominantCount —— 该骨是该顶点**权重最大**的那一根（这才是"这块几何归它"）。
		var jiggleWeights = CountJiggleWeights(geometry, jiggleBones);

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
			jiggleBones,
			model.BodyParts.Count,
			model.MaterialPaths.Count,
			lod0VertexCount,
			geometry.Vertices.Count,
			geometry.Triangles.Count,
			usedV7,
			[.. geometry.Parts.Select(part =>
				new SourcePartSummary(part.BodyPart, part.Model, part.VertexCount, part.TriangleCount))],
			jiggleWeights,
			model.Diagnostics)
		{
			MaterialFolders = model.MaterialFolders,
			MaterialRefs = model.MaterialRefs.Select(reference => reference.Path).ToArray(),
		};
	}

	/// <summary>
	/// 逐根 jiggle 骨的完整事实（按骨下标排序，输出稳定便于 diff 与测试）。
	/// <para>
	/// 抽出来是因为有<b>两个</b>调用方：检查报告（本类）和转换器（<c>vpk2patch</c> 要拿它
	/// 连同"链根锚点名"一起写出物理参数容器）。两处各写一份的话，"转换时算的锚点"和
	/// "嵌入时写的参数"会慢慢漂移，而漂移的表现是参数对不上某根骨——完全静默。
	/// </para>
	/// </summary>
	public static IReadOnlyList<SourceJiggleBoneFact> JiggleFacts(SourceModelDocument model)
	{
		ArgumentNullException.ThrowIfNull(model);
		return model.JiggleBones
			.OrderBy(bone => bone.Index)
			.Select(bone => new SourceJiggleBoneFact(
				bone.Index,
				bone.DisplayName,
				bone.ParentIndex,
				ParentName(model, bone),
				bone.Jiggle!.Flags,
				SourceJiggleFlags.Describe(bone.Jiggle!.Flags),
				new Vec3(bone.WorldBind.M14, bone.WorldBind.M24, bone.WorldBind.M34),
				bone.Jiggle!))
			.ToArray();
	}

	/// <summary>统计每根 jiggle 骨在装配后几何上带多少顶点（口径见 <see cref="SourceJiggleWeightFact"/>）。</summary>
	private static SourceJiggleWeightFact[] CountJiggleWeights(
		SourceGeometry geometry,
		IReadOnlyList<SourceJiggleBoneFact> jiggleBones)
	{
		var anyInfluence = new Dictionary<int, int>();
		var dominant = new Dictionary<int, int>();
		foreach (var vertex in geometry.Vertices)
		{
			var bestBone = -1;
			var bestWeight = 0f;
			foreach (var influence in vertex.Influences())
			{
				anyInfluence[influence.BoneIndex] = anyInfluence.GetValueOrDefault(influence.BoneIndex) + 1;
				if (influence.Weight > bestWeight)
				{
					bestWeight = influence.Weight;
					bestBone = influence.BoneIndex;
				}
			}
			if (bestBone >= 0)
			{
				dominant[bestBone] = dominant.GetValueOrDefault(bestBone) + 1;
			}
		}

		return jiggleBones
			.Select(bone => new SourceJiggleWeightFact(
				bone.BoneName,
				anyInfluence.GetValueOrDefault(bone.BoneIndex),
				dominant.GetValueOrDefault(bone.BoneIndex)))
			.ToArray();
	}

	/// <summary>父骨名；没有父骨（根骨）或下标越界时给一个稳定的占位。</summary>
	private static string ParentName(SourceModelDocument model, SourceBone bone) =>
		bone.ParentIndex >= 0 && bone.ParentIndex < model.Bones.Count
			? model.Bones[bone.ParentIndex].DisplayName
			: "<root>";
}
