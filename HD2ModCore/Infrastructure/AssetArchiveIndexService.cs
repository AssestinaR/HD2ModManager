using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HD2ModAdaptation.Analysis;
using HD2ModAdaptation.PatchReconstruction;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModCore.Application;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure.ArchiveHashes;
using HD2ModCore.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;
using CoreAssetKey = HD2ModCore.Domain.AssetKey;

namespace HD2ModCore.Infrastructure;

// 作用：使用 SQLite 构建并查询持久化的 (TypeID, FileID) -> ArchiveId 反向索引，用于推导替换目标与冲突分析。
// Purpose: Builds and queries a persisted (TypeID, FileID) -> ArchiveId reverse index using SQLite.
public sealed class AssetArchiveIndexService : IAssetArchiveIndexService
{
	private readonly StoragePaths _paths;
	private readonly IGameDataArchiveIndexer _archiveIndexer;
	private readonly IGameDataAuthorityProvider _authorityProvider;

	public AssetArchiveIndexService(
		StoragePaths paths,
		IGameDataArchiveIndexer? archiveIndexer = null,
		IGameDataAuthorityProvider? authorityProvider = null,
		string? registryRoot = null)
	{
		_paths = paths ?? throw new ArgumentNullException(nameof(paths));
		_archiveIndexer = archiveIndexer ?? new GameDataArchiveIndexer();
		// 建索引是**唯一**允许读游戏注册表原件（及其备份）的地方，所以"备份在哪里"
		// 必须在这里显式给定：靠默认解析一旦和护甲映射服务用的根不一致，就会静默
		// 读到被真映射改过的文件，并把错误数据固化进数据库。
		_authorityProvider = authorityProvider
			?? new GameDataAuthorityProvider(ReadConfiguredPreferredLocale(paths), registryRoot);
	}

	private static string? ReadConfiguredPreferredLocale(StoragePaths paths)
	{
		try
		{
			if (!File.Exists(paths.SettingsPath)) return null;
			using var document = JsonDocument.Parse(File.ReadAllText(paths.SettingsPath));
			if (!document.RootElement.TryGetProperty("Language", out var language)
				|| language.ValueKind != JsonValueKind.String)
			{
				return null;
			}
			return string.IsNullOrWhiteSpace(language.GetString()) ? null : language.GetString();
		}
		catch (IOException) { return null; }
		catch (UnauthorizedAccessException) { return null; }
		catch (JsonException) { return null; }
	}

	public ValueTask<bool> IndexExistsAsync(CancellationToken cancellationToken = default)
		=> ValueTask.FromResult(File.Exists(_paths.DbPath));

	public async ValueTask<GameDataIndexStatus> GetIndexStatusAsync(
		string gameDataDirectory,
		string archiveHashesJson,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(gameDataDirectory))
		{
			throw new ArgumentException("Value cannot be null or whitespace.", nameof(gameDataDirectory));
		}

		var normalizedGameDataDirectory = Path.GetFullPath(gameDataDirectory);
		if (!Directory.Exists(normalizedGameDataDirectory))
		{
			throw new DirectoryNotFoundException($"GameData directory does not exist: {normalizedGameDataDirectory}");
		}

		var authority = await _authorityProvider.LoadAsync(normalizedGameDataDirectory, cancellationToken).ConfigureAwait(false);
		var currentFingerprint = await ComputeSourceFingerprintAsync(
			normalizedGameDataDirectory,
			authority,
			cancellationToken).ConfigureAwait(false);
		var stored = await GetFingerprintAsync(cancellationToken).ConfigureAwait(false);
		if (stored is null)
		{
			// A missing database still has a concrete GameData + archive metadata
			// identity.  Exposing it lets plan-gated callers safely build an index
			// from scratch and detect drift before apply.
			return new GameDataIndexStatus(GameDataIndexState.Missing, null, Path.GetFullPath(gameDataDirectory), currentFingerprint);
		}
		var state = string.Equals(stored.SourceFingerprint, currentFingerprint, StringComparison.OrdinalIgnoreCase)
			? GameDataIndexState.Current
			: GameDataIndexState.Stale;

