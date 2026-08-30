using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using HD2ModAdaptation.PatchReconstruction;
using HD2ModAdaptation.PatchReconstruction.Lua;
using HD2ModCli;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using Xunit;
using AdaptationPatchEntryPayload = HD2ModAdaptation.PatchReconstruction.PatchEntryPayload;
using AdaptationPatchTocEntry = HD2ModAdaptation.PatchReconstruction.PatchTocEntry;

namespace HD2ModCli.Tests;

[Collection("cli-console")]
public sealed class PatchResourceRepackagingCliTests
{
	[Fact]
	public async Task PatchRepackage_InspectPlanValidateApply_WritesIndependentPatchWithoutChangingSource()
	{
		await using var fixture = await PatchResourceRepackagingFixture.CreateAsync();
		var planPath = Path.Combine(fixture.RootDirectory, "repackage-plan.json");
		var outputDirectory = Path.Combine(fixture.RootDirectory, "repacked-output");
		var sourceHashBefore = await ComputeSha256Async(fixture.SourcePatchPath);

		var inspectExit = await RunAsync("patch", "repackage", "inspect", fixture.NodeSelector,
			"--library", fixture.LibraryRoot, "--json");
		var planExit = await RunAsync("patch", "repackage", "plan", fixture.NodeSelector,
			"--library", fixture.LibraryRoot, "--output", planPath, "--json");
		var validateExit = await RunAsync("patch", "repackage", "validate",
			"--library", fixture.LibraryRoot, "--plan", planPath, "--json");
		var applyExit = await RunAsync("patch", "repackage", "apply",
			"--library", fixture.LibraryRoot, "--plan", planPath, "--output", outputDirectory, "--confirm", "--json");

		Assert.Equal(0, inspectExit);
		Assert.Equal(0, planExit);
		Assert.Equal(0, validateExit);
		Assert.Equal(0, applyExit);
		Assert.True(File.Exists(planPath));
		Assert.True(Directory.Exists(outputDirectory));

		using var plan = JsonDocument.Parse(await File.ReadAllTextAsync(planPath));
		Assert.Equal("hd2mod.patch-resource-repackage-plan", plan.RootElement.GetProperty("schema").GetString());
		Assert.True(plan.RootElement.GetProperty("selections").GetArrayLength() > 0);

		var outputPatch = Assert.Single(
			Directory.EnumerateFiles(outputDirectory, "*.patch_*", SearchOption.TopDirectoryOnly),
			path => !path.EndsWith(".stream", StringComparison.OrdinalIgnoreCase)
				&& !path.EndsWith(".gpu_resources", StringComparison.OrdinalIgnoreCase));
		Assert.NotEqual(Path.GetFullPath(fixture.SourcePatchPath), Path.GetFullPath(outputPatch));
		Assert.True(new FileInfo(outputPatch).Length > 0);
		Assert.Equal(sourceHashBefore, await ComputeSha256Async(fixture.SourcePatchPath));
	}

	[Fact]
	public async Task PatchRepackageApply_RejectsLibraryChildAndExistingOutputDirectory()
	{
		await using var fixture = await PatchResourceRepackagingFixture.CreateAsync();
		var planPath = Path.Combine(fixture.RootDirectory, "repackage-plan.json");
		Assert.Equal(0, await RunAsync("patch", "repackage", "plan", fixture.NodeSelector,
			"--library", fixture.LibraryRoot, "--output", planPath, "--json"));

		var libraryChildOutput = Path.Combine(fixture.LibraryRoot, "must-not-write-here");
		var libraryExit = await RunAsync("patch", "repackage", "apply",
			"--library", fixture.LibraryRoot, "--plan", planPath, "--output", libraryChildOutput, "--confirm", "--json");

		var existingOutput = Path.Combine(fixture.RootDirectory, "existing-output");
		Directory.CreateDirectory(existingOutput);
		var sentinelPath = Path.Combine(existingOutput, "keep.txt");
		await File.WriteAllTextAsync(sentinelPath, "do not touch");
		var existingExit = await RunAsync("patch", "repackage", "apply",
			"--library", fixture.LibraryRoot, "--plan", planPath, "--output", existingOutput, "--confirm", "--json");

		Assert.Equal(2, libraryExit);
		Assert.False(Directory.Exists(libraryChildOutput));
		Assert.Equal(2, existingExit);
		Assert.Equal("do not touch", await File.ReadAllTextAsync(sentinelPath));
	}

