using System.Text;

namespace HD2ModAdaptation.PatchReconstruction.UnitMesh;

// Purpose: Selects which raw byte mirrors a Unit parser retains after decoding typed mesh semantics.
public sealed record UnitMeshReadOptions(
	bool PreserveVertexData = true,
	bool PreserveComponentRawData = true,
	bool BuildTriangleMirror = true,
	// 只读结构（mesh / section / material）时可以完全跳过原始网格数据。
	// 这一步会逐顶点解码分量：一个头盔 unit 有 12 万个顶点，光这一步就是秒级；
	// 而"某 mesh 有几个 section、每个 section 多少索引"全在 TOC 里，根本不需要它。
	// Skipping raw mesh data is safe whenever only the structural facts are wanted: the
	// per-vertex decode is seconds on a 120k-vertex helmet, while section counts live in the TOC.
	bool BuildRawMeshData = true)
{
	public static UnitMeshReadOptions CanonicalSource { get; } = new(
		PreserveVertexData: false,
		PreserveComponentRawData: false,
		BuildTriangleMirror: false);

	/// <summary>只要结构：不解顶点、不建三角形镜像、不留原始字节。</summary>
	public static UnitMeshReadOptions StructureOnly { get; } = new(
		PreserveVertexData: false,
		PreserveComponentRawData: false,
		BuildTriangleMirror: false,
		BuildRawMeshData: false);
}

// 浣滅敤锛氱Щ妞?HD2SDK StingrayMeshFile 鐨勫彧璇荤粨鏋勮В鏋愰儴鍒嗭紝璇诲彇 Unit mesh/stream/material 鎽樿銆?
// Purpose: Ports the read-only structural parsing portion of HD2SDK StingrayMeshFile for Unit mesh/stream/material summaries.
public sealed class UnitMeshReader
{
	private const int StreamInfoComponentBlockSize = 320;
	private const uint UnsupportedOffset = 0;

	public UnitMeshModel Read(ReadOnlySpan<byte> tocData, ReadOnlySpan<byte> gpuData, ReadOnlySpan<byte> compositeTocData = default, ReadOnlySpan<byte> compositeGpuData = default, UnitBoneNames? boneNames = null, UnitMeshReadOptions? options = null)
	{
		try
		{
			return ReadCore(tocData, gpuData, compositeTocData, compositeGpuData, boneNames ?? UnitBoneNames.Empty, options ?? new UnitMeshReadOptions());
		}
		catch (OverflowException ex)
		{
			throw new InvalidDataException("Unit mesh data contains out-of-range counts or offsets.", ex);
		}
	}

	private static UnitMeshModel ReadCore(ReadOnlySpan<byte> tocData, ReadOnlySpan<byte> gpuData, ReadOnlySpan<byte> compositeTocData, ReadOnlySpan<byte> compositeGpuData, UnitBoneNames boneNames, UnitMeshReadOptions options)
	{
		if (tocData.Length < 96)
		{
			throw new InvalidDataException("Unit TocData is too small to contain a Stingray unit header.");
		}

		var nameHash = ReadUInt64(tocData, 0);
		var bonesRef = ReadUInt64(tocData, 8);
		var compositeRef = ReadUInt64(tocData, 16);
		var version = ReadUInt32(tocData, 0x2c);
		var unreversedLodGroupListDataOffset = ReadUInt32(tocData, 0x30);
		var transformInfoOffset = ReadUInt32(tocData, 0x34);
		// HD2SDK StingrayMeshFile serializes CustomizationInfoOffset after HeaderData2 at 0x4C.
		// 0x50 is UnkHeaderOffset1 and produces empty/garbled customization values.
		var customizationInfoOffset = ReadUInt32(tocData, 0x4c);
		var boneInfoOffset = ReadUInt32(tocData, 0x58);
		var streamInfoOffset = ReadUInt32(tocData, 0x5c);
		var endingOffset = ReadUInt32(tocData, 0x60);
		var meshInfoOffset = ReadUInt32(tocData, 0x64);
		var materialsOffset = ReadUInt32(tocData, 0x70);

		if (meshInfoOffset == UnsupportedOffset)
		{
			throw new InvalidDataException("Unsupported Unit mesh format: MeshInfoOffset is zero.");
		}

		if (streamInfoOffset == UnsupportedOffset && compositeRef == 0)
		{
			throw new InvalidDataException("Unsupported Unit mesh format: StreamInfoOffset is zero and no CompositeRef is present.");
		}

		if (streamInfoOffset == UnsupportedOffset && compositeTocData.IsEmpty)
		{
			throw new InvalidDataException($"Composite-backed Unit requires Composite Unit payload 0x{compositeRef:x16}.");
		}

		var isCompositeBacked = streamInfoOffset == UnsupportedOffset;
		var streamRead = streamInfoOffset == UnsupportedOffset
			? UnitStreamReadResult.Empty(version)
			: ReadStreams(tocData, streamInfoOffset, version);
		var streams = streamRead.Streams;
		var streamAbiResolution = streamRead.Resolution;
		var customizationInfo = customizationInfoOffset == UnsupportedOffset
			? UnitCustomizationInfo.Empty
			: ReadCustomizationInfo(tocData, customizationInfoOffset);
		var boneInfos = boneInfoOffset == UnsupportedOffset
			? Array.Empty<UnitBoneInfo>()
			: ReadBoneInfos(tocData, boneInfoOffset, streamInfoOffset == UnsupportedOffset ? meshInfoOffset : streamInfoOffset);
		var meshes = ReadMeshes(tocData, meshInfoOffset, readInlineMaterialSections: !isCompositeBacked, customizationInfo, boneNames);
		var effectiveGpuData = gpuData;
		if (isCompositeBacked)
		{
			var composite = ReadComposite(compositeTocData, nameHash);
			streams = composite.Streams;
			streamAbiResolution = composite.StreamAbiResolution;
			meshes = ApplyCompositeMeshInfo(meshes, composite.Meshes);
			effectiveGpuData = compositeGpuData.IsEmpty ? gpuData : compositeGpuData;
		}
		var materials = materialsOffset == UnsupportedOffset
			? Array.Empty<UnitMaterialBinding>()
			: ReadMaterials(tocData, materialsOffset);
		var rawMeshes = BuildRawMeshSummaries(meshes, streams, effectiveGpuData.Length);
		var rawMeshData = options.BuildRawMeshData
			? ReadRawMeshData(meshes, streams, effectiveGpuData, options)
			: Array.Empty<UnitRawMeshData>();
		meshes = ApplySdkMeshClassification(meshes, rawMeshData, materials);
		var transformInfo = transformInfoOffset == UnsupportedOffset
			? UnitTransformInfo.Empty
			: ReadTransformInfo(tocData, transformInfoOffset);
		var unreversedLodGroupListData = ReadUnreversedLodGroupListData(tocData, unreversedLodGroupListDataOffset, transformInfoOffset, customizationInfoOffset, ReadUInt32(tocData, 0x50), ReadUInt32(tocData, 0x54), boneInfoOffset, streamInfoOffset, meshInfoOffset);

		return new UnitMeshModel(
			version,
			nameHash,
			bonesRef,
			compositeRef,
			customizationInfoOffset,
			boneInfoOffset,
			streamInfoOffset,
			meshInfoOffset,
			materialsOffset,
			endingOffset,
			customizationInfo,
			boneInfos,
			streams,
			meshes,
			materials,
			rawMeshes,
			rawMeshData)
		{
			StreamAbiResolution = streamAbiResolution,
			UnreversedLodGroupListDataOffset = unreversedLodGroupListDataOffset,
			UnreversedLodGroupListData = unreversedLodGroupListData,
			TransformInfoOffset = transformInfoOffset,
			TransformInfo = transformInfo,
			TransformNameHashes = transformInfo.NameHashes
		};
	}

