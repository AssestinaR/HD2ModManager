using System.Text.Json;
using HD2ModCli;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using Xunit;

namespace HD2ModCli.Tests;

[Collection("cli-console")]
public sealed class DecorationSourceInspectionCliTests
{
	[Fact]
	public async Task DecorationSourceInspect_RejectsNonPatchReShadeWithoutWritingLibraryState()
	{
		await using var fixture = await Fixture.CreateAsync();
		var libraryBefore = await File.ReadAllBytesAsync(fixture.LibraryPath);
		var profilesBefore = await File.ReadAllBytesAsync(fixture.ProfilesPath);

		var exitCode = await CliApplication.RunAsync(
		[
			"decoration", "inspect", "source", fixture.NodeSelector,
			"--manager-root", fixture.ManagerRoot,
			"--library", fixture.LibraryRoot,
			"--game-data", fixture.GameDataDirectory,
			"--json",
		]);

		Assert.Equal(2, exitCode);
		Assert.Equal(libraryBefore, await File.ReadAllBytesAsync(fixture.LibraryPath));
		Assert.Equal(profilesBefore, await File.ReadAllBytesAsync(fixture.ProfilesPath));
	}

	[Fact]
	public async Task DecorationSourceInspect_RejectsWriteOptions()
	{
		await using var fixture = await Fixture.CreateAsync();

		var exitCode = await CliApplication.RunAsync(
		[
			"decoration", "inspect", "source", fixture.NodeSelector,
			"--manager-root", fixture.ManagerRoot,
			"--library", fixture.LibraryRoot,
			"--game-data", fixture.GameDataDirectory,
			"--confirm",
			"--json",
		]);

		Assert.Equal(2, exitCode);
	}

	private sealed class Fixture : IAsyncDisposable
	{
		private Fixture(string rootDirectory, string managerRoot, string libraryRoot, string gameDataDirectory, ModNodeId nodeId)
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
		public string LibraryPath => Path.Combine(LibraryRoot, "library.json");
		public string ProfilesPath => Path.Combine(LibraryRoot, "profiles.json");

		public static async ValueTask<Fixture> CreateAsync()
		{
			var root = Path.Combine(Path.GetTempPath(), "hd2-cli-decoration-inspect-" + Guid.NewGuid().ToString("N"));
			var managerRoot = Path.Combine(root, "manager");
			var libraryRoot = Path.Combine(root, "library");
			var gameDataDirectory = Path.Combine(root, "Helldivers 2", "data");
			var relativePath = "reshade-source";
			Directory.CreateDirectory(managerRoot);
			Directory.CreateDirectory(Path.Combine(libraryRoot, relativePath));
			Directory.CreateDirectory(gameDataDirectory);
			await File.WriteAllTextAsync(
				Path.Combine(managerRoot, "settings.json"),
				JsonSerializer.Serialize(new { ModLibraryFolder = libraryRoot, GameDataFolder = gameDataDirectory }));

			var now = DateTimeOffset.UtcNow;
			var nodeId = ModNodeId.New();
			var node = new ModNode(
				nodeId,
				relativePath,
				new ModNodeMetadata("ReShade source", null, now, now, ModNodeKind.ReShade),
				[],
				[]);
			await new JsonModLibraryStore(new StoragePaths(managerRoot, libraryRoot), allowLegacyMigration: false)
				.SaveAsync(new LibrarySnapshot(1, now, new Dictionary<ModNodeId, ModNode> { [nodeId] = node }, Array.Empty<Profile>()));
			return new Fixture(root, managerRoot, libraryRoot, gameDataDirectory, nodeId);
		}

		public ValueTask DisposeAsync()
		{
			try { Directory.Delete(RootDirectory, recursive: true); }
			catch { }
			return ValueTask.CompletedTask;
		}
	}
}
