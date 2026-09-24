using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HD2ModCore.Domain;
using Microsoft.Data.Sqlite;

namespace HD2ModCore.Infrastructure;

public sealed class RuntimeArmorRegistryService
{
	public const string ProtocolFileName = "armor-registry.protocol";
	public const string RuntimeStatusFileName = "runtime.status";
	public const string LocatorCacheFileName = "locator.cache";
	public const string DeploymentStateFileName = "runtime-armor-registry.deployment";

	private const int MaximumProtocolBytes = 65536;
	private readonly StoragePaths paths;
	private readonly string registryRoot;

	public RuntimeArmorRegistryService(StoragePaths paths, string? registryRoot = null)
	{
		this.paths = paths ?? throw new ArgumentNullException(nameof(paths));
		this.registryRoot = ResolveRegistryRoot(registryRoot);
	}

	public string RegistryRoot => registryRoot;
	public string DeploymentStatePath => Path.Combine(paths.DataDirectory, DeploymentStateFileName);

	public async ValueTask<bool> IsManagedDeploymentEnabledAsync(
		CancellationToken cancellationToken = default)
	{
		if (!File.Exists(DeploymentStatePath)) return false;
		var text = await ReadBoundedTextAsync(DeploymentStatePath, 256, cancellationToken).ConfigureAwait(false);
		return string.Equals(text.Trim(), "enabled=true", StringComparison.Ordinal);
	}