	private static byte[] ReadUnreversedLodGroupListData(ReadOnlySpan<byte> data, uint startOffset, params uint[] followingOffsets)
	{
		if (startOffset == UnsupportedOffset || startOffset >= data.Length) return Array.Empty<byte>();
		uint endOffset = (uint)data.Length;
		foreach (var offset in followingOffsets)
		{
			if (offset > startOffset && offset <= data.Length && offset < endOffset) endOffset = offset;
		}
		return data.Slice(checked((int)startOffset), checked((int)(endOffset - startOffset))).ToArray();
	}

	public static UnitTransformInfo ReadTransformInfoFromUnitToc(ReadOnlySpan<byte> tocData)
	{
		if (tocData.Length < 0x38) throw new InvalidDataException("Unit TocData is too small to contain TransformInfoOffset.");
		var transformInfoOffset = ReadUInt32(tocData, 0x34);
		return transformInfoOffset == UnsupportedOffset ? UnitTransformInfo.Empty : ReadTransformInfo(tocData, transformInfoOffset);
	}

	private static UnitTransformInfo ReadTransformInfo(ReadOnlySpan<byte> data, uint transformInfoOffset)
	{
		EnsureRange(data, transformInfoOffset, 16, "transform info header");
		var count = checked((int)ReadUInt32(data, transformInfoOffset));
		var reserved0 = ReadUInt32(data, transformInfoOffset + 4);
		var reserved1 = ReadUInt32(data, transformInfoOffset + 8);
		var reserved2 = ReadUInt32(data, transformInfoOffset + 12);
		var localOffset = checked((int)transformInfoOffset + 16);
		var matricesOffset = checked(localOffset + count * 64);
		var entriesOffset = checked(matricesOffset + count * 64);
		var hashesOffset = checked(entriesOffset + count * 4);
		EnsureRange(data, hashesOffset, checked(count * 4), "transform name hashes");

		var locals = new UnitLocalTransform[count];
		var matrices = new UnitTransformMatrix[count];
		var entries = new UnitTransformEntry[count];
		var hashes = new uint[count];
		for (var i = 0; i < count; i++)
		{
			var itemOffset = checked(localOffset + i * 64);
			locals[i] = new UnitLocalTransform(
				ReadSingles(data, itemOffset, 9),
				ReadSingles(data, itemOffset + 36, 3),
				ReadSingles(data, itemOffset + 48, 3),
				ReadSingle(data, itemOffset + 60));
			matrices[i] = new UnitTransformMatrix(ReadSingles(data, checked(matricesOffset + i * 64), 16));
			entries[i] = new UnitTransformEntry(ReadUInt16(data, checked(entriesOffset + i * 4)), ReadUInt16(data, checked(entriesOffset + i * 4 + 2)));
			hashes[i] = ReadUInt32(data, hashesOffset + i * 4);
		}

		return new UnitTransformInfo(reserved0, reserved1, reserved2, locals, matrices, entries, hashes);
	}

	private static float[] ReadSingles(ReadOnlySpan<byte> data, int offset, int count)
	{
		EnsureRange(data, offset, checked(count * 4), "float array");
		var values = new float[count];
		for (var i = 0; i < count; i++) values[i] = ReadSingle(data, checked(offset + i * 4));
		return values;
	}

	private static IReadOnlyList<UnitBoneInfo> ReadBoneInfos(ReadOnlySpan<byte> data, uint boneInfoOffset, uint endOffset)
	{
		EnsureRange(data, boneInfoOffset, 4, "bone info count");
		var count = checked((int)ReadUInt32(data, boneInfoOffset));
		var tableOffset = checked((int)boneInfoOffset + 4);
		EnsureRange(data, tableOffset, checked(count * 4), "bone info offset table");

		var offsets = new uint[count];
		for (var i = 0; i < count; i++)
		{
			offsets[i] = ReadUInt32(data, tableOffset + i * 4);
		}

		var result = new List<UnitBoneInfo>(count);
		for (var i = 0; i < count; i++)
		{
			var absoluteOffset = checked(boneInfoOffset + offsets[i]);
			var itemEndOffset = i + 1 < count ? checked(boneInfoOffset + offsets[i + 1]) : endOffset;
			result.Add(ReadBoneInfo(data, i, absoluteOffset, itemEndOffset));
		}

		return result;
	}

	private static UnitBoneInfo ReadBoneInfo(ReadOnlySpan<byte> data, int index, uint absoluteOffset, uint endOffset)
	{
		EnsureRange(data, absoluteOffset, 16, $"bone info {index}");
		var numBones = ReadUInt32(data, absoluteOffset);
		var matrixOffset = ReadUInt32(data, absoluteOffset + 4);
		var realIndicesOffset = ReadUInt32(data, absoluteOffset + 8);
		var remapDataOffset = ReadUInt32(data, absoluteOffset + 12);

		var matrices = ReadBoneInfoMatrices(data, absoluteOffset, endOffset, numBones, matrixOffset);
		var realIndices = ReadBoneInfoRealIndices(data, absoluteOffset, endOffset, numBones, realIndicesOffset);
		var remaps = ReadBoneInfoRemaps(data, absoluteOffset, endOffset, remapDataOffset);
		return new UnitBoneInfo(index, absoluteOffset, numBones, matrixOffset, realIndicesOffset, remapDataOffset, realIndices, remaps)
		{
			BoneMatrices = matrices
		};
	}

	private static IReadOnlyList<byte[]> ReadBoneInfoMatrices(ReadOnlySpan<byte> data, uint absoluteOffset, uint endOffset, uint numBones, uint matrixOffset)
	{
		var matrixStart = checked(absoluteOffset + matrixOffset);
		EnsureRangeWithin(data, matrixStart, checked((int)(numBones * 64)), endOffset, "bone info matrices");
		var matrices = new byte[checked((int)numBones)][];
		for (var i = 0; i < matrices.Length; i++)
		{
			matrices[i] = data.Slice(checked((int)matrixStart + i * 64), 64).ToArray();
		}
		return matrices;
	}

