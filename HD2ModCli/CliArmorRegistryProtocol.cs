using System.Globalization;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HD2ModCli;

public sealed record CliArmorRegistryMapping(uint TargetItemId, uint SourceItemId);

public sealed record CliArmorRegistrySpecification(
	string Schema,
	bool Enabled,
	IReadOnlyList<CliArmorRegistryMapping> Mappings);

public sealed record CliArmorRegistryDocument(
	int Schema,
	int Revision,
	bool Enabled,
	IReadOnlyList<CliArmorRegistryMapping> Mappings);

public sealed record CliArmorRegistryPlan(
	string Schema,
	DateTimeOffset CreatedUtc,
	string RegistryRoot,
	string InputFingerprint,
	CliArmorRegistryDocument Desired,
	string Integrity);

public sealed record CliArmorRegistryValidation(
	bool IsValid,
	string Status,
	string RegistryRoot,
	string CurrentFingerprint,
	IReadOnlyList<CliArmorRegistryDiagnostic> Diagnostics);

public sealed record CliArmorRegistryDiagnostic(string Code, string Message, bool Blocking);

public sealed record CliArmorRegistryExecution(
	bool Success,
	string Status,
	string RegistryRoot,
	string ProtocolPath,
	int Revision,
	int ActiveMappingCount);

public static class CliArmorRegistryProtocol
{
	public const string SpecificationSchema = "hd2mod.armor-registry-spec";
	public const string PlanSchema = "hd2mod.armor-registry-plan";
	public const string ProtocolFileName = "armor-registry.protocol";
	public const string RuntimeStatusFileName = "runtime.status";
	public const string LocatorCacheFileName = "locator.cache";

	private const int MaximumProtocolBytes = 65536;
	private const int MaximumRevision = int.MaxValue;

	private static readonly JsonSerializerOptions StrictJsonOptions = new(CliOutput.JsonOptions)
	{
		UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
	};

