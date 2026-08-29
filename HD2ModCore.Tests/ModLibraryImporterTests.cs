using HD2ModCore.Infrastructure;
using HD2ModAdaptation.Analysis;
using HD2ModCore.Application;
using HD2ModCore.Domain;

namespace HD2ModCore.Tests;

// 作用：验证导入器会把复杂目录拆成扁平 mod，并归一化 patch 编号。
// Purpose: Verifies importer splits complex directories into flat mods and normalizes patch numbering.
public sealed class ModLibraryImporterTests
{
	[Fact]
	public async Task ImportFolderAsync_SplitsPatchDirectories_AndNormalizesPatchNames()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		var appRoot = Path.Combine(root, "app");
		var src = Path.Combine(root, "src");
		Directory.CreateDirectory(appRoot);
		Directory.CreateDirectory(src);

		try
		{
			Directory.CreateDirectory(Path.Combine(src, "variantA"));
			Directory.CreateDirectory(Path.Combine(src, "variantB"));
			File.WriteAllText(Path.Combine(src, "variantA", "9ba626afa44a3aa3.patch_5"), "A");
			File.WriteAllText(Path.Combine(src, "variantA", "9ba626afa44a3aa3.patch_5.stream"), "AS");
			File.WriteAllText(Path.Combine(src, "variantB", "9ba626afa44a3aa3.patch_9"), "B");

			var paths = new StoragePaths(appRoot);
			var importer = new ModLibraryImporter(
				paths,
				new ObjectTreeImporter(new PatchFileNameParser()),
				new ArchiveObjectTreeImporter(new ObjectTreeImporter(new PatchFileNameParser())),
				new JsonModLibraryStore(paths));

			var result = await importer.ImportFolderAsync(src);

			Assert.True(Directory.Exists(paths.ModsDirectory));
			Assert.True(File.Exists(Path.Combine(paths.ModsDirectory, "library.json")));
            Assert.Equal(3, result.Snapshot.Nodes.Count);
            var patchNodes = result.Snapshot.Nodes.Values.Where(node => node.PatchGroups.Count != 0).ToArray();
            Assert.Equal(2, patchNodes.Length);
            Assert.Single(result.Snapshot.Nodes.Values, node => node.PatchGroups.Count == 0);

            foreach (var node in patchNodes)
			{
				var storedDir = Path.Combine(appRoot, "mods", node.RelativePath);
				Assert.True(Directory.Exists(storedDir));
				Assert.Single(node.RelativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
				Assert.True(File.Exists(Path.Combine(storedDir, "9ba626afa44a3aa3.patch_0")));
				Assert.DoesNotContain(Directory.EnumerateFiles(storedDir), p => Path.GetFileName(p).Contains("patch_5") || Path.GetFileName(p).Contains("patch_9"));
			}
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}

	[Fact]
	public async Task ImportFolderAsync_CommitsFiles_WithoutWaitingForStableFacts()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		var appRoot = Path.Combine(root, "app");
		var source = Path.Combine(root, "source");
		Directory.CreateDirectory(appRoot);
		Directory.CreateDirectory(source);
		File.WriteAllText(Path.Combine(source, "9ba626afa44a3aa3.patch_0"), "patch");
		try
		{
			var paths = new StoragePaths(appRoot);
			var importer = new ModLibraryImporter(
				paths,
				new ObjectTreeImporter(new PatchFileNameParser()),
				new ArchiveObjectTreeImporter(new ObjectTreeImporter(new PatchFileNameParser())),
				new JsonModLibraryStore(paths));

			var result = await importer.ImportFolderAsync(source);

			Assert.True(File.Exists(paths.LibraryPath));
			Assert.Single(result.Snapshot.Nodes);
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}

	[Fact]
	public async Task ImportFolderAsync_ImportsDecorationPackageAtLibraryRoot()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		var appRoot = Path.Combine(root, "app");
		var source = Path.Combine(root, "decoration");
		Directory.CreateDirectory(appRoot);
		Directory.CreateDirectory(source);
		File.WriteAllText(Path.Combine(source, "decoration.json"), "{}");
		File.WriteAllBytes(Path.Combine(source, "stocky.bin"), [1, 2, 3]);
		try
		{
			var paths = new StoragePaths(appRoot);
			var importer = new ModLibraryImporter(
				paths,
				new ObjectTreeImporter(new PatchFileNameParser()),
				new ArchiveObjectTreeImporter(new ObjectTreeImporter(new PatchFileNameParser())),
				new JsonModLibraryStore(paths));

			var result = await importer.ImportFolderAsync(source);

			var node = Assert.Single(result.Snapshot.Nodes.Values);
			Assert.Equal(ModNodeKind.Decoration, node.Metadata.Kind);
			Assert.Empty(node.PatchGroups);
			Assert.Single(node.RelativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
			Assert.True(File.Exists(Path.Combine(paths.ModsDirectory, node.RelativePath, "decoration.json")));
			Assert.True(File.Exists(Path.Combine(paths.ModsDirectory, node.RelativePath, "stocky.bin")));
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}

	[Fact]
	public async Task ImportFolderAsync_RestoresExportedNodeGuid_ByRelativePath()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		var appRoot = Path.Combine(root, "app");
		var source = Path.Combine(root, "source");
		var expectedId = Guid.NewGuid();
		Directory.CreateDirectory(Path.Combine(source, "option"));
		File.WriteAllText(Path.Combine(source, "option", "9ba626afa44a3aa3.patch_0"), "patch");
		File.WriteAllText(Path.Combine(source, "manifest.json"), $$"""
		{
		  "version": 1,
		  "nodes": [
		    { "relativePath": "option", "guid": "{{expectedId:D}}" }
		  ]
		}
		""");

		try
		{
			var paths = new StoragePaths(appRoot);
			var importer = new ModLibraryImporter(paths, new ObjectTreeImporter(new PatchFileNameParser()), new ArchiveObjectTreeImporter(new ObjectTreeImporter(new PatchFileNameParser())), new JsonModLibraryStore(paths));

			var result = await importer.ImportFolderAsync(source);

			Assert.True(result.Snapshot.Nodes.ContainsKey(new ModNodeId(expectedId)));
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}

	[Fact]
	public async Task ImportFolderAsync_IgnoresCommunityRootGuid_WithoutNodeIds()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		var appRoot = Path.Combine(root, "app");
		var source = Path.Combine(root, "source");
		var communityGuid = Guid.NewGuid();
		Directory.CreateDirectory(source);
		File.WriteAllText(Path.Combine(source, "9ba626afa44a3aa3.patch_0"), "patch");
		File.WriteAllText(Path.Combine(source, "manifest.json"), $$"""
		{
		  "version": 1,
		  "guid": "{{communityGuid:D}}",
		  "options": []
		}
		""");

		try
		{
			var paths = new StoragePaths(appRoot);
			var importer = new ModLibraryImporter(paths, new ObjectTreeImporter(new PatchFileNameParser()), new ArchiveObjectTreeImporter(new ObjectTreeImporter(new PatchFileNameParser())), new JsonModLibraryStore(paths));

			var result = await importer.ImportFolderAsync(source);

			Assert.False(result.Snapshot.Nodes.ContainsKey(new ModNodeId(communityGuid)));
			Assert.Single(result.Snapshot.Nodes);
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}

}
