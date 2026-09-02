using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

public sealed class GameDataArchiveSemanticClassifierTests
{
	[Fact]
	public void Classify_UsesAuthoritativeKitKindBeforeCommunityCategory()
	{
		var authority = Snapshot(
			new GameDataAuthorityKit(
				1, 0, 0, GameDataEquipmentKitKind.Helmet, "aaaaaaaaaaaaaaaa", "Helmet", []));

		Assert.Equal("Helmet", GameDataArchiveSemanticClassifier.Classify("aaaaaaaaaaaaaaaa", "Weapon", authority));
	}

	[Fact]
	public void Classify_RecognizesAuthoritativeEntityWeapon()
	{
		var authority = Snapshot(
			archiveNames: [new GameDataAuthorityArchiveName(
				"bbbbbbbbbbbbbbbb", 0, "zh-CN", "AC-8“机炮”", "GameData.generated_entities+strings:exact-unit", 100, "Weapon")]);

		Assert.Equal("Weapon", GameDataArchiveSemanticClassifier.Classify("bbbbbbbbbbbbbbbb", "Other", authority));
	}

	[Theory]
	[InlineData("Armor", "Armor")]
	[InlineData("Pistol", "Weapon")]
	[InlineData("Buildings", "Other")]
	[InlineData(null, "Other")]
	public void Classify_UsesSmallFallbackVocabulary(string? category, string expected)
	{
		Assert.Equal(expected, GameDataArchiveSemanticClassifier.Classify(
			"cccccccccccccccc",
			category,
			Snapshot()));
	}

	private static GameDataAuthoritySnapshot Snapshot(
		params GameDataAuthorityKit[] kits)
		=> Snapshot(kits, []);

	private static GameDataAuthoritySnapshot Snapshot(
		IReadOnlyList<GameDataAuthorityKit>? kits = null,
		IReadOnlyList<GameDataAuthorityArchiveName>? archiveNames = null)
		=> new(
			GameDataAuthoritySourceKind.PlaintextDataLibrary,
			"game",
			"fingerprint",
			DateTimeOffset.UtcNow,
			kits ?? [],
			[])
		{
			ArchiveNames = archiveNames ?? []
		};
}
