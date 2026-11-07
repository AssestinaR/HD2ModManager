using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

// 作用：验证 ApplyExecutor 的增量部署：已经正确的补丁链接不会被删除重建，只处理有差异的文件。
// Purpose: Verifies incremental deployment - a patch link that is already the library file is left untouched,
// and only files that differ are deleted, relinked or removed.
public sealed class ApplyExecutorIncrementalTests
{
	private const string Archive = "9ba626afa44a3aa3";

	private sealed class Workspace : IDisposable
	{
		public Workspace()
		{
			Root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
			GameData = Path.Combine(Root, "game", "data");
			Mods = Path.Combine(Root, "mods");
			Directory.CreateDirectory(GameData);
			Directory.CreateDirectory(Mods);
		}

		public string Root { get; }
		public string GameData { get; }
		public string Mods { get; }
		public string Target(int index) => Path.Combine(GameData, $"{Archive}.patch_{index}");

		public string Source(string name, string content)
		{
			var path = Path.Combine(Mods, name);
			File.WriteAllText(path, content);
			return path;
		}

		// Mirrors ApplyPlanner: every existing patch gets a DeletePatch, every wanted one a DeployPatch.
		public ApplyPlan Plan(IEnumerable<string> existingTargets, params (string Target, string Source)[] wanted)
		{
			var operations = new List<ApplyOperation>();
			foreach (var existing in existingTargets)
				operations.Add(new ApplyOperation(ApplyOperationKind.DeletePatch, existing, null, null, null, null, null, null));
			var index = 0;
			foreach (var (target, source) in wanted)
				operations.Add(new ApplyOperation(ApplyOperationKind.DeployPatch, target, source, Archive, index++, index - 1, PatchSidecarKind.Base, ModNodeId.New()));
			return new ApplyPlan(GameData, ProfileId.New(), 1, DateTimeOffset.UtcNow, operations, [], DeploymentMethod.HardLink)
			{
				UseFastLinkValidation = true,
			};
		}

		public IEnumerable<string> ExistingPatches() => Directory.EnumerateFiles(GameData, "*.patch_*").ToArray();

		public void Dispose()
		{
			try { Directory.Delete(Root, recursive: true); } catch { }
		}
	}

	[Fact]
	public async Task ExecuteAsync_SecondDeployOfSamePlan_LeavesCorrectLinksUntouched()
	{
		if (!OperatingSystem.IsWindows()) return;
		using var workspace = new Workspace();
		var source = workspace.Source("a.patch_0", "AAA");
		var target = workspace.Target(0);
		var executor = new ApplyExecutor();
		var first = await executor.ExecuteAsync(workspace.Plan([], (target, source)));
		Assert.True(first.Success, Describe(first));

		// Holding the target open without FileShare.Delete makes File.Delete fail: the second
		// deploy can only succeed if it does not delete and recreate a link that is already right.
		using var hold = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
		var second = await executor.ExecuteAsync(workspace.Plan(workspace.ExistingPatches(), (target, source)));

		Assert.True(second.Success, Describe(second));
		Assert.Contains(second.Operations, result => result.Operation.Kind == ApplyOperationKind.DeployPatch && result.Success && result.Message == ApplyExecutor.UnchangedMessage);
		Assert.Equal("AAA", await File.ReadAllTextAsync(target));
		var state = await new JsonActivationStateStore().TryLoadAsync(workspace.GameData);
		Assert.Equal(target, Assert.Single(state!.Files).TargetPath);
	}

