using System.Text.Json;
using HD2ModCli;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using Xunit;

namespace HD2ModCli.Tests;

[Collection("cli-console")]
public sealed class SameKeyReconstructionCliTests
{
	[Fact]
	public async Task PatchRepairInspect_WithoutSourcePatch_ReturnsSuccessWithoutWritingPlan()
	{
		await using var fixture = await SameKeyReconstructionFixture.CreateAsync();
		var unexpectedPlanPath = Path.Combine(fixture.RootDirectory, "unexpected-plan.json");

		var exitCode = await RunAsync(
			"patch", "repair", "inspect", fixture.NodeSelector,
			"--manager-root", fixture.ManagerRoot,
			"--library", fixture.LibraryRoot,
			"--game-data", fixture.GameDataDirectory,
			"--json");

		Assert.Equal(0, exitCode);
		Assert.False(File.Exists(unexpectedPlanPath));
	}

	[Fact]
	public async Task PatchRepairPlan_WithExplicitSourcePatchAndMissingGameDataIndex_WritesNotReadyPlan()
	{
		await using var fixture = await SameKeyReconstructionFixture.CreateAsync();
		var planPath = Path.Combine(fixture.RootDirectory, "same-key-plan.json");

		var exitCode = await RunAsync(
			"patch", "repair", "plan", fixture.NodeSelector, SameKeyReconstructionFixture.FirstBasePatchFileName,
			"--manager-root", fixture.ManagerRoot,
			"--library", fixture.LibraryRoot,
			"--game-data", fixture.GameDataDirectory,
			"--output", planPath,
			"--json");

		Assert.Equal(2, exitCode);
		Assert.True(File.Exists(planPath));

		using var plan = JsonDocument.Parse(await File.ReadAllTextAsync(planPath));
		var request = plan.RootElement.GetProperty("request");
		Assert.Equal(fixture.NodeSelector, request.GetProperty("sourceNodeId").GetString());
		Assert.Equal(SameKeyReconstructionFixture.FirstBasePatchFileName, request.GetProperty("sourcePatchFileName").GetString());
	}

	[Theory]
	[InlineData(@"nested\0123456789abcdef.patch_0", "with-directory")]
	[InlineData("1111111111111111.patch_0", "not-owned")]
	public async Task PatchRepairPlan_WithInvalidSourcePatchName_RejectsWithoutWritingPlan(string sourcePatchFileName, string outputName)
	{
		await using var fixture = await SameKeyReconstructionFixture.CreateAsync();
		var planPath = Path.Combine(fixture.RootDirectory, $"invalid-{outputName}.json");

		var exitCode = await RunAsync(
			"patch", "repair", "plan", fixture.NodeSelector, sourcePatchFileName,
			"--manager-root", fixture.ManagerRoot,
			"--library", fixture.LibraryRoot,
			"--game-data", fixture.GameDataDirectory,
			"--output", planPath,
			"--json");

		Assert.Equal(2, exitCode);
		Assert.False(File.Exists(planPath));
	}

	private static Task<int> RunAsync(params string[] args) => CliApplication.RunAsync(args);

	private sealed class SameKeyReconstructionFixture : IAsyncDisposable
	{
		private SameKeyReconstructionFixture(
			string rootDirectory,
			string managerRoot,
			string libraryRoot,
			string gameDataDirectory,
			ModNodeId nodeId)
		{
			RootDirectory = rootDirectory;
			ManagerRoot = managerRoot;
			LibraryRoot = libraryRoot;
			GameDataDirectory = gameDataDirectory;
			NodeId = nodeId;
		}

		public const string FirstBasePatchFileName = "0123456789abcdef.patch_0";
		public const string SecondBasePatchFileName = "fedcba9876543210.patch_0";

		public string RootDirectory { get; }
		public string ManagerRoot { get; }
		public string LibraryRoot { get; }
		public string GameDataDirectory { get; }
		public ModNodeId NodeId { get; }
		public string NodeSelector => NodeId.Value.ToString("D");

		public static async ValueTask<SameKeyReconstructionFixture> CreateAsync()
		{
			var root = Path.Combine(Path.GetTempPath(), "hd2-cli-same-key-" + Guid.NewGuid().ToString("N"));
			var managerRoot = Path.Combine(root, "manager");
			var libraryRoot = Path.Combine(root, "library");
			var gameDataDirectory = Path.Combine(root, "game-data");
			var relativePath = "same-key-source";
			var modDirectory = Path.Combine(libraryRoot, relativePath);
			var nodeId = ModNodeId.New();
			Directory.CreateDirectory(managerRoot);
			Directory.CreateDirectory(gameDataDirectory);
			Directory.CreateDirectory(modDirectory);
			await File.WriteAllBytesAsync(Path.Combine(modDirectory, FirstBasePatchFileName), Array.Empty<byte>());
			await File.WriteAllBytesAsync(Path.Combine(modDirectory, SecondBasePatchFileName), Array.Empty<byte>());

			var now = DateTimeOffset.UtcNow;
			var node = new ModNode(
				nodeId,
				relativePath,
				new ModNodeMetadata("Same-key CLI Fixture", null, now, now),
				[
					new PatchGroupKey("0123456789abcdef", 0),
					new PatchGroupKey("fedcba9876543210", 0),
				],
				[]);
			var snapshot = new LibrarySnapshot(
				1,
				now,
				new Dictionary<ModNodeId, ModNode> { [nodeId] = node },
				Array.Empty<Profile>());
			await new JsonModLibraryStore(new StoragePaths(managerRoot, libraryRoot), allowLegacyMigration: false).SaveAsync(snapshot);

			return new SameKeyReconstructionFixture(root, managerRoot, libraryRoot, gameDataDirectory, nodeId);
		}

		public ValueTask DisposeAsync()
		{
			try { Directory.Delete(RootDirectory, recursive: true); }
			catch { }
			return ValueTask.CompletedTask;
		}
	}
}
