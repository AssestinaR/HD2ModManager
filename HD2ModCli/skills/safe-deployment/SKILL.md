---
name: hd2mod-safe-deployment
description: Safely deploy or deactivate a Profile through audited planning, validation, and explicit confirmation.
---

# HD2 Mod Safe Deployment

Use only to apply an understood Profile to a stopped game. Only `deploy apply` writes GameData; this Skill is the narrow workflow for invoking it directly.

```powershell
hd2mod deploy plan [profile-or-active] --manager-root "<manager>" --game-data "<data>" --output deployment-plan.json --json
hd2mod deploy validate --plan deployment-plan.json --manager-root "<manager>" --game-data "<data>" --json
hd2mod deploy apply --plan deployment-plan.json --manager-root "<manager>" --game-data "<data>" --confirm --json

hd2mod deploy deactivate plan --manager-root "<manager>" --game-data "<data>" --output deactivation-plan.json --json
hd2mod deploy deactivate validate --plan deactivation-plan.json --manager-root "<manager>" --game-data "<data>" --json
hd2mod deploy deactivate apply --plan deactivation-plan.json --manager-root "<manager>" --game-data "<data>" --confirm --json
```

Before apply, review `patches`, `lua`, and `externalFiles` in the plan. `lua.inputCount >= 2` means one generated Lua Patch will be materialized; Lua is syntax-compiled only and is never run locally. `externalFiles` are manager-owned copies into the Game `bin` directory and must contain only expected source/target mappings. Plan and validation are read-only. Apply rechecks the plan under library and GameData leases, requires the manager and game to be closed, and refuses unknown or modified Patch ownership. Treat any blocking diagnostic as a stop condition.
