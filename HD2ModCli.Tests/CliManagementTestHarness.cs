using System.Text.Json;
using HD2ModCli;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCli.Tests;

internal sealed record CliCommandCapture(int ExitCode, string StandardOutput, string StandardError)
{
	public JsonDocument ParseJson()
		=> JsonDocument.Parse(StandardOutput.Trim());
}

internal static class CliTestConsole
{
	private static readonly SemaphoreSlim Gate = new(1, 1);

	public static async Task<CliCommandCapture> RunCapturedAsync(params string[] args)
	{
		await Gate.WaitAsync().ConfigureAwait(false);
		// CLI 测试跑的是完整管线；Lua 部署默认关闭是给用户的保护（游戏更新后 Lua 会崩），
		// 但断言 Lua 行为的用例需要它开着。这里显式打开，让"默认值"与"测试"互不干扰。
		HD2ModCore.Infrastructure.LuaDeploymentService.DeploymentEnabled = true;
		var originalOut = Console.Out;
		var originalError = Console.Error;
		using var output = new StringWriter();
		using var error = new StringWriter();
		try
		{
			Console.SetOut(output);
			Console.SetError(error);
			var exitCode = await CliApplication.RunAsync(args).ConfigureAwait(false);
			return new CliCommandCapture(exitCode, output.ToString(), error.ToString());
		}
		finally
		{
			Console.SetOut(originalOut);
			Console.SetError(originalError);
			Gate.Release();
		}
	}
}

internal sealed class CliManagementFixture : IAsyncDisposable
{
	private CliManagementFixture(
		string rootDirectory,
		string managerRoot,
		string libraryRoot,
		string gameDataDirectory,
		ModNode standardA,
		ModNode standardB,
		ModNode standardC,
		ModNode rootHost,
		ModNode alpha,
		ModNode beta,
		ModNode gamma,
		ModNode nestedHost,
		ModNode nestedChild,
		Profile defaultProfile)
	{
		RootDirectory = rootDirectory;
		ManagerRoot = managerRoot;
		LibraryRoot = libraryRoot;
		GameDataDirectory = gameDataDirectory;
		StandardA = standardA;
		StandardB = standardB;
		StandardC = standardC;
		RootHost = rootHost;
		Alpha = alpha;
		Beta = beta;
		Gamma = gamma;
		NestedHost = nestedHost;
		NestedChild = nestedChild;
		DefaultProfile = defaultProfile;
	}

	public string RootDirectory { get; }
	public string ManagerRoot { get; }
	public string LibraryRoot { get; }
	public string GameDataDirectory { get; }
	public string LibraryPath => Path.Combine(LibraryRoot, "library.json");
	public string ProfilesPath => Path.Combine(LibraryRoot, "profiles.json");
	public string OptionActivationsPath => Path.Combine(LibraryRoot, "option-activations.json");
	public ModNode StandardA { get; }
	public ModNode StandardB { get; }
	public ModNode StandardC { get; }
	public ModNode RootHost { get; }
	public ModNode Alpha { get; }
	public ModNode Beta { get; }
	public ModNode Gamma { get; }
	public ModNode NestedHost { get; }
	public ModNode NestedChild { get; }
	public Profile DefaultProfile { get; }
	public string StandardASelector => Selector(StandardA.Id);
	public string StandardBSelector => Selector(StandardB.Id);
	public string StandardCSelector => Selector(StandardC.Id);
	public string RootHostSelector => Selector(RootHost.Id);
	public string AlphaSelector => Selector(Alpha.Id);
	public string BetaSelector => Selector(Beta.Id);
	public string GammaSelector => Selector(Gamma.Id);
	public string NestedHostSelector => Selector(NestedHost.Id);
	public string NestedChildSelector => Selector(NestedChild.Id);
	public string DefaultProfileSelector => DefaultProfile.Id.Value.ToString("D");

