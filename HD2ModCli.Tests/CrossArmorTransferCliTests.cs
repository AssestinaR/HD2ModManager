using System.Text.Json;
using HD2ModCli;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using Xunit;

namespace HD2ModCli.Tests;

[Collection("cli-console")]
public sealed class CrossArmorTransferCliTests
{
	[Fact]
	public async Task CustomModelConvert_RequiresConfirmationBeforeAnyWrites()
	{
		await using var fixture = await CrossArmorFixture.CreateAsync();
		var output = Path.Combine(fixture.RootDirectory, "must-not-exist");
		var exitCode = await RunAsync("custom-model", "convert", fixture.NodeSelector, "0123456789abcdef", "Test",
			"--manager-root", fixture.ManagerRoot, "--library", fixture.LibraryRoot,
			"--game-data", fixture.GameDataDirectory, "--output", output, "--json");
		Assert.Equal(2, exitCode);
		Assert.False(Directory.Exists(output));
		Assert.False(File.Exists(new RuntimeArmorRegistryService(new StoragePaths(fixture.ManagerRoot, fixture.LibraryRoot)).WorkspacePath));
	}

	[Fact]
	public async Task CrossArmorPlan_IncludesCurrentLibraryCustomModelAndPersistentIds()
	{
		await using var fixture = await CrossArmorFixture.CreateAsync();
		var service = new RuntimeArmorRegistryService(new StoragePaths(fixture.ManagerRoot, fixture.LibraryRoot));
		await service.SaveWorkspaceAsync(new(0, false, [], [
			new("custom-test", null, [new(3, "fedcba9876543210", 2, 0)], "自定义素体")
		]));
		var before = await File.ReadAllTextAsync(service.WorkspacePath);
		var planPath = Path.Combine(fixture.RootDirectory, "custom-plan.json");
		Assert.Equal(2, await RunAsync("equipment", "cross-armor", "plan", fixture.NodeSelector,
			"--manager-root", fixture.ManagerRoot, "--library", fixture.LibraryRoot,
			"--game-data", fixture.GameDataDirectory, "--output", planPath, "--json"));
		using var plan = JsonDocument.Parse(await File.ReadAllTextAsync(planPath));
		var custom = Assert.Single(plan.RootElement.GetProperty("targetCandidates").EnumerateArray(),
			item => item.GetProperty("category").GetString() == "CustomModel");
		Assert.Equal("custom-model:custom-test", custom.GetProperty("archiveId").GetString());
		Assert.Equal("custom-test", custom.GetProperty("customModelKey").GetString());
		Assert.Contains("fedcba9876543210", custom.GetProperty("parts")[0].GetProperty("assetKey").GetString());
		Assert.Equal(before, await File.ReadAllTextAsync(service.WorkspacePath));
	}

	[Fact]
	public async Task CrossArmorInspect_WithNoUsableEquipment_ReturnsReadOnlyInspection()
	{
		await using var fixture = await CrossArmorFixture.CreateAsync();

		var exitCode = await RunAsync(
			"equipment", "cross-armor", "inspect", fixture.NodeSelector,
			"--manager-root", fixture.ManagerRoot,
			"--library", fixture.LibraryRoot,
			"--game-data", fixture.GameDataDirectory,
			"--json");

		Assert.Equal(0, exitCode);
		Assert.False(File.Exists(Path.Combine(fixture.RootDirectory, "unexpected-plan.json")));
	}

