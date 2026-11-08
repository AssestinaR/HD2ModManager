using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using Xunit;

namespace HD2ModCore.Tests;

/// <summary>
/// 锁住材质选项的**用户字段**：启用集合、顺序、名字、备注、配图。
/// <para>
/// 这几项都是用户输入，而且有两个必须成立的性质：
/// ① 顺序决定"谁覆盖谁"——它只改结构文件里的一个数字，不碰任何 patch；
/// ② 重转/折并重建结构文件时，它们必须原样留下来（否则用户改一次名字，下次重转就没了）。
/// </para>
/// </summary>
public sealed class MaterialOptionSettingsTests : IDisposable
{
	private const string Archive = "9ba626afa44a3aa3";
	private readonly string _root = Path.Combine(
		Path.GetTempPath(), "hd2-material-options-" + Guid.NewGuid().ToString("N"));

	public MaterialOptionSettingsTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		try { Directory.Delete(_root, recursive: true); } catch { }
	}

	/// <summary>一个宿主：patch_0(unit) + 两个材质 patch。</summary>
	private string NewHost()
	{
		var mod = Path.Combine(_root, "host");
		var payload = Path.Combine(mod, ModNodePayloadLayout.VpkPatchDirectoryName);
		Directory.CreateDirectory(payload);
		foreach (var index in new[] { 0, 1, 2 })
			File.WriteAllText(Path.Combine(payload, $"{Archive}.patch_{index}"), "toc");
		var document = SectionalPatchManifestFile.Build(payload, Archive, report: null, DateTimeOffset.UnixEpoch);
		Assert.Null(SectionalPatchManifestFile.TryWrite(payload, document));
		return mod;
	}

	[Fact]
	public void Read_ListsOnlyMaterialPatches_InDeclaredOrder()
	{
		var mod = NewHost();

		var options = MaterialOptionSettings.Read(mod);

		Assert.Equal([1, 2], options.Select(option => option.PatchIndex).ToArray());
		// **没有显式记录时默认全部启用**（2026-11-08 用户口径）：
		// "导入完就不管"的用户不该在游戏里看到模型没材质；不喜欢的人自己勾，勾过就有设置了。
		Assert.All(options, option => Assert.True(option.Enabled));
	}

	[Fact]
	public void Move_ReordersAndPersists()
	{
		var mod = NewHost();

		Assert.Null(MaterialOptionSettings.Move(mod, [2, 1]));

		Assert.Equal([2, 1], MaterialOptionSettings.Read(mod).Select(option => option.PatchIndex).ToArray());
		// patch 文件本身一个都没动
		var payload = ModNodePayloadLayout.GetVpkPatchDirectory(mod);
		Assert.True(File.Exists(Path.Combine(payload, $"{Archive}.patch_1")));
		Assert.True(File.Exists(Path.Combine(payload, $"{Archive}.patch_2")));
	}

	[Fact]
	public void SetEnabled_RecordsTheChosenSubset()
	{
		var mod = NewHost();

		Assert.Null(MaterialOptionSettings.SetEnabled(mod, [$"{Archive}.patch_2"]));

		var options = MaterialOptionSettings.Read(mod);
		Assert.False(options.Single(option => option.PatchIndex == 1).Enabled);
		Assert.True(options.Single(option => option.PatchIndex == 2).Enabled);
	}

	[Fact]
	public void SetNameAndNotes_Persist()
	{
		var mod = NewHost();

		Assert.Null(MaterialOptionSettings.SetName(mod, 2, "  蓝黑  "));
		Assert.Null(MaterialOptionSettings.SetNotes(mod, 2, "  夏天用  "));

		var option = MaterialOptionSettings.Read(mod).Single(item => item.PatchIndex == 2);
		Assert.Equal("蓝黑", option.Name);
		Assert.Equal("夏天用", option.Notes);
	}

	[Fact]
	public void SetImage_CopiesIntoTheOptionsDirectory()
	{
		var mod = NewHost();
		var source = Path.Combine(_root, "thumb.png");
		File.WriteAllText(source, "png");

		Assert.Null(MaterialOptionSettings.SetImage(mod, 1, source));

		var option = MaterialOptionSettings.Read(mod).Single(item => item.PatchIndex == 1);
		Assert.NotNull(option.ImageFileName);
		Assert.True(File.Exists(Path.Combine(MaterialOptionSettings.GetOptionsDirectory(mod), option.ImageFileName!)));
	}

	[Fact]
	public void SetImage_WithNull_ClearsIt()
	{
		var mod = NewHost();
		var source = Path.Combine(_root, "thumb.png");
		File.WriteAllText(source, "png");
		Assert.Null(MaterialOptionSettings.SetImage(mod, 1, source));

		Assert.Null(MaterialOptionSettings.SetImage(mod, 1, null));

		Assert.Null(MaterialOptionSettings.Read(mod).Single(item => item.PatchIndex == 1).ImageFileName);
	}

	[Fact]
	public async Task RebuildingTheManifest_KeepsUserFields()
	{
		var mod = NewHost();
		Assert.Null(MaterialOptionSettings.Move(mod, [2, 1]));
		Assert.Null(MaterialOptionSettings.SetName(mod, 2, "蓝黑"));
		Assert.Null(MaterialOptionSettings.SetNotes(mod, 2, "夏天用"));

		var payload = ModNodePayloadLayout.GetVpkPatchDirectory(mod);
		var previous = await SectionalPatchManifestFile.TryReadAsync(payload);
		var rebuilt = SectionalPatchManifestFile.Build(
			payload, Archive, report: null, DateTimeOffset.UnixEpoch, previous: previous);

		var entry = rebuilt.Patches.Single(item => item.PatchIndex == 2);
		Assert.Equal("蓝黑", entry.OptionName);
		Assert.Equal("夏天用", entry.Notes);
		Assert.Equal(0, entry.Order);
	}
}
