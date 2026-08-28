using System;
using System.IO;
using System.IO.Compression;
using System.Diagnostics;
using System.Text.Json;
using HD2ModAdaptation.Analysis;
using HD2ModAdaptation.PatchReconstruction;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModAdaptation.PatchReconstruction.UnitMesh.Canonical;
using HD2ModCore.Application;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using HD2ModManager.Models;
using AdaptationGameDataPackageResolver = HD2ModAdaptation.PatchReconstruction.GameDataPackageResolver;
using CoreAssetKey = HD2ModCore.Domain.AssetKey;

namespace HD2ModManager.Services;

// Rebuilds a host's generated Overlay only from its source patches. The marker prevents this
// service from ever deleting a user-created directory that merely happens to be named Overwrite.
public sealed class DecorationAttachmentService
{
    private const string MarkerName = ".hd2-decoration-overwrite";
    private const string HelmetMergeMarkerName = ".hd2-helmet-to-armor-overwrite";
    private readonly IEquipmentUnitCatalogService _catalog;
    private readonly IModInformationReader _informationReader;

    public DecorationAttachmentService(StoragePaths paths, IModInformationReader informationReader)
    {
        _catalog = CoreServices.CreateEquipmentUnitCatalogService(paths);
        _informationReader = informationReader ?? throw new ArgumentNullException(nameof(informationReader));
    }

