using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

// 作用：验证 ApplyExecutor 部署前会强制删除严格匹配的旧 patch，但不会触碰非 patch 文件。
// Purpose: Verifies ApplyExecutor force-deletes strict old patch files before deployment without touching non-patch files.
public sealed class ApplyExecutorCleanupSafetyTests
{
	[Fact]
	public async Task ExecuteAsync_DeletePatch_RemovesPatchButKeepsNonPatchFile()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		var gameData = Path.Combine(root, "game", "data");
		Directory.CreateDirectory(gameData);

		var patch = Path.Combine(gameData, "9ba626afa44a3aa3.patch_0");
		var nonPatch = Path.Combine(gameData, "readme.txt");
		await File.WriteAllTextAsync(patch, "OLD");
		await File.WriteAllTextAsync(nonPatch, "KEEP");

		try
		{
			var plan = new ApplyPlan(gameData, null, 0, DateTimeOffset.UtcNow, new[]
			{
				new ApplyOperation(ApplyOperationKind.DeletePatch, patch, null, null, null, null, null, null),
			}, Array.Empty<CoreIssue>());
			var exec = new ApplyExecutor();
			var result = await exec.ExecuteAsync(plan);

			Assert.True(result.Success);
			Assert.False(File.Exists(patch));
			Assert.True(File.Exists(nonPatch));
			Assert.Equal("KEEP", await File.ReadAllTextAsync(nonPatch));
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}

