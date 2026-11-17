#:project D:/gitCode/HD2ModManager/Decryptor/Decryptor.csproj
// LAB ONLY: decrypt game dl_bin files and compare with filediver plaintext snapshots.
using System.Security.Cryptography;
var game = args[0]; var fd = args[1];
var dec = FileDecryptor.Decryptor.CreateWithEmbeddedKeys();
foreach (var name in new[] { "dl_library.dl_typelib", "generated_animation_event_trigger_settings.dl_bin", "generated_entities.dl_bin", "generated_entity_deltas.dl_bin" })
{
    var enc = File.ReadAllBytes(Path.Combine(game, name));
    var snap = File.ReadAllBytes(Path.Combine(fd, name));
    if (!dec.TryDecodeData(enc, out var plain)) { Console.WriteLine($"{name}: DECRYPT FAILED"); continue; }
    var same = plain.AsSpan().SequenceEqual(snap);
    Console.WriteLine($"{name}: plain={plain.Length} snap={snap.Length} identical={same} sha={Convert.ToHexString(SHA256.HashData(plain))[..16]}");
    File.WriteAllBytes(Path.Combine(args[2], name), plain);
}
