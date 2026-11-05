using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

/// <summary>
/// 自定义素体的**权威来源**是 Mod 文件夹里那份 <c>custom-model.json</c>，注册表只是缓存。
/// <para>
/// 这一组钉住三件事：
/// </para>
/// <list type="number">
/// <item>能从两个固定位置（根 / <c>vpk2patch/</c>）收集到完整定义（含名字与件表）；</item>
/// <item><b>读不出来 ≠ 没有</b>：坏文件/无权限必须报进 <c>Unreadable</c>，让调用方 fail-closed；</item>
/// <item>库根上管理器自己的 <c>.hd2modmanager</c> 不被当成 Mod。</item>
/// </list>
/// </summary>
public sealed class CustomModelCatalogTests : IDisposable
{
	private readonly string root = Path.Combine(
		Path.GetTempPath(), "hd2-custom-model-catalog-" + Guid.NewGuid().ToString("N"));

	private string ModsDirectory => Path.Combine(root, "mods");

	public CustomModelCatalogTests() => Directory.CreateDirectory(ModsDirectory);

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

	private string WriteModel(string directoryName, string json, bool inPayloadDirectory = true)
	{
		var directory = inPayloadDirectory
			? Path.Combine(ModsDirectory, directoryName, ModNodePayloadLayout.VpkPatchDirectoryName)
			: Path.Combine(ModsDirectory, directoryName);
		Directory.CreateDirectory(directory);
		var path = Path.Combine(directory, VpkCustomModelFile.FileName);
		File.WriteAllText(path, json);
		return path;
	}

	private const string FullModel = """
		{
		  "key": "custom-1806c773b2fc44289f44c872b5f01d85",
		  "sourceItemId": null,
		  "name": "二阶堂希罗",
		  "pieces": [
		    { "body": 3, "unit": "77d99102e9c20001", "slot": 0, "pieceType": 0, "tone": 2,
		      "enabled": true, "isDefaultBase": false, "isReserved": false, "originalBody": 1 }
		  ]
		}
		""";

	[Fact]
	public void CollectsModelFromPayloadDirectoryWithNameAndPieces()
	{
		WriteModel("二阶堂希罗_bill", FullModel);

		var catalog = CustomModelCatalog.Collect(ModsDirectory);

		Assert.True(catalog.IsTrustworthy);
		var entry = catalog.Entries["custom-1806c773b2fc44289f44c872b5f01d85"];
		Assert.Equal("二阶堂希罗", entry.Model.Name);
		var piece = Assert.Single(entry.Model.Pieces!);
		Assert.Equal("77d99102e9c20001", piece.Unit);
		Assert.Equal(0u, piece.Slot);
		Assert.Equal((byte)2, piece.Tone);
		Assert.True(piece.Enabled);
		Assert.Equal(1u, piece.OriginalBody);
	}

	[Fact]
	public void CollectsModelFromModRootToo()
	{
		// 早期/手写的素体把文件放在根上；只认 vpk2patch/ 会让它们凭空消失。
		WriteModel("legacy_bill", FullModel, inPayloadDirectory: false);

		var catalog = CustomModelCatalog.Collect(ModsDirectory);

		Assert.True(catalog.Entries.ContainsKey("custom-1806c773b2fc44289f44c872b5f01d85"));
	}

	[Fact]
	public void ManagerDirectoryOnLibraryRootIsNotAMod()
	{
		var managerDirectory = Path.Combine(ModsDirectory, ".hd2modmanager");
		Directory.CreateDirectory(managerDirectory);
		File.WriteAllText(Path.Combine(managerDirectory, "armor-registry.workspace.json"), "{}");

		var catalog = CustomModelCatalog.Collect(ModsDirectory);

		// 只有管理器目录 ⇒ 一个 Mod 都没有 ⇒ 这次收集不可信（不能拿去删东西）。
		Assert.Empty(catalog.Entries);
		Assert.False(catalog.LibraryUsable);
	}

	[Fact]
	public void UnreadableFileMakesTheWholeCollectionUntrustworthy()
	{
		WriteModel("good_bill", FullModel);
		WriteModel("broken_bill", "{ this is not json");

		var catalog = CustomModelCatalog.Collect(ModsDirectory);

		Assert.Single(catalog.Entries);
		Assert.Single(catalog.Unreadable);
		// 关键：**不是**"读不到就当没有"。这条布尔量是 fail-closed 的唯一依据。
		Assert.False(catalog.IsTrustworthy);
		Assert.NotNull(catalog.DescribeBlocked());
	}

	[Fact]
	public void MissingLibraryIsNotTrustworthy()
	{
		var catalog = CustomModelCatalog.Collect(Path.Combine(root, "does-not-exist"));

		Assert.False(catalog.LibraryUsable);
		Assert.False(catalog.IsTrustworthy);
		Assert.Empty(catalog.Entries);
	}

	[Fact]
	public void FileWithoutKeyIsTreatedAsNoModelRatherThanBroken()
	{
		// 转换中断可能留下只有 name 的半成品：那是"没有"，不是"读不出来"，
		// 不该阻断整次对账（否则一个半成品文件会让所有映射都不敢动）。
		WriteModel("half_bill", "{ \"name\": \"只有名字\" }");

		var catalog = CustomModelCatalog.Collect(ModsDirectory);

		Assert.Empty(catalog.Entries);
		Assert.Empty(catalog.Unreadable);
		Assert.True(catalog.IsTrustworthy);
	}
}
