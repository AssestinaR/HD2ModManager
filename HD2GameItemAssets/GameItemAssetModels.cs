namespace HD2GameItemAssets;

public static class CustomizationKitType
{
	public const uint Armor = 0;
	public const uint Helmet = 1;
	public const uint Cape = 2;
}

/// <summary>
/// 件记录里的部位编号。**这是游戏自己的编号，全项目只有这一份**。
/// <para>
/// 身体套装的核心 6 个区域是 <c>2..7</c>（躯干、胯、左腿、右腿、左臂、右臂）；
/// <c>0</c> 只出现在头盔 kit，<c>1</c>（披风）与 <c>8/9</c>（双肩）是独立部位、不是每套都有。
/// </para>
/// <para>
/// 它和 unit 自己 <c>CustomizationInfo.Slot</c> 里的**字符串**是两套编号，5/6/7 档并不一致，
/// 不能互相替代。护甲的完整结构（四元组寻址、身形编码、件记录布局）见
/// <c>HD2ModCore/Domain/ArmorStructure.cs</c> 顶部的说明。
/// </para>
/// </summary>
public static class CustomizationKitSlot
{
	public const uint Helmet = 0;
	public const uint Cape = 1;
	public const uint Torso = 2;
	public const uint Hips = 3;
	public const uint LeftLeg = 4;
	public const uint RightLeg = 5;
	public const uint LeftArm = 6;
	public const uint RightArm = 7;
	public const uint LeftShoulder = 8;
	public const uint RightShoulder = 9;
}

public readonly record struct StingrayAssetId(ulong NameHash, ulong TypeHash)
{
	public static readonly ulong UnitTypeHash = StingrayHash.Murmur64A("unit");

	public static StingrayAssetId Unit(ulong nameHash) => new(nameHash, UnitTypeHash);

	public string NameHashHex => StingrayHash.Format(NameHash);
	public string TypeHashHex => StingrayHash.Format(TypeHash);
}

public sealed record GameItemAssetReference(
	string Role,
	ulong Hash)
{
	public string HashHex => StingrayHash.Format(Hash);
}

public sealed record GameItemPiece(
	StingrayAssetId UnitAsset,
	uint Slot,
	uint PieceType,
	uint Weight,
	uint BodyType,
	byte ToneVariations,
	IReadOnlyList<GameItemAssetReference> Resources);

public sealed record GameItemAssetRecord(
	uint Id,
	uint DlcId,
	uint SetId,
	uint NameUpperStringId,
	uint NameCasedStringId,
	uint DescriptionStringId,
	string Name,
	string Description,
	uint Rarity,
	uint Passive,
	ulong ArchiveHash,
	uint KitType,
	IReadOnlyList<GameItemPiece> Pieces)
{
	public string ArchiveHashHex => StingrayHash.Format(ArchiveHash);
}

public sealed record GameItemAssetCatalog(IReadOnlyList<GameItemAssetRecord> Items)
{
	public GameItemAssetRecord? FindById(uint id) => Items.FirstOrDefault(item => item.Id == id);

	public IEnumerable<GameItemAssetRecord> FindByArchive(ulong archiveHash) =>
		Items.Where(item => item.ArchiveHash == archiveHash);
}
