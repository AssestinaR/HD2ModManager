using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModAdaptation.PatchReconstruction.UnitMesh.Canonical;
using Xunit;

namespace HD2ModAdaptation.Tests;

// Purpose: Verifies SDK-compatible material occurrence handling for Canonical section bindings.
public sealed class CanonicalMaterialBindingResolverTests
{
	[Fact]
    public void Resolve_PreservesMultipleMaterialIdsForOneTargetShortId()
    {
        var source = CreateModel(
            materialSlots: [10, 11],
            materialBindings: [new(10, 0x100UL), new(11, 0x200UL)],
            sections: [
                new(0, 10, [new(0, 1, 2)]),
                new(1, 11, [new(0, 2, 3)])]);
        var sourceRaw = source.RawMeshData.Single();
        var targetRaw = sourceRaw with
        {
            MeshInfoIndex = 1,
            Sections = [
                sourceRaw.Sections[0] with { MaterialSlotId = 900 },
                sourceRaw.Sections[1] with { MaterialSlotId = 900 }]
        };

        var result = CanonicalMaterialBindingResolver.Resolve(source, sourceRaw, targetRaw);

        Assert.True(result.IsValid);
        Assert.Equal(
            [(900u, 0x100UL), (900u, 0x200UL)],
            result.Bindings.Select(binding => (binding.SectionId, binding.MaterialId)));
    }

    [Fact]
    public void Resolve_UsesTargetMaterialLookupForExpandedShell()
    {
        var source = CreateModel(
            materialSlots: [10, 11],
            materialBindings: [new(10, 0x100UL), new(11, 0x200UL)],
            sections: [
                new(0, 10, [new(0, 1, 2)]),
                new(1, 11, [new(0, 2, 3)])]);
        var sourceRaw = source.RawMeshData.Single();
        var targetRaw = sourceRaw with
        {
            MeshInfoIndex = 1,
            Sections = [sourceRaw.Sections[0] with { MaterialSlotId = 900 }]
        };

        var result = CanonicalMaterialBindingResolver.Resolve(source, sourceRaw, targetRaw);

        Assert.True(result.IsValid);
        Assert.Equal(
            [(900u, 0x100UL), (11u, 0x200UL)],
            result.Bindings.Select(binding => (binding.SectionId, binding.MaterialId)));
		Assert.All(result.ResolvedSectionBindings, binding => Assert.True(binding.UsesTargetUnitMaterialSlotLookup));
    }

    private static UnitMeshModel CreateModel(
        IReadOnlyList<uint> materialSlots,
        IReadOnlyList<UnitMaterialBinding> materialBindings,
        IReadOnlyList<UnitRawMeshSectionData> sections)
    {
        var vertices = Enumerable.Range(0, 4)
            .Select(index => new UnitRawVertexRecord(
                (uint)index,
                [],
                [new UnitVertexComponentValue(0, "", 0, "", 0, [index, index, index], [], [])]))
            .ToArray();
        var mesh = new UnitMeshInfo(
            0, 0, 1, 0, 0, 0, (uint)materialSlots.Count, 0, (uint)sections.Count, 0,
            UnitMeshSemanticInfo.Empty(0, 0), materialSlots, sections.Select((section, index) =>
                new UnitMeshSectionInfo(0, section.MaterialIndex, section.MaterialSlotId, 0, 4, (uint)(index * 3), 3, (uint)index)).ToArray());
        var raw = new UnitRawMeshData(0, 1, 0, 0, sections, sections.SelectMany(section => section.Triangles).ToArray(), vertices);
        return new UnitMeshModel(
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            UnitCustomizationInfo.Empty,
            [],
            [new UnitStreamInfo(0, 1, 0, 1, 0, 4, 12, 0, 6, 0, 0, 48, 48, 18, [])],
            [mesh], materialBindings, [], [raw]);
    }
}
