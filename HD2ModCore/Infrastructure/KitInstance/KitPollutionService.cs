using System.Collections.Concurrent;
using HD2ModCore.Domain;
using Microsoft.Data.Sqlite;

namespace HD2ModCore.Infrastructure.KitInstance;

/// <summary>污染分析服务：详情页与部署共用。</summary>
public interface IKitPollutionService
{
	/// <summary>
	/// 分析一个 Mod 覆盖的资源。索引库不存在或读不了时返回 <c>null</c>（调用方当作「没有结论」，不报污染）。
	/// 材质缓存没好时只给 Unit 部分的结论，并在后台开始构建（<paramref name="gameDataDirectory"/> 可用时）；
	/// 本方法<b>绝不等待构建</b>。
	/// </summary>
	Task<KitPollutionResult?> AnalyzeAsync(
		IEnumerable<AssetKey> modAssets, string? gameDataDirectory, CancellationToken cancellationToken = default);

	/// <summary>当前正在后台构建的材质缓存；没有构建时为 <c>null</c>。界面用它在构建完成后刷新。</summary>
	Task? PendingMaterialBuild { get; }
}

/// <summary>转专属规划要的全部游戏数据。<paramref name="IsVanilla"/> 同步查 <c>assets</c> 表（结果有缓存）。</summary>
public sealed record KitConversionContext(
	IReadOnlyList<KitPieceRef> Pieces,
	IReadOnlyDictionary<uint, string> KitNames,
	IReadOnlyDictionary<ulong, IReadOnlyCollection<ulong>>? UnitsByMaterial,
	Func<AssetKey, bool> IsVanilla);

/// <summary>
/// 把索引库（<c>game_data_authority_pieces</c> / <c>_kits</c> / <c>assets</c> / <c>asset_archives</c>）、
/// 材质缓存与 <see cref="KitPollutionAnalyzer"/> 串起来。
/// <para>
/// 构建只在后台进行：同一个缓存路径同一时间只有一个构建（跨实例去重）；构建整体失败时，
/// 本进程内同一个索引键不再重试，避免每次部署都重读几千个 Unit。
/// </para>
/// </summary>
public sealed class KitPollutionService : IKitPollutionService
{
	private static readonly ConcurrentDictionary<string, Task> Builds = new(StringComparer.OrdinalIgnoreCase);
	private static readonly ConcurrentDictionary<string, string> FailedBuilds = new(StringComparer.OrdinalIgnoreCase);
	private static readonly ConcurrentDictionary<string, (string Key, ArmorUnitMaterialMap Map)> MapMemo = new(StringComparer.OrdinalIgnoreCase);
	private static readonly ConcurrentDictionary<string, (string Key, Reference Data)> ReferenceMemo = new(StringComparer.OrdinalIgnoreCase);

	private readonly string _indexDbPath;
	private readonly string _cachePath;
	private readonly Func<string, IArmorUnitTocReader> _readerFactory;

	public KitPollutionService(StoragePaths paths)
		: this(
			(paths ?? throw new ArgumentNullException(nameof(paths))).DbPath,
			Path.Combine(paths.IndexDirectory, ArmorUnitMaterialCacheStore.FileName),
			gameData => new GameDataArmorUnitTocReader(gameData))
	{
	}

