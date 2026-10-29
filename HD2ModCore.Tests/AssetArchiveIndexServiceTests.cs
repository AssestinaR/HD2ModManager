using System.Text.Json;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using Microsoft.Data.Sqlite;

namespace HD2ModCore.Tests;

// 作用：验证 SQLite 索引构建、投票与过滤（百分比/固定值）在最小数据集上的行为。
// Purpose: Verifies SQLite index build, voting and filtering (percentage/absolute) on a minimal dataset.
public sealed class AssetArchiveIndexServiceTests
{
	[Fact]
	public async Task Build_AuthoritativeGameDataName_OverridesCommunityDisplayName()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		var appRoot = Path.Combine(root, "app");
		var gameData = Path.Combine(root, "game", "data");
		Directory.CreateDirectory(appRoot);
		Directory.CreateDirectory(gameData);
		try
		{
			const string archive = "aaaaaaaaaaaaaaaa";
			File.WriteAllBytes(Path.Combine(gameData, archive), BuildToc([new AssetKey(20, 200)]));
			var authority = new GameDataAuthoritySnapshot(
				GameDataAuthoritySourceKind.PlaintextDataLibrary,
				"installed-game-data",
				"authority-fingerprint",
				DateTimeOffset.UtcNow,
				Array.Empty<GameDataAuthorityKit>(),
				Array.Empty<CoreIssue>())
			{
				ArchiveNames =
				[
					new GameDataAuthorityArchiveName(archive, 0, "en-US", "Installed authoritative name", "game-data-test", 100, "Armor")
				]
			};
			var community = JsonSerializer.Serialize(new Dictionary<string, Dictionary<string, string>>
			{
				["Armor"] = new() { [archive] = "Community name" }
			});

			var service = new AssetArchiveIndexService(
				new StoragePaths(appRoot),
				authorityProvider: new FixedAuthorityProvider(authority));
			await service.BuildOrRebuildAsync(gameData, community);

			var details = await service.GetArchiveDetailsAsync(archive);
			Assert.NotNull(details);
			Assert.Equal("Installed authoritative name", details.Summary.DisplayName);
			Assert.Equal("Armor", details.Summary.Category);
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}

	// 作用：钉住归档摘要缓存的失效条件。
	//
	// 为什么这条必须有：摘要查询是 archives LEFT JOIN archive_entries（35 万行）的 GROUP BY，
	// 实测 ~215ms，而护甲页每打开一次就会问一次（见 docs/armor-registry-page-perf-20261027.md
	// §1 A2/A3）。所以它被缓存了。但缓存键是 source_fingerprint，而**展示名与分类来自
	// friendlynames / archivehashes**，两者都不在指纹里 —— 只换命名数据的一次重建会
	// 得到完全相同的指纹。如果失效只看指纹，用户改完名字重建后会一直看到旧名字。
	//
	// 这条用例刻意让两次构建的指纹完全相同，于是"第二次读到新名字"只可能来自显式失效。
	[Fact]
	public async Task Rebuild_WithUnchangedGameData_InvalidatesTheArchiveSummaryCache()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		var appRoot = Path.Combine(root, "app");
		var gameData = Path.Combine(root, "game", "data");
		Directory.CreateDirectory(appRoot);
		Directory.CreateDirectory(gameData);
		try
		{
			const string archive = "aaaaaaaaaaaaaaaa";
			File.WriteAllBytes(Path.Combine(gameData, archive), BuildToc([new AssetKey(20, 200)]));
			// 权威快照里刻意不带归档名，让社区命名成为唯一来源。
			var authority = new GameDataAuthoritySnapshot(
				GameDataAuthoritySourceKind.PlaintextDataLibrary,
				"installed-game-data",
				"authority-fingerprint",
				DateTimeOffset.UtcNow,
				Array.Empty<GameDataAuthorityKit>(),
				Array.Empty<CoreIssue>())
			{
				ArchiveNames = []
			};
			var paths = new StoragePaths(appRoot);

			var first = new AssetArchiveIndexService(paths, authorityProvider: new FixedAuthorityProvider(authority));
			await first.BuildOrRebuildAsync(gameData, CommunityName(archive, "First name"));
			var firstFingerprint = await first.GetFingerprintAsync();
			Assert.Equal("First name", Assert.Single(await first.GetArchiveSummariesAsync()).DisplayName);

			// 第二次构建用**另一个实例**：顺带钉住"缓存是进程级的，重建会作废它"。
			var second = new AssetArchiveIndexService(paths, authorityProvider: new FixedAuthorityProvider(authority));
			await second.BuildOrRebuildAsync(gameData, CommunityName(archive, "Second name"));
			var secondFingerprint = await second.GetFingerprintAsync();

			// 游戏文件一个字节都没动，指纹必须相同——否则这条用例就证明不了失效逻辑。
			Assert.NotNull(firstFingerprint);
			Assert.NotNull(secondFingerprint);
			Assert.Equal(firstFingerprint.SourceFingerprint, secondFingerprint.SourceFingerprint);

			Assert.Equal("Second name", Assert.Single(await second.GetArchiveSummariesAsync()).DisplayName);
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}

