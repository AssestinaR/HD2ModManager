using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HD2ModCli;
using HD2ModCore.Application;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using Xunit;

namespace HD2ModCli.Tests;

[Collection("cli-console")]
public sealed class AssetMetadataManagementCliTests
{
	[Fact]
	public async Task Plan_StagesVerifiedRemoteContent_WithoutMutatingManagerResources()
	{
		await using var fixture = await Fixture.CreateAsync();
		var planPath = Path.Combine(fixture.RootDirectory, "asset-metadata-plan.json");
		var workspace = CliWorkspace.Open(CreateOptions(fixture.ManagerRoot));

		var build = await CliAssetMetadataPlanArtifacts.BuildAsync(
			workspace,
			repositoryArgument: null,
			planPath,
			overwrite: false,
			stagingServiceFactory: paths => new FixtureSyncService(paths));

		Assert.True(build.Document.IsReady);
		Assert.False(File.Exists(fixture.ArchiveHashesPath));
		Assert.True(File.Exists(Path.Combine(planPath + ".assets", "data", "resources", "archivehashes.json")));
		Assert.True(build.Document.Staging.Snapshot.IsCompleteAndVerified);
		Assert.Equal("https://example.invalid/hd2sdk", build.Document.Staging.RepositoryRawBaseUrl);

		await CliOutput.WriteExplicitFileAsync(planPath, CliOutput.ToJson(build.Document), overwrite: false);
		var validation = await CliAssetMetadataPlanArtifacts.ValidateAsync(build.Document, CliWorkspace.Open(CreateOptions(fixture.ManagerRoot)));
		Assert.True(validation.IsValid);

		var execution = await CliAssetMetadataExecutor.ApplyAsync(build.Document, CliWorkspace.Open(CreateOptions(fixture.ManagerRoot)));
		Assert.True(execution.Success);
		Assert.True(execution.SettingsUpdated);
		Assert.True(File.Exists(fixture.ArchiveHashesPath));
		Assert.Equal(FixtureSyncService.ArchiveHashesContent, await File.ReadAllTextAsync(fixture.ArchiveHashesPath));
		using var settings = JsonDocument.Parse(await File.ReadAllTextAsync(fixture.SettingsPath));
		Assert.True(settings.RootElement.TryGetProperty("LastAssetMetadataCheckUtc", out var checkedAt));
		Assert.NotEqual(JsonValueKind.Null, checkedAt.ValueKind);
	}

	[Fact]
	public async Task Validate_WhenStagedPayloadChanges_RejectsThePlan()
	{
		await using var fixture = await Fixture.CreateAsync();
		var planPath = Path.Combine(fixture.RootDirectory, "asset-metadata-plan.json");
		var build = await CliAssetMetadataPlanArtifacts.BuildAsync(
			CliWorkspace.Open(CreateOptions(fixture.ManagerRoot)),
			repositoryArgument: null,
			planPath,
			overwrite: false,
			stagingServiceFactory: paths => new FixtureSyncService(paths));
		var stagedArchiveHashes = Path.Combine(planPath + ".assets", "data", "resources", "archivehashes.json");
		await File.AppendAllTextAsync(stagedArchiveHashes, "\n{ }");

		var validation = await CliAssetMetadataPlanArtifacts.ValidateAsync(build.Document, CliWorkspace.Open(CreateOptions(fixture.ManagerRoot)));

		Assert.False(validation.IsValid);
		Assert.Contains(validation.Diagnostics, diagnostic => string.Equals(diagnostic.Code, "AssetMetadataStageChanged", StringComparison.Ordinal));
		Assert.False(File.Exists(fixture.ArchiveHashesPath));
	}

	[Fact]
	public async Task Apply_RequiresConfirm_BeforeItReadsAPlanOrWrites()
	{
		await using var fixture = await Fixture.CreateAsync();

		var exitCode = await CliApplication.RunAsync(
		[
			"assets", "metadata", "apply",
			"--plan", Path.Combine(fixture.RootDirectory, "missing.json"),
			"--manager-root", fixture.ManagerRoot,
			"--json",
		]);

		Assert.Equal(2, exitCode);
		Assert.False(File.Exists(fixture.ArchiveHashesPath));
	}

