using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

/// <summary>
/// 自定义素体与 Mod 的<b>生命周期绑定</b>：没有 Mod 归属的素体必须被清掉。
/// <para>
/// 这条规则替代了"用户手工新建素体"那条路：素体是某个 Mod 的转换产物，
/// 那套 Unit 只有跟着那个 Mod 部署才有意义。测的是"谁有资格留在注册表里"，
/// 所以判据只能是 Mod 目录里那份 <c>custom-model.json</c>。
/// </para>
/// </summary>
public sealed class CustomModelOwnershipTests : IDisposable
{
	private readonly string root = Path.Combine(
		Path.GetTempPath(), "hd2-custom-model-owner-" + Guid.NewGuid().ToString("N"));

	private string ModsDirectory => Path.Combine(root, "mods");

	public void Dispose()
	{
		try
		{
			if (Directory.Exists(root))
				Directory.Delete(root, recursive: true);
		}
		catch (IOException)
		{
			// 清理失败不该让测试结果变红。
		}
	}

	private RuntimeArmorRegistryService CreateRegistry()
	{
		Directory.CreateDirectory(Path.Combine(root, "app"));
		Directory.CreateDirectory(ModsDirectory);
		// **协议目录也要隔离。** 不传它就落到本机真实的那份（LocalAppData 下的运行时协议），
		// 于是"工作区里什么都没有"时会读到开发者自己机器上已应用的那份映射 ——
		// 测试会莫名其妙地看到别人的数据。
		var registryRoot = Path.Combine(root, "registry");
		Directory.CreateDirectory(registryRoot);
		return new RuntimeArmorRegistryService(
			new StoragePaths(Path.Combine(root, "app"), ModsDirectory), registryRoot);
	}

	private CustomModelOwnershipService CreateService(RuntimeArmorRegistryService registry)
		=> new(registry, ModsDirectory);

	/// <summary>在库里造一个"持有素体"的 Mod，形状与真实产物一致（文件在 <c>vpk2patch/</c> 下）。</summary>
	private string OwnModel(string directoryName, string key, string? name = null)
	{
		var directory = Path.Combine(ModsDirectory, directoryName, "vpk2patch");
		Directory.CreateDirectory(directory);
		var json = name is null
			? $"{{ \"key\": \"{key}\" }}"
			: $"{{ \"key\": \"{key}\", \"name\": \"{name}\" }}";
		File.WriteAllText(Path.Combine(directory, VpkCustomModelFile.FileName), json);
		return directory;
	}

	private static RuntimeArmorRegistryModel Model(string key, string name)
		=> new(key, null, [new RuntimeArmorRegistryModelPiece(3, "0123456789abcdef", 2, 0)], name);

	/// <summary>
	/// 有 Mod 的留着，没有的删掉 —— 并且<b>连带删掉引用它的映射</b>。
	/// <para>
	/// 不删映射的话，下次校验会直接拒绝整份工作区（映射引用了不存在的素体）：
	/// 用户看到的是"护甲映射页打不开"，而不是"那个素体没了"。
	/// </para>
	/// </summary>
	[Fact]
	public async Task PruningKeepsOwnedModelsAndDropsTheMappingsPointingAtTheRest()
	{
		var registry = CreateRegistry();
		await registry.SaveWorkspaceAsync(new RuntimeArmorRegistryDocument(
			7,
			true,
			[
				new RuntimeArmorRegistryMapping(29533812, null, "custom-owned"),
				new RuntimeArmorRegistryMapping(34232232, null, "custom-orphan"),
			],
			[Model("custom-owned", "MourningVeil"), Model("custom-orphan", "milltina 尼克")]));

		OwnModel("MourningVeil_Bill", "custom-owned", "MourningVeil");

		var report = await CreateService(registry).PruneAsync();

		Assert.False(report.IsEmpty);
		Assert.Equal(["milltina 尼克"], report.Models);
		Assert.Equal(1, report.DroppedMappings);

		var after = await registry.ReadSnapshotAsync();
		Assert.Equal("custom-owned", Assert.Single(after.Document.ModelDefinitions).Key);
		Assert.Equal("custom-owned", Assert.Single(after.Document.Mappings).ModelKey);
	}

	/// <summary>
	/// 全都还有 Mod：一条都不许动，报告必须是空的（否则界面会平白弹一次"已清理"）。
	/// </summary>
	[Fact]
	public async Task PruningIsANoOpWhenEveryModelStillHasItsMod()
	{
		var registry = CreateRegistry();
		await registry.SaveWorkspaceAsync(new RuntimeArmorRegistryDocument(
			3,
			true,
			[new RuntimeArmorRegistryMapping(29533812, null, "custom-a")],
			[Model("custom-a", "A")]));
		OwnModel("mod-a", "custom-a");

		var report = await CreateService(registry).PruneAsync();

		Assert.True(report.IsEmpty);
		Assert.Null(report.Describe());
		Assert.Single((await registry.ReadSnapshotAsync()).Document.ModelDefinitions);
	}

