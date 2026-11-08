using Xunit;

namespace HD2ModCli.Tests;

/// <summary>
/// 头身合并是**实验 / 不安全**功能：`--head-merge on` 必须同时带 `--head-merge-consent`。
/// <para>
/// 这条约束把"调用方（尤其是 agent）先向用户说明风险、拿到同意"变成硬性的：
/// 不带同意标记就直接拒绝，而不是替用户默认同意。
/// </para>
/// </summary>
public sealed class HeadMergeConsentTests
{
	[Fact]
	public async Task Vpk2Patch_RefusesHeadMergeWithoutConsent()
	{
		var run = await CliTestConsole.RunCapturedAsync(
			"vpk2patch", "not-a-real.vpk", "--output", Path.GetTempPath(),
			"--head-merge", "on", "--json");

		Assert.Equal(2, run.ExitCode);
		using var json = run.ParseJson();
		Assert.Equal("HeadMergeConsentRequired", json.RootElement.GetProperty("error").GetProperty("code").GetString());
	}

	[Fact]
	public async Task Vpk2Patch_AcceptsHeadMergeOnceConsentIsGiven()
	{
		// 带上同意标记之后就应当越过这道门，走到真正的输入校验（这里文件不存在）。
		var run = await CliTestConsole.RunCapturedAsync(
			"vpk2patch", "not-a-real.vpk", "--output", Path.GetTempPath(),
			"--head-merge", "on", "--head-merge-consent", "--json");

		Assert.Equal(2, run.ExitCode);
		using var json = run.ParseJson();
		var code = json.RootElement.GetProperty("error").GetProperty("code").GetString();
		Assert.NotEqual("HeadMergeConsentRequired", code);
		Assert.Equal("VpkNotFound", code);
	}
}
