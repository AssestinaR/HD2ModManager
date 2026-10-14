---
name: hd2mod-equipment-facts
description: Read the game's own equipment authority map to answer which AssetKeys are armor, helmets, or capes.
---

# HD2 Mod Equipment Facts

Read-only access to the installed game's explicit Armor / Helmet / Cape customization map (`generated_customization_armor_sets`, libsodium-sealed, decoded from the game data directory). This is the same authority the desktop Manager uses to classify archives; it is more reliable than guessing from Unit structure.

No library, no manager root, and no write lease are required. `--game-data` must point at the game's `data` directory (or be configured in `settings.json`).

```powershell
hd2mod gamedata equipment list --game-data "<game data>" [--kind cape|armor|helmet|all] [--archive-id <hex>] --json
hd2mod gamedata equipment show --game-data "<game data>" [--kind cape|armor|helmet|all] [--archive-id <hex>] --json
```

## Output shape

`list` returns `equipment.kits[]` — each with `category`, `archiveId`, `displayName` and its `units[]` (type id, file id, part kind, body variant, slot, weight) — plus a de-duplicated `equipment.keys[]` array ready to pipe into a hiding or replacement step.

`show` returns the same projection without the flattened `keys` array.

## Important caveat

Some kits legitimately carry a zero AssetKey (`0x0000000000000000`) for a slot the game resolves by other means at runtime. Treat zero keys as "no explicit Unit here", not as an error, and filter them out before building a hide/replace list.

## Typical use

```powershell
# Enumerate every cape and its Unit AssetKeys
hd2mod gamedata equipment list --game-data "<game data>" --kind cape --json > capes.json

# Feed those keys straight into the Patch primitives
hd2mod patch hide-unit "<vanilla.patch_0>" --keys @capes.json --output "./capes-hidden" --json
```

Pair this with the `hd2mod-patch-primitives` skill to build category-wide hide or replace mods without any GUI.