	[Fact]
	public async Task CrossArmorPlan_WithNoUsableEquipment_WritesAuditableBlockedPlan()
	{
		await using var fixture = await CrossArmorFixture.CreateAsync();
		var planPath = Path.Combine(fixture.RootDirectory, "cross-armor-plan.json");

		var exitCode = await RunAsync(
			"equipment", "cross-armor", "plan", fixture.NodeSelector,
			"--manager-root", fixture.ManagerRoot,
			"--library", fixture.LibraryRoot,
			"--game-data", fixture.GameDataDirectory,
			"--output", planPath,
			"--json");

		Assert.Equal(2, exitCode);
		Assert.True(File.Exists(planPath));
		Assert.False(Directory.Exists(Path.Combine(fixture.ManagerRoot, "data")));
		using var plan = JsonDocument.Parse(await File.ReadAllTextAsync(planPath));
		Assert.Equal("hd2mod.cross-armor-transfer-plan", plan.RootElement.GetProperty("schema").GetString());
		Assert.Equal(fixture.NodeSelector, plan.RootElement.GetProperty("request").GetProperty("sourceNodeId").GetString());
		Assert.Equal("0123456789abcdef.patch_0", plan.RootElement.GetProperty("request").GetProperty("sourcePatchFileName").GetString());
		Assert.Equal(0, plan.RootElement.GetProperty("request").GetProperty("selectedTargetArchiveIds").GetArrayLength());
		Assert.Contains(
			plan.RootElement.GetProperty("diagnostics").EnumerateArray().Select(item => item.GetProperty("code").GetString()),
			code => string.Equals(code, "CrossArmorTargetRequired", StringComparison.Ordinal));
	}

	[Fact]
	public async Task CrossArmorValidate_WithBlockedPlan_ReturnsFailureWithoutWritingOutput()
	{
		await using var fixture = await CrossArmorFixture.CreateAsync();
		var planPath = Path.Combine(fixture.RootDirectory, "cross-armor-plan.json");
		Assert.Equal(2, await RunAsync(
			"equipment", "cross-armor", "plan", fixture.NodeSelector,
			"--manager-root", fixture.ManagerRoot,
			"--library", fixture.LibraryRoot,
			"--game-data", fixture.GameDataDirectory,
			"--output", planPath,
			"--json"));

		var outputDirectory = Path.Combine(fixture.RootDirectory, "must-not-exist");
		var exitCode = await RunAsync(
			"equipment", "cross-armor", "validate",
			"--plan", planPath,
			"--manager-root", fixture.ManagerRoot,
			"--library", fixture.LibraryRoot,
			"--game-data", fixture.GameDataDirectory,
			"--json");

		Assert.Equal(2, exitCode);
		Assert.False(Directory.Exists(outputDirectory));
	}

	[Fact]
	public async Task CrossArmorApply_RequiresConfirmBeforeReadingPlanOrWritingOutput()
	{
		await using var fixture = await CrossArmorFixture.CreateAsync();
		var outputDirectory = Path.Combine(fixture.RootDirectory, "must-not-exist");

		var exitCode = await RunAsync(
			"equipment", "cross-armor", "apply",
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

	private sealed class CrossArmorFixture : IAsyncDisposable
	{
		private CrossArmorFixture(string rootDirectory, string managerRoot, string libraryRoot, string gameDataDirectory, ModNodeId nodeId)
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

		public static async ValueTask<CrossArmorFixture> CreateAsync()
		{
			var root = Path.Combine(Path.GetTempPath(), "hd2-cli-cross-armor-" + Guid.NewGuid().ToString("N"));
			var managerRoot = Path.Combine(root, "manager");
			var libraryRoot = Path.Combine(root, "library");
			var gameRoot = Path.Combine(root, "Helldivers 2");
			var gameDataDirectory = Path.Combine(gameRoot, "data");
			var relativePath = "cross-armor-source";
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
				new ModNodeMetadata("Cross armor CLI fixture", null, now, now),
				[new PatchGroupKey("0123456789abcdef", 0)],
				[]);
			var snapshot = new LibrarySnapshot(
				1,
				now,
				new Dictionary<ModNodeId, ModNode> { [nodeId] = node },
				Array.Empty<Profile>());
			await new JsonModLibraryStore(new StoragePaths(managerRoot, libraryRoot), allowLegacyMigration: false).SaveAsync(snapshot);
			return new CrossArmorFixture(root, managerRoot, libraryRoot, gameDataDirectory, nodeId);
		}

		public ValueTask DisposeAsync()
		{
			try { Directory.Delete(RootDirectory, recursive: true); }
			catch { }
			return ValueTask.CompletedTask;
		}
	}
}
