using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModAdaptation.PatchReconstruction.UnitMesh.Canonical;
using Xunit;

namespace HD2ModAdaptation.Tests;

// Purpose: Verifies canonical full-RawMesh coverage, one-pass GPU ranges/alignment, and fail-closed layouts.
public sealed class CanonicalUnitRebuilderTests
{
	[Fact]
	public void Rebuild_RequiresEveryTargetMeshToHaveOneFinalRawMesh()
	{
		var target = Target(meshCount: 2);
		var result = new CanonicalUnitRebuilder().TryRebuild(target, Toc(target), [Raw(0)]);

		Assert.False(result.IsValid);
		Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "IncompleteRawMeshCoverage");
	}

	[Fact]
	public void Rebuild_RejectsRawMeshThatDoesNotMapToTargetMesh()
	{
		var target = Target(meshCount: 1);
		var result = new CanonicalUnitRebuilder().TryRebuild(target, Toc(target), [Raw(9)]);

		Assert.False(result.IsValid);
		Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "UnknownRawMeshTarget");
	}

	[Fact]
	public void Rebuild_RebuildsGpuRangesAndKeepsSixteenByteVertexAlignment()
	{
		var target = Target(meshCount: 2);
		var result = new CanonicalUnitRebuilder().TryRebuild(target, Toc(target), [Raw(0), Raw(1)]);

		Assert.True(result.IsValid, string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Message)));
		var stream = Assert.Single(result.Model!.Streams);
		Assert.Equal(4u, stream.NumVertices);
		Assert.Equal(6u, stream.NumIndices);
		Assert.Equal(0u, stream.VertexBufferOffset);
		Assert.Equal(32u, stream.IndexBufferOffset);
		Assert.Equal(32u, stream.VertexBufferSize);
		Assert.Equal(12u, stream.IndexBufferSize);
		Assert.Equal(44, result.Output!.GpuData.Length);
		Assert.Equal(0, (int)stream.IndexBufferOffset % 16);
	}

	[Fact]
	public void Rebuild_UsesIndependentVertexAndIndexOrdersAndMeshLocalIndices()
	{
		var target = TargetWithOffsets([new(90, 90, 90), new(10, 10, 10)]);
		var result = new CanonicalUnitRebuilder().TryRebuild(target, Toc(target), [Raw(0), Raw(1)]);

		Assert.True(result.IsValid, string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Message)));
		var meshes = result.Model!.Meshes.OrderBy(mesh => mesh.Index).ToArray();
		Assert.Equal(2u, meshes[0].Sections[0].VertexOffset);
		Assert.Equal(0u, meshes[1].Sections[0].VertexOffset);
		Assert.Equal(3u, meshes[0].Sections[0].IndexOffset);
		Assert.Equal(0u, meshes[1].Sections[0].IndexOffset);
		Assert.Equal((byte)0, result.Output!.GpuData[32]);
		Assert.Equal((byte)1, result.Output.GpuData[34]);
	}

	[Fact]
	public void Rebuild_NormalizesRepeatedAndNonContiguousMaterialIndices()
	{
		var target = Target(meshCount: 1);
		var raw = Raw(0) with
		{
			Sections = [new(90, 100, [new(0, 1, 0)]), new(7, 200, [new(0, 1, 0)])]
		};
		var expandedTarget = target with
		{
			Meshes = [target.Meshes[0] with
			{
				MaterialSlotIds = [10, 20],
				NumMaterials = 2,
				NumSections = 2,
				Sections = [target.Meshes[0].Sections[0], target.Meshes[0].Sections[0] with { Offset = 1050 }]
			}]
		};
		var result = new CanonicalUnitRebuilder().TryRebuild(expandedTarget, Toc(expandedTarget), [raw]);

		Assert.True(result.IsValid, string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Message)));
		Assert.Equal(new uint[] { 100, 200 }, result.Model!.Meshes[0].MaterialSlotIds);
		Assert.Equal(new uint[] { 0, 1 }, result.Model.Meshes[0].Sections.Select(section => section.MaterialIndex));
	}

	[Fact]
	public void Rebuild_RebuildsGroupIndexFromFinalSectionOrdinal()
	{
		var target = Target(meshCount: 1) with
		{
			Meshes = [Target(meshCount: 1).Meshes[0] with
			{
				MaterialSlotIds = [10, 20],
				NumMaterials = 2,
				NumSections = 2,
				Sections = [Target(meshCount: 1).Meshes[0].Sections[0], Target(meshCount: 1).Meshes[0].Sections[0] with { Offset = 1050, GroupIndex = 99 }]
			}]
		};
		var raw = Raw(0) with { Sections = [new(0, 10, [new(0, 1, 0)]), new(0, 20, [new(0, 1, 0)])] };

		var result = new CanonicalUnitRebuilder().TryRebuild(target, Toc(target), [raw]);

		Assert.True(result.IsValid, string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Message)));
		Assert.Equal(new uint[] { 0, 1 }, result.Model!.Meshes[0].Sections.Select(section => section.GroupIndex));
	}

	[Fact]
	public void Rebuild_RebuildsChangedDistinctMaterialSlotCount()
	{
		var target = Target(meshCount: 1);
		var raw = Raw(0) with { Sections = [new(0, 10, [new(0, 1, 0)]), new(0, 20, [new(0, 1, 0)])] };

		var result = new CanonicalUnitRebuilder().TryRebuild(target, Toc(target), [raw]);

		Assert.True(result.IsValid, string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Message)));
		Assert.Equal(new uint[] { 10, 20 }, result.Model!.Meshes[0].MaterialSlotIds);
		Assert.Equal(2u, result.Model.Meshes[0].NumMaterials);
	}

	[Fact]
	public void Rebuild_UsesFinalMaterialBindingForAnExistingOutputSlot()
	{
		var target = Target(meshCount: 1) with
		{
			Materials = [new UnitMaterialBinding(10, 999)]
		};

		var result = new CanonicalUnitRebuilder().TryRebuild(target, Toc(target), [Raw(0)]);

		Assert.True(result.IsValid, string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Message)));
		Assert.Equal(new UnitMaterialBinding(10, 999), Assert.Single(result.Model!.Materials));
	}

	[Fact]
	public void Rebuild_RejectsRawMeshStreamMismatch()
	{
		var target = Target(meshCount: 1);
		var result = new CanonicalUnitRebuilder().TryRebuild(target, Toc(target), [Raw(0) with { StreamIndex = 7, Sections = [new(0, 12345, [new(0, 1, 0)])] }]);

		Assert.False(result.IsValid);
		Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "RawMeshStreamMismatch");
	}

	[Fact]
	public void Rebuild_PreservesUnboundMeshLocalSlotWithoutSubstitutingAnotherMaterial()
	{
		var target = Target(meshCount: 1);
		var raw = Raw(0) with { Sections = [new(0, 12345, [new(0, 1, 0)])] };

		var result = new CanonicalUnitRebuilder().TryRebuild(target, Toc(target), [raw]);

		Assert.True(result.IsValid, string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Message)));
		Assert.Equal(12345u, Assert.Single(result.Model!.Meshes[0].MaterialSlotIds));
		Assert.Empty(result.Model.Materials);
	}

	[Fact]
	public void Rebuild_ReaderRoundTrip_UsesRealParseableUnitFixture()
	{
		var target = Target(meshCount: 1);
		var toc = Toc(target);
		WriteUInt32(toc, 0x2c, 1);
		WriteUInt32(toc, 0x5c, target.StreamInfoOffset);
		WriteUInt32(toc, checked((int)target.StreamInfoOffset), 1);
		WriteUInt32(toc, checked((int)target.StreamInfoOffset + 4), 0);
		WriteUInt32(toc, checked((int)target.StreamInfoOffset + 8 + 320 + 28), 6);

		var result = new CanonicalUnitRebuilder().TryRebuild(target, toc, [Raw(0)]);

		Assert.True(result.IsValid, string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Message)));
		var readback = new UnitMeshReader().Read(result.Output!.TocData, result.Output.GpuData);
		var mesh = Assert.Single(readback.Meshes);
		Assert.Equal(new uint[] { 10 }, mesh.MaterialSlotIds);
		Assert.Single(mesh.Sections);
		Assert.Equal(2u, mesh.Sections[0].NumVertices);
	}

	[Fact]
	public void Rebuild_WritesCompleteTargetStreamDeclaration()
	{
		var target = Target(meshCount: 1) with
		{
			Streams = [new UnitStreamInfo(0, 128, 0x1122334455667788, 2, 0, 0, 8, 0, 3, 0, 0, 0, 0, 0,
				[new(3, "position", 0, "vec2_half", 2, 4, 4), new(7, "uv", 1, "vec2_half", 6, 8, 4)])]
		};

		var result = new CanonicalUnitRebuilder().TryRebuild(target, Toc(target), [Raw(0)]);

		Assert.True(result.IsValid, string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Message)));
		Assert.Equal(0x55667788u, ReadUInt32(result.Output!.TocData, 128));
		Assert.Equal(0x11223344u, ReadUInt32(result.Output.TocData, 132));
		Assert.Equal(3u, ReadUInt32(result.Output.TocData, 136));
		Assert.Equal(7u, ReadUInt32(result.Output.TocData, 156));
		Assert.Equal(2u, ReadUInt32(result.Output.TocData, 456));
		Assert.Equal(8u, ReadUInt32(result.Output.TocData, 484));
		Assert.Equal(0u, ReadUInt32(result.Output.TocData, 476));
	}

	[Fact]
	public void Rebuild_FailsClosedForCompositeBackedTarget()
	{
		var target = Target(meshCount: 1) with { StreamInfoOffset = 0, CompositeRef = 42 };
		var result = new CanonicalUnitRebuilder().TryRebuild(target, Toc(target), [Raw(0)]);

		Assert.False(result.IsValid);
		Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "UnsupportedCompositeLayout");
	}

	[Fact]
	public void Rebuild_FailsClosedWhenTargetBoneInfoIsNotRebuilt()
	{
		var target = Target(meshCount: 1) with
		{
			BoneInfos = [new UnitBoneInfo(0, 64, 1, 0, 0, 0, [0], [new UnitBoneRemap(0, 0, [0])]) { BoneMatrices = [new byte[64]] }]
		};

		var result = new CanonicalUnitRebuilder().TryRebuild(target, Toc(target), [Raw(0)]);

		Assert.False(result.IsValid);
		Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "BoneInfoRewriteIncomplete");
	}

	[Fact]
	public void Rebuild_FailsClosedForOutOfRangeTriangle()
	{
		var target = Target(meshCount: 1);
		var result = new CanonicalUnitRebuilder().TryRebuild(target, Toc(target), [Raw(0, new(0, 1, 8))]);

		Assert.False(result.IsValid);
		Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SerializationFailed");
	}

	[Fact]
	public void Rebuild_FailsClosedWhenSingleStreamVertexBufferReachesD3D12BlockLimit()
	{
		const uint stride = 1024u * 1024u;
		var target = Target(meshCount: 1) with
		{
			Streams = [new UnitStreamInfo(0, 128, 0, 1, 0, 0, stride, 0, 3, 0, 0, 0, 0, 0,
				[new(0, "position", 0, "vec3_float", 0, 0, 12)])]
		};
		var vertices = Enumerable.Range(0, 128)
			.Select(index => new UnitRawVertexRecord((uint)index, [1, 2, 3, 4], []))
			.ToArray();
		var triangle = new UnitTriangleIndices(0, 1, 2);
		var raw = new UnitRawMeshData(0, 100, 0, 0, [new(0, 10, [triangle])], [triangle], vertices);

		var result = new CanonicalUnitRebuilder().TryRebuild(target, Toc(target), [raw]);

		Assert.False(result.IsValid);
		Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == CanonicalUnitRebuilder.StreamBufferLimitDiagnosticCode);
	}

	private static UnitMeshModel Target(int meshCount)
	{
		var meshes = Enumerable.Range(0, meshCount).Select(index => new UnitMeshInfo(
			index, checked(512u + (uint)index * 128u), 100u + (uint)index, 0, 0, 0, 1, 0, 1, 0,
			new("mesh", "slot", "piece", "body", "weight", 0, index, false, false, false),
			[10], [new(checked(1024u + (uint)index * 24u), 0, 10, 0, 3, 0, 0, 0)])).ToArray();
		var stream = new UnitStreamInfo(0, 128, 0, 1, 0, 0, 6, 0, 3, 0, 0, 0, 0, 0,
			[new(0, "position", 0, "vec2_half", 0, 0, 4)]);
		return new(1, 1, 0, 0, 0, 64, 128, 256, 400, 512, UnitCustomizationInfo.Empty,
			[], [stream], meshes, [new(10, 100), new(20, 200), new(100, 1000), new(200, 2000), new(999, 9999)], [], []);
	}

	private static byte[] Toc(UnitMeshModel target) => new byte[2048];

	private static void WriteUInt32(byte[] data, int offset, uint value)
	{
		data[offset] = (byte)value;
		data[offset + 1] = (byte)(value >> 8);
		data[offset + 2] = (byte)(value >> 16);
		data[offset + 3] = (byte)(value >> 24);
	}

	private static uint ReadUInt32(byte[] data, int offset) => (uint)(data[offset]
		| data[offset + 1] << 8
		| data[offset + 2] << 16
		| data[offset + 3] << 24);

	private static UnitMeshModel TargetWithOffsets(IReadOnlyList<(uint Vertex, uint Index, uint Slot)> offsets)
	{
		var target = Target(offsets.Count);
		return target with
		{
			Meshes = target.Meshes.Select((mesh, index) => mesh with
			{
				MaterialSlotIds = [offsets[index].Slot],
				Sections = [mesh.Sections[0] with { VertexOffset = offsets[index].Vertex, IndexOffset = offsets[index].Index }]
			}).ToArray()
		};
	}

	private static UnitRawMeshData Raw(int meshInfoIndex, UnitTriangleIndices? triangle = null)
	{
		var vertices = new[]
		{
			new UnitRawVertexRecord(0, [1, 2, 3, 4, 5, 6], []),
			new UnitRawVertexRecord(1, [7, 8, 9, 10, 11, 12], []),
		};
		var triangles = triangle is null ? new[] { new UnitTriangleIndices(0, 1, 0) } : new[] { triangle };
		return new(meshInfoIndex, 100u + (uint)meshInfoIndex, 0, 0, [new(0, 10, triangles)], triangles, vertices);
	}
}