	private static IReadOnlyList<uint> ReadBoneInfoRealIndices(ReadOnlySpan<byte> data, uint absoluteOffset, uint endOffset, uint numBones, uint realIndicesOffset)
	{
		var realIndicesStart = checked(absoluteOffset + realIndicesOffset);
		var bytes = checked((int)(numBones * 4));
		EnsureRangeWithin(data, realIndicesStart, bytes, endOffset, "bone info real indices");

		var realIndices = new uint[checked((int)numBones)];
		for (var i = 0; i < realIndices.Length; i++)
		{
			realIndices[i] = ReadUInt32(data, checked((int)realIndicesStart + i * 4));
		}

		return realIndices;
	}

	private static IReadOnlyList<UnitBoneRemap> ReadBoneInfoRemaps(ReadOnlySpan<byte> data, uint absoluteOffset, uint endOffset, uint remapDataOffset)
	{
		var remapStart = checked(absoluteOffset + remapDataOffset);
		EnsureRangeWithin(data, remapStart, 4, endOffset, "bone info remap count");
		var count = checked((int)ReadUInt32(data, remapStart));
		var tableStart = checked((int)remapStart + 4);
		EnsureRangeWithin(data, (uint)tableStart, checked(count * 8), endOffset, "bone info remap table");

		var offsets = new uint[count];
		var counts = new uint[count];
		var cursor = tableStart;
		for (var i = 0; i < count; i++)
		{
			offsets[i] = ReadUInt32(data, cursor); cursor += 4;
			counts[i] = ReadUInt32(data, cursor); cursor += 4;
		}

		var remaps = new List<UnitBoneRemap>(count);
		for (var i = 0; i < count; i++)
		{
			var valuesStart = checked(remapStart + offsets[i]);
			var bytes = checked((int)(counts[i] * 4));
			EnsureRangeWithin(data, valuesStart, bytes, endOffset, $"bone info remap {i}");

			var fakeIndices = new uint[checked((int)counts[i])];
			for (var j = 0; j < fakeIndices.Length; j++)
			{
				fakeIndices[j] = ReadUInt32(data, checked((int)valuesStart + j * 4));
			}

			remaps.Add(new UnitBoneRemap(i, offsets[i], fakeIndices));
		}

		return remaps;
	}

	private sealed record CompositeMeshInfoItem(
		uint StreamIndex,
		uint NumMaterials,
		uint MaterialOffset,
		IReadOnlyList<uint> MaterialSlotIds,
		uint NumSections,
		uint SectionsOffset,
		IReadOnlyList<UnitMeshSectionInfo> Sections);

	private sealed record CompositeUnitMeshInfo(
		IReadOnlyList<UnitStreamInfo> Streams,
		UnitStreamAbiResolution StreamAbiResolution,
		IReadOnlyDictionary<uint, CompositeMeshInfoItem> Meshes);

	private static CompositeUnitMeshInfo ReadComposite(ReadOnlySpan<byte> data, ulong unitNameHash)
	{
		EnsureRange(data, 0, 16, "composite header");
		var unitCount = checked((int)ReadUInt32(data, 8));
		var streamInfoOffset = ReadUInt32(data, 12);
		var unitTableOffset = 16;
		EnsureRange(data, unitTableOffset, checked(unitCount * 20), "composite unit table");

		var unitHashes = new ulong[unitCount];
		var meshInfoOffsets = new uint[unitCount];
		var cursor = unitTableOffset;
		for (var i = 0; i < unitCount; i++)
		{
			cursor += 8; // unit type hash
			unitHashes[i] = ReadUInt64(data, cursor); cursor += 8;
		}

		for (var i = 0; i < unitCount; i++)
		{
			meshInfoOffsets[i] = ReadUInt32(data, cursor); cursor += 4;
		}

		var unitIndex = Array.IndexOf(unitHashes, unitNameHash);
		if (unitIndex < 0)
		{
			throw new InvalidDataException("Composite Unit payload does not contain the requested Unit name hash.");
		}

		var streamRead = streamInfoOffset == UnsupportedOffset
			? UnitStreamReadResult.Empty(0)
			: ReadStreams(data, streamInfoOffset, 0);
		var meshes = ReadCompositeUnitMeshes(data, meshInfoOffsets[unitIndex]);
		return new CompositeUnitMeshInfo(streamRead.Streams, streamRead.Resolution, meshes);
	}

	private static IReadOnlyDictionary<uint, CompositeMeshInfoItem> ReadCompositeUnitMeshes(ReadOnlySpan<byte> data, uint offset)
	{
		EnsureRange(data, offset, 4, "composite mesh info count");
		var start = checked((int)offset);
		var count = checked((int)ReadUInt32(data, start));
		EnsureRange(data, start + 4, checked(count * 8), "composite mesh info table");

		var meshIds = new uint[count];
		var itemOffsets = new uint[count];
		var cursor = start + 4;
		for (var i = 0; i < count; i++)
		{
			meshIds[i] = ReadUInt32(data, cursor); cursor += 4;
		}
		for (var i = 0; i < count; i++)
		{
			itemOffsets[i] = ReadUInt32(data, cursor); cursor += 4;
		}

		var result = new Dictionary<uint, CompositeMeshInfoItem>();
		for (var i = 0; i < count; i++)
		{
			var itemStart = checked(start + (int)itemOffsets[i]);
			EnsureRange(data, itemStart, 48, $"composite mesh info item {i}");
			var streamIndex = ReadUInt32(data, itemStart);
			var numMaterials = ReadUInt32(data, itemStart + 24);
			var materialOffset = ReadUInt32(data, itemStart + 28);
			var numSections = ReadUInt32(data, itemStart + 40);
			var sectionsOffset = ReadUInt32(data, itemStart + 44);

			var materialSlotIds = ReadCompositeMaterialSlots(data, itemStart, numMaterials, materialOffset);
			var sections = ReadCompositeSections(data, itemStart, materialSlotIds, numSections, sectionsOffset);
			result[meshIds[i]] = new CompositeMeshInfoItem(
				streamIndex,
				numMaterials,
				checked((uint)(itemStart + materialOffset)),
				materialSlotIds,
				numSections,
				checked((uint)(itemStart + sectionsOffset)),
				sections);
		}

		return result;
	}

	private static IReadOnlyList<uint> ReadCompositeMaterialSlots(ReadOnlySpan<byte> data, int itemStart, uint count, uint relativeOffset)
	{
		var materialCount = checked((int)count);
		var offset = checked(itemStart + (int)relativeOffset);
		EnsureRange(data, offset, checked(materialCount * 4), "composite material slots");

		var result = new uint[materialCount];
		for (var i = 0; i < materialCount; i++)
		{
			result[i] = ReadUInt32(data, offset + i * 4);
		}
		return result;
	}

