using System.Text.Json;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using Xunit;

namespace HD2ModCore.Tests;

/// <summary>
/// 拆件后剪掉悬空组件（<see cref="CustomModelSectionalCompaction"/>）。
/// <para>
/// 拆件会把同一 (槽, 身形) 的多个来源 unit 合并进一个宿主，其余 donor 从 patch 里去掉。
/// 而自定义素体的定义是按 (body, slot, pieceType) → unit 记的，所以指向 donor 的条目会悬空。
/// 这里用合成数据守住"两处一起改"这个口径 —— 只改产物文件，界面照旧显示死组件；
/// 只改工作区，下次从产物重读又长回来。
/// </para>
/// </summary>
public sealed class CustomModelSectionalCompactionTests : IDisposable
{
	private readonly string _root;
	private readonly StoragePaths _paths;
	private readonly RuntimeArmorRegistryService _registry;
	private readonly string _modDirectory;

	public CustomModelSectionalCompactionTests()
	{
		_root = Path.Combine(Path.GetTempPath(), "hd2-compaction-" + Guid.NewGuid().ToString("N"));
		var appRoot = Path.Combine(_root, "app");
		var libraryRoot = Path.Combine(_root, "library");
		Directory.CreateDirectory(appRoot);
		Directory.CreateDirectory(libraryRoot);
		_paths = new StoragePaths(appRoot, libraryRoot);
		// **必须把注册表根也指到临时目录。** 默认值指向 LocalApplicationData 下的真实注册表：
		// 工作区文件不存在时 `ReadSnapshotAsync` 会回退去读那里的协议文件，于是把用户真实的
		// 素体列表带进这份"空工作区"，断言就会看到一堆 custom-*（而且真的被写回临时工作区）。
		_registry = new RuntimeArmorRegistryService(_paths, Path.Combine(_root, "registry"));
		_modDirectory = Path.Combine(libraryRoot, "TestMod");
		Directory.CreateDirectory(_modDirectory);
	}

	public void Dispose()
	{
		try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
		catch (IOException) { }
		catch (UnauthorizedAccessException) { }
	}

	[Fact]
	public async Task NoDefinitionFile_IsNoOp()
	{
		var result = await CustomModelSectionalCompaction.PruneMergedPiecesAsync(
			_registry, _modDirectory, ["aaaaaaaaaaaaaaaa"]);

		Assert.False(result.Changed);
	}

	[Fact]
	public async Task AllPiecesSurvive_IsNoOp()
	{
		await WriteDefinitionAsync("custom-a", [("aaaaaaaaaaaaaaaa", 0u), ("bbbbbbbbbbbbbbbb", 1u)]);

		var result = await CustomModelSectionalCompaction.PruneMergedPiecesAsync(
			_registry, _modDirectory, ["aaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbb"]);

		Assert.False(result.Changed);
		Assert.Equal(2, (await ReadDefinitionAsync()).Pieces!.Count);
	}

	[Fact]
	public async Task MergedDonorPieces_AreDroppedFromTheDefinitionFile()
	{
		// 同一 (body=0, slot=2) 的 pieceType 0/1/2 = 三个 unit，拆件会把后两个并进第一个。
		await WriteDefinitionAsync("custom-a",
			[("aaaaaaaaaaaaaaaa", 0u), ("bbbbbbbbbbbbbbbb", 1u), ("cccccccccccccccc", 2u)]);

		var result = await CustomModelSectionalCompaction.PruneMergedPiecesAsync(
			_registry, _modDirectory, ["aaaaaaaaaaaaaaaa"]);

		Assert.True(result.Changed);
		Assert.Equal(2, result.RemovedPieceCount);
		Assert.Equal(1, result.RemainingPieceCount);
		Assert.Equal(["bbbbbbbbbbbbbbbb", "cccccccccccccccc"], result.RemovedUnits);

		var definition = await ReadDefinitionAsync();
		var piece = Assert.Single(definition.Pieces!);
		Assert.Equal("aaaaaaaaaaaaaaaa", piece.Unit);
	}

