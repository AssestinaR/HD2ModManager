using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2SourceModel;
using static HD2SourceMigration.VpkConversion.ForearmWeightReweight;

namespace HD2SourceMigration.VpkConversion;

/// <summary>只修改出货权重，保留绑定姿势的网格和骨架。</summary>
internal sealed class JointWeights(UnitTransformInfo rig, bool shoulderTwist, bool kneeBlend, IReadOnlyList<uint> depthAdjustedKnees)
{
	private sealed record Shoulder(uint Bone, uint Twist, Vec3 Head, Vec3 ToElbow);
	private sealed record Knee(uint Thigh, uint Calf, Vec3 Head, Vec3 Axis, Vec3 Forward, bool Asymmetric);

	private static readonly string[] Sides = ["l", "r"];
	private readonly Shoulder[] _shoulders = shoulderTwist ? Sides.Select(side => FindShoulder(rig, side)).OfType<Shoulder>().ToArray() : [];
	private readonly Knee[] _knees = kneeBlend ? Sides.Select(side => FindKnee(rig, side, depthAdjustedKnees)).OfType<Knee>().ToArray() : [];

	private static Shoulder? FindShoulder(UnitTransformInfo rig, string side)
	{
		var bone = Hd2NameHash.Of($"{side}_shoulder");
		var twist = Hd2NameHash.Of($"{side}_shoulder_twist");
		if (!TryHead(rig, bone, out var head) || !TryHead(rig, twist, out _)
			|| !TryHead(rig, Hd2NameHash.Of($"{side}_elbow"), out var elbow)
			|| (elbow - head).Length <= 1e-9)
			return null;

		return new Shoulder(bone, twist, head, elbow - head);
	}

	private static Knee? FindKnee(UnitTransformInfo rig, string side, IReadOnlyList<uint> depthAdjustedKnees)
	{
		var thigh = Hd2NameHash.Of($"{side}_thigh");
		var calf = Hd2NameHash.Of($"{side}_knee");
		if (!TryHead(rig, thigh, out var hip) || !TryHead(rig, calf, out var head)
			|| !TryHead(rig, Hd2NameHash.Of($"{side}_foot"), out var ankle)
			|| (ankle - hip).Length <= 1e-9)
			return null;

		var axis = (ankle - hip).Normalized();
		// HD2 的 +Y 朝前，投影到腿的横截面，左右腿对称使用。
		var forward = (new Vec3(0, 1, 0) - (axis * axis.Y)).Normalized();
		return new Knee(thigh, calf, head, axis, forward, depthAdjustedKnees.Contains(calf));
	}

	internal void Apply(Dictionary<uint, double> weights, Vec3 point, int maxInfluences)
	{
		foreach (var shoulder in _shoulders)
		{
			var original = weights.GetValueOrDefault(shoulder.Bone);
			if (original <= 0 || (weights.Count >= maxInfluences && !weights.ContainsKey(shoulder.Twist)))
				continue;

			var relative = point - shoulder.Head;
			var axis = shoulder.ToElbow;
			var t = Vec3.Dot(relative, axis) / Vec3.Dot(axis, axis);
			var radius = (relative - (axis * t)).Length;
			// 肩球到上臂中段逐渐交给扭转骨，接近肘和远离上臂轴时交回肩骨。
			var fraction = 0.9 * SmoothStep(-0.32, 0.24, t) * (1 - SmoothStep(0.58, 1.0, t))
				* (1 - SmoothStep(0.09, 0.15, radius));
			if (fraction <= 0)
				continue;

			var moved = original * fraction;
			weights[shoulder.Bone] = original - moved;
			weights[shoulder.Twist] = weights.GetValueOrDefault(shoulder.Twist) + moved;
		}

		foreach (var knee in _knees)
		{
			var thigh = weights.GetValueOrDefault(knee.Thigh);
			var calf = weights.GetValueOrDefault(knee.Calf);
			var total = thigh + calf;
			if (total <= 0 || (weights.Count >= maxInfluences && (thigh == 0 || calf == 0)))
				continue;

			var relative = point - knee.Head;
			var axial = Vec3.Dot(relative, knee.Axis);
			var radius = (relative - (knee.Axis * axial)).Length;
			// 带外平滑回到原权重，在腿以外衰减并保留挂在其它骨头上的部件。
			const double symmetricHalfWidth = 0.11;
			var blend = (1 - SmoothStep(symmetricHalfWidth, 0.16, Math.Abs(axial))) * (1 - SmoothStep(0.13, 0.18, radius));
			if (knee.Asymmetric)
				blend *= SmoothStep(0.70, 0.95, total);
			if (blend <= 0)
				continue;

			// 截面已经对齐或采样不足时，保留原来以枢轴为中心的宽过渡。
			var target = knee.Asymmetric
				? KneeCalfFraction(axial, Vec3.Dot(relative, knee.Forward))
				: Math.Clamp((axial + symmetricHalfWidth) / (2 * symmetricHalfWidth), 0, 1);
			var calfWeight = calf + (((total * target) - calf) * blend);
			SetWeight(weights, knee.Thigh, total - calfWeight);
			SetWeight(weights, knee.Calf, calfWeight);
		}
	}

	internal static double KneeCalfFraction(double axial, double forward)
	{
		// 膝窝用宽过渡来收拢。膝前的过渡较窄，并向大腿偏移，让髌骨附近更多跟随小腿。
		var front = SmoothStep(-0.065, 0.025, forward);
		var halfWidth = 0.115 - (0.035 * front);
		var center = -0.020 * front;
		return Math.Clamp((axial - center + halfWidth) / (2 * halfWidth), 0, 1);
	}

	private static void SetWeight(Dictionary<uint, double> weights, uint bone, double weight)
	{
		if (weight > 0)
			weights[bone] = weight;
		else
			weights.Remove(bone);
	}
}
