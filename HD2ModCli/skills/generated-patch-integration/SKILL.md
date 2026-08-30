---
name: hd2mod-generated-patch-integration
description: Safely take a verified standalone Patch output through import, exact Profile alignment, deployment, and actual GameData winner inspection.
---

# Generated Patch Integration

Use this only after one output-producing Skill has completed successfully (`patch repair`, `patch repackage`, `equipment helmet-merge`, `equipment cross-armor`, or an importable decoration package). The output tool itself never imports or deploys its result.

1. Keep the output directory outside both the Mod library and the game directory. Inspect the source facts first when needed:

```powershell
hd2mod mod analyze <source-mod-id> --library "<library>" --json
```

2. Import the generated external directory without implicit Profile changes. Review and apply the ordinary import plan:

```powershell
hd2mod import plan "<generated-external-directory>" --manager-root "<manager>" --output generated-import-plan.json --json
hd2mod import validate --plan generated-import-plan.json --manager-root "<manager>" --json
hd2mod import apply --plan generated-import-plan.json --manager-root "<manager>" --confirm --json
```

Read the imported Mod GUID from the apply JSON. Do not assume the directory name is its identity. Immediately analyze the newly imported Mod; its AssetKey list, not the old source Mod's list, is the input for post-deployment winner checks:

```powershell
hd2mod mod analyze <imported-mod-id> --library "<library>" --json
```

3. Put the imported Mod at the intended final logical position using Profile alignment. Export the chosen Profile, edit only `entryNodeIds` so it contains the imported GUID exactly once in the desired order, then run the frozen plan:

```powershell
hd2mod profile align export <profile-or-active> --library "<library>" --output profile-spec.json --json
hd2mod profile align inspect profile-spec.json --manager-root "<manager>" --json
hd2mod profile align plan profile-spec.json --manager-root "<manager>" --output profile-align-plan.json --json
hd2mod profile align validate --plan profile-align-plan.json --manager-root "<manager>" --json
hd2mod profile align apply --plan profile-align-plan.json --manager-root "<manager>" --confirm --json
```

4. Deploy only after the Profile plan is current and the game is closed, then inspect real winners:

```powershell
hd2mod deploy plan <profile-or-active> --manager-root "<manager>" --game-data "<data>" --output deployment-plan.json --json
hd2mod deploy validate --plan deployment-plan.json --manager-root "<manager>" --game-data "<data>" --json
hd2mod deploy apply --plan deployment-plan.json --manager-root "<manager>" --game-data "<data>" --confirm --json
hd2mod gamedata deployment inspect --game-data "<data>" --json
hd2mod gamedata override show 0x<TYPE> 0x<FILE> --game-data "<data>" --json
```

For every AssetKey reported by the newly imported Mod's `mod analyze`, check the actual winner with `gamedata override show`. Stop on any blocking diagnostic. Never bypass this chain by copying generated Patch files directly into GameData: deployment records ownership, Lua compilation, external/ReShade files, and cleanup state that raw copying cannot safely reproduce.
