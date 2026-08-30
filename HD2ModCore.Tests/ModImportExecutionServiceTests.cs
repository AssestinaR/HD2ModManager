using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

public sealed class ModImportExecutionServiceTests
{
	[Fact]
	public async Task ImportFolderAsync_RegistersManifestTree_AndInitializesDefaultOptions()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		try
		{
			var source = Path.Combine(root, "source", "Bundle");
			Directory.CreateDirectory(source);
			await File.WriteAllTextAsync(Path.Combine(source, "manifest.json"), """
{
  "name": "CLI Manifest Root",
  "options": [
    { "name": "First", "include": [ "first" ] },
    { "name": "Second", "include": [ "second" ] }
  ]
}
""");
			await WritePatchAsync(Path.Combine(source, "first", "9ba626afa44a3aa3.patch_1"));
			await WritePatchAsync(Path.Combine(source, "second", "9ba626afa44a3aa3.patch_2"));
			var paths = new StoragePaths(Path.Combine(root, "manager"), Path.Combine(root, "library"));
			var service = CoreServices.CreateModImportExecutionService(paths);

			var result = await service.ImportFolderAsync(source);

			Assert.Equal(3, result.AddedNodeIds.Count);
			Assert.Equal(2, result.InitializedOptionActivationCount);
			var rootNode = Assert.Single(result.Snapshot.Nodes.Values, node => node.Metadata.Kind == ModNodeKind.Standard);
			var options = result.Snapshot.Nodes.Values.Where(node => node.Metadata.Kind == ModNodeKind.Option).OrderBy(node => node.Metadata.OptionOrder).ToArray();
			Assert.Equal(2, options.Length);
			var optionStates = await OptionActivationStore.TryLoadReadOnlySnapshotAsync(Path.Combine(paths.ModsDirectory, "option-activations.json"));
			Assert.All(options, option => Assert.True(optionStates.IsEnabled(option.Id.Value.ToString("N"), rootNode.Id.Value.ToString("N"))));
			Assert.True(File.Exists(Path.Combine(paths.ModsDirectory, "library.json")));
		}
		finally
		{
			ClearReadOnlyFiles(root);
			try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
		}
	}

	[Fact]
	public async Task ImportFolderAsync_WithConflictingManifestIncludes_SuppressesOnlySharedPayloadDefaults()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		try
		{
			var source = Path.Combine(root, "source", "ConflictBundle");
			Directory.CreateDirectory(source);
			await File.WriteAllTextAsync(Path.Combine(source, "manifest.json"), """
{
  "name": "Conflict Root",
	  "options": [
	    { "name": "Choice A", "include": [ "shared" ] },
	    { "name": "Choice B", "include": [ "shared" ] },
	    { "name": "Independent", "include": [ "independent" ] }
	  ]
}
""");
			await WritePatchAsync(Path.Combine(source, "shared", "9ba626afa44a3aa3.patch_4"));
			await WritePatchAsync(Path.Combine(source, "independent", "9ba626afa44a3aa4.patch_4"));
			var paths = new StoragePaths(Path.Combine(root, "manager"), Path.Combine(root, "library"));

			var result = await CoreServices.CreateModImportExecutionService(paths).ImportFolderAsync(source);

			Assert.True(result.HasOptionPathConflicts);
			Assert.Equal(5, result.InitializedOptionActivationCount);
			var rootNode = Assert.Single(result.Snapshot.Nodes.Values, node => node.Metadata.Kind == ModNodeKind.Standard);
			var choiceA = Assert.Single(result.Snapshot.Nodes.Values, node => node.Metadata.Name == "Choice A");
			var choiceB = Assert.Single(result.Snapshot.Nodes.Values, node => node.Metadata.Name == "Choice B");
			var independent = Assert.Single(result.Snapshot.Nodes.Values, node => node.Metadata.Name == "Independent");
			var physical = Assert.Single(result.Snapshot.Nodes.Values, node =>
				node.Metadata.Kind == ModNodeKind.Option
				&& node.Metadata.SourcePackagePath == "shared"
				&& node.PatchGroups.Count > 0);
			Assert.Equal(2, physical.Metadata.HostModGuids!.Count);
			var optionStates = await OptionActivationStore.TryLoadReadOnlySnapshotAsync(Path.Combine(paths.ModsDirectory, "option-activations.json"));
			Assert.True(optionStates.IsEnabled(choiceA.Id.Value.ToString("N"), rootNode.Id.Value.ToString("N")));
			Assert.True(optionStates.IsEnabled(choiceB.Id.Value.ToString("N"), rootNode.Id.Value.ToString("N")));
			Assert.True(optionStates.IsEnabled(independent.Id.Value.ToString("N"), rootNode.Id.Value.ToString("N")));
			Assert.False(optionStates.IsEnabled(physical.Id.Value.ToString("N"), choiceA.Id.Value.ToString("N")));
			Assert.False(optionStates.IsEnabled(physical.Id.Value.ToString("N"), choiceB.Id.Value.ToString("N")));
		}
		finally
		{
			ClearReadOnlyFiles(root);
			try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
		}
	}

	[Fact]
	public async Task ImportFolderAsync_WithLegacyLibraryAndFailedImport_DoesNotMigrateLegacyDataIntoNewLibrary()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		try
		{
			var managerRoot = Path.Combine(root, "manager");
			var libraryRoot = Path.Combine(root, "new-library");
			var paths = new StoragePaths(managerRoot, libraryRoot);
			var legacyPath = Path.Combine(paths.DataDirectory, "library", "library.json");
			Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
			await File.WriteAllTextAsync(legacyPath, """
			{
			  "version": 1,
			  "savedUtc": "2026-08-30T00:00:00+00:00",
			  "nodes": {},
			  "profiles": []
			}
			""");
			var missingSource = Path.Combine(root, "missing-source");

			var service = CoreServices.CreateModImportExecutionService(paths);

			await Assert.ThrowsAsync<DirectoryNotFoundException>(async () => await service.ImportFolderAsync(missingSource).AsTask());

			Assert.True(File.Exists(legacyPath));
			Assert.False(File.Exists(paths.LibraryPath));
			Assert.False(File.Exists(paths.ProfilesPath));
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
