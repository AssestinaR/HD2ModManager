using HD2SourceModel;

namespace HD2SourceMigration.Retarget;

/// <summary>4k 腿部重拟合的膝盖落点。</summary>
public enum LegRefitMode
{
	/// <summary>不做（发货默认）。脚底留在约 −0.09 m。</summary>
	Off,

	/// <summary>膝盖竖直对齐 HD2 自己的膝骨，大腿、小腿各自压缩。弯膝发生在骨头上，蹲/跪更准。</summary>
	Bone,

	/// <summary>大腿、小腿按同一个比例压缩，膝盖在腿上的相对位置不变。外形改动最小。</summary>
	Proportional,
}

public sealed partial class Retargeter
{
	/// <summary>
	/// 阶段 4k：仅网格的腿部重拟合——把脚放回地面，同时<b>不动髋以上任何一个顶点</b>。
	/// <para>
	/// 为什么需要：源模型的腿相对躯干比 HD2 骨架长 9–13%（多出来的主要在小腿：源 0.45–0.54 m，
	/// HD2 0.423 m）。4c 的 <see cref="RetargetOptions.MeshShift"/> 为了让手贴住手骨把整张网格压低 9 cm，
	/// 多出来的腿长于是只能伸进地面。<see cref="RetargetOptions.GroundLift"/> 整体抬升会让手离开手骨，
	/// 所以只能在腿上解决。
	/// </para>
	/// <para>
	/// 做法（每条腿独立）：在最终网格空间里取源髋 H、膝 K、踝 A 的高度，
	/// 目标 H' = H（不动）、A' = A + 抬升量（让这只脚的鞋底回到 z = 0）、K' 由 <see cref="LegRefitMode"/> 决定。
	/// 顶点只沿 <b>z</b> 移动，位移按高度分段线性：髋以上 0、髋→膝从 0 渐变到 K'−K、膝→踝渐变到 A'−A、
	/// 踝以下整块平移（鞋不被压扁）。同一高度的切片整体平移，所以<b>腿的横截面完全不变</b>，只是变短。
	/// </para>
	/// <para>
	/// 位移再乘以顶点挂在这条腿上的权重之和（沿源骨架上溯认骨，同 <see cref="SourceArmBones"/>）。
	/// 这一步是必需的：A-pose 下垂的手就在大腿高度，不乘权重手会被一起拉上去；
	/// 裙摆、披风挂在骨盆上，也因此原地不动。
	/// </para>
	/// </summary>
	private Vec3[] ApplyLegRefit(Vec3[] points, List<RetargetExtraBone> extraBones)
	{
		var result = (Vec3[])points.Clone();
		var extraDelta = new Dictionary<int, double>();
		foreach (var side in new[] { "l", "r" })
		{
			if (!TryLegJoints(side, out var hip, out var knee, out var ankle, out var kneeHd2))
				continue;

			var heights = new double[points.Length];
			var influence = new double[points.Length];
			var footWeight = new double[points.Length];
			for (var k = 0; k < points.Length; k++)
			{
				heights[k] = points[k].Z;
				(influence[k], footWeight[k]) = LegInfluence(k, side);
			}

			// 4j 的 GroundLift 会在之后整体再抬，所以 4k 的目标要先扣掉它，两者叠加后鞋底仍落在 LegRefitGround。
			var groundLift = _options.GroundLift;
			if (SoleLiftNeeded(heights, influence, footWeight, ankle, _options.LegRefitGround - groundLift) is not { } needed)
				continue;
			var lift = LegLiftCapacity(hip, ankle, needed);
			if (lift <= 0.0)
				continue;

			var kneeLift = LegKneeLift(_options.LegRefit, hip, knee, ankle, lift, kneeHd2 - groundLift);
			for (var k = 0; k < points.Length; k++)
			{
				if (influence[k] <= 0.0)
					continue;
				var dz = LegLiftAt(points[k].Z, hip, knee, ankle, kneeLift, lift) * influence[k];
				result[k] += new Vec3(0, 0, dz);
			}

			// jiggle 合成骨：位移在它<b>对应的网格位置</b>（Post 之后）求值，再加到它自己的原点上。
			// 注意合成骨原点本身从未经过 4c/4d（上游现状，不是本阶段引入的），这里只保证 4k 不让它与网格的相对偏差变大。
			for (var e = 0; e < extraBones.Count; e++)
			{
				var anchor = extraBones[e].AnchorIndex;
				if (anchor >= 0 && _nameByIndex[anchor] is { } name && IsLegBoneOf(name, side))
					extraDelta[e] = LegLiftAt(Post(extraBones[e].WorldOrigin).Z, hip, knee, ankle, kneeLift, lift);
			}
		}

		foreach (var (e, dz) in extraDelta)
			extraBones[e] = extraBones[e] with { WorldOrigin = extraBones[e].WorldOrigin + new Vec3(0, 0, dz) };
		return result;
	}

