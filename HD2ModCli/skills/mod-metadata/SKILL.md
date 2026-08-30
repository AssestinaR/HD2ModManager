---
name: hd2mod-mod-metadata
description: Safely rename a Mod or set and clear its notes without changing Profiles, deployment, or Patch bytes.
---

# HD2 Mod Metadata

Inspect the target first, then mutate one explicit Mod only.

```powershell
hd2mod mod show <mod-id> --manager-root "<manager>" --json
hd2mod mod rename <mod-id> "<new name>" --manager-root "<manager>" --confirm --json
hd2mod mod notes set <mod-id> "<notes>" --manager-root "<manager>" --confirm --json
hd2mod mod notes clear <mod-id> --manager-root "<manager>" --confirm --json
```

The desktop manager must be closed. A mutation takes the library write lease and never deploys or edits Patch bytes.
