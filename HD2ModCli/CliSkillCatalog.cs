using System.Reflection;

namespace HD2ModCli;

public sealed record CliSkillDefinition(
	string Name,
	string DirectoryName,
	string Description,
	string ResourceName,
	bool IsRouter = false);

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
		new("safe-import", "hd2mod-safe-import", "以 plan → validate → apply 的显式确认流程安全导入 Mod。", "HD2ModCli.Skill.safe-import"),
	];

	/// <summary>
	/// Returns the actual capability Skills. The router is intentionally absent:
	/// it is a compatibility document, not a workflow capability.
	/// </summary>
	public static IReadOnlyList<CliSkillDefinition> ListCapabilitySkills()
		=> Definitions.Where(definition => !definition.IsRouter).ToArray();

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
