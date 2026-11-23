using System.Numerics;
using HD2ModAdaptation.Preview;
using Xunit;

namespace HD2ModAdaptation.Tests;

// 作用：锁住相机的量化与取景规则。量化一旦不严，文件名就不再等于规格（规划 §7 的不变量 1）；
// 取景一旦错，图会"看起来没问题但其实框不住模型"。
public sealed class PreviewCameraTests
{
	[Fact]
	public void Quantize_NormalizesNegativeZeroAndWrapsAngles()
	{
		var camera = new PreviewCamera
		{
			Pivot = new Vector3(-0.0004f, 0.0004f, 0f),
			YawDegrees = -30f,
			PitchDegrees = 120f,
			RollDegrees = 200f,
			Zoom = 1.23456f,
		}.Quantize();

		Assert.Equal(0f, camera.Pivot.X);
		Assert.Equal(0f, camera.Pivot.Y);
		Assert.Equal(330f, camera.YawDegrees);
		Assert.Equal(PreviewCamera.MaximumPitchDegrees, camera.PitchDegrees);
		Assert.Equal(-160f, camera.RollDegrees);
		Assert.Equal(1.23f, camera.Zoom);
	}

	[Fact]
	public void Quantize_RoundsToThePrecisionTheFileNameUses()
	{
		var camera = new PreviewCamera { YawDegrees = 37.5049f, PitchDegrees = 20.005f, Zoom = 1.004f }.Quantize();
		Assert.Equal(37.5f, camera.YawDegrees);
		Assert.Equal(20f, camera.PitchDegrees);
		Assert.Equal(1f, camera.Zoom);
	}

	[Fact]
	public void IsRenderable_RejectsNonPositiveZoomAndNonFinitePivot()
	{
		Assert.False((new PreviewCamera { Zoom = 0f }).Quantize().IsRenderable);
		Assert.False((new PreviewCamera { Zoom = -1f }).Quantize().IsRenderable);
		Assert.False((new PreviewCamera { Pivot = new Vector3(float.NaN, 0, 0) }).Quantize().IsRenderable);
		Assert.True(new PreviewCamera().Quantize().IsRenderable);
	}

	[Fact]
	public void Resolve_PutsTheEyeOnThePlusYSideAtYawZeroBecauseTheModelFacesPlusY()
	{
		var bounds = new PreviewBoundingSphere(Vector3.Zero, 1f);
		var setup = new PreviewCamera().Resolve(bounds, 1f);

		Assert.Equal(0f, setup.Eye.X, 4);
		Assert.True(setup.Eye.Y > 1f, $"yaw=0 是正视图 ⇒ 相机应在 +Y 一侧，实际 y={setup.Eye.Y}");
		Assert.Equal(0f, setup.Eye.Z, 4);
		Assert.Equal(1f, Vector3.Dot(setup.Up, Vector3.UnitZ), 3);
	}

	[Fact]
	public void Resolve_PositivePitchLiftsTheEyeAboveTheTarget()
	{
		var bounds = new PreviewBoundingSphere(Vector3.Zero, 1f);
		var setup = new PreviewCamera { PitchDegrees = 45f }.Resolve(bounds, 1f);
		Assert.True(setup.Eye.Z > 0.5f, $"pitch=45 时相机应在上方，实际 z={setup.Eye.Z}");
	}

	[Fact]
	public void Resolve_BasisIsOrthonormal()
	{
		var bounds = new PreviewBoundingSphere(Vector3.Zero, 2f);
		var setup = new PreviewCamera { YawDegrees = 37f, PitchDegrees = 21f, RollDegrees = 13f }.Resolve(bounds, 1.5f);

		Assert.Equal(0f, Vector3.Dot(setup.Forward, setup.Right), 4);
		Assert.Equal(0f, Vector3.Dot(setup.Forward, setup.Up), 4);
		Assert.Equal(0f, Vector3.Dot(setup.Right, setup.Up), 4);
		Assert.Equal(1f, setup.Forward.Length(), 4);
		Assert.Equal(1f, setup.Right.Length(), 4);
		Assert.Equal(1f, setup.Up.Length(), 4);
	}

	[Fact]
	public void Resolve_ZoomPullsTheEyeCloserWithoutChangingTheTarget()
	{
		var bounds = new PreviewBoundingSphere(Vector3.Zero, 1f);
		var normal = new PreviewCamera().Resolve(bounds, 1f);
		var zoomed = new PreviewCamera { Zoom = 2f }.Resolve(bounds, 1f);

		var normalDistance = Vector3.Distance(normal.Eye, normal.Target);
		var zoomedDistance = Vector3.Distance(zoomed.Eye, zoomed.Target);
		Assert.Equal(normalDistance / 2f, zoomedDistance, 3);
		Assert.Equal(normal.Target, zoomed.Target);
	}

	[Fact]
	public void Resolve_PivotShiftsTheLookAtPointByRadiusUnits()
	{
		var bounds = new PreviewBoundingSphere(new Vector3(0, 0, 1), 2f);
		var setup = new PreviewCamera { Pivot = new Vector3(0, 0, 0.5f) }.Resolve(bounds, 1f);
		// pivot 0.5 个半径 = 1 个世界单位，落在包围球中心上方。
		Assert.Equal(2f, setup.Target.Z, 4);
	}

	[Fact]
	public void Resolve_RejectsBadAspectRatio()
	{
		Assert.Throws<ArgumentOutOfRangeException>(() => new PreviewCamera().Resolve(PreviewBoundingSphere.Degenerate, 0f));
	}
}
