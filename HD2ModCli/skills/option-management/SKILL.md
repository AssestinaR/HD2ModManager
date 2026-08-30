---
name: hd2mod-option-management
description: Enable, disable, and order direct Options for one explicit Mod or Option host.
---

# HD2 Mod Option Management

Inspect the host first. Every mutation requires `--host`; an Option may be shared, so never infer or broaden its host set.

```powershell
hd2mod mod show <host-id> --manager-root "<manager>" --json
hd2mod option enable <option-id> --host <host-id> --manager-root "<manager>" --confirm --json
hd2mod option disable <option-id> --host <host-id> --manager-root "<manager>" --confirm --json
hd2mod option move <option-id> [...] --host <host-id> --position <1-based-slot> --manager-root "<manager>" --confirm --json
```

`option insert` is an alias for `option move`; it never attaches an Option to a new host. `--position 1` is before the first direct Option; the end slot is direct-option count + 1. No command deploys automatically.
