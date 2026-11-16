using HD2ModAdaptation.Analysis;
using HD2ModAdaptation.PatchReconstruction;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModAdaptation.PatchReconstruction.UnitMesh.Canonical;
using HD2ModCore.Infrastructure;
using HD2ModCore.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;
using AdaptationAssetKey = HD2ModAdaptation.PatchReconstruction.AssetKey;

namespace HD2ModCore.Tests;

/// <summary>
/// 锁住「干净资产白名单」的闭包规则：选中的 unit → 它引用的材质 → 那些材质引用的贴图。
/// <para>
/// 这条规则决定了产物里到底带哪些材质：漏掉一层，产物会变成"模型在、材质没了"；
/// 多带一层，用户会发现产物里混进了跟这套护甲无关的材质包。
/// </para>
/// </summary>
public sealed class SectionalWhitelistBuilderTests
{
	private const ulong UnitType = 0x1111111111111111;
	private const ulong MaterialType = 0xeac0b497876adedf;
	private const ulong TextureType = 0xcd4238c6a0c69e32;

	private static AdaptationAssetKey Unit(ulong id) => new(UnitType, id);
	private static AdaptationAssetKey Material(ulong id) => new(MaterialType, id);
	private static AdaptationAssetKey Texture(ulong id) => new(TextureType, id);

	private static PatchAssetReference UnitMaterial(ulong unit, ulong material)
		=> new(Unit(unit), Material(material), PatchReferenceKind.UnitMaterial, 0);

	private static PatchAssetReference MaterialTexture(ulong material, ulong texture)
		=> new(Material(material), Texture(texture), PatchReferenceKind.MaterialTexture, 0);

	[Fact]
	public void 闭包只收被选中unit可达的材质与贴图()
	{
		PatchAssetReference[] references =
		[
			UnitMaterial(1, 10),
			UnitMaterial(2, 20),
			MaterialTexture(10, 100),
			MaterialTexture(20, 200),
		];

		var keys = SectionalWhitelistBuilder.Build([Unit(1)], references);

		Assert.Single(keys.Units);
		Assert.Equal([SectionalWhitelistBuilder.Format(Material(10))], keys.Materials);
		Assert.Equal([SectionalWhitelistBuilder.Format(Texture(100))], keys.Textures);
	}

	[Fact]
	public void 贴图只跟材质走_不跟unit隔层跳()
	{
		// unit 没有直接引用贴图；若实现里把 MaterialTexture 也按 unit 过滤，产物会丢贴图。
		PatchAssetReference[] references =
		[
			UnitMaterial(1, 10),
			MaterialTexture(10, 100),
			MaterialTexture(99, 999),
		];

		var keys = SectionalWhitelistBuilder.Build([Unit(1)], references);

		Assert.Equal([SectionalWhitelistBuilder.Format(Texture(100))], keys.Textures);
	}

	[Fact]
	public void 多个unit共用同一材质时只收一次()
	{
		PatchAssetReference[] references =
		[
			UnitMaterial(1, 10),
			UnitMaterial(2, 10),
			MaterialTexture(10, 100),
		];

		var keys = SectionalWhitelistBuilder.Build([Unit(1), Unit(2)], references);

		Assert.Equal([SectionalWhitelistBuilder.Format(Material(10))], keys.Materials);
	}

	[Fact]
	public void 没有引用关系时材质与贴图为空_但unit仍在()
	{
		var keys = SectionalWhitelistBuilder.Build([Unit(1)], []);

		Assert.Single(keys.Units);
		Assert.Empty(keys.Materials);
		Assert.Empty(keys.Textures);
		Assert.Contains("没有引用任何材质", SectionalWhitelistBuilder.Describe(keys)!, StringComparison.Ordinal);
	}

	[Fact]
	public void 没有unit时给出人话说明()
	{
		var keys = SectionalWhitelistBuilder.Build([], []);

		Assert.Contains("没有任何可转换的 unit", SectionalWhitelistBuilder.Describe(keys)!, StringComparison.Ordinal);
	}

