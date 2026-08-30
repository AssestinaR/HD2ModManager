---
name: hd2mod-mod-metadata
description: Rename one Mod or update its notes without changing relationships, Profiles, deployment, or Patch bytes.
---

# HD2 Mod Metadata

Inspect the explicit target, then perform one confirmed metadata change.

```powershell
hd2mod mod show <mod-id> --manager-root "<manager>" --json
hd2mod mod rename <mod-id> "<new-name>" --manager-root "<manager>" --confirm --json
hd2mod mod notes set <mod-id> "<notes>" --manager-root "<manager>" --confirm --json
hd2mod mod notes clear <mod-id> --manager-root "<manager>" --confirm --json
```

The desktop manager must be closed. These commands take the Mod-library write lease and do not deploy or alter Patch bytes.
