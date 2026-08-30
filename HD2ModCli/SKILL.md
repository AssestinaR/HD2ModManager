---
name: hd2mod-cli-router
description: Route an Agent to the smallest HD2 Mod Manager CLI capability Skill needed for inspection, import, Mod metadata, Options, safe deletion, or Profile management.
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
- `mod-metadata` — rename or annotate one Mod.
- `option-management` — manage direct Option state and order for one explicit host.
- `safe-library-deletion` — preview and safely delete Mod nodes with relationship-aware cleanup.
- `profile-management` — create and organize Profiles without deploying them.

`mod-management` remains a compatibility aggregate only. This router is not a workflow; use `hd2mod capabilities --json` as the command authority and do not invent absent write commands.
