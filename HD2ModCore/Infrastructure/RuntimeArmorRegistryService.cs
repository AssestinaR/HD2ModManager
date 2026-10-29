using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HD2ModAdaptation.Analysis;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModCore.Domain;
using Microsoft.Data.Sqlite;

namespace HD2ModCore.Infrastructure;

public sealed class RuntimeArmorRegistryService
{
	public const string ProtocolFileName = "armor-registry.protocol";
	public const string RuntimeStatusFileName = "runtime.status";
	public const string LocatorCacheFileName = "locator.cache";
	public const string RuntimeLogFileName = "ArmorRegistryRuntime.log";
	public const string DeploymentStateFileName = "runtime-armor-registry.deployment";
	public const string WorkspaceFileName = "armor-registry.workspace.json";
	public const string AppliedSourceFileName = "armor-registry.applied.json";
	// 协议旁边记录"这份协议属于哪个模组库"。
	// 为什么必须有：判断协议归属的旧凭据（DeploymentStateFileName）在程序目录下，**换目录解压就会丢**，
	// 而协议本身在用户目录里、换目录仍在——用"升级必被摧毁的凭据"守护"升级必被保留的文件"，
	// 守卫必然退化成"没有凭据 ⇒ 一律相信"（见 docs/armor-registry-page-20261011-regression.md §13）。
	// 把归属写在与协议同目录、同生命周期的地方，凭据才与被守护的对象同生共死。
	// 读取时优先本文件，回退旧的部署状态文件；两者都没有时**不拒绝**，只是无法判断归属（见 ReadSnapshotAsync）。
	public const string OwnerFileName = "armor-registry.owner";

	/// <summary>
	/// 自定义素体在"护甲归档 id"这个位置上的写法。它不是一个游戏归档，只是为了让
	/// "目标护甲"这一个下拉能同时装下游戏护甲和注册素体，于是给它一个带前缀的伪 id。
	/// <b>拼这个前缀只许走 <see cref="CustomModelArchiveId"/>，解析只许走 <see cref="TryReadCustomModelKey"/>。</b>
	/// </summary>
	public const string CustomModelArchiveIdPrefix = "custom-model:";

