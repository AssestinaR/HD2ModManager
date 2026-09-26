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

    public void Dispose()
    {
        // Fixture root is a literal per-test directory created above.
        Directory.Delete(root, true);
    }
}
