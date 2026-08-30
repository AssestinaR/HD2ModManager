---
name: hd2mod-option-management
description: Safely enable, disable, and order direct Options for one explicit parent Mod or Option host.
---

# HD2 Mod Option Management

Read the host first. Option activation and order are host-scoped; always provide `--host`.

```powershell
hd2mod mod show <host-id> --manager-root "<manager>" --json
hd2mod option enable <option-id> --host <host-id> --manager-root "<manager>" --confirm --json
hd2mod option disable <option-id> --host <host-id> --manager-root "<manager>" --confirm --json
hd2mod option move <option-id> [...] --host <host-id> --position <1-based slot> --manager-root "<manager>" --confirm --json
```

`option insert` is an alias for `option move`; it never attaches an Option to a new host. No command deploys automatically.