	public KitPollutionService(string indexDbPath, string cachePath, Func<string, IArmorUnitTocReader> readerFactory)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(indexDbPath);
		ArgumentException.ThrowIfNullOrWhiteSpace(cachePath);
		_indexDbPath = Path.GetFullPath(indexDbPath);
		_cachePath = Path.GetFullPath(cachePath);
		_readerFactory = readerFactory ?? throw new ArgumentNullException(nameof(readerFactory));
	}

	/// <summary>
	/// 转专属用：与污染分析同一份数据来源（索引库 pieces/kits + 材质缓存），不另起一套读取。
	/// 索引库不存在或没有护甲权威表时返回 <c>null</c>；材质缓存没好时 <see cref="KitConversionContext.UnitsByMaterial"/> 为 <c>null</c>
	/// （并像分析一样在后台开始构建，本方法不等待）。
	/// </summary>
	public async Task<KitConversionContext?> LoadConversionContextAsync(
		string? gameDataDirectory, CancellationToken cancellationToken = default)
	{
		string? key;
		Reference reference;
		try
		{
			key = await ArmorUnitMaterialCacheStore.ComputeIndexKeyAsync(_indexDbPath, cancellationToken).ConfigureAwait(false);
			if (key is null) return null;
			reference = await LoadReferenceAsync(key, cancellationToken).ConfigureAwait(false);
			if (reference.Pieces.Count == 0) return null;
		}
		catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
		{
			return null;
		}

		var materials = await GetMaterialMapAsync(key, gameDataDirectory, reference, cancellationToken).ConfigureAwait(false);
		var indexDb = _indexDbPath;
		var memo = new Dictionary<AssetKey, bool>();
		bool IsVanilla(AssetKey asset)
		{
			lock (memo)
			{
				if (memo.TryGetValue(asset, out var known)) return known;
				using var connection = OpenReadOnly(indexDb);
				connection.Open();
				using var command = connection.CreateCommand();
				command.CommandText = "SELECT 1 FROM assets WHERE type_id = $type AND file_id = $file LIMIT 1;";
				command.Parameters.AddWithValue("$type", unchecked((long)asset.TypeId));
				command.Parameters.AddWithValue("$file", unchecked((long)asset.FileId));
				return memo[asset] = command.ExecuteScalar() is not null;
			}
		}

		return new KitConversionContext(reference.Pieces, reference.KitNames, materials?.UnitsByMaterial, IsVanilla);
	}

	public Task? PendingMaterialBuild => Builds.TryGetValue(_cachePath, out var task) && !task.IsCompleted ? task : null;

	public async Task<KitPollutionResult?> AnalyzeAsync(
		IEnumerable<AssetKey> modAssets, string? gameDataDirectory, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(modAssets);
		var assets = modAssets.Distinct().ToArray();
		// 没有任何 Unit/材质/贴图可分析的 Mod（大多数 Lua/音频 Mod）不去碰索引库。
		if (!assets.Any(key => key.TypeId is KitPollutionAnalyzer.UnitTypeId or KitPollutionAnalyzer.MaterialTypeId or KitPollutionAnalyzer.TextureTypeId))
			return KitPollutionResult.None();

		string? key;
		Reference reference;
		HashSet<AssetKey> vanilla;
		try
		{
			key = await ArmorUnitMaterialCacheStore.ComputeIndexKeyAsync(_indexDbPath, cancellationToken).ConfigureAwait(false);
			if (key is null) return null;
			reference = await LoadReferenceAsync(key, cancellationToken).ConfigureAwait(false);
			if (reference.Pieces.Count == 0) return null;   // 索引里没有护甲权威表：没有结论，而不是「没有污染」
			vanilla = await QueryVanillaAsync(assets, cancellationToken).ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
		{
			return null;
		}

		var materials = await GetMaterialMapAsync(key, gameDataDirectory, reference, cancellationToken).ConfigureAwait(false);
		return KitPollutionAnalyzer.Analyze(
			assets, vanilla.Contains, reference.Pieces, reference.KitNames, materials?.UnitsByMaterial);
	}

	private async Task<ArmorUnitMaterialMap?> GetMaterialMapAsync(
		string key, string? gameDataDirectory, Reference reference, CancellationToken cancellationToken)
	{
		if (MapMemo.TryGetValue(_cachePath, out var memo) && memo.Key == key) return memo.Map;
		var cached = await ArmorUnitMaterialCacheStore.TryReadAsync(_cachePath, key, cancellationToken).ConfigureAwait(false);
		if (cached is not null)
		{
			MapMemo[_cachePath] = (key, cached);
			return cached;
		}

		StartBuild(key, gameDataDirectory, reference);
		return null;
	}

	private void StartBuild(string key, string? gameDataDirectory, Reference reference)
	{
		if (string.IsNullOrWhiteSpace(gameDataDirectory) || !Directory.Exists(gameDataDirectory)) return;
		if (FailedBuilds.TryGetValue(_cachePath, out var failedKey) && failedKey == key) return;

		var indexDb = _indexDbPath;
		var cachePath = _cachePath;
		var factory = _readerFactory;
		Builds.AddOrUpdate(
			cachePath,
			_ => Launch(),
			(_, existing) => existing.IsCompleted ? Launch() : existing);

		Task Launch() => Task.Run(async () =>
		{
			try
			{
				var unitArchives = await QueryUnitArchivesAsync(indexDb, reference.Pieces, CancellationToken.None).ConfigureAwait(false);
				var map = await ArmorUnitMaterialCacheBuilder.BuildAsync(
					unitArchives, factory(gameDataDirectory), CancellationToken.None).ConfigureAwait(false);
				await ArmorUnitMaterialCacheStore.WriteAsync(cachePath, key, map, CancellationToken.None).ConfigureAwait(false);
				MapMemo[cachePath] = (key, map);
			}
			catch (Exception exception) when (exception is IOException
				or UnauthorizedAccessException
				or InvalidDataException
				or SqliteException
				or ArgumentException
				or NotSupportedException)
			{
				// 整体失败（比如索引库或游戏目录读不了）：本进程里同一个键不再重试。界面继续只显示 Unit 部分的结论。
				FailedBuilds[cachePath] = key;
			}
		});
	}

	// ---- 数据库读取 ----------------------------------------------------------------------

	private sealed record Reference(IReadOnlyList<KitPieceRef> Pieces, IReadOnlyDictionary<uint, string> KitNames);

	private async Task<Reference> LoadReferenceAsync(string key, CancellationToken cancellationToken)
	{
		if (ReferenceMemo.TryGetValue(_indexDbPath, out var memo) && memo.Key == key) return memo.Data;

		await using var connection = OpenReadOnly(_indexDbPath);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

		var names = new Dictionary<uint, string>();
		await using (var command = connection.CreateCommand())
		{
			command.CommandText = "SELECT item_id, display_name FROM game_data_authority_kits;";
			await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				var raw = reader.GetInt64(0);
				if (raw is >= 0 and <= uint.MaxValue) names[(uint)raw] = reader.GetString(1);
			}
		}

		var pieces = new List<KitPieceRef>();
		await using (var command = connection.CreateCommand())
		{
			// 披风（slot 1）与 unit 0 留给分析器过滤：过滤规则只在一处。
			command.CommandText = "SELECT item_id, slot, unit_file_id, raw_body_type, body_variant FROM game_data_authority_pieces;";
			await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				var item = reader.GetInt64(0);
				var slot = reader.GetInt64(1);
				if (item is < 0 or > uint.MaxValue || slot is < 0 or > uint.MaxValue) continue;
				pieces.Add(new KitPieceRef(
					(uint)item, (uint)slot, unchecked((ulong)reader.GetInt64(2)),
					checked((int)reader.GetInt64(3)), checked((int)reader.GetInt64(4))));
			}
		}

		var data = new Reference(pieces, names);
		ReferenceMemo[_indexDbPath] = (key, data);
		return data;
	}

	/// <summary>Mod 覆盖的资源里，哪些在 <c>assets</c> 表里（= 原版）。type_id/file_id 按有符号 64 位存。</summary>
	private async Task<HashSet<AssetKey>> QueryVanillaAsync(IEnumerable<AssetKey> assets, CancellationToken cancellationToken)
	{
		var result = new HashSet<AssetKey>();
		await using var connection = OpenReadOnly(_indexDbPath);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT 1 FROM assets WHERE type_id = $type AND file_id = $file LIMIT 1;";
		var type = command.Parameters.Add("$type", SqliteType.Integer);
		var file = command.Parameters.Add("$file", SqliteType.Integer);
		foreach (var asset in assets)
		{
			if (asset.TypeId is not (KitPollutionAnalyzer.UnitTypeId or KitPollutionAnalyzer.MaterialTypeId or KitPollutionAnalyzer.TextureTypeId))
				continue;
			cancellationToken.ThrowIfCancellationRequested();
			type.Value = unchecked((long)asset.TypeId);
			file.Value = unchecked((long)asset.FileId);
			if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null) result.Add(asset);
		}
		return result;
	}

	/// <summary>全部非零 Unit（排除披风）→ 它所在的归档。</summary>
	private static async Task<IReadOnlyDictionary<ulong, IReadOnlyList<string>>> QueryUnitArchivesAsync(
		string indexDbPath, IReadOnlyList<KitPieceRef> pieces, CancellationToken cancellationToken)
	{
		var units = pieces
			.Where(piece => piece.Slot != KitPollutionAnalyzer.CapeSlot && piece.UnitFileId != 0)
			.Select(piece => piece.UnitFileId)
			.ToHashSet();
		var result = new Dictionary<ulong, List<string>>();
		await using var connection = OpenReadOnly(indexDbPath);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT file_id, archive_id FROM asset_archives WHERE type_id = $type ORDER BY archive_id;";
		command.Parameters.AddWithValue("$type", unchecked((long)KitPollutionAnalyzer.UnitTypeId));
		await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			var unit = unchecked((ulong)reader.GetInt64(0));
			if (!units.Contains(unit)) continue;
			if (!result.TryGetValue(unit, out var list)) result[unit] = list = [];
			list.Add(reader.GetString(1));
		}

		// 索引里查不到归档的 Unit 也要进表，这样会被算作失败，而不是悄悄少一块。
		foreach (var unit in units) result.TryAdd(unit, []);
		return result.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value);
	}

	private static SqliteConnection OpenReadOnly(string path) => new($"Data Source={path};Mode=ReadOnly;Cache=Shared");
}
