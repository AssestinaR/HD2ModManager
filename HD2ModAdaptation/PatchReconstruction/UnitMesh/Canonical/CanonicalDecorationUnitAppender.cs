namespace HD2ModAdaptation.PatchReconstruction.UnitMesh.Canonical;

public sealed record CanonicalDecorationFragment(
    UnitMeshInfo Mesh,
    UnitRawMeshData RawMesh,
    UnitStreamInfo Stream,
    IReadOnlyList<UnitMaterialBinding> Materials,
    IReadOnlyList<UnitBoneInfo> BoneInfos,
    UnitTransformInfo TransformInfo,
    IReadOnlyList<uint> TransformNameHashes);

public sealed record CanonicalDecorationAppendInput(CanonicalDecorationFragment Fragment, ulong MaterialNamespace);

public sealed record CanonicalDecorationAppendResult(PatchUnitMeshEditResult? Edit, IReadOnlyList<CanonicalPlanDiagnostic> Diagnostics)
{
    public bool IsValid => Edit is not null && Diagnostics.Count == 0;
}

// Unit-level Blender Join pipeline. All sources are appended before material layout and palette
// compilation, so multiple decorations share one authoritative final Unit representation.
public sealed class CanonicalDecorationUnitAppender
{
    public CanonicalDecorationAppendResult TryAppend(
        PatchUnitMesh targetUnit, int targetMeshInfoIndex, CanonicalDecorationFragment fragment,
        ulong decorationNamespace, UnitTransformInfo avatarTransformInfo)
        => TryAppendMany(targetUnit, targetMeshInfoIndex, [new(fragment, decorationNamespace)], avatarTransformInfo);

    public CanonicalDecorationAppendResult TryAppendMany(
        PatchUnitMesh targetUnit, int targetMeshInfoIndex, IReadOnlyList<CanonicalDecorationAppendInput> inputs,
        UnitTransformInfo avatarTransformInfo)
		=> TryAppendLodFamily(targetUnit,
			new Dictionary<int, IReadOnlyList<CanonicalDecorationAppendInput>> { [targetMeshInfoIndex] = inputs },
			avatarTransformInfo);

