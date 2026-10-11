using System.Security.Cryptography;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

// Purpose: Verifies actual deployment graphs reconcile Data with activation state and compute winners by target patch index.
public sealed class DeployedOverrideGraphServiceTests
{
	[Fact]
	public async Task BuildAsync_ComputesActualWinnerUsingTargetPatchIndex()
	{
		var root = CreateTempRoot();
		try
		{
			var gameData = Path.Combine(root, "data");
			var sourceDirectory = Path.Combine(root, "mods");
			Directory.CreateDirectory(gameData);
			Directory.CreateDirectory(sourceDirectory);
			var key = new AssetKey(10, 20);
			var sourceA = Path.Combine(sourceDirectory, "aaaaaaaaaaaaaaaa.patch_7");
			var sourceB = Path.Combine(sourceDirectory, "aaaaaaaaaaaaaaaa.patch_2");
			await File.WriteAllBytesAsync(sourceA, BuildToc([key]));
			await File.WriteAllBytesAsync(sourceB, BuildToc([key]));
			var nodeA = ModNodeId.New();
			var nodeB = ModNodeId.New();
			var plan = new ApplyPlan(gameData, ProfileId.New(), 5, DateTimeOffset.UtcNow,
			[
				new ApplyOperation(ApplyOperationKind.DeployPatch, Path.Combine(gameData, "aaaaaaaaaaaaaaaa.patch_0"), sourceA, "aaaaaaaaaaaaaaaa", 7, 0, PatchSidecarKind.Base, nodeA),
				new ApplyOperation(ApplyOperationKind.DeployPatch, Path.Combine(gameData, "aaaaaaaaaaaaaaaa.patch_1"), sourceB, "aaaaaaaaaaaaaaaa", 2, 1, PatchSidecarKind.Base, nodeB),
			], []);
			Assert.True((await new ApplyExecutor().ExecuteAsync(plan)).Success);
			var graph = await new DeployedOverrideGraphService(new JsonActivationStateStore(), new PatchFileNameParser()).BuildAsync(gameData);

			var chain = Assert.Single(graph.AssetChains);
			Assert.Equal(key, chain.AssetKey);
			Assert.Equal(nodeB, chain.Winner.NodeId);
			Assert.Equal(1, chain.Winner.TargetPatchIndex);
			Assert.DoesNotContain(graph.Issues, issue => issue.Severity == CoreIssueSeverity.Error);
		}
		finally
		{
			DeleteQuietly(root);
		}
	}

	[Fact]
	public async Task BuildAsync_ReportsMissingRecordedAndUntrackedDataFiles()
	{
		var root = CreateTempRoot();
		try
		{
			var gameData = Path.Combine(root, "data");
			Directory.CreateDirectory(gameData);
			var missing = Path.Combine(gameData, "aaaaaaaaaaaaaaaa.patch_0");
			var extra = Path.Combine(gameData, "bbbbbbbbbbbbbbbb.patch_0");
			await File.WriteAllBytesAsync(extra, BuildToc([]));
			var state = new ActivationState(2, ProfileId.New(), 1, DateTimeOffset.UtcNow, true,
			[
				new ActivationStateFileEntry(missing, Path.Combine(root, "source.patch"), DeploymentMethod.Copy, "aaaaaaaaaaaaaaaa", 0, 0, PatchSidecarKind.Base, ModNodeId.New(), 0, "deadbeef"),
			], []);
			await new JsonActivationStateStore().SaveAsync(gameData, state);

			var graph = await new DeployedOverrideGraphService(new JsonActivationStateStore(), new PatchFileNameParser()).BuildAsync(gameData);

			Assert.Contains(graph.Issues, issue => issue.Code == "RecordedTargetMissing");
			Assert.Contains(graph.Issues, issue => issue.Code == "UntrackedDataPatch");
		}
		finally
		{
			DeleteQuietly(root);
		}
	}

