using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using HD2ModCore.Infrastructure.KitInstance;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace HD2ModCore.Tests;

// 转专属 P3：用本机真实数据做端到端核对（原始爱弥斯 → FS-05 民主官 124623533；玩家神射手 3970674111 的 D9）。
// 本机没有这些文件（别的机器、CI）时直接返回，不算失败；有的话全部在临时目录里拷一份再跑，
// **不碰** Mod 库和 bin\ 下的索引（索引库与材质缓存也是先拷贝）。
public sealed class KitConversionRealDataTests : IDisposable
{
	private const string Library = @"E:\SteamLibrary\steamapps\common\HD2ModManager\mods";
	private const string IndexDirectory = @"D:\gitCode\HD2ModManager\HD2ModManager\bin\Release\net10.0-windows\data\indexes";
	private const string PatchName = "9ba626afa44a3aa3.patch_0";

	private readonly ITestOutputHelper _output;
	private readonly string _root = Path.Combine(Path.GetTempPath(), "hd2-kit-real-" + Guid.NewGuid().ToString("N"));

	public KitConversionRealDataTests(ITestOutputHelper output)
	{
		_output = output;
		Directory.CreateDirectory(_root);
	}

	public void Dispose()
	{
		SqliteConnection.ClearAllPools();
		try { Directory.Delete(_root, recursive: true); }
		catch (IOException) { }
		catch (UnauthorizedAccessException) { }
	}

	private static string Hex(ulong id) => id.ToString("x16");

	private static readonly string[] ExpectedHide =
	[
		"8d02a82d0c214171", "3bdfa900f32cd83f", "f2715c19b24ecd8b", "be65eae060a0267c", "210fb2f60f8308ea",
		"d19d17bc42ab9275", "99f001cb5a6e7116", "418b4fb786159bac", "74b2e79c1c5af767", "901fc9c35857dad2",
		"c4c7a5de567a87fe", "7e5111d05840be1a", "db487f9a3f738dc5", "324fb206612ce4f7", "64a71fbe6ef75b80",
		"e40aa3f8f6141a86", "d0007ea2f834da58", "e15680e4c4a89420", "effdfd2386aef629", "99a130858b3d8ff1",
	];

	private async Task<(KitPollutionService Service, KitConversionContext Context)?> LoadContextAsync()
	{
		var db = Path.Combine(IndexDirectory, "asset-index.sqlite");
		var cache = Path.Combine(IndexDirectory, "armor-unit-materials.json");
		if (!File.Exists(db) || !File.Exists(cache)) return null;
		var dbCopy = Path.Combine(_root, "asset-index.sqlite");
		var cacheCopy = Path.Combine(_root, "armor-unit-materials.json");
		File.Copy(db, dbCopy);
		File.Copy(cache, cacheCopy);
		var service = new KitPollutionService(dbCopy, cacheCopy, _ => throw new InvalidOperationException("测试里不该重建材质缓存。"));
		var context = await service.LoadConversionContextAsync(null);
		return context is null ? null : (service, context);
	}

