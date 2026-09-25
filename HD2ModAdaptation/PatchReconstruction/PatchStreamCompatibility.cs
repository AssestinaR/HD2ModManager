namespace HD2ModAdaptation.PatchReconstruction;

// Purpose: Recognizes a narrowly-scoped nonstandard Patch layout seen in
// game-loadable packages: an empty (or omitted empty) .stream sidecar
// accompanied by one repeated impossible StreamSize value and zero
// StreamOffset for every entry.
// The engine can load these GPU-backed assets without stream data, but generic
// readers otherwise attempt to read the bogus range.
public static class PatchStreamCompatibility
{
	public static IReadOnlyList<PatchTocEntry> NormalizeKnownEmptyStreamSentinel(
		string patchTocFilePath,
		IReadOnlyList<PatchTocEntry> entries)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(patchTocFilePath);
		ArgumentNullException.ThrowIfNull(entries);
		if (entries.Count == 0) return entries;

		var streamPath = patchTocFilePath + ".stream";
		// Mod import intentionally omits zero-byte files, so a missing .stream is
		// equivalent to an empty one here. The remaining TOC-wide signature is
		// deliberately strict enough that a normally missing stream still fails.
		// Inspect the opened resource, not symbolic-link metadata: a non-empty
		// deployed stream must never be mistaken for the empty-stream sentinel.
		try
		{
			using var stream = File.OpenRead(streamPath);
			if (stream.Length != 0) return entries;
		}
		catch (FileNotFoundException) { }
		catch (DirectoryNotFoundException) { }
		if (entries.Any(entry => entry.StreamOffset != 0)) return entries;

		var declaredSizes = entries
			.Select(entry => entry.DeclaredStreamSize == 0 ? entry.StreamSize : entry.DeclaredStreamSize)
			.Distinct()
			.ToArray();
		if (declaredSizes.Length != 1 || declaredSizes[0] == 0) return entries;

		var sentinel = declaredSizes[0];
		return entries
			.Select(entry => entry with { StreamSize = 0, DeclaredStreamSize = sentinel })
			.ToArray();
	}

	public static uint? GetEmptyStreamSentinel(IReadOnlyList<PatchTocEntry> entries)
	{
		ArgumentNullException.ThrowIfNull(entries);
		var declared = entries
			.Where(entry => entry.UsesEmptyStreamCompatibilityOverride)
			.Select(entry => entry.DeclaredStreamSize)
			.Distinct()
			.ToArray();
		return declared.Length == 1 && entries.All(entry => entry.UsesEmptyStreamCompatibilityOverride)
			? declared[0]
			: null;
	}
}
