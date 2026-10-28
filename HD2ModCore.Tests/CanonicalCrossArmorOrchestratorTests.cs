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
	[InlineData("d923906f8b815987.patch_0")]
	[InlineData("9ba626afa44a3aa3.patch_0")]
	[InlineData("0123456789ABCDEF.patch_0")]
	public void OutputCarrierIsFixedRegardlessOfSourceArmor(string source)
	{
		Assert.Equal("9ba626afa44a3aa3.patch_0",
			CanonicalCrossArmorOrchestrator.ResolveOutputPatchFileName(Path.Combine("source", source)));
	}

	[Theory]
	[InlineData("custom-model:test.patch_0")]
	[InlineData("d923906f8b815987.patch_1")]
	public void OutputCarrierStillRejectsInvalidSourceNames(string source)
	{
		Assert.Throws<InvalidDataException>(() =>
			CanonicalCrossArmorOrchestrator.ResolveOutputPatchFileName(source));
	}

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

	// 一具注册素体常把同一个 unit 同时挂在 body 0 和 body 1 上（"合并为 Any"之前的形状）。
	// 这不是残缺：每个件都有来源，只是件数多于 unit 数。旧判据（件数 == 去重 unit 数）
	// 把它判成 CustomModelOutputIncomplete，于是"把模型挂到已注册素体上"这条路走不通。
	[Fact]
	public async Task ExecuteAsync_CustomTargetWithSharedUnitsAcrossBodyVariants_PassesCompletenessGate()
	{
		var source = new EquipmentUnitPart(new HD2ModCore.Domain.AssetKey(PatchUnitMeshReader.UnitTypeId, 1),
			0, 1, UnitMeshPartKind.Torso, UnitMeshPartLayer.Undergarment, UnitMeshBodyVariant.Any, "source", 100, []);
		var unit = new HD2ModCore.Domain.AssetKey(PatchUnitMeshReader.UnitTypeId, 0xfedcba9876543210);
		var stocky = source with { UnitAssetKey = unit, BodyVariant = UnitMeshBodyVariant.Stocky };
		var slim = source with { UnitAssetKey = unit, BodyVariant = UnitMeshBodyVariant.Slim };
		var custom = new EquipmentUnitCatalogEntry("custom-model:test", "CustomModel", "test", [stocky, slim]) { CustomModelKey = "test" };
		var plan = new CrossArmorTransferPlan([], new("source", "Armor", "source", [source]), [custom],
			[new(new(unit), stocky, source, true, "", [], [], false, false)], [], []);

		var result = await new CanonicalCrossArmorOrchestrator().ExecuteAsync(
			new("missing-source.patch_0", "missing-data", "must-not-create", plan, DirectSourceUnitReuse: true));

		Assert.False(result.IsSuccessful);
		Assert.DoesNotContain(result.Issues, issue => issue.Code == "CustomModelOutputIncomplete");
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
