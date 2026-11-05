using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

/// <summary>
/// 素体 ↔ 映射对账：<b>素体以 Mod 文件夹为权威，只解散"指向不存在素体的映射"</b>。
/// <para>
/// 这组用例替代了旧的 <c>CustomModelOwnershipTests</c>（那套测的是"删素体"）。
/// 旧行为有两个会丢用户数据的结构性缺陷，这里逐条钉住新口径：
/// </para>
/// <list type="number">
/// <item><b>首次只标记、不删</b>——用户可能只是库路径临时指错；</item>
/// <item><b>读不出来就什么都不删</b>（fail-closed），而不是"读不到 = 没有归属"；</item>
/// <item><b>先补文件、再对账</b>——历史素体只要 Mod 目录还在，就先补出标记（并沿用原 key），
/// 而不是被解散。</item>
/// </list>
/// </summary>
public sealed class CustomModelReconcilerTests : IDisposable
{
	private readonly string root = Path.Combine(
		Path.GetTempPath(), "hd2-custom-model-reconcile-" + Guid.NewGuid().ToString("N"));

	private string ModsDirectory => Path.Combine(root, "mods");

	public CustomModelReconcilerTests() => Directory.CreateDirectory(ModsDirectory);

	public void Dispose()
	{
		try
		{
			if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
		}
		catch (IOException)
		{
		}
	}

	private RuntimeArmorRegistryService CreateRegistry()
	{
		Directory.CreateDirectory(Path.Combine(root, "app"));
		var registryRoot = Path.Combine(root, "registry");
		Directory.CreateDirectory(registryRoot);
		return new RuntimeArmorRegistryService(
			new StoragePaths(Path.Combine(root, "app"), ModsDirectory), registryRoot);
	}

	private CustomModelReconciler CreateReconciler(RuntimeArmorRegistryService registry)
		=> new(registry, ModsDirectory);

	/// <summary>造一个持有素体的 Mod（形状与真实产物一致：文件在 <c>vpk2patch/</c> 下）。</summary>
	private void OwnModel(string directoryName, string key, string name, string unit = "0123456789abcdef")
	{
		var directory = Path.Combine(ModsDirectory, directoryName, ModNodePayloadLayout.VpkPatchDirectoryName);
		Directory.CreateDirectory(directory);
		File.WriteAllText(
			Path.Combine(directory, VpkCustomModelFile.FileName),
			$$"""
			{ "key": "{{key}}", "name": "{{name}}",
			  "pieces": [ { "body": 3, "unit": "{{unit}}", "slot": 2, "pieceType": 0 } ] }
			""");
	}

	private static RuntimeArmorRegistryModel Model(string key, string name, string unit = "0123456789abcdef")
		=> new(key, null, [new RuntimeArmorRegistryModelPiece(3, unit, 2, 0)], name);

	/// <summary>造一个"有 Unit 产物、但没有 custom-model.json"的 Mod（模拟文件丢失）。</summary>
	private void ModWithoutDefinition(string directoryName, params string[] units)
	{
		var directory = Path.Combine(ModsDirectory, directoryName, ModNodePayloadLayout.VpkPatchDirectoryName);
		Directory.CreateDirectory(directory);
		var entries = units.Select((unit, index) =>
			$$"""
			{ "fileId": "{{unit}}", "slot": {{index + 2}}, "pieceType": 0, "bodyType": "Any" }
			""");
		File.WriteAllText(
			Path.Combine(directory, "sections.json"),
			$$"""
			{ "schema": "hd2mod.section-map/3", "units": [ {{string.Join(",", entries)}} ] }
			""");
	}

	[Fact]
	public async Task FirstRunOnlyMarksAndKeepsEverything()
	{
		var registry = CreateRegistry();
		await registry.SaveWorkspaceAsync(new RuntimeArmorRegistryDocument(
			3, true,
			[
				new RuntimeArmorRegistryMapping(29533812, null, "custom-owned"),
				new RuntimeArmorRegistryMapping(34232232, null, "custom-gone"),
			],
			[Model("custom-owned", "MourningVeil"), Model("custom-gone", "已删掉的素体", "fedcba9876543210")]));
		OwnModel("MourningVeil_Bill", "custom-owned", "MourningVeil");

		var report = await CreateReconciler(registry).ReconcileAsync();

		Assert.True(report.MarkedOnly);
		Assert.Equal(1, report.OrphanModels);
		Assert.Equal(0, report.DissolvedMappings);
		// 关键：一条映射都没删。
		var snapshot = await registry.ReadSnapshotAsync();
		Assert.Equal(2, snapshot.Document.Mappings.Count);
	}

	[Fact]
	public async Task SecondRunDissolvesOnlyTheMappingsPointingAtMissingModels()
	{
		var registry = CreateRegistry();
		await registry.SaveWorkspaceAsync(new RuntimeArmorRegistryDocument(
			3, true,
			[
				new RuntimeArmorRegistryMapping(29533812, null, "custom-owned"),
				new RuntimeArmorRegistryMapping(34232232, null, "custom-gone"),
				new RuntimeArmorRegistryMapping(88270577, 55555),
			],
			[Model("custom-owned", "MourningVeil"), Model("custom-gone", "已删掉的素体", "fedcba9876543210")]));
		OwnModel("MourningVeil_Bill", "custom-owned", "MourningVeil");

		var reconciler = CreateReconciler(registry);
		await reconciler.ReconcileAsync();          // 第一次：只标记
		var report = await reconciler.ReconcileAsync();   // 第二次：真正解散

		Assert.False(report.MarkedOnly);
		Assert.Equal(1, report.DissolvedMappings);
		var snapshot = await registry.ReadSnapshotAsync();
		Assert.Equal(2, snapshot.Document.Mappings.Count);
		Assert.DoesNotContain(snapshot.Document.Mappings, mapping => mapping.ModelKey == "custom-gone");
		// 素体列表里也不再有它（素体跟着 Mod 走）。
		Assert.DoesNotContain(snapshot.Document.ModelDefinitions, model => model.Key == "custom-gone");
		Assert.Contains(snapshot.Document.ModelDefinitions, model => model.Key == "custom-owned");
	}

