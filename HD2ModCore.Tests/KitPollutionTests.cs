using System.Buffers.Binary;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure.KitInstance;
using Microsoft.Data.Sqlite;

namespace HD2ModCore.Tests;

// 「按实例隐藏」P2：污染分析器（纯函数）、材质绑定缓存、污染服务。
// 全部用合成数据，不依赖本机的索引库或游戏目录。
public sealed class KitPollutionTests : IDisposable
{
	private const ulong Unit = KitPollutionAnalyzer.UnitTypeId;
	private const ulong Material = KitPollutionAnalyzer.MaterialTypeId;
	private const ulong Texture = KitPollutionAnalyzer.TextureTypeId;

	private readonly string _root = Path.Combine(Path.GetTempPath(), "hd2-kit-pollution-" + Guid.NewGuid().ToString("N"));

	public KitPollutionTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		SqliteConnection.ClearAllPools();
		try { Directory.Delete(_root, recursive: true); }
		catch (IOException) { }
		catch (UnauthorizedAccessException) { }
	}

	// ---- 分析器 --------------------------------------------------------------------------

	private static readonly IReadOnlyDictionary<uint, string> Names = new Dictionary<uint, string>
	{
		[1] = "FS-05“神射手”",
		[2] = "CM-17",
		[3] = "CE-64“掷弹兵”",
		[4] = "0x2cbae902",
		[5] = "FS-05“神射手”",
	};

	private static KitPieceRef Piece(uint item, ulong unit, uint slot = 2) => new(item, slot, unit);

	private static KitPollutionResult Analyze(
		IEnumerable<AssetKey> mod,
		IEnumerable<KitPieceRef> pieces,
		IReadOnlyDictionary<ulong, IReadOnlyCollection<ulong>>? materials = null,
		Func<AssetKey, bool>? vanilla = null)
		=> KitPollutionAnalyzer.Analyze(mod, vanilla ?? (_ => true), pieces, Names, materials);

	private static IReadOnlyDictionary<ulong, IReadOnlyCollection<ulong>> MaterialMap(params (ulong Material, ulong[] Units)[] rows)
		=> rows.ToDictionary(row => row.Material, row => (IReadOnlyCollection<ulong>)row.Units);

	[Fact]
	public void Analyzer_UnitOnly_NamesEveryKitThatSharesTheUnit()
	{
		var result = Analyze(
			[new(Unit, 0xA)],
			[Piece(1, 0xA), Piece(2, 0xA), Piece(3, 0xB)]);

		Assert.True(result.IsPolluted);
		Assert.Equal([1u, 2u], result.Kits.Select(kit => kit.ItemId).Order());   // 1 和 2，不含 3
		Assert.All(result.Kits, kit => Assert.Equal([0xAul], kit.Units));
		Assert.All(result.Kits, kit => Assert.Empty(kit.Materials));
		Assert.False(result.MaterialAnalysisAvailable);
	}

	[Fact]
	public void Analyzer_MaterialOnly_ReachesKitsThroughTheirUnits()
	{
		var result = Analyze(
			[new(Material, 0x10)],
			[Piece(1, 0xA), Piece(3, 0xB)],
			MaterialMap((0x10, [0xA, 0xB])));

		Assert.True(result.IsPolluted);
		Assert.True(result.MaterialAnalysisAvailable);
		Assert.Equal([1u, 3u], result.Kits.Select(kit => kit.ItemId).Order());
		Assert.All(result.Kits, kit => Assert.Equal([0x10ul], kit.Materials));
		Assert.All(result.Kits, kit => Assert.Empty(kit.Units));
	}

	[Fact]
	public void Analyzer_UnitAndMaterial_MergesReasonsPerKit()
	{
		var result = Analyze(
			[new(Unit, 0xA), new(Material, 0x10)],
			[Piece(1, 0xA), Piece(2, 0xA), Piece(3, 0xB)],
			MaterialMap((0x10, [0xB])));

		Assert.Equal([1u, 2u, 3u], result.Kits.Select(kit => kit.ItemId).Order());
		var first = result.Kits.Single(kit => kit.ItemId == 1);
		Assert.Equal([0xAul], first.Units);
		Assert.Empty(first.Materials);
		var third = result.Kits.Single(kit => kit.ItemId == 3);
		Assert.Empty(third.Units);
		Assert.Equal([0x10ul], third.Materials);
	}

	[Fact]
	public void Analyzer_OneKitIsNotPollution()
	{
		var result = Analyze([new(Unit, 0xA)], [Piece(1, 0xA), Piece(2, 0xB)]);
		Assert.Single(result.Kits);
		Assert.False(result.IsPolluted);
	}

	[Fact]
	public void Analyzer_NullMaterialMap_StillAnswersTheUnitPartAndSaysSo()
	{
		var result = Analyze(
			[new(Unit, 0xA), new(Material, 0x10)],
			[Piece(1, 0xA), Piece(2, 0xA), Piece(3, 0xB)],
			materials: null);

		Assert.False(result.MaterialAnalysisAvailable);
		Assert.Equal([1u, 2u], result.Kits.Select(kit => kit.ItemId).Order());
		Assert.True(result.IsPolluted);
	}

	[Fact]
	public void Analyzer_NonVanillaIdsAreIgnored()
	{
		var vanilla = new HashSet<AssetKey> { new(Unit, 0xA) };
		var result = Analyze(
			[new(Unit, 0xA), new(Unit, 0xB), new(Material, 0x10), new(Texture, 0x20)],
			[Piece(1, 0xA), Piece(2, 0xA), Piece(3, 0xB)],
			MaterialMap((0x10, [0xB])),
			vanilla.Contains);

		// 0xB / 0x10 / 0x20 不是原版：Mod 自己新增的，不污染任何人，也不计入原版贴图。
		Assert.Equal([1u, 2u], result.Kits.Select(kit => kit.ItemId).Order());
		Assert.Equal(0, result.VanillaTextureCount);
	}

	[Fact]
	public void Analyzer_CapesAndUnitZeroAreIgnored()
	{
		var result = Analyze(
			[new(Unit, 0xC), new(Unit, 0)],
			[Piece(1, 0xC, slot: KitPollutionAnalyzer.CapeSlot), Piece(2, 0xC, slot: KitPollutionAnalyzer.CapeSlot), Piece(1, 0), Piece(2, 0)]);

		Assert.Empty(result.Kits);
		Assert.False(result.IsPolluted);
	}

	[Fact]
	public void Analyzer_CountsVanillaTexturesWithoutAnalysingThem()
	{
		var result = Analyze([new(Texture, 1), new(Texture, 2), new(Texture, 2)], [Piece(1, 0xA)]);
		Assert.Equal(2, result.VanillaTextureCount);
		Assert.Empty(result.Kits);
	}

	[Fact]
	public void Analyzer_OrdersKitsStablyByNameThenId()
	{
		var result = Analyze([new(Unit, 0xA)], [Piece(3, 0xA), Piece(2, 0xA), Piece(1, 0xA)]);
		Assert.Equal(["CE-64“掷弹兵”", "CM-17", "FS-05“神射手”"], result.Kits.Select(kit => kit.Name));
	}

	[Fact]
	public void Analyzer_HashNamedKitsGoLastAndShowAsUnnamed()
	{
		// 「0x2cbae902」是没有本地化名的记录：按字符排序会排到最前，必须垫底。
		var result = Analyze([new(Unit, 0xA)], [Piece(4, 0xA), Piece(2, 0xA), Piece(6, 0xA)]);
		Assert.Equal([2u, 4u, 6u], result.Kits.Select(kit => kit.ItemId));
		Assert.Equal(["CM-17", "未命名护甲", "未命名护甲"], result.Kits.Select(kit => kit.Label));
		Assert.Equal("未命名护甲（4）", result.Kits[1].DetailedLabel);
	}

	[Fact]
	public void Analyzer_GroupsCausesByResourceMostKitsFirst()
	{
		var result = Analyze(
			[new(Unit, 0xA), new(Material, 0x10)],
			[Piece(1, 0xA), Piece(2, 0xB), Piece(3, 0xC)],
			MaterialMap((0x10, [0xA, 0xB, 0xC])));

		Assert.Equal(1, result.UnitCauseCount);
		Assert.Equal(1, result.MaterialCauseCount);
		Assert.Collection(result.CauseList,
			cause =>
			{
				Assert.Equal(KitPollutionCauseKind.Material, cause.Kind);
				Assert.Equal([3u, 2u, 1u], cause.Kits.Select(kit => kit.ItemId));
			},
			cause =>
			{
				Assert.Equal(KitPollutionCauseKind.Unit, cause.Kind);
				Assert.Equal([1u], cause.Kits.Select(kit => kit.ItemId));
			});
		Assert.Equal("1 个原版部件、1 个原版材质", KitPollutionAnalyzer.DescribeReplaced(result));
	}

	[Fact]
	public void DeploymentWarning_SaysTheCauseThenNamesAtMostTwoKits()
	{
		var three = Analyze([new(Unit, 0xA)], [Piece(1, 0xA), Piece(2, 0xA), Piece(3, 0xA)]);
		Assert.Equal(
			"『某 Mod』替换了 1 个原版部件，会连带改变 CE-64“掷弹兵”、CM-17 等 3 套护甲，可在详情页转专属",
			KitPollutionAnalyzer.FormatDeploymentWarning("某 Mod", three));

		var two = Analyze([new(Unit, 0xA)], [Piece(1, 0xA), Piece(2, 0xA)]);
		Assert.Equal(
			"『某 Mod』替换了 1 个原版部件，会连带改变 CM-17、FS-05“神射手” 共 2 套护甲，可在详情页转专属",
			KitPollutionAnalyzer.FormatDeploymentWarning("某 Mod", two));
	}

	[Fact]
	public void DeploymentWarning_SameNameRecordsAreNamedOnce()
	{
		// 玩家版与 NPC 版同名：只点一次名，第三个名字补上，且已覆盖全部时写「共」。
		var result = Analyze([new(Unit, 0xA)], [Piece(1, 0xA), Piece(5, 0xA), Piece(2, 0xA)]);
		Assert.Equal(
			"『某 Mod』替换了 1 个原版部件，会连带改变 CM-17、FS-05“神射手” 共 3 套护甲，可在详情页转专属",
			KitPollutionAnalyzer.FormatDeploymentWarning("某 Mod", result));
	}

	// ---- 缓存：构建 ----------------------------------------------------------------------

	private sealed class FakeTocReader(Func<string, ulong, byte[]?> read) : IArmorUnitTocReader
	{
		public int Calls;
		public ValueTask<byte[]?> ReadUnitTocAsync(string archiveId, ulong unitFileId, CancellationToken cancellationToken)
		{
			Interlocked.Increment(ref Calls);
			return ValueTask.FromResult(read(archiveId, unitFileId));
		}
	}

	/// <summary>最小的 Unit TOC：0x70 处的偏移指向 [count][section ids][material ids]。</summary>
	private static byte[] UnitToc(params ulong[] materials)
	{
		const int tableOffset = 0x80;
		var bytes = new byte[tableOffset + 4 + materials.Length * 12];
		BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x70), tableOffset);
		BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(tableOffset), (uint)materials.Length);
		for (var index = 0; index < materials.Length; index++)
		{
			BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(tableOffset + 4 + index * 4), (uint)index);
			BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(tableOffset + 4 + materials.Length * 4 + index * 8), materials[index]);
		}
		return bytes;
	}

	[Fact]
	public async Task Builder_SkipsFailingUnitsAndCountsThem()
	{
		var reader = new FakeTocReader((archive, unit) => unit switch
		{
			0xA => UnitToc(0x10, 0x11),
			0xB => UnitToc(0x10),
			0xC => throw new InvalidDataException("坏 TOC"),
			0xD => null,                                   // 归档里找不到
			0xE => archive == "bad" ? throw new IOException("读不了") : UnitToc(0x12),   // 第一个归档失败，第二个成功
			_ => UnitToc(),
		});
		var units = new Dictionary<ulong, IReadOnlyList<string>>
		{
			[0xA] = ["a1"], [0xB] = ["a1"], [0xC] = ["a2"], [0xD] = ["a2"], [0xE] = ["bad", "good"], [0xF] = [],
		};

		var map = await ArmorUnitMaterialCacheBuilder.BuildAsync(units, reader);

		Assert.Equal(6, map.UnitCount);
		Assert.Equal(3, map.FailedUnitCount);   // 0xC 抛异常、0xD 找不到、0xF 没有归档
		Assert.Equal([0xAul, 0xBul], map.UnitsByMaterial[0x10].Order());
		Assert.Equal([0xAul], map.UnitsByMaterial[0x11]);
		Assert.Equal([0xEul], map.UnitsByMaterial[0x12]);
		Assert.True(map.BuildSeconds >= 0);
	}

	// ---- 缓存：读写 ----------------------------------------------------------------------

	[Fact]
	public async Task Store_RoundTripsAndRejectsAMismatchedKey()
	{
		var path = Path.Combine(_root, "cache", ArmorUnitMaterialCacheStore.FileName);
		var map = new ArmorUnitMaterialMap(MaterialMap((0x10, [0xA, 0xB])), 5, 1, 2.5, DateTimeOffset.UtcNow);

		await ArmorUnitMaterialCacheStore.WriteAsync(path, "key-1", map);

		var read = await ArmorUnitMaterialCacheStore.TryReadAsync(path, "key-1");
		Assert.NotNull(read);
		Assert.Equal([0xAul, 0xBul], read!.UnitsByMaterial[0x10].Order());
		Assert.Equal(5, read.UnitCount);
		Assert.Equal(1, read.FailedUnitCount);
		Assert.Equal(2.5, read.BuildSeconds);
		Assert.Null(await ArmorUnitMaterialCacheStore.TryReadAsync(path, "key-2"));
		Assert.Contains("\"failedUnitCount\":1", await File.ReadAllTextAsync(path));   // 失败数写进缓存文件
	}

	[Fact]
	public async Task Store_CorruptOrMissingFileReadsAsNull()
	{
		var path = Path.Combine(_root, ArmorUnitMaterialCacheStore.FileName);
		Assert.Null(await ArmorUnitMaterialCacheStore.TryReadAsync(path, "k"));
		await File.WriteAllTextAsync(path, "{ not json");
		Assert.Null(await ArmorUnitMaterialCacheStore.TryReadAsync(path, "k"));
	}

	[Fact]
	public async Task Store_WriteIsAtomic_AFailedWriteKeepsTheOldFileAndLeavesNoTempFile()
	{
		var path = Path.Combine(_root, ArmorUnitMaterialCacheStore.FileName);
		var old = new ArmorUnitMaterialMap(MaterialMap((0x10, [0xA])), 1, 0, 0.1, DateTimeOffset.UtcNow);
		await ArmorUnitMaterialCacheStore.WriteAsync(path, "old", old);
		var replacement = new ArmorUnitMaterialMap(MaterialMap((0x99, [0x1])), 1, 0, 0.1, DateTimeOffset.UtcNow);

		using var cancelled = new CancellationTokenSource();
		await cancelled.CancelAsync();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(
			() => ArmorUnitMaterialCacheStore.WriteAsync(path, "new", replacement, cancelled.Token));

		var read = await ArmorUnitMaterialCacheStore.TryReadAsync(path, "old");
		Assert.NotNull(read);
		Assert.Contains(0x10ul, read!.UnitsByMaterial.Keys);
		Assert.Empty(Directory.GetFiles(_root, "*.tmp"));

		await ArmorUnitMaterialCacheStore.WriteAsync(path, "new", replacement);
		Assert.NotNull(await ArmorUnitMaterialCacheStore.TryReadAsync(path, "new"));
		Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
	}

	[Fact]
	public async Task IndexKey_UsesMetaWhenPresentAndFileStampOtherwise()
	{
		var withMeta = CreateIndex("with-meta", fingerprint: "fp-a", builtUtc: "t1");
		var key = await ArmorUnitMaterialCacheStore.ComputeIndexKeyAsync(withMeta);
		Assert.Equal("meta:fp-a|t1", key);

		var bare = CreateIndex("bare", fingerprint: null, builtUtc: null);
		var fileKey = await ArmorUnitMaterialCacheStore.ComputeIndexKeyAsync(bare);
		Assert.StartsWith("file:", fileKey);

		Assert.Null(await ArmorUnitMaterialCacheStore.ComputeIndexKeyAsync(Path.Combine(_root, "nope.sqlite")));
	}

	// ---- 服务（合成索引库）---------------------------------------------------------------

	private string CreateIndex(string name, string? fingerprint = "fp1", string? builtUtc = "t1")
	{
		var path = Path.Combine(_root, name + ".sqlite");
		using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
		connection.Open();
		Exec(connection, """
			CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
			CREATE TABLE assets (type_id INTEGER NOT NULL, file_id INTEGER NOT NULL, df INTEGER NOT NULL, PRIMARY KEY(type_id, file_id));
			CREATE TABLE asset_archives (type_id INTEGER NOT NULL, file_id INTEGER NOT NULL, archive_id TEXT NOT NULL, PRIMARY KEY(type_id, file_id, archive_id));
			CREATE TABLE game_data_authority_kits (item_id INTEGER PRIMARY KEY, dlc_id INTEGER NOT NULL, set_id INTEGER NOT NULL, kit_kind INTEGER NOT NULL, archive_id TEXT NOT NULL, display_name TEXT NOT NULL);
			CREATE TABLE game_data_authority_pieces (item_id INTEGER NOT NULL, slot INTEGER NOT NULL, unit_type_id INTEGER NOT NULL, unit_file_id INTEGER NOT NULL, raw_body_type INTEGER NOT NULL DEFAULT 0, body_variant INTEGER NOT NULL DEFAULT 0);
			""");
		if (fingerprint is not null) Exec(connection, $"INSERT INTO meta VALUES('source_fingerprint','{fingerprint}');");
		if (builtUtc is not null) Exec(connection, $"INSERT INTO meta VALUES('built_utc','{builtUtc}');");
		return path;
	}

	private static void Exec(SqliteConnection connection, string sql)
	{
		using var command = connection.CreateCommand();
		command.CommandText = sql;
		command.ExecuteNonQuery();
	}

	private static long S(ulong value) => unchecked((long)value);

	/// <summary>
	/// 套 1 FS-05：Unit A；套 2 CM-17：Unit A；套 3 CE-64：Unit B、Unit D（D 读不出来）、披风 C（slot 1）。
	/// 原版资源：Unit A/B/C/D、材质 10/11、贴图 20。
	/// </summary>
	private string CreateStandardIndex(string name)
	{
		var path = CreateIndex(name);
		using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
		connection.Open();
		Exec(connection, "INSERT INTO game_data_authority_kits VALUES (1,0,0,0,'x','FS-05'),(2,0,0,0,'x','CM-17'),(3,0,0,0,'x','CE-64');");
		Exec(connection, $"""
			INSERT INTO game_data_authority_pieces (item_id, slot, unit_type_id, unit_file_id) VALUES
				(1,2,{S(Unit)},{0xA}),(2,2,{S(Unit)},{0xA}),(3,2,{S(Unit)},{0xB}),(3,2,{S(Unit)},{0xD}),(3,1,{S(Unit)},{0xC});
			INSERT INTO assets VALUES
				({S(Unit)},{0xA},0),({S(Unit)},{0xB},0),({S(Unit)},{0xC},0),({S(Unit)},{0xD},0),
				({S(Material)},{0x10},0),({S(Material)},{0x11},0),({S(Texture)},{0x20},0);
			INSERT INTO asset_archives VALUES
				({S(Unit)},{0xA},'arch1'),({S(Unit)},{0xB},'arch1'),({S(Unit)},{0xC},'arch2'),({S(Unit)},{0xD},'arch2');
			""");
		return path;
	}

	private static FakeTocReader StandardReader() => new((_, unit) => unit switch
	{
		0xA => UnitToc(0x10),
		0xB => UnitToc(0x10, 0x11),
		0xD => throw new InvalidDataException("坏 Unit"),
		_ => UnitToc(),
	});

	private KitPollutionService Service(string indexPath, string cachePath, FakeTocReader reader)
		=> new(indexPath, cachePath, _ => reader);

	private string GameData()
	{
		var path = Path.Combine(_root, "game-data");
		Directory.CreateDirectory(path);
		return path;
	}

	private static async Task WaitForBuildAsync(KitPollutionService service)
	{
		var pending = service.PendingMaterialBuild;
		if (pending is not null) await pending.WaitAsync(TimeSpan.FromSeconds(30));
	}

	[Fact]
	public async Task Service_UnitOnlyUntilTheCacheIsBuilt_ThenMaterialsJoinIn_AndBuildSkipsOneBadUnit()
	{
		var index = CreateStandardIndex("standard");
		var cache = Path.Combine(_root, "cache-1.json");
		var reader = StandardReader();
		var service = Service(index, cache, reader);
		AssetKey[] mod = [new(Material, 0x10)];

		// 缓存还没有：只给 Unit 部分的结论（这个 Mod 没有 Unit ⇒ 没结论），并且不等构建。
		var first = await service.AnalyzeAsync(mod, GameData());
		Assert.NotNull(first);
		Assert.False(first!.MaterialAnalysisAvailable);
		Assert.Empty(first.Kits);

		await WaitForBuildAsync(service);
		var second = await service.AnalyzeAsync(mod, GameData());
		Assert.True(second!.MaterialAnalysisAvailable);
		Assert.True(second.IsPolluted);
		Assert.Equal([1u, 2u, 3u], second.Kits.Select(kit => kit.ItemId).Order());

		// 缓存文件带着构建耗时与失败数；披风（C）不在构建范围里：A、B、D 共 3 个，D 失败 1 个。
		var cached = await ArmorUnitMaterialCacheStore.TryReadAsync(cache, (await ArmorUnitMaterialCacheStore.ComputeIndexKeyAsync(index))!);
		Assert.NotNull(cached);
		Assert.Equal(3, cached!.UnitCount);
		Assert.Equal(1, cached.FailedUnitCount);
		Assert.Equal(3, reader.Calls);
	}

	[Fact]
	public async Task Service_RebuildsWhenTheIndexKeyChanges()
	{
		var index = CreateStandardIndex("rebuild");
		var cache = Path.Combine(_root, "cache-2.json");
		var reader = StandardReader();
		var service = Service(index, cache, reader);
		AssetKey[] mod = [new(Material, 0x10)];

		await service.AnalyzeAsync(mod, GameData());
		await WaitForBuildAsync(service);
		Assert.Equal(3, reader.Calls);

		// 键没变：命中缓存，不再读 Unit。
		Assert.True((await service.AnalyzeAsync(mod, GameData()))!.MaterialAnalysisAvailable);
		Assert.Equal(3, reader.Calls);
		Assert.Null(service.PendingMaterialBuild);

		// 索引重建（生成标识变了）：旧缓存作废，回到「分析中」，后台重建。
		using (var connection = new SqliteConnection($"Data Source={index};Pooling=False"))
		{
			connection.Open();
			Exec(connection, "UPDATE meta SET value='fp2' WHERE key='source_fingerprint';");
		}
		var stale = await service.AnalyzeAsync(mod, GameData());
		Assert.False(stale!.MaterialAnalysisAvailable);
		await WaitForBuildAsync(service);
		Assert.Equal(6, reader.Calls);
		Assert.True((await service.AnalyzeAsync(mod, GameData()))!.MaterialAnalysisAvailable);
	}

	[Fact]
	public async Task Service_WithoutAGameDataDirectoryNeverBuilds_ButStillAnswersTheUnitPart()
	{
		var index = CreateStandardIndex("no-game");
		var cache = Path.Combine(_root, "cache-3.json");
		var reader = StandardReader();
		var service = Service(index, cache, reader);

		var result = await service.AnalyzeAsync([new(Unit, 0xA)], gameDataDirectory: null);

		Assert.True(result!.IsPolluted);
		Assert.False(result.MaterialAnalysisAvailable);
		Assert.Null(service.PendingMaterialBuild);
		Assert.Equal(0, reader.Calls);
		Assert.False(File.Exists(cache));
	}

	[Fact]
	public async Task Service_IgnoresNonVanillaAssetsAndReportsVanillaTextures()
	{
		var index = CreateStandardIndex("vanilla");
		var service = Service(index, Path.Combine(_root, "cache-4.json"), StandardReader());

		var result = await service.AnalyzeAsync(
			[new(Unit, 0xFFFF), new(Material, 0xFFFE), new(Texture, 0x20), new(Texture, 0x21)], gameDataDirectory: null);

		Assert.Empty(result!.Kits);
		Assert.Equal(1, result.VanillaTextureCount);   // 0x20 在 assets 表里，0x21 不在
	}

	[Fact]
	public async Task Service_ModsWithoutUnitMaterialOrTextureTouchNothing()
	{
		var service = Service(Path.Combine(_root, "does-not-exist.sqlite"), Path.Combine(_root, "cache-5.json"), StandardReader());
		var result = await service.AnalyzeAsync([new(0x1234, 1)], GameData());
		Assert.NotNull(result);
		Assert.False(result!.IsPolluted);
	}

	[Fact]
	public async Task Service_MissingIndexOrEmptyAuthorityTableMeansNoConclusion()
	{
		var missing = Service(Path.Combine(_root, "missing.sqlite"), Path.Combine(_root, "cache-6.json"), StandardReader());
		Assert.Null(await missing.AnalyzeAsync([new(Unit, 0xA)], GameData()));

		var empty = Service(CreateIndex("empty"), Path.Combine(_root, "cache-7.json"), StandardReader());
		Assert.Null(await empty.AnalyzeAsync([new(Unit, 0xA)], GameData()));
	}
}
