using System.Buffers.Binary;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;

namespace HD2ModAdaptation.PatchReconstruction.UnitMesh.Canonical;

// Purpose: Rebuilds one canonical target Unit and its GPU sidecar from every prepared target RawMesh.
// SDK reference: docs/sdk流程架构.md requires all RawMeshes to converge before Unit/GPU output;
// tools/ref/HD2SDK-CommunityEdition/stingray/unit.py StingrayMeshFile.Serialize() first derives MeshInfo Sections/Materials from all
// RawMeshes, then SerializeGpuData() emits SerializeVertexBuffer() followed by SerializeIndexBuffer();
// both paths rewrite stream/section offsets from the one output layout. MeshInfo.Serialize() writes a
// 128-byte header followed by MaterialIDs and Sections, with MaterialOffset/SectionsOffset relative to
// the MeshInfo record. This implementation keeps the target TocData only as an explicit byte-preservation
// baseline for unknown regions and never calls the
// legacy UnitMeshWriter, PatchArchiveWriter, or Manager reconstruction paths.
public sealed record CanonicalUnitRebuildResult(
	UnitMeshModel? Model,
	UnitMeshWriteResult? Output,
	IReadOnlyList<CanonicalPlanDiagnostic> Diagnostics,
	CanonicalUnitSerializationTelemetry? SerializationTelemetry = null)
{
	public bool IsValid => Model is not null && Output is not null && Diagnostics.Count == 0;
}

public sealed record CanonicalBoneInfoRebuild(int LodIndex, UnitBoneInfo BoneInfo);

public sealed class CanonicalUnitRebuilder
{
	public const uint MaxD3D12SingleBufferBytes = 128u * 1024u * 1024u;
	public const string StreamBufferLimitDiagnosticCode = "StreamBufferExceedsD3D12BlockLimit";