	// A Unit has one serialized TransformInfo and material table shared by all of its LOD meshes.
	// Rebuild the full selected LOD family in one transaction so the game cannot switch from an
	// appended near LOD to an untouched far LOD.
	public CanonicalDecorationAppendResult TryAppendLodFamily(
		PatchUnitMesh targetUnit,
		IReadOnlyDictionary<int, IReadOnlyList<CanonicalDecorationAppendInput>> inputsByTargetMesh,
		UnitTransformInfo avatarTransformInfo,
		bool replaceTargetGeometry = false,
		CanonicalCullingBoundsMode cullingBounds = CanonicalCullingBoundsMode.Preserve)
    {
        ArgumentNullException.ThrowIfNull(targetUnit);
		ArgumentNullException.ThrowIfNull(inputsByTargetMesh);
        ArgumentNullException.ThrowIfNull(avatarTransformInfo);
		if (inputsByTargetMesh.Count == 0 || inputsByTargetMesh.Values.All(inputs => inputs.Count == 0))
			return new(null, [new("DecorationSourcesMissing", "Decoration append requires at least one source mesh.")]);
        var diagnostics = new List<CanonicalPlanDiagnostic>();
        if (targetUnit.Model.CompositeRef != 0) return new(null, [new("DecorationCompositeUnitUnsupported", "Composite-backed host Units are not supported for decoration append.")]);
		var groups = new List<(UnitRawMeshData TargetRaw, UnitMeshInfo TargetMesh, IReadOnlyList<CanonicalDecorationAppendInput> Inputs)>();
		foreach (var (targetMeshInfoIndex, inputs) in inputsByTargetMesh.OrderBy(item => item.Key))
		{
			if (inputs.Count == 0) continue;
			var targetRaw = targetUnit.Model.RawMeshData.SingleOrDefault(raw => raw.MeshInfoIndex == targetMeshInfoIndex);
			var targetMesh = targetUnit.Model.Meshes.SingleOrDefault(mesh => mesh.Index == targetMeshInfoIndex);
			if (targetRaw is null || targetMesh is null) return new(null, [new("DecorationTargetMeshMissing", "The selected host Unit has no readable target mesh.")]);
			if (targetRaw.LodIndex < 0) return new(null, [new("DecorationNonVisualLod", "Decoration append only supports visible LOD meshes.")]);
			if (inputs.Any(input => input.Fragment.RawMesh.LodIndex != targetRaw.LodIndex))
				return new(null, [new("DecorationLodMismatch", "A decoration LOD does not match the selected host LOD.")]);
			groups.Add((targetRaw, targetMesh, inputs));
		}
		if (groups.Count == 0) return new(null, [new("DecorationSourcesMissing", "Decoration append requires at least one source mesh.")]);
		if (groups.GroupBy(group => group.TargetRaw.LodIndex).Any(group => group.Count() > 1))
			return new(null, [new("DecorationDuplicateTargetLod", "Decoration append cannot rebuild more than one host mesh for the same LOD.")]);

		var allInputs = groups.SelectMany(group => group.Inputs).ToArray();
		var sourceModels = allInputs.Select(input => CreateSourceModel(input.Fragment)).ToArray();
        UnitMeshModel expanded;
        try
        {
            expanded = new CanonicalTransformInfoExpander().Expand(targetUnit.Model,
                sourceModels.Select((model, index) => (model, allInputs[index].Fragment.RawMesh)), avatarTransformInfo);
        }
        catch (InvalidDataException exception) { return new(null, [new("DecorationTransformExpansionFailed", exception.Message)]); }

        var materialProvenance = new List<CanonicalMaterialSectionProvenance>();
		var mergedByMesh = new Dictionary<int, UnitRawMeshData>();
		var originsByMesh = new Dictionary<int, IReadOnlyList<CanonicalAppendSectionOrigin>>();
		var sourceOffset = 0;
		foreach (var group in groups)
        {
			var merged = group.TargetRaw;
			if (replaceTargetGeometry)
			{
				var targetStream = targetUnit.Model.Streams.SingleOrDefault(stream => stream.Index == group.TargetRaw.StreamIndex);
				if (targetStream is null) return new(null, [new("DecorationTargetStreamMissing", "The selected replacement host mesh has no stream.")]);
				var placeholder = new CanonicalPlaceholderMinifier().TryMinify(group.TargetRaw, targetStream);
				diagnostics.AddRange(placeholder.Diagnostics);
				if (!placeholder.IsValid) return new(null, diagnostics);
				merged = placeholder.Mesh!;
			}
			var origins = merged.Sections.Select((_, index) => new CanonicalAppendSectionOrigin(index, -1, index)).ToList();
			var appendSources = new List<(UnitRawMeshData Source, System.Numerics.Matrix4x4 SourceToTargetLocal)>();
			var nextSectionStart = merged.Sections.Count;
			for (var sourceIndex = 0; sourceIndex < group.Inputs.Count; sourceIndex++)
			{
				var input = group.Inputs[sourceIndex];
				var source = sourceModels[sourceOffset + sourceIndex];
				var transform = new CanonicalTransformResolver().TryResolve(source, input.Fragment.RawMesh.MeshInfoIndex, expanded, group.TargetMesh.Index);
				diagnostics.AddRange(transform.Diagnostics);
				if (!transform.IsValid) return new(null, diagnostics);
				var start = nextSectionStart;
				var sourceBindings = BuildMaterialProvenance(source, input.Fragment.RawMesh, group.TargetRaw, start, group.TargetMesh.Index, input.MaterialNamespace, diagnostics);
				if (diagnostics.Count != 0) return new(null, diagnostics);
				materialProvenance.AddRange(sourceBindings);
				origins.AddRange(input.Fragment.RawMesh.Sections.Select((_, section) => new CanonicalAppendSectionOrigin(start + section, sourceIndex, section)));
				appendSources.Add((input.Fragment.RawMesh, transform.SourceToTargetLocal!.Value));
				nextSectionStart += input.Fragment.RawMesh.Sections.Count;
			}
			var appended = new CanonicalAppendMeshAssembler().TryAppendMany(merged, appendSources);
			diagnostics.AddRange(appended.Diagnostics);
			if (!appended.IsValid) return new(null, diagnostics);
			merged = appended.Mesh!;
			mergedByMesh.Add(group.TargetMesh.Index, merged);
			originsByMesh.Add(group.TargetMesh.Index, origins);
			sourceOffset += group.Inputs.Count;
        }

		var provisional = expanded.RawMeshData.Select(raw => mergedByMesh.TryGetValue(raw.MeshInfoIndex, out var merged) ? merged : raw).ToArray();
        var material = new CanonicalUnitMaterialLayoutCompiler().TryCompile(expanded, provisional, materialProvenance);
        diagnostics.AddRange(material.Diagnostics);
        if (!material.IsValid) return new(null, diagnostics);
		var finalByMesh = material.Meshes.ToDictionary(raw => raw.MeshInfoIndex);
		var rebuiltBonesByLod = new Dictionary<int, UnitBoneInfo>();
		sourceOffset = 0;
		foreach (var group in groups)
		{
			var finalMerged = finalByMesh[group.TargetMesh.Index];
			var groupSources = group.Inputs.Select((input, index) => new CanonicalAppendSource(sourceModels[sourceOffset + index], input.Fragment.RawMesh)).ToArray();
			var bones = new CanonicalAppendBoneCompiler().TryCompile(expanded, group.TargetRaw, groupSources, finalMerged, originsByMesh[group.TargetMesh.Index]);
			diagnostics.AddRange(bones.Diagnostics);
			if (!bones.IsValid) return new(null, diagnostics);
			finalByMesh[group.TargetMesh.Index] = MergeSectionsByMaterialSlot(bones.Mesh!);
			rebuiltBonesByLod.Add(group.TargetRaw.LodIndex, bones.BoneInfo!);
			sourceOffset += group.Inputs.Count;
		}

		var finalRaw = material.Meshes.Select(raw => finalByMesh.TryGetValue(raw.MeshInfoIndex, out var final) ? final : raw).ToArray();
        var stream = new CanonicalStreamContractCompiler().TryCompile(expanded, finalRaw);
        diagnostics.AddRange(stream.Diagnostics);
        if (!stream.IsValid) return new(null, diagnostics);
        expanded = expanded with { Streams = stream.Streams };
        var prepared = new List<UnitRawMeshData>(finalRaw.Length);
        foreach (var raw in finalRaw)
        {
            var result = new CanonicalMeshPreparation().TryPrepare(raw, expanded.Streams.Single(item => item.Index == raw.StreamIndex));
            diagnostics.AddRange(result.Diagnostics);
            if (result.Mesh is not null) prepared.Add(result.Mesh);
        }
        if (diagnostics.Count != 0 || prepared.Count != finalRaw.Length) return new(null, diagnostics);
		// 追加几何之后必须重算包围盒：CanonicalUnitRebuilder 不动 MeshInfo 里那 7 个 float，
		// 留下的会是**外壳的**盒，顶点跑出盒外就被游戏剔除（表现是"整块几何消失但游戏不崩"）。
		// 默认 Preserve 保持三条在役路径的产物字节不变；实验 / 拆件路径显式传 UnionWithComputed。
		expanded = CanonicalCullingBounds.Apply(expanded, prepared, cullingBounds);
		var rebuiltBones = expanded.BoneInfos.Select((bone, index) => new CanonicalBoneInfoRebuild(index, rebuiltBonesByLod.GetValueOrDefault(index) ?? bone)).ToArray();
        var rebuilt = new CanonicalUnitRebuilder().TryRebuild(expanded, targetUnit.Payload.TocData, prepared, rebuiltBones, material.Bindings);
        diagnostics.AddRange(rebuilt.Diagnostics);
        if (!rebuilt.IsValid) return new(null, diagnostics);
        return new(new PatchUnitMeshEditResult(targetUnit.Entry, targetUnit.Payload, rebuilt.Output!.TocData, rebuilt.Output.GpuData,
            ReplacementMaterialIds: material.Bindings.Select(binding => binding.MaterialId).Distinct().ToArray()), []);
    }

