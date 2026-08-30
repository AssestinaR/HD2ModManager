---
name: hd2mod-safe-deployment
description: Safely deploy or deactivate a Profile through audited planning, validation, and explicit confirmation.
---

# HD2 Mod Safe Deployment

Use only to apply an understood Profile to a stopped game. This is the only CLI Skill that writes GameData.

```powershell
hd2mod deploy plan [profile-or-active] --manager-root "<manager>" --game-data "<data>" --output deployment-plan.json --json
hd2mod deploy validate --plan deployment-plan.json --manager-root "<manager>" --game-data "<data>" --json
hd2mod deploy apply --plan deployment-plan.json --manager-root "<manager>" --game-data "<data>" --confirm --json

hd2mod deploy deactivate plan --manager-root "<manager>" --game-data "<data>" --output deactivation-plan.json --json
hd2mod deploy deactivate validate --plan deactivation-plan.json --manager-root "<manager>" --game-data "<data>" --json
hd2mod deploy deactivate apply --plan deactivation-plan.json --manager-root "<manager>" --game-data "<data>" --confirm --json
```

Plan and validation are read-only. Apply rechecks the plan under library and GameData leases, requires the manager and game to be closed, and refuses unknown or modified Patch ownership. Lua is syntax-compiled only and is never run locally. Treat any blocking diagnostic as a stop condition.
