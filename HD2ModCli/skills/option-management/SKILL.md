---
name: hd2mod-option-management
description: Inspect, enable, disable, and order direct Options for one explicit host or all strictly validated direct hosts.
---

# HD2 Mod Option Management

Inspect first. Single-host mutation requires `--host`; an Option can be shared, so never infer its host. `enable-all` and `disable-all` run only when every declared direct host is valid, otherwise they reject without partial writes.

```powershell
hd2mod option show <option-id> --library "<library>" --json
hd2mod option enable <option-id> --host <host-id> --manager-root "<manager>" --confirm --json
hd2mod option disable <option-id> --host <host-id> --manager-root "<manager>" --confirm --json
hd2mod option enable-all <option-id> --manager-root "<manager>" --confirm --json
hd2mod option disable-all <option-id> --manager-root "<manager>" --confirm --json
hd2mod option move <option-id> [...] --host <host-id> --position <1-based-slot> --manager-root "<manager>" --confirm --json
```

`option insert` is an exact alias of `option move`; it does not attach an Option to a new host. Position 1 precedes the first direct Option; the end position is direct-option count plus one. No command deploys automatically.