	[Fact]
	public void 格式与解析往返一致()
	{
		PatchAssetReference[] references = [UnitMaterial(1, 10), MaterialTexture(10, 100)];
		var keys = SectionalWhitelistBuilder.Build([Unit(1)], references);

		var parsed = SectionalWhitelistBuilder.Parse(keys);

		Assert.Contains(SectionalWhitelistBuilder.ToRef(Unit(1)), parsed);
		Assert.Contains(SectionalWhitelistBuilder.ToRef(Material(10)), parsed);
		Assert.Contains(SectionalWhitelistBuilder.ToRef(Texture(100)), parsed);
		Assert.Equal(3, parsed.Count);
	}

	[Fact]
	public async Task FindForeignEquipmentUnitsAsync_ExcludesOnlyUnitsExclusiveToUnselectedArchives()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2-foreign-unit-tests", Guid.NewGuid().ToString("N"));
		var paths = new StoragePaths(Path.Combine(root, "app"));
		Directory.CreateDirectory(paths.IndexDirectory);
		try
		{
			await using (var connection = new SqliteConnection($"Data Source={paths.DbPath};Pooling=False"))
			{
				await connection.OpenAsync();
				await SqliteSchema.EnsureCreatedAsync(connection, CancellationToken.None);
				await using var cmd = connection.CreateCommand();
				cmd.CommandText = """
					INSERT INTO game_data_authority_kits(item_id, dlc_id, set_id, kit_kind, archive_id, display_name)
					VALUES (100, 1, 1, 0, 'aaaaaaaaaaaaaaaa', 'KitA'),
					       (200, 1, 2, 0, 'bbbbbbbbbbbbbbbb', 'KitB');
					INSERT INTO game_data_authority_pieces(item_id, archive_id, kit_kind, raw_body_type, body_variant, slot, part_kind, piece_type, weight, tone_variations, unit_type_id, unit_file_id)
					VALUES (100, 'aaaaaaaaaaaaaaaa', 0, 3, 2, 2, 0, 0, 1, 0, 1, 1001),
					       (100, 'aaaaaaaaaaaaaaaa', 0, 3, 2, 3, 0, 0, 1, 0, 1, 1002),
					       (200, 'bbbbbbbbbbbbbbbb', 0, 3, 2, 3, 0, 0, 1, 0, 1, 1002),
					       (200, 'bbbbbbbbbbbbbbbb', 0, 3, 2, 2, 0, 0, 1, 0, 1, 1003);
					""";
				await cmd.ExecuteNonQueryAsync();
			}

			string Hex(ulong id) => id.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
			var foreign = await ArmorUnitCellLookup.FindForeignEquipmentUnitsAsync(
				paths,
				[Hex(1001), Hex(1002), Hex(1003), Hex(9999)],
				["aaaaaaaaaaaaaaaa"]);

			var only = Assert.Single(foreign);
			Assert.Equal(Hex(1003), only);
		}
		finally
		{
			SqliteConnection.ClearAllPools();
			if (Directory.Exists(root))
				Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public async Task ExpandIntentUnitsAsync_SkipsExcludedForeignUnitsWhileKeepingIntentUnits()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2-expand-tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			var intent = new AdaptationAssetKey(PatchUnitMeshReader.UnitTypeId, 1);
			var decoration = new AdaptationAssetKey(PatchUnitMeshReader.UnitTypeId, 2);
			var foreign = new AdaptationAssetKey(PatchUnitMeshReader.UnitTypeId, 3);

			var session = new CanonicalPatchSession();
			foreach (var key in new[] { intent, decoration, foreign })
			{
				session.AddEntry(new CanonicalPatchSessionEntry(
					key,
					CanonicalPatchEntryOwnership.TargetOutput,
					[1, 2, 3],
					[],
					[]));
			}
			Assert.True(session.Finalize(CanonicalDependencyClosureValidation.Valid).IsValid);
			await new CanonicalPatchWriter().WriteAsync(session, root, "multi.patch_0", null, overwriteExisting: true);
			var patchPath = Path.Combine(root, "multi.patch_0");

			var expanded = await SectionalWhitelistBuilder.ExpandIntentUnitsAsync(
				[intent],
				[patchPath],
				excludedUnits: [foreign, intent]);

			Assert.Contains(intent, expanded);
			Assert.Contains(decoration, expanded);
			Assert.DoesNotContain(foreign, expanded);
			Assert.Equal(2, expanded.Count);
		}
		finally
		{
			if (Directory.Exists(root))
				Directory.Delete(root, recursive: true);
		}
	}
}
