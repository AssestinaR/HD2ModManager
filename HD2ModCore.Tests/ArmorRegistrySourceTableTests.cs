using System.Text.Json;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using Xunit;

namespace HD2ModCore.Tests;

/// <summary>
/// 层 2 的"来源表"（<c>document.sourceItems</c>）：游戏条目被指定为视觉来源之后，
/// 它的存在**不再依赖有没有映射目标**。
/// <para>
/// 为什么值得专门守：来源以前只是映射行上的一个字段，于是"没有目标的来源"无处可存——
/// 把一个护甲设为来源、还没配目标，保存后它就不存在了；删掉某个来源的最后一个目标，来源也跟着蒸发。
/// 自定义素体没有这个问题（它们在 models 里有独立记录），这张表就是给游戏条目的同一份待遇。
/// </para>
/// </summary>
public sealed class ArmorRegistrySourceTableTests : IDisposable
{
	private readonly string _root;
	private readonly RuntimeArmorRegistryService _registry;

	public ArmorRegistrySourceTableTests()
	{
		_root = Path.Combine(Path.GetTempPath(), "hd2-source-table-" + Guid.NewGuid().ToString("N"));
		var appRoot = Path.Combine(_root, "app");
		var libraryRoot = Path.Combine(_root, "library");
		Directory.CreateDirectory(appRoot);
		Directory.CreateDirectory(libraryRoot);
		// 注册表根必须指到临时目录：默认值会去读用户真实的工作区（见 CustomModelSectionalCompactionTests 的注释）。
		_registry = new RuntimeArmorRegistryService(new StoragePaths(appRoot, libraryRoot), Path.Combine(_root, "registry"));
	}

	public void Dispose()
	{
		try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
		catch (IOException) { }
		catch (UnauthorizedAccessException) { }
	}

	[Fact]
	public async Task SourceItems_RoundTrip_WithoutAnyMapping()
	{
		await _registry.SaveWorkspaceAsync(new RuntimeArmorRegistryDocument(
			1,
			true,
			Array.Empty<RuntimeArmorRegistryMapping>(),
			SourceItems: [111u, 222u]));

		var snapshot = await _registry.ReadSnapshotAsync(default);

		Assert.Equal([111u, 222u], snapshot.Document.SourceItemIds.OrderBy(id => id).ToArray());
		Assert.Empty(snapshot.Document.Mappings);
	}

	[Fact]
	public async Task LegacyWorkspace_BackfillsSourceItemsFromMappings()
	{
		// 旧文件没有 sourceItems 字段：读的时候用映射行反推一次。
		// 素体映射的 sourceItemId 为空，天然被排除 —— 它本来就在 models 里。
		var json = """
		{
		  "schema": 1,
		  "revision": 3,
		  "enabled": true,
		  "mappings": [
		    { "targetItemId": 7001, "sourceItemId": 5001 },
		    { "targetItemId": 7002, "sourceItemId": 5001 },
		    { "targetItemId": 7003, "modelKey": "custom-a" }
		  ],
		  "models": [
		    { "key": "custom-a", "pieces": [], "name": "我的素体" }
		  ],
		  "compositionMode": "full"
		}
		""";
		Directory.CreateDirectory(Path.GetDirectoryName(_registry.WorkspacePath)!);
		await File.WriteAllTextAsync(_registry.WorkspacePath, json);

		var snapshot = await _registry.ReadSnapshotAsync(default);

		Assert.Equal([5001u], snapshot.Document.SourceItemIds.ToArray());
	}

	[Fact]
	public async Task EmptySourceTable_IsOmittedFromTheFile()
	{
		// 空表写成 null（WhenWritingNull 整个省掉属性）：文件里"没有来源表"和"来源表是空的"长得一样，
		// 少一个分支比多一个分支安全。这时没有任何映射可反推，读回来仍然是空表。
		await _registry.SaveWorkspaceAsync(new RuntimeArmorRegistryDocument(
			1,
			true,
			Array.Empty<RuntimeArmorRegistryMapping>()));

		var fileText = await File.ReadAllTextAsync(_registry.WorkspacePath);
		Assert.DoesNotContain("sourceItems", fileText, StringComparison.OrdinalIgnoreCase);

		var snapshot = await _registry.ReadSnapshotAsync(default);
		Assert.Empty(snapshot.Document.SourceItemIds);
	}

	[Fact]
	public async Task SourceTable_SurvivesModelPreservingSave()
	{
		// 素体对账 / 拆件这两条路径会整条重建文档；漏带来源表就会把用户设的游戏来源全抹掉。
		await _registry.SaveWorkspaceAsync(new RuntimeArmorRegistryDocument(
			1,
			true,
			[new RuntimeArmorRegistryMapping(7001, 5001)],
			[new RuntimeArmorRegistryModel("custom-a", null, [new RuntimeArmorRegistryModelPiece(3, "0000000000000001", 2, 0)], "我的素体")],
			SourceItems: [5001u, 5002u]));

		var merged = await _registry.SaveWorkspacePreservingExternalModelsAsync(
			new RuntimeArmorRegistryDocument(
				1,
				true,
				[new RuntimeArmorRegistryMapping(7001, 5001)],
				Array.Empty<RuntimeArmorRegistryModel>(),
				SourceItems: [5001u, 5002u]),
			new HashSet<string>(StringComparer.Ordinal) { "custom-a" });

		Assert.Equal([5001u, 5002u], merged.SourceItemIds.OrderBy(id => id).ToArray());

		var snapshot = await _registry.ReadSnapshotAsync(default);
		Assert.Equal([5001u, 5002u], snapshot.Document.SourceItemIds.OrderBy(id => id).ToArray());
	}
}
