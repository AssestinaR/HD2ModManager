using System.Security.Cryptography;
using System.Text.Json;
using HD2ModCli;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using Xunit;

namespace HD2ModCli.Tests;

[Collection("cli-console")]
public sealed class GameDataInspectionCliTests
{
	[Fact]
	public async Task StatusAndArchiveCommands_ReadCurrentIndexWithoutChangingLibrary()
	{
		await using var fixture = await Fixture.CreateAsync();
		var libraryBefore = await File.ReadAllBytesAsync(fixture.LibraryPath);
		var hashesBefore = await File.ReadAllBytesAsync(fixture.ArchiveHashesPath);

		var status = await CliTestConsole.RunCapturedAsync(
			"gamedata", "status",
			"--manager-root", fixture.ManagerRoot,
			"--library", fixture.LibraryRoot,
			"--game-data", fixture.GameDataDirectory,
			"--json");
		Assert.Equal(0, status.ExitCode);
		using (var response = status.ParseJson())
		{
			Assert.True(response.RootElement.GetProperty("ok").GetBoolean());
			Assert.True(response.RootElement.GetProperty("inspection").GetProperty("index").GetProperty("mappingTrusted").GetBoolean());
		}

		var list = await CliTestConsole.RunCapturedAsync(
			"gamedata", "archive", "list",
			"--manager-root", fixture.ManagerRoot,
			"--library", fixture.LibraryRoot,
			"--game-data", fixture.GameDataDirectory,
			"--limit", "1",
			"--json");
		string? nextCursor;
		Assert.Equal(0, list.ExitCode);
		using (var response = list.ParseJson())
		{
			Assert.Equal(2, response.RootElement.GetProperty("archives").GetProperty("totalCount").GetInt32());
			Assert.Equal(fixture.ArchiveId, Assert.Single(response.RootElement.GetProperty("archives").GetProperty("items").EnumerateArray()).GetProperty("packageName").GetString());
			nextCursor = response.RootElement.GetProperty("archives").GetProperty("nextCursor").GetString();
		}
		Assert.False(string.IsNullOrWhiteSpace(nextCursor));
		var secondPage = await CliTestConsole.RunCapturedAsync(
			"gamedata", "archive", "list",
			"--manager-root", fixture.ManagerRoot,
			"--library", fixture.LibraryRoot,
			"--game-data", fixture.GameDataDirectory,
			"--limit", "1",
			"--cursor", nextCursor!,
			"--json");
		Assert.Equal(0, secondPage.ExitCode);
		using (var response = secondPage.ParseJson())
			Assert.Equal(fixture.SecondaryArchiveId, Assert.Single(response.RootElement.GetProperty("archives").GetProperty("items").EnumerateArray()).GetProperty("packageName").GetString());

		var show = await CliTestConsole.RunCapturedAsync(
			"gamedata", "archive", "show", fixture.ArchiveId,
			"--manager-root", fixture.ManagerRoot,
			"--library", fixture.LibraryRoot,
			"--game-data", fixture.GameDataDirectory,
			"--with-effective",
			"--json");
		Assert.Equal(0, show.ExitCode);
		using (var response = show.ParseJson())
		{
			Assert.Equal(fixture.ArchiveId, response.RootElement.GetProperty("archive").GetProperty("archive").GetProperty("packageName").GetString());
			var asset = Assert.Single(response.RootElement.GetProperty("archive").GetProperty("assets").EnumerateArray());
			Assert.Equal("0x0000000000000014", asset.GetProperty("assetKey").GetProperty("typeId").GetString());
			Assert.Equal(1, asset.GetProperty("effectiveOverride").GetProperty("winner").GetProperty("targetPatchIndex").GetInt32());
		}

		Assert.Equal(libraryBefore, await File.ReadAllBytesAsync(fixture.LibraryPath));
		Assert.Equal(hashesBefore, await File.ReadAllBytesAsync(fixture.ArchiveHashesPath));
	}

