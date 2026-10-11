using System.Buffers.Binary;
using static HD2SourceModel.Tests.SourceFormatFixtures;

namespace HD2SourceModel.Tests;

/// <summary>
/// VVD 解析：顶点位置 / 法线 / UV / 蒙皮权重，以及 <b>fixup 重排</b>。
/// </summary>
public sealed class SourceVertexReaderTests
{
	private static VertexSpec Vertex(float x, float y, float z, byte bone = 0, float weight = 1f) =>
		new([weight, 0f, 0f], [bone, 0, 0], new Vec3(x, y, z), new Vec3(0, 1, 0), x / 10f, y / 10f);

	[Fact]
	public void Read_RejectsForeignMagic()
	{
		var vvd = BuildVvd([Vertex(0, 0, 0)]);
		vvd[0] = (byte)'X';

		Assert.Throws<InvalidDataException>(() => SourceVertexReader.Read(vvd, out _, out _));
	}

	[Fact]
	public void Read_RejectsUnsupportedVersion()
	{
		var vvd = BuildVvd([Vertex(0, 0, 0)]);
		BinaryPrimitives.WriteInt32LittleEndian(vvd.AsSpan(4), 7);

		Assert.Throws<NotSupportedException>(() => SourceVertexReader.Read(vvd, out _, out _));
	}

	[Fact]
	public void Read_ReadsPositionNormalUvAndWeights()
	{
		var spec = new VertexSpec(
			[0.75f, 0.25f, 0f], [3, 5, 0],
			new Vec3(1.5, 2.5, 3.5), new Vec3(0, 0, 1), 0.125f, 0.875f);

		var vertices = SourceVertexReader.Read(BuildVvd([spec]), out var lodCount, out var lod0Count);

		Assert.Equal(1, lodCount);
		Assert.Equal(1, lod0Count);
		var vertex = Assert.Single(vertices);
		Assert.Equal(new Vec3(1.5, 2.5, 3.5), vertex.Position);
		Assert.Equal(new Vec3(0, 0, 1), vertex.Normal);
		Assert.Equal(0.125f, vertex.U);
		Assert.Equal(0.875f, vertex.V);

		// 权重按文件原值读出，归一化留给真正蒙皮的那一步。
		var influences = vertex.Influences().ToArray();
		Assert.Equal(2, influences.Length);
		Assert.Equal(3, influences[0].BoneIndex);
		Assert.Equal(0.75f, influences[0].Weight);
		Assert.Equal(5, influences[1].BoneIndex);
		Assert.Equal(0.25f, influences[1].Weight);
	}

	[Fact]
	public void Read_AppliesFixupReorder()
	{
		// 文件里的顶点不是按最终顺序排的；不按 fixup 重排，整个网格的索引都会指错顶点。
		VertexSpec[] stored =
		[
			Vertex(0, 0, 0),
			Vertex(1, 0, 0),
			Vertex(2, 0, 0),
			Vertex(3, 0, 0),
		];

		var vertices = SourceVertexReader.Read(
			BuildVvd(stored, [new FixupSpec(0, 2, 2), new FixupSpec(0, 0, 2)]),
			out _,
			out var lod0Count);

		Assert.Equal(4, lod0Count);
		Assert.Equal(new Vec3(2, 0, 0), vertices[0].Position);
		Assert.Equal(new Vec3(3, 0, 0), vertices[1].Position);
		Assert.Equal(new Vec3(0, 0, 0), vertices[2].Position);
		Assert.Equal(new Vec3(1, 0, 0), vertices[3].Position);
	}

	[Fact]
	public void Read_SkipsFixupEntriesBelongingToOtherLods()
	{
		// lod < 0 表示那一段属于别的 LOD，LOD0 必须跳过它。
		VertexSpec[] stored = [Vertex(0, 0, 0), Vertex(1, 0, 0)];

		var vertices = SourceVertexReader.Read(
			BuildVvd(stored, [new FixupSpec(-1, 0, 2), new FixupSpec(0, 1, 1), new FixupSpec(0, 0, 1)]),
			out _,
			out _);

		Assert.Equal(2, vertices.Count);
		Assert.Equal(new Vec3(1, 0, 0), vertices[0].Position);
		Assert.Equal(new Vec3(0, 0, 0), vertices[1].Position);
	}

	[Fact]
	public void Read_ThrowsWhenFixupsDoNotAccountForEveryLod0Vertex()
	{
		// 拼出来的数量与头里声明的不符，说明解析错了；这里必须炸，不能悄悄少给顶点。
		VertexSpec[] stored = [Vertex(0, 0, 0), Vertex(1, 0, 0)];

		var error = Assert.Throws<InvalidDataException>(() =>
			SourceVertexReader.Read(BuildVvd(stored, [new FixupSpec(0, 0, 1)]), out _, out _));

		Assert.Contains("fixup", error.Message, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void Read_ThrowsWhenTheVertexArrayRunsPastTheFile()
	{
		var vvd = BuildVvd([Vertex(0, 0, 0)]);
		// 头里声明 99 个顶点，但文件里只有一个。
		BinaryPrimitives.WriteInt32LittleEndian(vvd.AsSpan(16), 99);

		Assert.Throws<InvalidDataException>(() => SourceVertexReader.Read(vvd, out _, out _));
	}
}
