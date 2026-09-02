using HD2GameItemAssets;
using HD2ModAdaptation.Analysis;
using AdaptationAssetKey = HD2ModAdaptation.PatchReconstruction.AssetKey;
using AdaptationStreamLayoutFact = HD2ModAdaptation.Analysis.GameDataStreamLayoutFact;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

public sealed class GameDataAuthorityArchiveNameResolverTests
{
	[Fact]
	public void Enrich_UsesExactWeaponAndExcludesSharedBackpackUnit()
	{
		const ulong weaponUnit = 0x1001UL;
		const ulong backpackUnit = 0x1002UL;
		var snapshot = CreateSnapshot(
			new GameDataAuthorityEntityFact(
				weaponUnit,
				new AssetKey(PatchUnitMeshReader.UnitTypeId, weaponUnit),
				true,
				true,
				[GeneratedEntityTypeIds.EquipmentComponentData, GeneratedEntityTypeIds.WieldableComponentData, GeneratedEntityTypeIds.WeaponDataComponentData],
				new Dictionary<string, string> { ["zh-CN"] = "解放者" },
				1,
				new Dictionary<string, string> { ["zh-CN"] = "AR-23" }),
			new GameDataAuthorityEntityFact(
				0x2002UL,
				new AssetKey(PatchUnitMeshReader.UnitTypeId, backpackUnit),
				false,
				false,
				[GeneratedEntityTypeIds.EquipmentComponentData, GeneratedEntityTypeIds.WieldableComponentData],
				new Dictionary<string, string> { ["zh-CN"] = "护卫犬" },
				21));
		var index = CreateIndex(
			("aaaaaaaaaaaaaaaa", "Assault Rifle", weaponUnit),
			("aaaaaaaaaaaaaaaa", "Assault Rifle", backpackUnit));

		var enriched = GameDataAuthorityArchiveNameResolver.Enrich(snapshot, index);

		var metadata = Assert.Single(enriched.BuildArchiveMetadata().Values);
		Assert.Equal("AR-23“解放者”", metadata.DisplayName);
		Assert.DoesNotContain("护卫犬", metadata.DisplayName, StringComparison.Ordinal);
	}

	[Fact]
	public void Enrich_AllowsDirectionalShieldByEquipmentType()
	{
		const ulong unit = 0x3001UL;
		var snapshot = CreateSnapshot(
			new GameDataAuthorityEntityFact(
				unit,
				new AssetKey(PatchUnitMeshReader.UnitTypeId, unit),
				true,
				false,
				[GeneratedEntityTypeIds.EquipmentComponentData, GeneratedEntityTypeIds.WieldableComponentData],
				new Dictionary<string, string> { ["zh-CN"] = "定向护盾" },
				GeneratedEquipmentTypeValues.DirectionalShield));
		var index = CreateIndex(("bbbbbbbbbbbbbbbb", "Support Weapon", unit));

		var metadata = Assert.Single(GameDataAuthorityArchiveNameResolver.Enrich(snapshot, index).BuildArchiveMetadata().Values);

		Assert.Equal("定向护盾", metadata.DisplayName);
	}

	[Fact]
	public void Enrich_AggregatesDistinctNamesFromMultipleWeaponUnits()
	{
		var snapshot = CreateSnapshot(
			CreateWeaponFact(0x4001UL, "第一武器", designation: null),
			CreateWeaponFact(0x4002UL, "第二武器", designation: null));
		var index = CreateIndex(
			("cccccccccccccccc", "Weapon", 0x4001UL),
			("cccccccccccccccc", "Weapon", 0x4002UL));

		var metadata = Assert.Single(GameDataAuthorityArchiveNameResolver.Enrich(snapshot, index).BuildArchiveMetadata().Values);

		Assert.Equal("第一武器 & 第二武器", metadata.DisplayName);
	}

	private static GameDataAuthorityEntityFact CreateWeaponFact(ulong unit, string name, string? designation = "W-1")
		=> new(
			unit,
			new AssetKey(PatchUnitMeshReader.UnitTypeId, unit),
			true,
			true,
			[GeneratedEntityTypeIds.EquipmentComponentData, GeneratedEntityTypeIds.WieldableComponentData, GeneratedEntityTypeIds.WeaponDataComponentData],
			new Dictionary<string, string> { ["zh-CN"] = name },
			1,
			designation is null ? null : new Dictionary<string, string> { ["zh-CN"] = designation });

	private static GameDataAuthoritySnapshot CreateSnapshot(params GameDataAuthorityEntityFact[] facts)
		=> new(
			GameDataAuthoritySourceKind.Unavailable,
			null,
			"test",
			DateTimeOffset.UtcNow,
			Array.Empty<GameDataAuthorityKit>(),
			Array.Empty<CoreIssue>())
		{
			PreferredLocale = "zh-CN",
			EntityFacts = facts
		};

	private static GameDataArchiveIndex CreateIndex(params (string Archive, string Category, ulong Unit)[] archives)
	{
		var facts = archives
			.GroupBy(item => item.Archive, StringComparer.OrdinalIgnoreCase)
			.Select(group => new GameDataArchiveFact(
				group.Key,
				group.Key,
				group.First().Archive,
				group.First().Category,
				false,
				group.Select((item, index) => new GameDataArchiveEntryFact(
					new AdaptationAssetKey(PatchUnitMeshReader.UnitTypeId, item.Unit),
					group.Key,
					(uint)index,
					0,
					0,
					0,
					0,
					0,
					0,
					0,
					0,
					0,
					0)).ToArray(),
				Array.Empty<PatchAnalysisIssue>()))
			.ToArray();
		return new GameDataArchiveIndex(
			new GameDataArchiveInput("."),
			facts,
			Array.Empty<AdaptationStreamLayoutFact>(),
			Array.Empty<PatchAnalysisIssue>(),
			DateTimeOffset.UtcNow,
			"test",
			"test");
	}
}
