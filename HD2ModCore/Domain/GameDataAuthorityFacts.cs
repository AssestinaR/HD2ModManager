using HD2ModAdaptation.Analysis;

namespace HD2ModCore.Domain;

// Purpose: Describes the source used for explicit equipment facts read from the installed game.
public enum GameDataAuthoritySourceKind
{
	Unavailable,
	PlaintextDataLibrary,
	DecryptedDataLibrary,
}

// Purpose: Keeps explicit game facts separate from Unit-structure-derived fallback facts.
public enum GameDataFactOrigin
{
	Unknown,
	AuthoritativeGameData,
	DerivedUnitStructure,
	HeuristicFallback,
}

// Purpose: Mirrors the game's customization-kit category without leaking binary parser details into callers.
public enum GameDataEquipmentKitKind : uint
{
	Armor = 0,
	Helmet = 1,
	Cape = 2,
	Unknown = uint.MaxValue,
}

public sealed record GameDataAuthorityResourceReference(string Role, ulong FileId);

// Purpose: An explicit installed-game relationship from a customization kit to one Unit and its resource references.
public sealed record GameDataAuthorityPiece(
	uint RawBodyType,
	UnitMeshBodyVariant BodyVariant,
	uint Slot,
	UnitMeshPartKind PartKind,
	uint PieceType,
	uint Weight,
	byte ToneVariations,
	AssetKey UnitAssetKey,
	IReadOnlyList<GameDataAuthorityResourceReference> Resources);

public sealed record GameDataAuthorityKit(
	uint ItemId,
	uint DlcId,
	uint SetId,
	GameDataEquipmentKitKind Kind,
	string ArchiveId,
	string DisplayName,
	IReadOnlyList<GameDataAuthorityPiece> Pieces)
{
	public string Category => Kind switch
	{
		GameDataEquipmentKitKind.Armor => "Armor",
		GameDataEquipmentKitKind.Helmet => "Helmet",
		GameDataEquipmentKitKind.Cape => "Cape",
		_ => "Customization",
	};
}

