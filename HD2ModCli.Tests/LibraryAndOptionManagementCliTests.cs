using System.Text.Json;
using HD2ModCli;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using Xunit;

namespace HD2ModCli.Tests;

[Collection("cli-console")]
public sealed class LibraryAndOptionManagementCliTests
{
	[Fact]
	public async Task LibrarySync_InspectPlanValidateApply_UsesPlannedIdAndOnlyWritesLibraryJson()
	{
		await using var fixture = await LibrarySyncFixture.CreateAsync(includeMissingRegisteredDirectory: false);
		var planPath = Path.Combine(fixture.RootDirectory, "library-sync-plan.json");
		var libraryBeforeInspect = await File.ReadAllBytesAsync(fixture.LibraryPath);

		Assert.Equal(0, await RunAsync(
			"library", "sync", "inspect",
			"--manager-root", fixture.ManagerRoot,
			"--json"));
		Assert.Equal(libraryBeforeInspect, await File.ReadAllBytesAsync(fixture.LibraryPath));

		Assert.Equal(0, await RunAsync(
			"library", "sync", "plan",
			"--manager-root", fixture.ManagerRoot,
			"--output", planPath,
			"--json"));

		using var plan = JsonDocument.Parse(await File.ReadAllTextAsync(planPath));
		Assert.Equal("hd2mod.library-synchronization-plan", plan.RootElement.GetProperty("schema").GetString());
		var additions = plan.RootElement.GetProperty("inspection").GetProperty("added");
		var addition = Assert.Single(additions.EnumerateArray());
		Assert.Equal(fixture.ExternalRelativePath, addition.GetProperty("relativePath").GetString());
		var plannedIdText = addition.GetProperty("nodeId").GetString();
		Assert.True(Guid.TryParse(plannedIdText, out var plannedId));

		Assert.Equal(0, await RunAsync(
			"library", "sync", "validate",
			"--manager-root", fixture.ManagerRoot,
			"--plan", planPath,
			"--json"));
		Assert.Equal(0, await RunAsync(
			"library", "sync", "apply",
			"--manager-root", fixture.ManagerRoot,
			"--plan", planPath,
			"--confirm",
			"--json"));

		var persisted = await new JsonModLibraryStore(new StoragePaths(fixture.ManagerRoot, fixture.LibraryRoot), allowLegacyMigration: false)
			.TryLoadReadOnlyAsync();
		Assert.NotNull(persisted);
		var added = Assert.Single(persisted!.Nodes.Values, node => string.Equals(node.RelativePath, fixture.ExternalRelativePath, StringComparison.OrdinalIgnoreCase));
		Assert.Equal(plannedId, added.Id.Value);
		Assert.Equal([new PatchGroupKey("0123456789abcdef", 0)], added.PatchGroups);

		Assert.Equal(fixture.ProfilesBefore, await File.ReadAllBytesAsync(fixture.ProfilesPath));
		Assert.Equal(fixture.OptionActivationsBefore, await File.ReadAllBytesAsync(fixture.OptionActivationsPath));
		Assert.Equal(fixture.ExternalPatchBefore, await File.ReadAllBytesAsync(fixture.ExternalPatchPath));
		Assert.Equal(fixture.ExternalSidecarBefore, await File.ReadAllBytesAsync(fixture.ExternalSidecarPath));
	}

