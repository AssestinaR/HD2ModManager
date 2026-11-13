using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using HD2GameItemAssets;
using HD2ModAdaptation.PatchReconstruction;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using HD2ModCore.Infrastructure.KitInstance;
using Microsoft.Data.Sqlite;
using AssetKey = HD2ModCore.Domain.AssetKey;
using PatchEntryPayloadReader = HD2ModAdaptation.PatchReconstruction.PatchEntryPayloadReader;
using PatchTocScanner = HD2ModAdaptation.PatchReconstruction.PatchTocScanner;

namespace HD2ModCore.Tests;

// 「按实例隐藏」P3：转专属规划器（纯函数）、派生 patch 写入器（改 ID 往返、Overwrite 冲突、撤销）、编排服务。
// 全部用合成数据；用真实爱弥斯的端到端核对在 KitConversionRealDataTests。
public sealed class KitConversionTests : IDisposable
{
	private const ulong Unit = KitPollutionAnalyzer.UnitTypeId;
	private const ulong Material = KitPollutionAnalyzer.MaterialTypeId;
	private const ulong Texture = KitPollutionAnalyzer.TextureTypeId;
	private const string PatchName = "9ba626afa44a3aa3.patch_0";

	private readonly string _root = Path.Combine(Path.GetTempPath(), "hd2-kit-convert-" + Guid.NewGuid().ToString("N"));