	/// <summary>
	/// 膝盖的抬升量。<see cref="LegRefitMode.Bone"/> 取"源膝 → HD2 膝"的竖直差，但夹在一个区间里：
	/// 两段的压缩比都只能在 [<see cref="MinSegmentScale"/>, 1]——<b>只缩不拉长</b>，也不把一段腿压到四分之三以下。
	/// 源髋普遍比 HD2 髋高几厘米，所以"膝往下对齐"常被夹成 0（大腿保持原长、全部压在小腿上）。
	/// </summary>
	/// <para>调用方必须先用 <see cref="LegLiftCapacity"/> 把 <paramref name="lift"/> 限制住：只有这样区间才非空、
	/// 按比例分配也不会把任一段压到下限以下。</para>
	internal static double LegKneeLift(LegRefitMode mode, double hip, double knee, double ankle, double lift, double kneeHd2)
	{
		var thigh = hip - knee;
		var calf = knee - ankle;
		if (mode != LegRefitMode.Bone)
			return lift * (thigh / (thigh + calf));

		var low = Math.Max(0.0, lift - ((1.0 - MinSegmentScale) * calf));
		var high = Math.Min((1.0 - MinSegmentScale) * thigh, lift);
		return low > high ? lift * (thigh / (thigh + calf)) : RetargetMath.Clamp(kneeHd2 - knee, low, high);
	}

	/// <summary>
	/// 把请求的抬升量限制在腿能承受的范围：整条腿（髋→踝）最多压掉 1 − <see cref="MinSegmentScale"/>。
	/// lift ≤ 这个容量时，<see cref="LegKneeLift"/> 的区间必然非空，两段压缩比都 ≥ 下限，腿段的 z 顺序也不会翻转。
	/// 超出的部分不补（脚仍会略陷），宁可少抬也不把腿压坏。
	/// </summary>
	internal static double LegLiftCapacity(double hip, double ankle, double requested) =>
		Math.Min(requested, (1.0 - MinSegmentScale) * (hip - ankle));

	/// <summary>
	/// 让这只脚的鞋底落到 <paramref name="target"/> 所需的（未限幅的）抬升量；没有鞋底样本时返回 null。
	/// <para>
	/// 样本：踝以下、<b>以本侧脚/脚趾为主</b>（脚权重 ≥ <see cref="GroundSampleWeight"/>）的顶点。以脚为主而不是
	/// "几乎全挂在脚上"：Source 的鞋常是 0.9 脚 + 0.1 小腿，门槛太高会整只鞋被跳过；挂在小腿上、垂得比鞋底
	/// 还低的附件也不会被当成鞋底。取最低 1 cm 内样本的平均高度（与 1b 量源脚底的口径一致）。
	/// </para>
	/// <para>
	/// 踝以下的实际位移是 lift × 腿权重，所以按样本的平均腿权重反求 lift——腿权重 0.95 时鞋底不会剩 4.5 mm。
	/// </para>
	/// </summary>
	internal static double? SoleLiftNeeded(
		IReadOnlyList<double> heights, IReadOnlyList<double> legWeight, IReadOnlyList<double> footWeight, double ankle, double target)
	{
		var footMin = double.MaxValue;
		for (var k = 0; k < heights.Count; k++)
		{
			if (footWeight[k] >= GroundSampleWeight && heights[k] < ankle)
				footMin = Math.Min(footMin, heights[k]);
		}

		if (footMin == double.MaxValue)
			return null;

		var sum = 0.0;
		var weight = 0.0;
		var count = 0;
		for (var k = 0; k < heights.Count; k++)
		{
			if (footWeight[k] >= GroundSampleWeight && heights[k] < footMin + 0.01)
			{
				sum += heights[k];
				weight += legWeight[k];
				count++;
			}
		}

		return (target - (sum / count)) / Math.Max(weight / count, GroundSampleWeight);
	}

	private const double GroundSampleWeight = 0.5;

	/// <summary>高度 z 处的竖直位移：髋以上 0，髋→膝、膝→踝分段线性，踝以下等于踝的抬升（整只脚刚性平移）。</summary>
	internal static double LegLiftAt(double z, double hip, double knee, double ankle, double kneeLift, double ankleLift)
	{
		if (z >= hip)
			return 0.0;
		if (z >= knee)
			return kneeLift * ((hip - z) / Math.Max(hip - knee, 1e-9));
		if (z >= ankle)
			return kneeLift + ((ankleLift - kneeLift) * ((knee - z) / Math.Max(knee - ankle, 1e-9)));
		return ankleLift;
	}

	private const double MinSegmentScale = 0.75;

