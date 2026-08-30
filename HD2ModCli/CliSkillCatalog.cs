using System.Reflection;

namespace HD2ModCli;

public sealed record CliSkillDefinition(
	string Name,
	string DirectoryName,
	string Description,
	string ResourceName,
	bool IsRouter = false,
	bool IsLegacy = false);

/// <summary>
/// Keeps every exported Agent Skill discoverable without forcing an Agent to
/// load unrelated operational guidance. Each resource is a self-contained
/// SKILL.md that can be exported on demand beside a batch script or into an
/// Agent's local skill collection.
/// </summary>
public static class CliSkillCatalog
{
	private const string RouterSkillName = "cli-router";

	private static readonly IReadOnlyList<CliSkillDefinition> Definitions =
	[
		new(RouterSkillName, "hd2mod-cli-router", "轻量路由：发现并按需导出 HD2 Mod CLI 的能力 Skill。", "HD2ModCli.Skill.cli-router", IsRouter: true),
		new("library-inspection", "hd2mod-library-inspection", "只读查看 Mod 库、Option 关系、Profile 与有效部署顺序。", "HD2ModCli.Skill.library-inspection"),
		new("library-synchronization", "hd2mod-library-synchronization", "审阅并确认库目录与 library.json 的 Patch 记录对账；不导入、删除或部署。", "HD2ModCli.Skill.library-synchronization"),
		new("decoration-package", "hd2mod-decoration-package", "审阅来源 Unit 并输出独立、可导入的装饰包；不自动导入、启用或部署。", "HD2ModCli.Skill.decoration-package"),
		new("safe-import", "hd2mod-safe-import", "以 plan → validate → apply 的显式确认流程安全导入 Mod。", "HD2ModCli.Skill.safe-import"),
		new("mod-metadata", "hd2mod-mod-metadata", "重命名与备注管理；不改变 Patch、Profile 或部署。", "HD2ModCli.Skill.mod-metadata"),
		new("option-management", "hd2mod-option-management", "按明确主体或严格校验的全部直属主体启停和排序 Option。", "HD2ModCli.Skill.option-management"),
		new("safe-library-deletion", "hd2mod-safe-library-deletion", "预览并执行关系感知的安全 Mod 删除。", "HD2ModCli.Skill.safe-library-deletion"),
		new("profile-management", "hd2mod-profile-management", "创建、管理与排序 Profile；不自动部署。", "HD2ModCli.Skill.profile-management"),
		new("safe-deployment", "hd2mod-safe-deployment", "以 plan → validate → apply 安全部署或停用已证明归属的 Patch。", "HD2ModCli.Skill.safe-deployment"),
		new("patch-resource-repackaging", "hd2mod-patch-resource-repackaging", "审阅并选择完整 AssetKey Payload，输出独立 Patch，不修改来源或部署。", "HD2ModCli.Skill.patch-resource-repackaging"),
		new("helmet-to-armor-merge", "hd2mod-helmet-to-armor-merge", "审阅完整 Armor/Any Helmet 集合并输出独立的头盔合并 Patch。", "HD2ModCli.Skill.helmet-to-armor-merge"),
		new("same-key-reconstruction", "hd2mod-same-key-reconstruction", "固定一个来源 Patch，审阅并输出 Same-key Canonical 重构候选。", "HD2ModCli.Skill.same-key-reconstruction"),
		new("mod-management", "hd2mod-mod-management", "兼容聚合 Skill：请优先使用更窄的 mod-metadata、option-management 或 safe-library-deletion。", "HD2ModCli.Skill.mod-management", IsLegacy: true),
	];

	/// <summary>
	/// Returns the actual capability Skills. The router is intentionally absent:
	/// it is a compatibility document, not a workflow capability.
	/// </summary>
	public static IReadOnlyList<CliSkillDefinition> ListCapabilitySkills()
		=> Definitions.Where(definition => !definition.IsRouter && !definition.IsLegacy).ToArray();

	public static CliSkillDefinition Resolve(string name)
	{
		var definition = Definitions.FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
		if (definition is null)
			throw new CliUserException("AgentSkillNotFound", $"未找到 Agent Skill：{name}。请运行 hd2mod agent skills list --json。");
		return definition;
	}

	public static CliSkillDefinition ResolveCapability(string name)
	{
		var definition = Resolve(name);
		if (definition.IsRouter)
			throw new CliUserException(
				"AgentSkillNotCapability",
				$"{RouterSkillName} 是兼容路由器，不是可安装能力 Skill。请导出 library-inspection 或 safe-import；如需旧式路由文件，请使用 hd2mod agent skill --output SKILL.md。");
		return definition;
	}

	public static string GetRouterContent() => GetContent(RouterSkillName);

	public static string GetContent(string name)
	{
		var definition = Resolve(name);
		var assembly = typeof(CliSkillCatalog).Assembly;
		using var stream = assembly.GetManifestResourceStream(definition.ResourceName)
			?? throw new InvalidOperationException($"The embedded HD2 Mod CLI skill resource is missing: {definition.Name}.");
		using var reader = new StreamReader(stream);
		return reader.ReadToEnd();
	}
}
