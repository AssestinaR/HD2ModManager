using System.Buffers.Binary;
using System.Security.Cryptography;
using HD2ModAdaptation.Analysis;
using HD2ModCore.Application;
using HD2ModCore.Domain;
using AdaptationPatchReconstruction = HD2ModAdaptation.PatchReconstruction;
using AdaptationAssetKey = HD2ModAdaptation.PatchReconstruction.AssetKey;
using AdaptationPatchTocEntry = HD2ModAdaptation.PatchReconstruction.PatchTocEntry;
using AdaptationPatchEntryPayload = HD2ModAdaptation.PatchReconstruction.PatchEntryPayload;
using AdaptationPayloadReader = HD2ModAdaptation.PatchReconstruction.IPatchEntryPayloadReader;

namespace HD2ModCore.Infrastructure;

// 作用：统一实现“多个 Mod / 多个 Patch 组 -> AssetKey 变体 -> 新 Patch”的规划与写出。
public sealed class PatchResourceRepackagingService : IPatchResourceRepackagingService
{
    private readonly IPatchFileNameParser fileNameParser;
    private readonly IPatchGroupAnalyzer analyzer;
    private readonly AdaptationPayloadReader payloadReader;
    private readonly AdaptationPatchReconstruction.PatchSubsetWriter subsetWriter;

    public PatchResourceRepackagingService(
        IPatchFileNameParser? fileNameParser = null,
        IPatchGroupAnalyzer? analyzer = null,
        AdaptationPayloadReader? payloadReader = null,
        AdaptationPatchReconstruction.PatchSubsetWriter? subsetWriter = null)
    {
        this.fileNameParser = fileNameParser ?? new PatchFileNameParser();
        this.analyzer = analyzer ?? new PatchGroupAnalyzer();
        this.payloadReader = payloadReader ?? new AdaptationPatchReconstruction.PatchEntryPayloadReader();
        this.subsetWriter = subsetWriter ?? new AdaptationPatchReconstruction.PatchSubsetWriter(payloadReader: this.payloadReader);
    }

    public IReadOnlyList<PatchResourcePatchGroup> DiscoverPatchGroups(
        IReadOnlyCollection<ModNode> nodes,
        string modsRootDirectory)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentException.ThrowIfNullOrWhiteSpace(modsRootDirectory);

        var result = new List<PatchResourcePatchGroup>();
        foreach (var node in nodes)
        {
            var directory = Path.GetFullPath(Path.Combine(modsRootDirectory, node.RelativePath));
            if (!Directory.Exists(directory)) continue;

            var parsed = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                .Select(path => (Path: path, Parsed: TryParse(path)))
                .Where(item => item.Parsed is not null)
                .Select(item => (item.Path, Info: item.Parsed!));

            foreach (var group in parsed
                .Where(item => item.Info.SidecarKind == PatchSidecarKind.Base)
                .GroupBy(item => (item.Info.ArchiveHex16, item.Info.PatchIndex))
                .OrderBy(group => group.Key.ArchiveHex16, StringComparer.OrdinalIgnoreCase)
                .ThenBy(group => group.Key.PatchIndex))
            {
                var basePath = group.Select(item => item.Path).First();
                result.Add(new PatchResourcePatchGroup(
                    node.Id,
                    node.Metadata.Name,
                    group.Key.ArchiveHex16,
                    group.Key.PatchIndex,
                    basePath));
            }
        }

