using HD2ModAdaptation.PatchReconstruction;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModAdaptation.PatchReconstruction.UnitMesh.Canonical;
using HD2ModAdaptation.PatchReconstruction.PatchWorkspace;
using HD2ModCore.Domain;
using HD2ModCore.Application;
using AdaptationAssetKey = HD2ModAdaptation.PatchReconstruction.AssetKey;
using CoreAssetKey = HD2ModCore.Domain.AssetKey;
using AdaptationPatchTocEntry = HD2ModAdaptation.PatchReconstruction.PatchTocEntry;
using AdaptationPatchEntryPayload = HD2ModAdaptation.PatchReconstruction.PatchEntryPayload;
using AdaptationGameDataPackageResolver = HD2ModAdaptation.PatchReconstruction.GameDataPackageResolver;
using System.Text;
using System.Text.RegularExpressions;
using System.Buffers.Binary;

namespace HD2ModCore.Infrastructure;

// Purpose: Orchestrates the isolated canonical replacement chain without entering legacy CrossArmor operations.
// SDK order: GetEntryByLoadArchive(IgnorePatch=True) -> Load -> AddEntryToPatchID -> RawMeshes[MeshInfoIndex] = mesh -> Entry.Save.
// Documentation: docs/sdk流程架构.md sections 1-8; tools/ref/HD2SDK-CommunityEdition/stingray/unit.py
// SDK references: TocEntry.SerializeData() makes each emitted TOC/GPU/stream payload entry-owned and StreamToc.Serialize()
// immediately rereads the final layout; unmatched target meshes use the isolated canonical tiny path.
public sealed class CanonicalCrossArmorOrchestrator
{
	private sealed record CanonicalRebuildSummary(
		int MeshCount,
		int StreamCount,
		int MaterialBindingCount,
		int RawMeshCount,
		int BoneInfoCount,
		int TransformNameHashCount,
		int TransformEntryCount,
		int TransformMatrixCount,
		IReadOnlyList<UnitRawMeshSummary> RawMeshes,
		IReadOnlyList<(int Index, uint NumVertices, uint NumIndices, uint VertexBufferSize, uint IndexBufferSize)> Streams,
		IReadOnlyList<(int Index, int RealIndicesCount, int RemapsCount)> BoneInfos);

	private readonly IModInformationReader informationReader;
	private readonly Func<string, GameDataUnitMeshReader> targetReaderFactory;
	private readonly CanonicalMeshSemanticMerger merger;
	private readonly CanonicalTransformResolver transformResolver;
	private readonly CanonicalBoneRebuilder boneRebuilder;
	private readonly CanonicalMeshSkinningRouter skinningRouter;
	private readonly CanonicalLodBonePaletteCompiler lodBonePaletteCompiler;
	private readonly CanonicalStreamContractCompiler streamContractCompiler;
	private readonly CanonicalMeshPreparation preparation;
	private readonly IHiddenUnitGenerator hiddenUnitGenerator;
	private readonly CanonicalTransformInfoExpander transformInfoExpander;
	private readonly CanonicalStaticMeshBinder staticMeshBinder;
	private readonly CanonicalUnitRebuilder rebuilder;
	private readonly IPatchWorkspaceWriter patchWorkspaceWriter;
	private readonly IPatchWorkspaceSessionComposer patchWorkspaceSessionComposer;
	private readonly StingrayMaterialReferenceReader materialReferenceReader;
	private readonly IPatchOperationWorkspaceFactory operationWorkspaceFactory;
	private readonly ICanonicalHiddenUnitOutputCache hiddenUnitCache;
	private readonly IAssetArchiveIndexService assetIndex;
	private readonly IArchiveHashesProvider archiveHashes;

	private sealed class CanonicalMarkdownReportState
	{
		public string Status { get; set; } = "Running";
		public List<string> Logs { get; } = [];
		public void Log(string message) => Logs.Add($"[{DateTimeOffset.Now:HH:mm:ss.fff}] {message}");
	}

	private static void ReportProgress(CrossArmorTransferCandidateRequest request, string stage, string text, int completed, int total, System.Diagnostics.Stopwatch stopwatch)
		=> request.Progress?.Report(new CrossArmorTransferProgress(stage, text, Math.Clamp(completed, 0, total), total, stopwatch.Elapsed));

	private static CanonicalRebuildSummary CreateRebuildSummary(AdaptationAssetKey key, UnitMeshModel model)
		=> new(
			model.Meshes.Count,
			model.Streams.Count,
			model.Materials.Count,
			model.RawMeshes.Count,
			model.BoneInfos.Count,
			model.TransformNameHashes.Count,
			model.TransformInfo.Entries.Count,
			model.TransformInfo.Matrices.Count,
			model.RawMeshes.OrderBy(mesh => mesh.MeshInfoIndex).ToArray(),
			model.Streams.OrderBy(stream => stream.Index).Select(stream => (Index: stream.Index, NumVertices: stream.NumVertices, NumIndices: stream.NumIndices, VertexBufferSize: stream.VertexBufferSize, IndexBufferSize: stream.IndexBufferSize)).ToArray(),
			model.BoneInfos.Select(bone => (Index: bone.Index, RealIndicesCount: bone.RealIndices.Count, RemapsCount: bone.Remaps.Count)).ToArray());

	public CanonicalCrossArmorOrchestrator(
		PatchUnitMeshReader? sourceReader = null,
		Func<string, GameDataUnitMeshReader>? targetReaderFactory = null,
		CanonicalMeshSemanticMerger? merger = null,
		CanonicalTransformResolver? transformResolver = null,
		CanonicalBoneRebuilder? boneRebuilder = null,
		CanonicalMeshSkinningRouter? skinningRouter = null,
		CanonicalLodBonePaletteCompiler? lodBonePaletteCompiler = null,
		CanonicalStreamContractCompiler? streamContractCompiler = null,
		CanonicalMeshPreparation? preparation = null,
		CanonicalPlaceholderMinifier? placeholderMinifier = null,
		CanonicalTransformInfoExpander? transformInfoExpander = null,
		CanonicalStaticMeshBinder? staticMeshBinder = null,
		CanonicalUnitRebuilder? rebuilder = null,
		HD2ModAdaptation.PatchReconstruction.IPatchEntryPayloadReader? sourcePayloadReader = null,
		StingrayMaterialReferenceReader? materialReferenceReader = null,
		IHiddenUnitGenerator? hiddenUnitGenerator = null,
		IPatchWorkspaceWriter? patchWorkspaceWriter = null,
		IPatchWorkspaceSessionComposer? patchWorkspaceSessionComposer = null,
		IPatchWorkspaceReader? patchWorkspaceReader = null,
		IPatchOperationWorkspaceFactory? operationWorkspaceFactory = null,
		ICanonicalHiddenUnitOutputCache? hiddenUnitCache = null,
		IAssetArchiveIndexService? assetIndex = null,
		IArchiveHashesProvider? archiveHashes = null,
		IModInformationReader? informationReader = null)
	{
		this.informationReader = informationReader ?? new ModInformationReader(patchWorkspaceReader, sourcePayloadReader, sourceReader);
		this.targetReaderFactory = targetReaderFactory ?? new Func<string, GameDataUnitMeshReader>(directory => new GameDataUnitMeshReader(new AdaptationGameDataPackageResolver(directory)));
		this.merger = merger ?? new CanonicalMeshSemanticMerger();
		this.transformResolver = transformResolver ?? new CanonicalTransformResolver();
		this.boneRebuilder = boneRebuilder ?? new CanonicalBoneRebuilder();
		this.skinningRouter = skinningRouter ?? new CanonicalMeshSkinningRouter(this.boneRebuilder, staticMeshBinder);
		this.lodBonePaletteCompiler = lodBonePaletteCompiler ?? new CanonicalLodBonePaletteCompiler();
		this.streamContractCompiler = streamContractCompiler ?? new CanonicalStreamContractCompiler();
		this.preparation = preparation ?? new CanonicalMeshPreparation();
		this.hiddenUnitGenerator = hiddenUnitGenerator ?? new HiddenUnitGenerator(placeholderMinifier ?? new CanonicalPlaceholderMinifier());
		this.transformInfoExpander = transformInfoExpander ?? new CanonicalTransformInfoExpander();
		this.staticMeshBinder = staticMeshBinder ?? new CanonicalStaticMeshBinder();
		this.rebuilder = rebuilder ?? new CanonicalUnitRebuilder();
		this.patchWorkspaceWriter = patchWorkspaceWriter ?? new PatchWorkspaceWriter();
		this.patchWorkspaceSessionComposer = patchWorkspaceSessionComposer ?? new PatchWorkspaceSessionComposer();
		this.materialReferenceReader = materialReferenceReader ?? new StingrayMaterialReferenceReader();
		this.operationWorkspaceFactory = operationWorkspaceFactory ?? new PatchOperationWorkspaceFactory();
		this.hiddenUnitCache = hiddenUnitCache ?? new CanonicalHiddenUnitOutputCache();
		var storagePaths = new StoragePaths(AppContext.BaseDirectory);
		this.assetIndex = assetIndex ?? new AssetArchiveIndexService(storagePaths);
		this.archiveHashes = archiveHashes ?? new FileSystemArchiveHashesProvider(storagePaths);
	}

	// Cross-armor production is intentionally routed through direct source Unit
	// reuse. The Canonical branch remains an explicit diagnostic fallback for
	// callers that request it; Same-key reconstruction uses Canonical separately.
	private static bool UsesCanonicalCrossArmorFallback(CrossArmorTransferCandidateRequest request)
		=> !request.DirectSourceUnitReuse;

