using System.Globalization;
using System.Text;
using HD2ModCore.Infrastructure;
using HD2SourceMigration.VpkConversion;

// 导出游戏里「根材质」的编译载荷信息，用于与 Mod 自带载荷比对。
//   PayloadDump "<游戏 data 目录>" "<输出目录>" [最小字节] [最大字节]      → 导出载荷文件
//   PayloadDump --fp "<游戏 data 目录>" "<输出文件>"                      → 导出每个根材质的 DXBC 指纹清单
const ulong MaterialTypeId = 0xEAC0B497876ADEDF;

static IEnumerable<string> Fingerprints(byte[] payload)
{
	var seen = new HashSet<string>(StringComparer.Ordinal);
	for (var i = 0; i + 20 <= payload.Length; i++)
	{
		if (payload[i] != (byte)'D' || payload[i + 1] != (byte)'X' || payload[i + 2] != (byte)'B' || payload[i + 3] != (byte)'C')
			continue;
		var fp = Convert.ToHexString(payload.AsSpan(i + 4, 16));
		if (seen.Add(fp)) yield return fp;
	}
}

// 导出单个材质资产的 TocData（供"覆盖本体根材质"时原样复用）：
//   PayloadDump --toc "<游戏 data 目录>" <fileId hex> "<输出目录>"
var tocMode = args.Length > 0 && args[0] == "--toc";
if (tocMode)
{
	if (args.Length < 4)
	{
		Console.Error.WriteLine("用法: PayloadDump --toc \"<游戏 data 目录>\" <fileId hex> \"<输出目录>\"");
		return 2;
	}
	var tocGameData = args[1];
	var wanted = ulong.Parse(args[2].Replace("0x", ""), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
	var tocOut = args[3];
	Directory.CreateDirectory(tocOut);
	var tocService = new GameDataAssetCatalogService(tocGameData);
	var tocScan = await tocService.EnumerateAsync([MaterialTypeId]);
	var asset = tocScan.Assets.Where(item => item.FileId == wanted).OrderByDescending(item => item.TocDataSize).FirstOrDefault();
	if (asset is null)
	{
		Console.Error.WriteLine($"没找到材质 {wanted:x16}");
		return 1;
	}
	var tocBytes = await tocService.ReadPartAsync(asset, GameDataAssetPart.TocData);
	await File.WriteAllBytesAsync(Path.Combine(tocOut, $"{wanted:x16}.toc"), tocBytes ?? []);
	var gpuBytes = await tocService.ReadPartAsync(asset, GameDataAssetPart.GpuResources);
	await File.WriteAllBytesAsync(Path.Combine(tocOut, $"{wanted:x16}.gpu"), gpuBytes ?? []);
	Console.WriteLine($"toc={tocBytes?.Length ?? 0} gpu={gpuBytes?.Length ?? 0} archive={asset.ArchiveId} -> {tocOut}");
	return 0;
}

// 根材质"特性清单"：导出每个根材质的贴图槽名、DXBC 数量、载荷大小，供挑选可移植的着色器家族。
//   PayloadDump --inventory "<游戏 data 目录>" "<输出.tsv>"
var inventoryMode = args.Length > 0 && args[0] == "--inventory";
if (inventoryMode)
{
	if (args.Length < 3)
	{
		Console.Error.WriteLine("用法: PayloadDump --inventory \"<游戏 data 目录>\" \"<输出.tsv>\"");
		return 2;
	}
	var invService = new GameDataAssetCatalogService(args[1]);
	var invScan = await invService.EnumerateAsync([MaterialTypeId]);
	var byIdInv = invScan.Assets.GroupBy(a => a.FileId)
		.ToDictionary(g => g.Key, g => g.OrderByDescending(a => a.TocDataSize).First());
	var invLines = new List<string> { "fileId\tgpuSize\ttocSize\tundat2\ttextures\tdxbc\ttexNames" };
	foreach (var pair in byIdInv.OrderBy(p => p.Key))
	{
		byte[]? toc;
		try { toc = await invService.ReadPartAsync(pair.Value, GameDataAssetPart.TocData); }
		catch { continue; }
		if (toc is null || toc.Length < 136) continue;
		Hd2MaterialAsset material;
		try { material = Hd2MaterialAsset.Read(toc); }
		catch { continue; }
		if (material.ParentMaterialId != 0) continue;   // 只看根材质
		byte[]? gpu = null;
		try { gpu = await invService.ReadPartAsync(pair.Value, GameDataAssetPart.GpuResources); }
		catch { }
		var names = new SortedSet<string>(StringComparer.Ordinal);
		var dxbc = 0;
		if (gpu is { Length: > 0 })
		{
			var text = System.Text.Encoding.ASCII.GetString(gpu);
			foreach (System.Text.RegularExpressions.Match m in
				System.Text.RegularExpressions.Regex.Matches(text, "__tex_[a-z0-9_]+"))
				names.Add(m.Value.Substring(6));
			for (var i = 0; i + 4 <= gpu.Length; i++)
				if (gpu[i] == (byte)'D' && gpu[i + 1] == (byte)'X' && gpu[i + 2] == (byte)'B' && gpu[i + 3] == (byte)'C')
					dxbc++;
		}
		invLines.Add($"{pair.Key:x16}\t{pair.Value.GpuResourceSize}\t{toc.Length}\t{material.Undat2}\t{material.TextureKeys.Count}\t{dxbc}\t{string.Join(',', names)}");
	}
	await File.WriteAllLinesAsync(args[2], invLines, new UTF8Encoding(false));
	Console.WriteLine($"根材质清单 {invLines.Count - 1} 行 -> {args[2]}");
	return 0;
}

var fingerprintMode = args.Length > 0 && args[0] == "--fp";
if (args.Length < (fingerprintMode ? 3 : 3))
{
	Console.Error.WriteLine("用法: PayloadDump --fp \"<游戏 data 目录>\" \"<输出.tsv>\"");
	Console.Error.WriteLine("      PayloadDump \"<游戏 data 目录>\" \"<输出目录>\" [最小字节] [最大字节]");
	return 2;
}

var gameData = fingerprintMode ? args[1] : args[0];
var target = fingerprintMode ? args[2] : args[1];
var minSize = !fingerprintMode && args.Length > 2 ? long.Parse(args[2], CultureInfo.InvariantCulture) : 0L;
var maxSize = !fingerprintMode && args.Length > 3 ? long.Parse(args[3], CultureInfo.InvariantCulture) : long.MaxValue;
if (!fingerprintMode) Directory.CreateDirectory(target);

var service = new GameDataAssetCatalogService(gameData);
var scan = await service.EnumerateAsync([MaterialTypeId]);
Console.Error.WriteLine($"material 资产 {scan.Assets.Count} 个");

var byId = scan.Assets
	.GroupBy(asset => asset.FileId)
	.ToDictionary(group => group.Key, group => group.OrderByDescending(asset => asset.TocDataSize).First());

var lines = new List<string>();
var roots = 0;
var dumped = 0;
foreach (var pair in byId.OrderBy(item => item.Key))
{
	byte[]? toc;
	try
	{
		toc = await service.ReadPartAsync(pair.Value, GameDataAssetPart.TocData);
	}
	catch
	{
		continue;
	}
	if (toc is null || toc.Length < 136) continue;

	Hd2MaterialAsset material;
	try
	{
		material = Hd2MaterialAsset.Read(toc);
	}
	catch
	{
		continue;
	}
	if (material.ParentMaterialId != 0) continue;

	roots++;
	var gpuSize = pair.Value.GpuResourceSize;
	if (fingerprintMode)
	{
		byte[]? gpu = null;
		try
		{
			gpu = await service.ReadPartAsync(pair.Value, GameDataAssetPart.GpuResources);
		}
		catch (Exception exception) when (exception is IOException or InvalidDataException or EndOfStreamException)
		{
			// 忽略读不出来的条目
		}
		var fps = gpu is { Length: > 0 } ? string.Join(',', Fingerprints(gpu)) : "";
		lines.Add($"{pair.Key:x16}\t{gpuSize}\t{toc.Length}\t{material.Undat2}\t{material.TextureKeys.Count}\t{material.Variables.Count}\t{fps}");
		continue;
	}

	if (gpuSize < minSize || gpuSize > maxSize) continue;
	try
	{
		var gpu = await service.ReadPartAsync(pair.Value, GameDataAssetPart.GpuResources);
		if (gpu is { Length: > 0 })
		{
			await File.WriteAllBytesAsync(Path.Combine(target, $"{pair.Key:x16}.bin"), gpu);
			dumped++;
		}
	}
	catch (Exception exception) when (exception is IOException or InvalidDataException or EndOfStreamException)
	{
		// 忽略
	}
}

if (fingerprintMode)
{
	await File.WriteAllLinesAsync(target, lines, new UTF8Encoding(false));
	Console.WriteLine($"根材质 {roots} 个，指纹行 {lines.Count} 条 -> {target}");
}
else
{
	Console.WriteLine($"根材质 {roots} 个，导出载荷 {dumped} 个 -> {target}");
}
return 0;