	[Fact]
	public async Task OriginalAemeath_ConvertedToDemocracyOfficer_MatchesHandWrittenRecord()
	{
		var source = Path.Combine(Library, "爱弥斯民主官_2", PatchName);
		var hand = Path.Combine(Library, "爱弥斯民主官", PatchName);
		if (!File.Exists(source) || !File.Exists(hand)) return;
		var loaded = await LoadContextAsync();
		if (loaded is not { } data) return;
		Assert.NotNull(data.Context.UnitsByMaterial);

		var node = Path.Combine(_root, "node");
		Directory.CreateDirectory(node);
		foreach (var suffix in new[] { "", ".stream", ".gpu_resources" })
			File.Copy(source + suffix, Path.Combine(node, PatchName + suffix));
		var originalHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(node, PatchName))));

		var scan = await KitModContentScanner.ScanAsync(node);
		Assert.Single(scan.Content.Units);
		Assert.Equal(53, scan.Content.Materials.Count);
		Assert.Equal(153, scan.Content.Textures.Count);

		var preview = await data.Service.LoadConversionContextAsync(null);
		var candidates = KitConversionPlanner.PlanCandidates(
			item => $"kit-{item}-test", scan.Content, data.Context.IsVanilla, data.Context.Pieces, data.Context.KitNames, data.Context.UnitsByMaterial);
		foreach (var candidate in candidates) _output.WriteLine($"候选 {candidate.Label}: {(candidate.CanConvert ? "可转" : candidate.Plan.FailureReason)}");
		Assert.Contains(candidates, candidate => candidate.ItemId == 124623533 && candidate.CanConvert);

		var plan = KitConversionPlanner.Plan(124623533, "kit-124623533-test", scan.Content, data.Context.IsVanilla,
			data.Context.Pieces, data.Context.KitNames, data.Context.UnitsByMaterial);
		foreach (var line in KitConversionPlanner.DescribePreview(plan)) _output.WriteLine(line);
		Assert.True(plan.Succeeded, plan.FailureReason);

		_output.WriteLine("all=" + string.Join(",", plan.All) + " none=" + string.Join(",", plan.None) + " radius=" + plan.Radius);
		_output.WriteLine("hide(" + plan.Hide.Count + ")=" + string.Join(" ", plan.Hide.Select(entry => Hex(entry.Unit))));
		_output.WriteLine($"removedMaterials={plan.RemovedMaterials.Count} removedTextures={plan.RemovedTextures.Count} remaps m={plan.MaterialRemaps.Count} t={plan.TextureRemaps.Count} global u={plan.GlobalUnits.Count} t={plan.GlobalTextures.Count}");
		var hideNineOnly = plan.Hide.Where(entry => entry.Materials.Any(m => new[]
		{
			0xa781f50f508ed81bul, 0x9fa98c69807de2c8ul, 0x391170e79dbbaa0cul, 0x102255634838df73ul, 0x9ca177fec94daeb2ul,
			0xde9e8371fc355495ul, 0xe6046e3141d481d9ul, 0x93aa849d6d00109dul, 0x6135204a71fd3f47ul,
		}.Contains(m))).Select(entry => Hex(entry.Unit)).ToArray();
		_output.WriteLine("仅由那 9 个材质产生的隐藏部件数：" + hideNineOnly.Length);

		Assert.Equal(["82595b5d94c57343"], plan.All);
		Assert.Empty(plan.None);
		Assert.Equal(0.3, plan.Radius);
		Assert.Equal(ExpectedHide.Order(StringComparer.Ordinal), plan.Hide.Select(entry => Hex(entry.Unit)).Order(StringComparer.Ordinal));

		// 写出派生 patch，与手工版逐条比对条目 ID。
		var written = await KitDerivedPatchWriter.WriteAsync(node, scan, plan);
		_output.WriteLine("written=" + string.Join(",", written));
		var derived = await new HD2ModAdaptation.PatchReconstruction.PatchTocScanner().ScanEntriesAsync(Path.Combine(node, "Overwrite", PatchName));
		var handEntries = await new HD2ModAdaptation.PatchReconstruction.PatchTocScanner().ScanEntriesAsync(hand);
		string KeyOf(HD2ModAdaptation.PatchReconstruction.PatchTocEntry entry) => $"{entry.AssetKey.TypeId:x16}/{entry.AssetKey.FileId:x16}";
		var derivedKeys = derived.Select(KeyOf).Order(StringComparer.Ordinal).ToArray();
		var handKeys = handEntries.Select(KeyOf).Order(StringComparer.Ordinal).ToArray();
		_output.WriteLine($"derived entries={derived.Count} units={derived.Count(e => e.AssetKey.TypeId == KitPollutionAnalyzer.UnitTypeId)} materials={derived.Count(e => e.AssetKey.TypeId == KitPollutionAnalyzer.MaterialTypeId)} textures={derived.Count(e => e.AssetKey.TypeId == KitPollutionAnalyzer.TextureTypeId)}");
		Assert.Equal(handKeys, derivedKeys);

		// 原文件一个字节都没动。
		Assert.Equal(originalHash, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(node, PatchName)))));
		Assert.Equal(new FileInfo(source).Length, new FileInfo(Path.Combine(node, PatchName)).Length);

		// Unit 材质表与手工版一致（其余字节也比一比）。
		var derivedToc = File.ReadAllBytes(Path.Combine(node, "Overwrite", PatchName));
		var handToc = File.ReadAllBytes(hand);
		var derivedUnit = derived.Single(e => e.AssetKey.TypeId == KitPollutionAnalyzer.UnitTypeId);
		var handUnit = handEntries.Single(e => e.AssetKey.TypeId == KitPollutionAnalyzer.UnitTypeId);
		Assert.Equal(
			handToc.AsSpan((int)handUnit.TocDataOffset, (int)handUnit.TocDataSize).ToArray(),
			derivedToc.AsSpan((int)derivedUnit.TocDataOffset, (int)derivedUnit.TocDataSize).ToArray());

		// 撤销：只删自己写的。
		var removed = KitDerivedPatchWriter.Remove(node, written);
		Assert.False(Directory.Exists(Path.Combine(node, "Overwrite")));
		Assert.Equal(written.Count, removed.Count);
	}

	[Fact]
	public async Task PlayerSharpshooter_D9_ReportsCarrierNoneAndNamedKits()
	{
		var loaded = await LoadContextAsync();
		if (loaded is not { } data) return;
		const uint player = 3970674111;
		var leftShoulder = data.Context.Pieces
			.Where(piece => piece.ItemId == player && piece.Slot == 8)
			.Select(piece => piece.UnitFileId).Distinct().Order().ToArray();
		_output.WriteLine("玩家神射手 slot8 部件：" + string.Join(",", leftShoulder.Select(Hex)));

		// 合成输入：一个 FS-05 原版 unit（左肩）+ 一个绑定在玩家神射手别的部件上的原版材质（让隐藏集不为空）。
		var targetUnits = data.Context.Pieces.Where(piece => piece.ItemId == player && piece.Slot != 1).Select(piece => piece.UnitFileId).ToHashSet();
		var material = data.Context.UnitsByMaterial!
			.Where(pair => pair.Value.Any(unit => targetUnits.Contains(unit) && !leftShoulder.Contains(unit))
				&& data.Context.IsVanilla(new AssetKey(KitPollutionAnalyzer.MaterialTypeId, pair.Key)))
			.Select(pair => pair.Key).Order().First();
		var mod = new KitConversionModContent([leftShoulder[0]], [material], [],
			new Dictionary<ulong, IReadOnlyList<ulong>>(), new Dictionary<ulong, IReadOnlyList<ulong>>());
		var plan = KitConversionPlanner.Plan(player, "kit-test", mod, data.Context.IsVanilla, data.Context.Pieces, data.Context.KitNames, data.Context.UnitsByMaterial);
		foreach (var line in KitConversionPlanner.DescribePreview(plan)) _output.WriteLine(line);
		_output.WriteLine("succeeded=" + plan.Succeeded + " " + plan.FailureReason);
		_output.WriteLine("all=" + string.Join(",", plan.All) + " none=" + string.Join(",", plan.None) + " usesShared=" + plan.UsesSharedCarrier);
		_output.WriteLine("also=" + string.Join(",", plan.AlsoShowsModKits.Select(kit => kit.ItemId)));
		_output.WriteLine("hide=" + string.Join(",", plan.Hide.Select(entry => Hex(entry.Unit) + ":" + entry.Reason)));
	}
}
