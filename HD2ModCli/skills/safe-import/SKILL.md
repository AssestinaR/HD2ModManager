---
name: hd2mod-safe-import
description: Import one reviewed local folder, archive, or file through a fresh plan-validation-confirmation workflow.
---

# HD2 Mod Safe Import

Use the no-write inspection and plan workflow; there is no `--dry-run`.

```powershell
hd2mod import inspect "<source>" --json
hd2mod import plan "<source>" --output import-plan.json --json
# Optional: freeze one existing Profile by name, GUID prefix, or active.
hd2mod import plan "<source>" --add-to-profile active --manager-root "<manager>" --output import-plan.json --json
hd2mod import validate --plan import-plan.json --manager-root "<manager>" --json
hd2mod import apply --plan import-plan.json --manager-root "<manager>" --confirm --json
```

Read the complete validation JSON and stop unless `ok` is `true`. `--add-to-profile` is default-off; it freezes Profile ID and revision in the plan, then apply adds only newly imported deployable Standard/ReShade entries if the Profile is unchanged. Apply never deploys GameData, edits Patch bytes, or retries a partial post-commit result automatically.
