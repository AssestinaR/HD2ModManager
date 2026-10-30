using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModAdaptation.PatchReconstruction.UnitMesh.Canonical;
using Xunit;

namespace HD2ModAdaptation.Tests;

// Purpose: Verifies canonical PrepareMesh/GetMeshData normalization and fail-closed ABI encoding.
public sealed class CanonicalMeshPreparationTests
{
	[Fact]
	public void Prepare_NormalizesWeightsAndKeepsFourLargestInfluences()
	{
		var source = Mesh([Component(7, "bone_weight", 31, "vec4_half", [1, 2, 3, 4, 5])]);
		var result = new CanonicalMeshPreparation().TryPrepare(source, Stream(8, [StreamComponent(7, 31, "vec4_half", 0, 8)]));

		Assert.True(result.IsValid);
		var weights = Assert.Single(result.Mesh!.Vertices).Components.Single().FloatValues;
		Assert.Equal(4, weights.Length);
		Assert.Equal(1f, weights.Sum(), 4);
		Assert.Equal(new[] { 5f / 14f, 4f / 14f, 3f / 14f, 2f / 14f }, weights);
	}

	[Fact]
	public void Prepare_ReordersBoneIndicesTogetherWithTheirNormalizedWeights()
	{
		var source = Mesh([
			Component(0, "position", 2, "vec3_float", [0, 0, 0]),
			new UnitVertexComponentValue(6, "bone_index", 28, "vec4_uint8", 0, [], [10, 20, 30, 40, 50], []),
			Component(7, "bone_weight", 31, "vec4_half", [1, 2, 3, 4, 5])
		]);
		var result = new CanonicalMeshPreparation().TryPrepare(source, Stream(24, [
			StreamComponent(0, 2, "vec3_float", 0, 12),
			StreamComponent(6, 28, "vec4_uint8", 0, 4),
			StreamComponent(7, 31, "vec4_half", 0, 8)
		]));

		Assert.True(result.IsValid, string.Join("; ", result.Diagnostics.Select(item => item.Message)));
		var vertex = Assert.Single(result.Mesh!.Vertices);
		Assert.Equal(new uint[] { 50, 40, 30, 20 }, vertex.Components.Single(component => component.Type == 6).UIntValues);
		Assert.Equal(new[] { 5f / 14f, 4f / 14f, 3f / 14f, 2f / 14f }, vertex.Components.Single(component => component.Type == 7).FloatValues);
	}

