using HD2SourceMigration.Retarget;

namespace HD2SourceMigration.Tests;

/// <summary>
/// 上臂长度补偿（3b）对<b>非手臂</b>顶点的闸门。
/// <para>
/// 回归背景（2026-11-21）：旧规则只钉住"肩关节内侧"的非手臂顶点。源模型是 A-pose（上臂下垂 36°–57°）时，
/// 脚、腿、髋都落在上臂轴的远端一侧，被当成"肘以外"整体平移——左右两遍叠加，脚底多陷 12–18 cm。
/// 新规则：非手臂顶点只按"本侧手臂权重"或"离上臂段的近距离"跟随。
/// </para>
/// </summary>
public sealed class ArmStretchGateTests
{
	[Fact]
	public void FootFarAlongADroopingArmAxis_IsPinned()
	{
		// A-pose 下脚在上臂轴上投影远大于上臂长度：旧规则给 1（整体平移），新规则必须是 0。
		Assert.Equal(0.0, Retargeter.NonArmStretch(ramp: 1.0, along: 1.2, armInfluence: 0.0, distanceToUpperArm: 1.1));
	}

	[Fact]
	public void HipBesideTheHangingArm_IsPinned()
	{
		// 髋离下垂上臂段约 0.2 m 以上：不应被手臂补偿拖动。
		Assert.Equal(0.0, Retargeter.NonArmStretch(ramp: 0.9, along: 0.25, armInfluence: 0.0, distanceToUpperArm: 0.2));
	}

	[Fact]
	public void ArmpitSkinWithArmWeight_FollowsInProportion()
	{
		Assert.Equal(0.3 * 0.4, Retargeter.NonArmStretch(ramp: 0.3, along: 0.08, armInfluence: 0.4, distanceToUpperArm: 0.5), 12);
	}

	[Fact]
	public void SeamVertexTouchingTheUpperArm_FollowsFully_EvenWithoutArmWeight()
	{
		// 纯脊柱权重、但贴着上臂的腋下顶点：钉住会撕开肩缝。
		Assert.Equal(0.25, Retargeter.NonArmStretch(ramp: 0.25, along: 0.07, armInfluence: 0.0, distanceToUpperArm: 0.02), 12);
	}

	[Fact]
	public void InsideTheShoulderJoint_IsAlwaysPinned()
	{
		Assert.Equal(0.0, Retargeter.NonArmStretch(ramp: 0.0, along: -0.05, armInfluence: 1.0, distanceToUpperArm: 0.0));
	}

	[Fact]
	public void SeamFeather_IsMonotonicBetweenNearAndFar()
	{
		var previous = double.MaxValue;
		for (var d = 0.0; d <= 0.2; d += 0.01)
		{
			var value = Retargeter.NonArmStretch(1.0, 0.1, 0.0, d);
			Assert.InRange(value, 0.0, 1.0);
			Assert.True(value <= previous + 1e-12);
			previous = value;
		}
	}
}