	public static string ResolveRegistryRoot(string? overridePath = null)
	{
		var path = string.IsNullOrWhiteSpace(overridePath)
			? Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
				"CowboyBingus",
				"Helldivers2",
				"ArmorRegistry")
			: overridePath;
		return Path.GetFullPath(path);
	}

	public async ValueTask<IReadOnlyList<RuntimeArmorCatalogEntry>> ReadArmorCatalogAsync(
		CancellationToken cancellationToken = default)
	{
		if (!File.Exists(paths.DbPath)) return Array.Empty<RuntimeArmorCatalogEntry>();
		await using var connection = new SqliteConnection($"Data Source={paths.DbPath};Mode=ReadOnly;Cache=Shared");
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using var command = connection.CreateCommand();
		command.CommandText = """
			SELECT item_id, archive_id, display_name
			FROM game_data_authority_kits
			WHERE kit_kind = 0
			ORDER BY display_name, archive_id, item_id;
			""";
		var entries = new List<RuntimeArmorCatalogEntry>();
		await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			var raw = reader.GetInt64(0);
			if (raw < 0 || raw > uint.MaxValue) continue;
			entries.Add(new RuntimeArmorCatalogEntry(
				(uint)raw,
				reader.GetString(1),
				reader.GetString(2)));
		}
		return entries;
	}

	public async ValueTask<RuntimeArmorRegistrySnapshot> ReadSnapshotAsync(
		CancellationToken cancellationToken = default)
	{
		var protocolPath = Path.Combine(registryRoot, ProtocolFileName);
		var runtimePath = Path.Combine(registryRoot, RuntimeStatusFileName);
		var cachePath = Path.Combine(registryRoot, LocatorCacheFileName);
		var protocolExists = File.Exists(protocolPath);
		var document = protocolExists
			? ParseProtocol(await ReadBoundedTextAsync(protocolPath, MaximumProtocolBytes, cancellationToken).ConfigureAwait(false))
			: new RuntimeArmorRegistryDocument(0, false, Array.Empty<RuntimeArmorRegistryMapping>());
		var fingerprint = await FingerprintProtocolAsync(cancellationToken).ConfigureAwait(false);
		var runtime = await ReadKeyValueFileAsync(runtimePath, cancellationToken).ConfigureAwait(false);
		var cache = await ReadKeyValueFileAsync(cachePath, cancellationToken).ConfigureAwait(false);
		var processRunning = IsRuntimeProcessRunning(runtime.Values);
		var acknowledged = processRunning
			&& runtime.Values.TryGetValue("state", out var state)
			&& string.Equals(state, "ready", StringComparison.Ordinal)
			&& runtime.Values.TryGetValue("revision", out var rawRevision)
			&& int.TryParse(rawRevision, NumberStyles.Integer, CultureInfo.InvariantCulture, out var revision)
			&& revision == document.Revision;
		return new RuntimeArmorRegistrySnapshot(
			registryRoot,
			protocolPath,
			fingerprint,
			protocolExists,
			document,
			runtime.Exists,
			processRunning,
			acknowledged,
			runtime.Values,
			cache.Exists,
			cache.Values);
	}

	public async ValueTask<RuntimeArmorRegistryApplyResult> ApplyAsync(
		string expectedFingerprint,
		bool enabled,
		IReadOnlyList<RuntimeArmorRegistryMapping> mappings,
		CancellationToken cancellationToken = default)
		=> await ApplyAsync(expectedFingerprint, enabled, mappings,
			Array.Empty<RuntimeArmorRegistryModel>(), cancellationToken).ConfigureAwait(false);

	public async ValueTask<RuntimeArmorRegistryApplyResult> ApplyAsync(
		string expectedFingerprint,
		bool enabled,
		IReadOnlyList<RuntimeArmorRegistryMapping> mappings,
		IReadOnlyList<RuntimeArmorRegistryModel> models,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(expectedFingerprint);
		ArgumentNullException.ThrowIfNull(mappings);
		ArgumentNullException.ThrowIfNull(models);
		ValidateMappings(mappings);
		ValidateModels(models, mappings);
		Directory.CreateDirectory(registryRoot);
		var lockPath = Path.Combine(registryRoot, ".manager.lock");
		await using var lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
		var currentFingerprint = await FingerprintProtocolAsync(cancellationToken).ConfigureAwait(false);
		if (!string.Equals(currentFingerprint, expectedFingerprint, StringComparison.Ordinal))
			throw new InvalidOperationException("护甲注册协议已被其他操作修改，请刷新后重试。");
		var current = File.Exists(Path.Combine(registryRoot, ProtocolFileName))
			? ParseProtocol(await ReadBoundedTextAsync(
				Path.Combine(registryRoot, ProtocolFileName),
				MaximumProtocolBytes,
				cancellationToken).ConfigureAwait(false))
			: new RuntimeArmorRegistryDocument(0, false, Array.Empty<RuntimeArmorRegistryMapping>());
		if (current.Revision == int.MaxValue)
			throw new InvalidOperationException("护甲注册协议 revision 已达到上限。");
		var desired = new RuntimeArmorRegistryDocument(
			current.Revision + 1,
			enabled,
			NormalizeMappings(mappings),
			NormalizeModels(models));
		var text = SerializeProtocol(desired);
		var protocolPath = Path.Combine(registryRoot, ProtocolFileName);
		var temporaryPath = Path.Combine(registryRoot, "." + ProtocolFileName + "." + Guid.NewGuid().ToString("N") + ".tmp");
		try
		{
			await File.WriteAllTextAsync(
				temporaryPath,
				text,
				new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
				cancellationToken).ConfigureAwait(false);
			File.Move(temporaryPath, protocolPath, overwrite: true);
		}
		finally
		{
			if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
		}
		Directory.CreateDirectory(paths.DataDirectory);
		await File.WriteAllTextAsync(
			DeploymentStatePath,
			enabled ? "enabled=true\n" : "enabled=false\n",
			new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
			cancellationToken).ConfigureAwait(false);
		var snapshot = await ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
		return new RuntimeArmorRegistryApplyResult(
			snapshot.Document,
			snapshot.Fingerprint,
			snapshot.RuntimeProcessRunning,
			snapshot.RuntimeAcknowledged);
	}

	public static RuntimeArmorRegistryDocument ParseProtocol(string text)
	{
		ArgumentNullException.ThrowIfNull(text);
		if (Encoding.UTF8.GetByteCount(text) > MaximumProtocolBytes)
			throw new InvalidDataException("护甲注册协议超过 64 KiB 上限。");
		int? schema = null;
		int? revision = null;
		bool? enabled = null;
		var mappings = new List<RuntimeArmorRegistryMapping>();
		var models = new List<RuntimeArmorRegistryModel>();
		var modelKeys = new HashSet<string>(StringComparer.Ordinal);
		var targets = new HashSet<uint>();
		var lineNumber = 0;
		foreach (var rawLine in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
		{
			lineNumber++;
			var line = rawLine.Trim();
			if (line.Length == 0 || line.StartsWith('#')) continue;
			var separator = line.IndexOf('=');
			if (separator <= 0) throw InvalidLine(lineNumber, "缺少 key=value 结构。");
			var key = line[..separator].Trim();
			var value = line[(separator + 1)..].Trim();
			switch (key)
			{
				case "schema":
					if (schema is not null) throw InvalidLine(lineNumber, "schema 重复。");
					schema = ParseInteger(value, lineNumber, "schema", 1, 2);
					break;
				case "revision":
					if (revision is not null) throw InvalidLine(lineNumber, "revision 重复。");
					revision = ParseInteger(value, lineNumber, "revision", 0, int.MaxValue);
					break;
				case "enabled":
					if (enabled is not null) throw InvalidLine(lineNumber, "enabled 重复。");
					enabled = value switch
					{
						"true" => true,
						"false" => false,
						_ => throw InvalidLine(lineNumber, "enabled 必须是 true 或 false。"),
					};
					break;
				case "map":
					var parts = value.Split(',');
					if (parts.Length != 2) throw InvalidLine(lineNumber, "map 必须是 target_item_id,source_item_id。");
					var target = ParseItemId(parts[0], lineNumber, "目标");
					var source = ParseItemId(parts[1], lineNumber, "来源");
					if (target == source) throw InvalidLine(lineNumber, "目标与来源护甲不能相同。");
					if (!targets.Add(target)) throw InvalidLine(lineNumber, $"目标护甲 {target} 重复。");
					mappings.Add(new RuntimeArmorRegistryMapping(target, source, null));
					break;
				case "model":
					var modelParts = value.Split(',');
					if (modelParts.Length != 2) throw InvalidLine(lineNumber, "model 必须是 key,source_item_id。");
					var modelKey = ParseModelKey(modelParts[0], lineNumber);
					var modelSource = string.IsNullOrWhiteSpace(modelParts[1])
						? (uint?)null
						: ParseItemId(modelParts[1], lineNumber, "模型来源");
					if (!modelKeys.Add(modelKey)) throw InvalidLine(lineNumber, $"模型键 {modelKey} 重复。");
					models.Add(new RuntimeArmorRegistryModel(modelKey, modelSource));
					break;
				case "model_piece":
					var pieceParts = value.Split(',');
					if (pieceParts.Length != 6)
						throw InvalidLine(lineNumber, "model_piece 必须是 key,body,unit,slot,piece_type,tone。");
					_ = ParseModelKey(pieceParts[0], lineNumber);
					_ = ParseInteger(pieceParts[1], lineNumber, "model body", 0, int.MaxValue);
					var unit = pieceParts[2].Trim();
					if (unit.Length != 16 || !unit.All(Uri.IsHexDigit))
						throw InvalidLine(lineNumber, "model unit 必须是 16 位十六进制 ID。");
					_ = ParseInteger(pieceParts[3], lineNumber, "model slot", 1, int.MaxValue);
					_ = ParseInteger(pieceParts[4], lineNumber, "model piece type", 0, int.MaxValue);
					_ = ParseInteger(pieceParts[5], lineNumber, "model tone", 0, 255);
					break;
				case "map_model":
					var modelMappingParts = value.Split(',');
					if (modelMappingParts.Length != 2) throw InvalidLine(lineNumber, "map_model 必须是 target_item_id,model_key。");
					var modelTarget = ParseItemId(modelMappingParts[0], lineNumber, "目标");
					var targetModelKey = ParseModelKey(modelMappingParts[1], lineNumber);
					if (!targets.Add(modelTarget)) throw InvalidLine(lineNumber, $"目标护甲 {modelTarget} 重复。");
					mappings.Add(new RuntimeArmorRegistryMapping(modelTarget, null, targetModelKey));
					break;
				default:
					throw InvalidLine(lineNumber, $"未知字段：{key}");
			}
		}
		if (schema is not (1 or 2)) throw new InvalidDataException("护甲注册协议必须包含 schema=1 或 schema=2。");
		if (revision is null) throw new InvalidDataException("护甲注册协议缺少 revision。");
		if (enabled is null) throw new InvalidDataException("护甲注册协议缺少 enabled。");
		if (schema == 1 && models.Count > 0)
			throw new InvalidDataException("schema=1 不支持模型素体。");
		return new RuntimeArmorRegistryDocument(revision.Value, enabled.Value,
			NormalizeMappings(mappings), models.OrderBy(model => model.Key, StringComparer.Ordinal).ToArray());
	}

	public static string SerializeProtocol(RuntimeArmorRegistryDocument document)
	{
		ArgumentNullException.ThrowIfNull(document);
		if (document.Revision < 0) throw new InvalidDataException("护甲注册协议 revision 无效。");
		ValidateMappings(document.Mappings);
		var builder = new StringBuilder()
			.Append("schema=2\n")
			.Append("revision=").Append(document.Revision.ToString(CultureInfo.InvariantCulture)).Append('\n')
			.Append("enabled=").Append(document.Enabled ? "true" : "false").Append('\n');
		foreach (var model in document.ModelDefinitions.OrderBy(model => model.Key, StringComparer.Ordinal))
		{
			builder.Append("model=")
				.Append(model.Key).Append(',')
				.Append(model.SourceItemId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append('\n');
		}
		foreach (var mapping in NormalizeMappings(document.Mappings))
		{
			builder.Append(mapping.IsModelMapping ? "map_model=" : "map=")
				.Append(mapping.TargetItemId.ToString(CultureInfo.InvariantCulture))
				.Append(',')
				.Append(mapping.IsModelMapping
					? mapping.ModelKey
					: mapping.SourceItemId.GetValueOrDefault().ToString(CultureInfo.InvariantCulture))
				.Append('\n');
		}
		return builder.ToString();
	}

	private async ValueTask<string> FingerprintProtocolAsync(CancellationToken cancellationToken)
	{
		var path = Path.Combine(registryRoot, ProtocolFileName);
		if (!File.Exists(path)) return "missing";
		var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
		return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
	}

	private static async ValueTask<string> ReadBoundedTextAsync(
		string path,
		int maximumBytes,
		CancellationToken cancellationToken)
	{
		if (new FileInfo(path).Length > maximumBytes)
			throw new InvalidDataException($"文件超过 {maximumBytes} 字节上限：{path}");
		return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
	}

	private static async ValueTask<(bool Exists, IReadOnlyDictionary<string, string> Values)> ReadKeyValueFileAsync(
		string path,
		CancellationToken cancellationToken)
	{
		if (!File.Exists(path))
			return (false, new Dictionary<string, string>(StringComparer.Ordinal));
		var values = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var rawLine in await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false))
		{
			var separator = rawLine.IndexOf('=');
			if (separator <= 0) continue;
			values[rawLine[..separator].Trim()] = rawLine[(separator + 1)..].Trim();
		}
		return (true, values);
	}

	private static bool IsRuntimeProcessRunning(IReadOnlyDictionary<string, string> values)
	{
		if (!values.TryGetValue("pid", out var raw)
			|| !int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
			|| pid <= 0)
			return false;
		try
		{
			using var process = Process.GetProcessById(pid);
			return !process.HasExited
				&& string.Equals(process.ProcessName, "helldivers2", StringComparison.OrdinalIgnoreCase);
		}
		catch (Exception exception) when (exception is ArgumentException
			or InvalidOperationException
			or System.ComponentModel.Win32Exception)
		{
			return false;
		}
	}

	private static void ValidateMappings(IEnumerable<RuntimeArmorRegistryMapping> mappings)
	{
		var targets = new HashSet<uint>();
		foreach (var mapping in mappings)
		{
			if (!mapping.IsModelMapping && mapping.SourceItemId is null)
				throw new InvalidDataException($"目标护甲 {mapping.TargetItemId} 缺少视觉来源。");
			if (mapping.IsModelMapping && mapping.SourceItemId is not null)
				throw new InvalidDataException($"目标护甲 {mapping.TargetItemId} 同时指定了护甲和模型来源。");
			if (!mapping.IsModelMapping && mapping.TargetItemId == mapping.SourceItemId)
				throw new InvalidDataException($"目标护甲 {mapping.TargetItemId} 不能映射到自身。");
			if (!targets.Add(mapping.TargetItemId))
				throw new InvalidDataException($"目标护甲 {mapping.TargetItemId} 重复。");
		}
	}

	private static IReadOnlyList<RuntimeArmorRegistryMapping> NormalizeMappings(
		IEnumerable<RuntimeArmorRegistryMapping> mappings)
		=> mappings.OrderBy(mapping => mapping.TargetItemId).ToArray();

	private static IReadOnlyList<RuntimeArmorRegistryModel> NormalizeModels(
		IEnumerable<RuntimeArmorRegistryModel> models)
		=> models.OrderBy(model => model.Key, StringComparer.Ordinal).ToArray();

	private static void ValidateModels(
		IEnumerable<RuntimeArmorRegistryModel> models,
		IEnumerable<RuntimeArmorRegistryMapping> mappings)
	{
		var definitions = models.ToArray();
		var keys = new HashSet<string>(StringComparer.Ordinal);
		foreach (var model in definitions)
		{
			_ = ParseModelKey(model.Key, 0);
			if (!keys.Add(model.Key))
				throw new InvalidDataException($"模型键 {model.Key} 重复。");
		}
		foreach (var mapping in mappings.Where(mapping => mapping.IsModelMapping))
		{
			if (!keys.Contains(mapping.ModelKey!))
				throw new InvalidDataException($"目标护甲 {mapping.TargetItemId} 引用了未定义模型 {mapping.ModelKey}。");
		}
	}

	private static int ParseInteger(string value, int line, string field, int minimum, int maximum)
		=> int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result)
			&& result >= minimum && result <= maximum
				? result
				: throw InvalidLine(line, $"{field} 超出支持范围。");

	private static uint ParseItemId(string value, int line, string label)
	{
		value = value.Trim();
		var style = NumberStyles.None;
		if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
		{
			value = value[2..];
			style = NumberStyles.AllowHexSpecifier;
		}
		return uint.TryParse(value, style, CultureInfo.InvariantCulture, out var result)
			? result
			: throw InvalidLine(line, $"{label} Item ID 不是 32 位无符号整数。");
	}

	private static string ParseModelKey(string value, int line)
	{
		var key = value.Trim();
		if (key.Length is < 1 or > 96
			|| key.Any(character => !(char.IsLetterOrDigit(character)
				|| character is '_' or '.' or '-' or '/' or ':')))
			throw InvalidLine(line, "模型键包含不支持的字符。");
		return key;
	}

	private static InvalidDataException InvalidLine(int line, string message)
		=> new($"护甲注册协议第 {line} 行无效：{message}");
}
