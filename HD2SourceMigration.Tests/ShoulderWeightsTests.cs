using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2SourceMigration.VpkConversion;
using HD2SourceModel;

namespace HD2SourceMigration.Tests;

public sealed class ShoulderWeightsTests
{
	private static readonly uint Chest = Hd2NameHash.Of("chest");
	private static readonly uint Hair = Hd2NameHash.Of("hair");

	private static UnitTransformInfo Rig(string side, float x, bool withClavicle = true) =>
		UnitTransformInfo.Empty with
		{
			NameHashes = withClavicle
				? [Hd2NameHash.Of($"{side}_shoulder"), Hd2NameHash.Of($"{side}_clavicle")]
				: [Hd2NameHash.Of($"{side}_shoulder")],
			Matrices = [
				new UnitTransformMatrix([1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, x, 0, 1, 1]),
				new UnitTransformMatrix([1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 1, 1]),
			],
		};

	[Theory]
	[InlineData(-0.15, 0)]
	[InlineData(0.03, 0.5)]
	[InlineData(0.10, 0.71875)]
	[InlineData(0.20, 1)]
	public void ArmFraction_HasBoundedLateralTransition(double outward, double expected)
	{
		Assert.Equal(expected, ShoulderWeights.ArmFraction(outward), 12);
	}

	[Theory]
	[InlineData("l", -1)]
	[InlineData("r", 1)]
	public void Apply_MirrorsTheTransitionAndTransfersFromArmToClavicle(string side, int sign)
	{
		var arm = Hd2NameHash.Of($"{side}_shoulder");
		var twist = Hd2NameHash.Of($"{side}_shoulder_twist");
		var clavicle = Hd2NameHash.Of($"{side}_clavicle");
		var weights = new Dictionary<uint, double> { [arm] = 0.6, [twist] = 0.2, [clavicle] = 0.15, [Chest] = 0.05 };

		new ShoulderWeights(Rig(side, sign * 0.25f)).Apply(weights, new Vec3(sign * 0.28, 0, 1), 4);

		Assert.Equal(0.45375, weights[arm], 12);
		Assert.Equal(0.15125, weights[twist], 12);
		Assert.Equal(0.345, weights[clavicle], 12);
		Assert.Equal(0.05, weights[Chest], 12);
		Assert.Equal(1, weights.Values.Sum(), 12);
	}

	[Fact]
	public void Apply_UsesClavicleForAnArmVertexWithNoTorsoWeight()
	{
		var arm = Hd2NameHash.Of("l_shoulder");
		var clavicle = Hd2NameHash.Of("l_clavicle");
		var weights = new Dictionary<uint, double> { [arm] = 1 };
		new ShoulderWeights(Rig("l", -0.25f)).Apply(weights, new Vec3(-0.25, 0, 1), 4);

		Assert.Equal(0.6140625, weights[arm], 12);
		Assert.Equal(0.3859375, weights[clavicle], 12);
	}

	[Theory]
	[InlineData(0.17, 4, true, false)]
	[InlineData(0, 1, true, false)]
	[InlineData(0, 4, false, false)]
	[InlineData(0, 4, true, true)]
	public void Apply_LeavesDistantVerticesAndProtectedInfluencesUntouched(double away, int slots, bool hasClavicle, bool hasHair)
	{
		var arm = Hd2NameHash.Of("l_shoulder");
		var weights = new Dictionary<uint, double> { [arm] = hasHair ? 0.4 : 1 };
		if (hasHair)
			weights[Hair] = 0.6;
		var original = weights.ToArray();

		new ShoulderWeights(Rig("l", -0.25f, hasClavicle)).Apply(weights, new Vec3(-0.25, away, 1), slots);

		Assert.Equal(original, weights.ToArray());
	}

	[Fact]
	public void Apply_CanTransferToTheArmWithoutTakingWeightFromAnAuxiliaryBone()
	{
		var arm = Hd2NameHash.Of("l_shoulder");
		var weights = new Dictionary<uint, double> { [Chest] = 0.99, [Hair] = 0.01 };
		new ShoulderWeights(Rig("l", -0.25f)).Apply(weights, new Vec3(-0.28, 0, 1), 4);

		Assert.Equal(0.01, weights[Hair]);
		Assert.Equal(0.32175, weights[arm], 12);
		Assert.Equal(1, weights.Values.Sum(), 12);
	}
}
