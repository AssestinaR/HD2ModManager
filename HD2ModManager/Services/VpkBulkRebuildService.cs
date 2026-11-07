using System.IO;
using System.Text;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModManager.Services;

/// <summary>批量重构建里的一个目标：库里的一个「VPK 类」Mod。</summary>
public sealed record VpkBulkRebuildTarget(ModNodeId NodeId, string Name, string Directory);

/// <summary>一个目标的批量重构建结果。</summary>
/// <param name="Ok">转换产物是否换成功（<c>false</c> 时 <paramref name="Error"/> 说明原因）。</param>
/// <param name="Outcome">成功时的完整回报（Unit 数、材质数、换甲与备注）；失败时可能为 <c>null</c>。</param>
public sealed record VpkBulkRebuildEntry(VpkBulkRebuildTarget Target, bool Ok, string? Error, VpkRebuildOutcome? Outcome);

public sealed class VpkBulkRebuildResult
{
	public VpkBulkRebuildResult(IReadOnlyList<VpkBulkRebuildEntry> entries, bool canceled)
	{
		Entries = entries;
		Canceled = canceled;
	}

	/// <summary>与选中的目标同序。取消后没轮到的项 <c>Ok=false</c>，且 <see cref="VpkBulkRebuildEntry.Outcome"/> 为 <c>null</c>。</summary>
	public IReadOnlyList<VpkBulkRebuildEntry> Entries { get; }
	public bool Canceled { get; }
	public int TotalCount => Entries.Count;
	public int SucceededCount => Entries.Count(entry => entry.Ok);
	public int FailedCount => Entries.Count(entry => !entry.Ok && entry.Outcome is not null);
	public int SkippedCount => Entries.Count(entry => !entry.Ok && entry.Outcome is null);

	/// <summary>成功换掉产物的那些节点 —— 调用方据此失效并重算库里的内容事实。</summary>
	public IReadOnlyList<ModNodeId> SucceededNodeIds => Entries.Where(entry => entry.Ok).Select(entry => entry.Target.NodeId).ToArray();

	public string Summary
	{
		get
		{
			var text = $"共 {TotalCount} 个：成功 {SucceededCount} 个";
			if (FailedCount != 0) text += $"，失败 {FailedCount} 个";
			if (SkippedCount != 0) text += $"，未执行 {SkippedCount} 个";
			return text + (Canceled ? "（已取消，未执行的保持原产物）" : string.Empty);
		}
	}
}

/// <summary>
/// 「一键重构所有 VPK 类模组」：把库里每一个 VPK 转换产物都按它自己记着的配置重出一遍。
/// <para>
/// <b>与详情页那个「重构建」是同一条路。</b>每个目标都调
/// <see cref="VpkRebuildService.RunAsync"/>，参数给 <c>null</c> ——
/// 也就是"按 wardrobe.json 里记着的那件护甲、那套 section 勾选"来。
/// 批量操作不该替用户改变任何 Mod 的配置，这一点和逐个点进去重建是一致的。
/// </para>
/// <para>
/// <b>为什么枚举判据是 <c>vpk2patch.json</c> 而不是 <see cref="VpkModWardrobeService.IsVpkMod"/>。</b>
/// 后者要求有 <c>sections.json</c>（带 Unit 的模型产物），材质包一律不算——而材质包<strong>也</strong>
/// 是 VPK 转换产物，同样会过期、同样需要重出一遍。用户说的"VPK 类模组"指的就是这一整类。
/// </para>
/// <para>
/// <b>并发安全性</b>：每个目标只写它自己的目录（<c>vpk2patch/</c>、<c>Overwrite/</c>、
/// <c>vpk2patch.json</c>），临时目录每次都带 GUID；CLI 对游戏资产索引是只读连接，
/// 所以同时跑几个进程不会互相踩。
/// </para>
/// </summary>
public static class VpkBulkRebuildService
{
	/// <summary>用户要的默认值。一次同时起 5 个转换进程。</summary>
	public const int DefaultConcurrency = 5;

