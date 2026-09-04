using System.Buffers.Binary;

namespace HD2ModAdaptation.PatchReconstruction;

// Purpose: Scans legacy and slim patch TOCs into rebuildable entry metadata.
public sealed class PatchTocScanner : IPatchTocScanner
{
	private const uint ExpectedMagic = 4026531857;
	private const int LegacyTypeOffset = 60;
	private const int SlimTypeOffset = 72;
	private const int TypeRecordSize = 32;
	private const int EntryRecordSize = 80;

	public async ValueTask<IReadOnlyList<PatchTocEntry>> ScanEntriesAsync(string patchTocFilePath, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(patchTocFilePath);
		var data = await File.ReadAllBytesAsync(patchTocFilePath, cancellationToken).ConfigureAwait(false);
		var entries = ScanEntries(data, patchTocFilePath);
		return PatchStreamCompatibility.NormalizeKnownEmptyStreamSentinel(patchTocFilePath, entries);
	}

	public IReadOnlyList<PatchTocEntry> ScanEntries(ReadOnlySpan<byte> tocData, string sourceFilePath, bool usesSlimEntryOffset = false)
	{
		if (tocData.Length < 12 || BinaryPrimitives.ReadUInt32LittleEndian(tocData) != ExpectedMagic) throw new InvalidDataException("Invalid patch TOC.");
		var typeCount = BinaryPrimitives.ReadUInt32LittleEndian(tocData.Slice(4, 4));
		var fileCount = BinaryPrimitives.ReadUInt32LittleEndian(tocData.Slice(8, 4));
		var entriesOffset = ResolveEntriesOffset(tocData, typeCount, fileCount, usesSlimEntryOffset);
		if (tocData.Length < checked(entriesOffset + checked((int)fileCount * EntryRecordSize))) throw new EndOfStreamException("TOC entry table is truncated.");
		var result = new List<PatchTocEntry>(checked((int)fileCount));
		var name = Path.GetFileName(sourceFilePath);
		for (var index = 0; index < fileCount; index++)
		{
			var offset = checked(entriesOffset + checked((int)index * EntryRecordSize));
			var streamSize = Read32(tocData, offset + 60);
			result.Add(new PatchTocEntry(new AssetKey(Read64(tocData, offset + 8), Read64(tocData, offset)), sourceFilePath, name,
				Read64(tocData, offset + 16), Read64(tocData, offset + 24), Read64(tocData, offset + 32), Read64(tocData, offset + 40), Read64(tocData, offset + 48),
				Read32(tocData, offset + 56), streamSize, Read32(tocData, offset + 64), Read32(tocData, offset + 68), Read32(tocData, offset + 72), Read32(tocData, offset + 76))
				{ DeclaredStreamSize = streamSize });
		}
		return result;
	}

	private static int ResolveEntriesOffset(ReadOnlySpan<byte> data, uint typeCount, uint fileCount, bool slim)
	{
		if (slim) return checked(SlimTypeOffset + checked((int)typeCount * TypeRecordSize));
		var legacy = checked(LegacyTypeOffset + checked((int)typeCount * TypeRecordSize));
		var standard = checked(SlimTypeOffset + checked((int)typeCount * TypeRecordSize));
		return typeCount != 0 && Score(data, SlimTypeOffset, standard, typeCount, fileCount) > Score(data, LegacyTypeOffset, legacy, typeCount, fileCount) ? standard : legacy;
	}

	private static int Score(ReadOnlySpan<byte> data, int typeOffset, int entriesOffset, uint typeCount, uint fileCount)
	{
		if (data.Length < checked(entriesOffset + checked((int)fileCount * EntryRecordSize))) return int.MinValue;
		var types = new HashSet<ulong>(); var count = 0UL;
		for (var index = 0; index < typeCount; index++) { var offset = typeOffset + index * TypeRecordSize; types.Add(Read64(data, offset + 8)); count += Read64(data, offset + 16); }
		var score = count == fileCount ? 1000 : 0;
		for (var index = 0; index < fileCount; index++) { var offset = entriesOffset + checked((int)index * EntryRecordSize); if (Read64(data, offset) != 0) score++; if (types.Contains(Read64(data, offset + 8))) score += 10; }
		return score;
	}

	private static uint Read32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));
	private static ulong Read64(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset, 8));
}
