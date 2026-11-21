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

			var influence = new double[points.Length];
			var footMin = double.MaxValue;
			for (var k = 0; k < points.Length; k++)
			{
				influence[k] = LegInfluence(k, side);
				if (influence[k] > 0.5 && points[k].Z < ankle)
					footMin = Math.Min(footMin, points[k].Z);
			}

			if (footMin == double.MaxValue)
				continue;

			// 鞋底：这只脚最低 1 cm 内顶点的平均高度，与 1b 量源脚底的口径一致。
			var soleSum = 0.0;
			var soleCount = 0;
			for (var k = 0; k < points.Length; k++)
			{
				if (influence[k] > 0.5 && points[k].Z < footMin + 0.01)
				{
					soleSum += points[k].Z;
					soleCount++;
				}
			}

			var lift = _options.LegRefitGround - (soleSum / soleCount);
			if (lift <= 0.0)
				continue;

			var kneeLift = LegKneeLift(_options.LegRefit, hip, knee, ankle, lift, kneeHd2);
			for (var k = 0; k < points.Length; k++)
			{
				if (influence[k] <= 0.0)
					continue;
				var dz = LegLiftAt(points[k].Z, hip, knee, ankle, kneeLift, lift) * influence[k];
				result[k] += new Vec3(0, 0, dz);
			}

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

		double Height(Hd2RigBone bone) => Post((_deform[bone.Index] * _restWorking[bone.Index]).Origin).Z;
		if (!_restWorking.ContainsKey(thighBone.Index) || !_restWorking.ContainsKey(kneeBone.Index) || !_restWorking.ContainsKey(footBone.Index))
			return false;

		hip = Height(thighBone);
		knee = Height(kneeBone);
		ankle = Height(footBone);
		kneeHd2 = _restHd2[kneeBone.Index].Origin.Z;
		return hip > knee + 0.05 && knee > ankle + 0.05;
	}

	/// <summary>顶点挂在本侧腿（大腿/小腿/脚/脚趾，含上溯到它们的辅助骨）上的权重之和，0–1。</summary>
	private double LegInfluence(int vertexIndex, string side)
	{
		var weights = _weights[vertexIndex];
		var bones = _boneIndices[vertexIndex];
		var sum = 0.0;
		for (var j = 0; j < weights.Length; j++)
		{
			if (weights[j] <= 0.0 || bones[j] < 0 || bones[j] >= _sourceNames.Length)
				continue;
			if (IsSourceLegBone(bones[j], side))
				sum += weights[j];
		}

		return RetargetMath.Clamp(sum, 0, 1);
	}

	private bool IsSourceLegBone(int boneIndex, string side)
	{
		var prefix = side.ToUpperInvariant() + "_";
		var current = boneIndex;
		for (var hops = 0; current >= 0 && current < _sourceNames.Length && hops <= RetargetConstants.MaxSourceAncestorHops; hops++)
		{
			var text = _sourceNames[current];
			if (text.StartsWith(RetargetConstants.SourceBonePrefix, StringComparison.Ordinal))
				text = text[RetargetConstants.SourceBonePrefix.Length..];
			if (text.StartsWith(prefix, StringComparison.Ordinal))
			{
				var tail = text[prefix.Length..];
				if (tail.StartsWith("Thigh", StringComparison.Ordinal) || tail.StartsWith("Calf", StringComparison.Ordinal)
					|| tail.StartsWith("Foot", StringComparison.Ordinal) || tail.StartsWith("Toe", StringComparison.Ordinal))
					return true;
			}

			// 认出任何一根已知的躯干/另一侧骨就停：它的祖先不可能再是这条腿。
			if (text is "Pelvis" || text.StartsWith("Spine", StringComparison.Ordinal))
				return false;
			current = _sourceParents[current];
		}

		return false;
	}

	private static bool IsLegBoneOf(string hd2Name, string side) =>
		hd2Name is var n && (n == $"{side}_thigh" || n == $"{side}_knee" || n == $"{side}_foot" || n == $"{side}_ball" || n == $"{side}_toe");
}