	private static string CommunityName(string archive, string name)
		=> JsonSerializer.Serialize(new Dictionary<string, Dictionary<string, string>>
		{
			["Armor"] = new() { [archive] = name }
		});

	// 作用：钉住"重复读摘要不再摸那 35 万行"这件事本身。
	// 指纹与库内容都没变时，第二次必须直接返回同一份结果（同一个实例）——
	// 只要它重新查了一次库，这条断言就会失败。
	[Fact]
	public async Task GetArchiveSummariesAsync_ServesRepeatCallsFromTheFingerprintKeyedCache()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		var appRoot = Path.Combine(root, "app");
		var gameData = Path.Combine(root, "game", "data");
		Directory.CreateDirectory(appRoot);
		Directory.CreateDirectory(gameData);
		try
		{
			const string archive = "aaaaaaaaaaaaaaaa";
			File.WriteAllBytes(Path.Combine(gameData, archive), BuildToc([new AssetKey(20, 200)]));
			var service = new AssetArchiveIndexService(new StoragePaths(appRoot));
			await service.BuildOrRebuildAsync(gameData, "{}");

			var first = await service.GetArchiveSummariesAsync();
			var second = await service.GetArchiveSummariesAsync();

			Assert.Single(first);
			Assert.Same(first, second);
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}

	[Fact]
	public async Task Build_EmptyCommunityMetadata_IndexesInstalledArchivesOnly()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		var appRoot = Path.Combine(root, "app");
		var gameData = Path.Combine(root, "game", "data");
		Directory.CreateDirectory(appRoot);
		Directory.CreateDirectory(gameData);

		try
		{
			const string archive = "aaaaaaaaaaaaaaaa";
			var key = new AssetKey(20, 200);
			File.WriteAllBytes(Path.Combine(gameData, archive), BuildToc([key]));
			// Both sidecars and data libraries belong to GameData but are not independently
			// indexable Archive TOCs.
			File.WriteAllBytes(Path.Combine(gameData, archive + ".stream"), [1, 2, 3]);
			File.WriteAllBytes(Path.Combine(gameData, archive + ".gpu_resources"), [4, 5, 6]);
			File.WriteAllBytes(Path.Combine(gameData, "generated_customization_armor_sets"), [7, 8, 9]);

			var service = new AssetArchiveIndexService(new StoragePaths(appRoot));
			await service.BuildOrRebuildAsync(gameData, "{}");

			var fingerprint = await service.GetFingerprintAsync();
			Assert.NotNull(fingerprint);
			Assert.Equal(1, fingerprint.ArchivesTotal);
			var match = Assert.Single(await service.FindAssetArchivesAsync(new HashSet<AssetKey> { key }));
			Assert.Equal(archive, Assert.Single(match.Archives).ArchiveId);
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}

	[Fact]
	public async Task MalformedCommunityMetadata_DoesNotBlockInstalledArchiveIndex()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		var appRoot = Path.Combine(root, "app");
		var gameData = Path.Combine(root, "game", "data");
		Directory.CreateDirectory(appRoot);
		Directory.CreateDirectory(gameData);
		try
		{
			const string archive = "aaaaaaaaaaaaaaaa";
			File.WriteAllBytes(Path.Combine(gameData, archive), BuildToc(new[] { new AssetKey(20, 200) }));
			var paths = new StoragePaths(appRoot);
			Directory.CreateDirectory(paths.ResourcesDirectory);
			await File.WriteAllTextAsync(paths.ArchiveHashesPath, "{\"Armor\":null}");

			var service = new AssetArchiveIndexService(paths);
			await service.BuildOrRebuildAsync(gameData, await File.ReadAllTextAsync(paths.ArchiveHashesPath));

			var fingerprint = await service.GetFingerprintAsync();
			Assert.NotNull(fingerprint);
			Assert.Equal(1, fingerprint.ArchivesTotal);
			Assert.Equal(1, fingerprint.ArchivesIndexed);
			var catalog = await new FileSystemAssetMetadataCatalogProvider(paths).LoadAsync();
			Assert.Empty(catalog.Archives);
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}

	[Fact]
	public async Task BuildAndVote_PercentageFilter_FiltersHighDfKeys()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		var appRoot = Path.Combine(root, "app");
		var gameData = Path.Combine(root, "game", "data");
		Directory.CreateDirectory(appRoot);
		Directory.CreateDirectory(gameData);