	public static bool TryParseStreamBufferLimitDiagnostic(
		CanonicalPlanDiagnostic diagnostic,
		out int streamIndex,
		out string bufferKind,
		out ulong actualBytes,
		out ulong limitBytes,
		out uint stride)
	{
		streamIndex = 0;
		bufferKind = "VertexBuffer";
		actualBytes = 0;
		limitBytes = MaxD3D12SingleBufferBytes;
		stride = 0;
		if (diagnostic is null || !string.Equals(diagnostic.Code, StreamBufferLimitDiagnosticCode, StringComparison.Ordinal))
			return false;

		foreach (var part in diagnostic.Message.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
		{
			var eq = part.IndexOf('=', StringComparison.Ordinal);
			if (eq <= 0) continue;
			var key = part[..eq].Trim();
			var val = part[(eq + 1)..].Trim();
			switch (key)
			{
				case "Stream":
					_ = int.TryParse(val, System.Globalization.CultureInfo.InvariantCulture, out streamIndex);
					break;
				case "Buffer":
					bufferKind = val;
					break;
				case "Bytes":
					_ = ulong.TryParse(val, System.Globalization.CultureInfo.InvariantCulture, out actualBytes);
					break;
				case "Limit":
					_ = ulong.TryParse(val, System.Globalization.CultureInfo.InvariantCulture, out limitBytes);
					break;
				case "Stride":
					_ = uint.TryParse(val, System.Globalization.CultureInfo.InvariantCulture, out stride);
					break;
			}
		}

		return actualBytes > 0;
	}

	private const int MeshInfoHeaderSize = 128;
	private const int MeshSectionSize = 24;
	private const int StreamRecordSize = 432;
	private const int UnitMeshInfoHeaderSize = 0x78;
	private static void Trace(string message) => System.Diagnostics.Trace.WriteLine($"[CanonicalUnitRebuilder] {message}");

	public CanonicalUnitRebuildResult TryRebuild(
		UnitMeshModel target,
		ReadOnlySpan<byte> targetOriginalTocData,
		IReadOnlyList<UnitRawMeshData> finalRawMeshes,
		IReadOnlyList<CanonicalBoneInfoRebuild>? rebuiltBoneInfos = null,
		IReadOnlyList<UnitMaterialBinding>? finalMaterialBindings = null)
	{
		ArgumentNullException.ThrowIfNull(target);
		ArgumentNullException.ThrowIfNull(finalRawMeshes);
		var diagnostics = new List<CanonicalPlanDiagnostic>();
		var effectiveMaterialBindings = finalMaterialBindings ?? target.Materials;
		ValidateLayout(target, targetOriginalTocData, finalRawMeshes, effectiveMaterialBindings, diagnostics);
		ValidateBoneInfoRebuilds(target, targetOriginalTocData, rebuiltBoneInfos, diagnostics);
		if (diagnostics.Count != 0)
			return new(null, null, Array.AsReadOnly(diagnostics.ToArray()));

		try
		{
			var phaseStopwatch = System.Diagnostics.Stopwatch.StartNew();
			var tocData = targetOriginalTocData.ToArray();
			var transformed = RelocateTransformInfo(target, tocData, diagnostics);
			if (diagnostics.Count != 0)
				return new(null, null, Array.AsReadOnly(diagnostics.ToArray()));
			tocData = transformed.TocData;
			target = transformed.Model;
			if (rebuiltBoneInfos is { Count: > 0 })
			{
				var relocated = RebuildBoneInfoBlock(target, tocData, rebuiltBoneInfos, diagnostics);
				if (diagnostics.Count != 0)
					return new(null, null, Array.AsReadOnly(diagnostics.ToArray()));
				tocData = relocated.TocData;
				target = relocated.Model;
			}
			// Every encoded vertex below follows this target declaration. Keep the full StreamInfo
			// block synchronized with the in-memory model rather than relying on preserved bytes.
			// A future SDK-profile compiler may change this model, but must still pass through this
			// single serializer so component order, stride and GPU bytes cannot diverge.
			WriteStreamLayouts(tocData, target.Streams);
			var rawByMesh = finalRawMeshes.ToDictionary(raw => raw.MeshInfoIndex);
			var rebuiltMeshes = new Dictionary<int, UnitMeshInfo>(target.Meshes.Count);
			var gpuData = new List<byte>();
			var rebuiltStreams = new List<UnitStreamInfo>(target.Streams.Count);
			var setupElapsed = phaseStopwatch.Elapsed;
			phaseStopwatch.Restart();

			foreach (var stream in target.Streams.OrderBy(stream => stream.Index))
			{
				var streamMeshes = target.Meshes
					.Where(mesh => mesh.StreamIndex == (uint)stream.Index)
					.OrderBy(mesh => mesh.Sections.FirstOrDefault()?.VertexOffset ?? 0)
					.ThenBy(mesh => mesh.Index)
					.ToArray();
				var indexOrderedMeshes = streamMeshes
					.OrderBy(mesh => mesh.Sections.FirstOrDefault()?.IndexOffset ?? 0)
					.ThenBy(mesh => mesh.Index)
					.ToArray();
				Trace($"stream={stream.Index} stride={stream.VertexStride} indexType={stream.IndexBufferType} meshes={streamMeshes.Length} gpuStart={gpuData.Count}");
				var plannedVertices = streamMeshes.Aggregate(0UL, (sum, mesh) => checked(sum + (ulong)rawByMesh[mesh.Index].Vertices.Count));
				var rawVertexBytes = checked(plannedVertices * stream.VertexStride);
				var plannedVertexEnd = checked(((ulong)gpuData.Count + rawVertexBytes + 15UL) & ~15UL);
				var plannedVertexBytes = checked(plannedVertexEnd - (ulong)gpuData.Count);
				if (plannedVertexBytes >= MaxD3D12SingleBufferBytes)
				{
					diagnostics.Add(new(
						StreamBufferLimitDiagnosticCode,
						$"Stream={stream.Index}; Buffer=VertexBuffer; Bytes={plannedVertexBytes}; Limit={MaxD3D12SingleBufferBytes}; Vertices={plannedVertices}; Stride={stream.VertexStride}; Stream {stream.Index} vertex buffer ({plannedVertexBytes} bytes) reaches or exceeds the Helldivers 2 D3D12 single-buffer block limit ({MaxD3D12SingleBufferBytes} bytes / 128 MiB)."));
					continue;
				}
				var plannedIndices = streamMeshes.Aggregate(0UL, (sum, mesh) => checked(sum + (ulong)rawByMesh[mesh.Index].Sections.Sum(section => (long)section.Triangles.Count * 3L)));
				var indexStride = stream.IndexBufferType == 0 ? 2u : 4u;
				var plannedIndexBytes = checked(plannedIndices * indexStride);
				if (plannedIndexBytes >= MaxD3D12SingleBufferBytes)
				{
					diagnostics.Add(new(
						StreamBufferLimitDiagnosticCode,
						$"Stream={stream.Index}; Buffer=IndexBuffer; Bytes={plannedIndexBytes}; Limit={MaxD3D12SingleBufferBytes}; Indices={plannedIndices}; Stride={indexStride}; Stream {stream.Index} index buffer ({plannedIndexBytes} bytes) reaches or exceeds the Helldivers 2 D3D12 single-buffer block limit ({MaxD3D12SingleBufferBytes} bytes / 128 MiB)."));
					continue;
				}
				var vertexOffsets = new Dictionary<int, uint>();
				var vertexStart = checked((uint)gpuData.Count);
				var vertexCount = 0u;
				foreach (var mesh in streamMeshes)
				{
					var raw = rawByMesh[mesh.Index];
					Trace($"vertices mesh={mesh.Index} vertices={raw.Vertices.Count} sections={raw.Sections.Count} slots={string.Join(',', raw.Sections.Select(section => section.MaterialSlotId))}");
					vertexOffsets.Add(mesh.Index, vertexCount);
					CanonicalPositionDiagnostics.RecordMesh("before-gpu-write", raw, stream);
					foreach (var vertex in raw.Vertices)
					{
						if (vertex.Data.Length > stream.VertexStride)
							throw new InvalidDataException($"RawMesh {mesh.Index} vertex data exceeds target stream stride.");
						var gpuCountBefore = gpuData.Count;
						gpuData.AddRange(vertex.Data);
						for (var padding = vertex.Data.Length; padding < stream.VertexStride; padding++) gpuData.Add(0);
						if (vertex.Index == 0)
							CanonicalPositionDiagnostics.RecordGpuAppend("gpu-append", stream.Index, mesh.Index, checked((int)vertex.Index), gpuCountBefore, vertexStart, vertexOffsets[mesh.Index], stream.VertexStride, vertex.Data, gpuData);
						vertexCount++;
					}
				}
				PadToAlignment(gpuData, 16);
				var vertexSize = checked((uint)gpuData.Count - vertexStart);
				foreach (var mesh in streamMeshes)
					CanonicalPositionDiagnostics.RecordGpuVertex("gpu-written", stream.Index, mesh.Index, vertexOffsets[mesh.Index], vertexStart, stream.VertexStride, gpuData);
				var indexStart = checked((uint)gpuData.Count);
				var indexCount = 0u;
				var indexOffsets = new Dictionary<int, uint>();
				foreach (var mesh in indexOrderedMeshes)
				{
					var raw = rawByMesh[mesh.Index];
					Trace($"indices mesh={mesh.Index} sections={raw.Sections.Count} triangles={raw.Sections.Sum(section => section.Triangles.Count)} indexStart={indexCount}");
					indexOffsets.Add(mesh.Index, indexCount);
					foreach (var section in raw.Sections)
					{
						foreach (var triangle in section.Triangles)
						{
							ValidateTriangle(raw, triangle);
							WriteIndex(gpuData, triangle.A, stream.IndexBufferType);
							WriteIndex(gpuData, triangle.B, stream.IndexBufferType);
							WriteIndex(gpuData, triangle.C, stream.IndexBufferType);
							indexCount += 3;
						}
					}
				}
				var indexSize = checked((uint)gpuData.Count - indexStart);
				WriteStreamGpuFields(tocData, stream, vertexCount, vertexStart, vertexSize, indexCount, indexStart, indexSize);
				foreach (var mesh in streamMeshes)
				{
					var raw = rawByMesh[mesh.Index];
					var rebuilt = RebuildMeshInfo(mesh, raw, vertexOffsets[mesh.Index], indexOffsets[mesh.Index]);
					Trace($"meshInfo={mesh.Index} finalSections={rebuilt.Sections.Count} finalMaterials={rebuilt.MaterialSlotIds.Count} vertexOffset={vertexOffsets[mesh.Index]} indexOffset={indexOffsets[mesh.Index]}");
					rebuiltMeshes.Add(rebuilt.Index, rebuilt);
				}
				rebuiltStreams.Add(stream with
				{
					NumVertices = vertexCount,
					NumIndices = indexCount,
					VertexBufferOffset = vertexStart,
					VertexBufferSize = vertexSize,
					IndexBufferOffset = indexStart,
					IndexBufferSize = indexSize
				});
			}

			if (rebuiltMeshes.Count != target.Meshes.Count)
				diagnostics.Add(new("IncompleteMeshWriteback", "Every target MeshInfo must participate in stream GPU serialization and section TOC writeback."));
			if (diagnostics.Count != 0)
				return new(null, null, Array.AsReadOnly(diagnostics.ToArray()));
			var gpuWriteElapsed = phaseStopwatch.Elapsed;
			phaseStopwatch.Restart();

			var orderedMeshes = target.Meshes.Select(mesh => rebuiltMeshes[mesh.Index]).ToArray();
			var rebuiltToc = RebuildMeshInfoAndUnitTail(
				tocData,
				target,
				orderedMeshes,
				finalRawMeshes,
				effectiveMaterialBindings,
				out var relocatedMeshes,
				out var materialsOffset,
				out var endingOffset,
				diagnostics);
			if (diagnostics.Count != 0)
				return new(null, null, Array.AsReadOnly(diagnostics.ToArray()));
			var tocRebuildElapsed = phaseStopwatch.Elapsed;
			phaseStopwatch.Restart();

			var rebuiltSummaries = finalRawMeshes.Select(raw => new UnitRawMeshSummary(
				raw.MeshInfoIndex,
				raw.MeshId,
				raw.LodIndex,
				raw.StreamIndex,
				checked((uint)raw.Vertices.Count),
				checked((uint)raw.Sections.Sum(section => section.Triangles.Count * 3)),
				checked((uint)raw.Sections.Select(section => section.MaterialSlotId).Distinct().Count()),
				checked((uint)raw.Sections.Count),
				true,
				true)).ToArray();
			var rebuiltModel = target with
			{
				MeshInfoOffset = checked(BinaryPrimitives.ReadUInt32LittleEndian(rebuiltToc.AsSpan(0x64, 4))),
				MaterialsOffset = materialsOffset,
				EndingOffset = endingOffset,
				Meshes = relocatedMeshes,
				Streams = rebuiltStreams,
				Materials = BuildUsedMaterialBindings(effectiveMaterialBindings, finalRawMeshes),
				RawMeshes = rebuiltSummaries,
				RawMeshData = finalRawMeshes
			};
			return new(rebuiltModel, new UnitMeshWriteResult(rebuiltToc, gpuData.ToArray()), Array.Empty<CanonicalPlanDiagnostic>(),
				new CanonicalUnitSerializationTelemetry(setupElapsed, gpuWriteElapsed, tocRebuildElapsed, phaseStopwatch.Elapsed));
		}
		catch (Exception exception) when (exception is InvalidDataException or OverflowException or ArgumentException)
		{
			diagnostics.Add(new("SerializationFailed", exception.Message));
			return new(null, null, Array.AsReadOnly(diagnostics.ToArray()));
		}
	}

	private static (byte[] TocData, UnitMeshModel Model) RelocateTransformInfo(UnitMeshModel target, byte[] tocData, List<CanonicalPlanDiagnostic> diagnostics)
	{
		if (target.TransformInfoOffset == 0 || target.TransformInfo.NameHashes.Count == 0)
			return (tocData, target);
		if (target.TransformInfoOffset > tocData.Length - 16)
		{
			diagnostics.Add(new("TransformInfoRewriteUnavailable", "Canonical target TransformInfo header is outside TocData."));
			return (tocData, target);
		}
		var oldCount = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(tocData.AsSpan((int)target.TransformInfoOffset, 4)));
		var oldLength = checked(16 + oldCount * 136);
		var oldStart = checked((int)target.TransformInfoOffset);
		var oldEnd = checked(oldStart + oldLength);
		if (oldEnd > tocData.Length)
		{
			diagnostics.Add(new("TransformInfoRewriteUnavailable", "Canonical target TransformInfo block is outside TocData."));
			return (tocData, target);
		}
		if (oldCount == target.TransformInfo.NameHashes.Count)
			return (tocData, target);
		var replacement = SerializeTransformInfo(target.TransformInfo).ToList();
		PadToAlignment(replacement, 16);
		var alignedOldEnd = checked((oldEnd + 15) & ~15);
		if (alignedOldEnd > tocData.Length)
		{
			diagnostics.Add(new("TransformInfoRewriteUnavailable", "Canonical target TransformInfo alignment range is outside TocData."));
			return (tocData, target);
		}
		var delta = replacement.Count - (alignedOldEnd - oldStart);
		var output = new byte[checked(tocData.Length + delta)];
		tocData.AsSpan(0, oldStart).CopyTo(output);
		replacement.CopyTo(output, oldStart);
		tocData.AsSpan(alignedOldEnd).CopyTo(output.AsSpan(oldStart + replacement.Count));
		foreach (var offset in new[] { 0x4c, 0x50, 0x54, 0x58, 0x5c, 0x60, 0x64, 0x70 })
			ShiftTransformHeaderOffset(output, offset, alignedOldEnd, delta, diagnostics);
		return (output, ShiftOffsets(target, alignedOldEnd, delta));
	}