	/// <summary>
	/// 上限。每个转换进程都要把来源 VPK、材质与贴图读进内存，实测重模型单体峰值几百 MB 到 1 GB 以上，
	/// 所以不提供"想填多少填多少"——那只会让用户在 32 GB 机器上换回一次蓝屏。
	/// </summary>
	public const int MaxConcurrency = 12;

	public const int MinConcurrency = 1;

	public static int NormalizeConcurrency(int value) => Math.Clamp(value, MinConcurrency, MaxConcurrency);

	/// <summary>
	/// 列出库里所有「VPK 类」Mod。<b>判据只看 <c>vpk2patch.json</c></b>：那是转换器写的报告，
	/// 普通导入的 Mod 不会有它。目录不存在的节点直接跳过（库索引领先于磁盘时的正常情况）。
	/// </summary>
	public static IReadOnlyList<VpkBulkRebuildTarget> EnumerateTargets(LibrarySnapshot snapshot, string? modsRootDirectory)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		if (string.IsNullOrWhiteSpace(modsRootDirectory) || !Directory.Exists(modsRootDirectory))
			return Array.Empty<VpkBulkRebuildTarget>();

		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var targets = new List<VpkBulkRebuildTarget>();
		foreach (var node in snapshot.Nodes.Values)
		{
			if (node.Metadata.IsLogicalContainer) continue;
			if (string.IsNullOrWhiteSpace(node.RelativePath)) continue;

			var directory = Path.Combine(modsRootDirectory, node.RelativePath);
			if (!seen.Add(directory)) continue;
			if (!Directory.Exists(directory)) continue;
			if (VpkConversionReport.TryRead(directory) is null) continue;

			targets.Add(new VpkBulkRebuildTarget(
				node.Id,
				string.IsNullOrWhiteSpace(node.Metadata.Name) ? node.RelativePath : node.Metadata.Name,
				directory));
		}

