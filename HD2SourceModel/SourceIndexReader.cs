using System.Buffers.Binary;

namespace HD2SourceModel;

/// <summary>
/// 解析 <c>.dx90.vtx</c>：把 bodypart / model / LOD / mesh / strip group 这棵树的索引读出来。
/// <para>
/// 这里只有一个真正的坑：<b>strip group 结构体的步长随版本变化</b>——v6 是 25 字节，v7 是 33 字节，
/// 而头里的版本号<b>不足以判断</b>：实测存在版本写着 7、步长却是 25 的文件。所以两种步长都试一遍，
/// 谁能自洽地解析完整棵树就用谁。步长用错时偏移会立刻错到界外，于是自洽性检查就能把它筛掉。
/// </para>
/// <para>
/// 顶点与索引的偏移都是<b>相对所在结构体自身</b>的字节偏移，不是相对文件头。
/// </para>
/// <para>
/// Parses a <c>.dx90.vtx</c> index tree.  The one real trap is that the strip-group struct
/// stride depends on the version (v6 = 25 bytes, v7 = 33 bytes) and the version number in the
/// header is <b>not</b> reliable -- files exist that say 7 but use the 25-byte layout.  So both
/// strides are attempted and the one that parses the whole tree consistently wins.  All offsets
/// are relative to the structure that owns them, not to the file header.
/// </para>
/// </summary>
public static class SourceIndexReader
{
	private const int VersionOffset = 0;
	private const int LodCountOffset = 20;
	private const int BodyPartCountOffset = 28;
	private const int BodyPartOffsetOffset = 32;
	private const int MinimumHeaderSize = 36;

	private const int BodyPartStride = 8;
	private const int ModelStride = 8;
	private const int LodStride = 12;
	private const int MeshStride = 9;

	/// <summary>v6 的 strip group 结构体步长。</summary>
	private const int StripGroupStrideV6 = 25;

	/// <summary>v7 的 strip group 结构体步长（多了 topology 两个字段与一个旗标字节）。</summary>
	private const int StripGroupStrideV7 = 33;

	/// <summary>VTX 顶点被打包成 9 字节；原始顶点号是其中第 4、5 字节的 uint16。</summary>
	private const int VtxVertexStride = 9;

	private const int OriginalVertexOffset = 4;
	private const int IndexStride = 2;

	private const int MinimumVersion = 6;
	private const int MaximumVersion = 7;

	private const int MaxBodyPartCount = 1024;
	private const int MaxModelCountPerBodyPart = 1024;
	private const int MaxLodCount = 8;
	private const int MaxMeshCountPerLod = 10000;
	private const int MaxStripGroupCountPerMesh = 100000;
	private const int MaxVertexCountPerGroup = 2_000_000;
	private const int MaxIndexCountPerGroup = 6_000_000;

	/// <summary>
	/// 读 VTX 索引树。返回的 bodypart 顺序与 MDL 的 bodypart 顺序一致，可直接按下标配对。
	/// </summary>
	/// <param name="vtx">VTX 文件的完整内容。</param>
	/// <param name="version">文件头里的版本号（6 或 7）。</param>
	/// <param name="usedV7Stride">实际解开的步长是否是 v7 的 33 字节（可能与头里的版本号不符）。</param>
	public static IReadOnlyList<SourceIndexBodyPart> Read(byte[] vtx, out int version, out bool usedV7Stride)
	{
		ArgumentNullException.ThrowIfNull(vtx);
		if (vtx.Length < MinimumHeaderSize)
			throw new InvalidDataException($"VTX 太小（{vtx.Length} 字节），不是有效的索引文件。");

		version = ReadInt32(vtx, VersionOffset);
		if (version is < MinimumVersion or > MaximumVersion)
			throw new NotSupportedException($"VTX 版本 {version} 不受支持（只接受 6 或 7）。");

		var lodCount = ReadInt32(vtx, LodCountOffset);
		var bodyPartCount = ReadInt32(vtx, BodyPartCountOffset);
		var bodyPartOffset = ReadInt32(vtx, BodyPartOffsetOffset);

		if (lodCount <= 0 || lodCount > MaxLodCount)
			throw new InvalidDataException($"VTX LOD 数量异常：{lodCount}。");
		if (bodyPartCount <= 0 || bodyPartCount > MaxBodyPartCount)
			throw new InvalidDataException($"VTX bodypart 数量异常：{bodyPartCount}。");

		// 先按 v6 的 25 字节试，失败再按 v7 的 33 字节试。步长用错时，第一次越界读就会抛
		// InvalidDataException，被这里吞掉去试另一种——所以这里的 catch 是"换一种布局"而不是掩盖错误。
		// 两种都失败时把两次的原因一起抛出来，否则真实原因会被"两种都解不开"这种笼统消息吃掉。
		var failures = new List<string>(2);
		for (var attempt = 0; attempt < 2; attempt++)
		{
			var useV7 = attempt == 1;
			try
			{
				var parts = ReadBodyParts(vtx, bodyPartCount, bodyPartOffset, lodCount, useV7);
				usedV7Stride = useV7;
				return parts;
			}
			catch (InvalidDataException error)
			{
				failures.Add($"{(useV7 ? "v7/33 字节" : "v6/25 字节")} → {error.Message}");
			}
		}

		throw new InvalidDataException(
			$"VTX 树用 v6(25 字节) 与 v7(33 字节) 两种 strip group 步长都解不开：{string.Join("；", failures)}");
	}

