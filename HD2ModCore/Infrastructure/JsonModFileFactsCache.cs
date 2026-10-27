using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using HD2ModCore.Application;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

// 作用：以原子替换 JSON 文件持久化 FileFacts，不依赖 SQLite。
// Purpose: Persists FileFacts as atomically replaced JSON without SQLite.
//
// FileFacts 的缓存文件按"整库快照的 generation"命名，所以一个文件可能同时包含多个节点的事实。
// 以前按节点失效只能把目录里每个 *.json 打开、JSON 解析一遍，再判断 filesByNode 里有没有这个节点：
// 实测 955 个文件 / 30.6 MB，单节点 150 ms（热缓存）/ 3.7 s（冷缓存），批删 N 个节点就重复 N 次。
// 现在额外维护一个 owners.index（generation → 该文件覆盖的节点集合），失效变成一次 O(1) 查表。
//
// Cache files are named after the generation of a whole-library snapshot, so one file can carry facts for
// several nodes. Invalidation used to open and JSON-parse every *.json in the directory to find out which
// files mentioned a node (955 files / 30.6 MB: 150 ms warm, 3.7 s cold, repeated once per deleted node).
// A sidecar owners.index (generation → covered nodes) makes invalidation a single O(1) lookup.
public sealed class JsonModFileFactsCache : IModFileFactsCache
{
	private const string ProducerVersion = "filesystem-v1";
	private const int SchemaVersion = 1;
	private const int OwnersVersion = 1;
	// 故意不用 .json 扩展名：旧版全量扫描和 JsonModInformationCache 的通配都按 *.json 枚举，
	// 让索引文件不参与那些枚举。Deliberately not .json so legacy globs never pick it up.
	private const string OwnersFileName = "owners.index";
	// 同一目录里还躺着 JsonModInformationCache 的按节点缓存（<kind>_<nodeId>_<generation>.json）。
	// 它们不是 FileFacts，解析它们既浪费时间又没有意义——直接按文件名排除。
	// The same directory also holds JsonModInformationCache's per-node files; they are not FileFacts,
	// so they are excluded by name instead of being parsed on every pass.
	private static readonly System.Text.RegularExpressions.Regex ForeignCacheFile = new(
		@"^[A-Za-z]+_[0-9a-fA-F]{32}_[0-9a-fA-F]{64}$",
		System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
	private static readonly ConcurrentDictionary<string, SemaphoreSlim> DirectoryGates = new(StringComparer.OrdinalIgnoreCase);
	private readonly string _directory;
	private static readonly JsonSerializerOptions Options = CreateOptions();

	private static JsonSerializerOptions CreateOptions()
	{
		var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = false };
		options.Converters.Add(new ModNodeIdJsonConverter());
		return options;
	}

	public JsonModFileFactsCache(StoragePaths paths)
	{
		ArgumentNullException.ThrowIfNull(paths);
		_directory = Path.Combine(paths.IndexDirectory, "mod-information");
	}

	public async ValueTask<PatchFileIndex?> TryLoadAsync(string generation, CancellationToken cancellationToken = default)
	{
		var path = GetPath(generation);
		if (!File.Exists(path)) return null;
		await using var stream = File.OpenRead(path);
		using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
		if (document.RootElement.TryGetProperty("facts", out _))
			return document.RootElement.Deserialize<CacheEnvelope>(Options)?.Facts;
		return document.RootElement.Deserialize<PatchFileIndex>(Options);
	}

	public async ValueTask SaveAsync(string generation, PatchFileIndex facts, CancellationToken cancellationToken = default)
	{
		Directory.CreateDirectory(_directory);
		var path = GetPath(generation);
		var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			await using (var stream = File.Create(temporary))
				await JsonSerializer.SerializeAsync(stream, new CacheEnvelope(facts, generation, ProducerVersion, SchemaVersion, DateTimeOffset.UtcNow), Options, cancellationToken).ConfigureAwait(false);
			File.Move(temporary, path, true);
		}
		finally
		{
			if (File.Exists(temporary)) File.Delete(temporary);
		}

