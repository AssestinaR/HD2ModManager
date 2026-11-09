using System.Text.Json;
using HD2ModCore.Application;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

/// <summary>
/// 认领库根下的外部包：判定 → 硬链接导入 → 写标记 → 源目录移进回收站。
/// <para>
/// <b>为什么是「导入 + 移走」而不是「原地登记」。</b>原地登记要让每个选项子目录变成
/// <c>包名/选项</c> 这种两层相对路径，而库里从对账、导出、残留清理到部署都按「一层目录」
/// 写；改这一层假设的代价远大于复制一次（何况同卷可以硬链接，等于不复制）。
/// </para>
/// <para>
/// <b>为什么标记文件是必需的。</b>导入成功与移走源目录是两次独立的磁盘操作，中间任何一步
/// 失败，下一轮重扫都会再导入一遍——结果是同一个包在库里出现两份、三份。
/// 标记把这两步变成可恢复的状态机：<c>importing</c> → <c>imported</c> → 源目录消失。
/// </para>
/// </summary>
public sealed class ExternalPackageAdoptionService : IExternalPackageAdoptionService
{
	/// <summary>写在包根上的认领状态文件。前缀与其它管理器标记一致，残留清理会当成无害文件。</summary>
	public const string MarkerFileName = ".hd2modmanager-adoption.json";

	public const string MarkerSchema = "hd2mod.adoption/1";

	/// <summary>
	/// 单轮最多认领多少个包。一个包十几个选项、每个几百 MB，一口气搬完会让「校验库」卡很久；
	/// 幂等保证剩下的下次同步继续，所以限流是安全的。
	/// </summary>
	public const int MaxAdoptionsPerRound = 25;

	/// <summary>
	/// <c>importing</c> 标记超过这个时长就当作「上次崩在半路」，允许重新导入。
	/// 窗口内一律跳过——否则一轮同步里刚写下的标记会被自己当成残留。
	/// </summary>
	private static readonly TimeSpan StaleImportWindow = TimeSpan.FromMinutes(10);

	private static readonly JsonSerializerOptions MarkerJsonOptions = new(JsonSerializerDefaults.Web)
	{
		WriteIndented = true,
		Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
	};

	private readonly StoragePaths _paths;
	private readonly IModLibraryImporter _importer;
	private readonly IPatchFileNameParser _fileNameParser;
	private readonly OptionActivationStore _optionActivations;
	private readonly OptionActivationDefaultInitializer _optionDefaults;

	public ExternalPackageAdoptionService(
		StoragePaths paths,
		IModLibraryImporter importer,
		IPatchFileNameParser fileNameParser,
		OptionActivationStore? optionActivations = null,
		OptionActivationDefaultInitializer? optionDefaults = null)
	{
		_paths = paths ?? throw new ArgumentNullException(nameof(paths));
		_importer = importer ?? throw new ArgumentNullException(nameof(importer));
		_fileNameParser = fileNameParser ?? throw new ArgumentNullException(nameof(fileNameParser));
		_optionActivations = optionActivations ?? CoreServices.CreateOptionActivationStore(paths);
		_optionDefaults = optionDefaults ?? new OptionActivationDefaultInitializer();
	}

