using HD2ModAdaptation.PatchReconstruction;

namespace HD2ModAdaptation.Analysis;

// Purpose: Defines read-only semantic facts that describe the visible armor role of each Unit mesh.
//
// 注意：这是**展示用的**部位枚举，不是游戏的编号。游戏自己的编号在
// HD2GameItemAssets.CustomizationKitSlot（0..9），两者要靠显式映射对齐。
// 它没有 Cape 成员——披风被并进 Accessory，与真正的配件（pouch/pad/plate）混在一个桶里。
// 护甲结构全貌（四元组寻址、身形编码、件布局）见 HD2ModCore/Domain/ArmorStructure.cs。
public enum UnitMeshPartKind
{
	Unknown = 0,
	// 名字与游戏的注册表一致（Helmet / Cape / Torso / Hips / …），中文显示名统一由
	// HD2ModCore.Domain.ArmorStructure 给出。数值按值序列化进 SQLite，发布后不许改。
	Helmet = 1,
	Torso = 2,
	Hips = 3,
	LeftArm = 4,
	RightArm = 5,
	LeftLeg = 6,
	RightLeg = 7,
	LeftShoulder = 8,
	RightShoulder = 9,
	Accessory = 10,
	/// <summary>披风。曾经被并进 <see cref="Accessory"/>，与真正的配件混在一个桶里。</summary>
	Cape = 11,
}

public enum UnitMeshPartLayer
{
	Unknown,
	Undergarment,
	Armor,
	Accessory,
	Culling,
	Static
}

public enum UnitMeshBodyVariant
{
	Unknown,
	Slim,
	Stocky,
	Any,
	Other
}

public enum UnitMeshPartEvidenceKind
{
	Unknown,
	CustomizationInfo,
	BoneName,
	NameHeuristic
}

public sealed record UnitMeshPartFact(
	AssetKey UnitAssetKey,
	int MeshInfoIndex,
	uint MeshId,
	UnitMeshPartKind PartKind,
	UnitMeshPartLayer Layer,
	UnitMeshBodyVariant BodyVariant,
	string SemanticName,
	UnitMeshPartEvidenceKind EvidenceKind,
	int Confidence,
	bool IsVisualMesh,
	bool IsLod,
	string Reason)
{
	public string PieceType { get; init; } = string.Empty;
}