	[Fact]
	public async Task PatchRepackageValidate_RejectsPlanWhenSourcePatchChanges()
	{
		await using var fixture = await PatchResourceRepackagingFixture.CreateAsync();
		var planPath = Path.Combine(fixture.RootDirectory, "repackage-plan.json");
		Assert.Equal(0, await RunAsync("patch", "repackage", "plan", fixture.NodeSelector,
			"--library", fixture.LibraryRoot, "--output", planPath, "--json"));

		await File.AppendAllTextAsync(fixture.SourcePatchPath, "source changed after planning");
		var validateExit = await RunAsync("patch", "repackage", "validate",
			"--library", fixture.LibraryRoot, "--plan", planPath, "--json");

		Assert.Equal(2, validateExit);
	}

	private static Task<int> RunAsync(params string[] args) => CliApplication.RunAsync(args);

	private static async Task<string> ComputeSha256Async(string path)
	{
		await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
		return Convert.ToHexString(await SHA256.HashDataAsync(stream));
	}

	private sealed class PatchResourceRepackagingFixture : IAsyncDisposable
	{
		private PatchResourceRepackagingFixture(string rootDirectory, string libraryRoot, ModNodeId nodeId, string sourcePatchPath)
		{
			RootDirectory = rootDirectory;
			LibraryRoot = libraryRoot;
			NodeId = nodeId;
			SourcePatchPath = sourcePatchPath;
		}

		public string RootDirectory { get; }
		public string LibraryRoot { get; }
		public ModNodeId NodeId { get; }
		public string NodeSelector => NodeId.Value.ToString("D");
		public string SourcePatchPath { get; }

		public static async ValueTask<PatchResourceRepackagingFixture> CreateAsync()
		{
			var root = Path.Combine(Path.GetTempPath(), "hd2-cli-repackage-" + Guid.NewGuid().ToString("N"));
			var managerRoot = Path.Combine(root, "manager");
			var libraryRoot = Path.Combine(root, "library");
			var modRelativePath = "source-mod";
			var sourceDirectory = Path.Combine(libraryRoot, modRelativePath);
			var nodeId = ModNodeId.New();
			var sourcePatchPath = Path.Combine(sourceDirectory, $"{LuaAssetKeys.BootArchiveId:x16}.patch_0");
			Directory.CreateDirectory(managerRoot);
			Directory.CreateDirectory(sourceDirectory);
			await WriteFixturePatchAsync(sourcePatchPath);

			var now = DateTimeOffset.UtcNow;
			var node = new ModNode(
				nodeId,
				modRelativePath,
				new ModNodeMetadata("Patch Repackage Fixture", null, now, now),
				[new PatchGroupKey($"{LuaAssetKeys.BootArchiveId:x16}", 0)],
				[]);
			var snapshot = new LibrarySnapshot(
				1,
				now,
				new Dictionary<ModNodeId, ModNode> { [nodeId] = node },
				Array.Empty<Profile>());
			await new JsonModLibraryStore(new StoragePaths(managerRoot, libraryRoot), allowLegacyMigration: false).SaveAsync(snapshot);

			return new PatchResourceRepackagingFixture(root, libraryRoot, nodeId, sourcePatchPath);
		}

		private static async ValueTask WriteFixturePatchAsync(string patchPath)
		{
			var header = new byte[0xc0];
			BinaryPrimitives.WriteUInt32LittleEndian(header, 0xF0000011);
			BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 1);
			BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), 1);
			BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(0x44), LuaAssetKeys.LuaTypeId);
			BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(0x4c), 1);
			BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(0x5c), LuaAssetKeys.BootFileId);
			BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(0x64), LuaAssetKeys.LuaTypeId);
			var entry = new AdaptationPatchTocEntry(LuaAssetKeys.BootAssetKey, patchPath, Path.GetFileName(patchPath));
			var document = new LuaPatchDocument(
				patchPath,
				entry,
				new AdaptationPatchEntryPayload(entry, [], [], []),
				header,
				[0x1b, 0x4c, 0x4a],
				[new LuaPatchBlock("fixture", string.Empty, "local fixture = true", false)],
				LuaPatchWrapperKind.Single,
				null,
				string.Empty);
			var composer = new LuaPatchComposer();
			await composer.WriteAsync(patchPath, composer.Compose([document]));
		}

		public ValueTask DisposeAsync()
		{
			try { Directory.Delete(RootDirectory, recursive: true); }
			catch { }
			return ValueTask.CompletedTask;
		}
	}
}
