using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModAdaptation.PatchReconstruction.UnitMesh.Canonical;
using Xunit;

namespace HD2ModAdaptation.Tests;

// Purpose: Verifies Canonical stream contracts are stream-wide, ABI-preserving, and fail closed.
public sealed class CanonicalStreamContractCompilerTests
{
	[Fact]
	public void Compile_PromotesIndexBufferWhenOneFinalMeshExceedsUint16()
	{
		var target = Target();
		var raw = Raw(0, 65_536);

		var result = new CanonicalStreamContractCompiler().TryCompile(target, [raw]);

		Assert.True(result.IsValid, string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Message)));
		Assert.Equal(1u, Assert.Single(result.Streams).IndexBufferType);
		Assert.Equal(target.Streams[0].ComponentInfoId, result.Streams[0].ComponentInfoId);
		Assert.Equal([0u], result.Streams[0].Components.Select(component => component.Type));
	}

	[Fact]
	public void Compile_PreservesMultipleFinalBoneIndexGroupsUsingSdkProfile()
	{
		var target = Target() with
		{
			Streams = [Target().Streams[0] with
			{
				VertexStride = 12,
				Components = [
					new UnitStreamComponentInfo(0, "position", 0, "vec3_float", 0, 0, 4),
					new UnitStreamComponentInfo(6, "bone_index", 0, "vec4_uint8", 0, 0, 4),
					new UnitStreamComponentInfo(6, "bone_index", 0, "vec4_uint8", 1, 0, 4)]
			}]
		};

		var raw = Raw(0, 3) with
		{
			Vertices = Raw(0, 3).Vertices.Select(vertex => vertex with
			{
				Components = [
					new UnitVertexComponentValue(0, "position", 2, "vec3_float", 0, [0, 0, 0], [], []),
					new UnitVertexComponentValue(6, "bone_index", 28, "vec4_uint8", 0, [], [0, 0, 0, 0], []),
					new UnitVertexComponentValue(6, "bone_index", 28, "vec4_uint8", 1, [], [0, 0, 0, 0], []),
					new UnitVertexComponentValue(7, "bone_weight", 35, "vec4_half", 0, [1, 0, 0, 0], [], [])]
			}).ToArray()
		};

		var result = new CanonicalStreamContractCompiler().TryCompile(target, [raw]);

		Assert.True(result.IsValid, string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Message)));
		Assert.Equal(2, result.Streams[0].Components.Count(component => component.Type == 6));
		Assert.Single(result.Streams[0].Components, component => component.Type == 7);
	}

	[Fact]
	public void Compile_RequiresEveryTargetMeshToProvideExactlyOneFinalRawMesh()
	{
		var target = Target(meshCount: 2);

		var result = new CanonicalStreamContractCompiler().TryCompile(target, [Raw(0, 3)]);

		Assert.False(result.IsValid);
		Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "IncompleteStreamRawMeshCoverage");
	}

	[Fact]
	public void Compile_PreservesExistingFloatUvStreamContract()
	{
		var target = Target() with
		{
			Streams = [Target().Streams[0] with
			{
				VertexStride = 56,
				Components = [
					new UnitStreamComponentInfo(5, "color", 4, "rgba_r8g8b8a8", 0, 0, 4),
					new UnitStreamComponentInfo(0, "position", 2, "vec3_float", 0, 0, 12),
					new UnitStreamComponentInfo(1, "normal", 30, "unk_normal", 0, 0, 4),
					new UnitStreamComponentInfo(4, "uv", 1, "vec2_float", 0, 0, 8),
					new UnitStreamComponentInfo(4, "uv", 1, "vec2_float", 1, 0, 8),
					new UnitStreamComponentInfo(4, "uv", 1, "vec2_float", 2, 0, 8),
					new UnitStreamComponentInfo(7, "bone_weight", 35, "vec4_half", 0, 0, 8),
					new UnitStreamComponentInfo(6, "bone_index", 28, "vec4_uint8", 0, 0, 4)]
			}]
		};
		var raw = Raw(0, 3) with
		{
			Vertices = Raw(0, 3).Vertices.Select(vertex => vertex with
			{
				Components = target.Streams[0].Components.Select(component => new UnitVertexComponentValue(
					component.Type, component.TypeName, component.Format, component.FormatName, component.Index,
					component.Type is 0 or 1 or 4 or 7 ? [0f, 0f, 0f, 0f] : [],
					component.Type == 6 ? [0u, 0u, 0u, 0u] : [], [])).ToArray()
			}).ToArray()
		};

		var result = new CanonicalStreamContractCompiler().TryCompile(target, [raw]);

		Assert.True(result.IsValid, string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Message)));
		var stream = Assert.Single(result.Streams);
		Assert.Equal(56u, stream.VertexStride);
		Assert.All(stream.Components.Where(component => component.Type == 4), component =>
		{
			Assert.Equal(1u, component.Format);
			Assert.Equal("vec2_float", component.FormatName);
			Assert.Equal(8u, component.Size);
		});
	}

	[Fact]
	public void Compile_FallbackKeepsTargetColorWhenSourceAddsUvChannel()
	{
		var target = Target() with
		{
			Streams = [Target().Streams[0] with
			{
				VertexStride = 48,
				Components = [
					new UnitStreamComponentInfo(5, "color", 4, "rgba_r8g8b8a8", 0, 0, 4),
					new UnitStreamComponentInfo(0, "position", 2, "vec3_float", 0, 0, 12),
					new UnitStreamComponentInfo(1, "normal", 30, "unk_normal", 0, 0, 4),
					new UnitStreamComponentInfo(4, "uv", 1, "vec2_float", 0, 0, 8),
					new UnitStreamComponentInfo(4, "uv", 1, "vec2_float", 1, 0, 8),
					new UnitStreamComponentInfo(7, "bone_weight", 35, "vec4_half", 0, 0, 8),
					new UnitStreamComponentInfo(6, "bone_index", 28, "vec4_uint8", 0, 0, 4)]
			}]
		};
		var raw = Raw(0, 3) with
		{
			Vertices = Raw(0, 3).Vertices.Select(vertex => vertex with
			{
				Components = [
					new UnitVertexComponentValue(0, "position", 2, "vec3_float", 0, [0, 0, 0], [], []),
					new UnitVertexComponentValue(1, "normal", 30, "unk_normal", 0, [0, 0, 1], [], []),
					new UnitVertexComponentValue(4, "uv", 29, "vec4_1010102", 0, [0, 0, 0, 0], [], []),
					new UnitVertexComponentValue(4, "uv", 29, "vec4_1010102", 1, [0, 0, 0, 0], [], []),
					new UnitVertexComponentValue(4, "uv", 29, "vec4_1010102", 2, [0, 0, 0, 0], [], [])]
			}).ToArray()
		};

		var result = new CanonicalStreamContractCompiler().TryCompile(target, [raw]);

		Assert.True(result.IsValid, string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Message)));
		var stream = Assert.Single(result.Streams);
		Assert.Contains(stream.Components, component => component.Type == 5 && component.Index == 0);
		Assert.Equal(3, stream.Components.Count(component => component.Type == 4));
		Assert.Equal(44u, stream.VertexStride);
	}

	[Fact]
	public void Compile_PromotesLegacyScalarWeightStreamWhenFinalVerticesNeedVec4Weights()
	{
		var target = Target() with
		{
			Streams = [Target().Streams[0] with
			{
				VertexStride = 20,
				Components = [
					new UnitStreamComponentInfo(0, "position", 2, "vec3_float", 0, 0, 12),
					new UnitStreamComponentInfo(7, "bone_weight", 0, "float", 0, 0, 4),
					new UnitStreamComponentInfo(6, "bone_index", 28, "vec4_uint8", 0, 0, 4)]
			}]
		};
		var raw = Raw(0, 3) with
		{
			Vertices = Raw(0, 3).Vertices.Select(vertex => vertex with
			{
				Components = [
					new UnitVertexComponentValue(0, "position", 2, "vec3_float", 0, [0, 0, 0], [], []),
					new UnitVertexComponentValue(7, "bone_weight", 35, "vec4_half", 0, [1, 0, 0, 0], [], []),
					new UnitVertexComponentValue(6, "bone_index", 28, "vec4_uint8", 0, [], [0, 0, 0, 0], [])]
			}).ToArray()
		};

		var result = new CanonicalStreamContractCompiler().TryCompile(target, [raw]);

		Assert.True(result.IsValid, string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Message)));
		var weight = Assert.Single(result.Streams[0].Components, component => component.Type == 7);
		Assert.Equal("vec4_half", weight.FormatName);
		Assert.Equal(8u, weight.Size);
	}

	private static UnitMeshModel Target(int meshCount = 1)
	{
		var stream = new UnitStreamInfo(0, 128, 99, 1, 0, 0, 4, 0, 0, 0, 0, 0, 0, 0,
			[new UnitStreamComponentInfo(0, "position", 0, "vec3_float", 0, 0, 4)]);
		var meshes = Enumerable.Range(0, meshCount).Select(index => new UnitMeshInfo(index, checked((uint)(256 + index * 128)), 1, 0, 0, 0, 1, 0, 1, 0,
			UnitMeshSemanticInfo.Empty(0, index), [10], [new(0, 0, 10, 0, 3, 0, 3, 0)])).ToArray();
		return new(1, 1, 0, 0, 0, 0, 128, 0, 0, 0, UnitCustomizationInfo.Empty, [], [stream], meshes, [], [], []);
	}

	private static UnitRawMeshData Raw(int meshInfoIndex, int vertices)
	{
		var data = Enumerable.Range(0, vertices).Select(index => new UnitRawVertexRecord((uint)index, [], [
			new UnitVertexComponentValue(0, "position", 2, "vec3_float", 0, [0, 0, 0], [], [])])).ToArray();
		var triangle = new UnitTriangleIndices(0, 1, checked((uint)(vertices - 1)));
		return new(meshInfoIndex, 1, 0, 0, [new UnitRawMeshSectionData(0, 10, [triangle])], [triangle], data);
	}
}
