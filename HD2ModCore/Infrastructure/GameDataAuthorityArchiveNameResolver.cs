using HD2GameItemAssets;
using HD2ModAdaptation.Analysis;
using HD2ModAdaptation.PatchReconstruction.UnitMesh;
using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

// Purpose: Joins generated Entity encyclopedia names to physical Archives
// only after the installed Archive TOCs have been scanned. A Unit may be
// shared by several Entities, so exact Entity==Unit matches are preferred and
// component-qualified candidates are used only as a controlled fallback.
internal static class GameDataAuthorityArchiveNameResolver
{
	private const string EntityNameSource = "GameData.generated_entities+strings";

	public static GameDataAuthoritySnapshot Enrich(
		GameDataAuthoritySnapshot snapshot,
		GameDataArchiveIndex index)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		ArgumentNullException.ThrowIfNull(index);
		if (snapshot.EntityFacts.Count == 0)
		{
			return snapshot;
		}

		var entitiesByUnit = snapshot.EntityFacts
			.Where(fact => fact.UnitAssetKey.TypeId == PatchUnitMeshReader.UnitTypeId && fact.UnitAssetKey.FileId != 0)
			.GroupBy(fact => fact.UnitAssetKey.FileId)
			.ToDictionary(group => group.Key, group => (IReadOnlyList<GameDataAuthorityEntityFact>)group.ToArray());

		var names = new List<GameDataAuthorityArchiveName>();
		foreach (var archive in index.Archives)
		{
			var unitIds = archive.Entries
				.Where(entry => entry.AssetKey.TypeId == PatchUnitMeshReader.UnitTypeId)
				.Select(entry => entry.AssetKey.FileId)
				.Where(fileId => fileId != 0)
				.Distinct()
				.ToArray();
			if (unitIds.Length == 0)
			{
				continue;
			}

			var selected = new List<(GameDataAuthorityEntityFact Fact, int Score, string Match)>();
			foreach (var unitId in unitIds)
			{
				if (!entitiesByUnit.TryGetValue(unitId, out var candidates))
				{
					continue;
				}

				var exact = candidates
					.Where(fact => IsReadableWeaponFact(fact, archive.Category))
					.Where(fact => fact.IsExactUnitMatch)
					.ToArray();
				if (exact.Length != 0)
				{
					selected.AddRange(exact.Select(fact => (fact, 100, "exact-unit")));
					continue;
				}

				var fallback = candidates
					.Where(fact => IsReadableWeaponFact(fact, archive.Category))
					.Where(fact => fact.HasComponent(GeneratedEntityTypeIds.WeaponCustomizationComponentData)
						|| IsExplicitWeaponCategory(archive.Category)
						|| (fact.HasComponent(GeneratedEntityTypeIds.EquipmentComponentData)
							&& fact.HasComponent(GeneratedEntityTypeIds.WieldableComponentData)))
					.ToArray();
				if (fallback.Length == 0)
				{
					continue;
				}

				var highestScore = fallback.Max(fact => fact.HasComponent(GeneratedEntityTypeIds.WeaponCustomizationComponentData) ? 90 : 80);
				selected.AddRange(fallback
					.Where(fact => (fact.HasComponent(GeneratedEntityTypeIds.WeaponCustomizationComponentData) ? 90 : 80) == highestScore)
					.Select(fact => (fact, highestScore, highestScore == 90 ? "qualified-customization" : "qualified-category")));
			}

			if (selected.Count == 0)
			{
				continue;
			}

			var archiveId = archive.PackageName.Trim().ToLowerInvariant();
			var category = string.IsNullOrWhiteSpace(archive.Category) ? "Weapon" : string.Empty;
			foreach (var localeGroup in selected
				.SelectMany(item => item.Fact.LocalizedNames.Select(name =>
					(Locale: name.Key, DisplayName: FormatDisplayName(item.Fact, name.Key, name.Value), item.Score, item.Match)))
				.Where(item => IsReadableName(item.DisplayName))
				.GroupBy(item => item.Locale, StringComparer.OrdinalIgnoreCase))
			{
				foreach (var displayName in localeGroup
					.Select(item => item.DisplayName.Trim())
					.Distinct(StringComparer.Ordinal)
					.OrderBy(value => value, StringComparer.Ordinal))
				{
					var strongest = localeGroup
						.Where(item => string.Equals(item.DisplayName.Trim(), displayName, StringComparison.Ordinal))
						.OrderByDescending(item => item.Score)
						.First();
					names.Add(new GameDataAuthorityArchiveName(
						archiveId,
						0,
						localeGroup.Key,
						displayName,
						$"{EntityNameSource}:{strongest.Match}",
						strongest.Score,
						category));
				}
			}
		}

