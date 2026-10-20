namespace HD2ModAdaptation.PatchReconstruction.UnitMesh;

// Purpose: Reads one Unit patch entry with the explicitly referenced Composite and bone-name payloads.
public sealed class PatchUnitMeshReader
{
	public const ulong UnitTypeId = 0xe0a48d0be9a7453f;
	public const ulong BoneTypeId = 0x18dead01056b72e9;
	public const ulong CompositeUnitTypeId = 0xc4f0f4be7fb0c8d6;

	private readonly IPatchEntryPayloadReader payloadReader;
	private readonly UnitMeshReader unitMeshReader;
	private readonly IPatchTocScanner tocScanner;

	public PatchUnitMeshReader(
		IPatchEntryPayloadReader? payloadReader = null,
		UnitMeshReader? unitMeshReader = null,
		IPatchTocScanner? tocScanner = null)
	{
		this.payloadReader = payloadReader ?? new PatchEntryPayloadReader();
		this.unitMeshReader = unitMeshReader ?? new UnitMeshReader();
		this.tocScanner = tocScanner ?? new PatchTocScanner();
	}

	public async ValueTask<PatchUnitMesh> ReadAsync(PatchTocEntry entry, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(entry);
		var entries = await tocScanner.ScanEntriesAsync(entry.SourceFilePath, cancellationToken).ConfigureAwait(false);
		return await ReadAsync(entry, entries, PatchUnitDependencyPolicy.RequirePatchLocalComposite, cancellationToken).ConfigureAwait(false);
	}

	public async ValueTask<PatchUnitMesh> ReadAsync(
		PatchTocEntry entry,
		IReadOnlyList<PatchTocEntry> patchEntries,
		PatchUnitDependencyPolicy dependencyPolicy = PatchUnitDependencyPolicy.RequirePatchLocalComposite,
		CancellationToken cancellationToken = default)
		=> await ReadCoreAsync(entry, patchEntries, dependencyPolicy, new UnitMeshReadOptions(), retainPayloads: true, cancellationToken).ConfigureAwait(false);

	// Canonical rebuilds consume typed mesh semantics only. Do not retain the source GPU
	// payload or per-vertex raw byte mirrors after decoding; final encoding is regenerated.
	public async ValueTask<PatchUnitMesh> ReadCanonicalSourceAsync(
		PatchTocEntry entry,
		IReadOnlyList<PatchTocEntry> patchEntries,
		PatchUnitDependencyPolicy dependencyPolicy = PatchUnitDependencyPolicy.RequirePatchLocalComposite,
		CancellationToken cancellationToken = default)
		=> await ReadCoreAsync(entry, patchEntries, dependencyPolicy, UnitMeshReadOptions.CanonicalSource, retainPayloads: false, cancellationToken).ConfigureAwait(false);

	// 只要结构：mesh / section / material 的 TOC 事实，完全不解顶点。
	// 只问"有几个 section、每个多少索引"的调用方（section 映射表、衣柜编辑器）走这条，
	// 12 万顶点的头盔 unit 从秒级降到几十毫秒。
	// Structure only: TOC facts (meshes/sections/materials) with no vertex decode at all.
	public async ValueTask<PatchUnitMesh> ReadStructureOnlyAsync(
		PatchTocEntry entry,
		IReadOnlyList<PatchTocEntry> patchEntries,
		PatchUnitDependencyPolicy dependencyPolicy = PatchUnitDependencyPolicy.RequirePatchLocalComposite,
		CancellationToken cancellationToken = default)
		=> await ReadCoreAsync(entry, patchEntries, dependencyPolicy, UnitMeshReadOptions.StructureOnly, retainPayloads: false, cancellationToken).ConfigureAwait(false);

	private async ValueTask<PatchUnitMesh> ReadCoreAsync(
		PatchTocEntry entry,
		IReadOnlyList<PatchTocEntry> patchEntries,
		PatchUnitDependencyPolicy dependencyPolicy,
		UnitMeshReadOptions options,
		bool retainPayloads,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(entry);
		ArgumentNullException.ThrowIfNull(patchEntries);
		if (entry.AssetKey.TypeId != UnitTypeId)
		{
			throw new InvalidDataException($"Patch entry type 0x{entry.AssetKey.TypeId:x16} is not a Unit resource.");
		}

		var payload = await payloadReader.ReadPayloadAsync(entry, cancellationToken).ConfigureAwait(false);
		var compositeReference = ReadReference(payload.TocData, 16, "Composite");
		var boneReference = ReadReference(payload.TocData, 8, "bone");
		var compositePayload = await ReadCompositePayloadAsync(payload, patchEntries, compositeReference, dependencyPolicy, cancellationToken).ConfigureAwait(false);
		var bonePayload = await TryReadBonePayloadAsync(payload, patchEntries, boneReference, cancellationToken).ConfigureAwait(false);
		var boneNames = bonePayload is null ? UnitBoneNames.Empty : new UnitBoneNamesReader().Read(bonePayload.TocData);
		var model = compositePayload is null
			? unitMeshReader.Read(payload.TocData, payload.GpuResourceData, boneNames: boneNames, options: options)
			: unitMeshReader.Read(payload.TocData, payload.GpuResourceData, compositePayload.TocData, compositePayload.GpuResourceData, boneNames, options);
		return new PatchUnitMesh(
			entry,
			retainPayloads ? payload : EmptyPayload(entry),
			model,
			retainPayloads ? compositePayload : null,
			new PatchUnitDependencyResolution(boneReference, compositeReference, bonePayload is not null, compositePayload is not null));
	}

	private static PatchEntryPayload EmptyPayload(PatchTocEntry entry)
		=> new(entry, Array.Empty<byte>(), Array.Empty<byte>(), Array.Empty<byte>());

	private async ValueTask<PatchEntryPayload?> ReadCompositePayloadAsync(
		PatchEntryPayload unitPayload,
		IReadOnlyList<PatchTocEntry> patchEntries,
		ulong reference,
		PatchUnitDependencyPolicy dependencyPolicy,
		CancellationToken cancellationToken)
	{
		if (reference == 0)
		{
			return null;
		}

		var referencedEntry = patchEntries.SingleOrDefault(candidate =>
			candidate.AssetKey.TypeId == CompositeUnitTypeId && candidate.AssetKey.FileId == reference);
		if (referencedEntry is null)
		{
			if (dependencyPolicy == PatchUnitDependencyPolicy.RequirePatchLocalComposite)
			{
				throw new InvalidDataException($"Unit references Composite asset 0x{reference:x16}, but that entry is absent from this patch.");
			}
			return null;
		}

		return await payloadReader.ReadPayloadAsync(referencedEntry, cancellationToken).ConfigureAwait(false);
	}

	private async ValueTask<PatchEntryPayload?> TryReadBonePayloadAsync(
		PatchEntryPayload unitPayload,
		IReadOnlyList<PatchTocEntry> patchEntries,
		ulong reference,
		CancellationToken cancellationToken)
	{
		if (reference == 0)
		{
			return null;
		}

		var referencedEntry = patchEntries.SingleOrDefault(candidate =>
			candidate.AssetKey.TypeId == BoneTypeId && candidate.AssetKey.FileId == reference);
		if (referencedEntry is null)
		{
			return null;
		}

		try
		{
			return await payloadReader.ReadPayloadAsync(referencedEntry, cancellationToken).ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
		{
			return null;
		}
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

// Purpose: Controls whether a source Unit must carry its Composite payload in the patch being reconstructed.
public enum PatchUnitDependencyPolicy
{
	RequirePatchLocalComposite,
	AllowExternalCompositeReference
}
