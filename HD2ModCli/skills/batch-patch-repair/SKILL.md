---
name: hd2mod-batch-patch-repair
description: Review and explicitly confirm conservative in-place repairs for one or more outdated HD2 Mod library entries.
---

# HD2 Mod Batch Patch Repair

Use this only for the manager-equivalent “detect and repair outdated Mods” workflow. It replaces source Patch files, so always use the complete plan flow and inspect `mods[].selected` before apply.

```powershell
hd2mod patch repair batch inspect [mod-id ...] --manager-root "<manager>" --library "<library>" --game-data "<data>" --json
hd2mod patch repair batch plan [mod-id ...] --manager-root "<manager>" --library "<library>" --game-data "<data>" --output batch-repair-plan.json --json
hd2mod patch repair batch validate --plan batch-repair-plan.json --manager-root "<manager>" --library "<library>" --game-data "<data>" --json
hd2mod patch repair batch apply --plan batch-repair-plan.json --manager-root "<manager>" --library "<library>" --game-data "<data>" --confirm --json
```

Omit `mod-id` to scan the whole library. The plan defaults `selected=true` only for confirmed outdated Mods; an Agent may only change `selected`. Validation rejects source Patch, Unit-version, library, or GameData-index drift. Apply first creates independent backups, restores a Mod if its commit fails, refreshes only successful nodes’ library fingerprints, and rebuilds active manager-generated Overwrites. It never deploys to GameData.
