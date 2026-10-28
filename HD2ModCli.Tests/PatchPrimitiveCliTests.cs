using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using HD2ModAdaptation.PatchReconstruction;
using HD2ModAdaptation.PatchReconstruction.Lua;
using HD2ModCli;
using Xunit;
using AssetKey = HD2ModAdaptation.PatchReconstruction.AssetKey;

namespace HD2ModCli.Tests;

/// <summary>
/// Covers the file-in / file-out Patch primitive commands: they must work on
/// arbitrary Patch paths (never requiring a registered Mod library), must never
/// mutate their source, and must publish only into a fresh --output directory.
/// </summary>
[Collection("cli-console")]
public sealed class PatchPrimitiveCliTests
{
	[Fact]
	public async Task PatchEntriesList_EnumeratesArbitraryPatchWithoutLibrary()
	{
		await using var fixture = await PatchPrimitiveFixture.CreateAsync();

		var capture = await CliTestConsole.RunCapturedAsync(
			"patch", "entries", "list", fixture.PatchPath, "--json");

		Assert.Equal(0, capture.ExitCode);
		using var json = capture.ParseJson();
		var patch = json.RootElement.GetProperty("patch");
		Assert.Equal(Path.GetFullPath(fixture.PatchPath), patch.GetProperty("tocPath").GetString());
		// One boot-Lua entry plus the two synthetic entries appended by the fixture.
		Assert.Equal(3, patch.GetProperty("entryCount").GetInt32());
		var entries = patch.GetProperty("entries");
		Assert.Equal(3, entries.GetArrayLength());
		// No --library / --manager-root was supplied: this must succeed purely
		// from the file path.
		Assert.All(entries.EnumerateArray(), entry =>
			Assert.False(string.IsNullOrWhiteSpace(entry.GetProperty("typeId").GetString())));
	}

	[Fact]
	public async Task PatchEntryExport_WritesThreeBareSegmentsAndNeverTouchesSource()
	{
		await using var fixture = await PatchPrimitiveFixture.CreateAsync();
		var outputDirectory = Path.Combine(fixture.RootDirectory, "exported");
		var sourceHashBefore = await Sha256Async(fixture.PatchPath);

		var capture = await CliTestConsole.RunCapturedAsync(
			"patch", "entry", "export", fixture.PatchPath,
			$"0x{FixturePatchB.TypeId:x16}", $"0x{FixturePatchB.FileId:x16}",
			"--output", outputDirectory, "--json");

		Assert.Equal(0, capture.ExitCode);
		var keyDirectory = Path.Combine(outputDirectory, $"0x{FixturePatchB.TypeId:x16}_{FixturePatchB.FileId:x16}");
		Assert.True(File.Exists(Path.Combine(keyDirectory, "toc.bin")));
		Assert.True(File.Exists(Path.Combine(keyDirectory, "stream.bin")));
		Assert.True(File.Exists(Path.Combine(keyDirectory, "gpu_resources.bin")));
		Assert.True(File.Exists(Path.Combine(keyDirectory, "entry.json")));
		Assert.Equal(sourceHashBefore, await Sha256Async(fixture.PatchPath));
	}

	[Fact]
	public async Task PatchCompose_MergesCrossSourceEntriesUnderHeaderTemplate()
	{
		await using var fixture = await PatchPrimitiveFixture.CreateAsync();
		var outputDirectory = Path.Combine(fixture.RootDirectory, "composed");

		var capture = await CliTestConsole.RunCapturedAsync(
			"patch", "compose",
			"--header", fixture.PatchPath,
			"--pick", fixture.SecondPatchPath, $"0x{FixturePatchB.TypeId:x16}", $"0x{FixturePatchB.FileId:x16}",
			"--pick", fixture.PatchPath, $"0x{FixturePatchA.TypeId:x16}", $"0x{FixturePatchA.FileId:x16}",
			"--output", outputDirectory, "--json");

		Assert.Equal(0, capture.ExitCode);
		var composed = Directory.EnumerateFiles(outputDirectory, "*.patch_*", SearchOption.TopDirectoryOnly)
			.Single(path => !path.EndsWith(".stream", StringComparison.OrdinalIgnoreCase)
				&& !path.EndsWith(".gpu_resources", StringComparison.OrdinalIgnoreCase));

		// Repacking must not retain the header template's own payload.
		var entries = await new PatchTocScanner().ScanEntriesAsync(composed);
		Assert.Contains(entries, entry => entry.AssetKey == FixturePatchA);
		Assert.Contains(entries, entry => entry.AssetKey == FixturePatchB);
	}