	private static byte[] SerializeTransformInfo(UnitTransformInfo info)
	{
		var count = info.NameHashes.Count;
		if (info.LocalTransforms.Count != count || info.Matrices.Count != count || info.Entries.Count != count)
			throw new InvalidDataException("Canonical TransformInfo arrays have inconsistent counts.");
		var data = new byte[checked(16 + count * 136)];
		WriteUInt32(data, 0, checked((uint)count));
		WriteUInt32(data, 4, info.Reserved0);
		WriteUInt32(data, 8, info.Reserved1);
		WriteUInt32(data, 12, info.Reserved2);
		var matricesOffset = 16 + count * 64;
		var entriesOffset = matricesOffset + count * 64;
		var hashesOffset = entriesOffset + count * 4;
		for (var index = 0; index < count; index++)
		{
			var local = info.LocalTransforms[index];
			if (local.Rotation.Count != 9 || local.Position.Count != 3 || local.Scale.Count != 3 || info.Matrices[index].Values.Count != 16)
				throw new InvalidDataException("Canonical TransformInfo has invalid transform dimensions.");
			var cursor = 16 + index * 64;
			foreach (var value in local.Rotation.Concat(local.Position).Concat(local.Scale).Append(local.Padding))
			{
				WriteSingle(data, cursor, value);
				cursor += 4;
			}
			cursor = matricesOffset + index * 64;
			foreach (var value in info.Matrices[index].Values)
			{
				WriteSingle(data, cursor, value);
				cursor += 4;
			}
			WriteUInt16(data, entriesOffset + index * 4, info.Entries[index].Increment);
			WriteUInt16(data, entriesOffset + index * 4 + 2, info.Entries[index].ParentIndex);
			WriteUInt32(data, hashesOffset + index * 4, info.NameHashes[index]);
		}
		return data;
	}

