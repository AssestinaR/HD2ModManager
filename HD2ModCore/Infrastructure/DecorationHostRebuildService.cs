using System;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using HD2ModAdaptation.Analysis;
using HD2ModAdaptation.PatchReconstruction;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModAdaptation.PatchReconstruction.UnitMesh.Canonical;
using HD2ModCore.Application;
using HD2ModCore.Decoration;
using HD2ModCore.Domain;
using AdaptationGameDataPackageResolver = HD2ModAdaptation.PatchReconstruction.GameDataPackageResolver;
using CoreAssetKey = HD2ModCore.Domain.AssetKey;

namespace HD2ModCore.Infrastructure;

// Rebuilds a host's generated Overlay only from its source patches. The marker prevents this
// service from ever deleting a user-created directory that merely happens to be named Overwrite.
public sealed class DecorationHostRebuildService
{
    public const string DecorationOverwriteMarkerName = ".hd2-decoration-overwrite";
    public const string HelmetMergeOverwriteMarkerName = ".hd2-helmet-to-armor-overwrite";
    private readonly IEquipmentUnitCatalogService _catalog;
    private readonly IModInformationReader _informationReader;

    public DecorationHostRebuildService(StoragePaths paths, IModInformationReader informationReader)
    {
        _catalog = CoreServices.CreateEquipmentUnitCatalogService(paths);
        _informationReader = informationReader ?? throw new ArgumentNullException(nameof(informationReader));
    }

