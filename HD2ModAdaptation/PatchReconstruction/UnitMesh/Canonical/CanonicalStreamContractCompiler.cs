namespace HD2ModAdaptation.PatchReconstruction.UnitMesh.Canonical;

// Purpose: Compiles the final RawMesh stream contract used by both vertex encoding and TOC declaration writing.
// SDK reference: SetupRawMeshComponents aggregates final RawMeshes by stream before SerializeGpuData.
// Existing game stream ABI is preserved whenever it can encode every final semantic. When an
// expansion is necessary, the fallback follows the community exporter profile: optional
// color/normal, required position, UV[n], one weight vector, and max bone-index groups.
public sealed record CanonicalStreamContractCompilation(
	IReadOnlyList<UnitStreamInfo> Streams,
	IReadOnlyList<CanonicalPlanDiagnostic> Diagnostics)
{
	public bool IsValid => Diagnostics.Count == 0;
}

public sealed class CanonicalStreamContractCompiler
{
	public CanonicalStreamContractCompilation TryCompile(
		UnitMeshModel target,
		IReadOnlyList<UnitRawMeshData> finalRawMeshes)
	{
		ArgumentNullException.ThrowIfNull(target);
		ArgumentNullException.ThrowIfNull(finalRawMeshes);
		var diagnostics = new List<CanonicalPlanDiagnostic>();
		var rawByMesh = finalRawMeshes.GroupBy(raw => raw.MeshInfoIndex).ToDictionary(group => group.Key, group => group.ToArray());
		var streams = new List<UnitStreamInfo>(target.Streams.Count);
		foreach (var stream in target.Streams)
		{
			var targetMeshes = target.Meshes.Where(mesh => mesh.StreamIndex == (uint)stream.Index).ToArray();
			if (targetMeshes.Any(mesh => !rawByMesh.TryGetValue(mesh.Index, out var raw) || raw.Length != 1))
			{
				diagnostics.Add(new("IncompleteStreamRawMeshCoverage", $"Stream {stream.Index} does not have exactly one final RawMesh for every target MeshInfo."));
				continue;
			}
			var rawMeshes = targetMeshes.Select(mesh => rawByMesh[mesh.Index][0]).ToArray();
			foreach (var raw in rawMeshes)
			{
				if (raw.StreamIndex != stream.Index)
					diagnostics.Add(new("RawMeshStreamMismatch", $"Final RawMesh {raw.MeshInfoIndex} does not belong to target stream {stream.Index}."));
				foreach (var triangle in raw.Sections.SelectMany(section => section.Triangles))
					if (triangle.A >= raw.Vertices.Count || triangle.B >= raw.Vertices.Count || triangle.C >= raw.Vertices.Count)
						diagnostics.Add(new("IndexOutOfRange", $"Final RawMesh {raw.MeshInfoIndex} contains an out-of-range triangle."));
			}
			var needs32Bit = rawMeshes.Any(raw => raw.Vertices.Count > ushort.MaxValue
				|| raw.Sections.SelectMany(section => section.Triangles).Any(triangle => triangle.A > ushort.MaxValue || triangle.B > ushort.MaxValue || triangle.C > ushort.MaxValue));
			streams.Add(BuildContract(target.Version, stream, rawMeshes, needs32Bit));
		}
		return diagnostics.Count == 0
			? new(streams, Array.Empty<CanonicalPlanDiagnostic>())
			: new([], Array.AsReadOnly(diagnostics.ToArray()));
	}

