using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using System.IO.Compression;

namespace HD2ModCore.Tests;

public sealed class ModImportPlannerParityTests
{
	[Fact]
	public async Task PlanFolderAsync_UsesTheSameHeuristicOptionTreeAsImport()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		try
		{
			var source = Path.Combine(root, "source", "WeaponBundle");
			await WritePatchAsync(Path.Combine(source, "改模", "主组", "A", "9ba626afa44a3aa3.patch_12"));
			await WritePatchAsync(Path.Combine(source, "改模", "主组", "B", "9ba626afa44a3aa3.patch_13"));

			var treeImporter = CoreServices.CreateObjectTreeImporter();
			var planner = new ModImportPlanner(treeImporter, CoreServices.CreateArchiveObjectTreeImporter());
			var plan = await planner.PlanFolderAsync(source);

			var paths = new StoragePaths(Path.Combine(root, "app"), Path.Combine(root, "library"));
			var importer = new ModLibraryImporter(
				paths,
				treeImporter,
				CoreServices.CreateArchiveObjectTreeImporter(),
				new JsonModLibraryStore(paths));
			var imported = await importer.ImportFolderAsync(source);
			var graph = OptionRelationshipGraph.Create(imported.Snapshot);

			var rootPlan = Assert.Single(plan.Items, item => item.Role == ImportPlanItemRole.PlaceholderHost);
			var logicalGroupPlan = Assert.Single(plan.Items, item => item.PlanNodeKey == "改模");
			var logicalSubgroupPlan = Assert.Single(plan.Items, item => item.PlanNodeKey == "改模/主组");
			var patchAPlan = Assert.Single(plan.Items, item => item.PlanNodeKey == "改模/主组/A");
			Assert.True(logicalGroupPlan.IsLogicalNode);
			Assert.True(logicalSubgroupPlan.IsLogicalNode);
			Assert.Equal([rootPlan.PlanNodeKey!], logicalGroupPlan.ParentPlanNodeKeys ?? []);
			Assert.Equal([logicalGroupPlan.PlanNodeKey!], logicalSubgroupPlan.ParentPlanNodeKeys ?? []);
			Assert.Equal([logicalSubgroupPlan.PlanNodeKey!], patchAPlan.ParentPlanNodeKeys ?? []);

			var importedRoot = Assert.Single(imported.Snapshot.Nodes.Values, node => node.Metadata.Kind == ModNodeKind.Standard);
			var importedGroup = Assert.Single(imported.Snapshot.Nodes.Values, node => node.Metadata.SourcePackagePath == "改模");
			var importedSubgroup = Assert.Single(imported.Snapshot.Nodes.Values, node => node.Metadata.SourcePackagePath == "改模/主组");
			var importedPatchA = Assert.Single(imported.Snapshot.Nodes.Values, node => node.Metadata.SourcePackagePath == "改模/主组/A");
			Assert.Equal(importedRoot.Id, Assert.Single(graph.GetParents(importedGroup.Id)));
			Assert.Equal(importedGroup.Id, Assert.Single(graph.GetParents(importedSubgroup.Id)));
			Assert.Equal(importedSubgroup.Id, Assert.Single(graph.GetParents(importedPatchA.Id)));
			Assert.Equal(plan.Items.Count, imported.Snapshot.Nodes.Count);
		}
		finally
		{
			ClearReadOnlyFiles(root);
			try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
		}
	}

	[Fact]
	public async Task PlanFolderAsync_UsesManifestNamesAndOptionOrderFromTheImportedTree()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		try
		{
			var source = Path.Combine(root, "source", "ManifestBundle");
			Directory.CreateDirectory(source);
			await File.WriteAllTextAsync(Path.Combine(source, "manifest.json"), """
{
  "name": "Manifest Root",
  "options": [
    { "name": "First Choice", "include": [ "first" ] },
    { "name": "Second Choice", "include": [ "second" ] }
  ]
}
""");
			await WritePatchAsync(Path.Combine(source, "first", "9ba626afa44a3aa3.patch_1"));
			await WritePatchAsync(Path.Combine(source, "second", "9ba626afa44a3aa3.patch_2"));

			var treeImporter = CoreServices.CreateObjectTreeImporter();
			var planner = new ModImportPlanner(treeImporter, CoreServices.CreateArchiveObjectTreeImporter());
			var plan = await planner.PlanFolderAsync(source);

			var paths = new StoragePaths(Path.Combine(root, "app"), Path.Combine(root, "library"));
			var importer = new ModLibraryImporter(
				paths,
				treeImporter,
				CoreServices.CreateArchiveObjectTreeImporter(),
				new JsonModLibraryStore(paths));
			var imported = await importer.ImportFolderAsync(source);

			Assert.Equal(ImportPlanMode.ManifestStrict, plan.Mode);
			Assert.Equal("Manifest Root", Assert.Single(plan.Items, item => item.PlanNodeKey == "root").DisplayName);
			Assert.Equal(0, Assert.Single(plan.Items, item => item.PlanNodeKey == "option:0").OptionOrder);
			Assert.Equal(1, Assert.Single(plan.Items, item => item.PlanNodeKey == "option:1").OptionOrder);
			Assert.Contains(imported.Snapshot.Nodes.Values, node => node.Metadata.Name == "Manifest Root");
			Assert.Contains(imported.Snapshot.Nodes.Values, node => node.Metadata.Name == "First Choice" && node.Metadata.OptionOrder == 0);
			Assert.Contains(imported.Snapshot.Nodes.Values, node => node.Metadata.Name == "Second Choice" && node.Metadata.OptionOrder == 1);
		}
		finally
		{
			ClearReadOnlyFiles(root);
			try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
		}
	}

	[Fact]
	public async Task PlanFolderAsync_ExpandsNestedArchiveUnitsLikeImport()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		try
		{
			var source = Path.Combine(root, "source", "Container");
			var archivePayload = Path.Combine(root, "archive-payload", "InnerMod");
			await WritePatchAsync(Path.Combine(archivePayload, "9ba626afa44a3aa3.patch_5"));
			Directory.CreateDirectory(source);
			var archive = Path.Combine(source, "inner.zip");
			ZipFile.CreateFromDirectory(archivePayload, archive);

			var treeImporter = CoreServices.CreateObjectTreeImporter();
			var planner = new ModImportPlanner(treeImporter, CoreServices.CreateArchiveObjectTreeImporter());
			var plan = await planner.PlanFolderAsync(source);

			var paths = new StoragePaths(Path.Combine(root, "app"), Path.Combine(root, "library"));
			var importer = new ModLibraryImporter(
				paths,
				treeImporter,
				CoreServices.CreateArchiveObjectTreeImporter(),
				new JsonModLibraryStore(paths));
			var imported = await importer.ImportFolderAsync(source);

			Assert.Equal(ImportPlanMode.NestedContainer, plan.Mode);
			var plannedHost = Assert.Single(plan.Items, item => item.Role == ImportPlanItemRole.Host);
			Assert.True(plannedHost.IsTemporarySource);
			Assert.Contains("!/inner.zip", plannedHost.SourcePath, StringComparison.OrdinalIgnoreCase);
			Assert.Single(imported.Snapshot.Nodes);
			Assert.Single(imported.Snapshot.Nodes.Values, node => node.Metadata.Kind == ModNodeKind.Standard && node.PatchGroups.Count == 1);
		}
		finally
		{
			ClearReadOnlyFiles(root);
			try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
		}
	}

	[Fact]
	public async Task PlanFolderAsync_ExposesManifestPathConflictsPerPhysicalPathAndAffectedLogicalOption()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		try
		{
			var source = Path.Combine(root, "source", "ManifestConflictBundle");
			Directory.CreateDirectory(source);
			await File.WriteAllTextAsync(Path.Combine(source, "manifest.json"), """
{
  "name": "Conflict Root",
  "options": [
    { "name": "Alpha", "include": [ "shared" ] },
    { "name": "Beta", "include": [ "shared" ] },
    { "name": "Independent", "include": [ "independent" ] }
  ]
}
""");
			await WritePatchAsync(Path.Combine(source, "shared", "nested", "9ba626afa44a3aa3.patch_1"));
			await WritePatchAsync(Path.Combine(source, "independent", "9ba626afa44a3aa4.patch_1"));

			var planner = new ModImportPlanner(CoreServices.CreateObjectTreeImporter(), CoreServices.CreateArchiveObjectTreeImporter());
			var plan = await planner.PlanFolderAsync(source);

			var conflict = Assert.Single(plan.Diagnostics, item => item.Code == "OptionPathConflict");
			Assert.Contains("shared/nested", conflict.Message, StringComparison.OrdinalIgnoreCase);
			var alpha = Assert.Single(plan.Items, item => item.DisplayName == "Alpha");
			var beta = Assert.Single(plan.Items, item => item.DisplayName == "Beta");
			var independent = Assert.Single(plan.Items, item => item.DisplayName == "Independent");
			Assert.Contains(alpha.Diagnostics ?? [], item => item.Code == "OptionPathConflict");
			Assert.Contains(beta.Diagnostics ?? [], item => item.Code == "OptionPathConflict");
			Assert.DoesNotContain(independent.Diagnostics ?? [], item => item.Code == "OptionPathConflict");
			var sharedPayload = Assert.Single(plan.Items, item =>
				item.PlannedFiles?.Contains("shared/nested", StringComparer.OrdinalIgnoreCase) == true
				&& item.ParentPlanNodeKeys?.Count == 2);
			Assert.Contains(sharedPayload.Diagnostics ?? [], item => item.Code == "SharedOptionPayload");
		}
		finally
		{
			ClearReadOnlyFiles(root);
			try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
		}
	}

	private static async Task WritePatchAsync(string path)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		await File.WriteAllBytesAsync(path, [0x00]);
	}

	private static void ClearReadOnlyFiles(string root)
	{
		if (!Directory.Exists(root)) return;
		foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
		{
			try
			{
				var attributes = File.GetAttributes(path);
				if ((attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
			}
			catch { }
		}
	}
}