	private static IReadOnlyList<SourceIndexBodyPart> ReadBodyParts(
		byte[] vtx, int bodyPartCount, int bodyPartOffset, int lodCount, bool useV7)
	{
		var stripGroupStride = useV7 ? StripGroupStrideV7 : StripGroupStrideV6;
		if (!Fits(vtx, bodyPartOffset, (long)bodyPartCount * BodyPartStride))
			throw new InvalidDataException($"VTX bodypart 数组越界：offset={bodyPartOffset} count={bodyPartCount}。");

		var parts = new List<SourceIndexBodyPart>(bodyPartCount);
		for (var index = 0; index < bodyPartCount; index++)
		{
			var entry = bodyPartOffset + (index * BodyPartStride);
			var modelCount = ReadInt32(vtx, entry);
			var modelOffset = ReadInt32(vtx, entry + 4);
			if (modelCount < 0 || modelCount > MaxModelCountPerBodyPart)
				throw new InvalidDataException($"VTX bodypart {index} 的 model 数量异常：{modelCount}。");

			var models = new List<SourceIndexModel>(modelCount);
			if (modelCount > 0)
			{
				var modelStart = (long)entry + modelOffset;
				if (!Fits(vtx, modelStart, (long)modelCount * ModelStride))
					throw new InvalidDataException($"VTX bodypart {index} 的 model 数组越界。");

				for (var slot = 0; slot < modelCount; slot++)
					models.Add(ReadModel(vtx, (int)modelStart + (slot * ModelStride), lodCount, stripGroupStride));
			}

			parts.Add(new SourceIndexBodyPart(models));
		}

		return parts;
	}

	private static SourceIndexModel ReadModel(byte[] vtx, int entry, int lodCount, int stripGroupStride)
	{
		var modelLodCount = ReadInt32(vtx, entry);
		var lodOffset = ReadInt32(vtx, entry + 4);
		if (modelLodCount < 0 || modelLodCount > lodCount)
			throw new InvalidDataException($"VTX model 的 LOD 数量异常：{modelLodCount}（头里是 {lodCount}）。");

		var lods = new List<SourceIndexLod>(modelLodCount);
		if (modelLodCount > 0)
		{
			var lodStart = (long)entry + lodOffset;
			if (!Fits(vtx, lodStart, (long)modelLodCount * LodStride))
				throw new InvalidDataException("VTX model 的 LOD 数组越界。");

			for (var slot = 0; slot < modelLodCount; slot++)
				lods.Add(ReadLod(vtx, (int)lodStart + (slot * LodStride), stripGroupStride));
		}

		return new SourceIndexModel(lods);
	}