	public async ValueTask<ExternalPackageAdoptionResult> AdoptAsync(
		LibrarySnapshot snapshot,
		string modsRootDirectory,
		IReadOnlySet<string>? excludedRelativePaths = null,
		IProgress<string>? progress = null,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		ArgumentException.ThrowIfNullOrWhiteSpace(modsRootDirectory);
		var root = Path.GetFullPath(modsRootDirectory);
		if (!Directory.Exists(root))
			return new ExternalPackageAdoptionResult(snapshot, Array.Empty<ExternalPackageAdoptionOutcome>(), Array.Empty<ModNodeId>());

		var managed = snapshot.Nodes.Values
			.Select(node => node.RelativePath)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		var candidates = ExternalPackageAdoptionPlanner.Plan(
			root, managed, excludedRelativePaths, _fileNameParser, cancellationToken);
		if (candidates.Count == 0)
			return new ExternalPackageAdoptionResult(snapshot, Array.Empty<ExternalPackageAdoptionOutcome>(), Array.Empty<ModNodeId>());

		var outcomes = new List<ExternalPackageAdoptionOutcome>(candidates.Count);
		var importedNodeIds = new List<ModNodeId>();
		var current = snapshot;
		var attempted = 0;
		var adoptableTotal = candidates.Count(candidate => candidate.IsAdoptable);

		foreach (var candidate in candidates)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (!candidate.IsAdoptable)
			{
				outcomes.Add(new ExternalPackageAdoptionOutcome(
					candidate.RelativePath, ExternalPackageAdoptionStatus.Skipped, Array.Empty<ModNodeId>(), candidate.BlockedReason));
				continue;
			}

			if (attempted >= MaxAdoptionsPerRound)
			{
				outcomes.Add(new ExternalPackageAdoptionOutcome(
					candidate.RelativePath, ExternalPackageAdoptionStatus.Skipped, Array.Empty<ModNodeId>(),
					$"单轮上限 {MaxAdoptionsPerRound} 个，留到下次同步继续。"));
				continue;
			}

			attempted++;
			progress?.Report($"认领外部包 {attempted}/{adoptableTotal}：{candidate.RelativePath}");
			var (outcome, nextSnapshot) = await AdoptOneAsync(candidate, current, importedNodeIds, cancellationToken)
				.ConfigureAwait(false);
			outcomes.Add(outcome);
			current = nextSnapshot;
		}

