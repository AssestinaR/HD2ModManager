using System.Text.Json;
using HD2ModAdaptation.Analysis;
using HD2ModCore.Decoration;
using HD2ModCore.Domain;

namespace HD2ModCore.Tests;

public sealed class DecorationContractTests
{
    [Fact]
    public void PlanDocument_DeserializesExistingPackageShape_WithExistingDefaults()
    {
        const string json = """
        {
          "format": "HD2ModManager.Decoration",
          "version": 3,
          "name": "Existing decoration",
          "sourceStorageMode": "PatchSnapshot",
          "sourceUnits": [
            { "typeId": 12, "fileId": 34, "meshInfoIndex": 2, "bodyVariant": "Slim", "layer": "Visible", "isCulling": false }
          ],
          "payloads": [ { "bodyVariant": "Dual", "file": "payload.bin" } ],
          "plan": {
            "targetModGuids": [ "target-id" ],
            "targetPart": "Torso",
            "targetBodyVariant": "Dual",
            "dualVariantMode": "AutoAssign",
            "replaceWhenSourcePartLayerMatches": true,
            "sourcePartLayers": [ "Torso/Visible" ],
            "createdUtc": "2026-08-30T00:00:00Z"
          }
        }
        """;

        var document = JsonSerializer.Deserialize<DecorationPlanDocument>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(document);
        Assert.Equal("HD2ModManager.Decoration", document.Format);
        Assert.Equal(3, document.Version);
        Assert.Equal("PatchSnapshot", document.SourceStorageMode);
        var source = Assert.Single(document.SourceUnits);
        Assert.Equal((ulong)12, source.TypeId);
        Assert.Equal("Slim", source.BodyVariant);
        Assert.Equal("target-id", Assert.Single(document.Plan.TargetModGuids));
        Assert.Equal("Torso/Visible", Assert.Single(document.Plan.SourcePartLayers));
    }

    [Fact]
    public void PlanningDefaults_UseExistingPartAndVariantRules()
    {
        var torso = new EquipmentUnitPart(
            new AssetKey(1, 2), 0, 0, UnitMeshPartKind.LeftArm, UnitMeshPartLayer.Armor,
            UnitMeshBodyVariant.Stocky, "arm", 1, []);
        var head = new EquipmentUnitPart(
            new AssetKey(1, 3), 0, 0, UnitMeshPartKind.Helmet, UnitMeshPartLayer.Armor,
            UnitMeshBodyVariant.Any, "head", 1, []);

        Assert.Equal("Torso", DecorationPlanningDefaults.ResolveTargetPart([torso, head]));
        Assert.Equal("双身形", DecorationPlanningDefaults.ResolveTargetBodyVariant([torso, head]));
        Assert.True(DecorationPlanningDefaults.MatchesPartLayer(
            [DecorationPlanningDefaults.ToPartLayerKey(UnitMeshPartKind.Helmet, UnitMeshPartLayer.Armor)],
            UnitMeshPartKind.Helmet,
            UnitMeshPartLayer.Armor));
    }
}
