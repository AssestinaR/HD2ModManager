using Xunit;

namespace HD2ModCli.Tests;

[Collection("cli-console")]
public sealed class ModMetadataAndJsonErrorCliTests
{
	[Fact]
	public async Task ModRenameAndNotes_ApplyNormalizedMetadataWithoutChangingProfileMembership()
	{
		await using var fixture = await CliManagementFixture.CreateAsync();

		Assert.Equal(0, await HD2ModCli.CliApplication.RunAsync(
		[
			"mod", "rename", fixture.StandardASelector, "  Renamed A  ",
			"--manager-root", fixture.ManagerRoot,
			"--confirm", "--json",
		]));
		Assert.Equal(0, await HD2ModCli.CliApplication.RunAsync(
		[
			"mod", "notes", "set", fixture.StandardASelector, "  useful note  ",
			"--manager-root", fixture.ManagerRoot,
			"--confirm", "--json",
		]));

		var renamed = await fixture.LoadSnapshotAsync();
		var renamedNode = renamed.Nodes[fixture.StandardA.Id];
		Assert.Equal("Renamed A", renamedNode.Metadata.Name);
		Assert.Equal("useful note", renamedNode.Metadata.Notes);
		Assert.Equal([fixture.StandardA.Id, fixture.StandardB.Id], Assert.Single(renamed.Profiles).Entries.OrderBy(entry => entry.LoadOrder).Select(entry => entry.NodeId).ToArray());

		Assert.Equal(0, await HD2ModCli.CliApplication.RunAsync(
		[
			"mod", "notes", "clear", fixture.StandardASelector,
			"--manager-root", fixture.ManagerRoot,
			"--confirm", "--json",
		]));
		var cleared = await fixture.LoadSnapshotAsync();
		Assert.Null(cleared.Nodes[fixture.StandardA.Id].Metadata.Notes);
		Assert.Equal([fixture.StandardA.Id, fixture.StandardB.Id], Assert.Single(cleared.Profiles).Entries.OrderBy(entry => entry.LoadOrder).Select(entry => entry.NodeId).ToArray());
	}

	[Theory]
	[InlineData(new[] { "mod", "rename", "00000000-0000-0000-0000-000000000000", "Name", "--json" }, "MutationConfirmationRequired")]
	[InlineData(new[] { "status", "--definitely-unknown", "--json" }, "UnknownOption")]
	[InlineData(new[] { "option", "move", "00000000-0000-0000-0000-000000000000", "--position", "0", "--json" }, "PositionInvalid")]
	public async Task JsonUserErrors_AreParseableAndExposeStableCodes(string[] args, string expectedCode)
	{
		var capture = await CliTestConsole.RunCapturedAsync(args);

		Assert.Equal(2, capture.ExitCode);
		Assert.True(string.IsNullOrWhiteSpace(capture.StandardError));
		using var document = capture.ParseJson();
		Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
		Assert.Equal(expectedCode, document.RootElement.GetProperty("error").GetProperty("code").GetString());
		Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("error").GetProperty("message").GetString()));
	}
}
