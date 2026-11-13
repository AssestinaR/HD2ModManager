using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure.KitInstance;

/// <summary>预览：每个候选套的规划结果（不能转的带原因）。<see cref="Unavailable"/> 非空表示连候选都没法算（没有游戏数据索引等）。</summary>
public sealed record KitConversionPreview(IReadOnlyList<KitConversionCandidate> Candidates, string? Unavailable);

public sealed record KitConversionApplyResult(bool Succeeded, string Message, KitConversionPlan? Plan, IReadOnlyList<string> WrittenFiles);

/// <summary>
/// 转专属 / 撤销专属的编排（规划 §6.1）。界面只和这个类说话：
/// <see cref="PreviewAsync"/> → 用户选目标套并看预览 → <see cref="ApplyAsync"/>；以后 <see cref="UndoAsync"/>。
/// <para>
/// 顺序约定：<b>先写派生 patch，再写 workspace 记录</b>；记录写失败就把刚写的派生 patch 删掉。
/// 撤销反过来，<b>先删记录、再删派生 patch</b>：中间任何时刻，「隐藏规则存在」都意味着派生 patch 还在，
/// 不会出现「原 Mod 的全局污染 + 隐藏规则」这种两头都坏的状态。
/// </para>
/// </summary>
public sealed class KitConversionService
{
	internal static readonly SemaphoreSlim WorkspaceLock = new(1, 1);
	private readonly KitPollutionService _pollution;

	public KitConversionService(KitPollutionService pollution)
		=> _pollution = pollution ?? throw new ArgumentNullException(nameof(pollution));

	public static string RuleNameFor(ModNodeId nodeId, uint targetItemId)
		=> $"kit-{targetItemId}-{nodeId.Value.ToString("N")[..8]}";

	public async Task<KitConversionPreview> PreviewAsync(
		ModNodeId nodeId, string nodeDirectory, string? gameDataDirectory, CancellationToken cancellationToken = default)
	{
		KitModContentScan scan;
		try { scan = await KitModContentScanner.ScanAsync(nodeDirectory, cancellationToken).ConfigureAwait(false); }
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
		{
			return new KitConversionPreview([], exception.Message);
		}

		var context = await _pollution.LoadConversionContextAsync(gameDataDirectory, cancellationToken).ConfigureAwait(false);
		if (context is null) return new KitConversionPreview([], "没有可用的游戏数据索引，请先在设置页重建资产索引。");
		var candidates = KitConversionPlanner.PlanCandidates(
			item => RuleNameFor(nodeId, item), scan.Content, context.IsVanilla, context.Pieces, context.KitNames, context.UnitsByMaterial);
		if (candidates.Count == 0) return new KitConversionPreview([], "这个 Mod 没有覆盖任何护甲的原版部件。");
		return new KitConversionPreview(candidates, null);
	}

