using HD2ModCore.Application;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

// 作用：验证「库根下的外部包」能被认领成受管节点，且失败时源目录一定还在。
// Purpose: Verifies external packages under the library root are adopted into managed nodes,
// and that a failed adoption always leaves the source directory untouched.
public sealed class ExternalPackageAdoptionServiceTests
{
	private const string PatchName = "9ba626afa44a3aa3.patch_0";

	[Fact]
	public async Task AdoptAsync_ManifestPackage_CreatesOptionNodesAndEnablesThemAll()
	{
		var root = CreateRoot();
		var mods = Path.Combine(root, "mods");
		var package = Path.Combine(mods, "测试包");
		Directory.CreateDirectory(Path.Combine(package, "本体"));
		Directory.CreateDirectory(Path.Combine(package, "狐耳"));
		try
		{
			File.WriteAllText(Path.Combine(package, "manifest.json"), """
				{
				  "Version": 1,
				  "Guid": "a7f3c8e1-5b2d-4f6a-9c3e-8d1b7a2f4c6e",
				  "Name": "测试包",
				  "IconPath": "0.png",
				  "Options": [
				    { "Name": "本体", "Include": ["本体"] },
				    { "Name": "狐耳", "Include": ["狐耳"] }
				  ]
				}
				""");
			File.WriteAllText(Path.Combine(package, "本体", PatchName), "body");
			File.WriteAllText(Path.Combine(package, "狐耳", PatchName), "ears");

			var (paths, _, service, activations) = CreateServices(root);
			var adoption = await service.AdoptAsync(EmptySnapshot(), mods);

			Assert.True(adoption.Changed);
			Assert.Equal(ExternalPackageAdoptionStatus.Adopted, Assert.Single(adoption.Outcomes).Status);
			Assert.Equal(3, adoption.Snapshot.Nodes.Count);

			var container = Assert.Single(adoption.Snapshot.Nodes.Values, node => node.Metadata.IsLogicalContainer);
			var options = adoption.Snapshot.Nodes.Values
				.Where(node => node.Metadata.Kind == ModNodeKind.Option)
				.ToArray();
			Assert.Equal(2, options.Length);
			Assert.All(options, option =>
				Assert.Equal(container.Id.Value.ToString("N"), Assert.Single(option.Metadata.HostModGuids!)));
			Assert.Equal(["本体", "狐耳"], options.Select(option => option.Metadata.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray());

			// 用户拍板的行为：进库即全开，但要能单独关。
			var states = activations.CreateSnapshot();
			Assert.All(options, option =>
				Assert.True(states.IsEnabled(option.Id.Value.ToString("N"), container.Id.Value.ToString("N"))));

			// 源目录必须已经不在库里，否则下一轮重扫会再导入一遍。
			Assert.False(Directory.Exists(package));
			Assert.Single(Directory.EnumerateDirectories(paths.DeletionTrashDirectory));
		}
		finally { TryDelete(root); }
	}

	[Fact]
	public async Task AdoptAsync_PackageWithoutManifest_ImportsNestedDirectoryAsSingleNode()
	{
		var root = CreateRoot();
		var mods = Path.Combine(root, "mods");
		var package = Path.Combine(mods, "无后座修复");
		Directory.CreateDirectory(Path.Combine(package, "无后座修复"));
		try
		{
			File.WriteAllText(Path.Combine(package, "无后座修复", PatchName), "recoil");

			var (_, _, service, _) = CreateServices(root);
			var adoption = await service.AdoptAsync(EmptySnapshot(), mods);

			var node = Assert.Single(adoption.Snapshot.Nodes.Values);
			Assert.Equal(ModNodeKind.Standard, node.Metadata.Kind);
			Assert.Single(node.PatchGroups);
			Assert.False(Directory.Exists(package));
		}
		finally { TryDelete(root); }
	}

	[Fact]
	public async Task AdoptAsync_UsesHardLinks_SoThePackageIsNotCopiedTwice()
	{
		var root = CreateRoot();
		var mods = Path.Combine(root, "mods");
		var package = Path.Combine(mods, "大包");
		Directory.CreateDirectory(Path.Combine(package, "本体"));
		try
		{
			var sourcePatch = Path.Combine(package, "本体", PatchName);
			File.WriteAllText(sourcePatch, "payload");

			var (paths, _, service, _) = CreateServices(root);
			var adoption = await service.AdoptAsync(EmptySnapshot(), mods);

			var node = Assert.Single(adoption.Snapshot.Nodes.Values, candidate => candidate.PatchGroups.Count != 0);
			var adoptedPatch = Path.Combine(paths.ModsDirectory, node.RelativePath, PatchName);
			var trashedPatch = Directory
				.EnumerateFiles(paths.DeletionTrashDirectory, PatchName, SearchOption.AllDirectories)
				.Single();

			Assert.True(File.Exists(adoptedPatch));
			Assert.True(FileIdentity.AreSameFile(adoptedPatch, trashedPatch));
		}
		finally { TryDelete(root); }
	}

	[Fact]
	public async Task AdoptAsync_SecondRun_DoesNotImportAgain()
	{
		var root = CreateRoot();
		var mods = Path.Combine(root, "mods");
		var package = Path.Combine(mods, "重复包");
		Directory.CreateDirectory(Path.Combine(package, "本体"));
		try
		{
			File.WriteAllText(Path.Combine(package, "本体", PatchName), "body");

			var (paths, _, service, _) = CreateServices(root);
			var first = await service.AdoptAsync(EmptySnapshot(), mods);
			var second = await service.AdoptAsync(first.Snapshot, mods);

			Assert.True(first.Changed);
			Assert.False(second.Changed);
			Assert.Empty(second.Outcomes);
			Assert.Equal(first.Snapshot.Nodes.Count, second.Snapshot.Nodes.Count);
			Assert.Single(Directory.EnumerateDirectories(paths.DeletionTrashDirectory));
		}
		finally { TryDelete(root); }
	}

	[Fact]
	public async Task AdoptAsync_ImportFailure_KeepsSourceAndLeavesNoNodes()
	{
		var root = CreateRoot();
		var mods = Path.Combine(root, "mods");
		var package = Path.Combine(mods, "坏包");
		Directory.CreateDirectory(Path.Combine(package, "本体"));
		try
		{
			File.WriteAllText(Path.Combine(package, "本体", PatchName), "body");
			var snapshot = EmptySnapshot();
			var service = new ExternalPackageAdoptionService(
				new StoragePaths(root),
				new ThrowingImporter(),
				new PatchFileNameParser(),
				CoreServices.CreateOptionActivationStore(new StoragePaths(root)));

			var adoption = await service.AdoptAsync(snapshot, mods);

			var outcome = Assert.Single(adoption.Outcomes);
			Assert.Equal(ExternalPackageAdoptionStatus.Failed, outcome.Status);
			Assert.False(adoption.Changed);
			Assert.Empty(adoption.Snapshot.Nodes);
			// 源目录原样保留，标记也不能残留——否则下一轮会被当成「已导入」直接移走。
			Assert.True(Directory.Exists(package));
			Assert.False(File.Exists(Path.Combine(package, ExternalPackageAdoptionService.MarkerFileName)));
		}
		finally { TryDelete(root); }
	}

	[Fact]
	public async Task AdoptAsync_IgnoresManagerDirectoriesAndManagedNodes()
	{
		var root = CreateRoot();
		var mods = Path.Combine(root, "mods");
		try
		{
			// 管理器的运行时产物：形状和外部包一模一样，但绝不能认领。
			Directory.CreateDirectory(Path.Combine(mods, ".runtime", "lua", "fingerprint"));
			File.WriteAllText(Path.Combine(mods, ".runtime", "lua", "fingerprint", PatchName), "lua");
			// 已受管节点：有设置文件，交给重扫。
			Directory.CreateDirectory(Path.Combine(mods, "已受管", "子目录"));
			File.WriteAllText(Path.Combine(mods, "已受管", ModSettingsFile.FileName), "{}");
			File.WriteAllText(Path.Combine(mods, "已受管", "子目录", PatchName), "managed");
			// 根上就有 patch：正常 Mod 形状，重扫能收。
			Directory.CreateDirectory(Path.Combine(mods, "正常Mod"));
			File.WriteAllText(Path.Combine(mods, "正常Mod", PatchName), "flat");

			var (_, _, service, _) = CreateServices(root);
			var adoption = await service.AdoptAsync(EmptySnapshot(), mods);

			Assert.Empty(adoption.Outcomes);
			Assert.True(Directory.Exists(Path.Combine(mods, ".runtime")));
			Assert.True(Directory.Exists(Path.Combine(mods, "已受管")));
			Assert.True(Directory.Exists(Path.Combine(mods, "正常Mod")));
		}
		finally { TryDelete(root); }
	}

	[Fact]
	public async Task AdoptAsync_ManifestDeclaresUnshippedOption_StillAdoptsAndWarns()
	{
		// 实测踩过的坑：作者声明了 9 个选项、只发了 8 个目录（milltina 修女 的「裙摆」）。
		// 曾经把这种"部分缺失"当成"复制到一半"去阻止认领，结果是那个包永久进不了库。
		var root = CreateRoot();
		var mods = Path.Combine(root, "mods");
		var package = Path.Combine(mods, "漏发选项");
		Directory.CreateDirectory(Path.Combine(package, "本体"));
		try
		{
			File.WriteAllText(Path.Combine(package, "manifest.json"), """
				{
				  "Version": 1,
				  "Name": "漏发选项",
				  "Options": [
				    { "Name": "本体", "Include": ["本体"] },
				    { "Name": "狐耳", "Include": ["狐耳"] }
				  ]
				}
				""");
			File.WriteAllText(Path.Combine(package, "本体", PatchName), "body");

			var (_, _, service, _) = CreateServices(root);
			var adoption = await service.AdoptAsync(EmptySnapshot(), mods);

			var outcome = Assert.Single(adoption.Outcomes);
			Assert.Equal(ExternalPackageAdoptionStatus.Adopted, outcome.Status);
			// 警告要落进日志：GUI 的认领路径没有 dry-run，这是用户唯一能看到提示的地方。
			Assert.Contains("狐耳", outcome.Detail, StringComparison.Ordinal);
			Assert.False(Directory.Exists(package));
		}
		finally { TryDelete(root); }
	}

	[Fact]
	public async Task AdoptAsync_RestoredPackage_IsNotTrashedAgain()
	{
		// 用户从 .hd2modmanager-trash 里把包还原回库根：包里的 imported 标记还在，
		// 而节点也还在库里。这时候再移一次等于把用户的还原动作静默撤销。
		var root = CreateRoot();
		var mods = Path.Combine(root, "mods");
		var package = Path.Combine(mods, "还原包");
		Directory.CreateDirectory(Path.Combine(package, "本体"));
		try
		{
			File.WriteAllText(Path.Combine(package, "本体", PatchName), "body");

			var (paths, _, service, _) = CreateServices(root);
			var first = await service.AdoptAsync(EmptySnapshot(), mods);
			Assert.Equal(ExternalPackageAdoptionStatus.Adopted, Assert.Single(first.Outcomes).Status);

			var trashedPackage = Directory.EnumerateDirectories(paths.DeletionTrashDirectory)
				.SelectMany(Directory.EnumerateDirectories)
				.Single();
			Directory.Move(trashedPackage, package);

			var second = await service.AdoptAsync(first.Snapshot, mods);

			var outcome = Assert.Single(second.Outcomes);
			Assert.Equal(ExternalPackageAdoptionStatus.Skipped, outcome.Status);
			Assert.False(second.Changed);
			Assert.True(Directory.Exists(Path.Combine(package, "本体")));
		}
		finally { TryDelete(root); }
	}

	[Fact]
	public async Task AdoptAsync_StaleImportedMarker_ReimportsInsteadOfTrashing()
	{
		// 标记说"已导入"，但库里已经没有那些节点（用户删了条目）。此时源目录是唯一副本，
		// 照旧"只补移动"就会把它丢进回收站而库里什么都没有。
		var root = CreateRoot();
		var mods = Path.Combine(root, "mods");
		var package = Path.Combine(mods, "标记过期");
		Directory.CreateDirectory(Path.Combine(package, "本体"));
		try
		{
			File.WriteAllText(Path.Combine(package, "本体", PatchName), "body");
			File.WriteAllText(Path.Combine(package, ExternalPackageAdoptionService.MarkerFileName), $$"""
				{
				  "schema": "{{ExternalPackageAdoptionService.MarkerSchema}}",
				  "status": "imported",
				  "nodeIds": ["{{Guid.NewGuid():N}}"],
				  "startedUtc": "{{DateTimeOffset.UtcNow.AddDays(-1):O}}"
				}
				""");

			var (_, _, service, _) = CreateServices(root);
			var adoption = await service.AdoptAsync(EmptySnapshot(), mods);

			var outcome = Assert.Single(adoption.Outcomes);
			Assert.Equal(ExternalPackageAdoptionStatus.Adopted, outcome.Status);
			Assert.NotEmpty(adoption.Snapshot.Nodes);
			Assert.False(Directory.Exists(package));
		}
		finally { TryDelete(root); }
	}

	private static (StoragePaths Paths, IModLibraryImporter Importer, ExternalPackageAdoptionService Service, OptionActivationStore Activations) CreateServices(string root)
	{
		var paths = new StoragePaths(root);
		var parser = new PatchFileNameParser();
		var importer = new ModLibraryImporter(
			paths,
			new ObjectTreeImporter(parser),
			new ArchiveObjectTreeImporter(new ObjectTreeImporter(parser)),
			new JsonModLibraryStore(paths));
		var activations = CoreServices.CreateOptionActivationStore(paths);
		var service = new ExternalPackageAdoptionService(paths, importer, parser, activations);
		return (paths, importer, service, activations);
	}

	private static LibrarySnapshot EmptySnapshot()
		=> new(1, DateTimeOffset.UtcNow, new Dictionary<ModNodeId, ModNode>(), Array.Empty<Profile>());

	private static string CreateRoot()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(Path.Combine(root, "mods"));
		return root;
	}

	private static void TryDelete(string root)
	{
		try { Directory.Delete(root, recursive: true); } catch { }
	}

	/// <summary>导入必定失败的替身：只用来验证「失败时源目录还在、标记不残留」。</summary>
	private sealed class ThrowingImporter : IModLibraryImporter
	{
		public ValueTask<ImportResult> ImportFolderAsync(string folderPath, CancellationToken cancellationToken = default)
			=> throw new InvalidDataException("synthetic import failure");

		public ValueTask<ImportResult> ImportLibraryResidentFolderAsync(string folderPath, bool preferHardLinks, CancellationToken cancellationToken = default)
			=> throw new InvalidDataException("synthetic import failure");

		public ValueTask<ImportResult> ImportArchiveAsync(string archiveFilePath, CancellationToken cancellationToken = default)
			=> throw new InvalidDataException("synthetic import failure");
	}
}
