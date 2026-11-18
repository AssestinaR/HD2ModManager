using HD2ModAdaptation.Analysis;
using HD2ModAdaptation.PatchReconstruction;
using HD2ModAdaptation.PatchReconstruction.PatchWorkspace;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModAdaptation.PatchReconstruction.UnitMesh.Canonical;
using HD2ModCore.Application;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using AdaptationAssetKey = HD2ModAdaptation.PatchReconstruction.AssetKey;

namespace HD2ModCore.Tests;

// Purpose: Verifies sectional conversion upgrades outdated staged Unit patches in-staging without touching current patches or corrupting on failure.
public sealed class Legacy2SectionalOutdatedUpgradeTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "hd2-sectional-upgrade-" + Guid.NewGuid().ToString("N"));

	public Legacy2SectionalOutdatedUpgradeTests() => Directory.CreateDirectory(_root);

	[Fact]
	public async Task UpgradesOnlyOutdatedStagedUnitPatches_AndReplacesSidecars()
	{
		var unitStage = Path.Combine(_root, "unit-stage");
		Directory.CreateDirectory(unitStage);
		var outdatedPatch = await WriteUnitPatchAsync(unitStage, "9ba626afa44a3aa3.patch_0", 0x00a4cd35U, [1, 2, 3]);
		var currentPatch = await WriteUnitPatchAsync(unitStage, "9ba626afa44a3aa3.patch_1", ModUnitCompatibilityReport.CurrentUnitVersion, [9, 9, 9]);

		var fake = new RecordingReconstruction(async (_, _, _, outputDir, _, _, _, _, sourcePatchTocPath) =>
		{
			var name = Path.GetFileName(sourcePatchTocPath!);
			await WriteUnitPatchAsync(outputDir, name, ModUnitCompatibilityReport.CurrentUnitVersion, [7, 8, 9, 10]);
			return new SameKeyReconstructionOperationResult(true, outputDir, null, null, 1, 1, 0, 1, 0, []);
		});

		var notes = new List<string>();
		await Legacy2SectionalConverter.UpgradeOutdatedStagedUnitPatchesAsync(
			[outdatedPatch, currentPatch],
			_root,
			_root,
			fake,
			notes);

		Assert.Equal([outdatedPatch], fake.RequestedSourcePatchPaths);
		Assert.Empty(notes);

		var entries = await new HD2ModAdaptation.PatchReconstruction.PatchTocScanner().ScanEntriesAsync(outdatedPatch);
		var analysis = new PatchGroupAnalysis(new PatchGroupInput(outdatedPatch), [], [], [], DateTimeOffset.UtcNow, "test", PatchAnalysisDepth.Inventory, entries);
		var report = ModUnitCompatibilityReport.FromEvidence(await new UnitVersionProbe().ProbeAsync(analysis));
		Assert.Equal(UnitCompatibilityStatus.CurrentCandidate, report.Status);
		Assert.Equal([7, 8, 9, 10], await File.ReadAllBytesAsync(outdatedPatch + ".gpu_resources"));
	}

	[Fact]
	public async Task RecordsNoteAndPreservesStagedPatch_WhenSameKeyUpgradeCannotComplete()
	{
		var unitStage = Path.Combine(_root, "unit-stage");
		Directory.CreateDirectory(unitStage);
		var outdatedPatch = await WriteUnitPatchAsync(unitStage, "9ba626afa44a3aa3.patch_0", 0x00a4cd35U, [4, 5, 6]);

		var fake = new RecordingReconstruction((_, _, _, _, _, _, _, _, _) =>
			ValueTask.FromResult(new SameKeyReconstructionOperationResult(
				false,
				null,
				null,
				null,
				0,
				0,
				0,
				0,
				0,
				[new CoreIssue(CoreIssueSeverity.Error, "CurrentTargetMissing", "找不到同 ID current target Unit。")])));

		var notes = new List<string>();
		await Legacy2SectionalConverter.UpgradeOutdatedStagedUnitPatchesAsync(
			[outdatedPatch],
			_root,
			_root,
			fake,
			notes);

		Assert.Single(notes);
		Assert.Contains("找不到同 ID current target Unit", notes[0]);
		Assert.True(File.Exists(outdatedPatch));
		Assert.Equal([4, 5, 6], await File.ReadAllBytesAsync(outdatedPatch + ".gpu_resources"));
	}

	private static async Task<string> WriteUnitPatchAsync(string directory, string name, uint unitVersion, byte[] gpuBytes)
	{
		var toc = new byte[0x30];
		BitConverter.GetBytes(unitVersion).CopyTo(toc, 0x2c);
		var session = new CanonicalPatchSession();
		session.AddEntry(new CanonicalPatchSessionEntry(
			new AdaptationAssetKey(PatchUnitMeshReader.UnitTypeId, 0xa66d9b5a14448f73UL),
			CanonicalPatchEntryOwnership.TargetOutput,
			toc,
			gpuBytes,
			[]));
		Assert.True(session.Finalize(CanonicalDependencyClosureValidation.Valid).IsValid);
		await new CanonicalPatchWriter().WriteAsync(session, directory, name, null, overwriteExisting: true);
		return Path.Combine(directory, name);
	}

	public void Dispose()
	{
		try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
	}

	private sealed class RecordingReconstruction(
		Func<ModNode, string, string, string, CancellationToken, IProgress<OperationProgressEvent>?, Guid?, bool, string?, ValueTask<SameKeyReconstructionOperationResult>> handler)
		: IModSameKeyReconstructionService
	{
		public List<string> RequestedSourcePatchPaths { get; } = [];

		public ValueTask<ModSameKeyReconstructionState> InspectAsync(
			ModNode source, string modsRootDirectory, string gameDataDirectory,
			CancellationToken cancellationToken = default, IProgress<OperationProgressEvent>? progress = null,
			Guid? operationId = null, string? sourcePatchTocPath = null)
			=> throw new NotSupportedException();

		public ValueTask<SameKeyReconstructionOperationResult> GenerateCandidateAsync(
			ModNode source, string modsRootDirectory, string gameDataDirectory, string outputRootDirectory,
			CancellationToken cancellationToken = default, IProgress<OperationProgressEvent>? progress = null,
			Guid? operationId = null, bool useSharedHiddenUnitTemplate = true, string? sourcePatchTocPath = null)
		{
			if (sourcePatchTocPath is not null) RequestedSourcePatchPaths.Add(sourcePatchTocPath);
			return handler(source, modsRootDirectory, gameDataDirectory, outputRootDirectory, cancellationToken, progress, operationId, useSharedHiddenUnitTemplate, sourcePatchTocPath);
		}
	}
}