		return targets
			.OrderBy(target => target.Name, StringComparer.CurrentCultureIgnoreCase)
			.ThenBy(target => target.Directory, StringComparer.OrdinalIgnoreCase)
			.ToArray();
	}

	/// <param name="onItemFinished">每完成一个目标回调一次（<b>可能在多个线程上并发到达</b>，界面要自己切回 UI 线程）。</param>
	/// <param name="onStage">进行中的一句人话，用来让底栏显示"现在在做什么"。</param>
	public static async Task<VpkBulkRebuildResult> RunAsync(
		IReadOnlyList<VpkBulkRebuildTarget> targets,
		int concurrency,
		Action<VpkBulkRebuildEntry, int, int>? onItemFinished = null,
		Action<string>? onStage = null,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(targets);
		var effective = NormalizeConcurrency(concurrency);
		var entries = new System.Collections.Concurrent.ConcurrentDictionary<int, VpkBulkRebuildEntry>();
		var finished = 0;

		var batch = await ParallelBatchRunner.RunAsync(
			targets,
			effective,
			async (target, index, token) =>
			{
				VpkBulkRebuildEntry entry;
				try
				{
					onStage?.Invoke($"{target.Name}：正在重新转换…");
					// 目标与勾选都给 null ⇒ 用 wardrobe.json 里记着的那套（= 这个 Mod 上次的配置）。
					var outcome = await VpkRebuildService.RunAsync(
						target.NodeId.Value.ToString("N"),
						target.Directory,
						new VpkHostContext(SettingsService.GetGameDataFolder(), SettingsService.CreateStoragePaths()),
						targetArchiveIds: null,
						disabledSections: null,
						stage => onStage?.Invoke($"{target.Name}：{stage}"),
						token).ConfigureAwait(false);
					entry = new VpkBulkRebuildEntry(target, outcome.Ok, outcome.Error, outcome);
				}
				catch (OperationCanceledException) when (token.IsCancellationRequested)
				{
					// 让 runner 记成"未执行"：这一项的产物现在是新的还是旧的说不准，不能报成功。
					throw;
				}
				catch (Exception exception) when (exception is IOException or InvalidDataException
					or InvalidOperationException or UnauthorizedAccessException)
				{
					entry = new VpkBulkRebuildEntry(target, false, exception.Message, null);
				}

				entries[index] = entry;
			},
			result =>
			{
				if (!entries.ContainsKey(result.Index)) return;   // 取消/未开动：没有结果可报
				var done = Interlocked.Increment(ref finished);
				onItemFinished?.Invoke(entries[result.Index], done, targets.Count);
			},
			cancellationToken).ConfigureAwait(false);

		// 把 runner 的状态补进来：没跑成的项（取消、或抛了上面没 catch 的异常）也要出现在结果里，
		// 否则用户会以为"没出现就是没问题"。
		var final = new List<VpkBulkRebuildEntry>(targets.Count);
		for (var index = 0; index < targets.Count; index++)
		{
			var target = targets[index];
			if (entries.TryGetValue(index, out var entry))
			{
				final.Add(entry);
				continue;
			}

			var state = batch.Items[index];
			var reason = state.State switch
			{
				ParallelBatchItemState.Failed => state.Error ?? "重构建失败。",
				_ => "已取消：这一项没有开始，产物保持原样。",
			};
			final.Add(new VpkBulkRebuildEntry(target, false, reason, null));
		}

		return new VpkBulkRebuildResult(final, batch.Canceled);
	}

	/// <summary>
	/// 这批重构建的逐条报告。消息中心里只留一句结论，能逐条核对的东西写在这里。
	/// 同名文件覆盖（每次批量一份，按时间戳命名，不会互相盖掉）。
	/// </summary>
	public static string? WriteReport(VpkBulkRebuildResult result, int concurrency, TimeSpan elapsed)
	{
		ArgumentNullException.ThrowIfNull(result);
		try
		{
			var directory = Path.Combine(Path.GetTempPath(), "hd2modmanager-rebuild");
			Directory.CreateDirectory(directory);
			var path = Path.Combine(directory, $"批量重构-{DateTime.Now:yyyyMMdd-HHmmss}.txt");

			var lines = new List<string>
			{
				"一键重构所有 VPK 类模组 · 报告",
				$"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}",
				$"并发：{concurrency}",
				$"耗时：{elapsed.TotalMinutes:F1} 分钟",
				string.Empty,
				result.Summary,
				string.Empty,
				"逐条结果：",
			};

			foreach (var entry in result.Entries)
			{
				var state = entry.Ok ? "成功" : entry.Outcome is null ? "未执行" : "失败";
				var detail = entry.Ok
					? DescribeOutcome(entry.Outcome)
					: entry.Error;
				lines.Add($"· [{state}] {entry.Target.Name} — {detail}");
				if (entry.Ok && entry.Outcome is { } outcome)
				{
					foreach (var note in outcome.Notes) lines.Add("    - " + note);
				}
			}

			File.WriteAllLines(path, lines, new UTF8Encoding(false));
			return path;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}

	/// <summary>
	/// 一句人话说明"这次换出了什么"。
	/// <para>
	/// <b>材质包与模型产物必须分开说。</b>材质包没有 Unit，照模型那句写出来就是
	/// "0 个 Unit，引用 0 种材质"——看起来像什么都没做，其实材质已经写好了。
	/// </para>
	/// </summary>
	private static string DescribeOutcome(VpkRebuildOutcome? outcome)
	{
		if (outcome is null) return "产物已替换";
		if (outcome.Units == 0)
			return $"材质包：写出 {outcome.Materials} 个材质";

		return $"{outcome.Units} 个 Unit，引用 {outcome.Materials} 种材质"
			+ (outcome.Rekeyed ? "，换甲产物已重新生成" : string.Empty);
	}
}
