---
name: hd2mod-profile-management
description: Create, select, rename, delete, and order Profiles without deploying to GameData.
---

# HD2 Mod Profile Management

Read a Profile before and after changing it. All writes require `--manager-root "<manager>" --confirm --json`.

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

Also available: `profile rename <profile> <new-name>`, `profile delete <profile>`, `profile set-active <profile>`, and `profile clear-active`, each with `--manager-root "<manager>" --confirm --json`. Profile changes are library-local and never deploy or write Patch bytes.
