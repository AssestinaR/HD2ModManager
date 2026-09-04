namespace HD2ModAdaptation.PatchReconstruction;

// Purpose: Defines the self-contained data contracts used by the migrated patch archive reconstruction pipeline.
public readonly record struct AssetKey(ulong TypeId, ulong FileId);

public sealed record PatchTocEntry(
	AssetKey AssetKey,
	string SourceFilePath,
	string SourceFileName,
	ulong TocDataOffset = 0,
	ulong StreamOffset = 0,
	ulong GpuResourceOffset = 0,
	ulong Unknown1 = 0,
	ulong Unknown2 = 0,
	uint TocDataSize = 0,
	uint StreamSize = 0,
	uint GpuResourceSize = 0,
	uint Unknown3 = 0,
	uint Unknown4 = 0,
	uint EntryIndex = 0)
{
	// The source TOC declaration is retained when a known streamless compatibility
	// signature is normalized for reading. Writers use StreamSize (the effective
	// readable size), while diagnostics can still explain the original metadata.
	public uint DeclaredStreamSize { get; init; }
	public bool UsesEmptyStreamCompatibilityOverride
		=> DeclaredStreamSize != 0 && StreamSize == 0 && DeclaredStreamSize != StreamSize;
}

public sealed record PatchEntryPayload(PatchTocEntry Entry, byte[] TocData, byte[] StreamData, byte[] GpuResourceData);

public sealed record PatchArchiveAdditionalEntry(
	AssetKey AssetKey,
	byte[] TocData,
	byte[] StreamData,
	byte[] GpuResourceData,
	ulong Unknown1 = 0,
	ulong Unknown2 = 0,
	uint Unknown3 = 0,
	uint Unknown4 = 0);

public sealed record PatchUnitMeshEditResult(
	PatchTocEntry Entry,
	PatchEntryPayload OriginalPayload,
	byte[] TocData,
	byte[] GpuResourceData,
	AssetKey? CompositeAssetKey = null,
	byte[]? CompositeTocData = null,
	byte[]? CompositeGpuResourceData = null,
	IReadOnlyCollection<ulong>? ReplacementMaterialIds = null);

public sealed record PatchArchiveFileWriteResult(
	string OutputDirectoryPath,
	string TocFilePath,
	string StreamFilePath,
	string GpuResourceFilePath,
	long TocFileSize,
	long StreamFileSize,
	long GpuResourceFileSize);

public sealed record PatchAssetReconstructionResult(
	PatchArchiveFileWriteResult WriteResult,
	IReadOnlyCollection<ulong> MaterialIds,
	MaterialDependencyResolutionResult MaterialDependencies);

public interface IPatchTocScanner
{
	ValueTask<IReadOnlyList<PatchTocEntry>> ScanEntriesAsync(string patchTocFilePath, CancellationToken cancellationToken = default);
	IReadOnlyList<PatchTocEntry> ScanEntries(ReadOnlySpan<byte> tocData, string sourceFilePath, bool usesSlimEntryOffset = false);
}

public interface IPatchEntryPayloadReader
{
	ValueTask<PatchEntryPayload> ReadPayloadAsync(PatchTocEntry entry, CancellationToken cancellationToken = default);
}