	[Fact]
	public async Task LibrarySync_MissingRegisteredDirectory_BlocksPlanValidationAndApplyWithoutChangingState()
	{
		await using var fixture = await LibrarySyncFixture.CreateAsync(includeMissingRegisteredDirectory: true);
		var planPath = Path.Combine(fixture.RootDirectory, "blocked-library-sync-plan.json");
		var libraryBefore = await File.ReadAllBytesAsync(fixture.LibraryPath);
		var profilesBefore = await File.ReadAllBytesAsync(fixture.ProfilesPath);
		var activationsBefore = await File.ReadAllBytesAsync(fixture.OptionActivationsPath);

		Assert.Equal(0, await RunAsync(
			"library", "sync", "inspect",
			"--manager-root", fixture.ManagerRoot,
			"--json"));
		Assert.Equal(2, await RunAsync(
			"library", "sync", "plan",
			"--manager-root", fixture.ManagerRoot,
			"--output", planPath,
			"--json"));
		Assert.Equal(2, await RunAsync(
			"library", "sync", "validate",
			"--manager-root", fixture.ManagerRoot,
			"--plan", planPath,
			"--json"));
		Assert.Equal(2, await RunAsync(
			"library", "sync", "apply",
			"--manager-root", fixture.ManagerRoot,
			"--plan", planPath,
			"--confirm",
			"--json"));

		using var plan = JsonDocument.Parse(await File.ReadAllTextAsync(planPath));
		Assert.Equal(1, plan.RootElement.GetProperty("inspection").GetProperty("missing").GetArrayLength());
		Assert.Equal(libraryBefore, await File.ReadAllBytesAsync(fixture.LibraryPath));
		Assert.Equal(profilesBefore, await File.ReadAllBytesAsync(fixture.ProfilesPath));
		Assert.Equal(activationsBefore, await File.ReadAllBytesAsync(fixture.OptionActivationsPath));
	}

	[Fact]
	public async Task OptionShowEnableAllDisableAll_ChangesOnlyValidatedDirectHostsAndPreservesOrders()
	{
		await using var fixture = await OptionFixture.CreateAsync(invalidHostDeclaration: false);

		Assert.Equal(0, await RunAsync(
			"option", "show", fixture.SharedOptionSelector,
			"--manager-root", fixture.ManagerRoot,
			"--json"));
		Assert.Equal(fixture.LibraryBefore, await File.ReadAllBytesAsync(fixture.LibraryPath));

		Assert.Equal(0, await RunAsync(
			"option", "enable-all", fixture.SharedOptionSelector,
			"--manager-root", fixture.ManagerRoot,
			"--confirm",
			"--json"));
		var enabled = await fixture.LoadActivationsAsync();
		Assert.True(enabled.IsEnabled(fixture.SharedOptionKey, fixture.HostAKey));
		Assert.True(enabled.IsEnabled(fixture.SharedOptionKey, fixture.HostBKey));
		Assert.Equal(8, enabled.GetEffectiveOrder(fixture.SharedOptionKey, fixture.HostAKey));
		Assert.Equal(27, enabled.GetEffectiveOrder(fixture.SharedOptionKey, fixture.HostBKey));
		Assert.True(enabled.IsEnabled(fixture.UnrelatedOptionKey, fixture.HostAKey));
		Assert.Equal(3, enabled.GetEffectiveOrder(fixture.UnrelatedOptionKey, fixture.HostAKey));

		Assert.Equal(0, await RunAsync(
			"option", "disable-all", fixture.SharedOptionSelector,
			"--manager-root", fixture.ManagerRoot,
			"--confirm",
			"--json"));
		var disabled = await fixture.LoadActivationsAsync();
		Assert.False(disabled.IsEnabled(fixture.SharedOptionKey, fixture.HostAKey));
		Assert.False(disabled.IsEnabled(fixture.SharedOptionKey, fixture.HostBKey));
		Assert.Equal(8, disabled.GetEffectiveOrder(fixture.SharedOptionKey, fixture.HostAKey));
		Assert.Equal(27, disabled.GetEffectiveOrder(fixture.SharedOptionKey, fixture.HostBKey));
		Assert.True(disabled.IsEnabled(fixture.UnrelatedOptionKey, fixture.HostAKey));
		Assert.Equal(3, disabled.GetEffectiveOrder(fixture.UnrelatedOptionKey, fixture.HostAKey));
		Assert.Equal(fixture.LibraryBefore, await File.ReadAllBytesAsync(fixture.LibraryPath));
		Assert.Equal(fixture.ProfilesBefore, await File.ReadAllBytesAsync(fixture.ProfilesPath));
	}

