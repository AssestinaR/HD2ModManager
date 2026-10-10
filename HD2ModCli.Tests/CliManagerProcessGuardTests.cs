using Xunit;

namespace HD2ModCli.Tests;

// 作用：显式覆盖"桌面端在运行时 CLI 必须拒绝写入 Mod 库"这条安全约束。
// Purpose: Covers the safety rule that every CLI Mod-library mutation is refused while a desktop
// HD2ModManager process is running.
//
// 其余 CLI 测试会把进程探测中性化为"未运行"，以便不依赖开发者桌面状态（见 CliTestEnvironment）。
// 这里刻意把探测还原成"有桌面端在运行"，确保该安全约束本身仍被验证——否则中性化就等于永久静默地
// 移除一条数据保护。
//
// The rest of the CLI suite neutralises the process probe so it does not depend on the developer's
// desktop (see CliTestEnvironment). This test deliberately restores it, so the constraint itself stays
// verified instead of being silently dropped by the neutralisation.
[Collection("cli-console")]
public sealed class CliManagerProcessGuardTests
{
	[Fact]
	public async Task LibraryMutation_WhileDesktopManagerIsRunning_IsRefused()
	{
		await using var fixture = await CliManagementFixture.CreateAsync();

		var capture = await CliTestEnvironment.WithManagerProcessIdsAsync(
			[4242],
			() => CliTestConsole.RunCapturedAsync(
				"mod", "rename", fixture.StandardASelector, "Renamed",
				"--manager-root", fixture.ManagerRoot,
				"--confirm", "--json"));

		Assert.Equal(2, capture.ExitCode);
		using var document = capture.ParseJson();
		Assert.Equal(
			"ManagerProcessRunning",
			document.RootElement.GetProperty("error").GetProperty("code").GetString());
	}

	[Fact]
	public async Task LibraryMutation_WhileNoDesktopManagerIsRunning_Succeeds()
	{
		await using var fixture = await CliManagementFixture.CreateAsync();

		var capture = await CliTestConsole.RunCapturedAsync(
			"mod", "rename", fixture.StandardASelector, "Renamed",
			"--manager-root", fixture.ManagerRoot,
			"--confirm", "--json");

		Assert.Equal(0, capture.ExitCode);
		var snapshot = await fixture.LoadSnapshotAsync();
		Assert.Equal("Renamed", snapshot.Nodes[fixture.StandardA.Id].Metadata.Name);
	}

	[Fact]
	public async Task LibraryMutation_WhenProcessEnumerationIsUnreliable_IsRefused()
	{
		await using var fixture = await CliManagementFixture.CreateAsync();

		// -1 is the sentinel for "could not enumerate reliably"; it must never be treated as a
		// safe write window.
		var capture = await CliTestEnvironment.WithManagerProcessIdsAsync(
			[-1],
			() => CliTestConsole.RunCapturedAsync(
				"mod", "rename", fixture.StandardASelector, "Renamed",
				"--manager-root", fixture.ManagerRoot,
				"--confirm", "--json"));

		Assert.Equal(2, capture.ExitCode);
		using var document = capture.ParseJson();
		Assert.Equal(
			"ManagerProcessCheckFailed",
			document.RootElement.GetProperty("error").GetProperty("code").GetString());
	}
}
