using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

/// <summary>
/// <c>wardrobe.json</c> 读出来的"目标名"：目标是自己的素体时一律是「自定义素体」。
/// <para>
/// 老文件里存的是素体名（<c>水墨牛牛-…（自定义素体）</c>）。名字跑到"目标"这一行上，
/// 用户会以为换到了另一具素体——而这个位置只回答"目标是哪一类"。
/// </para>
/// </summary>
public sealed class VpkWardrobeTargetNameTests : IDisposable
{
	private readonly string _root = Path.Combine(
		Path.GetTempPath(), "hd2-wardrobe-target-tests", Guid.NewGuid().ToString("N"));

	public VpkWardrobeTargetNameTests() => Directory.CreateDirectory(_root);

	[Fact]
	public async Task ReadAsync_NormalizesTheOwnModelTargetToTheGenericLabel()
	{
		await WriteAsync("""
			{ "schema": "hd2mod.vpk-wardrobe/1", "targetArmorArchiveId": "custom-model:custom-abc", "targetArmorName": "水墨牛牛-20261110-172224（自定义素体）" }
			""");

		var file = await VpkModWardrobeService.ReadAsync(_root);

		Assert.Equal("custom-model:custom-abc", file.TargetArmorArchiveId);
		Assert.Equal("自定义素体", file.TargetArmorName);
	}

	[Fact]
	public async Task ReadAsync_KeepsTheArmorNameForAGameArmorTarget()
	{
		await WriteAsync("""
			{ "schema": "hd2mod.vpk-wardrobe/1", "targetArmorArchiveId": "1e2ba3712fa1b030", "targetArmorName": "RS-100“制裁者”" }
			""");

		var file = await VpkModWardrobeService.ReadAsync(_root);

		Assert.Equal("RS-100“制裁者”", file.TargetArmorName);
	}

	private Task WriteAsync(string json)
		=> File.WriteAllTextAsync(Path.Combine(_root, VpkModWardrobeService.StateFileName), json);

	public void Dispose()
	{
		if (Directory.Exists(_root))
			Directory.Delete(_root, true);
	}
}
