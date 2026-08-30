using HD2ModCore.Application;
using HD2ModCore.Decoration;
using HD2ModCore.Domain;
using AdaptationPatchTocEntry = HD2ModAdaptation.PatchReconstruction.PatchTocEntry;

namespace HD2ModCore.Infrastructure;

// Stores the complete source Patch family needed by a decoration package beside decoration.json.
// The original Patch encoding is preserved so generation does not materialize a large mesh payload.
public sealed class DecorationPackageSnapshotService
{
    private readonly IModFileResolver _fileResolver;
    private readonly IModInformationReader _informationReader;

    public DecorationPackageSnapshotService(IModFileResolver fileResolver, IModInformationReader informationReader)
    {
        _fileResolver = fileResolver ?? throw new ArgumentNullException(nameof(fileResolver));
        _informationReader = informationReader ?? throw new ArgumentNullException(nameof(informationReader));
    }

    public async Task CaptureAsync(
        ModNode source,
        string modsRootDirectory,
        IReadOnlyList<DecorationSourceUnit> selected,
        string outputDirectory,
        IReadOnlyDictionary<string, IReadOnlyList<AdaptationPatchTocEntry>>? preparedEntries = null,
        CancellationToken cancellationToken = default,
        IProgress<DecorationOperationProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(modsRootDirectory);
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        var context = ModInformationRequestContext.Create(
            ModInformationCacheScope.Operation,
            operationName: "DecorationPatchSnapshot");
        try
        {
            var selectedKeys = selected.Select(item => (item.TypeId, item.FileId)).ToHashSet();
            var sources = await _fileResolver.ResolvePatchFilesAsync(source, modsRootDirectory, cancellationToken).ConfigureAwait(false);
            var matching = new List<string>();
            foreach (var patch in sources.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var fullPath = Path.GetFullPath(patch);
                var entries = preparedEntries is not null && preparedEntries.TryGetValue(fullPath, out var cached)
                    ? cached
                    : await ReadEntriesAsync(source, fullPath, context, cancellationToken).ConfigureAwait(false);
                if (entries.Any(entry => selectedKeys.Contains((entry.AssetKey.TypeId, entry.AssetKey.FileId)))) matching.Add(fullPath);
            }

            if (matching.Count == 0) throw new InvalidDataException("没有找到所选装饰 Unit 的来源 Patch。");

            Directory.CreateDirectory(outputDirectory);
            progress?.Report(new DecorationOperationProgress("正在复制装饰来源 Patch", 0, matching.Count));
            for (var index = 0; index < matching.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var patch = matching[index];
                foreach (var path in new[] { patch, patch + ".gpu_resources", patch + ".stream" })
                {
                    if (!File.Exists(path)) continue;
                    File.Copy(path, Path.Combine(outputDirectory, Path.GetFileName(path)), overwrite: true);
                }
                progress?.Report(new DecorationOperationProgress("正在复制装饰来源 Patch", index + 1, matching.Count));
            }
        }
        finally
        {
            _informationReader.ClearOperation(context.OperationId);
        }
    }

    private async ValueTask<IReadOnlyList<AdaptationPatchTocEntry>> ReadEntriesAsync(
        ModNode source,
        string patch,
        ModInformationRequestContext context,
        CancellationToken cancellationToken)
    {
        var result = await _informationReader.ReadPatchIndexAsync(
            new ModInformationReadRequest(
                patch,
                context,
                ContentView: ModInformationContentView.Effective,
                NodeId: source.Id),
            cancellationToken).ConfigureAwait(false);
        if (result.Data is not null) return result.Data.Entries;

        var detail = result.State.Diagnostics.FirstOrDefault()?.Message ?? "未知错误";
        throw new InvalidDataException($"无法读取装饰来源 Patch 目录：{detail}");
    }
}
