---
name: hd2mod-gamedata-index-management
description: Safely rebuild Manager-owned GameData archive indexes or Armor/Helmet Unit-part facts without modifying the game or Mod library.
---

# HD2 Mod GameData Index Management

Use this when a Patch workflow requires a current GameData asset index or current Armor / Helmet Unit facts. It writes only Manager-owned derived data; it never touches GameData, library.json, Profiles, or deployment.

`archivehashes.json` is optional display metadata. If it is missing or invalid, continue with the installed GameData authority facts; names may fall back to archive IDs. Do not hand-edit hashlist files.

```powershell
hd2mod gamedata index inspect --manager-root "<manager>" --game-data "<data>" --json
hd2mod gamedata index plan rebuild --manager-root "<manager>" --game-data "<data>" --output index-plan.json --json
hd2mod gamedata index plan units-all --manager-root "<manager>" --game-data "<data>" --output unit-facts-plan.json --json
hd2mod gamedata index plan units-missing --manager-root "<manager>" --game-data "<data>" --output unit-facts-plan.json --json
hd2mod gamedata index validate --plan index-plan.json --manager-root "<manager>" --game-data "<data>" --json
hd2mod gamedata index apply --plan index-plan.json --manager-root "<manager>" --game-data "<data>" --confirm --json
```

Run `rebuild` first when the base index is missing or stale. `units-all` fully recalculates Armor / Helmet facts; `units-missing` fills gaps, but automatically becomes a full rebuild when the base index fingerprint changed. Apply requires the desktop Manager to be closed and takes an exclusive Manager-data lock. If GameData or the optional `archivehashes.json` file changes after planning, validation rejects the plan.
