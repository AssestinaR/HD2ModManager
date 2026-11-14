using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using HD2ModCore.Infrastructure.KitInstance;
using HD2ModCore.Infrastructure.Sectional;

namespace HD2ModCore.Tests;

public sealed class ArmorMappingPickerPlannerTests
{
	private static RuntimeArmorCatalogEntry Entry(
		uint id,
		string name,
		GameDataEquipmentKitKind kind = GameDataEquipmentKitKind.Armor,
		uint setId = 0,
		uint dlcId = 0,
		string? archiveId = null)
		=> new(id, archiveId ?? $"0x{id:x8}", name, kind, setId, dlcId);

	[Fact]
	public void BuildBaseCatalog_FiltersHexAndNonPlayerKits_AndIsolatesDemocracyOfficer()
	{
		var catalog = new List<RuntimeArmorCatalogEntry>
		{
			Entry(0x11111111u, "0xdeadbeef", GameDataEquipmentKitKind.Armor, 1, 1),
			Entry(0x22222222u, "0xfeedface", GameDataEquipmentKitKind.Helmet, 1, 1),
			Entry(2107035846u, "隐藏护甲", GameDataEquipmentKitKind.Armor, 2, 1),
			Entry(1969314276u, "隐藏测试护甲", GameDataEquipmentKitKind.Armor, 3, 1),
			Entry(0x4db09ca4u, "未命名非玩家头盔", GameDataEquipmentKitKind.Helmet, 4, 1),
			Entry(0x782087b2u, "SC-30“侦察先驱”", GameDataEquipmentKitKind.Helmet, 5, 1),
			Entry(0x9b5bc520u, "FS-05“标记师”", GameDataEquipmentKitKind.Helmet, 6, 1),
			Entry(0x0542e6f1u, "CM-09“碎骨者”", GameDataEquipmentKitKind.Helmet, 7, 1),
			Entry(KitNonPlayerArmors.DemocracyOfficerArmorId, "FS-05“标记师”", GameDataEquipmentKitKind.Armor, 6, 1),
			Entry(1001u, "FS-05“标记师”", GameDataEquipmentKitKind.Armor, 6, 1),
			Entry(1002u, "FS-05“标记师”", GameDataEquipmentKitKind.Armor, 6, 1),
			Entry(2001u, "FS-05“标记师”", GameDataEquipmentKitKind.Helmet, 6, 1),
			Entry(2002u, "FS-05“标记师”", GameDataEquipmentKitKind.Helmet, 6, 1),
			Entry(1003u, "SC-30“侦察先驱”", GameDataEquipmentKitKind.Armor, 5, 1),
			Entry(2003u, "SC-30“侦察先驱”", GameDataEquipmentKitKind.Helmet, 5, 1),
		};

		var baseCatalog = ArmorMappingPickerPlanner.BuildBaseCatalog(catalog);

		// 所有非玩家及未命名都不应出现在任何选项卡中
		var allMemberIds = baseCatalog.Sets.SelectMany(row => row.MemberItemIds)
			.Concat(baseCatalog.Armors.SelectMany(row => row.MemberItemIds))
			.Concat(baseCatalog.Helmets.SelectMany(row => row.MemberItemIds))
			.ToHashSet();
		Assert.DoesNotContain(0x11111111u, allMemberIds);
		Assert.DoesNotContain(0x22222222u, allMemberIds);
		Assert.DoesNotContain(2107035846u, allMemberIds);
		Assert.DoesNotContain(1969314276u, allMemberIds);
		foreach (var helmetId in KitNonPlayerArmors.AllHelmetIds)
			Assert.DoesNotContain(helmetId, allMemberIds);

		// 民主官 NPC 单独成行且带标签，不合入玩家 FS-05，也不参与套装配对
		var npc = Assert.Single(baseCatalog.Armors, row => row.IsDemocracyOfficerNpc);
		Assert.Equal(KitNonPlayerArmors.DemocracyOfficerArmorId, npc.PrimaryItemId);
		Assert.Equal([KitNonPlayerArmors.DemocracyOfficerArmorId], npc.MemberItemIds);
		Assert.Equal(1, npc.VersionCount);
		Assert.Equal("护甲", npc.TypeLabel);

		var playerFs05Armor = Assert.Single(baseCatalog.Armors, row => !row.IsDemocracyOfficerNpc && row.Name == "FS-05“标记师”");
		Assert.Equal(2, playerFs05Armor.VersionCount);
		Assert.Equal([1001u, 1002u], playerFs05Armor.MemberItemIds);

		var fs05Helmet = Assert.Single(baseCatalog.Helmets, row => row.Name == "FS-05“标记师”");
		Assert.Equal([2001u, 2002u], fs05Helmet.MemberItemIds);

		var fs05Set = Assert.Single(baseCatalog.Sets, row => row.Name == "FS-05“标记师”");
		Assert.Equal([1001u, 2001u], fs05Set.ProviderSourceItemIds);
		Assert.Equal(4, fs05Set.MemberItemIds.Count);
		Assert.Equal("头盔+护甲", fs05Set.TypeLabel);

		var sc30Set = Assert.Single(baseCatalog.Sets, row => row.Name == "SC-30“侦察先驱”");
		Assert.Equal([1003u, 2003u], sc30Set.MemberItemIds);
	}

