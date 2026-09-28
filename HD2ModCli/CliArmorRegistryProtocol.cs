using System.Globalization;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HD2ModCli;

public sealed record CliArmorRegistryModel(
	string Key,
	uint? SourceItemId = null,
	IReadOnlyList<CliArmorRegistryModelPiece>? Pieces = null);

public sealed record CliArmorRegistryModelPiece(
	uint Body,
	string Unit,
	uint Slot,
	uint PieceType,
	byte Tone = 0,
	bool Enabled = true,
	bool IsDefaultBase = false);

public sealed record CliArmorRegistryPieceOverride(
	uint OwnerItemId,
	uint Body,
	string Unit,
	uint Slot,
	uint PieceType,
	bool Enabled = true);

public sealed record CliArmorRegistryTargetMode(uint TargetItemId, bool FollowSource = true);

public sealed record CliArmorRegistryMapping(
	uint TargetItemId,
	uint? SourceItemId = null,
	string? ModelKey = null)
{
	public bool IsModelMapping => !string.IsNullOrWhiteSpace(ModelKey);
}

public sealed record CliArmorRegistrySpecification(
	string Schema,
	bool Enabled,
	IReadOnlyList<CliArmorRegistryMapping> Mappings,
	IReadOnlyList<CliArmorRegistryModel>? Models = null,
	IReadOnlyList<CliArmorRegistryPieceOverride>? PieceOverrides = null,
	IReadOnlyList<CliArmorRegistryTargetMode>? TargetModes = null);

