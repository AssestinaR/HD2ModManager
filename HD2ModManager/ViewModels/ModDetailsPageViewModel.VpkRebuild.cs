using System.IO;
using System.Windows;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using HD2ModManager.Models;
using HD2ModManager.Services;

namespace HD2ModManager.ViewModels;

// 「重构建」：用 Mod 文件夹里那份来源 VPK 重跑一次转换，保住换甲目标、关掉的 section 与 unit id。
//
// 为什么做成一个按钮而不是让用户重新导入：重新导入会丢掉三样已经配好的东西
// （目标护甲、section 勾选、素体登记的 unit id），而 autor 发新版、或者我们改了转换器，
// 都是"内容该更新、配置不该丢"的场景。
public partial class ModDetailsPageViewModel
{
	/// <summary>一个可以一起重构建的选项（它自己也是 VPK 转换产物）。</summary>
	private sealed record VpkOptionTarget(string Selector, string Directory, string Name);

	private RelayCommand? _rebuildVpkCommand;

	/// <summary>
	/// 重构建。<b>不做禁用</b>（与「切换」「应用」一致）：没来源 VPK、上一个操作还在跑，
	/// 都在方法里用一句话说清楚，而不是让按钮灰着不解释自己。
	/// <para>
	/// 点了先开底栏（选范围与并发），而不是直接跑 —— 子级一多，逐个跑要等很久，
	/// 而并发直接决定内存与磁盘占用，这两件事该在开始前一起决定。
	/// </para>
	/// </summary>
	public RelayCommand RebuildVpkCommand => _rebuildVpkCommand ??= new(_ => OpenRebuildBottomBar());

	private void OpenRebuildBottomBar()
	{
		if (_vpkBusy)
		{
			VpkComponentStatus = "上一个操作还在跑，请等它结束。";
			return;
		}

		if (!TryGetCurrentNode(out var node)) return;
		var directory = Path.Combine(_library.ModsRootDirectory, node.RelativePath);
		if (VpkRebuildService.FindSourceVpkCount(directory) == 0)
		{
			VpkComponentStatus = "这个 Mod 的文件夹里没有来源 .vpk，无法重构建（把最初导入的那个 .vpk 放回根上再试）。";
			return;
		}

		if (System.Windows.Application.Current?.MainWindow?.DataContext is not ShellViewModel shell) return;
		var descendants = CollectVpkOptionTargets(node);
		var bar = new VpkRebuildBottomBarViewModel(node.Metadata.Name, descendants.Count,
			operation => RebuildVpkAsync(node, directory, operation, descendants),
			() => shell.DismissToolBottomBars());
		shell.OpenVpkRebuild(bar);
	}

