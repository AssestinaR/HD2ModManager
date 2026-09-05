---
name: hd2mod-option-management
description: Create logical containers and safely manage multi-parent Option ownership, activation, and ordering.
---

# HD2 Mod Option Management

Inspect first. A Mod may be an Option under more than one explicit host. Each host keeps an independent enable/order state, while deployment deduplicates the physical Option payload. Never infer a host.

```powershell
hd2mod option show <option-id> --library "<library>" --json
hd2mod option enable <option-id> --host <host-id> --manager-root "<manager>" --confirm --json
hd2mod option disable <option-id> --host <host-id> --manager-root "<manager>" --confirm --json
hd2mod option enable-all <option-id> --manager-root "<manager>" --confirm --json
hd2mod option disable-all <option-id> --manager-root "<manager>" --confirm --json
hd2mod option move <option-id> [...] --host <host-id> --position <1-based-slot> --manager-root "<manager>" --confirm --json
hd2mod mod create "<logical-container-name>" --manager-root "<manager>" --confirm --json
hd2mod option parent add <mod-id> [...] --host <host-id> --manager-root "<manager>" --confirm --json
hd2mod option parent remove <option-id> [...] --host <host-id> --manager-root "<manager>" --confirm --json
```

`mod create` writes a durable empty library directory with a logical-container marker. It can be placed in a Profile as a structural anchor; it does not create a fake Patch deployment entry.

`option parent add` converts a normal Mod to an Option when needed and removes it from every Profile. `mod convert-to-option <mod-id> --host <host-id>` is a convenient alias. `option parent remove` only removes the requested parent relation; a shared Option remains attached to its other parents. Removing its last parent restores Standard kind but never restores old Profile placement.

`option insert` is an exact alias of `option move`; it does not attach an Option to a new host. Position 1 precedes the first direct Option; the end position is direct-option count plus one. No command deploys automatically.
