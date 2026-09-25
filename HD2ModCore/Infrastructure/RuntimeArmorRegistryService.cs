using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HD2ModCore.Domain;
using Microsoft.Data.Sqlite;

namespace HD2ModCore.Infrastructure;

public sealed class RuntimeArmorRegistryService
{
	public const string ProtocolFileName = "armor-registry.protocol";
	public const string RuntimeStatusFileName = "runtime.status";
	public const string LocatorCacheFileName = "locator.cache";
	public const string DeploymentStateFileName = "runtime-armor-registry.deployment";
	public const string WorkspaceFileName = "armor-registry.workspace.json";

	private const int MaximumProtocolBytes = 65536;
	private readonly StoragePaths paths;
	private readonly string registryRoot;

	public RuntimeArmorRegistryService(StoragePaths paths, string? registryRoot = null)
	{
		this.paths = paths ?? throw new ArgumentNullException(nameof(paths));
		this.registryRoot = ResolveRegistryRoot(registryRoot);
	}

	public string RegistryRoot => registryRoot;
	public string WorkspaceRoot => Path.Combine(paths.ModsDirectory, ".hd2modmanager");
	public string WorkspacePath => Path.Combine(WorkspaceRoot, WorkspaceFileName);
	public string DeploymentStatePath => Path.Combine(paths.DataDirectory, DeploymentStateFileName);

	public async ValueTask SaveWorkspaceAsync(
		RuntimeArmorRegistryDocument document,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(document);
		ValidateMappings(document.Mappings);
		ValidateModels(document.ModelDefinitions, document.Mappings);
		ValidatePieceOverrides(document.PieceOverrideDefinitions);
		ValidateTargetModes(document.TargetModeDefinitions);
		await WriteWorkspaceAsync(document, cancellationToken).ConfigureAwait(false);
	}

	public async ValueTask<bool> IsManagedDeploymentEnabledAsync(
		CancellationToken cancellationToken = default)
	{
		if (!File.Exists(DeploymentStatePath)) return false;
		var values = await ReadKeyValueFileAsync(DeploymentStatePath, cancellationToken).ConfigureAwait(false);
		return values.Values.TryGetValue("enabled", out var enabled)
			&& string.Equals(enabled, "true", StringComparison.Ordinal);
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

	public async ValueTask<IReadOnlyList<RuntimeArmorRegistryModelPiece>> ReadArmorPiecesAsync(
		uint itemId,
		CancellationToken cancellationToken = default)
	{
		if (!File.Exists(paths.DbPath)) return Array.Empty<RuntimeArmorRegistryModelPiece>();
		await using var connection = new SqliteConnection($"Data Source={paths.DbPath};Mode=ReadOnly;Cache=Shared");
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using var command = connection.CreateCommand();
		command.CommandText = """
			SELECT body_variant, unit_file_id, slot, piece_type, tone_variations
			FROM game_data_authority_pieces
			WHERE item_id = $item
			ORDER BY slot, body_variant, piece_type, unit_file_id;
			""";
		command.Parameters.AddWithValue("$item", (long)itemId);
		var pieces = new List<RuntimeArmorRegistryModelPiece>();
		await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			var rawUnit = reader.GetInt64(1);
			if (rawUnit < 0) continue;
			var body = (uint)reader.GetInt64(0);
			pieces.Add(new RuntimeArmorRegistryModelPiece(
				body,
				((ulong)rawUnit).ToString("x16", CultureInfo.InvariantCulture),
				(uint)reader.GetInt64(2),
				(uint)reader.GetInt64(3),
				(byte)Math.Clamp(reader.GetInt64(4), 0, byte.MaxValue)));
		}
		return pieces;
	}