// Purpose: Supplies authoritative category metadata for an actual archive discovered from GameData.
public sealed record GameDataAuthorityArchiveMetadata(
	string ArchiveId,
	string Category,
	string DisplayName,
	IReadOnlyList<uint> ItemIds)
{
	// Keep every resolved locale (and any explicitly validated direct fallback)
	// alongside the single selected display name, so callers can switch UI
	// language without unnecessarily falling back to a hash.
	public IReadOnlyDictionary<string, string> LocalizedNames { get; init; }
		= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

// Purpose: Keeps one authoritative localized name and its originating kit
// separate from the single display name chosen for an archive row.
public sealed record GameDataAuthorityArchiveName(
	string ArchiveId,
	uint ItemId,
	string Locale,
	string DisplayName,
	string Source,
	int Confidence,
	string Category = "");

// Purpose: Immutable, fingerprinted result of parsing the installed generated_customization_armor_sets library.
public sealed record GameDataAuthoritySnapshot(
	GameDataAuthoritySourceKind SourceKind,
	string? SourcePath,
	string SourceFingerprint,
	DateTimeOffset ReadUtc,
	IReadOnlyList<GameDataAuthorityKit> Kits,
	IReadOnlyList<CoreIssue> Issues)
{
	public string PreferredLocale { get; init; } = "en-US";
	public IReadOnlyList<GameDataAuthorityArchiveName> ArchiveNames { get; init; }
		= Array.Empty<GameDataAuthorityArchiveName>();

	public bool IsAvailable => SourceKind != GameDataAuthoritySourceKind.Unavailable && Kits.Count != 0;

	public IReadOnlyDictionary<string, GameDataAuthorityArchiveMetadata> BuildArchiveMetadata()
	{
		var localizedNamesByArchive = ArchiveNames
			.Where(name => !string.IsNullOrWhiteSpace(name.ArchiveId))
			.GroupBy(name => name.ArchiveId, StringComparer.OrdinalIgnoreCase)
			.ToDictionary(
				group => group.Key,
				group => group
					.Where(name => !string.IsNullOrWhiteSpace(name.DisplayName))
					.GroupBy(name => name.Locale, StringComparer.OrdinalIgnoreCase)
					.ToDictionary(
						locale => locale.Key,
						locale => locale
							.OrderByDescending(name => name.Confidence)
							.ThenBy(name => name.ItemId)
							.ThenBy(name => name.DisplayName, StringComparer.Ordinal)
							.Select(name => name.DisplayName)
							.First(),
						StringComparer.OrdinalIgnoreCase),
				StringComparer.OrdinalIgnoreCase);

		var archiveIds = Kits.Select(kit => kit.ArchiveId)
			.Concat(ArchiveNames.Select(name => name.ArchiveId))
			.Where(id => !string.IsNullOrWhiteSpace(id))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray();

		return archiveIds.ToDictionary(
			archiveId => archiveId,
			archiveId =>
			{
				var kits = Kits.Where(kit => kit.ArchiveId.Equals(archiveId, StringComparison.OrdinalIgnoreCase)).ToArray();
				var names = ArchiveNames.Where(name => name.ArchiveId.Equals(archiveId, StringComparison.OrdinalIgnoreCase)).ToArray();
				var categories = kits.Select(kit => kit.Category)
					.Concat(names.Select(name => name.Category))
					.Where(category => !string.IsNullOrWhiteSpace(category))
					.Distinct(StringComparer.OrdinalIgnoreCase)
					.OrderBy(category => CategoryOrder(category))
					.ThenBy(category => category, StringComparer.OrdinalIgnoreCase)
					.ToArray();
				localizedNamesByArchive.TryGetValue(archiveId, out var localizedNames);
				var displayName = PickDisplayName(
					localizedNames,
					kits.Select(kit => kit.DisplayName).Concat(names.Select(name => name.DisplayName)),
					PreferredLocale,
					archiveId);
				return new GameDataAuthorityArchiveMetadata(
					archiveId,
					string.Join(", ", categories),
					displayName,
					kits.Select(kit => kit.ItemId).Concat(names.Select(name => name.ItemId)).Distinct().OrderBy(id => id).ToArray())
				{
					LocalizedNames = localizedNames
						?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
				};
			},
			StringComparer.OrdinalIgnoreCase);
	}

	private static string PickDisplayName(
		IReadOnlyDictionary<string, string>? localizedNames,
		IEnumerable<string> kitNames,
		string preferredLocale,
		string fallback)
	{
		if (localizedNames is not null)
		{
			var exact = localizedNames.FirstOrDefault(pair =>
				string.Equals(pair.Key, preferredLocale, StringComparison.OrdinalIgnoreCase)
				&& IsReadable(pair.Value));
			if (!string.IsNullOrWhiteSpace(exact.Value)) return exact.Value;

			var preferredLanguage = preferredLocale.Split('-', 2)[0];
			var neutral = localizedNames
				.Where(pair => pair.Key.StartsWith(preferredLanguage + "-", StringComparison.OrdinalIgnoreCase) && IsReadable(pair.Value))
				.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
				.Select(pair => pair.Value)
				.FirstOrDefault();
			if (!string.IsNullOrWhiteSpace(neutral)) return neutral;

			var english = localizedNames
				.Where(pair => (pair.Key.Equals("en-US", StringComparison.OrdinalIgnoreCase)
						|| pair.Key.Equals("en-GB", StringComparison.OrdinalIgnoreCase))
					&& IsReadable(pair.Value))
				.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
				.Select(pair => pair.Value)
				.FirstOrDefault();
			if (!string.IsNullOrWhiteSpace(english)) return english;

			var any = localizedNames
				.Where(pair => IsReadable(pair.Value))
				.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
				.Select(pair => pair.Value)
				.FirstOrDefault();
			if (!string.IsNullOrWhiteSpace(any)) return any;
		}

		return kitNames.FirstOrDefault(IsReadable)
			?? kitNames.FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))
			?? fallback;
	}

	private static bool IsReadable(string? value)
		=> !string.IsNullOrWhiteSpace(value)
			&& !value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
			&& !value.StartsWith("hash-", StringComparison.OrdinalIgnoreCase);

	private static int CategoryOrder(string category)
		=> category.Equals("Armor", StringComparison.OrdinalIgnoreCase) ? 0
			: category.Equals("Helmet", StringComparison.OrdinalIgnoreCase) ? 1
			: category.Equals("Cape", StringComparison.OrdinalIgnoreCase) ? 2
			: 100;
}
