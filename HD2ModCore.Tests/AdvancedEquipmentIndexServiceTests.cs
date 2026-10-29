using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using HD2ModCore.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;

namespace HD2ModCore.Tests;

// Purpose: Keeps the advanced equipment index freshness gate honest. A matching
// version/fingerprint is not enough when an indexed Unit is missing either its
// parsed part facts or its stream ABI layout.
//
// 第二个主题：「哪些 unit 需要部位事实」以**注册表**为准。以前是按 archive 分类扫
// archive_entries，于是被护甲套引用、实体却存在 Weapon 分类 archive 里的披风 unit
// 永远进不了部位表。下面两条用例把"注册表给集合、archive 只决定从哪解析"钉住。
public sealed class AdvancedEquipmentIndexServiceTests
{
	private const string ArmorArchive = "aaaaaaaaaaaaaaaa";
	private const string WeaponArchive = "bbbbbbbbbbbbbbbb";
	private const long BodyUnit = 0x1234;
	private const long CapeUnit = 0x5678;

	[Fact]
	public async Task IsCurrentAsync_ReturnsTrueWhenEveryEquipmentUnitHasPartAndStreamFacts()
	{
		await using var fixture = await Fixture.CreateAsync();
		await fixture.SeedArmorWithBodyUnitAsync();
		await fixture.SeedPartFactsAsync(ArmorArchive, BodyUnit);
		await fixture.SeedStreamLayoutAsync(ArmorArchive, BodyUnit);

		Assert.True(await new AdvancedEquipmentIndexService(fixture.Paths).IsCurrentAsync());
	}

	[Theory]
	[InlineData(false, false)]
	[InlineData(false, true)]
	[InlineData(true, false)]
	public async Task IsCurrentAsync_ReturnsFalseWhenEquipmentFactsAreIncomplete(bool includePartFacts, bool includeStreamLayouts)
	{
		await using var fixture = await Fixture.CreateAsync();
		await fixture.SeedArmorWithBodyUnitAsync();
		if (includePartFacts) await fixture.SeedPartFactsAsync(ArmorArchive, BodyUnit);
		if (includeStreamLayouts) await fixture.SeedStreamLayoutAsync(ArmorArchive, BodyUnit);

		Assert.False(await new AdvancedEquipmentIndexService(fixture.Paths).IsCurrentAsync());
	}

	// 披风 unit 就是这条：护甲套的 slot 1 引用了它，但它的字节存在一个 Weapon 分类的
	// archive 里。按 archive 分类枚举时它根本不在候选集里，于是任何护甲的披风槽都没有
	// 部件事实，而且没人会发现——它连"未定位"都不会显示，是彻底不存在。
	[Fact]
	public async Task ReadEquipmentUnitsAsync_TakesTheUnitSetFromTheRegistryNotFromTheArchiveCategory()
	{
		await using var fixture = await Fixture.CreateAsync();
		await fixture.SeedArmorWithBodyUnitAsync();
		await fixture.SeedArchiveAsync(WeaponArchive, "Weapon", CapeUnit);
		await fixture.SeedPieceAsync(itemId: 1, kitKind: GameDataEquipmentKitKind.Armor, archiveId: WeaponArchive, slot: 1, pieceType: 0, unitFileId: CapeUnit);
		var service = new AdvancedEquipmentIndexService(fixture.Paths);

		var units = await service.ReadEquipmentUnitsAsync(CancellationToken.None);

		Assert.Equal(2, units.Count);
		var cape = Assert.Single(units, unit => unit.AssetKey.FileId == CapeUnit);
		Assert.Equal(WeaponArchive, cape.ArchiveId);
		Assert.False(cape.HasPartFacts);
		Assert.False(cape.HasStreamLayouts);
		// 集合变大了，完整性判据必须跟着变大：否则披风 unit 永远缺事实、这个门永远为假。
		Assert.False(await service.IsCurrentAsync());
		await fixture.SeedPartFactsAsync(WeaponArchive, CapeUnit);
		await fixture.SeedStreamLayoutAsync(WeaponArchive, CapeUnit);
		await fixture.SeedPartFactsAsync(ArmorArchive, BodyUnit);
		await fixture.SeedStreamLayoutAsync(ArmorArchive, BodyUnit);
		Assert.True(await service.IsCurrentAsync());
	}

	// 同一个 unit 可能同时存在于装备 archive 和别的 archive 里（实测 429 个）。只解析装备
	// 分类的那一份：`EquipmentUnitCatalogService` 只读装备分类的 archive，解析另一份等于
	// 白写一遍谁都看不见的行。
	[Fact]
	public async Task ReadEquipmentUnitsAsync_PrefersTheEquipmentArchiveWhenAUnitIsStoredTwice()
	{
		await using var fixture = await Fixture.CreateAsync();
		await fixture.SeedArmorWithBodyUnitAsync();
		await fixture.SeedArchiveAsync(WeaponArchive, "Weapon", BodyUnit);

		var units = await new AdvancedEquipmentIndexService(fixture.Paths).ReadEquipmentUnitsAsync(CancellationToken.None);

		var unit = Assert.Single(units);
		Assert.Equal(ArmorArchive, unit.ArchiveId);
	}

