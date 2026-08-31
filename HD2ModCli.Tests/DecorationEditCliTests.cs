using System.Text.Json;
using HD2ModCli;
using HD2ModCore.Decoration;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using Xunit;

namespace HD2ModCli.Tests;

[Collection("cli-console")]
public sealed class DecorationEditCliTests
{
	[Fact]
	public async Task ExportAndPlan_WriteAuditableBlockedArtifactsWithoutChangingImportedDecoration()
	{
		await using var fixture = await Fixture.CreateAsync();
		var specificationPath = Path.Combine(fixture.RootDirectory, "decoration-edit-spec.json");
		var planPath = Path.Combine(fixture.RootDirectory, "decoration-edit-plan.json");
		var decorationBefore = await File.ReadAllBytesAsync(fixture.DecorationPlanPath);
		var activationBefore = await File.ReadAllBytesAsync(fixture.DecorationActivationsPath);

		var export = await CliTestConsole.RunCapturedAsync(
			"decoration", "edit", "export", fixture.DecorationSelector,
			"--manager-root", fixture.ManagerRoot,
			"--library", fixture.LibraryRoot,
			"--game-data", fixture.GameDataDirectory,
			"--output", specificationPath,
			"--json");

		Assert.Equal(2, export.ExitCode);
		Assert.True(File.Exists(specificationPath));
		using (var specification = JsonDocument.Parse(await File.ReadAllTextAsync(specificationPath)))
		{
			Assert.Equal("hd2mod.decoration-edit-spec", specification.RootElement.GetProperty("schema").GetString());
			Assert.Equal(fixture.DecorationSelector, specification.RootElement.GetProperty("source").GetProperty("decorationNodeId").GetString());
		}

		var plan = await CliTestConsole.RunCapturedAsync(
			"decoration", "edit", "plan", specificationPath,
			"--manager-root", fixture.ManagerRoot,
			"--library", fixture.LibraryRoot,
			"--game-data", fixture.GameDataDirectory,
			"--output", planPath,
			"--json");

		Assert.Equal(2, plan.ExitCode);
		Assert.True(File.Exists(planPath));
		using var document = JsonDocument.Parse(await File.ReadAllTextAsync(planPath));
		Assert.Equal("hd2mod.decoration-edit-plan", document.RootElement.GetProperty("schema").GetString());
		Assert.Contains(
			document.RootElement.GetProperty("diagnostics").EnumerateArray().Select(item => item.GetProperty("code").GetString()),
			code => string.Equals(code, "DecorationEditCandidatesMissing", StringComparison.Ordinal));
		Assert.Equal(decorationBefore, await File.ReadAllBytesAsync(fixture.DecorationPlanPath));
		Assert.Equal(activationBefore, await File.ReadAllBytesAsync(fixture.DecorationActivationsPath));
	}

	[Fact]
	public async Task InspectAndApplyHonorTheirSafetyBoundaries()
	{
		await using var fixture = await Fixture.CreateAsync();
		var specificationPath = Path.Combine(fixture.RootDirectory, "decoration-edit-spec.json");
		Assert.Equal(2, await CliApplication.RunAsync(
		[
			"decoration", "edit", "export", fixture.DecorationSelector,
			"--manager-root", fixture.ManagerRoot,
			"--library", fixture.LibraryRoot,
			"--game-data", fixture.GameDataDirectory,
			"--output", specificationPath,
			"--json",
		]));
		var decorationBefore = await File.ReadAllBytesAsync(fixture.DecorationPlanPath);
		var activationBefore = await File.ReadAllBytesAsync(fixture.DecorationActivationsPath);

		var inspect = await CliTestConsole.RunCapturedAsync(
			"decoration", "edit", "inspect", specificationPath,
			"--manager-root", fixture.ManagerRoot,
			"--library", fixture.LibraryRoot,
			"--game-data", fixture.GameDataDirectory,
			"--json");
		Assert.Equal(2, inspect.ExitCode);
		Assert.Equal(decorationBefore, await File.ReadAllBytesAsync(fixture.DecorationPlanPath));
		Assert.Equal(activationBefore, await File.ReadAllBytesAsync(fixture.DecorationActivationsPath));

		var apply = await CliTestConsole.RunCapturedAsync(
			"decoration", "edit", "apply",
			"--plan", Path.Combine(fixture.RootDirectory, "does-not-exist.json"),
			"--manager-root", fixture.ManagerRoot,
			"--library", fixture.LibraryRoot,
			"--game-data", fixture.GameDataDirectory,
			"--json");
		Assert.Equal(2, apply.ExitCode);
		using var response = apply.ParseJson();
		Assert.Equal("DecorationEditApplyConfirmationRequired", response.RootElement.GetProperty("error").GetProperty("code").GetString());
		Assert.Equal(decorationBefore, await File.ReadAllBytesAsync(fixture.DecorationPlanPath));
		Assert.Equal(activationBefore, await File.ReadAllBytesAsync(fixture.DecorationActivationsPath));
	}

