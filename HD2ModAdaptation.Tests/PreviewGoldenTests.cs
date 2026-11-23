using System.Security.Cryptography;
using HD2ModAdaptation.Preview;
using Xunit;

namespace HD2ModAdaptation.Tests;

// 作用：渲染结果的**回归锁**。合成立方体 + 固定相机 + 固定设置 → 像素的 SHA256 必须与记录的常量一致。
//
// 说明：常量是 2026-11-23 首次实现的实际输出。它锁的是"以后别悄悄变"，
// 不是在证明"这个画法是对的"——画法对不对要人看图。任何**有意**的着色/采样改动都要同步更新常量，
// 否则这条测试会变成障碍而不是护栏。
// Purpose: Pixel-hash regression lock. The constants are the first implementation's actual output;
// they guard against silent change, not correctness. Intentional shading changes must update them.
public sealed class PreviewGoldenTests
{
	private static readonly PreviewRenderOptions Options = new()
	{
		Width = 96,
		Height = 96,
		Supersample = 2,
		Background = PreviewBackground.Gray,
	};

	[Fact]
	public void Golden_FrontViewIsStable()
		=> AssertGolden("58fc7b02b99bb0d311ba089180fc0c601b3c02a6915e1ac8810683b7bfe7358f", new PreviewCamera());

	[Fact]
	public void Golden_LeftViewIsStable()
		=> AssertGolden("dad13639ec26b5bf15911f7c0fd5ef33c070fc73b00c1f1d15196eb6f01362c2", new PreviewCamera { YawDegrees = 270 });

	[Fact]
	public void Golden_BackViewIsStable()
		=> AssertGolden("e4fb431166ec30a56577ff45a1820a0da68706673a82a1a27d898e7ca1f9a3a8", new PreviewCamera { YawDegrees = 180 });

	[Fact]
	public void Golden_IsometricViewIsStable()
		=> AssertGolden("faa879bb72577b91270d69c1e600be55ddf5a25845af20790af7231caf302f5a", new PreviewCamera { YawDegrees = 45, PitchDegrees = 35.26f });

	private static void AssertGolden(string expected, PreviewCamera camera)
	{
		var image = PreviewRenderer.Render(PreviewTestScenes.Asymmetric(), camera, Options);
		var actual = Hash(image);
		// 设了就落盘，方便首次实现时把常量抄回来（CI 不设，不会写文件）。
		var path = Environment.GetEnvironmentVariable("HD2_PREVIEW_GOLDEN_OUT");
		if (!string.IsNullOrWhiteSpace(path))
		{
			Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
			File.AppendAllText(path, $"{camera.YawDegrees:0.##}/{camera.PitchDegrees:0.##} {actual}{Environment.NewLine}");
		}
		Assert.Equal(expected, actual);
	}

	private static string Hash(PreviewImage image)
		=> Convert.ToHexString(SHA256.HashData(image.ToStraightAlpha())).ToLowerInvariant();
}
