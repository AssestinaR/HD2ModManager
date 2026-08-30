---
name: hd2mod-cross-armor-transfer
description: Plan and output one isolated Direct-source Unit cross-armor candidate without modifying the Mod library or GameData.
---

# HD2 Mod Cross-armor Transfer

Use this only for a source Mod with exactly one base Patch. It produces a standalone candidate Patch; it never imports, enables, deploys, or edits the source.

```powershell
hd2mod equipment cross-armor inspect <mod-id> --manager-root "<manager>" --library "<library>" --game-data "<data>" --json
hd2mod equipment cross-armor plan <mod-id> --manager-root "<manager>" --library "<library>" --game-data "<data>" --output cross-armor-plan.json --json
hd2mod equipment cross-armor validate --plan cross-armor-plan.json --manager-root "<manager>" --library "<library>" --game-data "<data>" --json
hd2mod equipment cross-armor apply --plan cross-armor-plan.json --manager-root "<manager>" --library "<library>" --game-data "<data>" --output "<new-external-directory>" --confirm --json
```

The first plan intentionally has no target, so its nonzero readiness exit is expected. Edit only its `request`: choose existing `sourceArmorArchiveId` / `sourceHelmetArchiveId`, copy one or more target IDs into `selectedTargetArchiveIds`, and optionally set physical-Unit `manualMappings` or `manualSuppressedTargetAssetKeys`. AssetKeys are fixed `0xTYPE/0xFILE` strings. `sourceBodyVariant: "Any"` does not filter out Slim or Stocky source parts. Validation rebuilds the real Core plan and rejects stale, invented, conflicting, or multi-Patch input. Apply writes only a fresh directory outside the library and game root.
