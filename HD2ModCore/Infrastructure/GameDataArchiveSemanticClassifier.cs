using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

// Purpose: Reduces the many community/category labels to a small, stable set
// of user-facing Archive classes after installed GameData facts are joined.
public static class GameDataArchiveSemanticClassifier
{
	public const string Armor = "Armor";
	public const string Helmet = "Helmet";
	public const string Cape = "Cape";
	public const string Weapon = "Weapon";
	public const string Other = "Other";

	public static string Classify(
		string archiveId,
		string? fallbackCategory,
		GameDataAuthoritySnapshot authority)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(archiveId);
		ArgumentNullException.ThrowIfNull(authority);

		var normalized = archiveId.Trim();
		var kitKinds = authority.Kits
			.Where(kit => string.Equals(kit.ArchiveId, normalized, StringComparison.OrdinalIgnoreCase))
			.Select(kit => kit.Kind)
			.Distinct()
			.ToHashSet();

		// The installed customization table is the authoritative source for
		// player armor pieces. Keep the order deterministic for the rare case
		// where one physical Archive is shared by more than one kit kind.
		var equipmentCategories = new[]
		{
			kitKinds.Contains(GameDataEquipmentKitKind.Armor) ? Armor : null,
			kitKinds.Contains(GameDataEquipmentKitKind.Helmet) ? Helmet : null,
			kitKinds.Contains(GameDataEquipmentKitKind.Cape) ? Cape : null,
		}
			.Where(value => value is not null)
			.Cast<string>()
			.ToArray();
		if (equipmentCategories.Length != 0)
		{
			return string.Join(", ", equipmentCategories);
		}

		// Entity encyclopedia names are only emitted by the authority name
		// resolver for weapon-like Entity -> Unit -> Archive matches.
		if (authority.ArchiveNames.Any(name =>
			string.Equals(name.ArchiveId, normalized, StringComparison.OrdinalIgnoreCase)
			&& name.Source.StartsWith("GameData.generated_entities", StringComparison.OrdinalIgnoreCase)))
		{
			return Weapon;
		}

		// Community labels remain a deliberately low-confidence compatibility
		// fallback for old/unknown game versions. They never override an
		// authoritative armor or weapon classification above.
		return ClassifyFallback(fallbackCategory);
	}

	private static string ClassifyFallback(string? category)
	{
		if (string.IsNullOrWhiteSpace(category)) return Other;
		var value = category.ToLowerInvariant();
		if (value.Contains("helmet", StringComparison.Ordinal)) return Helmet;
		if (value.Contains("cape", StringComparison.Ordinal)) return Cape;
		if (value.Contains("armor", StringComparison.Ordinal)) return Armor;
		if (value.Contains("weapon", StringComparison.Ordinal)
			|| value.Contains("rifle", StringComparison.Ordinal)
			|| value.Contains("pistol", StringComparison.Ordinal)
			|| value.Contains("shotgun", StringComparison.Ordinal)
			|| value.Contains("launcher", StringComparison.Ordinal)
			|| value.Contains("cannon", StringComparison.Ordinal)
			|| value.Contains("support", StringComparison.Ordinal)
			|| value.Contains("throwable", StringComparison.Ordinal)
			|| value.Contains("marksman", StringComparison.Ordinal)
			|| value.Contains("assault", StringComparison.Ordinal)
			|| value.Contains("submachine", StringComparison.Ordinal)
			|| value.Contains("pistol", StringComparison.Ordinal))
		{
			return Weapon;
		}

		return Other;
	}
}
