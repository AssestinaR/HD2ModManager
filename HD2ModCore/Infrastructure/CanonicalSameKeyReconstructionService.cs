using HD2ModCore.Application;
using HD2ModCore.Domain;
using HD2ModAdaptation.PatchReconstruction;
using HD2ModAdaptation.PatchReconstruction.PatchWorkspace;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModAdaptation.PatchReconstruction.UnitMesh.Canonical;
using AdaptationAssetKey = HD2ModAdaptation.PatchReconstruction.AssetKey;
using CoreAssetKey = HD2ModCore.Domain.AssetKey;
using AdaptationGameDataPackageResolver = HD2ModAdaptation.PatchReconstruction.GameDataPackageResolver;
using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace HD2ModCore.Infrastructure;

// Purpose: Plans and executes one same-AssetKey Patch rebuild through the new Canonical/Workspace pipeline.
public sealed class CanonicalSameKeyReconstructionService : IModSameKeyReconstructionService
{
    private static readonly ConcurrentDictionary<Guid, long> progressSequences = new();
    private readonly IPatchFileNameParser fileNameParser;
    private readonly IAssetArchiveIndexService assetIndex;
    private readonly IArchiveHashesProvider archiveHashes;
    private readonly IModInformationReader informationReader;
    private readonly IPatchWorkspaceWriter workspaceWriter;
    private readonly IPatchOperationWorkspaceFactory operationWorkspaceFactory;
    private readonly ICanonicalHiddenUnitOutputCache hiddenUnitCache;
    private readonly CanonicalHiddenUnitBuilder hiddenUnitBuilder;

    public CanonicalSameKeyReconstructionService(
        IPatchFileNameParser fileNameParser,
        IAssetArchiveIndexService assetIndex,
        IArchiveHashesProvider archiveHashes,
        IPatchWorkspaceReader? workspaceReader = null,
        IPatchWorkspaceWriter? workspaceWriter = null,
        IPatchOperationWorkspaceFactory? operationWorkspaceFactory = null,
        ICanonicalHiddenUnitOutputCache? hiddenUnitCache = null,
        CanonicalHiddenUnitBuilder? hiddenUnitBuilder = null,
        IModInformationReader? informationReader = null)
    {
        this.fileNameParser = fileNameParser ?? throw new ArgumentNullException(nameof(fileNameParser));
        this.assetIndex = assetIndex ?? throw new ArgumentNullException(nameof(assetIndex));
        this.archiveHashes = archiveHashes ?? throw new ArgumentNullException(nameof(archiveHashes));
        this.informationReader = informationReader ?? new ModInformationReader(workspaceReader);
        this.workspaceWriter = workspaceWriter ?? new PatchWorkspaceWriter();
        this.operationWorkspaceFactory = operationWorkspaceFactory ?? new PatchOperationWorkspaceFactory();
        this.hiddenUnitCache = hiddenUnitCache ?? new CanonicalHiddenUnitOutputCache();
        this.hiddenUnitBuilder = hiddenUnitBuilder ?? new CanonicalHiddenUnitBuilder();
    }

