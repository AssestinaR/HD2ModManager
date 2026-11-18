using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

// Purpose: 钉住"自由标签 + 草稿 = 有效查询"的拼接规则（去空白、去重、保序、空格连接）。
public sealed class ModSearchQueryTests
{
	[Fact]
	public void Compose_BothEmpty_IsEmpty()
	{
		Assert.Equal(string.Empty, ModSearchQuery.Compose(Array.Empty<string>(), string.Empty));
		Assert.Equal(string.Empty, ModSearchQuery.Compose(null, null));
		Assert.Equal(string.Empty, ModSearchQuery.Compose(new[] { "   " }, "  \t "));
	}

	[Fact]
	public void Compose_KeepsTagsFirstThenDraft()
	{
		Assert.Equal("大衣 健壮 纹身", ModSearchQuery.Compose(new[] { "大衣", "健壮" }, "纹身"));
	}

	[Fact]
	public void Compose_SplitsDraftOnWhitespace()
	{
		Assert.Equal("大衣 健壮", ModSearchQuery.Compose(Array.Empty<string>(), "  大衣   健壮 "));
	}

	[Fact]
	public void Compose_DeduplicatesIgnoringCase()
	{
		Assert.Equal("Cape armor", ModSearchQuery.Compose(new[] { "Cape", "armor" }, "CAPE"));
	}

	[Fact]
	public void Normalize_PreservesFirstSpellingAndOrder()
	{
		Assert.Equal(new[] { "Armor", "cape" }, ModSearchQuery.Normalize(new[] { "Armor", " cape ", "ARMOR" }));
	}

	[Fact]
	public void Normalize_Null_IsEmpty()
	{
		Assert.Empty(ModSearchQuery.Normalize(null));
	}
}
