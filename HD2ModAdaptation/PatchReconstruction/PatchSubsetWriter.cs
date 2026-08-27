namespace HD2ModAdaptation.PatchReconstruction;

// Purpose: Writes a compact standalone Patch group from approved whole-entry payload copies.
public sealed class PatchSubsetWriter
{
	private readonly IPatchTocScanner scanner;
	private readonly IPatchEntryPayloadReader payloadReader;
	private readonly PatchArchiveWriter archiveWriter;

	public PatchSubsetWriter(
		IPatchTocScanner? scanner = null,
		IPatchEntryPayloadReader? payloadReader = null,
		PatchArchiveWriter? archiveWriter = null)
	{
		this.scanner = scanner ?? new PatchTocScanner();
		this.payloadReader = payloadReader ?? new PatchEntryPayloadReader();
		this.archiveWriter = archiveWriter ?? new PatchArchiveWriter(this.scanner, this.payloadReader);
	}

	public async ValueTask<PatchArchiveFileWriteResult> WriteAsync(
		string sourcePatchTocPath,
		string outputDirectoryPath,
		IReadOnlyCollection<AssetKey> selectedAssetKeys,
		bool overwriteExisting = false,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sourcePatchTocPath);
		ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectoryPath);
		ArgumentNullException.ThrowIfNull(selectedAssetKeys);
		if (selectedAssetKeys.Count == 0)
		{
			throw new InvalidDataException("A Patch subset must contain at least one AssetKey.");
		}

		var source = Path.GetFullPath(sourcePatchTocPath);
		return await WriteAsync(source, outputDirectoryPath, selectedAssetKeys.Select(key => new PatchSubsetSelection(source, key)).ToArray(), overwriteExisting, cancellationToken).ConfigureAwait(false);
	}

	// Writes only the selected entries. Unlike WriteAsync, this deliberately removes
	// every entry from the header template before adding the approved selections, so
	// a multi-source repack does not accidentally retain the template Patch payload.
	public async ValueTask<PatchArchiveFileWriteResult> WriteSelectedAsync(
		string headerTemplatePatchTocPath,
		string outputDirectoryPath,
		IReadOnlyCollection<PatchSubsetSelection> selections,
		bool overwriteExisting = false,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(headerTemplatePatchTocPath);
		ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectoryPath);
		ArgumentNullException.ThrowIfNull(selections);
		if (selections.Count == 0) throw new InvalidDataException("A Patch subset must contain at least one selected entry.");

		var headerTemplatePath = Path.GetFullPath(headerTemplatePatchTocPath);
		var duplicate = selections.GroupBy(selection => selection.AssetKey).FirstOrDefault(group => group.Count() != 1);
		if (duplicate is not null)
			throw new InvalidDataException($"Duplicate selected asset 0x{duplicate.Key.TypeId:x16}/0x{duplicate.Key.FileId:x16}.");

		var entriesBySource = new Dictionary<string, IReadOnlyDictionary<AssetKey, PatchTocEntry>>(StringComparer.OrdinalIgnoreCase);
		foreach (var source in selections.Select(selection => Path.GetFullPath(selection.SourcePatchTocPath)).Distinct(StringComparer.OrdinalIgnoreCase))
			entriesBySource[source] = (await scanner.ScanEntriesAsync(source, cancellationToken).ConfigureAwait(false))
				.GroupBy(entry => entry.AssetKey)
				.ToDictionary(group => group.Key, group => group.Last());

		var additions = new List<PatchArchiveAdditionalEntry>(selections.Count);
		foreach (var selection in selections)
		{
			var source = Path.GetFullPath(selection.SourcePatchTocPath);
			if (!entriesBySource[source].TryGetValue(selection.AssetKey, out var entry))
				throw new KeyNotFoundException($"Selected AssetKey 0x{selection.AssetKey.TypeId:x16}/0x{selection.AssetKey.FileId:x16} is absent from '{source}'.");
			var payload = await payloadReader.ReadPayloadAsync(entry, cancellationToken).ConfigureAwait(false);
			additions.Add(new PatchArchiveAdditionalEntry(entry.AssetKey, payload.TocData, payload.StreamData, payload.GpuResourceData, entry.Unknown1, entry.Unknown2, entry.Unknown3, entry.Unknown4));
		}

		var headerTemplate = await File.ReadAllBytesAsync(headerTemplatePath, cancellationToken).ConfigureAwait(false);
		var removedEntries = await scanner.ScanEntriesAsync(headerTemplatePath, cancellationToken).ConfigureAwait(false);
		return await archiveWriter.WriteAsync(
			headerTemplatePath,
			outputDirectoryPath,
			Array.Empty<PatchUnitMeshEditResult>(),
			additions,
			removedEntries,
			overwriteExisting,
			preserveOriginalStream: false,
			headerTemplateTocData: headerTemplate,
			cancellationToken).ConfigureAwait(false);
	}

	public async ValueTask<PatchArchiveFileWriteResult> WriteAsync(
		string headerTemplatePatchTocPath,
		string outputDirectoryPath,
		IReadOnlyCollection<PatchSubsetSelection> selections,
		bool overwriteExisting = false,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(headerTemplatePatchTocPath);
		ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectoryPath);
		ArgumentNullException.ThrowIfNull(selections);
		if (selections.Count == 0)
		{
			throw new InvalidDataException("A Patch subset must contain at least one selected entry.");
		}

		var headerTemplatePath = Path.GetFullPath(headerTemplatePatchTocPath);
		var duplicate = selections.GroupBy(selection => selection.AssetKey).FirstOrDefault(group => group.Count() != 1);
		if (duplicate is not null)
		{
			throw new InvalidDataException($"Duplicate selected asset 0x{duplicate.Key.TypeId:x16}/0x{duplicate.Key.FileId:x16}.");
		}

		var entriesBySource = new Dictionary<string, IReadOnlyDictionary<AssetKey, PatchTocEntry>>(StringComparer.OrdinalIgnoreCase);
		foreach (var source in selections.Select(selection => Path.GetFullPath(selection.SourcePatchTocPath)).Distinct(StringComparer.OrdinalIgnoreCase))
		{
			entriesBySource[source] = (await scanner.ScanEntriesAsync(source, cancellationToken).ConfigureAwait(false))
				.GroupBy(entry => entry.AssetKey)
				.ToDictionary(group => group.Key, group => group.Last());
		}

		var additions = new List<PatchArchiveAdditionalEntry>(selections.Count);
		foreach (var selection in selections)
		{
			var source = Path.GetFullPath(selection.SourcePatchTocPath);
			if (!entriesBySource[source].TryGetValue(selection.AssetKey, out var entry))
			{
				throw new KeyNotFoundException($"Selected AssetKey 0x{selection.AssetKey.TypeId:x16}/0x{selection.AssetKey.FileId:x16} is absent from '{source}'.");
			}
			var payload = await payloadReader.ReadPayloadAsync(entry, cancellationToken).ConfigureAwait(false);
			additions.Add(new PatchArchiveAdditionalEntry(entry.AssetKey, payload.TocData, payload.StreamData, payload.GpuResourceData, entry.Unknown1, entry.Unknown2, entry.Unknown3, entry.Unknown4));
		}

		var headerTemplate = await File.ReadAllBytesAsync(headerTemplatePath, cancellationToken).ConfigureAwait(false);
		return await archiveWriter.WriteAsync(
			headerTemplatePath,
			outputDirectoryPath,
			Array.Empty<PatchUnitMeshEditResult>(),
			additions,
			await scanner.ScanEntriesAsync(headerTemplatePath, cancellationToken).ConfigureAwait(false),
			overwriteExisting,
			preserveOriginalStream: false,
			headerTemplateTocData: headerTemplate.ToArray(),
			cancellationToken).ConfigureAwait(false);
	}
}

public sealed record PatchSubsetSelection(string SourcePatchTocPath, AssetKey AssetKey);