	[Fact]
	public async Task PatchEdit_RemovesEntryAndPublishesFreshPatch()
	{
		await using var fixture = await PatchPrimitiveFixture.CreateAsync();
		var outputDirectory = Path.Combine(fixture.RootDirectory, "edited");
		var sourceHashBefore = await Sha256Async(fixture.PatchPath);

		var capture = await CliTestConsole.RunCapturedAsync(
			"patch", "edit", fixture.PatchPath,
			"--remove-key", $"0x{FixturePatchB.TypeId:x16}:0x{FixturePatchB.FileId:x16}",
			"--output", outputDirectory, "--json");

		Assert.Equal(0, capture.ExitCode);
		var edited = Directory.EnumerateFiles(outputDirectory, "*.patch_*", SearchOption.TopDirectoryOnly)
			.Single(path => !path.EndsWith(".stream", StringComparison.OrdinalIgnoreCase)
				&& !path.EndsWith(".gpu_resources", StringComparison.OrdinalIgnoreCase));
		var entries = await new PatchTocScanner().ScanEntriesAsync(edited);
		Assert.Contains(entries, entry => entry.AssetKey == FixturePatchA);
		Assert.DoesNotContain(entries, entry => entry.AssetKey == FixturePatchB);
		Assert.Equal(sourceHashBefore, await Sha256Async(fixture.PatchPath));
	}

	[Fact]
	public async Task PatchEdit_RejectsUnknownKeyAndEmptySelection()
	{
		await using var fixture = await PatchPrimitiveFixture.CreateAsync();

		var emptyExit = await CliTestConsole.RunCapturedAsync(
			"patch", "edit", fixture.PatchPath,
			"--output", Path.Combine(fixture.RootDirectory, "out-empty"), "--json");
		Assert.Equal(2, emptyExit.ExitCode);

		var missingExit = await CliTestConsole.RunCapturedAsync(
			"patch", "edit", fixture.PatchPath,
			"--remove-key", "0xdeadbeefdeadbeef:0xdeadbeefdeadbeef",
			"--output", Path.Combine(fixture.RootDirectory, "out-missing"), "--json");
		Assert.Equal(2, missingExit.ExitCode);
		using var json = missingExit.ParseJson();
		Assert.Equal("PatchEditRemoveMissing", json.RootElement.GetProperty("error").GetProperty("code").GetString());
	}

	[Fact]
	public async Task PatchPrimitives_RejectMissingSourceAndExistingOutputWithoutOverwrite()
	{
		await using var fixture = await PatchPrimitiveFixture.CreateAsync();

		var missingExit = await CliTestConsole.RunCapturedAsync(
			"patch", "entries", "list", Path.Combine(fixture.RootDirectory, "absent.patch_0"), "--json");
		Assert.Equal(2, missingExit.ExitCode);
		using var missingJson = missingExit.ParseJson();
		Assert.Equal("PatchTocNotFound", missingJson.RootElement.GetProperty("error").GetProperty("code").GetString());

		// A second export into the same directory must not silently overwrite.
		var outputDirectory = Path.Combine(fixture.RootDirectory, "no-overwrite");
		Assert.Equal(0, (await CliTestConsole.RunCapturedAsync(
			"patch", "entry", "export", fixture.PatchPath,
			$"0x{FixturePatchA.TypeId:x16}", $"0x{FixturePatchA.FileId:x16}",
			"--output", outputDirectory, "--json")).ExitCode);
		var secondExit = await CliTestConsole.RunCapturedAsync(
			"patch", "entry", "export", fixture.PatchPath,
			$"0x{FixturePatchA.TypeId:x16}", $"0x{FixturePatchA.FileId:x16}",
			"--output", outputDirectory, "--json");
		Assert.Equal(2, secondExit.ExitCode);
	}

	[Fact]
	public async Task PatchPrimitives_RejectUnsupportedWriteParameters()
	{
		await using var fixture = await PatchPrimitiveFixture.CreateAsync();

		// Read-only commands must not accept --confirm / --plan.
		var exit = await CliTestConsole.RunCapturedAsync(
			"patch", "entries", "list", fixture.PatchPath, "--confirm", "--json");
		Assert.Equal(2, exit.ExitCode);
		using var json = exit.ParseJson();
		Assert.Equal("CommandOptionNotSupported", json.RootElement.GetProperty("error").GetProperty("code").GetString());
	}

