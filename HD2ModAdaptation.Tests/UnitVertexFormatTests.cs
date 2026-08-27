using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using Xunit;

namespace HD2ModAdaptation.Tests;

// Purpose: Locks the version-specific vertex format table proved by the UV29 A/B experiment.
public sealed class UnitVertexFormatTests
{
	[Fact]
	public void Read_10800433Format29_DecodesAsVec2Half()
	{
		var model = ReadFixture(10800433u, [0x00, 0x00, 0x00, 0x3c]);
		var stream = Assert.Single(model.Streams);
		var uv = Assert.Single(stream.Components, component => component.Type == 4);
		var vertexUv = Assert.Single(model.RawMeshData.SelectMany(mesh => mesh.Vertices)
			.SelectMany(vertex => vertex.Components), component => component.Type == 4);

		Assert.Equal("vec2_half", uv.FormatName);
		Assert.Equal(4u, uv.Size);
		Assert.Equal("vec2_half", vertexUv.FormatName);
		Assert.Equal(new[] { 0f, 1f }, vertexUv.FloatValues);
	}

	[Fact]
	public void Read_CurrentFormat29_RemainsPackedTenBit()
	{
		var model = ReadFixture(10800438u, [0x00, 0x00, 0x00, 0x3c]);
		var stream = Assert.Single(model.Streams);
		var uv = Assert.Single(stream.Components, component => component.Type == 4);
		var vertexUv = Assert.Single(model.RawMeshData.SelectMany(mesh => mesh.Vertices)
			.SelectMany(vertex => vertex.Components), component => component.Type == 4);

		Assert.Equal("vec4_1010102", uv.FormatName);
		Assert.Equal(4u, uv.Size);
		Assert.Equal("vec4_1010102", vertexUv.FormatName);
		Assert.Equal(new[] { 0f, 0f, 960f / 1023f, 0f }, vertexUv.FloatValues);
	}

	[Fact]
	public void Read_10800436Format29_UsesLegacyFamilyProfile()
	{
		var model = ReadFixture(10800436u, [0x00, 0x00, 0x00, 0x3c]);
		var uv = Assert.Single(Assert.Single(model.Streams).Components, component => component.Type == 4);

		Assert.Equal("vec2_half", uv.FormatName);
		Assert.Equal(4u, uv.Size);
		Assert.Equal(UnitStreamAbiResolutionPath.ExactVersionProfile, model.StreamAbiResolution.Path);
		Assert.Equal(UnitStreamAbiProfileKind.LegacyPre10800438, model.StreamAbiResolution.Profile);
	}

	[Fact]
	public void Read_UnknownVersionWithLegacyFingerprint_UsesFallbackProfile()
	{
		var model = ReadFixture(10990001u, [0x00, 0x00, 0x00, 0x3c]);
		var uv = Assert.Single(Assert.Single(model.Streams).Components, component => component.Type == 4);

		Assert.Equal("vec2_half", uv.FormatName);
		Assert.True(model.StreamAbiResolution.UsedFallback);
		Assert.Equal(UnitStreamAbiProfileKind.LegacyPre10800438, model.StreamAbiResolution.Profile);
		Assert.Contains(model.StreamAbiResolution.Evidence, value => value.Contains("结构指纹唯一命中", StringComparison.Ordinal));
	}

	private static UnitMeshModel ReadFixture(uint version, byte[] uvBytes)
	{
		const int streamInfoOffset = 0x80;
		const int streamOffset = 0x90;
		const int meshInfoOffset = 0x300;
		const int meshOffset = 0x310;
		const int vertexStride = 16;
		var toc = new byte[0x500];

		WriteUInt64(toc, 0, 0x1234);
		WriteUInt32(toc, 0x2c, version);
		WriteUInt32(toc, 0x5c, streamInfoOffset);
		WriteUInt32(toc, 0x64, meshInfoOffset);

		// StreamInfo table: one stream at streamInfoOffset + 0x10.
		WriteUInt32(toc, streamInfoOffset, 1);
		WriteUInt32(toc, streamInfoOffset + 4, streamOffset - streamInfoOffset);

		// StreamInfo component block.
		WriteUInt64(toc, streamOffset, 0x1122334455667788);
		WriteComponent(toc, streamOffset + 8, type: 0, format: 2, index: 0);
		WriteComponent(toc, streamOffset + 8 + 20, type: 4, format: 29, index: 0);
		WriteUInt64(toc, streamOffset + 328, 2);
		WriteUInt32(toc, streamOffset + 352, 1);
		WriteUInt32(toc, streamOffset + 356, vertexStride);
		WriteUInt32(toc, streamOffset + 392, 3);
		WriteUInt32(toc, streamOffset + 396, 0);
		WriteUInt32(toc, streamOffset + 416, 0);
		WriteUInt32(toc, streamOffset + 420, vertexStride);
		WriteUInt32(toc, streamOffset + 424, vertexStride);
		WriteUInt32(toc, streamOffset + 428, 6);

		// MeshInfo table and one inline MeshInfo record.
		WriteUInt32(toc, meshInfoOffset, 1);
		WriteUInt32(toc, meshInfoOffset + 4, meshOffset - meshInfoOffset);
		WriteUInt32(toc, meshInfoOffset + 8, 0x99);
		WriteUInt32(toc, meshOffset + 40, 0x99);
		WriteUInt32(toc, meshOffset + 48, 0);
		WriteUInt32(toc, meshOffset + 56, 0);
		WriteUInt32(toc, meshOffset + 60, 0);
		WriteUInt32(toc, meshOffset + 104, 1);
		WriteUInt32(toc, meshOffset + 108, 128);
		WriteUInt32(toc, meshOffset + 120, 1);
		WriteUInt32(toc, meshOffset + 124, 132);
		WriteUInt32(toc, meshOffset + 128, 0x12345678);
		WriteUInt32(toc, meshOffset + 132, 0);
		WriteUInt32(toc, meshOffset + 136, 0);
		WriteUInt32(toc, meshOffset + 140, 1);
		WriteUInt32(toc, meshOffset + 144, 0);
		WriteUInt32(toc, meshOffset + 148, 3);
		WriteUInt32(toc, meshOffset + 152, 0);

		var gpu = new byte[vertexStride + 6];
		WriteSingle(gpu, 0, 1f);
		WriteSingle(gpu, 4, 2f);
		WriteSingle(gpu, 8, 3f);
		uvBytes.CopyTo(gpu, 12);

		return new UnitMeshReader().Read(toc, gpu);
	}

	private static void WriteComponent(byte[] data, int offset, uint type, uint format, uint index)
	{
		WriteUInt32(data, offset, type);
		WriteUInt32(data, offset + 4, format);
		WriteUInt32(data, offset + 8, index);
	}

	private static void WriteUInt64(byte[] data, int offset, ulong value)
		=> BitConverter.GetBytes(value).CopyTo(data, offset);

	private static void WriteUInt32(byte[] data, int offset, uint value)
		=> BitConverter.GetBytes(value).CopyTo(data, offset);

	private static void WriteSingle(byte[] data, int offset, float value)
		=> BitConverter.GetBytes(value).CopyTo(data, offset);
}
