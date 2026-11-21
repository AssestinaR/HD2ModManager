using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2SourceMigration.Retarget;
using HD2SourceMigration.VpkConversion;
using HD2SourceModel;

namespace HD2SourceMigration.Tests;

public sealed class JointWeightsTests
{
	private static readonly uint Shoulder = Hd2NameHash.Of("l_shoulder");
	private static readonly uint Twist = Hd2NameHash.Of("l_shoulder_twist");
	private static readonly uint Thigh = Hd2NameHash.Of("l_thigh");
	private static readonly uint Knee = Hd2NameHash.Of("l_knee");
	private const uint Other = 1;

	private static UnitTransformInfo Rig(params (string Name, float X, float Z)[] bones) =>
		UnitTransformInfo.Empty with
		{
			NameHashes = bones.Select(bone => Hd2NameHash.Of(bone.Name)).ToArray(),
			Matrices = bones.Select(bone => new UnitTransformMatrix(
				[1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, bone.X, 0, bone.Z, 1])).ToArray(),
		};

	[Fact]
	public void Shoulder_SplitsOnlyTheShoulderInfluence()
	{
		var rig = Rig(("l_shoulder", 0, 1), ("l_shoulder_twist", 0.1f, 1), ("l_elbow", 0.25f, 1));
		var reweight = new JointWeights(rig, shoulderTwist: true, kneeBlend: false, []);
		var weights = new Dictionary<uint, double> { [Shoulder] = 0.8, [Other] = 0.2 };

		reweight.Apply(weights, new Vec3(0.1, 0, 1), 4);

		Assert.Equal(0.72, weights[Twist], 12);
		Assert.Equal(0.08, weights[Shoulder], 12);
		Assert.Equal(0.2, weights[Other]);
		Assert.Equal(1, weights.Values.Sum(), 12);
	}

	[Theory]
	[InlineData(false, 0, 4)]
	[InlineData(true, 0.20, 4)]
	[InlineData(true, 0, 2)]
	public void Shoulder_SkipsMissingBoneDistantPointsAndFullWeights(bool hasTwist, double radius, int limit)
	{
		var rig = hasTwist
			? Rig(("l_shoulder", 0, 1), ("l_shoulder_twist", 0.1f, 1), ("l_elbow", 0.25f, 1))
			: Rig(("l_shoulder", 0, 1), ("l_elbow", 0.25f, 1));
		var weights = new Dictionary<uint, double> { [Shoulder] = 0.8, [Other] = 0.2 };
		new JointWeights(rig, shoulderTwist: true, kneeBlend: false, [])
			.Apply(weights, new Vec3(0.1, radius, 1), limit);

		Assert.DoesNotContain(Twist, weights.Keys);
		Assert.Equal(0.8, weights[Shoulder]);
		Assert.Equal(0.2, weights[Other]);
	}

	[Theory]
	[InlineData(0.5075, 0.25)]
	[InlineData(0.45, 0.5)]
	[InlineData(0.3925, 0.75)]
	public void Knee_PreservesTheWideTransitionBehindTheShippedPivot(double height, double calfFraction)
	{
		var rig = Rig(("l_thigh", 0, 0.9f), ("l_knee", 0, 0.45f), ("l_foot", 0, 0));
		var weights = new Dictionary<uint, double> { [Thigh] = 0.98, [Other] = 0.02 };

		new JointWeights(rig, shoulderTwist: false, kneeBlend: true, [Knee])
			.Apply(weights, new Vec3(0, -0.08, height), 4);

		Assert.Equal(0.98 * calfFraction, weights[Knee], 6);
		Assert.Equal(0.98 * (1 - calfFraction), weights[Thigh], 6);
		Assert.Equal(0.02, weights[Other]);
		Assert.Equal(1, weights.Values.Sum(), 12);
	}

	[Fact]
	public void Knee_AnteriorTransitionIsNarrowerAndFavoursTheCalfAtThePivot()
	{
		Assert.Equal(0.625, JointWeights.KneeCalfFraction(0, 0.04), 12);
		Assert.Equal(0.5, JointWeights.KneeCalfFraction(0, -0.08), 12);
		Assert.Equal(1, JointWeights.KneeCalfFraction(0.08, 0.04), 12);
		Assert.InRange(JointWeights.KneeCalfFraction(0.08, -0.08), 0.8, 0.9);
	}

