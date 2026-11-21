using HD2SourceModel;

namespace HD2SourceMigration.Retarget;

public sealed partial class Retargeter
{
	/// <summary>
	/// 按<b>主骨</b>把一个顶点分到 (上臂 / 远端 / 手臂) 三类。
	/// <para>
	/// 归属由顶点的<b>主骨</b>（三组权重里最大的那个）决定，<b>绝不</b>用"任意权重大于零"。
	/// 一个肩部顶点可以带 0.9994 的 UpperArm 和 0.0006 的 Forearm，把这点痕迹算成"远端"会强行
	/// 让上臂<b>根部</b>的 <c>f = 1</c>，位移在一条边上跳 3.7 cm。用主骨，每个顶点就恰好属于
	/// 一侧、一组。
	/// </para>
	/// <para>
	/// 名字认不出来时由 <see cref="SourceArmBones.Classify"/> 沿源骨架上溯——为什么这一条必需，
	/// 以及 <c>source_extra_78</c> 折腕的完整测量，都写在那个类上。
	/// </para>
	/// </summary>
	private (bool[] Upper, bool[] Distal, bool[] Arm) ArmVertexPartition(string side)
	{
		var count = _sourcePoints.Length;
		var upper = new bool[count];
		var distal = new bool[count];
		var arm = new bool[count];
		for (var k = 0; k < count; k++)
		{
			if (!TryPrimarySourceBone(k, out var boneIndex))
				continue;
			var part = ClassifyArmBone(boneIndex, side);
			if (!SourceArmBones.IsArm(part))
				continue;
			arm[k] = true;
			if (part == SourceArmPart.Upper)
				upper[k] = true;
			else if (SourceArmBones.IsDistal(part))
				distal[k] = true;
		}

		return (upper, distal, arm);
	}

	private SourceArmPart ClassifyArmBone(int boneIndex, string side) =>
		SourceArmBones.Classify(side, _sourceNames, _sourceParents, boneIndex);

	/// <summary>
	/// 主骨是<b>手</b>或<b>手指</b>的顶点。
	/// <para>
	/// 这些顶点在<b>拓扑上</b>位于腕的远端，而不是按半径。这个区别就是全部要点：手指比小臂长度补偿的
	/// 圆管长（实测三个模型上拇指在腕后 0.0418–0.1437 m），所以径向闸会从远端指节中间切过去——
	/// 里面那半跟着补偿平移、外面那半不动，拇指尖就被剪了。那次剪切就是"拇指反曲"：
	/// MourningVeil/Lunalice 上远端指节被弯了 12.8–13.2°，而 Lilium（补偿只有 −1.0 mm）留在 1.2°。
	/// </para>
	/// </summary>
	private bool[] HandFingerMask(string side)
	{
		var count = _sourcePoints.Length;
		var result = new bool[count];
		for (var k = 0; k < count; k++)
		{
			if (!TryPrimarySourceBone(k, out var boneIndex))
				continue;
			// 与 ArmVertexPartition 同一套上溯规则：挂在手上的辅助骨也算手，
			// 否则圆管闸的"手/指豁免"漏掉它们，远端指节仍会被拦腰切开。
			if (SourceArmBones.IsHand(ClassifyArmBone(boneIndex, side)))
				result[k] = true;
		}

		return result;
	}

	/// <summary>
	/// 3b 的位移斜坡 <c>f</c>：
	/// <code>
	/// s = dot(v - SH, u)         u = (EL - SH)/|EL - SH|,  L = |EL - SH|
	/// f = clamp(s / L, 0, 1)     线性斜坡 → 均匀轴向拉伸
	/// f[distal] = 1              小臂/手/手指：刚性平移
	/// f[非本侧手臂] = 0           躯干、腿、另一只手臂、披风：一律不动
	/// </code>
	/// <para>
	/// <b>为什么非手臂顶点必须整体钉住，而不是只钉"肩关节内侧"。</b>旧规则是
	/// <c>!arm &amp;&amp; along &lt;= 0 ⇒ 0</c>，其余照样走斜坡。它只在源模型是 <b>T-pose</b> 时成立——
	/// 那时上臂轴水平朝外，肩外侧只有手臂。源模型是 <b>A-pose</b>（上臂下垂 36°–57°）时，
	/// 上臂轴斜向下，脚、腿、髋都落在 <c>along ≥ L</c> 一侧，被当成"肘以外"整体平移了
	/// 一个补偿量；左右两遍水平分量抵消、竖直分量叠加。
	/// 实测（2026-11-21，本机 11 个 L4D2 人物 mod）：5 个 A-pose 源的脚底被额外压下 12–18 cm
	/// （发货 soleZ −0.21 … −0.27，T-pose 源都是 −0.09），一一对应，无一例外。
	/// </para>
	/// <para>
	/// 当初不敢"钉住分区没认领的一切"，是因为 <c>source_extra_78</c>（挂在<b>手</b>上的辅助骨）
	/// 叫不出名字、会被钉住而撕开外壳（实测 5 cm 跳变）。那件事现在由
	/// <see cref="SourceArmBones.Classify"/> 的<b>沿祖先上溯</b>解决：它会被认成手，走 <c>distal</c>，
	/// 所以这里可以放心把真正的非手臂顶点全部钉住。
	/// </para>
	/// </summary>
	private (double[] Field, bool[] Upper, bool[] Distal, bool[] Arm) ArmStretchField(
		IReadOnlyList<Vec3> points, string side, Vec3 shoulder, Vec3 elbow)
	{
		var direction = elbow - shoulder;
		var length = direction.Length;
		var (upper, distal, arm) = ArmVertexPartition(side);
		var field = new double[points.Count];
		if (length < 1e-9)
			return (field, upper, distal, arm);

		var axis = direction / length;
		for (var i = 0; i < points.Count; i++)
		{
			var along = Vec3.Dot(points[i] - shoulder, axis);
			var value = RetargetMath.Clamp(along / length, 0, 1);
			if (distal[i])
				value = 1.0;
			else if (!arm[i])
				value = NonArmStretch(value, along, ArmInfluence(i, side), PointSegmentDistance(points[i], shoulder, elbow));
			field[i] = value;
		}

		return (field, upper, distal, arm);
	}

