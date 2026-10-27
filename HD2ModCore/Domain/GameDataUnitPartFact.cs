using HD2ModAdaptation.Analysis;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;

namespace HD2ModCore.Domain;

// Purpose: Stores the stable semantic part projection for one vanilla Unit mesh.
public sealed record GameDataUnitPartFact(
	string ArchiveId,
	AssetKey UnitAssetKey,
	int MeshInfoIndex,
	uint MeshId,
	UnitMeshPartKind PartKind,
	UnitMeshPartLayer Layer,
	UnitMeshBodyVariant BodyVariant,
	string SemanticName,
	int Confidence,
	bool IsVisualMesh,
	bool IsLod,
	string Reason)
{
	public GameDataFactOrigin Origin { get; init; } = GameDataFactOrigin.DerivedUnitStructure;
	public string PieceType { get; init; } = string.Empty;
	/// <summary>
	/// 注册表给出的权威格子（部位槽 × 层）。<see cref="ArmorCell.Unknown"/> 表示这个 unit 在注册表里查不到归属。
	/// 与 <see cref="Layer"/> 不是一回事：Layer 是从网格名猜的，实测 97% 与原版注册表不一致。
	/// </summary>
	public ArmorCell Cell { get; init; } = ArmorCell.Unknown;
	public int VertexCount { get; init; }
	public int TriangleCount { get; init; }
	public UnitMeshGeometryQuality GeometryQuality { get; init; } = UnitMeshGeometryQuality.Unreadable;
}