    public async Task RebuildHostAsync(ModNode host, IEnumerable<(ModEntity Mod, DecorationPlanDocument Plan)> enabled, string modsRoot, string gameDataDirectory, CancellationToken cancellationToken = default, IProgress<DecorationOperationProgress>? progress = null)
    {
        var totalStopwatch = Stopwatch.StartNew();
        var attachments = enabled.ToArray();
        var output = Path.Combine(modsRoot, host.RelativePath, "Overwrite");
        LogService.Info($"装饰合并开始：主体={host.Id.Value:N}，装饰数={attachments.Length}，输出={output}");
        if (attachments.Length == 0)
        {
            progress?.Report(new DecorationOperationProgress("正在清理已生成装饰", 0, 1));
            RemoveGeneratedOverwrite(output);
            progress?.Report(new DecorationOperationProgress("正在清理已生成装饰", 1, 1));
            LogService.Info($"装饰合并清理完成：主体={host.Id.Value:N}");
            return;
        }
        if (string.IsNullOrWhiteSpace(gameDataDirectory) || !Directory.Exists(gameDataDirectory))
            throw new InvalidOperationException("请在设置页重建资产索引。");
        var readContext = ModInformationRequestContext.Create(
            ModInformationCacheScope.Operation,
            operationName: "DecorationAttachment");
        try
        {
            var avatarStopwatch = Stopwatch.StartNew();
            var avatar = await new CanonicalAvatarRigReader(new AdaptationGameDataPackageResolver(gameDataDirectory)).ReadTransformInfoAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            LogService.Info($"装饰性能：主体={host.Id.Value:N}，阶段=读取 Avatar 骨架，耗时={avatarStopwatch.ElapsedMilliseconds}ms");
            var payloads = new List<(ModEntity Mod, DecorationAttachmentPlan Plan, DecorationPayloadDocument Payload)>();
            var payloadCount = attachments.Sum(attachment => IsPatchSnapshot(attachment.Plan) ? 1 : attachment.Plan.Payloads.Count);
            var readPayloads = 0;
            progress?.Report(new DecorationOperationProgress("正在读取装饰模型", 0, payloadCount));
            foreach (var attachment in attachments)
            {
                if (IsPatchSnapshot(attachment.Plan))
                {
                    var snapshotStopwatch = Stopwatch.StartNew();
                    var documents = await ReadSnapshotPayloadsAsync(attachment.Mod, attachment.Plan, modsRoot, readContext, cancellationToken).ConfigureAwait(false);
                    payloads.AddRange(documents.Select(document => (attachment.Mod, attachment.Plan.Plan, document)));
                    LogService.Info($"装饰性能：主体={host.Id.Value:N}，阶段=读取来源 Patch 快照，装饰={attachment.Mod.Guid}，Unit数={attachment.Plan.SourceUnits.Count}，Payload数={documents.Count}，耗时={snapshotStopwatch.ElapsedMilliseconds}ms");
                    progress?.Report(new DecorationOperationProgress("正在读取装饰来源 Patch", ++readPayloads, payloadCount));
                    continue;
                }
                foreach (var file in attachment.Plan.Payloads)
                {
                    if (!Path.GetExtension(file.File).Equals(".bin", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("仅兼容旧版 .bin 装饰，请重新生成该装饰。");
                    LogService.Info($"装饰合并读取 payload：装饰={attachment.Mod.Guid}，文件={file.File}，身形={file.BodyVariant}");
                    var payloadPath = Path.Combine(modsRoot, attachment.Mod.SourcePath ?? string.Empty, file.File);
                    var payloadStopwatch = Stopwatch.StartNew();
                    var payload = await ReadPayloadAsync(payloadPath, cancellationToken).ConfigureAwait(false);
                    var payloadBytes = new FileInfo(payloadPath).Length;
                    LogService.Info($"装饰性能：主体={host.Id.Value:N}，阶段=读取来源 Payload，装饰={attachment.Mod.Guid}，文件={file.File}，压缩大小={payloadBytes}B，片段={payload.Fragments.Count}，耗时={payloadStopwatch.ElapsedMilliseconds}ms");
                    payloads.Add((attachment.Mod, attachment.Plan.Plan, payload));
                    progress?.Report(new DecorationOperationProgress("正在读取装饰模型", ++readPayloads, payloadCount));
                }
            }
            var backup = PrepareGeneratedOverwrite(output);
            try
            {
                var patchPaths = ResolveOriginalPatchFiles(host, modsRoot);
                LogService.Info($"装饰合并主体 Patch：主体={host.Id.Value:N}，数量={patchPaths.Count}");
                var edits = 0;
                for (var index = 0; index < patchPaths.Count; index++)
                {
                    progress?.Report(new DecorationOperationProgress("正在重建主体 Patch", index, patchPaths.Count));
                    var patchStopwatch = Stopwatch.StartNew();
                    edits += await RebuildPatchAsync(host, patchPaths[index], output, payloads, avatar, readContext, cancellationToken, progress).ConfigureAwait(false);
                    LogService.Info($"装饰性能：主体={host.Id.Value:N}，阶段=重建主体 Patch，文件={Path.GetFileName(patchPaths[index])}，耗时={patchStopwatch.ElapsedMilliseconds}ms");
                }
                progress?.Report(new DecorationOperationProgress("正在重建主体 Patch", patchPaths.Count, patchPaths.Count));
                if (edits == 0) throw new InvalidDataException("没有找到与装饰计划匹配的主体 Unit。");
                await File.WriteAllTextAsync(Path.Combine(output, MarkerName), "generated", cancellationToken).ConfigureAwait(false);
                if (backup is not null) Directory.Delete(backup, recursive: true);
                LogService.Info($"装饰合并完成：主体={host.Id.Value:N}，Unit 编辑数={edits}，输出={output}");
                LogService.Info($"装饰性能：主体={host.Id.Value:N}，阶段=合并总计，Payload数={payloads.Count}，Patch数={patchPaths.Count}，Unit编辑数={edits}，耗时={totalStopwatch.ElapsedMilliseconds}ms");
            }
            catch (Exception exception)
            {
                LogService.Error($"装饰合并失败：主体={host.Id.Value:N}，输出={output}，异常={exception}");
                if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
                if (backup is not null && Directory.Exists(backup)) Directory.Move(backup, output);
                throw;
            }
        }
        finally
        {
            _informationReader.ClearOperation(readContext.OperationId);
        }
    }

    private async Task<int> RebuildPatchAsync(ModNode host, string patch, string output, IReadOnlyList<(ModEntity Mod, DecorationAttachmentPlan Plan, DecorationPayloadDocument Payload)> attachments, UnitTransformInfo avatar, ModInformationRequestContext readContext, CancellationToken cancellationToken, IProgress<DecorationOperationProgress>? progress)
    {
        LogService.Info($"装饰合并读取 Patch：{patch}");
        var scanStopwatch = Stopwatch.StartNew();
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
        LogService.Info($"装饰性能：Patch={Path.GetFileName(patch)}，阶段=扫描 TOC，条目={entries.Count}，耗时={scanStopwatch.ElapsedMilliseconds}ms");
        var keys = entries.Where(entry => entry.AssetKey.TypeId == PatchUnitMeshReader.UnitTypeId)
            .Select(entry => new HD2ModCore.Domain.AssetKey(entry.AssetKey.TypeId, entry.AssetKey.FileId)).ToHashSet();
        var catalogStopwatch = Stopwatch.StartNew();
        var catalog = await _catalog.GetEntriesAsync(keys, cancellationToken).ConfigureAwait(false);
        LogService.Info($"装饰性能：Patch={Path.GetFileName(patch)}，阶段=读取 Unit 目录，Unit数={keys.Count}，目录项={catalog.Count}，耗时={catalogStopwatch.ElapsedMilliseconds}ms");
        var candidates = catalog
            .SelectMany(entry => entry.Parts.Select(part => new DecorationHostCandidate(entry.ArchiveId, part)))
            .GroupBy(candidate => (candidate.ArchiveId, candidate.Part.UnitAssetKey, candidate.Part.MeshInfoIndex))
            .Select(group => group.OrderByDescending(candidate => candidate.Part.StoredBytes).ThenByDescending(candidate => candidate.Part.Confidence).First())
            .ToArray();
        progress?.Report(new DecorationOperationProgress("正在分析主体模型", 0, candidates.Length));
        var unitsByKey = new Dictionary<CoreAssetKey, PatchUnitMesh>();
        var geometryStopwatch = Stopwatch.StartNew();
        var geometryByMesh = await ReadCandidateGeometryFactsAsync(entries, candidates, unitsByKey, patchRequest, cancellationToken, progress).ConfigureAwait(false);
        LogService.Info($"装饰性能：Patch={Path.GetFileName(patch)}，阶段=宿主几何分析，候选={candidates.Length}，已缓存Unit={unitsByKey.Count}，耗时={geometryStopwatch.ElapsedMilliseconds}ms");
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
                .GroupBy(target => (target.Attachment.Mod.Guid, target.Attachment.Payload.BodyVariant), StringTupleComparer.OrdinalIgnoreCase)
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
                        .Select(fragment => new CanonicalDecorationAppendInput(ToCanonical(fragment), DecorationNamespace(attachment.Mod.Guid))))
                    .ToArray();
                if (inputs.Length != 0) inputsByTargetMesh.Add(lodTarget.Mesh.Index, inputs);
            }
            if (inputsByTargetMesh.Count == 0)
            {
                var available = string.Join(",", selectedAttachments.SelectMany(attachment => attachment.Payload.Fragments).Select(fragment => fragment.RawMesh.LodIndex).Distinct().OrderBy(value => value));
                throw new InvalidDataException($"装饰没有与目标 LOD {targetRaw.LodIndex} 对应的来源 Mesh（可用 LOD：{available}）。");
            }
            var allInputs = inputsByTargetMesh.Values.SelectMany(inputs => inputs).ToArray();
            LogService.Info($"装饰合并 Unit：Patch={Path.GetFileName(patch)}，目标=0x{entry.AssetKey.FileId:x16}，Mesh={targetPart.MeshInfoIndex}，目标LOD=[{string.Join(",", inputsByTargetMesh.Keys.Select(index => unit.Model.RawMeshData.Single(raw => raw.MeshInfoIndex == index).LodIndex).OrderBy(lod => lod))}]，部位={targetPart.PartKind}，层级={targetPart.Layer}，身形={targetPart.BodyVariant}，替换={replaceTargetGeometry}，来源数={allInputs.Length}，来源顶点={allInputs.Sum(input => input.Fragment.RawMesh.Vertices.Count)}，来源三角={allInputs.Sum(input => input.Fragment.RawMesh.Sections.Sum(section => section.Triangles.Count))}");
            var appendStopwatch = Stopwatch.StartNew();
            var result = new CanonicalDecorationUnitAppender().TryAppendLodFamily(unit, inputsByTargetMesh, avatar, replaceTargetGeometry);
            if (!result.IsValid)
            {
                var detail = string.Join("; ", result.Diagnostics.Select(item => $"{item.Code}: {item.Message}"));
                LogService.Error($"装饰合并 Unit 失败：目标=0x{entry.AssetKey.FileId:x16}，Mesh={targetPart.MeshInfoIndex}，问题={detail}");
                throw new InvalidDataException(detail);
            }
            edits.Add(result.Edit!);
            LogService.Info($"装饰性能：Patch={Path.GetFileName(patch)}，阶段=Canonical Unit 合并，目标=0x{entry.AssetKey.FileId:x16}，Mesh={targetPart.MeshInfoIndex}，LOD数={inputsByTargetMesh.Count}，来源数={allInputs.Length}，耗时={appendStopwatch.ElapsedMilliseconds}ms");
            progress?.Report(new DecorationOperationProgress("正在合并目标 Unit", targetIndex + 1, targetEntries.Length));
        }
        if (edits.Count != 0)
        {
            LogService.Info($"装饰合并写出 Patch：源={patch}，Unit 编辑数={edits.Count}");
            var writeStopwatch = Stopwatch.StartNew();
            await new PatchArchiveWriter().WriteAsync(patch, output, edits, overwriteExisting: true, cancellationToken: cancellationToken).ConfigureAwait(false);
            LogService.Info($"装饰性能：Patch={Path.GetFileName(patch)}，阶段=写出 Patch，Unit编辑数={edits.Count}，耗时={writeStopwatch.ElapsedMilliseconds}ms");
        }
        return edits.Count;
    }

    private async Task<IReadOnlyDictionary<(CoreAssetKey Unit, int Mesh), UnitMeshGeometryFact>> ReadCandidateGeometryFactsAsync(
        IReadOnlyList<HD2ModAdaptation.PatchReconstruction.PatchTocEntry> entries,
        IReadOnlyList<DecorationHostCandidate> candidates,
        IDictionary<CoreAssetKey, PatchUnitMesh> unitsByKey,
        ModInformationReadRequest patchRequest,
        CancellationToken cancellationToken,
        IProgress<DecorationOperationProgress>? progress)
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
                LogService.Info($"装饰宿主几何分析跳过不可读 Unit：0x{entry.AssetKey.FileId:x16}，原因={exception.Message}");
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
        IReadOnlyList<(ModEntity Mod, DecorationAttachmentPlan Plan, DecorationPayloadDocument Payload)> attachments)
    {
        var resolved = new List<DecorationResolvedTarget>();
        foreach (var attachmentGroup in attachments.GroupBy(attachment => attachment.Mod.Guid, StringComparer.OrdinalIgnoreCase))
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
                var sourceLayerText = sourceLayers.Count == 0 ? "legacy-fallback" : string.Join(",", sourceLayers);
                LogService.Info($"装饰宿主选择：装饰={attachmentSet[0].Mod.Guid}，Archive={archiveGroup.Key}，部位={plan.TargetPart}，来源层=[{sourceLayerText}]，候选=[{string.Join(", ", archiveCandidates.Select(DescribeCandidate))}]，选中=[{string.Join(", ", selections.Select(selection => $"{DescribeCandidate(selection.Candidate)}<-{selection.Attachment.Payload.BodyVariant}"))}]");
                resolved.AddRange(selections.Select(selection => new DecorationResolvedTarget(selection.Candidate.Part, selection.Attachment)));
            }
        }
        return resolved;
    }

    // Geometry is the first host gate. Layer and body policy only rank candidates
    // within the real-geometry set; otherwise a hidden placeholder would block an
    // actual host simply because it happens to share the source layer.
    private static IReadOnlyList<(DecorationHostCandidate Candidate, (ModEntity Mod, DecorationAttachmentPlan Plan, DecorationPayloadDocument Payload) Attachment)> SelectHosts(
        IReadOnlyList<DecorationHostCandidate> candidates,
        IReadOnlyList<(ModEntity Mod, DecorationAttachmentPlan Plan, DecorationPayloadDocument Payload)> attachments,
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
        var selections = new List<(DecorationHostCandidate Candidate, (ModEntity Mod, DecorationAttachmentPlan Plan, DecorationPayloadDocument Payload) Attachment)>();

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
        ICollection<(DecorationHostCandidate Candidate, (ModEntity Mod, DecorationAttachmentPlan Plan, DecorationPayloadDocument Payload) Attachment)> selections,
        DecorationHostCandidate? candidate,
        (ModEntity Mod, DecorationAttachmentPlan Plan, DecorationPayloadDocument Payload)? attachment)
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
        IEnumerable<(ModEntity Mod, DecorationAttachmentPlan Plan, DecorationPayloadDocument Payload)> attachments)
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

    private static (ModEntity Mod, DecorationAttachmentPlan Plan, DecorationPayloadDocument Payload)? FindPayload(
        IEnumerable<(ModEntity Mod, DecorationAttachmentPlan Plan, DecorationPayloadDocument Payload)> attachments,
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
        (ModEntity Mod, DecorationAttachmentPlan Plan, DecorationPayloadDocument Payload) Attachment);

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
        ModEntity decoration,
        DecorationPlanDocument document,
        string modsRoot,
        ModInformationRequestContext readContext,
        CancellationToken cancellationToken)
    {
        var root = Path.Combine(modsRoot, decoration.SourcePath ?? string.Empty);
        var parser = CoreServices.CreatePatchFileNameParser();
        var patches = Directory.EnumerateFiles(root)
            .Where(path => parser.TryParse(Path.GetFileName(path), out var info) && info?.SidecarKind == PatchSidecarKind.Base)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        var requested = document.SourceUnits.GroupBy(unit => (unit.TypeId, unit.FileId)).ToDictionary(group => group.Key, group => group.ToArray());
        var resolved = new HashSet<(ulong TypeId, ulong FileId, int Mesh, bool Culling)>();
        var fragments = new List<(string Variant, string Layer, DecorationMeshFragment Fragment)>();
        var nodeId = Guid.TryParse(decoration.Guid, out var parsedNodeId) ? new ModNodeId(parsedNodeId) : (ModNodeId?)null;
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
            if (!File.Exists(Path.Combine(directory, MarkerName))
                && !File.Exists(Path.Combine(directory, HelmetMergeMarkerName)))
                throw new InvalidOperationException("主体 Mod 的 Overwrite 目录不是管理器生成，已拒绝覆盖。");
            backup = directory + ".backup-" + Guid.NewGuid().ToString("N");
            Directory.Move(directory, backup);
        }
        Directory.CreateDirectory(directory);
        return backup;
    }

    private static void RemoveGeneratedOverwrite(string directory)
    {
        if (Directory.Exists(directory) && File.Exists(Path.Combine(directory, MarkerName))) Directory.Delete(directory, recursive: true);
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