	/// <summary>
	/// 顶点挂在<b>本侧手臂</b>上的权重之和（0–1）。非手臂顶点的拉伸斜坡要乘上它。
	/// <para>
	/// 乘它而不是直接置 0，是因为腋下/肩缝的皮肤主骨是 <c>Spine2</c>/<c>Spine4</c>，却带着一部分
	/// <c>UpperArm</c> 权重、和上臂顶点共边。直接钉死它们会把肩缝撕开（实测 2026-11-21：
	/// 某 A-pose 转换模型腋下边被拉到源边长的 111 倍）。按权重给，接缝随手臂走，没有手臂权重的
	/// 脚、腿、髋则是 0。
	/// </para>
	/// </summary>
	/// <summary>
	/// 非手臂顶点离上臂段（肩→肘）够近时，也跟着拉伸走：距离 ≤ <see cref="SeamNear"/> 给 1，
	/// ≥ <see cref="SeamFar"/> 给 0，中间 smoothstep。
	/// <para>
	/// 只靠 <see cref="ArmInfluence"/> 不够：A-pose 源的腋下有一圈<b>纯脊柱权重</b>的顶点直接和上臂顶点共边，
	/// 权重为 0 就被钉住，肩缝仍会撕开（实测两个 A-pose 转换模型腋下边分别 20×、53×）。
	/// 腿、髋离上臂段都在 0.2 m 以上，所以这段近距离羽化不会把下半身再拖下去。
	/// </para>
	/// </summary>
	/// <summary>
	/// 非本侧手臂顶点的拉伸系数：肩关节内侧为 0，其余为斜坡值 ×
	/// max(本侧手臂权重, 离上臂段的近距离羽化)。抽成纯函数是为了能脱离真实模型单测。
	/// </summary>
	internal static double NonArmStretch(double ramp, double along, double armInfluence, double distanceToUpperArm) =>
		along <= 0.0 ? 0.0 : ramp * Math.Max(armInfluence, SeamProximity(distanceToUpperArm));

	private static double SeamProximity(double distance)
	{
		var t = RetargetMath.Clamp((SeamFar - distance) / (SeamFar - SeamNear), 0, 1);
		return t * t * (3.0 - (2.0 * t));
	}

	private const double SeamNear = 0.04;
	private const double SeamFar = 0.12;

	private double ArmInfluence(int vertexIndex, string side)
	{
		var weights = _weights[vertexIndex];
		var bones = _boneIndices[vertexIndex];
		var sum = 0.0;
		for (var j = 0; j < weights.Length; j++)
		{
			if (weights[j] <= 0.0 || bones[j] < 0 || bones[j] >= _sourceNames.Length)
				continue;
			if (SourceArmBones.IsArm(ClassifyArmBone(bones[j], side)))
				sum += weights[j];
		}

		return RetargetMath.Clamp(sum, 0, 1);
	}

	/// <summary>
	/// 顶点的<b>主骨</b>下标（三组权重里最大的那个）。权重全为零或下标越界时返回 false。
	/// <para>
	/// 返回的是<b>下标</b>而不是名字，因为调用方需要沿 <c>_sourceParents</c> 上溯；
	/// 名字在上溯的每一跳上单独剥前缀（见 <see cref="ClassifyArmBone"/>）。
	/// </para>
	/// </summary>
	private bool TryPrimarySourceBone(int vertexIndex, out int boneIndex)
	{
		var weights = _weights[vertexIndex];
		var bones = _boneIndices[vertexIndex];
		var best = weights[0] >= weights[1] && weights[0] >= weights[2] ? 0 : weights[1] >= weights[2] ? 1 : 2;
		if (weights[best] <= 0.0)
		{
			boneIndex = -1;
			return false;
		}

		boneIndex = bones[best];
		return boneIndex >= 0 && boneIndex < _sourceNames.Length;
	}

	/// <summary>点到线段的距离。</summary>
	private static double PointSegmentDistance(Vec3 point, Vec3 a, Vec3 b)
	{
		var direction = b - a;
		var denominator = Vec3.Dot(direction, direction);
		var t = RetargetMath.Clamp(Vec3.Dot(point - a, direction) / Math.Max(denominator, 1e-15), 0, 1);
		return (point - (a + (direction * t))).Length;
	}
}