	public async ValueTask<CrossArmorTransferCandidateResult> ExecuteAsync(
		CrossArmorTransferCandidateRequest request,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);
		var issues = new List<CoreIssue>();
		var totalStopwatch = System.Diagnostics.Stopwatch.StartNew();
		var currentCanonicalUnit = "unknown";
		var currentCanonicalMesh = "none";
		var currentCanonicalPhase = "initializing";
		var currentCanonicalSource = "none";
		var reportState = new CanonicalMarkdownReportState();
		var unitTelemetry = new List<CanonicalUnitJobTelemetryRow>();
		CanonicalDiagnosticArtifacts? artifacts = null;
		using var positionDiagnostics = CanonicalPositionDiagnostics.Suppress();
		string? reportPath = null;
		string? unitTelemetryPath = null;
		void Log(string message)
		{
			reportState.Log(message);
			artifacts?.Log(message);
		}
		if (!request.Plan.CanContinue)
			return Failure(issues, "CanonicalPlanNotReady", "Canonical 链路要求现有 CrossArmorTransferPlan 已通过校验。");
		if (!File.Exists(request.SourcePatchTocPath))
			return Failure(issues, "CanonicalSourcePatchMissing", "Canonical 链路找不到 source patch TOC。");
		if (!Directory.Exists(request.GameDataDirectory))
			return Failure(issues, "CanonicalGameDataMissing", "Canonical 链路找不到 Game Data 目录。");
		var indexStatus = await assetIndex.GetIndexStatusAsync(request.GameDataDirectory, await archiveHashes.GetArchiveHashesJsonAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
		if (indexStatus.IsCurrent && !string.IsNullOrWhiteSpace(indexStatus.CurrentSourceFingerprint))
			await hiddenUnitCache.InitializeAsync(indexStatus.CurrentSourceFingerprint, gameDataIndexIsCurrent: true, cancellationToken).ConfigureAwait(false);
		using var sourceReadScope = BeginSourceReadScope(request);
		var sourceReadRequest = sourceReadScope.Request;

		try
		{
			Directory.CreateDirectory(Path.GetFullPath(request.OutputDirectory));
			artifacts = new CanonicalDiagnosticArtifacts(Path.GetFullPath(request.OutputDirectory), "CrossArmor");
			reportPath = Path.Combine(Path.GetFullPath(request.OutputDirectory), "canonical-report.md");
			unitTelemetryPath = artifacts.TelemetryPath;
			Log($"[START] SourcePatch={Path.GetFileName(request.SourcePatchTocPath)} Output={request.OutputDirectory} DirectSourceUnitReuse={request.DirectSourceUnitReuse}");
			await WriteMarkdownReportAsync(reportPath, request, reportState, [], [], new Dictionary<AdaptationAssetKey, CanonicalRebuildSummary>(), [], null, null, cancellationToken).ConfigureAwait(false);
			ReportProgress(request, "CanonicalPreparing", "正在准备 Canonical 跨护甲重建。", 0, 1, totalStopwatch);
			var replacementPlanMappings = request.Plan.Mappings
				.Where(mapping => mapping.WillReplace)
				.Select(mapping => new CanonicalReplacementMapping(
					new(new AdaptationAssetKey(mapping.Source!.UnitAssetKey.TypeId, mapping.Source.UnitAssetKey.FileId), mapping.Source.MeshInfoIndex),
					new(new AdaptationAssetKey(mapping.PhysicalTarget.UnitAssetKey.TypeId, mapping.PhysicalTarget.UnitAssetKey.FileId), mapping.Target.MeshInfoIndex),
					SkinningMode: CanonicalSkinningMode.BindStaticToTargetMeshTransform,
					BoneAnchor: CanonicalBoneAnchor.TargetMeshTransform))
				.ToArray();
			Log($"[PLAN] Mappings={replacementPlanMappings.Length} Targets={request.Plan.SelectedTargets.Count}");
			var planValidation = replacementPlanMappings.Length == 0
				? null
				: CanonicalReplacementPlan.TryCreate(replacementPlanMappings);
			if (planValidation is { IsValid: false })
				return Failure(issues, planValidation.Diagnostics);

			var indexedSourceWorkspace = await ReadSourcePatchIndexAsync(sourceReadRequest, cancellationToken).ConfigureAwait(false);
			var sourceWorkspaceIndex = request.PreparedSourceEntries is { Count: > 0 }
				? indexedSourceWorkspace with { Entries = request.PreparedSourceEntries }
				: indexedSourceWorkspace;
			var sourceEntries = sourceWorkspaceIndex.Entries;
			var sourceByKey = sourceEntries.ToDictionary(entry => entry.AssetKey);
			var sourceKeys = replacementPlanMappings.Select(mapping => mapping.Source.UnitKey).Distinct().ToArray();
			if (sourceKeys.Any(key => !sourceByKey.ContainsKey(key)))
				return Failure(issues, "CanonicalSourceUnitMissing", "Canonical 计划引用的 source Unit 不在 source patch 中。");

			var sourceReadElapsed = TimeSpan.Zero;
			var targetReadElapsed = TimeSpan.Zero;
			var mappingElapsed = TimeSpan.Zero;
			var rebuildElapsed = TimeSpan.Zero;
			var stagingElapsed = TimeSpan.Zero;
			var rebuildTelemetry = new CanonicalUnitRebuildTelemetryAccumulator();
			var sourceUnits = new Dictionary<AdaptationAssetKey, PatchUnitMesh>();
			var directSourcePayloads = new Dictionary<AdaptationAssetKey, AdaptationPatchEntryPayload>();
			var sourceReadJobs = sourceKeys.Select((key, index) => (Sequence: index, UnitKey: $"0x{key.FileId:x16}")).ToArray();
			if (request.DirectSourceUnitReuse)
			{
				if (!indexStatus.IsCurrent)
					Log("[DIRECT-PREFLIGHT] GameDataIndex=stale Action=PreserveSource");
				var directPayloads = await UnitJobExecutor.ExecuteAsync(
					sourceReadJobs,
					async (index, token) =>
					{
						var stopwatch = System.Diagnostics.Stopwatch.StartNew();
						var payload = await ReadSourcePayloadAsync(sourceByKey[sourceKeys[index]], sourceReadRequest, token).ConfigureAwait(false);
						return (Payload: payload, Elapsed: stopwatch.Elapsed);
					},
					cancellationToken: cancellationToken).ConfigureAwait(false);
				for (var index = 0; index < sourceKeys.Length; index++)
				{
					sourceReadElapsed += directPayloads[index].Elapsed;
					directSourcePayloads.Add(sourceKeys[index], directPayloads[index].Payload);
				}
			}
			#pragma warning disable CS0618 // The compatibility branch is intentionally kept behind an obsolete marker.
			else if (UsesCanonicalCrossArmorFallback(request))
			{
				Log("[OBSOLETE-ROUTE] CrossArmor=CanonicalFullRebuild Action=CompatibilityOnly");
				var sourceReadResults = await UnitJobExecutor.ExecuteAsync(
					sourceReadJobs,
					async (index, token) =>
					{
						var stopwatch = System.Diagnostics.Stopwatch.StartNew();
						var unit = await ReadSourceUnitAsync(sourceByKey[sourceKeys[index]], sourceEntries, sourceReadRequest, token).ConfigureAwait(false);
						return (Unit: unit, Elapsed: stopwatch.Elapsed);
					},
					cancellationToken: cancellationToken).ConfigureAwait(false);
				foreach (var (key, index) in sourceKeys.Select((key, index) => (key, index)))
				{
					sourceReadElapsed += sourceReadResults[index].Elapsed;
					sourceUnits.Add(key, sourceReadResults[index].Unit);
				}
			}
			#pragma warning restore CS0618

			var targetReader = targetReaderFactory(request.GameDataDirectory);
			var canonicalAvatarTransforms = request.DirectSourceUnitReuse
				? UnitTransformInfo.Empty
				: await new CanonicalAvatarRigReader(new AdaptationGameDataPackageResolver(request.GameDataDirectory)).ReadTransformInfoAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
			var outputEntries = new List<CanonicalPatchSessionEntry>();
			var directAuxiliaryKeys = new HashSet<AdaptationAssetKey>();
			var workspaceJobs = new List<PatchWorkspaceJobResult>();
			using var operationWorkspace = operationWorkspaceFactory.Create(request.OutputDirectory, "cross-armor-transfer");
			var directSourceEntries = sourceKeys.ToDictionary(
				key => key,
				key => CreateDirectSourceReuseEntry(key, sourceByKey[key]));
			if (request.DirectSourceUnitReuse && indexStatus.IsCurrent)
			{
				var coreSourceKeys = sourceKeys.Select(key => new CoreAssetKey(key.TypeId, key.FileId)).ToHashSet();
				var sourceArchives = await assetIndex.FindAssetArchivesAsync(coreSourceKeys, cancellationToken).ConfigureAwait(false);
				var sourceArchiveByKey = sourceArchives.ToDictionary(match => new AdaptationAssetKey(match.AssetKey.TypeId, match.AssetKey.FileId));
				UnitTransformInfo? sourceRepairAvatar = null;
				foreach (var sourceKey in sourceKeys)
				{
					var sourcePayload = directSourcePayloads[sourceKey];
					if (sourcePayload.TocData.Length < 0x30)
						return Failure(issues, "DirectReuseSourceTocTooShort", $"快速复用来源 Unit 0x{sourceKey.FileId:x16} 的 TOC 过短，无法执行版本预检。");
					if (!sourceArchiveByKey.TryGetValue(sourceKey, out var sourceMatch) || sourceMatch.Archives.Count == 0)
					{
						Log($"[DIRECT-PREFLIGHT] Source=0x{sourceKey.FileId:x16} SameKeyTarget=missing Action=PreserveSource");
						continue;
					}
					var sourceVersion = BinaryPrimitives.ReadUInt32LittleEndian(sourcePayload.TocData.AsSpan(0x2c, 4));
					var archive = sourceMatch.Archives.OrderBy(item => item.CategoryOrder).ThenBy(item => item.ArchiveOrder).First();
					var currentTarget = await targetReader.ReadAsync(archive.ArchiveId, sourceKey, allowGlobalDependencySearch: true, cancellationToken: cancellationToken).ConfigureAwait(false);
					if (sourceVersion == currentTarget.Model.Version)
					{
						Log($"[DIRECT-PREFLIGHT] Source=0x{sourceKey.FileId:x16} Version={sourceVersion} Action=PreserveSource");
						continue;
					}

					var sourceUnit = await ReadSourceUnitAsync(sourceByKey[sourceKey], sourceEntries, sourceReadRequest, cancellationToken).ConfigureAwait(false);
					var mappings = BuildSameKeyMappings(sourceKey, sourceUnit.Model, currentTarget.Model);
					if (mappings.Count == 0)
						return Failure(issues, "DirectReuseSourceRepairMappingMissing", $"来源 Unit 0x{sourceKey.FileId:x16} 已过时，但无法建立同 ID Canonical 重构映射。请先对来源 Mod 执行通用 Same-key 重构后再生成替换护甲。");
					sourceRepairAvatar ??= await new CanonicalAvatarRigReader(new AdaptationGameDataPackageResolver(request.GameDataDirectory)).ReadTransformInfoAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
					var repaired = new SameKeyCanonicalUnitRebuilder().Rebuild(new SameKeyCanonicalUnitRebuildRequest(sourceUnit, currentTarget, mappings)
					{
						AvatarTransformInfo = sourceRepairAvatar
					});
					if (!repaired.IsValid || repaired.Job is null)
						return Failure(issues, repaired.Diagnostics.Count == 0
							? [new CanonicalPlanDiagnostic("DirectReuseSourceRepairFailed", $"来源 Unit 0x{sourceKey.FileId:x16} 的同 ID Canonical 重构失败。")]
							: repaired.Diagnostics);
					var repairedEntry = repaired.Job.Outputs.SingleOrDefault(output => output.Key == sourceKey);
					if (repairedEntry is null)
						return Failure(issues, "DirectReuseSourceRepairOutputMissing", $"来源 Unit 0x{sourceKey.FileId:x16} 的同 ID Canonical 重构未产生 Unit 输出。");
					directSourceEntries[sourceKey] = operationWorkspace.Stage(repairedEntry);
					Log($"[DIRECT-PREFLIGHT] Source=0x{sourceKey.FileId:x16} Version={sourceVersion}->{currentTarget.Model.Version} Action=SameKeyRebuilt");
				}
			}
			else if (request.DirectSourceUnitReuse)
			{
				// A stale asset index cannot provide a trustworthy same-key target
				// version. Keep the source payload intact and skip the preflight rather
				// than comparing it against stale GameData metadata.
				Log("[DIRECT-PREFLIGHT] GameDataIndex=stale Action=PreserveSourceWithoutVersionCheck");
			}
			var rebuiltTargets = new Dictionary<AdaptationAssetKey, CanonicalRebuildSummary>();
			var outputUnitCount = 0;
			var replacementCount = 0;
			var minifiedCount = 0;
			CanonicalPatchSessionEntry? sharedHiddenTemplate = null;
			var sharedHiddenMeshCount = 0;
			var sharedHiddenTemplateUnit = default(AdaptationAssetKey);
			// Default mode emits every selected target Unit so unassigned shells are minified.
			// Compact mode emits only real replacement mappings; omitted Units remain untouched
			// in Game Data and are deliberately absent from the output Patch.
			var mappedTargetUnits = request.Plan.Mappings.Select(mapping => new TargetUnitSource(
				new AdaptationAssetKey(mapping.PhysicalTarget.UnitAssetKey.TypeId, mapping.PhysicalTarget.UnitAssetKey.FileId),
				FindTargetArchive(request.Plan, new AdaptationAssetKey(mapping.PhysicalTarget.UnitAssetKey.TypeId, mapping.PhysicalTarget.UnitAssetKey.FileId))));
			var targetUnits = (request.AutoHideUnmappedTargetUnits
				? request.Plan.SelectedTargets.SelectMany(target => target.Parts.Select(part => new TargetUnitSource(
					new AdaptationAssetKey(part.UnitAssetKey.TypeId, part.UnitAssetKey.FileId), target.ArchiveId))).Concat(mappedTargetUnits)
				: mappedTargetUnits.Where(target => request.Plan.Mappings.Any(mapping => mapping.WillReplace && SameKey(mapping.PhysicalTarget.UnitAssetKey, target.Key))))
				.GroupBy(source => source.Key)
				.Select(group => new TargetUnitSource(group.Key, group.Select(source => source.ArchiveName).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))))
				.ToArray();
			ReportProgress(request, "TargetUnitPlan", $"已准备 {targetUnits.Length} 个唯一目标 Unit，开始重建。", 0, Math.Max(targetUnits.Length, 1), totalStopwatch);
			foreach (var (targetUnit, targetIndex) in targetUnits.Select((value, index) => (value, index)))
			{
				cancellationToken.ThrowIfCancellationRequested();
				currentCanonicalUnit = $"0x{targetUnit.Key.FileId:x16}";
				currentCanonicalMesh = "none";
				currentCanonicalSource = "none";
				currentCanonicalPhase = "ReadTargetUnit";
				var unitStopwatch = System.Diagnostics.Stopwatch.StartNew();
				var allocationBefore = GC.GetTotalAllocatedBytes(precise: false);
				var gen0Before = GC.CollectionCount(0);
				var gen1Before = GC.CollectionCount(1);
				var gen2Before = GC.CollectionCount(2);
				var phaseStopwatch = System.Diagnostics.Stopwatch.StartNew();
				ReportProgress(request, "RebuildTargetUnit", $"Canonical：重建 Unit {targetIndex + 1}/{targetUnits.Length} 当前Unit=0x{targetUnit.Key.FileId:x16}", targetIndex, Math.Max(targetUnits.Length, 1), totalStopwatch);
				Log($"[UNIT-BEGIN] Unit=0x{targetUnit.Key.FileId:x16} Index={targetIndex + 1}/{targetUnits.Length}");
				var archiveName = targetUnit.ArchiveName;
				if (archiveName is null)
					return Failure(issues, "CanonicalTargetArchiveMissing", $"目标 Unit 0x{targetUnit.Key.FileId:x16} 没有明确的 Game Data archive。");
				var hasPlannedReplacement = request.Plan.Mappings.Any(mapping => mapping.WillReplace && SameKey(mapping.PhysicalTarget.UnitAssetKey, targetUnit.Key));
				if (!hasPlannedReplacement && request.UseSharedHiddenUnitTemplate && sharedHiddenTemplate is not null)
				{
					currentCanonicalPhase = "ReuseSharedHiddenTemplate";
					var stagingStopwatch = System.Diagnostics.Stopwatch.StartNew();
					var sharedOutputEntry = operationWorkspace.Stage(sharedHiddenTemplate with { Key = targetUnit.Key });
					stagingElapsed += stagingStopwatch.Elapsed;
					outputEntries.Add(sharedOutputEntry);
					workspaceJobs.Add(PatchWorkspaceJobResult.Unit(sharedOutputEntry, $"0x{targetUnit.Key.FileId:x16}"));
					outputUnitCount++;
					minifiedCount += sharedHiddenMeshCount;
					unitTelemetry.Add(CreateUnitJobTelemetryRow(
						targetIndex + 1, targetUnit.Key, usedHiddenCache: false, hasPlannedReplacement,
						meshCount: 0, vertexCount: 0, triangleCount: 0,
						TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero,
						TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, stagingStopwatch.Elapsed,
						unitStopwatch.Elapsed, allocationBefore, gen0Before, gen1Before, gen2Before));
					Log($"[UNIT-SHARED-HIDDEN] Unit=0x{targetUnit.Key.FileId:x16} Template=0x{sharedHiddenTemplateUnit.FileId:x16} HiddenMeshes={sharedHiddenMeshCount}");
					ReportProgress(request, "ReuseSharedHiddenTemplate", $"Canonical：复用统一隐藏 Unit {targetIndex + 1}/{targetUnits.Length} 当前Unit=0x{targetUnit.Key.FileId:x16}", targetIndex + 1, Math.Max(targetUnits.Length, 1), totalStopwatch);
					continue;
				}
				if (!hasPlannedReplacement && request.UseSharedHiddenUnitTemplate)
				{
					try
					{
						currentCanonicalPhase = "BuildSharedHiddenTemplate";
						var hiddenTargetReadStopwatch = System.Diagnostics.Stopwatch.StartNew();
						var hiddenTarget = await targetReader.ReadAsync(archiveName, targetUnit.Key, allowGlobalDependencySearch: false, cancellationToken: cancellationToken).ConfigureAwait(false);
						targetReadElapsed += hiddenTargetReadStopwatch.Elapsed;
						// Sharing a template is a storage optimization only. It must not change
						// culling behavior relative to the ordinary per-target hide path.
						var hidden = new CanonicalHiddenUnitBuilder().Build(hiddenTarget, canonicalAvatarTransforms);
						sharedHiddenTemplate = operationWorkspace.Stage(hidden.Entry);
						sharedHiddenMeshCount = hidden.HiddenMeshCount;
						sharedHiddenTemplateUnit = targetUnit.Key;
						outputEntries.Add(sharedHiddenTemplate);
						workspaceJobs.Add(PatchWorkspaceJobResult.Unit(sharedHiddenTemplate, $"0x{targetUnit.Key.FileId:x16}"));
						outputUnitCount++;
						minifiedCount += hidden.HiddenMeshCount;
						unitTelemetry.Add(CreateUnitJobTelemetryRow(
							targetIndex + 1, targetUnit.Key, usedHiddenCache: false, hasPlannedReplacement,
							meshCount: 0, vertexCount: 0, triangleCount: 0,
							hiddenTargetReadStopwatch.Elapsed, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero,
							TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero,
							unitStopwatch.Elapsed, allocationBefore, gen0Before, gen1Before, gen2Before));
						Log($"[UNIT-SHARED-HIDDEN-TEMPLATE] Unit=0x{targetUnit.Key.FileId:x16} HiddenMeshes={hidden.HiddenMeshCount} Culling=Preserved");
						ReportProgress(request, "BuildSharedHiddenTemplate", $"Canonical：生成统一隐藏 Unit {targetIndex + 1}/{targetUnits.Length} 当前Unit=0x{targetUnit.Key.FileId:x16}", targetIndex + 1, Math.Max(targetUnits.Length, 1), totalStopwatch);
						continue;
					}
					catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException)
					{
						Log($"[UNIT-SHARED-HIDDEN-FALLBACK] Unit=0x{targetUnit.Key.FileId:x16} Reason={exception.Message}");
					}
				}
				if (!hasPlannedReplacement)
				{
					var cached = await hiddenUnitCache.TryReadAsync(archiveName, targetUnit.Key, cancellationToken).ConfigureAwait(false);
					if (cached is not null)
					{
						currentCanonicalPhase = "UseHiddenUnitCache";
						var stagingStopwatch = System.Diagnostics.Stopwatch.StartNew();
						var cachedOutputEntry = operationWorkspace.Stage(cached.Entry);
						stagingElapsed += stagingStopwatch.Elapsed;
						outputEntries.Add(cachedOutputEntry);
						workspaceJobs.Add(PatchWorkspaceJobResult.Unit(cachedOutputEntry, $"0x{targetUnit.Key.FileId:x16}"));
						outputUnitCount++;
						minifiedCount += cached.HiddenMeshCount;
						unitTelemetry.Add(CreateUnitJobTelemetryRow(
							targetIndex + 1, targetUnit.Key, usedHiddenCache: true, hasPlannedReplacement,
							meshCount: 0, vertexCount: 0, triangleCount: 0,
							TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero,
							TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, stagingStopwatch.Elapsed,
							unitStopwatch.Elapsed, allocationBefore, gen0Before, gen1Before, gen2Before));
						Log($"[UNIT-CACHE-HIT] Unit=0x{targetUnit.Key.FileId:x16} HiddenMeshes={cached.HiddenMeshCount}");
						ReportProgress(request, "RebuildTargetUnit", $"Canonical：复用隐藏 Unit 缓存 {targetIndex + 1}/{targetUnits.Length} 当前Unit=0x{targetUnit.Key.FileId:x16}", targetIndex + 1, Math.Max(targetUnits.Length, 1), totalStopwatch);
						continue;
					}
				}
				if (hasPlannedReplacement && request.DirectSourceUnitReuse)
				{
					currentCanonicalPhase = "DirectSourceUnitReuse";
					var directMappings = request.Plan.Mappings
						.Where(mapping => mapping.WillReplace && SameKey(mapping.PhysicalTarget.UnitAssetKey, targetUnit.Key))
						.ToArray();
					var directSourceKeys = directMappings
						.Select(mapping => new AdaptationAssetKey(mapping.Source!.UnitAssetKey.TypeId, mapping.Source.UnitAssetKey.FileId))
						.Distinct()
						.ToArray();
					if (directSourceKeys.Length != 1)
						return Failure(issues, "DirectReuseMultipleSourceUnits", $"快速复用要求目标 Unit 0x{targetUnit.Key.FileId:x16} 的所有命中 Mesh 来自同一个来源 Unit；当前为 {directSourceKeys.Length} 个来源。请调整来源/目标映射后重试；通用 Canonical 重构不再作为替换护甲生产入口。");

					var directSourceKey = directSourceKeys[0];
					var directSourceEntry = sourceByKey[directSourceKey];
					var directSourcePayload = directSourcePayloads[directSourceKey];
					var auxiliaryDependencies = ResolvePatchLocalAuxiliaryDependencies(directSourcePayload.TocData, sourceByKey);
					foreach (var dependency in auxiliaryDependencies)
					{
						if (!directAuxiliaryKeys.Add(dependency.Key)) continue;
						var copied = CreateDirectSourceReuseEntry(dependency.Key, dependency.Entry) with
						{
							Ownership = CanonicalPatchEntryOwnership.RequiredDependency
						};
						outputEntries.Add(copied);
						workspaceJobs.Add(new PatchWorkspaceJobResult([copied], Array.Empty<CanonicalPlanDiagnostic>(), "DirectReuseAuxiliary", $"0x{dependency.Key.FileId:x16}"));
					}

					var directEntry = directSourceEntries[directSourceKey] with { Key = targetUnit.Key, Ownership = CanonicalPatchEntryOwnership.TargetOutput };
					outputEntries.Add(directEntry);
					workspaceJobs.Add(PatchWorkspaceJobResult.Unit(directEntry, $"0x{targetUnit.Key.FileId:x16}"));
					replacementCount += directMappings.Length;
					outputUnitCount++;
					unitTelemetry.Add(CreateUnitJobTelemetryRow(
						targetIndex + 1, targetUnit.Key, usedHiddenCache: false, hasPlannedReplacement,
						meshCount: 0, vertexCount: 0, triangleCount: 0,
						TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero,
						TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero,
						unitStopwatch.Elapsed, allocationBefore, gen0Before, gen1Before, gen2Before));
					Log($"[UNIT-DIRECT-REUSE] Target=0x{targetUnit.Key.FileId:x16} Source=0x{directSourceKeys[0].FileId:x16} MeshMappings={directMappings.Length} GpuBytes={directSourceEntry.GpuResourceSize} Auxiliary={auxiliaryDependencies.Count}");
					ReportProgress(request, "DirectSourceUnitReuse", $"快速复用来源 Unit {targetIndex + 1}/{targetUnits.Length} 当前Unit=0x{targetUnit.Key.FileId:x16}", targetIndex + 1, Math.Max(targetUnits.Length, 1), totalStopwatch);
					continue;
				}
				var targetReadStopwatch = System.Diagnostics.Stopwatch.StartNew();
				var target = await targetReader.ReadAsync(
					archiveName,
					targetUnit.Key,
					allowGlobalDependencySearch: false,
					cancellationToken: cancellationToken).ConfigureAwait(false);
				var targetReadForUnit = targetReadStopwatch.Elapsed;
				targetReadElapsed += targetReadForUnit;
				phaseStopwatch.Restart();
				var approvedUnitMappings = request.Plan.Mappings
					.Where(mapping => mapping.WillReplace && SameKey(mapping.PhysicalTarget.UnitAssetKey, targetUnit.Key))
					.Select(mapping =>
					{
						var sourceKey = new AdaptationAssetKey(mapping.Source!.UnitAssetKey.TypeId, mapping.Source.UnitAssetKey.FileId);
						var sourceModel = sourceUnits[sourceKey].Model;
						var sourceMeshInfoIndex = ResolvePlannedMeshInfoIndex(
							sourceModel,
							mapping.Source.MeshInfoIndex,
							mapping.Source.MeshId,
							$"Source Unit 0x{sourceKey.FileId:x16}");
						var targetMeshInfoIndex = ResolvePlannedMeshInfoIndex(
							target.Model,
							mapping.Target.MeshInfoIndex,
							mapping.Target.MeshId,
							$"Target Unit 0x{targetUnit.Key.FileId:x16}");
						return new CanonicalReplacementMapping(
							new(sourceKey, sourceMeshInfoIndex),
							new(targetUnit.Key, targetMeshInfoIndex),
							SkinningMode: CanonicalSkinningMode.BindStaticToTargetMeshTransform,
							BoneAnchor: CanonicalBoneAnchor.TargetMeshTransform);
					})
					.ToArray();
				var canonicalMappings = CanonicalAutoLodMappingExpander.Expand(
					target.Model,
					sourceUnits.ToDictionary(pair => pair.Key, pair => pair.Value.Model),
					approvedUnitMappings);
				var mappingForUnit = phaseStopwatch.Elapsed;
				mappingElapsed += mappingForUnit;
				phaseStopwatch.Restart();
				var rebuildStopwatch = System.Diagnostics.Stopwatch.StartNew();
				currentCanonicalPhase = "ExpandAutoLodMappings";
				var transformSources = canonicalMappings.Select(mapping =>
				{
					var source = sourceUnits[mapping.Source.UnitKey];
					var raw = source.Model.RawMeshData.SingleOrDefault(item => item.MeshInfoIndex == mapping.Source.MeshInfoIndex)
						?? throw new InvalidDataException("Source RawMesh payload is incomplete for Canonical TransformInfo expansion.");
					return (source.Model, raw);
				});
				target = target with { Model = transformInfoExpander.Expand(target.Model, transformSources, canonicalAvatarTransforms, includeAvatarSkeleton: true) };
				var transformExpansionElapsed = phaseStopwatch.Elapsed;
				phaseStopwatch.Restart();
				var mappingsByIndex = canonicalMappings.ToDictionary(mapping => mapping.Target.MeshInfoIndex);
				var finalRawMeshes = new List<UnitRawMeshData>(target.Model.Meshes.Count);
				var provisionalSkinnedMeshes = new List<CanonicalLodBoneInput>();
				var sourceMaterialSections = new List<CanonicalMaterialSectionProvenance>();
				var hiddenMeshCountForUnit = 0;
				var routeElapsed = TimeSpan.Zero;
				var mergeElapsed = TimeSpan.Zero;
				var minifyElapsed = TimeSpan.Zero;
				var materialResolutionElapsed = TimeSpan.Zero;
				var rebuiltBoneInfos = target.Model.BoneInfos
					.Select((boneInfo, index) => new { Index = index, BoneInfo = boneInfo })
					.ToDictionary(item => item.Index, item => item.BoneInfo);
				foreach (var targetMesh in target.Model.Meshes)
				{
					currentCanonicalMesh = $"MeshInfo={targetMesh.Index},Lod={targetMesh.LodIndex},Stream={targetMesh.StreamIndex}";
					currentCanonicalPhase = "PrepareFinalRawMesh";
					var targetRaw = target.Model.RawMeshData.SingleOrDefault(raw => raw.MeshInfoIndex == targetMesh.Index);
					var stream = target.Model.Streams.SingleOrDefault(candidate => candidate.Index == (int)targetMesh.StreamIndex);
					if (targetRaw is null || stream is null)
						return Failure(issues, [new CanonicalPlanDiagnostic("RawMeshUnavailable", $"目标 RawMesh/stream payload 不完整，无法重建 MeshInfo {targetMesh.Index}。")]);

					UnitRawMeshData finalRaw;
					UnitBoneInfo? provisionalBoneInfo = null;
					var participatesInLodPalette = false;
					if (!mappingsByIndex.TryGetValue(targetMesh.Index, out var mapping))
					{
						if (targetRaw.LodIndex == -1)
						{
							// No compatible source culling mesh was supplied. Preserve the
							// target cutout instead of converting visible source LOD0 into it.
							finalRaw = targetRaw;
						}
						else
						{
							var detailStopwatch = System.Diagnostics.Stopwatch.StartNew();
							var tiny = hiddenUnitGenerator.Generate(targetRaw, stream);
							minifyElapsed += detailStopwatch.Elapsed;
							if (!tiny.IsValid) return Failure(issues, tiny.Diagnostics);
							finalRaw = tiny.Mesh!;
							minifiedCount++;
							hiddenMeshCountForUnit++;
						}
					}
					else
					{
						var source = sourceUnits[mapping.Source.UnitKey];
						currentCanonicalSource = $"0x{mapping.Source.UnitKey.FileId:x16}/MeshInfo={mapping.Source.MeshInfoIndex}";
						currentCanonicalPhase = "ResolveTransform";
						var sourceRaw = source.Model.RawMeshData.SingleOrDefault(raw => raw.MeshInfoIndex == mapping.Source.MeshInfoIndex);
						if (sourceRaw is null)
							return Failure(issues, [new CanonicalPlanDiagnostic("RawMeshUnavailable", $"Source RawMesh payload 不完整，无法重建 MeshInfo {targetMesh.Index}。")]);
						var sourceRawForMaterials = sourceRaw;
						var detailStopwatch = System.Diagnostics.Stopwatch.StartNew();
						var transform = transformResolver.TryResolve(source.Model, sourceRaw.MeshInfoIndex, target.Model, targetRaw.MeshInfoIndex);
						if (!transform.IsValid) return Failure(issues, transform.Diagnostics);
						currentCanonicalPhase = "RouteMeshSkinning";
						var skinningRoute = skinningRouter.TryPrepare(
							source.Model,
							sourceRaw,
							target.Model,
							targetRaw,
							stream,
							mapping.SkinningMode,
							mapping.BoneAnchor);
						routeElapsed += detailStopwatch.Elapsed;
						if (!skinningRoute.IsValid) return Failure(issues, skinningRoute.Diagnostics);
						sourceRaw = skinningRoute.Mesh!;
						provisionalBoneInfo = skinningRoute.ProvisionalBoneInfo;
						participatesInLodPalette = skinningRoute.ParticipatesInLodPalette;
						currentCanonicalPhase = "MergeFinalMeshSections";
						detailStopwatch.Restart();
						var merged = merger.TryMerge(new(mapping.Source, mapping.Target, transform.SourceToTargetLocal!.Value), targetRaw, sourceRaw);
						mergeElapsed += detailStopwatch.Elapsed;
						if (!merged.IsValid) return Failure(issues, merged.Diagnostics);
						var finalMergedMesh = merged.Mesh!;
						// LOD -1/culling meshes can use proxy-only section slots that are not
						// present in the Unit root Material table. They still need the standard
						// geometry/bone/stream rebuild, but their material identity must remain
						// target-owned rather than blocking the replacement.
						detailStopwatch.Restart();
						var materialResolution = targetRaw.LodIndex == -1
							? new CanonicalMaterialBindingResolution([], [])
							: CanonicalMaterialBindingResolver.Resolve(source.Model, sourceRawForMaterials, targetRaw);
						if (!materialResolution.IsValid)
							return Failure(issues, materialResolution.Diagnostics);
						if (targetRaw.LodIndex == -1)
							finalMergedMesh = ApplyTargetCullingMaterialSlots(finalMergedMesh, targetRaw);
						materialResolutionElapsed += detailStopwatch.Elapsed;
						sourceMaterialSections.AddRange(materialResolution.ResolvedSectionBindings.Select(binding => new CanonicalMaterialSectionProvenance(
							targetMesh.Index,
							binding.FinalSectionIndex,
							mapping.Source.UnitKey.FileId,
							binding.SourceSlotId,
							binding.PreferredTargetSlotId,
							binding.MaterialId,
							binding.UsesTargetUnitMaterialSlotLookup)));
						finalRaw = finalMergedMesh;
						replacementCount++;
					}
					finalRawMeshes.Add(finalRaw);
					if (provisionalBoneInfo is not null && participatesInLodPalette)
						provisionalSkinnedMeshes.Add(new CanonicalLodBoneInput(finalRaw, provisionalBoneInfo));
				}
				var meshAssemblyElapsed = phaseStopwatch.Elapsed;
				phaseStopwatch.Restart();
				currentCanonicalPhase = "CompileUnitMaterialLayout";
				var compiledMaterialLayout = new CanonicalUnitMaterialLayoutCompiler().TryCompile(
					target.Model,
					finalRawMeshes,
					sourceMaterialSections);
				if (!compiledMaterialLayout.IsValid) return Failure(issues, compiledMaterialLayout.Diagnostics);
				finalRawMeshes = compiledMaterialLayout.Meshes.ToList();

