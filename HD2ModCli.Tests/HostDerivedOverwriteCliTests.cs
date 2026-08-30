using System.Text.Json;
using HD2ModCli;
using HD2ModCore.Decoration;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using Xunit;

namespace HD2ModCli.Tests;

[Collection("cli-console")]
public sealed class HostDerivedOverwriteCliTests
{
	[Fact]
	public async Task Inspect_IsReadOnly_WhenGameDataIndexIsMissing()
	{
		await using var fixture = await Fixture.CreateAsync(buildGameDataIndex: false);
		var libraryBefore = await File.ReadAllBytesAsync(fixture.LibraryPath);
		var activationsBefore = await File.ReadAllBytesAsync(fixture.DecorationActivationsPath);

		var capture = await CliTestConsole.RunCapturedAsync(
			"host-derived", "inspect", fixture.HostSelector,
			"--manager-root", fixture.ManagerRoot,
			"--library", fixture.LibraryRoot,
			"--game-data", fixture.GameDataDirectory,
			"--json");

		Assert.Equal(0, capture.ExitCode);
		using var response = capture.ParseJson();
		Assert.False(response.RootElement.GetProperty("ok").GetBoolean());
		Assert.Equal(libraryBefore, await File.ReadAllBytesAsync(fixture.LibraryPath));
		Assert.Equal(activationsBefore, await File.ReadAllBytesAsync(fixture.DecorationActivationsPath));
	}

	[Fact]
	public async Task DecorationEnablePlan_UsesDeclaredHostAndPreservesOtherDerivedLayer()
	{
		await using var fixture = await Fixture.CreateAsync(buildGameDataIndex: true, helmetMerged: true);
		var planPath = Path.Combine(fixture.RootDirectory, "host-derived-decoration-plan.json");

		var exitCode = await CliApplication.RunAsync(
		[
			"host-derived", "plan", "decoration", "enable", fixture.DecorationSelector,
			"--host", fixture.HostSelector,
			"--manager-root", fixture.ManagerRoot,
			"--library", fixture.LibraryRoot,
			"--game-data", fixture.GameDataDirectory,
			"--output", planPath,
			"--json",
		]);

		Assert.Equal(0, exitCode);
		using var plan = JsonDocument.Parse(await File.ReadAllTextAsync(planPath));
		Assert.Equal("hd2mod.host-derived-overwrite-plan", plan.RootElement.GetProperty("schema").GetString());
		Assert.Equal("decoration", plan.RootElement.GetProperty("request").GetProperty("operation").GetString());
		Assert.Equal("enable", plan.RootElement.GetProperty("request").GetProperty("action").GetString());
		var host = Assert.Single(plan.RootElement.GetProperty("hosts").EnumerateArray());
		Assert.True(host.GetProperty("currentHelmetToArmorMerged").GetBoolean());
		Assert.True(host.GetProperty("plannedHelmetToArmorMerged").GetBoolean());
		Assert.Empty(host.GetProperty("currentDecorations").EnumerateArray());
		Assert.Single(host.GetProperty("plannedDecorations").EnumerateArray());
	}

	[Fact]
	public async Task Plan_RejectsUnmanagedOverwriteWithoutTouchingIt()
	{
		await using var fixture = await Fixture.CreateAsync(buildGameDataIndex: true);
		var overwrite = Path.Combine(fixture.HostDirectory, "Overwrite");
		Directory.CreateDirectory(overwrite);
		var userFile = Path.Combine(overwrite, "user-file.txt");
		await File.WriteAllTextAsync(userFile, "do not touch");
		var planPath = Path.Combine(fixture.RootDirectory, "host-derived-unmanaged-plan.json");

		var exitCode = await CliApplication.RunAsync(
		[
			"host-derived", "plan", "rebuild", fixture.HostSelector,
			"--manager-root", fixture.ManagerRoot,
			"--library", fixture.LibraryRoot,
			"--game-data", fixture.GameDataDirectory,
			"--output", planPath,
			"--json",
		]);

		Assert.Equal(2, exitCode);
		Assert.Equal("do not touch", await File.ReadAllTextAsync(userFile));
		using var plan = JsonDocument.Parse(await File.ReadAllTextAsync(planPath));
		Assert.Contains(
			plan.RootElement.GetProperty("diagnostics").EnumerateArray().Select(item => item.GetProperty("code").GetString()),
			code => string.Equals(code, "HostDerivedOverwriteUnmanaged", StringComparison.Ordinal));
	}

