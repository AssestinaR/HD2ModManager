using System.Text.Json;
using HD2ModCli;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using Xunit;

namespace HD2ModCli.Tests;

[Collection("cli-console")]
public sealed class DecorationPackageCliTests
{
	[Fact]
	public async Task DecorationPackagePlan_WithNoUsableUnit_WritesAuditableBlockedPlanWithoutChangingLibrary()
	{
		await using var fixture = await Fixture.CreateAsync();
		var planPath = Path.Combine(fixture.RootDirectory, "decoration-package-plan.json");
		var libraryBefore = await File.ReadAllBytesAsync(fixture.LibraryPath);
		var profilesBefore = await File.ReadAllBytesAsync(fixture.ProfilesPath);

		var exitCode = await RunAsync(
			"decoration", "package", "plan", fixture.SourceSelector,
			"--host", fixture.HostSelector,
			"--manager-root", fixture.ManagerRoot,
			"--library", fixture.LibraryRoot,
			"--game-data", fixture.GameDataDirectory,
			"--output", planPath,
			"--json");

		Assert.Equal(2, exitCode);
		Assert.True(File.Exists(planPath));
		using var plan = JsonDocument.Parse(await File.ReadAllTextAsync(planPath));
		Assert.Equal("hd2mod.decoration-package-plan", plan.RootElement.GetProperty("schema").GetString());
		Assert.Equal(fixture.SourceSelector, plan.RootElement.GetProperty("source").GetProperty("nodeId").GetString());
		Assert.Equal(fixture.HostSelector, plan.RootElement.GetProperty("attachment").GetProperty("targetHostNodeIds")[0].GetString());
		Assert.Equal(libraryBefore, await File.ReadAllBytesAsync(fixture.LibraryPath));
		Assert.Equal(profilesBefore, await File.ReadAllBytesAsync(fixture.ProfilesPath));
	}

	[Fact]
	public async Task DecorationPackageValidateAndApply_BlockedPlanNeverWritesOutput()
	{
		await using var fixture = await Fixture.CreateAsync();
		var planPath = Path.Combine(fixture.RootDirectory, "decoration-package-plan.json");
		var outputDirectory = Path.Combine(fixture.RootDirectory, "must-not-exist");
		Assert.Equal(2, await RunAsync(
			"decoration", "package", "plan", fixture.SourceSelector,
			"--host", fixture.HostSelector,
			"--manager-root", fixture.ManagerRoot,
			"--library", fixture.LibraryRoot,
			"--game-data", fixture.GameDataDirectory,
			"--output", planPath,
			"--json"));

		Assert.Equal(2, await RunAsync(
			"decoration", "package", "validate",
			"--plan", planPath,
			"--manager-root", fixture.ManagerRoot,
			"--library", fixture.LibraryRoot,
			"--game-data", fixture.GameDataDirectory,
			"--json"));
		Assert.Equal(2, await RunAsync(
			"decoration", "package", "apply",
			"--plan", planPath,
			"--manager-root", fixture.ManagerRoot,
			"--library", fixture.LibraryRoot,
			"--game-data", fixture.GameDataDirectory,
			"--output", outputDirectory,
			"--json"));
		Assert.Equal(2, await RunAsync(
			"decoration", "package", "apply",
			"--plan", planPath,
			"--manager-root", fixture.ManagerRoot,
			"--library", fixture.LibraryRoot,
			"--game-data", fixture.GameDataDirectory,
			"--output", outputDirectory,
			"--confirm",
			"--json"));
		Assert.False(Directory.Exists(outputDirectory));
	}

	private static Task<int> RunAsync(params string[] args) => CliApplication.RunAsync(args);

	private sealed class Fixture : IAsyncDisposable
	{
		private Fixture(string rootDirectory, string managerRoot, string libraryRoot, string gameDataDirectory, ModNodeId sourceId, ModNodeId hostId)
		{
			RootDirectory = rootDirectory;
			ManagerRoot = managerRoot;
			LibraryRoot = libraryRoot;
			GameDataDirectory = gameDataDirectory;
			SourceId = sourceId;
			HostId = hostId;
		}

		public string RootDirectory { get; }
		public string ManagerRoot { get; }
		public string LibraryRoot { get; }
		public string GameDataDirectory { get; }
		public ModNodeId SourceId { get; }
		public ModNodeId HostId { get; }
		public string SourceSelector => SourceId.Value.ToString("D");
		public string HostSelector => HostId.Value.ToString("D");
		public string LibraryPath => Path.Combine(LibraryRoot, "library.json");
		public string ProfilesPath => Path.Combine(LibraryRoot, "profiles.json");

		public static async ValueTask<Fixture> CreateAsync()
		{
			var root = Path.Combine(Path.GetTempPath(), "hd2-cli-decoration-package-" + Guid.NewGuid().ToString("N"));
			var managerRoot = Path.Combine(root, "manager");
			var libraryRoot = Path.Combine(root, "library");
			var gameDataDirectory = Path.Combine(root, "Helldivers 2", "data");
			var sourceRelativePath = "decoration-source";
			var hostRelativePath = "decoration-host";
			Directory.CreateDirectory(managerRoot);
			Directory.CreateDirectory(gameDataDirectory);
			Directory.CreateDirectory(Path.Combine(libraryRoot, sourceRelativePath));
			Directory.CreateDirectory(Path.Combine(libraryRoot, hostRelativePath));
			await File.WriteAllTextAsync(
				Path.Combine(managerRoot, "settings.json"),
				JsonSerializer.Serialize(new { ModLibraryFolder = libraryRoot, GameDataFolder = gameDataDirectory }));
			await File.WriteAllBytesAsync(Path.Combine(libraryRoot, sourceRelativePath, "0123456789abcdef.patch_0"), Array.Empty<byte>());
			await File.WriteAllBytesAsync(Path.Combine(libraryRoot, hostRelativePath, "fedcba9876543210.patch_0"), Array.Empty<byte>());

			var now = DateTimeOffset.UtcNow;
			var source = new ModNode(
				ModNodeId.New(),
				sourceRelativePath,
				new ModNodeMetadata("Decoration package source", null, now, now),
				[new PatchGroupKey("0123456789abcdef", 0)],
				[]);
			var host = new ModNode(
				ModNodeId.New(),
				hostRelativePath,
				new ModNodeMetadata("Decoration package host", null, now, now),
				[new PatchGroupKey("fedcba9876543210", 0)],
				[]);
			await new JsonModLibraryStore(new StoragePaths(managerRoot, libraryRoot), allowLegacyMigration: false)
				.SaveAsync(new LibrarySnapshot(1, now, new Dictionary<ModNodeId, ModNode> { [source.Id] = source, [host.Id] = host }, Array.Empty<Profile>()));
			return new Fixture(root, managerRoot, libraryRoot, gameDataDirectory, source.Id, host.Id);
		}

		public ValueTask DisposeAsync()
		{
			try { Directory.Delete(RootDirectory, recursive: true); }
			catch { }
			return ValueTask.CompletedTask;
		}
	}
}