	private static UnitMeshModel ShiftOffsets(UnitMeshModel model, int threshold, int delta)
	{
		uint Shift(uint value) => value != 0 && value >= threshold ? checked((uint)((int)value + delta)) : value;
		return model with
		{
			UnreversedLodGroupListDataOffset = Shift(model.UnreversedLodGroupListDataOffset),
			CustomizationInfoOffset = Shift(model.CustomizationInfoOffset),
			BoneInfoOffset = Shift(model.BoneInfoOffset),
			StreamInfoOffset = Shift(model.StreamInfoOffset),
			EndingOffset = Shift(model.EndingOffset),
			MeshInfoOffset = Shift(model.MeshInfoOffset),
			MaterialsOffset = Shift(model.MaterialsOffset),
			Streams = model.Streams.Select(stream => stream with { Offset = Shift(stream.Offset) }).ToArray(),
			Meshes = model.Meshes.Select(mesh => mesh with
			{
				Offset = Shift(mesh.Offset),
				MaterialOffset = Shift(mesh.MaterialOffset),
				SectionsOffset = Shift(mesh.SectionsOffset),
				Sections = mesh.Sections.Select(section => section with { Offset = Shift(section.Offset) }).ToArray()
			}).ToArray()
		};
	}

	private static void ShiftTransformHeaderOffset(byte[] data, int offset, int threshold, int delta, List<CanonicalPlanDiagnostic> diagnostics)
	{
		var value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
		if (value != 0 && value >= threshold)
			WriteUInt32(data, offset, checked((uint)((int)value + delta)));
	}

