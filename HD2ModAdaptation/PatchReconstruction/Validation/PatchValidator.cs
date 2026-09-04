using HD2ModAdaptation.PatchReconstruction.UnitMesh;

namespace HD2ModAdaptation.PatchReconstruction.Validation;

// Purpose: Provides a detachable, read-only validation tool for Patch files and generated Unit payloads.
public sealed class PatchValidator : IPatchValidator
{
	private readonly IPatchTocScanner scanner;
	private readonly IPatchEntryPayloadReader payloadReader;
	private readonly PatchUnitMeshReader unitReader;

	public PatchValidator(
		IPatchTocScanner? scanner = null,
		IPatchEntryPayloadReader? payloadReader = null,
		PatchUnitMeshReader? unitReader = null)
	{
		this.scanner = scanner ?? new PatchTocScanner();
		this.payloadReader = payloadReader ?? new PatchEntryPayloadReader();
		this.unitReader = unitReader ?? new PatchUnitMeshReader(payloadReader: this.payloadReader, tocScanner: this.scanner);
	}

	public async ValueTask<PatchValidationResult> ValidateAsync(
		string patchTocFilePath,
		PatchValidationOptions? options = null,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(patchTocFilePath);
		options ??= new PatchValidationOptions();
		var fullPath = Path.GetFullPath(patchTocFilePath);
		var issues = new List<PatchValidationIssue>();
		var entries = Array.Empty<PatchTocEntry>();
		if (!File.Exists(fullPath))
		{
			issues.Add(Error("MissingToc", $"Patch TOC was not found: {fullPath}.", filePath: fullPath));
			return Result(fullPath, entries, issues, 0, 0);
		}

		try
		{
			entries = (await scanner.ScanEntriesAsync(fullPath, cancellationToken).ConfigureAwait(false)).ToArray();
		}
		catch (Exception exception) when (exception is IOException or InvalidDataException or EndOfStreamException or UnauthorizedAccessException)
		{
			issues.Add(Error("InvalidToc", exception.Message, filePath: fullPath, exception: exception));
			return Result(fullPath, entries, issues, 0, 0);
		}

		ValidateEntryIdentity(entries, issues);
		AppendEmptyStreamCompatibilityWarning(entries, fullPath, issues);
		ValidateSidecars(fullPath, entries, issues);
		var sourceGeometry = await PrepareSourceGeometryValidationAsync(entries, fullPath, options, issues, cancellationToken).ConfigureAwait(false);
		var unitsChecked = 0;
		var unitsReadable = 0;
		if (options.ReadUnitPayloads)
		{
			foreach (var entry in entries.Where(entry => entry.AssetKey.TypeId == PatchUnitMeshReader.UnitTypeId))
			{
				cancellationToken.ThrowIfCancellationRequested();
				unitsChecked++;
				try
				{
					var unit = await unitReader.ReadAsync(entry, entries,
						options.RequirePatchLocalComposite
							? PatchUnitDependencyPolicy.RequirePatchLocalComposite
							: PatchUnitDependencyPolicy.AllowExternalCompositeReference,
						cancellationToken).ConfigureAwait(false);
					unitsReadable++;
					ValidateUnit(entry, unit, options, issues);
					if (options.RequirePatchLocalBone && unit.Dependencies?.HasUnresolvedExternalBone == true)
						issues.Add(Error("MissingLocalBone", $"Unit references bone asset 0x{unit.Dependencies.BonesReference:x16}, but the bone entry is not present in this Patch.", entry.AssetKey, fullPath));
					if (sourceGeometry?.EntriesByKey.TryGetValue(entry.AssetKey, out var sourceEntry) == true)
						await ValidateSourceGeometryForUnitAsync(sourceEntry, sourceGeometry.Entries, unit, fullPath, options, issues, cancellationToken).ConfigureAwait(false);
				}
				catch (Exception exception) when (exception is IOException or InvalidDataException or EndOfStreamException or UnauthorizedAccessException or KeyNotFoundException)
				{
					issues.Add(Error("UnitReadbackFailed", $"Unit 0x{entry.AssetKey.FileId:x16} could not be decoded: {exception.Message}", entry.AssetKey, fullPath, exception));
				}
			}
		}
		else if (sourceGeometry is not null)
		{
			foreach (var sourceEntry in sourceGeometry.EntriesByKey.Values)
			{
				var outputEntry = entries.SingleOrDefault(entry => entry.AssetKey == sourceEntry.AssetKey);
				if (outputEntry is null) continue;
				try
				{
					var output = await unitReader.ReadAsync(outputEntry, entries, PatchUnitDependencyPolicy.AllowExternalCompositeReference, cancellationToken).ConfigureAwait(false);
					await ValidateSourceGeometryForUnitAsync(sourceEntry, sourceGeometry.Entries, output, fullPath, options, issues, cancellationToken).ConfigureAwait(false);
				}
				catch (Exception exception) when (exception is IOException or InvalidDataException or EndOfStreamException or UnauthorizedAccessException or KeyNotFoundException)
				{
					issues.Add(GeometryIssue(options, "SourceGeometryComparisonFailed", $"Geometry comparison for Unit 0x{sourceEntry.AssetKey.FileId:x16} failed: {exception.Message}", sourceEntry.AssetKey, fullPath, exception));
				}
			}
		}
		return Result(fullPath, entries, issues, unitsChecked, unitsReadable);
	}

