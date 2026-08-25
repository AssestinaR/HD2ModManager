namespace HD2ModAdaptation.PatchReconstruction;

public sealed record CanonicalUnitMaterialSectionFact(
	int MeshInfoIndex,
	int SectionIndex,
	uint SourceMaterialSlot,
	uint OutputMaterialSlot,
	IReadOnlyList<ulong> SourceMaterialIds,
	IReadOnlyList<ulong> OutputMaterialIds,
	bool MaterialIdentityPreserved,
	string? Diagnostic);

public sealed record CanonicalUnitMaterialGraphFact(
	AssetKey Unit,
	bool SourceReadable,
	bool OutputReadable,
	IReadOnlyList<CanonicalUnitMaterialSectionFact> Sections,
	IReadOnlyList<string> Diagnostics);

public sealed record CanonicalUnitMaterialGraphReport(
	int ComparedUnitCount,
	int ReadFailureCount,
	int MismatchedSectionCount,
	IReadOnlyList<CanonicalUnitMaterialGraphFact> Units)
{
	public bool IsConsistent => ReadFailureCount == 0 && MismatchedSectionCount == 0;
}

/// <summary>
/// Compares the Unit -> MeshInfo Section -> MaterialSlot -> Material AssetKey graph
/// before and after Canonical rebuilding. It does not rewrite any payload.
/// </summary>
public sealed class CanonicalUnitMaterialGraphAnalyzer
{
	private readonly IPatchEntryPayloadReader payloadReader;
	private readonly UnitMesh.UnitMeshReader meshReader = new();

	public CanonicalUnitMaterialGraphAnalyzer(IPatchEntryPayloadReader? payloadReader = null)
		=> this.payloadReader = payloadReader ?? new PatchEntryPayloadReader();

	public async ValueTask<CanonicalUnitMaterialGraphReport> AnalyzeAsync(
		IReadOnlyList<PatchTocEntry> sourceEntries,
		IReadOnlyList<PatchTocEntry> outputEntries,
		CancellationToken cancellationToken = default)
	{
		var sourceUnits = sourceEntries.Where(entry => entry.AssetKey.TypeId == UnitMesh.PatchUnitMeshReader.UnitTypeId).GroupBy(entry => entry.AssetKey).ToDictionary(group => group.Key, group => group.Last());
		var outputUnits = outputEntries.Where(entry => entry.AssetKey.TypeId == UnitMesh.PatchUnitMeshReader.UnitTypeId).GroupBy(entry => entry.AssetKey).ToDictionary(group => group.Key, group => group.Last());
		var facts = new List<CanonicalUnitMaterialGraphFact>();
		var readFailures = 0;
		var mismatches = 0;
		foreach (var unitKey in sourceUnits.Keys.Intersect(outputUnits.Keys).OrderBy(key => key.TypeId).ThenBy(key => key.FileId))
		{
			UnitMesh.UnitMeshModel? source = null;
			UnitMesh.UnitMeshModel? output = null;
			var diagnostics = new List<string>();
			try { source = await ReadAsync(sourceUnits[unitKey], cancellationToken).ConfigureAwait(false); }
			catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException or IOException) { diagnostics.Add($"来源 Unit 读取失败：{exception.Message}"); }
			try { output = await ReadAsync(outputUnits[unitKey], cancellationToken).ConfigureAwait(false); }
			catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException or IOException) { diagnostics.Add($"输出 Unit 读取失败：{exception.Message}"); }
			if (source is null || output is null)
			{
				readFailures++;
				facts.Add(new(unitKey, source is not null, output is not null, [], diagnostics));
				continue;
			}

			var sections = new List<CanonicalUnitMaterialSectionFact>();
			var sourceMeshes = source.Meshes.ToDictionary(mesh => mesh.Index);
			var outputMeshes = output.Meshes.ToDictionary(mesh => mesh.Index);
			foreach (var meshIndex in sourceMeshes.Keys.Union(outputMeshes.Keys).OrderBy(index => index))
			{
				if (!sourceMeshes.TryGetValue(meshIndex, out var sourceMesh) || !outputMeshes.TryGetValue(meshIndex, out var outputMesh))
				{
					mismatches++;
					diagnostics.Add($"MeshInfo {meshIndex} 只存在于 {(sourceMeshes.ContainsKey(meshIndex) ? "来源" : "输出")} Unit。");
					continue;
				}
				var sectionCount = Math.Max(sourceMesh.Sections.Count, outputMesh.Sections.Count);
				for (var sectionIndex = 0; sectionIndex < sectionCount; sectionIndex++)
				{
					var sourceSection = sectionIndex < sourceMesh.Sections.Count ? sourceMesh.Sections[sectionIndex] : null;
					var outputSection = sectionIndex < outputMesh.Sections.Count ? outputMesh.Sections[sectionIndex] : null;
					var sourceSlot = sourceSection?.MaterialSlotId ?? 0;
					var outputSlot = outputSection?.MaterialSlotId ?? 0;
					var sourceIds = sourceSection is null ? [] : MaterialIdsForSlot(source.Materials, sourceSlot);
					var outputIds = outputSection is null ? [] : MaterialIdsForSlot(output.Materials, outputSlot);
					var preserved = sourceSection is not null
						&& outputSection is not null
						&& sourceIds.Distinct().SequenceEqual(outputIds.Distinct());
					string? diagnostic = null;
					if (sourceSection is null || outputSection is null) diagnostic = "Section 数量或位置发生变化";
					else if (!preserved) diagnostic = $"Material AssetKey 不一致：source=[{FormatIds(sourceIds.Distinct())}], output=[{FormatIds(outputIds.Distinct())}]";
					if (diagnostic is not null) mismatches++;
					sections.Add(new(meshIndex, sectionIndex, sourceSlot, outputSlot, sourceIds, outputIds, preserved, diagnostic));
				}
			}
			facts.Add(new(unitKey, true, true, sections, diagnostics));
		}
		return new(facts.Count, readFailures, mismatches, facts);
	}

	private async ValueTask<UnitMesh.UnitMeshModel> ReadAsync(PatchTocEntry entry, CancellationToken cancellationToken)
	{
		var payload = await payloadReader.ReadPayloadAsync(entry, cancellationToken).ConfigureAwait(false);
		return meshReader.Read(payload.TocData, payload.GpuResourceData, options: new UnitMesh.UnitMeshReadOptions(PreserveVertexData: false, PreserveComponentRawData: false, BuildTriangleMirror: false));
	}

	private static IReadOnlyList<ulong> MaterialIdsForSlot(IReadOnlyList<UnitMesh.UnitMaterialBinding> bindings, uint slot)
		=> bindings.Where(binding => binding.SectionId == slot).Select(binding => binding.MaterialId).ToArray();

	private static string FormatIds(IEnumerable<ulong> ids) => string.Join(',', ids.Select(id => $"0x{id:x16}"));
}