	private static void ValidateLayout(UnitMeshModel target, ReadOnlySpan<byte> tocData, IReadOnlyList<UnitRawMeshData> rawMeshes, IReadOnlyList<UnitMaterialBinding> materialBindings, List<CanonicalPlanDiagnostic> diagnostics)
	{
		if (tocData.IsEmpty) diagnostics.Add(new("MissingTargetTocData", "Canonical rebuilding requires the target original TocData as the explicit unknown-region preservation baseline."));
		if (target.CompositeRef != 0 || target.StreamInfoOffset == 0)
			diagnostics.Add(new("UnsupportedCompositeLayout", "Composite-backed or unknown Unit layouts are unsupported; canonical rebuilding requires a writable StreamInfo layout."));
		if (target.Meshes.Count == 0) diagnostics.Add(new("MissingTargetMeshes", "The target Unit contains no MeshInfo records."));
		var duplicate = rawMeshes.GroupBy(raw => raw.MeshInfoIndex).Where(group => group.Count() > 1).Select(group => group.Key).ToArray();
		foreach (var index in duplicate) diagnostics.Add(new("DuplicateRawMesh", $"More than one final RawMesh targets MeshInfo {index}."));
		var targetIndices = target.Meshes.Select(mesh => mesh.Index).ToHashSet();
		foreach (var raw in rawMeshes.Where(raw => !targetIndices.Contains(raw.MeshInfoIndex)))
			diagnostics.Add(new("UnknownRawMeshTarget", $"Final RawMesh targets missing MeshInfo {raw.MeshInfoIndex}."));
		if (tocData.Length < UnitMeshInfoHeaderSize)
			diagnostics.Add(new("InvalidUnitHeaderRange", "Target TocData is too small to rewrite the Unit header safely."));
		foreach (var mesh in target.Meshes)
		{
			var matches = rawMeshes.Count(raw => raw.MeshInfoIndex == mesh.Index);
			if (matches != 1) diagnostics.Add(new("IncompleteRawMeshCoverage", $"Target MeshInfo {mesh.Index} requires exactly one prepared final RawMesh; found {matches}."));
		}
		foreach (var stream in target.Streams)
		{
			if (stream.VertexStride == 0) diagnostics.Add(new("InvalidVertexStride", $"Stream {stream.Index} has a zero vertex stride."));
			if (stream.IndexBufferType is not (0 or 1)) diagnostics.Add(new("UnsupportedIndexLayout", $"Stream {stream.Index} uses unsupported index buffer type {stream.IndexBufferType}."));
			if (stream.Offset == 0 || stream.Offset > tocData.Length - StreamRecordSize)
				diagnostics.Add(new("InvalidStreamRange", $"Stream {stream.Index} does not have a writable StreamInfo record range."));
		}
		foreach (var mesh in target.Meshes)
		{
			var raw = rawMeshes.FirstOrDefault(candidate => candidate.MeshInfoIndex == mesh.Index);
			if (raw is null) continue;
			if (raw.Sections.Count == 0) diagnostics.Add(new("MissingRawMeshSections", $"MeshInfo {mesh.Index} has no final material sections."));
			if (target.Streams.Count(stream => stream.Index == (int)mesh.StreamIndex) != 1)
				diagnostics.Add(new("MeshStreamCardinality", $"Target MeshInfo {mesh.Index} StreamIndex {mesh.StreamIndex} must match exactly one target Stream."));
			if (raw.StreamIndex != mesh.StreamIndex)
				diagnostics.Add(new("RawMeshStreamMismatch", $"Final RawMesh {raw.MeshInfoIndex} StreamIndex {raw.StreamIndex} does not match target MeshInfo {mesh.Index} StreamIndex {mesh.StreamIndex}."));
			if (mesh.Offset == 0 || mesh.Offset > tocData.Length - MeshInfoHeaderSize)
				diagnostics.Add(new("InvalidMeshInfoRange", $"MeshInfo {mesh.Index} does not have a writable MeshInfo record range."));
		}
		foreach (var duplicateStream in target.Streams.GroupBy(stream => stream.Index).Where(group => group.Count() != 1))
			diagnostics.Add(new("DuplicateTargetStreamIndex", $"Target StreamIndex {duplicateStream.Key} does not identify exactly one Stream."));
	}

	private static void ValidateBoneInfoRebuilds(UnitMeshModel target, ReadOnlySpan<byte> tocData, IReadOnlyList<CanonicalBoneInfoRebuild>? rebuilt, List<CanonicalPlanDiagnostic> diagnostics)
	{
		if (target.BoneInfos.Count > 0 && (rebuilt is null || rebuilt.Count == 0))
		{
			diagnostics.Add(new("BoneInfoRewriteIncomplete", "The target contains BoneInfo records, but no complete canonical BoneInfo rebuild result was supplied."));
			return;
		}
		if (rebuilt is null || rebuilt.Count == 0) return;
		if (target.BoneInfos.Count == 0 || target.BoneInfoOffset == 0)
		{
			diagnostics.Add(new("BoneInfoRewriteUnavailable", "Canonical bone reconstruction was requested, but the target has no writable BoneInfo block."));
			return;
		}
		if (target.StreamInfoOffset == 0 || target.BoneInfoOffset >= target.StreamInfoOffset || target.StreamInfoOffset > tocData.Length)
			diagnostics.Add(new("BoneInfoRewriteUnavailable", "Canonical BoneInfo block boundaries are not safely known."));
		var lods = rebuilt.Select(item => item.LodIndex).ToArray();
		if (lods.Distinct().Count() != lods.Length || lods.Any(lod => lod < 0 || lod >= target.BoneInfos.Count))
			diagnostics.Add(new("BoneInfoRewriteIncomplete", "Every rebuilt BoneInfo must identify one unique target LOD."));
		if (rebuilt.Count != target.BoneInfos.Count)
			diagnostics.Add(new("BoneInfoRewriteIncomplete", "Canonical BoneInfo writeback requires a complete replacement for every target BoneInfo record."));
	}