	[Fact]
	public async Task Inspect_ReportsMissingLocalCacheWithoutWriting()
	{
		await using var fixture = await Fixture.CreateAsync();

		var capture = await CliTestConsole.RunCapturedAsync(
			"assets", "metadata", "inspect",
			"--manager-root", fixture.ManagerRoot,
			"--json");

		Assert.Equal(2, capture.ExitCode);
		using var result = capture.ParseJson();
		Assert.False(result.RootElement.GetProperty("ok").GetBoolean());
		Assert.False(File.Exists(fixture.ArchiveHashesPath));
	}

	private static CliGlobalOptions CreateOptions(string managerRoot)
		=> new(managerRoot, null, null, null, null, null, null, null, null, null, null, false, false, false, false, false, false, false);

	private sealed class Fixture : IAsyncDisposable
	{
		private Fixture(string rootDirectory, string managerRoot)
		{
			RootDirectory = rootDirectory;
			ManagerRoot = managerRoot;
		}

		public string RootDirectory { get; }
		public string ManagerRoot { get; }
		public string SettingsPath => Path.Combine(ManagerRoot, "settings.json");
		public string ArchiveHashesPath => Path.Combine(ManagerRoot, "data", "resources", "archivehashes.json");

		public static async ValueTask<Fixture> CreateAsync()
		{
			var root = Path.Combine(Path.GetTempPath(), "hd2-cli-asset-metadata-" + Guid.NewGuid().ToString("N"));
			var managerRoot = Path.Combine(root, "manager");
			Directory.CreateDirectory(managerRoot);
			await File.WriteAllTextAsync(
				Path.Combine(managerRoot, "settings.json"),
				JsonSerializer.Serialize(new
				{
					AssetMetadataRepository = "https://example.invalid/hd2sdk",
					AutoUpdateAssetMetadata = false,
				}));
			return new Fixture(root, managerRoot);
		}

		public ValueTask DisposeAsync()
		{
			try { Directory.Delete(RootDirectory, recursive: true); }
			catch { }
			return ValueTask.CompletedTask;
		}
	}

	private sealed class FixtureSyncService : IAssetMetadataSyncService
	{
		public const string ArchiveHashesContent = "{\"armor\":{\"001\":\"value\"}}";
		private readonly StoragePaths _paths;

		public FixtureSyncService(StoragePaths paths) => _paths = paths;

		public async ValueTask<AssetMetadataSyncResult> SyncAsync(string repositoryRawBaseUrl, CancellationToken cancellationToken = default)
		{
			Directory.CreateDirectory(_paths.ResourcesDirectory);
			var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
			{
				["archivehashes.json"] = ArchiveHashesContent,
				["bonehash.txt"] = "1 root",
				["typehash.txt"] = "0011223344556677 unit",
				["friendlynames.txt"] = "1 asset",
			};
			var manifest = new AssetMetadataManifest
			{
				Source = repositoryRawBaseUrl,
				UpdatedAtUtc = DateTimeOffset.UtcNow,
			};
			foreach (var pair in files)
			{
				var path = Path.Combine(_paths.ResourcesDirectory, pair.Key);
				await File.WriteAllTextAsync(path, pair.Value, cancellationToken);
				manifest.Files[pair.Key] = new AssetMetadataFileManifest
				{
					Bytes = new FileInfo(path).Length,
					Sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pair.Value))),
				};
			}
			await File.WriteAllTextAsync(
				Path.Combine(_paths.ResourcesDirectory, "asset-metadata-manifest.json"),
				JsonSerializer.Serialize(manifest),
				cancellationToken);
			return new AssetMetadataSyncResult(true, manifest.UpdatedAtUtc, repositoryRawBaseUrl, files.Keys.ToArray(), null);
		}
	}
}
