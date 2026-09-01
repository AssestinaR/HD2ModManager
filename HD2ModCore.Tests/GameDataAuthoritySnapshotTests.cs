using HD2ModCore.Domain;

namespace HD2ModCore.Tests;

public sealed class GameDataAuthoritySnapshotTests
{
	[Fact]
	public void BuildArchiveMetadata_PicksPreferredLocaleButRetainsAllLocalizedNames()
	{
		var snapshot = new GameDataAuthoritySnapshot(
			GameDataAuthoritySourceKind.PlaintextDataLibrary,
			"armor",
			"fingerprint",
			DateTimeOffset.UtcNow,
			[new GameDataAuthorityKit(
				1,
				0,
				0,
				GameDataEquipmentKitKind.Armor,
				"aaaaaaaaaaaaaaaa",
				"0xname",
				Array.Empty<GameDataAuthorityPiece>())],
			Array.Empty<CoreIssue>())
		{
			PreferredLocale = "zh-CN",
			ArchiveNames =
			[
				new GameDataAuthorityArchiveName("aaaaaaaaaaaaaaaa", 1, "en-US", "English Name", "strings", 100),
				new GameDataAuthorityArchiveName("aaaaaaaaaaaaaaaa", 1, "zh-CN", "中文名称", "strings", 100),
			]
		};

		var metadata = Assert.Single(snapshot.BuildArchiveMetadata().Values);

		Assert.Equal("中文名称", metadata.DisplayName);
		Assert.Equal("English Name", metadata.LocalizedNames["en-US"]);
		Assert.Equal("中文名称", metadata.LocalizedNames["zh-CN"]);
	}
}
