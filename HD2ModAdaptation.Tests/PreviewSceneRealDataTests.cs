using System.Numerics;
using HD2ModAdaptation.Preview;
using Xunit;
using Xunit.Abstractions;

namespace HD2ModAdaptation.Tests;

// 作用：真实 patch 的冒烟测试。用 HD2_PREVIEW_PATCH 指定一个或多个 patch（分号分隔）。
// 缺样本时按本仓库的约定跳过（CI 上没有游戏数据），但设 HD2_REQUIRE_PREVIEW_SAMPLES=1
// 时改为**失败**，这样要必跑的人不会被静默跳过糊弄过去。
// Purpose: Real-patch smoke test. Point HD2_PREVIEW_PATCH at one or more patch files
// (semicolon separated). Missing samples skip by repository convention, but
// HD2_REQUIRE_PREVIEW_SAMPLES=1 turns that into a failure so a required run cannot pass silently.
public sealed class PreviewSceneRealDataTests
{
	private readonly ITestOutputHelper _output;

	public PreviewSceneRealDataTests(ITestOutputHelper output) => _output = output;

	[Fact]
	public async Task RealPatch_BuildsASceneWhoseGeometryIsSelfConsistent()
	{
		var paths = ParsePatchPaths();
		if (paths.Length == 0) return;

		var scene = await PreviewSceneBuilder.BuildAsync(paths);

		_output.WriteLine(scene.Describe());
		foreach (var note in scene.Stats.Notes) _output.WriteLine("note: " + note);
		_output.WriteLine($"bounds center={scene.Bounds.Center} radius={scene.Bounds.Radius}");
		_output.WriteLine($"aabb min={scene.Bounds.Min} max={scene.Bounds.Max} size={scene.Bounds.Size}");
		foreach (var group in scene.Meshes.GroupBy(mesh => mesh.UnitHex))
		{
			var triangles = group.Sum(mesh => mesh.Triangles.Count);
			var vertices = group.Sum(mesh => mesh.Vertices.Count);
			_output.WriteLine($"unit {group.Key}: meshes={group.Count()} vertices={vertices} triangles={triangles}");
			foreach (var mesh in group)
			{
				_output.WriteLine(
					$"  mesh lod={mesh.LodIndex} index={mesh.MeshIndex} kind={mesh.Kind} "
					+ $"vertices={mesh.Vertices.Count} triangles={mesh.Triangles.Count}");
			}
		}

		Assert.False(scene.IsEmpty, "真实 patch 里应当至少有一个可渲染网格");
		Assert.True(scene.Stats.Triangles > 0, "场景应当至少有一个三角形");
		Assert.True(float.IsFinite(scene.Bounds.Radius) && scene.Bounds.Radius > 0f, "包围球半径必须是有限正数");

		foreach (var mesh in scene.Meshes)
		{
			Assert.NotEmpty(mesh.UnitHex);
			Assert.True(mesh.Triangles.Count > 0, $"unit {mesh.UnitHex} mesh {mesh.MeshIndex} 应当有三角形");
			foreach (var vertex in mesh.Vertices)
			{
				Assert.True(IsFinite(vertex.Position), $"unit {mesh.UnitHex} 有非有限坐标");
				Assert.True(IsFinite(vertex.Normal), $"unit {mesh.UnitHex} 有非有限法线");
			}

			foreach (var triangle in mesh.Triangles)
			{
				Assert.InRange(triangle.A, 0, mesh.Vertices.Count - 1);
				Assert.InRange(triangle.B, 0, mesh.Vertices.Count - 1);
				Assert.InRange(triangle.C, 0, mesh.Vertices.Count - 1);
			}
		}
	}

	private static bool IsFinite(Vector3 value)
		=> float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

	internal static string[] ParsePatchPaths()
	{
		var raw = Environment.GetEnvironmentVariable("HD2_PREVIEW_PATCH") ?? string.Empty;
		var paths = raw
			.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Where(File.Exists)
			.ToArray();
		if (paths.Length == 0 && Environment.GetEnvironmentVariable("HD2_REQUIRE_PREVIEW_SAMPLES") == "1")
			Assert.Fail("HD2_REQUIRE_PREVIEW_SAMPLES=1，但 HD2_PREVIEW_PATCH 没有指向任何存在的 patch 文件。");
		return paths;
	}
}
