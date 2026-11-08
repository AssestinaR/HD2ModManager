using HD2SourceMigration.Retarget;
using Xunit;

namespace HD2ModCli.Tests;

/// <summary>
/// <c>--head-scale</c>：VPK 构建时的角色头部等比缩放比例（设置页「通用」卡片里那一项）。
/// <para>
/// <b>默认必须就是现行的 8/7。</b>设置里没存过时管理器**不传**这个参数，转换器用自己的
/// <see cref="RetargetOptions.HeadScale"/>；两处一旦不一致，用户什么都没改、默认观感却变了。
/// </para>
/// </summary>
public sealed class HeadScaleOptionTests
{
	[Fact]
	public void Default_IsTheShippedRatio()
	{
		// 发货比例 8/7。改这个常量等于改默认观感，必须与 SettingsService.DefaultHeadScale 同步。
		Assert.Equal(8.0 / 7.0, new RetargetOptions().HeadScale);
	}

	[Fact]
	public void Parse_KeepsTheValue()
	{
		var invocation = CliArgumentParser.Parse(["vpk2patch", "a.vpk", "--head-scale", "1.2"]);
		Assert.Equal(1.2, invocation.Options.HeadScale);
	}

	[Fact]
	public void Parse_WithoutTheOption_LeavesItUnset()
	{
		// 不传 = 用转换器默认值，不要在这里补一个 8/7 —— 补了就分不清"没设过"和"设成了 8/7"。
		Assert.Null(CliArgumentParser.Parse(["vpk2patch", "a.vpk"]).Options.HeadScale);
	}

	[Theory]
	[InlineData("0.4")]
	[InlineData("2.1")]
	[InlineData("abc")]
	[InlineData("")]
	public void Parse_RejectsOutOfRangeOrGarbage(string raw)
	{
		var error = Assert.Throws<CliUserException>(
			() => CliArgumentParser.Parse(["vpk2patch", "a.vpk", "--head-scale", raw]));
		Assert.Contains("--head-scale", error.Message);
	}
}
