using System.Globalization;
using HD2SourceModel;

namespace HD2SourceMigration;

/// <summary>一根源骨映射到一根 HD2 节点的结果。</summary>
/// <param name="SourceBone">源骨短名（已剥掉 <c>ValveBiped.Bip01_</c> 前缀）。</param>
/// <param name="Hd2Node">HD2 节点名。</param>
/// <param name="Transform">
/// 逐骨相似变换 <c>T = H · S⁻¹</c>：把源骨的世界 bind 变换到该 HD2 节点的世界 bind。
/// 它把源骨原点<b>精确</b>送到 HD2 关节上，所以映射后的骨架骨长与 HD2 完全一致——
/// 代价是源网格会被拉伸到 HD2 的骨长（见 <see cref="RigMap.Stretches"/>）。
/// </param>
/// <param name="SourceOriginMetres">该源骨原点的世界位置，已换算成米。</param>
/// <param name="Hd2Origin">对应 HD2 节点的世界位置。</param>
public sealed record RigMapEntry(
	string SourceBone,
	string Hd2Node,
	Mat4 Transform,
	Vec3 SourceOriginMetres,
	Vec3 Hd2Origin);

/// <summary>本该映射上、却没找到对端的一根骨。</summary>
public sealed record RigMapMissing(string SourceBone, string Hd2Node, string Reason);

/// <summary>
/// 一段骨在两侧的长度对比。
/// <para>
/// <see cref="Stretch"/> 是"网格会不会被拉伸"的诚实数字：逐骨相似变换让映射后的骨架拿到 HD2 的骨长，
/// 所以任何源长度与 HD2 不同的区域，都会被拉伸同样的比例。<c>2.615</c> 意味着那段在 HD2 里要长 2.6 倍。
/// </para>
/// </summary>
public sealed record RigSegmentStretch(string Segment, double SourceLength, double Hd2Length, double Stretch);

/// <summary>一次源骨架 → HD2 骨架映射的全部结果。</summary>
public sealed record RigMap(
	IReadOnlyList<RigMapEntry> Entries,
	IReadOnlyList<RigMapMissing> Missing,
	IReadOnlyList<RigSegmentStretch> Stretches);

/// <summary>
/// 源骨架到 HD2 骨架的对应表与求解器。
/// <para>
/// 这张表是<b>手工判断</b>的产物，不是从数据里推出来的，所以每一处选择都值得写清理由——
/// 尤其是躯干/头那几处：两侧的关节语义不同，选错不会报错，只会让脖子变长、肩膀压扁。
/// </para>
/// </summary>
public static class SourceToHd2RigMap
{
	/// <summary>源骨名的统一前缀。剥掉它才能与 HD2 的短名对上。</summary>
	public const string SourceBonePrefix = "ValveBiped.Bip01_";

	/// <summary>Source 的长度单位是英寸。</summary>
	public const double SourceInchesToMetres = 0.0254;

	/// <summary>
	/// 躯干与四肢的对应表（20 对）。
	/// <para>
	/// 几处<b>关节语义不同</b>的选择，是后面所有"比例失调"症状的源头，列在这里以免被当成笔误改掉：
	/// </para>
	/// <list type="bullet">
	/// <item><c>Spine4 → chest</c>：源侧没有独立的"胸腔"骨，<c>Spine4</c> 就是它的位置。</item>
	/// <item><c>Neck1 → neck</c>、<c>Head1 → head</c>：两侧对"颈根"和"头根"的定义不同
	/// （<c>neck→head</c> 段长比 <b>2.615</b>）。这一对是"长脖子"的直接来源。</item>
	/// <item><c>L_UpperArm → l_shoulder</c>（<b>不是</b> <c>l_clavicle</c>）：源侧的锁骨骨很短，
	/// 上臂骨才是肩关节；HD2 侧同理。选错会把整条手臂外推一大截。</item>
	/// </list>
	/// </summary>
	private static readonly (string Source, string Hd2)[] BodyBones =
	[
		("Pelvis", "hips"),
		("Spine", "spine1"),
		("Spine2", "spine2"),
		("Spine4", "chest"),
		("Neck1", "neck"),
		("Head1", "head"),
		("L_Clavicle", "l_clavicle"),
		("L_UpperArm", "l_shoulder"),
		("L_Forearm", "l_elbow"),
		("L_Hand", "l_hand"),
		("R_Clavicle", "r_clavicle"),
		("R_UpperArm", "r_shoulder"),
		("R_Forearm", "r_elbow"),
		("R_Hand", "r_hand"),
		("L_Thigh", "l_thigh"),
		("L_Calf", "l_knee"),
		("L_Foot", "l_foot"),
		("R_Thigh", "r_thigh"),
		("R_Calf", "r_knee"),
		("R_Foot", "r_foot"),
	];

	/// <summary>ValveBiped 的手指编号：0=拇指 … 4=小指。</summary>
	private static readonly string[] FingerNames = ["thumb", "index", "middle", "ring", "pinky"];

