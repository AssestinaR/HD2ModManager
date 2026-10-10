using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

// 作用：钉住残留清理的安全判据——只有"未被库引用 + 不含任何载荷 + 只含管理器元数据"的目录才允许删除。
// Purpose: Pins the residual-cleanup safety predicate. A directory may only be deleted when it is unreferenced
// by the library snapshot, carries no patch payload and no recognized external-install file, and contains
// nothing but manager metadata. Anything payload-bearing must survive so synchronization can recover it.
public sealed class ModLibraryResidualCleanupServiceTests
{
	[Fact]
	public void Cleanup_EmptyDirectory_IsRemoved()
	{
		var root = CreateRoot();
		try
		{
			Directory.CreateDirectory(Path.Combine(root, "empty"));

			var result = Cleanup(root, SnapshotWith());

			Assert.Equal(1, result.RemovedCount);
			Assert.Empty(result.Failures);
			Assert.False(Directory.Exists(Path.Combine(root, "empty")));
		}
		finally { TryDelete(root); }
	}

	[Fact]
	public void Cleanup_ManagerMetadataOnlyDirectory_IsRemoved()
	{
		var root = CreateRoot();
		try
		{
			var directory = Path.Combine(root, "metadata-only");
			Directory.CreateDirectory(directory);
			File.WriteAllText(Path.Combine(directory, "manifest.json"), "{}");
			File.WriteAllText(Path.Combine(directory, "icon.png"), "x");
			File.WriteAllText(Path.Combine(directory, "scratch.tmp"), "x");
			File.WriteAllText(Path.Combine(directory, ModNodeStorageProtocol.OptionRelationFileName), "{}");

			var result = Cleanup(root, SnapshotWith());

			Assert.Equal(1, result.RemovedCount);
			Assert.False(Directory.Exists(directory));
		}
		finally { TryDelete(root); }
	}

	[Fact]
	public void Cleanup_DirectoryWithPatchPayload_IsKept()
	{
		var root = CreateRoot();
		try
		{
			var directory = Path.Combine(root, "payload");
			Directory.CreateDirectory(directory);
			File.WriteAllBytes(Path.Combine(directory, "9ba626afa44a3aa3.patch_0"), new byte[] { 1, 2, 3 });

			var result = Cleanup(root, SnapshotWith());

			Assert.Equal(0, result.RemovedCount);
			Assert.True(Directory.Exists(directory));
		}
		finally { TryDelete(root); }
	}

	[Fact]
	public void Cleanup_PatchPayloadInNestedSubdirectory_IsKept()
	{
		var root = CreateRoot();
		try
		{
			var nested = Path.Combine(root, "mod", "sub");
			Directory.CreateDirectory(nested);
			File.WriteAllBytes(Path.Combine(nested, "9ba626afa44a3aa3.patch_1.stream"), new byte[] { 1 });

			var result = Cleanup(root, SnapshotWith());

			Assert.Equal(0, result.RemovedCount);
			Assert.True(Directory.Exists(Path.Combine(root, "mod")));
		}
		finally { TryDelete(root); }
	}

	[Fact]
	public void Cleanup_ReadableLogicalMarker_IsKept()
	{
		var root = CreateRoot();
		try
		{
			var directory = Path.Combine(root, "logical");
			Directory.CreateDirectory(directory);
			ModNodeStorageProtocol.WriteLogicalMarker(directory, ModNodeId.New());

			var result = Cleanup(root, SnapshotWith());

			Assert.Equal(0, result.RemovedCount);
			Assert.True(Directory.Exists(directory));
		}
		finally { TryDelete(root); }
	}

	[Fact]
	public void Cleanup_RecognizedExternalInstallFile_IsKept()
	{
		var root = CreateRoot();
		try
		{
			var directory = Path.Combine(root, "reshade");
			Directory.CreateDirectory(directory);
			File.WriteAllText(Path.Combine(directory, "MyAddon.addon64"), "x");

			var result = Cleanup(root, SnapshotWith());

			Assert.Equal(0, result.RemovedCount);
			Assert.True(Directory.Exists(directory));
		}
		finally { TryDelete(root); }
	}