	private static IReadOnlyList<UnitMeshSectionInfo> ReadCompositeSections(ReadOnlySpan<byte> data, int itemStart, IReadOnlyList<uint> materialSlotIds, uint count, uint relativeOffset)
	{
		var sectionCount = checked((int)count);
		var cursor = checked(itemStart + (int)relativeOffset);
		EnsureRange(data, cursor, checked(sectionCount * 24), "composite mesh sections");

		var sections = new List<UnitMeshSectionInfo>(sectionCount);
		for (var i = 0; i < sectionCount; i++)
		{
			var sectionOffset = checked((uint)cursor);
			var materialIndex = ReadUInt32(data, cursor); cursor += 4;
			var materialSlotId = materialIndex < materialSlotIds.Count ? materialSlotIds[(int)materialIndex] : 0;
			var vertexOffset = ReadUInt32(data, cursor); cursor += 4;
			var numVertices = ReadUInt32(data, cursor); cursor += 4;
			var indexOffset = ReadUInt32(data, cursor); cursor += 4;
			var numIndices = ReadUInt32(data, cursor); cursor += 4;
			var groupIndex = ReadUInt32(data, cursor); cursor += 4;
			sections.Add(new UnitMeshSectionInfo(sectionOffset, materialIndex, materialSlotId, vertexOffset, numVertices, indexOffset, numIndices, groupIndex));
		}

		return sections;
	}

	private static IReadOnlyList<UnitMeshInfo> ApplyCompositeMeshInfo(IReadOnlyList<UnitMeshInfo> unitMeshes, IReadOnlyDictionary<uint, CompositeMeshInfoItem> compositeMeshes)
	{
		var result = new List<UnitMeshInfo>(unitMeshes.Count);
		foreach (var mesh in unitMeshes)
		{
			if (!compositeMeshes.TryGetValue(mesh.MeshId, out var composite))
			{
				result.Add(mesh);
				continue;
			}

			result.Add(mesh with
			{
				StreamIndex = composite.StreamIndex,
				NumMaterials = composite.NumMaterials,
				MaterialOffset = composite.MaterialOffset,
				NumSections = composite.NumSections,
				SectionsOffset = composite.SectionsOffset,
				MaterialSlotIds = composite.MaterialSlotIds,
				Sections = composite.Sections
			});
		}

		return result;
	}

	private sealed record UnitStreamReadResult(
		IReadOnlyList<UnitStreamInfo> Streams,
		UnitStreamAbiResolution Resolution)
	{
		public static UnitStreamReadResult Empty(uint unitVersion)
			=> new(Array.Empty<UnitStreamInfo>(), new UnitStreamAbiResolution(
				UnitStreamAbiResolutionPath.ExactVersionProfile,
				UnitStreamAbiProfileKind.Current10800438,
				unitVersion,
				["Unit 不包含可解析的 StreamInfo。"]));
	}

	private sealed record RawUnitStreamInfo(
		int Index,
		uint Offset,
		ulong ComponentInfoId,
		ulong NumComponents,
		ulong VertexBufferId,
		uint NumVertices,
		uint VertexStride,
		ulong IndexBufferId,
		uint NumIndices,
		uint IndexBufferType,
		uint VertexBufferOffset,
		uint VertexBufferSize,
		uint IndexBufferOffset,
		uint IndexBufferSize,
		IReadOnlyList<UnitStreamAbiRawComponent> Components)
	{
		public UnitStreamInfo Resolve(UnitStreamAbiResolution resolution)
		{
			var components = Components.Select(component =>
			{
				var format = UnitStreamAbiProfiles.GetFormat(resolution, component.Format);
				return new UnitStreamComponentInfo(
					component.Type,
					GetComponentTypeName(component.Type),
					component.Format,
					format.Name,
					component.Index,
					component.Unknown,
					format.Size);
			}).ToArray();
			return new UnitStreamInfo(
				Index,
				Offset,
				ComponentInfoId,
				NumComponents,
				VertexBufferId,
				NumVertices,
				VertexStride,
				IndexBufferId,
				NumIndices,
				IndexBufferType,
				VertexBufferOffset,
				VertexBufferSize,
				IndexBufferOffset,
				IndexBufferSize,
				components);
		}

		public UnitStreamAbiRawStream ToAbiStream()
			=> new(Index, VertexStride, Components);
	}

	private static UnitStreamReadResult ReadStreams(ReadOnlySpan<byte> data, uint streamInfoOffset, uint unitVersion)
	{
		EnsureRange(data, streamInfoOffset, 4, "stream info count");
		var count = checked((int)ReadUInt32(data, streamInfoOffset));
		var tableOffset = checked((int)streamInfoOffset + 4);
		EnsureRange(data, tableOffset, checked(count * 8 + 4), "stream info table");

		var offsets = new uint[count];
		for (var i = 0; i < count; i++)
		{
			offsets[i] = ReadUInt32(data, tableOffset + i * 4);
		}

		var rawStreams = new List<RawUnitStreamInfo>(count);
		for (var i = 0; i < count; i++)
		{
			var absoluteOffset = checked(streamInfoOffset + offsets[i]);
			rawStreams.Add(ReadStream(data, i, absoluteOffset));
		}
		var resolution = UnitStreamAbiProfiles.Resolve(unitVersion, rawStreams.Select(stream => stream.ToAbiStream()).ToArray());
		return new UnitStreamReadResult(rawStreams.Select(stream => stream.Resolve(resolution)).ToArray(), resolution);
	}

	private static RawUnitStreamInfo ReadStream(ReadOnlySpan<byte> data, int index, uint absoluteOffset)
	{
		EnsureRange(data, absoluteOffset, 8 + StreamInfoComponentBlockSize + 88 + 16, $"stream info {index}");
		var componentInfoId = ReadUInt64(data, absoluteOffset);
		var componentOffset = checked((int)absoluteOffset + 8);
		var cursor = checked(componentOffset + StreamInfoComponentBlockSize);

		var numComponents = ReadUInt64(data, cursor); cursor += 8;
		var vertexBufferId = ReadUInt64(data, cursor); cursor += 8;
		cursor += 8; // VertexBuffer_unk1
		var numVertices = ReadUInt32(data, cursor); cursor += 4;
		var vertexStride = ReadUInt32(data, cursor); cursor += 4;
		cursor += 16; // VertexBuffer_unk2/unk3
		var indexBufferId = ReadUInt64(data, cursor); cursor += 8;
		cursor += 8; // IndexBuffer_unk1
		var numIndices = ReadUInt32(data, cursor); cursor += 4;
		var indexBufferType = ReadUInt32(data, cursor); cursor += 4;
		cursor += 16; // IndexBuffer_unk2/unk3
		var vertexBufferOffset = ReadUInt32(data, cursor); cursor += 4;
		var vertexBufferSize = ReadUInt32(data, cursor); cursor += 4;
		var indexBufferOffset = ReadUInt32(data, cursor); cursor += 4;
		var indexBufferSize = ReadUInt32(data, cursor);

		var componentCount = checked((int)numComponents);
		EnsureRange(data, componentOffset, checked(componentCount * 20), $"stream info {index} components");
		var components = new List<UnitStreamAbiRawComponent>(componentCount);
		for (var i = 0; i < componentCount; i++)
		{
			var c = componentOffset + i * 20;
			var type = ReadUInt32(data, c);
			var format = ReadUInt32(data, c + 4);
			var componentIndex = ReadUInt32(data, c + 8);
			var unknown = ReadUInt64(data, c + 12);
			components.Add(new UnitStreamAbiRawComponent(type, format, componentIndex, unknown));
		}

		return new RawUnitStreamInfo(
			index,
			absoluteOffset,
			componentInfoId,
			numComponents,
			vertexBufferId,
			numVertices,
			vertexStride,
			indexBufferId,
			numIndices,
			indexBufferType,
			vertexBufferOffset,
			vertexBufferSize,
			indexBufferOffset,
			indexBufferSize,
			components);
	}

