using Xunit;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModAdaptation.PatchReconstruction.UnitMesh.Canonical;

namespace HD2ModAdaptation.Tests;

/// <summary>
/// 包围盒重算：<b>这是"追加几何之后整块消失"的唯一已知根因</b>。
/// <para>
/// 守四件事：① 默认 <see cref="CanonicalCullingBoundsMode.Preserve"/> 一个字节都不改
/// （三条在役路径的产物不能因为引入这个功能而变化）；② 并集只会变大、不会变小
/// （变小等于把原本能画的东西剔掉）；③ 半径要覆盖盒角，不能只按顶点算；
/// ④ <c>CountOutside</c> 与契约检查同口径（容差 1e-4）。
/// </para>
/// </summary>
public sealed class CanonicalCullingBoundsTests
{
	private static UnitRawMeshData Mesh(int meshInfoIndex, params float[][] positions)
		=> new(
			meshInfoIndex,
			0,
			0,
			0,
			[],
			[],
			[
				.. positions.Select((position, index) =>
					new UnitRawVertexRecord(
						checked((uint)index),
						[],
						[new UnitVertexComponentValue(0, "position", 2, "vec3_float", 0, position, [], [])])),
			]);

	private static UnitMeshModel Model(int meshInfoIndex, params float[] bounds)
		=> new(
			0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
			UnitCustomizationInfo.Empty,
			[],
			[],
			[
				new UnitMeshInfo(
					meshInfoIndex, 0, 0, 0, 0, 0, 0, 0, 0, 0,
					UnitMeshSemanticInfo.Empty(0, meshInfoIndex),
					[],
					[])
				{
					CullingBounds = new UnitMeshCullingBounds(bounds),
				},
			],
			[],
			[],
			[]);

	[Fact]
	public void TryCompute_TakesMinMaxAndRadius()
	{
		var mesh = Mesh(0, [1f, 2f, 3f], [-1f, 0f, 5f]);

		var bounds = CanonicalCullingBounds.TryCompute(mesh);

		Assert.NotNull(bounds);
		Assert.Equal(7, bounds!.Length);
		Assert.Equal(-1f, bounds[0]);
		Assert.Equal(0f, bounds[1]);
		Assert.Equal(3f, bounds[2]);
		Assert.Equal(1f, bounds[3]);
		Assert.Equal(2f, bounds[4]);
		Assert.Equal(5f, bounds[5]);
		// 半径必须覆盖盒角，而不是只到最远顶点（√(1+4+25) ≈ 5.477）。
		Assert.True(bounds[6] >= MathF.Sqrt(1f + 4f + 25f) - 1e-3f);
	}

	[Fact]
	public void TryCompute_ReturnsNullWithoutPositions()
	{
		var mesh = new UnitRawMeshData(0, 0, 0, 0, [], [], []);

		Assert.Null(CanonicalCullingBounds.TryCompute(mesh));
	}

	[Fact]
	public void Union_NeverShrinks()
	{
		float[] existing = [-1f, -1f, -1f, 1f, 1f, 1f, 2f];
		float[] computed = [0f, 0f, 0f, 0.5f, 0.5f, 0.5f, 0.1f];

		var union = CanonicalCullingBounds.Union(existing, computed);

		Assert.Equal(-1f, union[0]);
		Assert.Equal(1f, union[3]);
		// 半径取大的那个：并集不该让包围球变小。
		Assert.Equal(2f, union[6]);
	}

	[Fact]
	public void Union_FallsBackToComputedWhenExistingIsEmpty()
	{
		float[] computed = [1f, 2f, 3f, 4f, 5f, 6f, 7f];

		Assert.Same(computed, CanonicalCullingBounds.Union([], computed));
	}

	[Fact]
	public void Apply_PreserveReturnsTheSameModel()
	{
		var model = Model(0, -1f, -1f, -1f, 1f, 1f, 1f, 2f);
		var prepared = new[] { Mesh(0, [9f, 9f, 9f]) };

		var applied = CanonicalCullingBounds.Apply(model, prepared, CanonicalCullingBoundsMode.Preserve);

		// 引用相同 = 一个字段都没改。三条在役路径走这条，产物字节不能变。
		Assert.Same(model, applied);
	}

	[Fact]
	public void Apply_UnionModeGrowsButNeverShrinks()
	{
		var model = Model(0, -1f, -1f, -1f, 1f, 1f, 1f, 2f);
		var prepared = new[] { Mesh(0, [0.5f, 0.5f, 0.5f]) };

		var applied = CanonicalCullingBounds.Apply(model, prepared, CanonicalCullingBoundsMode.UnionWithComputed);

		var bounds = Assert.Single(applied.Meshes).CullingBounds.Values;
		Assert.Equal(-1f, bounds[0]);
		Assert.Equal(1f, bounds[3]);
	}

	[Fact]
	public void Apply_ComputedModeTakesTheTightBox()
	{
		var model = Model(0, -1f, -1f, -1f, 1f, 1f, 1f, 2f);
		var prepared = new[] { Mesh(0, [0.5f, 0.5f, 0.5f], [0.25f, 0.25f, 0.25f]) };

		var applied = CanonicalCullingBounds.Apply(model, prepared, CanonicalCullingBoundsMode.Computed);

		var bounds = Assert.Single(applied.Meshes).CullingBounds.Values;
		Assert.Equal(0.25f, bounds[0]);
		Assert.Equal(0.5f, bounds[3]);
	}

	[Fact]
	public void CountOutside_CountsVerticesBeyondTheBox()
	{
		var mesh = Mesh(0, [0f, 0f, 0f], [2f, 0f, 0f], [0f, 3f, 0f]);
		float[] tight = [0f, 0f, 0f, 1f, 1f, 1f, 1f];

		var (outside, total) = CanonicalCullingBounds.CountOutside(mesh, tight);

		Assert.Equal(3, total);
		Assert.Equal(2, outside);
	}

	[Fact]
	public void CountOutside_IgnoresFloatingPointNoise()
	{
		var mesh = Mesh(0, [1.00001f, 0f, 0f]);
		float[] bounds = [0f, 0f, 0f, 1f, 1f, 1f, 1f];

		var (outside, _) = CanonicalCullingBounds.CountOutside(mesh, bounds);

		Assert.Equal(0, outside);
	}
}
