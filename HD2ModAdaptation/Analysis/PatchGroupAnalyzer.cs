using HD2ModAdaptation.PatchReconstruction;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;

namespace HD2ModAdaptation.Analysis;

// 作用：分别提供轻量 TOC 资产清单与完整 Unit/材质引用分析。
// Purpose: Performs the first low-cost patch-group analysis using the canonical TOC scanner.
public sealed class PatchGroupAnalyzer : IInventoryPatchGroupAnalyzer, IDependencyGraphPatchGroupAnalyzer
{
	private const string FullAnalyzerVersion = "patch-group-v5-sdk-source-eligibility";
	private const string InventoryAnalyzerVersion = "patch-group-v5-inventory";
	private const string DependencyGraphAnalyzerVersion = "patch-group-v6-dependency-graph";
	private readonly IPatchTocScanner tocScanner;
	private readonly IPatchEntryPayloadReader payloadReader;
	private readonly IUnitMaterialReferenceReader unitMaterialReader;
	private readonly StingrayMaterialReferenceReader materialReader;

	public PatchGroupAnalyzer(
		IPatchTocScanner? tocScanner = null,
		IPatchEntryPayloadReader? payloadReader = null,
		IUnitMaterialReferenceReader? unitMaterialReader = null,
		StingrayMaterialReferenceReader? materialReader = null)
	{
		this.tocScanner = tocScanner ?? new PatchTocScanner();
		this.payloadReader = payloadReader ?? new PatchEntryPayloadReader();
		this.unitMaterialReader = unitMaterialReader ?? new UnitMaterialReferenceReader();
		this.materialReader = materialReader ?? new StingrayMaterialReferenceReader();
	}

	public async ValueTask<PatchGroupAnalysis> AnalyzeAsync(PatchGroupInput input, CancellationToken cancellationToken = default)
		=> await AnalyzeCoreAsync(input, PatchAnalysisDepth.Full, cancellationToken).ConfigureAwait(false);

	public async ValueTask<PatchGroupAnalysis> AnalyzeInventoryAsync(PatchGroupInput input, CancellationToken cancellationToken = default)
		=> await AnalyzeCoreAsync(input, PatchAnalysisDepth.Inventory, cancellationToken).ConfigureAwait(false);

	public async ValueTask<PatchGroupAnalysis> AnalyzeDependencyGraphAsync(PatchGroupInput input, CancellationToken cancellationToken = default)
		=> await AnalyzeCoreAsync(input, PatchAnalysisDepth.DependencyGraph, cancellationToken).ConfigureAwait(false);

