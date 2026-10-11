using System.Buffers.Binary;

namespace HD2SourceModel;

/// <summary>
/// 解析 <c>.vvd</c>：顶点位置、法线、UV 与蒙皮权重。
/// <para>
/// 关键细节是 <b>fixup 重排</b>：顶点在文件里不是按最终顺序排的，要按 fixup 表（每条 12 字节：
/// lod / vertexIndex / count）拼出 LOD0 的序列，且只有 <c>lod &gt;= 0</c> 的条目才拷贝。
/// 拼出来的数量必须等于头里的 <c>lodVertices[0]</c>，否则说明解析错了。
/// </para>
/// <para>
/// Parses a <c>.vvd</c>.  The trap is the <b>fixup reorder</b>: the stored vertices are not in
/// final order, and LOD0 has to be assembled from the fixup table (12 bytes each: lod,
/// vertexIndex, count), copying only entries with <c>lod &gt;= 0</c>.  The assembled count must
/// equal the header's <c>lodVertices[0]</c> or the parse is wrong.
/// </para>
/// </summary>
public static class SourceVertexReader
{
	private const int VertexStride = 48;
	private const int FixupStride = 12;
	private const int LodCountOffset = 12;
	private const int LodVertexCountOffset = 16;
	private const int FixupCountOffset = 48;
	private const int FixupOffsetOffset = 52;
	private const int VertexOffsetOffset = 56;

	private const int MaxLodCount = 8;
	private const int MaxFixupCount = 4_000_000;

	public static IReadOnlyList<SourceVertex> Read(byte[] vvd, out int lodCount, out int lod0VertexCount)
	{
		ArgumentNullException.ThrowIfNull(vvd);
		if (vvd.Length < 64 || vvd[0] != (byte)'I' || vvd[1] != (byte)'D' || vvd[2] != (byte)'S' || vvd[3] != (byte)'V')
			throw new InvalidDataException("VVD magic 不是 IDSV，不是 Source 顶点文件。");

		var version = BinaryPrimitives.ReadInt32LittleEndian(vvd.AsSpan(4));
		if (version is not (4 or 5))
			throw new NotSupportedException($"VVD 版本 {version} 不受支持（只接受 4 或 5）。");

		lodCount = BinaryPrimitives.ReadInt32LittleEndian(vvd.AsSpan(LodCountOffset));
		if (lodCount <= 0 || lodCount > MaxLodCount)
			throw new InvalidDataException($"VVD LOD 数量异常：{lodCount}。");

		lod0VertexCount = BinaryPrimitives.ReadInt32LittleEndian(vvd.AsSpan(LodVertexCountOffset));
		var fixupCount = BinaryPrimitives.ReadInt32LittleEndian(vvd.AsSpan(FixupCountOffset));
		var fixupOffset = BinaryPrimitives.ReadInt32LittleEndian(vvd.AsSpan(FixupOffsetOffset));
		var vertexOffset = BinaryPrimitives.ReadInt32LittleEndian(vvd.AsSpan(VertexOffsetOffset));

		if (lod0VertexCount < 0 || vertexOffset < 0
			|| (long)vertexOffset + ((long)lod0VertexCount * VertexStride) > vvd.Length)
		{
			throw new InvalidDataException($"VVD 顶点数组越界：offset={vertexOffset} count={lod0VertexCount}。");
		}

		var stored = new SourceVertex[lod0VertexCount];
		for (var index = 0; index < lod0VertexCount; index++)
			stored[index] = ReadVertex(vvd, vertexOffset + (index * VertexStride));

		if (fixupCount == 0)
			return stored;

		if (fixupCount < 0 || fixupCount > MaxFixupCount
			|| (long)fixupOffset + ((long)fixupCount * FixupStride) > vvd.Length)
		{
			throw new InvalidDataException($"VVD fixup 表越界：offset={fixupOffset} count={fixupCount}。");
		}

		var ordered = new SourceVertex[lod0VertexCount];
		var written = 0;
		for (var index = 0; index < fixupCount; index++)
		{
			var entry = fixupOffset + (index * FixupStride);
			var lod = BinaryPrimitives.ReadInt32LittleEndian(vvd.AsSpan(entry));
			var sourceIndex = BinaryPrimitives.ReadInt32LittleEndian(vvd.AsSpan(entry + 4));
			var count = BinaryPrimitives.ReadInt32LittleEndian(vvd.AsSpan(entry + 8));

			// lod < 0 表示这一段属于别的 LOD，LOD0 不拷贝。
			if (lod < 0)
				continue;

			if (count < 0 || sourceIndex < 0 || written + count > ordered.Length
				|| (long)sourceIndex + count > stored.Length)
			{
				throw new InvalidDataException($"VVD fixup 第 {index} 条越界，无法拼出 LOD0。");
			}

			Array.Copy(stored, sourceIndex, ordered, written, count);
			written += count;
		}

		if (written != lod0VertexCount)
		{
			throw new InvalidDataException(
				$"VVD fixup 只拼出 {written} 个顶点，头里声明的是 {lod0VertexCount} 个。");
		}

		return ordered;
	}

	private static SourceVertex ReadVertex(byte[] vvd, int offset)
	{
		var weight0 = BinaryPrimitives.ReadSingleLittleEndian(vvd.AsSpan(offset));
		var weight1 = BinaryPrimitives.ReadSingleLittleEndian(vvd.AsSpan(offset + 4));
		var weight2 = BinaryPrimitives.ReadSingleLittleEndian(vvd.AsSpan(offset + 8));

		var bone0 = vvd[offset + 12];
		var bone1 = vvd[offset + 13];
		var bone2 = vvd[offset + 14];

		var position = new Vec3(
			BinaryPrimitives.ReadSingleLittleEndian(vvd.AsSpan(offset + 16)),
			BinaryPrimitives.ReadSingleLittleEndian(vvd.AsSpan(offset + 20)),
			BinaryPrimitives.ReadSingleLittleEndian(vvd.AsSpan(offset + 24)));

		var normal = new Vec3(
			BinaryPrimitives.ReadSingleLittleEndian(vvd.AsSpan(offset + 28)),
			BinaryPrimitives.ReadSingleLittleEndian(vvd.AsSpan(offset + 32)),
			BinaryPrimitives.ReadSingleLittleEndian(vvd.AsSpan(offset + 36)));

		var u = BinaryPrimitives.ReadSingleLittleEndian(vvd.AsSpan(offset + 40));
		var v = BinaryPrimitives.ReadSingleLittleEndian(vvd.AsSpan(offset + 44));

		return new SourceVertex(
			position, normal, u, v,
			new SourceVertexWeight(bone0, weight0),
			new SourceVertexWeight(bone1, weight1),
			new SourceVertexWeight(bone2, weight2));
	}
}
