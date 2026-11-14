using System.Text.Json;
using HD2ModAdaptation.Analysis;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure.KitInstance;

namespace HD2ModCore.Infrastructure.Sectional;

/// <summary>护甲映射页右侧「可用护甲」列表顶部的三个选项卡。</summary>
public enum ArmorMappingPickerTab
{
	Set = 0,
	Armor = 1,
	Helmet = 2,
}

/// <summary>护甲映射页可选/已映射行规划结果（已过滤非玩家、合并同名版本或配对套装、并完成联动状态计算）。</summary>
public sealed record ArmorMappingPickerRowPlan(
	string SelectionKey,
	ArmorMappingPickerTab Tab,
	uint PrimaryItemId,
	string PrimaryArchiveId,
	string Name,
	string TypeLabel,
	GameDataEquipmentKitKind KitKind,
	IReadOnlyList<uint> ArmorMemberIds,
	IReadOnlyList<uint> HelmetMemberIds,
	IReadOnlyList<uint> MemberItemIds,
	IReadOnlyList<uint> ProviderSourceItemIds,
	int VersionCount,
	bool IsDemocracyOfficerNpc,
	bool IsBlocked,
	string? BlockedReason,
	uint SetId,
	uint DlcId);

/// <summary>右侧「可用护甲」三选项卡完整投影结果。</summary>
public sealed record ArmorMappingPickerProjection(
	IReadOnlyList<ArmorMappingPickerRowPlan> Sets,
	IReadOnlyList<ArmorMappingPickerRowPlan> Armors,
	IReadOnlyList<ArmorMappingPickerRowPlan> Helmets);

/// <summary>未套用动态已管理/多选状态前的三选项卡基础模板（仅依赖目录）。</summary>
public sealed record ArmorMappingPickerBaseCatalog(
	IReadOnlyList<ArmorMappingPickerRowPlan> Sets,
	IReadOnlyList<ArmorMappingPickerRowPlan> Armors,
	IReadOnlyList<ArmorMappingPickerRowPlan> Helmets);

/// <summary>
/// 护甲映射页选品器与合并显示纯规划器（S4b-1）。
/// <para>
/// 负责：
/// 1. 玩家过滤（复用 <see cref="SectionalExclusivePlanner.IsNamed"/> 与 <see cref="KitNonPlayerArmors"/>，民主官 NPC 单独显示）；
/// 2. 同名玩家护甲/头盔去重合并；
/// 3. 三轮 1:1 套装配对（<see cref="OneClickArmorSetTable"/> → 注册表 <c>(DlcId, SetId)</c> → 同 <c>DlcId</c>/全局同名）；
/// 4. 已映射隐藏/置灰、低污染 2 槽素体头盔置灰、跨选项卡多选冲突联动计算；
/// 5. 左侧视觉来源配对与映射目标合并投影。
/// </para>
/// </summary>
public static class ArmorMappingPickerPlanner
{
	public const string TwoSlotHelmetBlockedReason = "头已并进身体，单独映射头盔不可用";
	public const string DemocracyOfficerTag = "民主官 NPC";

	private sealed record PickerGroup(
		string NormalizedNameKey,
		string DisplayName,
		GameDataEquipmentKitKind KitKind,
		IReadOnlyList<RuntimeArmorCatalogEntry> Members,
		bool IsDemocracyOfficerNpc = false,
		bool AllowPairing = true,
		string? CustomSelectionKey = null)
	{
		public RuntimeArmorCatalogEntry Primary => Members[0];
		public IReadOnlyList<uint> MemberIds { get; } = Members.Select(entry => entry.ItemId).ToArray();
	}