    public async ValueTask<ModSameKeyReconstructionState> InspectAsync(
        ModNode source, string modsRootDirectory, string gameDataDirectory,
        CancellationToken cancellationToken = default, IProgress<OperationProgressEvent>? progress = null, Guid? operationId = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var patch = FindBasePatchPaths(source, modsRootDirectory).FirstOrDefault();
        var issues = new List<CoreIssue>();
        if (patch is null) issues.Add(Error("PatchRequired", "Mod 没有 Patch 主文件。", source.Id));
        if (string.IsNullOrWhiteSpace(gameDataDirectory) || !Directory.Exists(gameDataDirectory)) issues.Add(Error("GameDataMissing", "Game Data 不可用。", source.Id));
        var current = false;
        if (issues.Count == 0)
        {
            var status = await assetIndex.GetIndexStatusAsync(gameDataDirectory, await archiveHashes.GetArchiveHashesJsonAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
            current = status.IsCurrent;
            if (!current) issues.Add(Error("GameDataIndexNotCurrent", "Game Data 资产索引不可用或已过期。", source.Id));
        }
        SameKeyReconstructionPlan? plan = null;
        if (issues.Count == 0)
        {
            using var sourceReadScope = BeginSourceReadScope(source, patch!, operationId);
            plan = await PlanAsync(source, modsRootDirectory, patch!, gameDataDirectory, cancellationToken, progress, operationId, sourceReadScope.Request).ConfigureAwait(false);
        }
        if (plan is not null) issues.AddRange(plan.Issues);
        return new ModSameKeyReconstructionState(source.Id, patch, plan, current,
            plan?.Units.Count(unit => unit.IsGeometryEligible) ?? 0,
            plan?.Units.Count(unit => !unit.IsGeometryEligible) ?? 0,
            0,
            0,
            0, issues);
    }

    public async ValueTask<SameKeyReconstructionOperationResult> GenerateCandidateAsync(
        ModNode source, string modsRootDirectory, string gameDataDirectory, string outputRootDirectory,
        CancellationToken cancellationToken = default, IProgress<OperationProgressEvent>? progress = null, Guid? operationId = null,
        bool useSharedHiddenUnitTemplate = true)
    {
        ArgumentNullException.ThrowIfNull(source);
        Report(progress, operationId, "InspectEligibility", "正在检查同 ID Canonical 重建资格", 0, 1);
        var patch = FindBasePatchPaths(source, modsRootDirectory).FirstOrDefault();
        if (patch is null) return Failure([Error("PatchRequired", "Mod 没有 Patch 主文件。", source.Id)]);
        if (string.IsNullOrWhiteSpace(gameDataDirectory) || !Directory.Exists(gameDataDirectory)) return Failure([Error("GameDataMissing", "Game Data 不可用。", source.Id)]);
        var status = await assetIndex.GetIndexStatusAsync(gameDataDirectory, await archiveHashes.GetArchiveHashesJsonAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        await hiddenUnitCache.InitializeAsync(status.CurrentSourceFingerprint, status.IsCurrent, cancellationToken).ConfigureAwait(false);
        if (!status.IsCurrent) return Failure([Error("GameDataIndexNotCurrent", "Game Data 资产索引不可用或已过期。", source.Id)]);

        using var sourceReadScope = BeginSourceReadScope(source, patch, operationId);
        var sourceReadRequest = sourceReadScope.Request;
        Report(progress, operationId, "Plan", "正在生成同 ID Canonical 重建计划", 0, 1);
        var planStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var index = await ReadSourcePatchIndexAsync(sourceReadRequest, cancellationToken).ConfigureAwait(false);
        var plan = await PlanAsync(source, modsRootDirectory, patch, gameDataDirectory, cancellationToken, progress, operationId, sourceReadRequest, index).ConfigureAwait(false);
        Report(progress, operationId, "Plan", $"Canonical 重建计划完成，用时={planStopwatch.ElapsedMilliseconds}ms", 1, 1);
        var issues = plan.Issues.ToList();
		if (issues.Any(issue => issue.Severity == CoreIssueSeverity.Error)) return Failure(issues);
        var resolver = new AdaptationGameDataPackageResolver(gameDataDirectory);
        var targetReader = new GameDataUnitMeshReader(resolver);
        Report(progress, operationId, "PrepareAvatarRig", "正在读取 Canonical Avatar Rig 变换", 0, 1);
        var avatarRigStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var avatarTransforms = await new CanonicalAvatarRigReader(resolver)
            .ReadTransformInfoAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        Report(progress, operationId, "PrepareAvatarRig", $"Canonical Avatar Rig 变换读取完成，用时={avatarRigStopwatch.ElapsedMilliseconds}ms", 1, 1);
        var output = Path.GetFullPath(outputRootDirectory);
        Directory.CreateDirectory(output);
		using var artifacts = new CanonicalDiagnosticArtifacts(output, "SameKey");
		artifacts.Log($"[START] SourcePatch={Path.GetFileName(patch)} Units={plan.Units.Count}");
		if (plan.Units.Count != 0)
		{
			try
			{
				return await GenerateGroupedCandidateAsync(
					source, patch, index, plan, resolver, targetReader, avatarTransforms, output, artifacts,
					cancellationToken, progress, operationId, sourceReadRequest, useSharedHiddenUnitTemplate).ConfigureAwait(false);
			}
			catch (Exception exception) when (exception is not OperationCanceledException)
			{
				var failure = Error("RecipePlanningFailed", exception.Message, source.Id);
				artifacts.Log($"[ERROR] Same-key grouped reconstruction failed: {exception}");
				await artifacts.WriteReportAsync("Failed", "配方分析或执行失败", [failure.Message], CancellationToken.None).ConfigureAwait(false);
				return Failure([.. plan.Issues, failure]);
			}
		}
        using var operationWorkspace = operationWorkspaceFactory.Create(output, "same-key-reconstruction");
        var jobsBySequence = new PatchWorkspaceJobResult?[plan.Units.Count];
        var removed = index.Entries.Where(entry => entry.AssetKey.TypeId == PatchUnitMeshReader.UnitTypeId).Select(entry => entry.AssetKey).ToHashSet();
        var sourceEntries = index.Entries;
        var sourcePayloadFingerprints = new UnitPayloadFingerprintCache();
        var parsedSources = new ConcurrentDictionary<string, Lazy<Task<PatchUnitMesh>>>(StringComparer.Ordinal);
        var sourcePayloadGroups = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        Report(progress, operationId, "BuildCandidate", "正在执行 Canonical Unit 作业", 0, plan.Units.Count);
        var sourceReadElapsed = TimeSpan.Zero;
        var targetReadElapsed = TimeSpan.Zero;
        var mappingElapsed = TimeSpan.Zero;
        var rebuildElapsed = TimeSpan.Zero;
        var stagingElapsed = TimeSpan.Zero;
		var rebuildTelemetry = new CanonicalUnitRebuildTelemetryAccumulator();
        var completedUnits = 0;
        var resultGate = new object();
        var replacementUnitCount = 0;
        var minifyOnlyUnitCount = 0;
        var replacementMeshCount = 0;
        var minifiedMeshCount = 0;
		var unitTelemetry = new List<CanonicalUnitJobTelemetryRow>();
        using var positionDiagnostics = CanonicalPositionDiagnostics.Suppress();
        var unitJobs = plan.Units.Select((unit, index) =>
            (Sequence: index, UnitKey: $"0x{unit.UnitAssetKey.FileId:x16}")).ToArray();
        var results = await UnitJobExecutor.ExecuteAsync(
            unitJobs,
            async (jobIndex, token) =>
            {
                var unitPlan = plan.Units[jobIndex];
                var sourceEntry = sourceEntries.Single(entry => entry.AssetKey == new AdaptationAssetKey(unitPlan.UnitAssetKey.TypeId, unitPlan.UnitAssetKey.FileId));
                var archiveId = unitPlan.TargetArchive!.ArchiveId;
                var sourceIsEligible = plan.EligibleSourceUnitAssetKeys?.Contains(unitPlan.UnitAssetKey) == true;
				var allocationBefore = GC.GetTotalAllocatedBytes(false);
				var gen0Before = GC.CollectionCount(0);
				var gen1Before = GC.CollectionCount(1);
				var gen2Before = GC.CollectionCount(2);
                if (!sourceIsEligible)
                {
                    var cached = await hiddenUnitCache.TryReadAsync(archiveId, sourceEntry.AssetKey, token).ConfigureAwait(false);
                    if (cached is not null)
                    {
                        var cachedResult = new SameKeyCanonicalUnitRebuildResult(PatchWorkspaceJobResult.Unit(cached.Entry), 0, cached.HiddenMeshCount, []);
						return new SameKeyUnitJobResult(jobIndex, unitPlan.UnitAssetKey, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, cachedResult,
							true, sourceIsEligible, 0, 0, 0, allocationBefore, gen0Before, gen1Before, gen2Before);
                    }
                }
                var unitStopwatch = System.Diagnostics.Stopwatch.StartNew();
                var targetUnit = await targetReader.ReadAsync(
                    archiveId,
                    sourceEntry.AssetKey,
                    allowGlobalDependencySearch: true,
                    cancellationToken: token).ConfigureAwait(false);
                var localTargetRead = unitStopwatch.Elapsed;
                unitStopwatch.Restart();
                if (!sourceIsEligible)
                {
                    var hidden = hiddenUnitBuilder.Build(targetUnit, avatarTransforms);
                    await hiddenUnitCache.StoreAsync(archiveId, hidden, token).ConfigureAwait(false);
                    var hiddenResult = new SameKeyCanonicalUnitRebuildResult(PatchWorkspaceJobResult.Unit(hidden.Entry), 0, hidden.HiddenMeshCount, []);
					return new SameKeyUnitJobResult(jobIndex, unitPlan.UnitAssetKey, TimeSpan.Zero, localTargetRead, TimeSpan.Zero, unitStopwatch.Elapsed, hiddenResult,
						false, sourceIsEligible, targetUnit.Model.Meshes.Count,
						checked(targetUnit.Model.RawMeshData.Sum(raw => raw.Vertices.Count)), checked(targetUnit.Model.RawMeshData.Sum(raw => raw.Triangles.Count)),
						allocationBefore, gen0Before, gen1Before, gen2Before);
                }
                var sourceFingerprint = await sourcePayloadFingerprints.CreateAsync(sourceEntry, sourceEntries, token).ConfigureAwait(false);
                var sourceTemplate = await parsedSources.GetOrAdd(sourceFingerprint, _ => new Lazy<Task<PatchUnitMesh>>(
                    () => ReadCanonicalSourceUnitAsync(sourceEntry, sourceEntries, sourceReadRequest, token).AsTask(),
                    LazyThreadSafetyMode.ExecutionAndPublication)).Value.ConfigureAwait(false);
                sourcePayloadGroups.TryAdd(sourceFingerprint, 0);
                var sourceUnit = RebindSourceUnit(sourceTemplate, sourceEntry);
                var localSourceRead = unitStopwatch.Elapsed;
                unitStopwatch.Restart();
                var mappings = BuildMappings(sourceEntry.AssetKey, sourceUnit.Model, targetUnit.Model,
                    sourceIsEligible).ToArray();
                var localMapping = unitStopwatch.Elapsed;
                unitStopwatch.Restart();
                SameKeyCanonicalUnitRebuildResult rebuilt;
                if (mappings.Length == 0)
                {
                    var hidden = hiddenUnitBuilder.Build(targetUnit, avatarTransforms);
                    await hiddenUnitCache.StoreAsync(archiveId, hidden, token).ConfigureAwait(false);
                    rebuilt = new SameKeyCanonicalUnitRebuildResult(PatchWorkspaceJobResult.Unit(hidden.Entry), 0, hidden.HiddenMeshCount, []);
                }
                else
                {
                    rebuilt = new SameKeyCanonicalUnitRebuilder().Rebuild(new SameKeyCanonicalUnitRebuildRequest(sourceUnit, targetUnit, mappings)
                    {
                        AvatarTransformInfo = avatarTransforms
                    });
                }
                var outputModel = rebuilt.Model;
				return new SameKeyUnitJobResult(jobIndex, unitPlan.UnitAssetKey, localSourceRead, localTargetRead, localMapping,
                    unitStopwatch.Elapsed, rebuilt, false, sourceIsEligible,
					outputModel?.Meshes.Count ?? targetUnit.Model.Meshes.Count,
					checked((outputModel ?? targetUnit.Model).RawMeshData.Sum(raw => raw.Vertices.Count)),
					checked((outputModel ?? targetUnit.Model).RawMeshData.Sum(raw => raw.Triangles.Count)),
					allocationBefore, gen0Before, gen1Before, gen2Before);
            },
	            (sequence, result) =>
	            {
	                var unitPlan = plan.Units[sequence];
	                lock (resultGate)
	                {
	                    sourceReadElapsed += result.SourceRead;
	                    targetReadElapsed += result.TargetRead;
	                    mappingElapsed += result.Mapping;
	                    rebuildElapsed += result.RebuildElapsed;
	                    var rebuilt = result.Rebuild;
	                    if (!rebuilt.IsValid || rebuilt.Job is null)
	                        issues.AddRange(rebuilt.Diagnostics.Select(diagnostic => Error(diagnostic.Code, $"Unit=0x{unitPlan.UnitAssetKey.FileId:x16}; {diagnostic.Message}", source.Id)));
	                    else
	                    {
	                        rebuildTelemetry.Add(rebuilt.Telemetry);
	                        var stagingStopwatch = System.Diagnostics.Stopwatch.StartNew();
	                        jobsBySequence[sequence] = operationWorkspace.Stage(rebuilt.Job);
                        stagingElapsed += stagingStopwatch.Elapsed;
						unitTelemetry.Add(CreateUnitJobTelemetryRow(result, stagingStopwatch.Elapsed));
	                        replacementMeshCount += rebuilt.ReplacedMeshCount;
	                        minifiedMeshCount += rebuilt.HiddenMeshCount;
	                        if (rebuilt.ReplacedMeshCount > 0) replacementUnitCount++;
	                        else minifyOnlyUnitCount++;
	                    }
	                }
	                foreach (var observation in result.Rebuild.MaterialObservations ?? [])
	                    artifacts.Log($"[MATERIAL] Unit=0x{unitPlan.UnitAssetKey.FileId:x16}; {observation.Message}");
	                artifacts.Log($"[UNIT] Unit=0x{unitPlan.UnitAssetKey.FileId:x16}; Cache={result.HiddenCacheHit}; SourceReadMs={result.SourceRead.TotalMilliseconds:F0}; TargetReadMs={result.TargetRead.TotalMilliseconds:F0}; RebuildMs={result.RebuildElapsed.TotalMilliseconds:F0}");
	                var completed = Interlocked.Increment(ref completedUnits);
	                Report(progress, operationId, "BuildCandidate", $"已完成 Unit {completed}/{plan.Units.Count}", completed, plan.Units.Count);
	                return ValueTask.CompletedTask;
	            },
	            cancellationToken: cancellationToken).ConfigureAwait(false);

        var jobs = jobsBySequence.Where(job => job is not null).Select(job => job!).ToList();

        if (results.Any(result => result is not null)) foreach (var result in results.OrderBy(result => result.Sequence))
        {
            sourceReadElapsed += result.SourceRead;
            targetReadElapsed += result.TargetRead;
            mappingElapsed += result.Mapping;
            rebuildElapsed += result.RebuildElapsed;
            var unitPlan = plan.Units[result.Sequence];
            cancellationToken.ThrowIfCancellationRequested();
            var rebuilt = result.Rebuild;
            if (!rebuilt.IsValid || rebuilt.Job is null)
                issues.AddRange(rebuilt.Diagnostics.Select(diagnostic => Error(
                    diagnostic.Code,
                    $"Unit=0x{unitPlan.UnitAssetKey.FileId:x16}; {diagnostic.Message}",
                    source.Id)));
            else
            {
				rebuildTelemetry.Add(rebuilt.Telemetry);
                var stagingStopwatch = System.Diagnostics.Stopwatch.StartNew();
                jobs.Add(operationWorkspace.Stage(rebuilt.Job));
                stagingElapsed += stagingStopwatch.Elapsed;
				unitTelemetry.Add(CreateUnitJobTelemetryRow(result, stagingStopwatch.Elapsed));
                foreach (var observation in rebuilt.MaterialObservations ?? [])
                    artifacts.Log($"[MATERIAL] Unit=0x{unitPlan.UnitAssetKey.FileId:x16}; {observation.Message}");
				artifacts.Log($"[UNIT] Unit=0x{unitPlan.UnitAssetKey.FileId:x16}; Cache={result.HiddenCacheHit}; SourceReadMs={result.SourceRead.TotalMilliseconds:F0}; TargetReadMs={result.TargetRead.TotalMilliseconds:F0}; RebuildMs={result.RebuildElapsed.TotalMilliseconds:F0}");

                replacementMeshCount += rebuilt.ReplacedMeshCount;
                minifiedMeshCount += rebuilt.HiddenMeshCount;
                if (rebuilt.ReplacedMeshCount > 0) replacementUnitCount++;
                else minifyOnlyUnitCount++;
            }
            completedUnits++;
            Report(progress, operationId, "BuildCandidate", $"已完成 Unit {completedUnits}/{plan.Units.Count}", completedUnits, plan.Units.Count);
        }
        Report(progress, operationId, "BuildCandidateMetrics", $"Same-key Unit 作业耗时：来源读取={sourceReadElapsed.TotalMilliseconds:F0}ms，目标读取={targetReadElapsed.TotalMilliseconds:F0}ms，重建={rebuildElapsed.TotalMilliseconds:F0}ms，落盘={stagingElapsed.TotalMilliseconds:F0}ms", completedUnits, plan.Units.Count);
		artifacts.Log($"[SOURCE-DEDUP] Units={plan.Units.Count}; UniquePayloads={sourcePayloadGroups.Count}; Reused={Math.Max(0, plan.Units.Count - sourcePayloadGroups.Count)}");
		await CanonicalUnitJobTelemetry.WriteCsvAsync(artifacts.TelemetryPath, unitTelemetry, cancellationToken).ConfigureAwait(false);
		await artifacts.WriteMappingsAsync(plan.Units.Select(unit => new CanonicalMappingDiagnosticRow(
			"未分类", unit.IsGeometryEligible ? "命中" : "隐藏", $"0x{unit.UnitAssetKey.FileId:x16}", $"0x{unit.UnitAssetKey.FileId:x16}",
			string.Empty, string.Empty, string.Empty, string.Empty, unit.TargetArchive?.ArchiveId ?? string.Empty,
			"Same-key", unit.Issues.Count == 0 ? string.Empty : string.Join("; ", unit.Issues.Select(issue => issue.Message)))).ToArray(), cancellationToken).ConfigureAwait(false);
		if (issues.Any(issue => issue.Severity == CoreIssueSeverity.Error))
		{
			artifacts.Log($"[ERROR-SUMMARY] {string.Join(" | ", issues.Where(issue => issue.Severity == CoreIssueSeverity.Error).Select(issue => issue.Message))}");
			await artifacts.WriteReportAsync("Failed", "Unit 作业验证失败", issues.Select(issue => issue.Message).ToArray(), CancellationToken.None).ConfigureAwait(false);
			return Failure(issues);
		}
        Report(progress, operationId, "WriteCandidate", "正在打包 Canonical Patch", 0, 1);
        Report(progress, operationId, "CanonicalUnitJobMetrics", $"Canonical Unit job metrics: Flow=SameKey, SourceRead={sourceReadElapsed.TotalMilliseconds:F0}ms, TargetRead={targetReadElapsed.TotalMilliseconds:F0}ms, Mapping={mappingElapsed.TotalMilliseconds:F0}ms, Rebuild={rebuildElapsed.TotalMilliseconds:F0}ms, Staging={stagingElapsed.TotalMilliseconds:F0}ms, {rebuildTelemetry.Snapshot().Describe()}", completedUnits, plan.Units.Count);
        var writeStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var write = await workspaceWriter.WriteAsync(index, jobs, removed, output, Path.GetFileName(patch),
            headerTemplateTocData: (await resolver.GetPackageTocAsync(plan.Units.First().TargetArchive!.ArchiveId, cancellationToken).ConfigureAwait(false))?.Data,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        Report(progress, operationId, "WriteCandidate", $"Canonical Patch 打包完成，用时={writeStopwatch.ElapsedMilliseconds}ms", 1, 1);
        artifacts.Log($"[WRITE-DONE] Units={jobs.Count}; Replacements={replacementMeshCount}; Hidden={minifiedMeshCount}");
		await artifacts.WriteReportAsync("WrittenForGameTest", $"Unit={jobs.Count}; 替换Mesh={replacementMeshCount}; 极小化Mesh={minifiedMeshCount}", issues.Select(issue => issue.Message).ToArray(), cancellationToken).ConfigureAwait(false);
        return new SameKeyReconstructionOperationResult(true, output, null, artifacts.ReportPath, jobs.Count,
            replacementUnitCount, minifyOnlyUnitCount, replacementMeshCount, minifiedMeshCount, issues);
    }

    private async ValueTask<SameKeyReconstructionOperationResult> GenerateGroupedCandidateAsync(
        ModNode source,
        string patch,
        PatchWorkspaceIndex index,
        SameKeyReconstructionPlan plan,
        AdaptationGameDataPackageResolver resolver,
        GameDataUnitMeshReader targetReader,
        UnitTransformInfo avatarTransforms,
        string output,
        CanonicalDiagnosticArtifacts artifacts,
        CancellationToken cancellationToken,
        IProgress<OperationProgressEvent>? progress,
        Guid? operationId,
        ModInformationReadRequest sourceReadRequest,
        bool useSharedHiddenUnitTemplate)
    {
        var issues = plan.Issues.ToList();
        var sourceEntries = index.Entries;
        var sourcePayloadFingerprints = new UnitPayloadFingerprintCache();
        var recipes = new Dictionary<string, SameKeyExecutionRecipe>(StringComparer.Ordinal);
        Report(progress, operationId, "BuildRecipePlan", "正在分析 Unit 复用配方", 0, plan.Units.Count);

        foreach (var (unitPlan, sequence) in plan.Units.Select((value, index) => (value, index)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (unitPlan.TargetArchive is null)
            {
                issues.AddRange(unitPlan.Issues);
                continue;
            }
            var sourceEntry = sourceEntries.Single(entry => entry.AssetKey == new AdaptationAssetKey(unitPlan.UnitAssetKey.TypeId, unitPlan.UnitAssetKey.FileId));
            // Determine reuse strictly from this patch's own payload structure. GPU and
            // stream ranges must be physically shared, while Unit TOC and local
            // dependencies must be byte-identical. GameData target shells intentionally
            // do not participate: direct-reuse patches map one source Unit to many keys.
            var recipeKey = await sourcePayloadFingerprints.CreateReuseSignatureAsync(sourceEntry, sourceEntries, cancellationToken).ConfigureAwait(false);
            if (!recipes.TryGetValue(recipeKey, out var recipe))
            {
                recipe = new SameKeyExecutionRecipe(recipeKey, unitPlan);
                recipes.Add(recipeKey, recipe);
            }
            recipe.Members.Add(unitPlan);
            Report(progress, operationId, "BuildRecipePlan", $"已分析 Unit {sequence + 1}/{plan.Units.Count}；复用配方 {recipes.Count}", sequence + 1, plan.Units.Count);
        }

        if (issues.Any(issue => issue.Severity == CoreIssueSeverity.Error)) return Failure(issues);
        artifacts.Log($"[RECIPE-PLAN] Units={plan.Units.Count}; Recipes={recipes.Count}; Reused={plan.Units.Count - recipes.Count}; Singletons={recipes.Values.Count(recipe => recipe.Members.Count == 1)}");
        Report(progress, operationId, "ExecuteRecipes", "正在生成 Canonical Unit 输出", 0, recipes.Count);

        // Keep completed recipe payloads on disk. The final Patch writer already
        // supports path-backed entries, so retaining large GPU byte arrays here only
        // increases GC pressure and keeps every previous recipe alive until the end.
        using var operationWorkspace = operationWorkspaceFactory.Create(output, "same-key-reconstruction");
        var jobs = new List<PatchWorkspaceJobResult>(plan.Units.Count);
        var replacementUnitCount = 0;
        var minifyOnlyUnitCount = 0;
        var replacementMeshCount = 0;
        var minifiedMeshCount = 0;
        var detectedHiddenSourceRecipeCount = 0;
        var visibleRebuildRecipeCount = 0;
        CanonicalPatchSessionEntry? sharedHiddenTemplate = null;
        var sharedHiddenMeshCount = 0;
        var sharedHiddenReuseCount = 0;
        var recipeIndex = 0;
        foreach (var recipe in recipes.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var representative = recipe.Representative;
            var sourceEntry = sourceEntries.Single(entry => entry.AssetKey == new AdaptationAssetKey(representative.UnitAssetKey.TypeId, representative.UnitAssetKey.FileId));
            var archiveId = representative.TargetArchive!.ArchiveId;
            PatchUnitMesh sourceUnit;
            try
            {
                // Each recipe is executed once, so retaining decoded source Units in
                // a dictionary provides no reuse benefit. Read the representative
                // directly and let its large object graph become collectible after
                // the recipe has been staged.
                var sourceTemplate = await ReadCanonicalSourceUnitAsync(
                    sourceEntry, sourceEntries, sourceReadRequest, cancellationToken).ConfigureAwait(false);
                sourceUnit = RebindSourceUnit(sourceTemplate, sourceEntry);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or EndOfStreamException or OverflowException or KeyNotFoundException)
            {
                var message = $"来源 Unit 0x{representative.UnitAssetKey.FileId:x16} 无法解析，已按不透明 Unit 保留：{exception.Message}";
                issues.Add(new CoreIssue(CoreIssueSeverity.Warning, "CanonicalSourceUnitOpaqueRetained", message, sourceEntry.SourceFilePath, source.Id, exception.ToString()));
                foreach (var member in recipe.Members)
                {
                    var memberEntry = sourceEntries.Single(entry => entry.AssetKey == new AdaptationAssetKey(member.UnitAssetKey.TypeId, member.UnitAssetKey.FileId));
                    jobs.Add(PatchWorkspaceJobResult.Unit(CreateSourcePassthroughEntry(memberEntry), $"0x{member.UnitAssetKey.FileId:x16}"));
                }
                artifacts.Log($"[OPAQUE-RETAIN] Unit=0x{representative.UnitAssetKey.FileId:x16}; Route=SourceParseFailed; Reason={exception.Message}");
                recipeIndex++;
                Report(progress, operationId, "ExecuteRecipes", $"已保留不透明配方 {recipeIndex}/{recipes.Count}，覆盖 Unit {jobs.Count}/{plan.Units.Count}", recipeIndex, recipes.Count);
                continue;
            }
            GameDataUnitMesh targetUnit;
            try
            {
                targetUnit = await targetReader.ReadAsync(archiveId, sourceEntry.AssetKey, allowGlobalDependencySearch: true, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or EndOfStreamException or OverflowException or KeyNotFoundException)
            {
                var message = $"同 ID 目标 Unit 0x{representative.UnitAssetKey.FileId:x16} 无法解析，已保留来源 Unit：{exception.Message}";
                issues.Add(new CoreIssue(CoreIssueSeverity.Warning, "CanonicalTargetUnitOpaqueRetained", message, archiveId, source.Id, exception.ToString()));
                foreach (var member in recipe.Members)
                {
                    var memberEntry = sourceEntries.Single(entry => entry.AssetKey == new AdaptationAssetKey(member.UnitAssetKey.TypeId, member.UnitAssetKey.FileId));
                    jobs.Add(PatchWorkspaceJobResult.Unit(CreateSourcePassthroughEntry(memberEntry), $"0x{member.UnitAssetKey.FileId:x16}"));
                }
                artifacts.Log($"[OPAQUE-RETAIN] Unit=0x{representative.UnitAssetKey.FileId:x16}; Route=TargetParseFailed; Reason={exception.Message}");
                recipeIndex++;
                Report(progress, operationId, "ExecuteRecipes", $"已保留不透明配方 {recipeIndex}/{recipes.Count}，覆盖 Unit {jobs.Count}/{plan.Units.Count}", recipeIndex, recipes.Count);
                continue;
            }
            var sourceCapability = CanonicalUnitCapabilityDetector.Analyze(sourceUnit.Model);
            var targetCapability = CanonicalUnitCapabilityDetector.Analyze(targetUnit.Model);
            artifacts.Log($"[UNIT-CAPABILITY] Unit=0x{representative.UnitAssetKey.FileId:x16}; SourceLayout={sourceCapability.Layout}; TargetLayout={targetCapability.Layout}; SourceRoute={sourceCapability.PreferredRoute}; TargetRoute={targetCapability.PreferredRoute}; SourceSkinning={sourceCapability.HasSkinning}; TargetSkinning={targetCapability.HasSkinning}; PlayerEquipment={targetCapability.IsLikelyPlayerEquipment}; Reason={targetCapability.Reason}");
            if (!sourceCapability.CanCanonicalRebuild || !targetCapability.CanCanonicalRebuild)
            {
                var message = $"Unit 0x{representative.UnitAssetKey.FileId:x16} 未进入 Canonical：来源={sourceCapability.PreferredRoute}/{sourceCapability.Reason}，目标={targetCapability.PreferredRoute}/{targetCapability.Reason}。当前按不透明来源 Unit 保留。";
                issues.Add(new CoreIssue(CoreIssueSeverity.Warning, "CanonicalUnitOpaqueRetained", message, sourceEntry.SourceFilePath, source.Id));
                foreach (var member in recipe.Members)
                {
                    var memberEntry = sourceEntries.Single(entry => entry.AssetKey == new AdaptationAssetKey(member.UnitAssetKey.TypeId, member.UnitAssetKey.FileId));
                    jobs.Add(PatchWorkspaceJobResult.Unit(CreateSourcePassthroughEntry(memberEntry), $"0x{member.UnitAssetKey.FileId:x16}"));
                }
                artifacts.Log($"[OPAQUE-RETAIN] Unit=0x{representative.UnitAssetKey.FileId:x16}; SourceRoute={sourceCapability.PreferredRoute}; TargetRoute={targetCapability.PreferredRoute}");
                recipeIndex++;
                Report(progress, operationId, "ExecuteRecipes", $"已保留不透明配方 {recipeIndex}/{recipes.Count}，覆盖 Unit {jobs.Count}/{plan.Units.Count}", recipeIndex, recipes.Count);
                continue;
            }
            var hiddenSource = UnitSourceVisibilityClassifier.Classify(sourceUnit);
            if (useSharedHiddenUnitTemplate && hiddenSource.IsHidden && sharedHiddenTemplate is not null)
            {
                foreach (var member in recipe.Members)
                {
                    var memberKey = new AdaptationAssetKey(member.UnitAssetKey.TypeId, member.UnitAssetKey.FileId);
                    var memberEntry = memberKey == sharedHiddenTemplate.Key
                        ? sharedHiddenTemplate
                        : operationWorkspace.Reference(sharedHiddenTemplate, memberKey);
                    jobs.Add(PatchWorkspaceJobResult.Unit(memberEntry, $"0x{member.UnitAssetKey.FileId:x16}"));
                }
                detectedHiddenSourceRecipeCount++;
                minifiedMeshCount += sharedHiddenMeshCount * recipe.Members.Count;
                minifyOnlyUnitCount += recipe.Members.Count;
                sharedHiddenReuseCount += recipe.Members.Count;
                artifacts.Log($"[HIDDEN-TEMPLATE-REUSE] Members={recipe.Members.Count}; Template=0x{sharedHiddenTemplate.Key.FileId:x16}; SourceHidden=True; Reason={hiddenSource.Reason}");
                recipeIndex++;
                Report(progress, operationId, "ExecuteRecipes", $"已完成配方 {recipeIndex}/{recipes.Count}，覆盖 Unit {jobs.Count}/{plan.Units.Count}", recipeIndex, recipes.Count);
                continue;
            }

            var mappings = hiddenSource.IsHidden
                ? []
                : BuildMappings(sourceEntry.AssetKey, sourceUnit.Model, targetUnit.Model, representative.IsSourceGeometryEligible).ToArray();
            SameKeyCanonicalUnitRebuildResult result;
            if (mappings.Length == 0)
            {
                CanonicalHiddenUnitOutput hidden;
                try
                {
					// Shared templates must preserve culling just like the ordinary hidden path.
					hidden = hiddenUnitBuilder.Build(targetUnit, avatarTransforms);
                }
                catch (Exception exception) when (useSharedHiddenUnitTemplate && hiddenSource.IsHidden && exception is InvalidDataException or InvalidOperationException)
                {
                    // A shared template is an optimization; preserve the established per-target hide path when a target cannot form one.
                    artifacts.Log($"[HIDDEN-TEMPLATE-FALLBACK] Unit=0x{sourceEntry.AssetKey.FileId:x16}; Reason={exception.Message}");
                    hidden = hiddenUnitBuilder.Build(targetUnit, avatarTransforms);
                }
                result = new SameKeyCanonicalUnitRebuildResult(PatchWorkspaceJobResult.Unit(hidden.Entry), 0, hidden.HiddenMeshCount, []);
            }
            else
            {
                result = new SameKeyCanonicalUnitRebuilder().Rebuild(new SameKeyCanonicalUnitRebuildRequest(sourceUnit, targetUnit, mappings)
                {
                    AvatarTransformInfo = avatarTransforms
                });
            }
            if (!result.IsValid || result.Job is null || result.Job.Outputs.Count != 1)
            {
                issues.AddRange(result.Diagnostics.Select(diagnostic => Error(diagnostic.Code, $"Unit=0x{representative.UnitAssetKey.FileId:x16}; {diagnostic.Message}", source.Id)));
                continue;
            }
            // Cross the recipe boundary as file-backed payloads. This releases the
            // generated GPU/TOC/Stream arrays from the long-lived jobs list while
            // preserving one physical payload set for every reused member.
            var template = operationWorkspace.Stage(result.Job).Outputs[0];
            if (useSharedHiddenUnitTemplate && hiddenSource.IsHidden && sharedHiddenTemplate is null)
            {
                sharedHiddenTemplate = template;
                sharedHiddenMeshCount = result.HiddenMeshCount;
				artifacts.Log($"[HIDDEN-TEMPLATE] Unit=0x{sourceEntry.AssetKey.FileId:x16}; HiddenMeshes={result.HiddenMeshCount}; Culling=Preserved; Staged=True");
            }
            foreach (var member in recipe.Members)
            {
                var memberKey = new AdaptationAssetKey(member.UnitAssetKey.TypeId, member.UnitAssetKey.FileId);
                var memberEntry = memberKey == template.Key
                    ? template
                    : operationWorkspace.Reference(template, memberKey);
                jobs.Add(PatchWorkspaceJobResult.Unit(memberEntry, $"0x{member.UnitAssetKey.FileId:x16}"));
            }
            replacementMeshCount += result.ReplacedMeshCount * recipe.Members.Count;
            minifiedMeshCount += result.HiddenMeshCount * recipe.Members.Count;
            if (hiddenSource.IsHidden) detectedHiddenSourceRecipeCount++;
            else visibleRebuildRecipeCount++;
            if (result.ReplacedMeshCount != 0) replacementUnitCount += recipe.Members.Count;
            else minifyOnlyUnitCount += recipe.Members.Count;
            artifacts.Log($"[RECIPE] Key={recipe.Key[..Math.Min(recipe.Key.Length, 48)]}; Members={recipe.Members.Count}; SourceHidden={hiddenSource.IsHidden}; HiddenReason={hiddenSource.Reason}; Hidden={mappings.Length == 0}; Replaced={result.ReplacedMeshCount}; Minified={result.HiddenMeshCount}");
            recipeIndex++;
            Report(progress, operationId, "ExecuteRecipes", $"已完成配方 {recipeIndex}/{recipes.Count}，覆盖 Unit {jobs.Count}/{plan.Units.Count}", recipeIndex, recipes.Count);
        }

        if (issues.Any(issue => issue.Severity == CoreIssueSeverity.Error)) return Failure(issues);
        Report(progress, operationId, "WriteCandidate", "正在打包 Canonical Patch", 0, 1);
        // Unit entries are replaced by jobs. Composite/Bone/other dependencies
        // remain available as opaque source entries until the generic dependency
        // closure can prove that they are safe to remove. This is essential for
        // non-player and Composite-backed Units, which are currently retained.
        var removed = index.Entries.Where(entry => entry.AssetKey.TypeId == PatchUnitMeshReader.UnitTypeId).Select(entry => entry.AssetKey).ToHashSet();
        var write = await workspaceWriter.WriteAsync(index, jobs, removed, output, Path.GetFileName(patch),
            headerTemplateTocData: (await resolver.GetPackageTocAsync(plan.Units.First().TargetArchive!.ArchiveId, cancellationToken).ConfigureAwait(false))?.Data,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        Report(progress, operationId, "WriteCandidate", "Canonical Patch 打包完成", 1, 1);
        artifacts.Log($"[WRITE-DONE] VisibleRebuildRecipes={visibleRebuildRecipeCount}; Units={jobs.Count}; Replacements={replacementMeshCount}; Hidden={minifiedMeshCount}; HiddenSourceRecipes={detectedHiddenSourceRecipeCount}; SharedHiddenTemplate={useSharedHiddenUnitTemplate}; SharedHiddenReuse={sharedHiddenReuseCount}");
        await artifacts.WriteReportAsync("WrittenForGameTest", $"可见重建配方={visibleRebuildRecipeCount}; Unit={jobs.Count}; 替换Mesh={replacementMeshCount}；极小化Mesh={minifiedMeshCount}", issues.Select(issue => issue.Message).ToArray(), cancellationToken).ConfigureAwait(false);
        return new SameKeyReconstructionOperationResult(true, output, null, artifacts.ReportPath, jobs.Count,
            replacementUnitCount, minifyOnlyUnitCount, replacementMeshCount, minifiedMeshCount, issues);
    }

    private sealed class SameKeyExecutionRecipe(
        string key,
        SameKeyUnitReconstructionPlan representative)
    {
        public string Key { get; } = key;
        public SameKeyUnitReconstructionPlan Representative { get; } = representative;
        public List<SameKeyUnitReconstructionPlan> Members { get; } = [];
    }

    private static CanonicalPatchSessionEntry CreateSourcePassthroughEntry(
        HD2ModAdaptation.PatchReconstruction.PatchTocEntry entry)
    {
        var tocPath = entry.SourceFilePath;
        var gpuPath = tocPath + ".gpu_resources";
        var streamPath = tocPath + ".stream";
        return new CanonicalPatchSessionEntry(
            entry.AssetKey,
            CanonicalPatchEntryOwnership.TargetOutput,
            entry.TocDataSize == 0 ? Array.Empty<byte>() : null,
            entry.GpuResourceSize == 0 ? Array.Empty<byte>() : null,
            entry.StreamSize == 0 ? Array.Empty<byte>() : null,
            entry.Unknown1,
            entry.Unknown2,
            entry.Unknown3,
            entry.Unknown4)
        {
            TocDataSource = entry.TocDataSize == 0 ? null : new CanonicalPayloadSourceRange(tocPath, entry.TocDataOffset, entry.TocDataSize),
            GpuDataSource = entry.GpuResourceSize == 0 ? null : new CanonicalPayloadSourceRange(gpuPath, entry.GpuResourceOffset, entry.GpuResourceSize),
            StreamDataSource = entry.StreamSize == 0 ? null : new CanonicalPayloadSourceRange(streamPath, entry.StreamOffset, entry.StreamSize)
        };
    }

    private async ValueTask<SameKeyReconstructionPlan> PlanAsync(
        ModNode source,
        string modsRootDirectory,
        string patch,
        string gameData,
        CancellationToken cancellationToken,
        IProgress<OperationProgressEvent>? progress,
        Guid? operationId,
        ModInformationReadRequest sourceReadRequest,
        PatchWorkspaceIndex? knownIndex = null)
    {
        var nodeId = source.Id;
        var index = knownIndex;
        var entries = index?.Entries;
        if (entries is null || entries.Count == 0)
        {
            // Cache entries may be unavailable for old cache versions; TOC metadata is
            // still cheap to read and does not decode Unit payloads.
            index = await ReadSourcePatchIndexAsync(sourceReadRequest, cancellationToken).ConfigureAwait(false);
            entries = index.Entries;
        }
        var units = entries.Where(entry => entry.AssetKey.TypeId == PatchUnitMeshReader.UnitTypeId).ToArray();
        // This is deliberately a TOC-level candidate set. Each Unit job performs the
        // definitive visible-geometry check while it reads the source payload.
        var sourceEligibility = new SourceUnitEligibilitySelection(
            units.Select(entry => new CoreAssetKey(entry.AssetKey.TypeId, entry.AssetKey.FileId)).ToHashSet(),
            Array.Empty<SourceUnitEligibility>());
        var matches = await assetIndex.FindAssetArchivesAsync(units.Select(entry => new CoreAssetKey(entry.AssetKey.TypeId, entry.AssetKey.FileId)).ToHashSet(), cancellationToken).ConfigureAwait(false);
        var byKey = matches.ToDictionary(match => match.AssetKey);
        var plans = new List<SameKeyUnitReconstructionPlan>();
        foreach (var entry in units)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = new CoreAssetKey(entry.AssetKey.TypeId, entry.AssetKey.FileId);
            var issues = new List<CoreIssue>();
            if (!byKey.TryGetValue(key, out var match) || match.Archives.Count == 0)
            {
                plans.Add(new SameKeyUnitReconstructionPlan(key, ToCoreEntry(entry), null, [], null, [Error("CurrentTargetMissing", "找不到同 ID current target Unit。", nodeId)], IsSourceGeometryEligible: sourceEligibility.EligibleUnitAssetKeys.Contains(key)));
                continue;
            }
            var archive = match.Archives.OrderBy(item => item.CategoryOrder).ThenBy(item => item.ArchiveOrder).First();
            plans.Add(new SameKeyUnitReconstructionPlan(
                key,
                ToCoreEntry(entry),
                archive,
                match.Archives,
                null,
                issues,
                // The Unit job is the authoritative visible-geometry check. Planning
                // only consumes TOC metadata and therefore never needs full analysis.
                IsSourceGeometryEligible: true));
            Report(progress, operationId, "Plan", $"已规划 Unit {plans.Count}/{units.Length}", plans.Count, units.Length);
        }
        return new SameKeyReconstructionPlan(
            new SameKeyReconstructionRequest(patch, gameData),
            plans,
            [],
            plans.Select(plan => plan.UnitAssetKey).ToHashSet());
    }

    private sealed record SameKeyUnitJobResult(
        int Sequence,
		CoreAssetKey UnitKey,
        TimeSpan SourceRead,
        TimeSpan TargetRead,
        TimeSpan Mapping,
        TimeSpan RebuildElapsed,
        SameKeyCanonicalUnitRebuildResult Rebuild,
		bool HiddenCacheHit,
		bool PlannedReplacement,
		int MeshCount,
		int VertexCount,
		int TriangleCount,
		long AllocationBefore,
		int Gen0Before,
		int Gen1Before,
		int Gen2Before);

	private static CanonicalUnitJobTelemetryRow CreateUnitJobTelemetryRow(SameKeyUnitJobResult result, TimeSpan staging)
	{
		var telemetry = result.Rebuild.Telemetry ?? CanonicalUnitRebuildTelemetry.Empty;
		return new CanonicalUnitJobTelemetryRow(
			"SameKey", result.Sequence + 1, result.UnitKey.FileId,
			result.HiddenCacheHit, result.PlannedReplacement, result.MeshCount, result.VertexCount, result.TriangleCount,
			result.SourceRead, result.TargetRead, result.Mapping, telemetry.TransformExpansion, telemetry.MeshAssembly,
			telemetry.MeshBreakdown, telemetry.BonePalette, telemetry.StreamContract, telemetry.FinalPreparation,
			telemetry.MaterialBindings, telemetry.Serialization, telemetry.SerializationBreakdown, staging, result.RebuildElapsed + staging,
			GC.GetTotalAllocatedBytes(false) - result.AllocationBefore, GC.GetGCMemoryInfo().HeapSizeBytes,
			Environment.WorkingSet, GC.CollectionCount(0) - result.Gen0Before, GC.CollectionCount(1) - result.Gen1Before,
			GC.CollectionCount(2) - result.Gen2Before);
	}

    private static IReadOnlyList<TargetShellMeshMapping> BuildMappings(AdaptationAssetKey sourceKey, UnitMeshModel source, UnitMeshModel target, bool isEligibleSourceUnit)
    {
        if (!isEligibleSourceUnit)
            return Array.Empty<TargetShellMeshMapping>();

		var sourceLod0 = UnitGeometryFactsBuilder.SelectBestRenderableLod0(source);
		var targetLod0 = UnitGeometryFactsBuilder.SelectBestRenderableLod0(target);
		if (sourceLod0 is null || targetLod0 is null)
		{
			// Pure cutout patches deliberately hide every display LOD but retain one or
			// more real LOD=-1 culling shells. Preserve matching shells instead of
			// converting the complete Unit into a hidden placeholder.
			return source.RawMeshData
				.Where(raw => raw.LodIndex == -1 && UnitGeometryFactsBuilder.HasRenderableGeometry(raw))
				.Select(sourceCulling => (Source: sourceCulling, Target: target.RawMeshData.SingleOrDefault(targetCulling =>
					targetCulling.LodIndex == -1 && targetCulling.MeshId == sourceCulling.MeshId && UnitGeometryFactsBuilder.HasRenderableGeometry(targetCulling))))
				.Where(pair => pair.Target is not null)
				.Select(pair => new TargetShellMeshMapping(sourceKey, pair.Source.MeshInfoIndex, pair.Target!.MeshInfoIndex))
				.ToArray();
		}

        var expanded = CanonicalAutoLodMappingExpander.Expand(
            target,
            new Dictionary<AdaptationAssetKey, UnitMeshModel> { [sourceKey] = source },
            [new CanonicalReplacementMapping(
                new CanonicalMeshKey(sourceKey, sourceLod0.MeshInfoIndex),
                new CanonicalMeshKey(sourceKey, targetLod0.MeshInfoIndex),
                SkinningMode: CanonicalSkinningMode.BindStaticToTargetMeshTransform,
                BoneAnchor: CanonicalBoneAnchor.TargetMeshTransform)]);
        return expanded
            .Select(mapping => new TargetShellMeshMapping(sourceKey, mapping.Source.MeshInfoIndex, mapping.Target.MeshInfoIndex))
            .ToArray();
    }

    private static PatchUnitMesh RebindSourceUnit(PatchUnitMesh template, HD2ModAdaptation.PatchReconstruction.PatchTocEntry entry)
        => template with
        {
            Entry = entry,
            Payload = new HD2ModAdaptation.PatchReconstruction.PatchEntryPayload(entry, Array.Empty<byte>(), Array.Empty<byte>(), Array.Empty<byte>())
        };

    // Content-addresses a complete source Unit without loading a shared GPU range more
    // than once. Bone and composite payloads participate because they can alter the
    // decoded mesh semantics even when the primary Unit GPU range is identical.
    private sealed class UnitPayloadFingerprintCache
    {
        private readonly ConcurrentDictionary<PayloadRange, Lazy<Task<byte[]>>> tocRanges = new();
        private readonly ConcurrentDictionary<PayloadRange, Lazy<Task<string>>> hashes = new();

        public async Task<string> CreateAsync(
            HD2ModAdaptation.PatchReconstruction.PatchTocEntry entry,
            IReadOnlyList<HD2ModAdaptation.PatchReconstruction.PatchTocEntry> entries,
            CancellationToken cancellationToken)
        {
            var toc = await ReadTocAsync(entry, cancellationToken).ConfigureAwait(false);
            var parts = new List<string>
            {
                await HashPayloadAsync(entry.SourceFilePath, entry.TocDataOffset, entry.TocDataSize, cancellationToken).ConfigureAwait(false),
                await HashPayloadAsync(entry.SourceFilePath + ".stream", entry.StreamOffset, entry.StreamSize, cancellationToken).ConfigureAwait(false),
                await HashPayloadAsync(entry.SourceFilePath + ".gpu_resources", entry.GpuResourceOffset, entry.GpuResourceSize, cancellationToken).ConfigureAwait(false)
            };
            foreach (var dependency in ResolveDependencies(toc, entries))
            {
                parts.Add(await HashPayloadAsync(dependency.SourceFilePath, dependency.TocDataOffset, dependency.TocDataSize, cancellationToken).ConfigureAwait(false));
                parts.Add(await HashPayloadAsync(dependency.SourceFilePath + ".stream", dependency.StreamOffset, dependency.StreamSize, cancellationToken).ConfigureAwait(false));
                parts.Add(await HashPayloadAsync(dependency.SourceFilePath + ".gpu_resources", dependency.GpuResourceOffset, dependency.GpuResourceSize, cancellationToken).ConfigureAwait(false));
            }
            return string.Join(':', parts);
        }

        public async Task<string> CreateReuseSignatureAsync(
            HD2ModAdaptation.PatchReconstruction.PatchTocEntry entry,
            IReadOnlyList<HD2ModAdaptation.PatchReconstruction.PatchTocEntry> entries,
            CancellationToken cancellationToken)
        {
            // This signature intentionally uses the physical GPU/stream ranges rather
            // than their contents. Equal ranges in the same patch sidecar are the same
            // bytes, so planning never has to load large GPU payloads.
            var toc = await ReadTocAsync(entry, cancellationToken).ConfigureAwait(false);
            var parts = new List<string>
            {
                $"unit-toc={await HashPayloadAsync(entry.SourceFilePath, entry.TocDataOffset, entry.TocDataSize, cancellationToken).ConfigureAwait(false)}",
                $"unit-stream={DescribeRange(entry.SourceFilePath + ".stream", entry.StreamOffset, entry.StreamSize)}",
                $"unit-gpu={DescribeRange(entry.SourceFilePath + ".gpu_resources", entry.GpuResourceOffset, entry.GpuResourceSize)}",
                $"unit-meta={entry.Unknown1:x16}:{entry.Unknown2:x16}:{entry.Unknown3:x8}:{entry.Unknown4:x8}"
            };
            foreach (var dependency in ResolveDependencies(toc, entries)
                .OrderBy(value => value.AssetKey.TypeId).ThenBy(value => value.AssetKey.FileId))
            {
                parts.Add($"dependency={dependency.AssetKey.TypeId:x16}:{dependency.AssetKey.FileId:x16}"
                    + $":{await HashPayloadAsync(dependency.SourceFilePath, dependency.TocDataOffset, dependency.TocDataSize, cancellationToken).ConfigureAwait(false)}"
                    + $":{DescribeRange(dependency.SourceFilePath + ".stream", dependency.StreamOffset, dependency.StreamSize)}"
                    + $":{DescribeRange(dependency.SourceFilePath + ".gpu_resources", dependency.GpuResourceOffset, dependency.GpuResourceSize)}"
                    + $":{dependency.Unknown1:x16}:{dependency.Unknown2:x16}:{dependency.Unknown3:x8}:{dependency.Unknown4:x8}");
            }
            return string.Join('|', parts);
        }

        private async Task<byte[]> ReadTocAsync(HD2ModAdaptation.PatchReconstruction.PatchTocEntry entry, CancellationToken cancellationToken)
        {
            var key = new PayloadRange(entry.SourceFilePath, entry.TocDataOffset, entry.TocDataSize);
            return await tocRanges.GetOrAdd(key, range => new Lazy<Task<byte[]>>(
                () => ReadRangeAsync(range, CancellationToken.None), LazyThreadSafetyMode.ExecutionAndPublication)).Value.ConfigureAwait(false);
        }

        private async Task<string> HashPayloadAsync(string path, ulong offset, uint length, CancellationToken cancellationToken)
        {
            if (length == 0) return "empty";
            var key = new PayloadRange(path, offset, length);
            return await hashes.GetOrAdd(key, range => new Lazy<Task<string>>(
                () => HashRangeAsync(range, CancellationToken.None), LazyThreadSafetyMode.ExecutionAndPublication)).Value.ConfigureAwait(false);
        }

        private static IEnumerable<HD2ModAdaptation.PatchReconstruction.PatchTocEntry> ResolveDependencies(
            byte[] toc,
            IReadOnlyList<HD2ModAdaptation.PatchReconstruction.PatchTocEntry> entries)
        {
            foreach (var (offset, typeId) in new[] { (8, PatchUnitMeshReader.BoneTypeId), (16, PatchUnitMeshReader.CompositeUnitTypeId) })
            {
                if (toc.Length < offset + sizeof(ulong)) continue;
                var reference = BitConverter.ToUInt64(toc.AsSpan(offset, sizeof(ulong)));
                if (reference == 0) continue;
                var dependency = entries.SingleOrDefault(candidate => candidate.AssetKey.TypeId == typeId && candidate.AssetKey.FileId == reference);
                if (dependency is not null) yield return dependency;
            }
        }

        private static async Task<string> HashRangeAsync(PayloadRange range, CancellationToken cancellationToken)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var stream = new FileStream(range.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan | FileOptions.Asynchronous);
            if (range.Offset > (ulong)stream.Length || range.Offset + range.Length > (ulong)stream.Length)
                throw new InvalidDataException($"Payload range is outside '{range.Path}'.");
            stream.Position = checked((long)range.Offset);
            var buffer = new byte[65536];
            var remaining = checked((int)range.Length);
            while (remaining > 0)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), cancellationToken).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException("Payload ended before its declared length.");
                hash.AppendData(buffer, 0, read);
                remaining -= read;
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }

        private static string DescribeRange(string path, ulong offset, uint length)
            => $"{path}:{offset:x16}:{length:x8}";

        private static async Task<byte[]> ReadRangeAsync(PayloadRange range, CancellationToken cancellationToken)
        {
            if (range.Length == 0) return Array.Empty<byte>();
            await using var stream = new FileStream(range.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan | FileOptions.Asynchronous);
            if (range.Offset > (ulong)stream.Length || range.Offset + range.Length > (ulong)stream.Length)
                throw new InvalidDataException($"Payload range is outside '{range.Path}'.");
            stream.Position = checked((long)range.Offset);
            var data = new byte[checked((int)range.Length)];
            await stream.ReadExactlyAsync(data, cancellationToken).ConfigureAwait(false);
            return data;
        }

        private readonly record struct PayloadRange(string Path, ulong Offset, uint Length);
    }

    private IReadOnlyList<string> FindBasePatchPaths(ModNode node, string root)
        => Directory.Exists(Path.Combine(root, node.RelativePath))
            ? Directory.EnumerateFiles(Path.Combine(root, node.RelativePath), "*", SearchOption.TopDirectoryOnly).Where(path => fileNameParser.TryParse(Path.GetFileName(path), out var info) && info?.SidecarKind == PatchSidecarKind.Base).OrderBy(path => path).ToArray()
            : Array.Empty<string>();

    private SourceReadScope BeginSourceReadScope(ModNode source, string patch, Guid? operationId)
    {
        var readerOperationId = operationId.GetValueOrDefault(Guid.NewGuid());
        var context = ModInformationRequestContext.Create(
            ModInformationCacheScope.Operation,
            operationId: readerOperationId,
            sessionId: readerOperationId,
            operationName: "SameKeyReconstruction");
        return new SourceReadScope(
            informationReader,
            readerOperationId,
            new ModInformationReadRequest(
                patch,
                context,
                ContentView: ModInformationContentView.Source,
                NodeId: source.Id));
    }

    private async ValueTask<PatchWorkspaceIndex> ReadSourcePatchIndexAsync(
        ModInformationReadRequest request,
        CancellationToken cancellationToken)
    {
        var result = await informationReader.ReadPatchIndexAsync(request, cancellationToken).ConfigureAwait(false);
        if (result.Data is not null) return result.Data;
        var message = result.State.Diagnostics.Count == 0
            ? "无法读取来源 Patch TOC。"
            : string.Join("; ", result.State.Diagnostics.Select(issue => issue.Message));
        throw new InvalidDataException(message);
    }

    private async ValueTask<PatchUnitMesh> ReadCanonicalSourceUnitAsync(
        HD2ModAdaptation.PatchReconstruction.PatchTocEntry entry,
        IReadOnlyList<HD2ModAdaptation.PatchReconstruction.PatchTocEntry> entries,
        ModInformationReadRequest request,
        CancellationToken cancellationToken)
    {
        // The grouped executor owns the decoded source Unit only for the current
        // recipe. Do not place this large vertex graph in the operation cache; the
        // cache would keep every previous recipe alive until the whole rebuild ends.
        var unitRequest = request with
        {
            Context = request.EffectiveContext with { CacheScope = ModInformationCacheScope.None }
        };
        var result = await informationReader.ReadUnitAsync(
            entry,
            entries,
            PatchUnitDependencyPolicy.RequirePatchLocalComposite,
            unitRequest,
            canonicalSource: true,
            cancellationToken).ConfigureAwait(false);
        if (result.Data is not null) return result.Data;
        var message = result.State.Diagnostics.Count == 0
            ? $"无法读取来源 Unit 0x{entry.AssetKey.FileId:x16}。"
            : string.Join("; ", result.State.Diagnostics.Select(issue => issue.Message));
        throw new InvalidDataException(message);
    }

    private sealed class SourceReadScope(IModInformationReader reader, Guid operationId, ModInformationReadRequest request) : IDisposable
    {
        public ModInformationReadRequest Request { get; } = request;

        public void Dispose() => reader.ClearOperation(operationId);
    }

    private static HD2ModCore.Domain.PatchTocEntry ToCoreEntry(HD2ModAdaptation.PatchReconstruction.PatchTocEntry entry) => new(new CoreAssetKey(entry.AssetKey.TypeId, entry.AssetKey.FileId), entry.SourceFilePath, entry.SourceFileName, entry.TocDataOffset, entry.StreamOffset, entry.GpuResourceOffset, entry.Unknown1, entry.Unknown2, entry.TocDataSize, entry.StreamSize, entry.GpuResourceSize, entry.Unknown3, entry.Unknown4, entry.EntryIndex);
    private static CoreIssue Error(string code, string message, ModNodeId nodeId) => new(CoreIssueSeverity.Error, code, message, NodeId: nodeId);
    private static SameKeyReconstructionOperationResult Failure(IReadOnlyList<CoreIssue> issues) => new(false, null, null, null, 0, 0, 0, 0, 0, issues);

    private static void Report(IProgress<OperationProgressEvent>? progress, Guid? operationId, string stageId, string message, long completed, long total)
    {
        if (progress is null) return;
        var id = operationId.GetValueOrDefault(Guid.NewGuid());
        var sequence = progressSequences.AddOrUpdate(id, 1, static (_, previous) => previous + 1);
        progress.Report(new OperationProgressEvent(
            id, null, OperationKind.PatchRepair, OperationStage.Processing,
            OperationState.Progress, completed, total, message, null, DateTimeOffset.UtcNow, sequence, stageId, message));
    }
}
