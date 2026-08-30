using Xunit;

namespace HD2ModCli.Tests;

[Collection("cli-console")]
public sealed class OptionSingleHostAndOrderingCliTests
{
	[Fact]
	public async Task OptionEnableDisable_AllowsAnOptionToHostItsOwnDirectOption()
	{
		await using var fixture = await CliManagementFixture.CreateAsync();

		Assert.Equal(0, await HD2ModCli.CliApplication.RunAsync(
		[
			"option", "enable", fixture.NestedChildSelector,
			"--host", fixture.NestedHostSelector,
			"--manager-root", fixture.ManagerRoot,
			"--confirm", "--json",
		]));
		var enabled = await fixture.LoadOptionActivationsAsync();
		Assert.True(enabled.IsEnabled(CliManagementFixture.Key(fixture.NestedChild.Id), CliManagementFixture.Key(fixture.NestedHost.Id)));
		Assert.True(enabled.IsEnabled(CliManagementFixture.Key(fixture.NestedHost.Id), CliManagementFixture.Key(fixture.RootHost.Id)));

		Assert.Equal(0, await HD2ModCli.CliApplication.RunAsync(
		[
			"option", "disable", fixture.NestedChildSelector,
			"--host", fixture.NestedHostSelector,
			"--manager-root", fixture.ManagerRoot,
			"--confirm", "--json",
		]));
		var disabled = await fixture.LoadOptionActivationsAsync();
		Assert.False(disabled.IsEnabled(CliManagementFixture.Key(fixture.NestedChild.Id), CliManagementFixture.Key(fixture.NestedHost.Id)));
		Assert.True(disabled.IsEnabled(CliManagementFixture.Key(fixture.NestedHost.Id), CliManagementFixture.Key(fixture.RootHost.Id)));
	}

	[Fact]
	public async Task OptionMoveAndInsert_ReorderOnlyTheSpecifiedDirectHostScope()
	{
		await using var fixture = await CliManagementFixture.CreateAsync();

		Assert.Equal(0, await HD2ModCli.CliApplication.RunAsync(
		[
			"option", "move", fixture.GammaSelector, fixture.AlphaSelector,
			"--host", fixture.RootHostSelector,
			"--position", "1",
			"--manager-root", fixture.ManagerRoot,
			"--confirm", "--json",
		]));
		Assert.Equal([fixture.Alpha.Id, fixture.Gamma.Id, fixture.Beta.Id, fixture.NestedHost.Id], await fixture.LoadRootOptionOrderAsync());

		Assert.Equal(0, await HD2ModCli.CliApplication.RunAsync(
		[
			"option", "insert", fixture.BetaSelector,
			"--host", fixture.RootHostSelector,
			"--position", "1",
			"--manager-root", fixture.ManagerRoot,
			"--confirm", "--json",
		]));
		Assert.Equal([fixture.Beta.Id, fixture.Alpha.Id, fixture.Gamma.Id, fixture.NestedHost.Id], await fixture.LoadRootOptionOrderAsync());

		var states = await fixture.LoadOptionActivationsAsync();
		Assert.Equal(0, states.GetEffectiveOrder(CliManagementFixture.Key(fixture.Beta.Id), CliManagementFixture.Key(fixture.RootHost.Id)));
		Assert.Equal(1, states.GetEffectiveOrder(CliManagementFixture.Key(fixture.Alpha.Id), CliManagementFixture.Key(fixture.RootHost.Id)));
		Assert.Equal(2, states.GetEffectiveOrder(CliManagementFixture.Key(fixture.Gamma.Id), CliManagementFixture.Key(fixture.RootHost.Id)));
		Assert.Equal(3, states.GetEffectiveOrder(CliManagementFixture.Key(fixture.NestedHost.Id), CliManagementFixture.Key(fixture.RootHost.Id)));
		Assert.Equal(0, states.GetEffectiveOrder(CliManagementFixture.Key(fixture.NestedChild.Id), CliManagementFixture.Key(fixture.NestedHost.Id)));
	}

	[Fact]
	public async Task OptionMove_InvalidPosition_RejectsBeforeWritingActivationState()
	{
		await using var fixture = await CliManagementFixture.CreateAsync();
		var before = await File.ReadAllBytesAsync(fixture.OptionActivationsPath);

		var capture = await CliTestConsole.RunCapturedAsync(
			"option", "move", fixture.AlphaSelector,
			"--host", fixture.RootHostSelector,
			"--position", "9",
			"--manager-root", fixture.ManagerRoot,
			"--confirm", "--json");

		Assert.Equal(2, capture.ExitCode);
		using var document = capture.ParseJson();
		Assert.Equal("OptionPositionOutOfRange", document.RootElement.GetProperty("error").GetProperty("code").GetString());
		Assert.Equal(before, await File.ReadAllBytesAsync(fixture.OptionActivationsPath));
	}
}