	/// <summary>构建静态三选项卡基础分组（只依赖目录本身，可在目录加载/刷新时缓存）。</summary>
	public static ArmorMappingPickerBaseCatalog BuildBaseCatalog(IReadOnlyList<RuntimeArmorCatalogEntry> catalog)
	{
		ArgumentNullException.ThrowIfNull(catalog);

		var armorGroups = new List<PickerGroup>();
		var helmetGroups = new List<PickerGroup>();
		var armorsByName = new Dictionary<string, List<RuntimeArmorCatalogEntry>>(StringComparer.OrdinalIgnoreCase);
		var helmetsByName = new Dictionary<string, List<RuntimeArmorCatalogEntry>>(StringComparer.OrdinalIgnoreCase);

		foreach (var entry in catalog.DistinctBy(item => item.ItemId).OrderBy(item => item.ItemId))
		{
			if (!SectionalExclusivePlanner.IsNamed(entry.DisplayName))
				continue;

			if (entry.KitKind == GameDataEquipmentKitKind.Armor)
			{
				if (entry.ItemId == KitNonPlayerArmors.DemocracyOfficerArmorId)
				{
					armorGroups.Add(new PickerGroup(
						NormalizedNameKey: $"npc:{entry.ItemId}",
						DisplayName: entry.DisplayName.Trim(),
						KitKind: GameDataEquipmentKitKind.Armor,
						Members: [entry],
						IsDemocracyOfficerNpc: true,
						AllowPairing: false,
						CustomSelectionKey: $"armor:npc:{entry.ItemId}"));
					continue;
				}

				if (KitNonPlayerArmors.Contains(entry.ItemId))
					continue;

				var nameKey = NormalizeNameKey(entry.DisplayName);
				if (!armorsByName.TryGetValue(nameKey, out var list))
					armorsByName[nameKey] = list = [];
				list.Add(entry);
			}
			else if (entry.KitKind == GameDataEquipmentKitKind.Helmet)
			{
				if (KitNonPlayerArmors.IsNonPlayerHelmet(entry.ItemId))
					continue;

				var nameKey = NormalizeNameKey(entry.DisplayName);
				if (!helmetsByName.TryGetValue(nameKey, out var list))
					helmetsByName[nameKey] = list = [];
				list.Add(entry);
			}
		}

		foreach (var (nameKey, list) in armorsByName)
			armorGroups.Add(new PickerGroup(nameKey, list[0].DisplayName.Trim(), GameDataEquipmentKitKind.Armor, list));
		foreach (var (nameKey, list) in helmetsByName)
			helmetGroups.Add(new PickerGroup(nameKey, list[0].DisplayName.Trim(), GameDataEquipmentKitKind.Helmet, list));

		var armorRows = armorGroups
			.Select(BuildArmorRow)
			.OrderBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase)
			.ThenBy(row => row.SelectionKey, StringComparer.Ordinal)
			.ToArray();

