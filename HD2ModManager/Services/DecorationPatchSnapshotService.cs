using HD2ModCore.Application;
using HD2ModCore.Decoration;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModManager.Services;

// Desktop compatibility wrapper. The snapshot implementation lives in HD2ModCore so GUI and
// future headless callers use the exact same Patch-family selection and copy semantics.
public sealed class DecorationPatchSnapshotService
{
    private readonly DecorationPackageSnapshotService _inner;

    public DecorationPatchSnapshotService(IModFileResolver fileResolver, IModInformationReader informationReader)
    {
        _inner = new DecorationPackageSnapshotService(fileResolver, informationReader);
    }

    public Task CaptureAsync(ModNode source, string modsRootDirectory, IReadOnlyList<DecorationSourceUnit> selected,
        string outputDirectory, IReadOnlyDictionary<string, IReadOnlyList<HD2ModAdaptation.PatchReconstruction.PatchTocEntry>>? preparedEntries,
        CancellationToken cancellationToken = default, IProgress<DecorationOperationProgress>? progress = null)
        => _inner.CaptureAsync(source, modsRootDirectory, selected, outputDirectory, preparedEntries, cancellationToken, progress);
}