	public async ValueTask<RuntimeArmorRegistrySnapshot> ReadSnapshotAsync(
		CancellationToken cancellationToken = default)
	{
		var protocolPath = Path.Combine(registryRoot, ProtocolFileName);
		var runtimePath = Path.Combine(registryRoot, RuntimeStatusFileName);
		var cachePath = Path.Combine(registryRoot, LocatorCacheFileName);
		var deploymentPath = DeploymentStatePath;
		var protocolExists = File.Exists(protocolPath);
		var deployment = await ReadKeyValueFileAsync(deploymentPath, cancellationToken).ConfigureAwait(false);
		var workspaceBelongsToCurrentLibrary = !deployment.Exists
			|| !deployment.Values.TryGetValue("workspace", out var deployedWorkspace)
			|| string.Equals(
				Path.GetFullPath(deployedWorkspace),
				Path.GetFullPath(WorkspacePath),
				StringComparison.OrdinalIgnoreCase);
		var document = File.Exists(WorkspacePath)
			? await ReadWorkspaceAsync(cancellationToken).ConfigureAwait(false)
			: protocolExists && workspaceBelongsToCurrentLibrary
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
		=> await ApplyAsync(expectedFingerprint, enabled, mappings, models,
			Array.Empty<RuntimeArmorRegistryPieceOverride>(),
			Array.Empty<RuntimeArmorRegistryTargetMode>(),
			cancellationToken).ConfigureAwait(false);

	public async ValueTask<RuntimeArmorRegistryApplyResult> ApplyAsync(
		string expectedFingerprint,
		bool enabled,
		IReadOnlyList<RuntimeArmorRegistryMapping> mappings,
		IReadOnlyList<RuntimeArmorRegistryModel> models,
		IReadOnlyList<RuntimeArmorRegistryPieceOverride> pieceOverrides,
		IReadOnlyList<RuntimeArmorRegistryTargetMode> targetModes,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(expectedFingerprint);
		ArgumentNullException.ThrowIfNull(mappings);
		ArgumentNullException.ThrowIfNull(models);
		ArgumentNullException.ThrowIfNull(pieceOverrides);
		ArgumentNullException.ThrowIfNull(targetModes);
		ValidateMappings(mappings);
		ValidateModels(models, mappings);
		ValidatePieceOverrides(pieceOverrides);
		ValidateTargetModes(targetModes);
		Directory.CreateDirectory(registryRoot);
		var lockPath = Path.Combine(registryRoot, ".manager.lock");
		await using var lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
		var currentFingerprint = await FingerprintProtocolAsync(cancellationToken).ConfigureAwait(false);
		if (!string.Equals(currentFingerprint, expectedFingerprint, StringComparison.Ordinal))
			throw new InvalidOperationException("护甲注册协议已被其他操作修改，请刷新后重试。");
		var current = File.Exists(WorkspacePath)
			? await ReadWorkspaceAsync(cancellationToken).ConfigureAwait(false)
			: File.Exists(Path.Combine(registryRoot, ProtocolFileName))
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
			NormalizeModels(models),
			NormalizePieceOverrides(pieceOverrides),
			NormalizeTargetModes(targetModes));
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
		await WriteWorkspaceAsync(desired, cancellationToken).ConfigureAwait(false);
		Directory.CreateDirectory(paths.DataDirectory);
		await File.WriteAllTextAsync(
			DeploymentStatePath,
			(enabled ? "enabled=true\n" : "enabled=false\n")
				+ "workspace=" + WorkspacePath + "\n",
			new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
			cancellationToken).ConfigureAwait(false);
		var snapshot = await ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
		return new RuntimeArmorRegistryApplyResult(
			snapshot.Document,
			snapshot.Fingerprint,
			snapshot.RuntimeProcessRunning,
			snapshot.RuntimeAcknowledged);
	}

	private static readonly JsonSerializerOptions WorkspaceJsonOptions = new(JsonSerializerDefaults.Web)
	{
		WriteIndented = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
	};

	private async ValueTask<RuntimeArmorRegistryDocument> ReadWorkspaceAsync(
		CancellationToken cancellationToken)
	{
		var info = new FileInfo(WorkspacePath);
		if (info.Length > MaximumProtocolBytes * 4)
			throw new InvalidDataException("护甲注册源文件超过 256 KiB 上限。");
		var json = await File.ReadAllTextAsync(WorkspacePath, cancellationToken).ConfigureAwait(false);
		WorkspaceDocument? workspace;
		try
		{
			workspace = JsonSerializer.Deserialize<WorkspaceDocument>(json, WorkspaceJsonOptions);
		}
		catch (JsonException exception)
		{
			throw new InvalidDataException($"护甲注册源文件 JSON 无效：{exception.Message}", exception);
		}
		if (workspace is null || workspace.Schema != 1)
			throw new InvalidDataException("护甲注册源文件 schema 必须是 1。");
		var document = new RuntimeArmorRegistryDocument(
			workspace.Revision,
			workspace.Enabled,
			workspace.Mappings ?? Array.Empty<RuntimeArmorRegistryMapping>(),
			workspace.Models ?? Array.Empty<RuntimeArmorRegistryModel>(),
			workspace.PieceOverrides ?? Array.Empty<RuntimeArmorRegistryPieceOverride>(),
			workspace.TargetModes ?? Array.Empty<RuntimeArmorRegistryTargetMode>());
		ValidateMappings(document.Mappings);
		ValidateModels(document.ModelDefinitions, document.Mappings);
		ValidatePieceOverrides(document.PieceOverrideDefinitions);
		ValidateTargetModes(document.TargetModeDefinitions);
		return document;
	}

