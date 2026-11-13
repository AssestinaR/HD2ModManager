using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using Microsoft.Data.Sqlite;

namespace HD2ModCore.Infrastructure.KitInstance;

/// <summary>
/// 材质 → Unit 映射：哪些护甲 Unit 绑定了哪个材质。索引库里没有这张表，所以派生一份缓存（规划 §5.1）。
/// 只覆盖 <c>game_data_authority_pieces</c> 里出现过的 Unit。
/// </summary>
public sealed record ArmorUnitMaterialMap(
	IReadOnlyDictionary<ulong, IReadOnlyCollection<ulong>> UnitsByMaterial,
	int UnitCount,
	int FailedUnitCount,
	double BuildSeconds,
	DateTimeOffset BuiltUtc);

/// <summary>读一个 Unit 的 TOC 数据。找不到返回 <c>null</c>；读失败抛 <see cref="IOException"/> / <see cref="InvalidDataException"/> 等。</summary>
public interface IArmorUnitTocReader
{
	ValueTask<byte[]?> ReadUnitTocAsync(string archiveId, ulong unitFileId, CancellationToken cancellationToken);
}

/// <summary>
/// 构建缓存。<b>单个 Unit 读失败只跳过并计数</b>，不让整个缓存失败；调用方按归档分组传入以便读取端复用 TOC。
/// </summary>
public static class ArmorUnitMaterialCacheBuilder
{
	/// <param name="unitArchives">Unit → 它出现的归档 ID（按顺序尝试，第一个读成功的算数）。</param>
	public static async Task<ArmorUnitMaterialMap> BuildAsync(
		IReadOnlyDictionary<ulong, IReadOnlyList<string>> unitArchives,
		IArmorUnitTocReader reader,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(unitArchives);
		ArgumentNullException.ThrowIfNull(reader);
		var stopwatch = Stopwatch.StartNew();
		var materialReader = new UnitMaterialReferenceReader();
		var map = new Dictionary<ulong, SortedSet<ulong>>();
		var failed = 0;

		// 同一归档里的 Unit 连着读，读取端对「当前归档」的目录只需解析一次。
		var ordered = unitArchives
			.OrderBy(pair => pair.Value.Count == 0 ? string.Empty : pair.Value[0], StringComparer.Ordinal)
			.ThenBy(pair => pair.Key);
		foreach (var (unit, archives) in ordered)
		{
			cancellationToken.ThrowIfCancellationRequested();
			IReadOnlyList<UnitMaterialReferenceBinding>? bindings = null;
			foreach (var archive in archives)
			{
				try
				{
					var toc = await reader.ReadUnitTocAsync(archive, unit, cancellationToken).ConfigureAwait(false);
					if (toc is null) continue;
					bindings = materialReader.ReadReferenceBindings(toc);
					break;
				}
				catch (Exception exception) when (exception is IOException
					or UnauthorizedAccessException
					or InvalidDataException
					or EndOfStreamException
					or OverflowException
					or ArgumentException
					or NotSupportedException)
				{
					// 换下一个归档重试；都失败就计一次。
				}
			}

			if (bindings is null) { failed++; continue; }
			foreach (var binding in bindings)
			{
				if (!map.TryGetValue(binding.MaterialId, out var units)) map[binding.MaterialId] = units = [];
				units.Add(unit);
			}
		}

		stopwatch.Stop();
		return new ArmorUnitMaterialMap(
			map.ToDictionary(pair => pair.Key, pair => (IReadOnlyCollection<ulong>)pair.Value.ToArray()),
			unitArchives.Count,
			failed,
			stopwatch.Elapsed.TotalSeconds,
			DateTimeOffset.UtcNow);
	}
}

/// <summary>
/// <c>&lt;DataDirectory&gt;/indexes/armor-unit-materials.json</c> 的读写。
/// 缓存键 = 索引库的生成标识；键不一致（索引重建过）即视为不存在，由调用方后台重建。
/// 写入走临时文件 + 原子替换，读的一侧看不到半份。
/// </summary>
public static class ArmorUnitMaterialCacheStore
{
	public const string FileName = "armor-unit-materials.json";
	public const int SchemaVersion = 1;
	private const long MaximumBytes = 64 * 1024 * 1024;

	private static readonly JsonSerializerOptions Options = new()
	{
		WriteIndented = false,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
	};

	/// <summary>
	/// 索引库的生成标识：<c>meta</c> 表里的 <c>source_fingerprint</c> 加 <c>built_utc</c>；
	/// 两者都没有时退回「文件长度 + 最后写入时间」。文件不存在返回 <c>null</c>。
	/// </summary>
	public static async Task<string?> ComputeIndexKeyAsync(string indexDbPath, CancellationToken cancellationToken = default)
	{
		if (!File.Exists(indexDbPath)) return null;
		string? fingerprint = null, built = null;
		try
		{
			await using var connection = new SqliteConnection($"Data Source={indexDbPath};Mode=ReadOnly;Cache=Shared");
			await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
			await using var command = connection.CreateCommand();
			command.CommandText = "SELECT key, value FROM meta WHERE key IN ('source_fingerprint', 'built_utc');";
			await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				var key = reader.GetString(0);
				if (key == "source_fingerprint") fingerprint = reader.GetString(1);
				else built = reader.GetString(1);
			}
		}
		catch (SqliteException)
		{
			// meta 表不存在 / 库打不开：走文件特征。
		}

