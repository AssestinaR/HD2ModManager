using System.Text.Json;
using HD2ModCli;
using Xunit;

namespace HD2ModCli.Tests;

public sealed class CliSkillExporterTests
{
	[Fact]
	public void CapabilitySkills_IncludeLibrarySynchronizationDerivedOverwriteDecorationDeploymentAndPatchResourceRepackaging()
	{
		var skills = CliSkillCatalog.ListCapabilitySkills();

		Assert.Contains(skills, skill => string.Equals(skill.Name, "safe-deployment", StringComparison.Ordinal));
		Assert.Contains(skills, skill => string.Equals(skill.Name, "library-synchronization", StringComparison.Ordinal));
		Assert.Contains(skills, skill => string.Equals(skill.Name, "decoration-package", StringComparison.Ordinal));
		Assert.Contains(skills, skill => string.Equals(skill.Name, "patch-resource-repackaging", StringComparison.Ordinal));
		Assert.Contains(skills, skill => string.Equals(skill.Name, "host-derived-overwrite", StringComparison.Ordinal));
		Assert.Contains(skills, skill => string.Equals(skill.Name, "gamedata-inspection", StringComparison.Ordinal));
		Assert.Contains(skills, skill => string.Equals(skill.Name, "gamedata-index-management", StringComparison.Ordinal));
		Assert.Contains(skills, skill => string.Equals(skill.Name, "same-key-reconstruction", StringComparison.Ordinal));
		Assert.Contains(skills, skill => string.Equals(skill.Name, "cross-armor-transfer", StringComparison.Ordinal));
		Assert.Contains(skills, skill => string.Equals(skill.Name, "profile-alignment", StringComparison.Ordinal));
		Assert.Contains(skills, skill => string.Equals(skill.Name, "patch-analysis", StringComparison.Ordinal));
		Assert.Contains(skills, skill => string.Equals(skill.Name, "generated-patch-integration", StringComparison.Ordinal));
	}

	[Fact]
	public void DecorationPackageSkill_DescribesThePlanGatedWorkflow()
	{
		var content = CliSkillCatalog.GetContent("decoration-package");

		Assert.Contains("decoration package plan", content, StringComparison.Ordinal);
		Assert.Contains("decoration package apply", content, StringComparison.Ordinal);
	}

	[Fact]
	public void LibrarySynchronizationSkill_DescribesThePlanGatedWorkflow()
	{
		var content = CliSkillCatalog.GetContent("library-synchronization");

		Assert.Contains("library sync inspect", content, StringComparison.Ordinal);
		Assert.Contains("library sync apply", content, StringComparison.Ordinal);
	}

	[Fact]
	public void PatchResourceRepackagingSkill_DescribesThePlanGatedWorkflow()
	{
		var content = CliSkillCatalog.GetContent("patch-resource-repackaging");

		Assert.Contains("patch repackage plan", content, StringComparison.Ordinal);
		Assert.Contains("patch repackage apply", content, StringComparison.Ordinal);
	}

	[Fact]
	public void SameKeyReconstructionSkill_RequiresAConcreteSourcePatch()
	{
		var content = CliSkillCatalog.GetContent("same-key-reconstruction");

		Assert.Contains("patch repair inspect", content, StringComparison.Ordinal);
		Assert.Contains("availableSourcePatchFileNames", content, StringComparison.Ordinal);
		Assert.Contains("patch repair apply", content, StringComparison.Ordinal);
	}

	[Fact]
	public void CrossArmorTransferSkill_DescribesPlanGatedUnitTransfer()
	{
		var content = CliSkillCatalog.GetContent("cross-armor-transfer");

		Assert.Contains("equipment cross-armor plan", content, StringComparison.Ordinal);
		Assert.Contains("manualMappings", content, StringComparison.Ordinal);
		Assert.Contains("equipment cross-armor apply", content, StringComparison.Ordinal);
	}