		var gate = GetDirectoryGate();
		await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			var owners = LoadOwners();
			owners.Generations[generation] = (facts.FilesByNode?.Keys ?? Enumerable.Empty<ModNodeId>())
				.Select(nodeId => nodeId.Value.ToString("N"))
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToArray();
			SaveOwners(owners);
		}
		finally
		{
			gate.Release();
		}
	}

	public async ValueTask DeleteNodeAsync(ModNodeId nodeId, CancellationToken cancellationToken = default)
	{
		if (!Directory.Exists(_directory)) return;

		var gate = GetDirectoryGate();
		await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			var token = nodeId.Value.ToString("N");
			var owners = LoadOwners();
			var changed = false;
			var deletedGenerations = new List<string>();
			foreach (var pair in owners.Generations)
			{
				if (!pair.Value.Contains(token, StringComparer.OrdinalIgnoreCase)) continue;
				TryDeleteFile(GetPath(pair.Key));
				deletedGenerations.Add(pair.Key);
			}
			foreach (var generation in deletedGenerations) owners.Generations.Remove(generation);
			changed |= deletedGenerations.Count != 0;

			// 没有登记在 owners.index 里的文件只可能是旧版本留下的，或者索引文件丢失/损坏。
			// 只对这部分退回全量扫描，扫描完就把归属补登记，之后不再重复。
			// Files missing from owners.index can only be legacy files or a lost/corrupt index; scan just
			// those, then record their ownership so the fallback never runs twice.
			var tracked = owners.Generations.Keys.Select(Sanitize).ToHashSet(StringComparer.OrdinalIgnoreCase);
			foreach (var path in Directory.EnumerateFiles(_directory, "*.json"))
			{
				cancellationToken.ThrowIfCancellationRequested();
				var generation = Path.GetFileNameWithoutExtension(path);
				if (tracked.Contains(generation)) continue;
				if (ForeignCacheFile.IsMatch(generation)) continue;
				if (await ContainsNodeAsync(path, nodeId, cancellationToken).ConfigureAwait(false))
				{
					TryDeleteFile(path);
					changed = true;
				}
				else if (TryReadNodeIds(path, out var covered))
				{
					owners.Generations[generation] = covered;
					changed = true;
				}
			}

			if (changed) SaveOwners(owners);
		}
		finally
		{
			gate.Release();
		}
	}

	private static async ValueTask<bool> ContainsNodeAsync(string path, ModNodeId nodeId, CancellationToken cancellationToken)
	{
		try
		{
			var facts = await ReadFactsAsync(path, cancellationToken).ConfigureAwait(false);
			if (facts?.FilesByNode is null) return false;
			return facts.FilesByNode.ContainsKey(nodeId);
		}
		catch (FileNotFoundException) { return false; }
		catch (DirectoryNotFoundException) { return false; }
		catch (IOException) { return false; }
		catch (UnauthorizedAccessException) { return false; }
		catch (System.Security.SecurityException) { return false; }
		catch (JsonException) { return false; }
	}

	private static bool TryReadNodeIds(string path, out string[] nodeIds)
	{
		nodeIds = Array.Empty<string>();
		try
		{
			using var stream = File.OpenRead(path);
			using var document = JsonDocument.Parse(stream);
			var root = document.RootElement;
			var facts = root.ValueKind == JsonValueKind.Object && TryGetPropertyIgnoreCase(root, "facts", out var envelopeFacts)
				? envelopeFacts
				: root;
			if (facts.ValueKind != JsonValueKind.Object
				|| !TryGetPropertyIgnoreCase(facts, "filesByNode", out var filesByNode)
				|| filesByNode.ValueKind != JsonValueKind.Object)
				return false;
			nodeIds = filesByNode.EnumerateObject().Select(property => property.Name).ToArray();
			return true;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or JsonException)
		{
			return false;
		}
	}

	private static async ValueTask<PatchFileIndex?> ReadFactsAsync(string path, CancellationToken cancellationToken)
	{
		await using var stream = File.OpenRead(path);
		using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
		var root = document.RootElement;
		if (root.ValueKind == JsonValueKind.Object && TryGetPropertyIgnoreCase(root, "facts", out _))
			return root.Deserialize<CacheEnvelope>(Options)?.Facts;
		return root.Deserialize<PatchFileIndex>(Options);
	}

	private void TryDeleteFile(string path)
	{
		try
		{
			if (!File.Exists(path)) return;
			var attributes = File.GetAttributes(path);
			if ((attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
			File.Delete(path);
		}
		catch (FileNotFoundException) { }
		catch (DirectoryNotFoundException) { }
		catch (IOException) { }
		// 并发失效时同一个文件可能正被另一个线程删除或读取：File.Delete 会拿到 ERROR_ACCESS_DENIED
		// 而不是 ERROR_SHARING_VIOLATION。它和 IOException 一样属于"这个文件已经不是我们能处理的"。
		// Concurrent invalidation can surface ERROR_ACCESS_DENIED from File.Delete.
		catch (UnauthorizedAccessException) { }
		catch (System.Security.SecurityException) { }
	}

	private OwnersDocument LoadOwners()
	{
		var path = Path.Combine(_directory, OwnersFileName);
		if (!File.Exists(path)) return new OwnersDocument(OwnersVersion, new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase));
		try
		{
			var document = JsonSerializer.Deserialize<OwnersDocument>(File.ReadAllText(path), Options);
			if (document is null || document.Version != OwnersVersion || document.Generations is null)
				return new OwnersDocument(OwnersVersion, new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase));
			return document with { Generations = new Dictionary<string, string[]>(document.Generations, StringComparer.OrdinalIgnoreCase) };
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
		{
			// 索引坏了就当作"什么都没登记"，退回全量扫描一次后重建。
			// A corrupt index degrades to "nothing tracked", i.e. one full scan that then rebuilds it.
			return new OwnersDocument(OwnersVersion, new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase));
		}
	}

	private void SaveOwners(OwnersDocument document)
	{
		var path = Path.Combine(_directory, OwnersFileName);
		var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			File.WriteAllText(temporary, JsonSerializer.Serialize(document, Options));
			File.Move(temporary, path, true);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// 索引写失败不影响缓存本身的正确性：下次失效会退回全量扫描。
			// A failed index write only costs a future full scan; cache correctness is unaffected.
		}
		finally
		{
			if (File.Exists(temporary)) TryDeleteFile(temporary);
		}
	}

	private SemaphoreSlim GetDirectoryGate()
		=> DirectoryGates.GetOrAdd(Path.GetFullPath(_directory), static _ => new SemaphoreSlim(1, 1));

	private static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
	{
		foreach (var property in element.EnumerateObject())
		{
			if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
			{
				value = property.Value;
				return true;
			}
		}
		value = default;
		return false;
	}
	private static string Sanitize(string value) => string.IsNullOrWhiteSpace(value) ? "unknown" : string.Concat(value.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_'));
	private string GetPath(string generation) => Path.Combine(_directory, Sanitize(generation) + ".json");
	private sealed record CacheEnvelope(PatchFileIndex Facts, string Generation, string ProducerVersion, int SchemaVersion, DateTimeOffset BuiltUtc);
	private sealed record OwnersDocument(int Version, Dictionary<string, string[]> Generations);

	private sealed class ModNodeIdJsonConverter : JsonConverter<ModNodeId>
	{
		public override ModNodeId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
			=> new(Guid.Parse(reader.GetString()!));
		public override void Write(Utf8JsonWriter writer, ModNodeId value, JsonSerializerOptions options)
			=> writer.WriteStringValue(value.Value.ToString("N"));
		public override ModNodeId ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
			=> new(Guid.ParseExact(reader.GetString()!, "N"));
		public override void WriteAsPropertyName(Utf8JsonWriter writer, ModNodeId value, JsonSerializerOptions options)
			=> writer.WritePropertyName(value.Value.ToString("N"));
	}
}
