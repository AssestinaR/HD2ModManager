using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

// 作用：验证真实 patch 索引会按原编号排序并生成 mod 内归一化顺序。
// Purpose: Verifies the real patch index orders by source index and produces per-mod normalized order.
public sealed class PatchFileIndexBuilderTests
{
	[Fact]
	public async Task BuildAsync_AssignsNormalizedOrder_PerNodeAndHex()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		var modsRoot = Path.Combine(root, "mods");
		var modDir = Path.Combine(modsRoot, "mod");
		Directory.CreateDirectory(modDir);

		try
		{
			File.WriteAllText(Path.Combine(modDir, "9ba626afa44a3aa3.patch_4"), "");
			File.WriteAllText(Path.Combine(modDir, "9ba626afa44a3aa3.patch_9"), "");
			File.WriteAllText(Path.Combine(modDir, "9ba626afa44a3aa3.patch_9.gpu_resources"), "");

			var nodeId = ModNodeId.New();
			var node = new ModNode(nodeId, "mod", new ModNodeMetadata("mod", null, DateTimeOffset.UtcNow, null), Array.Empty<PatchGroupKey>(), Array.Empty<ModNodeId>());
			var snapshot = new LibrarySnapshot(1, DateTimeOffset.UtcNow, new Dictionary<ModNodeId, ModNode> { [nodeId] = node }, Array.Empty<Profile>());

			var index = await new PatchFileIndexBuilder(new PatchFileNameParser()).BuildAsync(snapshot, modsRoot);

			var files = index.FilesByNode[nodeId];
			Assert.Equal(3, files.Count);
			Assert.Contains(files, f => f.SourcePatchIndex == 4 && f.NormalizedOrder == 0);
			Assert.Contains(files, f => f.SourcePatchIndex == 9 && f.NormalizedOrder == 1 && f.SidecarKind == PatchSidecarKind.Base);
			Assert.Contains(files, f => f.SourcePatchIndex == 9 && f.NormalizedOrder == 1 && f.SidecarKind == PatchSidecarKind.GpuResources);
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}

	[Fact]
	public async Task BuildAsync_ScansTopLevelOnly_AndIgnoresBakDirectory()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		var modsRoot = Path.Combine(root, "mods");
		var modDir = Path.Combine(modsRoot, "mod");
		var backupDir = Path.Combine(modDir, "bak", "20260714");
		Directory.CreateDirectory(backupDir);

		try
		{
			File.WriteAllText(Path.Combine(modDir, "9ba626afa44a3aa3.patch_0"), "active");
			File.WriteAllText(Path.Combine(backupDir, "9ba626afa44a3aa3.patch_1"), "backup");
			var nodeId = ModNodeId.New();
			var node = new ModNode(nodeId, "mod", new ModNodeMetadata("mod", null, DateTimeOffset.UtcNow, null), Array.Empty<PatchGroupKey>(), Array.Empty<ModNodeId>());
			var snapshot = new LibrarySnapshot(2, DateTimeOffset.UtcNow, new Dictionary<ModNodeId, ModNode> { [nodeId] = node }, Array.Empty<Profile>());

			var index = await new PatchFileIndexBuilder(new PatchFileNameParser()).BuildAsync(snapshot, modsRoot);

			var file = Assert.Single(index.FilesByNode[nodeId]);
			Assert.Equal(0, file.SourcePatchIndex);
			Assert.Equal(Path.Combine(modDir, "9ba626afa44a3aa3.patch_0"), file.FilePath);
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}

	[Fact]
	public async Task BuildAsync_PrefersValidSameNamedOverwritePatchAndSidecar()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		var modsRoot = Path.Combine(root, "mods");
		var modDir = Path.Combine(modsRoot, "mod");
		var overwrite = Path.Combine(modDir, "Overwrite");
		Directory.CreateDirectory(overwrite);
		try
		{
			var name = "9ba626afa44a3aa3.patch_0";
			File.WriteAllText(Path.Combine(modDir, name), "root");
			File.WriteAllText(Path.Combine(modDir, name + ".gpu_resources"), "root gpu");
			await File.WriteAllBytesAsync(Path.Combine(overwrite, name), BitConverter.GetBytes(4026531857u));
			File.WriteAllText(Path.Combine(overwrite, name + ".gpu_resources"), "overwrite gpu");
			var nodeId = ModNodeId.New();
			var node = new ModNode(nodeId, "mod", new ModNodeMetadata("mod", null, DateTimeOffset.UtcNow, null), [], []);
			var snapshot = new LibrarySnapshot(1, DateTimeOffset.UtcNow, new Dictionary<ModNodeId, ModNode> { [nodeId] = node }, []);

			var files = (await new PatchFileIndexBuilder(new PatchFileNameParser()).BuildAsync(snapshot, modsRoot)).FilesByNode[nodeId];

			Assert.All(files, file => Assert.StartsWith(overwrite, file.FilePath, StringComparison.OrdinalIgnoreCase));
		}
		finally { try { Directory.Delete(root, recursive: true); } catch { } }
	}
}
