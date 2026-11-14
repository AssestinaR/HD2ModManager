using System.Globalization;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure.KitInstance;

/// <summary>
/// 专属切换（P4a）编出来的隐藏规则内容。<see cref="Hide"/> 为空时不需要规则（<see cref="NeedsRule"/> 为假）。
/// </summary>
/// <param name="All">主签名（单件）。</param>
/// <param name="ExtraAll">备用签名，各自单件；见 <see cref="KitInstanceWorkspaceRecord.ExtraAll"/>。</param>
public sealed record KitExclusiveSeed(
	uint ItemId,
	string TargetName,
	double Radius,
	IReadOnlyList<string> All,
	IReadOnlyList<string> ExtraAll,
	IReadOnlyList<string> Hide)
{
	public bool NeedsRule => All.Count > 0 && Hide.Count > 0;
}

public sealed record KitExclusiveSyncResult(bool Ok, string? Error);

/// <summary>
/// VPK 专属切换的 workspace 记录管理（规划 §4.3）。记录按 <c>source=vpk-exclusive</c> 标记，由「切换」独占管理：
/// 重新切换时替换，关掉专属或切到别处时删除；P3 的「撤销专属」不对它显示。
/// <para>
/// 与 P3 共用同一把锁与同一份文件。本类<b>绝不动</b>不是自己写的记录（P3 转专属或手写）：
/// 遇到时写入方报错，删除方跳过。
/// </para>
/// </summary>
public static class KitExclusiveWorkspace
{
	public const string SourceTag = "vpk-exclusive";

	public static bool IsExclusiveRecord(KitInstanceWorkspaceRecord? record)
		=> record is not null && string.Equals(record.Source, SourceTag, StringComparison.Ordinal);

	public static string RuleNameFor(ModNodeId nodeId, uint itemId)
		=> $"vpk-{itemId.ToString(CultureInfo.InvariantCulture)}-{nodeId.Value.ToString("N")[..8]}";

	/// <summary>单目标同步（旧入口）：<paramref name="seed"/> 非空且需要规则 → 写入或替换；否则删除自己写的记录。</summary>
	public static Task<KitExclusiveSyncResult> SyncAsync(
		ModNodeId nodeId,
		string modsRootDirectory,
		string nodeDirectory,
		KitExclusiveSeed? seed,
		CancellationToken cancellationToken = default)
		=> SyncTargetsAsync(nodeId, modsRootDirectory, nodeDirectory, seed is null ? [] : [seed], cancellationToken);

	/// <summary>
	/// 把一批目标的规则种子拼成这个节点的 workspace 记录（每个节点仍是一条记录，记录里存多个目标，每个目标展开成自己的规则）。
	/// 同一个 item_id 只留第一个；不需要规则的目标不进记录。没有任何目标需要规则时返回 <c>null</c>。
	/// 单目标时和以前写出的记录逐字段相同（<see cref="KitInstanceWorkspaceRecord.Targets"/> 为空）。
	/// </summary>
	internal static KitInstanceWorkspaceRecord? BuildRecord(ModNodeId nodeId, string nodeDirectory, IReadOnlyList<KitExclusiveSeed> seeds)
	{
		var targets = seeds
			.Where(seed => seed.NeedsRule)
			.GroupBy(seed => seed.ItemId)
			.Select(group => group.First())
			.Select(seed => new KitInstanceWorkspaceTarget(
				seed.ItemId, RuleNameFor(nodeId, seed.ItemId), seed.Radius, seed.All, [], seed.Hide, seed.TargetName,
				seed.ExtraAll.Count == 0 ? null : seed.ExtraAll))
			.ToArray();
		if (targets.Length == 0) return null;
		var first = targets[0];
		return new KitInstanceWorkspaceRecord(
			first.TargetItemId, first.RuleName, first.Radius, first.All, [], first.Hide,
			KitInstanceContentFingerprint.Compute(nodeDirectory), first.TargetName, null,
			SourceTag, first.ExtraAll, targets.Length > 1 ? targets : null);
	}