	/// <summary>用来报告拉伸的骨段（源侧短名）。</summary>
	private static readonly (string From, string To)[] ReportedSegments =
	[
		("Pelvis", "Spine4"),
		("Spine4", "Neck1"),
		("Neck1", "Head1"),
		("L_UpperArm", "L_Hand"),
		("R_UpperArm", "R_Hand"),
		("L_Thigh", "L_Foot"),
		("R_Thigh", "R_Foot"),
	];

	/// <summary>完整的对应表：20 根身体骨 + 30 根手指骨 = 50 对。</summary>
	public static IReadOnlyList<(string SourceBone, string Hd2Node)> Correspondence { get; } = BuildCorrespondence();

	/// <summary>按对应表把源骨架解到 HD2 骨架上。</summary>
	public static RigMap Build(SourceModelDocument source, Hd2Rig hd2)
	{
		ArgumentNullException.ThrowIfNull(source);
		ArgumentNullException.ThrowIfNull(hd2);

		var sourceByShortName = IndexSourceBones(source);
		var entries = new List<RigMapEntry>(Correspondence.Count);
		var missing = new List<RigMapMissing>();

		foreach (var (sourceName, hd2Name) in Correspondence)
		{
			if (!sourceByShortName.TryGetValue(sourceName, out var sourceBone))
			{
				missing.Add(new RigMapMissing(sourceName, hd2Name, "源模型里没有这根骨"));
				continue;
			}

			var hd2Bone = hd2.Find(hd2Name);
			if (hd2Bone is null)
			{
				missing.Add(new RigMapMissing(sourceName, hd2Name, "HD2 骨架里没有这个节点"));
				continue;
			}

			// 源侧单位是英寸，先换算成米再与 HD2 比。不换算不会报错，只会让整张对应表差 39.37 倍。
			var sourceOriginMetres = ScaleTranslation(sourceBone.WorldBind, SourceInchesToMetres);
			if (!sourceOriginMetres.TryInvert(out var sourceInverse))
			{
				missing.Add(new RigMapMissing(sourceName, hd2Name, "源骨的 bind 矩阵不可逆"));
				continue;
			}

			entries.Add(new RigMapEntry(
				sourceName,
				hd2Name,
				hd2Bone.WorldBind * sourceInverse,
				sourceOriginMetres.Origin,
				hd2Bone.WorldOrigin));
		}

		return new RigMap(entries, missing, MeasureStretch(entries));
	}

	/// <summary>把源骨按短名索引。名字读不出来的骨（<c>source_extra_N</c>）不进表——它们本来就不该映射。</summary>
	private static Dictionary<string, SourceBone> IndexSourceBones(SourceModelDocument source)
	{
		var map = new Dictionary<string, SourceBone>(StringComparer.Ordinal);
		foreach (var bone in source.Bones)
		{
			if (bone.HasName && bone.Name!.StartsWith(SourceBonePrefix, StringComparison.Ordinal))
				map.TryAdd(bone.Name[SourceBonePrefix.Length..], bone);
		}

		return map;
	}

	private static IReadOnlyList<RigSegmentStretch> MeasureStretch(IReadOnlyList<RigMapEntry> entries)
	{
		var bySource = entries.ToDictionary(entry => entry.SourceBone, StringComparer.Ordinal);
		var result = new List<RigSegmentStretch>(ReportedSegments.Length);

		foreach (var (from, to) in ReportedSegments)
		{
			if (!bySource.TryGetValue(from, out var fromEntry) || !bySource.TryGetValue(to, out var toEntry))
				continue;

			var sourceLength = Vec3.Distance(fromEntry.SourceOriginMetres, toEntry.SourceOriginMetres);
			var hd2Length = Vec3.Distance(fromEntry.Hd2Origin, toEntry.Hd2Origin);
			result.Add(new RigSegmentStretch(
				$"{from}->{to}",
				sourceLength,
				hd2Length,
				sourceLength > 0 ? hd2Length / sourceLength : 0));
		}

		return result;
	}

	/// <summary>只缩放平移量（第 4 列），旋转部分保持不动。</summary>
	private static Mat4 ScaleTranslation(Mat4 matrix, double scale) => new(
		matrix.M11, matrix.M12, matrix.M13, matrix.M14 * scale,
		matrix.M21, matrix.M22, matrix.M23, matrix.M24 * scale,
		matrix.M31, matrix.M32, matrix.M33, matrix.M34 * scale,
		matrix.M41, matrix.M42, matrix.M43, matrix.M44);

	private static (string, string)[] BuildCorrespondence()
	{
		var table = new List<(string, string)>(BodyBones.Length + 30);
		table.AddRange(BodyBones);

		// ValveBiped 的手指名是 {L|R}_Finger{0..4}{""|1|2}（三节），
		// HD2 侧是 {l|r}_{thumb..pinky}_finger{1..3}。
		foreach (var (side, prefix) in new[] { ("L", "l"), ("R", "r") })
		{
			for (var finger = 0; finger < FingerNames.Length; finger++)
			{
				for (var joint = 0; joint < 3; joint++)
				{
					var suffix = joint == 0 ? string.Empty : joint.ToString(CultureInfo.InvariantCulture);
					table.Add(($"{side}_Finger{finger}{suffix}", $"{prefix}_{FingerNames[finger]}_finger{joint + 1}"));
				}
			}
		}

		return [.. table];
	}
}