	[Fact]
	public async Task AssetFind_ResolvesHexAssetKeyToIndexedArchive()
	{
		await using var fixture = await Fixture.CreateAsync();
		var capture = await CliTestConsole.RunCapturedAsync(
			"gamedata", "asset", "find", "0x0000000000000014", "00000000000000c8",
			"--manager-root", fixture.ManagerRoot,
			"--library", fixture.LibraryRoot,
			"--game-data", fixture.GameDataDirectory,
			"--json");

		Assert.Equal(0, capture.ExitCode);
		using var response = capture.ParseJson();
		var match = Assert.Single(response.RootElement.GetProperty("assets").GetProperty("matches").EnumerateArray());
		Assert.Equal("0x0000000000000014", match.GetProperty("assetKey").GetProperty("typeId").GetString());
		Assert.Equal(fixture.ArchiveId, Assert.Single(match.GetProperty("archives").EnumerateArray()).GetProperty("archiveId").GetString());
	}

	[Fact]
	public async Task DeploymentInspectAndBrowser_ReportActualWinnerWithoutWritingDerivedModCache()
	{
		await using var fixture = await Fixture.CreateAsync();
		Assert.False(File.Exists(fixture.ModFactsDbPath));

		var deployment = await CliTestConsole.RunCapturedAsync(
			"gamedata", "override", "show", "0000000000000014", "00000000000000c8",
			"--game-data", fixture.GameDataDirectory,
			"--json");
		Assert.True(deployment.ExitCode == 0, deployment.StandardError + Environment.NewLine + deployment.StandardOutput);
		using (var response = deployment.ParseJson())
		{
			var graph = response.RootElement.GetProperty("overrides");
			Assert.True(graph.GetProperty("found").GetBoolean());
			Assert.True(graph.GetProperty("isCompetition").GetBoolean());
			var entries = graph.GetProperty("entries").EnumerateArray().ToArray();
			Assert.Equal(2, entries.Length);
			Assert.True(entries[^1].GetProperty("isWinner").GetBoolean());
			Assert.Equal(1, entries[^1].GetProperty("targetPatchIndex").GetInt32());
		}

		var browser = await CliTestConsole.RunCapturedAsync(
			"gamedata", "browser",
			"--manager-root", fixture.ManagerRoot,
			"--library", fixture.LibraryRoot,
			"--game-data", fixture.GameDataDirectory,
			"--json");
		Assert.True(browser.ExitCode == 0, browser.StandardError + Environment.NewLine + browser.StandardOutput);
		using (var response = browser.ParseJson())
		{
			var item = response.RootElement.GetProperty("browser").GetProperty("browser").GetProperty("archives").EnumerateArray()
				.Single(candidate => string.Equals(candidate.GetProperty("archive").GetProperty("packageName").GetString(), fixture.ArchiveId, StringComparison.Ordinal));
			Assert.Equal(fixture.ArchiveId, item.GetProperty("archive").GetProperty("packageName").GetString());
			Assert.Single(item.GetProperty("overlay").GetProperty("libraryMods").EnumerateArray());
			Assert.Single(item.GetProperty("overlay").GetProperty("activeMods").EnumerateArray());
			Assert.Single(item.GetProperty("overlay").GetProperty("effectiveMods").EnumerateArray());
		}

		Assert.False(File.Exists(fixture.ModFactsDbPath));
	}

	[Fact]
	public async Task Status_ReportsStaleIndexWithoutWritingIt()
	{
		await using var fixture = await Fixture.CreateAsync();
		var indexBefore = await File.ReadAllBytesAsync(fixture.AssetIndexPath);
		await using (var stream = new FileStream(Path.Combine(fixture.GameDataDirectory, fixture.ArchiveId), FileMode.Append, FileAccess.Write, FileShare.Read))
			await stream.WriteAsync(new byte[] { 0xFF });

		var capture = await CliTestConsole.RunCapturedAsync(
			"gamedata", "status",
			"--manager-root", fixture.ManagerRoot,
			"--library", fixture.LibraryRoot,
			"--game-data", fixture.GameDataDirectory,
			"--json");

		Assert.Equal(2, capture.ExitCode);
		using var response = capture.ParseJson();
		Assert.False(response.RootElement.GetProperty("ok").GetBoolean());
		Assert.False(response.RootElement.GetProperty("inspection").GetProperty("index").GetProperty("mappingTrusted").GetBoolean());
		Assert.Equal(indexBefore, await File.ReadAllBytesAsync(fixture.AssetIndexPath));
	}

