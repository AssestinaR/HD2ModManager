using System.Buffers.Binary;
using System.Globalization;
using HD2GameItemAssets;
using HD2ModAdaptation.Analysis;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure.KitInstance;

/// <summary>
/// NPC 的伪护甲记录。飞船 NPC 没有护甲记录，替换 Mod 的做法是直接覆盖 NPC 的<b>根 unit</b>
/// （原版是个约 49KB、没有几何数据的空壳，Mod 往里填整身模型）。为了能像护甲一样出现在映射页、被映射，
/// 给每个 NPC 造一条只存在于管理器里的记录：唯一的部件 = 根 unit（躯干槽，两种体型共用）；
/// 映射 = 把根 unit 的链接指向素体的躯干（头身合并）unit，并把 NPC 的原版脸按实例隐藏。
/// <para>
/// 依据（2026-11-16）：根 unit 来自两个替换 Mod 的补丁（「守岸人 替换 女舰长」覆盖 d87d…，「2B Technician Replacer」覆盖 93fb8680…），
/// 脸来自实机 Hider 0.4.5 探针（站在 NPC 旁边，离玩家最近的候选头部）。
/// 飞船 NPC 沿用只补骨架引用的做法。SEAF 士兵另用原版根 unit 重建人物网格，保留它的骨架、挂点和 LOD 路由。
/// </para>
/// </summary>
public static class KitNpcRecords
{
	public const uint FemaleCaptainItemId = 0xFEED0001u;
	public const uint TechnicianItemId = 0xFEED0002u;
	public const uint SeafSoldierItemId = 0xFEED0003u;

	/// <summary>NPC 根 unit 所在的归档（两个根 unit 都在这个归档里）。</summary>
	public const string ArchiveId = "fc5b6bff0db90aab";

	/// <summary>
	/// 飞船 NPC 共用的骨架资源（bones）：女舰长、技术员的根 unit 头部 <c>0x08</c> 处都指向它（69 根骨，命名和玩家骨架同一套）。
	/// 玩家护甲的压缩 unit 头部这里是 0（没有骨架，靠玩家身上的 90 根骨驱动）；
	/// 直接拿它去顶 NPC 根 unit，NPC 的动画驱动不了它，模型停在绑定姿势（T 字）。
	/// 「守岸人 替换 女舰长」的做法是保留原 unit 的头部（骨架引用），只换网格——这里照着做：给压缩 unit 的副本补上骨架引用。
	/// </summary>
	public const ulong NpcBonesResourceId = 0xb5c27e4a810df382UL;

	/// <param name="Name">不含标注的名字。</param>
	/// <param name="Tag">选品器里挨在名字后面的标注，如「NPC」。</param>
	/// <param name="Header20">原版根 unit 头部 <c>0x20</c> 处的 u64（女舰长是她自己的 unit ID，技术员是骨架 ID），副本里原样写回。</param>
	public sealed record NpcRecord(
		uint ItemId, string Name, string Tag, ulong RootUnitId, ulong FaceUnitId, ulong Header20,
		string RootArchiveId = ArchiveId, ulong BonesResourceId = NpcBonesResourceId)
	{
		public string DisplayName => $"{Name}（{Tag}）";
		public bool RequiresNativeShell => ItemId == SeafSoldierItemId;
		public IReadOnlyList<ulong> ExtraHideUnits => FaceUnitId == 0 ? [] : [FaceUnitId];

		/// <summary>layout 文件里这个 NPC 专用副本的角色名。</summary>
		public string VariantRole => "npc-" + ItemId.ToString(CultureInfo.InvariantCulture);

		/// <summary>SEAF 保留原键供引擎查找同名配套资源。飞船 NPC 的副本 ID 由来源 unit 派生。</summary>
		public ulong VariantUnitId(string sourceUnitHex)
			=> RequiresNativeShell
				? RootUnitId
				: StingrayHash.Murmur64A($"hd2modmanager/npc/{ItemId.ToString(CultureInfo.InvariantCulture)}/{sourceUnitHex.ToLowerInvariant()}");
	}