	private async ValueTask WriteWorkspaceAsync(
		RuntimeArmorRegistryDocument document,
		CancellationToken cancellationToken)
	{
		Directory.CreateDirectory(WorkspaceRoot);
		var workspace = new WorkspaceDocument(
			1,
			document.Revision,
			document.Enabled,
			document.Mappings,
			document.ModelDefinitions,
			document.PieceOverrideDefinitions,
			document.TargetModeDefinitions);
		var json = JsonSerializer.Serialize(workspace, WorkspaceJsonOptions);
		var temporaryPath = WorkspacePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			await File.WriteAllTextAsync(
				temporaryPath,
				json,
				new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
				cancellationToken).ConfigureAwait(false);
			File.Move(temporaryPath, WorkspacePath, overwrite: true);
		}
		finally
		{
			if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
		}
	}

	private sealed record WorkspaceDocument(
		int Schema,
		int Revision,
		bool Enabled,
		IReadOnlyList<RuntimeArmorRegistryMapping>? Mappings,
		IReadOnlyList<RuntimeArmorRegistryModel>? Models,
		IReadOnlyList<RuntimeArmorRegistryPieceOverride>? PieceOverrides,
		IReadOnlyList<RuntimeArmorRegistryTargetMode>? TargetModes);

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
		var modelPieces = new Dictionary<string, List<RuntimeArmorRegistryModelPiece>>(StringComparer.Ordinal);
		var modelPieceKeys = new Dictionary<string, HashSet<(uint Body, uint Slot, uint PieceType)>>(StringComparer.Ordinal);
		var pieceOverrides = new List<RuntimeArmorRegistryPieceOverride>();
		var targetModes = new List<RuntimeArmorRegistryTargetMode>();
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
					models.Add(new RuntimeArmorRegistryModel(modelKey, modelSource, Array.Empty<RuntimeArmorRegistryModelPiece>()));
					modelPieces[modelKey] = [];
					modelPieceKeys[modelKey] = [];
					break;
				case "model_piece":
					var pieceParts = value.Split(',');
					if (pieceParts.Length is not (6 or 7 or 8))
						throw InvalidLine(lineNumber, "model_piece 必须是 key,body,unit,slot,piece_type,tone[,enabled[,default_base]]。");
					var pieceModelKey = ParseModelKey(pieceParts[0], lineNumber);
					if (!modelKeys.Contains(pieceModelKey))
						throw InvalidLine(lineNumber, $"model_piece 引用了未定义模型 {pieceModelKey}。");
					var body = (uint)ParseInteger(pieceParts[1], lineNumber, "model body", 0, int.MaxValue);
					var unit = pieceParts[2].Trim();
					if (unit.Length != 16 || !unit.All(Uri.IsHexDigit))
						throw InvalidLine(lineNumber, "model unit 必须是 16 位十六进制 ID。");
					var slot = (uint)ParseInteger(pieceParts[3], lineNumber, "model slot", 1, int.MaxValue);
					var pieceType = (uint)ParseInteger(pieceParts[4], lineNumber, "model piece type", 0, int.MaxValue);
					var tone = (byte)ParseInteger(pieceParts[5], lineNumber, "model tone", 0, 255);
					var pieceEnabled = pieceParts.Length == 6 || ParseBoolean(pieceParts[6], lineNumber, "model piece enabled");
					var isDefaultBase = pieceParts.Length == 8 && ParseBoolean(pieceParts[7], lineNumber, "model piece default_base");
					if (!modelPieceKeys[pieceModelKey].Add((body, slot, pieceType)))
						throw InvalidLine(lineNumber, $"模型 {pieceModelKey} 重复使用 body={body}, slot={slot}, piece_type={pieceType}。");
					modelPieces[pieceModelKey].Add(new RuntimeArmorRegistryModelPiece(
						body, unit.ToLowerInvariant(), slot, pieceType, tone, pieceEnabled, isDefaultBase));
					break;
				case "map_model":
					var modelMappingParts = value.Split(',');
					if (modelMappingParts.Length != 2) throw InvalidLine(lineNumber, "map_model 必须是 target_item_id,model_key。");
					var modelTarget = ParseItemId(modelMappingParts[0], lineNumber, "目标");
					var targetModelKey = ParseModelKey(modelMappingParts[1], lineNumber);
					if (!targets.Add(modelTarget)) throw InvalidLine(lineNumber, $"目标护甲 {modelTarget} 重复。");
					mappings.Add(new RuntimeArmorRegistryMapping(modelTarget, null, targetModelKey));
					break;
				case "piece_override":
					var overrideParts = value.Split(',');
					if (overrideParts.Length != 6)
						throw InvalidLine(lineNumber, "piece_override 必须是 item_id,body,unit,slot,piece_type,enabled。");
					var owner = ParseItemId(overrideParts[0], lineNumber, "组件覆盖对象");
					var overrideBody = (uint)ParseInteger(overrideParts[1], lineNumber, "override body", 0, int.MaxValue);
					var overrideUnit = overrideParts[2].Trim();
					if (overrideUnit.Length != 16 || !overrideUnit.All(Uri.IsHexDigit))
						throw InvalidLine(lineNumber, "piece_override unit 必须是 16 位十六进制 ID。");
					var overrideSlot = (uint)ParseInteger(overrideParts[3], lineNumber, "override slot", 1, int.MaxValue);
					var overridePieceType = (uint)ParseInteger(overrideParts[4], lineNumber, "override piece type", 0, int.MaxValue);
					var overrideEnabled = ParseBoolean(overrideParts[5], lineNumber, "piece_override enabled");
					pieceOverrides.Add(new RuntimeArmorRegistryPieceOverride(
						owner, overrideBody, overrideUnit.ToLowerInvariant(), overrideSlot, overridePieceType, overrideEnabled));
					break;
				case "target_mode":
					var modeParts = value.Split(',');
					if (modeParts.Length != 2)
						throw InvalidLine(lineNumber, "target_mode 必须是 target_item_id,follow|manual。");
					var modeTarget = ParseItemId(modeParts[0], lineNumber, "目标");
					var follow = modeParts[1].Trim() switch
					{
						"follow" => true,
						"manual" => false,
						_ => throw InvalidLine(lineNumber, "target_mode 必须是 follow 或 manual。"),
					};
					if (targetModes.Any(mode => mode.TargetItemId == modeTarget))
						throw InvalidLine(lineNumber, $"目标护甲 {modeTarget} 的 target_mode 重复。");
					targetModes.Add(new RuntimeArmorRegistryTargetMode(modeTarget, follow));
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
		var parsedModels = models
			.Select(model => model with { Pieces = modelPieces[model.Key].ToArray() })
			.OrderBy(model => model.Key, StringComparer.Ordinal)
			.ToArray();
		return new RuntimeArmorRegistryDocument(revision.Value, enabled.Value,
			NormalizeMappings(mappings), parsedModels, pieceOverrides, targetModes);
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
			foreach (var piece in model.Pieces ?? Array.Empty<RuntimeArmorRegistryModelPiece>())
			{
				builder.Append("model_piece=").Append(model.Key).Append(',')
					.Append(piece.Body.ToString(CultureInfo.InvariantCulture)).Append(',')
					.Append(piece.Unit).Append(',')
					.Append(piece.Slot.ToString(CultureInfo.InvariantCulture)).Append(',')
					.Append(piece.PieceType.ToString(CultureInfo.InvariantCulture)).Append(',')
					.Append(piece.Tone.ToString(CultureInfo.InvariantCulture)).Append(',')
					.Append(piece.Enabled ? "true" : "false");
				if (piece.IsDefaultBase)
					builder.Append(",true");
				builder.Append('\n');
			}
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
		foreach (var item in document.PieceOverrideDefinitions.OrderBy(item => item.OwnerItemId)
			.ThenBy(item => item.Body).ThenBy(item => item.Slot).ThenBy(item => item.PieceType))
		{
			builder.Append("piece_override=")
				.Append(item.OwnerItemId.ToString(CultureInfo.InvariantCulture)).Append(',')
				.Append(item.Body.ToString(CultureInfo.InvariantCulture)).Append(',')
				.Append(item.Unit).Append(',')
				.Append(item.Slot.ToString(CultureInfo.InvariantCulture)).Append(',')
				.Append(item.PieceType.ToString(CultureInfo.InvariantCulture)).Append(',')
				.Append(item.Enabled ? "true" : "false").Append('\n');
		}
		foreach (var mode in document.TargetModeDefinitions.OrderBy(mode => mode.TargetItemId))
		{
			builder.Append("target_mode=")
				.Append(mode.TargetItemId.ToString(CultureInfo.InvariantCulture)).Append(',')
				.Append(mode.FollowSource ? "follow" : "manual").Append('\n');
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

	private static IReadOnlyList<RuntimeArmorRegistryPieceOverride> NormalizePieceOverrides(
		IEnumerable<RuntimeArmorRegistryPieceOverride> overrides)
		=> overrides
			.OrderBy(item => item.OwnerItemId)
			.ThenBy(item => item.Body)
			.ThenBy(item => item.Slot)
			.ThenBy(item => item.PieceType)
			.ThenBy(item => item.Unit, StringComparer.Ordinal)
			.ToArray();

	private static IReadOnlyList<RuntimeArmorRegistryTargetMode> NormalizeTargetModes(
		IEnumerable<RuntimeArmorRegistryTargetMode> modes)
		=> modes.OrderBy(mode => mode.TargetItemId).ToArray();

	private static void ValidatePieceOverrides(
		IEnumerable<RuntimeArmorRegistryPieceOverride> overrides)
	{
		var keys = new HashSet<(uint Owner, uint Body, uint Slot, uint PieceType, string Unit)>();
		foreach (var item in overrides)
		{
			if (item.Slot == 0)
				throw new InvalidDataException($"组件覆盖对象 {item.OwnerItemId} 使用了无效的 slot=0。");
			if (item.Unit.Length != 16 || !item.Unit.All(Uri.IsHexDigit))
				throw new InvalidDataException($"组件覆盖对象 {item.OwnerItemId} 包含无效 Unit ID。");
			if (!keys.Add((item.OwnerItemId, item.Body, item.Slot, item.PieceType, item.Unit)))
				throw new InvalidDataException(
					$"组件覆盖对象 {item.OwnerItemId} 重复使用 body={item.Body}, slot={item.Slot}, piece_type={item.PieceType}, unit={item.Unit}。");
		}
	}

	private static void ValidateTargetModes(
		IEnumerable<RuntimeArmorRegistryTargetMode> modes)
	{
		var targets = new HashSet<uint>();
		foreach (var mode in modes)
		{
			if (!targets.Add(mode.TargetItemId))
				throw new InvalidDataException($"目标护甲 {mode.TargetItemId} 的 target_mode 重复。");
		}
	}

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
			var pieceKeys = new HashSet<(uint Body, uint Slot, uint PieceType)>();
			foreach (var piece in model.Pieces ?? Array.Empty<RuntimeArmorRegistryModelPiece>())
			{
				if (piece.Slot == 0)
					throw new InvalidDataException($"模型 {model.Key} 使用了无效的 slot=0。");
				if (piece.Unit.Length != 16 || !piece.Unit.All(Uri.IsHexDigit))
					throw new InvalidDataException($"模型 {model.Key} 包含无效 Unit ID。");
				if (!pieceKeys.Add((piece.Body, piece.Slot, piece.PieceType)))
					throw new InvalidDataException(
						$"模型 {model.Key} 重复使用 body={piece.Body}, slot={piece.Slot}, piece_type={piece.PieceType}。");
			}
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

	private static bool ParseBoolean(string value, int line, string field)
		=> value.Trim() switch
		{
			"true" => true,
			"false" => false,
			_ => throw InvalidLine(line, $"{field} 必须是 true 或 false。"),
		};

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