	private static void ValidateEntryIdentity(IReadOnlyList<PatchTocEntry> entries, ICollection<PatchValidationIssue> issues)
	{
		foreach (var duplicate in entries.GroupBy(entry => entry.AssetKey).Where(group => group.Count() > 1))
			issues.Add(Error("DuplicateAssetKey", $"Asset key 0x{duplicate.Key.TypeId:x16}:0x{duplicate.Key.FileId:x16} occurs {duplicate.Count()} times.", duplicate.Key));
	}

	private static void ValidateSidecars(string tocPath, IReadOnlyList<PatchTocEntry> entries, ICollection<PatchValidationIssue> issues)
	{
		var tocLength = new FileInfo(tocPath).Length;
		var streamPath = tocPath + ".stream";
		var gpuPath = tocPath + ".gpu_resources";
		ValidateSidecar(tocPath, tocLength, entries, static entry => ((ulong)entry.TocDataOffset, (ulong)entry.TocDataSize), "TocRange", issues);
		ValidateSidecar(streamPath, File.Exists(streamPath) ? new FileInfo(streamPath).Length : 0, entries, static entry => (entry.StreamOffset, entry.StreamSize), "StreamRange", issues);
		ValidateSidecar(gpuPath, File.Exists(gpuPath) ? new FileInfo(gpuPath).Length : 0, entries, static entry => (entry.GpuResourceOffset, entry.GpuResourceSize), "GpuRange", issues);
	}

	private static void ValidateSidecar(string path, long length, IReadOnlyList<PatchTocEntry> entries,
		Func<PatchTocEntry, (ulong Offset, ulong Size)> range, string code, ICollection<PatchValidationIssue> issues)
	{
		foreach (var entry in entries)
		{
			var (offset, size) = range(entry);
			if (size == 0) continue;
			if (!File.Exists(path))
			{
				issues.Add(Error("MissingSidecar", $"Entry 0x{entry.AssetKey.FileId:x16} requires sidecar '{Path.GetFileName(path)}', but it is missing.", entry.AssetKey, path));
				continue;
			}
			if (offset > (ulong)length || size > (ulong)length - offset)
				issues.Add(Error(code, $"Entry 0x{entry.AssetKey.FileId:x16} range [{offset}, {offset + size}) exceeds '{Path.GetFileName(path)}' length {length}.", entry.AssetKey, path));
		}
	}

