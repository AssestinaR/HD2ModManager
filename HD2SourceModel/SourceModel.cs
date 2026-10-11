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
	SourceJiggleBone? Jiggle)
{
	public bool HasName => !string.IsNullOrEmpty(Name);

	/// <summary>
	/// 没有可读名字时的占位名。名字读不出来的骨（头发/裙摆/大袖子的 helper）数量很大，
	/// 需要一个稳定、与下标一一对应的称呼，日志和映射报告才读得下去。
	/// </summary>
	public string DisplayName => HasName ? Name! : $"source_extra_{Index}";

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
	int TriangleCount);

/// <summary>把 MDL + VVD + VTX 三者装配起来的最终几何。</summary>
public sealed record SourceGeometry(
	IReadOnlyList<SourceVertex> Vertices,
	IReadOnlyList<SourceTriangle> Triangles,
	IReadOnlyList<SourceGeometryPart> Parts);

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
}
