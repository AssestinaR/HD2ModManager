using HD2ModAdaptation.Analysis;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModCore.Domain;
using AdaptationAssetKey = HD2ModAdaptation.PatchReconstruction.AssetKey;
using CoreAssetKey = HD2ModCore.Domain.AssetKey;

namespace HD2ModCore.Infrastructure;

// Rebinds a GameData-labelled decoration host part to the actual Mesh in the
// host Patch. MeshInfoIndex is only a position inside one Unit layout; MeshId
// and a conservative semantic fallback are the stable cross-version anchors.
internal static class DecorationHostTargetBinder
{
    internal sealed record BoundPart(EquipmentUnitPart Part, UnitMeshGeometryFact Geometry);

    private sealed record ActualMeshCandidate(
        UnitMeshInfo Mesh,
        UnitRawMeshData Raw,
        UnitMeshGeometryFact Geometry,
        UnitMeshPartFact Classification);

    public static bool TryBind(
        EquipmentUnitPart indexedPart,
        IReadOnlyList<EquipmentUnitPart> indexedPartsForUnit,
        UnitMeshModel actualModel,
        out BoundPart? bound)
    {
        ArgumentNullException.ThrowIfNull(indexedPart);
        ArgumentNullException.ThrowIfNull(indexedPartsForUnit);
        ArgumentNullException.ThrowIfNull(actualModel);

        bound = null;
        var actualMeshes = ReadReadableVisualMeshes(indexedPart.UnitAssetKey, actualModel);
        if (actualMeshes.Count == 0) return false;

        ActualMeshCandidate? selected = null;
        if (indexedPart.MeshId != 0)
        {
            selected = ChoosePreferred(actualMeshes.Where(item => item.Mesh.MeshId == indexedPart.MeshId));
        }

        // Keep the exact index as a compatibility path when the index still
        // exists and the actual semantic information does not contradict the
        // indexed label.
        selected ??= ChoosePreferred(actualMeshes
            .Where(item => item.Mesh.Index == indexedPart.MeshInfoIndex)
            .Where(item => IsSemanticallyCompatible(indexedPart, item)));

        // If both the index and MeshId changed, allow a unique semantic match.
        var semanticMatches = actualMeshes
            .Where(item => IsSemanticallyCompatible(indexedPart, item))
            .Where(item => HasStrongSemanticMatch(indexedPart, item))
            .ToArray();
        if (selected is null && semanticMatches.Length == 1)
            selected = semanticMatches[0];

        // The last reindex fallback is deliberately narrow. It is safe only
        // when this catalog scope describes one logical visible part and the
        // actual Unit has one visual LOD0 mesh. A multi-part Unit must never be
        // guessed from cardinality.
        if (selected is null && IsSingleIndexedVisualPart(indexedPartsForUnit))
        {
            var visualLod0 = actualMeshes.Where(item => item.Raw.LodIndex == 0).ToArray();
            if (visualLod0.Length == 1)
                selected = visualLod0[0];
        }

        if (selected is null) return false;

        bound = new BoundPart(
            indexedPart with
            {
                MeshInfoIndex = selected.Mesh.Index,
                MeshId = selected.Mesh.MeshId,
                VertexCount = selected.Geometry.VertexCount,
                TriangleCount = selected.Geometry.TriangleCount,
                GeometryQuality = selected.Geometry.Quality
            },
            selected.Geometry);
        return true;
    }

    private static IReadOnlyList<ActualMeshCandidate> ReadReadableVisualMeshes(CoreAssetKey unitAssetKey, UnitMeshModel model)
    {
        var geometry = UnitGeometryFactsBuilder.Analyze(model).Meshes
            .GroupBy(fact => fact.MeshInfoIndex)
            .ToDictionary(group => group.Key, group => group.First());
        var classifications = new UnitMeshPartClassifier()
            .Classify(new AdaptationAssetKey(unitAssetKey.TypeId, unitAssetKey.FileId), model)
            .GroupBy(fact => fact.MeshInfoIndex)
            .ToDictionary(group => group.Key, group => group.First());

        return model.Meshes
            .Select(mesh =>
            {
                var raw = model.RawMeshData.SingleOrDefault(item => item.MeshInfoIndex == mesh.Index);
                geometry.TryGetValue(mesh.Index, out var fact);
                classifications.TryGetValue(mesh.Index, out var classification);
                return (Mesh: mesh, Raw: raw, Geometry: fact, Classification: classification);
            })
            .Where(item => item.Raw is not null
                && item.Geometry is not null
                && item.Classification is not null
                && item.Raw.LodIndex >= 0
                && item.Mesh.SemanticInfo.IsVisualMesh)
            .Select(item => new ActualMeshCandidate(item.Mesh, item.Raw!, item.Geometry!, item.Classification!))
            .ToArray();
    }

