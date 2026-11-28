using HD2SourceMigration.Retarget;
using Xunit;

namespace HD2ModCli.Tests;

public sealed class JointWeightOptionTests
{
	[Fact]
	public void Defaults_KneeAndShoulderFixOn_TwistOff_AndCliLeavesThemUnset()
	{
		var defaults = new RetargetOptions();
		Assert.False(defaults.ShoulderTwist);
		Assert.True(defaults.ShoulderFix);
		Assert.True(defaults.KneeBlend);
		var options = CliArgumentParser.Parse(["vpk2patch", "a.vpk"]).Options;
		Assert.Null(options.ShoulderTwist);
		Assert.Null(options.ShoulderFix);
		Assert.Null(options.KneeBlend);
	}

	[Theory]
	[InlineData("on", "off", true, false)]
	[InlineData("off", "on", false, true)]
	public void Parse_KeepsTheOptionsIndependent(string shoulder, string knee, bool expectedShoulder, bool expectedKnee)
	{
		var options = CliArgumentParser.Parse(
			["vpk2patch", "a.vpk", "--shoulder-twist", shoulder, "--knee-blend", knee]).Options;
		Assert.Equal(expectedShoulder, options.ShoulderTwist);
		Assert.Equal(expectedKnee, options.KneeBlend);
	}

	[Theory]
	[InlineData("--shoulder-twist")]
	[InlineData("--shoulder-fix")]
	[InlineData("--knee-blend")]
	public void Parse_RejectsInvalidValue(string flag)
	{
		var error = Assert.Throws<CliUserException>(() => CliArgumentParser.Parse(["vpk2patch", "a.vpk", flag, "yes"]));
		Assert.Contains(flag, error.Message);
	}

	[Theory]
	[InlineData("on", true)]
	[InlineData("off", false)]
	public void Parse_ShoulderFixIsIndependentOfShoulderTwist(string value, bool expected)
	{
		var options = CliArgumentParser.Parse(["vpk2patch", "a.vpk", "--shoulder-fix", value]).Options;
		Assert.Equal(expected, options.ShoulderFix);
		Assert.Null(options.ShoulderTwist);
	}
}
