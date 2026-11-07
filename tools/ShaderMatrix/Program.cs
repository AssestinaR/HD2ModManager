using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using HD2ModCore.Infrastructure;
using HD2SourceMigration.VpkConversion;

// 扫描游戏数据里所有 material 资产，产出「根材质（= 引擎着色器变体）→ 槽位 / 变量」能力矩阵。
// 只读游戏包：不写任何游戏文件，也不依赖管理器的 SQLite 索引。
//
//   用法：ShaderMatrix "<游戏 data 目录>" [<shadervariables.txt>] [<输出.json>] [<输出.md>]
//
// 结论见 docs/hd2-shader-inventory.md。这里只说两件事：
//   * 材质资产的 `parent == 0` 那一层就是根材质，引擎按它的 id 绑定编译好的着色器变体；
//   * 同一份材质在多个归档里可能有副本（数据补丁会覆盖），所以按 TocData 最大的那份取。
const ulong MaterialTypeId = 0xEAC0B497876ADEDF;

if (args.Length == 0)
{
	Console.Error.WriteLine("用法: ShaderMatrix \"<游戏 data 目录>\" [<shadervariables.txt>] [<输出.json>] [<输出.md>]");
	return 2;
}

var gameData = args[0];
var hashListPath = args.Length > 1 ? args[1] : FindUpwards(Path.Combine("tools", "ref", "HD2SDK-CommunityEdition", "hashlists", "shadervariables.txt"))
	?? throw new FileNotFoundException("找不到 hashlists/shadervariables.txt，请显式传第二个参数。");
var outJson = args.Length > 2 ? args[2] : Path.Combine(AppContext.BaseDirectory, "shader-matrix.json");
var outMarkdown = args.Length > 3 ? args[3] : Path.Combine(AppContext.BaseDirectory, "shader-matrix.md");

static string? FindUpwards(string relative)
{
	foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
	{
		var directory = new DirectoryInfo(start);
		while (directory is not null)
		{
			var candidate = Path.Combine(directory.FullName, relative);
			if (File.Exists(candidate)) return candidate;
			directory = directory.Parent;
		}
	}
	return null;
}