	private static UnitStreamInfo BuildContract(uint unitVersion, UnitStreamInfo template, IReadOnlyList<UnitRawMeshData> meshes, bool needs32Bit)
	{
		var allComponents = meshes.SelectMany(mesh => mesh.Vertices).SelectMany(vertex => vertex.Components).ToArray();
		// Appending geometry must not silently reinterpret an existing Unit's vertex bytes.
		// Most game Units already have a valid stream ABI (for example float UVs at stride 56),
		// so preserve its order, formats and stride whenever it covers the final semantics.
		if (CoversAllFinalSemantics(template, allComponents))
		{
			return template with { IndexBufferType = needs32Bit ? 1u : template.IndexBufferType };
		}

		// The fallback mirrors the SDK writer's new-stream profile. In particular its
		// SetupRawMeshComponents writes UVs as vec2_float (Format 1), not vec2_half.
		var formats = unitVersion == 10800437
			? new SdkFormatProfile(4, 2, 26, 1, 24, 31)
			: new SdkFormatProfile(4, 2, 30, 1, 28, 35);
		var uvIndices = allComponents.Where(component => component.Type == 4).Select(component => component.Index).ToArray();
		var sourceUvCount = uvIndices.Length == 0 ? 0u : uvIndices.Max() + 1;
		var targetUvIndices = template.Components.Where(component => component.Type == 4).Select(component => component.Index).ToArray();
		var targetUvCount = targetUvIndices.Length == 0 ? 0u : targetUvIndices.Max() + 1;
		// A fallback stream is an expansion of the target ABI, not a fresh schema
		// derived only from source vertices. Keep target-declared UV channels even
		// when the source has fewer of them, then add any channels required by the
		// source geometry.
		var uvCount = Math.Max(sourceUvCount, targetUvCount);
		var indexGroups = allComponents.Where(component => component.Type == 6).Select(component => component.Index).ToArray();
		var isSkinned = indexGroups.Length != 0;
		var boneIndexGroups = isSkinned ? indexGroups.Max() + 1 : 0u;
		var components = new List<UnitStreamComponentInfo>();
		// Target-only attributes still carry shader semantics. In particular, the
		// current game Unit may declare vertex color while an older source Unit does
		// not. Dropping it changes the material input contract before preparation can
		// apply its SDK-compatible default value.
		if (allComponents.Any(component => component.Type == 5) || template.Components.Any(component => component.Type == 5))
			components.Add(Component(5, "color", formats.Color, "rgba_r8g8b8a8", 0, 4));
		if (!allComponents.Any(component => component.Type == 0 && component.Index == 0))
			throw new InvalidDataException($"Canonical stream {template.Index} has no final position semantic.");
		components.Add(Component(0, "position", formats.Position, "vec3_float", 0, 12));
		if (allComponents.Any(component => component.Type == 1) || template.Components.Any(component => component.Type == 1))
			components.Add(Component(1, "normal", formats.Normal, "unk_normal", 0, 4));
		for (var index = 0u; index < uvCount; index++) components.Add(Component(4, "uv", formats.Uv, "vec2_float", index, 8));
		if (isSkinned)
		{
			if (!allComponents.Any(component => component.Type == 7 && component.Index == 0))
				throw new InvalidDataException($"Canonical skinned stream {template.Index} has bone indices but no bone_weight[0].");
			components.Add(Component(7, "bone_weight", formats.Weight, "vec4_half", 0, 8));
			for (var index = 0u; index < boneIndexGroups; index++)
				components.Add(Component(6, "bone_index", formats.BoneIndex, "vec4_uint8", index, 4));
		}
		var stride = checked((uint)components.Sum(component => component.Size));
		return template with
		{
			NumComponents = checked((ulong)components.Count),
			VertexStride = stride,
			IndexBufferType = needs32Bit ? 1u : 0u,
			Components = components
		};
	}

	private static bool CoversAllFinalSemantics(
		UnitStreamInfo contract,
		IReadOnlyList<UnitVertexComponentValue> finalComponents)
	{
		foreach (var component in finalComponents)
		{
			var target = contract.Components.FirstOrDefault(item => item.Type == component.Type && item.Index == component.Index);
			if (target is null || !CanEncode(target, component))
				return false;
		}
		return contract.Components.Count != 0
			&& contract.Components.Sum(component => component.Size) == contract.VertexStride;
	}

	private static bool CanEncode(UnitStreamComponentInfo target, UnitVertexComponentValue source)
	{
		var sourceCount = source.FloatValues.Length != 0 ? source.FloatValues.Length : source.UIntValues.Length;
		var capacity = target.FormatName switch
		{
			"float" => 1,
			"vec2_float" or "vec2_half" => 2,
			"vec3_float" or "unk_normal" => 3,
			"vec4_float" or "vec4_half" or "vec4_uint8" or "vec4_uint32" or "rgba_r8g8b8a8" or "vec4_1010102" => 4,
			_ => 0
		};
		return capacity >= sourceCount;
	}

	private static UnitStreamComponentInfo Component(uint type, string typeName, uint format, string formatName, uint index, uint size)
		=> new(type, typeName, format, formatName, index, 0, size);

	private sealed record SdkFormatProfile(uint Color, uint Position, uint Normal, uint Uv, uint BoneIndex, uint Weight);
}
