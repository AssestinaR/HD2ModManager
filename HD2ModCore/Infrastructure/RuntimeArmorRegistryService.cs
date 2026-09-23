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
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(expectedFingerprint);
		ArgumentNullException.ThrowIfNull(mappings);
		ValidateMappings(mappings);
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
			NormalizeMappings(mappings));
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
					schema = ParseInteger(value, lineNumber, "schema", 1, 1);
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
					mappings.Add(new RuntimeArmorRegistryMapping(target, source));
					break;
				default:
					throw InvalidLine(lineNumber, $"未知字段：{key}");
			}
		}
		if (schema != 1) throw new InvalidDataException("护甲注册协议必须包含 schema=1。");
		if (revision is null) throw new InvalidDataException("护甲注册协议缺少 revision。");
		if (enabled is null) throw new InvalidDataException("护甲注册协议缺少 enabled。");
		return new RuntimeArmorRegistryDocument(revision.Value, enabled.Value, NormalizeMappings(mappings));
	}

	public static string SerializeProtocol(RuntimeArmorRegistryDocument document)
	{
		ArgumentNullException.ThrowIfNull(document);
		if (document.Revision < 0) throw new InvalidDataException("护甲注册协议 revision 无效。");
		ValidateMappings(document.Mappings);
		var builder = new StringBuilder()
			.Append("schema=1\n")
			.Append("revision=").Append(document.Revision.ToString(CultureInfo.InvariantCulture)).Append('\n')
			.Append("enabled=").Append(document.Enabled ? "true" : "false").Append('\n');
		foreach (var mapping in NormalizeMappings(document.Mappings))
		{
			builder.Append("map=")
				.Append(mapping.TargetItemId.ToString(CultureInfo.InvariantCulture))
				.Append(',')
				.Append(mapping.SourceItemId.ToString(CultureInfo.InvariantCulture))
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
			if (mapping.TargetItemId == mapping.SourceItemId)
				throw new InvalidDataException($"目标护甲 {mapping.TargetItemId} 不能映射到自身。");
			if (!targets.Add(mapping.TargetItemId))
				throw new InvalidDataException($"目标护甲 {mapping.TargetItemId} 重复。");
		}
	}

	private static IReadOnlyList<RuntimeArmorRegistryMapping> NormalizeMappings(
		IEnumerable<RuntimeArmorRegistryMapping> mappings)
		=> mappings.OrderBy(mapping => mapping.TargetItemId).ToArray();

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

	private static InvalidDataException InvalidLine(int line, string message)
		=> new($"护甲注册协议第 {line} 行无效：{message}");
}