		if (!string.IsNullOrEmpty(fingerprint) || !string.IsNullOrEmpty(built))
			return $"meta:{fingerprint}|{built}";
		var info = new FileInfo(indexDbPath);
		return string.Create(CultureInfo.InvariantCulture, $"file:{info.Length}|{info.LastWriteTimeUtc.Ticks}");
	}

	/// <summary>读缓存；文件不存在、损坏、schema 或键不匹配一律返回 <c>null</c>（= 需要重建）。</summary>
	public static async Task<ArmorUnitMaterialMap?> TryReadAsync(
		string cachePath, string expectedKey, CancellationToken cancellationToken = default)
	{
		try
		{
			if (!File.Exists(cachePath) || new FileInfo(cachePath).Length > MaximumBytes) return null;
			var json = await File.ReadAllTextAsync(cachePath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
			var file = JsonSerializer.Deserialize<CacheFile>(json, Options);
			if (file is null || file.Schema != SchemaVersion || !string.Equals(file.Key, expectedKey, StringComparison.Ordinal))
				return null;
			var map = new Dictionary<ulong, IReadOnlyCollection<ulong>>();
			foreach (var (material, units) in file.Materials ?? [])
			{
				map[ParseHex(material)] = (units ?? []).Select(ParseHex).ToArray();
			}
			return new ArmorUnitMaterialMap(map, file.UnitCount, file.FailedUnitCount, file.BuildSeconds, file.BuiltUtc);
		}
		catch (Exception exception) when (exception is IOException
			or UnauthorizedAccessException
			or JsonException
			or FormatException
			or OverflowException)
		{
			return null;
		}
	}

	public static async Task WriteAsync(
		string cachePath, string key, ArmorUnitMaterialMap map, CancellationToken cancellationToken = default)
	{
		var file = new CacheFile
		{
			Schema = SchemaVersion,
			Key = key,
			BuiltUtc = map.BuiltUtc,
			BuildSeconds = Math.Round(map.BuildSeconds, 3),
			UnitCount = map.UnitCount,
			FailedUnitCount = map.FailedUnitCount,
			Materials = map.UnitsByMaterial
				.OrderBy(pair => pair.Key)
				.ToDictionary(
					pair => Hex(pair.Key),
					pair => pair.Value.OrderBy(unit => unit).Select(Hex).ToArray()),
		};
		await KitInstanceFiles.WriteAtomicTextAsync(
			cachePath, JsonSerializer.Serialize(file, Options), cancellationToken).ConfigureAwait(false);
	}

	private static string Hex(ulong value) => value.ToString("x16", CultureInfo.InvariantCulture);
	private static ulong ParseHex(string value) => ulong.Parse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);

	private sealed class CacheFile
	{
		public int Schema { get; set; }
		public string? Key { get; set; }
		public DateTimeOffset BuiltUtc { get; set; }
		public double BuildSeconds { get; set; }
		public int UnitCount { get; set; }
		public int FailedUnitCount { get; set; }
		public Dictionary<string, string[]>? Materials { get; set; }
	}
}

/// <summary>真实读取端：直接读游戏数据包（与 <c>gamedata extract asset</c> 同一条路径）。同一归档的目录只枚举一次。</summary>
public sealed class GameDataArmorUnitTocReader : IArmorUnitTocReader
{
	private readonly GameDataAssetCatalogService _catalog;
	private string? _currentArchive;
	private Dictionary<ulong, GameDataAssetDescriptor> _currentUnits = [];

	public GameDataArmorUnitTocReader(string gameDataDirectory)
		=> _catalog = CoreServices.CreateGameDataAssetCatalogService(gameDataDirectory);

	public async ValueTask<byte[]?> ReadUnitTocAsync(string archiveId, ulong unitFileId, CancellationToken cancellationToken)
	{
		if (!string.Equals(_currentArchive, archiveId, StringComparison.OrdinalIgnoreCase))
		{
			// 先清掉当前归档，免得这次枚举失败后又拿上一个归档的目录来答。
			_currentArchive = null;
			_currentUnits = [];
			var scan = await _catalog.EnumerateAsync([KitPollutionAnalyzer.UnitTypeId], archiveId, cancellationToken).ConfigureAwait(false);
			_currentUnits = scan.Assets.GroupBy(asset => asset.FileId).ToDictionary(group => group.Key, group => group.First());
			_currentArchive = archiveId;
		}

		return _currentUnits.TryGetValue(unitFileId, out var descriptor)
			? await _catalog.ReadPartAsync(descriptor, GameDataAssetPart.TocData, cancellationToken).ConfigureAwait(false)
			: null;
	}
}
