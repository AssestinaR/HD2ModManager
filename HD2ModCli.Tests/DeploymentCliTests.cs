using System.Buffers.Binary;
using System.Text.Json;
using HD2ModAdaptation.PatchReconstruction;
using HD2ModAdaptation.PatchReconstruction.Lua;
using HD2ModCli;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using Xunit;
using AdaptationPatchEntryPayload = HD2ModAdaptation.PatchReconstruction.PatchEntryPayload;
using AdaptationPatchTocEntry = HD2ModAdaptation.PatchReconstruction.PatchTocEntry;

namespace HD2ModCli.Tests;

[CollectionDefinition("cli-console", DisableParallelization = true)]
public sealed class CliConsoleCollection;

[Collection("cli-console")]
public sealed class DeploymentCliTests
{
	[Fact]
	public async Task DeployPlanValidateApply_EmptyProfile_CompletesThroughTheFullGuardedWorkflow()
	{
		await using var fixture = await DeploymentFixture.CreateAsync();
		var planPath = Path.Combine(fixture.RootDirectory, "deployment-plan.json");

		var planExit = await CliApplication.RunAsync(
		[
			"deploy", "plan", "active",
			"--manager-root", fixture.ManagerRoot,
			"--game-data", fixture.GameDataDirectory,
			"--output", planPath,
			"--json",
		]);
		var validateExit = await CliApplication.RunAsync(
		[
			"deploy", "validate",
			"--manager-root", fixture.ManagerRoot,
			"--game-data", fixture.GameDataDirectory,
			"--plan", planPath,
			"--json",
		]);
		var applyExit = await CliApplication.RunAsync(
		[
			"deploy", "apply",
			"--manager-root", fixture.ManagerRoot,
			"--game-data", fixture.GameDataDirectory,
			"--plan", planPath,
			"--confirm",
			"--json",
		]);

		Assert.Equal(0, planExit);
		Assert.Equal(0, validateExit);
		Assert.Equal(0, applyExit);
		Assert.True(File.Exists(planPath));
		Assert.False(File.Exists(Path.Combine(fixture.GameDataDirectory, JsonActivationStateStore.StateFileName)));
	}

	[Fact]
	public async Task DeployPlanAndApply_RefuseUnmanagedPatchWithoutDeletingIt()
	{
		await using var fixture = await DeploymentFixture.CreateAsync();
		var foreignPatch = Path.Combine(fixture.GameDataDirectory, "0123456789abcdef.patch_0");
		await File.WriteAllTextAsync(foreignPatch, "FOREIGN");
		var planPath = Path.Combine(fixture.RootDirectory, "blocked-deployment-plan.json");

		var planExit = await CliApplication.RunAsync(
		[
			"deploy", "plan", "active",
			"--manager-root", fixture.ManagerRoot,
			"--game-data", fixture.GameDataDirectory,
			"--output", planPath,
			"--json",
		]);
		var applyExit = await CliApplication.RunAsync(
		[
			"deploy", "apply",
			"--manager-root", fixture.ManagerRoot,
			"--game-data", fixture.GameDataDirectory,
			"--plan", planPath,
			"--confirm",
			"--json",
		]);

		Assert.Equal(2, planExit);
		Assert.Equal(2, applyExit);
		Assert.Equal("FOREIGN", await File.ReadAllTextAsync(foreignPatch));
	}

	[Fact]
	public async Task DeployPlanValidateApply_OneValidLuaPatch_DeploysTrackedPatchFiles()
	{
		await using var fixture = await DeploymentFixture.CreateAsync(includeLuaPatch: true);
		var planPath = Path.Combine(fixture.RootDirectory, "lua-deployment-plan.json");

		var planExit = await CliApplication.RunAsync(
		[
			"deploy", "plan", "active",
			"--manager-root", fixture.ManagerRoot,
			"--game-data", fixture.GameDataDirectory,
			"--output", planPath,
			"--json",
		]);
		var applyExit = await CliApplication.RunAsync(
		[
			"deploy", "apply",
			"--manager-root", fixture.ManagerRoot,
			"--game-data", fixture.GameDataDirectory,
			"--plan", planPath,
			"--confirm",
			"--json",
		]);

		var state = await new JsonActivationStateStore().TryLoadAsync(fixture.GameDataDirectory);
		Assert.Equal(0, planExit);
		Assert.Equal(0, applyExit);
		Assert.NotNull(state);
		Assert.True(state!.Completed);
		Assert.Equal(3, state.Files.Count);
		Assert.All(state.Files, file => Assert.False(string.IsNullOrWhiteSpace(file.ContentSha256)));
		Assert.All(state.Files, file => Assert.True(File.Exists(file.TargetPath)));
	}

