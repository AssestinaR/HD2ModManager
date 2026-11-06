using HD2ModAdaptation.Analysis;
using HD2ModCore.Application;
using HD2ModCore.Domain;
using Microsoft.Data.Sqlite;

namespace HD2ModCore.Infrastructure;

// Purpose: Stores stable per-Mod patch assets, references and evidence in an independently versioned SQLite database.
public sealed class SqliteModFactsStore : IReferenceGraphQueryIndex, IReferenceGraphIndexWriter, IModDerivedDataCleanup
{
	private const int SchemaVersion = 3;
	private readonly StoragePaths paths;

	public SqliteModFactsStore(StoragePaths paths) => this.paths = paths ?? throw new ArgumentNullException(nameof(paths));

	public async ValueTask DeleteAsync(ModNodeId nodeId, CancellationToken cancellationToken = default)
	{
		await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
		await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
		await ExecuteAsync(connection, transaction, "DELETE FROM asset_references WHERE node_id = $node; DELETE FROM mod_assets WHERE node_id = $node; DELETE FROM patch_groups WHERE node_id = $node; ", nodeId.Value.ToString("N"), cancellationToken).ConfigureAwait(false);
		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
	}

	public ValueTask DeleteNodeAsync(ModNodeId nodeId, CancellationToken cancellationToken = default)
		=> DeleteAsync(nodeId, cancellationToken);

	public ValueTask ReplaceNodeAsync(ReferenceGraphFacts facts, CancellationToken cancellationToken = default)
		=> ReplaceNodeCoreAsync(facts.NodeId, facts.RelativePath, facts.Analyses, facts.BuiltUtc, cancellationToken);

	public ValueTask ReplaceNodeAsync(AdvancedUnitAnalysisFacts facts, CancellationToken cancellationToken = default)
		=> ReplaceNodeCoreAsync(facts.NodeId, facts.RelativePath, facts.Analyses, facts.BuiltUtc, cancellationToken);

	private async ValueTask ReplaceNodeCoreAsync(ModNodeId nodeId, string relativePath, IReadOnlyList<PatchGroupAnalysis> analyses, DateTimeOffset builtUtc, CancellationToken cancellationToken)
	{
		await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
		await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
		var node = nodeId.Value.ToString("N");
		await ExecuteAsync(connection, transaction, "DELETE FROM asset_references WHERE node_id = $node; DELETE FROM mod_assets WHERE node_id = $node; DELETE FROM patch_groups WHERE node_id = $node;", node, cancellationToken).ConfigureAwait(false);
		foreach (var analysis in analyses)
		{
			var groupId = $"{node}:{Path.GetFileName(analysis.Input.PatchTocFilePath)}";
			await ExecuteAsync(connection, transaction, "INSERT OR REPLACE INTO patch_groups(group_id,node_id,patch_path,analyzer_version) VALUES($group,$node,$path,$version);", (node, groupId, analysis.Input.PatchTocFilePath, analysis.AnalyzerVersion), cancellationToken).ConfigureAwait(false);
			foreach (var asset in analysis.Assets)
				await ExecuteAsync(connection, transaction, "INSERT OR REPLACE INTO mod_assets(node_id,group_id,type_id,file_id,toc_size,stream_size,gpu_size) VALUES($node,$group,$type,$file,$toc,$stream,$gpu);", (node, groupId, Hex(asset.AssetKey.TypeId), Hex(asset.AssetKey.FileId), asset.TocDataSize, asset.StreamSize, asset.GpuResourceSize), cancellationToken).ConfigureAwait(false);
			foreach (var reference in analysis.References)
				await ExecuteAsync(connection, transaction, "INSERT INTO asset_references(node_id,group_id,source_type_id,source_file_id,target_type_id,target_file_id,relation_kind,payload_offset,slot_id,reference_index) VALUES($node,$group,$sourceType,$sourceFile,$targetType,$targetFile,$kind,$offset,$slot,$index);", (node, groupId, Hex(reference.SourceAssetKey.TypeId), Hex(reference.SourceAssetKey.FileId), Hex(reference.TargetAssetKey.TypeId), Hex(reference.TargetAssetKey.FileId), (int)reference.Kind, reference.PayloadRelativeOffset, reference.SlotId, reference.ReferenceIndex), cancellationToken).ConfigureAwait(false);
		}
		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
	}