	[Fact]
	public void HostDerivedOverwriteSkill_DescribesPlanGatedStateChanges()
	{
		var content = CliSkillCatalog.GetContent("host-derived-overwrite");

		Assert.Contains("host-derived plan decoration", content, StringComparison.Ordinal);
		Assert.Contains("host-derived apply", content, StringComparison.Ordinal);
		Assert.Contains("Overwrite", content, StringComparison.Ordinal);
	}

	[Fact]
	public void ProfileAlignmentSkill_DescribesProfileOnlyPlanGatedWorkflow()
	{
		var content = CliSkillCatalog.GetContent("profile-alignment");

		Assert.Contains("profile align export", content, StringComparison.Ordinal);
		Assert.Contains("profile align apply", content, StringComparison.Ordinal);
		Assert.Contains("profiles.json", content, StringComparison.Ordinal);
	}

	[Fact]
	public void PatchAnalysisSkill_DescribesDirectNoCacheInspection()
	{
		var content = CliSkillCatalog.GetContent("patch-analysis");

		Assert.Contains("mod analyze", content, StringComparison.Ordinal);
		Assert.Contains("PatchGroupAnalyzer", content, StringComparison.Ordinal);
		Assert.Contains("mod-facts.sqlite", content, StringComparison.Ordinal);
	}

	[Fact]
	public void GeneratedPatchIntegrationSkill_ConnectsOutputToImportAlignmentAndDeployment()
	{
		var content = CliSkillCatalog.GetContent("generated-patch-integration");

		Assert.Contains("import plan", content, StringComparison.Ordinal);
		Assert.Contains("profile align", content, StringComparison.Ordinal);
		Assert.Contains("deploy plan", content, StringComparison.Ordinal);
		Assert.Contains("gamedata override show", content, StringComparison.Ordinal);
	}

	[Fact]
	public void GameDataInspectionSkill_SeparatesIndexAndOverrideFacts()
	{
		var content = CliSkillCatalog.GetContent("gamedata-inspection");

		Assert.Contains("gamedata archive list", content, StringComparison.Ordinal);
		Assert.Contains("gamedata override status", content, StringComparison.Ordinal);
		Assert.Contains("mappingTrusted", content, StringComparison.Ordinal);
	}

	[Fact]
	public void GameDataIndexManagementSkill_DescribesPlanGatedIndexWrites()
	{
		var content = CliSkillCatalog.GetContent("gamedata-index-management");

		Assert.Contains("gamedata index plan rebuild", content, StringComparison.Ordinal);
		Assert.Contains("units-all", content, StringComparison.Ordinal);
		Assert.Contains("gamedata index apply", content, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ExportSafeDeployment_WritesSkillAndManifestWithDeploymentWorkflow()
	{
		var outputDirectory = Path.Combine(Path.GetTempPath(), "hd2mod-cli-skills-" + Guid.NewGuid().ToString("N"));
		try
		{
			var export = await CliSkillExporter.ExportAsync(
				outputDirectory,
				["safe-deployment"],
				overwrite: false);

			var skill = Assert.Single(export.Skills);
			Assert.Equal("safe-deployment", skill.Name);

			var skillPath = Path.Combine(outputDirectory, skill.DirectoryName, "SKILL.md");
			Assert.True(File.Exists(skillPath));
			Assert.True(File.Exists(export.ManifestPath));

			var content = await File.ReadAllTextAsync(skillPath);
			Assert.Contains("deploy plan", content, StringComparison.Ordinal);
			Assert.Contains("deploy apply", content, StringComparison.Ordinal);

			using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(export.ManifestPath));
			var exportedSkills = manifest.RootElement.GetProperty("skills");
			Assert.Equal(JsonValueKind.Array, exportedSkills.ValueKind);
			Assert.Single(exportedSkills.EnumerateArray());
			Assert.Equal("safe-deployment", exportedSkills[0].GetProperty("name").GetString());
			Assert.Equal($"{skill.DirectoryName}/SKILL.md", exportedSkills[0].GetProperty("relativePath").GetString());
		}
		finally
		{
			if (Directory.Exists(outputDirectory))
				Directory.Delete(outputDirectory, recursive: true);
		}
	}
}