	/// <summary>
	/// <b>库目录不在时一个字都不许删。</b>
	/// <para>
	/// 那个状态更可能是"Mod 库盘没挂上 / 路径配错"，而不是"用户的素体全都失去归属"。
	/// 按规则字面执行就会把整份注册表清空 —— 一次磁盘故障换掉用户所有映射，
	/// 而且他没有任何办法看出是怎么丢的。
	/// </para>
	/// </summary>
	[Fact]
	public async Task AMissingLibraryDirectoryNeverPrunesAnything()
	{
		var registry = CreateRegistry();
		await registry.SaveWorkspaceAsync(new RuntimeArmorRegistryDocument(
			4, true, [], [Model("custom-a", "A")]));

		// 服务被指到一个**不存在的库目录**：等价于"Mod 库盘没挂上 / 路径配错"。
		var service = new CustomModelOwnershipService(registry, Path.Combine(root, "library-not-mounted"));
		var report = await service.PruneAsync();

		Assert.True(report.IsEmpty);
		Assert.Single((await registry.ReadSnapshotAsync()).Document.ModelDefinitions);
	}

	/// <summary>
	/// "只查不删"与"真删"必须给出同一个答案。
	/// <para>
	/// 预演用在确认对话框上：两边不一致就等于骗用户（问了"会删 2 个"，实际删了 5 个）。
	/// </para>
	/// </summary>
	[Fact]
	public async Task InspectPromisesExactlyWhatPruneDoes()
	{
		var registry = CreateRegistry();
		await registry.SaveWorkspaceAsync(new RuntimeArmorRegistryDocument(
			5,
			true,
			[
				new RuntimeArmorRegistryMapping(29533812, null, "custom-orphan"),
				new RuntimeArmorRegistryMapping(34232232, null, "custom-orphan"),
			],
			[Model("custom-owned", "Keeper"), Model("custom-orphan", "Gone")]));
		OwnModel("keeper", "custom-owned");

		var service = CreateService(registry);
		var preview = await service.InspectAsync();
		// 预演**不写盘**：工作区还在原样。
		Assert.Equal(2, (await registry.ReadSnapshotAsync()).Document.ModelDefinitions.Count);

		var actual = await service.PruneAsync();

		Assert.Equal(preview.Models, actual.Models);
		Assert.Equal(preview.DroppedMappings, actual.DroppedMappings);
		Assert.Equal(2, actual.DroppedMappings);
	}

	/// <summary>
	/// 撤销登记（生成成功、导入失败时回滚）只摘掉那个素体与它的映射，别的都不动。
	/// </summary>
	[Fact]
	public async Task RemovingOneModelLeavesTheOthersAlone()
	{
		var registry = CreateRegistry();
		await registry.SaveWorkspaceAsync(new RuntimeArmorRegistryDocument(
			6,
			true,
			[
				new RuntimeArmorRegistryMapping(29533812, null, "custom-a"),
				new RuntimeArmorRegistryMapping(34232232, null, "custom-b"),
			],
			[Model("custom-a", "A"), Model("custom-b", "B")]));

		Assert.True(await CreateService(registry).RemoveAsync("custom-b"));
		// 不存在时不算错误，但也不谎称删了。
		Assert.False(await CreateService(registry).RemoveAsync("custom-b"));

		var after = await registry.ReadSnapshotAsync();
		Assert.Equal("custom-a", Assert.Single(after.Document.ModelDefinitions).Key);
		Assert.Equal("custom-a", Assert.Single(after.Document.Mappings).ModelKey);
	}

	/// <summary>
	/// <c>FindOrphans</c> 是纯函数：给出"哪些没主"，不做任何假设。
	/// <para>
	/// 单独测它，是因为上面几条都要先落盘才能断言 —— 而这条判据本身
	/// （key 精确匹配、大小写不敏感之外没有别的花招）值得被钉住。
	/// </para>
	/// </summary>
	[Fact]
	public void FindOrphansComparesModelKeysAgainstTheOwners()
	{
		var models = new[] { Model("custom-a", "A"), Model("custom-b", "B") };
		var owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
		{
			["CUSTOM-A"] = "mod a",
		};

		var orphans = VpkCustomModelFile.FindOrphans(models, owners);

		Assert.Equal("custom-b", Assert.Single(orphans).Key);
		// 库里一个持有者都没有 ⇒ 每一具都是孤儿（"库被清空了"就是这种状态）。
		Assert.Equal(2, VpkCustomModelFile.FindOrphans(models, new Dictionary<string, string>()).Count);
		Assert.Empty(VpkCustomModelFile.FindOrphans([], owners));
	}
}

