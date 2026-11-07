using HD2ModCore.Domain;

namespace HD2ModCore.Infrastructure;

// Purpose: Maps a mod's cached asset summary to user-facing categories without file-system access.
// Archive category (what the asset belongs to) wins over asset type (what the bytes are), because a
// "Material" inside an armor archive should still read as an armor mod.
public static class ModCategoryClassifier
{
	public static readonly IReadOnlyList<ModCategory> Ordered = new[]
	{
		ModCategory.Armor, ModCategory.Cape, ModCategory.Weapon,
		ModCategory.Audio, ModCategory.Visual, ModCategory.Ui, ModCategory.Animation, ModCategory.Other,
	};

	public static string DisplayName(ModCategory category) => category switch
	{
		ModCategory.Armor => "护甲",
		ModCategory.Cape => "披风",
		ModCategory.Weapon => "武器",
		ModCategory.Audio => "音效",
		ModCategory.Visual => "材质/贴图",
		ModCategory.Ui => "界面",
		ModCategory.Animation => "动画",
		ModCategory.Other => "其他",
		_ => string.Empty,
	};

	// Null summary means facts are not scanned yet: report None so callers can treat it as "pending"
	// instead of dumping the mod into Other.
	public static ModCategory Classify(ModAssetSummary? summary)
	{
		if (summary is null || summary.Assets.Count == 0) return ModCategory.None;

		var result = ModCategory.None;
		foreach (var asset in summary.Assets)
		{
			var fromArchive = FromArchiveCategory(asset.ArchiveCategory);
			result |= fromArchive != ModCategory.None ? fromArchive : FromTypeCategory(asset.TypeCategory);
		}

		return ApplyPriority(result);
	}

	// Priority: clothing (armor incl. helmet / cape) > weapon > everything else. A character armor mod that ships
	// extra audio or effects is still an armor mod; letting those side assets add chips would be misleading.
	private static ModCategory ApplyPriority(ModCategory found)
	{
		if (found == ModCategory.None) return ModCategory.Other;
		const ModCategory clothing = ModCategory.Armor | ModCategory.Cape;
		if ((found & clothing) != 0) return found & clothing;
		if ((found & ModCategory.Weapon) != 0) return ModCategory.Weapon;
		var rest = found & ~ModCategory.Other;
		return rest == ModCategory.None ? ModCategory.Other : rest;
	}

	private static ModCategory FromArchiveCategory(string? archiveCategory)
	{
		if (string.IsNullOrWhiteSpace(archiveCategory)) return ModCategory.None;
		var value = archiveCategory.Trim();
		// 头盔和护甲是同一套装备，统一算"护甲"。
		if (value.Equals("Armor", StringComparison.OrdinalIgnoreCase)
			|| value.Equals("Helmet", StringComparison.OrdinalIgnoreCase)
			|| value.Equals("CustomModel", StringComparison.OrdinalIgnoreCase)) return ModCategory.Armor;
		if (value.Equals("Cape", StringComparison.OrdinalIgnoreCase)) return ModCategory.Cape;
		if (value.StartsWith("Weapon", StringComparison.OrdinalIgnoreCase)) return ModCategory.Weapon;
		return ModCategory.None;
	}

	private static ModCategory FromTypeCategory(AssetTypeCategory type) => type switch
	{
		AssetTypeCategory.Audio => ModCategory.Audio,
		AssetTypeCategory.Texture or AssetTypeCategory.Material => ModCategory.Visual,
		AssetTypeCategory.UI => ModCategory.Ui,
		AssetTypeCategory.Animation => ModCategory.Animation,
		_ => ModCategory.Other,
	};
}
