namespace HD2SourceMigration.Tests;

/// <summary>
/// 骨名哈希是"插新骨"与"顶点权重"两边必须一致的那个数；错了不会报错，只会让顶点静默不动。
/// 期望值是拿游戏自己的 <c>bonehash.txt</c> 交叉验证出来的，不是推的。
/// </summary>
public sealed class Hd2NameHashTests
{
	[Theory]
	// 左：名字；右：bonehash.txt 里的值（= 64 位哈希的高 32 位）
	[InlineData("head", 0x8c5570c9u)]
	[InlineData("chest", 0x37ef0820u)]
	[InlineData("hips", 0x668f6a68u)]
	[InlineData("spine1", 0x58d886fbu)]
	[InlineData("l_knee", 0x8fd2dfecu)]
	public void Of_MatchesTheGameBoneHashTable(string name, uint expected)
	{
		Assert.Equal(expected, Hd2NameHash.Of(name));
	}

	[Fact]
	public void Of_UsesTheHighHalfNotTheLowHalf()
	{
		// 这条是"口径"断言：低 32 位在 bonehash.txt 里一个都对不上。
		var hash64 = Hd2NameHash.Hash64("head");
		Assert.Equal((uint)(hash64 >> 32), Hd2NameHash.Of("head"));
		Assert.NotEqual((uint)(hash64 & 0xFFFFFFFFu), Hd2NameHash.Of("head"));
	}

	[Fact]
	public void Of_IsStableAndDistinct()
	{
		// 新插入的骨名（source_extra_72 这种）也走同一条路，必须稳定且互不相撞。
		Assert.Equal(Hd2NameHash.Of("source_extra_72"), Hd2NameHash.Of("source_extra_72"));
		Assert.NotEqual(Hd2NameHash.Of("source_extra_72"), Hd2NameHash.Of("source_extra_73"));
		Assert.NotEqual(Hd2NameHash.Of("source_extra_72"), Hd2NameHash.Of("head"));
	}

	[Fact]
	public void Of_HandlesShortAndLongNames()
	{
		// 长度 0/1/7/8/9 覆盖"没有整块、正好一块、跨块"三种尾部分支。
		foreach (var name in new[] { "", "a", "abcdefg", "abcdefgh", "abcdefghi" })
		{
			var value = Hd2NameHash.Of(name);
			_ = value;
		}

		Assert.Equal(Hd2NameHash.Of("abcdefgh"), Hd2NameHash.Of("abcdefgh"));
	}
}