		return new ExternalPackageAdoptionResult(current, outcomes, importedNodeIds);
	}

	private async ValueTask<(ExternalPackageAdoptionOutcome Outcome, LibrarySnapshot Snapshot)> AdoptOneAsync(
		ExternalPackageAdoptionCandidate candidate,
		LibrarySnapshot current,
		List<ModNodeId> importedNodeIds,
		CancellationToken cancellationToken)
	{
		var marker = TryReadMarker(candidate.FullPath);

		// 上轮导入成功、源目录还没移走：靠标记把它认出来，避免重复导入。
		if (marker is { Status: AdoptionStatus.Imported, NodeIds.Count: > 0 })
		{
			var stillInLibrary = marker.NodeIds.All(id => current.Nodes.ContainsKey(id));
			if (stillInLibrary)
			{
				// 节点还在库里，而源目录又出现在库根上——最典型的来源是用户**从回收站还原**了一份。
				// 这时候再移一次就等于把用户的还原动作静默撤销，所以只报告、不动手。
				return (Outcome(candidate, ExternalPackageAdoptionStatus.Skipped, marker.NodeIds,
					"该包已在库中（认领标记记录的节点都还在）。如需重新认领，删掉该目录里的 "
					+ MarkerFileName + " 后再校验。"), current);
			}
			// 节点已经不在库里：标记过期了。此时源目录是唯一副本，必须重新导入——
			// 若照旧「只补移动」，就等于把它丢进回收站而库里什么都没有。
			TryDeleteMarker(candidate.FullPath);
		}

		// 上轮崩在导入中途：窗口内先跳过，避免一轮同步里自我重入。
		if (marker is { Status: AdoptionStatus.Importing }
			&& DateTimeOffset.UtcNow - marker.StartedUtc < StaleImportWindow)
		{
			return (Outcome(candidate, ExternalPackageAdoptionStatus.Skipped, Array.Empty<ModNodeId>(),
				"上一轮导入尚未结束，本轮先跳过。"), current);
		}

		if (!IsWritable(candidate.FullPath, out var writeReason))
		{
			return (Outcome(candidate, ExternalPackageAdoptionStatus.Skipped, Array.Empty<ModNodeId>(), writeReason), current);
		}

		WriteMarker(candidate.FullPath, AdoptionStatus.Importing, Array.Empty<ModNodeId>(), DateTimeOffset.UtcNow);
		try
		{
			var preferHardLinks = IsSameVolume(candidate.FullPath, _paths.ModsDirectory);
			var import = await _importer
				.ImportLibraryResidentFolderAsync(candidate.FullPath, preferHardLinks, cancellationToken)
				.ConfigureAwait(false);
			var nodeIds = import.ImportedNodeIds;
			WriteMarker(candidate.FullPath, AdoptionStatus.Imported, nodeIds, DateTimeOffset.UtcNow);
			importedNodeIds.AddRange(nodeIds);

			// 选项默认态：和 GUI 导入走同一套规则（标准父节点下的直属选项默认全开）。
			// 漏掉这一步，包进库了但每个部件都是"关"，用户看到的是"导入成功、游戏里没变化"。
			string? defaultFailure = null;
			try
			{
				await _optionDefaults.InitializeMissingStatesAsync(
					import.Snapshot,
					_optionActivations,
					nodeIds.Count == 0 ? [import.ImportedRootId] : nodeIds,
					enableDefaults: true,
					cancellationToken).ConfigureAwait(false);
			}
			catch (Exception exception) when (exception is IOException
				or UnauthorizedAccessException
				or InvalidDataException)
			{
				defaultFailure = exception.Message;
			}

			var moveFailure = TryMoveToTrash(candidate.FullPath);
			var detail = moveFailure is null
				? (preferHardLinks ? "已导入（硬链接）。" : "已导入（复制）。")
				: $"已导入，但源目录移动失败：{moveFailure}（下一轮只重试移动）";
			if (defaultFailure is not null)
				detail += $" 选项默认态初始化失败：{defaultFailure}";
			// 作者漏写 manifest 是常见的；内容已经收进来了，但用户该知道"这个选项不是作者声明的"。
			if (import.UnownedPayloadPaths.Count > 0)
				detail += $" manifest 未声明的目录已按实际内容收为选项：{string.Join("、", import.UnownedPayloadPaths)}。";
			// 候选阶段的提示（预览图缺失、manifest 声明了没发布的选项目录…）只在这里有机会
			// 落进日志——GUI 的认领路径没有 dry-run，用户看不到 planner 的告警。
			if (candidate.Warnings.Count > 0)
				detail += " 注意：" + string.Join("；", candidate.Warnings);
			return (Outcome(candidate,
				moveFailure is null ? ExternalPackageAdoptionStatus.Adopted : ExternalPackageAdoptionStatus.Failed,
				nodeIds, detail), import.Snapshot);
		}
		catch (Exception exception) when (exception is IOException
			or UnauthorizedAccessException
			or InvalidDataException
			or InvalidOperationException
			or NotSupportedException
			or System.Security.SecurityException)
		{
			// 导入器自己已经回滚了半成品（CommitImportAsync 的 catch），这里只需保证
			// 源目录原样保留、标记不残留——否则下一轮会被误判成「已导入」而直接移走。
			TryDeleteMarker(candidate.FullPath);
			return (Outcome(candidate, ExternalPackageAdoptionStatus.Failed, Array.Empty<ModNodeId>(), exception.Message), current);
		}
		catch (OperationCanceledException)
		{
			TryDeleteMarker(candidate.FullPath);
			throw;
		}
	}

	private static ExternalPackageAdoptionOutcome Outcome(
		ExternalPackageAdoptionCandidate candidate,
		ExternalPackageAdoptionStatus status,
		IReadOnlyList<ModNodeId> nodeIds,
		string? detail)
		=> new(candidate.RelativePath, status, nodeIds, detail);

	/// <summary>源与目标是否同卷——硬链接的前提。不同卷时 <c>CopyFile</c> 本来也会回落复制，这里只是先声明意图。</summary>
	private static bool IsSameVolume(string first, string second)
	{
		try
		{
			return string.Equals(
				Path.GetPathRoot(Path.GetFullPath(first)),
				Path.GetPathRoot(Path.GetFullPath(second)),
				StringComparison.OrdinalIgnoreCase);
		}
		catch (ArgumentException) { return false; }
		catch (NotSupportedException) { return false; }
	}

	/// <summary>
	/// 在源目录里建一个探针文件再删掉。不先试一次的话，只读库（例如 Program Files 下的安装）
	/// 会走到「导入成功、移动失败、每轮重试」这条最吵的路上。
	/// </summary>
	private static bool IsWritable(string directory, out string? reason)
	{
		var probe = Path.Combine(directory, $".hd2modmanager-adoption-probe-{Guid.NewGuid():N}");
		try
		{
			File.WriteAllText(probe, string.Empty);
			File.Delete(probe);
			reason = null;
			return true;
		}
		catch (Exception exception) when (exception is IOException
			or UnauthorizedAccessException
			or NotSupportedException
			or System.Security.SecurityException)
		{
			reason = $"目录不可写（{exception.GetType().Name}），无法写入认领标记：{exception.Message}";
			return false;
		}
	}

	/// <summary>
	/// 把源目录移进 <c>.hd2modmanager-trash/&lt;事务&gt;/&lt;原名&gt;</c>。
	/// <para>
	/// 为什么不用 <see cref="ModStorageDeletionService"/>：那条路是「删掉受管节点」，
	/// 会登记 pending-deletion 墓碑、参与对账排除集；而认领的语义是「源目录已经不需要了，
	/// 但保留一份可还原的副本」，登记墓碑反而会让下一轮对账把新节点当成待删。
	/// </para>
	/// </summary>
	private string? TryMoveToTrash(string directory)
	{
		var transactionDirectory = Path.Combine(_paths.DeletionTrashDirectory, Guid.NewGuid().ToString("N"));
		try
		{
			Directory.CreateDirectory(transactionDirectory);
			Directory.Move(directory, Path.Combine(transactionDirectory, Path.GetFileName(directory)));
			return null;
		}
		catch (Exception exception) when (exception is IOException
			or UnauthorizedAccessException
			or NotSupportedException
			or System.Security.SecurityException)
		{
			TryDeleteEmptyDirectory(transactionDirectory);
			return exception.Message;
		}
	}

	private static void TryDeleteEmptyDirectory(string directory)
	{
		try
		{
			if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
				Directory.Delete(directory);
		}
		catch (IOException) { }
		catch (UnauthorizedAccessException) { }
	}

	private static AdoptionMarker? TryReadMarker(string directory)
	{
		var path = Path.Combine(directory, MarkerFileName);
		try
		{
			if (!File.Exists(path)) return null;
			var document = JsonSerializer.Deserialize<AdoptionMarkerDocument>(File.ReadAllText(path), MarkerJsonOptions);
			if (document is null || !string.Equals(document.Schema, MarkerSchema, StringComparison.Ordinal)) return null;
			var status = string.Equals(document.Status, "imported", StringComparison.OrdinalIgnoreCase)
				? AdoptionStatus.Imported
				: AdoptionStatus.Importing;
			var nodeIds = (document.NodeIds ?? Array.Empty<string>())
				.Select(value => Guid.TryParse(value, out var parsed) ? new ModNodeId(parsed) : (ModNodeId?)null)
				.Where(id => id.HasValue)
				.Select(id => id!.Value)
				.ToArray();
			return new AdoptionMarker(status, nodeIds, document.StartedUtc ?? DateTimeOffset.UtcNow);
		}
		catch (JsonException) { return null; }
		catch (IOException) { return null; }
		catch (UnauthorizedAccessException) { return null; }
	}

	private static void WriteMarker(
		string directory,
		AdoptionStatus status,
		IReadOnlyList<ModNodeId> nodeIds,
		DateTimeOffset startedUtc)
	{
		var path = Path.Combine(directory, MarkerFileName);
		var document = new AdoptionMarkerDocument(
			MarkerSchema,
			status == AdoptionStatus.Imported ? "imported" : "importing",
			nodeIds.Select(id => id.Value.ToString("N")).ToArray(),
			startedUtc);
		var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			File.WriteAllText(temporary, JsonSerializer.Serialize(document, MarkerJsonOptions) + "\n",
				new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
			File.Move(temporary, path, overwrite: true);
		}
		catch (IOException) { }
		catch (UnauthorizedAccessException) { }
		finally
		{
			try { if (File.Exists(temporary)) File.Delete(temporary); }
			catch (IOException) { }
			catch (UnauthorizedAccessException) { }
		}
	}

	private static void TryDeleteMarker(string directory)
	{
		try
		{
			var path = Path.Combine(directory, MarkerFileName);
			if (File.Exists(path)) File.Delete(path);
		}
		catch (IOException) { }
		catch (UnauthorizedAccessException) { }
	}

	private enum AdoptionStatus { Importing, Imported }

	private sealed record AdoptionMarker(AdoptionStatus Status, IReadOnlyList<ModNodeId> NodeIds, DateTimeOffset StartedUtc);

	private sealed record AdoptionMarkerDocument(
		string Schema,
		string Status,
		IReadOnlyList<string>? NodeIds,
		DateTimeOffset? StartedUtc);
}
