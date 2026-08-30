---
name: hd2mod-option-management
description: Inspect, enable, disable, and order direct Options for one explicit host or all strictly validated direct hosts.
---

# HD2 Mod Option Management

Inspect the host first. Single-host mutations require `--host`; an Option may be shared, so never infer a host. `enable-all` and `disable-all` are permitted only after the CLI proves every declared host is present, eligible, and directly related; otherwise the whole request is rejected without partial writes.

```powershell
hd2mod mod show <host-id> --manager-root "<manager>" --json
hd2mod option show <option-id> --library "<library>" --json
hd2mod option enable <option-id> --host <host-id> --manager-root "<manager>" --confirm --json
hd2mod option disable <option-id> --host <host-id> --manager-root "<manager>" --confirm --json
hd2mod option enable-all <option-id> --manager-root "<manager>" --confirm --json
hd2mod option disable-all <option-id> --manager-root "<manager>" --confirm --json
hd2mod option move <option-id> [...] --host <host-id> --position <1-based-slot> --manager-root "<manager>" --confirm --json
```

`option insert` is an alias for `option move`; it never attaches an Option to a new host. `--position 1` is before the first direct Option; the end slot is direct-option count + 1. No command deploys automatically.
