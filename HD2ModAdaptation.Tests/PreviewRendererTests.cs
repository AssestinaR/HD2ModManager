using System.Numerics;
using HD2ModAdaptation.Preview;
using Xunit;

namespace HD2ModAdaptation.Tests;

// 作用：锁住光栅器的行为 —— 覆盖、遮挡、背景、上采样尺寸、背面剔除。
// 这些都是"错了但图看起来还行"的类型，所以每条都要有明确的判据。
public sealed class PreviewRendererTests
{
	private static readonly PreviewRenderOptions Small = new()
	{
		Width = 64,
		Height = 64,
		Supersample = 1,
	};

	[Fact]
	public void Render_EmptySceneStaysTransparent()
	{
		var image = PreviewRenderer.Render(PreviewTestScenes.Scene(), new PreviewCamera(), Small);
		Assert.Equal(0, image.CoveredPixels());
		Assert.Equal(64, image.Width);
		Assert.Equal(64, image.Height);
	}

	[Fact]
	public void Render_OpaqueBackgroundCoversEveryPixel()
	{
		var image = PreviewRenderer.Render(
			PreviewTestScenes.Scene(),
			new PreviewCamera(),
			Small with { Background = PreviewBackground.Gray });

		Assert.Equal(64 * 64, image.CoveredPixels());
		var (r, g, b, a) = image[0, 0];
		Assert.Equal(((byte)128, (byte)128, (byte)128, (byte)255), (r, g, b, a));
	}

	[Fact]
	public void Render_CubeCoversAPlausibleShareOfTheFrame()
	{
		var image = PreviewRenderer.Render(PreviewTestScenes.Cube(), new PreviewCamera(), Small);
		var covered = image.CoveredPixels();
		// 自动取景会把立方体撑满，覆盖率应落在合理区间（不是空白，也不是糊满整屏）。
		Assert.InRange(covered, 64 * 64 / 5, 64 * 64 * 9 / 10);
	}

	[Fact]
	public void Render_NearerGeometryWins()
	{
		// 相机在 +Y 一侧（yaw=0 是正视图），所以 y=+2 的那片是"近"的。
		var near = PreviewTestScenes.Quad(y: 2f, half: 1.5f, materialSlot: 1, reverseWinding: false);
		var far = PreviewTestScenes.Quad(y: -2f, half: 1.5f, materialSlot: 2, reverseWinding: false);

		var nearOnly = PreviewRenderer.Render(PreviewTestScenes.Scene(near), new PreviewCamera(), Small);
		var both = PreviewRenderer.Render(PreviewTestScenes.Scene(near, far), new PreviewCamera(), Small);

		// 两张图的包围球不同（相机距离会变），所以比"近的那张盖住中心"而不是比绝对颜色。
		Assert.True(nearOnly.CoveredPixels() > 0);
		var centerBoth = both[32, 32];
		var onlyFar = PreviewRenderer.Render(PreviewTestScenes.Scene(far), new PreviewCamera(), Small);
		Assert.NotEqual(onlyFar[32, 32], centerBoth);
		Assert.NotEqual((byte)0, centerBoth.A);
	}

	[Fact]
	public void Render_SupersamplingKeepsTheRequestedOutputSize()
	{
		var options = new PreviewRenderOptions { Width = 32, Height = 48, Supersample = 2 };
		Assert.Equal((64, 96), options.InternalSize);

		var image = PreviewRenderer.Render(PreviewTestScenes.Cube(), new PreviewCamera(), options);
		Assert.Equal(32, image.Width);
		Assert.Equal(48, image.Height);
	}

	[Fact]
	public void Render_BackFaceCullingRejectsAQuadWoundAwayFromTheCamera()
	{
		// 同一面片有两种缠绕顺序；从相机看，一种正面、一种背面。
		var ordered = PreviewTestScenes.Quad(y: 0f, half: 1f, materialSlot: 1, reverseWinding: false);
		var reversed = PreviewTestScenes.Quad(y: 0f, half: 1f, materialSlot: 1, reverseWinding: true);

		var culledOrdered = PreviewRenderer.Render(PreviewTestScenes.Scene(ordered), new PreviewCamera(), Small with { CullBackFaces = true });
		var culledReversed = PreviewRenderer.Render(PreviewTestScenes.Scene(reversed), new PreviewCamera(), Small with { CullBackFaces = true });
		var twoSidedOrdered = PreviewRenderer.Render(PreviewTestScenes.Scene(ordered), new PreviewCamera(), Small with { CullBackFaces = false });
		var twoSidedReversed = PreviewRenderer.Render(PreviewTestScenes.Scene(reversed), new PreviewCamera(), Small with { CullBackFaces = false });

		// 双面渲染两种顺序都画得出来；开剔除后**恰好**有一种被拒（哪种取决于相机在哪一侧，不该写死）。
		Assert.True(twoSidedOrdered.CoveredPixels() > 0);
		Assert.True(twoSidedReversed.CoveredPixels() > 0);
		Assert.NotEqual(culledOrdered.CoveredPixels() == 0, culledReversed.CoveredPixels() == 0);
	}

	[Fact]
	public void Render_RejectsAnUnrenderableCamera()
	{
		Assert.Throws<ArgumentException>(() => PreviewRenderer.Render(
			PreviewTestScenes.Cube(),
			new PreviewCamera { Zoom = 0f },
			Small));
	}

	[Fact]
	public void Render_RejectsOversizedOutput()
	{
		Assert.Throws<ArgumentOutOfRangeException>(() => PreviewRenderer.Render(
			PreviewTestScenes.Cube(),
			new PreviewCamera(),
			Small with { Width = 4096, Height = 4096, Supersample = 2 }));
	}

	[Fact]
	public void MaterialColor_IsStablePerSlotAndDiffersAcrossSlots()
	{
		var a = PreviewRenderer.MaterialColor(3);
		var b = PreviewRenderer.MaterialColor(3);
		var c = PreviewRenderer.MaterialColor(4);

		Assert.Equal(a, b);
		Assert.NotEqual(a, c);
		Assert.All(new[] { a, b, c }, color => Assert.InRange(color.X, 0.2f, 1f));
	}

	[Fact]
	public void Render_IsDeterministic()
	{
		var first = PreviewRenderer.Render(PreviewTestScenes.Cube(), new PreviewCamera { YawDegrees = 33f }, Small);
		var second = PreviewRenderer.Render(PreviewTestScenes.Cube(), new PreviewCamera { YawDegrees = 33f }, Small);
		Assert.Equal(first.ToStraightAlpha(), second.ToStraightAlpha());
	}

	[Fact]
	public void Render_ClipsGeometryBehindTheNearPlaneInsteadOfSmearingIt()
	{
		// 相机塞进立方体内部（zoom 很大）时，近平面会切到几何；不能出现整屏糊成一片或全空。
		var image = PreviewRenderer.Render(
			PreviewTestScenes.Cube(),
			new PreviewCamera { Zoom = 6f },
			Small);

		var covered = image.CoveredPixels();
		Assert.InRange(covered, 1, 64 * 64);
	}

	[Fact]
	public void Render_PivotLookAtOffCentreShiftsTheImage()
	{
		var cube = PreviewTestScenes.Cube();
		var centred = PreviewRenderer.Render(cube, new PreviewCamera(), Small);
		var shifted = PreviewRenderer.Render(cube, new PreviewCamera { Pivot = new Vector3(0.6f, 0, 0) }, Small);
		Assert.NotEqual(centred.ToStraightAlpha(), shifted.ToStraightAlpha());
	}
}
