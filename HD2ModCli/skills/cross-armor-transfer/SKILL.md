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

## Custom visual model targets

Targets also include explicit-piece models from the current library's
`.hd2modmanager/armor-registry.workspace.json`. They have `category: "CustomModel"`,
`customModelKey`, and a `custom-model:<key>` target identifier. Copy that exact
identifier into `request.selectedTargetArchiveIds`; it is NOT a real Archive
or Item ID. Do not fabricate new Unit IDs in the plan.

The output reuses the model's persistent 64-bit Unit IDs and copies complete
source Unit payloads, including GPU/stream and dependencies. It does not merge
several source Units into one slot. Automatic assignment respects body region,
prefers matching body shape, and uses distinct source Units for overlapping
extra slots. Inspect mappings; use explicit same-region manual assignments if
necessary. Missing enabled components block output instead of attempting to
read nonexistent GameData targets. Disable unwanted components in the model
workspace and regenerate the plan. Disabled components (including reserved
alternate IDs) are not generated; enabling them later requires a Patch that
provides those IDs.

A current asset index is required to reject collisions with vanilla Unit IDs.
Changing model components invalidates the frozen plan. Output never changes
the workspace, applies mappings, imports a mod, or deploys it. Install the
result through the normal Mod workflow, map this custom model to real armor,
and apply the runtime registry separately. Existing Lua helmet/body separation
rules still apply.
