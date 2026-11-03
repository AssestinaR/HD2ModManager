namespace HD2SourceModel;

/// <summary>解析源模型时发现的一处问题。读模型应当"能读多少读多少"，所以问题走诊断而不是异常。</summary>
/// <param name="Code">稳定的机器可读标识，便于测试与日志过滤。</param>
/// <param name="Message">给人看的中文说明。</param>
public sealed record SourceModelDiagnostic(string Code, string Message);

/// <summary>
/// jigglebone（源引擎的"弹簧骨"）参数，来自 <c>mstudiojigglebone_t</c>（120 字节）。
/// <para>
/// 这些参数就是"头发 / 裙摆 / 大袖子该怎么晃"的原始意图，是本次迁移里唯一不需要猜的东西：
/// 源模型自己写好了。运行时侧要复现的就是它们。
/// </para>
/// <para>
/// The spring parameters Source itself authored for a bone (hair, skirt, wide sleeves).  They are
/// the one part of the migration that needs no guessing, and they are what the runtime side
/// eventually has to reproduce.
/// </para>
/// </summary>
public sealed record SourceJiggleBone(
	int Flags,
	float Length,
	float TipMass,
	float YawStiffness,
	float YawDamping,
	float PitchStiffness,
	float PitchDamping,
	float AlongStiffness,
	float AlongDamping,
	float AngleLimit,
	float MinYaw,
	float MaxYaw,
	float YawFriction,
	float YawBounce,
	float MinPitch,
	float MaxPitch,
	float PitchFriction,
	float PitchBounce,
	float BaseMass,
	float BaseStiffness,
	float BaseDamping,
	float BaseMinLeft,
	float BaseMaxLeft,
	float BaseLeftFriction,
	float BaseMinUp,
	float BaseMaxUp,
	float BaseUpFriction,
	float BaseMinForward,
	float BaseMaxForward,
	float BaseForwardFriction)
{
	public const int Flexible = 0x01;
	public const int Rigid = 0x02;
	public const int YawConstrained = 0x04;
	public const int PitchConstrained = 0x08;
	public const int AngleConstrained = 0x10;
	public const int LengthConstrained = 0x20;
	public const int BaseSpring = 0x40;

	public bool IsFlexible => (Flags & Flexible) != 0;
	public bool IsRigid => (Flags & Rigid) != 0;
	public bool HasYawConstraint => (Flags & YawConstrained) != 0;
	public bool HasPitchConstraint => (Flags & PitchConstrained) != 0;
	public bool HasAngleConstraint => (Flags & AngleConstrained) != 0;
	public bool HasLengthConstraint => (Flags & LengthConstrained) != 0;
	public bool HasBaseSpring => (Flags & BaseSpring) != 0;

	/// <summary>给日志用的旗标说明，例如 <c>flexible,yaw_con,pitch_con</c>。</summary>
	public string DescribeFlags()
	{
		var names = new List<string>(7);
		if (IsFlexible) names.Add("flexible");
		if (IsRigid) names.Add("rigid");
		if (HasYawConstraint) names.Add("yaw_con");
		if (HasPitchConstraint) names.Add("pitch_con");
		if (HasAngleConstraint) names.Add("angle_con");
		if (HasLengthConstraint) names.Add("length_con");
		if (HasBaseSpring) names.Add("base_spring");
		return names.Count == 0 ? "0" : string.Join(',', names);
	}
}