	private async Task RebuildVpkAsync(
		ModNode node,
		string directory,
		VpkRebuildBottomBarViewModel options,
		IReadOnlyList<VpkOptionTarget> optionTargets)
	{
		var includeOptions = options.IncludeDescendants && optionTargets.Count != 0;
		var concurrency = options.Concurrency;

		_vpkBusy = true;

		// **过程信息走消息中心，不往卡片上刷。**
		//
		// 重构建是几分钟的长任务，管理器对这类活的统一风格是"背景任务 + 消息中心"：
		// 阶段、进度、取消、报告都在那里；卡片只留一句结论。
		var stepCount = includeOptions ? 1 + optionTargets.Count : 1;
		var completedSteps = 0;
		var task = _backgroundTasks?.Enqueue(
			BackgroundTaskKind.RebuildVpk,
			"重构建 VPK 模组",
			node.Metadata.Name,
			origin: "Mod 详情",
			userVisibleReason: "用 Mod 文件夹里那份来源 VPK 重新转换，并按当前配置重新生成换甲产物。",
			suggestedAction: "完成后重启游戏生效；细节见本次任务的报告。",
			retry: () =>
			{
				// 重试 = 重新开底栏：范围与并发让用户再定一次，而不是悄悄用上次的。
				OpenRebuildBottomBar();
				return Task.CompletedTask;
			},
			presentation: BackgroundTaskPresentation.Foreground);
		var cancellation = task?.CancellationToken ?? CancellationToken.None;

		// 运行中让底栏的「取消」也能停：底栏不知道 CancellationToken，只发事件。
		void OnCancelRequested(object? sender, EventArgs e) => task?.Cancel();
		options.CancelRequested += OnCancelRequested;

		// 没有消息中心时（设计期 / 单测直接 new 这个 VM）退回卡片，行为不至于丢。
		void Report(string stage)
		{
			if (task is not null) task.UpdateStage(stage);
			else VpkComponentStatus = stage;
			options.Status = stage;
		}

		void Step()
		{
			completedSteps++;
			var ratio = Math.Min(1d, (double)completedSteps / stepCount);
			task?.UpdateProgress(ratio);
			options.Progress = ratio;
		}

		task?.MarkRunning("正在保存当前配置");
		try
		{
			// 状态先落盘，再重跑。
			//
			// 与「应用」同一个顺序，理由也一样：重构建是从状态算出来的，状态没写就先做，
			// 崩在中间会出现"产物已改、状态还是旧的"，下一次点「应用」会把旧状态烘回去。
			try
			{
				await VpkModWardrobeService.WriteAsync(directory, _vpkFile).ConfigureAwait(true);
			}
			catch (Exception exception) when (exception is IOException or InvalidOperationException)
			{
				task?.MarkFailed("保存当前勾选失败：" + exception.Message);
				VpkComponentStatus = "保存当前勾选失败，已停止重构建：" + exception.Message;
				return;
			}

			// 目标取**下拉里当前选中**的那件（用户可能刚换过、还没点「切换」），
			// 勾选取**内存里**的状态（同理）。这样"重构建"= 把眼前这套配置整个烘一遍。
			var targets = SelectedVpkTargetArmor is { } selected
				? new[] { selected.ArchiveId }
				: ResolveApplyTargets(directory);
			Report("正在重新转换来源 VPK…");
			var outcome = await VpkRebuildService.RunAsync(
				node.Id.Value.ToString("N"),
				directory,
				targets,
				_vpkFile.State.Disabled,
				Report,
				cancellation).ConfigureAwait(true);
			Step();

			if (!outcome.Ok)
			{
				var error = outcome.Error ?? "重构建失败。";
				task?.MarkFailed(error);
				VpkComponentStatus = error;
				if (task is null) _notifications?.Show(error, NotificationLevel.Error, TimeSpan.FromSeconds(20));
				return;
			}

			var summary = $"重构建完成：{outcome.Units} 个 Unit，引用 {outcome.Materials} 种材质。"
				+ (outcome.Rekeyed
					? (string.IsNullOrWhiteSpace(outcome.TargetName)
						? "换甲产物已按当前勾选重新生成。"
						: $"换甲产物已按「{outcome.TargetName}」重新生成。")
					: string.Empty);
			var detailNotes = new List<string>(outcome.Notes);

			// 逐条重建选项。**主体失败就不往下走**：那时选项引用的是新的材质 id 还是旧的没人知道，
			// 继续重建只会把问题叠起来。
			var rebuiltNodes = new List<ModNodeId> { node.Id };
			if (includeOptions)
			{
				// 并发跑：每个选项是独立目录、独立转换进程，互不影响。
				// 并发数由用户在底栏定（默认 5、上限 12），逐个跑在选项多时要等很久。
				var succeeded = 0;
				var failed = new List<string>();
				var optionNotes = new List<string>();
				var completedOptions = 0;
				using var gate = new SemaphoreSlim(concurrency, concurrency);
				var workers = optionTargets.Select(async option =>
				{
					await gate.WaitAsync(cancellation).ConfigureAwait(true);
					try
					{
						var optionOutcome = await VpkRebuildService.RunAsync(
							option.Selector,
							option.Directory,
							targetArchiveIds: null,
							disabledSections: null,
							stage => Report($"选项 {option.Name}：{stage}"),
							cancellation).ConfigureAwait(true);
						lock (optionNotes)
						{
							Interlocked.Increment(ref completedOptions);
							if (!optionOutcome.Ok)
							{
								failed.Add($"{option.Name}：{optionOutcome.Error ?? "原因未知"}");
								return;
							}

							succeeded++;
							if (TryParseNodeId(option.Selector, out var optionNodeId)) rebuiltNodes.Add(optionNodeId);
							foreach (var note in optionOutcome.Notes) optionNotes.Add($"{option.Name}：{note}");
						}

						Step();
					}
					finally
					{
						gate.Release();
					}
				}).ToArray();
				await Task.WhenAll(workers).ConfigureAwait(true);
				detailNotes.AddRange(optionNotes);

				summary += $"选项重建 {succeeded}/{optionTargets.Count} 个。";
				if (failed.Count != 0)
				{
					summary += $"有 {failed.Count} 个失败，详情见报告。";
					detailNotes.AddRange(failed.Select(line => "选项重建失败 — " + line));
				}
			}

			summary += "重启游戏后生效。";

			// 逐条说明（备份在哪、有没有物理参数、换甲时的注意）写进报告文件，消息中心里可打开：
			// 卡片只留一句话，报告里才是能逐条核对的细节。
			var reportPath = WriteRebuildReport(node.Metadata.Name, summary, detailNotes);
			task?.SetOutputArtifacts(directory, reportPath);
			if (detailNotes.Count == 0)
			{
				task?.MarkCompleted();
			}
			else
			{
				// 有需要人看一眼的细节（备份、物理参数缺失、部分选项失败）→ 消息中心标成"注意"，
				// 而不是混在成功里被划过去。
				task?.UpdateStage($"{detailNotes.Count} 条细节见报告");
				task?.MarkCompleted();
			}

			VpkComponentStatus = "重构建完成，过程与细节见消息中心。";
			if (task is null)
			{
				_notifications?.Show(
					detailNotes.Count == 0
						? summary
						: summary + Environment.NewLine + string.Join(Environment.NewLine, detailNotes),
					detailNotes.Count == 0 ? NotificationLevel.Info : NotificationLevel.Warning,
					TimeSpan.FromSeconds(15));
			}

			foreach (var rebuilt in rebuiltNodes)
				await _library.InvalidateContentNodeAsync(rebuilt, CancellationToken.None).ConfigureAwait(true);
			await _library.RefreshCommittedContentAsync(rebuiltNodes, ModContentChangeKind.Changed,
				alreadyInvalidated: true, cancellationToken: CancellationToken.None).ConfigureAwait(true);
			// 产物换了，section 表与开关表都是新的：树必须重画。
			await LoadVpkAsync(directory, node.Metadata.Name, _detailRequestGeneration).ConfigureAwait(true);
		}
		catch (OperationCanceledException)
		{
			task?.MarkCanceled();
			VpkComponentStatus = "重构建已取消。";
		}
		catch (Exception exception) when (exception is IOException or InvalidOperationException or InvalidDataException)
		{
			task?.MarkFailed(exception.Message);
			VpkComponentStatus = "重构建失败：" + exception.Message;
		}
		finally
		{
			options.CancelRequested -= OnCancelRequested;
			_vpkBusy = false;
		}
	}