var names = new Dictionary<uint, string>();
foreach (var line in File.ReadLines(hashListPath))
{
	var trimmed = line.Trim();
	if (trimmed.Length == 0) continue;
	var space = trimmed.LastIndexOf(' ');
	if (space <= 0 || space + 1 >= trimmed.Length) continue;
	var hashText = trimmed[(space + 1)..].Trim();
	if (hashText.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hashText = hashText[2..];
	if (!uint.TryParse(hashText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hash)) continue;
	names[hash] = trimmed[..space];
}

string NameOf(uint hash) => names.TryGetValue(hash, out var name) ? name : "0x" + hash.ToString("x8", CultureInfo.InvariantCulture);
string Hex(ulong value) => "0x" + value.ToString("x16", CultureInfo.InvariantCulture);

var stopwatch = Stopwatch.StartNew();
var service = new GameDataAssetCatalogService(gameData);
var scan = await service.EnumerateAsync([MaterialTypeId]);
Console.Error.WriteLine($"material 资产 {scan.Assets.Count} 个（{stopwatch.Elapsed.TotalSeconds:F1}s）");

// 同一 file id 可能在多个归档里各有一份（等级不同）：取 TocData 最大的那份。
var byFileId = scan.Assets
	.GroupBy(asset => asset.FileId)
	.ToDictionary(group => group.Key, group => group.OrderByDescending(asset => asset.TocDataSize).First());
Console.Error.WriteLine($"不同 material file id：{byFileId.Count}（含多归档副本）");

var childCounts = new Dictionary<ulong, int>();
var childSamples = new Dictionary<ulong, List<ulong>>();
var read = 0;
foreach (var asset in scan.Assets)
{
	var material = await TryReadAsync(service, asset);
	if (material is null) continue;
	read++;
	childCounts[material.ParentMaterialId] = childCounts.GetValueOrDefault(material.ParentMaterialId) + 1;
	if (!childSamples.TryGetValue(material.ParentMaterialId, out var list))
		childSamples[material.ParentMaterialId] = list = [];
	if (list.Count < 6) list.Add(asset.FileId);
}
Console.Error.WriteLine($"读取 {read} 个材质，父 id {childCounts.Count} 个（{stopwatch.Elapsed.TotalSeconds:F0}s）");

var rows = new List<Row>();
foreach (var pair in childCounts.OrderByDescending(item => item.Value))
{
	var parentId = pair.Key;
	string[] slots = [];
	string[] variables = [];
	var tocSize = 0L;
	var gpuSize = 0L;
	var isRootMaterial = false;
	var rootParentZero = false;
	var undat2 = 0UL;

	if (parentId != 0 && byFileId.TryGetValue(parentId, out var descriptor))
	{
		isRootMaterial = true;
		tocSize = descriptor.TocDataSize;
		gpuSize = descriptor.GpuResourceSize;
		var rootMaterial = await TryReadAsync(service, descriptor);
		if (rootMaterial is not null)
		{
			rootParentZero = rootMaterial.ParentMaterialId == 0;
			undat2 = rootMaterial.Undat2;
			slots = rootMaterial.TextureKeys.Select(NameOf).OrderBy(name => name, StringComparer.Ordinal).ToArray();
			variables = rootMaterial.Variables.Select(variable => NameOf(variable.Id)).OrderBy(name => name, StringComparer.Ordinal).ToArray();
			// 变量默认值也带上：判断某个特性能不能开，看默认值最快。
			variables = rootMaterial.Variables
				.Select(variable => NameOf(variable.Id) + "=" + string.Join("/", variable.Values.Select(v => v.ToString("0.###", CultureInfo.InvariantCulture))))
				.OrderBy(text => text, StringComparer.Ordinal)
				.ToArray();
		}
	}

	rows.Add(new Row
	{
		Parent = Hex(parentId),
		SdkName = Hd2MaterialAsset.KnownParentIds.TryGetValue(parentId, out var sdkName) ? sdkName : null,
		IsRootMaterial = isRootMaterial,
		RootParentZero = rootParentZero,
		Children = pair.Value,
		SlotCount = slots.Length,
		VarCount = variables.Length,
		Slots = slots,
		Vars = variables,
		Flags = Flags(slots, variables),
		TocSize = tocSize,
		GpuSize = gpuSize,
		Undat2 = "0x" + undat2.ToString("x", CultureInfo.InvariantCulture),
		Samples = childSamples[pair.Key].Select(Hex).ToArray(),
	});
}

Console.Error.WriteLine($"根材质 {rows.Count(row => row.IsRootMaterial)} / 父 id {rows.Count}（{stopwatch.Elapsed.TotalSeconds:F0}s）");

await File.WriteAllTextAsync(outJson, JsonSerializer.Serialize(new
{
	gameData,
	materialAssets = scan.Assets.Count,
	materialsRead = read,
	distinctParents = rows.Count,
	rootMaterials = rows.Count(row => row.IsRootMaterial),
	rows,
}, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), new UTF8Encoding(false));

var markdown = new StringBuilder();
markdown.AppendLine("# HD2 材质能力矩阵（根材质 = 引擎着色器变体）");
markdown.AppendLine();
markdown.AppendLine($"- 游戏数据：`{gameData}`");
markdown.AppendLine($"- material 资产：{scan.Assets.Count}；可读 {read}");
markdown.AppendLine($"- 不同父 id：{rows.Count}；其中本身是材质资产（根材质）：{rows.Count(row => row.IsRootMaterial)}");
markdown.AppendLine();
markdown.AppendLine("## 能力一览");
markdown.AppendLine();
markdown.AppendLine("| 根材质 | SDK名 | 用例数 | 槽 | 变量 | 能力 | gpu |");
markdown.AppendLine("|---|---|---|---|---|---|---|");
foreach (var row in rows)
{
	markdown.AppendLine($"| `{row.Parent}` | {row.SdkName ?? "—"} | {row.Children} | {row.SlotCount} | {row.VarCount} | {row.Flags} | {row.GpuSize / 1024} KB |");
}

markdown.AppendLine();
markdown.AppendLine("## 明细");
foreach (var row in rows)
{
	markdown.AppendLine();
	markdown.AppendLine($"### `{row.Parent}`{(!string.IsNullOrEmpty(row.SdkName) ? " — " + row.SdkName : "")}  用例 {row.Children} 个，toc {row.TocSize} B，gpu {row.GpuSize / 1024} KB，undat2 {row.Undat2}");
	markdown.AppendLine();
	if (row.Slots.Length > 0) markdown.AppendLine("- 槽位：" + string.Join("、", row.Slots));
	if (row.Vars.Length > 0) markdown.AppendLine("- 变量：" + string.Join("、", row.Vars));
	if (row.Samples.Length > 0) markdown.AppendLine("- 用例样本：" + string.Join("、", row.Samples.Select(sample => "`" + sample + "`")));
}

await File.WriteAllTextAsync(outMarkdown, markdown.ToString(), new UTF8Encoding(false));

Console.Error.WriteLine($"写出 {outJson} / {outMarkdown}");
foreach (var row in rows.Take(25))
	Console.WriteLine($"{row.Parent} n={row.Children,-5} slots={row.SlotCount,-3} vars={row.VarCount,-3} {row.SdkName ?? "",-12} {row.Flags}");

return 0;

static string Flags(string[] slots, string[] variables)
{
	var flags = new List<string>();
	var slotText = string.Join(" ", slots);
	var varText = string.Join(" ", variables);
	bool Has(string text, params string[] needles) => needles.Any(needle => text.Contains(needle, StringComparison.OrdinalIgnoreCase));

	if (Has(slotText, "Opacity", "Clip")) flags.Add("镂空槽");
	if (Has(slotText, "Metal")) flags.Add("金属槽");
	if (Has(varText, "Opacity")) flags.Add("不透明度");
	if (Has(varText, "Metal")) flags.Add("金属度");
	if (Has(varText, "Clearcoat")) flags.Add("清漆");
	if (Has(varText, "Subsurface")) flags.Add("次表面");
	if (Has(varText, "Emissive") || Has(slotText, "Emissive")) flags.Add("自发光");
	if (Has(slotText, "Lut") || Has(varText, "Palette")) flags.Add("调色板");
	if (Has(slotText, "Decal", "Pattern")) flags.Add("贴花/图案");
	if (Has(slotText, "MatCap", "Matcap") || Has(varText, "MatCap", "Matcap")) flags.Add("MatCap");
	if (Has(slotText, "Iridescen") || Has(varText, "Iridescen")) flags.Add("虹彩");
	if (Has(slotText, "Wound") || Has(varText, "Wound")) flags.Add("伤口");
	if (Has(slotText, "Weather") || Has(varText, "Weather")) flags.Add("天气侵蚀");
	if (Has(varText, "Ramp")) flags.Add("Ramp");
	if (Has(varText, "Aniso")) flags.Add("各向异性");
	if (Has(varText, "Fresnel", "Rim")) flags.Add("Fresnel/边缘光");
	if (Has(slotText, "Detail")) flags.Add("细节层");
	return string.Join(" ", flags);
}

static async ValueTask<Hd2MaterialAsset?> TryReadAsync(GameDataAssetCatalogService service, GameDataAssetDescriptor asset)
{
	try
	{
		var bytes = await service.ReadPartAsync(asset, GameDataAssetPart.TocData);
		if (bytes is null) return null;
		return Hd2MaterialAsset.Read(bytes);
	}
	catch (Exception exception) when (exception is IOException or InvalidDataException or EndOfStreamException or ArgumentOutOfRangeException or ArgumentException or OverflowException)
	{
		return null;
	}
}

internal sealed class Row
{
	public string Parent { get; set; } = "";
	public string? SdkName { get; set; }
	public bool IsRootMaterial { get; set; }
	public bool RootParentZero { get; set; }
	public int Children { get; set; }
	public int SlotCount { get; set; }
	public int VarCount { get; set; }
	public string[] Slots { get; set; } = [];
	public string[] Vars { get; set; } = [];
	public string Flags { get; set; } = "";
	public long TocSize { get; set; }
	public long GpuSize { get; set; }
	public string Undat2 { get; set; } = "";
	public string[] Samples { get; set; } = [];
}