	private static (byte[] TocData, UnitMeshModel Model) RebuildBoneInfoBlock(UnitMeshModel target, byte[] tocData, IReadOnlyList<CanonicalBoneInfoRebuild> rebuilt, List<CanonicalPlanDiagnostic> diagnostics)
	{
		var ordered = rebuilt.OrderBy(item => item.LodIndex).Select(item => item.BoneInfo).ToArray();
		var payloads = ordered.Select(info => UnitMeshWriter.SerializeBoneInfo(info, new Dictionary<uint, byte[]>())).ToArray();
		var start = checked((int)target.BoneInfoOffset);
		var end = checked((int)target.StreamInfoOffset);
		var replacement = new List<byte>();
		AppendUInt32(replacement, checked((uint)payloads.Length));
		var relative = checked(4 + payloads.Length * 4);
		foreach (var payload in payloads)
		{
			AppendUInt32(replacement, checked((uint)relative));
			relative = checked(relative + payload.Length);
		}
		foreach (var payload in payloads) replacement.AddRange(payload);
		PadToAlignment(replacement, 16);
		var delta = replacement.Count - (end - start);
		var output = new byte[checked(tocData.Length + delta)];
		tocData.AsSpan(0, start).CopyTo(output);
		replacement.CopyTo(output, start);
		tocData.AsSpan(end).CopyTo(output.AsSpan(start + replacement.Count));
		foreach (var offset in new[] { 0x5c, 0x60, 0x64, 0x70 })
			ShiftHeaderOffset(output, offset, end, delta, diagnostics);
		var model = ShiftBoneOffsets(target, end, delta) with { BoneInfos = ordered };
		return (output, model);
	}

	private static UnitMeshModel ShiftBoneOffsets(UnitMeshModel model, int threshold, int delta)
	{
		uint Shift(uint value) => value != 0 && value >= threshold ? checked((uint)((int)value + delta)) : value;
		return model with
		{
			BoneInfoOffset = Shift(model.BoneInfoOffset), StreamInfoOffset = Shift(model.StreamInfoOffset),
			EndingOffset = Shift(model.EndingOffset), MeshInfoOffset = Shift(model.MeshInfoOffset), MaterialsOffset = Shift(model.MaterialsOffset),
			Streams = model.Streams.Select(stream => stream with { Offset = Shift(stream.Offset) }).ToArray(),
			Meshes = model.Meshes.Select(mesh => mesh with { Offset = Shift(mesh.Offset), MaterialOffset = Shift(mesh.MaterialOffset), SectionsOffset = Shift(mesh.SectionsOffset), Sections = mesh.Sections.Select(section => section with { Offset = Shift(section.Offset) }).ToArray() }).ToArray()
		};
	}

	private static void ShiftHeaderOffset(byte[] data, int offset, int threshold, int delta, List<CanonicalPlanDiagnostic> diagnostics)
	{
		var value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
		if (value != 0 && value >= threshold) WriteUInt32(data, offset, checked((uint)((int)value + delta)));
		else if (value != 0 && value < threshold) diagnostics.Add(new("BoneInfoRewriteUnavailable", $"Header offset 0x{offset:x} precedes the BoneInfo successor block."));
	}

	private static UnitMeshInfo RebuildMeshInfo(UnitMeshInfo mesh, UnitRawMeshData raw, uint vertexOffset, uint indexOffset)
	{
		Trace($"rebuild-mesh-info mesh={mesh.Index} rawSections={raw.Sections.Count} rawVertices={raw.Vertices.Count} rawIndices={raw.Sections.Sum(section => section.Triangles.Count * 3)}");
		var materialSlots = raw.Sections.Select(section => section.MaterialSlotId).Distinct().ToArray();
		var sections = raw.Sections.Select((rawSection, index) => new UnitMeshSectionInfo(
			0,
			checked((uint)Array.IndexOf(materialSlots, rawSection.MaterialSlotId)),
			rawSection.MaterialSlotId,
			vertexOffset,
			checked((uint)raw.Vertices.Count),
			checked(indexOffset + (uint)raw.Sections.Take(index).Sum(item => item.Triangles.Count * 3)),
			checked((uint)(rawSection.Triangles.Count * 3)),
			checked((uint)index)
		)).ToArray();
		return mesh with
		{
			NumMaterials = checked((uint)raw.Sections.Select(section => section.MaterialSlotId).Distinct().Count()),
			NumSections = checked((uint)sections.Length),
			MaterialSlotIds = materialSlots,
			Sections = sections
		};
	}