    private static ActualMeshCandidate? ChoosePreferred(IEnumerable<ActualMeshCandidate> candidates)
        => candidates
            .OrderBy(item => item.Raw.LodIndex == 0 ? 0 : 1)
            .ThenBy(item => item.Raw.LodIndex)
            .ThenByDescending(item => item.Geometry.TriangleCount)
            .ThenByDescending(item => item.Geometry.VertexCount)
            .ThenBy(item => item.Mesh.Index)
            .FirstOrDefault();

    private static bool IsSemanticallyCompatible(EquipmentUnitPart indexedPart, ActualMeshCandidate actual)
    {
        var classification = actual.Classification;
        if (indexedPart.PartKind != UnitMeshPartKind.Unknown
            && classification.PartKind != UnitMeshPartKind.Unknown
            && indexedPart.PartKind != classification.PartKind)
            return false;

        if (indexedPart.BodyVariant is not (UnitMeshBodyVariant.Unknown or UnitMeshBodyVariant.Any)
            && classification.BodyVariant is not (UnitMeshBodyVariant.Unknown or UnitMeshBodyVariant.Any or UnitMeshBodyVariant.Other)
            && indexedPart.BodyVariant != classification.BodyVariant)
            return false;

        if (indexedPart.Layer != UnitMeshPartLayer.Unknown
            && classification.Layer != UnitMeshPartLayer.Unknown
            && indexedPart.Layer != classification.Layer)
            return false;

        if (!string.IsNullOrWhiteSpace(indexedPart.PieceType)
            && !string.IsNullOrWhiteSpace(actual.Mesh.SemanticInfo.PieceType)
            && !string.Equals(indexedPart.PieceType.Trim(), actual.Mesh.SemanticInfo.PieceType.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    private static bool HasStrongSemanticMatch(EquipmentUnitPart indexedPart, ActualMeshCandidate actual)
    {
        var indexedName = indexedPart.SemanticName?.Trim();
        var actualName = actual.Mesh.SemanticInfo.Name?.Trim();
        if (!string.IsNullOrWhiteSpace(indexedName)
            && !string.IsNullOrWhiteSpace(actualName)
            && string.Equals(indexedName, actualName, StringComparison.OrdinalIgnoreCase))
            return true;

        var indexedPiece = indexedPart.PieceType?.Trim();
        var actualPiece = actual.Mesh.SemanticInfo.PieceType?.Trim();
        if (!string.IsNullOrWhiteSpace(indexedPiece)
            && !string.IsNullOrWhiteSpace(actualPiece)
            && string.Equals(indexedPiece, actualPiece, StringComparison.OrdinalIgnoreCase))
            return true;

        return indexedPart.PartKind != UnitMeshPartKind.Unknown
            && actual.Classification.PartKind != UnitMeshPartKind.Unknown
            && indexedPart.PartKind == actual.Classification.PartKind
            && (indexedPart.BodyVariant is UnitMeshBodyVariant.Unknown or UnitMeshBodyVariant.Any
                || actual.Classification.BodyVariant is UnitMeshBodyVariant.Unknown or UnitMeshBodyVariant.Any or UnitMeshBodyVariant.Other
                || indexedPart.BodyVariant == actual.Classification.BodyVariant);
    }

    private static bool IsSingleIndexedVisualPart(IReadOnlyList<EquipmentUnitPart> parts)
        => parts
            .Where(part => !part.IsCullingMesh && part.Layer is not UnitMeshPartLayer.Culling and not UnitMeshPartLayer.Static)
            .Select(part => part.MeshInfoIndex)
            .Distinct()
            .Count() == 1;
}