	/// <summary>
	/// 给压缩 unit 的 TocData 副本补上 NPC 的头部：<c>0x08</c> = 骨架资源，<c>0x10</c> = 0（「守岸人」Mod 也清了这个字段），
	/// <c>0x20</c> = 原版根 unit 的值。其余字节（网格、材质、骨骼名哈希）一律不动。
	/// </summary>
	public static byte[] PatchHeader(NpcRecord npc, byte[] tocData)
	{
		ArgumentNullException.ThrowIfNull(npc);
		ArgumentNullException.ThrowIfNull(tocData);
		if (tocData.Length < 0x28) throw new InvalidDataException("unit TocData 太短，没有头部。");
		var copy = (byte[])tocData.Clone();
		BinaryPrimitives.WriteUInt64LittleEndian(copy.AsSpan(0x08, 8), npc.BonesResourceId);
		BinaryPrimitives.WriteUInt64LittleEndian(copy.AsSpan(0x10, 8), 0);
		BinaryPrimitives.WriteUInt64LittleEndian(copy.AsSpan(0x20, 8), npc.Header20);
		return copy;
	}

	public static IReadOnlyList<NpcRecord> All { get; } =
	[
		new(FemaleCaptainItemId, "女舰长", "飞船女军官 NPC", 0xd87d04aacbc0269cUL, 0x85d4ba84db7d27e5UL, 0xd87d04aacbc0269cUL),
		new(TechnicianItemId, "维修技术员", "NPC", 0x93fb8680b7ff7f96UL, 0x4d4017549585cb8eUL, 0xb5c27e4a810df382UL),
		new(SeafSoldierItemId, "SEAF 士兵", "NPC", 0x75f7e96af7dcd303UL, 0, 0x75f7e96af7dcd303UL,
			"792b2a2ba11ae85d", 0x7a3637cd33a54e4cUL),
	];

	public static bool IsNpc(uint itemId) => All.Any(record => record.ItemId == itemId);

	public static bool TryGet(uint itemId, out NpcRecord record)
	{
		foreach (var candidate in All)
		{
			if (candidate.ItemId != itemId) continue;
			record = candidate;
			return true;
		}

		record = null!;
		return false;
	}

	/// <summary>伪记录的件表：唯一的一件 = 根 unit，躯干槽、两种体型共用。</summary>
	public static GameDataAuthorityKit ToKit(NpcRecord record)
	{
		var piece = new GameDataAuthorityPiece(
			RawBodyType: 3u,
			BodyVariant: UnitMeshBodyVariant.Any,
			Slot: ArmorStructure.SlotOf(ArmorPart.Torso) ?? 0u,
			PartKind: UnitMeshPartKind.Torso,
			PieceType: 0u,
			Weight: 0u,
			ToneVariations: 0,
			UnitAssetKey: new AssetKey(0xe0a48d0be9a7453fUL, record.RootUnitId),
			Resources: []);
		return new GameDataAuthorityKit(record.ItemId, 0u, 0u, GameDataEquipmentKitKind.Armor, record.RootArchiveId, record.DisplayName, [piece]);
	}

	/// <summary>把 NPC 伪记录追加进（真实的）装备权威快照；已经有同 ID 的就不重复加。</summary>
	public static IReadOnlyList<GameDataAuthorityKit> AppendTo(IReadOnlyList<GameDataAuthorityKit> kits)
	{
		var known = kits.Select(kit => kit.ItemId).ToHashSet();
		var extra = All.Where(record => !known.Contains(record.ItemId)).Select(ToKit).ToArray();
		return extra.Length == 0 ? kits : [.. kits, .. extra];
	}

	public static IReadOnlyList<RuntimeArmorCatalogEntry> AppendTo(IReadOnlyList<RuntimeArmorCatalogEntry> catalog)
	{
		var known = catalog.Select(entry => entry.ItemId).ToHashSet();
		var extra = All.Where(record => !known.Contains(record.ItemId))
			.Select(record => new RuntimeArmorCatalogEntry(record.ItemId, record.RootArchiveId, record.DisplayName, GameDataEquipmentKitKind.Armor))
			.ToArray();
		return extra.Length == 0 ? catalog : [.. catalog, .. extra];
	}
}
