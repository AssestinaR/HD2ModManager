using System.Numerics;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModAdaptation.PatchReconstruction.UnitMesh.Canonical;
using Xunit;

namespace HD2ModAdaptation.Tests;

public sealed class CanonicalPrimitiveBuilderTests
{
	private static UnitStreamInfo Stream() => new(
		Index: 0,
		Offset: 0,
		ComponentInfoId: 0,
		NumComponents: 3,
		VertexBufferId: 0,
		NumVertices: 4,
		VertexStride: 32,
		IndexBufferId: 0,
		NumIndices: 6,
		IndexBufferType: 0,
		VertexBufferOffset: 0,
		VertexBufferSize: 128,
		IndexBufferOffset: 0,
		IndexBufferSize: 24,
		Components:
		[
			new UnitStreamComponentInfo(0, "position", 0, "vector3", 0, 0, 12),
			new UnitStreamComponentInfo(4, "uv", 0, "vector2", 0, 0, 8),
			new UnitStreamComponentInfo(7, "bone_weight", 0, "vector4", 0, 0, 8),
		]);

	private static UnitVertexComponentValue Component(UnitRawVertexRecord v, uint type)
		=> v.Components.First(c => c.Type == type);

	[Fact]
	public void Quad_ProducesFourVerticesAndTwoTriangles()
	{
		var result = new CanonicalPrimitiveBuilder().TryBuild(
			new CanonicalPrimitiveSpec(CanonicalPrimitiveShape.Quad, 0.5f, 0.5f), Stream());

		Assert.True(result.IsValid, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
		var mesh = result.Mesh!;
		Assert.Equal(4, mesh.Vertices.Count);
		Assert.Equal(2, mesh.Triangles.Count);
		Assert.Single(mesh.Sections);
		Assert.Equal(2, mesh.Sections[0].Triangles.Count);
	}

	[Fact]
	public void Quad_FlipsVByDefault_BecauseEngineVGoesDown()
	{
		var mesh = new CanonicalPrimitiveBuilder().TryBuild(
			new CanonicalPrimitiveSpec(CanonicalPrimitiveShape.Quad, 1f, 1f), Stream()).Mesh!;

		// 第一个顶点在原 UV 里是 (0,0)（左下）→ 翻转后应变成 (0,1)
		var uv0 = Component(mesh.Vertices[0], 4).FloatValues;
		var uv2 = Component(mesh.Vertices[2], 4).FloatValues;
		Assert.Equal(0f, uv0[0], 3);
		Assert.Equal(1f, uv0[1], 3);
		Assert.Equal(1f, uv2[0], 3);
		Assert.Equal(0f, uv2[1], 3);
	}

	[Fact]
	public void Quad_WithoutFlipV_KeepsOriginalOrder()
	{
		var mesh = new CanonicalPrimitiveBuilder().TryBuild(
			new CanonicalPrimitiveSpec(CanonicalPrimitiveShape.Quad, 1f, 1f, FlipV: false), Stream()).Mesh!;

		var uv0 = Component(mesh.Vertices[0], 4).FloatValues;
		Assert.Equal(0f, uv0[0], 3);
		Assert.Equal(0f, uv0[1], 3);
	}

	[Fact]
	public void Quad_OnYzPlane_KeepsXConstant()
	{
		var mesh = new CanonicalPrimitiveBuilder().TryBuild(
			new CanonicalPrimitiveSpec(CanonicalPrimitiveShape.Quad, 1f, 1f,
				Center: new Vector3(0.5f, 0f, 0f), Plane: CanonicalPrimitivePlane.Yz), Stream()).Mesh!;

		foreach (var v in mesh.Vertices)
			Assert.Equal(0.5f, Component(v, 0).FloatValues[0], 3);
	}

	[Fact]
	public void NoSkin_WritesZeroBoneWeights()
	{
		var mesh = new CanonicalPrimitiveBuilder().TryBuild(
			new CanonicalPrimitiveSpec(CanonicalPrimitiveShape.Quad, 1f, 1f, NoSkin: true), Stream()).Mesh!;

		foreach (var v in mesh.Vertices)
			Assert.Equal(0f, Component(v, 7).FloatValues[0], 3);
	}

	[Fact]
	public void Skinned_WritesFullWeightOnSlotZero()
	{
		var mesh = new CanonicalPrimitiveBuilder().TryBuild(
			new CanonicalPrimitiveSpec(CanonicalPrimitiveShape.Quad, 1f, 1f), Stream()).Mesh!;

		foreach (var v in mesh.Vertices)
			Assert.Equal(1f, Component(v, 7).FloatValues[0], 3);
	}

	[Fact]
	public void Sphere_RespectsSegmentCount()
	{
		var mesh = new CanonicalPrimitiveBuilder().TryBuild(
			new CanonicalPrimitiveSpec(CanonicalPrimitiveShape.Sphere, 1f, 1f, Segments: 6), Stream()).Mesh!;

		Assert.Equal((6 / 2 + 1) * (6 + 1), mesh.Vertices.Count);
		Assert.Equal((6 / 2) * 6 * 2, mesh.Triangles.Count);
	}

	[Fact]
	public void RejectsNonPositiveSize()
	{
		var result = new CanonicalPrimitiveBuilder().TryBuild(
			new CanonicalPrimitiveSpec(CanonicalPrimitiveShape.Quad, 0f, 1f), Stream());

		Assert.False(result.IsValid);
		Assert.Contains(result.Diagnostics, d => d.Code == "PrimitiveSizeInvalid");
	}
}