	[Fact]
	public async Task OptionShowAndEnableAll_InvalidHostDeclaration_RefuseWithoutWritingActivationState()
	{
		await using var fixture = await OptionFixture.CreateAsync(invalidHostDeclaration: true);
		var activationsBefore = await File.ReadAllBytesAsync(fixture.OptionActivationsPath);

		Assert.Equal(2, await RunAsync(
			"option", "show", fixture.SharedOptionSelector,
			"--manager-root", fixture.ManagerRoot,
			"--json"));
		Assert.Equal(2, await RunAsync(
			"option", "enable-all", fixture.SharedOptionSelector,
			"--manager-root", fixture.ManagerRoot,
			"--confirm",
			"--json"));

		Assert.Equal(activationsBefore, await File.ReadAllBytesAsync(fixture.OptionActivationsPath));
		Assert.Equal(fixture.LibraryBefore, await File.ReadAllBytesAsync(fixture.LibraryPath));
		Assert.Equal(fixture.ProfilesBefore, await File.ReadAllBytesAsync(fixture.ProfilesPath));
	}

	private static Task<int> RunAsync(params string[] args) => CliApplication.RunAsync(args);

	private sealed class LibrarySyncFixture : IAsyncDisposable
	{
		private LibrarySyncFixture(
			string rootDirectory,
			string managerRoot,
			string libraryRoot,
			string externalRelativePath,
			string externalPatchPath,
			string externalSidecarPath,
			byte[] profilesBefore,
			byte[] optionActivationsBefore,
			byte[] externalPatchBefore,
			byte[] externalSidecarBefore)
		{
			RootDirectory = rootDirectory;
			ManagerRoot = managerRoot;
			LibraryRoot = libraryRoot;
			ExternalRelativePath = externalRelativePath;
			ExternalPatchPath = externalPatchPath;
			ExternalSidecarPath = externalSidecarPath;
			ProfilesBefore = profilesBefore;
			OptionActivationsBefore = optionActivationsBefore;
			ExternalPatchBefore = externalPatchBefore;
			ExternalSidecarBefore = externalSidecarBefore;
		}

		public string RootDirectory { get; }
		public string ManagerRoot { get; }
		public string LibraryRoot { get; }
		public string ExternalRelativePath { get; }
		public string ExternalPatchPath { get; }
		public string ExternalSidecarPath { get; }
		public string LibraryPath => Path.Combine(LibraryRoot, "library.json");
		public string ProfilesPath => Path.Combine(LibraryRoot, "profiles.json");
		public string OptionActivationsPath => Path.Combine(LibraryRoot, "option-activations.json");
		public byte[] ProfilesBefore { get; }
		public byte[] OptionActivationsBefore { get; }
		public byte[] ExternalPatchBefore { get; }
		public byte[] ExternalSidecarBefore { get; }