	public static string CustomModelArchiveId(string modelKey)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(modelKey);
		return CustomModelArchiveIdPrefix + modelKey;
	}

	/// <summary>认出一个自定义素体伪 id 并取出它的 key；不是这个形状就返回 false。</summary>
	public static bool TryReadCustomModelKey(string? archiveId, out string modelKey)
	{
		modelKey = string.Empty;
		if (string.IsNullOrWhiteSpace(archiveId)) return false;
		if (!archiveId.StartsWith(CustomModelArchiveIdPrefix, StringComparison.OrdinalIgnoreCase)) return false;
		var key = archiveId[CustomModelArchiveIdPrefix.Length..].Trim();
		if (key.Length == 0) return false;
		modelKey = key;
		return true;
	}

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
	public string RuntimeLogPath => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"CowboyBingus", "Helldivers2", "Logs", RuntimeLogFileName);

	public async ValueTask<IReadOnlyList<string>> ReadRecentRuntimeLogSummaryAsync(
		CancellationToken cancellationToken = default)
	{
		if (!File.Exists(RuntimeLogPath)) return Array.Empty<string>();
		const int maximumBytes = 128 * 1024;
		await using var stream = new FileStream(RuntimeLogPath, FileMode.Open, FileAccess.Read,
			FileShare.ReadWrite, 4096, FileOptions.SequentialScan);
		var start = Math.Max(0, stream.Length - maximumBytes);
		stream.Seek(start, SeekOrigin.Begin);
		using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
		var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
		return text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
			.Where(line => line.Contains("skipped_target:", StringComparison.Ordinal)
				|| line.Contains("partial_apply:", StringComparison.Ordinal)
				|| line.Contains("apply_error:", StringComparison.Ordinal)
				|| line.Contains("protocol_error:", StringComparison.Ordinal))
			.TakeLast(12)
			.ToArray();
	}

	// Library projections must not inspect the running game or another library's deployed protocol.
	public async ValueTask<IReadOnlyList<RuntimeArmorRegistryModel>> ReadWorkspaceModelsAsync(CancellationToken cancellationToken = default)
		=> File.Exists(WorkspacePath)
			? (await ReadWorkspaceAsync(cancellationToken).ConfigureAwait(false)).ModelDefinitions
			: Array.Empty<RuntimeArmorRegistryModel>();

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

	/// <summary>
	/// 把真映射模式写进注册表的数据还原成原版。只在确实改过（存在状态文件）时动作。
	///
	/// 离开真映射模式时必须调用：数据层的改动没有任何运行时能撤销，留着它再叠加
	/// 运行时覆盖，等于把外观映射了两次。
	/// </summary>
	public async ValueTask<ArmorRegistryDataMappingResult> RestoreDataMappingAsync(
		string gameDataDirectory,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(gameDataDirectory)) return ArmorRegistryDataMappingResult.Nothing;
		var backupDirectory = Path.Combine(registryRoot, "backup");
		if (!File.Exists(Path.Combine(backupDirectory, ArmorRegistryDataMapper.StateFileName)))
			return ArmorRegistryDataMappingResult.Nothing;
		return await new ArmorRegistryDataMapper().ApplyAsync(
			gameDataDirectory,
			Array.Empty<ArmorRegistryDataMapping>(),
			backupDirectory,
			cancellationToken).ConfigureAwait(false);
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

	/// <summary>素体件表里的 unit 是 16 位十六进制字符串；解析失败表示这条件不可用。</summary>
	static bool TryParseUnitHex(string? text, out ulong unit)
	{
		unit = 0;
		if (string.IsNullOrWhiteSpace(text)) return false;
		var trimmed = text.Trim();
		if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) trimmed = trimmed[2..];
		return ulong.TryParse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out unit);
	}
	public async ValueTask<IReadOnlyList<RuntimeArmorCatalogEntry>> ReadArmorCatalogAsync(
		CancellationToken cancellationToken = default)
	{
		if (!File.Exists(paths.DbPath)) return Array.Empty<RuntimeArmorCatalogEntry>();
		await using var connection = new SqliteConnection($"Data Source={paths.DbPath};Mode=ReadOnly;Cache=Shared");
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using var command = connection.CreateCommand();
		command.CommandText = """
			SELECT item_id, archive_id, display_name, kit_kind, set_id, dlc_id
			FROM game_data_authority_kits
			WHERE kit_kind IN (0, 1)
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
				reader.GetString(2),
				reader.GetInt64(3) switch
				{
					0 => GameDataEquipmentKitKind.Armor,
					1 => GameDataEquipmentKitKind.Helmet,
					_ => GameDataEquipmentKitKind.Unknown
				},
				(uint)Math.Clamp(reader.GetInt64(4), 0, uint.MaxValue),
				(uint)Math.Clamp(reader.GetInt64(5), 0, uint.MaxValue)));
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
			SELECT raw_body_type, unit_file_id, slot, piece_type, tone_variations
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

	/// <summary>
	/// 把管理器自己持有的虚拟素体投影成跨护甲规划器消费的同一种目录形状。
	/// </summary>
	/// <remarks>
	/// 这些条目**没有 GameData 归档**，但它们的格子不是"未知"：素体的每个组件都显式声明了
	/// <c>(slot, pieceType)</c>，那正好就是 <see cref="ArmorCell"/> 的两个坐标，与注册表给
	/// 原版 unit 的权威格子同构。以前这里留成 <see cref="ArmorCell.Unknown"/>，代价是虚拟
	/// unit 在替换护甲页一律"未定位"、既不能当来源也不参与装箱。
	/// </remarks>
	public async ValueTask<IReadOnlyList<EquipmentUnitCatalogEntry>> ReadCustomModelCatalogAsync(
		CancellationToken cancellationToken = default)
	{
		var snapshot = await ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
		ValidateModels(snapshot.Document.ModelDefinitions, snapshot.Document.Mappings);
		return snapshot.Document.ModelDefinitions
			.Where(model => model.Pieces is { Count: > 0 })
			.Select(model => new EquipmentUnitCatalogEntry(
				CustomModelArchiveId(model.Key),
				"CustomModel",
				string.IsNullOrWhiteSpace(model.Name) ? model.Key : model.Name!,
				RuntimeArmorModelStructure.Active(model.Pieces ?? Array.Empty<RuntimeArmorRegistryModelPiece>())
					.Where(piece => piece.Enabled)
					.Select(piece => new EquipmentUnitPart(
						new AssetKey(HD2ModAdaptation.PatchReconstruction.UnitMesh.PatchUnitMeshReader.UnitTypeId,
							ulong.Parse(piece.Unit, NumberStyles.HexNumber, CultureInfo.InvariantCulture)),
						0,
						0,
						ArmorStructure.ToPartKind(piece.Slot),
						// 层级由 pieceType 决定（0 内层 / 1 护甲 / 2 装饰），不能一律写 Armor：
						// 下游按"层 == Undergarment 或 部位 == 头盔"来挑基础 7 槽，写错会让
						// 自定义素体的身体件全部落进"护甲层"，于是被当成附件隐藏掉。
						// Layer follows pieceType; hardcoding Armor pushed every body piece of a
						// custom model into the armor layer and made the base-7 filter drop it.
						ArmorStructure.ToLayer(piece.PieceType),
						ArmorStructure.ToBodyVariant(piece.Body),
						$"custom_{model.Key}_{piece.Slot}_{piece.PieceType}",
						100,
						Array.Empty<string>())
					{
						PieceType = piece.PieceType.ToString(CultureInfo.InvariantCulture),
						GeometryQuality = UnitMeshGeometryQuality.Unreadable,
						Cell = ToPieceCell(piece)
					})
					.ToArray()) { CustomModelKey = model.Key })
			.ToArray();
	}

	/// <summary>
	/// 组件声明的 <c>(slot, pieceType)</c> → 权威格子。
	/// </summary>
	/// <remarks>
	/// 只认 <see cref="ArmorStructure"/> 真的认识的槽位、以及合法层号。越界时保留
	/// <see cref="ArmorCell.Unknown"/>：宁可显示"未定位"，也不要造出一个查不到部位、
	/// 却因为坐标非负而被当成"已定位"的格子——那种半懂状态会被装箱规划器当成真格子用。
	/// </remarks>
	private static ArmorCell ToPieceCell(RuntimeArmorRegistryModelPiece piece)
		=> ArmorStructure.FromSlot(piece.Slot) != ArmorPart.Unknown && piece.PieceType <= ArmorStructure.MaxPieceType
			? new ArmorCell((int)piece.Slot, (int)piece.PieceType)
			: ArmorCell.Unknown;

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
		var workspaceExists = File.Exists(WorkspacePath);
		// 归属回退链：协议旁边的 owner（新，与协议同生命周期）→ 程序目录的部署状态（旧，换目录即丢）→ 无从判断。
		// 注意这里**只用于展示**，不参与"要不要采用协议"的决定——采用与否仍然只看上面那条 workspaceBelongsToCurrentLibrary，
		// 因为把回退收紧会让换过目录的老用户直接看不到自己的映射（用户已明确否决该方向）。
		var owner = await ReadKeyValueFileAsync(Path.Combine(registryRoot, OwnerFileName), cancellationToken).ConfigureAwait(false);
		string? protocolOwnerWorkspace = null;
		if (owner.Values.TryGetValue("workspace", out var ownedWorkspace) && !string.IsNullOrWhiteSpace(ownedWorkspace))
			protocolOwnerWorkspace = ownedWorkspace;
		else if (deployment.Values.TryGetValue("workspace", out var deployedOwner) && !string.IsNullOrWhiteSpace(deployedOwner))
			protocolOwnerWorkspace = deployedOwner;
		var documentSource = workspaceExists
			? RuntimeArmorRegistryDocumentSource.Workspace
			: protocolExists && workspaceBelongsToCurrentLibrary
				? RuntimeArmorRegistryDocumentSource.ProtocolFallback
				: RuntimeArmorRegistryDocumentSource.Empty;
		var document = workspaceExists
			? await ReadWorkspaceAsync(cancellationToken).ConfigureAwait(false)
			: documentSource is RuntimeArmorRegistryDocumentSource.ProtocolFallback
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
		var appliedSourcePath = Path.Combine(WorkspaceRoot, AppliedSourceFileName);
		var pendingChanges = !workspaceExists && !protocolExists ? false : File.Exists(appliedSourcePath)
			? !string.Equals(await File.ReadAllTextAsync(appliedSourcePath, cancellationToken).ConfigureAwait(false),
				SerializeWorkspace(document), StringComparison.Ordinal)
			: !protocolExists || !string.Equals(
				await ReadBoundedTextAsync(protocolPath, MaximumProtocolBytes, cancellationToken).ConfigureAwait(false),
				SerializeProtocol(document), StringComparison.Ordinal);
		// A previously "applied" workspace can still have an incompatible wire
		// file. Keep Apply available after upgrading without requiring a dummy edit.
		if (!pendingChanges && protocolExists)
		{
			var existingProtocol = await ReadBoundedTextAsync(protocolPath, MaximumProtocolBytes, cancellationToken).ConfigureAwait(false);
			pendingChanges = existingProtocol.Split('\n').Any(line =>
				line.TrimStart().StartsWith("model_piece=", StringComparison.Ordinal) && line.Split(',').Length == 8);
		}
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
			cache.Values)
		{
			HasUnappliedChanges = pendingChanges,
			DocumentSource = documentSource,
			ProtocolOwnerWorkspace = protocolOwnerWorkspace,
		};
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
		CancellationToken cancellationToken = default,
		string? gameDataDirectory = null)
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
			NormalizeTargetModes(targetModes),
			current.CompositionMode);
		var compiled = desired;
		if (gameDataDirectory is not null && desired.ModelDefinitions.Any(model => model.SourceItemId is null))
			compiled = RuntimeArmorResourceFilter.Compile(desired,
				await RuntimeArmorResourceFilter.ReadDeployedUnitsAsync(gameDataDirectory, cancellationToken).ConfigureAwait(false));
		var text = SerializeProtocol(compiled);
		if (Encoding.UTF8.GetByteCount(text) > MaximumProtocolBytes)
			throw new InvalidDataException("编译后的护甲注册协议超过 64 KiB 上限。");
		if (System.Text.Encoding.UTF8.GetByteCount(SerializeWorkspace(desired)) > MaximumProtocolBytes * 4)
			throw new InvalidDataException("护甲注册源文件超过 256 KiB 上限。");
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
		// 运行时真正执行的是 unit 对列表（wardrobe.links）；协议本身只是管理器的意图记录。
		// 没有游戏数据目录或编译失败时**不写文件**——留上一版映射，比写一份空映射安全。
		if (gameDataDirectory is not null && Directory.Exists(gameDataDirectory))
		{
			try
			{
				// 数据源是索引数据库，**不是**游戏注册表文件：真映射会改写后者，
				// 读它会把"目标护甲有哪些 unit"算成被我们改过之后的样子。
				var authority = await ArmorAuthorityIndexReader.TryReadAsync(paths, cancellationToken).ConfigureAwait(false);
				if (authority is not null)
				{
					var links = WardrobeLinkCompiler.Compile(
						authority, compiled.Mappings, compiled.Revision, compiled.Enabled, compiled.ModelDefinitions);
					if (links.LinkCount > 0)
					{
						await WriteAtomicTextAsync(
							Path.Combine(registryRoot, WardrobeLinkCompiler.FileName),
							links.Text,
							cancellationToken).ConfigureAwait(false);
					}
				}
				// authority 为 null 表示索引数据库里没有装备关系表：这时**不写** wardrobe.links
				// （留上一版），但下面的真映射落地 / 还原照常进行——它不需要权威表。

				// 无论当前是哪个模式都要走一遍：离开真映射模式时必须把注册表还原，
				// 否则数据层改动会一直留着，再叠加运行时覆盖就等于把外观映射了两次。
				var inTrueMapping = LuaDeploymentService.MappingMode == ArmorMappingMode.TrueMapping;
				{
					var modelsByKey = compiled.ModelDefinitions
						.Where(model => !string.IsNullOrWhiteSpace(model.Key))
						.GroupBy(model => model.Key, StringComparer.Ordinal)
						.ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
					var dataMappings = new List<ArmorRegistryDataMapping>();
					foreach (var mapping in inTrueMapping && enabled ? compiled.Mappings : Array.Empty<RuntimeArmorRegistryMapping>())
					{
						if (mapping.IsModelMapping)
						{
							if (!modelsByKey.TryGetValue(mapping.ModelKey!, out var model)) continue;
							var pieces = new List<ArmorRegistryDataSourcePiece>();
							foreach (var piece in model.Pieces ?? Array.Empty<RuntimeArmorRegistryModelPiece>())
							{
								if (!piece.Enabled) continue;
								if (!TryParseUnitHex(piece.Unit, out var unit)) continue;
								pieces.Add(new ArmorRegistryDataSourcePiece(piece.Body, piece.Slot, piece.PieceType, unit));
							}
							if (pieces.Count > 0)
								dataMappings.Add(new ArmorRegistryDataMapping(mapping.TargetItemId, null, pieces));
							else if (model.SourceItemId is uint templateId)
								// 素体没有显式件表时，它的件来自被当作模板的那套真实护甲。
								dataMappings.Add(new ArmorRegistryDataMapping(mapping.TargetItemId, templateId));
							continue;
						}
						if (mapping.SourceItemId is uint sourceId)
							dataMappings.Add(new ArmorRegistryDataMapping(mapping.TargetItemId, sourceId));
					}
					// 真映射模式下按映射重建；其它模式（以及关闭映射时）用空列表调用，
					// 走还原路径。只有确实改过（存在状态文件）才需要还原，避免无谓的
					// 解密与备份播种。
					var backupDirectory = Path.Combine(registryRoot, "backup");
					var everModified = File.Exists(
						Path.Combine(backupDirectory, ArmorRegistryDataMapper.StateFileName));
					if (inTrueMapping || everModified)
					{
						await new ArmorRegistryDataMapper().ApplyAsync(
							gameDataDirectory,
							dataMappings,
							backupDirectory,
							cancellationToken).ConfigureAwait(false);
					}
				}
			}
			catch (Exception exception) when (exception is IOException
				or InvalidDataException
				or InvalidOperationException
				or UnauthorizedAccessException)
			{
				// 链接编译失败不应让已经写好的协议作废；运行时保留上一版映射。
			}
		}
		await WriteWorkspaceAsync(desired, cancellationToken).ConfigureAwait(false);		await WriteAtomicTextAsync(Path.Combine(WorkspaceRoot, AppliedSourceFileName),
			SerializeWorkspace(desired), cancellationToken).ConfigureAwait(false);
		Directory.CreateDirectory(paths.DataDirectory);
		await File.WriteAllTextAsync(
			DeploymentStatePath,
			(enabled ? "enabled=true\n" : "enabled=false\n")
				+ "workspace=" + WorkspacePath + "\n",
			new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
			cancellationToken).ConfigureAwait(false);
		// 归属标记写在**协议旁边**，不是程序目录里：这样它与协议同生命周期，
		// 换目录解压后仍然存在，页面才能告诉用户"这份协议来自哪个库"。
		// 程序目录里的 DeploymentStatePath 仍然照写——它是给"同一安装内的库切换"用的旧凭据，不删。
		await WriteAtomicTextAsync(
			Path.Combine(registryRoot, OwnerFileName),
			"schema=1\n"
				+ "workspace=" + WorkspacePath + "\n"
				+ "revision=" + desired.Revision.ToString(CultureInfo.InvariantCulture) + "\n",
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
		if (workspace.CompositionMode is not ("full" or "existing_only"))
			throw new InvalidDataException("护甲注册源文件 compositionMode 必须是 full 或 existing_only。");
		var document = new RuntimeArmorRegistryDocument(
			workspace.Revision,
			workspace.Enabled,
			workspace.Mappings ?? Array.Empty<RuntimeArmorRegistryMapping>(),
			NormalizeLegacyHelmetSlots(workspace.Models ?? Array.Empty<RuntimeArmorRegistryModel>()),
			workspace.PieceOverrides ?? Array.Empty<RuntimeArmorRegistryPieceOverride>(),
			workspace.TargetModes ?? Array.Empty<RuntimeArmorRegistryTargetMode>(),
			workspace.CompositionMode);
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
		await WriteAtomicTextAsync(WorkspacePath, SerializeWorkspace(document), cancellationToken).ConfigureAwait(false);
	}

	private static string SerializeWorkspace(RuntimeArmorRegistryDocument document)
	{
		var workspace = new WorkspaceDocument(
			1,
			document.Revision,
			document.Enabled,
			document.Mappings,
			document.ModelDefinitions,
			document.PieceOverrideDefinitions,
			document.TargetModeDefinitions,
			document.CompositionMode);
		return JsonSerializer.Serialize(workspace, WorkspaceJsonOptions);
	}

	private static async Task WriteAtomicTextAsync(string path, string text, CancellationToken cancellationToken)
	{
		var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			await File.WriteAllTextAsync(
				temporaryPath,
				text,
				new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
				cancellationToken).ConfigureAwait(false);
			cancellationToken.ThrowIfCancellationRequested();
			File.Move(temporaryPath, path, overwrite: true);
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
		IReadOnlyList<RuntimeArmorRegistryTargetMode>? TargetModes,
		string CompositionMode = "full");

	public static RuntimeArmorRegistryDocument ParseProtocol(string text)
	{
		ArgumentNullException.ThrowIfNull(text);
		if (Encoding.UTF8.GetByteCount(text) > MaximumProtocolBytes)
			throw new InvalidDataException("护甲注册协议超过 64 KiB 上限。");
		int? schema = null;
		int? revision = null;
		bool? enabled = null;
		var compositionMode = "full";
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
				case "composition_mode":
					if (value is not ("full" or "existing_only"))
						throw InvalidLine(lineNumber, "composition_mode 必须是 full 或 existing_only。");
					compositionMode = value;
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
					var slot = ParseModelSlot(pieceParts[3], lineNumber, body);
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
					var overrideSlot = ParseOverrideSlot(overrideParts[3], lineNumber, overrideBody);
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
			NormalizeMappings(mappings), NormalizeLegacyHelmetSlots(parsedModels),
			pieceOverrides, targetModes, compositionMode);
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
		if (!string.Equals(document.CompositionMode, "full", StringComparison.Ordinal))
			builder.Append("composition_mode=existing_only\n");
		foreach (var model in document.ModelDefinitions.OrderBy(model => model.Key, StringComparer.Ordinal))
		{
			builder.Append("model=")
				.Append(model.Key).Append(',')
				.Append(model.SourceItemId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append('\n');
			foreach (var piece in RuntimeArmorModelStructure.Active(model.Pieces ?? Array.Empty<RuntimeArmorRegistryModelPiece>()))
			{
				builder.Append("model_piece=").Append(model.Key).Append(',')
					.Append(piece.Body.ToString(CultureInfo.InvariantCulture)).Append(',')
					.Append(piece.Unit).Append(',')
					.Append(piece.Slot.ToString(CultureInfo.InvariantCulture)).Append(',')
					.Append(piece.PieceType.ToString(CultureInfo.InvariantCulture)).Append(',')
					.Append(piece.Tone.ToString(CultureInfo.InvariantCulture)).Append(',')
					.Append(piece.Enabled ? "true" : "false");
				// Editor-only metadata belongs in the library workspace, not the Lua ABI.
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
		=> models.Select(model => model with { Pieces = RuntimeArmorModelStructure.Normalize(model.Pieces ?? []) })
			.OrderBy(model => model.Key, StringComparer.Ordinal).ToArray();

	private static IReadOnlyList<RuntimeArmorRegistryPieceOverride> NormalizePieceOverrides(
		IEnumerable<RuntimeArmorRegistryPieceOverride> overrides)
		=> overrides
			.OrderBy(item => item.OwnerItemId)
			.ThenBy(item => item.Body)
			.ThenBy(item => item.Slot)
			.ThenBy(item => item.PieceType)
			.ThenBy(item => item.Unit, StringComparer.Ordinal)
			.ToArray();

	private static IReadOnlyList<RuntimeArmorRegistryModel> NormalizeLegacyHelmetSlots(
		IEnumerable<RuntimeArmorRegistryModel> models)
		=> models.Select(model => model with
		{
			Pieces = (model.Pieces ?? Array.Empty<RuntimeArmorRegistryModelPiece>())
				.Select(piece => piece.Body == 3 && piece.Slot == 1 ? piece with { Slot = 0 } : piece)
				.ToArray()
		}).ToArray();

	private static uint ParseModelSlot(string value, int lineNumber, uint body)
	{
		var slot = (uint)ParseInteger(value, lineNumber, "model slot", 0, int.MaxValue);
		if (body == 3 && slot == 1) slot = 0;
		if (slot == 0 && body != 3)
			throw InvalidLine(lineNumber, "头盔 slot=0 只能用于 body=3 的 Any Piece。");
		return slot;
	}

	private static uint ParseOverrideSlot(string value, int lineNumber, uint body)
	{
		var slot = (uint)ParseInteger(value, lineNumber, "override slot", 0, int.MaxValue);
		if (slot == 0 && body != 3)
			throw InvalidLine(lineNumber, "头盔 slot=0 只能用于 body=3 的 Any Piece。");
		return slot;
	}

	private static IReadOnlyList<RuntimeArmorRegistryTargetMode> NormalizeTargetModes(
		IEnumerable<RuntimeArmorRegistryTargetMode> modes)
		=> modes.OrderBy(mode => mode.TargetItemId).ToArray();

	private static void ValidatePieceOverrides(
		IEnumerable<RuntimeArmorRegistryPieceOverride> overrides)
	{
		var keys = new HashSet<(uint Owner, uint Body, uint Slot, uint PieceType, string Unit)>();
		foreach (var item in overrides)
		{
			if (item.Slot == 0 && item.Body != 3)
				throw new InvalidDataException($"组件覆盖对象 {item.OwnerItemId} 的头盔 slot=0 只能用于 body=3。");
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
				if (piece.Slot == 0 && piece.Body != 3)
					throw new InvalidDataException($"模型 {model.Key} 的头盔 slot=0 只能用于 body=3。");
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
