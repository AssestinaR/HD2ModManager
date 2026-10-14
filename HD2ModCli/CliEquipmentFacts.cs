using HD2ModCore.Application;
using HD2ModCore.Domain;
using HD2ModCore.Infrastructure;

namespace HD2ModCli;

// Read-only access to the game's own equipment authority map
// (generated_customization_armor_sets, libsodium-sealed). This is the same
// facts source the desktop Manager uses to decide which archive is armor,
// helmet, or cape; exposing it lets a script answer questions like "which
// AssetKeys are capes?" without any guesswork from unit structure.

internal sealed record CliEquipmentFactUnit(
	string TypeId,
	string FileId,
	string PartKind,
	string BodyVariant,
	uint Slot,
	uint Weight);

internal sealed record CliEquipmentFactKit(
	uint ItemId,
	uint DlcId,
	uint SetId,
	string Category,
	string ArchiveId,
	string DisplayName,
	IReadOnlyList<CliEquipmentFactUnit> Units);

internal sealed record CliEquipmentFactResult(
	string SourcePath,
	string SourceFingerprint,
	string PreferredLocale,
	int KitCount,
	IReadOnlyList<CliEquipmentFactKit> Kits);

internal static class CliEquipmentFacts
{
	public static async ValueTask<GameDataAuthoritySnapshot> LoadAsync(string gameDataDirectory, CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(gameDataDirectory))
			throw new CliUserException("GameDataRequired", "读取装备权威表需要 --game-data <游戏 data 目录>（或已在 settings.json 中配置）。");
		if (!Directory.Exists(gameDataDirectory))
			throw new CliUserException("GameDataNotFound", $"GameData 目录不存在：{gameDataDirectory}");
		try
		{
			IGameDataAuthorityProvider provider = new GameDataAuthorityProvider();
			return await provider.LoadAsync(gameDataDirectory, cancellationToken).ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is IOException
			or UnauthorizedAccessException
			or InvalidDataException
			or FileNotFoundException
			or DirectoryNotFoundException)
		{
			throw new CliUserException("GameDataAuthorityUnreadable", $"无法读取游戏装备权威表：{exception.Message}");
		}
	}

	public static GameDataEquipmentKitKind? ParseKind(string? raw)
	{
		if (string.IsNullOrWhiteSpace(raw)) return null;
		return raw.Trim().ToLowerInvariant() switch
		{
			"armor" => GameDataEquipmentKitKind.Armor,
			"helmet" => GameDataEquipmentKitKind.Helmet,
			"cape" => GameDataEquipmentKitKind.Cape,
			"all" or "*" => null,
			_ => throw new CliUserException(
				"EquipmentKindInvalid",
				"参数 --kind 只接受 armor、helmet、cape 或 all。"),
		};
	}

	public static CliEquipmentFactResult Project(GameDataAuthoritySnapshot snapshot, GameDataEquipmentKitKind? filter, string? archiveFilter)
	{
		var kits = snapshot.Kits.AsEnumerable();
		if (filter is not null) kits = kits.Where(kit => kit.Kind == filter.Value);
		if (!string.IsNullOrWhiteSpace(archiveFilter))
			kits = kits.Where(kit => string.Equals(kit.ArchiveId, archiveFilter.Trim(), StringComparison.OrdinalIgnoreCase));

		var projected = kits
			.Select(kit => new CliEquipmentFactKit(
				kit.ItemId,
				kit.DlcId,
				kit.SetId,
				kit.Category,
				kit.ArchiveId,
				kit.DisplayName,
				kit.Pieces.Select(piece => new CliEquipmentFactUnit(
					$"0x{piece.UnitAssetKey.TypeId:x16}",
					$"0x{piece.UnitAssetKey.FileId:x16}",
					piece.PartKind.ToString(),
					piece.BodyVariant.ToString(),
					piece.Slot,
					piece.Weight)).ToArray()))
			.ToArray();

		return new CliEquipmentFactResult(
			snapshot.SourcePath ?? string.Empty,
			snapshot.SourceFingerprint,
			snapshot.PreferredLocale,
			projected.Length,
			projected);
	}

	/// <summary>
	/// Flattens a projection to the unique Unit AssetKeys a script would feed
	/// into a hide/replace pipeline. Deduplication is deliberate: several kits
	/// share the same Unit payload.
	/// </summary>
	public static IReadOnlyList<(string TypeId, string FileId)> DistinctUnitKeys(CliEquipmentFactResult result)
		=> result.Kits
			.SelectMany(kit => kit.Units)
			.Select(unit => (unit.TypeId, unit.FileId))
			.Distinct()
			.ToArray();
}