		var merged = snapshot.ArchiveNames
			.Concat(names)
			.GroupBy(name => (name.ArchiveId, name.ItemId, name.Locale, name.DisplayName, name.Source), ArchiveNameComparer.Instance)
			.Select(group => group.OrderByDescending(name => name.Confidence).First())
			.OrderBy(name => name.ArchiveId, StringComparer.OrdinalIgnoreCase)
			.ThenBy(name => name.ItemId)
			.ThenBy(name => name.Locale, StringComparer.OrdinalIgnoreCase)
			.ThenBy(name => name.DisplayName, StringComparer.Ordinal)
			.ToArray();

		return snapshot with { ArchiveNames = merged };
	}

	private static bool IsReadableWeaponFact(GameDataAuthorityEntityFact fact, string? archiveCategory)
		=> (fact.IsWeaponCandidate
			|| (IsExplicitWeaponCategory(archiveCategory)
				&& GeneratedEquipmentTypeValues.IsWeaponLike(fact.EquipmentType)
				&& fact.HasComponent(GeneratedEntityTypeIds.EquipmentComponentData)
				&& fact.HasComponent(GeneratedEntityTypeIds.WieldableComponentData)))
			&& fact.LocalizedNames.Any(pair => IsReadableName(pair.Value));

	private static bool IsExplicitWeaponCategory(string? category)
	{
		if (string.IsNullOrWhiteSpace(category))
		{
			return false;
		}

		var value = category.ToLowerInvariant();
		if (value.Contains("customization", StringComparison.Ordinal)
			|| value.Contains("attachment", StringComparison.Ordinal)
			|| value.Contains("scope", StringComparison.Ordinal)
			|| value.Contains("ammo", StringComparison.Ordinal)
			|| value.Contains("mag", StringComparison.Ordinal))
		{
			return false;
		}

		return value.Contains("weapon", StringComparison.Ordinal)
			|| value.Contains("rifle", StringComparison.Ordinal)
			|| value.Contains("pistol", StringComparison.Ordinal)
			|| value.Contains("shotgun", StringComparison.Ordinal)
			|| value.Contains("launcher", StringComparison.Ordinal)
			|| value.Contains("cannon", StringComparison.Ordinal)
			|| value.Contains("flamethrower", StringComparison.Ordinal)
			|| value.Contains("railgun", StringComparison.Ordinal)
			|| value.Contains("spear", StringComparison.Ordinal)
			|| value.Contains("shield", StringComparison.Ordinal)
			|| value.Contains("support", StringComparison.Ordinal)
			|| value.Contains("submachine", StringComparison.Ordinal)
			|| value.Contains("machine gun", StringComparison.Ordinal)
			|| value.Contains("energy-based", StringComparison.Ordinal)
			|| value.Contains("throwable", StringComparison.Ordinal)
			|| value.Contains("secondary", StringComparison.Ordinal)
			|| value.Contains("marksman", StringComparison.Ordinal)
			|| value.Contains("assault", StringComparison.Ordinal);
	}

	private static bool IsReadableName(string? value)
		=> !string.IsNullOrWhiteSpace(value)
			&& !value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
			&& !value.StartsWith("hash-", StringComparison.OrdinalIgnoreCase);

	private static string FormatDisplayName(GameDataAuthorityEntityFact fact, string locale, string displayName)
	{
		var name = displayName.Trim();
		if (fact.Designations.TryGetValue(locale, out var designation)
			&& IsReadableName(designation)
			&& !name.StartsWith(designation.Trim(), StringComparison.OrdinalIgnoreCase))
		{
			return $"{designation.Trim()}“{name}”";
		}

		return name;
	}

	private sealed class ArchiveNameComparer : IEqualityComparer<(string ArchiveId, uint ItemId, string Locale, string DisplayName, string Source)>
	{
		public static ArchiveNameComparer Instance { get; } = new();

		public bool Equals(
			(string ArchiveId, uint ItemId, string Locale, string DisplayName, string Source) x,
			(string ArchiveId, uint ItemId, string Locale, string DisplayName, string Source) y)
			=> x.ItemId == y.ItemId
				&& string.Equals(x.ArchiveId, y.ArchiveId, StringComparison.OrdinalIgnoreCase)
				&& string.Equals(x.Locale, y.Locale, StringComparison.OrdinalIgnoreCase)
				&& string.Equals(x.DisplayName, y.DisplayName, StringComparison.Ordinal)
				&& string.Equals(x.Source, y.Source, StringComparison.Ordinal);

		public int GetHashCode((string ArchiveId, uint ItemId, string Locale, string DisplayName, string Source) obj)
			=> HashCode.Combine(
				StringComparer.OrdinalIgnoreCase.GetHashCode(obj.ArchiveId),
				obj.ItemId,
				StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Locale),
				StringComparer.Ordinal.GetHashCode(obj.DisplayName),
				StringComparer.Ordinal.GetHashCode(obj.Source));
	}
}