	[Fact]
	public async Task Validate_RejectsTamperedPlanBeforeReadingSourcesAgain()
	{
		await using var fixture = await Fixture.CreateAsync(buildGameDataIndex: true);
		var planPath = Path.Combine(fixture.RootDirectory, "host-derived-plan.json");
		Assert.Equal(0, await CliApplication.RunAsync(
		[
			"host-derived", "plan", "rebuild", fixture.HostSelector,
			"--manager-root", fixture.ManagerRoot,
			"--library", fixture.LibraryRoot,
			"--game-data", fixture.GameDataDirectory,
			"--output", planPath,
			"--json",
		]));

		var json = await File.ReadAllTextAsync(planPath);
		await File.WriteAllTextAsync(planPath, json.Replace("Host Derived Fixture", "Tampered Host", StringComparison.Ordinal));
		var capture = await CliTestConsole.RunCapturedAsync(
			"host-derived", "validate",
			"--plan", planPath,
			"--manager-root", fixture.ManagerRoot,
			"--library", fixture.LibraryRoot,
			"--game-data", fixture.GameDataDirectory,
			"--json");

		Assert.Equal(2, capture.ExitCode);
		using var response = capture.ParseJson();
		Assert.Equal("HostDerivedPlanIntegrityMismatch", response.RootElement.GetProperty("error").GetProperty("code").GetString());
	}

	[Fact]
	public async Task Apply_RequiresConfirmBeforeReadingPlanOrCreatingOverwrite()
	{
		await using var fixture = await Fixture.CreateAsync(buildGameDataIndex: true);
		var capture = await CliTestConsole.RunCapturedAsync(
			"host-derived", "apply",
			"--plan", Path.Combine(fixture.RootDirectory, "does-not-exist.json"),
			"--manager-root", fixture.ManagerRoot,
			"--library", fixture.LibraryRoot,
			"--game-data", fixture.GameDataDirectory,
			"--json");

		Assert.Equal(2, capture.ExitCode);
		using var response = capture.ParseJson();
		Assert.Equal("HostDerivedApplyConfirmationRequired", response.RootElement.GetProperty("error").GetProperty("code").GetString());
		Assert.False(Directory.Exists(Path.Combine(fixture.HostDirectory, "Overwrite")));
	}

	[Fact]
	public async Task RebuildApply_WithNoDerivedLayers_IsSuccessfulAndDoesNotChangeStateFiles()
	{
		await using var fixture = await Fixture.CreateAsync(buildGameDataIndex: true);
		var planPath = Path.Combine(fixture.RootDirectory, "host-derived-rebuild-plan.json");
		var libraryBefore = await File.ReadAllBytesAsync(fixture.LibraryPath);
		var activationsBefore = await File.ReadAllBytesAsync(fixture.DecorationActivationsPath);
		Assert.Equal(0, await CliApplication.RunAsync(
		[
			"host-derived", "plan", "rebuild", fixture.HostSelector,
			"--manager-root", fixture.ManagerRoot,
			"--library", fixture.LibraryRoot,
			"--game-data", fixture.GameDataDirectory,
			"--output", planPath,
			"--json",
		]));

		var capture = await CliTestConsole.RunCapturedAsync(
			"host-derived", "apply",
			"--plan", planPath,
			"--manager-root", fixture.ManagerRoot,
			"--library", fixture.LibraryRoot,
			"--game-data", fixture.GameDataDirectory,
			"--confirm",
			"--json");

		Assert.Equal(0, capture.ExitCode);
		using var response = capture.ParseJson();
		Assert.True(response.RootElement.GetProperty("ok").GetBoolean());
		Assert.False(response.RootElement.GetProperty("execution").GetProperty("stateCommitted").GetBoolean());
		Assert.Equal("none", response.RootElement.GetProperty("execution").GetProperty("stateMutation").GetString());
		Assert.Equal(libraryBefore, await File.ReadAllBytesAsync(fixture.LibraryPath));
		Assert.Equal(activationsBefore, await File.ReadAllBytesAsync(fixture.DecorationActivationsPath));
		Assert.False(Directory.Exists(Path.Combine(fixture.HostDirectory, "Overwrite")));
	}

