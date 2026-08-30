using System.Text.Json;
using HD2ModCli;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using Xunit;

namespace HD2ModCli.Tests;

[Collection("cli-console")]
public sealed class HelmetToArmorMergeCliTests
{
	[Fact]
	public async Task HelmetMergePlan_WithNoUsableCandidate_WritesAuditableBlockedPlan()
	{
		await using var fixture = await HelmetMergeFixture.CreateAsync();
		var planPath = Path.Combine(fixture.RootDirectory, "helmet-merge-plan.json");

		var exitCode = await RunAsync(
			"equipment", "helmet-merge", "plan", fixture.NodeSelector,
			"--manager-root", fixture.ManagerRoot,
			"--library", fixture.LibraryRoot,
			"--game-data", fixture.GameDataDirectory,
			"--output", planPath,
			"--json");

		Assert.Equal(2, exitCode);
		Assert.True(File.Exists(planPath));
		using var plan = JsonDocument.Parse(await File.ReadAllTextAsync(planPath));
		Assert.Equal("hd2mod.helmet-to-armor-merge-plan", plan.RootElement.GetProperty("schema").GetString());
		Assert.Equal(fixture.NodeSelector, plan.RootElement.GetProperty("request").GetProperty("sourceNodeIds")[0].GetString());
		Assert.Equal(0, plan.RootElement.GetProperty("candidates").GetArrayLength());
		Assert.Contains(
			plan.RootElement.GetProperty("diagnostics").EnumerateArray().Select(item => item.GetProperty("code").GetString()),
			code => string.Equals(code, "HelmetMergeCandidatesMissing", StringComparison.Ordinal));
	}

	[Fact]
	public async Task HelmetMergeValidate_WithBlockedPlan_ReturnsFailureWithoutWritingOutput()
	{
		await using var fixture = await HelmetMergeFixture.CreateAsync();
		var planPath = Path.Combine(fixture.RootDirectory, "helmet-merge-plan.json");
		Assert.Equal(2, await RunAsync(
			"equipment", "helmet-merge", "plan", fixture.NodeSelector,
			"--manager-root", fixture.ManagerRoot,
			"--library", fixture.LibraryRoot,
			"--game-data", fixture.GameDataDirectory,
			"--output", planPath,
			"--json"));

		var outputDirectory = Path.Combine(fixture.RootDirectory, "must-not-exist");
		var exitCode = await RunAsync(
			"equipment", "helmet-merge", "validate",
			"--plan", planPath,
			"--manager-root", fixture.ManagerRoot,
			"--library", fixture.LibraryRoot,
			"--game-data", fixture.GameDataDirectory,
			"--json");

		Assert.Equal(2, exitCode);
		Assert.False(Directory.Exists(outputDirectory));
	}

	[Fact]
	public async Task HelmetMergeApply_RequiresConfirmBeforeReadingPlanOrWritingOutput()
	{
		await using var fixture = await HelmetMergeFixture.CreateAsync();
		var outputDirectory = Path.Combine(fixture.RootDirectory, "must-not-exist");

		var exitCode = await RunAsync(
			"equipment", "helmet-merge", "apply",
			"--plan", Path.Combine(fixture.RootDirectory, "not-even-read.json"),
			"--manager-root", fixture.ManagerRoot,
			"--library", fixture.LibraryRoot,
			"--game-data", fixture.GameDataDirectory,
			"--output", outputDirectory,
			"--json");

		Assert.Equal(2, exitCode);
		Assert.False(Directory.Exists(outputDirectory));
	}

	private static Task<int> RunAsync(params string[] args) => CliApplication.RunAsync(args);

	private sealed class HelmetMergeFixture : IAsyncDisposable
	{
		private HelmetMergeFixture(string rootDirectory, string managerRoot, string libraryRoot, string gameDataDirectory, ModNodeId nodeId)
		{
			RootDirectory = rootDirectory;
			ManagerRoot = managerRoot;
			LibraryRoot = libraryRoot;
			GameDataDirectory = gameDataDirectory;
			NodeId = nodeId;
		}

		public string RootDirectory { get; }
		public string ManagerRoot { get; }
		public string LibraryRoot { get; }
		public string GameDataDirectory { get; }
		public ModNodeId NodeId { get; }
		public string NodeSelector => NodeId.Value.ToString("D");

		public static async ValueTask<HelmetMergeFixture> CreateAsync()
		{
			var root = Path.Combine(Path.GetTempPath(), "hd2-cli-helmet-merge-" + Guid.NewGuid().ToString("N"));
			var managerRoot = Path.Combine(root, "manager");
			var libraryRoot = Path.Combine(root, "library");
			var gameRoot = Path.Combine(root, "Helldivers 2");
			var gameDataDirectory = Path.Combine(gameRoot, "data");
			var relativePath = "helmet-merge-source";
			var sourceDirectory = Path.Combine(libraryRoot, relativePath);
			var nodeId = ModNodeId.New();
			Directory.CreateDirectory(managerRoot);
			Directory.CreateDirectory(sourceDirectory);
			Directory.CreateDirectory(gameDataDirectory);
			await File.WriteAllTextAsync(
				Path.Combine(managerRoot, "settings.json"),
				JsonSerializer.Serialize(new { ModLibraryFolder = libraryRoot, GameDataFolder = gameDataDirectory }));
			await File.WriteAllBytesAsync(Path.Combine(sourceDirectory, "0123456789abcdef.patch_0"), Array.Empty<byte>());

			var now = DateTimeOffset.UtcNow;
			var node = new ModNode(
				nodeId,
				relativePath,
				new ModNodeMetadata("Helmet merge CLI fixture", null, now, now),
				[new PatchGroupKey("0123456789abcdef", 0)],
				[]);
			var snapshot = new LibrarySnapshot(
				1,
				now,
				new Dictionary<ModNodeId, ModNode> { [nodeId] = node },
				Array.Empty<Profile>());
			await new JsonModLibraryStore(new StoragePaths(managerRoot, libraryRoot), allowLegacyMigration: false).SaveAsync(snapshot);
			return new HelmetMergeFixture(root, managerRoot, libraryRoot, gameDataDirectory, nodeId);
		}

		public ValueTask DisposeAsync()
		{
			try { Directory.Delete(RootDirectory, recursive: true); }
			catch { }
			return ValueTask.CompletedTask;
		}
	}
}