	[Fact]
	public void Prepare_RejectsOutOfRangeIndices()
	{
		var mesh = Mesh([Component(0, "position", 2, "vec3_float", [0, 0, 0])], new(0, 1, 9));
		var result = new CanonicalMeshPreparation().TryPrepare(mesh, Stream(12, [StreamComponent(0, 2, "vec3_float", 0, 12)]));

		Assert.False(result.IsValid);
		Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "IndexOutOfRange");
	}

	[Fact]
	public void Prepare_RejectsUnknownTargetComponentFormat()
	{
		var result = new CanonicalMeshPreparation().TryPrepare(
			Mesh([Component(0, "position", 99, "unknown", [0, 0, 0])]),
			Stream(16, [StreamComponent(0, 99, "unknown", 0, 16)]));

		Assert.False(result.IsValid);
		Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "UnsupportedComponentFormat");
	}

	[Fact]
	public void Prepare_RebuildsDataAndComponentsWithMatchingStride()
	{
		var result = new CanonicalMeshPreparation().TryPrepare(
			Mesh([Component(0, "position", 2, "vec3_float", [1, 2, 3])]),
			Stream(12, [StreamComponent(0, 2, "vec3_float", 0, 12)]));

		Assert.True(result.IsValid);
		var vertex = Assert.Single(result.Mesh!.Vertices);
		Assert.Equal(12, vertex.Data.Length);
		Assert.Equal(12, vertex.Components.Sum(component => component.RawData.Length));
		Assert.Equal(vertex.Data, vertex.Components.SelectMany(component => component.RawData).ToArray());
	}

	[Fact]
	public void Prepare_EncodesAdditionalBoneIndexGroupsWhenTheSdkProfileDeclaresThem()
	{
		var mesh = Mesh([
			Component(0, "position", 2, "vec3_float", [1, 2, 3]),
			new UnitVertexComponentValue(6, "bone_index", 28, "vec4_uint8", 0, [], [0, 0, 0, 0], [])
		]);
		var stream = Stream(20, [
			StreamComponent(0, 2, "vec3_float", 0, 12),
			StreamComponent(6, 28, "vec4_uint8", 0, 4),
			StreamComponent(6, 28, "vec4_uint8", 1, 4)
		]);

		var result = new CanonicalMeshPreparation().TryPrepare(mesh, stream);

		Assert.True(result.IsValid, string.Join("; ", result.Diagnostics.Select(item => item.Message)));
		var vertex = Assert.Single(result.Mesh!.Vertices);
		Assert.Equal(2, vertex.Components.Count(component => component.Type == 6));
		Assert.Equal(20, vertex.Data.Length);
	}

	[Fact]
	public void Prepare_ExpandsLegacyScalarPlaceholderWeightForVec4BoneIndices()
	{
		var source = Mesh([
			Component(0, "position", 2, "vec3_float", [0, 0, 0]),
			new UnitVertexComponentValue(6, "bone_index", 28, "vec4_uint8", 0, [], [7, 0, 0, 0], []),
			Component(7, "bone_weight", 0, "float", [1])
		]);

		var result = new CanonicalMeshPreparation().TryPrepare(source, Stream(24, [
			StreamComponent(0, 2, "vec3_float", 0, 12),
			StreamComponent(6, 28, "vec4_uint8", 0, 4),
			StreamComponent(7, 31, "vec4_half", 0, 8)
		]));

		Assert.True(result.IsValid, string.Join("; ", result.Diagnostics.Select(item => item.Message)));
		var vertex = Assert.Single(result.Mesh!.Vertices);
		Assert.Equal(new uint[] { 7, 0, 0, 0 }, vertex.Components.Single(component => component.Type == 6).UIntValues);
		Assert.Equal(new[] { 1f, 0f, 0f, 0f }, vertex.Components.Single(component => component.Type == 7).FloatValues);
	}

	[Fact]
	public void Prepare_DecodesPackedNormalWhenSourceOnlyContainsUIntValue()
	{
		const uint packedNormal = 512u | (512u << 10);
		var source = Mesh([
			new UnitVertexComponentValue(1, "normal", 30, "unk_normal", 0, [], [packedNormal], [])
		]);

		var result = new CanonicalMeshPreparation().TryPrepare(
			source,
			Stream(4, [StreamComponent(1, 30, "unk_normal", 0, 4)]));

		Assert.True(result.IsValid, string.Join("; ", result.Diagnostics.Select(item => item.Message)));
		var normal = Assert.Single(result.Mesh!.Vertices).Components.Single();
		Assert.Equal(3, normal.FloatValues.Length);
		Assert.Equal(packedNormal, Assert.Single(normal.UIntValues));
		Assert.Equal(4, normal.RawData.Length);
	}

	[Fact]
	public void Prepare_ReportsPackedNormalSourceContextWhenValuesAreIncomplete()
	{
		var source = Mesh([
			Component(1, "normal", 30, "unk_normal", [1f])
		]);

		var result = new CanonicalMeshPreparation().TryPrepare(
			source,
			Stream(4, [StreamComponent(1, 30, "unk_normal", 0, 4)]));

		Assert.False(result.IsValid);
		var diagnostic = Assert.Single(result.Diagnostics);
		Assert.Equal("PackedNormalSourceInvalid", diagnostic.Code);
		Assert.Contains("Vertex 0", diagnostic.Message);
		Assert.Contains("component type 1 index 0", diagnostic.Message);
		Assert.Contains("unk_normal", diagnostic.Message);
	}

	/// <summary>
	/// 零法线 / 非有限法线**必须兜底成 +Z，不能抛**。
	/// <para>
	/// 真实源模型里就有这种顶点（退化三角形、没写法线的补片、重定向后塌成 0 的向量）：
	/// 实测 `影鸢女武神shinano` 的 Head 与 Chest 各有一个。而一个顶点抛异常会让**整个 Unit**
	/// 写不出来，最终整份 patch 没有产物——用户看到的是"这个 VPK 里没有 HD2 能用的资源"。
	/// </para>
	/// <para>
	/// 编码值兜底成 <c>(0,0,1)</c>，但 <see cref="UnitVertexComponentValue.FloatValues"/> 保留源值：
	/// 排查"这个法线是哪来的"要看原值，而不是兜底值。
	/// </para>
	/// </summary>
	[Theory]
	[InlineData(float.NaN, 0f, 0f)]
	[InlineData(0f, 0f, 0f)]
	[InlineData(float.PositiveInfinity, 0f, 0f)]
	public void Prepare_SanitizesZeroOrNonFiniteNormalsInsteadOfFailing(float x, float y, float z)
	{
		var source = Mesh([
			Component(1, "normal", 30, "unk_normal", [x, y, z])
		]);

		var result = new CanonicalMeshPreparation().TryPrepare(
			source,
			Stream(4, [StreamComponent(1, 30, "unk_normal", 0, 4)]));

		Assert.True(result.IsValid, string.Join("; ", result.Diagnostics.Select(item => item.Message)));
		var normal = Assert.Single(result.Mesh!.Vertices).Components.Single();
		// (0,0,1) 的八面体编码：两个 512 各占 10 位。编码结果在 RawData 里（UIntValues 是源值，为空）。
		const uint expectedPacked = 512u | (512u << 10);
		Assert.Equal(4, normal.RawData.Length);
		Assert.Equal(expectedPacked, BitConverter.ToUInt32(normal.RawData, 0));
	}

	[Fact]
	public void Prepare_ReportsWeightAndIndexComponentContextForArityMismatch()
	{
		var source = Mesh([
			new UnitVertexComponentValue(6, "bone_index", 28, "vec4_uint8", 0, [], [1, 2, 3, 4], []),
			Component(7, "bone_weight", 31, "vec4_half", [1, 0])
		]);

		var result = new CanonicalMeshPreparation().TryPrepare(
			source,
			Stream(12, [
				StreamComponent(6, 28, "vec4_uint8", 0, 4),
				StreamComponent(7, 31, "vec4_half", 0, 8)
			]));

		Assert.False(result.IsValid);
		var diagnostic = Assert.Single(result.Diagnostics);
		Assert.Equal("BoneWeightIndexArityMismatch", diagnostic.Code);
		Assert.Contains("Vertex 0", diagnostic.Message);
		Assert.Contains("weight component type 7 index 0 format vec4_half", diagnostic.Message);
		Assert.Contains("index component type 6 index 0 format vec4_uint8", diagnostic.Message);
	}

	[Fact]
	public void Prepare_ReindexesOutputVerticesSequentially()
	{
		var components = new[] { Component(0, "position", 2, "vec3_float", [1, 2, 3]) };
		var mesh = new UnitRawMeshData(
			0, 1, 0, 0,
			[new UnitRawMeshSectionData(0, 0, [])], [],
			[new UnitRawVertexRecord(7, [], components), new UnitRawVertexRecord(42, [], components)]);

		var result = new CanonicalMeshPreparation().TryPrepare(mesh, Stream(12, [StreamComponent(0, 2, "vec3_float", 0, 12)]));

		Assert.True(result.IsValid);
		Assert.Equal(new uint[] { 0, 1 }, result.Mesh!.Vertices.Select(vertex => vertex.Index));
	}

	[Fact]
	public void Prepare_UsesSdkDefaultForMissingVertexColor()
	{
		var result = new CanonicalMeshPreparation().TryPrepare(
			Mesh([Component(0, "position", 2, "vec3_float", [0, 0, 0])]),
			Stream(16, [
				StreamComponent(0, 2, "vec3_float", 0, 12),
				StreamComponent(5, 4, "rgba_r8g8b8a8", 0, 4)
			]));

		Assert.True(result.IsValid, string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Message)));
		var color = Assert.Single(result.Mesh!.Vertices).Components.Single(component => component.Type == 5);
		Assert.Equal(new[] { 0f, 0f, 0f, 0f }, color.FloatValues);
	}

	private static UnitRawMeshData Mesh(IReadOnlyList<UnitVertexComponentValue> components, UnitTriangleIndices? triangle = null)
	{
		var triangles = triangle is null ? Array.Empty<UnitTriangleIndices>() : new[] { triangle };
		var section = new UnitRawMeshSectionData(0, 0, triangles);
		return new(0, 1, 0, 0, [section], triangles, [new UnitRawVertexRecord(0, [], components)]);
	}

	private static UnitVertexComponentValue Component(uint type, string typeName, uint format, string formatName, float[] values)
		=> new(type, typeName, format, formatName, 0, values, [], []);

	private static UnitStreamInfo Stream(uint stride, IReadOnlyList<UnitStreamComponentInfo> components)
		=> new(0, 0, 0, (ulong)components.Count, 0, 1, stride, 0, 3, 0, 0, 0, 0, 0, components);

	private static UnitStreamComponentInfo StreamComponent(uint type, uint format, string formatName, uint index, uint size)
		=> new(type, type.ToString(), format, formatName, index, 0, size);
}
