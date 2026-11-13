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

	/// <summary>
	/// 同步这个节点的专属记录：<paramref name="seed"/> 非空且需要规则 → 写入或替换；否则删除自己写的记录。
	/// </summary>
	public static async Task<KitExclusiveSyncResult> SyncAsync(
		ModNodeId nodeId,
		string modsRootDirectory,
		string nodeDirectory,
		KitExclusiveSeed? seed,
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

			var existing = workspace.Records.GetValueOrDefault(nodeId);
			if (existing is not null && !IsExclusiveRecord(existing))
			{
				return seed is { NeedsRule: true }
					? new KitExclusiveSyncResult(false, "这个 Mod 已有一条手写或「转专属」生成的隐藏记录，专属切换不会覆盖它；请先撤销那条记录。")
					: new KitExclusiveSyncResult(true, null);
			}

			KitInstanceWorkspace next;
			if (seed is { NeedsRule: true })
			{
				var ruleName = RuleNameFor(nodeId, seed.ItemId);
				var rule = new KitInstanceRule(ruleName, seed.Radius, seed.All, [], seed.Hide);
				if (!KitInstanceRulesProtocol.TryValidate(rule, out var ruleError))
					return new KitExclusiveSyncResult(false, "生成的规则不合法：" + ruleError);
				var record = new KitInstanceWorkspaceRecord(
					seed.ItemId, ruleName, seed.Radius, seed.All, [], seed.Hide,
					KitInstanceContentFingerprint.Compute(nodeDirectory), seed.TargetName, null,
					SourceTag, seed.ExtraAll.Count == 0 ? null : seed.ExtraAll);
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
