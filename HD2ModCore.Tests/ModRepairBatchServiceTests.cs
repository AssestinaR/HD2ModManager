using System.Text.Json;
using HD2ModAdaptation.Analysis;
using HD2ModCore.Application;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

// Purpose: Exercises real ModRepairBatchService orchestration, filesystem commit/recovery, manifests and telemetry.
public sealed class ModRepairBatchServiceTests
{
    [Fact]
    public async Task RepairsNormalMod_AndWritesParseableManifest()
    {
        using var fixture = new Fixture();
        var source = fixture.AddMod("normal");
        fixture.Reconstruction.InspectResult = ReadyState(source);
        fixture.Reconstruction.CandidateFactory = (_, _, _, output, _, _, _) =>
        {
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "unit.patch"), "new");
            return new ValueTask<SameKeyReconstructionOperationResult>(new SameKeyReconstructionOperationResult(true, output, null, null, 1, 1, 0, 0, 0, Array.Empty<CoreIssue>()));
        };

        var events = new List<OperationProgressEvent>();
        var operationId = Guid.NewGuid();
        var result = await fixture.Service.RepairAsync(new[] { source }, fixture.ModsRoot, fixture.GameData, progress: new CollectingProgress(events), operationId: operationId);

        Assert.Equal(ModRepairBatchModStatus.Repaired, Assert.Single(result.Mods).Status);
        Assert.Equal("new", File.ReadAllText(Path.Combine(fixture.ModsRoot, "normal", "unit.patch")));
        AssertManifest(result);
        Assert.Equal(operationId, result.OperationId);
        Assert.Equal(1, result.StartedModCount);
        Assert.Equal(1, result.RepairedModCount);
    }

    [Fact]
    public async Task RepairsNormalMod_InvalidatesInformationCenterAfterCommit()
    {
        using var fixture = new Fixture();
        var source = fixture.AddMod("invalidate-after-commit");
        fixture.Reconstruction.InspectResult = ReadyState(source);
        fixture.Reconstruction.CandidateFactory = (_, _, _, output, _, _, _) =>
        {
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "unit.patch"), "new");
            return new ValueTask<SameKeyReconstructionOperationResult>(new SameKeyReconstructionOperationResult(true, output, null, null, 1, 1, 0, 0, 0, Array.Empty<CoreIssue>()));
        };

        var result = await fixture.Service.RepairAsync(new[] { source }, fixture.ModsRoot, fixture.GameData);

        Assert.Equal(ModRepairBatchModStatus.Repaired, Assert.Single(result.Mods).Status);
        Assert.Contains(source.Id, fixture.InformationCenter.InvalidatedNodeIds);
    }

    [Fact]
    public async Task CountsAreConserved_AndEveryChildHasOneTerminalEvent()
    {
        using var fixture = new Fixture();
        var ready = fixture.AddMod("ready");
        var skipped = fixture.AddMod("skipped");
        var failed = fixture.AddMod("failed");
        fixture.Reconstruction.InspectResult = ReadyState(ready);
        fixture.Reconstruction.InspectResults[skipped.Id] = ReadyState(skipped) with { Plan = null, Issues = new[] { new CoreIssue(CoreIssueSeverity.Warning, "NO", "not repairable") } };
        fixture.Reconstruction.InspectResults[failed.Id] = ReadyState(failed);
        fixture.Reconstruction.CandidateFactory = (source, _, _, output, _, _, _) =>
        {
            if (source.Id == failed.Id) return new ValueTask<SameKeyReconstructionOperationResult>(new SameKeyReconstructionOperationResult(false, null, null, null, 0, 0, 0, 0, 0, Array.Empty<CoreIssue>()));
            Directory.CreateDirectory(output); File.WriteAllText(Path.Combine(output, "unit.patch"), "new");
            return new ValueTask<SameKeyReconstructionOperationResult>(new SameKeyReconstructionOperationResult(true, output, null, null, 1, 1, 0, 0, 0, Array.Empty<CoreIssue>()));
        };
        var events = new List<OperationProgressEvent>();
        var result = await fixture.Service.RepairAsync(new[] { ready, skipped, failed }, fixture.ModsRoot, fixture.GameData, progress: new CollectingProgress(events));

        Assert.Equal(3, result.RequestedModCount);
        Assert.Equal(result.RequestedModCount, result.RepairedModCount + result.SkippedModCount + result.FailedModCount + result.CanceledModCount + result.NotStartedModCount);
        foreach (var child in events.Where(e => e.Kind == OperationKind.RepairBatchItem).GroupBy(e => e.OperationId))
        {
            Assert.Single(child.Where(e => e.IsTerminal));
            Assert.All(child, e => Assert.Equal(events.IndexOf(e), events.OrderBy(x => x.Sequence).ToList().IndexOf(e)));
            Assert.All(child, e => Assert.Equal(result.OperationId, e.ParentOperationId));
        }
        Assert.Equal(events.Count, events.Select(e => e.Sequence).Distinct().Count());
        Assert.Equal(events.Select(e => e.Sequence).OrderBy(sequence => sequence), events.Select(e => e.Sequence));
        Assert.All(events.Where(e => e.Kind == OperationKind.RepairBatchItem && e.IsTerminal), child =>
            Assert.True(child.Sequence < events.Last(e => e.Kind == OperationKind.RepairBatch && e.StageId == "Finalize").Sequence));
    }

    [Fact]
    public async Task FirstCancellation_LeavesAllItemsNotStarted_AndManifestExists()
    {
        using var fixture = new Fixture();
        var sources = new[] { fixture.AddMod("one"), fixture.AddMod("two") };
        using var cts = new CancellationTokenSource(); cts.Cancel();
        var events = new List<OperationProgressEvent>();
        var result = await fixture.Service.RepairAsync(sources, fixture.ModsRoot, fixture.GameData, cts.Token, new CollectingProgress(events));

        Assert.Equal(2, result.NotStartedModCount);
        Assert.All(result.Mods, item => Assert.Equal(ModRepairBatchModStatus.NotStarted, item.Status));
        AssertManifest(result);
        Assert.DoesNotContain(events, e => e.Kind == OperationKind.RepairBatchItem && e.StageId == "BatchPrepare");
    }

    [Fact]
    public async Task MiddleCancellation_LeavesRemainingItemsNotStarted()
    {
        using var fixture = new Fixture();
        var first = fixture.AddMod("first"); var second = fixture.AddMod("second"); var third = fixture.AddMod("third");
        fixture.Reconstruction.InspectResult = ReadyState(first);
        fixture.Reconstruction.InspectResults[second.Id] = ReadyState(second);
        fixture.Reconstruction.InspectResults[third.Id] = ReadyState(third);
        fixture.Reconstruction.InspectAction = source => { if (source.Id == first.Id) fixture.Cancellation.Cancel(); };
        fixture.Reconstruction.CandidateFactory = (_, _, _, output, _, _, _) => { Directory.CreateDirectory(output); File.WriteAllText(Path.Combine(output, "unit.patch"), "new"); return new ValueTask<SameKeyReconstructionOperationResult>(new SameKeyReconstructionOperationResult(true, output, null, null, 1, 1, 0, 0, 0, Array.Empty<CoreIssue>())); };

        var result = await fixture.Service.RepairAsync(new[] { first, second, third }, fixture.ModsRoot, fixture.GameData, fixture.Cancellation.Token);

        Assert.Equal(ModRepairBatchModStatus.Canceled, result.Mods.Single(x => x.NodeId == first.Id).Status);
        Assert.Equal(2, result.NotStartedModCount);
        Assert.All(result.Mods.Where(x => x.NodeId != first.Id), x => Assert.Equal(ModRepairBatchModStatus.NotStarted, x.Status));
        AssertManifest(result);
    }

    [Fact]
    public async Task CommitFailure_RestoresOriginalPatch()
    {
        using var fixture = new Fixture();
        var source = fixture.AddMod("restore");
        fixture.Reconstruction.InspectResult = ReadyState(source);
        fixture.Reconstruction.CandidateFactory = (_, _, _, output, _, _, _) => { Directory.CreateDirectory(output); File.WriteAllText(Path.Combine(output, "unit.patch"), "new"); return new ValueTask<SameKeyReconstructionOperationResult>(new SameKeyReconstructionOperationResult(true, output, null, null, 1, 1, 0, 0, 0, Array.Empty<CoreIssue>())); };
        fixture.CommitSeam = phase => { if (phase == "AfterDelete") throw new IOException("injected commit failure"); };

        var result = await fixture.Service.RepairAsync(new[] { source }, fixture.ModsRoot, fixture.GameData);

        var item = Assert.Single(result.Mods);
        Assert.Equal(ModRepairBatchModStatus.CommitFailed, item.Status);
        Assert.True(item.RestoreCompleted);
        Assert.True(item.RestoreAttempted);
        Assert.Equal("CommitFailed", item.StageId);
        Assert.Equal("original", File.ReadAllText(Path.Combine(fixture.ModsRoot, "restore", "unit.patch")));
        AssertManifest(result);
    }

    [Fact]
    public async Task CommitFailureAfterCopy_RestoresOriginalPatchAndReportsFailedCommit()
    {
        using var fixture = new Fixture();
        var source = fixture.AddMod("restore-after-copy");
        fixture.Reconstruction.InspectResult = ReadyState(source);
        fixture.Reconstruction.CandidateFactory = (_, _, _, output, _, _, _) =>
        {
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "unit.patch"), "new");
            return ValueTask.FromResult(new SameKeyReconstructionOperationResult(true, output, null, null, 1, 1, 0, 0, 0, Array.Empty<CoreIssue>()));
        };
        fixture.CommitSeam = phase => { if (phase == "AfterCopy") throw new IOException("injected post-copy failure"); };

        var result = await fixture.Service.RepairAsync(new[] { source }, fixture.ModsRoot, fixture.GameData);

        var item = Assert.Single(result.Mods);
        Assert.Equal(ModRepairBatchModStatus.CommitFailed, item.Status);
        Assert.True(item.RestoreAttempted);
        Assert.True(item.RestoreCompleted);
        Assert.Equal("original", File.ReadAllText(Path.Combine(fixture.ModsRoot, "restore-after-copy", "unit.patch")));
        Assert.Equal(1, result.FailedModCount);
    }

    [Fact]
    public async Task CommitFailureAfterModifiedSource_WhenRestoreFails_ReportsAuditAndFailedParent()
    {
        using var fixture = new Fixture();
        var source = fixture.AddMod("restore-failure");
        fixture.Reconstruction.InspectResult = ReadyState(source);
        fixture.Reconstruction.CandidateFactory = (_, _, _, output, _, _, _) =>
        {
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "unit.patch"), "new");
            return ValueTask.FromResult(new SameKeyReconstructionOperationResult(true, output, null, null, 1, 1, 0, 0, 0, Array.Empty<CoreIssue>()));
        };
        var events = new List<OperationProgressEvent>();
        fixture.CommitSeam = phase =>
        {
            if (phase != "AfterCopy") return;
            fixture.DeleteBackupFiles();
            throw new IOException("injected post-copy commit failure");
        };

        var result = await fixture.Service.RepairAsync(new[] { source }, fixture.ModsRoot, fixture.GameData, progress: new CollectingProgress(events));

        var item = Assert.Single(result.Mods);
        Assert.Equal(ModRepairBatchModStatus.CommitFailed, item.Status);
        Assert.True(item.BackupCompleted);
        Assert.False(item.CommitCompleted);
        Assert.True(item.RestoreAttempted);
        Assert.False(item.RestoreCompleted);
        Assert.Equal("CommitFailed", item.StageId);
        Assert.NotNull(item.CandidateDirectory);
        Assert.NotNull(item.BackupDirectory);
        Assert.Contains("提交失败", item.Detail);
        Assert.Contains("恢复失败", item.Detail);
        Assert.Equal(1, result.FailedModCount);
        var parentTerminal = Assert.Single(events.Where(e => e.Kind == OperationKind.RepairBatch && e.IsTerminal));
        Assert.Equal(result.OperationId, parentTerminal.OperationId);
        Assert.Equal(OperationState.Failed, parentTerminal.State);
        Assert.Equal(OperationStage.Failed, parentTerminal.Stage);
        Assert.False(result.ManifestWriteFailed);
        Assert.Equal("backup.json", Path.GetFileName(result.ManifestPath));
        Assert.True(File.Exists(result.ManifestPath));
        var manifest = JsonSerializer.Deserialize<ModRepairBatchResult>(File.ReadAllText(result.ManifestPath));
        Assert.NotNull(manifest);
        Assert.Equal(result.OperationId, manifest!.OperationId);
        var persistedItem = Assert.Single(manifest.Mods, mod => mod.NodeId == item.NodeId);
        Assert.Equal(ModRepairBatchModStatus.CommitFailed, persistedItem.Status);
        Assert.True(persistedItem.RestoreAttempted);
        Assert.False(persistedItem.RestoreCompleted);
        Assert.False(persistedItem.CommitCompleted);
        Assert.Equal("BatchFailed", parentTerminal.IssueCode);
        Assert.All(events.Where(e => e.Kind == OperationKind.RepairBatchItem), child => Assert.Equal(result.OperationId, child.ParentOperationId));
    }

    [Fact]
    public async Task InitializationFailure_LeavesAllItemsNotStarted_AndHasOneParentFailure()
    {
        using var fixture = new Fixture { InitializationSeam = () => throw new IOException("injected initialization failure") };
        var sources = new[] { fixture.AddMod("init-one"), fixture.AddMod("init-two") };
        var events = new List<OperationProgressEvent>();

        var result = await fixture.Service.RepairAsync(sources, fixture.ModsRoot, fixture.GameData, progress: new CollectingProgress(events));

        Assert.Equal(2, result.NotStartedModCount);
        Assert.All(result.Mods, item => Assert.Equal(ModRepairBatchModStatus.NotStarted, item.Status));
        Assert.Equal(result.RequestedModCount, result.RepairedModCount + result.SkippedModCount + result.FailedModCount + result.CanceledModCount + result.NotStartedModCount);
        Assert.Single(events.Where(e => e.Kind == OperationKind.RepairBatch && e.IsTerminal));
        Assert.Equal(OperationState.Failed, events.Last(e => e.Kind == OperationKind.RepairBatch && e.IsTerminal).State);
        Assert.Equal(2, events.Count(e => e.Kind == OperationKind.RepairBatchItem && e.IsTerminal));
        Assert.All(events.Where(e => e.Kind == OperationKind.RepairBatchItem), child => Assert.Equal(result.OperationId, child.ParentOperationId));
    }

    [Fact]
    public async Task ManifestFailure_SetsManifestIssueAndHasOneParentFailure()
    {
        using var fixture = new Fixture { ManifestWriter = (_, _) => throw new IOException("injected manifest failure") };
        var source = fixture.AddMod("manifest-failure");

        var result = await fixture.Service.RepairAsync(new[] { source }, fixture.ModsRoot, fixture.GameData);

        Assert.True(result.ManifestWriteFailed);
        Assert.Equal("ManifestWriteFailed", result.ManifestIssueCode);
        Assert.Single(result.Mods);
        Assert.Equal(result.RequestedModCount, result.RepairedModCount + result.SkippedModCount + result.FailedModCount + result.CanceledModCount + result.NotStartedModCount);
    }

    [Fact]
    public async Task UnknownException_IsReportedAsCandidateFailure_AndManifestIsValid()
    {
        using var fixture = new Fixture();
        var source = fixture.AddMod("exception");
        fixture.Reconstruction.InspectAction = _ => throw new InvalidOperationException("boom");

        var result = await fixture.Service.RepairAsync(new[] { source }, fixture.ModsRoot, fixture.GameData);

        Assert.Equal(ModRepairBatchModStatus.CandidateFailed, Assert.Single(result.Mods).Status);
        Assert.Contains("InvalidOperationException", result.Mods[0].Detail);
        AssertManifest(result);
    }

    [Fact]
    public async Task ProgressCallbackFailureAfterCommit_DoesNotRepeatOrRestore_AndManifestRemainsParseable()
    {
        using var fixture = new Fixture();
        var source = fixture.AddMod("callback-failure");
        fixture.Reconstruction.InspectResult = ReadyState(source);
        fixture.Reconstruction.CandidateFactory = (_, _, _, output, _, _, _) =>
        {
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "unit.patch"), "new");
            return new ValueTask<SameKeyReconstructionOperationResult>(new SameKeyReconstructionOperationResult(true, output, null, null, 1, 1, 0, 0, 0, Array.Empty<CoreIssue>()));
        };
        var progress = new ThrowAfterCommitProgress();

        var result = await fixture.Service.RepairAsync(new[] { source }, fixture.ModsRoot, fixture.GameData, progress: progress);

        var item = Assert.Single(result.Mods);
        Assert.Equal(ModRepairBatchModStatus.Repaired, item.Status);
        Assert.Equal("new", File.ReadAllText(Path.Combine(fixture.ModsRoot, "callback-failure", "unit.patch")));
        Assert.False(item.RestoreAttempted);
        Assert.Single(progress.Events.Where(e => e.Kind == OperationKind.RepairBatchItem && e.IsTerminal));
        AssertManifest(result);
    }

    [Fact]
    public async Task ParentCompletedEvent_ComesAfterAllChildTerminalEvents()
    {
        using var fixture = new Fixture();
        var first = fixture.AddMod("first"); var second = fixture.AddMod("second");
        fixture.Reconstruction.InspectResult = ReadyState(first);
        fixture.Reconstruction.InspectResults[second.Id] = ReadyState(second);
        fixture.Reconstruction.CandidateFactory = (_, _, _, output, _, _, _) => { Directory.CreateDirectory(output); File.WriteAllText(Path.Combine(output, "unit.patch"), "new"); return new ValueTask<SameKeyReconstructionOperationResult>(new SameKeyReconstructionOperationResult(true, output, null, null, 1, 1, 0, 0, 0, Array.Empty<CoreIssue>())); };
        var events = new List<OperationProgressEvent>();
        var result = await fixture.Service.RepairAsync(new[] { first, second }, fixture.ModsRoot, fixture.GameData, progress: new CollectingProgress(events));

        AssertManifest(result);
        var parentCompleted = events.Last(e => e.Kind == OperationKind.RepairBatch && e.State == OperationState.Completed);
        Assert.All(events.Where(e => e.Kind == OperationKind.RepairBatchItem), child => Assert.True(child.Sequence < parentCompleted.Sequence));
        Assert.All(events.Where(e => e.Kind == OperationKind.RepairBatchItem), child => Assert.Equal(result.OperationId, child.ParentOperationId));
        Assert.Equal(2, events.Count(e => e.Kind == OperationKind.RepairBatchItem && e.IsTerminal));
    }

    private static ModSameKeyReconstructionState ReadyState(ModNode source) => new(source.Id, "source.toc", new SameKeyReconstructionPlan(new("source.toc", "game"), new[] { new SameKeyUnitReconstructionPlan(default, new PatchTocEntry(default, "source", "source.patch"), null, Array.Empty<ArchiveMetadata>(), null, Array.Empty<CoreIssue>()) }, Array.Empty<CoreIssue>()), true, 1, 0, 0, 0, 0, Array.Empty<CoreIssue>());

    private static void AssertManifest(ModRepairBatchResult result)
    {
        Assert.True(File.Exists(result.ManifestPath));
        var parsed = JsonSerializer.Deserialize<ModRepairBatchResult>(File.ReadAllText(result.ManifestPath));
        Assert.NotNull(parsed);
        Assert.Equal(result.RequestedModCount, parsed!.RequestedModCount);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "hd2-repair-tests", Guid.NewGuid().ToString("N"));
        public readonly string ModsRoot; public readonly string GameData; public readonly CancellationTokenSource Cancellation = new();
        public readonly FakeReconstruction Reconstruction = new();
        public readonly FakeParser Parser = new();
        public readonly FakeInformationCenter InformationCenter = new(new Dictionary<ModNodeId, ModContentFacts>());
        public Action? InitializationSeam { get; set; }
        public Action<string>? CommitSeam { get; set; }
        public Func<string, string, Task>? ManifestWriter { get; set; }
		public IModRepairBatchService Service => new ModRepairBatchService(new StoragePaths(root), Reconstruction, Parser, InitializationSeam, CommitSeam, ManifestWriter, InformationCenter);
        public Fixture()
        {
            ModsRoot = Path.Combine(root, "mods"); GameData = Path.Combine(root, "game"); Directory.CreateDirectory(ModsRoot); Directory.CreateDirectory(GameData);
        }
        public ModNode AddMod(string name)
        {
            var node = new ModNode(ModNodeId.New(), name, new ModNodeMetadata(name, null, DateTimeOffset.UtcNow, null), Array.Empty<PatchGroupKey>(), Array.Empty<ModNodeId>());
            var directory = Path.Combine(ModsRoot, name); Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, "unit.patch"), "original"); return node;
        }
        public void DeleteBackupFiles()
        {
            if (!Directory.Exists(Path.Combine(root, "backups"))) return;
            foreach (var directory in Directory.GetDirectories(Path.Combine(root, "backups"))) Directory.Delete(directory, true);
        }
        public void Dispose() { Cancellation.Dispose(); try { Directory.Delete(root, true); } catch { } }
    }

    private sealed class CollectingProgress(List<OperationProgressEvent> events) : IProgress<OperationProgressEvent>
    {
        public void Report(OperationProgressEvent value) => events.Add(value);
    }

    private sealed class FakeParser : IPatchFileNameParser
    {
        public int ThrowOnCall { get; set; }
        private int calls;
        public bool TryParse(string fileName, out PatchFileNameInfo? info) { if (ThrowOnCall != 0 && ++calls == ThrowOnCall) throw new IOException("injected commit failure"); info = null; return fileName.EndsWith(".patch", StringComparison.OrdinalIgnoreCase); }
        public PatchFileNameInfo Parse(string fileName) => throw new NotSupportedException();
    }

    private sealed class ThrowAfterCommitProgress : IProgress<OperationProgressEvent>
    {
        public List<OperationProgressEvent> Events { get; } = new();
        private bool thrown;

        public void Report(OperationProgressEvent value)
        {
            Events.Add(value);
            if (!thrown && value.Kind == OperationKind.RepairBatchItem && value.IsTerminal && value.StageId == "Finalize")
            {
                thrown = true;
                throw new InvalidOperationException("injected progress failure");
            }
        }
    }

    private sealed class FakeReconstruction : IModSameKeyReconstructionService
    {
        public ModSameKeyReconstructionState? InspectResult; public Dictionary<ModNodeId, ModSameKeyReconstructionState> InspectResults { get; } = new(); public Action<ModNode>? InspectAction; public Func<ModNode, string, string, string, CancellationToken, IProgress<OperationProgressEvent>?, Guid?, ValueTask<SameKeyReconstructionOperationResult>>? CandidateFactory;
		public ValueTask<ModSameKeyReconstructionState> InspectAsync(ModNode source, string _, string __, CancellationToken cancellationToken = default, IProgress<OperationProgressEvent>? progress = null, Guid? operationId = null, string? sourcePatchTocPath = null) { InspectAction?.Invoke(source); cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(InspectResults.TryGetValue(source.Id, out var state) ? state : InspectResult ?? throw new InvalidOperationException("no state")); }
		public ValueTask<SameKeyReconstructionOperationResult> GenerateCandidateAsync(ModNode source, string mods, string game, string output, CancellationToken token = default, IProgress<OperationProgressEvent>? progress = null, Guid? operationId = null, bool useSharedHiddenUnitTemplate = true, string? sourcePatchTocPath = null) => CandidateFactory?.Invoke(source, mods, game, output, token, progress, operationId) ?? throw new InvalidOperationException("no candidate");
    }
}