        return result
            .OrderBy(group => group.ModName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.ArchiveHex, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.PatchIndex)
            .ToArray();
    }

    public async ValueTask<PatchResourceRepackagingInspection> InspectAsync(
        IReadOnlyCollection<PatchResourcePatchGroup> patchGroups,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(patchGroups);
        var issues = new List<CoreIssue>();
        var occurrences = new List<PatchResourceOccurrence>();

        foreach (var group in patchGroups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var analysis = analyzer is IDependencyGraphPatchGroupAnalyzer dependencyAnalyzer
                    ? await dependencyAnalyzer.AnalyzeDependencyGraphAsync(new PatchGroupInput(group.PatchTocPath), cancellationToken).ConfigureAwait(false)
                    : await analyzer.AnalyzeAsync(new PatchGroupInput(group.PatchTocPath), cancellationToken).ConfigureAwait(false);
                var facts = analysis.Assets
                    .GroupBy(asset => new AdaptationAssetKey(asset.AssetKey.TypeId, asset.AssetKey.FileId))
                    .ToDictionary(group => group.Key, group => group.Last());
                var references = analysis.References
                    .GroupBy(reference => reference.SourceAssetKey)
                    .ToDictionary(
                        entries => entries.Key,
                        entries => (IReadOnlyList<AssetKey>)entries
                            .Select(reference => new AssetKey(reference.TargetAssetKey.TypeId, reference.TargetAssetKey.FileId))
                            .Distinct()
                            .OrderBy(key => key.TypeId)
                            .ThenBy(key => key.FileId)
                            .ToArray());

                foreach (var entry in analysis.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var key = new AdaptationAssetKey(entry.AssetKey.TypeId, entry.AssetKey.FileId);
                    facts.TryGetValue(key, out var fact);
                    var payload = await payloadReader.ReadPayloadAsync(entry, cancellationToken).ConfigureAwait(false);
                    var fingerprint = Fingerprint(entry, payload);
                    var coreKey = new AssetKey(key.TypeId, key.FileId);
                    var targets = references.TryGetValue(key, out var targetKeys)
                        ? targetKeys
                        : Array.Empty<AssetKey>();
                    occurrences.Add(new PatchResourceOccurrence(
                        group.NodeId,
                        group.ModName,
                        group.ArchiveHex,
                        group.PatchIndex,
                        group.PatchTocPath,
                        coreKey,
                        TypeName(key.TypeId),
                        targets,
                        fact?.TocDataSize ?? entry.TocDataSize,
                        fact?.StreamSize ?? entry.StreamSize,
                        fact?.GpuResourceSize ?? entry.GpuResourceSize,
                        fingerprint));
                }

                foreach (var issue in analysis.Issues)
                {
                    issues.Add(new CoreIssue(
                        issue.Code is "InvalidToc" or "MissingToc" ? CoreIssueSeverity.Error : CoreIssueSeverity.Warning,
                        issue.Code,
                        issue.Message,
                        issue.SourceFilePath ?? group.PatchTocPath,
                        group.NodeId));
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                issues.Add(new CoreIssue(
                    CoreIssueSeverity.Error,
                    "PatchResourceInspectionFailed",
                    $"读取 {group.DisplayName} 失败：{exception.Message}",
                    group.PatchTocPath,
                    group.NodeId,
                    exception.ToString()));
            }
        }

        var variants = occurrences
            .GroupBy(occurrence => occurrence.AssetKey)
            .SelectMany(assetGroup => assetGroup
                .GroupBy(occurrence => occurrence.PayloadFingerprint, StringComparer.Ordinal)
                .Select(fingerprintGroup => new PatchResourceAssetVariant(
                    assetGroup.Key,
                    fingerprintGroup.First().TypeName,
                    fingerprintGroup.SelectMany(item => item.ReferenceTargets).Distinct().OrderBy(key => key.TypeId).ThenBy(key => key.FileId).ToArray(),
                    fingerprintGroup.OrderBy(item => item.ModName, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.ArchiveHex, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.PatchIndex).ToArray(),
                    fingerprintGroup.Key)
                {
                    IsConflict = assetGroup.Select(item => item.PayloadFingerprint).Distinct(StringComparer.Ordinal).Count() > 1
                }))
            .OrderBy(variant => variant.AssetKey.TypeId)
            .ThenBy(variant => variant.AssetKey.FileId)
            .ThenBy(variant => variant.PayloadFingerprint, StringComparer.Ordinal)
            .ToArray();

        return new PatchResourceRepackagingInspection(
            patchGroups.ToArray(),
            variants,
            issues);
    }

    public async ValueTask<string> WriteAsync(
        IReadOnlyCollection<PatchResourceRepackagingSelection> selections,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selections);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        if (selections.Count == 0) throw new InvalidDataException("至少需要选择一个 AssetKey。 ");

        var duplicate = selections
            .GroupBy(selection => selection.Occurrence.AssetKey)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new InvalidDataException($"同一 AssetKey 选择了多个冲突版本：0x{duplicate.Key.TypeId:x16}/0x{duplicate.Key.FileId:x16}。");

        var ordered = selections
            .OrderBy(selection => selection.Occurrence.AssetKey.TypeId)
            .ThenBy(selection => selection.Occurrence.AssetKey.FileId)
            .ToArray();
        var header = ordered[0].Occurrence.PatchTocPath;
        var adaptations = ordered.Select(selection => new AdaptationPatchReconstruction.PatchSubsetSelection(
            selection.Occurrence.PatchTocPath,
            new AdaptationAssetKey(selection.Occurrence.AssetKey.TypeId, selection.Occurrence.AssetKey.FileId))).ToArray();
        var written = await subsetWriter.WriteSelectedAsync(header, outputDirectory, adaptations, cancellationToken: cancellationToken).ConfigureAwait(false);
        return written.OutputDirectoryPath;
    }

    private PatchFileNameInfo? TryParse(string path)
        => fileNameParser.TryParse(Path.GetFileName(path), out var info) ? info : null;

    private static string TypeName(ulong typeId)
        => typeId switch
        {
            0xe0a48d0be9a7453f => "Unit",
            0xc4f0f4be7fb0c8d6 => "Composite",
            0xeac0b497876adedf => "Material",
            0xcd4238c6a0c69e32 => "Texture",
            _ => $"0x{typeId:x16}"
        };

    private static string Fingerprint(AdaptationPatchTocEntry entry, AdaptationPatchEntryPayload payload)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendUInt64(hash, entry.Unknown1);
        AppendUInt64(hash, entry.Unknown2);
        AppendUInt32(hash, entry.Unknown3);
        AppendUInt32(hash, entry.Unknown4);
        AppendBytes(hash, payload.TocData);
        AppendBytes(hash, payload.StreamData);
        AppendBytes(hash, payload.GpuResourceData);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void AppendBytes(IncrementalHash hash, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(length, checked((uint)data.Length));
        hash.AppendData(length);
        hash.AppendData(data);
    }

    private static void AppendUInt64(IncrementalHash hash, ulong value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void AppendUInt32(IncrementalHash hash, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }
}
