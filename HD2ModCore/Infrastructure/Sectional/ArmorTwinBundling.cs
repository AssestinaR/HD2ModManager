using System.Globalization;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure.KitInstance;

namespace HD2ModCore.Infrastructure.Sectional;

/// <summary>一个换色孪生组（同构非披风部件布局的多个正式名玩家护甲或头盔）。</summary>
public sealed record ArmorTwinGroup(
	GameDataEquipmentKitKind KitKind,
	IReadOnlyList<string> DisplayNames,
	IReadOnlyList<uint> MemberItemIds);

/// <summary>基于权威索引离线/启动时算出的护甲与头盔孪生组索引。</summary>
public sealed class ArmorTwinIndex
{
	public static ArmorTwinIndex Empty { get; } = new([], []);

	public ArmorTwinIndex(
		IReadOnlyList<ArmorTwinGroup> armorGroups,
		IReadOnlyList<ArmorTwinGroup> helmetGroups)
	{
		ArmorGroups = armorGroups;
		HelmetGroups = helmetGroups;
		var byItem = new Dictionary<uint, ArmorTwinGroup>();
		foreach (var group in armorGroups.Concat(helmetGroups))
		{
			foreach (var id in group.MemberItemIds)
				byItem[id] = group;
		}
		GroupByItemId = byItem;
	}

	public IReadOnlyList<ArmorTwinGroup> ArmorGroups { get; }
	public IReadOnlyList<ArmorTwinGroup> HelmetGroups { get; }
	public IReadOnlyDictionary<uint, ArmorTwinGroup> GroupByItemId { get; }
}

/// <summary>
/// 护甲映射页「换色孪生捆绑」纯逻辑（判定孪生组、集中开关、老数据部分映射自动补齐）。
/// </summary>
public static class ArmorTwinBundling
{
	public const string TwinBundleReason = "只是颜色不同，游戏里分不开，只能一起换";

	/// <summary>
	/// 集中控制是否启用孪生捆绑。将来做好私有零件分离方案后，置为 <c>false</c> 即可恢复单独映射。
	/// </summary>
	public static bool TwinBundlingEnabled { get; set; } = true;

	/// <summary>
	/// 从权威装备表计算护甲与头盔孪生组：
	/// 1. 仅限有正式名字的玩家记录（排除 <c>0x…</c> 未命名、拿不到的非玩家记录、以及单列的民主官 NPC）。
	/// 2. 比较非披风（<c>slot != 1</c>）unit 布局：<c>(UnitFileId, Slot, PieceType, RawBodyType)</c> 完全一致。
	/// 3. 按正式名先合并版本，再把共享任一同构布局的同名组并入同一孪生组（处理如 B-01 多版本布局）。
	/// </summary>
	public static ArmorTwinIndex BuildTwinIndex(IReadOnlyList<GameDataAuthorityKit> kits)
	{
		ArgumentNullException.ThrowIfNull(kits);
		if (kits.Count == 0) return ArmorTwinIndex.Empty;

		var capeSlot = ArmorStructure.SlotOf(ArmorPart.Cape) ?? 1u;
		var armorGroups = ComputeGroupsForKind(kits, GameDataEquipmentKitKind.Armor, capeSlot);
		var helmetGroups = ComputeGroupsForKind(kits, GameDataEquipmentKitKind.Helmet, capeSlot);
		return new ArmorTwinIndex(armorGroups, helmetGroups);
	}

	/// <summary>
	/// 老数据部分映射自动补齐：若某个孪生组在已有映射里只映射了部分成员，
	/// 自动把整组其余成员补齐（或统一对齐）到出现次数最多的视觉来源。
	/// </summary>
	public static (IReadOnlyList<RuntimeArmorRegistryMapping> Mappings, int AddedCount, int RealignedCount) CompletePartialTwinMappings(
		IReadOnlyList<RuntimeArmorRegistryMapping> mappings,
		ArmorTwinIndex? twinIndex,
		bool? twinBundlingEnabled = null)
	{
		ArgumentNullException.ThrowIfNull(mappings);
		if (!(twinBundlingEnabled ?? TwinBundlingEnabled)
			|| twinIndex is null
			|| mappings.Count == 0
			|| (twinIndex.ArmorGroups.Count == 0 && twinIndex.HelmetGroups.Count == 0))
		{
			return (mappings, 0, 0);
		}

		var byTarget = new Dictionary<uint, RuntimeArmorRegistryMapping>(mappings.Count);
		var order = new List<uint>(mappings.Count);
		foreach (var mapping in mappings)
		{
			if (!byTarget.ContainsKey(mapping.TargetItemId))
				order.Add(mapping.TargetItemId);
			byTarget[mapping.TargetItemId] = mapping;
		}

		var addedCount = 0;
		var realignedCount = 0;

		foreach (var group in twinIndex.ArmorGroups.Concat(twinIndex.HelmetGroups))
		{
			var existing = new List<RuntimeArmorRegistryMapping>();
			foreach (var memberId in group.MemberItemIds)
			{
				if (byTarget.TryGetValue(memberId, out var found))
					existing.Add(found);
			}
			if (existing.Count == 0) continue;

			var primarySource = existing
				.GroupBy(item => (item.SourceItemId, item.ModelKey ?? string.Empty))
				.OrderByDescending(g => g.Count())
				.ThenBy(g => g.Min(item => item.TargetItemId))
				.First()
				.Key;

			var primaryModelKey = string.IsNullOrEmpty(primarySource.Item2) ? null : primarySource.Item2;
			var allMatch = existing.Count == group.MemberItemIds.Count
				&& existing.All(item => item.SourceItemId == primarySource.SourceItemId
					&& string.Equals(item.ModelKey ?? string.Empty, primarySource.Item2, StringComparison.Ordinal));
			if (allMatch) continue;

			foreach (var memberId in group.MemberItemIds)
			{
				if (!byTarget.TryGetValue(memberId, out var current))
				{
					byTarget[memberId] = new RuntimeArmorRegistryMapping(memberId, primarySource.SourceItemId, primaryModelKey);
					order.Add(memberId);
					addedCount++;
				}
				else if (current.SourceItemId != primarySource.SourceItemId
					|| !string.Equals(current.ModelKey ?? string.Empty, primarySource.Item2, StringComparison.Ordinal))
				{
					byTarget[memberId] = new RuntimeArmorRegistryMapping(memberId, primarySource.SourceItemId, primaryModelKey);
					realignedCount++;
				}
			}
		}

		if (addedCount == 0 && realignedCount == 0)
			return (mappings, 0, 0);

		var completed = order.Select(id => byTarget[id]).ToArray();
		return (completed, addedCount, realignedCount);
	}

