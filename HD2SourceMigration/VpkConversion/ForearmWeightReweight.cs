using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2SourceModel;

namespace HD2SourceMigration.VpkConversion;

/// <summary>
/// 小臂/手腕的权重重分配。移植自 <c>tmp/rig-lab/reweight_forearm_twist.py</c>（第 68 轮）。
/// <para>
/// <b>为什么需要它。</b>源模型的整条小臂是"从肘部 0.08 到手部 1.00 的平滑手部权重斜坡"，
/// 而不是一个刚性段（`reweight_forearm.py` 开头的实测结论）。后果有两个，用户都报了：
/// </para>
/// <list type="number">
/// <item><b>橡皮筋感</b>：整个小臂跟着手腕每一次弯曲走。</item>
/// <item><b>手腕"纸张折叠"</b>：把斜坡收窄成窄带之后，<b>单条 33 mm 的网格边跨越了整个权重带</b>，
/// 于是一条边承担 0.945 的权重变化 —— 180° 手腕旋转时撕裂 83 mm，看起来就像纸被折了一下。</item>
/// </list>
/// <para>
/// <b>HD2 自带的解法。</b>骨架里有 <c>l_hand_twist</c> / <c>r_hand_twist</c>：
/// 与手共位的叶子骨、挂在肘下，<b>原版权重为 0</b>。扭转骨不参与弯曲，
/// 所以把小臂的"滚转"放到它上面，就能把滚转与弯曲解耦 —— 这是治本的一步。
/// </para>
/// <para>
/// <b>做法</b>（每侧，只动 <c>{肘, 手, 扭转}</c> 三根骨，其余权重原样保留）：
/// <code>
/// axis = normalize(handHead - elbowHead)，L = |handHead - elbowHead|
/// t    = dot(pos - elbowHead, axis)             沿臂轴的距离
/// rad  = |(pos - elbowHead) - t*axis|           到臂轴的垂距
/// ROI  = rad &lt; radius 且 0 &lt; t &lt; max(L, handEnd*L)
/// s = smoothstep(a*L, b*L, t)                   肘→手的混合
/// h = smoothstep(handStart*L, handEnd*L, t)     近腕处把扭转份额交还给手
/// other    = 不在 {肘,手,扭转} 上的权重之和（保留）
/// armTotal = 1 - other
/// 肘 = armTotal * (1 - s)
/// 扭 = armTotal * s * twistFrac * (1 - h)
/// 手 = armTotal * s * ((1 - twistFrac) + twistFrac * h)
/// </code>
/// </para>
/// <para>
/// ⚠️ <b>ROI 必须越过手腕</b>（<c>t_end = max(L, handEnd*L)</c>）。早先版本用 <c>t &lt; L</c> 截断了
/// 交接斜坡，在腕部留下台阶（第 74 轮："2 点钟的杆、3 点钟的头"那个错位）。
/// </para>
/// <para>
/// <b>手指权重不受影响</b>：只有落在 <c>{肘,手,扭转}</c> 上的权重被重新分配，
/// 手指骨（手的子骨）上的权重<b>逐字节不动</b>，所以弯手指仍然是 100% 跟随。
/// </para>
/// </summary>
public static class ForearmWeightReweight
{
	/// <summary>骨名哈希。来自 <c>docs/HD2SDK-CommunityEdition-3.8.0/hashlists/bonehash.txt</c>。</summary>
	private const uint LeftElbow = 480431462;

	private const uint RightElbow = 836980140;

	private const uint LeftHand = 1544317139;

	private const uint RightHand = 612369653;

	private const uint LeftHandTwist = 4135087676;

	private const uint RightHandTwist = 2467345260;

	// 出货参数：`_r79_assemble.py:60` 写的是
	//   reweight_forearm_twist.py --a 0.47 --b 0.88 --twist-frac 1.0
	// 其余取脚本默认值。

	/// <summary>肘→手混合斜坡的起点（沿臂轴的比例）。</summary>
	public const double A = 0.47;

	/// <summary>肘→手混合斜坡的终点。</summary>
	public const double B = 0.88;

	/// <summary>远端份额里给扭转骨的比例；1.0 = 全部给扭转骨（弯曲保持刚性）。</summary>
	public const double TwistFraction = 1.0;

	/// <summary>
	/// 近腕处开始把扭转份额交还手的起点。
	/// <para>
	/// ⚠️ <b>出货值是 0.9，不是脚本的默认值 1.01。</b>
	/// 依据是出货包里的 manifest（`E:\mod\Lunalice-AD26-R82-Final\*.manifest.json`，
	/// schema 就是 <c>hd2mod.rig-lab-forearm-twist</c>）：
	/// <c>hand_start=0.9, hand_end=1.12</c>。
	/// </para>
	/// <para>
	/// <b>这两个值直接决定腕部的权重分布</b>，而默认值与出货值差别很大：
	/// 在 t/L=1.0（腕部）处，出货配置给手 0.432、扭转 0.568；
	/// 而默认配置给扭转 1.0、手 0 —— 腕部完全刚性，于是出现褶皱。
	/// </para>
	/// </summary>
	public const double HandStart = 0.9;

