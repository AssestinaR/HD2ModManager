using System.Text.Json;
using HD2ModCli;
using Xunit;

namespace HD2ModCli.Tests;

/// <summary>
/// 覆盖 gamedata extract 直读提取路径：它必须在不依赖 SQLite 资产索引、也不需要
/// --manager-root 的前提下枚举并提取游戏包中的资产，同时守住输出目录边界。
/// Covers the direct gamedata extract path: it must enumerate and extract assets from the
/// game packages without a SQLite index or --manager-root, while guarding output boundaries.
/// </summary>
[Collection("cli-console")]
public sealed class GameDataExtractionCliTests
{
	private const ulong BonesTypeId = 0x18dead01056b72e9;

	private static readonly byte[] TocPayload = Enumerable.Range(0, 64).Select(index => (byte)index).ToArray();
	private static readonly byte[] StreamPayload = Enumerable.Range(0, 200).Select(index => (byte)(index * 3)).ToArray();
	private static readonly byte[] GpuPayload = Enumerable.Range(0, 32).Select(index => (byte)(255 - index)).ToArray();

	[Fact]
	public async Task ExtractList_DirectlyReadsLegacyArchiveAndResolvesTypeName()
	{
		await using var fixture = await Fixture.CreateAsync();

		var capture = await CliTestConsole.RunCapturedAsync(
			"gamedata", "extract", "list", "bones",
			"--game-data", fixture.GameDataDirectory,
			"--json");

		Assert.True(capture.ExitCode == 0, capture.StandardError + Environment.NewLine + capture.StandardOutput);
		using var response = capture.ParseJson();
		Assert.True(response.RootElement.GetProperty("ok").GetBoolean());
		var extraction = response.RootElement.GetProperty("extraction");
		Assert.Equal(1, extraction.GetProperty("totalCount").GetInt32());
		var item = Assert.Single(extraction.GetProperty("items").EnumerateArray());
		Assert.Equal("bones", item.GetProperty("typeName").GetString());
		Assert.Equal("0x18dead01056b72e9", item.GetProperty("typeId").GetString());
		Assert.Equal("0x00000000000000c8", item.GetProperty("fileId").GetString());
		Assert.Equal(TocPayload.Length + StreamPayload.Length + GpuPayload.Length, item.GetProperty("totalSize").GetInt64());
		Assert.Equal(fixture.ArchiveId, item.GetProperty("archiveId").GetString());
	}

	[Fact]
	public async Task ExtractList_FiltersOutNonMatchingTypes()
	{
		await using var fixture = await Fixture.CreateAsync();

		var capture = await CliTestConsole.RunCapturedAsync(
			"gamedata", "extract", "list", "unit",
			"--game-data", fixture.GameDataDirectory,
			"--json");

		Assert.Equal(0, capture.ExitCode);
		using var response = capture.ParseJson();
		Assert.Equal(0, response.RootElement.GetProperty("extraction").GetProperty("totalCount").GetInt32());
	}

	[Fact]
	public async Task ExtractAsset_WritesTocStreamAndGpuParts()
	{
		await using var fixture = await Fixture.CreateAsync();
		var output = Path.Combine(fixture.RootDirectory, "extracted");

		var capture = await CliTestConsole.RunCapturedAsync(
			"gamedata", "extract", "asset", "0x18dead01056b72e9", "0xc8",
			"--game-data", fixture.GameDataDirectory,
			"--output", output,
			"--json");

		Assert.True(capture.ExitCode == 0, capture.StandardError + Environment.NewLine + capture.StandardOutput);
		using var response = capture.ParseJson();
		var files = response.RootElement.GetProperty("asset").GetProperty("files").EnumerateArray().ToArray();
		Assert.Equal(3, files.Length);
		Assert.Equal("0x00000000000000c8", response.RootElement.GetProperty("asset").GetProperty("asset").GetProperty("fileId").GetString());
		Assert.Equal(TocPayload, await File.ReadAllBytesAsync(Path.Combine(output, "bones_0x00000000000000c8.toc")));
		Assert.Equal(StreamPayload, await File.ReadAllBytesAsync(Path.Combine(output, "bones_0x00000000000000c8.stream")));
		Assert.Equal(GpuPayload, await File.ReadAllBytesAsync(Path.Combine(output, "bones_0x00000000000000c8.gpu_resources")));
	}