	/// <summary>最终网格空间里源髋/膝/踝的高度（经过 4c/4d 的 <see cref="Post"/>），以及 HD2 膝骨的高度。</summary>
	private bool TryLegJoints(string side, out double hip, out double knee, out double ankle, out double kneeHd2)
	{
		hip = knee = ankle = kneeHd2 = 0;
		if (!_byName.TryGetValue($"{side}_thigh", out var thighBone)
			|| !_byName.TryGetValue($"{side}_knee", out var kneeBone)
			|| !_byName.TryGetValue($"{side}_foot", out var footBone))
			return false;

		// 三个关节都必须真的从源骨 snap 过来。_restWorking 对每根 HD2 骨都有一项，缺源骨时那是
		// 插值/继承出来的位置，拿它当"源膝"会把压缩分界放错地方——宁可这一侧不做。
		var sourceSide = side.ToUpperInvariant();
		foreach (var joint in new[] { "Thigh", "Calf", "Foot" })
		{
			if (!_shortByName.ContainsKey($"{sourceSide}_{joint}"))
				return false;
		}

		double Height(Hd2RigBone bone) => Post((_deform[bone.Index] * _restWorking[bone.Index]).Origin).Z;
		if (!_restWorking.ContainsKey(thighBone.Index) || !_restWorking.ContainsKey(kneeBone.Index) || !_restWorking.ContainsKey(footBone.Index))
			return false;

		hip = Height(thighBone);
		knee = Height(kneeBone);
		ankle = Height(footBone);
		kneeHd2 = _restHd2[kneeBone.Index].Origin.Z;
		return hip > knee + 0.05 && knee > ankle + 0.05;
	}

	/// <summary>
	/// 顶点挂在本侧腿上的权重之和（大腿/小腿/脚/脚趾，含上溯到它们的辅助骨），以及其中脚/脚趾那一部分。均 0–1。
	/// </summary>
	private (double Leg, double Foot) LegInfluence(int vertexIndex, string side)
	{
		var weights = _weights[vertexIndex];
		var bones = _boneIndices[vertexIndex];
		var leg = 0.0;
		var foot = 0.0;
		for (var j = 0; j < weights.Length; j++)
		{
			if (weights[j] <= 0.0 || bones[j] < 0 || bones[j] >= _sourceNames.Length)
				continue;
			var (boneSide, part) = ClassifySourceLegBone(_sourceNames, _sourceParents, bones[j]);
			if (part == SourceLegPart.None || boneSide != side)
				continue;
			leg += weights[j];
			if (part == SourceLegPart.Foot)
				foot += weights[j];
		}

		return (RetargetMath.Clamp(leg, 0, 1), RetargetMath.Clamp(foot, 0, 1));
	}

	internal enum SourceLegPart
	{
		None,
		Upper,
		Foot,
	}

	/// <summary>
	/// 沿源骨架上溯，<b>第一根</b>认得出的腿骨决定归属（哪一侧、是不是脚）。只按第一根判，
	/// 一根骨就只属于一侧——即便父链交叉（<c>R_Foot</c> 挂在 <c>L_Calf</c> 下）也不会被左右各算一次。
	/// 碰到骨盆/脊柱就停：裙摆、披风的辅助骨因此不算腿。
	/// </summary>
	internal static (string Side, SourceLegPart Part) ClassifySourceLegBone(
		IReadOnlyList<string> names, IReadOnlyList<int> parents, int boneIndex)
	{
		var current = boneIndex;
		for (var hops = 0; current >= 0 && current < names.Count && hops <= RetargetConstants.MaxSourceAncestorHops; hops++)
		{
			var text = names[current];
			if (text.StartsWith(RetargetConstants.SourceBonePrefix, StringComparison.Ordinal))
				text = text[RetargetConstants.SourceBonePrefix.Length..];
			if (text.Length > 2 && (text[0] == 'L' || text[0] == 'R') && text[1] == '_')
			{
				var tail = text[2..];
				var side = text[0] == 'L' ? "l" : "r";
				if (tail.StartsWith("Foot", StringComparison.Ordinal) || tail.StartsWith("Toe", StringComparison.Ordinal))
					return (side, SourceLegPart.Foot);
				if (tail.StartsWith("Thigh", StringComparison.Ordinal) || tail.StartsWith("Calf", StringComparison.Ordinal))
					return (side, SourceLegPart.Upper);
			}

			if (text is "Pelvis" || text.StartsWith("Spine", StringComparison.Ordinal) || current >= parents.Count)
				break;
			current = parents[current];
		}

		return (string.Empty, SourceLegPart.None);
	}

	private static bool IsLegBoneOf(string hd2Name, string side) =>
		hd2Name is var n && (n == $"{side}_thigh" || n == $"{side}_knee" || n == $"{side}_foot" || n == $"{side}_ball" || n == $"{side}_toe");
}
