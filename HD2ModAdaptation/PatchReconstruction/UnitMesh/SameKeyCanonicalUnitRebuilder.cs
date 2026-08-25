using HD2ModAdaptation.PatchReconstruction.PatchWorkspace;
using HD2ModAdaptation.PatchReconstruction.UnitMesh.Canonical;

namespace HD2ModAdaptation.PatchReconstruction.UnitMesh;

// Purpose: Rebuilds one same-AssetKey Unit with the current Canonical pipeline without using the legacy SDK-style chain.
public sealed record SameKeyCanonicalUnitRebuildRequest(
    PatchUnitMesh Source,
    GameDataUnitMesh Target,
    IReadOnlyList<TargetShellMeshMapping> MeshMappings)
{
    public UnitTransformInfo? AvatarTransformInfo { get; init; }
    public bool MinifyUnmappedCullingMeshes { get; init; }

    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Source);
        ArgumentNullException.ThrowIfNull(Target);
        ArgumentNullException.ThrowIfNull(MeshMappings);
        if (Source.Entry.AssetKey != Target.AssetKey)
            throw new InvalidDataException("Canonical same-key rebuilding requires identical source and target AssetKeys.");
        if (MeshMappings.Any(mapping => mapping.SourceUnitAssetKey != Source.Entry.AssetKey))
            throw new InvalidDataException("A same-key Canonical mapping references another source Unit.");
        if (MeshMappings.Select(mapping => mapping.TargetMeshInfoIndex).Distinct().Count() != MeshMappings.Count)
            throw new InvalidDataException("A same-key Canonical target mesh has multiple source mappings.");
    }
}

public sealed record SameKeyCanonicalUnitRebuildResult(
    PatchWorkspaceJobResult? Job,
    int ReplacedMeshCount,
    int HiddenMeshCount,
    IReadOnlyList<CanonicalPlanDiagnostic> Diagnostics,
    IReadOnlyList<CanonicalPlanDiagnostic>? MaterialObservations = null,
    CanonicalUnitRebuildTelemetry? Telemetry = null,
    UnitMeshModel? Model = null)
{
    public bool IsValid => Job is { IsValid: true } && Diagnostics.Count == 0;
}

public sealed class SameKeyCanonicalUnitRebuilder
{
    private readonly CanonicalTransformResolver transformResolver = new();
    private readonly CanonicalBoneRebuilder boneRebuilder = new();
    private readonly CanonicalMeshSkinningRouter skinningRouter = new();
    private readonly CanonicalMeshSemanticMerger merger = new();
    private readonly CanonicalMeshPreparation preparation = new();
    private readonly CanonicalPlaceholderMinifier minifier = new();
    private readonly CanonicalLodBonePaletteCompiler paletteCompiler = new();
    private readonly CanonicalStreamContractCompiler streamCompiler = new();
    private readonly CanonicalUnitRebuilder unitRebuilder = new();
    private readonly CanonicalTransformInfoExpander transformInfoExpander = new();

