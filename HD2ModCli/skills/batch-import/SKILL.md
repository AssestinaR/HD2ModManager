---
name: hd2mod-batch-import
description: Import a reviewed collection of local folders, archives, or files sequentially with per-source validation and an explicit partial-result boundary.
---

# HD2 Mod Batch Import

Inspect every source first, export one reviewed plan, then validate and apply it.

```powershell
hd2mod import batch inspect "<source-a>" "<source-b>" --json
hd2mod import batch plan "<source-a>" "<source-b>" --manager-root "<manager>" --output batch-import-plan.json --json
# Optional: add all imported deployable nodes to one unchanged Profile after every source succeeds.
hd2mod import batch plan "<source-a>" "<source-b>" --add-to-profile active --manager-root "<manager>" --output batch-import-plan.json --json
hd2mod import batch validate --plan batch-import-plan.json --manager-root "<manager>" --json
hd2mod import batch apply --plan batch-import-plan.json --manager-root "<manager>" --confirm --json
```

The plan freezes every source independently and execution is serial under one library lease. It does not promise rollback: if a later source fails, earlier completed sources remain imported and the JSON result identifies them. Apply never deploys GameData.
