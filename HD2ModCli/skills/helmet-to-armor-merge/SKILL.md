---
name: hd2mod-helmet-to-armor-merge
description: Review complete Armor and Any Helmet source units, then output a standalone merged Patch without changing the library or GameData.
---

# HD2 Mod Helmet-to-Armor Merge

Use only to append a source Mod's Any Helmet geometry to matching Armor torso Units and hide the source Helmet Unit in a new Patch. It is not an in-place edit, import, or deployment workflow.

```powershell
hd2mod equipment helmet-merge inspect <mod-id> [...] --manager-root "<manager>" --library "<library>" --game-data "<data>" --json
hd2mod equipment helmet-merge plan <mod-id> [...] --manager-root "<manager>" --library "<library>" --game-data "<data>" --output helmet-merge-plan.json --json
hd2mod equipment helmet-merge validate --plan helmet-merge-plan.json --manager-root "<manager>" --library "<library>" --game-data "<data>" --json
hd2mod equipment helmet-merge apply --plan helmet-merge-plan.json --manager-root "<manager>" --library "<library>" --game-data "<data>" --output "<new-external-directory>" --confirm --json
```

Review `candidates`, particularly `sharedArmorArchiveIds` and `sharedHelmetArchiveIds`. Edit only existing `sourceNodeId` and `candidateId` values in `selections`. Validation rejects drift, conflicting Armor targets, and Units that would be both Armor edits and hidden Helmets. Apply publishes isolated source outputs under a fresh root outside the library and game root.