	[Fact]
	public async Task ExecuteAsync_DeletePatch_RemovesReadOnlyHardLinkWithoutChangingSourceAttributes()
	{
		if (!OperatingSystem.IsWindows()) return;
		var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		var gameData = Path.Combine(root, "game", "data");
		var sourceDirectory = Path.Combine(root, "mods");
		Directory.CreateDirectory(gameData);
		Directory.CreateDirectory(sourceDirectory);
		var source = Path.Combine(sourceDirectory, "9ba626afa44a3aa3.patch_4");
		var target = Path.Combine(gameData, "9ba626afa44a3aa3.patch_0");
		await File.WriteAllTextAsync(source, "LINKED");
		try
		{
			var deployPlan = new ApplyPlan(gameData, ProfileId.New(), 1, DateTimeOffset.UtcNow,
			[
				new ApplyOperation(ApplyOperationKind.DeployPatch, target, source, "9ba626afa44a3aa3", 4, 0, PatchSidecarKind.Base, ModNodeId.New()),
			], [], DeploymentMethod.HardLink);
			var executor = new ApplyExecutor();
			var deployResult = await executor.ExecuteAsync(deployPlan);
			Assert.True(deployResult.Success, string.Join(" | ", deployResult.Issues.Select(issue => issue.Code + ":" + issue.Message)));
			var deletePlan = new ApplyPlan(gameData, ProfileId.New(), 2, DateTimeOffset.UtcNow,
			[
				new ApplyOperation(ApplyOperationKind.DeletePatch, target, null, null, null, null, null, null),
			], []);
			var deleteResult = await executor.ExecuteAsync(deletePlan);
			Assert.True(deleteResult.Success, string.Join(" | ", deleteResult.Issues.Select(issue => issue.Code + ":" + issue.Message)));
			Assert.False(File.Exists(target));
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}

	[Fact]
	public async Task ExecuteAsync_PreflightFailure_DoesNotChangeExistingData()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		var gameData = Path.Combine(root, "game", "data");
		Directory.CreateDirectory(gameData);
		var existingPatch = Path.Combine(gameData, "9ba626afa44a3aa3.patch_0");
		await File.WriteAllTextAsync(existingPatch, "OLD");
		try
		{
			var plan = new ApplyPlan(gameData, ProfileId.New(), 3, DateTimeOffset.UtcNow,
			[
				new ApplyOperation(ApplyOperationKind.DeletePatch, existingPatch, null, null, null, null, null, null),
				new ApplyOperation(ApplyOperationKind.DeployPatch, Path.Combine(gameData, "9ba626afa44a3aa3.patch_0"), Path.Combine(root, "missing.patch_0"), "9ba626afa44a3aa3", 0, 0, PatchSidecarKind.Base, ModNodeId.New()),
			], []);

			var result = await new ApplyExecutor().ExecuteAsync(plan);

			Assert.False(result.Success);
			Assert.Equal("OLD", await File.ReadAllTextAsync(existingPatch));
			Assert.False(File.Exists(Path.Combine(gameData, JsonActivationStateStore.StateFileName)));
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}

	[Fact]
	public async Task ExecuteAsync_CommitFailure_CleansOnlyTargetsOwnedByItsPlan()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		var gameData = Path.Combine(root, "game", "data");
		var sourceDirectory = Path.Combine(root, "mods");
		Directory.CreateDirectory(gameData);
		Directory.CreateDirectory(sourceDirectory);
		var source = Path.Combine(sourceDirectory, "9ba626afa44a3aa3.patch_4");
		var controlledTarget = Path.Combine(gameData, "9ba626afa44a3aa3.patch_0");
		var foreignPatch = Path.Combine(gameData, "9ba626afa44a3aa3.patch_9");
		var failingTarget = Path.Combine(gameData, "9ba626afa44a3aa3.patch_1");
		await File.WriteAllTextAsync(source, "NEW");
		await File.WriteAllTextAsync(foreignPatch, "FOREIGN");
		Directory.CreateDirectory(failingTarget);
		await File.WriteAllTextAsync(Path.Combine(failingTarget, "sentinel.txt"), "keep directory non-empty");
		try
		{
			var plan = new ApplyPlan(gameData, ProfileId.New(), 1, DateTimeOffset.UtcNow,
			[
				new ApplyOperation(ApplyOperationKind.DeployPatch, controlledTarget, source, "9ba626afa44a3aa3", 4, 0, PatchSidecarKind.Base, ModNodeId.New()),
				new ApplyOperation(ApplyOperationKind.DeployPatch, failingTarget, source, "9ba626afa44a3aa3", 4, 1, PatchSidecarKind.Base, ModNodeId.New()),
			], []);

			var result = await new ApplyExecutor().ExecuteAsync(plan);

			Assert.False(result.Success);
			Assert.False(File.Exists(controlledTarget));
			Assert.True(File.Exists(foreignPatch));
			Assert.Equal("FOREIGN", await File.ReadAllTextAsync(foreignPatch));
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}

	[Fact]
	public async Task ExecuteAsync_Success_PublishesVerifiedActivationState()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		var gameData = Path.Combine(root, "game", "data");
		var sourceDirectory = Path.Combine(root, "mods");
		Directory.CreateDirectory(gameData);
		Directory.CreateDirectory(sourceDirectory);
		var source = Path.Combine(sourceDirectory, "9ba626afa44a3aa3.patch_4");
		var target = Path.Combine(gameData, "9ba626afa44a3aa3.patch_0");
		await File.WriteAllTextAsync(source, "NEW");
		var profileId = ProfileId.New();
		try
		{
			var plan = new ApplyPlan(gameData, profileId, 9, DateTimeOffset.UtcNow,
			[
				new ApplyOperation(ApplyOperationKind.DeployPatch, target, source, "9ba626afa44a3aa3", 4, 0, PatchSidecarKind.Base, ModNodeId.New()),
			], []);

			var result = await new ApplyExecutor().ExecuteAsync(plan);
			var state = await new JsonActivationStateStore().TryLoadAsync(gameData);

			Assert.True(result.Success);
			Assert.Equal("NEW", await File.ReadAllTextAsync(target));
			Assert.NotNull(state);
			Assert.True(state!.Completed);
			Assert.Equal(profileId, state.ProfileId);
			Assert.Equal(9, state.ProfileRevision);
			var file = Assert.Single(state.Files);
			Assert.Equal(4, file.SourcePatchIndex);
			Assert.Equal(0, file.TargetPatchIndex);
			Assert.False(string.IsNullOrWhiteSpace(file.ContentSha256));
			Assert.Equal(
				Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("NEW"))).ToLowerInvariant(),
				file.ContentSha256);
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}

	[Fact]
	public async Task DeactivateAsync_IsIdempotent_AndKeepsNonPatchFiles()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		var patch = Path.Combine(root, "9ba626afa44a3aa3.patch_0");
		var stream = patch + ".stream";
		var nonPatch = Path.Combine(root, "readme.txt");
		await File.WriteAllTextAsync(patch, "P");
		await File.WriteAllTextAsync(stream, "S");
		await File.WriteAllTextAsync(nonPatch, "KEEP");
		await new JsonActivationStateStore().SaveAsync(root, new ActivationState(2, null, 0, DateTimeOffset.UtcNow, true,
		[
			new ActivationStateFileEntry(patch, Path.Combine(root, "source.patch"), DeploymentMethod.Copy, "9ba626afa44a3aa3", 0, 0, PatchSidecarKind.Base, null, 1, string.Empty),
			new ActivationStateFileEntry(stream, Path.Combine(root, "source.patch.stream"), DeploymentMethod.Copy, "9ba626afa44a3aa3", 0, 0, PatchSidecarKind.Stream, null, 1, string.Empty),
		], []));
		try
		{
			var executor = new ApplyExecutor();
			var first = await executor.DeactivateAsync(root);
			var second = await executor.DeactivateAsync(root);

			Assert.True(first.Success);
			Assert.True(second.Success);
			Assert.False(File.Exists(patch));
			Assert.False(File.Exists(stream));
			Assert.False(File.Exists(Path.Combine(root, JsonActivationStateStore.StateFileName)));
			Assert.Equal("KEEP", await File.ReadAllTextAsync(nonPatch));
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}

	[Fact]
	public async Task DeactivateAsync_DoesNotDeletePatchThatIsNotRecordedInActivationState()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		var owned = Path.Combine(root, "9ba626afa44a3aa3.patch_0");
		var foreign = Path.Combine(root, "0123456789abcdef.patch_0");
		await File.WriteAllTextAsync(owned, "OWNED");
		await File.WriteAllTextAsync(foreign, "FOREIGN");
		try
		{
			await new JsonActivationStateStore().SaveAsync(root, new ActivationState(2, null, 0, DateTimeOffset.UtcNow, true,
			[
				new ActivationStateFileEntry(owned, Path.Combine(root, "source.patch"), DeploymentMethod.Copy, "9ba626afa44a3aa3", 0, 0, PatchSidecarKind.Base, null, 5, string.Empty),
			], []));

			var result = await new ApplyExecutor().DeactivateAsync(root);

			Assert.True(result.Success, string.Join(" | ", result.Issues.Select(issue => issue.Code + ":" + issue.Message)));
			Assert.False(File.Exists(owned));
			Assert.Equal("FOREIGN", await File.ReadAllTextAsync(foreign));
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}

	[Fact]
	public async Task ExecuteAsync_SymbolicLink_VerifiesResolvedTargetInsteadOfLinkMetadataLength()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2coretests", Guid.NewGuid().ToString("N"));
		var gameData = Path.Combine(root, "game", "data");
		var sourceDirectory = Path.Combine(root, "mods");
		Directory.CreateDirectory(gameData);
		Directory.CreateDirectory(sourceDirectory);
		var source = Path.Combine(sourceDirectory, "9ba626afa44a3aa3.patch_4");
		var target = Path.Combine(gameData, "9ba626afa44a3aa3.patch_0");
		await File.WriteAllTextAsync(source, new string('S', 4096));
		try
		{
			var plan = new ApplyPlan(gameData, ProfileId.New(), 1, DateTimeOffset.UtcNow,
			[
				new ApplyOperation(ApplyOperationKind.DeployPatch, target, source, "9ba626afa44a3aa3", 4, 0, PatchSidecarKind.Base, ModNodeId.New()),
			], [], DeploymentMethod.SymbolicLink);

			var result = await new ApplyExecutor().ExecuteAsync(plan);
			var state = await new JsonActivationStateStore().TryLoadAsync(gameData);

			Assert.True(result.Success);
			Assert.Equal(DeploymentMethod.SymbolicLink, Assert.Single(state!.Files).Method);
			Assert.Equal(Path.GetFullPath(source), Path.GetFullPath(File.ResolveLinkTarget(target, true)!.FullName));
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); } catch { }
		}
	}
}