	[Fact]
	public void Cleanup_UnrecognizedFile_IsKept()
	{
		var root = CreateRoot();
		try
		{
			var directory = Path.Combine(root, "unknown");
			Directory.CreateDirectory(directory);
			File.WriteAllText(Path.Combine(directory, "readme.txt"), "not manager metadata");

			var result = Cleanup(root, SnapshotWith());

			Assert.Equal(0, result.RemovedCount);
			Assert.True(Directory.Exists(directory));
		}
		finally { TryDelete(root); }
	}

	[Fact]
	public void Cleanup_DirectoryReferencedBySnapshot_IsKept()
	{
		var root = CreateRoot();
		try
		{
			Directory.CreateDirectory(Path.Combine(root, "referenced"));

			var result = Cleanup(root, SnapshotWith("referenced"));

			Assert.Equal(0, result.RemovedCount);
			Assert.True(Directory.Exists(Path.Combine(root, "referenced")));
		}
		finally { TryDelete(root); }
	}

	[Fact]
	public void Cleanup_AncestorOfReferencedDirectory_IsKept()
	{
		var root = CreateRoot();
		try
		{
			var nested = Path.Combine(root, "parent", "child");
			Directory.CreateDirectory(nested);

			var result = Cleanup(root, SnapshotWith("parent/child"));

			Assert.Equal(0, result.RemovedCount);
			Assert.True(Directory.Exists(Path.Combine(root, "parent")));
		}
		finally { TryDelete(root); }
	}

	[Fact]
	public void Cleanup_TrashDirectory_IsKept()
	{
		var root = CreateRoot();
		try
		{
			Directory.CreateDirectory(Path.Combine(root, ".hd2modmanager-trash"));

			var result = Cleanup(root, SnapshotWith());

			Assert.Equal(0, result.RemovedCount);
			Assert.True(Directory.Exists(Path.Combine(root, ".hd2modmanager-trash")));
		}
		finally { TryDelete(root); }
	}

	[Fact]
	public void Cleanup_MixedDirectories_RemovesOnlyResiduals()
	{
		var root = CreateRoot();
		try
		{
			Directory.CreateDirectory(Path.Combine(root, "residual-a"));
			Directory.CreateDirectory(Path.Combine(root, "residual-b"));
			Directory.CreateDirectory(Path.Combine(root, "keep-payload"));
			File.WriteAllBytes(Path.Combine(root, "keep-payload", "9ba626afa44a3aa3.patch_0"), new byte[] { 1 });
			Directory.CreateDirectory(Path.Combine(root, "keep-referenced"));

			var result = Cleanup(root, SnapshotWith("keep-referenced"));

			Assert.Equal(2, result.RemovedCount);
			Assert.Empty(result.Failures);
			Assert.False(Directory.Exists(Path.Combine(root, "residual-a")));
			Assert.False(Directory.Exists(Path.Combine(root, "residual-b")));
			Assert.True(Directory.Exists(Path.Combine(root, "keep-payload")));
			Assert.True(Directory.Exists(Path.Combine(root, "keep-referenced")));
		}
		finally { TryDelete(root); }
	}

	[Fact]
	public void Cleanup_MissingModsRoot_ReturnsEmptyResult()
	{
		var root = CreateRoot();
		try
		{
			var result = Cleanup(root, SnapshotWith(), Path.Combine(root, "does-not-exist"));

			Assert.Equal(0, result.RemovedCount);
			Assert.Empty(result.Failures);
		}
		finally { TryDelete(root); }
	}

	private static ModLibraryResidualCleanupResult Cleanup(
		string root,
		LibrarySnapshot snapshot,
		string? modsRoot = null)
		=> new ModLibraryResidualCleanupService(new PatchFileNameParser())
			.Cleanup(snapshot, modsRoot ?? root);

	private static LibrarySnapshot SnapshotWith(params string[] relativePaths)
	{
		var nodes = new Dictionary<ModNodeId, ModNode>();
		foreach (var relativePath in relativePaths)
		{
			var id = ModNodeId.New();
			nodes[id] = new ModNode(
				id,
				relativePath,
				new ModNodeMetadata(relativePath, null, DateTimeOffset.UtcNow, null, ModNodeKind.Standard, IsLogicalContainer: false),
				Array.Empty<PatchGroupKey>(),
				Array.Empty<ModNodeId>());
		}
		return new LibrarySnapshot(1, DateTimeOffset.UtcNow, nodes, Array.Empty<Profile>());
	}

	private static string CreateRoot()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		return root;
	}

	private static void TryDelete(string root)
	{
		try { Directory.Delete(root, recursive: true); } catch { }
	}
}