	[Fact]
	public void BuildBaseCatalog_MergesSameNameVersions_PairsThreeRounds_AndExcludesSoloFromSets()
	{
		var catalog = new List<RuntimeArmorCatalogEntry>
		{
			// Round 1: OCAS 配对（使用不同的 SetId / DlcId 与不同名称，验证 OCAS 优先且命名拼接为 A + B）
			Entry(0x26d572a4u, "FS-23 战神护甲", GameDataEquipmentKitKind.Armor, setId: 100, dlcId: 1),
			Entry(0x1e9444f9u, "FS-23 战神头盔", GameDataEquipmentKitKind.Helmet, setId: 200, dlcId: 2),
			// Round 1 + 同名合并: B-01 多版本护甲与头盔，同名且应合并成同一套
			Entry(0x1369b95bu, "B-01“战术家”", GameDataEquipmentKitKind.Armor, setId: 10, dlcId: 1),
			Entry(0x1d0e3431u, "B-01“战术家”", GameDataEquipmentKitKind.Armor, setId: 11, dlcId: 1),
			Entry(0x250fd6c1u, "B-01“战术家”", GameDataEquipmentKitKind.Armor, setId: 12, dlcId: 1),
			Entry(0x2180eb8du, "B-01“战术家”", GameDataEquipmentKitKind.Helmet, setId: 10, dlcId: 1),
			Entry(0x261c4a52u, "B-01“战术家”", GameDataEquipmentKitKind.Helmet, setId: 13, dlcId: 1),
			// Round 2: 注册表 (DlcId, SetId) 配对（名称不同）
			Entry(9001u, "GS-11 甲", GameDataEquipmentKitKind.Armor, setId: 777, dlcId: 9),
			Entry(9002u, "GS-11 盔", GameDataEquipmentKitKind.Helmet, setId: 777, dlcId: 9),
			// Round 3b: 同名跨 DlcId 兜底配对（SetId 不同）
			Entry(9003u, "DS-42“联邦之刃”", GameDataEquipmentKitKind.Armor, setId: 801, dlcId: 3),
			Entry(9004u, "DS-42“联邦之刃”", GameDataEquipmentKitKind.Helmet, setId: 802, dlcId: 4),
			// 单件（无配对，不应进套装选项卡）
			Entry(9005u, "IX-孤狼头盔", GameDataEquipmentKitKind.Helmet, setId: 901, dlcId: 5),
			Entry(9006u, "EX-独狼护甲", GameDataEquipmentKitKind.Armor, setId: 902, dlcId: 5),
		};

		var baseCatalog = ArmorMappingPickerPlanner.BuildBaseCatalog(catalog);

		Assert.Equal(4, baseCatalog.Sets.Count);
		var b01Set = Assert.Single(baseCatalog.Sets, row => row.Name == "B-01“战术家”");
		Assert.Equal(3, b01Set.VersionCount);
		Assert.Equal(5, b01Set.MemberItemIds.Count);
		Assert.Equal(2, b01Set.ProviderSourceItemIds.Count);

		var fs23Set = Assert.Single(baseCatalog.Sets, row => row.Name == "FS-23 战神护甲 + FS-23 战神头盔");
		Assert.Equal("头盔+护甲", fs23Set.TypeLabel);

		Assert.Contains(baseCatalog.Sets, row => row.Name == "GS-11 甲 + GS-11 盔");
		Assert.Contains(baseCatalog.Sets, row => row.Name == "DS-42“联邦之刃”");

		Assert.DoesNotContain(baseCatalog.Sets, row => row.MemberItemIds.Contains(9005u) || row.MemberItemIds.Contains(9006u));
		Assert.Contains(baseCatalog.Helmets, row => row.PrimaryItemId == 9005u);
		Assert.Contains(baseCatalog.Armors, row => row.PrimaryItemId == 9006u);
	}