    public SameKeyCanonicalUnitRebuildResult Rebuild(SameKeyCanonicalUnitRebuildRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        using var positionDiagnostics = CanonicalPositionDiagnostics.BeginUnit(request.Target.AssetKey.FileId);
        var diagnostics = new List<CanonicalPlanDiagnostic>();
        var phaseStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var mappings = request.MeshMappings.ToDictionary(mapping => mapping.TargetMeshInfoIndex);
        var targetModel = request.Target.Model;
        var sourceCapability = CanonicalUnitCapabilityDetector.Analyze(request.Source.Model);
        var targetCapability = CanonicalUnitCapabilityDetector.Analyze(request.Target.Model);
        if (targetCapability.Layout == CanonicalUnitLayoutKind.CompositeBacked)
            return Failure("CompositeCanonicalRebuildPending", "当前 Canonical 已识别到 Composite-backed Unit；该 Unit 会保留或走直接复用，暂不进入 inline Mesh 重建。", 0, 0);
        if (targetCapability.Layout != CanonicalUnitLayoutKind.Inline || !targetCapability.HasSupportedStreamAbi)
            return Failure("UnsupportedCanonicalUnitLayout", targetCapability.Reason, 0, 0);
        if (request.AvatarTransformInfo is not null && targetCapability.RequiresAvatarRigExpansion && sourceCapability.RequiresAvatarRigExpansion)
        {
            var transformSources = request.MeshMappings
                .Select(mapping =>
                {
                    var sourceRaw = FindRaw(request.Source.Model, mapping.SourceMeshInfoIndex, diagnostics, "source");
                    return sourceRaw is null
                        ? ((UnitMeshModel Source, UnitRawMeshData SourceMesh)?)null
                        : (request.Source.Model, sourceRaw);
                })
                .Where(item => item is not null)
                .Select(item => item!.Value);
            if (diagnostics.Count == 0)
            {
                targetModel = transformInfoExpander.Expand(
                    targetModel,
                    transformSources,
                    request.AvatarTransformInfo,
                    includeAvatarSkeleton: true);
            }
        }
        var transformExpansionElapsed = phaseStopwatch.Elapsed;
        phaseStopwatch.Restart();
        var finalMeshes = new List<UnitRawMeshData>(request.Target.Model.Meshes.Count);
        var provisionalByMesh = new Dictionary<int, UnitBoneInfo>();
        var sourceMaterialSections = new List<CanonicalMaterialSectionProvenance>();
        var materialObservations = new List<CanonicalPlanDiagnostic>();
        var hiddenCount = 0;
        var replacedCount = 0;
		var routeElapsed = TimeSpan.Zero;
		var mergeElapsed = TimeSpan.Zero;
		var minifyElapsed = TimeSpan.Zero;
		var materialResolutionElapsed = TimeSpan.Zero;

        foreach (var targetMesh in request.Target.Model.Meshes)
        {
            var targetRaw = FindRaw(targetModel, targetMesh.Index, diagnostics, "target");
            if (targetRaw is null) continue;
            var targetStream = targetModel.Streams.SingleOrDefault(stream => stream.Index == (int)targetRaw.StreamIndex);
            if (targetStream is null) continue;

            if (!mappings.TryGetValue(targetMesh.Index, out var mapping))
            {
                if (targetRaw.LodIndex == -1 && !request.MinifyUnmappedCullingMeshes)
				{
					// A culling mesh with no explicit source counterpart must retain the
					// target cutout; it is not a visual mesh eligible for minification.
					finalMeshes.Add(targetRaw);
				}
				else
				{
					var minifyStopwatch = System.Diagnostics.Stopwatch.StartNew();
                    var hidden = minifier.TryMinify(targetRaw, targetStream);
					minifyElapsed += minifyStopwatch.Elapsed;
                    diagnostics.AddRange(hidden.Diagnostics);
                    if (hidden.Mesh is not null) finalMeshes.Add(hidden.Mesh);
                    hiddenCount++;
				}
                continue;
            }

            var sourceRaw = FindRaw(request.Source.Model, mapping.SourceMeshInfoIndex, diagnostics, "source");
            if (sourceRaw is null) continue;
            CanonicalPositionDiagnostics.RecordMesh("source", sourceRaw, targetStream);
			var detailStopwatch = System.Diagnostics.Stopwatch.StartNew();
            var transform = transformResolver.TryResolve(request.Source.Model, sourceRaw.MeshInfoIndex, targetModel, targetRaw.MeshInfoIndex);
            diagnostics.AddRange(transform.Diagnostics);
            if (!transform.IsValid) continue;

            var route = skinningRouter.TryPrepare(
                request.Source.Model,
                sourceRaw,
                targetModel,
                targetRaw,
                targetStream,
                CanonicalSkinningMode.BindStaticToTargetMeshTransform,
                CanonicalBoneAnchor.TargetMeshTransform);
			routeElapsed += detailStopwatch.Elapsed;
            diagnostics.AddRange(route.Diagnostics);
            if (!route.IsValid || route.Mesh is null) continue;
            var preparedSource = route.Mesh;
            var provisionalBone = route.ProvisionalBoneInfo;
            CanonicalPositionDiagnostics.RecordMesh("after-skinning-route", preparedSource, targetStream);

			detailStopwatch.Restart();
			var merged = merger.TryMerge(
                new CanonicalMeshSemanticMergeRequest(
                    new(request.Source.Entry.AssetKey, sourceRaw.MeshInfoIndex),
                    new(request.Target.AssetKey, targetRaw.MeshInfoIndex),
                    transform.SourceToTargetLocal!.Value),
                targetRaw,
                preparedSource);
			mergeElapsed += detailStopwatch.Elapsed;
			diagnostics.AddRange(merged.Diagnostics);
            if (!merged.IsValid || merged.Mesh is null) continue;

			detailStopwatch.Restart();
			var materialResolution = route.IsProxy
                ? new CanonicalMaterialBindingResolution([], [])
                : CanonicalMaterialBindingResolver.Resolve(request.Source.Model, sourceRaw, targetRaw);
            diagnostics.AddRange(materialResolution.Diagnostics);
            if (route.IsProxy || materialResolution.Bindings.Count == 0)
            {
                var sourceSlots = sourceRaw.Sections.Where(section => section.Triangles.Count != 0)
                    .Select(section => section.MaterialSlotId);
                var targetSlots = targetRaw.Sections.Where(section => section.Triangles.Count != 0)
                    .Select(section => section.MaterialSlotId);
                var resolvedBindings = string.Join(',', materialResolution.Bindings
                    .Select(binding => $"{binding.SectionId}:0x{binding.MaterialId:x16}"));
                materialObservations.Add(new(
                    "SameKeyMaterialRoute",
                    $"Unit=0x{request.Target.AssetKey.FileId:x16}; SourceMeshInfo={sourceRaw.MeshInfoIndex}/Lod={sourceRaw.LodIndex}; TargetMeshInfo={targetRaw.MeshInfoIndex}/Lod={targetRaw.LodIndex}; IsProxy={route.IsProxy}; SourceVisibleSlots=[{string.Join(',', sourceSlots)}]; TargetVisibleSlots=[{string.Join(',', targetSlots)}]; ResolvedBindings=[{resolvedBindings}]."));
            }
            sourceMaterialSections.AddRange(materialResolution.ResolvedSectionBindings.Select(binding => new CanonicalMaterialSectionProvenance(
                targetMesh.Index,
                binding.FinalSectionIndex,
                request.Source.Entry.AssetKey.FileId,
                binding.SourceSlotId,
                binding.PreferredTargetSlotId,
                binding.MaterialId,
                binding.UsesTargetUnitMaterialSlotLookup)));

            var finalMergedMesh = route.IsProxy
                ? ApplyTargetCullingMaterialSlots(merged.Mesh, targetRaw)
                : merged.Mesh;
			materialResolutionElapsed += detailStopwatch.Elapsed;
            CanonicalPositionDiagnostics.RecordMesh("merged", finalMergedMesh, targetStream);

            finalMeshes.Add(finalMergedMesh);
            if (route.ParticipatesInLodPalette && provisionalBone is not null)
                provisionalByMesh[targetRaw.MeshInfoIndex] = provisionalBone;
            replacedCount++;
        }

        var meshAssemblyElapsed = phaseStopwatch.Elapsed;
        phaseStopwatch.Restart();
        if (diagnostics.Count != 0)
            return new(null, replacedCount, hiddenCount, diagnostics);
        if (finalMeshes.Count != targetModel.Meshes.Count)
            return Failure("SameKeyCanonicalMeshCoverage", "Canonical same-key rebuilding did not produce one final RawMesh for every target MeshInfo.", replacedCount, hiddenCount);

        var compiledMaterialLayout = new CanonicalUnitMaterialLayoutCompiler().TryCompile(
            targetModel,
            finalMeshes,
            sourceMaterialSections);
        if (!compiledMaterialLayout.IsValid)
            return Failure(compiledMaterialLayout.Diagnostics, replacedCount, hiddenCount);
        finalMeshes = compiledMaterialLayout.Meshes.ToList();

        var provisional = new List<CanonicalLodBoneInput>();
        foreach (var raw in finalMeshes)
            if (provisionalByMesh.TryGetValue(raw.MeshInfoIndex, out var provisionalBone))
                provisional.Add(new CanonicalLodBoneInput(raw, provisionalBone));

        var rebuiltBones = targetModel.BoneInfos.Select((bone, index) => new CanonicalBoneInfoRebuild(index, bone)).ToDictionary(item => item.LodIndex);
        foreach (var group in provisional.GroupBy(item => item.Mesh.LodIndex))
        {
            var compiled = paletteCompiler.TryCompile(targetModel, group.Key, group.ToArray());
            if (!compiled.IsValid || compiled.BoneInfo is null)
                return Failure(compiled.Diagnostics, replacedCount, hiddenCount);
            rebuiltBones[group.Key] = new CanonicalBoneInfoRebuild(group.Key, compiled.BoneInfo);
            var byMesh = compiled.Meshes.ToDictionary(mesh => mesh.MeshInfoIndex);
            for (var index = 0; index < finalMeshes.Count; index++)
                if (byMesh.TryGetValue(finalMeshes[index].MeshInfoIndex, out var rewritten)) finalMeshes[index] = rewritten;
        }

        var bonePaletteElapsed = phaseStopwatch.Elapsed;
        phaseStopwatch.Restart();

        var streams = streamCompiler.TryCompile(targetModel, finalMeshes);
        if (!streams.IsValid) return Failure(streams.Diagnostics, replacedCount, hiddenCount);
        targetModel = targetModel with { Streams = streams.Streams };
        var streamContractElapsed = phaseStopwatch.Elapsed;
        phaseStopwatch.Restart();

        // Palette compilation rewrites bone-index components. Encode the final vertex
        // representation once, after both palette and stream layout are fixed.
        var preparedMeshes = new List<UnitRawMeshData>(finalMeshes.Count);
        foreach (var raw in finalMeshes)
        {
            var stream = targetModel.Streams.Single(stream => stream.Index == (int)raw.StreamIndex);
            var prepared = preparation.TryPrepare(raw, stream);
            if (!prepared.IsValid || prepared.Mesh is null)
                return Failure(prepared.Diagnostics, replacedCount, hiddenCount);
            CanonicalPositionDiagnostics.RecordMesh("prepared-final", prepared.Mesh, stream);
            preparedMeshes.Add(prepared.Mesh);
        }
        finalMeshes = preparedMeshes;
        var finalPreparationElapsed = phaseStopwatch.Elapsed;
        phaseStopwatch.Restart();

        var finalMaterialBindings = compiledMaterialLayout.Bindings;
        foreach (var mesh in finalMeshes.Where(mesh => mesh.Sections.Any(section => section.Triangles.Count != 0)))
        {
            var unboundSlots = mesh.Sections
                .Where(section => section.Triangles.Count != 0)
                .Select(section => section.MaterialSlotId)
                .Distinct()
                .Where(slot => !finalMaterialBindings.Any(binding => binding.SectionId == slot && binding.MaterialId != 0))
                .ToArray();
            if (unboundSlots.Length != 0)
            {
                var mappingDescription = mappings.TryGetValue(mesh.MeshInfoIndex, out var mapping)
                    ? $"SourceMeshInfo={mapping.SourceMeshInfoIndex}"
                    : "SourceMeshInfo=<minified-target>";
                var finalBindings = string.Join(',', finalMaterialBindings
                    .Where(binding => mesh.Sections.Any(section => section.MaterialSlotId == binding.SectionId))
                    .Select(binding => $"{binding.SectionId}:0x{binding.MaterialId:x16}"));
                materialObservations.Add(new(
                    "SameKeyVisibleMaterialBindingMissing",
                    $"Unit=0x{request.Target.AssetKey.FileId:x16}; TargetMeshInfo={mesh.MeshInfoIndex}/Lod={mesh.LodIndex}; {mappingDescription}; UnboundVisibleSlots=[{string.Join(',', unboundSlots)}]; FinalBindings=[{finalBindings}]."));
            }
        }
        if (diagnostics.Count != 0)
            return new(null, replacedCount, hiddenCount, diagnostics);

        var materialBindingsElapsed = phaseStopwatch.Elapsed;
        phaseStopwatch.Restart();
        var rebuilt = unitRebuilder.TryRebuild(targetModel, request.Target.Payload.TocData, finalMeshes, rebuiltBones.Values.ToArray(), finalMaterialBindings);
        if (!rebuilt.IsValid || rebuilt.Output is null)
            return Failure(rebuilt.Diagnostics, replacedCount, hiddenCount);
		var telemetry = new CanonicalUnitRebuildTelemetry(
			transformExpansionElapsed, meshAssemblyElapsed, streamContractElapsed, TimeSpan.Zero,
			bonePaletteElapsed, finalPreparationElapsed, materialBindingsElapsed, phaseStopwatch.Elapsed)
		{
			MeshBreakdown = new CanonicalMeshAssemblyTelemetry(routeElapsed, mergeElapsed, minifyElapsed, materialResolutionElapsed),
			SerializationBreakdown = rebuilt.SerializationTelemetry ?? CanonicalUnitSerializationTelemetry.Empty
		};
        var entry = new CanonicalPatchSessionEntry(
            request.Target.AssetKey,
            CanonicalPatchEntryOwnership.TargetOutput,
            rebuilt.Output.TocData,
            rebuilt.Output.GpuData,
            Array.Empty<byte>(),
            request.Target.Payload.Entry.Unknown1,
            request.Target.Payload.Entry.Unknown2,
            request.Target.Payload.Entry.Unknown3,
            request.Target.Payload.Entry.Unknown4);
        return new(PatchWorkspaceJobResult.Unit(entry, $"0x{request.Target.AssetKey.FileId:x16}"), replacedCount, hiddenCount, Array.Empty<CanonicalPlanDiagnostic>(), materialObservations, telemetry, rebuilt.Model);
    }