				// The material compiler establishes the final slot IDs and local material
				// ordinals. Palette compilation rewrites its input meshes, so refresh its
				// inputs now; retaining the pre-compiler meshes would silently restore the
				// source slots for every skinned LOD.
				var finalMeshesByIndex = finalRawMeshes.ToDictionary(mesh => mesh.MeshInfoIndex);
				provisionalSkinnedMeshes = provisionalSkinnedMeshes
					.Select(input => new CanonicalLodBoneInput(finalMeshesByIndex[input.Mesh.MeshInfoIndex], input.ProvisionalBoneInfo))
					.ToList();

				// SDK GetMeshData completes all final meshes before BoneInfo.SetRemap. Compile the
				// shared target LOD palette only after every replacement has reached final topology.
				foreach (var lodGroup in provisionalSkinnedMeshes.GroupBy(mesh => mesh.Mesh.LodIndex))
				{
					currentCanonicalPhase = $"CompileBonePalette:Lod={lodGroup.Key}";
					var compiled = lodBonePaletteCompiler.TryCompile(target.Model, lodGroup.Key, lodGroup.ToArray());
					if (!compiled.IsValid) return Failure(issues, compiled.Diagnostics);
					rebuiltBoneInfos[lodGroup.Key] = compiled.BoneInfo!;
					var byMeshIndex = compiled.Meshes.ToDictionary(mesh => mesh.MeshInfoIndex);
					for (var index = 0; index < finalRawMeshes.Count; index++)
						if (byMeshIndex.TryGetValue(finalRawMeshes[index].MeshInfoIndex, out var compiledMesh))
							finalRawMeshes[index] = compiledMesh;
				}
				var bonePaletteElapsed = phaseStopwatch.Elapsed;
				phaseStopwatch.Restart();
				// SetupRawMeshComponents is stream-wide in the community SDK. The Canonical
				// equivalent validates every completed RawMesh first and returns the one ABI
				// contract used for both TryPrepare and StreamInfo serialization.
				currentCanonicalPhase = "CompileStreamContract";
				var compiledStreams = streamContractCompiler.TryCompile(target.Model, finalRawMeshes);
				if (!compiledStreams.IsValid) return Failure(issues, compiledStreams.Diagnostics);
				target = target with { Model = target.Model with { Streams = compiledStreams.Streams } };
				var streamContractElapsed = phaseStopwatch.Elapsed;
				phaseStopwatch.Restart();
				for (var index = 0; index < finalRawMeshes.Count; index++)
				{
					currentCanonicalPhase = $"PrepareStreamEncoding:MeshInfo={finalRawMeshes[index].MeshInfoIndex}";
					var stream = target.Model.Streams.Single(candidate => candidate.Index == (int)finalRawMeshes[index].StreamIndex);
					var prepared = preparation.TryPrepare(finalRawMeshes[index], stream);
					if (!prepared.IsValid) return Failure(issues, prepared.Diagnostics);
					finalRawMeshes[index] = prepared.Mesh!;
				}
				var finalPreparationElapsed = phaseStopwatch.Elapsed;
				phaseStopwatch.Restart();