	[Fact]
	public async Task ArchiveCursor_RejectsIndexGenerationChange()
	{
		await using var fixture = await Fixture.CreateAsync();
		var first = await CliTestConsole.RunCapturedAsync(
			"gamedata", "archive", "list",
			"--manager-root", fixture.ManagerRoot,
			"--game-data", fixture.GameDataDirectory,
			"--limit", "1",
			"--json");
		Assert.Equal(0, first.ExitCode);
		string cursor;
		using (var response = first.ParseJson())
			cursor = response.RootElement.GetProperty("archives").GetProperty("nextCursor").GetString()!;
		Assert.False(string.IsNullOrWhiteSpace(cursor));

		await using (var stream = new FileStream(Path.Combine(fixture.GameDataDirectory, fixture.ArchiveId), FileMode.Append, FileAccess.Write, FileShare.Read))
			await stream.WriteAsync(new byte[] { 0xEE });
		var capture = await CliTestConsole.RunCapturedAsync(
			"gamedata", "archive", "list",
			"--manager-root", fixture.ManagerRoot,
			"--game-data", fixture.GameDataDirectory,
			"--limit", "1",
			"--cursor", cursor,
			"--json");

		Assert.Equal(2, capture.ExitCode);
		using var changed = capture.ParseJson();
		Assert.Equal("CursorGenerationChanged", changed.RootElement.GetProperty("error").GetProperty("code").GetString());
	}

	[Fact]
	public async Task ReadOnlyStatus_AllowsCopiedGameDataWithoutLiveGameSignature()
	{
		await using var fixture = await Fixture.CreateAsync();
		var copied = Path.Combine(fixture.RootDirectory, "copied-sample");
		Directory.CreateDirectory(copied);
		File.Copy(Path.Combine(fixture.GameDataDirectory, fixture.ArchiveId), Path.Combine(copied, fixture.ArchiveId));
		File.Copy(Path.Combine(fixture.GameDataDirectory, fixture.SecondaryArchiveId), Path.Combine(copied, fixture.SecondaryArchiveId));

		var capture = await CliTestConsole.RunCapturedAsync(
			"gamedata", "status",
			"--manager-root", fixture.ManagerRoot,
			"--game-data", copied,
			"--json");

		// The copied directory is readable without a live-game signature.  Its
		// index mapping is deliberately untrusted because the persisted index was
		// built for the original GameData location.
		Assert.Equal(2, capture.ExitCode);
		using var response = capture.ParseJson();
		Assert.Equal("unverified", response.RootElement.GetProperty("inspection").GetProperty("target").GetProperty("installationSignature").GetString());
		Assert.False(response.RootElement.GetProperty("inspection").GetProperty("index").GetProperty("mappingTrusted").GetBoolean());
	}

	[Fact]
	public async Task AssetFind_RejectsIncompleteKeyPairBeforeAnyRead()
	{
		await using var fixture = await Fixture.CreateAsync();
		var capture = await CliTestConsole.RunCapturedAsync(
			"gamedata", "asset", "find", "0000000000000014",
			"--manager-root", fixture.ManagerRoot,
			"--library", fixture.LibraryRoot,
			"--game-data", fixture.GameDataDirectory,
			"--json");

		Assert.Equal(2, capture.ExitCode);
		using var response = capture.ParseJson();
		Assert.Equal("GameDataAssetKeyPairRequired", response.RootElement.GetProperty("error").GetProperty("code").GetString());
	}

	private sealed class Fixture : IAsyncDisposable
	{
		private Fixture(string rootDirectory, string managerRoot, string libraryRoot, string gameDataDirectory, string archiveId, string secondaryArchiveId, ModNode node, StoragePaths paths)
		{
			RootDirectory = rootDirectory;
			ManagerRoot = managerRoot;
			LibraryRoot = libraryRoot;
			GameDataDirectory = gameDataDirectory;
			ArchiveId = archiveId;
			SecondaryArchiveId = secondaryArchiveId;
			Node = node;
			Paths = paths;
		}

