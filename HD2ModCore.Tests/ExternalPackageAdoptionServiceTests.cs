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
			Assert.Single(Directory.EnumerateDirectories(paths.AdoptedArchiveDirectory));
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
				.EnumerateFiles(paths.AdoptedArchiveDirectory, PatchName, SearchOption.AllDirectories)
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
			Assert.Single(Directory.EnumerateDirectories(paths.AdoptedArchiveDirectory));
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

			var trashedPackage = Directory.EnumerateDirectories(paths.AdoptedArchiveDirectory)
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

	[Fact]
	public async Task AdoptAsync_PendingMoveMarker_OnlyMovesWithoutImportingAgain()
	{
		// 上一轮「导入成功、移动失败」：标记是 imported（待移动），节点都还在库里。
		// 下一轮必须**只补移动**。旧实现把这种状态当成「已在库中」直接跳过，于是
		// 库根上永久留着一个带嵌套的外部包，每轮同步报一次，而文案还写着"下一轮只重试移动"。
		var root = CreateRoot();
		var mods = Path.Combine(root, "mods");
		var package = Path.Combine(mods, "待移动包");
		Directory.CreateDirectory(Path.Combine(package, "本体"));
		try
		{
			File.WriteAllText(Path.Combine(package, "本体", PatchName), "body");
			var nodeId = ModNodeId.New();
			File.WriteAllText(Path.Combine(package, ExternalPackageAdoptionService.MarkerFileName), $$"""
				{
				  "schema": "{{ExternalPackageAdoptionService.MarkerSchema}}",
				  "status": "imported",
				  "nodeIds": ["{{nodeId.Value:N}}"],
				  "startedUtc": "{{DateTimeOffset.UtcNow.AddHours(-1):O}}"
				}
				""");
			var snapshot = new LibrarySnapshot(
				1,
				DateTimeOffset.UtcNow,
				new Dictionary<ModNodeId, ModNode>
				{
					[nodeId] = new ModNode(
						nodeId,
						"库里已有",
						new ModNodeMetadata("库里已有", null, DateTimeOffset.UtcNow, null),
						Array.Empty<PatchGroupKey>(),
						Array.Empty<ModNodeId>()),
				},
				Array.Empty<Profile>());

			var (paths, _, service, _) = CreateServices(root);
			var adoption = await service.AdoptAsync(snapshot, mods);

			var outcome = Assert.Single(adoption.Outcomes);
			Assert.Equal(ExternalPackageAdoptionStatus.Adopted, outcome.Status);
			Assert.Contains("补移动", outcome.Detail, StringComparison.Ordinal);
			// 没有重新导入：库里还是那一个节点，源目录已经搬进副本目录。
			Assert.Single(adoption.Snapshot.Nodes);
			Assert.False(Directory.Exists(package));
			Assert.Single(Directory.EnumerateFiles(paths.AdoptedArchiveDirectory, PatchName, SearchOption.AllDirectories));
		}
		finally { TryDelete(root); }
	}

	[Fact]
	public async Task AdoptAsync_PartialImportFailure_RollsBackCommittedUnits()
	{
		// 多单元导入是逐单元提交的：第二个单元失败时第一个已经在库里。不回滚就删标记，
		// 下一轮会把第一个单元再导入一遍——库里出现两份。这里验证回滚真的发生了。
		var root = CreateRoot();
		var mods = Path.Combine(root, "mods");
		var package = Path.Combine(mods, "两段包");
		Directory.CreateDirectory(Path.Combine(package, "本体"));
		try
		{
			File.WriteAllText(Path.Combine(package, "本体", PatchName), "body");
			var importer = new PartiallyCommittingImporter();
			var paths = new StoragePaths(root);
			var service = new ExternalPackageAdoptionService(
				paths,
				importer,
				new PatchFileNameParser(),
				CoreServices.CreateOptionActivationStore(paths));

			var adoption = await service.AdoptAsync(EmptySnapshot(), mods);

			var outcome = Assert.Single(adoption.Outcomes);
			Assert.Equal(ExternalPackageAdoptionStatus.Failed, outcome.Status);
			Assert.Equal(importer.Committed, importer.RolledBack);
			Assert.Contains("已回滚", outcome.Detail, StringComparison.Ordinal);
			// 源目录原样保留、标记不残留：下一轮从零重导不会撞上重复节点。
			Assert.True(Directory.Exists(package));
			Assert.False(File.Exists(Path.Combine(package, ExternalPackageAdoptionService.MarkerFileName)));
		}
		finally { TryDelete(root); }
	}

	[Fact]
	public async Task AdoptAsync_RootAndNestedPatch_AdoptsAndWarns()
	{
		// 「导出为文件夹」产物的形状：本层有 patch，子目录里还有选项的 patch。
		// 旧判据（本层没有 patch 才认领）会把它当正常 Mod 收下，子目录内容静默消失。
		var root = CreateRoot();
		var mods = Path.Combine(root, "mods");
		var package = Path.Combine(mods, "导出形状");
		Directory.CreateDirectory(Path.Combine(package, "狐耳"));
		try
		{
			File.WriteAllText(Path.Combine(package, PatchName), "body");
			File.WriteAllText(Path.Combine(package, "狐耳", PatchName), "ears");

			var (_, _, service, _) = CreateServices(root);
			var adoption = await service.AdoptAsync(EmptySnapshot(), mods);

			var outcome = Assert.Single(adoption.Outcomes);
			Assert.Equal(ExternalPackageAdoptionStatus.Adopted, outcome.Status);
			Assert.Contains("本层有", outcome.Detail, StringComparison.Ordinal);
			// 本层内容 + 子目录选项，两份都进库了。
			Assert.Equal(2, adoption.Snapshot.Nodes.Count);
			// 选项名走的是启发式回退（包名-目录名），认的是它挂在哪个包路径上。
			Assert.Contains(adoption.Snapshot.Nodes.Values,
				node => node.Metadata.Kind == ModNodeKind.Option && node.Metadata.SourcePackagePath == "狐耳");
		}
		finally { TryDelete(root); }
	}

	[Fact]
	public async Task AdoptAsync_ManagedDirectoryWithNestedPayload_IsReportedNotAdopted()
	{
		// 已经被当成普通 Mod 收录的目录：认领它会给同一批内容再建一套节点、旧节点的启用状态作废。
		// 所以这里只报告，让用户自己决定（移出库根再移回来，就会按外部包认领）。
		var root = CreateRoot();
		var mods = Path.Combine(root, "mods");
		var package = Path.Combine(mods, "已收录");
		Directory.CreateDirectory(Path.Combine(package, "狐耳"));
		try
		{
			File.WriteAllText(Path.Combine(package, PatchName), "body");
			File.WriteAllText(Path.Combine(package, "狐耳", PatchName), "ears");
			var nodeId = ModNodeId.New();
			var snapshot = new LibrarySnapshot(
				1,
				DateTimeOffset.UtcNow,
				new Dictionary<ModNodeId, ModNode>
				{
					[nodeId] = new ModNode(
						nodeId,
						"已收录",
						new ModNodeMetadata("已收录", null, DateTimeOffset.UtcNow, null),
						Array.Empty<PatchGroupKey>(),
						Array.Empty<ModNodeId>()),
				},
				Array.Empty<Profile>());

			var (_, _, service, _) = CreateServices(root);
			var adoption = await service.AdoptAsync(snapshot, mods);

			var outcome = Assert.Single(adoption.Outcomes);
			Assert.Equal(ExternalPackageAdoptionStatus.Skipped, outcome.Status);
			Assert.Contains("没进库", outcome.Detail, StringComparison.Ordinal);
			Assert.True(Directory.Exists(package));
		}
		finally { TryDelete(root); }
	}

	[Fact]
	public async Task AdoptAsync_PrunesExpiredArchiveBatches()
	{
		// 副本目录必须有出口：过期批次清掉，用户自己放进去的东西不碰。
		var root = CreateRoot();
		var mods = Path.Combine(root, "mods");
		var package = Path.Combine(mods, "新包");
		Directory.CreateDirectory(Path.Combine(package, "本体"));
		try
		{
			File.WriteAllText(Path.Combine(package, "本体", PatchName), "body");
			var (paths, _, service, _) = CreateServices(root);
			var expired = Path.Combine(paths.AdoptedArchiveDirectory, "20200101-000000-abcdef12");
			var fresh = Path.Combine(paths.AdoptedArchiveDirectory, "29991231-000000-abcdef12");
			var foreign = Path.Combine(paths.AdoptedArchiveDirectory, "我的备份");
			Directory.CreateDirectory(Path.Combine(expired, "旧包"));
			Directory.CreateDirectory(Path.Combine(fresh, "新包"));
			Directory.CreateDirectory(foreign);
			Directory.SetLastWriteTimeUtc(expired, DateTime.UtcNow.AddDays(-(ExternalPackageAdoptionService.ArchiveRetention.TotalDays + 5)));

			var adoption = await service.AdoptAsync(EmptySnapshot(), mods);

			Assert.False(Directory.Exists(expired));
			Assert.True(Directory.Exists(fresh));
			Assert.True(Directory.Exists(foreign));
			Assert.Contains(adoption.Notes, note => note.Contains("清理过期认领副本", StringComparison.Ordinal));
		}
		finally { TryDelete(root); }
	}

	[Fact]
	public async Task AdoptAsync_BeyondRoundLimit_IsQueuedNotRejected()
	{
		// 超上限只是没轮到，不是被拒：两者在文案上必须分开，否则界面每轮都在报「未能认领」。
		var root = CreateRoot();
		var mods = Path.Combine(root, "mods");
		try
		{
			for (var index = 0; index <= ExternalPackageAdoptionService.MaxAdoptionsPerRound; index++)
			{
				var package = Path.Combine(mods, $"包{index:D3}");
				Directory.CreateDirectory(Path.Combine(package, "本体"));
				File.WriteAllText(Path.Combine(package, "本体", PatchName), "body");
			}

			var (_, _, service, _) = CreateServices(root);
			var adoption = await service.AdoptAsync(EmptySnapshot(), mods);

			Assert.Equal(
				ExternalPackageAdoptionService.MaxAdoptionsPerRound,
				adoption.Outcomes.Count(outcome => outcome.Status == ExternalPackageAdoptionStatus.Adopted));
			var queued = Assert.Single(adoption.Outcomes, outcome => outcome.Status == ExternalPackageAdoptionStatus.Queued);
			Assert.Contains("排队中", queued.Detail, StringComparison.Ordinal);
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

		public ValueTask<ImportResult> ImportLibraryResidentFolderAsync(
			string folderPath,
			bool preferHardLinks,
			Action<IReadOnlyList<ModNodeId>>? onUnitCommitted = null,
			CancellationToken cancellationToken = default)
			=> throw new InvalidDataException("synthetic import failure");

		public ValueTask<ImportResult> ImportArchiveAsync(string archiveFilePath, CancellationToken cancellationToken = default)
			=> throw new InvalidDataException("synthetic import failure");

		public ValueTask RollbackImportAsync(IReadOnlyCollection<ModNodeId> nodeIds, CancellationToken cancellationToken = default)
			=> ValueTask.CompletedTask;
	}

	/// <summary>
	/// 多单元导入的替身：第一个单元提交成功（回调已落库的节点），第二个单元抛错。
	/// 用来验证「中途失败必须回滚已经提交的部分」——不回滚的话，下一轮重导会在库里留下两份。
	/// </summary>
	private sealed class PartiallyCommittingImporter : IModLibraryImporter
	{
		public List<ModNodeId> Committed { get; } = new();
		public List<ModNodeId> RolledBack { get; } = new();

		public ValueTask<ImportResult> ImportFolderAsync(string folderPath, CancellationToken cancellationToken = default)
			=> throw new InvalidDataException("not used");

		public ValueTask<ImportResult> ImportLibraryResidentFolderAsync(
			string folderPath,
			bool preferHardLinks,
			Action<IReadOnlyList<ModNodeId>>? onUnitCommitted = null,
			CancellationToken cancellationToken = default)
		{
			var firstUnit = new[] { ModNodeId.New() };
			Committed.AddRange(firstUnit);
			onUnitCommitted?.Invoke(firstUnit);
			throw new InvalidDataException("second unit failed");
		}

		public ValueTask<ImportResult> ImportArchiveAsync(string archiveFilePath, CancellationToken cancellationToken = default)
			=> throw new InvalidDataException("not used");

		public ValueTask RollbackImportAsync(IReadOnlyCollection<ModNodeId> nodeIds, CancellationToken cancellationToken = default)
		{
			RolledBack.AddRange(nodeIds);
			return ValueTask.CompletedTask;
		}
	}
}