    private static IReadOnlyList<CanonicalMaterialSectionProvenance> BuildMaterialProvenance(
        UnitMeshModel source, UnitRawMeshData sourceRaw, UnitRawMeshData targetRaw, int finalSectionStart,
        int targetMeshInfoIndex, ulong materialNamespace, List<CanonicalPlanDiagnostic> diagnostics)
    {
        var resolved = CanonicalMaterialBindingResolver.Resolve(source, sourceRaw, targetRaw);
        diagnostics.AddRange(resolved.Diagnostics);
        if (!resolved.IsValid) return [];
        var result = new List<CanonicalMaterialSectionProvenance>();
        var visible = 0;
        for (var sectionIndex = 0; sectionIndex < sourceRaw.Sections.Count; sectionIndex++)
        {
            if (sourceRaw.Sections[sectionIndex].Triangles.Count == 0) continue;
            if (visible >= resolved.ResolvedSectionBindings.Count)
            {
                diagnostics.Add(new("DecorationMaterialBindingMismatch", "The decoration material sections could not be matched to its geometry."));
                break;
            }
            var binding = resolved.ResolvedSectionBindings[visible++];
            result.Add(new(targetMeshInfoIndex, finalSectionStart + sectionIndex, materialNamespace,
                binding.SourceSlotId, binding.PreferredTargetSlotId, binding.MaterialId,
                binding.UsesTargetUnitMaterialSlotLookup, PreferMeshLocalSlotReuse: true));
        }
        return result;
    }

	// SDK GetMeshData groups all Blender faces that share one material slot into one
	// RawMaterial section. object.join may leave multiple source sections with the same
	// slot; retaining them creates a non-SDK section table even though BoneInfo remaps
	// are material-slot based.
	private static UnitRawMeshData MergeSectionsByMaterialSlot(UnitRawMeshData mesh)
	{
		var sections = mesh.Sections
			.GroupBy(section => (section.MaterialIndex, section.MaterialSlotId))
			.Select(group => new UnitRawMeshSectionData(group.Key.MaterialIndex, group.Key.MaterialSlotId,
				group.SelectMany(section => section.Triangles).ToArray()))
			.ToArray();
		return mesh with { Sections = sections, Triangles = sections.SelectMany(section => section.Triangles).ToArray() };
	}

    private static UnitMeshModel CreateSourceModel(CanonicalDecorationFragment fragment)
        => new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, UnitCustomizationInfo.Empty, fragment.BoneInfos, [fragment.Stream], [fragment.Mesh], fragment.Materials, [], [fragment.RawMesh])
        { TransformInfo = fragment.TransformInfo, TransformNameHashes = fragment.TransformNameHashes };
}
