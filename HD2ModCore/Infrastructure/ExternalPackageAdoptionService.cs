using System.Globalization;
using System.Text.Json;
using HD2ModCore.Application;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

/// <summary>
/// 认领库根下的外部包：判定 → 硬链接导入 → 写标记 → 源目录移进 <c>&lt;库&gt;.adopted</c>。
/// <para>
/// <b>为什么是「导入 + 移走」而不是「原地登记」。</b>原地登记要让每个选项子目录变成
/// <c>包名/选项</c> 这种两层相对路径，而库里从对账、导出、残留清理到部署都按「一层目录」
/// 写；改这一层假设的代价远大于复制一次（何况同卷可以硬链接，等于不复制）。
/// </para>
/// <para>
/// <b>为什么标记文件是必需的。</b>导入成功与移走源目录是两次独立的磁盘操作，中间任何一步
/// 失败，下一轮重扫都会再导入一遍——结果是同一个包在库里出现两份、三份。
/// 标记把这两步变成可恢复的状态机：<c>importing</c> → <c>imported</c>（待移动）→ <c>moved</c>。
/// </para>
/// <para>
/// <b>为什么副本要移出库外。</b>见 <see cref="StoragePaths.AdoptedArchiveDirectory"/>：
/// 放进 <c>.hd2modmanager-trash</c> 会让库根长期背着一棵嵌套树，而回收站从来没有清理入口。
/// </para>
/// </summary>
public sealed class ExternalPackageAdoptionService : IExternalPackageAdoptionService
{
	/// <summary>写在包根上的认领状态文件。前缀与其它管理器标记一致，残留清理会当成无害文件。</summary>
	public const string MarkerFileName = ".hd2modmanager-adoption.json";

	public const string MarkerSchema = "hd2mod.adoption/1";

	/// <summary>
	/// 单轮最多认领多少个包。一个包十几个选项、每个几百 MB，一口气搬完会让「校验库」卡很久；
	/// 幂等保证剩下的下次继续，所以限流是安全的。
	/// </summary>
	public const int MaxAdoptionsPerRound = 25;

	/// <summary>
	/// 认领副本的保留期。过期批次会被清掉——回收站没有清理入口的教训就在眼前：
	/// 一个只进不出的目录，最后一定变成没人敢动的黑洞。
	/// </summary>
	public static readonly TimeSpan ArchiveRetention = TimeSpan.FromDays(30);

	/// <summary>
	/// <c>importing</c> 标记超过这个时长就当作「上次崩在半路」，允许回滚后重新导入。
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