		try
		{
			var archiveA = "aaaaaaaaaaaaaaaa";
			var archiveB = "bbbbbbbbbbbbbbbb";
			File.WriteAllBytes(Path.Combine(gameData, archiveA), BuildToc(new[]
			{
				new AssetKey(10, 100), // shared
				new AssetKey(20, 200), // only in A
			}));
			File.WriteAllBytes(Path.Combine(gameData, archiveB), BuildToc(new[]
			{
				new AssetKey(10, 100), // shared
				new AssetKey(30, 300), // only in B
			}));

			var archiveHashes = new Dictionary<string, Dictionary<string, string>>
			{
				["Armor"] = new Dictionary<string, string>
				{
					[archiveA] = "Armor A",
					[archiveB] = "Armor B",
				}
			};
			var json = JsonSerializer.Serialize(archiveHashes);

			var paths = new StoragePaths(appRoot);
			var indexService = new AssetArchiveIndexService(paths);
			await indexService.BuildOrRebuildAsync(gameData, json);

            var filter = new IndexFilterSettings(IndexFilterMode.Percentage, PercentageThreshold: 0.8, AbsoluteThreshold: null);

			// Vote using a set including a shared key and unique key.
			// shared df=2/2=1.0 => filtered, unique df=1/2=0.5 => not filtered.
			var votes = await indexService.VoteArchivesAsync(new HashSet<AssetKey>
			{
				new AssetKey(10, 100),
				new AssetKey(20, 200),
			}, filter);

			Assert.Single(votes);
			Assert.True(votes.ContainsKey(archiveA));
			Assert.Equal(1, votes[archiveA]);
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}

	[Fact]
	public async Task Build_DuplicateArchiveHashAcrossCategories_IsIndexedOnce()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		var appRoot = Path.Combine(root, "app");
		var gameData = Path.Combine(root, "game", "data");
		Directory.CreateDirectory(appRoot);
		Directory.CreateDirectory(gameData);

		try
		{
			const string archiveId = "bf2250de0b17285c";
			File.WriteAllBytes(Path.Combine(gameData, archiveId), BuildToc(new[] { new AssetKey(10, 100) }));
			var archiveHashes = new Dictionary<string, Dictionary<string, string>>
			{
				["Pistol"] = new() { [archiveId] = "P-2 Peacemaker" },
				["Mag"] = new() { [archiveId] = "P-2 Peacemaker" },
			};

			var service = new AssetArchiveIndexService(new StoragePaths(appRoot));
			await service.BuildOrRebuildAsync(gameData, JsonSerializer.Serialize(archiveHashes));

			var fingerprint = await service.GetFingerprintAsync();
			Assert.NotNull(fingerprint);
			Assert.Equal(1, fingerprint.ArchivesTotal);
			Assert.Equal(1, fingerprint.ArchivesIndexed);
			var matches = await service.FindAssetArchivesAsync(new HashSet<AssetKey> { new(10, 100) });
			var match = Assert.Single(matches);
			var archive = Assert.Single(match.Archives);
			// The public index exposes the stable semantic class rather than the
			// duplicate community labels that happened to mention Mag and Pistol.
			Assert.Equal("Weapon", archive.Category);
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}

	[Fact]
	public async Task BuildAndVote_AbsoluteFilter_FiltersHighDfKeys()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		var appRoot = Path.Combine(root, "app");
		var gameData = Path.Combine(root, "game", "data");
		Directory.CreateDirectory(appRoot);
		Directory.CreateDirectory(gameData);

		try
		{
			var archiveA = "aaaaaaaaaaaaaaaa";
			var archiveB = "bbbbbbbbbbbbbbbb";
			File.WriteAllBytes(Path.Combine(gameData, archiveA), BuildToc(new[]
			{
				new AssetKey(10, 100), // shared df=2
				new AssetKey(20, 200), // only in A df=1
			}));
			File.WriteAllBytes(Path.Combine(gameData, archiveB), BuildToc(new[]
			{
				new AssetKey(10, 100), // shared df=2
			}));

			var archiveHashes = new Dictionary<string, Dictionary<string, string>>
			{
				["Armor"] = new Dictionary<string, string>
				{
					[archiveA] = "Armor A",
					[archiveB] = "Armor B",
				}
			};
			var json = JsonSerializer.Serialize(archiveHashes);

			var paths = new StoragePaths(appRoot);
			var indexService = new AssetArchiveIndexService(paths);
			await indexService.BuildOrRebuildAsync(gameData, json);

			var filter = new IndexFilterSettings(IndexFilterMode.AbsoluteCount, PercentageThreshold: null, AbsoluteThreshold: 1);

			var votes = await indexService.VoteArchivesAsync(new HashSet<AssetKey>
			{
				new AssetKey(10, 100),
				new AssetKey(20, 200),
			}, filter);

			Assert.Single(votes);
			Assert.True(votes.ContainsKey(archiveA));
			Assert.Equal(1, votes[archiveA]);
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}

