using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2SourceModel;
using static HD2SourceMigration.VpkConversion.ForearmWeightReweight;

namespace HD2SourceMigration.VpkConversion;

/// <summary>在绑定肩部重分配上臂与躯干的权重，位置和绑定矩阵不参与修改。</summary>
internal sealed class ShoulderWeights(UnitTransformInfo rig)
{
	private sealed record Side(uint Arm, uint Twist, uint Elbow, uint[] Torso, Vec3 Head, double Outward);

	private static readonly uint[] Trunk = [Hd2NameHash.Of("chest"), Hd2NameHash.Of("spine"), Hd2NameHash.Of("spine1"), Hd2NameHash.Of("spine2")];
	private readonly Side[] _sides = new[] { Find(rig, "l", -1), Find(rig, "r", 1) }.OfType<Side>().ToArray();

	private static Side? Find(UnitTransformInfo rig, string prefix, double outward)
	{
		var arm = Hd2NameHash.Of($"{prefix}_shoulder");
		var clavicle = Hd2NameHash.Of($"{prefix}_clavicle");
		if (!TryHead(rig, arm, out var head) || !TryHead(rig, clavicle, out _))
			return null;

		return new Side(arm, Hd2NameHash.Of($"{prefix}_shoulder_twist"), Hd2NameHash.Of($"{prefix}_elbow"),
			[clavicle, .. Trunk], head, outward);
	}

	internal void Apply(Dictionary<uint, double> weights, Vec3 point, int maxInfluences)
	{
		foreach (var side in _sides)
		{
			var relative = point - side.Head;
			var local = 1 - SmoothStep(0.09, 0.16, relative.Length);
			if (local <= 0)
				continue;

			var arm = weights.GetValueOrDefault(side.Arm) + weights.GetValueOrDefault(side.Twist);
			var torso = 0.0;
			foreach (var bone in side.Torso)
				torso += weights.GetValueOrDefault(bone);
			var elbow = weights.GetValueOrDefault(side.Elbow);
			var total = arm + torso;
			if (total <= 0)
				continue;

			// 用绑定姿势的横向坐标定交接，避免斜下的上臂轴把腋下过早划到上臂一侧。
			// 少量其它影响连续降低修正强度，避免在有无辅助骨权重的边界上制造新的折线。
			var known = total + elbow;
			var blend = 0.65 * local * SmoothStep(0.70, 0.97, known / weights.Values.Sum())
				* (1 - SmoothStep(0.03, 0.22, elbow / known));
			var move = ((total * ArmFraction(relative.X * side.Outward)) - arm) * blend;
			if (Math.Abs(move) <= 1e-9)
				continue;

			// 接收骨固定为原生肩骨或锁骨。邻点即使有不同的少量胸骨权重，去向也是连续的。
			var receiver = move > 0 ? side.Arm : side.Torso[0];
			if (weights.Count >= maxInfluences && !weights.ContainsKey(receiver))
				continue;

			if (move > 0)
				Rescale(weights, side.Torso, 1 - (move / torso));
			else
				Rescale(weights, [side.Arm, side.Twist], 1 + (move / arm));
			weights[receiver] = weights.GetValueOrDefault(receiver) + Math.Abs(move);
		}
	}

	internal static double ArmFraction(double outward) => Math.Clamp((outward - 0.03 + 0.16) / 0.32, 0, 1);

	private static void Rescale(Dictionary<uint, double> weights, ReadOnlySpan<uint> bones, double scale)
	{
		foreach (var bone in bones)
		{
			if (weights.TryGetValue(bone, out var weight))
				weights[bone] = weight * scale;
		}
	}
}
