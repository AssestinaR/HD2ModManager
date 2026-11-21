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

	[Fact]
	public void ProportionalMode_SplitsTheLiftByLength()
	{
		var thigh = Hip - Knee;
		var calf = Knee - Ankle;
		Assert.Equal(0.09 * thigh / (thigh + calf), Retargeter.LegKneeLift(LegRefitMode.Proportional, Hip, Knee, Ankle, 0.09, 0.0), 12);
	}
}