	[Fact]
	public async Task Build_IndexFingerprintAndAssetLookup_ArePersisted()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		var appRoot = Path.Combine(root, "app");
		var gameData = Path.Combine(root, "game", "data");
		Directory.CreateDirectory(appRoot);
		Directory.CreateDirectory(gameData);

		try
		{
			var archiveA = "aaaaaaaaaaaaaaaa";
			File.WriteAllBytes(Path.Combine(gameData, archiveA), BuildToc(new[]
			{
				new AssetKey(20, 200),
			}));

			var archiveHashes = new Dictionary<string, Dictionary<string, string>>
			{
				["Armor"] = new Dictionary<string, string>
				{
					[archiveA] = "Armor A",
					["missingmissing00"] = "Missing Archive",
				}
			};
			var json = JsonSerializer.Serialize(archiveHashes);

			var paths = new StoragePaths(appRoot);
			var indexService = new AssetArchiveIndexService(paths);
			await indexService.BuildOrRebuildAsync(gameData, json);

			var fingerprint = await indexService.GetFingerprintAsync();
			Assert.NotNull(fingerprint);
			// Installed files, not stale community records, define the index inventory.
			Assert.Equal(1, fingerprint.ArchivesTotal);
			Assert.Equal(1, fingerprint.ArchivesIndexed);
			Assert.Equal(1, fingerprint.AssetKeysTotal);
			Assert.False(string.IsNullOrWhiteSpace(fingerprint.SourceFingerprint));

			var matches = await indexService.FindAssetArchivesAsync(new HashSet<AssetKey>
			{
				new AssetKey(20, 200),
				new AssetKey(99, 999),
			});

			Assert.Equal(2, matches.Count);
			var found = Assert.Single(matches, x => x.Found);
			Assert.Equal(new AssetKey(20, 200), found.AssetKey);
			var archive = Assert.Single(found.Archives);
			Assert.Equal(archiveA, archive.ArchiveId);
			Assert.Equal("Armor", archive.Category);
			Assert.Equal("Armor A", archive.DisplayName);
			Assert.Single(matches, x => !x.Found);
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}

	[Fact]
	public async Task GetArchiveDetailsAsync_ReturnsAssetsAndSharedArchives()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		var appRoot = Path.Combine(root, "app");
		var gameData = Path.Combine(root, "game", "data");
		Directory.CreateDirectory(appRoot);
		Directory.CreateDirectory(gameData);
		try
		{
			const string archiveA = "aaaaaaaaaaaaaaaa";
			const string archiveB = "bbbbbbbbbbbbbbbb";
			var shared = new AssetKey(20, 200);
			File.WriteAllBytes(Path.Combine(gameData, archiveA), BuildToc(new[] { shared }));
			File.WriteAllBytes(Path.Combine(gameData, archiveB), BuildToc(new[] { shared }));
			var json = JsonSerializer.Serialize(new Dictionary<string, Dictionary<string, string>>
			{
				["Armor"] = new() { [archiveA] = "Armor A", [archiveB] = "Armor B" }
			});
			var service = new AssetArchiveIndexService(new StoragePaths(appRoot));
			await service.BuildOrRebuildAsync(gameData, json);

			var details = await service.GetArchiveDetailsAsync(archiveA);

			Assert.NotNull(details);
			var asset = Assert.Single(details.Assets);
			Assert.Equal(shared, asset.AssetKey);
			Assert.Equal(archiveB, Assert.Single(asset.SharedPackages));
			Assert.Equal("Armor B", Assert.Single(asset.SharedDisplayNames));
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}