	[Fact]
	public async Task UnreadableDefinitionBlocksEverything()
	{
		var registry = CreateRegistry();
		await registry.SaveWorkspaceAsync(new RuntimeArmorRegistryDocument(
			3, true,
			[new RuntimeArmorRegistryMapping(34232232, null, "custom-gone")],
			[Model("custom-gone", "看起来没有主", "fedcba9876543210")]));
		OwnModel("Good_Bill", "custom-other", "另一个");
		// 一个坏文件：读不出来 ≠ 没有归属，本轮必须什么都不做。
		var broken = Path.Combine(ModsDirectory, "Broken_Bill", ModNodePayloadLayout.VpkPatchDirectoryName);
		Directory.CreateDirectory(broken);
		File.WriteAllText(Path.Combine(broken, VpkCustomModelFile.FileName), "{ not json");

		var reconciler = CreateReconciler(registry);
		var report = await reconciler.ReconcileAsync();

		Assert.NotNull(report.BlockedReason);
		Assert.Equal(0, report.DissolvedMappings);
		// 连"标记"都不写：不可信的一轮不该推进任何状态。
		Assert.False(File.Exists(Path.Combine(registry.WorkspaceRoot, CustomModelReconciler.StateFileName)));
		var snapshot = await registry.ReadSnapshotAsync();
		Assert.Single(snapshot.Document.Mappings);
	}

	[Fact]
	public async Task MissingLibraryBlocksEverything()
	{
		var registry = CreateRegistry();
		await registry.SaveWorkspaceAsync(new RuntimeArmorRegistryDocument(
			1, true, [new RuntimeArmorRegistryMapping(34232232, null, "custom-gone")],
			[Model("custom-gone", "素体")]));

		var reconciler = new CustomModelReconciler(registry, Path.Combine(root, "not-mounted"));
		var report = await reconciler.ReconcileAsync();

		Assert.NotNull(report.BlockedReason);
		Assert.Equal(0, report.DissolvedMappings);
	}

	[Fact]
	public async Task EverythingOwnedChangesNothing()
	{
		var registry = CreateRegistry();
		await registry.SaveWorkspaceAsync(new RuntimeArmorRegistryDocument(
			2, true, [new RuntimeArmorRegistryMapping(29533812, null, "custom-owned")],
			[Model("custom-owned", "MourningVeil")]));
		OwnModel("MourningVeil_Bill", "custom-owned", "MourningVeil");

		var report = await CreateReconciler(registry).ReconcileAsync();

		Assert.True(report.IsEmpty);
		Assert.Null(report.Describe());
	}

	/// <summary>
	/// 文件丢了但 Mod 还在：先**补出**标记，而且必须沿用原来的 key ——
	/// 否则新 key 与旧映射对不上，"恢复"等于没恢复。
	/// </summary>
	[Fact]
	public async Task MissingDefinitionIsRecoveredWithTheOriginalKey()
	{
		var registry = CreateRegistry();
		await registry.SaveWorkspaceAsync(new RuntimeArmorRegistryDocument(
			5, true,
			[new RuntimeArmorRegistryMapping(29533812, null, "custom-lost")],
			[Model("custom-lost", "二阶堂希罗", "77d99102e9c20001")]));
		ModWithoutDefinition("二阶堂希罗_bill", "77d99102e9c20001", "77d99102e9c20002");

		var report = await CreateReconciler(registry).ReconcileAsync();

		Assert.NotNull(report.Recovery);
		Assert.Single(report.Recovery!.Recovered);
		Assert.Equal(1, report.Recovery.ReusedKeys);
		// 素体没被判成孤儿，映射也还在。
		Assert.Equal(0, report.OrphanModels);
		var snapshot = await registry.ReadSnapshotAsync();
		Assert.Single(snapshot.Document.Mappings);

		// 文件真的落盘了，且 key / 名字沿用原值。
		var written = Path.Combine(
			ModsDirectory, "二阶堂希罗_bill", ModNodePayloadLayout.VpkPatchDirectoryName, VpkCustomModelFile.FileName);
		Assert.True(File.Exists(written));
		var text = File.ReadAllText(written);
		Assert.Contains("custom-lost", text);
		Assert.Contains("二阶堂希罗", text);
	}

	[Fact]
	public async Task MaterialOnlyModsGetNoEmptyModel()
	{
		var registry = CreateRegistry();
		// 只有材质、没有 Unit 的产物：不该被造出一具空素体。
		var directory = Path.Combine(ModsDirectory, "材质包", ModNodePayloadLayout.VpkPatchDirectoryName);
		Directory.CreateDirectory(directory);
		File.WriteAllText(Path.Combine(directory, "sections.json"), """{ "units": [] }""");

		var report = await CreateReconciler(registry).ReconcileAsync();

		Assert.Null(report.Recovery);
		Assert.False(File.Exists(Path.Combine(directory, VpkCustomModelFile.FileName)));
	}
}
