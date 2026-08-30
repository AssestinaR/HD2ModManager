---
name: hd2mod-cli-router
description: Route an Agent to the smallest HD2 Mod Manager CLI capability Skill needed for local library inspection or safe Mod import.
---

# HD2 Mod CLI Router

Do not load every workflow by default. Discover the shipped capability Skills:

```powershell
hd2mod agent skills list --json
```

Export only the relevant Skill:

```powershell
hd2mod agent skills export <skill-name> --output .\.agents\skills
```

Current routing:

- `library-inspection` — inspect Mod library, Option tree, Profile and effective deployment order without mutation.
- `safe-import` — import a reviewed source through `plan → validate → apply --confirm`.

These are the only capability Skills currently shipped. This router is compatibility-only; it is not a third workflow. Use `hd2mod capabilities --json` as the command authority and do not invent absent write commands.