	[Fact]
	public async Task GetUnitPartFactsAsync_ReturnsNoFactsForEmptyUnitKeySet()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		var appRoot = Path.Combine(root, "app");
		var gameData = Path.Combine(root, "game", "data");
		Directory.CreateDirectory(appRoot);
		Directory.CreateDirectory(gameData);
		try
		{
			const string archive = "aaaaaaaaaaaaaaaa";
			File.WriteAllBytes(Path.Combine(gameData, archive), BuildToc(Array.Empty<AssetKey>()));
			var json = JsonSerializer.Serialize(new Dictionary<string, Dictionary<string, string>> { ["Armor"] = new() { [archive] = "Armor A" } });
			var service = new AssetArchiveIndexService(new StoragePaths(appRoot));
			await service.BuildOrRebuildAsync(gameData, json);

			var parts = await service.GetUnitPartFactsAsync(new HashSet<AssetKey>());

			Assert.Empty(parts);
		}
		finally { try { Directory.Delete(root, recursive: true); } catch { } }
	}

	// 基础索引重建会清空整库，而 `game_data_unit_parts` 与 `game_data_stream_layouts`
	// 只有高级装备索引会写——基础索引自己一行都不产生。所以这两张表必须原样搬过重建。
	// 以前只搬了部位表：布局表被删掉之后 `IsCurrentAsync` 永远为假，用户点"补齐缺失"
	// 每次都在全量重算（本机实测布局表 0 行就是这么来的）。这条用例把两张表一起钉住。
	[Fact]
	public async Task Rebuild_PreservesUnitPartsAndStreamLayoutsThatTheBaseBuildCannotReproduce()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		var appRoot = Path.Combine(root, "app");
		var gameData = Path.Combine(root, "game", "data");
		Directory.CreateDirectory(appRoot);
		Directory.CreateDirectory(gameData);
		try
		{
			const string archive = "aaaaaaaaaaaaaaaa";
			const long unitFileId = 0x1234;
			var unitKey = new AssetKey(PatchUnitMeshReader.UnitTypeId, unitFileId);
			File.WriteAllBytes(Path.Combine(gameData, archive), BuildToc([unitKey]));
			var json = JsonSerializer.Serialize(new Dictionary<string, Dictionary<string, string>> { ["Armor"] = new() { [archive] = "Armor A" } });
			var paths = new StoragePaths(appRoot);
			var service = new AssetArchiveIndexService(paths);
			await service.BuildOrRebuildAsync(gameData, json);

			var unitTypeId = unchecked((long)unitKey.TypeId);
			await using (var connection = new SqliteConnection($"Data Source={paths.DbPath};Pooling=False"))
			{
				await connection.OpenAsync();
				// 分类必须真的是 Armor，否则恢复时的 `category LIKE '%armor%'` 会（正确地）
				// 丢掉这两行，用例就变成了在验证别的东西。
				Assert.Equal("Armor", await ScalarTextAsync(connection, $"SELECT category FROM archives WHERE archive_id='{archive}'"));
				await ExecuteSqlAsync(connection, $"INSERT INTO game_data_unit_parts(archive_id,unit_type_id,unit_file_id,mesh_info_index,mesh_id,part_kind,part_layer,body_variant,semantic_name,confidence,is_visual,is_lod,vertex_count,triangle_count,geometry_quality,fact_origin,piece_type,reason) VALUES('{archive}',{unitTypeId},{unitFileId},0,1,1,1,1,'Torso',100,1,0,3,1,1,1,'2','test');");
				await ExecuteSqlAsync(connection, $"INSERT INTO game_data_stream_layouts(archive_id,unit_type_id,unit_file_id,stream_index,component_info_id,unit_version,vertex_stride,components_json,layout_signature,is_skinned) VALUES('{archive}',{unitTypeId},{unitFileId},0,1,1,32,'[]','test-layout',1);");
			}

			await service.BuildOrRebuildAsync(gameData, json);

			await using (var connection = new SqliteConnection($"Data Source={paths.DbPath};Pooling=False"))
			{
				await connection.OpenAsync();
				Assert.Equal(1L, await CountAsync(connection, "game_data_unit_parts"));
				Assert.Equal(1L, await CountAsync(connection, "game_data_stream_layouts"));
			}
		}
		finally { try { Directory.Delete(root, recursive: true); } catch { } }
	}

	private static async Task ExecuteSqlAsync(SqliteConnection connection, string sql)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = sql;
		await command.ExecuteNonQueryAsync();
	}

	private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = sql;
		return await command.ExecuteScalarAsync();
	}

	private static async Task<string?> ScalarTextAsync(SqliteConnection connection, string sql)
		=> (string?)await ScalarAsync(connection, sql);

	private static async Task<long> CountAsync(SqliteConnection connection, string table)
		=> Convert.ToInt64(await ScalarAsync(connection, $"SELECT count(*) FROM {table};"), System.Globalization.CultureInfo.InvariantCulture);

	[Fact]
	public async Task GetIndexStatusAsync_DetectsCurrentStaleAndMissingIndex()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		var appRoot = Path.Combine(root, "app");
		var missingAppRoot = Path.Combine(root, "missing-app");
		var gameData = Path.Combine(root, "game", "data");
		Directory.CreateDirectory(appRoot);
		Directory.CreateDirectory(missingAppRoot);
		Directory.CreateDirectory(gameData);

		try
		{
			var archiveA = "aaaaaaaaaaaaaaaa";
			var archivePath = Path.Combine(gameData, archiveA);
			File.WriteAllBytes(archivePath, BuildToc(new[]
			{
				new AssetKey(20, 200),
			}));

			var archiveHashes = new Dictionary<string, Dictionary<string, string>>
			{
				["Armor"] = new Dictionary<string, string>
				{
					[archiveA] = "Armor A",
				}
			};
			var json = JsonSerializer.Serialize(archiveHashes);

			var indexService = new AssetArchiveIndexService(new StoragePaths(appRoot));
			await indexService.BuildOrRebuildAsync(gameData, json);

			var current = await indexService.GetIndexStatusAsync(gameData, json);
			Assert.Equal(GameDataIndexState.Current, current.State);
			Assert.True(current.IsCurrent);
			Assert.NotNull(current.StoredFingerprint);
			Assert.False(string.IsNullOrWhiteSpace(current.CurrentSourceFingerprint));

			await using (var stream = new FileStream(archivePath, FileMode.Append, FileAccess.Write, FileShare.Read))
			{
				await stream.WriteAsync(new byte[] { 1, 2, 3, 4 });
			}
			var stale = await indexService.GetIndexStatusAsync(gameData, json);
			Assert.Equal(GameDataIndexState.Stale, stale.State);
			Assert.False(stale.IsCurrent);
			Assert.NotEqual(current.CurrentSourceFingerprint, stale.CurrentSourceFingerprint);

			var missingIndexService = new AssetArchiveIndexService(new StoragePaths(missingAppRoot));
			var missing = await missingIndexService.GetIndexStatusAsync(gameData, json);
			Assert.Equal(GameDataIndexState.Missing, missing.State);
			Assert.Null(missing.StoredFingerprint);
			Assert.False(string.IsNullOrWhiteSpace(missing.CurrentSourceFingerprint));
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}

	[Fact]
	public async Task GetIndexStatusAsync_IgnoresDeployedPatchAndActivationStateFiles()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		var appRoot = Path.Combine(root, "app");
		var gameData = Path.Combine(root, "game", "data");
		Directory.CreateDirectory(appRoot); Directory.CreateDirectory(gameData);
		try
		{
			const string archive = "aaaaaaaaaaaaaaaa";
			File.WriteAllBytes(Path.Combine(gameData, archive), BuildToc(new[] { new AssetKey(20, 200) }));
			var json = JsonSerializer.Serialize(new Dictionary<string, Dictionary<string, string>> { ["Armor"] = new() { [archive] = "Armor A" } });
			var service = new AssetArchiveIndexService(new StoragePaths(appRoot));
			await service.BuildOrRebuildAsync(gameData, json);

			File.WriteAllBytes(Path.Combine(gameData, $"{archive}.patch_0"), BuildToc(new[] { new AssetKey(20, 200) }));
			File.WriteAllText(Path.Combine(gameData, "activation-state.json"), "{}");
			Assert.Equal(GameDataIndexState.Current, (await service.GetIndexStatusAsync(gameData, json)).State);

			File.Delete(Path.Combine(gameData, $"{archive}.patch_0"));
			File.Delete(Path.Combine(gameData, "activation-state.json"));
			Assert.Equal(GameDataIndexState.Current, (await service.GetIndexStatusAsync(gameData, json)).State);
		}
		finally { try { Directory.Delete(root, recursive: true); } catch { } }
	}

	[Fact]
	public async Task Build_BundledSlimArchive_IsIndexed()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		var appRoot = Path.Combine(root, "app");
		var gameData = Path.Combine(root, "game", "data");
		Directory.CreateDirectory(appRoot);
		Directory.CreateDirectory(gameData);

		try
		{
			var archiveA = "aaaaaaaaaaaaaaaa";
			var toc = BuildSlimToc(new[]
			{
				new AssetKey(20, 200),
			});
			File.WriteAllBytes(Path.Combine(gameData, "bundles.nxa"), BuildDsar(BuildBundleDatabase(archiveA, toc.Length)));
			File.WriteAllBytes(Path.Combine(gameData, "bundles.00.nxa"), BuildDsar(toc));

			var archiveHashes = new Dictionary<string, Dictionary<string, string>>
			{
				["Armor"] = new Dictionary<string, string>
				{
					[archiveA] = "Armor A",
				}
			};
			var json = JsonSerializer.Serialize(archiveHashes);

			var paths = new StoragePaths(appRoot);
			var indexService = new AssetArchiveIndexService(paths);
			await indexService.BuildOrRebuildAsync(gameData, json);

			var fingerprint = await indexService.GetFingerprintAsync();
			Assert.NotNull(fingerprint);
			Assert.Equal(1, fingerprint.ArchivesTotal);
			Assert.Equal(1, fingerprint.ArchivesIndexed);
			Assert.Equal(1, fingerprint.AssetKeysTotal);

			var matches = await indexService.FindAssetArchivesAsync(new HashSet<AssetKey>
			{
				new AssetKey(20, 200),
			});

			var found = Assert.Single(matches);
			Assert.True(found.Found);
			Assert.Equal(archiveA, Assert.Single(found.Archives).ArchiveId);
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}

	[Fact]
	public async Task GameDataPackageResolver_BundledResource_ReadsFromReconstructedPackageSlice()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		var gameData = Path.Combine(root, "game", "data");
		Directory.CreateDirectory(gameData);

		try
		{
			var archiveA = "aaaaaaaaaaaaaaaa";
			var payload = new byte[128];
			for (var i = 0; i < payload.Length; i++)
			{
				payload[i] = (byte)(i + 1);
			}

			File.WriteAllBytes(Path.Combine(gameData, "bundles.nxa"), BuildDsar(BuildBundleDatabase(archiveA, payload.Length)));
			File.WriteAllBytes(Path.Combine(gameData, "bundles.00.nxa"), BuildDsar(payload));

			var resolver = new GameDataPackageResolver(gameData);
			var data = await resolver.GetPackageResourceAsync(archiveA, 80, 16);

			Assert.Equal(payload.AsSpan(80, 16).ToArray(), data);
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}

	[Fact]
	public async Task GameDataPackageResolvers_BundledSidecar_ReadsHighBitVirtualOffset()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		var gameData = Path.Combine(root, "game", "data");
		Directory.CreateDirectory(gameData);
		try
		{
			const string archive = "aaaaaaaaaaaaaaaa";
			const ulong virtualOffset = 0x803fbb8f;
			var payload = new byte[] { 7, 11, 19, 23, 31 };
			File.WriteAllBytes(
				Path.Combine(gameData, "bundles.nxa"),
				BuildDsar(BuildBundleDatabase($"{archive}.gpu_resources", payload.Length, virtualOffset)));
			File.WriteAllBytes(Path.Combine(gameData, "bundles.00.nxa"), BuildDsar(payload, virtualOffset));

			var coreData = await new HD2ModCore.Infrastructure.GameDataPackageResolver(gameData)
				.GetPackageResourceAsync($"{archive}.gpu_resources", 0, (uint)payload.Length);
			var adaptationData = await new HD2ModAdaptation.PatchReconstruction.GameDataPackageResolver(gameData)
				.GetPackageResourceAsync($"{archive}.gpu_resources", 0, (uint)payload.Length);

			Assert.Equal(payload, coreData);
			Assert.Equal(payload, adaptationData);
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}

	[Fact]
	public async Task GameDataPackageResolver_PackageNames_IncludeDirectAndBundledArchives()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		var gameData = Path.Combine(root, "game", "data");
		Directory.CreateDirectory(gameData);

		try
		{
			var directArchive = "1111111111111111";
			var bundledArchive = "aaaaaaaaaaaaaaaa";
			File.WriteAllBytes(Path.Combine(gameData, directArchive), BuildToc(new[] { new AssetKey(20, 200) }));
			File.WriteAllBytes(Path.Combine(gameData, "bundles.nxa"), BuildDsar(BuildBundleDatabase(bundledArchive, 128)));
			File.WriteAllBytes(Path.Combine(gameData, "bundles.00.nxa"), BuildDsar(new byte[128]));

			var names = await new GameDataPackageResolver(gameData).GetPackageNamesAsync();

			Assert.Contains(directArchive, names, StringComparer.OrdinalIgnoreCase);
			Assert.Contains(bundledArchive, names, StringComparer.OrdinalIgnoreCase);
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}

	[Fact]
	public async Task ModCompatibilityAnalyzer_ClassifiesByCurrentIndexMatches()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		var appRoot = Path.Combine(root, "app");
		var gameData = Path.Combine(root, "game", "data");
		Directory.CreateDirectory(appRoot);
		Directory.CreateDirectory(gameData);

		try
		{
			var archiveA = "aaaaaaaaaaaaaaaa";
			File.WriteAllBytes(Path.Combine(gameData, archiveA), BuildToc(new[]
			{
				new AssetKey(10, 100),
				new AssetKey(20, 200),
			}));

			var archiveHashes = new Dictionary<string, Dictionary<string, string>>
			{
				["Armor"] = new Dictionary<string, string>
				{
					[archiveA] = "Armor A",
				}
			};
			var json = JsonSerializer.Serialize(archiveHashes);

			var paths = new StoragePaths(appRoot);
			var indexService = new AssetArchiveIndexService(paths);
			await indexService.BuildOrRebuildAsync(gameData, json);

			var summary = new ModAssetSummary(
				new ModNodeId(Guid.NewGuid()),
				"Old Mod",
				new[]
				{
					BuildAssetEntry(archiveA, new AssetKey(10, 100)),
					BuildAssetEntry(archiveA, new AssetKey(20, 200)),
					BuildAssetEntry(archiveA, new AssetKey(30, 300)),
					BuildAssetEntry(archiveA, new AssetKey(40, 400)),
				},
				Array.Empty<string>(),
				Array.Empty<ModAssetTargetGroup>());

			var analyzer = new ModCompatibilityAnalyzer(indexService);
			var report = await analyzer.AnalyzeAsync(summary);

			Assert.Equal(ModCompatibilityStatus.Partial, report.Status);
			Assert.Equal(4, report.TotalAssets);
			Assert.Equal(2, report.MatchedAssets);
			Assert.Equal(2, report.MissingAssets);
			Assert.Equal(0.5, report.MatchRatio);
			Assert.True(report.HasIndex);
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}

	private static byte[] BuildToc(AssetKey[] entries)
	{
		const uint magic = 4026531857;
		var numTypes = 0;
		var numFiles = entries.Length;
		var entriesOffset = 60 + numTypes * 32;
		var totalSize = entriesOffset + numFiles * 80;
		var buffer = new byte[totalSize];

		WriteUInt32(buffer, 0, magic);
		WriteUInt32(buffer, 4, (uint)numTypes);
		WriteUInt32(buffer, 8, (uint)numFiles);

		var offset = entriesOffset;
		foreach (var e in entries)
		{
			WriteUInt64(buffer, offset, e.FileId);
			WriteUInt64(buffer, offset + 8, e.TypeId);
			offset += 80;
		}

		return buffer;
	}

	private static byte[] BuildSlimToc(AssetKey[] entries)
	{
		const uint magic = 4026531857;
		var numTypes = 0;
		var numFiles = entries.Length;
		var entriesOffset = 72 + numTypes * 32;
		var totalSize = entriesOffset + numFiles * 80;
		var buffer = new byte[totalSize];

		WriteUInt32(buffer, 0, magic);
		WriteUInt32(buffer, 4, (uint)numTypes);
		WriteUInt32(buffer, 8, (uint)numFiles);

		var offset = entriesOffset;
		foreach (var e in entries)
		{
			WriteUInt64(buffer, offset, e.FileId);
			WriteUInt64(buffer, offset + 8, e.TypeId);
			offset += 80;
		}

		return buffer;
	}

	private static byte[] BuildBundleDatabase(string archiveId, int packageSize, ulong bundleOffset = 0, byte bundleIndex = 0)
	{
		var data = new byte[0x90];
		WriteUInt32(data, 0x0C, 1); // num bundles
		WriteUInt32(data, 0x10, 1); // num packages
		WriteUInt64(data, 0x18, (ulong)packageSize);
		WriteUInt32(data, 0x20, 0x40); // name offset
		WriteUInt32(data, 0x24, 1); // items count
		WriteUInt32(data, 0x28, 0x60); // items offset
		var nameBytes = System.Text.Encoding.ASCII.GetBytes(archiveId);
		Array.Copy(nameBytes, 0, data, 0x40, nameBytes.Length);
		WriteUInt64(data, 0x60, 0); // original archive offset
		WriteUInt32(data, 0x68, checked((uint)bundleOffset)); // uncompressed bundle offset
		data[0x6F] = bundleIndex; // bundle index
		return data;
	}

	private static byte[] BuildDsar(byte[] payload, ulong uncompressedOffset = 0)
	{
		var dataOffset = 0x40;
		var buffer = new byte[dataOffset + payload.Length];
		WriteUInt32(buffer, 0, 0x52415344);
		WriteUInt32(buffer, 8, 1);
		WriteUInt64(buffer, 0x20, uncompressedOffset); // uncompressed offset
		WriteUInt64(buffer, 0x28, (ulong)dataOffset); // compressed offset
		WriteUInt32(buffer, 0x30, (uint)payload.Length); // uncompressed size
		WriteUInt32(buffer, 0x34, (uint)payload.Length); // compressed size
		buffer[0x38] = 0; // uncompressed
		buffer[0x39] = 2; // start chunk
		Array.Copy(payload, 0, buffer, dataOffset, payload.Length);
		return buffer;
	}

	private static PatchAssetEntry BuildAssetEntry(string archiveId, AssetKey key)
		=> new(
			new PatchAssetKey(archiveId, key.TypeId, key.FileId),
			"Archive",
			"Armor",
			0,
			0,
			"File",
			"unit",
			AssetTypeCategory.Model,
			Array.Empty<string>(),
			Array.Empty<string>());

	private static void WriteUInt32(byte[] buffer, int offset, uint value)
	{
		buffer[offset + 0] = (byte)(value & 0xFF);
		buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
		buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
		buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
	}

	private static void WriteUInt64(byte[] buffer, int offset, ulong value)
	{
		buffer[offset + 0] = (byte)(value & 0xFF);
		buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
		buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
		buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
		buffer[offset + 4] = (byte)((value >> 32) & 0xFF);
		buffer[offset + 5] = (byte)((value >> 40) & 0xFF);
		buffer[offset + 6] = (byte)((value >> 48) & 0xFF);
		buffer[offset + 7] = (byte)((value >> 56) & 0xFF);
	}

	private sealed class FixedAuthorityProvider(GameDataAuthoritySnapshot snapshot) : HD2ModCore.Application.IGameDataAuthorityProvider
	{
		public ValueTask<GameDataAuthoritySnapshot> LoadAsync(
			string gameDataDirectory,
			CancellationToken cancellationToken = default,
			bool refreshPlaintextCache = false)
			=> ValueTask.FromResult(snapshot);
	}
}