		public string RootDirectory { get; }
		public string ManagerRoot { get; }
		public string LibraryRoot { get; }
		public string GameDataDirectory { get; }
		public string ArchiveId { get; }
		public string SecondaryArchiveId { get; }
		public ModNode Node { get; }
		public StoragePaths Paths { get; }
		public string LibraryPath => Paths.LibraryPath;
		public string ArchiveHashesPath => Paths.ArchiveHashesPath;
		public string AssetIndexPath => Paths.DbPath;
		public string ModFactsDbPath => Paths.ModFactsDbPath;

		public static async ValueTask<Fixture> CreateAsync()
		{
			var root = Path.Combine(Path.GetTempPath(), "hd2-cli-gamedata-" + Guid.NewGuid().ToString("N"));
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
			var node = new ModNode(
				ModNodeId.New(),
				"gamedata-fixture-mod",
				new ModNodeMetadata("GameData Fixture Mod", null, now, now),
				[new PatchGroupKey("1111111111111111", 0)],
				[]);
			var modDirectory = Path.Combine(libraryRoot, node.RelativePath);
			Directory.CreateDirectory(modDirectory);
			var patchBytes = BuildToc([new AssetKey(0x14, 0xc8)]);
			var sourcePatch = Path.Combine(modDirectory, "1111111111111111.patch_0");
			await File.WriteAllBytesAsync(sourcePatch, patchBytes);
			var profile = new Profile(ProfileId.New(), "GameData Fixture", now, now, [new ProfileEntry(node.Id, 0, now)]);
			var paths = new StoragePaths(managerRoot, libraryRoot);
			await new JsonModLibraryStore(paths, allowLegacyMigration: false).SaveAsync(
				new LibrarySnapshot(1, now, new Dictionary<ModNodeId, ModNode> { [node.Id] = node }, [profile], profile.Id));

			const string archiveId = "aaaaaaaaaaaaaaaa";
			await File.WriteAllBytesAsync(Path.Combine(gameData, archiveId), BuildToc([new AssetKey(0x14, 0xc8)]));
			var targetPatch = Path.Combine(gameData, "1111111111111111.patch_0");
			await File.WriteAllBytesAsync(targetPatch, patchBytes);
			var secondTargetPatch = Path.Combine(gameData, "1111111111111111.patch_1");
			await File.WriteAllBytesAsync(secondTargetPatch, patchBytes);
			var hash = Convert.ToHexString(SHA256.HashData(patchBytes)).ToLowerInvariant();
			await CoreServices.CreateActivationStateStore().SaveAsync(gameData, new ActivationState(
				JsonActivationStateStore.CurrentVersion,
				profile.Id,
				profile.Revision,
				now,
				true,
				[
					new ActivationStateFileEntry(targetPatch, sourcePatch, DeploymentMethod.Copy, "1111111111111111", 0, 0, PatchSidecarKind.Base, node.Id, patchBytes.Length, hash),
					new ActivationStateFileEntry(secondTargetPatch, sourcePatch, DeploymentMethod.Copy, "1111111111111111", 0, 1, PatchSidecarKind.Base, node.Id, patchBytes.Length, hash),
				],
				Array.Empty<CoreIssue>()));

			var archiveHashes = JsonSerializer.Serialize(new Dictionary<string, Dictionary<string, string>>
			{
				["Armor"] = new Dictionary<string, string>
				{
					[archiveId] = "GameData Fixture Armor",
					["bbbbbbbbbbbbbbbb"] = "GameData Fixture Secondary",
				},
			});
			const string secondaryArchiveId = "bbbbbbbbbbbbbbbb";
			await File.WriteAllBytesAsync(Path.Combine(gameData, secondaryArchiveId), BuildToc([new AssetKey(0x15, 0xc9)]));
			Directory.CreateDirectory(paths.ResourcesDirectory);
			await File.WriteAllTextAsync(paths.ArchiveHashesPath, archiveHashes);
			await CoreServices.CreateAssetArchiveIndexService(paths).BuildOrRebuildAsync(gameData, archiveHashes);

			return new Fixture(root, managerRoot, libraryRoot, gameData, archiveId, secondaryArchiveId, node, paths);
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
			const int entriesOffset = 60;
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