	/// <summary>交还完成的终点。出货值是 1.12（脚本默认 1.21）。</summary>
	public const double HandEnd = 1.12;

	/// <summary>ROI 半径（米）。</summary>
	public const double Radius = 0.10;

	private static readonly (uint Elbow, uint Hand, uint Twist)[] Sides =
	[
		(LeftElbow, LeftHand, LeftHandTwist),
		(RightElbow, RightHand, RightHandTwist),
	];

	/// <summary>
	/// 就地把 <paramref name="weights"/>（骨哈希 → 权重）按小臂规则重写。
	/// 不在 ROI 内、或三根骨都没有权重的顶点**原样返回**。
	/// </summary>
	/// <param name="position">该顶点在**最终网格**里的位置（HD2 世界坐标，米）。</param>
	public static void Apply(
		Dictionary<uint, double> weights, Vec3 position, UnitTransformInfo avatarTransformInfo)
	{
		ArgumentNullException.ThrowIfNull(weights);
		ArgumentNullException.ThrowIfNull(avatarTransformInfo);
		if (weights.Count == 0)
			return;

		foreach (var (elbowHash, handHash, twistHash) in Sides)
		{
			if (!TryHead(avatarTransformInfo, elbowHash, out var elbowHead)
				|| !TryHead(avatarTransformInfo, handHash, out var handHead))
			{
				// 骨架里没有这对骨（例如这个 Unit 根本不含手臂）——跳过，不改任何权重。
				continue;
			}

			var axisVector = handHead - elbowHead;
			var length = axisVector.Length;
			if (length <= 1e-9)
				continue;
			var axis = axisVector / length;

			var relative = position - elbowHead;
			var t = Dot(relative, axis);
			var perpendicular = relative - (axis * t);
			var radius = perpendicular.Length;

			// ROI 越过手腕：交接斜坡横跨 t/L = 1，只到 L 会把它截断、在腕部留台阶。
			var end = Math.Max(length, HandEnd * length);
			if (radius >= Radius || t <= 0 || t >= end)
				continue;

			// 这三根骨的总权重；其余（含手指骨）原样保留。
			var armTotal = 0.0;
			foreach (var (boneHash, weight) in weights)
			{
				if (boneHash == elbowHash || boneHash == handHash || boneHash == twistHash)
					armTotal += weight;
			}

			if (armTotal <= 0)
				continue;

			var s = SmoothStep(A * length, B * length, t);
			var h = SmoothStep(HandStart * length, HandEnd * length, t);

			var elbowWeight = armTotal * (1.0 - s);
			var twistWeight = armTotal * s * TwistFraction * (1.0 - h);
			var handWeight = armTotal * s * ((1.0 - TwistFraction) + (TwistFraction * h));

			Set(weights, elbowHash, elbowWeight);
			Set(weights, twistHash, twistWeight);
			Set(weights, handHash, handWeight);

			// 一次只处理一侧：两侧的 ROI 不会同时命中同一个顶点。
			return;
		}
	}

	private static void Set(Dictionary<uint, double> weights, uint hash, double weight)
	{
		if (weight > 0)
			weights[hash] = weight;
		else
			weights.Remove(hash);
	}

	/// <summary>SDK 的 <c>smoothstep</c>：<c>u = clamp((x-a)/(b-a), 0, 1); u²(3-2u)</c>。</summary>
	public static double SmoothStep(double a, double b, double x)
	{
		if (Math.Abs(b - a) <= 1e-12)
			return x >= b ? 1 : 0;
		var u = Math.Clamp((x - a) / (b - a), 0.0, 1.0);
		return u * u * (3.0 - (2.0 * u));
	}

	/// <summary>取一根骨的骨头位置。矩阵是行主序、平移在**最后一行**（下标 12..14）。</summary>
	private static bool TryHead(UnitTransformInfo info, uint hash, out Vec3 head)
	{
		head = Vec3.Zero;
		for (var i = 0; i < info.NameHashes.Count; i++)
		{
			if (info.NameHashes[i] != hash)
				continue;
			if (i >= info.Matrices.Count)
				return false;
			var values = info.Matrices[i].Values;
			if (values.Count < 15)
				return false;
			head = new Vec3(values[12], values[13], values[14]);
			return true;
		}

		return false;
	}

	private static double Dot(Vec3 a, Vec3 b) => (a.X * b.X) + (a.Y * b.Y) + (a.Z * b.Z);
}
