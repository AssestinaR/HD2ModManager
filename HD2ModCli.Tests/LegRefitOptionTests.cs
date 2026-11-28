using HD2SourceMigration.Retarget;
using Xunit;

namespace HD2ModCli.Tests;

/// <summary>
/// <c>--leg-refit on|off</c>：VPK 构建时的腿部重拟合（设置页「通用」卡片里的开关）。
/// <para>
/// <b>默认必须是关。</b>设置里没存过时管理器不传这个参数，转换器用自己的
/// <see cref="RetargetOptions.LegRefit"/>；它一旦变成开，用户什么都没改、所有转换模型的腿就都变了。
/// </para>
/// </summary>
public sealed class LegRefitOptionTests
{
	[Fact]
	public void Default_IsOff()
	{
		Assert.Equal(LegRefitMode.Off, new RetargetOptions().LegRefit);
	}

	[Theory]
	[InlineData("on", true)]
	[InlineData("off", false)]
	public void Parse_KeepsTheValue(string raw, bool expected)
	{
		var invocation = CliArgumentParser.Parse(["vpk2patch", "a.vpk", "--leg-refit", raw]);
		Assert.Equal(expected, invocation.Options.LegRefit);
	}

	[Fact]
	public void Parse_WithoutTheOption_LeavesItUnset()
	{
		Assert.Null(CliArgumentParser.Parse(["vpk2patch", "a.vpk"]).Options.LegRefit);
	}

	[Theory]
	[InlineData("yes")]
	[InlineData("1")]
	[InlineData("")]
	public void Parse_RejectsAnythingButOnOrOff(string raw)
	{
		var error = Assert.Throws<CliUserException>(
			() => CliArgumentParser.Parse(["vpk2patch", "a.vpk", "--leg-refit", raw]));
		Assert.Contains("--leg-refit", error.Message);
	}
}
