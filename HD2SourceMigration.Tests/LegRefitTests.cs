using HD2SourceMigration.Retarget;

namespace HD2SourceMigration.Tests;

/// <summary>4k 腿部重拟合的位移场与膝盖落点。</summary>
public sealed class LegRefitTests
{
	private const double Hip = 0.90;
	private const double Knee = 0.45;
	private const double Ankle = 0.09;

	[Fact]
	public void AboveTheHip_NeverMoves()
	{
		Assert.Equal(0.0, Retargeter.LegLiftAt(1.40, Hip, Knee, Ankle, 0.02, 0.09));
		Assert.Equal(0.0, Retargeter.LegLiftAt(Hip, Hip, Knee, Ankle, 0.02, 0.09));
	}

	[Fact]
	public void BelowTheAnkle_TheWholeFootMovesRigidly()
	{
		Assert.Equal(0.09, Retargeter.LegLiftAt(0.05, Hip, Knee, Ankle, 0.02, 0.09), 12);
		Assert.Equal(0.09, Retargeter.LegLiftAt(-0.09, Hip, Knee, Ankle, 0.02, 0.09), 12);
	}

	[Fact]
	public void LiftIsContinuousAndMonotoneDownTheLeg()
	{
		var previous = 0.0;
		for (var z = 1.0; z >= -0.1; z -= 0.005)
		{
			var lift = Retargeter.LegLiftAt(z, Hip, Knee, Ankle, 0.02, 0.09);
			Assert.True(lift >= previous - 1e-12);
			Assert.True(lift - previous <= 0.002, $"jump at z={z}");
			previous = lift;
		}
	}

	[Fact]
	public void BoneMode_NeverLengthensTheThigh()
	{
		// HD2 膝骨在源膝之下：对齐会拉长大腿，必须夹成 0，全部压缩交给小腿。
		Assert.Equal(0.0, Retargeter.LegKneeLift(LegRefitMode.Bone, Hip, Knee, Ankle, 0.09, kneeHd2: 0.42), 12);
	}

	[Fact]
	public void BoneMode_FollowsTheHd2KneeWhenFeasible()
	{
		Assert.Equal(0.03, Retargeter.LegKneeLift(LegRefitMode.Bone, Hip, Knee, Ankle, 0.09, kneeHd2: 0.48), 12);
	}

	[Fact]
	public void BoneMode_NeverOverCompressesTheCalf()
	{
		// 小腿 0.36 m，最多压到 0.75 倍（0.09 m）；抬升 0.12 时膝至少要抬 0.03。
		Assert.Equal(0.03, Retargeter.LegKneeLift(LegRefitMode.Bone, Hip, Knee, Ankle, 0.12, kneeHd2: 0.40), 12);
	}

	[Theory]
	[InlineData(LegRefitMode.Bone, 0.40)]
	[InlineData(LegRefitMode.Bone, 0.48)]
	[InlineData(LegRefitMode.Proportional, 0.0)]
	public void HugeRequestedLift_IsCapped_SoNoSegmentDropsBelowTheFloor(LegRefitMode mode, double kneeHd2)
	{
		// 审查 CR-001：请求 0.25 m 时旧实现两段都被压到约 0.69。
		var lift = Retargeter.LegLiftCapacity(Hip, Ankle, 0.25);
		Assert.True(lift < 0.25);
		var kneeLift = Retargeter.LegKneeLift(mode, Hip, Knee, Ankle, lift, kneeHd2);
		var thighScale = (Hip - (Knee + kneeLift)) / (Hip - Knee);
		var calfScale = ((Knee + kneeLift) - (Ankle + lift)) / (Knee - Ankle);
		Assert.InRange(thighScale, 0.75 - 1e-12, 1.0 + 1e-12);
		Assert.InRange(calfScale, 0.75 - 1e-12, 1.0 + 1e-12);
	}

	[Fact]
	public void SmallLift_IsNotCapped()
	{
		Assert.Equal(0.09, Retargeter.LegLiftCapacity(Hip, Ankle, 0.09), 12);
	}

	[Fact]
	public void LegBoneOwnership_IsDecidedByTheFirstLegAncestor()
	{
		// 审查 CR-006：交叉父链 R_Foot → L_Calf，辅助骨挂在 R_Foot 下，只能算右脚。
		string[] names = ["ValveBiped.Bip01_Pelvis", "ValveBiped.Bip01_L_Calf", "ValveBiped.Bip01_R_Foot", "source_extra_3", "Skirt_01"];
		int[] parents = [-1, 0, 1, 2, 0];
		Assert.Equal(("r", Retargeter.SourceLegPart.Foot), Retargeter.ClassifySourceLegBone(names, parents, 3));
		Assert.Equal(("l", Retargeter.SourceLegPart.Upper), Retargeter.ClassifySourceLegBone(names, parents, 1));
		Assert.Equal((string.Empty, Retargeter.SourceLegPart.None), Retargeter.ClassifySourceLegBone(names, parents, 4));
	}

	[Fact]
	public void MixedWeightShoe_IsStillSampled_AndLandsOnTheGround()
	{
		// 审查 CR-003 第 2 轮：鞋底 0.9 脚 + 0.1 小腿（腿权重 1.0）。0.95 门槛会整只鞋跳过。
		double[] z = [-0.090, -0.088, 0.30];
		double[] leg = [1.0, 1.0, 1.0];
		double[] foot = [0.9, 0.9, 0.0];
		var needed = Retargeter.SoleLiftNeeded(z, leg, foot, Ankle, 0.0);
		Assert.NotNull(needed);
		Assert.Equal(0.089, needed!.Value, 9);
	}

	[Fact]
	public void PartialLegWeight_IsCompensated_SoTheSoleReachesTheTarget()
	{
		// 腿权重 0.95：实际位移是 lift × 0.95，反求后鞋底恰好到 0，不再剩 4.5 mm。
		double[] z = [-0.090];
		double[] leg = [0.95];
		double[] foot = [0.95];
		var needed = Retargeter.SoleLiftNeeded(z, leg, foot, Ankle, 0.0)!.Value;
		Assert.Equal(0.0, -0.090 + (needed * 0.95), 12);
	}

	[Fact]
	public void CalfAttachmentHangingBelowTheShoe_IsNotTakenAsTheSole()
	{
		double[] z = [-0.200, -0.090];
		double[] leg = [1.0, 1.0];
		double[] foot = [0.0, 1.0];
		Assert.Equal(0.090, Retargeter.SoleLiftNeeded(z, leg, foot, Ankle, 0.0)!.Value, 12);
	}

	[Fact]
	public void NoFootSamples_SkipsTheSide()
	{
		Assert.Null(Retargeter.SoleLiftNeeded([-0.09], [1.0], [0.2], Ankle, 0.0));
	}

	[Fact]
	public void ProportionalMode_SplitsTheLiftByLength()
	{
		var thigh = Hip - Knee;
		var calf = Knee - Ankle;
		Assert.Equal(0.09 * thigh / (thigh + calf), Retargeter.LegKneeLift(LegRefitMode.Proportional, Hip, Knee, Ankle, 0.09, 0.0), 12);
	}
}
