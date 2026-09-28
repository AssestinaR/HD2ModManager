using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HD2ModCore.Infrastructure;

namespace HD2ModCli;

public static class ArmorDiagnosticEvidence
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    // No arbitrary string fields (paths, user names, model names, error messages).
    public static Dictionary<string, string> SafeStatus(IReadOnlyDictionary<string, string> values)
    {
        var result = new Dictionary<string, string>();
        foreach (var key in new[] { "pid", "revision", "active_mappings", "retained_clone_count", "retained_clone_bytes",
            "game_timestamp", "game_image_size", "last_built", "last_reused", "last_removed", "last_writes",
            "last_prepare_ms", "last_publish_ms", "locate_ms", "located_after_ms", "first_applied_after_ms", "record_count" })
            if (values.TryGetValue(key, out var value) && ulong.TryParse(value, out _)) result[key] = value;
        foreach (var key in new[] { "runtime_version", "state", "deployment_mode", "locator_pacing" })
            if (values.TryGetValue(key, out var value) && Regex.IsMatch(value, @"^[a-zA-Z0-9_.-]{1,64}$"))
                result[key] = value;
        return result;
    }

    public static Dictionary<string, string> ReadStatus(string registryRoot)
    {
        var result = new Dictionary<string, string>();
        var path = Path.Combine(registryRoot, "runtime.status");
        if (!File.Exists(path)) return result;
        if (new FileInfo(path).Length > 16384) throw new InvalidDataException("运行状态文件过大。");
        foreach (var line in File.ReadAllLines(path))
        {
            var split = line.IndexOf('=');
            if (split > 0) result[line[..split]] = line[(split + 1)..];
        }
        return result;
    }

    public static object ProtocolSummary(CliArmorRegistryDocument doc)
    {
        var models = doc.Models ?? [];
        var aliases = models.Select((m, i) => (m.Key, Alias: $"model_{i + 1}")).ToDictionary(x => x.Key, x => x.Alias);
        return new
        {
            doc.Revision, doc.Enabled,
            mappings = doc.Mappings.Select(m => new { m.TargetItemId, m.SourceItemId,
                model = m.ModelKey is not null && aliases.TryGetValue(m.ModelKey, out var a) ? a : null }),
            models = models.Select(m => new { alias = aliases[m.Key], m.SourceItemId, pieces = m.Pieces }),
            overrides = doc.PieceOverrides, targetModes = doc.TargetModes
        };
    }

    public static void Save(string directory, string file, object value)
        => ArmorDiagnosticSession.AtomicWrite(Path.Combine(directory, file), JsonSerializer.SerializeToUtf8Bytes(value, Json));

    public static void Mark(string directory, string stage, string action, string outcome, int? pid = null)
    {
        var line = JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, stage, action, outcome, pid });
        using var stream = new FileStream(Path.Combine(directory, "timeline.jsonl"), FileMode.Append, FileAccess.Write, FileShare.Read);
        stream.Write(Encoding.UTF8.GetBytes(line + "\n"));
        stream.Flush(true);
    }

    public static async Task<object> InventoryAsync(string gameData)
    {
        var units = await RuntimeArmorResourceFilter.ReadDeployedUnitsAsync(gameData);
        var files = new List<object>();
        foreach (var path in Directory.EnumerateFiles(gameData).OrderBy(Path.GetFileName))
        {
            var name = Path.GetFileName(path);
            if (!Regex.IsMatch(name, @"^[a-fA-F0-9]{16}\.patch_\d+(\.stream|\.gpu_resources)?$")) continue;
            using var file = File.OpenRead(path); // follows links, unlike FileInfo.Length
            // Hash base TOCs; do not stream multi-GB GPU payloads during diagnosis.
            var sha = name.Contains(".stream") || name.Contains(".gpu_resources")
                ? null : Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(file));
            files.Add(new { name, length = file.Length, tocSha256 = sha });
        }
        return new { files, deployedUnitIds = units.Order().Select(u => u.ToString("x16")).ToArray(),
            resourceInstantiationProven = false, gpuPayloadsHashed = false };
    }

    public static async Task CollectEventsAsync(string directory, string stage, DateTimeOffset since)
    {
        // Use Windows' built-in PowerShell 5.1, no user script/module dependency.
        // The only interpolated parameter is a generated invariant ISO timestamp.
        var script = """
            $ErrorActionPreference='Stop'
            [Console]::OutputEncoding=New-Object System.Text.UTF8Encoding($false)
            $since=[datetime]::Parse('__SINCE__').ToLocalTime()
            $errs=@()
            $ev=@(Get-WinEvent -FilterHashtable @{LogName='Application';Id=@(1000,1001,1002);StartTime=$since} -MaxEvents 512 -ErrorAction SilentlyContinue -ErrorVariable errs)
            if(@($errs|Where-Object {$_.FullyQualifiedErrorId -notlike 'NoMatchingEventsFound*'}).Count){throw 'event_read_failed'}
            $rows=@(foreach($e in $ev){
              [xml]$x=$e.ToXml();$d=@{};foreach($f in $x.Event.EventData.Data){$d[$f.Name]=$f.'#text'}
              if($d.AppName -eq 'helldivers2.exe' -or $d.P1 -eq 'helldivers2.exe' -or $d.AppPath -match '\\helldivers2\.exe$'){
                $row=@{utc=$e.TimeCreated.ToUniversalTime().ToString('o');eventId=$e.Id;recordId=$e.RecordId}
                foreach($key in @('ProcessId','ProcessCreationTime','ExceptionCode','FaultingOffset','AppVersion','ModuleVersion')){
                  if($d[$key] -match '^[a-fA-F0-9xX.]{1,64}$'){$row[$key]=$d[$key]}
                }
                if($d.ModuleName -match '^[a-zA-Z0-9_.-]{1,80}$'){$row.module=$d.ModuleName}
                foreach($key in @('ReportId','IntegratorReportId')){
                  if($d[$key] -match '^[a-fA-F0-9-]{36}$'){$row[$key]=$d[$key]}
                }
                $row
              }
            })
            @{state='complete';capped=($ev.Count -eq 512);events=$rows;absenceDoesNotExcludeCrash=$true}|ConvertTo-Json -Depth 5 -Compress
            """.Replace("__SINCE__", since.UtcDateTime.ToString("o", CultureInfo.InvariantCulture));
        try
        {
            var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                @"WindowsPowerShell\v1.0\powershell.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8 };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand",
                Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }) info.ArgumentList.Add(arg);
            using var process = Process.Start(info)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { process.Kill(); throw; }
            var text = await output;
            await errors;
            if (process.ExitCode != 0 || text.Length > 1024 * 1024) throw new InvalidDataException();
            using var parsed = JsonDocument.Parse(text);
            Save(directory, $"events-{stage}.json", parsed.RootElement);
        }
        catch { Save(directory, $"events-{stage}.json", new { state = "unavailable", absenceDoesNotExcludeCrash = true }); }
    }

    public static async Task SampleStatusAsync(string directory, string stage, string registryRoot, CancellationToken stop,
        int intervalMs = 2000, int maxSamples = 1800)
    {
        // File/process metadata only. No process-memory access, injection or
        // active Lua request; sampling remains useful while ReadLine waits.
        using var output = new FileStream(Path.Combine(directory, $"samples-{stage}.jsonl"),
            FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        for (var i = 0; i < maxSamples && !stop.IsCancellationRequested; i++)
        {
            object row;
            try
            {
                var games = Process.GetProcessesByName("helldivers2");
                try
                {
                    var processes = games.Take(4).Select(g => new
                    {
                        pid = g.Id, startUtc = g.StartTime.ToUniversalTime(), workingSetBytes = g.WorkingSet64,
                        privateMemoryBytes = g.PrivateMemorySize64
                    }).ToArray();
                    row = new { utc = DateTimeOffset.UtcNow, processes,
                        runtime = SafeStatus(ReadStatus(registryRoot)), atomic = false };
                }
                finally { foreach (var game in games) game.Dispose(); }
            }
            catch { row = new { utc = DateTimeOffset.UtcNow, state = "sample_unavailable" }; }
            output.Write(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(row) + "\n"));
            // The game can crash without killing this tool. Durable markers
            // are flushed separately; batch high-frequency samples to avoid
            // forcing a disk sync on every 100 ms tick.
            if (intervalMs >= 1000 || i % 10 == 0) output.Flush(true);
            try { await Task.Delay(intervalMs, stop); }
            catch (OperationCanceledException) { break; }
        }
        output.Flush(true);
    }

    public static long CompositionLogOffset(string registryRoot)
    {
        var path = Path.Combine(Path.GetDirectoryName(registryRoot)!, "Logs", "ArmorRegistryRuntime.log");
        return File.Exists(path) ? new FileInfo(path).Length : 0;
    }

    public static void CaptureCompositionLog(string directory, string stage, string registryRoot, long offset)
    {
        var path = Path.Combine(Path.GetDirectoryName(registryRoot)!, "Logs", "ArmorRegistryRuntime.log");
        if (!File.Exists(path)) return;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length < offset || stream.Length - offset > 1024 * 1024) return;
        stream.Position = offset;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var text = reader.ReadToEnd();
        var matches = Regex.Matches(text,
            @"composition mode=existing_only replaced=(\d+) skipped_source=(\d+)");
        Save(directory, $"composition-{stage}.json", new
        {
            schema = 1, mode = stage,
            reports = matches.Cast<Match>().Take(10).Select(match => new
            {
                replaced = ulong.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                skippedSource = ulong.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture)
            }).ToArray()
        });
    }

    // Explicit allowlist: backups, journal, full logs and workspace never enter ZIP.
    public static string Export(string directory)
    {
        var zipPath = directory + "-report-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6] + ".zip";
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var name in new[] { "summary.json", "timeline.jsonl", "protocol-summary.json", "inventory.json",
            "environment.json", "events-original.json", "events-disabled.json", "events-quick.json",
            "events-matrix.json", "events-memory.json",
            "events-composition.json",
            "composition-full.json", "composition-existing_only.json",
            "samples-original.jsonl", "samples-disabled.jsonl", "samples-quick.jsonl",
            "samples-matrix.jsonl", "samples-memory.jsonl", "memory-clean.json",
            "samples-composition.jsonl",
            "recovery.json", "catalog-summary.json" })
        {
            var path = Path.Combine(directory, name);
            var limit = name is "samples-quick.jsonl" or "samples-matrix.jsonl" or "samples-composition.jsonl"
                or "samples-memory.jsonl"
                ? 16 * 1024 * 1024 : 4 * 1024 * 1024;
            if (File.Exists(path) && new FileInfo(path).Length <= limit) zip.CreateEntryFromFile(path, name);
        }
        return zipPath;
    }
}