	[Fact]
	public async Task DeployPlanApply_TwoLuaPatches_MaterializesOneGeneratedPatchWithoutExecutingLua()
	{
		await using var fixture = await DeploymentFixture.CreateAsync(luaPatchCount: 2);
		var planPath = Path.Combine(fixture.RootDirectory, "merged-lua-deployment-plan.json");

		var planExit = await CliApplication.RunAsync(
		[
			"deploy", "plan", "active",
			"--manager-root", fixture.ManagerRoot,
			"--game-data", fixture.GameDataDirectory,
			"--output", planPath,
			"--json",
		]);
		var applyExit = await CliApplication.RunAsync(
		[
			"deploy", "apply",
			"--manager-root", fixture.ManagerRoot,
			"--game-data", fixture.GameDataDirectory,
			"--plan", planPath,
			"--confirm",
			"--json",
		]);

		var state = await new JsonActivationStateStore().TryLoadAsync(fixture.GameDataDirectory);
		Assert.Equal(0, planExit);
		Assert.Equal(0, applyExit);
		Assert.NotNull(state);
		Assert.Equal(3, state!.Files.Count);
		Assert.All(state.Files, file => Assert.Contains(Path.Combine(".runtime", "lua"), file.SourcePath, StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public async Task DeployDeactivatePlanValidateApply_RemovesOnlyTheTrackedDeployment()
	{
		await using var fixture = await DeploymentFixture.CreateAsync(includeLuaPatch: true);
		var deployPlanPath = Path.Combine(fixture.RootDirectory, "deploy.json");
		var deactivatePlanPath = Path.Combine(fixture.RootDirectory, "deactivate.json");

		Assert.Equal(0, await CliApplication.RunAsync(
		[
			"deploy", "plan", "active", "--manager-root", fixture.ManagerRoot, "--game-data", fixture.GameDataDirectory, "--output", deployPlanPath, "--json",
		]));
		Assert.Equal(0, await CliApplication.RunAsync(
		[
			"deploy", "apply", "--manager-root", fixture.ManagerRoot, "--game-data", fixture.GameDataDirectory, "--plan", deployPlanPath, "--confirm", "--json",
		]));
		Assert.Equal(0, await CliApplication.RunAsync(
		[
			"deploy", "deactivate", "plan", "--manager-root", fixture.ManagerRoot, "--game-data", fixture.GameDataDirectory, "--output", deactivatePlanPath, "--json",
		]));
		Assert.Equal(0, await CliApplication.RunAsync(
		[
			"deploy", "deactivate", "validate", "--manager-root", fixture.ManagerRoot, "--game-data", fixture.GameDataDirectory, "--plan", deactivatePlanPath, "--json",
		]));
		Assert.Equal(0, await CliApplication.RunAsync(
		[
			"deploy", "deactivate", "apply", "--manager-root", fixture.ManagerRoot, "--game-data", fixture.GameDataDirectory, "--plan", deactivatePlanPath, "--confirm", "--json",
		]));

		Assert.False(File.Exists(Path.Combine(fixture.GameDataDirectory, JsonActivationStateStore.StateFileName)));
		Assert.Empty(Directory.EnumerateFiles(fixture.GameDataDirectory, "*.patch_*", SearchOption.TopDirectoryOnly));
	}

	private sealed class DeploymentFixture : IAsyncDisposable
	{
		private DeploymentFixture(string rootDirectory, string managerRoot, string libraryRoot, string gameDataDirectory)
		{
			RootDirectory = rootDirectory;
			ManagerRoot = managerRoot;
			LibraryRoot = libraryRoot;
			GameDataDirectory = gameDataDirectory;
		}

		public string RootDirectory { get; }
		public string ManagerRoot { get; }
		public string LibraryRoot { get; }
		public string GameDataDirectory { get; }

		public static ValueTask<DeploymentFixture> CreateAsync(bool includeLuaPatch = false)
			=> CreateAsync(includeLuaPatch ? 1 : 0);

		public static async ValueTask<DeploymentFixture> CreateAsync(int luaPatchCount)
		{
			var root = Path.Combine(Path.GetTempPath(), "hd2-cli-deploy-" + Guid.NewGuid().ToString("N"));
			var manager = Path.Combine(root, "manager");
			var library = Path.Combine(root, "library");
			var gameRoot = Path.Combine(root, "Helldivers 2");
			var gameData = Path.Combine(gameRoot, "data");
			Directory.CreateDirectory(manager);
			Directory.CreateDirectory(Path.Combine(gameRoot, "bin"));
			Directory.CreateDirectory(gameData);
			await File.WriteAllTextAsync(Path.Combine(gameRoot, "bin", "helldivers2.exe"), string.Empty);
			await File.WriteAllTextAsync(
				Path.Combine(manager, "settings.json"),
				JsonSerializer.Serialize(new { modLibraryFolder = library, gameDataFolder = gameData }));

			var nodes = new Dictionary<ModNodeId, ModNode>();
			var entries = new List<ProfileEntry>();
			for (var index = 0; index < luaPatchCount; index++)
			{
				var nodeId = ModNodeId.New();
				var relativePath = $"lua-mod-{index + 1}";
				var modDirectory = Path.Combine(library, relativePath);
				Directory.CreateDirectory(modDirectory);
				var patchPath = Path.Combine(modDirectory, $"{LuaAssetKeys.BootArchiveId:x16}.patch_0");
				await WriteFixtureLuaPatchAsync(patchPath);
				nodes[nodeId] = new ModNode(
					nodeId,
					relativePath,
					new ModNodeMetadata("Lua Fixture", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
					[new PatchGroupKey($"{LuaAssetKeys.BootArchiveId:x16}", 0)],
					[]);
				entries.Add(new ProfileEntry(nodeId, 0, DateTimeOffset.UtcNow));
			}
			var profile = new Profile(ProfileId.New(), "Default", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, entries);
			var snapshot = new LibrarySnapshot(1, DateTimeOffset.UtcNow, nodes, [profile], profile.Id);
			await new JsonModLibraryStore(new StoragePaths(manager, library), allowLegacyMigration: false).SaveAsync(snapshot);
			return new DeploymentFixture(root, manager, library, gameData);
		}

		private static async ValueTask WriteFixtureLuaPatchAsync(string patchPath)
		{
			var header = new byte[0xc0];
			BinaryPrimitives.WriteUInt32LittleEndian(header, 0xF0000011);
			BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 1);
			BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), 1);
			// One legacy TOC type record at 0x3c and one entry at 0x5c.
			BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(0x44), LuaAssetKeys.LuaTypeId);
			BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(0x4c), 1);
			BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(0x5c), LuaAssetKeys.BootFileId);
			BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(0x64), LuaAssetKeys.LuaTypeId);
			var entry = new AdaptationPatchTocEntry(LuaAssetKeys.BootAssetKey, patchPath, Path.GetFileName(patchPath));
			var document = new LuaPatchDocument(
				patchPath,
				entry,
				new AdaptationPatchEntryPayload(entry, [], [], []),
				header,
				[0x1b, 0x4c, 0x4a],
				[new LuaPatchBlock("fixture", string.Empty, "local fixture = true", false)],
				LuaPatchWrapperKind.Single,
				null,
				string.Empty);
			var composer = new LuaPatchComposer();
			var composition = composer.Compose([document]);
			await composer.WriteAsync(patchPath, composition);
		}

		public ValueTask DisposeAsync()
		{
			try { Directory.Delete(RootDirectory, recursive: true); } catch { }
			return ValueTask.CompletedTask;
		}
	}
}