    public async Task<DecorationHostRebuildResult> RebuildHostAsync(
        ModNode host,
        IEnumerable<DecorationHostRebuildAttachment> enabled,
        string modsRoot,
        string gameDataDirectory,
        CancellationToken cancellationToken = default,
        IProgress<DecorationOperationProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(enabled);
        ArgumentException.ThrowIfNullOrWhiteSpace(modsRoot);

        var attachments = enabled.ToArray();
        var output = Path.Combine(Path.GetFullPath(modsRoot), host.RelativePath, "Overwrite");
        var validationIssues = ValidateRequest(host, attachments, modsRoot, output);
        if (validationIssues.Count != 0) return DecorationHostRebuildResult.Failure(output, validationIssues);
        if (attachments.Length == 0)
        {
            progress?.Report(new DecorationOperationProgress("正在清理已生成装饰", 0, 1));
            var cleanup = RemoveGeneratedDecorationOverwrite(output);
            progress?.Report(new DecorationOperationProgress("正在清理已生成装饰", 1, 1));
            return DecorationHostRebuildResult.Success(output, 0, 0, cleanup.Removed);
        }
        if (string.IsNullOrWhiteSpace(gameDataDirectory) || !Directory.Exists(gameDataDirectory))
            return Failure(output, host, "DecorationGameDataMissing", "请在设置页重建资产索引。", gameDataDirectory);
        var readContext = ModInformationRequestContext.Create(
            ModInformationCacheScope.Operation,
            operationName: "DecorationAttachment");
        var issues = new List<CoreIssue>();
        try
        {
            var avatar = await new CanonicalAvatarRigReader(new AdaptationGameDataPackageResolver(gameDataDirectory)).ReadTransformInfoAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            var payloads = new List<(DecorationHostRebuildAttachment Attachment, DecorationAttachmentPlan Plan, DecorationPayloadDocument Payload)>();
            var payloadCount = attachments.Sum(attachment => IsPatchSnapshot(attachment.Plan) ? 1 : attachment.Plan.Payloads.Count);
            var readPayloads = 0;
            progress?.Report(new DecorationOperationProgress("正在读取装饰模型", 0, payloadCount));
            foreach (var attachment in attachments)
            {
                if (IsPatchSnapshot(attachment.Plan))
                {
                    var documents = await ReadSnapshotPayloadsAsync(attachment, attachment.Plan, modsRoot, readContext, cancellationToken).ConfigureAwait(false);
                    payloads.AddRange(documents.Select(document => (attachment, attachment.Plan.Plan, document)));
                    progress?.Report(new DecorationOperationProgress("正在读取装饰来源 Patch", ++readPayloads, payloadCount));
                    continue;
                }
                foreach (var file in attachment.Plan.Payloads)
                {
                    if (!Path.GetExtension(file.File).Equals(".bin", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("仅兼容旧版 .bin 装饰，请重新生成该装饰。");
                    var payloadPath = Path.Combine(modsRoot, attachment.RelativePath, file.File);
                    var payload = await ReadPayloadAsync(payloadPath, cancellationToken).ConfigureAwait(false);
                    payloads.Add((attachment, attachment.Plan.Plan, payload));
                    progress?.Report(new DecorationOperationProgress("正在读取装饰模型", ++readPayloads, payloadCount));
                }
            }
            var backup = PrepareGeneratedOverwrite(output);
            try
            {
                var patchPaths = ResolveOriginalPatchFiles(host, modsRoot);
                var edits = 0;
                var rebuiltPatches = 0;
                for (var index = 0; index < patchPaths.Count; index++)
                {
                    progress?.Report(new DecorationOperationProgress("正在重建主体 Patch", index, patchPaths.Count));
                    var patchEdits = await RebuildPatchAsync(host, patchPaths[index], output, payloads, avatar, readContext, cancellationToken, progress, issues).ConfigureAwait(false);
                    edits += patchEdits;
                    if (patchEdits != 0) rebuiltPatches++;
                }
                progress?.Report(new DecorationOperationProgress("正在重建主体 Patch", patchPaths.Count, patchPaths.Count));
                if (edits == 0) throw new InvalidDataException("没有找到与装饰计划匹配的主体 Unit。");
                await File.WriteAllTextAsync(Path.Combine(output, DecorationOverwriteMarkerName), "generated", cancellationToken).ConfigureAwait(false);
                if (backup is not null) Directory.Delete(backup, recursive: true);
                return DecorationHostRebuildResult.Success(output, rebuiltPatches, edits, false, issues);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
                if (backup is not null && Directory.Exists(backup)) Directory.Move(backup, output);
                throw;
            }
            catch (Exception exception)
            {
                if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
                if (backup is not null && Directory.Exists(backup)) Directory.Move(backup, output);
                issues.Add(new CoreIssue(CoreIssueSeverity.Error, "DecorationHostRebuildFailed", exception.Message, output, host.Id, exception.ToString()));
                return DecorationHostRebuildResult.Failure(output, issues);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            issues.Add(new CoreIssue(CoreIssueSeverity.Error, "DecorationHostRebuildFailed", exception.Message, output, host.Id, exception.ToString()));
            return DecorationHostRebuildResult.Failure(output, issues);
        }
        finally
        {
            _informationReader.ClearOperation(readContext.OperationId);
        }
    }

    private async Task<int> RebuildPatchAsync(ModNode host, string patch, string output, IReadOnlyList<(DecorationHostRebuildAttachment Attachment, DecorationAttachmentPlan Plan, DecorationPayloadDocument Payload)> attachments, UnitTransformInfo avatar, ModInformationRequestContext readContext, CancellationToken cancellationToken, IProgress<DecorationOperationProgress>? progress, ICollection<CoreIssue> issues)
    {
        var patchRequest = new ModInformationReadRequest(
            patch,
            readContext,
            ContentView: ModInformationContentView.Source,
            NodeId: host.Id);
        var index = await _informationReader.ReadPatchIndexAsync(patchRequest, cancellationToken).ConfigureAwait(false);
        if (index.Data is null)
        {
            var detail = index.State.Diagnostics.FirstOrDefault()?.Message ?? "未知错误";
            throw new InvalidDataException($"无法读取主体 Patch 目录：{detail}");
        }
        var entries = index.Data.Entries;
        var keys = entries.Where(entry => entry.AssetKey.TypeId == PatchUnitMeshReader.UnitTypeId)
            .Select(entry => new HD2ModCore.Domain.AssetKey(entry.AssetKey.TypeId, entry.AssetKey.FileId)).ToHashSet();
        var catalog = await _catalog.GetEntriesAsync(keys, cancellationToken).ConfigureAwait(false);
        var candidates = catalog
            .SelectMany(entry => entry.Parts.Select(part => new DecorationHostCandidate(entry.ArchiveId, part)))
            .GroupBy(candidate => (candidate.ArchiveId, candidate.Part.UnitAssetKey, candidate.Part.MeshInfoIndex))
            .Select(group => group.OrderByDescending(candidate => candidate.Part.StoredBytes).ThenByDescending(candidate => candidate.Part.Confidence).First())
            .ToArray();
        progress?.Report(new DecorationOperationProgress("正在分析主体模型", 0, candidates.Length));
        var unitsByKey = new Dictionary<CoreAssetKey, PatchUnitMesh>();
        var geometryByMesh = await ReadCandidateGeometryFactsAsync(entries, candidates, unitsByKey, patchRequest, cancellationToken, progress, issues).ConfigureAwait(false);
        candidates = candidates.Select(candidate => candidate with
        {
            Geometry = geometryByMesh.GetValueOrDefault((candidate.Part.UnitAssetKey, candidate.Part.MeshInfoIndex))
        }).ToArray();
        var targets = ResolveTargets(candidates, attachments);
        var targetsByUnit = targets
            .GroupBy(target => target.Part.UnitAssetKey)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var edits = new List<PatchUnitMeshEditResult>();
        var targetEntries = entries.Where(entry => entry.AssetKey.TypeId == PatchUnitMeshReader.UnitTypeId && targetsByUnit.ContainsKey(new CoreAssetKey(entry.AssetKey.TypeId, entry.AssetKey.FileId))).ToArray();
        for (var targetIndex = 0; targetIndex < targetEntries.Length; targetIndex++)
        {
            var entry = targetEntries[targetIndex];
            var unitKey = new HD2ModCore.Domain.AssetKey(entry.AssetKey.TypeId, entry.AssetKey.FileId);
            if (!targetsByUnit.TryGetValue(unitKey, out var matching)) continue;
            var targetMeshes = matching.Select(target => target.Part.MeshInfoIndex).Distinct().ToArray();
            if (targetMeshes.Length != 1)
                throw new InvalidDataException($"Unit 0x{entry.AssetKey.FileId:x16} resolved to {targetMeshes.Length} decoration host meshes; choose separate target parts.");
            var targetPart = matching[0].Part;
            var unit = unitsByKey.TryGetValue(unitKey, out var cachedUnit)
                ? cachedUnit
                : await ReadUnitAsync(entry, entries, patchRequest, canonicalSource: false, cancellationToken).ConfigureAwait(false);
            progress?.Report(new DecorationOperationProgress("正在合并目标 Unit", targetIndex, targetEntries.Length));
            var targetRaw = unit.Model.RawMeshData.SingleOrDefault(raw => raw.MeshInfoIndex == targetPart.MeshInfoIndex)
                ?? throw new InvalidDataException($"Target MeshInfo {targetPart.MeshInfoIndex} has no readable RawMesh.");
            var targetMesh = unit.Model.Meshes.Single(mesh => mesh.Index == targetPart.MeshInfoIndex);
            var selectedAttachments = matching
                .GroupBy(target => (target.Attachment.Attachment.DecorationId, target.Attachment.Payload.BodyVariant), StringTupleComparer.OrdinalIgnoreCase)
                .Select(group => group.First().Attachment)
                .ToArray();
			var replaceTargetGeometry = matching.Any(target => target.Attachment.Plan.ReplaceWhenSourcePartLayerMatches
				&& DecorationPlanningDefaults.MatchesPartLayer(target.Attachment.Plan.SourcePartLayers, targetPart.PartKind, targetPart.Layer));
            var inputsByTargetMesh = new Dictionary<int, IReadOnlyList<CanonicalDecorationAppendInput>>();
            foreach (var lodTarget in ResolveVisibleLodFamily(unit.Model, targetRaw, targetMesh))
            {
                var inputs = selectedAttachments
                    .SelectMany(attachment => attachment.Payload.Fragments
                        .Where(fragment => fragment.RawMesh.LodIndex == lodTarget.Raw.LodIndex)
                        .Select(fragment => new CanonicalDecorationAppendInput(ToCanonical(fragment), DecorationNamespace(attachment.Attachment.DecorationId))))
                    .ToArray();
                if (inputs.Length != 0) inputsByTargetMesh.Add(lodTarget.Mesh.Index, inputs);
            }
            if (inputsByTargetMesh.Count == 0)
            {
                var available = string.Join(",", selectedAttachments.SelectMany(attachment => attachment.Payload.Fragments).Select(fragment => fragment.RawMesh.LodIndex).Distinct().OrderBy(value => value));
                throw new InvalidDataException($"装饰没有与目标 LOD {targetRaw.LodIndex} 对应的来源 Mesh（可用 LOD：{available}）。");
            }
            var result = new CanonicalDecorationUnitAppender().TryAppendLodFamily(unit, inputsByTargetMesh, avatar, replaceTargetGeometry);
            if (!result.IsValid)
            {
                var detail = string.Join("; ", result.Diagnostics.Select(item => $"{item.Code}: {item.Message}"));
                throw new InvalidDataException(detail);
            }
            edits.Add(result.Edit!);
            progress?.Report(new DecorationOperationProgress("正在合并目标 Unit", targetIndex + 1, targetEntries.Length));
        }
        if (edits.Count != 0)
        {
            await new PatchArchiveWriter().WriteAsync(patch, output, edits, overwriteExisting: true, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        return edits.Count;
    }

    private async Task<IReadOnlyDictionary<(CoreAssetKey Unit, int Mesh), UnitMeshGeometryFact>> ReadCandidateGeometryFactsAsync(
        IReadOnlyList<HD2ModAdaptation.PatchReconstruction.PatchTocEntry> entries,
        IReadOnlyList<DecorationHostCandidate> candidates,
        IDictionary<CoreAssetKey, PatchUnitMesh> unitsByKey,
        ModInformationReadRequest patchRequest,
        CancellationToken cancellationToken,
        IProgress<DecorationOperationProgress>? progress,
        ICollection<CoreIssue> issues)
    {
        var candidateKeys = candidates.Select(candidate => candidate.Part.UnitAssetKey).ToHashSet();
        var facts = new Dictionary<(CoreAssetKey Unit, int Mesh), UnitMeshGeometryFact>();
        var candidateEntries = entries.Where(entry => entry.AssetKey.TypeId == PatchUnitMeshReader.UnitTypeId && candidateKeys.Contains(new CoreAssetKey(entry.AssetKey.TypeId, entry.AssetKey.FileId))).ToArray();
        for (var index = 0; index < candidateEntries.Length; index++)
        {
            var entry = candidateEntries[index];
            try
            {
                var unit = await ReadUnitAsync(entry, entries, patchRequest, canonicalSource: false, cancellationToken).ConfigureAwait(false);
                var unitKey = new CoreAssetKey(entry.AssetKey.TypeId, entry.AssetKey.FileId);
                unitsByKey[unitKey] = unit;
                foreach (var fact in UnitGeometryFactsBuilder.Analyze(unit.Model).Meshes)
                    facts[(unitKey, fact.MeshInfoIndex)] = fact;
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or EndOfStreamException or UnauthorizedAccessException)
            {
                issues.Add(new CoreIssue(
                    CoreIssueSeverity.Warning,
                    "DecorationHostUnitUnreadable",
                    $"跳过不可读取的主体 Unit 0x{entry.AssetKey.FileId:x16}：{exception.Message}",
                    patchRequest.SourcePath,
                    patchRequest.NodeId,
                    exception.ToString()));
            }
            finally { progress?.Report(new DecorationOperationProgress("正在分析主体模型", index + 1, candidateEntries.Length)); }
        }
        return facts;
    }

    private async ValueTask<PatchUnitMesh> ReadUnitAsync(
        HD2ModAdaptation.PatchReconstruction.PatchTocEntry entry,
        IReadOnlyList<HD2ModAdaptation.PatchReconstruction.PatchTocEntry> entries,
        ModInformationReadRequest patchRequest,
        bool canonicalSource,
        CancellationToken cancellationToken)
    {
        var result = await _informationReader.ReadUnitAsync(
            entry,
            entries,
            PatchUnitDependencyPolicy.RequirePatchLocalComposite,
            patchRequest,
            canonicalSource,
            cancellationToken).ConfigureAwait(false);
        if (result.Data is not null) return result.Data;
        var detail = result.State.Diagnostics.FirstOrDefault()?.Message ?? "未知错误";
        throw new InvalidDataException($"无法读取 Unit 0x{entry.AssetKey.FileId:x16}：{detail}");
    }

    private static IReadOnlyList<DecorationResolvedTarget> ResolveTargets(
        IReadOnlyList<DecorationHostCandidate> candidates,
        IReadOnlyList<(DecorationHostRebuildAttachment Attachment, DecorationAttachmentPlan Plan, DecorationPayloadDocument Payload)> attachments)
    {
        var resolved = new List<DecorationResolvedTarget>();
        foreach (var attachmentGroup in attachments.GroupBy(attachment => attachment.Attachment.DecorationId, StringComparer.OrdinalIgnoreCase))
        {
            var attachmentSet = attachmentGroup.ToArray();
            var plan = attachmentSet[0].Plan;
            foreach (var archiveGroup in candidates
                .Where(candidate => IsRenderableHost(candidate.Part) && PartMatches(candidate.Part.PartKind, plan.TargetPart))
                .GroupBy(candidate => candidate.ArchiveId, StringComparer.OrdinalIgnoreCase))
            {
                var archiveCandidates = archiveGroup.ToArray();
                var sourceLayers = ReadSourceLayers(attachmentSet);
                var selections = SelectHosts(archiveCandidates, attachmentSet, plan, sourceLayers);

                if (selections.Count == 0) continue;
                resolved.AddRange(selections.Select(selection => new DecorationResolvedTarget(selection.Candidate.Part, selection.Attachment)));
            }
        }
        return resolved;
    }

    // Geometry is the first host gate. Layer and body policy only rank candidates
    // within the real-geometry set; otherwise a hidden placeholder would block an
    // actual host simply because it happens to share the source layer.
    private static IReadOnlyList<(DecorationHostCandidate Candidate, (DecorationHostRebuildAttachment Attachment, DecorationAttachmentPlan Plan, DecorationPayloadDocument Payload) Attachment)> SelectHosts(
        IReadOnlyList<DecorationHostCandidate> candidates,
        IReadOnlyList<(DecorationHostRebuildAttachment Attachment, DecorationAttachmentPlan Plan, DecorationPayloadDocument Payload)> attachments,
        DecorationAttachmentPlan plan,
        IReadOnlySet<UnitMeshPartLayer> sourceLayers)
    {
        var realGeometry = candidates.Where(HasRenderableGeometry).ToArray();
        var geometryEligible = realGeometry.Length != 0 ? realGeometry : candidates.ToArray();
        var sameLayer = sourceLayers.Count == 0
            ? geometryEligible
            : geometryEligible.Where(candidate => sourceLayers.Contains(candidate.Part.Layer)).ToArray();
        var ranked = sameLayer.Length != 0 ? sameLayer : geometryEligible;
        var stockyPayload = FindPayload(attachments, "Stocky");
        var slimPayload = FindPayload(attachments, "Slim");
        var stocky = ChooseLargest(ranked, UnitMeshBodyVariant.Stocky);
        var slim = ChooseLargest(ranked, UnitMeshBodyVariant.Slim);
        var selections = new List<(DecorationHostCandidate Candidate, (DecorationHostRebuildAttachment Attachment, DecorationAttachmentPlan Plan, DecorationPayloadDocument Payload) Attachment)>();

        if (stocky is not null && slim is not null)
        {
            AddIfAvailable(selections, stocky, stockyPayload);
            AddIfAvailable(selections, slim, slimPayload);
            if (selections.Count != 0) return selections;
        }

        var universalPayload = FindPayload(attachments, "Any")
            ?? FindPayload(attachments, plan.TargetBodyVariant)
            ?? stockyPayload
            ?? slimPayload;
        var any = ChooseLargest(ranked, UnitMeshBodyVariant.Any);
        if (any is not null && universalPayload is not null)
        {
            selections.Clear();
            AddIfAvailable(selections, any, universalPayload);
            return selections;
        }

        // When the matching layer has only one concrete body, retain the CrossArmor
        // completion behavior: look through the remaining layers for its counterpart
        // before accepting a single-body host.
        if (stocky is not null && slimPayload is not null)
            slim = ChooseLargest(geometryEligible.Where(candidate => candidate.Part.UnitAssetKey != stocky.Part.UnitAssetKey), UnitMeshBodyVariant.Slim);
        if (slim is not null && stockyPayload is not null)
            stocky = ChooseLargest(geometryEligible.Where(candidate => candidate.Part.UnitAssetKey != slim.Part.UnitAssetKey), UnitMeshBodyVariant.Stocky);
        if (stocky is not null && slim is not null)
        {
            selections.Clear();
            AddIfAvailable(selections, stocky, stockyPayload);
            AddIfAvailable(selections, slim, slimPayload);
            if (selections.Count != 0) return selections;
        }

        var crossLayerAny = ChooseLargest(geometryEligible, UnitMeshBodyVariant.Any);
        if (crossLayerAny is not null && universalPayload is not null)
        {
            selections.Clear();
            AddIfAvailable(selections, crossLayerAny, universalPayload);
            return selections;
        }

        selections.Clear();
        AddIfAvailable(selections, stocky, stockyPayload);
        AddIfAvailable(selections, slim, slimPayload);
        return selections;
    }

    private static void AddIfAvailable(
        ICollection<(DecorationHostCandidate Candidate, (DecorationHostRebuildAttachment Attachment, DecorationAttachmentPlan Plan, DecorationPayloadDocument Payload) Attachment)> selections,
        DecorationHostCandidate? candidate,
        (DecorationHostRebuildAttachment Attachment, DecorationAttachmentPlan Plan, DecorationPayloadDocument Payload)? attachment)
    {
        if (candidate is not null && attachment is not null) selections.Add((candidate, attachment.Value));
    }

    private static DecorationHostCandidate? ChooseLargest(IEnumerable<DecorationHostCandidate> candidates, UnitMeshBodyVariant bodyVariant)
        => candidates.Where(candidate => candidate.Part.BodyVariant == bodyVariant)
            .OrderByDescending(candidate => GeometryRank(candidate))
            .ThenByDescending(candidate => candidate.Geometry?.TriangleCount ?? candidate.Part.TriangleCount)
            .ThenByDescending(candidate => candidate.Geometry?.VertexCount ?? candidate.Part.VertexCount)
            .ThenByDescending(candidate => candidate.Part.StoredBytes)
            .ThenByDescending(candidate => candidate.Part.Confidence)
            .ThenBy(candidate => candidate.Part.UnitAssetKey.FileId)
            .ThenBy(candidate => candidate.Part.MeshInfoIndex)
            .FirstOrDefault();

    private static IReadOnlySet<UnitMeshPartLayer> ReadSourceLayers(
        IEnumerable<(DecorationHostRebuildAttachment Attachment, DecorationAttachmentPlan Plan, DecorationPayloadDocument Payload)> attachments)
    {
        var layers = new HashSet<UnitMeshPartLayer>();
        foreach (var layerText in attachments.SelectMany(attachment => attachment.Payload.SourceLayers ?? []))
        {
            if (Enum.TryParse<UnitMeshPartLayer>(layerText, ignoreCase: true, out var layer)
                && layer is not UnitMeshPartLayer.Unknown and not UnitMeshPartLayer.Culling and not UnitMeshPartLayer.Static)
                layers.Add(layer);
        }
        return layers;
    }

    private static (DecorationHostRebuildAttachment Attachment, DecorationAttachmentPlan Plan, DecorationPayloadDocument Payload)? FindPayload(
        IEnumerable<(DecorationHostRebuildAttachment Attachment, DecorationAttachmentPlan Plan, DecorationPayloadDocument Payload)> attachments,
        string bodyVariant)
    {
        foreach (var attachment in attachments)
        {
            if (string.Equals(attachment.Payload.BodyVariant, bodyVariant, StringComparison.OrdinalIgnoreCase))
                return attachment;
        }
        return null;
    }

    private static string DescribeCandidate(DecorationHostCandidate candidate)
        => $"0x{candidate.Part.UnitAssetKey.FileId:x16}/M{candidate.Part.MeshInfoIndex}/{candidate.Part.Layer}/{candidate.Part.BodyVariant}/Q{GeometryRank(candidate)}/{candidate.Geometry?.VertexCount ?? candidate.Part.VertexCount}V/{candidate.Geometry?.TriangleCount ?? candidate.Part.TriangleCount}T/{candidate.Part.StoredBytes}B";

    private static int GeometryRank(DecorationHostCandidate candidate)
        => UnitGeometryRanker.GetRank(candidate.Geometry?.Quality ?? candidate.Part.GeometryQuality);

    private static bool HasRenderableGeometry(DecorationHostCandidate candidate)
        => candidate.Geometry?.HasRenderableGeometry ?? candidate.Part.HasRenderableGeometry;

    private static bool IsRenderableHost(EquipmentUnitPart part)
        => !part.IsCullingMesh
            && part.Layer is not UnitMeshPartLayer.Culling and not UnitMeshPartLayer.Static;

    private static IReadOnlyList<(UnitRawMeshData Raw, UnitMeshInfo Mesh)> ResolveVisibleLodFamily(
        UnitMeshModel model, UnitRawMeshData anchorRaw, UnitMeshInfo anchorMesh)
        => model.RawMeshData
            .Select(raw => (Raw: raw, Mesh: model.Meshes.SingleOrDefault(mesh => mesh.Index == raw.MeshInfoIndex)))
            .Where(item => item.Mesh is not null
                && item.Raw.LodIndex >= 0
                && item.Mesh!.SemanticInfo.IsVisualMesh
                && SameLodFamily(anchorMesh.SemanticInfo, item.Mesh.SemanticInfo))
            .OrderBy(item => item.Raw.LodIndex)
            .ThenBy(item => item.Mesh!.Index)
            .Select(item => (item.Raw, item.Mesh!))
            .ToArray();

    private static bool SameLodFamily(UnitMeshSemanticInfo anchor, UnitMeshSemanticInfo candidate)
    {
        if (!anchor.HasValue || !candidate.HasValue) return true;
        return string.Equals(anchor.Slot, candidate.Slot, StringComparison.OrdinalIgnoreCase)
            && string.Equals(anchor.PieceType, candidate.PieceType, StringComparison.OrdinalIgnoreCase)
            && string.Equals(anchor.BodyType, candidate.BodyType, StringComparison.OrdinalIgnoreCase)
            && string.Equals(anchor.Weight, candidate.Weight, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PartMatches(UnitMeshPartKind part, string requested)
        => string.Equals(part.ToString(), requested, StringComparison.OrdinalIgnoreCase)
            || part == UnitMeshPartKind.Pelvis && string.Equals(requested, "Hips", StringComparison.OrdinalIgnoreCase);

    private sealed record DecorationHostCandidate(string ArchiveId, EquipmentUnitPart Part)
    {
        public UnitMeshGeometryFact? Geometry { get; init; }
    }

    private sealed record DecorationResolvedTarget(
        EquipmentUnitPart Part,
        (DecorationHostRebuildAttachment Attachment, DecorationAttachmentPlan Plan, DecorationPayloadDocument Payload) Attachment);

    private sealed class StringTupleComparer : IEqualityComparer<(string ModGuid, string BodyVariant)>
    {
        public static StringTupleComparer OrdinalIgnoreCase { get; } = new();

        public bool Equals((string ModGuid, string BodyVariant) x, (string ModGuid, string BodyVariant) y)
            => string.Equals(x.ModGuid, y.ModGuid, StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.BodyVariant, y.BodyVariant, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string ModGuid, string BodyVariant) value)
            => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(value.ModGuid), StringComparer.OrdinalIgnoreCase.GetHashCode(value.BodyVariant));
    }

    private static CanonicalDecorationFragment ToCanonical(DecorationMeshFragment fragment)
        => new(fragment.Mesh, fragment.RawMesh, fragment.Stream, fragment.Materials, fragment.BoneInfos, fragment.TransformInfo, fragment.TransformNameHashes);

    private static ulong DecorationNamespace(string id) => Guid.TryParse(id, out var value) ? BitConverter.ToUInt64(value.ToByteArray(), 0) : unchecked((ulong)StringComparer.OrdinalIgnoreCase.GetHashCode(id));

    private static async Task<DecorationPayloadDocument> ReadPayloadAsync(string path, CancellationToken cancellationToken)
    {
        await using var file = File.OpenRead(path);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        return await JsonSerializer.DeserializeAsync<DecorationPayloadDocument>(gzip, new JsonSerializerOptions(JsonSerializerDefaults.Web), cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Decoration payload is empty.");
    }

    private static bool IsPatchSnapshot(DecorationPlanDocument document)
        => string.Equals(document.SourceStorageMode, "PatchSnapshot", StringComparison.OrdinalIgnoreCase)
            && document.SourceUnits.Count != 0;

    private async Task<IReadOnlyList<DecorationPayloadDocument>> ReadSnapshotPayloadsAsync(
        DecorationHostRebuildAttachment decoration,
        DecorationPlanDocument document,
        string modsRoot,
        ModInformationRequestContext readContext,
        CancellationToken cancellationToken)
    {
        var root = Path.Combine(modsRoot, decoration.RelativePath);
        var parser = CoreServices.CreatePatchFileNameParser();
        var patches = Directory.EnumerateFiles(root)
            .Where(path => parser.TryParse(Path.GetFileName(path), out var info) && info?.SidecarKind == PatchSidecarKind.Base)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        var requested = document.SourceUnits.GroupBy(unit => (unit.TypeId, unit.FileId)).ToDictionary(group => group.Key, group => group.ToArray());
        var resolved = new HashSet<(ulong TypeId, ulong FileId, int Mesh, bool Culling)>();
        var fragments = new List<(string Variant, string Layer, DecorationMeshFragment Fragment)>();
        var nodeId = Guid.TryParse(decoration.DecorationId, out var parsedNodeId) ? new ModNodeId(parsedNodeId) : (ModNodeId?)null;
        foreach (var patch in patches)
        {
            var patchRequest = new ModInformationReadRequest(
                patch,
                readContext,
                ContentView: ModInformationContentView.Source,
                NodeId: nodeId);
            var index = await _informationReader.ReadPatchIndexAsync(patchRequest, cancellationToken).ConfigureAwait(false);
            if (index.Data is null)
            {
                var detail = index.State.Diagnostics.FirstOrDefault()?.Message ?? "未知错误";
                throw new InvalidDataException($"无法读取快照装饰 Patch 目录：{detail}");
            }
            var entries = index.Data.Entries;
            foreach (var entry in entries.Where(entry => requested.ContainsKey((entry.AssetKey.TypeId, entry.AssetKey.FileId))))
            {
                var selections = requested[(entry.AssetKey.TypeId, entry.AssetKey.FileId)];
                var unit = await ReadUnitAsync(entry, entries, patchRequest, canonicalSource: true, cancellationToken).ConfigureAwait(false);
                foreach (var selection in selections)
                {
                    if (selection.IsCulling)
                    {
                        var mesh = unit.Model.Meshes.SingleOrDefault(item => item.Index == selection.MeshInfoIndex)
                            ?? throw new InvalidDataException("快照装饰 culling Mesh 缺失。");
                        var raw = unit.Model.RawMeshData.SingleOrDefault(item => item.MeshInfoIndex == selection.MeshInfoIndex)
                            ?? throw new InvalidDataException("快照装饰 culling 几何缺失。");
                        fragments.Add((NormalizeVariant(selection.BodyVariant), NormalizeLayer(selection.Layer), CreateFragment(unit, mesh, raw)));
                        resolved.Add((selection.TypeId, selection.FileId, selection.MeshInfoIndex, true));
                        continue;
                    }
                    var lods = unit.Model.RawMeshData
                        .Select(raw => (Raw: raw, Mesh: unit.Model.Meshes.SingleOrDefault(mesh => mesh.Index == raw.MeshInfoIndex)))
                        .Where(item => item.Mesh is not null && IsVisibleSnapshotLod(item.Raw, item.Mesh))
                        .OrderBy(item => item.Raw.LodIndex).ToArray();
                    if (lods.Length == 0) throw new InvalidDataException("快照装饰 Unit 没有可见 LOD 几何。");
                    foreach (var lod in lods) fragments.Add((NormalizeVariant(selection.BodyVariant), NormalizeLayer(selection.Layer), CreateFragment(unit, lod.Mesh!, lod.Raw)));
                    resolved.Add((selection.TypeId, selection.FileId, selection.MeshInfoIndex, false));
                }
            }
        }
        var expected = document.SourceUnits.Select(unit => (unit.TypeId, unit.FileId, unit.MeshInfoIndex, unit.IsCulling)).ToHashSet();
        if (!expected.SetEquals(resolved)) throw new InvalidDataException("装饰来源 Patch 快照不完整或所选 Unit 不存在。");
        return BuildSnapshotPayloads(fragments, document.Plan);
    }

    private static IReadOnlyList<DecorationPayloadDocument> BuildSnapshotPayloads(
        IReadOnlyList<(string Variant, string Layer, DecorationMeshFragment Fragment)> fragments, DecorationAttachmentPlan plan)
    {
        DecorationPayloadDocument Create(string variant, IEnumerable<(string Variant, string Layer, DecorationMeshFragment Fragment)> source)
        {
            var items = source.ToArray();
            return new DecorationPayloadDocument { BodyVariant = variant, SourceLayers = items.Select(item => item.Layer).Where(layer => layer.Length != 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), Fragments = items.Select(item => item.Fragment).ToList() };
        }
        if (plan.TargetBodyVariant is "Stocky" or "Slim") return [Create(plan.TargetBodyVariant, fragments)];
        if (plan.DualVariantMode == "ApplyAllToBoth") return [Create("Stocky", fragments), Create("Slim", fragments)];
        var stocky = fragments.Where(item => item.Variant is "Stocky" or "Any").ToArray();
        var slim = fragments.Where(item => item.Variant is "Slim" or "Any").ToArray();
        if (stocky.Length == 0 || slim.Length == 0) throw new InvalidDataException("双身形自动分配需要 Slim 和 Stocky 来源；请重新生成或改为来源全部附加。");
        return [Create("Stocky", stocky), Create("Slim", slim)];
    }

    private static string NormalizeVariant(string variant) => variant.Equals("Slim", StringComparison.OrdinalIgnoreCase) ? "Slim" : variant.Equals("Stocky", StringComparison.OrdinalIgnoreCase) ? "Stocky" : "Any";
    private static string NormalizeLayer(string layer) => layer is "Armor" or "Undergarment" or "Accessory" ? layer : string.Empty;
    private static bool IsVisibleSnapshotLod(UnitRawMeshData raw, UnitMeshInfo mesh)
        => raw.LodIndex >= 0 && mesh.SemanticInfo.IsVisualMesh && !mesh.SemanticInfo.IsCullingBody && !mesh.SemanticInfo.IsStaticMesh && UnitGeometryFactsBuilder.HasRenderableGeometry(raw);
    private static DecorationMeshFragment CreateFragment(PatchUnitMesh unit, UnitMeshInfo mesh, UnitRawMeshData raw)
    {
        var stream = unit.Model.Streams.SingleOrDefault(item => item.Index == mesh.StreamIndex)
            ?? throw new InvalidDataException("快照装饰 Mesh 没有可用 Stream。");
        return new DecorationMeshFragment
        {
            Mesh = mesh, RawMesh = raw, Stream = stream, Materials = unit.Model.Materials.ToList(),
            BoneInfos = unit.Model.BoneInfos.ToList(), TransformInfo = unit.Model.TransformInfo,
            TransformNameHashes = unit.Model.TransformNameHashes.ToList()
        };
    }

    private static string? PrepareGeneratedOverwrite(string directory)
    {
        string? backup = null;
        if (Directory.Exists(directory))
        {
            // Helmet merge and decoration are two managed layers over the same
            // Overwrite directory. The library coordinator composes them, so a
            // helmet-generated directory is also safe for decoration replacement.
            if (!File.Exists(Path.Combine(directory, DecorationOverwriteMarkerName))
                && !File.Exists(Path.Combine(directory, HelmetMergeOverwriteMarkerName)))
                throw new InvalidOperationException("主体 Mod 的 Overwrite 目录不是管理器生成，已拒绝覆盖。");
            backup = directory + ".backup-" + Guid.NewGuid().ToString("N");
            Directory.Move(directory, backup);
        }
        Directory.CreateDirectory(directory);
        return backup;
    }

    public static DecorationGeneratedOverwriteCleanupResult RemoveGeneratedDecorationOverwrite(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!Directory.Exists(directory)) return new DecorationGeneratedOverwriteCleanupResult(false, false);
        if (!File.Exists(Path.Combine(directory, DecorationOverwriteMarkerName)))
            return new DecorationGeneratedOverwriteCleanupResult(false, false);
        Directory.Delete(directory, recursive: true);
        return new DecorationGeneratedOverwriteCleanupResult(true, true);
    }

    private static DecorationHostRebuildResult Failure(
        string output,
        ModNode host,
        string code,
        string message,
        string? filePath = null,
        Exception? exception = null)
        => DecorationHostRebuildResult.Failure(
            output,
            [new CoreIssue(CoreIssueSeverity.Error, code, message, filePath, host.Id, exception?.ToString())]);

    private static IReadOnlyList<CoreIssue> ValidateRequest(
        ModNode host,
        IReadOnlyList<DecorationHostRebuildAttachment> attachments,
        string modsRoot,
        string output)
    {
        var issues = new List<CoreIssue>();
        if (!TryResolveLibraryPath(modsRoot, host.RelativePath, out _))
        {
            issues.Add(new CoreIssue(
                CoreIssueSeverity.Error,
                "DecorationHostPathInvalid",
                "主体 Mod 的库内相对路径无效，已拒绝重建 Overwrite。",
                output,
                host.Id));
        }

        foreach (var attachment in attachments)
        {
            if (attachment is null)
            {
                issues.Add(new CoreIssue(
                    CoreIssueSeverity.Error,
                    "DecorationAttachmentMissing",
                    "已启用装饰描述为空。",
                    output,
                    host.Id));
                continue;
            }
            if (string.IsNullOrWhiteSpace(attachment.DecorationId))
            {
                issues.Add(new CoreIssue(
                    CoreIssueSeverity.Error,
                    "DecorationAttachmentIdMissing",
                    "已启用装饰缺少稳定 ID。",
                    output,
                    host.Id));
            }
            if (!TryResolveLibraryPath(modsRoot, attachment.RelativePath, out var attachmentRoot))
            {
                issues.Add(new CoreIssue(
                    CoreIssueSeverity.Error,
                    "DecorationAttachmentPathInvalid",
                    "已启用装饰的库内相对路径无效，已拒绝读取。",
                    attachment.RelativePath,
                    host.Id));
                continue;
            }
            if (attachment.Plan is null)
            {
                issues.Add(new CoreIssue(
                    CoreIssueSeverity.Error,
                    "DecorationAttachmentPlanMissing",
                    "已启用装饰缺少 decoration.json 计划。",
                    attachmentRoot,
                    host.Id));
                continue;
            }
            foreach (var payload in attachment.Plan.Payloads)
            {
                if (!TryResolveChildPath(attachmentRoot, payload.File, out _))
                {
                    issues.Add(new CoreIssue(
                        CoreIssueSeverity.Error,
                        "DecorationLegacyPayloadPathInvalid",
                        "旧版装饰 payload 路径必须位于装饰包目录内。",
                        payload.File,
                        host.Id));
                }
            }
        }
        return issues;
    }

    private static bool TryResolveLibraryPath(string root, string relativePath, out string resolved)
    {
        resolved = string.Empty;
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath)) return false;
        return TryResolveChildPath(root, relativePath, out resolved);
    }

    private static bool TryResolveChildPath(string root, string relativePath, out string resolved)
    {
        resolved = string.Empty;
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath)) return false;
        try
        {
            var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
            var prefix = fullRoot.EndsWith(Path.DirectorySeparatorChar) || fullRoot.EndsWith(Path.AltDirectorySeparatorChar)
                ? fullRoot
                : fullRoot + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
            resolved = fullPath;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static IReadOnlyList<string> ResolveOriginalPatchFiles(ModNode host, string modsRoot)
    {
        var directory = Path.Combine(modsRoot, host.RelativePath);
        if (!Directory.Exists(directory)) return [];
        var parser = CoreServices.CreatePatchFileNameParser();
        return Directory.EnumerateFiles(directory)
            .Where(path => parser.TryParse(Path.GetFileName(path), out var info) && info?.SidecarKind == PatchSidecarKind.Base)
            .ToArray();
    }
}