	/// <summary>
	/// 把这次重构建的逐条说明写成一份报告，交给消息中心（任务项上会出现"打开报告"）。
	/// 同名文件覆盖：一个 Mod 只留最近一次的记录，不会在临时目录里越堆越多。
	/// </summary>
	private static string? WriteRebuildReport(string? modName, string summary, IReadOnlyList<string> notes)
	{
		try
		{
			var directory = Path.Combine(Path.GetTempPath(), "hd2modmanager-rebuild");
			Directory.CreateDirectory(directory);
			var safeName = string.Concat((modName ?? "mod").Select(character =>
				Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
			var path = Path.Combine(directory, safeName + ".txt");
			var lines = new List<string>
			{
				$"重构建报告 · {modName}",
				$"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}",
				string.Empty,
				summary,
			};
			if (notes.Count != 0)
			{
				lines.Add(string.Empty);
				lines.Add("细节：");
				lines.AddRange(notes.Select(note => "· " + note));
			}

			File.WriteAllLines(path, lines);
			return path;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}

	/// <summary>
	/// 找出这个 Mod 的选项里**哪些本身也是 VPK 转换产物**（材质包、发型包…）。
	/// <para>
	/// <b>判据只看 <c>vpk2patch.json</c></b>：那是转换器写的报告，普通导入的材质包不会有它。
	/// 不能用 <see cref="VpkModWardrobeService.IsVpkMod"/>——那个要求有 <c>sections.json</c>，
	/// 也就是"带 Unit 的模型产物"，材质包一律不算，于是会被漏掉。
	/// </para>
	/// <para>认不出来的（关系表读不到、节点已不在库里）直接跳过，不阻断重构建。</para>
	/// </summary>
	private List<VpkOptionTarget> CollectVpkOptionTargets(ModNode host)
	{
		var result = new List<VpkOptionTarget>();
		IReadOnlyList<ModEntity> options;
		try
		{
			options = _library.GetOptionsForHost(host.Id.Value.ToString("N"));
		}
		catch (Exception exception) when (exception is InvalidOperationException or IOException)
		{
			return result;
		}

		foreach (var option in options)
		{
			if (!TryParseNodeId(option.Guid, out var optionId)) continue;
			if (!_library.Snapshot.Nodes.TryGetValue(optionId, out var optionNode)) continue;
			var optionDirectory = Path.Combine(_library.ModsRootDirectory, optionNode.RelativePath);
			if (VpkConversionReport.TryRead(optionDirectory) is null) continue;
			result.Add(new VpkOptionTarget(
				optionId.Value.ToString("N"),
				optionDirectory,
				optionNode.Metadata.Name ?? option.Name));
		}

		return result;
	}
}