		public static async ValueTask<LibrarySyncFixture> CreateAsync(bool includeMissingRegisteredDirectory)
		{
			var root = Path.Combine(Path.GetTempPath(), "hd2-cli-library-sync-" + Guid.NewGuid().ToString("N"));
			var managerRoot = Path.Combine(root, "manager");
			var libraryRoot = Path.Combine(root, "library");
			var registeredRelativePath = includeMissingRegisteredDirectory ? "missing-registered" : "registered";
			var externalRelativePath = "external-patch";
			Directory.CreateDirectory(managerRoot);
			Directory.CreateDirectory(libraryRoot);
			await File.WriteAllTextAsync(
				Path.Combine(managerRoot, "settings.json"),
				JsonSerializer.Serialize(new { ModLibraryFolder = libraryRoot }));

			if (!includeMissingRegisteredDirectory)
			{
				var registeredDirectory = Path.Combine(libraryRoot, registeredRelativePath);
				Directory.CreateDirectory(registeredDirectory);
				await File.WriteAllBytesAsync(Path.Combine(registeredDirectory, "fedcba9876543210.patch_1"), [0x10, 0x20]);
			}
			var externalDirectory = Path.Combine(libraryRoot, externalRelativePath);
			Directory.CreateDirectory(externalDirectory);
			var externalPatchPath = Path.Combine(externalDirectory, "0123456789abcdef.patch_0");
			var externalSidecarPath = externalPatchPath + ".gpu_resources";
			await File.WriteAllBytesAsync(externalPatchPath, [0x31, 0x32, 0x33]);
			await File.WriteAllBytesAsync(externalSidecarPath, [0x41, 0x42]);

			var nodeId = ModNodeId.New();
			var now = DateTimeOffset.UtcNow;
			var registered = new ModNode(
				nodeId,
				registeredRelativePath,
				new ModNodeMetadata("Registered", null, now, now),
				[new PatchGroupKey("fedcba9876543210", 1)],
				[]);
			var profile = new Profile(ProfileId.New(), "Default", now, now, [new ProfileEntry(nodeId, 0, now)]);
			var paths = new StoragePaths(managerRoot, libraryRoot);
			await new JsonModLibraryStore(paths, allowLegacyMigration: false).SaveAsync(
				new LibrarySnapshot(1, now, new Dictionary<ModNodeId, ModNode> { [nodeId] = registered }, [profile], profile.Id));
			await new OptionActivationStore(Path.Combine(libraryRoot, "option-activations.json"))
				.InitializeMissingStatesAsync([new OptionActivationInitialization(Guid.NewGuid().ToString("N"), nodeId.Value.ToString("N"), true, 9)]);

			return new LibrarySyncFixture(
				root,
				managerRoot,
				libraryRoot,
				externalRelativePath,
				externalPatchPath,
				externalSidecarPath,
				await File.ReadAllBytesAsync(Path.Combine(libraryRoot, "profiles.json")),
				await File.ReadAllBytesAsync(Path.Combine(libraryRoot, "option-activations.json")),
				await File.ReadAllBytesAsync(externalPatchPath),
				await File.ReadAllBytesAsync(externalSidecarPath));
		}

		public ValueTask DisposeAsync()
		{
			try { Directory.Delete(RootDirectory, recursive: true); } catch { }
			return ValueTask.CompletedTask;
		}
	}

	private sealed class OptionFixture : IAsyncDisposable
	{
		private OptionFixture(
			string rootDirectory,
			string managerRoot,
			string libraryRoot,
			ModNodeId hostA,
			ModNodeId hostB,
			ModNodeId sharedOption,
			ModNodeId unrelatedOption,
			byte[] libraryBefore,
			byte[] profilesBefore)
		{
			RootDirectory = rootDirectory;
			ManagerRoot = managerRoot;
			LibraryRoot = libraryRoot;
			HostAKey = hostA.Value.ToString("N");
			HostBKey = hostB.Value.ToString("N");
			SharedOptionKey = sharedOption.Value.ToString("N");
			UnrelatedOptionKey = unrelatedOption.Value.ToString("N");
			SharedOptionSelector = sharedOption.Value.ToString("D");
			LibraryBefore = libraryBefore;
			ProfilesBefore = profilesBefore;
		}

		public string RootDirectory { get; }
		public string ManagerRoot { get; }
		public string LibraryRoot { get; }
		public string LibraryPath => Path.Combine(LibraryRoot, "library.json");
		public string ProfilesPath => Path.Combine(LibraryRoot, "profiles.json");
		public string OptionActivationsPath => Path.Combine(LibraryRoot, "option-activations.json");
		public string HostAKey { get; }
		public string HostBKey { get; }
		public string SharedOptionKey { get; }
		public string UnrelatedOptionKey { get; }
		public string SharedOptionSelector { get; }
		public byte[] LibraryBefore { get; }
		public byte[] ProfilesBefore { get; }

