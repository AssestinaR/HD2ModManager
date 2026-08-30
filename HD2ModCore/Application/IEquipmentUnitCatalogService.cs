using HD2ModAdaptation.Analysis;
using HD2ModCore.Domain;

namespace HD2ModCore.Application;

// Purpose: Reads indexed Armor and Helmet Unit-part facts and produces a no-write cross-equipment transfer preview.
public interface IEquipmentUnitCatalogService
{
	ValueTask<IReadOnlyList<EquipmentUnitCatalogEntry>> GetEntriesAsync(
		IReadOnlySet<AssetKey>? unitAssetKeys = null,
		CancellationToken cancellationToken = default);

	/// <summary>
	/// Reads an already-built equipment catalog without schema migration, WAL
	/// setup, or any persistent write. Callers receive an empty result when the
	/// indexed tables are unavailable.
	/// </summary>
	ValueTask<IReadOnlyList<EquipmentUnitCatalogEntry>> GetEntriesReadOnlyAsync(
		IReadOnlySet<AssetKey>? unitAssetKeys = null,
		CancellationToken cancellationToken = default);

    ValueTask<CrossArmorTransferPlan> CreatePlanAsync(
		IReadOnlyList<EquipmentUnitCatalogEntry> sourceCandidates,
		IReadOnlyList<EquipmentUnitCatalogEntry> targetCandidates,
		string? selectedSourceArchiveId,
		UnitMeshBodyVariant? selectedSourceBodyVariant,
		CrossArmorBodyVariantPreference bodyVariantPreference,
		CrossArmorLayerPreference layerPreference,
		IReadOnlyCollection<string> selectedTargetArchiveIds,
		IReadOnlyList<CrossArmorManualMapping>? manualMappings = null,
		IReadOnlyList<CrossArmorManualSuppression>? manualSuppressions = null,
		bool manualMode = false,
		IReadOnlyCollection<string>? additionalSourceArchiveIds = null,
		CancellationToken cancellationToken = default);
}