	public static async ValueTask<CliManagementFixture> CreateAsync()
	{
		var root = Path.Combine(Path.GetTempPath(), "hd2-cli-management-" + Guid.NewGuid().ToString("N"));
		var managerRoot = Path.Combine(root, "manager");
		var libraryRoot = Path.Combine(root, "library");
		var gameRoot = Path.Combine(root, "Helldivers 2");
		var gameData = Path.Combine(gameRoot, "data");
		Directory.CreateDirectory(managerRoot);
		Directory.CreateDirectory(libraryRoot);
		Directory.CreateDirectory(Path.Combine(gameRoot, "bin"));
		Directory.CreateDirectory(gameData);
		await File.WriteAllTextAsync(Path.Combine(gameRoot, "bin", "helldivers2.exe"), string.Empty);
		await File.WriteAllTextAsync(
			Path.Combine(managerRoot, "settings.json"),
			JsonSerializer.Serialize(new { ModLibraryFolder = libraryRoot, GameDataFolder = gameData }));

		var now = DateTimeOffset.UtcNow;
		var standardA = CreateStandard("Standard A", "standard-a", "1111111111111111", now);
		var standardB = CreateStandard("Standard B", "standard-b", "2222222222222222", now);
		var standardC = CreateStandard("Standard C", "standard-c", "3333333333333333", now);
		var rootHost = CreateStandard("Option Root", "option-root", "4444444444444444", now);
		var alpha = CreateOption("Alpha", "option-alpha", [Key(rootHost.Id)], 0, now);
		var beta = CreateOption("Beta", "option-beta", [Key(rootHost.Id)], 1, now);
		var gamma = CreateOption("Gamma", "option-gamma", [Key(rootHost.Id)], 2, now);
		var nestedHost = CreateOption("Nested Host", "option-nested-host", [Key(rootHost.Id)], 3, now);
		var nestedChild = CreateOption("Nested Child", "option-nested-child", [Key(nestedHost.Id)], 0, now);
		var nodes = new[] { standardA, standardB, standardC, rootHost, alpha, beta, gamma, nestedHost, nestedChild };
		foreach (var node in nodes)
		{
			var directory = Path.Combine(libraryRoot, node.RelativePath);
			Directory.CreateDirectory(directory);
			foreach (var group in node.PatchGroups)
				await File.WriteAllBytesAsync(Path.Combine(directory, $"{group.ArchiveHex16}.patch_{group.PatchIndex}"), [0x01]);
		}

		var profile = new Profile(
			ProfileId.New(),
			"Default",
			now,
			now,
			[new ProfileEntry(standardA.Id, 0, now), new ProfileEntry(standardB.Id, 1, now)]);
		var paths = new StoragePaths(managerRoot, libraryRoot);
		await new JsonModLibraryStore(paths, allowLegacyMigration: false).SaveAsync(
			new LibrarySnapshot(1, now, nodes.ToDictionary(node => node.Id), [profile], profile.Id));
		var activations = new OptionActivationStore(Path.Combine(libraryRoot, "option-activations.json"));
		await activations.InitializeMissingStatesAsync(
		[
			new OptionActivationInitialization(Key(alpha.Id), Key(rootHost.Id), false, 0),
			new OptionActivationInitialization(Key(beta.Id), Key(rootHost.Id), true, 1),
			new OptionActivationInitialization(Key(gamma.Id), Key(rootHost.Id), false, 2),
			new OptionActivationInitialization(Key(nestedHost.Id), Key(rootHost.Id), true, 3),
			new OptionActivationInitialization(Key(nestedChild.Id), Key(nestedHost.Id), false, 0),
		]);

		return new CliManagementFixture(
			root, managerRoot, libraryRoot, gameData,
			standardA, standardB, standardC, rootHost, alpha, beta, gamma, nestedHost, nestedChild, profile);
	}

	public async ValueTask<LibrarySnapshot> LoadSnapshotAsync()
		=> await new JsonModLibraryStore(new StoragePaths(ManagerRoot, LibraryRoot), allowLegacyMigration: false)
			.TryLoadReadOnlyAsync()
			?? throw new InvalidOperationException("The fixture library snapshot is missing.");

	public ValueTask<OptionActivationSnapshot> LoadOptionActivationsAsync()
		=> OptionActivationStore.TryLoadReadOnlySnapshotAsync(OptionActivationsPath);

	public async ValueTask<IReadOnlyList<ModNodeId>> LoadRootOptionOrderAsync()
	{
		var activations = await LoadOptionActivationsAsync();
		var hostKey = Key(RootHost.Id);
		return new[] { Alpha, Beta, Gamma, NestedHost }
			.OrderBy(option => activations.GetEffectiveOrder(Key(option.Id), hostKey) ?? int.MaxValue)
			.ThenBy(option => option.Id.Value)
			.Select(option => option.Id)
			.ToArray();
	}

	public static string Selector(ModNodeId id) => id.Value.ToString("D");
	public static string Key(ModNodeId id) => id.Value.ToString("N");

	public ValueTask DisposeAsync()
	{
		try { Directory.Delete(RootDirectory, recursive: true); } catch { }
		return ValueTask.CompletedTask;
	}

	private static ModNode CreateStandard(string name, string relativePath, string archiveHex16, DateTimeOffset now)
		=> new(
			ModNodeId.New(),
			relativePath,
			new ModNodeMetadata(name, null, now, now),
			[new PatchGroupKey(archiveHex16, 0)],
			[]);

	private static ModNode CreateOption(string name, string relativePath, IReadOnlyList<string> hosts, int optionOrder, DateTimeOffset now)
		=> new(
			ModNodeId.New(),
			relativePath,
			new ModNodeMetadata(name, null, now, now, ModNodeKind.Option, hosts, OptionOrder: optionOrder),
			[],
			[]);
}
