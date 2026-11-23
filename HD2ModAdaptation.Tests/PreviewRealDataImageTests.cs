using HD2ModAdaptation.Preview;
using Xunit;
using Xunit.Abstractions;

namespace HD2ModAdaptation.Tests;

// 作用：真数据的出图冒烟 —— 这是 P1 真正的验收：不是"没抛异常"，而是"画出来的东西盖住了画面"。
// 设 HD2_PREVIEW_OUT 时把 PNG 落到该目录，方便人/agent 直接看图。
// Purpose: Real-patch image smoke test. Set HD2_PREVIEW_OUT to dump PNGs for human/agent review.
public sealed class PreviewRealDataImageTests
{
	private readonly ITestOutputHelper _output;

	public PreviewRealDataImageTests(ITestOutputHelper output) => _output = output;

	[Fact]
	public async Task RealPatch_RendersThreeViewsWithVisibleGeometry()
	{
		var paths = PreviewSceneRealDataTests.ParsePatchPaths();
		if (paths.Length == 0) return;

		var scene = await PreviewSceneBuilder.BuildAsync(paths);
		Assert.False(scene.IsEmpty, "真实 patch 应当能构出场景");

		var options = new PreviewRenderOptions
		{
			Width = 512,
			Height = 512,
			Supersample = 2,
			Background = PreviewBackground.Gray,
		};

		var views = new (string Name, PreviewCamera Camera)[]
		{
			("a000", new PreviewCamera()),
			("a090", new PreviewCamera { YawDegrees = 90 }),
			("a180", new PreviewCamera { YawDegrees = 180 }),
			("a270", new PreviewCamera { YawDegrees = 270 }),
		};

		var slots = scene.Meshes.SelectMany(mesh => mesh.Triangles).Select(triangle => triangle.MaterialSlotId).Distinct().Order().ToArray();
		_output.WriteLine("distinct material slots: " + string.Join(",", slots));

		var outputDirectory = Environment.GetEnvironmentVariable("HD2_PREVIEW_OUT");
		foreach (var (name, camera) in views)
		{
			var image = PreviewRenderer.Render(scene, camera, options);
			var covered = image.CoveredPixels();
			_output.WriteLine($"{name}: {image.Width}×{image.Height} covered={covered} ({covered * 100.0 / (image.Width * image.Height):0.0}%)");
			// 自动取景下，任何一侧视图都应当至少盖住几个百分点；空白说明相机/投影/裁剪有 bug。
			Assert.True(covered > 512 * 512 / 50, $"{name} 视图几乎空白（covered={covered}）");

			if (string.IsNullOrWhiteSpace(outputDirectory)) continue;
			Directory.CreateDirectory(outputDirectory);
			var path = Path.Combine(outputDirectory, name + ".png");
			await File.WriteAllBytesAsync(path, PreviewPngWriter.Encode(image));
			_output.WriteLine($"{name}: wrote {path} ({new FileInfo(path).Length} B)");
		}
	}
}
