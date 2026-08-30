using System.Text.Json;
using HD2ModCli;
using Xunit;

namespace HD2ModCli.Tests;

public sealed class CliSkillExporterTests
{
	[Fact]
	public void CapabilitySkills_IncludeLibrarySynchronizationDeploymentAndPatchResourceRepackaging()
	{
		var skills = CliSkillCatalog.ListCapabilitySkills();

		Assert.Contains(skills, skill => string.Equals(skill.Name, "safe-deployment", StringComparison.Ordinal));
		Assert.Contains(skills, skill => string.Equals(skill.Name, "library-synchronization", StringComparison.Ordinal));
		Assert.Contains(skills, skill => string.Equals(skill.Name, "patch-resource-repackaging", StringComparison.Ordinal));
		Assert.Contains(skills, skill => string.Equals(skill.Name, "same-key-reconstruction", StringComparison.Ordinal));
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
