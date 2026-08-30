using System.Text.Json;
using HD2ModCli;
using HD2ModCore.Decoration;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using Xunit;

namespace HD2ModCli.Tests;

[Collection("cli-console")]
public sealed class DecorationManagementCliTests
{
	[Fact]
	public async Task DecorationShow_ReadsDeclaredHostAndActivationWithoutWriting()
	{
		await using var fixture = await Fixture.CreateAsync();
		var libraryBefore = await File.ReadAllBytesAsync(fixture.LibraryPath);
		var activationsBefore = await File.ReadAllBytesAsync(fixture.DecorationActivationsPath);

		var exitCode = await CliApplication.RunAsync(
		[
			"decoration", "show", fixture.DecorationSelector,
			"--manager-root", fixture.ManagerRoot,
			"--library", fixture.LibraryRoot,
			"--json",
		]);

		Assert.Equal(0, exitCode);
		Assert.Equal(libraryBefore, await File.ReadAllBytesAsync(fixture.LibraryPath));
		Assert.Equal(activationsBefore, await File.ReadAllBytesAsync(fixture.DecorationActivationsPath));
	}

	[Fact]
	public async Task DecorationShow_RejectsWriteAndGameDataOptions()
	{
		await using var fixture = await Fixture.CreateAsync();

		Assert.Equal(2, await CliApplication.RunAsync(
		[
			"decoration", "show", fixture.DecorationSelector,
			"--manager-root", fixture.ManagerRoot,
			"--library", fixture.LibraryRoot,
			"--game-data", fixture.LibraryRoot,
			"--json",
		]));
	}

	private sealed class Fixture : IAsyncDisposable
	{
		private Fixture(string rootDirectory, string managerRoot, string libraryRoot, ModNodeId decorationId)
		{
			RootDirectory = rootDirectory;
			ManagerRoot = managerRoot;
			LibraryRoot = libraryRoot;
			DecorationId = decorationId;
		}

		public string RootDirectory { get; }
		public string ManagerRoot { get; }
		public string LibraryRoot { get; }
		public ModNodeId DecorationId { get; }
		public string DecorationSelector => DecorationId.Value.ToString("D");
		public string LibraryPath => Path.Combine(LibraryRoot, "library.json");
		public string DecorationActivationsPath => Path.Combine(LibraryRoot, "decoration-activations.json");

		public static async ValueTask<Fixture> CreateAsync()
		{
			var root = Path.Combine(Path.GetTempPath(), "hd2-cli-decoration-show-" + Guid.NewGuid().ToString("N"));
			var managerRoot = Path.Combine(root, "manager");
			var libraryRoot = Path.Combine(root, "library");
			var hostPath = "host";
			var decorationPath = "decoration";
			Directory.CreateDirectory(managerRoot);
			Directory.CreateDirectory(Path.Combine(libraryRoot, hostPath));
			Directory.CreateDirectory(Path.Combine(libraryRoot, decorationPath));
			await File.WriteAllTextAsync(Path.Combine(managerRoot, "settings.json"), JsonSerializer.Serialize(new { ModLibraryFolder = libraryRoot }));

			var now = DateTimeOffset.UtcNow;
			var host = new ModNode(
				ModNodeId.New(),
				hostPath,
				new ModNodeMetadata("Host", null, now, now),
				[new PatchGroupKey("0123456789abcdef", 0)],
				[]);
			var decoration = new ModNode(
				ModNodeId.New(),
				decorationPath,
				new ModNodeMetadata("Decoration", null, now, now, ModNodeKind.Decoration),
				[],
				[]);
			await new JsonModLibraryStore(new StoragePaths(managerRoot, libraryRoot), allowLegacyMigration: false)
				.SaveAsync(new LibrarySnapshot(1, now, new Dictionary<ModNodeId, ModNode> { [host.Id] = host, [decoration.Id] = decoration }, Array.Empty<Profile>()));
			var plan = new DecorationPlanDocument
			{
				Name = "Decoration",
				SourceUnits = [new DecorationSourceUnit { TypeId = 1, FileId = 2, MeshInfoIndex = 0, BodyVariant = "Any", Layer = "Armor" }],
				Plan = new DecorationAttachmentPlan { TargetModGuids = [host.Id.Value.ToString("N")], TargetPart = "Torso", TargetBodyVariant = "Dual", DualVariantMode = "AutoAssign", SourcePartLayers = ["Torso/Armor"] },
			};
			await File.WriteAllTextAsync(Path.Combine(libraryRoot, decorationPath, "decoration.json"), JsonSerializer.Serialize(plan));
			await new DecorationActivationStore(new StoragePaths(managerRoot, libraryRoot).DecorationActivationsPath)
				.SetEnabledHostsBatchAsync(new Dictionary<string, IReadOnlyCollection<string>> { [decoration.Id.Value.ToString("N")] = [host.Id.Value.ToString("N")] });
			return new Fixture(root, managerRoot, libraryRoot, decoration.Id);
		}

		public ValueTask DisposeAsync()
		{
			try { Directory.Delete(RootDirectory, recursive: true); }
			catch { }
			return ValueTask.CompletedTask;
		}
	}
}