	private static void ValidateUnit(PatchTocEntry entry, PatchUnitMesh unit, PatchValidationOptions options, ICollection<PatchValidationIssue> issues)
	{
		var model = unit.Model;
		if (options.ExpectedUnitVersion is uint expected && model.Version != expected)
		{
			var severity = options.TreatOutdatedUnitVersionAsError ? PatchValidationSeverity.Error : PatchValidationSeverity.Warning;
			issues.Add(new(severity, "OutdatedUnitVersion", $"Unit version {model.Version} does not match expected current version {expected}.", entry.AssetKey, entry.SourceFilePath));
		}
		foreach (var (stream, index) in model.Streams.Select((stream, index) => (stream, index)))
		{
			if (stream.VertexBufferSize == 0 || stream.IndexBufferSize == 0)
				issues.Add(Warning("EmptyStreamBuffer", $"Unit stream {index} has an empty vertex or index buffer.", entry.AssetKey, entry.SourceFilePath));
		}
		if (options.ReportEmptyUnitGeometry && model.Meshes.Count > 0 && model.RawMeshData.All(mesh => mesh.Triangles.Count == 0))
			issues.Add(Warning("UnitHasNoDecodedGeometry", "Unit was structurally decoded but no triangle geometry could be read from its GPU payload.", entry.AssetKey, entry.SourceFilePath));
		if (options.RequireFiniteVisiblePositions)
			ValidateFinitePositions(entry, model, issues);
		if (options.RequireBoundVisibleMaterialSlots)
			ValidateVisibleMaterialBindings(entry, model, issues);
	}

	private async ValueTask<SourceGeometryValidationContext?> PrepareSourceGeometryValidationAsync(
		IReadOnlyList<PatchTocEntry> outputEntries,
		string outputPath,
		PatchValidationOptions options,
		ICollection<PatchValidationIssue> issues,
		CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(options.SourcePatchTocFilePath)) return null;
		var sourcePath = Path.GetFullPath(options.SourcePatchTocFilePath!);
		IReadOnlyList<PatchTocEntry> sourceEntries;
		try
		{
			sourceEntries = await scanner.ScanEntriesAsync(sourcePath, cancellationToken).ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is IOException or InvalidDataException or EndOfStreamException or UnauthorizedAccessException)
		{
			issues.Add(Error("SourcePatchUnreadable", $"Source Patch could not be scanned for geometry comparison: {exception.Message}", filePath: sourcePath, exception: exception));
			return null;
		}