	// 作用：这一组用例锁住"部署没变时二次打开零重算"，以及"任何一样输入变了都必须真的重算"。
	// 判据用**实例同一性**与 BuiltUtc，而不是计数埋点：命中记忆时返回的是同一个对象、BuiltUtc 保持第一次的值；
	// 真重算时必然是另一个对象、BuiltUtc 是新的。这样断言不依赖任何为测试而加的钩子。
	// Purpose: Pins "no recompute on a second open when the deployment is unchanged", and that a changed input
	// really does recompute. The proof is reference identity plus BuiltUtc, so no test-only instrumentation is needed.
	[Fact]
	public async Task RepeatedBuildOfAnUnchangedDeploymentIsServedFromTheMemo()
	{
		var root = CreateTempRoot();
		try
		{
			var deployment = await DeploySinglePatchAsync(root);
			var service = CreateService();

			var first = await service.BuildAsync(deployment.GameData);
			var second = await service.BuildAsync(deployment.GameData);

			// 同一个对象 = 没有重新哈希、没有重新扫 TOC。
			Assert.Same(first, second);
			Assert.Equal(first.BuiltUtc, second.BuiltUtc);
		}
		finally
		{
			DeleteQuietly(root);
		}
	}

	[Fact]
	public async Task ChangingADeployedPatchFileForcesAGenuineRebuild()
	{
		var root = CreateTempRoot();
		try
		{
			var deployment = await DeploySinglePatchAsync(root);
			var service = CreateService();
			var first = await service.BuildAsync(deployment.GameData);
			Assert.DoesNotContain(first.Issues, issue => issue.Severity == CoreIssueSeverity.Error);

			// 改写已部署的目标文件：长度保持不变（同样是 1 条 TOC），但内容与修改时间都变了。
			await File.WriteAllBytesAsync(deployment.Target, BuildToc([new AssetKey(99, 98)]));
			File.SetLastWriteTimeUtc(deployment.Target, File.GetLastWriteTimeUtc(deployment.Target).AddMinutes(1));

			var second = await service.BuildAsync(deployment.GameData);

			Assert.NotSame(first, second);
			Assert.Contains(second.Issues, issue => issue.Code == "DeployedContentMismatch");
		}
		finally
		{
			DeleteQuietly(root);
		}
	}

	// 这条是整组里最关键的一条：它证明"源文件在指纹里"。
	// 为什么必须用 Copy 型部署来构造：真实的 ApplyExecutor 只支持硬链接与符号链接，
	// 而链接之下"目标"与"源"本来就是同一个文件，改源必然同时改目标，两件事分不开。
	// Copy 型（修复 / 导入流程与外部文件会记录这一种）才是"部署的 patch 没动、它引用的源被改了"
	// 唯一能成立的情形，也正是"缓存取代真实性"唯一能钻进来的缝。
	[Fact]
	public async Task ChangingARecordedSourceFileForcesAGenuineRebuild()
	{
		var root = CreateTempRoot();
		try
		{
			var gameData = Path.Combine(root, "data");
			Directory.CreateDirectory(gameData);
			var target = Path.Combine(gameData, "aaaaaaaaaaaaaaaa.patch_0");
			var source = Path.Combine(root, "source.patch_7");
			await File.WriteAllBytesAsync(target, BuildToc([new AssetKey(10, 20)]));
			await File.WriteAllBytesAsync(source, BuildToc([new AssetKey(10, 20)]));
			var state = new ActivationState(2, ProfileId.New(), 1, DateTimeOffset.UtcNow, true,
			[
				new ActivationStateFileEntry(target, source, DeploymentMethod.Copy, "aaaaaaaaaaaaaaaa", 7, 0, PatchSidecarKind.Base, ModNodeId.New(), new FileInfo(target).Length, HashOf(target)),
			], []);
			await new JsonActivationStateStore().SaveAsync(gameData, state);
			var service = CreateService();

			var first = await service.BuildAsync(gameData);
			Assert.DoesNotContain(first.Issues, issue => issue.Severity == CoreIssueSeverity.Error);

			// 只改源：目标的长度、修改时间、内容全部保持不变。
			// 长度与修改时间都变了，所以即使文件系统时间戳精度很粗，这条用例也不会因为"恰好同刻度"而失效。
			await File.WriteAllBytesAsync(source, BuildToc([new AssetKey(99, 98), new AssetKey(97, 96)]));
			File.SetLastWriteTimeUtc(source, File.GetLastWriteTimeUtc(source).AddMinutes(1));

			var second = await service.BuildAsync(gameData);

			Assert.NotSame(first, second);
			Assert.Contains(second.Issues, issue => issue.Code == "DeployedSourceChanged");
		}
		finally
		{
			DeleteQuietly(root);
		}
	}