	[Fact]
	public async Task DecorationEnableAll_RejectsConflictingExplicitHost()
	{
		await using var fixture = await Fixture.CreateAsync(buildGameDataIndex: true);
		var capture = await CliTestConsole.RunCapturedAsync(
			"host-derived", "plan", "decoration", "enable-all", fixture.DecorationSelector,
			"--host", fixture.HostSelector,
			"--manager-root", fixture.ManagerRoot,
			"--library", fixture.LibraryRoot,
			"--game-data", fixture.GameDataDirectory,
			"--output", Path.Combine(fixture.RootDirectory, "must-not-exist.json"),
			"--json");

		Assert.Equal(2, capture.ExitCode);
		using var response = capture.ParseJson();
		Assert.Equal("HostDerivedDecorationTargetAmbiguous", response.RootElement.GetProperty("error").GetProperty("code").GetString());
	}

	private sealed class Fixture : IAsyncDisposable
	{
		private Fixture(string rootDirectory, string managerRoot, string libraryRoot, string gameDataDirectory, ModNode host, ModNode decoration)
		{
			RootDirectory = rootDirectory;
			ManagerRoot = managerRoot;
			LibraryRoot = libraryRoot;
			GameDataDirectory = gameDataDirectory;
			Host = host;
			Decoration = decoration;
		}

		public string RootDirectory { get; }
		public string ManagerRoot { get; }
		public string LibraryRoot { get; }
		public string GameDataDirectory { get; }
		public ModNode Host { get; }
		public ModNode Decoration { get; }
		public string HostSelector => Host.Id.Value.ToString("D");
		public string DecorationSelector => Decoration.Id.Value.ToString("D");
		public string HostDirectory => Path.Combine(LibraryRoot, Host.RelativePath);
		public string LibraryPath => Path.Combine(LibraryRoot, "library.json");
		public string DecorationActivationsPath => Path.Combine(LibraryRoot, "decoration-activations.json");