	[Fact]
	public void EvaluateProjection_HidesFullyManagedRows_AndBlocksPartiallyManagedSets()
	{
		var catalog = new List<RuntimeArmorCatalogEntry>
		{
			Entry(101u, "A-01", GameDataEquipmentKitKind.Armor, setId: 1, dlcId: 1),
			Entry(102u, "A-01", GameDataEquipmentKitKind.Helmet, setId: 1, dlcId: 1),
			Entry(201u, "B-02", GameDataEquipmentKitKind.Armor, setId: 2, dlcId: 1),
			Entry(202u, "B-02", GameDataEquipmentKitKind.Helmet, setId: 2, dlcId: 1),
		};
		var baseCatalog = ArmorMappingPickerPlanner.BuildBaseCatalog(catalog);
		var managed = new Dictionary<uint, string>
		{
			[101u] = "已映射到 自定义素体A",
			[102u] = "已映射到 自定义素体A",
			[201u] = "已映射到 视觉甲B",
		};

		var projection = ArmorMappingPickerPlanner.EvaluateProjection(
			baseCatalog,
			managed,
			new HashSet<string>(),
			isTwoSlotCompressedSourceOpen: false);

		// A-01 全套都已管理，三边都完全隐藏
		Assert.DoesNotContain(projection.Sets, row => row.Name == "A-01");
		Assert.DoesNotContain(projection.Armors, row => row.Name == "A-01");
		Assert.DoesNotContain(projection.Helmets, row => row.Name == "A-01");

		// B-02 仅护甲已管理：护甲隐藏，头盔可用，套装保留但置灰并标明原因
		Assert.DoesNotContain(projection.Armors, row => row.Name == "B-02");
		var b02Helmet = Assert.Single(projection.Helmets, row => row.Name == "B-02");
		Assert.False(b02Helmet.IsBlocked);

		var b02Set = Assert.Single(projection.Sets, row => row.Name == "B-02");
		Assert.True(b02Set.IsBlocked);
		Assert.Equal("护甲已映射到 视觉甲B", b02Set.BlockedReason);
	}