	private static IReadOnlyList<UnitMeshInfo> ReadMeshes(ReadOnlySpan<byte> data, uint meshInfoOffset, bool readInlineMaterialSections, UnitCustomizationInfo customizationInfo, UnitBoneNames boneNames)
	{
		EnsureRange(data, meshInfoOffset, 4, "mesh info count");
		var count = checked((int)ReadUInt32(data, meshInfoOffset));
		var tableOffset = checked((int)meshInfoOffset + 4);
		EnsureRange(data, tableOffset, checked(count * 8), "mesh info table");

		var offsets = new uint[count];
		var meshIds = new uint[count];
		for (var i = 0; i < count; i++)
		{
			offsets[i] = ReadUInt32(data, tableOffset + i * 4);
			meshIds[i] = ReadUInt32(data, tableOffset + count * 4 + i * 4);
		}

		var result = new List<UnitMeshInfo>(count);
		for (var i = 0; i < count; i++)
		{
			var absoluteOffset = checked(meshInfoOffset + offsets[i]);
			result.Add(ReadMesh(data, i, absoluteOffset, meshIds[i], readInlineMaterialSections, customizationInfo, boneNames));
		}

		return result;
	}

	private static UnitMeshInfo ReadMesh(ReadOnlySpan<byte> data, int index, uint absoluteOffset, uint fallbackMeshId, bool readInlineMaterialSections, UnitCustomizationInfo customizationInfo, UnitBoneNames boneNames)
	{
		EnsureRange(data, absoluteOffset, 112, $"mesh info {index}");
		var cullingValues = new float[7];
		for (var valueIndex = 0; valueIndex < cullingValues.Length; valueIndex++) cullingValues[valueIndex] = ReadSingle(data, checked((int)absoluteOffset + 8 + valueIndex * sizeof(float)));
		var cullingBounds = new UnitMeshCullingBounds(cullingValues);
		var cursor = checked((int)absoluteOffset + 40);
		var meshId = ReadUInt32(data, cursor); cursor += 4;
		if (meshId == 0 && fallbackMeshId != 0)
		{
			meshId = fallbackMeshId;
		}
		cursor += 4; // unk3
		var transformIndex = ReadUInt32(data, cursor); cursor += 4;
		cursor += 4; // unk4
		var lodIndex = ReadInt32(data, cursor); cursor += 4;
		var streamIndex = ReadUInt32(data, cursor); cursor += 4;
		cursor += 40; // unk6
		var numMaterials = ReadUInt32(data, cursor); cursor += 4;
		var materialOffset = ReadUInt32(data, cursor); cursor += 4;
		cursor += 8; // unk8
		var numSections = ReadUInt32(data, cursor); cursor += 4;
		var sectionsOffset = ReadUInt32(data, cursor);
		var semanticInfo = BuildSemanticInfo(customizationInfo, boneNames, meshId, lodIndex, index);
		if (!readInlineMaterialSections)
		{
			return new UnitMeshInfo(
				index,
				absoluteOffset,
				meshId,
				lodIndex,
				transformIndex,
				streamIndex,
				0,
				0,
				0,
				0,
				semanticInfo,
				Array.Empty<uint>(),
				Array.Empty<UnitMeshSectionInfo>()) { CullingBounds = cullingBounds };
		}

		var materialCount = checked((int)numMaterials);
		var sectionCount = checked((int)numSections);
		EnsureRange(data, cursor + 4, checked(materialCount * 4 + sectionCount * 24), $"mesh info {index} material/section data");

		cursor += 4;
		var materialSlotIds = new List<uint>(materialCount);
		for (var i = 0; i < materialCount; i++)
		{
			materialSlotIds.Add(ReadUInt32(data, cursor));
			cursor += 4;
		}

		var sections = new List<UnitMeshSectionInfo>(sectionCount);
		for (var i = 0; i < sectionCount; i++)
		{
			var sectionOffset = checked((uint)cursor);
			var materialIndex = ReadUInt32(data, cursor); cursor += 4;
			var materialSlotId = materialIndex < materialSlotIds.Count ? materialSlotIds[(int)materialIndex] : 0;
			var vertexOffset = ReadUInt32(data, cursor); cursor += 4;
			var numVertices = ReadUInt32(data, cursor); cursor += 4;
			var indexOffset = ReadUInt32(data, cursor); cursor += 4;
			var numIndices = ReadUInt32(data, cursor); cursor += 4;
			var groupIndex = ReadUInt32(data, cursor); cursor += 4;
			sections.Add(new UnitMeshSectionInfo(sectionOffset, materialIndex, materialSlotId, vertexOffset, numVertices, indexOffset, numIndices, groupIndex));
		}

		return new UnitMeshInfo(
			index,
			absoluteOffset,
			meshId,
			lodIndex,
			transformIndex,
			streamIndex,
			numMaterials,
			materialOffset,
			numSections,
			sectionsOffset,
			semanticInfo,
			materialSlotIds,
			sections) { CullingBounds = cullingBounds };
	}

	private static IReadOnlyList<UnitMaterialBinding> ReadMaterials(ReadOnlySpan<byte> data, uint materialsOffset)
	{
		EnsureRange(data, materialsOffset, 4, "unit material count");
		var count = checked((int)ReadUInt32(data, materialsOffset));
		var cursor = checked((int)materialsOffset + 4);
		EnsureRange(data, cursor, checked(count * 12), "unit material bindings");

		var sectionIds = new uint[count];
		for (var i = 0; i < count; i++)
		{
			sectionIds[i] = ReadUInt32(data, cursor);
			cursor += 4;
		}

		var result = new List<UnitMaterialBinding>(count);
		for (var i = 0; i < count; i++)
		{
			result.Add(new UnitMaterialBinding(sectionIds[i], ReadUInt64(data, cursor)));
			cursor += 8;
		}

		return result;
	}