		public static async ValueTask<Fixture> CreateAsync(bool buildGameDataIndex, bool helmetMerged = false)
		{
			var root = Path.Combine(Path.GetTempPath(), "hd2-cli-host-derived-" + Guid.NewGuid().ToString("N"));
			var managerRoot = Path.Combine(root, "manager");
			var libraryRoot = Path.Combine(root, "library");
			var gameRoot = Path.Combine(root, "Helldivers 2");
			var gameData = Path.Combine(gameRoot, "data");
			var gameBin = Path.Combine(gameRoot, "bin");
			Directory.CreateDirectory(managerRoot);
			Directory.CreateDirectory(libraryRoot);
			Directory.CreateDirectory(gameData);
			Directory.CreateDirectory(gameBin);
			await File.WriteAllTextAsync(Path.Combine(gameBin, "helldivers2.exe"), string.Empty);
			await File.WriteAllTextAsync(
				Path.Combine(managerRoot, "settings.json"),
				JsonSerializer.Serialize(new { ModLibraryFolder = libraryRoot, GameDataFolder = gameData }));

			var now = DateTimeOffset.UtcNow;
			var host = new ModNode(
				ModNodeId.New(),
				"host-derived-fixture",
				new ModNodeMetadata("Host Derived Fixture", null, now, now, HelmetToArmorMerged: helmetMerged),
				[new PatchGroupKey("1111111111111111", 0)],
				[]);
			var decoration = new ModNode(
				ModNodeId.New(),
				"host-derived-decoration",
				new ModNodeMetadata("Host Decoration", null, now, now, ModNodeKind.Decoration),
				[new PatchGroupKey("2222222222222222", 0)],
				[]);
			Directory.CreateDirectory(Path.Combine(libraryRoot, host.RelativePath));
			Directory.CreateDirectory(Path.Combine(libraryRoot, decoration.RelativePath));
			await File.WriteAllBytesAsync(Path.Combine(libraryRoot, host.RelativePath, "1111111111111111.patch_0"), [0x01]);
			await File.WriteAllBytesAsync(Path.Combine(libraryRoot, decoration.RelativePath, "2222222222222222.patch_0"), [0x02]);
			var decorationPlan = new DecorationPlanDocument
			{
				Name = "Host fixture decoration",
				Plan = new DecorationAttachmentPlan
				{
					TargetModGuids = [host.Id.Value.ToString("N")],
					TargetPart = "Torso",
					TargetBodyVariant = "Dual",
					DualVariantMode = "AutoAssign",
					SourcePartLayers = ["Torso/Armor"],
				},
			};
			await File.WriteAllTextAsync(
				Path.Combine(libraryRoot, decoration.RelativePath, "decoration.json"),
				JsonSerializer.Serialize(decorationPlan, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

			var paths = new StoragePaths(managerRoot, libraryRoot);
			await new JsonModLibraryStore(paths, allowLegacyMigration: false).SaveAsync(
				new LibrarySnapshot(1, now, new Dictionary<ModNodeId, ModNode> { [host.Id] = host, [decoration.Id] = decoration }, Array.Empty<Profile>()));
			await File.WriteAllTextAsync(Path.Combine(libraryRoot, "decoration-activations.json"), "{}");

			const string archiveId = "aaaaaaaaaaaaaaaa";
			await File.WriteAllBytesAsync(Path.Combine(gameData, archiveId), BuildToc([new AssetKey(20, 200)]));
			var archiveHashes = JsonSerializer.Serialize(new Dictionary<string, Dictionary<string, string>>
			{
				["Armor"] = new Dictionary<string, string> { [archiveId] = "Fixture Armor" },
			});
			Directory.CreateDirectory(paths.ResourcesDirectory);
			await File.WriteAllTextAsync(paths.ArchiveHashesPath, archiveHashes);
			if (buildGameDataIndex)
				await CoreServices.CreateAssetArchiveIndexService(paths).BuildOrRebuildAsync(gameData, archiveHashes);

			return new Fixture(root, managerRoot, libraryRoot, gameData, host, decoration);
		}

		public ValueTask DisposeAsync()
		{
			try { Directory.Delete(RootDirectory, recursive: true); }
			catch { }
			return ValueTask.CompletedTask;
		}

		private static byte[] BuildToc(IReadOnlyList<AssetKey> entries)
		{
			const uint magic = 4026531857;
			var entriesOffset = 60;
			var buffer = new byte[entriesOffset + entries.Count * 80];
			BitConverter.GetBytes(magic).CopyTo(buffer, 0);
			BitConverter.GetBytes((uint)0).CopyTo(buffer, 4);
			BitConverter.GetBytes((uint)entries.Count).CopyTo(buffer, 8);
			var offset = entriesOffset;
			foreach (var entry in entries)
			{
				BitConverter.GetBytes(entry.FileId).CopyTo(buffer, offset);
				BitConverter.GetBytes(entry.TypeId).CopyTo(buffer, offset + 8);
				offset += 80;
			}
			return buffer;
		}
	}
}