	public static string ResolveRegistryRoot(string? overridePath)
	{
		var path = string.IsNullOrWhiteSpace(overridePath)
			? Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
				"CowboyBingus",
				"Helldivers2",
				"ArmorRegistry")
			: overridePath;
		try
		{
			return Path.GetFullPath(path);
		}
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
		{
			throw new CliUserException("ArmorRegistryRootInvalid", $"护甲注册协议目录无效：{path}");
		}
	}

	public static async ValueTask<CliArmorRegistryDocument> ReadProtocolAsync(
		string registryRoot,
		bool allowMissing,
		CancellationToken cancellationToken = default)
	{
		var path = Path.Combine(registryRoot, ProtocolFileName);
		if (!File.Exists(path))
		{
			if (allowMissing)
				return new CliArmorRegistryDocument(1, 0, false, Array.Empty<CliArmorRegistryMapping>());
			throw new CliUserException("ArmorRegistryProtocolMissing", $"尚未找到护甲注册协议：{path}");
		}

		string text;
		try
		{
			var info = new FileInfo(path);
			if (info.Length > MaximumProtocolBytes)
				throw new CliUserException("ArmorRegistryProtocolTooLarge", $"护甲注册协议超过 {MaximumProtocolBytes} 字节上限：{path}");
			text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
		}
		catch (CliUserException)
		{
			throw;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			throw new CliUserException("ArmorRegistryProtocolReadFailed", $"无法读取护甲注册协议：{exception.Message}");
		}
		return ParseProtocol(text);
	}

	public static CliArmorRegistryDocument ParseProtocol(string text)
	{
		ArgumentNullException.ThrowIfNull(text);
		if (Encoding.UTF8.GetByteCount(text) > MaximumProtocolBytes)
			throw new CliUserException("ArmorRegistryProtocolTooLarge", $"护甲注册协议超过 {MaximumProtocolBytes} 字节上限。");

		int? schema = null;
		int? revision = null;
		bool? enabled = null;
		var mappings = new List<CliArmorRegistryMapping>();
		var targets = new HashSet<uint>();
		var lineNumber = 0;
		foreach (var rawLine in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
		{
			lineNumber++;
			var line = rawLine.Trim();
			if (line.Length == 0 || line.StartsWith('#')) continue;
			var separator = line.IndexOf('=');
			if (separator <= 0)
				throw InvalidProtocol(lineNumber, "缺少 key=value 结构。");
			var key = line[..separator].Trim();
			var value = line[(separator + 1)..].Trim();
			switch (key)
			{
				case "schema":
					if (schema is not null) throw InvalidProtocol(lineNumber, "schema 重复。");
					schema = ParseInteger(value, "schema", lineNumber, 1, 1);
					break;
				case "revision":
					if (revision is not null) throw InvalidProtocol(lineNumber, "revision 重复。");
					revision = ParseInteger(value, "revision", lineNumber, 0, MaximumRevision);
					break;
				case "enabled":
					if (enabled is not null) throw InvalidProtocol(lineNumber, "enabled 重复。");
					enabled = value switch
					{
						"true" => true,
						"false" => false,
						_ => throw InvalidProtocol(lineNumber, "enabled 必须是 true 或 false。"),
					};
					break;
				case "map":
					var parts = value.Split(',');
					if (parts.Length != 2)
						throw InvalidProtocol(lineNumber, "map 必须是 target_item_id,source_item_id。");
					var target = ParseItemId(parts[0], lineNumber, "目标");
					var source = ParseItemId(parts[1], lineNumber, "来源");
					if (target == source)
						throw InvalidProtocol(lineNumber, "目标与来源护甲不能相同。");
					if (!targets.Add(target))
						throw InvalidProtocol(lineNumber, $"目标护甲 {target} 重复。");
					mappings.Add(new CliArmorRegistryMapping(target, source));
					break;
				default:
					throw InvalidProtocol(lineNumber, $"未知字段：{key}");
			}
		}

		if (schema != 1) throw new CliUserException("ArmorRegistryProtocolInvalid", "护甲注册协议必须包含 schema=1。");
		if (revision is null) throw new CliUserException("ArmorRegistryProtocolInvalid", "护甲注册协议缺少 revision。");
		if (enabled is null) throw new CliUserException("ArmorRegistryProtocolInvalid", "护甲注册协议缺少 enabled。");
		return new CliArmorRegistryDocument(1, revision.Value, enabled.Value, NormalizeMappings(mappings));
	}

	public static string SerializeProtocol(CliArmorRegistryDocument document)
	{
		ValidateDocument(document);
		var builder = new StringBuilder();
		builder.AppendLine("schema=1");
		builder.Append("revision=").Append(document.Revision.ToString(CultureInfo.InvariantCulture)).Append('\n');
		builder.Append("enabled=").Append(document.Enabled ? "true" : "false").Append('\n');
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

	public static async ValueTask<CliArmorRegistryPlan> BuildPlanAsync(
		string specificationPath,
		string registryRoot,
		CancellationToken cancellationToken = default)
	{
		var specification = await ReadSpecificationAsync(specificationPath, cancellationToken).ConfigureAwait(false);
		var current = await ReadProtocolAsync(registryRoot, allowMissing: true, cancellationToken).ConfigureAwait(false);
		if (current.Revision == MaximumRevision)
			throw new CliUserException("ArmorRegistryRevisionExhausted", "当前护甲注册协议 revision 已达到上限，无法继续递增。");
		var desired = new CliArmorRegistryDocument(
			1,
			current.Revision + 1,
			specification.Enabled,
			NormalizeMappings(specification.Mappings));
		ValidateDocument(desired);
		var fingerprint = await FingerprintProtocolAsync(registryRoot, cancellationToken).ConfigureAwait(false);
		var unsigned = new CliArmorRegistryPlan(
			PlanSchema,
			DateTimeOffset.UtcNow,
			registryRoot,
			fingerprint,
			desired,
			string.Empty);
		return unsigned with { Integrity = ComputePlanIntegrity(unsigned) };
	}

	public static async ValueTask<CliArmorRegistryPlan> ReadPlanAsync(
		string planPath,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(planPath))
			throw new CliUserException("ArmorRegistryPlanRequired", "请通过 --plan <护甲注册计划.json> 提供计划文件。");
		try
		{
			var json = await File.ReadAllTextAsync(Path.GetFullPath(planPath), cancellationToken).ConfigureAwait(false);
			var plan = JsonSerializer.Deserialize<CliArmorRegistryPlan>(json, StrictJsonOptions)
				?? throw new CliUserException("ArmorRegistryPlanInvalid", "护甲注册计划为空。");
			ValidatePlan(plan);
			return plan;
		}
		catch (CliUserException)
		{
			throw;
		}
		catch (JsonException exception)
		{
			throw new CliUserException("ArmorRegistryPlanInvalid", $"护甲注册计划 JSON 无效：{exception.Message}");
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or PathTooLongException)
		{
			throw new CliUserException("ArmorRegistryPlanReadFailed", $"无法读取护甲注册计划：{exception.Message}");
		}
	}

	public static async ValueTask<CliArmorRegistryValidation> ValidateAsync(
		CliArmorRegistryPlan plan,
		string? registryRootOverride,
		CancellationToken cancellationToken = default)
	{
		ValidatePlan(plan);
		var root = ResolvePlanRoot(plan, registryRootOverride);
		var currentFingerprint = await FingerprintProtocolAsync(root, cancellationToken).ConfigureAwait(false);
		var desiredFingerprint = FingerprintBytes(Encoding.UTF8.GetBytes(SerializeProtocol(plan.Desired)));
		if (string.Equals(currentFingerprint, desiredFingerprint, StringComparison.Ordinal))
		{
			return new CliArmorRegistryValidation(
				true,
				"already-applied",
				root,
				currentFingerprint,
				Array.Empty<CliArmorRegistryDiagnostic>());
		}
		if (!string.Equals(currentFingerprint, plan.InputFingerprint, StringComparison.Ordinal))
		{
			return new CliArmorRegistryValidation(
				false,
				"drifted",
				root,
				currentFingerprint,
				[new CliArmorRegistryDiagnostic(
					"ArmorRegistryProtocolChanged",
					"计划创建后 armor-registry.protocol 已变化；请重新读取并创建计划。",
					true)]);
		}
		return new CliArmorRegistryValidation(
			true,
			"ready",
			root,
			currentFingerprint,
			Array.Empty<CliArmorRegistryDiagnostic>());
	}

	public static async ValueTask<CliArmorRegistryExecution> ApplyAsync(
		CliArmorRegistryPlan plan,
		string? registryRootOverride,
		CancellationToken cancellationToken = default)
	{
		ValidatePlan(plan);
		var root = ResolvePlanRoot(plan, registryRootOverride);
		Directory.CreateDirectory(root);
		var lockPath = Path.Combine(root, ".manager.lock");
		FileStream? lease = null;
		try
		{
			lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			throw new CliUserException("ArmorRegistryWriteBusy", $"护甲注册协议正被另一项管理器操作修改：{exception.Message}");
		}
		await using (lease)
		{
			var validation = await ValidateAsync(plan, root, cancellationToken).ConfigureAwait(false);
			var protocolPath = Path.Combine(root, ProtocolFileName);
			if (validation.Status == "already-applied")
			{
				return new CliArmorRegistryExecution(
					true,
					"already-applied",
					root,
					protocolPath,
					plan.Desired.Revision,
					plan.Desired.Enabled ? plan.Desired.Mappings.Count : 0);
			}
			if (!validation.IsValid)
				throw new CliUserException("ArmorRegistryProtocolChanged", validation.Diagnostics[0].Message);

			var temporaryPath = Path.Combine(root, "." + ProtocolFileName + "." + Guid.NewGuid().ToString("N") + ".tmp");
			try
			{
				await File.WriteAllTextAsync(
					temporaryPath,
					SerializeProtocol(plan.Desired),
					new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
					cancellationToken).ConfigureAwait(false);
				File.Move(temporaryPath, protocolPath, overwrite: true);
			}
			finally
			{
				if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
			}

			return new CliArmorRegistryExecution(
				true,
				"applied",
				root,
				protocolPath,
				plan.Desired.Revision,
				plan.Desired.Enabled ? plan.Desired.Mappings.Count : 0);
		}
	}

	public static async ValueTask<object> ReadStatusAsync(
		string registryRoot,
		CancellationToken cancellationToken = default)
	{
		var protocolPath = Path.Combine(registryRoot, ProtocolFileName);
		var runtimePath = Path.Combine(registryRoot, RuntimeStatusFileName);
		var cachePath = Path.Combine(registryRoot, LocatorCacheFileName);
		var protocol = await ReadProtocolAsync(registryRoot, allowMissing: true, cancellationToken).ConfigureAwait(false);
		var protocolExists = File.Exists(protocolPath);
		var runtime = await ReadKeyValueFileAsync(runtimePath, cancellationToken).ConfigureAwait(false);
		var cache = await ReadKeyValueFileAsync(cachePath, cancellationToken).ConfigureAwait(false);
		var runtimeProcessRunning = IsRuntimeProcessRunning(runtime.Values);
		var runtimeAcknowledged = runtimeProcessRunning
			&& runtime.Values.TryGetValue("state", out var state)
			&& string.Equals(state, "ready", StringComparison.Ordinal)
			&& runtime.Values.TryGetValue("revision", out var runtimeRevision)
			&& int.TryParse(runtimeRevision, NumberStyles.Integer, CultureInfo.InvariantCulture, out var appliedRevision)
			&& appliedRevision == protocol.Revision;
		return new
		{
			registryRoot,
			protocolPath,
			runtimeStatusPath = runtimePath,
			locatorCachePath = cachePath,
			protocolExists,
			protocolFingerprint = await FingerprintProtocolAsync(registryRoot, cancellationToken).ConfigureAwait(false),
			protocol,
			runtimeStatusExists = runtime.Exists,
			runtimeProcessRunning,
			runtimeAcknowledged,
			runtimeStatus = runtime.Values,
			locatorCacheExists = cache.Exists,
			locatorCache = cache.Values,
		};
	}

	private static bool IsRuntimeProcessRunning(IReadOnlyDictionary<string, string> values)
	{
		if (!values.TryGetValue("pid", out var rawPid)
			|| !int.TryParse(rawPid, NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
			|| pid <= 0)
		{
			return false;
		}
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

	private static async ValueTask<CliArmorRegistrySpecification> ReadSpecificationAsync(
		string path,
		CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(path))
			throw new CliUserException("ArmorRegistrySpecificationRequired", "armor-registry plan 需要一个 JSON 规格文件。");
		try
		{
			var json = await File.ReadAllTextAsync(Path.GetFullPath(path), cancellationToken).ConfigureAwait(false);
			var specification = JsonSerializer.Deserialize<CliArmorRegistrySpecification>(json, StrictJsonOptions)
				?? throw new CliUserException("ArmorRegistrySpecificationInvalid", "护甲注册规格为空。");
			if (!string.Equals(specification.Schema, SpecificationSchema, StringComparison.Ordinal))
				throw new CliUserException("ArmorRegistrySpecificationInvalid", $"护甲注册规格 schema 必须是 {SpecificationSchema}。");
			if (specification.Mappings is null)
				throw new CliUserException("ArmorRegistrySpecificationInvalid", "护甲注册规格缺少 mappings。");
			ValidateMappings(specification.Mappings);
			return specification with { Mappings = NormalizeMappings(specification.Mappings) };
		}
		catch (CliUserException)
		{
			throw;
		}
		catch (JsonException exception)
		{
			throw new CliUserException("ArmorRegistrySpecificationInvalid", $"护甲注册规格 JSON 无效：{exception.Message}");
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or PathTooLongException)
		{
			throw new CliUserException("ArmorRegistrySpecificationReadFailed", $"无法读取护甲注册规格：{exception.Message}");
		}
	}

	private static void ValidatePlan(CliArmorRegistryPlan plan)
	{
		if (!string.Equals(plan.Schema, PlanSchema, StringComparison.Ordinal))
			throw new CliUserException("ArmorRegistryPlanInvalid", $"护甲注册计划 schema 必须是 {PlanSchema}。");
		if (string.IsNullOrWhiteSpace(plan.RegistryRoot) || !Path.IsPathFullyQualified(plan.RegistryRoot))
			throw new CliUserException("ArmorRegistryPlanInvalid", "护甲注册计划缺少绝对 registryRoot。");
		if (string.IsNullOrWhiteSpace(plan.InputFingerprint))
			throw new CliUserException("ArmorRegistryPlanInvalid", "护甲注册计划缺少输入指纹。");
		ValidateDocument(plan.Desired);
		var expected = ComputePlanIntegrity(plan with { Integrity = string.Empty });
		if (!CryptographicOperations.FixedTimeEquals(
			Encoding.ASCII.GetBytes(expected),
			Encoding.ASCII.GetBytes(plan.Integrity ?? string.Empty)))
		{
			throw new CliUserException("ArmorRegistryPlanIntegrityMismatch", "护甲注册计划内容已被修改；请重新创建计划。");
		}
	}

	private static void ValidateDocument(CliArmorRegistryDocument document)
	{
		if (document.Schema != 1)
			throw new CliUserException("ArmorRegistryProtocolInvalid", "护甲注册协议 schema 必须是 1。");
		if (document.Revision is < 0 or > MaximumRevision)
			throw new CliUserException("ArmorRegistryProtocolInvalid", "护甲注册协议 revision 超出支持范围。");
		if (document.Mappings is null)
			throw new CliUserException("ArmorRegistryProtocolInvalid", "护甲注册协议缺少 mappings。");
		ValidateMappings(document.Mappings);
	}

	private static void ValidateMappings(IEnumerable<CliArmorRegistryMapping> mappings)
	{
		var targets = new HashSet<uint>();
		foreach (var mapping in mappings)
		{
			if (mapping.TargetItemId == mapping.SourceItemId)
				throw new CliUserException("ArmorRegistryMappingInvalid", $"目标护甲 {mapping.TargetItemId} 不能映射到自身。");
			if (!targets.Add(mapping.TargetItemId))
				throw new CliUserException("ArmorRegistryMappingDuplicateTarget", $"目标护甲 {mapping.TargetItemId} 重复。");
		}
	}

	private static IReadOnlyList<CliArmorRegistryMapping> NormalizeMappings(IEnumerable<CliArmorRegistryMapping> mappings)
		=> mappings.OrderBy(mapping => mapping.TargetItemId).ToArray();

	private static string ResolvePlanRoot(CliArmorRegistryPlan plan, string? overridePath)
	{
		var planRoot = Path.GetFullPath(plan.RegistryRoot);
		if (string.IsNullOrWhiteSpace(overridePath)) return planRoot;
		var requested = ResolveRegistryRoot(overridePath);
		if (!string.Equals(
			Path.TrimEndingDirectorySeparator(planRoot),
			Path.TrimEndingDirectorySeparator(requested),
			StringComparison.OrdinalIgnoreCase))
		{
			throw new CliUserException(
				"ArmorRegistryRootMismatch",
				$"命令指定的协议目录与计划不一致。计划：{planRoot}；命令：{requested}");
		}
		return planRoot;
	}

	private static int ParseInteger(string value, string name, int line, int minimum, int maximum)
		=> int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
			&& parsed >= minimum && parsed <= maximum
				? parsed
				: throw InvalidProtocol(line, $"{name} 超出支持范围。");

	private static uint ParseItemId(string value, int line, string label)
	{
		value = value.Trim();
		var style = NumberStyles.None;
		if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
		{
			value = value[2..];
			style = NumberStyles.AllowHexSpecifier;
		}
		return uint.TryParse(value, style, CultureInfo.InvariantCulture, out var parsed)
			? parsed
			: throw InvalidProtocol(line, $"{label} Item ID 不是 32 位无符号整数。");
	}

	private static CliUserException InvalidProtocol(int line, string message)
		=> new("ArmorRegistryProtocolInvalid", $"护甲注册协议第 {line} 行无效：{message}");

	private static async ValueTask<string> FingerprintProtocolAsync(
		string registryRoot,
		CancellationToken cancellationToken)
	{
		var path = Path.Combine(registryRoot, ProtocolFileName);
		if (!File.Exists(path)) return "missing";
		try
		{
			var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
			return FingerprintBytes(bytes);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			throw new CliUserException("ArmorRegistryProtocolReadFailed", $"无法读取护甲注册协议指纹：{exception.Message}");
		}
	}

	private static string FingerprintBytes(byte[] bytes)
		=> Convert.ToHexStringLower(SHA256.HashData(bytes));

	private static string ComputePlanIntegrity(CliArmorRegistryPlan plan)
	{
		var canonical = string.Join(
			"\n",
			plan.Schema,
			plan.CreatedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
			Path.GetFullPath(plan.RegistryRoot),
			plan.InputFingerprint,
			SerializeProtocol(plan.Desired));
		return FingerprintBytes(Encoding.UTF8.GetBytes(canonical));
	}

	private static async ValueTask<(bool Exists, IReadOnlyDictionary<string, string> Values)> ReadKeyValueFileAsync(
		string path,
		CancellationToken cancellationToken)
	{
		if (!File.Exists(path))
			return (false, new Dictionary<string, string>(StringComparer.Ordinal));
		try
		{
			var values = new Dictionary<string, string>(StringComparer.Ordinal);
			foreach (var rawLine in await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false))
			{
				var separator = rawLine.IndexOf('=');
				if (separator <= 0) continue;
				values[rawLine[..separator].Trim()] = rawLine[(separator + 1)..].Trim();
			}
			return (true, values);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			throw new CliUserException("ArmorRegistryStatusReadFailed", $"无法读取运行时状态文件 {path}：{exception.Message}");
		}
	}
}