	public async Task<KitConversionApplyResult> ApplyAsync(
		ModNodeId nodeId,
		string modsRootDirectory,
		string nodeDirectory,
		uint targetItemId,
		string? gameDataDirectory,
		CancellationToken cancellationToken = default)
	{
		await WorkspaceLock.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			KitInstanceWorkspace workspace;
			try { workspace = await KitInstanceWorkspaceStore.ReadAsync(modsRootDirectory, cancellationToken).ConfigureAwait(false); }
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
			{
				return Fail("无法读取「按实例隐藏」记录：" + exception.Message);
			}

			if (workspace.Records.ContainsKey(nodeId))
				return Fail("这个 Mod 已经转过专属了，请先撤销专属。");

			KitModContentScan scan;
			try { scan = await KitModContentScanner.ScanAsync(nodeDirectory, cancellationToken).ConfigureAwait(false); }
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
			{
				return Fail(exception.Message);
			}

			var context = await _pollution.LoadConversionContextAsync(gameDataDirectory, cancellationToken).ConfigureAwait(false);
			if (context is null) return Fail("没有可用的游戏数据索引，请先在设置页重建资产索引。");
			var plan = KitConversionPlanner.Plan(
				targetItemId, RuleNameFor(nodeId, targetItemId), scan.Content, context.IsVanilla, context.Pieces, context.KitNames, context.UnitsByMaterial);
			if (!plan.Succeeded) return new KitConversionApplyResult(false, plan.FailureReason ?? "转换失败。", plan, []);
			var ruleCandidate = new KitInstanceRule(plan.RuleName, plan.Radius, plan.All, plan.None, plan.Hide.Select(entry => entry.Unit.ToString("x16", System.Globalization.CultureInfo.InvariantCulture)).ToArray());
			if (!KitInstanceRulesProtocol.TryValidate(ruleCandidate, out var ruleError)) return Fail("生成的规则不合法：" + ruleError);

			IReadOnlyList<string> written;
			try
			{
				written = await KitDerivedPatchWriter.WriteAsync(nodeDirectory, scan, plan, cancellationToken).ConfigureAwait(false);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
			{
				return new KitConversionApplyResult(false, "写派生 patch 失败：" + exception.Message, plan, []);
			}

			var record = new KitInstanceWorkspaceRecord(
				targetItemId,
				plan.RuleName,
				plan.Radius,
				plan.All,
				plan.None,
				ruleCandidate.Hide,
				KitInstanceContentFingerprint.Compute(nodeDirectory),
				plan.TargetName,
				written);
			try
			{
				await KitInstanceWorkspaceStore.WriteAsync(modsRootDirectory, workspace.With(nodeId, record), cancellationToken).ConfigureAwait(false);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				// 记录没写成：把刚写的派生 patch 删掉，不留半份。
				try { KitDerivedPatchWriter.Remove(nodeDirectory, written); }
				catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { /* 报告里说明 */ }
				return new KitConversionApplyResult(false, "写 workspace 记录失败，已撤回派生 patch：" + exception.Message, plan, []);
			}

			return new KitConversionApplyResult(true, "已转专属，下次部署后生效。", plan, written);
		}
		finally
		{
			WorkspaceLock.Release();
		}

		static KitConversionApplyResult Fail(string message) => new(false, message, null, []);
	}

	/// <summary>撤销专属：删掉记录与本功能写出的派生 patch，只删自己写的东西。</summary>
	public static async Task<KitConversionApplyResult> UndoAsync(
		ModNodeId nodeId, string modsRootDirectory, string nodeDirectory, CancellationToken cancellationToken = default)
	{
		await WorkspaceLock.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			KitInstanceWorkspace workspace;
			try { workspace = await KitInstanceWorkspaceStore.ReadAsync(modsRootDirectory, cancellationToken).ConfigureAwait(false); }
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
			{
				return new KitConversionApplyResult(false, "无法读取「按实例隐藏」记录：" + exception.Message, null, []);
			}

			if (!workspace.Records.TryGetValue(nodeId, out var record))
				return new KitConversionApplyResult(false, "这个 Mod 没有转专属记录。", null, []);

			try
			{
				await KitInstanceWorkspaceStore.WriteAsync(modsRootDirectory, workspace.Without(nodeId), cancellationToken).ConfigureAwait(false);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				return new KitConversionApplyResult(false, "写 workspace 记录失败，未做任何改动：" + exception.Message, null, []);
			}

			IReadOnlyList<string> removed = [];
			string? leftover = null;
			try { removed = KitDerivedPatchWriter.Remove(nodeDirectory, record.DerivedFiles ?? []); }
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				leftover = exception.Message;
			}

			return leftover is null
				? new KitConversionApplyResult(true, "已撤销专属，下次部署后回到 Mod 原样。", null, removed)
				: new KitConversionApplyResult(true, "已撤销专属记录，但删除派生 patch 时出错（Overwrite 目录里的文件需要手动清理）：" + leftover, null, removed);
		}
		finally
		{
			WorkspaceLock.Release();
		}
	}

	public static async Task<KitInstanceWorkspaceRecord?> TryGetRecordAsync(
		ModNodeId nodeId, string modsRootDirectory, CancellationToken cancellationToken = default)
	{
		try
		{
			var workspace = await KitInstanceWorkspaceStore.ReadAsync(modsRootDirectory, cancellationToken).ConfigureAwait(false);
			return workspace.Records.GetValueOrDefault(nodeId);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
		{
			return null;
		}
	}
}
