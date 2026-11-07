using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

/// <summary>
/// 锁住 Mod 自己的外观设置文件。
/// <para>
/// 它记的是**用户意图**（启用哪几个材质选项），不是派生数据——所以读不出来时必须返回
/// <c>null</c> 让调用方走默认，而不是当成"用户一个都没启用"。后者会让模型看起来没有材质。
/// </para>
/// </summary>
public sealed class ModSettingsFileTests : IDisposable
{
	private readonly string _root = Path.Combine(
		Path.GetTempPath(), "hd2-mod-settings-tests", Guid.NewGuid().ToString("N"));

	public ModSettingsFileTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		try { Directory.Delete(_root, recursive: true); } catch { }
	}

	[Fact]
	public void TryRead_OnMissingFile_ReturnsNull()
		=> Assert.Null(ModSettingsFile.TryRead(_root));

	[Fact]
	public void WriteThenRead_RoundTrips()
	{
		Assert.Null(ModSettingsFile.TryWrite(_root, new ModSettingsFile.Document(
			ModSettingsFile.CurrentSchema, ["9ba626afa44a3aa3.patch_1", "9ba626afa44a3aa3.patch_3"])));

		var document = ModSettingsFile.TryRead(_root);

		Assert.NotNull(document);
		Assert.Equal(["9ba626afa44a3aa3.patch_1", "9ba626afa44a3aa3.patch_3"], document!.EnabledMaterialPatches);
	}

	[Fact]
	public void Read_KeepsChineseReadable()
	{
		ModSettingsFile.TryWrite(_root, new ModSettingsFile.Document(ModSettingsFile.CurrentSchema, ["材质一"]));

		var text = File.ReadAllText(Path.Combine(_root, ModSettingsFile.FileName));

		Assert.Contains("材质一", text, StringComparison.Ordinal);
	}

	[Fact]
	public void TryRead_OnUnknownSchema_ReturnsNull()
	{
		File.WriteAllText(Path.Combine(_root, ModSettingsFile.FileName),
			"""{"schema":"hd2mod.mod/999","enabledMaterialPatches":[]}""");

		Assert.Null(ModSettingsFile.TryRead(_root));
	}

	[Fact]
	public void TryRead_OnBrokenJson_ReturnsNull()
	{
		File.WriteAllText(Path.Combine(_root, ModSettingsFile.FileName), "{ not json");

		Assert.Null(ModSettingsFile.TryRead(_root));
	}

	[Fact]
	public void TryWrite_OnEmptyDirectoryArgument_ReturnsReason()
		=> Assert.NotNull(ModSettingsFile.TryWrite(string.Empty, new ModSettingsFile.Document(
			ModSettingsFile.CurrentSchema, [])));
}
