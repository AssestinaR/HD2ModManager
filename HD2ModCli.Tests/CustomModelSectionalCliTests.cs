using Xunit;

namespace HD2ModCli.Tests;

/// <summary>
/// <c>custom-model sectional</c> 的命令行契约。
/// <para>
/// 只钉"参数没写对时说什么"这一层：真正的换甲 + 拆件需要一份**取代原版护甲/头盔**的
/// Mod 作为输入，那不是单元测试能造出来的（库里 133 个真实样本全是已转好的自定义素体）。
/// 所以这里守住最容易退化的部分 —— 用户少打参数时不该收到"不支持的命令"。
/// </para>
/// </summary>
public sealed class CustomModelSectionalCliTests
{
	[Fact]
	public async Task Sectional_MissingArguments_ReportsUsageInsteadOfUnknownCommand()
	{
		// 只有 "custom-model sectional plan"：少 <mod> <archives> <name>。
		var capture = await CliTestConsole.RunCapturedAsync("custom-model", "sectional", "plan");

		Assert.Equal(2, capture.ExitCode);
		// 关键：**不能**退化成"不支持的命令"。那只说明派发没接上，用户看不出自己少写了参数。
		Assert.DoesNotContain("UnknownCommand", capture.StandardError);
		Assert.Contains("InvalidArguments", capture.StandardError);
		Assert.Contains("plan|apply", capture.StandardError);
	}

	[Fact]
	public async Task Sectional_UnknownSubcommand_IsRejectedByArgumentValidation()
	{
		var capture = await CliTestConsole.RunCapturedAsync("custom-model", "sectional", "bogus", "a", "b", "c");

		Assert.Equal(2, capture.ExitCode);
		Assert.Contains("InvalidArguments", capture.StandardError);
		// 只接受 plan / apply，别的都在打开库之前就被挡下。
		Assert.Contains("plan 或 apply", capture.StandardError);
	}

	[Fact]
	public async Task Sectional_ApplyWithoutConfirm_IsRejected()
	{
		// 会写库的命令必须有 --confirm：这条护栏不能因为参数校验顺序而漏掉。
		var capture = await CliTestConsole.RunCapturedAsync("custom-model", "sectional", "apply", "a", "b", "c");

		Assert.Equal(2, capture.ExitCode);
		Assert.Contains("ConfirmationRequired", capture.StandardError);
	}
}
