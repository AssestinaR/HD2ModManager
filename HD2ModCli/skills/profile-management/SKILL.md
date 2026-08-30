---
name: hd2mod-profile-management
description: Create, select, rename, delete, and order Profiles without deploying to GameData.
---

# HD2 Mod Profile Management

Profile writes are library-local. Inspect before and after each mutation; all writes require `--manager-root "<manager>" --confirm --json`.

```powershell
hd2mod profile list --library "<library>" --json
hd2mod profile show [profile] --library "<library>" --json
hd2mod profile create "<name>" [--set-active] --manager-root "<manager>" --confirm --json
hd2mod profile add <profile> <mod-id> [...] --manager-root "<manager>" --confirm --json
hd2mod profile remove <profile> <mod-id> [...] --manager-root "<manager>" --confirm --json
hd2mod profile insert <profile> <mod-id> [...] --position <1-based-slot> --manager-root "<manager>" --confirm --json
hd2mod profile move <profile> <mod-id> [...] --position <1-based-slot> --manager-root "<manager>" --confirm --json
hd2mod profile reorder <profile> <every-mod-id-in-final-order> [...] --manager-root "<manager>" --confirm --json
```

Also available: `profile rename`, `profile delete`, `profile set-active`, and `profile clear-active`, with the same confirmation boundary. No Profile command deploys or writes Patch bytes.
