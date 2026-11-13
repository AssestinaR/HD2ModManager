using System.Text.RegularExpressions;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure.KitInstance;

public sealed record KitInstanceCompileResult(KitInstanceRulesDocument Document, IReadOnlyList<CoreIssue> Issues);

/// <summary>
/// 把「启用中的 Mod 集合 + workspace 记录」编译成规则文档（规划 §6.3 最后一段）。
/// 停用的 Mod 的记录不参与：规则随 Mod 一起消失。
/// </summary>
public static class KitInstanceRulesCompiler
{
	/// <param name="enabledNodes">当前方案里启用的 Mod 节点。</param>
	/// <param name="previousRevision">规则文件里现有的 revision；读不到时传 0，新 revision 就从 1 开始。</param>
	/// <param name="currentFingerprint">
	/// 当前 Mod 内容的指纹。记录里的指纹非空、本委托返回非空且二者不同 ⇒ 记录失效并给 Warning
	/// （Mod 被更新过，需要重新转换）。委托为 <c>null</c> 或返回 <c>null</c> = 不校验。
	/// </param>
	public static KitInstanceCompileResult Compile(
		IEnumerable<ModNodeId> enabledNodes,
		KitInstanceWorkspace workspace,
		int previousRevision,
		Func<ModNodeId, string?>? currentFingerprint = null)
	{
		ArgumentNullException.ThrowIfNull(enabledNodes);
		ArgumentNullException.ThrowIfNull(workspace);
		var issues = new List<CoreIssue>();
		var rules = new List<KitInstanceRule>();
		var names = new HashSet<string>(StringComparer.Ordinal);

		foreach (var nodeId in enabledNodes.Distinct().OrderBy(node => node.Value))
		{
			if (!workspace.Records.TryGetValue(nodeId, out var record)) continue;

			if (!string.IsNullOrEmpty(record.ContentFingerprint))
			{
				var current = currentFingerprint?.Invoke(nodeId);
				if (current is not null && !string.Equals(current, record.ContentFingerprint, StringComparison.OrdinalIgnoreCase))
				{
					issues.Add(new CoreIssue(
						CoreIssueSeverity.Warning,
						"KitInstanceRuleStale",
						$"Mod 内容与生成「按实例隐藏」规则 {record.RuleName} 时不一致（Mod 被更新过？），这条规则本次不生效，请重新转换。",
						NodeId: nodeId));
					continue;
				}
			}

			foreach (var rule in record.ToRules())
			{
				if (!KitInstanceRulesProtocol.TryValidate(rule, out var error))
				{
					issues.Add(new CoreIssue(
						CoreIssueSeverity.Warning,
						"KitInstanceRuleInvalid",
						$"「按实例隐藏」规则不合法，已忽略：{error}",
						NodeId: nodeId));
					continue;
				}
				if (!names.Add(rule.Name))
				{
					issues.Add(new CoreIssue(
						CoreIssueSeverity.Warning,
						"KitInstanceRuleDuplicateName",
						$"「按实例隐藏」规则名 {rule.Name} 重复，后者已忽略。",
						NodeId: nodeId));
					continue;
				}
				rules.Add(rule);
			}
		}

		if (rules.Count > KitInstanceRulesProtocol.MaximumRules)
		{
			issues.Add(new CoreIssue(
				CoreIssueSeverity.Warning,
				"KitInstanceTooManyRules",
				$"启用的「按实例隐藏」规则超过 {KitInstanceRulesProtocol.MaximumRules} 条，多出的已忽略。"));
			rules.RemoveRange(KitInstanceRulesProtocol.MaximumRules, rules.Count - KitInstanceRulesProtocol.MaximumRules);
		}

		// 稳定输出：按规则名排序，同一输入永远写出同一份文本（revision 除外）。
		var ordered = rules.OrderBy(rule => rule.Name, StringComparer.Ordinal).ToArray();
		var revision = previousRevision >= int.MaxValue - 1 ? int.MaxValue - 1 : Math.Max(0, previousRevision) + 1;
		return new KitInstanceCompileResult(new KitInstanceRulesDocument(revision, ordered), issues);
	}
}

/// <summary>规则文件（<c>ArmorRegistry/kit-instance.rules</c>）的读 revision 与原子写。</summary>
public static class KitInstanceRulesFile
{
	public static string GetPath(string registryRoot)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(registryRoot);
		return Path.Combine(Path.GetFullPath(registryRoot), KitInstanceRulesProtocol.FileName);
	}

	/// <summary>现有文件里的 revision；文件不存在或读不出来返回 0（于是新 revision 从 1 开始）。</summary>
	public static int ReadRevision(string registryRoot)
	{
		var path = GetPath(registryRoot);
		string text;
		try
		{
			if (!File.Exists(path)) return 0;
			// 读不到完整解析时也尽量取回 revision，避免坏文件把它重置回 1（Hider 看到变小的 revision 没问题，
			// 但保持单调对排查更友好）。
			using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
			var buffer = new byte[KitInstanceRulesProtocol.MaximumBytes + 1];
			var read = stream.Read(buffer, 0, buffer.Length);
			text = System.Text.Encoding.UTF8.GetString(buffer, 0, read);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return 0;
		}
		if (KitInstanceRulesProtocol.TryParse(text, out var document, out _)) return document!.Revision;
		var match = Regex.Match(text, @"^\s*revision\s*=\s*(\d+)\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant);
		return match.Success && int.TryParse(match.Groups[1].Value, out var revision) ? revision : 0;
	}

	public static async ValueTask WriteAsync(
		string registryRoot, KitInstanceRulesDocument document, CancellationToken cancellationToken = default)
	{
		await KitInstanceFiles.WriteAtomicTextAsync(
			GetPath(registryRoot),
			KitInstanceRulesProtocol.Serialize(document),
			cancellationToken).ConfigureAwait(false);
	}
}
