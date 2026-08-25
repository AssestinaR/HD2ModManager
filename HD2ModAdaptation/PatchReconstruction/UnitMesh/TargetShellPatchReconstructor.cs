namespace HD2ModAdaptation.PatchReconstruction.UnitMesh;

// Purpose: Writes a target-shell output while removing obsolete source Units and unreferenced source Composites.
[Obsolete("已过时：替换护甲生产统一使用 DirectSourceUnitReuse；仅保留兼容调用和测试。")]
public sealed class TargetShellPatchReconstructor
{
	private const int CompositeReferenceOffset = 16;
	private readonly IPatchTocScanner scanner;
	private readonly IPatchEntryPayloadReader payloadReader;
	private readonly PatchArchiveWriter writer;

	public TargetShellPatchReconstructor(
		IPatchTocScanner? scanner = null,
		IPatchEntryPayloadReader? payloadReader = null,
		PatchArchiveWriter? writer = null)
	{
		this.scanner = scanner ?? new PatchTocScanner();
		this.payloadReader = payloadReader ?? new PatchEntryPayloadReader();
		this.writer = writer ?? new PatchArchiveWriter(this.scanner, this.payloadReader);
	}

	public async ValueTask<TargetShellPatchReconstructionResult> WriteAsync(
		string sourcePatchTocPath,
		string outputDirectoryPath,
		TargetShellUnitOutput output,
		bool overwriteExisting = false,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sourcePatchTocPath);
		ArgumentNullException.ThrowIfNull(output);
		var sourcePath = Path.GetFullPath(sourcePatchTocPath);
		var entries = await scanner.ScanEntriesAsync(sourcePath, cancellationToken).ConfigureAwait(false);
		var removals = await GetSafeRemovalsAsync(entries, output.ReplacedSourceUnitAssetKeys, cancellationToken).ConfigureAwait(false);
		var writeResult = await writer.WriteAsync(
			sourcePath,
			outputDirectoryPath,
			Array.Empty<PatchUnitMeshEditResult>(),
			output.AdditionalEntries,
			removals,
			overwriteExisting,
			preserveOriginalStream: true,
			cancellationToken: cancellationToken).ConfigureAwait(false);
		return new TargetShellPatchReconstructionResult(writeResult, output.TargetUnitAssetKey, removals.Select(entry => entry.AssetKey).ToArray());
	}

	private async ValueTask<IReadOnlyCollection<PatchTocEntry>> GetSafeRemovalsAsync(
		IReadOnlyList<PatchTocEntry> entries,
		IReadOnlyCollection<AssetKey> replacedSourceUnitKeys,
		CancellationToken cancellationToken)
	{
		if (replacedSourceUnitKeys.Count == 0)
		{
			throw new InvalidDataException("A target-shell reconstruction must replace at least one old source Unit.");
		}

		var sourceUnits = entries.Where(entry => entry.AssetKey.TypeId == PatchUnitMeshReader.UnitTypeId).ToArray();
		var removedUnits = sourceUnits.Where(entry => replacedSourceUnitKeys.Contains(entry.AssetKey)).ToArray();
		if (removedUnits.Length != replacedSourceUnitKeys.Count)
		{
			throw new InvalidDataException("One or more explicit source Unit removals do not belong to the source patch.");
		}

		var compositeByUnit = new Dictionary<AssetKey, ulong>();
		foreach (var unit in sourceUnits)
		{
			var payload = await payloadReader.ReadPayloadAsync(unit, cancellationToken).ConfigureAwait(false);
			compositeByUnit.Add(unit.AssetKey, ReadReference(payload.TocData, CompositeReferenceOffset, "Composite"));
		}

		var removedUnitKeys = removedUnits.Select(entry => entry.AssetKey).ToHashSet();
		var candidateCompositeIds = removedUnits
			.Select(entry => compositeByUnit[entry.AssetKey])
			.Where(id => id != 0)
			.ToHashSet();
		var referencedByRetainedUnit = sourceUnits
			.Where(entry => !removedUnitKeys.Contains(entry.AssetKey))
			.Select(entry => compositeByUnit[entry.AssetKey])
			.ToHashSet();
		var removableCompositeIds = candidateCompositeIds.Where(id => !referencedByRetainedUnit.Contains(id)).ToHashSet();
		var composites = entries.Where(entry => entry.AssetKey.TypeId == PatchUnitMeshReader.CompositeUnitTypeId && removableCompositeIds.Contains(entry.AssetKey.FileId));
		return removedUnits.Concat(composites).ToArray();
	}

	private static ulong ReadReference(ReadOnlySpan<byte> tocData, int offset, string name)
	{
		if (tocData.Length < offset + sizeof(ulong))
		{
			throw new InvalidDataException($"Unit TocData is too short to read its {name} reference.");
		}
		return BitConverter.ToUInt64(tocData.Slice(offset, sizeof(ulong)));
	}
}

public sealed record TargetShellPatchReconstructionResult(
	PatchArchiveFileWriteResult WriteResult,
	AssetKey TargetUnitAssetKey,
	IReadOnlyCollection<AssetKey> RemovedAssetKeys);
