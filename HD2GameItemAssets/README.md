# HD2GameItemAssets

`HD2GameItemAssets` reads Helldivers 2's decrypted
`data/game/generated_customization_armor_sets` data-library file. It exposes
the explicit relationship from each armor, helmet, or cape customization kit
to its archive, `unit` assets, and non-zero material-related resource hashes.

```csharp
using HD2GameItemAssets;

var catalog = CustomizationArmorSetsReader.Read(
    @"D:\SteamLibrary\steamapps\common\Helldivers 2\data\game\generated_customization_armor_sets",
    localization);

var item = catalog.FindById(itemId);
foreach (var piece in item!.Pieces)
{
    var unit = piece.UnitAsset; // (Piece.Path, Murmur64A("unit"))
    foreach (var resource in piece.Resources)
    {
        // MaterialLut, PatternLut, CapeLut, CapeGradient, CapeNac,
        // DecalScalarFields, BaseData, or DecalSheet
    }
}

using var output = File.Create("game-item-assets.json");
CustomizationArmorSetsReader.WriteJson(output, catalog);
```

The optional `localization` map uses the game's `uint` string IDs. Without it,
`Name` and `Description` remain stable hexadecimal IDs while all asset hashes
are still fully usable.

## Authority and shared archives

The `ArchiveHash` in the customization table identifies the logical equipment
archive. A referenced `unit` can be stored in a different shared physical
archive, so consumers must resolve the Unit asset key globally instead of
assuming that it is physically inside the logical equipment archive.

The manager's SQLite index treats this installed-game relationship as the
authoritative source for Armor/Helmet slot and body-variant facts. The optional
`archivehashes.json` file is only display metadata; it may be missing or invalid
without preventing the installed GameData inventory from being indexed.

## Additional authoritative Archive names

The library also contains deliberately narrow readers for the installed
`hash_lookup`, `generated_weapon_customization_settings`, and
`generated_unit_customization_settings` data. They expose a name only when the
generated setting's path hash resolves through the game's exact `hash_lookup`
Archive mapping. Weapon labels are resolved through every installed `strings`
locale; current vehicle-skin entries carry an English display label directly in
their own validated record layout.

This is intentionally not a generic binary scanner. Unsupported data libraries
and names without a verified Archive relationship must remain unnamed and fall
back to the community metadata or the hexadecimal Archive ID instead of being
guessed.