		// 顺手清一次过期副本：清理必须挂在「用户主动认领」这条路上，否则又会变成只进不出。
		var notes = PruneArchive();

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
				// 排队 ≠ 被拒。两者都用 Skipped 的话，界面每轮都会报「有 N 个包未能认领」，
				// 而它们其实只是还没轮到——用户会去查一个根本不存在的故障。
				outcomes.Add(new ExternalPackageAdoptionOutcome(
					candidate.RelativePath, ExternalPackageAdoptionStatus.Queued, Array.Empty<ModNodeId>(),
					$"排队中：单轮上限 {MaxAdoptionsPerRound} 个，下次认领继续。"));
				continue;
			}

			attempted++;
			progress?.Report($"认领外部包 {attempted}/{adoptableTotal}：{candidate.RelativePath}");
			var (outcome, nextSnapshot) = await AdoptOneAsync(candidate, current, importedNodeIds, cancellationToken)
				.ConfigureAwait(false);
			outcomes.Add(outcome);
			current = nextSnapshot;
		}

		return new ExternalPackageAdoptionResult(current, outcomes, importedNodeIds) { Notes = notes };
	}

	private async ValueTask<(ExternalPackageAdoptionOutcome Outcome, LibrarySnapshot Snapshot)> AdoptOneAsync(
		ExternalPackageAdoptionCandidate candidate,
		LibrarySnapshot current,
		List<ModNodeId> importedNodeIds,
		CancellationToken cancellationToken)
	{
		var marker = TryReadMarker(candidate.FullPath);
		if (marker is not null)
		{
			switch (marker.Status)
			{
				// 源目录已经从副本目录里被还原回库根，而节点也还在库里：最典型的来源是用户
				// **从 &lt;库&gt;.adopted 里还原**了一份。这时候再移一次等于把用户的还原动作静默撤销，
				// 所以只报告、不动手。
				case AdoptionStatus.Moved when AllStillInLibrary(marker, current):
					return (Outcome(candidate, ExternalPackageAdoptionStatus.Skipped, marker.NodeIds,
						"该包已在库中（认领标记记录的节点都还在）。如需重新认领，删掉该目录里的 "
						+ MarkerFileName + " 后再校验。"), current);

				// 上轮导入成功、只是源目录没搬走：**只补移动**，绝不重复导入。
				case AdoptionStatus.Imported when AllStillInLibrary(marker, current):
					return (CompletePendingMove(candidate, marker), current);

				// 标记说「已导入 / 已移走」，但库里已经没有那些节点（用户删了条目）。
				// 此时源目录是唯一副本，必须重新导入——照旧「只补移动」就等于把它搬走而库里什么都没有。
				case AdoptionStatus.Moved:
				case AdoptionStatus.Imported:
					TryDeleteMarker(candidate.FullPath);
					break;

				// 上轮崩在导入中途。窗口内先跳过；窗口外先回滚它已经提交的部分，再重新导入——
				// 不回滚就重导，库里会多出一份（导入是逐单元提交的，崩溃前可能已经落了几个单元）。
				case AdoptionStatus.Importing:
					if (DateTimeOffset.UtcNow - marker.StartedUtc < StaleImportWindow)
					{
						return (Outcome(candidate, ExternalPackageAdoptionStatus.Skipped, Array.Empty<ModNodeId>(),
							"上一轮导入尚未结束，本轮先跳过。"), current);
					}
					if (marker.NodeIds.Count > 0)
					{
						var rolledBack = await TryRollbackAsync(marker.NodeIds).ConfigureAwait(false);
						if (!rolledBack)
						{
							return (Outcome(candidate, ExternalPackageAdoptionStatus.Failed, marker.NodeIds,
								$"上次导入残留了 {marker.NodeIds.Count} 个节点，回滚失败，本轮先不动"
								+ "（源目录原样保留，下次认领继续尝试回滚）。"), current);
						}
					}
					TryDeleteMarker(candidate.FullPath);
					break;
			}
		}

		if (!IsWritable(candidate.FullPath, out var writeReason))
		{
			return (Outcome(candidate, ExternalPackageAdoptionStatus.Skipped, Array.Empty<ModNodeId>(), writeReason), current);
		}

		var startedUtc = marker?.StartedUtc ?? DateTimeOffset.UtcNow;
		WriteMarker(candidate.FullPath, AdoptionStatus.Importing, Array.Empty<ModNodeId>(), startedUtc);
		// 逐单元记录已落库的节点：多单元导入是逐单元提交的，第二个单元失败时第一个已经在库里。
		// 标记里带上这些 id，下一轮才知道「这次导入其实留下了一半」，才能先回滚再重试。
		var committed = new List<ModNodeId>();
		try
		{
			var preferHardLinks = IsSameVolume(candidate.FullPath, _paths.ModsDirectory);
			var import = await _importer
				.ImportLibraryResidentFolderAsync(candidate.FullPath, preferHardLinks, unitNodeIds =>
				{
					committed.AddRange(unitNodeIds);
					WriteMarker(candidate.FullPath, AdoptionStatus.Importing, committed, startedUtc);
				}, cancellationToken)
				.ConfigureAwait(false);
			var nodeIds = import.ImportedNodeIds;
			WriteMarker(candidate.FullPath, AdoptionStatus.Imported, nodeIds, startedUtc);
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

			var moveFailure = TryMoveToArchive(candidate.FullPath, out var movedTo);
			var detail = moveFailure is null
				? (preferHardLinks ? "已导入（硬链接）。" : "已导入（复制）。")
				: $"已导入，但源目录移动失败：{moveFailure}（标记已记为「待移动」，下次认领只补移动，不会重新导入）";
			// 标记写在**移动后的位置**：源目录已经不在了，往原路径写只会静默失败，
			// 结果副本里留着的是 "imported"——用户把它还原回库根时就会被当成"待移动"再搬一次。
			if (movedTo is not null)
				WriteMarker(movedTo, AdoptionStatus.Moved, nodeIds, startedUtc);
			if (defaultFailure is not null)
				detail += $" 选项默认态初始化失败：{defaultFailure}";
			// 作者漏写 manifest 是常见的；内容已经收进来了，但用户该知道"这个选项不是作者声明的"。
			if (import.UnownedPayloadPaths.Count > 0)
				detail += $" manifest 未声明的目录已按实际内容收为选项：{string.Join("、", import.UnownedPayloadPaths)}。";
			// 候选阶段的提示（预览图缺失、本层与子树都有 patch…）只在这里有机会落进日志：
			// 判定是纯函数，调用方看不到 planner 的告警。
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
			var detail = await HandleImportFailureAsync(candidate, committed, exception.Message, startedUtc).ConfigureAwait(false);
			return (Outcome(candidate, ExternalPackageAdoptionStatus.Failed, Array.Empty<ModNodeId>(), detail), current);
		}
		catch (OperationCanceledException)
		{
			// 取消也要回滚：不然「用户按了取消」会留下一半内容 + 一个删掉的标记，
			// 下一轮从零重导，库里就多出一份。
			await HandleImportFailureAsync(candidate, committed, "已取消", startedUtc).ConfigureAwait(false);
			throw;
		}
	}

	/// <summary>
	/// 导入失败/取消后的收尾：已经提交的单元必须回滚，否则下一轮会重复导入。
	/// <para>
	/// 回滚失败时**保留**标记（写回 <c>importing</c> + 已提交的节点 id）：下一轮会先再回滚一次。
	/// 反过来若把标记删掉，下一轮从零重导，那批已提交的节点就成了永久重复项。
	/// </para>
	/// </summary>
	private async ValueTask<string> HandleImportFailureAsync(
		ExternalPackageAdoptionCandidate candidate,
		IReadOnlyList<ModNodeId> committed,
		string reason,
		DateTimeOffset startedUtc)
	{
		if (committed.Count == 0)
		{
			// 导入器自己已经回滚了半成品（CommitImportAsync 的 catch），这里只需保证
			// 源目录原样保留、标记不残留。
			TryDeleteMarker(candidate.FullPath);
			return reason;
		}

		var rolledBack = await TryRollbackAsync(committed).ConfigureAwait(false);
		if (rolledBack)
		{
			TryDeleteMarker(candidate.FullPath);
			return $"{reason}（已回滚这次导入落库的 {committed.Count} 个节点，源目录保留）";
		}

		WriteMarker(candidate.FullPath, AdoptionStatus.Importing, committed, startedUtc);
		return $"{reason}（这次导入已落库 {committed.Count} 个节点，回滚失败；"
			+ "标记已保留，下次认领会先回滚再重试，不会重复导入）";
	}

	/// <summary>「已导入、只差移动」的补做分支。</summary>
	private ExternalPackageAdoptionOutcome CompletePendingMove(
		ExternalPackageAdoptionCandidate candidate,
		AdoptionMarker marker)
	{
		var moveFailure = TryMoveToArchive(candidate.FullPath, out var movedTo);
		if (moveFailure is not null)
		{
			return Outcome(candidate, ExternalPackageAdoptionStatus.Failed, marker.NodeIds,
				$"已导入，但源目录移动仍然失败：{moveFailure}（下次认领继续只补移动）");
		}

		WriteMarker(movedTo!, AdoptionStatus.Moved, marker.NodeIds, marker.StartedUtc);
		return Outcome(candidate, ExternalPackageAdoptionStatus.Adopted, marker.NodeIds,
			$"补移动完成：上一轮已经导入 {marker.NodeIds.Count} 个节点，这次只把源目录搬进副本目录。");
	}

	private static bool AllStillInLibrary(AdoptionMarker marker, LibrarySnapshot current)
		=> marker.NodeIds.Count > 0 && marker.NodeIds.All(id => current.Nodes.ContainsKey(id));

	private static ExternalPackageAdoptionOutcome Outcome(
		ExternalPackageAdoptionCandidate candidate,
		ExternalPackageAdoptionStatus status,
		IReadOnlyList<ModNodeId> nodeIds,
		string? detail)
		=> new(candidate.RelativePath, status, nodeIds, detail);

	private async ValueTask<bool> TryRollbackAsync(IReadOnlyList<ModNodeId> nodeIds)
	{
		try
		{
			// 回滚不带调用方的取消令牌：取消已经发生了，回滚必须做完。
			await _importer.RollbackImportAsync(nodeIds, CancellationToken.None).ConfigureAwait(false);
			return true;
		}
		catch (Exception exception) when (exception is IOException
			or UnauthorizedAccessException
			or InvalidDataException
			or InvalidOperationException
			or NotSupportedException
			or System.Security.SecurityException)
		{
			return false;
		}
	}

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
	/// 把源目录移进 <c>&lt;库&gt;.adopted/&lt;批次&gt;/&lt;原名&gt;</c>。
	/// <para>
	/// 为什么不用 <see cref="ModStorageDeletionService"/>：那条路是「删掉受管节点」，
	/// 会登记 pending-deletion 墓碑、参与对账排除集；而认领的语义是「源目录已经不需要了，
	/// 但保留一份可还原的副本」，登记墓碑反而会让下一轮对账把新节点当成待删。
	/// </para>
	/// <para>
	/// <b>为什么副本不能直接删。</b><c>PersistFlattenedTree</c> 只拷顶层文件 + <c>sectional/</c>
	/// + 外部安装文件；包里没进库的东西（预览图、说明 txt、没有 base patch 的目录）只有这份
	/// 副本还在。所以这里永远是「移走」而不是「删掉」，清理由保留期负责。
	/// </para>
	/// </summary>
	/// <param name="movedTo">移动成功时源目录的新位置。标记要写在这里——源目录已经不在了。</param>
	private string? TryMoveToArchive(string directory, out string? movedTo)
	{
		movedTo = null;
		var batchDirectory = Path.Combine(
			_paths.AdoptedArchiveDirectory,
			DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
			+ "-" + Guid.NewGuid().ToString("N")[..8]);
		try
		{
			Directory.CreateDirectory(batchDirectory);
			var destination = Path.Combine(batchDirectory, Path.GetFileName(directory));
			Directory.Move(directory, destination);
			movedTo = destination;
			return null;
		}
		catch (Exception exception) when (exception is IOException
			or UnauthorizedAccessException
			or NotSupportedException
			or System.Security.SecurityException)
		{
			TryDeleteEmptyDirectory(batchDirectory);
			return exception.Message;
		}
	}

	/// <summary>
	/// 清掉超过保留期的批次目录。只认自己建的那种名字（<c>yyyyMMdd-HHmmss-xxxxxxxx</c>），
	/// 用户往这个目录里放别的东西时不会被误删。
	/// </summary>
	private IReadOnlyList<string> PruneArchive()
	{
		var archive = _paths.AdoptedArchiveDirectory;
		if (!Directory.Exists(archive)) return Array.Empty<string>();

		var cutoff = (DateTimeOffset.UtcNow - ArchiveRetention).UtcDateTime;
		var removed = 0;
		var freed = 0L;
		try
		{
			foreach (var entry in Directory.EnumerateDirectories(archive))
			{
				try
				{
					if (!IsArchiveBatchName(Path.GetFileName(entry))) continue;
					if (Directory.GetLastWriteTimeUtc(entry) > cutoff) continue;
					var size = TryMeasure(entry);
					Directory.Delete(entry, recursive: true);
					removed++;
					freed += size;
				}
				catch (IOException) { }
				catch (UnauthorizedAccessException) { }
			}
		}
		catch (IOException) { return Array.Empty<string>(); }
		catch (UnauthorizedAccessException) { return Array.Empty<string>(); }

		if (removed == 0) return Array.Empty<string>();
		return [$"清理过期认领副本：删除 {removed} 个批次（{freed / (1024.0 * 1024.0):F1} MB），"
			+ $"保留期 {ArchiveRetention.TotalDays:F0} 天。"];
	}

	private static bool IsArchiveBatchName(string name)
		=> name.Length == 24
			&& name[8] == '-' && name[15] == '-'
			&& name[..8].All(char.IsAsciiDigit)
			&& name[9..15].All(char.IsAsciiDigit)
			&& name[16..].All(Uri.IsHexDigit);

	private static long TryMeasure(string directory)
	{
		try
		{
			var total = 0L;
			foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
			{
				try { total += new FileInfo(path).Length; }
				catch (IOException) { }
			}
			return total;
		}
		catch (IOException) { return 0L; }
		catch (UnauthorizedAccessException) { return 0L; }
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
			var nodeIds = (document.NodeIds ?? Array.Empty<string>())
				.Select(value => Guid.TryParse(value, out var parsed) ? new ModNodeId(parsed) : (ModNodeId?)null)
				.Where(id => id.HasValue)
				.Select(id => id!.Value)
				.ToArray();
			return new AdoptionMarker(ParseStatus(document.Status), nodeIds, document.StartedUtc ?? DateTimeOffset.UtcNow);
		}
		catch (JsonException) { return null; }
		catch (IOException) { return null; }
		catch (UnauthorizedAccessException) { return null; }
	}

	private static AdoptionStatus ParseStatus(string? status)
		=> status?.ToLowerInvariant() switch
		{
			"moved" => AdoptionStatus.Moved,
			// 旧版本写的 "imported" 就是「已导入、还没移走」，语义正好对上，不需要迁移。
			"imported" => AdoptionStatus.Imported,
			_ => AdoptionStatus.Importing,
		};

	private static string SerializeStatus(AdoptionStatus status)
		=> status switch
		{
			AdoptionStatus.Moved => "moved",
			AdoptionStatus.Imported => "imported",
			_ => "importing",
		};

	private static void WriteMarker(
		string directory,
		AdoptionStatus status,
		IReadOnlyList<ModNodeId> nodeIds,
		DateTimeOffset startedUtc)
	{
		var path = Path.Combine(directory, MarkerFileName);
		var document = new AdoptionMarkerDocument(
			MarkerSchema,
			SerializeStatus(status),
			nodeIds.Select(id => id.Value.ToString("N")).ToArray(),
			startedUtc,
			DateTimeOffset.UtcNow);
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

	/// <summary>
	/// <see cref="Importing"/>：已开始，<see cref="AdoptionMarker.NodeIds"/> 是**已经落库**的部分。
	/// <see cref="Imported"/>：全部导入完成，只差把源目录搬走。
	/// <see cref="Moved"/>：连搬也做完了；目录还出现在库根上只可能是用户还原的。
	/// </summary>
	private enum AdoptionStatus { Importing, Imported, Moved }

	private sealed record AdoptionMarker(AdoptionStatus Status, IReadOnlyList<ModNodeId> NodeIds, DateTimeOffset StartedUtc);

	private sealed record AdoptionMarkerDocument(
		string Schema,
		string Status,
		IReadOnlyList<string>? NodeIds,
		DateTimeOffset? StartedUtc,
		DateTimeOffset? UpdatedUtc = null);
}
