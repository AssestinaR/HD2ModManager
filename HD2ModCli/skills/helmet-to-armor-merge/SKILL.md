---
name: hd2mod-helmet-to-armor-merge
description: Review complete Armor and Any Helmet source units, then output a standalone merged Patch without changing the library or GameData.
---

# HD2 Mod Helmet-to-Armor Merge

Use this only to append a selected source Mod's Any Helmet geometry to its matching Armor torso Unit and hide that source Helmet Unit in a new Patch. It is not an in-place Mod edit, import, or deployment workflow.

```powershell
hd2mod equipment helmet-merge inspect <mod-guid> [...] --manager-root "<manager>" --game-data "<game-data>" --json
hd2mod equipment helmet-merge plan <mod-guid> [...] --manager-root "<manager>" --game-data "<game-data>" --output .\helmet-merge.plan.json --json
hd2mod equipment helmet-merge validate --plan .\helmet-merge.plan.json --manager-root "<manager>" --game-data "<game-data>" --json
hd2mod equipment helmet-merge apply --plan .\helmet-merge.plan.json --manager-root "<manager>" --game-data "<game-data>" --output .\output\helmet-merge --confirm --json
```

Review `candidates`, especially `sharedArmorArchiveIds` and `sharedHelmetArchiveIds`. The default selections omit competing Helmet sources for one Armor Mesh. You may edit only `selections`, using existing `sourceNodeId` and `candidateId` values; never invent IDs.

`validate` and `apply` rebuild candidates and reject source/GameData drift, conflicting Armor targets, and a Unit that would be both edited as Armor and hidden as Helmet. `apply` emits one isolated child directory per source Mod beneath a fresh output root outside both the Mod library and the game root. Import or deploy the result later through their separate Skills if wanted.
