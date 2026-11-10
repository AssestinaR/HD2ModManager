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

/// <summary>
/// 追加之后「最终第 i 段来自哪个来源的第 j 段」。
/// <para>
/// <b>拆件器靠它把 donor 的几何翻译成组员</b>：一个组就是"某个 donor 贡献的那些段"。
/// <see cref="InputIndex"/> 是<b>本次调用</b> <c>inputsByTargetMesh</c> 里的 input 下标（同一个
/// 目标 unit 的各级 LOD 用同一套 input 顺序）。
/// </para>
/// </summary>
public sealed record CanonicalAppendedSection(int MeshInfoIndex, int FinalSectionIndex, int InputIndex, int SourceSectionIndex);

public sealed record CanonicalDecorationAppendResult(PatchUnitMeshEditResult? Edit, IReadOnlyList<CanonicalPlanDiagnostic> Diagnostics)
{
    public bool IsValid => Edit is not null && Diagnostics.Count == 0;

    /// <summary>
    /// 段来源账：<b>最终网格里的第几段</b>来自哪个输入的第几段。
    /// <para>
    /// <b>只列真正进了最终网格的段。</b>0 三角形段（没有材质）与同槽合并掉的段不会出现在这里——
    /// 它们本来就没有几何。所以这份账是"部分账"，调用方必须容忍某一段查不到来源。
    /// </para>
    /// <para>
    /// <c>uniqueSlotPerSection: true</c> 时按槽 id 反查最终下标（<see cref="ResolveOriginsBySlot"/>）；
    /// 其余三条在役路径行为不变：段数一致才给整份账，不一致留空（它们不读这份账）。
    /// </para>
    /// </summary>
    public IReadOnlyList<CanonicalAppendedSection> Origins { get; init; } = [];
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
		CanonicalCullingBoundsMode cullingBounds = CanonicalCullingBoundsMode.Preserve,
		bool uniqueSlotPerSection = false)
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
		// 临时段下标 → 材质槽 id。段数会因"0 三角形段被丢掉 / 同槽段被合并"而变，
		// 所以最终下标必须靠槽 id 反查，不能靠段数对齐（见文件末尾的段账说明）。
		var slotsByMesh = new Dictionary<int, IReadOnlyDictionary<int, uint>>();
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
			var slotByProvisionalIndex = new Dictionary<int, uint>();
			for (var index = 0; index < merged.Sections.Count; index++)
				slotByProvisionalIndex[index] = merged.Sections[index].MaterialSlotId;
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
				var sourceBindings = BuildMaterialProvenance(source, input.Fragment.RawMesh, group.TargetRaw, start, group.TargetMesh.Index, input.MaterialNamespace, diagnostics, uniqueSlotPerSection);
				if (diagnostics.Count != 0) return new(null, diagnostics);
				materialProvenance.AddRange(sourceBindings);
				foreach (var binding in sourceBindings)
					slotByProvisionalIndex[binding.SectionIndex] = binding.PreferredTargetSlotId;
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
			slotsByMesh.Add(group.TargetMesh.Index, slotByProvisionalIndex);
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

        // 段来源账。**按槽 id 反查最终下标**，不按段数对齐：
        // ① 材质布局会丢掉 0 三角形段（legacy unit 的第 0 段就是这种），
        // ② 按槽合并会把多段并成一段。两种都是**正常现象**，按段数判"账可不可信"
        //    等于永远不可信——实测拆件那条路 11 个 target 全部留空，变体组因此没有成员。
        // 现在只记"确实进了最终网格"的段：查不到槽的段直接不记（它本来就没有几何）。
        var appendedSections = new List<CanonicalAppendedSection>();
        var originsTrustworthy = true;
        foreach (var group in groups)
        {
            var meshInfoIndex = group.TargetMesh.Index;
            var origins = originsByMesh[meshInfoIndex];
            if (!finalByMesh.TryGetValue(meshInfoIndex, out var finalMesh))
                continue;

            if (uniqueSlotPerSection)
            {
                appendedSections.AddRange(ResolveOriginsBySlot(
                    meshInfoIndex, origins, slotsByMesh[meshInfoIndex], finalMesh.Sections));
                continue;
            }

            // 其余三条在役路径不变：段数一致才给账，不一致保持留空（它们不读这份账）。
            if (finalMesh.Sections.Count != origins.Count)
            {
                originsTrustworthy = false;
                break;
            }

            foreach (var origin in origins)
            {
                appendedSections.Add(new CanonicalAppendedSection(
                    meshInfoIndex,
                    origin.FinalSectionIndex,
                    origin.SourceIndex,
                    origin.SourceSectionIndex));
            }
        }

        return new(new PatchUnitMeshEditResult(targetUnit.Entry, targetUnit.Payload, rebuilt.Output!.TocData, rebuilt.Output.GpuData,
            ReplacementMaterialIds: material.Bindings.Select(binding => binding.MaterialId).Distinct().ToArray()), [])
        {
            Origins = originsTrustworthy ? appendedSections : [],
        };
    }

    /// <summary>
    /// 把"临时段下标"翻译成"最终段下标"，用 **材质槽 id** 对齐。
    /// <para>
    /// 每段在最终网格里都带着自己的槽 id（<see cref="UniqueSlotFor"/> 保证互不相同），
    /// 所以"这一段进了最终网格的哪一格"可以精确回答；被丢掉的段（0 三角形、材质没绑上）
    /// 没有终段——它们本来就没有几何，不进账是正确的，而不是"账不可信"。
    /// </para>
    /// <para>
    /// 同槽合并时只记第一次：那意味着多段真的并成了一段，记多次会让下游以为一块几何能单独开关。
    /// </para>
    /// </summary>
    public static IReadOnlyList<CanonicalAppendedSection> ResolveOriginsBySlot(
        int meshInfoIndex,
        IReadOnlyList<CanonicalAppendSectionOrigin> origins,
        IReadOnlyDictionary<int, uint> slotByProvisionalIndex,
        IReadOnlyList<UnitRawMeshSectionData> finalSections)
    {
        ArgumentNullException.ThrowIfNull(origins);
        ArgumentNullException.ThrowIfNull(slotByProvisionalIndex);
        ArgumentNullException.ThrowIfNull(finalSections);

        var finalIndexBySlot = new Dictionary<uint, int>();
        for (var index = 0; index < finalSections.Count; index++)
            finalIndexBySlot.TryAdd(finalSections[index].MaterialSlotId, index);

        var resolved = new List<CanonicalAppendedSection>(origins.Count);
        var claimed = new HashSet<int>();
        foreach (var origin in origins)
        {
            if (!slotByProvisionalIndex.TryGetValue(origin.FinalSectionIndex, out var slot))
                continue;
            if (!finalIndexBySlot.TryGetValue(slot, out var finalIndex))
                continue;
            if (!claimed.Add(finalIndex))
                continue;
            resolved.Add(new CanonicalAppendedSection(meshInfoIndex, finalIndex, origin.SourceIndex, origin.SourceSectionIndex));
        }

        return resolved;
    }

    private static IReadOnlyList<CanonicalMaterialSectionProvenance> BuildMaterialProvenance(
        UnitMeshModel source, UnitRawMeshData sourceRaw, UnitRawMeshData targetRaw, int finalSectionStart,
        int targetMeshInfoIndex, ulong materialNamespace, List<CanonicalPlanDiagnostic> diagnostics,
        bool uniqueSlotPerSection = false)
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
            if (uniqueSlotPerSection)
            {
                // 每段独占一个槽：段边界才保得住。
                // 材质槽分配器在 UsesTargetUnitMaterialSlotLookup=true 时会拿"目标该材质的第 k 个槽"
                // 复用（occurrence 语义），于是 donor 的段会并进目标那一段 —— 那正是"两块几何
                // 同生共死"的来源。改走普通来源路径 + 一个每段唯一的 PreferredTargetSlotId，
                // 槽就不再复用（多个槽指向同一个 MaterialId 是合法的，产品代码本来就会产出这种表）。
                result.Add(new(targetMeshInfoIndex, finalSectionStart + sectionIndex, materialNamespace,
                    binding.SourceSlotId, UniqueSlotFor(finalSectionStart + sectionIndex, binding.MaterialId),
                    binding.MaterialId,
                    UsesTargetUnitMaterialSlotLookup: false, PreferMeshLocalSlotReuse: false));
                continue;
            }

            result.Add(new(targetMeshInfoIndex, finalSectionStart + sectionIndex, materialNamespace,
                binding.SourceSlotId, binding.PreferredTargetSlotId, binding.MaterialId,
                binding.UsesTargetUnitMaterialSlotLookup, PreferMeshLocalSlotReuse: true));
        }
        return result;
    }

	/// <summary>
	/// 按 (最终段下标, 材质 id) 算一个稳定的槽 id。
	/// <para>
	/// 只要"同一份产物里每段不同、重跑一次还是同一个"就够 —— 它不需要好看，
	/// 也不需要跨构建稳定（换甲会整体重建）。碰撞由材质槽分配器的 occupied 兜底。
	/// </para>
	/// <para>
	/// <b>public 是刻意的</b>：拆件器（Core）要复用它，测试也要直接钉住它的性质。
	/// </para>
	/// </summary>
	public static uint UniqueSlotFor(int finalSectionIndex, ulong materialId)
	{
		ulong hash = 14695981039346656037UL;
		void Mix(ulong value)
		{
			for (var index = 0; index < sizeof(ulong); index++)
			{
				hash ^= (byte)(value >> (index * 8));
				hash *= 1099511628211UL;
			}
		}

		Mix(unchecked((uint)finalSectionIndex));
		Mix(materialId);
		var slot = unchecked((uint)(hash ^ (hash >> 32)));
		// 0 与默认材质槽（155175220，见 CanonicalUnitMaterialLayoutCompiler.DefaultMaterialSlotId）
		// 都是保留值，撞上会让"这一段没有材质"。
		return slot == 0 || slot == 155175220u ? 1u : slot;
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
