using System.Text.Json;
using HD2ModCli;
using Xunit;

namespace HD2ModCli.Tests;

[Collection("cli-console")]
public sealed class GameDataIndexManagementCliTests
{
	[Fact]
	public async Task IndexPlanRebuild_WithMissingIndex_WritesReadyAuditablePlan()
	{
		await using var fixture = await IndexFixture.CreateAsync();
		var planPath = Path.Combine(fixture.RootDirectory, "index-plan.json");

		var exitCode = await RunAsync(
			"gamedata", "index", "plan", "rebuild",
			"--manager-root", fixture.ManagerRoot,
			"--game-data", fixture.GameDataDirectory,
			"--output", planPath,
			"--json");

		Assert.Equal(0, exitCode);
		Assert.True(File.Exists(planPath));
		Assert.False(File.Exists(Path.Combine(fixture.ManagerRoot, "data", "indexes", "asset-index.sqlite")));
		using var plan = JsonDocument.Parse(await File.ReadAllTextAsync(planPath));
		Assert.Equal("hd2mod.gamedata-index-plan", plan.RootElement.GetProperty("schema").GetString());
		Assert.Equal("RebuildBase", plan.RootElement.GetProperty("request").GetProperty("operation").GetString());
		Assert.Equal("missing", plan.RootElement.GetProperty("inputs").GetProperty("baseIndexState").GetString());
		Assert.False(string.IsNullOrWhiteSpace(plan.RootElement.GetProperty("inputs").GetProperty("currentSourceFingerprint").GetString()));
	}

	[Fact]
	public async Task IndexValidate_WhenArchiveHashesChange_RejectsThePlan()
	{
		await using var fixture = await IndexFixture.CreateAsync();
		var planPath = Path.Combine(fixture.RootDirectory, "index-plan.json");
		Assert.Equal(0, await RunAsync(
			"gamedata", "index", "plan", "rebuild",
			"--manager-root", fixture.ManagerRoot,
			"--game-data", fixture.GameDataDirectory,
			"--output", planPath,
			"--json"));
		await File.WriteAllTextAsync(fixture.ArchiveHashesPath, "{ }");

		var exitCode = await RunAsync(
			"gamedata", "index", "validate",
			"--plan", planPath,
			"--manager-root", fixture.ManagerRoot,
			"--game-data", fixture.GameDataDirectory,
			"--json");

		Assert.Equal(2, exitCode);
	}

	[Fact]
	public async Task UnitFactsPlan_WithoutCurrentBaseIndex_IsBlocked()
	{
		await using var fixture = await IndexFixture.CreateAsync();
		var planPath = Path.Combine(fixture.RootDirectory, "unit-facts-plan.json");

		var exitCode = await RunAsync(
			"gamedata", "index", "plan", "units-all",
			"--manager-root", fixture.ManagerRoot,
			"--game-data", fixture.GameDataDirectory,
			"--output", planPath,
			"--json");

		Assert.Equal(2, exitCode);
		using var plan = JsonDocument.Parse(await File.ReadAllTextAsync(planPath));
		Assert.Contains(
			plan.RootElement.GetProperty("diagnostics").EnumerateArray().Select(item => item.GetProperty("code").GetString()),
			code => string.Equals(code, "GameDataIndexNotCurrent", StringComparison.Ordinal));
	}

	[Fact]
	public async Task IndexApply_RequiresConfirmBeforeReadingPlanOrWritingIndex()
	{
		await using var fixture = await IndexFixture.CreateAsync();

		var exitCode = await RunAsync(
			"gamedata", "index", "apply",
			"--plan", Path.Combine(fixture.RootDirectory, "not-even-read.json"),
			"--manager-root", fixture.ManagerRoot,
			"--game-data", fixture.GameDataDirectory,
			"--json");

		Assert.Equal(2, exitCode);
		Assert.False(File.Exists(Path.Combine(fixture.ManagerRoot, "data", "indexes", "asset-index.sqlite")));
	}

	private static Task<int> RunAsync(params string[] args) => CliApplication.RunAsync(args);

	private sealed class IndexFixture : IAsyncDisposable
	{
		private IndexFixture(string rootDirectory, string managerRoot, string gameDataDirectory, string archiveHashesPath)
		{
			RootDirectory = rootDirectory;
			ManagerRoot = managerRoot;
			GameDataDirectory = gameDataDirectory;
			ArchiveHashesPath = archiveHashesPath;
		}

		public string RootDirectory { get; }
		public string ManagerRoot { get; }
		public string GameDataDirectory { get; }
		public string ArchiveHashesPath { get; }

		public static async ValueTask<IndexFixture> CreateAsync()
		{
			var root = Path.Combine(Path.GetTempPath(), "hd2-cli-index-" + Guid.NewGuid().ToString("N"));
			var managerRoot = Path.Combine(root, "manager");
			var libraryRoot = Path.Combine(root, "library");
			var gameData = Path.Combine(root, "game-data");
			var archiveHashes = Path.Combine(managerRoot, "data", "resources", "archivehashes.json");
			Directory.CreateDirectory(managerRoot);
			Directory.CreateDirectory(libraryRoot);
			Directory.CreateDirectory(gameData);
			Directory.CreateDirectory(Path.GetDirectoryName(archiveHashes)!);
			await File.WriteAllTextAsync(Path.Combine(managerRoot, "settings.json"), JsonSerializer.Serialize(new { ModLibraryFolder = libraryRoot, GameDataFolder = gameData }));
			await File.WriteAllTextAsync(archiveHashes, "{}");
			return new IndexFixture(root, managerRoot, gameData, archiveHashes);
		}

		public ValueTask DisposeAsync()
		{
			try { Directory.Delete(RootDirectory, recursive: true); }
			catch { }
			return ValueTask.CompletedTask;
		}
	}
}
