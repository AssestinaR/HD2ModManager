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
		new("decoration-edit", "hd2mod-decoration-edit", "编辑已导入 PatchSnapshot 装饰的来源 Unit 和附着策略，并安全回建已启用主体。", "HD2ModCli.Skill.decoration-edit"),
		new("mod-export", "hd2mod-mod-export", "将一个已登记 Mod 及其递归选项树安全导出为带 manifest.json 的压缩包。", "HD2ModCli.Skill.mod-export"),
		new("safe-import", "hd2mod-safe-import", "以 plan → validate → apply 安全导入 Mod；可显式在计划中追加到指定 Profile。", "HD2ModCli.Skill.safe-import"),
		new("batch-import", "hd2mod-batch-import", "以逐来源 plan → validate → apply 串行安全导入多个 Mod；已完成来源不会伪装为回滚。", "HD2ModCli.Skill.batch-import"),
		new("mod-metadata", "hd2mod-mod-metadata", "重命名与备注管理；不改变 Patch、Profile 或部署。", "HD2ModCli.Skill.mod-metadata"),
		new("mod-media", "hd2mod-mod-media", "查看、替换或清除 Mod 目录中的规范 icon.* 图像；不改变 Patch、Profile 或部署。", "HD2ModCli.Skill.mod-media"),
		new("patch-analysis", "hd2mod-patch-analysis", "直接无缓存分析 Mod 的 Patch 条目、引用和 Unit/几何摘要；不写库或 SQLite。", "HD2ModCli.Skill.patch-analysis"),
		new("option-management", "hd2mod-option-management", "创建逻辑容器，并按明确主体管理多父级 Option 的归属、启停和排序。", "HD2ModCli.Skill.option-management"),
		new("safe-library-deletion", "hd2mod-safe-library-deletion", "以冻结目录内容的 plan → validate → apply 执行关系感知的安全 Mod 删除。", "HD2ModCli.Skill.safe-library-deletion"),
		new("profile-management", "hd2mod-profile-management", "创建、管理与排序 Profile；不自动部署。", "HD2ModCli.Skill.profile-management"),
		new("profile-alignment", "hd2mod-profile-alignment", "以可编辑规格和冻结计划精确对齐一个 Profile；仅写 profiles.json。", "HD2ModCli.Skill.profile-alignment"),
		new("safe-deployment", "hd2mod-safe-deployment", "以 plan → validate → apply 安全部署或停用已证明归属的 Patch。", "HD2ModCli.Skill.safe-deployment"),
		new("lua-reshade-external-deployment", "hd2mod-lua-reshade-external-deployment", "审阅并安全部署 Lua 合成以及 ReShade add-on/预设外部文件。", "HD2ModCli.Skill.lua-reshade-external-deployment"),
		new("patch-resource-repackaging", "hd2mod-patch-resource-repackaging", "审阅并选择完整 AssetKey Payload，输出独立 Patch，不修改来源或部署。", "HD2ModCli.Skill.patch-resource-repackaging"),
		new("generated-patch-integration", "hd2mod-generated-patch-integration", "将已验证的独立 Patch 输出安全导入、精确接入 Profile、部署并检查实际覆盖赢家。", "HD2ModCli.Skill.generated-patch-integration"),
		new("helmet-to-armor-merge", "hd2mod-helmet-to-armor-merge", "审阅完整 Armor/Any Helmet 集合并输出独立的头盔合并 Patch。", "HD2ModCli.Skill.helmet-to-armor-merge"),
		new("cross-armor-transfer", "hd2mod-cross-armor-transfer", "审阅来源装备 Unit 与目标装备目录，并输出隔离的跨护甲验证候选。", "HD2ModCli.Skill.cross-armor-transfer"),
		new("host-derived-overwrite", "hd2mod-host-derived-overwrite", "安全启停装饰或临时头盔合并，并重建主体的单一管理器 Overwrite。", "HD2ModCli.Skill.host-derived-overwrite"),
		new("gamedata-inspection", "hd2mod-gamedata-inspection", "只读检查原版 Archive 索引、AssetKey 映射与当前 Data-Patch 覆盖赢家。", "HD2ModCli.Skill.gamedata-inspection"),
		new("gamedata-extraction", "hd2mod-gamedata-extraction", "不依赖资产索引或管理器安装，直读游戏包枚举并提取任意资产的原始字节。", "HD2ModCli.Skill.gamedata-extraction"),
		new("asset-metadata-management", "hd2mod-asset-metadata-management", "固定在线资产 hashlist 内容并安全提交到管理器缓存，供索引和高级 Patch 工具使用。", "HD2ModCli.Skill.asset-metadata-management"),
		new("armor-registry", "hd2mod-armor-registry", "检查并安全热更新外置护甲视觉组合注册协议；不修改加密游戏注册表。", "HD2ModCli.Skill.armor-registry"),
		new("gamedata-index-management", "hd2mod-gamedata-index-management", "计划化重建 Manager 的 GameData 基础索引或 Armor / Helmet Unit 部位事实。", "HD2ModCli.Skill.gamedata-index-management"),
		new("batch-patch-repair", "hd2mod-batch-patch-repair", "审阅并显式确认多个过时 Mod 的 Canonical 原地 Patch 修复、备份与派生 Overwrite 回建。", "HD2ModCli.Skill.batch-patch-repair"),
		new("same-key-reconstruction", "hd2mod-same-key-reconstruction", "固定一个来源 Patch，审阅并输出 Same-key Canonical 重构候选。", "HD2ModCli.Skill.same-key-reconstruction"),
		new("patch-primitives", "hd2mod-patch-primitives", "文件进/文件出的 Patch 底座：枚举、导出、合并、条目级增删改与就地隐藏 Unit，作用于任意 Patch 路径且不要求登记进库。", "HD2ModCli.Skill.patch-primitives"),
		new("equipment-facts", "hd2mod-equipment-facts", "只读读取游戏装备/披风权威表，回答哪些 AssetKey 属于护甲、头盔或披风。", "HD2ModCli.Skill.equipment-facts"),
		new("unit-inspection", "hd2mod-unit-inspection", "只读解析任意 Patch 内 Unit 的网格/流/顶点分量/几何事实/骨骼表，不要求登记进库。", "HD2ModCli.Skill.unit-inspection"),
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
		if (definition.IsLegacy)
			throw new CliUserException(
				"AgentSkillLegacyUnavailable",
				$"{definition.Name} 是已退役的兼容指针，不再作为可安装能力导出。请改为导出 mod-metadata、option-management 或 safe-library-deletion。");
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