	[Fact]
	public async Task MergedDonorPieces_AreAlsoDroppedFromTheRegistryWorkspace()
	{
		// 只改产物文件是不够的：界面读的是工作区里的权威定义。
		await WriteDefinitionAsync("custom-a",
			[("aaaaaaaaaaaaaaaa", 0u), ("bbbbbbbbbbbbbbbb", 1u)]);
		await SeedWorkspaceAsync("custom-a",
			[("aaaaaaaaaaaaaaaa", 0u), ("bbbbbbbbbbbbbbbb", 1u)]);

		await CustomModelSectionalCompaction.PruneMergedPiecesAsync(
			_registry, _modDirectory, ["aaaaaaaaaaaaaaaa"]);

		var models = await _registry.ReadWorkspaceModelsAsync();
		var model = Assert.Single(models);
		var piece = Assert.Single(model.Pieces!);
		Assert.Equal("aaaaaaaaaaaaaaaa", piece.Unit);
	}

	[Fact]
	public async Task UnitIdsCompareCaseInsensitively()
	{
		await WriteDefinitionAsync("custom-a", [("AABBCCDDEEFF0011", 0u), ("1122334455667788", 1u)]);

		var result = await CustomModelSectionalCompaction.PruneMergedPiecesAsync(
			_registry, _modDirectory, ["aabbccddeeff0011"]);

		Assert.True(result.Changed);
		Assert.Equal(1, result.RemainingPieceCount);
	}

	[Fact]
	public async Task ModelMissingFromWorkspace_StillPrunesTheFile()
	{
		// 转换后还没登记过（或已被别人删掉）：产物文件照剪，工作区不用动。
		await WriteDefinitionAsync("custom-a", [("aaaaaaaaaaaaaaaa", 0u), ("bbbbbbbbbbbbbbbb", 1u)]);

		var result = await CustomModelSectionalCompaction.PruneMergedPiecesAsync(
			_registry, _modDirectory, ["aaaaaaaaaaaaaaaa"]);

		Assert.True(result.Changed);
		Assert.Empty(await _registry.ReadWorkspaceModelsAsync());
	}

	[Fact]
	public async Task WorkspaceWithoutDefinitionFile_IsNoOp()
	{
		// 没有 custom-model.json 就不是自定义素体，不该凭空去改工作区。
		await SeedWorkspaceAsync("custom-a", [("aaaaaaaaaaaaaaaa", 0u)]);

		var result = await CustomModelSectionalCompaction.PruneMergedPiecesAsync(
			_registry, _modDirectory, []);

		Assert.False(result.Changed);
		Assert.Single((await _registry.ReadWorkspaceModelsAsync())[0].Pieces!);
	}

	private async Task WriteDefinitionAsync(string key, IReadOnlyList<(string Unit, uint PieceType)> pieces)
	{
		var model = BuildModel(key, pieces);
		await File.WriteAllTextAsync(
			Path.Combine(_modDirectory, VpkCustomModelFile.FileName),
			JsonSerializer.Serialize(model, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
	}

	private async Task<RuntimeArmorRegistryModel> ReadDefinitionAsync()
	{
		var text = await File.ReadAllTextAsync(Path.Combine(_modDirectory, VpkCustomModelFile.FileName));
		return JsonSerializer.Deserialize<RuntimeArmorRegistryModel>(
			text, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
	}

	private async Task SeedWorkspaceAsync(string key, IReadOnlyList<(string Unit, uint PieceType)> pieces)
	{
		var document = new RuntimeArmorRegistryDocument(
			Revision: 1,
			Enabled: true,
			Mappings: [],
			Models: [BuildModel(key, pieces)]);
		await _registry.SaveWorkspaceAsync(document);
	}

	private static RuntimeArmorRegistryModel BuildModel(string key, IReadOnlyList<(string Unit, uint PieceType)> pieces)
		=> new(
			key,
			SourceItemId: null,
			Pieces: [.. pieces.Select(item => new RuntimeArmorRegistryModelPiece(
				Body: 0,
				Unit: item.Unit,
				Slot: 2,
				PieceType: item.PieceType))],
			Name: "Test");
}