	private static byte[] RebuildMeshInfoAndUnitTail(
		byte[] tocData,
		UnitMeshModel target,
		IReadOnlyList<UnitMeshInfo> meshes,
		IReadOnlyList<UnitRawMeshData> rawMeshes,
		IReadOnlyList<UnitMaterialBinding> materialBindings,
		out IReadOnlyList<UnitMeshInfo> relocatedMeshes,
		out uint materialsOffset,
		out uint endingOffset,
		List<CanonicalPlanDiagnostic> diagnostics)
	{
		// SDK MeshInfo.Serialize() seeks to MeshInfoOffset, writes count, offset table,
		// MeshInfoUnk (the mesh ids), then each self-contained record. We append this new
		// block instead of moving any unknown bytes, BoneInfo, or StreamInfo. Therefore all
		// absolute layouts that the reader depends on remain stable and the operation is
		// fail-closed rather than silently copying stale absolute addresses.
		var output = tocData.ToList();
		var meshInfoOffset = checked((uint)output.Count);
		AppendUInt32(output, checked((uint)meshes.Count));
		var offsetsTable = output.Count;
		for (var i = 0; i < meshes.Count; i++) AppendUInt32(output, 0);
		for (var i = 0; i < meshes.Count; i++) AppendUInt32(output, meshes[i].MeshId);
		var relocated = new List<UnitMeshInfo>(meshes.Count);
		var rawByIndex = rawMeshes.ToDictionary(raw => raw.MeshInfoIndex);
		for (var i = 0; i < meshes.Count; i++)
		{
			var mesh = meshes[i];
			var raw = rawByIndex[mesh.Index];
			var recordOffset = checked((uint)output.Count);
			var header = tocData.AsSpan(checked((int)mesh.Offset), MeshInfoHeaderSize).ToArray();
			output.AddRange(header);
			var slots = raw.Sections.Select(section => section.MaterialSlotId).Distinct().ToArray();
			var materialOffset = MeshInfoHeaderSize;
			foreach (var slot in slots) AppendUInt32(output, slot);
			var sectionsOffset = checked(output.Count - (int)recordOffset);
			var rebuiltSections = new List<UnitMeshSectionInfo>(raw.Sections.Count);
			for (var sectionIndex = 0; sectionIndex < raw.Sections.Count; sectionIndex++)
			{
				var rawSection = raw.Sections[sectionIndex];
				var materialIndex = checked((uint)Array.IndexOf(slots, rawSection.MaterialSlotId));
				var sectionOffset = checked((uint)output.Count);
				var indexCount = checked((uint)(rawSection.Triangles.Count * 3));
				AppendUInt32(output, materialIndex);
				AppendUInt32(output, mesh.Sections.Count > sectionIndex ? mesh.Sections[sectionIndex].VertexOffset : 0);
				AppendUInt32(output, checked((uint)raw.Vertices.Count));
				AppendUInt32(output, mesh.Sections.Count > sectionIndex ? mesh.Sections[sectionIndex].IndexOffset : 0);
				AppendUInt32(output, indexCount);
				AppendUInt32(output, checked((uint)sectionIndex));
				rebuiltSections.Add(new(sectionOffset, materialIndex, rawSection.MaterialSlotId,
					mesh.Sections.Count > sectionIndex ? mesh.Sections[sectionIndex].VertexOffset : 0,
					checked((uint)raw.Vertices.Count),
					mesh.Sections.Count > sectionIndex ? mesh.Sections[sectionIndex].IndexOffset : 0,
					indexCount, checked((uint)sectionIndex)));
			}
			var writableHeader = output.ToArray();
			WriteUInt32(writableHeader, recordOffset + 40, mesh.MeshId);
			WriteUInt32(writableHeader, recordOffset + 48, mesh.TransformIndex);
			WriteUInt32(writableHeader, recordOffset + 56, unchecked((uint)mesh.LodIndex));
			WriteUInt32(writableHeader, recordOffset + 60, mesh.StreamIndex);
			WriteUInt32(writableHeader, recordOffset + 104, checked((uint)slots.Length));
			WriteUInt32(writableHeader, recordOffset + 108, checked((uint)materialOffset));
			WriteUInt32(writableHeader, recordOffset + 120, checked((uint)rebuiltSections.Count));
			WriteUInt32(writableHeader, recordOffset + 124, checked((uint)sectionsOffset));
			output = writableHeader.ToList();
			SetUInt32(output, offsetsTable + i * 4, checked(recordOffset - meshInfoOffset));
			relocated.Add(mesh with
			{
				Offset = recordOffset,
				NumMaterials = checked((uint)slots.Length), MaterialOffset = checked((uint)materialOffset),
				NumSections = checked((uint)rebuiltSections.Count), SectionsOffset = checked((uint)sectionsOffset),
				MaterialSlotIds = slots, Sections = rebuiltSections
			});
		}
		materialsOffset = checked((uint)output.Count);
		var bindings = BuildUsedMaterialBindings(materialBindings, rawMeshes);
		AppendUInt32(output, checked((uint)bindings.Count));
		foreach (var binding in bindings) AppendUInt32(output, binding.SectionId);
		foreach (var binding in bindings) AppendUInt64(output, binding.MaterialId);
		endingOffset = checked((uint)output.Count);
		if (target.EndingOffset != 0) AppendUInt64(output, checked((ulong)meshes.Count));
		var final = output.ToArray();
		WriteUInt32(final, 0x64, meshInfoOffset);
		WriteUInt32(final, 0x70, materialsOffset);
		if (target.EndingOffset != 0) WriteUInt32(final, 0x60, endingOffset);
		relocatedMeshes = relocated;
		return final;
	}