	[Fact]
	public async Task RemovingADeployedPatchFileForcesAGenuineRebuild()
	{
		var root = CreateTempRoot();
		try
		{
			var deployment = await DeploySinglePatchAsync(root);
			var service = CreateService();
			var first = await service.BuildAsync(deployment.GameData);

			File.Delete(deployment.Target);

			var second = await service.BuildAsync(deployment.GameData);

			Assert.NotSame(first, second);
			Assert.Contains(second.Issues, issue => issue.Code == "RecordedTargetMissing");
		}
		finally
		{
			DeleteQuietly(root);
		}
	}

	private static DeployedOverrideGraphService CreateService()
		=> new(new JsonActivationStateStore(), new PatchFileNameParser());

	// 作用：算一个文件的 SHA-256，用来手写"记录的内容哈希"，让 Copy 型部署初始状态是自洽的。
	private static string HashOf(string path)
		=> Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

	// 作用：用真实的 ApplyExecutor 铺一个最小可用的部署（1 个 base patch），避免手写激活状态时抄错字段。
	private static async Task<(string GameData, string Source, string Target)> DeploySinglePatchAsync(string root)
	{
		var gameData = Path.Combine(root, "data");
		var sourceDirectory = Path.Combine(root, "mods");
		Directory.CreateDirectory(gameData);
		Directory.CreateDirectory(sourceDirectory);
		var source = Path.Combine(sourceDirectory, "aaaaaaaaaaaaaaaa.patch_7");
		var target = Path.Combine(gameData, "aaaaaaaaaaaaaaaa.patch_0");
		await File.WriteAllBytesAsync(source, BuildToc([new AssetKey(10, 20)]));
		var plan = new ApplyPlan(gameData, ProfileId.New(), 5, DateTimeOffset.UtcNow,
		[
			new ApplyOperation(ApplyOperationKind.DeployPatch, target, source, "aaaaaaaaaaaaaaaa", 7, 0, PatchSidecarKind.Base, ModNodeId.New()),
		], []);
		Assert.True((await new ApplyExecutor().ExecuteAsync(plan)).Success);
		return (gameData, source, target);
	}

	private static string CreateTempRoot()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2-deployed-graph-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		return root;
	}

	private static byte[] BuildToc(IReadOnlyList<AssetKey> entries)
	{
		const uint magic = 4026531857;
		var offset = 60;
		var buffer = new byte[offset + entries.Count * 80];
		WriteUInt32(buffer, 0, magic);
		WriteUInt32(buffer, 4, 0);
		WriteUInt32(buffer, 8, (uint)entries.Count);
		foreach (var entry in entries)
		{
			WriteUInt64(buffer, offset, entry.FileId);
			WriteUInt64(buffer, offset + 8, entry.TypeId);
			offset += 80;
		}
		return buffer;
	}

	private static void WriteUInt32(byte[] buffer, int offset, uint value)
	{
		for (var index = 0; index < 4; index++) buffer[offset + index] = (byte)(value >> (index * 8));
	}

	private static void WriteUInt64(byte[] buffer, int offset, ulong value)
	{
		for (var index = 0; index < 8; index++) buffer[offset + index] = (byte)(value >> (index * 8));
	}

	private static void DeleteQuietly(string root)
	{
		try { Directory.Delete(root, recursive: true); } catch { }
	}
}
