using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HD2ModCli;

/// <summary>Private recovery journal. Never include this file in an exported report.</summary>
public sealed record ArmorDiagnosticJournal(
    int Schema, string Id, DateTimeOffset StartedUtc, string ProtocolPath,
    string OriginalBase64, string OriginalHash, string DisabledBase64, string DisabledHash,
    string Stage, bool Completed);

public sealed class ArmorDiagnosticSession
{
    public const string RelativeDirectory = "data/diagnostics/armor";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly Action ensureStopped;
    public string DirectoryPath { get; }
    public string JournalPath => Path.Combine(DirectoryPath, "active.json");
    public ArmorDiagnosticJournal Journal { get; private set; }
    public string RunDirectory => Path.Combine(DirectoryPath, Journal.Id);

    private ArmorDiagnosticSession(string directory, ArmorDiagnosticJournal journal, Action guard)
    {
        DirectoryPath = directory;
        Journal = journal;
        ensureStopped = guard;
    }

    public static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static ArmorDiagnosticSession Create(string directory, string protocolPath, Action ensureStopped)
    {
        ensureStopped();
        if (File.Exists(Path.Combine(directory, "active.json")))
        {
            var old = Open(directory, protocolPath, ensureStopped);
            if (!old.Journal.Completed) throw new InvalidOperationException("存在未恢复的诊断，请先恢复。");
        }
        var bytes = ReadProtocol(protocolPath);
        var text = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
        var document = CliArmorRegistryProtocol.ParseProtocol(text);
        if (!document.Enabled || document.Mappings.Count == 0)
            throw new InvalidOperationException("当前实际协议没有启用的映射，请先在管理器中应用待测试配置。");
        if (document.Revision == int.MaxValue) throw new InvalidOperationException("协议版本已达到上限。");
        // Leave every byte of the original in the private backup. The control
        // disables mappings only; no Piece generation or workspace serialization.
        var disabled = Encoding.UTF8.GetBytes($"schema=2\nrevision={document.Revision + 1}\nenabled=false\n");
        var journal = new ArmorDiagnosticJournal(1,
            DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow, Path.GetFullPath(protocolPath), Convert.ToBase64String(bytes), Hash(bytes),
            Convert.ToBase64String(disabled), Hash(disabled), "original", false);
        Directory.CreateDirectory(directory);
        var session = new ArmorDiagnosticSession(directory, journal, ensureStopped);
        Directory.CreateDirectory(session.RunDirectory);
        session.Save(); // Durable recovery record BEFORE any protocol write.
        return session;
    }

    public static ArmorDiagnosticSession Open(string directory, string expectedProtocolPath, Action ensureStopped)
    {
        var file = Path.Combine(directory, "active.json");
        if (new FileInfo(file).Length > 256 * 1024) throw new InvalidDataException("诊断恢复记录过大。");
        var j = JsonSerializer.Deserialize<ArmorDiagnosticJournal>(File.ReadAllText(file), Json)
            ?? throw new InvalidDataException("无法读取诊断恢复记录。");
        if (j.Schema != 1 || !System.Text.RegularExpressions.Regex.IsMatch(j.Id, @"^\d{8}-\d{6}-[a-f0-9]{32}$")
            || !string.Equals(Path.GetFullPath(j.ProtocolPath), Path.GetFullPath(expectedProtocolPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("诊断恢复目标不匹配。");
        var original = Convert.FromBase64String(j.OriginalBase64);
        var disabled = Convert.FromBase64String(j.DisabledBase64);
        if (original.Length > 65536 || disabled.Length > 65536
            || Hash(original) != j.OriginalHash || Hash(disabled) != j.DisabledHash)
            throw new InvalidDataException("诊断恢复备份校验失败。");
        _ = CliArmorRegistryProtocol.ParseProtocol(Encoding.UTF8.GetString(original).TrimStart('\uFEFF'));
        var control = CliArmorRegistryProtocol.ParseProtocol(Encoding.UTF8.GetString(disabled));
        if (control.Enabled || control.Mappings.Count != 0) throw new InvalidDataException("无效的关闭映射对照协议。");
        return new ArmorDiagnosticSession(directory, j, ensureStopped);
    }

    public void Disable()
    {
        if (Journal.Completed) throw new InvalidOperationException("诊断已完成。");
        EnsureCurrent(Journal.OriginalHash);
        Journal = Journal with { Stage = "disable_pending" };
        Save();
        ReplaceProtocol(Convert.FromBase64String(Journal.DisabledBase64), Journal.OriginalHash);
        Journal = Journal with { Stage = "disabled" };
        Save();
    }

    public void Restore()
    {
        ensureStopped();
        var hash = Hash(ReadProtocol(Journal.ProtocolPath));
        if (hash != Journal.OriginalHash && hash != Journal.DisabledHash)
            throw new InvalidOperationException("协议已被其他操作修改，拒绝覆盖。原备份仍保留，请联系支持人员处理。");
        Journal = Journal with { Stage = "restore_pending" };
        Save();
        if (hash == Journal.DisabledHash)
            ReplaceProtocol(Convert.FromBase64String(Journal.OriginalBase64), hash);
        EnsureCurrent(Journal.OriginalHash);
        Journal = Journal with { Stage = "restored", Completed = true };
        Save();
    }

    public void EnsureCurrent(string expected)
    {
        if (Hash(ReadProtocol(Journal.ProtocolPath)) != expected)
            throw new InvalidOperationException("诊断期间协议发生改变，停止测试，避免混用结果。");
    }

    public static byte[] ReadProtocol(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (input.Length > 65536) throw new InvalidDataException("协议超过 64 KiB。");
        using var output = new MemoryStream();
        input.CopyTo(output);
        if (output.Length > 65536) throw new InvalidDataException("协议超过 64 KiB。");
        return output.ToArray();
    }

    private void ReplaceProtocol(byte[] bytes, string expected)
    {
        ensureStopped();
        using var lease = new FileStream(Path.Combine(Path.GetDirectoryName(Journal.ProtocolPath)!, ".manager.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        EnsureCurrent(expected);
        ensureStopped();
        AtomicWrite(Journal.ProtocolPath, bytes);
        EnsureCurrent(Hash(bytes));
    }

    private void Save() => AtomicWrite(JournalPath, JsonSerializer.SerializeToUtf8Bytes(Journal, Json));

    public static void AtomicWrite(string path, byte[] bytes)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
