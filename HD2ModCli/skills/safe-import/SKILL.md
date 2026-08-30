---
name: hd2mod-safe-import
description: Import one reviewed local folder, archive, or file through a fresh plan-validation-confirmation workflow.
---

# HD2 Mod Safe Import

There is no `--dry-run`. Use the actual no-write workflow below; read validation completely and stop unless `ok` is `true`.

```powershell
hd2mod import inspect "<source>" --json
hd2mod import plan "<source>" --output import.plan.json
hd2mod import validate --plan import.plan.json --manager-root "<manager>" --json
hd2mod import apply --plan import.plan.json --manager-root "<manager>" --confirm --json
```

Apply only after explicit human approval of that exact source and target. It writes the Mod library only: it does not change Profiles, deploy to GameData, edit Patch bytes, or blindly retry a partial post-commit result.
