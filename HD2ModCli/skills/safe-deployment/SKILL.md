---
name: hd2mod-safe-deployment
description: Safely deploy or deactivate a Profile through audited plan, validation, and explicit confirmation.
---

# HD2 Mod Safe Deployment

Use this only for applying an already-understood Profile to a stopped game.

```powershell
hd2mod deploy plan [profile-or-active] --manager-root "<manager>" --game-data "<game-data>" --output .\deployment-plan.json --json
hd2mod deploy validate --plan .\deployment-plan.json --manager-root "<manager>" --game-data "<game-data>" --json
hd2mod deploy apply --plan .\deployment-plan.json --manager-root "<manager>" --game-data "<game-data>" --confirm --json
```

Never invent `--force`. `apply` rechecks the plan under library and GameData leases, rejects a running Manager or game, and refuses any untracked/modified Patch. Lua is compiled for syntax only; Mod Lua is not executed locally.

To remove only the currently tracked deployment without changing the active Profile:

```powershell
hd2mod deploy deactivate plan --manager-root "<manager>" --game-data "<game-data>" --output .\deactivation-plan.json --json
hd2mod deploy deactivate validate --plan .\deactivation-plan.json --manager-root "<manager>" --game-data "<game-data>" --json
hd2mod deploy deactivate apply --plan .\deactivation-plan.json --manager-root "<manager>" --game-data "<game-data>" --confirm --json
```

Treat any blocking diagnostic as a stop condition. Do not delete or rename `activation-state.json`, GameData Patch files, or the generated plan to bypass it.