	private static SourceIndexLod ReadLod(byte[] vtx, int entry, int stripGroupStride)
	{
		var meshCount = ReadInt32(vtx, entry);
		var meshOffset = ReadInt32(vtx, entry + 4);
		if (meshCount < 0 || meshCount > MaxMeshCountPerLod)
			throw new InvalidDataException($"VTX LOD 的 mesh 数量异常：{meshCount}。");

		var meshes = new List<SourceIndexMesh>(meshCount);
		if (meshCount > 0)
		{
			var meshStart = (long)entry + meshOffset;
			if (!Fits(vtx, meshStart, (long)meshCount * MeshStride))
				throw new InvalidDataException("VTX LOD 的 mesh 数组越界。");

			for (var slot = 0; slot < meshCount; slot++)
				meshes.Add(ReadMesh(vtx, (int)meshStart + (slot * MeshStride), stripGroupStride));
		}

		return new SourceIndexLod(meshes);
	}

	private static SourceIndexMesh ReadMesh(byte[] vtx, int entry, int stripGroupStride)
	{
		var groupCount = ReadInt32(vtx, entry);
		var groupOffset = ReadInt32(vtx, entry + 4);
		if (groupCount < 0 || groupCount > MaxStripGroupCountPerMesh)
			throw new InvalidDataException($"VTX mesh 的 strip group 数量异常：{groupCount}。");

		var groups = new List<SourceIndexGroup>(groupCount);
		if (groupCount > 0)
		{
			var groupStart = (long)entry + groupOffset;
			if (!Fits(vtx, groupStart, (long)groupCount * stripGroupStride))
				throw new InvalidDataException("VTX mesh 的 strip group 数组越界。");

			for (var slot = 0; slot < groupCount; slot++)
				groups.Add(ReadGroup(vtx, (int)groupStart + (slot * stripGroupStride)));
		}

		return new SourceIndexMesh(groups);
	}

	private static SourceIndexGroup ReadGroup(byte[] vtx, int entry)
	{
		var vertexCount = ReadInt32(vtx, entry);
		var vertexOffset = ReadInt32(vtx, entry + 4);
		var indexCount = ReadInt32(vtx, entry + 8);
		var indexOffset = ReadInt32(vtx, entry + 12);

		if (vertexCount < 0 || vertexCount > MaxVertexCountPerGroup)
			throw new InvalidDataException($"VTX strip group 的顶点数量异常：{vertexCount}。");
		// 索引数量必须是 3 的倍数，否则这一层不是三角形列表——那说明步长选错了。
		if (indexCount < 0 || indexCount > MaxIndexCountPerGroup || indexCount % 3 != 0)
			throw new InvalidDataException($"VTX strip group 的索引数量异常：{indexCount}（必须是 3 的倍数）。");

		var vertexStart = (long)entry + vertexOffset;
		var indexStart = (long)entry + indexOffset;
		if (!Fits(vtx, vertexStart, (long)vertexCount * VtxVertexStride)
			|| !Fits(vtx, indexStart, (long)indexCount * IndexStride))
		{
			throw new InvalidDataException("VTX strip group 的顶点或索引数组越界。");
		}

		var vertexBase = (int)vertexStart;
		var indexBase = (int)indexStart;

		var originals = new ushort[vertexCount];
		for (var vertex = 0; vertex < vertexCount; vertex++)
			originals[vertex] = ReadUInt16(vtx, vertexBase + (vertex * VtxVertexStride) + OriginalVertexOffset);

		var indices = new ushort[indexCount];
		for (var slot = 0; slot < indexCount; slot++)
		{
			var index = ReadUInt16(vtx, indexBase + (slot * IndexStride));
			if (index >= vertexCount)
				throw new InvalidDataException($"VTX strip group 的索引 {index} 超出顶点数 {vertexCount}。");

			indices[slot] = index;
		}

		return new SourceIndexGroup(originals, indices);
	}

	/// <summary>
	/// 读 4 字节整数；越界抛 <see cref="InvalidDataException"/> 而不是让 Span 抛参数异常——
	/// 只有抛这个类型，"换一种步长再试"的循环才能正确接管。
	/// </summary>
	private static int ReadInt32(byte[] buffer, int offset)
	{
		if (!Fits(buffer, offset, 4))
			throw new InvalidDataException($"VTX 读取越界：offset={offset}。");

		return BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(offset));
	}

	private static ushort ReadUInt16(byte[] buffer, int offset)
	{
		if (!Fits(buffer, offset, 2))
			throw new InvalidDataException($"VTX 读取越界：offset={offset}。");

		return BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset));
	}

	private static bool Fits(byte[] buffer, long offset, long length) =>
		offset >= 0 && length >= 0 && offset + length <= buffer.Length;
}
