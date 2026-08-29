using System.Text.Json;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

public sealed class StandardManifestImportTests
{
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
            var node = Assert.Single(result.Snapshot.Nodes.Values, candidate => candidate.Metadata.Kind == ModNodeKind.Option);
            Assert.Equal(optionGuid, node.Id.Value);
            Assert.Equal("Option", node.Metadata.Name);
            Assert.Equal("Option note", node.Metadata.Notes);
            Assert.Empty(rootNode.PatchGroups);
            Assert.True(File.Exists(Path.Combine(paths.ModsDirectory, node.RelativePath, "icon.png")));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }
}
