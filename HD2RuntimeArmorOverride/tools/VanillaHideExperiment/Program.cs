using System.Security.Cryptography;
using System.Text.Json;
using HD2ModAdaptation.PatchReconstruction;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModAdaptation.PatchReconstruction.UnitMesh.Canonical;
using Microsoft.Data.Sqlite;

if (args.Length != 4 || args[0] is not ("scan" or "build"))
{
    Console.Error.WriteLine("Usage: VanillaHideExperiment scan|build <index.sqlite> <game-data> <experiment-directory>");
    return 2;
}

var mode = args[0];
var index = Path.GetFullPath(args[1]);
var gameData = Path.GetFullPath(args[2]);
var experiment = Path.GetFullPath(args[3]);
const string carrier = "9ba626afa44a3aa3";
const string patchName = carrier + ".patch_5";
const ulong unitType = PatchUnitMeshReader.UnitTypeId;
if (!File.Exists(index) || !Directory.Exists(gameData)) throw new FileNotFoundException("Index or game data missing.");
if (Directory.GetFiles(gameData, carrier + ".patch_*").Any(path =>
        Path.GetFileName(path).StartsWith(patchName, StringComparison.OrdinalIgnoreCase)))
    throw new IOException("The reserved patch_5 slot is already occupied.");

var running = System.Diagnostics.Process.GetProcesses().Any(p =>
{
    try { return p.ProcessName.Equals("helldivers2", StringComparison.OrdinalIgnoreCase) ||
                 p.ProcessName.Equals("HD2ModManager", StringComparison.OrdinalIgnoreCase); }
    finally { p.Dispose(); }
});
if (running) throw new InvalidOperationException("Close game and manager before scanning or building.");

var scanner = new PatchTocScanner();
var deployed = Directory.GetFiles(gameData, "*.patch_*")
    .Where(path => !path.EndsWith(".stream", StringComparison.OrdinalIgnoreCase) &&
                   !path.EndsWith(".gpu_resources", StringComparison.OrdinalIgnoreCase))
    .OrderBy(path => path).ToArray();
var occupied = new HashSet<AssetKey>();
var deployedManifest = new List<object>();
foreach (var path in deployed)
{
    var entries = await scanner.ScanEntriesAsync(path);
    occupied.UnionWith(entries.Select(entry => entry.AssetKey));
    deployedManifest.Add(new
    {
        name = Path.GetFileName(path),
        target = new FileInfo(path).LinkTarget,
        sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(),
        entries = entries.Count
    });
}

var candidates = new Dictionary<AssetKey, string>();
var categoryCounts = new Dictionary<string, int>();
await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
{
    DataSource = index, Mode = SqliteOpenMode.ReadOnly
}.ToString()))
{
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = """
        SELECT a.archive_id, a.category, e.type_id, e.file_id
        FROM archives a JOIN archive_entries e ON e.archive_id=a.archive_id
        WHERE a.status='Indexed'
          AND (lower(a.category) LIKE '%armor%' OR lower(a.category) LIKE '%helmet%')
          AND e.type_id=$unitType
        ORDER BY a.archive_id,e.file_id
        """;
    command.Parameters.AddWithValue("$unitType", unchecked((long)unitType));
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        var key = new AssetKey(unchecked((ulong)reader.GetInt64(2)), unchecked((ulong)reader.GetInt64(3)));
        var category = reader.GetString(1);
        categoryCounts[category] = categoryCounts.GetValueOrDefault(category) + 1;
        candidates.TryAdd(key, reader.GetString(0));
    }
}

var selected = candidates.Where(pair => !occupied.Contains(pair.Key))
    .OrderBy(pair => pair.Value).ThenBy(pair => pair.Key.FileId).ToArray();
Directory.CreateDirectory(experiment);
var manifestPath = Path.Combine(experiment, "B-hide-manifest.json");
var manifest = new
{
    schema = 1, utc = DateTimeOffset.UtcNow, mode, carrier, patchName,
    gameExeSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(gameData)!, "bin", "helldivers2.exe")))).ToLowerInvariant(),
    index = index, gameData = gameData, categoryCounts,
    uniqueOriginalUnits = candidates.Count,
    excludedExistingPatchUnits = candidates.Keys.Count(occupied.Contains),
    selectedUnits = selected.Length,
    deployed = deployedManifest,
    units = selected.Select(pair => new { archive = pair.Value, type = pair.Key.TypeId.ToString("x16"), file = pair.Key.FileId.ToString("x16") }).ToArray()
};
await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Original Unit keys: {candidates.Count}; excluded by deployed patches: {candidates.Keys.Count(occupied.Contains)}; B selected: {selected.Length}");
Console.WriteLine($"Manifest: {manifestPath}");
if (mode == "scan") return 0;
if (selected.Length == 0) throw new InvalidOperationException("No eligible units.");

var resolver = new GameDataPackageResolver(gameData);
var unitReader = new GameDataUnitMeshReader(resolver);
var transforms = await new CanonicalAvatarRigReader(resolver).ReadTransformInfoAsync();
var builder = new CanonicalHiddenUnitBuilder();
var session = new CanonicalPatchSession();
var failed = new List<string>();
for (var i = 0; i < selected.Length; i++)
{
    var (key, archive) = selected[i];
    try
    {
        var unit = await unitReader.ReadAsync(archive, key, allowGlobalDependencySearch: true);
        var hidden = builder.Build(unit, transforms);
        session.AddEntry(hidden.Entry);
    }
    catch (Exception error) when (error is InvalidDataException or IOException or InvalidOperationException or KeyNotFoundException or EndOfStreamException)
    {
        failed.Add($"{archive}/{key.FileId:x16}: {error.Message}");
    }
    if ((i + 1) % 50 == 0) Console.WriteLine($"Built {i + 1}/{selected.Length}, failed {failed.Count}");
}
await File.WriteAllLinesAsync(Path.Combine(experiment, "B-build-failures.txt"), failed);
if (failed.Count != 0) throw new InvalidDataException($"{failed.Count} Units failed; output withheld.");
var validation = session.Finalize(CanonicalDependencyClosureValidation.Valid);
if (!validation.IsValid) throw new InvalidDataException(string.Join("; ", validation.Diagnostics.Select(d => d.Message)));
var output = Path.Combine(experiment, "patch");
await new CanonicalPatchWriter().WriteAsync(session, output, patchName);
var builtEntries = await scanner.ScanEntriesAsync(Path.Combine(output, patchName));
if (builtEntries.Count != selected.Length || !builtEntries.Select(entry => entry.AssetKey).ToHashSet().SetEquals(selected.Select(pair => pair.Key)))
    throw new InvalidDataException("Written TOC does not match requested Unit keys.");
Console.WriteLine($"Validated {builtEntries.Count} hidden Units: {output}");
return 0;
