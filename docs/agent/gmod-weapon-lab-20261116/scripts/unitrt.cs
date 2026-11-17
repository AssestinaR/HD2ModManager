#:project D:/gitCode/HD2ModManager/HD2ModAdaptation/HD2ModAdaptation.csproj
// LAB ONLY (V2): read a weapon unit with the repo's armor-oriented UnitMeshReader, write it back unchanged, compare bytes.
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
var toc = File.ReadAllBytes(args[0]); var gpu = File.ReadAllBytes(args[1]);
try
{
    var model = new UnitMeshReader().Read(toc, gpu);
    Console.WriteLine($"read OK: version={model.Version} boneInfos={model.BoneInfos.Count}");
    foreach (var p in model.GetType().GetProperties())
    {
        var v = p.GetValue(model);
        if (v is System.Collections.ICollection c) Console.WriteLine($"  {p.Name}: count={c.Count}");
    }
    var res = new UnitMeshWriter().Write(model, toc);
    int Diff(byte[] a, byte[] b) { for (int i = 0; i < Math.Min(a.Length, b.Length); i++) if (a[i] != b[i]) return i; return a.Length == b.Length ? -1 : Math.Min(a.Length, b.Length); }
    Console.WriteLine($"toc: {toc.Length}->{res.TocData.Length} firstDiff={Diff(toc, res.TocData)}");
    var back = new UnitMeshReader().Read(res.TocData, res.GpuData);
    string Sig(UnitMeshModel m) => string.Join("|", m.GetType().GetProperties().Select(p => { var v = p.GetValue(m); return p.Name + "=" + (v is System.Collections.IEnumerable e && v is not string ? string.Join(",", e.Cast<object>().Select(x => x?.ToString())) : v?.ToString()); }));
    var a = Sig(model).Split('|'); var b = Sig(back).Split('|');
    for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) Console.WriteLine($"  model field differs: {a[i][..Math.Min(140, a[i].Length)]}  ->  {b[i][..Math.Min(140, b[i].Length)]}");
    Console.WriteLine("  toc bytes 88..112 orig: " + Convert.ToHexString(toc, 88, 24));
    Console.WriteLine("                   new:  " + Convert.ToHexString(res.TocData, 88, 24));
    Console.WriteLine($"  header offsets orig: cust={model.CustomizationInfoOffset} bone={model.BoneInfoOffset} stream={model.StreamInfoOffset} mesh={model.MeshInfoOffset} mat={model.MaterialsOffset} end={model.EndingOffset}");
    Console.WriteLine($"  header offsets new:  cust={back.CustomizationInfoOffset} bone={back.BoneInfoOffset} stream={back.StreamInfoOffset} mesh={back.MeshInfoOffset} mat={back.MaterialsOffset} end={back.EndingOffset}");
    Console.WriteLine($"gpu: {gpu.Length}->{res.GpuData.Length} firstDiff={Diff(gpu, res.GpuData)}");
}
catch (Exception e) { Console.WriteLine($"FAILED: {e.GetType().Name}: {e.Message}"); }