	private static IReadOnlyList<ArmorTwinGroup> ComputeGroupsForKind(
		IReadOnlyList<GameDataAuthorityKit> kits,
		GameDataEquipmentKitKind kind,
		uint capeSlot)
	{
		var byName = new Dictionary<string, (string DisplayName, List<uint> MemberIds, HashSet<string> Layouts)>(
			StringComparer.OrdinalIgnoreCase);

		foreach (var kit in kits.DistinctBy(item => item.ItemId).OrderBy(item => item.ItemId))
		{
			if (kit.Kind != kind || !SectionalExclusivePlanner.IsNamed(kit.DisplayName))
				continue;

			if (kind == GameDataEquipmentKitKind.Armor)
			{
				if (kit.ItemId == KitNonPlayerArmors.DemocracyOfficerArmorId
					|| KitNonPlayerArmors.Contains(kit.ItemId))
				{
					continue;
				}
			}
			else if (kind == GameDataEquipmentKitKind.Helmet)
			{
				if (KitNonPlayerArmors.IsNonPlayerHelmet(kit.ItemId))
					continue;
			}

			var layoutSignature = ComputeNonCapeLayoutSignature(kit.Pieces, capeSlot);
			if (string.IsNullOrEmpty(layoutSignature))
				continue;

			var displayName = kit.DisplayName.Trim();
			var nameKey = displayName.ToUpperInvariant();
			if (!byName.TryGetValue(nameKey, out var bucket))
				byName[nameKey] = bucket = (displayName, [], []);
			bucket.MemberIds.Add(kit.ItemId);
			bucket.Layouts.Add(layoutSignature);
		}

		if (byName.Count < 2) return [];

		var nameKeys = byName.Keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase).ToArray();
		var indexByNameKey = new Dictionary<string, int>(nameKeys.Length, StringComparer.OrdinalIgnoreCase);
		for (var i = 0; i < nameKeys.Length; i++)
			indexByNameKey[nameKeys[i]] = i;

		var parent = new int[nameKeys.Length];
		for (var i = 0; i < parent.Length; i++) parent[i] = i;

		int Find(int x)
		{
			while (parent[x] != x)
			{
				parent[x] = parent[parent[x]];
				x = parent[x];
			}
			return x;
		}

		void Union(int a, int b)
		{
			var ra = Find(a);
			var rb = Find(b);
			if (ra != rb) parent[rb] = ra;
		}

		var firstOwnerByLayout = new Dictionary<string, int>(StringComparer.Ordinal);
		for (var i = 0; i < nameKeys.Length; i++)
		{
			foreach (var layout in byName[nameKeys[i]].Layouts)
			{
				if (firstOwnerByLayout.TryGetValue(layout, out var firstIndex))
					Union(firstIndex, i);
				else
					firstOwnerByLayout[layout] = i;
			}
		}

		var components = new Dictionary<int, List<string>>();
		for (var i = 0; i < nameKeys.Length; i++)
		{
			var root = Find(i);
			if (!components.TryGetValue(root, out var list))
				components[root] = list = [];
			list.Add(nameKeys[i]);
		}

		var groups = new List<ArmorTwinGroup>();
		foreach (var componentKeys in components.Values)
		{
			if (componentKeys.Count < 2) continue;

			var names = componentKeys
				.Select(key => byName[key].DisplayName)
				.OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
				.ThenBy(name => name, StringComparer.Ordinal)
				.ToArray();
			var memberIds = componentKeys
				.SelectMany(key => byName[key].MemberIds)
				.Distinct()
				.OrderBy(id => id)
				.ToArray();
			groups.Add(new ArmorTwinGroup(kind, names, memberIds));
		}

		return groups
			.OrderBy(group => group.DisplayNames[0], StringComparer.CurrentCultureIgnoreCase)
			.ThenBy(group => group.MemberItemIds[0])
			.ToArray();
	}

	private static string ComputeNonCapeLayoutSignature(
		IReadOnlyList<GameDataAuthorityPiece> pieces,
		uint capeSlot)
	{
		if (pieces.Count == 0) return string.Empty;
		var nonCape = pieces
			.Where(piece => piece.Slot != capeSlot)
			.Select(piece => (
				FileId: piece.UnitAssetKey.FileId,
				piece.Slot,
				piece.PieceType,
				piece.RawBodyType))
			.OrderBy(item => item.FileId)
			.ThenBy(item => item.Slot)
			.ThenBy(item => item.PieceType)
			.ThenBy(item => item.RawBodyType)
			.ToArray();
		if (nonCape.Length == 0) return string.Empty;

		return string.Join(";", nonCape.Select(item => string.Create(
			CultureInfo.InvariantCulture,
			$"{item.FileId:x16}:{item.Slot}:{item.PieceType}:{item.RawBodyType}")));
	}
}
