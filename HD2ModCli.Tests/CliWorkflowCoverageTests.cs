using System.Text.Json;
using HD2ModCli;
using Xunit;

namespace HD2ModCli.Tests;

/// <summary>
/// Keeps the Agent-facing CLI contract aligned with the desktop workflows it
/// is intended to automate. This is deliberately a capability contract rather
/// than a UI test: navigation, dialogs and file pickers remain desktop-only,
/// while every durable Mod-management operation needs an explicit CLI route.
/// </summary>
public sealed class CliWorkflowCoverageTests
{
	[Fact]
	public void CapabilityProjection_CoversPrimaryDesktopAgentWorkflows()
	{
		var capabilities = ReadCapabilityNames();

		AssertWorkflowCoverage(capabilities, "单个与批量导入",
			"import inspect", "import plan", "import validate", "import apply", "import batch plan", "import batch apply");
		AssertWorkflowCoverage(capabilities, "Mod 库、图片、元数据与安全删除",
			"mod list", "mod show", "mod image set", "mod rename", "mod delete plan", "mod delete apply");
		AssertWorkflowCoverage(capabilities, "多层 Option 启停与排序",
			"option show", "option enable", "option disable", "option move", "option insert");
		AssertWorkflowCoverage(capabilities, "Profile 管理与精确对齐",
			"profile list", "profile create", "profile move", "profile align plan", "profile align apply");
		AssertWorkflowCoverage(capabilities, "部署、停用、Lua 与 ReShade",
			"deploy plan", "deploy apply", "deploy deactivate plan", "mod external inspect");
		AssertWorkflowCoverage(capabilities, "Mod 导出与 Patch 深度分析",
			"mod export plan", "mod export apply", "mod analyze");
		AssertWorkflowCoverage(capabilities, "Patch 资源重打包",
			"patch repackage inspect", "patch repackage plan", "patch repackage apply");
		AssertWorkflowCoverage(capabilities, "Same-key 与批量 Patch 修复",
			"patch repair inspect", "patch repair apply", "patch repair batch plan", "patch repair batch apply");
		AssertWorkflowCoverage(capabilities, "跨护甲输出",
			"equipment cross-armor inspect", "equipment cross-armor plan", "equipment cross-armor apply");
		AssertWorkflowCoverage(capabilities, "装饰生成、编辑与主体 Overwrite",
			"decoration package plan", "decoration package apply", "decoration edit plan", "decoration edit apply", "host-derived plan decoration", "host-derived apply");
		AssertWorkflowCoverage(capabilities, "头盔合并输出与临时状态",
			"equipment helmet-merge inspect", "equipment helmet-merge plan", "equipment helmet-merge apply", "host-derived plan helmet");
		AssertWorkflowCoverage(capabilities, "GameData 浏览、索引与在线资产",
			"gamedata browser", "gamedata index plan", "gamedata index apply", "assets metadata plan", "assets metadata apply");
		AssertWorkflowCoverage(capabilities, "库目录与 library.json 对账",
			"library sync inspect", "library sync plan", "library sync apply");
	}

	[Fact]
	public void ExportableSkills_CoverPrimaryDesktopAgentWorkflows()
	{
		var skills = CliSkillCatalog.ListCapabilitySkills()
			.Select(skill => skill.Name)
			.ToHashSet(StringComparer.Ordinal);

		AssertWorkflowCoverage(skills, "导入", "safe-import", "batch-import");
		AssertWorkflowCoverage(skills, "库与 Option", "library-inspection", "library-synchronization", "mod-metadata", "mod-media", "option-management", "safe-library-deletion");
		AssertWorkflowCoverage(skills, "Profile 与部署", "profile-management", "profile-alignment", "safe-deployment", "lua-reshade-external-deployment");
		AssertWorkflowCoverage(skills, "导出与 Patch 分析", "mod-export", "patch-analysis", "patch-resource-repackaging", "generated-patch-integration");
		AssertWorkflowCoverage(skills, "Patch 重构", "same-key-reconstruction", "batch-patch-repair");
		AssertWorkflowCoverage(skills, "装备与装饰", "cross-armor-transfer", "decoration-package", "decoration-edit", "helmet-to-armor-merge", "host-derived-overwrite");
		AssertWorkflowCoverage(skills, "GameData 与资产元数据", "gamedata-inspection", "gamedata-index-management", "asset-metadata-management");
	}

	private static HashSet<string> ReadCapabilityNames()
	{
		using var document = JsonDocument.Parse(JsonSerializer.Serialize(CliProjections.CreateCapabilities()));
		return document.RootElement
			.GetProperty("supports")
			.EnumerateArray()
			.Select(entry => entry.GetProperty("name").GetString())
			.OfType<string>()
			.ToHashSet(StringComparer.Ordinal);
	}

	private static void AssertWorkflowCoverage(IReadOnlySet<string> actual, string workflow, params string[] expected)
	{
		var missing = expected.Where(value => !actual.Contains(value)).ToArray();
		Assert.True(missing.Length == 0, $"{workflow} 缺少 CLI 对齐项：{string.Join("、", missing)}");
	}
}