	[Fact]
	public void EvaluateProjection_CrossTabSelectionBlocksConflicts_AndAllowsDisjointRows()
	{
		var catalog = new List<RuntimeArmorCatalogEntry>
		{
			Entry(101u, "Alpha", GameDataEquipmentKitKind.Armor, setId: 1, dlcId: 1),
			Entry(102u, "Alpha", GameDataEquipmentKitKind.Helmet, setId: 1, dlcId: 1),
			Entry(201u, "Beta", GameDataEquipmentKitKind.Armor, setId: 2, dlcId: 1),
			Entry(202u, "Beta", GameDataEquipmentKitKind.Helmet, setId: 2, dlcId: 1),
		};
		var baseCatalog = ArmorMappingPickerPlanner.BuildBaseCatalog(catalog);
		var alphaSetKey = Assert.Single(baseCatalog.Sets, row => row.Name == "Alpha").SelectionKey;
		var betaHelmetKey = Assert.Single(baseCatalog.Helmets, row => row.Name == "Beta").SelectionKey;

		// 选中 Alpha 套装 + Beta 头盔
		var projection = ArmorMappingPickerPlanner.EvaluateProjection(
			baseCatalog,
			new Dictionary<uint, string>(),
			new HashSet<string>(StringComparer.OrdinalIgnoreCase) { alphaSetKey, betaHelmetKey },
			isTwoSlotCompressedSourceOpen: false);

		// 已选自身不置灰
		var alphaSet = Assert.Single(projection.Sets, row => row.Name == "Alpha");
		Assert.False(alphaSet.IsBlocked);
		var betaHelmet = Assert.Single(projection.Helmets, row => row.Name == "Beta");
		Assert.False(betaHelmet.IsBlocked);

		// Alpha 套装冲突 -> Alpha 护甲与 Alpha 头盔均置灰
		var alphaArmor = Assert.Single(projection.Armors, row => row.Name == "Alpha");
		Assert.True(alphaArmor.IsBlocked);
		Assert.Equal("已包含在所选套装 Alpha 里", alphaArmor.BlockedReason);

		var alphaHelmet = Assert.Single(projection.Helmets, row => row.Name == "Alpha");
		Assert.True(alphaHelmet.IsBlocked);
		Assert.Equal("已包含在所选套装 Alpha 里", alphaHelmet.BlockedReason);

		// Beta 头盔已选 -> Beta 套装置灰，但 Beta 护甲仍可选（跨选项卡独立件互不冲突）
		var betaSet = Assert.Single(projection.Sets, row => row.Name == "Beta");
		Assert.True(betaSet.IsBlocked);
		Assert.Equal("所选头盔 Beta 属于这个套装", betaSet.BlockedReason);

		var betaArmor = Assert.Single(projection.Armors, row => row.Name == "Beta");
		Assert.False(betaArmor.IsBlocked);
	}

	[Fact]
	public void TwoSlotCompressedSource_BlocksHelmetTabOnly_AndReadsWardrobeFallback()
	{
		var catalog = new List<RuntimeArmorCatalogEntry>
		{
			Entry(101u, "Tact", GameDataEquipmentKitKind.Armor, setId: 1, dlcId: 1),
			Entry(102u, "Tact", GameDataEquipmentKitKind.Helmet, setId: 1, dlcId: 1),
		};

		var projection = ArmorMappingPickerPlanner.EvaluateProjection(
			ArmorMappingPickerPlanner.BuildBaseCatalog(catalog),
			new Dictionary<uint, string>(),
			new HashSet<string>(),
			isTwoSlotCompressedSourceOpen: true);

		Assert.False(Assert.Single(projection.Sets).IsBlocked);
		Assert.False(Assert.Single(projection.Armors).IsBlocked);
		var helmet = Assert.Single(projection.Helmets);
		Assert.True(helmet.IsBlocked);
		Assert.Equal(ArmorMappingPickerPlanner.TwoSlotHelmetBlockedReason, helmet.BlockedReason);

		var tempDir = Path.Combine(Path.GetTempPath(), "hd2-picker-twoslot-" + Guid.NewGuid().ToString("N"));
		try
		{
			Directory.CreateDirectory(tempDir);
			File.WriteAllText(VpkModWardrobeService.GetStatePath(tempDir), """{"headMergeIntoBody":true}""");
			Assert.True(ArmorMappingPickerPlanner.IsTwoSlotCompressedModDirectory(tempDir, lowPollutionMode: true));
			Assert.False(ArmorMappingPickerPlanner.IsTwoSlotCompressedModDirectory(tempDir, lowPollutionMode: false));

			var overwrite = ModNodePayloadLayout.GetOverwriteDirectory(tempDir);
			Directory.CreateDirectory(overwrite);
			File.WriteAllText(Path.Combine(overwrite, LowPollutionFreshness.UnsupportedMarkerFileName), "unsupported");
			Assert.False(ArmorMappingPickerPlanner.IsTwoSlotCompressedModDirectory(tempDir, lowPollutionMode: true));
		}
		finally
		{
			if (Directory.Exists(tempDir))
				Directory.Delete(tempDir, recursive: true);
		}
	}