		return new GameDataIndexStatus(state, stored, Path.GetFullPath(gameDataDirectory), currentFingerprint);
	}

	public async ValueTask<GameDataIndexFingerprint?> GetFingerprintAsync(CancellationToken cancellationToken = default)
	{
		if (!File.Exists(_paths.DbPath))
		{
			return null;
		}

		await using var connection = new SqliteConnection($"Data Source={_paths.DbPath};Mode=ReadOnly;Cache=Shared");
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

		var builtUtcText = await SqliteSchema.GetMetaAsync(connection, "built_utc", cancellationToken).ConfigureAwait(false);
		if (!DateTimeOffset.TryParse(builtUtcText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var builtUtc))
		{
			return null;
		}

		return new GameDataIndexFingerprint(
			await SqliteSchema.GetMetaAsync(connection, "game_data_directory", cancellationToken).ConfigureAwait(false) ?? string.Empty,
			builtUtc,
			ParseInt(await SqliteSchema.GetMetaAsync(connection, "archives_total", cancellationToken).ConfigureAwait(false)),
			ParseInt(await SqliteSchema.GetMetaAsync(connection, "archives_indexed", cancellationToken).ConfigureAwait(false)),
			ParseInt(await SqliteSchema.GetMetaAsync(connection, "asset_keys_total", cancellationToken).ConfigureAwait(false)),
			await SqliteSchema.GetMetaAsync(connection, "source_fingerprint", cancellationToken).ConfigureAwait(false) ?? string.Empty);
	}

	// 作用：摘要缓存（按"索引库路径"键控，指纹判新鲜，重建时显式作废）。
	//
	// 为什么必须有：这条查询是 archives LEFT JOIN archive_entries（35 万行）的 GROUP BY，
	// 外加每个归档一次 archive_issues 的 EXISTS。实测 ~215ms，而且**每次**打开护甲页
	// 都会跑一遍——信息中心的无节点事实账本只做并发合并、做完就移除（见
	// docs/armor-registry-page-perf-20261027.md §1 A2/A3）。
	//
	// 为什么可以缓存：摘要只取决于索引库本身的内容，而索引库由 source_fingerprint
	// 唯一标识。指纹不变，聚合结果就不可能变。
	//
	// 但**只**看指纹不够：展示名与分类来自 friendlynames / archivehashes，
	// 它们不在指纹里。只换了命名数据的一次重建会得到完全相同的指纹，
	// 于是重建路径必须无条件作废（见 BuildOrRebuildAsync 里的 TryRemove）。
	private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ArchiveSummaryCacheEntry> SummaryCache =
		new(StringComparer.OrdinalIgnoreCase);

	private readonly record struct ArchiveSummaryCacheEntry(string Fingerprint, IReadOnlyList<GameDataArchiveSummary> Summaries);

	public async ValueTask<IReadOnlyList<GameDataArchiveSummary>> GetArchiveSummariesAsync(CancellationToken cancellationToken = default)
	{
		if (!File.Exists(_paths.DbPath))
		{
			return Array.Empty<GameDataArchiveSummary>();
		}

		// 指纹是 5 次 meta 单行读，比下面那条聚合便宜两个数量级。
		// 拿不到指纹（索引半成品）时不做缓存，直接读——那种状态下"能读到什么"本身就不确定。
		var fingerprint = await GetFingerprintAsync(cancellationToken).ConfigureAwait(false);
		if (fingerprint is not null
			&& SummaryCache.TryGetValue(_paths.DbPath, out var cached)
			&& string.Equals(cached.Fingerprint, fingerprint.SourceFingerprint, StringComparison.OrdinalIgnoreCase))
		{
			return cached.Summaries;
		}

		var summaries = await ReadArchiveSummariesAsync(cancellationToken).ConfigureAwait(false);
		if (fingerprint is not null)
			SummaryCache[_paths.DbPath] = new ArchiveSummaryCacheEntry(fingerprint.SourceFingerprint, summaries);
		return summaries;
	}

	private async ValueTask<IReadOnlyList<GameDataArchiveSummary>> ReadArchiveSummariesAsync(CancellationToken cancellationToken)
	{
		await using var connection = new SqliteConnection($"Data Source={_paths.DbPath};Mode=ReadOnly;Cache=Shared");
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using var command = connection.CreateCommand();
		command.CommandText = @"
SELECT a.archive_id, a.display_name, a.category, COUNT(e.entry_index) AS entry_count,
	   CASE WHEN EXISTS (SELECT 1 FROM archive_issues i WHERE i.archive_id = a.archive_id)
			THEN '存在问题' ELSE '已索引' END AS status
FROM archives a
LEFT JOIN archive_entries e ON e.archive_id = a.archive_id
GROUP BY a.archive_id, a.display_name, a.category
ORDER BY a.archive_id;";

		var result = new List<GameDataArchiveSummary>();
		await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			result.Add(new GameDataArchiveSummary(
				reader.GetString(0),
				reader.GetString(1),
				reader.GetString(2),
				Convert.ToInt32(reader.GetInt64(3), CultureInfo.InvariantCulture),
				reader.GetString(4)));
		}

		return result;
	}

	public async ValueTask<GameDataArchiveDetails?> GetArchiveDetailsAsync(string packageName, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(packageName);
		if (!File.Exists(_paths.DbPath)) return null;

		var catalog = await new FileSystemAssetMetadataCatalogProvider(_paths).LoadAsync(cancellationToken).ConfigureAwait(false);
		await using var connection = new SqliteConnection($"Data Source={_paths.DbPath};Mode=ReadOnly;Cache=Shared");
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

		await using var summaryCommand = connection.CreateCommand();
		summaryCommand.CommandText = @"
SELECT a.archive_id, a.display_name, a.category, COUNT(e.entry_index),
       CASE WHEN EXISTS (SELECT 1 FROM archive_issues i WHERE i.archive_id = a.archive_id)
            THEN '存在问题' ELSE '已索引' END
FROM archives a
LEFT JOIN archive_entries e ON e.archive_id = a.archive_id
WHERE a.archive_id = $package
GROUP BY a.archive_id, a.display_name, a.category;";
		summaryCommand.Parameters.AddWithValue("$package", packageName);
		await using var summaryReader = await summaryCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		if (!await summaryReader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
		var summary = new GameDataArchiveSummary(
			summaryReader.GetString(0), summaryReader.GetString(1), summaryReader.GetString(2),
			Convert.ToInt32(summaryReader.GetInt64(3), CultureInfo.InvariantCulture), summaryReader.GetString(4));
		await summaryReader.DisposeAsync().ConfigureAwait(false);

		var keys = new List<CoreAssetKey>();
		await using var entriesCommand = connection.CreateCommand();
		entriesCommand.CommandText = @"
SELECT type_id, file_id
FROM archive_entries
WHERE archive_id = $package
ORDER BY type_id, file_id;";
		entriesCommand.Parameters.AddWithValue("$package", packageName);
		await using var entriesReader = await entriesCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await entriesReader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			keys.Add(new CoreAssetKey(unchecked((ulong)entriesReader.GetInt64(0)), unchecked((ulong)entriesReader.GetInt64(1))));
		}
		await entriesReader.DisposeAsync().ConfigureAwait(false);

		var unitKeys = keys.Where(key => key.TypeId == HD2ModAdaptation.PatchReconstruction.UnitMesh.PatchUnitMeshReader.UnitTypeId).ToHashSet();
		var partsByUnit = await GetUnitPartFactsAsync(unitKeys, cancellationToken).ConfigureAwait(false);
		var assets = new List<GameDataArchiveAssetEntry>(keys.Count);
		foreach (var key in keys)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var shared = await GetSharedArchivesAsync(connection, summary.PackageName, key.TypeId, key.FileId, cancellationToken).ConfigureAwait(false);
			var typeName = catalog.Types.TryGetValue(key.TypeId, out var type) ? type.Name : $"unknown ({key.TypeId:x16})";
			var friendlyName = catalog.Files.TryGetValue(key.FileId, out var file) ? file.FriendlyName : "—";
			assets.Add(new GameDataArchiveAssetEntry(
				key,
				typeName,
				friendlyName,
				partsByUnit.TryGetValue(key, out var parts) ? DescribeParts(parts) : "—",
				shared.Select(x => x.PackageName).ToArray(),
				shared.Select(x => x.DisplayName).ToArray()));
		}

		var issues = new List<CoreIssue>();
		await using var issuesCommand = connection.CreateCommand();
		issuesCommand.CommandText = "SELECT code, message FROM archive_issues WHERE archive_id = $package ORDER BY issue_id";
		issuesCommand.Parameters.AddWithValue("$package", packageName);
		await using var issuesReader = await issuesCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await issuesReader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			issues.Add(new CoreIssue(CoreIssueSeverity.Warning, issuesReader.GetString(0), issuesReader.GetString(1)));
		}

		return new GameDataArchiveDetails(summary, assets, issues);
	}

	public async ValueTask<IReadOnlyDictionary<CoreAssetKey, IReadOnlyList<GameDataUnitPartFact>>> GetUnitPartFactsAsync(IReadOnlySet<CoreAssetKey> unitAssetKeys, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(unitAssetKeys);
		if (unitAssetKeys.Count == 0 || !File.Exists(_paths.DbPath)) return new Dictionary<CoreAssetKey, IReadOnlyList<GameDataUnitPartFact>>();
		await using var connection = new SqliteConnection($"Data Source={_paths.DbPath};Mode=ReadOnly;Cache=Shared");
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using var command = connection.CreateCommand();
		command.CommandText = @"
SELECT archive_id,unit_type_id,unit_file_id,mesh_info_index,mesh_id,part_kind,part_layer,body_variant,semantic_name,piece_type,confidence,is_visual,is_lod,vertex_count,triangle_count,geometry_quality,fact_origin,reason
FROM game_data_unit_parts

WHERE unit_type_id=$unitType AND unit_file_id IN (SELECT value FROM json_each($fileIds))
ORDER BY unit_file_id,confidence DESC,mesh_info_index;";
		command.Parameters.AddWithValue("$unitType", unchecked((long)HD2ModAdaptation.PatchReconstruction.UnitMesh.PatchUnitMeshReader.UnitTypeId));
		command.Parameters.AddWithValue("$fileIds", JsonSerializer.Serialize(unitAssetKeys.Where(key => key.TypeId == HD2ModAdaptation.PatchReconstruction.UnitMesh.PatchUnitMeshReader.UnitTypeId).Select(key => unchecked((long)key.FileId))));
		var result = new Dictionary<CoreAssetKey, List<GameDataUnitPartFact>>();
		await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			var fact = new GameDataUnitPartFact(
				reader.GetString(0),
				new CoreAssetKey(unchecked((ulong)reader.GetInt64(1)), unchecked((ulong)reader.GetInt64(2))),
				reader.GetInt32(3),
				unchecked((uint)reader.GetInt64(4)),
				(UnitMeshPartKind)reader.GetInt32(5),
				(UnitMeshPartLayer)reader.GetInt32(6),
				(UnitMeshBodyVariant)reader.GetInt32(7),
				reader.GetString(8),
				reader.GetInt32(10),
				reader.GetInt32(11) != 0,
				reader.GetInt32(12) != 0,
				reader.GetString(17))
			{
				PieceType = reader.GetString(9),
				VertexCount = reader.GetInt32(13),
				TriangleCount = reader.GetInt32(14),
				GeometryQuality = (UnitMeshGeometryQuality)reader.GetInt32(15),
				Origin = (GameDataFactOrigin)reader.GetInt32(16)
			};
			if (!result.TryGetValue(fact.UnitAssetKey, out var parts))
			{
				parts = new List<GameDataUnitPartFact>();
				result[fact.UnitAssetKey] = parts;
			}
			parts.Add(fact);
		}
		return result.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<GameDataUnitPartFact>)pair.Value);
	}

	public async ValueTask BuildOrRebuildAsync(
		string gameDataDirectory,
		string archiveHashesJson,
		IProgress<IndexBuildProgress>? progress = null,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(gameDataDirectory))
		{
			throw new ArgumentException("Value cannot be null or whitespace.", nameof(gameDataDirectory));
		}

		var normalizedGameDataDirectory = Path.GetFullPath(gameDataDirectory);
		if (!Directory.Exists(normalizedGameDataDirectory))
		{
			throw new DirectoryNotFoundException($"GameData directory does not exist: {normalizedGameDataDirectory}");
		}

		Directory.CreateDirectory(_paths.IndexDirectory);

		var root = TryParseCommunityArchiveMetadata(archiveHashesJson);
		var authority = await _authorityProvider.LoadAsync(
			normalizedGameDataDirectory,
			cancellationToken,
			refreshPlaintextCache: true).ConfigureAwait(false);

		SqliteConnection.ClearAllPools();
		NormalizeExistingIndexFileAttributes();

		var archives = FlattenArchives(root);
		var metadataByPackage = BuildMetadataByPackage(archives, authority.BuildArchiveMetadata());
		var sourceFingerprint = await ComputeSourceFingerprintAsync(
			normalizedGameDataDirectory,
			authority,
			cancellationToken).ConfigureAwait(false);
		var archiveProgress = new Progress<GameDataArchiveIndexProgress>(item =>
			progress?.Report(new IndexBuildProgress(item.Current, Math.Max(item.Total, 1), $"{item.Stage}：{item.Item}")));
		var facts = await _archiveIndexer.BuildAsync(
			// Archive discovery must come from the installed GameData, not a community-maintained name list.
			new GameDataArchiveInput(normalizedGameDataDirectory, PackageNames: null, MetadataByPackageName: metadataByPackage, IncludeStreamLayouts: false),
			archiveProgress,
			cancellationToken).ConfigureAwait(false);
		if (facts.Archives.Count == 0)
		{
			throw new InvalidDataException($"No GameData archives were discovered in: {normalizedGameDataDirectory}");
		}

		// The generated Entity encyclopedia can name a weapon through its Unit,
		// but the Unit-to-Archive relationship is only known after the TOCs have
		// been scanned. Join those two authoritative facts now, then apply the
		// resolved metadata to the archive rows that will be persisted.
		authority = GameDataAuthorityArchiveNameResolver.Enrich(authority, facts);
		facts = ApplyArchiveMetadata(facts, BuildMetadataByPackage(archives, authority.BuildArchiveMetadata()));
		facts = ApplySemanticArchiveCategories(facts, authority);

		// 权威（注册表）与实体百科都覆盖不到的 archive，用内容路径补一个域和名字。
		// 只填空，不覆盖任何已有结论。
		var friendlyNames = await LoadFriendlyNamesAsync(cancellationToken).ConfigureAwait(false);
		var contentProfiles = BuildContentProfiles(facts, friendlyNames, BuildVehicleSkinArchives(authority));
		facts = ApplyContentProfiles(facts, contentProfiles);

		await using var connection = new SqliteConnection($"Data Source={_paths.DbPath};Pooling=False");
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await SqliteSchema.EnsureCreatedAsync(connection, cancellationToken).ConfigureAwait(false);

		await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
		await PreserveUnitPartFactsAsync(connection, tx, cancellationToken).ConfigureAwait(false);
		await SqliteSchema.ClearIndexDataAsync(connection, cancellationToken).ConfigureAwait(false);
		// 装备关系表只在**确实拿到新数据**时才重建。读不到游戏注册表（游戏更新换了格式、
		// 文件被改过、权限不足）时权威快照是空的；那一刻清空这张表，等于把已经正确的
		// 护甲列表、替换护甲的目标表一起删掉——而索引本身看起来还是"构建成功"。
		if (authority.Kits.Count != 0)
			await SqliteSchema.ClearAuthorityTablesAsync(connection, cancellationToken).ConfigureAwait(false);

		var total = facts.Archives.Count;
		var current = 0;
		var indexed = facts.Archives.Count(a => a.IsIndexed);

		var df = new Dictionary<CoreAssetKey, int>();
		var assetArchives = new Dictionary<CoreAssetKey, HashSet<string>>();
		await using var archiveCommand = CreateArchiveInsertCommand(connection);
		await using var issueCommand = CreateArchiveIssueInsertCommand(connection);
		await using var entryCommand = CreateArchiveEntryInsertCommand(connection);
		await using var domainCommand = CreateArchiveDomainInsertCommand(connection);
		await using var derivedNameCommand = CreateDerivedNameInsertCommand(connection);

		foreach (var archive in facts.Archives)
		{
			cancellationToken.ThrowIfCancellationRequested();
			current++;
			progress?.Report(new IndexBuildProgress(current, total, archive.PackageName));

			await InsertArchiveAsync(archiveCommand, archive, cancellationToken).ConfigureAwait(false);
			if (contentProfiles.TryGetValue(archive.PackageName, out var contentProfile))
			{
				await InsertArchiveDomainAsync(domainCommand, archive.PackageName, contentProfile, cancellationToken).ConfigureAwait(false);
				// 只有"这个名字确实被采用了"才记进名字表，避免派生名抢走权威名的位置。
				if (string.Equals(archive.DisplayName, contentProfile.DerivedName, StringComparison.Ordinal))
				{
					await InsertDerivedNameAsync(derivedNameCommand, archive.PackageName, contentProfile, cancellationToken).ConfigureAwait(false);
				}
			}
			foreach (var issue in archive.Issues)
				await InsertArchiveIssueAsync(issueCommand, archive.PackageName, issue, cancellationToken).ConfigureAwait(false);

			foreach (var entry in archive.Entries)
			{
				await InsertArchiveEntryAsync(entryCommand, entry, cancellationToken).ConfigureAwait(false);
				var key = new CoreAssetKey(entry.AssetKey.TypeId, entry.AssetKey.FileId);
				if (df.TryGetValue(key, out var count))
				{
					df[key] = count + 1;
				}
				else
				{
					df[key] = 1;
				}

				if (!assetArchives.TryGetValue(key, out var set))
				{
					set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
					assetArchives[key] = set;
				}
				set.Add(archive.PackageName);
			}
		}

		foreach (var (key, count) in df)
		{
			await InsertAssetAsync(connection, key, count, cancellationToken).ConfigureAwait(false);
		}

		foreach (var (key, archivesSet) in assetArchives)
		{
			foreach (var archiveId in archivesSet)
			{
				await InsertAssetArchiveAsync(connection, key, archiveId, cancellationToken).ConfigureAwait(false);
			}
		}

		await SaveAuthoritySnapshotAsync(connection, tx, authority, cancellationToken).ConfigureAwait(false);

		await RestoreUnitPartFactsAsync(connection, tx, cancellationToken).ConfigureAwait(false);

		await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

		await SqliteSchema.SetMetaAsync(connection, "built_utc", DateTimeOffset.UtcNow.ToString("O"), cancellationToken).ConfigureAwait(false);
		await SqliteSchema.SetMetaAsync(connection, "archives_total", total.ToString(), cancellationToken).ConfigureAwait(false);
		await SqliteSchema.SetMetaAsync(connection, "archives_indexed", indexed.ToString(), cancellationToken).ConfigureAwait(false);
		await SqliteSchema.SetMetaAsync(connection, "asset_keys_total", df.Count.ToString(), cancellationToken).ConfigureAwait(false);
		await SqliteSchema.SetMetaAsync(connection, "game_data_directory", normalizedGameDataDirectory, cancellationToken).ConfigureAwait(false);
		await SqliteSchema.SetMetaAsync(connection, "source_fingerprint", sourceFingerprint, cancellationToken).ConfigureAwait(false);
		await SqliteSchema.SetMetaAsync(connection, "game_data_authority_source_kind", authority.SourceKind.ToString(), cancellationToken).ConfigureAwait(false);
		await SqliteSchema.SetMetaAsync(connection, "game_data_authority_source_path", authority.SourcePath ?? string.Empty, cancellationToken).ConfigureAwait(false);
		await SqliteSchema.SetMetaAsync(connection, "game_data_authority_fingerprint", authority.SourceFingerprint, cancellationToken).ConfigureAwait(false);
		await SqliteSchema.SetMetaAsync(connection, "game_data_authority_version", "authority-v2-entity-encyclopedia-v1", cancellationToken).ConfigureAwait(false);
		await SqliteSchema.SetMetaAsync(connection, "parser_version", facts.ParserVersion, cancellationToken).ConfigureAwait(false);
		await SqliteSchema.SetMetaAsync(connection, "index_schema_version", facts.SchemaVersion, cancellationToken).ConfigureAwait(false);
		// Readers use pooled shared-cache connections. Purge their pre-rebuild pages so
		// in-process catalog queries observe the committed index without requiring restart.
		SqliteConnection.ClearAllPools();
		// 摘要缓存按指纹判新鲜，但**展示名与分类**来自 friendlynames / archivehashes，
		// 它们不在 source_fingerprint 里。所以重建之后必须无条件作废，
		// 否则"只换了命名数据、游戏文件没变"的一次重建会一直读到旧名字。
		SummaryCache.TryRemove(_paths.DbPath, out _);

		progress?.Report(new IndexBuildProgress(total, Math.Max(total, 1), "基础资产索引已完成；跨护甲高级数据按需建立。"));
	}

	public async ValueTask<IReadOnlyList<HD2ModCore.Domain.GameDataStreamLayoutFact>> FindStreamLayoutsAsync(
		IReadOnlyList<HD2ModCore.Domain.GameDataStreamComponentFact> components,
		uint vertexStride,
		bool requireSkinned = false,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(components);
		if (!File.Exists(_paths.DbPath)) return Array.Empty<HD2ModCore.Domain.GameDataStreamLayoutFact>();
		var signature = BuildLayoutSignature(components);
		await using var connection = new SqliteConnection($"Data Source={_paths.DbPath};Mode=ReadOnly;Cache=Shared");
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using var command = connection.CreateCommand();
		command.CommandText = @"
SELECT archive_id,unit_type_id,unit_file_id,stream_index,component_info_id,unit_version,vertex_stride,components_json,layout_signature,is_skinned
FROM game_data_stream_layouts
WHERE layout_signature=$signature AND vertex_stride=$stride AND ($skinned=0 OR is_skinned=1)
ORDER BY archive_id,unit_file_id,stream_index;";
		command.Parameters.AddWithValue("$signature", signature);
		command.Parameters.AddWithValue("$stride", vertexStride);
		command.Parameters.AddWithValue("$skinned", requireSkinned ? 1 : 0);
		var result = new List<HD2ModCore.Domain.GameDataStreamLayoutFact>();
		await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			var parsed = JsonSerializer.Deserialize<List<HD2ModCore.Domain.GameDataStreamComponentFact>>(reader.GetString(7)) ?? new List<HD2ModCore.Domain.GameDataStreamComponentFact>();
			result.Add(new HD2ModCore.Domain.GameDataStreamLayoutFact(
				reader.GetString(0),
				new CoreAssetKey(unchecked((ulong)reader.GetInt64(1)), unchecked((ulong)reader.GetInt64(2))),
				reader.GetInt32(3),
				unchecked((ulong)reader.GetInt64(4)),
				unchecked((uint)reader.GetInt64(5)),
				unchecked((uint)reader.GetInt64(6)),
				parsed,
				reader.GetString(8),
				reader.GetInt32(9) != 0));
		}
		return result;
	}

	public async ValueTask<IReadOnlyList<HD2ModCore.Domain.GameDataStreamLayoutFact>> GetStreamLayoutsAsync(CancellationToken cancellationToken = default)
	{
		if (!File.Exists(_paths.DbPath)) return Array.Empty<HD2ModCore.Domain.GameDataStreamLayoutFact>();
		await using var connection = new SqliteConnection($"Data Source={_paths.DbPath};Mode=ReadOnly;Cache=Shared");
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using var command = connection.CreateCommand();
		command.CommandText = @"
SELECT archive_id,unit_type_id,unit_file_id,stream_index,component_info_id,unit_version,vertex_stride,components_json,layout_signature,is_skinned
FROM game_data_stream_layouts
ORDER BY archive_id,unit_file_id,stream_index;";
		var result = new List<HD2ModCore.Domain.GameDataStreamLayoutFact>();
		await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			var components = JsonSerializer.Deserialize<List<HD2ModCore.Domain.GameDataStreamComponentFact>>(reader.GetString(7)) ?? new List<HD2ModCore.Domain.GameDataStreamComponentFact>();
			result.Add(new HD2ModCore.Domain.GameDataStreamLayoutFact(reader.GetString(0), new CoreAssetKey(unchecked((ulong)reader.GetInt64(1)), unchecked((ulong)reader.GetInt64(2))), reader.GetInt32(3), unchecked((ulong)reader.GetInt64(4)), unchecked((uint)reader.GetInt64(5)), unchecked((uint)reader.GetInt64(6)), components, reader.GetString(8), reader.GetInt32(9) != 0));
		}
		return result;
	}

	private static string BuildLayoutSignature(IEnumerable<HD2ModCore.Domain.GameDataStreamComponentFact> components)
		=> string.Join(";", components.Select(component => $"{component.Type}:{component.Format}:{component.Index}:{component.Unknown:x16}"));

	public async ValueTask<IReadOnlyList<AssetArchiveMatch>> FindAssetArchivesAsync(
		IReadOnlySet<CoreAssetKey> assetKeys,
		CancellationToken cancellationToken = default)
	{
		if (assetKeys is null)
		{
			throw new ArgumentNullException(nameof(assetKeys));
		}

		if (assetKeys.Count == 0 || !File.Exists(_paths.DbPath))
		{
			return Array.Empty<AssetArchiveMatch>();
		}

		await using var connection = new SqliteConnection($"Data Source={_paths.DbPath};Mode=ReadOnly;Cache=Shared");
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

		var matches = new List<AssetArchiveMatch>();
		foreach (var key in assetKeys.OrderBy(x => x.TypeId).ThenBy(x => x.FileId))
		{
			cancellationToken.ThrowIfCancellationRequested();
			matches.Add(new AssetArchiveMatch(key, await GetArchiveMetadataForAssetAsync(connection, key, cancellationToken).ConfigureAwait(false)));
		}

		return matches;
	}

	public async ValueTask<IReadOnlyDictionary<string, int>> VoteArchivesAsync(
		IReadOnlySet<CoreAssetKey> assetKeys,
		IndexFilterSettings filterSettings,
		CancellationToken cancellationToken = default)
	{
		if (assetKeys is null)
		{
			throw new ArgumentNullException(nameof(assetKeys));
		}

		if (assetKeys.Count == 0 || !File.Exists(_paths.DbPath))
		{
			return new Dictionary<string, int>();
		}

		await using var connection = new SqliteConnection($"Data Source={_paths.DbPath};Mode=ReadOnly;Cache=Shared");
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

		var totalArchivesText = await SqliteSchema.GetMetaAsync(connection, "archives_total", cancellationToken).ConfigureAwait(false);
		_ = int.TryParse(totalArchivesText, out var totalArchives);
		if (totalArchives <= 0)
		{
			totalArchives = 1;
		}

		var votes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
		foreach (var key in assetKeys)
		{
			cancellationToken.ThrowIfCancellationRequested();

			var df = await GetDfAsync(connection, key, cancellationToken).ConfigureAwait(false);
			if (IsFiltered(df, totalArchives, filterSettings))
			{
				continue;
			}

			await foreach (var archiveId in GetArchivesForAssetAsync(connection, key, cancellationToken).ConfigureAwait(false))
			{
				votes.TryGetValue(archiveId, out var count);
				votes[archiveId] = count + 1;
			}
		}

		return votes;
	}

	private static bool IsFiltered(int df, int totalArchives, IndexFilterSettings settings)
	{
		if (df <= 0)
		{
			return true;
		}

		return settings.Mode switch
		{
			IndexFilterMode.AbsoluteCount => settings.AbsoluteThreshold is int t && df > t,
			IndexFilterMode.Percentage => settings.PercentageThreshold is double p && p > 0 && (df / (double)totalArchives) > p,
			_ => false,
		};
	}

	private void NormalizeExistingIndexFileAttributes()
	{
		foreach (var path in new[] { _paths.DbPath, _paths.DbPath + "-wal", _paths.DbPath + "-shm" })
		{
			if (!File.Exists(path))
			{
				continue;
			}

			File.SetAttributes(path, FileAttributes.Normal);
		}
	}

	private static List<(string ArchiveId, string Category, string DisplayName)> FlattenArchives(ArchiveHashesRoot root)
	{
		var archives = new List<(string ArchiveId, string Category, string DisplayName)>();
		foreach (var (category, map) in root)
		{
			if (map is null) continue;
			foreach (var (archiveId, displayName) in map)
			{
				if (string.IsNullOrWhiteSpace(archiveId) || string.IsNullOrWhiteSpace(displayName))
				{
					continue;
				}

				archives.Add((archiveId.Trim().ToLowerInvariant(), category, displayName));
			}
		}

		return archives;
	}

	private static ArchiveHashesRoot TryParseCommunityArchiveMetadata(string archiveHashesJson)
	{
		if (string.IsNullOrWhiteSpace(archiveHashesJson)) return new ArchiveHashesRoot();
		try
		{
			var parsed = JsonSerializer.Deserialize<ArchiveHashesRoot>(archiveHashesJson);
			if (parsed is null) return new ArchiveHashesRoot();
			var sanitized = new ArchiveHashesRoot();
			foreach (var (category, map) in parsed)
			{
				if (string.IsNullOrWhiteSpace(category) || map is null) continue;
				var validEntries = map
					.Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
					.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
				if (validEntries.Count != 0) sanitized[category] = validEntries;
			}
			return sanitized;
		}
		catch (JsonException)
		{
			// Community files are display metadata only. Their failure must not prevent
			// installed GameData from supplying a real archive inventory.
			return new ArchiveHashesRoot();
		}
	}

	private static async ValueTask<string> ComputeSourceFingerprintAsync(
		string gameDataDirectory,
		GameDataAuthoritySnapshot authority,
		CancellationToken cancellationToken)
	{
		using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		AppendFingerprintValue(hash, Path.GetFullPath(gameDataDirectory));
		AppendFingerprintValue(hash, SqliteSchema.SchemaVersion.ToString(CultureInfo.InvariantCulture));
		AppendFingerprintValue(hash, "package-toc-v2-installed-archive-discovery");
		AppendFingerprintValue(hash, authority.SourceKind.ToString());
		AppendFingerprintValue(hash, authority.SourceFingerprint);
		AppendFingerprintValue(hash, authority.PreferredLocale);

		// 游戏版本号：只在这里体现"游戏更新了"。注册表文件本身不参与指纹——
		// 管理器管理下的用户可能用任何方式改过它，拿它做判据只会误报。
		var buildId = SteamAppManifest
			.TryRead(SteamAppManifest.ResolveManifestPath(gameDataDirectory))?.TrustedBuildId;
		AppendFingerprintValue(hash, "game-build:" + (buildId ?? "unknown"));

		foreach (var path in Directory.EnumerateFiles(gameDataDirectory, "*", SearchOption.TopDirectoryOnly)
			.Where(IsGameDataSourceFile)
			.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
		{
			cancellationToken.ThrowIfCancellationRequested();
			var info = new FileInfo(path);
			AppendFingerprintValue(hash, info.Name);
			AppendFingerprintValue(hash, info.Length.ToString(CultureInfo.InvariantCulture));
			AppendFingerprintValue(hash, info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture));
			await Task.Yield();
		}

		return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
	}

	private static bool IsGameDataSourceFile(string path)
	{
		var name = Path.GetFileName(path);
		if (string.Equals(name, "activation-state.json", StringComparison.OrdinalIgnoreCase)) return false;
		if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)) return false;
		return !new PatchFileNameParser().TryParse(name, out _);
	}

	private static void AppendFingerprintValue(IncrementalHash hash, string value)
	{
		var bytes = Encoding.UTF8.GetBytes(value);
		hash.AppendData(bytes);
		hash.AppendData(new byte[] { 0 });
	}

	private static int ParseInt(string? value)
		=> int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : 0;

	private static IReadOnlyDictionary<string, GameDataArchiveMetadata> BuildMetadataByPackage(
		IEnumerable<(string ArchiveId, string Category, string DisplayName)> archives,
		IReadOnlyDictionary<string, GameDataAuthorityArchiveMetadata> authorityArchives)
	{
		var result = new Dictionary<string, GameDataArchiveMetadata>(StringComparer.OrdinalIgnoreCase);
		foreach (var archive in archives)
		{
			if (!result.TryGetValue(archive.ArchiveId, out var existing))
			{
				result[archive.ArchiveId] = new GameDataArchiveMetadata(
					archive.ArchiveId,
					archive.DisplayName,
					archive.Category);
				continue;
			}

			var categories = string.Join(", ", new[] { existing.Category, archive.Category }
				.Where(value => !string.IsNullOrWhiteSpace(value))
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
			result[archive.ArchiveId] = existing with
			{
				Category = categories,
				DisplayName = string.IsNullOrWhiteSpace(existing.DisplayName) ? archive.DisplayName : existing.DisplayName
			};
		}

		foreach (var authority in authorityArchives.Values)
		{
			if (result.TryGetValue(authority.ArchiveId, out var existing))
			{
				// A name resolved from the installed GameData is authoritative for
				// this archive. Community metadata remains a useful fallback only
				// when the installed relation does not provide a readable name.
				var displayName = IsReadableAuthorityDisplayName(authority.DisplayName)
					? authority.DisplayName
					: existing.DisplayName;
				var category = string.Join(", ", new[] { existing.Category, authority.Category }
					.Where(value => !string.IsNullOrWhiteSpace(value))
					.SelectMany(value => value!.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
					.Distinct(StringComparer.OrdinalIgnoreCase)
					.OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
				result[authority.ArchiveId] = existing with
				{
					ArchiveHex = authority.ArchiveId,
					DisplayName = displayName,
					Category = category,
				};
			}
			else
			{
				// 注册表里有 32 个 kit 的 upper/cased/desc 三个 string id 全都不在 15 张语言表里
				// （未发布/被砍掉的装备）。GameItemAssetCatalog 会把它们渲染成 "0x<id>"——
				// 那是字符串 id，不是名字，不能当显示名写进去，否则列表里会出现 0xd8bce5f1 这种"名字"。
				result[authority.ArchiveId] = new GameDataArchiveMetadata(
					authority.ArchiveId,
					IsReadableAuthorityDisplayName(authority.DisplayName) ? authority.DisplayName : null,
					authority.Category);
			}
		}

		return result;
	}

	// 判定逻辑在 ArchiveDisplayNames 里，方便单测钉住"字符串 id 不算名字"这条。
	private static bool IsReadableAuthorityDisplayName(string? value) => ArchiveDisplayNames.IsReadable(value);

	private static GameDataArchiveIndex ApplyArchiveMetadata(
		GameDataArchiveIndex index,
		IReadOnlyDictionary<string, GameDataArchiveMetadata> metadataByPackage)
	{
		if (metadataByPackage.Count == 0)
		{
			return index;
		}

		var archives = index.Archives
			.Select(archive => metadataByPackage.TryGetValue(archive.PackageName, out var metadata)
				? archive with
				{
					ArchiveHex = string.IsNullOrWhiteSpace(metadata.ArchiveHex) ? archive.ArchiveHex : metadata.ArchiveHex,
					DisplayName = string.IsNullOrWhiteSpace(metadata.DisplayName) ? archive.DisplayName : metadata.DisplayName,
					Category = string.IsNullOrWhiteSpace(metadata.Category) ? archive.Category : metadata.Category
				}
				: archive)
			.ToArray();
		return index with { Archives = archives };
	}

	private static GameDataArchiveIndex ApplySemanticArchiveCategories(
		GameDataArchiveIndex index,
		GameDataAuthoritySnapshot authority)
	{
		var archives = index.Archives
			.Select(archive => archive with
			{
				Category = GameDataArchiveSemanticClassifier.Classify(
					archive.PackageName,
					archive.Category,
					authority)
			})
			.ToArray();
		return index with { Archives = archives };
	}

	// --- 内容域（从 archive 自己的内容反推"这是什么"） -----------------------

	private async Task<IReadOnlyDictionary<ulong, string>> LoadFriendlyNamesAsync(CancellationToken cancellationToken)
	{
		var result = new Dictionary<ulong, string>();
		if (!File.Exists(_paths.FriendlyNamesPath))
		{
			return result;
		}

		// friendlynames.txt：`<十进制哈希><空白><名字或内容路径>`，实测 4036 行。
		// 路径形如 content/env_super_earth/habs/…；也有纯名字（GL-21 Grenade Launcher）。
		foreach (var line in await File.ReadAllLinesAsync(_paths.FriendlyNamesPath, cancellationToken).ConfigureAwait(false))
		{
			if (string.IsNullOrWhiteSpace(line))
			{
				continue;
			}

			var separator = line.IndexOfAny([' ', '\t']);
			if (separator <= 0)
			{
				continue;
			}

			if (!ulong.TryParse(line.AsSpan(0, separator), out var hash))
			{
				continue;
			}

			var value = line[(separator + 1)..].Trim();
			if (value.Length != 0)
			{
				result[hash] = value;
			}
		}

		return result;
	}

	/// <summary>
	/// 由 <c>generated_unit_customization_settings</c> 命名的 archive 是**载具涂装**，
	/// 不是普通贴图包。实测 75 个，名字全是颜色（FOREST CAMO / FIRE ORANGE / TRUTH BLACK…）。
	/// 它们的名字只有英文兜底——游戏没有为这些 id 发行本地化字符串（见 ArchiveContentProfiler 注释）。
	/// </summary>
	private static HashSet<string> BuildVehicleSkinArchives(GameDataAuthoritySnapshot authority)
		=> authority.ArchiveNames
			.Where(name => name.Source.Contains("unit_customization_settings", StringComparison.OrdinalIgnoreCase))
			.Select(name => name.ArchiveId)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);

	private static Dictionary<string, ArchiveContentProfile> BuildContentProfiles(
		GameDataArchiveIndex facts,
		IReadOnlyDictionary<ulong, string> friendlyNames,
		IReadOnlySet<string> vehicleSkinArchives)
	{
		var profiles = new Dictionary<string, ArchiveContentProfile>(StringComparer.OrdinalIgnoreCase);
		foreach (var archive in facts.Archives)
		{
			// 载具涂装优先于通用"贴图包"——名字已经证明了它是什么。
			if (vehicleSkinArchives.Contains(archive.PackageName))
			{
				profiles[archive.PackageName] = new ArchiveContentProfile(
					"VehicleSkin",
					"vehicle-skin",
					"载具涂装",
					ArchiveContentProfiler.DerivedConfidence,
					FromContentPath: false);
				continue;
			}

			// 装备类但拿不到任何可读名字：注册表里有这件装备，游戏却没给它本地化字符串
			// （实测 32 个 kit 的 upper/cased/desc 三个 id 全都不在 15 张语言表里）。
			// 这不是"我们没解析出来"，是它确实没有——如实标成未发布，别让用户以为漏了。
			if (IsEquipmentCategory(archive.Category) && IsPlaceholderName(archive.DisplayName, archive.PackageName))
			{
				profiles[archive.PackageName] = new ArchiveContentProfile(
					"Unreleased",
					"no-localized-name",
					"未发布（游戏无本地化名）",
					ArchiveContentProfiler.DerivedConfidence,
					FromContentPath: false);
				continue;
			}

			if (ArchiveContentProfiler.Profile(archive.Entries, friendlyNames) is { } profile)
			{
				profiles[archive.PackageName] = profile;
			}
		}

		return profiles;
	}

	private static bool IsEquipmentCategory(string? category)
		=> category is "Armor" or "Helmet" or "Cape" or "Weapon";

	private static GameDataArchiveIndex ApplyContentProfiles(
		GameDataArchiveIndex facts,
		IReadOnlyDictionary<string, ArchiveContentProfile> profiles)
	{
		if (profiles.Count == 0)
		{
			return facts;
		}

		var archives = facts.Archives.Select(archive =>
		{
			if (!profiles.TryGetValue(archive.PackageName, out var profile))
			{
				return archive;
			}

			// 只填空：Armor / Helmet / Cape / Weapon 与社区结论一律不动。
			var category = string.IsNullOrWhiteSpace(archive.Category) || archive.Category == "Other"
				? profile.Domain
				: archive.Category;
			// 名字只在"从内容路径推出来的"时候采用；按构成兜底的名字不唯一，用了反而更难读。
			var displayName = profile.HasUsableName && IsPlaceholderName(archive.DisplayName, archive.PackageName)
				? profile.DerivedName
				: archive.DisplayName;
			return archive with { Category = category, DisplayName = displayName };
		}).ToArray();
		return facts with { Archives = archives };
	}

	private static bool IsPlaceholderName(string? displayName, string packageName)
		=> ArchiveDisplayNames.IsPlaceholder(displayName, packageName);

	private static SqliteCommand CreateArchiveDomainInsertCommand(SqliteConnection connection)
	{
		var cmd = connection.CreateCommand();
		cmd.CommandText = "INSERT OR REPLACE INTO archive_domains(archive_id,domain,subject,source,confidence) VALUES($a,$d,$s,$src,$c)";
		foreach (var name in new[] { "$a", "$d", "$s", "$src" }) cmd.Parameters.Add(name, SqliteType.Text);
		cmd.Parameters.Add("$c", SqliteType.Integer);
		cmd.Prepare();
		return cmd;
	}

	private static async Task InsertArchiveDomainAsync(SqliteCommand cmd, string archiveId, ArchiveContentProfile profile, CancellationToken cancellationToken)
	{
		cmd.Parameters["$a"].Value = archiveId;
		cmd.Parameters["$d"].Value = profile.Domain;
		cmd.Parameters["$s"].Value = profile.Subject;
		cmd.Parameters["$src"].Value = "derived:content-path";
		cmd.Parameters["$c"].Value = profile.Confidence;
		await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}

	private static SqliteCommand CreateDerivedNameInsertCommand(SqliteConnection connection)
	{
		var cmd = connection.CreateCommand();
		cmd.CommandText = "INSERT OR REPLACE INTO game_data_archive_names(archive_id,locale,display_name,source,confidence,item_id) VALUES($a,'und',$n,'derived:content-path',$c,0)";
		foreach (var name in new[] { "$a", "$n" }) cmd.Parameters.Add(name, SqliteType.Text);
		cmd.Parameters.Add("$c", SqliteType.Integer);
		cmd.Prepare();
		return cmd;
	}

	private static async Task InsertDerivedNameAsync(SqliteCommand cmd, string archiveId, ArchiveContentProfile profile, CancellationToken cancellationToken)
	{
		cmd.Parameters["$a"].Value = archiveId;
		cmd.Parameters["$n"].Value = profile.DerivedName;
		cmd.Parameters["$c"].Value = profile.Confidence;
		await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}

	private static async Task SaveAuthoritySnapshotAsync(
		SqliteConnection connection,
		SqliteTransaction transaction,
		GameDataAuthoritySnapshot snapshot,
		CancellationToken cancellationToken)
	{
		await using var kitCommand = connection.CreateCommand();
		kitCommand.Transaction = transaction;
		kitCommand.CommandText = @"
INSERT INTO game_data_authority_kits(item_id,dlc_id,set_id,kit_kind,archive_id,display_name)
VALUES($item,$dlc,$set,$kind,$archive,$name);";
		foreach (var name in new[] { "$item", "$dlc", "$set", "$kind" }) kitCommand.Parameters.Add(name, SqliteType.Integer);
		foreach (var name in new[] { "$archive", "$name" }) kitCommand.Parameters.Add(name, SqliteType.Text);
		kitCommand.Prepare();

		await using var pieceCommand = connection.CreateCommand();
		pieceCommand.Transaction = transaction;
		// 注册文件里现在会出现**完全重复**的 piece 行（八列全同，只有 tone_variations 等
		// 非键列可能不同）。加宽唯一键挡不住这种情况——实测加宽之后依旧撞 UNIQUE。
		// 所以改为"冲突时复用已有行"：RETURNING 把已有 piece_id 交回来，
		// 资源仍然挂到正确的那一行上，而不是像 INSERT OR IGNORE 那样拿到上一次的 rowid。
		pieceCommand.CommandText = @"
INSERT INTO game_data_authority_pieces(item_id,archive_id,kit_kind,raw_body_type,body_variant,slot,part_kind,piece_type,weight,tone_variations,unit_type_id,unit_file_id)
VALUES($item,$archive,$kind,$rawBody,$body,$slot,$part,$piece,$weight,$tone,$unitType,$unitFile)
ON CONFLICT(item_id,raw_body_type,body_variant,slot,piece_type,weight,unit_type_id,unit_file_id)
DO UPDATE SET item_id=item_id
RETURNING piece_id;";
		foreach (var name in new[] { "$item", "$kind", "$rawBody", "$body", "$slot", "$part", "$piece", "$weight", "$tone", "$unitType", "$unitFile" }) pieceCommand.Parameters.Add(name, SqliteType.Integer);
		pieceCommand.Parameters.Add("$archive", SqliteType.Text);
		pieceCommand.Prepare();

		await using var resourceCommand = connection.CreateCommand();
		resourceCommand.Transaction = transaction;
		// 同一份资源可能被多条（现已去重的）piece 行重复给出；
		// 主键就是 (piece_id, role, file_id)，重复即等价，忽略即可。
		resourceCommand.CommandText = "INSERT OR IGNORE INTO game_data_authority_resources(piece_id,role,file_id) VALUES($pieceId,$role,$file);";
		resourceCommand.Parameters.Add("$pieceId", SqliteType.Integer);
		resourceCommand.Parameters.Add("$role", SqliteType.Text);
		resourceCommand.Parameters.Add("$file", SqliteType.Integer);
		resourceCommand.Prepare();

		await using var pieceIdCommand = connection.CreateCommand();
		pieceIdCommand.Transaction = transaction;
		pieceIdCommand.CommandText = "SELECT last_insert_rowid();";

		await using var nameCommand = connection.CreateCommand();
		nameCommand.Transaction = transaction;
		nameCommand.CommandText = @"
INSERT INTO game_data_archive_names(archive_id,locale,display_name,source,confidence,item_id)
VALUES($archive,$locale,$name,$source,$confidence,$item);";
		nameCommand.Parameters.Add("$archive", SqliteType.Text);
		nameCommand.Parameters.Add("$locale", SqliteType.Text);
		nameCommand.Parameters.Add("$name", SqliteType.Text);
		nameCommand.Parameters.Add("$source", SqliteType.Text);
		nameCommand.Parameters.Add("$confidence", SqliteType.Integer);
		nameCommand.Parameters.Add("$item", SqliteType.Integer);
		nameCommand.Prepare();

		await using var entityCommand = connection.CreateCommand();
		entityCommand.Transaction = transaction;
		entityCommand.CommandText = @"
INSERT INTO game_data_authority_entities(entity_id,unit_type_id,unit_file_id,is_exact_unit_match,is_weapon_candidate,equipment_type,component_type_ids_json,localized_names_json,localized_designations_json)
VALUES($entity,$unitType,$unitFile,$exact,$weapon,$equipmentType,$components,$names,$designations);";
		entityCommand.Parameters.Add("$entity", SqliteType.Integer);
		entityCommand.Parameters.Add("$unitType", SqliteType.Integer);
		entityCommand.Parameters.Add("$unitFile", SqliteType.Integer);
		entityCommand.Parameters.Add("$exact", SqliteType.Integer);
		entityCommand.Parameters.Add("$weapon", SqliteType.Integer);
		entityCommand.Parameters.Add("$equipmentType", SqliteType.Integer);
		entityCommand.Parameters.Add("$components", SqliteType.Text);
		entityCommand.Parameters.Add("$names", SqliteType.Text);
		entityCommand.Parameters.Add("$designations", SqliteType.Text);
		entityCommand.Prepare();

		foreach (var entity in snapshot.EntityFacts)
		{
			cancellationToken.ThrowIfCancellationRequested();
			entityCommand.Parameters["$entity"].Value = unchecked((long)entity.EntityId);
			entityCommand.Parameters["$unitType"].Value = unchecked((long)entity.UnitAssetKey.TypeId);
			entityCommand.Parameters["$unitFile"].Value = unchecked((long)entity.UnitAssetKey.FileId);
			entityCommand.Parameters["$exact"].Value = entity.IsExactUnitMatch ? 1 : 0;
			entityCommand.Parameters["$weapon"].Value = entity.IsWeaponCandidate ? 1 : 0;
			entityCommand.Parameters["$equipmentType"].Value = entity.EquipmentType is uint equipmentType
				? unchecked((long)equipmentType)
				: DBNull.Value;
			entityCommand.Parameters["$components"].Value = JsonSerializer.Serialize(entity.ComponentTypeIds);
			entityCommand.Parameters["$names"].Value = JsonSerializer.Serialize(entity.LocalizedNames);
			entityCommand.Parameters["$designations"].Value = JsonSerializer.Serialize(entity.Designations);
			await entityCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

		foreach (var name in snapshot.ArchiveNames)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (string.IsNullOrWhiteSpace(name.ArchiveId)
				|| string.IsNullOrWhiteSpace(name.Locale)
				|| string.IsNullOrWhiteSpace(name.DisplayName))
			{
				continue;
			}

			nameCommand.Parameters["$archive"].Value = name.ArchiveId;
			nameCommand.Parameters["$locale"].Value = name.Locale;
			nameCommand.Parameters["$name"].Value = name.DisplayName;
			nameCommand.Parameters["$source"].Value = name.Source;
			nameCommand.Parameters["$confidence"].Value = name.Confidence;
			nameCommand.Parameters["$item"].Value = name.ItemId;
			await nameCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

		foreach (var kit in snapshot.Kits)
		{
			cancellationToken.ThrowIfCancellationRequested();
			kitCommand.Parameters["$item"].Value = kit.ItemId;
			kitCommand.Parameters["$dlc"].Value = kit.DlcId;
			kitCommand.Parameters["$set"].Value = kit.SetId;
			kitCommand.Parameters["$kind"].Value = (int)kit.Kind;
			kitCommand.Parameters["$archive"].Value = kit.ArchiveId;
			kitCommand.Parameters["$name"].Value = kit.DisplayName;
			await kitCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

			foreach (var piece in kit.Pieces)
			{
				cancellationToken.ThrowIfCancellationRequested();
				pieceCommand.Parameters["$item"].Value = kit.ItemId;
				pieceCommand.Parameters["$archive"].Value = kit.ArchiveId;
				pieceCommand.Parameters["$kind"].Value = (int)kit.Kind;
				pieceCommand.Parameters["$rawBody"].Value = piece.RawBodyType;
				pieceCommand.Parameters["$body"].Value = (int)piece.BodyVariant;
				pieceCommand.Parameters["$slot"].Value = piece.Slot;
				pieceCommand.Parameters["$part"].Value = (int)piece.PartKind;
				pieceCommand.Parameters["$piece"].Value = piece.PieceType;
				pieceCommand.Parameters["$weight"].Value = piece.Weight;
				pieceCommand.Parameters["$tone"].Value = piece.ToneVariations;
				pieceCommand.Parameters["$unitType"].Value = unchecked((long)piece.UnitAssetKey.TypeId);
				pieceCommand.Parameters["$unitFile"].Value = unchecked((long)piece.UnitAssetKey.FileId);
				// RETURNING 直接给出 piece_id：新插入时是新行，冲突时是已有行——
				// 两种情况下资源都挂到正确的那一行。
				var pieceId = Convert.ToInt64(
					await pieceCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
					CultureInfo.InvariantCulture);

				foreach (var resource in piece.Resources)
				{
					resourceCommand.Parameters["$pieceId"].Value = pieceId;
					resourceCommand.Parameters["$role"].Value = resource.Role;
					resourceCommand.Parameters["$file"].Value = unchecked((long)resource.FileId);
					await resourceCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
				}
			}
		}

		await using var issueCommand = connection.CreateCommand();
		issueCommand.Transaction = transaction;
		issueCommand.CommandText = @"
INSERT INTO game_data_authority_issues(severity,code,message,file_path,exception_message)
VALUES($severity,$code,$message,$path,$exception);";
		issueCommand.Parameters.Add("$severity", SqliteType.Integer);
		issueCommand.Parameters.Add("$code", SqliteType.Text);
		issueCommand.Parameters.Add("$message", SqliteType.Text);
		issueCommand.Parameters.Add("$path", SqliteType.Text);
		issueCommand.Parameters.Add("$exception", SqliteType.Text);
		issueCommand.Prepare();
		foreach (var issue in snapshot.Issues)
		{
			issueCommand.Parameters["$severity"].Value = (int)issue.Severity;
			issueCommand.Parameters["$code"].Value = issue.Code;
			issueCommand.Parameters["$message"].Value = issue.Message;
			issueCommand.Parameters["$path"].Value = (object?)issue.FilePath ?? DBNull.Value;
			issueCommand.Parameters["$exception"].Value = (object?)issue.ExceptionMessage ?? DBNull.Value;
			await issueCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}
	}

	private static async Task PreserveUnitPartFactsAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
	{
		await using var command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText = @"
DROP TABLE IF EXISTS temp.preserved_game_data_unit_parts;
CREATE TEMP TABLE preserved_game_data_unit_parts AS
SELECT archive_id,unit_type_id,unit_file_id,mesh_info_index,mesh_id,part_kind,part_layer,body_variant,semantic_name,piece_type,confidence,is_visual,is_lod,vertex_count,triangle_count,geometry_quality,fact_origin,authority_slot,authority_piece_type,reason
FROM game_data_unit_parts;";
		await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}

	private static async Task RestoreUnitPartFactsAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
	{
		await using var command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText = @"
INSERT OR IGNORE INTO game_data_unit_parts(archive_id,unit_type_id,unit_file_id,mesh_info_index,mesh_id,part_kind,part_layer,body_variant,semantic_name,piece_type,confidence,is_visual,is_lod,vertex_count,triangle_count,geometry_quality,fact_origin,authority_slot,authority_piece_type,reason)
SELECT p.archive_id,p.unit_type_id,p.unit_file_id,p.mesh_info_index,p.mesh_id,p.part_kind,p.part_layer,p.body_variant,p.semantic_name,p.piece_type,p.confidence,p.is_visual,p.is_lod,p.vertex_count,p.triangle_count,p.geometry_quality,p.fact_origin,p.authority_slot,p.authority_piece_type,p.reason
FROM preserved_game_data_unit_parts p
JOIN archives a ON a.archive_id=p.archive_id
JOIN archive_entries e ON e.archive_id=p.archive_id AND e.type_id=p.unit_type_id AND e.file_id=p.unit_file_id
WHERE lower(a.category) LIKE '%armor%' OR lower(a.category) LIKE '%helmet%';
DROP TABLE preserved_game_data_unit_parts;";
		await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}

	private static SqliteCommand CreateArchiveInsertCommand(SqliteConnection connection)
	{
		var cmd = connection.CreateCommand();
		cmd.CommandText = "INSERT OR REPLACE INTO archives(archive_id,category,display_name,archive_hex,uses_slim_entry_offset,status) VALUES($a,$c,$n,$h,$s,$status)";
		cmd.Parameters.Add("$a", SqliteType.Text);
		cmd.Parameters.Add("$c", SqliteType.Text);
		cmd.Parameters.Add("$n", SqliteType.Text);
		cmd.Parameters.Add("$h", SqliteType.Text);
		cmd.Parameters.Add("$s", SqliteType.Integer);
		cmd.Parameters.Add("$status", SqliteType.Text);
		cmd.Prepare();
		return cmd;
	}

	private static async Task InsertArchiveAsync(SqliteCommand cmd, GameDataArchiveFact archive, CancellationToken cancellationToken)
	{
		cmd.Parameters["$a"].Value = archive.PackageName;
		cmd.Parameters["$c"].Value = archive.Category ?? string.Empty;
		cmd.Parameters["$n"].Value = archive.DisplayName ?? archive.PackageName;
		cmd.Parameters["$h"].Value = (object?)archive.ArchiveHex ?? DBNull.Value;
		cmd.Parameters["$s"].Value = archive.UsesSlimEntryOffset ? 1 : 0;
		cmd.Parameters["$status"].Value = archive.IsIndexed ? "Indexed" : "Issues";
		await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}

	private static SqliteCommand CreateArchiveEntryInsertCommand(SqliteConnection connection)
	{
		var cmd = connection.CreateCommand();
		cmd.CommandText = @"INSERT OR REPLACE INTO archive_entries
(archive_id,entry_index,type_id,file_id,df,toc_data_offset,stream_offset,gpu_resource_offset,toc_data_size,stream_size,gpu_resource_size,unknown1,unknown2,unknown3,unknown4)
VALUES($a,$i,$t,$f,1,$td,$so,$go,$ts,$ss,$gs,$u1,$u2,$u3,$u4)";
		foreach (var name in new[] { "$a" }) cmd.Parameters.Add(name, SqliteType.Text);
		foreach (var name in new[] { "$i", "$t", "$f", "$td", "$so", "$go", "$ts", "$ss", "$gs", "$u1", "$u2", "$u3", "$u4" }) cmd.Parameters.Add(name, SqliteType.Integer);
		cmd.Prepare();
		return cmd;
	}

	private static async Task InsertArchiveEntryAsync(SqliteCommand cmd, GameDataArchiveEntryFact entry, CancellationToken cancellationToken)
	{
		cmd.Parameters["$a"].Value = entry.PackageName;
		cmd.Parameters["$i"].Value = entry.EntryIndex;
		cmd.Parameters["$t"].Value = unchecked((long)entry.AssetKey.TypeId);
		cmd.Parameters["$f"].Value = unchecked((long)entry.AssetKey.FileId);
		cmd.Parameters["$td"].Value = unchecked((long)entry.TocDataOffset);
		cmd.Parameters["$so"].Value = unchecked((long)entry.StreamOffset);
		cmd.Parameters["$go"].Value = unchecked((long)entry.GpuResourceOffset);
		cmd.Parameters["$ts"].Value = entry.TocDataSize;
		cmd.Parameters["$ss"].Value = entry.StreamSize;
		cmd.Parameters["$gs"].Value = entry.GpuResourceSize;
		cmd.Parameters["$u1"].Value = unchecked((long)entry.Unknown1);
		cmd.Parameters["$u2"].Value = unchecked((long)entry.Unknown2);
		cmd.Parameters["$u3"].Value = entry.Unknown3;
		cmd.Parameters["$u4"].Value = entry.Unknown4;
		await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}

	private static SqliteCommand CreateArchiveIssueInsertCommand(SqliteConnection connection)
	{
		var cmd = connection.CreateCommand();
		cmd.CommandText = "INSERT INTO archive_issues(archive_id,code,message) VALUES($a,$c,$m)";
		cmd.Parameters.Add("$a", SqliteType.Text);
		cmd.Parameters.Add("$c", SqliteType.Text);
		cmd.Parameters.Add("$m", SqliteType.Text);
		cmd.Prepare();
		return cmd;
	}

	// 这里曾经有一份 CreateUnitPartInsertCommand / InsertUnitPartAsync，没有任何调用方。
	// 写 game_data_unit_parts 的唯一活路径是 AdvancedEquipmentIndexService——保留第二份
	// 只会让人以为两条路径都在写、以为两份逻辑一致。
	private static string DescribeParts(IEnumerable<GameDataUnitPartFact> parts)
	{
		// 显示名只有 ArmorStructure 一份，这里不再自己写 switch。
		var summaries = parts.Where(part => part.IsVisualMesh && !part.IsLod && part.PartKind != UnitMeshPartKind.Unknown)
			.Select(part => $"{ArmorStructure.Display(ArmorStructure.FromPartKind(part.PartKind))}－{ArmorStructure.DisplayLayer(part.Layer)}－{ArmorStructure.DisplayBody(part.BodyVariant)}")
			.Distinct(StringComparer.Ordinal).ToArray();
		return summaries.Length == 0 ? "—" : string.Join("，", summaries);
	}

	private static async Task InsertArchiveIssueAsync(SqliteCommand cmd, string archiveId, PatchAnalysisIssue issue, CancellationToken cancellationToken)
	{
		cmd.Parameters["$a"].Value = archiveId;
		cmd.Parameters["$c"].Value = issue.Code;
		cmd.Parameters["$m"].Value = issue.Message;
		await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}

	private static async Task InsertAssetAsync(SqliteConnection connection, CoreAssetKey key, int df, CancellationToken cancellationToken)
	{
		await using var cmd = connection.CreateCommand();
		cmd.CommandText = "INSERT OR REPLACE INTO assets(type_id,file_id,df) VALUES($t,$f,$d)";
		cmd.Parameters.AddWithValue("$t", unchecked((long)key.TypeId));
		cmd.Parameters.AddWithValue("$f", unchecked((long)key.FileId));
		cmd.Parameters.AddWithValue("$d", df);
		await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}

	private static async Task InsertAssetArchiveAsync(SqliteConnection connection, CoreAssetKey key, string archiveId, CancellationToken cancellationToken)
	{
		await using var cmd = connection.CreateCommand();
		cmd.CommandText = "INSERT OR IGNORE INTO asset_archives(type_id,file_id,archive_id) VALUES($t,$f,$a)";
		cmd.Parameters.AddWithValue("$t", unchecked((long)key.TypeId));
		cmd.Parameters.AddWithValue("$f", unchecked((long)key.FileId));
		cmd.Parameters.AddWithValue("$a", archiveId);
		await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}

	private static async Task<int> GetDfAsync(SqliteConnection connection, CoreAssetKey key, CancellationToken cancellationToken)
	{
		await using var cmd = connection.CreateCommand();
		cmd.CommandText = "SELECT df FROM assets WHERE type_id=$t AND file_id=$f";
		cmd.Parameters.AddWithValue("$t", unchecked((long)key.TypeId));
		cmd.Parameters.AddWithValue("$f", unchecked((long)key.FileId));

		var obj = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
		return obj is null ? 0 : Convert.ToInt32(obj);
	}

	private static async Task<IReadOnlyList<ArchiveMetadata>> GetArchiveMetadataForAssetAsync(SqliteConnection connection, CoreAssetKey key, CancellationToken cancellationToken)
	{
		await using var cmd = connection.CreateCommand();
		cmd.CommandText = @"
SELECT a.archive_id, a.category, a.display_name
FROM asset_archives aa
JOIN archives a ON a.archive_id = aa.archive_id
WHERE aa.type_id=$t AND aa.file_id=$f
ORDER BY a.category, a.display_name, a.archive_id";
		cmd.Parameters.AddWithValue("$t", unchecked((long)key.TypeId));
		cmd.Parameters.AddWithValue("$f", unchecked((long)key.FileId));

		var archives = new List<ArchiveMetadata>();
		await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			archives.Add(new ArchiveMetadata(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
		}

		return archives;
	}

	private static async Task<IReadOnlyList<(string PackageName, string DisplayName)>> GetSharedArchivesAsync(
		SqliteConnection connection,
		string packageName,
		ulong typeId,
		ulong fileId,
		CancellationToken cancellationToken)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = @"
SELECT DISTINCT a.archive_id, a.display_name
FROM archive_entries e
JOIN archives a ON a.archive_id = e.archive_id
WHERE e.type_id = $typeId AND e.file_id = $fileId AND e.archive_id <> $package
ORDER BY a.display_name, a.archive_id;";
		command.Parameters.AddWithValue("$typeId", unchecked((long)typeId));
		command.Parameters.AddWithValue("$fileId", unchecked((long)fileId));
		command.Parameters.AddWithValue("$package", packageName);
		var result = new List<(string PackageName, string DisplayName)>();
		await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			result.Add((reader.GetString(0), reader.GetString(1)));
		}
		return result;
	}

	private static async IAsyncEnumerable<string> GetArchivesForAssetAsync(SqliteConnection connection, CoreAssetKey key, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
	{
		await using var cmd = connection.CreateCommand();
		cmd.CommandText = "SELECT archive_id FROM asset_archives WHERE type_id=$t AND file_id=$f";
		cmd.Parameters.AddWithValue("$t", unchecked((long)key.TypeId));
		cmd.Parameters.AddWithValue("$f", unchecked((long)key.FileId));

		await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			yield return reader.GetString(0);
		}
	}
}