	[Fact]
	public async Task ExecuteAsync_ChangedSource_RelinksOnlyThatTarget()
	{
		if (!OperatingSystem.IsWindows()) return;
		using var workspace = new Workspace();
		var sourceA = workspace.Source("a.patch_0", "AAA");
		var sourceB = workspace.Source("b.patch_0", "BBB");
		var sourceB2 = workspace.Source("b2.patch_0", "B2B2");
		var executor = new ApplyExecutor();
		var first = await executor.ExecuteAsync(workspace.Plan([], (workspace.Target(0), sourceA), (workspace.Target(1), sourceB)));
		Assert.True(first.Success, Describe(first));

		using var hold = new FileStream(workspace.Target(0), FileMode.Open, FileAccess.Read, FileShare.Read);
		var second = await executor.ExecuteAsync(workspace.Plan(workspace.ExistingPatches(), (workspace.Target(0), sourceA), (workspace.Target(1), sourceB2)));

		Assert.True(second.Success, Describe(second));
		Assert.Equal("AAA", await File.ReadAllTextAsync(workspace.Target(0)));
		Assert.Equal("B2B2", await File.ReadAllTextAsync(workspace.Target(1)));
		Assert.Single(second.Operations, result => result.Message == ApplyExecutor.UnchangedMessage);
	}

	[Fact]
	public async Task ExecuteAsync_ModTurnedOff_RemovesStaleTargetAndKeepsTheRest()
	{
		if (!OperatingSystem.IsWindows()) return;
		using var workspace = new Workspace();
		var sourceA = workspace.Source("a.patch_0", "AAA");
		var sourceB = workspace.Source("b.patch_0", "BBB");
		var executor = new ApplyExecutor();
		var first = await executor.ExecuteAsync(workspace.Plan([], (workspace.Target(0), sourceA), (workspace.Target(1), sourceB)));
		Assert.True(first.Success, Describe(first));

		using var hold = new FileStream(workspace.Target(0), FileMode.Open, FileAccess.Read, FileShare.Read);
		var second = await executor.ExecuteAsync(workspace.Plan(workspace.ExistingPatches(), (workspace.Target(0), sourceA)));

		Assert.True(second.Success, Describe(second));
		Assert.True(File.Exists(workspace.Target(0)));
		Assert.False(File.Exists(workspace.Target(1)));
		var state = await new JsonActivationStateStore().TryLoadAsync(workspace.GameData);
		Assert.Equal(workspace.Target(0), Assert.Single(state!.Files).TargetPath);
	}

	[Fact]
	public async Task ExecuteAsync_ForeignCopyWithIdenticalBytes_IsReplacedByLink()
	{
		if (!OperatingSystem.IsWindows()) return;
		using var workspace = new Workspace();
		var source = workspace.Source("a.patch_0", "AAA");
		var target = workspace.Target(0);
		// Same bytes, but a separate file: ownership is never inferred from content.
		await File.WriteAllTextAsync(target, "AAA");

		var result = await new ApplyExecutor().ExecuteAsync(workspace.Plan(workspace.ExistingPatches(), (target, source)));

		Assert.True(result.Success, Describe(result));
		Assert.DoesNotContain(result.Operations, item => item.Message == ApplyExecutor.UnchangedMessage);
		// A real hard link: a change made through the library file is visible at the target.
		await File.AppendAllTextAsync(source, "+");
		Assert.Equal("AAA+", await File.ReadAllTextAsync(target));
	}

	[Fact]
	public async Task ExecuteAsync_FirstDeploy_StillCreatesEveryLink()
	{
		using var workspace = new Workspace();
		var sourceA = workspace.Source("a.patch_0", "AAA");
		var sourceB = workspace.Source("b.patch_0", "BBB");

		var result = await new ApplyExecutor().ExecuteAsync(workspace.Plan([], (workspace.Target(0), sourceA), (workspace.Target(1), sourceB)));

		Assert.True(result.Success, Describe(result));
		Assert.Equal("AAA", await File.ReadAllTextAsync(workspace.Target(0)));
		Assert.Equal("BBB", await File.ReadAllTextAsync(workspace.Target(1)));
		Assert.DoesNotContain(result.Operations, item => item.Message == ApplyExecutor.UnchangedMessage);
	}

	private static string Describe(ApplyResult result)
		=> string.Join(" | ", result.Issues.Select(issue => issue.Code + ":" + issue.Message));
}