	private async ValueTask<PatchGroupAnalysis> AnalyzeCoreAsync(PatchGroupInput input, PatchAnalysisDepth depth, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(input);
		var issues = new List<PatchAnalysisIssue>();
		var assets = new List<PatchAssetFact>();
		var references = new List<PatchAssetReference>();
		var preparedSourceUnits = new List<SourceUnitPreparation>();
		IReadOnlyList<PatchTocEntry> entries = Array.Empty<PatchTocEntry>();
		var tocPath = Path.GetFullPath(input.PatchTocFilePath);
		if (!File.Exists(tocPath))
		{
			issues.Add(new PatchAnalysisIssue("MissingToc", $"Patch TOC was not found: {tocPath}", tocPath));
			return CreateResult(input, assets, references, issues);
		}

		try
		{
			entries = await tocScanner.ScanEntriesAsync(tocPath, cancellationToken).ConfigureAwait(false);
			foreach (var entry in entries)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var typeId = entry.AssetKey.TypeId;
				assets.Add(new PatchAssetFact(entry.AssetKey, entry.SourceFilePath, entry.TocDataSize, entry.StreamSize, entry.GpuResourceSize,
					typeId == PatchUnitMeshReader.UnitTypeId,
					typeId == PatchUnitMeshReader.CompositeUnitTypeId,
					typeId == MaterialDependencyResolver.MaterialTypeId,
					typeId == MaterialDependencyResolver.TextureTypeId,
					LuaAssetFacts.TryIdentify(entry.AssetKey)));
				if ((depth is PatchAnalysisDepth.DependencyGraph or PatchAnalysisDepth.Full) && entry.AssetKey.TypeId != PatchUnitMeshReader.UnitTypeId)
				{
					await ReadReferencesAsync(entry, references, issues, cancellationToken).ConfigureAwait(false);
				}
			}
			if (depth == PatchAnalysisDepth.DependencyGraph)
			{
				await ReadDirectUnitMaterialReferencesAsync(entries, references, issues, cancellationToken).ConfigureAwait(false);
			}
			else if (depth == PatchAnalysisDepth.Full)
			{
				preparedSourceUnits.AddRange(await ReadUnitMaterialReferencesAsync(entries, references, issues, cancellationToken).ConfigureAwait(false));
			}
		}
		catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException or IOException)
		{
			issues.Add(new PatchAnalysisIssue("InvalidToc", exception.Message, tocPath));
		}

		return CreateResult(input, assets, references, issues, depth, entries, preparedSourceUnits);
	}

	private async ValueTask ReadDirectUnitMaterialReferencesAsync(IReadOnlyList<PatchTocEntry> entries, ICollection<PatchAssetReference> references, ICollection<PatchAnalysisIssue> issues, CancellationToken cancellationToken)
	{
		foreach (var entry in entries.Where(entry => entry.AssetKey.TypeId == PatchUnitMeshReader.UnitTypeId))
		{
			await ReadLegacyUnitMaterialReferencesAsync(entry, references, issues, cancellationToken).ConfigureAwait(false);
		}
	}

	private async ValueTask ReadReferencesAsync(PatchTocEntry entry, ICollection<PatchAssetReference> references, ICollection<PatchAnalysisIssue> issues, CancellationToken cancellationToken)
	{
		var isUnit = entry.AssetKey.TypeId == PatchUnitMeshReader.UnitTypeId;
		var isMaterial = entry.AssetKey.TypeId == MaterialDependencyResolver.MaterialTypeId;
		var minimumPayloadLength = isUnit ? 0x74 : isMaterial ? 136 : 0;
		if (minimumPayloadLength == 0 || entry.TocDataSize < minimumPayloadLength)
		{
			return;
		}

		try
		{
			var payload = await payloadReader.ReadPayloadAsync(entry, cancellationToken).ConfigureAwait(false);
			if (!isUnit)
			{
				var textureIds = materialReader.ReadTextureIds(payload.TocData);
				for (var index = 0; index < textureIds.Count; index++)
				{
					if (textureIds[index] != 0)
					{
						references.Add(new PatchAssetReference(entry.AssetKey, new AssetKey(MaterialDependencyResolver.TextureTypeId, textureIds[index]), PatchReferenceKind.MaterialTexture, checked((uint)(136 + textureIds.Count * 4 + index * 8)), ReferenceIndex: index));
					}
				}
			}
		}
		catch (Exception exception) when (exception is IOException or InvalidDataException or EndOfStreamException or UnauthorizedAccessException or OverflowException)
		{
			issues.Add(new PatchAnalysisIssue(isUnit ? "InvalidUnitMaterialReferences" : "InvalidMaterialTextureReferences", exception.Message, entry.SourceFilePath, entry.AssetKey));
		}
	}

	private async ValueTask<IReadOnlyList<SourceUnitPreparation>> ReadUnitMaterialReferencesAsync(IReadOnlyList<PatchTocEntry> entries, ICollection<PatchAssetReference> references, ICollection<PatchAnalysisIssue> issues, CancellationToken cancellationToken)
	{
		var prepared = new List<SourceUnitPreparation>();
		var reader = new PatchUnitMeshReader(tocScanner: tocScanner);
		foreach (var entry in entries.Where(entry => entry.AssetKey.TypeId == PatchUnitMeshReader.UnitTypeId))
		{
			try
			{
				var unit = await reader.ReadAsync(entry, entries, PatchUnitDependencyPolicy.AllowExternalCompositeReference, cancellationToken).ConfigureAwait(false);
				var bindings = unit.Model.Materials
					.Where(binding => binding.MaterialId != 0)
					.GroupBy(binding => binding.SectionId)
					.ToDictionary(group => group.Key, group => group.Select(binding => binding.MaterialId).Distinct().ToArray());
				foreach (var mesh in unit.Model.Meshes)
				{
					var raw = unit.Model.RawMeshData.FirstOrDefault(candidate => candidate.MeshInfoIndex == mesh.Index);
					var isPlaceholder = raw is not null && raw.Vertices.Count <= 3 && raw.Triangles.Count <= 1;
					for (var sectionIndex = 0; sectionIndex < mesh.Sections.Count; sectionIndex++)
					{
						var section = mesh.Sections[sectionIndex];
						if (!bindings.TryGetValue(section.MaterialSlotId, out var materialIds) || materialIds.Length != 1) continue;
						references.Add(new PatchAssetReference(entry.AssetKey, new AssetKey(MaterialDependencyResolver.MaterialTypeId, materialIds[0]), PatchReferenceKind.UnitMaterial, 0, section.MaterialSlotId, sectionIndex, mesh.Index, isPlaceholder));
					}
				}
				prepared.Add(CreateSourceUnitPreparation(entry, unit));
			}
			catch (Exception exception) when (exception is IOException or InvalidDataException or EndOfStreamException or UnauthorizedAccessException or OverflowException)
			{
				await ReadLegacyUnitMaterialReferencesAsync(entry, references, issues, cancellationToken).ConfigureAwait(false);
				prepared.Add(new SourceUnitPreparation(entry, null, Array.Empty<SourceMeshPreparation>(), exception.Message));
			}
		}
		return prepared;
	}

	private static SourceUnitPreparation CreateSourceUnitPreparation(PatchTocEntry entry, PatchUnitMesh unit)
	{
		var materialIdsBySlot = unit.Model.Materials
			.Where(binding => binding.MaterialId != 0)
			.GroupBy(binding => binding.SectionId)
			.ToDictionary(group => group.Key, group => group.Select(binding => binding.MaterialId).Distinct().OrderBy(id => id).ToArray());
		var meshes = unit.Model.Meshes.Select(mesh =>
		{
			var raw = unit.Model.RawMeshData.FirstOrDefault(candidate => candidate.MeshInfoIndex == mesh.Index);
			var stream = raw is null ? null : unit.Model.Streams.FirstOrDefault(candidate => candidate.Index == raw.StreamIndex);
			var semantic = mesh.SemanticInfo;
			var isPlaceholder = raw is not null && !UnitGeometryFactsBuilder.HasRenderableGeometry(raw);
			var slots = mesh.MaterialSlotIds.Distinct().ToArray();
			var materialIds = slots.Where(materialIdsBySlot.ContainsKey).SelectMany(slot => materialIdsBySlot[slot]).Distinct().OrderBy(id => id).ToArray();
			return new SourceMeshPreparation(mesh.Index, mesh.MeshId, mesh.LodIndex, semantic.IsVisualMesh,
				raw is not null && IsSdkDefaultImportable(semantic) && !isPlaceholder,
				semantic.Name, semantic.BodyType, semantic.Slot, semantic.PieceType,
				raw is null ? 0U : checked((uint)raw.Vertices.Count), raw is null ? 0U : checked((uint)raw.Triangles.Count),
				checked((uint)mesh.Sections.Count), stream?.VertexStride ?? 0, slots, materialIds);
		}).ToArray();
		return new SourceUnitPreparation(entry, unit.Model.CompositeRef == 0 ? null : new AssetKey(PatchUnitMeshReader.CompositeUnitTypeId, unit.Model.CompositeRef), meshes);
	}

	// Mirrors the community SDK CreateModel defaults for this armor workflow after
	// UnitMeshReader attaches raw mesh semantics. The SDK also imports some -1 meshes
	// when they are not culling bodies, but cross-armor source selection requires a
	// real default-imported LOD0 so a physics/placeholder proxy cannot become source.
	private static bool IsSdkDefaultImportable(UnitMeshSemanticInfo semantic)
		=> semantic.IsVisualMesh && semantic.LodIndex == 0 && !semantic.IsLod && !semantic.IsCullingBody && !semantic.IsStaticMesh;

	private async ValueTask ReadLegacyUnitMaterialReferencesAsync(PatchTocEntry entry, ICollection<PatchAssetReference> references, ICollection<PatchAnalysisIssue> issues, CancellationToken cancellationToken)
	{
		try
		{
			var payload = await payloadReader.ReadPayloadAsync(entry, cancellationToken).ConfigureAwait(false);
			if (payload.TocData.Length < 0x74)
			{
				return;
			}
			foreach (var binding in unitMaterialReader.ReadReferenceBindings(payload.TocData).Where(binding => binding.MaterialId != 0))
			{
				references.Add(new PatchAssetReference(entry.AssetKey, new AssetKey(MaterialDependencyResolver.MaterialTypeId, binding.MaterialId), PatchReferenceKind.UnitMaterial, binding.MaterialIdPayloadRelativeOffset, binding.SectionId));
			}
		}
		catch (Exception exception) when (exception is IOException or InvalidDataException or EndOfStreamException or UnauthorizedAccessException or OverflowException)
		{
			issues.Add(new PatchAnalysisIssue("InvalidUnitMaterialReferences", exception.Message, entry.SourceFilePath, entry.AssetKey));
		}
	}

	private static PatchGroupAnalysis CreateResult(PatchGroupInput input, IReadOnlyList<PatchAssetFact> assets, IReadOnlyList<PatchAssetReference> references, IReadOnlyList<PatchAnalysisIssue> issues, PatchAnalysisDepth depth = PatchAnalysisDepth.Full, IReadOnlyList<PatchTocEntry>? entries = null, IReadOnlyList<SourceUnitPreparation>? sourceUnits = null)
		=> new(input, assets, references, issues, DateTimeOffset.UtcNow, depth switch
		{
			PatchAnalysisDepth.Inventory => InventoryAnalyzerVersion,
			PatchAnalysisDepth.DependencyGraph => DependencyGraphAnalyzerVersion,
			_ => FullAnalyzerVersion
		}, depth, entries, sourceUnits);
}