	private sealed class Fixture : IAsyncDisposable
	{
		private Fixture(string root, StoragePaths paths)
		{
			Root = root;
			Paths = paths;
		}

		public string Root { get; }
		public StoragePaths Paths { get; }

		public static async ValueTask<Fixture> CreateAsync()
		{
			var root = Path.Combine(Path.GetTempPath(), "hd2-equipment-index-tests", Guid.NewGuid().ToString("N"));
			var paths = new StoragePaths(Path.Combine(root, "app"));
			Directory.CreateDirectory(paths.IndexDirectory);

			await using var connection = new SqliteConnection($"Data Source={paths.DbPath};Pooling=False");
			await connection.OpenAsync();
			await SqliteSchema.EnsureCreatedAsync(connection, CancellationToken.None);
			await SqliteSchema.SetMetaAsync(connection, "source_fingerprint", "test-fingerprint", CancellationToken.None);
			await SqliteSchema.SetMetaAsync(connection, "advanced_equipment_index_source_fingerprint", "test-fingerprint", CancellationToken.None);
			await SqliteSchema.SetMetaAsync(connection, "advanced_equipment_index_version", AdvancedEquipmentIndexService.Version, CancellationToken.None);

			return new Fixture(root, paths);
		}

		/// <summary>一个 Armor archive，里面放 body unit；同时登记成注册表里的一个护甲套件。</summary>
		public async ValueTask SeedArmorWithBodyUnitAsync()
		{
			await SeedArchiveAsync(ArmorArchive, "Armor", BodyUnit);
			await SeedPieceAsync(itemId: 1, kitKind: GameDataEquipmentKitKind.Armor, archiveId: ArmorArchive, slot: 2, pieceType: 1, unitFileId: BodyUnit);
		}

		public async ValueTask SeedArchiveAsync(string archiveId, string category, long unitFileId)
			=> await ExecuteAsync($@"
INSERT INTO archives(archive_id,category,display_name,status) VALUES('{archiveId}','{category}','Test {category}','Indexed');
INSERT INTO archive_entries(archive_id,entry_index,type_id,file_id,df,toc_data_offset,stream_offset,gpu_resource_offset,toc_data_size,stream_size,gpu_resource_size,unknown1,unknown2,unknown3,unknown4)
VALUES('{archiveId}',0,{UnitTypeId},{unitFileId},1,0,0,0,0,0,0,0,0,0,0);");

		public async ValueTask SeedPieceAsync(uint itemId, GameDataEquipmentKitKind kitKind, string archiveId, uint slot, uint pieceType, long unitFileId)
			=> await ExecuteAsync($@"
INSERT OR IGNORE INTO game_data_authority_kits(item_id,dlc_id,set_id,kit_kind,archive_id,display_name) VALUES({itemId},0,0,{(int)kitKind},'{archiveId}','Test Kit');
INSERT INTO game_data_authority_pieces(item_id,archive_id,kit_kind,raw_body_type,body_variant,slot,part_kind,piece_type,weight,tone_variations,unit_type_id,unit_file_id)
VALUES({itemId},'{archiveId}',{(int)kitKind},3,3,{slot},{(int)ArmorStructure.ToPartKind(slot)},{pieceType},0,0,{UnitTypeId},{unitFileId});");

		public async ValueTask SeedPartFactsAsync(string archiveId, long unitFileId)
			=> await ExecuteAsync($@"INSERT INTO game_data_unit_parts(archive_id,unit_type_id,unit_file_id,mesh_info_index,mesh_id,part_kind,part_layer,body_variant,semantic_name,piece_type,confidence,is_visual,is_lod,vertex_count,triangle_count,geometry_quality,fact_origin,reason)
VALUES('{archiveId}',{UnitTypeId},{unitFileId},0,1,2,2,3,'torso',1,100,1,0,3,1,2,1,'test');");

		public async ValueTask SeedStreamLayoutAsync(string archiveId, long unitFileId)
			=> await ExecuteAsync($@"INSERT INTO game_data_stream_layouts(archive_id,unit_type_id,unit_file_id,stream_index,component_info_id,unit_version,vertex_stride,components_json,layout_signature,is_skinned)
VALUES('{archiveId}',{UnitTypeId},{unitFileId},0,1,1,32,'[]','test-layout',1);");

		private static long UnitTypeId => unchecked((long)PatchUnitMeshReader.UnitTypeId);

		private async ValueTask ExecuteAsync(string sql)
		{
			await using var connection = new SqliteConnection($"Data Source={Paths.DbPath};Pooling=False");
			await connection.OpenAsync();
			await using var command = connection.CreateCommand();
			command.CommandText = sql;
			await command.ExecuteNonQueryAsync();
		}

		public ValueTask DisposeAsync()
		{
			try { Directory.Delete(Root, recursive: true); }
			catch { }
			return ValueTask.CompletedTask;
		}
	}
}
