using HD2ModAdaptation.PatchReconstruction.PatchWorkspace;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModCore.Application;
using HD2ModCore.Decoration;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using AdaptationAssetKey = HD2ModAdaptation.PatchReconstruction.AssetKey;
using AdaptationPatchTocEntry = HD2ModAdaptation.PatchReconstruction.PatchTocEntry;
using CoreAssetKey = HD2ModCore.Domain.AssetKey;
using CorePatchEntryPayload = HD2ModCore.Domain.PatchEntryPayload;

namespace HD2ModCore.Tests;

public sealed class DecorationPackageSnapshotServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hd2-decoration-snapshot-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CaptureAsync_CopiesOnlySelectedPatchFamilyIncludingExistingSidecars()
    {
        var sourceDirectory = Path.Combine(_root, "source");
        var outputDirectory = Path.Combine(_root, "snapshot");
        Directory.CreateDirectory(sourceDirectory);
        var selectedPatch = Path.Combine(sourceDirectory, "selected.patch_0");
        var unrelatedPatch = Path.Combine(sourceDirectory, "unrelated.patch_0");
        await File.WriteAllBytesAsync(selectedPatch, [0x01, 0x02, 0x03]);
        await File.WriteAllBytesAsync(selectedPatch + ".gpu_resources", [0x04, 0x05]);
        await File.WriteAllBytesAsync(selectedPatch + ".stream", [0x06]);
        await File.WriteAllBytesAsync(unrelatedPatch, [0x07]);

        var selectedKey = new CoreAssetKey(0x1111UL, 0x2222UL);
        var unrelatedKey = new CoreAssetKey(0x3333UL, 0x4444UL);
        var node = new ModNode(ModNodeId.New(), "source", new ModNodeMetadata("Source", null, DateTimeOffset.UtcNow, null), [], []);
        var reader = new StubInformationReader(new Dictionary<string, PatchWorkspaceIndex>(StringComparer.OrdinalIgnoreCase)
        {
            [Path.GetFullPath(selectedPatch)] = Index(selectedPatch, selectedKey),
            [Path.GetFullPath(unrelatedPatch)] = Index(unrelatedPatch, unrelatedKey)
        });
        var progress = new List<DecorationOperationProgress>();
        var service = new DecorationPackageSnapshotService(
            new StubResolver(selectedPatch, unrelatedPatch),
            reader);

        await service.CaptureAsync(
            node,
            _root,
            [new DecorationSourceUnit { TypeId = selectedKey.TypeId, FileId = selectedKey.FileId }],
            outputDirectory,
            progress: new CollectingProgress(progress));

        await AssertFileBytesAsync(selectedPatch, Path.Combine(outputDirectory, Path.GetFileName(selectedPatch)));
        await AssertFileBytesAsync(selectedPatch + ".gpu_resources", Path.Combine(outputDirectory, Path.GetFileName(selectedPatch) + ".gpu_resources"));
        await AssertFileBytesAsync(selectedPatch + ".stream", Path.Combine(outputDirectory, Path.GetFileName(selectedPatch) + ".stream"));
        Assert.False(File.Exists(Path.Combine(outputDirectory, Path.GetFileName(unrelatedPatch))));
        Assert.Equal(2, progress.Count);
        Assert.Equal((0, 1), (progress[0].Completed, progress[0].Total));
        Assert.Equal((1, 1), (progress[1].Completed, progress[1].Total));
        Assert.Equal(1, reader.ClearedOperationCount);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static PatchWorkspaceIndex Index(string patchPath, CoreAssetKey key)
    {
        var entry = new AdaptationPatchTocEntry(
            new AdaptationAssetKey(key.TypeId, key.FileId),
            patchPath,
            Path.GetFileName(patchPath));
        return new PatchWorkspaceIndex(patchPath, [entry], [1]);
    }

    private static async Task AssertFileBytesAsync(string expectedPath, string actualPath)
        => Assert.Equal(await File.ReadAllBytesAsync(expectedPath), await File.ReadAllBytesAsync(actualPath));

    private sealed class StubResolver(params string[] patches) : IModFileResolver
    {
        public ValueTask<IReadOnlyList<string>> ResolvePatchFilesAsync(ModNode node, string modsRootDirectory, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<string>>(patches);
    }

    private sealed class StubInformationReader(IReadOnlyDictionary<string, PatchWorkspaceIndex> indexes) : IModInformationReader
    {
        public int ClearedOperationCount { get; private set; }

        public ValueTask<ModInformationPropertyResult<T>> RequestAsync<T>(FactKey key, ModInformationReadRequest request, FactReadOperation<T> operation, CancellationToken cancellationToken = default) where T : class
            => ValueTask.FromException<ModInformationPropertyResult<T>>(new NotSupportedException());

        public ValueTask<ModInformationPropertyResult<PatchWorkspaceIndex>> ReadPatchIndexAsync(ModInformationReadRequest request, CancellationToken cancellationToken = default)
        {
            var index = indexes[Path.GetFullPath(request.SourcePath)];
            var state = new ModInformationPropertyState(ModInformationPropertyKind.PatchCatalog, ModInformationPropertyStatus.Fresh, ModInformationValueSource.Producer);
            return ValueTask.FromResult(new ModInformationPropertyResult<PatchWorkspaceIndex>(index, state));
        }

        public ValueTask<ModInformationPropertyResult<CorePatchEntryPayload>> ReadPatchPayloadAsync(AdaptationPatchTocEntry entry, ModInformationReadRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromException<ModInformationPropertyResult<CorePatchEntryPayload>>(new NotSupportedException());

        public ValueTask<ModInformationPropertyResult<PatchUnitMesh>> ReadUnitAsync(AdaptationPatchTocEntry entry, IReadOnlyList<AdaptationPatchTocEntry>? patchEntries, PatchUnitDependencyPolicy dependencyPolicy, ModInformationReadRequest request, bool canonicalSource = false, CancellationToken cancellationToken = default)
            => ValueTask.FromException<ModInformationPropertyResult<PatchUnitMesh>>(new NotSupportedException());

        public ValueTask<ModInformationPropertyResult<ModUnitStructureSummary>> ReadUnitSummaryAsync(AdaptationPatchTocEntry entry, IReadOnlyList<AdaptationPatchTocEntry>? patchEntries, PatchUnitDependencyPolicy dependencyPolicy, ModInformationReadRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromException<ModInformationPropertyResult<ModUnitStructureSummary>>(new NotSupportedException());

        public ValueTask<ModInformationPropertyResult<ModSourceUnitFactsSnapshot>> ReadSourceUnitFactsAsync(PatchWorkspaceIndex index, ModInformationReadRequest request, PatchUnitDependencyPolicy dependencyPolicy = PatchUnitDependencyPolicy.RequirePatchLocalComposite, CancellationToken cancellationToken = default)
            => ValueTask.FromException<ModInformationPropertyResult<ModSourceUnitFactsSnapshot>>(new NotSupportedException());

        public void InvalidateNode(ModNodeId nodeId) { }
        public void ClearOperation(Guid operationId) => ClearedOperationCount++;
        public void ClearSession(Guid sessionId) { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CollectingProgress(List<DecorationOperationProgress> values) : IProgress<DecorationOperationProgress>
    {
        public void Report(DecorationOperationProgress value) => values.Add(value);
    }
}
