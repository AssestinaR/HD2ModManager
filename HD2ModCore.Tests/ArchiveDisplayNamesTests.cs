using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

/// <summary>
/// 注册表里有 32 个 kit 的 upper/cased/desc 三个 string id 全都不在 15 张语言表里
/// （未发布/被砍掉的装备）。GameItemAssetCatalog 会把查不到的 id 渲染成 0x&lt;id&gt;——
/// 那是字符串 id，不是名字。这组用例钉住"不许把它当显示名"。
/// </summary>
public sealed class ArchiveDisplayNamesTests
{
	[Theory]
	[InlineData("0xd8bce5f1")]
	[InlineData("0X2EEC8681")]
	[InlineData("0x34eeb43a")]
	[InlineData("—")]
	[InlineData("")]
	[InlineData("   ")]
	[InlineData(null)]
	public void IsReadable_RejectsOpaqueValues(string? value)
	{
		Assert.False(ArchiveDisplayNames.IsReadable(value));
	}

	[Theory]
	[InlineData("AR-23「解放者」")]
	[InlineData("UF-84 Doubt Killer")]
	[InlineData("env_super_earth/habs")]
	[InlineData("Rifle 5,5x50mm. Drum")]
	public void IsReadable_AcceptsRealNames(string value)
	{
		Assert.True(ArchiveDisplayNames.IsReadable(value));
	}

	[Fact]
	public void IsReadable_RejectsHashPrefixedPlaceholder()
	{
		Assert.False(ArchiveDisplayNames.IsReadable("hash-0x1234"));
	}

	[Fact]
	public void IsPlaceholder_DetectsPackageNameAsDisplayName()
	{
		Assert.True(ArchiveDisplayNames.IsPlaceholder("0845a15bbb31dcc6", "0845a15bbb31dcc6"));
		Assert.True(ArchiveDisplayNames.IsPlaceholder(null, "0845a15bbb31dcc6"));
		Assert.False(ArchiveDisplayNames.IsPlaceholder("AF-91 Field Chemist", "0845a15bbb31dcc6"));
	}
}