		public static async ValueTask<OptionFixture> CreateAsync(bool invalidHostDeclaration)
		{
			var root = Path.Combine(Path.GetTempPath(), "hd2-cli-option-" + Guid.NewGuid().ToString("N"));
			var managerRoot = Path.Combine(root, "manager");
			var libraryRoot = Path.Combine(root, "library");
			Directory.CreateDirectory(managerRoot);
			Directory.CreateDirectory(libraryRoot);
			await File.WriteAllTextAsync(
				Path.Combine(managerRoot, "settings.json"),
				JsonSerializer.Serialize(new { ModLibraryFolder = libraryRoot }));

			var now = DateTimeOffset.UtcNow;
			var hostA = CreateNode(ModNodeKind.Standard, "Host A", "host-a", now);
			var hostB = CreateNode(ModNodeKind.Standard, "Host B", "host-b", now);
			var shared = CreateOption(
				"Shared option",
				"shared-option",
				invalidHostDeclaration
					? [hostA.Id.Value.ToString("N"), "not-a-guid"]
					: [hostA.Id.Value.ToString("N"), hostB.Id.Value.ToString("N")],
				now);
			var unrelated = CreateOption("Unrelated option", "unrelated-option", [hostA.Id.Value.ToString("N")], now);
			foreach (var node in new[] { hostA, hostB, shared, unrelated })
				Directory.CreateDirectory(Path.Combine(libraryRoot, node.RelativePath));

			var profile = new Profile(ProfileId.New(), "Default", now, now, [new ProfileEntry(hostA.Id, 0, now)]);
			await new JsonModLibraryStore(new StoragePaths(managerRoot, libraryRoot), allowLegacyMigration: false).SaveAsync(
				new LibrarySnapshot(
					1,
					now,
					new Dictionary<ModNodeId, ModNode>
					{
						[hostA.Id] = hostA,
						[hostB.Id] = hostB,
						[shared.Id] = shared,
						[unrelated.Id] = unrelated,
					},
					[profile],
					profile.Id));
			var activations = new OptionActivationStore(Path.Combine(libraryRoot, "option-activations.json"));
			await activations.InitializeMissingStatesAsync(
			[
				new OptionActivationInitialization(shared.Id.Value.ToString("N"), hostA.Id.Value.ToString("N"), false, 8),
				new OptionActivationInitialization(shared.Id.Value.ToString("N"), hostB.Id.Value.ToString("N"), true, 27),
				new OptionActivationInitialization(unrelated.Id.Value.ToString("N"), hostA.Id.Value.ToString("N"), true, 3),
			]);

			return new OptionFixture(
				root,
				managerRoot,
				libraryRoot,
				hostA.Id,
				hostB.Id,
				shared.Id,
				unrelated.Id,
				await File.ReadAllBytesAsync(Path.Combine(libraryRoot, "library.json")),
				await File.ReadAllBytesAsync(Path.Combine(libraryRoot, "profiles.json")));
		}

		public ValueTask<OptionActivationSnapshot> LoadActivationsAsync()
			=> OptionActivationStore.TryLoadReadOnlySnapshotAsync(OptionActivationsPath);

		public ValueTask DisposeAsync()
		{
			try { Directory.Delete(RootDirectory, recursive: true); } catch { }
			return ValueTask.CompletedTask;
		}

		private static ModNode CreateNode(ModNodeKind kind, string name, string relativePath, DateTimeOffset now)
			=> new(
				ModNodeId.New(),
				relativePath,
				new ModNodeMetadata(name, null, now, now, kind),
				[],
				[]);

		private static ModNode CreateOption(string name, string relativePath, IReadOnlyList<string> hosts, DateTimeOffset now)
			=> new(
				ModNodeId.New(),
				relativePath,
				new ModNodeMetadata(name, null, now, now, ModNodeKind.Option, hosts, OptionOrder: 0),
				[],
				[]);
	}
}
