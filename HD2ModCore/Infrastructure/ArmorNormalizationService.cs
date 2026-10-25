using HD2ModAdaptation.Analysis;
using HD2ModCore.Application;
using HD2ModCore.Domain;
using HD2ModAdaptation.PatchReconstruction;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModAdaptation.PatchReconstruction.UnitMesh.Canonical;
using CoreAssetKey = HD2ModCore.Domain.AssetKey;
using AdaptationAssetKey = HD2ModAdaptation.PatchReconstruction.AssetKey;
using AdaptationGameDataPackageResolver = HD2ModAdaptation.PatchReconstruction.GameDataPackageResolver;

namespace HD2ModCore.Infrastructure;

public sealed class ArmorNormalizationService : IArmorNormalizationService
{
    private readonly IModEquipmentSourceFactsReader factsReader;
    private readonly IModInformationReader informationReader;
    private readonly string modsRootDirectory;

    public ArmorNormalizationService(IModEquipmentSourceFactsReader factsReader, IModInformationReader informationReader, string modsRootDirectory)
    {
        this.factsReader = factsReader;
        this.informationReader = informationReader;
        this.modsRootDirectory = modsRootDirectory;
    }

    public async ValueTask<ArmorNormalizationInspection> InspectAsync(ModNode source, string modsRootDirectory, string gameDataDirectory, CancellationToken cancellationToken = default)
    {
        var context = ModInformationRequestContext.Create(ModInformationCacheScope.Operation, operationName: "ArmorNormalizeInspect");
        try
        {
            var facts = await factsReader.ReadAsync(source, modsRootDirectory, new ModInformationRequest(ModInformationKind.AssetInventory, "ArmorNormalize", Context: context)
            {
                Property = ModInformationPropertyKind.UnitGeometrySummary
            }, cancellationToken).ConfigureAwait(false);
            var issues = facts.Issues.ToList();
            var patchByUnit = facts.Units.GroupBy(u => u.UnitAssetKey).ToDictionary(g => g.Key, g => g.Select(u => u.SourcePatchPath).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).First());
            var entriesByUnit = facts.PreparedEntries.Values.SelectMany(e => e).Where(e => e.AssetKey.TypeId == HD2ModAdaptation.PatchReconstruction.UnitMesh.PatchUnitMeshReader.UnitTypeId).GroupBy(e => new CoreAssetKey(e.AssetKey.TypeId, e.AssetKey.FileId)).ToDictionary(g => g.Key, g => g.First());
            var armors = new List<ArmorNormalizationArmor>();
            foreach (var entry in facts.SourceCandidates.Where(e => string.Equals(e.Category, "Armor", StringComparison.OrdinalIgnoreCase)))
            {
                var parts = entry.Parts.Where(p => p.HasRenderableGeometry && !p.IsCullingMesh && p.PartKind != UnitMeshPartKind.Unknown).ToArray();
                var targets = new List<ArmorNormalizationTarget>();
                foreach (var role in Roles)
                {
                    var roleParts = parts.Where(p => RoleMatches(role, p.PartKind)).ToArray();
                    var selected = SelectPair(roleParts);
                    if (selected.Count == 0)
                    {
                        issues.Add(new CoreIssue(CoreIssueSeverity.Warning, "ArmorNormalizeTargetMissing", $"Armor {entry.DisplayName} 缺少 {role} 标准承载 Unit。", entry.ArchiveId, source.Id));
                        continue;
                    }
                    targets.AddRange(selected.Select(p => new ArmorNormalizationTarget(role, Coverage(p.BodyVariant), Format(p.UnitAssetKey), p.MeshInfoIndex, p.Layer.ToString())));
                }
                if (targets.Count == 0) continue;
                var targetKeys = targets.Select(t => t.AssetKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var sources = parts.Select(p => new ArmorNormalizationSource(RoleName(p.PartKind), p.BodyVariant.ToString(), Format(p.UnitAssetKey), p.MeshInfoIndex, string.Join(',', targets.Where(t => RoleMatches(t.Role, p.PartKind) || t.Role == "Pelvis" && p.PartKind is UnitMeshPartKind.LeftLeg or UnitMeshPartKind.RightLeg).Select(t => t.Role)))).ToArray();
                // The archive is the visibility boundary: every non-target Unit belonging to
                // this armor must be hidden, including units that are not renderable candidates.
                var archiveParts = facts.TargetCandidates.FirstOrDefault(candidate => string.Equals(candidate.ArchiveId, entry.ArchiveId, StringComparison.OrdinalIgnoreCase))?.Parts
                    ?? entry.Parts;
                var hidden = archiveParts.Where(p => !targetKeys.Contains(Format(p.UnitAssetKey))).Select(p => Format(p.UnitAssetKey)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var targetCoverage = targets.GroupBy(t => t.Role).ToDictionary(g => g.Key, g => g.Select(t => t.BodyCoverage).ToArray(), StringComparer.OrdinalIgnoreCase);
                var warnings = new List<string>();
                foreach (var pair in targetCoverage)
                    if (pair.Value.Any(v => v == "Slim+Stocky") && pair.Value.Any(v => v == "Slim" || v == "Stocky")) warnings.Add($"{pair.Key} 身形覆盖重叠，可能导致重复叠加。");
                var targetUnitKeys = targets.Select(t => Parse(t.AssetKey)).Distinct().ToArray();
                var gpuGroups = targetUnitKeys.Where(entriesByUnit.ContainsKey).GroupBy(k => { var e = entriesByUnit[k]; return (e.SourceFilePath, e.GpuResourceOffset, e.GpuResourceSize); }).ToArray();
                var hasGpuReuse = gpuGroups.Any(g => g.Count() > 1);
                var lossy = !hasGpuReuse && targetUnitKeys.Length > 1;
                if (lossy) warnings.Add("未检测到重复 GPU range；将按基准护甲统一承载，可能不是无损规整。");
                var baseline = targets.OrderBy(t => t.Role, StringComparer.Ordinal).ThenBy(t => t.BodyCoverage, StringComparer.Ordinal).First().AssetKey;
                var patchPath = parts.Select(p => patchByUnit.GetValueOrDefault(p.UnitAssetKey)).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
                armors.Add(new ArmorNormalizationArmor(
                    patchPath is null ? "" : Path.GetFileName(patchPath),
                    entry.ArchiveId, entry.DisplayName, targets, sources, hidden, baseline, hasGpuReuse, lossy, warnings));
            }
            return new ArmorNormalizationInspection(source.Id, facts.SourcePatchTocPaths, armors, issues);
        }
        finally { informationReader.ClearOperation(context.OperationId); }
    }

    public async ValueTask<ArmorNormalizationResult> ExecuteAsync(ModNode source, string sourceDirectory, string outputDirectory, string gameDataDirectory, CancellationToken cancellationToken = default, IProgress<ArmorNormalizationProgress>? progress = null)
    {
        var issues = new List<CoreIssue>();
        if (!Directory.Exists(sourceDirectory)) return new(false, outputDirectory, 0, 0, 0, [new(CoreIssueSeverity.Error, "ArmorNormalizeSourceMissing", "来源目录不存在。", sourceDirectory, source.Id)]);
        Directory.CreateDirectory(outputDirectory);
        var inspection = await InspectAsync(source, modsRootDirectory, gameDataDirectory, cancellationToken).ConfigureAwait(false);
        if (!inspection.IsReady) return new(false, outputDirectory, 0, 0, 0, inspection.Issues);
        issues.AddRange(inspection.Issues);
        var avatarTransforms = await new CanonicalAvatarRigReader(
            new AdaptationGameDataPackageResolver(gameDataDirectory))
            .ReadTransformInfoAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var patches = inspection.SourcePatchPaths;
        var rebuiltUnits = 0; var hiddenUnits = 0; var processed = 0;
        await using var reader = CoreServices.CreateModInformationReader();
        foreach (var patch in patches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var context = ModInformationRequestContext.Create(ModInformationCacheScope.Operation, operationName: "ArmorNormalizeApply");
            var indexResult = await reader.ReadPatchIndexAsync(new ModInformationReadRequest(patch, context, ContentView: ModInformationContentView.Source, NodeId: source.Id), cancellationToken).ConfigureAwait(false);
            if (indexResult.Data is null) { issues.Add(new(CoreIssueSeverity.Warning, "ArmorNormalizePatchUnreadable", $"无法读取 Patch：{Path.GetFileName(patch)}。", patch, source.Id)); continue; }
            var entries = indexResult.Data.Entries;
            var entryByKey = entries.ToDictionary(e => e.AssetKey);
            var armors = inspection.Armors.Where(a => string.Equals(a.PatchFileName, Path.GetFileName(patch), StringComparison.OrdinalIgnoreCase)).ToArray();
            var editsByKey = new Dictionary<AdaptationAssetKey, PatchUnitMeshEditResult>();
            var additions = new List<PatchArchiveAdditionalEntry>();
            var gameDataReader = new GameDataUnitMeshReader(new AdaptationGameDataPackageResolver(gameDataDirectory));
            foreach (var armor in armors)
            {
                var byTarget = armor.Targets.GroupBy(t => t.AssetKey, StringComparer.OrdinalIgnoreCase);
                foreach (var targetGroup in byTarget)
                {
                    if (!TryParse(targetGroup.Key, out var targetKey) || !entryByKey.TryGetValue(targetKey, out var targetEntry)) continue;
                    PatchUnitMesh target;
                    try { target = (await reader.ReadUnitAsync(targetEntry, entries, PatchUnitDependencyPolicy.RequirePatchLocalComposite, new ModInformationReadRequest(patch, context, ContentView: ModInformationContentView.Source, NodeId: source.Id), canonicalSource: false, cancellationToken)).Data!; }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or EndOfStreamException or InvalidOperationException) { issues.Add(new(CoreIssueSeverity.Warning, "ArmorNormalizeTargetReadFailed", $"目标 Unit {targetGroup.Key} 无法读取：{ex.Message}", patch, source.Id)); continue; }
                    var inputsByMesh = new Dictionary<int, List<CanonicalDecorationAppendInput>>();
                    var targetRoles = targetGroup.Select(t => t.Role).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var targetCoverage = targetGroup.Select(t => t.BodyCoverage).FirstOrDefault() ?? "";
                    foreach (var src in armor.Sources.Where(s => !string.Equals(s.AssetKey, targetGroup.Key, StringComparison.OrdinalIgnoreCase) && SourceMapsToTarget(s, targetRoles) && BodyMatchesTarget(s.BodyVariant, targetCoverage)))
                    {
                        if (!TryParse(src.AssetKey, out var sourceKey) || !entryByKey.TryGetValue(sourceKey, out var sourceEntry)) continue;
                        PatchUnitMesh sourceUnit;
                        try { sourceUnit = (await reader.ReadUnitAsync(sourceEntry, entries, PatchUnitDependencyPolicy.RequirePatchLocalComposite, new ModInformationReadRequest(patch, context, ContentView: ModInformationContentView.Source, NodeId: source.Id), canonicalSource: false, cancellationToken)).Data!; }
                        catch { continue; }
                        var anchor = sourceUnit.Model.Meshes.SingleOrDefault(m => m.Index == src.MeshInfoIndex);
                        if (anchor is null) continue;
                        foreach (var lodTarget in ResolveVisibleLodFamily(target.Model, target.Model.Meshes.Single(m => m.Index == targetGroup.First().MeshInfoIndex)))
                        {
                            // Preserve the selected source part identity across LODs. Falling
                            // back to the first visual mesh can silently attach a different body
                            // part (for example torso geometry to a pelvis host).
                            var sourceRaw = sourceUnit.Model.RawMeshData
                                .Select(r => (Raw: r, Mesh: sourceUnit.Model.Meshes.SingleOrDefault(m => m.Index == r.MeshInfoIndex)))
                                .Where(x => x.Mesh is not null && x.Raw.LodIndex == lodTarget.Raw.LodIndex && x.Mesh!.SemanticInfo.IsVisualMesh)
                                .OrderByDescending(x => x.Mesh!.Index == src.MeshInfoIndex)
                                .ThenBy(x => x.Mesh!.Index)
                                .FirstOrDefault();
                            if (sourceRaw.Mesh is null) continue;
                            if (!inputsByMesh.TryGetValue(lodTarget.Mesh.Index, out var list)) inputsByMesh[lodTarget.Mesh.Index] = list = [];
                            list.Add(new CanonicalDecorationAppendInput(CreateFragment(sourceUnit, sourceRaw.Mesh, sourceRaw.Raw), MaterialNamespace(src.AssetKey)));
                        }
                    }
                    if (inputsByMesh.Count == 0) continue;
                    var appended = new CanonicalDecorationUnitAppender().TryAppendLodFamily(target, inputsByMesh.ToDictionary(p => p.Key, p => (IReadOnlyList<CanonicalDecorationAppendInput>)p.Value), avatarTransforms);
                    if (appended.IsValid && appended.Edit is not null) { editsByKey[targetEntry.AssetKey] = appended.Edit; rebuiltUnits++; }
                    else issues.AddRange(appended.Diagnostics.Select(d => new CoreIssue(CoreIssueSeverity.Warning, d.Code, d.Message, patch, source.Id)));
                }
                foreach (var hiddenKey in armor.HiddenUnitAssetKeys)
                {
                    if (!TryParse(hiddenKey, out var key)) continue;
                    if (!entryByKey.TryGetValue(key, out var entry))
                    {
                        try
                        {
                            var vanilla = await gameDataReader.ReadAsync(armor.ArchiveId, key, allowGlobalDependencySearch: true, cancellationToken: cancellationToken).ConfigureAwait(false);
                            var hidden = new CanonicalHiddenUnitBuilder().Build(vanilla, avatarTransforms);
                            additions.Add(new PatchArchiveAdditionalEntry(key, hidden.Entry.EffectiveTocData, hidden.Entry.EffectiveStreamData, hidden.Entry.EffectiveGpuData, hidden.Entry.Unknown1, hidden.Entry.Unknown2, hidden.Entry.Unknown3, hidden.Entry.Unknown4));
                            hiddenUnits++;
                        }
                        catch (Exception ex) { issues.Add(new(CoreIssueSeverity.Warning, "ArmorNormalizeVanillaHideFailed", $"无法从 GameData 隐藏 Unit {hiddenKey}：{ex.Message}", patch, source.Id)); }
                        continue;
                    }
                    try
                    {
                        var unit = (await reader.ReadUnitAsync(entry, entries, PatchUnitDependencyPolicy.RequirePatchLocalComposite, new ModInformationReadRequest(patch, context, ContentView: ModInformationContentView.Source, NodeId: source.Id), false, cancellationToken)).Data!;
                        var hidden = new CanonicalHiddenUnitBuilder().Build(new GameDataUnitMesh(key, patch, unit.Payload, unit.Model, unit.CompositePayload), avatarTransforms);
                        if (!editsByKey.ContainsKey(entry.AssetKey))
                        {
                            editsByKey[entry.AssetKey] = new PatchUnitMeshEditResult(entry, unit.Payload, hidden.Entry.EffectiveTocData, hidden.Entry.EffectiveGpuData);
                            hiddenUnits++;
                        }
                    }
                    catch (Exception ex) { issues.Add(new(CoreIssueSeverity.Warning, "ArmorNormalizeHideFailed", $"隐藏 Unit {hiddenKey} 失败：{ex.Message}", patch, source.Id)); }
                }
            }
            var edits = editsByKey.Values.ToArray();
            var wroteArchive = edits.Length != 0 || additions.Count != 0;
            if (wroteArchive)
            {
                // PatchArchiveWriter has already emitted a self-consistent TOC and sidecars.
                // Never copy the source sidecars afterward: doing so replaces the newly
                // generated GPU/stream payloads while leaving TOC offsets pointing at them.
                await new PatchArchiveWriter().WriteAsync(patch, outputDirectory, edits, additionalEntries: additions, overwriteExisting: true, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            else
            {
                File.Copy(patch, Path.Combine(outputDirectory, Path.GetFileName(patch)), overwrite: true);
                foreach (var sidecar in new[] { patch + ".stream", patch + ".gpu_resources" })
                    if (File.Exists(sidecar)) File.Copy(sidecar, Path.Combine(outputDirectory, Path.GetFileName(sidecar)), overwrite: true);
            }
            processed++;
            progress?.Report(new ArmorNormalizationProgress("Patch", processed, patches.Count));
        }
        return new(true, outputDirectory, processed, rebuiltUnits, hiddenUnits, issues);
    }

    private static bool SourceMapsToTarget(ArmorNormalizationSource source, IReadOnlySet<string> roles) => source.TargetRoles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Any(roles.Contains);
    private static bool BodyMatchesTarget(string sourceVariant, string targetCoverage)
        => sourceVariant.Equals("Any", StringComparison.OrdinalIgnoreCase)
            || targetCoverage.Equals("Slim", StringComparison.OrdinalIgnoreCase) && sourceVariant.Equals("Slim", StringComparison.OrdinalIgnoreCase)
            || targetCoverage.Equals("Stocky", StringComparison.OrdinalIgnoreCase) && sourceVariant.Equals("Stocky", StringComparison.OrdinalIgnoreCase);
    private static ulong MaterialNamespace(string assetKey)
        => BitConverter.ToUInt64(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(assetKey)), 0) | 1UL;
    private static CanonicalDecorationFragment CreateFragment(PatchUnitMesh unit, UnitMeshInfo mesh, UnitRawMeshData raw)
    {
        var stream = unit.Model.Streams.Single(s => s.Index == mesh.StreamIndex);
        return new(mesh, raw, stream, unit.Model.Materials.ToArray(), unit.Model.BoneInfos.ToArray(), unit.Model.TransformInfo, unit.Model.TransformNameHashes.ToArray());
    }
    private static IReadOnlyList<(UnitRawMeshData Raw, UnitMeshInfo Mesh)> ResolveVisibleLodFamily(UnitMeshModel model, UnitMeshInfo anchor)
        => model.RawMeshData.Select(r => (Raw:r, Mesh:model.Meshes.SingleOrDefault(m => m.Index == r.MeshInfoIndex))).Where(x => x.Mesh is not null && x.Raw.LodIndex >= 0 && x.Mesh!.SemanticInfo.IsVisualMesh && SameLodFamily(anchor.SemanticInfo, x.Mesh.SemanticInfo)).Select(x => (x.Raw, x.Mesh!)).ToArray();
    private static bool SameLodFamily(UnitMeshSemanticInfo a, UnitMeshSemanticInfo b) => !a.HasValue || !b.HasValue || (a.Slot == b.Slot && a.PieceType == b.PieceType && a.BodyType == b.BodyType && a.Weight == b.Weight);
    private static bool TryParse(string text, out AdaptationAssetKey key) { key = default; var p = text.Split('/'); if (p.Length != 2) return false; try { key = new(Convert.ToUInt64(p[0][2..], 16), Convert.ToUInt64(p[1][2..], 16)); return true; } catch { return false; } }

    private static readonly string[] Roles = ["Head", "Torso", "LeftArm", "RightArm", "Pelvis"];
    private static bool RoleMatches(string role, UnitMeshPartKind kind) => role switch
    {
        "Head" => kind == UnitMeshPartKind.Helmet,
        "Torso" => kind == UnitMeshPartKind.Torso,
        "LeftArm" => kind == UnitMeshPartKind.LeftArm,
        "RightArm" => kind == UnitMeshPartKind.RightArm,
        "Pelvis" => kind is UnitMeshPartKind.Hips or UnitMeshPartKind.LeftLeg or UnitMeshPartKind.RightLeg,
        _ => false
    };
    private static string RoleName(UnitMeshPartKind kind) => kind switch
    {
        UnitMeshPartKind.Helmet => "Head", UnitMeshPartKind.Torso => "Torso", UnitMeshPartKind.LeftArm => "LeftArm", UnitMeshPartKind.RightArm => "RightArm", UnitMeshPartKind.Hips or UnitMeshPartKind.LeftLeg or UnitMeshPartKind.RightLeg => "Pelvis", _ => "Other"
    };
    private static string Coverage(UnitMeshBodyVariant v) => v switch { UnitMeshBodyVariant.Any => "Slim+Stocky", UnitMeshBodyVariant.Slim => "Slim", UnitMeshBodyVariant.Stocky => "Stocky", _ => "Unknown" };
    private static string Format(CoreAssetKey key) => $"0x{key.TypeId:x16}/0x{key.FileId:x16}";
    private static CoreAssetKey Parse(string value)
    {
        var parts = value.Split('/');
        return new CoreAssetKey(Convert.ToUInt64(parts[0][2..], 16), Convert.ToUInt64(parts[1][2..], 16));
    }
    private static IReadOnlyList<EquipmentUnitPart> SelectPair(IReadOnlyList<EquipmentUnitPart> parts)
    {
        var groups = parts.GroupBy(p => p.Layer).OrderByDescending(g => LayerRank(g.Key));
        foreach (var group in groups)
        {
            var slim = group.Where(p => p.BodyVariant == UnitMeshBodyVariant.Slim).OrderByDescending(p => p.GeometryQuality).ThenBy(p => p.UnitAssetKey.FileId).FirstOrDefault();
            var stocky = group.Where(p => p.BodyVariant == UnitMeshBodyVariant.Stocky).OrderByDescending(p => p.GeometryQuality).ThenBy(p => p.UnitAssetKey.FileId).FirstOrDefault();
            if (slim is not null && stocky is not null && slim.UnitAssetKey != stocky.UnitAssetKey) return [slim, stocky];
            var any = group.Where(p => p.BodyVariant == UnitMeshBodyVariant.Any).OrderByDescending(p => p.GeometryQuality).ThenBy(p => p.UnitAssetKey.FileId).FirstOrDefault();
            if (any is not null) return [any];
        }
        return [];
    }
    private static int LayerRank(UnitMeshPartLayer layer) => layer switch { UnitMeshPartLayer.Armor => 3, UnitMeshPartLayer.Undergarment => 2, UnitMeshPartLayer.Accessory => 1, _ => 0 };
}