	[Fact]
	public async Task ExtractArchive_WritesTypeFoldersAndManifest()
	{
		await using var fixture = await Fixture.CreateAsync();
		var output = Path.Combine(fixture.RootDirectory, "archive-out");

		var capture = await CliTestConsole.RunCapturedAsync(
			"gamedata", "extract", "archive", fixture.ArchiveId,
			"--game-data", fixture.GameDataDirectory,
			"--output", output,
			"--json");

		Assert.True(capture.ExitCode == 0, capture.StandardError + Environment.NewLine + capture.StandardOutput);
		using var response = capture.ParseJson();
		var archive = response.RootElement.GetProperty("archive");
		Assert.Equal(1, archive.GetProperty("assetCount").GetInt32());
		Assert.Equal(3, archive.GetProperty("fileCount").GetInt32());
		Assert.True(File.Exists(Path.Combine(output, "bones", "bones_0x00000000000000c8.toc")));

		using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output, "extract-manifest.json")));
		Assert.Equal(fixture.ArchiveId, manifest.RootElement.GetProperty("archiveId").GetString());
		Assert.Equal(1, manifest.RootElement.GetProperty("assetCount").GetInt32());
	}

	[Fact]
	public async Task Extract_RejectsOutputInsideGameDataAndUnknownTypes()
	{
		await using var fixture = await Fixture.CreateAsync();

		var inside = await CliTestConsole.RunCapturedAsync(
			"gamedata", "extract", "asset", "0x18dead01056b72e9", "0xc8",
			"--game-data", fixture.GameDataDirectory,
			"--output", Path.Combine(fixture.GameDataDirectory, "out"),
			"--json");
		Assert.Equal(2, inside.ExitCode);
		using (var response = inside.ParseJson())
		{
			var diagnostic = Assert.Single(response.RootElement.GetProperty("asset").GetProperty("diagnostics").EnumerateArray());
			Assert.Equal("OutputInsideGameData", diagnostic.GetProperty("code").GetString());
			Assert.True(diagnostic.GetProperty("blocking").GetBoolean());
		}

		var unknown = await CliTestConsole.RunCapturedAsync(
			"gamedata", "extract", "list", "not-a-type",
			"--game-data", fixture.GameDataDirectory,
			"--json");
		Assert.Equal(2, unknown.ExitCode);
		using (var response = unknown.ParseJson())
		{
			Assert.Equal("GameDataAssetTypeUnknown",
				Assert.Single(response.RootElement.GetProperty("extraction").GetProperty("diagnostics").EnumerateArray()).GetProperty("code").GetString());
		}
	}

	[Fact]
	public async Task Extract_EnforcesOptionContractAndOverwriteGuard()
	{
		await using var fixture = await Fixture.CreateAsync();

		var withManagerRoot = await CliTestConsole.RunCapturedAsync(
			"gamedata", "extract", "list", "bones",
			"--game-data", fixture.GameDataDirectory,
			"--manager-root", fixture.RootDirectory,
			"--json");
		Assert.Equal(2, withManagerRoot.ExitCode);
		using (var response = withManagerRoot.ParseJson())
			Assert.Equal("CommandOptionNotSupported", response.RootElement.GetProperty("error").GetProperty("code").GetString());

		var output = Path.Combine(fixture.RootDirectory, "once");
		var first = await CliTestConsole.RunCapturedAsync(
			"gamedata", "extract", "asset", "0x18dead01056b72e9", "0xc8",
			"--game-data", fixture.GameDataDirectory,
			"--output", output,
			"--json");
		Assert.True(first.ExitCode == 0, first.StandardError + Environment.NewLine + first.StandardOutput);

		var second = await CliTestConsole.RunCapturedAsync(
			"gamedata", "extract", "asset", "0x18dead01056b72e9", "0xc8",
			"--game-data", fixture.GameDataDirectory,
			"--output", output,
			"--json");
		Assert.Equal(2, second.ExitCode);
		using (var response = second.ParseJson())
		{
			Assert.Equal("OutputDirectoryNotEmpty",
				Assert.Single(response.RootElement.GetProperty("asset").GetProperty("diagnostics").EnumerateArray()).GetProperty("code").GetString());
		}
	}

	private sealed class Fixture : IAsyncDisposable
	{
		private Fixture(string rootDirectory, string gameDataDirectory, string archiveId)
		{
			RootDirectory = rootDirectory;
			GameDataDirectory = gameDataDirectory;
			ArchiveId = archiveId;
		}

		public string RootDirectory { get; }
		public string GameDataDirectory { get; }
		public string ArchiveId { get; }

		public static async ValueTask<Fixture> CreateAsync()
		{
			var root = Path.Combine(Path.GetTempPath(), "hd2-cli-extract-" + Guid.NewGuid().ToString("N"));
			var gameData = Path.Combine(root, "Helldivers 2", "data");
			Directory.CreateDirectory(gameData);

			const string archiveId = "aaaaaaaaaaaaaaaa";
			const ulong fileId = 0xc8;
			const int tocDataOffset = 60 + 80;
			await File.WriteAllBytesAsync(Path.Combine(gameData, archiveId), BuildArchive(fileId, tocDataOffset));
			await File.WriteAllBytesAsync(Path.Combine(gameData, archiveId + ".stream"), StreamPayload);
			await File.WriteAllBytesAsync(Path.Combine(gameData, archiveId + ".gpu_resources"), GpuPayload);
			return new Fixture(root, gameData, archiveId);
		}

		public ValueTask DisposeAsync()
		{
			try { Directory.Delete(RootDirectory, recursive: true); }
			catch { }
			return ValueTask.CompletedTask;
		}

		private static byte[] BuildArchive(ulong fileId, int tocDataOffset)
		{
			const uint magic = 4026531857;
			const int entriesOffset = 60;
			var table = new byte[entriesOffset + 80];
			BitConverter.GetBytes(magic).CopyTo(table, 0);
			BitConverter.GetBytes((uint)0).CopyTo(table, 4);
			BitConverter.GetBytes((uint)1).CopyTo(table, 8);
			BitConverter.GetBytes(fileId).CopyTo(table, entriesOffset);
			BitConverter.GetBytes(BonesTypeId).CopyTo(table, entriesOffset + 8);
			BitConverter.GetBytes((ulong)tocDataOffset).CopyTo(table, entriesOffset + 16);
			BitConverter.GetBytes((ulong)0).CopyTo(table, entriesOffset + 24);
			BitConverter.GetBytes((ulong)0).CopyTo(table, entriesOffset + 32);
			BitConverter.GetBytes((uint)TocPayload.Length).CopyTo(table, entriesOffset + 56);
			BitConverter.GetBytes((uint)StreamPayload.Length).CopyTo(table, entriesOffset + 60);
			BitConverter.GetBytes((uint)GpuPayload.Length).CopyTo(table, entriesOffset + 64);
			BitConverter.GetBytes((uint)0).CopyTo(table, entriesOffset + 76);
			return table.Concat(TocPayload).ToArray();
		}
	}
}
