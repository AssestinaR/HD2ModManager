using Xunit;
using static HD2ModCli.CliVpkRekey;

namespace HD2ModCli.Tests;

/// <summary>
/// 勾选状态在换甲时的翻译。
/// <para>
/// 状态存在<b>来源</b> Unit 键空间（<c>wardrobe.json</c> 按 <c>vpk2patch</c> 记），而烘焙要写到
/// 目标键上。翻译错的表现是"关错了部件"——比"没关"更难查，因为玩家看到的是另一块消失。
/// </para>
/// </summary>
public sealed class VpkRekeyDisabledTranslationTests
{
	private const string OldHead = "18054b9f5a540001";
	private const string OldChest = "18054b9f5a540002";
	private const string NewHead = "3c09e08493ee44b8";
	private const string NewChest = "cae35ee15168145f";

	private static Dictionary<string, string> Map() => new(StringComparer.OrdinalIgnoreCase)
	{
		[OldHead] = NewHead,
		[OldChest] = NewChest,
	};

	[Fact]
	public void MovesEveryEntryOntoItsTargetUnit()
	{
		var translated = TranslateDisabled(
			new Dictionary<string, IReadOnlyCollection<int>>(StringComparer.OrdinalIgnoreCase)
			{
				[OldHead] = [1, 2],
				[OldChest] = [0],
			},
			Map());

		Assert.NotNull(translated);
		Assert.Equal([1, 2], translated![NewHead]);
		Assert.Equal([0], translated[NewChest]);
		// 来源键不该残留：它们在新 patch 里不存在，留着只会被烘焙器当成"找不到"。
		Assert.DoesNotContain(OldHead, translated.Keys);
	}

	/// <summary>目标里没有这个 Unit 的条目直接丢掉——那个部件在新目标上没有落点，关它没有意义。</summary>
	[Fact]
	public void DropsEntriesWhoseUnitIsNotInTheTarget()
	{
		var translated = TranslateDisabled(
			new Dictionary<string, IReadOnlyCollection<int>>(StringComparer.OrdinalIgnoreCase)
			{
				["ffffffffffffffff"] = [0],
			},
			Map());

		Assert.Null(translated);
	}

	/// <summary>两个来源 Unit 落到同一个目标 Unit 时，section 要并起来而不是互相覆盖。</summary>
	[Fact]
	public void MergesEntriesThatLandOnTheSameTargetUnit()
	{
		var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
		{
			[OldHead] = NewHead,
			[OldChest] = NewHead,
		};
		var translated = TranslateDisabled(
			new Dictionary<string, IReadOnlyCollection<int>>(StringComparer.OrdinalIgnoreCase)
			{
				[OldHead] = [3],
				[OldChest] = [1],
			},
			map);

		Assert.NotNull(translated);
		Assert.Equal([1, 3], Assert.Single(translated!).Value);
	}

	[Fact]
	public void AcceptsUnitKeysWrittenWithAHexPrefix()
	{
		var translated = TranslateDisabled(
			new Dictionary<string, IReadOnlyCollection<int>>(StringComparer.OrdinalIgnoreCase)
			{
				["0x" + OldHead] = [4],
			},
			Map());

		Assert.NotNull(translated);
		Assert.Equal([4], translated![NewHead]);
	}

	[Fact]
	public void AnEmptyStateTranslatesToNullSoNothingIsBaked()
	{
		Assert.Null(TranslateDisabled(null, Map()));
		Assert.Null(TranslateDisabled(new Dictionary<string, IReadOnlyCollection<int>>(), Map()));
		// 有键但没有 section 的条目同样等于"什么都没关"。
		Assert.Null(TranslateDisabled(
			new Dictionary<string, IReadOnlyCollection<int>>(StringComparer.OrdinalIgnoreCase) { [OldHead] = [] },
			Map()));
	}
}