	[Fact]
	public async Task PlanRejectsSpecificationFromAnOlderDecorationPlan()
	{
		await using var fixture = await Fixture.CreateAsync();
		var specificationPath = Path.Combine(fixture.RootDirectory, "decoration-edit-spec.json");
		var planPath = Path.Combine(fixture.RootDirectory, "decoration-edit-plan.json");
		Assert.Equal(2, await CliApplication.RunAsync(
		[
			"decoration", "edit", "export", fixture.DecorationSelector,
			"--manager-root", fixture.ManagerRoot,
			"--library", fixture.LibraryRoot,
			"--game-data", fixture.GameDataDirectory,
			"--output", specificationPath,
			"--json",
		]));

		var specificationJson = await File.ReadAllTextAsync(specificationPath);
		using var original = JsonDocument.Parse(specificationJson);
		var currentHash = original.RootElement.GetProperty("source").GetProperty("currentPlanSha256").GetString();
		Assert.False(string.IsNullOrWhiteSpace(currentHash));
		await File.WriteAllTextAsync(specificationPath, specificationJson.Replace(currentHash!, new string('0', currentHash!.Length), StringComparison.Ordinal));

		var capture = await CliTestConsole.RunCapturedAsync(
			"decoration", "edit", "plan", specificationPath,
			"--manager-root", fixture.ManagerRoot,
			"--library", fixture.LibraryRoot,
			"--game-data", fixture.GameDataDirectory,
			"--output", planPath,
			"--json");

		Assert.Equal(2, capture.ExitCode);
		using var plan = capture.ParseJson();
		Assert.Contains(
			plan.RootElement.GetProperty("edit").GetProperty("diagnostics").EnumerateArray().Select(item => item.GetProperty("code").GetString()),
			code => string.Equals(code, "DecorationEditSourceChanged", StringComparison.Ordinal));
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
		public string DecorationSelector => Decoration.Id.Value.ToString("D");
		public string DecorationPlanPath => Path.Combine(LibraryRoot, Decoration.RelativePath, "decoration.json");
		public string DecorationActivationsPath => Path.Combine(LibraryRoot, "decoration-activations.json");

		public static async ValueTask<Fixture> CreateAsync()
		{
			var root = Path.Combine(Path.GetTempPath(), "hd2-cli-decoration-edit-" + Guid.NewGuid().ToString("N"));
			var managerRoot = Path.Combine(root, "manager");
			var libraryRoot = Path.Combine(root, "library");
			var gameRoot = Path.Combine(root, "Helldivers 2");
			var gameData = Path.Combine(gameRoot, "data");
			Directory.CreateDirectory(managerRoot);
			Directory.CreateDirectory(libraryRoot);
			Directory.CreateDirectory(gameData);
			Directory.CreateDirectory(Path.Combine(gameRoot, "bin"));
			await File.WriteAllTextAsync(Path.Combine(gameRoot, "bin", "helldivers2.exe"), string.Empty);
			await File.WriteAllTextAsync(
				Path.Combine(managerRoot, "settings.json"),
				JsonSerializer.Serialize(new { ModLibraryFolder = libraryRoot, GameDataFolder = gameData }));

			var now = DateTimeOffset.UtcNow;
			var host = new ModNode(
				ModNodeId.New(),
				"host",
				new ModNodeMetadata("Decoration Edit Host", null, now, now),
				[new PatchGroupKey("1111111111111111", 0)],
				[]);
			var decoration = new ModNode(
				ModNodeId.New(),
				"decoration",
				new ModNodeMetadata("Decoration Edit Source", null, now, now, ModNodeKind.Decoration),
				[new PatchGroupKey("2222222222222222", 0)],
				[]);
			Directory.CreateDirectory(Path.Combine(libraryRoot, host.RelativePath));
			Directory.CreateDirectory(Path.Combine(libraryRoot, decoration.RelativePath));
			await File.WriteAllBytesAsync(Path.Combine(libraryRoot, host.RelativePath, "1111111111111111.patch_0"), Array.Empty<byte>());
			await File.WriteAllBytesAsync(Path.Combine(libraryRoot, decoration.RelativePath, "2222222222222222.patch_0"), Array.Empty<byte>());
			var decorationPlan = new DecorationPlanDocument
			{
				Name = "Decoration edit fixture",
				SourceStorageMode = "PatchSnapshot",
				SourceUnits = [new DecorationSourceUnit { TypeId = 1, FileId = 2, MeshInfoIndex = 0, BodyVariant = "Any", Layer = "Armor" }],
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
			await new DecorationActivationStore(paths.DecorationActivationsPath)
				.SetEnabledHostsBatchAsync(new Dictionary<string, IReadOnlyCollection<string>>
				{
					[decoration.Id.Value.ToString("N")] = [host.Id.Value.ToString("N")],
				});

			const string archiveId = "aaaaaaaaaaaaaaaa";
			await File.WriteAllBytesAsync(Path.Combine(gameData, archiveId), BuildToc([new AssetKey(20, 200)]));
			var archiveHashes = JsonSerializer.Serialize(new Dictionary<string, Dictionary<string, string>>
			{
				["Armor"] = new Dictionary<string, string> { [archiveId] = "Fixture Armor" },
			});
			Directory.CreateDirectory(paths.ResourcesDirectory);
			await File.WriteAllTextAsync(paths.ArchiveHashesPath, archiveHashes);
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
