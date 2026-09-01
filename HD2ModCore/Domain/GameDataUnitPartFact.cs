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
	public int VertexCount { get; init; }
	public int TriangleCount { get; init; }
	public UnitMeshGeometryQuality GeometryQuality { get; init; } = UnitMeshGeometryQuality.Unreadable;
}
