using System.Reflection;
using System.Text.Json;
using HD2SourceModel;

namespace HD2SourceMigration.Retarget;

/// <summary>
/// HD2 姿态参考表：三张按骨骼名索引的四元数表，来自 HD2-BatchTool 的
/// <c>assets/body_pose_profile.json</c>（文件自称 <c>bundled_hd2_reference_profile</c>）。
/// <list type="bullet">
/// <item><see cref="SymmetricAxisDeltas"/> —— 把骨骼轴从"原始"转到"轴对称"的逐骨局部旋转，
/// 20 根，<b>全是手臂链</b>（锁骨/肩/肘/手/手指），没有一根躯干、腿或头。</item>
/// <item><see cref="TposeBasisRotations"/> —— 把角色摆成 T-pose 的 30 根骨的局部旋转。</item>
/// <item><see cref="InitialBasisRotations"/> —— 从烘好的 T-pose 摆回初始姿态的 30 根骨。</item>
/// </list>
/// <para>
/// 这张表描述的是<b>游戏自己的骨架</b>，不是任何第三方模型的属性，所以它随本工程内置：
/// 转换流程因此不依赖任何外置文件，也就没有"参考表与游戏版本脱节导致映射静默错位"这个失败模式。
/// </para>
/// <para>
/// A table of HD2's own reference pose rotations, keyed by bone name.  It describes the
/// <b>game's skeleton</b>, not any third-party model's property, so it ships inside this
/// project: the conversion therefore depends on no external file, and the failure mode
/// "the reference table has drifted from the game version" cannot occur.
/// </para>
/// </summary>
public sealed class BodyPoseProfile
{
	private const string ResourceSuffix = "body_pose_profile.json";

	private static readonly Lazy<BodyPoseProfile> LazyDefault = new(LoadEmbedded, isThreadSafe: true);

	private BodyPoseProfile(
		IReadOnlySet<string> bodyBones,
		IReadOnlyDictionary<string, Mat4> symmetricAxisDeltas,
		IReadOnlyDictionary<string, Mat4> tposeBasisRotations,
		IReadOnlyDictionary<string, Mat4> initialBasisRotations)
	{
		BodyBones = bodyBones;
		SymmetricAxisDeltas = symmetricAxisDeltas;
		TposeBasisRotations = tposeBasisRotations;
		InitialBasisRotations = initialBasisRotations;
	}

	/// <summary>内置表。整个进程只解析一次。</summary>
	public static BodyPoseProfile Default => LazyDefault.Value;

	/// <summary>表里列出的"身体骨"名字集合。轴对称 delta 只对这些骨生效。</summary>
	public IReadOnlySet<string> BodyBones { get; }

	public IReadOnlyDictionary<string, Mat4> SymmetricAxisDeltas { get; }

	public IReadOnlyDictionary<string, Mat4> TposeBasisRotations { get; }

	public IReadOnlyDictionary<string, Mat4> InitialBasisRotations { get; }

	/// <summary>从内置资源读取。资源缺失是<b>构建配置错误</b>，所以抛异常而不是退化成空表——
	/// 空表不会报错，只会让姿态差出几十度。</summary>
	public static BodyPoseProfile LoadEmbedded()
	{
		var assembly = typeof(BodyPoseProfile).Assembly;
		var name = assembly.GetManifestResourceNames()
			.FirstOrDefault(candidate => candidate.EndsWith(ResourceSuffix, StringComparison.Ordinal))
			?? throw new InvalidOperationException($"内置姿态参考表缺失：{ResourceSuffix}");
		using var stream = assembly.GetManifestResourceStream(name)
			?? throw new InvalidOperationException($"无法读取内置姿态参考表：{name}");
		using var reader = new StreamReader(stream);
		return Parse(reader.ReadToEnd());
	}

	/// <summary>解析表文本。四元数按 <c>(w, x, y, z)</c> 读，与 Python 侧一致。</summary>
	public static BodyPoseProfile Parse(string json)
	{
		ArgumentNullException.ThrowIfNull(json);
		using var document = JsonDocument.Parse(json);
		var root = document.RootElement;

		var bodyBones = new HashSet<string>(StringComparer.Ordinal);
		if (root.TryGetProperty("body_bones", out var bones) && bones.ValueKind == JsonValueKind.Array)
		{
			foreach (var bone in bones.EnumerateArray())
			{
				var name = bone.GetString();
				if (!string.IsNullOrEmpty(name))
					bodyBones.Add(name);
			}
		}

		return new BodyPoseProfile(
			bodyBones,
			ReadQuaternionTable(root, "symmetric_axis_deltas_wxyz"),
			ReadQuaternionTable(root, "tpose_basis_rotations_wxyz"),
			ReadQuaternionTable(root, "initial_basis_rotations_from_baked_tpose_wxyz"));
	}

	private static IReadOnlyDictionary<string, Mat4> ReadQuaternionTable(JsonElement root, string property)
	{
		var result = new Dictionary<string, Mat4>(StringComparer.Ordinal);
		if (!root.TryGetProperty(property, out var table) || table.ValueKind != JsonValueKind.Object)
			return result;

		foreach (var entry in table.EnumerateObject())
		{
			if (entry.Value.ValueKind != JsonValueKind.Array)
				continue;
			var values = new double[4];
			var index = 0;
			foreach (var component in entry.Value.EnumerateArray())
			{
				if (index >= 4)
					break;
				values[index++] = component.GetDouble();
			}

			if (index != 4)
				continue;
			result[entry.Name] = RetargetMath.QuaternionWxyzToMat4(values[0], values[1], values[2], values[3]);
		}

		return result;
	}
}
