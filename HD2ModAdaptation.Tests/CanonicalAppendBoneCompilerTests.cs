using System.Numerics;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModAdaptation.PatchReconstruction.UnitMesh.Canonical;
using Xunit;

namespace HD2ModAdaptation.Tests;

public sealed class CanonicalAppendBoneCompilerTests
{
	[Fact]
	public void Compile_PreservesCompleteHostPaletteForEveryFinalMaterial()
	{
		var target = Model([10, 20, 30], [2, 0, 1]);
		var targetRaw = Mesh(0, 0, 1, 0);
		var source = Model([10, 20, 30], [1, 2]);
		var sourceRaw = Mesh(0, 0, 1, 0);
		var appended = new UnitRawMeshData(0, 1, 0, 0,
			[targetRaw.Sections[0], sourceRaw.Sections[0] with { MaterialIndex = 1, MaterialSlotId = 2, Triangles = [new UnitTriangleIndices(1, 1, 1)] }],
			[new UnitTriangleIndices(0, 0, 0), new UnitTriangleIndices(1, 1, 1)],
			[targetRaw.Vertices[0], sourceRaw.Vertices[0] with { Index = 1 }]);

		var result = new CanonicalAppendBoneCompiler().TryCompile(
			target,
			targetRaw,
			[new CanonicalAppendSource(source, sourceRaw)],
			appended,
			[new CanonicalAppendSectionOrigin(0, -1, 0), new CanonicalAppendSectionOrigin(1, 0, 0)]);

		Assert.True(result.IsValid, string.Join("; ", result.Diagnostics.Select(item => item.Message)));
		Assert.Equal([2u, 0u, 1u], result.BoneInfo!.RealIndices);
		Assert.Equal(2, result.BoneInfo.Remaps.Count);
		Assert.All(result.BoneInfo.Remaps, remap => Assert.Equal([1u, 0u, 2u], remap.FakeIndices));
		Assert.Equal(1u, result.Mesh!.Vertices[1].Components.Single(component => component.Type == 6).UIntValues[0]);
	}

	[Fact]
	public void Compile_StripsUnbackedExtraBoneIndexGroupsWhenVertexHasSingleWeightGroup()
	{
		var target = Model([10, 20, 30], [2, 0, 1]);
		var targetRaw = Mesh(0, 0, 1, 0);
		var source = Model([10, 20, 30], [1, 2]);
		var sourceBase = Mesh(0, 0, 1, 0);
		var sourceVertexWithDeadGroups = sourceBase.Vertices[0] with
		{
			Components =
			[
				new UnitVertexComponentValue(6, "bone_index", 28, "vec4_uint8", 0, [], [1, 0, 0, 0], []),
				new UnitVertexComponentValue(6, "bone_index", 28, "vec4_uint8", 1, [], [0, 0, 0, 0], []),
				new UnitVertexComponentValue(6, "bone_index", 28, "vec4_uint8", 2, [], [0, 0, 0, 0], []),
				new UnitVertexComponentValue(7, "bone_weight", 35, "vec4_half", 0, [1, 0, 0, 0], [], []),
			]
		};
		var sourceRaw = sourceBase with { Vertices = [sourceVertexWithDeadGroups] };
		var appended = new UnitRawMeshData(0, 1, 0, 0,
			[targetRaw.Sections[0], sourceRaw.Sections[0] with { MaterialIndex = 1, MaterialSlotId = 2, Triangles = [new UnitTriangleIndices(1, 1, 1)] }],
			[new UnitTriangleIndices(0, 0, 0), new UnitTriangleIndices(1, 1, 1)],
			[targetRaw.Vertices[0], sourceVertexWithDeadGroups with { Index = 1 }]);

		var result = new CanonicalAppendBoneCompiler().TryCompile(
			target,
			targetRaw,
			[new CanonicalAppendSource(source, sourceRaw)],
			appended,
			[new CanonicalAppendSectionOrigin(0, -1, 0), new CanonicalAppendSectionOrigin(1, 0, 0)]);

		Assert.True(result.IsValid, string.Join("; ", result.Diagnostics.Select(item => item.Message)));
		var boneGroups = result.Mesh!.Vertices[1].Components.Where(component => component.Type == 6).ToArray();
		var kept = Assert.Single(boneGroups);
		Assert.Equal(0u, kept.Index);
		Assert.Equal(1u, kept.UIntValues[0]);
	}

	private static UnitMeshModel Model(IReadOnlyList<uint> hashes, IReadOnlyList<uint> realIndices)
	{
		var identity = Matrix4x4.Identity;
		var matrix = new UnitTransformMatrix([identity.M11, identity.M12, identity.M13, identity.M14, identity.M21, identity.M22, identity.M23, identity.M24, identity.M31, identity.M32, identity.M33, identity.M34, identity.M41, identity.M42, identity.M43, identity.M44]);
		var mesh = new UnitMeshInfo(0, 1, 1, 0, 0, 0, 1, 0, 1, 0, UnitMeshSemanticInfo.Empty(0, 0), [1, 2], [new(1, 0, 1, 0, 3, 0, 0, 0)]);
		var remap = new UnitBoneRemap(0, 0, Enumerable.Range(0, realIndices.Count).Select(index => (uint)index).ToArray());
		var bone = new UnitBoneInfo(0, 0, (uint)realIndices.Count, 0, 0, 0, realIndices, [remap]);
		return new(1, 1, 0, 0, 0, 1, 1, 1, 0, 0, UnitCustomizationInfo.Empty, [bone], [], [mesh], [], [], [])
		{
			TransformInfo = new UnitTransformInfo(0, 0, 0, [], Enumerable.Repeat(matrix, hashes.Count).ToArray(), [], hashes),
			TransformNameHashes = hashes
		};
	}

	private static UnitRawMeshData Mesh(uint materialIndex, uint slotId, uint fakeIndex, uint vertexIndex)
	{
		var triangle = new UnitTriangleIndices(vertexIndex, vertexIndex, vertexIndex);
		var vertex = new UnitRawVertexRecord(vertexIndex, [], [
			new UnitVertexComponentValue(6, "bone_index", 28, "vec4_uint8", 0, [], [fakeIndex, 0, 0, 0], []),
			new UnitVertexComponentValue(7, "bone_weight", 35, "vec4_half", 0, [1, 0, 0, 0], [], [])]);
		return new UnitRawMeshData(0, 1, 0, 0, [new UnitRawMeshSectionData(materialIndex, slotId, [triangle])], [triangle], [vertex]);
	}
}