		var helmetRows = helmetGroups
			.Select(BuildHelmetRow)
			.OrderBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase)
			.ThenBy(row => row.SelectionKey, StringComparer.Ordinal)
			.ToArray();

		var pairableArmors = armorGroups.Where(group => group.AllowPairing).ToList();
		var pairableHelmets = helmetGroups.Where(group => group.AllowPairing).ToList();
		var (pairs, _, _) = PairGroups(pairableArmors, pairableHelmets);
		var setRows = pairs
			.Select(pair => BuildSetRow(pair.Armor, pair.Helmet, keyPrefix: "set:"))
			.OrderBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase)
			.ThenBy(row => row.SelectionKey, StringComparer.Ordinal)
			.ToArray();

		return new ArmorMappingPickerBaseCatalog(setRows, armorRows, helmetRows);
	}

	/// <summary>从原始目录一步计算右侧三选项卡动态投影。</summary>
	public static ArmorMappingPickerProjection EvaluateProjection(
		IReadOnlyList<RuntimeArmorCatalogEntry> catalog,
		IReadOnlyDictionary<uint, string> managedOwnerByItemId,
		IReadOnlySet<string> selectedKeys,
		bool isTwoSlotCompressedSourceOpen)
		=> EvaluateProjection(
			BuildBaseCatalog(catalog),
			managedOwnerByItemId,
			selectedKeys,
			isTwoSlotCompressedSourceOpen);

	/// <summary>在缓存的基础目录分组上重算已管理过滤、2 槽低污染置灰与跨选项卡多选冲突联动。</summary>
	public static ArmorMappingPickerProjection EvaluateProjection(
		ArmorMappingPickerBaseCatalog baseCatalog,
		IReadOnlyDictionary<uint, string> managedOwnerByItemId,
		IReadOnlySet<string> selectedKeys,
		bool isTwoSlotCompressedSourceOpen)
	{
		ArgumentNullException.ThrowIfNull(baseCatalog);
		ArgumentNullException.ThrowIfNull(managedOwnerByItemId);
		ArgumentNullException.ThrowIfNull(selectedKeys);

		var visibleSets = FilterManagedAndTwoSlot(baseCatalog.Sets, managedOwnerByItemId, isTwoSlotCompressedSourceOpen);
		var visibleArmors = FilterManagedAndTwoSlot(baseCatalog.Armors, managedOwnerByItemId, isTwoSlotCompressedSourceOpen);
		var visibleHelmets = FilterManagedAndTwoSlot(baseCatalog.Helmets, managedOwnerByItemId, isTwoSlotCompressedSourceOpen);

		// 收集在「排除已映射与 2 槽约束后仍有效」的跨选项卡已选行；它们的成员并集决定哪些行置灰。
		var activeSelected = visibleSets
			.Concat(visibleArmors)
			.Concat(visibleHelmets)
			.Where(row => !row.IsBlocked && selectedKeys.Contains(row.SelectionKey))
			.Select(row => (Row: row, Members: row.MemberItemIds.ToHashSet()))
			.ToArray();

		return new ArmorMappingPickerProjection(
			ApplySelectionConflicts(visibleSets, activeSelected),
			ApplySelectionConflicts(visibleArmors, activeSelected),
			ApplySelectionConflicts(visibleHelmets, activeSelected));
	}

	/// <summary>
	/// 左侧「映射目标」合并投影：保留未知/非命名/历史记录（不隐藏任何已存在的映射），
	/// 并按与右侧一致的同名去重 + 三轮套装配对口径把同一视觉来源名下的目标合并显示。
	/// </summary>
	public static IReadOnlyList<ArmorMappingPickerRowPlan> BuildTargetGroupPlans(
		IReadOnlyList<RuntimeArmorCatalogEntry> targetEntries)
	{
		ArgumentNullException.ThrowIfNull(targetEntries);
		if (targetEntries.Count == 0) return [];

		var armorsByName = new Dictionary<string, List<RuntimeArmorCatalogEntry>>(StringComparer.OrdinalIgnoreCase);
		var helmetsByName = new Dictionary<string, List<RuntimeArmorCatalogEntry>>(StringComparer.OrdinalIgnoreCase);
		var soloRows = new List<ArmorMappingPickerRowPlan>();

		foreach (var entry in targetEntries.DistinctBy(item => item.ItemId).OrderBy(item => item.ItemId))
		{
			if (!SectionalExclusivePlanner.IsNamed(entry.DisplayName)
				|| entry.KitKind is not (GameDataEquipmentKitKind.Armor or GameDataEquipmentKitKind.Helmet))
			{
				soloRows.Add(BuildTargetSoloRow(entry, isDemocracyOfficerNpc: false));
				continue;
			}

			if (entry.KitKind == GameDataEquipmentKitKind.Armor)
			{
				if (entry.ItemId == KitNonPlayerArmors.DemocracyOfficerArmorId)
				{
					soloRows.Add(BuildTargetSoloRow(entry, isDemocracyOfficerNpc: true));
					continue;
				}
				if (KitNonPlayerArmors.Contains(entry.ItemId))
				{
					soloRows.Add(BuildTargetSoloRow(entry, isDemocracyOfficerNpc: false));
					continue;
				}

				var nameKey = NormalizeNameKey(entry.DisplayName);
				if (!armorsByName.TryGetValue(nameKey, out var list))
					armorsByName[nameKey] = list = [];
				list.Add(entry);
			}
			else
			{
				if (KitNonPlayerArmors.IsNonPlayerHelmet(entry.ItemId))
				{
					soloRows.Add(BuildTargetSoloRow(entry, isDemocracyOfficerNpc: false));
					continue;
				}

				var nameKey = NormalizeNameKey(entry.DisplayName);
				if (!helmetsByName.TryGetValue(nameKey, out var list))
					helmetsByName[nameKey] = list = [];
				list.Add(entry);
			}
		}

		var armorGroups = armorsByName
			.Select(pair => new PickerGroup(pair.Key, pair.Value[0].DisplayName.Trim(), GameDataEquipmentKitKind.Armor, pair.Value))
			.ToList();
		var helmetGroups = helmetsByName
			.Select(pair => new PickerGroup(pair.Key, pair.Value[0].DisplayName.Trim(), GameDataEquipmentKitKind.Helmet, pair.Value))
			.ToList();

		var (pairs, unpairedArmors, unpairedHelmets) = PairGroups(armorGroups, helmetGroups);

		var result = new List<ArmorMappingPickerRowPlan>(pairs.Count + unpairedArmors.Count + unpairedHelmets.Count + soloRows.Count);
		result.AddRange(pairs.Select(pair => BuildSetRow(pair.Armor, pair.Helmet, keyPrefix: "detail:set:")));
		result.AddRange(unpairedArmors.Select(group => BuildArmorRow(group, keyPrefix: "detail:armor:")));
		result.AddRange(unpairedHelmets.Select(group => BuildHelmetRow(group, keyPrefix: "detail:helmet:")));
		result.AddRange(soloRows);

		return result
			.OrderBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase)
			.ThenBy(row => row.SelectionKey, StringComparer.Ordinal)
			.ToArray();
	}

	/// <summary>
	/// 左侧视觉来源列表配对：在保留单个记录或套装的 1 甲 + 1 盔配对语义下，
	/// 优先用 <see cref="OneClickArmorSetTable"/>，其余沿用注册表 <c>(DlcId, SetId)</c> 与同 <c>DlcId</c>/同名兜底。
	/// 同一边有歧义（同键多于 1 件护甲或头盔）时保持平铺不合并，避免丢失逐件控制。
	/// </summary>
	public static IReadOnlyList<(RuntimeArmorCatalogEntry Armor, RuntimeArmorCatalogEntry Helmet)> PairProviderSources(
		IReadOnlyList<RuntimeArmorCatalogEntry> entries)
	{
		ArgumentNullException.ThrowIfNull(entries);
		var distinct = entries.DistinctBy(item => item.ItemId).ToArray();
		var armorGroups = distinct
			.Where(item => item.KitKind == GameDataEquipmentKitKind.Armor)
			.Select(item => new PickerGroup($"p:{item.ItemId}", item.DisplayName.Trim(), GameDataEquipmentKitKind.Armor, [item]))
			.ToList();
		var helmetGroups = distinct
			.Where(item => item.KitKind == GameDataEquipmentKitKind.Helmet)
			.Select(item => new PickerGroup($"p:{item.ItemId}", item.DisplayName.Trim(), GameDataEquipmentKitKind.Helmet, [item]))
			.ToList();

		var (pairs, _, _) = PairGroups(armorGroups, helmetGroups);
		return pairs.Select(pair => (pair.Armor.Primary, pair.Helmet.Primary)).ToArray();
	}

	/// <summary>判定自定义素体是否为低污染模式下的 2 槽压缩来源（头已并进身体）。</summary>
	public static bool IsTwoSlotCompressedCustomModel(
		string modsDirectory,
		string? modelKey,
		bool lowPollutionMode)
	{
		if (!lowPollutionMode
			|| string.IsNullOrWhiteSpace(modsDirectory)
			|| string.IsNullOrWhiteSpace(modelKey))
		{
			return false;
		}

		var catalog = CustomModelCatalog.Collect(modsDirectory);
		return catalog.Entries.TryGetValue(modelKey, out var entry)
			&& IsTwoSlotCompressedModDirectory(entry.ModDirectory, lowPollutionMode);
	}

	/// <summary>判定指定 Mod 目录在当前低污染开关下是否为 2 槽压缩素体（读布局文件，未编译时兜底读 <c>wardrobe.json</c>）。</summary>
	public static bool IsTwoSlotCompressedModDirectory(
		string modDirectory,
		bool lowPollutionMode)
	{
		if (!lowPollutionMode || string.IsNullOrWhiteSpace(modDirectory) || !Directory.Exists(modDirectory))
			return false;

		if (LowPollutionLayoutReader.TryRead(modDirectory, out var layout, out _) && layout is not null)
			return layout.HeadMerge || layout.UnitOf(LowPollutionRoles.HeadBody) is not null;

		var overwrite = ModNodePayloadLayout.GetOverwriteDirectory(modDirectory);
		if (File.Exists(Path.Combine(overwrite, LowPollutionFreshness.UnsupportedMarkerFileName)))
			return false;

		var wardrobePath = VpkModWardrobeService.GetStatePath(modDirectory);
		if (!File.Exists(wardrobePath)) return false;
		try
		{
			using var document = JsonDocument.Parse(File.ReadAllText(wardrobePath));
			return document.RootElement.TryGetProperty("headMergeIntoBody", out var element)
				&& element.ValueKind == JsonValueKind.True;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
		{
			return false;
		}
	}

	private static string NormalizeNameKey(string displayName)
		=> displayName.Trim().ToUpperInvariant();

	private static ArmorMappingPickerRowPlan BuildArmorRow(PickerGroup group)
		=> BuildArmorRow(group, keyPrefix: "armor:");

	private static ArmorMappingPickerRowPlan BuildArmorRow(PickerGroup group, string keyPrefix)
	{
		var selectionKey = group.CustomSelectionKey is { Length: > 0 } custom && keyPrefix == "armor:"
			? custom
			: keyPrefix + group.NormalizedNameKey;
		return new ArmorMappingPickerRowPlan(
			SelectionKey: selectionKey,
			Tab: ArmorMappingPickerTab.Armor,
			PrimaryItemId: group.Primary.ItemId,
			PrimaryArchiveId: group.Primary.ArchiveId,
			Name: group.DisplayName,
			TypeLabel: "护甲",
			KitKind: GameDataEquipmentKitKind.Armor,
			ArmorMemberIds: group.MemberIds,
			HelmetMemberIds: [],
			MemberItemIds: group.MemberIds,
			ProviderSourceItemIds: [group.Primary.ItemId],
			VersionCount: group.Members.Count,
			IsDemocracyOfficerNpc: group.IsDemocracyOfficerNpc,
			IsBlocked: false,
			BlockedReason: null,
			SetId: group.Primary.SetId,
			DlcId: group.Primary.DlcId);
	}

	private static ArmorMappingPickerRowPlan BuildHelmetRow(PickerGroup group)
		=> BuildHelmetRow(group, keyPrefix: "helmet:");

	private static ArmorMappingPickerRowPlan BuildHelmetRow(PickerGroup group, string keyPrefix)
		=> new(
			SelectionKey: keyPrefix + group.NormalizedNameKey,
			Tab: ArmorMappingPickerTab.Helmet,
			PrimaryItemId: group.Primary.ItemId,
			PrimaryArchiveId: group.Primary.ArchiveId,
			Name: group.DisplayName,
			TypeLabel: "头盔",
			KitKind: GameDataEquipmentKitKind.Helmet,
			ArmorMemberIds: [],
			HelmetMemberIds: group.MemberIds,
			MemberItemIds: group.MemberIds,
			ProviderSourceItemIds: [group.Primary.ItemId],
			VersionCount: group.Members.Count,
			IsDemocracyOfficerNpc: false,
			IsBlocked: false,
			BlockedReason: null,
			SetId: group.Primary.SetId,
			DlcId: group.Primary.DlcId);

	private static ArmorMappingPickerRowPlan BuildSetRow(
		PickerGroup armor,
		PickerGroup helmet,
		string keyPrefix)
	{
		var name = string.Equals(armor.DisplayName, helmet.DisplayName, StringComparison.OrdinalIgnoreCase)
			? armor.DisplayName
			: $"{armor.DisplayName} + {helmet.DisplayName}";
		var memberIds = new uint[armor.MemberIds.Count + helmet.MemberIds.Count];
		var offset = 0;
		foreach (var id in armor.MemberIds) memberIds[offset++] = id;
		foreach (var id in helmet.MemberIds) memberIds[offset++] = id;

		return new ArmorMappingPickerRowPlan(
			SelectionKey: $"{keyPrefix}{armor.NormalizedNameKey}+{helmet.NormalizedNameKey}",
			Tab: ArmorMappingPickerTab.Set,
			PrimaryItemId: armor.Primary.ItemId,
			PrimaryArchiveId: armor.Primary.ArchiveId,
			Name: name,
			TypeLabel: "头盔+护甲",
			KitKind: GameDataEquipmentKitKind.Unknown,
			ArmorMemberIds: armor.MemberIds,
			HelmetMemberIds: helmet.MemberIds,
			MemberItemIds: memberIds,
			ProviderSourceItemIds: [armor.Primary.ItemId, helmet.Primary.ItemId],
			VersionCount: Math.Max(armor.MemberIds.Count, helmet.MemberIds.Count),
			IsDemocracyOfficerNpc: false,
			IsBlocked: false,
			BlockedReason: null,
			SetId: armor.Primary.SetId,
			DlcId: armor.Primary.DlcId);
	}

	private static ArmorMappingPickerRowPlan BuildTargetSoloRow(
		RuntimeArmorCatalogEntry entry,
		bool isDemocracyOfficerNpc)
	{
		var isHelmet = entry.KitKind == GameDataEquipmentKitKind.Helmet;
		return new ArmorMappingPickerRowPlan(
			SelectionKey: $"detail:raw:{entry.ItemId}",
			Tab: isHelmet ? ArmorMappingPickerTab.Helmet : ArmorMappingPickerTab.Armor,
			PrimaryItemId: entry.ItemId,
			PrimaryArchiveId: entry.ArchiveId,
			Name: entry.DisplayName.Trim(),
			TypeLabel: isHelmet ? "头盔" : "护甲",
			KitKind: entry.KitKind,
			ArmorMemberIds: isHelmet ? [] : [entry.ItemId],
			HelmetMemberIds: isHelmet ? [entry.ItemId] : [],
			MemberItemIds: [entry.ItemId],
			ProviderSourceItemIds: [entry.ItemId],
			VersionCount: 1,
			IsDemocracyOfficerNpc: isDemocracyOfficerNpc,
			IsBlocked: false,
			BlockedReason: null,
			SetId: entry.SetId,
			DlcId: entry.DlcId);
	}

	private static (
		List<(PickerGroup Armor, PickerGroup Helmet)> Pairs,
		List<PickerGroup> RemainingArmors,
		List<PickerGroup> RemainingHelmets)
		PairGroups(List<PickerGroup> armorGroups, List<PickerGroup> helmetGroups)
	{
		var remainingArmors = new List<PickerGroup>(armorGroups);
		var remainingHelmets = new List<PickerGroup>(helmetGroups);
		var pairs = new List<(PickerGroup Armor, PickerGroup Helmet)>();

		// 第 1 轮：OneClickArmorSet 权威表（最精确：覆盖异名套装与变体共享头盔）
		PairByKeys(remainingArmors, remainingHelmets, pairs, group =>
		{
			var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (var id in group.MemberIds)
			{
				if (OneClickArmorSetTable.TryGetCanonicalSetName(id, out var setName))
					keys.Add(setName);
			}
			return keys;
		});

		// 第 2 轮：游戏注册表的 (DlcId, SetId)（用于表中尚未收录的新护甲）
		PairByKeys(remainingArmors, remainingHelmets, pairs, group =>
		{
			var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (var member in group.Members)
			{
				if (member.SetId != 0)
					keys.Add($"{member.DlcId}:{member.SetId}");
			}
			return keys;
		});

		// 第 3a 轮：同 DlcId 下同名（处理注册表里盔/甲 set_id 不同的老套，如 DS-42“联邦之刃”）
		PairByKeys(remainingArmors, remainingHelmets, pairs, group =>
		{
			var nameKey = NormalizeNameKey(group.DisplayName);
			var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (var member in group.Members)
				keys.Add($"{member.DlcId}:{nameKey}");
			return keys;
		});

		// 第 3b 轮：全局同名兜底
		PairByKeys(remainingArmors, remainingHelmets, pairs, group => [NormalizeNameKey(group.DisplayName)]);

		return (pairs, remainingArmors, remainingHelmets);
	}

	private static void PairByKeys(
		List<PickerGroup> remainingArmors,
		List<PickerGroup> remainingHelmets,
		List<(PickerGroup Armor, PickerGroup Helmet)> pairs,
		Func<PickerGroup, IEnumerable<string>> keySelector)
	{
		if (remainingArmors.Count == 0 || remainingHelmets.Count == 0) return;

		var armorByKey = new Dictionary<string, List<PickerGroup>>(StringComparer.OrdinalIgnoreCase);
		foreach (var armor in remainingArmors)
		{
			foreach (var key in keySelector(armor))
			{
				if (!armorByKey.TryGetValue(key, out var list))
					armorByKey[key] = list = [];
				list.Add(armor);
			}
		}

		var helmetByKey = new Dictionary<string, List<PickerGroup>>(StringComparer.OrdinalIgnoreCase);
		foreach (var helmet in remainingHelmets)
		{
			foreach (var key in keySelector(helmet))
			{
				if (!helmetByKey.TryGetValue(key, out var list))
					helmetByKey[key] = list = [];
				list.Add(helmet);
			}
		}

		var matchedArmors = new HashSet<PickerGroup>();
		var matchedHelmets = new HashSet<PickerGroup>();
		foreach (var (key, armors) in armorByKey)
		{
			if (armors.Count != 1) continue;
			var armor = armors[0];
			if (matchedArmors.Contains(armor)) continue;
			if (!helmetByKey.TryGetValue(key, out var helmets) || helmets.Count != 1) continue;
			var helmet = helmets[0];
			if (matchedHelmets.Contains(helmet)) continue;

			pairs.Add((armor, helmet));
			matchedArmors.Add(armor);
			matchedHelmets.Add(helmet);
		}

		if (matchedArmors.Count == 0) return;
		remainingArmors.RemoveAll(matchedArmors.Contains);
		remainingHelmets.RemoveAll(matchedHelmets.Contains);
	}

	private static IReadOnlyList<ArmorMappingPickerRowPlan> FilterManagedAndTwoSlot(
		IReadOnlyList<ArmorMappingPickerRowPlan> rows,
		IReadOnlyDictionary<uint, string> managedOwnerByItemId,
		bool isTwoSlotCompressedSourceOpen)
	{
		var result = new List<ArmorMappingPickerRowPlan>(rows.Count);
		foreach (var row in rows)
		{
			var evaluated = EvaluateManagedAndTwoSlot(row, managedOwnerByItemId, isTwoSlotCompressedSourceOpen);
			if (evaluated is not null)
				result.Add(evaluated);
		}
		return result;
	}

	private static ArmorMappingPickerRowPlan? EvaluateManagedAndTwoSlot(
		ArmorMappingPickerRowPlan row,
		IReadOnlyDictionary<uint, string> managedOwnerByItemId,
		bool isTwoSlotCompressedSourceOpen)
	{
		var isBlocked = false;
		string? blockedReason = null;

		if (row.Tab == ArmorMappingPickerTab.Set)
		{
			var managedArmors = row.ArmorMemberIds.Where(managedOwnerByItemId.ContainsKey).ToArray();
			var managedHelmets = row.HelmetMemberIds.Where(managedOwnerByItemId.ContainsKey).ToArray();
			var allArmorsManaged = row.ArmorMemberIds.Count > 0 && managedArmors.Length == row.ArmorMemberIds.Count;
			var allHelmetsManaged = row.HelmetMemberIds.Count > 0 && managedHelmets.Length == row.HelmetMemberIds.Count;

			if (allArmorsManaged && allHelmetsManaged)
				return null;

			if (managedArmors.Length > 0 && managedHelmets.Length > 0)
			{
				isBlocked = true;
				blockedReason = $"护甲{managedOwnerByItemId[managedArmors[0]]}；头盔{managedOwnerByItemId[managedHelmets[0]]}";
			}
			else if (managedArmors.Length > 0)
			{
				isBlocked = true;
				blockedReason = $"护甲{managedOwnerByItemId[managedArmors[0]]}";
			}
			else if (managedHelmets.Length > 0)
			{
				isBlocked = true;
				blockedReason = $"头盔{managedOwnerByItemId[managedHelmets[0]]}";
			}
		}
		else
		{
			var managedIds = row.MemberItemIds.Where(managedOwnerByItemId.ContainsKey).ToArray();
			if (managedIds.Length == row.MemberItemIds.Count && row.MemberItemIds.Count > 0)
				return null;

			if (managedIds.Length > 0)
			{
				isBlocked = true;
				blockedReason = managedOwnerByItemId[managedIds[0]];
			}
		}

		if (isTwoSlotCompressedSourceOpen && row.Tab == ArmorMappingPickerTab.Helmet)
		{
			isBlocked = true;
			blockedReason ??= TwoSlotHelmetBlockedReason;
		}

		return isBlocked ? row with { IsBlocked = true, BlockedReason = blockedReason } : row;
	}

	private static IReadOnlyList<ArmorMappingPickerRowPlan> ApplySelectionConflicts(
		IReadOnlyList<ArmorMappingPickerRowPlan> rows,
		IReadOnlyList<(ArmorMappingPickerRowPlan Row, HashSet<uint> Members)> activeSelected)
	{
		if (activeSelected.Count == 0) return rows;

		var result = new ArmorMappingPickerRowPlan[rows.Count];
		for (var i = 0; i < rows.Count; i++)
		{
			var row = rows[i];
			if (row.IsBlocked)
			{
				result[i] = row;
				continue;
			}

			var isSelfSelected = false;
			List<ArmorMappingPickerRowPlan>? conflicts = null;
			foreach (var (selectedRow, selectedMembers) in activeSelected)
			{
				if (string.Equals(selectedRow.SelectionKey, row.SelectionKey, StringComparison.OrdinalIgnoreCase))
				{
					isSelfSelected = true;
					break;
				}
				if (row.MemberItemIds.Any(selectedMembers.Contains))
				{
					conflicts ??= [];
					conflicts.Add(selectedRow);
				}
			}

			if (isSelfSelected || conflicts is null)
			{
				result[i] = row;
				continue;
			}

			result[i] = row with
			{
				IsBlocked = true,
				BlockedReason = FormatConflictReason(row, conflicts),
			};
		}

		return result;
	}

	private static string FormatConflictReason(
		ArmorMappingPickerRowPlan targetRow,
		IReadOnlyList<ArmorMappingPickerRowPlan> conflicts)
	{
		var first = conflicts[0];
		if (first.Tab == ArmorMappingPickerTab.Set)
			return $"已包含在所选套装 {first.Name} 里";

		if (targetRow.Tab == ArmorMappingPickerTab.Set)
		{
			var hasArmor = conflicts.Any(item => item.Tab == ArmorMappingPickerTab.Armor);
			var hasHelmet = conflicts.Any(item => item.Tab == ArmorMappingPickerTab.Helmet);
			if (hasArmor && hasHelmet)
				return "所选护甲和头盔属于这个套装";
			if (first.Tab == ArmorMappingPickerTab.Armor)
				return $"所选护甲 {first.Name} 属于这个套装";
			if (first.Tab == ArmorMappingPickerTab.Helmet)
				return $"所选头盔 {first.Name} 属于这个套装";
		}

		return $"与所选 {first.Name} 冲突";
	}
}
