---
name: hd2mod-lua-reshade-external-deployment
description: Inspect and safely deploy Lua merge inputs, ReShade add-ons/presets, and supported external components through the normal deployment transaction.
---

# Lua, ReShade, and External Component Deployment

Use this only for a reviewed Profile that contains Lua patches, ReShade add-ons/presets, or both. Do not copy files into `data` or `bin` by hand: only `deploy apply` records ownership and makes later cleanup safe.

1. Inspect the registered Mod's actual files without using cached facts:

```powershell
hd2mod mod analyze <mod-id> --library "<library>" --json
hd2mod mod external inspect <mod-id> --library "<library>" --json
```

`mod analyze` identifies the supported Lua boot AssetKey. `mod external inspect` lists only safely recognized ReShade add-ons (`.addon`/`.addon64`) and ReShade presets (INI files with a root `Techniques=` key), plus rejected candidate paths and within-Mod target conflicts. It does not deploy anything.

2. Add the reviewed Mod to the intended Profile if it is not already a member, then export a deployment plan:

```powershell
hd2mod profile add <profile> <mod-id> --manager-root "<manager>" --confirm --json
hd2mod deploy plan <profile> --manager-root "<manager>" --game-data "<data>" --output deployment-plan.json --json
```

Review the plan JSON before continuing:

- `lua.inputCount = 0`: no Lua input. `1`: the original Lua Patch remains. `2+`: the manager will materialize one generated Lua Patch in Profile order; check `lua.compositionFingerprint` and `lua.generatedTargetPaths`.
- Each `externalFiles` item must have the expected `nodeId`, source, `kind`, and `targetRelativePath` below Game `bin`. Any blocking diagnostic, unknown file, or unexpected target is a stop condition.
- Multiple enabled Mods that map different content to one `bin` target are rejected by the plan. Do not resolve it by overwriting a file manually.

3. With both Manager and game closed, validate and apply the unchanged plan:

```powershell
hd2mod deploy validate --plan deployment-plan.json --manager-root "<manager>" --game-data "<data>" --json
hd2mod deploy apply --plan deployment-plan.json --manager-root "<manager>" --game-data "<data>" --confirm --json
```

Lua is syntax-compiled only; the CLI never executes Mod Lua locally. ReShade/external components are copied transactionally to Game `bin` and tracked in `activation-state.json`.

4. To remove only Manager-owned deployment outputs, use the matching deactivation workflow:

```powershell
hd2mod deploy deactivate plan --manager-root "<manager>" --game-data "<data>" --output deactivation-plan.json --json
hd2mod deploy deactivate validate --plan deactivation-plan.json --manager-root "<manager>" --game-data "<data>" --json
hd2mod deploy deactivate apply --plan deactivation-plan.json --manager-root "<manager>" --game-data "<data>" --confirm --json
```

The current safe external-file contract is limited to recognized ReShade add-ons and presets. Other files, including arbitrary game-bin payloads, remain unmanaged until they have an explicit reviewed format contract.