public sealed record CliArmorRegistryDocument(
	int Schema,
	int Revision,
	bool Enabled,
	IReadOnlyList<CliArmorRegistryMapping> Mappings,
	IReadOnlyList<CliArmorRegistryModel>? Models = null,
	IReadOnlyList<CliArmorRegistryPieceOverride>? PieceOverrides = null,
	IReadOnlyList<CliArmorRegistryTargetMode>? TargetModes = null,
	string CompositionMode = "full");

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
		var compositionMode = "full";
		var mappings = new List<CliArmorRegistryMapping>();
		var models = new List<CliArmorRegistryModel>();
		var pieceOverrides = new List<CliArmorRegistryPieceOverride>();
		var targetModes = new List<CliArmorRegistryTargetMode>();
		var modelKeys = new HashSet<string>(StringComparer.Ordinal);
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
					schema = ParseInteger(value, "schema", lineNumber, 1, 2);
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
				case "composition_mode":
					if (value is not ("full" or "existing_only"))
						throw InvalidProtocol(lineNumber, "composition_mode 必须是 full 或 existing_only。");
					compositionMode = value;
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
				case "model":
					var modelParts = value.Split(',');
					if (modelParts.Length != 2)
						throw InvalidProtocol(lineNumber, "model 必须是 key,source_item_id。");
					var modelKey = ParseModelKey(modelParts[0], lineNumber);
					var modelSource = string.IsNullOrWhiteSpace(modelParts[1])
						? (uint?)null
						: ParseItemId(modelParts[1], lineNumber, "模型来源");
					if (!modelKeys.Add(modelKey))
						throw InvalidProtocol(lineNumber, $"模型键 {modelKey} 重复。");
					models.Add(new CliArmorRegistryModel(modelKey, modelSource, Array.Empty<CliArmorRegistryModelPiece>()));
					break;
				case "model_piece":
					var pieceParts = value.Split(',');
					if (pieceParts.Length is not (6 or 7 or 8))
						throw InvalidProtocol(lineNumber, "model_piece 必须是 key,body,unit,slot,piece_type,tone[,enabled[,default_base]]。");
					var pieceKey = ParseModelKey(pieceParts[0], lineNumber);
					var modelIndex = models.FindIndex(model => string.Equals(model.Key, pieceKey, StringComparison.Ordinal));
					if (modelIndex < 0)
						throw InvalidProtocol(lineNumber, $"model_piece 引用了未定义模型 {pieceKey}。");
					var piece = new CliArmorRegistryModelPiece(
						ParseUInt32(pieceParts[1], lineNumber, "model body"),
						ParseUnitId(pieceParts[2], lineNumber),
						ParseModelSlot(pieceParts[3], lineNumber, ParseUInt32(pieceParts[1], lineNumber, "model body")),
						ParseUInt32(pieceParts[4], lineNumber, "model piece type"),
						(byte)ParseInteger(pieceParts[5], "model tone", lineNumber, 0, 255),
						pieceParts.Length == 6 || ParseBoolean(pieceParts[6], lineNumber, "model piece enabled"),
						pieceParts.Length == 8 && ParseBoolean(pieceParts[7], lineNumber, "model piece default_base"));
					var existingPieces = models[modelIndex].Pieces ?? Array.Empty<CliArmorRegistryModelPiece>();
					models[modelIndex] = models[modelIndex] with { Pieces = existingPieces.Append(piece).ToArray() };
					break;
				case "map_model":
					var modelMappingParts = value.Split(',');
					if (modelMappingParts.Length != 2)
						throw InvalidProtocol(lineNumber, "map_model 必须是 target_item_id,model_key。");
					var modelTarget = ParseItemId(modelMappingParts[0], lineNumber, "目标");
					var targetModel = ParseModelKey(modelMappingParts[1], lineNumber);
					if (!targets.Add(modelTarget))
						throw InvalidProtocol(lineNumber, $"目标护甲 {modelTarget} 重复。");
					mappings.Add(new CliArmorRegistryMapping(modelTarget, null, targetModel));
					break;
				case "piece_override":
					var overrideParts = value.Split(',');
					if (overrideParts.Length != 6)
						throw InvalidProtocol(lineNumber, "piece_override 必须是 item_id,body,unit,slot,piece_type,enabled。");
					var owner = ParseItemId(overrideParts[0], lineNumber, "组件覆盖对象");
					var overrideBody = ParseUInt32(overrideParts[1], lineNumber, "override body");
					var overrideUnit = ParseUnitId(overrideParts[2], lineNumber);
					var overrideSlot = ParseOverrideSlot(overrideParts[3], lineNumber, overrideBody);
					var overridePieceType = ParseUInt32(overrideParts[4], lineNumber, "override piece type");
					var overrideEnabled = ParseBoolean(overrideParts[5], lineNumber, "piece_override enabled");
					pieceOverrides.Add(new CliArmorRegistryPieceOverride(
						owner, overrideBody, overrideUnit, overrideSlot, overridePieceType, overrideEnabled));
					break;
				case "target_mode":
					var modeParts = value.Split(',');
					if (modeParts.Length != 2)
						throw InvalidProtocol(lineNumber, "target_mode 必须是 target_item_id,follow|manual。");
					var modeTarget = ParseItemId(modeParts[0], lineNumber, "目标");
					var follow = modeParts[1].Trim() switch
					{
						"follow" => true,
						"manual" => false,
						_ => throw InvalidProtocol(lineNumber, "target_mode 必须是 follow 或 manual。"),
					};
					if (targetModes.Any(mode => mode.TargetItemId == modeTarget))
						throw InvalidProtocol(lineNumber, $"目标护甲 {modeTarget} 的 target_mode 重复。");
					targetModes.Add(new CliArmorRegistryTargetMode(modeTarget, follow));
					break;
				default:
					throw InvalidProtocol(lineNumber, $"未知字段：{key}");
			}
		}

		if (schema is not (1 or 2)) throw new CliUserException("ArmorRegistryProtocolInvalid", "护甲注册协议必须包含 schema=1 或 schema=2。");
		if (revision is null) throw new CliUserException("ArmorRegistryProtocolInvalid", "护甲注册协议缺少 revision。");
		if (enabled is null) throw new CliUserException("ArmorRegistryProtocolInvalid", "护甲注册协议缺少 enabled。");
		return new CliArmorRegistryDocument(schema.Value, revision.Value, enabled.Value,
			NormalizeMappings(mappings), models.OrderBy(model => model.Key, StringComparer.Ordinal).ToArray(),
			pieceOverrides, targetModes, compositionMode);
	}

	public static string SerializeProtocol(CliArmorRegistryDocument document)
	{
		ValidateDocument(document);
		var builder = new StringBuilder();
		builder.AppendLine("schema=2");
		builder.Append("revision=").Append(document.Revision.ToString(CultureInfo.InvariantCulture)).Append('\n');
		builder.Append("enabled=").Append(document.Enabled ? "true" : "false").Append('\n');
		if (!string.Equals(document.CompositionMode, "full", StringComparison.Ordinal))
			builder.Append("composition_mode=existing_only\n");
		foreach (var model in (document.Models ?? Array.Empty<CliArmorRegistryModel>())
			.OrderBy(model => model.Key, StringComparer.Ordinal))
		{
			builder.Append("model=").Append(model.Key).Append(',')
				.Append(model.SourceItemId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append('\n');
			foreach (var piece in model.Pieces ?? Array.Empty<CliArmorRegistryModelPiece>())
			{
				builder.Append("model_piece=").Append(model.Key).Append(',')
					.Append(piece.Body.ToString(CultureInfo.InvariantCulture)).Append(',')
					.Append(piece.Unit).Append(',')
					.Append(piece.Slot.ToString(CultureInfo.InvariantCulture)).Append(',')
					.Append(piece.PieceType.ToString(CultureInfo.InvariantCulture)).Append(',')
					.Append(piece.Tone.ToString(CultureInfo.InvariantCulture)).Append(',')
					.Append(piece.Enabled ? "true" : "false");
				// Keep the wire format compatible with runtimes accepting 6/7 fields.
				builder.Append('\n');
			}
		}
		foreach (var mapping in NormalizeMappings(document.Mappings))
		{
			builder.Append(mapping.IsModelMapping ? "map_model=" : "map=")
				.Append(mapping.TargetItemId.ToString(CultureInfo.InvariantCulture))
				.Append(',')
				.Append(mapping.IsModelMapping ? mapping.ModelKey : mapping.SourceItemId.GetValueOrDefault().ToString(CultureInfo.InvariantCulture))
				.Append('\n');
		}
		foreach (var item in (document.PieceOverrides ?? Array.Empty<CliArmorRegistryPieceOverride>())
			.OrderBy(item => item.OwnerItemId).ThenBy(item => item.Body)
			.ThenBy(item => item.Slot).ThenBy(item => item.PieceType))
		{
			builder.Append("piece_override=").Append(item.OwnerItemId.ToString(CultureInfo.InvariantCulture)).Append(',')
				.Append(item.Body.ToString(CultureInfo.InvariantCulture)).Append(',')
				.Append(item.Unit).Append(',')
				.Append(item.Slot.ToString(CultureInfo.InvariantCulture)).Append(',')
				.Append(item.PieceType.ToString(CultureInfo.InvariantCulture)).Append(',')
				.Append(item.Enabled ? "true" : "false").Append('\n');
		}
		foreach (var mode in (document.TargetModes ?? Array.Empty<CliArmorRegistryTargetMode>())
			.OrderBy(mode => mode.TargetItemId))
		{
			builder.Append("target_mode=").Append(mode.TargetItemId.ToString(CultureInfo.InvariantCulture)).Append(',')
				.Append(mode.FollowSource ? "follow" : "manual").Append('\n');
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
			2,
			current.Revision + 1,
			specification.Enabled,
			NormalizeMappings(specification.Mappings),
			NormalizeModels(specification.Models ?? Array.Empty<CliArmorRegistryModel>()),
			specification.PieceOverrides ?? Array.Empty<CliArmorRegistryPieceOverride>(),
			specification.TargetModes ?? Array.Empty<CliArmorRegistryTargetMode>(),
			"full");
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
			ValidateModels(specification.Models ?? Array.Empty<CliArmorRegistryModel>(), specification.Mappings);
			return specification with
			{
				Mappings = NormalizeMappings(specification.Mappings),
				Models = NormalizeModels(specification.Models ?? Array.Empty<CliArmorRegistryModel>())
			};
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
		if (document.Schema is not (1 or 2))
			throw new CliUserException("ArmorRegistryProtocolInvalid", "护甲注册协议 schema 必须是 1 或 2。");
		if (document.Revision is < 0 or > MaximumRevision)
			throw new CliUserException("ArmorRegistryProtocolInvalid", "护甲注册协议 revision 超出支持范围。");
		if (document.Mappings is null)
			throw new CliUserException("ArmorRegistryProtocolInvalid", "护甲注册协议缺少 mappings。");
		ValidateMappings(document.Mappings);
		ValidateModels(document.Models ?? Array.Empty<CliArmorRegistryModel>(), document.Mappings);
	}

	private static void ValidateMappings(IEnumerable<CliArmorRegistryMapping> mappings)
	{
		var targets = new HashSet<uint>();
		foreach (var mapping in mappings)
		{
			if (!mapping.IsModelMapping && mapping.SourceItemId is null)
				throw new CliUserException("ArmorRegistryMappingInvalid", $"目标护甲 {mapping.TargetItemId} 缺少视觉来源。");
			if (mapping.IsModelMapping && mapping.SourceItemId is not null)
				throw new CliUserException("ArmorRegistryMappingInvalid", $"目标护甲 {mapping.TargetItemId} 同时指定了护甲和模型来源。");
			if (!mapping.IsModelMapping && mapping.TargetItemId == mapping.SourceItemId)
				throw new CliUserException("ArmorRegistryMappingInvalid", $"目标护甲 {mapping.TargetItemId} 不能映射到自身。");
			if (!targets.Add(mapping.TargetItemId))
				throw new CliUserException("ArmorRegistryMappingDuplicateTarget", $"目标护甲 {mapping.TargetItemId} 重复。");
		}
	}

	private static IReadOnlyList<CliArmorRegistryMapping> NormalizeMappings(IEnumerable<CliArmorRegistryMapping> mappings)
		=> mappings.OrderBy(mapping => mapping.TargetItemId).ToArray();

	private static IReadOnlyList<CliArmorRegistryModel> NormalizeModels(IEnumerable<CliArmorRegistryModel> models)
		=> models.OrderBy(model => model.Key, StringComparer.Ordinal).ToArray();

	private static void ValidateModels(
		IEnumerable<CliArmorRegistryModel> models,
		IEnumerable<CliArmorRegistryMapping> mappings)
	{
		var keys = new HashSet<string>(StringComparer.Ordinal);
		foreach (var model in models)
		{
			_ = ParseModelKey(model.Key, 0);
			if (!keys.Add(model.Key))
				throw new CliUserException("ArmorRegistryModelDuplicate", $"模型键 {model.Key} 重复。");
			var pieceKeys = new HashSet<(uint Body, uint Slot, uint PieceType)>();
			foreach (var piece in model.Pieces ?? Array.Empty<CliArmorRegistryModelPiece>())
			{
				if (piece.Slot == 0 && piece.Body != 3)
					throw new CliUserException(
						"ArmorRegistryModelSlotInvalid",
						$"模型 {model.Key} 的 slot=0 只能用于 body=3 的独立头盔 Piece。");
				if (!pieceKeys.Add((piece.Body, piece.Slot, piece.PieceType)))
					throw new CliUserException(
						"ArmorRegistryModelPieceDuplicate",
						$"模型 {model.Key} 重复使用 body={piece.Body}, slot={piece.Slot}, piece_type={piece.PieceType}。");
			}
		}
		foreach (var mapping in mappings.Where(mapping => mapping.IsModelMapping))
		{
			if (!keys.Contains(mapping.ModelKey!))
				throw new CliUserException("ArmorRegistryModelMissing", $"目标护甲 {mapping.TargetItemId} 引用了未定义模型 {mapping.ModelKey}。");
		}
	}

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

	private static uint ParseUInt32(string value, int line, string label)
		=> uint.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
			? parsed
			: throw InvalidProtocol(line, $"{label} 不是 32 位无符号整数。");

	private static uint ParseModelSlot(string value, int line, uint body)
	{
		var slot = ParseUInt32(value, line, "model slot");
		if (body == 3 && slot == 1) slot = 0;
		if (slot == 0 && body != 3)
			throw InvalidProtocol(line, "model slot=0 只能用于 body=3 的独立头盔 Piece。");
		return slot;
	}

	private static uint ParseOverrideSlot(string value, int line, uint body)
	{
		var slot = ParseUInt32(value, line, "override slot");
		if (slot == 0 && body != 3)
			throw InvalidProtocol(line, "piece_override slot=0 只能用于 body=3 的独立头盔 Piece。");
		return slot;
	}

	private static bool ParseBoolean(string value, int line, string label)
		=> value.Trim() switch
		{
			"true" => true,
			"false" => false,
			_ => throw InvalidProtocol(line, $"{label} 必须是 true 或 false。"),
		};

	private static string ParseUnitId(string value, int line)
	{
		var unit = value.Trim();
		if (unit.Length != 16 || !unit.All(Uri.IsHexDigit))
			throw InvalidProtocol(line, "model unit 必须是 16 位十六进制 ID。");
		return unit.ToLowerInvariant();
	}

	private static string ParseModelKey(string value, int line)
	{
		var key = value.Trim();
		if (key.Length is < 1 or > 96
			|| key.Any(character => !(char.IsLetterOrDigit(character)
				|| character is '_' or '.' or '-' or '/' or ':')))
			throw InvalidProtocol(line, "模型键包含不支持的字符。");
		return key;
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
