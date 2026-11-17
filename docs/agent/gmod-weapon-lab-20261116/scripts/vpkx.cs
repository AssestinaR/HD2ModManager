#:project D:/gitCode/HD2ModManager/HD2SourceModel/HD2SourceModel.csproj
// LAB ONLY (V5): list / extract files from a Source VPK using the repo's reader.
using HD2SourceModel;
using var vpk = VpkArchive.Open(args[0]);
var pattern = args[1]; var outDir = args.Length > 2 ? args[2] : null;
foreach (var e in vpk.Entries.Where(e => e.Path.Contains(pattern, StringComparison.OrdinalIgnoreCase)).OrderBy(e => e.Path))
{
    Console.WriteLine($"{e.TotalLength,9} {e.Path}");
    if (outDir is null) continue;
    var dst = Path.Combine(outDir, e.Path.Replace('/', Path.DirectorySeparatorChar));
    Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
    File.WriteAllBytes(dst, vpk.Read(e));
}
