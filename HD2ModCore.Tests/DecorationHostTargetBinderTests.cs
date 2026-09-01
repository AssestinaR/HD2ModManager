using HD2ModAdaptation.Analysis;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCore.Tests;

public sealed class DecorationHostTargetBinderTests
{
    [Fact]
    public void TryBind_RebindsChangedMeshInfoIndexByStableMeshId()
    {
        var indexed = Part(meshInfoIndex: 5, meshId: 0x1234);
        var model = Model(Mesh(meshInfoIndex: 1, meshId: 0x1234, semanticName: "g_torso_armor_lod0"));

        var success = DecorationHostTargetBinder.TryBind(indexed, [indexed], model, out var bound);

        Assert.True(success);
        Assert.NotNull(bound);
        Assert.Equal(1, bound.Part.MeshInfoIndex);
        Assert.Equal(0x1234u, bound.Part.MeshId);
        Assert.True(bound.Geometry.HasRenderableGeometry);
    }

    [Fact]
    public void TryBind_UsesSingleVisualLod0OnlyForSingleIndexedPart()
    {
        var indexed = Part(meshInfoIndex: 5, meshId: 0x1234) with { SemanticName = "legacy_unlabelled_mesh", PieceType = string.Empty };
        var model = Model(GenericMesh(meshInfoIndex: 2, meshId: 0x7777));

        var success = DecorationHostTargetBinder.TryBind(indexed, [indexed], model, out var bound);

        Assert.True(success);
        Assert.NotNull(bound);
        Assert.Equal(2, bound.Part.MeshInfoIndex);
    }

    [Fact]
    public void TryBind_RejectsAmbiguousReindexForMultiPartUnit()
    {
        var torso = Part(meshInfoIndex: 5, meshId: 0x1234) with { SemanticName = "legacy_unlabelled_mesh", PieceType = string.Empty };
        var arm = torso with { MeshInfoIndex = 6, MeshId = 0x4321, PartKind = UnitMeshPartKind.LeftArm };
        var model = Model(GenericMesh(meshInfoIndex: 2, meshId: 0x7777));

        var success = DecorationHostTargetBinder.TryBind(torso, [torso, arm], model, out var bound);

        Assert.False(success);
        Assert.Null(bound);
    }

    [Fact]
    public void TryBind_RejectsMeshWithoutReadableRawPayload()
    {
        var indexed = Part(meshInfoIndex: 5, meshId: 0x1234);
        var mesh = Mesh(meshInfoIndex: 5, meshId: 0x1234, semanticName: "g_torso_armor_lod0");
        var model = new UnitMeshModel(1, 1, 0, 0, 0, 0, 0, 0, 0, 0,
            UnitCustomizationInfo.Empty, [], [], [mesh], [], [], []);

        var success = DecorationHostTargetBinder.TryBind(indexed, [indexed], model, out var bound);

        Assert.False(success);
        Assert.Null(bound);
    }

    [Fact]
    public void TryBind_KeepsReadablePlaceholderAsEligibleHost()
    {
        var indexed = Part(meshInfoIndex: 5, meshId: 0x1234);
        var mesh = Mesh(meshInfoIndex: 1, meshId: 0x1234, semanticName: "g_torso_armor_lod0");
        var placeholder = new UnitRawMeshData(1, 0x1234, 0, 0,
            [new UnitRawMeshSectionData(0, 7, [new UnitTriangleIndices(0, 1, 2)])],
            [new UnitTriangleIndices(0, 1, 2)],
            Enumerable.Range(0, 3).Select(index => new UnitRawVertexRecord((uint)index, [1], [])).ToArray());
        var model = new UnitMeshModel(1, 1, 0, 0, 0, 0, 0, 0, 0, 0,
            UnitCustomizationInfo.Empty, [], [], [mesh], [], [], [placeholder]);

        var success = DecorationHostTargetBinder.TryBind(indexed, [indexed], model, out var bound);

        Assert.True(success);
        Assert.NotNull(bound);
        Assert.Equal(UnitMeshGeometryQuality.Placeholder, bound.Geometry.Quality);
        Assert.Equal(1, bound.Part.MeshInfoIndex);
    }

    private static EquipmentUnitPart Part(int meshInfoIndex, uint meshId)
        => new(new AssetKey(PatchUnitMeshReader.UnitTypeId, 0x1111), meshInfoIndex, meshId,
            UnitMeshPartKind.Torso, UnitMeshPartLayer.Armor, UnitMeshBodyVariant.Any,
            "g_torso_armor_lod0", 100, [])
        {
            PieceType = "Armor"
        };

    private static UnitMeshModel Model(UnitMeshInfo mesh)
    {
        var raw = Raw(mesh.Index, mesh.MeshId, mesh.LodIndex);
        return new UnitMeshModel(1, 1, 0, 0, 0, 0, 0, 0, 0, 0,
            UnitCustomizationInfo.Empty, [], [], [mesh], [], [], [raw]);
    }

    private static UnitMeshInfo Mesh(int meshInfoIndex, uint meshId, string semanticName)
        => new(meshInfoIndex, 0, meshId, 0, 0, 0, 1, 0, 1, 0,
            new UnitMeshSemanticInfo(semanticName, "Torso", "Armor", "Any", string.Empty, 0, meshInfoIndex, false, false, false),
            [7], [new UnitMeshSectionInfo(0, 0, 7, 0, 4, 0, 6, 0)]);

    private static UnitMeshInfo GenericMesh(int meshInfoIndex, uint meshId)
        => new(meshInfoIndex, 0, meshId, 0, 0, 0, 1, 0, 1, 0,
            UnitMeshSemanticInfo.Empty(0, meshInfoIndex),
            [7], [new UnitMeshSectionInfo(0, 0, 7, 0, 4, 0, 6, 0)]);

    private static UnitRawMeshData Raw(int meshInfoIndex, uint meshId, int lodIndex)
    {
        var triangles = new[] { new UnitTriangleIndices(0, 1, 2), new UnitTriangleIndices(0, 2, 3) };
        var vertices = Enumerable.Range(0, 4).Select(index => new UnitRawVertexRecord((uint)index, [1], [])).ToArray();
        return new UnitRawMeshData(meshInfoIndex, meshId, lodIndex, 0,
            [new UnitRawMeshSectionData(0, 7, triangles)], triangles, vertices);
    }
}
