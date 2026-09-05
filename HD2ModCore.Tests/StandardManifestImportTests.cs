using System.Text.Json;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

public sealed class StandardManifestImportTests
{
	[Fact]
	public async Task ImportFolderAsync_PreservesRecursiveLogicalOptions_AndCreatesEmptyParents()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		var appRoot = Path.Combine(root, "app");
		var package = Path.Combine(root, "package");
		Directory.CreateDirectory(Path.Combine(package, "simple"));
		Directory.CreateDirectory(Path.Combine(package, "enhanced"));
		Directory.CreateDirectory(appRoot);
		try
		{
			await File.WriteAllTextAsync(Path.Combine(package, "simple", "9ba626afa44a3aa3.patch_0"), "simple");
			await File.WriteAllTextAsync(Path.Combine(package, "enhanced", "9ba626afa44a3aa4.patch_0"), "enhanced");
			var manifest = new
			{
				Version = 1,
				Guid = Guid.NewGuid(),
				Name = "HUD",
				Options = new[]
				{
					new
					{
						Name = "Preset",
						SubOptions = new[]
						{
							new { Name = "Simple", Include = new[] { "simple" } },
							new { Name = "Enhanced", Include = new[] { "enhanced" } },
						},
					},
				},
			};
			await File.WriteAllTextAsync(Path.Combine(package, "manifest.json"), JsonSerializer.Serialize(manifest));

			var paths = new StoragePaths(appRoot);
			var importer = new ModLibraryImporter(
				paths,
				new ObjectTreeImporter(new PatchFileNameParser()),
				new ArchiveObjectTreeImporter(new ObjectTreeImporter(new PatchFileNameParser())),
				new JsonModLibraryStore(paths));
			var result = await importer.ImportFolderAsync(package);

			Assert.Equal(4, result.Snapshot.Nodes.Count);
			var rootNode = Assert.Single(result.Snapshot.Nodes.Values, node => node.Metadata.Kind == ModNodeKind.Standard);
			var preset = Assert.Single(result.Snapshot.Nodes.Values, node => node.Metadata.Name == "Preset");
			var simple = Assert.Single(result.Snapshot.Nodes.Values, node => node.Metadata.Name == "Simple");
			var enhanced = Assert.Single(result.Snapshot.Nodes.Values, node => node.Metadata.Name == "Enhanced");
			Assert.Empty(preset.PatchGroups);
			Assert.Single(simple.PatchGroups);
			Assert.Single(enhanced.PatchGroups);
			Assert.Equal(rootNode.Id.Value.ToString("N"), Assert.Single(preset.Metadata.HostModGuids!));
			Assert.Equal(preset.Id.Value.ToString("N"), Assert.Single(simple.Metadata.HostModGuids!));
			Assert.Equal(preset.Id.Value.ToString("N"), Assert.Single(enhanced.Metadata.HostModGuids!));
			Assert.True(File.Exists(Path.Combine(paths.ModsDirectory, preset.RelativePath, "option.json")));
		}
		finally { try { Directory.Delete(root, recursive: true); } catch { } }
	}

	[Fact]
	public async Task ImportFolderAsync_AggregatesMultipleIncludesFromNestedPatchDirectories_WithoutRestructuringOtherOptions()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		var appRoot = Path.Combine(root, "app");
		var package = Path.Combine(root, "package");
		Directory.CreateDirectory(Path.Combine(package, "part-a", "leaf-a"));
		Directory.CreateDirectory(Path.Combine(package, "part-b", "leaf-b"));
		Directory.CreateDirectory(Path.Combine(package, "other"));
		Directory.CreateDirectory(appRoot);
		try
		{
			await File.WriteAllTextAsync(Path.Combine(package, "part-a", "leaf-a", "9ba626afa44a3aa3.patch_0"), "a");
			await File.WriteAllTextAsync(Path.Combine(package, "part-b", "leaf-b", "9ba626afa44a3aa4.patch_0"), "b");
			await File.WriteAllTextAsync(Path.Combine(package, "other", "9ba626afa44a3aa5.patch_0"), "other");
			await File.WriteAllTextAsync(Path.Combine(package, "manifest.json"), JsonSerializer.Serialize(new
			{
				Version = 1,
				Name = "Combined",
				Options = new[]
				{
					new { Name = "Full", Include = new[] { "part-a", "part-b" } },
					new { Name = "Other", Include = new[] { "other" } },
				},
			}));

			var paths = new StoragePaths(appRoot);
			var importer = new ModLibraryImporter(
				paths,
				new ObjectTreeImporter(new PatchFileNameParser()),
				new ArchiveObjectTreeImporter(new ObjectTreeImporter(new PatchFileNameParser())),
				new JsonModLibraryStore(paths));
			var result = await importer.ImportFolderAsync(package);

			Assert.False(result.HasOptionPathConflicts);
			Assert.Empty(result.OptionPathConflicts);
			Assert.Equal(3, result.Snapshot.Nodes.Count);
			var full = Assert.Single(result.Snapshot.Nodes.Values, node => node.Metadata.Name == "Full");
			var other = Assert.Single(result.Snapshot.Nodes.Values, node => node.Metadata.Name == "Other");
			Assert.Equal(2, full.PatchGroups.Count);
			Assert.Single(other.PatchGroups);
			Assert.False(full.Metadata.SuppressDefaultDirectOptionActivation);
			Assert.False(other.Metadata.SuppressDefaultDirectOptionActivation);
			var stored = Path.Combine(paths.ModsDirectory, full.RelativePath);
			Assert.True(File.Exists(Path.Combine(stored, "9ba626afa44a3aa3.patch_0")));
			Assert.True(File.Exists(Path.Combine(stored, "9ba626afa44a3aa4.patch_0")));
		}
		finally { try { Directory.Delete(root, recursive: true); } catch { } }
	}

	[Fact]
	public async Task ImportFolderAsync_UsesCompatibilityModeForEveryPhysicalPayload_WhenAnyIncludeIsShared()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		var appRoot = Path.Combine(root, "app");
		var package = Path.Combine(root, "package");
		Directory.CreateDirectory(Path.Combine(package, "shared", "nested"));
		Directory.CreateDirectory(Path.Combine(package, "independent"));
		Directory.CreateDirectory(appRoot);
		try
		{
			await File.WriteAllTextAsync(Path.Combine(package, "shared", "nested", "9ba626afa44a3aa3.patch_0"), "shared");
			await File.WriteAllTextAsync(Path.Combine(package, "independent", "9ba626afa44a3aa4.patch_0"), "independent");
			await File.WriteAllTextAsync(Path.Combine(package, "manifest.json"), JsonSerializer.Serialize(new
			{
				Version = 1,
				Guid = Guid.NewGuid(),
				Name = "Conflict package",
				Options = new[]
				{
					new { Name = "Alpha", Include = new[] { "shared" } },
					new { Name = "Beta", Include = new[] { "shared" } },
					new { Name = "Independent", Include = new[] { "independent" } },
				},
			}));

			var paths = new StoragePaths(appRoot);
			var importer = new ModLibraryImporter(
				paths,
				new ObjectTreeImporter(new PatchFileNameParser()),
				new ArchiveObjectTreeImporter(new ObjectTreeImporter(new PatchFileNameParser())),
				new JsonModLibraryStore(paths));
			var result = await importer.ImportFolderAsync(package);

			Assert.True(result.HasOptionPathConflicts);
			var alpha = Assert.Single(result.Snapshot.Nodes.Values, node => node.Metadata.Name == "Alpha");
			var beta = Assert.Single(result.Snapshot.Nodes.Values, node => node.Metadata.Name == "Beta");
			var independent = Assert.Single(result.Snapshot.Nodes.Values, node => node.Metadata.Name == "Independent" && node.Metadata.SourcePackagePath == "option:2");
			var conflict = Assert.Single(result.OptionPathConflicts);
			Assert.Equal("shared/nested", conflict.SourceRelativePath);
			Assert.Equal(
				new[] { alpha.Id, beta.Id }.OrderBy(id => id.Value),
				conflict.AffectedParentOptionIds.OrderBy(id => id.Value));
			Assert.Empty(alpha.PatchGroups);
			Assert.Empty(beta.PatchGroups);
			Assert.Empty(independent.PatchGroups);
			Assert.True(alpha.Metadata.IsLogicalContainer);
			Assert.True(beta.Metadata.IsLogicalContainer);
			Assert.True(independent.Metadata.IsLogicalContainer);
			Assert.True(alpha.Metadata.SuppressDefaultDirectOptionActivation);
			Assert.True(beta.Metadata.SuppressDefaultDirectOptionActivation);
			Assert.False(independent.Metadata.SuppressDefaultDirectOptionActivation);
			Assert.True(File.Exists(ModNodeStorageProtocol.LogicalMarkerPath(Path.Combine(paths.ModsDirectory, alpha.RelativePath))));
			Assert.True(File.Exists(ModNodeStorageProtocol.LogicalMarkerPath(Path.Combine(paths.ModsDirectory, beta.RelativePath))));
			Assert.True(File.Exists(ModNodeStorageProtocol.LogicalMarkerPath(Path.Combine(paths.ModsDirectory, independent.RelativePath))));

			var sharedPhysical = Assert.Single(result.Snapshot.Nodes.Values, node =>
				node.Metadata.Kind == ModNodeKind.Option
				&& node.Metadata.SourcePackagePath == "shared/nested"
				&& node.PatchGroups.Count == 1);
			Assert.Equal(
				new[] { alpha.Id.Value.ToString("N"), beta.Id.Value.ToString("N") }.OrderBy(id => id),
				sharedPhysical.Metadata.HostModGuids!.OrderBy(id => id));
			Assert.True(File.Exists(Path.Combine(paths.ModsDirectory, sharedPhysical.RelativePath, "9ba626afa44a3aa3.patch_0")));

			var independentPhysical = Assert.Single(result.Snapshot.Nodes.Values, node =>
				node.Metadata.SourcePackagePath == "independent"
				&& node.PatchGroups.Count == 1);
			Assert.Equal(independent.Id.Value.ToString("N"), Assert.Single(independentPhysical.Metadata.HostModGuids!));
			Assert.True(File.Exists(Path.Combine(paths.ModsDirectory, independentPhysical.RelativePath, "9ba626afa44a3aa4.patch_0")));
		}
		finally { try { Directory.Delete(root, recursive: true); } catch { } }
	}

    [Fact]
    public async Task ImportFolderAsync_UsesStandardOptionMetadataAndCopiesInheritedIcon()
    {
        var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
        var appRoot = Path.Combine(root, "app");
        var package = Path.Combine(root, "package");
        Directory.CreateDirectory(Path.Combine(package, "option"));
        Directory.CreateDirectory(appRoot);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(package, "cover.png"), [1, 2, 3]);
            await File.WriteAllTextAsync(Path.Combine(package, "option", "9ba626afa44a3aa3.patch_0"), string.Empty);
            var optionGuid = Guid.NewGuid();
            var manifest = new
            {
                Version = 1,
                Guid = Guid.NewGuid(),
                Name = "Root",
                Description = "Root note",
                IconPath = "cover.png",
                Options = new[] { new { Name = "Option", Description = "Option note", Image = "cover.png", Include = new[] { "option" } } },
                Nodes = new[] { new { RelativePath = "option", Guid = optionGuid, Name = "Option", Notes = "Option note" } },
            };
            await File.WriteAllTextAsync(Path.Combine(package, "manifest.json"), JsonSerializer.Serialize(manifest));

            var paths = new StoragePaths(appRoot);
			await using var informationReader = CoreServices.CreateModInformationReader();
			await using var informationCenter = CoreServices.CreateModInformationCenter(paths, informationReader);
			var importer = CoreServices.CreateModLibraryImporter(paths, informationCenter, informationReader);
            var result = await importer.ImportFolderAsync(package);
			Assert.Equal(2, result.Snapshot.Nodes.Count);
			var rootNode = Assert.Single(result.Snapshot.Nodes.Values, candidate => candidate.Metadata.Kind == ModNodeKind.Standard);
			var node = Assert.Single(result.Snapshot.Nodes.Values, candidate => candidate.Metadata.Kind == ModNodeKind.Option && candidate.PatchGroups.Count > 0);
            Assert.Equal(optionGuid, node.Id.Value);
            Assert.Equal("Option", node.Metadata.Name);
            Assert.Equal("Option note", node.Metadata.Notes);
            Assert.Empty(rootNode.PatchGroups);
            Assert.True(File.Exists(Path.Combine(paths.ModsDirectory, node.RelativePath, "icon.png")));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }
}