		var entriesToPreserve = sourceEntries.Where(entry =>
			entry.AssetKey.TypeId == PatchUnitMeshReader.UnitTypeId &&
			(options.SourceGeometryPreservationUnitKeys is null || options.SourceGeometryPreservationUnitKeys.Contains(entry.AssetKey)))
			.ToArray();
		var outputKeys = outputEntries.Select(entry => entry.AssetKey).ToHashSet();
		foreach (var sourceEntry in entriesToPreserve.Where(entry => !outputKeys.Contains(entry.AssetKey)))
			issues.Add(GeometryIssue(options, "SourceUnitMissing", $"Output Patch is missing source Unit 0x{sourceEntry.AssetKey.FileId:x16}.", sourceEntry.AssetKey, outputPath));
		return new(sourceEntries, entriesToPreserve.ToDictionary(entry => entry.AssetKey));
	}

	private static void AppendEmptyStreamCompatibilityWarning(
		IReadOnlyList<PatchTocEntry> entries,
		string tocPath,
		ICollection<PatchValidationIssue> issues)
	{
		var sentinel = PatchStreamCompatibility.GetEmptyStreamSentinel(entries);
		if (!sentinel.HasValue) return;
		issues.Add(Warning(
			"NonStandardEmptyStreamSentinel",
			$"Patch 使用空或省略的 .stream 与统一 StreamSize 占位值 0x{sentinel.Value:x8}；已按无 Stream 资源兼容读取。",
			filePath: tocPath));
	}

	private async ValueTask ValidateSourceGeometryForUnitAsync(
		PatchTocEntry sourceEntry,
		IReadOnlyList<PatchTocEntry> sourceEntries,
		PatchUnitMesh output,
		string outputPath,
		PatchValidationOptions options,
		ICollection<PatchValidationIssue> issues,
		CancellationToken cancellationToken)
	{
		try
		{
			var source = await unitReader.ReadAsync(sourceEntry, sourceEntries, PatchUnitDependencyPolicy.AllowExternalCompositeReference, cancellationToken).ConfigureAwait(false);
			var meshInfoMappings = options.SourceGeometryMeshInfoMappings is not null
				&& options.SourceGeometryMeshInfoMappings.TryGetValue(sourceEntry.AssetKey, out var mappedMeshInfoIndices)
				? mappedMeshInfoIndices
				: null;
			foreach (var sourceMesh in source.Model.RawMeshData.Where(IsVisibleGeometry))
			{
				IReadOnlyCollection<int>? outputMeshInfoIndices = meshInfoMappings is null
					? [sourceMesh.MeshInfoIndex]
					: meshInfoMappings.TryGetValue(sourceMesh.MeshInfoIndex, out var mappedOutputMeshInfoIndices)
						? mappedOutputMeshInfoIndices
						: null;
				if (outputMeshInfoIndices is null) continue;
				foreach (var outputMeshInfoIndex in outputMeshInfoIndices)
				{
					var outputMesh = output.Model.RawMeshData.SingleOrDefault(mesh => mesh.MeshInfoIndex == outputMeshInfoIndex);
					if (outputMesh is null)
					{
						issues.Add(GeometryIssue(options, "SourceMeshMissing", $"Output Unit is missing source MeshInfo {sourceMesh.MeshInfoIndex}.", sourceEntry.AssetKey, outputPath));
						continue;
					}
					var outputMeshInfo = output.Model.Meshes.SingleOrDefault(mesh => mesh.Index == outputMesh.MeshInfoIndex);
					var outputIsCullingBody = outputMeshInfo?.SemanticInfo.IsCullingBody == true;
					if (!outputIsCullingBody && HasConcreteMaterialBinding(source.Model, sourceMesh) && !HasConcreteMaterialBinding(output.Model, outputMesh))
						issues.Add(GeometryIssue(options, "SourceVisibleMaterialBindingMissing", $"Output MeshInfo {outputMesh.MeshInfoIndex} has visible source geometry but no concrete Unit material binding.", sourceEntry.AssetKey, outputPath));
					if (!IsVisibleGeometry(outputMesh))
					{
						issues.Add(GeometryIssue(options, "SourceGeometryMinified", $"Output MeshInfo {sourceMesh.MeshInfoIndex} is minified or has no visible triangles; source has {sourceMesh.Vertices.Count} vertices and {sourceMesh.Triangles.Count} triangles.", sourceEntry.AssetKey, outputPath));
						continue;
					}
					var sourceReferencedVertexCount = CountReferencedVertices(sourceMesh);
					var outputReferencedVertexCount = CountReferencedVertices(outputMesh);
					if (sourceReferencedVertexCount != outputReferencedVertexCount || sourceMesh.Triangles.Count != outputMesh.Triangles.Count)
						issues.Add(GeometryIssue(options, "SourceGeometryCountMismatch", $"Output MeshInfo {outputMesh.MeshInfoIndex} has {outputReferencedVertexCount} referenced vertices/{outputMesh.Triangles.Count} triangles ({outputMesh.Vertices.Count} stored vertices); source has {sourceReferencedVertexCount}/{sourceMesh.Triangles.Count} ({sourceMesh.Vertices.Count} stored vertices).", sourceEntry.AssetKey, outputPath));
				}
			}
		}
		catch (Exception exception) when (exception is IOException or InvalidDataException or EndOfStreamException or UnauthorizedAccessException or KeyNotFoundException)
		{
			issues.Add(GeometryIssue(options, "SourceGeometryComparisonFailed", $"Geometry comparison for Unit 0x{sourceEntry.AssetKey.FileId:x16} failed: {exception.Message}", sourceEntry.AssetKey, outputPath, exception));
		}
	}

	private sealed record SourceGeometryValidationContext(
		IReadOnlyList<PatchTocEntry> Entries,
		IReadOnlyDictionary<AssetKey, PatchTocEntry> EntriesByKey);

	private static bool IsVisibleGeometry(UnitRawMeshData mesh) => UnitGeometryFactsBuilder.HasRenderableGeometry(mesh);

	private static int CountReferencedVertices(UnitRawMeshData mesh)
		=> mesh.Sections
			.SelectMany(section => section.Triangles)
			.SelectMany(triangle => new[] { triangle.A, triangle.B, triangle.C })
			.Distinct()
			.Count();

	private static bool HasConcreteMaterialBinding(UnitMeshModel model, UnitRawMeshData raw)
	{
		var slots = raw.Sections.Where(section => section.Triangles.Count != 0).Select(section => section.MaterialSlotId).Distinct();
		return slots.All(slot => model.Materials.Any(binding => binding.SectionId == slot && binding.MaterialId != 0));
	}

	private static void ValidateFinitePositions(PatchTocEntry entry, UnitMeshModel model, ICollection<PatchValidationIssue> issues)
	{
		foreach (var mesh in model.RawMeshData.Where(IsVisibleGeometry))
		{
			var positions = mesh.Vertices.SelectMany(vertex => vertex.Components)
				.Where(component => component.Type == 0)
				.SelectMany(component => component.FloatValues)
				.ToArray();
			if (positions.Length < mesh.Vertices.Count * 3 || positions.Any(value => !float.IsFinite(value)))
				issues.Add(Error("InvalidVisiblePositions", $"Visible MeshInfo {mesh.MeshInfoIndex} has missing or non-finite decoded position values.", entry.AssetKey, entry.SourceFilePath));
		}
	}

	private static void ValidateVisibleMaterialBindings(PatchTocEntry entry, UnitMeshModel model, ICollection<PatchValidationIssue> issues)
	{
		foreach (var raw in model.RawMeshData.Where(IsVisibleGeometry))
		{
			var mesh = model.Meshes.SingleOrDefault(candidate => candidate.Index == raw.MeshInfoIndex);
			if (mesh is null)
			{
				issues.Add(Error("VisibleMeshInfoMissing", $"Visible RawMesh {raw.MeshInfoIndex} has no MeshInfo record.", entry.AssetKey, entry.SourceFilePath));
				continue;
			}
			if (mesh.SemanticInfo.IsCullingBody)
				continue;
			foreach (var slot in raw.Sections.Where(section => section.Triangles.Count != 0).Select(section => section.MaterialSlotId).Distinct())
			{
				var bindings = model.Materials.Where(binding => binding.SectionId == slot && binding.MaterialId != 0).Select(binding => binding.MaterialId).Distinct().ToArray();
				if (bindings.Length == 0)
					issues.Add(Error("VisibleMaterialBindingMissing", $"Visible MeshInfo {raw.MeshInfoIndex} uses material slot {slot}, but the Unit has {bindings.Length} concrete material bindings for it.", entry.AssetKey, entry.SourceFilePath));
			}
		}
	}

	private static PatchValidationIssue GeometryIssue(PatchValidationOptions options, string code, string message, AssetKey key, string path, Exception? exception = null)
		=> new(options.RequireSourceGeometryPreservation ? PatchValidationSeverity.Error : PatchValidationSeverity.Warning, code, message, key, path, exception);

	private static PatchValidationResult Result(string path, IReadOnlyList<PatchTocEntry> entries, IReadOnlyList<PatchValidationIssue> issues, int checkedUnits, int readableUnits)
		=> new(path, entries, issues, checkedUnits, readableUnits, DateTimeOffset.UtcNow);

	private static PatchValidationIssue Error(string code, string message, AssetKey? key = null, string? filePath = null, Exception? exception = null)
		=> new(PatchValidationSeverity.Error, code, message, key, filePath, exception);

	private static PatchValidationIssue Warning(string code, string message, AssetKey? key = null, string? filePath = null)
		=> new(PatchValidationSeverity.Warning, code, message, key, filePath);
}