/// <summary>源模型里的一根骨骼。</summary>
/// <param name="Index">骨骼数组下标；顶点权重引用的就是这个值。</param>
/// <param name="Name">
/// 骨骼名；为 <c>null</c> 表示读不出来——这些模型的 <c>sznameindex</c> 指向内嵌的 SMD 文本而不是
/// 名字表，所以头发/裙摆/大袖子的 helper 骨普遍没有名字。父级下标仍然有效。
/// </param>
/// <param name="ParentIndex">父骨骼下标；<c>-1</c> 表示根。</param>
/// <param name="PoseToBone">bind 姿态矩阵，原样载入（列向量约定）。</param>
/// <param name="WorldBind">世界 bind 矩阵，即 <c>PoseToBone</c> 的逆。</param>
public sealed record SourceBone(
	int Index,
	string? Name,
	int ParentIndex,
	Mat4 PoseToBone,
	Mat4 WorldBind,
	SourceJiggleBone? Jiggle,
	string? Namespace = null)
{
	public bool HasName => !string.IsNullOrEmpty(Name);

	/// <summary>
	/// 没有可读名字时的占位名。名字读不出来的骨（头发/裙摆/大袖子的 helper）数量很大，
	/// 需要一个稳定、与下标一一对应的称呼，日志和映射报告才读得下去。
	/// </summary>
	/// <remarks>
	/// <b>为什么占位名必须带命名空间。</b>占位名是 <c>source_extra_&lt;源骨下标&gt;</c>，纯下标派生，
	/// 于是<b>不同模型上同一个下标必然重名</b> —— 实测两套护甲模型的 jiggle 骨有 50/56 个名字相同。
	/// 这些名字会作为节点名写进 Unit，运行时靠"名字能对上几根"判断哪份物理参数属于这套护甲；
	/// 一旦重名，运行时就会拿别的模型的参数与绑定方向去驱动，表现是<b>头发高高翘起</b>。
	/// 加按来源模型派生的命名空间后，两个模型的占位名不再相同（与材质的 <c>idNamespace</c> 同一思路）。
	/// <para>
	/// ⚠️ <b>只给占位名加前缀，有真名的骨保持原样。</b>父骨名要拿去查
	/// <c>M.anchor_name</c> 的源→HD2 映射表（<c>ValveBiped.Bip01_Head1</c> → <c>head</c>），
	/// 给有名字的父骨加前缀会让映射全部落空，整条链因"缺锚点"被丢掉。
	/// </para>
	/// </remarks>
	public string DisplayName => HasName
		? Name!
		: (string.IsNullOrEmpty(Namespace) ? $"source_extra_{Index}" : $"{Namespace}/source_extra_{Index}");

	/// <summary>骨骼局部 +X 轴在世界空间里的方向。两个引擎都把骨骼放在局部 +X 上。</summary>
	public Vec3 WorldAxis => new Vec3(WorldBind.M11, WorldBind.M21, WorldBind.M31).Normalized();

	/// <summary>骨骼原点在世界空间里的位置。</summary>
	public Vec3 WorldOrigin => WorldBind.Origin;
}

/// <summary>MDL 里 <c>mstudiomesh_t</c> 的一层。</summary>
/// <param name="VertexStart">该 mesh 在所属 model 顶点数组里的起点。</param>
public sealed record SourceMesh(int MaterialIndex, int VertexCount, int VertexStart);

/// <summary>MDL 里 <c>mstudiomodel_t</c> 的一层。</summary>
/// <param name="VertexOffset">
/// 该 model 的顶点在 VVD 全局顶点数组里的起点，等于 <c>vertexindex / 48</c>。
/// <b>漏掉这一项会把网格炸成尖刺</b>：全局下标是
/// <c>VertexOffset + mesh.VertexStart + VTX 里的原始顶点号</c>。
/// </param>
public sealed record SourceBodyPartModel(
	string Name,
	int VertexCount,
	int VertexOffset,
	IReadOnlyList<SourceMesh> Meshes);

/// <summary>MDL 里 <c>mstudiobodyparts_t</c> 的一层（头、躯干、裙子、鞋子……）。</summary>
public sealed record SourceBodyPart(string Name, IReadOnlyList<SourceBodyPartModel> Models);

/// <summary>一组蒙皮权重。</summary>
public readonly record struct SourceVertexWeight(int BoneIndex, float Weight);

/// <summary>
/// 一个蒙皮顶点。Source 的硬上限是每顶点 3 组权重。
/// <para>权重保持文件里的原值，没有归一化——归一化留给实际蒙皮的那一步做，读到的就是文件里写的。</para>
/// </summary>
public readonly record struct SourceVertex(
	Vec3 Position,
	Vec3 Normal,
	float U,
	float V,
	SourceVertexWeight Weight0,
	SourceVertexWeight Weight1,
	SourceVertexWeight Weight2)
{
	public const int MaxInfluences = 3;

	/// <summary>依次产出权重为正的骨骼影响。</summary>
	public IEnumerable<SourceVertexWeight> Influences()
	{
		if (Weight0.Weight > 0) yield return Weight0;
		if (Weight1.Weight > 0) yield return Weight1;
		if (Weight2.Weight > 0) yield return Weight2;
	}
}

/// <summary>一个三角形；下标指向几何数组里已装配好的顶点。</summary>
public readonly record struct SourceTriangle(int A, int B, int C, int MaterialIndex);