	private static Task<string> Sha256Async(string path)
		=> Task.FromResult(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));

	internal static readonly AssetKey FixturePatchA = new(0xe0a48d0be9a7453f, 0x1111111111111111);
	internal static readonly AssetKey FixturePatchB = new(0x18dead01056b72e9, 0x2222222222222222);

	private sealed class PatchPrimitiveFixture : IAsyncDisposable
	{
		private PatchPrimitiveFixture(string rootDirectory, string patchPath, string secondPatchPath)
		{
			RootDirectory = rootDirectory;
			PatchPath = patchPath;
			SecondPatchPath = secondPatchPath;
		}

		public string RootDirectory { get; }
		public string PatchPath { get; }
		public string SecondPatchPath { get; }

		public static async ValueTask<PatchPrimitiveFixture> CreateAsync()
		{
			var root = Path.Combine(Path.GetTempPath(), "hd2-cli-primitive-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(root);
			var patchPath = Path.Combine(root, "fixture-a.patch_0");
			var secondPatchPath = Path.Combine(root, "fixture-b.patch_0");
			await WritePatchAsync(patchPath, [FixturePatchA, FixturePatchB]);
			await WritePatchAsync(secondPatchPath, [FixturePatchB]);
			return new PatchPrimitiveFixture(root, patchPath, secondPatchPath);
		}

		private static async ValueTask WritePatchAsync(string patchPath, IReadOnlyList<AssetKey> keys)
		{
			// Start from a valid boot-Lua Patch, then append our synthetic asset
			// entries through the production writer so the fixture has exactly
			// the layout the primitives are expected to handle.
			var templateDirectory = Path.Combine(Path.GetDirectoryName(patchPath)!, "lua-template");
			Directory.CreateDirectory(templateDirectory);
			var luaTemplatePath = Path.Combine(templateDirectory, "9ba626afa44a3aa3.patch_0");
			await WriteBootPatchAsync(luaTemplatePath);
			var writer = new PatchArchiveWriter();
			var additions = keys
				.Select(key => new PatchArchiveAdditionalEntry(key, BuildFakeTocData(key), [], BuildFakeGpuData(key)))
				.ToArray();
			var staging = Path.Combine(Path.GetDirectoryName(patchPath)!, "staging-" + Guid.NewGuid().ToString("N"));
			var result = await writer.WriteAsync(
				luaTemplatePath,
				staging,
				Array.Empty<PatchUnitMeshEditResult>(),
				additions,
				Array.Empty<PatchTocEntry>(),
				overwriteExisting: true,
				preserveOriginalStream: false,
				headerTemplateTocData: null);
			// The writer names the output after the source template; move it to
			// the exact fixture path so the command sees a stable file name.
			File.Move(result.TocFilePath, patchPath);
			if (File.Exists(result.GpuResourceFilePath)) File.Move(result.GpuResourceFilePath, patchPath + ".gpu_resources");
			if (File.Exists(result.StreamFilePath)) File.Move(result.StreamFilePath, patchPath + ".stream");
			Directory.Delete(staging, recursive: true);
			Directory.Delete(templateDirectory, recursive: true);
			await Task.CompletedTask;
		}

		private static async ValueTask WriteBootPatchAsync(string patchPath)
		{
			var header = new byte[0xc0];
			BinaryPrimitives.WriteUInt32LittleEndian(header, 0xF0000011);
			BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 1);
			BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), 1);
			BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(0x44), LuaAssetKeys.LuaTypeId);
			BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(0x4c), 1);
			BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(0x5c), LuaAssetKeys.BootFileId);
			BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(0x64), LuaAssetKeys.LuaTypeId);
			var entry = new PatchTocEntry(LuaAssetKeys.BootAssetKey, patchPath, Path.GetFileName(patchPath));
			var document = new LuaPatchDocument(
				patchPath,
				entry,
				new PatchEntryPayload(entry, [], [], []),
				header,
				[0x1b, 0x4c, 0x4a],
				[new LuaPatchBlock("fixture", "local fixture = true")]);
			var composer = new LuaPatchComposer();
			await composer.WriteAsync(patchPath, composer.Wrap(document));
		}

		private static byte[] BuildFakeTocData(AssetKey key)
		{
			var data = new byte[32];
			BinaryPrimitives.WriteUInt64LittleEndian(data, key.FileId);
			BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(8), key.TypeId);
			return data;
		}

		private static byte[] BuildFakeGpuData(AssetKey key)
			=> Enumerable.Range(0, 64).Select(index => (byte)(key.FileId + (ulong)index)).ToArray();

		public ValueTask DisposeAsync()
		{
			try { Directory.Delete(RootDirectory, recursive: true); }
			catch { }
			return ValueTask.CompletedTask;
		}
	}
}