	/// <summary>
	/// 这批目标写进去之后，Hider 的规则文件会不会超限：规则条数（含别的 Mod 已有的）不超过 64、规则文件不超过 64 KiB。
	/// 超限时返回原因（用于在预览里拦下，此刻还没写任何文件）；没问题返回 <c>null</c>。
	/// </summary>
	/// <param name="others">别的节点已有的 workspace 记录（本节点自己的旧记录会被忽略）。</param>
	public static string? CheckRuleLimits(
		ModNodeId nodeId, string nodeDirectory, IReadOnlyList<KitExclusiveSeed> seeds, KitInstanceWorkspace others)
	{
		var mine = BuildRecord(nodeId, nodeDirectory, seeds)?.ToRules().ToArray() ?? [];
		var rules = new List<KitInstanceRule>(mine);
		foreach (var (key, record) in others.Records)
		{
			if (key == nodeId) continue;
			rules.AddRange(record.ToRules());
		}

		if (rules.Count > KitInstanceRulesProtocol.MaximumRules)
		{
			var foreign = rules.Count - mine.Length;
			return $"这次要写 {mine.Length} 条隐藏规则" + (foreign > 0 ? $"，加上别的 Mod 已有的 {foreign} 条共 {rules.Count} 条" : string.Empty)
				+ $"，超过 Hider 的规则上限 {KitInstanceRulesProtocol.MaximumRules} 条。请少选几套护甲。";
		}

		try
		{
			_ = KitInstanceRulesProtocol.Serialize(new KitInstanceRulesDocument(1, rules));
		}
		catch (InvalidDataException exception)
		{
			return $"隐藏规则合计超出 Hider 的限制：{exception.Message}（共 {rules.Count} 条）。请少选几套护甲。";
		}

		return null;
	}

	/// <summary>
	/// 同步这个节点的专属记录：<paramref name="seeds"/> 里有需要规则的 → 写入或替换（整条记录，含全部目标）；否则删除自己写的记录。
	/// </summary>
	public static async Task<KitExclusiveSyncResult> SyncTargetsAsync(
		ModNodeId nodeId,
		string modsRootDirectory,
		string nodeDirectory,
		IReadOnlyList<KitExclusiveSeed> seeds,
		CancellationToken cancellationToken = default)
	{
		await KitConversionService.WorkspaceLock.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			KitInstanceWorkspace workspace;
			try { workspace = await KitInstanceWorkspaceStore.ReadAsync(modsRootDirectory, cancellationToken).ConfigureAwait(false); }
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
			{
				return new KitExclusiveSyncResult(false, "无法读取「按实例隐藏」记录：" + exception.Message);
			}

			var record = BuildRecord(nodeId, nodeDirectory, seeds);
			var existing = workspace.Records.GetValueOrDefault(nodeId);
			if (existing is not null && !IsExclusiveRecord(existing))
			{
				return record is not null
					? new KitExclusiveSyncResult(false, "这个 Mod 已有一条手写或「转专属」生成的隐藏记录，专属切换不会覆盖它；请先撤销那条记录。")
					: new KitExclusiveSyncResult(true, null);
			}

			KitInstanceWorkspace next;
			if (record is not null)
			{
				foreach (var rule in record.ToRules())
				{
					if (!KitInstanceRulesProtocol.TryValidate(rule, out var ruleError))
						return new KitExclusiveSyncResult(false, "生成的规则不合法：" + ruleError);
				}

				next = workspace.With(nodeId, record);
			}
			else
			{
				if (existing is null) return new KitExclusiveSyncResult(true, null);
				next = workspace.Without(nodeId);
			}

			try
			{
				await KitInstanceWorkspaceStore.WriteAsync(modsRootDirectory, next, cancellationToken).ConfigureAwait(false);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				return new KitExclusiveSyncResult(false, "写「按实例隐藏」记录失败：" + exception.Message);
			}

			return new KitExclusiveSyncResult(true, null);
		}
		finally
		{
			KitConversionService.WorkspaceLock.Release();
		}
	}
}