	private static UnitCustomizationInfo ReadCustomizationInfo(ReadOnlySpan<byte> data, uint customizationInfoOffset)
	{
		try
		{
			var cursor = checked((int)customizationInfoOffset + 24);
			var bodyType = ReadLengthPrefixedString(data, ref cursor);
			cursor = checked(cursor + 12);
			var slot = ReadLengthPrefixedString(data, ref cursor);
			cursor = checked(cursor + 12);
			var weight = ReadLengthPrefixedString(data, ref cursor);
			cursor = checked(cursor + 12);
			var pieceType = ReadLengthPrefixedString(data, ref cursor);
			return new UnitCustomizationInfo(bodyType, slot, weight, pieceType);
		}
		catch (Exception ex) when (ex is ArgumentException or InvalidDataException or OverflowException or DecoderFallbackException)
		{
			return UnitCustomizationInfo.Empty;
		}
	}

	private static string ReadLengthPrefixedString(ReadOnlySpan<byte> data, ref int cursor)
	{
		EnsureRange(data, cursor, 4, "customization string length");
		var length = checked((int)ReadUInt32(data, cursor));
		cursor += 4;
		EnsureRange(data, cursor, length, "customization string");
		var value = System.Text.Encoding.UTF8.GetString(data.Slice(cursor, length)).Replace("\0", string.Empty, StringComparison.Ordinal);
		cursor += length;
		return value;
	}

	private static UnitMeshSemanticInfo BuildSemanticInfo(UnitCustomizationInfo customizationInfo, UnitBoneNames boneNames, uint meshId, int lodIndex, int meshInfoIndex)
	{
		var boneName = FindBoneNameForMeshId(boneNames, meshId);
		if (!customizationInfo.HasValue)
		{
			var fallbackName = lodIndex == -1 ? $"{meshId}_mesh{meshInfoIndex}" : $"{meshId}_lod{lodIndex}";
			var fallbackSemantic = InferSemanticFromName(boneName) ?? UnitMeshSemanticInfo.Empty(lodIndex, meshInfoIndex) with
			{
				Name = boneName ?? fallbackName
			};
			return fallbackSemantic with { LodIndex = lodIndex, MeshInfoIndex = meshInfoIndex, IsLod = lodIndex is not 0 and not -1 };
		}

		var slot = StripPrefix(customizationInfo.Slot, "HelldiverCustomizationSlot_");
		var pieceType = StripPrefix(customizationInfo.PieceType, "HelldiverCustomizationPieceType_");
		var bodyType = StripPrefix(customizationInfo.BodyType, "HelldiverCustomizationBodyType_");
		var weight = StripPrefix(customizationInfo.Weight, "HelldiverCustomizationWeight_");
		var suffix = lodIndex == -1 ? $"_mesh{meshInfoIndex}" : $"_lod{lodIndex}";
		var name = $"{slot}_{pieceType}_{bodyType}{suffix}";
		if (boneName is not null)
		{
			name = boneName;
			var inferred = InferSemanticFromName(boneName);
			if (inferred is not null)
			{
				slot = inferred.Slot;
				pieceType = inferred.PieceType;
				bodyType = inferred.BodyType;
			}
		}
		return new UnitMeshSemanticInfo(name, slot, pieceType, bodyType, weight, lodIndex, meshInfoIndex, false, false, lodIndex is not 0 and not -1);
	}

	private static string? FindBoneNameForMeshId(UnitBoneNames boneNames, uint meshId)
	{
		if (!boneNames.HasValue)
		{
			return null;
		}

		for (var index = 0; index < boneNames.Hashes.Count && index < boneNames.Names.Count; index++)
		{
			if (boneNames.Hashes[index] == meshId)
			{
				return boneNames.Names[index];
			}
		}
		return null;
	}

	private static UnitMeshSemanticInfo? InferSemanticFromName(string? name)
	{
		if (string.IsNullOrWhiteSpace(name))
		{
			return null;
		}

		var baseName = name;
		var dotIndex = baseName.IndexOf('.', StringComparison.Ordinal);
		if (dotIndex >= 0)
		{
			baseName = baseName[..dotIndex];
		}

		var parts = baseName.Split('_', StringSplitOptions.RemoveEmptyEntries);
		if (parts.Length < 3)
		{
			return null;
		}

		return new UnitMeshSemanticInfo(name, parts[0], parts[1], parts[2], string.Empty, 0, 0, false, false, false);
	}

	private static IReadOnlyList<UnitMeshInfo> ApplySdkMeshClassification(IReadOnlyList<UnitMeshInfo> meshes, IReadOnlyList<UnitRawMeshData> rawMeshes, IReadOnlyList<UnitMaterialBinding> materials)
	{
		if (meshes.Count == 0 || rawMeshes.Count == 0)
		{
			return meshes;
		}

		var rawByMeshInfoIndex = new Dictionary<int, UnitRawMeshData>();
		foreach (var rawMesh in rawMeshes)
		{
			rawByMeshInfoIndex.TryAdd(rawMesh.MeshInfoIndex, rawMesh);
		}
		var materialSectionIds = materials.Select(material => material.SectionId).ToHashSet();
		var result = new List<UnitMeshInfo>(meshes.Count);
		foreach (var mesh in meshes)
		{
			if (!rawByMeshInfoIndex.TryGetValue(mesh.Index, out var rawMesh))
			{
				result.Add(mesh);
				continue;
			}

			var isCullingBody = IsCullingBody(mesh, materialSectionIds);
			var isStaticMesh = IsStaticMesh(rawMesh);
			var isLod = mesh.LodIndex is not 0 and not -1 && !isCullingBody;
			result.Add(mesh with
			{
				SemanticInfo = mesh.SemanticInfo with
				{
					IsCullingBody = isCullingBody,
					IsStaticMesh = isStaticMesh,
					IsLod = isLod
				}
			});
		}

		return result;
	}

	private static bool IsCullingBody(UnitMeshInfo mesh, IReadOnlySet<uint> materialSectionIds)
		=> materialSectionIds.Count > 0
			&& mesh.Sections.Count > 0
			&& mesh.Sections.All(section => !materialSectionIds.Contains(section.MaterialSlotId));

	private static bool IsStaticMesh(UnitRawMeshData rawMesh)
	{
		// The SDK checks decoded VertexWeights. At this layer, an absent Type=7
		// component can also mean that a version-specific stream format was not
		// decoded, so classify that case conservatively as unknown rather than
		// discarding otherwise valid geometry. Only explicit all-zero weights are
		// treated as static meshes.
		var hasWeights = false;
		foreach (var vertex in rawMesh.Vertices)
		{
			foreach (var component in vertex.Components)
			{
				if (component.Type != 7) continue;
				hasWeights = true;
				if (component.FloatValues.Any(value => Math.Abs(value) > 0.00001f)
					|| component.UIntValues.Any(value => value != 0))
					return false;
			}
		}
		return hasWeights;
	}

