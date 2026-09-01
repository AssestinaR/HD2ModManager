using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModCore.Infrastructure;
using HD2ModCore.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;

namespace HD2ModCore.Tests;

// Purpose: Keeps the advanced equipment index freshness gate honest. A matching
// version/fingerprint is not enough when an indexed Unit is missing either its
// parsed part facts or its stream ABI layout.
public sealed class AdvancedEquipmentIndexServiceTests
{
	[Fact]
	public async Task IsCurrentAsync_ReturnsTrueWhenEveryEquipmentUnitHasPartAndStreamFacts()
	{
		await using var fixture = await Fixture.CreateAsync(includePartFacts: true, includeStreamLayouts: true);

		Assert.True(await new AdvancedEquipmentIndexService(fixture.Paths).IsCurrentAsync());
	}

	[Theory]
	[InlineData(false, false)]
	[InlineData(false, true)]
	[InlineData(true, false)]
	public async Task IsCurrentAsync_ReturnsFalseWhenEquipmentFactsAreIncomplete(bool includePartFacts, bool includeStreamLayouts)
	{
		await using var fixture = await Fixture.CreateAsync(includePartFacts, includeStreamLayouts);

		Assert.False(await new AdvancedEquipmentIndexService(fixture.Paths).IsCurrentAsync());
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

		public static async ValueTask<Fixture> CreateAsync(bool includePartFacts, bool includeStreamLayouts)
		{
			var root = Path.Combine(Path.GetTempPath(), "hd2-equipment-index-tests", Guid.NewGuid().ToString("N"));
			var paths = new StoragePaths(Path.Combine(root, "app"));
			Directory.CreateDirectory(paths.IndexDirectory);

			await using var connection = new SqliteConnection($"Data Source={paths.DbPath};Pooling=False");
			await connection.OpenAsync();
			await SqliteSchema.EnsureCreatedAsync(connection, CancellationToken.None);
			await SqliteSchema.SetMetaAsync(connection, "source_fingerprint", "test-fingerprint", CancellationToken.None);
			await SqliteSchema.SetMetaAsync(connection, "advanced_equipment_index_source_fingerprint", "test-fingerprint", CancellationToken.None);
			await SqliteSchema.SetMetaAsync(connection, "advanced_equipment_index_version", "equipment-v6-authoritative-equipment-facts", CancellationToken.None);

			const string archiveId = "aaaaaaaaaaaaaaaa";
			var unitTypeId = unchecked((long)PatchUnitMeshReader.UnitTypeId);
			const long unitFileId = 0x1234;
			await ExecuteAsync(connection, $"INSERT INTO archives(archive_id,category,display_name,status) VALUES('{archiveId}','Armor','Test Armor','Indexed');");
			await ExecuteAsync(connection, $"INSERT INTO archive_entries(archive_id,entry_index,type_id,file_id,df,toc_data_offset,stream_offset,gpu_resource_offset,toc_data_size,stream_size,gpu_resource_size,unknown1,unknown2,unknown3,unknown4) VALUES('{archiveId}',0,{unitTypeId},{unitFileId},1,0,0,0,0,0,0,0,0,0,0);");

			if (includePartFacts)
			{
				await ExecuteAsync(connection, $"INSERT INTO game_data_unit_parts(archive_id,unit_type_id,unit_file_id,mesh_info_index,mesh_id,part_kind,part_layer,body_variant,semantic_name,confidence,is_visual,is_lod,vertex_count,triangle_count,geometry_quality,fact_origin,piece_type,reason) VALUES('{archiveId}',{unitTypeId},{unitFileId},0,1,1,1,1,'Torso',100,1,0,3,1,1,1,'2','test');");
			}

			if (includeStreamLayouts)
			{
				await ExecuteAsync(connection, $"INSERT INTO game_data_stream_layouts(archive_id,unit_type_id,unit_file_id,stream_index,component_info_id,unit_version,vertex_stride,components_json,layout_signature,is_skinned) VALUES('{archiveId}',{unitTypeId},{unitFileId},0,1,1,32,'[]','test-layout',1);");
			}

			return new Fixture(root, paths);
		}

		private static async Task ExecuteAsync(SqliteConnection connection, string sql)
		{
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