    private static UnitRawMeshData? FindRaw(UnitMeshModel model, int index, List<CanonicalPlanDiagnostic> diagnostics, string role)
    {
        var matches = model.RawMeshData.Where(raw => raw.MeshInfoIndex == index).ToArray();
        if (matches.Length != 1)
        {
            diagnostics.Add(new("RawMeshCardinality", $"The {role} Unit must contain exactly one RawMesh for MeshInfo {index}."));
            return null;
        }
        return matches[0];
    }

    private static UnitRawMeshData ApplyTargetCullingMaterialSlots(UnitRawMeshData merged, UnitRawMeshData target)
    {
        if (target.Sections.Count == 0 || merged.Sections.Count == 0)
            return merged;

        var sections = merged.Sections.Select((section, index) =>
        {
            var targetSection = target.Sections[Math.Min(index, target.Sections.Count - 1)];
            return section with { MaterialIndex = targetSection.MaterialIndex, MaterialSlotId = targetSection.MaterialSlotId };
        }).ToArray();
        return merged with { Sections = sections, Triangles = sections.SelectMany(section => section.Triangles).ToArray() };
    }

    private static SameKeyCanonicalUnitRebuildResult Failure(string code, string message, int replaced, int hidden)
        => new(null, replaced, hidden, [new(code, message)]);

    private static SameKeyCanonicalUnitRebuildResult Failure(IReadOnlyList<CanonicalPlanDiagnostic> diagnostics, int replaced, int hidden)
        => new(null, replaced, hidden, diagnostics);
}
