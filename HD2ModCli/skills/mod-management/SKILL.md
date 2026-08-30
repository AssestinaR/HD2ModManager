---
name: hd2mod-mod-management
description: Safely rename or annotate a Mod, and manage direct Option activation and order for one explicit host.
---

# HD2 Mod Management

Use only after inspecting the current library. These commands change library metadata or host-scoped Option state; they never deploy or change Patch bytes.

```powershell
hd2mod mod show <mod-id> --manager-root "<manager directory>" --json
hd2mod mod rename <mod-id> "<new name>" --manager-root "<manager directory>" --confirm --json
hd2mod mod notes set <mod-id> "<text>" --manager-root "<manager directory>" --confirm --json
hd2mod mod notes clear <mod-id> --manager-root "<manager directory>" --confirm --json

hd2mod option enable <option-id> --host <host-id> --manager-root "<manager directory>" --confirm --json
hd2mod option disable <option-id> --host <host-id> --manager-root "<manager directory>" --confirm --json
hd2mod option move <option-id> [<option-id>...] --host <host-id> --position <1-based insertion slot> --manager-root "<manager directory>" --confirm --json
```

Always read the returned `directOptions` before and after an Option operation. `--host` is mandatory: shared Options must never be changed by guessing a host. `--position 1` is before the first direct Option; the end slot is direct-option count + 1. `option insert` is an alias of `option move`, not a way to attach an Option to a new host.

The desktop manager must be closed. CLI takes an OS write lease, reads the library again while holding it, and rejects missing/corrupt library state. Do not use this Skill for delete, Profile, deployment, decoration, or Patch reconstruction work; those capabilities are not yet available.