	private static string StripPrefix(string value, string prefix)
		=> value.StartsWith(prefix, StringComparison.Ordinal) ? value[prefix.Length..] : value;

	private static IReadOnlyList<UnitRawMeshSummary> BuildRawMeshSummaries(IReadOnlyList<UnitMeshInfo> meshes, IReadOnlyList<UnitStreamInfo> streams, int gpuLength)
	{
		var result = new List<UnitRawMeshSummary>(meshes.Count);
		foreach (var mesh in meshes)
		{
			var vertexCount = mesh.Sections.Count == 0 ? 0u : mesh.Sections.Max(s => s.VertexOffset + s.NumVertices) - mesh.Sections.Min(s => s.VertexOffset);
			var indexCount = mesh.Sections.Aggregate(0u, (sum, section) => checked(sum + section.NumIndices));
			var hasGpuVertexRange = false;
			var hasGpuIndexRange = false;

			if (mesh.StreamIndex < streams.Count)
			{
				var stream = streams[(int)mesh.StreamIndex];
				hasGpuVertexRange = IsRangeValid(gpuLength, stream.VertexBufferOffset, stream.VertexBufferSize);
				hasGpuIndexRange = IsRangeValid(gpuLength, stream.IndexBufferOffset, stream.IndexBufferSize);
			}

			result.Add(new UnitRawMeshSummary(
				mesh.Index,
				mesh.MeshId,
				mesh.LodIndex,
				mesh.StreamIndex,
				vertexCount,
				indexCount,
				mesh.NumMaterials,
				mesh.NumSections,
				hasGpuVertexRange,
				hasGpuIndexRange));
		}

		return result;
	}

	private static IReadOnlyList<UnitRawMeshData> ReadRawMeshData(IReadOnlyList<UnitMeshInfo> meshes, IReadOnlyList<UnitStreamInfo> streams, ReadOnlySpan<byte> gpuData, UnitMeshReadOptions options)
	{
		if (gpuData.IsEmpty)
		{
			return Array.Empty<UnitRawMeshData>();
		}

		var result = new List<UnitRawMeshData>(meshes.Count);
		foreach (var mesh in meshes)
		{
			if (mesh.StreamIndex >= streams.Count)
			{
				continue;
			}

			var stream = streams[(int)mesh.StreamIndex];
			if (!IsRangeValid(gpuData.Length, stream.VertexBufferOffset, stream.VertexBufferSize)
				|| !IsRangeValid(gpuData.Length, stream.IndexBufferOffset, stream.IndexBufferSize))
			{
				continue;
			}

			var sections = ReadRawMeshSections(mesh, stream, gpuData);
			var triangles = options.BuildTriangleMirror
				? sections.SelectMany(section => section.Triangles).ToArray()
				: Array.Empty<UnitTriangleIndices>();
			var vertices = ReadVertexRecords(mesh, stream, gpuData, options);
			result.Add(new UnitRawMeshData(mesh.Index, mesh.MeshId, mesh.LodIndex, mesh.StreamIndex, sections, triangles, vertices));
		}

		return result;
	}

	private static IReadOnlyList<UnitRawMeshSectionData> ReadRawMeshSections(UnitMeshInfo mesh, UnitStreamInfo stream, ReadOnlySpan<byte> gpuData)
	{
		var indexStride = stream.IndexBufferType == 1 ? 4 : 2;
		var baseVertexOffset = mesh.Sections.Count == 0 ? 0u : mesh.Sections.Min(section => section.VertexOffset);
		var sections = new List<UnitRawMeshSectionData>(mesh.Sections.Count);
		foreach (var section in mesh.Sections)
		{
			var triangles = new List<UnitTriangleIndices>();
			var sectionIndexOffset = checked(stream.IndexBufferOffset + section.IndexOffset * (uint)indexStride);
			var sectionIndexBytes = checked((int)(section.NumIndices * (uint)indexStride));
			EnsureGpuRange(gpuData, sectionIndexOffset, sectionIndexBytes, $"mesh {mesh.Index} index section");

			var cursor = checked((int)sectionIndexOffset);
			for (var i = 0u; i + 2 < section.NumIndices; i += 3)
			{
				var a = ReadIndex(gpuData, cursor, indexStride); cursor += indexStride;
				var b = ReadIndex(gpuData, cursor, indexStride); cursor += indexStride;
				var c = ReadIndex(gpuData, cursor, indexStride); cursor += indexStride;
				triangles.Add(new UnitTriangleIndices(
					NormalizeVertexIndex(a, baseVertexOffset),
					NormalizeVertexIndex(b, baseVertexOffset),
					NormalizeVertexIndex(c, baseVertexOffset)));
			}

			sections.Add(new UnitRawMeshSectionData(section.MaterialIndex, section.MaterialSlotId, triangles));
		}

		return sections;
	}

	private static uint NormalizeVertexIndex(uint index, uint baseVertexOffset)
		=> index >= baseVertexOffset ? index - baseVertexOffset : index;

	private static IReadOnlyList<UnitRawVertexRecord> ReadVertexRecords(UnitMeshInfo mesh, UnitStreamInfo stream, ReadOnlySpan<byte> gpuData, UnitMeshReadOptions options)
	{
		if (mesh.Sections.Count == 0 || stream.VertexStride == 0)
		{
			return Array.Empty<UnitRawVertexRecord>();
		}

		var baseVertexOffset = mesh.Sections.Min(section => section.VertexOffset);
		var vertexCount = mesh.Sections.Max(section => section.VertexOffset + section.NumVertices) - baseVertexOffset;
		var vertexOffset = checked(stream.VertexBufferOffset + baseVertexOffset * stream.VertexStride);
		var vertexBytes = checked((int)(vertexCount * stream.VertexStride));
		EnsureGpuRange(gpuData, vertexOffset, vertexBytes, $"mesh {mesh.Index} vertex section");

		var vertices = new List<UnitRawVertexRecord>(checked((int)vertexCount));
		var cursor = checked((int)vertexOffset);
		var stride = checked((int)stream.VertexStride);
		for (var i = 0u; i < vertexCount; i++)
		{
			var vertexData = gpuData.Slice(cursor, stride);
			var components = DecodeVertexComponents(vertexData, stream.Components, options.PreserveComponentRawData);
			vertices.Add(new UnitRawVertexRecord(i, options.PreserveVertexData ? vertexData.ToArray() : Array.Empty<byte>(), components));
			cursor += stride;
		}

		return vertices;
	}

