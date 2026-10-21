using HD2ModCore.Application;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

// 作用：验证 `vpk2patch/` 是节点自己的载荷（不是子选项），并且导入、部署解析、重新载入库三处都认得它。
// Purpose: Verifies `vpk2patch/` is node payload (not a child option) across import, deployment resolution and reload.
public sealed class VpkPatchPayloadTests
{
	private const string PatchName = "9ba626afa44a3aa3.patch_0";

	private static string CreateRoot()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		return root;
	}

	[Fact]
	public async Task ObjectTreeImporter_TreatsVpk2PatchAsNodePayload()
	{
		var root = CreateRoot();
		try
		{
			var source = Path.Combine(root, "Lunalice_Bill");
			Directory.CreateDirectory(Path.Combine(source, "vpk2patch"));
			File.WriteAllText(Path.Combine(source, "vpk2patch", PatchName), "toc");
			File.WriteAllText(Path.Combine(source, "vpk2patch", PatchName + ".gpu_resources"), "gpu");

			var tree = await new ObjectTreeImporter(new PatchFileNameParser()).ImportFolderAsync(source);

			var node = Assert.Single(tree.Nodes.Values);
			Assert.Equal(string.Empty, node.RelativePath);
			Assert.Single(node.PatchGroups);
			Assert.DoesNotContain(tree.Nodes.Values, candidate =>
				candidate.RelativePath.Contains("vpk2patch", StringComparison.OrdinalIgnoreCase));
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}

	[Fact]
	public async Task ModFileResolver_ResolvesVpk2PatchPayload()
	{
		var root = CreateRoot();
		try
		{
			var nodeDirectory = Path.Combine(root, "Lunalice_Bill");
			Directory.CreateDirectory(Path.Combine(nodeDirectory, "vpk2patch"));
			var expected = Path.Combine(nodeDirectory, "vpk2patch", PatchName);
			File.WriteAllText(expected, "toc");
			File.WriteAllText(expected + ".stream", "stream");
			var node = new ModNode(ModNodeId.New(), "Lunalice_Bill",
				new ModNodeMetadata("test", null, DateTimeOffset.UtcNow, null), [], []);

			var files = await new ModFileResolver(new PatchFileNameParser())
				.ResolvePatchFilesAsync(node, root);

			Assert.Equal(expected, Assert.Single(files));
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}

	[Fact]
	public async Task ModFileResolver_PrefersVpk2PatchOverSameNamedRootPatch()
	{
		var root = CreateRoot();
		try
		{
			var nodeDirectory = Path.Combine(root, "Lunalice_Bill");
			Directory.CreateDirectory(Path.Combine(nodeDirectory, "vpk2patch"));
			File.WriteAllText(Path.Combine(nodeDirectory, PatchName), "stale root copy");
			var expected = Path.Combine(nodeDirectory, "vpk2patch", PatchName);
			File.WriteAllText(expected, "generated copy");
			var node = new ModNode(ModNodeId.New(), "Lunalice_Bill",
				new ModNodeMetadata("test", null, DateTimeOffset.UtcNow, null), [], []);

			var files = await new ModFileResolver(new PatchFileNameParser())
				.ResolvePatchFilesAsync(node, root);

			Assert.Equal(expected, Assert.Single(files));
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}

	[Fact]
	public async Task ImportFolderAsync_KeepsVpk2PatchDirectory_AndReloadSeesIt()
	{
		var root = CreateRoot();
		try
		{
			var appRoot = Path.Combine(root, "app");
			Directory.CreateDirectory(appRoot);
			var source = Path.Combine(root, "Lunalice_Bill");
			Directory.CreateDirectory(Path.Combine(source, "vpk2patch"));
			File.WriteAllText(Path.Combine(source, "vpk2patch", PatchName), "toc");
			File.WriteAllText(Path.Combine(source, "vpk2patch.json"), "{}");
			var paths = new StoragePaths(appRoot);
			var importer = new ModLibraryImporter(
				paths,
				new ObjectTreeImporter(new PatchFileNameParser()),
				new ArchiveObjectTreeImporter(new ObjectTreeImporter(new PatchFileNameParser())),
				new JsonModLibraryStore(paths));

			var result = await importer.ImportFolderAsync(source);

			var node = Assert.Single(result.Snapshot.Nodes.Values);
			Assert.Single(node.PatchGroups);
			var storedDirectory = Path.Combine(paths.ModsDirectory, node.RelativePath);
			Assert.True(File.Exists(Path.Combine(storedDirectory, "vpk2patch", PatchName)));
			Assert.True(File.Exists(Path.Combine(storedDirectory, "vpk2patch.json")));

			// 重新载入库：对账只看顶层目录，但必须认得出"patch 全在 vpk2patch 里"的节点，
			// 否则下次启动这个 Mod 就会被当成空目录丢掉。
			var inspection = await new ModLibrarySynchronizer(new PatchFileNameParser())
				.InspectAsync(result.Snapshot, paths.ModsDirectory);
			var directory = Assert.Single(inspection.Directories);
			Assert.Equal(node.RelativePath.Replace('\\', '/'), directory.RelativePath);
			Assert.Single(directory.PatchGroups);
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}
}