/// <summary>装配好的几何里的一段，用来报告"哪一块来自哪个 bodypart"。</summary>
public sealed record SourceGeometryPart(
	string BodyPart,
	string Model,
	int VertexStart,
	int VertexCount,
	int TriangleStart,
	int TriangleCount)
{
	/// <summary>它在 MDL 里的 bodypart 下标；未知时为 -1。</summary>
	/// <remarks>
	/// <b>为什么必须记下来而不是靠顺序推。</b>顶点数为 0 的空变体不会进 <see cref="SourceGeometry.Parts"/>
	/// （引擎不画它们），所以 <c>Parts</c> 的下标和 MDL 的"第几个 bodypart / 第几个 model"是两套编号。
	/// 靠顺序对齐就等于假设"空的变体永远在最后"——那份假设一旦破了，切分会把配件挂到别的部位上，
	/// 而现象只是"某件衣服跑到腿上了"，看不出来是编号错位。
	/// </remarks>
	public int BodyPartIndex { get; init; } = -1;

	/// <summary>它在所属 bodypart 里的 model 下标；未知时为 -1。同一 bodypart 的多个 model 互斥（bodygroup 选项）。</summary>
	public int ModelIndex { get; init; } = -1;
}

/// <summary>把 MDL + VVD + VTX 三者装配起来的最终几何。</summary>
public sealed record SourceGeometry(
	IReadOnlyList<SourceVertex> Vertices,
	IReadOnlyList<SourceTriangle> Triangles,
	IReadOnlyList<SourceGeometryPart> Parts);

/// <summary>
/// 一条"模型需要的材质"：名字 + 它在 mdl 材质目录表里的那个目录。
/// <para>
/// <b>为什么需要目录。</b>同一个名字可以在 <c>…/P2/x.vmt</c> 和 <c>…/P3/x.vmt</c> 各有一份、
/// 内容不同；只按名字找等于把两者当成同一个材质。引擎的规则是
/// <c>materials/&lt;目录&gt;/&lt;名字&gt;.vmt</c>，按目录表的**顺序**找第一个存在的——
/// 所以"候选全路径"才是可判定的事实。
/// </para>
/// </summary>
/// <param name="Name">材质名（mdl <c>mtextures</c> 里的那一项，不含目录、不含扩展名）。</param>
/// <param name="Folder">材质目录（如 <c>diyu2026/GF2Exilium/Cheyanne/MaidenDebut/P2</c>）；空 = 模型没声明目录表。</param>
/// <param name="Priority">目录在表里的下标：数值小的优先（引擎按顺序试）。</param>
public sealed record SourceMaterialRef(string Name, string Folder, int Priority)
{
	/// <summary>引擎实际会去找的那个 VMT 路径（不含 <c>materials/</c> 前缀、不含扩展名）。</summary>
	public string Path
		=> Folder.Length == 0 ? Name : Folder + "/" + Name;
}

/// <summary>
/// MDL 文件的解析结果（骨架 + bodypart 结构 + 材质路径），还不含顶点数据。
/// </summary>
public sealed record SourceModelDocument(
	int Version,
	string Name,
	int Checksum,
	IReadOnlyList<SourceBone> Bones,
	IReadOnlyList<SourceBodyPart> BodyParts,
	IReadOnlyList<string> MaterialPaths,
	IReadOnlyList<SourceModelDiagnostic> Diagnostics)
{
	public IEnumerable<SourceBone> JiggleBones => Bones.Where(bone => bone.Jiggle is not null);

	/// <summary>
	/// mdl 头部声明的材质目录（<c>cdtextures</c>），**有序**：引擎按这个顺序解析材质名。
	/// 空 = 老模型没有这张表。
	/// </summary>
	public IReadOnlyList<string> MaterialFolders { get; init; } = [];

	/// <summary>
	/// 每个材质名 × 每个材质目录展开出的候选全路径（见 <see cref="SourceMaterialRef"/>）。
	/// <b>"模型需要哪些材质"以这份为准</b>；<see cref="MaterialPaths"/> 只是为了兼容只认识名字的老调用方。
	/// </summary>
	public IReadOnlyList<SourceMaterialRef> MaterialRefs { get; init; } = [];

	/// <summary>按名字取它的候选全路径（同名不同目录时会有多条，按 <c>Priority</c> 升序）。</summary>
	public IEnumerable<SourceMaterialRef> RefsFor(string materialName)
		=> MaterialRefs.Where(reference => string.Equals(reference.Name, materialName, StringComparison.OrdinalIgnoreCase));
}