	private static IReadOnlyList<UnitMaterialBinding> BuildUsedMaterialBindings(IReadOnlyList<UnitMaterialBinding> materialBindings, IReadOnlyList<UnitRawMeshData> rawMeshes)
	{
		var usedSlots = rawMeshes.SelectMany(raw => raw.Sections.Select(section => section.MaterialSlotId)).Distinct().ToHashSet();
		// SDK Serialize derives each MeshInfo material-slot table from RawMesh.Materials,
		// but only writes a Unit material-pair when that slot has a concrete MaterialId.
		// Keep unmatched target-local slots intact rather than substituting an unrelated
		// material; those slots are valid shell identity but have no portable dependency.
		return materialBindings
			.Where(binding => usedSlots.Contains(binding.SectionId))
			.ToArray();
	}

	private static void AppendUInt32(List<byte> data, uint value) { data.Add((byte)value); data.Add((byte)(value >> 8)); data.Add((byte)(value >> 16)); data.Add((byte)(value >> 24)); }
	private static void AppendUInt64(List<byte> data, ulong value) { AppendUInt32(data, (uint)value); AppendUInt32(data, (uint)(value >> 32)); }
	private static void SetUInt32(List<byte> data, int offset, uint value) { data[offset] = (byte)value; data[offset + 1] = (byte)(value >> 8); data[offset + 2] = (byte)(value >> 16); data[offset + 3] = (byte)(value >> 24); }
	private static void WriteUInt16(byte[] data, int offset, ushort value) { data[offset] = (byte)value; data[offset + 1] = (byte)(value >> 8); }
	private static void WriteSingle(byte[] data, int offset, float value) => WriteUInt32(data, offset, unchecked((uint)BitConverter.SingleToInt32Bits(value)));

	private static void WriteStreamGpuFields(byte[] tocData, UnitStreamInfo stream, uint vertexCount, uint vertexOffset, uint vertexSize, uint indexCount, uint indexOffset, uint indexSize)
	{
		var cursor = checked((int)stream.Offset + 352);
		WriteUInt32(tocData, cursor, vertexCount);
		WriteUInt32(tocData, cursor + 40, indexCount);
		WriteUInt32(tocData, cursor + 44, stream.IndexBufferType);
		WriteUInt32(tocData, cursor + 64, vertexOffset);
		WriteUInt32(tocData, cursor + 68, vertexSize);
		WriteUInt32(tocData, cursor + 72, indexOffset);
		WriteUInt32(tocData, cursor + 76, indexSize);
	}

	private static void WriteStreamLayouts(byte[] tocData, IReadOnlyList<UnitStreamInfo> streams)
	{
		foreach (var stream in streams)
		{
			if (stream.Components.Count * 20 > 320)
				throw new InvalidDataException($"Stream {stream.Index} exceeds the fixed 320-byte component block.");
			var offset = checked((int)stream.Offset);
			if (offset <= 0 || offset > tocData.Length - StreamRecordSize)
				throw new InvalidDataException($"Stream {stream.Index} has no writable StreamInfo record.");
			WriteUInt64(tocData, offset, stream.ComponentInfoId);
			Array.Clear(tocData, offset + 8, 320);
			for (var index = 0; index < stream.Components.Count; index++)
			{
				var component = stream.Components[index];
				var componentOffset = offset + 8 + index * 20;
				WriteUInt32(tocData, componentOffset, component.Type);
				WriteUInt32(tocData, componentOffset + 4, component.Format);
				WriteUInt32(tocData, componentOffset + 8, component.Index);
				WriteUInt64(tocData, componentOffset + 12, component.Unknown);
			}
			WriteUInt64(tocData, offset + 328, checked((ulong)stream.Components.Count));
			// StreamInfo writes NumVertices at +352 and VertexStride immediately after it
			// at +356. Keeping this aligned with SerializeVertexBuffer is mandatory: a
			// stale stride makes every MeshInfo vertex range decode beyond the GPU payload.
			WriteUInt32(tocData, offset + 356, stream.VertexStride);
		}
	}

	private static void ValidateTriangle(UnitRawMeshData mesh, UnitTriangleIndices triangle)
	{
		if (triangle.A >= mesh.Vertices.Count || triangle.B >= mesh.Vertices.Count || triangle.C >= mesh.Vertices.Count)
			throw new InvalidDataException($"RawMesh {mesh.MeshInfoIndex} contains a triangle outside its vertex range.");
	}

	private static void WriteIndex(List<byte> output, uint value, uint indexType)
	{
		if (indexType == 0 && value > ushort.MaxValue) throw new InvalidDataException("A 16-bit canonical index buffer cannot contain an index greater than 65535.");
		if (indexType == 0) { output.Add((byte)value); output.Add((byte)(value >> 8)); }
		else { output.Add((byte)value); output.Add((byte)(value >> 8)); output.Add((byte)(value >> 16)); output.Add((byte)(value >> 24)); }
	}

	private static void PadToAlignment(List<byte> output, int alignment)
	{
		while (output.Count % alignment != 0) output.Add(0);
	}

	private static void WriteUInt32(byte[] data, uint offset, uint value) => WriteUInt32(data, checked((int)offset), value);
	private static void WriteUInt64(byte[] data, int offset, ulong value)
	{
		WriteUInt32(data, offset, unchecked((uint)value));
		WriteUInt32(data, offset + 4, unchecked((uint)(value >> 32)));
	}
	private static void WriteUInt32(byte[] data, int offset, uint value)
	{
		if (offset < 0 || offset > data.Length - 4) throw new InvalidDataException("Canonical output write range is outside target TocData.");
		BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset, 4), value);
	}
}