	[Fact]
	public void BuildTargetGroupPlans_PreservesUnknownAndMergesSameNameAndSets()
	{
		var targets = new List<RuntimeArmorCatalogEntry>
		{
			Entry(101u, "A-01", GameDataEquipmentKitKind.Armor, setId: 1, dlcId: 1),
			Entry(102u, "A-01", GameDataEquipmentKitKind.Armor, setId: 2, dlcId: 1),
			Entry(103u, "A-01", GameDataEquipmentKitKind.Helmet, setId: 1, dlcId: 1),
			Entry(999999u, "未知护甲 999999", GameDataEquipmentKitKind.Unknown, setId: 0, dlcId: 0, archiveId: "999999"),
		};

		var grouped = ArmorMappingPickerPlanner.BuildTargetGroupPlans(targets);

		Assert.Equal(2, grouped.Count);
		var setRow = Assert.Single(grouped, row => row.Tab == ArmorMappingPickerTab.Set);
		Assert.Equal("A-01", setRow.Name);
		Assert.Equal("头盔+护甲", setRow.TypeLabel);
		Assert.Equal([101u, 102u, 103u], setRow.MemberItemIds);

		var unknownRow = Assert.Single(grouped, row => row.PrimaryItemId == 999999u);
		Assert.Equal("未知护甲 999999", unknownRow.Name);
	}

	[Fact]
	public async Task BuildBaseCatalog_RealAssetIndex_MatchesExpectedThreeTabCounts()
	{
		var managerRoot = TryFindManagerRootWithAssetIndex();
		if (managerRoot is null) return;

		var paths = new StoragePaths(managerRoot);
		var snapshot = await ArmorAuthorityIndexReader.TryReadAsync(paths);
		Assert.NotNull(snapshot);

		var catalog = snapshot.Kits
			.Select(kit => new RuntimeArmorCatalogEntry(
				kit.ItemId,
				kit.ArchiveId,
				kit.DisplayName,
				kit.Kind,
				kit.SetId,
				kit.DlcId))
			.ToArray();

		var baseCatalog = ArmorMappingPickerPlanner.BuildBaseCatalog(catalog);

		Assert.Equal(116, baseCatalog.Sets.Count);
		Assert.Equal(117, baseCatalog.Armors.Count);
		Assert.Equal(118, baseCatalog.Helmets.Count);

		var npc = Assert.Single(baseCatalog.Armors, row => row.IsDemocracyOfficerNpc);
		Assert.Equal(KitNonPlayerArmors.DemocracyOfficerArmorId, npc.PrimaryItemId);
		Assert.DoesNotContain(baseCatalog.Sets, row => row.MemberItemIds.Contains(KitNonPlayerArmors.DemocracyOfficerArmorId));

		var allMembers = baseCatalog.Sets.SelectMany(r => r.MemberItemIds)
			.Concat(baseCatalog.Armors.SelectMany(r => r.MemberItemIds))
			.Concat(baseCatalog.Helmets.SelectMany(r => r.MemberItemIds))
			.ToHashSet();
		foreach (var nonPlayerHelmetId in KitNonPlayerArmors.AllHelmetIds)
			Assert.DoesNotContain(nonPlayerHelmetId, allMembers);
	}

	private static string? TryFindManagerRootWithAssetIndex()
	{
		var envRoot = Environment.GetEnvironmentVariable("HD2_TEST_MANAGER_ROOT");
		if (!string.IsNullOrWhiteSpace(envRoot)
			&& File.Exists(Path.Combine(envRoot, "data", "indexes", "asset-index.sqlite")))
		{
			return envRoot;
		}

		for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
		{
			var sln = Path.Combine(dir.FullName, "WpfApp1.sln");
			if (!File.Exists(sln)) continue;

			var releaseRoot = Path.Combine(dir.FullName, "HD2ModManager", "bin", "Release", "net10.0-windows");
			if (File.Exists(Path.Combine(releaseRoot, "data", "indexes", "asset-index.sqlite")))
				return releaseRoot;

			var debugRoot = Path.Combine(dir.FullName, "HD2ModManager", "bin", "Debug", "net10.0-windows");
			if (File.Exists(Path.Combine(debugRoot, "data", "indexes", "asset-index.sqlite")))
				return debugRoot;
		}

		return null;
	}
}
