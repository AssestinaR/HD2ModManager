using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Xunit;

namespace HD2ModCli.Tests;

public sealed class ArmorDiagnosticTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "hd2-diag-test-" + Guid.NewGuid().ToString("N"));
    private string Protocol => Path.Combine(root, "registry", "armor-registry.protocol");
    private string Data => Path.Combine(root, "diagnostic");
    private readonly byte[] original = Encoding.UTF8.GetBytes("# private user comment\r\nschema=2\r\nrevision=45\r\nenabled=true\r\nmap=42,43\r\n");

    public ArmorDiagnosticTests()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Protocol)!);
        File.WriteAllBytes(Protocol, original);
    }

    [Fact]
    public void QuickMarker_RequiresMatchingForegroundGameAndCurrentRevision()
    {
        var status = new Dictionary<string, string>
        {
            ["pid"] = "42", ["revision"] = "7", ["state"] = "ready"
        };
        Assert.True(ArmorDiagnosticQuick.IsReady(status, 42, 7));
        Assert.False(ArmorDiagnosticQuick.IsReady(status, 41, 7));
        Assert.False(ArmorDiagnosticQuick.IsReady(status, 42, 8));
        Assert.False(ArmorDiagnosticQuick.AcceptMarker(41, 42, true));
        Assert.False(ArmorDiagnosticQuick.AcceptMarker(42, 42, false));
        Assert.True(ArmorDiagnosticQuick.AcceptMarker(42, 42, true));
    }

    [Fact]
    public void MemoryCleanPlans_AreDistinctAndOrdered()
    {
        Assert.Equal(new[] { 4 }, ArmorMemoryMetrics.CleanSteps(false).Select(x => x.Command));
        Assert.Equal(new[] { 2, 3, 4 }, ArmorMemoryMetrics.CleanSteps(true).Select(x => x.Command));
    }

    [Fact]
    public void MemoryCapture_ExposesComparableSystemCounters()
    {
        using var metrics = new ArmorMemoryMetrics();
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(metrics.Capture(null, null)));
        var root = doc.RootElement;
        Assert.True(root.GetProperty("physicalTotalBytes").GetUInt64() > 0);
        Assert.True(root.GetProperty("physicalAvailableBytes").GetUInt64() > 0);
        Assert.True(root.GetProperty("commitLimitBytes").GetUInt64() > 0);
        foreach (var name in new[] { "standbyCoreBytes", "standbyNormalBytes", "standbyReserveBytes",
            "modifiedPageBytes", "freeZeroBytes" })
            Assert.True(root.TryGetProperty(name, out _), name);
    }

    [Fact]
    public void CrashCleanup_RequiresEvent1000AndMatchingPidAndCreationTime()
    {
        var started = new DateTime(2026, 9, 27, 5, 30, 0, DateTimeKind.Utc);
        var ticks = started.ToFileTimeUtc();
        var report = $$"""
            {"events":[{"eventId":1000,"ProcessId":"0x2a","ProcessCreationTime":"0x{{ticks:x}}","module":"ntdll.dll"}]}
            """;
        Assert.True(ArmorDiagnosticCrashCleanup.HasMatchingCrash(report, 42, started));
        Assert.False(ArmorDiagnosticCrashCleanup.HasMatchingCrash(report, 43, started));
        Assert.False(ArmorDiagnosticCrashCleanup.HasMatchingCrash(report, 42, started.AddSeconds(1)));
        Assert.False(ArmorDiagnosticCrashCleanup.HasMatchingCrash(
            report.Replace("\"eventId\":1000", "\"eventId\":1001"), 42, started));
        Assert.False(ArmorDiagnosticCrashCleanup.HasMatchingCrash("{\"state\":\"unavailable\"}", 42, started));
    }

    [Fact]
    public void ControlAndRestore_PreserveExactBytes_AndPersistRecoveryBeforeChange()
    {
        var calls = 0;
        var session = ArmorDiagnosticSession.Create(Data, Protocol, () => calls++);
        Assert.Equal(original, File.ReadAllBytes(Protocol));
        Assert.False(session.Journal.Completed);
        session.Disable();
        var control = CliArmorRegistryProtocol.ParseProtocol(File.ReadAllText(Protocol));
        Assert.False(control.Enabled);
        Assert.Empty(control.Mappings);
        Assert.Equal(46, control.Revision);
        var restarted = ArmorDiagnosticSession.Open(Data, Protocol, () => calls++);
        restarted.Restore();
        Assert.Equal(original, File.ReadAllBytes(Protocol));
        Assert.True(restarted.Journal.Completed);
        Assert.True(calls >= 4);
    }

    [Fact]
    public void CompositionComparison_PreservesMappings_AndRestoresExactOriginal()
    {
        var session = ArmorDiagnosticSession.CreateComposition(Data, Protocol, () => { });
        session.SetCompositionStopped(false);
        var full = CliArmorRegistryProtocol.ParseProtocol(File.ReadAllText(Protocol));
        Assert.Equal("full", full.CompositionMode);
        Assert.Equal(46, full.Revision);
        Assert.True(full.Enabled);
        Assert.Single(full.Mappings);

        session.SetCompositionStopped(true);
        var existing = CliArmorRegistryProtocol.ParseProtocol(File.ReadAllText(Protocol));
        Assert.Equal("existing_only", existing.CompositionMode);
        Assert.Equal(47, existing.Revision);
        Assert.Equal(full.Mappings, existing.Mappings);

        var recovered = ArmorDiagnosticSession.Open(Data, Protocol, () => { });
        recovered.Restore();
        Assert.Equal(original, File.ReadAllBytes(Protocol));
    }

    [Fact]
    public void CompositionComparison_DoesNotOverwriteExternalChanges()
    {
        var session = ArmorDiagnosticSession.CreateComposition(Data, Protocol, () => { });
        session.SetCompositionStopped(true);
        File.WriteAllText(Protocol, "schema=2\nrevision=99\nenabled=false\n");
        Assert.Throws<InvalidOperationException>(() => session.Restore());
        Assert.Contains("revision=99", File.ReadAllText(Protocol));
    }

    [Fact]
    public void LiveSwitch_AlternatesExactProtocols_AndRecoveryAfterPendingWrite()
    {
        var stopped = true;
        var session = ArmorDiagnosticSession.Create(Data, Protocol,
            () => { if (!stopped) throw new InvalidOperationException("running"); });
        session.Disable();
        var disabled = File.ReadAllBytes(Protocol);
        stopped = false;
        session.EnableLive();
        Assert.Equal(original, File.ReadAllBytes(Protocol));
        session.DisableLive();
        Assert.Equal(disabled, File.ReadAllBytes(Protocol));
        stopped = true;
        var pending = session.Journal with { Stage = "enable_live_pending" };
        File.WriteAllText(session.JournalPath, JsonSerializer.Serialize(pending, ArmorDiagnosticEvidence.Json));
        File.WriteAllBytes(Protocol, original);
        var resumed = ArmorDiagnosticSession.Open(Data, Protocol, () => { });
        resumed.Restore();
        Assert.Equal(original, File.ReadAllBytes(Protocol));
        Assert.True(resumed.Journal.Completed);
    }

    [Fact]
    public void LiveSwitch_RejectsUnexpectedProtocolWithoutOverwritingIt()
    {
        var session = ArmorDiagnosticSession.Create(Data, Protocol, () => { });
        session.Disable();
        File.WriteAllText(Protocol, "schema=2\nrevision=80\nenabled=false\n");
        Assert.Throws<InvalidOperationException>(() => session.EnableLive());
        Assert.Contains("80", File.ReadAllText(Protocol));
    }

    [Fact]
    public void LiveSwitch_RespectsManagerProtocolLock()
    {
        var session = ArmorDiagnosticSession.Create(Data, Protocol, () => { });
        session.Disable();
        using var lease = new FileStream(Path.Combine(Path.GetDirectoryName(Protocol)!, ".manager.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Assert.Throws<IOException>(() => session.EnableLive());
        Assert.Equal(Convert.FromBase64String(session.Journal.DisabledBase64), File.ReadAllBytes(Protocol));
    }

    [Fact]
    public void MatrixReady_RequiresPidRevisionAndActiveCount()
    {
        var status = new Dictionary<string, string>
        {
            ["pid"] = "42", ["revision"] = "7", ["state"] = "ready", ["active_mappings"] = "32"
        };
        Assert.True(ArmorDiagnosticMatrix.Ready(status, 42, 7, 32));
        Assert.False(ArmorDiagnosticMatrix.Ready(status, 43, 7, 32));
        Assert.False(ArmorDiagnosticMatrix.Ready(status, 42, 8, 32));
        Assert.False(ArmorDiagnosticMatrix.Ready(status, 42, 7, 24));
    }

    [Fact]
    public void GuardFailureAndExternalEdit_NeverOverwrite()
    {
        var stopped = true;
        var session = ArmorDiagnosticSession.Create(Data, Protocol,
            () => { if (!stopped) throw new InvalidOperationException("running"); });
        stopped = false;
        Assert.Throws<InvalidOperationException>(() => session.Disable());
        Assert.Equal(original, File.ReadAllBytes(Protocol));
        stopped = true;
        session.Disable();
        File.WriteAllText(Protocol, "schema=2\nrevision=99\nenabled=false\n");
        Assert.Throws<InvalidOperationException>(() => session.Restore());
        Assert.Contains("99", File.ReadAllText(Protocol));
        Assert.False(ArmorDiagnosticSession.Open(Data, Protocol, () => { }).Journal.Completed);
    }

    [Fact]
    public void PendingWriteCrash_RecoveryAcceptsEitherKnownProtocol()
    {
        var session = ArmorDiagnosticSession.Create(Data, Protocol, () => { });
        var j = session.Journal with { Stage = "disable_pending" };
        File.WriteAllText(session.JournalPath, JsonSerializer.Serialize(j, ArmorDiagnosticEvidence.Json));
        // Simulate death after replace but before final journal update.
        File.WriteAllBytes(Protocol, Convert.FromBase64String(j.DisabledBase64));
        var recovered = ArmorDiagnosticSession.Open(Data, Protocol, () => { });
        recovered.Restore();
        Assert.Equal(original, File.ReadAllBytes(Protocol));
    }

    [Fact]
    public void BackupTamperAndTargetMismatch_AreRejected()
    {
        var session = ArmorDiagnosticSession.Create(Data, Protocol, () => { });
        Assert.Throws<InvalidDataException>(() => ArmorDiagnosticSession.Open(Data, Protocol + ".other", () => { }));
        File.WriteAllText(session.JournalPath, JsonSerializer.Serialize(session.Journal with { OriginalHash = "bad" },
            ArmorDiagnosticEvidence.Json));
        Assert.Throws<InvalidDataException>(() => ArmorDiagnosticSession.Open(Data, Protocol, () => { }));
    }

    [Fact]
    public void RegistryLock_RefusesConcurrentWrite()
    {
        var session = ArmorDiagnosticSession.Create(Data, Protocol, () => { });
        using var lease = new FileStream(Path.Combine(Path.GetDirectoryName(Protocol)!, ".manager.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Assert.Throws<IOException>(() => session.Disable());
        Assert.Equal(original, File.ReadAllBytes(Protocol));
    }

    [Fact]
    public void Export_ExcludesJournalBackupPathsAndNames()
    {
        var session = ArmorDiagnosticSession.Create(Data, Protocol, () => { });
        var doc = CliArmorRegistryProtocol.ParseProtocol(
            "schema=2\nrevision=1\nenabled=true\nmodel=PrivateUserModel,\n"
            + "model_piece=PrivateUserModel,0,1234567890abcdef,2,0,0,true\nmap_model=42,PrivateUserModel\n");
        ArmorDiagnosticEvidence.Save(session.RunDirectory, "protocol-summary.json", ArmorDiagnosticEvidence.ProtocolSummary(doc));
        ArmorDiagnosticEvidence.Save(session.RunDirectory, "summary.json", new { restored = false });
        File.WriteAllText(Path.Combine(session.RunDirectory, "original.protocol"), "PRIVATE_BACKUP");
        File.WriteAllText(Path.Combine(session.RunDirectory, "active.json"), "PRIVATE_JOURNAL");
        var filtered = ArmorDiagnosticEvidence.SafeStatus(new Dictionary<string, string>
        {
            ["pid"] = "123", ["message"] = @"C:\Users\Secret\error", ["blob_base"] = "0x123",
            ["state"] = "ready", ["runtime_version"] = @"C:\Secret"
        });
        Assert.Equal(2, filtered.Count);
        using var zip = ZipFile.OpenRead(ArmorDiagnosticEvidence.Export(session.RunDirectory));
        Assert.Equal(2, zip.Entries.Count);
        foreach (var e in zip.Entries)
        {
            using var reader = new StreamReader(e.Open());
            var text = reader.ReadToEnd();
            Assert.DoesNotContain("PrivateUserModel", text);
            Assert.DoesNotContain("PRIVATE", text);
            Assert.DoesNotContain(root, text);
        }
    }

    [Fact]
    public void EmptyOrDisabledOriginal_IsRejectedWithoutRecoveryJournal()
    {
        File.WriteAllText(Protocol, "schema=2\nrevision=1\nenabled=false\n");
        Assert.Throws<InvalidOperationException>(() => ArmorDiagnosticSession.Create(Data, Protocol, () => { }));
        Assert.False(File.Exists(Path.Combine(Data, "active.json")));
    }

    [Fact]
    public void QuickExport_ContainsMarkerSamplesAndEventsButNotPrivateCaptureConfig()
    {
        var session = ArmorDiagnosticSession.Create(Data, Protocol, () => { });
        File.WriteAllText(Path.Combine(session.RunDirectory, "timeline.jsonl"), "{\"action\":\"hotkey_F8\"}");
        File.WriteAllText(Path.Combine(session.RunDirectory, "samples-quick.jsonl"), "{\"pid\":42}");
        File.WriteAllText(Path.Combine(session.RunDirectory, "events-quick.json"), "{\"events\":[]}");
        File.WriteAllText(Path.Combine(session.RunDirectory, "capture-config.json"), "PRIVATE CONFIG");
        using var zip = ZipFile.OpenRead(ArmorDiagnosticEvidence.Export(session.RunDirectory));
        Assert.Contains(zip.Entries, e => e.Name == "timeline.jsonl");
        Assert.Contains(zip.Entries, e => e.Name == "samples-quick.jsonl");
        Assert.Contains(zip.Entries, e => e.Name == "events-quick.json");
        Assert.DoesNotContain(zip.Entries, e => e.Name == "capture-config.json" || e.Name == "active.json");
    }

    [Fact]
    public void MatrixExport_ContainsStatusAndEventsButNotPrivateRecovery()
    {
        var session = ArmorDiagnosticSession.Create(Data, Protocol, () => { });
        File.WriteAllText(Path.Combine(session.RunDirectory, "samples-matrix.jsonl"), "{\"pid\":42}");
        File.WriteAllText(Path.Combine(session.RunDirectory, "events-matrix.json"), "{\"events\":[]}");
        using var zip = ZipFile.OpenRead(ArmorDiagnosticEvidence.Export(session.RunDirectory));
        Assert.Contains(zip.Entries, e => e.Name == "samples-matrix.jsonl");
        Assert.Contains(zip.Entries, e => e.Name == "events-matrix.json");
        Assert.DoesNotContain(zip.Entries, e => e.Name == "active.json");
    }

    public void Dispose()
    {
        // Fixture root is a literal per-test directory created above.
        Directory.Delete(root, true);
    }
}
