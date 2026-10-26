using Microsoft.Data.Sqlite;

namespace HD2ModCore.Infrastructure.Sqlite;

// 作用：定义并创建 GameData facts 与“资产键 -> 原版 archive”反向索引的 SQLite 数据库结构。
// Purpose: Defines and applies the persisted GameData facts and asset->archive reverse index schema.
internal static class SqliteSchema
{
	// Version 9 adds generated-entity encyclopedia facts alongside the
	// installed-game weapon and vehicle customization names.
	// Version 8 added installed-game weapon and vehicle customization names to
	// the persisted authority snapshot. Bumping the source identity ensures an
	// existing index is rebuilt rather than silently retaining the old display
	// metadata.
	public const int SchemaVersion = 10;

	public static async Task EnsureCreatedAsync(SqliteConnection connection, CancellationToken cancellationToken)
	{
		// 权威件表的唯一键在 v2 里加宽了（见下面的 UNIQUE）。`CREATE TABLE IF NOT EXISTS`
		// 不会改动已存在的表，所以旧库必须**显式丢弃**这几张表再重建——它们是可重建的派生缓存，
		// 丢掉的代价只是下次重建索引，而留着旧约束的代价是索引永远建不起来。
		// 用一个独立的 meta 键做标记，不依赖别处是否写过 schema_version。
		await DropStaleAuthorityTablesAsync(connection, cancellationToken).ConfigureAwait(false);

		await using (var cmd = connection.CreateCommand())
		{
			cmd.CommandText = @"
PRAGMA journal_mode=WAL;
PRAGMA synchronous=NORMAL;

CREATE TABLE IF NOT EXISTS meta (
	key TEXT PRIMARY KEY,
	value TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS archives (
	archive_id TEXT PRIMARY KEY,
	category TEXT NOT NULL,
	display_name TEXT NOT NULL,
	archive_hex TEXT NULL,
	uses_slim_entry_offset INTEGER NOT NULL DEFAULT 0,
	status TEXT NOT NULL DEFAULT 'Indexed'
);

CREATE TABLE IF NOT EXISTS archive_entries (
	archive_id TEXT NOT NULL,
	entry_index INTEGER NOT NULL,
	type_id INTEGER NOT NULL,
	file_id INTEGER NOT NULL,
	df INTEGER NOT NULL,
	toc_data_offset INTEGER NOT NULL,
	stream_offset INTEGER NOT NULL,
	gpu_resource_offset INTEGER NOT NULL,
	toc_data_size INTEGER NOT NULL,
	stream_size INTEGER NOT NULL,
	gpu_resource_size INTEGER NOT NULL,
	unknown1 INTEGER NOT NULL,
	unknown2 INTEGER NOT NULL,
	unknown3 INTEGER NOT NULL,
	unknown4 INTEGER NOT NULL,
	PRIMARY KEY(archive_id, entry_index),
	FOREIGN KEY(archive_id) REFERENCES archives(archive_id)
);

CREATE TABLE IF NOT EXISTS archive_issues (
	issue_id INTEGER PRIMARY KEY AUTOINCREMENT,
	archive_id TEXT NULL,
	code TEXT NOT NULL,
	message TEXT NOT NULL,
	FOREIGN KEY(archive_id) REFERENCES archives(archive_id)
);

CREATE INDEX IF NOT EXISTS ix_archive_entries_asset ON archive_entries(type_id, file_id);
CREATE INDEX IF NOT EXISTS ix_archive_issues_archive ON archive_issues(archive_id);

CREATE TABLE IF NOT EXISTS assets (
	type_id INTEGER NOT NULL,
	file_id INTEGER NOT NULL,
	df INTEGER NOT NULL,
	PRIMARY KEY(type_id, file_id)
);

CREATE TABLE IF NOT EXISTS asset_archives (
	type_id INTEGER NOT NULL,
	file_id INTEGER NOT NULL,
	archive_id TEXT NOT NULL,
	PRIMARY KEY(type_id, file_id, archive_id),
	FOREIGN KEY(archive_id) REFERENCES archives(archive_id)
);

CREATE INDEX IF NOT EXISTS ix_asset_archives_archive ON asset_archives(archive_id);

CREATE TABLE IF NOT EXISTS game_data_unit_parts (
	archive_id TEXT NOT NULL,
	unit_type_id INTEGER NOT NULL,
	unit_file_id INTEGER NOT NULL,
	mesh_info_index INTEGER NOT NULL,
	mesh_id INTEGER NOT NULL,
	part_kind INTEGER NOT NULL,
	part_layer INTEGER NOT NULL,
	body_variant INTEGER NOT NULL DEFAULT 0,
	semantic_name TEXT NOT NULL,
	confidence INTEGER NOT NULL,
	is_visual INTEGER NOT NULL,
	is_lod INTEGER NOT NULL,
	vertex_count INTEGER NOT NULL DEFAULT 0,
	triangle_count INTEGER NOT NULL DEFAULT 0,
	geometry_quality INTEGER NOT NULL DEFAULT 0,
	fact_origin INTEGER NOT NULL DEFAULT 2,
	reason TEXT NOT NULL,
	PRIMARY KEY(archive_id, unit_type_id, unit_file_id, mesh_info_index),
	FOREIGN KEY(archive_id) REFERENCES archives(archive_id)
);

CREATE INDEX IF NOT EXISTS ix_game_data_unit_parts_unit ON game_data_unit_parts(unit_type_id, unit_file_id);

CREATE TABLE IF NOT EXISTS game_data_stream_layouts (
	archive_id TEXT NOT NULL,
	unit_type_id INTEGER NOT NULL,
	unit_file_id INTEGER NOT NULL,
	stream_index INTEGER NOT NULL,
	component_info_id INTEGER NOT NULL,
	unit_version INTEGER NOT NULL,
	vertex_stride INTEGER NOT NULL,
	components_json TEXT NOT NULL,
	layout_signature TEXT NOT NULL,
	is_skinned INTEGER NOT NULL,
	PRIMARY KEY(archive_id, unit_type_id, unit_file_id, stream_index),
	FOREIGN KEY(archive_id) REFERENCES archives(archive_id)
);

CREATE INDEX IF NOT EXISTS ix_game_data_stream_layouts_signature ON game_data_stream_layouts(layout_signature, vertex_stride, is_skinned);
CREATE INDEX IF NOT EXISTS ix_game_data_stream_layouts_component_info ON game_data_stream_layouts(component_info_id);

CREATE TABLE IF NOT EXISTS game_data_authority_kits (
	item_id INTEGER PRIMARY KEY,
	dlc_id INTEGER NOT NULL,
	set_id INTEGER NOT NULL,
	kit_kind INTEGER NOT NULL,
	archive_id TEXT NOT NULL,
	display_name TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_game_data_authority_kits_archive ON game_data_authority_kits(archive_id, kit_kind);

CREATE TABLE IF NOT EXISTS game_data_archive_names (
	archive_id TEXT NOT NULL,
	locale TEXT NOT NULL,
	display_name TEXT NOT NULL,
	source TEXT NOT NULL,
	confidence INTEGER NOT NULL,
	item_id INTEGER NOT NULL DEFAULT 0,
	PRIMARY KEY(archive_id, locale, display_name, item_id)
);

CREATE INDEX IF NOT EXISTS ix_game_data_archive_names_archive_locale ON game_data_archive_names(archive_id, locale);

-- 内容域：从 archive 自己的内容反推出的「这是什么」（环境 / 目标点 / 设施 / 音效…）。
-- 与 archives.category 的区别：category 只有一个值、优先放权威结论；这张表可以多值，
-- 并且带 source/confidence，资源树按域聚合时读它。
CREATE TABLE IF NOT EXISTS archive_domains (
	archive_id TEXT NOT NULL,
	domain TEXT NOT NULL,
	subject TEXT NOT NULL,
	source TEXT NOT NULL,
	confidence INTEGER NOT NULL,
	PRIMARY KEY(archive_id, domain, subject)
);

CREATE INDEX IF NOT EXISTS ix_archive_domains_domain ON archive_domains(domain, subject);

CREATE TABLE IF NOT EXISTS game_data_authority_entities (
	entity_id INTEGER PRIMARY KEY,
	unit_type_id INTEGER NOT NULL,
	unit_file_id INTEGER NOT NULL,
	is_exact_unit_match INTEGER NOT NULL,
	is_weapon_candidate INTEGER NOT NULL,
	equipment_type INTEGER NULL,
	component_type_ids_json TEXT NOT NULL,
	localized_names_json TEXT NOT NULL,
	localized_designations_json TEXT NOT NULL DEFAULT '{}'
);

CREATE INDEX IF NOT EXISTS ix_game_data_authority_entities_unit ON game_data_authority_entities(unit_type_id, unit_file_id);

CREATE TABLE IF NOT EXISTS game_data_authority_pieces (
	piece_id INTEGER PRIMARY KEY AUTOINCREMENT,
	item_id INTEGER NOT NULL,
	archive_id TEXT NOT NULL,
	kit_kind INTEGER NOT NULL,
	raw_body_type INTEGER NOT NULL,
	body_variant INTEGER NOT NULL,
	slot INTEGER NOT NULL,
	part_kind INTEGER NOT NULL,
	piece_type INTEGER NOT NULL,
	weight INTEGER NOT NULL,
	tone_variations INTEGER NOT NULL,
	unit_type_id INTEGER NOT NULL,
	unit_file_id INTEGER NOT NULL,
	-- body_variant 与 weight 是 piece 身份的一部分：2026-10-20 的游戏更新里出现了
	-- 「同 (条目, 原始身形, 槽位, 层, unit) 但身形变体/重量不同」的合法重复行，
	-- 旧唯一键不含这两列，于是插入直接撞 UNIQUE 约束、整次索引构建失败。
	UNIQUE(item_id, raw_body_type, body_variant, slot, piece_type, weight, unit_type_id, unit_file_id),
	FOREIGN KEY(item_id) REFERENCES game_data_authority_kits(item_id)
);

CREATE INDEX IF NOT EXISTS ix_game_data_authority_pieces_archive_unit ON game_data_authority_pieces(archive_id, unit_type_id, unit_file_id);
CREATE INDEX IF NOT EXISTS ix_game_data_authority_pieces_unit ON game_data_authority_pieces(unit_type_id, unit_file_id);

CREATE TABLE IF NOT EXISTS game_data_authority_resources (
	piece_id INTEGER NOT NULL,
	role TEXT NOT NULL,
	file_id INTEGER NOT NULL,
	PRIMARY KEY(piece_id, role, file_id),
	FOREIGN KEY(piece_id) REFERENCES game_data_authority_pieces(piece_id)
);

CREATE INDEX IF NOT EXISTS ix_game_data_authority_resources_file ON game_data_authority_resources(file_id);

CREATE TABLE IF NOT EXISTS game_data_authority_issues (
	issue_id INTEGER PRIMARY KEY AUTOINCREMENT,
	severity INTEGER NOT NULL,
	code TEXT NOT NULL,
	message TEXT NOT NULL,
	file_path TEXT NULL,
	exception_message TEXT NULL
);

DROP TABLE IF EXISTS game_data_unit_part_scans;

";
			await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

		await AddColumnIfMissingAsync(connection, "archives", "archive_hex", "TEXT NULL", cancellationToken).ConfigureAwait(false);
		await AddColumnIfMissingAsync(connection, "archives", "uses_slim_entry_offset", "INTEGER NOT NULL DEFAULT 0", cancellationToken).ConfigureAwait(false);
		await AddColumnIfMissingAsync(connection, "archives", "status", "TEXT NOT NULL DEFAULT 'Indexed'", cancellationToken).ConfigureAwait(false);
		await AddColumnIfMissingAsync(connection, "game_data_unit_parts", "body_variant", "INTEGER NOT NULL DEFAULT 0", cancellationToken).ConfigureAwait(false);
		await AddColumnIfMissingAsync(connection, "game_data_unit_parts", "piece_type", "TEXT NOT NULL DEFAULT ''", cancellationToken).ConfigureAwait(false);
		await AddColumnIfMissingAsync(connection, "game_data_unit_parts", "vertex_count", "INTEGER NOT NULL DEFAULT 0", cancellationToken).ConfigureAwait(false);
		await AddColumnIfMissingAsync(connection, "game_data_unit_parts", "triangle_count", "INTEGER NOT NULL DEFAULT 0", cancellationToken).ConfigureAwait(false);
		await AddColumnIfMissingAsync(connection, "game_data_unit_parts", "geometry_quality", "INTEGER NOT NULL DEFAULT 0", cancellationToken).ConfigureAwait(false);
		await AddColumnIfMissingAsync(connection, "game_data_unit_parts", "fact_origin", "INTEGER NOT NULL DEFAULT 2", cancellationToken).ConfigureAwait(false);
		await AddColumnIfMissingAsync(connection, "game_data_authority_entities", "localized_designations_json", "TEXT NOT NULL DEFAULT '{}'", cancellationToken).ConfigureAwait(false);

		await SetMetaAsync(connection, "schema_version", SchemaVersion.ToString(), cancellationToken).ConfigureAwait(false);
	}

	private static async Task AddColumnIfMissingAsync(
		SqliteConnection connection,
		string tableName,
		string columnName,
		string columnDefinition,
		CancellationToken cancellationToken)
	{
		await using var check = connection.CreateCommand();
		check.CommandText = $"SELECT 1 FROM pragma_table_info('{tableName}') WHERE name=$name LIMIT 1";
		check.Parameters.AddWithValue("$name", columnName);
		if (await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
		{
			return;
		}

		await using var alter = connection.CreateCommand();
		alter.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDefinition}";
		await alter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}

	public static async Task SetMetaAsync(SqliteConnection connection, string key, string value, CancellationToken cancellationToken)
	{
		await using var cmd = connection.CreateCommand();
		cmd.CommandText = @"INSERT INTO meta(key,value) VALUES($key,$value)
ON CONFLICT(key) DO UPDATE SET value=excluded.value;";
		cmd.Parameters.AddWithValue("$key", key);
		cmd.Parameters.AddWithValue("$value", value);
		await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}

	public static async Task<string?> GetMetaAsync(SqliteConnection connection, string key, CancellationToken cancellationToken)
	{
		await using var cmd = connection.CreateCommand();
		cmd.CommandText = "SELECT value FROM meta WHERE key=$key";
		cmd.Parameters.AddWithValue("$key", key);
		var obj = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
		return obj?.ToString();
	}

	/// <summary>权威件表的结构版本。加宽唯一键时把它 +1。</summary>
	private const string AuthoritySchemaMetaKey = "authority_pieces_schema";
	private const string AuthoritySchemaVersion = "2";

	/// <summary>
	/// 结构版本不符时丢弃权威件表（含依赖它的两张表），让随后的 CREATE 重新建出来。
	/// 只丢这三张：它们全部由注册文件重建，丢掉不损失任何权威数据。
	/// </summary>
	private static async Task DropStaleAuthorityTablesAsync(SqliteConnection connection, CancellationToken cancellationToken)
	{
		string? version;
		try
		{
			version = await GetMetaAsync(connection, AuthoritySchemaMetaKey, cancellationToken).ConfigureAwait(false);
		}
		catch (SqliteException)
		{
			// meta 表还不存在：全新库，什么都不用丢。
			return;
		}

		if (string.Equals(version, AuthoritySchemaVersion, StringComparison.Ordinal))
			return;

		await using (var drop = connection.CreateCommand())
		{
			drop.CommandText = @"
DROP TABLE IF EXISTS game_data_authority_resources;
DROP TABLE IF EXISTS game_data_authority_pieces;
DROP TABLE IF EXISTS game_data_authority_kits;
";
			await drop.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

		await SetMetaAsync(connection, AuthoritySchemaMetaKey, AuthoritySchemaVersion, cancellationToken).ConfigureAwait(false);
	}

	public static async Task ClearIndexDataAsync(SqliteConnection connection, CancellationToken cancellationToken)
	{
		await using var cmd = connection.CreateCommand();
		cmd.CommandText = @"
DELETE FROM asset_archives;
DELETE FROM assets;
DELETE FROM game_data_stream_layouts;
DELETE FROM game_data_unit_parts;
DELETE FROM game_data_authority_resources;
DELETE FROM game_data_authority_pieces;
DELETE FROM game_data_authority_kits;
DELETE FROM game_data_archive_names;
DELETE FROM archive_domains;
DELETE FROM game_data_authority_entities;
DELETE FROM game_data_authority_issues;
DELETE FROM archive_issues;
DELETE FROM archive_entries;
DELETE FROM archives;
";
		await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}
}
