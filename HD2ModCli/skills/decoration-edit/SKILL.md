---
name: hd2mod-decoration-edit
description: Safely edit an imported PatchSnapshot decoration's source Units and attachment policy, then rebuild every currently enabled host.
---

# HD2 Mod Decoration Edit

Use this only for an already imported Decoration Mod whose `decoration.json` uses `PatchSnapshot`. It does not deploy GameData.

```powershell
hd2mod decoration edit export <decoration-id> --manager-root "<manager>" --library "<library>" --game-data "<data>" --output decoration-edit-spec.json --json
# Edit only selections and attachment in decoration-edit-spec.json.
hd2mod decoration edit inspect decoration-edit-spec.json --manager-root "<manager>" --library "<library>" --game-data "<data>" --json
hd2mod decoration edit plan decoration-edit-spec.json --manager-root "<manager>" --library "<library>" --game-data "<data>" --output decoration-edit-plan.json --json
hd2mod decoration edit validate --plan decoration-edit-plan.json --manager-root "<manager>" --library "<library>" --game-data "<data>" --json
hd2mod decoration edit apply --plan decoration-edit-plan.json --manager-root "<manager>" --library "<library>" --game-data "<data>" --confirm --json
```

`export` generates informational candidates but `plan` and `validate` always re-read the source snapshot. Do not alter the plan document itself.

An added target host is only declared; it is not automatically enabled. If a currently enabled host is removed from `attachment.targetHostNodeIds`, apply rebuilds it without this decoration and removes that one stale activation. If a write or rebuild fails, the CLI restores the original `decoration.json`, activation state, and completed host Overwrites. It never touches GameData or an unmanaged Overwrite.
