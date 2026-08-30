---
name: hd2mod-profile-management
description: Create, select, rename, delete, and order HD2 Mod Manager Profiles without deploying to GameData.
---

# HD2 Mod Profile Management

Use explicit Profile and Mod selectors. Inspect the Profile before and after a mutation.

```powershell
hd2mod profile create "<name>" --set-active --manager-root "<manager>" --confirm --json
hd2mod profile add <profile> <mod-id> [...] --manager-root "<manager>" --confirm --json
hd2mod profile insert <profile> <mod-id> [...] --position <1-based slot> --manager-root "<manager>" --confirm --json
hd2mod profile move <profile> <mod-id> [...] --position <1-based slot> --manager-root "<manager>" --confirm --json
hd2mod profile reorder <profile> <all profile mod ids in final order> --manager-root "<manager>" --confirm --json
hd2mod profile show <profile> --library "<library>" --json
```

Profiles accept deployable Standard Mods and ReShade Mods only. Every mutation is library-local: it never changes GameData or starts deployment.
