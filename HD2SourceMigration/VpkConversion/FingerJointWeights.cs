using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2SourceMigration.Retarget;
using HD2SourceModel;

namespace HD2SourceMigration.VpkConversion;

/// <summary>Aligns the weight transitions within each long finger to the shipped joint pivots.</summary>
internal sealed class FingerJointWeights
{
	private const double BlendWidth = 0.014;
	private readonly Dictionary<uint, Chain> _chains = [];

	internal FingerJointWeights(IReadOnlyList<FingerPoseRow> fingers, UnitTransformInfo rig)
	{
		foreach (var finger in fingers)
		{
			if (finger.Stem is not ("index" or "middle" or "ring" or "pinky") || finger.Hd2Bones.Count != 3)
				continue;

			var first = Hd2NameHash.Of(finger.Hd2Bones[0]);
			var second = Hd2NameHash.Of(finger.Hd2Bones[1]);
			var third = Hd2NameHash.Of(finger.Hd2Bones[2]);
			if (!ForearmWeightReweight.TryHead(rig, first, out var root)
				|| !ForearmWeightReweight.TryHead(rig, second, out var middle)
				|| !ForearmWeightReweight.TryHead(rig, third, out var distal))
				continue;

			var proximalAxis = (middle - root).Normalized();
			var distalAxis = (distal - middle).Normalized();
			var middleAxis = (proximalAxis + distalAxis).Normalized();
			if (proximalAxis == Vec3.Zero || distalAxis == Vec3.Zero || middleAxis == Vec3.Zero)
				continue;

			var chain = new Chain(first, second, third, middle, distal, middleAxis, distalAxis);
			_chains[first] = chain;
			_chains[second] = chain;
			_chains[third] = chain;
		}
	}

	internal void Apply(Dictionary<uint, double> weights, Vec3 point, int influenceLimit)
	{
		Chain? match = null;
		var count = 0;
		var total = 0.0;
		foreach (var (hash, weight) in weights)
		{
			if (!_chains.TryGetValue(hash, out var chain))
				continue;
			// Vertices shared by multiple fingers keep their authored blend across the chains.
			if (match is not null && match != chain)
				return;
			match = chain;
			count++;
			total += weight;
		}

		if (match is not { } finger || total <= 0.01)
			return;

		// Center each transition on the pivot, where the two adjacent bone transforms agree.
		var middle = Ramp(Vec3.Dot(point - finger.Middle, finger.MiddleAxis));
		var distal = Ramp(Vec3.Dot(point - finger.Distal, finger.DistalAxis));
		var firstWeight = (1 - middle) * total;
		var secondWeight = middle * (1 - distal) * total;
		var thirdWeight = middle * distal * total;
		var newCount = (firstWeight > 0 ? 1 : 0) + (secondWeight > 0 ? 1 : 0) + (thirdWeight > 0 ? 1 : 0);
		if (weights.Count - count + newCount > influenceLimit)
			return;

		Set(weights, finger.First, firstWeight);
		Set(weights, finger.Second, secondWeight);
		Set(weights, finger.Third, thirdWeight);
	}

	private static double Ramp(double distance) => Math.Clamp(0.5 + (distance / BlendWidth), 0, 1);

	private static void Set(Dictionary<uint, double> weights, uint hash, double weight)
	{
		if (weight > 0)
			weights[hash] = weight;
		else
			weights.Remove(hash);
	}

	private sealed record Chain(uint First, uint Second, uint Third, Vec3 Middle, Vec3 Distal, Vec3 MiddleAxis, Vec3 DistalAxis);
}
