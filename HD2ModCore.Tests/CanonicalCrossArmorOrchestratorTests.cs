using HD2ModAdaptation.PatchReconstruction;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModAdaptation.Analysis;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;
using Xunit;

namespace HD2ModCore.Tests;

// Purpose: Verifies canonical orchestration fails closed before any legacy CrossArmor operation is entered.
// SDK order reference: GetEntryByLoadArchive(IgnorePatch=True) -> Load -> AddEntryToPatchID -> Entry.Save;
// documentation reference: docs/sdk流程架构.md sections 1-8.
public sealed class CanonicalCrossArmorOrchestratorTests
{
	[Theory]
	[InlineData(false, true)]
	[InlineData(true, false)]
	public async Task ExecuteAsync_CustomTargetRejectsFallbackAndIncompleteOutputBeforeIO(bool direct, bool mapped)
	{
		var source = new EquipmentUnitPart(new HD2ModCore.Domain.AssetKey(PatchUnitMeshReader.UnitTypeId, 1),
			0, 1, UnitMeshPartKind.Torso, UnitMeshPartLayer.Armor, UnitMeshBodyVariant.Any, "source", 100, []);
		var target = source with { UnitAssetKey = new(PatchUnitMeshReader.UnitTypeId, 0xfedcba9876543210) };
		var custom = new EquipmentUnitCatalogEntry("custom-model:test", "CustomModel", "test", [target]) { CustomModelKey = "test" };
		var plan = new CrossArmorTransferPlan([], new("source", "Armor", "source", [source]), [custom],
			[new(new(target.UnitAssetKey), target, mapped ? source : null, mapped, "", [], [], false, false)], [], []);
		var result = await new CanonicalCrossArmorOrchestrator().ExecuteAsync(
			new("missing-source.patch_0", "missing-data", "must-not-create", plan, DirectSourceUnitReuse: direct));
		Assert.False(result.IsSuccessful);
		Assert.Contains(result.Issues, issue => issue.Code == "CustomModelOutputIncomplete");
	}

	[Fact]
	public async Task ExecuteAsync_RejectsEmptyPlanBeforeReadersOrLegacyPath()
	{
		var request = new CrossArmorTransferCandidateRequest(
			"missing-source.patch_0",
			Directory.GetCurrentDirectory(),
			Path.Combine(Path.GetTempPath(), "canonical-orchestrator-test"),
			new CrossArmorTransferPlan([], null, [], [], [], [new(CoreIssueSeverity.Error, "Empty", "empty")]));

		var result = await new CanonicalCrossArmorOrchestrator().ExecuteAsync(request);

		Assert.False(result.IsSuccessful);
		Assert.Contains(result.Issues, issue => issue.Code == "CanonicalPlanNotReady");
	}

	[Fact]
	public async Task ExecuteAsync_RejectsUnmatchedPlanWithoutGameDataReader()
	{
		var sourcePath = Path.Combine(Path.GetTempPath(), $"canonical-source-{Guid.NewGuid():N}.patch_0");
		var gameData = Path.Combine(Path.GetTempPath(), $"canonical-game-{Guid.NewGuid():N}");
		Directory.CreateDirectory(gameData);
		await File.WriteAllBytesAsync(sourcePath, [1]);
		try
		{
			var source = new EquipmentUnitPart(new HD2ModCore.Domain.AssetKey(PatchUnitMeshReader.UnitTypeId, 1), 0, 1, UnitMeshPartKind.Unknown, UnitMeshPartLayer.Unknown, UnitMeshBodyVariant.Unknown, "source", 1, []);
			var target = source with { UnitAssetKey = new HD2ModCore.Domain.AssetKey(PatchUnitMeshReader.UnitTypeId, 2) };
			var plan = new CrossArmorTransferPlan(
				[],
				new EquipmentUnitCatalogEntry("target.archive", "Armor", "target", [target]),
				[new EquipmentUnitCatalogEntry("target.archive", "Armor", "target", [target])],
				[new CrossArmorTransferMapping(new(target.UnitAssetKey), target, source, true, "test", [], [], false, false)],
				[], []);

			var result = await new CanonicalCrossArmorOrchestrator().ExecuteAsync(new(sourcePath, gameData, Path.Combine(gameData, "out"), plan));

			Assert.False(result.IsSuccessful);
			Assert.DoesNotContain(result.Issues, issue => issue.Code.Contains("Legacy", StringComparison.OrdinalIgnoreCase));
		}
		finally
		{
			File.Delete(sourcePath);
			Directory.Delete(gameData, true);
		}
	}
}