	public async ValueTask<IReadOnlyList<ModAssetConsumerFact>> FindConsumerFactsAsync(HD2ModAdaptation.PatchReconstruction.AssetKey targetAssetKey, CancellationToken cancellationToken = default)
	{
		await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT node_id,group_id,source_type_id,source_file_id,relation_kind,payload_offset,slot_id,reference_index FROM asset_references WHERE target_type_id=$type AND target_file_id=$file";
		command.Parameters.AddWithValue("$type", Hex(targetAssetKey.TypeId));
		command.Parameters.AddWithValue("$file", Hex(targetAssetKey.FileId));
		var result = new List<ModAssetConsumerFact>();
		await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			var source = new HD2ModAdaptation.PatchReconstruction.AssetKey(ParseHex(reader.GetString(2)), ParseHex(reader.GetString(3)));
			var reference = new PatchAssetReference(source, targetAssetKey, (PatchReferenceKind)reader.GetInt32(4), checked((uint)reader.GetInt64(5)), reader.IsDBNull(6) ? null : checked((uint)reader.GetInt64(6)), reader.IsDBNull(7) ? null : reader.GetInt32(7));
			result.Add(new ModAssetConsumerFact(new ModNodeId(Guid.ParseExact(reader.GetString(0), "N")), reader.GetString(1), reference));
		}
		return result;
	}

	public async ValueTask<IReadOnlyList<ModAssetConsumerFact>> FindConsumerFactsAsync(
		IReadOnlyCollection<HD2ModAdaptation.PatchReconstruction.AssetKey> targetAssetKeys,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(targetAssetKeys);
		var keys = targetAssetKeys.Distinct().ToArray();
		if (keys.Length == 0) return Array.Empty<ModAssetConsumerFact>();

		await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
		var results = new List<ModAssetConsumerFact>();
		foreach (var batch in keys.Chunk(400))
		{
			cancellationToken.ThrowIfCancellationRequested();
			await using var command = connection.CreateCommand();
			var predicates = new List<string>();
			for (var index = 0; index < batch.Length; index++)
			{
				predicates.Add($"(target_type_id=$type{index} AND target_file_id=$file{index})");
				command.Parameters.AddWithValue($"$type{index}", Hex(batch[index].TypeId));
				command.Parameters.AddWithValue($"$file{index}", Hex(batch[index].FileId));
			}
			command.CommandText = $"SELECT node_id,group_id,source_type_id,source_file_id,target_type_id,target_file_id,relation_kind,payload_offset,slot_id,reference_index FROM asset_references WHERE {string.Join(" OR ", predicates)}";
			await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				var source = new HD2ModAdaptation.PatchReconstruction.AssetKey(ParseHex(reader.GetString(2)), ParseHex(reader.GetString(3)));
				var target = new HD2ModAdaptation.PatchReconstruction.AssetKey(ParseHex(reader.GetString(4)), ParseHex(reader.GetString(5)));
				var reference = new PatchAssetReference(source, target, (PatchReferenceKind)reader.GetInt32(6), checked((uint)reader.GetInt64(7)), reader.IsDBNull(8) ? null : checked((uint)reader.GetInt64(8)), reader.IsDBNull(9) ? null : reader.GetInt32(9));
				results.Add(new ModAssetConsumerFact(new ModNodeId(Guid.ParseExact(reader.GetString(0), "N")), reader.GetString(1), reference));
			}
		}
		return results;
	}

	public async ValueTask<IReadOnlyList<ModAssetProviderFact>> FindProviderFactsAsync(
		IReadOnlyCollection<HD2ModAdaptation.PatchReconstruction.AssetKey> assetKeys,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(assetKeys);
		var keys = assetKeys.Distinct().ToArray();
		if (keys.Length == 0) return Array.Empty<ModAssetProviderFact>();

		await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
		var results = new List<ModAssetProviderFact>();
		foreach (var batch in keys.Chunk(400))
		{
			cancellationToken.ThrowIfCancellationRequested();
			await using var command = connection.CreateCommand();
			var predicates = new List<string>();
			for (var index = 0; index < batch.Length; index++)
			{
				predicates.Add($"(type_id=$type{index} AND file_id=$file{index})");
				command.Parameters.AddWithValue($"$type{index}", Hex(batch[index].TypeId));
				command.Parameters.AddWithValue($"$file{index}", Hex(batch[index].FileId));
			}
			command.CommandText = $"SELECT node_id,group_id,type_id,file_id FROM mod_assets WHERE {string.Join(" OR ", predicates)}";
			await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				results.Add(new ModAssetProviderFact(
					new ModNodeId(Guid.ParseExact(reader.GetString(0), "N")),
					reader.GetString(1),
					new HD2ModCore.Domain.AssetKey(ParseHex(reader.GetString(2)), ParseHex(reader.GetString(3)))));
			}
		}
		return results;
	}

	public async ValueTask<ProfileIndexedFacts> ReadProfileFactsAsync(IReadOnlyCollection<ModNodeId> nodeIds, CancellationToken cancellationToken = default)
	{
		var ids = nodeIds.Distinct().Select(id => id.Value.ToString("N")).ToArray();
		if (ids.Length == 0) return ProfileIndexedFacts.Empty;
		await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
		var assets = new List<ProfileIndexedAsset>();
		var references = new List<ProfileIndexedReference>();
		foreach (var batch in ids.Chunk(400))
		{
			var placeholders = string.Join(",", batch.Select((_, index) => "$node" + index));
			await using (var command = connection.CreateCommand())
			{
				command.CommandText = $"SELECT DISTINCT node_id,type_id,file_id FROM mod_assets WHERE node_id IN ({placeholders})";
				for (var index = 0; index < batch.Length; index++) command.Parameters.AddWithValue("$node" + index, batch[index]);
				await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
				while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) assets.Add(new ProfileIndexedAsset(new ModNodeId(Guid.ParseExact(reader.GetString(0), "N")), new AssetKey(ParseHex(reader.GetString(1)), ParseHex(reader.GetString(2)))));
			}
			await using (var command = connection.CreateCommand())
			{
				command.CommandText = $"SELECT DISTINCT node_id,source_type_id,source_file_id,target_type_id,target_file_id,relation_kind FROM asset_references WHERE node_id IN ({placeholders})";
				for (var index = 0; index < batch.Length; index++) command.Parameters.AddWithValue("$node" + index, batch[index]);
				await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
				while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) references.Add(new ProfileIndexedReference(new ModNodeId(Guid.ParseExact(reader.GetString(0), "N")), new AssetKey(ParseHex(reader.GetString(1)), ParseHex(reader.GetString(2))), new AssetKey(ParseHex(reader.GetString(3)), ParseHex(reader.GetString(4))), reader.GetInt32(5)));
			}
		}
		return new ProfileIndexedFacts(assets, references);
	}

	private async ValueTask<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
	{
		Directory.CreateDirectory(paths.IndexDirectory); var connection = new SqliteConnection($"Data Source={paths.ModFactsDbPath}"); await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using var command = connection.CreateCommand(); command.CommandText = $"PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; DROP TABLE IF EXISTS mod_fact_snapshots; DROP TABLE IF EXISTS advanced_mod_analysis_snapshots; CREATE TABLE IF NOT EXISTS schema_info(version INTEGER NOT NULL); INSERT INTO schema_info(version) SELECT {SchemaVersion} WHERE NOT EXISTS(SELECT 1 FROM schema_info); CREATE TABLE IF NOT EXISTS patch_groups(group_id TEXT PRIMARY KEY,node_id TEXT NOT NULL,patch_path TEXT NOT NULL,analyzer_version TEXT NOT NULL); CREATE TABLE IF NOT EXISTS mod_assets(node_id TEXT NOT NULL,group_id TEXT NOT NULL,type_id TEXT NOT NULL,file_id TEXT NOT NULL,toc_size INTEGER NOT NULL,stream_size INTEGER NOT NULL,gpu_size INTEGER NOT NULL,PRIMARY KEY(node_id,group_id,type_id,file_id)); CREATE INDEX IF NOT EXISTS ix_mod_assets_key ON mod_assets(type_id,file_id); CREATE TABLE IF NOT EXISTS asset_references(node_id TEXT NOT NULL,group_id TEXT NOT NULL,source_type_id TEXT NOT NULL,source_file_id TEXT NOT NULL,target_type_id TEXT NOT NULL,target_file_id TEXT NOT NULL,relation_kind INTEGER NOT NULL,payload_offset INTEGER NOT NULL,slot_id INTEGER NULL,reference_index INTEGER NULL); CREATE INDEX IF NOT EXISTS ix_asset_refs_source ON asset_references(source_type_id,source_file_id); CREATE INDEX IF NOT EXISTS ix_asset_refs_target ON asset_references(target_type_id,target_file_id);"; await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); return connection;
	}

	private static async ValueTask ExecuteAsync(SqliteConnection connection, System.Data.Common.DbTransaction transaction, string sql, string node, CancellationToken cancellationToken) { await using var command = connection.CreateCommand(); command.Transaction = (SqliteTransaction)transaction; command.CommandText = sql; command.Parameters.AddWithValue("$node", node); await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); }
	private static async ValueTask ExecuteAsync(SqliteConnection connection, System.Data.Common.DbTransaction transaction, string sql, (string Node, string Group, string Path, string Version) values, CancellationToken cancellationToken) => await ExecuteParametersAsync(connection, transaction, sql, cancellationToken, ("$node", values.Node), ("$group", values.Group), ("$path", values.Path), ("$version", values.Version));
	private static async ValueTask ExecuteAsync(SqliteConnection connection, System.Data.Common.DbTransaction transaction, string sql, (string Node, string Group, string Type, string File, uint Toc, uint Stream, uint Gpu) values, CancellationToken cancellationToken) => await ExecuteParametersAsync(connection, transaction, sql, cancellationToken, ("$node", values.Node), ("$group", values.Group), ("$type", values.Type), ("$file", values.File), ("$toc", values.Toc), ("$stream", values.Stream), ("$gpu", values.Gpu));
	private static async ValueTask ExecuteAsync(SqliteConnection connection, System.Data.Common.DbTransaction transaction, string sql, (string Node, string Group, string SourceType, string SourceFile, string TargetType, string TargetFile, int Kind, uint Offset, uint? Slot, int? Index) values, CancellationToken cancellationToken) => await ExecuteParametersAsync(connection, transaction, sql, cancellationToken, ("$node", values.Node), ("$group", values.Group), ("$sourceType", values.SourceType), ("$sourceFile", values.SourceFile), ("$targetType", values.TargetType), ("$targetFile", values.TargetFile), ("$kind", values.Kind), ("$offset", values.Offset), ("$slot", values.Slot), ("$index", values.Index));
	private static async ValueTask ExecuteParametersAsync(SqliteConnection connection, System.Data.Common.DbTransaction transaction, string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] values) { await using var command = connection.CreateCommand(); command.Transaction = (SqliteTransaction)transaction; command.CommandText = sql; foreach (var value in values) command.Parameters.AddWithValue(value.Name, value.Value ?? DBNull.Value); await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); }
	private static string Hex(ulong value) => value.ToString("x16");
	private static ulong ParseHex(string value) => Convert.ToUInt64(value, 16);
}