				target = target with { Model = target.Model with { Materials = compiledMaterialLayout.Bindings } };
				var materialBindingsElapsed = phaseStopwatch.Elapsed;
				phaseStopwatch.Restart();
				currentCanonicalPhase = "SerializeCanonicalUnit";
				var rebuilt = rebuilder.TryRebuild(target.Model, target.Payload.TocData, finalRawMeshes,
					rebuiltBoneInfos.Select(item => new CanonicalBoneInfoRebuild(item.Key, item.Value)).ToArray());
				if (!rebuilt.IsValid) return Failure(issues, rebuilt.Diagnostics);
				// A TargetOutput owns all three sidecars. The first Canonical rebuilder has no StreamData
				// serializer, so an existing target stream cannot be inherited as if it were new output.
				if (target.Payload.StreamData.Length != 0)
					return Failure(issues, "CanonicalTargetStreamPayloadUnsupported", $"目标 Unit 0x{targetUnit.Key.FileId:x16} 带有非空 source stream payload；Canonical 重建尚未生成等价 stream payload，因此拒绝静默沿用旧 stream。");
				var outputTargetEntry = new CanonicalPatchSessionEntry(targetUnit.Key, CanonicalPatchEntryOwnership.TargetOutput,
					rebuilt.Output!.TocData, rebuilt.Output.GpuData, Array.Empty<byte>(), target.Payload.Entry.Unknown1,
					target.Payload.Entry.Unknown2, target.Payload.Entry.Unknown3, target.Payload.Entry.Unknown4);
				if (!hasPlannedReplacement)
					await hiddenUnitCache.StoreAsync(archiveName, new CanonicalHiddenUnitOutput(outputTargetEntry, hiddenMeshCountForUnit), cancellationToken).ConfigureAwait(false);
				var serializationElapsed = phaseStopwatch.Elapsed;
				rebuildElapsed += rebuildStopwatch.Elapsed;
				var unitRebuildTelemetry = new CanonicalUnitRebuildTelemetry(
					transformExpansionElapsed, meshAssemblyElapsed, streamContractElapsed, TimeSpan.Zero,
					bonePaletteElapsed, finalPreparationElapsed, materialBindingsElapsed, serializationElapsed)
				{
					MeshBreakdown = new CanonicalMeshAssemblyTelemetry(routeElapsed, mergeElapsed, minifyElapsed, materialResolutionElapsed),
					SerializationBreakdown = rebuilt.SerializationTelemetry ?? CanonicalUnitSerializationTelemetry.Empty
				};
				rebuildTelemetry.Add(unitRebuildTelemetry);
				phaseStopwatch.Restart();
				outputTargetEntry = operationWorkspace.Stage(outputTargetEntry);
				var stagingForUnit = phaseStopwatch.Elapsed;
				stagingElapsed += stagingForUnit;
				outputEntries.Add(outputTargetEntry);
				workspaceJobs.Add(PatchWorkspaceJobResult.Unit(outputTargetEntry, $"0x{targetUnit.Key.FileId:x16}"));
				rebuiltTargets.Add(targetUnit.Key, CreateRebuildSummary(targetUnit.Key, rebuilt.Model!));
				unitTelemetry.Add(CreateUnitJobTelemetryRow(
					targetIndex + 1, targetUnit.Key, usedHiddenCache: false, hasPlannedReplacement,
					rebuilt.Model!.Meshes.Count,
					checked(rebuilt.Model.RawMeshData.Sum(raw => raw.Vertices.Count)),
					checked(rebuilt.Model.RawMeshData.Sum(raw => raw.Triangles.Count)),
					targetReadForUnit, mappingForUnit, transformExpansionElapsed, meshAssemblyElapsed,
					bonePaletteElapsed, streamContractElapsed, finalPreparationElapsed, materialBindingsElapsed,
					serializationElapsed, stagingForUnit,
					unitStopwatch.Elapsed, allocationBefore, gen0Before, gen1Before, gen2Before,
					unitRebuildTelemetry.MeshBreakdown, unitRebuildTelemetry.SerializationBreakdown));
				outputUnitCount++;
				Log($"[UNIT-DONE] Unit=0x{targetUnit.Key.FileId:x16} Meshes={rebuilt.Model!.Meshes.Count} Materials={rebuilt.Model.Materials.Count} Replacements={canonicalMappings.Count}");
				ReportProgress(request, "RebuildTargetUnit", $"Canonical：重建 Unit {targetIndex + 1}/{targetUnits.Length} 当前Unit=0x{targetUnit.Key.FileId:x16} 用时={unitStopwatch.Elapsed:hh\\:mm\\:ss}", targetIndex + 1, Math.Max(targetUnits.Length, 1), totalStopwatch);
			}
			if (unitTelemetryPath is not null)
			{
				await CanonicalUnitJobTelemetry.WriteCsvAsync(unitTelemetryPath, unitTelemetry, cancellationToken).ConfigureAwait(false);
				Log($"[TELEMETRY] File={Path.GetFileName(unitTelemetryPath)} Rows={unitTelemetry.Count}");
			}
			await artifacts!.WriteMappingsAsync(request.Plan.Mappings.Select(mapping => new CanonicalMappingDiagnosticRow(
				mapping.Target.PartKind.ToString(), mapping.WillReplace ? "命中" : "隐藏",
				mapping.Source is null ? string.Empty : $"0x{mapping.Source.UnitAssetKey.FileId:x16}",
				$"0x{mapping.PhysicalTarget.UnitAssetKey.FileId:x16}",
				mapping.Source?.StoredSizeText ?? string.Empty, mapping.Target.StoredSizeText,
				mapping.Target.BodyVariant.ToString(), mapping.Source?.BodyVariant.ToString() ?? string.Empty,
				string.Join(';', mapping.UsedByArchiveIds), mapping.IsManual ? "手动" : "自动", mapping.Reason)).ToArray(), cancellationToken).ConfigureAwait(false);
			ReportProgress(request, "CanonicalUnitJobMetrics", $"Canonical Unit job metrics: Flow=CrossArmor, SourceRead={sourceReadElapsed.TotalMilliseconds:F0}ms, TargetRead={targetReadElapsed.TotalMilliseconds:F0}ms, Mapping={mappingElapsed.TotalMilliseconds:F0}ms, Rebuild={rebuildElapsed.TotalMilliseconds:F0}ms, Staging={stagingElapsed.TotalMilliseconds:F0}ms, {rebuildTelemetry.Snapshot().Describe()}", targetUnits.Length, Math.Max(targetUnits.Length, 1), totalStopwatch);

			// Unit payloads have already been staged to disk. Do not keep the full source
			// mesh graph alive while carrying through source-owned material entries.
			targetReader.ClearCaches();
			sourceUnits.Clear();
			GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);
			GC.WaitForPendingFinalizers();
			GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);

			await CarryThroughSourceMaterialsAsync(outputEntries, workspaceJobs, sourceEntries, operationWorkspace, request, sourceReadRequest, cancellationToken, totalStopwatch).ConfigureAwait(false);
			Log($"[MATERIAL-CARRY] Entries={outputEntries.Count(entry => entry.Ownership == CanonicalPatchEntryOwnership.RequiredDependency)}");

			var session = new CanonicalPatchSession();
			ReportProgress(request, "ValidateUnitReferences", "正在验证 Unit 材质引用表。", targetUnits.Length, Math.Max(targetUnits.Length, 1), totalStopwatch);
			var finalized = patchWorkspaceSessionComposer.ComposeJobs(session, workspaceJobs, Array.Empty<CanonicalPatchSessionEntry>(), CanonicalDependencyClosureValidation.Valid);
			if (!finalized.IsValid) return Failure(issues, finalized.Diagnostics);
			var headerArchive = request.Plan.SelectedTargets.FirstOrDefault()?.ArchiveId;
			var header = headerArchive is null ? null : await new AdaptationGameDataPackageResolver(request.GameDataDirectory).GetPackageTocAsync(headerArchive, cancellationToken).ConfigureAwait(false);
			ReportProgress(request, "WritePatch", "正在写入 Canonical Patch 和 GPU sidecar。", targetUnits.Length, Math.Max(targetUnits.Length, 1), totalStopwatch);
			var removedSourceKeys = sourceWorkspaceIndex.Entries.Select(entry => entry.AssetKey).ToHashSet();
			var written = await patchWorkspaceWriter.WriteAsync(
				sourceWorkspaceIndex,
				workspaceJobs,
				removedSourceKeys,
				request.OutputDirectory,
				ResolveOutputPatchFileName(request.SourcePatchTocPath),
				header?.Data,
				overwriteExisting: false,
				cancellationToken: cancellationToken).ConfigureAwait(false);
			var outputReadRequest = sourceReadRequest with
			{
				SourcePath = written.TocFilePath,
				ContentView = ModInformationContentView.Effective
			};
			var fileDiagnostics = await ValidateWrittenFilesAsync(written, outputEntries, outputReadRequest, cancellationToken).ConfigureAwait(false);
			issues.AddRange(fileDiagnostics.Select(diagnostic => new CoreIssue(CoreIssueSeverity.Error, diagnostic.Code, diagnostic.Message)));
			reportState.Status = fileDiagnostics.Count == 0 ? "WrittenForGameTest" : "Failed";
			Log($"[WRITE-DONE] Patch={Path.GetFileName(written.TocFilePath)} Units={outputUnitCount} FileDiagnostics={fileDiagnostics.Count}");
			await WriteMarkdownReportAsync(reportPath, request, reportState, replacementPlanMappings, outputEntries, rebuiltTargets, fileDiagnostics, outputUnitCount, replacementCount, cancellationToken).ConfigureAwait(false);
			await artifacts.WriteReportAsync(reportState.Status, $"Unit={outputUnitCount}; 替换Mesh={replacementCount}; 极小化Mesh={minifiedCount}; 总耗时={totalStopwatch.Elapsed}", fileDiagnostics.Select(item => item.Message).ToArray(), cancellationToken).ConfigureAwait(false);
			ReportProgress(request, "CanonicalCompleted", $"替换成功，文件位置：{written.OutputDirectoryPath}", targetUnits.Length, Math.Max(targetUnits.Length, 1), totalStopwatch);
			if (fileDiagnostics.Count != 0)
				return new CrossArmorTransferCandidateResult(false, written.OutputDirectoryPath, reportPath, outputUnitCount, replacementCount, minifiedCount, issues);
			return new CrossArmorTransferCandidateResult(true, written.OutputDirectoryPath, reportPath, outputUnitCount, replacementCount, minifiedCount, issues) { IsCommitted = true };
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or KeyNotFoundException or ArgumentException or OverflowException)
		{
			var context = $"Unit={currentCanonicalUnit}, Mesh={currentCanonicalMesh}, Source={currentCanonicalSource}, Phase={currentCanonicalPhase}";
			issues.Add(new(CoreIssueSeverity.Error, "CanonicalExecutionFailed", $"{context}; {exception.Message}", ExceptionMessage: exception.ToString()));
			reportState.Status = "Failed";
			Log($"[ERROR] {context}; {exception}");
			if (reportPath is not null)
				try { await WriteMarkdownReportAsync(reportPath, request, reportState, [], [], new Dictionary<AdaptationAssetKey, CanonicalRebuildSummary>(), [new CanonicalPlanDiagnostic("CanonicalExecutionFailed", exception.Message)], null, null, cancellationToken).ConfigureAwait(false); } catch (IOException) { }
			if (artifacts is not null)
				try { await artifacts.WriteReportAsync("Failed", context, [exception.Message], CancellationToken.None).ConfigureAwait(false); } catch (IOException) { }
			return new CrossArmorTransferCandidateResult(false, Directory.Exists(request.OutputDirectory) ? request.OutputDirectory : null, reportPath, 0, 0, 0, issues);
		}
		finally
		{
			artifacts?.Dispose();
		}
	}

	private async ValueTask CarryThroughSourceMaterialsAsync(
		ICollection<CanonicalPatchSessionEntry> outputEntries,
		ICollection<PatchWorkspaceJobResult> workspaceJobs,
		IReadOnlyList<AdaptationPatchTocEntry> sourceEntries,
		IPatchOperationWorkspace operationWorkspace,
		CrossArmorTransferCandidateRequest request,
		ModInformationReadRequest sourceReadRequest,
		CancellationToken cancellationToken,
		System.Diagnostics.Stopwatch totalStopwatch)
	{
		var sourceByKey = sourceEntries.ToDictionary(entry => entry.AssetKey);
		var materialIds = outputEntries
			.Where(entry => entry.Ownership == CanonicalPatchEntryOwnership.TargetOutput)
			.SelectMany(entry => new UnitMaterialReferenceReader().ReadReferenceBindings(entry.EffectiveTocData))
			.Select(binding => binding.MaterialId)
			.Where(materialId => materialId != 0)
			.Distinct()
			.OrderBy(materialId => materialId)
			.ToArray();
		var carriedKeys = outputEntries.Select(entry => entry.Key).ToHashSet();
		var carriedTextureIds = new HashSet<ulong>();
		ReportProgress(request, "CarryThroughMaterials", $"Canonical：顺延来源材质 0/{materialIds.Length}", 0, Math.Max(materialIds.Length, 1), totalStopwatch);

		for (var index = 0; index < materialIds.Length; index++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var materialKey = new AdaptationAssetKey(MaterialDependencyResolver.MaterialTypeId, materialIds[index]);
			if (!sourceByKey.TryGetValue(materialKey, out var materialEntry))
			{
				ReportProgress(request, "CarryThroughMaterials", $"Canonical：来源未包含 Material 0x{materialIds[index]:x16}，保留外部引用", index + 1, Math.Max(materialIds.Length, 1), totalStopwatch);
				continue;
			}

			HD2ModAdaptation.PatchReconstruction.PatchEntryPayload materialPayload;
			try
			{
				materialPayload = await ReadSourcePayloadAsync(materialEntry, sourceReadRequest, cancellationToken).ConfigureAwait(false);
			}
			catch (Exception exception) when (exception is IOException or InvalidDataException or EndOfStreamException or UnauthorizedAccessException)
			{
				ReportProgress(request, "CarryThroughMaterials", $"Canonical：Material 0x{materialIds[index]:x16} 读取失败，保留外部引用：{exception.Message}", index + 1, Math.Max(materialIds.Length, 1), totalStopwatch);
				continue;
			}

			if (carriedKeys.Add(materialKey))
			{
				var materialOutput = new CanonicalPatchSessionEntry(materialKey, CanonicalPatchEntryOwnership.RequiredDependency,
					materialPayload.TocData, materialPayload.GpuResourceData, materialPayload.StreamData, materialEntry.Unknown1, materialEntry.Unknown2, materialEntry.Unknown3, materialEntry.Unknown4);
				materialOutput = operationWorkspace.Stage(materialOutput);
				outputEntries.Add(materialOutput);
				workspaceJobs.Add(new PatchWorkspaceJobResult([materialOutput], Array.Empty<CanonicalPlanDiagnostic>(), "Material", $"0x{materialKey.FileId:x16}"));
			}

			IReadOnlyList<ulong> textureIds;
			try
			{
				textureIds = materialReferenceReader.ReadTextureIds(materialPayload.TocData);
			}
			catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException or OverflowException)
			{
				ReportProgress(request, "CarryThroughMaterials", $"Canonical：Material 0x{materialIds[index]:x16} 的 Texture 表无法解析，仅顺延 Material", index + 1, Math.Max(materialIds.Length, 1), totalStopwatch);
				continue;
			}

			foreach (var textureId in textureIds.Where(textureId => textureId != 0).Distinct())
			{
				if (!carriedTextureIds.Add(textureId))
					continue;
				var textureKey = new AdaptationAssetKey(MaterialDependencyResolver.TextureTypeId, textureId);
				if (!sourceByKey.TryGetValue(textureKey, out var textureEntry) || !carriedKeys.Add(textureKey))
					continue;
				HD2ModAdaptation.PatchReconstruction.PatchEntryPayload texturePayload;
				try
				{
					texturePayload = await ReadSourcePayloadAsync(textureEntry, sourceReadRequest, cancellationToken).ConfigureAwait(false);
				}
				catch (Exception exception) when (exception is IOException or InvalidDataException or EndOfStreamException or UnauthorizedAccessException)
				{
					carriedKeys.Remove(textureKey);
					continue;
				}
				var textureOutput = new CanonicalPatchSessionEntry(textureKey, CanonicalPatchEntryOwnership.RequiredDependency,
					texturePayload.TocData, texturePayload.GpuResourceData, texturePayload.StreamData, textureEntry.Unknown1, textureEntry.Unknown2, textureEntry.Unknown3, textureEntry.Unknown4);
				textureOutput = operationWorkspace.Stage(textureOutput);
				outputEntries.Add(textureOutput);
				workspaceJobs.Add(new PatchWorkspaceJobResult([textureOutput], Array.Empty<CanonicalPlanDiagnostic>(), "Texture", $"0x{textureKey.FileId:x16}"));
			}

			ReportProgress(request, "CarryThroughMaterials", $"Canonical：来源 Material 0x{materialIds[index]:x16} 顺延完成", index + 1, Math.Max(materialIds.Length, 1), totalStopwatch);
		}
	}

	private SourceReadScope BeginSourceReadScope(CrossArmorTransferCandidateRequest request)
	{
		var operationId = Guid.NewGuid();
		var context = ModInformationRequestContext.Create(
			ModInformationCacheScope.Operation,
			operationId: operationId,
			sessionId: operationId,
			operationName: "CrossArmorTransfer");
		return new SourceReadScope(
			informationReader,
			operationId,
			new ModInformationReadRequest(
				request.SourcePatchTocPath,
				context,
				ContentView: ModInformationContentView.Source));
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

	private async ValueTask<AdaptationPatchEntryPayload> ReadSourcePayloadAsync(
		AdaptationPatchTocEntry entry,
		ModInformationReadRequest request,
		CancellationToken cancellationToken)
	{
		// Payloads are either retained explicitly by direct-reuse preflight or staged
		// immediately to the operation workspace. Avoid duplicating large GPU/texture
		// buffers in the reader cache beyond their useful lifetime.
		var payloadRequest = request with { Context = request.EffectiveContext with { CacheScope = ModInformationCacheScope.None } };
		var result = await informationReader.ReadPatchPayloadAsync(entry, payloadRequest, cancellationToken).ConfigureAwait(false);
		if (result.Data is not null)
			return new AdaptationPatchEntryPayload(entry, result.Data.TocData, result.Data.StreamData, result.Data.GpuResourceData);
		var message = result.State.Diagnostics.Count == 0
			? $"无法读取来源资源 0x{entry.AssetKey.FileId:x16}。"
			: string.Join("; ", result.State.Diagnostics.Select(issue => issue.Message));
		throw new InvalidDataException(message);
	}

	private async ValueTask<PatchUnitMesh> ReadSourceUnitAsync(
		AdaptationPatchTocEntry entry,
		IReadOnlyList<AdaptationPatchTocEntry> entries,
		ModInformationReadRequest request,
		CancellationToken cancellationToken)
	{
		// CrossArmor owns decoded source Units in sourceUnits and releases that graph
		// before material carry-through. A second operation-cache ownership would keep
		// the same large vertex data alive and defeat that release boundary.
		var unitRequest = request with { Context = request.EffectiveContext with { CacheScope = ModInformationCacheScope.None } };
		var result = await informationReader.ReadUnitAsync(
			entry,
			entries,
			PatchUnitDependencyPolicy.RequirePatchLocalComposite,
			unitRequest,
			canonicalSource: false,
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


	private static string? FindTargetArchive(CrossArmorTransferPlan plan, AdaptationAssetKey key)
		=> plan.SelectedTargets.FirstOrDefault(target => target.Parts.Any(part => SameKey(part.UnitAssetKey, key)))?.ArchiveId
			?? plan.SelectedTargets.SelectMany(target => target.Parts).FirstOrDefault(part => SameKey(part.UnitAssetKey, key))?.SharedArchiveIds.FirstOrDefault();

	private static CanonicalPatchSessionEntry CreateDirectSourceReuseEntry(AdaptationAssetKey targetKey, AdaptationPatchTocEntry sourceEntry)
	{
		var sourceTocPath = sourceEntry.SourceFilePath;
		var sourceGpuPath = sourceTocPath + ".gpu_resources";
		var sourceStreamPath = sourceTocPath + ".stream";
		return new CanonicalPatchSessionEntry(
			targetKey,
			CanonicalPatchEntryOwnership.TargetOutput,
			sourceEntry.TocDataSize == 0 ? Array.Empty<byte>() : null,
			sourceEntry.GpuResourceSize == 0 ? Array.Empty<byte>() : null,
			sourceEntry.StreamSize == 0 ? Array.Empty<byte>() : null,
			sourceEntry.Unknown1,
			sourceEntry.Unknown2,
			sourceEntry.Unknown3,
			sourceEntry.Unknown4)
		{
			TocDataSource = sourceEntry.TocDataSize == 0 ? null : new CanonicalPayloadSourceRange(sourceTocPath, sourceEntry.TocDataOffset, sourceEntry.TocDataSize),
			GpuDataSource = sourceEntry.GpuResourceSize == 0 ? null : new CanonicalPayloadSourceRange(sourceGpuPath, sourceEntry.GpuResourceOffset, sourceEntry.GpuResourceSize),
			StreamDataSource = sourceEntry.StreamSize == 0 ? null : new CanonicalPayloadSourceRange(sourceStreamPath, sourceEntry.StreamOffset, sourceEntry.StreamSize)
		};
	}

	private static IReadOnlyList<(AdaptationAssetKey Key, AdaptationPatchTocEntry Entry)> ResolvePatchLocalAuxiliaryDependencies(
		ReadOnlySpan<byte> unitTocData,
		IReadOnlyDictionary<AdaptationAssetKey, AdaptationPatchTocEntry> sourceByKey)
	{
		if (unitTocData.Length < 24) throw new InvalidDataException("快速复用来源 Unit TOC 过短，无法读取 Composite/Bone 引用。");
		var boneReference = BinaryPrimitives.ReadUInt64LittleEndian(unitTocData.Slice(8, 8));
		var compositeReference = BinaryPrimitives.ReadUInt64LittleEndian(unitTocData.Slice(16, 8));
		var result = new List<(AdaptationAssetKey Key, AdaptationPatchTocEntry Entry)>();
		foreach (var key in new[]
		{
			boneReference == 0 ? default : new AdaptationAssetKey(PatchUnitMeshReader.BoneTypeId, boneReference),
			compositeReference == 0 ? default : new AdaptationAssetKey(PatchUnitMeshReader.CompositeUnitTypeId, compositeReference)
		})
		{
			if (key == default || !sourceByKey.TryGetValue(key, out var entry)) continue;
			result.Add((key, entry));
		}
		return result;
	}

	private static IReadOnlyList<TargetShellMeshMapping> BuildSameKeyMappings(AdaptationAssetKey sourceKey, UnitMeshModel source, UnitMeshModel target)
	{
		var sourceLod0 = UnitGeometryFactsBuilder.SelectBestRenderableLod0(source);
		var targetLod0 = UnitGeometryFactsBuilder.SelectBestRenderableLod0(target);
		if (sourceLod0 is null || targetLod0 is null)
			return source.RawMeshData
				.Where(raw => raw.LodIndex == -1 && UnitGeometryFactsBuilder.HasRenderableGeometry(raw))
				.Select(sourceCulling => (Source: sourceCulling, Target: target.RawMeshData.SingleOrDefault(targetCulling =>
					targetCulling.LodIndex == -1 && targetCulling.MeshId == sourceCulling.MeshId && UnitGeometryFactsBuilder.HasRenderableGeometry(targetCulling))))
				.Where(pair => pair.Target is not null)
				.Select(pair => new TargetShellMeshMapping(sourceKey, pair.Source.MeshInfoIndex, pair.Target!.MeshInfoIndex))
				.ToArray();

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

	private static int ResolvePlannedMeshInfoIndex(
		UnitMeshModel model,
		int plannedIndex,
		uint plannedMeshId,
		string role)
	{
		var indexedMesh = model.Meshes.FirstOrDefault(mesh => mesh.Index == plannedIndex);
		var indexedRaw = model.RawMeshData.FirstOrDefault(mesh => mesh.MeshInfoIndex == plannedIndex);
		if (indexedMesh is not null && indexedRaw is not null && (plannedMeshId == 0 || indexedMesh.MeshId == plannedMeshId))
			return plannedIndex;

		if (indexedMesh is not null && plannedMeshId != 0 && indexedMesh.MeshId != plannedMeshId)
			System.Diagnostics.Trace.WriteLine(
				$"[CanonicalCrossArmorOrchestrator] {role} 的计划 MeshInfoIndex={plannedIndex} 对应 MeshId=0x{indexedMesh.MeshId:x8}，计划 MeshId=0x{plannedMeshId:x8}，将按 MeshId 校正。");

		if (plannedMeshId == 0)
			throw new InvalidDataException(
				$"{role} 的计划 MeshInfoIndex={plannedIndex} 不存在或无法与当前 Unit 结构一致，且计划没有 MeshId 可用于安全重定位。");

		var matches = model.Meshes
			.Where(mesh => mesh.MeshId == plannedMeshId)
			.Where(mesh => model.RawMeshData.Any(raw => raw.MeshInfoIndex == mesh.Index))
			.ToArray();
		if (matches.Length == 1)
			return matches[0].Index;

		if (matches.Length == 0)
			throw new InvalidDataException(
				$"{role} 的计划 MeshInfoIndex={plannedIndex} 无效，MeshId=0x{plannedMeshId:x8} 在当前 Unit 中不存在。");

		throw new InvalidDataException(
			$"{role} 的计划 MeshInfoIndex={plannedIndex} 无效，MeshId=0x{plannedMeshId:x8} 在当前 Unit 中对应多个 MeshInfo，拒绝猜测。");
	}

	private static bool SameKey(CoreAssetKey coreKey, AdaptationAssetKey adaptationKey)
		=> coreKey.TypeId == adaptationKey.TypeId && coreKey.FileId == adaptationKey.FileId;

	private sealed record TargetUnitSource(AdaptationAssetKey Key, string? ArchiveName);

	private static UnitRawMeshData ApplyTargetCullingMaterialSlots(UnitRawMeshData merged, UnitRawMeshData target)
	{
		if (target.Sections.Count == 0 || merged.Sections.Count == 0)
			return merged;

		var sections = merged.Sections.Select((section, index) =>
		{
			var targetSection = target.Sections[Math.Min(index, target.Sections.Count - 1)];
			return section with
			{
				MaterialIndex = targetSection.MaterialIndex,
				MaterialSlotId = targetSection.MaterialSlotId
			};
		}).ToArray();
		return merged with
		{
			Sections = sections,
			Triangles = sections.SelectMany(section => section.Triangles).ToArray()
		};
	}

	private static string ResolveOutputPatchFileName(string sourcePatchTocPath)
	{
		var fileName = Path.GetFileName(sourcePatchTocPath);
		if (!Regex.IsMatch(fileName, "^[0-9a-fA-F]{16}\\.patch_0$", RegexOptions.CultureInvariant))
			throw new InvalidDataException("Canonical 输出要求来源 Patch 文件名为 16 位十六进制 ID 加 .patch_0。");
		return fileName.ToLowerInvariant();
	}

	private async ValueTask<IReadOnlyList<CanonicalPlanDiagnostic>> ValidateWrittenFilesAsync(
		PatchArchiveFileWriteResult written,
		IReadOnlyList<CanonicalPatchSessionEntry> entries,
		ModInformationReadRequest outputReadRequest,
		CancellationToken cancellationToken)
	{
		var diagnostics = new List<CanonicalPlanDiagnostic>();
		if (!File.Exists(written.TocFilePath)) diagnostics.Add(new("CanonicalOutputPatchMissing", "写出后找不到 Patch 文件。"));
		if (written.TocFileSize <= 0) diagnostics.Add(new("CanonicalOutputPatchEmpty", "输出 Patch 文件为空。"));
		if (diagnostics.Count != 0) return diagnostics;
		var outputIndex = await informationReader.ReadPatchIndexAsync(outputReadRequest, cancellationToken).ConfigureAwait(false);
		if (outputIndex.Data is null)
		{
			var message = outputIndex.State.Diagnostics.Count == 0
				? "写出后无法读取输出 Patch TOC。"
				: string.Join("; ", outputIndex.State.Diagnostics.Select(issue => issue.Message));
			diagnostics.Add(new("CanonicalOutputTocUnreadable", message));
			return diagnostics;
		}
		var actual = outputIndex.Data.Entries.ToDictionary(entry => entry.AssetKey);
		foreach (var expected in entries)
		{
			var expectedKey = new AdaptationAssetKey(expected.Key.TypeId, expected.Key.FileId);
			if (!actual.TryGetValue(expectedKey, out var entry))
			{
				diagnostics.Add(new("CanonicalOutputEntryMissing", $"输出 Patch 缺少 Entry 0x{expected.Key.FileId:x16}。"));
				continue;
			}
			ValidateOutputPayloadRange(expectedKey, "TOC", entry.TocDataOffset, entry.TocDataSize, written.TocFileSize, ExpectedPayloadLength(expected.TocData, expected.TocDataPath, expected.TocDataSource), diagnostics);
			ValidateOutputPayloadRange(expectedKey, "GPU", entry.GpuResourceOffset, entry.GpuResourceSize, written.GpuResourceFileSize, ExpectedPayloadLength(expected.GpuData, expected.GpuDataPath, expected.GpuDataSource), diagnostics);
			ValidateOutputPayloadRange(expectedKey, "Stream", entry.StreamOffset, entry.StreamSize, written.StreamFileSize, ExpectedPayloadLength(expected.StreamData, expected.StreamDataPath, expected.StreamDataSource), diagnostics);
		}
		return diagnostics;
	}

	private static int ExpectedPayloadLength(byte[]? data, string? path, CanonicalPayloadSourceRange? sourceRange)
		=> data?.Length ?? (path is not null ? checked((int)new FileInfo(path).Length) : checked((int)(sourceRange?.Length ?? 0)));

	private static void ValidateOutputPayloadRange(AdaptationAssetKey key, string kind, ulong offset, uint size, long fileLength, int expectedSize, ICollection<CanonicalPlanDiagnostic> diagnostics)
	{
		if (size != expectedSize)
			diagnostics.Add(new("CanonicalOutputPayloadSizeMismatch", $"Entry 0x{key.FileId:x16} 的 {kind} 大小为 {size}，预期 {expectedSize}。"));
		if (size != 0 && (offset > (ulong)fileLength || size > (ulong)fileLength - offset))
			diagnostics.Add(new("CanonicalOutputPayloadRangeInvalid", $"Entry 0x{key.FileId:x16} 的 {kind} range 超出输出文件范围。"));
	}

	private static CanonicalUnitJobTelemetryRow CreateUnitJobTelemetryRow(
		int sequence,
		AdaptationAssetKey unitKey,
		bool usedHiddenCache,
		bool hasPlannedReplacement,
		int meshCount,
		int vertexCount,
		int triangleCount,
		TimeSpan targetRead,
		TimeSpan mapping,
		TimeSpan transform,
		TimeSpan meshAssembly,
		TimeSpan bonePalette,
		TimeSpan streamContract,
		TimeSpan finalPreparation,
		TimeSpan materialBindings,
		TimeSpan serialization,
		TimeSpan staging,
		TimeSpan total,
		long allocationBefore,
		int gen0Before,
		int gen1Before,
		int gen2Before,
		CanonicalMeshAssemblyTelemetry? meshBreakdown = null,
		CanonicalUnitSerializationTelemetry? serializationBreakdown = null)
		=> new(
			"CrossArmor", sequence, unitKey.FileId, usedHiddenCache, hasPlannedReplacement, meshCount, vertexCount, triangleCount,
			TimeSpan.Zero, targetRead, mapping, transform, meshAssembly, meshBreakdown ?? CanonicalMeshAssemblyTelemetry.Empty,
			bonePalette, streamContract, finalPreparation, materialBindings, serialization,
			serializationBreakdown ?? CanonicalUnitSerializationTelemetry.Empty, staging, total,
			Math.Max(0, GC.GetTotalAllocatedBytes(precise: false) - allocationBefore),
			GC.GetTotalMemory(forceFullCollection: false),
			Environment.WorkingSet,
			GC.CollectionCount(0) - gen0Before,
			GC.CollectionCount(1) - gen1Before,
			GC.CollectionCount(2) - gen2Before);

	private static async ValueTask WriteMarkdownReportAsync(
		string reportPath,
		CrossArmorTransferCandidateRequest request,
		CanonicalMarkdownReportState state,
		IReadOnlyList<CanonicalReplacementMapping> mappings,
		IReadOnlyList<CanonicalPatchSessionEntry> entries,
		IReadOnlyDictionary<AdaptationAssetKey, CanonicalRebuildSummary> rebuiltTargets,
		IReadOnlyList<CanonicalPlanDiagnostic> diagnostics,
		int? outputUnitCount,
		int? replacementCount,
		CancellationToken cancellationToken)
	{
		var builder = new StringBuilder();
		builder.AppendLine("# Canonical 护甲替换报告").AppendLine();
		builder.AppendLine("## 使用说明").AppendLine();
		builder.AppendLine("- 本输出只负责重建目标 Unit、几何、骨骼、Stream/GPU 和 Unit 材质引用。");
		builder.AppendLine("- 来源 Patch 中已有的 Material/Texture 会尽力顺延；不存在或读取失败时保留外部引用，不阻断 Unit 输出。");
		builder.AppendLine("- 请将输出 Patch 与原本提供材质的 Patch 一起启用，并以游戏实际显示结果作为最终验证。");
		builder.AppendLine("- 如果测试失败，请完整提交本 Markdown 文件。").AppendLine();
		builder.AppendLine("## 输出摘要").AppendLine();
		builder.AppendLine($"- 状态：{state.Status}");
		builder.AppendLine($"- 来源 Patch：{Path.GetFileName(request.SourcePatchTocPath)}");
		builder.AppendLine($"- 目标 Unit：{outputUnitCount?.ToString() ?? rebuiltTargets.Count.ToString()}");
		builder.AppendLine($"- 替换 Mesh：{replacementCount?.ToString() ?? mappings.Count.ToString()}");
		builder.AppendLine($"- 顺延资源：{entries.Count(entry => entry.Ownership == CanonicalPatchEntryOwnership.RequiredDependency)}");
		builder.AppendLine($"- 诊断数量：{diagnostics.Count}").AppendLine();
		builder.AppendLine("## Unit 详情").AppendLine().AppendLine("```csv");
		builder.AppendLine("unit_file_id,mesh_count,stream_count,material_binding_count,raw_mesh_count,bone_info_count,planned_sources,output_status");
		foreach (var target in rebuiltTargets.OrderBy(pair => pair.Key.FileId))
		{
			var sources = mappings.Where(mapping => mapping.Target.UnitKey == target.Key)
				.Select(mapping => $"0x{mapping.Source.UnitKey.FileId:x16}:mesh{mapping.Source.MeshInfoIndex}");
			builder.AppendLine($"0x{target.Key.FileId:x16},{target.Value.MeshCount},{target.Value.StreamCount},{target.Value.MaterialBindingCount},{target.Value.RawMeshCount},{target.Value.BoneInfoCount},\"{string.Join(';', sources)}\",{(diagnostics.Any() ? "CheckDiagnostics" : "WrittenForGameTest")}");
		}
		builder.AppendLine("```").AppendLine();
		builder.AppendLine("## 详细日志").AppendLine().AppendLine("```log");
		foreach (var log in state.Logs) builder.AppendLine(log);
		foreach (var diagnostic in diagnostics) builder.AppendLine($"[DIAGNOSTIC] {diagnostic.Code}: {diagnostic.Message}");
		builder.AppendLine("```").AppendLine();
		await File.WriteAllTextAsync(reportPath, builder.ToString(), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
	}

	private static CrossArmorTransferCandidateResult Failure(List<CoreIssue> issues, string code, string message)
		=> Failure(issues, [new CanonicalPlanDiagnostic(code, message)]);

	private static CrossArmorTransferCandidateResult Failure(List<CoreIssue> issues, IEnumerable<CanonicalPlanDiagnostic> diagnostics)
	{
		issues.AddRange(diagnostics.Select(diagnostic => new CoreIssue(CoreIssueSeverity.Error, diagnostic.Code, diagnostic.Message)));
		return new(false, null, null, 0, 0, 0, issues);
	}
}