	public KitConversionTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		SqliteConnection.ClearAllPools();
		try { Directory.Delete(_root, recursive: true); }
		catch (IOException) { }
		catch (UnauthorizedAccessException) { }
	}

	private static string Hex(ulong id) => id.ToString("x16", CultureInfo.InvariantCulture);

	private static IReadOnlyDictionary<ulong, IReadOnlyCollection<ulong>> Bound(params (ulong Material, ulong[] Units)[] rows)
		=> rows.ToDictionary(row => row.Material, row => (IReadOnlyCollection<ulong>)row.Units);

	private static AssetKey K(ulong type, ulong id) => new(type, id);

	private static Func<AssetKey, bool> Vanilla(params AssetKey[] keys)
	{
		var set = keys.ToHashSet();
		return set.Contains;
	}

	private static KitConversionModContent Content(
		ulong[] units, ulong[] materials, ulong[] textures,
		Dictionary<ulong, ulong[]>? unitMaterials = null, Dictionary<ulong, ulong[]>? materialTextures = null)
		=> new(
			units, materials, textures,
			(unitMaterials ?? []).ToDictionary(pair => pair.Key, pair => (IReadOnlyList<ulong>)pair.Value),
			(materialTextures ?? []).ToDictionary(pair => pair.Key, pair => (IReadOnlyList<ulong>)pair.Value));

	private static KitPieceRef P(uint item, ulong unit, uint slot = 2, int body = 0, int variant = 0) => new(item, slot, unit, body, variant);

	private static readonly IReadOnlyDictionary<uint, string> Names = new Dictionary<uint, string>
	{
		[1] = "FS-05“神射手”",
		[2] = "FS-05“神射手”",
		[3] = "CM-17",
		[4] = "CE-64“掷弹兵”",
	};

	// ---- 规划器 ---------------------------------------------------------------------------

	[Fact]
	public void Plan_AemeathShape_KeepsCarrierDropsOverriddenMaterialsAndHidesBoundUnits()
	{
		// 套 1 有独有部件 0x101；0x201 / 0x202 与套 3 共用。Mod：覆盖 0x101，并全局换了两个原版材质，Unit 自己不用它们。
		var pieces = new[] { P(1, 0x101, 8), P(1, 0x201), P(1, 0x202), P(3, 0x201), P(3, 0x202), P(3, 0x999) };
		var plan = KitConversionPlanner.Plan(
			1, "kit-1-test",
			Content([0x101], [0x301, 0x302], [0x401, 0x402, 0x403], null,
				new() { [0x301] = [0x401], [0x302] = [0x402] }),
			Vanilla(K(Unit, 0x101), K(Unit, 0x201), K(Unit, 0x202), K(Material, 0x301), K(Material, 0x302)),
			pieces, Names,
			Bound((0x301, [0x201]), (0x302, [0x202, 0x999])));

		Assert.True(plan.Succeeded, plan.FailureReason);
		Assert.Equal([Hex(0x101)], plan.All);
		Assert.Empty(plan.None);
		Assert.Equal(0.3, plan.Radius);
		Assert.Equal([0x201ul, 0x202ul], plan.HideUnits);   // 0x999 不属于套 1，不隐藏
		Assert.Equal([0x301ul, 0x302ul], plan.RemovedMaterials);
		Assert.Empty(plan.MaterialRemaps);
		// 贴图：401/402 只被去掉的材质引用；403 是 Mod 自己新增、没有任何材质引用。
		Assert.Equal([0x401ul, 0x402ul, 0x403ul], plan.RemovedTextures);
		Assert.Equal([0x403ul], plan.OrphanTextures);
		Assert.Empty(plan.UnitMoves);
		Assert.False(plan.UsesSharedCarrier);
	}

	[Fact]
	public void Plan_SharedPieceMovesToFreeUniquePiece_PrefersSameSlot_AndRenamesMaterialsAndTextures()
	{
		// 套 1 的独有部件：0x111（slot 2）、0x112（slot 3）。Mod 覆盖的 0x211（slot 3）与套 3 共用 ⇒ 搬到 0x112，而不是 ID 更小的 0x111。
		var pieces = new[] { P(1, 0x111, 2), P(1, 0x112, 3), P(1, 0x211, 3), P(1, 0x212, 4), P(3, 0x211, 3), P(3, 0x212, 4) };
		var vanilla = Vanilla(
			K(Unit, 0x211), K(Material, 0x311), K(Texture, 0x411), K(Texture, 0x412));
		var plan = KitConversionPlanner.Plan(
			1, "kit-1-test",
			Content([0x211], [0x311], [0x411, 0x412], new() { [0x211] = [0x311] }, new() { [0x311] = [0x411, 0x412] }),
			vanilla, pieces, Names,
			Bound((0x311, [0x211, 0x212])));

		Assert.True(plan.Succeeded, plan.FailureReason);
		var move = Assert.Single(plan.UnitMoves);
		Assert.Equal(new KitUnitMove(0x211, 0x112, SameSlot: true, SameBody: true), move);
		Assert.Equal([Hex(0x112)], plan.All);
		Assert.Equal([0x211ul, 0x212ul], plan.HideUnits);
		Assert.Equal(KitHideReason.MovedAway, plan.Hide[0].Reason);
		Assert.Equal(KitHideReason.MaterialReplaced, plan.Hide[1].Reason);

		// 新 ID 由确定的公式给出：同一个 Mod 重复转换结果不变。
		var expectedMaterial = StingrayHash.Murmur64A($"hd2modmanager/kit/1/{Hex(0x311)}");
		Assert.Equal(expectedMaterial, plan.MaterialRemaps[0x311]);
		Assert.Equal(StingrayHash.Murmur64A($"hd2modmanager/kit/1/{Hex(0x411)}"), plan.TextureRemaps[0x411]);
		Assert.Equal(2, plan.TextureRemaps.Count);
		Assert.Empty(plan.RemovedMaterials);
		Assert.Empty(plan.GlobalTextures);
	}

	[Fact]
	public void Plan_NotEnoughFreeUniquePieces_FailsAsAWhole()
	{
		// 套 1 只有一个独有部件，Mod 却有两个要搬的共用部件。
		var pieces = new[] { P(1, 0x111, 2), P(1, 0x211, 3), P(1, 0x212, 4), P(3, 0x211, 3), P(3, 0x212, 4) };
		var plan = KitConversionPlanner.Plan(
			1, "kit-1-test", Content([0x211, 0x212], [], []),
			Vanilla(K(Unit, 0x211), K(Unit, 0x212)), pieces, Names, null);

		Assert.False(plan.Succeeded);
		Assert.Contains("不够", plan.FailureReason);
		Assert.Empty(plan.UnitMoves);   // 整体失败，不做半套
	}

	[Fact]
	public void Plan_TargetWithoutUniquePiecesAndWithoutSameNameRecord_Fails()
	{
		var pieces = new[] { P(4, 0x211), P(3, 0x211) };
		var plan = KitConversionPlanner.Plan(4, "kit-4-test", Content([0x211], [], []), Vanilla(K(Unit, 0x211)), pieces, Names, null);
		Assert.False(plan.Succeeded);
		Assert.Contains("同名记录", plan.FailureReason);
	}

	// D9：套 1 没有独有部件；同名的套 2 与它共用左肩（两个身形各一件）。
	private static readonly KitPieceRef[] D9Pieces =
	[
		P(1, 0x501, 8, 0, 2), P(1, 0x502, 8, 1, 1), P(1, 0x601, 2, 0, 2),
		P(2, 0x501, 8, 0, 2), P(2, 0x502, 8, 1, 1), P(2, 0x703, 3, 0, 2), P(2, 0x701, 3, 0, 2), P(2, 0x702, 3, 1, 1),
		P(2, 0x601, 2, 0, 2),
		P(3, 0x601, 2, 0, 2),   // 0x601 还被名字不同的套 3 共用 ⇒ 不能当承载件
	];

	[Fact]
	public void Plan_D9_UsesSharedShoulderAsCarrier_NoneIsFirstDifferingPieceOfEachSameNameBody()
	{
		var plan = KitConversionPlanner.Plan(
			1, "kit-1-test",
			Content([0x501, 0x502], [0x801], [], null, null),
			Vanilla(K(Unit, 0x501), K(Unit, 0x502), K(Material, 0x801)),
			D9Pieces, Names, Bound((0x801, [0x601])));

		Assert.True(plan.Succeeded, plan.FailureReason);
		Assert.True(plan.UsesSharedCarrier);
		Assert.Equal([Hex(0x501)], plan.All);
		// 身形 (0,2) 里套 2 的差异件有 0x701、0x703，取 ID 最小的 0x701；身形 (1,1) 取 0x702。
		Assert.Equal([Hex(0x701), Hex(0x702)], plan.None);
		Assert.Equal([2u], plan.AlsoShowsModKits.Select(kit => kit.ItemId));
		Assert.Equal([0x601ul], plan.HideUnits);
		var preview = string.Join("\n", KitConversionPlanner.DescribePreview(plan));
		Assert.Contains("同名记录", preview);
		Assert.Contains("2）", preview);   // 点名套 2（item_id 2）
	}

	[Fact]
	public void Plan_D9_SameNameBodyWithCarrierButNoDifferingPiece_Fails_ButBodiesWithoutCarrierDoNotMatter()
	{
		// 套 2 身形 (1,1) 带着承载件 0x502，但它的部件全在套 1 里 ⇒ 无法区分，失败。
		var broken = D9Pieces.Where(piece => piece != P(2, 0x702, 3, 1, 1)).ToArray();
		var fail = KitConversionPlanner.Plan(
			1, "kit-1-test", Content([0x501, 0x502], [0x801], []),
			Vanilla(K(Unit, 0x501), K(Unit, 0x502), K(Material, 0x801)), broken, Names, Bound((0x801, [0x601])));
		Assert.False(fail.Succeeded);
		Assert.Contains("1/1", fail.FailureReason);

		// 只覆盖 0x501（身形 (0,2) 的左肩）：套 2 的 (1,1) 身形里没有这个承载件，不需要差异件，转换成立。
		var ok = KitConversionPlanner.Plan(
			1, "kit-1-test", Content([0x501], [0x801], []),
			Vanilla(K(Unit, 0x501), K(Material, 0x801)), broken, Names, Bound((0x801, [0x601])));
		Assert.True(ok.Succeeded, ok.FailureReason);
		Assert.Equal([Hex(0x701)], ok.None);
	}

	[Fact]
	public void Plan_ModWithoutUnits_IsRejected_AndOnlyMaterialsNeverHideEverything()
	{
		var plan = KitConversionPlanner.Plan(
			1, "kit-1-test", Content([], [0x301], [0x401]),
			Vanilla(K(Material, 0x301)), [P(1, 0x101, 8)], Names, Bound((0x301, [0x101])));
		Assert.False(plan.Succeeded);
		Assert.Contains("纯材质", plan.FailureReason);
	}

	[Fact]
	public void Plan_NothingToHide_IsRejected()
	{
		var plan = KitConversionPlanner.Plan(
			1, "kit-1-test", Content([0x101], [], []),
			Vanilla(K(Unit, 0x101)), [P(1, 0x101, 8), P(1, 0x201)], Names, null);
		Assert.False(plan.Succeeded);
		Assert.Contains("没有需要隐藏", plan.FailureReason);
	}

	[Fact]
	public void Plan_VanillaMaterialsWithoutMaterialAnalysis_AsksToWait()
	{
		var plan = KitConversionPlanner.Plan(
			1, "kit-1-test", Content([0x101], [0x301], []),
			Vanilla(K(Unit, 0x101), K(Material, 0x301)), [P(1, 0x101, 8), P(1, 0x201)], Names, null);
		Assert.False(plan.Succeeded);
		Assert.Contains("材质影响分析", plan.FailureReason);
	}

	[Fact]
	public void Plan_NewIdCollidingWithAVanillaAsset_IsSalted()
	{
		var pieces = new[] { P(1, 0x111, 2), P(1, 0x112, 3), P(1, 0x201), P(3, 0x201) };
		var first = StingrayHash.Murmur64A($"hd2modmanager/kit/1/{Hex(0x311)}");
		var plan = KitConversionPlanner.Plan(
			1, "kit-1-test", Content([0x111], [0x311], [], new() { [0x111] = [0x311] }),
			Vanilla(K(Unit, 0x111), K(Unit, 0x201), K(Material, 0x311), K(Material, first)),
			pieces, Names, Bound((0x311, [0x201])));

		Assert.True(plan.Succeeded, plan.FailureReason);
		var chosen = plan.MaterialRemaps[0x311];
		Assert.NotEqual(first, chosen);
		Assert.Equal(StingrayHash.Murmur64A($"hd2modmanager/kit/1/{Hex(0x311)}/1"), chosen);
	}

	[Fact]
	public void Plan_TextureStillUsedByAKeptMaterial_StaysAndNonVanillaOrphansGo()
	{
		// 材质 0x311 被 Unit 引用（改名），贴图 0x411（原版）同时被 Mod 自己的材质 0x9A1 引用 ⇒ 新旧两个 ID 都输出，旧的仍全局生效。
		var pieces = new[] { P(1, 0x111, 2), P(1, 0x201), P(3, 0x201) };
		var plan = KitConversionPlanner.Plan(
			1, "kit-1-test",
			Content([0x111], [0x311, 0x9A1], [0x411], new() { [0x111] = [0x311, 0x9A1] },
				new() { [0x311] = [0x411], [0x9A1] = [0x411] }),
			Vanilla(K(Unit, 0x111), K(Unit, 0x201), K(Material, 0x311), K(Texture, 0x411)),
			pieces, Names, Bound((0x311, [0x201])));

		Assert.True(plan.Succeeded, plan.FailureReason);
		Assert.Equal([0x311ul], plan.MaterialRemaps.Keys);
		Assert.Equal([0x411ul], plan.TextureRemaps.Keys);
		Assert.Equal([0x411ul], plan.GlobalTextures);
		Assert.Empty(plan.RemovedTextures);
	}

	[Fact]
	public void PlanCandidates_ListsEveryKitThatOwnsAModUnit_WithReasonsForTheOnesThatCannotConvert()
	{
		// 0x211 同属套 1 与套 3。套 1 有空闲独有部件 0x111；套 3 没有独有部件、也没有同名记录。
		var pieces = new[] { P(1, 0x111, 3), P(1, 0x211, 3), P(1, 0x212), P(3, 0x211, 3), P(3, 0x212) };
		var candidates = KitConversionPlanner.PlanCandidates(
			item => $"kit-{item}-test",
			Content([0x211], [0x311], [], new() { [0x211] = [0x311] }, new()),
			Vanilla(K(Unit, 0x211), K(Material, 0x311)),
			pieces, Names, Bound((0x311, [0x211, 0x212])));

		Assert.Equal([1u, 3u], candidates.Select(candidate => candidate.ItemId).Order());
		Assert.True(candidates.Single(candidate => candidate.ItemId == 1).CanConvert);
		var blocked = candidates.Single(candidate => candidate.ItemId == 3);
		Assert.False(blocked.CanConvert);
		Assert.False(string.IsNullOrWhiteSpace(blocked.Plan.FailureReason));
		Assert.Equal(1u, candidates[0].ItemId);   // 能转的排前面
	}

	// ---- 合成 patch ----------------------------------------------------------------------

	private sealed record SynEntry(ulong Type, ulong Id, byte[] Toc, byte[]? Gpu = null);

	private static byte[] UnitToc(params ulong[] materials)
	{
		var data = new byte[0x80 + 4 + materials.Length * 12 + 16];
		BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x70), 0x80);
		BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x80), (uint)materials.Length);
		for (var i = 0; i < materials.Length; i++)
		{
			BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x84 + i * 4), (uint)(i + 1));
			BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(0x84 + materials.Length * 4 + i * 8), materials[i]);
		}

		return data;
	}

	private static byte[] MaterialToc(params ulong[] textures)
	{
		var data = new byte[136 + textures.Length * 12 + 8];
		BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(64), (uint)textures.Length);
		for (var i = 0; i < textures.Length; i++)
			BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(136 + textures.Length * 4 + i * 8), textures[i]);
		data[0] = 0x4D;
		return data;
	}

	private static byte[] TextureToc(byte marker) => Enumerable.Repeat(marker, 24).ToArray();

	/// <summary>写一个合成 patch（slim 布局）：TOC、可选的 gpu_resources（每条 16 字节，64 对齐）。</summary>
	private static string WritePatch(string directory, string name, params SynEntry[] entries)
	{
		Directory.CreateDirectory(directory);
		var types = entries.Select(entry => entry.Type).Distinct().ToArray();
		var entryOffset = 72 + types.Length * 32;
		var tocOffset = entryOffset + entries.Length * 80;
		var toc = new List<byte>();
		var gpu = new List<byte>();
		var table = new byte[entryOffset + entries.Length * 80];
		BinaryPrimitives.WriteUInt32LittleEndian(table, 4026531857);
		BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(4), (uint)types.Length);
		BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(8), (uint)entries.Length);
		for (var t = 0; t < types.Length; t++)
		{
			BinaryPrimitives.WriteUInt64LittleEndian(table.AsSpan(72 + t * 32 + 8), types[t]);
			BinaryPrimitives.WriteUInt64LittleEndian(table.AsSpan(72 + t * 32 + 16), (ulong)entries.Count(entry => entry.Type == types[t]));
			BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(72 + t * 32 + 24), 16);
			BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(72 + t * 32 + 28), 64);
		}

		for (var i = 0; i < entries.Length; i++)
		{
			var entry = entries[i];
			var at = entryOffset + i * 80;
			while ((tocOffset + toc.Count) % 16 != 0) toc.Add(0);
			var thisToc = tocOffset + toc.Count;
			toc.AddRange(entry.Toc);
			var gpuOffset = 0;
			if (entry.Gpu is { Length: > 0 })
			{
				while (gpu.Count % 64 != 0) gpu.Add(0);
				gpuOffset = gpu.Count;
				gpu.AddRange(entry.Gpu);
			}

			BinaryPrimitives.WriteUInt64LittleEndian(table.AsSpan(at), entry.Id);
			BinaryPrimitives.WriteUInt64LittleEndian(table.AsSpan(at + 8), entry.Type);
			BinaryPrimitives.WriteUInt64LittleEndian(table.AsSpan(at + 16), (ulong)thisToc);
			BinaryPrimitives.WriteUInt64LittleEndian(table.AsSpan(at + 32), (ulong)gpuOffset);
			BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(at + 56), (uint)entry.Toc.Length);
			BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(at + 64), (uint)(entry.Gpu?.Length ?? 0));
			BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(at + 76), (uint)(i + 1));
		}

		var path = Path.Combine(directory, name);
		File.WriteAllBytes(path, [.. table, .. toc]);
		if (gpu.Count > 0) File.WriteAllBytes(path + ".gpu_resources", gpu.ToArray());
		return path;
	}

	private static byte[] Gpu(byte marker) => Enumerable.Repeat(marker, 16).ToArray();

	// 一个 Mod：Unit 0x101 引用 材质 0x301（原版，改名）与 0x999（Mod 自己的）；
	// 0x301 引用 贴图 0x401（原版）；0x302（原版）引用 0x402，不被 Unit 引用 ⇒ 去掉；0x999 引用 0x403（Mod 自己的）。
	private static string WriteSampleMod(string nodeDirectory)
		=> WritePatch(nodeDirectory, PatchName,
			new SynEntry(Unit, 0x101, UnitToc(0x301, 0x999)),
			new SynEntry(Material, 0x301, MaterialToc(0x401)),
			new SynEntry(Material, 0x302, MaterialToc(0x402)),
			new SynEntry(Material, 0x999, MaterialToc(0x403)),
			new SynEntry(Texture, 0x401, TextureToc(0xA1), Gpu(0xA1)),
			new SynEntry(Texture, 0x402, TextureToc(0xA2), Gpu(0xA2)),
			new SynEntry(Texture, 0x403, TextureToc(0xA3), Gpu(0xA3)));

	private static readonly KitPieceRef[] SamplePieces = [P(1, 0x101, 8), P(1, 0x201), P(3, 0x201)];

	private static readonly Func<AssetKey, bool> SampleVanilla = Vanilla(
		K(Unit, 0x101), K(Unit, 0x201), K(Material, 0x301), K(Material, 0x302), K(Texture, 0x401));

	private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

	private static KitConversionPlan SamplePlan(KitModContentScan scan)
		=> KitConversionPlanner.Plan(1, "kit-1-test", scan.Content, SampleVanilla, SamplePieces, Names, Bound((0x302, [0x201])));

	// ---- 写入器 ---------------------------------------------------------------------------

	[Fact]
	public async Task Writer_RewritesIdsRoundTrip_DropsRemovedEntries_AndNeverTouchesTheSource()
	{
		var node = Path.Combine(_root, "node");
		var source = WriteSampleMod(node);
		var before = (Sha(source), Sha(source + ".gpu_resources"));
		var scan = await KitModContentScanner.ScanAsync(node);
		Assert.Equal([0x101ul], scan.Content.Units);
		var plan = SamplePlan(scan);
		Assert.True(plan.Succeeded, plan.FailureReason);
		var newMaterial = plan.MaterialRemaps[0x301];
		var newTexture = plan.TextureRemaps[0x401];

		var written = await KitDerivedPatchWriter.WriteAsync(node, scan, plan);

		Assert.Equal([PatchName, PatchName + ".gpu_resources"], written.Order(StringComparer.Ordinal));
		Assert.Equal(before, (Sha(source), Sha(source + ".gpu_resources")));   // 原 Mod 文件一个字节都没动
		Assert.DoesNotContain(Directory.EnumerateDirectories(node), directory => Path.GetFileName(directory).StartsWith(KitDerivedPatchWriter.StagePrefix));

		var derivedPath = Path.Combine(node, "Overwrite", PatchName);
		var derived = await new PatchTocScanner().ScanEntriesAsync(derivedPath);
		var keys = derived.Select(entry => (entry.AssetKey.TypeId, entry.AssetKey.FileId)).ToHashSet();
		Assert.Equal(
			new HashSet<(ulong, ulong)>
			{
				(Unit, 0x101), (Material, newMaterial), (Material, 0x999), (Texture, newTexture), (Texture, 0x403),
			},
			keys);   // 0x301 改名、0x302 去掉、0x401 改名、0x402 只被去掉的材质引用 ⇒ 去掉

		var toc = File.ReadAllBytes(derivedPath);
		byte[] TocOf(ulong type, ulong id)
		{
			var entry = derived.Single(item => item.AssetKey.TypeId == type && item.AssetKey.FileId == id);
			return toc.AsSpan((int)entry.TocDataOffset, (int)entry.TocDataSize).ToArray();
		}

		// Unit 材质表：0x301 → 新 ID，0x999 不变，其余字节不变。
		var unitToc = TocOf(Unit, 0x101);
		var expectedUnit = UnitToc(newMaterial, 0x999);
		Assert.Equal(expectedUnit, unitToc);
		// 材质里的贴图引用：0x401 → 新 ID。
		Assert.Equal(MaterialToc(newTexture), TocOf(Material, newMaterial));
		Assert.Equal(MaterialToc(0x403), TocOf(Material, 0x999));
		// 改名贴图的内容（TOC 与 gpu）跟着走。
		Assert.Equal(TextureToc(0xA1), TocOf(Texture, newTexture));
		var payload = await new PatchEntryPayloadReader().ReadPayloadAsync(derived.Single(item => item.AssetKey.FileId == newTexture));
		Assert.Equal(Gpu(0xA1), payload.GpuResourceData);
	}

	[Fact]
	public async Task Writer_RefusesWhenOverwriteAlreadyHasTheSameName_AndLeavesEverythingAlone()
	{
		var node = Path.Combine(_root, "node");
		var source = WriteSampleMod(node);
		var foreign = Path.Combine(node, "Overwrite", PatchName);
		Directory.CreateDirectory(Path.GetDirectoryName(foreign)!);
		File.WriteAllText(foreign, "别的功能的产物");
		var scan = await KitModContentScanner.ScanAsync(node);

		var error = await Assert.ThrowsAsync<InvalidOperationException>(() => KitDerivedPatchWriter.WriteAsync(node, scan, SamplePlan(scan)));

		Assert.Contains(PatchName, error.Message);
		Assert.Equal("别的功能的产物", File.ReadAllText(foreign));
		Assert.Equal([foreign], Directory.EnumerateFiles(Path.Combine(node, "Overwrite")));
		Assert.DoesNotContain(Directory.EnumerateDirectories(node), directory => Path.GetFileName(directory).StartsWith(KitDerivedPatchWriter.StagePrefix));
		Assert.True(File.Exists(source));
	}

	[Fact]
	public async Task Writer_FailureMidWay_LeavesNoHalfWrittenFiles()
	{
		var node = Path.Combine(_root, "node");
		WriteSampleMod(node);
		var scan = await KitModContentScanner.ScanAsync(node);
		var plan = SamplePlan(scan);
		// 贴图的 gpu 数据文件在写之前被删掉 ⇒ 读 payload 失败（FileNotFound 属于 IOException）。
		File.Delete(Path.Combine(node, PatchName + ".gpu_resources"));

		await Assert.ThrowsAnyAsync<IOException>(() => KitDerivedPatchWriter.WriteAsync(node, scan, plan));

		Assert.False(Directory.Exists(Path.Combine(node, "Overwrite")));
		Assert.Empty(Directory.EnumerateDirectories(node));
	}

	[Fact]
	public void Remove_DeletesOnlyTheListedFiles_AndTheDirectoryOnlyWhenEmpty()
	{
		var node = Path.Combine(_root, "node");
		var overwrite = Path.Combine(node, "Overwrite");
		Directory.CreateDirectory(overwrite);
		File.WriteAllText(Path.Combine(overwrite, PatchName), "x");
		File.WriteAllText(Path.Combine(overwrite, PatchName + ".gpu_resources"), "x");
		File.WriteAllText(Path.Combine(overwrite, "someone-else.patch_0"), "keep");
		File.WriteAllText(Path.Combine(node, "outside.txt"), "keep");

		var removed = KitDerivedPatchWriter.Remove(node, [PatchName, PatchName + ".gpu_resources", @"..\outside.txt", "missing.patch_0"]);

		Assert.Equal([PatchName, PatchName + ".gpu_resources"], removed);
		Assert.True(File.Exists(Path.Combine(overwrite, "someone-else.patch_0")));   // 别人的文件留着，目录也留着
		Assert.True(File.Exists(Path.Combine(node, "outside.txt")));

		File.Delete(Path.Combine(overwrite, "someone-else.patch_0"));
		KitDerivedPatchWriter.Remove(node, []);
		Assert.False(Directory.Exists(overwrite));
	}

	[Fact]
	public void Fingerprint_IgnoresOverwrite_ButChangesWhenTheBasePayloadChanges()
	{
		var node = Path.Combine(_root, "node");
		var source = WriteSampleMod(node);
		var before = KitInstanceContentFingerprint.Compute(node);

		Directory.CreateDirectory(Path.Combine(node, "Overwrite"));
		File.WriteAllText(Path.Combine(node, "Overwrite", PatchName), "派生");
		Assert.Equal(before, KitInstanceContentFingerprint.Compute(node));

		File.AppendAllText(source + ".gpu_resources", "更新");
		Assert.NotEqual(before, KitInstanceContentFingerprint.Compute(node));
	}

	[Fact]
	public void Compiler_RecordWithAStaleFingerprintDoesNotTakeEffect_AndWarns()
	{
		var node = new ModNodeId(Guid.NewGuid());
		var record = new KitInstanceWorkspaceRecord(1, "kit-1-test", 0.3, [Hex(0x101)], [], [Hex(0x201)], "old-fingerprint");
		var workspace = KitInstanceWorkspace.Empty.With(node, record);

		var stale = KitInstanceRulesCompiler.Compile([node], workspace, 0, _ => "new-fingerprint");
		Assert.Empty(stale.Document.Rules);
		Assert.Contains(stale.Issues, issue => issue.Code == "KitInstanceRuleStale" && issue.Severity == CoreIssueSeverity.Warning);

		var fresh = KitInstanceRulesCompiler.Compile([node], workspace, 0, _ => "old-fingerprint");
		Assert.Single(fresh.Document.Rules);
		Assert.Empty(fresh.Issues);
	}

	[Fact]
	public void WorkspaceRecord_RoundTripsTargetNameAndDerivedFiles()
	{
		var node = new ModNodeId(Guid.NewGuid());
		var workspace = KitInstanceWorkspace.Empty.With(node, new KitInstanceWorkspaceRecord(
			1, "kit-1-test", 0.3, [Hex(0x101)], [], [Hex(0x201)], "fp", "FS-05“神射手”", [PatchName]));

		var parsed = KitInstanceWorkspaceStore.Parse(KitInstanceWorkspaceStore.Serialize(workspace)).Records[node];

		Assert.Equal("FS-05“神射手”", parsed.TargetName);
		Assert.Equal([PatchName], parsed.DerivedFiles);
		Assert.Equal("fp", parsed.ContentFingerprint);
	}

	// ---- 编排服务（合成索引库）-----------------------------------------------------------

	private async Task<KitConversionService> CreateServiceAsync()
	{
		var db = Path.Combine(_root, "index.sqlite");
		using (var connection = new SqliteConnection($"Data Source={db};Pooling=False"))
		{
			connection.Open();
			Exec(connection, $"""
				CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
				CREATE TABLE assets (type_id INTEGER NOT NULL, file_id INTEGER NOT NULL, df INTEGER NOT NULL, PRIMARY KEY(type_id, file_id));
				CREATE TABLE asset_archives (type_id INTEGER NOT NULL, file_id INTEGER NOT NULL, archive_id TEXT NOT NULL, PRIMARY KEY(type_id, file_id, archive_id));
				CREATE TABLE game_data_authority_kits (item_id INTEGER PRIMARY KEY, dlc_id INTEGER NOT NULL, set_id INTEGER NOT NULL, kit_kind INTEGER NOT NULL, archive_id TEXT NOT NULL, display_name TEXT NOT NULL);
				CREATE TABLE game_data_authority_pieces (item_id INTEGER NOT NULL, slot INTEGER NOT NULL, unit_type_id INTEGER NOT NULL, unit_file_id INTEGER NOT NULL, raw_body_type INTEGER NOT NULL DEFAULT 0, body_variant INTEGER NOT NULL DEFAULT 0);
				INSERT INTO meta VALUES('source_fingerprint','fp'),('built_utc','t');
				INSERT INTO game_data_authority_kits VALUES (1,0,0,0,'x','FS-05'),(3,0,0,0,'x','CM-17');
				INSERT INTO game_data_authority_pieces (item_id, slot, unit_type_id, unit_file_id) VALUES
					(1,8,{unchecked((long)Unit)},{0x101}),(1,2,{unchecked((long)Unit)},{0x201}),(3,2,{unchecked((long)Unit)},{0x201});
				INSERT INTO assets VALUES
					({unchecked((long)Unit)},{0x101},0),({unchecked((long)Unit)},{0x201},0),
					({unchecked((long)Material)},{0x301},0),({unchecked((long)Material)},{0x302},0),({unchecked((long)Texture)},{0x401},0);
				""");
		}

		var cache = Path.Combine(_root, "armor-unit-materials.json");
		var key = await ArmorUnitMaterialCacheStore.ComputeIndexKeyAsync(db);
		await ArmorUnitMaterialCacheStore.WriteAsync(cache, key!, new ArmorUnitMaterialMap(
			Bound((0x302, [0x201])), 3, 0, 0, DateTimeOffset.UtcNow));
		return new KitConversionService(new KitPollutionService(db, cache, _ => throw new InvalidOperationException("不该重建")));
	}

	private static void Exec(SqliteConnection connection, string sql)
	{
		using var command = connection.CreateCommand();
		command.CommandText = sql;
		command.ExecuteNonQuery();
	}

	[Fact]
	public async Task Service_ApplyWritesRecordAndDerivedPatch_RefusesTwice_AndUndoRemovesOnlyItsOwnFiles()
	{
		var service = await CreateServiceAsync();
		var mods = Path.Combine(_root, "mods");
		var node = Path.Combine(mods, "mod-a");
		var source = WriteSampleMod(node);
		var before = (Sha(source), Sha(source + ".gpu_resources"));
		var nodeId = new ModNodeId(Guid.NewGuid());

		var preview = await service.PreviewAsync(nodeId, node, null);
		Assert.Null(preview.Unavailable);
		Assert.Equal([1u], preview.Candidates.Select(candidate => candidate.ItemId));   // 0x101 只属于套 1
		Assert.True(preview.Candidates[0].CanConvert);

		var applied = await service.ApplyAsync(nodeId, mods, node, 1, null);
		Assert.True(applied.Succeeded, applied.Message);
		var record = await KitConversionService.TryGetRecordAsync(nodeId, mods);
		Assert.NotNull(record);
		Assert.Equal(1u, record!.TargetItemId);
		Assert.Equal(KitConversionService.RuleNameFor(nodeId, 1), record.RuleName);
		Assert.Equal([Hex(0x101)], record.All);
		Assert.Equal([Hex(0x201)], record.Hide);
		Assert.Equal("FS-05", record.TargetName);
		Assert.Equal(KitInstanceContentFingerprint.Compute(node), record.ContentFingerprint);   // 指纹不含 Overwrite
		Assert.Equal([PatchName, PatchName + ".gpu_resources"], record.DerivedFiles!.Order(StringComparer.Ordinal));
		Assert.True(File.Exists(Path.Combine(node, "Overwrite", PatchName)));
		Assert.Equal(before, (Sha(source), Sha(source + ".gpu_resources")));

		var compiled = KitInstanceRulesCompiler.Compile([nodeId], await KitInstanceWorkspaceStore.ReadAsync(mods), 0,
			_ => KitInstanceContentFingerprint.Compute(node));
		Assert.Single(compiled.Document.Rules);

		var again = await service.ApplyAsync(nodeId, mods, node, 1, null);
		Assert.False(again.Succeeded);
		Assert.Contains("已经转过", again.Message);

		// 撤销前，别的功能往 Overwrite 里放了一个无关文件：撤销不动它。
		File.WriteAllText(Path.Combine(node, "Overwrite", "someone-else.patch_0"), "keep");
		var undone = await KitConversionService.UndoAsync(nodeId, mods, node);
		Assert.True(undone.Succeeded, undone.Message);
		Assert.Null(await KitConversionService.TryGetRecordAsync(nodeId, mods));
		Assert.False(File.Exists(Path.Combine(node, "Overwrite", PatchName)));
		Assert.True(File.Exists(Path.Combine(node, "Overwrite", "someone-else.patch_0")));
		Assert.Equal(before, (Sha(source), Sha(source + ".gpu_resources")));

		var nothing = await KitConversionService.UndoAsync(nodeId, mods, node);
		Assert.False(nothing.Succeeded);
	}

	[Fact]
	public async Task Service_ApplyOverAForeignOverwriteFileIsRefused_AndNoRecordIsWritten()
	{
		var service = await CreateServiceAsync();
		var mods = Path.Combine(_root, "mods");
		var node = Path.Combine(mods, "mod-b");
		WriteSampleMod(node);
		Directory.CreateDirectory(Path.Combine(node, "Overwrite"));
		File.WriteAllText(Path.Combine(node, "Overwrite", PatchName), "foreign");
		var nodeId = new ModNodeId(Guid.NewGuid());

		var result = await service.ApplyAsync(nodeId, mods, node, 1, null);

		Assert.False(result.Succeeded);
		Assert.Contains("Overwrite", result.Message);
		Assert.Null(await KitConversionService.TryGetRecordAsync(nodeId, mods));
		Assert.Equal("foreign", File.ReadAllText(Path.Combine(node, "Overwrite", PatchName)));
	}

	[Fact]
	public async Task Service_UndoOfAHandWrittenRecordOnlyRemovesTheRecord()
	{
		var mods = Path.Combine(_root, "mods");
		var node = Path.Combine(mods, "mod-c");
		Directory.CreateDirectory(Path.Combine(node, "Overwrite"));
		File.WriteAllText(Path.Combine(node, "Overwrite", PatchName), "用户自己的");
		var nodeId = new ModNodeId(Guid.NewGuid());
		await KitInstanceWorkspaceStore.WriteAsync(mods, KitInstanceWorkspace.Empty.With(nodeId,
			new KitInstanceWorkspaceRecord(124623533, "aemeath_democracy_officer", 0.3, [Hex(0x101)], [], [Hex(0x201)])));

		var undone = await KitConversionService.UndoAsync(nodeId, mods, node);

		Assert.True(undone.Succeeded, undone.Message);
		Assert.Null(await KitConversionService.TryGetRecordAsync(nodeId, mods));
		Assert.Equal("用户自己的", File.ReadAllText(Path.Combine(node, "Overwrite", PatchName)));   // 手写记录没有 DerivedFiles ⇒ 不删任何文件
	}
}