	[Theory]
	[InlineData(false, 0.45, 4)]
	[InlineData(true, 0.20, 4)]
	[InlineData(true, 0.45, 2)]
	public void Knee_SkipsDisabledDistantAndFullWeights(bool enabled, double height, int limit)
	{
		var rig = Rig(("l_thigh", 0, 0.9f), ("l_knee", 0, 0.45f), ("l_foot", 0, 0));
		var weights = new Dictionary<uint, double> { [Thigh] = 0.98, [Other] = 0.02 };
		new JointWeights(rig, shoulderTwist: false, kneeBlend: enabled, [Knee]).Apply(weights, new Vec3(0, 0, height), limit);

		Assert.DoesNotContain(Knee, weights.Keys);
		Assert.Equal(0.98, weights[Thigh]);
	}

	[Fact]
	public void Knee_IgnoresAccessoriesWeaklyBoundToTheLeg()
	{
		var rig = Rig(("l_thigh", 0, 0.9f), ("l_knee", 0, 0.45f), ("l_foot", 0, 0));
		var weights = new Dictionary<uint, double> { [Thigh] = 0.3, [Knee] = 0.1, [Other] = 0.6 };
		new JointWeights(rig, shoulderTwist: false, kneeBlend: true, [Knee]).Apply(weights, new Vec3(0, 0.03, 0.45), 4);

		Assert.Equal(0.3, weights[Thigh]);
		Assert.Equal(0.1, weights[Knee]);
		Assert.Equal(0.6, weights[Other]);
	}

	[Theory]
	[InlineData(0.08)]
	[InlineData(-0.08)]
	public void Knee_KeepsTheOriginalSymmetricWeightsWhenDepthDoesNotChange(double forward)
	{
		var rig = Rig(("l_thigh", 0, 0.9f), ("l_knee", 0, 0.45f), ("l_foot", 0, 0));
		var weights = new Dictionary<uint, double> { [Thigh] = 0.8, [Other] = 0.2 };
		new JointWeights(rig, shoulderTwist: false, kneeBlend: true, [])
			.Apply(weights, new Vec3(0, forward, 0.505), 4);

		Assert.Equal(0.2, weights[Knee], 6);
		Assert.Equal(0.6, weights[Thigh], 6);
		Assert.Equal(0.2, weights[Other]);
	}

	[Fact]
	public void Knee_DepthUsesTheActualSectionIntersection()
	{
		var a = new Vec3(-1, 1, -1);
		var b = new Vec3(1, 3, -1);
		var c = new Vec3(0, 4, 1);
		Assert.Equal(3, Retargeter.KneeSectionDepth(a, b, c));
		Assert.Null(Retargeter.KneeSectionDepth(a, b, new Vec3(1, 2, -2)));
	}

	[Theory]
	[InlineData(-0.097, 0.034, 0.0165)]
	[InlineData(-0.125, -0.044, 0.035)]
	[InlineData(-0.097, 0.076, 0)]
	[InlineData(-0.05, -0.048, 0)]
	[InlineData(double.PositiveInfinity, double.NegativeInfinity, 0)]
	public void Knee_DepthCorrectionIsBoundedAndRequiresACoherentSection(double back, double front, double correction)
	{
		Assert.Equal(correction, Retargeter.KneeDepthCorrection(back, front), 12);
	}

	[Theory]
	[InlineData(1.05, 1, 0)]
	[InlineData(0.9, 1, 0)]
	[InlineData(0.5, 1, 0.035)]
	[InlineData(0.25, 1, 0.035)]
	[InlineData(-0.1, 1, 0.035)]
	[InlineData(0.7, 1, 0.0175)]
	[InlineData(0.7, 0.4, 0.007)]
	[InlineData(0.25, 0.4, 0.014)]
	[InlineData(0.25, 0, 0)]
	public void Knee_DepthShiftMovesTheEntireLowerLegAndScalesByInfluence(double height, double influence, double expected)
	{
		Assert.Equal(expected, Retargeter.KneeDepthShift(0.035, height, 0.9, 0.5, influence), 12);
	}

	[Fact]
	public void Knee_DepthShiftIncreasesMonotonicallyFromHipToKnee()
	{
		var prior = Retargeter.KneeDepthShift(0.035, 0.9, 0.9, 0.5, 1);
		for (var step = 1; step <= 20; step++)
		{
			var shift = Retargeter.KneeDepthShift(0.035, 0.9 - (step * 0.02), 0.9, 0.5, 1);
			Assert.True(shift > prior);
			prior = shift;
		}
	}
}