	private static IReadOnlyList<UnitVertexComponentValue> DecodeVertexComponents(ReadOnlySpan<byte> vertexData, IReadOnlyList<UnitStreamComponentInfo> components, bool preserveRawData)
	{
		var result = new List<UnitVertexComponentValue>(components.Count);
		var cursor = 0;
		foreach (var component in components)
		{
			var size = checked((int)component.Size);
			if (size <= 0 || cursor + size > vertexData.Length)
			{
				result.Add(new UnitVertexComponentValue(
					component.Type,
					component.TypeName,
					component.Format,
					component.FormatName,
					component.Index,
					Array.Empty<float>(),
					Array.Empty<uint>(),
					Array.Empty<byte>()));
				continue;
			}

			var raw = vertexData.Slice(cursor, size);
			result.Add(DecodeVertexComponent(component, raw, preserveRawData));
			cursor += size;
		}

		return result;
	}

	private static UnitVertexComponentValue DecodeVertexComponent(UnitStreamComponentInfo component, ReadOnlySpan<byte> raw, bool preserveRawData)
	{
		var floats = Array.Empty<float>();
		var uints = Array.Empty<uint>();
		switch (component.FormatName)
		{
			case "float":
				floats = [ReadSingle(raw, 0)];
				break;
			case "vec2_float":
				floats = [ReadSingle(raw, 0), ReadSingle(raw, 4)];
				break;
			case "vec3_float":
				floats = [ReadSingle(raw, 0), ReadSingle(raw, 4), ReadSingle(raw, 8)];
				break;
			case "vec4_float":
				floats = [ReadSingle(raw, 0), ReadSingle(raw, 4), ReadSingle(raw, 8), ReadSingle(raw, 12)];
				break;
			case "rgba_r8g8b8a8":
				uints = [raw[0], raw[1], raw[2], raw[3]];
				floats = [raw[0] / 255f, raw[1] / 255f, raw[2] / 255f, raw[3] / 255f];
				break;
			case "vec4_uint32":
				uints = [ReadUInt32(raw, 0), ReadUInt32(raw, 4), ReadUInt32(raw, 8), ReadUInt32(raw, 12)];
				break;
			case "vec4_uint8":
				uints = [raw[0], raw[1], raw[2], raw[3]];
				break;
			case "vec4_1010102":
				uints = [ReadUInt32(raw, 0)];
				floats = DecodeTenBitUnsigned(ReadUInt32(raw, 0));
				break;
			case "unk_normal":
				uints = [ReadUInt32(raw, 0)];
				floats = DecodePackedOctNormal(ReadUInt32(raw, 0));
				break;
			case "vec2_half":
				floats = [ReadHalf(raw, 0), ReadHalf(raw, 2)];
				break;
			case "vec4_half":
				floats = [ReadHalf(raw, 0), ReadHalf(raw, 2), ReadHalf(raw, 4), ReadHalf(raw, 6)];
				break;
		}

		return new UnitVertexComponentValue(
			component.Type,
			component.TypeName,
			component.Format,
			component.FormatName,
			component.Index,
			floats,
			uints,
			preserveRawData ? raw.ToArray() : Array.Empty<byte>());
	}

	private static uint ReadIndex(ReadOnlySpan<byte> data, int offset, int stride)
	{
		return stride == 4
			? ReadUInt32(data, offset)
			: ReadUInt16(data, offset);
	}

	private static bool IsRangeValid(int totalLength, uint offset, uint size)
	{
		return size == 0 || ((ulong)offset + size <= (ulong)totalLength);
	}

	private static void EnsureGpuRange(ReadOnlySpan<byte> data, uint offset, int size, string name)
	{
		if (offset > int.MaxValue || size < 0 || offset > data.Length || size > data.Length - (int)offset)
		{
			throw new InvalidDataException($"Unit GPU data does not contain a valid {name} range.");
		}
	}

	private static string GetComponentTypeName(uint type) => type switch
	{
		0 => "position",
		1 => "normal",
		2 => "tangent",
		3 => "bitangent",
		4 => "uv",
		5 => "color",
		6 => "bone_index",
		7 => "bone_weight",
		_ => "unknown"
	};

	private static float[] DecodeTenBitUnsigned(uint value)
	{
		return [
			(value & 0x3ff) / 1023f,
			((value >> 10) & 0x3ff) / 1023f,
			((value >> 20) & 0x3ff) / 1023f,
			((value >> 30) & 0x3) / 3f
		];
	}

	private static float[] DecodePackedOctNormal(uint value)
	{
		var x = (value & 0x3ff) * (2f / 1023f) - 1f;
		var y = ((value >> 10) & 0x3ff) * (2f / 1023f) - 1f;
		var z = 1f - Math.Abs(x) - Math.Abs(y);
		if (z < 0)
		{
			var oldX = x;
			x = (1f - Math.Abs(y)) * Math.Sign(oldX == 0 ? 1f : oldX);
			y = (1f - Math.Abs(oldX)) * Math.Sign(y == 0 ? 1f : y);
		}

		var length = MathF.Sqrt(x * x + y * y + z * z);
		return length <= 0 ? [0f, 0f, 0f] : [x / length, y / length, z / length];
	}

	private static void EnsureRange(ReadOnlySpan<byte> data, uint offset, int size, string name)
	{
		EnsureRange(data, checked((int)offset), size, name);
	}

	private static void EnsureRange(ReadOnlySpan<byte> data, int offset, int size, string name)
	{
		if (offset < 0 || size < 0 || offset > data.Length || size > data.Length - offset)
		{
			throw new InvalidDataException($"Unit TocData does not contain a valid {name} range.");
		}
	}

	private static void EnsureRangeWithin(ReadOnlySpan<byte> data, uint offset, int size, uint endOffset, string name)
	{
		EnsureRange(data, offset, size, name);
		if ((ulong)offset + (ulong)size > endOffset)
		{
			throw new InvalidDataException($"Unit TocData does not contain a valid {name} range before the next section.");
		}
	}

	private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset)
	{
		EnsureRange(data, offset, 4, "uint32");
		return System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));
	}

	private static uint ReadUInt32(ReadOnlySpan<byte> data, uint offset) => ReadUInt32(data, checked((int)offset));

	private static ushort ReadUInt16(ReadOnlySpan<byte> data, int offset)
	{
		EnsureRange(data, offset, 2, "uint16");
		return (ushort)(data[offset] | (data[offset + 1] << 8));
	}

	private static int ReadInt32(ReadOnlySpan<byte> data, int offset) => unchecked((int)ReadUInt32(data, offset));

	private static float ReadSingle(ReadOnlySpan<byte> data, int offset) => BitConverter.Int32BitsToSingle(ReadInt32(data, offset));

	private static float ReadHalf(ReadOnlySpan<byte> data, int offset) => (float)BitConverter.UInt16BitsToHalf(ReadUInt16(data, offset));

	private static ulong ReadUInt64(ReadOnlySpan<byte> data, int offset)
	{
		EnsureRange(data, offset, 8, "uint64");
		return System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset, 8));
	}

	private static ulong ReadUInt64(ReadOnlySpan<byte> data, uint offset) => ReadUInt64(data, checked((int)offset));
}
